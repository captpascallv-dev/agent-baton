$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -ProjectRoot $projectRoot
$testProject = Join-Path $projectRoot 'tests\CodexUsageHud.Tests\CodexUsageHud.Tests.csproj'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [Globalization.CultureInfo]::InvariantCulture)
$diagnosticRoot = Join-Path $projectRoot ".artifacts\refresh-diagnostic\$timestamp"

New-Item -ItemType Directory -Force -Path $diagnosticRoot | Out-Null
& $dotnet build $testProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet run --project $testProject -c Release --no-build --no-restore -- --refresh-diagnostic $diagnosticRoot
exit $LASTEXITCODE
