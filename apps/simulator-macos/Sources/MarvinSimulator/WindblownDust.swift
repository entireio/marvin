import AppKit
import SceneKit

/// Irregular density rather than a radial white sprite. Geometry colors are
/// explicitly consumed: SceneKit's constant material does not apply them for us.
enum WindblownDust {
    static func configure(_ material:SCNMaterial) {
        material.shaderModifiers=[.geometry:"""
        #pragma varyings
        float4 dustTint;
        #pragma body
        out.dustTint=_geometry.color;
        """,.surface:"""
        #pragma body
        _surface.diffuse.rgb *= pow(max(in.dustTint.rgb,float3(0.0)),float3(2.2));
        """,.fragment:"""
        #pragma transparent
        #pragma body
        float opacity=_surface.diffuse.a*in.dustTint.a;
        _output.color.rgb *= opacity;
        _output.color.a=opacity;
        """]
    }
    static func texture()->NSImage {
        let size=128
        let bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:size,pixelsHigh:size,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:size*4,bitsPerPixel:32)!
        let bytes=bitmap.bitmapData!
        for y in 0..<size { for x in 0..<size {
            let u=Double(x)/Double(size-1),v=Double(y)/Double(size-1)
            let broad=CityMaterials.surfaceNoise(u,v,cells:5,seed:71)
            let fine=CityMaterials.surfaceNoise(u,v,cells:17,seed:29)
            let envelope=pow(max(0,sin(u * .pi)*sin(v * .pi)),2)
            let density=envelope*max(0,min(1,(broad*0.7+fine*0.3-0.22)*1.6))
            let i=(y*size+x)*4
            bytes[i]=255;bytes[i+1]=255;bytes[i+2]=255;bytes[i+3]=UInt8(density*255)
        }}
        let image=NSImage(size:NSSize(width:size,height:size));image.addRepresentation(bitmap);return image
    }
}
