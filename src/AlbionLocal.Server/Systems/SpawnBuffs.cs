using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Zone-entry protection (stageP league-pvp SPEC §2.4, B4; analysis/work/stageP/league-pvp/SPEC.md): the worldsettings.xml
/// &lt;clustertype&gt;&lt;spawnbuff&gt; lists and the clustertype's spellcooldownsonenteringseconds, given on the Join that ends a cluster
/// change (and a respawn's arrival).
/// <list type="bullet">
/// <item><b>Spawn buffs</b> (Pvp:SpawnBuffs): the list's &lt;buff&gt;s in document order whose conditions hold (<see cref="SpawnBuff.Applies"/>):
/// isclusterchange (the Join ends a cluster change), isrevive (a respawn's arrival), ispvppossible (the zone's archetype allows any PvP:
/// faction PvP, PvP allowed or forced), fromshortspawnbuffcluster (the cluster one came from has hasshortspawnbuffonleave: a dungeon,
/// expedition or tunnel, so SPAWN_INVULNERABILITY_SHORT instead of SPAWN_INVULNERABILITY); fromhideout, fromstaticdungeon, isingvg,
/// hashomeincluster and isfirstlogin are false here (no hideouts, territories or GvG). Only the SPAWN_* family is given here
/// (SPAWN_INVULNERABILITY 30 s and its SHORT / EXPEDITION variants, SPAWN_SPEED, SPAWN_BUFFIMMUNITY, SPAWN_BUFFIMMUNITY_ONDEATH); the
/// other names of the lists belong to their systems (HELL_*: <see cref="Hellgates"/>, which gives its instances' lists itself with the
/// same rule; ARENA_*: the arena; CORRUPTED_DUNGEON_ENTER: <see cref="CorruptedDungeons"/>; MISTS_*, DRAGON_AREA_ENTER, the hideout and
/// shrine buffs: not modelled). A buff the player is immune to by a spellimmunity of an earlier one (SPAWN_BUFFIMMUNITY_ONDEATH blocks the
/// shield after a respawn, SPAWN_BUFFIMMUNITY a second shield within 60 s and 150 m) is skipped, as the data's order intends ("immunity LAST");
/// an effect bound to a spot (expireafterdistance) of another cluster ends first, as the engine ends it on any move there, so the last zone's
/// SPAWN_BUFFIMMUNITY blocks no shield in the next one [infer: the official reach of that immunity is not captured, R6].
/// They run through the effect engine: the invincibility, invisibility to mobs and immunities break on attacking, casting or interacting
/// and expire after 150 m (SHORT 50 m).</item>
/// <item><b>Spell cooldowns on entering</b> (Pvp:EnterSpellCooldowns): spellcooldownsonenteringseconds (15 s in the yellow, red and black
/// zones and their dungeons) on every gear spell slot (<see cref="SpellCasts.EnterCooldowns"/>) on a cluster change's arrival.</item>
/// </list>
/// Not on a login (a fresh client), nor in a hellgate instance. Under <see cref="World.Sync"/>.
/// </summary>
public static class SpawnBuffs
{
    /// <summary>The spawn buffs this system gives; every other name of a list is another system's.</summary>
    public static bool Ours(string name) => name.StartsWith("SPAWN_", StringComparison.Ordinal);

    /// <summary>
    /// After <paramref name="p"/>'s Join (<see cref="Network.WorldSession"/>): <paramref name="arrived"/> the arrival of a cluster change,
    /// <paramref name="revived"/> a respawn's arrival. The spawn buffs and the spell cooldowns on entering (see the class).
    /// </summary>
    public static void Joined(Player p, bool arrived, bool revived)
    {
        var world = p.World;
        if (!arrived && !revived || p.Map is not { } map || map.Template is not { } t || p.IsBot) return;
        if (world.Hellgates.Find(map.Cluster) is not null) return; // the hellgate's own (Hellgates.JoinedB)
        var type = t.Info.Type;
        if (world.Config.PvpSpawnBuffs && t.SpawnBuffs.Count > 0)
        {
            var rule = Pvp.RulesOf(world, map).Rule;
            var from = world.Clusters?.WorldIfLoaded?.Find(p.Character.PreviousCluster)?.Type;
            var fromShort = from is not null && Archetypes(world)?.ShortSpawnBuffOnLeave(from) == true;
            bool Holds(string condition) => condition switch
            {
                "isclusterchange" => arrived,
                "isrevive" => revived,
                "ispvppossible" => rule is PvpRule.FactionPvpOnly or PvpRule.PvpAllowed or PvpRule.PvpForced,
                "fromshortspawnbuffcluster" => fromShort,
                _ => false,
            };
            var given = Give(p, t.SpawnBuffs, Holds, Ours);
            if (given.Count > 0) Log.Info("world", $"'{p.Character.Name}' enters {map.Cluster} ({type}): {string.Join(", ", given)}");
        }
        if (arrived && world.Config.PvpEnterSpellCooldowns && Archetypes(world)?.EnterCooldownOf(type) is > 0 and var seconds)
        {
            var slots = SpellCasts.EnterCooldowns(p, seconds);
            if (slots > 0) Log.Debug("world", $"'{p.Character.Name}' enters {map.Cluster}: {slots} spell slot(s) on {seconds} s");
        }
    }

    private static WorldArchetypes? Archetypes(World world)
    {
        try { return world.Clusters?.Archetypes; }
        catch (Exception e) when (e is InvalidDataException or IOException) { return null; }
    }

    /// <summary>
    /// Gives <paramref name="p"/> the buffs of <paramref name="list"/> that <paramref name="ours"/> takes and whose conditions
    /// <paramref name="holds"/> says hold, in order, as spells of its own (<see cref="Pvp.ApplySpell"/>), skipping one it is immune to by now;
    /// the list goes out once. Returns the names given. Shared with the hellgates' instances and the arena (<see cref="Arenas"/>, whose
    /// ARENA_ENTER has instant effects: <paramref name="run"/> casts each spell whole on the player, <see cref="SpellCasts.Execute"/>, so its
    /// health and energy refill, cooldown reset and applied spells run too).
    /// </summary>
    public static List<string> Give(Player p, IEnumerable<SpawnBuff> list, Func<string, bool> holds, Func<string, bool> ours, bool run = false)
    {
        var world = p.World;
        var now = world.GameNow;
        SpellEffects.ExpireAfterMovement(p); // effects bound to the spot of another cluster (the last zone's SPAWN_BUFFIMMUNITY) end first
        var given = new List<string>();
        foreach (var b in list)
        {
            if (!ours(b.Name) || !b.Applies(holds) || world.Spells?.Template(b.Name) is not { } spell || SpellEffects.Immune(p, spell, now)) continue;
            if (run && p.Map is { } map)
            {
                SpellCasts.Execute(new SpellCasts.SpellContext(p, map, spell, 0, 100, null, p.PositionAt(DateTime.UtcNow), now), spell);
                given.Add(b.Name);
            }
            else if (Pvp.ApplySpell(p, b.Name, now, send: false)) given.Add(b.Name);
        }
        if (given.Count > 0 && !run)
        {
            SpellEffects.Send(world, p);
            SpellEffects.Refresh(world, p);
        }
        return given;
    }
}
