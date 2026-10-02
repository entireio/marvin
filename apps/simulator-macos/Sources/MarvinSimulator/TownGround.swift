import SceneKit
import simd
import SimulationCore

/// Baked, soft-edged wear attached to actual entrances and walking routes.
/// This is a single static mesh; it adds no per-frame pathfinding or decals.
enum TownGround {
    static func build(access:[TownWorld.PedestrianAccess])->SCNNode {
        let mesh=TownMesh()
        func patch(_ center:SIMD2<Double>,_ axis:SIMD2<Double>,_ width:Double,_ depth:Double,_ seed:Int,_ ink:UInt32,_ opacity:Float) {
            let across=SIMD2(axis.y,-axis.x),segments=18
            let height:Float = -0.019
            func vertex(_ angle:Double,_ ring:Double)->SIMD3<Float> {
                let uneven=1+0.15*sin(angle*3+Double(seed)*0.8)+0.09*cos(angle*5+Double(seed))
                let p=center+across*(cos(angle)*width*ring*uneven)+axis*(sin(angle)*depth*ring*uneven)
                return SIMD3(Float(p.x),height,Float(p.y))
            }
            func triangle(_ a:SIMD3<Float>,_ b:SIMD3<Float>,_ c:SIMD3<Float>,_ alpha:[Float]) {
                let start=mesh.colors.count/4;mesh.triangle(a,b,c,ink)
                for j in 0..<3 { mesh.colors[(start+j)*4+3]=alpha[j] }
            }
            let middle=SIMD3(Float(center.x),height,Float(center.y))
            for k in 0..<segments {
                let a=Double(k)*2 * .pi/Double(segments),b=Double(k+1)*2 * .pi/Double(segments)
                let ia=vertex(a,0.38),ib=vertex(b,0.38),oa=vertex(a,1),ob=vertex(b,1)
                triangle(middle,ib,ia,[opacity,opacity*0.72,opacity*0.72])
                triangle(ia,ib,ob,[opacity*0.72,opacity*0.72,0]);triangle(ia,ob,oa,[opacity*0.72,0,0])
            }
        }
        for (index,entry) in access.enumerated() where max(abs(entry.building.x),abs(entry.building.y))>30 {
            guard entry.route.count>3 else { continue }
            let direction=simd_normalize(entry.route[min(4,entry.route.count-1)]-entry.door)
            // Foot traffic and wind leave broad, irregular tonal transitions,
            // not hard driveway strips or a identical pad around every house.
            if index%3 != 0 {
                patch(entry.door+direction*0.75,direction,0.9+Double(index%4)*0.16,1.3+Double(index%5)*0.22,index,0x8e806a,0.22)
            }
            if index%4==0,entry.route.count>18 {
                let p=entry.route[entry.route.count/2]
                patch(p,direction,1.2,2.1,index+73,0xc2ac87,0.17)
            }
        }
        let material=SCNMaterial();material.lightingModel = .physicallyBased
        material.roughness.contents=1.0;material.diffuse.contents=NSColor.white
        material.writesToDepthBuffer=false;material.transparencyMode = .aOne
        material.shaderModifiers=[.geometry:"""
        #pragma varyings
        half4 groundTint;
        #pragma body
        out.groundTint=half4(half3(pow(max(_geometry.color.rgb,float3(0)),float3(2.2))),half(_geometry.color.a));
        """,.surface:"_surface.diffuse.rgb=float3(in.groundTint.rgb);",.fragment:"""
        #pragma transparent
        #pragma body
        _output.color.rgb *= float(in.groundTint.a);
        _output.color.a=float(in.groundTint.a);
        """]
        let node=SCNNode(geometry:mesh.geometry(material:material));node.castsShadow=false;node.name="Worn doorway approaches and windblown courtyard sand"
        let root=SCNNode();root.name="Town ground surfaces"
        root.addChildNode(trampledGround());root.addChildNode(node)
        return root
    }
    private static func trampledGround()->SCNNode {
        let mesh=TownMesh(),step=4
        for z in stride(from:-152,to:152,by:step) { for x in stride(from:-152,to:152,by:step) {
            let points=[SIMD3(Float(x),Float(-0.023),Float(z)),SIMD3(Float(x),Float(-0.023),Float(z+step)),SIMD3(Float(x+step),Float(-0.023),Float(z+step)),SIMD3(Float(x+step),Float(-0.023),Float(z))]
            let alpha=points.map { p -> Float in
                let inner=max(abs(p.x),abs(p.z))
                let outer=Float(TownFootprint.edgeDistance(SIMD2(Double(p.x),Double(p.z))))
                return min(1,max(0,(inner-28)/8))*min(1,max(0,(-outer+5)/12))
            }
            if alpha.allSatisfy({$0==0}) { continue }
            for ids in [[0,1,2],[0,2,3]] {
                let start=mesh.colors.count/4
                mesh.triangle(points[ids[0]],points[ids[1]],points[ids[2]],0xffffff)
                for j in 0..<3 {
                    let p=points[ids[j]]
                    mesh.colors[(start+j)*4+3]=alpha[ids[j]]
                    mesh.uv[start+j]=CGPoint(x:Double(p.x)/4,y:Double(p.z)/4)
                }
            }
        }}
        let m=SCNMaterial();m.lightingModel = .physicallyBased
        m.diffuse.contents=CityMaterials.asset("ground-base.jpg")
        m.normal.contents=CityMaterials.asset("ground-normal.jpg");m.normal.intensity=0.65
        m.roughness.contents=CityMaterials.asset("ground-rough.jpg")
        for map in [m.diffuse,m.normal,m.roughness] { map.wrapS = .repeat;map.wrapT = .repeat;map.mipFilter = .linear;map.maxAnisotropy=8 }
        m.writesToDepthBuffer=false;m.transparencyMode = .aOne
        m.shaderModifiers=[.geometry:"""
        #pragma varyings
        half groundBlend;
        #pragma body
        out.groundBlend=half(_geometry.color.a);
        """,.surface:"""
        #pragma body
        float grain=dot(_surface.diffuse.rgb,float3(0.2126,0.7152,0.0722));
        _surface.diffuse.rgb=float3(0.337,0.262,0.172)*(0.52+2.5*grain);
        """,.fragment:"""
        #pragma transparent
        #pragma body
        _output.color.rgb *= float(in.groundBlend);
        _output.color.a=float(in.groundBlend);
        """]
        let node=SCNNode(geometry:mesh.geometry(material:m));node.castsShadow=false
        node.name="Trampled sand in town, blended out before the circuit and dunes"
        return node
    }
}
