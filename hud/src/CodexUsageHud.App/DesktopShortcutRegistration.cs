using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CodexUsageHud.App;

public static class DesktopShortcutRegistration
{
    public const string ShortcutFileName = "Codex Usage HUD.lnk";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutFileName);

    public static string CreateOrRepair()
    {
        var executable = Path.GetFullPath(Environment.ProcessPath ??
            Process.GetCurrentProcess().MainModule?.FileName ??
            throw new InvalidOperationException("executable_path_unavailable"));
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ??
            throw new InvalidOperationException("windows_script_host_unavailable");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType) ??
                throw new InvalidOperationException("windows_script_host_unavailable");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(ShortcutPath);
            dynamic dynamicShortcut = shortcut ??
                throw new InvalidOperationException("shortcut_creation_failed");
            dynamicShortcut.TargetPath = executable;
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty;
            dynamicShortcut.Description = "Codex Usage HUD";
            dynamicShortcut.IconLocation = executable + ",0";
            dynamicShortcut.Save();
            return ShortcutPath;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    public static bool Remove()
    {
        if (!File.Exists(ShortcutPath)) return false;
        File.Delete(ShortcutPath);
        return true;
    }
}
