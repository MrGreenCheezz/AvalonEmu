using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageD effects batch 3 (analysis/work/stageD/effects/SPEC.md §7) and the rest of the effect kinds that act in a fight: chainspell, the
/// area removals of an owner and around a target, the shapeshifter forms and their end, setcooldowns, cancelmultispell, CLEANSE's
/// setcrowdcontroldiminishingreturns with IfTargetCCEffect, gainenergyondamage, the mob strength buffs, notinterruptible, cannotaggro,
/// removefromthreatlist and weightbonusovertime. Light: the rules with the spells.xml numbers.
/// </summary>
public static class EffectsBatch3Test
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("effects batch 3: ARCANE_CHAIN_MISSILE's chain marks 3 mobs 0.25 s apart (unique, 7 m); removespelleffectareasoftarget / affectingtarget take a FIREWALL off", ChainsAndAreas),
        ("effects batch 3: SHAPESHIFT_PANTHER listed 12 s, SHAPESHIFT_HUMAN ends it; setcooldowns 15 s; cancelmultispell (457 at now); CLEANSE sets the stun DR to 0.3", FormsSlotsCleanse),
        ("effects batch 3: ENERGYSHIELD2 +16 energy per hit, MOB_OPENWORLD_BUFF_Q4 × 1.15 damage and health, notinterruptible, cannotaggro, removefromthreatlist, the timed fiber weight", UnitStates),
        ("spells-bosses: AXETHROW_EFFECT -160 -> -220 with 2 AXE_BLEED (IfCharge), IfTargetHealthPercentage, IfChannelTick modulo, IfTargetMobCategory/Tier, a gathering tome, a fame buff", ConditionsAndTomes),
        ("spells2: AFTER_IMAGE stores its area's centre (tag IMAGE); AFTER_IMAGE_RETURN (storedtarget) is cast there, teleports back and removes it", StoredTargets),
    ];

    private static void StoredTargets()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, _, me) = arena.Join("Imager", new InventoryItem("T4_MAIN_SWORD"));
        var map = world.Maps.Find("0204")!;
        var image = T("AFTER_IMAGE");
        var back = T("AFTER_IMAGE_RETURN");
        var home = me.PositionAt(DateTime.UtcNow);
        lock (world.Sync) SpellCasts.Execute(new SpellCasts.SpellContext(me, map, image, 1, 100, null, home + new Vector2(5, 0), world.GameNow), image);
        var stored = me.Combat.Stored.SingleOrDefault();
        Check(stored is { Tag: "IMAGE", Unit: null } && Vector2.Distance(stored.Point, home) < 0.1f && back.StoredTargetTag == "IMAGE",
              $"storetargets ground/spelleffectarea: the image's centre where it was cast, tag IMAGE (got {stored})");
        arena.Run(0.5); // its own dash, 5 m towards the aim
        var away = Vector2.Distance(me.PositionAt(DateTime.UtcNow), home);
        (PendingCast? Cast, CastRefusal Why) check;
        lock (world.Sync)
        {
            me.Combat.Multi[5] = new MultiSlot(back, image, world.GameNow + SpellCasts.Seconds(6), true);
            check = SpellCasts.Check(me, new int[14], 5, back.Id, 0, default, 2, world.GameNow);
            if (check.Cast is { } cast) SpellCasts.Start(me, cast);
        }
        arena.Run(0.5);
        Check(check is { Why: CastRefusal.None, Cast.Aim: var aim } && Vector2.Distance(aim, home) < 0.1f && Vector2.Distance(me.PositionAt(DateTime.UtcNow), home) < 0.5f
              && me.Combat.Stored.Count == 0 && away > 4,
              $"AFTER_IMAGE_RETURN at the stored point: back home from {away:0.#} m ({Vector2.Distance(me.PositionAt(DateTime.UtcNow), home):0.##} m off), removestoredtargets ({me.Combat.Stored.Count} left)");
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static SpellTemplate T(string name) => Client.Spells!.Template(name) ?? throw new Exception($"{name} not in spells.xml");

    private static SpellCasts.SpellContext Ctx(Player me, Map map, SpellTemplate spell, Mob? target, World world) =>
        new(me, map, spell, 1, 100, target, target?.Position ?? SpellMovement.PositionOf(me, world.GameNow), world.GameNow);

    private static void ChainsAndAreas()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, peer, me) = arena.Join("Chainer", new InventoryItem("T4_MAIN_FIRESTAFF"));
        var map = world.Maps.Find("0204")!;
        var a = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(0, 6));
        var b = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(0, 11));
        var c = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(0, 16));
        var far = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(0, 30));
        var chain = T("ARCANE_CHAIN_MISSILE_EFFECT");
        var mark = T("ARCANE_CHAIN_MISSILE_MARK");
        var t0 = world.GameNow;
        lock (world.Sync) SpellCasts.Execute(Ctx(me, map, chain, a, world), chain);
        arena.Run(1);
        ActiveSpell? On(Mob m) => m.Effects.Find(mark.Id, me.Id);
        Check(On(a)?.Start == t0 && On(b)?.Start == t0 + SpellCasts.Seconds(0.25) && On(c)?.Start == t0 + SpellCasts.Seconds(0.5) && On(far) is null,
              $"maxcount 3, 7 m, 0.25 s: the mark on the target, then the next two, none 14 m on (got {On(a)?.Start - t0}, {On(b)?.Start - t0}, {On(c)?.Start - t0}, {On(far)})");

        // removespelleffectareasoftarget: the owner's FIREWALL by its holding spell, an areatag filter that fits none keeps it.
        var wall = T("FIREWALL");
        lock (world.Sync) SpellCasts.Execute(new SpellCasts.SpellContext(me, map, wall, 2, 100, null, arena.Stand + new Vector2(3, 0), world.GameNow), wall);
        var area = map.Objects.Values.OfType<SpellArea>().Single();
        var mark2 = peer.Events.Count;
        int other, tagged, removed;
        lock (world.Sync)
        {
            other = SpellAreas.RemoveOf(me, "FIREBALL", null);
            tagged = SpellAreas.RemoveOf(me, "FIREWALL", "GROUNDBURN");
            removed = SpellAreas.RemoveOf(me, "FIREWALL", null);
        }
        Check(other == 0 && tagged == 0 && removed == 1 && area.Gone && Since(peer, mark2).Any(e => e.Code == EventCodes.Leave && L(e.P, 0) == area.Id),
              "another spell or areatag leaves the wall; spell FIREWALL takes it off (Leave)");

        // removespelleffectareasaffectingtarget ownedby caster: the caster's area that touches the target within effectarearadius.
        lock (world.Sync) SpellCasts.Execute(new SpellCasts.SpellContext(me, map, wall, 3, 100, null, arena.Stand + new Vector2(3, 0), world.GameNow), wall);
        var second = map.Objects.Values.OfType<SpellArea>().Single(x => !x.Gone);
        XElement Node(string? spell, float radius) => new("removespelleffectareasaffectingtarget", new XAttribute("target", "enemy"), new XAttribute("ownedby", "caster"),
            new XAttribute("effectarearadius", radius), spell is null ? null : new XAttribute("spell", spell));
        int away, near;
        lock (world.Sync)
        {
            away = SpellAreas.RemoveAffecting(me, far, Node(null, 1));
            near = SpellAreas.RemoveAffecting(me, far, Node("FIREWALL", 60));
        }
        Check(away == 0 && near == 1 && second.Gone, "a target 1 m off the wall keeps it; within 60 m of it the wall goes");
    }

    private static void FormsSlotsCleanse()
    {
        using var arena = new Arena();
        var world = arena.World;
        var e = Client.Spells!.Template(Client.Spells.SlotsOf("T4_MAIN_FIRESTAFF")!.Active[2][0])!;
        var (_, peer, me) = arena.Join("Shifter", new InventoryItem("T4_MAIN_FIRESTAFF") { Active = [null, null, e.Name] });
        var map = world.Maps.Find("0204")!;
        var panther = T("SHAPESHIFT_PANTHER");
        var mark = peer.Events.Count;
        lock (world.Sync) SpellCasts.Execute(Ctx(me, map, panther, null, world), panther);
        var form = me.Effects.Find(panther.Id, me.Id);
        Check(form is { Subs: [{ Tag: "transformation", Active: true, DurationMs: 12000 }] }
              && Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && e.P[1] is short[] ids && ids.Contains(panther.Id)),
              $"SHAPESHIFT_PANTHER: the transformation listed for 12 s (got {form})");
        var human = T("SHAPESHIFT_HUMAN");
        mark = peer.Events.Count;
        lock (world.Sync) SpellCasts.Execute(Ctx(me, map, human, null, world), human);
        Check(me.Effects.Find(panther.Id, me.Id) is null && Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == me.Id),
              "SHAPESHIFT_HUMAN's endtransformation takes the form off, the list goes out at once");

        // setcooldowns: offhandormainhand3 15 s from now, whether it ran or not.
        var penalty = T("PANTHER_CLAWS_COOLDOWN_PENALTY");
        mark = peer.Events.Count;
        var now = world.GameNow;
        lock (world.Sync) SpellCasts.Execute(Ctx(me, map, penalty, null, world), penalty);
        var update = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.SpellCooldownUpdate).P;
        Check(me.Combat.Cooldowns.GetValueOrDefault(e.Id) == now + SpellCasts.Seconds(15) && update is not null && update[0] is byte[] and [2],
              $"setcooldowns: slot 2's {e} ready 15 s from now, SpellCooldownUpdate {{0 [2]}} (got {Show(Since(peer, mark).Where(e => e.Code == EventCodes.SpellCooldownUpdate))})");

        // cancelmultispell offhandormainhand3 (AIR_RAID_CLEANUP): the window ends now, 457 {0 the holder, 2 now}.
        var kite = T("SPEEDARCHER_KITE");
        lock (world.Sync) me.Combat.Multi[2] = new MultiSlot(T("SPEEDARCHER_KITE_MULTI_DASH"), kite, world.GameNow + SpellCasts.Seconds(8), true);
        var cleanup = T("AIR_RAID_CLEANUP");
        mark = peer.Events.Count;
        now = world.GameNow;
        lock (world.Sync) SpellCasts.Execute(Ctx(me, map, cleanup, null, world), cleanup);
        var replace = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.ReplaceSpellSlotWithMultiSpell).P;
        Check(!me.Combat.Multi.ContainsKey(2) && replace is not null && Convert.ToInt16(replace[0]) == kite.Id && L(replace, 2) == now,
              "cancelmultispell: slot 2's window is gone, ReplaceSpellSlotWithMultiSpell {0 SPEEDARCHER_KITE, 2 now}");

        // CLEANSE on a stunned player: its stun diminishing return level is set to 0.3 (IfTargetCCEffect stun); no root, so none set.
        var stunSpell = Enumerable.Range(0, Client.Spells!.Count).Select(i => Client.Spells.Template(i))
            .First(s => s is { Kind: "activespell" } && s.Effects.Count == 1 && s.Effects[0].Name.LocalName == "stun" && SpellTemplate.Float(s.Effects[0], "time") > 0)!;
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(2, 0));
        lock (world.Sync)
        {
            var sub = SpellEffects.Make(world, ghoul, 100, 100, stunSpell.Effects[0], 0, me, world.GameNow);
            SpellEffects.Apply(world, me, stunSpell, ghoul.Id, 100, 100, world.GameNow, [sub]);
            SpellEffects.SetDiminishing(me, new XElement("setcrowdcontroldiminishingreturns", new XAttribute("type", "DRStun"), new XAttribute("value", "0.8")), world.GameNow);
        }
        Check(SpellEffects.Stunned(me, world.GameNow), $"{stunSpell} stuns the player");
        var cleanse = T("CLEANSE");
        lock (world.Sync) SpellCasts.Execute(Ctx(me, map, cleanse, null, world), cleanse);
        var dr = SpellEffects.Diminishing(world, me, "stun", world.GameNow);
        Check(Math.Abs(dr - 0.3) < 1e-6 && SpellEffects.Diminishing(world, me, "root", world.GameNow) == 0, $"CLEANSE: the stun DR level 0.8 → 0.3, no root DR (got {dr})");
    }

    private static void UnitStates()
    {
        using var arena = new Arena(ai: true);
        var world = arena.World;
        arena.Setup = c => c.Inventory = [new InventoryItem("T4_FIBER", 100)];
        var (_, peer, me) = arena.Join("Shield", new InventoryItem("T4_MAIN_FIRESTAFF"));
        var map = world.Maps.Find("0204")!;
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(40, 0));

        // ENERGYSHIELD2: every hit taken while it lasts gives 16 energy (EnergyUpdate {2 +16, 4 2}).
        var shield = T("ENERGYSHIELD2");
        lock (world.Sync)
        {
            SpellCasts.Execute(Ctx(me, map, shield, null, world), shield);
            me.Energy.Set(me.Energy.Max / 2, world.GameNow);
        }
        var before = me.Energy.At(world.GameNow);
        var mark = peer.Events.Count;
        lock (world.Sync) Combat.DamagePlayer(ghoul, me, 10, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        var energy = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.EnergyUpdate).P;
        Check(energy is not null && F(energy, 2) == 16 && Convert.ToByte(energy[4]) == 2 && Convert.ToInt16(energy[6]) == shield.Id && Math.Abs(me.Energy.At(world.GameNow) - before - 16) < 0.5,
              $"a hit under ENERGYSHIELD2: +16 energy (got {Show(Since(peer, mark).Where(e => e.Code == EventCodes.EnergyUpdate))})");

        // MOB_OPENWORLD_BUFF_Q4 (its conditions aside): the mob deals and has × 1.15.
        var buff = T("MOB_OPENWORLD_BUFF_Q4");
        var now = world.GameNow;
        var max = ghoul.Health.Max;
        var share = ghoul.Health.At(now) / max;
        lock (world.Sync)
        {
            SubEffect?[] subs = [.. buff.Effects.Select((n, i) => SpellEffects.Make(world, ghoul, 100, 100, n, i, ghoul, now))];
            SpellEffects.Apply(world, ghoul, buff, ghoul.Id, 100, 100, now, subs);
            SpellEffects.Refresh(world, ghoul);
        }
        Check(Math.Abs(SpellEffects.DealtFactor(ghoul, spell: false, EffectType.Physical, vsPlayer: true, now) - 1.15) < 1e-6 && ghoul.Health.Max == MathF.Round(max * 1.15f)
              && Math.Abs(ghoul.Health.At(world.GameNow) / ghoul.Health.Max - share) < 0.01,
              $"mobdamagemodifier / mobhealthmodifier 1.15: damage × 1.15, health {max} → {ghoul.Health.Max}, its share kept ({share:0.00} → {ghoul.Health.At(world.GameNow) / ghoul.Health.Max:0.00})");

        // notinterruptible (PASSIVE_HOLY_ASCENDED_EFFECT 2 s), cannotaggro (MOB_WATCHTOWER_COLLECTER_PROTECTION).
        var ascended = T("PASSIVE_HOLY_ASCENDED_EFFECT");
        var protection = T("MOB_WATCHTOWER_COLLECTER_PROTECTION");
        lock (world.Sync)
        {
            SpellCasts.Execute(Ctx(me, map, ascended, null, world), ascended);
            MobCasts.Execute(new MobCasts.MobHit(ghoul, map, protection, 0, 100, ghoul, ghoul.Position, world.GameNow), protection);
        }
        Check(SpellEffects.NotInterruptible(me, world.GameNow) && SpellEffects.CannotAggro(ghoul, world.GameNow) && !SpellEffects.CannotAggro(me, world.GameNow),
              "notinterruptible on the player, cannotaggro on the mob");

        // removefromthreatlist target allplayers (AGGROSWITCH_DEMONBOSS): the mob's threat list is empty.
        var other = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(45, 0));
        var demon = T("AGGROSWITCH_DEMONBOSS");
        lock (world.Sync)
        {
            other.Threat[me] = 5;
            MobCasts.Execute(new MobCasts.MobHit(other, map, demon, 0, 100, me, SpellMovement.PositionOf(me, world.GameNow), world.GameNow), demon);
        }
        Check(other.Threat.Count == 0, "removefromthreatlist allplayers: off the threat list");

        // weightbonusovertime (ASIA_BETA_BUFF): fiber weighs 1 − 0.66 of its weight.
        var load = Server.Systems.Passives.Load(me);
        var beta = T("ASIA_BETA_BUFF");
        lock (world.Sync) SpellCasts.Execute(Ctx(me, map, beta, null, world), beta);
        var fiber = Server.Systems.Passives.WeightOf(world.Items.Find("T4_FIBER")!);
        var after = Server.Systems.Passives.Load(me);
        Check(Math.Abs(load - after - 100 * fiber * 0.66) < 1e-4, $"100 T4_FIBER: {load:0.##} kg → {after:0.##} (− 100 × {fiber} × 0.66)");
    }

    private static void ConditionsAndTomes()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, _, me) = arena.Join("Conditioner", new InventoryItem("T4_MAIN_AXE"), ("GATHER_ORE_T5", 0));
        var map = world.Maps.Find("0204")!;
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(0, 4));
        var throwHit = T("AXETHROW_EFFECT").Effects.First(e => e.Name.LocalName == "directattributechange");
        var bleed = T("AXE_BLEED");
        var f = new SpellConditions.Facts(me, ghoul, ghoul, map, world.GameNow, 0);
        SubEffect?[] Subs() => [.. bleed.Effects.Select((n, i) => SpellEffects.Make(world, me, 100, 100, n, i, ghoul, world.GameNow))];
        float plain, two;
        lock (world.Sync)
        {
            plain = SpellConditions.Value(throwHit, "change", f);
            SpellEffects.Apply(world, ghoul, bleed, me.Id, 100, 100, world.GameNow, Subs());
            SpellEffects.Apply(world, ghoul, bleed, me.Id, 100, 100, world.GameNow, Subs());
            two = SpellConditions.Value(throwHit, "change", f);
        }
        Check(plain == -160 && two == -220, $"valueoverride IfCharge: {plain} without stacks, {two} with 2 AXE_BLEED stacks of the caster");
        ghoul.Health.Set(ghoul.Health.Max * 0.3f, world.GameNow);
        XElement C(string tag, params object[] attrs) => new(tag, attrs);
        bool H(XElement e, int tick = 0) => SpellConditions.Holds(e, f with { Ticks = world.GameNow, ChannelTick = tick }) == true;
        Check(H(C("IfTargetHealthPercentage", new XAttribute("op", "less"), new XAttribute("value", "0.4")))
              && !H(C("IfTargetHealthPercentage", new XAttribute("op", "greaterOrEqual"), new XAttribute("value", "0.4")))
              && H(C("IfChannelTick", new XAttribute("modulo", "2"), new XAttribute("op", "equals"), new XAttribute("value", "1")), 3)
              && !H(C("IfChannelTick", new XAttribute("op", "equals"), new XAttribute("value", "1")))
              && H(C("IfTargetMobCategory", new XAttribute("category", "roaming"))) && H(C("IfTargetMobTier", new XAttribute("op", "equals"), new XAttribute("value", "4")))
              && H(C("IfTargetMobFaction", new XAttribute("op", "notequals"), new XAttribute("value", "CRITTER"))),
              "a ghoul at 30 %: below 40 %, tick 3 is odd, no tick outside a channel, category roaming, tier 4, not a critter");
        // A gathering tome: its node only; a fame buff of another category leaves a roaming kill at × 1.
        var node = world.GameData!.Board!.Find("GATHER_ORE_T5")!;
        var tome = new XElement("adddestinyboardgatheringfame", new XAttribute("target", "self"), new XAttribute("achievementid", "GATHER_ORE_T5"), new XAttribute("amount", "1000"));
        var solo = T("DYNAMIC_EVENT_SOLO_DUNGEONS");
        double roaming;
        lock (world.Sync)
        {
            Consumables.Award(Ctx(me, map, solo, null, world), tome);
            SpellCasts.Execute(Ctx(me, map, solo, null, world), solo);
            roaming = Combat.FameBuff(world, me, ghoul, map);
        }
        var progress = me.Character.Progression!.Progress.GetValueOrDefault(node.Id) + 10000L * me.Character.Progression.Levels.GetValueOrDefault(node.Id);
        Check(progress > 0 && me.Effects.Has("mobfamebycategoryovertime", world.GameNow) && roaming == 1,
              $"GATHER_ORE_T5 took the tome's fame ({progress}); DYNAMIC_EVENT_SOLO_DUNGEONS listed, × {roaming} on a roaming ghoul (rd_solo only)");
    }
}
