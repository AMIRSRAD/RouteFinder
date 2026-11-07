namespace RouteFinder.WinUI;

internal sealed class LaunchInstance : IDisposable
{
    readonly Mutex lifetime;
    readonly EventWaitHandle activate;
    readonly RegisteredWaitHandle registration;

    LaunchInstance(Mutex lifetime, EventWaitHandle activate, Action onActivate)
    {
        this.lifetime = lifetime;
        this.activate = activate;
        registration = ThreadPool.RegisterWaitForSingleObject(activate, (_, _) => onActivate(), null,
            Timeout.Infinite, executeOnlyOnce: false);
    }

    public static bool TryActivate(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name + ".Instance", out var running)) return false;
            using (running)
            {
                if (!EventWaitHandle.TryOpenExisting(name + ".Activate", out var signal)) return false;
                using (signal) return signal.Set();
            }
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static LaunchInstance? Acquire(string name, Action onActivate)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate");
        Mutex? lifetime = null;
        try
        {
            lifetime = new Mutex(false, name + ".Instance", out bool first);
            if (first) return new LaunchInstance(lifetime, signal, onActivate);
            signal.Set();
        }
        catch
        {
            signal.Dispose();
            lifetime?.Dispose();
            throw;
        }
        signal.Dispose();
        lifetime.Dispose();
        return null;
    }

    public void Dispose()
    {
        registration.Unregister(null);
        lifetime.Dispose();
        activate.Dispose();
    }
}
