using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// spawnmob (stageD effects batch 3, 614 uses; most in mob spells: the necromancer's and the Morgana bosses' adds, the keepers'
/// elementals, the vanity pets of players): the mob of <c>type</c> is put on the map as a server mob of its own (NewMob to the
/// players in view, its mobs.xml AI) with <see cref="Mob.Summoner"/> set, no spawn point (it never respawns) and:
/// <list type="bullet">
/// <item>where: target self / all / other the summoner, enemy the target, ground the point; + distancefromtarget along
/// anglefromcaster (degrees off the summoner's facing);</item>
/// <item>limit: at most that many of the type per summoner alive; one more takes the oldest off, or with
/// limitreachedbehavor="blockspawn" none spawns;</item>
/// <item>ttl s: then it is taken off (a Leave); ttlaftercasterdeath s after its summoner died (the summons of a dead boss go);</item>
/// <item>a mob's summon takes on its summoner's target (<see cref="AI.MobAI.AddThreat"/>), a player's (a vanity pet) runs its own AI.</item>
/// </list>
/// <item>ttlafterreset s after the summon itself reset, ttlaftercasterreset s after its summoner reset (<see cref="Reset"/>), unless
/// that mob fights again by then;</item>
/// <item>its summoner despawned (<see cref="SpawnManager.Despawn"/>), died or (a player) went down or left: <see cref="SummonerGone"/>.</item>
/// Not modelled: sharenewthreats, returnhomebehavior, inheritsummonerascaster. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Summons
{
    /// <summary>
    /// Runs spawnmob <paramref name="node"/> of <paramref name="summoner"/> at <paramref name="ticks"/>: <paramref name="target"/> is the
    /// node's unit (the summoner itself for self), <paramref name="point"/> the ground point. Returns the new mob; null when its type is
    /// unknown, the limit blocks it or the map has no grid.
    /// </summary>
    public static Mob? Spawn(World world, Map map, IUnit summoner, XElement node, IUnit? target, Vector2 point, long ticks)
    {
        var name = (string?)node.Attribute("type");
        if (world.Clusters?.Mobs.Find(name ?? "") is not { } type)
        {
            Combat.LogOnce("summon:" + name, $"spawnmob type {name} is not in mobs.xml: nothing spawns");
            return null;
        }
        var limit = (int)SpellTemplate.Float(node, "limit");
        if (limit > 0)
        {
            var alive = Of(map, summoner).Where(m => m.Template == type).ToList();
            if (alive.Count >= limit)
            {
                if ((string?)node.Attribute("limitreachedbehavor") == "blockspawn") return null;
                foreach (var old in alive.Take(alive.Count - limit + 1)) Remove(world, old, "its summoner's limit");
            }
        }
        var facing = summoner switch { Player p => p.Facing, Mob m => m.Direction, _ => 0f };
        var origin = (string?)node.Attribute("target") switch
        {
            "ground" => point,
            "enemy" when target is not null => SpellMovement.PositionOf(target, ticks),
            _ => SpellMovement.PositionOf(summoner, ticks),
        };
        var distance = SpellTemplate.Float(node, "distancefromtarget");
        if (distance != 0) // a negative distance stands on the far side (the Morgana demon gates at −10 m)
            origin += SpellMovement.Facing(facing + SpellTemplate.Float(node, "anglefromcaster")) * distance;
        var mob = SpawnManager.SpawnAt(map, type, origin, facing, world);
        mob.Summoner = summoner;
        mob.SummonNode = node;
        Log.Debug("combat", string.Create(CultureInfo.InvariantCulture, $"{summoner.Id} summoned {mob} at ({origin.X:0.##}, {origin.Y:0.##})"));
        var ttl = SpellTemplate.Float(node, "ttl");
        if (ttl > 0) map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(ttl), () => Remove(world, mob, "its ttl ran out"));
        // A mob's adds fight its target at once.
        if (summoner is Mob { TargetId: not 0 } boss && world.MobAi && map.Fighters.FirstOrDefault(p => p.Id == boss.TargetId) is { } victim)
            mob.AI.AddThreat(victim, 1);
        return mob;
    }

    /// <summary>The living summons of <paramref name="summoner"/> on <paramref name="map"/>, oldest first.</summary>
    public static IEnumerable<Mob> Of(Map map, IUnit summoner) =>
        map.Objects.Values.OfType<Mob>().Where(m => m.Summoner == summoner && !m.Dead && !m.Removed).OrderBy(m => m.Id);

    /// <summary>
    /// <paramref name="summoner"/> died, was despawned (another summon's ttl or limit, .mob), or its player went down or left: its summons go after their ttlaftercasterdeath (at once without one, as
    /// the official adds of a killed boss vanish).
    /// </summary>
    public static void SummonerGone(World world, Map map, IUnit summoner)
    {
        foreach (var mob in Of(map, summoner).ToList())
        {
            var delay = mob.SummonNode is { } node ? SpellTemplate.Float(node, "ttlaftercasterdeath") : 0;
            if (delay <= 0) Remove(world, mob, "its summoner is gone");
            else map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(delay), () => Remove(world, mob, "its summoner is gone"));
        }
    }

    /// <summary>
    /// <paramref name="mob"/> reset (a leash, <see cref="AI.AggressiveAI"/>): a summon with ttlafterreset goes that long after, the
    /// summons with ttlaftercasterreset go that long after, each only while the mob it waits on is still out of the fight. Without the
    /// attribute they stay.
    /// </summary>
    public static void Reset(World world, Map map, Mob mob)
    {
        if (mob.SummonNode?.Attribute("ttlafterreset") is not null)
            After(world, map, mob, SpellTemplate.Float(mob.SummonNode, "ttlafterreset"), mob, "it reset");
        foreach (var add in Of(map, mob).ToList())
            if (add.SummonNode?.Attribute("ttlaftercasterreset") is not null)
                After(world, map, add, SpellTemplate.Float(add.SummonNode, "ttlaftercasterreset"), mob, "its summoner reset");
    }

    /// <summary>Takes <paramref name="mob"/> off after <paramref name="seconds"/>, unless <paramref name="waiting"/> has a target again by then.</summary>
    private static void After(World world, Map map, Mob mob, float seconds, Mob waiting, string why)
    {
        if (seconds <= 0) Remove(world, mob, why);
        else map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(seconds), () =>
        {
            if (waiting.TargetId == 0) Remove(world, mob, why);
        });
    }

    /// <summary>Takes a living summon off its map (a Leave; a dead one goes with its corpse).</summary>
    private static void Remove(World world, Mob mob, string why)
    {
        if (mob.Dead || mob.Removed) return;
        Log.Debug("combat", $"{mob} unsummoned: {why}");
        SpawnManager.Despawn(mob, world, respawn: false);
    }
}
