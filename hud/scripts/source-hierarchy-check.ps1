param(
    [string]$CodexHome = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -ProjectRoot $projectRoot
$testProject = Join-Path $projectRoot 'tests\CodexUsageHud.Tests\CodexUsageHud.Tests.csproj'

& $dotnet build $testProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$arguments = @('run', '--project', $testProject, '-c', 'Release', '--no-build', '--no-restore', '--',
    '--source-hierarchy-check')
if (-not [string]::IsNullOrWhiteSpace($CodexHome)) { $arguments += $CodexHome }
& $dotnet @arguments
exit $LASTEXITCODE
