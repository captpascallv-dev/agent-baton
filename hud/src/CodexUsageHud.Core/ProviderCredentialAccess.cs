using System.Text;
using Microsoft.Data.Sqlite;

namespace CodexUsageHud.Core;

public sealed record LoginPresence(
    string ProviderId,
    bool Present,
    string RelativeHint,
    DateTimeOffset? LastWriteUtc,
    long? Length);

public static class WindowsLoginPresence
{
    public static LoginPresence Cursor(string? appData = null)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");
        return InspectFile(ProviderIds.Cursor, path, @"%APPDATA%\Cursor\User\globalStorage\state.vscdb");
    }

    public static LoginPresence Grok(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(userProfile, ".grok");
        return InspectDirectory(ProviderIds.Grok, path, @"%USERPROFILE%\.grok");
    }

    public static LoginPresence GrokAuthFile(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(userProfile, ".grok", "auth.json");
        return InspectFile(ProviderIds.Grok, path, @"%USERPROFILE%\.grok\auth-file");
    }

    public static LoginPresence GrokBot(string? appData = null)
    {
        var cursor = Cursor(appData);
        return cursor with
        {
            ProviderId = ProviderIds.GrokBot,
            RelativeHint = @"%APPDATA%\Cursor\User\globalStorage\state.vscdb（Grok Bot 走 Cursor 登录）",
        };
    }

    public static LoginPresence PiCodexAuthFile(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(userProfile, ".pi", "agent", "auth.json");
        return InspectFile(ProviderIds.Codex, path, @"%USERPROFILE%\.pi\agent\auth-file");
    }

    public static LoginPresence CodexCliAuthFile(string? codexHome)
    {
        if (string.IsNullOrWhiteSpace(codexHome))
            return new LoginPresence(ProviderIds.Codex, false, @"CODEX_HOME\auth-file", null, null);
        return InspectFile(ProviderIds.Codex, Path.Combine(codexHome, "auth.json"), @"CODEX_HOME\auth-file");
    }

    public static LoginPresence ClaudeCredentialsFile(string? userProfile = null, string? configDir = null)
    {
        var configured = configDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var hint = string.IsNullOrWhiteSpace(configured)
            ? @"%USERPROFILE%\.claude\.credentials.json"
            : @"%CLAUDE_CONFIG_DIR%\.credentials.json";
        return InspectFile(ProviderIds.Claude, ClaudeCredentialPaths.Resolve(userProfile, configured), hint);
    }

    public static LoginPresence ClaudeDesktopProfile(string? roamingAppData = null, string? localAppData = null)
    {
        var resolution = ClaudeDesktopProfileResolution.Resolve(roamingAppData, localAppData);
        if (resolution.AccountAmbiguous)
        {
            return new LoginPresence(ProviderIds.Claude, true, @"多个 Claude Desktop 配置，未选用", null, null);
        }

        if (string.IsNullOrWhiteSpace(resolution.Directory))
            return new LoginPresence(ProviderIds.Claude, false, @"%APPDATA%\Claude", null, null);

        var hint = resolution.ProfileKind == "msix"
            ? @"%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\config.json"
            : @"%APPDATA%\Claude\config.json";
        return InspectFile(ProviderIds.Claude, Path.Combine(resolution.Directory, "config.json"), hint);
    }

    private static LoginPresence InspectFile(string providerId, string path, string hint)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new LoginPresence(providerId, false, hint, null, null);
            return new LoginPresence(providerId, true, hint, new DateTimeOffset(info.LastWriteTimeUtc),
                info.Length);
        }
        catch (IOException)
        {
            return new LoginPresence(providerId, false, hint, null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new LoginPresence(providerId, false, hint, null, null);
        }
    }

    private static LoginPresence InspectDirectory(string providerId, string path, string hint)
    {
        try
        {
            if (!Directory.Exists(path)) return new LoginPresence(providerId, false, hint, null, null);
            var info = new DirectoryInfo(path);
            return new LoginPresence(providerId, true, hint, new DateTimeOffset(info.LastWriteTimeUtc), null);
        }
        catch (IOException)
        {
            return new LoginPresence(providerId, false, hint, null, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new LoginPresence(providerId, false, hint, null, null);
        }
    }
}

public interface ICursorTokenSource
{
    string? ReadAccessToken();
}

public interface IGrokTokenSource
{
    string? ReadAccessToken();
    GrokLoginInspection InspectLogin();
    Task<GrokRenewalOutcome> EnsureFreshAsync(bool force, CancellationToken cancellationToken);
}

public interface IPiCodexTokenSource
{
    PiCodexCredential? ReadCredential();
    PiCodexLoginInspection InspectLogin();
    string ConfigurationFingerprint();
}

public interface IClaudeTokenSource
{
    string? ReadAccessToken();
    ClaudeLoginInspection InspectLogin();
}

public static class ClaudeCredentialPaths
{
    public static string Resolve(string? userProfile = null, string? configDir = null)
    {
        if (!string.IsNullOrWhiteSpace(configDir))
            return Path.Combine(configDir.Trim(), ".credentials.json");
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude", ".credentials.json");
    }
}

public sealed record ClaudeLoginInspection(
    bool FilePresent,
    bool Recognized,
    bool HasAccess,
    bool HasExpiryField,
    bool Expired,
    bool Usable,
    string FormatKind)
{
    public static ClaudeLoginInspection Missing { get; } =
        new(false, false, false, false, false, false, "absent");

    public static ClaudeLoginInspection Injected(bool usable) =>
        new(true, usable, usable, false, false, usable, usable ? "injected" : "absent")
        {
            SourceKind = "injected",
        };

    public string SourceKind { get; init; } = "claude_code_file";

    public bool AccountAmbiguous { get; init; }

    public bool DecryptFailed { get; init; }

    public string AmbiguousReason { get; init; } = "none";

    public string Format() =>
        "file_present=" + (FilePresent ? "1" : "0") +
        " recognized=" + (Recognized ? "1" : "0") +
        " has_access=" + (HasAccess ? "1" : "0") +
        " has_expiry=" + (HasExpiryField ? "1" : "0") +
        " expired=" + (Expired ? "1" : "0") +
        " usable=" + (Usable ? "1" : "0") +
        " format_kind=" + SanitizeLabel(FormatKind) +
        " source_kind=" + SanitizeLabel(SourceKind) +
        " ambiguous=" + (AccountAmbiguous ? "1" : "0") +
        " ambiguous_reason=" + SanitizeLabel(AmbiguousReason) +
        " decrypt_failed=" + (DecryptFailed ? "1" : "0");

    private static string SanitizeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40) return "redacted";
        foreach (var character in value)
        {
            if (character is not '_' && !char.IsAsciiLetterOrDigit(character)) return "redacted";
        }

        return value;
    }
}

public sealed class InjectedTokenSource : ICursorTokenSource, IGrokTokenSource
{
    private readonly string? _token;
    public InjectedTokenSource(string? token) => _token = token;
    public string? ReadAccessToken() => _token;
    public GrokLoginInspection InspectLogin() =>
        GrokLoginInspection.Injected(!string.IsNullOrWhiteSpace(_token));
    public Task<GrokRenewalOutcome> EnsureFreshAsync(bool force, CancellationToken cancellationToken) =>
        Task.FromResult(string.IsNullOrWhiteSpace(_token)
            ? GrokRenewalOutcome.NotRenewable
            : GrokRenewalOutcome.NotNeeded);
}

public sealed class InjectedPiCodexTokenSource : IPiCodexTokenSource
{
    private readonly PiCodexCredential? _credential;
    private readonly string _fingerprint;
    private readonly PiCodexLoginInspection _inspection;

    public InjectedPiCodexTokenSource(PiCodexCredential? credential, string fingerprint = "injected",
        PiCodexLoginInspection? inspection = null)
    {
        _credential = credential;
        _fingerprint = fingerprint;
        _inspection = inspection ?? PiCodexLoginInspection.Injected(credential is not null);
    }

    public PiCodexCredential? ReadCredential() => _credential;
    public PiCodexLoginInspection InspectLogin() => _inspection;
    public string ConfigurationFingerprint() => _fingerprint;
}

public sealed class CursorStateTokenSource : ICursorTokenSource
{
    private readonly string _databasePath;

    public CursorStateTokenSource(string databasePath)
    {
        _databasePath = databasePath;
    }

    public string? ReadAccessToken()
    {
        if (!File.Exists(_databasePath)) return null;
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 2,
                Pooling = false,
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1;";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) return null;
            if (reader.GetFieldType(0) == typeof(byte[]))
            {
                var bytes = (byte[])reader.GetValue(0);
                var text = DecodePossiblyUtf16(bytes);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            var value = reader.GetString(0);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (SqliteException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DecodePossiblyUtf16(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes.Length % 2 == 0)
        {
            var utf16 = Encoding.Unicode.GetString(bytes).Trim('\0');
            if (utf16.StartsWith("eyJ", StringComparison.Ordinal) || utf16.Contains('.', StringComparison.Ordinal))
                return utf16;
        }

        return Encoding.UTF8.GetString(bytes).Trim('\0');
    }
}

public sealed class GrokAuthFileTokenSource : IGrokTokenSource
{
    private readonly string _path;
    private readonly IGrokSessionRenewer? _renewer;
    private readonly object _gate = new();
    private Task<GrokRenewalOutcome>? _inFlight;

    public GrokAuthFileTokenSource(string path, IGrokSessionRenewer? renewer = null)
    {
        _path = path;
        _renewer = renewer ?? GrokCliModelsRenewer.TryFromAuthFile(path);
    }

    public string? ReadAccessToken()
    {
        var inspection = InspectLogin();
        if (!inspection.Usable) return null;
        if (!File.Exists(_path)) return null;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            return GrokAuthTokenParser.Extract(json);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public GrokLoginInspection InspectLogin()
    {
        if (!File.Exists(_path)) return GrokLoginInspection.Missing;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var parsed = GrokAuthTokenParser.Inspect(json);
            return new GrokLoginInspection(true, parsed.RecognizedRootToken || parsed.RecognizedIssuerEntries,
                parsed.HasExpiryField, parsed.Expired, parsed.Usable, parsed.HasRefreshToken, parsed.FormatKind);
        }
        catch (IOException)
        {
            return new GrokLoginInspection(true, false, false, false, false, false, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new GrokLoginInspection(true, false, false, false, false, false, "unreadable");
        }
    }

    public Task<GrokRenewalOutcome> EnsureFreshAsync(bool force, CancellationToken cancellationToken)
    {
        var inspection = InspectLogin();
        if (inspection.Usable && !force) return Task.FromResult(GrokRenewalOutcome.NotNeeded);
        if (!inspection.HasRefreshToken) return Task.FromResult(GrokRenewalOutcome.NotRenewable);
        if (_renewer is null) return Task.FromResult(GrokRenewalOutcome.BinaryMissing);

        Task<GrokRenewalOutcome> pending;
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false }) pending = _inFlight;
            else
            {
                pending = RenewCoreAsync(cancellationToken);
                _inFlight = pending;
            }
        }

        return AwaitAndClearAsync(pending);
    }

    private async Task<GrokRenewalOutcome> AwaitAndClearAsync(Task<GrokRenewalOutcome> pending)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, pending)) _inFlight = null;
            }
        }
    }

    private async Task<GrokRenewalOutcome> RenewCoreAsync(CancellationToken cancellationToken)
    {
        GrokRenewalKind kind;
        try
        {
            kind = await _renewer!.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            kind = GrokRenewalKind.Timeout;
        }
        catch (Exception)
        {
            kind = GrokRenewalKind.TransientFailure;
        }

        var after = InspectLogin();
        if (!after.Usable && kind == GrokRenewalKind.Refreshed)
        {
            try { await Task.Delay(200, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                return new GrokRenewalOutcome(GrokRenewalKind.Timeout, true);
            }

            after = InspectLogin();
        }

        if (after.Usable) return new GrokRenewalOutcome(GrokRenewalKind.Refreshed, true);
        if (!after.HasRefreshToken && after.Expired)
            return new GrokRenewalOutcome(GrokRenewalKind.Revoked, true);
        if (kind == GrokRenewalKind.Refreshed)
            return new GrokRenewalOutcome(GrokRenewalKind.Unchanged, true);
        return new GrokRenewalOutcome(kind, true);
    }
}

public static class DefaultTokenSources
{
    public static ICursorTokenSource Cursor(string? appData = null)
    {
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new CursorStateTokenSource(Path.Combine(appData, "Cursor", "User", "globalStorage",
            "state.vscdb"));
    }

    public static IGrokTokenSource Grok(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new GrokAuthFileTokenSource(Path.Combine(userProfile, ".grok", "auth.json"));
    }

    public static IPiCodexTokenSource PiCodex(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new PiCodexAuthFileTokenSource(Path.Combine(userProfile, ".pi", "agent", "auth.json"));
    }

    public static IClaudeTokenSource Claude(string? userProfile = null, string? configDir = null,
        string? roamingAppData = null, string? localAppData = null) =>
        new ClaudeLoginFallbackTokenSource(
            new ClaudeCodeCredentialTokenSource(ClaudeCredentialPaths.Resolve(userProfile,
                configDir ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"))),
            ClaudeDesktopTokenSource.FromProfileRoots(roamingAppData, localAppData));
}

public sealed class InjectedClaudeTokenSource : IClaudeTokenSource
{
    private readonly string? _token;
    private readonly ClaudeLoginInspection _inspection;
    public int ReadCalls;
    public int InspectCalls;

    public InjectedClaudeTokenSource(string? token, ClaudeLoginInspection? inspection = null)
    {
        _token = token;
        _inspection = inspection ?? ClaudeLoginInspection.Injected(!string.IsNullOrWhiteSpace(token));
    }

    public string? ReadAccessToken()
    {
        ReadCalls++;
        return _token;
    }

    public ClaudeLoginInspection InspectLogin()
    {
        InspectCalls++;
        return _inspection;
    }
}

public sealed class ClaudeCodeCredentialTokenSource : IClaudeTokenSource
{
    private readonly string _path;

    public ClaudeCodeCredentialTokenSource(string path)
    {
        _path = path;
    }

    public string? ReadAccessToken()
    {
        var inspection = InspectLogin();
        if (!inspection.Usable) return null;
        if (!File.Exists(_path)) return null;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            return ClaudeOAuthParser.ExtractAccessToken(json);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public ClaudeLoginInspection InspectLogin()
    {
        if (!File.Exists(_path)) return ClaudeLoginInspection.Missing;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var parsed = ClaudeOAuthParser.Inspect(json);
            return new ClaudeLoginInspection(true, parsed.Recognized, parsed.HasAccess, parsed.HasExpiryField,
                parsed.Expired, parsed.Usable, parsed.FormatKind);
        }
        catch (IOException)
        {
            return new ClaudeLoginInspection(true, false, false, false, false, false, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new ClaudeLoginInspection(true, false, false, false, false, false, "unreadable");
        }
    }
}

public sealed class PiCodexAuthFileTokenSource : IPiCodexTokenSource
{
    private readonly string _path;

    public PiCodexAuthFileTokenSource(string path)
    {
        _path = path;
    }

    public PiCodexCredential? ReadCredential()
    {
        var inspection = InspectLogin();
        if (!inspection.Usable) return null;
        if (!File.Exists(_path)) return null;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            return PiCodexAuthParser.Extract(json);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public PiCodexLoginInspection InspectLogin()
    {
        if (!File.Exists(_path)) return PiCodexLoginInspection.Missing;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            var parsed = PiCodexAuthParser.Inspect(json);
            return new PiCodexLoginInspection(true, parsed.HasOpenAiCodexKey, parsed.TypeOauth, parsed.HasAccess,
                parsed.HasAccountId, parsed.HasExpiryField, parsed.Expired, parsed.Usable, parsed.FormatKind);
        }
        catch (IOException)
        {
            return new PiCodexLoginInspection(true, false, false, false, false, false, false, false, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new PiCodexLoginInspection(true, false, false, false, false, false, false, false, "unreadable");
        }
    }

    public string ConfigurationFingerprint()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return "0";
            return info.LastWriteTimeUtc.Ticks.ToString() + ":" + info.Length.ToString();
        }
        catch (IOException)
        {
            return "0";
        }
        catch (UnauthorizedAccessException)
        {
            return "0";
        }
    }
}
