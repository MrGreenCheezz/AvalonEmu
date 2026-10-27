using System.Numerics;
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
using Microsoft.Data.Sqlite;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The guild hideouts (analysis/work/stageP/hideouts/SPEC.md H1-H4) in Northstrand Dunes (2305, OPENPVP_BLACK_1, one watchtower territory at
/// (-15, 145), timeregion US_22_00): placement by the rules, the construction phases, persistence.
/// </summary>
public static class HideoutTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("hideouts H1: data (hideouts.xml, kit, zones); placement refused by the rules (no guild, permission, prime time, territory, one per zone, 180 m) and allowed (op 69, 15 s, kit used, 419)", Placement),
        ("hideouts H1: construction 1200 s -> invulnerable until the next prime time -> 1200 s -> built (419 mode 1 phases, then mode 2 at 2 shields); ConstructionSeconds 30", Construction),
        ("hideouts H1: a hideout survives a restart; v39 -> v40 migration adds hideouts and hideout_homes", Persistence),
        ("hideouts H2: entry (237, 4 s, push {0 @HIDEOUT@, 1 HIDEOUT-0001a, 2 guild}), the interior (420, 425, 421, bank, repair), exit back; a stranger refused (private), allowed (public); gone hideout at login", Entry),
        ("hideouts H3: own vault (a hideout deposit is not in the Lymhurst bank and back), Make Home (205, UpdateHome, Join 67), respawn inside, Journey Back (BuyJourney kind 1), Leave Home, access preset (198)", Services),
        ("hideouts H4: food (397) in and eaten at the prime time, a starving day costs a shield; tribute (400) to the territory owner; upgrade (394: 20 M + resources, 48 h, put out, HIDEOUT-0001b with a market); restart", Upkeep),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private const string Zone = "2305";

    private static readonly DateTime Day = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

    internal sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    /// <summary>One world on a test database; the game time is <see cref="Day"/> + <see cref="Shift"/> + the clock (Shift starts 10 min into 2305's prime time).</summary>
    internal sealed class Fixture : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly SqliteCharacterRepository Characters;
        public readonly ManualClock Clock = new();
        public readonly ServerConfig Config;
        public World World;
        public TimeSpan Shift;

        public Fixture(ServerConfig? config = null)
        {
            Config = (config ?? new ServerConfig()) with { JoinByCharacterId = true, GuildCreateCost = 0, GuildRejoinCooldownMinutes = 0 };
            Characters = Repository(Dir);
            _ = Client.Clusters!.Get(Zone);
            World = Make();
            Shift = (World.Hideouts.PrimeStart(Zone) ?? throw new Exception("2305 has no prime time")) + TimeSpan.FromMinutes(10);
        }

        private World Make() => new(Config, Characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = Clock, Clusters = Client.Clusters, MapSeed = 3, GameTicks = () => Day.Ticks + Shift.Ticks + Clock.Now.Ticks, MobAi = false,
        };

        public void Restart()
        {
            World.Dispose();
            World = Make();
        }

        public Member Join(string name, Vector2 at, string cluster = Zone, params (string Item, int Count)[] items)
        {
            var c = StarterKit.Default.NewCharacter(name + Guid.NewGuid().ToString("N")[..6]);
            (c.Cluster, c.PositionX, c.PositionY) = (cluster, at.X, at.Y);
            c.Silver = 1_000_000 * Pricing.A7m;
            foreach (var (item, count) in items) c.Inventory.Add(new InventoryItem(item, count));
            Check(Characters.Create(c), $"character {name} created");
            return Rejoin(c.Id);
        }

        /// <summary>A Join of character <paramref name="id"/> on a new connection (after a cluster change, or a login), waiting for its map.</summary>
        public Member Rejoin(Guid id)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = id }, null), encrypted: true);
            Check(session.Player is not null, $"join of {id} answered with a player (RC {peer.Responses.FirstOrDefault()?.ReturnCode})");
            var map = session.Player!.Map!;
            for (var i = 0; i < 200 && map.Template is null; i++)
            {
                Thread.Sleep(20);
                Run(0.1);
            }
            Run(0.25);
            return new Member(session, peer, session.Player!);
        }

        public Guild Guild(Member founder, string name)
        {
            Check(World.Guilds.Create(founder.Player, name, GuildLogo.Default, "", out _) == 0, $"guild {name} founded");
            return World.Guilds.Of(founder.Id)!;
        }

        public void Into(Member inviter, Member joiner)
        {
            lock (World.Sync)
                Check(World.Guilds.Invite(inviter.Player, joiner.Player.Character.Name, out _, out _) == 0 && World.Guilds.Accept(joiner.Player, out var g) == 0
                      && g is not null && Do(() => World.Guilds.AfterJoin(joiner.Player, g)), $"{joiner.Player.Character.Name} joins");
        }

        public void Run(double seconds)
        {
            for (var i = 0; i < Math.Max(1, Math.Round(seconds * 4)); i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(250));
                lock (World.Sync) World.Update();
            }
        }

        /// <summary>The game time jumps by <paramref name="by"/> (the world clock does not), then a tick a second later.</summary>
        public void Jump(TimeSpan by)
        {
            Shift += by;
            Run(1.25);
        }

        public void Move(Member m, Vector2 to)
        {
            lock (World.Sync) m.Player.Teleport(to, 0, new DateTime(World.GameNow, DateTimeKind.Utc));
            Run(0.25);
        }

        /// <summary>.tele <paramref name="cluster"/> x z for <paramref name="m"/> (the push follows; Rejoin then).</summary>
        public void Tele(Member m, string cluster, Vector2 at)
        {
            lock (World.Sync) World.Commands.Execute(new CommandContext(World, m.Player, AccountLevel.Admin, (_, _) => { }), FormattableString.Invariant($".tele {cluster} {at.X} {at.Y}"));
        }

        public OperationResponse? Call(Member m, AlbionRequest req, int id = 7)
        {
            var before = m.Peer.Responses.Count;
            lock (World.Sync) m.Session.OnRequest(Request(req, id), encrypted: false);
            return m.Peer.Responses.Count > before ? m.Peer.Responses[^1] : null;
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    private static bool Do(Action a)
    {
        a();
        return true;
    }

    internal static IEnumerable<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) =>
        peer.Events.Skip(from).Where(e => Code(e) == (int)code).Select(e => e.Parameters);

    private static long L(Dictionary<byte, object?> p, byte k) => Convert.ToInt64(p.GetValueOrDefault(k) ?? 0L);

    /// <summary>A spot of 2305 where <paramref name="m"/> may place a hideout now (searched on a 30 m grid around the zone's centre).</summary>
    internal static Vector2 Spot(Fixture f, Member m)
    {
        for (var r = 60; r < 420; r += 30)
            for (var a = 0; a < 360; a += 15)
            {
                var at = new Vector2(MathF.Round(r * MathF.Cos(a * MathF.PI / 180)), MathF.Round(r * MathF.Sin(a * MathF.PI / 180)));
                string? why;
                lock (f.World.Sync) why = f.World.Hideouts.CheckPlace(m.Player, at);
                if (why is null) return at;
            }
        throw new Exception("no free hideout spot in 2305");
    }

    /// <summary>Places a hideout of <paramref name="m"/>'s guild through op 69 with its kit and the 15 s; returns the hideout.</summary>
    internal static Hideout PlaceByKit(Fixture f, Member m, Vector2 at)
    {
        f.Move(m, at + new Vector2(6, 0));
        var kit = m.Player.Inventory.Bag.Items.First(i => i.Template.Name == HideoutData.Kit);
        f.Call(m, new PlaceableObjectPlaceRequest { x6 = f.World.GameNow, x7 = kit.Id, x8 = f.World.Hideouts.BuildingType, x9 = at, ya = 90 });
        f.Run(15.5);
        return f.World.Hideouts.All.Single(h => h.Position == at);
    }

    private static void Placement()
    {
        using var f = new Fixture();
        var d = f.World.Hideouts.Data;
        Check(d.Phases is [{ Seconds: 1200, MaxDurability: 20_000, Invulnerable: false }, { Invulnerable: true }, { Seconds: 1200, MaxDurability: 40_000 }]
              && d.Levels is [{ Cluster: "HIDEOUT-0001a", MaxShields: 2, DailyUpkeep: 15_000, MaxNutrition: 150_000, HomeAlliance: false },
                  { Cluster: "HIDEOUT-0001b", MaxShields: 4, UpgradeSilver: 20_000_000, UpgradeSeconds: 172_800, UpgradeResources.Count: 4 }, { Cluster: "HIDEOUT-0001c", HomeAlliance: true }]
              && d.StartingNutrition == 15_000 && d.EnterChannelSeconds == 4 && d.MinDistance == 180 && d.MinDistanceInTunnel == 60 && d.PlacementSeconds == 15
              && d.PrimeTimeMinutes == 60 && d.MaxPerGuild == 6 && d.PlacementTypes.Contains("OPENPVP_BLACK_1") && d.PlacementTypes.Contains("OPENPVP_BLACK_6")
              && !d.PlacementTypes.Contains("PLAYERCITY_SAFEAREA_01") && d.TunnelTypes.Contains("TUNNEL_HIDEOUT") && f.World.Hideouts.BuildingType > 0,
              $"hideouts.xml: 3 phases (1200 s / invulnerable / 1200 s), levels a/b/c (2/4/5 shields, upkeep, b 20 M + 48 h), kit 180/60 m 15 s 60 min, 6 per guild, black zones (type {f.World.Hideouts.BuildingType})");
        Check(HideoutData.InstanceId(Guid.Empty) == "@HIDEOUT@00000000-0000-0000-0000-000000000000" && HideoutData.GuidOf(HideoutData.InstanceId(Guid.Parse("0e860295-bb5c-4be1-89d0-9b1bbcba0b99"))) is { } g
              && g == Guid.Parse("0e860295-bb5c-4be1-89d0-9b1bbcba0b99") && HideoutData.GuidOf("2305") is null, "instance id \"@HIDEOUT@<guid>\" and back");

        var anna = f.Join("Anna", new Vector2(0, 0), items: (HideoutData.Kit, 2));
        var bert = f.Join("Bert", new Vector2(5, 0));
        var cara = f.Join("Cara", new Vector2(-5, 0), items: (HideoutData.Kit, 1));
        string? Why(Member m, Vector2 at)
        {
            lock (f.World.Sync) return f.World.Hideouts.CheckPlace(m.Player, at);
        }
        var monolith = new Vector2(-15, 145);
        Check(Why(anna, Vector2.Zero) is { } noGuild && noGuild.Contains("NOT_IN_GUILD"), $"no guild: refused ({Why(anna, Vector2.Zero)})");
        var wolves = f.Guild(anna, "Wolves");
        f.Into(anna, bert);
        var spot = Spot(f, anna);
        Check(Why(bert, spot) is { } recruit && recruit.Contains("NO_GUILDPERMISSION"), $"a recruit lacks Manage Hideouts ({Why(bert, spot)})");
        Check(Why(anna, monolith + new Vector2(10, 0)) is { } near && near.Contains("BLOCKED_BY_TERRITORY"), $"10 m from the monolith: blocked ({Why(anna, monolith + new Vector2(10, 0))})");
        f.Shift += TimeSpan.FromHours(3);
        Check(Why(anna, spot) is { } late && late.Contains("WRONG_TIME"), $"3 h after the prime time: refused ({Why(anna, spot)})");
        f.Shift -= TimeSpan.FromHours(3);

        // Through the kit (op 69): 15 s, then the hideout; one kit used; 419 to the zone in construction phase 0.
        f.Move(anna, spot + new Vector2(6, 0));
        var from = anna.Peer.Events.Count;
        var kit = anna.Player.Inventory.Bag.Items.First(i => i.Template.Name == HideoutData.Kit);
        f.Call(anna, new PlaceableObjectPlaceRequest { x6 = f.World.GameNow, x7 = kit.Id, x8 = f.World.Hideouts.BuildingType, x9 = spot, ya = 90 });
        f.Run(10);
        Check(!f.World.Hideouts.All.Any(), "10 s into the placement: no hideout yet (placementduration 15 s)");
        f.Run(5.5);
        var h = f.World.Hideouts.All.SingleOrDefault();
        var e = Of(anna.Peer, EventCodes.NewHideoutObject, from).LastOrDefault();
        Check(h is { Phase: 0, Built: false, Level: 0, Nutrition: 15_000 } && h.Guild == wolves.Id && h.Cluster == Zone && h.Position == spot && kit.Count == 1
              && Math.Abs(new DateTime(h.PhaseEnds, DateTimeKind.Utc).Subtract(new DateTime(h.Placed, DateTimeKind.Utc)).TotalSeconds - 1200) < 1,
              $"placed after 15 s: phase 0 for 1200 s, 15000 food, one kit left (got {h}, {kit.Count} kit(s))");
        Check(e is not null && e[1] is byte[] hid && new Guid(hid) == h!.Id && L(e, 2) == f.World.Hideouts.BuildingType && e[3] is HideoutData.Kit
              && e[6] is byte[] gid && new Guid(gid) == wolves.Id && e[7] is "Wolves" && L(e, 13) == 0 && e.ContainsKey(13) && L(e, 14) == 2 && L(e, 15) == 1
              && e.ContainsKey(17) && L(e, 17) == 0 && L(e, 18) == h.PhaseEnds && L(e, 21) == 20_000 * Pricing.A7m,
              "419: 1 the hideout, 2 UNIQUE_HIDEOUT's type, 3 its name, 6/7 Wolves, 13 level 0, 14 2 shields, 15 mode 1 (construction), 17 phase 0, 18 its end, 21 20000 durability");
        var access = Of(anna.Peer, EventCodes.AccessStatus, from).Select(p => { var a = new AccessStatusEvent(); AlbionSerializer.FillFields(a, p); return a; })
            .FirstOrDefault(a => a.ddn == L(e!, 0));
        Check(access is { bq5: 1, bq9: ["owner", "visitor", "noaccess"] } && access.bra is ["", "Wolves", ""], "its AccessStatus: preset 1 (private), the guild visitor");

        Check(Why(anna, -spot) is { } twice && twice.Contains("MAX_HIDEOUTS_IN_CLUSTER"), "a second hideout of Wolves in 2305: refused");
        var bears = f.Guild(cara, "Bears");
        Check(Why(cara, spot + new Vector2(100, 0)) is { } close && close.Contains("TOO_CLOSE"), $"Bears 100 m from Wolves' hideout: refused ({Why(cara, spot + new Vector2(100, 0))})");
        // A refused op 69 keeps the kit and says why.
        var caraKit = cara.Player.Inventory.Bag.Items.First(i => i.Template.Name == HideoutData.Kit);
        var lines = cara.Peer.Events.Count;
        f.Call(cara, new PlaceableObjectPlaceRequest { x6 = f.World.GameNow, x7 = caraKit.Id, x8 = f.World.Hideouts.BuildingType, x9 = spot + new Vector2(100, 0) });
        f.Run(16);
        Check(caraKit.Count == 1 && f.World.Hideouts.OfGuild(bears.Id).Count == 0 && Of(cara.Peer, EventCodes.SystemMessage, lines).Any(),
              "Bears' refused placement: the kit stays, a system line");
        using var off = new Fixture(new ServerConfig { Hideouts = new HideoutOptions { RequirePrimeTime = false, RequireTerritory = false, MaxPerGuild = 0 } });
        var dora = off.Join("Dora", Vector2.Zero);
        off.Guild(dora, "Owls");
        off.Shift += TimeSpan.FromHours(5);
        string? offWhy;
        lock (off.World.Sync) offWhy = off.World.Hideouts.CheckPlace(dora.Player, new Vector2(300, -300));
        Check(offWhy is { } cap && cap.Contains("MAX_HIDEOUTS_FOR_GUILD"), $"RequirePrimeTime/RequireTerritory false: any hour; MaxPerGuild 0: none ({offWhy})");
    }

    private static void Construction()
    {
        using var f = new Fixture();
        var anna = f.Join("Anna", Vector2.Zero, items: (HideoutData.Kit, 1));
        f.Guild(anna, "Wolves");
        var h = PlaceByKit(f, anna, Spot(f, anna));
        var from = anna.Peer.Events.Count;
        var placed = new DateTime(h.Placed, DateTimeKind.Utc);
        f.Jump(TimeSpan.FromSeconds(1200));
        var prime = f.World.Hideouts.NextPrimeTime(Zone, placed.AddSeconds(1200));
        Check(h.Phase == 1 && !h.Built && Math.Abs((new DateTime(h.PhaseEnds, DateTimeKind.Utc) - prime).TotalSeconds) < 1 && (prime - placed).TotalHours is > 20 and < 24.1,
              $"1200 s: phase 2 (invulnerable) until the next prime time {prime:MM-dd HH:mm} (got phase {h.Phase} until {new DateTime(h.PhaseEnds, DateTimeKind.Utc):MM-dd HH:mm})");
        f.Jump(prime - new DateTime(f.World.GameNow, DateTimeKind.Utc) + TimeSpan.FromSeconds(1));
        Check(h.Phase == 2 && !h.Built, $"the next prime time: phase 3 (got {h.Phase})");
        f.Jump(TimeSpan.FromSeconds(1200));
        Check(h is { Built: true, Level: 0, Shields: 2, PhaseEnds: 0 }, $"1200 s later: built at level 0 with 2 shields (got {h})");
        var modes = Of(anna.Peer, EventCodes.NewHideoutObject, from).Select(p => (L(p, 15), L(p, 17))).ToList();
        Check(modes.SequenceEqual([(1L, 1L), (1L, 2L), (2L, 0L)]), $"419 again at each change: phase 1, phase 2, established (got {string.Join(" ", modes)})");

        using var quick = new Fixture(new ServerConfig { Hideouts = new HideoutOptions { ConstructionSeconds = 30 } });
        var bert = quick.Join("Bert", Vector2.Zero, items: (HideoutData.Kit, 1));
        quick.Guild(bert, "Bears");
        var q = PlaceByKit(quick, bert, Spot(quick, bert));
        quick.Jump(TimeSpan.FromSeconds(10));
        quick.Jump(TimeSpan.FromSeconds(10));
        Check(!q.Built && q.Phase == 2, $"ConstructionSeconds 30: three phases of 10 s (got phase {q.Phase})");
        quick.Jump(TimeSpan.FromSeconds(10));
        Check(q.Built, "and built after 30 s");
    }

    private static void Persistence()
    {
        using (var f = new Fixture())
        {
            var anna = f.Join("Anna", Vector2.Zero, items: (HideoutData.Kit, 1));
            var wolves = f.Guild(anna, "Wolves");
            var h = PlaceByKit(f, anna, Spot(f, anna));
            f.Restart();
            var again = f.World.Hideouts.Find(h.Id);
            Check(again is { Phase: 0, Built: false } && again.Guild == wolves.Id && again.Position == h.Position && again.PhaseEnds == h.PhaseEnds && again.Nutrition == 15_000,
                  "restart: the hideout is back in construction phase 0 with its end and food");
            var b = f.Join("Bert", again!.Position + new Vector2(10, 0));
            Check(b.Player.Map!.Objects.Values.OfType<HideoutObject>().Any(o => o.Hideout == again), "and stands in 2305 again");
            lock (f.World.Sync) f.World.Guilds.Disband(f.World.Guilds.ById(wolves.Id)!);
            Check(f.World.Hideouts.Find(h.Id) is null && !b.Player.Map.Objects.Values.OfType<HideoutObject>().Any(), "the guild disbanded: its hideout is gone");
            f.Restart();
            Check(f.World.Hideouts.Find(h.Id) is null, "and stays gone after a restart");
        }

        var dir = NewDir();
        var path = Path.Combine(dir, "save.db");
        try
        {
            using (new SqliteCharacterRepository(path)) { }
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "ALTER TABLE progression DROP COLUMN tracked; DROP TABLE hideout_homes; DROP TABLE hideouts; PRAGMA user_version=39;";
                cmd.ExecuteNonQuery();
            }
            using (var migrated = new SqliteCharacterRepository(path))
                Check(migrated.LoadHideouts().Count == 0 && migrated.LoadHideoutHomes().Count == 0, "v39 save migrated: empty hideouts and hideout_homes");
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 43, "schema version 43");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>A built hideout of a new guild "Wolves" (founder Anna, kit placed in 2305, construction finished); Anna stands next to it.</summary>
    internal static (Member Anna, Hideout Hideout, HideoutObject Surface) Built(Fixture f, params (string Item, int Count)[] items)
    {
        _ = Client.Clusters!.Get("HIDEOUT-0001a");
        var anna = f.Join("Anna", Vector2.Zero, items: [(HideoutData.Kit, 1), .. items]);
        f.Guild(anna, "Wolves");
        var h = PlaceByKit(f, anna, Spot(f, anna));
        lock (f.World.Sync) f.World.Hideouts.Finish(h);
        var surface = anna.Player.Map!.Objects.Values.OfType<HideoutObject>().Single(o => o.Hideout == h);
        return (anna, h, surface);
    }

    /// <summary>ExitEnterStart on <paramref name="target"/>, the channel run out; then the session's push (null when none came).</summary>
    internal static OperationResponse? Through(Fixture f, Member m, long target)
    {
        var before = m.Peer.Responses.Count;
        f.Call(m, new ExitEnterStartRequest { wf = f.World.GameNow, kq = target });
        f.Run(4.5);
        return m.Peer.Responses.Skip(before).LastOrDefault(r => Convert.ToInt16(r.Parameters.GetValueOrDefault(ReservedKeys.OperationCode) ?? (short)0) == (short)OperationCodes.ChangeCluster);
    }

    private static void Entry()
    {
        using var f = new Fixture();
        var (anna, h, surface) = Built(f);
        var bert = f.Join("Bert", surface.Position + new Vector2(8, 0));
        Check(h.Built && f.World.Hideouts.WorldOf(h.Instance) == "HIDEOUT-0001a" && f.World.Hideouts.WorldOf("2305") is null, "built: the instance is HIDEOUT-0001a's");

        // A stranger (no guild) at the private hideout: refused, 254 and a line.
        var from = bert.Peer.Events.Count;
        var refused = Through(f, bert, surface.Id);
        Check(refused is null && bert.Session.Status == SessionStatus.InWorld && Of(bert.Peer, EventCodes.ExitEnterCancel, from).Any() && Of(bert.Peer, EventCodes.SystemMessage, from).Any(),
              "a stranger at the private hideout: ExitEnterCancel and a system line, no change");

        // The founder: 253, 4 s, 255, the push {0 the instance, 1 HIDEOUT-0001a, 2 Wolves}.
        from = anna.Peer.Events.Count;
        var seen = bert.Peer.Events.Count;
        var push = Through(f, anna, surface.Id);
        Check(Of(bert.Peer, EventCodes.ExitEnterStart, seen).Any(p => L(p, 0) == anna.Player.Id && L(p, 3) == surface.Id) && Of(anna.Peer, EventCodes.ExitEnterFinished, from).Any()
              && push is not null && push.Parameters[0] is string id && id == h.Instance && push.Parameters[1] is "HIDEOUT-0001a" && push.Parameters[2] is "Wolves"
              && !push.Parameters.ContainsKey(ReservedKeys.RequestId) && anna.Session.Status == SessionStatus.Transferring,
              $"the guild member: 253 (to those in view), 255, then the push {{0 {h.Instance}, 1 HIDEOUT-0001a, 2 Wolves}}");
        var inside = f.Rejoin(anna.Id);
        var map = inside.Player.Map!;
        var mgmt = Of(inside.Peer, EventCodes.NewHideoutManagement).FirstOrDefault();
        var update = Of(inside.Peer, EventCodes.HideoutManagementUpdate).FirstOrDefault();
        var exit = map.Objects.Values.OfType<HideoutExitObject>().SingleOrDefault();
        Check(map.Cluster == h.Instance && map.Template?.Id == "HIDEOUT-0001a" && mgmt is not null && mgmt[1] is string mid && mid == h.Instance
              && mgmt[4] is "Wolves" && update is not null && update.ContainsKey(8) && L(update, 9) == 15_000 * Pricing.A7m && exit is not null
              && Of(inside.Peer, EventCodes.NewHideoutExit).Any(p => L(p, 0) == exit.Id)
              && map.Objects.Values.OfType<Building>().Any(b => b.Template.Kind == "bankbuilding") && map.Objects.Values.OfType<Building>().Any(b => b.Template.Kind == "repairbuilding")
              && Vector2.Distance(inside.Player.PositionAt(DateTime.UtcNow), exit.Position) < 12,
              $"inside {map.Template?.Id}: NewHideoutManagement {{1 the instance, 4 Wolves}}, 425 (8 level, 9 the food 15000), NewHideoutExit, the bank and the repair shop; arrival at the exit");

        // Out through the exit: back in 2305 in front of the hideout.
        var outPush = Through(f, inside, exit!.Id);
        Check(outPush?.Parameters[0] is "2305", "the exit: the push {0 2305}");
        var outside = f.Rejoin(anna.Id);
        Check(outside.Player.Map!.Cluster == "2305" && Vector2.Distance(outside.Player.PositionAt(DateTime.UtcNow), h.Position) < 20, "back in 2305 in front of the hideout");

        // Public: the stranger walks in (ChangeCluster on the object, at once).
        h.Access = HideoutAccess.Public;
        var walk = f.Call(bert, new ChangeClusterRequest { ee = surface.Id }, 9);
        Check(walk is { ReturnCode: 0 } && walk.Parameters[0] is string wid && wid == h.Instance && walk.Parameters[1] is "HIDEOUT-0001a",
              $"public: the stranger's ChangeCluster on the hideout goes in (RC {walk?.ReturnCode})");
        var guest = f.Rejoin(bert.Id);
        Check(guest.Player.Map!.Cluster == h.Instance, "and joins inside");

        // The hideout gone while the stranger is inside: out to 2305; a login saved inside a gone hideout too.
        var saved = guest.Player.Character.Clone();
        lock (f.World.Sync) f.World.Hideouts.Remove(h, "test");
        Check(guest.Session.Status == SessionStatus.Transferring && guest.Player.Character.Cluster == "2305", "the hideout removed: the guest is put out into 2305");
        bool moved;
        lock (f.World.Sync) moved = f.World.Hideouts.ReturnIfGone(saved);
        Check(moved && saved.Cluster == "0000", $"a login saved inside the gone hideout (no row any more): its last city, Thetford without one (got {saved.Cluster})");
    }

    /// <summary>RegisterToObject on the bank of <paramref name="m"/>'s map: the items of the vault tab it attaches (their template names) and the container.</summary>
    private static (List<string> Items, Guid Container) OpenBank(Fixture f, Member m)
    {
        var bank = m.Player.Map!.Objects.Values.OfType<Building>().First(b => b.Template.Kind == "bankbuilding");
        f.Move(m, bank.Position + new Vector2(2, 0));
        var from = m.Peer.Events.Count;
        f.Call(m, new RegisterToObjectRequest { zg = bank.Id });
        var attach = Of(m.Peer, EventCodes.AttachItemContainer, from).Last();
        var ids = (long[])attach[3]!;
        var container = new Guid((byte[])attach[1]!);
        var names = ids.Where(id => id != 0).Select(id => m.Player.FindContainer(container)!.Items.First(i => i.Id == id).Template.Name).ToList();
        return (names, container);
    }

    private static void Deposit(Fixture f, Member m, Guid vault, string item)
    {
        var stack = m.Player.Inventory.Bag.Items.First(i => i.Template.Name == item);
        f.Call(m, new InventoryMoveItemRequest { ru = m.Player.Inventory.Bag.IndexOf(stack), rv = m.Player.Character.InventoryContainer, rx = 0, ry = vault, rz = 1 });
        var bank = m.Player.Map!.Objects.Values.OfType<Building>().First(b => b.Template.Kind == "bankbuilding");
        f.Call(m, new UnRegisterFromObjectRequest { ahc = bank.Id });
    }

    private static void Services()
    {
        using var f = new Fixture();
        _ = Client.Clusters!.Get("1001");
        _ = Client.Clusters!.Get("0000");
        var (anna, h, surface) = Built(f, ("T5_WOOD", 5), ("T5_ORE", 5));
        Check(f.World.Hideouts.VaultLocation(anna.Player.Map!) is null, "the zone's own maps: no hideout vault");
        Through(f, anna, surface.Id);
        var inside = f.Rejoin(anna.Id);
        Check(f.World.Hideouts.VaultLocation(inside.Player.Map!) == h.Instance && !inside.Player.Inventory.Bag.Items.Any(i => i.Template.Name == HideoutData.Kit),
              "inside: the bank's vault location is the instance id (Hideouts:BankVault Own); the used kit stayed gone over the cluster change");

        var near = f.World.Hideouts.NearestCityVault(Zone);
        Check(Client.Clusters!.World.Find(near)?.Type.StartsWith("PLAYERCITY_", StringComparison.Ordinal) == true, $"BankVault City would open {near}, the nearest city's vault");

        // A deposit in the hideout's bank.
        var (before, vault) = OpenBank(f, inside);
        Deposit(f, inside, vault, "T5_WOOD");
        var c = inside.Player.Character;
        Check(before.Count == 0 && c.Vaults.Keys.Any(k => k.StartsWith(h.Instance + "|", StringComparison.Ordinal)) && !c.Vaults.Keys.Any(k => k.StartsWith("1001|", StringComparison.Ordinal))
              && !inside.Player.Inventory.Bag.Items.Any(i => i.Template.Name == "T5_WOOD"),
              $"T5_WOOD deposited in the hideout's own vault (vaults {string.Join(", ", c.Vaults.Keys)})");

        // The Lymhurst bank does not show it; a deposit there stays there.
        var bankOf1001 = Client.Clusters!.Get("1001").Buildings.First(b => b.Template.Kind == "bankbuilding");
        f.Tele(inside, "1001", bankOf1001.Position + new Vector2(2, 0));
        var city = f.Rejoin(anna.Id);
        var (cityItems, cityVault) = OpenBank(f, city);
        Deposit(f, city, cityVault, "T5_ORE");
        Check(cityItems.Count == 0 && city.Player.Character.Vaults.Keys.Any(k => k.StartsWith("1001|", StringComparison.Ordinal)),
              $"the Lymhurst bank: empty (got {string.Join(", ", cityItems)}), the ore deposited there");
        lock (f.World.Sync) f.World.Hideouts.Enter(city.Player, h, "test");
        var back = f.Rejoin(anna.Id);
        var (again, _) = OpenBank(f, back);
        Check(again.SequenceEqual(["T5_WOOD"]), $"back in the hideout: only its own T5_WOOD (got {string.Join(", ", again)})");

        // Make Home: UpdateHome {1 "", 2 the instance}; the next Join has key 67.
        var mgmt = back.Player.Map!.Objects.Values.OfType<HideoutManagement>().Single();
        f.Move(back, mgmt.Position + new Vector2(2, 0));
        var from = back.Peer.Events.Count;
        var made = f.Call(back, new MakeHomeRequest { vq = mgmt.Id });
        var update = Of(back.Peer, EventCodes.UpdateHome, from).FirstOrDefault();
        Check(made is { ReturnCode: 0 } && update is not null && L(update, 0) == back.Player.Id && update[1] is "" && update[2] is string home && home == h.Instance
              && f.World.Hideouts.HomeOf(anna.Id) == h, $"MakeHome: RC {made?.ReturnCode}, UpdateHome {{0 player, 1 \"\", 2 the instance}}");

        // Respawn: a death in 2305 respawns inside the home hideout.
        lock (f.World.Sync) f.World.Hideouts.Leave(back.Player, h, "test");
        var zone = f.Rejoin(anna.Id);
        var join = new JoinResponse();
        AlbionSerializer.FillFields(join, zone.Peer.Responses[0].Parameters);
        Check(join.a95 == h.Instance, $"Join key 67 = the home hideout (got '{join.a95}')");
        var pushes = zone.Peer.Responses.Count;
        lock (f.World.Sync)
        {
            Death.Kill(zone.Player, null, 0, "test", Death.Cause.NoLoss, false, f.World.GameNow);
            Death.Respawn(zone.Player, "test");
        }
        var respawn = zone.Peer.Responses.Skip(pushes).LastOrDefault();
        Check(respawn?.Parameters[0] is string rid && rid == h.Instance && respawn.Parameters[1] is "HIDEOUT-0001a",
              $"the respawn goes into the home hideout (got {respawn?.Parameters.GetValueOrDefault((byte)0)})");

        // Journey Back: from Thetford's travel point, the window's home entry (kind 1) leads into the hideout.
        var revived = f.Rejoin(anna.Id);
        Check(revived.Player.Map!.Cluster == h.Instance, "joined inside after the respawn");
        f.Tele(revived, "0000", Client.Clusters!.Get("0000").Travelpoints.First().SpawnPosition ?? Vector2.Zero);
        var thetford = f.Rejoin(anna.Id);
        var tp = thetford.Player.Map!.Objects.Values.OfType<Travelpoint>().OrderBy(t => Vector2.Distance(t.Position, thetford.Player.PositionAt(DateTime.UtcNow))).First();
        pushes = thetford.Peer.Responses.Count;
        f.Call(thetford, new BuyJourneyRequest { df = tp.Id, dg = "", dh = 1, dj = 0 });
        var journey = thetford.Peer.Responses.Skip(pushes).ToList();
        Check(journey is [{ ReturnCode: 0 }, var hp] && hp.Parameters[0] is string jid && jid == h.Instance && hp.Parameters[1] is "HIDEOUT-0001a" && hp.Parameters[2] is "Wolves",
              $"Journey Back home (BuyJourney kind 1): RC 0, the push into the hideout (got {string.Join(" / ", journey.Select(r => r.ReturnCode))})");

        // Leave Home: UpdateHome back to "", no home any more.
        var home2 = f.Rejoin(anna.Id);
        var mgmt2 = home2.Player.Map!.Objects.Values.OfType<HideoutManagement>().Single();
        from = home2.Peer.Events.Count;
        var left = f.Call(home2, new LeaveHomeRequest { th = mgmt2.Id });
        Check(left is { ReturnCode: 0 } && Of(home2.Peer, EventCodes.UpdateHome, from).Any(p => p[2] is "") && f.World.Hideouts.HomeOf(anna.Id) is null,
              "LeaveHome: RC 0, UpdateHome to \"\", no home");

        // The access preset: a stranger may not change it; the founder makes it public (AccessStatus again).
        var bert = f.Join("Bert", surface.Position + new Vector2(5, 0));
        var refused = f.Call(bert, new AccessRightsChangeSettingsRequest { d = surface.Id, g = 2 });
        var seen = bert.Peer.Events.Count;
        string? set;
        lock (f.World.Sync) set = f.World.Hideouts.SetAccess(home2.Player, mgmt2.Id, 2, null, null, out _);
        Check(refused is { ReturnCode: 1 } && set is null && h.Access == HideoutAccess.Public && Of(bert.Peer, EventCodes.AccessStatus, seen).Any(p => L(p, 0) == surface.Id && L(p, 1) == 2),
              "a stranger's change refused (RC 1); the founder makes it public: the surface object's AccessStatus again with preset 2");
    }

    private static void Upkeep()
    {
        using var f = new Fixture();
        var food = Client.Items.All.First(i => Hideouts.Nutrition(i) is >= 400 and < 2000 && i.CanCreate && i.MaxStackSize >= 50);
        var per = Hideouts.Nutrition(food);
        var (anna, h, surface) = Built(f, (food.Name, 50), ("UNIQUE_GVGTOKEN_GENERIC", 1000), ("T1_WOOD", 1250), ("T1_ROCK", 125), ("T6_STONEBLOCK", 500));
        var bert = f.Join("Bert", surface.Position + new Vector2(8, 0));
        Through(f, anna, surface.Id);
        var inside = f.Rejoin(anna.Id);
        var mgmt = inside.Player.Map!.Objects.Values.OfType<HideoutManagement>().Single();

        // Food: 10 items into the store (425 with the new food).
        var stack = inside.Player.Inventory.Bag.Items.First(i => i.Template == food);
        var from = inside.Peer.Events.Count;
        var fed = f.Call(inside, new HideoutFillNutritionRequest { p4 = mgmt.Id, p5 = stack.Id, p6 = 10 });
        var update = Of(inside.Peer, EventCodes.HideoutManagementUpdate, from).LastOrDefault();
        Check(fed is { ReturnCode: 0 } && Math.Abs(h.Nutrition - (15_000 + 10 * per)) < 0.01 && stack.Count == 40 && update is not null
              && L(update, 9) == (long)Math.Round(h.Nutrition * Pricing.A7m),
              $"10 x {food.Name} ({per} each): food {h.Nutrition}, 40 left, 425 key 9 the food a7m");
        var wood = inside.Player.Inventory.Bag.Items.First(i => i.Template.Name == "T1_WOOD");
        Check(f.Call(inside, new HideoutFillNutritionRequest { p4 = mgmt.Id, p5 = wood.Id, p6 = 1 }) is { ReturnCode: 1 } && wood.Count > 0, "wood is no food: RC 1");

        // The daily upkeep at the zone's next prime time: 15000 eaten; a day without enough food costs a shield, a fed day gives it back.
        var before = h.Nutrition;
        var next = f.World.Hideouts.NextPrimeTime(Zone, new DateTime(f.World.GameNow, DateTimeKind.Utc));
        f.Jump(next - new DateTime(f.World.GameNow, DateTimeKind.Utc) + TimeSpan.FromSeconds(1));
        Check(Math.Abs(h.Nutrition - (before - 15_000)) < 0.01 && h.Shields == 2, $"the prime time: 15000 eaten (food {before} -> {h.Nutrition}), 2 shields");
        f.Jump(TimeSpan.FromDays(1));
        Check(h.Nutrition == 0 && h.Shields == 1, $"a day with too little food: none left, a shield lost (got {h.Nutrition}, {h.Shields})");
        f.Call(inside, new HideoutFillNutritionRequest { p4 = mgmt.Id, p5 = stack.Id, p6 = 40 });
        var stocked = h.Nutrition;
        f.Jump(TimeSpan.FromDays(1));
        Check(stocked >= 15_000 && h.Shields == 2 && Math.Abs(h.Nutrition - (stocked - 15_000)) < 0.01, $"fed again: the day's food eaten, the shield back (got {h.Shields})");

        // Tribute: a stranger may not set it; the founder sets 1000 a day, paid from the guild account to 2305's territory owner.
        var bears = f.Guild(bert, "Bears");
        Check(f.Call(bert, new HideoutSetTributeRequest { qa = surface.Id, qb = 5 * Pricing.A7m }) is { ReturnCode: Hideouts.TributeNoPermission }, "another guild's tribute change: 16000");
        Check(f.Call(inside, new HideoutSetTributeRequest { qa = mgmt.Id, qb = 1000 * Pricing.A7m }) is { ReturnCode: 0 } && h.Tribute == 1000 * Pricing.A7m, "the founder sets 1000 silver a day");
        var wolves = f.World.Guilds.ById(h.Guild)!;
        Check(f.World.Hideouts.PayTribute(h) == 0, "no owner of the zone's territory: nothing paid");
        lock (f.World.Sync)
        {
            f.World.Guilds.Deposit(wolves, 5000 * Pricing.A7m);
            f.World.Territories.Gm(f.World.Territories.All.First(t => t.Cluster.Id == Zone), "owner", bears);
        }
        long paid;
        lock (f.World.Sync) paid = f.World.Hideouts.PayTribute(h);
        Check(paid == 1000 * Pricing.A7m && wolves.SilverA7m == 4000 * Pricing.A7m && bears.SilverA7m == 1000 * Pricing.A7m,
              $"Bears own the territory: 1000 silver from Wolves' account to Bears' (Wolves {wolves.SilverA7m / Pricing.A7m}, Bears {bears.SilverA7m / Pricing.A7m})");

        // Upgrade to level b: refused for the stranger; the founder pays 20 M and the resources; 48 h; nobody enters meanwhile; then level b.
        Check(f.Call(bert, new UpgradeHideoutRequest { ah1 = surface.Id, ah2 = 20_000_000 * Pricing.A7m }) is { ReturnCode: 4 }, "a stranger's upgrade: NotInOwnersGuild (4)");
        Check(f.Call(inside, new UpgradeHideoutRequest { ah1 = mgmt.Id, ah2 = 20_000_000 * Pricing.A7m }) is { ReturnCode: 6 } && !h.Upgrading, "1 M silver: NotEnoughSilver (6)");
        inside.Player.Character.Silver = 25_000_000 * Pricing.A7m;
        var up = f.Call(inside, new UpgradeHideoutRequest { ah1 = mgmt.Id, ah2 = 20_000_000 * Pricing.A7m });
        Check(up is { ReturnCode: 0 } && h.Upgrading && inside.Player.Character.Silver == 5_000_000 * Pricing.A7m
              && !inside.Player.Inventory.Bag.Items.Any(i => i.Template.Name is "UNIQUE_GVGTOKEN_GENERIC" or "T1_WOOD" or "T1_ROCK" or "T6_STONEBLOCK")
              && f.World.Hideouts.CanVisit(h, anna.Id) is { } busy && busy.Contains("UPGRADING"),
              $"the upgrade: RC {up?.ReturnCode}, 20 M and the resources taken, nobody enters while it runs");
        Check(f.Call(inside, new UpgradeHideoutRequest { ah1 = mgmt.Id, ah2 = 0 }) is { ReturnCode: 2 }, "again: AlreadyUpgrading (2)");
        lock (f.World.Sync) h.Nutrition = 100_000; // fed for the two days of the upgrade
        f.Jump(TimeSpan.FromSeconds(172_800));
        Check(h is { Level: 1, Upgrading: false, Shields: 4 } && f.World.Hideouts.WorldOf(h.Instance) == "HIDEOUT-0001b" && inside.Session.Status == SessionStatus.Transferring
              && inside.Player.Character.Cluster == Zone,
              $"48 h later: level b (4 shields, HIDEOUT-0001b), the player inside put out to {inside.Player.Character.Cluster}");
        _ = Client.Clusters!.Get("HIDEOUT-0001b");
        var outside = f.Rejoin(anna.Id);
        Through(f, outside, surface.Id);
        var b = f.Rejoin(anna.Id);
        Check(b.Player.Map!.Template?.Id == "HIDEOUT-0001b" && b.Player.Map.Objects.Values.OfType<Building>().Any(x => x.Template.Kind == "marketplacebuilding"),
              "the next entry builds HIDEOUT-0001b, with its own market");

        // Saved: level, food, shields and tribute after a restart.
        var food1 = h.Nutrition;
        f.Restart();
        var saved = f.World.Hideouts.Find(h.Id);
        Check(saved is { Level: 1, Shields: 4, Upgrading: false } && saved.Tribute == 1000 * Pricing.A7m && Math.Abs(saved.Nutrition - food1) < 0.01 && saved.LastUpkeep == h.LastUpkeep,
              "restart: level b, its shields, the food, the tribute and the last upkeep kept");

        // Upkeep false: nothing is eaten.
        using var off = new Fixture(new ServerConfig { Hideouts = new HideoutOptions { Upkeep = false } });
        var (dora, dh, _) = Built(off);
        var food0 = dh.Nutrition;
        off.Jump(TimeSpan.FromDays(2));
        Check(dh.Nutrition == food0 && dh.Shields == 2, "Hideouts:Upkeep false: two days, nothing eaten");
    }
}
