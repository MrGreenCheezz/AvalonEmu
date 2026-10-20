using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageD batch 1 part A (analysis/work/stageD/effects/SPEC.md §8): the client effect index list, the bytes of the captured
/// ActiveSpellEffectsUpdate examples, the crowd control duration fit and its diminishing returns, FIRESTAFFBOLT2's stacks on
/// a mob, and a self buff with its stacks, spell immunity and charges, the stun and root enforcement and the list for a new
/// observer. Part B: the FIREWALL and SEARING_FLAME areas, the FLAMEDASH dash, the ENERGY_BARRIER shield, ENRAGE's trigger and
/// the PARRY channel; a toggle and ChannelingCancel. Light: the numbers the SPEC lists, the rest only as far as it shows the rule.
/// </summary>
public static class EffectsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("effects: client effect index list (registered tags only: FIRESTAFFBOLT2 2, FLAMEDASH 13, THROWINGBLADES 4, ACID_BOMB 2) and gamedata numbers", Indices),
        ("effects: ActiveSpellEffectsUpdate bytes of the captured examples (dbb62d03, c330, d8300c, 4902; keys 5, 8, 9, 10)", Layout),
        ("effects: crowd control durations (GROUNDARROW_ROOT 1729/1591 ms, HOWL_SLOW 2947 ms), diminishing returns, root and stun on a mob", CrowdControl),
        ("effects: FIRESTAFFBOLT2 listed before its hit (7 {0x18}, 8 [3300]), a second stack restarts it with double ticks, no update at its end, new observers", BoltStacks),
        ("effects: DEFENSERUN on self: list, buffs, spell immunity, HEROIC_STACK stacks and IfConsumeCharges, stun refusals, the list after NewCharacter", SelfBuffs),
        ("effects: FIREWALL area (113 keys, fear ForcedMovement 3.25 m, c330, CastHits, DoT, re-entry CastHit type 2, Leave at +5 s), SEARING_FLAME deletes at its hit", Areas),
        ("effects: FLAMEDASH (ForcedMovement +164 ms, 11 listed groups of 750 ms, Move lock and key-6 echo, aura, FLAMEDASH_END at the end point)", Dash),
        ("effects: SWORD_SPIN dashes 9 m in 634 ms under 150 % load; overloaded, its pulses hit and it does not move (IfOwnLoadPercent)", SpinLoad),
        ("effects: SWORD_SPIN through the CastStart handler at the ground 6 m away: CastStart {2 aim}, the caster's ForcedMovement of 634 ms, the key-6 echo ends the lock", SpinCast),
        ("effects: BLINK shoes through the CastStart handler 6 m away (stageM): ForcedMovement {1 1, 3 = 2, 5 [P, aim], 7 4065, 8 5} to the caster and the watcher before the list, no Teleport, the key-6 echo; pull is kind 4", BlinkCast),
        ("effects: ENERGY_BARRIER shield (DamageShieldUpdate, absorb without HealthUpdate, bare at +3 s), ENRAGE stacks on damage, PARRY channel end and cancel", ShieldsAndChannels),
        ("effects: ENFEEBLEAURA toggle: TerminateToggleSpell takes it off (CastCancel {4 true, 5 slot, cooldown}), castable after the cooldown, ends by itself at +5 s; ChannelingCancel", Toggles),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>Whether an event's key 0 is object <paramref name="id"/> (key 0 is an array in some events).</summary>
    private static bool Is(Dictionary<byte, object?> p, long id) => p.TryGetValue(0, out var v) && v is IConvertible && Convert.ToInt64(v) == id;

    private static SpellTemplate T(string name) => Client.Spells!.Template(name) ?? throw new Exception($"{name} not in spells.xml");

    private static void Indices()
    {
        var flame = T("FLAMEDASH");
        var blades = T("THROWINGBLADES");
        Check(T("FIRESTAFFBOLT2").Effects.Count == 2 && flame.Effects.Count == 13 && flame.Effects[1].Name.LocalName == "dash"
              && blades.Effects.Count == 4 && Enumerable.Range(1, 3).All(i => blades.Effects[i].Name.LocalName == "spelleffectarea")
              && T("ACID_BOMB").Effects.Select(e => e.Name.LocalName).SequenceEqual(["spelleffectarea", "directattributechange"]),
              $"effect lists: FIRESTAFFBOLT2 2, FLAMEDASH {flame.Effects.Count} (dash at 1), THROWINGBLADES {blades.Effects.Count} (areas at 1-3), ACID_BOMB without its 3 projectiles");
        var tags = Client.Spells!.EffectTags();
        var run = tags.Keys.Count(SpellCasts.Supported.Contains);
        Check(tags.Keys.All(SpellTemplate.Registered.Contains) && SpellTemplate.Registered.Count == 131 && SpellTemplate.Timed.Count == 59 && tags.Count == 125 && run == 82
              && tags.Keys.Count(SpellCasts.NoOps.Contains) == 2,
              $"only registered tags (131) are listed, 59 timed; the start-up report: {run} of {tags.Count} kinds run (26 + the 3 inventory item tags of stageD consumables + DISMOUNTED's cannotattackplayers + 24 of stageD effects batch 2/3 + 18 of batch 3 + resurrect of stageN + 5 of stageN spells-bosses: learning point books, gathering tomes, fishing fame, repair kits, fame buffs + 4 of stageN spells2: storetargets, removestoredtargets, and activaterandomdungeon and castspellaftermoving, which ran outside the list), the 2 unlocks as no-ops");
        var e = Client.GameData!.Effects;
        Check(e.CcFactorPlayers == 0.7 && e.CcFactorMobs == 0.5 && e.DiminishingMax == 0.8 && e.Diminishing["stun"] == (0.3, 5) && e.Diminishing["knockback"] == (0.08, 10)
              && e.Diminishing["slow"] == (0.3, 10) && Math.Abs(e.HitPointProgression - 0.663445601) < 1e-12 && e.BaseLoad == 25,
              "gamedata AbilityPowerProgression (ccdurationfactor 0.7 / 0.5) and CrowdControlDiminishingReturns (stun 0.3 over 5 s, knockback 0.08, max 0.8)");
        var ox = Client.GameData.CombatProgressions["mainhand_1h"];
        Check(Math.Abs(Combat.AbilityPower(100, 1.0918, 800) - 201.90387) < 0.001 && Math.Abs(Combat.AbilityPower(100, 1.0564, 800) - 155.10541) < 0.001 && ox.CcPower == 1.0474,
              "keys 2/3: T5_MOUNT_OX at IP 800 100 × 1.0918^8 = 201.904, × 1.0564^8 = 155.105 (captured); mainhand_1h ccpowerprogression 1.0474");
        var s = EffectSettings.Default;
        Check(Math.Abs(SpellEffects.Scale("hitpointsmax", 100, 400, s) - 100 * Math.Pow(4, 0.663445601)) < 1e-9 && SpellEffects.Scale("physicalarmor", -0.16, 100, s) == -32
              && SpellEffects.Scale("movespeedbonus", 0.3, 400, s) == 0.3 && SpellEffects.Scale("crowdcontrolresistance", 1, 200, s) == 200
              && Math.Abs(SpellEffects.Scale("maxload", 1, 200, s) - (Math.Pow(2, 2.264101591) * 25 - 25)) < 1e-9,
              "bej.a_4: hitpointsmax × (AP/100)^0.6634, armor × 200 × (AP/100)^0.3366, bonuses unscaled, CCR × AP, maxload");
    }

    private static Dictionary<byte, object?> Wire(AlbionEvent e) => CombatTest.Wire(AlbionSerializer.BuildEvent(e));

    private static void Layout()
    {
        const long me = 1000, mob = 2000, caster = 1000;
        var now = GameTime.Now;
        // SPAWN_BUFFIMMUNITY (7 × spellimmunity 60 s), PORTAL_RESTRICTION_LYMHURST (time 0), PASSIVE_AA_STACK (dummy 15 s).
        var spawn = Wire(SpellEffects.Update(me, [
            ActiveSpell.Of(T("SPAWN_BUFFIMMUNITY"), 0, 100, 100, now, 1, [.. Enumerable.Range(0, 7).Select(i => (i, 60000))]),
            ActiveSpell.Of(T("PORTAL_RESTRICTION_LYMHURST"), 0, 100, 100, now, 1, (0, 0)),
            ActiveSpell.Of(T("PASSIVE_AA_STACK"), me, 100, 100, now, 1, (0, 15000)),
        ], now));
        Check(spawn[7] is byte[] and [0xdb, 0xb6, 0x2d, 0x03] && spawn[8] is int[] k8 && k8.SequenceEqual([.. Enumerable.Repeat(60000, 7), 15000])
              && spawn[9] is byte[] and [0x03] && spawn[10] is byte[] and [0, 0] && !spawn.ContainsKey(6),
              $"groups 3,3,3,3,3,3,3,1,3 → key 7 db b6 2d 03, key 8 [60000 × 7, 15000], key 9 {{03}} (the world's two), key 10 {{0, 0}} (got {Show([(EventCodes.ActiveSpellEffectsUpdate, spawn)])})");
        // A mob hit by FIREWALL: FIREWALL_EFFECT (DoT, applyspell, immunity vs mob, immunity vs player) and FIREWALL_FEAR.
        ActiveSpell Effect() => ActiveSpell.Of(T("FIREWALL_EFFECT"), caster, 250.64, 184.5, now, 1, (0, 3000), (2, 3000));
        ActiveSpell Fear() => ActiveSpell.Of(T("FIREWALL_FEAR"), caster, 250.64, 184.5, now, 1, (0, 500));
        var wall = Wire(SpellEffects.Update(mob, [Effect(), Fear()], now));
        Check(wall[7] is byte[] and [0xc3, 0x30] && wall[8] is int[] and [3000, 3000, 500] && wall[5] is byte[] and [1, 1] && wall[9] is byte[] and [0x03]
              && wall[10] is long[] and [caster, caster] && wall[2] is float[] and [250.64f, 250.64f] && wall[1] is short[] { Length: 2 } && wall[4] is long[] { Length: 2 },
              $"FIREWALL on a mob: key 7 c3 30, key 8 [3000, 3000, 500], key 5 {{1, 1}}, key 9 {{03}}, key 10 [caster, caster], key 2 [250.64 × 2] (got {Show([(EventCodes.ActiveSpellEffectsUpdate, wall)])})");
        var bolt = Wire(SpellEffects.Update(mob, [ActiveSpell.Of(T("FIRESTAFFBOLT2"), caster, 250.64, 184.5, now, 2, (1, 3300)), Effect(), Fear()], now));
        Check(bolt[7] is byte[] and [0xd8, 0x30, 0x0c] && bolt[5] is byte[] and [2, 1, 1] && bolt[8] is int[] and [3300, 3000, 3000, 500],
              $"a 2-stack FIRESTAFFBOLT2 in front: key 7 d8 30 0c, key 5 {{2, 1, 1}}, key 8 [3300, 3000, 3000, 500] (got {Show([(EventCodes.ActiveSpellEffectsUpdate, bolt)])})");
        var home = Wire(SpellEffects.Update(mob, [ActiveSpell.Of(T("MOB_RETURNHOME"), 0, 100, 100, now, 1, (0, 0), (1, 0), (2, 0), (3, 0))], now));
        var built = Wire(Combat.BuffEffects(mob, T("MOB_RETURNHOME").Id, 4, now));
        Check(home[7] is byte[] and [0x49, 0x02] && !home.ContainsKey(8) && home.Keys.Order().SequenceEqual(built.Keys.Order())
              && home.All(kv => kv.Key >= 250 || Show([(EventCodes.ActiveSpellEffectsUpdate, new() { [kv.Key] = kv.Value })]) == Show([(EventCodes.ActiveSpellEffectsUpdate, new() { [kv.Key] = built[kv.Key] })])),
              $"MOB_RETURNHOME: key 7 49 02, no key 8, the same as the captured reset buff layout (got {Show([(EventCodes.ActiveSpellEffectsUpdate, home)])})");
        var empty = Wire(SpellEffects.Update(mob, [], now));
        Check(empty[7] is byte[] and [0] && empty[9] is byte[] and [0] && empty[3] is float[] { Length: 0 } && !empty.ContainsKey(1), "the empty list is the captured death clear");
        // An inactive group never carries bit 1: a spell with its timed sub-effect not Active lists 000 there, and no key 8.
        var idle = Wire(SpellEffects.Update(mob, [ActiveSpell.Of(T("FIRESTAFFBOLT2"), caster, 100, 100, now, 1)], now));
        Check(idle[7] is byte[] and [0] && !idle.ContainsKey(8), "an inactive group: 000 and no duration");
    }

    private static void CrowdControl()
    {
        // The fit of SPEC §3.2 (K = 0.84 = 0.7 × (1 + 0.2), the ×1.2 most likely BonusCCDurationVsMobs, §9 Q4).
        var ghoul = SpellEffects.CcDuration(1.8, 157.9, 0.7, 0.2, 38, 0) * 1000;
        var soldier = SpellEffects.CcDuration(1.8, 157.9, 0.7, 0.2, 50, 0) * 1000;
        Check(Math.Abs(ghoul - 1729) <= 2 && Math.Abs(soldier - 1591) <= 2,
              $"GROUNDARROW_ROOT 1.8 s, CcAp 157.9, K 0.84: ghoul (CCR 38) {ghoul:0} ms (captured 1729), soldier (CCR 50) {soldier:0} ms (captured 1591)");
        // Without the kit's plate armour: its bonusccdurationvsmobs 0.4 (stageJ pvp-spells phase 0) would lengthen the captured durations.
        using var arena = new Arena { Setup = c => c.Equipment[3] = null };
        var world = arena.World;
        var (_, _, me) = arena.Join("Archer", new InventoryItem("T4_MAIN_SWORD"));
        var mob = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(4, 0));
        Check(mob.Template.CrowdControlResistance == 50, $"mobs.xml crowdcontrolresistance of the soldier: 50 (got {mob.Template.CrowdControlResistance})");
        var howl = T("HOWL_SLOW");
        SubEffect? Slow() { lock (world.Sync) return SpellEffects.Make(world, me, 100, 157.9, howl.Effects[0], 0, mob, world.GameNow); }
        var first = Slow();
        arena.Run(2);
        var second = Slow();
        Check(first is { CcType: "slow", DurationMs: 2947 } && second is { CcType: "slow" } && Math.Abs(second.DurationMs - 2947 * (1 - 0.3 * 0.8)) <= 1,
              $"HOWL_SLOW 4 s on the soldier: 4 × 1.579 × 0.7 × 100/150 = 2947 ms (captured 2947); 2 s later the slow DR level 0.3 × (1 − 2/10): {second?.DurationMs} ms");
        // A root: the mob's walk speed is 0 (no event 3), a stun also stops its swings; a cceffectimmunity keeps a stun off.
        var stun = FirstWith("stun");
        lock (world.Sync)
        {
            SpellEffects.Apply(world, mob, T("GROUNDARROW_ROOT"), me.Id, 100, 157.9, world.GameNow,
                [SpellEffects.Make(world, me, 100, 157.9, T("GROUNDARROW_ROOT").Effects[0], 0, mob, world.GameNow)]);
            mob.Motion.MoveTo(mob.Position + new Vector2(5, 0), 5);
        }
        Check(SpellEffects.Rooted(mob, world.GameNow) && !mob.Motion.Moving && SpellEffects.SpeedFactor(mob, world.GameNow) == 0,
              "rooted: speed factor 0, a walk is a stop");
        arena.Run(1.7);
        lock (world.Sync) mob.Motion.MoveTo(mob.Position + new Vector2(5, 0), 5);
        Check(!SpellEffects.Rooted(mob, world.GameNow) && mob.Motion.Moving && mob.Effects.Count == 0, "after the root's 1.8 × 1.579 × 0.7 × 100/150 = 1326 ms it walks again; the entry is pruned");
        var spell = stun.Spell;
        var node = stun.Node;
        lock (world.Sync)
        {
            SpellEffects.Apply(world, mob, spell, me.Id, 100, 100, world.GameNow, Planned(spell, SpellEffects.Make(world, me, 100, 100, node, stun.Index, mob, world.GameNow)));
            SpellEffects.Refresh(world, mob);
        }
        Check(SpellEffects.Stunned(mob, world.GameNow) && SpellEffects.CannotAttack(mob, world.GameNow) && !mob.Motion.Moving,
              $"stunned by {spell.Name}: no swing, no walk");
        var immune = T("SPAWN_KNOCKDOWN_INVULNERABILITY");
        lock (world.Sync)
        {
            SpellEffects.Apply(world, me, immune, 0, 100, 100, world.GameNow, [.. immune.Effects.Select((n, i) => SpellEffects.Make(world, me, 100, 100, n, i, me, world.GameNow))]);
            Check(SpellEffects.Make(world, mob, 100, 100, node, stun.Index, me, world.GameNow) is null && SpellEffects.CcImmune(me, "root", world.GameNow)
                  && SpellEffects.Invincible(me, world.GameNow) && SpellEffects.Immune(me, T("FIRESTAFFBOLT2"), world.GameNow),
                  "SPAWN_KNOCKDOWN_INVULNERABILITY: cceffectimmunity keeps a stun off, invincibility, spellimmunity category damage blocks FIRESTAFFBOLT2");
        }
    }

    /// <summary>The first spell of spells.xml with a direct child <paramref name="tag"/> that has a time and no conditions.</summary>
    private static (SpellTemplate Spell, System.Xml.Linq.XElement Node, int Index) FirstWith(string tag)
    {
        var spells = Client.Spells!;
        for (var id = 0; id < spells.Count; id++)
            if (spells.Template(id) is { } t)
                for (var i = 0; i < t.Effects.Count; i++)
                    if (t.Effects[i].Name.LocalName == tag && SpellTemplate.Float(t.Effects[i], "time") > 0.5f && !t.Effects[i].HasElements)
                        return (t, t.Effects[i], i);
        throw new Exception($"no spell with <{tag}>");
    }

    private static SubEffect?[] Planned(SpellTemplate spell, SubEffect? sub)
    {
        var a = new SubEffect?[spell.Effects.Count];
        if (sub is not null) a[sub.Index] = sub;
        return a;
    }

    private static void BoltStacks()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = arena.Join("Pyro",
            new InventoryItem("T4_MAIN_FIRESTAFF") { Quality = 5, Active = ["FIRESTAFFBOLT2", "FIRESTAFFIGNITE2_SPREAD", "PYROBLAST_SKILLSHOT"] },
            ("COMBAT_FIRESTAFFS_FIRE", 100), ("COMBAT_FIRESTAFFS", 100), ("COMBAT_FIRESTAFFS_AVALON", 100));
        var soldier = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(6, 0));
        var staff = me.Inventory.Equipment[0]!;
        var bolt = T("FIRESTAFFBOLT2").Id;
        var mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = bolt, d1 = soldier.Id, d4 = 1 });
        arena.Run(0.8);
        var hit1 = L(Since(peer, mark).Single(e => e.Code == EventCodes.CastSpell).P, 5);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = bolt, d1 = soldier.Id, d4 = 2 }); // the first is finished: the next at once
        arena.Run(0.25);
        var first = Since(peer, mark).Where(e => e.Code is EventCodes.ActiveSpellEffectsUpdate or EventCodes.CastHit or EventCodes.HealthUpdate).ToList();
        Check(first.Count >= 3 && first[0].Code == EventCodes.ActiveSpellEffectsUpdate && first[1].Code == EventCodes.CastHit && first[2].Code == EventCodes.HealthUpdate,
              $"the hit batch: ActiveSpellEffectsUpdate, CastHit, HealthUpdate (got {Show(first)})");
        var list = first[0].P;
        Check(L(list, 0) == soldier.Id && list[1] is short[] and [3214] && list[2] is float[] and [var ap] && ap == (float)Combat.AbilityPowerOf(me, staff)
              && list[3] is float[] and [var ccAp] && ccAp == (float)Combat.CcAbilityPowerOf(me, staff) && ccAp < ap && list[4] is long[] and [var h] && h == hit1
              && list[5] is byte[] and [1] && list[7] is byte[] and [0x18] && list[8] is int[] and [3300] && list[9] is byte[] and [1] && list[10] is long[] and [var by] && by == me.Id,
              $"ASEU {{1 [3214], 2 [AP], 3 [CcAp], 4 [the hit], 5 {{1}}, 7 {{0x18}}, 8 [3300], 9 {{1}}, 10 [me]}} (got {Show(first.Take(1))})");
        mark = peer.Events.Count;
        arena.Run(0.8);
        var second = Since(peer, mark);
        var again = second.Single(e => e.Code == EventCodes.ActiveSpellEffectsUpdate).P;
        var hit2 = again[4] is long[] and [var h2] ? h2 : 0;
        Check(again[5] is byte[] and [2] && hit2 > hit1 && hit2 - hit1 < SpellCasts.Seconds(3.3) && soldier.Effects.Spells is [{ Stacks: 2 }],
              $"the second bolt within 3.3 s: key 5 {{2}}, key 4 its own hit (got {Show(second.Where(e => e.Code == EventCodes.ActiveSpellEffectsUpdate))})");
        mark = peer.Events.Count;
        arena.Run(4);
        var ticks = Since(peer, mark).Where(e => e.Code == EventCodes.HealthUpdate && Convert.ToByte(e.P[5]) == 5).ToList();
        // 4 × AP 275.9 / 100 × 100/221 = 4.99 per stack: the restarted ticks hit for 2 × that.
        Check(ticks.Count == 4 && ticks.All(t => F(t.P, 2) == -11) && ticks.Select(t => L(t.P, 1) - hit2).SequenceEqual(new[] { 0.3, 1.3, 2.3, 3.3 }.Select(SpellCasts.Seconds))
              && !Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate) && soldier.Effects.Count == 0,
              $"4 ticks of -11 (2 stacks × 4 × AP 2.759 × 1.1 for the starter plate set 1's spell damage bonus × R) from the second hit, the first's later ticks dropped; no update at its end, the entry pruned (got {Show(ticks)})");
        // A newcomer seeing a debuffed mob: NewMob, then its list.
        lock (world.Sync)
            SpellEffects.Apply(world, soldier, T("AXESMASH_EFFECT"), me.Id, 200, 150, world.GameNow,
                [.. T("AXESMASH_EFFECT").Effects.Select((n, i) => SpellEffects.Make(world, me, 200, 150, n, i, soldier, world.GameNow))]);
        var (_, newcomer, _) = arena.Join("Newcomer", new InventoryItem("T4_MAIN_SWORD"));
        var seen = Since(newcomer, 0).Where(e => Is(e.P, soldier.Id) && e.Code is EventCodes.NewMob or EventCodes.ActiveSpellEffectsUpdate).ToList();
        Check(seen.Count == 2 && seen[0].Code == EventCodes.NewMob && seen[1].Code == EventCodes.ActiveSpellEffectsUpdate && seen[1].P[1] is short[] and [var debuff]
              && debuff == T("AXESMASH_EFFECT").Id,
              $"a new observer: NewMob, then the mob's list (got {Show(seen)})");
        var resisted = Combat.ResistanceOf(soldier, EffectType.Physical);
        Check(Math.Abs(resisted - Combat.Resistance(soldier.Template.Armor - 0.16 * 200 * Math.Pow(2, 0.336554399))) < 1e-6,
              "the armor debuff's scaled points (−0.16 × 200 × 2^0.3366) lower the soldier's armor");
    }

    private static void SelfBuffs()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = arena.Join("Knight", new InventoryItem("T4_MAIN_SWORD"));
        var map = world.Maps.Find("0204")!;
        var run = T("DEFENSERUN");
        void Cast(SpellTemplate spell)
        {
            lock (world.Sync)
                SpellCasts.Execute(new SpellCasts.SpellContext(me, map, spell, 1, 100, null, me.PositionAt(DateTime.UtcNow), world.GameNow), spell);
        }
        var mark = peer.Events.Count;
        Cast(run);
        var list = Since(peer, mark).Single(e => e.Code == EventCodes.ActiveSpellEffectsUpdate).P;
        var stack = T("HEROIC_STACK");
        // DEFENSERUN: applyspell 000, 3 × spellimmunity 3 s, 3 × buffovertime 3 s; then HEROIC_STACK (applied by it) 6 s.
        Check(list[1] is short[] ids && ids.SequenceEqual([run.Id, stack.Id]) && list[7] is byte[] and [0xd8, 0xb6, 0x6d] && list[8] is int[] k8
              && k8.SequenceEqual([3000, 3000, 3000, 3000, 3000, 3000, 6000]) && list[9] is byte[] and [0] && !list.ContainsKey(10),
              $"DEFENSERUN then HEROIC_STACK, own casts (key 9 {{0}}, no key 10) (got {Show([(EventCodes.ActiveSpellEffectsUpdate, list)])})");
        var now = world.GameNow;
        Check(SpellEffects.Immune(me, T("PURGE"), now) && !SpellEffects.Immune(me, T("FIRESTAFFBOLT2"), now) && Math.Abs(me.Effects.Sum("movespeedbonus", now) - 0.3) < 1e-6
              && Math.Abs(SpellEffects.TakenFactor(me, fromPlayer: false, now) - 1 / 1.35) < 1e-6 && Math.Abs(me.Effects.Sum("attackspeedbonus", now) - 0.12) < 1e-6,
              "spellimmunity PURGE (3 s), movespeedbonus 0.3, bonusdefensevsmobs 0.35 (damage × 1/1.35), HEROIC_STACK attackspeedbonus 0.12");
        Cast(run);
        Cast(run);
        Check(me.Effects.Find(stack.Id, me.Id) is { Stacks: 3 } && me.Effects.Find(run.Id, me.Id) is { Stacks: 1 } && Math.Abs(me.Effects.Sum("attackspeedbonus", world.GameNow) - 0.36) < 1e-6,
              "three casts: HEROIC_STACK 3 stacks (maxcharges 3, its buff × 3), DEFENSERUN stays 1 (no maxstack)");
        // SWORD_SPIN: the stack branch (IfConsumeCharges takes one), not the "not IfSpellActive" one.
        mark = peer.Events.Count;
        Cast(T("SWORD_SPIN"));
        Check(me.Effects.Find(stack.Id, me.Id) is { Stacks: 2 } && Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && e.P[5] is byte[] b && b.Contains((byte)2)),
              "SWORD_SPIN consumes one HEROIC_STACK charge: key 5 2, sent at once");
        // A stun (a world-cast one): CastStart and AttackStart refused, Move ignored.
        var stun = FirstWith("stun");
        lock (world.Sync)
        {
            SpellEffects.Apply(world, me, stun.Spell, 0, 100, 100, world.GameNow, Planned(stun.Spell, SpellEffects.Make(world, me, 100, 100, stun.Node, stun.Index, me, world.GameNow)));
            Check(SpellCasts.Check(me, [T("HEROICSTRIKE2").Id], 0, T("HEROICSTRIKE2").Id, 0, me.PositionAt(DateTime.UtcNow), 1, world.GameNow).Why
                  is CastRefusal.Stunned, "stunned: CastStart refused");
        }
        var responses = peer.Responses.Count;
        var before = me.Movement;
        Send(session, new AttackStartRequest { wf = world.GameNow, co = 1 });
        var here = me.PositionAt(DateTime.UtcNow);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [here.X, here.Y], v7 = 90, v8 = [here.X + 20, here.Y], v9 = 5.5f });
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == WorldSession.AttackInvalidTarget && me.Movement == before,
              "stunned: AttackStart refused (-501), a Move ignored");
        // A new observer of the buffed player: NewCharacter, then its list.
        var (_, watcher, _) = arena.Join("Watcher", new InventoryItem("T4_MAIN_SWORD"));
        var seen = Since(watcher, 0).Where(e => Is(e.P, me.Id)).Select(e => e.Code).ToList();
        Check(seen.Take(2).SequenceEqual([EventCodes.NewCharacter, EventCodes.ActiveSpellEffectsUpdate]), $"a new observer: NewCharacter, then the player's list (got {string.Join(", ", seen)})");
    }

    // ---- part B (SPEC §8 items 3, 4, 8; channels and ENRAGE lightly) ----

    /// <summary>Runs <paramref name="spell"/> as <paramref name="me"/>'s hit now (counter <paramref name="counter"/>), aimed at <paramref name="aim"/>.</summary>
    private static long Hit(Arena arena, Player me, SpellTemplate spell, byte counter, double ap, Vector2 aim, Mob? target = null)
    {
        var world = arena.World;
        lock (world.Sync)
        {
            var now = world.GameNow;
            SpellCasts.Execute(new SpellCasts.SpellContext(me, world.Maps.Find("0204")!, spell, counter, ap, target, aim, now), spell);
            return now;
        }
    }

    private static bool About(float a, float b) => Math.Abs(a - b) < 0.01f;

    private static void Areas()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, peer, me) = arena.Join("Pyro", new InventoryItem("T4_MAIN_FIRESTAFF"));
        var soldier = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(2, 0));
        var wall = T("FIREWALL");
        var effect = T("FIREWALL_EFFECT");
        var fear = T("FIREWALL_FEAR");
        Check(wall.Id == 3234 && effect.Id == 3235 && fear.Id == 3236, $"FIREWALL 3234, FIREWALL_EFFECT 3235, FIREWALL_FEAR 3236 (got {wall.Id}, {effect.Id}, {fear.Id})");
        var mark = peer.Events.Count;
        var t = Hit(arena, me, wall, 7, 250.64, arena.Stand + new Vector2(3, 0));
        var batch = Since(peer, mark).Where(e => e.Code == EventCodes.NewSpellEffectArea || Is(e.P, soldier.Id)).ToList();
        var area = batch.FirstOrDefault(e => e.Code == EventCodes.NewSpellEffectArea).P;
        Check(area is not null && L(area, 1) == me.Id && L(area, 4) == wall.Id && !area.ContainsKey(5) && L(area, 6) == t && L(area, 7) == t + SpellCasts.Seconds(5)
              && L(area, 8) == 7 && area.TryGetValue(9, out var k9) && Convert.ToByte(k9) == 0 && !area.ContainsKey(10) && About(F(area, 3), 90)
              && area[2] is float[] c && About(c[0], arena.Stand.X) && About(c[1], arena.Stand.Y),
              $"NewSpellEffectArea {{1 me, 2 the caster (not + the node offset), 3 90°, 4 3234, 5 0, 6 T, 7 T+5 s, 8 7, 9 0}} (got {Show(batch.Where(e => e.Code == EventCodes.NewSpellEffectArea))})");
        var codes = batch.Select(e => e.Code).ToList();
        var fm = batch.FirstOrDefault(e => e.Code == EventCodes.ForcedMovement).P;
        Check(codes.Take(5).SequenceEqual([EventCodes.NewSpellEffectArea, EventCodes.ForcedMovement, EventCodes.ActiveSpellEffectsUpdate, EventCodes.CastHits, EventCodes.HealthUpdate]),
              $"the hit batch: NewSpellEffectArea, ForcedMovement, ActiveSpellEffectsUpdate, CastHits, HealthUpdate (got {string.Join(", ", codes)})");
        Check(fm is not null && Convert.ToByte(fm[1]) == 3 && L(fm, 2) == t && L(fm, 3) == t + SpellCasts.Seconds(0.5) && About(F(fm, 4), 6.5f) && soldier.Template.AttackMoveSpeed == 6.5f
              && fm[5] is float[] path && About(path[0], arena.Stand.X + 2) && About(path[2], arena.Stand.X + 2 + 3.25f) && About(path[3], arena.Stand.Y)
              && L(fm, 7) == fear.Id && !fm.ContainsKey(8) && !fm.ContainsKey(9),
              $"fear: ForcedMovement {{1 3, 2 T, 3 T+500, 4 6.5 (the soldier's attackmovespeed), 5 [p, p + 3.25 m away from the caster], 7 3236}}, no key 9 (got {Show(batch.Where(e => e.Code == EventCodes.ForcedMovement))})");
        var list = batch.First(e => e.Code == EventCodes.ActiveSpellEffectsUpdate).P;
        var hits = batch.First(e => e.Code == EventCodes.CastHits).P;
        Check(list[1] is short[] ids && ids.SequenceEqual([effect.Id, fear.Id]) && list[7] is byte[] and [0xc3, 0x30] && list[8] is int[] and [3000, 3000, 500]
              && hits[1] is long[] casters && casters.All(x => x == me.Id) && hits[2] is short[] spells && spells.SequenceEqual([effect.Id, fear.Id])
              && hits[3] is byte[] and [1, 1] && hits[4] is byte[] and [0, 0],
              $"ASEU c3 30 [3000, 3000, 500], CastHits {{2 [3235, 3236], 3 {{1, 1}}, 4 {{0, 0}}}} (got {Show(batch.Where(e => e.Code is EventCodes.ActiveSpellEffectsUpdate or EventCodes.CastHits))})");
        // The fear's end: it stands 3.25 m beyond the wall; walking back into it at T+1 s meets the 3 s spell immunity.
        arena.Run(0.6);
        Check(Vector2.Distance(soldier.Position, arena.Stand + new Vector2(2 + 3.25f, 0)) < 0.01f && !SpellMovement.Moving(soldier, world.GameNow), "after the fear the soldier stands at the path's end");
        mark = peer.Events.Count;
        lock (world.Sync) soldier.Motion.MoveTo(arena.Stand + new Vector2(3, 0), 40);
        arena.Run(0.4);
        var back = Since(peer, mark).Where(e => Is(e.P, soldier.Id) && e.Code is EventCodes.CastHit or EventCodes.CastHits or EventCodes.ActiveSpellEffectsUpdate or EventCodes.HealthUpdate).ToList();
        Check(back.Count(e => e.Code == EventCodes.CastHit && L(e.P, 2) == effect.Id && Convert.ToByte(e.P[3]) == 2) == 1
              && !back.Any(e => e.Code is EventCodes.ActiveSpellEffectsUpdate or EventCodes.CastHits) && back.Where(e => e.Code == EventCodes.HealthUpdate).All(e => Convert.ToByte(e.P[5]) == 5),
              $"the re-entry: CastHit {{2 3235, 3 2}}, no ASEU, only the DoT's own ticks (got {Show(back)})");
        mark = peer.Events.Count;
        arena.Run(4.1);
        var later = Since(peer, mark);
        var areaId = L(area!, 0);
        var ticks = Since(peer, 0).Where(e => e.Code == EventCodes.HealthUpdate && Is(e.P, soldier.Id) && Convert.ToByte(e.P[5]) == 5).Select(e => L(e.P, 1) - t).ToList();
        Check(ticks.SequenceEqual(new[] { 0.0, 1, 2, 3 }.Select(SpellCasts.Seconds)) && later.Count(e => e.Code == EventCodes.Leave && Is(e.P, areaId)) == 1,
              $"the DoT at T, T+1, T+2, T+3 s (origin 5), the Leave of the area at T+5 s (got ticks {string.Join(", ", ticks.Select(x => x / TimeSpan.TicksPerMillisecond))} ms)");
        // SEARING_FLAME (maxeffectareatargets 1, deletewhenmaxtargetshit): the area leaves in the tick of its first hit, before its 0.81 s.
        mark = peer.Events.Count;
        Hit(arena, me, T("SEARING_FLAME"), 9, 250.64, soldier.Position);
        arena.Run(0.3);
        var flame = Since(peer, mark);
        var flameArea = flame.FirstOrDefault(e => e.Code == EventCodes.NewSpellEffectArea).P;
        Check(flameArea is not null && flame.Any(e => e.Code == EventCodes.Leave && Is(e.P, L(flameArea, 0))) && flame.Any(e => e.Code == EventCodes.HealthUpdate && Is(e.P, soldier.Id)),
              $"SEARING_FLAME: hit, then its Leave at once (got {string.Join(", ", flame.Select(e => e.Code))})");
    }

    private static void Dash()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = arena.Join("Dasher", new InventoryItem("T4_MAIN_FIRESTAFF"));
        var mob = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(2.3f, 1));
        var flame = T("FLAMEDASH");
        var p = arena.Stand;
        var a = p + new Vector2(4.59f, 0);
        var mark = peer.Events.Count;
        var t = Hit(arena, me, flame, 4, 200, a);
        var own = Since(peer, mark);
        var fm = own.FirstOrDefault(e => e.Code == EventCodes.ForcedMovement).P;
        Check(flame.Id == 3275 && fm is not null && L(fm, 0) == me.Id && (!fm.ContainsKey(1) || Convert.ToByte(fm[1]) == 0) && L(fm, 2) == t
              && L(fm, 3) == t + 164 * TimeSpan.TicksPerMillisecond && About(F(fm, 4), 28) && fm[5] is float[] path && About(path[0], p.X) && About(path[2], a.X)
              && L(fm, 7) == flame.Id && Convert.ToByte(fm[8]) == 1 && L(fm, 9) == me.Id,
              $"FLAMEDASH 4.59 m: ForcedMovement {{0 me, 1 0, 3 T+164 ms, 4 28, 5 [P, A], 7 3275, 8 1, 9 me}} (got {Show(own.Where(e => e.Code == EventCodes.ForcedMovement))})");
        var list = own.First(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && Is(e.P, me.Id)).P;
        var bits = new byte[(3 * 13 + 7) / 8];
        for (var g = 2; g < 13; g++)
        {
            bits[3 * g / 8] |= (byte)(1 << (3 * g % 8));
            bits[(3 * g + 1) / 8] |= (byte)(1 << ((3 * g + 1) % 8));
        }
        Check(list[1] is short[] and [var id] && id == flame.Id && list[7] is byte[] k7 && k7.SequenceEqual(bits) && list[8] is int[] k8 && k8.Length == 11 && k8.All(x => x == 750),
              $"ASEU on me: FLAMEDASH with nop and dash 000 and 11 groups of 750 ms (got {Show([(EventCodes.ActiveSpellEffectsUpdate, list)])})");
        Check(SpellEffects.Invincible(me, world.GameNow) && SpellEffects.NotHittable(me, world.GameNow) && mob.Effects.Find(T("FLAMEDASH_PASSTHROUGH_DAMAGE").Id, me.Id) is not null,
              "on the dash: invincible, not hittable; the aura put FLAMEDASH_PASSTHROUGH_DAMAGE on the mob 1 m beside the path");
        arena.Run(0.05);
        var before = me.Movement;
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [p.X + 1, p.Y], v7 = 90, v8 = [p.X + 9, p.Y], v9 = 5.5f });
        Check(ReferenceEquals(me.Movement, before), "a Move at T+50 ms without the echo is ignored");
        mark = peer.Events.Count;
        arena.Run(0.15);
        var end = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.CastSpell && L(e.P, 0) == me.Id).P;
        Check(end is not null && L(end, 3) == T("FLAMEDASH_END").Id && L(end, 4) == t + 164 * TimeSpan.TicksPerMillisecond && end[2] is float[] at && About(at[0], a.X)
              && end.TryGetValue(6, out var k6) && Convert.ToByte(k6) == 0 && Vector2.Distance(me.PositionAt(DateTime.UtcNow), a) < 0.01f,
              $"CastSpell FLAMEDASH_END {{2 the end point, 4 T+164, 6 0}} at the end, the player stands there (got {(end is null ? "none" : Show([(EventCodes.CastSpell, end)]))})");
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [a.X, a.Y], v7 = 90, v8 = [a.X + 3, a.Y], v9 = 5.5f, wb = t });
        Check(me.Movement is { Speed: 5.5f } && me.Effects.Forced is null, "the Move with key 6 = the ForcedMovement's key 2 is taken and ends the lock");
    }

    private static void SpinLoad()
    {
        var spin = T("SWORD_SPIN");
        foreach (var heavy in new[] { false, true })
        {
            using var arena = new Arena();
            if (heavy) arena.Setup = c => c.Inventory.AddRange(Enumerable.Range(0, 8).Select(_ => (InventoryItem?)new InventoryItem("T4_ORE", 999)));
            var (_, peer, me) = arena.Join(heavy ? "Laden" : "Spinner", new InventoryItem("T4_MAIN_SWORD"));
            var mob = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(2, 0));
            var mark = peer.Events.Count;
            Hit(arena, me, spin, 3, 200, arena.Stand + new Vector2(6, 0));
            arena.Run(0.8);
            var events = Since(peer, mark);
            var moved = events.Any(e => e.Code == EventCodes.ForcedMovement && Is(e.P, me.Id) && L(e.P, 3) - L(e.P, 2) == 634 * TimeSpan.TicksPerMillisecond);
            var hit = events.Any(e => e.Code == EventCodes.HealthUpdate && Is(e.P, mob.Id));
            Check(hit && moved != heavy && Passives.LoadPercent(me) is var load && (load < 150) != heavy,
                  $"SWORD_SPIN at {Passives.LoadPercent(me):0} % load: the pulses hit, {(heavy ? "no dash" : "the 634 ms dash")} (got hit {hit}, dash {moved})");
        }
    }

    /// <summary>The user's report (README "Known issues"): the whole cast path of the client's SWORD_SPIN, not Execute alone.</summary>
    private static void SpinCast()
    {
        using var arena = new Arena();
        var world = arena.World;
        var spin = T("SWORD_SPIN");
        var (session, peer, me) = arena.Join("Whirler", new InventoryItem("T4_MAIN_SWORD") { Active = ["HEROICSTRIKE2", "SWORD_SPIN", "MIGHTYBLOW"] });
        var (_, watcher, _) = arena.Join("Onlooker", new InventoryItem("T4_MAIN_SWORD"));
        var aim = arena.Stand + new Vector2(6, 0);
        var (mark, wmark, responses) = (peer.Events.Count, watcher.Events.Count, peer.Responses.Count);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = spin.Id, d2 = aim, d4 = 1 });
        var start = Since(watcher, wmark).FirstOrDefault(e => e.Code == EventCodes.CastStart && Is(e.P, me.Id)).P;
        var fm = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.ForcedMovement && Is(e.P, me.Id)).P;
        Check(peer.Responses.Count == responses && start is not null && start[2] is float[] { Length: 2 } at && Vector2.Distance(new Vector2(at[0], at[1]), aim) < 0.01f
              && fm is not null && L(fm, 3) - L(fm, 2) == 634 * TimeSpan.TicksPerMillisecond,
              $"CastStart {{2 aim}} to the watcher, ForcedMovement {{3-2 = 634 ms}} to the caster (got {(start is null ? "no CastStart" : Show([(EventCodes.CastStart, start)]))}, {(fm is null ? "no ForcedMovement" : Show([(EventCodes.ForcedMovement, fm)]))})");
        if (fm is null) return;
        var own = Since(peer, mark);
        var fmAt = own.FindIndex(e => e.Code == EventCodes.ForcedMovement && Is(e.P, me.Id));
        var listAt = own.FindIndex(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && Is(e.P, me.Id));
        Check(listAt < 0 || fmAt < listAt, $"stageM: the caster's ForcedMovement before its ActiveSpellEffectsUpdate, as the official own dashes (got {string.Join(", ", own.Select(e => e.Code))})");
        arena.Run(0.7);
        var end = me.PositionAt(DateTime.UtcNow);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [end.X, end.Y], v7 = 90, v8 = [end.X + 3, end.Y], v9 = 5.5f, wb = L(fm, 2) });
        Check(me.Movement is { Speed: 5.5f } && me.Effects.Forced is null && Vector2.Distance(end, arena.Stand) > 5,
              $"the caster ends {Vector2.Distance(end, arena.Stand):0.0} m away, and the Move with key 6 = the ForcedMovement's key 2 is taken");
    }

    /// <summary>
    /// The user's report of stageM (the shoes' blink does not move): BLINK through the client's own CastStart. The caster's client moves
    /// only by a ForcedMovement (kind 1, end = start: it snaps), and its next Move echoes key 2; an older Move without the echo is ignored.
    /// </summary>
    private static void BlinkCast()
    {
        using var arena = new Arena();
        arena.Setup = c => c.Equipment[4] = new InventoryItem("T4_SHOES_CLOTH_SET2") { Active = ["BLINK"] };
        var world = arena.World;
        var blink = T("BLINK");
        var (session, peer, me) = arena.Join("Blinker", new InventoryItem("T4_2H_QUARTERSTAFF"));
        var (_, watcher, _) = arena.Join("Onlooker", new InventoryItem("T4_MAIN_SWORD"));
        var p = me.PositionAt(DateTime.UtcNow);
        var aim = p + new Vector2(6, 0);
        var (mark, wmark) = (peer.Events.Count, watcher.Events.Count);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 5, d0 = blink.Id, d2 = aim, d4 = 1 });
        var own = Since(peer, mark);
        var fm = own.FirstOrDefault(e => e.Code == EventCodes.ForcedMovement && Is(e.P, me.Id)).P;
        var seen = Since(watcher, wmark);
        var wfm = seen.FirstOrDefault(e => e.Code == EventCodes.ForcedMovement && Is(e.P, me.Id)).P;
        var fmAt = own.FindIndex(e => e.Code == EventCodes.ForcedMovement);
        var listAt = own.FindIndex(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && Is(e.P, me.Id));
        Check(blink.Id == 4065 && fm is not null && Convert.ToByte(fm[1]) == 1 && L(fm, 3) == L(fm, 2) && fm[5] is float[] path && About(path[0], p.X) && About(path[2], aim.X)
              && About(path[3], aim.Y) && L(fm, 7) == blink.Id && Convert.ToByte(fm[8]) == 5 && L(fm, 9) == me.Id && fmAt >= 0 && (listAt < 0 || fmAt < listAt)
              && wfm is not null && Convert.ToByte(wfm[1]) == 1 && !seen.Any(e => e.Code == EventCodes.Teleport) && !own.Any(e => e.Code == EventCodes.Teleport)
              && Vector2.Distance(SpellMovement.PositionOf(me, world.GameNow), aim) < 0.01f,
              $"BLINK: ForcedMovement {{1 1, 3 = 2, 5 [P, aim], 7 4065, 8 5, 9 me}} to the caster before its list and to the watcher, no Teleport, the server has it at the aim (got {Show(own)} / watcher {Show(seen.Where(e => e.Code is EventCodes.ForcedMovement or EventCodes.Teleport))})");
        if (fm is null) return;
        arena.Run(0.05);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [p.X, p.Y], v7 = 90, v8 = [p.X, p.Y + 3], v9 = 5.5f });
        var stale = Vector2.Distance(SpellMovement.PositionOf(me, world.GameNow), aim) < 0.01f;
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [aim.X, aim.Y], v7 = 90, v8 = [aim.X, aim.Y + 3], v9 = 5.5f, wb = L(fm, 2) });
        Check(stale && me.Movement is { Speed: 5.5f } m && Vector2.Distance(m.From, aim) < 0.01f && me.Effects.Forced is null
              && SpellMovement.KindOf("pull") == 4 && SpellMovement.KindOf("teleport") == 1,
              "a Move from the old spot without the echo is ignored; the echo Move at the aim is taken and ends the lock; pull is kind 4 (bkg.MovementTypes)");
    }

    private static void ShieldsAndChannels()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = arena.Join("Mage", new InventoryItem("T4_MAIN_FIRESTAFF"));
        var mob = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(1.5f, 0));
        var mark = peer.Events.Count;
        Hit(arena, me, T("ENERGY_BARRIER"), 2, 250, me.PositionAt(DateTime.UtcNow));
        var shield = Since(peer, mark).Where(e => e.Code == EventCodes.DamageShieldUpdate).ToList();
        Check(shield.Count == 1 && L(shield[0].P, 0) == me.Id && About(F(shield[0].P, 1), 192 * 2.5f),
              $"ENERGY_BARRIER: DamageShieldUpdate {{1 192 × Ap/100 = 480}} in the cast batch (got {Show(shield)})");
        mark = peer.Events.Count;
        var health = me.Health.At(world.GameNow);
        lock (world.Sync) Combat.DamagePlayer(mob, me, 100, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        var hit = Since(peer, mark);
        Check(!hit.Any(e => e.Code == EventCodes.HealthUpdate) && me.Health.At(world.GameNow) == health
              && hit.Single(e => e.Code == EventCodes.DamageShieldUpdate).P is var left && About(F(left, 1), 380),
              $"100 damage on the shield: no HealthUpdate, DamageShieldUpdate {{1 380}} (got {Show(hit)})");
        mark = peer.Events.Count;
        arena.Run(3.1);
        var gone = Since(peer, mark).Where(e => e.Code == EventCodes.DamageShieldUpdate).ToList();
        Check(gone.Count == 1 && !gone[0].P.ContainsKey(1) && L(gone[0].P, 0) == me.Id, $"at +3 s DamageShieldUpdate {{0}} only (got {Show(gone)})");
        // ENRAGE: every ReceiveDamage adds an ENRAGE_BUFF stack (its applyeffectoneventactive, count 7).
        var buff = T("ENRAGE_BUFF");
        Hit(arena, me, T("ENRAGE"), 3, 100, me.PositionAt(DateTime.UtcNow));
        var first = me.Effects.Find(buff.Id, me.Id)?.Stacks;
        lock (world.Sync)
            for (var i = 0; i < 2; i++) Combat.DamagePlayer(mob, me, 10, EffectType.Physical, HitOrigin.MeleeAutoAttack, -1, world.GameNow);
        Check(first == 1 && me.Effects.Find(buff.Id, me.Id) is { Stacks: 3 }, $"ENRAGE: ENRAGE_BUFF 1 stack, 3 after two hits (got {first}, {me.Effects.Find(buff.Id, me.Id)?.Stacks})");
        // PARRY: 5 ticks of 0.2 s, ChannelingEnded {1 3, 2 counter} 0.8 s after the cast end; its linked invincibility ends with it.
        var parry = T("PARRY");
        PendingCast Cast(byte counter)
        {
            var now = world.GameNow;
            return new PendingCast(parry, 0, counter, null, 100, null, me.PositionAt(DateTime.UtcNow), now, now, 0);
        }
        mark = peer.Events.Count;
        lock (world.Sync) SpellCasts.Finish(me, Cast(11));
        Check(SpellEffects.Invincible(me, world.GameNow) && me.Combat.Channel is not null, "PARRY channelling: invincible");
        arena.Run(0.85);
        var ended = Since(peer, mark).Where(e => e.Code == EventCodes.ChannelingEnded).ToList();
        Check(ended.Count == 1 && Convert.ToInt32(ended[0].P[1]) == 3 && Convert.ToByte(ended[0].P[2]) == 11 && me.Combat.Channel is null && !SpellEffects.Invincible(me, world.GameNow),
              $"ChannelingEnded {{1 3, 2 11}} 0.8 s after the cast end; the invincibility linked to it is off (got {Show(ended)})");
        lock (world.Sync) SpellCasts.Finish(me, Cast(12));
        mark = peer.Events.Count;
        var here = me.PositionAt(DateTime.UtcNow);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [here.X, here.Y], v7 = 0, v8 = [here.X, here.Y + 5], v9 = 5.5f });
        var cancelled = Since(peer, mark).Where(e => e.Code == EventCodes.ChannelingEnded).ToList();
        Check(cancelled.Count == 1 && Convert.ToInt32(cancelled[0].P[1]) == 2 && me.Combat.Channel is null,
              $"a walking Move cancels PARRY (allowmovement false): ChannelingEnded {{1 2}} (got {Show(cancelled)})");
    }

    /// <summary>
    /// ENFEEBLEAURA (togglespell, slot 4): its aura and buffs on, no cooldown while it is on; TerminateToggleSpell {1 4} takes them
    /// off and answers CastCancel {4 true, 5 4, 6 end, 7 now, 8 the 30 s span}; castable again after the cooldown; a second one ends
    /// by itself when its effects run out (+5 s after the hit). ChannelingCancel ends PARRY's channel with ChannelingEnded {1 2}.
    /// </summary>
    private static void Toggles()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = arena.Join("Toggler", new InventoryItem("T4_MAIN_FIRESTAFF"));
        var aura = T("ENFEEBLEAURA");
        const byte slot = 4;
        PendingCast Cast(SpellTemplate spell, byte s, byte counter)
        {
            var now = world.GameNow;
            return new PendingCast(spell, s, counter, null, 100, null, me.PositionAt(DateTime.UtcNow), now, now, 0);
        }
        lock (world.Sync) SpellCasts.Finish(me, Cast(aura, slot, 5));
        arena.Run(0.5); // its hit (hitdelay 0.4)
        Check(me.Combat.Toggles.ContainsKey(slot) && me.Effects.Find(aura.Id, me.Id) is not null && !me.Combat.Cooldowns.ContainsKey(aura.Id),
              "ENFEEBLEAURA on: listed, no cooldown while it is on");
        var mark = peer.Events.Count;
        Send(session, new TerminateToggleSpellRequest { wf = world.GameNow, agt = slot });
        var off = Since(peer, mark);
        var cancel = off.SingleOrDefault(e => e.Code == EventCodes.CastCancel).P;
        Check(cancel is not null && L(cancel, 0) == me.Id && cancel[4] is true && L(cancel, 5) == slot && L(cancel, 2) == 5
              && L(cancel, 6) - L(cancel, 7) == SpellCasts.Seconds(30) && L(cancel, 8) == SpellCasts.Seconds(30),
              $"TerminateToggleSpell: CastCancel {{0 me, 2 5, 4 true, 5 4, 6-7 30 s, 8 30 s}} (got {Show(off)})");
        Check(!me.Combat.Toggles.ContainsKey(slot) && me.Effects.Find(aura.Id, me.Id) is null && off.Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate),
              "the toggle is off: its entry gone, the list sent");
        int[] slots = [0, 0, 0, 0, aura.Id];
        CastRefusal Why() { lock (world.Sync) return SpellCasts.Check(me, slots, slot, aura.Id, 0, me.PositionAt(DateTime.UtcNow), 6, world.GameNow).Why; }
        var early = Why();
        arena.Run(30.1);
        Check(early == CastRefusal.Cooldown && Why() == CastRefusal.None, $"cooldown from the end: refused at once ({early}), castable after 30 s ({Why()})");
        lock (world.Sync) SpellCasts.Finish(me, Cast(aura, slot, 6));
        mark = peer.Events.Count;
        arena.Run(5.6);
        var expired = Since(peer, mark).Where(e => e.Code == EventCodes.CastCancel).ToList();
        Check(expired.Count == 1 && expired[0].P[4] is true && !me.Combat.Toggles.ContainsKey(slot), $"the toggle ends by itself at +5 s: CastCancel {{4 true}} (got {Show(expired)})");
        // ChannelingCancel: the client has left PARRY's channel.
        lock (world.Sync) SpellCasts.Finish(me, Cast(T("PARRY"), 0, 7));
        mark = peer.Events.Count;
        Send(session, new ChannelingCancelRequest { wf = world.GameNow });
        var ended = Since(peer, mark).Where(e => e.Code == EventCodes.ChannelingEnded).ToList();
        Check(ended.Count == 1 && Convert.ToInt32(ended[0].P[1]) == 2 && Convert.ToByte(ended[0].P[2]) == 7 && me.Combat.Channel is null,
              $"ChannelingCancel: ChannelingEnded {{1 2, 2 7}} (got {Show(ended)})");
        // Ended while a cast with a cast time runs: its CastCancel {4 true} waits for that cast's end (the client would abort it).
        lock (world.Sync) SpellCasts.Finish(me, Cast(aura, slot, 8));
        arena.Run(0.5);
        var bolt = Cast(T("FIRESTAFFBOLT2"), 0, 9) with { End = world.GameNow + SpellCasts.Seconds(8) };
        lock (world.Sync) SpellCasts.Start(me, bolt);
        mark = peer.Events.Count;
        Send(session, new TerminateToggleSpellRequest { wf = world.GameNow, agt = slot });
        var during = Since(peer, mark).Count(e => e.Code == EventCodes.CastCancel && e.P.ContainsKey(4));
        lock (world.Sync) SpellCasts.Cancel(me, bolt, interrupted: false);
        var after = Since(peer, mark).Count(e => e.Code == EventCodes.CastCancel && e.P.ContainsKey(4));
        Check(during == 0 && after == 1 && !me.Combat.Toggles.ContainsKey(slot), $"a toggle's CastCancel waits for the cast under way ({during}, then {after})");
        // The knockdown ends it and sends the list without it, though it took nothing else off.
        lock (world.Sync) SpellCasts.Finish(me, Cast(aura, slot, 10));
        arena.Run(0.5);
        mark = peer.Events.Count;
        lock (world.Sync) Knockdown.Start(me, me.Id, "test", world.GameNow);
        Check(!me.Combat.Toggles.ContainsKey(slot) && me.Effects.Find(aura.Id, me.Id) is null && Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate),
              "knocked down: the toggle off, the list without it sent");
    }
}
