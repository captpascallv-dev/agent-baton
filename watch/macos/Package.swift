// swift-tools-version: 5.9
import PackageDescription
let package = Package(name: "CliWatchMac", platforms: [.macOS(.v13)], products: [.executable(name: "CliWatchMac", targets: ["CliWatchMac"])], targets: [.executableTarget(name: "CliWatchMac")])
