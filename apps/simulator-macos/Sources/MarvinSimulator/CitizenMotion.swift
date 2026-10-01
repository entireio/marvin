import SceneKit

/// Joint-space deformation on the GPU keeps the large spectator batches intact.
/// Per-person bind frames and joint masks travel with every LOD's vertices.
enum CitizenMotion {
    static let shader = """
    #pragma arguments
    float crowdTime;
    float walkCycle;
    float walkBlend;
    #pragma body
    float2 originXZ = _geometry.texcoords[1];
    float2 frame = _geometry.texcoords[2];
    float2 person = _geometry.texcoords[3];
    float2 arm = _geometry.texcoords[4];
    float2 joints = _geometry.texcoords[5];
    float2 kind = _geometry.texcoords[6];
    float c=cos(frame.x), s=sin(frame.x);
    float3 p=_geometry.position.xyz-float3(originXZ.x,frame.y,originXZ.y);
    float3 n=_geometry.normal;
    p=float3(c*p.x-s*p.z,p.y,s*p.x+c*p.z);
    n=float3(c*n.x-s*n.z,n.y,s*n.x+c*n.z);
    float phase=person.x, neck=person.y;
    float headAngle=(0.26*sin(crowdTime*0.47+phase)+0.09*sin(crowdTime*0.19+phase*2.0));
    float headWeight=smoothstep(neck-0.012,neck+0.045,p.y);
    float ha=headAngle*headWeight, hc=cos(ha), hs=sin(ha);
    p.xz=float2(hc*p.x+hs*p.z,-hs*p.x+hc*p.z);
    n.xz=float2(hc*n.x+hs*n.z,-hs*n.x+hc*n.z);
    float cycle=walkCycle+phase;
    float waveWindow=pow(max(0.0,sin(crowdTime*0.31+phase)),6.0);
    // Seated spectators and a few standing onlookers react independently.
    float wave=(1.0-walkBlend)*waveWindow*(kind.x>0.5 ? 1.0:0.45);
    // Alternate arms; a bent elbow reads as a wave rather than a T-pose.
    wave *= (fmod(floor(phase),2.0)<1.0 ? (arm.y>0.0 ? 1.0:0.0):(arm.y<0.0 ? 1.0:0.0));
    float2 bind=_geometry.texcoords[7];
    float3 shoulder=float3(arm.y*bind.x,bind.y,0.0);
    float elbowWeight=arm.x*(1.0-smoothstep(bind.y-0.20,bind.y-0.12,p.y));
    float ea=arm.y*(0.9+0.23*sin(crowdTime*5.0+phase))*wave*elbowWeight;
    float ec=cos(ea),es=sin(ea);
    float3 elbow=shoulder+float3(arm.y*0.025,-0.18,0.04),q=p-elbow;
    p=elbow+float3(ec*q.x-es*q.y,es*q.x+ec*q.y,q.z);
    n=float3(ec*n.x-es*n.y,es*n.x+ec*n.y,n.z);
    float a=arm.y*0.92*wave*arm.x, ac=cos(a), sa=sin(a);
    q=p-shoulder;p=shoulder+float3(ac*q.x-sa*q.y,sa*q.x+ac*q.y,q.z);
    n=float3(ac*n.x-sa*n.y,sa*n.x+ac*n.y,n.z);
    float swing=arm.y*0.18*sin(cycle)*walkBlend*arm.x,sc=cos(swing),ss=sin(swing);
    q=p-shoulder;p=shoulder+float3(q.x,sc*q.y-ss*q.z,ss*q.y+sc*q.z);
    n=float3(n.x,sc*n.y-ss*n.z,ss*n.y+sc*n.z);
    if(walkBlend>0.001 && kind.x<0.5) {
        float side=p.x<0.0 ? -1.0:1.0;
        float legPhase=cycle+(side<0.0 ? 3.14159265:0.0);
        float legWeight=(kind.y==2.0 || kind.y==3.0) ? 1.0:0.32;
        legWeight*=1.0-smoothstep(joints.x-0.05,joints.x+0.02,p.y);
        float angle=0.24*cos(legPhase)*walkBlend*legWeight;
        float lc=cos(angle),ls=sin(angle);
        q=p-float3(side*0.06,joints.x,0.0);
        p=float3(q.x,lc*q.y-ls*q.z,ls*q.y+lc*q.z)+float3(side*0.06,joints.x,0.0);
        n=float3(n.x,lc*n.y-ls*n.z,ls*n.y+lc*n.z);
        // Both the boot and trouser hem receive the same lower-leg transform.
        float kneeWeight=1.0-smoothstep(joints.y-0.025,joints.y+0.04,_geometry.position.y-frame.y);
        float bend=0.42*max(0.0,sin(legPhase))*walkBlend*kneeWeight*legWeight;
        float kc=cos(bend),ks=sin(bend);
        q=p-float3(side*0.06,joints.y,0.0);
        p=float3(q.x,kc*q.y-ks*q.z,ks*q.y+kc*q.z)+float3(side*0.06,joints.y,0.0);
        n=float3(n.x,kc*n.y-ks*n.z,ks*n.y+kc*n.z);
        p.y-=joints.x*(1.0-cos(0.24*cos(cycle)))*walkBlend;
    }
    _geometry.position.xyz=float3(c*p.x+s*p.z,p.y,-s*p.x+c*p.z)+float3(originXZ.x,frame.y,originXZ.y);
    _geometry.normal=normalize(float3(c*n.x+s*n.z,n.y,-s*n.x+c*n.z));
    """
    static func materials()->[SCNMaterial] {
        [CityMaterials.skin,CityMaterials.crowdCloth,CityMaterials.leather].map {
            let material=$0.copy() as! SCNMaterial
            var modifiers=material.shaderModifiers ?? [:]
            let original=modifiers[.geometry] ?? "#pragma body\n"
            // Keep palette conversion in the same geometry modifier.
            modifiers[.geometry]=shader.replacingOccurrences(of:"#pragma body",with:original)
            material.shaderModifiers=modifiers
            material.setValue(Float(0),forKey:"crowdTime")
            material.setValue(Float(0),forKey:"walkCycle")
            material.setValue(Float(0),forKey:"walkBlend")
            return material
        }
    }
}

extension CitizenMotion {
    /// Evaluate only the sole vertices, using the same joint transforms as the
    /// GPU. One planted foot stays on the ground throughout the stride.
    struct FootPlacement {
        let soles:[SIMD3<Float>],phase:Float,hip:Float,knee:Float
        init(_ geometry:SCNGeometry) {
            func values(_ source:SCNGeometrySource,_ index:Int)->[Float] {
                (0..<source.componentsPerVector).map { axis in
                    source.data.withUnsafeBytes { bytes in
                        let offset=source.dataOffset+index*source.dataStride+axis*source.bytesPerComponent
                        return source.bytesPerComponent==4 ? bytes.loadUnaligned(fromByteOffset:offset,as:Float.self):Float(bytes.loadUnaligned(fromByteOffset:offset,as:Double.self))
                    }
                }
            }
            let positions=geometry.sources(for:.vertex)[0],uv=geometry.sources(for:.texcoord)
            phase=values(uv[3],0)[0];let joints=values(uv[5],0);hip=joints[0];knee=joints[1]
            soles=(0..<positions.vectorCount).compactMap { i in
                let p=values(positions,i)
                return values(uv[6],i)[1]==3 && p[1]<0.19 ? SIMD3(p[0],p[1],p[2]):nil
            }
        }
        func minimum(cycle:Float,blend:Float)->Float {
            let cycle=cycle+phase,bob=hip*(1-cos(0.24*cos(cycle)))*blend
            var lowest=Float.infinity
            for side:Float in [-1,1] {
                let phase=cycle+(side<0 ? .pi:0),angle=0.24*cos(phase)*blend
                let c=cos(angle),s=sin(angle),bend=0.42*max(0,sin(phase))*blend,kc=cos(bend),ks=sin(bend)
                for p in soles where p.x*side>0 {
                    let y=(p.y-hip)*c-p.z*s+hip,z=(p.y-hip)*s+p.z*c
                    lowest=min(lowest,(y-knee)*kc-z*ks+knee-bob)
                }
            }
            return lowest.isFinite ? lowest:0
        }
    }
}
