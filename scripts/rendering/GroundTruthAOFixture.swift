import AppKit
import CryptoKit
import Metal
import simd

@main struct GroundTruthAOFixture {
    static func main() throws {
        let input=URL(fileURLWithPath:CommandLine.arguments[1]),out=URL(fileURLWithPath:CommandLine.arguments[2])
        try FileManager.default.createDirectory(at:out,withIntermediateDirectories:true)
        let inputHashes=try Dictionary(uniqueKeysWithValues:["preparation.json","normals-coverage.rgba16f","depth.depth32f"].map { name in
            (name,SHA256.hash(data:try Data(contentsOf:input.appendingPathComponent(name))).map{String(format:"%02x",$0)}.joined())
        })
        let metadata=try JSONSerialization.jsonObject(with:Data(contentsOf:input.appendingPathComponent("preparation.json"))) as! [String:Any]
        let dimensions=metadata["resolution"] as! [Int],w=dimensions[0],h=dimensions[1]
        let values=(metadata["projectionColumnMajor"] as! [NSNumber]).map{$0.floatValue}
        let projection=simd_float4x4(columns:(SIMD4(values[0],values[1],values[2],values[3]),SIMD4(values[4],values[5],values[6],values[7]),SIMD4(values[8],values[9],values[10],values[11]),SIMD4(values[12],values[13],values[14],values[15])))
        let device=MTLCreateSystemDefaultDevice()!,queue=device.makeCommandQueue()!
        func texture(_ name:String,_ format:MTLPixelFormat,_ stride:Int) throws ->MTLTexture {
            let data=try Data(contentsOf:input.appendingPathComponent(name));precondition(data.count==w*h*stride)
            let descriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:format,width:w,height:h,mipmapped:false)
            descriptor.storageMode = .shared;descriptor.usage = .shaderRead
            let texture=device.makeTexture(descriptor:descriptor)!
            data.withUnsafeBytes { texture.replace(region:MTLRegionMake2D(0,0,w,h),mipmapLevel:0,withBytes:$0.baseAddress!,bytesPerRow:w*stride) }
            return texture
        }
        let normal=try texture("normals-coverage.rgba16f",.rgba16Float,8)
        // Identical raw float depth values in a shader-readable color texture.
        let depth=try texture("depth.depth32f",.r32Float,4)
        let slices=Int(ProcessInfo.processInfo.environment["MARVIN_AO_SLICES"] ?? "3")!,steps=Int(ProcessInfo.processInfo.environment["MARVIN_AO_STEPS"] ?? "6")!
        let ao=try GroundTruthAO(device:device,width:w,height:h,slices:slices,steps:steps)
        var samples:[Double]=[],stageSamples:[[Double]]=[]
        for frame in 0..<45 {
            var frameStages:[Double]=[]
            for stage in 0..<3 {
            let command=queue.makeCommandBuffer()!
            ao.encode(command:command,normals:normal,depth:depth,projection:projection,reverseZ:metadata["reverseZ"] as! Bool,radius:Float(ProcessInfo.processInfo.environment["MARVIN_AO_RADIUS"] ?? "1.6")!,intensity:1,falloffFraction:Float(ProcessInfo.processInfo.environment["MARVIN_AO_FALLOFF"] ?? "0.5")!,stage:stage)
            command.commit();command.waitUntilCompleted()
            if let error=command.error { throw error }
            frameStages.append((command.gpuEndTime-command.gpuStartTime)*1000)
            }
            if frame>=15 { stageSamples.append(frameStages);samples.append(frameStages.reduce(0,+)) }
        }
        let buffer=device.makeBuffer(length:w*h*4,options:.storageModeShared)!,command=queue.makeCommandBuffer()!,blit=command.makeBlitCommandEncoder()!
        blit.copy(from:ao.output,sourceSlice:0,sourceLevel:0,sourceOrigin:MTLOrigin(x:0,y:0,z:0),sourceSize:MTLSize(width:w,height:h,depth:1),to:buffer,destinationOffset:0,destinationBytesPerRow:w*4,destinationBytesPerImage:w*h*4)
        blit.endEncoding();command.commit();command.waitUntilCompleted()
        if let error=command.error { throw error }
        try Data(bytes:buffer.contents(),count:w*h*4).write(to:out.appendingPathComponent("ao.r32f"))
        let pixels=buffer.contents().bindMemory(to:Float.self,capacity:w*h)
        let bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:w,pixelsHigh:h,bitsPerSample:8,samplesPerPixel:3,hasAlpha:false,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:w*3,bitsPerPixel:24)!
        for i in 0..<w*h {
            precondition(pixels[i].isFinite && pixels[i]>=0 && pixels[i]<=1)
            let value=UInt8((pixels[i]*255).rounded())
            for c in 0..<3 { bitmap.bitmapData![i*3+c]=value }
        }
        try bitmap.representation(using:.png,properties:[:])!.write(to:out.appendingPathComponent("ao.png"))
        let report:[String:Any]=["accepted":false,"inputSHA256":inputHashes,"radius":Float(ProcessInfo.processInfo.environment["MARVIN_AO_RADIUS"] ?? "1.6")!,"slices":slices,"stepsPerSide":steps,"falloffFraction":Float(ProcessInfo.processInfo.environment["MARVIN_AO_FALLOFF"] ?? "0.5")!,"gpuDevice":device.name,"resolution":dimensions,"gpuComputeAndFilterMS":samples,"stageSamplesMS":stageSamples,"stageColumns":["linearDepthPreparation","horizonIntegral","spatialFilter"],"thermalState":ProcessInfo.processInfo.thermalState.rawValue,"scope":"Frozen calculation and spatial filter only; excludes preparation, main render, synchronization and presentation"]
        try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]).write(to:out.appendingPathComponent("compute.json"))
        print("GTAO compute fixture complete: \(out.path)")
    }
}
