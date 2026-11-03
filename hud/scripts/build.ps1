$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -ProjectRoot $projectRoot
$solution = Join-Path $projectRoot 'CodexUsageHud.sln'

& $dotnet restore $solution --packages $env:NUGET_PACKAGES --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$projects = @(
    (Join-Path $projectRoot 'src\CodexUsageHud.Core\CodexUsageHud.Core.csproj'),
    (Join-Path $projectRoot 'src\CodexUsageHud.App\CodexUsageHud.App.csproj'),
    (Join-Path $projectRoot 'tests\CodexUsageHud.Tests\CodexUsageHud.Tests.csproj')
)
foreach ($project in $projects) {
    & $dotnet build $project -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
exit 0
