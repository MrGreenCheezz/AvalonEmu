using System.Globalization;
using System.Text.Json;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Content;

/// <summary>
/// What a wave of a world event puts down (content/events.json Waves[].Kind): the mobs of the cluster's faction of that mobs.xml
/// category. A kind the cluster's spawn blocks do not have falls back to the next lower one (boss → miniboss → champion → standard).
/// </summary>
public enum WaveKind
{
    /// <summary>The standard and trash mobs (and those of no category) of the faction's spawn blocks.</summary>
    Standard,

    Champion,

    Miniboss,

    Boss,
}

/// <summary>One wave of an event: how many mobs (uniform min..max), of what kind, and the factors on their health and damage.</summary>
public sealed record EventWave(WaveKind Kind, (int Min, int Max) Count, double HealthFactor = 1, double DamageFactor = 1);

/// <summary>
/// A kind of world event (content/events.json Events[]): its waves in order, how long it may run, how far from a player it comes, and
/// the reward of the players who did at least <see cref="RewardMinShare"/> of its damage: a personal chest of
/// <see cref="Chest"/> (a lootchests.xml LootChestSpawnpoint: its weighted chests, rolled at the cluster's tier) with ×
/// <see cref="RewardFactor"/> on its loot, and <see cref="RewardFame"/> fame each.
/// </summary>
public sealed record EventType
{
    public required string Name { get; init; }

    /// <summary>The name in the chat lines ("Undead invasion T6"); {faction} is the faction of its mobs, {tier} the difficulty (the cluster's tier without one).</summary>
    public string Title { get; init; } = "{faction} invasion T{tier}";

    /// <summary>The weight of a scheduled pick among the kinds (0: only by .event start).</summary>
    public double Weight { get; init; } = 1;

    public IReadOnlyList<EventWave> Waves { get; init; } = [];

    /// <summary>Minutes from the start until the event fails when its last wave is not beaten (its mobs go).</summary>
    public double DurationMinutes { get; init; } = 10;

    /// <summary>Seconds between a wave's last death and the next wave.</summary>
    public double WaveDelaySeconds { get; init; } = 10;

    /// <summary>How far from the chosen player its point is (m): a mob spawn point of the cluster in this ring, else the nearest one beyond Min.</summary>
    public (double Min, double Max) Distance { get; init; } = (40, 120);

    /// <summary>The ring around the point the wave mobs stand on (m).</summary>
    public double SpreadMeters { get; init; } = 6;

    /// <summary>The share of the event's damage a player needs for a reward (0: every player that hit one of its mobs).</summary>
    public double RewardMinShare { get; init; } = 0.05;

    /// <summary>Factor on the reward chest's loot (× Rates:Loot, as the open-world chests).</summary>
    public double RewardFactor { get; init; } = 2;

    /// <summary>Fame for each rewarded player (in fame points; × Rates:Fame, to the destiny board as a kill of the cluster's tier).</summary>
    public double RewardFame { get; init; } = 2000;

    /// <summary>The LootChestSpawnpoint of the reward chest (DEFAULT_TREASURE_VETERAN: the open world's veteran treasure chests, T4-T8, every biome).</summary>
    public string Chest { get; init; } = "DEFAULT_TREASURE_VETERAN";

    /// <summary>Minutes the reward chest stands.</summary>
    public double ChestMinutes { get; init; } = 10;
}

/// <summary>
/// Stage I (invasions SPEC §3.3): one difficulty T1-T10 of a player's invasion (content/events.json Tiers[]): its mobs (tier, faction,
/// level buff, factors, extra waves) and its rewards (the chest, its loot tier, item tier and enchantment, silver and fame).
/// </summary>
public sealed record EventTier
{
    /// <summary>The difficulty, 1-10.</summary>
    public required int Tier { get; init; }

    /// <summary>The tier of its mobs, 1-8 (mobs.xml has none above T8: T9 and T10 are T8 mobs with a level buff).</summary>
    public required int MobTier { get; init; }

    /// <summary>The mob faction (mobs.xml faction, HERETIC, UNDEAD, …); null = the invasion faction of the spawn block nearest the point.</summary>
    public string? Faction { get; init; }

    /// <summary>The level buff each mob carries (spells.xml, as a dungeon floor's MOB_RANDOM_DUNGEON_BUFF_0n); null = none.</summary>
    public string? LevelBuff { get; init; }

    public double HealthFactor { get; init; } = 1;

    public double DamageFactor { get; init; } = 1;

    /// <summary>A factor on the kill fame of its mobs.</summary>
    public double FameFactor { get; init; } = 1;

    /// <summary>A factor on each wave's mob count.</summary>
    public double CountFactor { get; init; } = 1;

    /// <summary>Waves after the kind's own.</summary>
    public IReadOnlyList<EventWave> ExtraWaves { get; init; } = [];

    /// <summary>Minutes more time per extra wave.</summary>
    public double ExtraMinutes { get; init; } = 3;

    /// <summary>The LootChestSpawnpoints of the reward chest, the first that gives one (empty: the kind's Chest).</summary>
    public IReadOnlyList<string> Chest { get; init; } = [];

    /// <summary>The tier the chest's loot is rolled at (null: the cluster's).</summary>
    public int? LootTier { get; init; }

    /// <summary>The tier the rolled items are moved to where items.xml has them (T4_X → T2_X; null: kept).</summary>
    public int? ItemTier { get; init; }

    /// <summary>Enchantment levels added to the rolled items where items.xml has the variant (0-4).</summary>
    public int Enchant { get; init; }

    /// <summary>A factor on the chest's loot (× the kind's RewardFactor).</summary>
    public double LootFactor { get; init; } = 1;

    /// <summary>Whole silver for each rewarded player (× Rates:Silver).</summary>
    public long Silver { get; init; }

    /// <summary>Fame for each rewarded player (× Rates:Fame), in place of the kind's RewardFame.</summary>
    public double Fame { get; init; }

    /// <summary>Minutes the chest stands; null = the kind's ChestMinutes.</summary>
    public double? ChestMinutes { get; init; }
}

/// <summary>Stage I: an item that starts an invasion when used from the bag (a ticket): its uniquename, the event kind, its tier (0 = the player's chosen one).</summary>
public sealed record InvasionItem(string Item, string Kind, int Tier = 0);

/// <summary>Stage I (invasions SPEC §3.2): the rules of the invasions players start themselves (.invasion, the tickets).</summary>
public sealed record PlayerInvasionSettings
{
    /// <summary>Whether players may start invasions at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether .invasion &lt;tier&gt; starts one (false: the tickets only).</summary>
    public bool Command { get; init; } = true;

    /// <summary>Whole silver a command start costs, by tier (index 0 = T1; 0 = free).</summary>
    public IReadOnlyList<long> CommandSilver { get; init; } = new long[10];

    /// <summary>The tickets (default: the two corrupted dungeon maps, dead items on this server otherwise).</summary>
    public IReadOnlyList<InvasionItem> Items { get; init; } =
        [new("T5_CORRUPTED_NONLETHAL_MAP", "invasion"), new("T6_CORRUPTED_LETHAL_MAP", "champion")];

    /// <summary>Tickets a start uses, by tier (a fixed-tier item: always 1).</summary>
    public IReadOnlyList<int> ItemCost { get; init; } = [1, 1, 1, 1, 1, 1, 1, 1, 2, 3];

    /// <summary>Minutes after a command start before the character may start another.</summary>
    public double CooldownMinutes { get; init; } = 10;

    /// <summary>Minutes after a ticket start (the ticket is the price).</summary>
    public double ItemCooldownMinutes { get; init; } = 2;

    /// <summary>Starts per character per UTC day; 0 = no cap.</summary>
    public int DailyLimit { get; init; }

    /// <summary>Player-started invasions at once on the server.</summary>
    public int MaxRunning { get; init; } = 5;

    public int MinTier { get; init; } = 1;

    public int MaxTier { get; init; } = 10;

    /// <summary>The point from the starter (m): a mob spawn point in this ring, else the nearest beyond Min.</summary>
    public (double Min, double Max) Distance { get; init; } = (15, 45);

    /// <summary>The world.xml cluster types (prefixes) a player may start one in: the open world, no city, island or dungeon.</summary>
    public IReadOnlyList<string> ClusterTypes { get; init; } = ["SAFEAREA", "STARTAREA", "OPENPVP_", "TUNNEL_", "PASSAGE_"];

    /// <summary>Cluster types refused even when a prefix matches.</summary>
    public IReadOnlyList<string> ExcludeTypes { get; init; } = ["SAFEAREA_TUTORIAL"];

    /// <summary>The mob factions an invasion takes (and their _CAMP), from the spawn block nearest the point; HERETIC when the map has none.</summary>
    public IReadOnlyList<string> Factions { get; init; } = ["HERETIC", "UNDEAD", "KEEPER", "MORGANA", "AVALON"];

    /// <summary>Most players rewarded (the largest damage first).</summary>
    public int MaxRewarded { get; init; } = 20;

    /// <summary>A party member of a rewarded player within PartyShareMeters of the point at the win is rewarded too.</summary>
    public bool PartyShare { get; init; } = true;

    public double PartyShareMeters { get; init; } = 60;

    /// <summary>A wave's mob count × min(GroupCountMax, 1 + this × (the players within 60 m of the point − 1)).</summary>
    public double GroupCountPerPlayer { get; init; } = 0.25;

    public double GroupCountMax { get; init; } = 2;

    /// <summary>Whether <paramref name="type"/> (world.xml cluster type) may have a player's invasion.</summary>
    public bool Allows(string type) =>
        ClusterTypes.Any(t => type.StartsWith(t, StringComparison.OrdinalIgnoreCase)) && !ExcludeTypes.Any(t => type.Equals(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="faction"/> (mobs.xml) is an invasion faction (UNDEAD_CAMP counts as UNDEAD).</summary>
    public bool IsFaction(string faction) =>
        faction.Length > 0 && Factions.Contains(faction.EndsWith("_CAMP", StringComparison.Ordinal) ? faction[..^5] : faction, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Stage I (invasions SPEC §3.4): NPC sell orders of the tickets on a city market (as the journal merchant's).</summary>
public sealed record EventMerchant
{
    public IReadOnlyList<string> Markets { get; init; } = ["0000"];

    /// <summary>Whole silver per ticket by uniquename; empty = no orders.</summary>
    public IReadOnlyDictionary<string, long> Prices { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["T5_CORRUPTED_NONLETHAL_MAP"] = 15000, ["T6_CORRUPTED_LETHAL_MAP"] = 30000 };

    /// <summary>The amount an order shows (it never runs out).</summary>
    public int Stock { get; init; } = 999;
}

/// <summary>Stage I (invasions SPEC §3.4): the chances of a ticket from open-world boss and miniboss kills and from a won invasion.</summary>
public sealed record EventDrops
{
    public double Boss { get; init; } = 0.20;

    public double Miniboss { get; init; } = 0.05;

    /// <summary>For each rewarded player of a won player invasion: a ticket (the kind's own) in its chest.</summary>
    public double Won { get; init; } = 0.25;

    /// <summary>The ticket a boss or miniboss drops.</summary>
    public string Item { get; init; } = "T5_CORRUPTED_NONLETHAL_MAP";
}

/// <summary>
/// The world events (PVE-DESIGN.md §2.H, §3 step 7; <see cref="Systems.WorldEvents"/>): content/events.json, as content/bosses.json.
/// A key the file leaves out keeps the built-in value (<see cref="Default"/>, which has the shipped file's two kinds); a missing file or
/// one that does not parse gives the built-in values with a log line. Immutable.
/// </summary>
public sealed record EventSettings
{
    /// <summary>The built-in tiers (invasions SPEC §3.3; declared before <see cref="Default"/>, which reads them): T1-T3 solo chests with T2/T3 items, T4-T6 veteran, T7-T10 elite (else veteran); T9/T10 T8 mobs with a level buff and extra waves.</summary>
    public static IReadOnlyList<EventTier> DefaultTiers { get; } =
    [
        new() { Tier = 1, MobTier = 1, Chest = ["DEFAULT_TREASURE_SOLO"], LootTier = 4, ItemTier = 2, LootFactor = 0.5, Silver = 1000, Fame = 250 },
        new() { Tier = 2, MobTier = 2, Chest = ["DEFAULT_TREASURE_SOLO"], LootTier = 4, ItemTier = 2, LootFactor = 0.6, Silver = 2000, Fame = 500 },
        new() { Tier = 3, MobTier = 3, Chest = ["DEFAULT_TREASURE_SOLO"], LootTier = 4, ItemTier = 3, LootFactor = 0.8, Silver = 4000, Fame = 1000 },
        new() { Tier = 4, MobTier = 4, Chest = ["DEFAULT_TREASURE_VETERAN"], LootTier = 4, LootFactor = 1.0, Silver = 7500, Fame = 2000 },
        new() { Tier = 5, MobTier = 5, Chest = ["DEFAULT_TREASURE_VETERAN"], LootTier = 5, LootFactor = 1.1, Silver = 12000, Fame = 3500 },
        new() { Tier = 6, MobTier = 6, Chest = ["DEFAULT_TREASURE_VETERAN"], LootTier = 6, LootFactor = 1.2, Silver = 20000, Fame = 6000 },
        new() { Tier = 7, MobTier = 7, Chest = ["DEFAULT_TREASURE_ELITE", "DEFAULT_TREASURE_VETERAN"], LootTier = 7, LootFactor = 1.3, Silver = 32000, Fame = 10000 },
        new() { Tier = 8, MobTier = 8, Chest = ["DEFAULT_TREASURE_ELITE", "DEFAULT_TREASURE_VETERAN"], LootTier = 8, LootFactor = 1.4, Silver = 50000, Fame = 16000 },
        new()
        {
            Tier = 9, MobTier = 8, LevelBuff = "MOB_RANDOM_DUNGEON_BUFF_02", FameFactor = 1.5, ExtraWaves = [new(WaveKind.Champion, (1, 2), 1.5)],
            Chest = ["DEFAULT_TREASURE_ELITE", "DEFAULT_TREASURE_VETERAN"], LootTier = 8, Enchant = 1, LootFactor = 1.6, Silver = 80000, Fame = 25000,
        },
        new()
        {
            Tier = 10, MobTier = 8, LevelBuff = "MOB_RANDOM_DUNGEON_BUFF_04", FameFactor = 2, ExtraWaves = [new(WaveKind.Champion, (1, 2), 1.5), new(WaveKind.Miniboss, (1, 1), 2)],
            Chest = ["DEFAULT_TREASURE_ELITE", "DEFAULT_TREASURE_VETERAN"], LootTier = 8, Enchant = 2, LootFactor = 2, Silver = 120000, Fame = 40000,
        },
    ];

    public static EventSettings Default { get; } = new();

    /// <summary>Whether events run at all (off: neither scheduled nor .event start).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether the server starts events by itself (off: only .event start and the players' .invasion and tickets; stage I default off).</summary>
    public bool Scheduled { get; init; }

    /// <summary>Minutes from the server start or the end of an event until the next scheduled one (uniform min..max).</summary>
    public (double Min, double Max) IntervalMinutes { get; init; } = (30, 60);

    /// <summary>The players a cluster needs on its map for a scheduled event there.</summary>
    public int MinPlayers { get; init; } = 1;

    /// <summary>At most this many events at once (scheduled; .event start is not counted against it).</summary>
    public int MaxRunning { get; init; } = 1;

    /// <summary>Only these clusters get scheduled events (empty: every open-world cluster with mob spawn blocks).</summary>
    public IReadOnlyList<string> Clusters { get; init; } = [];

    /// <summary>Who reads the lines: the players in the event's cluster (false) or every player in game.</summary>
    public bool AnnounceToAll { get; init; }

    /// <summary>
    /// The start line, per player: {event} the title, {cluster}, {x} {z} the point, {distance} m and {direction} (N, NE, …) from that
    /// player, {waves} the wave count, {minutes} the time it has.
    /// </summary>
    public string AnnounceStart { get; init; } = "{event} in {cluster}: {distance} m {direction} of you at ({x}, {z}); {waves} waves, {minutes} min";

    /// <summary>A wave's line: {event}, {wave}, {waves}, {count} its mobs, {mob} the name of its first mob.</summary>
    public string AnnounceWave { get; init; } = "{event}: wave {wave} of {waves}, {count} x {mob}";

    /// <summary>The win line: {event}, {x}, {z}, {count} the rewarded players.</summary>
    public string AnnounceWon { get; init; } = "{event} beaten! Reward chests at ({x}, {z}) for {count} player(s)";

    /// <summary>The line of an event whose time ran out (its mobs go): {event}.</summary>
    public string AnnounceFailed { get; init; } = "{event}: the invaders have retreated";

    /// <summary>The line of an event a GM stopped: {event}.</summary>
    public string AnnounceStopped { get; init; } = "{event} was called off";

    /// <summary>Stage I: the line before the start line of a player's invasion: {player} the starter, {event}, {tier}.</summary>
    public string AnnounceCalled { get; init; } = "{player} called {event}";

    /// <summary>Stage I: the invasions players start (.invasion, the tickets).</summary>
    public PlayerInvasionSettings Player { get; init; } = new();

    /// <summary>Stage I: the difficulties T1-T10 of a player's invasion (or a GM's .event start with a tier), in order.</summary>
    public IReadOnlyList<EventTier> Tiers { get; init; } = DefaultTiers;

    /// <summary>Stage I: the NPC sell orders of the tickets.</summary>
    public EventMerchant Merchant { get; init; } = new();

    /// <summary>Stage I: the ticket drops.</summary>
    public EventDrops Drops { get; init; } = new();

    /// <summary>The row of difficulty <paramref name="tier"/> (1-10).</summary>
    public EventTier Tier(int tier) => Tiers[Math.Clamp(tier, 1, Tiers.Count) - 1];

    /// <summary>The ticket <paramref name="item"/> is (its base name, ignoring case); null when it is none.</summary>
    public InvasionItem? ItemFor(Data.ItemTemplate item) =>
        Player.Items.FirstOrDefault(i => i.Item.Equals(item.BaseName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The two corrupted dungeon maps, the default tickets (invasion and champion).</summary>
    public static readonly IReadOnlyList<string> CorruptedMaps = ["T5_CORRUPTED_NONLETHAL_MAP", "T6_CORRUPTED_LETHAL_MAP"];

    /// <summary>
    /// StageL corrupted (SPEC §4.8, Corrupted:MapsOpenDungeons): the tickets move off the corrupted maps, which open their corrupted portal
    /// again, to the dead Knight's Vows: a ticket of kind invasion on a corrupted map becomes T4..T8_GVGSEASONREWARD_FAMEBUFF_SHORT with its
    /// Tier 4..8, one of kind champion T4..T8_GVGSEASONREWARD_FAMEBUFF (T1-T3 and T9-T10 stay on .invasion); the merchant's orders of the maps
    /// go (the vows are not tradable: no market orders), a dropped map becomes T5_GVGSEASONREWARD_FAMEBUFF_SHORT. Tickets that are no
    /// corrupted map stay as they are; without corrupted map tickets the settings are returned unchanged.
    /// </summary>
    public EventSettings MoveTickets()
    {
        static bool Map(string item) => CorruptedMaps.Contains(item, StringComparer.OrdinalIgnoreCase);
        if (!Player.Items.Any(i => Map(i.Item)) && !Map(Drops.Item) && !Merchant.Prices.Keys.Any(Map)) return this;
        var items = new List<InvasionItem>();
        foreach (var ticket in Player.Items)
        {
            if (!Map(ticket.Item))
            {
                items.Add(ticket);
                continue;
            }
            var family = ticket.Kind.Equals("champion", StringComparison.OrdinalIgnoreCase) ? "GVGSEASONREWARD_FAMEBUFF" : "GVGSEASONREWARD_FAMEBUFF_SHORT";
            for (var tier = 4; tier <= 8; tier++)
                items.Add(new InvasionItem(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"T{tier}_{family}"), ticket.Kind, tier));
        }
        return this with
        {
            Player = Player with { Items = items },
            Merchant = Merchant with { Prices = Merchant.Prices.Where(p => !Map(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase) },
            Drops = Map(Drops.Item) ? Drops with { Item = "T5_GVGSEASONREWARD_FAMEBUFF_SHORT" } : Drops,
        };
    }

    /// <summary>The kinds (built in: invasion, three waves; champion, one strong champion).</summary>
    public IReadOnlyList<EventType> Events { get; init; } =
    [
        new EventType
        {
            Name = "invasion", Title = "{faction} invasion T{tier}",
            Waves = [new(WaveKind.Standard, (6, 10)), new(WaveKind.Standard, (6, 10)), new(WaveKind.Champion, (1, 2), 1.5)],
        },
        new EventType
        {
            Name = "champion", Title = "{faction} champion hunt T{tier}", Weight = 0.5, RewardFame = 1000,
            Waves = [new(WaveKind.Miniboss, (1, 1), 3)],
        },
    ];

    /// <summary>The kind called <paramref name="name"/> (ignoring case); null when there is none.</summary>
    public EventType? Find(string name) => Events.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static EventSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn("content", $"{path} missing: built-in world event settings");
            return Default;
        }
        try
        {
            var s = Parse(File.ReadAllText(path), path);
            Log.Info("content", string.Create(CultureInfo.InvariantCulture,
                $"events: {(s.Enabled ? "on" : "off")}, {(s.Scheduled ? $"every {s.IntervalMinutes.Min}-{s.IntervalMinutes.Max} min" : "not scheduled")}, {string.Join(", ", s.Events.Select(e => $"{e.Name} ({e.Waves.Count} waves)"))}; player invasions {(s.Player.Enabled ? $"T{s.Player.MinTier}-T{s.Player.MaxTier}, {s.Player.Items.Count} ticket(s)" : "off")}"));
            return s;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            Log.Error("content", $"{path}: {e.Message}; built-in world event settings used");
            return Default;
        }
    }

    /// <summary>
    /// The file: Enabled, Scheduled, IntervalMinutes [min, max], MinPlayers, MaxRunning, Clusters [..], AnnounceToAll, AnnounceStart,
    /// AnnounceWave, AnnounceWon, AnnounceFailed, AnnounceStopped, Events [{ Name, Title, Weight, Waves [{ Kind, Count [min, max],
    /// HealthFactor, DamageFactor }], DurationMinutes, WaveDelaySeconds, Distance [min, max], SpreadMeters, RewardMinShare, RewardFactor,
    /// RewardFame, Chest, ChestMinutes }] (a given Events list replaces the built-in one); stage I: AnnounceCalled, Player { … },
    /// Tiers [{ Tier, … }] (each entry changes the keys it gives of its built-in row), Merchant { Markets, Prices, Stock }, Drops { Boss,
    /// Miniboss, Won, Item } (invasions SPEC §3). A ticket of a kind Events has not is skipped with a warning.
    /// </summary>
    public static EventSettings Parse(string text, string path)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not a JSON object");
        var s = Default;
        double Num(JsonElement v, string key, double min, double max) =>
            v.TryGetDouble(out var d) && d >= min && d <= max ? d : throw new InvalidDataException($"{key}: {v} is not {min}..{max}");
        bool Bool(JsonElement v, string key) => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new InvalidDataException($"{key}: not true or false");
        string Str(JsonElement v, string key) => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"{key}: not a string");
        (double, double) Pair(JsonElement v, string key, double min, double max)
        {
            if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() != 2) throw new InvalidDataException($"{key}: not a list of two numbers");
            var (a, b) = (Num(v[0], key, min, max), Num(v[1], key, min, max));
            return (Math.Min(a, b), Math.Max(a, b));
        }
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "Enabled": s = s with { Enabled = Bool(p.Value, p.Name) }; break;
                case "Scheduled": s = s with { Scheduled = Bool(p.Value, p.Name) }; break;
                case "IntervalMinutes": s = s with { IntervalMinutes = Pair(p.Value, p.Name, 0.01, 7 * 24 * 60) }; break;
                case "MinPlayers": s = s with { MinPlayers = (int)Num(p.Value, p.Name, 1, 1000) }; break;
                case "MaxRunning": s = s with { MaxRunning = (int)Num(p.Value, p.Name, 1, 100) }; break;
                case "Clusters":
                    s = s with
                    {
                        Clusters = p.Value.ValueKind == JsonValueKind.Array ? [.. p.Value.EnumerateArray().Select(c => Str(c, p.Name))] : throw new InvalidDataException($"{p.Name}: not a list"),
                    };
                    break;
                case "AnnounceToAll": s = s with { AnnounceToAll = Bool(p.Value, p.Name) }; break;
                case "AnnounceStart": s = s with { AnnounceStart = Str(p.Value, p.Name) }; break;
                case "AnnounceWave": s = s with { AnnounceWave = Str(p.Value, p.Name) }; break;
                case "AnnounceWon": s = s with { AnnounceWon = Str(p.Value, p.Name) }; break;
                case "AnnounceFailed": s = s with { AnnounceFailed = Str(p.Value, p.Name) }; break;
                case "AnnounceStopped": s = s with { AnnounceStopped = Str(p.Value, p.Name) }; break;
                case "AnnounceCalled": s = s with { AnnounceCalled = Str(p.Value, p.Name) }; break;
                case "Player": s = s with { Player = ParsePlayer(p.Value, path, Num, Bool, Str, Pair) }; break;
                case "Tiers": s = s with { Tiers = ParseTiers(p.Value, path, Num, Str, Pair) }; break;
                case "Merchant": s = s with { Merchant = ParseMerchant(p.Value, path, Num, Str) }; break;
                case "Drops": s = s with { Drops = ParseDrops(p.Value, path, Num, Str) }; break;
                case "Events":
                    if (p.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{p.Name}: not a list");
                    var list = new List<EventType>();
                    foreach (var e in p.Value.EnumerateArray()) list.Add(ParseType(e, path, Num, Str, Pair));
                    if (list.Select(e => e.Name.ToLowerInvariant()).Distinct().Count() != list.Count) throw new InvalidDataException($"{p.Name}: a Name used twice");
                    s = s with { Events = list };
                    break;
                default: Log.Warn("content", $"{path}: unknown key {p.Name}, skipped"); break;
            }
        }
        if (s.Player.Items.Where(i => s.Find(i.Kind) is null).ToList() is { Count: > 0 } orphans)
        {
            Log.Warn("content", $"{path}: Player.Items {string.Join(", ", orphans.Select(i => i.Item))} start a kind Events has not, skipped");
            s = s with { Player = s.Player with { Items = [.. s.Player.Items.Except(orphans)] } };
        }
        return s;
    }

    private static List<string> Strings(JsonElement v, string key, Func<JsonElement, string, string> str) =>
        v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(c => str(c, key))] : throw new InvalidDataException($"{key}: not a list");

    /// <summary>A list of exactly 10 numbers (one per tier).</summary>
    private static List<double> Ten(JsonElement v, string key, Func<JsonElement, string, double, double, double> num, double min, double max) =>
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 10 ? [.. v.EnumerateArray().Select(x => num(x, key, min, max))]
            : throw new InvalidDataException($"{key}: not a list of 10 numbers (T1-T10)");

    /// <summary>Player { Enabled, Command, CommandSilver [10], Items [{ Item, Kind, Tier }], ItemCost [10], CooldownMinutes, … } (SPEC §3.2).</summary>
    private static PlayerInvasionSettings ParsePlayer(JsonElement v, string path, Func<JsonElement, string, double, double, double> num,
        Func<JsonElement, string, bool> boolean, Func<JsonElement, string, string> str, Func<JsonElement, string, double, double, (double, double)> pair)
    {
        if (v.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Player: not an object");
        var s = new PlayerInvasionSettings();
        foreach (var q in v.EnumerateObject())
        {
            var key = $"Player.{q.Name}";
            switch (q.Name)
            {
                case "Enabled": s = s with { Enabled = boolean(q.Value, key) }; break;
                case "Command": s = s with { Command = boolean(q.Value, key) }; break;
                case "CommandSilver": s = s with { CommandSilver = [.. Ten(q.Value, key, num, 0, 1e12).Select(d => (long)d)] }; break;
                case "ItemCost": s = s with { ItemCost = [.. Ten(q.Value, key, num, 1, 999).Select(d => (int)d)] }; break;
                case "Items":
                    if (q.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{key}: not a list");
                    var items = new List<InvasionItem>();
                    foreach (var e in q.Value.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{key}: an entry is not an object");
                        var item = new InvasionItem("", "invasion");
                        foreach (var r in e.EnumerateObject())
                            switch (r.Name)
                            {
                                case "Item": item = item with { Item = str(r.Value, $"{key}.Item").ToUpperInvariant() }; break;
                                case "Kind": item = item with { Kind = str(r.Value, $"{key}.Kind") }; break;
                                case "Tier": item = item with { Tier = (int)num(r.Value, $"{key}.Tier", 0, 10) }; break;
                                default: Log.Warn("content", $"{path}: unknown key {key}.{r.Name}, skipped"); break;
                            }
                        if (item.Item.Length == 0) throw new InvalidDataException($"{key}: an entry without Item");
                        items.Add(item);
                    }
                    s = s with { Items = items };
                    break;
                case "CooldownMinutes": s = s with { CooldownMinutes = num(q.Value, key, 0, 7 * 24 * 60) }; break;
                case "ItemCooldownMinutes": s = s with { ItemCooldownMinutes = num(q.Value, key, 0, 7 * 24 * 60) }; break;
                case "DailyLimit": s = s with { DailyLimit = (int)num(q.Value, key, 0, 10000) }; break;
                case "MaxRunning": s = s with { MaxRunning = (int)num(q.Value, key, 1, 1000) }; break;
                case "MinTier": s = s with { MinTier = (int)num(q.Value, key, 1, 10) }; break;
                case "MaxTier": s = s with { MaxTier = (int)num(q.Value, key, 1, 10) }; break;
                case "Distance": s = s with { Distance = pair(q.Value, key, 0, 2000) }; break;
                case "ClusterTypes": s = s with { ClusterTypes = Strings(q.Value, key, str) }; break;
                case "ExcludeTypes": s = s with { ExcludeTypes = Strings(q.Value, key, str) }; break;
                case "Factions": s = s with { Factions = [.. Strings(q.Value, key, str).Select(f => f.ToUpperInvariant())] }; break;
                case "MaxRewarded": s = s with { MaxRewarded = (int)num(q.Value, key, 1, 1000) }; break;
                case "PartyShare": s = s with { PartyShare = boolean(q.Value, key) }; break;
                case "PartyShareMeters": s = s with { PartyShareMeters = num(q.Value, key, 0, 1000) }; break;
                case "GroupCountPerPlayer": s = s with { GroupCountPerPlayer = num(q.Value, key, 0, 10) }; break;
                case "GroupCountMax": s = s with { GroupCountMax = num(q.Value, key, 1, 10) }; break;
                default: Log.Warn("content", $"{path}: unknown key {key}, skipped"); break;
            }
        }
        if (s.MinTier > s.MaxTier) throw new InvalidDataException("Player: MinTier above MaxTier");
        return s;
    }

    /// <summary>Tiers [{ Tier, MobTier, … }]: each entry changes the built-in row of its Tier (the keys it gives; SPEC §3.3).</summary>
    private static List<EventTier> ParseTiers(JsonElement v, string path, Func<JsonElement, string, double, double, double> num,
        Func<JsonElement, string, string> str, Func<JsonElement, string, double, double, (double, double)> pair)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Tiers: not a list");
        var rows = DefaultTiers.ToList();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("Tier", out var t)) throw new InvalidDataException("Tiers: an entry is not an object with a Tier");
            var tier = (int)num(t, "Tiers.Tier", 1, 10);
            var row = rows[tier - 1];
            foreach (var q in e.EnumerateObject())
            {
                var key = $"Tiers[T{tier}].{q.Name}";
                var none = q.Value.ValueKind == JsonValueKind.Null;
                switch (q.Name)
                {
                    case "Tier": break;
                    case "MobTier": row = row with { MobTier = (int)num(q.Value, key, 1, 8) }; break;
                    case "Faction": row = row with { Faction = none ? null : str(q.Value, key).ToUpperInvariant() }; break;
                    case "LevelBuff": row = row with { LevelBuff = none ? null : str(q.Value, key) }; break;
                    case "HealthFactor": row = row with { HealthFactor = num(q.Value, key, 0.01, 100) }; break;
                    case "DamageFactor": row = row with { DamageFactor = num(q.Value, key, 0, 100) }; break;
                    case "FameFactor": row = row with { FameFactor = num(q.Value, key, 0, 100) }; break;
                    case "CountFactor": row = row with { CountFactor = num(q.Value, key, 0.1, 10) }; break;
                    case "ExtraWaves": row = row with { ExtraWaves = ParseWaves(q.Value, key, path, num, str, pair) }; break;
                    case "ExtraMinutes": row = row with { ExtraMinutes = num(q.Value, key, 0, 24 * 60) }; break;
                    case "Chest":
                        row = row with
                        {
                            Chest = q.Value.ValueKind == JsonValueKind.String ? [str(q.Value, key).ToUpperInvariant()] : [.. Strings(q.Value, key, str).Select(c => c.ToUpperInvariant())],
                        };
                        break;
                    case "LootTier": row = row with { LootTier = none ? null : (int)num(q.Value, key, 1, 8) }; break;
                    case "ItemTier": row = row with { ItemTier = none ? null : (int)num(q.Value, key, 1, 8) }; break;
                    case "Enchant": row = row with { Enchant = (int)num(q.Value, key, 0, 4) }; break;
                    case "LootFactor": row = row with { LootFactor = num(q.Value, key, 0, 100) }; break;
                    case "Silver": row = row with { Silver = (long)num(q.Value, key, 0, 1e10) }; break;
                    case "Fame": row = row with { Fame = num(q.Value, key, 0, 1e8) }; break;
                    case "ChestMinutes": row = row with { ChestMinutes = none ? null : num(q.Value, key, 0.01, 24 * 60) }; break;
                    default: Log.Warn("content", $"{path}: unknown key {key}, skipped"); break;
                }
            }
            rows[tier - 1] = row;
        }
        return rows;
    }

    /// <summary>Merchant { Markets [..], Prices { item: silver }, Stock }.</summary>
    private static EventMerchant ParseMerchant(JsonElement v, string path, Func<JsonElement, string, double, double, double> num, Func<JsonElement, string, string> str)
    {
        if (v.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Merchant: not an object");
        var m = new EventMerchant();
        foreach (var q in v.EnumerateObject())
        {
            var key = $"Merchant.{q.Name}";
            switch (q.Name)
            {
                case "Markets": m = m with { Markets = Strings(q.Value, key, str) }; break;
                case "Prices":
                    if (q.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{key}: not an object");
                    m = m with
                    {
                        Prices = q.Value.EnumerateObject().ToDictionary(e => e.Name.ToUpperInvariant(), e => (long)num(e.Value, $"{key}.{e.Name}", 1, 1e10), StringComparer.OrdinalIgnoreCase),
                    };
                    break;
                case "Stock": m = m with { Stock = (int)num(q.Value, key, 1, 1_000_000) }; break;
                default: Log.Warn("content", $"{path}: unknown key {key}, skipped"); break;
            }
        }
        return m;
    }

    /// <summary>Drops { Boss, Miniboss, Won, Item }.</summary>
    private static EventDrops ParseDrops(JsonElement v, string path, Func<JsonElement, string, double, double, double> num, Func<JsonElement, string, string> str)
    {
        if (v.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Drops: not an object");
        var d = new EventDrops();
        foreach (var q in v.EnumerateObject())
        {
            var key = $"Drops.{q.Name}";
            switch (q.Name)
            {
                case "Boss": d = d with { Boss = num(q.Value, key, 0, 1) }; break;
                case "Miniboss": d = d with { Miniboss = num(q.Value, key, 0, 1) }; break;
                case "Won": d = d with { Won = num(q.Value, key, 0, 1) }; break;
                case "Item": d = d with { Item = str(q.Value, key).ToUpperInvariant() }; break;
                default: Log.Warn("content", $"{path}: unknown key {key}, skipped"); break;
            }
        }
        return d;
    }

    private static EventType ParseType(JsonElement e, string path, Func<JsonElement, string, double, double, double> num,
        Func<JsonElement, string, string> str, Func<JsonElement, string, double, double, (double, double)> pair)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Events: an entry is not an object");
        var t = new EventType { Name = "", Waves = [] };
        foreach (var q in e.EnumerateObject())
        {
            var key = $"Events.{q.Name}";
            switch (q.Name)
            {
                case "Name": t = t with { Name = str(q.Value, key) }; break;
                case "Title": t = t with { Title = str(q.Value, key) }; break;
                case "Weight": t = t with { Weight = num(q.Value, key, 0, 1000) }; break;
                case "DurationMinutes": t = t with { DurationMinutes = num(q.Value, key, 0.01, 24 * 60) }; break;
                case "WaveDelaySeconds": t = t with { WaveDelaySeconds = num(q.Value, key, 0, 3600) }; break;
                case "Distance": t = t with { Distance = pair(q.Value, key, 0, 2000) }; break;
                case "SpreadMeters": t = t with { SpreadMeters = num(q.Value, key, 0, 50) }; break;
                case "RewardMinShare": t = t with { RewardMinShare = num(q.Value, key, 0, 1) }; break;
                case "RewardFactor": t = t with { RewardFactor = num(q.Value, key, 0, 100) }; break;
                case "RewardFame": t = t with { RewardFame = num(q.Value, key, 0, 10_000_000) }; break;
                case "Chest": t = t with { Chest = str(q.Value, key).ToUpperInvariant() }; break;
                case "ChestMinutes": t = t with { ChestMinutes = num(q.Value, key, 0.01, 24 * 60) }; break;
                case "Waves": t = t with { Waves = ParseWaves(q.Value, key, path, num, str, pair) }; break;
                default: Log.Warn("content", $"{path}: unknown key {key}, skipped"); break;
            }
        }
        if (t.Name.Length == 0 || t.Name.Contains(' ', StringComparison.Ordinal) || t.Waves.Count == 0)
            throw new InvalidDataException("Events: an entry without a one-word Name or without Waves");
        return t;
    }

    /// <summary>Waves [{ Kind, Count [min, max], HealthFactor, DamageFactor }].</summary>
    private static List<EventWave> ParseWaves(JsonElement v, string key, string path, Func<JsonElement, string, double, double, double> num,
        Func<JsonElement, string, string> str, Func<JsonElement, string, double, double, (double, double)> pair)
    {
        if (v.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{key}: not a list");
        var waves = new List<EventWave>();
        foreach (var w in v.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{key}: an entry is not an object");
            var wave = new EventWave(WaveKind.Standard, (1, 1));
            foreach (var r in w.EnumerateObject())
            {
                var wkey = $"{key}.{r.Name}";
                switch (r.Name)
                {
                    case "Kind":
                        wave = wave with
                        {
                            Kind = Enum.TryParse<WaveKind>(str(r.Value, wkey), ignoreCase: true, out var k) && Enum.IsDefined(k) ? k
                                : throw new InvalidDataException($"{wkey}: {r.Value} is not standard, champion, miniboss or boss"),
                        };
                        break;
                    case "Count":
                        var (min, max) = pair(r.Value, wkey, 1, 50);
                        wave = wave with { Count = ((int)min, (int)max) };
                        break;
                    case "HealthFactor": wave = wave with { HealthFactor = num(r.Value, wkey, 0.01, 100) }; break;
                    case "DamageFactor": wave = wave with { DamageFactor = num(r.Value, wkey, 0, 100) }; break;
                    default: Log.Warn("content", $"{path}: unknown key {wkey}, skipped"); break;
                }
            }
            waves.Add(wave);
        }
        return waves;
    }
}
