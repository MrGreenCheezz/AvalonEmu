using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageQ outposts (analysis/work/stageQ/outposts; <see cref="Outposts"/>): 1207's three Forest towers as captured (365 + 366 in the Join's phase B,
/// then 468), their outpost.xml guards, and a capture by a Swamp player standing in a cleared tower's zone: 8 s to empty Forest's points, 16 s to
/// fill its own, 367 and the Swamp guards after their respawndelay.
/// </summary>
public static class OutpostTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("outposts: 1207's 3 towers (365 tile/type/pos + 366 Forest full, then 468), 8 Forest guards each, killed by a Swamp player (not unflagged), who takes one in 8 + 16 s (366 key 7 -80000 / +160000, 367 5 <- 2), Swamp guards back", Flow),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static void Flow()
    {
        var template = Client.Clusters!.Get("1207"); // resolved before the join: the map fills at once
        var towers = template.Outposts.OrderBy(o => o.Position.X).ToList();
        Check(towers.Count == 3 && towers.All(t => t is { Tile: "FOREST_GREEN_FACTION_WARFARE_OUTPOST_TOWER", Type: "OUTPOST_SAFE_3", Guards.Count: 8 } && t.World is not null)
              && towers[0].Position == new Vector2(-356, 266) && towers[1].Position == new Vector2(-296, -164) && towers[2].Position == new Vector2(94, -4)
              && towers[2].CaptureZone == new Vector2(110, -20),
              $"1207: 3 towers at the captured (-356, 266), (-296, -164), (94, -4), each 8 guard spots, the capture zone at its template centre (got {string.Join("; ", towers.Select(t => $"{t.Position} {t.CaptureZone}"))})");
        var clock = new ManualClock();
        var start = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true, FactionWarfare = new FactionWarfareOptions { Claimable = OutpostClaimRule.All } },
            Repository(NewDir()), Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Client.Clusters, MapSeed = 3, GameTicks = () => start + clock.Now.Ticks, // the towers run on game time
        };
        world.MobAi = false;
        var roster = world.Outposts.Data?.Guards("default", 2, 4) ?? [];
        Check(roster is [{ Mob: "T5_MOB_CITY_FOREST_BOSS", Amount: 1, RespawnSeconds: 15 }, { Mob: "T5_MOB_CITY_FOREST_GUARD", Amount: 7, RespawnSeconds: 30 }],
              "outpost.xml: Forest at tier 4 = 1 T5 boss (15 s) + 7 T5 guards (30 s)");
        var characters = (AlbionLocal.Server.Persistence.SqliteCharacterRepository)world.Characters;
        var c = StarterKit.Default.NewCharacter("O" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY, c.Flag) = ("1207", 110, -20, 5); // Swamp, in the third tower's capture zone
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
        var map = world.Maps.Find("1207")!;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        for (var i = 0; i < 100 && map.Template is null; i++)
        {
            Thread.Sleep(20);
            Tick(0.1);
        }
        Tick(0.2);
        // The Join: per tower 365 then its 366, after them one 468.
        var codes = peer.Events.Select(Code).ToList();
        var news = peer.Events.Where(e => Code(e) == (int)EventCodes.NewOutpostObject).Select(e => e.Parameters).ToList();
        var firstUpdate = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.OutpostUpdate)?.Parameters;
        var state = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.FactionWarfareClusterState)?.Parameters;
        var i365 = codes.IndexOf((int)EventCodes.NewOutpostObject);
        Check(news.Count == 3 && news.All(p => p[1] is "FOREST_GREEN_FACTION_WARFARE_OUTPOST_TOWER" && p[2] is "OUTPOST_SAFE_3")
              && news.Any(p => p[3] is float[] { Length: 2 } xz && xz[0] == 94 && xz[1] == -4) && i365 >= 0 && codes[i365 + 1] == (int)EventCodes.OutpostUpdate
              && firstUpdate is not null && Convert.ToInt32(firstUpdate[1]) == 2 && Convert.ToInt32(firstUpdate[8]) == 2 && Convert.ToInt64(firstUpdate[4]) == Outposts.Full
              && Convert.ToInt64(firstUpdate[5]) == Outposts.Full && !firstUpdate.ContainsKey(7)
              && state is not null && codes.IndexOf((int)EventCodes.FactionWarfareClusterState) > codes.LastIndexOf((int)EventCodes.NewOutpostObject)
              && state[3] is byte[] { Length: 3 } owners && owners.All(o => o == 2) && state[5] is bool[] { Length: 6 } present && present[4] && Convert.ToInt32(state[4]) == 2,
              "Join: 3 × 365 (tile, OUTPOST_SAFE_3, (94, -4)) each with its 366 (Forest 2, 1000 of 1000, still), then 468 (3 owners Forest, presence[Swamp], controller 2)");
        var tower = map.Objects.Values.OfType<OutpostObject>().MinBy(t => Vector2.Distance(t.Position, new Vector2(94, -4)))!;
        var s = tower.State;
        Check(s.Squad.Count == 8 && s.Squad.All(m => m is { Dead: false }) && s.Squad[0]!.Template.Name == "T5_MOB_CITY_FOREST_BOSS" && s.Squad[0]!.Flagging == 2
              && s.Claim == Outposts.Guarded && s.Contested == Outposts.MinorContested,
              $"the tower's 8 guards stand (the boss first, NewMob faction 2); Guarded, Minor contest with a Swamp player near (got {s})");
        // The Swamp player kills the 8 guards (they take its damage; unflagged, a guard takes none).
        var me = map.Players.Values.Single();
        var boss = s.Squad[0]!;
        lock (world.Sync)
        {
            me.Character.Flag = 0;
            Combat.Damage(me, boss, 100, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
            me.Character.Flag = 5;
        }
        var unharmed = boss.Health.At(world.GameNow) >= boss.Health.Max - 0.5f && !s.Squad.Any(m => m!.Threat.ContainsKey(me));
        lock (world.Sync)
            foreach (var m in s.Squad.ToList()) Combat.Damage(me, m!, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        Check(unharmed && s.Squad.All(m => m is { Dead: true }), "unflagged, the player's hit does nothing to the boss; flagged Swamp, its hits kill all 8 guards");
        peer.Events.Clear();
        Tick(0.5);
        var draining = peer.Events.Where(e => Code(e) == (int)EventCodes.OutpostUpdate).Select(e => e.Parameters).LastOrDefault();
        Check(s.Claim == Outposts.Claimable && s.Holder == 2 && draining is not null && Convert.ToInt32(draining[2]) == Outposts.Claimable
              && Convert.ToInt64(draining[7]) == -80_000 && !draining.ContainsKey(5) && Convert.ToInt32(draining[9]) == Outposts.MajorContested,
              $"cleared: Claimable, Forest's points drain at -1 per 8 ms (366 key 7 -80000, key 5 0), Major contest (got {s})");
        for (var t = 0; t < 16; t++) Tick(0.5); // 8.5 s in
        var filling = peer.Events.Where(e => Code(e) == (int)EventCodes.OutpostUpdate).Select(e => e.Parameters).LastOrDefault();
        Check(s.Holder == 5 && filling is not null && Convert.ToInt32(filling[1]) == 5 && Convert.ToInt64(filling[7]) == 160_000 && Convert.ToInt64(filling[5]) == Outposts.Full
              && s.Owner == 2,
              $"after 8 s the points are Swamp's, filling at +1 per 16 ms (key 1 5, key 7 160000); still Forest's tower (got {s})");
        for (var t = 0; t < 34 && s.Owner == 2; t++) Tick(0.5);
        var claimed = peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.OutpostClaimed)?.Parameters;
        Check(s.Owner == 5 && claimed is not null && Convert.ToInt64(claimed[0]) == tower.Id && Convert.ToInt32(claimed[1]) == 5 && Convert.ToInt32(claimed[2]) == 2,
              $"16 s later the tower is Swamp's: 367 {{0 tower, 1 Swamp 5, 2 Forest 2}} (got {s})");
        for (var t = 0; t < 32; t++) Tick(1);
        Check(s.Squad.All(m => m is { Dead: false } && m.Flagging == 5) && s.Squad[0]!.Template.Name == "T5_MOB_CITY_SWAMP_BOSS" && s.Claim == Outposts.Guarded,
              $"30 s after the capture Swamp's 8 guards stand and the tower is Guarded again (got {s})");
        world.Dispose();
    }
}
