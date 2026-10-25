using System.Numerics;
using AlbionLocal.Server.Bots;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage N arena (analysis/work/stageN/arena/SPEC.md): two players queue for the 1v1 at the agency (ArenaRegister category 2), the ready
/// check, the instance at the 1v1 map's warcamps, two defeats end the best of three, the ladder moves and is saved (schema v32), the way back;
/// then the duel ladder, .ladder / .rating and a three-player tournament.
/// </summary>
public static class ArenaTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("arena: 1v1 queue (267 status 3, 5), ready check (7, 8), @ARENA@ on ARENA-1v1-01 at its warcamps, Hostile only in the fight, 2 defeats win, ratings 1210/1190 saved, back in 0000", OneVsOne),
        ("arena: a duel won moves the duel ladder (saved), .ladder and .rating, a 3-player tournament (a bye, a duel decides, the final)", Ladder),
        ("arena: without bots (official, or Arena:Bots on but Bots:Enabled off) a lone registration never gets a match against empty slots", NoBots),
        ("arena: Bots:World:Enabled false with Bots:Arena:Enabled: no city bots, but a lone 1v1 gets a bot opponent; Bots:Arena:Enabled false: none", BotsWithoutWorld),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static ClusterStore Store => Client.Clusters!;

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static int Key(EventData e, byte key) => Convert.ToInt32(e.Parameters.GetValueOrDefault(key) ?? 0);

    private static List<int> Statuses(FakePeer on, int from = 0) =>
        [.. on.Events.Skip(from).Where(e => Code(e) == (int)EventCodes.ArenaRegistrationInfo).Select(e => Key(e, 2))];

    private static (World World, ManualClock Clock, string Dir, SqliteCharacterRepository Characters) NewWorld(ServerConfig? config = null)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World((config ?? ServerConfig.PveDefaults) with { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = 3, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        _ = Store.Get("0000");
        return (world, clock, dir, characters);
    }

    private static Character Hero(SqliteCharacterRepository characters, string name, float x)
    {
        var hero = StarterKit.Default.NewCharacter(name);
        (hero.Cluster, hero.PositionX, hero.PositionY) = ("0000", x, 5);
        Check(characters.Create(hero), $"character {name} created");
        return hero;
    }

    private static (WorldSession Session, FakePeer Peer, JoinResponse Join) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        var join = new JoinResponse();
        AlbionSerializer.FillFields(join, peer.Responses[0].Parameters);
        return (session, peer, join);
    }

    private static OperationResponse Call(World world, WorldSession session, FakePeer on, AlbionRequest req)
    {
        lock (world.Sync) session.OnRequest(Request(req, 5), encrypted: false);
        return on.Responses[^1];
    }

    private static void Cleanup(World world, SqliteCharacterRepository characters, string dir)
    {
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void NoBots()
    {
        foreach (var config in new[] { new ServerConfig(), new ServerConfig { Arena = new ArenaOptions { Bots = true, BotWaitSeconds = 1 } } })
        {
            var (world, clock, dir, characters) = NewWorld(config);
            var hero = Hero(characters, "Solo", 5);
            var (session, peer, _) = Join(world, hero.Id);
            var r = Call(world, session, peer, new ArenaRegisterRequest { bo = 1, bp = 2, bq = false, br = [], bs = -1, bt = -1 });
            for (var i = 0; i < 60; i++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                lock (world.Sync) world.Update();
            }
            Check(r.ReturnCode == 0 && !world.Bots.Enabled && world.Arenas.Matches.Count == 0 && session.Player?.Character.DungeonReturn is null,
                  $"Arena:Bots {config.Arena.Bots}, Bots:Enabled false: still queued after 60 s, no match ({world.Arenas.Matches.Count}), no free win");
            Cleanup(world, characters, dir);
        }
    }

    private static void BotsWithoutWorld()
    {
        foreach (var arenaBots in new[] { true, false })
        {
            var config = ServerConfig.PveDefaults with
            {
                Arena = ServerConfig.PveDefaults.Arena with { Bots = true, BotWaitSeconds = 1 },
                Bots = ServerConfig.PveDefaults.Bots with { WorldEnabled = false, ArenaEnabled = arenaBots, PopulateSeconds = 0.5 },
            };
            var (world, clock, dir, characters) = NewWorld(config);
            world.Bots.Nav.Preload(Store.Get("ARENA-1v1-01"));
            world.Bots.Nav.Preload(Store.Get("0000"));
            void Tick(int seconds)
            {
                for (var i = 0; i < seconds; i++)
                {
                    clock.Advance(TimeSpan.FromSeconds(1));
                    lock (world.Sync) world.Update();
                }
            }
            var hero = Hero(characters, "Lone", 5);
            var (session, peer, _) = Join(world, hero.Id);
            var r = Call(world, session, peer, new ArenaRegisterRequest { bo = 1, bp = 2, bq = false, br = [], bs = -1, bt = -1 });
            Tick(5);
            var noCity = world.Maps.Get("0000").Bots.Count == 0;
            var m = world.Arenas.Matches.SingleOrDefault();
            if (!arenaBots)
            {
                Tick(30);
                Check(r.ReturnCode == 0 && noCity && world.Bots.Enabled && world.Arenas.Matches.Count == 0,
                      $"Bots:Arena:Enabled false: no city bots, still queued after 35 s, no match ({world.Arenas.Matches.Count})");
                Cleanup(world, characters, dir);
                continue;
            }
            Check(r.ReturnCode == 0 && noCity && m is { State: ArenaState.ReadyCheck }, $"World off: no city bots in Thetford, a 1v1 match found for the lone player ({m?.State})");
            Call(world, session, peer, new JoinArenaMatchRequest { sv = [], sw = 0 });
            Join(world, hero.Id);
            Tick(2);
            Check(m!.Bots.Count == 1 && m.Bots.Keys.All(b => b.Bot is { Role: BotRole.Arena } && b.Map?.Cluster == m.Id) && m.HadBots,
                  $"in the arena: one arena bot on the other team ({m.Bots.Count}), with Bots:World:Enabled false");
            Cleanup(world, characters, dir);
        }
    }

    private static void OneVsOne()
    {
        var (world, clock, dir, characters) = NewWorld();
        void Tick(double seconds)
        {
            for (var i = 0; i < Math.Ceiling(seconds); i++)
            {
                clock.Advance(TimeSpan.FromSeconds(Math.Min(1, seconds - i)));
                lock (world.Sync) world.Update();
            }
        }
        var template = Store.Get("ARENA-1v1-01");
        var annChar = Hero(characters, "Ann", 5);
        var bobChar = Hero(characters, "Bob", 15);
        var (sa, pa, _) = Join(world, annChar.Id);
        var (sb, pb, _) = Join(world, bobChar.Id);

        // ArenaRegister {1 2 (ranked_1v1)}: RC 0; 267 status 3 (signed up) then 5 (queued) to each; matched: 7 (the ready popup) to both.
        var ra = Call(world, sa, pa, new ArenaRegisterRequest { bo = 1, bp = 2, bq = false, br = [], bs = -1, bt = -1 });
        var rb = Call(world, sb, pb, new ArenaRegisterRequest { bo = 1, bp = 2, bq = false, br = [], bs = -1, bt = -1 });
        var queued = Statuses(pa);
        Tick(1);
        var m = world.Arenas.Matches.SingleOrDefault();
        Check(ra.ReturnCode == 0 && rb.ReturnCode == 0 && queued.SequenceEqual([3, 5]) && template.Warcamps.Count == 2
              && m is { State: ArenaState.ReadyCheck, Mode.Name: "1v1" } && Statuses(pa).Last() == Arenas.StatusReady && Statuses(pb).Last() == Arenas.StatusReady,
              $"both queued (Ann's 267: {string.Join(",", queued)}), ARENA-1v1-01 has {template.Warcamps.Count} warcamps, a 1v1 match found: 267 status 7 to both");

        // Both accept (JoinArenaMatch): 8, then the push {0 @ARENA@<guid>, 1 ARENA-1v1-01}; the Joins at the two camps.
        var before = Statuses(pa).Count;
        Call(world, sa, pa, new JoinArenaMatchRequest { sv = [], sw = 0 });
        var accepted = Statuses(pa, 0).Skip(before).ToList();
        Call(world, sb, pb, new JoinArenaMatchRequest { sv = [], sw = 0 });
        var push = pa.Responses[^1];
        Check(accepted.Contains(Arenas.StatusAccepted) && push.Parameters.GetValueOrDefault((byte)0) is string id && id == m!.Id && id.StartsWith(ArenaIds.Prefix, StringComparison.Ordinal)
              && push.Parameters.GetValueOrDefault((byte)1) is "ARENA-1v1-01" && sa.Status == SessionStatus.Transferring && Statuses(pa).Last() == Arenas.StatusCleared,
              $"accepted (8), then 9 and the push {{0 {m?.Id}, 1 ARENA-1v1-01}}");
        var (sa2, pa2, ja) = Join(world, annChar.Id);
        var (sb2, pb2, jb) = Join(world, bobChar.Id);
        var ann = sa2.Player!;
        var bob = sb2.Player!;
        var camps = template.Warcamps.Select(w => w.Spawns[0]).ToList();
        Check(ja.a8i == m.Id && Vector2.Distance(ja.a8j, camps[m.Team[annChar.Id]]) < 0.5f && Vector2.Distance(jb.a8j, camps[m.Team[bobChar.Id]]) < 0.5f
              && m.State == ArenaState.Countdown && Pvp.Of(ann, bob) == Relation.Neutral,
              $"the Joins at the warcamps ({ja.a8j.X:0.#}, {ja.a8j.Y:0.#}) / ({jb.a8j.X:0.#}, {jb.a8j.Y:0.#}), the countdown: Neutral");

        // The fight: Hostile; two defeats of Bob (a round reset between) end it: Ann wins, ratings 1210 / 1190, saved; nothing lost.
        Tick(world.Config.Arena.CountdownSeconds + 1);
        var hostile = Pvp.Of(ann, bob) == Relation.Hostile && m.State == ArenaState.Fight;
        void Defeat()
        {
            lock (world.Sync) Pvp.Damage(ann, bob, 1e9, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        }
        Defeat();
        var round = m.State == ArenaState.RoundOver && m.Tickets[m.Team[bobChar.Id]] == 1 && bob.Combat.Down is { Kind: KnockdownKind.Pvp };
        Tick(3 + world.Config.Arena.CountdownSeconds + 1);
        var reset = bob.Combat.Down is null && bob.Health.At(world.GameNow) >= bob.Health.Max - 0.5f && m.State == ArenaState.Fight;
        var items = Enumerable.Range(0, Inventory.SlotNames.Length).Count(i => bob.Inventory.Equipment[i] is not null);
        Defeat();
        var saved = characters.LoadRatings().ToDictionary(r => r.Character);
        Check(hostile && round && reset && m.State == ArenaState.Ended && m.Winner == m.Team[annChar.Id]
              && world.Ratings.RatingOf(annChar.Id, "1v1") == 1210 && world.Ratings.RatingOf(bobChar.Id, "1v1") == 1190
              && saved.GetValueOrDefault(annChar.Id) is { Ladder: "1v1", Rating: 1210, Wins: 1 } && saved.GetValueOrDefault(bobChar.Id) is { Rating: 1190, Losses: 1 }
              && Enumerable.Range(0, Inventory.SlotNames.Length).Count(i => bob.Inventory.Equipment[i] is not null) == items && bob.Character.Silver > 0,
              $"Hostile in the fight, a defeat ends round 1, both reset, the second wins: Ann {world.Ratings.RatingOf(annChar.Id, "1v1")}, Bob {world.Ratings.RatingOf(bobChar.Id, "1v1")} (saved {saved.Count}), Bob keeps his gear and has a loss reward");

        // Arena:EndSeconds later both go back to 0000 where they registered; the instance is gone.
        Tick(world.Config.Arena.EndSeconds + 1);
        var back = pa2.Responses[^1];
        Check(back.Parameters.GetValueOrDefault((byte)0) is "0000" && world.Arenas.Matches.Count == 0 && sa2.Status == SessionStatus.Transferring && world.Maps.Find(m.Id) is null,
              "the end: both back to 0000, the match and its map gone");
        Cleanup(world, characters, dir);
    }

    private static void Ladder()
    {
        var (world, _, dir, characters) = NewWorld();
        var names = new[] { "Cid", "Dee", "Eve" };
        var players = names.Select((n, i) => Join(world, Hero(characters, n, 5 + 3 * i).Id).Session.Player!).ToList();
        var (cid, dee, eve) = (players[0], players[1], players[2]);
        var replies = new List<string>();
        CommandContext Ctx(Player? p, AccountLevel level = AccountLevel.Player) => new(world, p, level, (t, _) => replies.Add(t));

        // A duel won: the duel ladder (K 20: +10 / -10 from 1200), saved, .ladder and .rating show it.
        lock (world.Sync) world.Ratings.DuelWon(dee, cid);
        world.Commands.Execute(Ctx(eve), ".ladder duel");
        world.Commands.Execute(Ctx(dee), ".rating");
        var saved = characters.LoadRatings().Where(r => r.Ladder == Ratings.Duel).ToDictionary(r => r.Name);
        Check(saved.GetValueOrDefault("Dee")?.Rating == 1210 && saved.GetValueOrDefault("Cid")?.Rating == 1190
              && replies.Count == 2 && replies[0].Contains("1. Dee 1210 (1-0)") && replies[0].Contains("2. Cid 1190 (0-1)") && replies[1].StartsWith("Dee: duel 1210 (#1, 1-0)"),
              $"the duel ladder saved and shown (got {string.Join(" | ", replies)})");

        // A tournament of three: seeded by the duel ladder, Dee (best) has a bye, Eve (1200) meets Cid (1190); Eve wins her duel, then the final.
        replies.Clear();
        lock (world.Sync)
        {
            world.Commands.Execute(Ctx(cid, AccountLevel.GameMaster), ".tournament open");
            foreach (var p in players) world.Commands.Execute(Ctx(p), ".tournament join");
            world.Commands.Execute(Ctx(cid, AccountLevel.GameMaster), ".tournament start");
        }
        var t = world.Tournament;
        var first = t.Pairs.Select(p => (p.A, p.B)).ToList();
        lock (world.Sync) t.DuelWon(eve, cid);
        var second = t.Round == 2 && t.Pairs is [{ } final] && final.A == dee.Character.Id && final.B == eve.Character.Id;
        lock (world.Sync) world.Commands.Execute(Ctx(cid, AccountLevel.GameMaster), ".tournament win Eve");
        Check(first.SequenceEqual([(dee.Character.Id, (Guid?)null), (eve.Character.Id, cid.Character.Id)]) && second && !t.Running
              && eve.Session is not null,
              $"round 1: Dee (bye), Eve vs Cid; Eve's duel win makes round 2 Dee vs Eve; .tournament win Eve ends it (running {t.Running})");
        Cleanup(world, characters, dir);
    }
}
