# 往某个参与方的收件箱投一封信。写临时文件后原子改名，对方不会读到半截。
# 用法：pwsh -NoProfile -File Send-Mail.ps1 -Root <dir> -Parties a,b -From a -To b -Subject "标题" (-Body "正文" | -BodyFile <路径>)
# 正好两方且省略 -To 时，投给另一方。根目录也可用环境变量 AGENT_MAILBOX_ROOT。
param(
    [string]$Root = '',
    [string]$Parties = '',
    [string]$PartiesFile = '',
    [Parameter(Mandatory = $true)][string]$From,
    [string]$To = '',
    [Parameter(Mandatory = $true)][string]$Subject,
    [string]$Body,
    [string]$BodyFile,
    [string]$Thread = '',
    [string]$ReplyTo = '',
    [switch]$NeedsReply
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
. (Join-Path $PSScriptRoot 'Mailbox-Common.ps1')
$ctx = Get-MailboxContext -Root $Root -Parties $Parties -PartiesFile $PartiesFile
Assert-MailboxParty -Name $From -Parties $ctx.Parties
if (-not $To) {
    if ($ctx.Parties.Count -ne 2) { throw '-To 不能省略：参与方不是正好两方。' }
    $others = @($ctx.Parties | Where-Object { $_ -ne $From })
    if (@($others).Count -ne 1) { throw '-To 不能省略。' }
    $To = [string]($others[0])
}
Assert-MailboxParty -Name $To -Parties $ctx.Parties
if ($BodyFile) {
    if (-not (Test-Path -LiteralPath $BodyFile -PathType Leaf)) { throw "找不到正文文件：$BodyFile" }
    $Body = Get-Content -LiteralPath $BodyFile -Raw -Encoding utf8
}
if (-not $Body) { throw '没有正文：用 -Body 或 -BodyFile。' }

function Repair-MailLine([string]$Value) { (($Value -replace '[\r\n]+', ' ').Trim()) }

$From = Repair-MailLine $From
$To = Repair-MailLine $To
$Subject = Repair-MailLine $Subject
$Thread = Repair-MailLine $Thread
$ReplyTo = Repair-MailLine $ReplyTo
$slug = ($Subject -replace '[\\/:*?"<>|\s]+', '-').Trim('-')
if (-not $slug) { $slug = 'note' }
if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40).Trim('-') }
$inbox = Get-MailboxInbox -Root $ctx.Root -Party $To
New-Item -ItemType Directory -Force -Path $inbox | Out-Null

$name = $null
$now = [DateTime]::UtcNow
foreach ($try in 1..5) {
    $now = [DateTime]::UtcNow
    $candidate = '{0}_{1}_{2}.md' -f $now.ToString('yyyyMMddTHHmmssfffZ'), $From, $slug
    if (-not (Test-Path -LiteralPath (Join-Path $inbox $candidate))) { $name = $candidate; break }
    Start-Sleep -Milliseconds 2
}
if (-not $name) { throw '信件文件名冲突，稍后再试。' }
$needs = if ($NeedsReply) { 'true' } else { 'false' }
$header = @(
    '---'
    "from: $From"
    "to: $To"
    "subject: $Subject"
    "created_at_utc: $($now.ToString('yyyy-MM-ddTHH:mm:ssZ'))"
    "thread: $Thread"
    "reply_to: $ReplyTo"
    "needs_reply: $needs"
    '---'
    ''
) -join "`n"
$tmp = Join-Path $inbox ".$name.tmp"
Set-Content -LiteralPath $tmp -Value ($header + $Body.TrimEnd() + "`n") -Encoding utf8 -NoNewline
Move-Item -LiteralPath $tmp -Destination (Join-Path $inbox $name)
"SENT to=$To file=$name"
