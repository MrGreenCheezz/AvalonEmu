using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>RelationShipStatus (stageE pvp SPEC §0): what one unit is to another; only Hostile may be attacked.</summary>
public enum Relation : byte
{
    Friendly = 0,
    Neutral = 1,
    Hostile = 2,
}

/// <summary>
/// The shared base of player versus player (stageE pvp SPEC §1, sub-step E0, analysis/work/stageE/pvp/SPEC.md): the cluster's
/// rules (<see cref="ClusterPvp"/>, the archetype of worldsettings.xml), the client's hostility rule between two players
/// (<see cref="Of"/>, a5g.b 0x18090DDF0 with ctp.aee's duel opponent first), and a player's auto-attack on another player with the
/// PvP damage factors (<see cref="Swing"/>, <see cref="Damage"/>). The flag (FlaggingStatus) is the character's (saved), changed by
/// the flag cast of op 354 (<see cref="Flagging"/>, E2) or a GM's .pvp flag. The guild test G is one guild or one alliance (<see cref="Guilds.Friends"/>;
/// stageP hideouts SPEC §2.3, A.9: the client's a5g.d_1 tests both), and no unrestricted zones (§1.4: U false). E2 (§3.3-§3.5): a Hostile hit on a mounted player goes to its mount bar (at 0
/// the hostile forced dismount), the crimes cost reputation and implicate (<see cref="Crime"/>), a knockdown outside a duel wears the victim's items
/// (<see cref="KnockedDown"/>, AliveToKnockedOut) and gives the attacker KillRewardedNoFame. E3 (§4): in a playersarelethal cluster the
/// hit that takes the health to 0 kills (<see cref="Death.Kill"/>, AliveToDead). StageJ pvp-spells phase 2 (Pvp:Spells): a player's enemy
/// spells reach the players Hostile to it by the same <see cref="Refusal"/> (<see cref="SpellTargets.Of"/>); every hit on a player, a spell's,
/// an over-time tick's and an area's too, goes through <see cref="Damage"/>, and an effect without damage of its own (a stun, a slow, a
/// knockback) through <see cref="Engage"/>, which keeps the attack's bookkeeping in one place. PVE-DESIGN.md
/// §1: Pvp:Mode Consensual / Off narrow <see cref="Of(Player, Player)"/> (<see cref="ModeOf"/>), Pvp:LethalDeath false makes the lethal
/// zones knock down (<see cref="Lethal"/>), Pvp:KnockdownWear sets the knockdown's wear. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Pvp
{
    /// <summary>FlaggingStatus Blue (unflagged), Caerleon (6) and Red (255, hostile); 1-6 are the factions.</summary>
    public const byte Blue = 0, Caerleon = 6, Red = 255;

    /// <summary>
    /// The reputation level index of NEUTRAL (gamedata &lt;Reputation&gt;&lt;level&gt; in file order, SPEC §0): 0 points (a level left
    /// out, 0, would read as POSITIVE04). The level of a player's points is <see cref="Reputation.Level"/>.
    /// </summary>
    public const byte NeutralLevel = 4;

    /// <summary>A faction flag (bcj.b: 1-6).</summary>
    public static bool IsFaction(byte flag) => flag is >= 1 and <= 6;

    /// <summary>
    /// The PvP rules of <paramref name="map"/>: its cluster's archetype, <see cref="World.PvpFallback"/> before it is resolved
    /// (<see cref="World.PvpOverride"/> over both, the self-test's).
    /// </summary>
    public static ClusterPvp RulesOf(World world, Map? map) => world.PvpOverride ?? map?.Template?.Pvp ?? world.PvpFallback;

    /// <summary>
    /// Whether a killing PvP hit on <paramref name="map"/> kills (E3): its archetype's playersarelethal and Pvp:LethalDeath (the pve
    /// preset: false, the hit knocks down as in a yellow zone, and there is no execute and no die button).
    /// </summary>
    public static bool Lethal(World world, Map? map) => world.Config.PvpLethalDeath && RulesOf(world, map).PlayersAreLethal;

    /// <summary>B is in A's party (a5g.b_1: the same party; no party sets of other kinds yet).</summary>
    public static bool SameParty(Player a, Player b) =>
        a.World.Parties.Of(a.Character.Id) is { } party && a.World.Parties.Of(b.Character.Id) == party;

    /// <summary>
    /// What <paramref name="b"/> is to <paramref name="a"/> on a's map (ctp.aee → a5g.b): the duel opponent is Hostile before
    /// every other rule (in a city, a party, anywhere), oneself Friendly, everything else by <see cref="ModeOf"/> with Pvp:Mode
    /// (a Hostile guild mate Neutral under Guilds:NoFriendlyFire, a Hostile ally under Alliances:NoFriendlyFire).
    /// </summary>
    public static Relation Of(Player a, Player b)
    {
        if (a == b) return Relation.Friendly;
        if (a.World.Corrupted.Opponents(a, b)) return Relation.Hostile; // stageL corrupted: the two sides of an invasion, whatever else
        if (a.World.Hellgates.Rivals(a, b)) return Relation.Hostile; // stageL hellgates phase B: the two parties of a match, during its fight
        if (a.IsBot || b.IsBot) return Bots.BotPvp.Of(a, b); // stage I: a bot is never in a duel or a party
        if (a.World.Arenas.RelationOf(a, b) is { } arena) return arena; // stage N arena: the teams, Hostile only during a fight
        if (a.World.Duels.OpponentOf(a) == b) return Relation.Hostile;
        var guild = a.World.Guilds.Same(a.Character.Id, b.Character.Id);
        // StageP: G is one guild or one alliance (the client's a5g.d_1), so the server and the client agree on allies.
        var ally = !guild && a.World.Alliances.Allied(a.Character.Id, b.Character.Id);
        // StageL mists phase B (Pvp:Mode Official only): an active camp's free-for-all circle, guild mates kept apart under NoFriendlyFire.
        var relation = a.World.Mists.Unrestricted(a, b) ? Relation.Hostile : ModeOf(a.Flag, b.Flag, SameParty(a, b), guild || ally, RulesOf(a.World, a.Map), a.World.Config.PvpMode);
        // Guilds:NoFriendlyFire (guilds SPEC §10.2): guild mates are never Hostile (the table's case: two factions in one guild); allies under Alliances:NoFriendlyFire.
        var noFriendlyFire = guild && a.World.Config.GuildNoFriendlyFire || ally && a.World.Config.AllianceNoFriendlyFire;
        return relation == Relation.Hostile && noFriendlyFire ? Relation.Neutral : relation;
    }

    /// <summary>
    /// <see cref="Of(byte, byte, bool, bool, ClusterPvp)"/> under Pvp:Mode <paramref name="mode"/> (PVE-DESIGN.md §1). Official: the
    /// table as it is. Consensual: a black zone (PvpForced, None) read as a red one (PvpAllowed, its lethality kept), and Hostile only
    /// between two Red players, which the table makes Hostile outside one party; every other Hostile becomes Neutral, so nobody
    /// attacks a Blue or faction player, and with it no crime, reputation loss or outlaw is possible. Off: nothing Hostile (the
    /// duel opponent is <see cref="Of(Player, Player)"/>'s). The client still judges by its own table (a black zone shows a Blue
    /// player as an enemy and lets it press attack): the server's refusal is what it gets, as for a dead mob.
    /// </summary>
    public static Relation ModeOf(byte fa, byte fb, bool party, bool guild, ClusterPvp rules, PvpMode mode)
    {
        if (mode == PvpMode.Official) return Of(fa, fb, party, guild, rules);
        if (mode == PvpMode.Consensual && rules.Rule is PvpRule.PvpForced or PvpRule.None) rules = rules with { Rule = PvpRule.PvpAllowed };
        var relation = Of(fa, fb, party, guild, rules);
        if (relation != Relation.Hostile) return relation;
        return mode == PvpMode.Consensual && fa == Red && fb == Red ? Relation.Hostile : Relation.Neutral;
    }

    /// <summary>
    /// a5g.b's table (SPEC §1.2, verified §6 V2) for viewer flag <paramref name="fa"/> and target flag <paramref name="fb"/>, both
    /// players, outside unrestricted zones: <paramref name="party"/> = P, <paramref name="guild"/> = G (same guild or alliance).
    /// </summary>
    public static Relation Of(byte fa, byte fb, bool party, bool guild, ClusterPvp rules)
    {
        var caerleon = fa == Caerleon && fb == Caerleon;
        var sameFaction = IsFaction(fa) && fa == fb;
        switch (rules.Rule)
        {
            case PvpRule.PvpForced or PvpRule.None: // black zones: everyone but party, guild and alliance
                return party || guild ? Relation.Friendly : Relation.Hostile;
            case PvpRule.PvpDisabled: // cities, islands, hideouts
                return Relation.Friendly;
            case PvpRule.FactionPvpOnly: // green zones
                if (!IsFaction(fa)) return IsFaction(fb) ? Relation.Neutral : Relation.Friendly;
                if (caerleon) return party ? Relation.Friendly : Relation.Hostile;
                if (!IsFaction(fb) || sameFaction) return Relation.Friendly;
                return party ? Relation.Neutral : Relation.Hostile;
            default: // PvpAllowed: yellow, orange, red
                if (fa == Blue)
                {
                    if (fb == Blue) return Relation.Friendly;
                    if (IsFaction(fb)) return Relation.Neutral;
                    return party || guild ? Relation.Neutral : Relation.Hostile;
                }
                if (IsFaction(fa))
                {
                    if (caerleon) return party ? Relation.Friendly : Relation.Hostile;
                    if (fb == Blue || sameFaction) return Relation.Friendly;
                    if (party) return Relation.Neutral;
                    if (IsFaction(fb)) return Relation.Hostile;
                    return guild ? Relation.Neutral : rules.PlayersAreLethal ? Relation.Hostile : Relation.Neutral;
                }
                if (fa == Red)
                {
                    if (party || guild) return IsFaction(fb) ? Relation.Neutral : Relation.Friendly;
                    if (fb is Blue or Red) return Relation.Hostile;
                    return rules.PlayersAreLethal ? Relation.Hostile : Relation.Neutral;
                }
                return Relation.Friendly;
        }
    }

    /// <summary>
    /// Why <paramref name="attacker"/> may not attack <paramref name="target"/> now (SPEC §1.2, §1.3; null = it may): not Hostile,
    /// either knocked down (the execute is E3), the target invincible or not hittable, the attacker under cannotattackplayers, or the
    /// two in their duel's preparation (no damage before its start, §2.4).
    /// </summary>
    public static string? Refusal(Player attacker, Player target, long now)
    {
        if (attacker.Combat.Down is not null || target.Combat.Down is not null) return "knocked down";
        if (Of(attacker, target) != Relation.Hostile) return "not hostile";
        if (attacker.World.Duels.Preparing(attacker, target, now)) return "the duel has not started";
        if (attacker.Effects.Count > 0 && attacker.Effects.Has("cannotattackplayers", now)) return "cannot attack players";
        if (SpellEffects.Invincible(target, now) || SpellEffects.NotHittable(target, now)) return "invulnerable";
        // The get-up buff protects from players only by its invincibility: SPAWN_KNOCKDOWN (RED / BLACK, SPEC §1.1) has none (its
        // invisibility is to mobs). Without spell data the stand-in of SPAWN_KNOCKDOWN_INVULNERABILITY counts as one.
        if (target.Combat.InvulnerableUntil is not null && target.World.Spells?.Template(target.Combat.UpBuff) is null) return "invulnerable";
        return null;
    }

    /// <summary>Whether a spell named <paramref name="name"/> is on <paramref name="p"/> at <paramref name="now"/> (CANNOTDISABLEPVP, OUTLAW, …).</summary>
    public static bool SpellActive(Player p, string name, long now) =>
        p.Effects.Count > 0 && p.Effects.Spells.Any(s => s.Spell.Name == name && !s.Pending && s.Live(now));

    /// <summary>
    /// Puts spell <paramref name="name"/> on <paramref name="p"/> as its own (caster = the player, so it is saved over a relog like
    /// the food buff; again on it: restarted), its timed effects at ability power 100; <paramref name="send"/> sends the list and
    /// works the stats out. False without spell data or such a spell.
    /// </summary>
    public static bool ApplySpell(Player p, string name, long now, bool send = true)
    {
        var world = p.World;
        if (world.Spells?.Template(name) is not { } spell) return false;
        SpellEffects.Apply(world, p, spell, p.Id, 100, 100, now, [.. spell.Effects.Select((node, i) => SpellEffects.Make(world, p, 100, 100, node, i, p, now))]);
        if (send)
        {
            SpellEffects.Send(world, p);
            SpellEffects.Refresh(world, p);
        }
        return true;
    }

    /// <summary>
    /// Whether a Hostile hit of <paramref name="attacker"/> on <paramref name="target"/> is a crime (SPEC §3.4 [infer]): a Red attacker
    /// in a PvpAllowed cluster against a player who is not Red and not implicated, outside their duel. A Blue or faction player who
    /// hits back, faction fights and black zones commit none. Stage I (bots SPEC §1.8): a bot commits none (it has no reputation), and a
    /// hit on a bot is one only with Bots:Pvp:CrimesAgainstBots (official true, pve false).
    /// </summary>
    public static bool IsCrime(Player attacker, Player target, long now) =>
        !attacker.IsBot && (!target.IsBot || attacker.World.Config.Bots.CrimesAgainstBots)
        && attacker.Flag == Red && target.Flag != Red && !target.PvpState.Implicated(now)
        && RulesOf(attacker.World, attacker.Map).Rule == PvpRule.PvpAllowed && attacker.World.Duels.OpponentOf(attacker) != target;

    /// <summary>
    /// A crime of <paramref name="attacker"/> on <paramref name="target"/> (SPEC §3.4 [infer]): <paramref name="reason"/>
    /// <see cref="Reputation.LostAttack"/> costs the victim level's attackpenalty, at most once per victim in attacktimeoutseconds;
    /// <see cref="Reputation.LostKnockdown"/> its knockdownpenalty, <see cref="Reputation.LostKill"/> its killpenalty (E3). Each
    /// implicates the attacker (<see cref="Implicate"/>).
    /// </summary>
    public static void Crime(Player attacker, Player target, byte reason, long now)
    {
        var world = attacker.World;
        var level = Reputation.LevelData(target);
        if (reason == Reputation.LostAttack)
        {
            var clock = world.Clock.Now;
            var attacked = attacker.PvpState.Attacked;
            var timeout = TimeSpan.FromSeconds(Reputation.Settings(world).AttackTimeoutSeconds);
            foreach (var old in attacked.Where(x => clock - x.Value >= timeout).Select(x => x.Key).ToList()) attacked.Remove(old); // pruned
            if (!attacked.ContainsKey(target.Character.Id))
            {
                attacked[target.Character.Id] = clock;
                Reputation.Change(attacker, -level.AttackPenalty, reason, victim: target.Character.Name);
            }
        }
        else Reputation.Change(attacker, -(reason == Reputation.LostKill ? level.KillPenalty : level.KnockdownPenalty), reason, victim: target.Character.Name);
        Implicate(attacker, now);
    }

    /// <summary>OUTLAW's time without spell data (spells.xml dummy time="180").</summary>
    public const double OutlawSeconds = 180;

    /// <summary>
    /// <paramref name="p"/>'s crime implicates it until now + OUTLAW's time (180 s), where the cluster's outlawbuffenabled allows:
    /// the OUTLAW spell on it (its ActiveSpellEffectsUpdate; it stays through a knockdown) and ReputationImplicationUpdate (310) {0 me,
    /// 1 the end} to it and its observers (captured once with OUTLAW). Within a second of the last one nothing goes out again.
    /// Attacking it is no crime meanwhile.
    /// </summary>
    public static void Implicate(Player p, long now)
    {
        var world = p.World;
        if (!RulesOf(world, p.Map).OutlawBuffEnabled) return;
        var seconds = world.Spells?.Template("OUTLAW")?.Effects.Select(e => (double)SpellTemplate.Float(e, "time")).DefaultIfEmpty(0).Max() is > 0 and var t ? t : OutlawSeconds;
        var until = now + SpellCasts.Seconds(seconds);
        var state = p.PvpState;
        if (until - state.ImplicatedUntil < TimeSpan.TicksPerSecond) return;
        state.ImplicatedUntil = until;
        ApplySpell(p, "OUTLAW", now);
        p.SendAround(new ReputationImplicationUpdateEvent { ddn = p.Id, dvd = until });
    }

    /// <summary>
    /// At the Join (after the saved effects are back): <see cref="PlayerPvp.ImplicatedUntil"/> again from the OUTLAW spell still on
    /// <paramref name="p"/> at <paramref name="now"/>, so a cluster change or a relog keeps the implication its icon shows. The attack
    /// penalty times (<see cref="PlayerPvp.Attacked"/>) are not kept over it [infer: at most one more penalty per victim].
    /// </summary>
    public static void RestoreImplication(Player p, long now)
    {
        if (p.Effects.Count == 0) return;
        foreach (var s in p.Effects.Spells)
        {
            if (s.Spell.Name != "OUTLAW" || s.Pending || !s.Live(now)) continue;
            var end = s.Subs.Where(x => x.Active && x.DurationMs > 0).Select(s.EndOf).DefaultIfEmpty(0).Max();
            p.PvpState.ImplicatedUntil = Math.Max(p.PvpState.ImplicatedUntil, end);
        }
    }

    /// <summary>
    /// <paramref name="target"/> is knocked down by <paramref name="attacker"/> outside a duel (SPEC §3.5; the knockdown's own events
    /// went out): every equipped and inventory item with durability loses AliveToKnockedOut's share of its maximum (5 %, at least 1 %
    /// of it left; an item below that keeps what it has; rewardtoken nothing; Pvp:KnockdownWear instead when set, the pve preset's 0
    /// none), each with its NewEquipmentItem to the victim (<see cref="WearAll"/>); the
    /// attacker gets KillRewardedNoFame (615) {0 attacker} [infer: no PvP fame for a knockdown] and, for a crime
    /// (<paramref name="crime"/>, judged before the hit), the knockdown penalty. Not modelled (TODO, SPEC §3.5 [infer]): the silver
    /// drop (its amount is unproven) and the trade packs that drop on a PvP knockdown (droponpvpknockdown, a loot bag of E3).
    /// Only outside playersarelethal clusters: there the hit kills (E3).
    /// </summary>
    public static void KnockedDown(Player attacker, Player target, bool crime, long ticks)
    {
        var world = target.World;
        var gamedata = world.GameData?.KnockedOutLoss ?? DurabilityLossRule.KnockedOut;
        if (world.Config.PvpKnockdownWear is not { } share) WearAll(target, gamedata, ticks);
        else if (share > 0) WearAll(target, gamedata with { MinFactor = share, MaxFactor = share }, ticks);
        if (attacker.IsBot)
        {
            // Stage I (bots SPEC §1.9): a bot's knockdown of a player feeds no goal and no reward of the bot; it (and its patrol group)
            // drops the victim as its target and spares him for Bots:Patrol:SpareMinutes.
            world.Bots.KnockedDownPlayer(attacker, target, ticks);
            world.Corrupted.Defeated(attacker, target); // stageL corrupted: a bot invader beat the owner
            return;
        }
        // Stage I (pvp-goals SPEC §2.1, §2.4): the PvP goals of the knockdown; with Pvp:KnockdownFameFactor a credited one gives that share
        // of the kill fame instead of KillRewardedNoFame.
        var attackers = Death.Attackers(target);
        var fame = Death.KillFame(target);
        var kill = new PvpKill(PvpActor.Of(attacker), PvpActor.Of(target), [.. attackers.Select(a => (PvpActor.Of(a.Player), a.Damage))], target.Map!,
            PvpOutcome.Knockdown, crime, fame, ticks, Math.Max(1, attackers.Count));
        var goals = world.Goals.Pvp;
        long mine = 0;
        // Each sharer's fame only when its own credit would not be refused (the same account, the same victim again, ...).
        if (goals is { Enabled: true, KnockdownFameFactor: > 0 } && PvpGoals.Refusal(world, kill, attacker) is null)
            kill = kill with
            {
                FameShares = Death.KnockdownFame(attacker, target, (long)Math.Round(fame * goals.KnockdownFameFactor), attackers,
                    p => PvpGoals.Refusal(world, kill, p) is null, out mine),
            };
        if (mine <= 0) attacker.Session.Send(new KillRewardedNoFameEvent { ddn = attacker.Id });
        if (crime) Crime(attacker, target, Reputation.LostKnockdown, ticks);
        PvpGoals.OnPvpKill(world, kill);
        world.Corrupted.Defeated(attacker, target); // stageL corrupted: one side of an invasion beat the other (after its goals)
        // HitBy stays while it is down: an execute's death (Death.Kill) shares its fame and wear by these attackers. The get-up clears it,
        // so a second knockdown shares only its own damage.
    }

    /// <summary>
    /// A knockdown's wear: every equipped and inventory item of <paramref name="p"/> with durability loses <paramref name="rule"/>'s
    /// share (<see cref="Death.Wear"/>), each changed one with its NewEquipmentItem to it; then saved and the stats worked out again.
    /// The PvP knockdown's (<see cref="KnockedDown"/>) and the mob knockdown's of Pve:KnockdownWear (Combat).
    /// </summary>
    public static void WearAll(Player p, DurabilityLossRule rule, long ticks)
    {
        var random = p.Map?.Random ?? Random.Shared;
        var worn = false;
        foreach (var item in p.Inventory.Equipment.Items.Concat(p.Inventory.Bag.Items).ToList())
        {
            if (!Death.Wear(item, rule, 1, random)) continue;
            Network.WorldSession.SendItemTo(p, item);
            worn = true;
        }
        if (worn)
        {
            p.SaveItems();
            p.RecalculateStats(ticks, keep: true);
        }
    }

    /// <summary>Distance between the edges of two players (centre distance less both collision radii, not below 0).</summary>
    public static float EdgeDistance(Player p, Vector2 at, Player target) =>
        Math.Max(0, Vector2.Distance(at, target.PositionAt(DateTime.UtcNow)) - 2 * p.World.CollisionRadius);

    /// <summary>
    /// The PvP-only factor on damage that reached <paramref name="target"/> (SPEC §3.3 [infer: the client never computes PvP
    /// damage]): × 1 / (1 + Σ bonusdefensevsplayers) of its buffs and its worn items (<see cref="Player.Defense"/>: a shield's
    /// value × 1.16^(IP/100), stageJ armour SPEC §2.2; T4 shield at IP 700 0.0305), the shape of
    /// <see cref="SpellEffects.TakenFactor"/> that mob hits use too (stageD effects SPEC §9 Q5: the client shows the attribute as
    /// 100 − 100 / (1 + v)), not the SPEC's unproven (1 − Σ). The attacker's bonusdamagevsplayers is in its dealt factor
    /// (<see cref="SpellEffects.DealtFactor"/> with vsPlayer).
    /// </summary>
    public static double DefenseFactor(Player target, long now) =>
        SpellEffects.TakenFactor(target, fromPlayer: true, now, target.Defense.DefenseVsPlayers);

    /// <summary>
    /// An accepted AttackStart of <paramref name="p"/> on player <paramref name="target"/> (the mob swing of
    /// <see cref="SpellCasts.Swing"/> with a player as the target): Attack (13) to the attacker's observers, the attacker in combat,
    /// and at the impact <see cref="Damage"/> with the weapon's damage × bonusdamagevsplayers × the combo factor × the target's
    /// armor or magic resistance (its worn items and buffs, <see cref="Combat.ResistanceOf(Player, EffectType)"/>).
    /// </summary>
    public static void Swing(Player p, Player target, byte combo, long start)
    {
        var world = p.World;
        var map = p.Map!;
        var weapon = Combat.WeaponOf(p);
        var damageTime = (world.GameData?.AttackDamageTimes.GetValueOrDefault(weapon.Type) ?? 0.25) * weapon.DamageTimeFactor;
        var damageAt = start + SpellCasts.Seconds(damageTime);
        var utc = DateTime.UtcNow;
        var flight = weapon.Type == "melee" ? 0 : Vector2.Distance(p.PositionAt(utc), target.PositionAt(utc)) / world.Combat.AutoAttackProjectileSpeed;
        var impact = damageAt + SpellCasts.Seconds(flight);
        p.Combat.LastSwing = start;
        p.SendToObservers(new AttackEvent
        {
            ddn = p.Id, bs0 = start, bs1 = target.Id, bs2 = weapon.AttackType, bs3 = damageAt, bs4 = impact, Extra = new() { [6] = combo },
        });
        Combat.EnterCombat(p, null);
        SpellEffects.Trigger(p, "AutoAttack", target, start);
        var raw = (weapon.Progression is { } prog ? Combat.AutoAttack(weapon.Damage, prog.AttackDamage, weapon.ItemPower) : weapon.Damage)
            * SpellEffects.DealtFactor(p, spell: false, weapon.Effect, vsPlayer: true, world.GameNow);
        var factor = world.Combat.Combo(weapon.Item?.Template.Name, combo);
        SpellCasts.At(world, map, impact, () =>
        {
            if (p.Map != map || p.Combat.Down is not null) return; // knocked down meanwhile: the swing is lost
            Damage(p, target, raw * factor * Combat.ResistanceOf(target, weapon.Effect), weapon.Effect, weapon.Origin, -1, impact);
        });
    }

    /// <summary>
    /// The bookkeeping of an attack of player <paramref name="attacker"/> on player <paramref name="target"/> at <paramref name="ticks"/>
    /// (stageJ pvp-spells SPEC §2.5, D6), the part before the hit that <see cref="Damage"/> runs and that a Hostile spell effect without
    /// damage of its own runs once per hit and target (a stun, a slow, a knockback: <see cref="SpellCasts.Execute"/>): a bot target answers
    /// (stage I, <see cref="Bots.BotManager.Attacked"/>), a non-duelist on a duelist ends that duel (OutsiderInterruptedDuel, SPEC §2.5
    /// [infer]), both are in combat (the target hit), an attack (not a reflected hit or an over-time tick) breaks the attacker's
    /// attackingbreakseffect effects, the target's flag cast and execute end (<see cref="Flagging.Interrupt"/>, damagecancelscasting;
    /// stageJ: any attack [inference]), and a crime (<see cref="IsCrime"/>, judged before the effect; never a reflected hit) costs the
    /// attack penalty (<see cref="Crime"/>). Returns the crime flag. The caller has checked <see cref="Refusal"/>.
    /// </summary>
    public static bool Engage(Player attacker, Player target, long ticks, HitOrigin origin = HitOrigin.Direct)
    {
        var world = attacker.World;
        if (target.IsBot) world.Bots.Attacked(target, attacker, ticks); // stage I: the bot answers (also a hit on its mount bar, a stun)
        world.Duels.Damaged(attacker, target);
        var crime = origin != HitOrigin.Reflected && IsCrime(attacker, target, ticks);
        Combat.EnterCombat(attacker, null);
        Combat.EnterCombat(target, null, hit: true);
        if (origin is not (HitOrigin.Reflected or HitOrigin.OverTime)) SpellEffects.Break(world, attacker, "attacking", ticks);
        Flagging.Interrupt(target, "attacked"); // damagecancelscasting (SPEC §3.1), the execute too
        if (crime) Crime(attacker, target, Reputation.LostAttack, ticks);
        return crime;
    }

    /// <summary>
    /// <paramref name="amount"/> damage (resisted; × <see cref="DefenseFactor"/> and rounded here) of player <paramref name="attacker"/>
    /// on player <paramref name="target"/> at <paramref name="ticks"/>. Checked again at the impact (<see cref="Refusal"/>: a duel
    /// ended or a flag changed meanwhile drops the hit). The attack's bookkeeping is <see cref="Engage"/>'s (the bot's answer, the duel an
    /// outsider ends, combat, the target's flag cast or execute ended, a crime judged before the hit). A damagecancelscasting channel of
    /// the target ends; the hit goes to its damage shields (its reflectdamageactive answers, unless <paramref name="reflectable"/> is false: a
    /// spell node's reflectdamage="never"). A spell's <paramref name="hit"/> (CastHit 21, stageJ pvp-spells SPEC §2.10) goes to the target and
    /// its observers right before its HealthUpdate (alone when all goes to a shield or the mount bar). A mounted target's mount bar takes the hit
    /// (<see cref="Mounts.Damage"/>, hostile: at 0 FORCE_DISMOUNTED_HOSTILE and Mounted key 13, SPEC §3.3 [infer]). The HealthUpdate {2 -damage,
    /// 3 left, 4 type, 5 origin, 6 the attacker, 7 spell} to the target and its observers, as against mobs. At 0 the target is knocked
    /// down by the attacker (KnockedDown key 3 its name, no "@"): its duel opponent's knockdown is the duel's (<see cref="Duels"/>:
    /// DuelEnded and the post's Leave before this lethal HealthUpdate, as captured), any other one the PvP knockdown
    /// (<see cref="KnockdownKind.Pvp"/>, then <see cref="KnockedDown"/>: the items' wear, KillRewardedNoFame, the knockdown penalty);
    /// in a playersarelethal cluster the death instead (E3, <see cref="Death.Kill"/>: AliveToDead, a crime's killpenalty). Every hit on the
    /// health is kept for the death's attackers (<see cref="Death.Hit"/>).
    /// </summary>
    public static void Damage(Player attacker, Player target, double amount, EffectType type, HitOrigin origin, short spell, long ticks,
        AlbionEvent? hit = null, bool reflectable = true)
    {
        if (target.Map is not { } map || attacker.Map != map) return;
        var world = attacker.World;
        if (Refusal(attacker, target, ticks) is { } why)
        {
            Log.Debug("combat", $"'{attacker.Character.Name}' hit on '{target.Character.Name}' dropped: {why}");
            return;
        }
        var before = target.Health.At(ticks);
        if (before <= 0) return;
        var crime = Engage(attacker, target, ticks, origin); // judged before the hit (a knockdown, a flag change)
        if (attacker.IsBot) amount *= world.Config.Bots.DamageFactor * world.Config.Bots.Difficulty * attacker.Bot!.DamageFactor; // stageN botpower: Bots:Difficulty // stage I: Bots:Combat:DamageFactor, the bots' difficulty (stageL: an invader's own)
        amount *= PvpDetails.Resilience(attacker, target, ticks); // stageP: resilience (focus fire protection) under Pvp:Resilience
        var damage = Math.Max(0, Combat.Rounded(amount * DefenseFactor(target, ticks)));
        if (target.Combat.Channel is { Info.DamageCancels: true } && !SpellEffects.NotInterruptible(target, ticks))
            SpellCasts.EndChannel(target, SpellCasts.ChannelEnd.Interrupted);
        if (hit is not null) target.SendAround(hit); // a spell's CastHit before its HealthUpdate, as against mobs
        if (target.IsMounted)
        {
            // The mount bar takes the hit, as from a mob; at 0 the hostile forced dismount (SPEC §3.3 [infer], see Mounts.Died).
            Mounts.Damage(target, attacker.Id, damage, type, origin, spell, ticks, hostile: true);
            return;
        }
        var raw = damage;
        damage = Combat.Rounded(SpellEffects.Absorb(world, target, damage, ticks));
        var health = target.Health.At(ticks);
        var left = (float)SpellEffects.Clamp(world, target, health, Math.Max(0, health - damage), ticks);
        if (left > Math.Max(0, health - damage)) damage = health - left;
        if (damage > 0) Mounts.Cancel(target, "damage");
        HealthUpdateEvent? update = null;
        if (damage > 0 || raw <= 0)
        {
            target.Health.Set(left, Math.Max(ticks, target.Health.Stamp));
            if (damage > 0) SpellEffects.Damaged(target, type, ticks);
            update = Combat.HealthUpdate(target.Id, ticks, -damage, left, type, origin, attacker.Id, spell);
            Death.Hit(attacker, target, damage);
            DeathRecaps.Hit(target, attacker, spell, origin, -damage, ticks); // stageP: the death recap's player rows
            if (damage > 0) world.Arenas.Damaged(target); // stageP: a hit breaks an arena orb channel
            if (target.IsBot) world.Bots.Hurt(target, attacker, damage, ticks); // stage I: the bot's retarget measure
        }
        if (raw > 0) SpellEffects.EnergyOnDamage(target, ticks);
        SpellEffects.Break(world, target, "damage", ticks);
        if (left > 0 || update is null)
        {
            if (update is not null) target.SendAround(update);
            // reflectdamageactive, not back and forth, nor a reflectdamage="never" spell hit
            if (origin != HitOrigin.Reflected && reflectable) SpellEffects.Reflect(target, attacker, raw, ticks);
            SpellEffects.Trigger(target, "ReceiveDamage", attacker, ticks);
            if (origin != HitOrigin.OverTime) SpellEffects.Trigger(target, "ReceiveDirectDamage", attacker, ticks);
        }
        // Stage N arena (SPEC §4.3): a defeat in an arena match is the arena's alone: no loot, wear or death, a ticket of the team.
        else if (target.IsBot ? world.Arenas.BotDown(attacker, target, update) : world.Duels.OpponentOf(target) != attacker && world.Arenas.Down(attacker, target, update, ticks)) { }
        else if (target.IsBot)
        {
            // Stage I (bots SPEC §1.10): a bot's defeat is the bot manager's alone (its knockdown or death show, the despawn, the slot's respawn).
            target.SendAround(update);
            world.Bots.Defeated(target, attacker, crime, ticks);
        }
        else
        {
            var duel = world.Duels.OpponentOf(target) == attacker;
            if (!duel) target.SendAround(update); // the PvE order: the lethal HealthUpdate, then the knockdown
            // E3 (SPEC §4.1): a lethal cluster's lethal hit kills, no knockdown (not with Pvp:LethalDeath false); a bot's hit knocks down
            // (stage I) unless Bots:Pvp:LethalToPlayers.
            if (!duel && Lethal(world, map) && (!attacker.IsBot || world.Config.Bots.LethalToPlayers))
                Death.Kill(target, attacker, attacker.Id, attacker.Character.Name, Death.Cause.AliveToDead, crime, ticks);
            else
            {
                Knockdown.Start(target, attacker.Id, attacker.Character.Name, ticks, duel ? KnockdownKind.Duel : KnockdownKind.Pvp);
                if (duel) target.SendAround(update); // a duel's: last, after DuelEnded and the post's Leave (SPEC §2.5 step 6)
                else KnockedDown(attacker, target, crime, ticks);
            }
        }
        // The attacker's triggers and healonhit, as against mobs (Combat.Damage): after the hit, the knocking-down one too.
        SpellEffects.Trigger(attacker, "DoDamage", target, ticks);
        if (origin != HitOrigin.OverTime) SpellEffects.Trigger(attacker, "DoDirectDamage", target, ticks);
        Passives.OnHit(attacker, raw, origin, ticks);
    }
}
