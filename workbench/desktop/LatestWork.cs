namespace FontBook.Workbench;

/// <summary>UI-thread owned. At most one worker per lane; only its newest request may publish.</summary>
internal sealed class LatestWork
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _current;
    public void Cancel()
    {
        _current?.Cancel();
        _current = null;
    }
    public async Task Run<T>(Func<CancellationToken, T> compute, Action<T> publish, Action<Exception> failed,
        int debounceMs = 0, Action<T>? discard = null)
    {
        Cancel();
        using var activity = FontReadActivity.Begin();
        using var source = new CancellationTokenSource();
        _current = source;
        var ct = source.Token;
        try
        {
            if (debounceMs > 0)
                await Task.Delay(debounceMs, ct);
            await _gate.WaitAsync(ct);
            T value;
            try
            {
                value = await Task.Run(() => compute(ct), ct);
            }
            finally { _gate.Release(); }
            if (ct.IsCancellationRequested)
            {
                discard?.Invoke(value);
                return;
            }
            publish(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { if (!ct.IsCancellationRequested) failed(ex); }
        finally { if (ReferenceEquals(_current, source)) _current = null; }
    }
}
