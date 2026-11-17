using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>StageR fw-campaign (analysis/work/stageR/fw-campaign/SPEC.md): the campaign window's data, its rewards, the weekly report and the nameplate rank.</summary>
public static class FactionCampaignTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("fw campaign: points feed the day and month (436), the daily standing reward and a chest unlock (484), the chest claimed with premium (437), the weekly report (434)", Campaign),
        ("fw campaign: the nameplate rank (483 on a rank up and the enlistment, NewCharacter key 57)", Rank),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>The first state of key 3 (bcg.d[] as a packed int enum array: 4 bytes each).</summary>
    private static int State(byte[]? packed) => packed is { Length: >= 4 } ? BitConverter.ToInt32(packed, 0) : -1;

    private static (World World, WorldSession S, FakePeer Peer, string Dir, Server.Persistence.SqliteCharacterRepository Repo) Start()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = GameTime.Now;
        var world = new World(ServerConfig.PveDefaults with { JoinByCharacterId = true, PremiumMode = PremiumMode.None }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Client.Clusters!, GameTicks = () => anchor + clock.Now.Ticks, MapSeed = 3,
        };
        world.MobAi = false;
        var c = StarterKit.Default.NewCharacter("Campaigner");
        c.Cluster = "0000";
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var s = new WorldSession(peer, world);
        lock (world.Sync) s.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
        return (world, s, peer, dir, characters);
    }

    private static void Stop(World world, Server.Persistence.SqliteCharacterRepository repo, string dir)
    {
        world.Dispose();
        repo.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Campaign()
    {
        var (world, s, peer, dir, repo) = Start();
        var me = s.Player!;
        lock (world.Sync) world.Factions.Enlist(me, 5, force: true);

        // 1300 points: the day's first trigger (1250) gives its 1250 standing at once (86) and 484 names FACTIONCAMPAIGN_REWARD_DAILY_01.
        var mark = peer.Events.Count;
        lock (world.Sync) world.Factions.Earn(me, 1300 * 10000L, null);
        var events = Since(peer, mark);
        var unlocked = events.FirstOrDefault(e => e.Code == EventCodes.FactionWarfareCampaignRewardsUnlocked).P;
        var standing = events.Where(e => e.Code == EventCodes.UpdateFactionStanding).ToList();
        Check(unlocked?[0] is string[] ids && ids.SequenceEqual(["FACTIONCAMPAIGN_REWARD_DAILY_01"]) && standing.Count == 2 && L(standing[1].P, 1) == 1250 * 10000L
              && me.Character.FactionStanding[4] == (1300 + 1250) * 10000L,
              $"1300 points: 484 {string.Join(",", unlocked?[0] as string[] ?? [])}, {standing.Count} x 86, standing {me.Character.FactionStanding[4] / 10000}");

        // 25000 more: the other daily rewards and the first campaign chest (unlockday 0) unlock; 436 shows the tracks [1 daily, 0 campaign] with the
        // day's and month's points and the chest UNLOCKED (1: no premium); a claim is refused.
        mark = peer.Events.Count;
        lock (world.Sync) world.Factions.Earn(me, 25000 * 10000L, null);
        unlocked = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.FactionWarfareCampaignRewardsUnlocked).P;
        OperationResponse Ask(AlbionRequest req)
        {
            var n = peer.Responses.Count;
            lock (world.Sync) s.OnRequest(Request(req, 9), encrypted: false);
            return peer.Responses.Skip(n).Single();
        }
        var data = Ask(new GetFactionWarfareCampaignDataRequest());
        var month = WorldSession.CampaignMonth(Seasons.UtcNow(world));
        var points = 26300 * 10000L;
        Check(unlocked?[0] is string[] more && more.Contains("FACTIONCAMPAIGN_REWARD_01") && more.Contains("FACTIONCAMPAIGN_TRIGGER_DAILY_03")
              && data.Parameters[1] is long[] progress && progress.SequenceEqual([points, points]) && Convert.ToInt32(data.Parameters[4]) == month
              && data.Parameters[2] is string[] { Length: 1 } rewardIds && rewardIds[0] == "FACTIONCAMPAIGN_REWARD_01" && data.Parameters[5] is long[] { Length: 6 } tally && tally[4] == points
              && data.Parameters[6] is int[] { Length: 6 } blue && blue[4] > 0,
              $"25000 more: 484 {string.Join(",", unlocked?[0] as string[] ?? [])}; 436 points {string.Join("/", data.Parameters.GetValueOrDefault((byte)1) as long[] ?? [])}, rewards {string.Join(",", data.Parameters.GetValueOrDefault((byte)2) as string[] ?? [])}, key 5 {string.Join("/", data.Parameters.GetValueOrDefault((byte)5) as long[] ?? [])}, key 6 {data.Parameters.GetValueOrDefault((byte)6)?.GetType().Name} {string.Join("/", data.Parameters.GetValueOrDefault((byte)6) as int[] ?? [])}, key 4 {data.Parameters.GetValueOrDefault((byte)4)} vs {month}");
        var state = data.Parameters[3] is byte[] packed ? packed : null;
        var refused = Ask(new ClaimFactionWarfareItemRewardRequest { fn = "FACTIONCAMPAIGN_REWARD_01" });
        Check(State(state) == FactionCampaign.Unlocked && refused.ReturnCode == FactionCampaign.RcGeneric,
              $"without premium the chest is UNLOCKED (key 3 {Convert.ToHexString(state ?? [])}) and its claim refused (rc {refused.ReturnCode})");

        // With premium (more than 6 days): CLAIMABLE, the claim gives the Swamp chest and then shows CLAIMED.
        lock (world.Sync) Premium.Set(me, true, world.GameNow);
        var before = Ask(new GetFactionWarfareCampaignDataRequest()).Parameters[3] as byte[];
        var claim = Ask(new ClaimFactionWarfareItemRewardRequest { fn = "FACTIONCAMPAIGN_REWARD_01" });
        var after = Ask(new GetFactionWarfareCampaignDataRequest()).Parameters[3] as byte[];
        var again = Ask(new ClaimFactionWarfareItemRewardRequest { fn = "FACTIONCAMPAIGN_REWARD_01" });
        Check(State(before) == FactionCampaign.Claimable && claim.ReturnCode == 0 && State(after) == FactionCampaign.Claimed && again.ReturnCode == FactionCampaign.RcGeneric
              && me.Inventory.Bag.Items.Any(i => i.Template.Name == "UNIQUE_LOOTCHEST_FACTIONCAMPAIGN_REWARD_SWAMP_01"),
              $"premium: {Convert.ToHexString(before ?? [])} → claim rc {claim.ReturnCode} → {Convert.ToHexString(after ?? [])}, again rc {again.ReturnCode}");

        // The weekly report: this week, the faction; the points of last week (none yet). Saved in the goals JSON.
        var report = Ask(new GetFactionWarfareWeeklyReportRequest());
        lock (world.Sync) repo.Save(me.Character.Clone());
        Check(report.ReturnCode == 0 && Convert.ToInt32(report.Parameters[0]) == FactionCampaign.Week(Seasons.UtcNow(world)) && Convert.ToInt32(report.Parameters[1]) == 5
              && repo.Find(me.Character.Id)?.Goals?.FactionCampaign is { MonthPoints: var saved } log && saved == points && log.Rewards["FACTIONCAMPAIGN_REWARD_01"] == FactionCampaign.Claimed,
              $"434 week {report.Parameters.GetValueOrDefault((byte)0)} faction {report.Parameters.GetValueOrDefault((byte)1)}; the campaign log is saved");
        Stop(world, repo, dir);
    }

    private static void Rank()
    {
        var (world, s, peer, dir, repo) = Start();
        var me = s.Player!;
        var mark = peer.Events.Count;
        lock (world.Sync) world.Factions.Enlist(me, 5, force: true);
        var enlist = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.UpdateFactionRank).P;
        mark = peer.Events.Count;
        lock (world.Sync) world.Factions.Earn(me, 50000 * 10000L, null); // standing 50000 + the daily 8750 → rank 7 (42000), index 6
        var rank = Since(peer, mark).LastOrDefault(e => e.Code == EventCodes.UpdateFactionRank).P;
        var create = me.BuildCreate(DateTime.UtcNow);
        Check(enlist is { } e0 && L(e0, 0) == me.Id && !e0.ContainsKey(1) && rank is { } r && L(r, 1) == 6 && create.cxi == 6 && create.cxh == 5,
              $"483 on enlisting {{1 {enlist?.GetValueOrDefault((byte)1)}}}, on the rank up {{1 {rank?.GetValueOrDefault((byte)1)}}}; NewCharacter key 57 {create.cxi}");
        Stop(world, repo, dir);
    }
}
