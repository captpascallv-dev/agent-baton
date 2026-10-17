# SPDX-License-Identifier: MPL-2.0
# Check the publishable source tree, excluding local build outputs.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$count = 0
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
    $parts = $relative -split '[\\/]'
    if (@($parts | Where-Object { $_ -in @('bin','obj','runtime','__pycache__','.git','TestResults') }).Count) { continue }
    if ($file.Name -like '*.local.json') { continue }
    if ($file.Extension -in @('.dll','.exe','.pdb','.nupkg','.zip')) { throw ("Compiled/archive payload in source: " + $relative) }
    if ($file.Name -eq 'FORK_ORIGIN.json') { throw 'Private fork manifest must not be exported.' }
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($file.FullName -ne $PSCommandPath -and -not $relative.StartsWith('src' + [IO.Path]::DirectorySeparatorChar + 'licenses' + [IO.Path]::DirectorySeparatorChar)) {
        foreach ($pattern in @('PascalCockpit','[\\/]Users[\\/]+Pascal','qingqing','huhu-direct','小葡萄','清清','糊糊','BotRoom','01a[0-9a-f]{5}-[0-9a-f-]{27}')) {
            if ($text -match $pattern) { throw ("Personal source marker in " + $relative + ": " + $pattern) }
        }
    }
    if ($file.Extension -eq '.json') { $null = $text | ConvertFrom-Json -ErrorAction Stop }
    if ($file.Extension -eq '.ps1') {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw ("PowerShell parse failure: " + $relative + " " + $errors[0].Message) }
    }
    if ($file.Extension -in @('.csproj','.props','.xaml','.manifest')) { $null = [xml]$text }
    $count++
}
if (-not [IO.File]::ReadAllText((Join-Path $root 'LICENSE')).Contains('Mozilla Public License Version 2.0')) { throw 'MPL-2.0 license is missing.' }
Write-Output ("PUBLIC CHECK PASS: " + $count + " source/config/document files; syntax and personal markers checked.")
