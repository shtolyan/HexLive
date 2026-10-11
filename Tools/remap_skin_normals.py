"""Remap existing Unity skin normal textures onto the prepared body UV atlases.

First run Unity -executeMethod HexLive.UnityPresentation.Wearing.Editor.PeopleSkinNormalExport.Export.
Then run Blender --background --disable-autoexec --python-exit-code 1
  --python Tools/remap_skin_normals.py -- --source-dir Build/PeopleSkinNormalSource --output Build/PeopleSkinNormals

Preserves the authored texture perturbation, not high/low-poly shape differences.
The output requires Unity material/painter and visual checks before publication.
"""
import argparse, bpy, hashlib, json, pathlib, sys, numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ROOT=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source-dir',type=pathlib.Path,default=ROOT/'Build/PeopleSkinNormalSource')
parser.add_argument('--output',type=pathlib.Path,default=ROOT/'Build/PeopleSkinNormals')
args=parser.parse_args(sys.argv[sys.argv.index('--')+1:] if '--' in sys.argv else [])
SOURCE=args.source_dir.resolve(strict=True)
OUT=args.output.resolve();OUT.mkdir(parents=True,exist_ok=True)
ROWS={r['actor']:r for r in json.loads((SOURCE/'source.json').read_text())}
SIZE=1024

def unit(v):
    return v/np.maximum(np.linalg.norm(v,axis=-1,keepdims=True),1e-10)

def sample(image,uv):
    h,w,_=image.shape
    xy=uv*np.array([w,h])-.5; ij=np.floor(xy).astype(int); f=xy-ij
    x,y=ij[:,0]%w,ij[:,1]%h; xx,yy=(x+1)%w,(y+1)%h
    return ((image[y,x,:3]*(1-f[:,0,None])+image[y,xx,:3]*f[:,0,None])*(1-f[:,1,None])+
            (image[yy,x,:3]*(1-f[:,0,None])+image[yy,xx,:3]*f[:,0,None])*f[:,1,None])

def filtered(pyramid,uv,lod):
    lod=np.clip(lod,0,len(pyramid)-1);lo=np.floor(lod).astype(int);fraction=lod-lo
    value=np.zeros((len(uv),3))
    for level in np.unique(lo):
        use=lo==level
        value[use]=sample(pyramid[level],uv[use])*(1-fraction[use,None])+sample(pyramid[min(level+1,len(pyramid)-1)],uv[use])*fraction[use,None]
    return value

reports=[]
for actor,fbx in [('Marta','Martanaked/Martanaked.fbx'),('Kshishtof','Kshishtof/Kshishtof.fbx')]:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    old=set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(ROOT/'Assets/ImportedActors/Daz3D'/fbx),use_anim=False,use_image_search=False)
    source=max((o for o in set(bpy.data.objects)-old if o.type=='MESH'),key=lambda o:len(o.data.vertices))
    mesh=source.data; mesh.calc_loop_triangles(); mesh.calc_tangents(uvmap=mesh.uv_layers.active.name)
    sv=np.asarray([v.co[:] for v in mesh.vertices]); triangles=np.asarray([t.vertices[:] for t in mesh.loop_triangles])
    loops=np.asarray([t.loops[:] for t in mesh.loop_triangles]); points=sv[triangles]
    sn=np.asarray([n.vector[:] for n in mesh.corner_normals])[loops]
    st=np.asarray([l.tangent[:] for l in mesh.loops])[loops]
    sw=np.asarray([l.bitangent_sign for l in mesh.loops])[loops]
    su=np.asarray([u.uv[:] for u in mesh.uv_layers.active.data])[loops]
    slots=np.asarray([mesh.polygons[t.polygon_index].material_index for t in mesh.loop_triangles])
    tree=BVHTree.FromPolygons(sv.tolist(),triangles.tolist(),all_triangles=True)
    source_rows={r['slot']:r for r in ROWS[actor]['materials']}
    slot_rows=[]; images={}
    for slot in source.material_slots:
        name=slot.material.name.split('.')[0]; row=source_rows[name]; slot_rows.append(row)
        if row.get('enabled') and row.get('image') and row['image'] not in images:
            image=bpy.data.images.load(str(SOURCE/row['image']),check_existing=True); image.colorspace_settings.name='Non-Color'
            buf=np.empty(image.size[0]*image.size[1]*4,dtype=np.float32);image.pixels.foreach_get(buf)
            images[row['image']]=buf.reshape(image.size[1],image.size[0],4)
    target=json.loads((SOURCE/(actor+'-mesh.json')).read_text())
    pyramids={}
    for name,image in images.items():
        levels=[image]
        while min(levels[-1].shape[:2])>1:
            p=levels[-1];h,w=p.shape[:2]
            levels.append(p.reshape(h//2,2,w//2,2,4).mean(axis=(1,3)))
        pyramids[name]=levels
    tv=np.asarray(target['vertices']); tn=np.asarray(target['normals']); tt=np.asarray(target['tangents']); tu=np.asarray(target['uv'])
    ti=np.asarray(target['triangles']).reshape(-1,3)
    derivatives=np.zeros((len(ti),3,2))
    pixel_batches=[]; weight_batches=[]; triangle_batches=[]
    for triangle,indices in enumerate(ti):
        uv=tu[indices]*SIZE
        lo=np.maximum(np.floor(uv.min(axis=0)-.5).astype(int),0); hi=np.minimum(np.ceil(uv.max(axis=0)-.5).astype(int),SIZE-1)
        if np.any(hi<lo):continue
        matrix=np.stack([uv[1]-uv[0],uv[2]-uv[0]],axis=1)
        if abs(np.linalg.det(matrix))<1e-8:continue
        derivatives[triangle]=(tv[indices[1:]]-tv[indices[0]]).T@np.linalg.inv(matrix)
        derivatives[triangle,0,:]*=-1
        xx,yy=np.meshgrid(np.arange(lo[0],hi[0]+1),np.arange(lo[1],hi[1]+1)); xy=np.stack([xx.ravel(),yy.ravel()],axis=1)
        bc=(xy+.5-uv[0])@np.linalg.inv(matrix).T
        weights=np.column_stack([1-bc.sum(axis=1),bc]); valid=np.all(weights>=-1e-7,axis=1)
        if not valid.any():continue
        pixel_batches.append(xy[valid,1]*SIZE+xy[valid,0]);weight_batches.append(weights[valid]);triangle_batches.append(np.full(valid.sum(),triangle,dtype=int))
    pixels=np.concatenate(pixel_batches); weights=np.concatenate(weight_batches); target_tri=np.concatenate(triangle_batches)
    result=np.empty((SIZE*SIZE,4),dtype=np.float32);result[:]=(.5,.5,1,1)
    distances=[]; bad_basis=0; negative_z=0; mip_levels=[]; max_angle_error=0.
    for start in range(0,len(pixels),20000):
        end=min(start+20000,len(pixels)); wi=weights[start:end]; idx=ti[target_tri[start:end]]
        pos=(tv[idx]*wi[:,:,None]).sum(axis=1); pos[:,0]*=-1
        hits=[tree.find_nearest(Vector(p)) for p in pos]
        closest=np.asarray([h[0][:] for h in hits]); si=np.asarray([h[2] for h in hits]);distances.extend(h[3] for h in hits)
        a=points[si,0]; e0=points[si,1]-a; e1=points[si,2]-a; v=closest-a
        d00=np.sum(e0*e0,axis=1);d01=np.sum(e0*e1,axis=1);d11=np.sum(e1*e1,axis=1)
        d20=np.sum(v*e0,axis=1);d21=np.sum(v*e1,axis=1);den=np.maximum(d00*d11-d01*d01,1e-15)
        u=(d11*d20-d01*d21)/den;w=(d00*d21-d01*d20)/den; bw=np.column_stack([1-u-w,u,w])
        source_uv=(su[si]*bw[:,:,None]).sum(axis=1)
        dp=derivatives[target_tri[start:end]]
        d20=np.einsum('nik,ni->nk',dp,e0);d21=np.einsum('nik,ni->nk',dp,e1)
        du=(d11[:,None]*d20-d01[:,None]*d21)/den[:,None]
        dv=(d00[:,None]*d21-d01[:,None]*d20)/den[:,None]
        uv_derivatives=(su[si,1]-su[si,0])[:,:,None]*du[:,None,:]+(su[si,2]-su[si,0])[:,:,None]*dv[:,None,:]
        normal_ts=np.zeros((len(si),3));normal_ts[:,2]=1
        for slot,row in enumerate(slot_rows):
            mask=slots[si]==slot
            if not mask.any() or not row.get('enabled') or not row.get('image'):continue
            uv=source_uv[mask]*row['uvScale']+row['uvOffset']
            image=images[row['image']];h,w=image.shape[:2]
            footprint=uv_derivatives[mask]*np.array(row['uvScale'])[None,:,None]*np.array([w,h])[None,:,None]
            lod=np.log2(np.maximum(np.linalg.norm(footprint,axis=1).max(axis=1),1))
            mip_levels.extend(lod.tolist())
            normal_ts[mask]=unit(filtered(pyramids[row['image']],uv,lod)*2-1)
        ns=unit((sn[si]*bw[:,:,None]).sum(axis=1));ts=unit((st[si]*bw[:,:,None]).sum(axis=1))
        bs=np.cross(ns,ts)*np.sign((sw[si]*bw).sum(axis=1))[:,None]
        world=unit(ts*normal_ts[:,0,None]+bs*normal_ts[:,1,None]+ns*normal_ts[:,2,None]);world[:,0]*=-1
        nt=(tn[idx]*wi[:,:,None]).sum(axis=1); tangent=(tt[idx]*wi[:,:,None]).sum(axis=1)
        # Transfer the authored texture perturbation, not the difference between
        # high/low-poly surface shapes. Parallel transport maps a flat source
        # normal exactly to the destination surface normal while retaining the
        # source bump's angle and UV orientation.
        source_normal=ns.copy();source_normal[:,0]*=-1
        source_angle=np.sum(world*source_normal,axis=1)
        dest_normal=unit(nt);axis=np.cross(source_normal,dest_normal)
        cosine=np.sum(source_normal*dest_normal,axis=1)
        opposite=cosine<-.99999
        cross=np.cross(axis,world)
        transported=world+cross+np.cross(axis,cross)/np.maximum(1+cosine[:,None],1e-8)
        if opposite.any():
            n=source_normal[opposite];a=np.cross(n,np.array([1.,0.,0.]))
            small=np.linalg.norm(a,axis=1)<.01;a[small]=np.cross(n[small],np.array([0.,1.,0.]))
            a=unit(a);w=world[opposite];transported[opposite]=2*a*np.sum(a*w,axis=1)[:,None]-w
        world=unit(transported)
        max_angle_error=max(max_angle_error,float(np.max(np.abs(np.sum(world*dest_normal,axis=1)-source_angle))))
        bt=np.cross(nt,tangent[:,:3])*tangent[:,3,None]
        basis=np.stack([tangent[:,:3],bt,nt],axis=-1)
        valid=np.abs(np.linalg.det(basis))>1e-6;bad_basis+=int((~valid).sum())
        local=np.zeros_like(world);local[:,2]=1
        local[valid]=unit(np.linalg.solve(basis[valid],world[valid,:,None])[:,:,0])
        negative_z+=int((local[:,2]<0).sum())
        result[pixels[start:end],:3]=local*.5+.5
        print(actor,'projected',end,'/',len(pixels),flush=True)
    # Dilate UV borders for filtering, without changing covered pixels.
    coverage=np.zeros(SIZE*SIZE,dtype=bool);coverage[pixels]=True;coverage=coverage.reshape(SIZE,SIZE);result=result.reshape(SIZE,SIZE,4)
    for step in range(4):
        old=coverage.copy(); prev=result.copy()
        for dy,dx in [(0,1),(0,-1),(1,0),(-1,0)]:
            neighbors=np.roll(old,(dy,dx),(0,1));take=~coverage&neighbors
            result[take]=np.roll(prev,(dy,dx),(0,1))[take];coverage[take]=True
    if np.percentile(distances,95)>1 or max(distances)>2:
        raise RuntimeError(f'{actor}: source/target alignment changed; do not export')
    if negative_z or bad_basis>len(pixels)*.0001 or max_angle_error>.001:
        raise RuntimeError(f'{actor}: invalid normal transfer: negative Z={negative_z}, singular basis={bad_basis}, angle error={max_angle_error}')
    image=bpy.data.images.new(actor+'_closest_normal',width=SIZE,height=SIZE,alpha=False)
    image.colorspace_settings.name='Non-Color';image.pixels.foreach_set(result.ravel())
    image.filepath_raw=str(OUT/(actor+'_skin_n.png'));image.file_format='PNG';image.save()
    reports.append({'actor':actor,'pixels':len(pixels),'singularBasisPixels':bad_basis,'negativeTangentZPixels':negative_z,'maxPerturbationCosineError':max_angle_error,
        'distanceCmP95':float(np.percentile(distances,95)),'distanceCmMax':max(distances),'meanSourceMip':float(np.mean(mip_levels)),'sourceManifestSha256':hashlib.sha256((SOURCE/'source.json').read_bytes()).hexdigest(),'targetMeshSha256':hashlib.sha256((SOURCE/(actor+'-mesh.json')).read_bytes()).hexdigest(),'sourceFbxSha256':hashlib.sha256((ROOT/'Assets/ImportedActors/Daz3D'/fbx).read_bytes()).hexdigest(),'status':'remapped; Unity verification required'})
    (OUT/'projection.json').write_text(json.dumps(reports,indent=2))
    print(json.dumps(reports[-1]),flush=True)
