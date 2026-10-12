import Foundation
import PetDeviceCore
import SimulationCore

func checkEqual<T:Equatable>(_ actual:T,_ expected:T,file:StaticString=#file,line:UInt=#line) { precondition(actual==expected,"Expected \(expected), got \(actual)",file:file,line:line) }
func checkGreater(_ actual:Double,_ expected:Double,file:StaticString=#file,line:UInt=#line) { precondition(actual>expected,"Expected forward movement",file:file,line:line) }

final class PetDeviceChecks {
    private func command(_ action:String="remote",id:String="command",epoch:Int=1,boot:String="boot",deadline:Double=1100,args:[String:Any]=["throttle":1,"turn":0,"headYaw":0,"headPitch":0,"autonomousHead":true,"sequence":1]) throws -> PetCommand {
        try JSONDecoder().decode(PetCommand.self,from:JSONSerialization.data(withJSONObject:["type":"command","id":id,"deviceId":"device","epoch":epoch,"bootId":boot,"deadline":deadline,"action":action,"args":args]))
    }
    private func receive(_ state:PetDeviceState,_ command:PetCommand,now:Double=0) { state.receive(command,deviceId:"device",epoch:1,bootId:"boot",wallMs:1000,now:now) }
    func testRemoteMovesAndStopsWhenSamplesExpire() throws {
        let state=try PetDeviceState();var simulation=Simulation()
        receive(state,try command())
        let initial=simulation.z
        for frame in 0..<12 { simulation.advance(state.advance(dt:1.0/60,now:Double(frame)/60),dt:1.0/60) }
        checkGreater(simulation.z,initial)
        checkEqual(state.advance(dt:0.02,now:0.3).throttle,0)
        state.stop();checkEqual(state.advance(dt:0.02,now:0.31).throttle,0)
    }
    func testWrongEpochBootAndExpiredCommandsNeverMove() throws {
        for cmd in [try command(epoch:2),try command(boot:"other"),try command(deadline:999)] {
            let state=try PetDeviceState();receive(state,cmd);checkEqual(state.advance(dt:0.02,now:0.02).throttle,0)
        }
    }
    func testWriteAheadLedgerPreventsRepeatingUncertainMotion() throws {
        let folder=FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString),url=folder.appendingPathComponent("ledger.json")
        defer { try? FileManager.default.removeItem(at:folder) }
        let first=try PetDeviceState(ledgerURL:url),cmd=try command("tracks",args:["left":0.2,"right":0.2,"durationMs":400])
        receive(first,cmd);checkGreater(first.advance(dt:0.02,now:0.02).throttle,0)
        let restarted=try PetDeviceState(ledgerURL:url);var statuses:[String]=[];restarted.onStatus={ _,state,_ in statuses.append(state) }
        receive(restarted,cmd);checkEqual(statuses,["failed"]);checkEqual(restarted.advance(dt:0.02,now:0.02).throttle,0)
    }
    func testExpiredReceiptsDoNotExhaustTheLedger() throws {
        let state=try PetDeviceState();var accepted=0
        state.onStatus={ _,status,_ in if status=="accepted" { accepted+=1 } }
        for index in 0..<4100 {
            let time=1000+Double(index)*200
            let cmd=try command("eyes",id:"eye-\(index)",deadline:time+100,args:["expression":"neutral"])
            state.receive(cmd,deviceId:"device",epoch:1,bootId:"boot",wallMs:time,now:Double(index))
            _=state.advance(dt:0.02,now:Double(index)+0.01)
        }
        checkEqual(accepted,4100)
    }
    func testCancellationAndRemoteTakeoverEndNamedMotion() throws {
        let state=try PetDeviceState();var statuses:[String]=[];state.onStatus={ _,value,_ in statuses.append(value) }
        receive(state,try command("motion",args:["primitive":"forward_bit"]));state.cancel("command")
        checkEqual(statuses,["accepted","cancelled"]);checkEqual(state.advance(dt:0.02,now:0.02).throttle,0)
        receive(state,try command("motion",id:"second",args:["primitive":"forward_bit"]));receive(state,try command(id:"remote"))
        checkEqual(statuses.suffix(2),["accepted","cancelled"])
    }
    func testPauseAndPickupPreventMovementAndCompletionIsTimed() throws {
        let state=try PetDeviceState();state.enabled=false;receive(state,try command());checkEqual(state.advance(dt:0.02,now:0).throttle,0)
        state.enabled=true;state.pickedUp=true;receive(state,try command());checkEqual(state.advance(dt:0.02,now:0).throttle,0)
        state.pickedUp=false;var statuses:[String]=[];state.onStatus={ _,value,_ in statuses.append(value) }
        receive(state,try command("head",args:["yaw":40,"pitch":20,"durationMs":600]))
        for frame in 0..<30 { _=state.advance(dt:0.02,now:Double(frame)*0.02) }
        checkEqual(state.yaw,40);checkEqual(statuses,["accepted"])
        _=state.advance(dt:0.02,now:0.61);checkEqual(statuses,["accepted","completed"])
    }
}

let checks=PetDeviceChecks()
try checks.testRemoteMovesAndStopsWhenSamplesExpire()
try checks.testWrongEpochBootAndExpiredCommandsNeverMove()
try checks.testWriteAheadLedgerPreventsRepeatingUncertainMotion()
try checks.testCancellationAndRemoteTakeoverEndNamedMotion()
try checks.testPauseAndPickupPreventMovementAndCompletionIsTimed()
try checks.testExpiredReceiptsDoNotExhaustTheLedger()
print("PASS: 6 device command checks")
