using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The open world's contested treasures (public issue #19, analysis/work/coffers/SPEC.md, "SPEC" here), with World:TreasureCoffers:
/// <list type="bullet">
/// <item>Spots (SPEC §2.1): the COFFER spots are the clusters' TreasureSpawnpointCoffer tiles (the dynamic mob camp templates), the CHEST spots
/// the TreasureSpawnpointLootchest prefabs the resource placement puts on resource nodes (<see cref="ClusterTemplate.TreasureChestPrefab"/>).</item>
/// <item>The draw (SPEC §2.2): treasuredistribution.xml's &lt;Distribute&gt; of the subtype for the cluster's continent, world.xml type and tier.
/// It has maxslotsused slots; at the fill and whenever a slot is free again, the slot draws after uniform(activationtimemin, max) ÷ Rates:ChestRespawn
/// (TreasureCoffers:RespawnMinutes n: n minutes) with activationchance: an entry by weight (a &lt;None&gt; or a size left out of TreasureCoffers:Sizes,
/// such as the faction chests the yellow and red zones also draw, places nothing this time), its LootChestSpawnpoint's chest by weight on a free
/// spot (no treasure on it, its positioncooldown since the last one over). The first draw happens at the fill, standing for the long-running
/// official cluster [assume, SPEC §4].</item>
/// <item>The treasure (SPEC §1, §3): LockedByTimer (ChestState 1) for its protectiontimeseconds from the spawn (the client counts key 7 down), then
/// Unlocked (UpdateLootChest); opened by the client's channelled slot-25 cast (alwaysrequireschanneling, <see cref="FakeSlotCasts"/>), its loot rolled
/// for the cluster's tier (<see cref="RandomDungeons.OpenChest"/>); gone despawntimeseconds (300 s) after the opening, or maxlifetime (86 400 s)
/// unopened; then the slot draws again.</item>
/// <item>The PvP circle (SPEC §3), under Pvp:Mode Official: NewLootChest key 27 true (the client's barrier of pvpzoneradius, 30 m, the elite chests
/// 40 m), and a player within the radius of a standing treasure is unrestricted: UnrestrictedPvpZoneUpdate 308 {0 the player, 1 1} to it and every
/// client that knows it (0 when it leaves), and two such players outside one party are Hostile (<see cref="Unrestricted"/>, Pvp.Of). The Mists'
/// treasure chests get the same circle (<see cref="Contest"/>).</item>
/// </list>
/// <item>The map markers (public issue #23): the standing treasures in LocalTreasuresUpdate 284 at every Join and to the map's players at every
/// change (<see cref="LocalTreasures"/>), the veteran and elite chests also in the world map's ClusterInfoUpdate (<see cref="FillWorldMap"/>).</item>
/// Not done (SPEC §5): the faction chests and the Red Zone Event, the activityquotient. The state lives with the map: an unloaded map takes it along, the next one draws afresh. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class TreasureCoffers(World world)
{
    /// <summary>ChestState LockedByTimer (the protection countdown).</summary>
    public const byte LockedByTimer = 1;

    /// <summary>UnrestrictedPvpZoneUpdate key 1 inside a circle (bgi 1; 0 outside) [assume: SPEC §3.3].</summary>
    public const byte InsideStatus = 1;

    /// <summary>A treasure spot of a map: its subtype (COFFER, CHEST), where, the treasure on it and when it may get the next one.</summary>
    public sealed class Spot(string subtype, Vector2 position, float direction)
    {
        public string Subtype { get; } = subtype;
        public Vector2 Position { get; } = position;
        public float Direction { get; } = direction;
        public LootChest? Chest { get; internal set; }

        /// <summary>The end of its positioncooldown (world clock).</summary>
        internal TimeSpan FreeAt { get; set; }
    }

    /// <summary>One of a subtype's maxslotsused: the treasure it placed, or none (its next draw due).</summary>
    public sealed class Slot(string subtype, TreasureDraw draw)
    {
        public string Subtype { get; } = subtype;
        public TreasureDraw Draw { get; } = draw;
        public LootChest? Chest { get; internal set; }

        /// <summary>Whether its next draw is scheduled.</summary>
        internal bool Waiting { get; set; }
    }

    private sealed class MapTreasures
    {
        public readonly List<Spot> Spots = [];
        public readonly List<Slot> Slots = [];

        /// <summary>The standing chests with their PvP circle up (the treasures, the Mists' treasure chests).</summary>
        public readonly List<LootChest> Contested = [];

        /// <summary>The players inside a circle now, with the players their status was told to.</summary>
        public readonly Dictionary<Player, HashSet<Player>> Inside = [];

        /// <summary>The treasures this system made and that stand (the slots' and the GM's), for their despawn after the opening.</summary>
        public readonly HashSet<LootChest> Made = [];
        public int Tier;

        /// <summary>The map's template (the map has it only after its fill: the treasures of the fill need its biome and PvP rules).</summary>
        public ClusterTemplate? Template;

        /// <summary>Issue #23: the treasures of the cluster's LocalTreasuresUpdate (284), in the order they came.</summary>
        public readonly Dictionary<LootChest, Listing> Listed = [];

        /// <summary>Whether <see cref="Listed"/> changed since the last 284 to the map's players.</summary>
        public bool ListChanged;
    }

    /// <summary>A listed treasure: its Guid (284 key 1, NewLootChest key 20), its index in the client's list (key 2), its subtype (key 8) and definition.</summary>
    public sealed record Listing(Guid Guid, int Index, string Subtype, TreasureLootchest Def);

    private readonly ConditionalWeakTable<Map, MapTreasures> _maps = new();

    private ServerConfig Config => world.Config;

    /// <summary>The treasure spots of <paramref name="map"/>.</summary>
    public IReadOnlyList<Spot> SpotsOf(Map map) => _maps.TryGetValue(map, out var m) ? m.Spots : [];

    /// <summary>The treasure slots of <paramref name="map"/>.</summary>
    public IReadOnlyList<Slot> SlotsOf(Map map) => _maps.TryGetValue(map, out var m) ? m.Slots : [];

    /// <summary>Whether <paramref name="p"/> stands in a treasure's PvP circle (its last tick).</summary>
    public bool IsInside(Player p) => p.Map is { } map && _maps.TryGetValue(map, out var m) && m.Inside.ContainsKey(p);

    /// <summary>The size of a lootchests.xml chesttype (TreasureCoffers:Sizes); null for any other (faction chests, the castle and Mists treasures).</summary>
    public static string? SizeOf(string chestType) => chestType switch
    {
        "treasurecoffer" => "coffer",
        "treasurechestsmall" => "solo",
        "treasurechestmedium" => "veteran",
        "treasurechestlarge" => "elite",
        _ => null,
    };

    /// <summary>
    /// At an open-world map's fill (MapSpawner, after the mobs): its spots and slots, and the treasures the first draws place, not yet on the map.
    /// Nothing with World:TreasureCoffers off, on an instance (dungeon, expedition, hellgate, Mists, …), or before the data is read.
    /// </summary>
    public List<LootChest> Populate(Map map, ClusterTemplate template)
    {
        var made = new List<LootChest>();
        if (!Config.TreasureCoffers || world.Clusters is not { } store) return made;
        if (ClusterNameInfo.Parse(template.Info.File) is not { Type: 0 or 20 } cni || !ClusterNameInfo.ContinentNames.TryGetValue(cni.Continent, out var continent)
            || cni.Continent is 0 or 4 || DungeonLayout.IsInstance(map.Cluster) || MistsIds.IsInstance(map.Cluster))
            return made;
        if (store.TreasuresIfLoaded is null || store.LootChestsIfLoaded is null)
        {
            if (!Config.TreasureTestAlways) return made; // the template reads both with a treasure spot: none here
            // TestAlways on a cluster without treasure spots: the files are read now
            try { _ = store.Treasures; _ = store.LootChests; }
            catch (Exception e) when (e is InvalidDataException or IOException) { Combat.LogOnce("treasures-data", $"treasures: {e.Message}"); return made; }
        }
        var data = store.Treasures;
        var tier = template.Name.Tier ?? cni.Tier;
        var m = new MapTreasures { Tier = tier, Template = template };
        foreach (var s in template.MistsSpots.Where(s => s.Kind is "treasure:COFFER" or "treasure:CHEST"))
            m.Spots.Add(new Spot(s.Kind["treasure:".Length..], s.Position, s.Direction));
        foreach (var p in template.Resources?.Placements.Where(p => p.Name == ClusterTemplate.TreasureChestPrefab) ?? [])
            m.Spots.Add(new Spot("CHEST", p.Position, p.NodeDirection));
        foreach (var subtype in TreasureDistributionData.Subtypes)
        {
            var draw = data.DrawFor(subtype, continent, template.Info.Type, tier);
            if (Config.TreasureTestAlways)
            {
                draw ??= data.DrawFor(subtype, "Outlands", "", Math.Clamp(tier, 5, 8));
                if (draw is not null && !m.Spots.Any(s => s.Subtype == subtype))
                    foreach (var at in template.MobSpawnPoints.Select(p => p.Position).Distinct().Take(20)) m.Spots.Add(new Spot(subtype, at, 0));
            }
            if (draw is null || !m.Spots.Any(s => s.Subtype == subtype)) continue;
            for (var i = 0; i < Math.Max(1, draw.Slots); i++) m.Slots.Add(new Slot(subtype, draw));
        }
        if (m.Slots.Count == 0) return made;
        _maps.AddOrUpdate(map, m);
        foreach (var slot in m.Slots)
            if (Draw(map, m, slot) is { } chest) made.Add(chest);
            else Schedule(map, m, slot);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"map {map.Cluster}: {m.Spots.Count(s => s.Subtype == "COFFER")} coffer and {m.Spots.Count(s => s.Subtype == "CHEST")} treasure chest spot(s), {m.Slots.Count} slot(s) ({continent} {template.Info.Type} T{tier}); {made.Count} treasure(s) now"));
        return made;
    }

    /// <summary>A slot's next draw, uniform(activationtimemin, max) ÷ Rates:ChestRespawn from now (TreasureCoffers:RespawnMinutes: that many minutes).</summary>
    private void Schedule(Map map, MapTreasures m, Slot slot)
    {
        if (slot.Waiting) return;
        slot.Waiting = true;
        var seconds = Config.TreasureRespawnMinutes > 0 ? Config.TreasureRespawnMinutes * 60
            : slot.Draw.MinSeconds + Math.Max(0, slot.Draw.MaxSeconds - slot.Draw.MinSeconds) * map.Random.NextDouble();
        map.Timers.At(world.Now + TimeSpan.FromSeconds(Math.Max(1, seconds) / world.Rates.ChestRespawn), () =>
        {
            slot.Waiting = false;
            if (map.Unloaded || map.Grid is null || !_maps.TryGetValue(map, out var live) || live != m || slot.Chest is not null) return;
            if (Draw(map, m, slot) is { } chest)
            {
                map.AddObject(chest);
                Log.Info("world", $"map {map.Cluster}: treasure {chest} spawned");
            }
            else Schedule(map, m, slot);
        });
    }

    /// <summary>
    /// A slot's draw (SPEC §2.2): activationchance (always under TestAlways), an entry by weight with &lt;None&gt;, a size of TreasureCoffers:Sizes,
    /// a free spot; then its treasure (<see cref="Make"/>), not yet on the map. Null = nothing this time.
    /// </summary>
    private LootChest? Draw(Map map, MapTreasures m, Slot slot)
    {
        if (world.Clusters?.TreasuresIfLoaded is not { } data || world.Clusters.LootChestsIfLoaded is not { } chests) return null;
        var rng = map.Random;
        if (!Config.TreasureTestAlways && rng.NextDouble() >= slot.Draw.Chance) return null;
        var options = slot.Draw.Entries.Select(e => ((string?)e.Name, e.Weight)).ToList();
        if (slot.Draw.NoneWeight > 0 && !Config.TreasureTestAlways) options.Add((null, slot.Draw.NoneWeight));
        if (options.Count == 0 || DungeonGenerator.Pick(rng, options) is not { } name) return null;
        if (!data.Lootchests.TryGetValue(name, out var kind) || !chests.Spawnpoints.TryGetValue(kind.Spawnpoint, out var behaviour))
        {
            Combat.LogOnce("treasure:" + name, $"treasure {name} has no TreasureLootchest or LootChestSpawnpoint in the client data: not spawned");
            return null;
        }
        var templates = behaviour.Chests.Select(c => chests.Chests.GetValueOrDefault(c.Chest)).OfType<LootChestTemplate>().ToList();
        if (templates.Count == 0 || SizeOf(templates[0].ChestType) is not { } size || !Config.TreasureSizes.Contains(size))
        {
            Combat.LogOnce("treasure-size:" + name, $"treasure {name} ({(templates.Count > 0 ? templates[0].ChestType : "no chest")}) is not one of TreasureCoffers:Sizes: not spawned (the faction chests are not run)");
            return null;
        }
        var free = m.Spots.Where(s => s.Subtype == slot.Subtype && s.Chest is null && s.FreeAt <= world.Now).ToList();
        if (free.Count == 0) return null;
        var spot = free[rng.Next(free.Count)];
        if (Make(map, m, spot.Position, spot.Direction, behaviour, kind, slot.Subtype) is not { } chest) return null;
        (spot.Chest, slot.Chest) = (chest, chest);
        return chest;
    }

    /// <summary>
    /// A treasure of <paramref name="behaviour"/> at <paramref name="at"/>: its chest and rare state by weight, the tile of the cluster's biome
    /// and sub-biome, LockedByTimer for its protectiontimeseconds (Unlocked without), its PvP circle (<see cref="Contest"/>) and its maxlifetime.
    /// Null without a tile. Listed (issue #23, <see cref="List"/>) as <paramref name="def"/> of <paramref name="subtype"/>; <paramref name="kind"/>
    /// also gives its maxlifetime.
    /// </summary>
    private LootChest? Make(Map map, MapTreasures m, Vector2 at, float direction, ChestSpawnBehaviour behaviour, TreasureLootchest? kind, string subtype,
        TreasureLootchest? def = null)
    {
        var data = world.Clusters!.LootChestsIfLoaded!;
        var choices = behaviour.Chests.Where(c => c.Weight > 0 && data.Chests.ContainsKey(c.Chest)).ToList();
        if (choices.Count == 0) return null;
        var template = data.Chests[DungeonGenerator.Pick(map.Random, [.. choices.Select(c => (c.Chest, c.Weight))])];
        var cluster = m.Template ?? map.Template;
        var (biome, folder) = (cluster?.Name.Biome, cluster?.Name.TemplateFolder ?? "NONE");
        var tile = template.TileFor(biome, folder);
        var states = template.States.Where(s => s.Weight > 0).ToList();
        if (tile.Length == 0 || states.Count == 0)
        {
            Combat.LogOnce("chesttile:" + template.Name, $"treasure {template.Name} has no tile for {biome}/{folder} or no rare state: not spawned");
            return null;
        }
        var point = new LootChestSpawn
        {
            Key = new SpawnKey(map.Cluster, "treasure", map.CustomSpawns++), Tile = "", Position = at, Direction = direction,
            Component = new XElement("lootchestspawnpoint", new XAttribute("spawnbehaviour", behaviour.Name)), SpawnBehaviour = behaviour.Name, Behaviour = behaviour,
        };
        var protect = template.ProtectionSeconds > 0;
        var chest = new LootChest(world.ObjectIds.Next(), point, template, DungeonGenerator.Pick(map.Random, [.. states.Select(s => (s, s.Weight))]), tile,
            protect ? LockedByTimer : (byte)6, world.GameNow)
        {
            MinimapDisplay = behaviour.MinimapDisplay, LootTier = m.Tier,
        };
        if (protect)
            map.Timers.At(world.Now + TimeSpan.FromSeconds(template.ProtectionSeconds), () =>
            {
                if (chest.Map != map || chest.LockState != LockedByTimer) return; // gone, or unlocked by a GM
                chest.SetState(6, world.GameNow);
                foreach (var o in map.ObserversOf(chest)) o.Session.Send(chest.Update(o));
                Log.Info("world", $"treasure {chest}: its protection is over");
            });
        m.Made.Add(chest);
        if (kind is { MaxLifetimeSeconds: > 0 })
            map.Timers.At(world.Now + TimeSpan.FromSeconds(kind.MaxLifetimeSeconds), () =>
            {
                if (chest.Map == map && !chest.Rolled) Combat.RemoveBag(chest, "its maxlifetime is up unopened");
            });
        ContestIn(map, chest, m);
        List(m, chest, def ?? kind, subtype);
        return chest;
    }

    /// <summary>
    /// Issue #23: <paramref name="chest"/> into the cluster's LocalTreasuresUpdate as <paramref name="def"/> (a &lt;TreasureLootchest&gt;) of
    /// <paramref name="subtype"/>, before it is announced (NewLootChest key 20). Not listed (logged once) without a definition or with one the
    /// client's list lacks: cjm.a(g7s, short) 0x180AB73C0 would drop it with "TreasureSpawns: invalid treasure".
    /// </summary>
    private void List(MapTreasures m, LootChest chest, TreasureLootchest? def, string subtype)
    {
        var index = def is null ? 0 : world.Clusters?.TreasuresIfLoaded?.ClientIndex("TreasureLootchest", def.Name) ?? 0;
        if (def is null || index <= 1)
        {
            Combat.LogOnce("treasure-list:" + (def?.Name ?? chest.SpawnpointName), $"treasure {def?.Name ?? chest.SpawnpointName}: no treasure definition in the client's list, not on the map");
            return;
        }
        var guid = Guid.NewGuid();
        chest.TreasureGuid = guid;
        m.Listed[chest] = new Listing(guid, index, subtype, def);
        m.ListChanged = true;
    }

    /// <summary>
    /// LocalTreasuresUpdate (284, aru) of <paramref name="map"/> (issue #23): the cluster's standing, unopened treasures, parallel arrays. The client
    /// (cjm.a(g7s, short) 0x180AB73C0) replaces its whole list with each one (new dictionaries by key 4 and key 1, then its bk event: fwf.d
    /// 0x181BEA7A0 adds a minimap label per Guid of the current cluster and removes the labels of the Guids gone), so every 284 is the full
    /// list. Key 0 is not read and not sent (captures 002835, 103829, 113433, 114814: keys 1-8 only, empty arrays without a treasure, at every
    /// Join); a null array would throw, so the empty ones go too. Key 1 Guid, 2 the index in the client's treasure list (<see
    /// cref="TreasureDistributionData.ClientIndex"/>), 3 enchantment 0, 4 the id (cjl.b; the chest object's [assume]), 5 position, 6 the moment it
    /// becomes openable (cjl.f: the label's countdown target for a TreasureLootchest, TreasureSpawnMapLabel.UpdateTime), 7 its despawn (cjl.g,
    /// the target for the mob-carried types) or 0 when it has no lifetime, 8 the subtype.
    /// <para>
    /// Keys 6/7 used to be "now" and 0, i.e. already expired, which hid the countdown the moment it arrived. Note the client also hides it unless
    /// the treasure definition sets <c>showminimaptimer</c> (bf1.cs:295), which no &lt;treasure&gt; of this build does — only the two faction-banner
    /// &lt;TreasureMob&gt;s. So a lootchest shows an icon that changes with its state, not a timer; the times are still sent correctly for the types
    /// that can show one, and for the world map's protection countdown.
    /// </para>
    /// </summary>
    public LocalTreasuresUpdateEvent LocalTreasures(Map? map)
    {
        var listed = map is not null && _maps.TryGetValue(map, out var m) ? m.Listed.Where(kv => Standing(map, kv.Key)).ToList() : [];
        // When the chest can be opened: its protection runs from the state that locked it, the same value FillWorldMap sends as key 54.
        static long Openable(LootChest c) => c.LockState == LockedByTimer
            ? c.StateTicks + TimeSpan.FromSeconds(c.Template.ProtectionSeconds).Ticks
            : c.StateTicks;
        // When it despawns unopened: its maxlifetime runs from its spawn, which is the state it was listed with (0 when it has none).
        static long Despawn(Listing l, LootChest c) => l.Def is { MaxLifetimeSeconds: > 0 } d
            ? c.StateTicks + TimeSpan.FromSeconds(d.MaxLifetimeSeconds).Ticks
            : 0;
        return new LocalTreasuresUpdateEvent
        {
            cnq = [.. listed.Select(kv => kv.Value.Guid)],
            cnr = [.. listed.Select(kv => kv.Value.Index)],
            cns = new int[listed.Count],
            cnt = [.. listed.Select(kv => kv.Key.Id)],
            cnu = [.. listed.Select(kv => kv.Key.Position)],
            cnv = [.. listed.Select(kv => Openable(kv.Key))],
            cnw = [.. listed.Select(kv => Despawn(kv.Value, kv.Key))],
            cnx = [.. listed.Select(kv => kv.Value.Subtype)],
        };
    }

    /// <summary>A listed treasure still in the list: on its map and not opened (an opened one waits for its despawn, no longer a treasure [assume]).</summary>
    private static bool Standing(Map map, LootChest chest) => chest.Map == map && map.Objects.ContainsKey(chest.Id) && !chest.Rolled;

    /// <summary>
    /// ClusterInfoUpdate (139) keys 51-56 of <paramref name="cluster"/> for a viewer in <paramref name="from"/> (issue #23): the world-map
    /// markers. WorldmapCluster.UpdateTreasureSpawns 0x181BE3940 reads them parallel: 51 Guid, 52 the index in the client's treasure list (a
    /// TreasureLootchest), 53 position (cluster coordinates ÷ 100 on the map), 54 protected until and 55 a mob's lifetime
    /// (WorldmapTreasureSpawn.InitMarker), 56 rarity; the marker is WorldmapResources.GetTreasureSpawnMarker of the definition's
    /// worldmaptreasure (none: an error line, so only those that have one: the veteran and elite chests). Shown to a viewer at most
    /// worldmaprange cluster exits away (veteran 1, elite 2) [assume: the client does not read worldmaprange].
    /// </summary>
    public void FillWorldMap(ClusterInfoUpdateEvent e, string cluster, string? from)
    {
        if (from is null || world.Maps.Find(cluster) is not { } map || !_maps.TryGetValue(map, out var m) || m.Listed.Count == 0
            || world.Clusters?.WorldIfLoaded is not { } info) return;
        var shown = m.Listed.Where(kv => kv.Value.Def is { WorldMapTreasure.Length: > 0, WorldMapRange: >= 0 } && Standing(map, kv.Key)
            && Hops(info, cluster, from, kv.Value.Def.WorldMapRange)).ToList();
        if (shown.Count == 0) return;
        e.bzo = [.. shown.Select(kv => kv.Value.Guid)];
        e.bzp = [.. shown.Select(kv => kv.Value.Index)];
        e.bzq = [.. shown.Select(kv => kv.Key.Position)];
        e.bzr = [.. shown.Select(kv => kv.Key.LockState == LockedByTimer
            ? kv.Key.StateTicks + TimeSpan.FromSeconds(kv.Key.Template.ProtectionSeconds).Ticks : kv.Key.StateTicks)];
        e.bzs = new long[shown.Count];
        e.bzt = [.. shown.Select(kv => Math.Max(0, kv.Key.RarityFor(kv.Key.LockState)))];
    }

    /// <summary>Whether <paramref name="to"/> is at most <paramref name="range"/> cluster exits (world.xml) from <paramref name="start"/>.</summary>
    private static bool Hops(WorldInfo info, string start, string to, int range)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var ring = new List<string> { start };
        for (var i = 0; ; i++)
        {
            if (ring.Contains(to, StringComparer.Ordinal)) return true;
            if (i >= range || ring.Count == 0) return false;
            ring = [.. ring.SelectMany(c => info.Find(c)?.Exits.Select(x => x.TargetCluster) ?? []).Where(c => c.Length > 0 && seen.Add(c))];
        }
    }

    /// <summary>
    /// <paramref name="chest"/>'s PvP circle up (SPEC §3): with Pvp:Mode Official and a pvpzoneradius, NewLootChest key 27 true and the players
    /// inside unrestricted while it stands. Called before the chest is announced (its create carries the key). The Mists' treasures too.
    /// </summary>
    public void Contest(Map map, LootChest chest) => ContestIn(map, chest, null);

    /// <summary>
    /// The same for a Mists treasure of <paramref name="subtype"/> (CHEST, COFFER), which is also listed (issue #23): as the first
    /// &lt;TreasureLootchest&gt; of treasuredistribution.xml that spawns its spawnpoint (MISTS_TREASURE_SOLO → MISTS_CHEST_SOLO, …).
    /// </summary>
    public void Contest(Map map, LootChest chest, string subtype)
    {
        ContestIn(map, chest, null);
        var m = _maps.GetValue(map, _ => new MapTreasures { Tier = map.Template?.Name.Tier ?? 4 });
        List(m, chest, world.Clusters?.TreasuresIfLoaded?.Lootchests.Values.FirstOrDefault(d => d.Spawnpoint == chest.SpawnpointName), subtype);
    }

    private void ContestIn(Map map, LootChest chest, MapTreasures? m)
    {
        // a PvpDisabled cluster (the Outlands' rest areas also draw chests: their Distribute has no clustertype) keeps its peace [assume, SPEC §3.4]
        m ??= _maps.GetValue(map, _ => new MapTreasures { Tier = map.Template?.Name.Tier ?? 4 });
        var rules = world.PvpOverride ?? (m.Template ?? map.Template)?.Pvp ?? world.PvpFallback;
        if (Config.PvpMode != PvpMode.Official || chest.Template.PvpZoneRadius <= 0 || rules.Rule == PvpRule.PvpDisabled) return;
        chest.PvpZone = true;
        m.Contested.Add(chest);
    }

    /// <summary>
    /// .coffer spawn: a treasure of <paramref name="size"/> (coffer, solo, veteran, elite: TREASURE_COFFER_SOLO, DEFAULT_TREASURE_SOLO, _VETERAN,
    /// _ELITE) at <paramref name="at"/>, with its countdown and circle; it is not one of the cluster's slots and never comes back.
    /// </summary>
    public LootChest SpawnAt(Map map, string size, Vector2 at, float direction)
    {
        var spawnpoint = size.ToLowerInvariant() switch
        {
            "coffer" => "TREASURE_COFFER_SOLO",
            "solo" => "DEFAULT_TREASURE_SOLO",
            "veteran" => "DEFAULT_TREASURE_VETERAN",
            "elite" => "DEFAULT_TREASURE_ELITE",
            _ => throw new InvalidOperationException($"'{size}' is not one of {string.Join(", ", ServerConfig.TreasureSizeNames)}"),
        };
        var store = world.Clusters ?? throw new InvalidOperationException("no client data");
        var data = store.LootChests;
        if (!data.Spawnpoints.TryGetValue(spawnpoint, out var behaviour)) throw new InvalidOperationException($"{spawnpoint} is no LootChestSpawnpoint of lootchests.xml");
        var m = _maps.GetValue(map, _ => new MapTreasures { Tier = map.Template?.Name.Tier ?? 4 });
        // listed as the open world's definition of the size (issue #23); the file is read here if no cluster has needed it yet (a GM command)
        TreasureLootchest? def = null;
        try { def = store.Treasures.Lootchests.GetValueOrDefault(size.ToLowerInvariant() is "coffer" ? "COFFER_SOLO" : "DEFAULT_CHEST_" + size.ToUpperInvariant()); }
        catch (Exception e) when (e is InvalidDataException or IOException) { Combat.LogOnce("treasures-data", $"treasures: {e.Message}"); }
        var chest = Make(map, m, at, direction, behaviour, null, size.ToLowerInvariant() is "coffer" ? "COFFER" : "CHEST", def) ?? throw new InvalidOperationException($"{spawnpoint} draws no chest with a tile here");
        map.AddObject(chest);
        return chest;
    }

    /// <summary><paramref name="chest"/> opened for the first time (<see cref="RandomDungeons.OpenChest"/>): a treasure goes despawntimeseconds (300 s) later.</summary>
    public void Opened(LootChest chest)
    {
        if (chest.Map is not { } map || !_maps.TryGetValue(map, out var m) || !m.Made.Contains(chest)) return;
        if (chest.Spawn is not LootChestSpawn { Behaviour.DespawnSeconds: >= 0 and var seconds }) return;
        map.Timers.At(world.Now + TimeSpan.FromSeconds(seconds), () => Combat.RemoveBag(chest, "its time after the opening is up"));
    }

    /// <summary>
    /// The world tick: a treasure gone (despawned, emptied, its lifetime up) frees its spot (positioncooldown) and its slot's next draw is
    /// scheduled; the players' PvP circles are entered and left (308 to the player and the clients that know it).
    /// </summary>
    public void Update(Map map)
    {
        if (!_maps.TryGetValue(map, out var m)) return;
        foreach (var slot in m.Slots)
        {
            if (slot.Chest is not { } chest || chest.Map == map && map.Objects.ContainsKey(chest.Id)) continue;
            slot.Chest = null;
            if (m.Spots.FirstOrDefault(s => s.Chest == chest) is { } spot)
            {
                spot.Chest = null;
                spot.FreeAt = world.Now + TimeSpan.FromSeconds(slot.Draw.CooldownSeconds);
            }
            Schedule(map, m, slot);
        }
        m.Contested.RemoveAll(c => c.Map != map || !map.Objects.ContainsKey(c.Id));
        m.Made.RemoveWhere(c => c.Map != map || !map.Objects.ContainsKey(c.Id));
        // issue #23: a treasure gone or opened leaves the list; a changed list goes to every client on the map (bots have none)
        foreach (var c in m.Listed.Keys.Where(c => !Standing(map, c)).ToList())
        {
            m.Listed.Remove(c);
            m.ListChanged = true;
        }
        if (m.ListChanged)
        {
            m.ListChanged = false;
            var list = LocalTreasures(map);
            foreach (var p in map.Players.Values.Where(p => !p.IsBot)) p.Session.Send(list);
        }
        if (m.Contested.Count == 0 && m.Inside.Count == 0) return;
        var utc = DateTime.UtcNow;
        foreach (var p in m.Inside.Keys.Where(p => p.Map != map).ToList()) m.Inside.Remove(p); // gone from the map: its new map's client starts afresh
        foreach (var p in map.Players.Values)
        {
            var at = p.PositionAt(utc);
            var inside = !p.IsBot && m.Contested.Any(c => Vector2.Distance(at, c.Position) <= c.Template.PvpZoneRadius);
            if (inside)
            {
                if (!m.Inside.TryGetValue(p, out var told))
                {
                    m.Inside[p] = told = [];
                    p.Session.Send(Status(p, InsideStatus));
                    Log.Debug("pvp", $"'{p.Character.Name}' entered a treasure's PvP circle");
                }
                told.RemoveWhere(o => !p.Observers.Contains(o)); // out of sight: its client forgot the status with the character
                foreach (var o in p.Observers)
                    if (told.Add(o)) o.Session.Send(Status(p, InsideStatus));
            }
            else if (m.Inside.Remove(p))
            {
                p.SendAround(Status(p, 0));
                Log.Debug("pvp", $"'{p.Character.Name}' left a treasure's PvP circle");
            }
        }
    }

    private static UnrestrictedPvpZoneUpdateEvent Status(Player p, byte status)
    {
        var e = new UnrestrictedPvpZoneUpdateEvent { ddn = p.Id, d2h = status };
        if (status == 0) e.Extra = new() { [1] = (byte)0 }; // 0 too, which the serializer would leave out
        return e;
    }

    /// <summary>
    /// Pvp.Of: whether <paramref name="a"/> and <paramref name="b"/>, two players outside one party, both stand in a treasure's PvP circle (Pvp:Mode
    /// Official): then Hostile, as the client's a5g.b for two unrestricted characters (guild mates still Neutral under Guilds:NoFriendlyFire).
    /// </summary>
    public bool Unrestricted(Player a, Player b)
    {
        if (a.IsBot || b.IsBot || a.Map is not { } map || b.Map != map || !_maps.TryGetValue(map, out var m) || m.Inside.Count < 2) return false;
        return m.Inside.ContainsKey(a) && m.Inside.ContainsKey(b) && !Pvp.SameParty(a, b);
    }

    /// <summary>.coffer list: the treasure spots of <paramref name="map"/> nearest <paramref name="at"/> first, and the slots.</summary>
    public IEnumerable<string> Describe(Map map, Vector2 at)
    {
        if (!_maps.TryGetValue(map, out var m)) yield break;
        foreach (var g in m.Slots.GroupBy(s => s.Subtype))
        {
            var d = g.First().Draw;
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {g.Key}: {g.Count()} slot(s), {g.Count(s => s.Chest is not null)} standing; draw every {d.MinSeconds:0}-{d.MaxSeconds:0} s, chance {d.Chance:0.###}, {string.Join(", ", d.Entries.Select(e => $"{e.Name} {e.Weight:0.##}"))}");
        }
        foreach (var s in m.Spots.OrderBy(s => Vector2.Distance(s.Position, at)).Take(12))
        {
            var what = s.Chest is { } c ? $"#{c.Id} {c.Template.Name} {c.State.State}, ChestState {c.LockState}" : s.FreeAt > world.Now ? "cooling down" : "free";
            yield return string.Create(CultureInfo.InvariantCulture, $"  {s.Subtype} at ({s.Position.X:0.##}, {s.Position.Y:0.##}), {Vector2.Distance(s.Position, at):0} m: {what}");
        }
    }
}
