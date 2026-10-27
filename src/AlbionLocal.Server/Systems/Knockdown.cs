using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The player's PvE knockdown and get-up (stage 5e, analysis/work/stage5/SPEC.md §5e): mobs do not kill a player, they knock
/// it down (gamedata DeathSettings), and it stands up again where it fell. No Died (164), no Respawn operation and no
/// respawn point: those belong to the real death of PvP and executions, out of scope for stage 5 and never captured.
/// <list type="number">
/// <item><b>Down</b> (<see cref="Start"/>, both captures in this order): the lethal HealthUpdate without key 3 (the
/// caller's), RegenerationHealthChanged with the knockdown regeneration (max × knockdownhealthregentopercent /
/// knockdowngetuptimeseconds_pve, 2071 / 600 = 3.4517 captured) and RegenerationPlayerComboChanged with the same rate,
/// InCombatStateUpdate {0 me} (combat cleared), KnockedDown (165) {0 me, 1 ts, 2 the mob, 3 "@" + its namelocatag, 4 me,
/// 5 the own name} to the player and its observers; every mob drops it from its threat list, and a mob with no threat left
/// resets in its next tick (SPEC §5d.3).</item>
/// <item><b>Combat over</b> (<see cref="Update"/>: no living mob has it in its threat list or as its target): the
/// regeneration becomes max / knockdowngetuptimeseconds_combatover (18 s; 2071 / 18 = 115.06 captured), again with
/// RegenerationHealthChanged and RegenerationPlayerComboChanged.</item>
/// <item><b>Get up</b> (<see cref="GetUp"/>) 18 s after the combat ended (both captures: 18.1 s after KnockedDown), 600 s
/// after the knockdown at the latest: Respawn (88) {0 me, 1 ts, 2 the same spot, 3 facing, 4/5 health = max, 7 regeneration,
/// 8 ts, 9/10 energy = max, 12 regeneration, 13 ts, 14 true, 15 true (the get-up animation)} to the player and its
/// observers, then RegenerationHealthChanged with the regular rate. Health is max (official: max − 1 after the ramp).</item>
/// <item><b>Invulnerability</b> getuptimeseconds (3 s) later: CastSpell {0 me, 1 me, 3 995, 6 0} and ActiveSpellEffectsUpdate
/// with SPAWN_KNOCKDOWN_INVULNERABILITY (995, as captured; gamedata's knockdownbuffaftergetup names SPAWN_KNOCKDOWN, which
/// has no invincibility). Walking preserves it; AttackStart, CastStart or the spell's
/// 15 s end it, with the empty list. An action before it comes does not stop it (<see cref="Combat.EffectsCleared"/>).</item>
/// </list>
/// While down, Move gets rc 881 (the official code), AttackStart and CastStart are refused, heals do nothing and no mob
/// aggroes or hits it; while invulnerable mobs neither aggro nor hit it. Nothing of it is saved: a player who leaves while
/// down joins again standing at the knockdown spot with full bars (SPEC §5e.3 step 4). Not modelled: the silver drop
/// (NewSilverObject {3 player, 2 amount, 18 3}: there is no money yet),
/// durability loss (the SPEC defines none for a PvE knockdown). The death recap is frozen here (stageP, <see cref="DeathRecaps"/>). Touched only under <see cref="World.Sync"/>.
/// <para>StageE (analysis/work/stageE/pvp/SPEC.md §2.5, §2.6, §3.5): a <see cref="KnockdownKind"/> picks the get-up. A duel's
/// (<see cref="KnockdownKind.Duel"/>): knockdowngetuptimeseconds_duel (6 s) with max / 6 as the regeneration, the Respawn keeps the
/// energy as it is (captured 214/340, 162/265, 110/265) and in the same instant CastSpell DUEL_SPAWN_KNOCKDOWN (996) runs on the
/// player (its heal over time and cleanse); no item or reputation change. A PvP knockdown outside a duel
/// (<see cref="KnockdownKind.Pvp"/>): the archetype's knockdowngetuptimeseconds_pvp, else DeathSettings' (180 s), without the
/// combat-over shortcut, then the archetype's knockdownbuffaftergetup (else DeathSettings' SPAWN_KNOCKDOWN) getuptimeseconds later;
/// the item wear, KillRewardedNoFame and the reputation of SPEC §3.5 follow in <see cref="Pvp.KnockedDown"/> (E2; the silver drop is
/// TODO). Every knockdown of a duelist ends its duel (<see cref="Duels.KnockedDown"/>); a flag cast under way ends (<see cref="Flagging"/>).</para>
/// <para>StageE E3 (SPEC §4): a death in a lethal cluster is a knockdown of kind <see cref="KnockdownKind.Dead"/> (<see cref="Die"/>):
/// the same clean-up, but no KnockedDown event and no regeneration, the spells without persistsafterdeath end (OUTLAW), and it ends by
/// the respawn of <see cref="Death"/>, not by a get-up.</para>
/// </summary>
public static class Knockdown
{
    /// <summary>The ReturnCode of a Move while knocked down (the official server's).</summary>
    public const short MoveRefused = 881;

    /// <summary>The get-up buff the captures show (spell 995).</summary>
    public const string BuffSpell = "SPAWN_KNOCKDOWN_INVULNERABILITY";

    /// <summary>Its id in 345319, for a world without spell data.</summary>
    public const short DefaultBuffId = 995;

    /// <summary>The buff's time without spell data (spells.xml: time="15" on each of its effects).</summary>
    public const double DefaultBuffSeconds = 15;

    private static DeathSettings Settings(World world) => world.GameData?.Death ?? DeathSettings.Default;

    /// <summary>
    /// <paramref name="p"/>, at 0 health from the HealthUpdate the caller has sent, goes down at GameTimeStamp
    /// <paramref name="ticks"/>: <paramref name="killer"/> is KnockedDown key 2 (the mob's object id; the player's own for a
    /// GM .kill) and <paramref name="killerText"/> key 3. A cast under way is interrupted, a harvest cancelled, a walk stopped
    /// where it has got to. Nothing when it is down already or off the map. <see cref="KnockdownKind.Dead"/>: see <see cref="Die"/>.
    /// </summary>
    public static void Start(Player p, long killer, string killerText, long ticks, KnockdownKind kind = KnockdownKind.Pve)
    {
        if (p.Combat.Down is not null || p.Map is not { } map) return;
        var world = p.World;
        var c = p.Combat;
        var togglesOff = SpellCasts.EndToggles(p, SpellCasts.ToggleEnd.KnockedDown, send: false); // its toggles end (their list goes with step 6)
        Mounts.KnockedDown(p, ticks); // a mount under way ends, a rider is thrown off, the mount object leaves (stageD mounts SPEC §4.4)
        Emotes.Stop(p, "knocked down"); // stageN qol: a looping emote ends for the observers
        var dead = kind == KnockdownKind.Dead;
        c.Down = new KnockedDownState(world.Now, killer, kind) { KillerText = killerText };
        world.Recaps.Freeze(p, ticks); // stageP: the death recap of this knockdown (or death)
        c.BuffDue = null;
        c.InvulnerableUntil = null;
        if (c.Cast is { } cast) SpellCasts.Cancel(p, cast, interrupted: true);
        Flagging.Interrupt(p, "knocked down"); // stageE: a flag cast under way
        SpellCasts.EndChannel(p, SpellCasts.ChannelEnd.Interrupted); // stageD: its channel and areas end with it
        SpellAreas.OwnerGone(p);
        Summons.SummonerGone(world, map, p); // its summons go (ttlaftercasterdeath: SUMMON_FOX "0", at once)
        if (p.Harvest is not null) p.Session.CancelHarvest(p, "knocked down", tellClient: true);
        Fishing.End(p, "knocked down"); // stageG fishing SPEC §4
        var utc = DateTime.UtcNow;
        if (p.Movement is { } walk && walk.PositionAt(utc) != walk.Target)
        {
            var at = walk.PositionAt(utc);
            p.Move(at, at, 0, p.Facing, 0, utc); // a stop (event 3, pos = target) to the observers
        }
        p.StorePosition(utc);
        // 2. The knockdown regeneration: to knockdownhealthregentopercent of the maximum over knockdowngetuptimeseconds_pve.
        var now = Math.Max(ticks, p.Health.Stamp);
        //    Its own spells end first (sent with step 6), so the maxima are the unbuffed ones.
        //    A spell with persistsafterknockdown="true" stays (the food buffs, stageD consumables SPEC §4.2).
        var keep = dead ? "persistsafterdeath" : "persistsafterknockdown"; // E3: a death ends OUTLAW (persistsafterdeath false)
        var hadEffects = SpellEffects.Remove(world, p, s => !s.Subs.Any(sub => SpellTemplate.Bool(sub.Node, keep))) | togglesOff;
        p.Effects.Forced = null;
        Consumables.Abort(p, "knocked down");
        if (hadEffects)
        {
            SpellEffects.ShieldCheck(p, now); // a damage shield ends with them: the bare DamageShieldUpdate
            p.RecalculateStats(now, keep: true);
        }
        p.Health.Set(0, now);
        p.Health.RegenPerSecond = Regeneration(p, p.Health.Max);
        SendRegeneration(p, now);
        // 3. Combat cleared: InCombatStateUpdate {0 me}.
        c.InCombat = false;
        c.Hit = false;
        c.Enemies.Clear();
        p.Session.Send(new InCombatStateUpdateEvent { ddn = p.Id });
        // 5. KnockedDown to the player and its observers (not for a death: Died follows, Death.Kill).
        if (!dead && !Revive.Begin(p, c.Down, ticks)) // stageN: the bleed-out (BleedOut 693) of a player with an ally on the map
        {
            var down = new KnockedDownEvent { ddn = p.Id, cmr = ticks, cms = killer, cmt = killerText, cmu = p.Id, cmv = p.Character.Name };
            p.Session.Send(down);
            p.SendToObservers(down);
        }
        // 6. Every mob drops it; one without threat left resets in its next tick. Its own spells on mobs end too (no more
        //    ticks that would bring it back into combat while down; their lists go out at once), and its own buffs.
        AI.MobAI.Forget(map, p);
        foreach (var mob in map.Objects.Values.OfType<Mob>().ToList())
        {
            if (mob.TargetId == p.Id) mob.TargetId = 0;
            if (!mob.Dead && SpellEffects.Remove(world, mob, s => s.Caster == p.Id))
            {
                SpellEffects.Send(world, mob);
                SpellEffects.Refresh(world, mob);
            }
        }
        if (hadEffects) SpellEffects.Send(world, p); // the list without them (empty: the death clear's layout)
        world.Duels.KnockedDown(p, killer); // stageE: a duelist's knockdown ends its duel (DuelEnded, the post's Leave)
        world.Trades.KnockedDown(p); // stageJ: and its trade
        Log.Info("combat", $"'{p.Character.Name}' {(dead ? "dies" : "is knocked down")} by {(killer == p.Id ? "itself" : map.Objects.GetValueOrDefault(killer)?.ToString() ?? $"object {killer}")}");
    }

    /// <summary>
    /// <paramref name="p"/> dies at <paramref name="ticks"/> (E3, <see cref="Death.Kill"/>) by <paramref name="killer"/> (Died key 9,
    /// <paramref name="killerText"/> key 10): standing, the clean-up of <see cref="Start"/> with <see cref="KnockdownKind.Dead"/>; already
    /// knocked down (an execute, the die button), its knockdown becomes the death: health 0 without regeneration
    /// (RegenerationHealthChanged and RegenerationPlayerComboChanged), the spells without persistsafterdeath end.
    /// </summary>
    internal static void Die(Player p, long killer, string killerText, long ticks)
    {
        var c = p.Combat;
        if (c.Down is null)
        {
            Start(p, killer, killerText, ticks, KnockdownKind.Dead);
            return;
        }
        var world = p.World;
        c.Down = new KnockedDownState(world.Now, killer, KnockdownKind.Dead) { KillerText = killerText };
        world.Recaps.Freeze(p, ticks); // stageP: the recap again, with the killing blow
        var now = Math.Max(ticks, p.Health.Stamp);
        if (SpellEffects.Remove(world, p, s => !s.Subs.Any(sub => SpellTemplate.Bool(sub.Node, "persistsafterdeath"))))
        {
            p.RecalculateStats(now, keep: true);
            SpellEffects.Send(world, p);
        }
        p.Health.Set(0, now);
        p.Health.RegenPerSecond = 0;
        SendRegeneration(p, now);
        Log.Info("combat", $"'{p.Character.Name}' dies where it lay");
    }

    /// <summary>
    /// <paramref name="amount"/> damage that no mob deals (GM .damage, .kill) on <paramref name="p"/> now: the HealthUpdate
    /// {2 -damage, 3 left (none at 0), 4 1 physical, 5 2 direct, 6 the player, 7 -1} to it and its observers, at 0 the
    /// knockdown with the player as the causer. No combat. Nothing while it is down.
    /// </summary>
    public static void DamageSelf(Player p, float amount)
    {
        if (p.Combat.Down is not null || p.Map is null) return;
        var now = Math.Max(p.World.GameNow, p.Health.Stamp);
        var damage = Math.Max(0, Combat.Rounded(amount));
        var left = Math.Max(0, p.Health.At(now) - damage);
        p.Health.Set(left, now);
        var e = Combat.HealthUpdate(p.Id, now, -damage, left, EffectType.Physical, HitOrigin.Direct, p.Id, -1);
        p.Session.Send(e);
        p.SendToObservers(e);
        if (left <= 0) Start(p, p.Id, p.Character.Name, now);
    }

    /// <summary>
    /// GM .heal: a knocked-down player gets up at once (<see cref="GetUp"/>); a standing one gets full health and energy
    /// (RegenerationPlayerComboChanged to it and its observers).
    /// </summary>
    public static void Heal(Player p)
    {
        if (p.Combat.Down is not null)
        {
            GetUp(p);
            return;
        }
        var now = Math.Max(p.World.GameNow, Math.Max(p.Health.Stamp, p.Energy.Stamp));
        p.Health.Set(p.Health.Max, now);
        p.Energy.Set(p.Energy.Max, now);
        var combo = Combat.PlayerCombo(p, now);
        p.Session.Send(combo);
        p.SendToObservers(combo);
    }

    /// <summary>
    /// The world tick of a knocked-down or freshly risen player (before its combat timeout): the combat's end once no mob
    /// fights it, the get-up when due, the invulnerability buff getuptimeseconds after it and the buff's end.
    /// </summary>
    public static void Update(Player p, TimeSpan now)
    {
        var c = p.Combat;
        var d = Settings(p.World);
        if (c.Down is { } down)
        {
            if (down.Kind == KnockdownKind.Dead)
            {
                if (p.IsBot) return; // stage I: a dead bot's Leave is its despawn (Bots/BotCombat), it never respawns
                Death.Update(p, down, now); // E3: the Leave for the others, the auto-respawn
                return;
            }
            if (Revive.Update(p, down, now)) return; // stageN: the bleed-out's points and an open resurrection offer
            if (down.Kind != KnockdownKind.Pve)
            {
                if (now >= down.At + TimeSpan.FromSeconds(Seconds(p, down.Kind))) GetUp(p);
                return;
            }
            if (down.CombatOver is null && !Fought(p))
            {
                // 7. Combat over: the rest of the way up in knockdowngetuptimeseconds_combatover.
                down.CombatOver = now;
                var ticks = Math.Max(p.World.GameNow, p.Health.Stamp);
                p.Health.Set(p.Health.At(ticks), ticks);
                p.Health.RegenPerSecond = Regeneration(p, p.Health.Max);
                SendRegeneration(p, ticks);
            }
            var latest = down.At + TimeSpan.FromSeconds(d.PveSeconds);
            var due = down.CombatOver is { } over ? Min(over + TimeSpan.FromSeconds(d.CombatOverSeconds), latest) : latest;
            if (now >= due) GetUp(p);
            return;
        }
        if (c.BuffDue is { } buffAt && now >= buffAt) ApplyBuff(p);
        if (c.InvulnerableUntil is { } until && now >= until) EndBuff(p, "it ran out");
    }

    /// <summary>
    /// The get-up (SPEC §5e.2): full bars with the regular regeneration, Respawn (88) at the same spot to the player and its
    /// observers, RegenerationHealthChanged with the regular rate, the invulnerability buff getuptimeseconds later. A revive
    /// (stageN, <see cref="Revive"/>) passes the <paramref name="healthShare"/> of the maximum it gets up with.
    /// </summary>
    public static void GetUp(Player p, double healthShare = 1)
    {
        var c = p.Combat;
        if (c.Down is not { } down) return;
        c.Down = null;
        var world = p.World;
        if (down.Kind == KnockdownKind.Pvp)
        {
            // Stage I: the attackers of a PvP knockdown stay for its execute (Death.Kill) and end here, so a second knockdown shares only
            // its own damage and is a new result for the PvP goals.
            p.PvpState.HitBy.Clear();
            p.PvpState.DownCredits = null;
        }
        var now = Math.Max(world.GameNow, Math.Max(p.Health.Stamp, p.Energy.Stamp));
        var stats = p.ComputeStats();
        p.Health.Fill(p.Health.Max, stats.HealthRegen, now);
        if (healthShare < 1) p.Health.Set((float)Math.Max(1, Math.Round(p.Health.Max * healthShare)), now);
        if (down.Kind == KnockdownKind.Duel) p.Energy.Set(p.Energy.At(now), now); // a duel's get-up keeps the energy (SPEC §2.5)
        else p.Energy.Set(p.Energy.Max, now);
        var at = p.PositionAt(DateTime.UtcNow);
        var respawn = new RespawnEvent
        {
            ddn = p.Id, dvt = now, dvu = at, dvv = p.Facing,                                           // [0-3]
            dvw = p.Health.Value, dvx = p.Health.Max, dvz = p.Health.RegenPerSecond, dv0 = now,         // [4] [5] [7] [8]
            dv1 = p.Energy.Value, dv2 = p.Energy.Max, dv4 = p.Energy.RegenPerSecond, dv5 = now,         // [9] [10] [12] [13]
            dv6 = true, dv7 = true,                                                                     // [14] [15] the get-up animation
        };
        p.Session.Send(respawn);
        p.SendToObservers(respawn);
        p.Session.Send(Combat.RegenerationChanged(p, now));
        if (down.Hidden) p.Visibility.Reannounce(); // E3: a dead player raised in place (GM .heal) is shown to the others again
        if (down.Kind == KnockdownKind.Duel) DuelBuff(p, at, now);
        else
        {
            c.UpBuff = down.Kind == KnockdownKind.Pvp ? Pvp.RulesOf(world, p.Map).KnockdownBuffAfterGetUp ?? Settings(world).BuffAfterGetUp : BuffSpell;
            c.BuffDue = world.Now + TimeSpan.FromSeconds(Settings(world).GetUpSeconds);
        }
        Log.Info("combat", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' gets up at ({at.X:0.##}, {at.Y:0.##})"));
    }

    /// <summary>
    /// The player attacks or casts: the get-up buff ends. Walking does not call this because the authored buff permits it. An action in the getuptimeseconds
    /// before it comes changes nothing (SPEC §5e.2: it is applied 3 s after the get-up and removed at the first action after
    /// that). A no-op otherwise.
    /// </summary>
    public static void Acted(Player p, string what)
    {
        if (p.Combat.InvulnerableUntil is not null) EndBuff(p, what);
    }

    /// <summary>The player leaves its map: the knockdown and the buff end without events (nothing of them is saved).</summary>
    public static void Leave(Player p)
    {
        p.Combat.Down = null;
        p.Combat.BuffDue = null;
        p.Combat.InvulnerableUntil = null;
        p.Combat.UpBuff = BuffSpell;
    }

    /// <summary>
    /// The knockdown's health regeneration for a maximum of <paramref name="maxHealth"/>: max × knockdownhealthregentopercent
    /// over knockdowngetuptimeseconds_pve while mobs still fight the player, over knockdowngetuptimeseconds_combatover once
    /// the combat is over (also for a maximum changed by gear while down, <see cref="Player.RecalculateStats"/>).
    /// </summary>
    public static float Regeneration(Player p, float maxHealth)
    {
        var d = Settings(p.World);
        var down = p.Combat.Down;
        if (down is { Kind: KnockdownKind.Dead }) return 0; // E3: the dead do not regenerate
        var seconds = down is { Kind: not KnockdownKind.Pve } ? Seconds(p, down.Kind) : down?.CombatOver is null ? d.PveSeconds : d.CombatOverSeconds;
        return (float)(maxHealth * d.HealthRegenToPercent / seconds);
    }

    /// <summary>
    /// The longest a knockdown of <paramref name="kind"/> lasts: knockdowngetuptimeseconds_pve (600 s), _duel (6 s), or the
    /// archetype's knockdowngetuptimeseconds_pvp (YELLOW / ORANGE 90 s), else DeathSettings' _pvp (180 s).
    /// </summary>
    public static double Seconds(Player p, KnockdownKind kind)
    {
        var d = Settings(p.World);
        return kind switch
        {
            KnockdownKind.Duel => d.DuelSeconds,
            KnockdownKind.Pvp => p.World.Hellgates.KnockdownSeconds(p) ?? Pvp.RulesOf(p.World, p.Map).KnockdownSecondsPvp ?? d.PvpSeconds, // stageL: Hellgates:KnockdownSeconds inside
            _ => d.PveSeconds,
        };
    }

    /// <summary>
    /// The duel get-up's buff (SPEC §2.5 step 7, captured 3 of 3 in the instant of the Respawn): CastSpell {0 me, 1 me, 2 the spot,
    /// 3 DUEL_SPAWN_KNOCKDOWN (996), 4 = 5 now, 6 0, 7 0} to the player and its observers, then the spell runs on it (its heal over
    /// time, crowd control resistance and cleanse; its own breakseffect flags end it). Without spell data only the CastSpell.
    /// </summary>
    private static void DuelBuff(Player p, Vector2 at, long now)
    {
        var world = p.World;
        var name = world.GameData?.Duel.GetUpBuff ?? DuelSettings.Default.GetUpBuff;
        var spell = world.Spells?.Template(name);
        p.SendAround(new CastSpellEvent
        {
            ddn = p.Id, bvw = p.Id, bvx = at, bvy = spell?.Id ?? DefaultDuelBuffId, bvz = now, bv0 = now, Extra = new() { [6] = (byte)0, [7] = (short)0 },
        });
        if (spell is not null && p.Map is { } map)
            SpellCasts.Execute(new SpellCasts.SpellContext(p, map, spell, 0, 100, null, at, now), spell);
    }

    /// <summary>DUEL_SPAWN_KNOCKDOWN's id in 345319, for a world without spell data.</summary>
    public const short DefaultDuelBuffId = 996;

    /// <summary>A Move that walks somewhere (its target is not where the player stands), as against a turn on the spot.</summary>
    public static bool Walks(Vector2 position, Vector2? target) => target is { } t && Vector2.Distance(position, t) > 0.01f;

    private static void ApplyBuff(Player p)
    {
        var world = p.World;
        var c = p.Combat;
        c.BuffDue = null;
        var name = c.UpBuff;
        var spell = world.Spells?.Template(name);
        if (spell is null && name != BuffSpell) return; // a PvP get-up buff without spell data: none
        var id = spell?.Id ?? DefaultBuffId;
        var seconds = spell is null ? DefaultBuffSeconds
            : spell.Effects.Select(e => Data.SpellTemplate.Float(e, "time")).DefaultIfEmpty(0).Max() is var t and > 0 ? t : DefaultBuffSeconds;
        var now = world.GameNow;
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvy = id, Extra = new() { [6] = (byte)0 } });
        if (spell is null) p.SendAround(Combat.BuffEffects(p.Id, id, 1, now));
        else
        {
            // The buff as an entry of the player's list (stageD effects SPEC §2), cast by the world.
            SpellEffects.Apply(world, p, spell, 0, 100, 100, now, [.. spell.Effects.Select((node, i) => SpellEffects.Make(world, p, 100, 100, node, i, p, now))]);
            SpellEffects.Send(world, p);
            SpellEffects.Refresh(world, p);
        }
        c.InvulnerableUntil = world.Now + TimeSpan.FromSeconds(seconds);
        Log.Debug("combat", $"'{p.Character.Name}' is invulnerable for {seconds:0.#} s ({name})");
    }

    private static void EndBuff(Player p, string why)
    {
        p.Combat.InvulnerableUntil = null;
        var world = p.World;
        var name = p.Combat.UpBuff;
        if (SpellEffects.Remove(world, p, s => s.Spell.Name == name && s.Caster == 0) || p.Effects.Count == 0)
        {
            SpellEffects.Send(world, p); // the list without it (the empty one when it was the only spell)
            SpellEffects.Refresh(world, p);
        }
        Log.Debug("combat", $"'{p.Character.Name}' loses {name}: {why}");
    }

    /// <summary>RegenerationHealthChanged to the player and RegenerationPlayerComboChanged to it and its observers.</summary>
    private static void SendRegeneration(Player p, long now)
    {
        p.Session.Send(Combat.RegenerationChanged(p, now));
        var combo = Combat.PlayerCombo(p, now);
        p.Session.Send(combo);
        p.SendToObservers(combo);
    }

    /// <summary>Whether a living mob on the player's map still fights it (has it in its threat list or as its target).</summary>
    private static bool Fought(Player p) =>
        p.Map is { } map && map.Objects.Values.OfType<Mob>().Any(m => !m.Dead && (m.Threat.ContainsKey(p) || m.TargetId == p.Id));

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
