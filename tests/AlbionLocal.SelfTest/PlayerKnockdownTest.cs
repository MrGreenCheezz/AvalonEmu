using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage 5e (analysis/work/stage5/SPEC.md §5e.3): the PvE knockdown in 0204 on a <see cref="ManualClock"/> with FakePeers. A
/// soldier veteran hits a player to 0: the lethal HealthUpdate without key 3, the knockdown regeneration (max / 600),
/// InCombatStateUpdate {}, KnockedDown with the mob, the mob's reset, then max / 18 once the fight is over; while down Move
/// gets 881, AttackStart and CastStart are refused and no mob swings at it; 18 s later Respawn in place with full bars, 3 s
/// later the SPAWN_KNOCKDOWN_INVULNERABILITY buff (995), allowing walking until an attack or expiry. .kill, .heal and a rejoin while down.
/// </summary>
public static class PlayerKnockdownTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("knockdown: a soldier knocks the player down, Respawn after 18 s, buff 995 permits walking and ends on attack", MobKnockdown),
        ("knockdown: get-up animation and walking protection prevent immediate mob focus and damage, then expire", GetUpProtection),
        ("knockdown: .kill and .heal on oneself, .damage; leaving while down and joining again stands at the spot with full bars", CommandsAndRejoin),
        ("knockdown: the player's DoT and a bolt in flight do nothing once it is down (no combat, no regen 0), an early walk keeps the buff, no harvest or loot while down", EffectsAfterKnockdown),
    ];

    private const string Soldier = "T4_MOB_UNDEAD_SOLDIER_VETERAN";

    private static (EventCodes Code, Dictionary<byte, object?> P) First(List<(EventCodes Code, Dictionary<byte, object?> P)> events, EventCodes code, long id) =>
        events.First(e => e.Code == code && L(e.P, 0) == id);

    private static void MobKnockdown()
    {
        using var arena = new Arena(ai: true);
        var world = arena.World;
        var (session, peer, me) = arena.Join("Fallen", new InventoryItem("T4_MAIN_SWORD"));
        var (_, watcherPeer, _) = arena.Join("Watcher", new InventoryItem("T4_MAIN_SWORD"));
        var map = world.Maps.Find("0204")!;
        lock (world.Sync)
            foreach (var m in map.Objects.Values.OfType<Mob>().Where(m => Vector2.Distance(m.Position, arena.Stand) < 150).ToList())
                SpawnManager.Despawn(m, world, respawn: false);
        var soldier = arena.Spawn(Soldier, new Vector2(5, 0));
        var max = me.Health.Max;
        // One swing is enough: the player is at 10 health.
        lock (world.Sync) me.Health.Set(10, Math.Max(world.GameNow, me.Health.Stamp));
        var mark = peer.Events.Count;
        var watcherMark = watcherPeer.Events.Count;
        for (var i = 0; i < 400 && me.Combat.Down is null; i++) arena.Run(0.05);
        var downAt = world.Now;
        Check(me.Combat.Down is { } down && down.Killer == soldier.Id, $"the soldier knocks the player down ({soldier.AI.Describe()})");

        // 1. The order of the knockdown (SPEC §5e.1).
        var events = Since(peer, mark).Where(e => e.Code != EventCodes.Move).ToList();
        var lethal = events.FindIndex(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == me.Id && L(e.P, 6) == soldier.Id && !e.P.ContainsKey(3));
        var order = events.Skip(lethal).Where(e => L(e.P, 0) == me.Id).Select(e => e.Code).Take(5).ToList();
        Check(lethal >= 0 && order.SequenceEqual([EventCodes.HealthUpdate, EventCodes.RegenerationHealthChanged, EventCodes.RegenerationPlayerComboChanged,
                  EventCodes.InCombatStateUpdate, EventCodes.KnockedDown]),
              $"lethal HealthUpdate without key 3, RegenerationHealthChanged, RegenerationPlayerComboChanged, InCombatStateUpdate, KnockedDown (got {string.Join(", ", order)})");
        var after = events.Skip(lethal).ToList();
        var regen = First(after, EventCodes.RegenerationHealthChanged, me.Id).P;
        var combo = First(after, EventCodes.RegenerationPlayerComboChanged, me.Id).P;
        var state = First(after, EventCodes.InCombatStateUpdate, me.Id).P;
        var kd = First(after, EventCodes.KnockedDown, me.Id).P;
        Check(!regen.ContainsKey(2) && F(regen, 3) == max && Math.Abs(F(regen, 5) - max / 600f) < 1e-3 && Math.Abs(F(combo, 5) - max / 600f) < 1e-3,
              $"the knockdown regeneration max × 1 / 600 = {max / 600f:0.####} in 91 (no key 2) and 96 (got {Show([(EventCodes.RegenerationHealthChanged, regen)])})");
        var tag = soldier.Template.NameLocaTag ?? soldier.Template.Name;
        Check(state.Keys.Where(k => k < 250).SequenceEqual([(byte)0]) && L(kd, 2) == soldier.Id && (string)kd[3]! == (tag.StartsWith('@') ? tag : "@" + tag)
              && L(kd, 4) == me.Id && (string)kd[5]! == "Fallen" && !kd.ContainsKey(6) && !kd.ContainsKey(7),
              $"InCombatStateUpdate {{0 me}} and KnockedDown {{2 mob, 3 \"@\" + namelocatag, 4 me, 5 name}} (got {Show([(EventCodes.KnockedDown, kd)])})");
        var seen = Since(watcherPeer, watcherMark);
        Check(seen.Any(e => e.Code == EventCodes.KnockedDown && L(e.P, 0) == me.Id) && seen.Any(e => e.Code == EventCodes.RegenerationPlayerComboChanged && L(e.P, 0) == me.Id),
              "the other player sees KnockedDown and the regeneration combo");
        // The mob resets (its threat is gone) and the combat is over: max / 18.
        var reset = after.FindIndex(e => e.Code == EventCodes.RegenerationHealthEnergyComboChanged && L(e.P, 0) == soldier.Id);
        var overRegen = after.FindLastIndex(e => e.Code == EventCodes.RegenerationHealthChanged && L(e.P, 0) == me.Id);
        Check(reset > after.FindIndex(e => e.Code == EventCodes.KnockedDown) && F(after[reset].P, 5) == 699 && soldier.AI.State == AlbionLocal.Server.AI.MobState.Returning
              && overRegen > reset && Math.Abs(F(after[overRegen].P, 5) - max / 18f) < 1e-2 && me.Combat.Down!.CombatOver == downAt,
              $"then the mob's reset (95 {{5: 699}}), and the combat over at once: RegenerationHealthChanged max / 18 = {max / 18f:0.##}");
        arena.Run(0.25);
        Check(Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == soldier.Id && e.P[1] is short[] and [1063]),
              "the mob's return buff (ActiveSpellEffectsUpdate [1063])");

        // 2. While down: Move 881, AttackStart and CastStart refused, no mob swings at it.
        var responses = peer.Responses.Count;
        var here = me.PositionAt(DateTime.UtcNow);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [here.X + 3, here.Y], v7 = 90, v8 = [here.X + 6, here.Y], v9 = 5.5f });
        Send(session, new AttackStartRequest { wf = world.GameNow, co = soldier.Id });
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = 1, d4 = 1 });
        Check(peer.Responses.Count == responses + 3 && peer.Responses[responses].ReturnCode == Knockdown.MoveRefused
              && peer.Responses[responses + 1].ReturnCode == WorldSession.AttackInvalidTarget && peer.Responses[responses + 2].ReturnCode == WorldSession.CastRefused
              && me.PositionAt(DateTime.UtcNow) == here,
              $"Move rc 881 (not moved), AttackStart -501, CastStart 960 (got {string.Join(", ", peer.Responses.Skip(responses).Select(r => r.ReturnCode))})");
        mark = peer.Events.Count;
        lock (world.Sync) me.Teleport(soldier.Home + new Vector2(2, 0), 0, DateTime.UtcNow); // right next to its home, inside aggroradius
        arena.Run(5);
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.Attack && L(e.P, 2) == me.Id) && soldier.TargetId != me.Id && me.Combat.Down is not null,
              "while down no mob aggroes or swings at it, even inside the aggroradius");

        // 3. 18 s after the combat ended: Respawn in place with full bars, the regular regeneration; 3 s later the buff.
        var spot = me.PositionAt(DateTime.UtcNow);
        mark = peer.Events.Count;
        while (me.Combat.Down is not null && world.Now - downAt < TimeSpan.FromSeconds(30)) arena.Run(0.05);
        var upAfter = (world.Now - downAt).TotalSeconds;
        var up = Since(peer, mark);
        var respawn = First(up, EventCodes.Respawn, me.Id).P;
        Check(upAfter >= 18 && upAfter < 18.1 && F(respawn, 4) == max && F(respawn, 5) == max && F(respawn, 9) == me.Energy.Max && F(respawn, 10) == me.Energy.Max
              && respawn[2] is float[] pos && new Vector2(pos[0], pos[1]) == spot && respawn[14] is true && respawn[15] is true && F(respawn, 7) == me.ComputeStats().HealthRegen,
              $"Respawn {upAfter:0.##} s after KnockedDown {{2 the same spot, 4/5 max, 7 regular regen, 9/10 max energy, 14 true, 15 true}} (got {Show([(EventCodes.Respawn, respawn)])})");
        Check(up.SkipWhile(e => e.Code != EventCodes.Respawn).Any(e => e.Code == EventCodes.RegenerationHealthChanged && L(e.P, 0) == me.Id && F(e.P, 5) == me.ComputeStats().HealthRegen)
              && me.Health.At(world.GameNow) == max && Since(watcherPeer, watcherMark).Any(e => e.Code == EventCodes.Respawn && L(e.P, 0) == me.Id),
              "then RegenerationHealthChanged with the regular rate; full health; the other player sees the Respawn");
        mark = peer.Events.Count;
        arena.Run(2.9);
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.CastSpell && L(e.P, 0) == me.Id), "no buff before getuptimeseconds (3 s)");
        arena.Run(0.15);
        var buff = Since(peer, mark);
        var cast = First(buff, EventCodes.CastSpell, me.Id).P;
        var list = First(buff, EventCodes.ActiveSpellEffectsUpdate, me.Id).P;
        Check(world.Spells!.IdOf(Knockdown.BuffSpell) == Knockdown.DefaultBuffId && L(cast, 1) == me.Id && Convert.ToInt16(cast[3]) == 995 && Convert.ToByte(cast[6]) == 0
              && list[1] is short[] and [995] && me.Combat.InvulnerableUntil is not null,
              $"3 s later CastSpell {{0 me, 1 me, 3 995, 6 0}} and ActiveSpellEffectsUpdate [995] (got {Show([(EventCodes.CastSpell, cast), (EventCodes.ActiveSpellEffectsUpdate, list)])})");
        // Invulnerable: the soldier (home again, the player inside its aggroradius) leaves it alone.
        arena.Run(2);
        Check(soldier.TargetId != me.Id && !soldier.Threat.ContainsKey(me), $"invulnerable: no aggro on it ({soldier.AI.Describe()})");
        // 4. The authored buff permits walking; an attack still ends it.
        mark = peer.Events.Count;
        here = me.PositionAt(DateTime.UtcNow);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [here.X, here.Y], v7 = 90, v8 = [here.X + 20, here.Y], v9 = 5.5f });
        Check(me.Combat.InvulnerableUntil is not null && me.Combat.Untouchable
            && !Since(peer, mark).Any(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == me.Id),
            "walking preserves the protection and does not clear its effects");
        Knockdown.Acted(me, "attacks");
        var cleared = Since(peer, mark).Where(e => e.Code == EventCodes.ActiveSpellEffectsUpdate && L(e.P, 0) == me.Id).Select(e => e.P).ToList();
        Check(cleared is [var c] && c[3] is float[] { Length: 0 } && c[7] is byte[] and [0] && me.Combat.InvulnerableUntil is null && !me.Combat.Untouchable,
              "attacking ends the buff with the empty ActiveSpellEffectsUpdate");
    }

    private static void GetUpProtection()
    {
        using var arena = new Arena(ai: true, spells: true);
        var (session, peer, me) = arena.Join("ProtectedGetUp", new InventoryItem("T4_MAIN_SWORD"));
        var world = arena.World;
        var map = me.Map!;
        foreach (var mob in map.Objects.Values.OfType<Mob>().ToList())
            SpawnManager.Despawn(mob, world, respawn: false);
        var soldier = arena.Spawn(Soldier, new Vector2(1, 0));
        Knockdown.DamageSelf(me, me.Health.Max + 1);
        Knockdown.GetUp(me);
        var health = me.Health.At(world.GameNow);
        Check(me.Combat.Down is null && me.Combat.BuffDue is not null && me.Combat.Untouchable,
            "PvE get-up is protected before the visual buff arrives");
        var mark = peer.Events.Count;
        arena.Run(2.9);
        Check(soldier.TargetId != me.Id && !soldier.Threat.ContainsKey(me) && me.Health.At(world.GameNow) >= health,
            "a nearby mob neither focuses nor damages the player during the get-up animation");
        arena.Run(0.15);
        Check(me.Combat.InvulnerableUntil is not null, "the usual 15-second buff arrives after the animation");
        var here = me.PositionAt(DateTime.UtcNow);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [here.X, here.Y], v7 = 0,
            v8 = [here.X, here.Y + 0.5f], v9 = 5.5f });
        arena.Run(1);
        Check(me.Combat.InvulnerableUntil is not null && soldier.TargetId != me.Id && !soldier.Threat.ContainsKey(me)
            && !Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == me.Id && F(e.P, 2) < 0),
            "walking beside the mob preserves protection and takes no hits");
        // Do not prolong protection every time the player moves.
        me.Teleport(arena.Stand, 0, DateTime.UtcNow);
        arena.Run(15);
        Check(me.Combat.InvulnerableUntil is null && !me.Combat.Untouchable,
            "protection expires normally after its authored duration");
        Check(soldier.TargetId == me.Id || soldier.Threat.ContainsKey(me),
            "the mob may focus the nearby player again once protection expires");
    }

    private static void CommandsAndRejoin()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (session, peer, me) = arena.Join("Tester", new InventoryItem("T4_MAIN_SWORD"));
        var replies = new List<(string Text, bool Error)>();
        var gm = new CommandContext(world, me, AccountLevel.GameMaster, (t, e) => replies.Add((t, e)));
        var max = me.Health.Max;

        // .damage takes health with a HealthUpdate caused by the player itself; .heal fills the bars again.
        var mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".damage 100");
        var hit = Since(peer, mark).Single(e => e.Code == EventCodes.HealthUpdate).P;
        Check(F(hit, 2) == -100 && F(hit, 3) == max - 100 && L(hit, 6) == me.Id && me.Combat.Down is null && !me.Combat.InCombat,
              $".damage 100: HealthUpdate {{2 -100, 3 left, 6 me}}, no combat (got {Show([(EventCodes.HealthUpdate, hit)])})");
        mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".heal");
        Check(Since(peer, mark).Single(e => e.Code == EventCodes.RegenerationPlayerComboChanged).P is var full && F(full, 2) == max && me.Health.At(world.GameNow) == max,
              ".heal: RegenerationPlayerComboChanged with full health");

        // .kill: knocked down by itself; .heal gets it up at once.
        mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".kill");
        var kd = Since(peer, mark).Single(e => e.Code == EventCodes.KnockedDown).P;
        Check(me.Combat.Down is not null && L(kd, 2) == me.Id && replies.All(r => !r.Error), $".kill: KnockedDown {{2 me}} (replies: {string.Join(" | ", replies.Select(r => r.Text))})");
        mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".heal");
        Check(me.Combat.Down is null && Since(peer, mark).Any(e => e.Code == EventCodes.Respawn) && me.Health.At(world.GameNow) == max, ".heal while down: Respawn at once");
        arena.Run(3.1);
        Check(me.Combat.InvulnerableUntil is not null, "the buff follows the .heal get-up too");

        // Leaving while down: joined again, it stands at the knockdown spot with full bars (nothing of the knockdown is saved).
        var spot = arena.Stand + new Vector2(4, 1);
        lock (world.Sync)
        {
            me.Teleport(spot, 0, DateTime.UtcNow);
            world.Commands.Execute(gm, ".kill");
        }
        Check(me.Combat.Down is not null, "down again");
        lock (world.Sync) peer.Disconnect();
        var id = me.Character.Id;
        var again = new FakePeer();
        var rejoin = new WorldSession(again, world);
        again.Disconnected = rejoin.OnDisconnected;
        lock (world.Sync) rejoin.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
        Check(rejoin.Player is { } back && back.Combat.Down is null && back.Health.At(world.GameNow) == back.Health.Max && back.PositionAt(DateTime.UtcNow) == spot,
              $"joined again: standing at the knockdown spot with full bars (at {rejoin.Player?.PositionAt(DateTime.UtcNow)}, wanted {spot})");
        _ = session;
    }

    private static void EffectsAfterKnockdown()
    {
        using var arena = new Arena(); // no AI: only the player's own effects hit the mob
        var world = arena.World;
        var (session, peer, me) = arena.Join("Burner",
            new InventoryItem("T4_MAIN_FIRESTAFF") { Quality = 5, Active = ["FIRESTAFFBOLT2", "FIRESTAFFIGNITE2_SPREAD", "PYROBLAST_SKILLSHOT"] });
        var gm = new CommandContext(world, me, AccountLevel.GameMaster, (_, _) => { });
        var soldier = arena.Spawn(Soldier, new Vector2(6, 0));
        var bolt = Client.Spells!.IdOf("FIRESTAFFBOLT2");
        static bool OnMob(Mob m, (EventCodes Code, Dictionary<byte, object?> P) e) => e.Code is EventCodes.HealthUpdate or EventCodes.CastHit && L(e.P, 0) == m.Id;

        // 1. The bolt hits (the DoT starts, 4 ticks over 3.3 s); the player goes down 0.1 s later: no tick, no combat after it.
        var mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = bolt, d1 = soldier.Id, d4 = 1 });
        for (var i = 0; i < 40 && !Since(peer, mark).Any(e => e.Code == EventCodes.CastHit); i++) arena.Run(0.05);
        arena.Run(0.1);
        Check(me.Combat.InCombat && Since(peer, mark).Count(e => OnMob(soldier, e)) == 2, "the bolt hit the soldier (CastHit + HealthUpdate), the player is in combat");
        mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".kill");
        var health = soldier.Health.At(world.GameNow);
        arena.Run(4);
        var down = Since(peer, mark);
        Check(me.Combat.Down is not null && !me.Combat.InCombat && !down.Any(e => OnMob(soldier, e)) && soldier.Health.At(world.GameNow) >= health
              && !down.Any(e => e.Code == EventCodes.InCombatStateUpdate && e.P.ContainsKey(1))
              && down.Where(e => e.Code == EventCodes.RegenerationHealthChanged && L(e.P, 0) == me.Id).All(e => F(e.P, 5) > 0),
              $"down: the DoT's ticks stop, no InCombatStateUpdate {{1 true}}, no RegenerationHealthChanged with regen 0 (got {Show(down.Where(e => e.Code is EventCodes.InCombatStateUpdate or EventCodes.RegenerationHealthChanged or EventCodes.HealthUpdate).ToList())})");

        // 2. No harvest and no loot while down.
        var responses = peer.Responses.Count;
        Send(session, new HarvestStartRequest { Start = world.GameNow, HarvestableId = soldier.Id, ToolObjectId = -1 });
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == (short)HarvestRefusal.NotPossible && me.Harvest is null,
              "HarvestStart while down: refused (2080)");

        // 3. Up again (.heal); a walk within getuptimeseconds does not stop the buff, which still comes 3 s after the get-up.
        lock (world.Sync) world.Commands.Execute(gm, ".heal");
        arena.Run(1);
        var here = me.PositionAt(DateTime.UtcNow);
        Send(session, new MoveRequest { wf = world.GameNow, v6 = [here.X, here.Y], v7 = 90, v8 = [here.X - 2, here.Y], v9 = 5.5f });
        mark = peer.Events.Count;
        arena.Run(2.1);
        Check(me.Combat.InvulnerableUntil is not null && Since(peer, mark).Any(e => e.Code == EventCodes.CastSpell && L(e.P, 0) == me.Id),
              "a walk 1 s after the get-up: the buff still comes at +3 s (SPEC §5e.2)");

        // 4. A bolt in flight when the player goes down does no damage.
        arena.Run(2.5); // the cast cooldown
        mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = bolt, d1 = soldier.Id, d4 = 2 });
        for (var i = 0; i < 40 && !Since(peer, mark).Any(e => e.Code == EventCodes.CastSpell && L(e.P, 0) == me.Id); i++) arena.Run(0.05);
        var flying = Since(peer, mark).Any(e => e.Code == EventCodes.CastSpell && L(e.P, 0) == me.Id) && !Since(peer, mark).Any(e => OnMob(soldier, e));
        lock (world.Sync) world.Commands.Execute(gm, ".kill");
        mark = peer.Events.Count;
        arena.Run(1);
        Check(flying && !Since(peer, mark).Any(e => OnMob(soldier, e)) && !me.Combat.InCombat, "a bolt in flight at the knockdown: no CastHit, no damage, no combat");
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
