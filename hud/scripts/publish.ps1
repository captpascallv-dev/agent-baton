param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
$dotnet = & (Join-Path $PSScriptRoot 'resolve-dotnet.ps1') -ProjectRoot $projectRoot
$appProject = Join-Path $projectRoot 'src\CodexUsageHud.App\CodexUsageHud.App.csproj'
$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff', [Globalization.CultureInfo]::InvariantCulture)
$distRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'dist'))
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".artifacts\release-staging\$timestamp"))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $stagingRoot 'CodexUsageHUD-win-x64'))
$zipPath = [IO.Path]::GetFullPath((Join-Path $distRoot 'CodexUsageHUD-win-x64.zip'))
$zipHashPath = [IO.Path]::GetFullPath((Join-Path $distRoot 'CodexUsageHUD-win-x64.zip.sha256'))
$publicAcceptancePath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'docs\PACKAGE_ACCEPTANCE.md'))
$publicSpecPath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'docs\TECHNICAL_SPEC.md'))
$publicImagesPath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'docs\images'))
$licensePath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'LICENSE'))
$thirdPartyNoticesPath = [IO.Path]::GetFullPath((Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md'))
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".artifacts\package-smoke\$timestamp"))
$verifyRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".artifacts\package-verify\$timestamp"))
$distPrefix = $distRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$stagingPrefix = $stagingRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $publishRoot.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $zipPath.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $zipHashPath.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'publish_target_outside_allowed_roots'
}

function Get-PackageRelativePath([string]$root, [string]$file) {
    $prefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'manifest_path_outside_package'
    }
    return $file.Substring($prefix.Length).Replace('\', '/')
}

function Test-InternalManifest([string]$root) {
    $manifestPath = Join-Path $root 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'internal_manifest_missing'
    }
    $entries = @{}
    foreach ($line in Get-Content -LiteralPath $manifestPath) {
        if ($line -notmatch '^([0-9A-F]{64})  (.+)$') { throw 'internal_manifest_format' }
        $entries[$Matches[2]] = $Matches[1]
    }
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath })
    if ($entries.Count -ne $files.Count) { throw 'internal_manifest_count' }
    foreach ($file in $files) {
        $relative = Get-PackageRelativePath $root $file.FullName
        if (-not $entries.ContainsKey($relative)) { throw 'internal_manifest_entry_missing' }
        $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($entries[$relative] -ne $actual) { throw 'internal_manifest_hash_mismatch' }
    }
}

function Test-FileContainsText([string]$path, [string]$needle) {
    if ([string]::IsNullOrEmpty($needle)) { return $false }
    $encoding = New-Object Text.UTF8Encoding($false, $false)
    $reader = New-Object IO.StreamReader($path, $encoding, $true, 65536)
    try {
        $buffer = New-Object char[] 65536
        $tail = ''
        while (($read = $reader.ReadBlock($buffer, 0, $buffer.Length)) -gt 0) {
            $chunk = $tail + [string]::new($buffer, 0, $read)
            if ($chunk.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
            $tailLength = [Math]::Min([Math]::Max(0, $needle.Length - 1), $chunk.Length)
            $tail = if ($tailLength -eq 0) { '' } else { $chunk.Substring($chunk.Length - $tailLength) }
        }
        return $false
    }
    finally {
        $reader.Dispose()
    }
}

function Test-PackagePrivacy([string]$root) {
    $privateMarkers = @($env:USERPROFILE, $projectRoot) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        foreach ($marker in $privateMarkers) {
            if (Test-FileContainsText $file.FullName $marker) {
                throw "package_private_path:$($file.Name)"
            }
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.Extension -in '.md', '.txt' }) {
        $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        if ($text -match '(?i)C:\\Users\\[^\\\s`]+' -or
            $text -match '(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b') {
            throw "package_private_identity:$($file.Name)"
        }
    }
}

if (-not (Test-Path -LiteralPath $publicAcceptancePath -PathType Leaf)) { throw 'public_acceptance_missing' }
if (-not (Test-Path -LiteralPath $publicSpecPath -PathType Leaf)) { throw 'public_spec_missing' }
if (-not (Test-Path -LiteralPath $publicImagesPath -PathType Container)) { throw 'public_images_missing' }
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) { throw 'license_missing' }
if (-not (Test-Path -LiteralPath $thirdPartyNoticesPath -PathType Leaf)) { throw 'third_party_notices_missing' }
New-Item -ItemType Directory -Force -Path (Split-Path $publishRoot -Parent) | Out-Null
if (Test-Path -LiteralPath $publishRoot) { Remove-Item -LiteralPath $publishRoot -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $zipHashPath) { Remove-Item -LiteralPath $zipHashPath -Force }

if (-not $SkipBuild) {
    & $dotnet build $appProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $dotnet publish $appProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false -p:DebugType=None -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-ChildItem -LiteralPath $publishRoot -Filter *.pdb -File | Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $publishRoot 'README.md') -Force
Copy-Item -LiteralPath $publicAcceptancePath -Destination (Join-Path $publishRoot 'ACCEPTANCE_EVIDENCE.md') -Force
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $publishRoot 'LICENSE.txt') -Force
Copy-Item -LiteralPath $thirdPartyNoticesPath -Destination (Join-Path $publishRoot 'THIRD_PARTY_NOTICES.md') -Force
$packageDocsPath = Join-Path $publishRoot 'docs'
$packageImagesPath = Join-Path $packageDocsPath 'images'
New-Item -ItemType Directory -Force -Path $packageImagesPath | Out-Null
Copy-Item -LiteralPath $publicSpecPath -Destination (Join-Path $packageDocsPath 'TECHNICAL_SPEC.md') -Force
Copy-Item -LiteralPath (Join-Path $publicImagesPath 'hud-expanded.png') -Destination $packageImagesPath -Force
Copy-Item -LiteralPath (Join-Path $publicImagesPath 'hud-collapsed.png') -Destination $packageImagesPath -Force
Test-PackagePrivacy $publishRoot
$manifest = Join-Path $publishRoot 'SHA256SUMS.txt'
$lines = foreach ($file in Get-ChildItem -LiteralPath $publishRoot -Recurse -File |
    Where-Object { $_.FullName -ne $manifest } | Sort-Object FullName) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    $relative = Get-PackageRelativePath $publishRoot $file.FullName
    "$hash  $relative"
}
$lines | Set-Content -LiteralPath $manifest -Encoding ascii
Test-InternalManifest $publishRoot

$executable = Join-Path $publishRoot 'CodexUsageHud.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'published_executable_missing' }
$smokeData = Join-Path $smokeRoot 'app-data'
$smokeCodexHome = Join-Path $smokeRoot 'codex-home'
New-Item -ItemType Directory -Force -Path $smokeData | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $smokeCodexHome 'sessions') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $smokeCodexHome 'archived_sessions') | Out-Null
$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = $executable
$startInfo.Arguments = "--data-dir `"$smokeData`" --qa-offscreen --qa-exit-after-ms 4000"
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$startInfo.EnvironmentVariables['CODEX_HOME'] = $smokeCodexHome
$process = [Diagnostics.Process]::Start($startInfo)
$exitedCleanly = $null -ne $process -and $process.WaitForExit(15000)
$launchOk = $exitedCleanly -and $process.ExitCode -eq 0 -and
    (Test-Path -LiteralPath (Join-Path $smokeData 'usage.db') -PathType Leaf)
if ($null -ne $process -and -not $process.HasExited) {
    Stop-Process -Id $process.Id -Force
    $null = $process.WaitForExit(2000)
}
if (-not $launchOk) { exit 2 }

Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zipPath -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToUpperInvariant()
"$zipHash  CodexUsageHUD-win-x64.zip" | Set-Content -LiteralPath $zipHashPath -Encoding ascii

$extracted = Join-Path $verifyRoot 'CodexUsageHUD-win-x64'
New-Item -ItemType Directory -Force -Path $extracted | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $extracted
Test-InternalManifest $extracted
$sidecar = (Get-Content -LiteralPath $zipHashPath -Raw).Trim()
if ($sidecar -ne "$zipHash  CodexUsageHUD-win-x64.zip") { throw 'external_zip_hash_mismatch' }
if ((Get-Content -LiteralPath $manifest -Raw).Contains('CodexUsageHUD-win-x64.zip')) {
    throw 'zip_hash_self_reference'
}

$signature = (Get-AuthenticodeSignature -LiteralPath $executable).Status
Write-Output "PUBLISH staging=.artifacts\release-staging\$timestamp\CodexUsageHUD-win-x64"
Write-Output "ZIP path=dist\CodexUsageHUD-win-x64.zip hash=$zipHash"
Write-Output 'ZIP_HASH path=dist\CodexUsageHUD-win-x64.zip.sha256 self_referential=False'
Write-Output "INTERNAL_MANIFEST passed=True entries=$($lines.Count)"
Write-Output 'PACKAGE_PRIVACY passed=True private_paths=False stable_session_ids=False'
Write-Output "LAUNCH_SMOKE passed=$launchOk data_scope=.artifacts\package-smoke"
Write-Output "SIGNATURE status=$signature unsigned_caveat_documented=True"
