// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CliWatch.App;
using CliWatch.Collection;
using CliWatch.Contracts;
using CliWatch.Mac;
using CliWatch.Normalization;
using CliWatch.Projection;

static Dictionary<string,string> Options(string[] args)
{
    var result = new Dictionary<string,string>();
    for (int i = 0; i < args.Length; i++)
    {
        var key = args[i];
        if (result.ContainsKey(key)) throw new ArgumentException("Duplicate option");
        if (key is "--once" or "--watch" or "--demo" or "--self-test") result[key] = "true";
        else if (key is "--config" or "--allowed-root" or "--output" or "--output-dir")
        {
            if (++i == args.Length || args[i].StartsWith("--")) throw new ArgumentException("Missing value");
            result[key] = args[i];
        }
        else throw new ArgumentException("Unknown option");
    }
    if (result.ContainsKey("--once") && result.ContainsKey("--watch")) throw new ArgumentException("Choose once or watch");
    if (result.ContainsKey("--self-test") && result.Count != 1) throw new ArgumentException("Self-test must be standalone");
    if (result.ContainsKey("--demo") && result.ContainsKey("--config")) throw new ArgumentException("Demo cannot collect configured sources");
    if (result.ContainsKey("--output") && result.ContainsKey("--output-dir")) throw new ArgumentException("Choose output or output-dir");
    if (result.ContainsKey("--config") != result.ContainsKey("--allowed-root")) throw new ArgumentException("Fixture config requires allowed-root");
    return result;
}
static CockpitSnapshot Demo(DateTimeOffset now)
{
    var docs = new List<RawDocument>();
    void Add(string kind, JsonObject data, string key)
    {
        data["job_key"] = key; data["mailbox_root"] = "fixture";
        docs.Add(new RawDocument(kind+key, kind, "fixture", data, now, now, "demo"));
    }
    Add("mailbox_job", new JsonObject { ["project"]="demo", ["job_id"]="sample", ["attempt_id"]="1", ["task"]="离线演示：已交卷不代表验收", ["state"]="registered", ["registered_at"]=now.AddMinutes(-3).ToString("o") }, "sample--1");
    Add("outbox_ready", new JsonObject { ["status"]="completed" }, "sample--1");
    return new CliWatchProjector().Project(new IdentityNormalizer().Normalize(new CollectionBatch(now, docs, Array.Empty<SourceIssue>())));
}
static string Opaque(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..20].ToLowerInvariant();
static string? Display(string? value)
{
    if (value is null) return null;
    if (value.Contains("--",StringComparison.Ordinal) || value.Contains('`') || value.Contains('|') || value.Contains("token",StringComparison.OrdinalIgnoreCase)) return "[受限文本]";
    var clean=System.Text.RegularExpressions.Regex.Replace(value, @"(?:/|~[/\\]|[A-Za-z]:[/\\])[^\s，。；]*", "[路径]");
    clean=System.Text.RegularExpressions.Regex.Replace(clean, @"[\w.+-]+@[\w.-]+", "[账号]");
    clean=clean.Replace('\n',' ').Replace('\r',' ');
    return clean.Length>160 ? clean[..160]+"…" : clean;
}
static byte[] Encode(CockpitSnapshot snapshot, string mode)
{
    // Public display allowlist. Evidence, filesystem locations, command lines and navigation are not exported.
    var body = new {
        schema = 1, written_at_utc = snapshot.CollectedAt, mode,
        snapshot = new {
            snapshot.CollectedAt, snapshot.Quality,
            projects = snapshot.Projects.Select(p => new {
                id=Opaque(p.Id), name=Display(p.Name), summary=Display(p.Summary), p.Phase, nextStep=Display(p.NextStep), p.IsActive, p.IsPaused, isCurrent=CliWatch.Views.CurrentList.IsDefaultCurrent(p), p.Quality,
                workItems = p.WorkItems.Select(w => new { id=Opaque(w.Id), w.Role, summary=Display(w.Summary), actorName=Display(w.ActorName), taskName=Display(w.TaskName), blocker=Display(w.Blocker), w.Axes, w.Quality,
                    artifacts = w.Artifacts.Select(a => new { id=Opaque(a.Id), label=Display(a.Label), a.State }) })
            }),
            issues = snapshot.Issues.Select(i => new { code=Display(i.Code), detail="来源受限或资料缺失，状态不可据此认定。" })
        }
    };
    var options = new JsonSerializerOptions { PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    return JsonSerializer.SerializeToUtf8Bytes(body, options);
}
static async Task Write(string output, byte[] bytes, CancellationToken ct)
{
    FixtureBoundary.RequireNoLinks(output);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    FixtureBoundary.RequireNoLinks(output);
    var temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try { await File.WriteAllBytesAsync(temp, bytes, ct); ct.ThrowIfCancellationRequested(); File.Move(temp,output,true); }
    finally { if (File.Exists(temp)) File.Delete(temp); }
}
static async Task SelfTest()
{
    static void Assert(bool condition) { if (!condition) throw new Exception("Offline assertion failed"); }
    var probe = new MacProcessSnapshot();
    Assert(probe.List(CancellationToken.None).Count == 0 && probe.Observe(1,1).State == "unknown");
    using var selfBoundary = WatchReadBoundary.Use(_ => throw new IOException("No fixture sources"));
    var empty = await new MailboxCollector().CollectAsync(new CollectorSettings(Array.Empty<string>(),Array.Empty<string>()), CancellationToken.None);
    var projected = new CliWatchProjector().Project(new IdentityNormalizer().Normalize(empty));
    Assert(projected.Projects.Count == 0);
    using var encoded = JsonDocument.Parse(Encode(Demo(DateTimeOffset.UtcNow), "demo"));
    Assert(encoded.RootElement.GetProperty("schema").GetInt32()==1);
    var text = encoded.RootElement.GetRawText();
    Assert(!text.Contains("commandLine") && !text.Contains("evidence") && !text.Contains("targets"));
    var root = Path.Combine(Path.GetTempPath(), "watch-boundary-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try {
        bool rejected=false; try { FixtureBoundary.CheckedPath("../escape",root); } catch(ArgumentException) { rejected=true; }
        Assert(rejected);
        File.WriteAllText(Path.Combine(root,"job.json"), "{\"outbox\":\"/private/not-allowed\"}");
        rejected=false; try { FixtureBoundary.CheckedPath("/private/not-allowed",root); } catch(ArgumentException) { rejected=true; }
        Assert(rejected);
        using (WatchReadBoundary.Use(path => FixtureBoundary.CheckedPath(path,root)))
        {
            rejected=false; try { WatchReadBoundary.Check("/private/not-allowed"); } catch(ArgumentException) { rejected=true; }
            Assert(rejected);
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            var link=Path.Combine(root,"escape-link");
            Directory.CreateSymbolicLink(link,Path.GetTempPath());
            rejected=false; try { FixtureBoundary.CheckedPath(Path.Combine(link,"metadata.json"),root); } catch(ArgumentException) { rejected=true; }
            Assert(rejected);
            rejected=false; try { FixtureBoundary.RequireNoLinks(Path.Combine(link,"output.json")); } catch(ArgumentException) { rejected=true; }
            Assert(rejected);
            Directory.Delete(link);
        }
        Assert(!Display("/Users/fixture/private.json user@example.invalid")!.Contains("/Users/",StringComparison.Ordinal));
        Assert(Display("run --secret PRIVATE")=="[受限文本]");
    } finally { Directory.Delete(root,true); }
    Console.WriteLine("PASS empty unknown, default no process discovery, shared fixture projection, public schema, path and reference boundary");
}
try
{
    var options = Options(args);
    if (options.ContainsKey("--self-test")) { await SelfTest(); return 0; }
    var demo = options.ContainsKey("--demo");
    var root = options.GetValueOrDefault("--allowed-root") is { } rootOption ? FixtureBoundary.CanonicalSystemPath(rootOption) : null;
    var output = FixtureBoundary.CanonicalSystemPath(options.GetValueOrDefault("--output") ?? Path.Combine(options.GetValueOrDefault("--output-dir") ?? ProductPaths.DataDirectory,"watch-snapshot.json"));
    if (root is not null && (output == Path.GetFullPath(root) || output.StartsWith(Path.GetFullPath(root).TrimEnd('/')+"/",StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException("Output must be separate from fixture inputs");
    if (demo && !options.ContainsKey("--output") && !options.ContainsKey("--output-dir")) throw new ArgumentException("Demo requires separate output");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_,e) => { e.Cancel=true; stop.Cancel(); };
    var projector = new CliWatchProjector();
    CockpitSnapshot? previous = null;
    do
    {
        var config = new AppConfig();
        CollectorSettings settings = config.ToCollectorSettings();
        MacProcessSnapshot process = new();
        if (root is not null)
        {
            var path = FixtureBoundary.CheckedPath(options["--config"],root);
            if (new FileInfo(path).Length>1048576) throw new ArgumentException("Config too large");
            var json = await File.ReadAllTextAsync(path, stop.Token);
            using var valid = JsonDocument.Parse(json);
            config = AppConfig.Parse(json,path);
            settings = FixtureBoundary.Validate(config,root);
            var identities = FixtureBoundary.OwnerIdentities(root);
            process = new MacProcessSnapshot(identities.Keys,identities);
        }
        CockpitSnapshot current;
        if (demo) current = Demo(DateTimeOffset.UtcNow);
        else {
            using var boundary = WatchReadBoundary.Use(path => root is null ? throw new IOException("No allowed sources") : FixtureBoundary.CheckedPath(path,root));
            var batch = await new MailboxCollector(process,process).CollectAsync(settings,stop.Token);
            if(root is null) batch = batch with { Issues=batch.Issues.Append(new SourceIssue("configuration","sources_unknown","No authorized fixture sources",batch.CollectedAt)).ToArray() };
            current = projector.Project(new IdentityNormalizer().Normalize(batch),previous);
        }
        previous=current;
        var bytes = Encode(current,demo ? "demo" : root is null ? "unknown" : "fixture");
        await Write(output,bytes,stop.Token);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
        if (!options.ContainsKey("--watch")) break;
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5,config.RefreshSeconds)),stop.Token);
    } while (!stop.IsCancellationRequested);
    return 0;
}
catch(OperationCanceledException) { return 0; }
catch { Console.Error.WriteLine("Watch collection failed; no private diagnostic paths exported."); return 1; }
