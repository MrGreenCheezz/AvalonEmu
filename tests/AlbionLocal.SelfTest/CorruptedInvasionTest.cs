using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
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
/// Corrupted dungeons, phase B (analysis/work/stageL/corrupted/SPEC.md §6, "SPEC" here), on FakePeer sessions with a manual clock and Hunter
/// runs of CORRUPT-001 under the pve corrupted options: the Demonic Shrine (416, 460, 459, 450 key 7) and its refusals, no invasion without
/// it, the bot invader after it (its spawn, its relations, its defeat's InvaderReward), the shards, a bot's win, two consenting players
/// matched (449 kinds 6/3/9, the push in and home), the maps' portals with the tickets moved to the Knight's Vows, and the chain. The open
/// world's corrupted portals are RandomDungeonTest's (0204).
/// </summary>
public static class CorruptedInvasionTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("corrupted B: the phase B keys under pve and official, content/corrupted.json InvaderReward and Rolls, CORRUPT-001's invader spawn points", Data),
        ("corrupted B: the shrine (416 rc 0 -> 460 of the owner, 459 state 1, 450 key 7; another player, after 45 s: refused), 30 min without it no invasion, with it the bot invader in [60, 180] s far off, Hostile to the owner only, beaten: InvaderReward, 449 kind 10", Shrine),
        ("corrupted B: 3 shards end an invasion (449 kind 5 1/2/3, kind 10, the bot gone); an invader gone without a fight: unpaid; the bot beats the owner: a knockdown, no item or infamy loss, the run goes on", Shards),
        ("corrupted B: two consenting players of a rank matched (449 kind 6, the push into the target, 449 kind 3, Hostile pair; his kill no points, the chests not his), the owner wins (449 kind 9 last), the invader home 30 s later; no shrine, never a target", Players),
        ("corrupted B: MapsOpenDungeons: a T6 Knight's Vow starts a T6 invasion, the corrupted map opens a portal (no invasion), its enter cast into a Hunter run", Maps),
        ("corrupted B: the chain: 449 kind 15 and the chain exit (key 12), Next Dungeon of another rank refused, of the same: a new run, the first way back, streak 1", Chain),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static readonly string Hunter = new ClusterVariant("CORRUPT-001", 5, "CORRUPTED_DUNGEON_BEGINNER").Id;

    /// <summary>A world of the record's config with the pve corrupted options (<paramref name="change"/> over them), bots on without a population, CORRUPT-001 the only map.</summary>
    private sealed class Field : IDisposable
    {
        public readonly World World;
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Characters;
        private readonly string _dir;

        public Field(int seed, Func<CorruptedOptions, CorruptedOptions>? change = null, EventSettings? events = null)
        {
            _dir = NewDir();
            Characters = Repository(_dir);
            var anchor = DateTime.UtcNow.Ticks;
            var options = change?.Invoke(ServerConfig.PveDefaults.Corrupted) ?? ServerConfig.PveDefaults.Corrupted;
            var c = new ServerConfig { JoinByCharacterId = true, Corrupted = options, Bots = new BotOptions { Enabled = true } };
            World = new World(c, Characters, Client.Items, Client.GameData, Client.Spells)
            {
                Sync = new object(), Clock = Clock, Clusters = Store, MapSeed = seed, GameTicks = () => anchor + Clock.Now.Ticks,
                BotSettings = BotSettings.Default with { Counts = new Dictionary<string, IReadOnlyDictionary<BotRole, int>>(StringComparer.OrdinalIgnoreCase) },
                EventSettings = events ?? EventSettings.Default,
            };
            World.MobAi = false;
            World.CorruptedSettings = CorruptedSettings.Default with { Maps = Store.Corrupted.Maps.ToDictionary(m => m.Id, m => m.Id == "CORRUPT-001" ? 1.0 : 0) };
            _ = Store.Get("0000-HellDen");
            World.Bots.Nav.Preload(Store.Get(Hunter)); // the invader's walk grid at once
        }

        public void Tick(double seconds)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (World.Sync) World.Update();
        }

        public Character Hero(string cluster, Vector2 at, params InventoryItem[] items)
        {
            var hero = StarterKit.Default.NewCharacter("C" + Guid.NewGuid().ToString("N")[..10]);
            (hero.Cluster, hero.PositionX, hero.PositionY) = (cluster, at.X, at.Y);
            foreach (var item in items) hero.Inventory.Add(item);
            Check(Characters.Create(hero), "character created");
            return hero;
        }

        /// <summary>A new Hunter run for a new character from the Hell Den, and its Join inside: the run, the session, the peer.</summary>
        public (CorruptedRun Run, WorldSession Session, FakePeer Peer) Run()
        {
            var hero = Hero("0000-HellDen", new Vector2(795, 162));
            var (s, _, _) = Join(World, hero.Id);
            CorruptedRun run;
            lock (World.Sync) run = World.Corrupted.Enter(s.Player!, Store.Corrupted.At(0)!, "CORRUPT-001", Hunter);
            var (inside, peer, _) = Join(World, hero.Id);
            return (run, inside, peer);
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
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

    private static List<EventData> Of(FakePeer on, EventCodes code, int from = 0) => [.. on.Events.Skip(from).Where(e => Code(e) == (int)code)];

    private static List<int> Kinds(FakePeer on, int from = 0) => [.. Of(on, EventCodes.CorruptedDungeonUpdate, from).Select(e => Convert.ToInt32(e.Parameters[1]))];

    private static void Move(World world, WorldSession s, Vector2 to)
    {
        lock (world.Sync) s.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [to.X, to.Y], v8 = [to.X, to.Y], v9 = 0f }, null), encrypted: false);
    }

    private static OperationResponse Call(World world, WorldSession s, FakePeer peer, AlbionRequest req, int id)
    {
        lock (world.Sync) s.OnRequest(Request(req, id), encrypted: false);
        return peer.Responses[^1];
    }

    private static void Data()
    {
        var cfg = NewDir();
        var none = Path.Combine(cfg, "albionlocal.json");
        Check(LoadTestConfig(none, ["--Preset=pve"]).Corrupted is { Invasions: CorruptedInvasionMode.Consent, BotInvaders: true, PlayerInvasions: CorruptedInvasionMode.Consent, InfamyLossOnDefeat: 0,
                  MaxInvasionsPerRun: 1, AntiReset: false, ChainBonus: 0.1, OpenWorldPortals: true, MapsOpenDungeons: false, Chain: true, BotInvaderDelaySeconds: (60, 180) }
              && LoadTestConfig(none, ["--Preset=official"]).Corrupted is { Invasions: CorruptedInvasionMode.Official, BotInvaders: false, InfamyLossOnDefeat: 0.1, MaxInvasionsPerRun: 3, AntiReset: true, ChainBonus: 0 }
              && LoadTestConfig(none, ["--Corrupted:Invader:DamageFactor=0.5,1,2", "--Corrupted:BotInvaderDelaySeconds=5-10"]).Corrupted is { InvaderDamageFactor: (0.5, 1, 2), BotInvaderDelaySeconds: (5, 10) },
              "Corrupted phase B keys: pve consent with a bot invader, nothing lost, one invasion, no imps, chain bonus 0.1; official rolls, real players, 0.1 loss, 3, imps; the factor list and the range parse");
        try { Directory.Delete(cfg, true); } catch (IOException) { }
        var file = CorruptedSettings.Parse(File.ReadAllText(Path.Combine(DataDir(), "..", "content", "corrupted.json")), "corrupted.json");
        var t = Store.Get(Hunter);
        Check(file.InvaderReward.OrderBy(r => r.Key).SequenceEqual(CorruptedSettings.Default.InvaderReward.OrderBy(r => r.Key)) && file.Rolls == CorruptedRolls.Fixed
              && file.InvaderRewardOf("pro") == new CorruptedInvaderReward(40_000, 6_000) && t.InvaderSpawns.Count > 0
              && t.MobSpawnPoints.Any(m => m.SpawnBehavior == CorruptedData.AntiResetImp && m.Block is not null),
              $"content/corrupted.json: the built-in invasion rewards, Rolls Fixed; CORRUPT-001 has {t.InvaderSpawns.Count} InvaderSpawnpoint(s) and bound anti-reset points");
    }

    private static void Shrine()
    {
        using var f = new Field(21);
        var (run, s, peer) = f.Run();
        var me = s.Player!;
        var map = me.Map!;
        var shrine = run.Shrine!;
        var shown = Of(peer, EventCodes.NewCorruptedShrine).SingleOrDefault();
        // A watcher in the run (a GM): not its owner.
        var watcherChar = f.Hero(run.Id, run.Start.Position + new Vector2(0, 2));
        var (ws, wpeer, _) = Join(f.World, watcherChar.Id);
        f.Tick(11);
        Move(f.World, s, shrine.Position + new Vector2(1.5f, 0));
        Move(f.World, ws, shrine.Position + new Vector2(0, 1.5f));
        var other = Call(f.World, ws, wpeer, new UseCorruptedShrineRequest { wf = f.World.GameNow, ah7 = shrine.Id }, 1);
        var mark = peer.Events.Count;
        var used = Call(f.World, s, peer, new UseCorruptedShrineRequest { wf = f.World.GameNow, ah7 = shrine.Id }, 2);
        var started = Of(peer, EventCodes.CorruptedShrineUsageStart, mark).SingleOrDefault();
        f.Tick(3.1);
        var update = Of(peer, EventCodes.UpdateCorruptedShrine, mark).SingleOrDefault();
        var status = Of(peer, EventCodes.CorruptedDungeonStatus, mark).LastOrDefault();
        Check(shown?.Parameters.GetValueOrDefault((byte)4) is (byte)0 && other.ReturnCode != 0 && used.ReturnCode == 0 && started is not null && Convert.ToInt64(started.Parameters[0]) == me.Id
              && Of(wpeer, EventCodes.CorruptedShrineUsageStart).Any(e => Convert.ToInt64(e.Parameters[0]) == me.Id)
              && update?.Parameters[1] is (byte)1 && status?.Parameters.GetValueOrDefault((byte)7) is true && run.Consented && run.BotDue is { } due
              && due - f.World.Now is var left && left >= TimeSpan.FromSeconds(60 - 15) && left <= TimeSpan.FromSeconds(180) && Kinds(peer, mark).SequenceEqual([12]),
              $"the shrine shown usable (458 state 0); another player's 416 refused (rc {other.ReturnCode}), the owner's rc 0 with 460, 3 s later 459 state 1, 450 key 7 and 449 kind 12; a bot in 60-180 s; 460 is the owner's (key 0 its id), its observer's too");

        // §6 check 1 (late) and 2 (no shrine): a second run whose owner waits 45 s cannot use its shrine; 30 min of rolls never invade it.
        var (late, ls, lpeer) = f.Run();
        f.Tick(11);
        Move(f.World, ls, late.Shrine!.Position + new Vector2(1.5f, 0));
        f.Tick(40);
        var after = Call(f.World, ls, lpeer, new UseCorruptedShrineRequest { wf = f.World.GameNow, ah7 = late.Shrine.Id }, 3);
        CorruptedInvasion? invasion = null;
        Vector2 spawned = default, owner = default;
        for (var t = 0; t < 180 && invasion is null; t += 5)
        {
            f.Tick(5);
            if (run.Invasion is { Bot: { } b } i)
                (invasion, spawned, owner) = (i, b.PositionAt(f.World.Bots.Time), me.PositionAt(DateTime.UtcNow));
        }
        Check(after.ReturnCode != 0 && late.Shrine.State == CorruptedShrine.Inactive && invasion is { Live: true, Bot: { } } && Kinds(peer, mark).Contains(3)
              && (Vector2.Distance(spawned, owner) >= 120 || Store.Get(Hunter).InvaderSpawns.All(p => Vector2.Distance(p.Position, owner) <= Vector2.Distance(spawned, owner) + 8))
              && invasion!.Shards.Count == 3,
              $"after 45 s the shrine is closed (rc {after.ReturnCode}); the consenting owner's bot invader came (449 kind 3) {Vector2.Distance(spawned, owner):0} m off (120 m or the farthest point), 3 shards");

        // §6 check 3: the bot is Hostile to the owner both ways, Neutral to the watcher; beaten: InvaderReward (5000 silver, 300 x 2 infamy), 449 kind 10.
        var bot = invasion!.Bot!;
        var relations = (Pvp.Of(me, bot), Pvp.Of(bot, me), Pvp.Of(ws.Player!, bot), Pvp.Of(bot, ws.Player!));
        var (silver, infamy) = (me.Character.Silver, CorruptedDungeons.Current(me.Character));
        mark = peer.Events.Count;
        lock (f.World.Sync) Pvp.Damage(me, bot, 1e9, EffectType.Physical, HitOrigin.Direct, 0, f.World.GameNow);
        Check(relations == (Relation.Hostile, Relation.Hostile, Relation.Neutral, Relation.Neutral) && invasion.Ended && invasion.OwnerWon
              && me.Character.Silver - silver == (long)Math.Round(5000 * 10000 * (1 + run.Bonus)) && CorruptedDungeons.Current(me.Character) - infamy == 600
              && Kinds(peer, mark).Contains(10) && Of(peer, EventCodes.CorruptedDungeonStatus, mark).LastOrDefault()?.Parameters.GetValueOrDefault((byte)6) is not true,
              $"the invader Hostile to the owner, Neutral to the watcher (got {relations}); beaten: +5000 silver, +600 infamy, 449 kind 10, 450 key 6 off");
        f.Tick(11);
        for (var t = 0; t < 1800 - 240; t += 30) f.Tick(30);
        Check(bot.Map is null && run.Invasion is null && run.Invasions == 1 && late.Invasions == 0 && late.Invasion is null && !Kinds(lpeer).Contains(3),
              "the beaten bot gone; 30 minutes on the run without the shrine was never invaded, the other had its one invasion");
    }

    private static void Shards()
    {
        using var f = new Field(23);
        var (run, s, peer) = f.Run();
        var me = s.Player!;
        f.Tick(1);
        string? why;
        lock (f.World.Sync)
        {
            f.World.Corrupted.Consent(run, me);
            why = f.World.Corrupted.Invade(run, bot: true);
        }
        var invasion = run.Invasion!;
        var mark = peer.Events.Count;
        var (silver, infamy) = (me.Character.Silver, CorruptedDungeons.Current(me.Character));
        foreach (var id in invasion.Shards.ToList())
            lock (f.World.Sync) Combat.Damage(me, (Mob)me.Map!.Objects[id], 1e9, EffectType.Physical, HitOrigin.Direct, 0, f.World.GameNow);
        var counts = Of(peer, EventCodes.CorruptedDungeonUpdate, mark).Where(e => Convert.ToInt32(e.Parameters[1]) == 5).Select(e => Convert.ToInt64(e.Parameters[2])).ToList();
        f.Tick(0.5);
        Check(why is null && counts.SequenceEqual([1L, 2L, 3L]) && Kinds(peer, mark).Last() == 10 && invasion is { Ended: true, OwnerWon: true, Destroyed: 3 }
              && invasion.Bot!.Map is null && run.Invasion is null && me.Character.Silver > silver && CorruptedDungeons.Current(me.Character) - infamy == 600,
              $"3 shards destroyed: 449 kind 5 with 1, 2, 3 (got {string.Join(",", counts)}), then kind 10; the invader banished at once, the owner rewarded");

        // An invader gone without a fight (off the map 10 s: a leave, a disconnect) ends the invasion unpaid.
        var (quit, sq, _) = f.Run();
        f.Tick(1);
        lock (f.World.Sync)
        {
            f.World.Corrupted.Consent(quit, sq.Player!);
            f.World.Corrupted.Invade(quit, bot: true);
            f.World.Bots.Despawn(quit.Invasion!.Bot!, "test", BotRefill.Now);
        }
        var gone = quit.Invasion!;
        (silver, infamy) = (sq.Player!.Character.Silver, CorruptedDungeons.Current(sq.Player.Character));
        f.Tick(1);
        f.Tick(11);
        Check(gone is { Ended: true, OwnerWon: true } && sq.Player.Character.Silver == silver && CorruptedDungeons.Current(sq.Player.Character) == infamy,
              "the invader off the map 10 s: the invasion is over, the owner's but with no InvaderReward");

        // §6 check 5: a second run whose owner the bot knocks down: a knockdown, its items and infamy kept, the run goes on.
        var (other, s2, peer2) = f.Run();
        var victim = s2.Player!;
        f.Tick(1);
        lock (f.World.Sync)
        {
            f.World.Corrupted.Consent(other, victim);
            f.World.Corrupted.SetInfamy(victim.Character, 1000);
            f.World.Corrupted.Invade(other, bot: true);
        }
        var bot = other.Invasion!.Bot!;
        var items = victim.Inventory.Equipment.Items.Count() + victim.Inventory.Bag.Items.Count();
        mark = peer2.Events.Count;
        lock (f.World.Sync) Pvp.Damage(bot, victim, 1e9, EffectType.Physical, HitOrigin.Direct, 0, f.World.GameNow);
        var down = victim.Combat.Down?.Kind;
        f.Tick(6);
        Check(down == KnockdownKind.Pvp && other.Invasion is null && !other.Closed && !other.OwnerLeft && f.World.Corrupted.RunOf(victim.Character.Id) == other
              && victim.Inventory.Equipment.Items.Count() + victim.Inventory.Bag.Items.Count() == items && CorruptedDungeons.Current(victim.Character) == 1000
              && Kinds(peer2, mark).Contains(10) && bot.Map is null,
              $"the bot's win: a PvP knockdown (got {down}), nothing lost (pve InfamyLossOnDefeat 0), 449 kind 10, the bot gone 5 s later, the run goes on");
    }

    private static void Players()
    {
        using var f = new Field(25, o => o with { BotInvaders = false });
        var (a, sa, pa) = f.Run();
        var (b, sb, pb) = f.Run();
        var (c, _, pc) = f.Run();
        lock (f.World.Sync)
        {
            f.World.Corrupted.Consent(a, sa.Player!);
            f.World.Corrupted.Consent(b, sb.Player!);
        }
        CorruptedInvasion? invasion = null;
        for (var t = 0; t < 120 && invasion is null; t++)
        {
            f.Tick(1);
            invasion = a.Invasion ?? b.Invasion;
        }
        var target = invasion?.Run;
        var from = invasion?.From;
        var (ts, tpeer, ipeer) = target == a ? (sa, pa, pb) : (sb, pb, pa);
        var countdown = Of(ipeer, EventCodes.CorruptedDungeonUpdate).Where(e => Convert.ToInt32(e.Parameters[1]) == 6).ToList();
        Check(invasion is { Live: false, Bot: null } && from is not null && from != target && (target == a || target == b) && c.Invasion is null && countdown.Count == 1
              && Convert.ToInt64(countdown[0].Parameters[2]) > f.World.GameNow,
              "two consenting Hunters matched within 2 min: the invader gets 449 kind 6 with the countdown's end; the third run (no shrine) is no target");

        // The countdown's end: the push into the target {0 its id, 1 CORRUPT-001, 4 BEGINNER}; the invader's Join begins the fight.
        f.Tick(10.5);
        var push = ipeer.Responses[^1];
        var mark = tpeer.Events.Count;
        var (si, ip2, _) = Join(f.World, from!.Owner);
        var invader = si.Player!;
        var owner = ts.Player!;
        var status = Of(ip2, EventCodes.CorruptedDungeonStatus).LastOrDefault();
        Check(push.Parameters.GetValueOrDefault((byte)0) as string == target!.Id && push.Parameters.GetValueOrDefault((byte)1) is "CORRUPT-001" && push.Parameters.GetValueOrDefault((byte)4) is "CORRUPTED_DUNGEON_BEGINNER"
              && invasion!.Live && from.Away && Kinds(tpeer, mark).Contains(3) && status?.Parameters.GetValueOrDefault((byte)6) is true && status.Parameters[1] is byte[] host && new Guid(host) == target.Owner
              && Pvp.Of(owner, invader) == Relation.Hostile && Pvp.Of(invader, owner) == Relation.Hostile,
              "the push into the target's instance; at the invader's Join 449 kind 3 to the owner, 450 {1 the owner, 6 true} to the invader, the pair Hostile");

        // The run is the owner's alone: the invader's kill of a counted mob gives no points (its fame leaves W), its chests refuse him.
        var counted = target.Worth.Keys.Select(id => owner.Map!.Objects.GetValueOrDefault(id)).OfType<Mob>().First();
        var (points, weight) = (target.Points, target.Weight);
        lock (f.World.Sync) Combat.Damage(invader, counted, 1e9, EffectType.Physical, HitOrigin.Direct, 0, f.World.GameNow);
        var chest = target.Chests.Append(target.BossChest).OfType<LootChest>().FirstOrDefault();
        string? refused, mine;
        lock (f.World.Sync) (refused, mine) = (chest is null ? "" : f.World.Corrupted.ChestRefused(invader, chest), chest is null ? null : f.World.Corrupted.ChestRefused(owner, chest));
        Check(counted.Dead && target.Points == points && !target.Worth.ContainsKey(counted.Id) && target.Weight < weight && refused is not null && mine is null,
              $"the invader's kill: no points ({target.Points}), its fame off W; a chest refused to him ({refused}), open to the owner");

        // The owner beats the invader: InvaderReward, 449 kind 9 to the invader with the 30 s expel (its last 449, no 450 after it); then the push home.
        var silver = owner.Character.Silver;
        var before = ip2.Events.Count;
        lock (f.World.Sync) Pvp.Damage(owner, invader, 1e9, EffectType.Physical, HitOrigin.Direct, 0, f.World.GameNow);
        var expel = Of(ip2, EventCodes.CorruptedDungeonUpdate, before).Where(e => Convert.ToInt32(e.Parameters[1]) == 9).ToList();
        var last = Kinds(ip2, before).LastOrDefault();
        var statusAfter = Of(ip2, EventCodes.CorruptedDungeonStatus, before).Count;
        var paired = f.World.Corrupted.Opponents(owner, invader);
        f.Tick(31);
        var home = ip2.Responses[^1];
        Check(invasion!.Ended && invasion.OwnerWon && owner.Character.Silver > silver && expel.Count == 1 && last == 9 && statusAfter == 0 && !paired
              && home.Parameters.GetValueOrDefault((byte)0) as string == from.Id && home.Parameters.GetValueOrDefault((byte)4) is "CORRUPTED_DUNGEON_BEGINNER" && !from.Away && target.Invasion is null,
              "the owner's win: its reward, the invader's 449 kind 9, the pair no longer opponents; 30 s later the push home {0 its own run, 4 BEGINNER}");
        Check(!Kinds(pc).Contains(3) && !Kinds(pc).Contains(6), "the run without the shrine never invaded nor invading");
    }

    private static void Maps()
    {
        using var f = new Field(27, o => o with { MapsOpenDungeons = true }, EventSettings.Default.MoveTickets()); // as ServerHost does with the switch on
        var events = f.World.EventSettings;
        _ = Store.Get("0204");
        var hero = f.Hero("0204", new Vector2(3, 0), new InventoryItem("T6_GVGSEASONREWARD_FAMEBUFF_SHORT", 1), new InventoryItem("T5_CORRUPTED_NONLETHAL_MAP", 1));
        var (s, peer, _) = Join(f.World, hero.Id);
        var me = s.Player!;
        var vow = me.Inventory.Bag.Items.Single(i => i.Template.Name == "T6_GVGSEASONREWARD_FAMEBUFF_SHORT");
        lock (f.World.Sync) CombatTest.Send(s, new UseConsumableFromInventoryRequest { ah5 = vow.Id, ah6 = 1 });
        f.Tick(2);
        var invasion = f.World.Events.Running.SingleOrDefault();
        Check(events.Player.Items.Count == 10 && events.ItemFor(Client.Items.Find("T6_GVGSEASONREWARD_FAMEBUFF_SHORT")!) is { Kind: "invasion", Tier: 6 }
              && events.ItemFor(Client.Items.Find("T5_CORRUPTED_NONLETHAL_MAP")!) is null && events.Merchant.Prices.Count == 0
              && events.Drops.Item == "T5_GVGSEASONREWARD_FAMEBUFF_SHORT" && invasion is { Tier: 6 },
              $"the tickets moved to the Knight's Vows (T4-T8, no market orders, the drop T5 vow): the T6 vow starts a T6 invasion (got {invasion?.Tier})");
        lock (f.World.Sync) foreach (var r in f.World.Events.Running.ToList()) f.World.Events.Stop(r);

        // The corrupted map opens a corrupted portal (its activaterandomdungeon) and no invasion; its enter cast goes into a Hunter run.
        _ = Store.Get(Hunter);
        var map = me.Map!;
        var item = me.Inventory.Bag.Items.Single(i => i.Template.Name == "T5_CORRUPTED_NONLETHAL_MAP");
        lock (f.World.Sync) CombatTest.Send(s, new UseConsumableFromInventoryRequest { ah5 = item.Id, ah6 = 1 });
        f.Tick(5);
        var portal = f.World.Dungeons.ExitsOf(map).SingleOrDefault(e => e.FromMap);
        Check(portal is { Name: "CORRUPTED_SOLO_NONLETHAL", Info.Corrupted: true } && f.World.Events.Running.Count == 0 && !me.Inventory.Bag.Items.Any(i => i.Template == item.Template),
              $"the corrupted map used: a personal CORRUPTED_SOLO_NONLETHAL portal, no invasion (got {portal?.Name})");
        Move(f.World, s, portal!.Position + new Vector2(2, 0));
        var spell = f.World.Spells!.IdOf("ENTER_CURRUPTED_DUNGEON_SPELL_1");
        lock (f.World.Sync) s.OnRequest(Request(new CastStartRequest { wf = f.World.GameNow, dx = CorruptedDungeons.EnterSlot, d0 = spell, d2 = portal.Position, d4 = 1 }, null), encrypted: false);
        f.Tick(3.1);
        var run = f.World.Corrupted.RunOf(hero.Id);
        Check(run is { Difficulty.Index: 0 } && run.Back.Cluster == "0204" && peer.Responses[^1].Parameters.GetValueOrDefault((byte)4) is "CORRUPTED_DUNGEON_BEGINNER" && portal.LastUse is not null,
              "the portal's enter cast (slot 25, Hunter) 3 s later: a Hunter run, its way back in 0204, the portal used");
    }

    private static void Chain()
    {
        using var f = new Field(29);
        var (run, s, peer) = f.Run();
        var me = s.Player!;
        var mark = peer.Events.Count;
        lock (f.World.Sync) f.World.Corrupted.Complete(run, me.Map!);
        var chain = run.ChainExit;
        var exit = chain?.BuildCreate(me).OfType<NewRandomDungeonExitEvent>().Single();
        var other = f.World.Spells!.IdOf("ENTER_CURRUPTED_DUNGEON_SPELL_2");
        var refused = Call(f.World, s, peer, new CastStartRequest { wf = f.World.GameNow, dx = CorruptedDungeons.EnterSlot, d0 = other, d2 = chain?.Position ?? default, d4 = 1 }, 1);
        Check(chain is { Chain: true } && exit is { c8z: true } && Kinds(peer, mark).SequenceEqual([2, 15]) && Of(peer, EventCodes.CorruptedDungeonStatus, mark).LastOrDefault()?.Parameters.GetValueOrDefault((byte)8) is true
              && refused.ReturnCode == WorldSession.CastRefused,
              $"completed: 449 kinds 2, 15 (got {string.Join(",", Kinds(peer, mark))}), 450 key 8, the chain exit (324 key 12 true); a Stalker Next Dungeon from a Hunter run refused (960)");
        var spell = f.World.Spells.IdOf("ENTER_CURRUPTED_DUNGEON_SPELL_1");
        lock (f.World.Sync) s.OnRequest(Request(new CastStartRequest { wf = f.World.GameNow, dx = CorruptedDungeons.EnterSlot, d0 = spell, d2 = chain!.Position, d4 = 2 }, null), encrypted: false);
        f.Tick(3.1);
        var next = f.World.Corrupted.RunOf(run.Owner);
        var push = peer.Responses[^1];
        Check(next is { Streak: 1, Difficulty.Index: 0 } && next != run && next.Back == run.Back && run.OwnerLeft && push.Parameters.GetValueOrDefault((byte)0) as string == next.Id
              && push.Parameters.GetValueOrDefault((byte)4) is "CORRUPTED_DUNGEON_BEGINNER" && Math.Abs(next.ChainFactor - 1.1) < 1e-9,
              "Next Dungeon at the chain exit: a new Hunter run (the push {0 its id, 4 BEGINNER}), the first run's way back, streak 1 (x1.1), the old run left");
    }
}
