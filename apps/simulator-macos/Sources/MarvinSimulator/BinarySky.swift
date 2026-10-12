import AppKit
import SceneKit
import simd

/// A fictional circumbinary world: a 0.20 AU binary seen from ~1 AU,
/// coplanar with the planet's orbit, at 25 degrees north on an equinox.
/// Binary phase is fixed over a short race; planet rotation supplies time of day.
struct BinaryDaylight {
    let fraction: Double
    let phase: Double
    let directions: [SIMD3<Double>]
    let radii = [0.55,0.38].map { $0 * Double.pi/180 }
    init(fraction:Double,phase:Double) {
        self.fraction=max(0.015,min(0.985,fraction));self.phase=phase
        let offsets=[atan2(-0.06*sin(phase),1+0.06*cos(phase)),atan2(0.14*sin(phase),1-0.14*cos(phase))]
        // Shared daylight: both stellar centers are above the horizon.
        let first=max(-Double.pi/2-offsets[0],-Double.pi/2-offsets[1])
        let last=min(Double.pi/2-offsets[0],Double.pi/2-offsets[1])
        let h=first+(last-first)*self.fraction,latitude=25*Double.pi/180
        directions=offsets.map { d in SIMD3(-sin(h+d),cos(latitude)*cos(h+d),-sin(latitude)*cos(h+d)) }
    }
    static func random()->Self {
        // Avoid conjunction/eclipses for this race setting: always show a pair.
        let phase=Double.random(in:0.35...(Double.pi-0.35)) + (Bool.random() ? Double.pi:0)
        return Self(fraction:Double.random(in:0.015...0.985),phase:phase)
    }
    var separationDegrees:Double { acos(max(-1,min(1,simd_dot(directions[0],directions[1]))))*180/Double.pi }
}

final class BinarySky {
    private var stormActive=false
    func setStorm(_ enabled:Bool) { stormActive=enabled;apply(daylight) }
    let root=SCNNode()
    private let dome=SCNNode()
    private let skyMaterial=SCNMaterial()
    private let ambient=SCNNode()
    let suns=[SCNNode(),SCNNode()]
    private(set) var daylight=BinaryDaylight(fraction:0.5,phase:1.2)
    private weak var scene:SCNScene?

    init(scene:SCNScene) {
        self.scene=scene;root.name="Binary daylight"
        let sphere=SCNSphere(radius:220);sphere.segmentCount=48
        skyMaterial.lightingModel = .constant;skyMaterial.cullMode = .front
        skyMaterial.readsFromDepthBuffer=false;skyMaterial.writesToDepthBuffer=false
        skyMaterial.shaderModifiers = [
            .geometry:"""
            #pragma varyings
            float3 skyDirection;
            #pragma body
            out.skyDirection = _geometry.position.xyz;
            """,
            .fragment:"""
            #pragma arguments
            float3 sunA;
            float3 sunB;
            float3 zenith;
            float3 horizon;
            float3 tintA;
            float3 tintB;
            float2 sunRadii;
            float daylight;
            float3 duskBand;
            float storm;
            float3 stormTint;
            #pragma body
            float3 d=normalize(in.skyDirection);
            float height=max(d.y,0.0);
            float h=1.0-exp(-height/mix(0.085,0.35,daylight));
            float3 sky=mix(horizon,zenith,h);
            float3 solarAxis=normalize(sunA+sunB);
            float facing=pow(max(0.0,dot(d.xz,solarAxis.xz)/(max(length(d.xz),0.00001)*max(length(solarAxis.xz),0.00001))),3.0);
            // A narrow warm horizon beneath cool upper air, with a dusty
            // rose transition at dusk. Scattering is strongest toward the suns.
            float band=exp(-pow((height-0.12)/0.10,2.0));
            sky+=duskBand*band*(1.0-daylight)*(0.35+0.65*facing);
            sky+=float3(0.34,0.095,0.018)*facing*exp(-height/0.15)*(1.0-daylight);
            float a=acos(clamp(dot(d,sunA),-1.0,1.0));
            float b=acos(clamp(dot(d,sunB),-1.0,1.0));
            float haze=mix(1.0,0.35,daylight);
            sky+=tintA*haze*(0.5*exp(-a*a/0.006)+0.65*exp(-a/0.017));
            sky+=tintB*haze*(0.3*exp(-b*b/0.004)+0.4*exp(-b/0.013));
            float discA=1.0-smoothstep(sunRadii.x*0.91,sunRadii.x*1.05,a);
            float discB=1.0-smoothstep(sunRadii.y*0.91,sunRadii.y*1.05,b);
            float limbA=sqrt(max(0.0,1.0-pow(a/sunRadii.x,2.0)));
            float limbB=sqrt(max(0.0,1.0-pow(b/sunRadii.y,2.0)));
            sky=mix(sky,tintA*(3.8+2.4*limbA),discA);
            sky=mix(sky,tintB*(2.5+1.5*limbB),discB);
            // This is an infinitely distant sky: bypass scene distance fog.
            _output.color=float4(mix(sky,stormTint+sky*0.025,storm*0.97),1.0);
            """]
        sphere.materials=[skyMaterial];dome.geometry=sphere;dome.castsShadow=false
        dome.renderingOrder = -10000;root.addChildNode(dome)
        ambient.light=SCNLight();ambient.light?.type = .ambient;root.addChildNode(ambient)
        for (i,sun) in suns.enumerated() {
            sun.name="Sun \(i+1)";sun.light=SCNLight();sun.light?.type = .directional
            // Forward shadows attenuate each star separately. The companion
            // lights the other star's shadow instead of painting two dark decals.
            sun.light?.castsShadow=true;sun.light?.shadowMode = .forward
            let resolution=i==0 ? 4096:2048
            sun.light?.shadowMapSize=CGSize(width:resolution,height:resolution)
            sun.light?.automaticallyAdjustsShadowProjection=false
            sun.light?.sampleDistributedShadowMaps=false
            sun.light?.forcesBackFaceCasters=false
            sun.light?.zNear=0.1;sun.light?.zFar=220
            sun.light?.maximumShadowDistance=500
            sun.light?.orthographicScale=58;sun.light?.shadowRadius=i==0 ? 3:2
            sun.light?.shadowSampleCount=8;sun.light?.shadowColor=NSColor.black
            sun.light?.shadowBias=0.6;root.addChildNode(sun)
        }
        scene.rootNode.addChildNode(root)
        apply(daylight)
    }
    /// World-anchored maps are independent of the viewing camera. Only exploration
    /// moves their footprint, in light-space texel increments to avoid shimmer.
    func updateShadowCenter(_ position:SIMD3<Double>) {
        let center=max(abs(position.x),abs(position.z))<28 ? SIMD3<Double>.zero:position
        for sun in suns {
            let transform=sun.simdWorldTransform
            let right=SIMD3<Double>(Double(transform.columns.0.x),Double(transform.columns.0.y),Double(transform.columns.0.z))
            let up=SIMD3<Double>(Double(transform.columns.1.x),Double(transform.columns.1.y),Double(transform.columns.1.z))
            let back=SIMD3<Double>(Double(transform.columns.2.x),Double(transform.columns.2.y),Double(transform.columns.2.z))
            let texel=116/Double(sun.light!.shadowMapSize.width)
            func snap(_ value:Double)->Double { (value/texel).rounded()*texel }
            let anchor=right*snap(simd_dot(center,right))+up*snap(simd_dot(center,up))+back*snap(simd_dot(center,back))
            sun.position=SCNVector3(anchor+back*80)
        }
    }
    func attach(camera:SCNNode) {
        dome.constraints=[SCNTransformConstraint.positionConstraint(inWorldSpace:true) { [weak camera] _,_ in camera?.presentation.worldPosition ?? SCNVector3Zero }]
        guard let lens=camera.camera else { return }
        lens.wantsHDR=true; lens.wantsExposureAdaptation=false
        // Fixed exposure avoids pumping when a tiny sun enters/leaves frame.
        lens.exposureOffset = 0; lens.bloomIntensity=0.38
        lens.bloomThreshold=1.2;lens.bloomBlurRadius=12
    }
    func apply(_ value:BinaryDaylight) {
        daylight=value
        skyMaterial.setValue(Float(stormActive ? 1:0),forKey:"storm")
        skyMaterial.setValue(NSValue(scnVector3:SCNVector3(0.46,0.29,0.14)),forKey:"stormTint")
        skyMaterial.setValue(NSValue(point:NSPoint(x:value.radii[0],y:value.radii[1])),forKey:"sunRadii")
        let elevation=max(0,value.directions.map{$0.y}.max()!)
        let day=min(1,elevation/0.55)
        let evening=Float(value.fraction>0.5 ? 1:0)
        let lowZenith=SIMD3<Float>(0.018,0.070,0.17)*(1-evening)+SIMD3<Float>(0.065,0.027,0.10)*evening
        let lowHorizon=SIMD3<Float>(0.95,0.36,0.09)*(1-evening)+SIMD3<Float>(0.85,0.19,0.035)*evening
        let zenith=lowZenith+(SIMD3<Float>(0.20,0.38,0.62)-lowZenith)*Float(day)
        let horizon=lowHorizon+(SIMD3<Float>(0.65,0.72,0.75)-lowHorizon)*Float(day)
        skyMaterial.setValue(Float(day),forKey:"daylight")
        let band=SIMD3<Float>(0.04,0.055,0.06)*(1-evening)+SIMD3<Float>(0.22,0.045,0.085)*evening
        skyMaterial.setValue(NSValue(scnVector3:SCNVector3(band)),forKey:"duskBand")
        func ink(_ p:SIMD3<Float>)->NSColor { NSColor(calibratedRed:CGFloat(p.x),green:CGFloat(p.y),blue:CGFloat(p.z),alpha:1) }
        for (key,v) in [("zenith",zenith),("horizon",horizon)] { skyMaterial.setValue(NSValue(scnVector3:SCNVector3(v)),forKey:key) }
        for i in 0..<2 {
            let d=value.directions[i],height=max(0,d.y)
            // Kasten-style air mass approximation; Beer-Lambert extinction
            // removes more blue than red along a long horizon path.
            let altitude=asin(height)*180/Double.pi
            let mass=1/(height+0.50572*pow(altitude+6.07995,-1.6364))
            let attenuation=SIMD3(exp(-0.035*mass),exp(-0.070*mass),exp(-0.15*mass))
            let intrinsic=i==0 ? SIMD3<Double>(1,0.94,0.83):SIMD3<Double>(1,0.73,0.46)
            let rgb=intrinsic*attenuation
            let tint=SIMD3<Float>(Float(rgb.x),Float(rgb.y),Float(rgb.z))
            suns[i].position=SCNVector3(d*80)
            suns[i].look(at:SCNVector3Zero,up:SCNVector3(0,1,0),localFront:SCNVector3(0,0,-1))
            suns[i].light?.color=ink(tint)
            suns[i].light?.intensity=(i==0 ? 1550:470)*(stormActive ? 0.20:1)
            skyMaterial.setValue(NSValue(scnVector3:SCNVector3(d)),forKey:i==0 ? "sunA":"sunB")
            skyMaterial.setValue(NSValue(scnVector3:SCNVector3(tint)),forKey:i==0 ? "tintA":"tintB")
        }
        // Open-sky fill keeps racing surfaces readable in backlight. It is
        // deliberately weaker than direct light, not a camera-facing key light.
        ambient.light?.color=ink(SIMD3<Float>(0.54,0.62,0.78));ambient.light?.intensity=260-70*day
        scene?.fogColor=ink(horizon);scene?.fogStartDistance=115;scene?.fogEndDistance=240
        // A diffuse sky probe has no baked sun at the old lighting direction.
        // Directional lights supply the correctly positioned specular highlights.
        scene?.lightingEnvironment.contents=stormActive ? skyProbe(zenith:SIMD3(0.46,0.34,0.23),horizon:SIMD3(0.68,0.50,0.32)):skyProbe(zenith:zenith,horizon:horizon)
        scene?.lightingEnvironment.intensity=stormActive ? 0.75:0.95-0.20*day
        if stormActive {
            scene?.fogColor=color(0xae865b);scene?.fogStartDistance=3;scene?.fogEndDistance=65
            ambient.light?.color=color(0xd4b48a);ambient.light?.intensity=320
        }
    }
    private func skyProbe(zenith:SIMD3<Float>,horizon:SIMD3<Float>)->NSImage {
        let w=64,h=32
        let bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:w,pixelsHigh:h,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:w*4,bitsPerPixel:32)!
        for y in 0..<h {
            let t=Float(pow(max(0,cos(Double(y)/Double(h-1)*Double.pi)),0.42))
            let rgb=(horizon*(1-t)+zenith*t)*(y>h/2 ? 0.55:1)
            for x in 0..<w { let j=(y*w+x)*4;for c in 0..<3 { bitmap.bitmapData![j+c]=UInt8(max(0,min(255,rgb[c]*255))) };bitmap.bitmapData![j+3]=255 }
        }
        let image=NSImage(size:NSSize(width:w,height:h));image.addRepresentation(bitmap);return image
    }
}
