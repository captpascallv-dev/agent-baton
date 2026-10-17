// SPDX-License-Identifier: MPL-2.0
using CliWatch.Contracts;
using CliWatch.Views;

namespace CliWatch.App;

/// <summary>
/// Copyable help summary from the current snapshot. Never invents PASS.
/// Uses the same HUD/details semantic path; language follows the selected UI language.
/// </summary>
public static class HelpSummaryBuilder
{
    public static string Build(CockpitSnapshot? snapshot, string? selectedProjectId, string? recipient = null) =>
        HelpSummaryText.Build(snapshot, selectedProjectId, UiLang.Zh, recipient);

    public static string Build(CockpitSnapshot? snapshot, string? selectedProjectId, UiLang lang, string? recipient = null) =>
        HelpSummaryText.Build(snapshot, selectedProjectId, lang, recipient);

    public static bool ContainsInventedPass(string text) =>
        HelpSummaryText.ContainsInventedPass(text);
}
