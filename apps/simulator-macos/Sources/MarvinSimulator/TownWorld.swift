import AppKit
import SceneKit
import SimulationCore
import simd

/// A compact, deterministic desert port. Static geometry is baked into spatial
/// cells with two detail levels; the race never acquires scenery physics bodies.
final class TownWorld {
    let root = SCNNode()
    private let surface = CityMaterials.plaster
    private let crowd = TownCrowd()
    private let signs = TownSigns()
    private var storefrontSigns=0
    private var cells: [String: TownCell] = [:]
    private var people: [TownPerson] = []
    private(set) var buildings = 0
    private(set) var population = 0
    private(set) var triangleCount = 0
    private(set) var coarseTriangles = 0
    private(set) var animatedCount = 0
    private(set) var lots: [TownLot] = []
    private var cameraBounds: [(SIMD3<Double>,SIMD3<Double>)] = []
    private var clock = 0.0
    private var visibilityClock = -1.0
    private let sand: UInt32 = 0xc5a174, cream: UInt32 = 0xe6c89c
    private let rust: UInt32 = 0xa25a40, teal: UInt32 = 0x427c80
    private let dark: UInt32 = 0x3e4545, trim: UInt32 = 0x9d805e
    private let finishZ = DirtCourse.point(0).z

    struct TownLot {
        let x: Double, z: Double, width: Double, depth: Double
    }
    private final class TownCell {
        let near = TownMesh(), far = TownMesh()
        let origin: SIMD3<Float>
        init(_ x: Float, _ z: Float) { origin = SIMD3(x, 0, z) }
    }
    private struct TownPerson {
        let node: SCNNode, origin: SCNVector3
        let phase: Double
    }

    init() {
        root.name = "Mos Aster desert spaceport"
        buildRoads()
        buildGrandstand()
        buildSettlement()
        buildRepairPit()
        buildLandmarks()
        buildStreetLife()
        buildMarketDetails()
        buildWayfinding()
        crowd.finish(into:root)
        for key in cells.keys.sorted() {
            let cell = cells[key]!
            let near = cell.near.geometry(material: surface, relativeTo: cell.origin)
            let far = cell.far.geometry(material: surface, relativeTo: cell.origin)
            near.levelsOfDetail = [SCNLevelOfDetail(geometry: far, worldSpaceDistance: 82)]
            let node = SCNNode(geometry: near)
            node.simdPosition = cell.origin; node.name = "Town cell \(key)"
            // Keep expensive shadow submissions bounded. Architecture gets
            // vertex-toned recesses; the sun still gives near walls contact.
            node.castsShadow = abs(cell.origin.x) < 48 && abs(cell.origin.z) < 48
            root.addChildNode(node)
            triangleCount += cell.near.indices.count / 3
            coarseTriangles += cell.far.indices.count / 3
        }
    }

    private func cell(_ x: Double, _ z: Double) -> TownCell {
        let span = max(abs(x),abs(z)) < 48 ? 16.0 : 32.0
        let ix = Int(floor(x / span)), iz = Int(floor(z / span)), key = "\(Int(span)):\(ix),\(iz)"
        if let existing = cells[key] { return existing }
        let value = TownCell(Float(Double(ix)*span+span/2), Float(Double(iz)*span+span/2))
        cells[key] = value; return value
    }
    private func paint(_ x: Double, _ z: Double, yaw: Double = 0) -> TownPainter {
        let c = cell(x,z)
        return TownPainter(near: c.near, far: c.far, origin: SIMD3(Float(x),0,Float(z)), yaw: Float(yaw))
    }
    /// Entire footprint plus a margin must clear every part of the spline.
    /// Dense edge/interior samples also prevent a lot spanning another branch.
    private func clearLot(_ x: Double, _ z: Double, _ w: Double, _ d: Double) -> Bool {
        if abs(x)-w/2 > 22 || abs(z)-d/2 > 22 { return true }
        let nx = Int(ceil(w / 0.35)), nz = Int(ceil(d / 0.35))
        for i in 0...nx { for j in 0...nz {
            let px = x-w/2+w*Double(i)/Double(nx), pz = z-d/2+d*Double(j)/Double(nz)
            if DirtCourse.projection(x:px,z:pz).distance < DirtCourse.terrainEdge+0.6 { return false }
        }}
        return true
    }

    private struct Street {
        let points:[SIMD2<Double>]
        let width:Double
    }
    // Destination-led arteries: market approach, dock access and northern trade
    // route. None forms a closed perimeter around the race.
    private let streets:[Street] = [
        Street(points:[SIMD2(-150,-76),SIMD2(-64,-39),SIMD2(-35,-29),SIMD2(-15,-27),SIMD2(0,-26),SIMD2(18,-31),SIMD2(45,-45),SIMD2(145,-68)],width:3.8),
        Street(points:[SIMD2(45,-45),SIMD2(35,-25),SIMD2(28,-10),SIMD2(33,3),SIMD2(26,14)],width:4.2),
        Street(points:[SIMD2(26,14),SIMD2(36,30),SIMD2(18,36),SIMD2(-8,32),SIMD2(-30,39),SIMD2(-64,58),SIMD2(-145,80)],width:3.6),
        Street(points:[SIMD2(-145,-5),SIMD2(-62,-8),SIMD2(-39,-15),SIMD2(-35,-29)],width:3.4),
        Street(points:[SIMD2(36,30),SIMD2(60,48),SIMD2(105,42),SIMD2(150,52)],width:4.0),
        Street(points:[SIMD2(-64,58),SIMD2(-48,92),SIMD2(-65,150)],width:3.4)]

    private func streetDistance(_ x:Double,_ z:Double)->Double {
        let p=SIMD2(x,z)
        var distance=Double.greatestFiniteMagnitude
        for street in streets { for i in 1..<street.points.count {
            let a=street.points[i-1],d=street.points[i]-a
            let t=max(0,min(1,simd_dot(p-a,d)/simd_length_squared(d)))
            distance=min(distance,simd_length(p-a-d*t)-street.width/2)
        }}
        return distance
    }
    private func buildRoads() {
        for street in streets {
            for i in 1..<street.points.count {
                let a=street.points[i-1],b=street.points[i],delta=b-a
                let length=simd_length(delta),steps=Int(ceil(length/8))
                let yaw=atan2(delta.x,delta.y)
                for k in 0..<steps {
                    let center=a+delta*(Double(k)+0.5)/Double(steps)
                    let p=paint(center.x,center.y,yaw:yaw)
                    p.box(0,-0.012,0,street.width+0.7,0.016,length/Double(steps)+0.08,0x8e806e)
                    p.box(0,-0.002,0,street.width,0.012,length/Double(steps)+0.1,0xa49680)
                    // Worn cart tracks replace the modern white kerbs/markings.
                    for side in [-1.0,1] {
                        p.box(side*0.72,0.006,0,0.19,0.008,length/Double(steps)+0.1,0x938672,detail:true)
                    }
                }
                let q=paint(b.x,b.y)
                q.cylinder(0,0,0,street.width/2,street.width/2,0.008,0xa49680,sides:16)
            }
        }
        let plaza=paint(0,finishZ-7.0)
        plaza.box(0,-0.006,0,19,0.018,7.0,0x9f917b)
        // Small local passages connect the market doors to the main approach.
        for x in [-7.0,0,7] { plaza.box(x,0.005,0,1.4,0.012,7.2,0xb4a28a) }
    }

    private func buildGrandstand() {
        let z = finishZ-4.9, p = paint(0,z)
        cameraBounds.append((SIMD3(-7.5,0,z-2.6),SIMD3(7.5,3.9,z+1.95)))
        // Shops form the plinth, with seating rising toward the back of town.
        p.box(0,0.67,-0.65,12.8,1.34,3.8,sand)
        p.box(0,0.12,0.9,13.2,0.24,1.0,cream)
        for x in stride(from:-5.5,through:5.5,by:1.85) {
            p.box(x,0.76,1.26,1.42,1.12,0.04,dark)
            p.dome(x,1.28,1.28,0.71,0.45,0.09,sand,detail:true)
            p.box(x,0.16,1.35,1.48,0.18,0.32,trim)
            p.box(x,1.23,1.60,1.55,0.10,0.85,Int(x*10)%2 == 0 ? teal : rust)
            for sx in [-0.72,0.72] { p.box(x+sx,0.62,1.90,0.055,1.24,0.055,dark,detail:true) }
            for k in 0..<3 { p.box(x-0.42+Double(k)*0.38,0.42,1.44,0.28,0.35,0.23,0xa17c51,detail:true) }
        }
        for row in 0..<5 {
            let y=1.4+Double(row)*0.31, rz=0.72-Double(row)*0.56
            p.box(0,y-0.12,rz,12.6,0.24,0.58,cream)
            p.box(0,y+0.06,rz-0.12,12.1,0.12,0.25,rust,detail:true)
            for seat in 0..<22 where seat != 10 && seat != 11 && (seat+row*3)%9 != 0 {
                let x = -5.7+Double(seat)*0.54
                citizen(x,z+rz,y:y+0.12,yaw:0,index:row*22+seat,seated:true,animated:row == 0 && seat%4 == 0)
            }
        }
        // Central aisle and accessible-looking side stairs, not a solid slab.
        for side in [-1.0,1] {
            for step in 0..<12 {
                let h=Double(step+1)*0.225
                p.box(side*6.95,h/2,1.1-Double(step)*0.30,0.85,h,0.31,cream)
            }
            p.box(side*6.45,2.05,-0.4,0.09,1.45,3.65,trim)
        }
        // Shade roof with a gap over the central aisle. Opaque geometry avoids
        // layers of alpha over the crowded start and finish.
        for x in [-3.45,3.45] {
            p.box(x,3.78,-1.12,5.65,0.085,2.8,teal)
            for pole in [-2.7,2.7] { p.box(x+pole,2.49,-2.30,0.07,2.6,0.07,dark,detail:true) }
            for stripe in stride(from:-2.4,through:2.4,by:0.6) {
                p.box(x+stripe,3.826,-1.12,0.10,0.01,2.75,cream,detail:true)
            }
        }
        for (i,x) in [-5.2,-2.4,2.4,5.2].enumerated() {
            let sector=["A","B","C","D"][i]
            signs.plate("SECTOR \(sector)",eyebrow:"GRANDSTAND",footer:"ROWS 01–05",badge:sector,
                        at:SCNVector3(x,3.55,z+0.29),width:1.05,height:0.32,accent:teal,into:root)
            p.beam(SIMD3(x,3.73,0.29),SIMD3(x,3.82,0.29),0.018,dark,sides:5)
        }
        // Two plaster towers bookend the stand and make the civic landmark
        // readable from the track as well as the opening overview.
        for x in [-8.5,8.5] {
            buildings += 1
            cameraBounds.append((SIMD3(x-1.3,0,finishZ-7.4),SIMD3(x+1.3,6.6,finishZ-3.6)))
            let q=paint(x,finishZ-5.5)
            q.adobe(0,1.75,0,2.4,3.5,3.6,sand)
            for side in [-1.0,1] { q.door(0,side*1.81,0.70,1.35,dark,sand,side:side) }
            q.cylinder(0,4.45,0,0.85,0.62,1.8,cream)
            q.dome(0,5.35,0,0.72,0.55,0.72,sand)
            q.box(0,4.55,0.69,0.65,0.5,0.035,dark,detail:true)
            q.box(0,6.1,0,0.055,0.85,0.055,dark,detail:true)
        }
        signs.plate("MOS ASTER GRAND PRIX",eyebrow:"INTERPLANETARY RACING SERIES",footer:"START  /  FINISH",badge:"07",
                    at:SCNVector3(0,1.49,z+2.06),width:8.2,height:0.94,into:root)
        for (i,x) in [-8.5,8.5].enumerated() {
            signs.plate("GATE \(i+1)",eyebrow:"GRANDSTAND",footer:i==0 ? "SECTORS A / B":"SECTORS C / D",badge:i==0 ? "A":"C",
                        at:SCNVector3(x,2.37,finishZ-3.64),width:1.78,height:0.58,accent:teal,into:root)
        }
    }

    private var citySeed:UInt64 = 0xA57E2026
    private func random()->Double {
        citySeed = citySeed &* 6364136223846793005 &+ 1442695040888963407
        return Double(citySeed >> 32)/Double(UInt32.max)
    }
    private func reserved(_ x:Double,_ z:Double,_ w:Double,_ d:Double)->Bool {
        if infield(x,z) || !clearLot(x,z,w,d) { return true }
        if streetDistance(x,z)<hypot(w,d)*0.44 { return true }
        if abs(x)<11+w/2 && abs(z-(finishZ-6))<3.8+d/2 { return true }
        if abs(x-26)<5.2+w/2 && abs(z-17)<7.0+d/2 { return true }
        if abs(x-3)<4.4+w/2 && abs(z-22)<1.6+d/2 { return true }
        // Authored civic landmarks have room to breathe without an empty belt.
        for (cx,cz,r) in [(-27.0,22.0,3.0),(10.0,43.0,5.0),(-38.0,-5.0,5.0),(43.0,7.0,5.0)] {
            if abs(x-cx)<r+w/2 && abs(z-cz)<r+d/2 { return true }
        }
        return false
    }
    private func buildSettlement() {
        var index=0, row=0
        var z = -145.0
        // Dense irregular compounds, extending well past every overview edge.
        // The outer city gets only silhouette geometry, never small props/people.
        while z<145 {
            let stepZ=6.4+random()*2.4
            var x = -150.0+random()*6
            while x<150 {
                let stepX=5.8+random()*3.6
                let bx=x+stepX/2,bz=z+stepZ/2+sin(Double(row)*1.9+Double(index)*0.67)*0.55
                let w=stepX-0.38,d=stepZ-0.42
                let yaw=(random()-0.5)*0.16
                let boundW=w*cos(yaw)+d*abs(sin(yaw)),boundD=d*cos(yaw)+w*abs(sin(yaw))
                let h=2.0+random()*3.8
                if !reserved(bx,bz,boundW+0.2,boundD+0.2) {
                    cityCompound(bx,bz,w:w,d:d,h:h,index:index,yaw:yaw)
                }
                x += stepX;index += 1
            }
            z += stepZ;row += 1
        }
        // Smaller infill hugs the course where full urban compounds cannot fit.
        for j in 0..<16 { for i in 0..<16 {
            let x = -29.0+Double(i)*3.8,z = -28.0+Double(j)*3.8
            let w=2.9,d=2.8
            if reserved(x,z,w+0.4,d+0.4) { continue }
            if lots.contains(where:{abs(x-$0.x)<($0.width+w)/2+0.15 && abs(z-$0.z)<($0.depth+d)/2+0.15}) { continue }
            cityCompound(x,z,w:w,d:d,h:1.8+random()*1.5,index:1400+i+j*16,yaw:0)
        }}
    }

    private func cityCompound(_ x:Double,_ z:Double,w:Double,d:Double,h:Double,index:Int,yaw:Double) {
        let near=max(abs(x),abs(z))<54
        let p=paint(x,z,yaw:yaw)
        let boundW=w*cos(yaw)+d*abs(sin(yaw)),boundD=d*cos(yaw)+w*abs(sin(yaw))
        lots.append(TownLot(x:x,z:z,width:boundW+0.2,depth:boundD+0.2))
        buildings += 1
        if near { cameraBounds.append((SIMD3(x-boundW/2,0,z-boundD/2),SIMD3(x+boundW/2,h+min(w,d)*0.5,z+boundD/2))) }
        let colors:[UInt32]=[0xb5a084,0xbdaa8c,0xc4ad8c,0xa58c70,0xc9b89b,0x9e8872,0xb7a890,0xc0a786]
        let ink=colors[(index*7+index/11)%colors.count]
        let roof:UInt32=0x9e8970
        let style=(index*13+index/7)%9
        // Ground contact is baked into opaque strips, including far districts.
        p.box(0,-0.008,0,w+0.14,0.02,d+0.14,0x756c5f)
        if style<3 {
            // Joined adobe dwelling + offset domed chamber; no individual plinth.
            p.adobe(-w*0.17,h*0.43,0,w*0.66,h*0.86,d,ink,simple:!near)
            p.adobe(w*0.25,h*0.32,d*0.10,w*0.50,h*0.64,d*0.79,ink,simple:!near)
            let r=min(w*0.30,d*0.43)
            p.dome(-w*0.17,h*0.86,0,r,r*0.57,r,ink,sides:near ? 20:12)
            if style==1 { p.dome(w*0.25,h*0.64,d*0.1,w*0.22,w*0.15,w*0.22,ink,sides:near ? 16:10) }
        } else if style<5 {
            // Connected stepped roofscape with recessed terraces and parapets.
            p.adobe(0,h*0.30,0,w,h*0.60,d,ink,simple:!near)
            p.adobe(-w*0.18,h*0.78,-d*0.16,w*0.61,h*0.36,d*0.67,ink,simple:!near)
            p.box(w*0.23,h*0.606,d*0.16,w*0.47,0.025,d*0.58,roof)
            p.box(w*0.47,h*0.65,0,w*0.05,0.26,d,ink)
            p.box(0,h*0.65,d*0.47,w,0.26,d*0.05,ink)
            if style==4 { p.dome(-w*0.18,h*0.96,-d*0.16,w*0.24,w*0.15,w*0.24,ink,sides:near ? 16:10) }
        } else if style==5 {
            // Buttressed rotunda integrated into a long low workshop.
            p.adobe(0,h*0.22,0,w,h*0.44,d,ink,simple:!near)
            let r=min(w,d)*0.40
            p.cylinder(-w*0.08,h*0.54,-d*0.04,r,r*0.91,h*1.08,ink,sides:near ? 20:12)
            p.dome(-w*0.08,h*1.08,-d*0.04,r*0.91,r*0.5,r*0.91,ink,sides:near ? 20:12)
            p.cylinder(-w*0.08,h*0.8,-d*0.04,r*0.955,r*0.955,0.16,roof,sides:near ? 20:12)
        } else if style==6 {
            // Open courtyard enclosed on three sides, shaded workshop frontage.
            p.adobe(-w*0.34,h*0.42,0,w*0.32,h*0.84,d,ink,simple:!near)
            p.adobe(w*0.10,h*0.35,-d*0.31,w*0.78,h*0.70,d*0.38,ink,simple:!near)
            p.adobe(w*0.40,h*0.25,d*0.07,w*0.20,h*0.50,d*0.69,ink,simple:!near)
            p.canopy(w*0.05,d*0.21,w*0.57,d*0.41,h*0.59,0.25,0x87725c)
        } else if style==7 {
            // Industrial block: vaulted hall, sunken rooftop machinery enclosure.
            p.adobe(0,h*0.30,0,w,h*0.60,d,ink,simple:!near)
            p.dome(-w*0.2,h*0.60,0,w*0.28,w*0.27,d*0.45,ink,sides:near ? 16:10)
            p.box(w*0.25,h*0.605,0,w*0.40,0.025,d*0.7,0x665c50)
            for k in 0..<3 { p.box(w*0.25,h*0.71,-d*0.23+Double(k)*d*0.23,w*0.29,0.35,0.17,roof,detail:near) }
        } else {
            // Two unequal flat-roof dwellings linked by a utility room.
            p.adobe(-w*0.23,h*0.5,-d*0.05,w*0.54,h,d*0.9,ink,simple:!near)
            p.adobe(w*0.26,h*0.29,0,w*0.47,h*0.58,d,ink,simple:!near)
            p.box(-w*0.23,h+0.07,-d*0.05,w*0.48,0.15,d*0.83,roof)
        }
        guard near else { return }
        // Equipment on usable flat roofs, with cables/pipes at the near tier.
        if style==3 || style==4 || style==8 {
            let roofY=style==8 ? h*0.58:h*0.60
            p.box(w*0.25,roofY+0.19,d*0.08,w*0.25,0.38,d*0.28,0x7f7867)
            for k in 0..<4 { p.box(w*0.25,roofY+0.385,d*0.01+Double(k)*0.11,w*0.22,0.018,0.045,0x49483f,detail:true) }
            p.beam(SIMD3(w*0.15,roofY+0.12,-d*0.18),SIMD3(w*0.41,roofY+0.12,-d*0.18),0.095,0x918773,sides:8)
            p.beam(SIMD3(w*0.41,roofY+0.12,-d*0.18),SIMD3(w*0.41,roofY+0.12,d*0.29),0.095,0x918773,sides:8)
        }
        // Front and rear facades are dressed so driving around a block works.
        for side in [-1.0,1] {
            let face=(style==8 && side>0 ? d*0.4 : side*d/2)+side*0.018
            let doorX = style==6 ? -w*0.34 : -w*0.16
            p.door(doorX,face,0.70,min(1.4,h*0.57),0x3d352e,ink,side:side)
            if side>0 && max(abs(x),abs(z))<36 && index%7==0 && storefrontSigns<8 {
                let title=["MACHINE WORKS","CANTINA","OFFWORLD GOODS","REACTOR SUPPLY"][storefrontSigns%4]
                let sy=min(h-0.22,min(1.4,h*0.57)+0.43)
                for sx in [-0.55,0.55] { p.box(doorX+sx,sy,face-0.09,0.045,0.05,0.28,dark,detail:true) }
                let wx=x+doorX*cos(yaw)+(face+0.06)*sin(yaw)
                let wz=z-doorX*sin(yaw)+(face+0.06)*cos(yaw)
                signs.plate(title,eyebrow:"MOS ASTER",footer:"MARKET DISTRICT",badge:String(format:"%02d",storefrontSigns+11),
                            at:SCNVector3(wx,sy,wz),width:min(2.0,CGFloat(w)*0.62),height:0.42,yaw:CGFloat(yaw),
                            accent:storefrontSigns%2==0 ? rust:teal,into:root)
                storefrontSigns += 1
            }
            for k in 0..<2 {
                let xx = -w*0.32+Double(k)*w*0.55
                p.box(xx,h*0.41,face,0.24,0.57,0.035,0x39332c,detail:true)
                p.box(xx,h*0.41-0.27,face+side*0.045,0.30,0.075,0.12,roof,detail:true)
            }
            if index%3==0 {
                p.awning(doorX,face+side*0.38,w*0.37,0.90,min(1.9,h*0.70),index%2==0 ? 0x7e6a50:0x8f5140)
            }
        }
        if style<5 || style==8 {
            for side in [-1.0,1] {
                p.adobe(side*w*0.43,h*0.26,d*0.41,0.28,h*0.52,0.35,ink)
            }
        }
        // Roof equipment is useful silhouette at medium distance, tiny wires LOD out.
        let ry = style==5 ? h*1.10 : (style<3 ? h*0.65:h*0.65)
        if index%3==0 {
            p.cylinder(w*0.28,ry+0.3,-d*0.21,0.32,0.29,0.60,roof,sides:8,detail:true)
            p.box(w*0.28,ry+0.64,-d*0.21,0.72,0.10,0.72,0x756654,detail:true)
        }
        if index%5==0 {
            p.cylinder(-w*0.31,h+0.30,-d*0.1,0.08,0.065,0.90,0x5c5449,sides:6,detail:true)
            p.box(-w*0.31,h+0.62,-d*0.1,0.66,0.045,0.045,0x65594b,detail:true)
        }
        for k in 0..<(index%3) {
            p.box(w*0.31,0.19+Double(k)*0.29,d*0.33,0.51,0.36,0.43,k%2==0 ? 0x715642:0x6a7870,detail:true)
        }
    }

    /// Ray crossing against the closed racing spline. Clearance alone does not
    /// distinguish a safe infield pocket from land outside the circuit.
    private func infield(_ x:Double,_ z:Double)->Bool {
        if abs(x)>22 || abs(z)>22 { return false }
        var inside=false
        var a=DirtCourse.point(0)
        for i in 1...DirtCourse.sampleCount {
            let b=DirtCourse.point(Double(i)/Double(DirtCourse.sampleCount)*2 * .pi)
            if (a.z>z) != (b.z>z) && x < (b.x-a.x)*(z-a.z)/(b.z-a.z)+a.x { inside.toggle() }
            a=b
        }
        return inside
    }

    private var repairLots:[TownLot] = []
    private func buildRepairPit() {
        // Two pockets on the west side of the infield, clear of the dirt shoulder.
        for (i,location) in [(-7.5,-7.5),(-8.5,3.5)].enumerated() {
            let x=location.0,z=location.1,w=4.0,d=4.0
            guard infield(x,z),clearLot(x,z,w+0.3,d+0.3) else { continue }
            repairLots.append(TownLot(x:x,z:z,width:w+0.3,depth:d+0.3))
            let p=paint(x,z)
            p.box(0,0.025,0,w+0.2,0.05,d+0.2,0x897c63)
            p.canopy(0,0,w,d,2.0,0.65,i == 0 ? rust:teal)
            cameraBounds.append((SIMD3(x-w/2,2.0,z-d/2),SIMD3(x+w/2,2.65,z+d/2)))
            // Open sides reveal benches, parts racks and a robot on a lift.
            p.box(-0.92,0.69,0.15,0.64,0.12,1.7,trim)
            for zz in [-0.55,0.85] { p.box(-0.92,0.33,zz,0.48,0.66,0.09,dark,detail:true) }
            for k in 0..<4 {
                let zz = -0.44+Double(k)*0.35
                p.cylinder(-0.92,0.84,zz,0.12,0.12,0.18,k%2 == 0 ? teal:cream,sides:8,detail:true)
                p.box(-0.69,0.775,zz,0.08,0.035,0.25,dark,detail:true) // spanners
                p.box(-0.69,0.775,zz+0.11,0.16,0.035,0.07,cream,detail:true)
            }
            p.box(0.24,0.14,0.20,0.95,0.28,1.3,dark)
            p.box(0.24,0.57,0.20,0.48,0.58,0.56,cream)
            p.box(0.24,0.63,0.49,0.32,0.34,0.04,dark,detail:true) // exposed circuitry
            for k in 0..<3 { p.box(0.14+Double(k)*0.1,0.64,0.52,0.035,0.25,0.035,k%2 == 0 ? rust:teal,detail:true) }
            p.dome(0.24,0.99,0.20,0.26,0.23,0.26,teal,sides:10)
            for xx in [-0.13,0.61] { p.box(xx,0.30,0.30,0.20,0.20,0.70,trim) }
            // Engine hoist and hanging spare motor.
            p.box(1.17,0.8,0.91,0.09,1.6,0.09,dark)
            p.box(0.83,1.60,0.91,0.76,0.10,0.10,rust)
            p.box(0.51,1.29,0.91,0.03,0.54,0.03,dark,detail:true)
            p.cylinder(0.51,0.94,0.91,0.18,0.22,0.36,trim,sides:8)
            for k in 0..<3 {
                p.cylinder(1.03,0.12+Double(k)*0.17,-0.86,0.27,0.27,0.15,dark,sides:10,detail:true)
                p.cylinder(1.03,0.20+Double(k)*0.17,-0.86,0.14,0.14,0.015,cream,sides:10,detail:true)
            }
            citizen(x+0.73,z-0.35,y:0.055,yaw:-1.2,index:707+i,seated:false)
            p.box(-0.96,0.19,-1.06,0.60,0.36,0.40,teal)
            p.box(-0.96,0.40,-1.06,0.21,0.06,0.08,dark,detail:true)
            for sx in [-0.65,0.65] { p.beam(SIMD3(sx,2.02,-1.56),SIMD3(sx,2.43,-1.56),0.012,dark,sides:5) }
            signs.plate(i==0 ? "DROID REPAIR":"PARTS & SALVAGE",eyebrow:"RACE SERVICE",footer:"CREW ACCESS ONLY",badge:i==0 ? "01":"02",
                        at:SCNVector3(x,1.77,z-1.56),width:1.85,height:0.46,yaw:.pi,accent:rust,into:root)
        }
    }

    private func buildLandmarks() {
        // Spaceport hangar and landing circle, outside the circuit.
        let p=paint(23,14)
        cameraBounds.append((SIMD3(21.3,0,18.5),SIMD3(30.7,5.1,23.5)))
        p.box(3,0.035,0,9,0.07,12,0x9f947b)
        p.cylinder(3,0.08,1.0,3.7,3.7,0.06,trim)
        p.cylinder(3,0.115,1.0,3.4,3.4,0.015,0xb7a98d)
        for i in 0..<12 {
            let a=Double(i)*Double.pi/6
            p.box(3+cos(a)*3.55,0.14,1+sin(a)*3.55,0.25,0.04,0.25,cream,detail:true)
        }
        p.box(3,2.3,7.0,9,4.6,4.2,sand)
        p.box(3,1.85,4.87,7.5,3.7,0.055,dark)
        p.box(3,4.8,7,9.4,0.40,4.5,cream)
        for x in stride(from:-0.5,through:6.5,by:0.7) { p.box(x,1.9,4.82,0.065,3.6,0.06,trim,detail:true) }
        signs.plate("DOCK 07",eyebrow:"MOS ASTER SPACEPORT",footer:"ARRIVALS  /  CARGO",badge:"07",
                    at:SCNVector3(26,4.13,18.77),width:5.6,height:0.65,yaw:.pi,accent:teal,into:root)
        // A small parked original utility shuttle: low hull, wings and engines.
        p.box(3,0.7,0.6,1.35,0.65,3.2,cream)
        p.box(3,1.13,0.2,0.75,0.36,1.1,teal)
        p.box(3,0.62,1.2,4.2,0.16,1.6,trim)
        for x in [1.25,4.75] {
            p.cylinder(x,0.7,1.2,0.29,0.24,0.75,dark,detail:true)
            p.box(x,0.28,1.2,0.11,0.5,0.5,dark,detail:true)
        }
        // Comms tower on the far skyline, constructed from a few opaque forms.
        let t=paint(-27,22)
        t.cylinder(0,2.0,0,1.7,1.3,4,sand)
        t.cylinder(0,4.5,0,1.4,1.1,1,cream)
        t.cylinder(0,6.8,0,0.23,0.16,4.2,dark)
        t.cylinder(0,7.3,0,1.9,0.25,0.9,cream)
        t.box(0,9.4,0,0.075,2.4,0.075,dark)
        for x in [-1.0,1] { t.box(x,8.0,0,0.035,1.6,0.035,trim,detail:true) }
        buildCityLandmarks()
    }

    private func buildCityLandmarks() {
        // A layered navigation tower and large civic dome break the roofscape.
        let p=paint(10,43)
        p.adobe(0,2.3,0,9,4.6,8,0xad9678)
        for side in [-1.0,1] {
            p.door(0,side*4.015,1.5,2.1,0x393129,0xad9678,side:side)
            for x in [-3.8,3.8] { p.adobe(x,2.2,side*3.75,0.48,4.4,0.75,0x9b8367) }
            for x in [-2.2,2.2] { p.box(x,3.4,side*4.01,0.33,0.75,0.025,0x594b3c) }
        }
        p.cylinder(0,6.4,0,3.0,2.4,4.0,0xbca88a,sides:16)
        for y in [5.0,8.3] { p.cylinder(0,y,0,3.2,3.05,0.45,0x88785f,sides:16) }
        p.dome(0,8.5,0,2.6,1.5,2.6,0xbba58a,sides:20)
        p.cylinder(0,10.7,0,0.30,0.20,1.7,dark,sides:8)
        for i in 0..<12 {
            let a=Double(i)*Double.pi/6,q=paint(10+cos(a)*2.72,43+sin(a)*2.72,yaw:Double.pi/2-a)
            q.box(0,6.7,0,0.36,1.25,0.07,0x4b4338)
            q.box(0,3.0,1.15,0.42,4.0,0.5,0x927d61)
        }
        // Circular docking courts inspired by the film's excavated landing bays.
        for (i,pt) in [(-38.0,-5.0),(43.0,7.0)].enumerated() {
            let q=paint(pt.0,pt.1)
            q.cylinder(0,0.025,0,4.8,4.8,0.05,0x514a42,sides:32)
            q.ring(0,0.90,0,4.8,4.1,1.8,0xaf9878,sides:32)
            q.ring(0,1.83,0,4.93,4.02,0.14,0xc0ad8d,sides:32)
            q.box(0,0.11,0,4.5,0.05,0.12,0x9a8668)
            q.box(0,0.6,0,1.2,0.5,2.8,0x8b9187)
            q.box(0,0.72,0.3,3.4,0.13,1.5,0xa69780)
            q.box(0,1.0,-0.4,0.65,0.3,0.9,0x465e60)
            q.adobe(-2.0,1.0,4.0,3.2,2.0,2.5,0xaf9878)
            q.dome(-2.0,2.0,4.0,1.5,0.9,1.15,0xaf9878,sides:16)
            for k in 0..<6 { q.box(2.7,0.15+Double(k)*0.27,3.8-Double(k)*0.36,1.0,0.3,0.38,0x9c886b) }
            signs.plate("LANDING BAY",eyebrow:"SPACEPORT AUTHORITY",footer:"KEEP APRON CLEAR",badge:"0\(i+8)",
                        at:SCNVector3(pt.0,1.15,pt.1-4.94),width:2.9,height:0.59,yaw:.pi,accent:teal,into:root)
        }
        // Small roof-mounted utilities sit within the city instead of a mesa ring.
        for (x,z) in [(-53.0,48.0),(49.0,53.0),(-70.0,-41.0),(22.0,71.0)] {
            let q=paint(x,z)
            q.cylinder(0,5.8,0,1.3,0.85,11.6,0x9c886c,sides:10)
            for y in [4.0,7.0,10.0] { q.cylinder(0,y,0,1.6,1.45,0.30,0xb29d7e,sides:10) }
            q.dome(0,11.6,0,0.87,0.5,0.87,0xbba88b,sides:12)
        }
    }

    private func buildStreetLife() {
        // Residents by selected entrances; each is clear of the full circuit.
        for (i,lot) in lots.enumerated() where i%3 == 0 && max(abs(lot.x),abs(lot.z))<47 {
            let z=lot.z+lot.depth/2+0.25
            if DirtCourse.projection(x:lot.x,z:z).distance > 3.5 {
                citizen(lot.x,z,y:0.03,yaw:Double(i),index:i+200,seated:false,animated:i%12 == 0)
                if i%4 == 0 { citizen(lot.x+0.50,z+0.18,y:0.03,yaw:Double(i)+1,index:i+210,seated:false) }
            }
        }
        for i in 0..<36 {
            let x = -8.5+Double(i%18)*0.98+sin(Double(i)*2.3)*0.22, z=finishZ-7.8-Double(i/18)*0.85+cos(Double(i)*1.9)*0.30
            citizen(x,z,y:0.04,yaw:Double(i)*0.9,index:i+400,seated:false,animated:i%9 == 0,walking:i%9 == 0)
        }
        // Side terrace: civic spectators overlooking the northern sweeping turn.
        let p=paint(3,22)
        p.box(0,0.62,0,8,1.24,2.4,sand)
        for i in 0..<18 {
            let x = -0.7+Double(i%9)*0.65, z=21.7+Double(i/9)*0.65
            citizen(x,z,y:1.26,yaw:Double.pi,index:i+500,seated:false)
        }
        for x in [-3.8,3.8] { p.box(x,1.7,0,0.08,1,2.3,cream,detail:true) }
    }

    private func buildMarketDetails() {
        for (i,x) in [-7.5,-3.7,3.7,7.5].enumerated() {
            let z=finishZ-10.0,p=paint(x,z)
            p.canopy(0,0,2.8,1.45,1.65,0.22,i%2==0 ? 0x8b5c44:0x617875)
            p.box(0,0.62,-0.1,2.35,0.16,0.65,0x766048)
            for side in [-1.0,1] { p.box(side*0.95,0.3,-0.1,0.14,0.6,0.5,0x54483b,detail:true) }
            for k in 0..<6 {
                let xx = -0.9+Double(k)*0.36
                if i%2==0 { p.cylinder(xx,0.82,-0.08,0.12,0.17,0.28,0x97816a,sides:8,detail:true) }
                else { p.box(xx,0.81,-0.08,0.25,0.22,0.34,k%2==0 ? 0x667771:0xad8f65,detail:true) }
            }
            p.beam(SIMD3(-1.4,1.64,-0.77),SIMD3(1.4,1.64,-0.77),0.022,dark,sides:6)
            for sx in [-0.75,0.75] { p.beam(SIMD3(sx,1.57,-0.77),SIMD3(sx,1.64,-0.77),0.012,dark,sides:5) }
            signs.plate(["CERAMICS","DROID EXCHANGE","SPICE MERCHANT","POWER CELLS"][i],eyebrow:"ASTER BAZAAR",footer:"TRADE  /  REPAIR  /  SUPPLIES",badge:"0\(i+1)",
                        at:SCNVector3(x,1.37,z-0.77),width:2.15,height:0.40,yaw:.pi,accent:i%2==0 ? rust:teal,into:root)
            citizen(x,z+0.42,y:0.03,yaw:.pi,index:1701+i,seated:false)
            citizen(x+1.25,z+0.82,y:0.03,yaw:-0.4,index:1801+i,seated:false)
            p.box(1.12,0.24,0.45,0.43,0.48,0.44,0x665846,detail:true)
        }
        // Pedestrian groups follow roads and cluster at shops, never the course.
        for (roadIndex,street) in streets.enumerated() {
            for i in 1..<street.points.count {
                let a=street.points[i-1],delta=street.points[i]-a,length=simd_length(delta)
                let tangent=delta/length,normal=SIMD2(-tangent.y,tangent.x)
                let count=Int(length/3.5)
                for k in 0..<count {
                    let t=(Double(k)+0.5)/Double(max(1,count))
                    let center=a+delta*t+normal*((k%2==0 ? 1.0:-1.0)*(street.width/2-0.32))
                    guard max(abs(center.x),abs(center.y))<52 else { continue }
                    let index=2000+roadIndex*100+i*13+k
                    citizen(center.x,center.y,y:0.02,yaw:atan2(tangent.x,tangent.y),index:index,seated:false)
                    if k%4==0 { citizen(center.x+normal.x*0.40,center.y+normal.y*0.40,y:0.02,yaw:1.3,index:index+17,seated:false) }
                }
            }
        }
        for (x,z,yaw,ink) in [(31.0,-16.0,0.40,UInt32(0x996551)),(-24.0,-27.7,1.4,UInt32(0x7c8880)),(7.0,34.0,-1.4,UInt32(0x9a855f))] {
            let p=paint(x,z,yaw:yaw)
            // Original low-slung utility speeders: rounded nose, cockpit, side pods.
            p.box(0,0.19,0,1.3,0.04,2.5,0x726957)
            p.adobe(0,0.52,0,1.22,0.36,2.5,ink)
            p.dome(0,0.64,0.82,0.55,0.23,0.75,ink,sides:12)
            p.box(0,0.77,-0.33,0.86,0.26,0.85,0x414b4b)
            p.box(0,0.80,-0.91,0.88,0.26,0.14,ink)
            for side in [-1.0,1] {
                p.beam(SIMD3(side*0.75,0.50,-0.95),SIMD3(side*0.75,0.50,0.95),0.19,0x605c51,sides:8)
                p.box(side*0.4,0.51,1.26,0.16,0.12,0.035,0xd7c393,detail:true)
            }
        }
        // Visible cables and hardware turn the repair pockets into a paddock.
        for z in [-7.5,3.5] {
            let x = z<0 ? -7.5:-8.5,p=paint(x,z)
            for k in 0..<5 {
                p.beam(SIMD3(-1.25,0.08,-0.35+Double(k)*0.22),SIMD3(-0.5,0.08,-0.55+Double(k)*0.2),0.023,0x554a3d,sides:5)
            }
            p.box(1.50,0.47,0.95,0.32,0.94,0.40,0x77766a)
            p.box(1.50,0.80,0.73,0.22,0.18,0.025,0x45656b)
            p.box(1.50,0.55,0.73,0.13,0.05,0.03,0xc5a56c)
        }
    }

    private func citizen(_ x:Double,_ z:Double,y:Double,yaw:Double,index:Int,seated:Bool,animated:Bool=false,walking:Bool=false) {
        population += 1
        let node = crowd.add(x:x,y:y,z:z,yaw:yaw,index:index,seated:seated,
                             animated:animated && people.count<(walking ? 16:12))
        if let node {
            node.name="Animated town spectator"
            root.addChildNode(node)
            // The full body uses a relaxed authored pose with bounded idle motion.
            people.append(TownPerson(node:node,origin:node.position,phase:Double(index)*1.618))
        }
    }
    private func buildWayfinding() {
        // Signs face the approach and stand at street edges, not in the roadway.
        let routes:[(Double,Double,Double,String,String,String)] = [
            (-12.5,finishZ-10.2,Double.pi,"←  BAZAAR","GRANDSTAND  /  GATES 1–2","M"),
            (20.5,-29.0,Double.pi,"←  SPACEPORT","DOCK 07  /  LANDING BAYS","D"),
            (27.0,-12.0,-Double.pi/2,"GATES 1–2  ←","GRANDSTAND  /  BAZAAR","R")]
        for (x,z,yaw,title,footer,badge) in routes {
            let p=paint(x,z,yaw:yaw)
            p.beam(SIMD3(0,0,0),SIMD3(0,2.5,0),0.045,0x55574f,sides:8)
            signs.plate(title,eyebrow:"MOS ASTER WAYFINDING",footer:footer,badge:badge,
                        at:SCNVector3(x,2.14,z),width:2.45,height:0.55,yaw:CGFloat(yaw),accent:teal,into:root)
        }
        signs.plate("NORTH CURVE",eyebrow:"MOS ASTER GRAND PRIX",footer:"SPECTATOR TERRACE",badge:"N",
                    at:SCNVector3(3,0.82,20.76),width:3.6,height:0.55,yaw:.pi,into:root)
    }

    func update(dt:Double,camera:SCNVector3,player:SIMD2<Double>) {
        guard dt>0 else { return }
        clock += dt
        if clock-visibilityClock>0.25 {
            visibilityClock=clock; animatedCount=0
            for person in people {
                let d=hypot(Double(camera.x-person.origin.x),Double(camera.z-person.origin.z))
                // Far animated figures remain visible in their resting pose.
                if d<24 { animatedCount += 1 }
            }
        }
        for person in people {
            let d=hypot(Double(camera.x-person.origin.x),Double(camera.z-person.origin.z))
            guard d<24 else { continue }
            person.node.eulerAngles.z=CGFloat(sin(clock*0.8+person.phase)*0.012)
        }
    }
    func reset() {
        clock=0;visibilityClock = -1
        for p in people { p.node.position=p.origin;p.node.eulerAngles.z=0 }
    }
    /// Clip the chase/orbit boom against simple scenery bounds, with a small
    /// near-plane margin. Does not add any scene geometry to race physics.
    func clearCamera(from target:SCNVector3,to desired:SCNVector3)->SCNVector3 {
        let a=SIMD3<Double>(Double(target.x),Double(target.y),Double(target.z))
        let b=SIMD3<Double>(Double(desired.x),Double(desired.y),Double(desired.z)), delta=b-a
        var limit=1.0
        for (low,high) in cameraBounds {
            let lo=low-SIMD3(repeating:0.20),hi=high+SIMD3(repeating:0.20)
            var enter=0.0,leave=1.0,hit=true
            for axis in 0..<3 {
                if abs(delta[axis])<1e-8 {
                    if a[axis]<lo[axis] || a[axis]>hi[axis] { hit=false;break }
                } else {
                    let t0=(lo[axis]-a[axis])/delta[axis],t1=(hi[axis]-a[axis])/delta[axis]
                    enter=max(enter,min(t0,t1));leave=min(leave,max(t0,t1))
                    if enter>leave { hit=false;break }
                }
            }
            if hit && enter>0 { limit=min(limit,max(0.08,enter-0.025)) }
        }
        let result=a+delta*limit
        return SCNVector3(result.x,result.y,result.z)
    }
    var statistics: [String:Int] {
        ["buildings":buildings,"repairTents":repairLots.count,"infieldHouses":lots.filter{infield($0.x,$0.z)}.count,"people":population,"animatedPeople":people.count,
         "signs":signs.count,"signTextFits":signs.valid ? 1:0,"walkingPeople":0,"crowdCells":crowd.cellCount,"crowdNearTriangles":crowd.triangles,"crowdFarTriangles":crowd.farTriangles,"cells":cells.count,"nearTriangles":triangleCount,"farTriangles":coarseTriangles]
    }
    var cityCoveragePassed:Bool {
        (0..<8).allSatisfy { sector in
            lots.filter { lot in
                let radius=hypot(lot.x,lot.z)
                let angle=atan2(lot.z,lot.x)+Double.pi
                return radius>55 && radius<120 && min(7,Int(angle/(2 * Double.pi)*8))==sector
            }.count>30
        }
    }
    var streetNetworkPassed:Bool {
        // Each route branches from a prior route; no isolated road or ring road.
        var vertices:Set<SIMD2<Double>>=[]
        var edges=0
        for (i,street) in streets.enumerated() {
            if i>0 && !street.points.contains(where:{vertices.contains($0)}) { return false }
            vertices.formUnion(street.points);edges += street.points.count-1
        }
        return edges==vertices.count-1
    }
    func validate() -> Bool {
        buildings>800 && population>120 && people.count<=16 && triangleCount<360_000 && coarseTriangles<290_000 && cells.count<150
            && signs.valid && signs.count>=20 && crowd.valid && cityCoveragePassed && streetNetworkPassed
            && repairLots.count == 2
            && lots.allSatisfy { !infield($0.x,$0.z) && clearLot($0.x,$0.z,$0.width,$0.depth) }
            && repairLots.allSatisfy { infield($0.x,$0.z) && clearLot($0.x,$0.z,$0.width,$0.depth) }
    }

}

/// Offline-style mesh batching performed once at scene preparation. No SceneKit
/// primitive nodes survive for each window, brick or spectator body part.
final class TownMesh {
    var materialSlot=0
    private var groups:[[Int32]]=[[],[],[]]
    var positions:[SCNVector3]=[], normals:[SCNVector3]=[], uv:[CGPoint]=[], colors:[Float]=[], indices:[Int32]=[]
    func triangle(_ a:SIMD3<Float>,_ b:SIMD3<Float>,_ c:SIMD3<Float>,_ color:UInt32, smooth:[SIMD3<Float>]?=nil) {
        let cross=simd_cross(b-a,c-a)
        guard simd_length_squared(cross)>1e-12 else { return }
        let n=simd_normalize(cross), base=Int32(positions.count)
        // Baked face tone supplies cheap architectural depth even outside sun shadows.
        for (i,v) in [a,b,c].enumerated() {
            let normal=smooth?[i] ?? n
            let contact:Float=0.83+0.17*min(1,max(0,v.y)/1.2)
            let shade:Float=(0.94+0.06*max(0,normal.y))*contact
            positions.append(SCNVector3(v));normals.append(SCNVector3(normal))
            let axis=abs(n)
            let tex:SIMD2<Float> = axis.y>max(axis.x,axis.z) ? SIMD2(v.x,v.z) : (axis.x>axis.z ? SIMD2(v.z,v.y):SIMD2(v.x,v.y))
            uv.append(CGPoint(x:Double(tex.x)*0.48,y:Double(tex.y)*0.48))
            colors += [Float((color>>16)&255)/255*shade,Float((color>>8)&255)/255*shade,Float(color&255)/255*shade,1]
        }
        indices += [base,base+1,base+2]
        groups[materialSlot] += [base,base+1,base+2]
    }
    func geometry(material:SCNMaterial,relativeTo origin:SIMD3<Float> = .zero)->SCNGeometry {
        let source=colors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:positions.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
        let local=positions.map { SCNVector3(Float($0.x)-origin.x,Float($0.y)-origin.y,Float($0.z)-origin.z) }
        let g=SCNGeometry(sources:[SCNGeometrySource(vertices:local),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv),source],elements:groups.filter{!$0.isEmpty}.map{SCNGeometryElement(indices:$0,primitiveType:.triangles)})
        let materials=[material,CityMaterials.cloth,CityMaterials.metal]
        g.materials=groups.indices.filter{!groups[$0].isEmpty}.map{materials[$0]};return g
    }
}
private struct TownPainter {
    let near:TownMesh,far:TownMesh,origin:SIMD3<Float>,yaw:Float
    private func point(_ x:Double,_ y:Double,_ z:Double)->SIMD3<Float> {
        let c=cos(yaw),s=sin(yaw)
        return origin+SIMD3(Float(x)*c+Float(z)*s,Float(y),-Float(x)*s+Float(z)*c)
    }
    private func tri(_ a:SIMD3<Float>,_ b:SIMD3<Float>,_ c:SIMD3<Float>,_ ink:UInt32,_ detail:Bool) {
        near.triangle(a,b,c,ink);if !detail { far.triangle(a,b,c,ink) }
    }
    private func quad(_ a:SIMD3<Float>,_ b:SIMD3<Float>,_ c:SIMD3<Float>,_ d:SIMD3<Float>,_ ink:UInt32,_ detail:Bool) {
        tri(a,b,c,ink,detail);tri(a,c,d,ink,detail)
    }
    func beam(_ from:SIMD3<Double>,_ to:SIMD3<Double>,_ radius:Double,_ ink:UInt32,sides:Int=8) {
        near.materialSlot=2;far.materialSlot=2
        defer { near.materialSlot=0;far.materialSlot=0 }
        let axis=simd_normalize(to-from)
        let seed=abs(axis.y)<0.9 ? SIMD3<Double>(0,1,0):SIMD3<Double>(1,0,0)
        let u=simd_normalize(simd_cross(axis,seed))*radius,v=simd_cross(axis,u)
        func world(_ p:SIMD3<Double>)->SIMD3<Float> { point(p.x,p.y,p.z) }
        for i in 0..<sides {
            let a=Double(i)*2 * Double.pi/Double(sides),b=Double(i+1)*2 * Double.pi/Double(sides)
            let aa=u*cos(a)+v*sin(a),bb=u*cos(b)+v*sin(b)
            quad(world(from+aa),world(to+aa),world(to+bb),world(from+bb),ink,true)
            tri(world(from),world(from+bb),world(from+aa),ink,true)
            tri(world(to),world(to+aa),world(to+bb),ink,true)
        }
    }
    func adobe(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ d:Double,_ ink:UInt32,simple:Bool=false) {
        if simple { box(x,y,z,w,h,d,ink);return }
        let bevel=min(0.18,min(w,d)*0.12)
        var outline:[(Double,Double,Double)]=[]
        for (cx,cz,start) in [(w/2-bevel,-d/2+bevel,-Double.pi/2),(w/2-bevel,d/2-bevel,0),(-w/2+bevel,d/2-bevel,Double.pi/2),(-w/2+bevel,-d/2+bevel,Double.pi)] {
            for k in 0...2 {
                let angle=start+Double(k) * .pi/4
                outline.append((cx+cos(angle)*bevel,cz+sin(angle)*bevel,angle))
            }
        }
        func normal(_ angle:Double,_ rise:Float)->SIMD3<Float> {
            let a=Float(angle)-yaw
            return simd_normalize(SIMD3(cos(a),rise,sin(a)))
        }
        func smoothQuad(_ v:[SIMD3<Float>],_ n:[SIMD3<Float>]) {
            for ids in [[0,1,2],[0,2,3]] {
                let normals=ids.map{n[$0]}
                near.triangle(v[ids[0]],v[ids[1]],v[ids[2]],ink,smooth:normals)
                far.triangle(v[ids[0]],v[ids[1]],v[ids[2]],ink,smooth:normals)
            }
        }
        for i in 0..<outline.count {
            let a=outline[i],b=outline[(i+1)%outline.count]
            let v0=point(x+a.0,y-h/2,z+a.1),v1=point(x+b.0,y-h/2,z+b.1)
            let v2=point(x+b.0*0.97,y+h/2-bevel,z+b.1*0.97),v3=point(x+a.0*0.97,y+h/2-bevel,z+a.1*0.97)
            let v4=point(x+b.0*0.92,y+h/2,z+b.1*0.92),v5=point(x+a.0*0.92,y+h/2,z+a.1*0.92)
            smoothQuad([v0,v3,v2,v1],[normal(a.2,0),normal(a.2,0.18),normal(b.2,0.18),normal(b.2,0)])
            smoothQuad([v3,v5,v4,v2],[normal(a.2,0.18),normal(a.2,1.3),normal(b.2,1.3),normal(b.2,0.18)])
            tri(point(x,y+h/2,z),v4,v5,ink,false)
        }
    }

    func ring(_ x:Double,_ y:Double,_ z:Double,_ outer:Double,_ inner:Double,_ h:Double,_ ink:UInt32,sides:Int=24) {
        for i in 0..<sides {
            let a=Double(i)*2 * Double.pi/Double(sides),b=Double(i+1)*2 * Double.pi/Double(sides)
            let lo=y-h/2,hi=y+h/2
            let a0=point(x+cos(a)*outer,lo,z+sin(a)*outer),b0=point(x+cos(b)*outer,lo,z+sin(b)*outer)
            let a1=point(x+cos(a)*outer,hi,z+sin(a)*outer),b1=point(x+cos(b)*outer,hi,z+sin(b)*outer)
            let a2=point(x+cos(a)*inner,hi,z+sin(a)*inner),b2=point(x+cos(b)*inner,hi,z+sin(b)*inner)
            let a3=point(x+cos(a)*inner,lo,z+sin(a)*inner),b3=point(x+cos(b)*inner,lo,z+sin(b)*inner)
            quad(a0,a1,b1,b0,ink,false);quad(a1,a2,b2,b1,ink,false);quad(a2,a3,b3,b2,ink,false)
        }
    }
    func door(_ x:Double,_ z:Double,_ w:Double,_ h:Double,_ ink:UInt32,_ trim:UInt32,side:Double) {
        box(x,h*0.43,z,w,h*0.86,0.05,ink)
        dome(x,h*0.86,z,w/2,w*0.52,0.055,ink,sides:12)
        for sx in [-1.0,1] { adobe(x+sx*(w/2+0.07),h*0.42,z-side*0.015,0.13,h*0.84,0.13,trim) }
        for i in 0..<16 {
            let a=Double(i) * .pi/16,b=Double(i+1) * .pi/16
            let r=w/2,outer=r+0.12,cy=h*0.86
            let a0=point(x+cos(a)*r,cy+sin(a)*r,z+side*0.06)
            let a1=point(x+cos(a)*outer,cy+sin(a)*outer,z+side*0.10)
            let b0=point(x+cos(b)*r,cy+sin(b)*r,z+side*0.06)
            let b1=point(x+cos(b)*outer,cy+sin(b)*outer,z+side*0.10)
            if side>0 { quad(a0,a1,b1,b0,trim,true) } else { quad(b0,b1,a1,a0,trim,true) }
        }
        near.materialSlot=2
        box(x,h*0.40,z+side*0.035,w*0.79,h*0.72,0.012,0x86715b,detail:true)
        for sx in [-0.22,0.22] { box(x+sx*w,h*0.4,z+side*0.047,0.022,h*0.70,0.014,0x493e34,detail:true) }
        box(x+w*0.22,h*0.4,z+side*0.068,0.055,0.14,0.035,0xb7a184,detail:true)
        near.materialSlot=0
        box(x,0.04,z+side*0.13,w+0.18,0.08,0.31,trim,detail:true)
    }
    func awning(_ x:Double,_ z:Double,_ w:Double,_ d:Double,_ y:Double,_ ink:UInt32) {
        cloth(x,z,w,d,y,0.12,ink,ridge:false)
    }
    private func cloth(_ x:Double,_ z:Double,_ w:Double,_ d:Double,_ y:Double,_ rise:Double,_ ink:UInt32,ridge:Bool) {
        near.materialSlot=1;far.materialSlot=1
        defer { near.materialSlot=0;far.materialSlot=0 }
        let nx=8,nz=4
        func v(_ i:Int,_ j:Int)->SIMD3<Float> {
            let u=Double(i)/Double(nx),t=Double(j)/Double(nz)
            let roof=ridge ? rise*(1-abs(u*2-1)) : -t*0.15
            let sag=0.10*sin(u * .pi)*sin(t * .pi)+0.018*sin(u*12 * .pi)*sin(t * .pi)
            return point(x+(u-0.5)*w,y+roof-sag,z+(t-0.5)*d)
        }
        for i in 0..<nx { for j in 0..<nz {
            let a=v(i,j),b=v(i+1,j),c=v(i+1,j+1),e=v(i,j+1)
            quad(a,e,c,b,ink,true);quad(b,c,e,a,ink,true)
        }}
        // Distant cloth keeps the silhouette without folds.
        let a=v(0,0),b=v(nx,0),c=v(nx,nz),e=v(0,nz)
        far.triangle(a,e,c,ink);far.triangle(a,c,b,ink)
        far.triangle(c,e,a,ink);far.triangle(b,c,a,ink)
    }
    func canopy(_ x:Double,_ z:Double,_ w:Double,_ d:Double,_ y:Double,_ rise:Double,_ ink:UInt32) {
        cloth(x,z,w,d,y,rise,ink,ridge:true)
        for side in [-1.0,1.0] { for zz in [-d/2,d/2] {
            beam(SIMD3(x+side*w/2,0,z+zz),SIMD3(x+side*w/2,y,z+zz),0.035,0x625b50)
        }}
        beam(SIMD3(x,y+rise,z-d/2),SIMD3(x,y+rise,z+d/2),0.04,0x625b50)
    }
    func box(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ d:Double,_ ink:UInt32,detail:Bool=false) {
        let v=[point(x-w/2,y-h/2,z-d/2),point(x+w/2,y-h/2,z-d/2),point(x+w/2,y+h/2,z-d/2),point(x-w/2,y+h/2,z-d/2),point(x-w/2,y-h/2,z+d/2),point(x+w/2,y-h/2,z+d/2),point(x+w/2,y+h/2,z+d/2),point(x-w/2,y+h/2,z+d/2)]
        for f in [[0,3,2,1],[4,5,6,7],[0,4,7,3],[1,2,6,5],[3,7,6,2],[0,1,5,4]] { quad(v[f[0]],v[f[1]],v[f[2]],v[f[3]],ink,detail) }
    }
    func cylinder(_ x:Double,_ y:Double,_ z:Double,_ bottom:Double,_ top:Double,_ h:Double,_ ink:UInt32,sides:Int=12,detail:Bool=false) {
        for i in 0..<sides {
            let a=Double(i)*2 * Double.pi/Double(sides),b=Double(i+1)*2 * Double.pi/Double(sides)
            let v0=point(x+cos(a)*bottom,y-h/2,z+sin(a)*bottom),v1=point(x+cos(b)*bottom,y-h/2,z+sin(b)*bottom)
            let v2=point(x+cos(b)*top,y+h/2,z+sin(b)*top),v3=point(x+cos(a)*top,y+h/2,z+sin(a)*top)
            func normal(_ angle:Double)->SIMD3<Float> {
                let a=Float(angle)-yaw
                return simd_normalize(SIMD3(cos(a),Float((bottom-top)/max(h,0.001)),sin(a)))
            }
            for (v,n) in [([v0,v3,v2],[normal(a),normal(a),normal(b)]),([v0,v2,v1],[normal(a),normal(b),normal(b)])] {
                near.triangle(v[0],v[1],v[2],ink,smooth:n)
                if !detail { far.triangle(v[0],v[1],v[2],ink,smooth:n) }
            }
            tri(point(x,y+h/2,z),v2,v3,ink,detail)
            tri(point(x,y-h/2,z),v0,v1,ink,detail)
        }
    }
    func dome(_ x:Double,_ y:Double,_ z:Double,_ rx:Double,_ ry:Double,_ rz:Double,_ ink:UInt32,sides:Int=16,detail:Bool=false) {
        let rings=sides>=16 ? 8:4
        for j in 0..<rings { for i in 0..<sides {
            let a=Double(i)*2 * Double.pi/Double(sides),b=Double(i+1)*2 * Double.pi/Double(sides)
            let p=Double(j)/Double(rings)*Double.pi/2,q=Double(j+1)/Double(rings)*Double.pi/2
            let v0=point(x+cos(a)*cos(p)*rx,y+sin(p)*ry,z+sin(a)*cos(p)*rz)
            let v1=point(x+cos(b)*cos(p)*rx,y+sin(p)*ry,z+sin(b)*cos(p)*rz)
            let v2=point(x+cos(b)*cos(q)*rx,y+sin(q)*ry,z+sin(b)*cos(q)*rz)
            let v3=point(x+cos(a)*cos(q)*rx,y+sin(q)*ry,z+sin(a)*cos(q)*rz)
            let center=point(x,y,z)
            func normal(_ v:SIMD3<Float>)->SIMD3<Float> {
                let delta=v-center,c=cos(yaw),s=sin(yaw)
                let local=SIMD3(delta.x*c-delta.z*s,delta.y,delta.x*s+delta.z*c)
                let n=simd_normalize(local/SIMD3(Float(rx*rx),Float(ry*ry),Float(rz*rz)))
                return SIMD3(n.x*c+n.z*s,n.y,-n.x*s+n.z*c)
            }
            for vertices in [[v0,v3,v2],[v0,v2,v1]] {
                let normals=vertices.map{normal($0)}
                near.triangle(vertices[0],vertices[1],vertices[2],ink,smooth:normals)
                if !detail { far.triangle(vertices[0],vertices[1],vertices[2],ink,smooth:normals) }
            }
        }}
    }
}
