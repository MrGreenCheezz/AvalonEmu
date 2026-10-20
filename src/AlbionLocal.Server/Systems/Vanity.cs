using System.Globalization;
using System.Text.RegularExpressions;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using Unlock = AlbionLocal.Server.Data.VanityCatalog.Unlock;
using UnlockType = AlbionLocal.Server.Data.VanityCatalog.UnlockType;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageN vanity (analysis/work/stageN/vanity/SPEC.md): cosmetics without purchases. The vanity store shows the client's catalogue; nothing in it
/// is sold (BuyVanityUnlocks is refused with a line saying so). Wardrobe skins and mount skins drop very rarely from content (<see cref="VanityOptions"/>):
/// a drop unlocks one for the character (event 408 and a chat line); the account owns the union of its characters' unlocks, as the client keeps
/// them per account. An owned skin goes on with the client's own windows: the wardrobe (SetWardrobe 387 → UpdateWardrobe 416, Join 108,
/// NewCharacter 55) and the mount skins (GetMountSkins 385, SetMountSkin 386, NewMountObject key 2, Mounted key 3). Under World.Sync.
/// </summary>
public sealed class Vanity(World world)
{
    /// <summary>The refusal RC of BuyVanityUnlocks and a refused SetMountSkin (any non-zero: the client shows its generic failure).</summary>
    public const short Refused = 1;

    public const string NotForSale = "Cosmetics cannot be bought on this server: they drop, very rarely, from bosses, boss chests, hunts, arenas and other content.";

    private VanityCatalog? _catalog;
    private List<Unlock>? _pool;

    public VanityOptions Options => world.Config.Vanity;

    /// <summary>The drops' random (a test sets its own).</summary>
    public Random Random { get; set; } = new();

    /// <summary>The client's vanity data, read at the first use; <see cref="VanityCatalog.Empty"/> without it (logged once).</summary>
    public VanityCatalog Catalog
    {
        get
        {
            if (_catalog is not null) return _catalog;
            try
            {
                if (world.Clusters is not { } store) throw new InvalidDataException("no client data");
                _catalog = VanityCatalog.Load(store.Folder, world.Items);
                Log.Info("data", $"vanity: {_catalog.All.Count} unlocks, {Pool.Count} droppable skins, {_catalog.MountCategories.Count} mount categories");
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                _catalog = VanityCatalog.Empty;
                Log.Warn("data", $"vanity data: {e.Message}; no cosmetics");
            }
            return _catalog;
        }
    }

    /// <summary>What drops: the wardrobe and mount skins the client shows as Locked or Buyable (not the Hidden ones, nor the free ones).</summary>
    public IReadOnlyList<Unlock> Pool => _pool ??= [.. Catalog.All.Where(u => u.Type is UnlockType.Skin or UnlockType.Mount && u.State is "Locked" or "Buyable")];

    // ---- ownership ----

    public static VanityLog LogOf(Character c) => c.Vanity ??= new VanityLog();

    /// <summary>The unlock ids of <paramref name="c"/>'s account: its own and its other characters' (stored).</summary>
    public HashSet<short> Owned(Character c)
    {
        var set = new HashSet<short>(c.Vanity?.Unlocks ?? []);
        if (c.Account != Guid.Empty)
            try
            {
                foreach (var other in world.Characters.List(c.Account))
                    if (other.Id != c.Id && other.Vanity is { } v) set.UnionWith(v.Unlocks);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { Log.Warn("vanity", $"account unlocks of '{c.Name}': {e.Message}"); }
        return set;
    }

    public bool Has(Character c, Unlock u) => u.Free || Owned(c).Contains(u.Id);

    /// <summary>GetVanityUnlocks key 0: the account's unlock ids, sorted.</summary>
    public short[] UnlockIds(Character c) => [.. Owned(c).Where(id => Catalog.Find(id) is not null).Order()];

    /// <summary>
    /// <paramref name="u"/> for <paramref name="p"/> from <paramref name="source"/>: kept, event 408 {0 id, 1 true} (the client's unlock popup and
    /// its cache) and a chat line. False when the account has it already.
    /// </summary>
    public bool Grant(Player p, Unlock u, string source)
    {
        if (Owned(p.Character).Contains(u.Id)) return false;
        LogOf(p.Character).Unlocks.Add(u.Id);
        world.Saves.MarkChanged(p.Character);
        p.Session.Send(new UnlockVanityUnlockEvent { d2f = true, Extra = new() { [0] = u.Id } });
        Expeditions.Tell(p, $"Cosmetic unlocked: {Describe(u)} ({source}). Apply it in the character window (vanity tab) or the vanity store.");
        Log.Info("vanity", $"'{p.Character.Name}' unlocked {u} ({source})");
        return true;
    }

    /// <summary>Takes <paramref name="u"/> from <paramref name="c"/> (the GM's .vanity revoke) and off its wardrobe or mount skins. False when it had none.</summary>
    public bool Revoke(Character c, Unlock u)
    {
        if (c.Vanity is not { } log || !log.Unlocks.Remove(u.Id)) return false;
        for (var i = 0; i < log.Wardrobe.Length; i++)
            if (u.Type == UnlockType.Skin && log.Wardrobe[i] == u.Number) log.Wardrobe[i] = -1;
        foreach (var k in log.MountSkins.Where(kv => u.Type == UnlockType.Mount && kv.Value == u.Number).Select(kv => kv.Key).ToList()) log.MountSkins.Remove(k);
        world.Saves.MarkChanged(c);
        return true;
    }

    public static string Describe(Unlock u) => u.Type switch
    {
        UnlockType.Mount => $"mount skin {u.Name}",
        UnlockType.Skin => $"{(u.Slot is >= 0 and < 7 ? SlotNames[u.Slot] : "")} skin {u.Name}",
        _ => $"{u.Type.ToString().ToLowerInvariant()} {u.Name}",
    };

    private static readonly string[] SlotNames = ["weapon", "off-hand", "head", "armor", "shoes", "bag", "cape"];

    // ---- drops ----

    /// <summary>
    /// The themes of the sources (SPEC §3.2): a mob's, a chest's or a hunt target's name with one of the words takes its skins (the names that match
    /// the pattern) with Vanity:ThemeChance. Avalonian content has no skins of its own: it draws from the whole pool.
    /// </summary>
    private static readonly (string Theme, string[] Words, Regex Skins)[] Themes =
    [
        ("undead", ["UNDEAD", "SKELETON", "LICH", "GHOUL", "REVENANT"], new Regex("UNDEAD|SKELETON", RegexOptions.CultureInvariant)),
        ("keeper", ["KEEPER"], new Regex("(^|_)KEEPER(_|$)|SOULKEEPER", RegexOptions.CultureInvariant)),
        ("morgana", ["MORGANA"], new Regex("MORGANA", RegexOptions.CultureInvariant)),
        ("heretic", ["HERETIC"], new Regex("HERETIC", RegexOptions.CultureInvariant)),
        ("demon", ["DEMON", "HELL"], new Regex("DEMON", RegexOptions.CultureInvariant)),
        ("mists", ["MIST", "FEY"], new Regex("MIST", RegexOptions.CultureInvariant)),
        ("corrupted", ["CORRUPTED"], new Regex("CORRUPT", RegexOptions.CultureInvariant)),
    ];

    /// <summary>The theme of a source name (null = none).</summary>
    public static string? ThemeOf(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;
        var upper = source.ToUpperInvariant();
        foreach (var (theme, words, _) in Themes)
            if (words.Any(upper.Contains)) return theme;
        return null;
    }

    /// <summary>The pool's skins of <paramref name="theme"/> (empty for none or an unknown one).</summary>
    public IEnumerable<Unlock> ThemePool(string? theme) =>
        Themes.FirstOrDefault(t => t.Theme == theme) is { Skins: { } re } ? Pool.Where(u => re.IsMatch(u.Name)) : [];

    /// <summary>One chance of <paramref name="chance"/> × Vanity:ChanceFactor for <paramref name="p"/>; a hit drops (<see cref="Drop"/>).</summary>
    public Unlock? Roll(Player p, double chance, string source, string? themeSource)
    {
        if (!Options.Enabled || chance <= 0 || p.Bot is not null) return null;
        if (Random.NextDouble() >= chance * Options.ChanceFactor) return null;
        return Drop(p, source, ThemeOf(themeSource));
    }

    /// <summary>
    /// A drop for <paramref name="p"/>: a skin the account lacks, of <paramref name="theme"/> with Vanity:ThemeChance (when it lacks one there),
    /// else of the whole pool; when it owns them all, Vanity:DuplicateSilver instead. Null when nothing was unlocked.
    /// </summary>
    public Unlock? Drop(Player p, string source, string? theme)
    {
        var owned = Owned(p.Character);
        var themed = ThemePool(theme).Where(u => !owned.Contains(u.Id)).ToList();
        var any = Pool.Where(u => !owned.Contains(u.Id)).ToList();
        var from = themed.Count > 0 && Random.NextDouble() < Options.ThemeChance ? themed : any;
        if (from.Count == 0)
        {
            var silver = (long)Math.Round(Options.DuplicateSilver * 10000);
            if (silver > 0)
            {
                Wallet.Credit(p, silver, "vanity duplicate");
                Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture, $"A cosmetic dropped ({source}), but you own them all: {silver / 10000.0:0} silver instead."));
            }
            return null;
        }
        var u = from[Random.Next(from.Count)];
        return Grant(p, u, source) ? u : null;
    }

    /// <summary>
    /// <see cref="Combat.Kill"/>: a world boss for each of its looters (Vanity:WorldBossChance), any other boss, miniboss or champion for the
    /// killer (BossChance, MinibossChance, EliteChance), themed by the mob's name.
    /// </summary>
    public void Killed(Player killer, Mob mob, IReadOnlyList<Player>? bossLooters)
    {
        if (!Options.Enabled) return;
        var name = mob.Template.Name;
        if (mob.Boss is not null)
        {
            foreach (var p in bossLooters ?? [killer]) Roll(p, Options.WorldBossChance, $"world boss {name}", name);
            return;
        }
        var chance = mob.Template.Category switch
        {
            "boss" => Options.BossChance,
            "miniboss" => Options.MinibossChance,
            "champion" => Options.EliteChance,
            _ => 0,
        };
        Roll(killer, chance, $"{mob.Template.Category} {name}", name);
    }

    /// <summary>RandomDungeons.OpenChest: a chest's first roll for <paramref name="p"/>: a boss chest × (1 + rarity) with BossChestChance, any other with ChestChance.</summary>
    public void ChestOpened(Player p, LootChest chest)
    {
        var name = chest.Template.Name;
        var boss = name.Contains("BOSS", StringComparison.OrdinalIgnoreCase);
        var chance = (boss ? Options.BossChestChance : Options.ChestChance) * (1 + Math.Max(0, chest.State.Rarity));
        Roll(p, chance, $"chest {name}", name);
    }

    /// <summary>Hunts.Killed: a hunt's final target slain, for every member online.</summary>
    public void HuntWon(IEnumerable<Player> members, string target)
    {
        foreach (var p in members) Roll(p, Options.HuntChance, $"hunt {target}", target);
    }

    public void ArenaWon(Player p, string mode) => Roll(p, Options.ArenaWinChance, $"arena {mode} victory", null);

    public void TournamentWon(Player p) => Roll(p, Options.TournamentChance, "duel tournament victory", null);

    public void SeasonRewards(Player p, int count)
    {
        for (var i = 0; i < count; i++) Roll(p, Options.SeasonRewardChance, "Adventurer's Challenge", null);
    }

    public void GuildGoal(IEnumerable<Player> members)
    {
        foreach (var p in members) Roll(p, Options.GuildGoalChance, "weekly guild goal", null);
    }

    /// <summary>Destiny.Award: each Vanity:FameMilestones mark the total fame (a7m raw) passed from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public void Fame(Player p, long before, long after)
    {
        if (!Options.Enabled || after <= before) return;
        foreach (var mark in Options.FameMilestones)
        {
            var raw = mark * 10000;
            if (before < raw && after >= raw)
                Roll(p, Options.FameMilestoneChance, string.Create(CultureInfo.InvariantCulture, $"{mark:#,0} total fame"), null);
        }
    }

    // ---- the wardrobe and the mount skins ----

    /// <summary>The seven wardrobe skin numbers of <paramref name="c"/> (-1 = none): Join key 108, NewCharacter key 55.</summary>
    public static int[] Wardrobe(Character c) => c.Vanity?.Wardrobe is { Length: 7 } w ? [.. w] : [-1, -1, -1, -1, -1, -1, -1];

    /// <summary>
    /// SetWardrobe (op 387): the seven slots as asked, each skin kept only when it is a wardrobe skin of that slot the account owns (else -1);
    /// saved, and UpdateWardrobe (416 {0 id, 1 the array}) to the player and its observers. Returns what was kept.
    /// </summary>
    public int[] SetWardrobe(Player p, int[]? wanted)
    {
        var kept = Enumerable.Repeat(-1, 7).ToArray();
        HashSet<short>? owned = null;
        for (var i = 0; i < Math.Min(7, wanted?.Length ?? 0); i++)
        {
            if (wanted![i] < 0 || Catalog.WardrobeSkin(wanted[i]) is not { } u) continue;
            owned ??= Owned(p.Character);
            if (u.Slot == i && (u.Free || owned.Contains(u.Id))) kept[i] = wanted[i];
            else Log.Info("vanity", $"'{p.Character.Name}' wardrobe slot {i}: {u} refused ({(u.Slot != i ? "another slot" : "not unlocked")})");
        }
        LogOf(p.Character).Wardrobe = kept;
        world.Saves.MarkChanged(p.Character);
        var e = new UpdateWardrobeEvent { ddn = p.Id, d59 = [.. kept] };
        p.Session.Send(e);
        p.SendToObservers(e);
        return kept;
    }

    /// <summary>GetMountSkins (op 385) key 0: the mount skin numbers chosen, one per category.</summary>
    public static int[] MountSkins(Character c) => [.. (c.Vanity?.MountSkins ?? []).OrderBy(kv => kv.Key).Select(kv => kv.Value)];

    /// <summary>SetMountSkin (op 386 {0 category, 1 skin number or -1}): an owned skin of that category, or none. Null, or why not.</summary>
    public string? SetMountSkin(Player p, int category, int skin)
    {
        if (category < 0 || category >= Catalog.MountCategories.Count) return $"no mount category {category}";
        var log = LogOf(p.Character);
        if (skin < 0) log.MountSkins.Remove(category);
        else if (Catalog.MountSkin(skin) is not { } u) return $"no mount skin {skin}";
        else if (u.Slot != category) return $"{u} is not of category {Catalog.MountCategories[category]}";
        else if (!Has(p.Character, u)) return $"{u} is not unlocked";
        else log.MountSkins[category] = skin;
        world.Saves.MarkChanged(p.Character);
        return null;
    }

    /// <summary>The mount skin number <paramref name="c"/> chose for the category of <paramref name="mount"/>; -1 = none.</summary>
    public int MountSkinFor(Character c, ItemTemplate? mount)
    {
        if (mount is null || c.Vanity is not { MountSkins.Count: > 0 } log) return -1;
        var category = Catalog.MountCategory(mount.Attrs.GetValueOrDefault("mountcategory"));
        return category >= 0 && log.MountSkins.TryGetValue(category, out var skin) ? skin : -1;
    }

    /// <summary>.vanity list: the owned unlocks of <paramref name="c"/>'s account, or the catalogue entries whose name has <paramref name="filter"/>.</summary>
    public IEnumerable<string> Describe(Character c, string? filter)
    {
        var owned = Owned(c);
        if (string.IsNullOrWhiteSpace(filter))
        {
            yield return $"{owned.Count} cosmetic(s) unlocked; {Pool.Count} can drop; wardrobe [{string.Join(", ", Wardrobe(c))}], mount skins [{string.Join(", ", MountSkins(c))}]";
            foreach (var id in owned.Order()) if (Catalog.Find(id) is { } u) yield return $"  {u.Id} {Describe(u)}";
            yield break;
        }
        var hits = Catalog.All.Where(u => u.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        yield return $"{hits.Count} match(es) for '{filter.Trim()}'";
        foreach (var u in hits.Take(25))
            yield return $"  {u.Id} {Describe(u)} [{u.State}, {u.AvailableVia}]{(owned.Contains(u.Id) || u.Free ? " owned" : "")}";
    }
}
