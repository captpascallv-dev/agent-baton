using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexUsageHud.Core;

public sealed record ClaudeDesktopProfileResolution(
    string? Directory,
    string ProfileKind,
    bool AccountAmbiguous)
{
    public static ClaudeDesktopProfileResolution Found(string directory, string profileKind) =>
        new(directory, profileKind, false);

    public static ClaudeDesktopProfileResolution None() => new(null, "absent", false);

    public static ClaudeDesktopProfileResolution Ambiguous() => new(null, "ambiguous", true);

    public static ClaudeDesktopProfileResolution Resolve(string? roamingAppData = null, string? localAppData = null)
    {
        roamingAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roaming = Path.Combine(roamingAppData, "Claude");
        if (HasCredentialPair(roaming))
            return Found(roaming, "roaming");

        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(localAppData, "Packages");
        string? match = null;
        try
        {
            if (!System.IO.Directory.Exists(packages))
                return None();

            foreach (var packageDir in System.IO.Directory.EnumerateDirectories(packages))
            {
                var name = Path.GetFileName(packageDir);
                if (name.Length < 8 || !name.StartsWith("Claude_", StringComparison.Ordinal))
                    continue;
                var candidate = Path.Combine(packageDir, "LocalCache", "Roaming", "Claude");
                if (!HasCredentialPair(candidate))
                    continue;
                if (match is not null)
                    return Ambiguous();
                match = candidate;
            }
        }
        catch (IOException)
        {
            return match is null ? None() : Found(match, "msix");
        }
        catch (UnauthorizedAccessException)
        {
            return match is null ? None() : Found(match, "msix");
        }

        return match is null ? None() : Found(match, "msix");
    }

    private static bool HasCredentialPair(string directory)
    {
        try
        {
            return File.Exists(Path.Combine(directory, "config.json")) &&
                   File.Exists(Path.Combine(directory, "Local State"));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed class ClaudeLoginFallbackTokenSource : IClaudeTokenSource
{
    private readonly IClaudeTokenSource _codeFile;
    private readonly IClaudeTokenSource _desktop;

    public ClaudeLoginFallbackTokenSource(IClaudeTokenSource codeFile, IClaudeTokenSource desktop)
    {
        _codeFile = codeFile;
        _desktop = desktop;
    }

    public ClaudeLoginInspection InspectLogin()
    {
        var primary = Inspect(_codeFile);
        if (primary.Usable)
            return primary;

        var desktop = Inspect(_desktop);
        if (desktop.Usable || desktop.AccountAmbiguous || desktop.DecryptFailed || desktop.FilePresent)
            return desktop;
        return primary;
    }

    public string? ReadAccessToken()
    {
        var inspection = InspectLogin();
        if (!inspection.Usable)
            return null;
        try
        {
            return string.Equals(inspection.SourceKind, "claude_desktop", StringComparison.Ordinal)
                ? _desktop.ReadAccessToken()
                : _codeFile.ReadAccessToken();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static ClaudeLoginInspection Inspect(IClaudeTokenSource source)
    {
        try
        {
            return source.InspectLogin();
        }
        catch (IOException)
        {
            return new ClaudeLoginInspection(true, false, false, false, false, false, "unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            return new ClaudeLoginInspection(true, false, false, false, false, false, "unreadable");
        }
        catch (CryptographicException)
        {
            return new ClaudeLoginInspection(true, false, false, false, false, false, "decrypt_failed")
            {
                DecryptFailed = true,
            };
        }
    }
}

public sealed class ClaudeDesktopTokenSource : IClaudeTokenSource
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxPlaintextBytes = 256 * 1024;
    private const int MaxCacheEntries = 24;
    private const string Audience = "https://api.anthropic.com";

    private readonly ClaudeDesktopProfileResolution _profile;

    public ClaudeDesktopTokenSource(ClaudeDesktopProfileResolution profile)
    {
        _profile = profile;
    }

    public ClaudeDesktopTokenSource(string directory)
        : this(ClaudeDesktopProfileResolution.Found(directory, "directory"))
    {
    }

    public static ClaudeDesktopTokenSource FromProfileRoots(string? roamingAppData = null, string? localAppData = null) =>
        new(ClaudeDesktopProfileResolution.Resolve(roamingAppData, localAppData));

    public string? ReadAccessToken()
    {
        var read = ReadCore();
        return read.Inspection.Usable ? read.AccessToken : null;
    }

    public ClaudeLoginInspection InspectLogin() => ReadCore().Inspection;

    private DesktopRead ReadCore()
    {
        if (_profile.AccountAmbiguous)
        {
            return DesktopRead.WithoutToken(new ClaudeLoginInspection(true, false, false, false, false, false, "ambiguous")
            {
                SourceKind = "claude_desktop",
                AccountAmbiguous = true,
                AmbiguousReason = "multiple_profiles",
            });
        }

        if (string.IsNullOrWhiteSpace(_profile.Directory))
            return DesktopRead.WithoutToken(ClaudeLoginInspection.Missing with { SourceKind = "claude_desktop" });

        var configPath = Path.Combine(_profile.Directory, "config.json");
        var statePath = Path.Combine(_profile.Directory, "Local State");
        if (!File.Exists(configPath) || !File.Exists(statePath))
            return DesktopRead.WithoutToken(ClaudeLoginInspection.Missing with { SourceKind = "claude_desktop" });

        string? configText;
        byte[]? key;
        try
        {
            configText = ReadBounded(configPath);
            key = ReadOsCryptKey(statePath);
        }
        catch (IOException)
        {
            return Unreadable();
        }
        catch (UnauthorizedAccessException)
        {
            return Unreadable();
        }
        catch (JsonException)
        {
            return DecryptFailed();
        }
        catch (FormatException)
        {
            return DecryptFailed();
        }
        catch (CryptographicException)
        {
            return DecryptFailed();
        }

        if (configText is null || key is null)
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            return Unreadable();
        }

        try
        {
            using var document = JsonDocument.Parse(configText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Unreadable();
            if (!TrySelectCache(document.RootElement, out var sealedValue))
            {
                return DesktopRead.WithoutToken(new ClaudeLoginInspection(true, false, false, false, false, false, "unreadable")
                {
                    SourceKind = "claude_desktop",
                });
            }

            var plaintext = DecryptOsCrypt(sealedValue, key);
            if (plaintext is null)
                return DecryptFailed();
            return ParseCache(plaintext);
        }
        catch (JsonException)
        {
            return Unreadable();
        }
        catch (FormatException)
        {
            return DecryptFailed();
        }
        catch (CryptographicException)
        {
            return DecryptFailed();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool TrySelectCache(JsonElement root, out string sealedValue)
    {
        sealedValue = "";
        if (root.TryGetProperty("oauth:tokenCacheV2", out var current))
        {
            if (current.ValueKind != JsonValueKind.String)
                return false;
            sealedValue = current.GetString() ?? "";
            return sealedValue.Length > 0;
        }

        if (!root.TryGetProperty("oauth:tokenCache", out var legacy) || legacy.ValueKind != JsonValueKind.String)
            return false;
        sealedValue = legacy.GetString() ?? "";
        return sealedValue.Length > 0;
    }

    private DesktopRead ParseCache(string plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Unreadable();

            var candidates = new List<CacheCandidate>(4);
            var missingAccount = false;
            var missingExpiry = false;
            var count = 0;
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                count++;
                if (count > MaxCacheEntries)
                    return Ambiguous("too_many_entries");
                if (!entry.Name.Contains(Audience, StringComparison.Ordinal) ||
                    entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryReadAccess(entry.Value, out var access, out var conflict))
                {
                    if (conflict)
                        return Ambiguous("token_conflict");
                    continue;
                }

                if (!TryAccountId(entry.Name, out var accountId))
                {
                    missingAccount = true;
                    continue;
                }

                if (!TryExpiry(entry.Value, out var expiresAt))
                {
                    missingExpiry = true;
                    continue;
                }

                candidates.Add(new CacheCandidate(accountId, access, expiresAt));
            }

            if (missingAccount)
                return Ambiguous("missing_account");
            if (missingExpiry && candidates.Count > 0)
                return Ambiguous("missing_expiry");
            if (candidates.Count == 0)
            {
                return DesktopRead.WithoutToken(new ClaudeLoginInspection(
                    true, true, missingExpiry, false, false, false, "claude_desktop_oauth")
                {
                    SourceKind = "claude_desktop",
                });
            }

            var accounts = new HashSet<string>(candidates.Select(candidate => candidate.AccountId), StringComparer.Ordinal);
            if (accounts.Count != 1)
                return Ambiguous("multiple_accounts");

            var now = DateTimeOffset.UtcNow;
            CacheCandidate? selected = null;
            var sawExpiry = false;
            var tied = false;
            foreach (var candidate in candidates)
            {
                sawExpiry = true;
                if (candidate.ExpiresAt <= now)
                    continue;
                if (selected is null || candidate.ExpiresAt > selected.Value.ExpiresAt)
                {
                    selected = candidate;
                    tied = false;
                    continue;
                }

                if (candidate.ExpiresAt == selected.Value.ExpiresAt &&
                    !string.Equals(candidate.AccessToken, selected.Value.AccessToken, StringComparison.Ordinal))
                {
                    tied = true;
                }
            }

            if (selected is { } current && !tied)
            {
                return new DesktopRead(current.AccessToken, new ClaudeLoginInspection(true, true, true, true, false, true, "claude_desktop_oauth")
                {
                    SourceKind = "claude_desktop",
                });
            }

            if (sawExpiry && selected is null)
            {
                return DesktopRead.WithoutToken(new ClaudeLoginInspection(true, true, true, true, true, false, "claude_desktop_oauth")
                {
                    SourceKind = "claude_desktop",
                });
            }

            return Ambiguous("multiple_tokens");
        }
        catch (JsonException)
        {
            return Unreadable();
        }
    }

    private static bool TryReadAccess(JsonElement value, out string access, out bool conflict)
    {
        access = "";
        conflict = false;
        var token = ReadString(value, "token");
        var alternate = ReadString(value, "accessToken");
        if (token is not null && alternate is not null && !string.Equals(token, alternate, StringComparison.Ordinal))
        {
            conflict = true;
            return false;
        }

        var chosen = token ?? alternate;
        if (chosen is null || chosen.Length is < 1 or > ClaudeOAuthParser.MaxAccessTokenChars)
            return false;
        foreach (var character in chosen)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
                return false;
        }

        access = chosen;
        return true;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryAccountId(string key, out string accountId)
    {
        accountId = "";
        var colon = key.IndexOf(':');
        if (colon <= 0)
            return false;
        if (!key.Contains(Audience, StringComparison.Ordinal))
            return false;
        var account = key[..colon];
        if (account.Length is < 1 or > 200)
            return false;
        foreach (var character in account)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
                return false;
        }

        accountId = account;
        return true;
    }

    private static bool TryExpiry(JsonElement value, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (!value.TryGetProperty("expiresAt", out var element) &&
            !value.TryGetProperty("expires_at", out element))
        {
            return false;
        }

        var parsed = JsonQuotaFields.ReadDate(element);
        if (!parsed.HasValue)
            return false;
        expiresAt = parsed.Value;
        return true;
    }

    private static byte[] ReadOsCryptKey(string localStatePath)
    {
        var text = ReadBounded(localStatePath) ?? throw new CryptographicException("local_state_unreadable");
        using var document = JsonDocument.Parse(text);
        if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
            osCrypt.ValueKind != JsonValueKind.Object ||
            !osCrypt.TryGetProperty("encrypted_key", out var encoded) ||
            encoded.ValueKind != JsonValueKind.String)
        {
            throw new CryptographicException("os_crypt_missing");
        }

        var blob = Convert.FromBase64String(encoded.GetString() ?? "");
        var prefix = "DPAPI"u8;
        if (blob.Length <= prefix.Length || !blob.AsSpan(0, prefix.Length).SequenceEqual(prefix))
            throw new CryptographicException("os_crypt_prefix");
        if (!OperatingSystem.IsWindows())
            throw new CryptographicException("dpapi_unavailable");

        var key = ProtectedData.Unprotect(blob.AsSpan(prefix.Length).ToArray(), null, DataProtectionScope.CurrentUser);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("os_crypt_key_length");
        }

        return key;
    }

    private static string? DecryptOsCrypt(string sealedValue, byte[] key)
    {
        var blob = Convert.FromBase64String(sealedValue);
        const int prefixLength = 3;
        const int nonceLength = 12;
        const int tagLength = 16;
        if (blob.Length <= prefixLength + nonceLength + tagLength || blob.Length > MaxPlaintextBytes + prefixLength + nonceLength + tagLength)
            return null;
        var prefix = Encoding.ASCII.GetString(blob, 0, prefixLength);
        if (prefix is not ("v10" or "v11"))
            return null;

        var cipherLength = blob.Length - prefixLength - nonceLength - tagLength;
        if (cipherLength is < 2 or > MaxPlaintextBytes)
            return null;
        var plaintext = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, tagLength);
            aes.Decrypt(
                blob.AsSpan(prefixLength, nonceLength),
                blob.AsSpan(prefixLength + nonceLength, cipherLength),
                blob.AsSpan(blob.Length - tagLength, tagLength),
                plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string? ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > MaxFileBytes)
            return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static DesktopRead Ambiguous(string reason) =>
        DesktopRead.WithoutToken(AmbiguousInspection(reason));

    private static ClaudeLoginInspection AmbiguousInspection(string reason) =>
        new(true, true, false, false, false, false, "ambiguous")
        {
            SourceKind = "claude_desktop",
            AccountAmbiguous = true,
            AmbiguousReason = reason,
        };

    private static DesktopRead DecryptFailed() =>
        DesktopRead.WithoutToken(new ClaudeLoginInspection(true, false, false, false, false, false, "decrypt_failed")
        {
            SourceKind = "claude_desktop",
            DecryptFailed = true,
        });

    private static DesktopRead Unreadable() =>
        DesktopRead.WithoutToken(new ClaudeLoginInspection(true, false, false, false, false, false, "unreadable")
        {
            SourceKind = "claude_desktop",
        });

    private readonly record struct CacheCandidate(string AccountId, string AccessToken, DateTimeOffset ExpiresAt);

    private readonly record struct DesktopRead(string? AccessToken, ClaudeLoginInspection Inspection)
    {
        public static DesktopRead WithoutToken(ClaudeLoginInspection inspection) => new(null, inspection);
    }
}
