using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Xaml;

namespace RouteFinder.WinUI;

public sealed partial class App : Application
{
    MainWindow? window;
    LaunchInstance? instance;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        int startupArgument = Array.IndexOf(arguments, "--check-startup");
        if (startupArgument >= 0 && arguments.ElementAtOrDefault(startupArgument + 1) is { } report)
        {
            window = MainWindow.VerifyStartup(report);
            return;
        }
        int captureArgument = Array.IndexOf(arguments, "--capture-preview");
        string? capture = captureArgument >= 0 ? arguments.ElementAtOrDefault(captureArgument + 1) : null;
        bool preview = capture is not null || arguments.Contains("--preview");
        if (!preview && !StartNormalLaunch()) { Exit(); return; }
        window = new MainWindow(preview);
        window.Closed += (_, _) => instance?.Dispose();
        window.Activate();
        if (capture is not null) window.RootLoadedForCapture(capture);
    }

    bool StartNormalLaunch()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string name = $@"Local\RouteFinder.WinUI.{identity.User?.Value ?? "Default"}";
            if (LaunchInstance.TryActivate(name)) return false;
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The RouteFinder executable was not found.");
                using var elevated = Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(executable)!
                });
                return false;
            }

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            instance = LaunchInstance.Acquire(name, () => dispatcher.TryEnqueue(() => window?.RestoreWindow()));
            return instance is not null;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
        catch (Exception error)
        {
            System.Windows.Forms.MessageBox.Show(error.Message, "Could not open RouteFinder",
                System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            return false;
        }
    }
}
