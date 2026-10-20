using System.Text.Json;
using System.Text.RegularExpressions;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Mods;

/// <summary>One extra drop of a pack's server/drops.json: Item from the mobs (or chests) whose name matches, with Chance × Rates:Loot.</summary>
public sealed record ModDrop(string Pack, string Item, IReadOnlyList<string> Mobs, IReadOnlyList<string> Chests, string? Category, double Chance, int Amount)
{
    internal Regex[] MobPatterns { get; init; } = [];
    internal Regex[] ChestPatterns { get; init; } = [];
}

/// <summary>
/// A content pack's mob variant (studio phase 3, mods/&lt;pack&gt;/server/mobs.json): an official mob (the client's model, name and
/// spells) with factors on its health, damage and fame, extra loot lists, and a chance to turn up instead of the template where the
/// template spawns in the world; GMs spawn one with .mob variant. The client knows nothing of it.
/// </summary>
public sealed record ModVariant(string Pack, string Id, string Template, string? NameEn, string? NameRu, double HealthFactor, double DamageFactor, double FameFactor,
    double ReplaceChance, IReadOnlyList<(string List, double Chance)> Loot);

/// <summary>One entry of a pack loot list: an item (Min..Max) or another list (a pack's or loot.xml's); Weight &gt; 0: a weighted pick, else its own Chance.</summary>
public sealed record ModLootEntry(string? Item, string? List, double Chance, double Weight, int Min, int Max);

/// <summary>A content pack's loot list (mods/&lt;pack&gt;/server/loot.json): the unweighted entries each roll their chance, the weighted ones share Rolls picks.</summary>
public sealed record ModLootList(string Pack, string Id, int Rolls, IReadOnlyList<ModLootEntry> Entries);

/// <summary>
/// The server-only extras of the content packs listed in Mods:Packs (modkit SPEC §4): where their items drop. Read at the start
/// from mods/&lt;pack&gt;/server/drops.json; nothing in game-local changes. Each entry rolls on its own random after the normal
/// loot of a mob kill (<see cref="Systems.Combat"/>) or a chest's first roll (<see cref="Systems.RandomDungeons.OpenChest"/>).
/// </summary>
public sealed class ModDrops
{
    public static readonly ModDrops None = new([]);

    public IReadOnlyList<ModDrop> Drops { get; }

    /// <summary>The rolls' own random (the maps' loot rolls stay as they were).</summary>
    public Random Random { get; set; } = new();

    /// <summary>The packs' mob variants by id (studio phase 3).</summary>
    public IReadOnlyDictionary<string, ModVariant> Variants { get; }

    /// <summary>The packs' loot lists by id (studio phase 3).</summary>
    public IReadOnlyDictionary<string, ModLootList> Lists { get; }

    public ModDrops(IReadOnlyList<ModDrop> drops, IReadOnlyDictionary<string, ModVariant>? variants = null, IReadOnlyDictionary<string, ModLootList>? lists = null)
    {
        Drops = drops;
        Variants = variants ?? new Dictionary<string, ModVariant>();
        Lists = lists ?? new Dictionary<string, ModLootList>();
    }

    private sealed record VariantEntry(string? Id, string? Template, LocEntry? Name, double HealthFactor = 1, double DamageFactor = 1, double FameFactor = 1, double ReplaceChance = 0, VariantLoot[]? Loot = null);

    private sealed record LocEntry(string? En, string? Ru);

    private sealed record VariantLoot(string? List, double Chance = 1);

    private sealed record ListEntry(string? Id, int Rolls = 1, ListItem[]? Entries = null);

    private sealed record ListItem(string? Item, string? List, double Chance = 1, double Weight = 0, int Min = 1, int Max = 1);

    private sealed record MobsRoot(VariantEntry[]? Mobs);

    private sealed record ListsRoot(ListEntry[]? Lists);

    private static T? ReadJson<T>(string path)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (JsonException e) { throw new InvalidDataException($"{path}: {e.Message}", e); }
    }

    /// <summary>server/mobs.json and server/loot.json of a pack (studio phase 3); a bad entry throws, as drops.json's do.</summary>
    private static void LoadContent(string dir, string pack, Dictionary<string, ModVariant> variants, Dictionary<string, ModLootList> lists)
    {
        var mobsPath = Path.Combine(dir, "server", "mobs.json");
        if (File.Exists(mobsPath))
            foreach (var (e, i) in (ReadJson<MobsRoot>(mobsPath)?.Mobs ?? []).Select((e, i) => (e, i)))
            {
                var where = $"{mobsPath} Mobs[{i}]";
                if (e.Id is not { Length: > 0 } || e.Template is not { Length: > 0 }) throw new InvalidDataException($"{where}: Id and Template needed");
                if (e.HealthFactor is <= 0 or > 1000 || e.DamageFactor is <= 0 or > 1000 || e.FameFactor is < 0 or > 1000 || e.ReplaceChance is < 0 or > 1)
                    throw new InvalidDataException($"{where}: factors 0..1000, ReplaceChance 0..1");
                variants[e.Id] = new ModVariant(pack, e.Id, e.Template, e.Name?.En, e.Name?.Ru, e.HealthFactor, e.DamageFactor, e.FameFactor, e.ReplaceChance,
                    [.. (e.Loot ?? []).Where(l => l.List is { Length: > 0 }).Select(l => (l.List!, Math.Clamp(l.Chance, 0, 1)))]);
                Log.Info("mods", $"pack {pack}: mob variant {e.Id} of {e.Template} (health x{e.HealthFactor}, damage x{e.DamageFactor}, replaces {e.ReplaceChance:0.####})");
            }
        var lootPath = Path.Combine(dir, "server", "loot.json");
        if (File.Exists(lootPath))
            foreach (var (e, i) in (ReadJson<ListsRoot>(lootPath)?.Lists ?? []).Select((e, i) => (e, i)))
            {
                if (e.Id is not { Length: > 0 }) throw new InvalidDataException($"{lootPath} Lists[{i}]: Id needed");
                lists[e.Id] = new ModLootList(pack, e.Id, Math.Clamp(e.Rolls, 0, 50),
                    [.. (e.Entries ?? []).Where(x => x.Item is { Length: > 0 } || x.List is { Length: > 0 })
                        .Select(x => new ModLootEntry(x.Item, x.List, Math.Clamp(x.Chance, 0, 1), Math.Max(0, x.Weight), Math.Max(1, x.Min), Math.Max(Math.Max(1, x.Min), x.Max)))]);
            }
    }

    /// <summary>A variant of <paramref name="template"/> that turns up instead of it now (each its ReplaceChance), or null.</summary>
    public ModVariant? ReplaceFor(string template, Random random)
    {
        if (Variants.Count == 0) return null;
        foreach (var v in Variants.Values)
            if (v.ReplaceChance > 0 && string.Equals(v.Template, template, StringComparison.OrdinalIgnoreCase) && random.NextDouble() < v.ReplaceChance) return v;
        return null;
    }

    /// <summary>
    /// One roll of the pack list <paramref name="id"/> (its unweighted entries each by chance, Rolls weighted picks); a name that is
    /// no pack list goes to <paramref name="official"/> (loot.xml) when given.
    /// </summary>
    public List<(string Item, int Amount)> RollList(string id, Random random, Func<string, IReadOnlyList<(string Item, int Amount)>>? official = null, int depth = 0)
    {
        var result = new List<(string, int)>();
        if (depth > 8) return result;
        if (!Lists.TryGetValue(id, out var list))
        {
            if (official is not null) result.AddRange(official(id));
            return result;
        }
        void Give(ModLootEntry e)
        {
            if (e.Item is { Length: > 0 } item) result.Add((item, random.Next(e.Min, e.Max + 1)));
            else if (e.List is { Length: > 0 } sub) result.AddRange(RollList(sub, random, official, depth + 1));
        }
        foreach (var e in list.Entries.Where(e => e.Weight <= 0))
            if (random.NextDouble() < e.Chance) Give(e);
        var weighted = list.Entries.Where(e => e.Weight > 0).ToList();
        var total = weighted.Sum(e => e.Weight);
        for (var r = 0; r < list.Rolls && total > 0; r++)
        {
            var pick = random.NextDouble() * total;
            foreach (var e in weighted)
                if ((pick -= e.Weight) < 0) { Give(e); break; }
        }
        return result;
    }

    private sealed record FileEntry(string? Item, string[]? Mobs, string[]? Chests, string? Category, double Chance, int Amount = 1);

    private sealed record FileRoot(FileEntry[]? Drops);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
    };

    /// <summary>"WORLD_BOSS_*": * any text, the rest literal, ignoring case.</summary>
    private static Regex Pattern(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The drops of <paramref name="packs"/> in <paramref name="modsDir"/>. A pack folder that is missing or a bad file throws
    /// <see cref="InvalidDataException"/> (a typo stops the start); an item the client data lacks (the pack not built into
    /// game-local) is warned about and left out.
    /// </summary>
    public static ModDrops Load(string modsDir, IReadOnlyList<string> packs, ItemStore? items)
    {
        var drops = new List<ModDrop>();
        var variants = new Dictionary<string, ModVariant>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, ModLootList>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in packs)
        {
            var dir = Path.Combine(modsDir, pack);
            if (!Directory.Exists(dir)) throw new InvalidDataException($"Mods:Packs: no pack folder {dir}");
            LoadContent(dir, pack, variants, lists);
            var path = Path.Combine(dir, "server", "drops.json");
            if (!File.Exists(path)) { Log.Info("mods", $"pack {pack}: no server/drops.json"); continue; }
            FileRoot? root;
            try { root = JsonSerializer.Deserialize<FileRoot>(File.ReadAllText(path), Json); }
            catch (JsonException e) { throw new InvalidDataException($"{path}: {e.Message}", e); }
            foreach (var (e, i) in (root?.Drops ?? []).Select((e, i) => (e, i)))
            {
                var where = $"{path} Drops[{i}]";
                if (e.Item is not { Length: > 0 }) throw new InvalidDataException($"{where}: Item missing");
                if (e.Chance is < 0 or > 1 || !double.IsFinite(e.Chance)) throw new InvalidDataException($"{where}: Chance {e.Chance} is not 0..1");
                if (e.Amount is < 1 or > 999) throw new InvalidDataException($"{where}: Amount {e.Amount} is not 1..999");
                if ((e.Mobs?.Length ?? 0) == 0 && (e.Chests?.Length ?? 0) == 0 && e.Category is null)
                    throw new InvalidDataException($"{where}: give Mobs, Chests or Category (where it drops)");
                if (items is not null && items.Find(e.Item) is null)
                {
                    Log.Warn("mods", $"{where}: {e.Item} is not in the client's items (is the pack built into game-local? tools/AlbionLocal.ModKit build {pack}): left out");
                    continue;
                }
                drops.Add(new ModDrop(pack, e.Item, e.Mobs ?? [], e.Chests ?? [], e.Category, e.Chance, e.Amount)
                {
                    MobPatterns = [.. (e.Mobs ?? []).Select(Pattern)],
                    ChestPatterns = [.. (e.Chests ?? []).Select(Pattern)],
                });
            }
        }
        foreach (var d in drops)
            Log.Info("mods", $"pack {d.Pack}: {d.Item} x{d.Amount} at {d.Chance:0.####} from {string.Join(", ", [.. d.Mobs, .. d.Chests.Select(c => "chest " + c), .. d.Category is null ? Array.Empty<string>() : ["category " + d.Category]])}");
        return drops.Count == 0 && variants.Count == 0 && lists.Count == 0 ? None : new ModDrops(drops, variants, lists);
    }

    /// <summary>The mob's drops plus the pack drops that roll for a mob named <paramref name="mob"/> of <paramref name="category"/>.</summary>
    public IReadOnlyList<(string Item, int Amount)> ForMob(string mob, string? category, double lootRate, IReadOnlyList<(string Item, int Amount)> drops,
        string? variant = null, Func<string, IReadOnlyList<(string Item, int Amount)>>? official = null)
    {
        List<(string, int)>? more = null;
        // A variant's own loot lists (studio phase 3), each with its chance x Rates:Loot.
        if (variant is not null && Variants.TryGetValue(variant, out var v))
            foreach (var (list, chance) in v.Loot)
            {
                if (Random.NextDouble() >= Math.Min(1, chance * lootRate)) continue;
                var rolled = RollList(list, Random, official);
                if (rolled.Count == 0) continue;
                (more ??= [.. drops]).AddRange(rolled);
                Log.Info("mods", $"pack {v.Pack}: variant {variant} drops {string.Join(", ", rolled.Select(x => $"{x.Item} x{x.Amount}"))} from {list}");
            }
        if (Drops.Count == 0) return more ?? drops;
        foreach (var d in Drops)
        {
            var named = d.MobPatterns.Any(p => p.IsMatch(mob)) || (variant is not null && d.MobPatterns.Any(p => p.IsMatch(variant)));
            var inCategory = d.Category is not null && string.Equals(d.Category, category, StringComparison.OrdinalIgnoreCase);
            // Mobs and Category together: both must hold; one alone: that one.
            var fits = d.MobPatterns.Length > 0 && d.Category is not null ? named && inCategory : named || inCategory;
            if (!fits || Random.NextDouble() >= Math.Min(1, d.Chance * lootRate)) continue;
            (more ??= [.. drops]).Add((d.Item, d.Amount));
            Log.Info("mods", $"pack {d.Pack}: {mob} drops {d.Item} x{d.Amount}");
        }
        return more ?? drops;
    }

    /// <summary>A chest's first roll plus the pack drops for a chest named <paramref name="chest"/> (lootchests.xml).</summary>
    public IReadOnlyList<(string Item, int Amount)> ForChest(string chest, double lootRate, IReadOnlyList<(string Item, int Amount)> drops)
    {
        if (Drops.Count == 0) return drops;
        List<(string, int)>? more = null;
        foreach (var d in Drops)
        {
            if (!d.ChestPatterns.Any(p => p.IsMatch(chest)) || Random.NextDouble() >= Math.Min(1, d.Chance * lootRate)) continue;
            (more ??= [.. drops]).Add((d.Item, d.Amount));
            Log.Info("mods", $"pack {d.Pack}: chest {chest} holds {d.Item} x{d.Amount}");
        }
        return more ?? drops;
    }
}
