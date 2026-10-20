using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server;

/// <summary>
/// One mob spawn point of a map (ARCHITECTURE.md §4.2, §5 SpawnManager; stage 5 SPEC §5a.2): the mob it has now, or when
/// the next one comes. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class MobSpawnSlot(MobSpawnPoint point)
{
    public MobSpawnPoint Point { get; } = point;

    /// <summary>The living mob of the point; null while it waits for <see cref="RespawnAt"/>.</summary>
    public Mob? Mob { get; internal set; }

    /// <summary><see cref="World.Clock"/> time the point spawns again; null while it has a mob.</summary>
    public TimeSpan? RespawnAt { get; internal set; }

    /// <summary>StageL roads (SPEC §5.5): the level of the road chest the point guards (an allowUpgradeByChest block); null = its lowest (no chest in reach).</summary>
    public int? ChestLevel { get; internal set; }

    public override string ToString() => $"{Point.Key} {Point.MobName}{(Point.SpawnBehavior is { } b ? $" ({b})" : "")}";
}

/// <summary>
/// The mobs of a map (stage 5a, analysis/work/stage5/SPEC.md §5a; ARCHITECTURE.md §4.4 step 4, "respawns"): one
/// <see cref="MobSpawnSlot"/> per spawn point of the cluster, the template's own and those of the resource layout's prefabs
/// (stage 4b: mob camps, roaming mobs, hide and fiber critters), each with a mob at the map's creation. A named point
/// spawns its mobs.xml mob; a RANDOM point a weighted pick from the spawn behaviour block bound to it (§5a.2), again at every
/// respawn; WORLDBOSS points are left to <see cref="Systems.WorldBosses"/> (stageH), and a mob name mobs.xml does not have is logged once per cluster (<see cref="ClusterStore.Get"/>) and
/// its points skipped. GUARD, PATROL and animswitch points stand like the others for now (NullAI). A mob taken off its
/// point (<see cref="Despawn"/>: .mob kill, and a killed mob after its corpse time, <see cref="Systems.Combat"/>) leaves every client that knows it (Leave) and
/// the point respawns after uniform(min, max) seconds (the block's for a RANDOM point, the mob's otherwise) divided by
/// <see cref="Game.Rates.Respawn"/>. Respawn times are clock times kept on the map, run by the world tick also while
/// no player is near; an unloaded map takes them with it (the next map spawns every point anew). Touched only under
/// <see cref="World.Sync"/>.
/// </summary>
public static class SpawnManager
{
    /// <summary>
    /// The map's spawn points and their first mobs (<see cref="MapSpawner.Populate"/>, before the map takes its objects):
    /// the mobs are returned for the map to put into its grid; the counts and what was skipped are logged.
    /// </summary>
    public static List<Mob> Start(Map map, ClusterTemplate template, World world)
    {
        var mobs = new List<Mob>();
        var empty = 0;
        var points = template.MobSpawnPoints.Concat(template.Resources?.MobSpawnPoints ?? []);
        foreach (var point in points)
        {
            switch (point.MobName)
            {
                case null: continue;
                case MobSpawnPoint.WorldBossName: continue; // stageH: Systems.WorldBosses (its own slots and schedule)
                // Unknown names and RANDOM points without a block: logged once per cluster (ClusterStore.Get), skipped here.
                case MobSpawnPoint.Random when point.Block is null: continue;
                case not MobSpawnPoint.Random when point.Template is null: continue;
            }
            // StageL corrupted (SPEC §1.2): the demon, the shards and the anti-reset imps never spawn by timer; the instance places them.
            if (point.SpawnBehavior is { } behaviour && map.ScriptedSpawns.Contains(behaviour)) continue;
            var slot = new MobSpawnSlot(point) { ChestLevel = world.WorldChests.PlannedLevel(map, point) }; // stageL roads: the camp guards the chest's level
            map.MobSlots.Add(slot);
            if (Spawn(map, slot, world, template.Name) is { } mob) mobs.Add(mob);
            else
            {
                empty++;
                slot.RespawnAt = world.Now + RespawnDelay(map, slot, null, world);
            }
        }
        if (empty > 0) Log.Warn("world", $"map {map.Cluster}: {empty} RANDOM spawn point(s) whose block has no mob to pick, tried again at their respawn");
        return mobs;
    }

    /// <summary>The world tick (every map, with or without players): the points whose respawn time has come get their mob.</summary>
    public static void Update(Map map, World world, TimeSpan now)
    {
        if (map.Grid is null) return;
        foreach (var slot in map.MobSlots)
        {
            if (slot.Mob is not null || slot.RespawnAt is not { } due || now < due) continue;
            slot.RespawnAt = null;
            if (Spawn(map, slot, world) is { } mob)
            {
                map.AddObject(mob);
                world.WorldChests.MobSpawned(map, mob); // stageH: a protecting mob back locks its POI chest again
            }
            else slot.RespawnAt = now + RespawnDelay(map, slot, null, world);
        }
    }

    /// <summary>
    /// A new mob for <paramref name="slot"/> (a RANDOM point picks again) with a new object id, full bars and the point's
    /// position and rotation; not yet on the map. Null when a RANDOM block has nothing to pick. <paramref name="cluster"/>:
    /// the map's cluster name while the map has no template yet (its first spawn).
    /// </summary>
    public static Mob? Spawn(Map map, MobSpawnSlot slot, World world, ClusterName? cluster = null)
    {
        var point = slot.Point;
        // stageH: Rates:ChampionWeight on the elites; stageL roads (SPEC §5.5 rule 1): a road camp point the entries of its chest's level, else its lowest.
        var template = point.Template ?? (point.Block is { AllowUpgradeByChest: true } camp && world.Config.Roads.Chests
            ? camp.PickAt(map.Random, slot.ChestLevel ?? camp.LowestLevel, world.Rates.ChampionWeight) : point.Block?.Pick(map.Random, world.Rates.ChampionWeight));
        if (template is null) return null;
        var variant = world.ModDrops.ReplaceFor(template.Name, map.Random); // a content pack's mob variant turns up instead now and then (studio phase 3)
        var mob = new Mob(world.ObjectIds.Next(), point, template, Mob.FlaggingOf(template, point.Tile, cluster ?? map.Template?.Name), world.GameNow, world.Now)
        {
            Slot = slot, World = world, HealthFactor = map.MobHealthFactor * (variant?.HealthFactor ?? 1), DamageFactor = map.MobDamageFactor * (variant?.DamageFactor ?? 1), // stageH: an expedition's group scaling
            FameFactor = variant?.FameFactor ?? 1, Variant = variant?.Id,
        };
        if (variant is not null) Log.Info("mods", $"pack {variant.Pack}: {template.Name} spawns as the variant {variant.Id}");
        slot.Mob = mob;
        LevelBuff(map, mob, world);
        if (mob.LevelBuff is null && mob.HealthFactor != 1) mob.Health.Fill(Systems.SpellEffects.MobMaxHealth(mob, world.GameNow), template.HitPointsRegeneration, world.GameNow);
        return mob;
    }

    /// <summary>
    /// StageG dungeons (SPEC §7): on a floor of level 1-4 a mob carries the level's mobmodifier (DifficultyLevels default,
    /// MOB_RANDOM_DUNGEON_BUFF_0n: buffovertime hitpointsmaxbonus and bonusdamagevsplayers, time 0) from its spawn, as an active spell
    /// of its list (sent after NewMob), with its health full at the buffed maximum (not with <paramref name="fill"/> false). A reset keeps it
    /// through its walk home (keeponretreat, AggressiveAI). Nothing at level 0, off a dungeon floor or without the spell.
    /// </summary>
    internal static void LevelBuff(Map map, Mob mob, World world, bool fill = true)
    {
        var spell = mob.LevelBuff ?? (map.Difficulty?.MobModifier is { } name ? world.Spells?.Template(name) : null);
        if (spell is null) return;
        var now = Math.Max(world.GameNow, mob.Health.Stamp);
        var subs = spell.Effects.Select((node, i) => Systems.SpellEffects.Make(world, mob, 100, 100, node, i, mob, now)).ToArray();
        Systems.SpellEffects.Apply(world, mob, spell, 0, 100, 100, now, subs);
        mob.LevelBuff = spell;
        if (fill) mob.Health.Fill(Systems.SpellEffects.MobMaxHealth(mob, now), mob.Template.HitPointsRegeneration, now);
    }

    /// <summary>
    /// A GM mob (.mob spawn): <paramref name="template"/> at <paramref name="position"/> facing <paramref name="direction"/>, put
    /// on the map at once (NewMob to the players in view), with a spawn key of its own ("&lt;cluster&gt;:custom:&lt;n&gt;") and
    /// no spawn point: it never respawns. <paramref name="behavior"/> and <paramref name="waypoints"/>: a point's idle
    /// behaviour (GUARD, PATROL with its waypoints; stage 5d), none by default. <paramref name="healthFactor"/>,
    /// <paramref name="damageFactor"/> and <paramref name="worldEvent"/>: a world event's wave mob (<see cref="Systems.WorldEvents"/>);
    /// stage I: <paramref name="levelBuff"/> (a T9/T10 invasion's MOB_RANDOM_DUNGEON_BUFF_0n, applied as a dungeon floor's before
    /// NewMob goes out, its health full at the buffed maximum) and <paramref name="fameFactor"/> on its kill fame.
    /// </summary>
    public static Mob SpawnAt(Map map, MobTemplate template, Vector2 position, float direction, World world, string? behavior = null,
        IReadOnlyList<Waypoint>? waypoints = null, double healthFactor = 1, double damageFactor = 1, Systems.WorldEvents.Run? worldEvent = null,
        SpellTemplate? levelBuff = null, double fameFactor = 1, string? variant = null)
    {
        var point = new MobSpawnPoint
        {
            Key = new SpawnKey(map.Cluster, "custom", map.CustomSpawns++), Tile = "", Position = position, Direction = (float)TileMath.Mod360(direction),
            Component = new XElement("mobspawnpoint", new XAttribute("mobname", template.Name)), Mob = template.Name, Template = template,
            Behavior = behavior, Waypoints = waypoints ?? [],
        };
        var mob = new Mob(world.ObjectIds.Next(), point, template, Mob.FlaggingOf(template, "", map.Template?.Name), world.GameNow, world.Now)
        {
            World = world, HealthFactor = healthFactor, DamageFactor = damageFactor, Event = worldEvent, FameFactor = fameFactor, Variant = variant,
        };
        if (levelBuff is not null)
        {
            mob.LevelBuff = levelBuff;
            LevelBuff(map, mob, world); // its health full at the buffed maximum (× healthFactor)
        }
        else if (healthFactor != 1) mob.Health.Fill(Systems.SpellEffects.MobMaxHealth(mob, world.GameNow), template.HitPointsRegeneration, world.GameNow);
        map.AddObject(mob);
        return mob;
    }

    /// <summary>
    /// Takes <paramref name="mob"/> off its map: a Leave to every client that knows it, and its spawn point waits for its
    /// respawn (<see cref="RespawnDelay"/>; <paramref name="respawn"/> false: never). A killed mob comes through here after
    /// its corpse time (stage 5c, a map timer of <see cref="Systems.Combat"/>).
    /// </summary>
    public static void Despawn(Mob mob, World world, bool respawn = true)
    {
        if (mob.Removed) return;
        mob.Removed = true;
        var map = mob.Map;
        if (map is not null && !mob.Dead) Systems.Combat.EnemyGone(map, mob); // a fighting mob despawned (.mob kill): its players' combat may end
        if (map is not null && !mob.Dead) Systems.Summons.SummonerGone(world, map, mob); // its adds go too (a summoned portal's own adds); a dead one's went at its death
        map?.RemoveObject(mob);
        if (map is not null && mob.Template.Category is "boss" or "miniboss") world.Dungeons.BossChecks(map); // stageH: a boss chest it guarded unlocks
        if (mob.Slot is not { } slot || slot.Mob != mob) return;
        slot.Mob = null;
        slot.RespawnAt = respawn && map is { NoRespawn: false } ? world.Now + RespawnDelay(map, slot, mob.Template, world) : null;
    }

    /// <summary>
    /// The wait before <paramref name="slot"/> spawns again (SPEC §5a.2): uniform(min, max) seconds of the RANDOM point's
    /// block, else of the mob's respawntimesecondsmin/max (<paramref name="template"/>, the point's named mob without one),
    /// divided by <see cref="Game.Rates.Respawn"/> (Rates:Respawn, 2 = back in half the time).
    /// </summary>
    public static TimeSpan RespawnDelay(Map map, MobSpawnSlot slot, MobTemplate? template, World world)
    {
        var (min, max) = slot.Point.Block is { } block ? (block.RespawnSecondsMin, block.RespawnSecondsMax)
            : (template ?? slot.Point.Template) is { } t ? (t.RespawnSecondsMin, t.RespawnSecondsMax) : (60f, 60f);
        if (max < min) max = min;
        var seconds = min + (max - min) * map.Random.NextDouble();
        return TimeSpan.FromSeconds(Math.Max(0, seconds / world.Rates.Respawn));
    }

    /// <summary>"51 kinds of mob at 1116 spawn points (3 waiting)" for the map's log line.</summary>
    public static string Summary(Map map)
    {
        var waiting = map.MobSlots.Count(s => s.Mob is null);
        var kinds = map.MobSlots.Where(s => s.Mob is not null).Select(s => s.Mob!.Template.Id).Distinct().Count();
        return string.Create(CultureInfo.InvariantCulture, $"{kinds} kinds of mob at {map.MobSlots.Count} spawn points{(waiting > 0 ? $" ({waiting} waiting)" : "")}");
    }
}
