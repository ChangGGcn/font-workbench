namespace FontBook.Workbench.Session;

public interface ISessionFontBackend
{
    IDisposable Pin(string path);
    int Add(string path);
    bool Remove(string path);
    void Notify();
}

/// <summary>Tracks only the registrations successfully added by this process. There is deliberately
/// no persistent list to replay after a crash: such a list is not proof of ownership of a GDI reference.</summary>
public sealed class SessionFontSet(ISessionFontBackend backend)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Original, IDisposable Pin)> _pins = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> ActivePaths
    {
        get
        {
            lock (_gate)
                return _pins.Keys.ToArray();
        }
    }

    public int Activate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            if (_pins.ContainsKey(path))
                throw new InvalidOperationException("此文件已由本工作台临时启用。");
            if (_pins.Count >= 128)
                throw new InvalidOperationException("同时试用最多 128 个文件。");
            // Reserve the dictionary entry before calling GDI. No allocation may orphan a successful add.
            IDisposable pin = backend.Pin(path);
            try
            {
                _pins.Add(path, (path, pin));
            }
            catch { pin.Dispose(); throw; }
            int added;
            try
            {
                added = backend.Add(path);
                if (added <= 0)
                    throw new InvalidOperationException("Windows 拒绝启用此字体；未记为成功。");
            }
            catch { _pins.Remove(path); pin.Dispose(); throw; }
            NotifyBestEffort();
            return added;
        }
    }

    public bool Release(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            if (!_pins.TryGetValue(path, out var entry) || !backend.Remove(entry.Original))
                return false;
            _pins.Remove(path);
            try
            {
                entry.Pin.Dispose();
            }
            finally { NotifyBestEffort(); }
            return true;
        }
    }

    private void NotifyBestEffort()
    {
        // Notification is advisory. Never repeat an already applied OS state transition on failure.
        try
        {
            backend.Notify();
        }
        catch (Exception) { }
    }
}
