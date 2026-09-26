// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MarvinSimulator",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "MarvinSimulator", targets: ["MarvinSimulator"])],
    targets: [
        .target(name: "SimulationCore"),
        .executableTarget(name: "MarvinSimulator", dependencies: ["SimulationCore"]),
        .executableTarget(name: "SimulationChecks", dependencies: ["SimulationCore"], path: "Tests/SimulationCoreTests"),
    ]
)
