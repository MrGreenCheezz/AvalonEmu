using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
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
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage I bots, phase 1 (analysis/work/stageI/bots/SPEC.md §6): <see cref="FakePeer"/> humans in Thetford (0000) on a
/// <see cref="ManualClock"/> with the game time bound to it. A bot is a player to the humans (the same NewCharacter to two of them, its
/// walk as event 3, Leave out of range, never saved, nothing sent to its peer); the population fills out of view, walks on walkable
/// ground only and leaves after the last human; the turnover's newcomer arrives at an exit; names; a city bot is never fought and its
/// looping emote is shown to a newcomer; the .bot commands.
/// </summary>
public static class BotTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bots: a GM city bot is a player to two humans: one NewCharacter each, event 3 of its walk, Leave at 80 m and back, never saved, nothing to its peer", Entity),
        ("bots: Thetford's population: none before a human, spawned out of view, every sample on walkable ground for 60 s, gone 30 s after the human", Population),
        ("bots: turnover: a bot whose stay is over leaves and the next of its slot arrives at an exit with another name and the same persona", Turnover),
        ("bots: names: the shape, a repository name never drawn, 200 unique; a created character's name takes the bot away", Names),
        ("bots: a city bot in a PvP zone is Friendly both ways (AttackStart -501); a sitting bot's emote reaches a newcomer, StopEmote before it walks", CityPeace),
        ("bots: a bot put off the walk grid steps onto it and blames no POI for its failed paths", OffGrid),
        ("bots: .bot spawn city 3 seen alike by two humans, .bot, .bot clear here, .bot off refuses .bot spawn", Commands),
        ("bots: Bots:* keys (pve on, official off, a range, a nested group, a bad value refused), characters.xml's 22 look presets and the shipped content/bots.json", Config),
        ("bots switches: official all off, pve every kind on with today's figures; World/City off, Bots:Zones elsewhere: no population in Thetford", Switches),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>
    /// Thetford's map (or <paramref name="cluster"/>'s), the pve preset with <paramref name="bots"/>, <paramref name="settings"/> (built-in
    /// without), a manual clock. Also the gatherer tests' (<see cref="BotGatherTest"/>).
    /// </summary>
    internal sealed class Field : IDisposable
    {
        private readonly string _dir = NewDir();
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Characters;
        public readonly World World;

        public readonly string Cluster;

        /// <summary>Ticks added to the game time (bot-schedule: a jump to another hour of the day).</summary>
        public long TimeShift;

        /// <summary>A world of the pve preset (changed by <paramref name="config"/> when given) with <paramref name="bots"/> on <paramref name="cluster"/>.</summary>
        public Field(BotOptions bots, BotSettings? settings = null, ClusterPvp? rules = null, string cluster = "0000", Func<ServerConfig, ServerConfig>? config = null)
        {
            Cluster = cluster;
            Characters = new SqliteCharacterRepository(Path.Combine(_dir, "albionlocal.db"));
            var store = Client.Clusters!;
            _ = store.Get(cluster);
            var anchor = GameTime.Now;
            var options = ServerConfig.PveDefaults with { JoinByCharacterId = true, Bots = bots };
            World = new World(config?.Invoke(options) ?? options, Characters, Client.Items, Client.GameData, Client.Spells)
            {
                Sync = new object(), Clock = Clock, Clusters = store, GameTicks = () => anchor + Clock.Now.Ticks + TimeShift, MapSeed = 17, PvpOverride = rules,
                BotSettings = settings ?? BotSettings.Default,
            };
        }

        /// <summary>The cluster's walk grid, built now (outside the lock).</summary>
        public ClusterNav Nav() => World.Bots.Nav.Preload(Client.Clusters!.Get(Cluster));

        public Map Map => World.Maps.Get(Cluster);

        /// <summary>A new character of the starter kit at <paramref name="at"/> with <paramref name="flag"/> (changed by <paramref name="edit"/>), joined.</summary>
        public (WorldSession Session, FakePeer Peer, Player Player) Join(string name, Vector2 at, byte flag = Pvp.Blue, Action<Character>? edit = null)
        {
            var c = StarterKit.Default.NewCharacter(name);
            (c.Cluster, c.PositionX, c.PositionY, c.Flag) = (Cluster, at.X, at.Y, flag);
            edit?.Invoke(c);
            Check(Characters.Create(c), $"character {name} created");
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
            return (session, peer, session.Player!);
        }

        public void Run(double seconds, Action? eachTick = null)
        {
            for (var i = 0; i < Math.Round(seconds * 20); i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(50));
                lock (World.Sync)
                {
                    World.Update();
                    eachTick?.Invoke();
                }
            }
        }

        public string Gm(Player who, string line)
        {
            var replies = new List<string>();
            var ctx = new CommandContext(World, who, AccountLevel.GameMaster, (t, error) => replies.Add((error ? "error: " : "") + t));
            lock (World.Sync) World.Commands.Execute(ctx, line);
            return string.Join("\n", replies);
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    internal static List<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) =>
        [.. Since(peer, from).Where(e => e.Code == code).Select(e => e.P)];

    private static IEnumerable<MultiplayerTest.Sample> Moves(FakePeer peer, long id, int from = 0) =>
        peer.Events.Skip(from).Where(e => e.Code == MovementCodec.PhotonEventCode && Convert.ToInt64(e.Parameters[0]) == id)
            .Select(e => MultiplayerTest.Decode((byte[])e.Parameters[1]!));

    /// <summary>A walkable spot in Thetford with room around it: the arrival point of its first exit.</summary>
    private static Vector2 Square(ClusterNav nav) => nav.Exits[0].Arrival;

    /// <summary>The built-in tables with no population in Thetford (the GM's and the test's own bots only).</summary>
    internal static readonly BotSettings NoPopulation = BotSettings.Default with
    {
        Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["0000"] = new Dictionary<BotRole, int> { [BotRole.City] = 0 } },
    };

    private static void Entity()
    {
        using var f = new Field(new BotOptions { Enabled = true }, NoPopulation);
        var nav = f.Nav();
        var at = Square(nav);
        var (_, peerA, a) = f.Join("Anna", at);
        var (_, peerB, b) = f.Join("Boris", at + new Vector2(2, 0));
        var calls = BotPeer.Instance.Calls;
        int markA = peerA.Events.Count, markB = peerB.Events.Count;
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.City, 4, at + new Vector2(7, 0), 270)!;
        var created = Of(peerA, EventCodes.NewCharacter, markA).Where(p => L(p, 0) == bot.Id).ToList();
        var seenByB = Of(peerB, EventCodes.NewCharacter, markB).Where(p => L(p, 0) == bot.Id).ToList();
        Check(created.Count == 1 && seenByB.Count == 1, $"each human gets one NewCharacter of the bot in the spawn's call ({created.Count}, {seenByB.Count})");
        var k = created[0];
        var equipment = (int[])k[40]!;
        var tiers = bot.Inventory.Equipment.Items.Select(i => i.Template.Tier).ToList();
        var length = bot.Character.Template == 1 ? 4 : 5;
        Check(k[1] is string name && name == bot.Character.Name && k[7] is byte[] guid && new Guid(guid) == bot.Character.Id && equipment.Length == 10
              && equipment[2] != 0 && equipment[3] != 0 && equipment[4] != 0 && tiers.All(t => t <= 4) && k[43] is int[] { Length: 14 } && k[55] is int[] { Length: 7 }
              && !k.ContainsKey(53) && !k.ContainsKey(16) && !k.ContainsKey(17) && (!k.ContainsKey(2) || Convert.ToInt32(k[2]) is >= 1 and <= 10)
              && k[5] is int[] custom && custom.Length == length && k[6] is int[] clothes && clothes.Length == length,
              $"NewCharacter: name, Guid, head/armor/shoes worn (tiers {string.Join(",", tiers)} ≤ 4), 43 int[14], 55 int[7], Blue (no 53), no 16/17, avatar 1-10 or none, 5/6 of the template's {length}");
        Check(seenByB[0][1] is string other && other == (string)k[1]! && ((int[])seenByB[0][40]!).SequenceEqual(equipment), "the second human sees the same bot: name and gear");
        Check(f.Map.Bots.ContainsKey(bot.Id) && !f.Map.Players.ContainsKey(bot.Id) && a.Observers.Contains(bot) && b.Observers.Contains(bot) && bot.Observers.Count == 2
              && bot.Inventory.Equipment.Items.All(i => i.Passive.Length == 0) && f.Map.HumanCount == 2,
              "in map.Bots, not map.Players; both humans observe it; no item has a passive");

        // Never saved: its items written into its character, a save of everything, the writer flushed.
        var before = f.Characters.List().Count;
        lock (f.World.Sync)
        {
            bot.SaveItems();
            f.World.Saves.SaveAll();
        }
        f.World.Saves.Flush();
        Check(f.Characters.Find(bot.Character.Id) is null && f.Characters.Find(bot.Character.Name) is null && f.Characters.List().Count == before,
              "never saved: no row by Guid or name, the repository's count unchanged");

        // It walks 80 m away: event 3 to both, then Leave out of range; back: NewCharacter again; despawned: Leave.
        markA = peerA.Events.Count;
        var start = bot.PositionAt(f.World.Bots.Time);
        var far = start + new Vector2(80, 0);
        lock (f.World.Sync) f.World.Bots.Walk(bot, [start, far], 90);
        var first = Moves(peerA, bot.Id, markA).FirstOrDefault();
        Check(first is { Speed: 5.5f } && Vector2.Distance(first.Pos, start) < 0.01f && first.Target == far, $"event 3 of the walk: from {start} to {far} at 5.5 m/s (got {first?.Pos} → {first?.Target} at {first?.Speed})");
        f.Run(16);
        var resent = Moves(peerA, bot.Id, markA).Count();
        Check(Of(peerA, EventCodes.Leave, markA).Any(p => L(p, 0) == bot.Id) && Of(peerB, EventCodes.Leave).Any(p => L(p, 0) == bot.Id) && !a.Observers.Contains(bot)
              && resent >= 8, $"80 m away: Leave {{0 bot}} to both, {resent} samples while it was seen (resent every 0.5 s)");
        markA = peerA.Events.Count;
        var away = bot.PositionAt(f.World.Bots.Time);
        lock (f.World.Sync) f.World.Bots.Walk(bot, [away, start], 270);
        f.Run(16);
        var again = Of(peerA, EventCodes.NewCharacter, markA).Where(p => L(p, 0) == bot.Id).ToList();
        Check(again.Count == 1 && a.Observers.Contains(bot) && again[0][14] is float[] { Length: 2 } p14 && Vector2.Distance(new Vector2(p14[0], p14[1]), away) > 20,
              $"back in range: NewCharacter again, its key 14 where the bot was then, not the start of the leg it began unseen at {away}");
        markA = peerA.Events.Count;
        lock (f.World.Sync) f.World.Bots.Despawn(bot, "test", BotRefill.Now);
        Check(Of(peerA, EventCodes.Leave, markA).Any(p => L(p, 0) == bot.Id) && bot.Map is null && f.World.Bots.Count == 0 && BotPeer.Instance.Calls == calls,
              $"despawned: Leave {{0 bot}}, off the map; its peer was never called ({BotPeer.Instance.Calls - calls})");
    }

    private static void Population()
    {
        using var f = new Field(new BotOptions { Enabled = true, PopulateSeconds = 0.5, SpawnPerPass = 12, StayMinutes = (600, 600), ThinkSeconds = (0.5, 1) });
        f.Run(3);
        Check(f.World.Bots.Count == 0, "no human: no bots");
        var nav = f.Nav();
        var at = Square(nav);
        var (session, peer, human) = f.Join("Hana", at);
        f.Run(1);
        var bots = f.Map.Bots.Values.ToList();
        var grid = f.Map.Grid!;
        var cells = f.World.Config.Visibility.EnterCells;
        Check(bots.Count is > 0 and <= 24 && bots.All(b => CellGrid.Distance(grid.CellOf(b.Bot!.Home), grid.CellOf(at)) > cells) && human.Observers.Count == 0
              && bots.All(b => b.Bot!.Role == BotRole.City && b.Bot.PersonaId.StartsWith("0000:city:", StringComparison.Ordinal) && nav.Grid.Walkable(b.Bot.Home)),
              $"one human: {bots.Count} city bots (≤ 24), each spawned on walkable ground more than {cells} cells from the human, none in view at once");
        var samples = 0;
        var off = new List<string>();
        f.Run(60, () =>
        {
            foreach (var b in f.Map.Bots.Values)
                if (b.Movement is { } m && m.Speed > 0)
                {
                    samples++;
                    if (!nav.Grid.Walkable(m.From) || !nav.Grid.Walkable(m.Target)) off.Add($"{b.Character.Name} {m.From}→{m.Target}");
                }
        });
        Check(samples > 0 && off.Count == 0, $"60 s of walking: {samples} walking samples, every position and target walkable (off: {string.Join("; ", off.Take(3))})");
        lock (f.World.Sync) session.OnDisconnected("test");
        f.Run(29);
        var stayed = f.Map.Bots.Count;
        f.Run(2);
        Check(stayed > 0 && f.Map.Bots.Count == 0 && f.World.Bots.Count == 0, $"the human gone: the bots stay {f.World.Config.Bots.LingerSeconds} s ({stayed} at 29 s), then none");
    }

    private static void Turnover()
    {
        var one = BotSettings.Default with { Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>> { ["0000"] = new Dictionary<BotRole, int> { [BotRole.City] = 1 } } };
        using var f = new Field(new BotOptions { Enabled = true, PopulateSeconds = 0.5, StayMinutes = (0.05, 0.05), ArrivalSeconds = (1, 1), ThinkSeconds = (0.5, 0.5), ActiveRange = 10000 }, one);
        var nav = f.Nav();
        var (_, peer, human) = f.Join("Tess", Square(nav));
        f.Run(1);
        var first = f.Map.Bots.Values.Single();
        var (name, persona) = (first.Character.Name, first.Bot!.PersonaId);
        Player? next = null; // the first successor seen (a short stay: a later sample may fall between a logout and the next arrival)
        for (var i = 0; i < 180 && next is null; i++)
        {
            f.Run(0.5);
            next = f.Map.Bots.Values.SingleOrDefault(b => b != first);
        }
        Check(first.Map is null && next is not null && next != first && next.Character.Name != name && next.Bot!.PersonaId == persona
              && nav.Exits.Any(e => Vector2.Distance(e.Arrival, next.Bot.Home) < 1),
              $"'{name}' left after its 3 s stay (through an exit or a logout); '{next?.Character.Name}' of {persona} arrived at an exit's arrival point");
    }

    private static void Names()
    {
        using var f = new Field(new BotOptions { Enabled = true }, NoPopulation);
        var aldric = StarterKit.Default.NewCharacter("Aldric");
        Check(f.Characters.Create(aldric), "Aldric created");
        var tight = BotSettings.Default with { NamePrefixes = ["Al"], NameMiddles = ["dr"], NameSuffixes = ["ic", "wyn"] };
        var random = new Random(3);
        var drawn = Enumerable.Range(0, 20).Select(_ => BotNames.Next(tight, random, f.World.Bots.Taken)).ToList();
        Check(drawn.All(n => n == "Aldrwyn"), $"Aldric is a character's: only Aldrwyn is drawn ({string.Join(", ", drawn.Distinct())})");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 200; i++)
            if (BotNames.Next(BotSettings.Default, random, n => names.Contains(n) || f.World.Bots.Taken(n)) is { } n) names.Add(n);
        Check(names.Count == 200 && names.All(n => BotNames.Shape().IsMatch(n)) && !names.Contains("Aldric"), "200 unique names of the shape ^[A-Z][a-z]{4,11}$, none a character's");
        var (_, peer, human) = f.Join("Nina", Square(f.Nav()));
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.City, 4, human.PositionAt(DateTime.UtcNow) + new Vector2(5, 0), 0)!;
        var mark = peer.Events.Count;
        lock (f.World.Sync) f.World.Bots.NameTaken(bot.Character.Name.ToUpperInvariant());
        Check(bot.Map is null && Of(peer, EventCodes.Leave, mark).Any(p => L(p, 0) == bot.Id) && f.World.Bots.Taken(bot.Character.Name),
              $"a character created as '{bot.Character.Name.ToUpperInvariant()}': the bot of that name gone (Leave), the name taken");
    }

    private static void CityPeace()
    {
        using var f = new Field(new BotOptions { Enabled = true, ThinkSeconds = (60, 60) }, NoPopulation, new ClusterPvp(PvpRule.PvpAllowed, WarningType: "red"));
        var at = Square(f.Nav());
        var (session, peer, red) = f.Join("Rex", at, Pvp.Red);
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.City, 4, at + new Vector2(2, 0), 0)!;
        var responses = peer.Responses.Count;
        lock (f.World.Sync) Send(session, new AttackStartRequest { wf = f.World.GameNow, co = bot.Id });
        Check(Pvp.Of(red, bot) == Relation.Friendly && Pvp.Of(bot, red) == Relation.Friendly && peer.Responses.Skip(responses).Any(r => r.ReturnCode == -501),
              "a Red human in a red zone: the city bot is Friendly both ways, AttackStart on it refused -501");
        long start;
        lock (f.World.Sync)
        {
            f.World.Bots.Emote(bot, 12);
            start = bot.Bot!.Emote!.Start;
        }
        f.Run(1);
        var (_, late, _) = f.Join("Lena", at + new Vector2(-2, 0));
        var spawn = Since(late, 0).SkipWhile(e => !(e.Code == EventCodes.NewCharacter && L(e.P, 0) == bot.Id)).ToList();
        Check(spawn.Count > 1 && spawn.Skip(1).Any(e => e.Code == EventCodes.PlayEmote && L(e.P, 0) == bot.Id && Convert.ToInt32(e.P[1]) == 12 && L(e.P, 3) == start && !e.P.ContainsKey(2)),
              "a human joining later: NewCharacter of the sitting bot, then PlayEmote {0 bot, 1 12, 3 the original start}, no key 2");
        var mark = late.Events.Count;
        var here = bot.PositionAt(f.World.Bots.Time);
        lock (f.World.Sync) f.World.Bots.Walk(bot, [here, here + new Vector2(0, 3)], null);
        var after = late.Events.Skip(mark).ToList();
        var stop = after.FindIndex(e => e.Parameters.ContainsKey(ReservedKeys.EventCode) && Code(e) == (int)EventCodes.StopEmote);
        var move = after.FindIndex(e => e.Code == MovementCodec.PhotonEventCode);
        Check(stop >= 0 && move > stop && bot.Bot.Emote is null, $"it walks on: StopEmote (at {stop}) before its first event 3 (at {move})");
    }

    private static void OffGrid()
    {
        using var f = new Field(new BotOptions { Enabled = true, ThinkSeconds = (0.5, 0.5) }, NoPopulation);
        var nav = f.Nav();
        var at = Square(nav);
        var (_, _, human) = f.Join("Owen", at);
        // A cell within 30 m that is not walkable but has walkable ground within 6 m (by a wall, a pillar, a door).
        var wall = Enumerable.Range(-30, 61).SelectMany(dz => Enumerable.Range(-30, 61).Select(dx => at + new Vector2(dx, dz)))
            .First(p => !nav.Grid.Walkable(p) && nav.Grid.Nearest(p, 6) is { } n && Vector2.Distance(n, p) > 1);
        Player bot;
        lock (f.World.Sync) bot = f.World.Bots.Spawn(f.Map, BotRole.City, 4, wall, 0)!;
        f.Run(20);
        Check(bot.Map is not null && nav.Grid.Walkable(bot.PositionAt(f.World.Bots.Time)) && f.World.Bots.UnreachableOf(f.Map) == 0,
              $"spawned in a blocked cell at {wall}: it stepped onto walkable ground, {f.World.Bots.UnreachableOf(f.Map)} POI(s) marked without a path");
    }

    private static void Commands()
    {
        using var f = new Field(new BotOptions { Enabled = true, PopulateSeconds = 0.5 }, NoPopulation);
        var at = Square(f.Nav());
        var (_, peerA, gm) = f.Join("Gina", at);
        var (_, peerB, _) = f.Join("Gabe", at + new Vector2(1, 0));
        var (markA, markB) = (peerA.Events.Count, peerB.Events.Count);
        var reply = f.Gm(gm, ".bot spawn city 3");
        var ids = f.Map.Bots.Keys.ToHashSet();
        Check(ids.Count == 3 && Of(peerA, EventCodes.NewCharacter, markA).Select(p => L(p, 0)).ToHashSet().SetEquals(ids)
              && Of(peerB, EventCodes.NewCharacter, markB).Select(p => L(p, 0)).ToHashSet().SetEquals(ids)
              && f.Map.Bots.Values.All(b => b.Bot!.PersonaId.StartsWith("gm:", StringComparison.Ordinal)),
              $".bot spawn city 3: three GM bots, both humans get the same three NewCharacter ({reply})");
        var status = f.Gm(gm, ".bot");
        Check(status.Contains("bots on: 3 in the world", StringComparison.Ordinal) && status.Contains("0000 (City): 2 human(s), 3 bot(s) (3 city)", StringComparison.Ordinal), $".bot: {status}");
        (markA, markB) = (peerA.Events.Count, peerB.Events.Count);
        reply = f.Gm(gm, ".bot clear here");
        Check(reply == "3 bot(s) despawned" && Of(peerA, EventCodes.Leave, markA).Count(p => ids.Contains(L(p, 0))) == 3
              && Of(peerB, EventCodes.Leave, markB).Count(p => ids.Contains(L(p, 0))) == 3 && f.Map.Bots.Count == 0, $".bot clear here: three Leave to each ({reply})");
        f.Gm(gm, ".bot off");
        var refused = f.Gm(gm, ".bot spawn city");
        f.Run(2);
        Check(refused.StartsWith("error: bots are off", StringComparison.Ordinal) && f.World.Bots.Count == 0, $".bot off: .bot spawn refused ({refused}), no bots");
    }

    private static void Switches()
    {
        var dir = NewDir();
        var file = Path.Combine(dir, "albionlocal.json");
        File.WriteAllText(file, "{}");
        static bool Refused(string file, string arg)
        {
            try { LoadTestConfig(file, [arg]); return false; }
            catch (InvalidDataException) { return true; }
        }
        // Official: the master off, so every kind off at run time (the kind switches true under it); pve: all on, today's figures.
        var official = LoadTestConfig(file, ["--Preset=official"]);
        using (var repo = new SqliteCharacterRepository(Path.Combine(dir, "o.db")))
        using (var w = new World(official with { JoinByCharacterId = true }, repo, Client.Items, Client.GameData, Client.Spells) { Sync = new object() })
            Check(!official.Bots.Enabled && !w.Bots.Enabled && !w.Bots.WorldEnabled && !w.Bots.ArenaBots && !w.Bots.HellgateBots && !w.Bots.InvaderBots,
                  "official: Bots:Enabled false, so no world, arena, hellgate or invader bots");
        var pve = LoadTestConfig(file, ["--Preset=pve"]).Bots;
        Check(pve is { Enabled: true, WorldEnabled: true, CityEnabled: true, GathererEnabled: true, PatrolEnabled: true, DrifterEnabled: true, Zones.Count: 0,
                  ArenaEnabled: true, HellgateEnabled: true, InvaderEnabled: true, PvpEnabled: true, RewardsEnabled: true,
                  CityPoiShare: 0.6, CityWanderMeters: (15, 40), CityExitShare: 0.5, FleeSeconds: 20, FleeDistance: 25, FollowSpacing: 2.5,
                  RetargetSeconds: 5, RetargetShare: 1.5, KiteSeconds: 1.2, KiteEverySeconds: 2.5 }
              && pve == ServerConfig.PveDefaults.Bots,
              "pve: every kind on, Pvp and Rewards on, the former constants at their values; the loaded pve equals PveDefaults");
        var c = LoadTestConfig(file, ["--Bots:World:Enabled=false", "--Bots:Zones=city, 1207", "--Bots:Patrol:Enabled=false", "--Bots:Arena:Enabled=false", "--Bots:City:ExitShare=0.25"]).Bots;
        Check(c is { WorldEnabled: false, PatrolEnabled: false, ArenaEnabled: false, CityExitShare: 0.25, HellgateEnabled: true } && c.Zones.SequenceEqual(["City", "1207"])
              && c.ZoneAllowed("City", "0000") && c.ZoneAllowed("Red", "1207") && !c.ZoneAllowed("Yellow", "1000")
              && Refused(file, "--Bots:City:PoiShare=2") && Refused(file, "--Bots:Rewards:Enabled=maybe") && Refused(file, "--Bots:Zones=[1"),
              "keys read (Zones a comma list, a zone class in its spelling or a cluster id); bad values refused");
        try { Directory.Delete(dir, true); } catch (IOException) { }

        // Thetford (City) with one human: the population as usual, none with World, City or Bots:Zones elsewhere.
        foreach (var (options, want) in new[]
        {
            (new BotOptions { Enabled = true, PopulateSeconds = 0.5, SpawnPerPass = 12 }, true),
            (new BotOptions { Enabled = true, PopulateSeconds = 0.5, SpawnPerPass = 12, WorldEnabled = false }, false),
            (new BotOptions { Enabled = true, PopulateSeconds = 0.5, SpawnPerPass = 12, CityEnabled = false }, false),
            (new BotOptions { Enabled = true, PopulateSeconds = 0.5, SpawnPerPass = 12, Zones = ["Red", "Black"] }, false),
            (new BotOptions { Enabled = true, PopulateSeconds = 0.5, SpawnPerPass = 12, Zones = ["0000"] }, true),
        })
        {
            using var f = new Field(options);
            var nav = f.Nav();
            f.Join("Wanda", Square(nav));
            f.Run(2);
            Check((f.Map.Bots.Count > 0) == want,
                  $"World {options.WorldEnabled}, City {options.CityEnabled}, Zones [{string.Join(",", options.Zones)}]: {f.Map.Bots.Count} bot(s) in Thetford (want {(want ? "some" : "none")})");
        }
    }

    private static void Config()
    {
        var dir = NewDir();
        var file = Path.Combine(dir, "albionlocal.json");
        File.WriteAllText(file, """{ "Bots": { "StayMinutes": "1-2", "Nav": { "LatticeStep": 6 }, "City": { "PerPoi": 2 } } }""");
        var c = LoadTestConfig(file, ["--Preset=pve"]);
        Check(c.Bots is { Enabled: true, StayMinutes: (1, 2), LatticeStep: 6, CityPerPoi: 2, MaxPerMap: 40, PvpRules: BotPvpRules.Consent } && c.Bots.SpawnCells(c.Visibility) == 5
              && !LoadTestConfig(file, ["--Preset=official"]).Bots.Enabled && LoadTestConfig(file, ["--Bots:ThinkSeconds=3"]).Bots.ThinkSeconds == (3, 3),
              "pve: bots on, \"1-2\" a range, Nav: { LatticeStep } a group, MinSpawnCells = EnterCells + 1; official: off; one number a range of one");
        static bool Refused(string file, string arg)
        {
            try { LoadTestConfig(file, [arg]); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check(Refused(file, "--Bots:StayMinutes=x-2") && Refused(file, "--Bots:Look=Pretty") && Refused(file, "--Bots:Nav:Lattice=4"), "a bad range, look or an unknown key refused");
        Check(c.Bots is { CrimesAgainstBots: false, GathererFlag: BotGathererFlag.Zone, ConsumeNodes: false, DownDespawnSeconds: 10, GathererNodes: (6, 20) }
              && LoadTestConfig(file, ["--Preset=official"]).Bots.CrimesAgainstBots && LoadTestConfig(file, ["--Bots:Gatherer:FleeHealth=0.4"]).Bots.FleeHealth == 0.4
              && Refused(file, "--Bots:DownDespawnSeconds=90") && Refused(file, "--Bots:Gatherer:Flag=Green"),
              "phase 2 keys: pve no crimes against bots (official yes), Gatherer:* a group, DownDespawnSeconds below the 90 s get-up, a bad flag refused");
        var looks = Client.GameData!.Looks;
        Check(looks.Presets.Count == 22 && looks.Presets.Count(p => p.Template == 1) == 11
              && looks.Presets.All(p => p.Customization.Length == looks.Items[p.Template].Count && p.Clothes.Length == looks.Items[p.Template].Count),
              $"characters.xml: 22 look presets, 11 female (their BEARD entry skipped), each of its template's item count ({looks.Presets.Count}, {looks.Presets.Count(p => p.Template == 1)} female)");
        var shipped = BotSettings.Load(Path.Combine(DataDir(), "..", "content", "bots.json"));
        Check(shipped.Counts["City"][BotRole.City] == 24 && shipped.Counts["Black"][BotRole.Patrol] == 10 && shipped.Counts["Red"][BotRole.Gatherer] == 12 && shipped.ScaleOf(5) == 0.6
              && BotSettings.Zones.All(z => shipped.Counts[z].OrderBy(x => x.Key).SequenceEqual(BotSettings.Default.Counts[z].OrderBy(x => x.Key)))
              && shipped.NamePrefixes.Count == BotSettings.Default.NamePrefixes.Count
              && shipped.Emotes.SequenceEqual(BotSettings.Default.Emotes) && shipped.Tools["ore"] == "T{t}_2H_TOOL_PICK" && shipped.CountsOf("City", "0000", "PLAYERCITY_SAFEAREA_01")[BotRole.City] == 24
              && shipped.Weapons.SequenceEqual(BotSettings.Default.Weapons) && !shipped.Weapons.Contains("MAIN_HOLYSTAFF")
              && shipped.GathererWeaponShare == 0.6 && shipped.GathererStartCarry == (5, 20) && shipped.GathererStartCarry == BotSettings.Default.GathererStartCarry,
              "content/bots.json: the built-in tables (stageM: 24 city bots in a royal city, 12 gatherers + 8 patrols in red zones, 10 patrols in black ones; no holy staff; 60 % armed gatherers carrying 5-20 from the spawn)");
        var pve = ServerConfig.PveDefaults.Bots;
        Check(new BotOptions() is { MaxTotal: 400, SpawnPerPass: 4, SpellChance: 0.6, DeathGearDropChance: 1.0, MinItems: 0 } && pve is { DeathGearDropChance: 0.5, MinItems: 1 }
              && c.Bots is { MaxTotal: 400, DeathGearDropChance: 0.5, MinItems: 1 } && LoadTestConfig(file, ["--Preset=official"]).Bots is { DeathGearDropChance: 1.0, MinItems: 0 }
              && LoadTestConfig(file, ["--Bots:Rewards:MinItems=2"]).Bots.MinItems == 2 && Refused(file, "--Bots:Rewards:MinItems=7") && Refused(file, "--Bots:Rewards:DeathGearDropChance=1.5"),
              "stageM keys: MaxTotal 400, SpawnPerPass 4, SpellChance 0.6; Rewards:DeathGearDropChance official 1.0 / pve 0.5, Rewards:MinItems official 0 / pve 1 (0-6)");
        Check(BotSettings.Parse("""{ "Gear": { "GathererWeaponShare": 0.25, "GathererStartCarry": "3-4" } }""", "test") is { GathererWeaponShare: 0.25, GathererStartCarry: (3, 4) }
              && BotSettings.Parse("""{ "Gear": { "GathererStartCarry": 0 } }""", "test").GathererStartCarry == (0, 0),
              "Gear.GathererWeaponShare and Gear.GathererStartCarry (\"min-max\" or one number) read");
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }
}
