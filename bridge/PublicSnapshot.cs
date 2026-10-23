using System.Text.Json;
using System.Text.RegularExpressions;
using CliWatch.Contracts;
using CliWatch.Projection;
using CliWatch.Views;

namespace AiMoeStudio.Bridge;

public sealed record PublicSource(string Name, DateTimeOffset? CollectedAt, DateTimeOffset? PublishedAt,
    string Quality, bool Stale, string Status, string? Error, string Coverage);
public sealed record PublicArtifact(string Label, string State);
public sealed record PublicWork(string Id, string? ActorName, string? ActorKind, string Role, string? Route,
    string? TaskName, string Summary, string? Model, string? Effort, string? Blocker, string Quality,
    bool IsHistorical, WorkAxes Axes, IReadOnlyList<PublicArtifact> Artifacts);
public sealed record PublicProject(string Id, string Name, string ProjectLabel, string Summary, string Phase,
    string StatusLabel, string StatusBasis, string? Goal, bool IsCurrent, string Quality,
    DateTimeOffset? LastFactAt, DateTimeOffset? ObservedActivityAt, IReadOnlyList<PublicWork> Works);
public sealed record PublicIssue(string Code, string Detail, string Scope);
public sealed record PublicSnapshot(int SchemaVersion, string Mode, PublicSource Source,
    IReadOnlyList<PublicProject> Projects, IReadOnlyList<PublicIssue> Issues);

public sealed class PublishedState
{
    CockpitSnapshot? _last;
    DateTimeOffset? _publishedAt;
    string? _error;
    PublicSnapshot? _persisted;

    public void RestoreLastPublished(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<PublicSnapshot>(File.ReadAllText(path), AtomicSnapshot.JsonOptions);
            if (saved?.SchemaVersion == 1 && saved.Mode == "live" && saved.Source.CollectedAt is not null)
                _persisted = saved;
        }
        catch (Exception) { /* Missing/invalid old files are not a live frame. */ }
    }

    public void Success(CockpitSnapshot snapshot)
    {
        _last = snapshot;
        _publishedAt = DateTimeOffset.UtcNow;
        _error = null;
        _persisted = null;
    }
    public void Failure(string error) => _error = error;

    public PublicSnapshot Export()
    {
        var failed = _error is not null;
        if (_last is null && _persisted is not null)
            return _persisted with { Source = _persisted.Source with {
                Quality = "LastKnown", Stale = true, Status = "degraded",
                Error = _error ?? "等待本次真实采集；目前保留上次观察。"
            } };
        var quality = _last is null ? "Unavailable" : failed ? "LastKnown" : _last.Quality.ToString();
        var status = _last is null ? (failed ? "offline" : "starting")
            : failed || _last.Quality != DataQuality.Fresh ? "degraded" : "live";
        return new PublicSnapshot(1, "live", new PublicSource("CLI Watch", _last?.CollectedAt,
            _publishedAt, quality, _last is null || failed, status, _error, "本机 CLI 执行状态"),
            _last?.Projects.Select(Project).ToArray() ?? Array.Empty<PublicProject>(),
            _last?.Issues.Select(issue => new PublicIssue(DisplayText.Clean(issue.Code)!,
                DisplayText.Clean(issue.Detail)!, CurrentList.IsHistoricalIssue(issue) ? "history" : "current"))
                .ToArray() ?? Array.Empty<PublicIssue>());
    }

    static PublicProject Project(ProjectView project) => new(project.Id, DisplayText.Clean(project.Name)!,
        DisplayText.Clean(ProjectLabels.ProjectLabel(ProjectLabels.ProjectKeyFromName(project.Name), UiLang.Zh))!,
        DisplayText.Clean(project.Summary)!, project.Phase, CliWatchStatus.LabelZh(project.Phase),
        CliWatchStatus.BasisZh(project.Phase), DisplayText.Clean(project.Goal),
        CurrentList.IsDefaultCurrent(project), project.Quality.ToString(), project.LastFactAt,
        project.ObservedActivityAt, project.WorkItems.Select(work => new PublicWork(work.Id,
            DisplayText.Clean(work.ActorName), DisplayText.Clean(work.ActorKind), DisplayText.Clean(work.Role)!,
            DisplayText.Clean(work.Route), DisplayText.Clean(work.TaskName), DisplayText.Clean(work.Summary)!,
            DisplayText.Clean(work.Model), DisplayText.Clean(work.Effort), DisplayText.Clean(work.Blocker),
            work.Quality.ToString(), StatusLanguage.IsHistoricalWork(work), work.Axes,
            work.Artifacts.Select(artifact => new PublicArtifact(DisplayText.Clean(artifact.Label)!,
                DisplayText.Clean(artifact.State)!)).ToArray())).ToArray());
}

// The public DTO has no raw documents/evidence, command lines, transcripts or targets.
public static class DisplayText
{
    static readonly Regex LocalPath = new(@"(?i)(?:[a-z]:[\\/]|\\\\|file://)[^\s\r\n""<>|;，。]+", RegexOptions.CultureInvariant);
    static readonly Regex Secret = new(@"(?i)(?:(?:api[_-]?key|access[_-]?token|authorization|password|secret|cookie)\s*[:=]\s*[^\s;,]+|\b(?:sk-(?:proj-)?[a-z0-9_-]{10,}|Bearer\s+[a-z0-9._-]+))", RegexOptions.CultureInvariant);
    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var cleaned = Secret.Replace(LocalPath.Replace(text, "[本机路径]"), "[已隐藏凭据]");
        return cleaned.Length <= 1200 ? cleaned : cleaned[..1200] + "…";
    }
}

public static class AtomicSnapshot
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static async Task WriteAsync(string path, PublicSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
