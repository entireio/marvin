import SceneKit
import SimulationCore
import simd

extension DirtWorld {
    /// Fixed, prebuilt tiles: no mesh allocation during play. Near tiles use the
    /// exact collision grid; distant LODs retain matching vertices along their shared edges.
    func addDesertTerrain(earth:SCNMaterial) {
        let sand=earth.copy() as! SCNMaterial
        sand.diffuse.contentsTransform=SCNMatrix4Identity
        sand.roughness.contents=0.94
        sand.shaderModifiers=[.surface:"""
        #pragma body
        float2 p=_surface.diffuseTexcoord*4.0;
        float edge=max(abs(p.x),abs(p.y));
        float desert=smoothstep(156.0,192.0,edge);
        float bands=0.5+0.5*sin(p.x*0.071+p.y*0.043+2.0*sin(p.y*0.019));
        float grain=dot(_surface.diffuse.rgb,float3(0.299,0.587,0.114));
        float3 dune=float3(0.64,0.43,0.23)*(0.83+0.30*grain)+bands*float3(0.075,0.058,0.029);
        _surface.diffuse.rgb=mix(_surface.diffuse.rgb,dune,desert);
        float ripple=sin(p.x*17.0+p.y*5.8+1.8*sin(p.y*0.41)+sin(p.x*0.22));
        _surface.normal=normalize(_surface.normal+float3(0.022*ripple,0.0,0.008*ripple)*desert);
        """]
        let tile=64.0
        func geometry(_ x:Double,_ z:Double,_ stride:Int)->SCNGeometry {
            var vertices:[SCNVector3]=[],normals:[SCNVector3]=[],uv:[CGPoint]=[],indices:[Int32]=[]
            var vertexIDs:[Int:Int32]=[:]
            func vertex(_ i:Int,_ j:Int)->Int32 {
                let key=j*33+i
                if let id=vertexIDs[key] { return id }
                let px=x+Double(i)*2,pz=z+Double(j)*2
                let y=DesertTerrain.vertexHeight(x:px,z:pz)
                let g=DesertTerrain.gradient(x:px,z:pz),normal=simd_normalize(SIMD3(-g.x,1,-g.y))
                let id=Int32(vertices.count);vertexIDs[key]=id
                vertices.append(SCNVector3(Double(i)*2,y,Double(j)*2))
                normals.append(SCNVector3(normal.x,normal.y,normal.z));uv.append(CGPoint(x:px/4,y:pz/4))
                return id
            }
            for j in Swift.stride(from:0,to:32,by:stride) { for i in Swift.stride(from:0,to:32,by:stride) {
                let corners=[SIMD2(i,j),SIMD2(i,j+stride),SIMD2(i+stride,j+stride),SIMD2(i+stride,j)]
                if stride==1 || (i>0 && j>0 && i+stride<32 && j+stride<32) {
                    let ids=corners.map{vertex($0.x,$0.y)}
                    indices += [ids[0],ids[1],ids[3],ids[3],ids[1],ids[2]]
                } else {
                    // Every LOD preserves the exact 2 m boundary, stitching its
                    // coarse interior to identical neighbor vertices. No cracks
                    // or dark vertical skirt strips across the sand ridgelines.
                    var polygon:[Int32]=[]
                    let boundary=[i==0,j+stride==32,i+stride==32,j==0]
                    for edge in 0..<4 {
                        let a=corners[edge],b=corners[(edge+1)%4],steps=boundary[edge] ? stride:1
                        for k in 0..<steps {
                            let qx=a.x+(b.x-a.x)*k/steps,qz=a.y+(b.y-a.y)*k/steps
                            polygon.append(vertex(qx,qz))
                        }
                    }
                    let center=vertex(i+stride/2,j+stride/2)
                    for k in polygon.indices { indices += [center,polygon[k],polygon[(k+1)%polygon.count]] }
                }
            }}
            let result=SCNGeometry(sources:[SCNGeometrySource(vertices:vertices),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv)],elements:[SCNGeometryElement(indices:indices,primitiveType:.triangles)])
            result.materials=[sand];return result
        }
        for j in -12..<12 { for i in -12..<12 {
            if (-2..<2).contains(i) && (-2..<2).contains(j) { continue }
            let x=Double(i)*tile,z=Double(j)*tile
            let mesh=geometry(x,z,1)
            mesh.levelsOfDetail=[SCNLevelOfDetail(geometry:geometry(x,z,2),worldSpaceDistance:130),SCNLevelOfDetail(geometry:geometry(x,z,4),worldSpaceDistance:260)]
            let node=SCNNode(geometry:mesh);node.position=SCNVector3(x,0,z)
            node.name="Wind-shaped sand dunes";node.castsShadow=false
            scene.rootNode.addChildNode(node)
        }}
        // Fill beyond the finite dune field; its perimeter eases back to flat.
        let horizon=SCNNode(geometry:SCNPlane(width:4000,height:4000))
        horizon.geometry?.materials=[earth];horizon.eulerAngles.x = -.pi/2;horizon.position.y = -0.05
        horizon.castsShadow=false;scene.rootNode.addChildNode(horizon)
    }
}
