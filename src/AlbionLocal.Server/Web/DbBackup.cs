using AlbionLocal.Photon;
using Microsoft.Data.Sqlite;

namespace AlbionLocal.Server.Web;

/// <summary>
/// Copies of the SQLite database while the server runs (SPEC §4): VACUUM INTO on a connection of its own writes a consistent
/// snapshot (SQLite takes a read transaction; the save writer waits at most for that, never sees a half copy), into
/// Paths:Saves/backups/albionlocal-yyyyMMdd-HHmmss.db; then only the newest <see cref="BackupOptions.Keep"/> stay.
/// Never under the world lock: it does not touch world state. One backup at a time.
/// </summary>
public sealed class DbBackup : IDisposable
{
    private const string Prefix = "albionlocal-";
    private readonly string _database;
    private readonly BackupOptions _options;
    private readonly object _gate = new();
    private readonly Timer? _timer;
    private readonly Action? _prepare;

    /// <param name="prepare">Run before an automatic backup, off the world thread: the unsaved characters written first.</param>
    public DbBackup(string database, string folder, BackupOptions options, Action? prepare = null)
    {
        _database = database;
        _prepare = prepare;
        Folder = folder;
        _options = options;
        if (options.EveryHours > 0)
        {
            var period = TimeSpan.FromHours(options.EveryHours);
            _timer = new Timer(_ => Auto(), null, period, period);
            Log.Info("backup", $"a database backup every {options.EveryHours:0.##} h into {folder}, the newest {options.Keep} kept (Backup:EveryHours, Backup:Keep)");
        }
    }

    /// <summary>Paths:Saves/backups.</summary>
    public string Folder { get; }

    private void Auto()
    {
        try
        {
            _prepare?.Invoke();
            Run("automatic");
        }
        catch (Exception e) { Log.Error("backup", $"automatic backup failed: {e.Message}"); }
    }

    /// <summary>Makes one backup now; its path. Throws (SqliteException, IOException) when it fails.</summary>
    public string Run(string why)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(Folder, $"{Prefix}{stamp}.db");
            for (var n = 2; File.Exists(path); n++) path = Path.Combine(Folder, $"{Prefix}{stamp}-{n}.db");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _database, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "VACUUM INTO $path";
                cmd.Parameters.AddWithValue("$path", path);
                cmd.CommandTimeout = 60;
                cmd.ExecuteNonQuery();
            }
            var size = new FileInfo(path).Length;
            Log.Info("backup", $"{why} backup: {path} ({size / 1024.0:0.#} KB, {watch.ElapsedMilliseconds} ms)");
            Prune();
            return path;
        }
    }

    /// <summary>The backups, newest first.</summary>
    public IReadOnlyList<FileInfo> List() =>
        Directory.Exists(Folder)
            ? [.. new DirectoryInfo(Folder).GetFiles(Prefix + "*.db").OrderByDescending(f => f.Name, StringComparer.Ordinal)]
            : [];

    private void Prune()
    {
        foreach (var old in List().Skip(_options.Keep))
        {
            try
            {
                old.Delete();
                Log.Info("backup", $"old backup {old.Name} removed (Backup:Keep {_options.Keep})");
            }
            catch (IOException e) { Log.Warn("backup", $"cannot remove {old.Name}: {e.Message}"); }
        }
    }

    public void Dispose() => _timer?.Dispose();
}
