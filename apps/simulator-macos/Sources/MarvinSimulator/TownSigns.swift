import AppKit
import SceneKit

/// Physical, bounded sign plates. Text is rasterized once, centered using actual
/// font metrics, and mipmapped; no per-frame text layout or floating billboards.
final class TownSigns {
    private(set) var count=0
    private(set) var valid=true
    private var textures:[String:NSImage]=[:]
    func plate(_ title:String,eyebrow:String,footer:String,badge:String,
               at position:SCNVector3,width:CGFloat,height:CGFloat,yaw:CGFloat=0,
               accent:UInt32=0xb98450,into root:SCNNode) {
        let node=SCNNode();node.name="Sign · \(title)";node.position=position;node.eulerAngles.y=yaw
        let frame=SCNBox(width:width+0.045,height:height+0.045,length:0.055,chamferRadius:0.012)
        let metal=SCNMaterial();metal.lightingModel = .physicallyBased
        metal.diffuse.contents=NSColor(calibratedWhite:0.17,alpha:1);metal.metalness.contents=0.55;metal.roughness.contents=0.65
        frame.materials=[metal];let backing=SCNNode(geometry:frame);backing.castsShadow=false;node.addChildNode(backing)
        let plane=SCNPlane(width:width,height:height)
        let material=SCNMaterial();material.lightingModel = .physicallyBased
        let key="\(title)|\(eyebrow)|\(footer)|\(badge)|\(accent)|\(width/height)"
        if textures[key]==nil { textures[key]=image(title,eyebrow:eyebrow,footer:footer,badge:badge,aspect:width/height,accent:accent) }
        material.diffuse.contents=textures[key];material.diffuse.mipFilter = .linear;material.diffuse.maxAnisotropy=8
        material.roughness.contents=0.82
        // A small self-lit contribution keeps signs legible under market awnings.
        material.emission.contents=textures[key];material.emission.intensity=0.12
        plane.materials=[material];let face=SCNNode(geometry:plane);face.position.z=0.029;face.castsShadow=false
        node.addChildNode(face);root.addChildNode(node);count += 1
        valid = valid && width>0 && height>0 && width/height>2 && width/height<9
    }
    private func image(_ title:String,eyebrow:String,footer:String,badge:String,aspect:CGFloat,accent:UInt32)->NSImage {
        let w=1024,h=Int((1024/aspect).rounded())
        let bitmap=NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:w,pixelsHigh:h,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:w*4,bitsPerPixel:32)!
        NSGraphicsContext.saveGraphicsState();NSGraphicsContext.current=NSGraphicsContext(bitmapImageRep:bitmap)
        let W=CGFloat(w),H=CGFloat(h)
        func color(_ rgb:UInt32)->NSColor { NSColor(calibratedRed:CGFloat((rgb>>16)&255)/255,green:CGFloat((rgb>>8)&255)/255,blue:CGFloat(rgb&255)/255,alpha:1) }
        color(0x263c3c).setFill();NSRect(x:0,y:0,width:W,height:H).fill()
        color(accent).setFill();NSRect(x:0,y:H-10,width:W,height:10).fill()
        color(0xb8aa8c).withAlphaComponent(0.45).setStroke()
        let outline=NSBezierPath(rect:NSRect(x:12,y:12,width:W-24,height:H-31));outline.lineWidth=2;outline.stroke()
        let badgeW=H*0.55, left=badgeW+H*0.15, right=W-H*0.10
        color(accent).setFill();NSRect(x:H*0.10,y:H*0.25,width:badgeW,height:H*0.49).fill()
        func text(_ value:String,in rect:NSRect,size:CGFloat,ink:UInt32,weight:NSFont.Weight,tracking:CGFloat=0) {
            let paragraph=NSMutableParagraphStyle();paragraph.alignment = .center;paragraph.lineBreakMode = .byClipping
            var size=size
            func attributes()->[NSAttributedString.Key:Any] {
                [.font:NSFont(name:weight == .bold ? "AvenirNextCondensed-DemiBold":"AvenirNextCondensed-Medium",size:size) ?? NSFont.systemFont(ofSize:size,weight:weight),.foregroundColor:color(ink),.paragraphStyle:paragraph,.kern:tracking]
            }
            while size>8 {
                let measured=NSAttributedString(string:value,attributes:attributes()).size()
                if measured.width<=rect.width && measured.height<=rect.height { break }
                size -= 1
            }
            let string=NSAttributedString(string:value,attributes:attributes()),measured=string.size()
            if measured.width>rect.width+1 || measured.height>rect.height+2 {
                print("Sign text overflow: \(value), measured \(measured), available \(rect.size)")
                valid=false
            }
            string.draw(in:NSRect(x:rect.minX,y:rect.midY-measured.height/2,width:rect.width,height:measured.height+1))
        }
        text(badge,in:NSRect(x:H*0.10,y:H*0.25,width:badgeW,height:H*0.49),size:H*0.30,ink:0x263c3c,weight:.bold)
        let x=left+H*0.08,tw=right-x
        text(eyebrow,in:NSRect(x:x,y:H*0.73,width:tw,height:H*0.16),size:H*0.105,ink:0xc2b69c,weight:.medium,tracking:2.6)
        text(title,in:NSRect(x:x,y:H*0.29,width:tw,height:H*0.43),size:H*0.34,ink:0xf1e1bc,weight:.bold,tracking:1.5)
        color(accent).withAlphaComponent(0.70).setFill();NSRect(x:x+tw*0.1,y:H*0.28,width:tw*0.8,height:1.5).fill()
        text(footer,in:NSRect(x:x,y:H*0.09,width:tw,height:H*0.17),size:H*0.105,ink:0xc2b69c,weight:.medium,tracking:1.8)
        // Restrained enamel wear; lettering remains readable.
        color(0xd9ccb0).withAlphaComponent(0.08).setFill()
        for i in 0..<65 {
            NSRect(x:CGFloat((i*179+23)%w),y:CGFloat((i*43+17)%h),width:CGFloat(1+i%6),height:1).fill()
        }
        NSGraphicsContext.restoreGraphicsState()
        let image=NSImage(size:NSSize(width:w,height:h));image.addRepresentation(bitmap);return image
    }
}
