param(
    [string]$CodexHome = '',
    [string]$PrivacySentinel = '__CUH_PRIVACY_SENTINEL__'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -ProjectRoot $projectRoot
$testProject = Join-Path $projectRoot 'tests\CodexUsageHud.Tests\CodexUsageHud.Tests.csproj'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [Globalization.CultureInfo]::InvariantCulture)
$databasePath = Join-Path $projectRoot ".artifacts\correction-04\real-session\$timestamp\usage.db"
$resolvedHome = if ([string]::IsNullOrWhiteSpace($CodexHome)) {
    Join-Path $projectRoot 'tests\fixtures\correction-04\real-session-home'
} else { $CodexHome }
$sourceKind = if ([string]::IsNullOrWhiteSpace($CodexHome)) { 'synthetic-correction-04' } else { 'explicit-local-home' }
New-Item -ItemType Directory -Force -Path (Split-Path $databasePath -Parent) | Out-Null
& $dotnet build $testProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "REAL_SESSION_SCOPE source=$sourceKind database=project-local-fresh"
& $dotnet run --project $testProject -c Release --no-build --no-restore -- --real-check $resolvedHome $databasePath $PrivacySentinel
exit $LASTEXITCODE
