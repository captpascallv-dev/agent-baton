// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using CliWatch.Contracts;

namespace CliWatch.App;

/// <summary>
/// Compiles against stub Views (SetSnapshot + NavigationRequested only).
/// After 04-views integration, also wires SelectionChanged / SelectProject /
/// SelectedProjectId when those members exist.
/// </summary>
public static class ViewSelectionBinder
{
    public static bool TrySetSnapshot(object view, CockpitSnapshot snapshot, out string? error)
    {
        error = null;
        try
        {
            var method = view.GetType().GetMethod("SetSnapshot", BindingFlags.Instance | BindingFlags.Public);
            if (method is null)
            {
                error = "SetSnapshot 不存在";
                return false;
            }
            method.Invoke(view, new object[] { snapshot });
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException)
        {
            error = "Views 实现尚未接入本槽";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    public static void TrySelectProject(object view, string? projectId)
    {
        var method = view.GetType().GetMethod("SelectProject", BindingFlags.Instance | BindingFlags.Public, binder: null, types: new[] { typeof(string) }, modifiers: null)
            ?? view.GetType().GetMethod("SelectProject", BindingFlags.Instance | BindingFlags.Public);
        if (method is null) return;
        try
        {
            method.Invoke(view, new object?[] { projectId });
        }
        catch (TargetInvocationException)
        {
        }
        catch (NotImplementedException)
        {
        }
    }

    public static string? TryReadSelectedProjectId(object view)
    {
        var prop = view.GetType().GetProperty("SelectedProjectId", BindingFlags.Instance | BindingFlags.Public);
        if (prop is null) return null;
        try
        {
            return prop.GetValue(view) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool TryWireSelectionChanged(object view, Action<string?> onSelected)
    {
        var evt = view.GetType().GetEvent("SelectionChanged", BindingFlags.Instance | BindingFlags.Public);
        if (evt is null) return false;
        EventHandler handler = (_, _) => onSelected(TryReadSelectedProjectId(view));
        try
        {
            evt.AddEventHandler(view, handler);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void ApplyHost(object hud, object details, CockpitHostModel host)
    {
        if (host.Snapshot is null) return;
        TrySetSnapshot(hud, host.Snapshot, out _);
        TrySetSnapshot(details, host.Snapshot, out _);
        TrySelectProject(hud, host.SelectedProjectId);
        TrySelectProject(details, host.SelectedProjectId);
    }
}
