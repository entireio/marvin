#!/usr/bin/env python3
"""Analytic AO controls with immutable, ray-integrated reference samples.

Generate before native rendering; checker reads saved conditions, not ambient
settings. Passing sanity controls is not visual or performance acceptance.
"""
import argparse,json,os,hashlib
from pathlib import Path
import numpy as np

KINDS=('flat','tilted','grazing','corner','corner-mirror','corner-rotated','thin-occluder','half-coverage','empty')

def planes(kind):
    n=np.array([.3,0,1.] if kind=='tilted' else [1.,0,.2] if kind=='grazing' else [0.,0.,1.]);n/=np.linalg.norm(n)
    shapes=[(n,np.array([0.,0.,-4.]),None)]
    if kind in ('corner','corner-mirror','corner-rotated'):
        normal,point={'corner':([0,1,0],[0,-1.2,0]),'corner-mirror':([0,-1,0],[0,1.2,0]),'corner-rotated':([1,0,0],[-1.2,0,0])}[kind]
        shapes.append((np.array(normal,dtype=float),np.array(point,dtype=float),None))
    if kind=='thin-occluder':shapes.append((np.array([0.,0.,1.]),np.array([0.,0.,-3.]),(.035,.6)))
    return shapes

def intersect(origins,directions,shapes):
    hits=np.full(directions.shape[:-1],np.inf);indices=np.full(hits.shape,-1,dtype=int)
    for i,(normal,point,bounds) in enumerate(shapes):
        denominator=directions@normal
        t=np.divide((point-origins)@normal,denominator,out=np.full(hits.shape,np.inf),where=np.abs(denominator)>1e-9)
        valid=(t>1e-7)&np.isfinite(t)
        if bounds is not None:
            position=origins+directions*np.where(valid,t,0)[...,None]
            valid &= (abs(position[...,0])<=bounds[0])&(abs(position[...,1])<=bounds[1])
        update=valid&(t<hits);hits[update]=t[update];indices[update]=i
    return hits,indices

def reference(points,normals,shapes,falloff):
    count=16384;i=np.arange(count);r=np.sqrt((i+.5)/count);phi=i*np.pi*(3-np.sqrt(5))
    local=np.stack((r*np.cos(phi),r*np.sin(phi),np.sqrt(1-r*r)),axis=-1)
    result=[]
    for point,normal in zip(points,normals):
        helper=[1,0,0] if abs(normal[0])<.9 else [0,1,0]
        tangent=np.cross(normal,helper);tangent/=np.linalg.norm(tangent);bitangent=np.cross(normal,tangent)
        directions=local[:,0,None]*tangent+local[:,1,None]*bitangent+local[:,2,None]*normal
        hit,_=intersect(point+normal*.002,directions,shapes)
        # Smooth ray-distance attenuation is NOT equivalent to weighting the
        # candidate's horizon cosine. Hard-radius controls remove that mismatch.
        result.append(1-((hit<1.6).mean() if falloff==0 else np.clip((1.6-hit)/(1.6*falloff),0,1).mean()))
    return np.array(result)

def generate(root):
    w,h=int(os.environ.get('MARVIN_AO_FIXTURE_WIDTH','256')),int(os.environ.get('MARVIN_AO_FIXTURE_HEIGHT','192'))
    assert w>=64 and h>=64
    falloff=float(os.environ.get('MARVIN_AO_FALLOFF','.5'))
    near,far=.02,250.;f=1/np.tan(np.pi/6)
    projection=np.array([[f/(w/h),0,0,0],[0,f,0,0],[0,0,-(far+near)/(far-near),-2*far*near/(far-near)],[0,0,-1,0]],dtype=np.float32)
    y,x=np.mgrid[:h,:w];rays=np.stack(((2*(x+.5)/w-1)/projection[0,0],(1-2*(y+.5)/h)/projection[1,1],-np.ones((h,w))),axis=-1)
    for kind in KINDS:
        shapes=planes(kind);distance,surfaces=intersect(np.zeros(3),rays,shapes)
        covered=np.isfinite(distance)&(distance>=near)&(distance<far)
        if kind=='empty':covered[:]=False
        points=rays*np.where(covered,distance,1)[...,None]
        normals=np.array([s[0] for s in shapes])[np.maximum(surfaces,0)]
        coverage=covered.astype(float)*(.5 if kind=='half-coverage' else 1)
        encoded=np.concatenate(((normals*.5+.5)*coverage[...,None],coverage[...,None]),axis=-1)
        z=points[:,:,2];ndc=(projection[2,2]*z+projection[2,3])/(-z);depth=np.where(covered,(1-ndc)/2,0)
        directory=root/kind;directory.mkdir(parents=True,exist_ok=True)
        encoded.astype('<f2').tofile(directory/'normals-coverage.rgba16f');depth.astype('<f4').tofile(directory/'depth.depth32f')
        (directory/'preparation.json').write_text(json.dumps({'resolution':[w,h],'projectionColumnMajor':projection.T.reshape(-1).tolist(),'reverseZ':True}))
        # Grid spans both sides of every corner, including its grazing surface.
        yy,xx=np.mgrid[int(h*.12):int(h*.88):max(1,h//16),int(w*.12):int(w*.88):max(1,w//20)]
        valid=covered[yy,xx];yy,xx=yy[valid],xx[valid]
        expected=reference(points[yy,xx],normals[yy,xx],shapes,falloff) if len(xx) else np.array([])
        reference_data={'kind':kind,'resolution':[w,h],'radius':1.6,'falloffFraction':falloff,'originBias':.002,'rayCount':16384,'pixels':np.stack((xx,yy),axis=-1).tolist(),'surfaces':surfaces[yy,xx].tolist(),'visibility':expected.tolist()}
        (directory/'reference.json').write_text(json.dumps(reference_data,indent=2)+'\n')

def error_stats(errors):
    return {'samples':int(len(errors)),'signedMean':float(errors.mean()),'meanAbsolute':float(abs(errors).mean()),'p95Absolute':float(np.percentile(abs(errors),95)),'maxAbsolute':float(abs(errors).max())}

def check(root,result_name="result"):
    images={};reports={}
    for kind in KINDS:
        directory=root/kind;reference_data=json.loads((directory/'reference.json').read_text());compute=json.loads((directory/result_name/'compute.json').read_text())
        assert compute['resolution']==reference_data['resolution'],f'{kind}: dimensions mismatch'
        assert abs(compute['radius']-reference_data['radius'])<1e-6,f'{kind}: radius mismatch'
        assert abs(compute['falloffFraction']-reference_data['falloffFraction'])<1e-6,f'{kind}: attenuation mismatch'
        for name in ('preparation.json','normals-coverage.rgba16f','depth.depth32f'):
            assert compute['inputSHA256'][name]==hashlib.sha256((directory/name).read_bytes()).hexdigest(),f'{kind}: stale input {name}'
        w,h=reference_data['resolution'];image=np.fromfile(directory/result_name/'ao.r32f',dtype='<f4').reshape(h,w)
        assert np.isfinite(image).all() and image.min()>=0 and image.max()<=1
        images[kind]=image;pixels=np.asarray(reference_data['pixels'],dtype=int)
        if len(pixels):
            observed=image[pixels[:,1],pixels[:,0]];errors=observed-np.array(reference_data['visibility']);surfaces=np.array(reference_data['surfaces'])
            reports[kind]=error_stats(errors)
            reports[kind]['surfaces']={str(int(i)):error_stats(errors[surfaces==i]) for i in np.unique(surfaces)}
    result={'qualityAccepted':False,'scope':'Provisional analytic controls; no live scene, motion or performance acceptance','referenceComparisons':reports,
        'flatMinimum':float(images['flat'][8:-8,8:-8].min()),'tiltedMinimum':float(images['tilted'][8:-8,8:-8].min()),'coverageMaximumDifference':float(abs(images['flat']-images['half-coverage']).max()),'emptyMinimum':float(images['empty'].min())}
    result['sanityPassed']=result['flatMinimum']>.97 and result['tiltedMinimum']>.95 and result['coverageMaximumDifference']==0 and result['emptyMinimum']==1
    result['referenceAccuracyPassed']=all(r['meanAbsolute']<.03 and r['p95Absolute']<.08 for r in reports.values())
    (root/('checked.json' if result_name=='result' else 'checked-'+result_name+'.json')).write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2));return result['sanityPassed'] and result['referenceAccuracyPassed']

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('directory',type=Path);parser.add_argument('--generate',action='store_true');parser.add_argument('--result-name',default='result');args=parser.parse_args()
    if args.generate:generate(args.directory)
    else:raise SystemExit(0 if check(args.directory,args.result_name) else 1)
