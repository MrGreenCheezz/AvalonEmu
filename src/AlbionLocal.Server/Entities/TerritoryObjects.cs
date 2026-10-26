using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// The state of one guild territory or castle (stageP territories SPEC §6): its world.xml entry, cluster and owner. A castle's owner comes from
/// a capture (phase P5, <see cref="Fight"/>), a watchtower's from a claim (phase P3); both are saved (schema v39 territory_owners), a watchtower's
/// food, access and tower power points with them (v41 territory_state). The NPC holder of Territories:NpcOwners (<see cref="NpcHeld"/>), its
/// guards and the season's Tower Guardian are runtime only.
/// </summary>
public sealed class TerritoryState(WorldTerritory territory, ClusterInfo cluster)
{
    public WorldTerritory Territory { get; } = territory;

    public ClusterInfo Cluster { get; } = cluster;

    public Guid Id => Territory.Id;

    /// <summary>The castle fight, its capture zone, chest and scoring (castles only; runtime, but for the owner).</summary>
    public CastleFight Fight { get; } = new();

    /// <summary>Since when the owner holds it (UTC; MinValue without one).</summary>
    public DateTime Since { get; set; }

    /// <summary>The owner guild; Guid.Empty = none.</summary>
    public Guid Owner { get; set; }

    public string OwnerName { get; set; } = "";

    /// <summary>The owner's alliance; Guid.Empty = none.</summary>
    public Guid Alliance { get; set; }

    public string AllianceName { get; set; } = "";

    /// <summary>The owner's crest (null without an owner).</summary>
    public GuildLogo? Logo { get; set; }

    /// <summary>
    /// The food stored (whole nutrition) at <see cref="FedAt"/> (UTC); it runs down by one per territorytypes secondspernutrition from then
    /// (Territories:Food; <see cref="Systems.Territories.NutritionAt"/>).
    /// </summary>
    public double Nutrition { get; set; }

    public DateTime FedAt { get; set; }

    /// <summary>ChangeTerritoryAccessRights key 1 as sent (@MONOLITH_TOGGLE_GUILD_ONLY / _EVERYONE; kept, no shield yet).</summary>
    public byte Access { get; set; }

    /// <summary>The tower power points at <see cref="PowerAt"/> (UTC); they decay by the tower level's decaydays from then (phase P4).</summary>
    public double Power { get; set; }

    public DateTime PowerAt { get; set; }

    /// <summary>The last scoring tick of a watchtower's owner (UTC; null = none yet).</summary>
    public DateTime? LastScore { get; set; }

    /// <summary>The monolith on its loaded map (null = none standing).</summary>
    public MonolithObject? Monolith { get; set; }

    /// <summary>Territories:NpcOwners: the NPC holder's guards standing at the monolith (empty = none spawned).</summary>
    public List<Mob> NpcGuards { get; } = [];

    /// <summary>When the NPC holder's guards were beaten (UTC): the territory is free until they stand again (GuardSettings respawntimeseconds).</summary>
    public DateTime? NpcBeatenAt { get; set; }

    /// <summary>The season's Tower Guardian standing at the monolith (null = none) and when the last one died (UTC; runtime only).</summary>
    public Mob? Invader { get; set; }

    public DateTime? InvaderKilledAt { get; set; }

    /// <summary>Whether the NPC holder of Territories:NpcOwners holds it (<see cref="Systems.Territories.NpcGuild"/>).</summary>
    public bool NpcHeld => Owner == Systems.Territories.NpcGuild;

    /// <summary>world.xml cluster timeregion (US_22_00 …; "" = none).</summary>
    public string TimeRegion => Cluster.Attrs.GetValueOrDefault("timeregion") ?? "";

    public bool Owned => Owner != Guid.Empty;
}

/// <summary>
/// A watchtower territory's monolith (stageP territories SPEC §2.1) as NewMonolithObject (109, aui; cjb.a3 0x180a9e940 → cln.af 0x180b55570):
/// key 0 id, 1 the position (cjb.a binds the object to the landscape's "monolith"/"watchtower" object there, else drops it: the critical
/// member's own position, world.xml's monolith), 2 its direction, 3-6 the owner guild's Guid and name and its alliance's Guid and name (cln.af
/// copies them and redraws the territory UI when 3 changes), 7 the territory's Guid (the cln ctor looks the client's territory up with it,
/// aa.be; without one cln.af returns at once), 21-27 the owner's crest (bux.j, only with key 3 set), 28 the territory state message (av.a,
/// shown on updates only). Unowned here: 3-6 Empty and "", every other key 0 (8-20, 28; the neutral values, not decoded: SPEC §9 risk 2).
/// Landscape-bound like the shrines: never left, sent again on re-entry.
/// </summary>
public sealed class MonolithObject(long id, CriticalMember member, TerritoryState state) : WorldObject(id, member)
{
    public TerritoryState State { get; } = state;

    public override string Kind => "monolith";

    public override string TypeName => $"{State.Territory.Name} ({State.Territory.ResourceType}{(State.Owned ? $", {State.OwnerName}" : ", unowned")})";

    public override bool LeaveOnRange => false;

    public NewMonolithObjectEvent Build()
    {
        var e = new NewMonolithObjectEvent
        {
            c6g = Id, c6h = Position, c6i = Direction,
            c6j = State.Owner, c6k = State.OwnerName, c6l = State.Alliance, c6m = State.AllianceName,
            c6n = State.Id,
        };
        if (State.Owned && State.Logo is { } l)
            (e.c61, e.c62, e.c63, e.c64, e.c65, e.c66, e.c67) = (l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
        return e;
    }

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return Build();
    }
}

/// <summary>
/// A castle or castle outpost (stageP territories SPEC §2.5) as NewCastleObject (112, atb; cjb.b0 0x180aa5bc0 → cj0.af 0x180b2c350): key 0 id,
/// 1 the tiles.xml tile of the castle member (the cj0 ctor builds the view from a9f.u GetTile, T1_CASTLE), 2 the castles.xml index of its
/// castletype (bbk.abo(int); none = no object), 3 the territory's Guid (the critical member's, aa.be), 4-7 owner guild Guid and name,
/// alliance name and Guid (no crest with key 4 empty), 8/9 position and direction, 10 the castle phase (a6n 0-9; 0 = calm in the music and
/// stingers of CastleObjectView), 11 a flag (+0xF0, not decoded: false), 12 the ChestState (Empty 8: no chest), 13 the next chest's rarity
/// (bsl.aq: standard/uncommon/rare/legendary, "" = none), 14-20 the owner's crest. Phase P5: the phase, owner and chest of its
/// <see cref="CastleFight"/> (<see cref="Systems.Territories"/>; sent again to its observers on each change). Its view is made from the tile,
/// not bound to the landscape: left with the window like a building. <see cref="Guards"/> are the &lt;castle&gt; component's guardspawnpoints in
/// world coordinates (their pos is relative to the castle tile, turned by its yaw).
/// </summary>
public sealed class CastleObject : WorldObject
{
    /// <summary>ChestState Empty (8): no castle chest (BossLocked 0 … Unlocked 6, Open 7, Empty 8).</summary>
    public const int ChestEmpty = 8;

    public CastleObject(long id, CriticalMember member, TerritoryState state, CastleType type) : base(id, member)
    {
        (State, Type) = (state, type);
        var castle = member.Components.FirstOrDefault(c => c.Name.LocalName == "castle");
        Vector2 Place(Vector2 local)
        {
            var (x, z) = TileMath.Rotate(local.X, local.Y, member.Direction);
            return member.Position + new Vector2((float)x, (float)z);
        }
        Guards = castle is null ? [] : [.. castle.Elements("guardspawnpoint").Select(g => new CastleGuardSpot(
            (string?)g.Attribute("mobname") ?? "", ((string?)g.Attribute("guardtype") ?? "BASIC").ToUpperInvariant(),
            Place(TileMath.V2((string?)g.Attribute("pos")) ?? Vector2.Zero),
            (float)TileMath.Mod360(member.Direction + TileMath.Float((string?)g.Attribute("roty"))), (string?)g.Attribute("behavior")))
            .Where(g => g.Mob.Length > 0)];
        var zone = ((string?)castle?.Attribute("capturezonepos"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        CaptureZone = Place(zone.Length >= 3 ? new Vector2(TileMath.Float(zone[0]), TileMath.Float(zone[2])) : TileMath.V2((string?)castle?.Attribute("capturezonepos")) ?? Vector2.Zero);
    }

    public TerritoryState State { get; }

    public CastleType Type { get; }

    /// <summary>The castle's guards (BASIC, ELITE, LORD) from its tile's &lt;castle&gt; component.</summary>
    public IReadOnlyList<CastleGuardSpot> Guards { get; }

    /// <summary>The capture zone's centre (capturezonepos, the castle tile's own position for 0 0 0).</summary>
    public Vector2 CaptureZone { get; }

    /// <summary>Where its chest stands (the template's TreasureSpawnpointCastle / …CastleOutpost tile, else the capture zone); set at the map fill.</summary>
    public Vector2 ChestSpot { get; internal set; }

    /// <summary>a6n 0-9 (guessed names, capture T5; <see cref="CastleFight"/>): 0 = no fight, 7 = the capture zone.</summary>
    public int Phase => State.Fight.Phase;

    /// <summary>The ChestState of its standing chest (key 12; Empty 8 without one).</summary>
    public int Chest => State.Fight.Chest is { Map: not null } c ? c.LockState : ChestEmpty;

    public override string Kind => "castle";

    public override string TypeName => $"{Type.Name} {State.Territory.Name}{(State.Owned ? $" ({State.OwnerName})" : " (unowned)")}";

    public override bool LeaveOnRange => true;

    public NewCastleObjectEvent Build()
    {
        var e = new NewCastleObjectEvent
        {
            cu4 = Id, cu5 = Spawn.Tile, cu6 = Type.Index, cu7 = State.Id,
            cu8 = State.Owner, cu9 = State.OwnerName, cva = State.AllianceName, cvb = State.Alliance,
            cvc = Position, cvd = Direction, cve = Phase, cvf = false, cvg = Chest,
            cvh = State.Fight.Chest is { Map: not null } ? State.Fight.ChestRarity : "",
        };
        if (State.Owned && State.Logo is { } l)
            (e.cvi, e.cvj, e.cvk, e.cvl, e.cvm, e.cvn, e.cvo) = (l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
        return e;
    }

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return Build();
    }
}

/// <summary>A guardspawnpoint of a castle (its mobs.xml mob, guardtype BASIC / ELITE / LORD, world position and yaw, behavior GUARD of the mortars).</summary>
public sealed record CastleGuardSpot(string Mob, string GuardType, Vector2 Position, float Direction, string? Behavior);

/// <summary>
/// The fight for one castle (stageP territories SPEC §3.4, phase P5; <see cref="Systems.Territories"/>), its capture zone, chest and scoring.
/// The phase numbers are a6n values (CastlePhaseChanged 417 key 2 → cj0+0xB8): the client decodes 0 (calm; 7 → 0 plays won/lost by the
/// owner change), 7 (the capture zone: Castle.OnChanged shows the claim progress only then) and stingers on 4, 6 and 8; the other names are
/// the server's guess (capture T5). Runtime only: a fight ends with its map (the guards stand anew), the owner is saved.
/// </summary>
public sealed class CastleFight
{
    /// <summary>a6n values: no fight; the basic guards fought; elites on their way / standing; the lord on his way / standing; the capture zone; the lord back.</summary>
    public const int Idle = 0, Guards = 1, ElitesComing = 3, Elites = 4, LordComing = 5, Lord = 6, Capture = 7, LordBack = 8;

    public int Phase { get; internal set; }

    /// <summary>When the phase began (UTC; the phase timeout and the lord's return count from it).</summary>
    public DateTime PhaseSince { get; internal set; }

    /// <summary>When the next wave (elites, lord) arrives in a …Coming phase (UTC).</summary>
    public DateTime? WaveAt { get; internal set; }

    /// <summary>The standing defenders of the phase: the basic guards (Idle, Guards), the elites, the lord.</summary>
    public List<Mob> Mobs { get; } = [];

    /// <summary>The map whose castle object the fight belongs to (a new map of the cluster starts it anew).</summary>
    public Map? Map { get; internal set; }

    /// <summary>After a capture: when the basic guards stand again (UTC); null = at once.</summary>
    public DateTime? GuardsBackAt { get; internal set; }

    /// <summary>The guild whose claim fills the capture zone (Guid.Empty = none), its alliance, and the claim 0..1.</summary>
    public Guid Claimer { get; internal set; }

    public Guid ClaimerAlliance { get; internal set; }

    public double Progress { get; internal set; }

    /// <summary>Whether the claim is the owner's own (it ends the fight with the castle kept).</summary>
    public bool OwnerClaim { get; internal set; }

    /// <summary>Whether two teams (or a defender) stand in the zone: the claim pauses.</summary>
    public bool Contested { get; internal set; }

    internal DateTime LastTick { get; set; }

    internal DateTime LastProgressSent { get; set; }

    internal (Guid Claimer, long Progress)? SentProgress { get; set; }

    internal Guid SentLogo { get; set; }

    /// <summary>The castle chest standing now (null = none), its rarity text (NewCastleObject 13), when it goes, and the last schedule slot drawn.</summary>
    public LootChest? Chest { get; internal set; }

    public string ChestRarity { get; internal set; } = "";

    public DateTime ChestUntil { get; internal set; }

    public DateTime? ChestSlot { get; internal set; }

    internal int SentChest { get; set; } = CastleObject.ChestEmpty;

    /// <summary>The last scoring tick of the owner (UTC).</summary>
    public DateTime? LastScore { get; internal set; }

    public override string ToString() => $"phase {Phase}" + (Phase == Capture ? $", claim {Progress:0.##} by {Claimer}{(Contested ? " (contested)" : "")}" : "")
        + $", {Mobs.Count(m => m is { Dead: false, Removed: false })} defender(s) standing";
}
