# 读出自己收件箱里的全部未读信，并移到 read\ 归档。加 -Peek 只看不归档。
# 用法：pwsh -NoProfile -File Read-Mail.ps1 -Root <dir> -Parties a,b -Me a [-Peek]
param(
    [string]$Root = '',
    [string]$Parties = '',
    [string]$PartiesFile = '',
    [Parameter(Mandatory = $true)][string]$Me,
    [switch]$Peek
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Mailbox-Common.ps1')
$ctx = Get-MailboxContext -Root $Root -Parties $Parties -PartiesFile $PartiesFile
Assert-MailboxParty -Name $Me -Parties $ctx.Parties
$inbox = Get-MailboxInbox -Root $ctx.Root -Party $Me
$read = Join-Path $inbox 'read'
New-Item -ItemType Directory -Force -Path $inbox, $read | Out-Null

$mail = @(Get-ChildItem -LiteralPath $inbox -Filter '*.md' -File | Sort-Object Name)
if ($mail.Count -eq 0) { "NO-MAIL me=$Me"; exit 0 }
foreach ($m in $mail) {
    "===== $($m.Name)"
    Get-Content -LiteralPath $m.FullName -Raw -Encoding utf8
    if (-not $Peek) { Move-Item -LiteralPath $m.FullName -Destination (Join-Path $read $m.Name) }
}
