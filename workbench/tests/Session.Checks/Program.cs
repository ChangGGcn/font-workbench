using System.Collections.Concurrent;
using FontBook.Workbench.Session;

namespace FontBook.Workbench.Session.Checks;

internal static class Program
{
    private const string A = @"C:\SessionChecks\One\Example.ttf";
    private const string B = @"C:\SessionChecks\Two\Example.ttf";
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static int Main()
    {
        Action[] checks = [ActivateOnce, ActivationFailures, ReleaseRetries, IndependentPaths, NotifyFailures, ConcurrentActivation];
        int passed = 0;
        foreach (var check in checks)
        {
            try
            {
                check();
                passed++;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"{check.Method.Name}: {error}");
            }
        }
        Console.WriteLine($"Session.Checks: {passed}/{checks.Length} in-memory checks passed");
        if (passed != checks.Length)
            return 1;

        try
        {
            if (OperatingSystem.IsWindows())
                FontBook.Workbench.SessionTests.WindowsPinChecks.Run();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"WindowsPinChecks: {error}");
            return 1;
        }
        return 0;
    }

    private static void ActivateOnce()
    {
        var backend = new FakeBackend();
        var set = new SessionFontSet(backend);
        Check(set.Activate(A) == 2, "Activate did not return the backend's count");
        var pin = backend.Pins.Single();
        Check(HasPaths(set.ActivePaths, A) && pin.Disposals == 0, "activation must keep the path and pin");
        ExpectCalls(backend, $"Pin {A}", $"Add {A}", "Notify");
        Throws<InvalidOperationException>(() => set.Activate(A.ToLowerInvariant()));
        Check(HasPaths(set.ActivePaths, A) && pin.Disposals == 0, "duplicate activation changed the active registration");
        ExpectCalls(backend);
    }

    private static void ActivationFailures()
    {
        var backend = new FakeBackend { AddResult = () => 0 };
        var set = new SessionFontSet(backend);
        Throws<InvalidOperationException>(() => set.Activate(A));
        Check(set.ActivePaths.Count == 0 && backend.Pins.Single().Disposals == 1, "zero Add count must roll back the pin and state");
        ExpectCalls(backend, $"Pin {A}", $"Add {A}");

        var failure = new IOException("simulated Add failure");
        backend.AddResult = () => throw failure;
        Check(ReferenceEquals(failure, Throws<IOException>(() => set.Activate(A))), "Add exception was replaced");
        Check(set.ActivePaths.Count == 0 && backend.Pins.All(pin => pin.Disposals == 1), "throwing Add must roll back only its pin and state");
        ExpectCalls(backend, $"Pin {A}", $"Add {A}");

        backend.PinError = new IOException("simulated Pin failure");
        Check(ReferenceEquals(backend.PinError, Throws<IOException>(() => set.Activate(A))), "Pin exception was replaced");
        Check(set.ActivePaths.Count == 0, "failed Pin must not leave an active path");
        ExpectCalls(backend, $"Pin {A}");
    }

    private static void ReleaseRetries()
    {
        var backend = new FakeBackend { RemoveResult = () => false };
        var set = new SessionFontSet(backend);
        set.Activate(A);
        var pin = backend.Pins.Single();
        ExpectCalls(backend, $"Pin {A}", $"Add {A}", "Notify");

        Check(!set.Release(B), "releasing an unknown path must return false");
        ExpectCalls(backend);
        Check(!set.Release(A) && HasPaths(set.ActivePaths, A) && pin.Disposals == 0, "false Remove must keep the active pin for retry");
        ExpectCalls(backend, $"Remove {A}");

        var failure = new IOException("simulated Remove failure");
        backend.RemoveResult = () => throw failure;
        Check(ReferenceEquals(failure, Throws<IOException>(() => set.Release(A))), "Remove exception was replaced");
        Check(HasPaths(set.ActivePaths, A) && pin.Disposals == 0, "throwing Remove must keep the active pin for retry");
        ExpectCalls(backend, $"Remove {A}");

        backend.RemoveResult = () => true;
        Check(set.Release(A) && set.ActivePaths.Count == 0 && pin.Disposals == 1, "successful retry must clear the path and dispose the pin once");
        ExpectCalls(backend, $"Remove {A}", "Notify");
        Check(!set.Release(A) && pin.Disposals == 1, "already released path must not dispose again");
        ExpectCalls(backend);
    }

    private static void IndependentPaths()
    {
        var backend = new FakeBackend();
        var set = new SessionFontSet(backend);
        set.Activate(A);
        var firstSnapshot = set.ActivePaths;
        set.Activate(B);
        var bothSnapshot = set.ActivePaths;
        var pins = backend.Pins.ToArray();
        Check(HasPaths(bothSnapshot, A, B) && pins.All(pin => pin.Disposals == 0), "different files must hold independent pins");
        Check(set.Release(A) && HasPaths(set.ActivePaths, B) && pins[0].Disposals == 1 && pins[1].Disposals == 0, "releasing A affected B");
        Check(set.Release(B) && set.ActivePaths.Count == 0 && pins.All(pin => pin.Disposals == 1), "B must remain independently releasable");
        Check(HasPaths(firstSnapshot, A) && HasPaths(bothSnapshot, A, B), "ActivePaths snapshots changed after later operations");
        ExpectCalls(backend, $"Pin {A}", $"Add {A}", "Notify", $"Pin {B}", $"Add {B}", "Notify", $"Remove {A}", "Notify", $"Remove {B}", "Notify");
    }

    private static void NotifyFailures()
    {
        var backend = new FakeBackend { NotifyError = new IOException("simulated Notify failure") };
        var set = new SessionFontSet(backend);
        Check(set.Activate(A) == 2 && HasPaths(set.ActivePaths, A), "Notify error must not undo or reject a successful Add");
        var pin = backend.Pins.Single();
        Check(pin.Disposals == 0, "Notify error after Add disposed the active pin");
        Throws<InvalidOperationException>(() => set.Activate(A));
        ExpectCalls(backend, $"Pin {A}", $"Add {A}", "Notify");
        Check(set.Release(A) && set.ActivePaths.Count == 0 && pin.Disposals == 1, "Notify error must not undo or reject a successful Remove");
        Check(!set.Release(A) && pin.Disposals == 1, "Notify error left the registration eligible for another Remove");
        ExpectCalls(backend, $"Remove {A}", "Notify");
    }

    private static void ConcurrentActivation()
    {
        using var start = new Barrier(2);
        var backend = new FakeBackend { AddResult = SlowAdd };
        var set = new SessionFontSet(backend);
        var attempts = Task.WhenAll(Task.Run(() => Attempt(A)), Task.Run(() => Attempt(A.ToLowerInvariant())));
        var results = attempts.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Check(results.Count(count => count.HasValue) == 1 && results.Contains(2), "exactly one racing activation must succeed");
        Check(HasPaths(set.ActivePaths, A) && backend.Pins.Single().Disposals == 0, "race must leave one active pin");
        ExpectCalls(backend, $"Pin {A}", $"Add {A}", "Notify");

        int? Attempt(string path)
        {
            Check(start.SignalAndWait(TimeSpan.FromSeconds(5)), "activation workers did not meet");
            try { return set.Activate(path); }
            catch (InvalidOperationException) { return null; }
        }

        static int SlowAdd()
        {
            // Widen the overlap window between the two calls.
            Thread.Sleep(50);
            return 2;
        }
    }

    private static bool HasPaths(IReadOnlyList<string> actual, params string[] expected) =>
        actual.Count == expected.Length && expected.All(path => actual.Contains(path, PathComparer));

    private static void ExpectCalls(FakeBackend backend, params string[] expected)
    {
        var actual = backend.Calls.ToArray();
        Check(actual.SequenceEqual(expected, PathComparer), $"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
        backend.Calls.Clear();
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"expected {typeof(T).Name}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeBackend : ISessionFontBackend
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public ConcurrentQueue<FakePin> Pins { get; } = new();
        public Func<int> AddResult { get; set; } = () => 2;
        public Func<bool> RemoveResult { get; set; } = () => true;
        public Exception? PinError { get; set; }
        public Exception? NotifyError { get; set; }

        public IDisposable Pin(string path)
        {
            Calls.Enqueue($"Pin {path}");
            if (PinError is { } error) throw error;
            var pin = new FakePin();
            Pins.Enqueue(pin);
            return pin;
        }

        public int Add(string path)
        {
            Calls.Enqueue($"Add {path}");
            return AddResult();
        }

        public bool Remove(string path)
        {
            Calls.Enqueue($"Remove {path}");
            return RemoveResult();
        }

        public void Notify()
        {
            Calls.Enqueue("Notify");
            if (NotifyError is { } error) throw error;
        }
    }

    private sealed class FakePin : IDisposable
    {
        private int _disposals;
        public int Disposals => Volatile.Read(ref _disposals);
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
