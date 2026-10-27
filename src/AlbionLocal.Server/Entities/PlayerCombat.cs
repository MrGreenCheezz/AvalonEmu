using System.Numerics;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// A player's fight (stage 5b/5c, analysis/work/stage5/SPEC.md §5b): the combat state the client shows (InCombatStateUpdate
/// 277, with the health regeneration off while it lasts), the mobs it is fighting, the last accepted auto-attack swing,
/// the cast under way and the spell cooldowns. Lives with its <see cref="Player"/> (one game connection); only the slot
/// cooldowns are saved (stageD consumables). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class PlayerCombat
{
    /// <summary>In combat (InCombatStateUpdate key 1 sent, regeneration off) until <see cref="Systems.Combat.EndCombat"/>.</summary>
    public bool InCombat { get; internal set; }

    /// <summary>A mob has hit it in this combat (stage 5d): InCombatStateUpdate {1 true, 2 true} sent.</summary>
    public bool Hit { get; internal set; }

    /// <summary><see cref="World.Clock"/> time of the last own hit or swing: combat ends gamedata InCombat activetimeoutseconds (20 s) later.</summary>
    public TimeSpan LastAction { get; internal set; }

    /// <summary>The living mobs it fights; when the last of them dies, its combat ends at once (SPEC §5b.5).</summary>
    public HashSet<Mob> Enemies { get; } = [];

    /// <summary>
    /// <see cref="World.Clock"/> time of its last swing at or hit by another player (stageE pvp, <see cref="Systems.Combat.EnterCombat"/>
    /// with no mob); null = none in this combat. While within the combat timeout, the last mob enemy's death or reset does not end it.
    /// </summary>
    public TimeSpan? LastPvp { get; internal set; }

    /// <summary>GameTimeStamp of the last accepted AttackStart (the swing's start); 0 = none yet.</summary>
    public long LastSwing { get; internal set; }

    /// <summary>The cast with a cast time under way (CastStart accepted, CastFinished not sent yet); null when none.</summary>
    public PendingCast? Cast { get; internal set; }

    /// <summary>The channel under way after a cast (stageD effects SPEC §7 step 9, <see cref="Systems.SpellCasts.EndChannel"/>); null when none.</summary>
    public Systems.SpellCasts.ChannelState? Channel { get; internal set; }

    /// <summary>The toggles on, by CharacterSpellSlot (<see cref="Systems.SpellCasts.EndToggle"/>).</summary>
    public Dictionary<byte, Systems.SpellCasts.ToggleState> Toggles { get; } = [];

    /// <summary>The CastCancel {4 true} of toggles that ended while <see cref="Cast"/> ran: sent when it finishes or is cancelled.</summary>
    public List<Protocol.CastCancelEvent> HeldToggleCancels { get; } = [];

    /// <summary>GameTimeStamp each spell (by id) may be cast again from: cast end + recastdelay × the cooldown factor (SPEC §5b.3).</summary>
    public Dictionary<short, long> Cooldowns { get; } = [];

    /// <summary>What hit it in the death recap window (stageP SPEC §2.1, <see cref="Systems.DeathRecaps"/>), frozen at a knockdown or death.</summary>
    public Systems.DeathRecaps.Record Recap { get; } = new();

    /// <summary>Healing sickness (stageP SPEC §2.2, <see cref="Systems.PvpDetails"/>): the other players and bots that healed it, by object id, with their last heal's GameTimeStamp.</summary>
    internal Dictionary<long, long> Healers { get; } = [];

    /// <summary>Resilience (stageP SPEC §2.3): the players and bots whose hits reached it, by object id, with the last hit's GameTimeStamp.</summary>
    internal Dictionary<long, long> FocusAttackers { get; } = [];

    /// <summary>Resilience in force: the attacker count it was last raised to (2 and more) and the GameTimeStamp it ends at.</summary>
    internal (int Level, long Until) FocusFire { get; set; }

    /// <summary>
    /// The cooldowns per CharacterSpellSlot (stageD consumables SPEC §2.2, the client's a48[30]): so far the potion (12) and food
    /// (13) slots, whose spell changes with the item in them; a swapped potion does not skip the slot's cooldown. Saved with
    /// the character (<see cref="Game.Character.Cooldowns"/>) and sent at Join as SpellCooldownUpdate (249).
    /// </summary>
    public Dictionary<byte, SlotCooldown> SlotCooldowns { get; } = [];

    /// <summary>
    /// The multispells in force by CharacterSpellSlot (stageD effects batch 2, ReplaceSpellSlotWithMultiSpell 457): the slot casts the
    /// follow-up spell instead of its own until the window ends.
    /// </summary>
    public Dictionary<byte, Systems.MultiSlot> Multi { get; } = [];

    /// <summary>
    /// Its stored targets (stageN spells2, storetargets): a unit or a point (a spell area's centre) under a tag until a GameTimeStamp, oldest
    /// first; a target="storedtarget" spell (the multispell follow-ups: AFTER_IMAGE_RETURN, SHOCKWAVE_PUNCH2, ICE_SCULPTURE_EXPLODE) is cast at
    /// the newest of its storedtargettag (<see cref="Systems.SpellCasts.Stored"/>).
    /// </summary>
    public List<Systems.StoredTarget> Stored { get; } = [];

    /// <summary>The batch use from the inventory under way (UseConsumableFromInventory, <see cref="Systems.Consumables"/>); null when none.</summary>
    public Systems.Consumables.Batch? Batch { get; internal set; }

    /// <summary>The knockdown it lies in (stage 5e, <see cref="Systems.Knockdown"/>); null while it stands.</summary>
    public KnockedDownState? Down { get; internal set; }

    /// <summary><see cref="World.Clock"/> time the get-up's invulnerability buff comes (getuptimeseconds after the Respawn); null = none due.</summary>
    public TimeSpan? BuffDue { get; internal set; }

    /// <summary><see cref="World.Clock"/> time the invulnerability buff (SPAWN_KNOCKDOWN_INVULNERABILITY) runs out; null = not on it.</summary>
    public TimeSpan? InvulnerableUntil { get; internal set; }

    /// <summary>The get-up buff <see cref="BuffDue"/> brings (SPAWN_KNOCKDOWN_INVULNERABILITY; a PvP knockdown's the archetype's, stageE).</summary>
    public string UpBuff { get; internal set; } = Systems.Knockdown.BuffSpell;

    /// <summary>Knocked down, getting up with PvE protection due, or on the get-up buff: mobs neither aggro nor hit it.</summary>
    public bool Untouchable => Down is not null || InvulnerableUntil is not null
        || (BuffDue is not null && UpBuff == Systems.Knockdown.BuffSpell);
}

/// <summary>
/// A slot's cooldown (SpellCooldownUpdate keys 2, 1, 3): GameTimeStamps of its start (the cast end) and end (start + recastdelay
/// × the cooldown factor), and the base span (recastdelay, ticks) the client draws the sweep against.
/// </summary>
public readonly record struct SlotCooldown(long Start, long End, long Base);

/// <summary>
/// What knocked a player down (stageE pvp SPEC §2.6): a mob or the player itself (stage 5e), its duel opponent, another
/// player outside a duel. It picks the get-up time, the energy at the get-up and the buff (<see cref="Systems.Knockdown"/>).
/// <see cref="Dead"/> (E3, SPEC §4): the player died in a lethal cluster and lies there until it respawns (<see cref="Systems.Death"/>);
/// every refusal of a knocked-down player holds for it too.
/// </summary>
public enum KnockdownKind : byte
{
    Pve,
    Duel,
    Pvp,
    Dead,
}

/// <summary>
/// A knockdown under way (SPEC §5e.1): when it came (<see cref="World.Clock"/>), the mob that did it (KnockedDown key 2;
/// the player itself for .kill; a player since stageE) and its kind, and when the fight around it ended (no mob fights the
/// player any more), which starts the 18 s to the get-up of a PvE knockdown; null until then.
/// </summary>
public sealed class KnockedDownState(TimeSpan at, long killer, KnockdownKind kind = KnockdownKind.Pve)
{
    public TimeSpan At { get; } = at;

    public long Killer { get; } = killer;

    public KnockdownKind Kind { get; } = kind;

    public TimeSpan? CombatOver { get; internal set; }

    /// <summary>KnockedDown key 3 of the causer (a player's name, "@" + a mob's namelocatag): Died key 10 when the player dies from it (E3).</summary>
    public string KillerText { get; init; } = "";

    /// <summary>A death (<see cref="KnockdownKind.Dead"/>): the others have had its Leave (playervisibilityafterdeath) and get no NewCharacter of it.</summary>
    public bool Hidden { get; internal set; }

    /// <summary>
    /// The bleed-out of a knockdown (stageN revive SPEC §2, <see cref="Systems.Revive"/>): null = a plain knockdown (KnockedDown 165, no revive by
    /// a party mate); else the points left at <see cref="BleedOutStamp"/> (<see cref="World.Clock"/> time) and their change per second (negative).
    /// </summary>
    public double? BleedOutPoints { get; internal set; }

    public TimeSpan BleedOutStamp { get; internal set; }

    public double BleedOutRate { get; internal set; }

    /// <summary>A holy resurrection offered to it (ResurrectionOffer 218): the caster, the health share and the World.Clock deadline; null when none.</summary>
    public (Player Caster, double Restoration, TimeSpan Until, string? WaitingSpell)? Offer { get; internal set; }

    /// <summary>A death whose auto-respawn has been started (autorespawntimerinsecs over).</summary>
    public bool Respawning { get; internal set; }

    /// <summary>A respawn's cluster did not resolve: the next one goes to Death.DefaultRespawnCluster (stageE E3).</summary>
    public bool RespawnFailed { get; internal set; }
}

/// <summary>
/// A cast of a player's spell (stage 5b, SPEC §5b.1-§5b.4): the spell, the CharacterSpellSlot and the cast counter of the
/// request (keys 1 and 8), the item that grants it (its item power scales the ability power and the energy cost), the
/// target unit (CastStart key 5: a mob, or since stageJ pvp-spells D1 another player; null = the caster) or the aim point (ground
/// spells; the caster's position for self spells), the start (the
/// request's key 0, clamped) and end GameTimeStamps, and the energy it costs at its end. <see cref="CcAbilityPower"/>: the
/// item's crowd control power (ActiveSpellEffectsUpdate key 3, stageD effects SPEC §1). <see cref="MountSpell"/>: a spell of the ridden
/// mount (<see cref="Systems.Mounts.SpellSlots"/>; its source is the mount item), dropped when the rider dismounts.
/// </summary>
public sealed record PendingCast(SpellTemplate Spell, byte Slot, byte Counter, Item? Source, double AbilityPower, IUnit? Target, Vector2 Aim,
    long Start, long End, int EnergyCost)
{
    public double CcAbilityPower { get; init; } = AbilityPower;

    public bool MountSpell { get; init; }
}

/// <summary>
/// A player's open-world PvP state (stageE pvp SPEC §3, sub-step E2): the flag cast under way (<see cref="Systems.Flagging"/>), when it
/// last paid the attack penalty per victim character (one per attacktimeoutseconds, §3.4) and until when its crimes implicate it
/// (OUTLAW, ReputationImplicationUpdate 310). Lives with its <see cref="Player"/>; nothing of it is saved (the OUTLAW and
/// CANNOTDISABLEPVP spells are, as the player's own effects; the implication comes back from OUTLAW, <see cref="Systems.Pvp.RestoreImplication"/>). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class PlayerPvp
{
    /// <summary>The flag cast under way (op 354 accepted, ChangeFlaggingFinished not sent yet); null when none.</summary>
    public Systems.Flagging.FlagCast? FlagCast { get; internal set; }

    /// <summary>The execute under way (PLAYER_COUPDEGRACE on slot 26, E3: <see cref="Systems.Death.Execute"/>); null when none.</summary>
    public Systems.Death.ExecuteCast? Execute { get; internal set; }

    /// <summary>The revive of a bleeding-out ally under way (PLAYER_BLEEDOUT_REVIVE on slot 27: <see cref="Systems.Revive.Start"/>); null when none.</summary>
    public Systems.Revive.ReviveCast? Revive { get; internal set; }

    /// <summary>
    /// The players who hit it, by player, with the damage summed and the <see cref="World.Clock"/> time of the last hit (E3: the number
    /// of attackers of a death, the kill fame's shares); the hits older than the combat timeout drop out (<see cref="Systems.Death.Attackers"/>).
    /// </summary>
    public Dictionary<Player, (double Damage, TimeSpan Last)> HitBy { get; } = [];

    /// <summary><see cref="World.Clock"/> time of the last attack penalty by victim character id.</summary>
    public Dictionary<Guid, TimeSpan> Attacked { get; } = [];

    /// <summary>
    /// <see cref="World.Clock"/> time its last Red flag cast finished, for Pvp:RedLockMinutes (Systems.Flagging); null = none since the
    /// Join (a relog or a cluster change keeps no server lock [choice]).
    /// </summary>
    public TimeSpan? RedSince { get; internal set; }

    /// <summary>GameTimeStamp its implication ends (ReputationImplicationUpdate key 1); 0 = never implicated.</summary>
    public long ImplicatedUntil { get; internal set; }

    /// <summary>Implicated at GameTimeStamp <paramref name="now"/>: attacking it is no crime (SPEC §3.4).</summary>
    public bool Implicated(long now) => ImplicatedUntil > now;

    /// <summary>
    /// Stage I (pvp-goals SPEC §2.4): the GameTimeStamps of its credited PvP kills (Assist: an assist credit) and of its PvE kills (Assist:
    /// a party kill), the last minutes only, for the journal's killingspree. In memory: a logout ends a spree.
    /// </summary>
    public List<(long Ticks, bool Assist)> PvpKills { get; } = [];

    public List<(long Ticks, bool Assist)> PveKills { get; } = [];

    /// <summary>
    /// Stage I: the receivers credited for its PvP knockdown while it is still down (true: that credit gave season points), so the death
    /// that follows (an execute) completes that one result instead of being a second (<see cref="Systems.PvpGoals.OnPvpKill"/>); null =
    /// no credited knockdown. Cleared at the get-up, the death and a map change, with <see cref="HitBy"/>.
    /// </summary>
    public Dictionary<Player, bool>? DownCredits { get; internal set; }
}
