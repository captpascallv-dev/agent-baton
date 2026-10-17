# SPDX-License-Identifier: MPL-2.0
# Exercise the extracted ZIP through its CMD entry point, without SDK/PS7 on PATH.
param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh test output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$extract = Join-Path $output 'extracted with spaces 中文'
Expand-Archive -LiteralPath $archive -DestinationPath $extract
$roots = @(Get-ChildItem -LiteralPath $extract -Directory)
if ($roots.Count -ne 1) { throw 'The ZIP must have one top-level directory.' }
$package = $roots[0].FullName
$exe = Join-Path $package 'CliWatch.App.exe'
$launcher = Join-Path $package 'Start-CLI-Watch.cmd'
$config = Join-Path $package 'config/default.json'
function Assert-That([bool]$value, [string]$message) { if (-not $value) { throw $message } }
foreach ($file in @('CliWatch.App.exe','Start-CLI-Watch.cmd','config/default.json','coreclr.dll','hostfxr.dll','PresentationFramework.dll','LICENSE','NOTICE.txt','licenses/DOTNET-LICENSE.txt','licenses/DOTNET-THIRD-PARTY-NOTICES.txt','README.txt','RELEASE.txt')) {
    Assert-That (Test-Path -LiteralPath (Join-Path $package $file) -PathType Leaf) ('Missing portable payload: ' + $file)
}
$defaults = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
Assert-That (@($defaults.claude_mailbox_roots).Count -eq 0 -and @($defaults.mailbox_roots).Count -eq 0) 'The ZIP must not contain user task roots.'
Assert-That ([IO.File]::ReadAllText((Join-Path $package 'CliWatch.App.runtimeconfig.json')).Contains('includedFrameworks')) 'The app is not self-contained.'
function Run-Portable([string[]]$Arguments, [string]$ExpectedFile) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = Join-Path $env:SystemRoot 'System32/cmd.exe'
    $quoted = foreach ($arg in @($launcher) + $Arguments) { '"' + $arg + '"' }
    $info.Arguments = '/d /s /c "' + ($quoted -join ' ') + '"'
    $info.WorkingDirectory = $output # Deliberately outside the extracted package.
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
    $info.Environment['DOTNET_ROOT'] = Join-Path $output 'no-dotnet-installed'
    $info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    foreach ($key in @('CLI_WATCH_CONFIG','CLI_WATCH_DATA_DIR','CLI_WATCH_INSTANCE_NAME')) { [void]$info.Environment.Remove($key) }
    $process = [Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'Portable CMD launcher failed.' }
    $deadline = (Get-Date).AddSeconds(35)
    while (-not (Test-Path -LiteralPath $ExpectedFile -PathType Leaf)) {
        if ((Get-Date) -ge $deadline) { throw ('Portable app did not produce ' + $ExpectedFile) }
        Start-Sleep -Milliseconds 100
    }
    # Only wait for the executable from this newly extracted package.
    foreach ($app in @(Get-Process -Name CliWatch.App -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })) {
        # Retain the handle before exit; Get-Process may otherwise expose no exit code.
        $null = $app.Handle
        if (-not $app.WaitForExit(15000)) { throw 'The isolated portable app did not exit after capture/probe.' }
        if ($null -ne $app.ExitCode -and $app.ExitCode -ne 0) { throw ('The isolated portable app exited with code ' + $app.ExitCode) }
    }
}
$probe = Join-Path $output 'default-probe.json'
Run-Portable @('--probe-out', $probe) $probe
$result = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
Assert-That ($result.probe -eq 'cli-watch-readonly' -and -not $result.wrote_sources -and $result.claude_jobs_read -eq 0) 'Default portable configuration was not loaded correctly.'
$claude = Join-Path $output 'example agent-baton/claude'
$job = Join-Path $claude 'jobs/portable-example'
$outbox = Join-Path $output 'example delivery'
[IO.Directory]::CreateDirectory($job) | Out-Null
[IO.Directory]::CreateDirectory($outbox) | Out-Null
@{
    job_id='portable-example'; project='portable-demo'; task='Portable package example'; executor='pi'
    workspace=$output; outbox=$outbox; host_pid=2147483000; dispatched_at=[DateTimeOffset]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $job 'job.json') -Encoding utf8
@{status='completed'; deliverables=@('REPORT.md')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outbox 'READY.json') -Encoding utf8
'PORTABLE_PRIVATE_BODY_SENTINEL' | Set-Content -LiteralPath (Join-Path $outbox 'REPORT.md') -Encoding utf8
$defaults.claude_mailbox_roots = @($claude)
$defaults | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config -Encoding utf8
$configuredProbe = Join-Path $output 'configured-probe.json'
Run-Portable @('--probe-out', $configuredProbe) $configuredProbe
$result = Get-Content -LiteralPath $configuredProbe -Raw | ConvertFrom-Json
Assert-That ($result.claude_jobs_read -eq 1 -and $result.claude_registrations[0].job_id -eq 'portable-example') 'Editing the extracted config did not bind the public task directory.'
Assert-That (-not ([IO.File]::ReadAllText($configuredProbe)).Contains('PORTABLE_PRIVATE_BODY_SENTINEL')) 'A report body entered collection.'
$current = Join-Path $output 'current.png'
$details = Join-Path $output 'details.png'
$history = Join-Path $output 'history.png'
Run-Portable @('--data-dir', (Join-Path $output 'window data'), '--instance-name', ('PortableSmoke.' + [guid]::NewGuid().ToString('N')), '--screenshot', $current, '--screenshot-expanded', $details, '--screenshot-history', $history, '--exit-after-capture') $history
foreach ($file in @($current,$details,$history)) { Assert-That ((Get-Item -LiteralPath $file).Length -gt 100) 'A portable WPF render is missing.' }
Assert-That (-not (Test-Path -LiteralPath (Join-Path $output 'window data/ui-error.txt'))) 'The portable app reported a UI error.'
Write-Output 'PORTABLE SMOKE PASS: extracted Unicode/spaced path; CMD startup; no SDK/PS7 on PATH; included runtime; empty defaults; configured public task; body exclusion; actual WPF renders.'
