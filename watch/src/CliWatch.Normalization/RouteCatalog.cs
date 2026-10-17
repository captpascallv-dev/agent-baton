// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.Normalization;

/// <summary>Frozen eight telephone routes. Catalog claims ≠ live verification.</summary>
internal static class RouteCatalog
{
    public static readonly IReadOnlyList<string> EightRoutes = new[]
    {
        "deepsea-codex-cli",
        "deepsea-grok-cli",
        "deepsea-v4",
        "direct-claude-code",
        "direct-codex-cli",
        "direct-cursor",
        "direct-grok-cli",
        "direct-pi"
    };
}
