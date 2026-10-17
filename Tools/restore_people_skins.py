"""Bake legacy authored skin colours onto the prepared People UV layout.

Run in an isolated Blender process with --factory-startup --python this-file.
Uses the same source/target registration as remap_skin_normals.py. No generated
complexions, mesh edits, or skin-tone multipliers. Outputs staging files only.
"""
import hashlib
import json
import pathlib
import re
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUT = ROOT / 'Build/RestoredPeopleSkins'
OUT.mkdir(parents=True, exist_ok=True)
SIZE = 1024


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    for marker in ('SUCCESS', 'ERROR.txt'):
        (OUT / marker).unlink(missing_ok=True)
    # Resolve serialized material references, not guessed texture filenames.
    guids = {}
    index = OUT / 'source-guids.txt'
    if index.exists():
        for line in index.read_text(encoding='utf-8-sig').splitlines():
            path, guid = line.rsplit(':guid: ', 1)
            guids[guid] = ROOT / path[:-5]
    else:
        for path in (ROOT / 'Assets/ImportedActors').rglob('*.meta'):
            match = re.search(r'^guid: (\w+)', path.read_text(errors='replace'), re.M)
            if match:
                guids[match[1]] = pathlib.Path(str(path)[:-5])
    print('Indexed authored sources', len(guids), flush=True)
    report = []
    for actor, fbx, donors in [
        ('Marta', 'Martanaked/Martanaked.fbx', ['Marta', 'Molly', 'Jana', 'Jolly']),
        ('Kshishtof', 'Kshishtof/Kshishtof.fbx', ['Kshishtof']),
    ]:
        for obj in list(bpy.data.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        source_path = ROOT / 'Assets/ImportedActors/Daz3D' / fbx
        bpy.ops.import_scene.fbx(filepath=str(source_path), use_anim=False, use_image_search=False)
        print('Imported source', actor, flush=True)
        source = max((o for o in bpy.data.objects if o.type == 'MESH'), key=lambda o: len(o.data.vertices))
        mesh = source.data
        mesh.calc_loop_triangles()
        sv = np.asarray([v.co[:] for v in mesh.vertices])
        tri = np.asarray([t.vertices[:] for t in mesh.loop_triangles])
        loops = np.asarray([t.loops[:] for t in mesh.loop_triangles])
        su = np.asarray([u.uv[:] for u in mesh.uv_layers.active.data])[loops]
        slots = np.asarray([t.material_index for t in mesh.loop_triangles])
        names = [s.material.name.split('.')[0] for s in source.material_slots]
        tree = BVHTree.FromPolygons(sv.tolist(), tri.tolist(), all_triangles=True)
        target_path = ROOT / 'Build/PeopleSkinNormalSource' / (actor + '-mesh.json')
        target = json.loads(target_path.read_text())
        tv, tu = np.asarray(target['vertices']), np.asarray(target['uv'])
        ti = np.asarray(target['triangles']).reshape(-1, 3)
        pixels, positions = [], []
        for ids in ti:
            uv = tu[ids] * SIZE
            low = np.maximum(np.floor(uv.min(0) - .5).astype(int), 0)
            high = np.minimum(np.ceil(uv.max(0) - .5).astype(int), SIZE - 1)
            matrix = np.stack([uv[1] - uv[0], uv[2] - uv[0]], axis=1)
            if np.any(high < low) or abs(np.linalg.det(matrix)) < 1e-8:
                continue
            xx, yy = np.meshgrid(np.arange(low[0], high[0]+1), np.arange(low[1], high[1]+1))
            xy = np.stack([xx.ravel(), yy.ravel()], axis=1)
            bc = (xy + .5 - uv[0]) @ np.linalg.inv(matrix).T
            weights = np.column_stack([1-bc.sum(1), bc])
            valid = np.all(weights >= -1e-7, axis=1)
            pixels.append(xy[valid, 1]*SIZE + xy[valid, 0])
            positions.append(weights[valid] @ tv[ids])
        pixels, positions = np.concatenate(pixels), np.concatenate(positions)
        positions[:, 0] *= -1  # Unity-to-Blender mesh coordinates.
        hits = [tree.find_nearest(Vector(p)) for p in positions]
        distance = np.asarray([h[3] for h in hits])
        if np.percentile(distance, 95) > 1 or distance.max() > 2:
            raise RuntimeError('Source/target registration failed: ' + actor)
        si = np.asarray([h[2] for h in hits])
        p = sv[tri[si]]
        v = np.asarray([h[0][:] for h in hits]) - p[:, 0]
        e0, e1 = p[:, 1]-p[:, 0], p[:, 2]-p[:, 0]
        dot = lambda a, b: np.sum(a*b, axis=1)
        aa, ab, bb = dot(e0,e0), dot(e0,e1), dot(e1,e1)
        den = np.maximum(aa*bb-ab*ab, 1e-15)
        u, w = (bb*dot(v,e0)-ab*dot(v,e1))/den, (aa*dot(v,e1)-ab*dot(v,e0))/den
        source_uv = (su[si]*np.column_stack([1-u-w,u,w])[:,:,None]).sum(1)
        for donor in donors:
            prefab = ROOT / 'Assets/HexLiveContent/RuntimeSource/Actors' / (donor+'.prefab')
            material_paths = [guids[g] for g in re.findall(r'fileID: 2100000, guid: (\w+)', prefab.read_text()) if g in guids]
            materials = {path.stem: path for path in material_paths}
            if donor in ('Jolly', 'Molly'):
                materials.update({p.stem:p for p in (ROOT/'Assets/ImportedActors/Actors'/donor/'Materials').glob('*.mat')})
            if not materials:
                raise RuntimeError('No authored materials: '+donor)
            result = np.ones((SIZE*SIZE, 4), dtype=np.float32)
            receipts, images = {}, {}
            for slot in np.unique(slots[si]):
                name = names[slot]
                path = materials.get(name)
                if path is None:
                    raise RuntimeError(f'{donor}: missing source slot {name}')
                text = path.read_text()
                match = re.search(r'- _BaseMap:\s+m_Texture: \{fileID: \d+, guid: (\w+)', text)
                if not match:
                    # Teeth/eye parts have their own runtime materials; preserve a white empty slot.
                    if name in ('Cornea','EyeMoisture','Pupils','Eyelashes'):
                        if name in ('Pupils', 'Eyelashes'):
                            result[pixels[slots[si] == slot], :3] = 0
                        continue
                    raise RuntimeError(f'{donor}: no authored base map for {name}')
                texture = guids[match[1]]
                if texture not in images:
                    image = bpy.data.images.load(str(texture), check_existing=True)
                    # Preserve the source sRGB bytes; no lighting or exposure bake.
                    image.colorspace_settings.name = 'Non-Color'
                    buf = np.empty(image.size[0]*image.size[1]*4, dtype=np.float32)
                    image.pixels.foreach_get(buf)
                    images[texture] = buf.reshape(image.size[1], image.size[0], 4)
                image = images[texture]
                mask = slots[si] == slot
                uv = source_uv[mask]
                # Current authored materials use identity transforms; fail on a future change.
                section = text.split('- _BaseMap:',1)[1].split('\n    - ',1)[0]
                if 'm_Scale: {x: 1, y: 1}' not in section or 'm_Offset: {x: 0, y: 0}' not in section:
                    raise RuntimeError('Unsupported source UV transform: '+str(path))
                h, width = image.shape[:2]
                xy = uv*np.array([width,h])-.5
                ij = np.floor(xy).astype(int); f = xy-ij
                x,y = ij[:,0]%width,ij[:,1]%h
                xx,yy = (x+1)%width,(y+1)%h
                result[pixels[mask]] = ((image[y,x]*(1-f[:,0,None])+image[y,xx]*f[:,0,None])*(1-f[:,1,None])+
                                       (image[yy,x]*(1-f[:,0,None])+image[yy,xx]*f[:,0,None])*f[:,1,None])
                receipts[name] = {'material':path.relative_to(ROOT).as_posix(), 'texture':texture.relative_to(ROOT).as_posix(), 'sha256':sha(texture)}
            result = result.reshape(SIZE,SIZE,4)
            known = np.zeros(SIZE*SIZE,bool); known[pixels] = True; known=known.reshape(SIZE,SIZE)
            for _ in range(24):
                old, prev = known.copy(), result.copy()
                for dy,dx in [(0,1),(0,-1),(1,0),(-1,0)]:
                    neighbor=np.roll(old,(dy,dx),(0,1))
                    if dy>0: neighbor[:dy]=False
                    if dy<0: neighbor[dy:]=False
                    if dx>0: neighbor[:,:dx]=False
                    if dx<0: neighbor[:,dx:]=False
                    take=~known & neighbor
                    result[take]=np.roll(prev,(dy,dx),(0,1))[take]; known[take]=True
            image=bpy.data.images.new(actor+'_'+donor,width=SIZE,height=SIZE,alpha=False)
            image.colorspace_settings.name='Non-Color'; image.pixels.foreach_set(result.ravel())
            output=OUT/(actor+'_'+donor+'_BaseColor.png')
            image.filepath_raw=str(output); image.file_format='PNG'; image.save()
            report.append({'body':actor,'skin':donor,'output':output.name,'sha256':sha(output),
                           'targetMeshSha256':sha(target_path),'sourceMeshSha256':sha(source_path),
                           'distanceCmP95':float(np.percentile(distance,95)), 'distanceCmMax':float(distance.max()),
                           'sources':receipts})
            (OUT/'receipt.json').write_text(json.dumps(report,indent=2))
            print('Restored',actor,donor,flush=True)


if __name__ == '__main__':
    try:
        main()
        (OUT/'SUCCESS').write_text('Completed all authored skins.\n')
    except Exception:
        import traceback
        (OUT/'ERROR.txt').write_text(traceback.format_exc())
        raise
    finally:
        bpy.ops.wm.quit_blender()
