param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot
)

$dir = [System.IO.Path]::GetFullPath($ProjectRoot)
while ($dir) {
    $localDotnet = Join-Path $dir '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet -PathType Leaf) {
        return (Resolve-Path -LiteralPath $localDotnet).Path
    }

    $parent = Split-Path -Parent $dir
    if (-not $parent -or $parent -eq $dir) { break }
    $dir = $parent
}

$systemDotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $systemDotnet) {
    $systemDotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
}
if ($null -eq $systemDotnet) {
    throw 'dotnet_sdk_missing: install .NET SDK 8.0.423 or place it under .tools\dotnet'
}

return $systemDotnet.Source
