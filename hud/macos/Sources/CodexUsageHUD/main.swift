import SwiftUI
import AppKit
import Combine

struct Snapshot: Decodable {
    let schema: Int
    var hud_version: String? = nil
    let written_at_utc: String
    let slots: [Slot]
}
struct Slot: Decodable, Identifiable {
    let slot: String
    let label: String
    let state: String
    let windows: [QuotaWindow]
    var id: String { slot }
}
struct QuotaWindow: Decodable {
    let pool: String
    let remaining_percent: Double?
    let resets_at_utc: String?
}
func date(_ value: String?) -> Date? {
    guard let value else { return nil }
    let f = ISO8601DateFormatter()
    f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
    return f.date(from: value) ?? ISO8601DateFormatter().date(from: value)
}
@MainActor final class Store: ObservableObject {
    static let shared = Store()
    private var refreshTimer: Timer?
    @Published var snapshot: Snapshot?
    @Published var problem: String?
    let demo: Bool
    let path: String?
    init() {
        let args = CommandLine.arguments
        func option(_ key: String) -> String? {
            guard let i = args.firstIndex(of: key), i + 1 < args.count else { return nil }
            return args[i + 1]
        }
        path = option("--snapshot")
        demo = args.contains("--demo") || path == nil
        refresh()
        refreshTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
    }
    func refresh() {
        if demo {
            if snapshot != nil { objectWillChange.send(); return }
            let f = ISO8601DateFormatter()
            snapshot = Snapshot(schema: 1, written_at_utc: f.string(from: Date()), slots: [
                Slot(slot: "codex_primary", label: "Codex主", state: "ok", windows: [QuotaWindow(pool: "codex_primary_window", remaining_percent: 72, resets_at_utc: f.string(from: Date().addingTimeInterval(8100))), QuotaWindow(pool: "codex_secondary_window", remaining_percent: 84, resets_at_utc: f.string(from: Date().addingTimeInterval(172800)))]),
                Slot(slot: "claude", label: "Claude", state: "disabled", windows: []),
                Slot(slot: "cursor", label: "Cursor", state: "disabled", windows: [])])
            return
        }
        do {
            guard let path else { return }
            let url = URL(fileURLWithPath: path)
            let attrs = try FileManager.default.attributesOfItem(atPath: path)
            guard (attrs[.size] as? NSNumber)?.intValue ?? 0 <= 1_048_576 else { throw CocoaError(.fileReadTooLarge) }
            let value = try JSONDecoder().decode(Snapshot.self, from: Data(contentsOf: url))
            guard value.schema == 1 else { throw CocoaError(.coderReadCorrupt) }
            snapshot = value
            problem = nil
        } catch {
            problem = "快照不可用"
            // Never present a previous reading as a newly successful observation.
        }
    }
    var stale: Bool { !demo && (problem != nil || (snapshot.flatMap { date($0.written_at_utc) }.map { Date().timeIntervalSince($0) > 300 } ?? true)) }
    var isDemo: Bool { demo || snapshot?.hud_version?.contains("demo") == true }
    var primary: Slot? { snapshot?.slots.first { $0.slot == "codex_primary" } }
    var percent: Double? {
        guard !stale, let p = primary, p.state == "ok" else { return nil }
        let value = (p.windows.first { $0.pool == "codex_primary_window" } ?? p.windows.first)?.remaining_percent
        guard let value, value.isFinite, (0...100).contains(value) else { return nil }
        return value
    }
    func usable(_ window: QuotaWindow, in slot: Slot) -> Double? {
        guard !stale, slot.state == "ok", let value = window.remaining_percent,
              value.isFinite, (0...100).contains(value) else { return nil }
        return value
    }
    var menuTitle: String { percent.map { String(format: "%.0f%%", $0) } ?? "—" }
}
struct Panel: View {
    @ObservedObject var store: Store
    let clock = Timer.publish(every: 1, on: .main, in: .common).autoconnect()
    @State private var now = Date()
    func countdown(_ value: String?) -> String {
        guard let reset = date(value) else { return "重置时间未提供" }
        let seconds = max(0, Int(reset.timeIntervalSince(now)))
        if seconds == 0 { return "已到重置时间 · 等待刷新" }
        return seconds >= 86400 ? "距离重置 \(seconds / 86400)天 \(seconds % 86400 / 3600)小时" : "距离重置 \(seconds / 3600)小时 \(seconds % 3600 / 60)分钟"
    }
    func windowLabel(_ pool: String) -> String {
        let labels = ["codex_primary_window": "主窗口", "codex_secondary_window": "次窗口", "codex_weekly": "每周窗口", "codex": "额度窗口", "cursor_auto": "Auto 窗口", "cursor_api": "API 窗口", "cursor_team": "团队共用", "grok_weekly": "每周窗口", "grok_bot": "Bot 窗口", "claude_five_hour": "5 小时窗口", "claude_seven_day": "每周全部模型"]
        return labels[pool] ?? "额度窗口（\(pool)）"
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Image(systemName: "chart.donut").foregroundStyle(.blue)
                Text("额度").font(.headline)
                Spacer()
                Text(store.isDemo ? "演示" : store.stale ? "已过期" : "只读").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 4) {
                Text("Codex 剩余额度").font(.subheadline).foregroundStyle(.secondary)
                Text(store.menuTitle).font(.system(size: 52, weight: .semibold, design: .rounded)).monospacedDigit()
                if let value = store.percent { ProgressView(value: value, total: 100).tint(.blue) }
            }
            Divider()
            ForEach(store.snapshot?.slots ?? []) { slot in
                VStack(alignment: .leading, spacing: 7) {
                    HStack { Text(slot.label).font(.subheadline.weight(.medium)); Spacer(); Text(["disabled": "已关闭", "ok": "可用", "stale": "已过期", "unavailable": "不可用"][slot.state] ?? "未知").font(.caption).foregroundStyle(.secondary) }
                    ForEach(Array(slot.windows.enumerated()), id: \.offset) { _, window in
                        HStack(alignment: .top) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(windowLabel(window.pool)).font(.caption)
                                Text(!store.stale && slot.state == "ok" ? countdown(window.resets_at_utc) : "重置时间未提供").font(.caption2).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Text(store.usable(window, in: slot).map { String(format: "%.0f%%", $0) } ?? "未知").monospacedDigit().font(.subheadline.weight(.medium))
                        }
                    }
                }
            }
            if let problem = store.problem { Text(problem).font(.caption).foregroundStyle(.orange) }
            HStack {
                Text(store.isDemo ? "模拟数据 · 未访问账号" : store.stale ? "上次观测 · 非实时" : "本地快照已更新").font(.caption2).foregroundStyle(.secondary)
                Spacer()
                Button { NSApplication.shared.terminate(nil) } label: { Image(systemName: "power") }.buttonStyle(.borderless).help("退出")
            }
        }.padding(22).frame(width: 320)
        .onReceive(clock) { now = $0 }
    }
}
@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    var preview: NSWindow?
    private var statusItem: NSStatusItem?
    private let popover = NSPopover()
    private var observation: AnyCancellable?

    private func panelSize() -> NSSize {
        let fitting = NSHostingView(rootView: Panel(store: Store.shared)).fittingSize
        let maxHeight = max(240, (NSScreen.main?.visibleFrame.height ?? 800) - 100)
        return NSSize(width: max(320, fitting.width), height: min(max(240, fitting.height), maxHeight))
    }
    private var requestedAppearance: NSAppearance? {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--appearance"), i + 1 < args.count else { return nil }
        return NSAppearance(named: args[i + 1] == "dark" ? .darkAqua : .aqua)
    }
    private var requestedColorScheme: ColorScheme? {
        guard let appearance = requestedAppearance else { return nil }
        return appearance.name == .darkAqua ? .dark : .light
    }
    private func panelView(_ size: NSSize) -> some View {
        ScrollView(.vertical) { Panel(store: Store.shared) }
            .frame(width: size.width, height: size.height)
            .preferredColorScheme(requestedColorScheme)
    }
    func applicationDidFinishLaunching(_ notification: Notification) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem = item
        if let button = item.button {
            button.title = Store.shared.menuTitle
            button.font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
            button.image = NSImage(systemSymbolName: "chart.donut", accessibilityDescription: "额度")
            button.imagePosition = .imageLeading
            button.target = self
            button.action = #selector(togglePopover(_:))
        }
        observation = Store.shared.objectWillChange.sink { [weak self] _ in
            DispatchQueue.main.async { self?.statusItem?.button?.title = Store.shared.menuTitle }
        }
        popover.behavior = .transient
        popover.animates = false
        if CommandLine.arguments.contains("--panel") { showPreview() }
        if CommandLine.arguments.contains("--popover") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { self.showPopover() }
        }
    }
    @objc private func togglePopover(_ sender: Any?) {
        if popover.isShown { popover.performClose(sender) } else { showPopover() }
    }
    private func showPopover() {
        guard let button = statusItem?.button else { return }
        let size = panelSize()
        popover.contentSize = size
        let controller = NSHostingController(rootView: panelView(size))
        controller.view.appearance = requestedAppearance
        popover.appearance = requestedAppearance
        popover.contentViewController = controller
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            if let view = self.popover.contentViewController?.view, let window = view.window {
                print("HUD_POPOVER_WINDOW_ID=\(window.windowNumber)")
                print("HUD_POPOVER_WINDOW_SIZE=\(window.frame.width),\(window.frame.height)")
                let rect = window.convertToScreen(view.convert(view.bounds, to: nil))
                let primaryTop = NSScreen.screens.first?.frame.maxY ?? 0
                print("HUD_POPOVER_CONTENT_RECT_CAPTURE=\(Int(rect.minX.rounded(.up))),\(Int((primaryTop - rect.maxY).rounded(.up))),\(Int(rect.width.rounded(.down))),\(Int(rect.height.rounded(.down)))")
            }
            if let window = button.window {
                let rect = window.convertToScreen(button.convert(button.bounds, to: nil))
                print("HUD_STATUS_RECT_APPKIT=\(rect.origin.x),\(rect.origin.y),\(rect.width),\(rect.height)")
                // screencapture -R uses top-left points relative to the primary display.
                let primaryTop = NSScreen.screens.first?.frame.maxY ?? 0
                print("HUD_STATUS_RECT_CAPTURE=\(Int(rect.minX.rounded(.down))),\(Int((primaryTop - rect.maxY).rounded(.down))),\(Int(rect.width.rounded(.up))),\(Int(rect.height.rounded(.up)))")
            }
            fflush(stdout)
        }
    }
    private func showPreview() {
        let size = panelSize()
        let window = NSWindow(contentRect: NSRect(origin: NSPoint(x: 180, y: 180), size: size), styleMask: [.titled, .closable], backing: .buffered, defer: false)
        window.title = "额度 · 预览"
        window.contentView = NSHostingView(rootView: panelView(size))
        window.appearance = requestedAppearance
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        preview = window
        print("HUD_WINDOW_ID=\(window.windowNumber)")
        fflush(stdout)
    }
}
@main struct UsageHUD: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    init() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--appearance"), i + 1 < args.count {
            NSApplication.shared.appearance = NSAppearance(named: args[i + 1] == "dark" ? .darkAqua : .aqua)
        }
    }
    var body: some Scene {
        Settings { EmptyView() }
    }
}
