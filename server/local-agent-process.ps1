$ErrorActionPreference = 'Stop'
# This helper never emits window titles, executable paths, command lines or text.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class StudioLocalWindows {
  public delegate bool Callback(IntPtr hwnd, IntPtr parameter);
  [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr parameter);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
  public static Dictionary<uint,List<string>> Read() {
    var found = new Dictionary<uint,List<string>>();
    EnumWindows((hwnd, parameter) => {
      if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4) != IntPtr.Zero || (GetWindowLong(hwnd, -20) & 0x80) != 0) return true;
      int cloaked; if (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
      uint pid; GetWindowThreadProcessId(hwnd, out pid);
      if (!found.ContainsKey(pid)) found[pid] = new List<string>();
      found[pid].Add(hwnd.ToInt64().ToString()); return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@
$windows = [StudioLocalWindows]::Read()
$rows = [System.Collections.Generic.List[object]]::new()
$all = @(Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,CreationDate)
foreach ($process in $all) {
  $name = [string]$process.Name
  $exe = ([string]$process.ExecutablePath).Replace('\','/')
  $command = ([string]$process.CommandLine).Replace('\','/')
  $agent = $null
  $type = 'cli'
  if ($name -ieq 'DeepSeek Harness.exe') { $agent = 'dsh'; $type = 'desktop' }
  elseif ($name -ieq 'Grok Bot.exe') { $agent = 'grok-bot'; $type = 'desktop' }
  elseif ($name -ieq 'Cursor.exe') { $agent = 'cursor'; $type = 'desktop' }
  elseif ($name -ieq 'claude.exe') {
    $agent = 'claude'
    if ($exe -match '/AnthropicClaude/|/Programs/Claude/|/WindowsApps/Claude') { $type = 'desktop' }
  }
  elseif ($name -match '^(?i:grokbuild|grok)\.exe$' -or ($name -ieq 'agent.exe' -and $exe -match '/\.grok/')) { $agent = 'grok' }
  elseif ($name -match '^(?i:cursor-agent|agent)\.exe$' -and $exe -match '/cursor-agent/|/\.cursor/') { $agent = 'cursor' }
  elseif ($name -match '^(?i:opencode)\.exe$') { $agent = 'opencode' }
  elseif ($name -match '^(?i:pi)\.exe$') { $agent = 'pi' }
  elseif ($name -ieq 'codex.exe' -and $command -match '^\s*(?:"[^"\r\n]*codex\.exe"|[^"\s]*codex\.exe)\s+exec(?:\s|$)') {
    # Only native headless exec; App/app-server instances keep their own observer.
    $agent = 'codex'
  }
  elseif ($name -match '^(?i:node|bun)\.exe$') {
    # Only package-entry identifiers are inspected; argument text never leaves this process.
    if ($command -match '/(?:@earendil-works|@mariozechner)/pi-coding-agent/(?:dist|bin)/') { $agent = 'pi' }
    elseif ($command -match '/(?:@opencode/cli|opencode-ai)/') { $agent = 'opencode' }
    elseif ($command -match '/(?:@vibe-kit/grok-cli|@x-ai/grok|grok-cli|\.grok)/(?:[^ ]*/)?(?:cli|agent|index|grok)\.(?:m?js|cjs)') { $agent = 'grok' }
    elseif ($command -match '/(?:cursor-agent|@cursor/agent)/') { $agent = 'cursor' }
    elseif ($command -match '/@anthropic-ai/claude-code/') { $agent = 'claude' }
    elseif ($command -match '/@deepseek-ai/dsh/(?:lib|bin)/') {
      $agent = 'dsh'
      if ($command -match '(?:\s|["''])web(?:\s|["''])|--web|webui') { $type = 'host' }
    }
  }
  if ($null -eq $agent) { continue }
  $handles = @()
  if ($windows.ContainsKey([uint32]$process.ProcessId)) { $handles = @($windows[[uint32]$process.ProcessId]) }
  $rows.Add([pscustomobject]@{
    pid = [int]$process.ProcessId; parentPid = [int]$process.ParentProcessId
    agentId = $agent; type = $type; windows = $handles
    startedAt = if ($process.CreationDate) { $process.CreationDate.ToUniversalTime().ToString('o') } else { $null }
  })
}
$parents = @($all | ForEach-Object { [pscustomobject]@{
  pid=[int]$_.ProcessId; parentPid=[int]$_.ParentProcessId
  startedAt=if ($_.CreationDate) { $_.CreationDate.ToUniversalTime().ToString('o') } else { $null }
} })
[pscustomobject]@{available=$true; windowsAvailable=$true; processes=@($rows); parents=$parents} | ConvertTo-Json -Depth 5 -Compress
