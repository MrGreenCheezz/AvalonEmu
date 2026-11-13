using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The flag change (stageE pvp SPEC §3.1, sub-step E2, analysis/work/stageE/pvp/SPEC.md): ChangeFlaggingPrepare (op 354) {0 the new
/// FlaggingStatus} starts a server cast of the flag spell on CharacterSpellSlot 24 (ChangeFlagging): FLAG_RED and FLAG_BLUE
/// castingtime 15 s, the faction flags FLAG_HIGHLAND … FLAG_CAERLEON 0 s (spells.xml; 1037-1044 and 15 / 0 s without spell data).
/// <list type="number">
/// <item><b>Check</b> (<see cref="Refusal"/>, the client's cwg.u 0x1809E1320 checked first, stageJ flag-toggle NOTES §1.2): a valid
/// other flag; not knocked down, no flag cast, spell cast or channel under way; not mounted or mounting when the spell has a cast
/// time (cwg.u code 12: the 0 s faction flags are allowed mounted). No combat check (cwg.u has none; damage taken ends the cast). A
/// faction: the cluster's allowfactionflagging, the enlisted faction is that one, the flag is Blue (the faction NPC in range is not
/// checked: there are none). Red: from Blue, in every cluster (cwg.u has no pvprule check and the HUD button is always there; Red
/// players are captured inside royal cities, NOTES §2), refused under Pvp:Mode Off and, under Official, in a FactionPvpOnly (blue)
/// cluster (where <see cref="Arrive"/> would take it away again). Blue: not while CANNOTDISABLEPVP (FLAG_RED's
/// 15 min) or CANNOTDISABLEFACTIONFLAGGING is on, nor at a level with candisablepvp false. The response: rc 0 with no keys, else
/// <see cref="Refused"/> (the client ignores both, cwg.ai7 0x1809E1180). The client sent op 354 and then started its own cast of the
/// flag spell on slot 24 (cwc.v), so a refusal ends that cast: CastCancel {0 me, 1 true, 2 0, 3 the cooldown factor} to the player
/// and the reason as a SystemMessage line; its CastStart for slot 24, if it sends one, is let go without an answer (the
/// server's cast is this one, <c>WorldSession.CastStart</c>).</item>
/// <item><b>Cast</b>: CastStart (14) {0 me, 1 now, 4 now + castingtime, 5 spell, 6 -1, 7 me, 8 counter 0 [infer], 9 24} to the
/// observers (the client casts its own, SPEC §3.1). Damage taken (damagecancelscasting on every flag spell) or a knockdown interrupts
/// it: CastCancel {0, 1 true, 2 counter, 9 0} to the observers, {0, 1 true, 2, 3 the cooldown factor} to the player (as
/// <see cref="SpellCasts.Cancel"/>); its own CastCancel (op 25, a walk or another action of its client) drops it without the latter,
/// so does the server-side start of an attack, a cast, a harvest, a station action, loot or a mount [infer]
/// (<see cref="Mounts.BeforeAction"/>, <see cref="Mounts.Start"/>), leaving the map without any.</item>
/// <item><b>End</b> (<see cref="Update"/>, the world tick): CastSpell (19) {0 me, 1 me, 2 the position, 3 spell, 4 = 5 now, 6 0,
/// 7 0}, then ChangeFlaggingFinished (364) {0 me, 1 flag} to the player and its observers (22 of 22 captured in this order), the
/// flag saved with the character; the spell's applyspell target="self" children go on the player (FLAG_RED's CANNOTDISABLEPVP, its
/// ActiveSpellEffectsUpdate).</item>
/// </list>
/// PVE-DESIGN.md §1: Pvp:Mode Consensual allows Red in a black zone too (it counts as a red one), Off allows no Red; with
/// Pvp:RedLockMinutes set FLAG_RED's CANNOTDISABLEPVP is not put on and the server keeps that many minutes of lock (0: none).
/// The safe-zone rules (§3.1, NOTES §2): Red is kept in a city (PvpDisabled: nobody is Hostile there anyway); a Red player who joins
/// a FactionPvpOnly (blue) cluster under Pvp:Mode Official is Blue again, silently (the Join carries it, <see cref="Arrive"/>; no Red
/// player was captured in a blue zone), under Consensual it stays Red, under Off it is Blue again anywhere; a faction-flagged one may not enter a cluster with allowfactionflaggedenter false
/// (<see cref="EntryRefusal"/>, 1655), nor anyone below its requiredreputationtoenter (1652). Not modelled (TODO): the temporary
/// flagging (TemporaryFlaggingStatusUpdate 545), the party's "different flagging status" check (SPEC §3.1 step 4 leaves it).
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Flagging
{
    /// <summary>CharacterSpellSlot ChangeFlagging (SPEC §0).</summary>
    public const byte Slot = 24;

    /// <summary>The return code of a refused op 354 (RcGeneric.InvalidState; the client ignores the response).</summary>
    public const short Refused = -501;

    /// <summary>ChangeCluster's ReputationTooLow and NoFactionPlayersAllowed (RcOpChangeCluster, client enum).</summary>
    public const short ReputationTooLow = 1652, NoFactionPlayersAllowed = 1655;

    /// <summary>A flag cast under way: the flag it sets, its spell (null without spell data) and id, its start and end GameTimeStamps.</summary>
    public sealed record FlagCast(byte To, SpellTemplate? Spell, short SpellId, long Start, long End);

    /// <summary>The flag spell for <paramref name="flag"/> (the client's gamedata FlaggingStatus → spell dictionary).</summary>
    public static string SpellName(byte flag) => flag switch
    {
        Pvp.Red => "FLAG_RED",
        Pvp.Blue => "FLAG_BLUE",
        1 => "FLAG_HIGHLAND",
        2 => "FLAG_FOREST",
        3 => "FLAG_STEPPE",
        4 => "FLAG_MOUNTAIN",
        5 => "FLAG_SWAMP",
        _ => "FLAG_CAERLEON",
    };

    /// <summary>The ids of 345319 for a world without spell data (spell-ids/spells.tsv).</summary>
    private static short DefaultId(byte flag) => flag switch
    {
        Pvp.Red => 1037, Pvp.Caerleon => 1038, 3 => 1039, 4 => 1040, 5 => 1041, 1 => 1042, 2 => 1043, _ => 1044,
    };

    /// <summary>Why <paramref name="p"/> may not change its flag to <paramref name="to"/> now (null = it may); see the class.</summary>
    public static string? Refusal(Player p, byte to, long now)
    {
        if (!(to <= Pvp.Caerleon || to == Pvp.Red)) return $"no FlaggingStatus {to}";
        if (to == p.Flag) return "already so";
        if (p.Map is null) return "not on a map";
        if (p.Combat.Down is not null) return "knocked down";
        if (p.PvpState.FlagCast is not null) return "a flag change is under way";
        if ((p.IsMounted || p.Mount.State == MountState.Mounting) && CastSeconds(p.World, to) > 0) return "mounted";
        if (p.Combat.Cast is not null || p.Combat.Channel is not null) return "a cast or channel is under way";
        var rules = Pvp.RulesOf(p.World, p.Map);
        if (Pvp.IsFaction(to))
        {
            if (!rules.AllowFactionFlagging) return "no faction flagging in this cluster";
            if (p.Faction != to) return "not enlisted to that faction";
            if (p.Flag != Pvp.Blue) return "not Blue";
            return null;
        }
        if (to == Pvp.Red)
        {
            if (p.Flag != Pvp.Blue) return "not Blue";
            // Every cluster (NOTES §1.2, §2): where nobody is Hostile (a city, a blue zone) the flag simply has no effect yet.
            // Pvp:Mode Consensual: Red is the consent to fight other Red players (a black zone counts as a red one).
            if (p.World.Config.PvpMode == PvpMode.Off) return "no hostile flag on this server (Pvp:Mode Off)";
            // Official: Arrive turns Red back to Blue in a blue cluster, so it is not cast there (no 15 s and 15 min lock for nothing).
            return p.World.Config.PvpMode == PvpMode.Official && rules.Rule == PvpRule.FactionPvpOnly ? "no hostile flag in a blue cluster (Pvp:Mode Official)" : null;
        }
        if (p.World.Config.PvpRedLockMinutes is { } minutes)
        {
            // Pvp:RedLockMinutes: the server's lock instead of FLAG_RED's CANNOTDISABLEPVP (not put on, see Finish).
            if (p.Flag == Pvp.Red && p.PvpState.RedSince is { } since && p.World.Clock.Now < since + TimeSpan.FromMinutes(minutes)) return $"Red for less than {minutes:0.#} min";
        }
        else if (Pvp.SpellActive(p, "CANNOTDISABLEPVP", now)) return "CANNOTDISABLEPVP is on";
        if (Pvp.SpellActive(p, "CANNOTDISABLEFACTIONFLAGGING", now)) return "CANNOTDISABLEFACTIONFLAGGING is on";
        if (!Reputation.LevelData(p).CanDisablePvp) return "the reputation level may not unflag";
        return null;
    }

    /// <summary>The name of FlaggingStatus <paramref name="flag"/> for the log and the player (@FLAGGINGSTATUS_*: Friendly, Hostile, the cities).</summary>
    public static string FlagName(byte flag) => flag switch
    {
        Pvp.Blue => "Blue", Pvp.Red => "Red", 1 => "Martlock", 2 => "Lymhurst", 3 => "Bridgewatch", 4 => "Fort Sterling", 5 => "Thetford",
        Pvp.Caerleon => "Caerleon", _ => flag.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The castingtime of the flag spell for <paramref name="flag"/> (spells.xml; 15 s for Red and Blue, 0 s else, without spell data).</summary>
    public static double CastSeconds(World world, byte flag) =>
        world.Spells?.Template(SpellName(flag))?.CastingTime ?? (flag is Pvp.Red or Pvp.Blue ? 15 : 0);

    /// <summary>
    /// Op 354: the checks, then the cast (see the class); a refusal ends the cast the client started itself (CastCancel to the
    /// player) and tells it why (SystemMessage). Returns the response's return code.
    /// </summary>
    public static short Prepare(Player p, byte to)
    {
        var world = p.World;
        var now = world.GameNow;
        var rules = Pvp.RulesOf(world, p.Map);
        var where = $"{p.Map?.Cluster ?? "no map"} ({rules.Rule}, Pvp:Mode {world.Config.PvpMode})";
        Log.Debug("pvp", $"'{p.Character.Name}' asks for flag {FlagName(to)} ({to}) from {FlagName(p.Flag)} on {where}");
        if (Refusal(p, to, now) is { } why)
        {
            Log.Info("pvp", $"'{p.Character.Name}' ChangeFlaggingPrepare to {FlagName(to)} ({to}) on {where} refused: {why}");
            if (p.Map is not null && !p.IsBot)
            {
                p.Session.Send(new CastCancelEvent { ddn = p.Id, buo = true, bup = 0, buq = SpellCasts.CooldownFactorOf(p) });
                p.Session.Send(new SystemMessageEvent { d0p = $"PvP flag not changed to {FlagName(to)}: {why}.", d0q = 1 });
            }
            return Refused;
        }
        var spell = world.Spells?.Template(SpellName(to));
        var seconds = CastSeconds(world, to);
        var cast = new FlagCast(to, spell, spell?.Id ?? DefaultId(to), now, now + SpellCasts.Seconds(seconds));
        p.SendToObservers(new CastStartEvent
        {
            ddn = p.Id, bwb = cast.Start, bwe = cast.End, bwf = cast.SpellId, bwg = -1, bwh = p.Id, Extra = new() { [8] = (byte)0, [9] = Slot },
        });
        Log.Info("pvp", $"'{p.Character.Name}' flags {FlagName(to)} ({to}) in {seconds:0.#} s on {where}: the cast starts");
        p.PvpState.FlagCast = cast;
        if (cast.End <= cast.Start) Finish(p, cast);
        return 0;
    }

    /// <summary>The world tick: the flag cast that is due ends.</summary>
    public static void Update(Player p)
    {
        if (p.PvpState.FlagCast is { } cast && p.World.GameNow >= cast.End) Finish(p, cast);
    }

    /// <summary>
    /// The flag cast under way ends without its flag (see the class): <paramref name="interrupted"/> by the server (damage, a
    /// knockdown) tells the player as well; its own CastCancel only the observers. A no-op without one. The execute under way (E3, the
    /// other server cast of a PvP slot) ends the same way and for the same reasons (<see cref="Death.Interrupt"/>).
    /// </summary>
    public static void Interrupt(Player p, string why, bool interrupted = true)
    {
        Death.Interrupt(p, why, interrupted);
        // stageL/M: a slot-25 server cast (a corrupted entrance, a chest: damagecancelscasting; the mists' ENTER_MISTS: damagecancelscasting
        // false, FakeSlotCast.DamageCancels, so "damage" leaves it on)
        p.World.FakeCasts.Interrupt(p, why, interrupted);
        if (p.PvpState.FlagCast is null) return;
        p.PvpState.FlagCast = null;
        if (p.Map is not null)
        {
            p.SendToObservers(new CastCancelEvent { ddn = p.Id, buo = true, bup = 0, Extra = new() { [9] = (byte)0 } });
            if (interrupted) p.Session.Send(new CastCancelEvent { ddn = p.Id, buo = true, bup = 0, buq = SpellCasts.CooldownFactorOf(p) });
        }
        Log.Info("pvp", $"'{p.Character.Name}''s flag change is cancelled: {why}");
    }

    /// <summary>
    /// Sets <paramref name="p"/>'s flag to <paramref name="flag"/> and tells it and its observers (ChangeFlaggingFinished 364); saved.
    /// The cast's end, and a GM's .pvp flag.
    /// </summary>
    public static void Set(Player p, byte flag)
    {
        p.Flag = flag;
        p.World.Saves.MarkChanged(p.Character);
        p.SendAround(new ChangeFlaggingFinishedEvent { ddn = p.Id, bwr = flag });
    }

    private static void Finish(Player p, FlagCast cast)
    {
        p.PvpState.FlagCast = null;
        if (p.Map is not { } map) return;
        var world = p.World;
        var now = world.GameNow;
        var at = p.PositionAt(DateTime.UtcNow);
        // CastFinished (18) {0 me, 1 me, 2 the spell, 3 slot 24, 4 0, 5 the cast end, 6 1.0} before the hit: it is what ends the
        // client's own cast of slot 24. Without it the client stayed in the casting animation with its input blocked — the player
        // was stuck until a relog (reported after a deflag: FLAG_MOUNTAIN → FLAG_BLUE, 15 s). Official batch, one instant:
        // fortress-capture-kit/output/fortress_20261107_231749.transcript.txt 1794090356.9412 (events 8, 18, 19, 364 for spell 1044
        // on slot 24, CastFinished key 3 = 24, key 6 = 1.0); the ordinary cast path sends the same pair in SpellCasts.Finish.
        p.SendAround(new CastFinishedEvent
        {
            ddn = p.Id, bux = p.Id, buy = cast.SpellId, buz = Slot, bu0 = 0, bu1 = cast.End, bu2 = 1.0f,
        });
        p.SendAround(new CastSpellEvent
        {
            ddn = p.Id, bvw = p.Id, bvx = at, bvy = cast.SpellId, bvz = now, bv0 = now, Extra = new() { [6] = (byte)0, [7] = (short)0 },
        });
        Set(p, cast.To);
        if (cast.To == Pvp.Red) p.PvpState.RedSince = world.Clock.Now;
        var applied = false;
        foreach (var node in cast.Spell?.Effects.Where(e => e.Name.LocalName == "applyspell") ?? [])
            if ((string?)node.Attribute("target") == "self" && (string?)node.Attribute("spell") is { Length: > 0 } name)
            {
                if (name == "CANNOTDISABLEPVP" && world.Config.PvpRedLockMinutes is not null) continue; // the server's lock instead
                applied |= Pvp.ApplySpell(p, name, now, send: false);
            }
        if (applied)
        {
            SpellEffects.Send(world, p);
            SpellEffects.Refresh(world, p);
        }
        Log.Info("pvp", $"'{p.Character.Name}' is flagged {FlagName(cast.To)} ({cast.To}) on {map.Cluster}: ChangeFlaggingFinished sent");
    }

    /// <summary>
    /// <paramref name="p"/> joins a cluster with <paramref name="rules"/> (the Join, its cluster resolved): Red becomes Blue without
    /// an event (the Join's key 49 carries it, SPEC §3.1 [infer]) in a FactionPvpOnly (blue) cluster under Pvp:Mode Official, in
    /// every cluster under Off, never under Consensual (the consent is kept; Red is harmless where nobody is Hostile). A city keeps it
    /// (Red players are captured in royal cities, stageJ flag-toggle NOTES §2). A bot keeps the old rule: Blue again in a
    /// PvpDisabled or FactionPvpOnly cluster (its flag is by zone, stage I bots SPEC D4).
    /// </summary>
    public static void Arrive(Player p, ClusterPvp rules)
    {
        if (p.Flag != Pvp.Red) return;
        var mode = p.World.Config.PvpMode;
        var reset = p.IsBot
            ? rules.Rule is PvpRule.PvpDisabled or PvpRule.FactionPvpOnly
            : mode == PvpMode.Off || mode == PvpMode.Official && rules.Rule == PvpRule.FactionPvpOnly;
        if (!reset) return;
        p.Flag = Pvp.Blue;
        p.World.Saves.MarkChanged(p.Character);
        Log.Info("pvp", $"'{p.Character.Name}' arrives Red in a {rules.Rule} cluster (Pvp:Mode {mode}): Blue again");
    }

    /// <summary>
    /// Why <paramref name="p"/> may not enter a cluster with <paramref name="rules"/> (SPEC §3.1, §3.4): the ChangeCluster code and
    /// the reason, or null. Faction-flagged where allowfactionflaggedenter is false (1655), reputation below requiredreputationtoenter (1652).
    /// </summary>
    public static (short Code, string Why)? EntryRefusal(Player p, ClusterPvp rules)
    {
        if (Pvp.IsFaction(p.Flag) && !rules.AllowFactionFlaggedEnter) return (NoFactionPlayersAllowed, "faction-flagged players may not enter");
        if (rules.RequiredReputationToEnter is { } required && p.Character.Reputation < required)
            return (ReputationTooLow, $"reputation {p.Character.Reputation:0.##} is below {required}");
        return null;
    }
}
