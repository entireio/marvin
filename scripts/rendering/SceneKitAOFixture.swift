import AppKit
import SceneKit
import Metal
import simd

/// Unit-intensity AO extraction control, not gameplay appearance acceptance.
/// Build independently; USE_SSAO sentinel verifies the main-pass extraction.
@main struct Fixture {
 static func main() throws {
  let input=URL(fileURLWithPath:CommandLine.arguments[1]),out=URL(fileURLWithPath:CommandLine.arguments[2])
  try FileManager.default.createDirectory(at:out,withIntermediateDirectories:true)
  let data=try JSONSerialization.jsonObject(with:Data(contentsOf:input.appendingPathComponent("preparation.json"))) as! [String:Any]
  let wh=data["resolution"] as! [Int],w=wh[0],h=wh[1],a=(data["projectionColumnMajor"] as! [NSNumber]).map{$0.floatValue}
  let projection=simd_float4x4(columns:(SIMD4(a[0],a[1],a[2],a[3]),SIMD4(a[4],a[5],a[6],a[7]),SIMD4(a[8],a[9],a[10],a[11]),SIMD4(a[12],a[13],a[14],a[15])))
  let device=MTLCreateSystemDefaultDevice()!,scene=SCNScene(),eye=SCNNode();eye.camera=SCNCamera();eye.camera!.projectionTransform=SCNMatrix4(projection)
  eye.camera!.zNear=0.02;eye.camera!.zFar=250;eye.camera!.wantsHDR=false
  eye.camera!.screenSpaceAmbientOcclusionIntensity=1;eye.camera!.screenSpaceAmbientOcclusionRadius=1.6;eye.camera!.screenSpaceAmbientOcclusionBias=0.025
  scene.rootNode.addChildNode(eye)
  let light=SCNNode();light.light=SCNLight();light.light!.type = .ambient;light.light!.color=NSColor.white;light.light!.intensity=1000;scene.rootNode.addChildNode(light)
  let kind=input.lastPathComponent
  func plane(_ position:SIMD3<Float>,_ normal:SIMD3<Float>,_ width:CGFloat=1000,_ height:CGFloat=1000) {
   let material=SCNMaterial();material.lightingModel = .physicallyBased;material.diffuse.contents=NSColor.white;material.roughness.contents=1;material.metalness.contents=0
   material.shaderModifiers=[.fragment:"#pragma body\n#if defined(USE_SSAO)\n_output.color=float4(float3(_surface.ambientOcclusion),1.0);\n#endif\n"]
   if ProcessInfo.processInfo.environment["MARVIN_AO_SENTINEL"]=="1" { material.shaderModifiers=[.fragment:"#pragma body\n#if defined(USE_SSAO)\n_output.color=float4(0.25,0.5,0.75,1.0);\n#endif\n"] }
   let geometry=SCNPlane(width:width,height:height);geometry.materials=[material]
   let node=SCNNode(geometry:geometry);node.simdPosition=position;node.simdOrientation=simd_quatf(from:SIMD3<Float>(0,0,1),to:simd_normalize(normal));scene.rootNode.addChildNode(node)
  }
  if kind != "empty" { plane(SIMD3(0,0,-4),kind=="tilted" ? SIMD3(0.3,0,1):kind=="grazing" ? SIMD3(1,0,0.2):SIMD3(0,0,1)) }
  if kind=="corner" { plane(SIMD3(0,-1.2,0),SIMD3(0,1,0)) }
  if kind=="corner-mirror" { plane(SIMD3(0,1.2,0),SIMD3(0,-1,0)) }
  if kind=="corner-rotated" { plane(SIMD3(-1.2,0,0),SIMD3(1,0,0)) }
  if kind=="thin-occluder" { plane(SIMD3(0,0,-3),SIMD3(0,0,1),0.07,1.2) }
  let renderer=SCNRenderer(device:device,options:nil);renderer.scene=scene;renderer.pointOfView=eye;renderer.usesReverseZ=true;renderer.update(atTime:0)
  let desc=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.rgba16Float,width:w,height:h,mipmapped:false);desc.usage=[.renderTarget,.shaderRead];desc.storageMode = .private;let color=device.makeTexture(descriptor:desc)!
  desc.pixelFormat = .depth32Float;let depth=device.makeTexture(descriptor:desc)!
  let pass=MTLRenderPassDescriptor();pass.colorAttachments[0].texture=color;pass.colorAttachments[0].loadAction = .clear;pass.colorAttachments[0].storeAction = .store;pass.colorAttachments[0].clearColor=MTLClearColorMake(1,1,1,1);pass.depthAttachment.texture=depth;pass.depthAttachment.loadAction = .clear;pass.depthAttachment.storeAction = .dontCare;pass.depthAttachment.clearDepth=0
  let queue=device.makeCommandQueue()!
  for _ in 0..<10 { let c=queue.makeCommandBuffer()!;renderer.render(withViewport:CGRect(x:0,y:0,width:w,height:h),commandBuffer:c,passDescriptor:pass);c.commit();c.waitUntilCompleted();if let e=c.error { throw e } }
  let b=device.makeBuffer(length:w*h*8,options:.storageModeShared)!,c=queue.makeCommandBuffer()!,blit=c.makeBlitCommandEncoder()!
  blit.copy(from:color,sourceSlice:0,sourceLevel:0,sourceOrigin:MTLOrigin(x:0,y:0,z:0),sourceSize:MTLSize(width:w,height:h,depth:1),to:b,destinationOffset:0,destinationBytesPerRow:w*8,destinationBytesPerImage:w*h*8);blit.endEncoding();c.commit();c.waitUntilCompleted()
  let report:[String:Any]=["resolution":wh,"radius":1.6,"intensity":1.0,"bias":0.025,"extraction":"Fragment USE_SSAO guard exports _surface.ambientOcclusion at unit intensity; not final gameplay appearance","sentinel":ProcessInfo.processInfo.environment["MARVIN_AO_SENTINEL"]=="1"]
  try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:out.appendingPathComponent("scenekit.json"))
  try Data(bytes:b.contents(),count:w*h*8).write(to:out.appendingPathComponent("scenekit.rgba16f"));print(kind,"done")
 }
}
