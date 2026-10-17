# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param(
    [string]$ConfigPath = '',
    [string]$DataDir = '',
    [string]$InstanceName = 'AgentBaton.Watch.SingleInstance.v1',
    [string]$ProbeOutput = '',
    [string]$Screenshot = '',
    [string]$ScreenshotExpanded = '',
    [string]$ScreenshotHistory = '',
    [switch]$Expanded,
    [switch]$ExitAfterCapture,
    [switch]$Wait
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'CLI Watch requires Windows.' }
$exe = Join-Path $PSScriptRoot 'runtime/win-x64/CliWatch.App.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Run watch/Build-CliWatch.ps1 first.' }
if (-not $DataDir) { $DataDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AgentBatonWatch' }
$DataDir = [IO.Path]::GetFullPath($DataDir)
[IO.Directory]::CreateDirectory($DataDir) | Out-Null
if (-not $ConfigPath) {
    $template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'config/default.json'))
    $repoRoot = (Split-Path -Parent $PSScriptRoot).Replace('\', '/')
    $ConfigPath = Join-Path $DataDir 'default-config.json'
    [IO.File]::WriteAllText($ConfigPath, $template.Replace('${REPOSITORY_ROOT}', $repoRoot), [Text.UTF8Encoding]::new($false))
}
$ConfigPath = (Resolve-Path -LiteralPath $ConfigPath -ErrorAction Stop).Path
$argv = [Collections.Generic.List[string]]::new()
foreach ($item in @('--config', $ConfigPath, '--data-dir', $DataDir, '--instance-name', $InstanceName)) { $argv.Add($item) }
foreach ($pair in @(
    @('--probe-out', $ProbeOutput), @('--screenshot', $Screenshot),
    @('--screenshot-expanded', $ScreenshotExpanded), @('--screenshot-history', $ScreenshotHistory)
)) {
    if ($pair[1]) { $argv.Add($pair[0]); $argv.Add([IO.Path]::GetFullPath($pair[1])) }
}
if ($Expanded) { $argv.Add('--expanded') }
if ($ExitAfterCapture) { $argv.Add('--exit-after-capture') }
if ($Wait -or $ProbeOutput -or $ExitAfterCapture) {
    # Keep automated probes/captures in the caller's bounded process tree.
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $exe; $info.UseShellExecute = $false
    $info.WorkingDirectory = $PSScriptRoot
    foreach ($item in $argv) { [void]$info.ArgumentList.Add($item) }
    $process = [Diagnostics.Process]::Start($info)
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw ("CLI Watch exited " + $process.ExitCode) }
} else {
    # Explorer owns the interactive GUI, so an agent host ending does not close it.
    $quoted = foreach ($item in $argv) { '"' + ($item -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
    $shell = New-Object -ComObject Shell.Application
    $shell.ShellExecute($exe, ($quoted -join ' '), $PSScriptRoot, 'open', 1)
}
