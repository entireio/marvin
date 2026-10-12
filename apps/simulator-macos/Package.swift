// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "MarvinSimulator",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "MarvinSimulator", targets: ["MarvinSimulator"])],
    targets: [
        .target(name: "SimulationCore"),
        .target(name: "PetDeviceCore", dependencies: ["SimulationCore"]),
        .executableTarget(name: "MarvinSimulator", dependencies: ["SimulationCore", "PetDeviceCore"]),
        .executableTarget(name: "PetDeviceChecks", dependencies: ["PetDeviceCore", "SimulationCore"], path: "Tests/PetDeviceChecks"),
        .executableTarget(name: "SimulationChecks", dependencies: ["SimulationCore"], path: "Tests/SimulationCoreTests"),
    ]
)
