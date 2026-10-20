using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The static dungeons (analysis/work/stageN/static-dungeons/SPEC.md): the fixed DUNGEON_* clusters of world.xml (Stoneroot Caverns and
/// the others; 98 dungeons of 1-2 clusters, by the clusters' belongstostaticdungeonid) and their open-world entrances (the exits with a
/// staticdungeonid; the main one of targettype DungeonGroup or DungeonSolo). Travel in and out is plain exit travel (shared clusters,
/// not instanced, as official); mobs, bosses and chests are the cluster's own spawn data. This class adds what the client shows of
/// them: the dungeon events of staticdungeons.xml (per clustertype and tier: a cooldown of uniform(mincooldown, maxcooldown) between
/// events, one event of the group by weight lasting its duration) and the dungeon value (StaticDungeonValueInfo).
/// <list type="bullet">
/// <item>The schedule is a pure function of the dungeon id and the time (slots of (min + max) / 2 + duration with a start jitter of
/// (max − min) / 2, so the gap between two events stays within [min, max]): no state, the same after a restart.</item>
/// <item>The value is the share of living mobs on the dungeon's loaded maps (an unloaded map counts as full) against
/// globalsettings' low/medium/high thresholds: VERY_LOW 1, LOW 2, MEDIUM 3, HIGH 4 [assume: the official measure is not in the client].</item>
/// <item>The world map (ClusterInfoUpdate keys 65-68, <see cref="Network.WorldSession.ClusterInfo"/>) and, on each Join into a dungeon
/// cluster or an entrance cluster, StaticDungeonEntrancesDungeonEventStatusUpdates (499: ids, start, end, rarity, value; start 0 = no
/// event; the client times the event out itself, cjp.c 0x180ab94b0).</item>
/// <item>The event's effect [assume: the client only names it]: a chest made on a dungeon map while its event runs draws its rare state
/// rerollnumber times and keeps the rarest (<see cref="ChestRolls"/>). respawntimemodifier is 1 in every group and is not applied.</item>
/// </list>
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class StaticDungeons(World world)
{
    public sealed record Dungeon(Guid Id, string Type, int Tier, IReadOnlyList<string> Clusters, IReadOnlyList<Entrance> Entrances);

    public sealed record Entrance(string Cluster, Guid Exit, bool Main);

    public sealed record EventDef(string Name, int Weight, int Rerolls, int DurationSeconds, byte Rarity);

    public sealed record EventGroup(string ClusterType, int Tier, int MinCooldown, int MaxCooldown, IReadOnlyList<EventDef> Events);

    /// <summary>staticdungeons.xml: globalsettings' value thresholds, events@upcomingeventtimeframeinsec and the event groups.</summary>
    public sealed record Settings(double Low, double Medium, double High, int UpcomingSeconds, IReadOnlyList<EventGroup> Groups)
    {
        public static Settings Parse(XElement root)
        {
            static double D(XElement? e, string a, double d) => double.TryParse((string?)e?.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
            static int I(XElement? e, string a, int d) => int.TryParse((string?)e?.Attribute(a), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
            var g = root.Element("globalsettings");
            var events = root.Element("events");
            return new Settings(D(g, "lowvaluethreshhold", 0.25), D(g, "mediumvaluethreshhold", 0.5), D(g, "highvaluethreshhold", 0.75), I(events, "upcomingeventtimeframeinsec", 300),
                [.. (events?.Elements("eventgroup") ?? []).Select(e => new EventGroup((string?)e.Attribute("clustertype") ?? "", I(e, "tier", 0), I(e, "mincooldown", 0), I(e, "maxcooldown", 0),
                    [.. e.Elements("event").Select(x => new EventDef((string?)x.Attribute("name") ?? "", I(x, "weight", 0), I(x, "rerollnumber", 1), I(x, "duration", 0),
                        (string?)x.Attribute("rarity") == "rare" ? (byte)2 : (byte)1))]))]);
        }
    }

    /// <summary>A scheduled event: GameTimeStamps (ticks) of its start and end.</summary>
    public readonly record struct Event(long Start, long End, EventDef Def);

    private Dictionary<Guid, Dungeon>? _dungeons;
    private Dictionary<string, Dungeon>? _byCluster;
    private Settings? _settings;
    private bool _settingsTried;

    /// <summary>staticdungeons.xml; null when it is missing or unreadable (no events, the default thresholds).</summary>
    public Settings? Data
    {
        get
        {
            if (_settingsTried) return _settings;
            _settingsTried = true;
            if (world.Clusters?.Folder is not { } folder) return null;
            try { _settings = Settings.Parse(GameDataFile.Open(folder, "staticdungeons.bin").Root()); }
            catch (Exception e) { Log.Warn("world", $"staticdungeons.bin not read, static dungeons without events: {e.Message}"); }
            return _settings;
        }
        init { _settings = value; _settingsTried = true; }
    }

    /// <summary>The static dungeons by id, from world.xml once it is loaded (empty before).</summary>
    public IReadOnlyDictionary<Guid, Dungeon> Dungeons => Index() ? _dungeons! : new Dictionary<Guid, Dungeon>();

    /// <summary>The dungeon cluster <paramref name="cluster"/> belongs to; null for any other cluster.</summary>
    public Dungeon? Of(string? cluster) => cluster is not null && Index() && _byCluster!.TryGetValue(cluster, out var d) ? d : null;

    /// <summary>The dungeons with an entrance in <paramref name="cluster"/> (each once, in exit order).</summary>
    public IEnumerable<Dungeon> EnteredFrom(ClusterInfo cluster)
    {
        if (!Index()) yield break;
        var seen = new HashSet<Guid>();
        foreach (var e in cluster.Exits)
            if (DungeonId(e) is { } id && _dungeons!.TryGetValue(id, out var d) && seen.Add(id)) yield return d;
    }

    private static Guid? DungeonId(WorldExit e) =>
        e.Attrs.TryGetValue("staticdungeonid", out var s) && Guid.TryParse(s, out var g) && g != Guid.Empty ? g : null;

    private bool Index()
    {
        if (_dungeons is not null) return true;
        if (world.Clusters?.WorldIfLoaded is not { } info) return false;
        var dungeons = new Dictionary<Guid, Dungeon>();
        var entrances = info.Clusters.SelectMany(c => c.Exits.Where(e => DungeonId(e) is not null).Select(e => (Id: DungeonId(e)!.Value,
            Entrance: new Entrance(c.Id, e.Id, e.Attrs.GetValueOrDefault("staticdungeonentrance") == "Main")))).ToLookup(x => x.Id, x => x.Entrance);
        foreach (var group in info.Clusters.Where(c => c.Attrs.ContainsKey("belongstostaticdungeonid"))
                     .GroupBy(c => Guid.TryParse(c.Attrs["belongstostaticdungeonid"], out var g) ? g : Guid.Empty).Where(g => g.Key != Guid.Empty))
        {
            var first = group.First();
            var tier = ClusterName.Parse(first.File).Tier ?? 0;
            dungeons[group.Key] = new Dungeon(group.Key, first.Type, tier, [.. group.Select(c => c.Id)], [.. entrances[group.Key]]);
        }
        _byCluster = dungeons.Values.SelectMany(d => d.Clusters.Select(c => (c, d))).ToDictionary(x => x.c, x => x.d, StringComparer.Ordinal);
        _dungeons = dungeons;
        return true;
    }

    /// <summary>The event group of <paramref name="d"/>'s clustertype and tier with at least one event; null = none.</summary>
    public EventGroup? GroupOf(Dungeon d) => Data?.Groups.FirstOrDefault(g => g.ClusterType == d.Type && g.Tier == d.Tier && g.Events.Any(e => e.Weight > 0 && e.DurationSeconds > 0));

    /// <summary>The event of <paramref name="d"/>'s schedule slot at <paramref name="ticks"/> (whether it has begun or not); null without an event group.</summary>
    public Event? SlotEvent(Dungeon d, long ticks, int slotOffset = 0)
    {
        if (GroupOf(d) is not { } g) return null;
        var events = g.Events.Where(e => e.Weight > 0 && e.DurationSeconds > 0).ToList();
        var min = Math.Max(0, Math.Min(g.MinCooldown, g.MaxCooldown));
        var max = Math.Max(g.MinCooldown, g.MaxCooldown);
        var period = (min + max) / 2 + events.Max(e => e.DurationSeconds);
        var jitter = (max - min) / 2;
        var bytes = d.Id.ToByteArray();
        var phase = (long)(BitConverter.ToUInt32(bytes, 0) % (uint)period);
        var seconds = ticks / TimeSpan.TicksPerSecond;
        var slot = (long)Math.Floor((seconds - phase) / (double)period) + slotOffset;
        var rng = new Random(unchecked((int)(BitConverter.ToUInt32(bytes, 4) ^ BitConverter.ToUInt32(bytes, 8) ^ (uint)slot * 2654435761u)));
        var start = phase + slot * period + rng.Next(0, jitter + 1);
        var total = events.Sum(e => e.Weight);
        var roll = rng.Next(total);
        var def = events.First(e => (roll -= e.Weight) < 0);
        return new Event(start * TimeSpan.TicksPerSecond, (start + def.DurationSeconds) * TimeSpan.TicksPerSecond, def);
    }

    /// <summary>The event of <paramref name="d"/> running at <paramref name="ticks"/> (default now); null = none. A GM's <see cref="Force"/> goes first while it runs.</summary>
    public Event? Current(Dungeon d, long? ticks = null)
    {
        var now = ticks ?? world.GameNow;
        if (_forced.TryGetValue(d.Id, out var forced))
        {
            if (forced.Start <= now && now < forced.End) return forced;
            if (now >= forced.End) _forced.Remove(d.Id);
        }
        return SlotEvent(d, now) is { } e && e.Start <= now && now < e.End ? e : null;
    }

    private readonly Dictionary<Guid, Event> _forced = [];

    /// <summary>
    /// A GM's .sd event: an event of <paramref name="d"/> from now for <paramref name="minutes"/> over its schedule (not saved), the first standard
    /// (not rare) event of its group by weight (a made-up "GM_EVENT" without a group).
    /// </summary>
    public Event Force(Dungeon d, double minutes)
    {
        var now = world.GameNow;
        var def = GroupOf(d)?.Events.Where(e => e.Weight > 0 && e.DurationSeconds > 0).OrderBy(e => e.Rarity).ThenByDescending(e => e.Weight).FirstOrDefault()
            ?? new EventDef("GM_EVENT", 1, 2, (int)(minutes * 60), 1);
        var e = new Event(now, now + (long)(minutes * 60 * TimeSpan.TicksPerSecond), def);
        _forced[d.Id] = e;
        return e;
    }

    /// <summary>The running event, else the next one when it starts within upcomingeventtimeframeinsec (300 s); null = neither.</summary>
    public Event? CurrentOrUpcoming(Dungeon d, long? ticks = null)
    {
        var now = ticks ?? world.GameNow;
        if (Current(d, now) is { } running) return running;
        var window = (Data?.UpcomingSeconds ?? 300) * TimeSpan.TicksPerSecond;
        foreach (var offset in new[] { 0, 1 })
            if (SlotEvent(d, now, offset) is { } e && e.Start > now && e.Start - now <= window) return e;
        return null;
    }

    /// <summary>The share of living mobs of <paramref name="d"/>'s loaded maps, 1 when none is loaded or they have no spawn points.</summary>
    public double Fill(Dungeon d)
    {
        int slots = 0, alive = 0;
        foreach (var id in d.Clusters)
            if (world.Maps.Find(id) is { } map)
                foreach (var s in map.MobSlots)
                {
                    slots++;
                    if (s.Mob is { Dead: false, Removed: false }) alive++;
                }
        return slots == 0 ? 1 : alive / (double)slots;
    }

    /// <summary>StaticDungeonValueInfo of <paramref name="d"/>: VERY_LOW 1, LOW 2, MEDIUM 3, HIGH 4 by globalsettings' thresholds.</summary>
    public byte Value(Dungeon d)
    {
        var fill = Fill(d);
        var s = Data;
        return fill < (s?.Low ?? 0.25) ? (byte)1 : fill < (s?.Medium ?? 0.5) ? (byte)2 : fill < (s?.High ?? 0.75) ? (byte)3 : (byte)4;
    }

    /// <summary>How often a chest made on map <paramref name="cluster"/> draws its rare state: its dungeon's running event's rerollnumber, else 1.</summary>
    public int ChestRolls(string cluster) => Of(cluster) is { } d && Current(d) is { } e ? Math.Max(1, e.Def.Rerolls) : 1;

    /// <summary>
    /// The 499 of <paramref name="dungeons"/>: per dungeon its id, the running or upcoming event's start and end (0 and 0 = none) and
    /// rarity, and its value. Null for none.
    /// </summary>
    public StaticDungeonEntrancesDungeonEventStatusUpdatesEvent? StatusOf(IReadOnlyCollection<Dungeon> dungeons)
    {
        if (dungeons.Count == 0) return null;
        var events = dungeons.Select(d => CurrentOrUpcoming(d)).ToList();
        return new StaticDungeonEntrancesDungeonEventStatusUpdatesEvent
        {
            d0c = [.. dungeons.Select(d => d.Id)],
            d0d = [.. events.Select(e => e?.Start ?? 0)],
            d0e = [.. events.Select(e => e?.End ?? 0)],
            d0f = [.. events.Select(e => e?.Def.Rarity ?? 0)],
            d0g = [.. dungeons.Select(Value)],
        };
    }

    /// <summary>After a Join: 499 for the dungeon the player is in and the dungeons with an entrance in its cluster.</summary>
    public void Joined(Player p)
    {
        if (p.Map?.Cluster is not { } cluster || world.Clusters?.WorldIfLoaded?.Find(cluster) is not { } info) return;
        var list = new List<Dungeon>();
        if (Of(cluster) is { } own) list.Add(own);
        list.AddRange(EnteredFrom(info).Where(d => !list.Contains(d)));
        if (StatusOf(list) is { } ev) p.Session.Send(ev);
    }
}
