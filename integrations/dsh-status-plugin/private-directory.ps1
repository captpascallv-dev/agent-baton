param([Parameter(Mandatory=$true)][string]$Directory)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($Directory)
if (-not [IO.Path]::IsPathRooted($Directory) -or $target -ne $Directory.TrimEnd('\','/')) { throw 'Absolute canonical output directory required' }
$current = [Security.Principal.WindowsIdentity]::GetCurrent().User
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$cursor = [IO.DirectoryInfo]::new($target)
while ($null -ne $cursor) {
  if ($cursor.Exists -and ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse output directory rejected' }
  $cursor = $cursor.Parent
}
if (-not [IO.Directory]::Exists($target)) {
  $parent = [IO.Directory]::GetParent($target)
  if ($null -eq $parent -or -not $parent.Exists) { throw 'Output parent must already exist' }
  [void][IO.Directory]::CreateDirectory($target)
  $acl = [Security.AccessControl.DirectorySecurity]::new()
  $acl.SetOwner($current)
  $acl.SetAccessRuleProtection($true, $false)
  foreach ($identity in @($current,$system)) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
    [void]$acl.AddAccessRule($rule)
  }
  [IO.Directory]::SetAccessControl($target, $acl)
}
$verified = [IO.Directory]::GetAccessControl($target)
if ($verified.GetOwner([Security.Principal.SecurityIdentifier]) -ne $current -or -not $verified.AreAccessRulesProtected) { throw 'Private output owner/inheritance required' }
$allowed = @($current.Value,$system.Value)
$userRule = $false
foreach ($rule in $verified.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
  if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowed) { throw 'Output directory grants another principal access' }
  if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -eq $current.Value) { $userRule = $true }
}
if (-not $userRule) { throw 'Output owner access required' }
