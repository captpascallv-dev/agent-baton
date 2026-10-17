# SPDX-License-Identifier: MPL-2.0
# Build a self-contained Windows ZIP; never bundle user state or local configs.
[CmdletBinding()]
param([string]$SdkCommand = 'dotnet', [string]$OutputDirectory = '', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'CLI Watch packaging requires Windows.' }
$project = [xml][IO.File]::ReadAllText((Join-Path $PSScriptRoot 'src/CliWatch.App/CliWatch.App.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A three-part release version is required.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'runtime/releases' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$name = 'CLIWatch-' + $version + '-win-x64'
$stage = Join-Path $output $name
$archive = Join-Path $output ($name + '.zip')
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $archive)) {
    throw 'Release output already exists. Use a new output directory.'
}
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build-CliWatch.ps1') -SdkCommand $SdkCommand }
$runtime = Join-Path $PSScriptRoot 'runtime/win-x64'
foreach ($name in @('CliWatch.App.exe','CliWatch.App.dll','CliWatch.App.runtimeconfig.json','coreclr.dll','hostfxr.dll','PresentationFramework.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $runtime $name) -PathType Leaf)) { throw ('Missing self-contained runtime file: ' + $name) }
}
$builtVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $runtime 'CliWatch.App.dll')).ProductVersion
if ($builtVersion -notmatch ('^' + [regex]::Escape($version) + '(\+|$)')) { throw 'Runtime version does not match the release source. Rebuild first.' }
[IO.Directory]::CreateDirectory($stage) | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $runtime -File) {
    if ($file.Extension -notin @('.dll','.exe','.json')) { throw ('Unexpected runtime payload: ' + $file.Name) }
    Copy-Item -LiteralPath $file.FullName -Destination $stage
}
foreach ($name in @('Start-CLI-Watch.cmd','README.txt')) {
    # CMD stays ASCII/CRLF; the user-facing README is UTF-8 with BOM for Notepad.
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot ('portable/' + $name))).Replace("`r`n", "`n").Replace("`n", "`r`n")
    $encoding = if ($name.EndsWith('.cmd')) { [Text.Encoding]::ASCII } else { [Text.UTF8Encoding]::new($true) }
    [IO.File]::WriteAllText((Join-Path $stage $name), $source, $encoding)
}
[IO.Directory]::CreateDirectory((Join-Path $stage 'config')) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'portable/default.json') -Destination (Join-Path $stage 'config/default.json')
foreach ($name in @('LICENSE','NOTICE.txt')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $stage }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'src/licenses') -Destination (Join-Path $stage 'licenses') -Recurse
$tag = 'watch-v' + $version
$release = "CLI Watch $version`r`nWindows x64, self-contained .NET 8`r`nSource: https://github.com/captpascallv-dev/agent-baton/tree/$tag/watch`r`nLicense: MPL-2.0 (bundled dependencies retain their own terms)`r`n"
[IO.File]::WriteAllText((Join-Path $stage 'RELEASE.txt'), $release, [Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath $stage -DestinationPath $archive -CompressionLevel Optimal
Write-Output ('Portable ZIP: ' + $archive)
