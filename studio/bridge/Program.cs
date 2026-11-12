using CliWatch.App;
using CliWatch.Collection;
using CliWatch.Normalization;
using CliWatch.Projection;
using AiMoeStudio.Bridge;
#if STUDIO_MAC
using CliWatch.Mac;
#endif

if (args.SequenceEqual(new[] { "--self-test" }))
{
    await BridgeSelfTest.RunAsync();
    return 0;
}
if (args.SequenceEqual(new[] { "--identity-self-test" }))
{
    LinkedIdentitySelfTest.Run();
    return 0;
}

string? configPath = null, snapshotPath = null;
var refreshSeconds = 5;
var once = false;
int? parentPid = null;
#if STUDIO_MAC
string? allowedRoot = null;
var allowedPids = new List<int>();
#endif
try
{
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--config": configPath = Path.GetFullPath(args[++i]); break;
            case "--snapshot": snapshotPath = Path.GetFullPath(args[++i]); break;
            case "--refresh-seconds":
                if (!int.TryParse(args[++i], out refreshSeconds) || refreshSeconds is < 1 or > 3600)
                    throw new ArgumentException();
                break;
            case "--once": once = true; break;
#if STUDIO_MAC
            case "--allowed-root": allowedRoot = Path.GetFullPath(args[++i]); break;
            case "--allowed-pid":
                if (!int.TryParse(args[++i], out var allowedPid) || allowedPid < 1) throw new ArgumentException();
                allowedPids.Add(allowedPid);
                break;
#endif
            case "--parent-pid":
                if (!int.TryParse(args[++i], out var parsedPid) || parsedPid < 1) throw new ArgumentException();
                parentPid = parsedPid;
                break;
            default: throw new ArgumentException();
        }
    }
    if (configPath is null || snapshotPath is null) throw new ArgumentException();
#if STUDIO_MAC
    if (allowedRoot is null) throw new ArgumentException();
    FixtureBoundary.CheckedPath(configPath, allowedRoot);
    FixtureBoundary.CheckedPath(snapshotPath, allowedRoot);
    StudioReadBoundary.AllowedRoot = allowedRoot;
#endif
    OutputBoundary.RequireIndependent(snapshotPath, configPath, null);
}
catch (Exception)
{
#if STUDIO_MAC
    Console.Error.WriteLine("Usage: AiMoeStudio.Bridge --config <fixture config> --snapshot <independent output.json> --allowed-root <task fixture directory> [--allowed-pid PID] [--refresh-seconds 5] [--once]");
#else
    Console.Error.WriteLine("Usage: AiMoeStudio.Bridge --config <CLI Watch config> --snapshot <independent output.json> [--refresh-seconds 5] [--once]");
#endif
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
using var parent = ParentLifetime.Attach(parentPid, cancel);
#if STUDIO_MAC
var macOwners = FixtureBoundary.OwnerIdentities(allowedRoot!);
var macSnapshot = new MacProcessSnapshot(allowedPids.Count > 0 ? allowedPids : macOwners.Keys, macOwners);
#endif
using var refresh = new RefreshService(
#if STUDIO_MAC
    new MailboxCollector(macSnapshot, new MacProcessProbe(macSnapshot), promptBindingDirectory: null),
#else
    new MailboxCollector(new WindowsProcessSnapshot(), new WindowsProcessProbe(), promptBindingDirectory: null),
#endif
    new LinkedIdentityNormalizer(new IdentityNormalizer()), new CliWatchProjector());

// Keep the collector's activity baselines/budget cursors and projector's previous frame.
// Delay is after completion, so slow collections cannot overlap the following one.
var state = new PublishedState();
state.RestoreLastPublished(snapshotPath);
var lastSucceeded = false;
do
{
    try
    {
        var config = BridgeConfig.Load(configPath);
#if STUDIO_MAC
        var collectorSettings = FixtureBoundary.Validate(config, allowedRoot!);
#else
        var collectorSettings = config.ToCollectorSettings();
#endif
        OutputBoundary.RequireIndependent(snapshotPath, configPath, config);
        var result = await refresh.RefreshAsync(collectorSettings, cancel.Token);
        if (result.Canceled) break;
        if (result.Succeeded && result.Snapshot is not null)
        {
            state.Success(result.Snapshot);
            lastSucceeded = true;
        }
        else
        {
            state.Failure("本次 CLI Watch 采集失败，保留上次真实状态。");
            lastSucceeded = false;
        }
    }
    catch (BridgeConfigException)
    {
        state.Failure("CLI Watch 配置缺失或无效，请检查本机只读配置。");
        lastSucceeded = false;
    }
    catch (OutputBoundaryException)
    {
        Console.Error.WriteLine("snapshot_output_boundary_rejected");
        return 2;
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
    catch (Exception)
    {
        state.Failure("本次 CLI Watch 采集不可用，保留上次真实状态。");
        lastSucceeded = false;
    }

    try
    {
#if STUDIO_MAC
        // Recheck on every publish, not only at startup: a source directory may
        // have been replaced with a link while the long-lived bridge was running.
        FixtureBoundary.CheckedPath(snapshotPath, allowedRoot!);
        FixtureBoundary.RequireNoLinks(snapshotPath);
#endif
        await AtomicSnapshot.WriteAsync(snapshotPath, state.Export());
    }
    catch (Exception)
    {
        // Exception messages can include source paths and evidence; never emit them.
        Console.Error.WriteLine("snapshot_publish_failed");
        if (once) return 1;
    }
    if (once) break;
    try { await Task.Delay(TimeSpan.FromSeconds(refreshSeconds), cancel.Token); }
    catch (OperationCanceledException) { break; }
} while (!cancel.IsCancellationRequested);
return lastSucceeded ? 0 : 1;
