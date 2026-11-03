# Watch several dispatched jobs with ONE background command; exit as soon as any of them reaches a terminal state.
# 一个守候盯多单：任意一单交卷或执行者结束就退出（叫醒 Lead），其余的再挂一轮即可。
#
# In a PowerShell session (or a pwsh -Command script block), pass actual arrays:
#   & ./Wait-Many.ps1 -Outbox @('<dirA>', '<dirB>') -ProcessId @(111, 222) -TimeoutMinutes 720
# pwsh -File cannot bind an array parameter from comma-separated command-line strings.
#
# Exit codes: 0 some READY.json appeared, 4 some executor process is gone without READY, 2 timed out.
param(
    [Parameter(Mandatory = $true)][string[]]$Outbox,
    [int[]]$ProcessId = @(),          # same order as -Outbox; 0 or omitted = don't watch that process
    [double]$TimeoutMinutes = 720,
    [int]$IntervalSeconds = 20
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true) {
    for ($i = 0; $i -lt $Outbox.Count; $i++) {
        if (Test-Path -LiteralPath (Join-Path $Outbox[$i] 'READY.json')) { "READY $($Outbox[$i])"; exit 0 }
        $procId = if ($i -lt $ProcessId.Count) { $ProcessId[$i] } else { 0 }
        if ($procId -gt 0 -and -not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) {
            Start-Sleep -Seconds 5
            if (-not (Test-Path -LiteralPath (Join-Path $Outbox[$i] 'READY.json'))) { "GONE-WITHOUT-READY $($Outbox[$i]) pid=$procId"; exit 4 }
        }
    }
    if ((Get-Date) -ge $deadline) { "TIMEOUT after_min=$TimeoutMinutes"; exit 2 }
    Start-Sleep -Seconds $IntervalSeconds
}
