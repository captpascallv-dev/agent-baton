// SPDX-License-Identifier: MPL-2.0
namespace CliWatch.App;

/// <summary>
/// Named mutex for this product identity only. Does not take the legacy
/// cockpit or Codex Usage HUD mutex.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly string _name;
    Mutex? _mutex;
    bool _owned;

    public SingleInstance(string? name = null)
    {
        _name = string.IsNullOrWhiteSpace(name) ? ProductPaths.MutexName : name;
    }

    public string Name => _name;
    public bool IsOwner => _owned;

    public bool TryAcquire()
    {
        if (_owned) return true;
        var mutex = new Mutex(initiallyOwned: true, name: _name, createdNew: out var created);
        if (!created)
        {
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        _owned = true;
        return true;
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try
        {
            if (_owned)
                _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        _mutex.Dispose();
        _mutex = null;
        _owned = false;
    }
}
