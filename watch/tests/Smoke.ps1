# SPDX-License-Identifier: MPL-2.0
# Local fake PI process + real Dispatch + real WPF collector. No model/network.
param([string]$CaptureDirectory = '', [switch]$KeepTemp)
$ErrorActionPreference = 'Stop'
$watch = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $watch
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$node = (Get-Command node -ErrorAction Stop).Source
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$temp = Join-Path $tempBase ('agent-baton-watch-smoke-' + [guid]::NewGuid().ToString('N'))
$processes = [Collections.Generic.List[object]]::new()
function Assert-That([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Write-Json([string]$path, $value) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
}
function Wait-File([string]$path) {
    $deadline = (Get-Date).AddSeconds(35)
    while (-not (Test-Path -LiteralPath $path)) {
        if ((Get-Date) -ge $deadline) { throw ("Fixture timed out: " + $path) }
        Start-Sleep -Milliseconds 50
    }
}
function Start-Fixture([string]$id, [int]$delay) {
    $outbox = Join-Path $temp ('out-' + $id)
    $config = Join-Path $temp ('executor-' + $id + '.json')
    Write-Json $config @{ executors = @{ pi = @{
        command = $node
        args = @($entry, '--outbox', '{outbox}', '--delay-ms', $delay.ToString(), '--marker', '{jobdir}/mock-started.json', '--model', 'demo-model', '--thinking', 'high')
    } } }
    & $pwsh -NoProfile -File (Join-Path $sandbox 'bin/Dispatch.ps1') -Executor pi -Workspace $temp -Outbox $outbox -PromptFile $card -Task ("Local example: " + $id) -Project demo -JobId $id -Config $config | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Fixture dispatch failed.' }
    $jobDir = Join-Path $sandbox ('jobs/' + $id)
    Wait-File (Join-Path $jobDir 'mock-started.json')
    $job = Get-Content -LiteralPath (Join-Path $jobDir 'job.json') -Raw | ConvertFrom-Json
    $process = Get-Process -Id $job.host_pid -ErrorAction SilentlyContinue
    if ($process) { $processes.Add(@{ Id = $process.Id; StartTime = $process.StartTime }) }
    return @{ Directory = $jobDir; Outbox = $outbox; Job = $job }
}
try {
    [IO.Directory]::CreateDirectory($temp) | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp '.owner'), 'agent-baton-watch-smoke')
    $sandbox = Join-Path $temp 'claude'
    [IO.Directory]::CreateDirectory((Join-Path $sandbox 'bin')) | Out-Null
    foreach ($name in @('Dispatch.ps1','Run-Agent.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repo ('claude/bin/' + $name)) -Destination (Join-Path $sandbox ('bin/' + $name))
    }
    $entry = Join-Path $temp 'node_modules/@earendil-works/pi-coding-agent/dist/cli.js'
    [IO.Directory]::CreateDirectory((Split-Path -Parent $entry)) | Out-Null
    $mock = @'
const fs = require('node:fs'), path = require('node:path');
const option = name => process.argv[process.argv.indexOf(name) + 1];
const outbox = option('--outbox');
fs.writeFileSync(option('--marker'), JSON.stringify({pid:process.pid}));
setTimeout(() => {
  fs.mkdirSync(outbox,{recursive:true});
  fs.writeFileSync(path.join(outbox,'REPORT.md'),'SYNTHETIC_BODY_SENTINEL');
  fs.writeFileSync(path.join(outbox,'READY.json'),JSON.stringify({status:'completed',deliverables:['REPORT.md']}));
},Number(option('--delay-ms')));
'@
    [IO.File]::WriteAllText($entry, $mock)
    $card = Join-Path $temp 'task card 中文.md'
    [IO.File]::WriteAllText($card, 'SYNTHETIC_BODY_SENTINEL')
    $config = Join-Path $temp 'watch config 中文.json'
    Write-Json $config @{
        mailbox_roots = @(); claude_mailbox_roots = @($sandbox); grok_home = (Join-Path $temp 'no-grok')
        registry_paths = @(); additional_source_paths = @(); max_files_per_refresh = 512
        process_discovery_rules = @(@{ cli='PI'; image='node.exe'; command_contains=@($temp); script_packages=@('@earendil-works/pi-coding-agent') })
    }
    $done = Start-Fixture 'completed' 0
    Wait-File (Join-Path $done.Directory 'terminal.json')
    $running = Start-Fixture 'running' 20000
    $probe = Join-Path $temp 'probe.json'
    & $pwsh -NoProfile -File (Join-Path $watch 'Start-CliWatch.ps1') -ConfigPath $config -DataDir (Join-Path $temp 'ui-data') -ProbeOutput $probe
    if ($LASTEXITCODE -ne 0) { throw 'CLI Watch probe failed.' }
    $data = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    Assert-That ($data.claude_jobs_read -eq 2) 'The public jobs/<id>/job.json records were not both observed.'
    $registration = @($data.claude_registrations | Where-Object { $_.job_id -eq 'running' })
    Assert-That ($registration.Count -eq 1 -and $registration[0].attempt_id -eq '1') 'Public directory identity was not preserved.'
    $binding = @($data.processes | Where-Object { $_.job_key -eq 'running--1' -and $_.cli_alive })
    Assert-That ($binding.Count -eq 1) 'The real fake-PI child was not bound to its runner identity.'
    Assert-That (@($data.projects | Where-Object { $_.phase -eq 'delivered' }).Count -eq 1) 'READY was not represented as delivery.'
    Assert-That (-not ([IO.File]::ReadAllText($probe)).Contains('SYNTHETIC_BODY_SENTINEL')) 'A task/report body entered collected metadata.'
    if ($CaptureDirectory) {
        $CaptureDirectory = [IO.Path]::GetFullPath($CaptureDirectory)
        [IO.Directory]::CreateDirectory($CaptureDirectory) | Out-Null
        & $pwsh -NoProfile -File (Join-Path $watch 'Start-CliWatch.ps1') -ConfigPath $config -DataDir (Join-Path $temp 'capture-data') -InstanceName ('WatchSmoke.' + [guid]::NewGuid().ToString('N')) -Screenshot (Join-Path $CaptureDirectory 'current.png') -ScreenshotExpanded (Join-Path $CaptureDirectory 'details.png') -ScreenshotHistory (Join-Path $CaptureDirectory 'history.png') -ExitAfterCapture
        if ($LASTEXITCODE -ne 0) { throw 'CLI Watch capture failed.' }
        foreach ($image in @('current.png','details.png','history.png')) {
            Assert-That (Test-Path -LiteralPath (Join-Path $CaptureDirectory $image)) ("Missing render: " + $image)
        }
    }
    Wait-File (Join-Path $running.Directory 'terminal.json')
    $owner = Get-Content -LiteralPath (Join-Path $running.Directory 'owner.json') -Raw | ConvertFrom-Json
    $terminal = Get-Content -LiteralPath (Join-Path $running.Directory 'terminal.json') -Raw | ConvertFrom-Json
    Assert-That ($owner.pid -eq $terminal.pid -and $owner.start_time_utc_ticks -eq $terminal.start_time_utc_ticks) 'Runner terminal lost its birth identity.'
    Write-Output 'SMOKE PASS: real Dispatch, both flat jobs, exact live CLI child, READY, bounded metadata, optional WPF renders.'
} finally {
    foreach ($identity in $processes) {
        $process = Get-Process -Id $identity.Id -ErrorAction SilentlyContinue
        if ($process -and $process.StartTime -eq $identity.StartTime) { $process.Kill($true); $process.WaitForExit() }
    }
    if (-not $KeepTemp -and (Test-Path -LiteralPath $temp)) {
        $resolved = (Get-Item -LiteralPath $temp).FullName
        Assert-That ([IO.Path]::GetDirectoryName($resolved) -eq $tempBase -and [IO.Path]::GetFileName($resolved).StartsWith('agent-baton-watch-smoke-')) 'Unexpected cleanup directory.'
        Assert-That (((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Fixture is a reparse point.'
        Assert-That ([IO.File]::ReadAllText((Join-Path $resolved '.owner')) -eq 'agent-baton-watch-smoke') 'Fixture ownership is missing.'
        [IO.Directory]::Delete($resolved, $true)
    }
}
