using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The personal islands, phase 1 of analysis/work/stageH/islands/SPEC.md (PVE-DESIGN step 14): the owned islands (in memory by Guid,
/// every change written at once to the island store, <see cref="IIslandStore"/>; none in hand-made test worlds), the merchant's list
/// (GetIslandInfos 218, §1.2), the instance "@ISLAND@&lt;guid&gt;" built from the world.xml template of the island's current level
/// (<see cref="WorldOf"/>, §0.3), its island management (§1.5), the Join keys 81/82, the way home of a character whose island is
/// gone (§1.6) and the free starter island of Islands:StarterIsland. The journeys there and back are BuyJourney's
/// (Handlers/World/JourneyHandler). Phase 2 (buying, upgrading, the showroom, favourites): Islands.Merchant.cs; phase 3 (farms, herb
/// gardens and their crops): Islands.Farming.cs. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Islands
{
    private readonly World world;
    private readonly IIslandStore? store;
    private readonly Dictionary<Guid, Island> _islands = [];

    public Islands(World world, IIslandStore? store)
    {
        this.world = world;
        this.store = store;
        if (store is null) return;
        foreach (var i in store.LoadIslands()) _islands[i.Id] = i;
        foreach (var b in store.LoadIslandBuildings().Where(b => _islands.ContainsKey(b.Island))) _buildings[b.Id] = b;
        foreach (var f in store.LoadFarmables().Where(f => _buildings.ContainsKey(f.Building))) _crops[f.Id] = f;
        foreach (var l in store.LoadLabourers().Where(l => _buildings.ContainsKey(l.Building))) _labourers[l.Id] = l;
        if (_islands.Count > 0) Log.Info("world", $"{_islands.Count} island(s) loaded, {_buildings.Count} building(s), {_crops.Count} crop(s) and furniture, {_labourers.Count} labourer(s)");
    }

    /// <summary>Islands:Enabled.</summary>
    public bool Enabled => world.Config.IslandsEnabled;

    public IEnumerable<Island> All => _islands.Values;

    public Island? Find(Guid id) => _islands.GetValueOrDefault(id);

    /// <summary>The island of instance id <paramref name="cluster"/>; null when it is none or unknown.</summary>
    public Island? Find(string? cluster) => IslandData.GuidOf(cluster) is { } id ? _islands.GetValueOrDefault(id) : null;

    /// <summary>The islands of <paramref name="owner"/>, oldest first (the official answer lists the personal island first).</summary>
    public IReadOnlyList<Island> Of(Guid owner) => [.. _islands.Values.Where(i => i.Owner == owner).OrderBy(i => i.Bought)];

    /// <summary>
    /// The island types (worldsettings.xml), read now if they are not yet (a small file; a login onto an island before any city with an
    /// island merchant was resolved needs them for SelectCharacter key 2); null without cluster data or when the file is unreadable.
    /// </summary>
    public IslandData? Data
    {
        get
        {
            if (world.Clusters is not { } store) return null;
            try { return store.Islands; }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                Log.Warn("world", $"island types not read: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// The world.xml template a map of <paramref name="cluster"/> is built from (ChangeCluster key 1, SelectCharacter key 2): the cluster of
    /// the island's current level (§1.6); null when it is no live island (unknown, islands off, no data).
    /// </summary>
    public string? WorldOf(string cluster) =>
        Enabled && Find(cluster) is { } i && Data?.TypeAt(i.Type)?.LevelAt(i.Level) is { } level ? level.Cluster : null;

    /// <summary>
    /// "&lt;islandaccesspoint id&gt;@&lt;city&gt;" of <paramref name="type"/>'s city (world.xml, one per island city); null when world.xml is
    /// not read yet or the city has none.
    /// </summary>
    public string? AccessPointOf(IslandType type) =>
        world.Clusters?.WorldIfLoaded?.Find(type.City)?.IslandAccessPoints.FirstOrDefault() is { } ap ? $"{ap.Id:D}@{type.City}" : null;

    /// <summary>
    /// A new personal island of <paramref name="type"/> at <paramref name="level"/> for <paramref name="owner"/>, saved; null with the reason
    /// when refused: one island per character and type (§1.1), a personal type, a level the type has, its city's access point known.
    /// </summary>
    public Island? Give(Character owner, IslandType type, int level, out string why)
    {
        why = "";
        if (type.Kind != 0) why = $"{type.Name} is a guild island";
        else if (_islands.Values.Any(i => i.Owner == owner.Id && i.Type == type.Id)) why = $"'{owner.Name}' owns a {type.Name} already";
        else if (type.LevelAt(level) is null) why = $"{type.Name} has no level {level} (0-{type.MaxLevel})";
        else if (AccessPointOf(type) is not { } ap) why = $"no island access point of {type.City} in world.xml (not read yet?)";
        else
        {
            var island = new Island(Guid.NewGuid(), owner.Id, owner.Name, type.Id, ap) { Level = level, Bought = world.GameNow };
            _islands[island.Id] = island;
            store?.SaveIsland(island);
            Log.Info("world", $"'{owner.Name}' gets {island} ({type.Name})");
            if (world.Config.IslandsStarterBuildings) AddStarterBuildings(island);
            return island;
        }
        return null;
    }

    /// <summary>
    /// A GM's level change (.island level): at once, without the 130 s upgrade time. Players on the island go to its city first and
    /// its map is dropped (<see cref="Evict"/>), so the next visit loads the new template (§2.3).
    /// </summary>
    public void SetLevel(Island island, int level)
    {
        Evict(island, "level");
        island.Level = level;
        store?.SaveIsland(island);
        Log.Info("world", $"{island}: level set");
    }

    /// <summary>
    /// Every player on <paramref name="island"/> goes to its city's travel point (a cluster change; first <paramref name="message"/> as a
    /// system line, a localisation tag), then its map is unloaded.
    /// </summary>
    public void Evict(Island island, string why, string? message = null)
    {
        if (world.Maps.Find(island.Cluster) is not { } map) return;
        foreach (var p in map.Players.Values.ToList())
        {
            if (message is not null) p.Session.Send(new SystemMessageEvent { d0p = message });
            if (p.Session.Status == SessionStatus.InWorld && p.Session.Player == p) p.Session.Teleport(HomeOf(island, p.Character), $"island:{island.Id}:{why}");
            else world.Maps.Remove(p, world.Clock.Now);
        }
        world.Maps.Unload(map, force: true);
    }

    /// <summary>A character's island and every row of it go with the character (the database row follows by ON DELETE CASCADE).</summary>
    public void CharacterDeleted(Guid character)
    {
        foreach (var i in _islands.Values.Where(i => i.Owner == character).ToList())
        {
            _islands.Remove(i.Id);
            foreach (var b in _buildings.Values.Where(b => b.Island == i.Id).ToList()) _buildings.Remove(b.Id);
            foreach (var f in _crops.Values.Where(f => f.Island == i.Id).ToList()) _crops.Remove(f.Id);
            foreach (var l in _labourers.Values.Where(l => l.Island == i.Id).ToList()) _labourers.Remove(l.Id);
        }
    }

    /// <summary>
    /// Where a character leaving <paramref name="island"/> without a journey arrives (§1.6): the travel point of its access point in its city
    /// (the spawn point of the resolved city, else the world.xml travel point itself); without the island the character's last city
    /// (Join key 118), else 0000.
    /// </summary>
    public Arrival HomeOf(Island? island, Character c)
    {
        var city = island?.City ?? CityOf(c.LastCity is { Length: > 0 } last ? last : "0000");
        var info = world.Clusters?.WorldIfLoaded?.Find(city);
        Guid? travelpoint = island is null ? null : info?.IslandAccessPoints.FirstOrDefault(a => a.Id == island.AccessPointId)?.Travelpoint;
        var spawn = world.CachedCluster(city)?.Travelpoints.FirstOrDefault(t => travelpoint is null || t.Guid == travelpoint)?.SpawnPosition;
        var at = spawn ?? info?.Travelpoints.FirstOrDefault(t => travelpoint is null || t.Id == travelpoint)?.Position ?? Vector2.Zero;
        return new Arrival(city, at, c.Direction);
    }

    /// <summary>
    /// The city a last city stands for: itself when world.xml gives it a travel point, else (a city interior such as 1001 the Lymhurst bank,
    /// PLAYERCITY_* too but with exits only) the first cluster its exits lead to that has one; unchanged without world.xml.
    /// </summary>
    public string CityOf(string cluster)
    {
        if (world.Clusters?.WorldIfLoaded is not { } w || w.Find(cluster) is not { } info || info.Travelpoints.Count > 0) return cluster;
        return info.Exits.Select(e => w.Find(e.TargetCluster)).FirstOrDefault(t => t is { Travelpoints.Count: > 0 })?.Id ?? cluster;
    }

    /// <summary>
    /// SelectCharacter (§1.6): a character saved on an island that is gone (unknown, islands off, no template) goes to <see cref="HomeOf"/>,
    /// saved; true when it was moved.
    /// </summary>
    public bool ReturnIfGone(Character c)
    {
        if (!IslandData.IsInstance(c.Cluster) || WorldOf(c.Cluster) is not null) return false;
        var to = HomeOf(Find(c.Cluster), c); // the row (access point, city) is kept with Islands:Enabled false too
        (c.Cluster, c.PositionX, c.PositionY) = (to.Cluster, to.Position.X, to.Position.Y);
        c.PendingTransfer = null;
        world.Saves.SaveNow(c);
        Log.Info("world", $"'{c.Name}' was on an island that is gone: back in {to.Cluster}");
        return true;
    }

    /// <summary>The arrival on an island (§1.3): its template's travel point (FASTTRAVEL_POINT_ISLAND) + spawnpos, (180, 56) on levels a-c.</summary>
    public static Vector2? ArrivalOf(ClusterTemplate template) => template.Travelpoints.FirstOrDefault()?.SpawnPosition;

    /// <summary>
    /// .island go: <paramref name="p"/> onto <paramref name="island"/> as after a journey (the push {0 id, 1 template, 2 owner name}); false
    /// when the template is not resolved yet (it is asked for, try again).
    /// </summary>
    public bool Send(Player p, Island island, string via)
    {
        if (WorldOf(island.Cluster) is not { } template) return false;
        if (world.CachedCluster(island.Cluster) is not { } resolved || ArrivalOf(resolved) is not { } at)
        {
            world.PrefetchCluster(island.Cluster);
            return false;
        }
        p.Session.Teleport(new Arrival(island.Cluster, at, p.Facing), via, template, island.OwnerName);
        return true;
    }

    /// <summary>The FlaggingStatus of a city's personal islands (GetIslandInfos key 13, §1.2 [cap: Lymhurst 2]; Q4): the city's faction.</summary>
    public static byte CityFlag(string city) => city switch
    {
        "3004" => 1, "1000" => 2, "2000" => 3, "4000" => 4, "0000" => 5, "3003" => 6, _ => 0,
    };

    /// <summary>
    /// GetIslandInfos (op 218, response afw, client ctp.af_82 0x180995890; SPEC §1.2): the player's islands as parallel arrays, every row
    /// key 0-8 and 10-22 present with one entry per island (a missing or short one makes the client throw), key 9 left out (only read
    /// for guild-owned rows). No island (or islands off): the empty answer of before (FillEmpty, no rows). With Islands:StarterIsland a
    /// character without a personal island first gets one of the city whose merchant it stands at (<see cref="GrantStarter"/>).
    /// </summary>
    public GetIslandInfosResponse Infos(Player p)
    {
        if (!Enabled) return new GetIslandInfosResponse().FillEmpty();
        GrantStarter(p);
        PrefetchShowroom(p);
        var mine = Of(p.Character.Id);
        if (mine.Count == 0) return new GetIslandInfosResponse().FillEmpty();
        var n = mine.Count;
        var ids = mine.Select(i => i.Id).ToArray();
        return new GetIslandInfosResponse
        {
            azq = ids,                                         // [0] island Guid (= the instance's)
            azr = [.. mine.Select(i => i.AccessPoint)],        // [1] "<access point>@<city>"
            azs = [.. mine.Select(i => i.Type)],               // [2] island type (bg3.abo)
            azt = [.. mine.Select(i => i.OwnerName)],          // [3] owner name
            azu = [.. mine.Select(i => i.Owner)],              // [4] owner = the character (Join key 1): "my islands"
            azv = ids,                                         // [5] the level rows (key 6-8)
            azw = [.. mine.Select(i => i.Level)],              // [6] level 0-5
            azx = [.. mine.Select(i => i.LevelChanged)],       // [7] last level change (0 = never; "upgrading" for 130 s after)
            azy = [.. mine.Select(i => i.Bought)],             // [8] purchase
            az0 = new int[n], az1 = new int[n], az2 = new int[n], // [10-12] captured 0
            az3 = [.. mine.Select(i => CityFlag(i.City))],    // [13] FlaggingStatus (the personal Lymhurst island: 2)
            az4 = new int[n], az5 = new int[n], az6 = new int[n], az7 = new int[n], az8 = new int[n], // [14-18] guild territory data: 0
            az9 = new float[n], a0a = new float[n],            // [19-20] 0
            a0b = [.. mine.Select(i => i.Favorite)],           // [21] favourite
            a0c = [.. mine.Select(i => i.Hidden)],             // [22] hidden
        };
    }

    /// <summary>
    /// Islands:StarterIsland: <paramref name="p"/>, with an island merchant (NewIslandAccessPoint) in view on the map of a city that has a
    /// personal island type, owns no personal island: it gets one there, free, at Islands:StarterLevel (the merchant then offers "Visit").
    /// A PvE choice beyond the SPEC: it is not Islands:FreeIslands (SPEC "PvE direction" 4, purchases at 0 silver, phase 2).
    /// </summary>
    public void GrantStarter(Player p)
    {
        if (!world.Config.IslandsStarterIsland || p.Map is not { } map || !map.Objects.Values.OfType<IslandAccessPoint>().Any(p.Visibility.Knows)) return; // the merchant in view
        if (Data is not { } data || data.PersonalOf(map.Cluster) is not { } type) return;
        if (Of(p.Character.Id).Any(i => data.TypeAt(i.Type)?.Kind == 0)) return;
        var level = Math.Min(world.Config.IslandsStarterLevel, type.MaxLevel);
        if (Give(p.Character, type, level, out var why) is null) Log.Warn("world", $"starter island for '{p.Character.Name}' not given: {why}");
    }

    /// <summary>
    /// The Join keys of the islands: on an island (§1.5) key 81 its access point, key 82 the island Guid (both left out elsewhere). Key 91,
    /// the premium days the merchant checks (§2.1: "Buy" only at 7 or more), is <see cref="Premium.JoinKeys"/>'s (stageJ focus-lp SPEC §5.2).
    /// </summary>
    public void JoinKeys(Character c, JoinResponse r)
    {
        if (!Enabled) return;
        if (Find(c.Cluster) is not { } island) return;
        r.baj = island.AccessPoint; // [81] → ctp+0x398
        r.bak = island.Id;          // [82] → ctp+0x388
    }

    /// <summary>
    /// The objects of an island instance's map: its island management (§1.5), one per islandmanagement tile of the template, owned by the
    /// island's owner; then its player buildings, each followed by its crops (§3, from the store).
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!Enabled || Find(map.Cluster) is not { } island) yield break;
        foreach (var m in template.IslandManagements) yield return new IslandManagement(world.ObjectIds.Next(), m, island, GuildOf);
        foreach (var o in FarmObjects(map, island)) yield return o;
    }

    /// <summary>The guild of a character for the island management (Join key 56 and its name); null without one.</summary>
    private (Guid Id, string Name)? GuildOf(Guid character) => world.Guilds.Of(character) is { } g ? (g.Id, g.Name) : null;

    /// <summary>
    /// The location of the vault a bank on <paramref name="map"/> opens (§0.2): on an island the city bank of world.xml bankgroup
    /// ("0e860295-…@1001" → 1001: the island bank opens the home city's vault); elsewhere the map's own cluster.
    /// </summary>
    public static string VaultLocation(Map map) =>
        IslandData.IsInstance(map.Cluster) && map.Template?.Info.Attrs.GetValueOrDefault("bankgroup") is { } group && group.IndexOf('@') is > 0 and var at
            ? group[(at + 1)..] : map.Cluster;

    /// <summary>.island list: one line per island.</summary>
    public IEnumerable<string> Describe(Guid? owner = null) =>
        _islands.Values.Where(i => owner is null || i.Owner == owner).OrderBy(i => i.Bought).Select(i =>
            $"{i} {(Data?.TypeAt(i.Type)?.Name ?? "?")} → {WorldOf(i.Cluster) ?? "-"}{(world.Maps.Find(i.Cluster) is { } m ? $", {m.Players.Count} player(s) on it" : "")}");
}
