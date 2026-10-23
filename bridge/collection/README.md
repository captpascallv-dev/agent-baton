# Bridge-local CLI Watch collection

This source copy provides a bridge-local `CliWatch.Collection.dll`, targeting the public [CLI Watch 0.1.0 release](https://github.com/captpascallv-dev/agent-baton/releases/tag/watch-v0.1.0). Its nine source files were copied from [agent-baton/watch/src/CliWatch.Collection at watch-v0.1.0](https://github.com/captpascallv-dev/agent-baton/tree/watch-v0.1.0/watch/src/CliWatch.Collection), with the bridge performance modifications below. It retains the public `CliWatch.*` namespaces, Claude mailbox layouts and upstream MPL-2.0 license; see [LICENSE](LICENSE). The source files are included here so this bridge can build without a separate CLI Watch source checkout.

The nine collector sources are `FileCollector.cs`, `GrokSessionReader.cs`, `JsonAllowlist.cs`, `LeadDispositionObservation.cs`, `MailboxCollector.cs`, `NativeObservation.cs`, `ProcessActivityStore.cs`, `ProcessMatch.cs` and `PromptFileIdentity.cs`.

The performance changes are limited to `MailboxCollector.cs` and `GrokSessionReader.cs`:

- Index documents once per validation pass, preserving ordinal job-key comparison and exact root checks.
- Rent a buffer for serial JSON reads instead of allocating the maximum file budget for every small file. The original `maxBytes + 1` read limit, size rejection and cancellation remain.
- Classify immutable process rows once per binding pass. Process claims, PID/start-time identity and current CPU/I/O activity still use the fresh frame.
- Index Grok session directory names within one collection, preserving direct workspace-path priority and exact-path checks for groups that cannot be listed. Rebuild the index next collection; read files and activity metadata normally, and check removed directories before use.

No file content, model, process state or working/sleeping state is cached across collections. Source scope, file budgets and the bridge's five-second delay remain unchanged.

`AiMoeStudio.Collection.csproj` deliberately emits the compatible public assembly name `CliWatch.Collection`. The bridge references this local project; its other five CLI Watch assemblies come from the caller's `CliWatchRuntimeDirectory`, pointing at an independently extracted `CLIWatch-0.1.0-win-x64.zip`. Keep that input directory separate from bridge outputs. Nothing replaces the downloaded or installed CLI Watch DLLs.

Focused synthetic checks are in `../tests/CollectorBoundaryTests.csproj`. They support `-p:UseInstalledCollector=true` to run the same boundary expectations against the unmodified release collector. A future CLI Watch upgrade requires a compatibility check of this local copy as well as the five referenced assemblies.
