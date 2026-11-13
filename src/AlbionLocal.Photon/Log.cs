namespace AlbionLocal.Photon;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>Minimal console logger (no external packages); <see cref="SetFile"/> also appends every line to a file.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static string? _filePath;
    private static long _fileBytes;
    private static long _rotateBytes = RotateBytes;

    public static LogLevel Level { get; set; } = LogLevel.Info;

    public static void Write(LogLevel level, string source, string message)
    {
        if (level < Level) return;
        Emit(level, source, message);
    }

    /// <summary>An info line written whatever <see cref="Level"/> says (replies to console commands).</summary>
    public static void Always(string source, string message) => Emit(LogLevel.Info, source, message);

    /// <summary>
    /// Size (counted in characters while running) at which the log file is renamed to "&lt;file&gt;.1", replacing an
    /// older one, and started anew: at <see cref="SetFile"/> and while running, so long -v/-vv runs (a dump per request)
    /// cannot fill the disk.
    /// </summary>
    public const long RotateBytes = 16 << 20;

    /// <summary>
    /// Appends every following line to <paramref name="path"/> too (created with its folder; one writer, shared for
    /// reading, so it can be followed with tail -F while the server runs); null closes the file. A file that cannot be
    /// opened, e.g. the one a server already running here writes, is reported on the console and logging goes on
    /// without it. <paramref name="rotateBytes"/>: see <see cref="RotateBytes"/>.
    /// </summary>
    public static void SetFile(string? path, long rotateBytes = RotateBytes)
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
            _filePath = null;
            if (path is null) return;
            _rotateBytes = rotateBytes;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                OpenFile(path);
                var header = $"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss} log opened";
                _file!.WriteLine(header);
                _fileBytes += header.Length + Environment.NewLine.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _file = null;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Warn  [log] cannot write {path}: {e.Message}; console only");
            }
        }
    }

    /// <summary>
    /// Opens the file for appending, renamed first when it has reached the limit. Write sharing is denied: two appending
    /// handles each keep their own offset and write over each other's lines (a second server started next to a running
    /// one opens the log before it fails on the port).
    /// </summary>
    private static void OpenFile(string path)
    {
        FileStream Open() => new(path, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        var stream = Open();
        if (stream.Length >= _rotateBytes)
        {
            stream.Dispose();
            try { File.Move(path, path + ".1", overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Warn  [log] cannot rename {path}: {e.Message}; it keeps growing");
            }
            stream = Open();
        }
        _file = new StreamWriter(stream) { AutoFlush = true };
        _filePath = path;
        // A file that could not be renamed is tried again after another limit's worth of lines.
        _fileBytes = stream.Length < _rotateBytes ? stream.Length : 0;
    }

    private static long _writingSince; // Environment.TickCount64 + 1 while a line is being written, 0 otherwise

    /// <summary>
    /// How long the line being written has taken so far (zero when none is). A console that stops taking output (a Windows console
    /// with a QuickEdit selection, a paused terminal) blocks the writer, and every thread that logs waits behind it, the world thread
    /// too while it holds the world lock (public issue #28): the server's watchdog reads this without the lock.
    /// </summary>
    public static TimeSpan WritingFor => Interlocked.Read(ref _writingSince) is var since and > 0
        ? TimeSpan.FromMilliseconds(Environment.TickCount64 + 1 - since) : TimeSpan.Zero;

    /// <summary>
    /// <paramref name="line"/> appended to "&lt;log file&gt;.watchdog.txt" without the logger's lock (it may be the one that is stuck); nothing
    /// without a log file. For the server's watchdog.
    /// </summary>
    public static void WriteAside(string line)
    {
        if (Volatile.Read(ref _filePath) is not { } path) return;
        try { File.AppendAllText(path + ".watchdog.txt", $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void Emit(LogLevel level, string source, string message)
    {
        lock (Gate)
        {
            Interlocked.Exchange(ref _writingSince, Environment.TickCount64 + 1);
            try
            {
                var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} [{source}] {message}";
                var old = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Trace => ConsoleColor.DarkGray,
                    LogLevel.Debug => ConsoleColor.Gray,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Error => ConsoleColor.Red,
                    _ => ConsoleColor.White,
                };
                Console.WriteLine(line);
                Console.ForegroundColor = old;
                if (_file is null) return;
                try
                {
                    _file.WriteLine(line);
                    if ((_fileBytes += line.Length + Environment.NewLine.Length) >= _rotateBytes)
                    {
                        _file.Dispose();
                        _file = null;
                        OpenFile(_filePath!);
                    }
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException)
                {
                    // A full disk or a removed drive: say so once and go on with the console.
                    _file = null;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Warn  [log] writing {_filePath} failed: {e.Message}; console only");
                }
            }
            finally { Interlocked.Exchange(ref _writingSince, 0); }
        }
    }

    public static void Trace(string src, string msg) => Write(LogLevel.Trace, src, msg);
    public static void Debug(string src, string msg) => Write(LogLevel.Debug, src, msg);
    public static void Info(string src, string msg) => Write(LogLevel.Info, src, msg);
    public static void Warn(string src, string msg) => Write(LogLevel.Warn, src, msg);
    public static void Error(string src, string msg) => Write(LogLevel.Error, src, msg);
}
