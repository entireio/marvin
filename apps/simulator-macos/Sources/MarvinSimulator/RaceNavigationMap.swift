import AppKit
import SimulationCore
import simd

extension RaceHUD {
    /// Rasterize static town/terrain once, rather than redrawing hundreds of buildings
    /// or sampling dune heights on every HUD frame.
    func configureNavigationMap(town:TownWorld) {
        guard townMapImage == nil else { return }
        let image=NSImage(size:NSSize(width:336,height:336))
        image.lockFocusFlipped(true)
        let scale=1.0
        func point(_ p:SIMD2<Double>)->NSPoint { NSPoint(x:168-p.x*scale,y:168-p.y*scale) }
        color(0xb49a71,alpha:0.7).setFill()
        for corners in town.mapBuildings {
            let path=NSBezierPath()
            for (i,p) in corners.enumerated() { if i==0 { path.move(to:point(p)) } else { path.line(to:point(p)) } }
            path.close();path.fill()
        }
        color(0xe4c899,alpha:0.8).setStroke()
        for street in town.mapStreets {
            let path=NSBezierPath();path.lineWidth=1.5;path.lineJoinStyle = .round
            for (i,p) in street.enumerated() { if i==0 { path.move(to:point(p)) } else { path.line(to:point(p)) } }
            path.stroke()
        }
        let course=NSBezierPath()
        for i in 0...160 {
            let p=DirtCourse.point(Double(i)*2 * .pi/160),q=point(SIMD2(p.x,p.z))
            if i==0 { course.move(to:q) } else { course.line(to:q) }
        }
        color(0xf1a477).setStroke();course.lineWidth=2.5;course.stroke()
        image.unlockFocus();townMapImage=image

        let n=384,bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:n,pixelsHigh:n,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:n*4,bitsPerPixel:32)!
        let data=bitmap.bitmapData!
        for y in 0..<n { for x in 0..<n {
            let px=(Double(n)/2-Double(x))*4,pz=(Double(n)/2-Double(y))*4
            let h=DesertTerrain.height(x:px,z:pz)
            let band=0.78+0.14*min(1,max(0,h/9))-(abs(h.rounded()-h)<0.09 && h>0.2 ? 0.08:0)
            let i=y*bitmap.bytesPerRow+x*4
            data[i]=UInt8(110*band);data[i+1]=UInt8(98*band);data[i+2]=UInt8(70*band);data[i+3]=255
        }}
        let terrain=NSImage(size:NSSize(width:n,height:n));terrain.addRepresentation(bitmap);planetMapImage=terrain
    }
    func drawNavigationMap(in map:NSRect) {
        guard mapRegion != .course else { drawCourseMap(in:map);return }
        NSGraphicsContext.saveGraphicsState();defer { NSGraphicsContext.restoreGraphicsState() }
        NSBezierPath(rect:map).addClip()
        let radius=mapRegion == .town ? max(150,max(abs(x),abs(z))+14):max(240,max(abs(x),abs(z))*1.2)
        let scale=Double(map.height)/(radius*2)
        func point(_ px:Double,_ pz:Double)->NSPoint { NSPoint(x:map.midX-px*scale,y:map.midY-pz*scale) }
        func image(_ image:NSImage?,halfWidth:Double,halfHeight:Double) {
            let corner=point(halfWidth,halfHeight)
            image?.draw(in:NSRect(x:corner.x,y:corner.y,width:halfWidth*2*scale,height:halfHeight*2*scale),from:.zero,operation:.sourceOver,fraction:1,respectFlipped:true,hints:nil)
        }
        if mapRegion == .dunes {
            image(planetMapImage,halfWidth:768,halfHeight:768)
            let outline=NSBezierPath()
            for i in 0...128 {
                let a=Double(i)*2 * .pi/128,r=TownFootprint.radius(at:a)
                let p=point(cos(a)*r,sin(a)*r)
                if i==0 { outline.move(to:p) } else { outline.line(to:p) }
            }
            outline.close();color(0x253d38,alpha:0.65).setFill();outline.fill()
        }
        image(townMapImage,halfWidth:168,halfHeight:168)
        if mapRegion == .dunes {
            let line=NSBezierPath();line.move(to:point(0,0));line.line(to:point(x,z));line.setLineDash([3,3],count:2,phase:0)
            color(0xffdf9d,alpha:0.65).setStroke();line.lineWidth=1;line.stroke()
            let town=point(0,0)
            text("MOS ASTER",town.x+7,town.y+7,9,true)
            text(String(format:"TOWN  %.0f m",hypot(x,z)),map.minX+5,map.maxY-16,10,true)
        }
        func marker(_ px:Double,_ pz:Double,_ tint:UInt32,_ player:Bool=false) {
            let p=point(px,pz),r:CGFloat=player ? 4:2.5
            guard map.insetBy(dx:r,dy:r).contains(p) else { return }
            color(0x14251e).setFill();NSBezierPath(ovalIn:NSRect(x:p.x-r-2,y:p.y-r-2,width:(r+2)*2,height:(r+2)*2)).fill()
            color(tint).setFill();NSBezierPath(ovalIn:NSRect(x:p.x-r,y:p.y-r,width:r*2,height:r*2)).fill()
            if player {
                color(0xffffff).setStroke();let ring=NSBezierPath(ovalIn:NSRect(x:p.x-r-1,y:p.y-r-1,width:(r+1)*2,height:(r+1)*2));ring.lineWidth=1;ring.stroke()
                let arrow=NSBezierPath();arrow.move(to:p);arrow.line(to:NSPoint(x:p.x-sin(heading)*11,y:p.y-cos(heading)*11));arrow.lineWidth=2;arrow.stroke()
            }
        }
        marker(x,z,playerColor,true)
    }
}
