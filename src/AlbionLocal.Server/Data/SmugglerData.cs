using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// The client data the smugglers' crates and the carried objects need (analysis/work/stageR/smugglers/SPEC.md, "SPEC" here):
/// <list type="bullet">
/// <item>piledobjects.bin: every entry (smugglercratepile, factionfortressweaponcache) in file order; NewPiledObject key 3 and the pile events
/// carry that index (brc.abo(int) 0x180737170 is a plain list index, the list filled in document order by brc.da 0x1807371F0).</item>
/// <item>carriables.bin: every carriable type element (crystal, energycore, wisp, siegebanner, smugglerscrate, helldungeonsoul, carriablekey,
/// factionbattlestandard) in file order with its rarities; NewCarriableObject key 3 and the deliver events carry that index (bbj.abq(int)
/// 0x180587320, a plain list index [infer: filled in document order like brc]); the &lt;carriablesettings&gt; decay time.</item>
/// <item>pvpchallenge.bin: the &lt;objective&gt; might, favor and smugglers standing a delivered crate's mightreward gives.</item>
/// <item>tilebiomereferences.bin: the tile of a pile's tilerefname per biome and sub-biome (SMUGGLER_CRATE_PILE → SWAMP_RED_SMUGGLER_WAGON).</item>
/// <item>treasuredistribution.bin: the &lt;Distribute group="SMUGGLER_PILE"&gt; draws of &lt;TreasurePile&gt; entries.</item>
/// <item>characters.bin: claimcarriableonsmugglersdestinationrange (5 m).</item>
/// </list>
/// Immutable once read.
/// </summary>
public sealed class SmugglerData
{
    /// <summary>The treasure spawn point prefab of the piles (tiles.xml: &lt;treasurespawnpoint subtype="SMUGGLER_PILE"/&gt;), a filler of the roaming-mob nodes.</summary>
    public const string PileSpotPrefab = "TreasureSpawnpointSmugglerPile";

    /// <summary>The name ending of the delivery station prefabs (&lt;smugglerscratedeliverystation deliverytype="smugglerscrate"/&gt;), one per biome and sub-biome.</summary>
    public const string StationSuffix = "_SMUGGLER_CRATE_DELIVERY_STATION";

    /// <summary>The carriable type of the smugglers' crates.</summary>
    public const string Crate = "SMUGGLERS_CRATE";

    public IReadOnlyList<PileDefinition> Piles { get; private init; } = [];

    public IReadOnlyList<CarriableType> Carriables { get; private init; } = [];

    /// <summary>pvpchallenge.xml objectives by uniquename.</summary>
    public IReadOnlyDictionary<string, ChallengeObjective> Objectives { get; private init; } = new Dictionary<string, ChallengeObjective>();

    /// <summary>The smugglers' pile draws (subtype SMUGGLER_PILE_SMALL / _MEDIUM / _LARGE; the base SMUGGLER_PILE has no slot).</summary>
    public IReadOnlyList<PileDistribute> Draws { get; private init; } = [];

    /// <summary>treasuredistribution.xml &lt;TreasurePile&gt; definitions: uniquename → the piledobjects.xml piletype and maxlifetime (86 400 s).</summary>
    public IReadOnlyDictionary<string, (string PileType, double MaxLifetimeSeconds)> TreasurePiles { get; private init; } = new Dictionary<string, (string, double)>();

    /// <summary>tilebiomereferences: tileref → biome → green/red/dead → tile.</summary>
    private IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, string>>> _tileRefs = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();

    /// <summary>characters.xml claimcarriableonsmugglersdestinationrange (5): how near a station a carrier may deliver.</summary>
    public double DeliverRange { get; private init; } = 5;

    /// <summary>carriablesettings carriabledroppeddefaultdecaytime (1800 s): how long a dropped carriable lies before it is gone.</summary>
    public double DroppedDecaySeconds { get; private init; } = 1800;

    public PileDefinition? Pile(string name) => Piles.FirstOrDefault(p => p.Name == name);

    public CarriableType? Carriable(string name) => Carriables.FirstOrDefault(c => c.Name == name);

    /// <summary>The tile of <paramref name="tileRef"/> for a cluster of <paramref name="biome"/> (Swamp, Forest, …) and template folder <paramref name="folder"/> (GREEN, RED, DEAD); null = none.</summary>
    public string? TileFor(string tileRef, string? biome, string? folder) =>
        biome is not null && folder is not null && _tileRefs.TryGetValue(tileRef, out var biomes) && biomes.TryGetValue(biome, out var subs)
        && subs.TryGetValue(folder.ToLowerInvariant(), out var tile) ? tile : null;

    /// <summary>The draw of <paramref name="subtype"/> for an Outlands cluster of <paramref name="continent"/> and <paramref name="tier"/>; null = none.</summary>
    public PileDraw? DrawFor(string subtype, string continent, int tier) =>
        Draws.Where(d => d.Subtype == subtype && string.Equals(d.Continent, continent, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Tiers.GetValueOrDefault(tier)).FirstOrDefault(t => t is not null);

    private static double D(XElement e, string a, double fallback) =>
        double.TryParse((string?)e.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public static SmugglerData Load(XElement piled, XElement carriables, XElement challenges, XElement tileRefs, XElement treasures, XElement? characters)
    {
        var piles = new List<PileDefinition>();
        foreach (var e in piled.Elements())
        {
            var rewards = e.Elements("smugglercratepilereward").Select(r => new PileReward(
                (string?)r.Attribute("carriabletype") ?? "", (byte)D(r, "rarity", 0), D(r, "weight", 0), (string?)r.Attribute("pickupvfxspell"))).ToList();
            piles.Add(new PileDefinition(piles.Count, e.Name.LocalName, (string?)e.Attribute("uniquename") ?? "", (string?)e.Attribute("tilerefname") ?? "",
                D(e, "pileitempickupdurationsec", 3), Math.Max(1, (int)D(e, "stacksize", 1)), D(e, "protectiontimesec", 0), D(e, "despawnsafteremptiedinsec", 30),
                D(e, "pickuprange", 5), rewards));
        }
        var settings = carriables.Element("carriablesettings");
        var decay = settings is null ? 1800 : D(settings, "carriabledroppeddefaultdecaytime", 1800);
        var types = new List<CarriableType>();
        foreach (var e in carriables.Elements().Where(e => e.Name.LocalName != "carriablesettings"))
        {
            var rarities = new Dictionary<byte, CarriableRarity>();
            foreach (var r in e.Elements("rarity"))
                rarities.TryAdd((byte)D(r, "raritylevel", 0), new CarriableRarity((byte)D(r, "raritylevel", 0), (string?)r.Attribute("tileid") ?? "",
                    (string?)r.Attribute("carrybuff") ?? "", (string?)r.Attribute("mightreward"), (string?)r.Attribute("claimvfxspell"),
                    D(r, "initialspawnpickupdelaysec", 0)));
            types.Add(new CarriableType(types.Count, e.Name.LocalName, (string?)e.Attribute("uniquename") ?? "", D(e, "deliverduration", 3),
                D(e, "pickupduration", 0.5), D(e, "droppeddecaytime", decay), rarities));
        }
        var objectives = new Dictionary<string, ChallengeObjective>(StringComparer.Ordinal);
        foreach (var o in challenges.Descendants("objective"))
            if ((string?)o.Attribute("uniquename") is { Length: > 0 } name)
                objectives.TryAdd(name, new ChallengeObjective(name, D(o, "might", 0), D(o, "favor", 0),
                    o.Elements("standingreward").ToDictionary(s => (string?)s.Attribute("type") ?? "", s => D(s, "value", 0))));
        var refs = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var t in tileRefs.Elements("tileref"))
        {
            var biomes = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in t.Elements("biome"))
                biomes[(string?)b.Attribute("name") ?? ""] = new[] { "green", "red", "dead" }
                    .Where(s => (string?)b.Attribute(s) is { Length: > 0 }).ToDictionary(s => s, s => (string)b.Attribute(s)!);
            refs[(string?)t.Attribute("uniquename") ?? ""] = biomes;
        }
        var draws = new List<PileDistribute>();
        foreach (var d in treasures.Element("GameobjectDistribution")?.Elements("Distribute") ?? [])
        {
            if ((string?)d.Attribute("group") != "SMUGGLER_PILE") continue;
            var tiers = new Dictionary<int, PileDraw>();
            foreach (var t in d.Elements("ClusterTier"))
            {
                if (t.Element("Random") is not { } r) continue;
                var entries = r.Elements("TreasurePile").Select(x => ((string?)x.Attribute("name") ?? "", D(x, "weight", 1))).Where(x => x.Item1.Length > 0 && x.Item2 > 0).ToList();
                tiers.TryAdd((int)D(t, "tier", 0), new PileDraw((int)D(r, "maxslotsused", 0), D(r, "activationtimemin", 0), D(r, "activationtimemax", 0),
                    D(r, "positioncooldown", 0), D(r, "activationchance", 1), entries));
            }
            draws.Add(new PileDistribute((string?)d.Attribute("continent") ?? "", ((string?)d.Attribute("subtype") ?? "").ToUpperInvariant(), tiers));
        }
        var treasurePiles = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        foreach (var t in treasures.Element("TreasurePiles")?.Elements("TreasurePile") ?? [])
            if ((string?)t.Attribute("uniquename") is { Length: > 0 } name)
                treasurePiles.TryAdd(name, ((string?)t.Attribute("piletype") ?? name, D(t, "maxlifetime", 86400)));
        var range = characters?.Descendants().Select(e => (string?)e.Attribute("claimcarriableonsmugglersdestinationrange")).FirstOrDefault(v => v is not null);
        return new SmugglerData
        {
            Piles = piles, Carriables = types, Objectives = objectives, Draws = draws, TreasurePiles = treasurePiles, _tileRefs = refs, DroppedDecaySeconds = decay,
            DeliverRange = double.TryParse(range, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 5,
        };
    }

    /// <summary>Reads the six files from the client's GameData folder.</summary>
    public static SmugglerData Load(string folder)
    {
        XElement? characters = null;
        try { characters = GameDataFile.Open(folder, "characters.bin").Root(); }
        catch (InvalidDataException) { } // the 5 m default
        return Load(GameDataFile.Open(folder, "piledobjects.bin").Root(), GameDataFile.Open(folder, "carriables.bin").Root(),
            GameDataFile.Open(folder, "pvpchallenge.bin").Root(), GameDataFile.Open(folder, "tilebiomereferences.bin").Root(),
            GameDataFile.Open(folder, "treasuredistribution.bin").Root(), characters);
    }
}

/// <summary>A piledobjects.xml entry: its client index, element name, uniquename, tilerefname, pickup channel seconds, crates, protection and despawn seconds, range, rewards.</summary>
public sealed record PileDefinition(int Index, string Kind, string Name, string TileRef, double PickupSeconds, int StackSize, double ProtectionSeconds,
    double DespawnSeconds, double PickupRange, IReadOnlyList<PileReward> Rewards);

/// <summary>A &lt;smugglercratepilereward&gt;: the carriable type and rarity a crate of the pile is, by weight, and the VFX spell of its pickup.</summary>
public sealed record PileReward(string Carriable, byte Rarity, double Weight, string? PickupVfx);

/// <summary>A carriables.xml type: its client index, element name, uniquename, deliver and pickup seconds, a dropped one's decay, its rarities.</summary>
public sealed record CarriableType(int Index, string Kind, string Name, double DeliverSeconds, double PickupSeconds, double DroppedDecaySeconds,
    IReadOnlyDictionary<byte, CarriableRarity> Rarities);

/// <summary>A carriable rarity: its ground tile, the carry buff (unique: the client knows the carried object by it), might reward, claim VFX, the pickup delay of a new drop.</summary>
public sealed record CarriableRarity(byte Level, string Tile, string CarryBuff, string? MightReward, string? ClaimVfx, double InitialPickupDelaySeconds);

/// <summary>A pvpchallenge.xml objective: might, favor (the FAVOR currency) and standing by standing type.</summary>
public sealed record ChallengeObjective(string Name, double Might, double Favor, IReadOnlyDictionary<string, double> Standing);

/// <summary>A &lt;Distribute group="SMUGGLER_PILE"&gt;: continent, subtype and its draws by cluster tier.</summary>
public sealed record PileDistribute(string Continent, string Subtype, IReadOnlyDictionary<int, PileDraw> Tiers);

/// <summary>A &lt;Random&gt; draw of piles: slots at once, the seconds until a free slot draws again, a spot's cooldown, the chance and the weighted TreasurePile names.</summary>
public sealed record PileDraw(int Slots, double MinSeconds, double MaxSeconds, double CooldownSeconds, double Chance, IReadOnlyList<(string Name, double Weight)> Entries);
