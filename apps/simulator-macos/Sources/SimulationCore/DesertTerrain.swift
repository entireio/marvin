import Foundation
import simd

/// A baked heightfield shared by rendering, wheel contact and terrain collision.
/// The settlement stays flat; wind-shaped ridges rise beyond its last compounds.
public enum DesertTerrain {
    public static let extent=768.0, spacing=2.0, townEdge=156.0
    private static let count=Int(extent*2/spacing)+1
    private static func smooth(_ x:Double)->Double { let t=max(0,min(1,x));return t*t*(3-2*t) }
    private static func noise(_ x:Double,_ z:Double)->Double {
        let ix=Int(floor(x)),iz=Int(floor(z)),u=smooth(x-floor(x)),v=smooth(z-floor(z))
        func hash(_ a:Int,_ b:Int)->Double {
            var n=UInt64(bitPattern:Int64(a)) &* 0x9e3779b185ebca87 ^ UInt64(bitPattern:Int64(b)) &* 0xc2b2ae3d27d4eb4f
            n ^= n>>29;n &*= 0x165667b19e3779f9;n ^= n>>32
            return Double(n&0xffffff)/Double(0xffffff)
        }
        return (hash(ix,iz)*(1-u)+hash(ix+1,iz)*u)*(1-v)+(hash(ix,iz+1)*(1-u)+hash(ix+1,iz+1)*u)*v
    }
    private static func sample(_ x:Double,_ z:Double)->Double {
        let edge=max(abs(x),abs(z))
        guard edge>townEdge && edge<extent else { return -0.025 }
        let apron=smooth((edge-townEdge)/30)
        // Fade out only at the distant map perimeter, beyond the fog horizon.
        let perimeter=smooth((extent-edge)/96)
        let warp=15*(noise(x/90,z/90)-0.5)+7*sin(z*0.019)
        let phase=(x*0.94+z*0.34+warp)/64
        let u=phase-floor(phase),crest=0.67
        let ridge=u<crest ? smooth(u/crest):1-smooth((u-crest)/(1-crest))
        let amplitude=6.0+4.0*noise(x/127+8,z/113-3)
        let swell=1.8*pow(0.5+0.5*sin((x*0.3-z*0.95)/29+noise(x/93,z/101)*2),2)
        return -0.025+0.8*apron*perimeter*(amplitude*ridge+swell)
    }
    private static let samples:[Double] = {
        var result=[Double]();result.reserveCapacity(count*count)
        for z in 0..<count { for x in 0..<count {
            result.append(sample(Double(x)*spacing-extent,Double(z)*spacing-extent))
        }}
        return result
    }()
    public static func vertexHeight(x:Double,z:Double)->Double {
        let ix=max(0,min(count-1,Int(((x+extent)/spacing).rounded())))
        let iz=max(0,min(count-1,Int(((z+extent)/spacing).rounded())))
        return samples[iz*count+ix]
    }
    /// Same diagonal and barycentric interpolation as the two rendered triangles.
    public static func height(x:Double,z:Double)->Double {
        guard max(abs(x),abs(z))>townEdge-2,max(abs(x),abs(z))<extent else { return -0.025 }
        let gx=(x+extent)/spacing,gz=(z+extent)/spacing
        let ix=Int(floor(gx)),iz=Int(floor(gz)),u=gx-Double(ix),v=gz-Double(iz)
        let a=samples[iz*count+ix],b=samples[iz*count+ix+1]
        let c=samples[(iz+1)*count+ix],d=samples[(iz+1)*count+ix+1]
        return u+v<=1 ? a+(b-a)*u+(c-a)*v:d+(c-d)*(1-u)+(b-d)*(1-v)
    }
    public static func gradient(x:Double,z:Double)->SIMD2<Double> {
        let e=0.3
        return SIMD2((height(x:x+e,z:z)-height(x:x-e,z:z))/(2*e),
                     (height(x:x,z:z+e)-height(x:x,z:z-e))/(2*e))
    }
}
