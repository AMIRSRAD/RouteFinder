using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace RouteFinder.WinUI;

/// <summary>
/// Finds executables for apps and catalog games: among running processes (including protected ones such as
/// anti-cheat), and on disk near files already found or in the app's install folder from the Windows uninstall list.
/// </summary>
public sealed partial class MainWindow
{
    // Reads a process's executable path with the limited rights Windows grants even for protected processes.
    // Process.MainModule needs far more access and fails for anti-cheat, launchers, and services.
    static string? ProcessImagePath(int processId)
    {
        nint handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    static readonly EnumerationOptions DeepSearch = new()
    {
        RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
    };

    // Folders too broad to search: drive roots and shared system or profile folders.
    static bool IsBroadFolder(string folder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (Path.GetPathRoot(full) is { } root && full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return true;
        var broad = new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.CommonProgramFiles
        }.Select(Environment.GetFolderPath).Where(path => path.Length > 0).Select(Path.TrimEndingDirectorySeparator);
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        return broad.Append(programs).Append(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "")
            .Any(path => path.Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Searches installed files for the given executable names. Search roots are the folders of related files that
    /// were already found (and up to three parents, short of shared folders), plus install folders from the Windows
    /// uninstall list whose name matches <paramref name="title"/>. When several copies exist, the newest wins.
    /// </summary>
    static string[] FindInstalledApps(IEnumerable<string> names, IEnumerable<string> related, string? title)
    {
        var stems = names.Select(ExecutableStem).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (stems.Count == 0) return [];
        var roots = new List<string>();
        void AddRoot(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            try
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim().Trim('"')));
                if (!Directory.Exists(full) || IsBroadFolder(full)) return;
                // A folder already inside another root adds nothing.
                if (roots.Any(root => full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    full.Equals(root, StringComparison.OrdinalIgnoreCase))) return;
                roots.RemoveAll(root => root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                roots.Add(full);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException) { }
        }
        foreach (string path in related)
        {
            // Climb from the app's folder toward its install root, stopping before shared folders.
            string? folder = Path.GetDirectoryName(path);
            for (int level = 0; level < 4 && folder is not null && !IsBroadFolder(folder); level++)
            {
                string? parent = Path.GetDirectoryName(folder);
                if (parent is null || IsBroadFolder(parent)) break;
                folder = parent;
            }
            AddRoot(folder);
        }
        foreach (string folder in UninstallFolders(title)) AddRoot(folder);

        var found = new Dictionary<string, (string Path, DateTime Written)>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.exe", DeepSearch))
                {
                    string stem = ExecutableStem(file);
                    if (!stems.Contains(stem)) continue;
                    var written = File.GetLastWriteTimeUtc(file);
                    if (!found.TryGetValue(stem, out var best) || written > best.Written) found[stem] = (file, written);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return found.Values.Select(item => item.Path).ToArray();
    }

    // Install folders of programs whose uninstall entry is named like the game or app.
    static IEnumerable<string> UninstallFolders(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length < 3) yield break;
        string name = title.Trim();
        var hives = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
        };
        foreach (var (hive, path) in hives)
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) continue;
            foreach (string sub in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(sub);
                if (entry?.GetValue("DisplayName") is not string display ||
                    !display.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.GetValue("InstallLocation") is string location && location.Length > 0) yield return location;
                foreach (string value in new[] { "DisplayIcon", "UninstallString" })
                    if (entry.GetValue(value) is string command && ExecutableInCommand(command) is { } exe)
                        yield return Path.GetDirectoryName(exe) ?? "";
            }
        }
    }

    // Pulls the executable path out of values like "C:\App\app.exe",0 or "C:\App\uninstall.exe" --flag.
    static string? ExecutableInCommand(string command)
    {
        string text = command.Trim();
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : null;
    }

    /// <summary>Read-only diagnostic for --detect-apps: what running and on-disk detection find for a game.</summary>
    internal static string DiagnoseDetection(string title, string[] names, string[] related)
    {
        var running = FindRunningApps(new HashSet<string>(StringComparer.OrdinalIgnoreCase), names, false);
        var installed = FindInstalledApps(names.Where(name => !running.Any(path => ExecutableMatches(path, name))), related, title);
        var lines = new List<string> { $"{title}: {names.Length} names, {related.Length} related files" };
        foreach (string name in names)
        {
            string? hit = running.FirstOrDefault(path => ExecutableMatches(path, name));
            string? file = installed.FirstOrDefault(path => ExecutableMatches(path, name));
            lines.Add($"  {name}: {(hit is not null ? "running " + hit : file is not null ? "on disk " + file : "not found")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);
}
