import Foundation
import Metal
import SceneKit
import simd

/// Opt-in benchmark capture. Runs with Metal capture enabled are diagnostic only.
final class BenchmarkGPUCapture {
    private let requests: [Double] = CommandLine.arguments.contains("--benchmark-gpu-capture")
        ? (ProcessInfo.processInfo.environment["MARVIN_GPU_CAPTURE_AT"] ?? "30")
            .split(separator: ",").compactMap { Double($0) }.filter { $0.isFinite && $0 >= 0 } : []
    private let match = ProcessInfo.processInfo.environment["MARVIN_GPU_CAPTURE_MATCH"] == "1"
    private let lock = NSLock()
    private var start = 0.0, waypoint = 0, index = 0
    private var runID = "", directory: URL?
    private weak var player: SCNNode?
    private var viewport = CGSize.zero
    private var active = false
    private var frames: [[String: Any]] = []
    private var target: Pose?
    private var startedUptime = 0.0
    private struct Pose {
        var player: simd_float4x4
        var camera: simd_float4x4
        var projection: [Double]
        var waypoint: Int
    }

    func configure(start: Double, runID: String, player: SCNNode, viewport: CGSize, directory: URL) {
        guard !requests.isEmpty else { return }
        lock.lock(); defer { lock.unlock() }
        self.start = start; self.runID = runID; self.player = player
        self.viewport = viewport; self.directory = directory
    }
    func update(waypoint: Int) {
        guard !requests.isEmpty else { return }
        lock.lock(); self.waypoint = waypoint; lock.unlock()
    }
    private func matrix(_ m: simd_float4x4) -> [Double] {
        [m.columns.0, m.columns.1, m.columns.2, m.columns.3].flatMap { c in
            [Double(c.x), Double(c.y), Double(c.z), Double(c.w)]
        }
    }
    private func distance(_ a: simd_float4x4, _ b: simd_float4x4) -> Float {
        simd_distance(SIMD3(a.columns.3.x,a.columns.3.y,a.columns.3.z),
                      SIMD3(b.columns.3.x,b.columns.3.y,b.columns.3.z))
    }
    private func angle(_ a: simd_float4x4, _ b: simd_float4x4) -> Float {
        let dot = abs(simd_dot(simd_quatf(a).vector, simd_quatf(b).vector))
        return 2 * acos(min(1, dot)) * 180 / .pi
    }
    // Stricter capture trigger leaves margin for the next complete captured frame.
    private func matches(_ a: Pose, _ b: Pose) -> Bool {
        a.waypoint == b.waypoint && distance(a.player,b.player) <= 0.07 &&
        distance(a.camera,b.camera) <= 0.07 && angle(a.player,b.player) <= 0.6 &&
        angle(a.camera,b.camera) <= 0.6 &&
        zip(a.projection,b.projection).allSatisfy { abs($0-$1) <= 1e-6 }
    }
    func beginFrame(_ renderer: SCNSceneRenderer, time: TimeInterval) {
        guard !requests.isEmpty else { return }
        lock.lock(); defer { lock.unlock() }
        let now = ProcessInfo.processInfo.systemUptime
        guard index < requests.count, now-start >= requests[index],
              let directory, let player, let cameraNode = renderer.pointOfView?.presentation,
              let camera = cameraNode.camera else { return }
        let projection = matrix(simd_float4x4(camera.projectionTransform(withViewportSize: viewport)))
        let pose = Pose(player:player.presentation.simdWorldTransform,
                        camera:cameraNode.simdWorldTransform,projection:projection,waypoint:waypoint)
        if !active {
            if match, let target, !matches(pose,target) { return }
            let descriptor = MTLCaptureDescriptor()
            // Capture every queue on the renderer's actual device, including auxiliaries.
            descriptor.captureObject = renderer.device
            descriptor.destination = .gpuTraceDocument
            descriptor.outputURL = directory.appendingPathComponent(requests.count == 1
                ? "native-frame.gputrace" : "native-frame-\(index).gputrace")
            do {
                guard renderer.device != nil else { throw NSError(domain:"BenchmarkGPUCapture",code:1) }
                try MTLCaptureManager.shared().startCapture(with:descriptor)
                active = true; frames = []; startedUptime = now
            } catch {
                NSLog("Native GPU capture failed: %@", String(describing:error)); index += 1; return
            }
        }
    }
    func willRender(_ renderer: SCNSceneRenderer, time: TimeInterval) {
        guard !requests.isEmpty else { return }
        lock.lock(); defer { lock.unlock() }
        guard active, let player, let cameraNode = renderer.pointOfView?.presentation,
              let camera = cameraNode.camera else { return }
        let now = ProcessInfo.processInfo.systemUptime
        let pose = Pose(player:player.presentation.simdWorldTransform,
            camera:cameraNode.simdWorldTransform,
            projection:matrix(simd_float4x4(camera.projectionTransform(withViewportSize:viewport))),
            waypoint:waypoint)
        frames.append(["uptime":now,"elapsed":now-start,"sceneTime":time,
                       "waypoint":waypoint,"playerWorldTransform":matrix(pose.player),
                       "cameraWorldTransform":matrix(pose.camera),"projection":pose.projection,
                       "viewportPoints":[viewport.width,viewport.height],
                       "thermalState":ProcessInfo.processInfo.thermalState.rawValue])
        // BeginFrame starts during updateAtTime, before scene rendering. Frame 1
        // is the comparison target; Xcode must confirm which complete frames exist.
        if index == 0 && frames.count == 2 { target = pose }
    }
    func didRender() {
        guard !requests.isEmpty else { return }
        lock.lock(); defer { lock.unlock() }
        guard active, frames.count >= 3, let directory else { return }
        MTLCaptureManager.shared().stopCapture(); active = false
        let report: [String:Any] = ["runID":runID,"pid":ProcessInfo.processInfo.processIdentifier,
            "captureIndex":index,"requestedNotBeforeElapsed":requests[index],
            "captureStartUptime":startedUptime,"captureStopUptime":ProcessInfo.processInfo.systemUptime,
            "frames":frames,"comparisonFrameIndex":1,"matchingEnabled":match,
            "tolerances":["positionMeters":0.10,"orientationDegrees":1.0,"projectionCoefficient":1e-6],
            "limitation":"First recorded frame may be partial; confirm complete frame identity in Xcode."]
        do {
            try JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys])
                .write(to:directory.appendingPathComponent("capture-\(index).json"))
        } catch { NSLog("Capture metadata write failed: %@", String(describing:error)) }
        NSLog("Native GPU capture %d stopped; verify replay and actual frame matching",index)
        index += 1
    }
}
