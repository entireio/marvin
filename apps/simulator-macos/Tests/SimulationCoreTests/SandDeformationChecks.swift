import Foundation
import SimulationCore
import simd

extension SimulationTests {
    func testSandDeformation() {
        let field=SandDeformation()
        let p=DirtCourse.projection(x:211,z:71)
        var state=Simulation(dirtTrack:true,dirtStartOffset:p.offset,dirtStartPhase:p.phase,character:.wallE)
        var physics=DirtRacePhysics(characters:[.wallE,.marvin,.r2d2,.bb8]);physics.sand=field
        var race=DirtRace();race.countDown(dt:3)
        var rivals=(1...3).map{DirtOpponent(slot:DirtCourse.startingGrid[$0])}
        let contacts=[SandDeformation.Contact(x:0.30,z:0,width:0.196,length:0.44),.init(x: -0.30,z:0,width:0.196,length:0.44)]
        field.contactLayouts[3]=contacts
        var lowest=0.0,highest=0.0
        for frame in 0..<900 {
            var input=DriveInput()
            if frame<180 {
                let error=atan2(sin(0.72-state.heading),cos(0.72-state.heading))
                input.turn=max(-1,min(1,-error*2.5))
            } else if frame<660 { input.throttle=0.6 }
            else { input.brake=true }
            physics.advance(input,player:&state,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60,robotCollisionsEnabled:false)
            if frame%2==0 {
                field.begin(dt:1.0/30,positions:[SIMD2(state.x,state.z)])
                field.stamp(state,contacts:contacts,dt:1.0/30);field.settle(dt:1.0/30)
            }
            require(state.groundY.isFinite && state.bodyRoll.isFinite)
            require(state.groundY>=state.supportHeight-0.015)
        }
        for tile in field.tiles.values { for value in tile.delta {
            require(value.isFinite && value > -0.14 && value<0.11)
            lowest=min(lowest,Double(value));highest=max(highest,Double(value))
        }}
        require(lowest < -0.01 && highest>0.005 && field.displacedVolume>0)
        // The CPU sampler uses exactly the rendered triangle diagonal. Shared
        // tile edges must be continuous even when a footprint crosses them.
        for key in field.tiles.keys { for j in stride(from:0,to:64,by:7) {
            let ix=key.x*64+63,iz=key.z*64+j,u=0.37,v=0.76
            let a=Double(field.vertex(ix,iz)),b=Double(field.vertex(ix+1,iz)),c=Double(field.vertex(ix,iz+1)),d=Double(field.vertex(ix+1,iz+1))
            near(field.offset(x:(Double(ix)+u)*SandDeformation.step,z:(Double(iz)+v)*SandDeformation.step),d+(c-d)*(1-u)+(b-d)*(1-v),accuracy:1e-7)
            let x=Double(key.x+1)*4,z=Double(iz)*SandDeformation.step
            near(field.offset(x:x-1e-7,z:z),field.offset(x:x+1e-7,z:z),accuracy:0.000001)
            _=a
        }}
        // Snapshot uploaded to Metal matches the collision sampler, including
        // neighboring tiles used for boundary normals.
        for key in field.tiles.keys {
            let grid=field.grid(key)
            for j in stride(from:-1,through:65,by:3) { for i in stride(from:-1,through:65,by:3) {
                near(Double(grid[(j+1)*67+i+1]),Double(field.vertex(key.x*64+i,key.z*64+j)),accuracy:0)
            }}
        }
        let orientation=state.duneOrientation
        let up=orientation.act(SIMD3(0,1,0)),forward=orientation.act(SIMD3(0,0,1)),right=orientation.act(SIMD3(1,0,0))
        near(simd_length(up),1,accuracy:1e-9)
        near(simd_dot(up,forward),0,accuracy:1e-9)
        near(simd_dot(up,right),0,accuracy:1e-9)
        // Drive the cache beyond capacity while protecting the original robot.
        // Eviction must never remove the terrain supporting a current racer.
        let protectedPoint=SIMD2(state.x,state.z),protectedKey=SandDeformation.key(x:state.x,z:state.z)
        require(field.tiles[protectedKey] != nil)
        for i in 0..<180 {
            let p=DirtCourse.projection(x:(i%2==0 ? 1.0:-1.0)*(220+Double(i%40)*10),z:80+Double(i/40)*12)
            var visitor=Simulation(dirtTrack:true,dirtStartOffset:p.offset,dirtStartPhase:p.phase,character:.wallE)
            visitor.sand=field
            field.begin(dt:1.0/30,positions:[protectedPoint,SIMD2(visitor.x,visitor.z)])
            field.stamp(visitor,contacts:contacts,dt:1.0/30)
            require(field.tiles.count<=SandDeformation.capacity && field.tiles[protectedKey] != nil)
        }
        print("Sand cache: \(field.tiles.count) tiles, \(field.topologyVersion) allocations")
        require(field.tiles.count==SandDeformation.capacity && field.topologyVersion>SandDeformation.capacity*2)
        // No modifications to race/town soil; resetting removes the shared data.
        near(field.offset(x:0,z:0),0,accuracy:0)
        require(field.tiles.count<=SandDeformation.capacity)
        field.reset();require(field.tiles.isEmpty);near(field.offset(x:state.x,z:state.z),0,accuracy:0)
        print("Dune deformation: PASS, cut \(lowest)m, bank \(highest)m; contact, GPU snapshots, seams, cache eviction and reset")
    }
}
