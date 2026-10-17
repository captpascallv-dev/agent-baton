// SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace CliWatch.Projection;

/// <summary>
/// CLI Watch observation states. Executor exit/success is not Lead acceptance.
/// Long silence is not a hang. Wrapper alive is not CLI alive.
/// </summary>
public static class CliWatchStatus
{
    public const string Registered = "registered";
    public const string StartUnconfirmed = "start_unconfirmed";
    public const string RunningActive = "running_active";
    public const string RunningQuiet = "running_quiet";
    public const string RunningUnknown = "running_unknown";
    public const string Waiting = "waiting";
    public const string Delivered = "delivered";
    public const string ReportedFailure = "reported_failure";
    public const string ProcessGone = "process_gone";
    public const string TransportFailed = "transport_failed";
    public const string ExecutorFinished = "executor_finished";
    public const string Failed = "failed";
    public const string Retired = "retired";
    public const string Unknown = "unknown";
    public const string Unbound = "unbound";
    public const string AcceptanceUnverifiedZh = "已有Lead验收记录，结论未核实。已结束的交卷归入历史，不代表本窗判定通过。";
    public const string AcceptanceUnverifiedEn = "A Lead acceptance record exists; its conclusion is not verified. Ended deliveries move to history without a pass verdict from this window.";

    public static bool IsKnown(string? code) =>
        code is Registered or StartUnconfirmed or RunningActive or RunningQuiet or RunningUnknown
            or Waiting or Delivered or ReportedFailure or ProcessGone or TransportFailed or ExecutorFinished or Failed or Retired or Unknown or Unbound;

    public static bool IsRunning(string? code) =>
        code is RunningActive or RunningQuiet or RunningUnknown or Waiting;

    public static bool IsTerminal(string? code) =>
        code is Delivered or ReportedFailure or ProcessGone or TransportFailed or ExecutorFinished or Failed;

    public static string LabelZh(string? code) => code switch
    {
        Registered => "已登记/启动未证实",
        StartUnconfirmed => "包装器在，CLI 未证实",
        RunningActive => "运行中有活动",
        RunningQuiet => "进程在，暂无新活动",
        RunningUnknown => "进程在，活动未知",
        Waiting => "明确等待",
        Delivered => "已交付待 Lead",
        ReportedFailure => "问题已交回，待 Lead",
        ProcessGone => "未见原进程，缺交卷记录",
        TransportFailed => "执行交付链异常，待 Lead",
        ExecutorFinished => "执行器已结束，未见交付",
        Failed => "明确异常退出/失败",
        Retired => "已退役/已被替代",
        Unbound => "未关联任务",
        _ => "状态未知"
    };

    public static string BasisZh(string? code) => code switch
    {
        Registered => "任务已登记，还没有证实对应 CLI 进程。",
        StartUnconfirmed => "登记的包装器进程还在，但没有证实实际 CLI 子进程。包装器存活不等于 CLI 存活。",
        RunningActive => "已绑定的 CLI 进程仍在，并且这一轮观察到新的过程记录。",
        RunningQuiet => "已绑定的 CLI 进程仍在，但这一轮没有新的过程记录。不能据此认定卡死。",
        RunningUnknown => "已绑定的 CLI 进程仍在，但活动观察不足或尚未建立基线。不能说有活动，也不能认定卡死。",
        Waiting => "原生活动记录表明当前在等待。",
        Delivered => "已见到交付文件。这只说明执行方交回，不是 Lead 验收通过。",
        ReportedFailure => "交卷明确报告失败或阻碍；这是任务结果，不等于执行者进程异常退出。原始失败记录保留，待原 Lead 处理。",
        ProcessGone => "已核对原启动身份，当前未见原进程，但还没有匹配的终态回执或交卷。退出原因未知，不据此认定成功、失败或自动重跑。",
        TransportFailed => "原适配器已记录执行交付链失败，尚未见到有效交卷。业务结果未核实，需原 Lead 查看原始退出与交付证据；本窗不自动重跑。",
        ExecutorFinished => "执行器回执表明过程已结束，但还没有见到交付文件。exit 0 也不等于业务验收。",
        Failed => "有明确失败/异常退出依据。",
        Retired => "登记明确退役或已被替代。这不是完成、失败或 Lead 验收。",
        Unbound => "本机有可识别的 CLI 进程，但没有对应的任务登记。不猜测所属项目。",
        _ => "现有依据不足以判断。"
    };

    public static string Label(string? code, bool english) =>
        english ? LabelEn(code) : LabelZh(code);

    public static string Basis(string? code, bool english) =>
        english ? BasisEn(code) : BasisZh(code);

    public static string LabelEn(string? code) => code switch
    {
        Registered => "Registered / start not confirmed",
        StartUnconfirmed => "Wrapper alive, CLI not confirmed",
        RunningActive => "Running with activity",
        RunningQuiet => "Process alive, no new activity",
        RunningUnknown => "Process alive, activity unknown",
        Waiting => "Waiting (native evidence)",
        Delivered => "Delivered, waiting on Lead",
        ReportedFailure => "Issue returned, waiting on Lead",
        ProcessGone => "Original process absent, no hand-back",
        TransportFailed => "Execution hand-back failed, waiting on Lead",
        ExecutorFinished => "Executor finished, no delivery yet",
        Failed => "Abnormal exit / failure",
        Retired => "Retired / superseded",
        Unbound => "Unbound CLI",
        _ => "Status unknown"
    };

    public static string BasisEn(string? code) => code switch
    {
        Registered => "The job is registered; no matching CLI process is confirmed.",
        StartUnconfirmed => "The registered wrapper is alive, but the actual CLI child is not confirmed. Wrapper alive is not CLI alive.",
        RunningActive => "The bound CLI process is alive and this refresh observed new activity evidence.",
        RunningQuiet => "The bound CLI process is alive, but this refresh has no new activity record. This is not a hang verdict.",
        RunningUnknown => "The bound CLI process is alive, but activity observation is missing or the baseline is not established. That is not activity and not a hang.",
        Waiting => "Native activity evidence shows a wait.",
        Delivered => "A delivery file is present. That is executor hand-back, not Lead acceptance.",
        ReportedFailure => "The delivery reports failure or a blocker. That is a task outcome, not proof of a process crash. The original result is preserved for the Lead.",
        ProcessGone => "The original launch identity was verified, but the process is no longer observed and no matching terminal receipt or delivery is available. Cause is unknown; no success, failure or retry is inferred.",
        TransportFailed => "The original adapter records execution or hand-back transport failure with no valid delivery. Business outcome is unverified; the Lead must inspect the original evidence. No automatic retry.",
        ExecutorFinished => "The executor receipt says the process ended, but no delivery file is present. Exit 0 is not business acceptance.",
        Failed => "There is explicit failure or abnormal-exit evidence.",
        Retired => "The registration is explicitly retired or superseded. That is not completion, failure, or Lead acceptance.",
        Unbound => "A recognizable CLI process is running with no matching job. Project is not guessed.",
        _ => "Not enough evidence to judge."
    };

    public static string? ActivityKindZh(string? kind) => kind switch
    {
        "cli_log" => "最近 CLI 日志",
        "child_io" => "子程序有计算/读写活动（不等于任务在推进）",
        "cli_compute" => "CLI 进程有计算/读写活动（不等于任务在推进）",
        "quiet" => "进程在但未观测到新活动",
        "unavailable" => "活动观察不可用",
        "baseline" => "活动基线未建立",
        _ => null
    };

    public static string? ActivityKindEn(string? kind) => kind switch
    {
        "cli_log" => "recent CLI log",
        "child_io" => "child process compute/IO (not task progress)",
        "cli_compute" => "CLI process compute/IO (not task progress)",
        "quiet" => "process alive, no new activity observed",
        "unavailable" => "activity observation unavailable",
        "baseline" => "activity baseline not yet established",
        _ => null
    };

    public static string? ActivityNoteZh(string? kind) => kind switch
    {
        "child_io" => "子程序有计算或读写。这不等于任务有效推进或完成。",
        "cli_compute" => "已绑定 CLI 有计算或读写。这不等于任务有效推进或完成。",
        "unavailable" => "未能读取进程计数，活动按未知处理，不伪造有活动。",
        "quiet" => "进程仍在，但暂时没有新的日志或计数增量。不能据此认定卡死。",
        _ => null
    };

    public static string FormatDuration(TimeSpan? span, bool english)
    {
        if (span is null) return english ? "duration unknown" : "运行时长未知";
        var t = span.Value;
        if (t.TotalSeconds < 0) t = TimeSpan.Zero;
        if (t.TotalDays >= 1)
            return english
                ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
                : $"{(int)t.TotalDays} 天 {t.Hours} 小时 {t.Minutes} 分";
        if (t.TotalHours >= 1)
            return english
                ? $"{(int)t.TotalHours}h {t.Minutes}m"
                : $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1)
            return english
                ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
                : $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒";
        return english ? $"{(int)t.TotalSeconds}s" : $"{(int)t.TotalSeconds} 秒";
    }

    public static string FormatAgo(DateTimeOffset? at, DateTimeOffset now, bool english)
    {
        if (at is null) return english ? "Last activity: unknown" : "最后活动未知";
        var delta = now - at.Value;
        if (delta.TotalSeconds < 0) delta = TimeSpan.Zero;
        var core = FormatDuration(delta, english);
        return english ? "Last activity: " + core + " ago" : "最后活动 " + core + "前";
    }

    public static bool TryParseTime(string? raw, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)
               || DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
}
