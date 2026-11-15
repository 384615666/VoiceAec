// swift-tools-version:5.9
import PackageDescription
import Foundation

let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().path
let linkFlags: [String] = [
    "-L", "\(root)/Vendor", "-llocalvqe",
    "-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks",
    "-Xlinker", "-rpath", "-Xlinker", "\(root)/Vendor",
]
let frameworks: [String] = [
    "AppKit", "AVFoundation", "AudioToolbox", "CoreAudio", "CoreMedia",
    "ScreenCaptureKit", "WebKit", "ServiceManagement",
]

let package = Package(
    name: "VoiceAec",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "VoiceAec", targets: ["VoiceAec"]),
    ],
    targets: [
        .target(name: "CLocalVQE", path: "Sources/CLocalVQE"),
        .target(
            name: "VoiceAecCore",
            dependencies: ["CLocalVQE"],
            path: "Sources/VoiceAecCore",
            linkerSettings: frameworks.map { .linkedFramework($0) } + [.unsafeFlags(linkFlags)]
        ),
        .executableTarget(
            name: "VoiceAec",
            dependencies: ["VoiceAecCore"],
            path: "Sources/VoiceAec"
        ),
        .executableTarget(
            name: "VoiceAecSelfTest",
            dependencies: ["VoiceAecCore"],
            path: "Sources/VoiceAecSelfTest"
        ),
        .testTarget(
            name: "VoiceAecCoreTests",
            dependencies: ["VoiceAecCore"],
            path: "Tests/VoiceAecCoreTests"
        ),
    ]
)
