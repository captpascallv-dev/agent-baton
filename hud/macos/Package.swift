// swift-tools-version: 5.9
import PackageDescription
let package = Package(name: "CodexUsageHUD", platforms: [.macOS(.v13)], products: [.executable(name: "CodexUsageHUD", targets: ["CodexUsageHUD"])], targets: [.executableTarget(name: "CodexUsageHUD")])
