using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

// The static world objects of stage 2 (SPEC stage 2 §1 and §3, "SPEC" below): what the client cannot place or use without
// an event from the server. Each one sends exactly the keys the official server sends (captures 103829, 114814); zero and
// default values are left out by the serializer. Kinds still missing: NewRealEstate 191 (plot ownership unknown),
// NewFishingZoneObject 360, NewBannerObject 519, outposts 365/366 (open world). Mobs: Entities/Mob.cs.

/// <summary>
/// An object whose client gets an AccessStatus (209) with it (<see cref="VisibilityTracker"/>: on a Join in one batch after JoinFinished,
/// later right after its New* event): the NPC buildings and the island management (stageH islands SPEC §1.5).
/// </summary>
public interface IAccessStatusObject
{
    AccessStatusEvent AccessStatus();
}

/// <summary>
/// An NPC building (NewBuilding 45, client as7; SPEC §3): the action building tile of the template, whose tile name is
/// its buildings.xml uniquename. The faction trader (its Guid is a world.xml questgiver id) is cluster-wide and comes
/// with QuestGiverInfoForPlayer 259; the others are left when out of range.
/// </summary>
public sealed class Building(long id, BuildingSpawn spawn, long durabilityStamp, long loadTime) : WorldObject(id, spawn), IAccessStatusObject
{
    public new BuildingSpawn Spawn { get; } = spawn;

    public BuildingTemplate Template => Spawn.Template;

    /// <summary>The faction trader (world.xml questgiver): cluster-wide, never left, with QuestGiverInfoForPlayer.</summary>
    public bool IsQuestGiver => Spawn.QuestGiver is not null;

    public override string Kind => "building";
    public override string TypeName => Template.Name;
    public override AnnounceRange Range => IsQuestGiver ? AnnounceRange.Cluster : AnnounceRange.Window;
    public override bool LeaveOnRange => true;

    /// <summary>
    /// Key 8, the stamp of the durability bar (key 7). Official: a stamp that stays with the building (2024–2026 in the
    /// captures); here the map's creation time, fixed while the map lives.
    /// </summary>
    public long DurabilityStamp { get; } = durabilityStamp;

    /// <summary>
    /// Key 20, the map instance load time: the client's periodic-timer anchor when the template has a period. Official
    /// cities: about 2.4 min after the server start; a static city may use the map's creation time.
    /// </summary>
    public long LoadTime { get; } = loadTime;

    /// <summary>
    /// Key 22, the stamp of the craft-capacity bar (key 21): the map load time (key 20) for the bank, the marketplace and
    /// the island bank (bankbuilding, marketplacebuilding; the Black Market too, the client's same marketplace class cpi
    /// [infer: not captured]), key 8 for every other NPC building (repair shop, vanity store,
    /// meld building, arena, faction trader, farming merchant; SPEC §3, verify-code nb*.py).
    /// </summary>
    public long CraftCapacityStamp => Template.Kind is "bankbuilding" or "marketplacebuilding" or "blackmarketbuilding" ? LoadTime : DurabilityStamp;

    /// <summary>A buildings.xml number as a7m raw (fixed point × 10000), as keys 7, 18 and 21 carry it.</summary>
    private static long Raw(double value) => (long)Math.Round(value * 10000);

    /// <summary>
    /// NewBuilding with the keys of every captured NPC building (Lymhurst repair shop: {0:1449, 1:guid, 2:497,
    /// 3:"FOREST_GREEN_REPAIRSHOP", 4:[-55,-25], 5:90, 6:true, 7:17496000000, 8:stamp, 18:7950000000, 19:-1, 20:load,
    /// 21:7949930000, 22:=8, 26-28:true, 29:0}). Keys 11/12 (6-character strings, masked by the decoder, meaning unknown),
    /// 13/14 (faction trader names; the client falls back to the template) and 16 (toggles while in use) are left out:
    /// the client reads all of them null-safe (cou.abv). Key 15 must stay false (true makes the client build the cow class).
    /// Then, for the faction trader, QuestGiverInfoForPlayer. The building's <see cref="AccessStatus"/> is sent apart by the
    /// <see cref="VisibilityTracker"/> (on a Join after JoinFinished, later right after this).
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return NewBuilding();
        // cpm starts with an empty state; SimpleBehaviourObjectView requires event 645 even
        // for the authored default. Initialize each viewer on every announcement, without
        // storing per-character state on the shared map's building. Journal transitions are separate.
        if (Template.Kind == "simplebehaviourbuilding"
            && (string?)Template.Element.Elements("triggerablestate")
                .FirstOrDefault(s => (bool?)s.Attribute("default") == true)?.Attribute("name") is { Length: > 0 } state)
            yield return new SimpleBehaviourBuildingStateUpdateEvent { ddn = Id, dyl = state };
        if (Spawn.QuestGiver is { } giver)
            // coz.ajv → cmh.r 0x180b641c0 reads keys 3 (a7m) and 4 (stamp) into the quest-giver bar and bools 5/6; it ignores
            // keys 1 and 2, which the official server sends anyway. 3, 5 and 6 are never sent.
            yield return new QuestGiverInfoForPlayerEvent
            {
                BuildingObjectId = Id,                       // [0] the building above
                QuestGiverGuid = giver.Id,                   // [1] world.xml questgiver id
                QuestGiverType = Spawn.QuestGiverType ?? 0,  // [2] questgivertypes.xml id (FACTIONSTORE_SWAMP 20)
                BarStamp = LoadTime,                         // [4] = the building's key 20
            };
    }

    /// <summary>The access groups of every NPC building's AccessStatus (002835: always these three, in this order).</summary>
    public static readonly string[] AccessGroups = ["@_Owner", "@_Guild", "@_Everyone"];

    /// <summary>
    /// AccessStatus (209, akh) {0 building, 1 preset 2, 4 <see cref="AccessGroups"/>, 5 the level per group, 6 one "" per
    /// group}, for every NPC building (002835: Thetford and Lymhurst, keys 2/3 never). Its timing there: on a Join all of
    /// them in one batch after the Join response and JoinFinished (+85.048-85.050, the NewBuildings at +85.034), for a
    /// building that comes into view later 10-30 ms after its NewBuilding (+58.235 → +58.264); no client request before. The client keeps
    /// it as the building's access rights (a6r: group → level, default @_Owner owner, the others noaccess, c2r ctor
    /// 0x18293F710) and checks them itself before some actions: the salvage at a
    /// repair shop (cml.al 0x180B6F5B0 → cpl.ad4 0x180BBD130, AccessRightsRepairBuilding.SalvageItem) shows "you don't have
    /// permission to salvage here" (InvalidRights) and sends nothing while @_Everyone is noaccess. Levels as captured: the
    /// bank, the marketplace and the faction trader owner/noaccess/noaccess; every other NPC building (repair shop, meld
    /// building, vanity store, arena agency, crafting stations) owner/friend/user.
    /// </summary>
    public AccessStatusEvent AccessStatus() => new()
    {
        ddn = Id,
        bq5 = 2, // [1] Preset 2 on every captured NPC building
        bq8 = AccessGroups,
        bq9 = Template.Kind is "bankbuilding" or "marketplacebuilding" or "blackmarketbuilding" or "factionbuilding" || IsQuestGiver
            ? ["owner", "noaccess", "noaccess"] : ["owner", "friend", "user"],
        bra = ["", "", ""],
    };

    /// <summary>
    /// The NewBuilding of <see cref="BuildCreate"/>; <paramref name="inUse"/> sets key 16, which the official server toggles
    /// while a station action runs (stage 6 SPEC §6c.3: re-sent with key 16 = true at the start, without it at the end).
    /// </summary>
    public NewBuildingEvent NewBuilding(bool inUse = false) =>
        new()
        {
            ObjectId = Id,                              // [0]
            BuildingGuid = Spawn.Guid,                  // [1] criticalObjects guid (= questgiver id for the faction trader)
            TypeId = Template.Id,                       // [2] buildings.xml index + 1; must resolve (bbh.abo, else the handler throws)
            Tile = Spawn.Tile,                          // [3] the tile the client places (actionbuilding tile = uniquename)
            Position = Position,                        // [4] tile position; left out at (0, 0) (the bank interior)
            Direction = Direction,                      // [5] left out when 0
            cug = true,                                 // [6] true on every NPC building, meaning unknown
            Durability = Raw(Template.Durability),      // [7] durability (absent: 100) × 10000
            DurabilityStamp = DurabilityStamp,          // [8]
            Nutrition = Raw(Template.NutritionStorage), // [18] nutritionstorage (absent: 1) × 10000
            ItemTypeId = -1,                            // [19] no item type
            LoadTime = LoadTime,                        // [20]
            CraftCapacity = Raw(Template.CraftCapacity), // [21] only when > 0 (zero is left out)
            CraftCapacityStamp = CraftCapacityStamp,    // [22]
            ct5 = true, ct6 = true, ct7 = true,         // [26]-[28] true on every NPC building
            ctv = inUse,                                // [16] in use (a station action under way); left out otherwise
            Extra = new() { [29] = (byte)0 },           // [29] byte 0: the official server always sends it (ByteZero)
        };
}

/// <summary>
/// An exit (NewExit 215, atn): the client placed its tile with the landscape and registered the world.xml exit Guid
/// (ddt.al1 → ddt.d); the event binds to the tile at the exact position (cjb.a(pos, tag) 0x180aa1b10) and makes it usable.
/// Never left; re-sent on re-entry.
/// </summary>
public sealed class Exit(long id, ExitSpawn spawn, WorldExit world) : WorldObject(id, spawn)
{
    public new ExitSpawn Spawn { get; } = spawn;

    /// <summary>The world.xml exit (target, target type; its pos = the tile position, checked by the spawner).</summary>
    public WorldExit World { get; } = world;

    /// <summary>world.xml exit/@pos, which is the tile position (the spawner checks |d| &lt; 0.001 per axis).</summary>
    public override Vector2 Position => World.Position;

    public override string Kind => "exit";
    public override string TypeName => $"to {World.TargetCluster}";
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewExitEvent
        {
            ObjectId = Id,              // [0]
            ExitGuid = World.Id,        // [1] the Guid ddt.al1 registered; another one gives an exit without target data, silently
            Position = Position,        // [2] world.xml exit/@pos = the tile position (bound to the landscape)
        };
    }
}

/// <summary>A fast travel point (NewTravelpoint 213, au5): the client does not place its tile (ddt.alu = false); the event places the tile of key 3.</summary>
public sealed class Travelpoint(long id, TravelpointSpawn spawn) : WorldObject(id, spawn)
{
    public new TravelpointSpawn Spawn { get; } = spawn;

    public override string Kind => "travel point";
    public override string TypeName => Spawn.Tile;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewTravelpointEvent
        {
            ObjectId = Id,                  // [0]
            Position = Position,            // [1] travelpoint position
            TravelpointGuid = Spawn.Guid,   // [2] travelpoint id
            Tile = Spawn.Tile,              // [3] FASTTRAVEL_POINT: the tile the client places (cmr ctor → a9f.u(name)); unknown = no visual
        };
    }
}

/// <summary>The city's war wagon (NewFactionWarfarePortal 662, att): placed by the client, made usable by the event.</summary>
public sealed class FactionWarfarePortal(long id, FactionWarfarePortalSpawn spawn, byte faction) : WorldObject(id, spawn)
{
    public new FactionWarfarePortalSpawn Spawn { get; } = spawn;

    /// <summary>FlaggingStatus of the city (Highland 1, Forest 2, Steppe 3, Mountain 4, Swamp 5, Caerleon 6), read by ckd.af_4.</summary>
    public byte Faction { get; } = faction;

    public override string Kind => "faction warfare portal";
    public override string TypeName => Spawn.Tile;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewFactionWarfarePortalEvent
        {
            ObjectId = Id,              // [0]
            Position = Position,        // [1] the tile with the factionwarfareportal component (bound)
            PortalGuid = Spawn.Guid,    // [2] criticalObjects guid
            Tile = Spawn.Tile,          // [3] never read by the client, sent as official
            Faction = Faction,          // [4]
        };
    }
}

/// <summary>The island seller (NewIslandAccessPoint 214, at6): bound to its landscape tile; never left.</summary>
public sealed class IslandAccessPoint(long id, IslandAccessPointSpawn spawn) : WorldObject(id, spawn)
{
    public new IslandAccessPointSpawn Spawn { get; } = spawn;

    public override string Kind => "island access point";
    public override string TypeName => Spawn.Tile;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewIslandAccessPointEvent
        {
            ObjectId = Id,                  // [0]
            Position = Position,            // [1] bound to the tile
            Direction = Direction,          // [2] tile direction
            AccessPointGuid = Spawn.Guid,   // [3] world.xml islandaccesspoint id
        };
    }
}

/// <summary>
/// The island management spot of an island instance (NewIslandManagement 225, at7, client cku.af 0x180B3BB60; stageH islands SPEC §1.5): the
/// islandmanagement tile of the template, owned by the island's owner. Announced like an NPC building (the window, left out of range,
/// sent again on re-entry), with its AccessStatus preset 3.
/// </summary>
public sealed class IslandManagement(long id, IslandManagementSpawn spawn, Game.Island island, Func<Guid, (Guid Id, string Name)?> guildOf)
    : WorldObject(id, spawn), IAccessStatusObject
{
    public new IslandManagementSpawn Spawn { get; } = spawn;

    public Game.Island Island { get; } = island;

    public override string Kind => "island management";
    public override string TypeName => Spawn.Tile;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewIslandManagementEvent
        {
            c20 = Id,                                       // [0]
            c21 = Position,                                 // [1] the tile position (185.5, 64.5) [cap 113433]
            c22 = Island.Owner,                             // [2] the owner character (= its Join key 1)
            c23 = guildOf(Island.Owner)?.Id ?? Guid.Empty,  // [3] the owner's guild (= Join key 56); left out without one
            c24 = Island.OwnerName,                         // [4] the owner's name
        };
    }

    /// <summary>
    /// AccessStatus {0 id, 1 preset 3 (cku.af builds AccessRights…co(3)), 4 <see cref="Building.AccessGroups"/>, 5 owner / noaccess /
    /// noaccess, 6 "", the owner's guild name (or ""), ""} [cap 113433, strings by length only].
    /// </summary>
    public AccessStatusEvent AccessStatus() => new()
    {
        ddn = Id,
        bq5 = 3,
        bq8 = Building.AccessGroups,
        bq9 = ["owner", "noaccess", "noaccess"],
        bra = ["", guildOf(Island.Owner)?.Name ?? "", ""],
    };
}

/// <summary>The expedition agent (NewExpeditionAgent 314, ato): bound to its landscape tile; never left.</summary>
public sealed class ExpeditionAgent(long id, ExpeditionAgentSpawn spawn, int agentId) : WorldObject(id, spawn)
{
    /// <summary>
    /// Key 4. The official server sends a name made by the agent's namegenerator (16 characters in the capture); the
    /// client shows whatever comes, so any non-empty name does (SPEC §3).
    /// </summary>
    public const string NpcName = "Expedition Agent";

    public new ExpeditionAgentSpawn Spawn { get; } = spawn;

    /// <summary>expeditionagents.xml id of the tile's agenttype (checked by the spawner: an unknown id gives a null agent template).</summary>
    public int AgentId { get; } = agentId;

    public override string Kind => "expedition agent";
    public override string TypeName => Spawn.AgentType;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewExpeditionAgentEvent
        {
            ObjectId = Id,              // [0]
            Position = Position,        // [1] bound to the tile
            AgentGuid = Spawn.Guid,     // [2] criticalObjects guid
            AgentType = AgentId,        // [3] cj8.af_2 → bcc.abo_1
            NpcName = NpcName,          // [4]
        };
    }
}

/// <summary>
/// A location marker (NewLocationMarker 383, auc): no visual; the client makes a cli object and tells its listeners
/// (ci7+0x210) about the location (labels, triggers). No tile lookup. Left when out of range.
/// </summary>
public sealed class LocationMarker(long id, LocationMarkerSpawn spawn) : WorldObject(id, spawn)
{
    public new LocationMarkerSpawn Spawn { get; } = spawn;

    public override string Kind => "location marker";
    public override string TypeName => Spawn.LocationId;
    public override bool LeaveOnRange => true;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewLocationMarkerEvent
        {
            ObjectId = Id,                  // [0]
            Position = Position,            // [1]
            LocationId = Spawn.LocationId,  // [2] locationid (MARKETPLACE, REPAIR, …)
            Radius = Spawn.Radius,          // [3]
        };
    }
}

/// <summary>
/// A static tree or rock of the template (a tile with a harvestable component), or a resource node: a harvestable of a
/// resource prefab the client's placement put on a node tile (stage 4b, <see cref="HarvestableSpawn.Prefab"/>,
/// <see cref="ResourceLayout"/>). Sent in NewSimpleHarvestableObjectList 39 (<see cref="Lists"/>); WOOD, ROCK, FIBER and
/// ORE bind to the landscape tile at the exact position. Never left; re-sent on re-entry, with the charges it has then (a
/// depleted one with 0: capture 002835, nodes 2660, 2239, 2223). An enchanted resource node
/// (<see cref="RareState"/> &gt; 0) is not in the lists: it comes as NewHarvestableObject 40 (<see cref="BuildCreate"/>,
/// stage 4b SPEC §5.4) and is left by visibility. <see cref="Charges"/> starts full; gathering takes them
/// (World/Harvesting.cs, stage 4a) and the regrowth timer of the map gives them back one step at a time (<see cref="RegrowAt"/>).
/// A killed mob's skinnable corpse (<see cref="Corpse"/>, World/Corpses.cs) is one too: made at the death position, sent as
/// event 40 with the corpse keys, never regrown, gone after its decay time.
/// </summary>
public sealed class Harvestable(long id, HarvestableSpawn spawn) : WorldObject(id, spawn)
{
    /// <summary>Official lists carry at most 40 objects.</summary>
    public const int ListSize = 40;

    public new HarvestableSpawn Spawn { get; } = spawn;

    public HarvestableTemplate Type => Spawn.Type;

    public int Tier => Spawn.Tier.Tier;

    /// <summary>The tier's numbers (harvest time, charge levels, regrowth).</summary>
    public HarvestableTier TierData => Spawn.Tier;

    /// <summary>Charges left (key 4); full = the upper bound of the tier's highest Charge level (WOOD T1 2, WOOD_GIANTTREE T1 5).</summary>
    public int Charges { get; set; } = spawn.Charges;

    /// <summary>The full charges of the tier (<see cref="HarvestableSpawn.Charges"/>), what regrowth stops at.</summary>
    public int FullCharges => Spawn.Charges;

    /// <summary>
    /// The rare (enchantment) state, HarvestableChangeState key 2: 0 for every static tree and in all 84 captured states
    /// (0204 has rareresourcedistribution SAFE); the client picks the gfx by it (cn2.ajv 0x180b881d0, cn2+0x90). A resource
    /// node's is rolled by the server (<see cref="ResourceNodes"/>), only while no client knows the node: it decides
    /// whether the node goes in a list (0) or as event 40.
    /// </summary>
    public byte RareState { get; set; }

    /// <summary>The killed mob, owner and loot protection of a corpse harvestable (<see cref="Corpses"/>); null for every other one.</summary>
    public HarvestableCorpse? Corpse { get; init; }

    /// <summary>A resource node placed by the client's layout (stage 4b), not a static one of the template.</summary>
    public bool IsResourceNode => Spawn.Prefab is not null;

    /// <summary>Whether the node goes out in NewSimpleHarvestableObjectList 39 (not enchanted); else as NewHarvestableObject 40.</summary>
    public bool Listed => RareState == 0 && Corpse is null;

    /// <summary><see cref="World.Clock"/> time of the next regrowth step; null while full (or for a type that does not regrow).</summary>
    public TimeSpan? RegrowAt { get; set; }

    public override string Kind => Corpse is not null ? "corpse" : IsResourceNode ? "resource node" : "harvestable";
    public override string TypeName => RareState > 0 ? $"{Type.Name} T{Tier}.{RareState}" : $"{Type.Name} T{Tier}";

    /// <summary>A listed one is forgotten silently (never a Leave for list 39); one sent as event 40 is left, as official event-40 objects are.</summary>
    public override bool LeaveOnRange => !Listed;

    /// <summary>
    /// Nothing for a listed one (<see cref="Lists"/> carries it). An enchanted resource node: NewHarvestableObject (40, aty;
    /// SPEC §5.4) {0 id, 2 GameTimeStamp of creation (the map's; official: one stamp per session, a few minutes after the
    /// start), 5 type (sent also as 0, WOOD), 6 short -1 (no mob type: a resource), 7 tier, 8 the prefab position (bound as
    /// in list 39), 9 direction (left out at 0), 10 charges (sent also as 0), 11 rare state}; keys 1 and 3 (dead mob,
    /// owner) are corpse keys and left out.
    /// A corpse (<see cref="Corpse"/>; the 10 mob-derived ones of capture 002835, HARVEST.md §2): {0 id, 1 the
    /// dead mob's object id, 2 GameTimeStamp = the end of its loot protection, 3 Guid owner = the killer's character, 5
    /// type, 6 mob type (mobs.xml index + 16, NewMob key 1), 7 tier, 8 the death position, 9 the mob's facing, 10 charges, 11
    /// rare state}; 5, 10 and 11 go out also as 0, as captured (depleted corpses with 10 = 0, every corpse with 11 = 0).
    /// Keys 4 and 12 were never sent. The same for every viewer: the client compares key 3 with its own character itself.
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        if (Corpse is { } corpse)
        {
            yield return new NewHarvestableObjectEvent
            {
                c0k = Id,                                   // [0]
                c0l = corpse.Mob.Id,                        // [1] the dead mob
                c0m = corpse.ProtectedUntil,                // [2] GameTimeStamp: the kill + the loot protection
                c0n = corpse.Owner,                         // [3] the killer's character Guid
                c0q = corpse.Mob.Template.Id,               // [6] mob type (index + 16)
                c0r = (byte)Tier,                           // [7]
                c0s = Position,                             // [8] the death position, not on a grid
                c0t = Direction,                            // [9] the mob's facing
                Extra = new() { [5] = Type.Id, [10] = (short)Charges, [11] = RareState }, // [5] type, [10] charges, [11] rare state, zero included
            };
            yield break;
        }
        if (Listed) yield break;
        yield return new NewHarvestableObjectEvent
        {
            c0k = Id,                                   // [0]
            c0m = Map?.CreatedTicks ?? 0,               // [2] GameTimeStamp
            c0q = -1,                                   // [6] mob type: none
            c0r = (byte)Tier,                           // [7]
            c0s = Position,                             // [8]
            c0t = Direction,                            // [9] left out at 0
            c0v = RareState,                            // [11]
            Extra = new() { [5] = Type.Id, [10] = (short)Charges }, // [5] type, [10] charges, zero included
        };
    }

    /// <summary>
    /// HarvestableChangeState (46, ap5) with the charges and rare state as they are now: {0 harvestable, 1 short charges, 2
    /// byte rare state}. Keys 1 and 2 go out also when 0, as the official server sends them (the depleted tree 1709:
    /// {0: 1709, 1: 0, 2: 0}); the client sets cn2+0x78 / +0x90 from them and recomputes the gfx state (cn2.ajv 0x180b881d0).
    /// </summary>
    public HarvestableChangeStateEvent BuildChangeState() => new()
    {
        HarvestableId = Id,                                           // [0]
        Extra = new() { [1] = (short)Charges, [2] = RareState },      // [1] charges, [2] rare state, zero included
    };

    /// <summary>
    /// NewSimpleHarvestableObjectList (auy) for <paramref name="objects"/>, at most <see cref="ListSize"/> per event, in
    /// order: parallel arrays 0 ids, 1 type (harvestables.xml index), 2 tier, 3 positions (x, z flattened), 4 charges. All
    /// five keys are non-null and equally long: the client throws otherwise, and on a type it does not know (bng.abo null)
    /// or a tier outside the type's (it indexes tier − 1), which the spawner rules out.
    /// </summary>
    public static IEnumerable<NewSimpleHarvestableObjectListEvent> Lists(IReadOnlyList<Harvestable> objects)
    {
        for (var start = 0; start < objects.Count; start += ListSize)
        {
            var chunk = objects.Skip(start).Take(ListSize).ToArray();
            yield return new NewSimpleHarvestableObjectListEvent
            {
                ObjectIds = [.. chunk.Select(h => h.Id)],
                Types = [.. chunk.Select(h => h.Type.Id)],
                Tiers = [.. chunk.Select(h => (byte)h.Tier)],
                Positions = [.. chunk.Select(h => h.Position)],
                Charges = [.. chunk.Select(h => (short)h.Charges)],
            };
        }
    }
}

/// <summary>
/// What makes a <see cref="Harvestable"/> a killed mob's corpse (World/Corpses.cs): the mob (NewHarvestableObject key 1 is its
/// object id, key 6 its type), <see cref="Owner"/> = the killer's character Guid (key 3), <see cref="ProtectedUntil"/> = the
/// GameTimeStamp its loot protection ends (key 2), the killer's party at the kill (0 = none; not on the wire), and when it
/// decays (<see cref="World.Clock"/> time: the kill + the tier's decaytimeseconds).
/// </summary>
public sealed record HarvestableCorpse(Mob Mob, Guid Owner, int PartyId, long ProtectedUntil)
{
    /// <summary>
    /// Whether <paramref name="p"/> may harvest it at <paramref name="now"/> (GameTimeStamp ticks): anyone once the protection
    /// is over, before that the owner and the party the owner was in at the kill, by its current members (only its id is
    /// kept, so one who joins it later may too; as a silver pile, stageD party SPEC §6 [choice]).
    /// The client shows @HARVEST_ERROR_PROTECTED with the time left to the others by itself.
    /// </summary>
    public bool MayHarvest(Player p, long now) =>
        now >= ProtectedUntil || p.Character.Id == Owner || PartyId != 0 && p.World.Parties.Of(p.Character.Id)?.Id == PartyId;

    /// <summary><see cref="World.Clock"/> time the corpse is taken off its map.</summary>
    public TimeSpan DecayAt { get; init; }
}
