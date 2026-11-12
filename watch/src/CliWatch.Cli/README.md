# macOS task-fixture Watch CLI

.NET 8 CLI reuses Contracts, Collection, IdentityNormalizer and CliWatchProjector.
Windows/WPF projects retain their behavior. This is a limited fixture collector,
not a general session-history reader or a production background agent.

From repository root:

```
dotnet run --project watch/src/CliWatch.Cli -- --self-test
dotnet run --project watch/src/CliWatch.Cli -- --demo --output-dir /tmp/watch-demo
dotnet run --project watch/src/CliWatch.Cli -- --config /tmp/task-fixture/config.json --allowed-root /tmp/task-fixture --output /tmp/watch-output/watch-snapshot.json --once
```

`--watch` repeats at config `refresh_seconds` (minimum 5s); default is one pass.
Use the existing JSON config fields, with source paths inside the explicit
fixture root. Outputs must be outside that root. Empty default collection emits
mode `unknown`, not a claim of idle. Default output is
`~/Library/Application Support/AgentBatonWatch/watch-snapshot.json`.

Fixture collection accepts only read-only sources. Native session discovery,
Grok home, arbitrary process discovery rules, scope and closeout files are disabled.
Every collector read/stat/enumeration is gated by the explicit root, with symlink
ancestors rejected; indirect metadata references cannot admit outside paths.
This check-before-open is defense in depth for trusted task fixtures, not a
race-proof sandbox against concurrent malicious directory replacement. Do not
point it at a home directory or private session/log tree.

The Mac process probe reads PID/PPID topology, then only argv for fixture owner
PIDs whose recorded start ticks match, and their descendants. It does not scan
all process command lines or read session files. No command lines or executable
paths are included in the JSON output. `.NET` start times and CPU time are used;
IO counters are unavailable. PID presence is not model generation or success.
Missing/inaccessible identity stays unknown; observed PID reuse invalidates the
old identity. `ps command=` does not preserve argv boundaries reliably for paths
with spaces/quoting, so association may remain unknown. No arguments are guessed.

JSON schema 1 envelope: `written_at_utc`, `mode` (`demo`, `fixture`, `unknown`),
`snapshot`. Snapshot display fields use camelCase and camelCase enum strings.
`projects.isCurrent` is computed by the original shared `CurrentList.IsDefaultCurrent`
(`isActive && !isPaused`); viewers should use it for current/history grouping.
Projects also retain the shared projector's `isActive`, `phase`, work `axes`, and
artifact state. Delivery, process exit and acceptance remain distinct. Evidence,
source paths, navigation targets and raw diagnostics are omitted. Consumers must
not infer acceptance from a READY marker or a zero exit code.

Self-tests are offline; they do not invoke a model, quota interface, plugin, or
private history. The Windows/WPF suite is not part of these portable tests.
