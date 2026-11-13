using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace RouteFinder.WinUI;

/// <summary>
/// Start with Windows. RouteFinder needs administrator rights, so a plain Run-key entry would raise a UAC prompt at
/// every sign-in. A scheduled task that runs at logon with highest privileges starts it elevated without a prompt.
/// The installer and Settings both use this; the task is the single source of truth for whether it is enabled.
/// </summary>
internal static class StartupTask
{
    internal const string TaskName = "RouteFinder startup";
    internal const string Argument = "--startup";

    /// <summary>Task definition: at logon of the current user, a short delay so the network is up, no time limit, battery-safe.</summary>
    internal static string BuildXml(string executable, string user, bool highest = true)
    {
        string path = SecurityElement.Escape(executable)!;
        string folder = SecurityElement.Escape(Path.GetDirectoryName(executable) ?? "")!;
        string account = SecurityElement.Escape(user)!;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts RouteFinder in the tray when you sign in to Windows.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{account}</UserId><Delay>PT10S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{account}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>{(highest ? "HighestAvailable" : "LeastPrivilege")}</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{path}</Command><Arguments>{Argument}</Arguments><WorkingDirectory>{folder}</WorkingDirectory></Exec>
              </Actions>
            </Task>
            """;
    }

    internal static bool Exists() => Run("/Query", "/TN", TaskName).ExitCode == 0;

    /// <summary>Turns the task on (pointing at <paramref name="executable"/>) or off. Returns null on success, else the reason.</summary>
    internal static string? Set(bool enabled, string? executable = null, bool highest = true)
    {
        if (!enabled)
        {
            if (!Exists()) return null;
            var removed = Run("/Delete", "/TN", TaskName, "/F");
            return removed.ExitCode == 0 ? null : removed.Output;
        }
        executable ??= Environment.ProcessPath;
        if (executable is null || !File.Exists(executable)) return "RouteFinder's file was not found.";
        string file = Path.Combine(Path.GetTempPath(), $"routefinder-startup-{Environment.ProcessId}.xml");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            File.WriteAllText(file, BuildXml(executable, identity.Name, highest), Encoding.Unicode);
            var created = Run("/Create", "/TN", TaskName, "/XML", file, "/F");
            return created.ExitCode == 0 ? null : created.Output;
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }

    static (int ExitCode, string Output) Run(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("schtasks did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000)) { try { process.Kill(); } catch (InvalidOperationException) { } return (-1, "Task Scheduler did not respond."); }
            return (process.ExitCode, (output.Result + " " + errors.Result).Trim());
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return (-1, error.Message);
        }
    }
}
