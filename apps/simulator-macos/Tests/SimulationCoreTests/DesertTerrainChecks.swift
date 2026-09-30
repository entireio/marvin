import Foundation
import SimulationCore
import simd

extension SimulationTests {
    func testDesertTerrain() {
        // Keep every existing city foundation and both track entrances level.
        for z in stride(from:-154.0,through:154,by:7) { for x in stride(from:-154.0,through:154,by:7) {
            near(DesertTerrain.height(x:x,z:z),-0.025,accuracy:1e-12)
        }}
        var maxSlope=0.0,maxHeight=0.0
        for z in stride(from:-700.0,through:700,by:7.3) { for x in stride(from:-700.0,through:700,by:7.7) {
            let h=DesertTerrain.height(x:x,z:z),g=DesertTerrain.gradient(x:x,z:z)
            require(h.isFinite && h >= -0.0250001)
            maxSlope=max(maxSlope,simd_length(g));maxHeight=max(maxHeight,h)
            // Independent barycentric construction at the exact mesh vertices.
            let x0=floor(x/2)*2,z0=floor(z/2)*2,u=(x-x0)/2,v=(z-z0)/2
            let a=DesertTerrain.vertexHeight(x:x0,z:z0),b=DesertTerrain.vertexHeight(x:x0+2,z:z0)
            let c=DesertTerrain.vertexHeight(x:x0,z:z0+2),d=DesertTerrain.vertexHeight(x:x0+2,z:z0+2)
            let rendered=u+v<=1 ? (1-u-v)*a+u*b+v*c:(u+v-1)*d+(1-u)*c+(1-v)*b
            near(h,rendered,accuracy:1e-10)
        }}
        require(maxHeight>8 && maxSlope<0.67)
        // No vertical seam at the town apron, tile boundaries or field perimeter.
        for x in [156.0,160,192,256,320,640,768] { for z in [0.0,51,181,321] {
            near(DesertTerrain.height(x:x-1e-5,z:z),DesertTerrain.height(x:x+1e-5,z:z),accuracy:0.00002)
        }}
        // Drive every playable chassis across a rising dune with real coupled
        // dynamics; verify ground contact, slope response and successful travel.
        for character in RacePerformance.Character.allCases {
            let desired=SIMD2<Double>(171,57),p=DirtCourse.projection(x:desired.x,z:desired.y)
            var player=Simulation(dirtTrack:true,dirtStartOffset:p.distance,dirtStartPhase:p.phase,character:character)
            let others=RacePerformance.Character.allCases.filter{$0 != character}
            var physics=DirtRacePhysics(characters:[character]+others)
            var rivals=[DirtOpponent(),DirtOpponent(laneOffset:0),DirtOpponent(laneOffset:-0.65)]
            var race=DirtRace();race.countDown(dt:3)
            let target=SIMD2<Double>(250,74)
            var highest=player.groundY,pitch=0.0,arrived=false
            for _ in 0..<6000 {
                let d=target-SIMD2(player.x,player.z)
                if simd_length(d)<0.5 { arrived=true;break }
                let error=atan2(sin(atan2(d.x,d.y)-player.heading),cos(atan2(d.x,d.y)-player.heading))
                var input=DriveInput();input.turn=max(-1,min(1,-error*2.5));input.throttle=abs(error)<0.3 ? 0.8:0
                physics.advance(input,player:&player,race:&race,opponents:&rivals,dt:1.0/60,raceDT:1.0/60)
                require(player.groundY>=DirtCourse.height(x:player.x,z:player.z)-0.005)
                require(player.x.isFinite && player.bodyPitch.isFinite && player.bodyRoll.isFinite)
                highest=max(highest,player.groundY);pitch=max(pitch,abs(player.bodyPitch))
            }
            require(arrived && highest>2 && pitch>0.03)
            require(player.groundY<highest-0.25)
        }
        print("Dunes: PASS, peak \(maxHeight)m, maximum sampled slope \(atan(maxSlope)*180 / .pi) degrees")
    }
}
