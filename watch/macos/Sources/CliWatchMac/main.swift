// SPDX-License-Identifier: MPL-2.0
import SwiftUI
import AppKit

// Decode the shared .NET projection only. No process/session/provider probing here.
struct Envelope: Decodable {
    let schema: Int
    let written_at_utc: String
    let mode: String
    let snapshot: Snapshot
}
struct Snapshot: Decodable { let projects: [Project]; let issues: [Issue]; let quality: String }
struct Issue: Decodable { let code: String; let detail: String }
struct Project: Decodable, Identifiable {
    let id: String; let name: String; let summary: String; let phase: String
    let nextStep: String?; let isActive: Bool; let isCurrent: Bool; let isPaused: Bool; let quality: String
    let workItems: [Work]
}
struct Work: Decodable, Identifiable {
    let id: String; let role: String; let summary: String; let actorName: String?; let taskName: String?
    let axes: [String: String]; let artifacts: [Artifact]; let quality: String
    let blocker: String?
}
struct Artifact: Decodable, Identifiable { let id: String; let label: String; let state: String }
func option(_ name: String) -> String? {
    let args = CommandLine.arguments
    guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
    return args[i + 1]
}
func quality(_ value: String) -> String {
    ["fresh":"来源可读", "lastKnown":"历史／上次已知", "unavailable":"不可用", "conflicting":"来源冲突", "partial":"本轮部分可读"][value] ?? "未知"
}
// Presentation-only labels aligned with Windows StatusLanguage.HumanizeToken.
// Each axis is translated in isolation; none is promoted to an overall verdict.
func sourceText(_ value: String?) -> String {
    guard let value, !value.trimmingCharacters(in: .whitespaces).isEmpty else { return "未知" }
    let labels: [String: String] = ["active":"进行中", "running":"进行中", "alive":"存活", "dead":"已退出", "exited":"已退出", "failed":"失败", "succeeded":"成功返回", "success":"成功返回", "returned":"已返回", "complete":"已完成（该轴）", "completed":"已完成（该轴）", "pending":"待处理", "unknown":"未知", "paused":"已暂停", "accepted":"已接受", "adopted":"已采用（部分或全部）", "adoption_partial":"已采用（部分或全部）", "executing":"执行中", "generation":"生成", "awaiting_lead_acceptance":"待原Lead验收", "lead_accepting":"负责人正在验收", "acceptance_in_progress":"负责人正在验收", "repair_dispatched":"退修已派出", "callback_wait":"等待回叫", "content_not_accepted":"内容未验收", "lead_handled":"已处理", "handled":"已处理", "fault_pending_handling":"故障待处理", "execution":"执行", "exec":"执行", "lead_execution":"执行", "lead":"原Lead", "delivered":"已交卷"]
    return labels[value.lowercased()] ?? value
}
@MainActor final class Store: ObservableObject {
    static let shared = Store()
    @Published var envelope: Envelope?
    @Published var error: String?
    private var timer: Timer?
    private let path: String
    init() {
        path = option("--snapshot") ?? NSHomeDirectory() + "/Library/Application Support/AgentBatonWatch/watch-snapshot.json"
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
    }
    private func timestampWarning(_ raw: String) -> String? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let timestamp = fractional.date(from: raw) ?? ISO8601DateFormatter().date(from: raw) else {
            return "快照时间无法解析，当前状态未知。以下仅展示已有记录，不作为当前活动证据。"
        }
        let age = Date().timeIntervalSince(timestamp)
        if age < -30 {
            return "快照时间明显超前，当前状态未知。以下仅展示已有记录，不作为当前活动证据。"
        }
        if age > 60 {
            return "快照已过期（超过 60 秒未更新）。以下仅为上次已知，当前活动状态未知。"
        }
        return nil
    }
    func refresh() {
        do {
            let attrs = try FileManager.default.attributesOfItem(atPath: path)
            guard ((attrs[.size] as? NSNumber)?.intValue ?? 0) <= 4_194_304 else { throw CocoaError(.fileReadTooLarge) }
            let value = try JSONDecoder().decode(Envelope.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
            guard value.schema == 1 else { throw CocoaError(.coderReadCorrupt) }
            envelope = value
            error = timestampWarning(value.written_at_utc)
        } catch {
            self.error = envelope == nil ? "尚无可读快照，当前状态未知。" : "快照读取失败。以下仅为上次已知，不能当作当前状态。"
        }
    }
}
struct FactTile: View {
    let label: String
    let value: String?
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(sourceText(value)).font(.system(size: 12, weight: .medium)).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }.frame(maxWidth: .infinity, alignment: .leading).padding(10)
            .background(Color.primary.opacity(0.035), in: RoundedRectangle(cornerRadius: 8))
    }
}
struct AxisDefinition: Identifiable {
    let id: String
    let label: String
}
let primaryAxes: [AxisDefinition] = [
    .init(id: "process", label: "进程"), .init(id: "turn", label: "轮次"),
    .init(id: "execution", label: "执行／退出"), .init(id: "delivery", label: "交卷"),
    .init(id: "acceptance", label: "验收")]
let secondaryAxes: [AxisDefinition] = [
    .init(id: "transport", label: "传递"), .init(id: "callback", label: "回执"),
    .init(id: "leadHandling", label: "负责人处理"), .init(id: "adoption", label: "采纳"),
    .init(id: "goal", label: "目标")]
struct AxisGrid: View {
    let definitions: [AxisDefinition]
    let values: [String: String]
    private let columns = Array(repeating: GridItem(.flexible(), alignment: .topLeading), count: 5)
    var body: some View {
        LazyVGrid(columns: columns, alignment: .leading, spacing: 8) {
            ForEach(definitions) { axis in FactTile(label: axis.label, value: values[axis.id]) }
        }
    }
}
struct WorkHeading: View {
    let work: Work
    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline) {
                Text(work.taskName ?? work.summary).font(.subheadline.weight(.semibold))
                Spacer()
                Text(work.actorName ?? work.role).font(.caption).foregroundStyle(.secondary)
            }
            if work.taskName != nil && !work.summary.isEmpty {
                Text(work.summary).font(.caption).foregroundStyle(.secondary)
            }
        }.textSelection(.enabled)
    }
}
struct ArtifactRow: View {
    let item: Artifact
    var body: some View {
        HStack {
            Image(systemName: "doc.text")
            Text(item.label)
            Spacer()
            Text(sourceText(item.state))
        }.font(.caption).foregroundStyle(.secondary)
    }
}
struct WorkCard: View {
    let work: Work
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            WorkHeading(work: work)
            AxisGrid(definitions: primaryAxes, values: work.axes)
            if let blocker = work.blocker, !blocker.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "info.circle")
                    Text(blocker).textSelection(.enabled)
                }.font(.caption).foregroundStyle(.secondary)
            }
            DisclosureGroup("更多独立事实") {
                AxisGrid(definitions: secondaryAxes, values: work.axes).padding(.top, 8)
            }.font(.caption).foregroundStyle(.secondary)
            ForEach(work.artifacts) { ArtifactRow(item: $0) }
        }.padding(16)
            .background(Color(nsColor: .controlBackgroundColor), in: RoundedRectangle(cornerRadius: 12))
    }
}
struct ProjectCard: View {
    let project: Project
    let lastKnown: Bool
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(project.name).font(.headline)
                Text(lastKnown ? (project.isCurrent ? "原当前记录" : "原历史记录") : (project.isCurrent ? "当前" : "历史")).font(.caption).foregroundStyle(.secondary)
                if project.isPaused { Text("已暂停").font(.caption).foregroundStyle(.secondary) }
                Spacer()
                Text((lastKnown ? "原观测：" : "") + quality(project.quality)).font(.caption).foregroundStyle(.secondary)
            }
            if !project.summary.isEmpty {
                Text(project.summary).font(.subheadline).foregroundStyle(.secondary).textSelection(.enabled)
            }
            ForEach(project.workItems) { WorkCard(work: $0) }
            if let next = project.nextStep, !next.isEmpty {
                Text("下一步：" + next).font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}
struct DashboardHeader: View {
    @ObservedObject var store: Store
    @Binding var selection: String
    var currentCount: Int { store.envelope?.snapshot.projects.filter { $0.isCurrent }.count ?? 0 }
    var historyCount: Int { store.envelope?.snapshot.projects.filter { !$0.isCurrent }.count ?? 0 }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 5) {
                    Text("CLI 执行窗").font(.system(size: 25, weight: .semibold))
                    Text("活动、交卷、退出与验收，分开看。").font(.subheadline).foregroundStyle(.secondary)
                }
                Spacer()
                Text(["fixture", "demo"].contains(store.envelope?.mode ?? "") ? "模拟数据" : "只读投影")
                    .font(.caption.weight(.medium)).padding(8)
                    .background(Color.blue.opacity(0.10), in: Capsule())
                Button { store.refresh() } label: { Image(systemName: "arrow.clockwise") }
                    .help("刷新本地快照").keyboardShortcut("r")
            }
            scope
            if let error = store.error { Text(error).font(.caption).foregroundStyle(.orange) }
        }.padding(24)
    }
    private var scope: some View {
        HStack {
            Picker("范围", selection: $selection) {
                Text("全部").tag("all")
                Text("当前 · \(currentCount)").tag("current")
                Text("历史 · \(historyCount)").tag("history")
            }.pickerStyle(.segmented).frame(width: 320)
            Spacer()
            Text(store.error != nil ? "当前未知 · 保留已有记录" : quality(store.envelope?.snapshot.quality ?? "unknown")).font(.caption).foregroundStyle(.secondary)
        }
    }
}
struct CoverageView: View {
    let issues: [Issue]
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if !issues.isEmpty { Text("覆盖缺口").font(.headline) }
            ForEach(issues.indices, id: \.self) { index in
                Text(issues[index].detail).font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}
struct Dashboard: View {
    @ObservedObject var store: Store
    @State private var selection = "all"
    var projects: [Project] { store.envelope?.snapshot.projects ?? [] }
    var selectedProjects: [Project] {
        projects.filter { project in
            selection == "all" || (selection == "current" ? project.isCurrent : !project.isCurrent)
        }
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            DashboardHeader(store: store, selection: $selection)
            Divider()
            content
            Divider()
            footer
        }.frame(minWidth: 760, minHeight: 500).background(Color(nsColor: .windowBackgroundColor))
    }
    private var content: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                if projects.isEmpty {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("当前状态未知").font(.title3.weight(.semibold))
                        Text("没有可展示的投影数据。不把未读到的记录当作没有任务，也不猜测执行结果。")
                            .font(.subheadline).foregroundStyle(.secondary)
                    }.frame(maxWidth: .infinity, alignment: .leading).padding(.vertical, 24)
                }
                ForEach(selectedProjects) { ProjectCard(project: $0, lastKnown: store.error != nil) }
                CoverageView(issues: store.envelope?.snapshot.issues ?? [])
            }.padding(24)
        }
    }
    private var footer: some View {
        HStack {
            Text("交卷 ≠ 验收通过 · 退出 ≠ 业务成功").font(.caption)
            Spacer()
            Text(store.envelope?.written_at_utc ?? "尚未采集").font(.caption2).monospacedDigit()
        }.foregroundStyle(.secondary).padding(.horizontal,24).padding(.vertical,12)
    }
}
@MainActor final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    private var window: NSWindow?
    func applicationDidFinishLaunching(_ notification: Notification) {
        let appearance: NSAppearance? = option("--appearance").flatMap { NSAppearance(named: $0 == "dark" ? .darkAqua : .aqua) }
        let scheme: ColorScheme? = option("--appearance").map { $0 == "dark" ? .dark : .light }
        let visible = NSScreen.main?.visibleFrame ?? NSRect(x:0,y:0,width:1024,height:768)
        let size = NSSize(width:min(960,visible.width-40),height:min(820,visible.height-60))
        let w = NSWindow(contentRect:NSRect(origin:.zero,size:size),styleMask:[.titled,.closable,.miniaturizable,.resizable],backing:.buffered,defer:false)
        w.title = "CLI 执行窗"
        w.appearance = appearance
        let host = NSHostingView(rootView:Dashboard(store:Store.shared).preferredColorScheme(scheme))
        host.appearance = appearance
        w.contentView = host
        w.delegate = self
        w.center(); w.makeKeyAndOrderFront(nil)
        window = w
        NSApp.activate(ignoringOtherApps:true)
        DispatchQueue.main.asyncAfter(deadline:.now()+0.5) {
            print("WATCH_WINDOW_ID=\(w.windowNumber)")
            print("WATCH_WINDOW_SIZE=\(w.frame.width),\(w.frame.height)")
            fflush(stdout)
        }
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender:NSApplication) -> Bool { true }
}
@main struct CliWatchMac: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    var body: some Scene { Settings { EmptyView() } }
}
