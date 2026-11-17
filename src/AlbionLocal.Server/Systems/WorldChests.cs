using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The loot chests of the open world (analysis/work/stageH/bosses/SPEC.md §4.2-4.6, "SPEC" here; PVE-DESIGN.md §3 step 5), on the
/// lootchestspawnpoint tiles of an open-world cluster (<see cref="ClusterTemplate.LootChests"/>), with World:OpenWorldChests on:
/// <list type="bullet">
/// <item>POI spots (a LootChestSpawnpoint whose entries carry lootchestupgradelevelindex: STATIC_&lt;F&gt;_VETERAN_POI in the static
/// focusMOB areas, §4.2): at most Chests:PoiLimit chests at once per cluster (−1 the data's worldsettings poilootchestspawnlimit, 1;
/// 0 every spot), each on a free spot drawn at random. The variant comes from the nearest living mob of a boss point
/// (triggersmobupgradespawnpointrespawn, the chest's faction) within 1.5 × the chest's alertradius: a champion → level 2, a
/// miniboss → 3, a boss → the highest; else a plain weighted pick of the entries that are not only an upgrade. LockedByProtectingMobs
/// (2) while a living mob of the chest's faction has its spawn point within alertradius (15 m), Unlocked (6) when the last one dies
/// (UpdateLootChest to every observer, event 395 false), locked again when one respawns before the opening. Rerolled every
/// rerolltimeseconds while unopened and locked (a full NewLootChest, same id). Gone despawntimeseconds (300 s) after its first opening
/// or when emptied; the next one comes uniform(respawntimeseconds) later.</item>
/// <item>Mob-upgrade spots (LootChestSpawnpointMobUpgrade, STATIC_&lt;F&gt;_VETERAN, §4.3): empty until a mob of a boss point of
/// mobupgradefaction dies with its spawn point within mobupgradealertradius (15 m); then the group whose mulithreshhold is the largest
/// ≤ the mob's mobUpgradeLevelIndex (CHAMPION_B 4 → CHAMPION, MINIBOSS 6, BOSS 8) puts its chest there, Unlocked. A trigger while one
/// stands changes nothing; it goes despawntimeseconds after its opening.</item>
/// <item>Camp chests (LootChestSpawnpointPersonal of the rolled camp layers, §4.4): one chest for everyone, with a ChestState per
/// player (key 23): LockedByQuest (4) until that player's local quest of the camp's localquestarea is done, then Unlocked; each player
/// rolls its own loot. Entering the quest circle starts the quest (LocalQuestStarted 540; leaving LocalQuestInactive 542, back
/// LocalQuestActive 541), the raw fame (× the zone's famefactor / mindivisor) of the quest faction's mobs killed by a player in the
/// circle counts (a party member in the same circle shares it), LocalQuestProgressUpdate 543 after each, and at the target the chest
/// unlocks for that player. cleanupdelayseconds (600 s) after its opening, or after the player left the circle with the quest done
/// and the chest unopened, the player's chest state and quest are cleared (its open container detached, LocalQuestAreaGone 603 when
/// its client knows the quest), so the camp can be done again. A camp chest no per-player quest unlocks (none in localquests.xml,
/// no killmobfame, no unlockpersonallootchest, the file unreadable) is not spawned: it would open with no quest.</item>
/// <item>StageQ faction camps (the open quest FACTION_CAMP_OPEN_QUEST_SMALL / _MEDIUM of a faction camp's FWARFARE_ACTIVE layer, <see cref="FactionCamps"/>):
/// one quest for everyone, listed at the Join (LocalQuestInfos 539 after the zone's 468, as captured in 1207: key 4 LocalQuestState Started 1 /
/// Finished 3, key 6 {"RelevantMobCountInArea":n}); its killallmobsinarea counts the mobs of the camp's isquestmobspawn points, and the last one's
/// death (LocalQuestProgressUpdate 543 to the map's players after each [infer]) finishes it: the camp's one chest (FACTION_DYNAMIC_CAMP_*_LC, a plain
/// spawnpoint) goes from LockedByQuest (4) to Unlocked (6) for everyone (unlocksharedlootchest), and the camp's switch is set off. When the camp goes
/// Inactive the quest goes (LocalQuestAreaGone 603) with its chest; at its reactivation a fresh one comes with the map's full 539 [infer].</item>
/// <item>StageL road chests (LootChestSpawnpointChestUpgrade, analysis/work/stageL/roads/SPEC.md §5.5, with Roads:Chests): at the fill (before the
/// mobs, <see cref="Plan"/>) the spot rolls its spawnchance and draws an entry by weight, whose lootchestupgradelevelindex L (BASE 4, CHAMPION 6,
/// BOSS 8) is the level its camp points (allowUpgradeByChest, the spot's mobupgradefaction, within mobupgraderadius) spawn at; a point near no
/// chest, or one whose roll failed, spawns its level-4 entries. LockedByProtectingMobs while a living mob of the chest's faction has its point
/// within alertradius (60 m), as a POI chest. A chest (re)spawn brings its dead guards back at once at its level (triggermobspawn) and a redraw
/// to another level replaces the living ones. An unopened locked chest no player sees is redrawn with rerollchance every rerolltimeseconds;
/// after its opening the next comes respawntimeseconds later (OnOpen); a failed roll tries again a reroll time later.</item>
/// </list>
/// Chests roll the cluster's tier (<see cref="RandomDungeons.OpenChest"/>). Respawn and reroll times are ÷ Rates:ChestRespawn. The state
/// lives with the map [assume: the SPEC keeps it in World, §5]: an unloaded map takes it along, and the next one starts afresh, as
/// its mob respawn timers do. Not handled (departures): the 11 s re-arm of a camp after its cleanup (the quest restarts at once), the open-world faction chests,
/// the maketreasureshrinevisible reward. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class WorldChests(World world)
{
    /// <summary>One chest spot of a map: its tile, spawnpoint, the chest standing on it, and (a camp chest) its quest circle.</summary>
    public sealed class Spot(LootChestSpawn point, ChestSpawnBehaviour behaviour)
    {
        public LootChestSpawn Point { get; } = point;
        public ChestSpawnBehaviour Behaviour { get; } = behaviour;
        public LootChest? Chest { get; internal set; }
        public QuestArea? Area { get; internal set; }

        /// <summary>Whether a respawn is scheduled (a POI spot's next chest, a camp chest gone).</summary>
        internal bool Waiting { get; set; }

        /// <summary>StageL roads: a road chest spot (LootChestSpawnpointChestUpgrade).</summary>
        public bool IsRoad => Behaviour.Kind == ChestSpawnKind.ChestUpgrade;

        /// <summary>StageL roads: a road chest spot's camp points (allowUpgradeByChest, its mobupgradefaction, within its mobupgraderadius).</summary>
        public List<MobSpawnSlot> Guards { get; } = [];

        /// <summary>StageL roads: the entry the fill drew (<see cref="Plan"/>); null = its spawn roll failed.</summary>
        internal ChestEntry? Planned { get; set; }

        /// <summary>Whether the chest on it locks by its protecting mobs (a POI chest, a road chest).</summary>
        internal bool LocksByMobs => Behaviour.IsPoi || IsRoad;

        public override string ToString() => $"{Point.Key} {Behaviour.Name} ({Behaviour.Kind}{(Behaviour.IsPoi ? ", POI" : "")})";
    }

    /// <summary>A camp's quest circle on a map: its quest object id (LocalQuestStarted key 0), the quest, and each player's progress.</summary>
    public sealed class QuestArea(long id, LocalQuestAreaSpawn spawn, LocalQuest quest)
    {
        public long Id { get; } = id;
        public LocalQuestAreaSpawn Spawn { get; } = spawn;
        public LocalQuest Quest { get; } = quest;
        public Spot? Chest { get; internal set; }
        internal Dictionary<Guid, QuestState> States { get; } = [];

        /// <summary>The players' progress, by character Guid (.quest local).</summary>
        public IReadOnlyDictionary<Guid, QuestState> Progress => States;

        /// <summary>The killmobfame target: the mission's value × the cluster quest factor (1 [assume], SPEC §4.4).</summary>
        public double Target => Quest.Fame;

        /// <summary>StageQ camps: an open quest (one for everyone, <see cref="LocalQuest.Open"/>): no per-player state, <see cref="Marked"/> decides.</summary>
        public bool Open => Quest.Open;

        /// <summary>An open quest's points: the camp slot's isquestmobspawn points, all of whose mobs must die (killallmobsinarea onlycountmobsmarkedforquest).</summary>
        public List<MobSpawnSlot> Marked { get; } = [];

        /// <summary>An open quest done (LocalQuestState Finished).</summary>
        public bool Finished { get; internal set; }

        /// <summary>The marked points with no living mob (dead, gone or never spawned).</summary>
        public int Cleared => Marked.Count(s => s.Mob is not { Dead: false, Removed: false });

        /// <summary>An open quest's progress (543 key 3): the share of its marked points cleared.</summary>
        public float Share => Marked.Count == 0 ? 1 : (float)Cleared / Marked.Count;

        public bool Contains(Vector2 at) => Vector2.Distance(at, Spawn.Position) <= Spawn.Radius;
    }

    /// <summary>A player's state in a quest circle: the fame so far, done, inside now, and the player object its client knows it from.</summary>
    public sealed class QuestState
    {
        public double Fame { get; internal set; }
        public bool Done { get; internal set; }
        public bool Inside { get; internal set; }
        internal Player? Known { get; set; }

        /// <summary>Whether the player opened the camp chest (its own cleanup runs then, not the left-the-circle one).</summary>
        internal bool Opened { get; set; }

        /// <summary>How often the player left the circle (a left-the-circle cleanup stands only for its own leaving).</summary>
        internal int Left { get; set; }
    }

    private sealed class MapChests
    {
        public readonly List<Spot> Spots = [];
        public readonly List<QuestArea> Areas = [];
        public int PoiLimit = int.MaxValue;
    }

    private readonly ConditionalWeakTable<Map, MapChests> _maps = new();

    /// <summary>The chest spots of <paramref name="map"/> (empty off the open world or with World:OpenWorldChests off).</summary>
    public IReadOnlyList<Spot> SpotsOf(Map map) => _maps.TryGetValue(map, out var m) ? m.Spots : [];

    /// <summary>The quest circles of <paramref name="map"/>.</summary>
    public IReadOnlyList<QuestArea> AreasOf(Map map) => _maps.TryGetValue(map, out var m) ? m.Areas : [];

    /// <summary>
    /// At an open-world map's fill (MapSpawner, after the mobs): the spots of its POI, mob-upgrade and camp spawnpoints and its quest
    /// circles, and the chests that stand from the start (the POI chests up to the cap, every camp chest), not yet on the map.
    /// </summary>
    public List<LootChest> Populate(Map map, ClusterTemplate template)
    {
        var made = new List<LootChest>();
        if (!world.Config.OpenWorldChests && !RoadChests || !OpenWorld(map, template) || world.Clusters?.LootChestsIfLoaded is not { } data) return made;
        // StageL roads: the road chest spots came with the plan (before the mobs); now their guards and chests.
        var m = _maps.TryGetValue(map, out var planned) ? planned : new MapChests();
        m.PoiLimit = world.Config.ChestsPoiLimit switch { 0 => int.MaxValue, > 0 and var n => n, _ => template.PoiChestLimit ?? int.MaxValue };
        foreach (var spot in m.Spots.Where(s => s.IsRoad))
        {
            Bind(map, spot);
            if (spot.Planned is { } entry && Make(map, spot, data, entry) is { } chest)
            {
                made.Add(chest);
                RerollRoad(map, spot, chest);
            }
            else Respawn(map, spot, Delay(map, spot.Behaviour.RerollSeconds ?? spot.Behaviour.RespawnSeconds)); // §5.5 rule 3: a failed roll tries again
        }
        if (!world.Config.OpenWorldChests)
        {
            if (m.Spots.Count > 0) _maps.AddOrUpdate(map, m);
            return made;
        }
        foreach (var point in template.LootChests)
            if (point.Behaviour is { } b && (b.IsPoi || b.Kind is ChestSpawnKind.MobUpgrade or ChestSpawnKind.Personal || OpenCampChest(template, point)))
                m.Spots.Add(new Spot(point, b));
        var open = new List<LocalQuestAreaSpawn>();
        foreach (var a in template.LocalQuestAreas)
        {
            if (a.Quest is { Open: true, KillAllMarked: true }) // stageQ camps: a faction camp's open quest, made after the per-player ones are bound
            {
                open.Add(a);
                continue;
            }
            if (a.Quest is not { PerPlayer: true, Fame: > 0 } quest) continue;
            var area = new QuestArea(world.ObjectIds.Next(), a, quest);
            // its camp chest: the nearest personal spot of the same template slot
            area.Chest = m.Spots.Where(s => s.Behaviour.Kind == ChestSpawnKind.Personal && s.Point.Key.Slot == a.Key.Slot && s.Area is null)
                .MinBy(s => Vector2.Distance(s.Point.Position, a.Position));
            if (area.Chest is not null && quest.UnlocksChest) area.Chest.Area = area;
            m.Areas.Add(area);
        }
        foreach (var a in open)
            if (Open(map, m, a, data) is { Chest: { } chest }) made.Add(chest);
        if (m.Spots.Count == 0 && m.Areas.Count == 0) return made;
        _maps.AddOrUpdate(map, m);
        foreach (var spot in m.Spots.Where(s => s.Behaviour.Kind == ChestSpawnKind.Personal))
        {
            if (spot.Area is null)
            {
                // no per-player quest unlocks it: Unlocked for all, it would give a fresh roll per player every cleanup (SPEC §4.4)
                Combat.LogOnce("campchest:" + spot.Behaviour.Name, $"camp chest {spot.Behaviour.Name}: no per-player local quest unlocks it, not spawned");
                continue;
            }
            if (Make(map, spot, data, Pick(map, spot.Behaviour.Entries)) is { } chest) made.Add(chest);
        }
        var pois = m.Spots.Where(s => s.Behaviour.IsPoi).OrderBy(_ => map.Random.Next()).ToList();
        foreach (var spot in pois.Take(Math.Min(pois.Count, m.PoiLimit)))
        {
            if (spot.Behaviour.InitialSeconds > 0)
            {
                Respawn(map, spot, TimeSpan.FromSeconds(spot.Behaviour.InitialSeconds));
                continue;
            }
            if (MakePoi(map, spot, data) is { } chest) made.Add(chest);
        }
        Log.Info("world", $"map {map.Cluster}: {m.Spots.Count(s => s.Behaviour.IsPoi)} POI, {m.Spots.Count(s => s.Behaviour.Kind == ChestSpawnKind.MobUpgrade)} mob-upgrade, "
            + $"{m.Spots.Count(s => s.IsRoad)} road and {m.Spots.Count(s => s.Behaviour.Kind == ChestSpawnKind.Personal)} camp chest spot(s), {m.Areas.Count} camp quest(s)"
            + (open.Count > 0 ? $" ({open.Count} faction camp quest(s))" : "") + $"; {made.Count} chest(s) now"
            + (m.PoiLimit < int.MaxValue ? $" (POI chests at most {m.PoiLimit} at once)" : ""));
        return made;
    }

    /// <summary>Whether <paramref name="map"/> is an open-world map with chest spots (no dungeon floor, expedition, corrupted dungeon or hellgate).</summary>
    private static bool OpenWorld(Map map, ClusterTemplate template) =>
        !DungeonLayout.IsInstance(map.Cluster) && !ExpeditionData.IsInstance(map.Cluster) && !CorruptedData.IsInstance(map.Cluster) && !HellgateData.IsInstance(map.Cluster)
        && template.LootChests.Count > 0;

    /// <summary>StageQ camps: a plain spawnpoint in the slot of a faction camp's open quest that unlocks a shared chest (SPAWNPOINT_FACTION_DYNAMIC_CAMP_*_LC).</summary>
    private static bool OpenCampChest(ClusterTemplate template, LootChestSpawn point) =>
        point.Behaviour is { Kind: ChestSpawnKind.Plain, IsPoi: false }
        && template.LocalQuestAreas.Any(a => a.Key.Slot == point.Key.Slot && a.Quest is { Open: true, KillAllMarked: true, UnlocksSharedChest: true });

    // ---- the faction camps' open quests (stageQ) ----

    /// <summary>
    /// A faction camp's open quest on <paramref name="a"/>: its marked points (the slot's isquestmobspawn ones, their mobs standing), and its chest on
    /// the nearest open-camp spot of the slot, LockedByQuest (4) for everyone; the chest is not on the map yet.
    /// </summary>
    private (QuestArea Area, LootChest? Chest) Open(Map map, MapChests m, LocalQuestAreaSpawn a, LootChestData data)
    {
        var area = new QuestArea(world.ObjectIds.Next(), a, a.Quest!);
        area.Marked.AddRange(map.MobSlots.Where(s => s.Point.Key.Slot == a.Key.Slot && s.Point.Block is { IsQuestMobSpawn: true }));
        m.Areas.Add(area);
        if (!area.Quest.UnlocksSharedChest) return (area, null);
        area.Chest = m.Spots.Where(s => s.Point.Key.Slot == a.Key.Slot && s.Area is null && s.Behaviour is { Kind: ChestSpawnKind.Plain, IsPoi: false })
            .MinBy(s => Vector2.Distance(s.Point.Position, a.Position));
        if (area.Chest is not { } spot) return (area, null);
        spot.Area = area;
        if (Make(map, spot, data, Pick(map, spot.Behaviour.Entries)) is not { } chest) return (area, null);
        chest.SetState(4, world.GameNow); // LockedByQuest until the camp's last marked mob dies
        return (area, chest);
    }

    /// <summary>LocalQuestInfos 539 of the map's open quests (as captured in 1207: keys 1, 4 and 9 as byte arrays); null when it has none.</summary>
    private static LocalQuestInfosEvent? Infos(MapChests m)
    {
        var list = m.Areas.Where(a => a.Open).ToList();
        if (list.Count == 0) return null;
        return new LocalQuestInfosEvent
        {
            d8p = [.. list.Select(a => a.Id)],                                                    // [0] quest object ids
            d8r = [.. list.Select(_ => "")],                                                      // [2] ""
            d8s = [.. list.Select(a => new[] { a.Finished })],                                    // [3] mission done flags
            d8t = [.. list.Select(a => a.Finished ? (byte)3 : (byte)1)],                          // [4] LocalQuestState Started / Finished
            d8u = [.. list.Select(a => a.Finished ? 0f : a.Share)],                               // [5] progress (captured 0, the finished one too)
            d8v = [.. list.Select(a => $"{{\"RelevantMobCountInArea\":{a.Marked.Count}}}")],    // [6] the mission's text arguments
            d8w = [.. list.Select(_ => false)],                                                   // [7]
            d8x = [.. list.Select(a => a.Spawn.Position)],                                        // [8] centres
            d8y = [.. list.Select(_ => (byte)1)],                                                 // [9] LocalQuestAreaType CircleDynamic
            d8z = [.. list.Select(a => a.Spawn.Radius)],                                          // [10] radii
            d80 = [.. list.Select(_ => "")],                                                      // [11] ""
            Extra = new() { [1] = list.Select(a => (byte)a.Quest.Index).ToArray() },             // [1] localquests.xml index, a byte[] on the wire
        };
    }

    /// <summary>The Join's phase B after the zone's 468 (<see cref="VisibilityTracker.SendClusterWide"/>): the map's open quests (539).</summary>
    public void Joined(Player p)
    {
        if (!p.IsBot && p.Map is { } map && _maps.TryGetValue(map, out var m) && Infos(m) is { } infos) p.Session.Send(infos);
    }

    /// <summary>An open quest's progress (543) to the map's players; at the last marked point cleared it is done (<see cref="Finish"/>).</summary>
    private void Progress(Map map, QuestArea area)
    {
        if (area.Finished) return;
        if (area.Cleared >= area.Marked.Count)
        {
            Finish(map, area);
            return;
        }
        var update = new LocalQuestProgressUpdateEvent { d81 = area.Id, d83 = [false], d84 = area.Share };
        foreach (var p in map.Players.Values) p.Session.Send(update);
    }

    /// <summary>
    /// An open quest done: Finished (543 done, 1), its chest Unlocked for everyone (UpdateLootChest to its observers), the camp's switch set off
    /// (<see cref="FactionCamps.QuestDone"/>).
    /// </summary>
    private void Finish(Map map, QuestArea area)
    {
        area.Finished = true;
        var done = new LocalQuestProgressUpdateEvent { d81 = area.Id, d83 = [true], d84 = 1 };
        foreach (var p in map.Players.Values) p.Session.Send(done);
        if (area.Chest?.Chest is { LockState: < 6 } chest && chest.Map == map)
        {
            chest.SetState(6, world.GameNow);
            foreach (var o in map.ObserversOf(chest)) o.Session.Send(chest.Update(o));
        }
        Log.Info("world", $"map {map.Cluster}: the faction camp quest {area.Quest.Name} of {area.Spawn.Key.Slot} is done ({area.Marked.Count} marked mobs dead), its chest unlocked");
        world.FactionCamps.QuestDone(map, area);
    }

    /// <summary>A faction camp gone Inactive (<see cref="FactionCamps"/>): its open quest goes (603 to the map's players) and its chest with it.</summary>
    public void Close(Map map, QuestArea area)
    {
        if (!_maps.TryGetValue(map, out var m) || !m.Areas.Remove(area)) return;
        if (area.Chest is { } spot && spot.Area == area)
        {
            spot.Area = null;
            if (spot.Chest is { } chest) Combat.RemoveBag(chest, "its faction camp went inactive");
            spot.Chest = null;
        }
        var gone = new LocalQuestAreaGoneEvent { d8n = area.Id };
        foreach (var p in map.Players.Values) p.Session.Send(gone);
    }

    /// <summary>A faction camp standing again (<see cref="FactionCamps"/>, its points spawned): a fresh open quest and chest, then the map's 539 to its players.</summary>
    public void Reopen(Map map, string slot)
    {
        if (!_maps.TryGetValue(map, out var m) || map.Template is not { } template || world.Clusters?.LootChestsIfLoaded is not { } data) return;
        foreach (var a in template.LocalQuestAreas.Where(a => a.Key.Slot == slot && a.Quest is { Open: true, KillAllMarked: true }))
            if (Open(map, m, a, data) is { Chest: { } chest }) map.AddObject(chest);
        if (Infos(m) is { } infos) foreach (var p in map.Players.Values) p.Session.Send(infos);
    }

    /// <summary>StageL roads: the road camps' chests run (Roads:Chests).</summary>
    private bool RoadChests => world.Config.Roads.Chests;

    // ---- the road chests (stageL roads SPEC §5.5) ----

    /// <summary>
    /// At a map's fill, before its mobs (MapSpawner): its road chest spots, each with its spawn roll and entry drawn, so that the camp points
    /// spawn at their chest's level (<see cref="PlannedLevel"/>). Nothing without road chest spots or with Roads:Chests off.
    /// </summary>
    public void Plan(Map map, ClusterTemplate template)
    {
        if (!RoadChests || !OpenWorld(map, template) || world.Clusters?.LootChestsIfLoaded is null) return;
        var spots = template.LootChests.Where(p => p.Behaviour is { Kind: ChestSpawnKind.ChestUpgrade }).Select(p => new Spot(p, p.Behaviour!)).ToList();
        if (spots.Count == 0) return;
        var m = new MapChests();
        foreach (var spot in spots)
        {
            spot.Planned = RoadEntry(map, spot, null);
            m.Spots.Add(spot);
        }
        _maps.AddOrUpdate(map, m);
    }

    /// <summary>
    /// The level a camp point of <paramref name="point"/> spawns at (§5.5 rule 1): the planned chest's of the nearest road chest spot that binds it
    /// (an allowUpgradeByChest block of the spot's faction within its mobupgraderadius); null = none (its lowest level).
    /// </summary>
    public int? PlannedLevel(Map map, MobSpawnPoint point)
    {
        if (point.Block is not { AllowUpgradeByChest: true } block || !_maps.TryGetValue(map, out var m)) return null;
        return m.Spots.Where(s => s.IsRoad && Binds(s, block.Faction ?? "", point.Position)).MinBy(s => Vector2.Distance(s.Point.Position, point.Position))?.Planned?.Level;
    }

    private static bool Binds(Spot spot, string faction, Vector2 at) =>
        SameFaction(faction, spot.Behaviour.MobUpgradeFaction ?? "") && Vector2.Distance(at, spot.Point.Position) <= spot.Behaviour.MobUpgradeRadius;

    /// <summary>A road chest spot's camp points: those of an allowUpgradeByChest block of its faction within its mobupgraderadius, nearest to it (a point in two spots' reach is the nearer one's).</summary>
    private void Bind(Map map, Spot spot)
    {
        spot.Guards.Clear();
        var roads = _maps.TryGetValue(map, out var m) ? m.Spots.Where(s => s.IsRoad).ToList() : [spot];
        foreach (var slot in map.MobSlots)
            if (slot.Point.Block is { AllowUpgradeByChest: true } block && Binds(spot, block.Faction ?? "", slot.Point.Position)
                && roads.Where(s => Binds(s, block.Faction ?? "", slot.Point.Position)).MinBy(s => Vector2.Distance(s.Point.Position, slot.Point.Position)) == spot)
                spot.Guards.Add(slot);
    }

    /// <summary>The spot's draw: its spawnchance (not for a GM's <paramref name="level"/>), then an entry by weight (of that level for a GM); null = none.</summary>
    private ChestEntry? RoadEntry(Map map, Spot spot, int? level)
    {
        if (level is null && map.Random.NextDouble() >= spot.Behaviour.SpawnChance) return null;
        return Pick(map, spot.Behaviour.Entries.Where(e => level is null || e.Level == level));
    }

    /// <summary>
    /// The camp points of <paramref name="spot"/> at <paramref name="level"/> (§5.5 rules 1-2): a living guard of another level is replaced by one
    /// of this level, a dead or waiting point spawns at once (triggermobspawn) when <paramref name="respawn"/>.
    /// </summary>
    private void SetGuards(Map map, Spot spot, int? level, bool respawn)
    {
        foreach (var slot in spot.Guards)
        {
            slot.ChestLevel = level;
            var block = slot.Point.Block!;
            var at = level ?? block.LowestLevel;
            if (slot.Mob is { Dead: false, Removed: false } mob)
            {
                if (block.HasAt(mob.Template, at)) continue;
                SpawnManager.Despawn(mob, world, respawn: false);
            }
            else if (!respawn) continue;
            slot.RespawnAt = null;
            if (SpawnManager.Spawn(map, slot, world) is { } guard) map.AddObject(guard);
            else slot.RespawnAt = world.Now + SpawnManager.RespawnDelay(map, slot, null, world);
        }
    }

    /// <summary>
    /// A road chest spot's next chest (the respawn after an opening, a retry, a GM's <paramref name="level"/>): the roll, then the guards at its
    /// level and the chest, locked while they live; a failed roll leaves the guards at the lowest level and tries again a reroll time later.
    /// </summary>
    public LootChest? SpawnRoad(Map map, Spot spot, int? level = null)
    {
        if (map.Unloaded || map.Grid is null || world.Clusters?.LootChestsIfLoaded is not { } data) return null;
        if (spot.Chest is { } old && old.Map == map) Combat.RemoveBag(old, "replaced");
        spot.Chest = null;
        var entry = RoadEntry(map, spot, level);
        if (entry is null)
        {
            SetGuards(map, spot, null, respawn: false);
            if (level is null) Respawn(map, spot, Delay(map, spot.Behaviour.RerollSeconds ?? spot.Behaviour.RespawnSeconds));
            return null;
        }
        SetGuards(map, spot, entry.Level, respawn: true);
        if (Make(map, spot, data, entry) is not { } chest) return null;
        map.AddObject(chest);
        RerollRoad(map, spot, chest);
        Log.Info("world", $"{spot}: {chest} at level {entry.Level} with {spot.Guards.Count(g => g.Mob is { Dead: false })} guard(s)");
        return chest;
    }

    /// <summary>
    /// A road chest's redraws: every rerolltimeseconds while it stands unopened and locked and no player sees it or any of its living guards (they
    /// stand up to mobupgraderadius off, past the chest's own window), with rerollchance an entry by weight; another level replaces its living
    /// guards with that level's, the dead ones keep their respawn timers (§5.5 rule 2: dead guards come back only with a chest (re)spawn).
    /// </summary>
    private void RerollRoad(Map map, Spot spot, LootChest chest)
    {
        if (spot.Behaviour.RerollSeconds is not { } reroll) return;
        map.Timers.At(world.Now + Delay(map, reroll), () =>
        {
            if (spot.Chest != chest || chest.Map != map || chest.Rolled) return;
            if (chest.LockState < 6 && !map.ObserversOf(chest).Any() && !spot.Guards.Any(g => g.Mob is { Dead: false } m && map.ObserversOf(m).Any()) && map.Random.NextDouble() < spot.Behaviour.RerollChance
                && world.Clusters?.LootChestsIfLoaded is { } data && Pick(map, spot.Behaviour.Entries) is { } entry && data.Chests.TryGetValue(entry.Chest, out var template)
                && template != chest.Template)
            {
                var tile = template.TileFor(map.Template?.Name.Biome, map.Template?.Name.TemplateFolder ?? "NONE");
                var states = template.States.Where(s => s.Weight > 0).ToList();
                if (tile.Length > 0 && states.Count > 0)
                {
                    chest.Redraw(template, DungeonGenerator.Pick(map.Random, [.. states.Select(s => (s, s.Weight))]), tile);
                    SetGuards(map, spot, entry.Level, respawn: false);
                    if (!Protected(map, chest) && chest.LockState < 6) SetLock(map, chest, locked: false);
                    Log.Debug("world", $"{spot}: rerolled to {chest} (level {entry.Level})");
                }
            }
            RerollRoad(map, spot, chest);
        });
    }

    // ---- making chests ----

    /// <summary>A weighted pick of <paramref name="entries"/> (all of them, onlyAsUpgrade too: the caller narrows them); null when none.</summary>
    private static ChestEntry? Pick(Map map, IEnumerable<ChestEntry> entries)
    {
        var list = entries.Where(e => e.Weight > 0).ToList();
        return list.Count == 0 ? null : DungeonGenerator.Pick(map.Random, [.. list.Select(e => (e, e.Weight))]);
    }

    /// <summary>
    /// The chest of <paramref name="entry"/> on <paramref name="spot"/>: its rare state by weight (hidden until unlock), the tile of the
    /// cluster's biome and sub-biome, the lock of its kind (a POI's by its protecting mobs, a mob-upgrade chest Unlocked, a camp chest
    /// Unlocked for itself and LockedByQuest per player when its circle unlocks it); null without a chest lootchests.xml has, or a tile.
    /// </summary>
    private LootChest? Make(Map map, Spot spot, LootChestData data, ChestEntry? entry)
    {
        if (entry is null || !data.Chests.TryGetValue(entry.Chest, out var template)) return null;
        var (biome, folder) = (map.Template?.Name.Biome, map.Template?.Name.TemplateFolder ?? "NONE");
        var tile = template.TileFor(biome, folder);
        var states = template.States.Where(s => s.Weight > 0).ToList();
        if (tile.Length == 0 || states.Count == 0)
        {
            Combat.LogOnce("chesttile:" + template.Name, $"loot chest {template.Name} has no tile for {biome}/{folder} or no rare state: not spawned");
            return null;
        }
        var state = DungeonGenerator.Pick(map.Random, [.. states.Select(s => (s, s.Weight))]);
        for (var roll = world.StaticDungeons.ChestRolls(map.Cluster); roll > 1; roll--) // stageN: a static dungeon event keeps the rarest of rerollnumber draws
            if (DungeonGenerator.Pick(map.Random, [.. states.Select(s => (s, s.Weight))]) is { } again && again.Rarity > state.Rarity) state = again;
        var personal = spot.Behaviour.Kind == ChestSpawnKind.Personal;
        var chest = new LootChest(world.ObjectIds.Next(), spot.Point, template, state, tile, 6, world.GameNow)
        {
            Personal = personal, MinimapDisplay = spot.Behaviour.MinimapDisplay,
            DefaultViewerState = personal ? (byte)4 : (byte)6, // a camp chest: LockedByQuest until the player's own quest is done
        };
        if (spot.LocksByMobs && Protected(map, chest))
        {
            chest.SetState(2, world.GameNow);
            chest.ProtectedByMobs = true;
        }
        spot.Chest = chest;
        spot.Waiting = false;
        return chest;
    }

    /// <summary>A POI chest on <paramref name="spot"/> (SPEC §4.2 rule 2) with its reroll timer.</summary>
    private LootChest? MakePoi(Map map, Spot spot, LootChestData data)
    {
        var chest = Make(map, spot, data, PoiEntry(map, spot));
        if (chest is not null) Reroll(map, spot, chest);
        return chest;
    }

    /// <summary>
    /// The POI variant (SPEC §4.2 rule 2): the entries of the level the nearest living boss-point mob of the chest's faction within 1.5 ×
    /// alertradius gives (champion 2, miniboss 3, boss the highest), else a weighted pick of those that are not only an upgrade.
    /// </summary>
    private ChestEntry? PoiEntry(Map map, Spot spot)
    {
        var entries = spot.Behaviour.Entries;
        var data = world.Clusters?.LootChestsIfLoaded;
        var first = entries.Select(e => data?.Chests.GetValueOrDefault(e.Chest)).FirstOrDefault(t => t is not null);
        if (first is not null)
        {
            var reach = first.AlertRadius * 1.5;
            var upgrader = map.MobSlots
                .Where(s => s.Mob is { Dead: false, Removed: false } && s.Point.Block is { TriggersMobUpgrade: true } && SameFaction(FactionOf(s), first.Faction)
                    && Vector2.Distance(s.Point.Position, spot.Point.Position) <= reach)
                .MinBy(s => Vector2.Distance(s.Point.Position, spot.Point.Position))?.Mob;
            var level = upgrader?.Template.Category switch
            {
                "champion" => 2,
                "miniboss" => 3,
                "boss" => entries.Max(e => e.Level),
                _ => 0,
            };
            if (level > 0 && Pick(map, entries.Where(e => e.Level == level)) is { } upgraded) return upgraded;
        }
        return Pick(map, entries.Where(e => !e.OnlyAsUpgrade));
    }

    /// <summary>The faction a spawn point's mob counts for: its block's (UNDEAD, UNDEAD_CAMP), else the mob's own.</summary>
    private static string FactionOf(MobSpawnSlot slot) => slot.Point.Block?.Faction ?? slot.Mob?.Template.Faction ?? "";

    private static bool SameFaction(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a living mob of <paramref name="chest"/>'s faction has its spawn point within the chest's alertradius (SPEC §4.2 rule 3).</summary>
    private static bool Protected(Map map, LootChest chest) =>
        chest.Template.AlertRadius > 0 && map.MobSlots.Any(s => s.Mob is { Dead: false, Removed: false } && SameFaction(FactionOf(s), chest.Template.Faction)
            && Vector2.Distance(s.Point.Position, chest.Position) <= chest.Template.AlertRadius);

    /// <summary>uniform(min, max) seconds ÷ Rates:ChestRespawn on the map's random.</summary>
    private TimeSpan Delay(Map map, (double Min, double Max) seconds)
    {
        var (min, max) = seconds;
        if (max < min) max = min;
        return TimeSpan.FromSeconds(Math.Max(0, (min + (max - min) * map.Random.NextDouble()) / world.Rates.ChestRespawn));
    }

    /// <summary>
    /// A POI spot's chest is redrawn while it stands unopened and locked (SPEC §4.2 rule 5), and the next reroll set: the chain runs
    /// until the chest is opened or gone, and only skips the redraw while it is Unlocked (it may lock again, <see cref="MobSpawned"/>).
    /// </summary>
    private void Reroll(Map map, Spot spot, LootChest chest)
    {
        if (spot.Behaviour.RerollSeconds is not { } reroll) return;
        map.Timers.At(world.Now + Delay(map, reroll), () =>
        {
            if (spot.Chest != chest || chest.Map != map || chest.Rolled) return;
            if (chest.LockState < 6 && world.Clusters?.LootChestsIfLoaded is { } data && PoiEntry(map, spot) is { } entry
                && data.Chests.TryGetValue(entry.Chest, out var template))
            {
                var tile = template.TileFor(map.Template?.Name.Biome, map.Template?.Name.TemplateFolder ?? "NONE");
                var states = template.States.Where(s => s.Weight > 0).ToList();
                if (tile.Length > 0 && states.Count > 0)
                {
                    chest.Redraw(template, DungeonGenerator.Pick(map.Random, [.. states.Select(s => (s, s.Weight))]), tile);
                    foreach (var o in map.ObserversOf(chest)) foreach (var e in chest.BuildCreate(o)) o.Session.Send(e); // a known id: a full update
                    Log.Debug("world", $"{spot}: rerolled to {chest}");
                }
            }
            Reroll(map, spot, chest);
        });
    }

    /// <summary>The next chest of <paramref name="spot"/> after <paramref name="delay"/>: a POI spot's goes to a free POI spot drawn then.</summary>
    private void Respawn(Map map, Spot spot, TimeSpan delay)
    {
        spot.Waiting = true;
        map.Timers.At(world.Now + delay, () =>
        {
            spot.Waiting = false;
            if (!_maps.TryGetValue(map, out var m) || map.Unloaded || map.Grid is null || world.Clusters?.LootChestsIfLoaded is not { } data) return;
            var target = spot;
            if (spot.Behaviour.IsPoi)
            {
                if (m.Spots.Count(s => s.Behaviour.IsPoi && s.Chest is not null) >= m.PoiLimit) return;
                var free = m.Spots.Where(s => s.Behaviour.IsPoi && s.Chest is null && (!s.Waiting || s == spot)).ToList();
                if (free.Count == 0) return;
                target = free[map.Random.Next(free.Count)];
            }
            if (target.Chest is not null) return;
            if (target.IsRoad)
            {
                SpawnRoad(map, target); // stageL roads: the roll, the guards, the chest
                return;
            }
            var chest = target.Behaviour.IsPoi ? MakePoi(map, target, data) : Make(map, target, data, Pick(map, target.Behaviour.Entries));
            if (chest is null) return;
            map.AddObject(chest);
            Log.Info("world", $"{target}: {chest} spawned");
        });
    }

    // ---- the lock (SPEC §4.2 rule 3) ----

    private void SetLock(Map map, LootChest chest, bool locked)
    {
        chest.SetState(locked ? (byte)2 : (byte)6, world.GameNow);
        var was = chest.ProtectedByMobs;
        chest.ProtectedByMobs = locked;
        foreach (var o in map.ObserversOf(chest)) o.Session.Send(chest.Update(o));
        if (chest.MinimapDisplay && was != locked) map.SendToObservers(chest, new UpdateLootProtectedByMobsWithMinimapDisplayEvent { ddn = chest.Id, d4z = locked });
        Log.Info("world", $"{chest} {(locked ? "locked again by a protecting mob" : "unlocked: its protecting mobs are dead")}");
    }

    /// <summary>A GM's unlock (.chest unlock): a static chest for all, a personal one for <paramref name="p"/>.</summary>
    public void Unlock(LootChest chest, Player p)
    {
        if (chest.Map is not { } map) return;
        if (chest.Personal)
        {
            chest.SetViewerState(p, 6, world.GameNow);
            p.Session.Send(chest.Update(p));
            return;
        }
        if (chest.LockState < 6) SetLock(map, chest, locked: false);
    }

    /// <summary>A mob back on its point (SpawnManager): an unopened, unlocked POI chest it protects locks again [assume].</summary>
    public void MobSpawned(Map map, Mob mob)
    {
        if (!_maps.TryGetValue(map, out var m)) return;
        foreach (var spot in m.Spots)
            if (spot is { LocksByMobs: true, Chest: { Rolled: false, LockState: 6 } chest } && chest.Map == map && Protected(map, chest))
                SetLock(map, chest, locked: true);
    }

    /// <summary>
    /// <paramref name="mob"/> killed by <paramref name="killer"/> (Combat.Kill, after the kill batch), worth <paramref name="rawFame"/>: the
    /// POI chests it was the last protector of unlock; a boss-point mob fills the mob-upgrade spots in reach; the camp quests count it.
    /// </summary>
    public void Killed(Map map, Player killer, Mob mob, double rawFame)
    {
        if (!_maps.TryGetValue(map, out var m)) return;
        foreach (var spot in m.Spots)
            if (spot is { LocksByMobs: true, Chest: { Rolled: false, LockState: < 6 } chest } && chest.Map == map && !Protected(map, chest))
                SetLock(map, chest, locked: false);
        var faction = mob.Slot is { } slot ? FactionOf(slot) : mob.Template.Faction;
        if (mob.Slot?.Point is { Block: { TriggersMobUpgrade: true } block } home && world.Clusters?.LootChestsIfLoaded is { } data)
        {
            var level = block.UpgradeLevelOf(mob.Template);
            foreach (var spot in m.Spots.Where(s => s.Behaviour.Kind == ChestSpawnKind.MobUpgrade && s.Chest is null
                         && SameFaction(faction, s.Behaviour.MobUpgradeFaction ?? "") && Vector2.Distance(home.Position, s.Point.Position) <= s.Behaviour.MobUpgradeRadius))
            {
                if (spot.Behaviour.UpgradeFor(level) is not { Count: > 0 } group) continue; // below the lowest threshold
                if (Make(map, spot, data, Pick(map, group)) is not { } chest) continue;
                map.AddObject(chest);
                Log.Info("world", $"{spot}: {chest} for the kill of {mob} (upgrade level {level})");
            }
        }
        // StageQ camps: a faction camp's open quest counts its marked points' mobs, whoever killed them.
        foreach (var area in m.Areas.Where(a => a.Open && mob.Slot is { } s && a.Marked.Contains(s)).ToList()) Progress(map, area);
        // The camp quests (SPEC §4.4): the killer in the circle, and its party members in the same circle with countpartykillsinsamelocalquestarea.
        var utc = DateTime.UtcNow;
        foreach (var area in m.Areas)
        {
            if (area.Open || !SameFaction(faction, area.Quest.Faction) || !area.Contains(killer.PositionAt(utc))) continue;
            var party = area.Quest.PartyKills ? world.Parties.Of(killer.Character.Id) : null;
            foreach (var p in map.Players.Values)
            {
                if (p != killer && (party is null || party.Find(p.Character.Id) is null)) continue;
                if (!area.States.TryGetValue(p.Character.Id, out var st) || st.Done || st.Known != p || !area.Contains(p.PositionAt(utc))) continue;
                st.Fame += rawFame;
                st.Done = st.Fame >= area.Target;
                p.Session.Send(new LocalQuestProgressUpdateEvent { d81 = area.Id, d83 = [st.Done], d84 = (float)Math.Min(1, st.Fame / area.Target) });
                if (!st.Done) continue;
                Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' did the camp quest {area.Quest.Name} ({st.Fame:0} fame)"));
                if (area.Chest?.Chest is { } camp && camp.Map == map)
                {
                    camp.SetViewerState(p, 6, world.GameNow); // <unlockpersonallootchest/>: for this player alone
                    if (p.Visibility.Knows(camp)) p.Session.Send(camp.Update(p));
                }
            }
        }
    }

    /// <summary>
    /// <paramref name="p"/> opened <paramref name="chest"/> for the first time (<see cref="RandomDungeons.OpenChest"/>): a POI or reward
    /// chest goes despawntimeseconds later; a camp chest clears the player's state and quest cleanupdelayseconds later.
    /// </summary>
    public void Opened(LootChest chest, Player p)
    {
        if (chest.Map is not { } map || !_maps.TryGetValue(map, out var m) || m.Spots.FirstOrDefault(s => s.Chest == chest) is not { } spot) return;
        if (spot.IsRoad) world.Bounties.RoadChestOpened(p, map.Template?.Name.Tier ?? 4); // stageL roads phase B: the roadchest bounties
        if (spot.IsRoad && spot.Behaviour.Respawn == ChestRespawn.OnOpen)
        {
            // stageL roads (SPEC §5.5): respawn OnOpen, the next chest respawntimeseconds after the opening (its guards with it)
            spot.Waiting = true;
            map.Timers.At(world.Now + Delay(map, spot.Behaviour.RespawnSeconds), () =>
            {
                spot.Waiting = false;
                if ((spot.Chest == chest || spot.Chest is null) && _maps.TryGetValue(map, out var live) && live == m) SpawnRoad(map, spot); // emptied meanwhile too
            });
            return;
        }
        if (!chest.Personal)
        {
            if (spot.Behaviour.DespawnSeconds >= 0)
                map.Timers.At(world.Now + TimeSpan.FromSeconds(spot.Behaviour.DespawnSeconds), () => Combat.RemoveBag(chest, "its time after the opening is up"));
            return;
        }
        if (spot.Behaviour.CleanupSeconds < 0) return;
        var id = p.Character.Id;
        if (spot.Area?.States.GetValueOrDefault(id) is { } st) st.Opened = true;
        map.Timers.At(world.Now + TimeSpan.FromSeconds(spot.Behaviour.CleanupSeconds), () =>
        {
            if (spot.Chest == chest) ResetCamp(map, spot.Area, id);
        });
    }

    /// <summary>
    /// A camp's cleanup for character <paramref name="id"/> (SPEC §4.4): its open camp chest closed (DetachItemContainer of its own
    /// container, which goes), its chest state cleared (LockedByQuest again), and its quest dropped, with LocalQuestAreaGone (603 {0
    /// id}) when its client knows the quest, so that a later LocalQuestStarted of the same id finds none.
    /// </summary>
    private static void ResetCamp(Map map, QuestArea? area, Guid id)
    {
        var p = map.Players.Values.FirstOrDefault(x => x.Character.Id == id);
        if (area?.Chest is { Area: { } own, Chest: { } chest } && own == area && chest.Map == map)
        {
            if (p is not null && p.OpenBag == chest)
            {
                p.OpenBag = null;
                p.Session.Send(new DetachItemContainerEvent { b20 = chest.ItemsFor(p).Id });
            }
            chest.ResetViewer(id);
            if (p is not null && p.Visibility.Knows(chest)) p.Session.Send(chest.Update(p));
        }
        if (area is not null && area.States.Remove(id, out var st) && p is not null && st.Known == p)
            p.Session.Send(new LocalQuestAreaGoneEvent { d8n = area.Id }); // the quest starts anew at the next visit
    }

    /// <summary>
    /// <paramref name="p"/> left <paramref name="area"/> with its quest done and the chest unopened: cleanupdelayseconds later, still out
    /// and unopened, the camp is reset for it (SPEC §4.4, §4.5).
    /// </summary>
    private void LeftDone(Map map, QuestArea area, Player p, QuestState st)
    {
        if (st.Opened || area.Chest?.Area != area || area.Chest.Behaviour.CleanupSeconds < 0) return;
        var (id, left) = (p.Character.Id, st.Left);
        map.Timers.At(world.Now + TimeSpan.FromSeconds(area.Chest.Behaviour.CleanupSeconds), () =>
        {
            if (area.States.GetValueOrDefault(id) == st && st is { Done: true, Inside: false, Opened: false } && st.Left == left) ResetCamp(map, area, id);
        });
    }

    /// <summary>
    /// The world tick: a chest gone from its map (despawned, emptied) frees its spot and the next one is scheduled (a POI's
    /// uniform(respawntimeseconds), a camp chest's after its respawn time); the players' quest circles are entered and left.
    /// </summary>
    public void Update(Map map)
    {
        if (!_maps.TryGetValue(map, out var m) || map.Grid is null) return;
        foreach (var spot in m.Spots)
        {
            if (spot.Chest is not { } chest) continue;
            if (chest.Map == map && map.Objects.ContainsKey(chest.Id))
            {
                // a locked POI or road chest whose last protector went without a player's kill (.mob kill, a despawn): unlocked
                if (spot.LocksByMobs && chest is { Rolled: false, LockState: < 6 } && !Protected(map, chest)) SetLock(map, chest, locked: false);
                continue;
            }
            spot.Chest = null;
            if (spot.IsRoad && spot.Waiting) continue; // stageL roads: emptied after its opening, the OnOpen respawn is on its way
            if (spot.Behaviour.Kind == ChestSpawnKind.MobUpgrade || spot.Behaviour.Respawn == ChestRespawn.Never && !spot.Behaviour.IsPoi) continue;
            Respawn(map, spot, Delay(map, spot.Behaviour.RespawnSeconds));
        }
        // StageQ camps: an open quest whose last marked mob went without a player's kill (.mob kill, a despawn) is done too.
        foreach (var area in m.Areas.Where(a => a is { Open: true, Finished: false, Marked.Count: > 0 } && a.Cleared >= a.Marked.Count).ToList()) Finish(map, area);
        if (m.Areas.Count == 0 || map.Players.Count == 0) return;
        var utc = DateTime.UtcNow;
        foreach (var area in m.Areas)
            foreach (var p in map.Players.Values)
            {
                if (area.Open) break; // known from 539, no per-player state [infer: the captured Joins show no 540-542 for them]
                var inside = area.Contains(p.PositionAt(utc));
                area.States.TryGetValue(p.Character.Id, out var st);
                if (inside && (st is null || st.Known != p))
                {
                    st ??= area.States[p.Character.Id] = new QuestState();
                    (st.Known, st.Inside) = (p, true);
                    // SPEC §3.7, the captured keys {0, 1, 2, 3, 5, 6, 7}: 1 the localquests.xml index, 3 a done flag per mission,
                    // 5 the centre, 6 the area type 1, 7 the radius (key 2, 5 undecoded characters, stays out)
                    var started = new LocalQuestStartedEvent
                    {
                        d86 = area.Id, d87 = area.Quest.Index, d89 = [st.Done], d9b = area.Spawn.Position, d9c = 1, d9d = area.Spawn.Radius,
                    };
                    if (area.Quest.Index == 0) started.Extra = new() { [1] = 0 }; // index 0 too, which the serializer would leave out
                    p.Session.Send(started);
                    if (st.Fame > 0) p.Session.Send(new LocalQuestProgressUpdateEvent { d81 = area.Id, d83 = [st.Done], d84 = (float)Math.Min(1, st.Fame / area.Target) });
                }
                else if (st is not null && st.Known == p && inside != st.Inside)
                {
                    st.Inside = inside;
                    p.Session.Send(inside ? new LocalQuestActiveEvent { d8m = area.Id } : new LocalQuestInactiveEvent { d8o = area.Id });
                    if (!inside && st.Done)
                    {
                        st.Left++;
                        LeftDone(map, area, p, st);
                    }
                }
            }
    }

    /// <summary>.chest list: the spots of <paramref name="map"/> nearest <paramref name="at"/> first.</summary>
    public IEnumerable<string> Describe(Map map, Vector2 at) =>
        SpotsOf(map).OrderBy(s => Vector2.Distance(s.Point.Position, at)).Select(s =>
        {
            var what = s.Chest is { } c ? $"#{c.Id} {c.Template.Name} {c.State.State}, ChestState {c.LockState}{(c.Personal ? $", {c.Viewers.Count()} player state(s)" : "")}"
                : s.Waiting ? "waiting for its respawn" : "empty";
            return string.Create(CultureInfo.InvariantCulture,
                $"  {s.Behaviour.Name} at ({s.Point.Position.X:0.##}, {s.Point.Position.Y:0.##}), {Vector2.Distance(s.Point.Position, at):0} m: {what}");
        });
}
