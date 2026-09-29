import Foundation

/// Closed motocross spline with a start straight, mixed turns and jump sections.
public enum DirtCourse {
    /// Half the compacted lane width; centerline control points stay unchanged.
    public static let playerGrid = (phase: -0.055, offset: -0.72)
    public static let opponentGrid = (phase: -0.14, offset: 0.72)
    public static let startingGrid = [playerGrid, opponentGrid,
        (phase: -0.225, offset: -0.72), (phase: -0.31, offset: 0.72)]

    /// One distinct staggered slot per racer; caller owns the random generator.
    public static func shuffledGrid<R: RandomNumberGenerator>(using random: inout R) -> [(phase: Double, offset: Double)] {
        startingGrid.shuffled(using: &random)
    }
    public static let width = 1.3 * 1.5
    public static let bermWidth = 0.35
    public static let fenceOffset = width + 0.5
    public static let boundaryWallThickness = 0.15
    public static let shoulderEdge = fenceOffset + 0.1
    public static let terrainEdge = shoulderEdge + 0.45
    // Shared by the visible wall opening, chassis constraints and the baked service-route dirt.
    public static let serviceEntryX = -7.5
    public static let serviceEntryHalfWidth = 1.25
    public static func serviceAccess(x:Double,z:Double,clearance:Double=0) -> Bool {
        abs(x-serviceEntryX) < max(0,serviceEntryHalfWidth-clearance) && z > -15.4 && z < -5.3
    }
    public static let railClearance = 0.31
    public static let postHeight = 0.40
    public static let postEmbed = 0.08
    private static let controls: [SIMD2<Double>] = [
        .init(0,-10),.init(8,-10),.init(12,-6),.init(10,0),
        .init(5,0),.init(4,-4),.init(-1,-4),.init(-2,3),
        .init(6,5),.init(10,10),.init(3,12),.init(-6,10),
        .init(-11,5),.init(-10,-2),.init(-10,-9),.init(-5,-10)]
    public static let sampleCount = 768
    private static func center(_ phase: Double) -> SIMD2<Double> {
        let u = (phase / (2 * .pi)).truncatingRemainder(dividingBy: 1)
        let t = (u < 0 ? u + 1 : u) * Double(controls.count)
        let i = Int(t), f = t - Double(i), n = controls.count
        let a = controls[(i+n-1)%n], b = controls[i%n], c = controls[(i+1)%n], d = controls[(i+2)%n]
        // A periodic B-spline keeps tight bends smooth enough for the full
        // lane and shoulder widths (interpolating splines can fold inside turns).
        let constant = a+b*4+c
        let linear = (-a*3+c*3)*f
        let quadratic = (a*3-b*6+c*3)*(f*f)
        let cubic = (-a+b*3-c*3+d)*(f*f*f)
        return (constant+linear+quadratic+cubic)*0.25
    }
    public static func point(_ phase: Double, offset: Double = 0) -> (x: Double, z: Double) {
        let p = center(phase), d = center(phase+0.0001)-center(phase-0.0001)
        let length = hypot(d.x,d.y)
        return (p.x + d.y/length*offset,p.y - d.x/length*offset)
    }
    /// Trim loops in parallel offsets at tight inside bends. Collapsing the
    /// removed samples to their intersection preserves the centerline and phase
    /// indexing while preventing folded terrain and crossing fence segments.
    public static func surfacePoints(offset: Double) -> [SIMD2<Double>] {
        var points = (0..<sampleCount).map { i -> SIMD2<Double> in
            let p = point(Double(i)*2 * .pi/Double(sampleCount),offset:offset)
            return SIMD2(p.x,p.z)
        }
        if abs(offset) > 1.85 {
            func cross(_ a:SIMD2<Double>,_ b:SIMD2<Double>) -> Double { a.x*b.y-a.y*b.x }
            for i in 0..<sampleCount-2 {
                let a = points[i], r = points[i+1]-a
                if r.x*r.x+r.y*r.y < 1e-16 { continue }
                for j in i+2..<sampleCount {
                    if i == 0 && j == sampleCount-1 { continue }
                    let b = points[j], s = points[(j+1)%sampleCount]-b
                    let denominator = cross(r,s)
                    if abs(denominator) < 1e-12 { continue }
                    let t = cross(b-a,s)/denominator, u = cross(b-a,r)/denominator
                    if t > 1e-8 && t < 1-1e-8 && u > 1e-8 && u < 1-1e-8 {
                        let intersection = a+r*t
                        for k in i+1...j { points[k] = intersection }
                        break
                    }
                }
            }
        }
        points.append(points[0])
        return points
    }
    public static func heading(_ phase: Double) -> Double {
        let a = center(phase-0.0001), b = center(phase+0.0001)
        return atan2(b.x-a.x,b.y-a.y)
    }
    private static let samples = (0...sampleCount).map { center(Double($0)*2 * .pi/Double(sampleCount)) }
    private struct ProjectionNode {
        let low: SIMD2<Double>, high: SIMD2<Double>
        let start: Int, end: Int, left: Int, right: Int
        func distanceSquared(_ p: SIMD2<Double>) -> Double {
            let x=max(0,max(low.x-p.x,p.x-high.x))
            let z=max(0,max(low.y-p.y,p.y-high.y))
            return x*x+z*z
        }
    }
    // Adjacent spline segments are spatially coherent. A balanced bounds tree
    // rejects whole arcs while retaining the exact original segment projection.
    private static let projectionNodes: [ProjectionNode] = {
        var nodes:[ProjectionNode]=[]
        func build(_ start:Int,_ end:Int)->Int {
            var lo=SIMD2<Double>(repeating:.infinity),hi=SIMD2<Double>(repeating:-.infinity)
            for i in start...end {
                lo.x=min(lo.x,samples[i].x);lo.y=min(lo.y,samples[i].y)
                hi.x=max(hi.x,samples[i].x);hi.y=max(hi.y,samples[i].y)
            }
            let index=nodes.count
            nodes.append(ProjectionNode(low:lo,high:hi,start:start,end:end,left:-1,right:-1))
            if end-start>12 {
                let middle=(start+end)/2,left=build(start,middle),right=build(middle,end)
                nodes[index]=ProjectionNode(low:lo,high:hi,start:start,end:end,left:left,right:right)
            }
            return index
        }
        _=build(0,sampleCount)
        return nodes
    }()
    public static func projection(x: Double, z: Double) -> (phase: Double, offset: Double, distance: Double) {
        let p = SIMD2<Double>(x,z)
        var best = Double.infinity, bestPhase = 0.0, offset = 0.0, bestIndex=sampleCount
        func search(_ index:Int) {
            let node=projectionNodes[index]
            // Allow rounding slack in the lower bound, including distant queries.
            if node.distanceSquared(p)>best+max(1e-12,best.ulp*8) { return }
            if node.left>=0 {
                let leftDistance=projectionNodes[node.left].distanceSquared(p)
                let rightDistance=projectionNodes[node.right].distanceSquared(p)
                if leftDistance<=rightDistance { search(node.left);search(node.right) }
                else { search(node.right);search(node.left) }
                return
            }
            for i in node.start..<node.end {
                let a = samples[i], d = samples[i+1]-a, v = p-a
                let lengthSquared = d.x*d.x+d.y*d.y
                let t = max(0,min(1,(v.x*d.x+v.y*d.y)/lengthSquared))
                let e = p-(a+d*t), distance = e.x*e.x+e.y*e.y
                // The original ascending scan resolves ties to the first segment.
                if distance < best || (distance == best && bestIndex<sampleCount && i<bestIndex) {
                    best = distance; bestIndex=i
                    bestPhase = (Double(i)+t)*2 * .pi/Double(sampleCount)
                    offset = (e.x*d.y-e.y*d.x)/sqrt(lengthSquared)
                }
            }
        }
        search(0)
        return (bestPhase,offset,sqrt(best))
    }
    public static func phase(x: Double, z: Double) -> Double { projection(x:x,z:z).phase }
    public static func contains(x: Double, z: Double, margin: Double = 0) -> Bool {
        projection(x:x,z:z).distance <= width-margin
    }
    public static func elevation(_ phase: Double, offset: Double = 0) -> Double {
        let u = ((phase/(2 * .pi)).truncatingRemainder(dividingBy:1)+1).truncatingRemainder(dividingBy:1)
        func bump(_ center:Double,_ half:Double,_ height:Double) -> Double {
            let t = abs(u-center)/half
            return t < 1 ? height*pow(cos(t * .pi/2),2) : 0
        }
        // Filled tabletop, rounded rollers, rhythm doubles, raised step-up,
        // then a run of smaller whoops. Features blend into the same mesh.
        let table = max(0,min(1,min((u-0.025)/0.015,(0.085-u)/0.015)))*0.70
        let rollers = bump(0.26,0.020,0.24)+bump(0.30,0.020,0.32)+bump(0.34,0.020,0.24)
        let rhythm = bump(0.49,0.025,0.62)+bump(0.54,0.025,0.56)
        let hill = bump(0.69,0.095,1.30)
        let whoops = (0..<6).reduce(0.0) { $0+bump(0.82+Double($1)*0.015,0.0075,0.16) }
        let h0 = heading(phase-0.01), h1 = heading(phase+0.01)
        let turn = atan2(sin(h1-h0),cos(h1-h0))
        let bank = min(0.35,abs(turn)*3)*pow(max(0,offset*(turn > 0 ? -1 : 1)/width),2)
        // Broad climbs and descents under the jump features leave the grid flat.
        let terrain = bump(0.17,0.065,0.65)+bump(0.40,0.065,0.90)+bump(0.91,0.065,0.55)
        return table+rollers+rhythm+hill+whoops+terrain+bank
    }
    public static func surfaceHeight(_ phase: Double, offset: Double) -> Double {
        let distance = abs(offset)
        let base = elevation(phase,offset:max(-width,min(width,offset)))
        if distance <= width { return base }
        if distance <= width+bermWidth {
            let crest = offset > 0 ? 0.10 : 0.055
            return base + sin((distance-width)/bermWidth * .pi)*crest
        }
        // Retaining walls replace the earthen slope, except at the service ramp.
        let location=point(phase,offset:offset)
        let ramp=offset<0 && serviceAccess(x:location.x,z:location.z)
        if !ramp {
            return distance<=fenceOffset+boundaryWallThickness ? base : -0.025
        }
        if distance <= shoulderEdge { return base }
        let t = max(0,min(1,(distance-shoulderEdge)/(terrainEdge-shoulderEdge)))
        let blend = t*t*(3-2*t)
        return base*(1-blend) - 0.025*blend
    }
    /// Project only against the fence. The robot can slide along it and reverse
    /// away; the compacted lane edge is a traction change, not a collision wall.
    public static func resolveMove(x:Double,z:Double,heading:Double) -> (x:Double,z:Double,contact:Bool) {
        let p = projection(x:x,z:z), tangent = self.heading(p.phase)
        let relative = heading-tangent
        let support = 0.35*abs(cos(relative)) + 0.33*abs(sin(relative))
        if p.offset < 0 && serviceAccess(x:x,z:z,clearance:support) { return (x,z,false) }
        let insideField = p.offset < 0 && p.distance > fenceOffset
        let limit = fenceOffset+(insideField ? boundaryWallThickness+support+0.025 : -support-0.025)
        guard insideField ? p.distance < limit : p.distance > limit else { return (x,z,false) }
        let corrected = point(p.phase,offset:p.offset < 0 ? -limit : limit)
        return (corrected.x,corrected.z,true)
    }
    public static func traction(x:Double,z:Double) -> Double {
        let distance = projection(x:x,z:z).distance
        let loose = max(0,min(1,(distance-(width-0.35))/0.40))
        return 1-loose*0.72
    }
    public static func height(x:Double,z:Double) -> Double {
        let p = projection(x:x,z:z)
        return surfaceHeight(p.phase,offset:p.offset)
    }
}

public struct DirtRace: Sendable {
    public private(set) var countdown = 3.0
    public private(set) var elapsed = 0.0
    public private(set) var laps: [Double] = []
    public private(set) var progress = 0.0
    private var previousPhase = 0.0, lapStart = 0.0
    public var finished: Bool { laps.count == 3 }
    public var currentLap: Double { elapsed - lapStart }
    public var wrongWay = false
    public init(startPhase: Double = 0) { previousPhase = startPhase; progress = startPhase }
    public mutating func countDown(dt: Double) { countdown = max(0, countdown - max(0, dt)) }
    public mutating func advance(x: Double, z: Double, dt: Double) {
        guard countdown == 0, !finished, dt > 0, dt.isFinite else { return }
        let phase = DirtCourse.phase(x: x, z: z)
        let delta = atan2(sin(phase - previousPhase), cos(phase - previousPhase))
        previousPhase = phase
        let oldTime = elapsed; elapsed += dt
        wrongWay = delta < -0.0001
        // Reject teleports/off-course samples. Signed progress means reversing
        // over the finish or oscillating across it cannot award extra laps.
        guard DirtCourse.projection(x:x,z:z).distance < DirtCourse.fenceOffset, abs(delta) < 0.3 else { return }
        let oldProgress = progress
        progress += delta
        let finish = Double(laps.count + 1) * 2 * Double.pi
        if oldProgress < finish && progress >= finish - 1e-9 && delta > 0 {
            let fraction = max(0, min(1, (finish - oldProgress) / delta))
            let crossing = oldTime + dt * fraction
            laps.append(crossing - lapStart); lapStart = crossing
            if finished { elapsed = crossing }
        }
    }
}

public struct DirtScore: Codable, Sendable {
    public let date: Date
    public let laps: [Double]
    public var total: Double { laps.reduce(0, +) }
    public init(laps: [Double], date: Date = Date()) { self.laps = laps; self.date = date }
    public var valid: Bool { laps.count == 3 && laps.allSatisfy { $0.isFinite && $0 > 0 } }
}

public enum DirtScores {
    public static func ranked(_ scores: [DirtScore]) -> [DirtScore] {
        Array(scores.filter(\.valid).sorted { $0.total < $1.total }.prefix(10))
    }
    public static func load(_ url: URL) throws -> [DirtScore] {
        guard FileManager.default.fileExists(atPath: url.path) else { return [] }
        return ranked(try JSONDecoder().decode([DirtScore].self, from: Data(contentsOf: url)))
    }
    public static func save(_ scores: [DirtScore], to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONEncoder().encode(ranked(scores)).write(to: url, options: .atomic)
    }
}

/// Finishers sort by their frozen crossing time, remaining racers by progress.
/// Original index breaks ties deterministically without inventing finish times.
public enum DirtStandings {
    public static func order(_ races: [DirtRace]) -> [Int] {
        races.indices.sorted { a,b in
            if races[a].finished != races[b].finished { return races[a].finished }
            if races[a].finished && races[a].elapsed != races[b].elapsed { return races[a].elapsed < races[b].elapsed }
            if !races[a].finished && races[a].progress != races[b].progress { return races[a].progress > races[b].progress }
            return a < b
        }
    }
}
