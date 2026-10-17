# Offline mailbox smoke. No Pester, model, credentials, or network.
param([switch]$KeepTemp)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sourceRoot = Split-Path -Parent $PSScriptRoot
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
$checks = [Collections.Generic.List[string]]::new()
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ('agent-mailbox smoke ' + [guid]::NewGuid().ToString('N'))
$owner = [guid]::NewGuid().ToString('N')
$ownerFile = Join-Path $tempRoot '.smoke-owner'

function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Pass([string]$Name) {
    $checks.Add($Name)
    "PASS $Name"
}
function Invoke-Pwsh([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $pwsh
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) {
        $process.Kill($true)
        throw 'Mailbox smoke subprocess exceeded 60 seconds.'
    }
    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    }
    $process.Dispose()
    Assert-That ($result.ExitCode -eq $ExpectedExit) "Expected exit $ExpectedExit, got $($result.ExitCode): $($result.Output)"
    return $result
}
function Invoke-Script([string]$Path, [string[]]$Arguments = @(), [int]$ExpectedExit = 0) {
    Invoke-Pwsh -Arguments (@('-NoProfile', '-File', $Path) + $Arguments) -ExpectedExit $ExpectedExit
}

try {
    foreach ($script in Get-ChildItem -LiteralPath (Join-Path $sourceRoot 'bin') -Filter '*.ps1') {
        $tokens = $null; $errors = $null
        [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
        Assert-That ($errors.Count -eq 0) "Syntax errors in $($script.Name): $errors"
    }
    $exampleParties = Get-Content -LiteralPath (Join-Path $sourceRoot 'parties.example.json') -Raw -Encoding utf8 | ConvertFrom-Json
    Assert-That (@($exampleParties.parties).Count -ge 2) 'parties.example.json needs at least two parties.'
    Pass 'syntax and parties example'

    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Set-Content -LiteralPath $ownerFile -Value $owner -Encoding utf8
    $root = Join-Path $tempRoot 'box'
    $send = Join-Path $sourceRoot 'bin/Send-Mail.ps1'
    $read = Join-Path $sourceRoot 'bin/Read-Mail.ps1'
    $wait = Join-Path $sourceRoot 'bin/Wait-Mail.ps1'
    $partiesFile = Join-Path $sourceRoot 'parties.example.json'
    $sent = Invoke-Script $send @('-Root', $root, '-PartiesFile', $partiesFile, '-From', 'alpha', '-To', 'gamma',
        '-Subject', '主题 一', '-Body', '正文甲', '-Thread', 'demo', '-ReplyTo', 'previous.md', '-NeedsReply')
    Assert-That ($sent.Output -match 'SENT to=gamma file=(\S+)') "Send did not name the file: $($sent.Output)"
    $fileName = $Matches[1].Trim()
    Assert-That ($fileName -match '^\d{8}T\d{9}Z_alpha_主题-一\.md$') "Unexpected mail filename: $fileName"
    $inbox = Join-Path $root 'to-gamma'
    $mailPath = Join-Path $inbox $fileName
    Assert-That (Test-Path -LiteralPath $mailPath) 'Sent file is missing.'
    Assert-That (@(Get-ChildItem -LiteralPath $inbox -Force -Filter '*.tmp').Count -eq 0) 'Temporary mail file was left behind.'
    $lines = @(Get-Content -LiteralPath $mailPath -Encoding utf8)
    Assert-That ($lines.Count -ge 10) 'Mail is shorter than the header contract.'
    Assert-That ($lines[0] -eq '---' -and $lines[1] -eq 'from: alpha' -and $lines[2] -eq 'to: gamma' -and $lines[3] -eq 'subject: 主题 一') 'Header order from/to/subject is wrong.'
    Assert-That ($lines[4] -match '^created_at_utc: \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$') 'created_at_utc is missing or out of order.'
    Assert-That ($lines[5] -eq 'thread: demo' -and $lines[6] -eq 'reply_to: previous.md' -and $lines[7] -eq 'needs_reply: true' -and $lines[8] -eq '---') 'Header tail is out of order.'
    Assert-That ($lines[9] -eq '正文甲') ("Body was not preserved: " + ($lines -join ' | '))
    Pass 'send atomic file, header order, Chinese subject'

    $waited = Invoke-Script $wait @('-Root', $root, '-PartiesFile', $partiesFile, '-Me', 'gamma', '-TimeoutMinutes', '1', '-IntervalSeconds', '1')
    Assert-That ($waited.Output.Contains('MAIL me=gamma unread=1')) 'Wait did not see the unread letter.'
    Assert-That ($waited.Output.Contains($fileName)) 'Wait did not list the letter.'
    $peeked = Invoke-Script $read @('-Root', $root, '-PartiesFile', $partiesFile, '-Me', 'gamma', '-Peek')
    Assert-That ($peeked.Output.Contains('正文甲')) 'Peek did not show the body.'
    Assert-That (Test-Path -LiteralPath $mailPath) 'Peek archived the letter.'
    $archived = Invoke-Script $read @('-Root', $root, '-PartiesFile', $partiesFile, '-Me', 'gamma')
    Assert-That ($archived.Output.Contains('正文甲')) 'Read did not show the body.'
    Assert-That (-not (Test-Path -LiteralPath $mailPath)) 'Read left the letter in the inbox.'
    Assert-That (Test-Path -LiteralPath (Join-Path $inbox "read/$fileName")) 'Read did not move the letter to read\.'
    $empty = Invoke-Script $read @('-Root', $root, '-PartiesFile', $partiesFile, '-Me', 'gamma')
    Assert-That ($empty.Output.Contains('NO-MAIL me=gamma')) 'Second read still found mail.'
    $timedOut = Invoke-Script $wait @('-Root', $root, '-PartiesFile', $partiesFile, '-Me', 'gamma', '-TimeoutMinutes', '0', '-IntervalSeconds', '1') 2
    Assert-That ($timedOut.Output.Contains('timeout')) 'Empty inbox did not time out.'
    Pass 'wait, peek, archive, empty read, timeout'

    $bodyFile = Join-Path $tempRoot 'body.txt'
    Set-Content -LiteralPath $bodyFile -Value 'from-file' -Encoding utf8
    Invoke-Script $send @('-Root', $root, '-PartiesFile', $partiesFile, '-From', 'beta', '-To', 'alpha', '-Subject', 'file', '-BodyFile', $bodyFile) | Out-Null
    $fromFile = Invoke-Script $read @('-Root', $root, '-PartiesFile', $partiesFile, '-Me', 'alpha')
    Assert-That ($fromFile.Output.Contains('from-file')) 'BodyFile content was lost.'
    Pass 'body file'

    $pair = Join-Path $tempRoot 'pair'
    Invoke-Script $send @('-Root', $pair, '-Parties', 'red,blue', '-From', 'red', '-Subject', 'ping', '-Body', 'two-party') | Out-Null
    Assert-That (@(Get-ChildItem -LiteralPath (Join-Path $pair 'to-blue') -Filter '*.md' -File).Count -eq 1) 'Two-party send did not infer the other inbox.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $pair 'to-red'))) 'Inferred send also wrote the sender inbox.'
    Pass 'two-party recipient is inferred'

    $decoy = Join-Path $tempRoot 'decoy'
    $real = Join-Path $tempRoot 'real'
    $env:AGENT_MAILBOX_ROOT = $decoy
    $env:AGENT_MAILBOX_PARTIES = 'red,blue'
    try {
        Invoke-Script $send @('-Root', $real, '-Parties', 'red,blue', '-From', 'blue', '-To', 'red', '-Subject', 'override', '-Body', 'rooted') | Out-Null
    } finally {
        Remove-Item Env:AGENT_MAILBOX_ROOT, Env:AGENT_MAILBOX_PARTIES -ErrorAction SilentlyContinue
    }
    Assert-That (@(Get-ChildItem -LiteralPath (Join-Path $real 'to-red') -Filter '*.md' -File -ErrorAction SilentlyContinue).Count -eq 1) '-Root did not override the environment.'
    Assert-That (-not (Test-Path -LiteralPath $decoy)) 'Explicit -Root still wrote to AGENT_MAILBOX_ROOT.'
    Pass 'explicit root wins over the environment'

    $envWrapper = Join-Path $tempRoot 'env-wrapper.ps1'
    @'
param([string]$Send, [string]$Root)
$ErrorActionPreference = 'Stop'
$env:AGENT_MAILBOX_ROOT = $Root
$env:AGENT_MAILBOX_PARTIES = 'red,blue'
& $Send -From red -Subject env -Body via-env
if (-not $?) { exit 1 }
exit 0
'@ | Set-Content -LiteralPath $envWrapper -Encoding utf8
    $envRoot = Join-Path $tempRoot 'env-box'
    Invoke-Script $envWrapper @('-Send', $send, '-Root', $envRoot) | Out-Null
    Assert-That (@(Get-ChildItem -LiteralPath (Join-Path $envRoot 'to-blue') -Filter '*.md' -File).Count -eq 1) 'Environment parties/root were not used.'
    Pass 'root and parties can come from the environment'

    $missingWrapper = Join-Path $tempRoot 'missing-wrapper.ps1'
    @'
param([string]$Send)
$ErrorActionPreference = 'Stop'
Remove-Item Env:AGENT_MAILBOX_ROOT, Env:AGENT_MAILBOX_PARTIES -ErrorAction SilentlyContinue
& $Send -From alpha -Subject hi -Body body
if (-not $?) { exit 1 }
exit 0
'@ | Set-Content -LiteralPath $missingWrapper -Encoding utf8
    $missing = Invoke-Script $missingWrapper @('-Send', $send) 1
    Assert-That ($missing.Output.Contains('AGENT_MAILBOX_ROOT')) 'Missing root was not refused.'
    $badParty = Invoke-Script $send @('-Root', $root, '-PartiesFile', $partiesFile, '-From', 'nope', '-To', 'alpha', '-Subject', 'no', '-Body', 'no') 1
    Assert-That ($badParty.Output.Contains('不在名单里')) 'Unknown party was accepted.'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $root 'to-nope'))) 'Unknown party created an inbox.'
    Pass 'missing root and unknown party are refused'

    "SMOKE PASS: $($checks.Count) checks; PowerShell $($PSVersionTable.PSVersion)"
}
finally {
    Remove-Item Env:AGENT_MAILBOX_ROOT, Env:AGENT_MAILBOX_PARTIES -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $tempRoot) {
        if ($KeepTemp) { "SMOKE TEMP: $tempRoot" }
        else {
            $resolvedRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $tempRoot).Path)
            $resolvedBase = $tempBase.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            Assert-That ($resolvedRoot.StartsWith($resolvedBase, [StringComparison]::OrdinalIgnoreCase)) 'Refusing cleanup outside the OS temporary directory.'
            Assert-That ((Split-Path -Leaf $resolvedRoot) -like 'agent-mailbox smoke *') 'Refusing cleanup of an unexpected directory.'
            Assert-That ((Get-Content -LiteralPath $ownerFile -Raw).Trim() -eq $owner) 'Refusing cleanup without the matching smoke owner marker.'
            Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        }
    }
}
