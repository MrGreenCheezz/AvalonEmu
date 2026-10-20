using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>StageN faction (analysis/work/stageN/faction/SPEC.md): enlist at Thetford's trader, earn points from a kill in a swamp warzone, buy at the trader.</summary>
public static class FactionTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("faction: op 433 enlists (482, cooldown on a switch), a flagged kill in 0204 earns points and standing (85, 86, Join 104/105), the trader sells a cape blueprint for points (v30)", Enlist),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static void Enlist()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = GameTime.Now;
        var store = Client.Clusters!;
        var world = new World(ServerConfig.PveDefaults with { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = store, GameTicks = () => anchor + clock.Now.Ticks, MapSeed = 3,
        };
        world.MobAi = false;
        var traderSpawn = store.Get("0000").Buildings.First(b => b.Template.Name == "SWAMP_GREEN_FACTION_TRADER");
        var c = StarterKit.Default.NewCharacter("Fen");
        (c.Cluster, c.PositionX, c.PositionY) = ("0000", traderSpawn.Position.X + 2, traderSpawn.Position.Y);
        Check(characters.Create(c), "character created");
        (WorldSession S, FakePeer P) Join()
        {
            var peer = new FakePeer();
            var s = new WorldSession(peer, world);
            peer.Disconnected = s.OnDisconnected;
            lock (world.Sync) s.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
            return (s, peer);
        }
        var (s, peer) = Join();
        var me = s.Player!;
        var trader = me.Map!.Objects.Values.OfType<Building>().Single(b => b.Template.Name == "SWAMP_GREEN_FACTION_TRADER");

        // 1. Op 433 at the trader: {0 the stamp of the next allowed switch}, 482 {0 me, 1 Swamp 5}; a switch elsewhere within the cooldown is refused.
        var n = peer.Responses.Count;
        var mark = peer.Events.Count;
        lock (world.Sync) s.OnRequest(Request(new EnlistFactionWarfareRequest { j8 = trader.Id }, 5), encrypted: false);
        var enlisted = peer.Responses.Skip(n).FirstOrDefault();
        var changed = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.FactionEnlistmentChanged);
        string? again;
        lock (world.Sync) again = world.Factions.Enlist(me, Server.Data.FactionWarfareData.FlagOf("forest"), force: false).Refused;
        Check(enlisted is { ReturnCode: 0 } && L(enlisted.Parameters, 0) == world.GameNow + TimeSpan.FromMinutes(1440).Ticks && changed.P is { } p482 && L(p482, 0) == me.Id && L(p482, 1) == 5
              && me.Faction == 5 && again is not null && me.Faction == 5,
              $"op 433: rc {enlisted?.ReturnCode}, 482 {{1 {changed.P?.GetValueOrDefault((byte)1)}}}, enlisted {me.Faction}; a switch to Forest at once refused ({again})");

        // 2. Flagged Swamp (no PvP under Consensual), in 0204 (DUCHY_YELLOW_02, FW_HIGH_SAFE_3): a mob kill's fame × 0.007 → points (85) and standing (86).
        lock (world.Sync) Flagging.Set(me, 5);
        world.ResolveCluster("0204")!.Wait(TimeSpan.FromSeconds(60));
        world.Commands.Execute(new CommandContext(world, me, AccountLevel.Admin, (_, _) => { }), ".tele 0204");
        (s, peer) = Join();
        me = s.Player!;
        var map = me.Map!;
        var kind = store.Mobs.Find("T4_MOB_ROAMING_UNDEAD_GHOUL")!;
        var mob = SpawnManager.SpawnAt(map, kind, me.PositionAt(DateTime.UtcNow) + new Vector2(2, 0), 0, world);
        mark = peer.Events.Count;
        lock (world.Sync) Combat.Damage(me, mob, 1e9, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        var events = Since(peer, mark);
        var currency = events.FirstOrDefault(e => e.Code == EventCodes.UpdateCurrency).P;
        var standing = events.FirstOrDefault(e => e.Code == EventCodes.UpdateFactionStanding).P;
        var points = me.Character.FactionPoints[4];
        Check(mob.Dead && map.Cluster == "0204" && me.Flag == 5 && points > 0 && currency is { } p85 && L(p85, 0) == Factions.FromCluster && L(p85, 2) == 5 && L(p85, 9) == points
              && standing is { } p86 && L(p86, 0) == 5 && L(p86, 3) == me.Character.FactionStanding[4] && me.Character.FactionStanding[4] > 0,
              $"{kind.Name} (dead {mob.Dead}, map {map.Cluster}, flag {me.Flag}, faction {me.Faction}, warzone {world.Factions.Data?.IsWarzone(map.Cluster)}, fame {me.Character.Fame}) killed flagged in 0204: {points / 10000.0:0.####} FACTION_SWAMP points (85 {{2 index 5, 9 total}}), standing {me.Character.FactionStanding[4] / 10000.0:0.####} (86)");

        // 3. Back at the trader with 400 points: the T4 cape blueprint is sold for them (85 the new total); the T5 one (15 000 standing) is refused.
        lock (world.Sync) world.Factions.Earn(me, 400 * 10000L, null);
        world.Commands.Execute(new CommandContext(world, me, AccountLevel.Admin, (_, _) => { }), ".tele 0000");
        (s, peer) = Join();
        me = s.Player!;
        var join = peer.Responses[0];
        lock (world.Sync) me.Teleport(trader.Position + new Vector2(2, 0), 0, DateTime.UtcNow);
        var have = me.Character.FactionPoints[4];
        n = peer.Responses.Count;
        lock (world.Sync) s.OnRequest(Request(new ActionOnBuildingStartRequest { wf = world.GameNow, o = trader.Id, p = 1, q = 1, x = "T5_CAPEITEM_FW_THETFORD_BP", w = 1 }, 7), encrypted: false);
        var refused = peer.Responses.Skip(n).FirstOrDefault();
        mark = peer.Events.Count;
        lock (world.Sync) s.OnRequest(Request(new ActionOnBuildingStartRequest { wf = world.GameNow, o = trader.Id, p = 1, q = 2, x = "T4_CAPEITEM_FW_THETFORD_BP", w = 1 }, 7), encrypted: false);
        var bought = me.Inventory.Bag.Items.FirstOrDefault(i => i.Template.Name == "T4_CAPEITEM_FW_THETFORD_BP");
        var paid = Since(peer, mark).LastOrDefault(e => e.Code == EventCodes.UpdateCurrency).P;
        Check(join.Parameters[105] is long[] { Length: 6 } standings && standings[4] == me.Character.FactionStanding[4] && join.Parameters[104] is Dictionary<int, long> wallet && wallet[5] == have
              && refused is { ReturnCode: WorldSession.StationComponentError } && bought is not null && me.Character.FactionPoints[4] == have - 400 * 10000L && paid is { } p && L(p, 9) == have - 400 * 10000L,
              $"Join 104/105 carry the points and standing; T5 blueprint refused (rc {refused?.ReturnCode}); T4 blueprint bought: {bought?.Template.Name}, points {have / 10000.0:0.##} → {me.Character.FactionPoints[4] / 10000.0:0.##}");

        // Saved with the character (schema v30).
        lock (world.Sync) characters.Save(me.Character.Clone());
        Check(characters.Find(c.Id) is { Faction: 5 } saved && saved.FactionPoints[4] == me.Character.FactionPoints[4] && saved.FactionStanding[4] == me.Character.FactionStanding[4] && saved.FactionChangedAt > 0,
              "the enlistment, points, standing and the change's stamp are saved (v30)");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
