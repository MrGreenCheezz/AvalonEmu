using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// lootchests.xml as the random dungeon chests use it (stageG dungeons SPEC §7 "Chests"): the LootChestSpawnpoint behaviours the
/// tiles' lootchestspawnpoint components name (a weighted list of chests), the LootChest entries (prefab, name tag, the rare
/// states with their loot by tier) and the RareStateMappings (state name → rarity, NewLootChest key 21). Read with the first
/// dungeon floor that has chest spawn points (off the world thread, <see cref="ClusterStore.LootChests"/>); immutable.
/// </summary>
public sealed class LootChestData
{
    /// <summary>LootChestSpawnpoint by name (the spawnbehaviour of the tiles).</summary>
    public IReadOnlyDictionary<string, ChestSpawnBehaviour> Spawnpoints { get; private init; } = new Dictionary<string, ChestSpawnBehaviour>();

    /// <summary>LootChest by uniquename (NewLootChest key 3; the client looks it up and throws on an unknown one).</summary>
    public IReadOnlyDictionary<string, LootChestTemplate> Chests { get; private init; } = new Dictionary<string, LootChestTemplate>();

    public static LootChestData Load(XElement root)
    {
        static double D(XElement e, string a, double fallback) =>
            double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var rarities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in root.Element("RareStateMappings")?.Elements("RareState") ?? [])
            if ((string?)m.Attribute("name") is { } n) rarities[n] = (int)D(m, "rarity", 0);
        var chests = new Dictionary<string, LootChestTemplate>(StringComparer.Ordinal);
        foreach (var c in root.Elements("LootChest"))
        {
            if ((string?)c.Attribute("uniquename") is not { Length: > 0 } name) continue;
            var states = new List<ChestRareState>();
            foreach (var s in c.Element("RareStates")?.Elements("RareState") ?? [])
            {
                var state = (string?)s.Attribute("state") ?? "standard";
                var tiers = new SortedDictionary<int, XElement>();
                foreach (var t in s.Element("Loot")?.Elements("LootByTier") ?? []) tiers.TryAdd((int)D(t, "tier", 0), new XElement(t)); // detached
                // A rare state without a weight counts 1 [assume]: most chests have one state and no weight.
                states.Add(new ChestRareState(state, rarities.GetValueOrDefault(state, 0), D(s, "weight", 1), tiers));
            }
            // <Tiles><Biome name="Swamp" green=… red=… dead=…/> (Roads: none=…): the tile NewLootChest key 4 places (stageH bosses SPEC §3.1).
            var tiles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in c.Element("Tiles")?.Elements("Biome") ?? [])
                if ((string?)b.Attribute("name") is { Length: > 0 } biome)
                    tiles.TryAdd(biome, b.Attributes().Where(a => a.Name.LocalName != "name" && a.Value.Length > 0)
                        .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase));
            chests.TryAdd(name, new LootChestTemplate(name, (string?)c.Attribute("prefab") ?? "", (string?)c.Attribute("namelocatag"), states)
            {
                Tiles = tiles, Faction = (string?)c.Attribute("faction") ?? "", AlertRadius = D(c, "alertradius", 0),
                Spell = (string?)c.Attribute("spell") is { Length: > 0 } spell ? spell : null,
                // every chest of the file has it (325 OnUnlock, 14 OnSpawn); none = shown from the spawn [assume]
                HideRarityUntil = (string?)c.Element("RareStates")?.Attribute("hiderarityuntil") ?? "OnSpawn",
                // public issue #19 (analysis/work/coffers/SPEC.md §1): the contested treasures' countdown, PvP circle, channel and size
                ProtectionSeconds = D(c, "protectiontimeseconds", 0), PvpZoneRadius = D(c, "pvpzoneradius", 0),
                AlwaysChannel = (string?)c.Attribute("alwaysrequireschanneling") == "true", ChestType = (string?)c.Attribute("chesttype") ?? "",
            });
        }
        // The spawnpoint kinds of the file (stageH bosses SPEC §4.2-4.4; LootChestSpawnpointChestUpgrade, the Roads of Avalon chests,
        // a kind of its own the open world leaves out, Q10). bsq.ak 0x180779550, the client's parser: respawn OnDespawn, a missing respawn min or max
        // 36 000 000 s, initial and despawn −1 (unset).
        var spawnpoints = new Dictionary<string, ChestSpawnBehaviour>(StringComparer.Ordinal);
        foreach (var p in root.Elements().Where(e => e.Name.LocalName.StartsWith("LootChestSpawnpoint", StringComparison.Ordinal)))
        {
            if ((string?)p.Attribute("name") is not { Length: > 0 } name) continue;
            var kind = p.Name.LocalName switch
            {
                "LootChestSpawnpointMobUpgrade" => ChestSpawnKind.MobUpgrade,
                "LootChestSpawnpointPersonal" => ChestSpawnKind.Personal,
                "LootChestSpawnpointChestUpgrade" => ChestSpawnKind.ChestUpgrade,
                _ => ChestSpawnKind.Plain,
            };
            // A mob-upgrade spot's ChestUpgradeGroup mulithreshhold is the upgrade level of its chests (bss.aj 0x180779DE0, the same
            // field lootchestupgradelevelindex fills), every one only as an upgrade.
            var entries = p.Elements("LootChest").Select(x => new ChestEntry((string?)x.Attribute("name") ?? "", D(x, "weight", 1),
                    (int)D(x, "lootchestupgradelevelindex", 0), (string?)x.Attribute("onlyAsUpgrade") == "true"))
                .Concat(p.Elements("ChestUpgradeGroup").SelectMany(g => g.Elements("LootChest").Select(x =>
                    new ChestEntry((string?)x.Attribute("name") ?? "", D(x, "weight", 1), (int)D(g, "mulithreshhold", 0), true))))
                .Where(x => x.Chest.Length > 0).ToList();
            spawnpoints.TryAdd(name, new ChestSpawnBehaviour(name, [.. entries.Select(x => (x.Chest, x.Weight))])
            {
                Kind = kind, Entries = entries,
                Respawn = (string?)p.Attribute("respawn") is { } r && Enum.TryParse<ChestRespawn>(r, ignoreCase: true, out var rs) ? rs : ChestRespawn.OnDespawn,
                RespawnSeconds = (D(p, "respawntimesecondsmin", 36_000_000), D(p, "respawntimesecondsmax", 36_000_000)),
                RerollSeconds = p.Attribute("rerolltimesecondsmin") is null ? null : (D(p, "rerolltimesecondsmin", 0), D(p, "rerolltimesecondsmax", 0)),
                InitialSeconds = D(p, "initialspawntimeseconds", -1), DespawnSeconds = D(p, "despawntimeseconds", -1),
                CleanupSeconds = D(p, "cleanupdelayseconds", -1),
                // a ChestUpgrade spot's is mobupgraderadius (60 / 100 m, stageL roads SPEC §1.5), with its spawn and reroll chances
                MobUpgradeFaction = (string?)p.Attribute("mobupgradefaction"), MobUpgradeRadius = D(p, "mobupgradealertradius", D(p, "mobupgraderadius", -1)),
                SpawnChance = D(p, "spawnchance", 1), RerollChance = D(p, "rerollchance", 1),
                MinimapDisplay = p.Element("MinimapDisplay") is not null,
            });
        }
        if (chests.Count == 0) throw new InvalidDataException("lootchests.bin: no <LootChest>");
        return new LootChestData { Spawnpoints = spawnpoints, Chests = chests };
    }
}

/// <summary>
/// A spawnpoint of lootchests.xml: the chests it may put on its tile, by weight (<see cref="Chests"/>: every entry, the upgrade groups'
/// too), and what the open world needs of it (stageH bosses SPEC §4.2-4.5): its kind, the entries with their upgrade levels, the
/// respawn, reroll, despawn and cleanup times (seconds; −1 unset) and the mob-upgrade faction and radius.
/// </summary>
public sealed record ChestSpawnBehaviour(string Name, IReadOnlyList<(string Chest, double Weight)> Chests)
{
    public ChestSpawnKind Kind { get; init; }

    /// <summary>The &lt;LootChest&gt; entries, and those of the ChestUpgradeGroups (level = mulithreshhold, only as an upgrade).</summary>
    public IReadOnlyList<ChestEntry> Entries { get; init; } = [];

    /// <summary>respawn (bsq.RespawnStrategy; OnDespawn when missing, as the client parser).</summary>
    public ChestRespawn Respawn { get; init; } = ChestRespawn.OnDespawn;

    /// <summary>respawntimesecondsmin/max (36 000 000 each when missing, as the client parser).</summary>
    public (double Min, double Max) RespawnSeconds { get; init; } = (36_000_000, 36_000_000);

    /// <summary>rerolltimesecondsmin/max: how often an unopened locked chest is drawn again; null = never.</summary>
    public (double Min, double Max)? RerollSeconds { get; init; }

    public double InitialSeconds { get; init; } = -1;

    /// <summary>despawntimeseconds: how long a chest stays after its first opening; −1 = until it is emptied.</summary>
    public double DespawnSeconds { get; init; } = -1;

    /// <summary>cleanupdelayseconds of a personal spawnpoint: when a player's opened chest and quest are reset.</summary>
    public double CleanupSeconds { get; init; } = -1;

    /// <summary>mobupgradefaction of a mob-upgrade spot.</summary>
    public string? MobUpgradeFaction { get; init; }

    /// <summary>mobupgradealertradius of a mob-upgrade spot (−1 unset, as the client parser); a ChestUpgrade spot's mobupgraderadius.</summary>
    public double MobUpgradeRadius { get; init; } = -1;

    /// <summary>spawnchance and rerollchance of a ChestUpgrade spot (0.648, 0.54 on the roads; 1 when missing): a chest at the (re)spawn, a redraw at a reroll.</summary>
    public double SpawnChance { get; init; } = 1;

    public double RerollChance { get; init; } = 1;

    /// <summary>Whether it has a &lt;MinimapDisplay&gt; (NewLootChest key 19 counts only then).</summary>
    public bool MinimapDisplay { get; init; }

    /// <summary>A POI spot (stageH bosses SPEC §4.2): a plain spawnpoint whose entries carry lootchestupgradelevelindex (not a ChestUpgrade one).</summary>
    public bool IsPoi => Kind == ChestSpawnKind.Plain && Entries.Any(e => e.Level > 0);

    /// <summary>
    /// What a mob-upgrade spot spawns for a mob of upgrade level <paramref name="mobLevel"/> (stageH bosses SPEC §4.3): the entries of
    /// the group with the largest mulithreshhold ≤ it (4 → 3, 6 → 5, 8 → 7); none below the lowest.
    /// </summary>
    public IReadOnlyList<ChestEntry> UpgradeFor(int mobLevel)
    {
        var levels = Entries.Where(e => e.Level <= mobLevel).Select(e => e.Level).ToList();
        if (levels.Count == 0) return [];
        var level = levels.Max();
        return [.. Entries.Where(e => e.Level == level)];
    }
}

/// <summary>The spawnpoint kinds of lootchests.xml (by element name).</summary>
public enum ChestSpawnKind
{
    /// <summary>LootChestSpawnpoint (dungeon chests, POI spots, faction chests).</summary>
    Plain,

    /// <summary>LootChestSpawnpointMobUpgrade: a reward chest when a boss-point mob dies near it.</summary>
    MobUpgrade,

    /// <summary>LootChestSpawnpointPersonal: a camp chest with a state per player.</summary>
    Personal,

    /// <summary>
    /// LootChestSpawnpointChestUpgrade (the Roads of Avalon chests, lootchestupgradelevelindex 4/6/8): on a road the chest's level picks its
    /// camp guards' (Systems/WorldChests, stageL roads SPEC §5.5); a dungeon tile naming one draws from it as from a plain one (<see cref="ChestSpawnBehaviour.Chests"/>).
    /// </summary>
    ChestUpgrade,
}

/// <summary>bsq.RespawnStrategy, the respawn attribute.</summary>
public enum ChestRespawn
{
    Never,
    OnDespawn,
    OnOpen,
    OnSpawn,
}

/// <summary>One chest of a spawnpoint: its weight, its upgrade level (lootchestupgradelevelindex, or its group's mulithreshhold; 0 = none) and onlyAsUpgrade.</summary>
public sealed record ChestEntry(string Chest, double Weight, int Level, bool OnlyAsUpgrade);

/// <summary>A LootChest: its prefab (the client's), name tag, rare states, biome tiles and when its rarity shows.</summary>
public sealed record LootChestTemplate(string Name, string Prefab, string? NameLocaTag, IReadOnlyList<ChestRareState> States)
{
    /// <summary>&lt;Tiles&gt;: by biome (Swamp, Forest, …, Mists, Roads) the tile of each sub-biome (green, red, dead; none on the roads).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tiles { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>faction: the mobs that lock it (LockedByProtectingMobs within <see cref="AlertRadius"/>, stageH bosses SPEC §4.2).</summary>
    public string Faction { get; init; } = "";

    /// <summary>alertradius (15 on the POI chests, 0 on the camp chests).</summary>
    public double AlertRadius { get; init; }

    /// <summary>
    /// spell: the opening spell the client casts in slot 25 (cz5.u reads it from the chest, clj+0xC0; OPEN_LOOTCHEST, _SOLO,
    /// OPEN_TREASURECHEST; stageM chest-slot25 NOTES); null without the attribute.
    /// </summary>
    public string? Spell { get; init; }

    /// <summary>RareStates hiderarityuntil (bdy.RevealRarityEvent Never, OnSpawn, OnUnlock, OnOpen): until when NewLootChest key 21 is −1.</summary>
    public string HideRarityUntil { get; init; } = "OnSpawn";

    /// <summary>
    /// protectiontimeseconds (public issue #19, coffers SPEC §1): how long a treasure stands LockedByTimer (ChestState 1) after its spawn; the client
    /// counts NewLootChest key 7 down by it (clj.af 0x180B47580: template +0x108). 0 = none (every non-treasure chest).
    /// </summary>
    public double ProtectionSeconds { get; init; }

    /// <summary>pvpzoneradius: the PvP circle around a treasure (30 m, the elite chests 40 m; the client's barrier, LootChestObjectView.CreateBarrier); 0 = none.</summary>
    public double PvpZoneRadius { get; init; }

    /// <summary>alwaysrequireschanneling: the opening is always a channelled cast (the client's; the server's slot-25 cast path, FakeSlotCasts).</summary>
    public bool AlwaysChannel { get; init; }

    /// <summary>chesttype (treasurecoffer, treasurechestsmall, treasurechestmedium, treasurechestlarge, factionchest…); "" without.</summary>
    public string ChestType { get; init; } = "";

    /// <summary>
    /// NewLootChest key 4, the tile the client places (clj.arn 0x180B496D0: "No tileid set loot chest" and nothing placed without one;
    /// stageH bosses SPEC §3.1): the &lt;Tiles&gt; entry of <paramref name="biome"/> for the sub-biome of the template folder
    /// <paramref name="folder"/> (GREEN → green, RED → red, DEAD → dead, NONE → none), else the prefab; "" when it has neither.
    /// </summary>
    public string TileFor(string? biome, string folder) =>
        biome is not null && Tiles.GetValueOrDefault(biome)?.GetValueOrDefault(folder.ToLowerInvariant()) is { Length: > 0 } tile ? tile : Prefab;

    /// <summary>Whether key 21 is −1 (the rarity hidden, bsl.ax: the locked name tag) for a viewer whose ChestState is <paramref name="state"/>.</summary>
    public bool HidesRarity(byte state) => HideRarityUntil switch
    {
        "Never" => true,
        "OnUnlock" => state < 6,
        "OnOpen" => state < 7,
        _ => false, // OnSpawn
    };
}

/// <summary>
/// One RareState of a chest: its name, the rarity of the RareStateMappings (standard 0, uncommon 1, rare 2, legendary 3; NewLootChest
/// key 21), its weight, and its &lt;LootByTier&gt; elements by tier (their LootListReference children roll as a mob's &lt;Loot&gt;).
/// </summary>
public sealed record ChestRareState(string State, int Rarity, double Weight, IReadOnlyDictionary<int, XElement> LootByTier)
{
    /// <summary>The loot of tier <paramref name="tier"/>: its own, else the highest tier below it, else the lowest there is; null without any.</summary>
    public XElement? LootFor(int tier) =>
        LootByTier.TryGetValue(tier, out var own) ? own
        : LootByTier.Where(t => t.Key < tier).OrderByDescending(t => t.Key).Select(t => t.Value).FirstOrDefault() ?? LootByTier.Values.FirstOrDefault();
}

/// <summary>
/// localquests.xml (stageH bosses SPEC §3.7, §4.4): the local quests in file order, whose index LocalQuestStarted key 1 carries
/// (8 = LOCAL_QUEST_SMALL_UNDEAD). Read with the first cluster that has a localquestarea tile; immutable.
/// </summary>
public sealed class LocalQuestData
{
    public IReadOnlyList<LocalQuest> Quests { get; private init; } = [];

    /// <summary>The quest named <paramref name="name"/> (a localquestarea's questid); null when the file has none.</summary>
    public LocalQuest? Find(string name) => Quests.FirstOrDefault(q => q.Name == name);

    public static LocalQuestData Load(XElement root)
    {
        var quests = new List<LocalQuest>();
        foreach (var q in root.Elements("localquest"))
        {
            var fame = q.Element("missions")?.Element("killmobfame");
            quests.Add(new LocalQuest(quests.Count, (string?)q.Attribute("uniquename") ?? "", (string?)q.Attribute("questtype") == "perplayer",
                double.TryParse((string?)fame?.Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0,
                (string?)fame?.Attribute("faction") ?? "", q.Element("rewards")?.Element("unlockpersonallootchest") is not null)
            {
                PartyKills = (string?)fame?.Attribute("countpartykillsinsamelocalquestarea") == "true",
            });
        }
        if (quests.Count == 0) throw new InvalidDataException("localquests.bin: no <localquest>");
        return new LocalQuestData { Quests = quests };
    }
}

/// <summary>
/// One local quest: its index in the file, name, whether each player runs it alone (questtype perplayer; the open faction-camp
/// quests are not), its killmobfame mission (the raw fame of mobs of <see cref="Faction"/>; 0 = none) and whether it unlocks the
/// camp's personal chest.
/// </summary>
public sealed record LocalQuest(int Index, string Name, bool PerPlayer, double Fame, string Faction, bool UnlocksChest)
{
    /// <summary>countpartykillsinsamelocalquestarea: a party member's kill counts for the members in the same area.</summary>
    public bool PartyKills { get; init; }
}
