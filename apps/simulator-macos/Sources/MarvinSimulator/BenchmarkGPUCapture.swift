import Foundation
import Metal

/// Optional, bounded native-frame capture for Xcode's Metal debugger.
/// Captured runs are diagnostic only and must never pass the cadence gate.
final class BenchmarkGPUCapture {
    private let requested: Double? = CommandLine.arguments.contains("--benchmark-gpu-capture")
        ? (ProcessInfo.processInfo.environment["MARVIN_GPU_CAPTURE_AT"].flatMap(Double.init) ?? 30) : nil
    private var started = false
    private var finished = false
    private var stopAt = 0.0

    func update(elapsed: Double, device: MTLDevice?, directory: URL) {
        guard let requested, requested.isFinite, requested >= 0, !finished else { return }
        let manager = MTLCaptureManager.shared()
        if !started && elapsed >= requested {
            started = true
            let descriptor = MTLCaptureDescriptor()
            descriptor.captureObject = device
            descriptor.destination = .gpuTraceDocument
            descriptor.outputURL = directory.appendingPathComponent("native-frame.gputrace")
            do {
                guard device != nil, manager.supportsDestination(.gpuTraceDocument) else {
                    throw NSError(domain: "BenchmarkGPUCapture", code: 1,
                        userInfo: [NSLocalizedDescriptionKey: "GPU capture unavailable; launch with MTL_CAPTURE_ENABLED=1"])
                }
                try manager.startCapture(with: descriptor)
                stopAt = elapsed + 0.05
            } catch {
                finished = true
                NSLog("Native GPU capture failed: %@", String(describing: error))
            }
        } else if started && elapsed >= stopAt {
            manager.stopCapture()
            finished = true
            NSLog("Native GPU capture stopped; inspect native-frame.gputrace in Xcode")
        }
    }
}
