// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;
using CliWatch.Projection;

namespace CliWatch.Views;

public static class CliWatchCopy
{
    public static bool IsWatch(ProjectView project) =>
        CliWatchStatus.IsKnown(project.Phase)
        || project.WorkItems.Any(w => (w.Route ?? "").StartsWith("watch:", StringComparison.Ordinal));

    public static bool TryJudgment(ProjectView project, UiLang lang, out (string Label, string Basis) result)
    {
        result = default;
        if (!IsWatch(project)) return false;
        var en = lang == UiLang.En;
        var basis = CliWatchStatus.Basis(project.Phase, en);
        if (HasUnverifiedAcceptance(project))
            basis = en ? CliWatchStatus.AcceptanceUnverifiedEn : CliWatchStatus.AcceptanceUnverifiedZh;
        result = (CliWatchStatus.Label(project.Phase, en), basis);
        return true;
    }

    public static string OneLiner(ProjectView project, UiLang lang)
    {
        if (!string.IsNullOrWhiteSpace(project.Summary) && !project.Summary.Contains("unknown", StringComparison.OrdinalIgnoreCase))
            return lang == UiLang.En ? ConsumerCopy.Localize(project.Summary, lang) : project.Summary;
        var work = project.WorkItems.FirstOrDefault();
        if (work is not null && !string.IsNullOrWhiteSpace(work.Summary))
            return lang == UiLang.En ? ConsumerCopy.Localize(work.Summary, lang) : work.Summary;
        return CliWatchStatus.Label(project.Phase, lang == UiLang.En);
    }

    public static string WorkPhrase(WorkView work, UiLang lang)
    {
        var route = work.Route ?? "";
        if (route.StartsWith("watch:", StringComparison.Ordinal))
            return CliWatchStatus.Label(route["watch:".Length..], lang == UiLang.En);
        if (!string.IsNullOrWhiteSpace(work.Summary))
            return lang == UiLang.En ? ConsumerCopy.Localize(work.Summary, lang) : work.Summary;
        return CliWatchStatus.Label(CliWatchStatus.Unknown, lang == UiLang.En);
    }

    public static (string Who, string HowFar, string Stuck, string Next, string User, string Phase, string Goal, string Summary)
        Situation(ProjectView project, UiLang lang)
    {
        var work = project.WorkItems.FirstOrDefault(w => !StatusLanguage.IsHistoricalWork(w))
                   ?? project.WorkItems.FirstOrDefault();
        var who = WhoLine(project, work, lang);
        var howFar = OneLiner(project, lang);
        var stuck = project.Phase switch
        {
            CliWatchStatus.RunningActive => ConsumerCopy.L(lang,
                "有日志或子程序计数增量。子程序有活动不等于任务有效推进或完成。",
                "There is a log or child-process counter increase. Child activity is not task progress or completion."),
            CliWatchStatus.RunningQuiet => ConsumerCopy.L(lang,
                "进程仍在，但暂时没有新的日志或计数增量。不能据此认定卡死，需要时打开依据查看。",
                "The process is alive but there is no new log or counter increase. This is not a hang. Open evidence if you need to look."),
            CliWatchStatus.RunningUnknown => ConsumerCopy.L(lang,
                "进程仍在，活动观察不足或尚未建立基线。不能说有活动，也不能认定卡死。",
                "The process is alive, but activity observation is missing or the baseline is not established. That is not activity and not a hang."),
            CliWatchStatus.StartUnconfirmed => ConsumerCopy.L(lang,
                "包装器还在，实际 CLI 尚未证实。包装器存活不等于 CLI 存活。",
                "The wrapper is alive; the actual CLI is not confirmed. Wrapper alive is not CLI alive."),
            CliWatchStatus.Unknown => ConsumerCopy.L(lang, "状态依据不足。", "Not enough evidence."),
            CliWatchStatus.Failed => ConsumerCopy.L(lang, "有明确失败或异常退出依据。", "There is explicit failure or abnormal-exit evidence."),
            CliWatchStatus.ReportedFailure => CliWatchStatus.Basis(project.Phase, lang == UiLang.En),
            CliWatchStatus.ProcessGone => CliWatchStatus.Basis(project.Phase, lang == UiLang.En),
            CliWatchStatus.TransportFailed => CliWatchStatus.Basis(project.Phase, lang == UiLang.En),
            CliWatchStatus.Retired => ConsumerCopy.L(lang, "登记明确退役或已被替代，不是完成或失败。", "The registration is retired or superseded; that is not completion or failure."),
            CliWatchStatus.Waiting => ConsumerCopy.L(lang, "原生活动记录表明当前在等待。", "Native activity evidence shows a wait."),
            _ => ConsumerCopy.L(lang, "目前没有需要当成卡死的依据。", "No evidence that it is stuck.")
        };
        var next = string.IsNullOrWhiteSpace(project.NextStep)
            ? ConsumerCopy.L(lang, "继续观察。本窗不派单、不干预。", "Keep watching. This window does not dispatch or intervene.")
            : ConsumerCopy.Localize(project.NextStep, lang);
        var user = ConsumerCopy.L(lang, "本窗不要求你操作任务。", "This window does not ask you to operate the job.");
        var goal = string.IsNullOrWhiteSpace(project.Goal)
            ? ConsumerCopy.L(lang, "未声明", "not stated")
            : project.Goal;
        return (
            who,
            howFar,
            stuck,
            next,
            user,
            CliWatchStatus.Label(project.Phase, lang == UiLang.En),
            goal,
            OneLiner(project, lang));
    }

    public static string WhoLine(ProjectView project, WorkView? work, UiLang lang)
    {
        if (HasUnverifiedAcceptance(project, work))
            return ConsumerCopy.L(lang, CliWatchStatus.AcceptanceUnverifiedZh, CliWatchStatus.AcceptanceUnverifiedEn);

        if (work is null)
            return ConsumerCopy.L(lang, "还没有绑定的 CLI。", "No bound CLI yet.");

        var actor = string.IsNullOrWhiteSpace(work.ActorName) ? "CLI" : work.ActorName;
        var task = string.IsNullOrWhiteSpace(work.TaskName) ? project.Name : work.TaskName;
        var zhTask = "「" + task + "」";
        var enTask = "\"" + task + "\"";
        return project.Phase switch
        {
            CliWatchStatus.RunningActive or CliWatchStatus.Waiting =>
                ConsumerCopy.L(lang,
                    actor + " 在执行" + zhTask + "。本窗只观察。",
                    actor + " is running " + enTask + ". This window only observes."),
            CliWatchStatus.RunningQuiet =>
                ConsumerCopy.L(lang,
                    actor + " 进程仍在，这一轮没有新的过程记录。「" + task + "」本窗只观察。",
                    actor + " is still running; this refresh has no new activity on " + enTask + ". This window only observes."),
            CliWatchStatus.RunningUnknown =>
                ConsumerCopy.L(lang,
                    actor + " 进程仍在，活动观察未知。「" + task + "」本窗只观察。",
                    actor + " is alive; activity is unknown for " + enTask + ". This window only observes."),
            CliWatchStatus.Delivered =>
                ConsumerCopy.L(lang,
                    actor + " 已交回" + zhTask + "，待 Lead。本窗不验收。",
                    actor + " handed back " + enTask + "; waiting on Lead. This window does not accept."),
            CliWatchStatus.ReportedFailure =>
                ConsumerCopy.L(lang,
                    actor + " 已交回" + zhTask + "的失败或阻碍报告，待 Lead；这不等于进程异常退出。",
                    actor + " returned a failure or blocker report for " + enTask + "; waiting on Lead. This does not imply a process crash."),
            CliWatchStatus.ExecutorFinished =>
                ConsumerCopy.L(lang,
                    actor + " 已结束" + zhTask + "，未见交付。本窗不验收。",
                    actor + " finished " + enTask + "; no delivery seen. This window does not accept."),
            CliWatchStatus.ProcessGone =>
                ConsumerCopy.L(lang,
                    "当前未见 " + actor + " 的原执行进程，也未见" + zhTask + "的终态或交卷记录。退出原因未知。",
                    "The original " + actor + " process is absent, with no terminal receipt or delivery for " + enTask + ". Cause is unknown."),
            CliWatchStatus.TransportFailed =>
                ConsumerCopy.L(lang,
                    actor + " 的执行交付链报告异常，" + zhTask + "的业务结果尚未核实，待原 Lead 处理。",
                    actor + " has an execution hand-back failure; the business outcome of " + enTask + " is unverified and awaiting the Lead."),
            CliWatchStatus.Failed =>
                ConsumerCopy.L(lang,
                    actor + " 对" + zhTask + "明确失败或异常退出。本窗不重试。",
                    actor + " failed or exited abnormally on " + enTask + ". This window does not retry."),
            CliWatchStatus.Retired =>
                ConsumerCopy.L(lang,
                    zhTask + "已退役或已被替代。不是完成、失败或验收。",
                    enTask + " is retired or superseded. That is not completion, failure, or acceptance."),
            CliWatchStatus.StartUnconfirmed =>
                ConsumerCopy.L(lang,
                    "包装器在，尚未证实 " + actor + " 已启动" + zhTask + "。包装器存活不等于 CLI 存活。",
                    "Wrapper is alive; " + actor + " is not confirmed started on " + enTask + ". Wrapper alive is not CLI alive."),
            CliWatchStatus.Registered =>
                ConsumerCopy.L(lang,
                    zhTask + "已登记，尚未证实 " + actor + " 启动。本窗只观察。",
                    enTask + " is registered; " + actor + " start is not confirmed. This window only observes."),
            CliWatchStatus.Unbound =>
                ConsumerCopy.L(lang,
                    actor + " 是未关联任务，不猜测所属项目。",
                    actor + " is unbound; project is not guessed."),
            _ =>
                ConsumerCopy.L(lang,
                    actor + " · " + zhTask + " 状态未知。本窗只观察。",
                    actor + " · " + enTask + " status is unknown. This window only observes.")
        };
    }

    static bool HasUnverifiedAcceptance(ProjectView project, WorkView? work = null)
    {
        if (ContainsAcceptance(project.Summary) || ContainsAcceptance(project.NextStep))
            return true;
        if (work is not null && (ContainsAcceptance(work.Summary) || ContainsAcceptance(work.Blocker)))
            return true;
        return project.WorkItems.Any(item => ContainsAcceptance(item.Summary) || ContainsAcceptance(item.Blocker));
    }

    static bool ContainsAcceptance(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Contains(CliWatchStatus.AcceptanceUnverifiedZh, StringComparison.Ordinal);
}
