# Dispatch one task card to an external agent CLI, detached; print the watch command.
# 派一个任务卡给外部 agent CLI（后台隐藏进程），输出守候命令。
#
#   pwsh -File Dispatch.ps1 -Executor codex -Workspace <dir> -Outbox <dir> -PromptFile <card.md> -Task "name"
#
# Executors are defined in executors.json (copy executors.example.json). No model/account is hard-coded here.
# -ResumeSessionId selects args_on_resume and fills {resume} / {resume_path}. It does not pick an executor.
param(
    [Parameter(Mandatory = $true)][string]$Executor,
    [Parameter(Mandatory = $true)][string]$Workspace,
    [Parameter(Mandatory = $true)][string]$Outbox,
    [Parameter(Mandatory = $true)][string]$PromptFile,
    [Parameter(Mandatory = $true)][string]$Task,
    [string]$Project = '',
    [ValidatePattern('^$|^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$')][string]$JobId = '',
    [ValidatePattern('^$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')][string]$ResumeSessionId = '',
    [string]$ResumeSessionPath = '',
    [int]$TimeoutMinutes = 90,
    [string]$Config = '',
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = Split-Path -Parent $PSScriptRoot
if (-not $Config) { $Config = Join-Path $root 'executors.json' }
if (-not (Test-Path -LiteralPath $Config)) { Write-Error "Config not found: $Config (copy executors.example.json to executors.json)"; exit 2 }
$cfg = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json -AsHashtable
if (-not $cfg.executors.ContainsKey($Executor)) { Write-Error "Unknown executor '$Executor'. Known: $($cfg.executors.Keys -join ', ')"; exit 2 }
$e = $cfg.executors[$Executor]
if ($TimeoutMinutes -lt 1) { throw "TimeoutMinutes must be >= 1." }

foreach ($p in $Workspace, $PromptFile) { if (-not (Test-Path -LiteralPath $p)) { Write-Error "Not found: $p"; exit 2 } }
$Workspace = (Resolve-Path -LiteralPath $Workspace).Path
$PromptFile = (Resolve-Path -LiteralPath $PromptFile).Path

$resuming = [bool]($ResumeSessionId -or $ResumeSessionPath)
if ($ResumeSessionPath -and -not $ResumeSessionId) { throw "-ResumeSessionPath requires -ResumeSessionId." }
$argTemplate = @($e.args)
if ($resuming) {
    if (-not $e.args_on_resume) {
        throw "Executor '$Executor' has no args_on_resume. Refusing to drop -ResumeSessionId. Add args_on_resume to the executor config."
    }
    $argTemplate = @($e.args_on_resume)
    $joined = $argTemplate -join "`n"
    if ($joined -notmatch '\{resume\}' -and $joined -notmatch '\{resume_path\}') {
        throw "args_on_resume for '$Executor' must include {resume} or {resume_path}."
    }
    if ($joined -match '\{resume_path\}' -and -not $ResumeSessionPath) {
        throw "args_on_resume uses {resume_path} but -ResumeSessionPath was not given."
    }
}
if ($ResumeSessionPath) {
    if (-not (Test-Path -LiteralPath $ResumeSessionPath -PathType Leaf)) { throw "Resume session file not found: $ResumeSessionPath" }
    $ResumeSessionPath = (Resolve-Path -LiteralPath $ResumeSessionPath).Path
}
$unset = @()
if ($e.unset_env) { $unset = @($e.unset_env) }
foreach ($name in $unset) {
    if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Invalid unset_env name: $name" }
}

if (Test-Path -LiteralPath (Join-Path $Outbox 'READY.json')) {
    throw "Outbox already contains READY.json: $Outbox. Use a new outbox for each task; existing delivery files are preserved."
}
New-Item -ItemType Directory -Force $Outbox | Out-Null
$Outbox = (Resolve-Path -LiteralPath $Outbox).Path
if (-not $JobId) { $JobId = [guid]::NewGuid().ToString('D') }
$jobDir = Join-Path $root "jobs/$JobId"
# Default 90 still watches 29 minutes: min(29, TimeoutMinutes + 10). Shorter tasks do not keep a 29-minute watch.
$watchMinutes = 29
if ($TimeoutMinutes -lt 19) { $watchMinutes = $TimeoutMinutes + 10 }

$tokens = @{
    '{workspace}' = $Workspace; '{outbox}' = $Outbox; '{card}' = $PromptFile
    '{carddir}' = (Split-Path -Parent $PromptFile); '{jobdir}' = $jobDir
    '{resume}' = $ResumeSessionId; '{resume_path}' = $ResumeSessionPath
}
function Expand([string]$s) { foreach ($k in $tokens.Keys) { $s = $s.Replace($k, $tokens[$k]) }; $s }
$pointerTemplate = if ($cfg.pointer) { $cfg.pointer } else { 'Read the task card file "{card}" (UTF-8) in full and follow it exactly. Do nothing outside the card.' }
$tokens['{pointer}'] = Expand $pointerTemplate

$cmd = Get-Command $e.command -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $cmd) { Write-Error "Command not found on PATH: $($e.command)"; exit 2 }
$expandedArgs = [Collections.Generic.List[string]]::new()
foreach ($item in $argTemplate) { $expandedArgs.Add((Expand ([string]$item))) }
$spec = [ordered]@{
    command      = $cmd.Source
    args         = @($expandedArgs)
    cwd          = if ($e.cwd) { Expand $e.cwd } else { $Workspace }
    stdin_card   = [bool]$e.stdin_card
    card         = $PromptFile
    env          = if ($e.env) { $e.env } else { @{} }
    unset_env    = @($unset)
}

if ($DryRun) { "DRYRUN JobId=$JobId executor=$Executor"; $spec | ConvertTo-Json -Depth 5; exit 0 }
if (Test-Path -LiteralPath $jobDir) { Write-Error "Job exists: $jobDir"; exit 2 }
New-Item -ItemType Directory -Force $jobDir | Out-Null
Copy-Item -LiteralPath $PromptFile (Join-Path $jobDir 'TASK_CARD.md')
$spec | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $jobDir 'spec.json') -Encoding utf8

$runner = Join-Path $PSScriptRoot 'Run-Agent.ps1'
if ($IsWindows) {
    # Use the shell so the executor does not inherit this dispatcher's stdout pipe.
    # A host that waits for the dispatch command's output would otherwise block until the executor exits.
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh -ErrorAction Stop).Source
    $startInfo.UseShellExecute = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($arg in @('-NoProfile', '-File', $runner, '-JobDir', $jobDir)) { [void]$startInfo.ArgumentList.Add($arg) }
    $proc = [Diagnostics.Process]::Start($startInfo)
} else {
    $proc = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-File', "`"$runner`"", '-JobDir', "`"$jobDir`"") -PassThru
}
if ($null -eq $proc) { throw 'Executor wrapper process did not start.' }

[ordered]@{
    job_id = $JobId; project = $Project; task = $Task; executor = $Executor
    workspace = $Workspace; outbox = $Outbox; host_pid = $proc.Id
    timeout_minutes = $TimeoutMinutes; watch_minutes = $watchMinutes
    resume_session_id = $ResumeSessionId; resume_session_path = $ResumeSessionPath
    dispatched_at = (Get-Date -Format o)
} | ConvertTo-Json | Set-Content (Join-Path $jobDir 'job.json') -Encoding utf8

"DISPATCHED JobId=$JobId executor=$Executor pid=$($proc.Id)"
"WATCH: pwsh -NoProfile -File `"$PSScriptRoot/Wait-Delivery.ps1`" -JobDir `"$Outbox`" -ProcessId $($proc.Id) -TimeoutMinutes $watchMinutes -IntervalSeconds 20"
"NOTE: The 29-min watcher default is based on a contributor-reported ~30-min Claude Code background limit; verify your version. If the watcher exits with code 2 while pid is alive, re-arm WATCH."
