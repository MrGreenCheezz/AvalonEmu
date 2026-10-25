using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.9, §1.10, bots-combat SPEC §2.5, §2.7): a bot's fight. A player's hit on it
/// (<see cref="Attacked"/>, from <see cref="Pvp.Damage"/> right after its refusal check, also a hit on its mount bar) engages it: its harvest
/// show and emote end, the attacker is its target and for Bots:Pvp:EngageSeconds (20) after its last hit the bot may fight it
/// (<see cref="BotPvp.Engaged"/>). Its brain then hands over to <see cref="Think"/> every Bots:Combat:TickMs (200): the target is dropped when
/// it left the map or the bot's view, is refused (<see cref="Pvp.Refusal"/>: knocked down, no longer Hostile), is in a duel, stands
/// beyond Bots:Patrol:LeashRadius (40) of where the fight began or (a gatherer) is no longer engaged (its or its party's last hit older than
/// the engage time, whatever Pvp:Mode makes the pair); the most recent attacker that is still valid comes next, and one that dealt at least
/// 1.5 × the target's health damage in the last 5 s takes its place (<see cref="Hurt"/>). The first swing
/// comes Bots:Combat:ReactionMs (600) after the first hit; then auto-attacks with its mainhand (a gatherer's tool, or the weapon an armed one draws, <see cref="Arm"/>) at the weapon's interval
/// within its range (<see cref="Pvp.Swing"/>: Attack to its observers, the target's HealthUpdate at the impact), a riding bot dismounted
/// first (<see cref="Mounts.BeforeAction"/>), out of range a chase (<see cref="BotNav.ChaseTo"/>); with Bots:Combat:Spells Full the spells of
/// its gear too (<see cref="CastSpell"/>, stageJ pvp-spells SPEC §2.9), and no swing or chase while its cast or channel runs. A gatherer below Bots:Gatherer:FleeHealth
/// runs off once per fight with Bots:Gatherer:FleeChance for 20 s (<see cref="BotNav.FleeFrom"/>), with no swing meanwhile. The fight ends
/// when no attacker is engaged any more. Phase 3, a patrol: it also starts fights (<see cref="Scan"/>: on sight, the server's PvP rules
/// willing), its group takes its target on (<see cref="Engage"/>, <see cref="GroupTarget"/>), a target given up at the leash is left alone
/// for <see cref="LeashSpareSeconds"/>, and a player its group knocked down for Bots:Patrol:SpareMinutes (<see cref="KnockedDownPlayer"/>).
/// At 0 health <see cref="Defeated"/> is the one owner of the bot's end: a death in a lethal zone (Bots:Defeat:DieInLethalZones: Died to the
/// map's humans), a knockdown elsewhere (KnockedDown to its observers), the killer's rewards and PvP goals (<see cref="BotRewards"/>), gone
/// after playervisibilityafterdeath (3 s) or Bots:DownDespawnSeconds (10 s) with one Leave, its slot respawning after Bots:RespawnSeconds.
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class BotCombat
{
    /// <summary>How long a gatherer runs off (s, SPEC §1.9): the default of Bots:Gatherer:FleeSeconds.</summary>
    public const double FleeSeconds = 20;

    /// <summary>How far it runs (m): the default of Bots:Gatherer:FleeDistance.</summary>
    public const float FleeDistance = 25;

    /// <summary>The window of the retarget's damage (s), and the share an attacker must deal of the target's damage in it (SPEC §1.9): the defaults of Bots:Combat:RetargetSeconds / RetargetShare.</summary>
    public const double RetargetSeconds = 5;

    public const double RetargetShare = 1.5;

    /// <summary>How long a patrol leaves a target alone that it gave up at the leash (s): no chase back and forth at its edge.</summary>
    public const double LeashSpareSeconds = 30;

    /// <summary>A patrol attacks a player on sight only when its way to him is at most this many times the straight distance (bots-world SPEC §2.6).</summary>
    public const float DetourFactor = 1.5f;

    /// <summary>
    /// A hit of <paramref name="attacker"/> on <paramref name="bot"/> at GameTimeStamp <paramref name="ticks"/> (before its damage): the
    /// attacker engaged, the fight's centre where the bot stands, the target when it has none, the first swing Bots:Combat:ReactionMs later;
    /// its harvest show (HarvestCancel) and looping emote end, and it decides at the next tick. City bots are never hit (Friendly).
    /// </summary>
    public static void Attacked(BotManager bots, Player bot, Player attacker, long ticks)
    {
        if (bot.Bot is not { } info || info.Role == BotRole.City || attacker.IsBot || info.Fight.Defeated) return;
        var fight = info.Fight;
        if (!fight.Active) fight.ReadyAt = ticks + bots.Options.ReactionMs * TimeSpan.TicksPerMillisecond;
        fight.Engaged[attacker.Id] = ticks;
        if (info.Group is { } group) group.Engaged[attacker.Id] = ticks; // the whole group may answer him, even after this one's defeat
        fight.EngagePoint ??= bot.PositionAt(bots.Time);
        if (fight.TargetId == 0) fight.TargetId = attacker.Id;
        bots.EndShow(bot, cancelled: true);
        bots.StopEmote(bot);
        Arm(bot, true, ticks);
        info.NextThink = bot.World.Clock.Now;
        info.Brain.Attacked(bot, attacker);
    }

    /// <summary>
    /// StageM bots: an armed gatherer (content/bots.json Gear.GathererWeaponShare) swaps the weapon in its bag into the mainhand when attacked
    /// (<paramref name="arm"/>) and its tool back when the fight is over: Container moves of its own two items (never a copy), its stats
    /// worked out again with the current health and energy kept, then RegenerationPlayerComboChanged when its bars changed and
    /// CharacterEquipmentChanged {0 bot, 1 now, 2 type ids, 7 spell slots, 8 true} to its observers, as a player's equip sends them. A
    /// tool has no active spell slot; the weapon brings three. True when it swapped.
    /// </summary>
    public static bool Arm(Player bot, bool arm, long ticks)
    {
        if (bot.Bot is not { Role: BotRole.Gatherer } info || info.Armed == arm) return false;
        var (equipment, bag) = (bot.Inventory.Equipment, bot.Inventory.Bag);
        var swap = -1;
        for (var i = 0; i < bag.Size && swap < 0; i++)
            if (bag[i] is { Template.Slot: "mainhand" } candidate && IsTool(bot, candidate) != arm) swap = i;
        if (swap < 0) return false;
        var into = bag.Take(swap)!;
        bag.Put(swap, equipment.Take(0));
        equipment.Put(0, into);
        info.Armed = arm;
        if (bot.RecalculateStats(ticks, keep: true)) bot.SendToObservers(Combat.PlayerCombo(bot, ticks));
        bot.SendToObservers(new CharacterEquipmentChangedEvent
        {
            ddn = bot.Id,                               // [0] the bot
            bw3 = ticks,                                // [1] GameTimeStamp
            bw4 = bot.Inventory.EquipmentTypeIds(),     // [2] item type id per EquipmentSlot
            bw9 = bot.Session.SpellSlots(bot.Inventory), // [7] active spell per CharacterSpellSlot
            bxa = true,                                 // [8] an item went into the equipment
            bxb = false,                                // [9] no spell change
        });
        Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}) {(arm ? "draws its" : "puts back its tool for its")} {into.Template.Name}");
        return true;
    }

    /// <summary>Whether <paramref name="item"/> is one of the gathering tools of content/bots.json Gear.Tools (any tier).</summary>
    private static bool IsTool(Player bot, Item item) =>
        bot.World.BotSettings.Tools.Values.Any(t => Enumerable.Range(1, 8).Any(tier => t.Replace("{t}", tier.ToString(CultureInfo.InvariantCulture)) == item.Template.Name));

    /// <summary>
    /// <paramref name="attacker"/>'s hit took <paramref name="damage"/> of <paramref name="bot"/>'s health at <paramref name="ticks"/>
    /// (<see cref="Pvp.Damage"/>, next to <see cref="Death.Hit"/>): kept for the retarget for Bots:Combat:RetargetSeconds.
    /// </summary>
    public static void Hurt(Player bot, Player attacker, double damage, long ticks)
    {
        if (bot.Bot is not { } info || damage <= 0 || info.Fight.Defeated) return;
        var hits = info.Fight.Hits;
        hits.RemoveAll(h => h.At < ticks - (long)(bot.World.Config.Bots.RetargetSeconds * TimeSpan.TicksPerSecond));
        hits.Add((attacker.Id, ticks, damage));
    }

    /// <summary>
    /// One fighting decision of <paramref name="bot"/> (see the class); false when the fight is over (no attacker engaged any more), so that
    /// its brain takes over again.
    /// </summary>
    public static bool Think(BotManager bots, Player bot)
    {
        var info = bot.Bot!;
        var fight = info.Fight;
        if (fight.Defeated) return true;
        if (bot.Map is not { } map || bots.NavOf(map) is not { } nav) return false;
        var world = bot.World;
        var ticks = world.GameNow;
        var since = ticks - (long)(bots.Options.EngageSeconds * TimeSpan.TicksPerSecond);
        foreach (var gone in fight.Engaged.Where(e => e.Value < since).Select(e => e.Key).ToList()) fight.Engaged.Remove(gone);
        if (info.Group is { } group)
            foreach (var gone in group.Engaged.Where(e => e.Value < since).Select(e => e.Key).ToList()) group.Engaged.Remove(gone);
        if (fight.FleeUntil is { } until)
        {
            if (ticks < until) return true; // running off: no swings
            fight.FleeUntil = null;
        }
        // Stunned by a player's spell (stageJ pvp-spells phase 2): it stands, without turning, chasing or swinging, as a stunned player does.
        if (SpellEffects.Stunned(bot, ticks)) return true;
        if (fight.TargetId != 0 && Valid(bots, bot, fight.TargetId, ticks) is null)
        {
            if (Leashed(bots, bot, fight.TargetId) is { } gone) Spare(bot, gone, LeashSpareSeconds, ticks);
            fight.TargetId = 0;
        }
        if (fight.TargetId == 0)
        {
            fight.TargetId = fight.Engaged.OrderByDescending(e => e.Value).Select(e => e.Key).FirstOrDefault(id => Valid(bots, bot, id, ticks) is not null);
            if (fight.TargetId == 0 && BotManager.Patrols(info.Role))
                fight.TargetId = Scan(bots, bot, nav)?.Id ?? MemberTarget(bots, bot, ticks) ?? 0; // a patrol looks around, or helps its group
        }
        else if (Retarget(bots, bot, ticks) is { } other)
        {
            Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}) turns to its harder hitter {other}");
            fight.TargetId = other;
        }
        if (fight.TargetId == 0)
        {
            if (fight.Engaged.Count > 0) return true; // its attackers are out of reach for now: it waits while they are engaged
            fight.End();
            if (info.Motion.Busy) info.Motion.Stop(bot, null, bots);
            Arm(bot, false, ticks); // an armed gatherer takes its tool again
            Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}): the fight is over");
            return false;
        }
        var target = map.FindPlayer(fight.TargetId)!;
        var here = bot.PositionAt(bots.Time);
        if (info.Role == BotRole.Gatherer && !fight.FleeRolled && bot.Health.At(ticks) < bot.Health.Max * bots.Options.FleeHealth)
        {
            fight.FleeRolled = true;
            if (bots.Random.NextDouble() < bots.Options.FleeChance && BotNav.FleeFrom(bots, nav, bot, target.PositionAt(DateTime.UtcNow), (float)bots.Options.FleeDistance))
            {
                fight.FleeUntil = ticks + (long)(bots.Options.FleeSeconds * TimeSpan.TicksPerSecond);
                Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}) runs off from '{target.Character.Name}'");
                return true;
            }
        }
        // Its own cast or channel under way (stageJ pvp-spells SPEC §2.9): it stands, as a player's client does not swing meanwhile.
        if (bot.Combat.Cast is not null || bot.Combat.Channel is not null)
        {
            fight.SpellRollAt = ticks; // its own cast's time gives no chance of the next one
            return true;
        }
        var weapon = Combat.WeaponOf(bot);
        if (bots.Options.Tactics && ticks >= fight.ReadyAt)
        {
            if (LowHealth(bots, bot, nav, target, ticks)) return true;
            if (ticks < fight.KiteUntil) return true; // stepping back: no swing meanwhile
            if (Focus(bots, bot, weapon.Range, ticks) is { } weaker)
            {
                Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}) turns to the weaker '{weaker.Character.Name}'");
                fight.TargetId = weaker.Id;
                target = weaker;
            }
            if (Kite(bots, bot, nav, target, here, weapon.Range, ticks)) return true;
        }
        if (ticks >= fight.ReadyAt && CastSpell(bots, bot, target, here, ticks)) return true;
        if (Pvp.EdgeDistance(bot, here, target) <= weapon.Range + SpellCasts.RangeTolerance)
        {
            var facing = MotionMaster.Facing(here, target.PositionAt(DateTime.UtcNow));
            var turn = Math.Abs(((bot.Facing - facing) % 360 + 540) % 360 - 180);
            if (info.Motion.Busy || bot.Movement is { Speed: > 0 } || turn > 20) info.Motion.Stop(bot, facing, bots); // it stands and turns to the target
            var last = bot.Combat.LastSwing;
            if (ticks < fight.ReadyAt || bot.Combat.Down is not null || (last != 0 && ticks < last + SpellCasts.SwingInterval(bot))) return true;
            if (Pvp.Refusal(bot, target, ticks) is not null) return true;
            if (SpellEffects.CannotAttack(bot, ticks)) return true; // cannotattack without a stun (a player's spell, stageJ pvp-spells phase 2): it turns and walks, no swing
            Knockdown.Acted(bot, "attacks");
            Mounts.BeforeAction(bot, "attacks"); // a riding bot is dismounted first, as the handler does for a player
            Pvp.Swing(bot, target, 0, ticks);
            return true;
        }
        // Out of range: up to the weapon's reach less half a metre, centre to centre (the edges plus both collision radii).
        var reach = (float)Math.Max(0.5, weapon.Range + 2 * world.CollisionRadius - 0.5);
        if (!BotNav.ChaseTo(bots, nav, bot, target, reach)) fight.TargetId = 0; // no way to it: the next attacker, or none
        return true;
    }

    /// <summary>A self spell of a bot (a buff, a spin around itself) goes out when its target is this near, edge to edge (m).</summary>
    public const float SelfSpellReach = 5;

    /// <summary>A friendly spell of a bot (a heal, a shield) goes on itself below this share of its health.</summary>
    public const double HealHealth = 0.7;

    /// <summary>
    /// A spell of <paramref name="bot"/>'s gear at <paramref name="target"/> (stageJ pvp-spells SPEC §2.9, Bots:Combat:Spells Full), with
    /// Bots:Combat:SpellChance per second of its fight (on the game time since its last roll, at most 1 s: the extra decisions a hit brings,
    /// <see cref="Attacked"/>, add no chance): its active slots (Q, W, E, armour, helmet, shoes; <see cref="Network.WorldSession.SpellSlots"/>,
    /// a full choice by <see cref="Network.WorldSession.NewBotItem"/>) by intent (stageM bots: a heal when hurt, then the attacks, then the
    /// buffs not on it yet, each group in a random order; never a usableincombat="false" one), the first one that the players' cast checks accept
    /// (<see cref="SpellCasts.Check"/>: the slot, stun and silence, the target's class and range, cooldown and energy): an enemy or any-unit
    /// spell (enemy…, all…, other…) at the target (key 5 its id), a ground spell at its position with the target within castrange edge to
    /// edge (else none: no area on empty ground, no stop in the chase), a friendly one (FriendAll…) on itself below <see cref="HealHealth"/>
    /// of its health, any other on itself (self) with the target within <see cref="SelfSpellReach"/>. Toggles and the potion and food slots
    /// are left alone. It stops facing the target, is dismounted (<see cref="Mounts.BeforeAction"/>) and casts as the CastStart handler does
    /// (<see cref="SpellCasts.Start"/>: CastStart to its observers, the hit, a dash or channel of its own, every effect under the server's
    /// PvP rules for a bot caster, <see cref="SpellTargets.Of"/>). True when a cast started.
    /// </summary>
    private static bool CastSpell(BotManager bots, Player bot, Player target, Vector2 here, long ticks)
    {
        var options = bots.Options;
        if (options.Spells == BotSpells.Off || options.SpellChance <= 0 || bot.World.Spells is not { } spells || bot.Bot is not { } info) return false;
        var fight = info.Fight;
        var elapsed = fight.SpellRollAt == 0 ? options.CombatTickMs / 1000.0 : Math.Min(1, (ticks - fight.SpellRollAt) / (double)TimeSpan.TicksPerSecond);
        fight.SpellRollAt = ticks;
        var rolled = elapsed > 0 && bots.Random.NextDouble() < 1 - Math.Pow(1 - Math.Min(1, options.SpellChance), elapsed);
        // StageN botpower (Bots:Combat:Tactics): the situation's spells go out as soon as they are ready, without the roll.
        var tactics = options.Tactics;
        if (!rolled && !tactics) return false;
        var slots = bot.Session.SpellSlots(bot.Inventory);
        var at = SpellMovement.PositionOf(target, ticks);
        var distance = Pvp.EdgeDistance(bot, here, target);
        var hurt = bot.Health.At(ticks) < bot.Health.Max * HealHealth;
        var weaponRange = Combat.WeaponOf(bot).Range;
        var low = tactics && bot.Health.At(ticks) < bot.Health.Max * LowHealthShare;
        var far = tactics && weaponRange < RangedWeapon && distance > weaponRange + 3; // a melee bot that must close in
        var casting = tactics && (target.Combat.Cast is not null || target.Combat.Channel is not null);
        var finish = tactics && target.Health.At(ticks) < target.Health.Max * FinishHealth;
        // StageM bots: by intent, a heal first when it is hurt, then its attacks (enemy, ground, a damage or cc spell around itself), a buff
        // last and only when it is not on it already; a spell with usableincombat="false" never (OUTOFCOMBATHEAL).
        var choices = new List<(int Slot, int Id, SpellTemplate Spell, long TargetId, Vector2 Aim, int Rank, int Roll)>();
        foreach (var slot in Enumerable.Range(0, 6))
        {
            var id = slots[slot];
            if (id < 0 || spells.Template(id) is not { Kind: "activespell" } spell || !spell.UsableInCombat) continue;
            var cls = SpellTargets.Parse(spell.Target);
            long targetId = 0;
            var aim = here;
            int rank;
            if (cls == SpellTargets.Cls.Ground)
            {
                if (distance > spell.CastRange) continue; // out of its reach: the chase goes on
                var line = at - here; // the centre may lie a little beyond castrange: aimed at castrange towards it
                aim = line.Length() > spell.CastRange && line.Length() > 0.01f ? here + line / line.Length() * spell.CastRange : at;
                rank = far && Mobility(spell) || finish && Damage(spell) || casting && Control(spell) ? Urgent : 1;
            }
            else if (SpellCasts.Enemy(cls) || cls is SpellTargets.Cls.Other or SpellTargets.Cls.OtherPlayers or SpellTargets.Cls.All
                     or SpellTargets.Cls.AllPlayers or SpellTargets.Cls.AllUnmounted or SpellTargets.Cls.AllUnmountedPlayers or SpellTargets.Cls.AllNotPassive)
            {
                targetId = target.Id; // FIRESTAFFIGNITE2_SPREAD (all) is an enemy spell: at the target, Check judges it Hostile
                rank = casting && Control(spell) || far && Mobility(spell) || finish && Damage(spell) ? Urgent : 1;
            }
            else if (cls is >= SpellTargets.Cls.FriendAll and <= SpellTargets.Cls.FriendAllNotPassive)
            {
                if (!hurt) continue; // a heal or shield on itself only when it needs one
                rank = low ? Urgent - 1 : 0;
            }
            else
            {
                // StageN botpower: low, a shield or heal around itself whatever the distance; far, a sprint of its own to close in.
                var defend = low && (Defensive(spell) || !Offensive(spell) && spell.UiType == "buff");
                var sprint = far && Mobility(spell);
                if (!defend && !sprint && distance > SelfSpellReach) continue; // a buff or a spin around itself only with the target near
                rank = defend ? Urgent - 1 : sprint ? Urgent : Offensive(spell) ? 1 : 2;
                if (rank != 1 && OnItself(bot, spell, ticks)) continue; // the buff is on it still: no second one
            }
            if (!rolled && rank >= 0) continue; // without the roll only the situation's spells
            choices.Add((slot, id, spell, targetId, aim, rank, bots.Random.Next()));
        }
        foreach (var (slot, id, spell, targetId, aim, _, _) in choices.OrderBy(c => c.Rank).ThenBy(c => c.Roll))
        {
            fight.CastCounter = (byte)(fight.CastCounter % 255 + 1);
            var (cast, _) = SpellCasts.Check(bot, slots, (byte)slot, id, targetId, aim, fight.CastCounter, ticks);
            if (cast is null) continue;
            info.Motion.Stop(bot, MotionMaster.Facing(here, at), bots); // it stands facing the target, then casts (a dash moves it from here)
            Knockdown.Acted(bot, "casts");
            Mounts.BeforeAction(bot, "casts");
            SpellCasts.Start(bot, cast);
            Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}) casts {spell.Name} at '{target.Character.Name}'");
            return true;
        }
        return false;
    }

    /// <summary>A spell around itself that hurts or holds (category damage, crowdcontrol or debuff, or uitype damage or crowdcontrol): an attack.</summary>
    private static bool Offensive(SpellTemplate spell) =>
        spell.Category is "damage" or "crowdcontrol" or "debuff" || spell.UiType is "damage" or "crowdcontrol";

    // ---- stageN botpower: tactics (Bots:Combat:Tactics, analysis/work/stageN/botpower/SPEC.md) ----

    /// <summary>The rank of a spell the situation calls for (before the attacks, 1, and the buffs, 2); a defence when low comes before it.</summary>
    private const int Urgent = -1;

    /// <summary>Below this share of its health a bot's shields and heals go out at once (its potion and fall-back at Bots:Combat:RetreatHealth).</summary>
    public const double LowHealthShare = 0.45;

    /// <summary>Below this share of the target's health a bot bursts it (its damage spells at once) and a patrol turns to such a player.</summary>
    public const double FinishHealth = 0.35;

    /// <summary>A weapon of at least this attack range (m) is a ranged one: its bot kites and never dashes in.</summary>
    public const float RangedWeapon = 6;

    /// <summary>How long a kite's step back lasts (s) and how often at most (s); how long a fall-back lasts (s).</summary>
    public const double KiteSeconds = 1.2, KiteEverySeconds = 2.5, RetreatSeconds = 3;

    /// <summary>A gap closer: a dash, leap or sprint (uitype movement, category movementbuff).</summary>
    private static bool Mobility(SpellTemplate spell) => spell.UiType == "movement" || spell.Category == "movementbuff";

    /// <summary>A defence: a heal or a damage shield (category heal or buff_damageshield, uitype heal).</summary>
    private static bool Defensive(SpellTemplate spell) => spell.Category is "heal" or "buff_damageshield" || spell.UiType == "heal";

    /// <summary>Crowd control (a stun, root, silence, knockback: category or uitype crowdcontrol).</summary>
    private static bool Control(SpellTemplate spell) => spell.Category == "crowdcontrol" || spell.UiType == "crowdcontrol";

    /// <summary>A damage spell (uitype or category damage).</summary>
    private static bool Damage(SpellTemplate spell) => spell.UiType == "damage" || spell.Category == "damage";

    /// <summary>
    /// Below Bots:Combat:RetreatHealth of its health: its healing potion (the potion slot, through the players' cast path; its slot cooldown
    /// willing), else once per fight a fall-back of <see cref="RetreatSeconds"/> away from <paramref name="target"/> (a gatherer keeps its own
    /// flight, Bots:Gatherer:FleeHealth). True when it acted.
    /// </summary>
    private static bool LowHealth(BotManager bots, Player bot, ClusterNav nav, Player target, long ticks)
    {
        var info = bot.Bot!;
        if (bot.Health.At(ticks) >= bot.Health.Max * bots.Options.RetreatHealth) return false;
        if (Drink(bots, bot, Consumables.PotionSlot, ticks)) return true;
        if (info.Role == BotRole.Gatherer || info.Fight.FleeRolled) return false;
        info.Fight.FleeRolled = true;
        if (!BotNav.FleeFrom(bots, nav, bot, target.PositionAt(DateTime.UtcNow), (float)bots.Options.FleeDistance / 2)) return false;
        info.Fight.FleeUntil = ticks + (long)(RetreatSeconds * TimeSpan.TicksPerSecond);
        Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}) falls back from '{target.Character.Name}' at low health");
        return true;
    }

    /// <summary>
    /// <paramref name="bot"/> uses the potion or food of its equipment's <paramref name="slot"/> (CharacterSpellSlot 12 or 13) as a player does
    /// (<see cref="SpellCasts.Check"/>, <see cref="SpellCasts.Start"/>: the stack's item taken at the cast's end, the slot's cooldown). True when it started.
    /// </summary>
    private static bool Drink(BotManager bots, Player bot, byte slot, long ticks)
    {
        if (bot.Bot is not { } info || bot.Combat.Down is not null) return false;
        var slots = bot.Session.SpellSlots(bot.Inventory);
        if (slots[slot] < 0) return false;
        info.Fight.CastCounter = (byte)(info.Fight.CastCounter % 255 + 1);
        var (cast, _) = SpellCasts.Check(bot, slots, slot, slots[slot], 0, bot.PositionAt(bots.Time), info.Fight.CastCounter, ticks);
        if (cast is null) return false;
        if (info.Motion.Busy) info.Motion.Stop(bot, null, bots);
        Mounts.BeforeAction(bot, "casts");
        SpellCasts.Start(bot, cast);
        Log.Debug("bots", $"'{bot.Character.Name}' ({info.PersonaId}) uses {cast.Spell.Name} (slot {slot})");
        return true;
    }

    /// <summary>StageN botpower (Bots:Combat:Food): a new patrol or Drifter eats the meal of its food slot (its buff for its stay).</summary>
    public static void Eat(BotManager bots, Player bot)
    {
        if (bot.Bot is { Role: BotRole.Patrol or BotRole.Drifter } && bot.Inventory.Equipment[9] is not null) Drink(bots, bot, Consumables.FoodSlot, bot.World.GameNow);
    }

    /// <summary>
    /// Bots:Combat:FocusWeakest: a patrol fighting a player above half health turns, at most once a second, to another player it may fight
    /// that is below <see cref="FinishHealth"/> within its weapon's reach + 6 m (the weakest of them). Null when it keeps its target.
    /// </summary>
    private static Player? Focus(BotManager bots, Player bot, double range, long ticks)
    {
        var fight = bot.Bot!.Fight;
        if (!bots.Options.FocusWeakest || !BotManager.Patrols(bot.Bot.Role) || ticks - fight.FocusAt < TimeSpan.TicksPerSecond) return null;
        fight.FocusAt = ticks;
        if (bot.Map?.FindPlayer(fight.TargetId) is not { } current || current.Health.At(ticks) < current.Health.Max * 0.5) return null;
        var here = bot.PositionAt(bots.Time);
        return bot.Observers.Where(p => !p.IsBot && p.Id != current.Id && p.Health.At(ticks) < p.Health.Max * FinishHealth
                && Pvp.EdgeDistance(bot, here, p) <= range + 6 && !fight.Spared.ContainsKey(p.Character.Id) && Valid(bots, bot, p.Id, ticks) is not null)
            .OrderBy(p => p.Health.At(ticks) / Math.Max(1, p.Health.Max)).FirstOrDefault();
    }

    /// <summary>
    /// A ranged bot (a weapon of at least <see cref="RangedWeapon"/> m) with <paramref name="target"/> nearer than Bots:Combat:KiteDistance edge to
    /// edge steps back, at most every Bots:Combat:KiteEverySeconds: towards the walkable spot about its weapon's range away from the target with
    /// a way to it (<see cref="BotNav.PathFrom"/>, never through a wall), for Bots:Combat:KiteSeconds. True when it kites.
    /// </summary>
    private static bool Kite(BotManager bots, Player bot, ClusterNav nav, Player target, Vector2 here, double range, long ticks)
    {
        var fight = bot.Bot!.Fight;
        var options = bots.Options;
        if (options.KiteDistance <= 0 || range < RangedWeapon || bot.IsMounted || ticks - fight.KitedAt < (long)(options.KiteEverySeconds * TimeSpan.TicksPerSecond)
            || Pvp.EdgeDistance(bot, here, target) >= options.KiteDistance || SpellEffects.Rooted(bot, ticks)) return false;
        var from = target.PositionAt(DateTime.UtcNow);
        var away = here - from;
        var direction = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : new Vector2(0, 1);
        var step = (float)Math.Max(options.KiteDistance + 2, range * 0.75);
        IReadOnlyList<Vector2>? best = null;
        foreach (var turn in new[] { 0f, 0.6f, -0.6f, 1.2f, -1.2f })
        {
            var (sin, cos) = MathF.SinCos(turn);
            var d = new Vector2(direction.X * cos - direction.Y * sin, direction.X * sin + direction.Y * cos);
            if (nav.Grid.Nearest(here + d * step, 3) is not { } spot || Vector2.Distance(spot, from) <= Vector2.Distance(here, from) + 1) continue;
            if ((nav.Grid.Clear(here, spot) ? [here, spot] : BotNav.PathFrom(bots, nav, here, spot)) is { } path) { best = path; break; }
        }
        fight.KitedAt = ticks;
        if (best is null) return false;
        bots.Walk(bot, best, null);
        fight.KiteUntil = ticks + (long)(options.KiteSeconds * TimeSpan.TicksPerSecond);
        Log.Debug("bots", $"'{bot.Character.Name}' ({bot.Bot.PersonaId}) steps back from '{target.Character.Name}'");
        return true;
    }

    /// <summary>Whether a spell of <paramref name="spell"/>'s family (its name or a sub-spell of it) by <paramref name="bot"/> is still on it.</summary>
    private static bool OnItself(Player bot, SpellTemplate spell, long ticks) =>
        bot.Effects.Spells.Any(s => s.Caster == bot.Id && s.Live(ticks) && s.Spell.Name.StartsWith(spell.Name, StringComparison.Ordinal));

    /// <summary>
    /// The valid attacker (object id) that dealt at least Bots:Combat:RetargetShare × the current target's health damage within
    /// Bots:Combat:RetargetSeconds (the most of them), else null (SPEC §1.9).
    /// </summary>
    private static long? Retarget(BotManager bots, Player bot, long ticks)
    {
        var fight = bot.Bot!.Fight;
        var since = ticks - (long)(bots.Options.RetargetSeconds * TimeSpan.TicksPerSecond);
        var dealt = fight.Hits.Where(h => h.At >= since).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.Sum(h => h.Damage));
        var current = dealt.GetValueOrDefault(fight.TargetId);
        foreach (var (id, damage) in dealt.Where(d => d.Key != fight.TargetId).OrderByDescending(d => d.Value))
        {
            if (damage <= 0 || damage < bots.Options.RetargetShare * current) break;
            if (Valid(bots, bot, id, ticks) is not null) return id;
        }
        return null;
    }

    /// <summary>
    /// The player of object id <paramref name="id"/> when <paramref name="bot"/> may fight it now: a human on its map and in its view, not
    /// refused (<see cref="Pvp.Refusal"/>), not in a duel, within Bots:Patrol:LeashRadius of where the fight began, for a gatherer engaged
    /// (<see cref="BotPvp.Engaged"/>: it only answers, also where Pvp:Mode would let it start a fight); else null.
    /// </summary>
    private static Player? Valid(BotManager bots, Player bot, long id, long ticks)
    {
        if (bot.Map?.FindPlayer(id) is not { IsBot: false } p || !bot.Observers.Contains(p)) return null;
        if (Pvp.Refusal(bot, p, ticks) is not null || bot.World.Duels.OpponentOf(p) is not null) return null;
        if (bot.Bot!.Role == BotRole.Gatherer && !BotPvp.Engaged(bot, p)) return null;
        if (bot.Bot.Role is BotRole.Invader or BotRole.Hellgate) return p; // stageL: an invader's leash is the whole map (corrupted SPEC §4.5), a hellgate enemy's too
        var centre = bot.Bot!.Fight.EngagePoint ?? bot.PositionAt(bots.Time);
        return Vector2.Distance(p.PositionAt(DateTime.UtcNow), centre) <= bots.Options.LeashRadius ? p : null;
    }

    /// <summary>
    /// <paramref name="bot"/> knocked <paramref name="victim"/> down at <paramref name="ticks"/> (<see cref="Pvp.KnockedDown"/> after the
    /// knockdown's own events): the victim is no longer engaged nor the target of the bot or its group, which leave him alone for
    /// Bots:Patrol:SpareMinutes (no camping), and his journal counts a death by a bot (<see cref="PvpGoals.OnPvpDeath"/>). The bot gets no
    /// fame, goal or reputation.
    /// </summary>
    public static void KnockedDownPlayer(BotManager bots, Player bot, Player victim, long ticks)
    {
        if (bot.Bot is not { } info || victim.Map is not { } map) return;
        info.Group?.Engaged.Remove(victim.Id);
        foreach (var m in MembersOf(bot).Append(bot).Distinct())
        {
            var fight = m.Bot!.Fight;
            fight.Engaged.Remove(victim.Id);
            if (fight.TargetId == victim.Id) fight.TargetId = 0;
            Spare(m, victim, bots.Options.SpareMinutes * 60, ticks);
        }
        PvpGoals.OnPvpDeath(victim, map, byBot: true);
        Log.Info("bots", $"'{bot.Character.Name}' ({info.PersonaId}) knocked '{victim.Character.Name}' down");
    }

    // ---- patrols (phase 3) ----

    /// <summary>The members of <paramref name="bot"/>'s patrol group on its map and not defeated, or the bot alone.</summary>
    public static IEnumerable<Player> MembersOf(Player bot) =>
        bot.Bot?.Group is { } g ? g.Members.Where(m => m.Map is not null && m.Map == bot.Map && m.Bot is { Fight.Defeated: false }) : [bot];

    /// <summary>
    /// The player <paramref name="bot"/> (a patrol) attacks on sight (SPEC §1.9): the nearest human in its view within Bots:Patrol:AggroRadius
    /// it may fight now (<see cref="Valid"/>: the server's PvP rules, no duel, the leash in a fight), not spared, not riding unless
    /// Bots:Patrol:AttackMounted, not on the map for less than Bots:Patrol:JoinGraceSeconds, with a way to him (<see cref="Reachable"/>); null
    /// when none.
    /// </summary>
    public static Player? Scan(BotManager bots, Player bot, ClusterNav nav)
    {
        if (bot.Bot is not { Role: BotRole.Patrol or BotRole.Drifter, Fight.Defeated: false } info || bot.Combat.Down is not null || bot.Map is not { } map) return null;
        var options = bots.Options;
        var ticks = bot.World.GameNow;
        var here = bot.PositionAt(bots.Time);
        var utc = DateTime.UtcNow;
        var spared = info.Fight.Spared;
        foreach (var gone in spared.Where(s => s.Value <= ticks).Select(s => s.Key).ToList()) spared.Remove(gone);
        Player? best = null;
        var bestDistance = float.MaxValue;
        foreach (var p in bot.Observers)
        {
            if (p.IsBot) continue;
            var at = p.PositionAt(utc);
            var d = Vector2.Distance(here, at);
            if (d > options.AggroRadius || d >= bestDistance || Valid(bots, bot, p.Id, ticks) is null) continue;
            if (spared.ContainsKey(p.Character.Id) || (p.IsMounted && !options.AttackMounted) || !bots.Settled(map, p)) continue;
            if (!Reachable(bots, nav, here, at)) continue;
            (best, bestDistance) = (p, d);
        }
        if (best is null || !options.FocusWeakest) return best;
        // StageN botpower (Bots:Combat:FocusWeakest): of the players it may attack now, the one with the lowest health share (the nearest on a tie).
        var weakest = best;
        var weakestShare = Share(best, ticks);
        foreach (var p in bot.Observers)
        {
            if (p.IsBot || p == best || Share(p, ticks) >= weakestShare - 0.05) continue;
            var at = p.PositionAt(utc);
            if (Vector2.Distance(here, at) > options.AggroRadius || Valid(bots, bot, p.Id, ticks) is null) continue;
            if (spared.ContainsKey(p.Character.Id) || (p.IsMounted && !options.AttackMounted) || !bots.Settled(map, p) || !Reachable(bots, nav, here, at)) continue;
            (weakest, weakestShare) = (p, Share(p, ticks));
        }
        return weakest;
    }

    private static double Share(Player p, long ticks) => p.Health.At(ticks) / Math.Max(1, p.Health.Max);

    /// <summary>
    /// Whether a bot at <paramref name="from"/> can walk to <paramref name="to"/> (bots-world SPEC §2.6): a clear line of the walk grid, or a
    /// path at most <see cref="DetourFactor"/> × the straight distance (no aggro through a wall or across a cliff).
    /// </summary>
    public static bool Reachable(BotManager bots, ClusterNav nav, Vector2 from, Vector2 to)
    {
        if (nav.Grid.Clear(from, to)) return true;
        if (BotNav.PathFrom(bots, nav, from, to) is not { } path) return false;
        return path.Zip(path.Skip(1), Vector2.Distance).Sum() <= DetourFactor * Vector2.Distance(from, to) + 2;
    }

    /// <summary>
    /// <paramref name="bot"/>'s group takes <paramref name="target"/> on (SPEC §1.9): every member without a target that may fight him (the
    /// bot itself included) does so from where it stands, its first swing Bots:Combat:ReactionMs from now, and decides at the next tick.
    /// </summary>
    public static void Engage(BotManager bots, Player bot, Player target)
    {
        var world = bot.World;
        var ticks = world.GameNow;
        foreach (var m in MembersOf(bot).Append(bot).Distinct().ToList())
        {
            var info = m.Bot!;
            var fight = info.Fight;
            if (fight.Defeated || fight.TargetId != 0 || Valid(bots, m, target.Id, ticks) is null) continue;
            if (!fight.Active) fight.ReadyAt = ticks + bots.Options.ReactionMs * TimeSpan.TicksPerMillisecond;
            fight.TargetId = target.Id;
            fight.EngagePoint ??= m.PositionAt(bots.Time);
            bots.StopEmote(m);
            info.NextThink = world.Clock.Now;
            info.Brain.Attacked(m, target);
        }
        Log.Info("bots", $"'{bot.Character.Name}' ({bot.Bot!.PersonaId}) attacks '{target.Character.Name}' on sight");
    }

    /// <summary>A member of <paramref name="bot"/>'s group fights someone <paramref name="bot"/> may fight too: it joins in (true).</summary>
    public static bool GroupTarget(BotManager bots, Player bot)
    {
        if (bot.Bot is not { Fight.TargetId: 0 } || MemberTarget(bots, bot, bot.World.GameNow) is not { } id || bot.Map?.FindPlayer(id) is not { } target) return false;
        Engage(bots, bot, target);
        return bot.Bot.Fight.TargetId != 0;
    }

    /// <summary>The target of another member of <paramref name="bot"/>'s group that is valid for it (object id); null when none.</summary>
    private static long? MemberTarget(BotManager bots, Player bot, long ticks)
    {
        foreach (var m in MembersOf(bot))
            if (m != bot && m.Bot!.Fight.TargetId is var id and not 0 && Valid(bots, bot, id, ticks) is not null) return id;
        return null;
    }

    /// <summary>The player of object id <paramref name="id"/> when he stands beyond the leash of <paramref name="bot"/>'s fight; else null.</summary>
    private static Player? Leashed(BotManager bots, Player bot, long id) =>
        bot.Bot!.Fight.EngagePoint is { } centre && bot.Map?.FindPlayer(id) is { IsBot: false } p
        && Vector2.Distance(p.PositionAt(DateTime.UtcNow), centre) > bots.Options.LeashRadius ? p : null;

    /// <summary><paramref name="bot"/>'s on-sight scan leaves <paramref name="p"/> alone for <paramref name="seconds"/> from <paramref name="ticks"/>.</summary>
    private static void Spare(Player bot, Player p, double seconds, long ticks)
    {
        var spared = bot.Bot!.Fight.Spared;
        var until = ticks + (long)(seconds * TimeSpan.TicksPerSecond);
        spared[p.Character.Id] = Math.Max(spared.GetValueOrDefault(p.Character.Id), until);
    }

    /// <summary>
    /// <paramref name="bot"/> at 0 health by <paramref name="killer"/>'s hit at <paramref name="ticks"/> (the caller sent the lethal
    /// HealthUpdate; see the class; <paramref name="crime"/> judged before the hit), once: its show and emote end, it stops where it is, then
    /// a death (Died {0 position, 1 bot, 2 name, 5 flag, 9 killer, 10 name, 13 killer flag} to the map's humans, as <see cref="Death.Kill"/>
    /// sends it) in a playersarelethal zone with Bots:Defeat:DieInLethalZones, else a PvP knockdown (KnockedDown to its observers); the
    /// killer's rewards and PvP goals (<see cref="BotRewards.Grant"/>, with the attackers of the combat timeout taken first); its despawn is
    /// due 3 s or Bots:DownDespawnSeconds later.
    /// </summary>
    public static void Defeated(BotManager bots, Player bot, Player killer, bool crime, long ticks)
    {
        if (bot.Bot is not { } info || info.Fight.Defeated || bot.Map is not { } map) return;
        var world = bot.World;
        var fight = info.Fight;
        fight.Defeated = true;
        var attackers = Death.Attackers(bot); // now: the despawn's Combat.Leave clears its hits
        fight.End();
        bots.EndShow(bot, cancelled: true);
        bots.StopEmote(bot);
        info.Motion.Stop(bot, null, bots); // where it stands now on the bots' clock (the knockdown's own stop reads the UTC time)
        var death = Pvp.RulesOf(world, map).PlayersAreLethal && bots.Options.DieInLethalZones;
        if (death)
        {
            Knockdown.Die(bot, killer.Id, killer.Character.Name, ticks);
            var died = new DiedEvent
            {
                b21 = bot.PositionAt(bots.Time), b22 = bot.Id, b23 = bot.Character.Name, b3a = killer.Id, b3b = killer.Character.Name,
                Extra = new() { [5] = bot.Flag, [13] = killer.Flag },
            };
            foreach (var p in map.Players.Values.ToList()) p.Session.Send(died);
        }
        else Knockdown.Start(bot, killer.Id, killer.Character.Name, ticks, KnockdownKind.Pvp);
        // StageL corrupted (SPEC §4.5): a beaten invader gives the run's InvaderReward and the PvP goals, no fame, bag or silver.
        if (info.Role == BotRole.Invader) world.Corrupted.InvaderBeaten(bot, killer, attackers, death, crime, ticks);
        else bots.Rewards.Grant(bot, killer, attackers, death, crime, ticks);
        if (info.Role == BotRole.Hellgate) world.Hellgates.BotDefeated(bot); // stageL hellgates: the last of the enemy team wins objective 2
        var seconds = death ? Death.Settings(world).VisibilityAfterDeathSeconds : bots.Options.DownDespawnSeconds;
        fight.DespawnAt = world.Clock.Now + TimeSpan.FromSeconds(seconds);
        Log.Info("bots", string.Create(CultureInfo.InvariantCulture,
            $"'{bot.Character.Name}' ({info.PersonaId}) {(death ? "dies" : "is knocked down")} by '{killer.Character.Name}', gone in {seconds:0.#} s"));
    }
}
