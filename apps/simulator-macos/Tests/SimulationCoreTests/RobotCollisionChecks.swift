import Foundation
import simd
import SimulationCore

extension SimulationTests {
    func testCollisionRecovery() {
        for phase in [0.0,2.0,5.0] { for direction in [-1.0,1.0] {
            let p = DirtCourse.point(phase), heading = DirtCourse.heading(phase)
            var body = RobotCollisions.Body(position:SIMD3(p.x,0,p.z),heading:heading+direction*2.8,
                angularVelocity:direction*9,profile:RobotCollisions.profiles[0])
            let original = body.position
            var recovery = CollisionRecovery(); body.contacted = true
            for _ in 0..<720 {
                recovery.advance(&body,dt:1.0/240)
                body.contacted = false; body.heading += body.angularVelocity/240
            }
            let error = atan2(sin(body.heading-heading),cos(body.heading-heading))
            require(abs(error) < 0.03); require(abs(body.angularVelocity) < 0.1)
            equal(body.position,original); equal(body.velocity,.zero)
        } }
        var untouched = RobotCollisions.Body(position:.zero,heading:1,angularVelocity:2,profile:RobotCollisions.profiles[0])
        var recovery = CollisionRecovery(); recovery.advance(&untouched,dt:1.0/60)
        equal(untouched.angularVelocity,2)
        equal(DirtStandings.order([DirtRace(startPhase:0),DirtRace(startPhase:1),DirtRace(startPhase:1)]),[1,2,0])
    }
    func testRobotCollisionImpulses() {
        typealias Body = RobotCollisions.Body
        let light = RobotCollisions.Profile(mass:12,halfWidth:0.25,halfDepth:0.25,height:0.5)
        let heavy = RobotCollisions.Profile(mass:60,halfWidth:0.25,halfDepth:0.25,height:0.8)
        func momentum(_ bodies: [Body]) -> SIMD3<Double> { bodies.reduce(.zero) { $0+$1.velocity*$1.profile.mass } }
        func energy(_ bodies: [Body]) -> Double {
            bodies.reduce(0) { total,b in
                let inertia = b.profile.round ? b.profile.mass*b.profile.halfWidth*b.profile.halfWidth/2 : b.profile.mass*(pow(b.profile.halfWidth,2)+pow(b.profile.halfDepth,2))/3
                return total+0.5*b.profile.mass*simd_length_squared(b.velocity)+0.5*inertia*b.angularVelocity*b.angularVelocity
            }
        }
        for profile in [light,heavy] {
            var bodies = [Body(position:SIMD3(0,0,-0.24),velocity:SIMD3(0,0,6),profile:light),Body(position:SIMD3(0,0,0.24),profile:profile)]
            let before = momentum(bodies), initialEnergy = energy(bodies)
            require(RobotCollisions.resolve(&bodies) > 0)
            near(simd_length(momentum(bodies)-before),0,accuracy:1e-9)
            require(energy(bodies) <= initialEnergy+1e-8)
            require(bodies[1].velocity.z > bodies[0].velocity.z)
            near(bodies[0].angularVelocity,0,accuracy:1e-9); near(bodies[1].angularVelocity,0,accuracy:1e-9)
            require((RobotCollisions.contact(bodies[0],bodies[1])?.penetration ?? 0) < 0.0003)
            if profile.mass == heavy.mass { require(bodies[1].velocity.z < 1.2) }
        }
        // Off-center contact transfers angular momentum and dissipates energy.
        var glance = [Body(position:SIMD3(0.28,0,-0.24),velocity:SIMD3(2,0,6),profile:light),Body(position:SIMD3(0,0,0.24),profile:heavy)]
        let before = momentum(glance), initialEnergy = energy(glance)
        RobotCollisions.resolve(&glance)
        near(simd_length(momentum(glance)-before),0,accuracy:1e-8)
        require(energy(glance) <= initialEnergy+1e-8)
        require(abs(glance[0].angularVelocity)+abs(glance[1].angularVelocity) > 0.1)
        // Separated vertical intervals pass overhead; landing transfers vertical momentum.
        var high = [Body(position:SIMD3(0,1,0),velocity:SIMD3(0,0,8),profile:light),Body(position:.zero,profile:heavy)]
        equal(RobotCollisions.resolve(&high),0)
        high[0].position.y = 0.79; high[0].velocity = SIMD3(0,-2,0)
        let verticalBefore = momentum(high)
        require(RobotCollisions.resolve(&high) > 0)
        near(simd_length(momentum(high)-verticalBefore),0,accuracy:1e-8)
        require(high[0].position.y >= 0.795)
        // Rotation changes box corners; rounded BB-8 uses circle/box corner distance.
        let rotated = Body(position:.zero,heading:.pi/4,profile:heavy)
        let bb = Body(position:SIMD3(0.44,0,0),profile:RobotCollisions.profiles[2])
        require(RobotCollisions.contact(rotated,bb) != nil)
        require(RobotCollisions.contact(Body(position:.zero,profile:heavy),Body(position:SIMD3(0.42,0,0.42),profile:RobotCollisions.profiles[2])) == nil)
        // Initially coincident centers remain finite and are separated deterministically.
        var overlap = [Body(position:.zero,profile:light),Body(position:.zero,profile:heavy)]
        RobotCollisions.resolve(&overlap)
        require(overlap.allSatisfy { $0.position.x.isFinite && $0.velocity.z.isFinite })
        require((RobotCollisions.contact(overlap[0],overlap[1])?.penetration ?? 0) < 0.0003)
        // Opposing boosted motion cannot tunnel at the shared physics step.
        var fast = [Body(position:SIMD3(0,0,-1),velocity:SIMD3(0,0,12),profile:light),Body(position:SIMD3(0,0,1),velocity:SIMD3(0,0,-12),profile:heavy)]
        var hits = 0
        for _ in 0..<120 {
            for i in fast.indices { fast[i].position += fast[i].velocity/240 }
            hits += RobotCollisions.resolve(&fast)
        }
        require(hits > 0); require(fast[0].position.z < fast[1].position.z)
    }

    func testCollisionPileupsAndClock() {
        var bodies = RobotCollisions.profiles.enumerated().map { i,profile -> RobotCollisions.Body in
            let p = DirtCourse.point(0,offset:1.0+Double(i)*0.32)
            return RobotCollisions.Body(position:SIMD3(p.x,DirtCourse.height(x:p.x,z:p.z),p.z),heading:DirtCourse.heading(0),profile:profile)
        }
        for _ in 0..<10 { RobotCollisions.resolve(&bodies,terrain:true) }
        for i in bodies.indices { for j in bodies.indices where j > i {
            require((RobotCollisions.contact(bodies[i],bodies[j])?.penetration ?? 0) < 0.002)
        } }
        require(bodies.allSatisfy { DirtCourse.projection(x:$0.position.x,z:$0.position.z).distance < DirtCourse.fenceOffset && simd_length($0.velocity) < 1e-8 })
        func setup() -> (Simulation,DirtRace,[DirtOpponent]) {
            let slot = DirtCourse.playerGrid
            var race = DirtRace(startPhase:slot.phase); race.countDown(dt:3)
            return (Simulation(seed:0,dirtTrack:true,dirtStartOffset:slot.offset,dirtStartPhase:slot.phase),race,
                Array(DirtCourse.startingGrid.dropFirst()).enumerated().map { DirtOpponent(slot:$0.element,laneOffset:[0.65,0,-0.65][$0.offset]) })
        }
        var endings: [[Simulation]] = []
        for fps in [10.0,30.0,60.0,120.0] {
            var (player,race,rivals) = setup(), world = DirtRacePhysics(), input = DriveInput()
            input.throttle = 1; input.boost = true; input.turn = 0.08
            for _ in 0..<Int(fps*4) { world.advance(input,player:&player,race:&race,opponents:&rivals,dt:1/fps,raceDT:1/fps) }
            endings.append([player]+rivals.map { $0.simulation })
            let before = player, clock = race.elapsed
            world.advance(input,player:&player,race:&race,opponents:&rivals,dt:.nan,raceDT:1)
            world.advance(input,player:&player,race:&race,opponents:&rivals,dt:1,raceDT:.infinity)
            equal(player.x,before.x); equal(race.elapsed,clock)
        }
        for states in endings.dropFirst() { for (a,b) in zip(endings[0],states) {
            near(a.x,b.x,accuracy:1e-7); near(a.z,b.z,accuracy:1e-7)
            near(a.heading,b.heading,accuracy:1e-7)
            near(simd_length(a.velocity-b.velocity),0,accuracy:1e-7)
        } }
        var (player,race,rivals) = setup(), world = DirtRacePhysics(), input = DriveInput()
        input.throttle = 1
        for _ in 0..<30 { world.advance(input,player:&player,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60) }
        let moving = hypot(player.velocity.x,player.velocity.z)
        input.brake = true
        world.advance(input,player:&player,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60)
        require(hypot(player.velocity.x,player.velocity.z) > 0.1)
        require(hypot(player.velocity.x,player.velocity.z) < moving)
    }

    func testCoupledRacePhysics() {
        var totals: [Double] = []
        for fps in [30.0,60.0,120.0] {
            let slot = DirtCourse.startingGrid[3]
            var player = Simulation(seed:0,dirtTrack:true,dirtStartOffset:slot.offset,dirtStartPhase:slot.phase)
            var race = DirtRace(startPhase:slot.phase), world = DirtRacePhysics()
            var rivals = DirtCourse.startingGrid.prefix(3).enumerated().map { DirtOpponent(slot:$0.element,laneOffset:[0.65,0,-0.65][$0.offset]) }
            var input = DriveInput(); input.throttle = 1
            world.advance(input,player:&player,race:&race,opponents:&rivals,dt:1/fps,raceDT:1/fps)
            equal(player.distance,0); race.countDown(dt:3)
            for _ in 0..<Int(fps*180) {
                let phase = DirtCourse.phase(x:player.x,z:player.z), target = DirtCourse.point(phase+0.05,offset:0.1)
                let desired = atan2(target.x-player.x,target.z-player.z)
                let error = atan2(sin(desired-player.heading),cos(desired-player.heading))
                input.throttle = max(0.15,1-abs(error)*1.5); input.turn = -error*3; input.boost = abs(error) < 0.08
                world.advance(input,player:&player,race:&race,opponents:&rivals,dt:1/fps,raceDT:1/fps)
                let states = [player]+rivals.map { $0.simulation }
                require(states.allSatisfy { $0.x.isFinite && $0.z.isFinite && $0.velocity.x.isFinite && DirtCourse.projection(x:$0.x,z:$0.z).distance < DirtCourse.fenceOffset })
                if race.finished { break }
            }
            print("Coupled race at \(fps) fps: \(race.elapsed), laps \(race.laps.count), contacts \(world.contactCount)")
            require(race.finished); require(world.contactCount > 0)
            totals.append(race.elapsed)
            if fps == 60 {
                let finishTime = race.elapsed, laps = race.laps, traveled = player.distance
                // Player input is ignored on cooldown; unfinished opponents still finish.
                var hostile = DriveInput(); hostile.brake = true; hostile.turn = 1; hostile.throttle = -1
                for _ in 0..<60*90 {
                    world.advance(hostile,player:&player,race:&race,opponents:&rivals,dt:1/fps,raceDT:1/fps)
                    if rivals.allSatisfy({ $0.race.finished }) && player.distance > traveled+10 { break }
                }
                require(rivals.allSatisfy { $0.race.finished })
                require(player.distance > traveled+10)
                equal(race.elapsed,finishTime); equal(race.laps,laps)
                let distances = rivals.map { $0.simulation.distance }, times = rivals.map { $0.race.elapsed }
                for _ in 0..<60*4 { world.advance(hostile,player:&player,race:&race,opponents:&rivals,dt:1/fps,raceDT:1/fps) }
                for i in rivals.indices { require(rivals[i].simulation.distance > distances[i]+0.1); equal(rivals[i].race.elapsed,times[i]) }
                let ranks = DirtStandings.order([race]+rivals.map { $0.race })
                let results = [race]+rivals.map { $0.race }
                require(zip(ranks,ranks.dropFirst()).allSatisfy { results[$0].elapsed <= results[$1].elapsed })
            }
            let x = player.x, time = player.elapsed
            player.paused = true
            world.advance(input,player:&player,race:&race,opponents:&rivals,dt:0.1,raceDT:0.1)
            equal(player.x,x); equal(player.elapsed,time)
            player.reset(); world = DirtRacePhysics()
            equal(player.velocity,.zero); equal(player.angularVelocity,0); equal(world.contactCount,0)
        }
        // Player steering is sampled per displayed frame, so tolerate a small
        // difference in this interactive-controller test. Fixed-input determinism
        // is covered separately by the integration checks.
        require(totals.max()!-totals.min()! < 12)
    }
}
