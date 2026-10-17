// SPDX-License-Identifier: MPL-2.0
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CliWatch.App;

public sealed class WindowPreferences
{
    public double Left { get; set; } = 64;
    public double Top { get; set; } = 64;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 480;
    public double ExpandedWidth { get; set; } = 920;
    public double ExpandedHeight { get; set; } = 640;
    public bool Topmost { get; set; }
    public bool Collapsed { get; set; } = true;
    public int Schema { get; set; } = 1;
    /// <summary>Display language code: "zh" or "en". Unknown/missing loads as "zh".</summary>
    public string Language { get; set; } = "zh";
}

/// <summary>
/// Persist window geometry / topmost / collapsed / display language. No credentials.
/// </summary>
public sealed class PreferenceStore
{
    public static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Left", "Top", "Width", "Height", "ExpandedWidth", "ExpandedHeight",
        "Topmost", "Collapsed", "Schema", "Language"
    };

    public static readonly HashSet<string> ForbiddenKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "refresh_token", "password", "cookie", "cookies",
        "authorization", "api_key", "apikey", "secret", "client_secret",
        "credential", "credentials", "auth", "session"
    };

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    readonly string _path;

    public PreferenceStore(string? path = null)
    {
        _path = path ?? ProductPaths.SettingsFile;
    }

    public string Path => _path;

    public WindowPreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new WindowPreferences();

            var json = File.ReadAllText(_path);
            return Parse(json);
        }
        catch (Exception)
        {
            return new WindowPreferences();
        }
    }

    public static WindowPreferences Parse(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException) { return new WindowPreferences(); }

        if (node is not JsonObject obj)
            return new WindowPreferences();

        var prefs = new WindowPreferences();
        ApplyNumber(obj, "Left", v => prefs.Left = v, prefs.Left);
        ApplyNumber(obj, "Top", v => prefs.Top = v, prefs.Top);
        ApplyNumber(obj, "Width", v => prefs.Width = Math.Max(160, v), prefs.Width);
        ApplyNumber(obj, "Height", v => prefs.Height = Math.Max(160, v), prefs.Height);
        ApplyNumber(obj, "ExpandedWidth", v => prefs.ExpandedWidth = Math.Max(320, v), prefs.ExpandedWidth);
        ApplyNumber(obj, "ExpandedHeight", v => prefs.ExpandedHeight = Math.Max(240, v), prefs.ExpandedHeight);
        if (obj["Topmost"] is JsonValue tm && tm.TryGetValue<bool>(out var topmost))
            prefs.Topmost = topmost;
        if (obj["Collapsed"] is JsonValue cl && cl.TryGetValue<bool>(out var collapsed))
            prefs.Collapsed = collapsed;
        if (obj["Schema"] is JsonValue sc && sc.TryGetValue<int>(out var schema))
            prefs.Schema = schema;
        if (obj["Language"] is JsonValue langNode && langNode.TryGetValue<string>(out var langRaw))
            prefs.Language = NormalizeLanguage(langRaw);
        else
            prefs.Language = "zh";
        return prefs;
    }

    public static string NormalizeLanguage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "zh";
        var t = raw.Trim();
        if (t.Equals("en", StringComparison.OrdinalIgnoreCase)
            || t.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            || t.Equals("en_us", StringComparison.OrdinalIgnoreCase)
            || t.Equals("english", StringComparison.OrdinalIgnoreCase))
        {
            return "en";
        }

        return "zh";
    }

    public void Save(WindowPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var payload = new Dictionary<string, object?>
        {
            ["Left"] = prefs.Left,
            ["Top"] = prefs.Top,
            ["Width"] = prefs.Width,
            ["Height"] = prefs.Height,
            ["ExpandedWidth"] = prefs.ExpandedWidth,
            ["ExpandedHeight"] = prefs.ExpandedHeight,
            ["Topmost"] = prefs.Topmost,
            ["Collapsed"] = prefs.Collapsed,
            ["Schema"] = prefs.Schema,
            ["Language"] = NormalizeLanguage(prefs.Language)
        };

        File.WriteAllText(_path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static IReadOnlyList<string> CollectKeys(string json)
    {
        var keys = new List<string>();
        if (JsonNode.Parse(json) is JsonObject obj)
        {
            foreach (var kv in obj)
                keys.Add(kv.Key);
        }
        return keys;
    }

    public static bool ContainsForbiddenKey(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (ForbiddenKeys.Contains(key))
                return true;
        }
        return false;
    }

    static void ApplyNumber(JsonObject obj, string key, Action<double> apply, double fallback)
    {
        if (obj[key] is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) { apply(d); return; }
            if (v.TryGetValue<int>(out var n)) { apply(n); return; }
        }
        apply(fallback);
    }
}
