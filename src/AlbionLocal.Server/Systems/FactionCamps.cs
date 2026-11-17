using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageQ: the dynamic templates' states and the faction camps (analysis/work/stageQ/camps/CLIENT.md, outposts/DATA.md §5; FactionWarfare:Camps of
/// <see cref="FactionWarfareOptions"/>).
/// <list type="bullet">
/// <item><b>DynamicTemplate 604</b>: every rolled dyn slot with VAR layers is a <see cref="DynamicTemplateObject"/> in the visibility window, so the
/// client draws its Default (or faction warfare) camp; before this the server never sent it and the client drew none of the camps' VAR layers.</item>
/// <item><b>Faction camps</b> (the slots the resolver gave the FactionWarfare feature layer, <see cref="DynamicRoll.FeatureLayer"/>): the
/// *_DYN_VAR_FWARFARE_ACTIVE layer's FACTION_CAMP_* points (T&lt;t&gt;_MOB_CITY_CAMP_CAERLEON_GUARD_*, npchostility guard, faction CITY_CAERLEON)
/// fight the players flagged for another faction (<see cref="Mob.Foes"/>) and never respawn while the camp stands (respawntimeseconds 99999); the
/// open quest FACTION_CAMP_OPEN_QUEST_SMALL / _MEDIUM and the shared chest FACTION_DYNAMIC_CAMP_*_LC run in <see cref="WorldChests"/> (locked by
/// the quest until the last marked mob dies). switchdynamictemplatestate delaysec (300 s) after the quest the camp goes Inactive (604 Inactive /
/// FactionWarfare: the client's FWARFARE_INACTIVE ruins; its chest and quest gone), and after factionwarfare.xml's
/// dynamictemplatecampsreactivationtimer{small,medium}camp (420-660 s, 900-1260 s; ÷ Rates:Respawn) it stands again: Active, every point anew,
/// a fresh quest and chest [infer: the order of the reactivation's events is not captured].</item>
/// </list>
/// A Default camp keeps its mobs' own respawns and stays Active (its duration cycle of dynamictemplates.xml is not run). The state lives with the
/// map: an unloaded map takes it along, the next one starts with every camp Active. Not done: the faction points / supplies of a camp
/// (factionsupplyrewardreference, pvpchallengeobjective), the red zone event's camp timers. Under <see cref="World.Sync"/>.
/// </summary>
public sealed class FactionCamps(World world)
{
    /// <summary>The faction camp mobs' faction: every FACTION_CAMP_* mob is CITY_CAERLEON (FlaggingStatus 6).</summary>
    public const byte Caerleon = 6;

    /// <summary>One faction camp of a map: its 604 object, size and points.</summary>
    public sealed class Camp(Map map, DynamicTemplateObject slot, bool medium)
    {
        public Map Map { get; } = map;
        public DynamicTemplateObject Object { get; } = slot;
        public bool Medium { get; } = medium;
        public string Slot => Object.Slot;

        /// <summary>The mob points of its FWARFARE_ACTIVE layer.</summary>
        public List<MobSpawnSlot> Guards { get; } = [];

        /// <summary>When its next state comes (the switch after the quest, the reactivation); null while it waits for its quest.</summary>
        public TimeSpan? Due { get; internal set; }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture,
            $"{(Medium ? "medium" : "small")} faction camp {Slot} ({Object.TypeName}) {(Object.State == DynamicRoll.Active ? "Active" : "Inactive")}, {Guards.Count(g => g.Mob is { Dead: false, Removed: false })}/{Guards.Count} guards up");
    }

    private readonly ConditionalWeakTable<Map, List<Camp>> _maps = new();

    /// <summary>The faction camps of <paramref name="map"/>.</summary>
    public IReadOnlyList<Camp> Of(Map map) => _maps.TryGetValue(map, out var list) ? list : [];

    /// <summary>The players a faction camp's guards fight: those flagged for a faction other than theirs (Caerleon) [infer, as the towers' guards].</summary>
    private static readonly Func<Player, bool> CampFoes = p => Pvp.IsFaction(p.Flag) && p.Flag != Caerleon;

    /// <summary>
    /// <see cref="MobSpawnSlot.Foes"/> of <paramref name="point"/> (<see cref="SpawnManager.Start"/>): a point of a faction camp's slot fights the
    /// players flagged for another faction than Caerleon; null for every other point.
    /// </summary>
    public static Func<Player, bool>? FoesAt(ClusterTemplate? template, MobSpawnPoint point) =>
        template?.Instances.FirstOrDefault(i => i.Slot == point.Key.Slot)?.Roll is { FeatureLayer: DynamicRoll.FactionWarfare } ? CampFoes : null;

    /// <summary>
    /// At a map's fill (<see cref="MapSpawner"/>, after the mobs): one <see cref="DynamicTemplateObject"/> per rolled slot with VAR layers, Active;
    /// the faction camps with their points.
    /// </summary>
    public List<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        var objects = new List<WorldObject>();
        var camps = new List<Camp>();
        foreach (var i in template.Instances)
        {
            if (i.Roll is not { Switched: true } roll) continue;
            var spawn = new DynamicSlotSpawn
            {
                Key = new SpawnKey(template.Id, i.Slot, -1), Tile = "", Position = i.Position, Height = i.Height, Direction = i.Rotation, Roll = roll,
                Component = new XElement("dyntemplateslot", new XAttribute("id", i.Slot), new XAttribute("template", roll.Template)),
            };
            var o = new DynamicTemplateObject(world.ObjectIds.Next(), spawn);
            objects.Add(o);
            if (roll.FeatureLayer != DynamicRoll.FactionWarfare) continue;
            var camp = new Camp(map, o, roll.Template.StartsWith("M_", StringComparison.Ordinal));
            camp.Guards.AddRange(map.MobSlots.Where(s => s.Point.Key.Slot == i.Slot));
            camps.Add(camp);
        }
        if (camps.Count > 0)
        {
            _maps.AddOrUpdate(map, camps);
            Log.Info("world", $"map {map.Cluster}: {camps.Count} faction camp(s) ({string.Join(", ", camps.Select(c => $"{c.Slot} {c.Object.TypeName}, {c.Guards.Count} guards"))})");
        }
        return objects;
    }

    /// <summary>The faction camp of <paramref name="map"/> on slot <paramref name="slot"/>; null when none.</summary>
    public Camp? CampOf(Map map, string slot) => Of(map).FirstOrDefault(c => c.Slot == slot);

    /// <summary>
    /// <see cref="WorldChests"/>: the open quest of a faction camp is done (its last marked mob died): the camp goes Inactive switchdynamictemplatestate
    /// delaysec later (300 s).
    /// </summary>
    public void QuestDone(Map map, WorldChests.QuestArea area)
    {
        if (CampOf(map, area.Spawn.Key.Slot) is not { } camp || camp.Object.State != DynamicRoll.Active || camp.Due is not null) return;
        var delay = TimeSpan.FromSeconds(Math.Max(0, area.Quest.SwitchStateSeconds ?? 300));
        camp.Due = world.Now + delay;
        map.Timers.At(camp.Due.Value, () => Deactivate(camp, area));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"map {map.Cluster}: {camp} cleared, inactive in {delay.TotalSeconds:0} s"));
    }

    /// <summary>Inactive: 604 to the players who see the slot, the quest and the chest gone, the points held; the reactivation timer set.</summary>
    private void Deactivate(Camp camp, WorldChests.QuestArea area)
    {
        var map = camp.Map;
        if (map.Unloaded || camp.Object.State != DynamicRoll.Active) return;
        camp.Object.State = DynamicRoll.Inactive;
        map.SendToObservers(camp.Object, camp.Object.Build());
        world.WorldChests.Close(map, area);
        foreach (var slot in camp.Guards)
        {
            slot.Held = true; // a corpse still lying there sets no respawn either
            if (slot.Mob is { Dead: false, Removed: false } m) SpawnManager.Despawn(m, world, respawn: false);
            slot.RespawnAt = null;
        }
        var data = world.Clusters?.WarfareIfLoaded;
        var (min, max) = camp.Medium ? data?.MediumCampReactivation ?? (900, 1260) : data?.SmallCampReactivation ?? (420, 660);
        if (max < min) max = min;
        camp.Due = world.Now + TimeSpan.FromSeconds(Math.Max(0, (min + (max - min) * map.Random.NextDouble()) / world.Rates.Respawn));
        map.Timers.At(camp.Due.Value, () => Activate(camp));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"map {map.Cluster}: {camp}, back in {(camp.Due.Value - world.Now).TotalSeconds:0} s"));
    }

    /// <summary>Active again: 604, every point's mob anew, a fresh open quest and chest (<see cref="WorldChests.Reopen"/>).</summary>
    private void Activate(Camp camp)
    {
        var map = camp.Map;
        if (map.Unloaded || map.Grid is null || camp.Object.State != DynamicRoll.Inactive) return;
        camp.Object.State = DynamicRoll.Active;
        camp.Due = null;
        map.SendToObservers(camp.Object, camp.Object.Build());
        foreach (var slot in camp.Guards)
        {
            slot.Held = false;
            slot.RespawnAt = null;
            if (slot.Mob is { Removed: false } old) SpawnManager.Despawn(old, world, respawn: false);
            if (SpawnManager.Spawn(map, slot, world) is { } mob) map.AddObject(mob);
            else slot.RespawnAt = world.Now + SpawnManager.RespawnDelay(map, slot, null, world);
        }
        world.WorldChests.Reopen(map, camp.Slot);
        Log.Info("world", $"map {map.Cluster}: {camp} again");
    }

    /// <summary>.camp: the dynamic templates of <paramref name="map"/>, nearest first from <paramref name="at"/>.</summary>
    public IEnumerable<string> Describe(Map map, Vector2 at)
    {
        var slots = map.Objects.Values.OfType<DynamicTemplateObject>().OrderBy(o => Vector2.Distance(o.Position, at)).ToList();
        if (slots.Count == 0) yield return "no dynamic template with VAR layers on this map";
        foreach (var o in slots)
        {
            var camp = CampOf(map, o.Slot);
            var due = camp?.Due is { } d ? string.Create(CultureInfo.InvariantCulture, $", next state in {(d - world.Now).TotalSeconds:0} s") : "";
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {o.Slot} {o.TypeName} at ({o.Position.X:0}, {o.Position.Y:0}) {Vector2.Distance(o.Position, at):0} m: {(o.FeatureLayer == DynamicRoll.FactionWarfare ? "FactionWarfare" : "Default")} "
                + $"{(o.State == DynamicRoll.Active ? "Active" : "Inactive")}{(camp is null ? "" : $", {camp.Guards.Count(g => g.Mob is { Dead: false, Removed: false })}/{camp.Guards.Count} guards up")}{due}");
        }
    }
}
