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
            #pragma body
            float3 d=normalize(in.skyDirection);
            float h=pow(clamp(d.y,0.0,1.0),0.42);
            float3 sky=mix(horizon,zenith,h);
            float a=acos(clamp(dot(d,sunA),-1.0,1.0));
            float b=acos(clamp(dot(d,sunB),-1.0,1.0));
            // Forward scattering is confined to each actual sun direction.
            sky+=tintA*(0.13*exp(-a*a/0.020)+0.16*exp(-a/0.022));
            sky+=tintB*(0.065*exp(-b*b/0.014)+0.10*exp(-b/0.018));
            float discA=1.0-smoothstep(sunRadii.x*0.97,sunRadii.x,a);
            float discB=1.0-smoothstep(sunRadii.y*0.97,sunRadii.y,b);
            sky=mix(sky,tintA*5.0,discA);
            sky=mix(sky,tintB*3.4,discB);
            // This is an infinitely distant sky: bypass scene distance fog.
            _output.color=float4(sky,1.0);
            """]
        sphere.materials=[skyMaterial];dome.geometry=sphere;dome.castsShadow=false
        dome.renderingOrder = -10000;root.addChildNode(dome)
        ambient.light=SCNLight();ambient.light?.type = .ambient;root.addChildNode(ambient)
        for (i,sun) in suns.enumerated() {
            sun.name="Sun \(i+1)";sun.light=SCNLight();sun.light?.type = .directional
            // Forward shadows attenuate each star separately. The companion
            // lights the other star's shadow instead of painting two dark decals.
            sun.light?.castsShadow=true;sun.light?.shadowMode = .forward
            let resolution=i==0 ? 2048:1024
            sun.light?.shadowMapSize=CGSize(width:resolution,height:resolution)
            sun.light?.orthographicScale=58;sun.light?.shadowRadius=i==0 ? 3:2
            sun.light?.shadowSampleCount=4;sun.light?.shadowColor=NSColor.black
            sun.light?.shadowBias=0.6;root.addChildNode(sun)
        }
        scene.rootNode.addChildNode(root)
        apply(daylight)
    }
    func attach(camera:SCNNode) {
        dome.constraints=[SCNTransformConstraint.positionConstraint(inWorldSpace:true) { [weak camera] _,_ in camera?.presentation.worldPosition ?? SCNVector3Zero }]
        guard let lens=camera.camera else { return }
        lens.wantsHDR=true; lens.wantsExposureAdaptation=false
        // Fixed exposure avoids pumping when a tiny sun enters/leaves frame.
        lens.exposureOffset = -0.25; lens.bloomIntensity=0.16
        lens.bloomThreshold=1.6;lens.bloomBlurRadius=7
    }
    func apply(_ value:BinaryDaylight) {
        daylight=value
        skyMaterial.setValue(NSValue(point:NSPoint(x:value.radii[0],y:value.radii[1])),forKey:"sunRadii")
        let elevation=max(0,value.directions.map{$0.y}.max()!)
        let day=min(1,elevation/0.55)
        let zenith=SIMD3<Float>(0.23,0.32,0.46)+(SIMD3<Float>(0.32,0.49,0.67)-SIMD3<Float>(0.23,0.32,0.46))*Float(day)
        let horizon=SIMD3<Float>(0.73,0.39,0.23)+(SIMD3<Float>(0.73,0.76,0.73)-SIMD3<Float>(0.73,0.39,0.23))*Float(day)
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
            suns[i].light?.intensity=i==0 ? 1120:340
            skyMaterial.setValue(NSValue(scnVector3:SCNVector3(d)),forKey:i==0 ? "sunA":"sunB")
            skyMaterial.setValue(NSValue(scnVector3:SCNVector3(tint)),forKey:i==0 ? "tintA":"tintB")
        }
        ambient.light?.color=ink(zenith);ambient.light?.intensity=110+80*day
        scene?.fogColor=ink(horizon);scene?.fogStartDistance=115;scene?.fogEndDistance=240
        // A diffuse sky probe has no baked sun at the old lighting direction.
        // Directional lights supply the correctly positioned specular highlights.
        scene?.lightingEnvironment.contents=skyProbe(zenith:zenith,horizon:horizon)
        scene?.lightingEnvironment.intensity=0.45+0.18*day
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
