$ErrorActionPreference = 'Stop'
# The Python caller sends UTF-8 bytes over pipes; detached Windows shells can
# otherwise inherit an OEM code page and corrupt Chinese paths in the JSON.
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$sourceText = [Console]::In.ReadToEnd()
$parseTokens = $null
$parseErrors = $null
$tree = [System.Management.Automation.Language.Parser]::ParseInput($sourceText, [ref]$parseTokens, [ref]$parseErrors)

function Describe-Literal($node) {
    if ($null -eq $node) { return @{ kind = 'missing' } }
    switch ($node.GetType().Name) {
        'StringConstantExpressionAst' { return @{ kind = 'literal'; value = $node.Value } }
        'ConstantExpressionAst' { return @{ kind = 'literal'; value = $node.Value } }
        'VariableExpressionAst' {
            if ($node.VariablePath.UserPath -eq 'true') { return @{ kind = 'literal'; value = $true } }
            if ($node.VariablePath.UserPath -eq 'false') { return @{ kind = 'literal'; value = $false } }
            return @{ kind = 'dynamic' }
        }
        'ArrayLiteralAst' { return @{ kind = 'array'; values = @($node.Elements | ForEach-Object { Describe-Literal $_ }) } }
        'ArrayExpressionAst' {
            $values = @()
            foreach ($statement in $node.SubExpression.Statements) {
                if ($statement -isnot [System.Management.Automation.Language.PipelineAst] -or $statement.PipelineElements.Count -ne 1 -or $statement.PipelineElements[0] -isnot [System.Management.Automation.Language.CommandExpressionAst]) { return @{ kind = 'dynamic' } }
                $values += ,(Describe-Literal $statement.PipelineElements[0].Expression)
            }
            return @{ kind = 'array'; values = $values }
        }
        'ParenExpressionAst' {
            if ($node.Pipeline -is [System.Management.Automation.Language.PipelineAst] -and $node.Pipeline.PipelineElements.Count -eq 1 -and $node.Pipeline.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) { return Describe-Literal $node.Pipeline.PipelineElements[0].Expression }
            return @{ kind = 'dynamic' }
        }
        'CommandParameterAst' { return @{ kind = 'parameter'; name = $node.ParameterName; argument = (Describe-Literal $node.Argument) } }
        default { return @{ kind = 'dynamic' } }
    }
}

function Assigned-Names($node) {
    if ($node -is [System.Management.Automation.Language.VariableExpressionAst]) { return @($node.VariablePath.UserPath) }
    if ($node -is [System.Management.Automation.Language.ArrayLiteralAst]) { return @($node.Elements | ForEach-Object { Assigned-Names $_ }) }
    return @()
}

$assignments = @()
foreach ($node in $tree.FindAll({ param($candidate) $candidate -is [System.Management.Automation.Language.AssignmentStatementAst] -or $candidate -is [System.Management.Automation.Language.UnaryExpressionAst] -or $candidate -is [System.Management.Automation.Language.ForEachStatementAst] }, $true)) {
    if ($node -is [System.Management.Automation.Language.AssignmentStatementAst]) { $assignments += @(Assigned-Names $node.Left) }
    elseif ($node -is [System.Management.Automation.Language.ForEachStatementAst]) { $assignments += @(Assigned-Names $node.Variable) }
    elseif ($node.TokenKind.ToString() -match 'PlusPlus|MinusMinus') { $assignments += @(Assigned-Names $node.Child) }
}

$commands = @($tree.FindAll({ param($candidate) $candidate -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object {
    @{ elements = @($_.CommandElements | ForEach-Object { Describe-Literal $_ }) }
})
$methods = @($tree.FindAll({ param($candidate) $candidate -is [System.Management.Automation.Language.InvokeMemberExpressionAst] }, $true) | ForEach-Object {
    $typeName = $null
    if ($_.Expression -is [System.Management.Automation.Language.TypeExpressionAst]) { $typeName = $_.Expression.TypeName.FullName }
    @{ static = $_.Static; type = $typeName; member = (Describe-Literal $_.Member); arguments = @($_.Arguments | ForEach-Object { Describe-Literal $_ }) }
})
$readonlyNames = @(Get-Variable | Where-Object { ($_.Options -band [System.Management.Automation.ScopedItemOptions]::ReadOnly) -or ($_.Options -band [System.Management.Automation.ScopedItemOptions]::Constant) } | Select-Object -ExpandProperty Name)
@{ parse_error_count = @($parseErrors).Count; assignments = $assignments; readonly_names = $readonlyNames; commands = $commands; methods = $methods } | ConvertTo-Json -Depth 32 -Compress
