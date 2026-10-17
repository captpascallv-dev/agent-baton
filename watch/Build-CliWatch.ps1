# SPDX-License-Identifier: MPL-2.0
[CmdletBinding()]
param([string]$SdkCommand = 'dotnet', [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'CLI Watch requires Windows and the .NET 8 SDK.' }
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'runtime/win-x64' }
$output = [IO.Path]::GetFullPath($OutputPath)
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'src'))
if ($output.Equals($source, [StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Build output must not overwrite source.'
}
$project = Join-Path $source 'CliWatch.App/CliWatch.App.csproj'
$sdk = (Get-Command $SdkCommand -ErrorAction Stop | Select-Object -First 1).Source
$publish = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-m:1', '-p:UseSharedCompilation=false',
    '-o', $output, '-p:DebugType=none', '-p:DebugSymbols=false', '-p:SatelliteResourceLanguages=en',
    ('-p:PathMap=' + $PSScriptRoot + '=/_/watch'))
& $sdk @publish
if ($LASTEXITCODE -ne 0) { throw 'CLI Watch build failed.' }
Write-Output ("Built: " + (Join-Path $output 'CliWatch.App.exe'))
