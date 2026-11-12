using System.Collections.Concurrent;
using System.Text;

namespace RouteFinder.Core;

/// <summary>
/// Detailed plain-text log of everything RouteFinder decides and observes: routing choices, adapter rules, tunnel
/// steps, Windows network changes, and periodic snapshots. Written on a background thread so callers never wait.
/// Files live in %LOCALAPPDATA%\RouteFinder\logs, roll over at 20 MB, and the newest 15 are kept.
/// Never pass private keys, tokens, passwords, or proxy credentials to it.
/// </summary>
public static class DiagnosticLog
{
    const long MaxFileBytes = 20L * 1024 * 1024;
    const int KeepFiles = 15;
    static readonly BlockingCollection<string> queue = new(50_000);
    static readonly object startGate = new();
    static Thread? writer;
    static string? folder;
    static string? currentPath;
    static int dropped;

    public static bool Enabled => folder is not null;
    public static string? Folder => folder;
    public static string? CurrentFile => currentPath;

    public static void Start(string logFolder)
    {
        lock (startGate)
        {
            if (folder is not null) return;
            Directory.CreateDirectory(logFolder);
            folder = logFolder;
            Prune();
            currentPath = NewPath();
            restart = true;
            if (writer is not null) return;
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "RouteFinder log", Priority = ThreadPriority.BelowNormal };
            writer.Start();
        }
    }

    /// <summary>Stops writing; entries already queued still reach the file. Start opens a new file.</summary>
    public static void Stop()
    {
        lock (startGate) { folder = null; }
    }

    static volatile bool restart;

    /// <summary>Writes one entry. Continuation lines are indented under it.</summary>
    public static void Write(string category, string message)
    {
        if (folder is null) return;
        var line = new StringBuilder(message.Length + 48);
        line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(category.PadRight(9)).Append(' ');
        bool first = true;
        foreach (string part in message.Replace("\r\n", "\n").Split('\n'))
        {
            if (!first) line.Append('\n').Append(' ', 34);
            line.Append(part);
            first = false;
        }
        if (!queue.TryAdd(line.ToString())) Interlocked.Increment(ref dropped);
    }

    public static void Error(string category, string context, Exception error) =>
        Write(category, $"{context}: {error.GetType().Name}: {error.Message}\n{error}");

    /// <summary>Waits briefly until queued entries are on disk, for exit and for reports.</summary>
    public static void Flush(int milliseconds = 1500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Thread.Sleep(30);
    }

    /// <summary>Last lines of the current file, for support reports.</summary>
    public static string[] Tail(int lines)
    {
        if (currentPath is null || !File.Exists(currentPath)) return [];
        Flush(800);
        try
        {
            using var stream = new FileStream(currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new Queue<string>(lines);
            while (reader.ReadLine() is { } line)
            {
                if (buffer.Count == lines) buffer.Dequeue();
                buffer.Enqueue(line);
            }
            return buffer.ToArray();
        }
        catch (IOException) { return []; }
    }

    static string NewPath() => Path.Combine(folder!, $"routefinder-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");

    static void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(folder!).GetFiles("routefinder-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc).Skip(KeepFiles - 1))
                old.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    static void WriteLoop()
    {
        StreamWriter? output = null;
        try
        {
            foreach (string line in queue.GetConsumingEnumerable())
            {
                try
                {
                    if (restart) { output?.Dispose(); output = null; restart = false; }
                    output ??= Open(currentPath!);
                    int lost = Interlocked.Exchange(ref dropped, 0);
                    if (lost > 0) output.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} LOG       {lost} entries were dropped (queue full).");
                    output.WriteLine(line);
                    if (queue.Count == 0) output.Flush();
                    if (output.BaseStream.Length > MaxFileBytes)
                    {
                        output.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} LOG       Continued in a new file.");
                        output.Dispose();
                        currentPath = NewPath();
                        Prune();
                        output = Open(currentPath);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    output?.Dispose();
                    output = null;
                    Thread.Sleep(500);
                }
            }
        }
        finally { output?.Dispose(); }
    }

    static StreamWriter Open(string path) =>
        new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
}
