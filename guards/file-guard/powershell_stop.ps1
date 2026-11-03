$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$commandSource = [Console]::In.ReadToEnd()
$commandTokens = $null
$commandErrors = $null
$commandTree = [System.Management.Automation.Language.Parser]::ParseInput($commandSource, [ref]$commandTokens, [ref]$commandErrors)
if (@($commandErrors).Count) {
    @{ code = 'POWERSHELL_PARSE_ERROR'; reason = 'Correct the PowerShell syntax before this command can run.' } | ConvertTo-Json -Compress
    exit 2
}

# Insert in the original script scope, after using/param syntax. Do not wrap in
# a child scriptblock, change return semantics, or alter native-exit-code policy.
$insertionOffset = 0
foreach ($usingStatement in $commandTree.UsingStatements) {
    $insertionOffset = [Math]::Max($insertionOffset, $usingStatement.Extent.EndOffset)
}
if ($commandTree.ParamBlock) {
    $insertionOffset = [Math]::Max($insertionOffset, $commandTree.ParamBlock.Extent.EndOffset)
}
$insertions = @()
$preferenceStatement = "`n# AgentFileSafety: ErrorActionPreference Stop`n`$ErrorActionPreference = 'Stop'`n"

function Block-OpeningOffset($block) {
    $token = $commandTokens | Where-Object { $_.Kind.ToString() -eq 'LCurly' -and $_.Extent.StartOffset -ge $block.Extent.StartOffset -and $_.Extent.EndOffset -le $block.Extent.EndOffset } | Select-Object -First 1
    if (-not $token) { throw 'Named script block opening token unavailable.' }
    return $token.Extent.EndOffset
}

if ($commandTree.EndBlock -and $commandTree.EndBlock.Unnamed) {
    $insertions += @{ offset = $insertionOffset; text = $preferenceStatement }
} else {
    if ($commandTree.DynamicParamBlock) {
        $insertions += @{ offset = (Block-OpeningOffset $commandTree.DynamicParamBlock); text = $preferenceStatement }
        $insertionOffset = [Math]::Max($insertionOffset, $commandTree.DynamicParamBlock.Extent.EndOffset)
    }
    if ($commandTree.BeginBlock) {
        $insertions += @{ offset = (Block-OpeningOffset $commandTree.BeginBlock); text = $preferenceStatement }
    } else {
        $insertions += @{ offset = $insertionOffset; text = "`nbegin { $preferenceStatement }`n" }
    }
}
foreach ($insertion in ($insertions | Sort-Object offset -Descending)) {
    $commandSource = $commandSource.Insert($insertion.offset, $insertion.text)
}
$verifyTokens = $null
$verifyErrors = $null
$null = [System.Management.Automation.Language.Parser]::ParseInput($commandSource, [ref]$verifyTokens, [ref]$verifyErrors)
if (@($verifyErrors).Count) {
    @{ code = 'POWERSHELL_REWRITE_FAILED'; reason = 'This script cannot receive the Stop preference without invalidating its syntax.' } | ConvertTo-Json -Compress
    exit 2
}
@{ command = $commandSource } | ConvertTo-Json -Compress
