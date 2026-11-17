using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The world bosses (analysis/work/stageH/bosses/SPEC.md §4.1, §6.1, "SPEC" here; PVE-DESIGN.md §2.E, §3 step 6), with content/bosses.json
/// (<see cref="BossSettings"/>) and worldbosses.xml (<see cref="WorldBossData"/>, read at the start):
/// <list type="bullet">
/// <item>Slots: every WORLDBOSS spawn point of a cluster (its worldboss attribute names the worldbosses.xml boss, whose mob it is; 1007,
/// 2009, 0005 and the black zones of SPEC §1.2) and the file's ExtraSpawns of the cluster, made at the cluster's first map fill and kept
/// here, not on the map: an unloaded map loses its boss mob, not the slot's state, and the next map of the cluster gets the boss again
/// (full bars) if it was standing, else at its time. A name or mob the data does not have is logged once and skipped.</item>
/// <item>A boss is a normal mob on the wire (NewMob; SPEC §3.8: no boss bar, no spawn event, the client's world map shows the data
/// schedule itself), with the point's GUARD behaviour, its max health × HealthFactor (NewMob key 14) and its damage × DamageFactor.
/// It stands from the map's first fill (SpawnAtStart), else comes at its schedule's first time; after its death (or a despawn) it comes
/// back by its schedule: the next data spawn time (UTC, the day of the cycle from startdate), uniform(respawn min, max) ÷ Rates:Respawn
/// (the mob's respawntimeseconds, an extra spawn's RespawnMinutes), or the first of the two. A boss still standing at a spawn time stays
/// [assume, SPEC §10 Q7].</item>
/// <item>Its timed spawns, GM spawns and deaths go out as a system chat line (SystemMessage, as the GM command replies; the client cannot
/// show an out-of-view boss on the map, SPEC §10 Q1, so the line has the position) to the players in its cluster (SPEC §4.1), or with
/// AnnounceToAll on to every player in game.</item>
/// <item>Its loot (the T0_LOOTCHEST_RAIDBOSS bag of the normal kill path, <see cref="Combat.Kill"/>): Shared (default), the one bag of
/// one roll that every contributor (a threat share of at least LootMinShare when it died, the killer always) may open; Personal, a roll
/// of its own for every contributor (a departure: N contributors make N times the items); Killer, the official bag of the killer and
/// its party.</item>
/// </list>
/// Not handled (departures): the boss enrage, summon, pull and knockback spells (stage 5f.4, SPEC §1.1), the death spells' fame buff, a
/// presence at the start decided by the last data time. The time is <see cref="World.GameNow"/> (the UTC time; the self-test's manual
/// clock). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class WorldBosses(World world)
{
    /// <summary>A world boss spot: its boss and mob, where it stands, how it comes back, and its state.</summary>
    public sealed class Slot
    {
        public required SpawnKey Key { get; init; }
        public required WorldBossType Boss { get; init; }
        public required MobTemplate Template { get; init; }

        /// <summary>The spawn point the mob stands on (the template's WORLDBOSS point, or one made for an extra spawn).</summary>
        public required MobSpawnPoint Point { get; init; }

        public required BossSchedule Schedule { get; init; }

        /// <summary>An extra spawn's RespawnMinutes; null: the mob's respawntimeseconds.</summary>
        public (double Min, double Max)? RespawnMinutes { get; init; }

        public double HealthFactor { get; init; } = 1;
        public double DamageFactor { get; init; } = 1;

        /// <summary>From content/bosses.json ExtraSpawns, not from the client data.</summary>
        public bool Extra { get; init; }

        /// <summary>A GM boss (.boss spawn … here): announced, never back.</summary>
        public bool OneOff { get; init; }

        /// <summary>Whether the boss stands (it is spawned when its map is there).</summary>
        public bool Present { get; internal set; }

        /// <summary>The living boss on its map; null while it waits or its map is not loaded.</summary>
        public Mob? Mob { get; internal set; }

        /// <summary>When it comes (UTC) while it does not stand; null while it stands (or never, a one-off).</summary>
        public DateTime? NextSpawn { get; internal set; }

        public DateTime? LastKill { get; internal set; }

        /// <summary>When the standing boss came (UTC; the fight length in the kill log line).</summary>
        public DateTime? SpawnedAt { get; internal set; }

        public string Cluster => Key.Cluster;

        public override string ToString() => $"{Boss.Name} ({Template.Name}) at {Key}";
    }

    private readonly Dictionary<string, List<Slot>> _clusters = new(StringComparer.Ordinal);
    private Random? _random;

    private BossSettings Settings => world.BossSettings;

    private Random Random => _random ??= world.MapSeed is { } seed ? new Random(seed) : new Random();

    /// <summary>Now in UTC, on the world's GameTimeStamp (the self-test's manual clock moves it).</summary>
    public DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>The boss slots of <paramref name="cluster"/> (empty before its first map fill).</summary>
    public IReadOnlyList<Slot> SlotsOf(string cluster) => _clusters.TryGetValue(cluster, out var l) ? l : [];

    /// <summary>Every slot made so far (the clusters filled since the start).</summary>
    public IEnumerable<Slot> All => _clusters.Values.SelectMany(l => l);

    /// <summary>
    /// At a map's fill (MapSpawner, after the mobs): the cluster's slots, made the first time, and the bosses that stand, not yet on the map.
    /// </summary>
    public List<Mob> Populate(Map map, ClusterTemplate template)
    {
        var made = new List<Mob>();
        if (!Settings.Enabled || DungeonLayout.IsInstance(map.Cluster) || ExpeditionData.IsInstance(map.Cluster) || CorruptedData.IsInstance(map.Cluster) || HellgateData.IsInstance(map.Cluster)
            || MistsIds.IsInstance(map.Cluster) || DragonAreaIds.IsInstance(map.Cluster) || HellDungeonIds.IsInstance(map.Cluster)) return made;
        if (!_clusters.TryGetValue(map.Cluster, out var slots))
        {
            slots = MakeSlots(map.Cluster, template);
            if (slots.Count == 0) return made;
            _clusters[map.Cluster] = slots;
            var now = UtcNow;
            foreach (var s in slots)
            {
                s.Present = Settings.SpawnAtStart;
                if (!s.Present) s.NextSpawn = Next(s, now);
            }
            Log.Info("world", $"map {map.Cluster}: {slots.Count} world boss slot(s): {string.Join(", ", slots.Select(s => s.Boss.Name + (s.Extra ? " (extra)" : "")))}");
        }
        foreach (var s in slots)
        {
            s.Mob = null; // a mob of an unloaded map of the cluster is gone with it
            if (s.Present) made.Add(Spawn(map, s, template.Name));
        }
        return made;
    }

    private List<Slot> MakeSlots(string cluster, ClusterTemplate template)
    {
        var slots = new List<Slot>();
        if (world.BossData is not { } data || world.Clusters?.MobsIfLoaded is not { } mobs)
        {
            if (template.MobSpawnPoints.Any(p => p.MobName == MobSpawnPoint.WorldBossName) || Settings.ExtraSpawns.Any(e => e.Cluster == cluster))
                Combat.LogOnce("bosses:nodata", $"world bosses: no {Settings.DataFile}.bin or mobs.xml, the bosses stay out");
            return slots;
        }
        (WorldBossType, MobTemplate)? Resolve(string name)
        {
            if (data.Find(name) is not { } boss) { Combat.LogOnce("bosses:" + name, $"world boss {name} is not in {Settings.DataFile}.xml: its spawn point stays empty"); return null; }
            if (mobs.Find(boss.Mob) is not { } mob) { Combat.LogOnce("bosses:" + boss.Mob, $"world boss {name}: mob {boss.Mob} is not in mobs.xml"); return null; }
            return (boss, mob);
        }
        foreach (var p in template.MobSpawnPoints.Where(p => p.MobName == MobSpawnPoint.WorldBossName))
        {
            if (p.WorldBoss is not { } name || Resolve(name) is not var (boss, mob)) continue;
            slots.Add(new Slot
            {
                Key = p.Key, Boss = boss, Template = mob, Point = p with { Mob = mob.Name, Template = mob }, Schedule = Settings.Schedule,
                HealthFactor = Settings.HealthFactor, DamageFactor = Settings.DamageFactor,
            });
        }
        var extras = Settings.ExtraSpawns.Where(e => e.Cluster == cluster).ToList();
        for (var i = 0; i < extras.Count; i++)
        {
            var e = extras[i];
            if (Resolve(e.Boss) is not var (boss, mob)) continue;
            slots.Add(new Slot
            {
                Key = new SpawnKey(cluster, "boss", i), Boss = boss, Template = mob, Point = MakePoint(new SpawnKey(cluster, "boss", i), mob, e.Position, e.Direction),
                Schedule = e.Schedule, RespawnMinutes = e.RespawnMinutes, HealthFactor = e.HealthFactor ?? Settings.HealthFactor,
                DamageFactor = e.DamageFactor ?? Settings.DamageFactor, Extra = true,
            });
        }
        return slots;
    }

    /// <summary>A server-made GUARD point for <paramref name="mob"/> (an extra spawn, a GM boss).</summary>
    private static MobSpawnPoint MakePoint(SpawnKey key, MobTemplate mob, Vector2 at, float direction) => new()
    {
        Key = key, Tile = "", Position = at, Direction = (float)TileMath.Mod360(direction), Behavior = "GUARD",
        Component = new XElement("mobspawnpoint", new XAttribute("mobname", mob.Name)), Mob = mob.Name, Template = mob,
    };

    /// <summary>A new boss mob for <paramref name="slot"/> with its factors and full (scaled) health; not yet on the map.</summary>
    private Mob Spawn(Map map, Slot slot, ClusterName? cluster)
    {
        var t = slot.Template;
        var mob = new Mob(world.ObjectIds.Next(), slot.Point, t, Mob.FlaggingOf(t, slot.Point.Tile, cluster ?? map.Template?.Name), world.GameNow, world.Now)
        {
            World = world, Boss = slot, HealthFactor = slot.HealthFactor, DamageFactor = slot.DamageFactor,
        };
        var now = world.GameNow;
        mob.Health.Fill(SpellEffects.MobMaxHealth(mob, now), t.HitPointsRegeneration, now);
        slot.Mob = mob;
        slot.Present = true;
        slot.NextSpawn = null;
        slot.SpawnedAt = UtcNow;
        return mob;
    }

    /// <summary>
    /// The world tick: a boss whose time has come stands (on its map when that is loaded, announced either way), a despawned one (.mob kill)
    /// waits for its next time.
    /// </summary>
    public void Update()
    {
        if (_clusters.Count == 0) return;
        var now = UtcNow;
        foreach (var (cluster, slots) in _clusters)
        {
            var map = world.Maps.Find(cluster);
            var ready = map is { Template: not null, Grid: not null, Unloaded: false };
            foreach (var s in slots)
            {
                if (s.Mob is { } m && (m.Map != map || m.Removed))
                {
                    s.Mob = null;
                    if (m.Removed && !m.Dead && m.Map == map) Gone(s, now); // taken off its live map (.mob kill): a despawn
                }
                if (!s.Present && s.NextSpawn is { } due && now >= due)
                {
                    s.Present = true;
                    s.NextSpawn = null;
                    if (ready) map!.AddObject(Spawn(map, s, null));
                    Announce(s, Fill(Settings.AnnounceSpawn, s, null));
                    Log.Info("world", $"world boss {s.Boss.Name} spawned in {cluster}{(ready ? "" : " (its map is not loaded: it stands when it is)")}");
                }
                else if (s.Present && s.Mob is null && ready) map!.AddObject(Spawn(map, s, null)); // present while the map was being filled
            }
        }
    }

    /// <summary>
    /// The players who share a boss's loot (<see cref="Combat.Kill"/>, before its death clears its threat): those on its threat list with at
    /// least LootMinShare of the total, on its map, and the killer. Null for a mob that is no boss or with Loot Killer: the official bag.
    /// </summary>
    public List<Player>? Looters(Mob mob, Player killer)
    {
        if (mob.Boss is null || Settings.Loot == BossLoot.Killer) return null;
        var total = mob.Threat.Values.Where(v => v > 0).Sum();
        var list = mob.Threat.Where(kv => kv.Value > 0 && kv.Value >= Settings.LootMinShare * total && kv.Key.Map == mob.Map).Select(kv => kv.Key).ToList();
        if (!list.Contains(killer)) list.Insert(0, killer);
        return list;
    }

    /// <summary>Whether a boss's looters each roll their own bag (Loot Personal) rather than share one.</summary>
    public bool PersonalLoot => Settings.Loot == BossLoot.Personal;

    /// <summary>After a boss's kill batch (<see cref="Combat.Kill"/>): its slot waits for its next time, and the death is announced.</summary>
    public void Killed(Mob mob)
    {
        if (mob.Boss is not { } slot) return;
        if (slot.Mob == mob) slot.Mob = null;
        var now = UtcNow;
        Gone(slot, now);
        slot.LastKill = now;
        Announce(slot, Fill(slot.NextSpawn is null ? Settings.AnnounceKillOnce : Settings.AnnounceKill, slot, slot.NextSpawn));
        var after = slot.SpawnedAt is { } at ? string.Create(CultureInfo.InvariantCulture, $" after {(now - at).TotalSeconds:0} s") : "";
        Log.Info("world", $"world boss {slot.Boss.Name} killed in {slot.Cluster}{after}; next spawn {(slot.NextSpawn is { } next ? next.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "never")}");
    }

    /// <summary>The boss of <paramref name="slot"/> is gone (killed or despawned): it waits for its next time.</summary>
    private void Gone(Slot slot, DateTime now)
    {
        slot.Present = false;
        slot.NextSpawn = slot.OneOff ? null : Next(slot, now);
    }

    /// <summary>
    /// When <paramref name="slot"/> comes after <paramref name="now"/>: the next data time, the respawn delay (÷ Rates:Respawn), or the
    /// first of the two (the data time when the respawn has none, and the other way round).
    /// </summary>
    public DateTime? Next(Slot slot, DateTime now)
    {
        DateTime? data = slot.Schedule is BossSchedule.Data or BossSchedule.Both ? slot.Boss.NextSpawn(now) : null;
        DateTime? respawn = null;
        if (slot.Schedule is BossSchedule.Respawn or BossSchedule.Both)
        {
            var (min, max) = slot.RespawnMinutes is { } m ? (m.Min * 60, m.Max * 60) : (slot.Template.RespawnSecondsMin, (double)slot.Template.RespawnSecondsMax);
            if (max < min) max = min;
            respawn = now + TimeSpan.FromSeconds(Math.Max(0, (min + (max - min) * Random.NextDouble()) / world.Rates.Respawn));
        }
        return data is { } d && respawn is { } r ? (d < r ? d : r) : data ?? respawn;
    }

    /// <summary>
    /// .boss spawn: the boss of a slot of the player's cluster now (<paramref name="slot"/> not standing), announced; with
    /// <paramref name="at"/> instead a one-off GM boss of <paramref name="boss"/> and <paramref name="mob"/> there, which never comes back.
    /// </summary>
    public Mob SpawnNow(Map map, Slot? slot, WorldBossType? boss, MobTemplate mob, Vector2 at, float direction)
    {
        if (slot is null)
        {
            var key = new SpawnKey(map.Cluster, "gmboss", map.CustomSpawns++);
            slot = new Slot
            {
                Key = key, Boss = boss ?? new WorldBossType(mob.Name, mob.Name, DateTime.MinValue, null, new Dictionary<int, TimeSpan[]>()), Template = mob,
                Point = MakePoint(key, mob, at, direction), Schedule = BossSchedule.Respawn, HealthFactor = Settings.HealthFactor,
                DamageFactor = Settings.DamageFactor, OneOff = true,
            };
        }
        else if (slot.Mob is { } old && !old.Removed) throw new InvalidOperationException($"{slot.Boss.Name} stands already ({old})");
        var made = Spawn(map, slot, null);
        map.AddObject(made);
        Announce(slot, Fill(Settings.AnnounceSpawn, slot, null));
        return made;
    }

    /// <summary>.boss kill: <paramref name="mob"/> off its map (a despawn: no corpse, loot or fame) and its slot waits for its next time.</summary>
    public void Despawn(Mob mob)
    {
        SpawnManager.Despawn(mob, world);
        if (mob.Boss is not { } slot) return;
        if (slot.Mob == mob) slot.Mob = null;
        Gone(slot, UtcNow);
    }

    /// <summary>
    /// The announcement to the players in the boss's cluster (or in game, AnnounceToAll: on the chat server's Announcement channel to
    /// those who joined it, <see cref="ChatHub.Announce"/>), logged; nothing with Announce off.
    /// </summary>
    private void Announce(Slot slot, string text)
    {
        if (!Settings.Announce || text.Length == 0) return;
        Log.Info("world", $"announce: {text}");
        var e = new SystemMessageEvent { d0p = text.StartsWith('@') ? " " + text : text, d0q = 0 };
        var readers = world.Maps.Maps.Where(m => Settings.AnnounceToAll || m.Cluster == slot.Cluster).SelectMany(m => m.Players.Values);
        foreach (var p in Settings.AnnounceToAll ? world.Chat.Announce(text, readers) : readers) p.Session.Send(e);
    }

    private static string Fill(string text, Slot slot, DateTime? next) => text
        .Replace("{boss}", DisplayName(slot.Template), StringComparison.Ordinal)
        .Replace("{cluster}", slot.Cluster, StringComparison.Ordinal)
        .Replace("{x}", slot.Point.Position.X.ToString("0", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{z}", slot.Point.Position.Y.ToString("0", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace("{next}", next is { } n ? n.ToString("HH:mm", CultureInfo.InvariantCulture) : "never", StringComparison.Ordinal);

    /// <summary>
    /// A readable name of the boss's mob for the chat line (the client localises only a whole line that starts with '@', so the
    /// mob's @MOB_ name cannot be put in a sentence): T5_MOB_KEEPER_SEER_ELITE_BOSS → "Keeper Seer (T5)".
    /// </summary>
    public static string DisplayName(MobTemplate mob)
    {
        var parts = mob.Name.Split('_').ToList();
        if (parts.Count > 0 && parts[0].Length is 2 && parts[0][0] == 'T' && char.IsDigit(parts[0][1])) parts.RemoveAt(0);
        if (parts.Count > 0 && parts[0] == "MOB") parts.RemoveAt(0);
        while (parts.Count > 1 && parts[^1] is "BOSS" or "ELITE" or "VETERAN") parts.RemoveAt(parts.Count - 1);
        var words = string.Join(' ', parts.Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
        return mob.Tier > 0 ? $"{words} (T{mob.Tier})" : words;
    }

    /// <summary>.boss list: the slots of <paramref name="cluster"/> (every cluster's with null), one line each.</summary>
    public IEnumerable<string> Describe(string? cluster)
    {
        var now = UtcNow;
        foreach (var s in cluster is null ? All : SlotsOf(cluster))
        {
            var state = s.Mob is { Dead: false, Removed: false } m
                ? string.Create(CultureInfo.InvariantCulture, $"stands, {m.Health.At(world.GameNow):0} / {m.Health.Max:0} hp, #{m.Id}")
                : s.Present ? "stands when its map is loaded"
                : s.NextSpawn is { } next ? string.Create(CultureInfo.InvariantCulture, $"next {next:yyyy-MM-dd HH:mm:ss} UTC (in {Math.Max(0, (next - now).TotalMinutes):0.#} min)")
                : "never back";
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {s.Boss.Name} {s.Template.Name} at ({s.Point.Position.X:0.##}, {s.Point.Position.Y:0.##}){(s.Extra ? " extra" : "")}, {s.Schedule.ToString().ToLowerInvariant()}, health x{s.HealthFactor}, damage x{s.DamageFactor}: {state}");
        }
    }
}
