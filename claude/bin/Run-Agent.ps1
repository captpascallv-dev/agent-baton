# Runs inside the detached process started by Dispatch.ps1. Do not call directly.
# 由 Dispatch.ps1 在后台进程里拉起：按 spec.json 执行 agent CLI，写日志和退出码。
param([Parameter(Mandatory = $true)][string]$JobDir)
$jobId = Split-Path -Leaf $JobDir
$startedAt = (Get-Process -Id $PID -ErrorAction Stop).StartTime.ToUniversalTime()
# Observation-only identity for watch/. Never store command lines or prompt text.
$ownerPath = Join-Path $JobDir 'owner.json'
if (Test-Path -LiteralPath $ownerPath) { throw 'Runner identity already exists; refusing to overwrite or repeat this job.' }
$owner = [ordered]@{
    protocol_version = 'agent-baton-runner-owner-v1'
    job_id = $jobId; attempt_id = '1'
    pid = $PID; start_time_utc_ticks = $startedAt.Ticks
    started_at_utc = $startedAt.ToString('o')
}
$ownerTemp = Join-Path $JobDir ("owner.json.$PID.tmp")
$owner | ConvertTo-Json | Set-Content -LiteralPath $ownerTemp -Encoding utf8 -ErrorAction Stop
Move-Item -LiteralPath $ownerTemp -Destination $ownerPath -ErrorAction Stop
$spec = Get-Content -LiteralPath (Join-Path $JobDir 'spec.json') -Raw | ConvertFrom-Json -AsHashtable
# UTF-8 both ways; otherwise non-ASCII prompts/logs get mangled on Windows (e.g. GBK code page).
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Text.UTF8Encoding]::new($false)
# A single-element JSON array can come back as a string. Wrap before iterating.
foreach ($name in @($spec['unset_env'])) {
    if ($name) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
}
foreach ($k in $spec.env.Keys) { if ($spec.env[$k]) { [Environment]::SetEnvironmentVariable($k, [string]$spec.env[$k]) } }
Set-Location -LiteralPath $spec.cwd
$log = Join-Path $JobDir 'agent.log'
$cmdArgs = @($spec.args)
if ($spec.stdin_card) {
    Get-Content -LiteralPath $spec.card -Raw | & $spec.command @cmdArgs *> $log
} else {
    & $spec.command @cmdArgs *> $log
}
$code = $LASTEXITCODE
"$code" | Set-Content (Join-Path $JobDir 'exit_code.txt')
# Atomic end record for operators. The default WATCH command does not treat this file as a terminal marker.
$term = [ordered]@{
    job_id = $jobId
    attempt_id = '1'
    pid = $PID
    start_time_utc_ticks = $startedAt.Ticks
    exit_code = $code
    finished_at_utc = (Get-Date).ToUniversalTime().ToString('o')
}
$tmp = Join-Path $JobDir 'terminal.json.tmp'
$term | ConvertTo-Json | Set-Content -LiteralPath $tmp -Encoding utf8
Move-Item -LiteralPath $tmp -Destination (Join-Path $JobDir 'terminal.json') -Force
