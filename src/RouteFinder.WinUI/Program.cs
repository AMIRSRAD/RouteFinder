namespace RouteFinder.WinUI;

/// <summary>
/// Entry point. The DNS relay helper (--dns-relay) runs here without starting XAML: it is a renamed copy of this
/// launcher, and XAML would look for resources under that other name. Everything else starts the normal app,
/// exactly as the generated entry point did.
/// </summary>
public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        int resolve = Array.IndexOf(args, "--dns-resolve");
        if (resolve >= 0 && args.Length > resolve + 3)
        {
            DnsRelay.ResolveOnce(args[resolve + 1].Split(',', StringSplitOptions.RemoveEmptyEntries),
                args[resolve + 2].Split(',', StringSplitOptions.RemoveEmptyEntries), args[resolve + 3], args.ElementAtOrDefault(resolve + 4));
            return;
        }
        int relay = Array.IndexOf(args, "--dns-relay");
        if (relay >= 0 && args.Length > relay + 2 && int.TryParse(args[relay + 2], out int parent))
        {
            DnsRelay.Run(args[relay + 1].Split(',', StringSplitOptions.RemoveEmptyEntries), parent, args.ElementAtOrDefault(relay + 3));
            Thread.Sleep(Timeout.Infinite); // the relay exits the process when RouteFinder does
            return;
        }
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
