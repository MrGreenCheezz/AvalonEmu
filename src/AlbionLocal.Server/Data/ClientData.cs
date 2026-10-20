using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The static client data the server runs on (ARCHITECTURE.md §5.1, TC's DBC stores): the item templates, the stat and
/// destiny board tables and the spell tables, built at startup from the .bin files of the client's GameData folder
/// (Paths:ClientGameData, the client install's GameData files). With them, what was loaded: the client
/// build and a SHA-256 per file, logged and kept in <see cref="RecordFile"/> next to the saves (<see cref="Record"/>),
/// so that an official update reaching game-local through its hard links is noticed at the next start.
/// GameData and Spells are null only in hand-made tables (self-test): the server then sends default stats, no
/// destiny board and no spells, as before without the generated files. The cluster data (<see cref="Clusters"/>) is
/// read later, lazily, at the first Join into a cluster.
/// </summary>
public sealed record ClientData(ItemStore Items, GameData? GameData, SpellData? Spells)
{
    /// <summary>The fingerprint of the last start, in Paths:Saves.</summary>
    public const string RecordFile = "clientdata.json";

    /// <summary>The GameData files the tables come from.</summary>
    public static readonly string[] FileNames = ["items.bin", "spells.bin", "gamedata.bin", "characters.bin", "achievements.bin", "craftingmodifiers.bin", "legendaryitems.bin"];

    /// <summary>The client build: the first line of the client's version.txt ("albiononline-win32-full-1.32.030.345319"); null = unknown.</summary>
    public string? Build { get; init; }

    /// <summary>SHA-256 (lower-case hex) per .bin file used, by file name; empty for hand-made tables.</summary>
    public IReadOnlyDictionary<string, string> Hashes { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The clusters of the same GameData folder (world.bin, tiles.bin, the .cluster and template files, buildings and
    /// harvestables), read when a cluster is first asked for, not here; null for hand-made tables (no world objects).
    /// Its files are not in <see cref="Hashes"/>: they are not read at startup.
    /// </summary>
    public ClusterStore? Clusters { get; init; }

    /// <summary>craftingmodifiers.bin: the crafting and refining bonus per city (stage 6); <see cref="CraftingModifiers.None"/> for hand-made tables.</summary>
    public CraftingModifiers Crafting { get; init; } = CraftingModifiers.None;

    /// <summary>legendaryitems.bin: awakened weapons and their traits (stageN awakening SPEC); null for hand-made tables.</summary>
    public LegendaryData? Legendary { get; init; }

    /// <summary>
    /// Reads the tables from <paramref name="folder"/> and logs the build, the hashes and the load time. A missing folder
    /// or file, or one that does not decrypt or parse, throws <see cref="InvalidDataException"/> naming it: the server
    /// does not start without its data (principle 6). The cluster store is only set up (<paramref name="clusters"/>:
    /// World:EventLayers, ClientNumbering:BuildingIdOffset); its files are read when a cluster is first needed.
    /// </summary>
    public static ClientData Load(string folder, ClusterOptions? clusters = null)
    {
        if (!Directory.Exists(folder))
            throw new InvalidDataException($"Paths:ClientGameData: {folder} not found (the client's Albion-Online_Data/StreamingAssets/GameData folder)");
        var watch = Stopwatch.StartNew();
        var files = FileNames.ToDictionary(name => name, name => GameDataFile.Open(folder, name));
        var items = ItemStore.Load(files["items.bin"]);
        var gameData = GameData.FromClient(items, files["gamedata.bin"].Root(), files["characters.bin"].Root(), files["achievements.bin"].Elements());
        var spells = SpellData.FromClient(files["spells.bin"].Elements(), items);
        // A modkit build (tools/AlbionLocal.ModKit): the new items and spells must have the ids it gave them, the official ones theirs.
        Mods.ModState.Verify(folder, items, spells, files.Values.ToDictionary(f => f.Name, f => f.Sha256));
        watch.Stop();
        var build = ReadBuild(folder);
        if (build is null) Log.Warn("data", $"client build unknown: no version.txt in or above {folder}");
        else Log.Info("data", $"client build {build}");
        foreach (var f in files.Values)
            Log.Info("data", $"  {f.Name,-16} {f.Length,7} bytes  sha256 {f.Sha256}");
        Log.Info("data", string.Create(CultureInfo.InvariantCulture,
            $"client data: {items.Count} items, {spells.Count} spells ({spells.Items.Count} items with spell slots), "
            + $"{gameData.Achievements.Length} achievements from {folder} in {watch.Elapsed.TotalSeconds:0.00} s"));
        // The start-up report of ARCHITECTURE.md §6.4: which spell effect kinds run, and the most used ones that do not yet.
        var tags = spells.EffectTags();
        var noOps = tags.Keys.Count(Systems.SpellCasts.NoOps.Contains);
        var missing = tags.Where(t => !Systems.SpellCasts.Supported.Contains(t.Key) && !Systems.SpellCasts.NoOps.Contains(t.Key))
            .OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
        Log.Info("data", $"spell effects: {tags.Count - missing.Count - noOps} of {tags.Count} kinds run, {noOps} as no-ops; not yet ({missing.Sum(t => t.Value)} uses): "
            + string.Join(", ", missing.Take(10).Select(t => $"{t.Key} {t.Value}")) + (missing.Count > 10 ? ", …" : ""));
        return new ClientData(items, gameData, spells)
        {
            Build = build,
            Hashes = files.Values.ToDictionary(f => f.Name, f => f.Sha256),
            Crafting = CraftingModifiers.Load(files["craftingmodifiers.bin"]),
            Legendary = LegendaryData.Load(files["legendaryitems.bin"]),
            Clusters = new ClusterStore(folder, clusters),
        };
    }

    /// <summary>The first line of the client's version.txt, searched from the GameData folder upwards (it sits next to Albion-Online.exe).</summary>
    private static string? ReadBuild(string folder)
    {
        var dir = new DirectoryInfo(folder);
        for (var up = 0; dir is not null && up < 5; up++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "version.txt");
            if (!File.Exists(path)) continue;
            try { return File.ReadLines(path).FirstOrDefault()?.Trim() is { Length: > 0 } line ? line : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
        return null;
    }

    /// <summary>What <see cref="RecordFile"/> holds.</summary>
    private sealed record Fingerprint(string? Build, SortedDictionary<string, string> Files);

    /// <summary>
    /// Compares the build and the hashes with <see cref="RecordFile"/> in <paramref name="savesPath"/> (written by the
    /// previous start), warns about every difference and stores the current ones. Returns the differences (none on the
    /// first start and for hand-made tables). The file is only rewritten when something differs.
    /// </summary>
    public IReadOnlyList<string> Record(string savesPath)
    {
        if (Hashes.Count == 0) return [];
        var path = Path.Combine(savesPath, RecordFile);
        var now = new Fingerprint(Build, new SortedDictionary<string, string>(Hashes.ToDictionary()));
        var options = new JsonSerializerOptions { WriteIndented = true };
        Fingerprint? old = null;
        try
        {
            if (File.Exists(path)) old = JsonSerializer.Deserialize<Fingerprint>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn("data", $"{path}: {e.Message}; written anew");
        }
        var changes = new List<string>();
        if (old is not null)
        {
            if (old.Build != Build) changes.Add($"client build changed since the last start: {old.Build ?? "unknown"} -> {Build ?? "unknown"}");
            foreach (var (file, hash) in now.Files)
                if (old.Files?.GetValueOrDefault(file) is { } before && before != hash)
                    changes.Add($"{file} changed since the last start (sha256 {before[..Math.Min(12, before.Length)]}... -> {hash[..12]}...)");
        }
        foreach (var change in changes) Log.Warn("data", change);
        if (changes.Count > 0)
            Log.Warn("data", "the client's GameData differs from the last start (an official update of game-local?): ids and formats may have moved, "
                + "check the golden tests (data/items.tsv, gamedata.json, spells.json) and the protocol");
        var text = JsonSerializer.Serialize(now, options);
        if (old is not null && JsonSerializer.Serialize(old, options) == text) return changes;
        try
        {
            Directory.CreateDirectory(savesPath);
            File.WriteAllText(path, text);
            if (old is null) Log.Info("data", $"client build and hashes recorded in {path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("data", $"cannot write {path}: {e.Message}");
        }
        return changes;
    }
}
