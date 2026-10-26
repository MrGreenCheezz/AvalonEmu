using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The client's spell target rules on the server (stageJ pvp-spells SPEC §1.2, §2.2, §2.3; analysis/work/stageJ/pvp-spells/SPEC.md):
/// the class of a spell's or an effect node's <c>target</c> (<see cref="Cls"/>, the client's bil.SpellTarget), whether a unit matches
/// it for a player caster (<see cref="Cast"/> = bil.n3 0x1806BC290 for the CastStart target, <see cref="Effect"/> = bi6.n3 0x1806C99F0
/// for each effect node) from the facts of that unit (<see cref="Of"/>), and the kind filter of an applied spell's own class
/// (<see cref="Kind"/>). The client's "not hostile" is read as <see cref="Facts.Friend"/> (<see cref="Friend"/>: not Hostile under the
/// server's Pvp:Mode and not under the client's own table, <see cref="OfficialOf"/>), so a player that is neither Hostile nor a Friend
/// (the consensual mode's narrowing, a duelist, a knocked-down player) gets no effect of another player's spell at all (D4), and a mob
/// that is not attackable or not targetable none either. Phase 2 (Pvp:Spells): a player or bot the caster may attack now is Hostile
/// (<see cref="Pvp.Refusal"/>), so enemy spells reach it as they reach a mob. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class SpellTargets
{
    /// <summary>bil.SpellTarget (cpp2il Albion.Common), the names lower-cased in spells.xml (<c>friendallplayers</c> = 24); bi6.EffectTarget is the same list without 16, 28-31 and 38.</summary>
    public enum Cls : byte
    {
        None, Self, Other, OtherPlayers, All, AllMobs, AllPlayers, AllUnmountedPlayers, AllUnmounted, AllNotPassive,
        Enemy, EnemyMobs, EnemyPlayers, EnemyUnmounted, EnemyUnmountedNotPassive, EnemyNotPassive, EnemyAliveOrKnockedDownNotPassive,
        FriendOther, FriendOtherMobs, FriendOtherPlayers, FriendOtherUnmounted, FriendOtherNotPassive,
        FriendAll, FriendAllMobs, FriendAllPlayers, FriendAllUnmounted, FriendAllNotPassive,
        Ground, KnockedDownPlayer, KnockedDownFriendPlayer, KnockedDownEnemyPlayer, DeadPlayer,
        FactionSelf, FactionAll, FactionOther, FriendFaction, FriendFactionOther, EnemyFaction, StoredTarget,
    }

    /// <summary>
    /// What the rules ask of a unit for a caster (cjq.a 0x180AB97B0 fills the client's arguments): itself, Hostile, a Friend, a player,
    /// targetable (a mob: attackable and not notselectable / nothittable; a player: always), dead, knocked down, mounted, passive (a mob
    /// of category harmless, chest, vanity or environment, c0a.aqs), the caster's and the unit's flag a faction (cpx.aur).
    /// </summary>
    public readonly record struct Facts(bool Self, bool Hostile, bool Friend, bool Player, bool Targetable, bool Dead, bool Down, bool Mounted,
        bool Passive, bool FactionCaster, bool FactionTarget);

    private static readonly Dictionary<string, Cls> Names =
        Enum.GetValues<Cls>().ToDictionary(c => c.ToString().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The class of a spells.xml <c>target</c>; empty = <see cref="Cls.None"/>, an unknown name too (logged once).</summary>
    public static Cls Parse(string? name)
    {
        if (string.IsNullOrEmpty(name)) return Cls.None;
        if (Names.TryGetValue(name, out var cls)) return cls;
        Combat.LogOnce("class:" + name, $"spell target class '{name}' is not known: read as none");
        return Cls.None;
    }

    /// <summary>
    /// bi6.n3 (an effect node's class, SPEC §1.2 table) with "not hostile" read as Friend; a player that is neither Hostile nor a Friend
    /// and a mob that is not targetable match nothing (D4). None, Ground, StoredTarget and the knocked-down and dead classes: false.
    /// </summary>
    public static bool Effect(Cls cls, Facts f)
    {
        if (f.Player && !f.Self && !f.Hostile && !f.Friend) return false; // D4: protected by the PvP mode, a duelist, knocked down
        if (!f.Player && !f.Targetable) return false;                    // D4: a dead or friendly mob, one not to be selected
        var other = !f.Self;
        var faction = f.FactionCaster && f.FactionTarget;
        return cls switch
        {
            Cls.Self => f.Self,
            Cls.Other => other,
            Cls.OtherPlayers => other && f.Player,
            Cls.All => true,
            Cls.AllMobs => !f.Player,
            Cls.AllPlayers => f.Player,
            Cls.AllUnmountedPlayers => f.Player && !f.Mounted,
            Cls.AllUnmounted => !f.Mounted,
            Cls.AllNotPassive => !f.Passive,
            Cls.Enemy => f.Hostile,
            Cls.EnemyAliveOrKnockedDownNotPassive => f.Hostile && !f.Passive,
            Cls.EnemyMobs => f.Hostile && !f.Player,
            Cls.EnemyPlayers => f.Hostile && f.Player,
            Cls.EnemyUnmounted => f.Hostile && !f.Mounted,
            Cls.EnemyNotPassive => f.Hostile && !f.Passive,
            Cls.EnemyUnmountedNotPassive => f.Hostile && !f.Mounted && !f.Passive,
            Cls.FriendOther => f.Friend && other,
            Cls.FriendOtherMobs => f.Friend && other && !f.Player,
            Cls.FriendOtherPlayers => f.Friend && other && f.Player,
            Cls.FriendOtherUnmounted => f.Friend && other && !f.Mounted,
            Cls.FriendOtherNotPassive => f.Friend && other && !f.Passive,
            Cls.FriendAll => f.Friend,
            Cls.FriendAllMobs => f.Friend && !f.Player && other,
            Cls.FriendAllPlayers => f.Friend && f.Player,
            Cls.FriendAllUnmounted => f.Friend && !f.Mounted,
            Cls.FriendAllNotPassive => f.Friend && !f.Passive,
            Cls.FactionSelf => f.Self && faction,
            Cls.FactionAll => faction,
            Cls.FactionOther => other && faction,
            Cls.FriendFaction => f.Friend && faction,
            Cls.FriendFactionOther => f.Friend && other && faction,
            Cls.EnemyFaction => f.Hostile && faction,
            _ => false,
        };
    }

    /// <summary>
    /// bil.n3 (the CastStart target's class): <see cref="Effect"/>'s table, every living class also not dead and not knocked down; the
    /// faction classes without that test; KnockedDownPlayer / KnockedDownFriendPlayer / KnockedDownEnemyPlayer a knocked-down player (a
    /// Friend / Hostile one), DeadPlayer a dead one (the resurrection of SPEC phase 5); Ground and StoredTarget false. (Its FriendAllMobs keeps
    /// no self test, which changes nothing: the caster is a player.)
    /// </summary>
    public static bool Cast(Cls cls, Facts f) => cls switch
    {
        Cls.KnockedDownPlayer => f.Player && f.Down && !f.Dead,
        Cls.KnockedDownFriendPlayer => f.Player && f.Down && !f.Dead && f.Friend,
        Cls.KnockedDownEnemyPlayer => f.Player && f.Down && !f.Dead && f.Hostile,
        Cls.DeadPlayer => f.Player && f.Dead,
        Cls.FactionSelf or Cls.FactionAll or Cls.FactionOther or Cls.FriendFaction or Cls.FriendFactionOther or Cls.EnemyFaction => Effect(cls, f),
        _ => !f.Dead && !f.Down && Effect(cls, f),
    };

    /// <summary>
    /// The facts of <paramref name="unit"/> for player <paramref name="caster"/> at <paramref name="now"/> (SPEC §2.3): the caster itself is
    /// its own Friend; a mob is Hostile while attackable (a mob running home stays a target: its spell immunity answers the hit) and never a
    /// Friend; another player (a bot too) on the caster's map is Hostile under Pvp:Spells when the caster may attack it now
    /// (<see cref="Pvp.Refusal"/>, the auto-attacks' rule: the zone's flags under Pvp:Mode, the duel opponent once the duel has started, neither
    /// knocked down, not invulnerable; SPEC phase 2), else a Friend by <see cref="Friend"/>, else neither. A bot caster (SPEC §2.9, phase 4):
    /// a mob is never its target (bots never fight mobs: neither Hostile nor targetable, so no class reaches it), and its Hostile players
    /// are only those its target choice allows (<see cref="Bots.BotPvp.MayHit"/>: no duelist, no spared player; a gatherer's only the
    /// ones it answers).
    /// </summary>
    public static Facts Of(Player caster, IUnit unit, long now)
    {
        var fc = Pvp.IsFaction(caster.Flag);
        switch (unit)
        {
            case Mob m when caster.IsBot && !caster.IsCompanion: // stageN companions fight mobs as players do
                return new Facts(false, false, false, false, false, m.Dead, false, false, false, fc, false);
            case Mob m:
                return new Facts(false, m.Attackable, false, false, m.Attackable && !SpellEffects.NotTargetable(m, now), m.Dead, false, false,
                    m.Template.Category is "harmless" or "chest" or "vanity" or "environment", fc, false);
            case Player q:
                var self = q == caster;
                var hostile = !self && caster.World.Config.PvpSpells && q.Map is not null && q.Map == caster.Map && Pvp.Refusal(caster, q, now) is null
                    && (!caster.IsBot || Bots.BotPvp.MayHit(caster, q));
                return new Facts(self, hostile, self || (!hostile && Friend(caster, q, now)), true, true, q.Combat.Down?.Kind == KnockdownKind.Dead,
                    q.Combat.Down is not null, q.IsMounted, false, fc, Pvp.IsFaction(q.Flag));
            default:
                return default;
        }
    }

    /// <summary>
    /// Whether <paramref name="b"/> is <paramref name="a"/>'s Friend for friendly spells (SPEC §2.3, D3): oneself; else on a's map, not
    /// knocked down, both humans or both bots (D10), neither in a duel under Pvp:DuelIsolation (a duelist takes and gives friendly effects
    /// only itself), not Hostile by <see cref="Pvp.Of(Player, Player)"/> (the server's Pvp:Mode) and not by <see cref="OfficialOf"/> (the
    /// client's table: under the consensual mode a Blue healer never helps a Red player in a fight the client calls hostile), then
    /// Pvp:FriendlySpells (All; Group: the party or the guild, for two world bots their patrol group; Self: nobody).
    /// </summary>
    public static bool Friend(Player a, Player b, long now)
    {
        if (a == b) return true;
        var world = a.World;
        // StageN companions count as players here: a companion heals its party and the party's spells reach it.
        if (b.Map is null || b.Map != a.Map || b.Combat.Down is not null || (a.IsBot && !a.IsCompanion) != (b.IsBot && !b.IsCompanion)) return false;
        if (world.Config.PvpDuelIsolation && (world.Duels.Of(a) is not null || world.Duels.Of(b) is not null)) return false;
        if (Pvp.Of(a, b) == Relation.Hostile || OfficialOf(a, b) == Relation.Hostile) return false;
        return world.Config.PvpFriendlySpells switch
        {
            FriendlySpells.All => true,
            // Bot support roles: two world bots of one patrol group are a group too (bots have no party or guild).
            FriendlySpells.Group => Pvp.SameParty(a, b) || world.Guilds.Same(a.Character.Id, b.Character.Id) || a.Bot?.Group is { } g && b.Bot?.Group == g,
            _ => false,
        };
    }

    /// <summary>
    /// What <paramref name="b"/> is to <paramref name="a"/> by the client's own table (a5g.b, SPEC §2.3): the duel opponent Hostile, else
    /// <see cref="Pvp.Of(byte, byte, bool, bool, ClusterPvp)"/> of the two flags in a's zone, without the Pvp:Mode narrowing and without
    /// Guilds:NoFriendlyFire (two guild mates of rival factions stay Hostile there). A bot's relation is <see cref="Pvp.Of(Player, Player)"/>'s.
    /// </summary>
    public static Relation OfficialOf(Player a, Player b)
    {
        if (a == b) return Relation.Friendly;
        if (a.IsBot || b.IsBot) return Pvp.Of(a, b);
        var world = a.World;
        if (world.Duels.OpponentOf(a) == b) return Relation.Hostile;
        return Pvp.Of(a.Flag, b.Flag, Pvp.SameParty(a, b), world.Guilds.Same(a.Character.Id, b.Character.Id), Pvp.RulesOf(world, a.Map));
    }

    /// <summary>
    /// The kind filter of an applied spell's own class (SPEC §2.2 "Applied spells", PvE bug 5): where an applyspell, an area, an aura, a
    /// pulse, a chain or a trigger runs <paramref name="applied"/> on <paramref name="unit"/>, a class of players (…Players,
    /// AllUnmountedPlayers, the knocked-down and dead classes) needs a player, one of mobs (…Mobs) a mob, one of the unmounted (…Unmounted)
    /// an unmounted unit. The relation part is not checked again: the applying node did that, and the data applies across it
    /// (GHOSTSTRIKE_INVIS: a self node applies the enemy-class REMOVE_BEAMS). ARCANEORB2_SILENCE_PLAYERS stays off mobs, _MOBS off players.
    /// </summary>
    public static bool Kind(SpellTemplate applied, IUnit unit)
    {
        var player = unit is Player;
        var mounted = unit is Player { IsMounted: true };
        return Parse(applied.Target) switch
        {
            Cls.OtherPlayers or Cls.AllPlayers or Cls.EnemyPlayers or Cls.FriendOtherPlayers or Cls.FriendAllPlayers or Cls.KnockedDownPlayer
                or Cls.KnockedDownFriendPlayer or Cls.KnockedDownEnemyPlayer or Cls.DeadPlayer => player,
            Cls.AllUnmountedPlayers => player && !mounted,
            Cls.AllMobs or Cls.EnemyMobs or Cls.FriendOtherMobs or Cls.FriendAllMobs => !player,
            Cls.AllUnmounted or Cls.EnemyUnmounted or Cls.FriendOtherUnmounted or Cls.FriendAllUnmounted => !mounted,
            _ => true,
        };
    }

    /// <summary>Whether a cast of class <paramref name="cls"/> names a target (CastStart key 5): all but Self, Ground, FactionSelf and StoredTarget (and None).</summary>
    public static bool NamesTarget(Cls cls) => cls is not (Cls.None or Cls.Self or Cls.Ground or Cls.FactionSelf or Cls.StoredTarget);
}
