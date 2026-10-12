import Metal
import simd

/// Rejected full-resolution GTAO experiment: too slow and over-occludes thin
/// geometry. Retained solely for reproducible regression controls. Inputs are the
/// validated view-normal/coverage and geometric depth textures. The horizon
/// integral follows Jimenez et al. (2016), with projected-normal integration
/// adapted from Intel XeGTAO. This is not the complete XeGTAO implementation.
/// https://github.com/GameTechDev/XeGTAO
/*
MIT License

Copyright (C) 2016-2021, Intel Corporation 

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/
final class GroundTruthAO {
    private let prepare:MTLComputePipelineState,calculate:MTLComputePipelineState,filter:MTLComputePipelineState
    private let raw:MTLTexture,linearDepth:MTLTexture
    let output:MTLTexture
    struct Parameters {
        var inverseProjection:simd_float4x4
        var projectionScale:SIMD2<Float>
        var resolution:SIMD2<UInt32>
        var radius:Float
        var reverseZ:UInt32
        var intensity:Float
        var falloffFraction:Float=0.5
    }
    init(device:MTLDevice,width:Int,height:Int,slices:Int=3,steps:Int=6) throws {
        precondition(slices>0 && steps>0)
        let library=try device.makeLibrary(source:"#define GTAO_SLICES \(slices)\n#define GTAO_STEPS \(steps)\n"+Self.shader,options:nil)
        prepare=try device.makeComputePipelineState(function:library.makeFunction(name:"gtaoPrepare")!)
        calculate=try device.makeComputePipelineState(function:library.makeFunction(name:"gtaoCalculate")!)
        filter=try device.makeComputePipelineState(function:library.makeFunction(name:"gtaoFilter")!)
        let descriptor=MTLTextureDescriptor.texture2DDescriptor(pixelFormat:.r32Float,width:width,height:height,mipmapped:false)
        descriptor.storageMode = .private;descriptor.usage=[.shaderRead,.shaderWrite]
        guard let raw=device.makeTexture(descriptor:descriptor),let output=device.makeTexture(descriptor:descriptor),let linearDepth=device.makeTexture(descriptor:descriptor) else { throw CocoaError(.coderInvalidValue) }
        self.raw=raw;self.output=output;self.linearDepth=linearDepth
        raw.label="GTAO unfiltered visibility";output.label="GTAO filtered visibility"
    }
    func encode(command:MTLCommandBuffer,normals:MTLTexture,depth:MTLTexture,projection:simd_float4x4,reverseZ:Bool,radius:Float=1.6,intensity:Float=0.7,falloffFraction:Float=0.5,stage:Int?=nil) {
        precondition(normals.width==output.width && normals.height==output.height && depth.width==output.width && depth.height==output.height)
        var parameters=Parameters(inverseProjection:projection.inverse,projectionScale:SIMD2(projection[0][0],projection[1][1]),resolution:SIMD2(UInt32(output.width),UInt32(output.height)),radius:radius,reverseZ:reverseZ ? 1:0,intensity:intensity,falloffFraction:falloffFraction)
        precondition(projection[3][3]==0 && projection[2][3] == -1 && projection[2][0]==0 && projection[2][1]==0, "GTAO requires a centered perspective projection")
        for (index,pipeline,target) in [(0,prepare,linearDepth),(1,calculate,raw),(2,filter,output)] {
            let isFilter=index==2
            if let stage,stage != index { continue }
            let encoder=command.makeComputeCommandEncoder()!
            encoder.label=index==0 ? "GTAO linear depth preparation":isFilter ? "GTAO depth-normal spatial filter":"GTAO horizon integral"
            encoder.setComputePipelineState(pipeline)
            encoder.setTexture(normals,index:0);encoder.setTexture(index==0 ? depth:linearDepth,index:1)
            encoder.setTexture(target,index:2)
            if isFilter { encoder.setTexture(raw,index:3) }
            encoder.setBytes(&parameters,length:MemoryLayout<Parameters>.stride,index:0)
            encoder.dispatchThreads(MTLSize(width:output.width,height:output.height,depth:1),threadsPerThreadgroup:MTLSize(width:8,height:8,depth:1))
            encoder.endEncoding()
        }
    }
    private static let shader = """
    #include <metal_stdlib>
    using namespace metal;
    struct Parameters {
        float4x4 inverseProjection;
        float2 projectionScale;
        uint2 resolution;
        float radius;
        uint reverseZ;
        float intensity;
        float falloffFraction;
    };
    float3 viewPosition(uint2 pixel,float depth,constant Parameters &p) {
        float2 uv=(float2(pixel)+0.5)/float2(p.resolution);
        return float3((uv.x*2.0-1.0)*depth/p.projectionScale.x,(1.0-uv.y*2.0)*depth/p.projectionScale.y,-depth);
    }
    bool covered(float4 encoded) { return encoded.a>0.001; }
    float3 mappedNormal(float4 encoded) {
        float3 n=encoded.rgb/max(encoded.a,0.001)*2.0-1.0;
        return n*rsqrt(max(dot(n,n),1e-10));
    }
    kernel void gtaoPrepare(texture2d<float,access::read> normals [[texture(0)]],
        texture2d<float,access::read> depth [[texture(1)]],
        texture2d<float,access::write> result [[texture(2)]],
        constant Parameters &p [[buffer(0)]],uint2 pixel [[thread_position_in_grid]]) {
        if(any(pixel>=p.resolution)) return;
        if(!covered(normals.read(pixel))) { result.write(float4(0),pixel);return; }
        float d=depth.read(pixel).r;
        float z=p.reverseZ ? 1.0-2.0*d:2.0*d-1.0;
        float4 v=p.inverseProjection*float4(0,0,z,1);
        result.write(float4(-v.z/v.w),pixel);
    }
    kernel void gtaoCalculate(texture2d<float,access::read> normals [[texture(0)]],
        texture2d<float,access::read> depth [[texture(1)]],
        texture2d<float,access::write> result [[texture(2)]],
        constant Parameters &p [[buffer(0)]],uint2 pixel [[thread_position_in_grid]]) {
        if(any(pixel>=p.resolution)) return;
        float4 encoded=normals.read(pixel);
        if(!covered(encoded)) { result.write(float4(1),pixel);return; }
        float3 center=viewPosition(pixel,depth.read(pixel).r,p);
        float3 normal=mappedNormal(encoded),view=normalize(-center);
        float pixelRadius=p.radius*abs(p.projectionScale.y)*float(p.resolution.y)/(2.0*max(-center.z,0.01));
        float visibility=0,unoccludedIntegral=0;
        float rotation=fract(52.9829189*fract(dot(float2(pixel),float2(0.06711056,0.00583715))));
        constexpr float pi=3.141592653589793;
        for(uint slice=0;slice<GTAO_SLICES;slice++) {
            float phi=(float(slice)+rotation)*pi/float(GTAO_SLICES);
            float3 direction=float3(cos(phi),sin(phi),0);
            float3 ortho=direction-dot(direction,view)*view;
            float3 axis=normalize(cross(ortho,view));
            float3 projected=normal-axis*dot(normal,axis);
            float projectedLength=length(projected);
            if(projectedLength<1e-5) continue;
            float cosNormal=clamp(dot(projected,view)/projectedLength,0.0,1.0);
            float angle=sign(dot(ortho,projected))*acos(cosNormal);
            float2 lower=cos(float2(angle+pi/2.0,angle-pi/2.0));
            float2 horizon=lower;
            for(uint step=0;step<GTAO_STEPS;step++) {
                float distanceFraction=(float(step)+0.5)/float(GTAO_STEPS);
                float offset=1.0+distanceFraction*distanceFraction*pixelRadius;
                int2 delta=int2(round(float2(cos(phi),-sin(phi))*offset));
                for(uint side=0;side<2;side++) {
                    int2 q=int2(pixel)+(side==0 ? delta:-delta);
                    if(any(q<0)||any(q>=int2(p.resolution))) continue;
                    float sampleDepth=depth.read(uint2(q)).r;
                    if(sampleDepth<=0) continue;
                    float3 samplePosition=viewPosition(uint2(q),sampleDepth,p);
                    // Match the analytic ray reference origin and radius test.
                    float3 difference=samplePosition-(center+normal*0.002);
                    float distance=length(difference);
                    if(distance<1e-5 || distance>=p.radius) continue;
                    float cosine=dot(normalize(difference),view);
                    float falloff=p.falloffFraction<=0 ? 1.0:clamp((p.radius-distance)/(p.radius*p.falloffFraction),0.0,1.0);
                    horizon[side]=max(horizon[side],mix(lower[side],cosine,falloff));
                }
            }
            float h0=-acos(clamp(horizon.y,-1.0,1.0));
            float h1=acos(clamp(horizon.x,-1.0,1.0));
            float integral0=(cosNormal+2.0*h0*sin(angle)-cos(2.0*h0-angle))*0.25;
            float integral1=(cosNormal+2.0*h1*sin(angle)-cos(2.0*h1-angle))*0.25;
            visibility+=projectedLength*(integral0+integral1);
            // Normalize against the same projected hemisphere with no sampled
            // obstruction. Without this, finite slice integration darkens an
            // isolated plane at oblique view angles (analytic fixture failure).
            float base0=angle-pi/2.0,base1=angle+pi/2.0;
            float open0=(cosNormal+2.0*base0*sin(angle)-cos(2.0*base0-angle))*0.25;
            float open1=(cosNormal+2.0*base1*sin(angle)-cos(2.0*base1-angle))*0.25;
            unoccludedIntegral+=projectedLength*(open0+open1);
        }
        visibility=clamp(visibility/max(unoccludedIntegral,1e-6),0.0,1.0);
        result.write(float4(mix(1.0,visibility,p.intensity)),pixel);
    }
    kernel void gtaoFilter(texture2d<float,access::read> normals [[texture(0)]],
        texture2d<float,access::read> depth [[texture(1)]],
        texture2d<float,access::write> result [[texture(2)]],
        texture2d<float,access::read> raw [[texture(3)]],
        constant Parameters &p [[buffer(0)]],uint2 pixel [[thread_position_in_grid]]) {
        if(any(pixel>=p.resolution)) return;
        float4 encoded=normals.read(pixel);
        if(!covered(encoded)) { result.write(float4(1),pixel);return; }
        float3 center=viewPosition(pixel,depth.read(pixel).r,p),normal=mappedNormal(encoded);
        float total=0,weightSum=0;
        for(int y=-2;y<=2;y++) for(int x=-2;x<=2;x++) {
            int2 q=int2(pixel)+int2(x,y);
            if(any(q<0)||any(q>=int2(p.resolution))) continue;
            float4 neighbor=normals.read(uint2(q));
            if(!covered(neighbor)) continue;
            float3 sampleNormal=mappedNormal(neighbor);
            float3 delta=viewPosition(uint2(q),depth.read(uint2(q)).r,p)-center;
            float planeDistance=max(abs(dot(delta,normal)),abs(dot(delta,sampleNormal)));
            float weight=exp(-float(x*x+y*y)*0.25-planeDistance/0.025)*pow(max(dot(normal,sampleNormal),0.0),16.0);
            total+=weight*raw.read(uint2(q)).r;weightSum+=weight;
        }
        result.write(float4(clamp(total/max(weightSum,1e-8),0.0,1.0)),pixel);
    }
    """
}
