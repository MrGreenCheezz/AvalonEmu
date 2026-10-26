using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
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
/// The guild goals (stage I, analysis/work/stageI/guild-goals/SPEC.md §4 tests 1-11): the client's guild seasons, the Guild Challenge
/// from two members' season points (390, the level-up's 162 after the 351, the journal, 152), the weekly goals paid once with the blessing
/// the week after, the Member Logs (309) and a restart, the PvP credits that count, the ranking with a boss kill's season points (151, 447,
/// 432), the season's end paid once (an offline member at its login after a restart) and the guild might (450, 449, the 68 popup).
/// </summary>
public static class GuildGoalTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("guild goals: gvgseasons_europe (index 11 = GVG_SEASON_34 to 2026-11-09, 99 levels, 2 = 100000/20), a local season after it, goals.json Guild, presets", Data),
        ("guild goals: two members' season points -> 390 total/rows/window, level 2 with 20 season points, 162 popup after 351, journal, 152 rank", Challenge),
        ("guild goals: weekly goal paid once (account, xp, season points), the blessing next week, 309 this/last/all time, a restart keeps it", Weekly),
        ("guild goals: a credited PvP kill counts pvpkills, a refused one (same victim again) nothing", PvpKills),
        ("guild goals: a boss kill's points split between two guilds with 432, 151 by points (18 keys, equal lengths), 447 by activity", Ranking),
        ("guild goals: the season's end pays the bracket once (account, items, journal), an offline member at its login after a restart", SeasonEnd),
        ("guild goals: might from kill fame and a corrupted objective, two PVE levels with their points and 68 popups, 450 and 449", Might),
        ("guild goals: season history 153 (past seasons, rank, points), member history 154, participation 504 (echo, rank, share), the full 351 with SEASON_33's winner", History),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>2026-10-17 12:00 UTC: inside GVG_SEASON_34 (and the October Adventurer's Challenge).</summary>
    private static readonly DateTime Saturday = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static GvgSeasonData Europe => LazyEurope.Value;

    private static readonly Lazy<GvgSeasonData> LazyEurope = new(() => GvgSeasonData.Load(ClientGameDataDir(), "europe"));

    private sealed record Member(WorldSession Session, FakePeer Peer, Player Player);

    /// <summary>Thetford with the client data at <see cref="Saturday"/> + the manual clock; the guild goals of <paramref name="guild"/>.</summary>
    private sealed class Town : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Characters;
        public readonly ServerConfig Config;
        public readonly GoalSettings Goals;
        public World World;

        public Town(ServerPreset preset, GuildGoalSettings? guild = null)
        {
            Characters = Repository(Dir);
            _ = Client.Clusters!.Get("0000");
            _ = Client.Clusters!.Seasons;
            _ = Client.Clusters!.Journal;
            Config = new ServerConfig
            {
                JoinByCharacterId = true, Preset = preset, GuildCreateCost = 1000, GuildRejoinCooldownMinutes = 0, PvpLethalDeath = preset != ServerPreset.Pve, // as the presets
            };
            Goals = GoalSettings.Default with { Guild = guild ?? new GuildGoalSettings() };
            World = Make();
        }

        private World Make() => new(Config, Characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = Clock, Clusters = Client.Clusters, MapSeed = 7, GameTicks = () => Saturday.Ticks + Clock.Now.Ticks,
            Goals = Goals, GvgSeasons = Europe,
        };

        public Member Join(string name, long silver = 5000)
        {
            var c = StarterKit.Default.NewCharacter(name);
            c.Silver = silver * 10000;
            Check(Characters.Create(c), $"character {name} created");
            return Again(c.Id);
        }

        public Member Again(Guid id)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
            return new Member(session, peer, session.Player!);
        }

        /// <summary>The world saved and built again on the same database (a server restart).</summary>
        public void Restart()
        {
            World.Dispose();
            World = Make();
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    private static OperationResponse? Send(Town town, Member m, AlbionRequest req, int? id = 9)
    {
        var n = m.Peer.Responses.Count;
        lock (town.World.Sync) m.Session.OnRequest(Request(req, id), encrypted: false);
        return m.Peer.Responses.Count > n ? m.Peer.Responses[^1] : null;
    }

    /// <summary>A founds "Lone Pines", B joins it.</summary>
    private static Guild Found(Town town, Member a, Member b)
    {
        var w = town.World;
        lock (w.Sync)
        {
            Check(w.Guilds.Create(a.Player, "Lone Pines", GuildLogo.Default, "", out _) == 0, "A founds the guild");
            Check(w.Guilds.Invite(a.Player, b.Player.Character.Name, out _, out _) == 0 && w.Guilds.Accept(b.Player, out _) == 0, "B joins");
        }
        return w.Guilds.Of(a.Player.Character.Id)!;
    }

    /// <summary>StageP territories P2: SEASON_33 (index 10) won by Lone Pines with 5000 points, then this season's answers.</summary>
    private static void History()
    {
        using var town = new Town(ServerPreset.Official);
        var (a, b) = (town.Join("Pia"), town.Join("Quin"));
        var g = Found(town, a, b);
        var w = town.World;
        var s33 = Europe.Seasons[10];
        lock (w.Sync)
        {
            var old = GuildGoals.StateOf(g, s33);
            (old.Points, old.PointsAt, old.Closed) = (5000, 42, true);
            old.Members[a.Player.Character.Id] = 700;
            w.GuildGoals.AddChallenge(g, a.Player.Character.Id, 1000); // this season: only A gives
            w.GuildGoals.GrantPoints(g, 50);
        }
        var h = Send(town, b, new GetGvgSeasonHistoryRankingsRequest { nr = "Lone Pines" });
        Check(h is { ReturnCode: 0 } && Convert.ToInt64(h.Parameters[0]) == 42 && h.Parameters[1] is int[] seasons && seasons.SequenceEqual([10])
              && h.Parameters[2] is int[] ranks && ranks.SequenceEqual([1]) && h.Parameters[3] is long[] pts && pts.SequenceEqual([5000L]),
              "153 Lone Pines: one past season (10 = SEASON_33), rank 1, 5000 points; not the current season");
        var none = Send(town, b, new GetGvgSeasonHistoryRankingsRequest { nr = "Nobody Here" });
        Check(none is { ReturnCode: 0 } && none.Parameters[1] is int[] { Length: 1 }, "153 for an unknown name: the requester's guild");
        var m = Send(town, a, new GetGvgSeasonGuildMemberHistoryRequest { nq = 10 });
        var lengths = Enumerable.Range(0, 12).Select(k => m?.Parameters.GetValueOrDefault((byte)k) is Array arr ? arr.Length / (k == 0 ? 16 : 1) : -1).ToList();
        Check(m is { ReturnCode: 0 } && m.Parameters[1] is string[] names && names.SequenceEqual(["Pia", "Quin"]) && m.Parameters[2] is long[] given
              && given.SequenceEqual([700L, 0]) && lengths.All(n => n == 2),
              $"154 season 10: both members by challenge points (Pia 700, Quin 0), 12 arrays of 2 (got {string.Join("/", lengths)})");
        Guid Echo(OperationResponse r) => new((byte[])r.Parameters[0]!);
        var pa = Send(town, a, new GvGSeasonPlayerGuildParticipationDetailsRequest { pu = 11, pv = a.Player.Character.Id });
        var pb = Send(town, b, new GvGSeasonPlayerGuildParticipationDetailsRequest { pu = 11, pv = b.Player.Character.Id });
        var season = Europe.ClientSeason(Saturday)!;
        var required = (long)((season.End - season.Start).Ticks * 0.51);
        Check(pa is { ReturnCode: 0 } && Echo(pa) == a.Player.Character.Id && Convert.ToInt32(pa.Parameters[1]) == 11 && pa.Parameters[2] is "Lone Pines"
              && Convert.ToInt32(pa.Parameters[12]) == 1 && Convert.ToInt64(pa.Parameters[10]) == GuildGoals.Peek(g, season)!.Points
              && Convert.ToInt64(pa.Parameters[13]) == required && pa.Parameters[14] is true
              && pb is { ReturnCode: 0 } && Echo(pb) == b.Player.Character.Id && !pb.Parameters.ContainsKey(13) && !pb.Parameters.ContainsKey(14),
              "504: 0/1 echo the requester and season, 2 the guild, 10 its points, 12 rank 1, 13 the full required time and 14 true for the giver, 0 / false for the other");
        Check(Send(town, a, new GvGSeasonPlayerGuildParticipationDetailsRequest { pu = 40, pv = a.Player.Character.Id }) is null,
              "504 for a season the client lacks: no answer (c21.a would throw)");
        AlbionLocal.Photon.Serialization.EventData ev;
        lock (w.Sync) ev = AlbionSerializer.BuildEvent(w.GuildGoals.SeasonUpdate(11));
        var p = ev.Parameters;
        Check(Convert.ToInt32(p[0]) == 11 && Convert.ToInt32(p[1]) == 10 && p[2] is byte[] id && new Guid(id) == g.Id && p[3] is "Lone Pines"
              && !p.ContainsKey(4) && p[5] is "" && p[6] is "" && Convert.ToInt32(p[11]) == GuildLogo.Default.SymbolColor && Convert.ToSingle(p[12]) == 1f
              && Convert.ToInt64(p[14]) == 5000 && Convert.ToInt32(p[15]) == 1,
              "351: 0 season 11, 1 10, 2/3 SEASON_33's winner, 5/6 \"\" (no alliance), 11 the symbol colour, 12 the scale, 14 its 5000 points, 15 one win");
        var rex = town.Join("Rex");
        var sent = rex.Peer.Events.LastOrDefault(e => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode)) == (int)EventCodes.GvgSeasonUpdate);
        Check(sent is not null && sent.Parameters[3] is "Lone Pines", "the 351 after a Join carries the winner");
    }

    private static void Data()
    {
        var s34 = Europe.ClientSeason(Saturday);
        Check(Europe.Seasons.Count == 12 && s34 is { Index: 11, Key: "GVG_SEASON_34", IsClient: true } && s34.End == new DateTime(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc)
              && s34.Levels.Count == 99 && s34.Row(2) is { Points: 100_000, SeasonPoints: 20 } && s34.Row(99) is { Points: 485_100_000, SeasonPoints: 300 }
              && s34.Levels.SequenceEqual(GvgSeasonData.DefaultLevels) && s34.Brackets.Count == 5 && s34.BracketOf(7_500)?.Name == "bronze"
              && s34.LevelOf(99_999) == 1 && s34.LevelOf(100_000) == 2,
              $"gvgseasons_europe: 12 seasons, 2026-10-17 in index 11 GVG_SEASON_34 to 2026-11-09 11:00, 99 levels (2 = 100000/20, the built-in formula), brackets (got {s34?.Key})");
        var after = Europe.Current(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), 28);
        Check(after is { IsClient: false, Key: "LOCAL@0" } && after.Start == s34!.End && after.End == s34.End.AddDays(28) && after.Levels.Count == 99
              && Europe.Current(s34.End.AddDays(29), 28).Key == "LOCAL@1" && GvgSeasonData.Fallback.Current(Saturday, 28) is { IsClient: false },
              "2026-11-10: the local season LOCAL@0 from SEASON_34's end, 28 days, its table; without data local seasons only");
        Check(GuildGoals.TwoDigits(1234) == 1200 && GuildGoals.TwoDigits(87.4) == 87 && GuildGoals.TwoDigits(0.3) == 1 && GuildGoals.TwoDigits(522.3) == 520,
              "targets: two significant digits, at least 1");
        var pveMight = s34!.MightCategory("PVE");
        Check(s34.Might.Count == 14 && s34.Might[0].Name == "PVE" && pveMight is { Levels.Count: 99 } && pveMight.Levels[0] == new MightLevel(20_000 * 10000L, 200)
              && pveMight.LevelOf(60_000 * 10000L) == 2 && s34.MightCategory("CORRUPTED")!.Objectives.SetEquals(["CORRUPTED_STALKER", "CORRUPTED_SLAYER"])
              && Europe.Seasons[10].Might.Count > 0 && after.Might == s34.Might
              && Europe.Might.Fame["pve"] is { Objective: "PVPCHALLENGE_PVE", Factor: 0.0016, SoloDungeonFactor: 0.33 } pveRule && pveRule.ZoneFactor("orange") == 0 && pveRule.ZoneFactor("red") == 1
              && Europe.Might.Objectives["PVPCHALLENGE_CORRUPTED_SLAYER"] == ("CORRUPTED_SLAYER", 180.88),
              $"might: SEASON_34 the DEFAULT template (14 categories, PVE 99 rows from 20000 might / 200 points), SEASON_33 its own, the local season the last one's; the pve rule 0.0016 (solo 0.33, orange 0), SLAYER 180.88 (got {s34.Might.Count})");

        var goals = GoalSettings.Load(Path.Combine(DataDir(), "..", "content", "goals.json"));
        Check(goals.Guild is { Enabled: true, Region: "europe", ChallengeFactor: null, SeasonPointsFactor: null, Weekly: null, MightFactor: null, GoalsPerWeek: 3, MinParticipation: 0.51 }
              && goals.Guild.Pools.Keys.Order().SequenceEqual(["crafting", "gathering", "pve", "pvp"]) && goals.Guild.Pools["pve"].Count == 3
              && goals.Guild.BossSeasonPoints["worldboss"] == 20 && goals.Guild.BracketSilver["crystal"] == 8_000_000 && goals.Guild.BracketItems["bronze"]["T4_SHARD_CRYSTAL"] == 20,
              "content/goals.json Guild: on, europe, the preset keys left to the preset, four pools, boss points, bracket silver and items");
        static bool Refused(string text)
        {
            try { GoalSettings.Parse(text); return false; }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException) { return true; }
        }
        Check(Refused("""{ "Guild": { "Pools": { "pve": [ { "Kind": "dance" } ] } } }""") && Refused("""{ "Guild": { "Region": "mars" } }""")
              && Refused("""{ "Guild": { "Weekley": true } }""") && Refused("""{ "Guild": { "BracketSilver": { "wood": 5 } } }""") && Refused("""{ "Guild": { "BracketItems": { "Iron": { "T4_SHARD_CRYSTAL": 1 } } } }""")
              && Refused("""{ "Guild": { "BossSeasonPoints": { "dragon": 5 } } }""") && GoalSettings.Parse("{}").Guild.Pools.Count == 4,
              "goals.json Guild: a bad kind, region, key, bracket or boss kind refused; {} = the built-in pools");

        using var official = new Town(ServerPreset.Official);
        using var pve = new Town(ServerPreset.Pve);
        Check(official.World.GuildGoals is { ChallengeFactor: 1, PointsFactor: 1, CraftFactor: 0.002, WeeklyOn: false, PvpFameOn: true, MightFactor: 1 }
              && pve.World.GuildGoals is { ChallengeFactor: 20, PointsFactor: 5, CraftFactor: 0.002, WeeklyOn: true, PvpFameOn: false, MightFactor: 10 },
              "presets: official factor 1 / points 1 / might 1 / no weekly goals, pve 20 / 5 / 10 / weekly without PvP fame; crafts 0.002 in both");
    }

    private static void Challenge()
    {
        using var town = new Town(ServerPreset.Official);
        var (a, b) = (town.Join("Gilda"), town.Join("Hugo"));
        Check(Since(a.Peer, 0).Where(e => e.Code == EventCodes.GvgSeasonUpdate).ToList() is [var update] && update.P[0] is 11 && update.P[1] is 10 && a.Player.GvgSeasonSent == 11,
              "Join in a client season: one GvgSeasonUpdate {0: 11, 1: 10}");
        var g = Found(town, a, b);
        Check(Send(town, a, new GetGvgSeasonRankRequest { ns = 11, nt = g.Name }) is { ReturnCode: 0 } none && none.Parameters[0] is -1 && !none.Parameters.ContainsKey(1),
              "152 without points: {0: -1}, no key 1");

        var marks = (a.Peer.Events.Count, b.Peer.Events.Count);
        lock (town.World.Sync)
        {
            Seasons.Gain(a.Player, 60_000 * 10000L);
            Seasons.Gain(b.Player, 40_000 * 10000L);
        }
        var st = GuildGoals.Peek(g, Europe.ClientSeason(Saturday)!)!;
        Check(st is { Challenge: 100_000, Level: 2, Points: 20 } && st.Activity[GuildGoals.ChallengeLevelUp] == 20,
              $"challenge 60000 + 40000 (factor 1): level 2, 20 season points as ChallengeLevelUp (got {st.Challenge}, {st.Level}, {st.Points})");
        Check(Since(a.Peer, marks.Item1).Where(e => e.Code == EventCodes.GameEvent).ToList() is [var popup] && popup.P[0] is 40 && popup.P[1] is 4
              && popup.P[2] is """{"level":2,"season":11}""" && Since(b.Peer, marks.Item2).Count(e => e.Code == EventCodes.GameEvent) == 1,
              "the level-up: GameEvent 162 {0: 40, 1: 4, 2: level 2 / season 11} to both members");
        var journal = Client.Clusters!.Journal;
        var level2 = journal.LeavesOf("reachguildchallengelevel").First(l => l.Goal == 2).Achievement.Name;
        var points = journal.LeavesOf("gainchallengepoints").First(l => l.Goal == 10_000 * 10000L).Achievement.Name;
        Check(a.Player.Character.Goals!.Done.ContainsKey(level2) && b.Player.Character.Goals!.Done.ContainsKey(level2) && a.Player.Character.Goals.Done.ContainsKey(points),
              $"journal: {level2} done for both, {points} (10000 challenge points) for A");

        var r = Send(town, b, new GetGuildChallengePointsRequest { nb = g.Id, nd = 16 });
        Check(r is { ReturnCode: 0 } && new Guid((byte[])r.Parameters[0]!) == g.Id && r.Parameters[1] is long stamp && stamp == st.Updated
              && r.Parameters[2] is 100_000 && r.Parameters[3] is 2 && !r.Parameters.ContainsKey(4)
              && r.Parameters[5] is string[] names && names.SequenceEqual(["Gilda", "Hugo"]) && r.Parameters[6] is int[] rows && rows.SequenceEqual([60_000, 40_000]),
              "390: guild, its last change, total 100000, 2 members, rows by points");
        var window = Send(town, b, new GetGuildChallengePointsRequest { nb = g.Id, nc = 1, nd = 1 });
        var other = Send(town, b, new GetGuildChallengePointsRequest { nb = Guid.NewGuid(), nd = 16 });
        Check(window is { ReturnCode: 0 } && window.Parameters[4] is 1 && window.Parameters[5] is string[] { Length: 1 } one && one[0] == "Hugo" && window.Parameters[6] is int[] { Length: 1 } hugo && hugo[0] == 40_000
              && other is { ReturnCode: 0 } && !other.Parameters.ContainsKey(3) && !other.Parameters.ContainsKey(5),
              "390 first 1 count 1: one row from index 1; another guild's Guid: no members, no rows");
        Check(Send(town, a, new GetGvgSeasonRankRequest { ns = 11, nt = g.Name }) is { ReturnCode: 0 } rank && rank.Parameters[0] is 1 && rank.Parameters[1] is 20L,
              "152 after the level-up: {0: 1, 1: 20}");
        var own = Send(town, b, new GetGuildChallengePointsRequest { nb = g.Id, nd = 1, ne = "Hugo" });
        Check(own is { ReturnCode: 0 } && own.Parameters[4] is 1 && own.Parameters[5] is string[] { Length: 1 } mine && mine[0] == "Hugo",
              "390 with the member's name and no first row: the window that holds its row");

        lock (town.World.Sync)
        {
            town.World.GuildGoals.OnChallenge(a.Player, 5000);
            town.World.GuildGoals.OnChallenge(a.Player, 5000);
            var goals = b.Player.Character.Goals!;
            var leaf = journal.LeavesOf("reachseasonpoints").First(l => !goals.Done.ContainsKey(l.Achievement.Name));
            (goals.Journal[leaf.Key], goals.JournalVersion) = (99, 0);
            AlbionJournal.GuildSeasonPoints(b.Player);
            Check(st.Challenge == 100_001 && !goals.Journal.ContainsKey(leaf.Key) && goals.JournalVersion == 1,
                  $"two half points add up to one (got {st.Challenge}); an old reachseasonpoints progress is dropped once");
        }
    }

    private static void Weekly()
    {
        var settings = new GuildGoalSettings
        {
            GoalsPerWeek = 1, TargetExponent = 0,
            Pools = new Dictionary<string, IReadOnlyList<GuildGoalEntry>> { ["gathering"] = [new GuildGoalEntry { Kind = "gather", Target = 10 }] },
        };
        using var town = new Town(ServerPreset.Pve, settings);
        var (a, b) = (town.Join("Ida"), town.Join("Jon"));
        var g = Found(town, a, b);
        var w = town.World;
        var ore = Client.Items.Find("T4_ORE")!;
        GuildWeek week;
        lock (w.Sync)
        {
            week = w.GuildGoals.WeekOf(g)!;
            var first = week.Goals.Single();
            g.Progress.Week = null;
            var again = w.GuildGoals.WeekOf(g)!.Goals.Single();
            Check(first is { Kind: "gather", Target: 10, Progress: 0 } && again.Kind == first.Kind && again.Tier == first.Tier && again.Target == first.Target,
                  "the weekly roll: one gather goal of 10, the same when rolled again for the guild and week");
            week = g.Progress.Week!;
        }
        var goal = week.Goals.Single();
        var (silver, xp) = (g.SilverA7m, g.Xp);
        lock (w.Sync)
        {
            w.GuildGoals.OnGather(a.Player, ore, 6);
            w.GuildGoals.OnGather(b.Player, ore, 6);
            w.GuildGoals.OnGather(a.Player, ore, 6);
        }
        var paid = 100_000L * (goal.Tier > 0 ? goal.Tier : 4) / 4;
        var state = GuildGoals.Peek(g, w.GuildGoals.Season)!;
        Check(goal is { Done: true, Progress: 10 } && g.SilverA7m - silver == paid * 10000 && g.Xp - xp == 5000 && state.Points == 750
              && state.Activity[GuildGoals.ChallengeLevelUp] == 750,
              $"6 + 6 ore: done once, {paid} silver into the account, 5000 guild xp, 150 x 5 season points (got {(g.SilverA7m - silver) / 10000}, {g.Xp - xp}, {state.Points})");

        // The Member Logs: A's kill fame this week.
        var fame = 120 * 10000L;
        lock (w.Sync) Destiny.Award(a.Player, fame, Destiny.Kill(a.Player, 4));
        long[] Stats(Member m, int frame, int stat) => (long[])Send(town, m, new QueryGuildPlayerStatsRequest { yx = frame, yy = stat }, null)!.Parameters[1]!;
        var ids = (byte[])Send(town, b, new QueryGuildPlayerStatsRequest { yx = 1, yy = GuildMemberLog.PveFame }, null)!.Parameters[0]!;
        var thisWeek = Stats(b, 1, GuildMemberLog.PveFame);
        Check(ids.Length == 32 && thisWeek.SequenceEqual([fame, 0]) && Stats(b, 2, GuildMemberLog.PveFame).SequenceEqual([0L, 0])
              && Stats(b, 0, GuildMemberLog.PveFame)[0] == fame && Stats(b, 1, GuildMemberLog.GatheringFame).All(v => v == 0),
              $"309 PVEFAME: this week A's 120 kill fame, last week 0, all time 120 (got {string.Join(", ", thisWeek)})");

        town.Clock.Advance(TimeSpan.FromDays(7));
        lock (w.Sync)
        {
            var blessing = w.GuildGoals.Blessing(a.Player);
            Check(blessing == (0.05, 0.05, 0.03) && w.Guilds.FameBonus(a.Player) == 0.05 && g.Progress.LastWeek?.Start == week.Start && g.Progress.Week!.Blessing == 1,
                  $"next week: blessing 1 step (+5 % fame, +5 % silver, +3 % yield) from last week's goal (got {blessing})");
        }
        Check(Stats(b, 1, GuildMemberLog.PveFame).All(v => v == 0) && Stats(b, 2, GuildMemberLog.PveFame)[0] == fame && Stats(b, 0, GuildMemberLog.PveFame)[0] == fame,
              "309 a week later: this week 0, last week 120, all time 120");

        var id = g.Id;
        town.Restart();
        var loaded = town.World.Guilds.ById(id)!;
        Check(loaded.Progress is { Week.Blessing: 1, LastWeek.Goals: [{ Done: true, Progress: 10 }] } && GuildGoals.Peek(loaded, town.World.GuildGoals.Season) is { Points: 750, Challenge: > 0 }
              && loaded.Progress.Logs[a.Player.Character.Id].All[GuildMemberLog.PveFame] == fame,
              "a restart keeps the weeks, the done goal, the season points and challenge, the member logs");
    }

    private sealed record TestBot(string PersonaId, string Name, byte Flag, int Tier, long Id) : IPvpBot;

    private static void PvpKills()
    {
        var settings = new GuildGoalSettings
        {
            GoalsPerWeek = 2,
            Pools = new Dictionary<string, IReadOnlyList<GuildGoalEntry>>
            {
                ["pvp"] = [new GuildGoalEntry { Kind = "pvpkills", Target = 3 }, new GuildGoalEntry { Kind = "pvpfame", Target = 100 }],
            },
        };
        using var town = new Town(ServerPreset.Pve, settings);
        var (a, b) = (town.Join("Kara"), town.Join("Lev"));
        var g = Found(town, a, b);
        var w = town.World;
        var map = a.Player.Map!;
        lock (w.Sync)
        {
            // The zone of Thetford with points, so that the credit counts (the PvP goals' ZoneFactors).
            var warning = Pvp.RulesOf(w, map).WarningType;
            w.Goals = w.Goals with { Pvp = new PvpGoalSettings { ZoneFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [warning] = 1 } } };
            var bot = PvpActor.Of(new TestBot("bot-1", "Raider", 255, 6, 999_001));
            PvpKill Kill() => new(PvpActor.Of(a.Player), bot, [], map, PvpOutcome.Knockdown, false, 0, w.GameNow, 1);
            PvpGoals.OnPvpKill(w, Kill());
            var goal = w.GuildGoals.WeekOf(g)!.Goals.Single();
            Check(goal is { Kind: "pvpkills", Progress: 1 }, $"pve (no PvP fame): pvpfame not rolled; a credited knockdown of a T6 bot: pvpkills 1 (got {goal.Progress})");
            PvpGoals.OnPvpKill(w, Kill());
            Check(goal.Progress == 1 && a.Player.Character.Goals!.PvpRefused.GetValueOrDefault("same victim cooldown") == 1,
                  "the same victim again within the cooldown: refused by the PvP goals, pvpkills stays 1");
        }
    }

    private static void Ranking()
    {
        using var town = new Town(ServerPreset.Official);
        var (a, b, c) = (town.Join("Mira"), town.Join("Nils"), town.Join("Olga"));
        var g = Found(town, a, b);
        var w = town.World;
        Guild oaks;
        var boss = Client.Clusters!.Mobs.All.First(t => t.Category == "boss");
        var marks = (a.Peer.Events.Count, b.Peer.Events.Count, c.Peer.Events.Count);
        lock (w.Sync)
        {
            Check(w.Guilds.Create(c.Player, "Red Oaks", GuildLogo.Default with { Symbol = 3 }, "", out _) == 0, "C founds a second guild");
            oaks = w.Guilds.Of(c.Player.Character.Id)!;
            var mob = SpawnManager.SpawnAt(a.Player.Map!, boss, new System.Numerics.Vector2(a.Player.Character.PositionX, a.Player.Character.PositionY), 0, w);
            w.GuildGoals.OnKill(a.Player.Map!, a.Player, mob, [a.Player, c.Player]);
            w.GuildGoals.GrantPoints(g, 300);
            w.GuildGoals.GrantPoints(oaks, 100);
        }
        var season = Europe.ClientSeason(Saturday)!;
        Check(GuildGoals.Peek(g, season) is { Points: 303 } mine && mine.Activity[GuildGoals.KillMob] == 3 && GuildGoals.Peek(oaks, season)!.Activity[GuildGoals.KillMob] == 3,
              "a boss (5 points) killed by members of two guilds: 3 each as KillMob (2.5 rounded)");
        var points = Since(a.Peer, marks.Item1).Where(e => e.Code == EventCodes.ReceivedGvgSeasonPoints).ToList();
        Check(points is [var got] && L(got.P, 0) == a.Player.Id && L(got.P, 1) == 3 && L(got.P, 2) == boss.Id
              && Since(c.Peer, marks.Item3).Count(e => e.Code == EventCodes.ReceivedGvgSeasonPoints) == 1 && Since(b.Peer, marks.Item2).All(e => e.Code != EventCodes.ReceivedGvgSeasonPoints),
              "432 {0 the receiver's own id, 1 the gain 3, 2 the mob type} to each credited member, none to the one not there");

        var r = Send(town, b, new GetGvgSeasonRankingsRequest { nu = 11, nw = 7 });
        var lengths = Enumerable.Range(3, 15).Select(k => r?.Parameters.GetValueOrDefault((byte)k) is Array arr ? arr.Length / (k is 4 or 14 ? 16 : 1) : -1).ToList();
        Check(r is { ReturnCode: 0 } && r.Parameters[1] is 2 && r.Parameters[3] is string[] names && names.SequenceEqual(["Lone Pines", "Red Oaks"])
              && r.Parameters[15] is long[] scores && scores.SequenceEqual([303L, 103]) && r.Parameters[6] is int[] symbols && symbols.SequenceEqual([0, 3])
              && r.Parameters[10] is float[] && lengths.All(n => n == 2),
              $"151: 2 guilds by points 303 / 103, the crest, keys 3-17 all of 2 rows (got {string.Join(",", lengths)})");
        var none = Send(town, b, new GetGvgSeasonRankingsRequest { nu = 3, nw = 7 });
        Check(none is { ReturnCode: 0 } && !none.Parameters.ContainsKey(1) && none.Parameters[15] is long[] { Length: 0 } && none.Parameters[3] is string[] { Length: 0 },
              "151 of a past season: no guilds, empty arrays");
        var by = Send(town, b, new GetGvgSeasonContributionByActivityRequest { nl = g.Id });
        Check(by is { ReturnCode: 0 } && by.Parameters[0] is 11 && by.Parameters[3] is 303L && by.Parameters[4] is string[] acts && acts.SequenceEqual([GuildGoals.ChallengeLevelUp, GuildGoals.KillMob])
              && by.Parameters[5] is long[] values && values.SequenceEqual([300L, 3]),
              "447: season 11, total 303, ChallengeLevelUp 300 and KillMob 3");
        Check(Send(town, b, new GetGvgSeasonRankRequest { ns = 11, nt = "Red Oaks" }) is { } rank && rank.Parameters[0] is 2 && rank.Parameters[1] is 103L,
              "152 of the other guild by name: rank 2, 103 points");
    }

    private static void SeasonEnd()
    {
        using var town = new Town(ServerPreset.Official);
        var (a, b) = (town.Join("Pia"), town.Join("Quin"));
        var g = Found(town, a, b);
        var w = town.World;
        var season = Europe.ClientSeason(Saturday)!;
        lock (w.Sync)
        {
            Seasons.Gain(a.Player, 60_000 * 10000L);
            Seasons.Gain(b.Player, 40_000 * 10000L);
            w.GuildGoals.GrantPoints(g, 7_480);
            w.GuildGoals.Update();
            b.Peer.Disconnect();
        }
        var silver = g.SilverA7m;
        town.Clock.Advance(TimeSpan.FromDays(24));
        lock (w.Sync) w.GuildGoals.Update();
        var st = GuildGoals.Peek(g, season)!;
        var shards = a.Player.Inventory.Bag.Items.Where(i => i.Template.Name == "T4_SHARD_CRYSTAL").Sum(i => i.Count);
        var done = a.Player.Character.Goals!.Done;
        Check(st is { Closed: true, Points: 7_500 } && g.SilverA7m - silver == 1_000_000 * 10000L && shards == 20
              && done.ContainsKey("JOURNAL_GUILD_GUILD_SEASON_BRONZE_BRACKET") && done.ContainsKey("JOURNAL_GUILD_GUILD_SEASON_IRON_BRACKET")
              && !done.ContainsKey("JOURNAL_GUILD_GUILD_SEASON_SILVER_BRACKET") && g.Progress.Owed.Keys.SequenceEqual([b.Player.Character.Id])
              && w.GuildGoals.Season is { IsClient: false, Key: "LOCAL@0" } && GuildGoals.Peek(g, w.GuildGoals.Season) is null,
              $"SEASON_34 ended at 7500 points: bronze, 1 M silver into the account, A (in game) 20 T4 shards and the iron and bronze journal rewards, B owed; LOCAL@0 at 0 (got {shards} shards)");
        lock (w.Sync) w.GuildGoals.CloseSeason(season.Key);
        Check(g.SilverA7m - silver == 1_000_000 * 10000L && g.Progress.Owed.Count == 1, "closing it again pays nothing");
        string? key;
        lock (w.Sync)
        {
            w.GuildGoals.GrantPoints(g, 7_480);
            key = w.GuildGoals.EndSeason();
        }
        var local = w.GuildGoals.Season;
        var paid = local.BracketOf(7_480) is { } lb ? w.Goals.Guild.BracketSilver.GetValueOrDefault(lb.Name) : 0;
        Check(key == "LOCAL@0#1" && g.Progress.Seasons.Single(x => x.Season == key).Closed && GuildGoals.Peek(g, local) is null && paid > 0
              && g.SilverA7m - silver == (1_000_000 + paid) * 10000L && g.Progress.Owed.Count == 1
              && a.Player.Inventory.Bag.Items.Where(i => i.Template.Name == "T4_SHARD_CRYSTAL").Sum(i => i.Count) == 20,
              "GM season end of LOCAL@0 (points from a GM only): kept as #1 and paid, the season at 0 again, no member reward (nobody took part)");

        var id = g.Id;
        town.Restart();
        Check(town.World.Guilds.ById(id)!.Progress.Owed.ContainsKey(b.Player.Character.Id), "a restart keeps B's reward owed");
        var back = town.Again(b.Player.Character.Id);
        Check(back.Player.Inventory.Bag.Items.Where(i => i.Template.Name == "T4_SHARD_CRYSTAL").Sum(i => i.Count) == 20
              && back.Player.Character.Goals!.Done.ContainsKey("JOURNAL_GUILD_GUILD_SEASON_BRONZE_BRACKET") && town.World.Guilds.ById(id)!.Progress.Owed.Count == 0,
              "B's login: its 20 shards and the journal reward, nothing owed any more");
    }

    private static void Might()
    {
        using var town = new Town(ServerPreset.Pve);
        var (a, b) = (town.Join("Rhea"), town.Join("Sven"));
        var g = Found(town, a, b);
        var w = town.World;
        var season = Europe.ClientSeason(Saturday)!;
        var mark = a.Peer.Events.Count;
        GuildMight pve;
        lock (w.Sync)
        {
            Destiny.Award(a.Player, 1000 * 10000L, Destiny.Kill(a.Player, 4));
            pve = GuildGoals.Peek(g, season)!.Might["PVE"];
            Check(pve is { Total: 160_000, Level: 0 } && pve.Members[a.Player.Character.Id] == 160_000,
                  $"1000 kill fame in Thetford: 16 PVE might (x 0.0016 x MightFactor 10) for A (got {pve.Total})");
            w.GuildGoals.GrantMight(g, a.Player.Character.Id, "PVE", 60_000 - 16);
        }
        var st = GuildGoals.Peek(g, season)!;
        var popups = Since(a.Peer, mark).Where(e => e.Code == EventCodes.GameEvent && e.P[0] is 68).Select(e => e.P[2] as string).ToList();
        Check(pve is { Total: 600_000_000, Level: 2 } && st.Activity[GuildGoals.MightLevelUp + "PVE"] == 400
              && popups.SequenceEqual(["""{"level":0,"category":"PVE","season":11}""", """{"level":1,"category":"PVE","season":11}"""]),
              $"60000 PVE might: levels 1 and 2 (200 season points each, MightCategoryLevelUp@PVE), the 68 popups with the rows 0 and 1 (got {pve.Level}, {string.Join(" ", popups)})");

        var overview = Send(town, b, new GetGuildMightCategoryOverviewRequest { nk = g.Id });
        Check(overview is { ReturnCode: 0 } && overview.Parameters[2] is string[] cats && cats.Length == 14 && overview.Parameters[3] is long[] might
              && might[Array.IndexOf(cats, "PVE")] == 600_000_000 && might.Count(v => v > 0) == 1,
              "450: the 14 categories of the season, PVE 60000 might (a7m), the others 0");
        var list = Send(town, b, new GetGuildMightCategoryContributionRequest { nf = g.Id, ng = "PVE", ni = 10 });
        Check(list is { ReturnCode: 0 } && list.Parameters[1] is "PVE" && list.Parameters[3] is 600_000_000L && list.Parameters[4] is 2
              && list.Parameters[6] is string[] names && names.SequenceEqual(["Rhea", "Sven"]) && list.Parameters[7] is long[] rows && rows.SequenceEqual([600_000_000L, 0]),
              "449 PVE: the guild's might, 2 members, rows by might");
        lock (w.Sync) w.GuildGoals.OnObjective(a.Player, "PVPCHALLENGE_CORRUPTED_SLAYER");
        Check(st.Might["CORRUPTED"].Total == 18_088_000, "a Slayer corrupted dungeon: 180.88 x 10 CORRUPTED might");
    }
}
