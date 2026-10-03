import AppKit
import SceneKit
import SimulationCore

extension AppController {
    /// Compare the same frozen native scene with the old transparent ground and
    /// the covered-base partition. Keep HDR, SSAO, shadows and MSAA enabled.
    func checkGroundPerformance(at directory:URL)->Bool {
        do {
            try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:true)
            let tangentFixturesPassed = !CommandLine.arguments.contains("--benchmark-tangent-reuse") || checkTangentReuseFixtures()
            weatherOverride=false;defer { weatherOverride=nil }
            startDirtTrack();dirtIntro=nil;race.countDown(dt:3)
            guard let terrain=dirtWorld.scene.rootNode.childNode(withName:"Town base terrain",recursively:true),let earth=terrain.geometry?.firstMaterial else { return false }
            let reference=SCNPlane(width:256,height:256);reference.materials=[earth]
            // Validate the primitive's actual generated UV mapping rather than
            // assuming SceneKit uses a particular vertical texture convention.
            guard let positions=reference.sources(for:.vertex).first,let coordinates=reference.sources(for:.texcoord).first else { return false }
            func component(_ source:SCNGeometrySource,_ vertex:Int,_ axis:Int)->Double {
                source.data.withUnsafeBytes { bytes in
                    let offset=source.dataOffset+vertex*source.dataStride+axis*source.bytesPerComponent
                    if source.bytesPerComponent==4 { return Double(bytes.loadUnaligned(fromByteOffset:offset,as:Float.self)) }
                    return bytes.loadUnaligned(fromByteOffset:offset,as:Double.self)
                }
            }
            let xs=(0..<positions.vectorCount).map { component(positions,$0,0) }
            let ys=(0..<positions.vectorCount).map { component(positions,$0,1) }
            // SCNPlane exposes a unit primitive source; width/height are applied
            // by SceneKit separately. Compare its normalized affine mapping.
            let uvMatches=(0..<positions.vectorCount).allSatisfy { i in
                abs(component(coordinates,i,0)-(xs[i]-xs.min()!)/(xs.max()!-xs.min()!))<1e-6
                    && abs(component(coordinates,i,1)-(ys.max()!-ys[i])/(ys.max()!-ys.min()!))<1e-6
            }
            guard uvMatches else { print("SCNPlane UV mapping differs from covered terrain");return false }
            let optimized=TownGround.coveredTerrain(material:earth)
            let renderer=SCNRenderer(device:view.device,options:nil)
            renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera
            var rows:[[String:Any]]=[]
            let meshComparison=CommandLine.arguments.contains("--mesh-reuse-test")
            let pairs=TownMesh.validationPairs
            let meshDataPassed = !meshComparison || (!pairs.isEmpty && pairs.allSatisfy { validateMeshReuse($0.0,$0.1) })
            let originals=Dictionary(uniqueKeysWithValues:pairs.map { (ObjectIdentifier($0.1),$0.0) })
            var meshNodes:[(SCNNode,SCNGeometry,SCNGeometry)]=[]
            if meshComparison {
                for (old,new) in pairs {
                    old.levelsOfDetail=new.levelsOfDetail?.map { level in
                        let g=level.geometry.flatMap { originals[ObjectIdentifier($0)] } ?? level.geometry
                        return level.screenSpaceRadius>0 ? SCNLevelOfDetail(geometry:g,screenSpaceRadius:level.screenSpaceRadius):SCNLevelOfDetail(geometry:g,worldSpaceDistance:level.worldSpaceDistance)
                    }
                }
                dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in
                    if let g=node.geometry,let old=originals[ObjectIdentifier(g)] { meshNodes.append((node,old,g)) }
                }
            }
            let shadowComparison=CommandLine.arguments.contains("--shadow-culling-test")
            var frustumPassed=true
            let views:[(String,SCNVector3,SCNVector3)]=[
                ("street",SCNVector3(35,1.3,14),SCNVector3(40,0.1,22)),
                ("inner-boundary",SCNVector3(29,1.1,-13),SCNVector3(39,-0.02,-16)),
                ("outer-boundary",SCNVector3(92,1.2,8),SCNVector3(101,0,10)),
                ("aerial",SCNVector3(42,34,15),SCNVector3(42,0,15)),
                ("uncovered-infield",SCNVector3(0,6,-4),SCNVector3(-4,0,-8)),
                ("inner-fade",SCNVector3(27,0.65,0),SCNVector3(36,-0.02,3)),
                ("grazing",SCNVector3(40,0.64,18),SCNVector3(90,0,20)),
                ("reverse-turn",SCNVector3(35,1.3,14),SCNVector3(29,0.1,3)),
                ("lod-boundary",SCNVector3(30,1.3,20),SCNVector3(110,0.8,10)),
                ("closeup-street",SCNVector3(35,1.3,14),SCNVector3(40,0.8,22)),
                ("closeup-reverse",SCNVector3(35,1.3,14),SCNVector3(29,0.8,3))]
            let originalFOV=world.camera.camera!.fieldOfView
            defer { world.camera.camera!.fieldOfView=originalFOV }
            for (light,fraction,stormEnabled) in [("midday",0.5,false),("low-sun",0.12,false),("storm",0.5,true)] {
                var storm=Sandstorm(enabled:stormEnabled);storm.advance(90)
                dirtWorld.configureStorm(storm)
                dirtWorld.sky.apply(BinaryDaylight(fraction:fraction,phase:1.2))
                for (name,eye,target) in views {
                    world.camera.camera!.fieldOfView=name.hasPrefix("closeup-") ? 20:originalFOV
                    world.camera.position=eye;world.camera.look(at:target,up:eye.x==target.x && eye.z==target.z ? SCNVector3(0,0,-1):SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
                    dirtWorld.sky.updateShadowCenter(SIMD3(Double(target.x),0,Double(target.z)))
                    var images:[NSBitmapImageRep]=[],casters:[Int]=[]
                    let optimizedFirst=shadowComparison && name=="reverse-turn"
                    let variants=optimizedFirst ? [("optimized",true),("reference",false)]:[("reference",false),("optimized",true)]
                    for (label,useOptimized) in variants {
                        terrain.geometry=shadowComparison || meshComparison || useOptimized ? optimized:reference
                        for (node,old,new) in meshNodes { node.geometry=useOptimized ? new:old }
                        dirtWorld.town.shadowCullingEnabled=shadowComparison && useOptimized
                        dirtWorld.town.shadowDirections=dirtWorld.sky.daylight.directions
                        dirtWorld.town.update(dt:0,camera:eye,player:SIMD2(Double(target.x),Double(target.z)),shadowCamera:world.camera,viewportAspect:1920.0/1080)
                        casters.append(dirtWorld.town.shadowCasterCount)
                        _=renderer.prepare(dirtWorld.scene,shouldAbortBlock:nil)
                        let image=renderer.snapshot(atTime:0,with:CGSize(width:1920,height:1080),antialiasingMode:view.antialiasingMode)
                        let bitmap=NSBitmapImageRep(data:image.tiffRepresentation!)!;images.append(bitmap)
                        try bitmap.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("\(light)-\(name)-\(label).png"))
                    }
                    if optimizedFirst { images.reverse();casters.reverse() }
                    if shadowComparison {
                        let frusta=ShadowFrustum.cameras(world.camera,aspect:1920.0/1080)
                        for (depth,expected) in [(-8.0,true),(8.0,false)] {
                            let p=world.camera.convertPosition(SCNVector3(0,0,depth),to:nil)
                            let proxy=SCNNode(geometry:SCNBox(width:0.5,height:0.5,length:0.5,chamferRadius:0));proxy.position=p
                            let center=SIMD3(Double(p.x),Double(p.y),Double(p.z))
                            let bounds=ShadowBounds(low:center-SIMD3(repeating:0.25),high:center+SIMD3(repeating:0.25))
                            frustumPassed = frustumPassed && renderer.isNode(proxy,insideFrustumOf:world.camera)==expected
                            if expected { frustumPassed = frustumPassed && frusta.contains { $0.intersects(bounds) } }
                        }
                        // A native-visible box must never be rejected by the
                        // conservative mathematical side-plane classifier.
                        for x in stride(from:-90.0,through:90.0,by:30) { for z in stride(from:-90.0,through:90.0,by:30) {
                            let proxy=SCNNode(geometry:SCNBox(width:4,height:10,length:4,chamferRadius:0))
                            proxy.position=SCNVector3(x,5,z)
                            let bounds=ShadowBounds(low:SIMD3(x-2,0,z-2),high:SIMD3(x+2,10,z+2))
                            if renderer.isNode(proxy,insideFrustumOf:world.camera) && !frusta.isEmpty {
                                frustumPassed = frustumPassed && frusta.contains { $0.intersects(bounds) }
                            }
                        }}
                    }
                    var changed=0,total=0,error=0.0,maximumError=0.0
                    // Full-resolution comparison: sparse sampling missed isolated
                    // normal-map differences at grazing angles.
                    for y in 0..<1080 { for x in 0..<1920 {
                        let a=images[0].colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                        let b=images[1].colorAt(x:x,y:y)!.usingColorSpace(.deviceRGB)!
                        let delta=max(abs(a.redComponent-b.redComponent),max(abs(a.greenComponent-b.greenComponent),abs(a.blueComponent-b.blueComponent)))
                        if delta>5.0/255 { changed += 1 };error += delta;maximumError=max(maximumError,delta);total += 1
                    }}
                    rows.append(["view":"\(light)-\(name)","changedFraction":Double(changed)/Double(total),"meanMaxChannelError":error/Double(total),"maximumChannelError":maximumError,"comparedPixels":total,"referenceCasters":casters[0],"optimizedCasters":casters[1]])
                }
            }
            let reduced=rows.contains { ($0["optimizedCasters"] as! Int)<($0["referenceCasters"] as! Int) }
            let passed=tangentFixturesPassed && TownMesh.maximumMergedBasisRadians<0.005*Double.pi/180 && meshDataPassed && (!meshComparison || (!meshNodes.isEmpty && TownMesh.outputVertices<TownMesh.inputVertices)) && frustumPassed && (!shadowComparison || reduced) && rows.allSatisfy { ($0["changedFraction"] as! Double)<(shadowComparison || meshComparison ? 0.00001:0.001) }
            let report:[String:Any]=["passed":passed,"tangentFixturesPassed":tangentFixturesPassed,"meshDataPassed":meshDataPassed,"meshInputVertices":TownMesh.inputVertices,"meshOutputVertices":TownMesh.outputVertices,"maximumMergedBasisDegrees":TownMesh.maximumMergedBasisRadians*180/Double.pi,"mergedBasisComparisons":TownMesh.mergedBasisComparisons,"comparisons":rows,"uvMappingPassed":uvMatches,"frustumPassed":frustumPassed,"shadowComparison":shadowComparison,"casterReductionObserved":reduced,"note":"Original receiver depth, transparent surface and material retained; small rasterization differences require manual review."]
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("comparison.json"))
            print(report);return passed
        } catch { print(error);return false }
    }

    private func checkTangentReuseFixtures()->Bool {
        func count(_ frames:[(SIMD3<Float>,SIMD3<Float>)])->Int {
            let mesh=TownMesh(),n=SIMD3<Float>(0,0,1)
            for (t,b) in frames { mesh.triangle(.zero,t,b,0xffffff,smooth:[n,n,n]) }
            mesh.uv=frames.flatMap { _ in [CGPoint.zero,CGPoint(x:1,y:0),CGPoint(x:0,y:1)] }
            return mesh.geometry(material:SCNMaterial()).sources(for:.vertex).first!.vectorCount
        }
        let epsilon:Float=0.000002
        let unstable=count([(SIMD3(epsilon,epsilon,1),SIMD3(-1,0,0)),(SIMD3(-epsilon,epsilon,1),SIMD3(-1,0,0))])==6
        let mirrored=count([(SIMD3(1,0,0),SIMD3(0,1,0)),(SIMD3(1,0,0),SIMD3(0,-1,0))])==6
        let stable=count([(SIMD3(1,0,0),SIMD3(0,1,0)),(SIMD3(2,0,0),SIMD3(0,2,0))])==5
        let y:Float=0.250001
        let isolatedFallback=count([(SIMD3(1,0,0),SIMD3(sqrt(1-y*y),y,0)),(SIMD3(1,0,0),SIMD3(0.96824646,0.25,0))])==6
        print("Tangent reuse fixtures: unstable=\(unstable), mirrored=\(mirrored), stable=\(stable), fallback=\(isolatedFallback)")
        return unstable && mirrored && stable && isolatedFallback
    }
}
