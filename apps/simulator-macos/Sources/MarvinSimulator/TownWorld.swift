import AppKit
import SceneKit
import SimulationCore
import simd

/// A compact, deterministic desert port. Static geometry is baked into spatial
/// cells with two detail levels; matching collision proxies use a spatial index.
final class TownWorld {
    let root = SCNNode()
    fileprivate let collisionBuilder=TownCollisionBuilder()
    private var stormActive=false
    private var absentPeople=Set<Int>()
    private lazy var clearCollisions=CityCollisionWorld(collisionBuilder.bodies)
    private lazy var stormCollisions=CityCollisionWorld(collisionBuilder.bodies.enumerated().filter{!absentPeople.contains($0.offset)}.map{$0.element})
    var collisionWorld:CityCollisionWorld { (stormActive ? stormCollisions:clearCollisions).withDynamicBodies((residents?.bodies ?? [])+(streetResidents?.bodies ?? [])) }
    private(set) var doorways:[TownDoorway]=[]
    private(set) var residents:TownResidents?
    struct StreetActivity {
        let position:SIMD2<Double>, target:SIMD2<Double>, role:String, group:Int, index:Int
        var yaw:Double { atan2(target.x-position.x,target.y-position.y) }
    }
    private var pendingActivities:[[StreetActivity]]=[]
    private(set) var streetActivities:[StreetActivity]=[]
    private var walkingCount=0
    private var walkingStreets:[[SIMD2<Double>]]=[]
    private(set) var streetResidents:TownStreetResidents?
    var visiblePopulation:Int { (stormActive ? crowd.stormPopulation:population-walkingCount)+(residents?.visible ?? 0)+(streetResidents?.visible ?? 0) }
    func setStorm(_ active:Bool) { stormActive=active;crowd.setStorm(active);residents?.setStorm(active);streetResidents?.setStorm(active) }
    private let surface = CityMaterials.plaster
    private let crowd = TownCrowd()
    private let signs = TownSigns()
    private var storefrontSigns=0
    private var cells: [String: TownCell] = [:]
    private var explorationCells: [String: TownCell] = [:]
    private var explorationNodes: [SCNNode] = []
    private var architectureNodes:[SCNNode]=[]
    var shadowDirections:[SIMD3<Double>]=[]
    var shadowCullingEnabled=CommandLine.arguments.contains("--benchmark-shadow-culling")
    private var shadowProxyDirections:[SIMD3<Double>]=[]
    private var priorShadowFrusta:[ShadowFrustum]=[]
    private var shadowVolumes:[ObjectIdentifier:[ShadowBounds]]=[:]
    private(set) var shadowCasterCount=0
    private func prepareShadowVolumes() {
        guard shadowDirections != shadowProxyDirections else { return }
        shadowProxyDirections=shadowDirections;shadowVolumes.removeAll()
        guard shadowDirections.count==2,shadowDirections.allSatisfy({ $0.x.isFinite && $0.y.isFinite && $0.z.isFinite && $0.y>0.01 }) else { return }
        for node in architectureNodes {
            let allBounds=[node.boundingBox]+(node.geometry?.levelsOfDetail ?? []).compactMap { $0.geometry?.boundingBox }
            var low=SIMD3<Double>(repeating:.infinity),high=SIMD3<Double>(repeating:-.infinity)
            for bounds in allBounds { for x in [bounds.min.x,bounds.max.x] { for y in [bounds.min.y,bounds.max.y] { for z in [bounds.min.z,bounds.max.z] {
                let p=node.convertPosition(SCNVector3(x,y,z),to:nil)
                let point=SIMD3(Double(p.x),Double(p.y),Double(p.z))
                low=simd_min(low,point);high=simd_max(high,point)
            }}}}
            guard (0..<3).allSatisfy({low[$0].isFinite && high[$0].isFinite && high[$0]>=low[$0]}) else { continue }
            // One metre around the caster exceeds the current 116m/2048 map's
            // 2–3 texel filter footprint. Expand BEFORE low-sun projection.
            low -= SIMD3(repeating:1);high += SIMD3(repeating:1)
            var volumes:[ShadowBounds]=[]
            for sun in shadowDirections {
                var shadowLow=low,shadowHigh=high
                for point in ShadowBounds(low:low,high:high).corners {
                    let end=point-sun*(max(0,point.y+2)/sun.y)
                    shadowLow=simd_min(shadowLow,end);shadowHigh=simd_max(shadowHigh,end)
                }
                volumes.append(ShadowBounds(low:shadowLow,high:shadowHigh))
            }
            shadowVolumes[ObjectIdentifier(node)]=volumes
        }
    }
    private(set) var explorationTriangles = 0
    var explorationDetailEnabled = true // native benchmark comparison only
    var activeExplorationCells:Int { explorationNodes.filter { !$0.isHidden }.count }
    func updateExplorationDetail(camera:SCNVector3,player:SIMD2<Double>,frusta:[ShadowFrustum]=[]) {
        if shadowCullingEnabled { prepareShadowVolumes() }
        shadowCasterCount=0
        // A street-level view outside the circuit gets a moving detail window.
        // Neither the racing cameras nor the locked finish overview needs it.
        // Shadow casters follow the player into town. Keeping only the original
        // race-side cells enabled left every outer courtyard unshadowed.
        let focus = camera.y>=12 ? SIMD2(Double(camera.x),Double(camera.z)+33):player
        let outside=max(abs(focus.x),abs(focus.y))>28
        for node in architectureNodes {
            let p=node.position
            var enabled=outside ? hypot(Double(p.x)-focus.x,Double(p.z)-focus.y)<75:(abs(p.x)<48 && abs(p.z)<48)
            if enabled,shadowCullingEnabled,!frusta.isEmpty,let volumes=shadowVolumes[ObjectIdentifier(node)] { enabled=volumes.contains { volume in frusta.contains { $0.intersects(volume) } } }
            if enabled { shadowCasterCount += 1 }
            if node.castsShadow != enabled { node.castsShadow=enabled }
        }
        let exploring=explorationDetailEnabled && max(abs(focus.x),abs(focus.y))>28 && camera.y<60
        for node in explorationNodes {
            let distance=hypot(Double(camera.x-node.position.x),Double(camera.z-node.position.z))
            let amount=exploring ? max(0,min(1,(58-distance)/16)):0
            node.isHidden=amount==0
            if amount>0 { node.opacity=CGFloat(amount*amount*(3-2*amount)) }
        }
    }
    private(set) var buildings = 0
    private(set) var population = 0
    private(set) var householdYards:[SIMD2<Double>]=[]
    private var spectatorZones:[String:SpectatorSoundZone]=[:]
    var spectatorSoundZones:[SpectatorSoundZone] { spectatorZones.keys.sorted().map{spectatorZones[$0]!} }
    /// Acoustic emitters come from the same authored venues and visible activities.
    lazy var soundZones:[TownSoundZone] = {
        var zones=venueSites.enumerated().map { i,site in
            TownSoundZone(position:site.center,kind:i==0 ? .workshop:i==1 ? .cantina:.market)
        }
        zones += InfieldLayout.tentOrigins.map { TownSoundZone(position:$0,kind:.workshop,activity:0.65,infieldRepair:true) }
        let groups=Dictionary(grouping:streetActivities,by:{$0.group})
        for key in groups.keys.sorted() {
            let group=groups[key]!
            guard let first=group.first,group.count>1 else { continue }
            if first.role.contains("market") || first.role.contains("conversation") {
                zones.append(TownSoundZone(position:first.position,kind:.market,activity:0.22))
            }
        }
        return zones
    }()
    private(set) var triangleCount = 0
    private(set) var coarseTriangles = 0
    private(set) var lots: [TownLot] = []
    private(set) var mapBuildings:[[SIMD2<Double>]]=[]
    private var cameraBounds: [(SIMD3<Double>,SIMD3<Double>)] = []
    private var clock = 0.0
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

    init(progress:((Double,String)->Void)? = nil) {
        root.name = "Mos Aster desert spaceport"
        progress?(0.02,"Laying out town streets")
        buildRoads()
        progress?(0.08,"Building spectator stands")
        buildGrandstand()
        progress?(0.12,"Building landmarks")
        buildLandmarks()
        buildDistrictPlaces()
        progress?(0.14,"Building town districts")
        buildSettlement()
        progress?(0.38,"Preparing the repair yard")
        buildRepairPit()
        progress?(0.48,"Adding spectators")
        buildStreetLife()
        progress?(0.54,"Adding town details")
        buildMarketDetails()
        progress?(0.59,"Weathering the town")
        buildReferenceDetails()
        progress?(0.64,"Placing signs")
        buildWayfinding()
        buildNeighborhoodUtilities()
        buildDoorstepLife()
        buildDomesticCourts()
        buildHouseholdYards()
        refreshPedestrianAccess()
        root.addChildNode(TownGround.build(access:pedestrianAccess+venueAccess,yards:householdYards))
        placeStreetActivities()
        progress?(0.66,"Connecting residents to their homes")
        residents=TownResidents(doors:doorways,city:clearCollisions,crowd:crowd,root:root,count:10)
        population -= walkingCount-(residents?.walkers.count ?? 0)
        walkingCount=residents?.walkers.count ?? 0
        streetResidents=TownStreetResidents(paths:walkingStreets,city:clearCollisions,crowd:crowd,root:root)
        population += streetResidents?.walkers.count ?? 0
        walkingCount += streetResidents?.walkers.count ?? 0
        crowd.finish(into:root)
        let keys=cells.keys.sorted()
        for (index,key) in keys.enumerated() {
            progress?(0.70+0.30*Double(index)/Double(keys.count),"Preparing the town")
            let cell = cells[key]!
            let near = cell.near.geometry(material: surface, relativeTo: cell.origin)
            let far = cell.far.geometry(material: surface, relativeTo: cell.origin)
            near.levelsOfDetail = [SCNLevelOfDetail(geometry: far, worldSpaceDistance: 82)]
            let node = SCNNode(geometry: near)
            node.simdPosition = cell.origin; node.name = "Town cell \(key)"
            // Initial racing footprint; exploration moves the bounded caster set.
            node.castsShadow = abs(cell.origin.x) < 48 && abs(cell.origin.z) < 48
            architectureNodes.append(node);root.addChildNode(node)
            triangleCount += cell.near.indices.count / 3
            coarseTriangles += cell.far.indices.count / 3
        }
        for key in explorationCells.keys.sorted() {
            let cell=explorationCells[key]!
            let node=SCNNode(geometry:cell.near.geometry(material:surface,relativeTo:cell.origin))
            node.simdPosition=cell.origin;node.name="Exploration detail \(key)"
            node.castsShadow=false;node.isHidden=true
            explorationTriangles += cell.near.indices.count/3
            explorationNodes.append(node);root.addChildNode(node)
        }
        // CPU mesh builders are no longer needed once the GPU buffers exist.
        explorationCells.removeAll()

    }

    private func explorationPainter(_ x:Double,_ z:Double,yaw:Double)->TownPainter {
        let ix=Int(floor(x/16)),iz=Int(floor(z/16)),key="\(ix),\(iz)"
        let cell=explorationCells[key] ?? TownCell(Float(ix*16+8),Float(iz*16+8))
        explorationCells[key]=cell
        return TownPainter(near:cell.near,far:cell.far,origin:SIMD3(Float(x),0,Float(z)),yaw:Float(yaw))
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
        return TownPainter(near: c.near, far: c.far, origin: SIMD3(Float(x),0,Float(z)), yaw: Float(yaw),collisions:collisionBuilder)
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
        let kind:Int
        let path:[SIMD2<Double>]
        init(points:[SIMD2<Double>],width:Double,kind:Int=0) {
            self.points=points;self.width=width;self.kind=kind
            // Interpolating Hermite curves retain junctions while easing changes
            // of direction. Tangents are limited by the shorter adjacent block.
            let tangents=points.indices.map { i -> SIMD2<Double> in
                if i==0 { return points[1]-points[0] }
                if i==points.count-1 { return points[i]-points[i-1] }
                let incoming=points[i]-points[i-1],outgoing=points[i+1]-points[i]
                return simd_normalize(simd_normalize(incoming)+simd_normalize(outgoing))*min(simd_length(incoming),simd_length(outgoing))
            }
            var samples:[SIMD2<Double>]=[]
            for i in 0..<points.count-1 {
                let count=max(8,Int(ceil(simd_length(points[i+1]-points[i])/0.9)))
                for j in 0..<count {
                    let t=Double(j)/Double(count),t2=t*t,t3=t2*t
                    samples.append(points[i]*(2*t3-3*t2+1)+tangents[i]*(t3-2*t2+t)+points[i+1]*(-2*t3+3*t2)+tangents[i+1]*(t3-t2))
                }
            }
            path=samples+[points.last!]
        }
    }
    // Destination-led arteries: market approach, dock access and northern trade
    // route. None forms a closed perimeter around the race.
    var mapStreets:[[SIMD2<Double>]] { streets.map{$0.path} }
    private let streets:[Street] = [
        // Broad shared sandy streets; the gaps between compounds form local
        // passages. Do not clear residential loops or individual driveways.
        Street(points:[SIMD2(-150,-76),SIMD2(-64,-39),SIMD2(-35,-29),SIMD2(-15,-27),SIMD2(0,-26),SIMD2(18,-31),SIMD2(45,-45),SIMD2(145,-68)],width:3.8),
        Street(points:[SIMD2(45,-45),SIMD2(35,-25),SIMD2(28,-10),SIMD2(33,3),SIMD2(35,14)],width:4.2),
        Street(points:[SIMD2(35,14),SIMD2(36,30),SIMD2(18,36),SIMD2(-8,32),SIMD2(-30,39),SIMD2(-64,58),SIMD2(-145,80)],width:3.6),
        Street(points:[SIMD2(-145,-5),SIMD2(-62,-8),SIMD2(-39,-15),SIMD2(-35,-29)],width:2.4,kind:1),
        Street(points:[SIMD2(36,30),SIMD2(60,48),SIMD2(105,42),SIMD2(150,52)],width:4.0),
        Street(points:[SIMD2(-64,58),SIMD2(-48,92),SIMD2(-65,150)],width:2.0,kind:1),
        // Secondary streets connect districts, not individual driveways. Keep
        // the established circuit-side fabric and all six original arteries.
        Street(points:[SIMD2(-64,-39),SIMD2(-84,-20),SIMD2(-92,10),SIMD2(-80,37),SIMD2(-64,58)],width:2.3,kind:1),
        Street(points:[SIMD2(-64,-39),SIMD2(-67,-57),SIMD2(-42,-64),SIMD2(-12,-78),SIMD2(20,-72),SIMD2(55,-67),SIMD2(81,-42),SIMD2(78,-14),SIMD2(65,17),SIMD2(60,48)],width:2.8,kind:1),
        Street(points:[SIMD2(-48,92),SIMD2(-20,86),SIMD2(11,69),SIMD2(40,80),SIMD2(69,69),SIMD2(60,48)],width:2.5,kind:1),
        Street(points:[SIMD2(-84,-20),SIMD2(-111,-40),SIMD2(-102,-68),SIMD2(-72,-87),SIMD2(-42,-64)],width:2.0,kind:1),
        Street(points:[SIMD2(20,-72),SIMD2(18,-102),SIMD2(51,-112),SIMD2(79,-93),SIMD2(81,-42)],width:2.1,kind:1),
        Street(points:[SIMD2(105,42),SIMD2(113,17),SIMD2(104,-10),SIMD2(78,-14)],width:2.2,kind:1),
        Street(points:[SIMD2(-80,37),SIMD2(-111,55),SIMD2(-100,78),SIMD2(-48,92)],width:1.8,kind:1)
    ]


    // Native visual/performance survey follows the actual eastbound street.
    func explorationSurveyPoint(_ fraction:Double)->SIMD2<Double> {
        let path=streets[4].path,t=max(0,min(1,fraction))*Double(path.count-1)
        let i=min(path.count-2,Int(t)),blend=t-Double(i)
        return path[i]*(1-blend)+path[i+1]*blend
    }
    private var preservingTrackside=false
    private func streetDistance(_ x:Double,_ z:Double)->Double {
        let p=SIMD2(x,z)
        var distance=Double.greatestFiniteMagnitude
        for street in (preservingTrackside ? Array(streets.prefix(6)):streets) { for i in 1..<street.path.count {
            let a=street.path[i-1],d=street.path[i]-a
            let t=max(0,min(1,simd_dot(p-a,d)/simd_length_squared(d)))
            distance=min(distance,simd_length(p-a-d*t)-street.width/2)
        }}
        return distance
    }
    private func drawStreets(_ routes:[Street],name:String) {
        let mesh=TownMesh()
        for street in routes {
            // One continuous ground ribbon, with soft shoulders. No overlapping
            // rectangular slabs or ruler-straight parallel cart-track markings.
            let shoulder=0.6
            let widths=[-street.width/2-shoulder,-street.width*0.30,street.width*0.30,street.width/2+shoulder]
            let ink:UInt32=street.kind==0 ? 0x9d8d75:0xa99a81
            let opacity:Float=street.kind==0 ? 0.44:0.23
            let normals=street.path.indices.map { i -> SIMD2<Double> in
                let tangent=simd_normalize(street.path[min(i+1,street.path.count-1)]-street.path[max(0,i-1)])
                return SIMD2(-tangent.y,tangent.x)
            }
            for i in 1..<street.path.count {
                for band in 0..<widths.count-1 {
                    func vertex(_ j:Int,_ edge:Int)->SIMD3<Float> {
                        let center=street.path[j]
                        let irregular=1+0.07*sin(center.x*0.63+center.y*0.31)+0.04*sin(center.y*1.43)
                        let p=center+normals[j]*widths[edge]*irregular
                        return SIMD3(Float(p.x),Float(-0.016+Double(street.kind)*0.001),Float(p.y))
                    }
                    let a=vertex(i-1,band),b=vertex(i,band),c=vertex(i,band+1),d=vertex(i-1,band+1)
                    let first=mesh.colors.count/4
                    mesh.triangle(a,c,b,ink);mesh.triangle(a,d,c,ink)
                    let left:Float=band==0 ? 0:opacity
                    let right:Float=band==widths.count-2 ? 0:opacity
                    for (j,alpha) in [left,right,left,left,right,right].enumerated() {
                        let v=[a,c,b,a,d,c][j],p=SIMD2(Double(v.x),Double(v.z))
                        var fade=1.0
                        for end in [street.path.first!,street.path.last!] where TownFootprint.edgeDistance(end)>0 {
                            let t=min(1,simd_distance(p,end)/14)
                            fade *= t*t*(3-2*t)
                        }
                        mesh.colors[(first+j)*4+3]=alpha*Float(fade)
                    }
                }
            }
        }
        let material=SCNMaterial()
        material.lightingModel = .physicallyBased
        material.diffuse.contents=NSColor.white;material.roughness.contents=1.0
        material.transparencyMode = .aOne
        material.writesToDepthBuffer=false
        material.shaderModifiers=[.geometry:"""
        #pragma varyings
        half4 streetTint;
        #pragma body
        out.streetTint=half4(half3(pow(max(_geometry.color.rgb,float3(0)),float3(2.2))),half(_geometry.color.a));
        """,.surface:"_surface.diffuse.rgb=float3(in.streetTint.rgb);",.fragment:TownGround.pigmentFunctions + "\n" + """
        #pragma transparent
        #pragma body
        float2 p=(scn_frame.inverseViewTransform*float4(_surface.position,1.0)).xz;
        float angle=atan2(p.y,p.x);
        float radius=130.0+13.0*sin(3.0*angle+0.4)+9.0*cos(5.0*angle-0.7)+6.0*sin(2.0*angle);
        float edge=length(p)-radius;
        float exposure=smoothstep(-18.0,22.0,edge);
        // Oblique wind-driven tongues eat through the road at different widths;
        // exposed remnants become smaller until the underlying sand covers all.
        float2 wind=float2(p.x*0.86+p.y*0.51,-p.x*0.51+p.y*0.86);
        float tongues=townNoise(wind/float2(2.8,0.75));
        float broken=smoothstep(0.28,0.72,tongues*0.65+exposure*0.70);
        float remaining=(1.0-smoothstep(-4.0,22.0,edge))*(1.0-exposure*broken);
        float alpha=float(in.streetTint.a)*remaining;
        _output.color.rgb *= alpha;
        _output.color.a=alpha;
        """]
        let road=SCNNode(geometry:mesh.geometry(material:material))
        road.name=name;road.castsShadow=false;root.addChildNode(road)
    }
    private func buildRoads() {
        drawStreets(streets,name:"Worn shared sandy streets")
        let plaza=paint(0,finishZ-7.0)
        plaza.box(0,-0.006,0,19,0.018,7.0,0xab9679)
    }

    private func buildGrandstand() {
        let z = finishZ-4.9, p = paint(0,z)
        // Shops form the plinth, with seating rising toward the back of town.
        // Structural arcade: real recessed bays, not dark rectangles on a solid box.
        p.box(0,0.68,-2.36,12.8,1.36,0.30,0xb49d7e)
        p.box(0,1.34,-0.65,12.8,0.18,3.8,0xc7b18b)
        p.box(0,0.04,-0.65,12.8,0.08,3.8,0x8a7964)
        p.box(0,0.12,0.9,13.2,0.24,1.0,cream)
        for (bay,x) in stride(from:-5.5,through:5.5,by:1.85).enumerated() {
            p.arcade(x,0.05,1.22,1.44,1.22,0.42,0.17,0xc5b08b)
            p.box(x,0.61,0.32,1.42,1.06,0.08,0x51493d,detail:true)
            p.box(x,0.16,1.36,1.54,0.15,0.47,0xa9977b)
            p.awning(x,1.66,1.64,1.02,1.46,bay%2==0 ? 0xad9671:0x758680)
            for sx in [-0.77,0.77] {
                p.beam(SIMD3(x+sx,0.12,2.06),SIMD3(x+sx,1.44,2.06),0.025,0x5e5548,sides:6)
                p.beam(SIMD3(x+sx,1.42,2.06),SIMD3(x+sx,1.57,1.10),0.017,0x746554,sides:6)
            }
            // Joinery, open shelves, pots and supply cases are visible in the alcove.
            for shelf in [0.42,0.85] {
                p.box(x,shelf,0.73,1.2,0.055,0.46,0x77634c,detail:true)
                for k in 0..<4 {
                    let xx=x-0.43+Double(k)*0.28
                    if (k+bay)%2==0 { p.cylinder(xx,shelf+0.12,0.74,0.08,0.10,0.22,0xab8c64,sides:10,detail:true) }
                    else { p.box(xx,shelf+0.12,0.74,0.19,0.23,0.20,0x64756c,detail:true) }
                }
            }
            p.plasterPatch(x-0.88,0.38,1.448,0.08,0.27,0x927f64,seed:bay)
        }
        for row in 0..<5 {
            let y=1.4+Double(row)*0.31, rz=0.72-Double(row)*0.56
            p.box(0,y-0.12,rz,12.6,0.24,0.58,cream)
            p.box(0,y+0.06,rz-0.12,12.1,0.12,0.25,rust,detail:true)
            for seat in 0..<22 where seat != 10 && seat != 11 && (seat*7+row*11)%13>1 {
                let x = -5.7+Double(seat)*0.54+sin(Double(seat*17+row))*0.035
                let turn=sin(Double(seat*7+row*13))*0.23
                citizen(x,z+rz+cos(Double(seat*11))*0.035,y:y+0.12,yaw:turn,index:row*22+seat,seated:true)
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
        for (i,x) in [-3.45,3.45].enumerated() {
            p.canopy(x,-1.12,5.65,2.8,3.83,0.32,i==0 ? 0xb6a17e:0x9eaa9b)
            p.valance(x,0.285,5.65,3.83,0.23,i==0 ? 0xb6a17e:0x9eaa9b,rise:0.32)
            for edge in [-2.8,2.8] {
                p.cable(SIMD3(x+edge,3.83,0.28),SIMD3(x+edge,2.85,0.28),0.018,0x766653)
            }
            // Tension seams, reinforced hems and roof battens follow the cloth.
            for xx in [-1.85,0,1.85] {
                p.beam(SIMD3(x+xx,3.79,-2.52),SIMD3(x+xx,3.79,0.27),0.025,0x75634e,sides:6)
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
            let q=paint(x,finishZ-5.5)
            q.adobe(0,1.75,0,2.4,3.5,3.6,sand)
            for side in [-1.0,1] { q.door(0,side*1.81,0.70,1.35,dark,sand,side:side) }
            q.cylinder(0,4.45,0,0.85,0.62,1.8,cream)
            q.dome(0,5.315,0,0.72,0.55,0.72,sand)
            q.box(0,4.55,0.72,0.45,0.65,0.30,sand,detail:true)
            q.box(0,4.55,0.88,0.27,0.42,0.025,dark,detail:true)
            for j in 0..<4 { q.box(0,4.41+Double(j)*0.09,0.90,0.30,0.025,0.035,trim,detail:true) }
            q.box(0,6.1,0,0.055,0.85,0.055,dark,detail:true)
        }
        signs.plate("MOS ASTER GRAND PRIX",eyebrow:"INTERPLANETARY RACING SERIES",footer:"START  /  FINISH",badge:"07",
                    at:SCNVector3(0,1.54,z+2.10),width:5.7,height:0.68,into:root)
        for (i,x) in [-8.5,8.5].enumerated() {
            signs.plate("GATE \(i+1)",eyebrow:"GRANDSTAND",footer:i==0 ? "SECTORS A / B":"SECTORS C / D",badge:i==0 ? "A":"C",
                        at:SCNVector3(x,2.37,finishZ-3.64),width:1.78,height:0.58,accent:teal,into:root)
        }
    }

    struct Entrance {
        let center:SIMD2<Double>,yaw:Double,width:Double,height:Double,variant:Int,walkable:Bool
    }
    private(set) var entrances:[Entrance]=[]
    private(set) var windowSupports:[SIMD3<Double>]=[]
    struct PedestrianAccess {
        let building:SIMD2<Double>,door:SIMD2<Double>,route:[SIMD2<Double>]
    }
    private(set) var pedestrianAccess:[PedestrianAccess]=[]
    private(set) var inaccessibleBuildings:[SIMD2<Double>]=[]
    private(set) var venueAccess:[PedestrianAccess]=[]
    private var plannedDoors:[Int:DoorPlan]=[:]
    private var accessMap:TownAccessMap?
    private struct Facade { let x:Double,z:Double,turn:Double,span:Double,height:Double }
    private struct DoorPlan { let facade:Facade,offset:Double,width:Double,height:Double,variant:Int }
    private func compoundFacades(w:Double,d:Double,h:Double,style:Int)->[Facade] {
        var faces=[Facade]()
        func block(_ cx:Double,_ cz:Double,_ width:Double,_ depth:Double,_ height:Double, sides:[Int]) {
            for side in sides {
                let turn=Double(side)*Double.pi/2
                let half=side%2==0 ? depth/2:width/2
                faces.append(Facade(x:cx+sin(turn)*half,z:cz+cos(turn)*half,turn:turn,span:side%2==0 ? width:depth,height:height))
            }
        }
        if style<3 {
            block(-w*0.17,0,w*0.66,d,h*0.86,sides:[0,2,3])
            block(w*0.25,d*0.10,w*0.50,d*0.79,h*0.64,sides:[0,1])
        } else if style==6 {
            block(-w*0.34,0,w*0.32,d,h*0.84,sides:[0,2,3])
            block(w*0.40,d*0.07,w*0.20,d*0.69,h*0.50,sides:[1])
        } else if style==8 {
            block(-w*0.23,-d*0.05,w*0.54,d*0.90,h,sides:[0,2,3])
            block(w*0.26,0,w*0.47,d,h*0.58,sides:[0,1,2])
        } else { block(0,0,w,d,style==5 ? max(1.6,h*0.44):h*0.60,sides:[0,1,2,3]) }
        return faces
    }
    private func entrancePlan(_ x:Double,_ z:Double,w:Double,d:Double,h:Double,index:Int,yaw:Double,style:Int)->DoorPlan? {
        let faces=compoundFacades(w:w,d:d,h:h,style:style)
        var best:(Double,DoorPlan)?
        let seed=UInt32(truncatingIfNeeded:index &* 747796405 &+ 2891336453)
        let hash=Int((seed ^ (seed>>16)) & 0x7fffffff)
        for (f,face) in faces.enumerated() {
            let variant=(hash/31+f)%5
            let width=min(face.span-0.44,[0.78,1.03,0.88,1.32,0.96][variant])
            let height=min(face.height-0.18,[1.45,1.38,1.62,1.48,1.36][variant])
            guard width>=0.65,height>=1.12 else { continue }
            for slot in 0..<5 {
                let offset=(Double(slot)-2)/2*max(0,(face.span-width)/2-0.55)
                let lx=face.x+cos(face.turn)*offset,lz=face.z-sin(face.turn)*offset
                let center=SIMD2(x+cos(yaw)*lx+sin(yaw)*lz,z-sin(yaw)*lx+cos(yaw)*lz)
                let out=SIMD2(sin(yaw+face.turn),cos(yaw+face.turn))
                guard let distance=accessMap?.streetDistance(center+out*0.55),
                      accessMap?.streetDistance(center+out*0.65) != nil else { continue }
                // Prefer a short public approach, but stagger neighbouring entries.
                let score=distance+Double((slot+hash)%5)*0.32
                if best==nil || score<best!.0 { best=(score,DoorPlan(facade:face,offset:offset,width:width,height:height,variant:variant)) }
            }
        }
        return best?.1
    }
    private var citySeed:UInt64 = 0xA57E2026
    private func random()->Double {
        citySeed = citySeed &* 6364136223846793005 &+ 1442695040888963407
        return Double(citySeed >> 32)/Double(UInt32.max)
    }
    private func reserved(_ x:Double,_ z:Double,_ w:Double,_ d:Double,checkStreet:Bool=true)->Bool {
        if venueSites.contains(where:{abs(x-$0.center.x)<$0.halfWidth+w/2+1.1 && abs(z-$0.center.y)<$0.halfDepth+d/2+1.1}) { return true }
        if infield(x,z) || !clearLot(x,z,w,d) || CityExit.reserved(SIMD2(x,z),radius:hypot(w,d)/2) { return true }
        if checkStreet && streetDistance(x,z)<hypot(w,d)*0.44 { return true }
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
        var compounds:[(Double,Double,Double,Double,Double,Int,Double)]=[]
        func reserve(_ x:Double,_ z:Double,_ w:Double,_ d:Double,_ h:Double,_ i:Int,_ yaw:Double) {
            compounds.append((x,z,w,d,h,i,yaw))
            lots.append(TownLot(x:x,z:z,width:w*abs(cos(yaw))+d*abs(sin(yaw))+0.2,depth:d*abs(cos(yaw))+w*abs(sin(yaw))+0.2))
        }
        preservingTrackside=true
        var z = -145.0
        // Reproduce the established core exactly, including its random stream
        // and infill clearance, before replacing only the outer districts.
        while z<145 {
            let stepZ=6.4+random()*2.4
            var x = -150.0+random()*6
            while x<150 {
                let stepX=5.8+random()*3.6
                let bx=x+stepX/2,bz=z+stepZ/2+sin(Double(row)*1.9+Double(index)*0.67)*0.55
                let w=stepX-0.38,d=stepZ-0.42
                let yaw=(random()-0.5)*0.16
                let boundW=w*abs(cos(yaw))+d*abs(sin(yaw)),boundD=d*abs(cos(yaw))+w*abs(sin(yaw))
                let h=2.0+random()*3.8
                if !reserved(bx,bz,boundW+0.2,boundD+0.2) {
                    reserve(bx,bz,w,d,h,index,yaw)
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
            reserve(x,z,w,d,1.8+random()*1.5,1400+i+j*16,0)
        }}
        compounds=compounds.filter{max(abs($0.0),abs($0.1))<56}.map { x,z,w,d,h,i,yaw in
            // Keep trackside positions and silhouettes, but open pedestrian
            // lanes within the formerly impenetrable residential blocks.
            let setback=w>4.5 && d>4.5 ? 1.5:0.0
            return (x,z,w-setback,d-setback,max(h,2.35),i,yaw)
        }
        func updateLots() {
            lots=compounds.map { x,z,w,d,_,_,yaw in
                TownLot(x:x,z:z,width:w*abs(cos(yaw))+d*abs(sin(yaw))+0.2,depth:d*abs(cos(yaw))+w*abs(sin(yaw))+0.2)
            }
        }
        updateLots()
        preservingTrackside=false
        // Outside the protected trackside core, grow street-facing blocks by
        // deterministic dart throwing. Variable setbacks and usable passages
        // replace nearly touching rectangular rows. Wider civic courts break
        // dense neighborhoods, and density tapers at the irregular urban edge.
        let courts:[(SIMD2<Double>,Double)]=[(SIMD2(-84,-20),6),(SIMD2(-42,-64),5),
            (SIMD2(55,-67),7),(SIMD2(60,48),7),(SIMD2(-20,86),5),(SIMD2(-80,37),6)]
        func frontage(_ p:SIMD2<Double>)->(distance:Double,yaw:Double) {
            var best=Double.infinity,yaw=0.0
            for street in streets { for i in stride(from:1,to:street.path.count,by:3) {
                let a=street.path[max(0,i-3)],d=street.path[i]-a
                let t=max(0,min(1,simd_dot(p-a,d)/max(0.0001,simd_length_squared(d))))
                let distance=simd_length(p-a-d*t)-street.width/2
                if distance<best { best=distance;yaw = -atan2(d.y,d.x) }
            }}
            return (best,yaw)
        }
        func place(_ x:Double,_ z:Double,_ w:Double,_ d:Double,_ h:Double,_ yaw:Double,_ i:Int,gap:Double=1.25) {
            let p=SIMD2(x,z),c=abs(cos(yaw)),s=abs(sin(yaw)),bw=w*c+d*s,bd=d*c+w*s
            if max(abs(x),abs(z))<56 || TownFootprint.edgeDistance(p) > -hypot(w,d)/2-1 { return }
            if courts.contains(where:{simd_distance(p,$0.0)<$0.1+hypot(w,d)/2}) { return }
            if reserved(x,z,bw+0.4,bd+0.4,checkStreet:false) { return }
            let u=SIMD2(cos(yaw),-sin(yaw)),v=SIMD2(sin(yaw),cos(yaw))
            // Test the real rotated footprint, not an inflated circumscribed
            // rectangle that pushes every street facade several metres away.
            for (a,b) in [(-1.0,-1.0),(1,-1),(-1,1),(1,1),(0,-1),(0,1),(-1,0),(1,0)] {
                let q=p+u*(a*w/2)+v*(b*d/2)
                if streetDistance(q.x,q.y)<0.45 { return }
            }
            for (xx,zz,ww,dd,_,_,angle) in compounds {
                let delta=p-SIMD2(xx,zz),ou=SIMD2(cos(angle),-sin(angle)),ov=SIMD2(sin(angle),cos(angle))
                if abs(delta.x)>(bw+ww*abs(ou.x)+dd*abs(ov.x))/2+gap || abs(delta.y)>(bd+ww*abs(ou.y)+dd*abs(ov.y))/2+gap { continue }
                let separated=[u,v,ou,ov].contains { axis in
                    abs(simd_dot(delta,axis)) > (w*abs(simd_dot(u,axis))+d*abs(simd_dot(v,axis))+ww*abs(simd_dot(ou,axis))+dd*abs(simd_dot(ov,axis)))/2+gap
                }
                if !separated { return }
            }
            reserve(x,z,w,d,h,i,yaw)
        }
        // Establish continuous but varied street frontage first. Small gaps
        // lead into courtyards and rear alleys, rather than isolated cottages.
        var frontageID=2000
        for street in streets {
            var distance=0.0,next=3.0
            for j in 1..<street.path.count {
                distance += simd_distance(street.path[j],street.path[j-1])
                if distance<next { continue }
                let tangent=simd_normalize(street.path[j]-street.path[j-1]),normal=SIMD2(-tangent.y,tangent.x)
                let w=4.8+random()*2.4,d=5.0+random()*2.5
                for side in [-1.0,1.0] {
                    let p=street.path[j]+normal*side*(street.width/2+d/2+0.8+random()*0.4)
                    place(p.x,p.y,w,d,2.35+random()*3.25,-atan2(tangent.y,tangent.x),frontageID);frontageID += 1
                }
                next=distance+w+0.8+random()*0.8
            }
        }
        for i in 0..<18000 {
            let x=(random()-0.5)*306,z=(random()-0.5)*306,p=SIMD2(x,z)
            if max(abs(x),abs(z))<56 || TownFootprint.edgeDistance(p) > -4 { continue }
            let w=3.4+random()*4.3,d=3.8+random()*4.0,h=2.35+random()*3.15,front=frontage(p)
            if front.distance>28 { continue }
            let yaw=front.yaw+(random()-0.5)*0.20
            place(x,z,w,d,h,yaw,5000+i)
        }
        // Smaller workshops occupy residual frontage pockets. This adds a
        // second scale of buildings without moving the established compounds
        // or sacrificing the pedestrian clearance between them.
        let beforeInfill=compounds.count
        for i in 0..<9000 {
            if compounds.count-beforeInfill>=64 { break }
            let x=(random()-0.5)*280,z=(random()-0.5)*280,p=SIMD2(x,z)
            if max(abs(x),abs(z))<58 || TownFootprint.edgeDistance(p) > -9 { continue }
            let front=frontage(p)
            if front.distance>16 { continue }
            let w=2.6+random()*1.2,d=3.0+random()*1.5
            place(x,z,w,d,2.7+random()*1.2,front.yaw+(random()-0.5)*0.08,24000+i,gap:0.95)
        }
        let landmarks=collisionBuilder.bodies.filter{$0.position.y<1.4 && $0.profile.mass != 70 && $0.profile.height>0.18}.map { body in
            TownAccessMap.Footprint(center:SIMD2(body.position.x,body.position.z),width:body.profile.halfWidth*2,depth:body.profile.halfDepth*2,yaw:body.heading)
        }
        func footprints()->[TownAccessMap.Footprint] {
            compounds.flatMap { x,z,w,d,_,i,yaw -> [TownAccessMap.Footprint] in
                let style=(i*13+i/7)%9
                var shapes:[(Double,Double,Double,Double)]
                if style<3 { shapes=[(-w*0.17,0,w*0.66,d),(w*0.25,d*0.1,w*0.50,d*0.79)] }
                else if style==6 { shapes=[(-w*0.34,0,w*0.32,d),(w*0.1,-d*0.31,w*0.78,d*0.38),(w*0.4,d*0.07,w*0.2,d*0.69)] }
                else if style==8 { shapes=[(-w*0.23,-d*0.05,w*0.54,d*0.90),(w*0.26,0,w*0.47,d)] }
                else { shapes=[(0,0,w,d)] }
                return shapes.map { xx,zz,ww,dd in
                    TownAccessMap.Footprint(center:SIMD2(x+xx*cos(yaw)+zz*sin(yaw),z-xx*sin(yaw)+zz*cos(yaw)),width:ww,depth:dd,yaw:yaw)
                }
            }
        }
        // Audit every compound before building it. If a block has trapped a
        // doorway, create more alley clearance at that footprint and replan.
        for iteration in 0..<6 {
            accessMap=TownAccessMap(footprints:footprints()+landmarks,streets:streets.map{$0.path})
            plannedDoors.removeAll();var missing=[Int]()
            for (j,compound) in compounds.enumerated() {
                let (x,z,w,d,h,i,yaw)=compound
                if let plan=entrancePlan(x,z,w:w,d:d,h:h,index:i,yaw:yaw,style:(i*13+i/7)%9) { plannedDoors[i]=plan }
                else { missing.append(j) }
            }
            if missing.isEmpty || iteration==5 { break }
            for j in missing {
                let (x,z,w,d,h,i,yaw)=compounds[j]
                compounds[j]=(x,z,max(2.3,w-0.45),max(2.3,d-0.45),max(2.6,h),i,yaw)
                if iteration>=2 {
                    for k in compounds.indices where k != j {
                        let (xx,zz,ww,dd,hh,ii,yy)=compounds[k]
                        if hypot(xx-x,zz-z)<(hypot(w,d)+hypot(ww,dd))/2+1.5 && ww>3 && dd>3 {
                            compounds[k]=(xx,zz,ww-0.35,dd-0.35,hh,ii,yy)
                        }
                    }
                }
            }
        }
        updateLots()
        mapBuildings=compounds.map { x,z,w,d,_,_,yaw in
            [SIMD2(-w/2,-d/2),SIMD2(w/2,-d/2),SIMD2(w/2,d/2),SIMD2(-w/2,d/2)].map { p in
                SIMD2(x+p.x*cos(yaw)+p.y*sin(yaw),z-p.x*sin(yaw)+p.y*cos(yaw))
            }
        }
        mapBuildings += venueSites.map { site in
            [SIMD2(-site.halfWidth,-site.halfDepth),SIMD2(site.halfWidth,-site.halfDepth),SIMD2(site.halfWidth,site.halfDepth),SIMD2(-site.halfWidth,site.halfDepth)].map { p in
                site.center+SIMD2(p.x*cos(site.yaw)+p.y*sin(site.yaw),-p.x*sin(site.yaw)+p.y*cos(site.yaw))
            }
        }
        for (x,z,w,d,h,i,yaw) in compounds {
            if let plan=plannedDoors[i] {
                let f=plan.facade,lx=f.x+cos(f.turn)*plan.offset,lz=f.z-sin(f.turn)*plan.offset
                let center=SIMD2(x+lx*cos(yaw)+lz*sin(yaw),z-lx*sin(yaw)+lz*cos(yaw))
                let out=SIMD2(sin(yaw+f.turn),cos(yaw+f.turn))
                if let route=accessMap?.routeToStreet(from:center+out*0.65) {
                    pedestrianAccess.append(PedestrianAccess(building:SIMD2(x,z),door:center,route:[center+out*0.42,center+out*0.6]+route))
                } else { inaccessibleBuildings.append(SIMD2(x,z)) }
            } else { inaccessibleBuildings.append(SIMD2(x,z)) }
            cityCompound(x,z,w:w,d:d,h:h,index:i,yaw:yaw)
        }
        print("Pedestrian access: \(pedestrianAccess.count)/\(compounds.count), inaccessible \(inaccessibleBuildings)")
        accessMap=nil // Planning data never participates in the render loop.
    }

    private func cityCompound(_ x:Double,_ z:Double,w:Double,d:Double,h:Double,index:Int,yaw:Double) {
        let near=max(abs(x),abs(z))<52
        var p=paint(x,z,yaw:yaw)
        buildings += 1
        let colors:[UInt32]=[0xb5a084,0xbdaa8c,0xc4ad8c,0xa58c70,0xc9b89b,0x9e8872,0xb7a890,0xc0a786]
        let chalk:[UInt32]=[0xc9bca5,0xd9ceba,0xb9aa95,0xd2c4a9,0xc5bcb0,0xae987e,0xded3bb,0xb8a58f]
        let palette=near ? colors:chalk
        let ink=palette[(index*7+index/11)%palette.count]
        let roof:UInt32=0x9e8970
        let style=(index*13+index/7)%9
        let plan=plannedDoors[index]
        let entrance=plan != nil && near && max(abs(x),abs(z))<43 && (style==3 || style==4 || style==7) && h*0.6>1.50 && doorways.count<18
        // Sun/contact shading seats the actual walls in sand; no rectangular
        // dark slab is stamped beneath an irregular compound.
        if style<3 {
            // Joined adobe dwelling + offset domed chamber; no individual plinth.
            p.adobe(-w*0.17,h*0.43,0,w*0.66,h*0.86,d,ink,simple:false)
            p.adobe(w*0.25,h*0.32,d*0.10,w*0.50,h*0.64,d*0.79,ink,simple:false)
            let r=min(w*0.30,d*0.43)
            if style==2 {
                // Roof terrace with an asymmetric shade sail, not another dome.
                p.box(-w*0.17,h*0.86+0.13,-d*0.46,w*0.66,0.26,0.12,ink)
                p.box(-w*0.47,h*0.86+0.13,0,0.12,0.26,d*0.88,ink)
                p.awning(-w*0.17,-d*0.08,w*0.48,d*0.53,h*0.86+1.25,0x706f58)
                for xx in [-w*0.39,w*0.05] { p.box(xx,h*0.86+0.62,-d*0.33,0.07,1.24,0.07,0x695b48) }
            } else { p.dome(-w*0.17,h*0.86,0,r,r*0.57,r,ink,sides:near ? 20:16) }
            if style==1 { p.dome(w*0.25,h*0.64,d*0.1,w*0.22,w*0.15,w*0.22,ink,sides:16) }
        } else if style<5 {
            // Connected stepped roofscape with recessed terraces and parapets.
            if entrance,let plan {
                let turn=plan.facade.turn
                let q=paint(x,z,yaw:yaw+turn)
                q.residentHouse(abs(sin(turn))>0.5 ? d:w,h*0.60,abs(sin(turn))>0.5 ? w:d,ink,doorX:plan.offset)
            }
            else { p.adobe(0,h*0.30,0,w,h*0.60,d,ink,simple:false) }
            p.adobe(-w*0.18,h*0.78,-d*0.16,w*0.61,h*0.36,d*0.67,ink,simple:false)
            p.box(w*0.23,h*0.606,d*0.16,w*0.47,0.025,d*0.58,roof)
            p.box(w*0.47,h*0.65,0,w*0.05,0.26,d,ink)
            p.box(0,h*0.65,d*0.47,w,0.26,d*0.05,ink)
            if style==4 {
                // Ventilated windcatcher rises above a stepped flat roof.
                p.adobe(-w*0.18,h*0.96+0.8,-d*0.16,w*0.26,1.6,d*0.26,ink,simple:false)
                p.box(-w*0.18,h*0.96+1.63,-d*0.16,w*0.34,0.12,d*0.34,roof)
                for side in [-1.0,1] { p.box(-w*0.18,h*0.96+1.18,-d*0.16+side*d*0.131,w*0.15,0.5,0.025,0x4a493e) }
            }
        } else if style==5 {
            // Buttressed rotunda integrated into a long low workshop.
            p.adobe(0,h*0.22,0,w,h*0.44,d,ink,simple:false)
            let r=min(w,d)*0.40
            p.cylinder(-w*0.08,h*0.54,-d*0.04,r,r*0.91,h*1.08,ink,sides:near ? 20:16)
            p.dome(-w*0.08,h*1.08,-d*0.04,r*0.91,r*0.5,r*0.91,ink,sides:near ? 20:16)
            p.cylinder(-w*0.08,h*0.8,-d*0.04,r*0.955,r*0.955,0.16,roof,sides:near ? 20:16)
        } else if style==6 {
            // Open courtyard enclosed on three sides, shaded workshop frontage.
            p.adobe(-w*0.34,h*0.42,0,w*0.32,h*0.84,d,ink,simple:false)
            p.adobe(w*0.10,h*0.35,-d*0.31,w*0.78,h*0.70,d*0.38,ink,simple:false)
            p.adobe(w*0.40,h*0.25,d*0.07,w*0.20,h*0.50,d*0.69,ink,simple:false)
            p.awning(-w*0.08,-d*0.04,w*0.34,d*0.36,h*0.65,0x87725c)
            // An open-to-sky domestic patio, with a low bench along its wall.
            p.box(-w*0.10,0.22,-d*0.065,w*0.32,0.44,0.28,0x9d8467)
            p.vessel(w*0.23,0.02,-d*0.065,0.55,0xa27b54)
        } else if style==7 {
            // Industrial block: vaulted hall, sunken rooftop machinery enclosure.
            if entrance,let plan {
                let turn=plan.facade.turn
                let q=paint(x,z,yaw:yaw+turn)
                q.residentHouse(abs(sin(turn))>0.5 ? d:w,h*0.60,abs(sin(turn))>0.5 ? w:d,ink,doorX:plan.offset)
            }
            else { p.adobe(0,h*0.30,0,w,h*0.60,d,ink,simple:false) }
            p.dome(-w*0.2,h*0.60,0,w*0.28,w*0.27,d*0.45,ink,sides:16)
            p.box(w*0.25,h*0.605,0,w*0.40,0.025,d*0.7,0x665c50)
            for k in 0..<3 { p.box(w*0.25,h*0.71,-d*0.23+Double(k)*d*0.23,w*0.29,0.35,0.17,roof,detail:near) }
        } else {
            // Two unequal flat-roof dwellings linked by a utility room.
            p.adobe(-w*0.23,h*0.5,-d*0.05,w*0.54,h,d*0.9,ink,simple:false)
            p.adobe(w*0.26,h*0.29,0,w*0.47,h*0.58,d,ink,simple:false)
            p.box(-w*0.23,h+0.07,-d*0.05,w*0.48,0.15,d*0.83,roof)
        }
        if !near && index%4==0 {
            let roofY=style==8 ? h*0.58:(style==6 ? h*0.70:h*0.60)
            if [3,4,8].contains(style) {
                let steps=max(7,Int(ceil(h*(style==8 ? 0.42:0.36)/0.19)))
                let run=w*0.40,tread=run/Double(steps),edge=style==8 ? w*0.04:w*0.125
                for k in 0..<steps {
                    let rise=h*(style==8 ? 0.42:0.36)*Double(k+1)/Double(steps)
                    p.box(edge+run-tread*(Double(k)+0.5),roofY+rise/2,d*0.19,tread+0.005,rise,0.56,ink)
                }
            }
        }
        if !near { p=explorationPainter(x,z,yaw:yaw) }
        // Equipment on usable flat roofs, with cables/pipes at the near tier.
        if style==3 || style==4 || style==8 {
            let roofY=style==8 ? h*0.58:h*0.60
            p.box(w*0.25,roofY+0.19,d*0.08,w*0.25,0.38,d*0.28,0x7f7867)
            for k in 0..<4 { p.box(w*0.25,roofY+0.385,d*0.01+Double(k)*0.11,w*0.22,0.018,0.045,0x49483f,detail:true) }
            p.beam(SIMD3(w*0.15,roofY+0.12,-d*0.18),SIMD3(w*0.41,roofY+0.12,-d*0.18),0.095,0x918773,sides:8)
            p.beam(SIMD3(w*0.41,roofY+0.12,-d*0.18),SIMD3(w*0.41,roofY+0.12,d*0.29),0.095,0x918773,sides:8)
        }
        // Dress only a usable entrance; there is no mirrored rear-door stamp.
        if let plan {
            let f=plan.facade,lx=f.x+cos(f.turn)*plan.offset,lz=f.z-sin(f.turn)*plan.offset
            let center=SIMD2(x+lx*cos(yaw)+lz*sin(yaw),z-lx*sin(yaw)+lz*cos(yaw))
            let direction=yaw+f.turn
            // Entrances define the building even from an elevated town camera.
            let q=paint(center.x,center.y,yaw:direction)
            let dw=entrance ? 0.92:plan.width,dh=entrance ? 1.30:plan.height
            if style==5 && h*0.44<dh+0.18 { q.adobe(0,(dh+0.18)/2,-0.18,dw+0.44,dh+0.18,0.40,ink) }
            q.cityDoor(dw,dh,ink,variant:plan.variant,open:entrance)
            if !near && index%4==0 && f.span>3 {
                let utility=explorationPainter(center.x,center.y,yaw:direction),side=plan.offset>0 ? -1.0:1.0
                let xx=side*min(f.span*0.30,dw/2+0.52)
                utility.box(xx,1.35,0.16,0.36,0.58,0.25,0x7d8276)
                utility.beam(SIMD3(xx,0.20,0.18),SIMD3(xx,dh+0.15,0.18),0.045,0x91826b)
                utility.cable(SIMD3(xx,dh+0.15,0.16),SIMD3(0,dh+0.18,0.16),0.10,0x554b3e)
            }
            if entrance { doorways.append(TownDoorway(center:center,yaw:direction,root:root,variant:plan.variant)) }
            entrances.append(Entrance(center:center,yaw:direction,width:dw,height:dh,variant:plan.variant,walkable:entrance))
            if index%3==0 { q.awning(0,0.40,min(f.span-0.12,dw+0.55),0.85,dh+0.28,index%2==0 ? 0x7e6a50:0x8f5140) }
            if near && max(abs(x),abs(z))<36 && index%7==0 && storefrontSigns<8 && f.height>dh+0.55 {
                let title=["MACHINE WORKS","CANTINA","OFFWORLD GOODS","REACTOR SUPPLY"][storefrontSigns%4]
                let sy=min(f.height-0.12,dh+0.45)
                signs.plate(title,eyebrow:"MOS ASTER",footer:"MARKET DISTRICT",badge:String(format:"%02d",storefrontSigns+11),
                            at:SCNVector3(center.x+sin(direction)*0.12,sy,center.y+cos(direction)*0.12),width:min(2.0,CGFloat(f.span)*0.62),height:0.34,yaw:CGFloat(direction),
                            accent:storefrontSigns%2==0 ? rust:teal,into:root)
                storefrontSigns += 1
            }
        }
        // Openings use the same real facade catalog as door planning. A
        // bounding-box face can be empty space in a joined or U-shaped house.
        for (faceIndex,f) in compoundFacades(w:w,d:d,h:h,style:style).enumerated() where (faceIndex+index)%2==0 && f.span>1.0 {
            let offset=f.span*(index%2==0 ? 0.21:-0.21)
            let lx=f.x+cos(f.turn)*offset,lz=f.z-sin(f.turn)*offset
            if let plan {
                let pf=plan.facade,dx=pf.x+cos(pf.turn)*plan.offset,dz=pf.z-sin(pf.turn)*plan.offset
                if hypot(lx-dx,lz-dz)<plan.width/2+0.42 { continue }
            }
            let center=SIMD2(x+lx*cos(yaw)+lz*sin(yaw),z-lx*sin(yaw)+lz*cos(yaw))
            let direction=yaw+f.turn,wallHeight=style==5 ? h*0.44:f.height
            let wy=min(1.75,min(wallHeight*0.64,wallHeight-0.30))
            let q=near ? paint(center.x,center.y,yaw:direction):explorationPainter(center.x,center.y,yaw:direction)
            q.box(0,wy,-0.035,0.23,0.46,0.12,0x39332c,detail:true)
            q.box(0,wy-0.245,0.0,0.32,0.075,0.18,roof,detail:true)
            for yy in [wy-0.22,wy+0.22] { windowSupports.append(SIMD3(center.x-sin(direction)*0.20,yy,center.y-cos(direction)*0.20)) }
        }
        if near && (style<5 || style==8) {
            for side in [-1.0,1] {
                p.adobe(side*w*0.43,h*0.26,d*0.41,0.28,h*0.52,0.35,ink)
            }
        }
        // Roof equipment is useful silhouette at medium distance, tiny wires LOD out.
        let ry = near ? (style==5 ? h*1.10:h*0.65):h*(style==8 ? 0.58:0.60)
        if index%3==0 && (near || style==3 || style==4 || style==7 || style==8) {
            p.cylinder(w*0.28,ry+0.3,-d*0.21,0.32,0.29,0.60,roof,sides:8,detail:true)
            p.box(w*0.28,ry+0.64,-d*0.21,0.72,0.10,0.72,0x756654,detail:true)
        }
        if index%5==0 && (near || style==8) {
            p.cylinder(-w*0.31,h+0.30,-d*0.1,0.08,0.065,0.90,0x5c5449,sides:6,detail:true)
            p.box(-w*0.31,h+0.62,-d*0.1,0.66,0.045,0.045,0x65594b,detail:true)
        }
        for k in 0..<(near ? index%3:0) {
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

    private func repairFootprintClear(_ index:Int)->Bool {
        let points=InfieldLayout.canopies[index].points
        for i in 1..<points.count-1 {
            for u in 0...24 { for v in 0...(24-u) {
                let p=points[0]+(points[i]-points[0])*Double(u)/24+(points[i+1]-points[0])*Double(v)/24
                if !infield(p.x,p.y) || DirtCourse.projection(x:p.x,z:p.y).distance<DirtCourse.terrainEdge+0.6 { return false }
            }}
        }
        return true
    }

    private var repairLots:[TownLot] = []
    private func buildRepairPit() {
        let entry=paint(DirtCourse.serviceEntryX,-12.2)
        for side in [-1.0,1.0] {
            let ground=DirtCourse.height(x:DirtCourse.serviceEntryX+side*1.4,z:-12.2)
            entry.cylinder(side*1.40,ground+0.28,0,0.045,0.045,0.56,0xb59b62,sides:8,detail:true)
            entry.cylinder(side*1.40,ground+0.40,0,0.046,0.046,0.075,0x554c40,sides:8,detail:true)
        }
        let signGround=DirtCourse.height(x:DirtCourse.serviceEntryX-1.85,z:-12.21)
        signs.plate("SERVICE ACCESS",eyebrow:"REPAIR BAY",footer:"KEEP CLEAR",badge:"S",
                    at:SCNVector3(DirtCourse.serviceEntryX-1.85,signGround+0.68,-12.2),width:1.02,height:0.34,yaw:.pi,accent:rust,into:root)
        entry.beam(SIMD3(-1.85,signGround,-0.01),SIMD3(-1.85,signGround+0.67,-0.01),0.025,trim,sides:6)
        // Two pockets on the west side of the infield, clear of the dirt shoulder.
        for (i,location) in InfieldLayout.tentOrigins.enumerated() {
            let x=location.x,z=location.y,w=i==0 ? 3.2:4.0,d=i==0 ? 3.2:4.0
            guard repairFootprintClear(i) else { continue }
            repairLots.append(TownLot(x:x,z:z,width:w+0.3,depth:d+0.3))
            var p=paint(x,z,yaw:InfieldLayout.tentYaws[i])
            p.repairRug(w-0.2,d-0.2,i)
            if i==0 { p.triangularRepairCanopy(rust) }
            else { p.canopy(0,0,w,d,2.0,0.65,teal) }
            if i==0 { let center=InfieldLayout.tentPoint(0,SIMD2(-0.1,0.35));p=paint(center.x,center.y,yaw:InfieldLayout.tentYaws[0]) }
            let roof=InfieldLayout.canopies[i].points
            cameraBounds.append((SIMD3(roof.map{$0.x}.min()!,2.0,roof.map{$0.y}.min()!),SIMD3(roof.map{$0.x}.max()!,2.65,roof.map{$0.y}.max()!)))
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
            // Tapered service droid with separate shell, collar and articulated limbs.
            p.cylinder(0.24,0.62,0.20,0.28,0.23,0.63,cream,sides:24)
            p.cylinder(0.24,0.92,0.20,0.245,0.245,0.065,dark,sides:24,detail:true)
            p.dome(0.24,0.965,0.20,0.26,0.23,0.26,teal,sides:24)
            p.box(0.24,1.07,0.448,0.18,0.065,0.035,dark,detail:true)
            p.box(0.30,1.07,0.47,0.035,0.035,0.016,0xbb9567,detail:true)
            p.box(0.24,0.66,0.465,0.25,0.32,0.035,dark,detail:true)
            for k in 0..<5 {
                p.box(0.24,0.55+Double(k)*0.047,0.487,0.20,0.018,0.012,trim,detail:true)
            }
            for side in [-1.0,1.0] {
                let xx=0.24+side*0.34
                p.cylinder(xx,0.77,0.20,0.10,0.10,0.12,trim,sides:16,detail:true)
                p.beam(SIMD3(xx,0.74,0.20),SIMD3(xx+side*0.09,0.40,0.32),0.065,cream,sides:12)
                p.beam(SIMD3(xx+side*0.09,0.40,0.32),SIMD3(xx,0.33,0.46),0.045,dark,sides:12)
                p.box(xx,0.29,0.34,0.22,0.15,0.46,trim,detail:true)
                p.cylinder(xx,0.375,0.43,0.068,0.068,0.035,cream,sides:16,detail:true)
            }
            // Engine hoist and hanging spare motor.
            p.box(1.17,0.8,0.91,0.09,1.6,0.09,dark)
            p.box(0.83,1.60,0.91,0.76,0.10,0.10,rust)
            p.box(0.51,1.29,0.91,0.03,0.54,0.03,dark,detail:true)
            p.cylinder(0.51,0.94,0.91,0.18,0.22,0.36,trim,sides:8)
            for k in 0..<3 {
                p.cylinder(i==0 ? -0.1:1.03,0.12+Double(k)*0.17,i==0 ? 0.85:-0.86,0.27,0.27,0.15,dark,sides:10,detail:true)
                p.cylinder(i==0 ? -0.1:1.03,0.20+Double(k)*0.17,i==0 ? 0.85:-0.86,0.14,0.14,0.015,cream,sides:10,detail:true)
            }
            let mechanic=InfieldLayout.tentPoint(i,SIMD2(i==0 ? 0.65:0.73,i==0 ? -0.25:-0.35))
            citizen(mechanic.x,mechanic.y,y:0.055,yaw:InfieldLayout.tentYaws[i]-1.2,index:707+i,seated:false)
            p.box(-0.96,0.19,-1.06,0.60,0.36,0.40,teal)
            p.box(-0.96,0.40,-1.06,0.21,0.06,0.08,dark,detail:true)
            if i==1 { for sx in [-0.65,0.65] { p.beam(SIMD3(sx,1.98,-2.015),SIMD3(sx,2.44,-2.015),0.012,dark,sides:5) } }
            let sign=InfieldLayout.tentPoint(i,SIMD2(i==0 ? -1.625:0,i==0 ? -0.65:-2.035))
            signs.plate(i==0 ? "DROID REPAIR":"PARTS & SALVAGE",eyebrow:"RACE SERVICE",footer:"CREW ACCESS ONLY",badge:i==0 ? "01":"02",
                        at:SCNVector3(sign.x,1.74,sign.y),width:1.85,height:0.46,yaw:InfieldLayout.tentYaws[i]+(i==0 ? -.pi/2:.pi),accent:rust,into:root)
        }
        for (index,part) in InfieldLayout.parts.enumerated() {
            let p=paint(part.x,part.z,yaw:part.yaw)
            p.salvage(part.width,part.depth,part.height,part.kind,index)
        }

    }

    private func buildLandmarks() {
        // Spaceport hangar and landing circle, outside the circuit.
        let p=paint(23,14)
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
            q.ring(0,0.90,0,4.8,4.1,1.8,0xaf9878,sides:32,entry:true)
            q.ring(0,1.83,0,4.93,4.02,0.14,0xc0ad8d,sides:32,entry:true)
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
        // Distinct civic silhouettes, within the established reserved footprints.
        // Utility towers should read as different functions, not four copies.
        for (index,position) in [(-53.0,48.0),(49.0,53.0),(-70.0,-41.0),(22.0,71.0)].enumerated() {
            let (x,z)=position,q=paint(x,z)
            switch index {
            case 0: // Narrow stepped communications mast with a receiver crown.
                for k in 0..<4 {
                    let width=2.55-Double(k)*0.37,base=Double(k)*2.35
                    q.adobe(0,base+1.175,0,width,2.35,width*0.88,0xaa9271)
                    q.box(0,base+2.26,0,width+0.16,0.12,width*0.88+0.16,0x89785f)
                }
                q.cylinder(0,9.85,0,0.19,0.13,0.9,0x616459,sides:16)
                q.cylinder(0,10.45,0,0.30,1.45,0.45,0xaba486,sides:24)
                q.cylinder(0,10.70,0,1.48,1.48,0.10,0x716e5b,sides:24)
                q.beam(SIMD3(0,10.7,0),SIMD3(0,12.1,0),0.045,0x6f7164)
            case 1: // Ventilated observation drum with a broad domed cap.
                q.cylinder(0,4.6,0,1.32,0.97,9.2,0xa18e74,sides:24)
                q.cylinder(0,9.3,0,1.45,1.45,0.28,0x7d7562,sides:24)
                q.cylinder(0,9.87,0,1.17,1.17,0.9,0x414b43,sides:24)
                for k in 0..<12 {
                    let a=Double(k)*Double.pi/6
                    q.box(cos(a)*1.18,9.88,sin(a)*1.18,0.10,0.94,0.10,0x9b8d72)
                }
                q.cylinder(0,10.39,0,1.49,1.49,0.15,0xb5a181,sides:24)
                q.dome(0,10.46,0,1.49,0.85,1.49,0xb5a181,sides:24)
            case 2: // Thick-walled windcatcher: open dark louvres under a flat cap.
                q.adobe(0,4.0,0,2.65,8,2.35,0xb6a084)
                q.box(0,8.9,0,2.25,1.8,1.97,0x4e5248)
                for side in [-1.0,1] {
                    for zz in [-1.08,1.08] { q.adobe(side*1.21,8.9,zz,0.23,1.8,0.23,0xb6a084) }
                    for k in 0..<5 { q.box(0,8.25+Double(k)*0.28,side*1.11,2.40,0.09,0.14,0x8a8068) }
                }
                q.adobe(0,9.94,0,2.95,0.28,2.66,0xc1af8f)
                q.adobe(-0.7,10.39,0.46,0.58,0.62,0.6,0xb6a084)
            default: // Condenser column, exposed pipework and unequal fin bands.
                q.cylinder(0,5.8,0,1.30,0.85,11.6,0x9c886c,sides:24)
                for y in [3.2,3.65,7.6,10.0] { q.cylinder(0,y,0,1.55,1.40,0.20,0xb29d7e,sides:24) }
                q.dome(0,11.6,0,0.87,0.5,0.87,0xbba88b,sides:24)
                for a in [0.4,2.5,4.6] {
                    q.beam(SIMD3(cos(a)*1.25,0.2,sin(a)*1.25),SIMD3(cos(a)*1.03,8.4,sin(a)*1.03),0.045,0x717869)
                }
            }
        }
    }

    struct VenueSite {
        let name:String,center:SIMD2<Double>,halfWidth:Double,halfDepth:Double,yaw:Double
    }
    let venueSites=[VenueSite(name:"DROID EXCHANGE",center:SIMD2(64,64),halfWidth:9,halfDepth:10,yaw:.pi),
        VenueSite(name:"THE TWIN SUNS",center:SIMD2(-44,-53),halfWidth:9,halfDepth:9,yaw:.pi),
        VenueSite(name:"MOS ASTER MARKET",center:SIMD2(-108,11),halfWidth:9,halfDepth:10,yaw:.pi/2)]
    private func buildDistrictPlaces() {
        for (i,site) in venueSites.enumerated() {
            let q=paint(site.center.x,site.center.y,yaw:site.yaw)
            func point(_ x:Double,_ z:Double)->SIMD2<Double> {
                site.center+SIMD2(x*cos(site.yaw)+z*sin(site.yaw),-x*sin(site.yaw)+z*cos(site.yaw))
            }
            if i==0 {
                // A U-shaped industrial compound with an open repair/salvage
                // yard. The crane, racks and stacked running gear identify it.
                q.adobe(0,2.1,-6.8,16,4.2,4.0,0x9c8668)
                q.adobe(-7,1.55,-0.8,3,3.1,8.0,0xb29a78)
                q.adobe(7,1.7,-2.5,3,3.4,6.0,0xa38c6c)
                for k in 0..<4 {
                    let x = -5.7+Double(k)*3.8
                    q.box(x,4.23,-6.8,3.5,0.16,4.3,0x697574)
                    q.box(x,4.55,-7.8,2.5,0.64,0.12,0x5e6764)
                }
                for x in [-4.4,4.4] { q.box(x,2.65,-0.5,0.22,5.3,0.25,0x76523c) }
                q.box(0,5.28,-0.5,9.3,0.30,0.10,0x806249)
                for y in [5.10,5.46] { q.box(0,y,-0.5,9.5,0.08,0.42,0x76523c) }
                for x in [-4.4,4.4] {
                    q.box(x,0.10,-0.5,0.75,0.20,0.85,0x6a5140)
                    q.beam(SIMD3(x,4.3,-0.5),SIMD3(x+(x<0 ? 0.8:-0.8),5.1,-0.5),0.05,0x856447)
                }
                q.beam(SIMD3(-2.4,5.1,-0.5),SIMD3(-2.4,1.6,-0.5),0.025,0x454944,sides:6)
                q.engineAssembly(-2.4,1.0,-0.5,scale:1.0,variant:0)
                for z in [-0.78,-0.22] { q.cylinder(-2.4,1.69,z,0.22,0.19,0.18,0x8b7055,sides:12) }
                for x in [-6.4,-4.6] { for z in [2.85,3.55] { q.box(x,0.34,z,0.12,0.68,0.12,0x555e56) } }
                q.box(-5.5,0.76,3.2,2.4,0.16,1.0,0x69706a)
                q.box(-5.9,1.02,3.2,0.7,0.36,0.52,0x80624b)
                for j in 0..<3 { q.beam(SIMD3(-5.3+Double(j)*0.17,0.87,3),SIMD3(-5.3+Double(j)*0.17,0.87,3.45),0.025,0x4c5752,sides:6) }
                for k in 0..<5 {
                    let z = -3.2+Double(k)*1.65
                    if k%2==0 {
                        q.engineAssembly(4.6,0,z,scale:0.85+Double(k)*0.07,variant:k)
                    } else {
                        q.box(4.6,0.23,z,0.85,0.46,1.1,0x4b524c)
                        for j in 0..<6 { q.box(4.6,0.48,z-0.45+Double(j)*0.18,0.93,0.08,0.08,0x927758) }
                    }
                    if k<3 {
                        q.droidSalvage(-4.7,0,z,variant:k)
                    }
                }
                q.box(0,1.3,-4.78,3.3,2.6,0.06,0x343e3b)
                for k in 0..<9 { q.box(0,0.18+Double(k)*0.27,-4.72,3.2,0.025,0.05,0x68716a) }
                q.cable(SIMD3(-4.4,4.8,-0.48),SIMD3(4.4,4.8,-0.48),0.35,0x45483e)
                for k in 0..<3 { q.crate(3.4+Double(k%2)*0.7,0.02+Double(k/2)*0.62,-3.3,0.62,0x80735a) }
                let sign=point(0,-4.68)
                signs.plate("DROID EXCHANGE",eyebrow:"REPAIRS  /  REBUILT MOTORS",footer:"PARTS · TRACKS · POWER CELLS",badge:"08",at:SCNVector3(sign.x,3.22,sign.y),width:6.8,height:0.95,yaw:CGFloat(site.yaw),accent:teal,into:root)
                conversation(at:point(-2.8,3.7),axis:SIMD2(cos(site.yaw),-sin(site.yaw)),index:22000,count:2)
            } else if i==1 {
                // Low rotunda + unequal annex; the shaded seating terrace is
                // visibly a cantina, with a clear central approach to its door.
                q.cylinder(-3.2,1.7,-3.5,3.8,3.6,3.4,0xb79b78,sides:24)
                q.dome(-3.2,3.4,-3.5,3.6,1.2,3.6,0xc0a480,sides:24)
                q.adobe(3.4,1.45,-4.2,6.5,2.9,6,0xa89070)
                q.box(3.4,3.02,-4.2,6.7,0.24,6.2,0x948369)
                q.box(1.4,1.06,-1.18,1.5,2.12,0.06,0x363c32)
                q.awning(0.7,3.1,12.0,5.5,3.15,0x955d42)
                for x in [-5.1,6.5] { for z in [0.5,5.6] { q.box(x,1.55,z,0.10,3.1,0.10,0x71644e) } }
                for (k,p) in [SIMD2(-3.7,1.6),SIMD2(-3.7,4.4),SIMD2(4.7,1.6),SIMD2(4.7,4.4)].enumerated() {
                    q.cylinder(p.x,0.58,p.y,0.62,0.62,0.06,0x6d5944,sides:20)
                    q.cylinder(p.x,0.28,p.y,0.06,0.06,0.56,0x494c42,sides:12)
                    for a in 0..<3 {
                        let angle=Double(a)*2 * .pi/3
                        q.beam(SIMD3(p.x,0.16,p.y),SIMD3(p.x+cos(angle)*0.38,0.04,p.y+sin(angle)*0.38),0.028,0x555246)
                        q.vessel(p.x+cos(angle)*0.24,0.62,p.y+sin(angle)*0.24,0.14,0xc5b399)
                    }
                    for side in [-1.0,1] { q.cylinder(p.x+side*0.95,0.24,p.y,0.3,0.3,0.48,0x8c7154,sides:12) }
                    let group=point(p.x,p.y+0.95)
                    conversation(at:group,axis:SIMD2(cos(site.yaw),-sin(site.yaw)),index:23000+k*3,count:2)
                }
                q.box(0.7,3.08,5.8,12,0.18,0.18,0x71644e)
                let sign=point(0.7,5.95)
                signs.plate("THE TWIN SUNS",eyebrow:"MOS ASTER CANTINA",footer:"DRINKS · MUSIC · SHADE",badge:"09",at:SCNVector3(sign.x,3.15,sign.y),width:5.4,height:0.70,yaw:CGFloat(site.yaw),accent:0xa66e44,into:root)
            } else {
                // Market arcade and independent fabric stalls frame a shared
                // pedestrian court. Unequal awnings create a broken roofline.
                q.adobe(0,1.9,-7.0,17,3.8,3.8,0xbfa780)
                for (j,x) in [-6.0,0,6].enumerated() { q.dome(x,3.8,-7,1.6+Double(j)*0.24,0.6+Double(j%2)*0.3,1.7,0xc9b48f,sides:16) }
                for x in [-5.0,0,5] {
                    q.box(x,0.74,-5.08,1.14,1.48,0.08,0x414a3d)
                    q.arcade(x,0,-4.90,1.14,1.58,0.32,0.22,0xc2ae8d)
                    q.box(x,1.85,-5.02,1.85,0.12,0.28,0xa68d69)
                    for side in [-1.0,1] { q.adobe(x+side*0.84,0.96,-5.02,0.22,1.92,0.28,0xc2ae8d) }
                }
                // Deep, traversable masonry reveals layer the shopfront behind
                // the stalls. The piers stay outside the central pedestrian aisle.
                for (j,x) in [-5.0,0,5].enumerated() {
                    q.arcade(x,0,-4.45,3.45,3.10,1.25,0.34,0xbba582,wallTop:3.48)
                    q.adobe(x,3.64,-4.45,4.34,0.44+Double(j%2)*0.12,1.25,0xbba582)
                    for side in [-1.0,1] {
                        q.adobe(x+side*2.02,1.43,-4.45,0.55,2.86,1.25,0xbba582)
                    }
                }
                for side in [-1.0,1] { for k in 0..<3 {
                    let x=side*(5.3+Double(k%2)*0.65),z = -3.4+Double(k)*3.6+(side>0 ? 0.55:0)
                    q.canopy(x,z,3.6,2.9,2.55+Double(k%2)*0.23,0.30,[0x846849,0x5d7d76,0x9a6747][k])
                    q.box(x,0.58,z,2.8,0.10,0.8,0x74624a)
                    for dx in [-1.15,1.15] { for dz in [-0.28,0.28] { q.box(x+dx,0.265,z+dz,0.07,0.53,0.07,0x64583f) } }
                    for j in 0..<5 {
                        let px=x-1.0+Double(j)*0.49
                        if k==0 { q.vessel(px,0.64,z,0.22+Double(j%3)*0.08,j%2==0 ? 0xa8865f:0x8e7354) }
                        else if k==1 {
                            q.produceTray(px,0.64,z,variant:j+(side>0 ? 5:0))
                        } else {
                            q.cylinder(px,0.84,z,0.13,0.13,0.40,side<0 ? 0x608681:0xad7954,sides:12)
                            q.cylinder(px,1.07,z,0.16,0.16,0.06,0xb59b73,sides:12)
                        }
                    }
                    q.valance(x,z+1.45,3.6,2.40+Double(k%2)*0.23,0.18,[0x846849,0x5d7d76,0x9a6747][k],rise:0.0)
                    q.crate(x+side*1.1,0.02,z-0.9,0.48,0x837052)
                    q.cable(SIMD3(x-1.65,2.27,z-1.2),SIMD3(x+1.65,2.27,z-1.2),0.12,0x51483c)
                    for item in 0..<4 {
                        let xx=x-1.15+Double(item)*0.69,yy=1.7+Double((item+k)%3)*0.08
                        q.beam(SIMD3(xx,2.15,z-1.2),SIMD3(xx,yy+0.17,z-1.2),0.009,0x675b48,sides:6)
                        if k==0 { q.vessel(xx,yy,z-1.2,0.22,0xae8f65) }
                        else {
                            q.cylinder(xx,yy,z-1.2,0.07,0.075,0.24,0x72857c,sides:16)
                            for band in [-0.10,0.06] { q.cylinder(xx,yy+band,z-1.2,0.082,0.082,0.025,0xb29871,sides:16) }
                            q.cylinder(xx,yy+0.15,z-1.2,0.045,0.025,0.06,0x726951,sides:12)
                        }
                    }
                    let seller=point(x,z-0.9),customer=point(x-side*2.0,z+0.3)
                    pendingActivities.append([StreetActivity(position:seller,target:customer,role:"market vendor",group:24000+k+(side>0 ? 10:0),index:24000+k+(side>0 ? 10:0)),StreetActivity(position:customer,target:seller,role:"market customer",group:24000+k+(side>0 ? 10:0),index:24100+k+(side>0 ? 10:0))])
                }}
                let sign=point(0,-3.79)
                signs.plate("MOS ASTER MARKET",eyebrow:"FOOD  /  WATER  /  OFFWORLD GOODS",footer:"TRADERS COURT",badge:"10",at:SCNVector3(sign.x,3.65,sign.y),width:4.1,height:0.55,yaw:CGFloat(site.yaw),accent:teal,into:root)
            }
            buildings += 1
        }
    }
    private func buildNeighborhoodUtilities() {
        let courts=[SIMD2(-84.0,-20.0),SIMD2(55,-67),SIMD2(-20,86),SIMD2(-80,37)]
        for (index,center) in courts.enumerated() {
            let city=CityCollisionWorld(collisionBuilder.bodies)
            guard let p=(0..<24).map({ k -> SIMD2<Double> in
                let angle=Double(k)*2 * .pi/24
                return center+SIMD2(cos(angle),sin(angle))*3.8
            }).first(where:{ p in
                guard streetDistance(p.x,p.y)>1.5,!blocksEntrance(p) else { return false }
                let body=RobotCollisions.Body(position:SIMD3(p.x,0,p.y),profile:.init(mass:1,halfWidth:1.5,halfDepth:1.5,height:3.8,round:true))
                return city.nearby(body).allSatisfy{RobotCollisions.contact(body,$0)==nil}
            }) else { continue }
            let q=paint(p.x,p.y,yaw:Double(index)*0.8)
            if index%2==0 {
                // Communal water condenser: tank, fin stack, pipe, stone seat.
                q.cylinder(0,0.45,0,0.68,0.57,0.9,0xafa68c,sides:16)
                q.cylinder(0,1.7,0,0.14,0.12,2.5,0x8b9183,sides:12)
                for k in 0..<6 { q.cylinder(0,1.20+Double(k)*0.28,0,0.38-Double(k)*0.025,0.38-Double(k)*0.025,0.08,0xaca990,sides:12) }
                q.beam(SIMD3(0.25,0.68,0),SIMD3(0.9,0.68,0),0.065,0x696b5b)
                q.adobe(0.9,0.23,0.75,0.42,0.46,1.2,0xb8a68a)
            } else {
                // Freight handcart and accumulated deliveries at a shared court.
                q.box(0,0.38,0,1.55,0.16,0.95,0x717a6c)
                for x in [-0.65,0.65] { for z in [-0.38,0.38] { q.beam(SIMD3(x-0.07,0.17,z),SIMD3(x+0.07,0.17,z),0.17,0x484d44,sides:12) } }
                q.crate(-0.3,0.47,0,0.62,0x947c5a);q.crate(0.37,0.47,0.09,0.47,0xa38c65)
                q.beam(SIMD3(0.72,0.45,-0.4),SIMD3(1.3,0.95,-0.4),0.04,0x6a7161)
                q.beam(SIMD3(0.72,0.45,0.4),SIMD3(1.3,0.95,0.4),0.04,0x6a7161)
            }
            conversation(at:p+SIMD2(2,0),axis:SIMD2(0,1),index:25000+index*10,count:2)
        }
    }

    private func buildDoorstepLife() {
        let city=CityCollisionWorld(collisionBuilder.bodies)
        for (i,e) in entrances.enumerated() where i%3==1 && max(abs(e.center.x),abs(e.center.y))>40 {
            let out=SIMD2(sin(e.yaw),cos(e.yaw)),side=SIMD2(out.y,-out.x)
            let sign=i%2==0 ? 1.0:-1.0
            let p=e.center+side*sign*(e.width/2+0.85)+out*0.46
            let body=RobotCollisions.Body(position:SIMD3(p.x,0,p.y),heading:e.yaw,profile:.init(mass:1,halfWidth:0.48,halfDepth:0.35,height:1.1))
            guard !blocksEntrance(p),streetDistance(p.x,p.y)>0.8,
                  city.nearby(body).allSatisfy({RobotCollisions.contact(body,$0)==nil}),
                  pedestrianAccess.allSatisfy({$0.route.allSatisfy{simd_distance($0,p)>0.82}}) else { continue }
            let q=paint(p.x,p.y,yaw:e.yaw)
            switch (i/3)%5 {
            case 0:
                q.vessel(-0.19,0,0,0.48,0xa98a65);q.vessel(0.23,0.0,0.08,0.29,0xb9a17e)
            case 1:
                q.crate(-0.17,0,0,0.47,0x897356);q.crate(0.15,0.47,-0.03,0.28,0x9f8b69)
            case 2:
                q.box(0,0.30,0,0.86,0.10,0.42,0x8b765b)
                for x in [-0.33,0.33] { q.adobe(x,0.13,0,0.15,0.26,0.34,0xbca98c) }
                q.vessel(0.23,0.36,0,0.16,0xc2b199)
            case 3:
                q.cylinder(0,0.42,0,0.28,0.25,0.84,0x92978a,sides:16)
                for y in [0.1,0.68] { q.cylinder(0,y,0,0.295,0.295,0.05,0x585e50,sides:16) }
                q.beam(SIMD3(0,0.88,0),SIMD3(0.27,0.88,0),0.025,0x646a5c)
            default:
                q.box(0,0.14,0,0.72,0.28,0.47,0x655a4b)
                for k in 0..<3 { q.cylinder(-0.23+Double(k)*0.23,0.37,0,0.09,0.07,0.20,0x8c9484,sides:10) }
            }
        }
    }

    private func buildHouseholdYards() {
        // Activity belongs against a facade, with the door-to-street route clear.
        // Check the entire furnished footprint, not only each prop's centre.
        for (i,e) in entrances.enumerated().sorted(by: { ($0.offset*73)%503 < ($1.offset*73)%503 }) where max(abs(e.center.x),abs(e.center.y))>36 {
            if householdYards.count>=130 { break }
            let out=SIMD2(sin(e.yaw),cos(e.yaw)),side=SIMD2(out.y,-out.x)
            let city=CityCollisionWorld(collisionBuilder.bodies)
            let nearby=pedestrianAccess.filter{simd_distance($0.door,e.center)<8}.flatMap{$0.route}
            var chosen:SIMD2<Double>?
            for sign in [i%2==0 ? 1.0:-1.0,i%2==0 ? -1.0:1.0] {
                for along in [1.8,2.7] {
                    let p=e.center+side*sign*along+out*0.88
                    let footprint=RobotCollisions.Body(position:SIMD3(p.x,0,p.y),heading:e.yaw,profile:.init(mass:1,halfWidth:0.98,halfDepth:0.70,height:2.3))
                    guard streetDistance(p.x,p.y)>1.2,
                          city.nearby(footprint).allSatisfy({RobotCollisions.contact(footprint,$0)==nil}),
                          nearby.allSatisfy({point in
                              let walker=RobotCollisions.Body(position:SIMD3(point.x,0,point.y),profile:.init(mass:70,halfWidth:0.26,halfDepth:0.26,height:1.45,round:true))
                              return RobotCollisions.contact(footprint,walker)==nil
                          }) else { continue }
                    chosen=p;break
                }
                if chosen != nil { break }
            }
            guard let p=chosen else { continue }
            let q=paint(p.x,p.y,yaw:e.yaw),kind=i%6
            householdYards.append(p)
            switch kind {
            case 0: // Shaded household workbench, drawers and loose tools.
                q.canopy(0,0,1.8,1.25,1.9,0.12,[UInt32(0x8e7756),0x677c70,0x936b50][(i/6)%3])
                q.box(0,0.60,-0.15,1.4,0.10,0.65,0x7e6c52)
                for x in [-0.57,0.57] { q.box(x,0.28,-0.15,0.09,0.56,0.56,0x6f6550) }
                q.crate(-0.35,0.04,-0.1,0.38,0x8d775a)
                for j in 0..<3 {
                    let x = -0.35+Double(j)*0.22,z = -0.34+Double((i/6+j)%3)*0.045
                    q.beam(SIMD3(x,0.68,z),SIMD3(x+0.07,0.68,0.05),0.016,0x737b71,sides:6)
                    q.ring(x,0.68,z,0.038,0.021,0.025,0x919785,sides:6)
                }
            case 1: // Water storage and plumbing rather than a decorative barrel.
                q.cylinder(-0.25,0.55,-0.1,0.34,0.30,1.1,0x9a9b87,sides:16)
                for y in [0.14,0.92] { q.cylinder(-0.25,y,-0.1,0.355,0.355,0.05,0x666d5c,sides:16) }
                q.beam(SIMD3(-0.25,0.28,0.25),SIMD3(0.24,0.28,0.25),0.035,0x777461)
                q.vessel(0.48,0,0.24,0.5,0xab8257)
            case 2: // Uneven stacks of deliveries on a low pallet.
                for k in 0..<5 { q.box(0,0.07,-0.43+Double(k)*0.20,1.48,0.14,0.14,0x83704f) }
                q.crate(-0.35,0.14,0,0.55,0x9c825d);q.crate(0.29,0.14,-0.15,0.43,0x7b785f)
                q.crate(-0.30,0.69,-0.04,0.36,0x8b7556)
            case 3: // Seat, storage niche and a rolled shade mat.
                q.adobe(0,0.20,-0.17,1.6,0.40,0.54,0xb2a084)
                q.box(0,0.43,-0.17,1.42,0.06,0.48,0x806854)
                q.vessel(0.49,0.47,-0.18,0.20,0xbfa583)
                q.beam(SIMD3(-0.65,0.15,0.35),SIMD3(0.35,0.15,0.35),0.14,0x9c7a54,sides:12)
            case 4: // Repair work: open toolbox, gearing and a spare wheel.
                q.box(-0.30,0.16,0,0.65,0.32,0.48,0x76634f)
                for j in 0..<4 { q.box(-0.53+Double(j)*0.15,0.34,0,0.075,0.05,0.36,0x91947f) }
                q.ring(0.43,0.15,-0.11,0.30,0.16,0.30,0x695747,sides:16)
                q.cylinder(0.45,0.35,-0.12,0.12,0.12,0.1,0x94826a,sides:12)
            default: // Clay storage vessels with different profiles and sizes.
                q.vessel(-0.4,0,-0.12,0.72,0x9c7954)
                q.vessel(0.13,0,0.14,0.46,0xbba17a)
                q.crate(0.49,0,-0.22,0.34,0x8f7c5c)
            }
        }
        print("Household outdoor work/storage areas: \(householdYards.count)")
    }

    private func buildDomesticCourts() {
        var made=0
        for (i,e) in entrances.enumerated() where i%3==0 && max(abs(e.center.x),abs(e.center.y))>58 {
            if made>=24 { break }
            let out=SIMD2(sin(e.yaw),cos(e.yaw)),side=SIMD2(out.y,-out.x)
            let origin=e.center+out*0.18
            let pieces:[(Double,Double,Double,Double)]=[(-1.35,1.25,0.18,2.5),(1.35,1.25,0.18,2.5),(-1.04,2.43,0.8,0.18),(1.04,2.43,0.8,0.18)]
            let city=CityCollisionWorld(collisionBuilder.bodies)
            let bodies=pieces.map { x,z,w,d -> RobotCollisions.Body in
                let p=origin+side*x+out*z
                return RobotCollisions.Body(position:SIMD3(p.x,0,p.y),heading:e.yaw,profile:.init(mass:1,halfWidth:w/2+0.05,halfDepth:d/2+0.05,height:0.80))
            }
            guard bodies.allSatisfy({body in
                streetDistance(body.position.x,body.position.z)>0.7 && city.nearby(body).allSatisfy{RobotCollisions.contact(body,$0)==nil}
            }) else { continue }
            let nearbyRoutes=pedestrianAccess.filter{simd_distance($0.door,origin)<9}.flatMap{$0.route}
            guard nearbyRoutes.allSatisfy({p in
                let person=RobotCollisions.Body(position:SIMD3(p.x,0,p.y),profile:.init(mass:70,halfWidth:0.26,halfDepth:0.26,height:1.4,round:true))
                return bodies.allSatisfy{RobotCollisions.contact(person,$0)==nil}
            }) else { continue }
            let q=paint(origin.x,origin.y,yaw:e.yaw),ink:UInt32=i%2==0 ? 0xb8a58a:0xc5b697
            for (x,z,w,d) in pieces { q.adobe(x,0.40,z,w,0.80,d,ink) }
            // The open central gate is wider than the audited pedestrian body.
            for x in [-0.59,0.59] { q.adobe(x,0.53,2.43,0.16,1.06,0.23,ink) }
            made += 1
        }
        print("Domestic outdoor courts: \(made)")
    }

    private func refreshPedestrianAccess() {
        // Replan using all final architecture, stalls, steps and street props,
        // so the audit cannot pass a path blocked by later set dressing.
        let obstacles=collisionBuilder.bodies.filter{$0.profile.mass != 70 && $0.position.y<1.45 && $0.position.y+$0.profile.height>0.10}.map { body in
            TownAccessMap.Footprint(center:SIMD2(body.position.x,body.position.z),width:body.profile.halfWidth*2,depth:body.profile.halfDepth*2,yaw:body.heading)
        }
        let access=TownAccessMap(footprints:obstacles,streets:streets.map{$0.path})
        pedestrianAccess=pedestrianAccess.compactMap { old in
            guard let e=entrances.first(where:{simd_distance($0.center,old.door)<0.01}) else { return nil }
            let out=SIMD2(sin(e.yaw),cos(e.yaw))
            guard let route=access.routeToStreet(from:e.center+out*0.65) else {
                inaccessibleBuildings.append(old.building);return nil
            }
            return PedestrianAccess(building:old.building,door:e.center,route:[e.center+out*0.42,e.center+out*0.6]+route)
        }
        for (i,site) in venueSites.enumerated() {
            let local:SIMD2<Double> = i==0 ? SIMD2(0,-4.1):(i==1 ? SIMD2(1.4,-0.5):SIMD2(0,-4.3))
            let door=site.center+SIMD2(local.x*cos(site.yaw)+local.y*sin(site.yaw),-local.x*sin(site.yaw)+local.y*cos(site.yaw))
            if let route=access.routeToStreet(from:door) { venueAccess.append(PedestrianAccess(building:site.center,door:door,route:route)) }
        }
        print("Final pedestrian access: \(pedestrianAccess.count), inaccessible \(inaccessibleBuildings)")
    }

    private func buildStreetLife() {
        // Wait beside a real entrance, looking at its door, without occupying
        // the approach used by visitors. Conversations occupy small forecourts.
        for (i,e) in entrances.enumerated() where i%5==0 && max(abs(e.center.x),abs(e.center.y))<47 {
            let out=SIMD2(sin(e.yaw),cos(e.yaw)),side=SIMD2(out.y,-out.x)
            if i%10==0 {
                pendingActivities.append([StreetActivity(position:e.center+out*0.75+side*0.95,
                    target:e.center,role:"waiting at door",group:10000+i,index:10000+i)])
            } else {
                conversation(at:e.center+out*1.2+side*1.6,axis:out,index:11000+i,count:2)
            }
        }
        for group in 0..<12 {
            conversation(at:SIMD2(-8.3+Double(group%6)*3.2,finishZ-8.0-Double(group/6)*1.25),
                         axis:SIMD2(cos(Double(group)*0.6),sin(Double(group)*0.6)),index:12000+group*3,count:3)
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
                if i%2==0 { p.vessel(xx,0.73,-0.08,0.38+Double(k%3)*0.05,k%2==0 ? 0x97816a:0xa8795c) }
                else { p.box(xx,0.81,-0.08,0.25,0.22,0.34,k%2==0 ? 0x667771:0xad8f65,detail:true) }
            }
            p.beam(SIMD3(-1.4,1.64,-0.77),SIMD3(1.4,1.64,-0.77),0.022,dark,sides:6)
            for sx in [-0.75,0.75] { p.beam(SIMD3(sx,1.57,-0.77),SIMD3(sx,1.64,-0.77),0.012,dark,sides:5) }
            signs.plate(["CERAMICS","DROID EXCHANGE","SPICE MERCHANT","POWER CELLS"][i],eyebrow:"ASTER BAZAAR",footer:"TRADE  /  REPAIR  /  SUPPLIES",badge:"0\(i+1)",
                        at:SCNVector3(x,1.37,z-0.77),width:2.15,height:0.40,yaw:.pi,accent:i%2==0 ? rust:teal,into:root)
            pendingActivities.append([
                StreetActivity(position:SIMD2(x,z+0.65),target:SIMD2(x,z-0.85),role:"market vendor",group:13000+i,index:1701+i),
                StreetActivity(position:SIMD2(x,z-0.85),target:SIMD2(x,z+0.65),role:"market customer",group:13000+i,index:1801+i)])
            p.box(1.12,0.24,0.45,0.43,0.48,0.44,0x665846,detail:true)
        }
        // Pedestrian groups follow roads and cluster at shops, never the course.
        for (roadIndex,street) in streets.prefix(6).enumerated() {
            for i in 1..<street.points.count {
                let a=street.points[i-1],delta=street.points[i]-a,length=simd_length(delta)
                let count=Int(length/3.5)
                for k in 0..<count {
                    let t=(Double(k)+0.5)/Double(max(1,count))
                    let routePoint=a+delta*t
                    let nearest=street.path.indices.min { simd_length_squared(street.path[$0]-routePoint)<simd_length_squared(street.path[$1]-routePoint) }!
                    let curvedTangent=simd_normalize(street.path[min(nearest+1,street.path.count-1)]-street.path[max(0,nearest-1)])
                    let curvedNormal=SIMD2(-curvedTangent.y,curvedTangent.x)
                    let center=street.path[nearest]+curvedNormal*((k%2==0 ? 1.0:-1.0)*(street.width/2-0.32))
                    guard max(abs(center.x),abs(center.y))<52 else { continue }
                    let index=2000+roadIndex*100+i*13+k
                    if k%2==0 {
                        // Keep the actual street placement instead of discarding it
                        // and spawning every pedestrian at the same few houses.
                        let side=k%4==0 ? 1.0:-1.0
                        var route=[SIMD2<Double>]()
                        for j in max(0,nearest-24)...min(street.path.count-1,nearest+24) {
                            let d=street.path[min(j+1,street.path.count-1)]-street.path[max(0,j-1)]
                            let n=SIMD2(-d.y,d.x)/max(0.001,simd_length(d))
                            route.append(street.path[j]+n*(side*(street.width/2-0.55)))
                        }
                        walkingStreets.append(route)
                    } else {
                        // Outside the walking lane, leave a complete pair or nobody.
                        conversation(at:center-curvedNormal*0.65,axis:curvedTangent,index:14000+index*3,count:2)
                    }
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
        for (i,origin) in InfieldLayout.tentOrigins.enumerated() {
            let z=origin.y,center=InfieldLayout.tentPoint(i,SIMD2(i==0 ? -0.1:0,i==0 ? 0.35:0)),p=paint(center.x,center.y,yaw:InfieldLayout.tentYaws[i])
            for k in 0..<5 {
                p.beam(SIMD3(-1.25,0.08,-0.35+Double(k)*0.22),SIMD3(-0.5,0.08,-0.55+Double(k)*0.2),0.023,0x554a3d,sides:5)
            }
            p.box(z<0 ? -1.5:1.5,0.47,z<0 ? 0.5:0.95,0.32,0.94,0.40,0x77766a)
            p.box(z<0 ? -1.5:1.5,0.80,z<0 ? 0.28:0.73,0.22,0.18,0.025,0x45656b)
            p.box(z<0 ? -1.5:1.5,0.55,z<0 ? 0.28:0.73,0.13,0.05,0.03,0xc5a56c)
        }
    }

    private func conversation(at center:SIMD2<Double>,axis:SIMD2<Double>,index:Int,count:Int) {
        let angle=atan2(axis.y,axis.x)
        pendingActivities.append((0..<count).map { member in
            let a=angle+Double(member)*2*Double.pi/Double(count)
            return StreetActivity(position:center+SIMD2(cos(a),sin(a))*0.48,
                                  target:center,role:"conversation",group:index,index:index+member)
        })
    }

    private func blocksEntrance(_ point:SIMD2<Double>)->Bool {
        entrances.contains { e in
            let delta=point-e.center,out=SIMD2(sin(e.yaw),cos(e.yaw))
            let forward=simd_dot(delta,out),side=simd_dot(delta,SIMD2(out.y,-out.x))
            return simd_length(delta)<0.65 || (forward > -0.3 && forward<1.7 && abs(side)<0.58)
        }
    }

    private func placeStreetActivities() {
        // Validate against finished scenery, before building navigation and the
        // permanent collision cache. Admit groups atomically; no orphan talkers.
        let scenery=CityCollisionWorld(collisionBuilder.bodies)
        for group in pendingActivities {
            guard group.allSatisfy({ person in
                let p=person.position,course=DirtCourse.projection(x:p.x,z:p.y)
                guard !blocksEntrance(p), course.offset>0, course.distance>DirtCourse.fenceOffset+0.3 else { return false }
                let body=RobotCollisions.Body(position:SIMD3(p.x,0.02,p.y),profile:.init(mass:70,halfWidth:0.24,halfDepth:0.24,height:1.45,round:true))
                return !scenery.nearby(body).contains { RobotCollisions.contact(body,$0) != nil }
                    && !streetActivities.contains { simd_distance($0.position,p)<0.65 }
            }) else { continue }
            for person in group {
                citizen(person.position.x,person.position.y,y:0.02,yaw:person.yaw,index:person.index,seated:false,shelter:true,activity:person.role=="conversation" ? .conversation:(person.role=="waiting at door" ? .waiting:.trading))
                streetActivities.append(person)
            }
        }
        pendingActivities.removeAll()
    }

    private func citizen(_ x:Double,_ z:Double,y:Double,yaw:Double,index:Int,seated:Bool,walking:Bool=false,shelter:Bool=false,activity:TownCrowd.Activity = .ordinary) {
        if !seated && blocksEntrance(SIMD2(x,z)) { return }
        population += 1
        if seated {
            let ix=Int(floor(x/8)),iz=Int(floor(z/8)),key="\(ix),\(iz)"
            var zone=spectatorZones[key] ?? SpectatorSoundZone(position:.zero,people:0,stormPeople:0)
            zone.position=(zone.position*Double(zone.people)+SIMD2(x,z))/Double(zone.people+1)
            zone.people += 1
            if TownCrowd.staysOutside(x:x,z:z,index:index) { zone.stormPeople += 1 }
            spectatorZones[key]=zone
        }
        if walking && walkingCount<18 { walkingCount += 1;return }
        let projection=DirtCourse.projection(x:x,z:z)
        if projection.offset>0 && projection.distance>DirtCourse.fenceOffset {
            if shelter || !TownCrowd.staysOutside(x:x,z:z,index:index) { absentPeople.insert(collisionBuilder.bodies.count) }
            collisionBuilder.bodies.append(.init(position:SIMD3(x,y,z),heading:yaw,profile:.init(mass:70,halfWidth:0.20,halfDepth:0.20,height:seated ? 0.8:1.45,round:true)))
        }
        _ = crowd.add(x:x,y:y,z:z,yaw:yaw,index:index,seated:seated,animated:false,shelter:shelter,activity:activity)
    }
    /// Authored reference block: construction, repairs and usable objects have
    /// specific placements rather than scattering decoration over the whole city.
    private func buildReferenceDetails() {
        let z=finishZ-4.9,p=paint(0,z)
        for side in [-1.0,1] {
            // Handrails follow the flights, with vertical posts and socket plates.
            let x=side*7.29
            p.beam(SIMD3(x,0.86,1.30),SIMD3(x,3.31,-2.10),0.026,0x6d6555)
            for i in 0..<5 {
                let zz=1.3-Double(i)*0.85,y=0.08+Double(i)*0.60
                p.beam(SIMD3(x,y,zz),SIMD3(x,y+0.81,zz),0.02,0x6d6555,sides:6)
            }
            let q=paint(side*8.5,finishZ-5.5)
            // Plaster repairs collect at the plinth, pipes and sill edges.
            for k in 0..<7 {
                q.plasterPatch(-0.94+Double(k)*0.30,0.18+Double(k%3)*0.12,1.802,0.23,0.24,0xa38e71,seed:k)
            }
            q.box(0,3.28,1.83,2.24,0.11,0.20,0xaa9676,detail:true)
            q.box(0,3.46,1.84,2.33,0.12,0.23,0xd0ba95,detail:true)
            // Copper service riser, elbows, straps and junction housing.
            q.beam(SIMD3(0.93,0.18,1.86),SIMD3(0.93,2.76,1.86),0.036,0x897157)
            q.beam(SIMD3(0.93,2.76,1.86),SIMD3(0.38,2.76,1.86),0.036,0x897157)
            for y in [0.48,1.35,2.28] { q.box(0.93,y,1.88,0.14,0.05,0.09,0x554c40,detail:true) }
            q.box(0.65,1.10,1.94,0.33,0.48,0.19,0x6d7970,detail:true)
            q.box(0.65,1.10,2.042,0.26,0.36,0.012,0x8d998a,detail:true)
            for k in 0..<4 { q.box(0.65,1.20-Double(k)*0.055,2.052,0.18,0.014,0.009,0x3e4842,detail:true) }
            q.cable(SIMD3(-0.98,2.91,1.88),SIMD3(0.9,2.82,1.88),0.18,0x594b3c)
            q.vessel(-0.91,0.02,2.02,0.44,0x9f7154)
            q.crate(0.5,0.02,2.12,0.48,0x8d7656)
        }
        // Riveted fascia and support brackets make the race sign a built object.
        for x in [-2.7,-1.35,0,1.35,2.7] {
            p.box(x,1.54,2.04,0.06,0.77,0.08,0x655d50,detail:true)
            for y in [1.26,1.83] { p.dome(x,y,2.125,0.023,0.023,0.014,0x968b70,sides:6,detail:true) }
        }
        // Market work surfaces: plank joints, stacked produce and hanging stock.
        for (i,x) in [-7.5,-3.7,3.7,7.5].enumerated() {
            let q=paint(x,finishZ-10.0)
            q.valance(0,-0.73,2.8,1.65,0.16,0xa58d67,rise:0.22)
            for k in 0..<8 { q.box(-1.0+Double(k)*0.29,0.718,-0.1,0.25,0.022,0.67,0x9c8765,detail:true) }
            q.crate(-1.05,0.025,0.77,0.51,0x817057)
            q.crate(-1.01,0.54,0.76,0.43,0x9b835e)
            q.vessel(0.7,0.72,-0.12,0.33,0xba9470)
            q.vessel(1.02,0.02,0.64,0.48,0x876653)
            q.cable(SIMD3(-1.4,1.64,-0.75),SIMD3(1.4,1.64,-0.75),0.10,0x6c5c47)
            for k in 0..<3 {
                let xx = -0.86+Double(k)*0.44
                q.beam(SIMD3(xx,1.60,-0.74),SIMD3(xx,1.28,-0.74),0.008,0x615a4c,sides:5)
                if i%2==0 { q.vessel(xx,1.02,-0.74,0.26,0x987559) }
                else { q.ring(xx,1.24,-0.74,0.10,0.068,0.10,0x6b7167,sides:12) }
            }
        }
        // The hero pit is a functioning workshop: a workboard, drawers, hoist
        // hardware, engine fins and a hose resting on the packed-earth apron.
        let center=InfieldLayout.tentPoint(0,SIMD2(-0.1,0.35)),q=paint(center.x,center.y,yaw:InfieldLayout.tentYaws[0])
        q.box(-1.54,1.05,0.15,0.08,0.77,1.75,0x586157,detail:true)
        for j in 0..<9 {
            let zz = -0.55+Double(j)*0.17
            q.beam(SIMD3(-1.48,0.82,zz),SIMD3(-1.48,1.23-Double(j%3)*0.08,zz),0.014,0xb0a891,sides:6)
            q.box(-1.46,1.22-Double(j%3)*0.08,zz,0.025,0.045,0.07,0x8a8d80,detail:true)
        }
        for row in 0..<3 {
            let y=0.18+Double(row)*0.15
            q.box(-0.93,y,0.93,0.57,0.13,0.13,0x6e8279,detail:true)
            q.box(-0.93,y,1.006,0.19,0.025,0.02,0xb3ad94,detail:true)
        }
        for k in 0..<7 { q.cylinder(0.51,0.80+Double(k)*0.041,0.91,0.235,0.235,0.014,0x7e8174,sides:14,detail:true) }
        q.cable(SIMD3(1.4,0.14,-1.1),SIMD3(0.7,0.10,0.40),-0.035,0x4c5148)
        q.crate(-1.30,0.08,-0.85,0.50,0x8a795a)
        q.vessel(-1.42,0.08,-1.43,0.39,0x988b69)
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

    func update(dt:Double,camera:SCNVector3,player:SIMD2<Double>,robots:[RobotCollisions.Body]=[],visible:((SCNNode)->Bool)?=nil,shadowCamera:SCNNode?=nil,viewportAspect:Double=1) {
        let current=shadowCullingEnabled ? ShadowFrustum.cameras(shadowCamera,aspect:viewportAspect):[]
        // Querying SceneKit presentation nodes synchronizes with rendering.
        // Retain our own preceding camera poses instead, plus the current pose.
        updateExplorationDetail(camera:camera,player:player,frusta:current+priorShadowFrusta)
        priorShadowFrusta=Array((current+priorShadowFrusta).prefix(2))
        guard dt>0 else { return }
        clock += dt
        crowd.update(time:clock);residents?.update(dt:dt,robots:robots,pedestrians:streetResidents?.bodies ?? [],visible:visible)
        streetResidents?.update(dt:dt,obstacles:robots+(residents?.bodies ?? []),visible:visible)
    }
    func reset() {
        clock=0;crowd.update(time:0);residents?.reset();streetResidents?.reset()
        for node in explorationNodes { node.isHidden=true }
    }
    /// Clip the chase/orbit boom against simple scenery bounds, with a small
    /// near-plane margin, including buildings beyond the race-side district.
    func cameraPivot(position:SIMD3<Double>,chassisHeight:Double)->SCNVector3 {
        let head=SCNVector3(position.x,position.y+chassisHeight+0.18,position.z)
        // The distance field anticipates walls and lintels before their edge
        // crosses an upward ray; it also covers low beams intersecting head Y.
        let rise=min(0.72,cameraRoom(at:head,range:0.9))
        let raised=SCNVector3(position.x,Double(head.y)+rise,position.z)
        return clearCamera(from:head,to:raised)
    }
    /// Nearby wall clearance anticipates corner occlusion before the boom ray
    /// suddenly crosses a facade. It varies continuously with player position.
    func cameraRoom(at point:SCNVector3,range:Double=9)->Double {
        let a=SIMD3<Double>(Double(point.x),Double(point.y),Double(point.z))
        let radius=max(0.1,(range+0.2)/sqrt(2))
        let query=RobotCollisions.Body(position:a,profile:.init(mass:1,halfWidth:radius,halfDepth:radius,height:1))
        var room=Double.infinity
        for body in (collisionWorld.nearby(query)+InfieldLayout.obstacles) where body.profile.mass != 70 {
            let d=a-body.position,c=cos(body.heading),s=sin(body.heading),p=body.profile
            let q=SIMD3(abs(c*d.x-s*d.z)-p.halfWidth,max(-d.y,d.y-p.height),abs(s*d.x+c*d.z)-p.halfDepth)
            room=min(room,simd_length(simd_max(q,.zero)))
        }
        for (low,high) in cameraBounds { room=min(room,simd_length(simd_max(simd_max(low-a,a-high),.zero))) }
        return max(0,room-0.16)
    }
    /// Lift a long dune boom over ridges continuously, rather than collapsing
    /// it several metres when a shallow ray first becomes tangent to a crest.
    func terrainCamera(from pivot:SCNVector3,to desired:SCNVector3)->SCNVector3 {
        guard max(abs(Double(pivot.x)),abs(Double(pivot.z)))>DesertTerrain.townEdge else { return desired }
        var result=desired
        for i in 1...64 {
            let t=Double(i)/64,x=Double(pivot.x)+(Double(desired.x)-Double(pivot.x))*t,z=Double(pivot.z)+(Double(desired.z)-Double(pivot.z))*t
            let needed=(DirtCourse.height(x:x,z:z)+0.25-Double(pivot.y)*(1-t))/t
            result.y=max(result.y,CGFloat(needed))
        }
        return result
    }
    func clearCamera(from target:SCNVector3,to desired:SCNVector3)->SCNVector3 {
        let a=SIMD3<Double>(Double(target.x),Double(target.y),Double(target.z))
        let b=SIMD3<Double>(Double(desired.x),Double(desired.y),Double(desired.z)), delta=b-a
        var limit=1.0
        func clip(_ origin:SIMD3<Double>,_ direction:SIMD3<Double>,_ low:SIMD3<Double>,_ high:SIMD3<Double>) {
            let lo=low-SIMD3(repeating:0.14),hi=high+SIMD3(repeating:0.14)
            var enter=0.0,leave=1.0
            for axis in 0..<3 {
                if abs(direction[axis])<1e-8 {
                    if origin[axis]<lo[axis] || origin[axis]>hi[axis] { return }
                } else {
                    let t0=(lo[axis]-origin[axis])/direction[axis],t1=(hi[axis]-origin[axis])/direction[axis]
                    enter=max(enter,min(t0,t1));leave=min(leave,max(t0,t1))
                    if enter>leave { return }
                }
            }
            limit=min(limit,max(0,enter-0.01/max(0.01,simd_length(delta))))
        }
        // Use the actual individual, rotated walls. Compound envelopes cover
        // empty courtyards and squeeze a camera even in a clear passage.
        let middle=(a+b)/2,radius=max(0.1,simd_length(delta)/2+0.2)
        let query=RobotCollisions.Body(position:middle,profile:.init(mass:1,halfWidth:radius,halfDepth:radius,height:1))
        for body in (collisionWorld.nearby(query)+InfieldLayout.obstacles) where body.profile.mass != 70 {
            let c=cos(body.heading),s=sin(body.heading)
            func local(_ p:SIMD3<Double>)->SIMD3<Double> { SIMD3(c*p.x-s*p.z,p.y,s*p.x+c*p.z) }
            let p=body.profile
            clip(local(a-body.position),local(delta),SIMD3(-p.halfWidth,0,-p.halfDepth),SIMD3(p.halfWidth,p.height,p.halfDepth))
        }
        for (low,high) in cameraBounds { clip(a,delta,low,high) }
        // A camera boom can intersect a dune even if both endpoints are above
        // ground. Stop at the first obstruction, leaving near-plane clearance.
        let steps=max(1,Int(ceil(simd_length(delta)*limit/0.20)))
        for i in 1...steps {
            let t=limit*Double(i)/Double(steps),p=a+delta*t
            if p.y<DirtCourse.height(x:p.x,z:p.z)+0.18 {
                limit=limit*Double(i-1)/Double(steps);break
            }
        }
        let result=a+delta*limit
        return SCNVector3(result.x,result.y,result.z)
    }
    var statistics: [String:Int] {
        ["householdYards":householdYards.count,"accessibleCompounds":pedestrianAccess.count,"inaccessibleCompounds":inaccessibleBuildings.count,"explorationCells":explorationNodes.count,"explorationTriangles":explorationTriangles,"streetRoutes":streets.count,"doorConnections":residents?.connections ?? 0,"buildings":buildings,"repairTents":repairLots.count,"infieldHouses":lots.filter{infield($0.x,$0.z)}.count,"people":population,"animatedPeople":population,
         "signs":signs.count,"signTextFits":signs.valid ? 1:0,"walkingPeople":(residents?.walkers.count ?? 0)+(streetResidents?.walkers.count ?? 0),"crowdCells":crowd.cellCount,"crowdNearTriangles":crowd.triangles,"crowdFarTriangles":crowd.farTriangles,"cells":cells.count,"nearTriangles":triangleCount,"farTriangles":coarseTriangles]
    }
    var cityCoveragePassed:Bool {
        (0..<8).allSatisfy { sector in
            lots.filter { lot in
                let radius=hypot(lot.x,lot.z)
                let angle=atan2(lot.z,lot.x)+Double.pi
                return radius>55 && radius<120 && min(7,Int(angle/(2 * Double.pi)*8))==sector
            }.count>16
        }
    }
    var streetNetworkPassed:Bool {
        var reached:Set<Int>=[0]
        var changed=true
        while changed {
            changed=false
            for i in streets.indices where !reached.contains(i) {
                if reached.contains(where:{ j in
                    streets[i].points.contains(where:{streets[j].points.contains($0)})
                }) { reached.insert(i);changed=true }
            }
        }
        return reached.count==streets.count
    }
    func validate() -> Bool {
        buildings>350 && population>120 && (residents?.walkers.count ?? 0)<=18 && (residents?.connections ?? 0)>=4 && triangleCount<440_000 && coarseTriangles<320_000 && cells.count<150
            && inaccessibleBuildings.isEmpty && pedestrianAccess.count==lots.count && venueAccess.count==venueSites.count
            && signs.valid && signs.count>=20 && crowd.valid && cityCoveragePassed && streetNetworkPassed
            && repairLots.count == 2
            && lots.allSatisfy { !infield($0.x,$0.z) && clearLot($0.x,$0.z,$0.width,$0.depth) }
            && InfieldLayout.tentOrigins.indices.allSatisfy { repairFootprintClear($0) }
    }

}

/// Offline-style mesh batching performed once at scene preparation. No SceneKit
/// primitive nodes survive for each window, brick or spectator body part.
final class TownMesh {
    static var inputVertices=0,outputVertices=0
    static var maximumMergedBasisRadians=0.0,mergedBasisComparisons=0
    static var validationPairs:[(SCNGeometry,SCNGeometry)]=[]
    static var tangentProbePairs:[(SCNGeometry,SCNGeometry)]=[]
    var materialSlot=0
    private var groups:[[Int32]]=[[],[],[],[]]
    var wearUV:[CGPoint]=[]
    var wearProjector:((SIMD3<Float>)->CGPoint)?
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
            wearUV.append(wearProjector?(v) ?? CGPoint(x:0.0625,y:0.0625))
            colors += [Float((color>>16)&255)/255*shade,Float((color>>8)&255)/255*shade,Float(color&255)/255*shade,1]
        }
        indices += [base,base+1,base+2]
        groups[materialSlot] += [base,base+1,base+2]
    }
    private struct VertexKey:Hashable {
        let quantizedBasis:Bool
        let position:SIMD3<Float>,normal:SIMD3<Float>,tangent:SIMD3<Float>,bitangent:SIMD3<Float>
        let uv:SIMD2<Double>,wear:SIMD2<Double>,color:SIMD4<Float>
    }
    func geometry(material:SCNMaterial,relativeTo origin:SIMD3<Float> = .zero)->SCNGeometry {
        // Reuse only identical complete vertex attributes. Keep every original
        // triangle, material slot and index order, including normal/UV seams.
        // Guard projected tangent directions too: unrestricted merging would
        // change SceneKit-generated tangent averages for normal maps.
        // This reduces repeated vertex work in both sun maps and the color pass.
        let collectBasisComparisons=CommandLine.arguments.contains("--mesh-reuse-test") || CommandLine.arguments.contains("--benchmark-tangent-reuse")
        func build(reuseNearbyTangents:Bool)->SCNGeometry {
            var lookup:[VertexKey:Int32]=[:],remap:[Int32]=[]
            var local:[SCNVector3]=[],outNormals:[SCNVector3]=[],outUV:[CGPoint]=[],outWear:[CGPoint]=[],outColors:[Float]=[]
            lookup.reserveCapacity(positions.count/2);remap.reserveCapacity(positions.count)
            var basisRepresentatives:[(SIMD3<Float>,SIMD3<Float>)]=[]
            var tangent=SIMD3<Float>.zero,bitangent=SIMD3<Float>.zero
            for i in positions.indices {
                if i%3==0 {
                    func point(_ j:Int)->SIMD3<Float> { SIMD3(Float(positions[j].x)-origin.x,Float(positions[j].y)-origin.y,Float(positions[j].z)-origin.z) }
                    let e1=point(i+1)-point(i),e2=point(i+2)-point(i)
                    let u1=SIMD2(Float(uv[i+1].x)-Float(uv[i].x),Float(uv[i+1].y)-Float(uv[i].y))
                    let u2=SIMD2(Float(uv[i+2].x)-Float(uv[i].x),Float(uv[i+2].y)-Float(uv[i].y))
                    let determinant=u1.x*u2.y-u1.y*u2.x
                    tangent = .zero;bitangent = .zero
                    if abs(determinant)>1e-8 { tangent=(e1*u2.y-e2*u1.y)/determinant;bitangent=(e2*u1.x-e1*u2.x)/determinant }
                }
                let p=SIMD3(Float(positions[i].x)-origin.x,Float(positions[i].y)-origin.y,Float(positions[i].z)-origin.z)
                let n=SIMD3(Float(normals[i].x),Float(normals[i].y),Float(normals[i].z))
                let c=SIMD4(colors[i*4],colors[i*4+1],colors[i*4+2],colors[i*4+3])
                // Compare the basis after normal projection. Raw gradients can
                // differ greatly in length yet generate the same tangent frame.
                // Ill-conditioned or degenerate frames retain the exact key.
                let unitNormal=simd_normalize(n)
                let projectedT=tangent-unitNormal*simd_dot(unitNormal,tangent)
                let projectedB=bitangent-unitNormal*simd_dot(unitNormal,bitangent)
                let tLength=simd_length(projectedT),bLength=simd_length(projectedB)
                let stable=tLength.isFinite && bLength.isFinite && tLength>0.1 && bLength>0.1
                    && tLength>=0.25*simd_length(tangent) && bLength>=0.25*simd_length(bitangent)
                    && abs(simd_dot(simd_cross(unitNormal,projectedT),projectedB))>0.25*tLength*bLength
                func basisKey(_ exact:SIMD3<Float>,_ projected:SIMD3<Float>,_ length:Float)->SIMD3<Float> {
                    guard reuseNearbyTangents && stable else { return exact }
                    let scaled=projected/length*65536
                    return SIMD3(scaled.x.rounded(),scaled.y.rounded(),scaled.z.rounded())/65536
                }
                let key=VertexKey(quantizedBasis:reuseNearbyTangents && stable,position:p,normal:n,tangent:basisKey(tangent,projectedT,tLength),bitangent:basisKey(bitangent,projectedB,bLength),uv:SIMD2(Double(uv[i].x),Double(uv[i].y)),wear:SIMD2(Double(wearUV[i].x),Double(wearUV[i].y)),color:c)
                if let existing=lookup[key] {
                    if reuseNearbyTangents && stable && collectBasisComparisons {
                        let previous=basisRepresentatives[Int(existing)]
                        for (a,b) in [(previous.0,projectedT),(previous.1,projectedB)] {
                            let x=simd_normalize(SIMD3<Double>(a)),y=simd_normalize(SIMD3<Double>(b))
                            let angle=atan2(simd_length(simd_cross(x,y)),simd_dot(x,y))
                            TownMesh.maximumMergedBasisRadians=max(TownMesh.maximumMergedBasisRadians,angle)
                            TownMesh.mergedBasisComparisons += 1
                        }
                    }
                    remap.append(existing);continue
                }
                if reuseNearbyTangents && collectBasisComparisons { basisRepresentatives.append((projectedT,projectedB)) }
                let index=Int32(local.count);lookup[key]=index;remap.append(index)
                local.append(SCNVector3(p));outNormals.append(normals[i]);outUV.append(uv[i]);outWear.append(wearUV[i]);outColors += [c.x,c.y,c.z,c.w]
            }
            let source=outColors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:local.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
            let g=SCNGeometry(sources:[SCNGeometrySource(vertices:local),SCNGeometrySource(normals:outNormals),SCNGeometrySource(textureCoordinates:outUV),SCNGeometrySource(textureCoordinates:outWear),source],elements:groups.filter{!$0.isEmpty}.map{SCNGeometryElement(indices:$0.map{remap[Int($0)]},primitiveType:.triangles)})
            let materials=[material,CityMaterials.cloth,CityMaterials.metal,CityMaterials.adobe]
            g.materials=groups.indices.filter{!groups[$0].isEmpty}.map{materials[$0]}
            return g
        }
        let reuseNearbyTangents = !CommandLine.arguments.contains("--benchmark-exact-tangents")
        let g=build(reuseNearbyTangents:reuseNearbyTangents)
        if CommandLine.arguments.contains("--benchmark-tangent-reuse") && CommandLine.arguments.contains("--benchmark-gpu-probe") {
            TownMesh.tangentProbePairs.append((build(reuseNearbyTangents:false),g))
        }
        TownMesh.inputVertices += positions.count;TownMesh.outputVertices += g.sources(for:.vertex).first!.vectorCount
        if CommandLine.arguments.contains("--mesh-reuse-test") || CommandLine.arguments.contains("--benchmark-original-vertices") {
            let rawColor=colors.withUnsafeBytes { SCNGeometrySource(data:Data($0),semantic:.color,vectorCount:positions.count,usesFloatComponents:true,componentsPerVector:4,bytesPerComponent:4,dataOffset:0,dataStride:16) }
            let rawLocal=positions.map { SCNVector3(Float($0.x)-origin.x,Float($0.y)-origin.y,Float($0.z)-origin.z) }
            let original=SCNGeometry(sources:[SCNGeometrySource(vertices:rawLocal),SCNGeometrySource(normals:normals),SCNGeometrySource(textureCoordinates:uv),SCNGeometrySource(textureCoordinates:wearUV),rawColor],elements:groups.filter{!$0.isEmpty}.map{SCNGeometryElement(indices:$0,primitiveType:.triangles)})
            original.materials=groups.indices.filter { !groups[$0].isEmpty }.map { [material,CityMaterials.cloth,CityMaterials.metal,CityMaterials.adobe][$0] }
            if CommandLine.arguments.contains("--benchmark-original-vertices") { return original }
            TownMesh.validationPairs.append((original,g))
        }
        return g
    }
}
final class TownCollisionBuilder { var bodies:[RobotCollisions.Body]=[] }

private struct TownPainter {
    let near:TownMesh,far:TownMesh,origin:SIMD3<Float>,yaw:Float
    var collisions:TownCollisionBuilder? = nil
    private func solid(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ d:Double,round:Bool=false,turn:Double=0) {
        guard h>0.08,w>0.04,d>0.04,y+h/2>0.10 else { return }
        let p=point(x,y-h/2,z)
        // Infield fixtures already have their detailed shared collision layout.
        let projection=DirtCourse.projection(x:Double(p.x),z:Double(p.z))
        guard projection.offset>0 && projection.distance>DirtCourse.fenceOffset else { return }
        collisions?.bodies.append(.init(position:SIMD3(Double(p.x),Double(p.y),Double(p.z)),heading:Double(yaw)+turn,profile:.init(mass:1,halfWidth:w/2,halfDepth:d/2,height:h,round:round)))
    }
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
        let middle=(from+to)/2,delta=to-from
        solid(middle.x,middle.y,middle.z,radius*2,abs(delta.y)+radius*2,hypot(delta.x,delta.z)+radius*2,turn:atan2(delta.x,delta.z))
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
    /// Replace the wall face itself with a jagged opening, inset reveals and a dark cavity.
    private func damagedWall(_ v0:SIMD3<Float>,_ v1:SIMD3<Float>,_ v2:SIMD3<Float>,_ v3:SIMD3<Float>,_ ink:UInt32,_ seed:Int) {
        let normal=simd_normalize(simd_cross(v3-v0,v1-v0))
        func sample(_ u:Float,_ v:Float)->SIMD3<Float> { (v0*(1-u)+v1*u)*(1-v)+(v3*(1-u)+v2*u)*v }
        let boundary:[SIMD2<Float>]=[SIMD2(0,0),SIMD2(0.5,0),SIMD2(1,0),SIMD2(1,0.5),SIMD2(1,1),SIMD2(0.5,1),SIMD2(0,1),SIMD2(0,0.5)]
        let center=SIMD2<Float>(0.28+Float(seed%41)*0.01,0.30+Float((seed/41)%34)*0.01)
        let holes=boundary.enumerated().map { i,p -> SIMD3<Float> in
            let jitter:Float=0.73+Float((seed+i*7)%5)*0.12
            let angle=Float(i)*Float.pi/4-Float.pi*0.75
            let uv=center+SIMD2(cos(angle)*(0.10+Float(seed%7)*0.01),sin(angle)*(0.11+Float((seed/7)%5)*0.012))*jitter
            return sample(uv.x,uv.y)
        }
        let depth:Float=0.13+Float(seed%4)*0.025
        let backing=sample(center.x,center.y)-normal*(depth+0.035)
        for i in 0..<8 {
            let j=(i+1)%8,a=boundary[i],b=boundary[j]
            quad(sample(a.x,a.y),holes[i],holes[j],sample(b.x,b.y),ink,true)
            let innerA=holes[i]-normal*depth,innerB=holes[j]-normal*depth
            quad(holes[i],innerA,innerB,holes[j],Self.tone(ink,i%3==0 ? 0.58:0.76),true)
            tri(backing,innerB,innerA,Self.tone(ink,0.48),true)
        }
        // The tiny cavity disappears at the existing architecture LOD distance.
        far.triangle(v0,v3,v2,ink);far.triangle(v0,v2,v1,ink)
    }

    func adobe(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ d:Double,_ ink:UInt32,simple:Bool=false) {
        let oldNear=near.materialSlot,oldFar=far.materialSlot
        // Two scanned plaster traditions, selected per compound, not a repeating stain.
        if max(abs(origin.x),abs(origin.z))>30 && abs(Int(origin.x*17+origin.z*31))%5 != 0 {
            near.materialSlot=3;far.materialSlot=3
        }
        defer { near.materialSlot=oldNear;far.materialSlot=oldFar }
        if simple { box(x,y,z,w,h,d,ink);return }
        solid(x,y,z,w,h,d)
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
            func hash(_ input:Int)->Int {
                var n=UInt32(truncatingIfNeeded:input)
                n=(n ^ (n >> 16)) &* 0x7feb352d;n=(n ^ (n >> 15)) &* 0x846ca68b
                return Int((n ^ (n >> 16)) & 0x7fffffff)
            }
            let buildingSeed=hash(Int(origin.x*17)*73856093 ^ Int(origin.z*17)*19349663)
            let wearSeed=hash(buildingSeed ^ Int(x*53+y*101+w*71+d*97) ^ i*379)
            let condition=buildingSeed%100
            let history=condition<30 ? 0:(condition<70 ? 1:(condition<92 ? 2:3))
            let tile=history*16+wearSeed%16
            let along=v1-v0,up=v3-v0
            let mapper:(SIMD3<Float>)->CGPoint = { vertex in
                let relative=vertex-v0
                let u=max(0,min(1,simd_dot(relative,along)/simd_length_squared(along)))
                let v=max(0,min(1,simd_dot(relative,up)/simd_length_squared(up)))
                return CGPoint(x:(Double(tile%8)+0.05+Double(u)*0.90)/8,
                               y:(Double(tile/8)+0.05+Double(1-v)*0.90)/8)
            }
            // Broad wear belongs to walls; roofs, pavement and equipment retain their own surfaces.
            near.wearProjector=mapper;far.wearProjector=mapper
            defer { near.wearProjector=nil;far.wearProjector=nil }
            let worn=w>1.5 && h>1.5 && history==3 && wearSeed%3 != 0
            if worn && i == (wearSeed%3==0 ? 11:5) {
                damagedWall(v0,v1,v2,v3,ink,wearSeed)
            } else {
                smoothQuad([v0,v3,v2,v1],[normal(a.2,0),normal(a.2,0.18),normal(b.2,0.18),normal(b.2,0)])
            }
            near.wearProjector=nil;far.wearProjector=nil
            smoothQuad([v3,v5,v4,v2],[normal(a.2,0.18),normal(a.2,1.3),normal(b.2,1.3),normal(b.2,0.18)])
            tri(point(x,y+h/2,z),v4,v5,ink,false)
        }
    }

    func ring(_ x:Double,_ y:Double,_ z:Double,_ outer:Double,_ inner:Double,_ h:Double,_ ink:UInt32,sides:Int=24,entry:Bool=false) {
        for i in 0..<sides {
            let a=Double(i)*2 * Double.pi/Double(sides),b=Double(i+1)*2 * Double.pi/Double(sides)
            if entry && abs((a+b)/2-Double.pi/2)<Double.pi/8 { continue }
            let mid=(a+b)/2,r=(outer+inner)/2
            solid(x+cos(mid)*r,y,z+sin(mid)*r,(b-a)*r+0.02,h,outer-inner,turn:Double.pi/2-mid)
            let lo=y-h/2,hi=y+h/2
            let a0=point(x+cos(a)*outer,lo,z+sin(a)*outer),b0=point(x+cos(b)*outer,lo,z+sin(b)*outer)
            let a1=point(x+cos(a)*outer,hi,z+sin(a)*outer),b1=point(x+cos(b)*outer,hi,z+sin(b)*outer)
            let a2=point(x+cos(a)*inner,hi,z+sin(a)*inner),b2=point(x+cos(b)*inner,hi,z+sin(b)*inner)
            let a3=point(x+cos(a)*inner,lo,z+sin(a)*inner),b3=point(x+cos(b)*inner,lo,z+sin(b)*inner)
            quad(a0,a1,b1,b0,ink,false);quad(a1,a2,b2,b1,ink,false);quad(a2,a3,b3,b2,ink,false)
            if entry && abs(a-Double.pi*5/8)<0.001 { quad(a0,a3,a2,a1,ink,false) }
            if entry && abs(b-Double.pi*3/8)<0.001 { quad(b0,b1,b2,b3,ink,false) }
        }
    }
    /// Three solid wall pieces leave an actual walkable vestibule in the facade.
    func residentHouse(_ w:Double,_ h:Double,_ d:Double,_ ink:UInt32,doorX:Double) {
        let x=doorX,opening=0.92,depth=min(1.2,d*0.45)
        let left=x-opening/2+w/2,right=w/2-x-opening/2
        adobe(-w/2+left/2,h/2,0,left,h,d,ink)
        adobe(w/2-right/2,h/2,0,right,h,d,ink)
        adobe(x,(h+1.3)/2,d/2-depth/2,opening,h-1.3,depth,ink)
        adobe(x,h/2,-depth/2,opening,h,d-depth,ink)
        box(x,0.005,d/2-depth/2,opening,0.01,depth,0x71634e)
    }
    /// Five related building traditions: rounded adobe arch, clipped lintel,
    /// pointed arch, broad workshop arch, and a plain metal service entrance.
    func cityDoor(_ w:Double,_ h:Double,_ ink:UInt32,variant:Int,open:Bool) {
        let thickness=[0.11,0.16,0.09,0.14,0.085][variant]
        let frameInk=Self.tone(ink,[1.04,0.90,0.98,1.08,0.83][variant])
        if open {
            for side in [-1.0,1] { box(side*(w/2+thickness/2),h/2,0.045,thickness,h,0.18,frameInk) }
            box(0,h+thickness/2,0.045,w+2*thickness,thickness,0.18,frameInk)
            if variant==0 || variant==2 { box(0,h+0.16,0.025,w+0.34,0.07,0.24,ink) }
        } else {
            func outline(_ width:Double,_ height:Double)->[SIMD2<Double>] {
                let r=width/2
                var result=[SIMD2(-r,0),SIMD2(r,0)]
                if variant==1 {
                    result += [SIMD2(r,height-0.23),SIMD2(r-0.17,height),SIMD2(-r+0.17,height),SIMD2(-r,height-0.23)]
                } else if variant==2 {
                    result += [SIMD2(r,height*0.66),SIMD2(r*0.65,height*0.86),SIMD2(0,height),SIMD2(-r*0.65,height*0.86),SIMD2(-r,height*0.66)]
                } else if variant==4 { result += [SIMD2(r,height),SIMD2(-r,height)] }
                else {
                    let rise=variant==3 ? width*0.22:r
                    for i in 0...12 { let a=Double(i)*Double.pi/12;result.append(SIMD2(cos(a)*r,height-rise+sin(a)*rise)) }
                }
                return result
            }
            let inside=outline(w,h),outside=outline(w+2*thickness,h+thickness)
            let center=point(0,h*0.4,0.038)
            for i in inside.indices {
                let j=(i+1)%inside.count,a=inside[i],b=inside[j],c=outside[i],d=outside[j]
                tri(center,point(a.x,a.y,0.038),point(b.x,b.y,0.038),0x302b26,false)
                if i==0 { continue } // Sand meets the threshold without a step.
                quad(point(a.x,a.y,0.07),point(c.x,c.y,0.15),point(d.x,d.y,0.15),point(b.x,b.y,0.07),frameInk,false)
                quad(point(c.x,c.y,-0.035),point(d.x,d.y,-0.035),point(d.x,d.y,0.15),point(c.x,c.y,0.15),ink,false)
            }
            let colors:[UInt32]=[0x72634f,0x65716b,0x846654,0x574b40,0x74716a]
            near.materialSlot=2;far.materialSlot=2
            let leaf=outline(w*0.91,h-0.045),middle=point(0,h*0.4,0.052)
            for i in leaf.indices {
                let a=leaf[i],b=leaf[(i+1)%leaf.count]
                tri(middle,point(a.x,a.y,0.052),point(b.x,b.y,0.052),colors[variant],false)
            }
            func top(_ x:Double)->Double {
                var result=0.0
                for (a,b) in zip(leaf,leaf.dropFirst()+[leaf[0]]) where abs(b.x-a.x)>0.001 {
                    let t=(x-a.x)/(b.x-a.x)
                    if t>=0 && t<=1 { result=max(result,a.y+(b.y-a.y)*t) }
                }
                return result
            }
            let divisions=variant==3 ? 5:(variant==4 ? 3:2)
            for k in 1..<divisions {
                let x = -w*0.44+w*0.88*Double(k)/Double(divisions)
                let height=max(0.1,top(x)-0.04)
                box(x,height/2,0.061,0.016,height,0.012,0x3d3730,detail:true)
            }
            if variant==1 || variant==4 {
                for y in [0.20,0.47] { box(0,h*y,0.064,w*0.81,0.027,0.018,0x99917c,detail:true) }
            }
            box(w*0.27,h*0.42,0.077,0.04,0.13,0.038,0xb3a18a,detail:true)
            near.materialSlot=0;far.materialSlot=0
        }
        // Wall-mounted access control varies sides and height with the family.
        if variant==1 || variant==4 {
            box(w/2+thickness+0.10,h*0.64,0.045,0.12,0.20,0.07,0x545b57,detail:true)
            box(w/2+thickness+0.10,h*0.68,0.083,0.055,0.035,0.01,0x9fa990,detail:true)
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
        let nx=w>5 ? 24:12,nz=d>3 ? 10:6
        func v(_ i:Int,_ j:Int)->SIMD3<Float> {
            let u=Double(i)/Double(nx),t=Double(j)/Double(nz)
            let roof=ridge ? rise*(1-abs(u*2-1)) : -t*0.15
            let sag=min(0.38,d*0.065)*sin(t * .pi)*(0.65+0.35*sin(u * .pi))+0.045*sin(u*8 * .pi)*sin(t * .pi)
            return point(x+(u-0.5)*w,y+roof-sag,z+(t-0.5)*d)
        }
        for i in 0..<nx { for j in 0..<nz {
            let a=v(i,j),b=v(i+1,j),c=v(i+1,j+1),e=v(i,j+1)
            let panelInk:UInt32 = (i+Int(abs(origin.x+origin.z)))%5==0 ? Self.tone(ink,0.88):ink
            quad(a,e,c,b,panelInk,true)
        }}
        // Distant cloth keeps the silhouette without folds.
        let a=v(0,0),b=v(nx,0),c=v(nx,nz),e=v(0,nz)
        far.triangle(a,e,c,ink);far.triangle(a,c,b,ink)
    }
    func repairRug(_ w:Double,_ d:Double,_ variation:Int) {
        if variation==0 { triangularRug();return }
        near.materialSlot=1;far.materialSlot=1
        defer { near.materialSlot=0;far.materialSlot=0 }
        let base:UInt32=variation==0 ? 0x8a7960:0x827568
        func patch(_ x:Double,_ z:Double,_ width:Double,_ depth:Double,_ ink:UInt32,_ lift:Double=0) {
            let y=0.004+lift
            quad(point(x-width/2,y,z-depth/2),point(x-width/2,y,z+depth/2),point(x+width/2,y,z+depth/2),point(x+width/2,y,z-depth/2),ink,false)
        }
        patch(0,0,w,d,base)
        for side in [-1.0,1.0] {
            patch(side*(w/2-0.16),0,0.19,d-0.13,0x545849,0.002)
            patch(0,side*(d/2-0.17),w-0.14,0.21,0x545849,0.002)
            patch(0,side*(d/2-0.33),w-0.31,0.035,0xb4a17d,0.003)
            for j in 0..<26 {
                let x = -w/2+0.10+Double(j)*(w-0.20)/25
                let length=0.09+Double((j*7+variation)%5)*0.013
                quad(point(x,0.003,side*d/2),point(x+0.024,0.003,side*d/2),point(x+0.02,0.001,side*(d/2+length)),point(x-0.005,0.001,side*(d/2+length)),0xa69574,true)
            }
        }
        // Muted woven checks remain legible as a single textile, with no raised tile seams.
        for row in 0..<5 { for col in 0..<5 {
            let x = -1.20+Double(col)*0.60,z = -1.20+Double(row)*0.60
            if (row+col+variation)%2==0 { patch(x,z,0.58,0.58,0x958b70,0.001) }
        }}
        for side in [-1.0,1.0] { for j in 0..<7 {
            let x = -1.2+Double(j)*0.4,z=side*(d/2-0.17),y=0.008
            quad(point(x-0.085,y,z),point(x,y,z+0.068),point(x+0.085,y,z),point(x,y,z-0.068),0xb7a783,true)
        }}
    }

    private func triangularRug() {
        near.materialSlot=1;far.materialSlot=1
        defer { near.materialSlot=0;far.materialSlot=0 }
        let outline=InfieldLayout.orangeCorners.map{$0*0.94}
        func patch(_ polygon:[SIMD2<Double>],_ ink:UInt32,_ y:Double) {
            var clipped=polygon
            for i in 0..<3 {
                let a=outline[i],b=outline[(i+1)%3],d=b-a
                func distance(_ p:SIMD2<Double>)->Double { d.x*(p.y-a.y)-d.y*(p.x-a.x) }
                var output:[SIMD2<Double>]=[]
                guard !clipped.isEmpty else { return }
                for j in clipped.indices {
                    let p=clipped[j],q=clipped[(j+1)%clipped.count],dp=distance(p),dq=distance(q)
                    if dp<=0 { output.append(p) }
                    if (dp<=0) != (dq<=0) { output.append(p+(q-p)*(dp/(dp-dq))) }
                }
                clipped=output
            }
            guard clipped.count>=3 else { return }
            for i in 1..<clipped.count-1 {
                let a=clipped[0],b=clipped[i],c=clipped[i+1]
                tri(point(a.x,y,a.y),point(b.x,y,b.y),point(c.x,y,c.y),ink,false)
            }
        }
        patch(outline,0x8a7960,0.004)
        for x in -3...2 { for z in -3...2 where (x+z)%2==0 {
            let a=Double(x)*0.5,b=Double(z)*0.5
            patch([SIMD2(a,b),SIMD2(a,b+0.49),SIMD2(a+0.49,b+0.49),SIMD2(a+0.49,b)],0x958b70,0.006)
        }}
        let center=outline.reduce(SIMD2<Double>.zero,+)/3
        for i in 0..<3 {
            let a=outline[i],b=outline[(i+1)%3],ia=a+(center-a)*0.14,ib=b+(center-b)*0.14
            patch([a,b,ib,ia],0x545849,0.009)
            let direction=b-a,n=simd_normalize(SIMD2(-direction.y,direction.x))
            for j in 1..<17 {
                let p=a+direction*Double(j)/17,q=p+simd_normalize(direction)*0.022
                quad(point(p.x,0.006,p.y),point(q.x,0.006,q.y),point(q.x+n.x*0.08,0.003,q.y+n.y*0.08),point(p.x+n.x*0.08,0.003,p.y+n.y*0.08),0xa69574,true)
            }
        }
    }

    func triangularRepairCanopy(_ ink:UInt32) {
        let corners=InfieldLayout.orangeCorners.map { SIMD3<Double>($0.x,2,$0.y) }
        near.materialSlot=1;far.materialSlot=1
        let center=point(-0.6,2.3,0.53)
        for i in 0..<3 { let a=corners[i],b=corners[(i+1)%3]
            tri(center,point(a.x,a.y,a.z),point(b.x,b.y,b.z),ink,false)
        }
        near.materialSlot=0;far.materialSlot=0
        for a in corners { beam(SIMD3(a.x,0,a.z),a,0.035,0x625b50) }
        for i in 0..<3 { cable(corners[i],corners[(i+1)%3],0.025,0x625b50) }
    }
    func engineAssembly(_ x:Double,_ y:Double,_ z:Double,scale:Double,variant:Int) {
        let oldNear=near.materialSlot,oldFar=far.materialSlot
        near.materialSlot=2;far.materialSlot=2
        defer { near.materialSlot=oldNear;far.materialSlot=oldFar }
        let ink:UInt32=variant%2==0 ? 0x7e725e:0x7b5540
        box(x,y+0.17*scale,z,0.88*scale,0.34*scale,0.64*scale,ink)
        for side in [-1.0,1] {
            cylinder(x+side*0.28*scale,y+0.45*scale,z,0.18*scale,0.16*scale,0.34*scale,ink,sides:20)
            for fin in 0..<5 { cylinder(x+side*0.28*scale,y+(0.31+Double(fin)*0.065)*scale,z,0.205*scale,0.205*scale,0.025*scale,0x9b9682,sides:16) }
            for dz in [-0.20,0.20] { box(x+side*0.42*scale,y+0.355*scale,z+dz*scale,0.045*scale,0.035*scale,0.045*scale,0x403d32,detail:true) }
        }
        box(x,y+0.21*scale,z+0.335*scale,0.44*scale,0.20*scale,0.055*scale,0x393f39)
        for k in 0..<7 { box(x+Double(k-3)*0.05*scale,y+0.21*scale,z+0.37*scale,0.014*scale,0.15*scale,0.015*scale,0x96957f,detail:true) }
    }
    func droidSalvage(_ x:Double,_ y:Double,_ z:Double,variant:Int) {
        let oldNear=near.materialSlot,oldFar=far.materialSlot
        near.materialSlot=2;far.materialSlot=2
        defer { near.materialSlot=oldNear;far.materialSlot=oldFar }
        let ink:UInt32=variant%2==0 ? 0xaaa58e:0x8f7763
        cylinder(x,y+0.29,z,0.26,0.25,0.58,ink,sides:24)
        for k in 0..<3 { box(x,y+0.13+Double(k)*0.16,z+0.247,0.25,0.10,0.04,0x485e5b) }
        dome(x,y+0.59,z,0.29,0.24,0.29,0xa8ada1,sides:24)
        cylinder(x,y+0.585,z,0.295,0.295,0.035,0x4f5f59,sides:24)
        box(x-0.08,y+0.72,z+0.25,0.12,0.10,0.065,0x283a37)
        for side in [-1.0,1] {
            box(x+side*0.31,y+0.21,z,0.10,0.42,0.13,ink)
            box(x+side*0.31,y+0.055,z+0.065,0.17,0.11,0.28,0x8b8b76)
        }
    }

    func salvage(_ w:Double,_ d:Double,_ h:Double,_ kind:Int,_ seed:Int) {
        near.materialSlot=2;far.materialSlot=2
        defer { near.materialSlot=0;far.materialSlot=0 }
        let rust:UInt32=[0x80533b,0x98603e,0x694c39,0x9c704a][seed%4]
        if kind==0 {
            ring(0,h/2,0,w/2,w*0.29,h,rust,sides:16)
            for i in 0..<6 { let a=Double(i)*Double.pi/3
                box(cos(a)*w*0.39,h+0.01,sin(a)*w*0.39,0.045,0.025,0.045,0x51483d,detail:true)
            }
        } else if kind==1 {
            box(0,h*0.46,0,w*0.78,h*0.92,d*0.80,rust)
            for k in 0..<5 { box(0,h*0.95,-d*0.32+Double(k)*d*0.16,w,0.035,0.035,0x564b3e,detail:true) }
            cylinder(0,h,0,w*0.16,w*0.16,0.04,0x6b6350,sides:10,detail:true)
        } else {
            box(0,h/2,0,w,h,d,rust)
            for k in 0..<4 { box(-w*0.38+Double(k)*w*0.25,h+0.005,0,0.025,0.015,d*0.85,0x514a3d,detail:true) }
        }
        for k in 0..<7 {
            let xx=sin(Double(seed*23+k*17))*w*0.32,zz=cos(Double(seed*11+k*29))*d*0.29
            box(xx,h+0.02,zz,0.035+Double(k%3)*0.012,0.005,0.025,0xb07a45,detail:true)
        }
    }

    func canopy(_ x:Double,_ z:Double,_ w:Double,_ d:Double,_ y:Double,_ rise:Double,_ ink:UInt32) {
        cloth(x,z,w,d,y,rise,ink,ridge:true)
        for side in [-1.0,1.0] { for zz in [-d/2,d/2] {
            beam(SIMD3(x+side*w/2,0,z+zz),SIMD3(x+side*w/2,y,z+zz),0.035,0x625b50)
        }}
        beam(SIMD3(x,y+rise,z-d/2),SIMD3(x,y+rise,z+d/2),0.04,0x625b50)
    }
    /// Open voussoir arch. Intrados, jambs and wall thickness are actual geometry.
    func arcade(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ depth:Double,_ thickness:Double,_ ink:UInt32,wallTop:Double?=nil) {
        let r=w/2,spring=y+h-r
        for side in [-1.0,1] {
            adobe(x+side*(r+thickness/2),(y+spring)/2,z,thickness,spring-y,depth,ink)
            box(x+side*(r+thickness/2),y+0.055,z,thickness+0.06,0.11,depth+0.08,Self.tone(ink,0.85),detail:true)
        }
        for i in 0..<14 {
            let joint=wallTop==nil ? 0.005:0.0
            let a=Double(i)*Double.pi/14+joint,b=Double(i+1)*Double.pi/14-joint
            let color=Self.tone(ink,[0.97,1.015,0.94,1.0][i%4])
            func v(_ angle:Double,_ radius:Double,_ zz:Double)->SIMD3<Float> {
                point(x+cos(angle)*radius,spring+sin(angle)*radius,zz)
            }
            let f=z+depth/2,back=z-depth/2
            let a0=v(a,r,f),b0=v(b,r,f),a1=v(a,r+thickness,f),b1=v(b,r+thickness,f)
            let c0=v(a,r,back),d0=v(b,r,back),c1=v(a,r+thickness,back),d1=v(b,r+thickness,back)
            quad(a0,a1,b1,b0,color,false);quad(d0,d1,c1,c0,color,false)
            quad(a0,b0,d0,c0,Self.tone(color,0.72),false)
            quad(b1,a1,c1,d1,color,false)
            quad(c0,c1,a1,a0,color,true);quad(b0,b1,d1,d0,color,true)
            if let top=wallTop {
                // Fill the spandrel to the horizontal roof course. Sharing the
                // exact outer arc avoids slivers of daylight through masonry.
                let af=point(x+cos(a)*(r+thickness),top,f),bf=point(x+cos(b)*(r+thickness),top,f)
                let ab=point(x+cos(a)*(r+thickness),top,back),bb=point(x+cos(b)*(r+thickness),top,back)
                quad(a1,af,bf,b1,ink,false);quad(d1,bb,ab,c1,ink,false)
                quad(af,ab,bb,bf,ink,false)
            }
        }
    }
    /// Thin, irregular patches follow wall surfaces; no extra decal pass or transparency.
    func plasterPatch(_ x:Double,_ y:Double,_ z:Double,_ rx:Double,_ ry:Double,_ ink:UInt32,seed:Int) {
        let n=11
        for i in 0..<n {
            func edge(_ k:Int)->SIMD3<Float> {
                let a=Double(k)*2 * .pi/Double(n)
                let r=0.79+0.16*sin(Double(k*17+seed*23))
                return point(x+cos(a)*rx*r,y+sin(a)*ry*r,z)
            }
            tri(point(x,y,z),edge(i),edge(i+1),ink,true)
        }
    }
    func cable(_ from:SIMD3<Double>,_ to:SIMD3<Double>,_ sag:Double,_ ink:UInt32) {
        var previous=from
        for i in 1...8 {
            let t=Double(i)/8
            let p=from+(to-from)*t-SIMD3(0,sin(t * .pi)*sag,0)
            beam(previous,p,0.009,ink,sides:5);previous=p
        }
    }
    func valance(_ x:Double,_ z:Double,_ width:Double,_ y:Double,_ drop:Double,_ ink:UInt32,rise:Double=0) {
        near.materialSlot=1
        defer { near.materialSlot=0 }
        for i in 0..<24 {
            let a=Double(i)/24,b=Double(i+1)/24
            func top(_ u:Double)->SIMD3<Float> { point(x+(u-0.5)*width,y+rise*(1-abs(u*2-1)),z) }
            func bottom(_ u:Double)->SIMD3<Float> { point(x+(u-0.5)*width,y+rise*(1-abs(u*2-1))-drop*(0.78+0.22*sin(u*6 * .pi)),z+0.025*sin(u*12 * .pi)) }
            quad(top(a),bottom(a),bottom(b),top(b),ink,true)
        }
    }
    func crate(_ x:Double,_ y:Double,_ z:Double,_ size:Double,_ ink:UInt32) {
        box(x,y+size/2,z,size,size,size*0.72,Self.tone(ink,0.72),detail:true)
        for k in 0..<4 {
            box(x,y+(Double(k)+0.5)*size/4,z+size*0.37,size*0.94,size*0.19,0.026,ink,detail:true)
        }
        for side in [-1.0,1] { box(x+side*size*0.38,y+size/2,z+size*0.39,0.045,size,0.025,Self.tone(ink,0.84),detail:true) }
    }
    /// Open slatted tray with individual produce, not a solid coloured cuboid.
    func produceTray(_ x:Double,_ y:Double,_ z:Double,variant:Int) {
        box(x,y+0.015,z,0.44,0.03,0.48,0x776044,detail:true)
        for side in [-1.0,1] {
            box(x+side*0.22,y+0.09,z,0.025,0.15,0.50,0x96734d,detail:true)
            for level in [0.035,0.115] { box(x,y+level,z+side*0.24,0.44,0.04,0.025,0x96734d,detail:true) }
        }
        let colors:[UInt32]=[0x987342,0x8a5637,0x7b8152,0xada069,0x6a7450]
        for k in 0..<9 {
            let xx=x+Double(k%3-1)*0.135+sin(Double(k*7+variant))*0.012
            let zz=z+Double(k/3-1)*0.145+cos(Double(k*11+variant))*0.013
            let r=0.059+Double((k+variant)%3)*0.006
            dome(xx,y+0.07,zz,r,r*(variant%2==0 ? 1.4:0.9),r,colors[(k/3+variant)%colors.count],sides:12,detail:true)
        }
    }

    func vessel(_ x:Double,_ y:Double,_ z:Double,_ size:Double,_ ink:UInt32) {
        // Lathed clay profile includes the lip and hollow interior, with smooth normals.
        let profile:[SIMD2<Double>]=[SIMD2(0.24,0),SIMD2(0.43,0.24),SIMD2(0.40,0.43),SIMD2(0.22,0.56),SIMD2(0.25,0.66),SIMD2(0.17,0.66),SIMD2(0.17,0.53),SIMD2(0.26,0.13),SIMD2(0,0.12)]
        for j in 0..<(profile.count-1) {
            let lo=profile[j],hi=profile[j+1],delta=hi-lo
            func vertex(_ a:Double,_ v:SIMD2<Double>)->SIMD3<Float> { point(x+cos(a)*v.x*size,y+v.y*size,z+sin(a)*v.x*size) }
            func normal(_ a:Double)->SIMD3<Float> {
                simd_normalize(SIMD3(Float(cos(a-Double(yaw))*delta.y),Float(-delta.x),Float(sin(a-Double(yaw))*delta.y)))
            }
            for k in 0..<16 {
                let a=Double(k)*2 * Double.pi/16,b=Double(k+1)*2 * Double.pi/16
                let v0=vertex(a,lo),v1=vertex(a,hi),v2=vertex(b,hi),v3=vertex(b,lo)
                let c=j>4 ? Self.tone(ink,0.66):ink
                near.triangle(v0,v1,v2,c,smooth:[normal(a),normal(a),normal(b)])
                near.triangle(v0,v2,v3,c,smooth:[normal(a),normal(b),normal(b)])
            }
        }
    }

    static func tone(_ ink:UInt32,_ factor:Double)->UInt32 {
        let r=UInt32(min(255,Double((ink>>16)&255)*factor)),g=UInt32(min(255,Double((ink>>8)&255)*factor)),b=UInt32(min(255,Double(ink&255)*factor))
        return r<<16 | g<<8 | b
    }
    func box(_ x:Double,_ y:Double,_ z:Double,_ w:Double,_ h:Double,_ d:Double,_ ink:UInt32,detail:Bool=false) {
        solid(x,y,z,w,h,d)
        let v=[point(x-w/2,y-h/2,z-d/2),point(x+w/2,y-h/2,z-d/2),point(x+w/2,y+h/2,z-d/2),point(x-w/2,y+h/2,z-d/2),point(x-w/2,y-h/2,z+d/2),point(x+w/2,y-h/2,z+d/2),point(x+w/2,y+h/2,z+d/2),point(x-w/2,y+h/2,z+d/2)]
        for f in [[0,3,2,1],[4,5,6,7],[0,4,7,3],[1,2,6,5],[3,7,6,2],[0,1,5,4]] { quad(v[f[0]],v[f[1]],v[f[2]],v[f[3]],ink,detail) }
    }
    func cylinder(_ x:Double,_ y:Double,_ z:Double,_ bottom:Double,_ top:Double,_ h:Double,_ ink:UInt32,sides:Int=12,detail:Bool=false) {
        solid(x,y,z,max(bottom,top)*2,h,max(bottom,top)*2,round:true)
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
