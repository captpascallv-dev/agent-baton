// SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;

namespace CliWatch.Normalization;

internal static class JsonField
{
    public static string? Str(JsonObject? o, params string[] keys)
    {
        if (o is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (o[key] is JsonValue v)
            {
                if (v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    return s.Trim();
                }

                if (v.TryGetValue<bool>(out var b))
                {
                    return b ? "true" : "false";
                }

                if (v.TryGetValue<long>(out var l))
                {
                    return l.ToString(CultureInfo.InvariantCulture);
                }

                if (v.TryGetValue<int>(out var i))
                {
                    return i.ToString(CultureInfo.InvariantCulture);
                }

                if (v.TryGetValue<double>(out var d))
                {
                    return d.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return null;
    }

    public static bool? Bool(JsonObject? o, params string[] keys)
    {
        if (o is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (o[key] is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b))
                {
                    return b;
                }

                if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    public static int? Int(JsonObject? o, params string[] keys)
    {
        if (o is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (o[key] is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i))
                {
                    return i;
                }

                if (v.TryGetValue<long>(out var l) && l is >= int.MinValue and <= int.MaxValue)
                {
                    return (int)l;
                }

                if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    public static long? Long(JsonObject? o, params string[] keys)
    {
        if (o is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (o[key] is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l))
                {
                    return l;
                }

                if (v.TryGetValue<int>(out var i))
                {
                    return i;
                }

                if (v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    public static JsonObject? Obj(JsonObject? o, params string[] keys)
    {
        if (o is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (o[key] is JsonObject child)
            {
                return child;
            }
        }

        return null;
    }

    public static JsonArray? Arr(JsonObject? o, params string[] keys)
    {
        if (o is null)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (o[key] is JsonArray a)
            {
                return a;
            }
        }

        return null;
    }

    public static string AxisOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

    public static string BoolAxis(bool? value, string whenTrue, string whenFalse) =>
        value is null ? "unknown" : value.Value ? whenTrue : whenFalse;
}
