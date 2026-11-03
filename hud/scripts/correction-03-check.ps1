$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -ProjectRoot $projectRoot
$testProject = Join-Path $projectRoot 'tests\CodexUsageHud.Tests\CodexUsageHud.Tests.csproj'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [Globalization.CultureInfo]::InvariantCulture)
$evidenceRoot = Join-Path $projectRoot ".artifacts\correction-03\check\$timestamp"
$evidencePath = Join-Path $evidenceRoot 'correction-03-check.txt'

New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null

& $dotnet build $testProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = @(& $dotnet run --project $testProject -c Release --no-build --no-restore -- --correction-03-check 2>&1)
$runExitCode = $LASTEXITCODE
$output | Tee-Object -FilePath $evidencePath
if ($runExitCode -ne 0) { exit $runExitCode }

$relativeEvidence = $evidencePath.Substring($projectRoot.Length).TrimStart('\')
Write-Output "CORRECTION_03_EVIDENCE path=$relativeEvidence"
exit 0
