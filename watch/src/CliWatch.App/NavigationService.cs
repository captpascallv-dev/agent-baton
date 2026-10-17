// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Diagnostics;
using CliWatch.Contracts;

namespace CliWatch.App;

public sealed class NavigationOutcome
{
    public bool Succeeded { get; init; }
    public bool Rejected { get; init; }
    public bool Unsupported { get; init; }
    public string Message { get; init; } = "";
    public string? CopyText { get; init; }

    public static NavigationOutcome Ok(string message) => new() { Succeeded = true, Message = message };
    public static NavigationOutcome Deny(string message) => new() { Rejected = true, Message = message };
    public static NavigationOutcome NotSupported(string message, string? copy = null) =>
        new() { Unsupported = true, Message = message, CopyText = copy };
}

/// <summary>
/// Open only NavigationTarget already bound on the current snapshot, and only
/// after an explicit user click. File kinds use Process.Start + UseShellExecute.
/// Unsupported Codex task open: clear message + copyable ID, never fake success.
/// </summary>
public sealed class NavigationService
{
    public NavigationOutcome TryNavigate(CockpitSnapshot? snapshot, NavigationTarget? target)
    {
        if (target is null)
            return NavigationOutcome.Deny("没有导航目标。");
        if (!NavigationGuard.IsAllowed(snapshot, target))
            return NavigationOutcome.Deny("目标不在当前快照已绑定实体上，已拒绝。");
        if (NavigationGuard.LooksLikeUnsupportedUrl(target))
            return NavigationOutcome.Deny("拒绝未知 URL 协议或不一致对象。");

        if (NavigationGuard.IsFileKind(target.Kind))
            return OpenLocalFile(target.Target);

        if (NavigationGuard.IsCodexTaskKind(target.Kind))
        {
            return NavigationOutcome.NotSupported(
                "当前不支持直接打开该 Codex 任务。可复制原 ID，不要当作已打开。",
                target.Target);
        }

        return NavigationOutcome.NotSupported(
            "不支持的导航类型：" + target.Kind + "。可复制原目标。",
            target.Target);
    }

    static NavigationOutcome OpenLocalFile(string path)
    {
        var local = path;
        if (local.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { local = new Uri(local).LocalPath; }
            catch (UriFormatException)
            {
                return NavigationOutcome.Deny("本地文件路径无效，未打开。");
            }
        }

        if (!File.Exists(local) && !Directory.Exists(local))
            return NavigationOutcome.Deny("本地路径不存在，未打开：" + local);

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return NavigationOutcome.Deny("当前操作系统无法打开本地文件。");

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = local,
                UseShellExecute = true
            };
            var started = Process.Start(info);
            if (started is null && OperatingSystem.IsWindows())
                return NavigationOutcome.Deny("未能启动关联程序，未伪造成功。");
            return NavigationOutcome.Ok("已请求打开本地文件。");
        }
        catch (Exception ex)
        {
            return NavigationOutcome.Deny("打开本地文件失败：" + ex.Message);
        }
    }
}
