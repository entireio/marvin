import Foundation

/// Closed motocross spline with a start straight, mixed turns and jump sections.
public enum DirtCourse {
    public static let width = 1.3
    public static let fenceOffset = 1.8
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
    public static func heading(_ phase: Double) -> Double {
        let a = center(phase-0.0001), b = center(phase+0.0001)
        return atan2(b.x-a.x,b.y-a.y)
    }
    private static let samples = (0...sampleCount).map { center(Double($0)*2 * .pi/Double(sampleCount)) }
    public static func projection(x: Double, z: Double) -> (phase: Double, offset: Double, distance: Double) {
        let p = SIMD2<Double>(x,z)
        var best = Double.infinity, bestPhase = 0.0, offset = 0.0
        for i in 0..<sampleCount {
            let a = samples[i], d = samples[i+1]-a, v = p-a
            let lengthSquared = d.x*d.x+d.y*d.y
            let t = max(0,min(1,(v.x*d.x+v.y*d.y)/lengthSquared))
            let e = p-(a+d*t), distance = e.x*e.x+e.y*e.y
            if distance < best {
                best = distance; bestPhase = (Double(i)+t)*2 * .pi/Double(sampleCount)
                offset = (e.x*d.y-e.y*d.x)/sqrt(lengthSquared)
            }
        }
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
        let table = max(0,min(1,min((u-0.025)/0.015,(0.085-u)/0.015)))*0.48
        let rollers = bump(0.26,0.016,0.16)+bump(0.30,0.016,0.20)+bump(0.34,0.016,0.16)
        let rhythm = bump(0.49,0.020,0.42)+bump(0.54,0.020,0.38)
        let hill = bump(0.69,0.075,0.75)
        let whoops = (0..<6).reduce(0.0) { $0+bump(0.82+Double($1)*0.015,0.0075,0.10) }
        let h0 = heading(phase-0.01), h1 = heading(phase+0.01)
        let turn = atan2(sin(h1-h0),cos(h1-h0))
        let bank = min(0.35,abs(turn)*3)*pow(max(0,offset*(turn > 0 ? -1 : 1)/width),2)
        return table+rollers+rhythm+hill+whoops+bank
    }
    public static func surfaceHeight(_ phase: Double, offset: Double) -> Double {
        let distance = abs(offset)
        let base = elevation(phase,offset:max(-width,min(width,offset)))
        if distance <= width { return base }
        if distance <= 1.65 {
            let crest = offset > 0 ? 0.10 : 0.055
            return base + sin((distance-width)/0.35 * .pi)*crest
        }
        let blend = max(0,min(1,(1.95-distance)/0.30))
        return (base+0.005)*blend - 0.025*(1-blend)
    }
    /// Project only against the fence. The robot can slide along it and reverse
    /// away; the compacted lane edge is a traction change, not a collision wall.
    public static func resolveMove(x:Double,z:Double,heading:Double) -> (x:Double,z:Double,contact:Bool) {
        let p = projection(x:x,z:z), tangent = self.heading(p.phase)
        let relative = heading-tangent
        let support = 0.35*abs(cos(relative)) + 0.33*abs(sin(relative))
        let limit = fenceOffset-support-0.025
        guard p.distance > limit else { return (x,z,false) }
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
    public init() {}
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
