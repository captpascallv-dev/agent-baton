using System.Diagnostics;

namespace CodexUsageHud.Core;

public enum GrokRenewalKind
{
    NotNeeded,
    NotRenewable,
    Refreshed,
    Unchanged,
    TransientFailure,
    Timeout,
    BinaryMissing,
    Revoked,
}

public sealed record GrokRenewalOutcome(GrokRenewalKind Kind, bool Attempted)
{
    public static GrokRenewalOutcome NotNeeded { get; } = new(GrokRenewalKind.NotNeeded, false);
    public static GrokRenewalOutcome NotRenewable { get; } = new(GrokRenewalKind.NotRenewable, false);
    public static GrokRenewalOutcome BinaryMissing { get; } = new(GrokRenewalKind.BinaryMissing, false);
}

public interface IGrokSessionRenewer
{
    Task<GrokRenewalKind> RefreshSessionAsync(CancellationToken cancellationToken);
}

public sealed class GrokCliModelsRenewer : IGrokSessionRenewer
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private readonly string _executable;
    private readonly TimeSpan _timeout;

    public GrokCliModelsRenewer(string executable, TimeSpan? timeout = null)
    {
        _executable = executable;
        _timeout = timeout ?? DefaultTimeout;
    }

    public static GrokCliModelsRenewer? TryFromAuthFile(string authPath, TimeSpan? timeout = null)
    {
        var executable = ResolveExecutable(authPath);
        return executable is null ? null : new GrokCliModelsRenewer(executable, timeout);
    }

    public static string? ResolveExecutable(string? authPath = null, string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configured = Environment.GetEnvironmentVariable("GROK_HOME");
        var grokHome = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(userProfile, ".grok")
            : Path.GetFullPath(configured);

        if (!string.IsNullOrWhiteSpace(authPath))
        {
            var home = Path.GetDirectoryName(Path.GetFullPath(authPath));
            if (string.IsNullOrWhiteSpace(home)) return null;
            var nested = Path.Combine(home, "bin", "grok.exe");
            if (File.Exists(nested)) return nested;
            return SamePath(home, grokHome) ? ExistingGrokBinary(grokHome) : null;
        }

        return ExistingGrokBinary(grokHome);
    }

    public async Task<GrokRenewalKind> RefreshSessionAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_executable)) return GrokRenewalKind.BinaryMissing;

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = _executable,
            Arguments = "models",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetTempPath(),
        };
        try
        {
            if (!process.Start()) return GrokRenewalKind.TransientFailure;
        }
        catch (Exception)
        {
            return GrokRenewalKind.TransientFailure;
        }

        try { process.StandardInput.Close(); }
        catch (Exception) { }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await DrainAsync(stdout, stderr).ConfigureAwait(false);
            return process.ExitCode == 0 ? GrokRenewalKind.Refreshed : GrokRenewalKind.TransientFailure;
        }
        catch (OperationCanceledException)
        {
            TryStop(process);
            await DrainAsync(stdout, stderr).ConfigureAwait(false);
            return GrokRenewalKind.Timeout;
        }
    }

    private static string? ExistingGrokBinary(string grokHome)
    {
        var candidate = Path.Combine(grokHome, "bin", "grok.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static bool SamePath(string left, string right)
    {
        var a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DrainAsync(Task<string> stdout, Task<string> stderr)
    {
        try { await stdout.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception) { }
        try { await stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }
    }
}
