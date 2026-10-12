import Foundation
import SimulationCore

extension SimulationTests {
    func testExactCourseProjection() {
        let count=DirtCourse.sampleCount
        let points=(0...count).map { i -> SIMD2<Double> in
            let p=DirtCourse.point(Double(i)*2 * .pi/Double(count))
            return SIMD2(p.x,p.z)
        }
        // Independent exhaustive oracle: same contract as the pre-optimization
        // implementation, including the first-segment tie rule at shared vertices.
        func exhaustive(_ p:SIMD2<Double>)->(Double,Double,Double) {
            var best=Double.infinity,phase=0.0,offset=0.0
            for i in 0..<count {
                let a=points[i],d=points[i+1]-a,v=p-a
                let length=d.x*d.x+d.y*d.y
                let t=max(0,min(1,(v.x*d.x+v.y*d.y)/length))
                let e=p-(a+d*t),distance=e.x*e.x+e.y*e.y
                if distance<best {
                    best=distance;phase=(Double(i)+t)*2 * .pi/Double(count)
                    offset=(e.x*d.y-e.y*d.x)/sqrt(length)
                }
            }
            return (phase,offset,sqrt(best))
        }
        func check(_ p:SIMD2<Double>) {
            let expected=exhaustive(p),actual=DirtCourse.projection(x:p.x,z:p.y)
            near(actual.phase,expected.0,accuracy:1e-12)
            near(actual.offset,expected.1,accuracy:1e-10)
            near(actual.distance,expected.2,accuracy:1e-10)
        }
        for p in points { check(p) }
        for i in 0..<count {
            let phase=(Double(i)+0.5)*2 * .pi/Double(count)
            for offset in [-DirtCourse.terrainEdge,-DirtCourse.width,0,DirtCourse.width,DirtCourse.terrainEdge] {
                let p=DirtCourse.point(phase,offset:offset);check(SIMD2(p.x,p.z))
            }
        }
        // Includes interiors, tight competing arcs, shoulders and distant city lots.
        for z in -70...70 { for x in -70...70 { check(SIMD2(Double(x)*0.47,Double(z)*0.47)) } }
        for z in [-150.0,0,150] { for x in [-150.0,0,150] { check(SIMD2(x,z)) } }
        // Overflow-sized positions keep the old no-finite-candidate result.
        for x in [-1e200,1e200] { for z in [-1e200,1e200] {
            let result=DirtCourse.projection(x:x,z:z)
            equal(result.phase,0);equal(result.offset,0);require(result.distance.isInfinite)
        }}
        print("PASS: exact spatial projection agrees with exhaustive scan at 24,499 queries plus overflow fallback")
    }
}
