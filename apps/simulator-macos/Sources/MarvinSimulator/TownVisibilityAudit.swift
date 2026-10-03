import AppKit
import SceneKit

extension AppController {
    /// Frozen-frame removal experiment, never a gameplay culling decision.
    /// The independent shadow proxies remain enabled while camera cells are hidden.
    func auditTownVisibility(at directory:URL)throws {
        guard dirtWorld.town.shadowBatchDiagnostics["enabled"]==1 else {
            throw NSError(domain:"TownVisibilityAudit",code:1,userInfo:[NSLocalizedDescriptionKey:"Independent production shadow proxies required"])
        }
        let priorScene=view.scene,priorContinuous=view.rendersContinuously,priorPlaying=view.isPlaying
        defer { view.scene=priorScene;view.isPlaying=priorPlaying;view.rendersContinuously=priorContinuous }
        view.rendersContinuously=false;view.isPlaying=false;view.scene=nil
        let renderer=SCNRenderer(device:view.device,options:nil)
        renderer.scene=dirtWorld.scene;renderer.pointOfView=world.camera;renderer.usesReverseZ=view.usesReverseZ
        var nodes:[SCNNode]=[]
        var inventoryNodes:[SCNNode]=[]
        var inventory:[[String:Any]]=[]
        dirtWorld.scene.rootNode.enumerateChildNodes { node,_ in
            if node.name?.hasPrefix("Town cell ")==true,!node.isHidden { nodes.append(node) }
            if node.geometry != nil { inventoryNodes.append(node) }
        }
        func recordInventory() {
          for node in inventoryNodes {
            guard let geometry=node.geometry,node.categoryBitMask & world.camera.camera!.categoryBitMask != 0 else { continue }
            var ancestry:[String]=[],current:SCNNode?=node,hidden=false
            while let item=current {
                if let name=item.name { ancestry.append(name) }
                hidden = hidden || item.isHidden || item.opacity==0
                current=item.parent
            }
            if !hidden {
                inventory.append(["ancestry":ancestry.reversed().map{$0},"insideCameraFrustum":renderer.isNode(node,insideFrustumOf:world.camera),
                    "basePrimitives":geometry.elements.reduce(0){$0+$1.primitiveCount},
                    "baseVertices":geometry.sources(for:.vertex).first?.vectorCount ?? 0,
                    "lodPrimitiveCounts":(geometry.levelsOfDetail ?? []).map{$0.geometry?.elements.reduce(0){$0+$1.primitiveCount} ?? 0}])
            }
          }
        }
        nodes.sort { ($0.name ?? "") < ($1.name ?? "") }
        defer { for node in nodes { node.isHidden=false } }
        func snapshot()->NSBitmapImageRep {
            autoreleasepool {
                let image=renderer.snapshot(atTime:0,with:CGSize(width:1920,height:1080),antialiasingMode:.multisampling2X)
                return NSBitmapImageRep(data:image.tiffRepresentation!)!
            }
        }
        func difference(_ a:NSBitmapImageRep,_ b:NSBitmapImageRep)throws -> (Int,Int) {
            guard a.bitsPerSample==8,b.bitsPerSample==8,a.samplesPerPixel==b.samplesPerPixel,
                  a.bitsPerPixel==a.samplesPerPixel*8,b.bitsPerPixel==b.samplesPerPixel*8,a.bitmapFormat==b.bitmapFormat,
                  a.pixelsWide==b.pixelsWide,a.pixelsHigh==b.pixelsHigh,!a.isPlanar,!b.isPlanar,
                  let aa=a.bitmapData,let bb=b.bitmapData else { throw NSError(domain:"TownVisibilityAudit",code:2) }
            var changed=0,maximum=0
            for y in 0..<a.pixelsHigh { for x in 0..<a.pixelsWide {
                var delta=0
                for c in 0..<a.samplesPerPixel {
                    delta=max(delta,abs(Int(aa[y*a.bytesPerRow+x*a.samplesPerPixel+c])-Int(bb[y*b.bytesPerRow+x*b.samplesPerPixel+c])))
                }
                if delta>0 { changed += 1 };maximum=max(maximum,delta)
            }}
            return (changed,maximum)
        }
        _=snapshot();_=snapshot()
        let baseline=snapshot(),repeatBaseline=snapshot()
        // SceneKit's frustum query needs the renderer's initialized camera state.
        recordInventory()
        let repeatDifference=try difference(baseline,repeatBaseline)
        try baseline.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("visibility-reference.png"))
        var rows:[[String:Any]]=[],invisible:[SCNNode]=[]
        for node in nodes {
            let frustum=renderer.isNode(node,insideFrustumOf:world.camera)
            let near=node.geometry!,far=near.levelsOfDetail?.first?.geometry
            var row:[String:Any]=["node":node.name!,"insideCameraFrustum":frustum,
                "nearTriangles":near.elements.reduce(0){$0+$1.primitiveCount},
                "farTriangles":far?.elements.reduce(0){$0+$1.primitiveCount} ?? 0,
                "nearVertices":near.sources(for:.vertex).first?.vectorCount ?? 0,
                "materialGroups":near.elements.count]
            if frustum {
                node.isHidden=true
                let removed=snapshot()
                node.isHidden=false
                let restored=snapshot()
                let delta=try difference(baseline,removed),restore=try difference(baseline,restored)
                row["removedChangedPixels"]=delta.0;row["removedMaxChannelDifference"]=delta.1
                row["restoredChangedPixels"]=restore.0
                let unchanged=repeatDifference.0==0 && restore.0==0 && delta.0==0
                row["noPixelContributionAtThisView"]=unchanged
                if unchanged { invisible.append(node) }
            }
            rows.append(row)
        }
        for node in invisible { node.isHidden=true }
        let combined=snapshot()
        for node in invisible { node.isHidden=false }
        let combinedDifference=try difference(baseline,combined),restoredDifference=try difference(baseline,snapshot())
        let valid=repeatDifference.0==0 && combinedDifference.0==0 && restoredDifference.0==0
            && rows.allSatisfy { ($0["restoredChangedPixels"] as? Int ?? 0)==0 }
            && rows.contains { ($0["removedChangedPixels"] as? Int ?? 0)>0 }
            && inventory.contains { ($0["insideCameraFrustum"] as? Bool)==true }
        try combined.representation(using:.png,properties:[:])!.write(to:directory.appendingPathComponent("visibility-combined-removal.png"))
        let report:[String:Any]=["validFrozenComparison":valid,"cameraPosition":[world.camera.position.x,world.camera.position.y,world.camera.position.z],
            "resolution":[1920,1080],"msaaSamples":2,"repeatBaselineChangedPixels":repeatDifference.0,
            "combinedRemovalChangedPixels":combinedDifference.0,"finalRestorationChangedPixels":restoredDifference.0,
            "noContributionCells":valid ? invisible.map{$0.name!}:[],"candidateNoContributionCells":invisible.map{$0.name!},"cells":rows,"geometryInventory":inventory,
            "method":"Frozen native cell removal with independent shadows retained; exact packed-channel comparison and repeated/restored controls.",
            "limitations":"One view only. Frustum membership and mesh inventory are not measured GPU submissions. Near/far counts do not identify selected LOD. Zero contribution is not a safe future-frame culling rule. Diagnostic snapshots are not performance measurements."]
        try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:directory.appendingPathComponent("visibility-audit.json"))
    }
}
