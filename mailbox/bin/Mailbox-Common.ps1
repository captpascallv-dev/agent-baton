# Shared by Send-Mail.ps1, Read-Mail.ps1, and Wait-Mail.ps1. Dot-source only.
# 根目录和参与方都不写死：-Root / AGENT_MAILBOX_ROOT，-Parties / -PartiesFile / AGENT_MAILBOX_PARTIES，
# 再否则使用根目录下的 parties.json。

function Get-MailboxContext {
    param(
        [string]$Root,
        [string]$Parties,
        [string]$PartiesFile
    )
    if ($PartiesFile -and $Parties) { throw '-Parties 和 -PartiesFile 只能用一个。' }
    if (-not $Root) { $Root = [string]$env:AGENT_MAILBOX_ROOT }
    if (-not $Root) { throw '缺少信箱根目录：用 -Root 或环境变量 AGENT_MAILBOX_ROOT。' }
    $parent = Split-Path -Parent $Root
    if ($parent -and -not (Test-Path -LiteralPath $parent)) { throw "信箱根目录的上级不存在：$Root" }
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    $Root = (Resolve-Path -LiteralPath $Root).Path

    $names = @()
    if ($PartiesFile) {
        if (-not (Test-Path -LiteralPath $PartiesFile -PathType Leaf)) { throw "找不到参与方文件：$PartiesFile" }
        $parsed = Get-Content -LiteralPath $PartiesFile -Raw -Encoding utf8 | ConvertFrom-Json
        $names = @($parsed.parties)
    } elseif ($Parties) {
        $names = @($Parties -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    } elseif ($env:AGENT_MAILBOX_PARTIES) {
        $names = @([string]$env:AGENT_MAILBOX_PARTIES -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    } else {
        $fallback = Join-Path $Root 'parties.json'
        if (-not (Test-Path -LiteralPath $fallback -PathType Leaf)) { throw '缺少参与方：用 -Parties、-PartiesFile、AGENT_MAILBOX_PARTIES，或在根目录放 parties.json。' }
        $parsed = Get-Content -LiteralPath $fallback -Raw -Encoding utf8 | ConvertFrom-Json
        $names = @($parsed.parties)
    }
    $clean = [Collections.Generic.List[string]]::new()
    foreach ($name in $names) {
        $value = [string]$name
        if ($value -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') { throw "参与方名字不合法：$value" }
        if ($clean -contains $value) { throw "参与方重复：$value" }
        $clean.Add($value)
    }
    if ($clean.Count -lt 1) { throw '参与方名单是空的。' }
    [pscustomobject]@{ Root = $Root; Parties = @($clean) }
}

function Assert-MailboxParty {
    param([string]$Name, [string[]]$Parties)
    if ($Parties -notcontains $Name) { throw "参与方不在名单里：$Name" }
}

function Get-MailboxInbox {
    param([string]$Root, [string]$Party)
    $inbox = Join-Path $Root "to-$Party"
    $rootFull = [IO.Path]::GetFullPath($Root)
    $inboxFull = [IO.Path]::GetFullPath($inbox)
    $prefix = $rootFull.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $inboxFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '收件箱路径超出信箱根目录。' }
    $inboxFull
}
