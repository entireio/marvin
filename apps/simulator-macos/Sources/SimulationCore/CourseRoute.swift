import Foundation

/// Shortest visibility-graph route around conservative robot-clearance polygons.
/// Circumscribed corner arcs leave room for Marvin's circular collision footprint.
public enum CourseRoute {
    private static var barriers: [[Checkpoint]] {
        let r = Simulation.radius / cos(.pi/8) + 0.002
        return Simulation.obstacles.map { box in
            let corners = [(1.0,1.0,0.0), (-1.0,1.0,90.0), (-1.0,-1.0,180.0), (1.0,-1.0,270.0)]
            return corners.flatMap { sx, sz, degrees in
                (0...2).map { step in
                    let a = (degrees+Double(step)*45) * .pi/180
                    return Checkpoint(x: box.x+sx*box.width/2+r*cos(a), z: box.z+sz*box.depth/2+r*sin(a))
                }
            }
        }
    }
    private static func clear(_ a: Checkpoint, _ b: Checkpoint, barriers: [[Checkpoint]]) -> Bool {
        // Clip the segment against the interior half-planes of each convex polygon.
        for polygon in barriers {
            var lower = 0.0, upper = 1.0
            for i in polygon.indices {
                let p = polygon[i], q = polygon[(i+1)%polygon.count]
                let ex = q.x-p.x, ez = q.z-p.z
                let start = ex*(a.z-p.z)-ez*(a.x-p.x)-1e-9
                let delta = ex*(b.z-a.z)-ez*(b.x-a.x)
                if abs(delta) < 1e-12 {
                    if start <= 0 { upper = -1; break }
                } else if delta > 0 { lower = max(lower, -start/delta) }
                else { upper = min(upper, -start/delta) }
            }
            if lower < upper { return false }
        }
        return true
    }
    public static func path(from start: Checkpoint, to goal: Checkpoint) -> [Checkpoint] {
        let polygons = barriers
        let corners = polygons.flatMap { $0 }.filter {
            abs($0.x) < Simulation.halfWidth-Simulation.radius && abs($0.z) < Simulation.halfDepth-Simulation.radius
        }
        let points = [start, goal]+corners
        var distance = Array(repeating: Double.infinity, count: points.count)
        var previous = Array(repeating: -1, count: points.count)
        var visited = Set<Int>()
        distance[0] = 0
        while let u = points.indices.filter({ !visited.contains($0) }).min(by: { distance[$0] < distance[$1] }) {
            if !distance[u].isFinite { break }
            if u == 1 {
                var route = [goal], index = 1
                while previous[index] >= 0 { index = previous[index]; route.append(points[index]) }
                return route.reversed()
            }
            visited.insert(u)
            for v in points.indices where !visited.contains(v) && clear(points[u], points[v], barriers: polygons) {
                let next = distance[u]+hypot(points[v].x-points[u].x, points[v].z-points[u].z)
                if next < distance[v] { distance[v] = next; previous[v] = u }
            }
        }
        // The fixed arena and generated checkpoints always have a route.
        preconditionFailure("Checkpoint is unreachable")
    }
    public static func labelYaw(from start: Checkpoint, to goal: Checkpoint) -> Double {
        let route = path(from: start, to: goal)
        let approach = route[route.count-2]
        return atan2(approach.x-goal.x, approach.z-goal.z)
    }
}
