using System.Diagnostics;

namespace AiMoeStudio.Bridge;

public static class ParentLifetime
{
    // Windows force-closing Node does not run JS shutdown handlers. Hold the parent
    // process handle so this bridge still exits, without touching any observed CLI.
    public static Process? Attach(int? parentPid, CancellationTokenSource cancellation)
    {
        if (parentPid is null) return null;
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(parentPid.Value);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => cancellation.Cancel();
            if (parent.HasExited) cancellation.Cancel();
            return parent;
        }
        catch (Exception)
        {
            parent?.Dispose();
            cancellation.Cancel();
            return null;
        }
    }
}
