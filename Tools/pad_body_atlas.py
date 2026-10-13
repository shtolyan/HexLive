"""Extend body atlas colour beyond UV islands before mip filtering.

Uses the exported Unity mesh UVs. Erode two pixels of boundary contamination,
then propagate neighbouring interior colours through a 24-pixel gutter.
Does not change mesh topology, UVs, normals, or the interior of the atlas.
"""
import argparse, json
from pathlib import Path
import numpy as np
from PIL import Image

def pad(source, mesh, output, gutter=24):
    rgb=np.asarray(Image.open(source).convert('RGB')).copy()
    height,width=rgb.shape[:2]
    data=json.loads(Path(mesh).read_text())
    uv=np.array(data['uv'])*[width,height]
    uv[:,1]=height-uv[:,1]
    coverage=np.zeros((height,width),bool)
    for ids in np.array(data['triangles']).reshape(-1,3):
        p=uv[ids];low=np.maximum(np.floor(p.min(0)-.5).astype(int),0)
        high=np.minimum(np.ceil(p.max(0)-.5).astype(int),[width-1,height-1])
        if np.any(high<low):continue
        matrix=np.stack([p[1]-p[0],p[2]-p[0]],axis=1)
        if abs(np.linalg.det(matrix))<1e-9:continue
        xx,yy=np.meshgrid(np.arange(low[0],high[0]+1),np.arange(low[1],high[1]+1))
        q=np.stack([xx.ravel(),yy.ravel()],axis=1)
        b=(q+.5-p[0])@np.linalg.inv(matrix).T
        inside=(b[:,0]>=-1e-7)&(b[:,1]>=-1e-7)&(b.sum(1)<=1+1e-7)
        coverage[q[inside,1],q[inside,0]]=True
    known=coverage.copy()
    def shifted(a,dy,dx):
        b=np.roll(a,(dy,dx),(0,1))
        if dy>0:b[:dy]=0
        if dy<0:b[dy:]=0
        if dx>0:b[:,:dx]=0
        if dx<0:b[:,dx:]=0
        return b
    neighbors=[(-1,0),(1,0),(0,-1),(0,1)]
    for _ in range(2):
        old=known.copy()
        for dy,dx in neighbors:known &= shifted(old,dy,dx)
    protected=known.copy()
    colours=rgb.astype(np.float32)
    for _ in range(gutter+2):
        total=np.zeros_like(colours);count=np.zeros(known.shape,np.uint8)
        for dy,dx in neighbors:
            valid=shifted(known,dy,dx)
            total+=shifted(colours,dy,dx)*valid[:,:,None];count+=valid
        take=(~known)&(count>0)
        colours[take]=total[take]/count[take,None];known[take]=True
    result=rgb.copy();result[known]=np.rint(colours[known]).clip(0,255).astype(np.uint8)
    assert np.array_equal(result[protected],rgb[protected])
    Path(output).parent.mkdir(parents=True,exist_ok=True)
    Image.fromarray(result).save(output)
    report={'source':str(source),'mesh':str(mesh),'output':str(output),'size':[width,height],
        'coveredPixels':int(coverage.sum()),'unchangedInteriorPixels':int(protected.sum()),
        'changedPixels':int(np.any(result!=rgb,axis=2).sum()),'gutterPixels':gutter}
    Path(str(output)+'.json').write_text(json.dumps(report,indent=2))
    print(json.dumps(report))

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--source',required=True);p.add_argument('--mesh',required=True);p.add_argument('--output',required=True)
    a=p.parse_args();pad(a.source,a.mesh,a.output)
