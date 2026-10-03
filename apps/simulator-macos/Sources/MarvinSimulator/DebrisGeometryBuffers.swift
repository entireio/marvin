import Metal
import SceneKit
import simd

/// Immutable GPU-backed attributes avoid SceneKit staging four separate data
/// sources per debris update. Published buffers are never modified or recycled.
final class DebrisGeometryBuffers {
    private let device:MTLDevice
    private var elements:[Bool:MTLBuffer]=[:]
    private let capacity:Int
    init(device:MTLDevice,capacity:Int) {
        self.device=device;self.capacity=capacity
        for dust in [false,true] {
            var indices:[Int32]=[]
            for i in 0..<capacity {
                let base=Int32(i*4)
                indices += (dust ? [0,1,2,0,2,3]:[0,2,1,0,3,2,0,1,3,1,2,3]).map{base+Int32($0)}
            }
            elements[dust]=indices.withUnsafeBytes { device.makeBuffer(bytes:$0.baseAddress!,length:$0.count,options:.storageModeShared) }
        }
    }
    func geometry(vertices:[SCNVector3],normals:[SCNVector3],uv:[CGPoint],rgba:[Float],dust:Bool,material:SCNMaterial)->SCNGeometry? {
        guard !vertices.isEmpty,vertices.count<=capacity*4,vertices.count%4==0,
              normals.count==vertices.count,uv.count==vertices.count,rgba.count==vertices.count*4,
              let indexBuffer=elements[dust],
              let buffer=device.makeBuffer(length:vertices.count*48,options:.storageModeShared) else { return nil }
        let data=buffer.contents().bindMemory(to:Float.self,capacity:vertices.count*12)
        var low=SIMD3<Float>(repeating:.infinity),high=SIMD3<Float>(repeating:-.infinity)
        for i in vertices.indices {
            let p=SIMD3(Float(vertices[i].x),Float(vertices[i].y),Float(vertices[i].z))
            let n=normals[i],offset=i*12
            low=simd_min(low,p);high=simd_max(high,p)
            data[offset]=p.x;data[offset+1]=p.y;data[offset+2]=p.z
            data[offset+3]=Float(n.x);data[offset+4]=Float(n.y);data[offset+5]=Float(n.z)
            data[offset+6]=Float(uv[i].x);data[offset+7]=Float(uv[i].y)
            for c in 0..<4 { data[offset+8+c]=rgba[i*4+c] }
        }
        let sources:[SCNGeometrySource]=[
            SCNGeometrySource(buffer:buffer,vertexFormat:.float3,semantic:.vertex,vertexCount:vertices.count,dataOffset:0,dataStride:48),
            SCNGeometrySource(buffer:buffer,vertexFormat:.float3,semantic:.normal,vertexCount:vertices.count,dataOffset:12,dataStride:48),
            SCNGeometrySource(buffer:buffer,vertexFormat:.float2,semantic:.texcoord,vertexCount:vertices.count,dataOffset:24,dataStride:48),
            SCNGeometrySource(buffer:buffer,vertexFormat:.float4,semantic:.color,vertexCount:vertices.count,dataOffset:32,dataStride:48)]
        let element=SCNGeometryElement(buffer:indexBuffer,primitiveType:.triangles,primitiveCount:vertices.count/4*(dust ? 2:4),bytesPerIndex:4)
        let geometry=SCNGeometry(sources:sources,elements:[element]);geometry.materials=[material]
        geometry.boundingBox=(SCNVector3(low),SCNVector3(high))
        return geometry
    }
}
