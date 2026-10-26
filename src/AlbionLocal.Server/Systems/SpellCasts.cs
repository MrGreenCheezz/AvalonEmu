using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>Why a cast or auto-attack is refused (logged; the code the client gets is in the handler).</summary>
public enum CastRefusal
{
    None,
    NoSpell,
    NotInSlot,
    NoTarget,
    OutOfRange,
    Cooldown,
    NoEnergy,
    KnockedDown,
    Stunned,
    Silenced,

    /// <summary>An enemy spell at a unit that is not Hostile to the caster (stageJ pvp-spells SPEC §2.4).</summary>
    NotHostile,

    /// <summary>A friendly spell at a unit that is not the caster's Friend (a Hostile player, a mob, a stranger under Pvp:FriendlySpells).</summary>
    NotFriend,

    /// <summary>A friendly spell at or from a duelist under Pvp:DuelIsolation.</summary>
    InDuel,

    /// <summary>A spell with usableincombat="false" (OUTOFCOMBATHEAL) while the caster is in combat (stageM bots).</summary>
    InCombat,
}

/// <summary>
/// A player's auto-attacks and spells (stage 5b, analysis/work/stage5/SPEC.md §5b.1-§5b.4; ARCHITECTURE.md §6.4): the swing
/// of an AttackStart, the start, end and hit of a CastStart with its events to the caster and the observers, and the
/// effect tree of spells.xml run tag by tag at the hit (<see cref="Execute"/>). Every timed step is a map timer at the
/// GameTimeStamp the client predicted (<see cref="World.ClockAt"/>). Effects run: directattributechange on health and
/// energy (single target origin 2, with effectarearadius origin 3 on every enemy in the radius; changetype relativemax,
/// relativecurrent, relativemissing, absolute), attributechangeovertime (origin 5, its ticks at initialinterval and every
/// interval, × the stacks of its entry), applyspell and pulsingspell (the named spell on the effect's target, now or per
/// pulse), and since stageD (analysis/work/stageD/effects/SPEC.md §1-§3, batch 1 part A) every timed sub-effect a spell
/// leaves on its target as an entry of the target's list (<see cref="SpellEffects"/>, ActiveSpellEffectsUpdate 11 before the
/// hit's CastHit and HealthUpdate): buffovertime, spellimmunity (a blocked spell: CastHit hit type 2), cceffectimmunity,
/// dummy, invincibility, stun, root, silence, cannotattack; removeactivespell and removeactiveeffects; nop as a no-op. Part B
/// (§3.4, §3.5, §4, §5, §7 step 9): spelleffectarea (<see cref="SpellAreas"/>), dash, knockback, pull and forcedmovement
/// (<see cref="SpellMovement"/>), damageshield, aura, applyeffectoneventactive, nothittablebyothers and notselectable, and the
/// channel of a spell that has one (<see cref="EndChannel"/>). Conditions: not/and/or, IfTargetRange, IfTargetType, IfChance,
/// IfSpellActive, IfSpellCategoryActive, IfSpellEffectActive, IfConsumeCharges (takes the charges), IfOwnLoadPercent (
/// the carried load in %, Systems.Passives), IfOwnEnergyPercentage, IfOwnHealthPercentage, Event (a trigger: true); every other If* is
/// false. StageD effects batch 2/3 (SPEC §7, SpellCasts.Extra.cs): multispell, teleport, resetcooldowns, the threat effects, interruptcast,
/// spawnmob, removespelleffectareasinarea, generateloot, and the worn items' passives (<see cref="Passives"/>). Every other tag is a no-op
/// logged once with its count in spells.xml. StageD consumables (analysis/work/stageD/consumables/SPEC.md): the potion and food
/// slots (12/13, <see cref="Consumables"/>), the leading removals of a spell before its listing, the consumable hit order
/// (<see cref="ExecuteFirst"/>) and the tome, focus and silver tags. Toggles (togglespell): on until TerminateToggleSpell or their
/// effects run out, the cooldown from their end (<see cref="EndToggle"/>). Touched only under <see cref="World.Sync"/>.
/// </summary>
public static partial class SpellCasts
{
    /// <summary>How early a swing or cast may come before its interval or cooldown ends: the client predicts (SPEC §5b.1, §5b.5).</summary>
    public static readonly TimeSpan Early = TimeSpan.FromMilliseconds(150);

    /// <summary>The range tolerance on top of attackrange or castrange (SPEC §5b.1).</summary>
    public const float RangeTolerance = 1;

    /// <summary>How far a request's key 0 may lie in the past: start = clamp(key 0, now − 1 s, now) (SPEC §5b.1).</summary>
    public static readonly TimeSpan StartWindow = TimeSpan.FromSeconds(1);

    /// <summary>CastFinished key 6 without cooldown reduction (an official value too, §9 Q18).</summary>
    public const float CooldownFactor = 1f;

    /// <summary>
    /// CastFinished key 6 and the cooldown factor of a cast of <paramref name="p"/>: 1 / (1 + Σ magiccooldownreduction) (bej.a_t3356's
    /// adjusted conversion, stageD effects SPEC §3.1); 1 without buffs.
    /// </summary>
    public static float CooldownFactorOf(Player p) =>
        p.Effects.Idle ? CooldownFactor : (float)(1 / Math.Max(0.01, 1 + p.Effects.Sum("magiccooldownreduction", p.World.GameNow)));

    /// <summary>The effect tags <see cref="Execute"/> runs, or skips on purpose (client-side: nop).</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    {
        "directattributechange", "attributechangeovertime", "applyspell", "pulsingspell", "dash", "nop", "dummy", "buffovertime",
        "spellimmunity", "cceffectimmunity", "invincibility", "stun", "root", "silence", "cannotattack", "removeactivespell",
        "removeactiveeffects",
        // Part B (stageD effects SPEC §3.4, §3.5, §4, §5).
        "spelleffectarea", "knockback", "pull", "forcedmovement", "damageshield", "aura", "applyeffectoneventactive", "nothittablebyothers",
        "notselectable",
        // StageD consumables (SPEC §4.3): what the items used from the inventory do.
        "adddestinyboardpvefame", "awardcraftingfocus", "awardsilver",
        // StageD mounts: DISMOUNTED's PvP flag, listed only (no PvP). castspellaftermoving is left out of the report's count: it runs
        // only in a mounted buff (Systems.Mounts.Update), not in the other spells that have it (AXE_CHARGE_CONDITION, …).
        "cannotattackplayers",
        // StageD effects batch 2 (SPEC §7): the passives of the worn items (Systems.Passives), the trigger, threat and slot effects.
        "buff", "applyeffectoneventpassive", "pulsingspellpassive", "healonhitpassive", "dummypassive", "healonhit", "reflectdamageactive",
        "clamphitpoints", "invisibility", "cannotreceivehealing", "multispell", "teleport", "resetcooldowns", "changeaggro",
        "maketargettopthreat", "matchhighestthreatlevel", "interruptcast",
        // Batch 3: summons, the carry load, loot items and chests, gathering yield, removing areas.
        "spawnmob", "weightbonus", "generateloot", "resourcegatheringbuff", "resourcegatheringbuffovertime", "removespelleffectareasinarea",
        "nothittable", // listed since part B (SpellEffects.NotHittable), not counted before
        // Batch 3, the rest that acts in a fight: the shapeshifter forms and their end, casts nothing interrupts, the mob buffs and threat,
        // chains, random picks, the slot and cooldown effects, removing areas, the diminishing returns, the timed carry load.
        "portalrestriction", "transformation", "endtransformation", "notinterruptible", "cannotaggro", "gainenergyondamage", "mobdamagemodifier",
        "mobhealthmodifier", "weightbonusovertime", "removespelleffectareasoftarget", "removespelleffectareasaffectingtarget", "chainspell",
        "cancelmultispell", "applyrandomspell", "setcrowdcontroldiminishingreturns", "terminatetogglespells", "setcooldowns", "removefromthreatlist",
        // StageN revive: the holy resurrection's offer (Systems.Revive.Offer).
        "resurrect",
        // StageN spells-bosses: learning point books (Consumables.Award ran them only in name), the gathering tomes, the fish food's
        // fishing fame, the repair kits, the fame buffs (Combat.Kill: FameBuff).
        "awardlearningpoints", "adddestinyboardgatheringfame", "adddestinyboardfishingfame", "repairequipment", "mobfamebycategoryovertime",
        // StageN spells2: the stored targets of the multispell follow-ups, and two tags run outside this list all along: a dungeon map's
        // activaterandomdungeon (Execute, Consumables) and castspellaftermoving (the mounts' speed buff, Systems.Mounts.Update).
        "storetargets", "removestoredtargets", "activaterandomdungeon", "castspellaftermoving",
    };

    /// <summary>
    /// The tags <see cref="Execute"/> takes as no-ops on purpose, logged once (the start-up report counts them apart): the avatar and
    /// vanity unlocks of the season rewards and skins. TODO: the account's unlocks (avatars, avatar rings, mount skins) once they are kept.
    /// </summary>
    public static readonly IReadOnlySet<string> NoOps = new HashSet<string>(StringComparer.Ordinal) { "unlockavatar", "unlockvanityunlock" };

    /// <summary>A request's start: its key 0 clamped to [now − 1 s, now].</summary>
    public static long StartOf(World world, long requested)
    {
        var now = world.GameNow;
        return Math.Clamp(requested, now - StartWindow.Ticks, now);
    }

    /// <summary>Runs <paramref name="action"/> at GameTimeStamp <paramref name="ticks"/>: now when that has come, else by a map timer.</summary>
    public static void At(World world, Map map, long ticks, Action action)
    {
        if (ticks <= world.GameNow) action();
        else map.Timers.At(world.ClockAt(ticks), action);
    }

    // ---- auto-attack (AttackStart) ----

    /// <summary>
    /// An accepted AttackStart (op 23) of <paramref name="p"/> on <paramref name="target"/>, combo index <paramref name="combo"/>,
    /// started at <paramref name="start"/>: Attack (13) to the players who see the attacker (never to itself: it predicts)
    /// {0 attacker, 1 start, 2 target, 3 type (1 melee, 2 ranged, 4 magic), 4 start + attackdamagetime × the weapon's
    /// attackdamagetimefactor, 5 the impact (+ the projectile's flight for ranged and magic), 6 the combo index}, the attacker
    /// in combat, and at the impact the damage: attackdamage × attackdamageprogression^(IP/100) × the combo factor × the
    /// mob's resistance, HealthUpdate {5: 0 melee, 1 ranged/magic; 7: -1} stamped at the impact.
    /// </summary>
    public static void Swing(Player p, Mob target, byte combo, long start)
    {
        var world = p.World;
        var map = p.Map!;
        var weapon = Combat.WeaponOf(p);
        var damageTime = (world.GameData?.AttackDamageTimes.GetValueOrDefault(weapon.Type) ?? 0.25) * weapon.DamageTimeFactor;
        var damageAt = start + Seconds(damageTime);
        var flight = weapon.Type == "melee" ? 0 : Vector2.Distance(p.PositionAt(DateTime.UtcNow), target.Position) / world.Combat.AutoAttackProjectileSpeed;
        var impact = damageAt + Seconds(flight);
        p.Combat.LastSwing = start;
        p.SendToObservers(new AttackEvent
        {
            ddn = p.Id, bs0 = start, bs1 = target.Id, bs2 = weapon.AttackType, bs3 = damageAt, bs4 = impact, Extra = new() { [6] = combo },
        });
        if (!target.Immune) Combat.EnterCombat(p, target); // a mob running home (spell immunity, 5d) does not fight: no combat with it
        SpellEffects.Trigger(p, "AutoAttack", target, start);
        var raw = (weapon.Progression is { } prog ? Combat.AutoAttack(weapon.Damage, prog.AttackDamage, weapon.ItemPower) : weapon.Damage)
            * SpellEffects.DealtFactor(p, spell: false, weapon.Effect, vsPlayer: false, world.GameNow);
        var factor = world.Combat.Combo(weapon.Item?.Template.Name, combo);
        At(world, map, impact, () =>
        {
            if (p.Map != map || p.Combat.Down is not null) return; // knocked down meanwhile (5e): the swing is lost
            Combat.Damage(p, target, raw * factor * Combat.ResistanceOf(target, weapon.Effect), weapon.Effect, weapon.Origin, -1, impact);
        });
    }

    /// <summary>The swing interval of the player's weapon: 1 / (attackspeed × (1 + Σ attackspeedbonus)) (T4_MAIN_FIRESTAFF 1.35 → 0.74 s).</summary>
    public static long SwingInterval(Player p) =>
        Seconds(1 / Math.Max(0.01, Combat.WeaponOf(p).Speed * Math.Max(0.01, 1 + p.Effects.Sum("attackspeedbonus", p.World.GameNow))));

    // ---- casts (CastStart) ----

    /// <summary>
    /// The checks of a CastStart (SPEC §5b.1, in this order, after "the caster is alive": not knocked down, 5e): the spell
    /// is known and active and is in CharacterSpellSlot <paramref name="slot"/> (the stage-1 spell choice); the target (key 5,
    /// <see cref="CastTarget"/>) or the aim point (ground spells) is within castrange + 1 m; the cooldown has run out (150 ms early
    /// allowed); the energy is there. Since stageD not stunned unless the spell is castablewhenstunned (the cleanses, HOLYTOUCH; stageJ
    /// pvp-spells phase 0), and not silenced unless it is castablewhensilenced; not in combat (a bot: in a fight) when it is
    /// usableincombat="false" (stageM bots: OUTOFCOMBATHEAL); the cast time is castingtime / (1 + Σ
    /// magiccasttimereduction) (SPEC §3.1).
    /// <paramref name="mount"/>: the ridden mount item when <paramref name="slots"/> is its spell-slot layout (a mount spell,
    /// <see cref="Mounts.SpellSlots"/>): it grants the spell (ability power, energy) instead of the equipment in that slot.
    /// <paramref name="grantedBy"/>: the worn item an extra-slot spell comes from (<see cref="ExtraBars"/>; null for a plugin's override: the
    /// main hand's power then), its ability power and energy cost as a slot's item.
    /// Returns the cast to start, or the refusal.
    /// </summary>
    public static (PendingCast? Cast, CastRefusal Why) Check(Player p, int[] slots, byte slot, int spellId, long targetId, Vector2 aim, byte counter, long start,
        Item? mount = null, Item? grantedBy = null)
    {
        var world = p.World;
        if (p.Combat.Down is not null) return (null, CastRefusal.KnockedDown);
        if (world.Spells?.Template(spellId) is not { Kind: "activespell" or "togglespell" } spell) return (null, CastRefusal.NoSpell);
        if (!spell.CastableWhenStunned && SpellEffects.Stunned(p, start)) return (null, CastRefusal.Stunned);
        if (!spell.CastableWhenSilenced && SpellEffects.Silenced(p, start)) return (null, CastRefusal.Silenced);
        if (!spell.UsableInCombat && (p.Combat.InCombat || p.Bot is { Fight.Active: true })) return (null, CastRefusal.InCombat);
        if ((slot >= slots.Length || slots[slot] != spellId) && !MultiAllows(p, slot, spellId, start)) return (null, CastRefusal.NotInSlot);
        var here = p.IsBot ? SpellMovement.PositionOf(p, start) : p.PositionAt(DateTime.UtcNow); // a bot's samples are on the game clock (phase 4)
        IUnit? target = null;
        var point = here;
        var cls = SpellTargets.Parse(spell.Target);
        if (cls == SpellTargets.Cls.Ground)
        {
            point = aim;
            if (Vector2.Distance(here, aim) > spell.CastRange + RangeTolerance) return (null, CastRefusal.OutOfRange);
        }
        else if (cls == SpellTargets.Cls.StoredTarget && Stored(p, spell, start) is { } stored)
        {
            // StageN spells2: the newest stored target of its tag (a unit, or a point such as AFTER_IMAGE's image), within castrange + 1 m.
            target = stored.Unit;
            point = stored.Unit is { } u ? SpellMovement.PositionOf(u, start) : stored.Point;
            if (Vector2.Distance(here, point) > spell.CastRange + RangeTolerance + (stored.Unit is Mob sm ? sm.Template.CollisionRadius : 0)) return (null, CastRefusal.OutOfRange);
        }
        else if (SpellTargets.NamesTarget(cls))
        {
            var (unit, why) = CastTarget(p, spell, cls, targetId, here, start);
            if (why != CastRefusal.None) return (null, why);
            target = unit;
            if (unit is not null) point = SpellMovement.PositionOf(unit, start);
        }
        if (p.Combat.Cooldowns.TryGetValue(spell.Id, out var ready) && start < ready - Early.Ticks) return (null, CastRefusal.Cooldown);
        // The potion and food slots keep their own cooldown (stageD consumables SPEC §2.2): a swapped potion does not skip it.
        if (p.Combat.SlotCooldowns.TryGetValue(slot, out var slotReady) && start < slotReady.End - Early.Ticks) return (null, CastRefusal.Cooldown);
        var source = mount ?? grantedBy ?? Combat.SpellSource(p.Inventory, slot); // grantedBy: an extra slot's worn item (ExtraBars)
        var cost = Combat.EnergyCost(p, spell, source);
        if (p.Energy.At(start) < cost) return (null, CastRefusal.NoEnergy);
        var castTime = spell.CastingTime / Math.Max(0.01, 1 + p.Effects.Sum("magiccasttimereduction", start)); // stageD SPEC §3.1 correction
        var end = start + Seconds(castTime);
        return (new PendingCast(spell, slot, counter, source, Combat.AbilityPowerOf(p, source), target, point, start, end, cost)
            { CcAbilityPower = Combat.CcAbilityPowerOf(p, source), MountSpell = mount is not null }, CastRefusal.None);
    }

    /// <summary>
    /// The target of a CastStart whose class names one (stageJ pvp-spells SPEC §2.4; <see cref="SpellTargets.NamesTarget"/>): key 5 = 0 or
    /// the caster's own id is the caster (null), refused when the class does not take it (FriendOther, Other, Enemy); another player
    /// (a bot too) must be in view, match the class (<see cref="SpellTargets.Cast"/>: a Friend for a friendly spell, Hostile for an enemy one,
    /// phase 2: the auto-attacks' <see cref="Pvp.Refusal"/> under Pvp:Spells, refused as NotHostile) and stand within castrange + 1 m edge to
    /// edge; a mob must be known, attackable, match the class (a friendly spell on a
    /// hostile mob is refused) and be within reach. Anything else: no target.
    /// </summary>
    private static (IUnit? Target, CastRefusal Why) CastTarget(Player p, SpellTemplate spell, SpellTargets.Cls cls, long id, Vector2 here, long now)
    {
        if (id == 0 || id == p.Id) return (null, SpellTargets.Cast(cls, SpellTargets.Of(p, p, now)) ? CastRefusal.None : CastRefusal.NoTarget);
        var map = p.Map!;
        if (map.FindPlayer(id) is { } q)
        {
            // A companion sees no other bot (it has no client), nor does a world bot (bot support roles: a healer's group mates).
            if (!p.Observers.Contains(q) && !((p.IsCompanion || p.IsBot && q.IsBot) && q.Map == p.Map)) return (null, CastRefusal.NoTarget);
            var facts = SpellTargets.Of(p, q, now);
            if (!SpellTargets.Cast(cls, facts))
                return (null, Enemy(cls) ? CastRefusal.NotHostile
                    : p.World.Config.PvpDuelIsolation && (p.World.Duels.Of(p) is not null || p.World.Duels.Of(q) is not null) ? CastRefusal.InDuel : CastRefusal.NotFriend);
            return Pvp.EdgeDistance(p, here, q) > spell.CastRange + RangeTolerance ? (null, CastRefusal.OutOfRange) : (q, CastRefusal.None);
        }
        if (map.Objects.GetValueOrDefault(id) is not Mob { Attackable: true } mob || !p.Visibility.Knows(mob) && !p.IsCompanion) return (null, CastRefusal.NoTarget);
        if (!SpellTargets.Cast(cls, SpellTargets.Of(p, mob, now))) return (null, Enemy(cls) ? CastRefusal.NoTarget : CastRefusal.NotFriend);
        return Combat.EdgeDistance(p, here, mob) > spell.CastRange + RangeTolerance ? (null, CastRefusal.OutOfRange) : (mob, CastRefusal.None);
    }

    /// <summary>The enemy classes (Enemy … EnemyAliveOrKnockedDownNotPassive, KnockedDownEnemyPlayer, EnemyFaction): their refusal is NotHostile, the others' NotFriend.</summary>
    internal static bool Enemy(SpellTargets.Cls cls) =>
        cls is >= SpellTargets.Cls.Enemy and <= SpellTargets.Cls.EnemyAliveOrKnockedDownNotPassive or SpellTargets.Cls.KnockedDownEnemyPlayer or SpellTargets.Cls.EnemyFaction;

    /// <summary>
    /// Whether a cast of <paramref name="spell"/> without another unit as its target names the caster in CastStart key 7, CastFinished key 1
    /// and CastSpell key 1 (stageJ pvp-spells SPEC §1.4, §2.4, phase 0: captured self 45/45, all 21/21): every class but Ground (and
    /// StoredTarget, None), whose cast names no unit.
    /// </summary>
    private static bool NamesCaster(SpellTemplate spell) =>
        SpellTargets.Parse(spell.Target) is not (SpellTargets.Cls.Ground or SpellTargets.Cls.StoredTarget or SpellTargets.Cls.None);

    /// <summary>
    /// Starts an accepted cast: a cast still under way is cancelled first; the observers get CastStart (14) {0 caster, 1 start, 2 aim (a
    /// ground cast only: left out for a cast at a unit or on oneself, as the official server, stageJ pvp-spells SPEC §2.10), 4 end (= start
    /// for instants), 5 spell, 6 the source item's items.xml index, 7 target (the caster itself for a cast on oneself, stageJ pvp-spells
    /// phase 0; left out for a ground cast), 8 counter, 9 slot}; an instant spell finishes in the same tick (the three result events in the
    /// request's batch), one with a cast time when its end comes (a map timer). A channel under way ends first (ChannelingEnded {1 2}).
    /// </summary>
    public static void Start(Player p, PendingCast cast)
    {
        if (p.Combat.Cast is { } running) Cancel(p, running, interrupted: false, flush: false); // the toggles' CastCancel waits for this one
        if (p.Combat.Channel is not null) EndChannel(p, ChannelEnd.Cancelled);
        p.SendToObservers(new CastStartEvent
        {
            ddn = p.Id, bwb = cast.Start, bwc = SpellTargets.Parse(cast.Spell.Target) == SpellTargets.Cls.Ground ? cast.Aim : default, bwe = cast.End, bwf = cast.Spell.Id, bwg = (short)(cast.Source?.Template.Index ?? -1),
            bwh = cast.Target?.Id ?? (NamesCaster(cast.Spell) ? p.Id : 0), Extra = new() { [8] = cast.Counter, [9] = cast.Slot },
        });
        if (cast.End <= cast.Start)
        {
            Finish(p, cast);
            SendHeldToggleCancels(p);
            return;
        }
        p.Combat.Cast = cast;
        var map = p.Map!;
        At(p.World, map, cast.End, () =>
        {
            if (p.Combat.Cast != cast || p.Map != map) return;
            p.Combat.Cast = null;
            Finish(p, cast);
            SendHeldToggleCancels(p);
        });
    }

    /// <summary>
    /// Drops a cast under way. The observers get CastCancel (16) {0 caster, 1 true when interrupted, 2 counter, 9 byte 0};
    /// the caster gets nothing for its own CastCancel, and for a server-side interruption CastCancel {0, 1 true, 2 counter,
    /// 3 the cooldown factor} (SPEC §5b.4). The toggles' CastCancel held back while it ran go out then, unless a new cast replaces
    /// it (<paramref name="flush"/> false, <see cref="Start"/>: the client is in that one's cast already).
    /// </summary>
    public static void Cancel(Player p, PendingCast cast, bool interrupted, bool flush = true)
    {
        if (p.Combat.Cast == cast) p.Combat.Cast = null;
        p.World.Plugins.CastCancelled(p, cast, interrupted); // the plugins' Spells.OnCancel
        p.SendToObservers(new CastCancelEvent { ddn = p.Id, buo = interrupted, bup = cast.Counter, Extra = new() { [9] = (byte)0 } });
        if (interrupted) p.Session.Send(new CastCancelEvent { ddn = p.Id, buo = true, bup = cast.Counter, buq = CooldownFactorOf(p) });
        if (flush && p.Combat.Cast is null) SendHeldToggleCancels(p);
    }

    /// <summary>
    /// The end of a cast (SPEC §5b.4): a cast whose target no longer matches its class is interrupted (an enemy mob died, a friend
    /// left, was knocked down or turned Hostile; stageJ pvp-spells SPEC §2.4); otherwise the energy is
    /// charged now (not at the start) and the caster and its observers get, in this order, EnergyUpdate (8) {0 caster, 1 now,
    /// 2 -cost (left out at 0), 3 new energy, 4 byte 7, 5 caster, 6 spell}, CastFinished (18) {0 caster, 1 target (left out
    /// for ground casts), 2 spell, 3 slot, 4 counter, 5 Int64 start + cast time, 6 cooldown factor} and CastSpell (19) {0
    /// caster, 1 target, 2 aim or target position, 3 spell, 4 cast end, 5 hit, 6 counter, 7 source item index 0}; the
    /// cooldown runs from the end. The hit comes hitdelay later (+ distance / flyspeed for a projectile), then the spell's
    /// channel, if it has one (<see cref="StartChannel"/>). The caster's CastFinished triggers (applyeffectoneventactive) run.
    /// StageD consumables (SPEC §2.1): a self spell names the caster in CastFinished key 1 and CastSpell key 1 (CastSpell key 2 its
    /// position), since stageJ pvp-spells phase 0 every cast on oneself but a ground cast; a cast from the potion or food slot (12/13) takes one item of the stack after the EnergyUpdate
    /// (<see cref="Consumables.UseSlotItem"/>; a stack moved out of the slot meanwhile interrupts the cast and costs nothing),
    /// starts the slot's cooldown and runs its effects in the consumable order (<see cref="Consumables.Hit"/>).
    /// </summary>
    public static void Finish(Player p, PendingCast cast)
    {
        var world = p.World;
        var map = p.Map;
        if (map is null) return;
        var consumable = Consumables.IsSlot(cast.Slot);
        var storedCast = SpellTargets.Parse(cast.Spell.Target) == SpellTargets.Cls.StoredTarget; // stageN spells2: its unit was checked when stored
        if (cast.Target is { } aimed && !storedCast && !SpellTargets.Cast(SpellTargets.Parse(cast.Spell.Target), SpellTargets.Of(p, aimed, cast.End))
            || consumable && !Consumables.InSlot(p, cast))
        {
            Cancel(p, cast, interrupted: true);
            return;
        }
        var spell = cast.Spell;
        var energyBefore = p.Energy.At(cast.End);
        var energy = energyBefore - cast.EnergyCost;
        p.Energy.Set(energy, Math.Max(cast.End, p.Energy.Stamp));
        world.Plugins.EnergyChanged(p, energyBefore, energy, p.Id, spell.Id); // the plugins' Units.OnEnergyChange
        var point = cast.Target is { } at ? SpellMovement.PositionOf(at, cast.End) : spell.Target == "ground" || storedCast ? cast.Aim : p.PositionAt(DateTime.UtcNow);
        var flight
 = spell.ProjectileSpeed is { } speed ? Vector2.Distance(p.PositionAt(DateTime.UtcNow), point) / speed : 0;
        var hit = cast.End + Seconds(spell.HitDelay + flight);
        var targetId = cast.Target?.Id ?? (NamesCaster(spell) ? p.Id : 0);
        var cooldown = CooldownFactorOf(p);
        // The plugins' Formulas.OnCalcCooldown: the client is told the changed cooldown as CastFinished's factor (recast × key 6).
        var recast = world.Plugins.CalcCooldown(p, spell, cast.Target, spell.RecastDelay * cooldown);
        if (spell.RecastDelay > 0 && recast != spell.RecastDelay * cooldown) cooldown = (float)(recast / spell.RecastDelay);
        p.SendAround(new EnergyUpdateEvent { ddn = p.Id, b5b = cast.End, b5c = -cast.EnergyCost, b5d = p.Energy.Value, b5e = Combat.EnergyCast, b5f = p.Id, b5g = spell.Id });
        if (consumable) Consumables.UseSlotItem(p, cast);
        p.SendAround(new CastFinishedEvent
        {
            ddn = p.Id, bux = targetId, buy = spell.Id, bu1 = cast.End, bu2 = cooldown,
            Extra = new() { [3] = cast.Slot, [4] = cast.Counter },
        });
        p.SendAround(new CastSpellEvent
        {
            ddn = p.Id, bvw = targetId, bvx = point, bvy = spell.Id, bvz = cast.End, bv0 = hit,
            Extra = new() { [6] = cast.Counter, [7] = (short)0 },
        });
        // A toggle's cooldown runs from its end (TerminateToggleSpell, its effects running out, …), not from the cast (EndToggle).
        var toggle = !consumable && spell.Kind == "togglespell" ? StartToggle(p, cast, hit) : null;
        if (toggle is null) p.Combat.Cooldowns[spell.Id] = cast.End + Seconds(spell.RecastDelay * cooldown);
        if (toggle is null && ExtraBars.Modded(p)) world.ExtraBars.Cooled(p, [spell.Id]); // stageN extra bars: event 9002 for the slot holding it
        if (consumable) p.Combat.SlotCooldowns[cast.Slot] = new SlotCooldown(cast.End, cast.End + Seconds(spell.RecastDelay * cooldown), Seconds(spell.RecastDelay));
        MultiCast(p, cast);
        world.Plugins.CastDone(p, cast, point, cast.AbilityPower); // the plugins' Spells.OnCast, Mounts.OnMountSpell, a scripted spell's OnCast
        OpeningTutorial.SpellFinished(p, spell);
        Log.Debug("combat", string.Create(CultureInfo.InvariantCulture,
            $"'{p.Character.Name}' cast {spell} ({cast.EnergyCost} energy, AP {cast.AbilityPower:0.#}){(cast.Target is { } t ? $" on {t}" : "")}"));
        var context = new SpellContext(p, map, spell, cast.Counter, cast.AbilityPower, cast.Target, point, hit)
            { CcAbilityPower = cast.CcAbilityPower, Item = consumable ? cast.Source?.Template : null, Toggle = toggle };
        SpellEffects.Trigger(p, "CastFinished", cast.Target, cast.End, cast.Slot);
        SpellEffects.Break(world, p, "casting", cast.End);
        At(world, map, hit, () =>
        {
            if (p.Map != map || context.ToggledOff) return;
            // The plugins' Spells.OnBeforeHit (PreventDefault) and a scripted spell's OnHit (ReplaceEffects), then OnAfterHit.
            if (world.Plugins.BeforeHit(context, cast))
            {
                if (consumable) Consumables.Hit(context, spell);
                else Execute(context, spell);
            }
            world.Plugins.AfterHit(context, cast);
        });
        StartChannel(p, context, cast);
    }

    // ---- channels (stageD effects SPEC §7 step 9) ----

    /// <summary>How a channel ends: ChannelingEnded key 1 3 when completed, 2 when cancelled or interrupted (inferred from timing, SPEC §9 Q9).</summary>
    public enum ChannelEnd { Completed, Cancelled, Interrupted }

    /// <summary>A channel under way: the cast spell, its channel spell (the next id), the cast counter, its start (the cast end) and end.</summary>
    public sealed class ChannelState(SpellTemplate parent, SpellTemplate channel, byte counter, long start, long end)
    {
        public SpellTemplate Parent { get; } = parent;
        public SpellTemplate Template { get; } = channel;
        public ChannelInfo Info => Template.Channel!;
        public byte Counter { get; } = counter;
        public long Start { get; } = start;
        public long End { get; } = end;

        /// <summary>Ended (completed, cancelled or interrupted): its ticks and linked areas stop.</summary>
        public bool Over { get; internal set; }

        /// <summary>The context its ticks and end effect run with.</summary>
        internal SpellContext? Context { get; set; }
    }

    /// <summary>
    /// The channel of a finished cast whose spell has one (PARRY, BLOCK, OUTOFCOMBATHEAL, PYROBLAST_SKILLSHOT): effectcount ticks of the
    /// channel spell at cast end + initialeffectinterval + k × effectinterval, then at the last one ChannelingEnded (24) {0 caster,
    /// 1 3, 2 counter, 3 the cooldown factor} (HAMMER_TREMOR 1.6 s, DEFLECTINGSTANCE 3.5 s, PYROBLAST 1.2 s after the cast end, as
    /// captured) and its endeffect. Its linktochanneling sub-effects and areas end with it (<see cref="EndChannel"/>).
    /// </summary>
    private static void StartChannel(Player p, SpellContext c, PendingCast cast)
    {
        var world = p.World;
        if (!cast.Spell.HasChannel || world.Spells?.Template(cast.Spell.Id + 1) is not { Channel: { } info } channel || p.Map is not { } map) return;
        if (p.Combat.Channel is not null) EndChannel(p, ChannelEnd.Cancelled);
        var state = new ChannelState(cast.Spell, channel, cast.Counter, cast.End, cast.End + Seconds(info.Seconds));
        var context = c with { Channel = state, Depth = c.Depth + 1 };
        state.Context = context;
        p.Combat.Channel = state;
        Log.Debug("combat", $"'{p.Character.Name}' channels {channel}: {info.Count} ticks, {info.Seconds:0.##} s");
        for (var k = 0; k < info.Count; k++)
        {
            var ticks = cast.End + Seconds(info.Initial + k * info.Interval);
            var tick = k + 1; // IfChannelTick counts from 1
            At(world, map, ticks, () =>
            {
                if (p.Combat.Channel != state || p.Map != map) return;
                var at = context with { Ticks = ticks, ChannelTick = tick, Point = c.Target is { } t ? SpellMovement.PositionOf(t, ticks) : cast.Spell.Target == "ground" ? cast.Aim : SpellMovement.PositionOf(p, ticks) };
                if (world.Plugins.ChannelTick(p, cast, channel, tick, info.Count, at.Target, at.Point, at.AbilityPower)) Execute(at, channel); // Spells.OnTick, a script's OnTick
            });
        }
        At(world, map, state.End, () =>
        {
            if (p.Combat.Channel == state && p.Map == map) EndChannel(p, ChannelEnd.Completed);
        });
    }

    /// <summary>
    /// The end of <paramref name="p"/>'s channel: ChannelingEnded {0 me, 1 3 completed / 2 otherwise, 2 counter, 3 the cooldown
    /// factor} to it and its observers; its linktochanneling sub-effects (on itself, the mobs and the other players: a friend's buff)
    /// switched off, the lists sent; its
    /// linked areas left; its endeffect run (on the caster, at its target) unless a cancel with cancelpreventsendeffects or an
    /// interruption with interruptpreventsendeffects. Called at its end, by a Move (allowmovement="false"), CastCancel (allowcancel),
    /// a new cast, damage (damagecancelscasting), a stun and the knockdown.
    /// </summary>
    public static void EndChannel(Player p, ChannelEnd how)
    {
        if (p.Combat.Channel is not { } state) return;
        var world = p.World;
        p.Combat.Channel = null;
        state.Over = true;
        p.SendAround(new ChannelingEndedEvent
        {
            ddn = p.Id, bwu = how == ChannelEnd.Completed ? 3 : 2, bwv = state.Counter, bww = CooldownFactorOf(p),
        });
        Log.Debug("combat", $"'{p.Character.Name}' channel {state.Template} {how}");
        if (p.Map is not { } map) return;
        IEnumerable<IUnit> units = [p, .. map.Objects.Values.OfType<Mob>(), .. map.Characters.Where(q => q != p)];
        foreach (var unit in units)
        {
            var changed = false;
            foreach (var entry in unit.Effects.Spells.Where(s => s.Caster == p.Id && s.Start >= state.Start))
                foreach (var sub in entry.Subs.Where(s => s.Active && SpellTemplate.Bool(s.Node, "linktochanneling")))
                {
                    sub.Active = false;
                    changed = true;
                }
            if (!changed) continue;
            SpellEffects.Send(world, unit);
            SpellEffects.Refresh(world, unit);
        }
        SpellAreas.OwnerGone(p, state);
        var info = state.Info;
        if (info.EndEffect is not { } name || world.Spells?.Template(name) is not { } end || p.Combat.Down is not null) return;
        if ((how == ChannelEnd.Cancelled && info.CancelPreventsEnd) || (how == ChannelEnd.Interrupted && info.InterruptPreventsEnd)) return;
        var now = world.GameNow;
        Execute(state.Context! with { Ticks = now, Point = SpellMovement.PositionOf(state.Context!.Target ?? p, now) }, end);
    }

    // ---- toggles ----

    /// <summary>Why a toggle ends (logged; the client gets the same CastCancel for all but <see cref="ToggleEnd.Left"/>).</summary>
    public enum ToggleEnd { Terminated, Expired, Removed, NoEnergy, Recast, KnockedDown, Mount, Left }

    /// <summary>
    /// A toggle under way (togglespell, the client's bi5): the spell, its CharacterSpellSlot and cast counter, its start (the cast
    /// end) and when its effects run out (<see cref="ToggleSeconds"/> after the hit).
    /// </summary>
    public sealed class ToggleState(SpellTemplate spell, byte slot, byte counter, long start, long end)
    {
        public SpellTemplate Spell { get; } = spell;
        public byte Slot { get; } = slot;
        public byte Counter { get; } = counter;
        public long Start { get; } = start;
        public long End { get; } = end;

        /// <summary>Ended: its hit, pulses and over-time ticks still to come are dropped.</summary>
        public bool Over { get; internal set; }
    }

    /// <summary>
    /// How long a toggle's effects last (it ends by itself then): the longest of its effects' time, an over-time effect's or a
    /// pulse's last tick (initialinterval + (count − 1) × interval), and the same of the spells it applies (ENFEEBLEAURA 5 s,
    /// UNDEADARROWS 8 s, ASSASSINSPIRIT 15 s, MOUNTSPELL_SPRINT_* 600 s).
    /// </summary>
    public static double ToggleSeconds(World world, SpellTemplate spell, int depth = 0)
    {
        var longest = 0.0;
        foreach (var node in spell.Effects)
        {
            longest = Math.Max(longest, SpellTemplate.Float(node, "time"));
            if (node.Name.LocalName is "attributechangeovertime" or "pulsingspell")
                longest = Math.Max(longest, SpellTemplate.Float(node, "initialinterval") + (SpellTemplate.Float(node, "count", 1) - 1) * SpellTemplate.Float(node, "interval", 1));
            if (node.Name.LocalName == "applyspell" && depth < 4 && world.Spells?.Template((string?)node.Attribute("spell")) is { } applied)
                longest = Math.Max(longest, ToggleSeconds(world, applied, depth + 1));
        }
        return longest;
    }

    /// <summary>
    /// The spells a toggle leaves on units, by id: itself and every spell its effects name (applyspell, pulsingspell, the aura's
    /// spell, applyeffectoneventactive), recursively: what <see cref="EndToggle"/> takes off again.
    /// </summary>
    private static HashSet<short> ToggleSpells(World world, SpellTemplate spell)
    {
        var ids = new HashSet<short> { spell.Id };
        var open = new Stack<(SpellTemplate Spell, int Depth)>([(spell, 0)]);
        while (open.TryPop(out var next))
            foreach (var e in next.Spell.Effects.SelectMany(n => n.DescendantsAndSelf()))
                if (next.Depth < 4 && world.Spells?.Template((string?)e.Attribute("spell")) is { } named && ids.Add(named.Id))
                    open.Push((named, next.Depth + 1));
        return ids;
    }

    /// <summary>
    /// A finished cast of a togglespell (<see cref="Finish"/>): the client marks the slot's toggle active itself (ct4.c 0x1809b0310:
    /// a48.u, no cooldown for a bi5) and shows it until a CastCancel with key 4 (<see cref="EndToggle"/>). A toggle still on in that
    /// slot ends first. It ends by itself when its effects run out (<see cref="ToggleSeconds"/> after the hit).
    /// </summary>
    private static ToggleState StartToggle(Player p, PendingCast cast, long hit)
    {
        var world = p.World;
        if (p.Combat.Toggles.GetValueOrDefault(cast.Slot) is { } old) EndToggle(p, old, ToggleEnd.Recast);
        var state = new ToggleState(cast.Spell, cast.Slot, cast.Counter, cast.End, hit + Seconds(ToggleSeconds(world, cast.Spell)));
        p.Combat.Toggles[cast.Slot] = state;
        var map = p.Map!;
        Log.Debug("combat", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' toggles {cast.Spell} on in slot {cast.Slot} for {(state.End - cast.End) / (double)TimeSpan.TicksPerSecond:0.##} s"));
        At(world, map, state.End, () =>
        {
            if (!state.Over && p.Map == map) EndToggle(p, state, ToggleEnd.Expired);
        });
        return state;
    }

    /// <summary>
    /// The end of <paramref name="p"/>'s toggle <paramref name="t"/> (TerminateToggleSpell, its effects ran out or were removed, the
    /// energy for its next tick is gone, a recast, the knockdown, a mount or dismount, leaving the map): the spells it left (itself
    /// and those it applied, its aura's on the mobs, a friend's buffs: removeonleave) come off, the lists go out (the caster's only with
    /// <paramref name="send"/>: the knockdown and the mounts send their own), the cooldown runs from now and the caster gets CastCancel
    /// {0 me, 2 counter, 3 cooldown factor, 4 true, 5 slot, 6 cooldown end, 7 now, 8 the span (ticks), 9 0}: the client's cwa.aft
    /// 0x1809d6180 → cwa.u 0x1809d66a0 switches the toggle off (a48.an) and starts the slot's cooldown from keys 5-9 (a48.u
    /// 0x1808f28d0, the layout of SpellCooldownUpdate). Leaving the map ends it quietly (no events; its buffs are not saved).
    /// While another cast with a cast time runs, the CastCancel waits for its end: the client's handler cancels the cast it is in
    /// (state 3) without looking at keys 2 or 5 (<see cref="SendHeldToggleCancels"/>). Returns whether the caster's list changed.
    /// </summary>
    public static bool EndToggle(Player p, ToggleState t, ToggleEnd why, bool send = true)
    {
        if (t.Over) return false;
        t.Over = true;
        var world = p.World;
        if (p.Combat.Toggles.GetValueOrDefault(t.Slot) == t) p.Combat.Toggles.Remove(t.Slot);
        var now = world.GameNow;
        var factor = CooldownFactorOf(p); // before its own buffs come off
        var span = Seconds(t.Spell.RecastDelay * factor);
        p.Combat.Cooldowns[t.Spell.Id] = now + span;
        var ids = ToggleSpells(world, t.Spell);
        bool Left(ActiveSpell s) => s.Caster == p.Id && s.Start >= t.Start && ids.Contains(s.Spell.Id);
        var quiet = why == ToggleEnd.Left;
        var removed = SpellEffects.Remove(world, p, Left);
        if (removed && send && !quiet)
        {
            SpellEffects.Send(world, p);
            SpellEffects.Refresh(world, p);
        }
        if (p.Map is { } map && !quiet)
            foreach (var unit in map.Objects.Values.OfType<Mob>().Cast<IUnit>().Concat(map.Characters.Where(q => q != p)).ToList())
                if (SpellEffects.Remove(world, unit, Left))
                {
                    SpellEffects.Send(world, unit);
                    SpellEffects.Refresh(world, unit);
                }
        Log.Debug("combat", $"'{p.Character.Name}' toggle {t.Spell} in slot {t.Slot} off: {why}");
        if (quiet) return removed;
        var cancel = new CastCancelEvent { ddn = p.Id, bup = t.Counter, buq = factor, bur = true, bus = t.Slot, but = now + span, buu = now, buv = span };
        if (p.Combat.Cast is not null) p.Combat.HeldToggleCancels.Add(cancel);
        else p.Session.Send(cancel);
        return removed;
    }

    /// <summary>Ends every toggle of <paramref name="p"/> (<see cref="EndToggle"/>); whether the caster's list changed.</summary>
    public static bool EndToggles(Player p, ToggleEnd why, bool send = true)
    {
        var removed = false;
        foreach (var t in p.Combat.Toggles.Values.ToList()) removed |= EndToggle(p, t, why, send);
        if (why == ToggleEnd.Left) p.Combat.HeldToggleCancels.Clear();
        return removed;
    }

    /// <summary>The toggles' CastCancel held back while a cast ran (<see cref="EndToggle"/>), once it has finished or been cancelled.</summary>
    private static void SendHeldToggleCancels(Player p)
    {
        foreach (var cancel in p.Combat.HeldToggleCancels) p.Session.Send(cancel);
        p.Combat.HeldToggleCancels.Clear();
    }

    /// <summary>
    /// After a removal on <paramref name="p"/> (<see cref="SpellEffects.Remove"/>: IMMORTAL_CONDITION's removeactivespell IMMORTAL when
    /// the energy runs low, a cleanse, a mob's dispel): a toggle whose own entry has gone after its hit ends.
    /// </summary>
    internal static void ToggleEntriesGone(Player p)
    {
        var now = p.World.GameNow;
        foreach (var t in p.Combat.Toggles.Values.ToList())
            if (now > t.Start + Seconds(t.Spell.HitDelay) && p.Effects.Find(t.Spell.Id, p.Id) is null && t.Spell.Effects.Any(e => SpellEffects.Listed.Contains(e.Name.LocalName)))
                EndToggle(p, t, ToggleEnd.Removed);
    }

    // ---- the effect tree ----

    /// <summary>
    /// What a spell's effects run with: the caster, the root spell's counter, the ability power, the target or point and the time of the
    /// effect. The target is a mob or, since stageJ pvp-spells (D1), another player (a Friend); null = the caster.
    /// </summary>
    public sealed record SpellContext(Player Caster, Map Map, SpellTemplate Root, byte Counter, double AbilityPower, IUnit? Target, Vector2 Point, long Ticks)
    {
        /// <summary>Recursion depth of applyspell/pulsingspell (a spell that applies itself stops at 8).</summary>
        public int Depth { get; init; }

        /// <summary>The crowd control power of the cast's source (ActiveSpellEffectsUpdate key 3; the CC durations, stageD SPEC §3.2).</summary>
        public double CcAbilityPower { get; init; } = AbilityPower;

        /// <summary>CastHit key 4: the cast counter for the cast spell itself, 0 for a spell it triggers (as the captured CastHits of FIREWALL_EFFECT).</summary>
        public byte CounterOf(SpellTemplate spell) => spell == Root ? Counter : (byte)0;

        /// <summary>Where an area effect is centred: the target, else the point (a self spell: the caster).</summary>
        public Vector2 Center => Target is { } t ? SpellMovement.PositionOf(t, Ticks) : Point;

        /// <summary>The unit the context's effects act on: its target, else the caster.</summary>
        public IUnit Unit => Target ?? Caster;

        /// <summary>Where a knockback or fear pushes away from: an area's centre; null = the caster.</summary>
        public Vector2? Source { get; init; }

        /// <summary>
        /// Applied by an area (stageD SPEC §4): the hit goes out as CastHits {2 [the effect, the spells it applies…]} after the list,
        /// the damage without its own CastHit, and ForcedMovement without key 9.
        /// </summary>
        public bool FromArea { get; init; }

        /// <summary>The channel the effects run in (its ticks): linktochanneling areas end with it; null = none.</summary>
        public ChannelState? Channel { get; init; }

        /// <summary>The channel tick the effects run in, from 1 (IfChannelTick); 0 = not a tick.</summary>
        public int ChannelTick { get; init; }

        /// <summary>The consumable whose spell this is (a potion, food or an item used from the inventory; a tome's UpdateFame key 7); null = none.</summary>
        public ItemTemplate? Item { get; init; }

        /// <summary>The roll of the spell's generateloot, made (and checked to fit) before the item was used (<see cref="Consumables"/>); null = roll there.</summary>
        public IReadOnlyList<(ItemTemplate Item, int Count)>? Loot { get; init; }

        /// <summary>The toggle the effects belong to (its hit, pulses and ticks): they stop when it ends; null = none.</summary>
        public ToggleState? Toggle { get; init; }

        /// <summary>Whether the effects belong to a toggle that has ended.</summary>
        public bool ToggledOff => Toggle is { Over: true };
    }

    /// <summary>
    /// The decisions of one <see cref="Execute"/>, taken in its first pass and kept for the second, so both see the same: the
    /// conditions of each node (IfChance, IfConsumeCharges run once), which spells a spellimmunity blocks on which unit (as it
    /// was before this hit applied anything), and the spells to list, in pre-order (a spell before the spells it applies).
    /// </summary>
    private sealed class Plan
    {
        public readonly Dictionary<(XElement Node, long Unit), bool> Holds = [];
        public readonly Dictionary<(short Spell, long Unit), (IUnit Unit, long Caster, byte Counter)?> Blocked = [];
        public readonly List<Application> Applications = [];

        /// <summary>The entries this hit listed but that are not in force yet, by spell (<see cref="ActiveSpell.Pending"/>).</summary>
        public readonly Dictionary<short, List<(IUnit Unit, ActiveSpell Entry)>> Pending = [];

        /// <summary>An area's hit: the spells that reach each unit, in pre-order (CastHits key 2).</summary>
        public readonly List<(IUnit Unit, SpellTemplate Spell)> Reached = [];

        /// <summary>
        /// The removals that come before every listed sub-effect of their spell (a food's removeactiveeffects foodbuff before its
        /// buff, stageD consumables SPEC §4.2): run before the new entries are listed, so they never take the spell's own entry.
        /// </summary>
        public readonly List<(SpellContext Context, XElement Node, IUnit Unit)> Removals = [];
    }

    /// <summary>A spell to list on a unit with its sub-effects by client index (null = inactive).</summary>
    private sealed record Application(SpellContext Context, SpellTemplate Spell, IUnit Unit, SubEffect?[] Subs);

    /// <summary>
    /// Runs <paramref name="spell"/> with <paramref name="c"/> (see the class summary) in the hit batch order of stageD SPEC
    /// §2: first every timed sub-effect of the spell and of the spells it applies goes into its unit's list
    /// (<see cref="SpellEffects.Apply"/>; the knockbacks, pulls and fears go out as ForcedMovement, then one ActiveSpellEffectsUpdate
    /// per changed unit, its stats, the damage shields and auras; an area's hit then its CastHits), then CastHit {3 2} for
    /// each spell a spellimmunity blocked on a unit (nothing else of it runs there), then the instant effects in document
    /// order (damage and heals with their CastHit and HealthUpdate, the over-time ticks, the pulses, the removals).
    /// </summary>
    public static void Execute(SpellContext c, SpellTemplate spell)
    {
        if (c.Depth > 8) return;
        var world = c.Caster.World;
        var plan = new Plan();
        Collect(c, spell, plan);
        if (world.Plugins.Hooks.BuffCheck.Any) plan.Applications.RemoveAll(a => world.Plugins.BuffBlocked(a.Unit, a.Spell, a.Context.Caster)); // Buffs.OnCheckApply
        var changed = Removed(plan, refresh: false);
        // An effect on a Hostile player or bot is an attack (stageJ pvp-spells SPEC §2.5, D6): its bookkeeping once per hit and unit, before
        // the effect (a stun, a slow, a knockback; a damage hit runs it again in Pvp.Damage, which changes nothing then).
        foreach (var q in plan.Applications.Select(a => a.Unit).OfType<Player>().Where(q => q != c.Caster).Distinct().ToList())
            if (SpellTargets.Of(c.Caster, q, c.Ticks).Hostile) Pvp.Engage(c.Caster, q, c.Ticks);
        var entries = new List<(Application A, ActiveSpell Entry)>();
        foreach (var a in plan.Applications)
        {
            var entry = SpellEffects.Apply(world, a.Unit, a.Spell, a.Context.Caster.Id, a.Context.AbilityPower, a.Context.CcAbilityPower, a.Context.Ticks, a.Subs, pending: true);
            entries.Add((a, entry));
            if (entry.Pending)
            {
                if (!plan.Pending.TryGetValue(a.Spell.Id, out var list)) plan.Pending[a.Spell.Id] = list = [];
                list.Add((a.Unit, entry));
            }
            if (!changed.Contains(a.Unit)) changed.Add(a.Unit);
        }
        // The pushes and fears go out before the lists (FIREWALL: NewSpellEffectArea, ForcedMovement, ActiveSpellEffectsUpdate, CastHits).
        foreach (var (a, _) in entries)
            foreach (var sub in a.Subs)
                if (sub is { Tag: "knockback" or "pull" or "forcedmovement" })
                    SpellMovement.Push(world, a.Context.Caster, a.Spell, sub, a.Unit, a.Context.Source ?? SpellMovement.PositionOf(a.Context.Caster, a.Context.Ticks),
                        a.Context.Ticks, a.Context.FromArea);
        // The caster's own dash or blink goes out before its list (stageM movement NOTES §1 E: the official own dashes, 8 of 8, send
        // CastSpell, ForcedMovement and the ActiveSpellEffectsUpdate after them): its list waits for Run.
        var ownMove = !c.FromArea && c.Depth == 0 && spell.Effects.Any(n => n.Name.LocalName is "dash" or "teleport" && Holds(plan, c, n)) && changed.Contains(c.Caster);
        foreach (var u in changed)
        {
            if (!(ownMove && u == c.Caster)) SpellEffects.Send(world, u);
            SpellEffects.Refresh(world, u); // a re-applied entry (a stack more) is in force at once
        }
        var shielded = Started(entries);
        if (c.FromArea)
            foreach (var group in plan.Reached.GroupBy(r => r.Unit))
            {
                var spells = group.Select(r => r.Spell.Id).ToArray();
                group.Key.SendAround(spells.Length == 1
                    ? Combat.CastHit(group.Key.Id, c.Caster.Id, spells[0], 0)
                    : new CastHitsEvent { ddn = group.Key.Id, bu7 = [.. spells.Select(_ => c.Caster.Id)], bu8 = spells, bu9 = [.. spells.Select(_ => (byte)1)], bva = new byte[spells.Length] });
            }
        foreach (var ((id, _), blocked) in plan.Blocked)
            if (blocked is var (unit, caster, counter))
                unit.SendAround(new CastHitEvent { ddn = unit.Id, bu3 = caster, bu4 = id, bu5 = 2, Extra = new() { [4] = counter } });
        Run(c, spell, plan);
        if (ownMove) SpellEffects.Send(world, c.Caster);
        foreach (var id in plan.Pending.Keys.ToList()) InForce(world, plan, id);
        foreach (var u in shielded) SpellEffects.ShieldChanged(u, world.GameNow); // in force now: DamageShieldUpdate {1 left}
    }

    /// <summary>
    /// <see cref="Execute"/> in the order of a consumable's hit (stageD consumables SPEC §2.1 step 5, §3.2): the leading removals
    /// (their stats worked out again: RegenerationHealthChanged with the base regeneration on a re-eaten food), the new entries
    /// in force at once (the stats again: the food's doubled regeneration), the instant effects (the first heal or energy
    /// tick, a tome's fame), and only then the caster's list (ActiveSpellEffectsUpdate) when the spell listed or removed
    /// anything. The caller sends the CastHit. Returns whether a list went out.
    /// </summary>
    public static bool ExecuteFirst(SpellContext c, SpellTemplate spell)
    {
        var world = c.Caster.World;
        var plan = new Plan();
        Collect(c, spell, plan);
        if (world.Plugins.Hooks.BuffCheck.Any) plan.Applications.RemoveAll(a => world.Plugins.BuffBlocked(a.Unit, a.Spell, a.Context.Caster)); // Buffs.OnCheckApply
        var changed = Removed(plan, refresh: true);
        var entries = new List<(Application A, ActiveSpell Entry)>();
        foreach (var a in plan.Applications)
        {
            entries.Add((a, SpellEffects.Apply(world, a.Unit, a.Spell, a.Context.Caster.Id, a.Context.AbilityPower, a.Context.CcAbilityPower, a.Context.Ticks, a.Subs)));
            if (!changed.Contains(a.Unit)) changed.Add(a.Unit);
        }
        foreach (var u in changed) SpellEffects.Refresh(world, u);
        var shielded = Started(entries); // a potion's aura or damage shield, as in Execute
        Run(c, spell, plan);
        foreach (var u in changed) SpellEffects.Send(world, u);
        foreach (var u in shielded) SpellEffects.ShieldChanged(u, world.GameNow);
        return changed.Count > 0;
    }

    /// <summary>Runs the plan's leading removals (<see cref="Plan.Removals"/>); returns the units whose list changed, their stats worked out again when <paramref name="refresh"/>.</summary>
    private static List<IUnit> Removed(Plan plan, bool refresh)
    {
        var changed = new List<IUnit>();
        foreach (var (c, node, unit) in plan.Removals)
            if (RemoveEffects(c, node, unit, send: false) && !changed.Contains(unit))
            {
                changed.Add(unit);
                if (refresh) SpellEffects.Refresh(c.Caster.World, unit);
            }
        return changed;
    }

    /// <summary>
    /// What starts with a listed entry (stageD SPEC §3.4, §3.5): an aura's ticks (<see cref="SpellAreas.Aura"/>). Returns the units
    /// that got a damage shield: their DamageShieldUpdate goes out once the entries are in force (end of the cast batch).
    /// </summary>
    private static HashSet<IUnit> Started(List<(Application A, ActiveSpell Entry)> entries)
    {
        var shielded = new HashSet<IUnit>();
        foreach (var (a, entry) in entries)
            for (var i = 0; i < a.Subs.Length; i++)
                switch (a.Subs[i]?.Tag)
                {
                    case "damageshield":
                        shielded.Add(a.Unit);
                        break;
                    case "aura":
                        SpellAreas.Aura(a.Context, entry, entry.Subs[i], a.Unit);
                        break;
                }
        return shielded;
    }

    /// <summary>The entries of spell <paramref name="id"/> this hit listed come in force: the stats are worked out again, a stun interrupts.</summary>
    private static void InForce(World world, Plan plan, short id)
    {
        if (!plan.Pending.Remove(id, out var entries)) return;
        foreach (var (unit, entry) in entries)
        {
            entry.Pending = false;
            SpellEffects.Refresh(world, unit);
            SpellEffects.Interrupt(unit, world.GameNow);
        }
    }

    /// <summary>The first pass: the conditions, the blocked spells and the timed sub-effects of <paramref name="spell"/> and of what it applies.</summary>
    private static void Collect(SpellContext c, SpellTemplate spell, Plan plan)
    {
        if (c.Depth > 8) return;
        var world = c.Caster.World;
        if (c.FromArea && !Blocked(plan, c, spell, c.Unit)) plan.Reached.Add((c.Unit, spell));
        var slot = plan.Applications.Count;
        var own = new Dictionary<long, Application>();
        for (var i = 0; i < spell.Effects.Count; i++)
        {
            var node = spell.Effects[i];
            if (!Holds(plan, c, node)) continue;
            var tag = node.Name.LocalName;
            foreach (var target in Targets(c, node))
            {
                var unit = UnitOf(c, target);
                if (Blocked(plan, c, spell, unit)) continue;
                if (tag == "applyspell")
                {
                    // The applied spell's own class narrows by kind (SpellTargets.Kind: ARCANEORB2_SILENCE_PLAYERS never on a mob).
                    if (Sub(c, node) is { } applied && SpellTargets.Kind(applied, unit) && !Blocked(plan, c, applied, unit)) Collect(Child(c, target), applied, plan);
                    continue;
                }
                if (Leading(spell, i))
                {
                    plan.Removals.Add((c, node, unit));
                    continue;
                }
                if (SpellEffects.Make(world, c.Caster, c.AbilityPower, c.CcAbilityPower, node, i, unit, world.GameNow) is not { } sub) continue;
                if (!own.TryGetValue(unit.Id, out var a))
                {
                    own[unit.Id] = a = new Application(c, spell, unit, new SubEffect?[spell.Effects.Count]);
                    plan.Applications.Insert(slot++, a);
                }
                a.Subs[i] = sub;
            }
        }
    }

    /// <summary>The second pass: the instant effects of <paramref name="spell"/> in document order, with the first pass's decisions.</summary>
    private static void Run(SpellContext c, SpellTemplate spell, Plan plan)
    {
        if (c.Depth > 8) return;
        var world = c.Caster.World;
        var hits = new HashSet<long>();
        var dungeonPlaced = false; // stageG: of a dungeon map's activaterandomdungeon blocks only the first that holds places an exit
        for (var i = 0; i < spell.Effects.Count; i++)
        {
            var node = spell.Effects[i];
            if (!Holds(plan, c, node)) continue;
            var tag = node.Name.LocalName;
            switch (tag)
            {
                case "directattributechange":
                    if (!Attribute(node, spell)) break;
                    foreach (var target in Targets(c, node))
                        if (!Blocked(plan, c, spell, UnitOf(c, target)))
                            Change(c, spell, node, target, Radius(node) > 0 ? HitOrigin.Area : HitOrigin.Direct, !c.FromArea && hits.Add(target?.Id ?? c.Caster.Id));
                    break;
                case "attributechangeovertime":
                    if (!Attribute(node, spell)) break;
                    foreach (var target in Targets(c, node))
                        if (!Blocked(plan, c, spell, UnitOf(c, target))) OverTime(c, spell, node, i, target);
                    break;
                case "applyspell":
                    if (Sub(c, node) is not { } applied) break;
                    foreach (var target in Targets(c, node))
                        if (UnitOf(c, target) is var unit && SpellTargets.Kind(applied, unit) && !Blocked(plan, c, spell, unit) && !Blocked(plan, c, applied, unit))
                            Run(Child(c, target), applied, plan);
                    break;
                case "pulsingspell":
                    if (Sub(c, node) is not { } pulsed) break;
                    var pulseSelf = (string?)node.Attribute("target") == "self";
                    if (!Blocked(plan, c, spell, pulseSelf ? c.Caster : c.Unit) && (pulseSelf || Pulsed(c, node, pulsed, c.Ticks))) Pulses(c, node, pulsed);
                    break;
                case "removeactivespell" or "removeactiveeffects":
                    if (Leading(spell, i)) break; // run before the listing (Plan.Removals)
                    foreach (var target in Targets(c, node))
                        if (UnitOf(c, target) is var unit && !Blocked(plan, c, spell, unit)) RemoveEffects(c, node, unit);
                    break;
                case "adddestinyboardpvefame" or "awardcraftingfocus" or "awardsilver" or "generateloot" or "awardlearningpoints"
                    or "adddestinyboardgatheringfame" or "adddestinyboardfishingfame" or "repairequipment":
                    Consumables.Award(c, node);
                    break;
                case "activaterandomdungeon": // stageG dungeons SPEC §10: a dungeon map
                    if (!dungeonPlaced) dungeonPlaced = world.Dungeons.FromMap(c.Caster, node);
                    break;
                case "multispell":
                    Multi(c, spell, node, i);
                    break;
                case "teleport":
                    Blink(c, spell, node, i);
                    break;
                case "resetcooldowns":
                    ResetCooldowns(c, node);
                    break;
                case "resurrect": // stageN revive: ResurrectionOffer to the knocked-down friend
                    foreach (var target in Targets(c, node))
                        if (target is Player q && !Blocked(plan, c, spell, q))
                            Revive.Offer(c.Caster, q, SpellTemplate.Float(node, "restoration") is var r and > 0 ? r : 0.3,
                                SpellTemplate.Float(node, "time") is var t and > 0 ? t : 10, (string?)node.Attribute("removespellonresurrectanswer"), c.Ticks);
                    break;
                case "changeaggro" or "maketargettopthreat" or "matchhighestthreatlevel" or "interruptcast":
                    foreach (var target in tag == "interruptcast" ? Targets(c, node) : ThreatTargets(c, node))
                        if (target is Mob mob && !Blocked(plan, c, spell, mob)) // threat acts on mobs only
                        {
                            if (tag == "interruptcast") InterruptMob(mob);
                            else Threat(c, node, mob);
                        }
                        else if (tag == "interruptcast" && target is Player q && SpellTargets.Of(c.Caster, q, c.Ticks).Hostile && !Blocked(plan, c, spell, q))
                        {
                            Pvp.Engage(c.Caster, q, c.Ticks); // a Hostile player or bot (stageJ pvp-spells phase 2): an attack without damage
                            InterruptPlayer(q, c.Ticks, "interruptcast");
                        }
                    break;
                case "spawnmob":
                    Summons.Spawn(world, c.Map, c.Caster, node, c.Target, c.Point, c.Ticks);
                    break;
                case "removespelleffectareasinarea":
                    SpellAreas.RemoveInArea(c.Caster, c.Map, c.Center, node);
                    break;
                case var _ when PassiveTags.Contains(tag):
                    break; // the worn items' passives (Systems.Passives)
                case "spelleffectarea":
                    SpellAreas.Create(c, spell, node, i);
                    break;
                case "endtransformation" or "terminatetogglespells" or "setcrowdcontroldiminishingreturns":
                    foreach (var target in Targets(c, node))
                        if (UnitOf(c, target) is var unit && !Blocked(plan, c, spell, unit)) OnUnit(c, node, unit);
                    break;
                case "removespelleffectareasoftarget":
                    SpellAreas.RemoveOf(c.Caster, (string?)node.Attribute("spell"), (string?)node.Attribute("areatags"));
                    break;
                case "removespelleffectareasaffectingtarget":
                    foreach (var target in Targets(c, node))
                        SpellAreas.RemoveAffecting(c.Caster, UnitOf(c, target), node);
                    break;
                case "chainspell": // the first target as the other per-unit tags: a spellimmunity for this spell skips it
                    Chain(c, node, unit => !Blocked(plan, c, spell, unit));
                    break;
                case "applyrandomspell":
                    foreach (var target in Targets(c, node))
                        if (UnitOf(c, target) is var unit && !Blocked(plan, c, spell, unit) && PickRandom(c, node) is { } picked && SpellTargets.Kind(picked, unit))
                            Execute(Child(c, target), picked);
                    break;
                case "cancelmultispell":
                    CancelMulti(c, node);
                    break;
                case "storetargets": // stageN spells2: what the follow-up spell is cast at
                    Store(c, spell, node);
                    break;
                case "removestoredtargets":
                    RemoveStored(c, node);
                    break;
                case "setcooldowns":
                    ResetCooldowns(c, node, set: true);
                    break;
                case var _ when NoOps.Contains(tag):
                    Combat.LogOnce("noop:" + tag, $"spell effect <{tag}> of {spell.Name} is a no-op (no account unlocks yet)");
                    break;
                case "dash":
                    SpellMovement.Dash(c, spell, node, i);
                    break;
                case "nop":
                    break; // markers
                default:
                    if (SpellEffects.Listed.Contains(tag)) break; // listed in the first pass; what it does is read from the list
                    Combat.LogOnce("tag:" + tag, () =>
                        $"spell effect <{tag}> is not supported yet ({world.Spells?.EffectTags().GetValueOrDefault(tag) ?? 0} uses in spells.xml): {spell.Name} skips it");
                    break;
            }
        }
        InForce(world, plan, spell.Id); // its effects have run: what it listed acts from now on
    }

    /// <summary>The context of a spell applied to <paramref name="target"/> (null = the caster).</summary>
    private static SpellContext Child(SpellContext c, IUnit? target) =>
        c with { Target = target, Point = target is null ? c.Caster.PositionAt(DateTime.UtcNow) : SpellMovement.PositionOf(target, c.Ticks), Depth = c.Depth + 1 };

    private static IUnit UnitOf(SpellContext c, IUnit? target) => target ?? c.Caster;

    /// <summary>
    /// Whether a spellimmunity on <paramref name="unit"/> blocks <paramref name="spell"/> in this Execute (decided once, before
    /// it applied anything). A mob running home is immune to all (MOB_RETURNHOME's spellname "*", <see cref="Mob.Immune"/>,
    /// SPEC §3.3), also before its buff is listed.
    /// </summary>
    private static bool Blocked(Plan plan, SpellContext c, SpellTemplate spell, IUnit unit)
    {
        var key = (spell.Id, unit.Id);
        if (plan.Blocked.TryGetValue(key, out var b)) return b is not null;
        var blocked = unit is Mob { Immune: true } || SpellEffects.Immune(unit, spell, c.Caster.World.GameNow)
            || c.Caster.World.Plugins.EffectBlocked(c, spell, unit); // the plugins' Spells.OnEffect (PreventDefault)
        plan.Blocked[key] = blocked ? (unit, c.Caster.Id, c.CounterOf(spell)) : null;
        if (blocked) Log.Debug("combat", $"{spell} on #{unit.Id} is blocked by a spell immunity");
        return blocked;
    }

    /// <summary>Whether the effect changes health or energy (the attributes so far; others are logged once).</summary>
    private static bool Attribute(XElement node, SpellTemplate spell)
    {
        var attribute = (string?)node.Attribute("attribute") ?? "";
        if (attribute is "health" or "energy") return true;
        Combat.LogOnce("attr:" + attribute, $"spell effect on attribute '{attribute}' is not supported yet: {spell.Name} skips it");
        return false;
    }

    private static float Radius(XElement node) => SpellTemplate.Float(node, "effectarearadius");

    /// <summary>
    /// Who an effect node acts on (stageJ pvp-spells SPEC §2.5; null in the list = the caster): target "self" the caster; with
    /// effectarearadius every unit within it (+ its collision radius) of the centre that matches the node's class
    /// (<see cref="SpellTargets.Effect"/>: enemy the attackable mobs, friendall the caster and its Friends, enemymobs / enemyplayers by
    /// kind; <see cref="Around"/>); else the context's unit (its target, or the caster) when it matches. The class is read for mobs too
    /// (phase 0: HELL_ARROW_DAMAGE's enemyplayers node no longer hits a mob). An empty or unknown class, and a ground cast's own node
    /// (its dummy, the vanity dice), keep the reading of before: enemy with a target in the context, else the caster.
    /// </summary>
    private static List<IUnit?> Targets(SpellContext c, XElement node)
    {
        var name = (string?)node.Attribute("target") ?? "";
        if (name == "self") return [null];
        var cls = SpellTargets.Parse(name);
        var unread = cls is SpellTargets.Cls.None or SpellTargets.Cls.Ground;
        if (unread) cls = SpellTargets.Cls.Enemy;
        var radius = Radius(node);
        if (radius > 0) return Around(c, cls, c.Center, radius);
        if (unread && c.Target is null) return [null];
        return SpellTargets.Effect(cls, SpellTargets.Of(c.Caster, c.Unit, c.Ticks)) ? [c.Target] : [];
    }

    /// <summary>
    /// Whether <paramref name="spell"/>, run by <paramref name="caster"/>'s area on <paramref name="unit"/>, would act on that unit at all
    /// (stageJ pvp-spells SPEC §2.6): one of its nodes matches it as <see cref="Targets"/> reads the class (an empty class: enemy, or the
    /// caster itself), an applyspell node also through the applied spell (its kind, then its nodes). A target="self" node counts for the
    /// caster and for a Hostile unit: an enemy that enters sets off the owner's own part (QSTAFF_COMBO_3RDHIT's buffs, CRYSTAL_SCYTHE's
    /// cooldown, WEREWOLF_TEAR_APART's area). Conditions are not weighed. An area of class all whose effect acts on enemies only
    /// (CALTROPS_EFFECT, GROUNDMINE_EFFECT2, ARMOR_WEB_EFFECT) thus neither counts a Friend who steps in as a hit (maxeffectareatargets)
    /// nor gives the owner its self part for it nor shows the Friend a CastHits.
    /// </summary>
    internal static bool Reaches(Player caster, SpellTemplate spell, IUnit unit, long now, int depth = 0)
    {
        if (depth > 8) return false;
        var facts = SpellTargets.Of(caster, unit, now);
        foreach (var node in spell.Effects)
        {
            var name = (string?)node.Attribute("target") ?? "";
            if (name == "self")
            {
                if (facts.Self || facts.Hostile) return true;
                continue;
            }
            var cls = SpellTargets.Parse(name);
            var unread = cls is SpellTargets.Cls.None or SpellTargets.Cls.Ground;
            if (!(unread ? facts.Self || SpellTargets.Effect(SpellTargets.Cls.Enemy, facts) : SpellTargets.Effect(cls, facts))) continue;
            if (node.Name.LocalName != "applyspell") return true;
            if (caster.World.Spells?.Template((string?)node.Attribute("spell")) is { } applied && SpellTargets.Kind(applied, unit)
                && Reaches(caster, applied, unit, now, depth + 1)) return true;
        }
        return false;
    }

    /// <summary>The units within <paramref name="radius"/> (+ their collision radius) of <paramref name="center"/> that match <paramref name="cls"/> for the caster: the mobs, then the players and bots (the caster as null).</summary>
    private static List<IUnit?> Around(SpellContext c, SpellTargets.Cls cls, Vector2 center, float radius)
    {
        var found = new List<IUnit?>();
        foreach (var m in c.Map.Objects.Values.OfType<Mob>())
            if (Vector2.Distance(m.Position, center) <= radius + m.Template.CollisionRadius && SpellTargets.Effect(cls, SpellTargets.Of(c.Caster, m, c.Ticks))) found.Add(m);
        var reach = radius + c.Caster.World.CollisionRadius;
        foreach (var q in c.Map.Characters)
            if (Vector2.Distance(SpellMovement.PositionOf(q, c.Ticks), center) <= reach && SpellTargets.Effect(cls, SpellTargets.Of(c.Caster, q, c.Ticks)))
                found.Add(q == c.Caster ? null : q);
        return found;
    }

    /// <summary>
    /// One directattributechange on <paramref name="target"/> (null = the caster), × <paramref name="stacks"/> (an over-time
    /// tick of an entry with stacks): damage (change &lt; 0) on a mob = change × AP / 100 × the caster's damage bonuses × the resistance
    /// of its effecttype, with a CastHit before the HealthUpdate (once per target and spell), and with interruptscasting="true" its cast
    /// under way cancelled (HOWL_EFFECT; stageJ pvp-spells phase 0); damage on a Hostile player or bot (stageJ pvp-spells SPEC §2.5, phase 2)
    /// the same with bonusdamagevsplayers and the player's resistance through <see cref="Pvp.Damage"/> (its CastHit, its PvP checks, a
    /// reflectdamage="never" node not reflected), interruptscasting there ending its cast, channel, flag cast or execute
    /// (<see cref="InterruptPlayer"/>); a heal (change &gt; 0) on the caster or a Friend (stageJ pvp-spells
    /// SPEC §2.5, phase 1) = change × (AP / 100)^0.6634 × (1 + the caster's healbonus, buffs and worn items) × the receiver's healmodifier
    /// sum (neither with usehealingmodifiers="false"), HealthUpdate {6 the caster} to the receiver and its observers. changetype (stageD
    /// SPEC §0.2, §3.4): relativemax × the maximum, relativecurrent × the current value, relativemissing × the missing part, absolute (and
    /// ignoreabilitypowerscaling) the raw value. attribute="energy": the receiver's energy, EnergyUpdate {4: 2, 5 the caster}; a drain
    /// (change &lt; 0) on the caster or a Hostile player or bot (an attack: <see cref="Pvp.Engage"/>; phase 3, <see cref="DrainEnergy(Player, double, SpellTemplate, long, long, HitOrigin)"/>).
    /// </summary>
    private static void Change(SpellContext c, SpellTemplate spell, XElement node, IUnit? target, HitOrigin origin, bool firstHit, int stacks = 1)
    {
        var change = SpellConditions.Value(node, "change", Facts(c) with { EffectTarget = target ?? c.Caster }) * stacks;
        if (change == 0) return;
        var energy = (string?)node.Attribute("attribute") == "energy";
        var changeType = (string?)node.Attribute("changetype") ?? "";
        var raw = SpellTemplate.Bool(node, "ignoreabilitypowerscaling") || changeType == "absolute";
        double Relative(double scaled, float current, float max) => changeType switch
        {
            "relativemax" => change * max,
            "relativecurrent" => change * current,
            "relativemissing" => change * Math.Max(0, max - current),
            _ => raw ? change : scaled,
        };
        if (change > 0)
        {
            // Heals and energy on the caster or a Friend (the node's class chose it); none on a mob (no player summons as friends yet, SPEC Q5).
            if ((target ?? c.Caster) is not Player p) return;
            if (energy)
            {
                GainEnergy(p, Relative(change * c.AbilityPower / 100, p.Energy.At(c.Ticks), p.Energy.Max), spell, c.Ticks, origin, c.Caster.Id);
                return;
            }
            if (c.Caster != p) PvpDetails.Healed(c.Caster, p, c.Ticks); // stageP: healing sickness by distinct healers, before this heal
            var amount = Relative(Combat.Heal(change, c.AbilityPower), p.Health.At(c.Ticks), p.Health.Max);
            if (SpellTemplate.Bool(node, "usehealingmodifiers", true)) amount *= Combat.HealBonusFactor(c.Caster, c.Ticks) * SpellEffects.HealFactor(p, c.Ticks);
            var plugins = c.Caster.World.Plugins; // Formulas.OnCalcHeal, Spells.OnDamageEarly / OnDamageLate
            if (plugins.DamageEarly(c.Caster, spell, p, amount, heal: true, energy: false, origin == HitOrigin.OverTime, origin == HitOrigin.Area || c.FromArea, magic: true) is not { } healed) return;
            var healLate = plugins.WantsDamageLate(spell);
            var healBefore = healLate ? Plugins.PluginHost.HealthOf(p, c.Caster.World.GameNow) : 0;
            Combat.HealPlayer(c.Caster, p, healed, origin, spell.Id, c.Ticks);
            if (healLate) plugins.DamageLate(c.Caster, spell, p, healBefore, heal: true, energy: false, origin == HitOrigin.OverTime, origin == HitOrigin.Area || c.FromArea, magic: true);
            return;
        }
        if (energy)
        {
            // Energy drain (stageJ pvp-spells SPEC §2.5, phase 3): the caster's own (HYPER_FOCUS) or a Hostile player's or bot's (the node's
            // class chose it; the PvP rules again at the hit, an attack without damage: Pvp.Engage). Every negative energy node a player
            // can cast today is target="self"; the other-player path waits for a spell that drains others. A mob's energy: not modelled.
            if ((target ?? c.Caster) is not Player drained) return;
            if (drained != c.Caster)
            {
                if (Pvp.Refusal(c.Caster, drained, c.Ticks) is not null) return;
                Pvp.Engage(c.Caster, drained, c.Ticks, origin);
            }
            var amount = -Relative(change * c.AbilityPower / 100, drained.Energy.At(c.Ticks), drained.Energy.Max);
            if (drained == c.Caster) DrainEnergy(c, spell, amount, origin);
            else DrainEnergy(drained, amount, spell, c.Caster.Id, c.Ticks, origin);
            return;
        }
        var type = (string?)node.Attribute("effecttype") == "magic" ? EffectType.Magic : EffectType.Physical;
        if (target is Player q && q != c.Caster)
        {
            // A Hostile player or bot (the node's class chose it; stageJ pvp-spells SPEC §2.5, phase 2): through Pvp.Damage, which checks the
            // PvP rules again at the hit, × bonusdamagevsplayers, its armour or magic resistance and bonusdefensevsplayers.
            var dealt = Relative(Combat.SpellChange(change, c.AbilityPower) * SpellEffects.DealtFactor(c.Caster, spell: true, type, vsPlayer: true, c.Ticks),
                q.Health.At(c.Ticks), q.Health.Max);
            var cast = firstHit ? Combat.CastHit(q.Id, c.Caster.Id, spell.Id, c.CounterOf(spell)) : null;
            var lands = Pvp.Refusal(c.Caster, q, c.Ticks) is null;
            var pvpPlugins = c.Caster.World.Plugins; // Spells.OnDamageEarly / OnDamageLate
            var area = origin == HitOrigin.Area || c.FromArea;
            if (pvpPlugins.DamageEarly(c.Caster, spell, q, -dealt * Combat.ResistanceOf(q, type), heal: false, energy: false, origin == HitOrigin.OverTime, area, type == EffectType.Magic) is not { } pvpDamage) return;
            var pvpLate = pvpPlugins.WantsDamageLate(spell);
            var pvpBefore = pvpLate ? Plugins.PluginHost.HealthOf(q, c.Caster.World.GameNow) : 0;
            Pvp.Damage(c.Caster, q, pvpDamage, type, origin, spell.Id, c.Ticks, cast,
                reflectable: (string?)node.Attribute("reflectdamage") != "never");
            if (pvpLate) pvpPlugins.DamageLate(c.Caster, spell, q, pvpBefore, heal: false, energy: false, origin == HitOrigin.OverTime, area, type == EffectType.Magic);
            if (lands && SpellTemplate.Bool(node, "interruptscasting")) InterruptPlayer(q, c.Ticks, "interruptscasting"); // after the hit, as on a mob
            return;
        }
        // Damage on the caster itself: not modelled.
        if (target is not Mob mob) return;
        var value = Relative(Combat.SpellChange(change, c.AbilityPower) * SpellEffects.DealtFactor(c.Caster, spell: true, type, vsPlayer: false, c.Ticks),
            mob.Health.At(c.Ticks), mob.Health.Max);
        var hit = firstHit ? Combat.CastHit(mob.Id, c.Caster.Id, spell.Id, c.CounterOf(spell)) : null;
        var mobPlugins = c.Caster.World.Plugins; // Spells.OnDamageEarly / OnDamageLate
        var inArea = origin == HitOrigin.Area || c.FromArea;
        if (mobPlugins.DamageEarly(c.Caster, spell, mob, -value * Combat.ResistanceOf(mob, type), heal: false, energy: false, origin == HitOrigin.OverTime, inArea, type == EffectType.Magic) is not { } mobDamage)
        {
            if (hit is not null) mob.Map?.SendToObservers(mob, hit); // the CastHit still goes out: the client shows the hit, no damage
            return;
        }
        var mobLate = mobPlugins.WantsDamageLate(spell);
        var mobBefore = mobLate ? Plugins.PluginHost.HealthOf(mob, c.Caster.World.GameNow) : 0;
        Combat.Damage(c.Caster, mob, mobDamage, type, origin, spell.Id, c.Ticks, hit);
        if (mobLate) mobPlugins.DamageLate(c.Caster, spell, mob, mobBefore, heal: false, energy: false, origin == HitOrigin.OverTime, inArea, type == EffectType.Magic);
        if (SpellTemplate.Bool(node, "interruptscasting") && mob.Attackable) InterruptMob(mob); // as interruptcast, after the hit
    }

    /// <summary>
    /// <paramref name="amount"/> energy for <paramref name="p"/> by <paramref name="spell"/> (ENERGY_BARRIER_MANA, the energy
    /// potions): up to the maximum; EnergyUpdate (8) {0 me, 1 ticks, 2 +gain, 3 energy, 4 origin, 5 me (<paramref name="source"/>: a
    /// Friend's spell names its caster, stageJ pvp-spells SPEC §2.5 [inference]), 6 spell} to it and its observers (captured +10 with
    /// key 4 = 2). StageD consumables SPEC §4.1: key 4 is 2 direct and 5 over time; key 2 is the
    /// unclamped gain (POTION_ENERGY_P3 +127 at full energy 342/342), rounded when direct (0.371 × 342 = 126.88 → 127), not over
    /// time (0.053 × 342 → 18.125999).
    /// </summary>
    internal static void GainEnergy(Player p, double amount, SpellTemplate spell, long ticks, HitOrigin origin, long source = 0)
    {
        if (p.Combat.Down is not null) return;
        var overTime = origin == HitOrigin.OverTime;
        var gain = Math.Max(0, overTime ? (float)amount : Combat.Rounded(amount));
        var energyBefore = p.Energy.At(ticks);
        var after = Math.Min(p.Energy.Max, energyBefore + gain);
        p.Energy.Set(after, Math.Max(ticks, p.Energy.Stamp));
        p.World.Plugins.EnergyChanged(p, energyBefore, after, source != 0 ? source : p.Id, spell.Id); // Units.OnEnergyChange
        p.SendAround(new EnergyUpdateEvent { ddn = p.Id, b5b = ticks, b5c = gain, b5d = after, b5e = (byte)(overTime ? 5 : 2), b5f = source != 0 ? source : p.Id, b5g = spell.Id });
    }

    /// <summary>
    /// <paramref name="amount"/> energy the caster's own spell takes from it (HYPER_FOCUS and IMMORTAL: −2 % of the maximum per
    /// second): EnergyUpdate {0 me, 1 ticks, 2 −amount, 3 energy, 4 origin (5 over time, 2 direct), 5 me, 6 spell} as
    /// <see cref="GainEnergy"/>. A toggle whose next tick can no longer be paid ends (<see cref="EndToggle"/>, energy exhaustion).
    /// </summary>
    private static void DrainEnergy(SpellContext c, SpellTemplate spell, double amount, HitOrigin origin)
    {
        var p = c.Caster;
        if (p.Combat.Down is not null || amount <= 0) return;
        var after = DrainEnergy(p, amount, spell, p.Id, c.Ticks, origin);
        if (c.Toggle is { Over: false } toggle && after < amount) EndToggle(p, toggle, ToggleEnd.NoEnergy);
    }

    /// <summary>
    /// <paramref name="amount"/> energy taken from <paramref name="p"/> (a player or a bot; stageJ pvp-spells SPEC §2.5, phase 3:
    /// a Hostile player's by a player's spell, a player's by a mob's, UNDEAD_HARVESTER_ENERGY_DRAIN −10 % of the maximum) down to 0:
    /// EnergyUpdate (8) {0 p, 1 ticks, 2 −taken, 3 energy, 4 origin (5 over time, 2 direct), 5 <paramref name="source"/> (the caster
    /// [inference], as a Friend's gain names it), 6 spell} to it and its observers. A knocked-down player loses none. Returns the energy left.
    /// </summary>
    internal static float DrainEnergy(Player p, double amount, SpellTemplate spell, long source, long ticks, HitOrigin origin)
    {
        var before = p.Energy.At(ticks);
        if (p.Combat.Down is not null || amount <= 0) return before;
        var after = (float)Math.Max(0, before - amount);
        p.Energy.Set(after, Math.Max(ticks, p.Energy.Stamp));
        p.World.Plugins.EnergyChanged(p, before, after, source, spell.Id); // Units.OnEnergyChange
        p.SendAround(new EnergyUpdateEvent
        {
            ddn = p.Id, b5b = ticks, b5c = after - before, b5d = after, b5e = (byte)(origin == HitOrigin.OverTime ? 5 : 2), b5f = source, b5g = spell.Id,
        });
        return after;
    }

    /// <summary>
    /// attributechangeovertime: count ticks, the first initialinterval after the hit, then every interval, each a change as
    /// <see cref="Change"/> with origin 5 and no CastHit (FIRESTAFFBOLT2: -4 × AP / 100, 4 ticks at +0.3, +1.3, +2.3, +3.3 s),
    /// × the stacks of its entry in the target's list (stageD SPEC §1: a second bolt within 3.3 s, ticks of 2 × the per-stack
    /// value). A new application (a stack more) drops the older ticks; the ticks stop when the entry leaves the list (the
    /// mob died or reset, a removal, the caster's knockdown).
    /// </summary>
    private static void OverTime(SpellContext c, SpellTemplate spell, XElement node, int index, IUnit? target)
    {
        var count = (int)SpellTemplate.Float(node, "count", 1);
        var first = SpellTemplate.Float(node, "initialinterval");
        var interval = SpellTemplate.Float(node, "interval", 1);
        var unit = UnitOf(c, target);
        var entry = unit.Effects.Find(spell.Id, c.Caster.Id);
        var sub = entry is not null && index < entry.Subs.Length && entry.Subs[index].Active ? entry.Subs[index] : null;
        var generation = sub is null ? 0 : ++sub.Generation;
        for (var k = 0; k < count; k++)
        {
            var ticks = c.Ticks + Seconds(first + k * interval);
            At(c.Caster.World, c.Map, ticks, () =>
            {
                if (c.Caster.Map != c.Map || c.ToggledOff || target is Player { Map: var there } && there != c.Map) return; // a Friend who left
                if (sub is not null && (entry!.Cancelled || sub.Generation != generation)) return;
                c.Caster.World.Plugins.BuffTicked(unit, entry); // the plugins' Buffs.OnTick
                Change(c with { Ticks = ticks }, spell, node, target, HitOrigin.OverTime, firstHit: false, entry?.Stacks ?? 1);
            });
        }
    }

    /// <summary>
    /// pulsingspell: the named spell count times, initialinterval after the hit and then every interval. target="self": on the caster.
    /// Else on the context's unit (its target, or the caster) while it still matches the node's class (<see cref="Pulsed"/>, stageJ pvp-spells
    /// SPEC §2.6): ARMOR_WEB_EFFECT's enemy pulse skips a Friend in the web, MAGICCIRCLE_PULSING's friendall pulse buffs its owner too.
    /// </summary>
    private static void Pulses(SpellContext c, XElement node, SpellTemplate pulsed)
    {
        var count = (int)SpellTemplate.Float(node, "count", 1);
        var first = SpellTemplate.Float(node, "initialinterval");
        var interval = SpellTemplate.Float(node, "interval", 1);
        var self = (string?)node.Attribute("target") == "self";
        for (var k = 0; k < count; k++)
        {
            var ticks = c.Ticks + Seconds(first + k * interval);
            At(c.Caster.World, c.Map, ticks, () =>
            {
                if (c.Caster.Map != c.Map || c.ToggledOff) return;
                if (!self && !Pulsed(c, node, pulsed, ticks)) return;
                var point = self ? c.Caster.PositionAt(DateTime.UtcNow) : c.Center;
                Execute(c with { Target = self ? null : c.Target, Point = point, Ticks = ticks, Depth = c.Depth + 1 }, pulsed);
            });
        }
    }

    /// <summary>
    /// Whether a pulse of a non-self pulsingspell <paramref name="node"/> runs on the context's unit at <paramref name="ticks"/>: the unit
    /// (its target, or the caster) matches the node's class for the caster (<see cref="SpellTargets.Effect"/>; empty = enemy, as
    /// <see cref="Targets"/> reads it) and the pulsed spell's own class takes its kind (<see cref="SpellTargets.Kind"/>). A mob that died or
    /// went home, a Friend who left or turned Hostile no longer matches.
    /// </summary>
    private static bool Pulsed(SpellContext c, XElement node, SpellTemplate pulsed, long ticks)
    {
        var cls = SpellTargets.Parse((string?)node.Attribute("target"));
        if (cls is SpellTargets.Cls.None or SpellTargets.Cls.Ground) cls = SpellTargets.Cls.Enemy;
        var unit = c.Unit;
        if (unit is Player { Map: var there } && there != c.Map) return false;
        return SpellTargets.Effect(cls, SpellTargets.Of(c.Caster, unit, ticks)) && SpellTargets.Kind(pulsed, unit);
    }

    /// <summary>
    /// removeactivespell spell="X" (charges n: n stacks; casterchargesonly: only the caster's entries) and removeactiveeffects
    /// category="C" (every entry of that category) on <paramref name="unit"/> (stageD SPEC §3.3): the list goes out at once.
    /// </summary>
    private static bool RemoveEffects(SpellContext c, XElement node, IUnit unit, bool send = true)
    {
        var world = c.Caster.World;
        var charges = (int)SpellTemplate.Float(node, "charges");
        var mine = SpellTemplate.Bool(node, "casterchargesonly");
        Func<ActiveSpell, bool> match = node.Name.LocalName == "removeactivespell"
            ? (string?)node.Attribute("spell") is var name && name is not null ? s => s.Spell.Name == name && (!mine || s.Caster == c.Caster.Id) : _ => false
            : (string?)node.Attribute("category") is { Length: > 0 } category ? s => s.Spell.Category == category : _ => false;
        if (!SpellEffects.Remove(world, unit, match, charges)) return false;
        if (!send) return true;
        SpellEffects.Send(world, unit);
        SpellEffects.Refresh(world, unit);
        return true;
    }

    /// <summary>
    /// Whether effect <paramref name="index"/> of <paramref name="spell"/> is a removal (removeactivespell, removeactiveeffects)
    /// before every sub-effect the spell lists (<see cref="SpellEffects.Listed"/>): in document order it acts before them, so it
    /// runs before the listing (<see cref="Plan.Removals"/>) and cannot take the spell's own new entry (FOOD_*: removeactiveeffects
    /// foodbuff, then the buff; stageD consumables SPEC §4.2). An applyspell counts as listing (its spell's entries are listed with this
    /// spell's): BUFFSHRINE_RANDOM_BUFFS removes its three buffs, then applies them again (stageN shrines SPEC §4).
    /// </summary>
    private static bool Leading(SpellTemplate spell, int index)
    {
        if (spell.Effects[index].Name.LocalName is not ("removeactivespell" or "removeactiveeffects")) return false;
        static bool Lists(XElement e) => SpellEffects.Listed.Contains(e.Name.LocalName) || e.Name.LocalName == "applyspell";
        for (var i = 0; i < index; i++)
            if (Lists(spell.Effects[i])) return false;
        for (var i = index + 1; i < spell.Effects.Count; i++)
            if (Lists(spell.Effects[i])) return true;
        return false;
    }

    /// <summary>The spell an applyspell/pulsingspell names; logged once and null when spells.xml has none.</summary>
    private static SpellTemplate? Sub(SpellContext c, XElement node)
    {
        var name = (string?)node.Attribute("spell");
        if (c.Caster.World.Spells?.Template(name) is { } sub) return sub;
        Combat.LogOnce("spell:" + name, $"spell {name} named by {c.Root.Name} is not in spells.xml");
        return null;
    }

    // ---- conditions ----

    /// <summary>
    /// Whether <paramref name="node"/> runs in this Execute: its conditions hold and its chance (applyspell chance="p") comes
    /// up; decided once per node and context target.
    /// </summary>
    private static bool Holds(Plan plan, SpellContext c, XElement node)
    {
        var key = (node, c.Target?.Id ?? 0);
        if (plan.Holds.TryGetValue(key, out var holds)) return holds;
        holds = Conditions(c, node) && (node.Attribute("chance") is null || c.Map.Random.NextDouble() < SpellTemplate.Float(node, "chance", 1));
        plan.Holds[key] = holds;
        return holds;
    }

    /// <summary>Whether every condition child of <paramref name="node"/> holds (nodes without conditions always run).</summary>
    private static bool Conditions(SpellContext c, XElement node) => node.Elements().Where(IsCondition).All(e => Holds(c, e));

    /// <summary>Whether the conditions of <paramref name="node"/> hold for <paramref name="c"/> (a passive's trigger, <see cref="Passives.Trigger"/>).</summary>
    internal static bool ConditionsHold(SpellContext c, XElement node) => Conditions(c, node);

    private static bool IsCondition(XElement e) => e.Name.LocalName is "not" or "and" or "or" or "Event" || e.Name.LocalName.StartsWith("If", StringComparison.Ordinal);

    /// <summary>The unit a condition asks about: who="Caster" the caster, else (SpellTarget, EffectTarget) the context's unit.</summary>
    private static IUnit Who(SpellContext c, XElement e) => (string?)e.Attribute("who") == "Caster" ? c.Caster : c.Unit;

    /// <summary>The live entries of spell <paramref name="name"/> on <paramref name="unit"/> (casterchargesonly: only <paramref name="c"/>'s caster's).</summary>
    private static IEnumerable<ActiveSpell> Entries(SpellContext c, XElement e, IUnit unit, string? name)
    {
        var now = c.Caster.World.GameNow;
        unit.Effects.Prune(now);
        var mine = SpellTemplate.Bool(e, "casterchargesonly");
        return unit.Effects.Spells.Where(s => s.Spell.Name == name && (!mine || s.Caster == c.Caster.Id) && s.Live(now));
    }

    private static bool Holds(SpellContext c, XElement e)
    {
        var name = e.Name.LocalName;
        switch (name)
        {
            // by3.tf 0x1807bf040: true when none of its children holds (PASSIVE_CAPE_BRIDGEWATCH: not cape_cooldown, not the dismount debuff).
            case "not": return !e.Elements().Where(IsCondition).Any(x => Holds(c, x));
            case "and": return e.Elements().Where(IsCondition).All(x => Holds(c, x));
            case "or": return e.Elements().Where(IsCondition).Any(x => Holds(c, x));
            case "Event": return true; // a trigger (onSuccessfullHit): the effect runs with its hit
            case "IfTargetType": // stageJ pvp-spells SPEC §2.5, phase 0: of the context's unit (its target, else the caster)
            {
                var unit = c.Unit;
                return ((string?)e.Attribute("type") ?? "") switch
                {
                    "mob" => unit is Mob,
                    "player" => unit is Player,
                    "self" => unit == c.Caster,
                    "enemy" => SpellTargets.Of(c.Caster, unit, c.Ticks).Hostile,
                    "friendall" => SpellTargets.Of(c.Caster, unit, c.Ticks).Friend,
                    "groupmember" => unit is Player q && Pvp.SameParty(c.Caster, q),
                    "guildmember" => unit is Player g && c.Caster.World.Guilds.Same(c.Caster.Character.Id, g.Character.Id),
                    "alliancemember" => unit is Player al && c.Caster.World.Alliances.SameAlliance(c.Caster.Character.Id, al.Character.Id),
                    _ => false,
                };
            }
            case "IfChance": return c.Map.Random.NextDouble() < SpellTemplate.Float(e, "value", 1);
            case "IfTargetRange":
                if (c.Target is null) return false;
                return Compare(e, Vector2.Distance(c.Caster.PositionAt(DateTime.UtcNow), SpellMovement.PositionOf(c.Target, c.Ticks)));
            case "IfSpellActive":
                // spell, casterchargesonly, atleastcharges (stacks).
                return Entries(c, e, Who(c, e), (string?)e.Attribute("spell")).Any(s => s.Stacks >= Math.Max(1, (int)SpellTemplate.Float(e, "atleastcharges", 1)));
            case "IfSpellCategoryActive":
            {
                var unit = Who(c, e);
                var category = (string?)e.Attribute("category");
                var now = c.Caster.World.GameNow;
                return unit.Effects.Spells.Any(s => s.Spell.Category == category && s.Live(now));
            }
            case "IfSpellEffectActive":
            {
                var effect = (string?)e.Attribute("effectname");
                var now = c.Caster.World.GameNow;
                return Entries(c, e, Who(c, e), (string?)e.Attribute("spell"))
                    .Any(s => s.Subs.Any(sub => s.On(sub, now) && (string?)sub.Node.Attribute("effectname") == effect));
            }
            case "IfConsumeCharges":
            {
                // True, and takes the charges, when the unit has that many stacks of the spell.
                var unit = Who(c, e);
                var charges = Math.Max(1, (int)SpellTemplate.Float(e, "charges", 1));
                if (Entries(c, e, unit, (string?)e.Attribute("spell")).FirstOrDefault(s => s.Stacks >= charges) is not { } entry) return false;
                var world = c.Caster.World;
                if (SpellEffects.Remove(world, unit, s => s == entry, charges))
                {
                    SpellEffects.Send(world, unit);
                    SpellEffects.Refresh(world, unit);
                }
                return true;
            }
            case "IfTargetCCEffect": // CLEANSE: the unit is under that crowd control (stun, root, silence, slow, …)
            {
                var type = (string?)e.Attribute("type");
                return Who(c, e).Effects.On(c.Caster.World.GameNow).Any(x => x.Sub.CcType == type);
            }
            case "IfClusterArcheType": // stageG: the worldsettings archetype of the caster's cluster type (the dungeon maps: GREEN, TUNNEL, …)
            {
                var type = c.Map.Template?.Info.Type;
                string? archetype = null;
                try { archetype = type is null ? null : c.Caster.World.Clusters?.Archetypes.Of(type).Archetype; }
                catch (Exception x) when (x is InvalidDataException or IOException) { }
                var equal = string.Equals(archetype, (string?)e.Attribute("value"), StringComparison.OrdinalIgnoreCase);
                return ((string?)e.Attribute("op") ?? "equals") is "equals" or "equal" ? equal : !equal;
            }
            case "IfOwnLoadPercent": // the carried load in % (weightbonus, maxload buffs)
            {
                var load = (float)Passives.LoadPercent(c.Caster);
                if (Compare(e, load)) return true;
                // The dashes and blinks need less than 150 % (SWORD_SPIN, FLAMEDASH, …): an overloaded caster's spell runs without its move.
                Log.Info("combat", string.Create(CultureInfo.InvariantCulture,
                    $"'{c.Caster.Character.Name}' {c.Root}: load {load:0.#} % of {Passives.MaxLoad(c.Caster):0.#} kg, its <{e.Parent?.Name.LocalName}> is skipped ({(string?)e.Attribute("op")} {(string?)e.Attribute("value")} % needed)"));
                return false;
            }
            case "IfOwnHealthPercentage": // the caster's health share (the capes' low-health triggers)
                return Compare(e, c.Caster.Health.Max > 0 ? c.Caster.Health.At(c.Ticks) / c.Caster.Health.Max : 0);
            case "IfOwnEnergyPercentage": // the caster's energy share (IMMORTAL_CONDITION: less 0.02 ends the toggle)
                return Compare(e, c.Caster.Energy.Max > 0 ? c.Caster.Energy.At(c.Ticks) / c.Caster.Energy.Max : 0);
            case "IfNumberOfTargetsHit": // the units the condition's node reaches (ROOTFIELD's later ticks root only 3 or more)
                return e.Parent is { } node && Compare(e, Targets(c, node).Count);
            default:
                if (SpellConditions.Holds(e, Facts(c)) is { } known) return known;
                Combat.LogOnce("cond:" + name, $"spell condition <{name}> is not supported yet: read as false");
                return false;
        }
    }

    /// <summary><paramref name="v"/> against the condition's value with its op (<see cref="SpellConditions.Compare"/>).</summary>
    private static bool Compare(XElement e, float v) => SpellConditions.Compare(e, v);

    /// <summary>What the shared conditions are asked with: the context's unit is both the spell's and the effect's target.</summary>
    private static SpellConditions.Facts Facts(SpellContext c) => new(c.Caster, c.Unit, c.Unit, c.Map, c.Ticks, c.ChannelTick);

    /// <summary>Seconds as GameTimeStamp ticks.</summary>
    public static long Seconds(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
}
