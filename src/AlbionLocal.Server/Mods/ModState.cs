using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Mods;

/// <summary>An installed file: its hash as built and the packs in it.</summary>
public sealed record InstalledFile(string Sha256, List<string> Packs);

/// <summary>
/// modkit-state.json next to game-local's GameData folder (in StreamingAssets, a new file: no link to the official install):
/// the official hashes the modkit started from (kept across restores, to notice an official update), what is installed, and
/// the ids the build gave the new items and spells, which the server checks at every start (<see cref="Verify"/>).
/// </summary>
public sealed record ModState
{
    public const string FileName = "modkit-state.json";

    /// <summary>The official client build and file hashes of the first build (or the last --force).</summary>
    public string? ClientBuild { get; set; }
    public Dictionary<string, string> Pristine { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, InstalledFile> Installed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Packs { get; set; } = [];
    public DateTime? BuiltUtc { get; set; }
    public ModNumbering? Items { get; set; }
    public ModNumbering? Spells { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathFor(string localGameData) => Path.GetFullPath(Path.Combine(localGameData, "..", FileName));

    public static ModState? Load(string localGameData)
    {
        var path = PathFor(localGameData);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ModState>(File.ReadAllText(path)); }
        catch (JsonException e) { throw new InvalidDataException($"{path}: {e.Message}", e); }
    }

    public void Save(string localGameData) => File.WriteAllText(PathFor(localGameData), JsonSerializer.Serialize(this, Options));

    /// <summary>
    /// The server's start-up check (ClientData.Load): with modded files in <paramref name="folder"/>, every official item and spell
    /// must still have its id (the fingerprint of the first PristineCount names in id order) and every new one the id the modkit
    /// gave it, else the server refuses to start (an id that moved would give the players other items than they see). A file
    /// changed after the build is warned about. Nothing happens without a state file or with nothing installed.
    /// </summary>
    public static void Verify(string folder, ItemStore items, SpellData spells, IReadOnlyDictionary<string, string> hashes)
    {
        var state = Load(folder);
        if (state is null || state.Installed.Count == 0) return;
        var problems = new List<string>();
        if (state.Items is { } i)
        {
            if (items.Count < i.PristineCount || ModBuilder.NamesFingerprint(items.All.Select(t => t.Name), i.PristineCount) != i.PristineNamesSha)
                problems.Add($"the {i.PristineCount} official item ids differ from the modkit's build");
            foreach (var (name, id) in i.New)
                if (items.Find(name)?.Index != id) problems.Add($"item {name} has id {items.Find(name)?.Index.ToString() ?? "none"}, the modkit gave it {id}");
        }
        if (state.Spells is { } s)
        {
            if (spells.Count < s.PristineCount || ModBuilder.NamesFingerprint(Enumerable.Range(0, s.PristineCount).Select(id => spells.Name(id) ?? ""), s.PristineCount) != s.PristineNamesSha)
                problems.Add($"the {s.PristineCount} official spell ids differ from the modkit's build");
            foreach (var (name, id) in s.New)
                if (spells.IdOf(name) != id) problems.Add($"spell {name} has id {spells.IdOf(name)}, the modkit gave it {id}");
        }
        if (problems.Count > 0)
            throw new InvalidDataException($"{PathFor(folder)}: the modded GameData does not match the modkit's build ({string.Join("; ", problems)}); "
                + "run the modkit's build again (or restore) before starting the server");
        foreach (var (file, installed) in state.Installed)
            if (hashes.GetValueOrDefault(file) is { } now && now != installed.Sha256)
                Log.Warn("mods", $"{file} changed since the modkit installed it (sha256 {installed.Sha256[..12]}... -> {now[..12]}...): run the modkit's status");
        Log.Info("mods", $"modded client data: packs {string.Join(", ", state.Packs)} in {string.Join(", ", state.Installed.Keys)}; "
            + $"{state.Items?.New.Count ?? 0} new items and {state.Spells?.New.Count ?? 0} new spells at the ids the modkit gave them");
    }
}
