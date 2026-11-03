# Read-only independent reconciliation of HUD lineage/cycle totals.
# Encodes tuple/legacy identity, parent resolution, winner order, and
# raw/canonical totals separately from production lineage helpers. Uses only
# allowlisted token columns and parent metadata. Does not mutate the DB.
# Hosted checker uses complete schema-9 and schema-10 SELECT ... FROM forms.
# Hosted on net8 because Windows PowerShell 5.1 cannot load Microsoft.Data.Sqlite.
param(
    [string]$DatabasePath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    $DatabasePath = Join-Path $env:LOCALAPPDATA 'CodexUsageHUD\usage.db'
}

function Write-Result {
    param([string]$Status, [hashtable]$Fields)
    $parts = @("LINEAGE_CYCLE_CHECK status=$Status")
    foreach ($key in @('schema', 'samples', 'canonical_stored', 'canonical_independent',
            'stored_canonical_total', 'independent_total', 'raw_sample_total', 'roots',
            'cyclic_isolated', 'mismatch', 'cycle_independent_total', 'db')) {
        if ($Fields.ContainsKey($key)) { $parts += "$key=$($Fields[$key])" }
    }
    Write-Output ($parts -join ' ')
}

function Resolve-PinnedDotnet {
    $candidates = @(
        (Join-Path $projectRoot '.tools\dotnet\dotnet.exe'),
        (Join-Path $projectRoot '..\..\..\.tools\dotnet\dotnet.exe')
    )
    foreach ($candidate in $candidates) {
        $resolved = [IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath $resolved -PathType Leaf) { return $resolved }
    }
    $command = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $command) {
        $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
    }
    if ($null -eq $command) {
        throw 'dotnet_sdk_missing: install .NET SDK 8.0.423 or place it under .tools\dotnet'
    }
    return $command.Source
}

try {
    $dotnet = Resolve-PinnedDotnet
}
catch {
    Write-Result 'UNAVAILABLE' @{ db = 'dotnet-sdk-missing'; mismatch = 1 }
    exit 2
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$dotnetRoot = Split-Path -Parent $dotnet
$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$cliHome = Join-Path (Split-Path -Parent $dotnetRoot) 'cli-home'
$nuget = Join-Path (Split-Path -Parent $dotnetRoot) 'nuget'
if (Test-Path -LiteralPath $cliHome) { $env:DOTNET_CLI_HOME = $cliHome }
if (Test-Path -LiteralPath $nuget) { $env:NUGET_PACKAGES = $nuget }

$testProject = Join-Path $projectRoot 'tests\CodexUsageHud.Tests\CodexUsageHud.Tests.csproj'
& $dotnet build $testProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Result 'UNAVAILABLE' @{ db = 'build-failed'; mismatch = 1 }
    exit 2
}

& $dotnet run --project $testProject -c Release --no-build --no-restore -- --lineage-cycle-check $DatabasePath
exit $LASTEXITCODE
