namespace FontBook.Workbench;

// A cancelled worker can still own a native face until its result is disposed.
// A confirmed uninstall must wait for that disposal, not just signal cancellation.
internal sealed class FontReadActivity : IDisposable
{
    private static readonly object Gate = new();
    private static int _count;
    private static TaskCompletionSource? _idle;

    internal static FontReadActivity Begin()
    {
        lock (Gate)
        {
            if (_count++ == 0)
                _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new FontReadActivity();
        }
    }

    internal static Task WhenIdle()
    {
        lock (Gate)
            return _idle?.Task ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (--_count == 0)
            {
                _idle!.TrySetResult();
                _idle = null;
            }
        }
    }
}
