# 守着自己的收件箱，一来新信就退出（用来叫醒挂它的会话）。只看文件，不调模型，也不把信移走。
# 退出码：0 有未读信，2 超时没信。
# 用法：pwsh -NoProfile -File Wait-Mail.ps1 -Root <dir> -Parties a,b -Me a [-TimeoutMinutes 29] [-IntervalSeconds 15]
param(
    [string]$Root = '',
    [string]$Parties = '',
    [string]$PartiesFile = '',
    [Parameter(Mandatory = $true)][string]$Me,
    [double]$TimeoutMinutes = 29,
    [int]$IntervalSeconds = 15
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Mailbox-Common.ps1')
$ctx = Get-MailboxContext -Root $Root -Parties $Parties -PartiesFile $PartiesFile
Assert-MailboxParty -Name $Me -Parties $ctx.Parties
if ($IntervalSeconds -lt 1) { throw 'IntervalSeconds 至少为 1。' }
$inbox = Get-MailboxInbox -Root $ctx.Root -Party $Me
New-Item -ItemType Directory -Force -Path $inbox | Out-Null
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)

while ($true) {
    $mail = @(Get-ChildItem -LiteralPath $inbox -Filter '*.md' -File | Sort-Object Name)
    if ($mail.Count -gt 0) {
        "MAIL me=$Me unread=$($mail.Count)"
        $mail | ForEach-Object { "  $($_.Name)" }
        exit 0
    }
    if ((Get-Date) -ge $deadline) { "MAIL me=$Me timeout after_min=$TimeoutMinutes"; exit 2 }
    Start-Sleep -Seconds $IntervalSeconds
}
