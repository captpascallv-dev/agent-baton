param(
    [string]$ConfigPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'ai-moe-studio-state/config.json'),
    [switch]$NoOpen,
    [switch]$Demo
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Start-Studio.ps1 is Windows-only (COM, node.exe and Windows TCP cmdlets). Use the documented Node server entry point on macOS/Linux.' }
$studioRoot = $PSScriptRoot
try {
if ($Demo) {
    $studioPort = 4173
    $studioUrl = "http://127.0.0.1:$studioPort/"
    function Test-StudioDemo {
        try {
            $health = Invoke-RestMethod -Uri ($studioUrl + 'health') -TimeoutSec 2
            return $health.app -eq 'ai-moe-studio' -and $health.mode -eq 'demo' -and $health.schemaVersion -eq 1
        } catch { return $false }
    }
    if (-not (Test-StudioDemo)) {
        if (Get-NetTCPConnection -LocalPort $studioPort -State Listen -ErrorAction SilentlyContinue) {
            throw "端口 $studioPort 已被其他程序占用。演示模式可改用：npm.cmd run demo -- --port <空闲端口>。未停止任何现有程序。"
        }
        $studioNode = (Get-Command node.exe -ErrorAction Stop).Source
        if (-not (Test-Path -LiteralPath (Join-Path $studioRoot 'dist/client/index.html'))) { throw '研究室尚未构建，请先运行 npm.cmd run build。' }
        $studioLogRoot = Join-Path ([IO.Path]::GetTempPath()) 'ai-moe-studio-demo-logs'
        New-Item -ItemType Directory -Path $studioLogRoot -Force | Out-Null
        $studioProcess = Start-Process -FilePath $studioNode -ArgumentList @('server/live-server.mjs','--demo') -WorkingDirectory $studioRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $studioLogRoot "server-$studioPort.stdout.log") -RedirectStandardError (Join-Path $studioLogRoot "server-$studioPort.stderr.log") -PassThru
        $studioReady = $false
        for ($studioAttempt = 0; $studioAttempt -lt 40; $studioAttempt++) {
            if (Test-StudioDemo) { $studioReady = $true; break }
            if ($studioProcess.HasExited) { throw "研究室演示模式启动失败，请查看 $studioLogRoot 下的日志。" }
            Start-Sleep -Milliseconds 250
        }
        if (-not $studioReady) { throw "研究室演示模式未能及时启动，请查看 $studioLogRoot 下的日志。" }
    }
    if (-not $NoOpen) { Start-Process $studioUrl }
    Write-Output "AI娘研究室（演示数据）：$studioUrl"
} else {
if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "找不到研究室配置文件：$ConfigPath"
}
$studioConfigPath = (Resolve-Path -LiteralPath $ConfigPath).Path
$studioConfig = Get-Content -LiteralPath $studioConfigPath -Raw | ConvertFrom-Json
$studioPort = if ($null -ne $studioConfig.port) { [int]$studioConfig.port } else { 4173 }
if ($studioPort -lt 1 -or $studioPort -gt 65535) { throw '研究室配置中的端口无效。' }
$studioUrl = "http://127.0.0.1:$studioPort/"

function Test-StudioLive {
    try {
        $health = Invoke-RestMethod -Uri ($studioUrl + 'health') -TimeoutSec 2
        return $health.app -eq 'ai-moe-studio' -and $health.mode -eq 'live' -and $health.schemaVersion -eq 1
    } catch { return $false }
}

if (-not (Test-StudioLive)) {
    if (Get-NetTCPConnection -LocalPort $studioPort -State Listen -ErrorAction SilentlyContinue) {
        throw "端口 $studioPort 已被其他程序占用，请检查研究室配置；未停止任何现有程序。"
    }
    $studioNode = (Get-Command node.exe -ErrorAction Stop).Source
    if (-not (Test-Path -LiteralPath (Join-Path $studioRoot 'dist/client/index.html'))) { throw '研究室尚未构建，请先运行 npm.cmd run build。' }
    $studioLogRoot = Join-Path (Split-Path $studioConfigPath -Parent) 'logs'
    New-Item -ItemType Directory -Path $studioLogRoot -Force | Out-Null
    $studioProcess = Start-Process -FilePath $studioNode -ArgumentList @('server/live-server.mjs','--config',('"' + $studioConfigPath + '"')) -WorkingDirectory $studioRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $studioLogRoot "server-$studioPort.stdout.log") -RedirectStandardError (Join-Path $studioLogRoot "server-$studioPort.stderr.log") -PassThru
    $studioReady = $false
    for ($studioAttempt = 0; $studioAttempt -lt 40; $studioAttempt++) {
        if (Test-StudioLive) { $studioReady = $true; break }
        if ($studioProcess.HasExited) { throw "研究室启动失败，请查看 $studioLogRoot 下的日志。" }
        Start-Sleep -Milliseconds 250
    }
    if (-not $studioReady) { throw "研究室未能及时启动，请查看 $studioLogRoot 下的日志。" }
}
if (-not $NoOpen) { Start-Process $studioUrl }
Write-Output "AI娘研究室（CLI真实状态）：$studioUrl"
}
} catch {
    $studioFailure = $_.Exception.Message
    $studioFailureLog = Join-Path $studioRoot 'work/startup-error.log'
    New-Item -ItemType Directory -Path (Split-Path -Parent $studioFailureLog) -Force | Out-Null
    $studioFailureTime = [TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTimeOffset]::Now, 'Taipei Standard Time').ToString('o')
    Add-Content -LiteralPath $studioFailureLog -Value "$studioFailureTime $studioFailure" -Encoding UTF8
    if (-not $NoOpen) {
        $studioNotice = New-Object -ComObject WScript.Shell
        $studioNotice.Popup("研究室没有打开。`n`n$studioFailure`n`n错误记录：$studioFailureLog", 0, 'AI娘研究室', 16) | Out-Null
    }
    Write-Error -Message $studioFailure -ErrorAction Continue
    exit 1
}
