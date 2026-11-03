using System.Diagnostics;

namespace CodexUsageHud.Core;

// Invoke only PI's own existing-login check. Never read or interpret its output.
public interface IPiCodexSessionRenewer
{
    Task<PiCodexRenewalResult> CheckAsync(CancellationToken cancellationToken);
}

public enum PiCodexRenewalResult { Ready, Failed, Timeout, BinaryMissing }

public sealed class PiCodexNativeRenewer : IPiCodexSessionRenewer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    public async Task<PiCodexRenewalResult> CheckAsync(CancellationToken cancellationToken)
    {
        // Fixed installed PI package and Node locations; no setting, PATH shell, or credential argument.
        var cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
        var node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "nodejs", "node.exe");
        if (!File.Exists(cli) || !File.Exists(node)) return PiCodexRenewalResult.BinaryMissing;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in new[] { cli, "auth", "check", "--provider", "openai-codex" })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) return PiCodexRenewalResult.Failed;
            process.StandardInput.Close(); // Never permit an interactive sign-in.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            // Drain bytes directly to a null sink, not strings/logs, so child pipes cannot block.
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                return process.ExitCode == 0 ? PiCodexRenewalResult.Ready : PiCodexRenewalResult.Failed;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return PiCodexRenewalResult.Timeout;
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return PiCodexRenewalResult.Timeout;
        }
        catch (Exception)
        {
            TryKill(process);
            return PiCodexRenewalResult.Failed;
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception) { }
    }
}
