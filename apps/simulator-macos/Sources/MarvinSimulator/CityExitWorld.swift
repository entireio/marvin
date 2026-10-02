import AppKit
import SceneKit
import SimulationCore
import simd

extension DirtWorld {
    func updateGate(_ gate:CityGate) {
        let b=gate.body
        cityGateNode.position=SCNVector3(b.position.x,b.position.y,b.position.z)
        cityGateNode.eulerAngles.y=CGFloat(b.heading)
    }
    func addCityExit(clay:SCNMaterial,earth:SCNMaterial) {
        let metal=material(0x60615a,roughness:0.79)
        metal.metalness.contents=0.72
        let rusty=material(0x805443,roughness:0.94)
        func piece(_ w:Double,_ h:Double,_ d:Double,_ x:Double,_ y:Double,_ z:Double,_ m:SCNMaterial)->SCNNode {
            let geometry=SCNBox(width:w,height:h,length:d,chamferRadius:0.012)
            geometry.materials=[m];let node=SCNNode(geometry:geometry)
            node.position=SCNVector3(x,y,z);cityGateNode.addChildNode(node);return node
        }
        _=piece(CityExit.width,CityExit.gateHeight,0.085,0,CityExit.gateHeight/2,0,metal)
        for x in [-1.48,-0.5,0.5,1.48] { _=piece(0.065,CityExit.gateHeight,0.11,x,CityExit.gateHeight/2,0,rusty) }
        for y in [0.045,CityExit.gateHeight-0.045] { _=piece(CityExit.width,0.09,0.12,0,y,0,rusty) }
        let brace=piece(3.14,0.065,0.12,0,0.475,0,rusty);brace.eulerAngles.z=0.24
        for x in [-1.47,1.47] { for y in [0.14,0.81] {
            _=piece(0.065,0.065,0.135,x,y,0,metal)
        }}
        for x in stride(from:-0.7,through:0.7,by:0.2) {
            _=piece(0.14,0.11,0.09,x,0.67,0.012,material(Int(x*10)%4==0 ? 0xb6a064:0x454541,roughness:0.9))
        }
        cityGateNode.name="Motor-driven metal city gate";scene.rootNode.addChildNode(cityGateNode)
        for side in [-1.0,1.0] {
            let p=CityExit.point(side*(CityExit.width/2+0.09),0)
            let post=SCNCylinder(radius:0.085,height:CityExit.floor+CityExit.gateHeight+0.16)
            post.radialSegmentCount=12;post.materials=[rusty]
            let node=SCNNode(geometry:post);node.position=SCNVector3(p.x,(CityExit.floor+CityExit.gateHeight+0.11)/2,p.y)
            scene.rootNode.addChildNode(node)
        }
        updateGate(CityGate())
        // Match the track's actual offset-curve vertices at the inner edge.
        // A rectangular overlap grid cut across the raised lane and exposed
        // sliver triangles. This apron begins inside the retaining masonry.
        let nx=96,nz=100,start=Int(Double(DirtCourse.sampleCount)*0.125)-48
        let outlines=(0...nz).map { j in DirtCourse.surfacePoints(offset:DirtCourse.fenceOffset+Double(j)*0.08) }
        var v:[SCNVector3]=[],n:[SCNVector3]=[],uv:[CGPoint]=[],clayUV:[CGPoint]=[],colors:[Float]=[],indices:[Int32]=[]
        func heightAt(_ p:SIMD2<Double>)->Double {
            let h=DirtCourse.height(x:p.x,z:p.y),t=max(0,min(1,(h+0.025)/0.035))
            return h-0.008*(1-t*t*(3-2*t))
        }
        for j in 0...nz { for i in 0...nx {
            let point=outlines[j][(start+i+DirtCourse.sampleCount)%DirtCourse.sampleCount]
            let p=SIMD2(point.x,point.y),local=CityExit.local(p),along=local.x,out=local.y
            let height=heightAt(p)
            v.append(SCNVector3(p.x,height,p.y));uv.append(CGPoint(x:p.x/4,y:-p.y/4))
            clayUV.append(CGPoint(x:Double(start+i)/Double(DirtCourse.sampleCount),y:1+Double(j)*0.08/(DirtCourse.fenceOffset-DirtCourse.width-DirtCourse.bermWidth)))
            let epsilon=0.025
            let normal=simd_normalize(SIMD3(heightAt(p-SIMD2(epsilon,0))-heightAt(p+SIMD2(epsilon,0)),epsilon*2,heightAt(p-SIMD2(0,epsilon))-heightAt(p+SIMD2(0,epsilon))))
            n.append(SCNVector3(normal.x,normal.y,normal.z))
            let fade=max(0,min(1,(height+0.025)/0.06)),run=max(0,min(1,out/CityExit.run))
            let noise=CityMaterials.surfaceNoise(p.x/12,p.y/12,cells:9,seed:327)
            let distance=Double(j)*0.08,shoulder=min(1,distance/0.6),blend=shoulder*shoulder*(3-2*shoulder)
            let red=max(0,min(1,(1-run)*(1-min(1,abs(along)/4)*blend)+run*(1-run)*(noise-0.5)*0.7))
            colors += [Float(red*fade*fade*(3-2*fade)),0,0,1]
        }}
        for j in 0..<nz { for i in 0..<nx {
            let a=Int32(j*(nx+1)+i),b=a+1,c=a+Int32(nx+1),d=c+1
            indices += [a,b,c,b,d,c]
        }}
        let border=(0...nx).map{$0}+(1...nz).map{$0*(nx+1)+nx}+(0..<nx).reversed().map{nz*(nx+1)+$0}+(1..<nz).reversed().map{$0*(nx+1)}
        let bottom=Int32(v.count)
        for i in border { let p=v[i];v.append(SCNVector3(p.x,-0.1,p.z));n.append(SCNVector3(0,-1,0));uv.append(uv[i]);clayUV.append(clayUV[i]);colors += [0,0,0,1] }
        for i in border.indices { let j=(i+1)%border.count;indices += [Int32(border[i]),bottom+Int32(i),Int32(border[j]),Int32(border[j]),bottom+Int32(i),bottom+Int32(j)] }
        for i in 1..<border.count-1 { indices += [bottom,bottom+Int32(i),bottom+Int32(i+1)] }
        let sources=[SCNGeometrySource(vertices:v),SCNGeometrySource(normals:n),SCNGeometrySource(textureCoordinates:uv)]
        let elements=[SCNGeometryElement(indices:indices,primitiveType:.triangles)]
        let base=SCNGeometry(sources:sources,elements:elements),sand=earth.copy() as! SCNMaterial
        sand.diffuse.contentsTransform=SCNMatrix4Identity;base.materials=[sand]
        let baseNode=SCNNode(geometry:base);baseNode.name="Filled outside turn one city ramp";baseNode.castsShadow=false;scene.rootNode.addChildNode(baseNode)
        let tint=colors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:v.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
        let overlay=SCNGeometry(sources:[sources[0],sources[1],SCNGeometrySource(textureCoordinates:clayUV),tint],elements:[SCNGeometryElement(indices:Array(indices.prefix(nx*nz*6)),primitiveType:.triangles)]),pigment=clay.copy() as! SCNMaterial
        var modifiers=pigment.shaderModifiers ?? [:]
        modifiers[.geometry]="#pragma varyings\nhalf redSoil;\n#pragma body\nout.redSoil=half(_geometry.color.r);"
        modifiers[.fragment]="#pragma transparent\n#pragma body\n_output.color.rgb *= float(in.redSoil);\n_output.color.a=float(in.redSoil);"
        pigment.shaderModifiers=modifiers;pigment.transparencyMode = .aOne;pigment.writesToDepthBuffer=false;overlay.materials=[pigment]
        let top=SCNNode(geometry:overlay);top.position.y=0.0002;top.castsShadow=false;scene.rootNode.addChildNode(top)
    }
}
