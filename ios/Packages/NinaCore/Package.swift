// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "NinaCore",
    platforms: [.iOS(.v17), .macOS(.v14)],
    products: [
        .library(name: "NinaCore", targets: ["NinaCore"])
    ],
    targets: [
        .target(name: "NinaCore"),
        .testTarget(name: "NinaCoreTests", dependencies: ["NinaCore"])
    ]
)
