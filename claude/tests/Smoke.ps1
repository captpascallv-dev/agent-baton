# Offline consumer smoke test. No agent CLI, model, credentials, or network required.
# All runtime files, jobs, and config live in a uniquely owned temporary directory.
param([switch]$KeepTemp, [switch]$OutboxReuseOnly)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sourceRoot = Split-Path -Parent $PSScriptRoot
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$checks = [Collections.Generic.List[string]]::new()
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ('agent-lead-wake smoke ' + [guid]::NewGuid().ToString('N'))
$owner = [guid]::NewGuid().ToString('N')
$ownerFile = Join-Path $tempRoot '.smoke-owner'
$started = [Collections.Generic.List[object]]::new()

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Pass([string]$Name) {
    $checks.Add($Name)
    "PASS $Name"
}
function Invoke-Pwsh([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $pwsh
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) {
        $process.Kill($true)
        throw 'Local smoke subprocess exceeded 60 seconds.'
    }
    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    }
    $process.Dispose()
    Assert-That ($result.ExitCode -eq $ExpectedExit) "Expected exit $ExpectedExit, got $($result.ExitCode): $($result.Output)"
    return $result
}
function Invoke-Script([string]$Path, [string[]]$Arguments = @(), [int]$ExpectedExit = 0) {
    Invoke-Pwsh -Arguments (@('-NoProfile', '-File', $Path) + $Arguments) -ExpectedExit $ExpectedExit
}
function Dispatch-Mock([string]$Executor, [string]$Id, [string]$Outbox) {
    $result = Invoke-Script $dispatch @('-Executor', $Executor, '-Workspace', $workspace,
        '-Outbox', $Outbox, '-PromptFile', $card, '-Task', '中文离线交付', '-JobId', $Id, '-Config', $config)
    Assert-That ($result.Output -match 'DISPATCHED .* pid=(\d+)') "Missing dispatch PID: $($result.Output)"
    $hostProcess = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
    if ($hostProcess) { $started.Add([pscustomobject]@{ Id = $hostProcess.Id; StartTime = $hostProcess.StartTime }) }
    $job = Get-Content -LiteralPath (Join-Path $sandbox "jobs/$Id/job.json") -Raw | ConvertFrom-Json
    Assert-That ($job.outbox -eq $Outbox) 'Dispatch did not preserve the outbox path.'
    return $job
}
function Wait-HostExit([int]$ProcessId) {
    $deadline = (Get-Date).AddSeconds(15)
    while (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
        if ((Get-Date) -ge $deadline) { throw "Mock host $ProcessId did not exit." }
        Start-Sleep -Milliseconds 100
    }
}
function Test-OutboxReuse {
    $oldOutbox = Join-Path $tempRoot 'previous delivery outbox 中文'
    New-Item -ItemType Directory -Path $oldOutbox | Out-Null
    $oldReady = Join-Path $oldOutbox 'READY.json'
    $oldReport = Join-Path $oldOutbox 'REPORT.md'
    Set-Content -LiteralPath $oldReady -Value '{"status":"completed","notes":"旧交卷"}' -Encoding utf8
    Set-Content -LiteralPath $oldReport -Value '保留上次交卷。' -Encoding utf8
    $readyBytes = [IO.File]::ReadAllBytes($oldReady)
    $reportBytes = [IO.File]::ReadAllBytes($oldReport)
    $result = Invoke-Script $dispatch @('-Executor', 'stdin', '-Workspace', $workspace,
        '-Outbox', $oldOutbox, '-PromptFile', $card, '-Task', 'new task', '-JobId', 'smoke-reused-outbox', '-Config', $config) 1
    Assert-That ($result.Output.Contains('Outbox already contains READY.json')) 'Dispatch did not explicitly refuse the previous READY.'
    Assert-That ([Collections.StructuralComparisons]::StructuralEqualityComparer.Equals($readyBytes, [IO.File]::ReadAllBytes($oldReady))) 'Existing READY bytes changed.'
    Assert-That ([Collections.StructuralComparisons]::StructuralEqualityComparer.Equals($reportBytes, [IO.File]::ReadAllBytes($oldReport))) 'Existing REPORT bytes changed.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $sandbox 'jobs/smoke-reused-outbox'))) 'Rejected Outbox created a new job.'
    Assert-That (@(Get-ChildItem -LiteralPath $oldOutbox -File).Count -eq 2) 'Rejected Outbox gained new delivery files.'
    Pass 'Dispatch refuses an Outbox with previous READY and preserves old delivery files'
}

try {
    if (-not $OutboxReuseOnly) {
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'bin') -Filter '*.ps1') {
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
        Assert-That ($errors.Count -eq 0) "Syntax errors in $($script.Name): $errors"
    }
    $example = Get-Content -LiteralPath (Join-Path $sourceRoot 'executors.example.json') -Raw | ConvertFrom-Json -AsHashtable
    Assert-That ($example.executors.Count -gt 0) 'Executor example is empty.'
    foreach ($entry in $example.executors.Values) {
        Assert-That ([bool]$entry.command -and $entry.args -is [array]) 'Invalid executor command/args example.'
        $basePlaceholders = @('{workspace}', '{outbox}', '{card}', '{carddir}', '{jobdir}', '{pointer}')
        foreach ($argument in @($entry.args)) {
            foreach ($match in [regex]::Matches([string]$argument, '\{[^{}]+\}')) {
                Assert-That ($match.Value -in $basePlaceholders) "Unknown placeholder $($match.Value)"
            }
        }
        $entryNames = @($entry.Keys)
        if ($entryNames -contains 'args_on_resume' -and $entry.args_on_resume) {
            foreach ($argument in @($entry.args_on_resume)) {
                foreach ($match in [regex]::Matches([string]$argument, '\{[^{}]+\}')) {
                    Assert-That ($match.Value -in ($basePlaceholders + @('{resume}', '{resume_path}'))) "Unknown resume placeholder $($match.Value)"
                }
            }
        }
        if ($entryNames -contains 'unset_env' -and $entry.unset_env) {
            foreach ($name in @($entry.unset_env)) {
                Assert-That ($name -match '^[A-Za-z_][A-Za-z0-9_]*$') "Invalid unset_env name $name"
            }
        }
    }
    Assert-That (Test-Path -LiteralPath (Join-Path $sourceRoot 'templates/TASK_CARD.md')) 'Task-card template path is missing.'
    Pass 'syntax, JSON, template paths'
    }

    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Set-Content -LiteralPath $ownerFile -Value $owner -Encoding utf8
    $sandbox = Join-Path $tempRoot 'copied package with spaces'
    New-Item -ItemType Directory -Path $sandbox | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'bin') -Destination $sandbox -Recurse
    $workspace = Join-Path $tempRoot 'workspace with spaces 中文'
    New-Item -ItemType Directory -Path $workspace | Out-Null
    $card = Join-Path $tempRoot 'task card 中文.md'
    $cardText = "# 中文任务：离线交付`n保留 UTF-8：葡萄、执行、交卷。"
    Set-Content -LiteralPath $card -Value $cardText -Encoding utf8
    $mock = Join-Path $tempRoot 'mock executor.ps1'
    @'
param([string]$Outbox, [string]$Mode, [string]$Pointer, [string]$Workspace, [string]$Resume, [string]$ResumePath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
if ((Get-Location).Path -ne $Workspace) { throw 'Wrong executor working directory.' }
if ($Mode -eq 'stdin') { $text = [Console]::In.ReadToEnd() }
elseif ($Mode -eq 'pointer') {
    if ($Pointer -notmatch '^Read the task card file "([^"]+)" \(UTF-8\) in full and follow it exactly\. Do nothing outside the card\.$') { throw 'Bad pointer argument.' }
    $text = Get-Content -LiteralPath $Matches[1] -Raw -Encoding utf8
}
elseif ($Mode -eq 'no-ready') { '中文执行者退出，没有交卷。'; exit 7 }
elseif ($Mode -eq 'resume') {
    if ($env:SHELL -or $env:MSYSTEM) { throw 'Inherited shell env was not cleared.' }
    if (-not $Resume) { throw 'Missing resume id.' }
    if (-not (Test-Path -LiteralPath $ResumePath -PathType Leaf)) { throw 'Missing resume path.' }
    Set-Content -LiteralPath (Join-Path $Outbox 'resume-seen.txt') -Value $Resume -Encoding utf8
    $text = ''
}
else { throw 'Unknown mock mode.' }
if ($Mode -ne 'resume') {
    if (-not $text.Contains('中文任务：离线交付') -or -not $text.Contains('葡萄、执行、交卷。')) { throw 'UTF-8 task card was lost.' }
    Set-Content -LiteralPath (Join-Path $Outbox 'received-card.txt') -Value $text -Encoding utf8
}
Set-Content -LiteralPath (Join-Path $Outbox 'REPORT.md') -Value '中文交卷完成：葡萄、执行、交卷。' -Encoding utf8
'中文执行日志：任务已完成。'
@{ status = 'completed'; deliverables = @('REPORT.md', 'received-card.txt'); notes = '中文离线交付' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Outbox 'READY.json') -Encoding utf8
'@
        | Set-Content -LiteralPath $mock -Encoding utf8
    $config = Join-Path $tempRoot 'mock executors.json'
    $mockExecutors = @{}
    foreach ($mode in @('stdin', 'pointer', 'no-ready')) {
        $mockExecutors[$mode] = @{
            command = $pwsh
            args = @('-NoProfile', '-File', $mock, '-Outbox', '{outbox}', '-Mode', $mode, '-Workspace', '{workspace}')
            stdin_card = ($mode -eq 'stdin')
        }
        if ($mode -eq 'pointer') { $mockExecutors[$mode].args += @('-Pointer', '{pointer}') }
    }
    $mockExecutors['resume'] = @{
        command = $pwsh
        args = @('-NoProfile', '-File', $mock, '-Outbox', '{outbox}', '-Mode', 'resume', '-Workspace', '{workspace}')
        args_on_resume = @('-NoProfile', '-File', $mock, '-Outbox', '{outbox}', '-Mode', 'resume', '-Workspace', '{workspace}', '-Resume', '{resume}', '-ResumePath', '{resume_path}')
        stdin_card = $false
        unset_env = @('SHELL', 'MSYSTEM')
    }
    @{ executors = $mockExecutors } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $config -Encoding utf8
    $dispatch = Join-Path $sandbox 'bin/Dispatch.ps1'
    $wait = Join-Path $sandbox 'bin/Wait-Delivery.ps1'
    $waitMany = Join-Path $sandbox 'bin/Wait-Many.ps1'
    $pending = Join-Path $sandbox 'bin/Pending.ps1'
    if ($OutboxReuseOnly) {
        Test-OutboxReuse
        "SMOKE PASS: $($checks.Count) checks; PowerShell $($PSVersionTable.PSVersion); $($PSVersionTable.Platform)"
        return
    }
    $jobs = @{}
    foreach ($mode in @('stdin', 'pointer')) {
        $outbox = Join-Path $tempRoot "$mode outbox 中文"
        $job = Dispatch-Mock $mode "smoke-$mode" $outbox
        $jobs[$mode] = $job
        $result = Invoke-Script $wait @('-JobDir', $outbox, '-ProcessId', [string]$job.host_pid, '-TimeoutMinutes', '0.5', '-IntervalSeconds', '1')
        Assert-That ($result.Output.Contains('state=ready ready_status=completed')) 'Ready terminal state was not reported.'
        $ready = Get-Content -LiteralPath (Join-Path $outbox 'READY.json') -Raw -Encoding utf8 | ConvertFrom-Json
        Assert-That ($ready.status -eq 'completed' -and $ready.notes -eq '中文离线交付') 'READY content is not UTF-8/completed.'
        Assert-That ((Get-Content -LiteralPath (Join-Path $outbox 'REPORT.md') -Raw -Encoding utf8).Contains('中文交卷完成')) 'Chinese REPORT content is missing.'
        Assert-That ((Get-Content -LiteralPath (Join-Path $outbox 'received-card.txt') -Raw -Encoding utf8).Contains($cardText)) 'Task-card content changed in transit.'
        Wait-HostExit $job.host_pid
        $jobPath = Join-Path $sandbox "jobs/$($job.job_id)"
        Assert-That ((Get-Content -LiteralPath (Join-Path $jobPath 'exit_code.txt') -Raw).Trim() -eq '0') 'Mock executor did not exit successfully.'
        Assert-That ((Get-Content -LiteralPath (Join-Path $jobPath 'agent.log') -Raw -Encoding utf8).Contains('中文执行日志')) 'Chinese executor log is missing.'
        $terminal = Get-Content -LiteralPath (Join-Path $jobPath 'terminal.json') -Raw -Encoding utf8 | ConvertFrom-Json
        Assert-That ($terminal.exit_code -eq 0) 'terminal.json did not record a successful executor exit.'
        $runnerOwner = Get-Content -LiteralPath (Join-Path $jobPath 'owner.json') -Raw -Encoding utf8 | ConvertFrom-Json
        Assert-That ($runnerOwner.job_id -eq $job.job_id -and $runnerOwner.attempt_id -eq '1') 'Runner observation identity lost its registered job.'
        Assert-That ($runnerOwner.pid -eq $job.host_pid -and $runnerOwner.start_time_utc_ticks -gt 0) 'Runner PID/birth identity is missing.'
        Assert-That ($terminal.pid -eq $runnerOwner.pid -and $terminal.start_time_utc_ticks -eq $runnerOwner.start_time_utc_ticks) 'Terminal identity differs from the original runner.'
        Assert-That ($job.watch_minutes -eq 29) 'Default dispatch did not keep the 29-minute watch.'
        Pass "$mode Dispatch -> Run-Agent -> REPORT/READY -> Wait-Delivery, spaces and UTF-8"
    }

    $empty = Join-Path $tempRoot 'empty outbox'
    New-Item -ItemType Directory -Path $empty | Out-Null
    $result = Invoke-Script $wait @('-JobDir', $empty, '-TimeoutMinutes', '0', '-IntervalSeconds', '1') 2
    Assert-That ($result.Output.Contains('state=timeout')) 'Timeout was not reported.'
    Pass 'Wait-Delivery timeout'

    $noReady = Join-Path $tempRoot 'no ready outbox'
    $unfinished = Dispatch-Mock 'no-ready' 'smoke-no-ready' $noReady
    Wait-HostExit $unfinished.host_pid
    $exitMarker = Join-Path $sandbox 'jobs/smoke-no-ready/exit_code.txt'
    Assert-That ((Get-Content -LiteralPath $exitMarker -Raw).Trim() -eq '7') 'No-ready mock exit code was not retained.'
    $result = Invoke-Script $wait @('-JobDir', $noReady, '-TerminalFiles', $exitMarker, '-TimeoutMinutes', '0.5', '-IntervalSeconds', '1') 3
    Assert-That ($result.Output.Contains('state=ended-without-ready')) 'Native end marker was not reported.'
    $result = Invoke-Script $wait @('-JobDir', $noReady, '-ProcessId', [string]$unfinished.host_pid, '-TimeoutMinutes', '0.5', '-IntervalSeconds', '1') 4
    Assert-That ($result.Output.Contains('state=process-gone-without-ready')) 'Missing process without READY was not reported.'
    Pass 'Wait-Delivery terminal marker and process-gone without READY'

    # A wrapper passes real arrays; pwsh -File does not accept array-valued CLI parameters.
    $manyWrapper = Join-Path $tempRoot 'wait many wrapper.ps1'
    @'
param([string]$ScriptPath, [string]$First, [string]$Second, [int]$FirstProcess = 0, [double]$Minutes = 0.5)
& $ScriptPath -Outbox @($First, $Second) -ProcessId @($FirstProcess, 0) -TimeoutMinutes $Minutes -IntervalSeconds 1
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $manyWrapper -Encoding utf8
    $result = Invoke-Script $manyWrapper @('-ScriptPath', $waitMany, '-First', $noReady, '-Second', $jobs.pointer.outbox)
    Assert-That ($result.Output.Contains("READY $($jobs.pointer.outbox)")) 'Wait-Many did not identify the completed second job.'
    $result = Invoke-Script $manyWrapper @('-ScriptPath', $waitMany, '-First', $noReady, '-Second', $empty, '-Minutes', '0') 2
    Assert-That ($result.Output.Contains('TIMEOUT')) 'Wait-Many timeout was not reported.'
    $result = Invoke-Script $manyWrapper @('-ScriptPath', $waitMany, '-First', $noReady, '-Second', $empty, '-FirstProcess', [string]$unfinished.host_pid) 4
    Assert-That ($result.Output.Contains("GONE-WITHOUT-READY $noReady")) 'Wait-Many missing-process state was not reported.'
    Pass 'Wait-Many multiple outboxes, READY, timeout, process-gone'

    $result = Invoke-Script $pending
    Assert-That ($result.Output.Contains('PENDING smoke-no-ready') -and $result.Output.Contains('alive=False')) 'Pending did not expose the unfinished ended job.'
    Assert-That (-not $result.Output.Contains('PENDING smoke-stdin') -and -not $result.Output.Contains('PENDING smoke-pointer')) 'Pending included already delivered jobs.'
    $watch = ($result.Output -split '\r?\n' | Where-Object { $_ -match '^\s+WATCH: ' } | Select-Object -First 1) -replace '^\s+WATCH: ', ''
    Assert-That ([bool]$watch) 'Pending did not return a recovery WATCH command.'
    $resumed = Invoke-Pwsh @('-NoProfile', '-Command', ($watch + '; exit $LASTEXITCODE')) 4
    Assert-That ($resumed.Output.Contains('state=process-gone-without-ready')) 'Pending recovery WATCH command was not executable.'
    Pass 'Pending filters delivered jobs and emits an executable recovery command'

    $resumeId = [guid]::NewGuid().ToString('D')
    $resumeFile = Join-Path $tempRoot 'resume session.jsonl'
    Set-Content -LiteralPath $resumeFile -Value "session-marker`n" -Encoding utf8
    $env:SHELL = 'bash'
    $env:MSYSTEM = 'MINGW64'
    try {
        $refusedOutbox = Join-Path $tempRoot 'resume refused outbox'
        $refused = Invoke-Script $dispatch @('-Executor', 'stdin', '-Workspace', $workspace, '-Outbox', $refusedOutbox,
            '-PromptFile', $card, '-Task', 'resume refused', '-JobId', 'smoke-resume-refused', '-Config', $config,
            '-ResumeSessionId', $resumeId) 1
        Assert-That ($refused.Output.Contains('no args_on_resume')) 'Resume without args_on_resume was not refused.'
        Assert-That (-not (Test-Path -LiteralPath $refusedOutbox)) 'Refused resume created an outbox.'
        $resumeOutbox = Join-Path $tempRoot 'resume outbox 中文'
        $resumeResult = Invoke-Script $dispatch @('-Executor', 'resume', '-Workspace', $workspace, '-Outbox', $resumeOutbox,
            '-PromptFile', $card, '-Task', 'resume ok', '-JobId', 'smoke-resume', '-Config', $config,
            '-ResumeSessionId', $resumeId, '-ResumeSessionPath', $resumeFile)
        Assert-That ($resumeResult.Output -match 'DISPATCHED .* pid=(\d+)') "Missing resume dispatch PID: $($resumeResult.Output)"
        $resumeHost = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
        if ($resumeHost) { $started.Add([pscustomobject]@{ Id = $resumeHost.Id; StartTime = $resumeHost.StartTime }) }
        $resumeJob = Get-Content -LiteralPath (Join-Path $sandbox 'jobs/smoke-resume/job.json') -Raw | ConvertFrom-Json
        Assert-That ($resumeJob.resume_session_id -eq $resumeId) 'job.json did not record the resume id.'
        $resumeWait = Invoke-Script $wait @('-JobDir', $resumeOutbox, '-ProcessId', [string]$resumeJob.host_pid, '-TimeoutMinutes', '0.5', '-IntervalSeconds', '1')
        Assert-That ($resumeWait.Output.Contains('state=ready')) 'Resume dispatch did not deliver.'
        Assert-That ((Get-Content -LiteralPath (Join-Path $resumeOutbox 'resume-seen.txt') -Raw -Encoding utf8).Trim() -eq $resumeId) 'Executor did not see the resume id.'
        Wait-HostExit $resumeJob.host_pid
    } finally {
        Remove-Item Env:SHELL, Env:MSYSTEM -ErrorAction SilentlyContinue
    }
    Pass 'Resume uses args_on_resume, and a missing resume template is refused before writes'

    Set-Content -LiteralPath (Join-Path $sandbox 'jobs/smoke-no-ready/CLOSED.txt') -Value 'closed' -Encoding utf8
    $closedPending = Invoke-Script $pending
    Assert-That ($closedPending.Output.Contains('NO PENDING JOBS')) 'CLOSED.txt job was still pending.'
    Assert-That (-not $closedPending.Output.Contains('PENDING smoke-no-ready')) 'CLOSED.txt did not hide the unfinished job.'
    Pass 'Pending skips a job directory marked CLOSED.txt'

    foreach ($badId in @('../escape', '..\escape', '.', 'nested/job')) {
        $badOutbox = Join-Path $tempRoot 'invalid id outbox'
        $rejected = Invoke-Script $dispatch @('-Executor', 'stdin', '-Workspace', $workspace, '-Outbox', $badOutbox,
            '-PromptFile', $card, '-Task', 'invalid id', '-JobId', $badId, '-Config', $config) 1
        Assert-That (-not (Test-Path -LiteralPath $badOutbox)) 'Invalid JobId created an outbox before validation.'
    }
    Pass 'JobId rejects traversal before any dispatch writes'
    Test-OutboxReuse
    "SMOKE PASS: $($checks.Count) checks; PowerShell $($PSVersionTable.PSVersion); $($PSVersionTable.Platform)"
}
finally {
    # Only stop hosts started by this smoke run, before touching its files.
    foreach ($hostRecord in $started) {
        $process = Get-Process -Id $hostRecord.Id -ErrorAction SilentlyContinue
        if ($process -and $process.StartTime -eq $hostRecord.StartTime) { $process | Stop-Process -ErrorAction SilentlyContinue }
    }
    if (Test-Path -LiteralPath $tempRoot) {
        if ($KeepTemp) { "SMOKE TEMP: $tempRoot" }
        else {
            $resolvedRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $tempRoot).Path)
            $resolvedBase = $tempBase.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            Assert-That ($resolvedRoot.StartsWith($resolvedBase, [StringComparison]::OrdinalIgnoreCase)) 'Refusing cleanup outside the OS temporary directory.'
            Assert-That ((Split-Path -Leaf $resolvedRoot) -like 'agent-lead-wake smoke *') 'Refusing cleanup of an unexpected directory.'
            Assert-That ((Get-Content -LiteralPath $ownerFile -Raw).Trim() -eq $owner) 'Refusing cleanup without the matching smoke owner marker.'
            Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        }
    }
}
