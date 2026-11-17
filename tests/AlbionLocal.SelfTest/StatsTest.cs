using System.Text.Json;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>StageR stats (analysis/work/stageR/stats/SPEC.md): the kill history, the personal might and the Conqueror's weekly challenge.</summary>
public static class StatsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("kill history: a PvP death and a knockdown fill the kill history (143-146 after GetCharacterStats), 148 has the details JSON, an unknown kill is refused", Kills),
        ("conqueror: personal might in SEASON_34 (453), the weekly bar and favor (451, 85), the chest needs premium (18080), claimed once (497 {3}, 18081), saved", Weekly),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static OperationResponse Ask(World world, WorldSession s, FakePeer peer, AlbionRequest req)
    {
        var n = peer.Responses.Count;
        lock (world.Sync) s.OnRequest(Request(req, 9), encrypted: false);
        return peer.Responses.Skip(n).Single();
    }

    /// <summary>Ash kills Bea (Death.Kill), then knocks Cid down (Pvp.KnockedDown); Dan looks at Ash's and Bea's stats and at a kill's details.</summary>
    private static void Kills()
    {
        using var arena = new Arena();
        var world = arena.World;
        var (_, _, ash) = arena.Join("Ash", new InventoryItem("T4_MAIN_SWORD"));
        var (_, _, bea) = arena.Join("Bea", new InventoryItem("T5_MAIN_SWORD"));
        var (_, _, cid) = arena.Join("Cid", new InventoryItem("T4_MAIN_SWORD"));
        var (ds, dp, _) = arena.Join("Dan", new InventoryItem("T4_MAIN_SWORD"));
        lock (world.Sync)
        {
            Death.Kill(bea, ash, ash.Id, ash.Character.Name, Death.Cause.NoLoss, false, world.GameNow);
            Pvp.KnockedDown(ash, cid, false, world.GameNow);
        }

        var mark = dp.Events.Count;
        var stats = Ask(world, ds, dp, new GetCharacterStatsRequest { mf = "Ash", mg = 0 });
        var events = Since(dp, mark);
        var kills = new CharacterStatsKillHistoryEvent();
        AlbionSerializer.FillFields(kills, events.First(e => e.Code == EventCodes.CharacterStatsKillHistory).P);
        var downs = new CharacterStatsKnockDownHistoryEvent();
        AlbionSerializer.FillFields(downs, events.First(e => e.Code == EventCodes.CharacterStatsKnockDownHistory).P);
        Check(stats.ReturnCode == 0 && kills.dpv == "Ash" && kills.dpw is ["Bea"] && kills.dpy is [{ Length: 16 }] && kills.dp0 is [> 0]
              && downs.dp2 is ["Cid"] && events.Any(e => e.Code == EventCodes.CharacterStatsDeathHistory) && events.Any(e => e.Code == EventCodes.CharacterStatsKnockedDownHistory),
              $"GetCharacterStats Ash: rc {stats.ReturnCode}, 143 {string.Join(",", kills.dpw ?? [])}, 145 {string.Join(",", downs.dp2 ?? [])}, events {string.Join(",", events.Select(e => e.Code))}");

        mark = dp.Events.Count;
        Ask(world, ds, dp, new GetCharacterStatsRequest { mf = "Bea", mg = 0 });
        var deaths = new CharacterStatsDeathHistoryEvent();
        AlbionSerializer.FillFields(deaths, Since(dp, mark).First(e => e.Code == EventCodes.CharacterStatsDeathHistory).P);
        var kill = new Guid(kills.dpy![0]);
        Check(deaths.dpq is ["Ash"] && deaths.dps is [var dl] && new Guid(dl) == kill, "Bea's death list names Ash with the same kill");

        mark = dp.Events.Count;
        var details = Ask(world, ds, dp, new GetKillHistoryDetailsRequest { ny = kill });
        var shown = Since(dp, mark).FirstOrDefault(e => e.Code == EventCodes.KillHistoryDetails).P;
        using var json = JsonDocument.Parse(shown?[2] as string ?? "{}");
        var root = json.RootElement;
        Check(details.ReturnCode == 0 && shown?[1] is byte[] guid && new Guid(guid) == kill && shown.GetValueOrDefault((byte)3) is null or false
              && root.TryGetProperty("kn", out var kn) && kn.GetString() == "Ash" && root.GetProperty("dn").GetString() == "Bea" && root.GetProperty("kft").GetInt32() == 1
              && root.GetProperty("di").GetArrayLength() == bea.Inventory.Equipment.Size && root.GetProperty("di")[0].GetProperty("id").GetString() == "T5_MAIN_SWORD",
              $"146 → 148 {{the Guid, the JSON, false}}: {shown?[2]}");

        var unknown = Ask(world, ds, dp, new GetKillHistoryDetailsRequest { ny = Guid.NewGuid() });
        Check(unknown.ReturnCode != 0, $"an unknown kill is refused (rc {unknown.ReturnCode})");
        Check(world.KillHistory.Of(ash.Character.Id, true, true).Count == 1 && arena.Characters.LoadKills().Count == 2, "the two rows are saved");
    }

    /// <summary>A day of SEASON_34 (Europe): 12 000 might, then 9 000; the season stats, the weekly bar, the favor rewards and the chest.</summary>
    private static void Weekly()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc); // inside SEASON_34 (to 2026-11-09)
        var world = new World(ServerConfig.PveDefaults with { JoinByCharacterId = true, PremiumMode = PremiumMode.None }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Client.Clusters!, GameTicks = () => start.Ticks + clock.Now.Ticks, MapSeed = 3,
            GvgSeasons = GvgSeasonData.Load(ClientGameDataDir(), "europe"),
        };
        world.MobAi = false;
        try
        {
            var c = StarterKit.Default.NewCharacter("Conqueror");
            c.Cluster = "0000";
            Check(characters.Create(c), "character created");
            var peer = new FakePeer();
            var s = new WorldSession(peer, world);
            lock (world.Sync) s.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
            var me = s.Player!;
            var season = world.GuildGoals.Season;
            Check(season.Index is not null && world.GvgSeasons!.Might.Weekly is { Might: 200_000_000 } && world.GvgSeasons.Might.Personal.GetValueOrDefault("gathering") == "GATHERING",
                  $"SEASON_34 runs ({season.Key}), the weeklychallenge is read");

            var mark = peer.Events.Count;
            var pve = world.GvgSeasons.Might.Fame["pve"].Objective;
            lock (world.Sync) Conqueror.Add(me, pve, null, 12_000 * 10000.0);
            var favor = Since(peer, mark).Where(e => e.Code == EventCodes.UpdateCurrency).ToList();
            var data = Ask(world, s, peer, new GetPvpChallengeDataRequest());
            var personal = Ask(world, s, peer, new GetPersonalMightStatsRequest { od = season.Index!.Value });
            Check(N(data.Parameters, 0) == 12_000 * 10000L && N(data.Parameters, 1) == 12_000 * 10000L && N(data.Parameters, 2) == Conqueror.Locked
                  && favor.Count == 2 && me.Character.MistFavor == 4000 * 10000L
                  && Convert.ToInt32(personal.Parameters[0]) == season.Index && personal.Parameters[1] is string[] and ["PVE"] && personal.Parameters[2] is long[] and [120_000_000],
                  $"12000 might: 451 {{{data.Parameters.GetValueOrDefault((byte)0)}, {data.Parameters.GetValueOrDefault((byte)1)}, {data.Parameters.GetValueOrDefault((byte)2)}}}, {favor.Count} x 85, favor {me.Character.MistFavor / 10000}; 453 {string.Join(",", personal.Parameters.GetValueOrDefault((byte)1) as string[] ?? [])}");

            mark = peer.Events.Count;
            lock (world.Sync) Conqueror.Add(me, pve, null, 9_000 * 10000.0);
            var unlocked = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.WeeklyPvpChallengeRewardStateUpdate).P;
            var refused = Ask(world, s, peer, new ClaimPvpChallengeWeeklyRewardRequest());
            Check(unlocked?[0] is byte b && b == Conqueror.NotClaimable && refused.ReturnCode == Conqueror.RcNeedsPremium && me.Character.MistFavor == 6000 * 10000L,
                  $"21000 might: 497 {{{unlocked?[0]}}} without premium, the claim rc {refused.ReturnCode}, favor {me.Character.MistFavor / 10000}");

            lock (world.Sync) Premium.Set(me, true, world.GameNow);
            mark = peer.Events.Count;
            var claim = Ask(world, s, peer, new ClaimPvpChallengeWeeklyRewardRequest());
            var claimed = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.WeeklyPvpChallengeRewardStateUpdate).P;
            var again = Ask(world, s, peer, new ClaimPvpChallengeWeeklyRewardRequest());
            Check(claim.ReturnCode == 0 && claimed?[0] is byte cb && cb == Conqueror.Claimed && again.ReturnCode == Conqueror.RcAlreadyClaimed
                  && me.Inventory.Bag.Items.Count(i => i.Template.Name == "UNIQUE_LOOTCHEST_CONQUEROR_WEEKLY_REWARD") == 1,
                  $"premium: claim rc {claim.ReturnCode}, 497 {{{claimed?[0]}}}, again rc {again.ReturnCode}, one chest");

            lock (world.Sync) characters.Save(me.Character.Clone());
            Check(characters.Find(me.Character.Id)?.Goals?.Conqueror is { Chest: Conqueror.Claimed, WeekMight: 210_000_000 } log && log.Seasons[season.Index.Value]["PVE"] == 210_000_000,
                  "the Conqueror log is saved");

            // Next week: the bar and the chest start again.
            clock.Advance(TimeSpan.FromDays(7));
            var next = Ask(world, s, peer, new GetPvpChallengeDataRequest());
            Check(N(next.Parameters, 1) == 0 && N(next.Parameters, 2) == Conqueror.Locked, "a week later: no weekly might, the chest locked");
        }
        finally
        {
            world.Dispose();
            characters.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static long N(Dictionary<byte, object?> p, byte key) => p.GetValueOrDefault(key) is { } v ? Convert.ToInt64(v) : 0;
}
