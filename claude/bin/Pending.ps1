# List dispatched jobs whose outbox has no READY.json yet, with the command to re-arm the watcher.
# Run once after the Claude session restarts. 会话重启后跑一次，重挂守候。
# jobs/<id>/CLOSED.txt means the lead closed that job on purpose; it is not re-armed.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = Split-Path -Parent $PSScriptRoot
$found = 0
Get-ChildItem (Join-Path $root 'jobs/*/job.json') -ErrorAction SilentlyContinue | ForEach-Object {
    if (Test-Path -LiteralPath (Join-Path $_.DirectoryName 'CLOSED.txt')) { return }
    $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
    if (Test-Path -LiteralPath (Join-Path $j.outbox 'READY.json')) { return }
    $found++
    $procId = 0
    if ($null -ne $j.host_pid -and "$($j.host_pid)" -ne '') { $procId = [int]$j.host_pid }
    $alive = $procId -gt 0 -and [bool](Get-Process -Id $procId -ErrorAction SilentlyContinue)
    $watchMinutes = 29
    if ($j.PSObject.Properties.Name -contains 'watch_minutes' -and $j.watch_minutes) { $watchMinutes = $j.watch_minutes }
    "PENDING $($j.job_id) | $($j.executor) | $($j.task) | pid=$procId alive=$alive | outbox=$($j.outbox)"
    "  WATCH: pwsh -NoProfile -File `"$PSScriptRoot/Wait-Delivery.ps1`" -JobDir `"$($j.outbox)`" -ProcessId $procId -TimeoutMinutes $watchMinutes -IntervalSeconds 20"
}
if ($found -eq 0) { 'NO PENDING JOBS' }
