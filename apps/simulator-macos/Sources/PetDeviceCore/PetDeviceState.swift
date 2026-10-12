import Foundation
import SimulationCore

public struct PetArguments: Codable {
    public var throttle: Double?, turn: Double?, headYaw: Double?, headPitch: Double?, autonomousHead: Bool?, sequence: Int?
    public var yaw: Double?, pitch: Double?, left: Double?, right: Double?, durationMs: Int?
    public var primitive: String?, expression: String?, x: Double?, y: Double?
}
public struct PetCommand: Codable {
    public let type: String, id: String, deviceId: String, bootId: String, action: String
    public let epoch: Int, deadline: Double, args: PetArguments
}
public struct PetCalibration: Codable {
    public var yawCenter = 90, pitchCenter = 90
    public var yawReversed = true, pitchReversed = true
    public init() {}
    public var valid: Bool { (60...120).contains(yawCenter) && (60...120).contains(pitchCenter) }
}
public struct PetPreferences: Codable {
    public var headCalibration = PetCalibration()
    public var eyeDesign = "friendly", showBatteryIcon = false
    public init() {}
}

/// Protocol behavior independent of AppKit and SceneKit. Acknowledgements describe
/// simulated execution, never physical hardware. All calls use one serial owner.
public final class PetDeviceState {
    public static let capabilities = ["remote", "head", "tracks", "motion", "eyes", "gaze"]
    public var onStatus: ((String, String, String?) -> Void)?
    public var preferences = PetPreferences()
    public private(set) var expression = "neutral", gazeX = 0.0, gazeY = 0.0
    public private(set) var yaw = 0.0, pitch = 0.0
    public var pickedUp = false
    public var enabled = true
    private let ledgerURL: URL?
    private struct Receipt: Codable { let status: String, deadline: Double }
    private var ledger: [String: Receipt] = [:]
    private var serverWallMs = 0.0
    private var current: PetCommand?, began = 0.0, until = 0.0
    private var remote: PetArguments?, remoteAt = -Double.infinity
    private var targetYaw = 0.0, targetPitch = 0.0
    public init(ledgerURL: URL? = nil) throws {
        self.ledgerURL = ledgerURL
        if let url=ledgerURL, FileManager.default.fileExists(atPath:url.path) {
            ledger = try JSONDecoder().decode([String:Receipt].self,from:Data(contentsOf:url))
            guard ledger.count<=4096 else { throw CocoaError(.fileReadCorruptFile) }
        }
    }
    private func record(_ id: String, _ state: String, deadline: Double? = nil) throws {
        // An expired deadline cannot be replayed through receive. Retain active
        // work while it runs, then discard expired receipts on the next write.
        var next=ledger.filter { $0.value.deadline>serverWallMs || $0.key==current?.id }
        next[id]=Receipt(status:state,deadline:deadline ?? ledger[id]?.deadline ?? serverWallMs)
        if let url=ledgerURL {
            try FileManager.default.createDirectory(at:url.deletingLastPathComponent(),withIntermediateDirectories:true)
            try JSONEncoder().encode(next).write(to:url,options:.atomic)
            try FileManager.default.setAttributes([.posixPermissions:0o600],ofItemAtPath:url.path)
        }
        ledger=next
    }
    private func status(_ id: String, _ state: String, _ code: String? = nil) { onStatus?(id,state,code) }
    private func bounded(_ value: Double?, _ limit: Double) -> Bool { guard let value else { return false }; return value.isFinite && abs(value)<=limit }
    private func duration(_ value: Int?, _ min: Int, _ max: Int) -> Bool { value.map { (min...max).contains($0) } ?? false }
    public func receive(_ command: PetCommand, deviceId: String, epoch: Int, bootId: String, wallMs: Double, now: Double) {
        guard command.type=="command",command.deviceId==deviceId,command.epoch==epoch,command.bootId==bootId else { return }
        guard command.deadline.isFinite,command.deadline>wallMs,command.deadline<=wallMs+5000 else { status(command.id,"failed","DEADLINE_INVALID");return }
        guard enabled else { status(command.id,"failed","SIMULATOR_PAUSED");return }
        let a=command.args
        if command.action=="remote" {
            guard bounded(a.throttle,1),bounded(a.turn,1),bounded(a.headYaw,1),bounded(a.headPitch,1),a.autonomousHead != nil,(a.sequence ?? -1)>=0 else { status(command.id,"failed","INVALID_REMOTE_INPUT");return }
            guard !pickedUp else { status(command.id,"failed","MOTION_UNSAFE");return }
            stopCurrent("cancelled"); remote=a;remoteAt=now;return
        }
        serverWallMs=wallMs
        if let previous=ledger[command.id] { status(command.id,previous.status=="started" ? "failed":previous.status,previous.status=="started" ? "DUPLICATE_UNCERTAIN":nil);return }
        guard current==nil,ledger.values.filter({ $0.deadline>wallMs }).count<4096 else { status(command.id,"failed","MOTION_BUSY");return }
        var seconds=0.0
        switch command.action {
        case "head":
            guard bounded(a.yaw,40),bounded(a.pitch,30),duration(a.durationMs,100,3000) else { status(command.id,"failed","INVALID_MOTION");return }
            seconds=Double(a.durationMs!)/1000
        case "tracks":
            guard !pickedUp,bounded(a.left,0.3),bounded(a.right,0.3),duration(a.durationMs,50,30000) else { status(command.id,"failed","INVALID_MOTION");return }
            seconds=Double(a.durationMs!)/1000
        case "motion":
            guard !pickedUp,let primitive=a.primitive,["forward_bit","backward_bit","turn_around","turn_right","turn_left","move_around"].contains(primitive) else { status(command.id,"failed","INVALID_MOTION");return }
            seconds=primitive=="move_around" ? 1.3:primitive.hasPrefix("turn") ? 1.2:0.4
        case "eyes":
            guard let value=a.expression,["neutral","listening","thinking","speaking","concerned","sleeping"].contains(value) else { status(command.id,"failed","INVALID_EXPRESSION");return }
        case "gaze":
            guard bounded(a.x,1),bounded(a.y,1),duration(a.durationMs,100,3000) else { status(command.id,"failed","INVALID_GAZE");return }
            seconds=Double(a.durationMs!)/1000
        default: status(command.id,"failed","CAPABILITY_UNAVAILABLE");return
        }
        do { try record(command.id,"started",deadline:command.deadline) } catch { status(command.id,"failed","LEDGER_WRITE_FAILED");return }
        remote=nil;current=command;began=now;until=now+seconds
        if command.action=="head" { targetYaw=a.yaw!;targetPitch=a.pitch! }
        if command.action=="eyes" { expression=a.expression! }
        if command.action=="gaze" { gazeX=a.x!;gazeY=a.y! }
        status(command.id,"accepted")
    }
    private func stopCurrent(_ state: String) {
        guard let command=current else { return }; current=nil
        if command.action=="gaze" { gazeX=0;gazeY=0 }
        do { try record(command.id,state);status(command.id,state) } catch { status(command.id,"failed","LEDGER_WRITE_FAILED") }
    }
    public func cancel(_ id: String) { if current?.id==id { stopCurrent("cancelled");remote=nil } }
    public func stop() { remote=nil;stopCurrent("failed");targetYaw=yaw;targetPitch=pitch }
    public func advance(dt: Double, now: Double) -> DriveInput {
        var input=DriveInput()
        guard enabled,!pickedUp else { stop();return input }
        if let command=current,now>=until { if command.action=="gaze" { gazeX=0;gazeY=0 };stopCurrent("completed") }
        if let a=remote,now-remoteAt<=0.25 {
            func curve(_ v:Double)->Double { abs(v)<0.08 ? 0:v*abs(v) }
            input.throttle=curve(a.throttle!)*0.55
            input.turn=curve(a.turn!)*0.55*0.7/0.65
            let autoYaw=a.autonomousHead==true ? (abs(a.turn!)>0.08 ? a.turn!*35:sin(now*0.45)*10):0
            targetYaw=max(-80,min(80,autoYaw+a.headYaw!*80));targetPitch=a.headPitch!*30
        } else {
            remote=nil
            if let command=current {
                let a=command.args
                if command.action=="tracks" { input.throttle=(a.left!+a.right!)/2;input.turn=(a.left!-a.right!)/(2*0.65) }
                if command.action=="motion" {
                    switch a.primitive! {
                    case "forward_bit": input.throttle=0.22
                    case "backward_bit": input.throttle = -0.22
                    case "turn_around","turn_right": input.turn=0.3
                    case "turn_left": input.turn = -0.3
                    default: input.throttle=0.18;input.turn=now-began<0.4 ? 0:now-began<0.85 ? 0.1:-0.1
                    }
                }
            }
        }
        let rate=min(0.1,max(0,dt))*100
        yaw += max(-rate,min(rate,targetYaw-yaw));pitch += max(-rate,min(rate,targetPitch-pitch))
        return input
    }
}
