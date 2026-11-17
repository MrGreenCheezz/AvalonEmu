using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>StageR gold (analysis/work/stageR/gold/SPEC.md): the gold rewards of play (not official, Gold:Rewards), one test per source and the master key off.</summary>
public static class GoldRewardsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("gold rewards: the day's first 10000 fame gives 100 gold (81 with key 2, a system message) once a day, the cap is saved, the next day again", DailyFame),
        ("gold rewards: the Conqueror's weekly might at the chest gives 250 gold once a week, premium or not", ConquerorWeekly),
        ("gold rewards: an Albion Journal claim (514) gives 10 gold, cut to the weekly cap of 200", JournalClaim),
        ("gold rewards: the faction campaign's last daily reward gives 50 gold once a campaign day", FactionDaily),
        ("gold rewards: Gold:Rewards:Enabled false gives no gold", Off),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private const long Gold = 10000;

    /// <summary>The UpdateMoney events and the system messages since <paramref name="mark"/>.</summary>
    private static (List<Dictionary<byte, object?>> Money, List<string> Text) Seen(FakePeer peer, int mark)
    {
        var events = Since(peer, mark);
        return ([.. events.Where(e => e.Code == EventCodes.UpdateMoney).Select(e => e.P)],
                [.. events.Where(e => e.Code == EventCodes.SystemMessage).Select(e => e.P.GetValueOrDefault((byte)0) as string ?? "")]);
    }

    /// <summary>A world of <paramref name="config"/> (JoinByCharacterId added) with a character joined in Thetford.</summary>
    private static (World World, WorldSession S, FakePeer Peer, ManualClock Clock, Server.Persistence.SqliteCharacterRepository Repo, string Dir) Start(
        ServerConfig config, DateTime? start = null, bool seasons = false)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = start?.Ticks ?? GameTime.Now;
        var world = new World(config with { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Client.Clusters!, GameTicks = () => anchor + clock.Now.Ticks, MapSeed = 3,
            GvgSeasons = seasons ? GvgSeasonData.Load(ClientGameDataDir(), "europe") : null,
        };
        world.MobAi = false;
        var c = StarterKit.Default.NewCharacter("Goldie");
        c.Cluster = "0000";
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var s = new WorldSession(peer, world);
        lock (world.Sync) s.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
        return (world, s, peer, clock, characters, dir);
    }

    private static void Stop(World world, Server.Persistence.SqliteCharacterRepository repo, string dir)
    {
        world.Dispose();
        repo.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void DailyFame()
    {
        var (world, s, peer, clock, repo, dir) = Start(new ServerConfig());
        try
        {
            var me = s.Player!;
            var fame = Destiny.Kill(me, 4);
            lock (world.Sync) Destiny.Award(me, 6_000 * Gold, fame);
            Check(me.Character.Gold == 0, "6000 fame: no gold yet");

            var mark = peer.Events.Count;
            lock (world.Sync) Destiny.Award(me, 5_000 * Gold, fame);
            var (money, text) = Seen(peer, mark);
            Check(me.Character.Gold == 100 * Gold && money.Count == 1 && L(money[0], 2) == 100 * Gold && text.Any(t => t.StartsWith("[Gold] +100 gold")),
                  $"11000 fame: 100 gold, 81 key 2 {(money.Count > 0 ? money[0].GetValueOrDefault((byte)2) : null)}, message '{string.Join("|", text)}'");

            lock (world.Sync)
            {
                Destiny.Award(me, 50_000 * Gold, fame);
                Destiny.Award(me, 50_000 * Gold, Destiny.Harvest(Client.Items.Find("T4_FIBER")!));
            }
            Check(me.Character.Gold == 100 * Gold, "more fame the same day: no more gold");

            lock (world.Sync) repo.Save(me.Character.Clone());
            Check(repo.Find(me.Character.Id)?.Goals?.Gold is { DailyGiven: true } && repo.Find(me.Character.Id)!.Gold == 100 * Gold, "the daily cap and the gold are saved");

            clock.Advance(TimeSpan.FromDays(1));
            lock (world.Sync) Destiny.Award(me, 10_000 * Gold, Destiny.Harvest(Client.Items.Find("T4_FIBER")!));
            Check(me.Character.Gold == 200 * Gold, $"the next day 10000 gathering fame: 100 gold again (gold {me.Character.Gold / Gold})");
        }
        finally { Stop(world, repo, dir); }
    }

    private static void ConquerorWeekly()
    {
        var start = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc); // inside SEASON_34, as StatsTest
        var (world, s, peer, clock, repo, dir) = Start(ServerConfig.PveDefaults with { PremiumMode = PremiumMode.None }, start, seasons: true);
        try
        {
            var me = s.Player!;
            var pve = world.GvgSeasons!.Might.Fame["pve"].Objective;
            lock (world.Sync) Conqueror.Add(me, pve, null, 12_000 * 10000.0);
            Check(me.Character.Gold == 0, "12000 might: no gold");
            var mark = peer.Events.Count;
            lock (world.Sync) Conqueror.Add(me, pve, null, 9_000 * 10000.0);
            var (_, text) = Seen(peer, mark);
            Check(me.Character.Gold == 250 * Gold && text.Any(t => t.Contains("Conqueror")), $"21000 might (no premium): 250 gold ({me.Character.Gold / Gold}), '{string.Join("|", text)}'");
            lock (world.Sync) Conqueror.Add(me, pve, null, 5_000 * 10000.0);
            Check(me.Character.Gold == 250 * Gold, "more might the same week: no more gold");
            clock.Advance(TimeSpan.FromDays(7));
            lock (world.Sync) Conqueror.Add(me, pve, null, 21_000 * 10000.0);
            Check(me.Character.Gold == 500 * Gold, "the next week: 250 gold again");
        }
        finally { Stop(world, repo, dir); }
    }

    private static void JournalClaim()
    {
        _ = Client.Clusters!.Journal;
        var (world, s, peer, _, repo, dir) = Start(new ServerConfig());
        try
        {
            var me = s.Player!;
            const string T2 = "JOURNAL_GATHERING_RESOURCES_ANY_TYPE_T2";
            lock (world.Sync)
            {
                AlbionJournal.Gathered(me, Client.Items.Find("T2_FIBER")!, 80);
                GoldRewards.LogOf(me).JournalGold = 195; // this week's journal gold nearly complete
            }
            OperationResponse Ask(AlbionRequest req)
            {
                var n = peer.Responses.Count;
                lock (world.Sync) s.OnRequest(Request(req, 9), encrypted: false);
                return peer.Responses.Skip(n).Single();
            }
            var before = me.Character.Gold;
            var mark = peer.Events.Count;
            var claim = Ask(new ClaimAlbionJournalRewardRequest { fi = [T2] });
            var (money, text) = Seen(peer, mark);
            Check(claim.Parameters[0] is short[] and [0] && me.Character.Gold - before == 5 * Gold && money.Count == 1 && text.Any(t => t.Contains(T2)),
                  $"the claim [0] gives 5 gold (10 cut to the cap 200): {(me.Character.Gold - before) / Gold}, '{string.Join("|", text)}'");
            var again = Ask(new ClaimAlbionJournalRewardRequest { fi = [T2] });
            Check(again.Parameters[0] is short[] and [AlbionJournal.AlreadyClaimed] && me.Character.Gold - before == 5 * Gold && GoldRewards.LogOf(me).JournalGold == 200,
                  "claimed again: 20561, no gold");
        }
        finally { Stop(world, repo, dir); }
    }

    private static void FactionDaily()
    {
        var (world, s, peer, clock, repo, dir) = Start(ServerConfig.PveDefaults with { PremiumMode = PremiumMode.None });
        try
        {
            var me = s.Player!;
            lock (world.Sync)
            {
                world.Factions.Enlist(me, 5, force: true);
                world.Factions.Earn(me, 3_000 * Gold, null);
            }
            Check(me.Character.Gold == 0, "3000 points (two of the three daily rewards): no gold");
            var mark = peer.Events.Count;
            lock (world.Sync) world.Factions.Earn(me, 2_500 * Gold, null);
            var (_, text) = Seen(peer, mark);
            Check(me.Character.Gold == 50 * Gold && text.Any(t => t.Contains("faction campaign")), $"5500 points: 50 gold ({me.Character.Gold / Gold}), '{string.Join("|", text)}'");
            lock (world.Sync) world.Factions.Earn(me, 10_000 * Gold, null);
            Check(me.Character.Gold == 50 * Gold, "more points the same day: no more gold");
            clock.Advance(TimeSpan.FromDays(1));
            lock (world.Sync) world.Factions.Earn(me, 5_000 * Gold, null);
            Check(me.Character.Gold == 100 * Gold, $"the next day: 50 gold again ({me.Character.Gold / Gold})");
        }
        finally { Stop(world, repo, dir); }
    }

    private static void Off()
    {
        var (world, s, peer, _, repo, dir) = Start(new ServerConfig { GoldRewards = new GoldRewardOptions { Enabled = false } });
        try
        {
            var me = s.Player!;
            var mark = peer.Events.Count;
            lock (world.Sync)
            {
                Destiny.Award(me, 50_000 * Gold, Destiny.Kill(me, 4));
                GoldRewards.ConquerorWeekly(me);
                GoldRewards.JournalClaimed(me, "X");
                GoldRewards.FactionDaily(me, 1);
            }
            var (money, text) = Seen(peer, mark);
            Check(me.Character.Gold == 0 && money.Count == 0 && !text.Any(t => t.StartsWith("[Gold]")), "off: no gold, no 81, no message");
        }
        finally { Stop(world, repo, dir); }
    }
}
