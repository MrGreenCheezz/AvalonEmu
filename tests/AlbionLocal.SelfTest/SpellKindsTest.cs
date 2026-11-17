using System.Numerics;
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
/// StageR spell-effects (analysis/work/stageR/spell-effects/SPEC.md): the effect kinds added to the executor. Light: one check per kind
/// with the spells.xml numbers.
/// </summary>
public static class SpellKindsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("spell kinds: dismount (the transformation tome: off the ox, no mount for 5 s, MountCooldownUpdate)", Dismount),
        ("spell kinds: castspellafterdistancemoved (FROSTWALK: FROSTWALK_PULSE at once and every 1 s standing, 6 in its 5.5 s; 3 m moved casts at once)", DistanceMoved),
        ("spell kinds: targetsinarea (the panther: invisible after 1 s alone, a mob within 9 m breaks it)", TargetsInArea),
        ("spell kinds: removeeffectoneventactive (SHIELDFRIENDLY's shield used up ends its CCIMMUNITY); cannotuseobject, cannotlootplayers, transparency listed", EventRemovalAndBlocks),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static SpellTemplate T(string name) => Client.Spells!.Template(name) ?? throw new Exception($"{name} not in spells.xml");

    private static void Self(Player me, string spell)
    {
        var world = me.World;
        var t = T(spell);
        lock (world.Sync) SpellCasts.Execute(new SpellCasts.SpellContext(me, me.Map!, t, 1, 100, null, SpellMovement.PositionOf(me, world.GameNow), world.GameNow), t);
    }

    private static void Dismount()
    {
        using var arena = new Arena { Setup = c => c.Equipment[Mounts.MountSlot] = new InventoryItem("T5_MOUNT_OX") };
        var world = arena.World;
        var (session, peer, me) = arena.Join("Bookworm", new InventoryItem("T4_MAIN_SWORD"));
        Send(session, new MountRequest { wf = world.GameNow, v3 = Mounts.ItemOf(me)!.Id, v4 = true });
        arena.Run(10.5);
        Check(me.IsMounted, "mounted on the ox");
        var mark = peer.Events.Count;
        var now = world.GameNow;
        Self(me, "T8_SKILLBOOK_SPECIAL_NONTRADABLE_SPELL");
        Check(!me.IsMounted && me.Character.MountCooldownUntil == now + SpellCasts.Seconds(5)
              && Since(peer, mark).Any(e => e.Code == EventCodes.MountCooldownUpdate),
              $"the tome's dismount (cooldown 5 s outside the mists): off, mount blocked to now + 5 s (got {me.IsMounted}, {me.Character.MountCooldownUntil - now})");
        mark = peer.Events.Count;
        Self(me, "BUFFSHRINE_ESCAPE_DUNGEON");
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.DungonEscapeReady), "escapedungeon in the open world: no DungonEscapeReady");
    }

    private static void DistanceMoved()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, peer, me) = arena.Join("Frostwalker", new InventoryItem("T4_MAIN_SWORD"));
        var mark = peer.Events.Count;
        int Areas() => Since(peer, mark).Count(e => e.Code == EventCodes.NewSpellEffectArea);
        Self(me, "FROSTWALK");
        arena.Run(0.1);
        Check(Areas() == 1 && me.Effects.Spells.Any(s => s.Spell.Name == "FROSTWALK" && s.Subs[0] is { Tag: "castspellafterdistancemoved", Active: true }),
              $"firstcastonapply: one FROSTWALK_PULSE area at once, the effect listed Active (got {Areas()} areas)");
        arena.Run(7);
        Check(Areas() == 6, $"standing: one more every castspellintervalnotenoughmovement 1 s, none after the 5.5 s (got {Areas()})");

        // Moving: 3 m since the last cast casts at once, before the 1 s.
        mark = peer.Events.Count;
        Self(me, "FROSTWALK");
        arena.Run(0.1);
        lock (world.Sync)
        {
            me.Teleport(SpellMovement.PositionOf(me, world.GameNow) + new Vector2(3.5f, 0), 0, DateTime.UtcNow);
        }
        arena.Run(0.3);
        Check(Areas() == 2, $"3.5 m moved within 0.4 s: the second pulse already (got {Areas()})");
    }

    private static void TargetsInArea()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, _, me) = arena.Join("Panther", new InventoryItem("T4_MAIN_SWORD"));
        var away = arena.Stand + new Vector2(0, -40);
        lock (world.Sync)
        {
            me.Teleport(away, 0, DateTime.UtcNow);
        }
        bool Invisible() => me.Effects.Spells.Any(s => s.Spell.Name == "PASSIVE_SHAPE_PANTHER_INVIS" && s.Live(world.GameNow));
        bool alone;
        lock (world.Sync) alone = !SpellWatchers.AnyIn(me.Map!, me, SpellTargets.Cls.EnemyAliveOrKnockedDownNotPassive, 10, world.GameNow);
        Check(alone, "no enemy within 10 m of the test spot");
        Self(me, "PASSIVE_PANTHER_INVIS_TRIGGER_EFFECT");
        arena.Run(1.5);
        Check(Invisible(), "alone: the pulse spell, PASSIVE_SHAPE_PANTHER_INVIS after 1 s");
        arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", away - arena.Stand + new Vector2(0, 5));
        arena.Run(0.7);
        Check(!Invisible() && me.Effects.Spells.Any(s => s.Spell.Name == "PASSIVE_PANTHER_INVIS_BREAK"),
              "a mob 5 m off: PASSIVE_PANTHER_INVIS_BREAK takes the invisibility off and blocks it");
    }

    private static void EventRemovalAndBlocks()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, _, me) = arena.Join("Shielded", new InventoryItem("T4_MAIN_ARCANESTAFF"));
        var mob = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(0, 30));
        Self(me, "SHIELDFRIENDLY");
        var entry = me.Effects.Spells.Single(s => s.Spell.Name == "SHIELDFRIENDLY");
        long now;
        lock (world.Sync) now = world.GameNow;
        Check(entry.On(entry.Subs[0], now) && entry.On(entry.Subs[1], now), "SHIELDFRIENDLY: shield and forced-movement immunity on");
        double left;
        lock (world.Sync) left = SpellEffects.Absorb(world, me, 100000, now, mob, HitOrigin.Direct, 0);
        Check(left > 0 && !entry.On(entry.Subs[0], now) && !entry.On(entry.Subs[1], now) && !SpellEffects.CcImmune(me, "knockback", now),
              "the shield used up (TerminatedPrematurely): CCIMMUNITY ends with it");

        bool blocked, noLoot;
        lock (world.Sync)
        {
            Pvp.ApplySpell(me, "OUTLANDS_TELEPORTATION_PORTAL_BLOCKER_DEBUFF", world.GameNow);
            Pvp.ApplySpell(me, "DISMOUNTED_HOSTILE", world.GameNow);
            blocked = Pvp.SpellActive(me, "OUTLANDS_TELEPORTATION_PORTAL_BLOCKER_DEBUFF", world.GameNow);
            noLoot = SpellEffects.CannotLootPlayers(me, world.GameNow);
        }
        Check(blocked && noLoot, "cannotuseobject listed: the safe portal's 30 s blocker holds; DISMOUNTED_HOSTILE's cannotlootplayers holds");
        arena.Run(5.5);
        lock (world.Sync) noLoot = SpellEffects.CannotLootPlayers(me, world.GameNow);
        Check(!noLoot, "cannotlootplayers ends after its 5 s");
        Check(SpellEffects.Listed.Contains("transparency"), "transparency is listed (the client's own effect)");
        var tags = Client.Spells!.EffectTags();
        var run = tags.Keys.Count(t => SpellCasts.Supported.Contains(t) && !SpellCasts.NoOps.Contains(t));
        Check(run >= 95, $"the start-up report: {run} of {tags.Count} kinds run (82 before stageR)");
    }
}
