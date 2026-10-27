using System.Numerics;
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
using Microsoft.Data.Sqlite;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The watchtower territories, phase P3 (analysis/work/stageP/territories/SPEC.md §3.1, §3.5): claim, give up, access, food, the guild's list and
/// the season schedule, on Wetgrave Bog Coombe (0305's monolith at 125/-135).
/// </summary>
public static class TerritoryClaimTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("territory claim: refused outside prime time / Tower Guardian / fame / silver / rights (12085), the 15 s channel (340, 342), cost charged, owner in 109, 138 lists it; pve: the NPC holder's guards first, any time, 200000", Claim),
        ("territory food (eaten per 22.2 s, 223 feeds, empty = lost), access (116), give up (115, 137), hideout tribute to the new owner", Upkeep),
        ("territory season schedule: resetterritories frees a territory and castle held from before it (not one claimed after); SeasonSchedule off keeps them; restart and v40 -> v41 migration", Schedule),
        ("territory scoring: base points x scoremultiplier per tick (TerritoryHold), the prime-time tick paid again (TerritoryPrimeTime), 447 shows them; scoring window; tower power decay level by level (433)", Scoring),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    /// <summary>2026-10-17 00:00 UTC: day 36 of GVG_SEASON_34 (Europe), in its second claimable window, after the day-29 Tower Guardian.</summary>
    private static readonly DateTime Saturday = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Coombe = Guid.Parse("44b03c90-a6b5-4a05-bd5e-f27387856def"), Tor = Guid.Parse("47306493-1904-48d8-a77e-f9fd5cfd309e");

    private static readonly Vector2 Tower = new(125, -135);

    private sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    /// <summary>One world on a test database at 0305; the game time is Saturday + <see cref="Shift"/> + the clock.</summary>
    private sealed class Fixture : IDisposable
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
            _ = Client.Clusters!.Get("0305");
            World = Make();
        }

        private World Make() => new(Config, Characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = Clock, Clusters = Client.Clusters, MapSeed = 3, GameTicks = () => Saturday.Ticks + Shift.Ticks + Clock.Now.Ticks,
            GvgSeasons = GvgSeasonData.Load(ClientGameDataDir(), "europe"), MobAi = false,
        };

        public void Restart()
        {
            World.Dispose();
            World = Make();
        }

        public DateTime Now => new(World.GameNow, DateTimeKind.Utc);

        public TerritoryState State => World.Territories.Find(Coombe.ToString())!;

        public MonolithObject Monolith => World.Maps.Find("0305")!.Objects.Values.OfType<MonolithObject>().Single();

        /// <summary>The game time of day of 0305's prime time start (+ <paramref name="into"/>) on the next day it comes.</summary>
        public void ToPrime(TimeSpan into)
        {
            var start = World.Territories.Rules.TimeRegions[State.TimeRegion].Start + into;
            var now = Now;
            var at = now.Date + start;
            while (at <= now) at = at.AddDays(1);
            Jump(at - now);
        }

        public Member Join(string name, Vector2 at, params (string Item, int Count)[] items)
        {
            var c = StarterKit.Default.NewCharacter(name + Guid.NewGuid().ToString("N")[..6]);
            (c.Cluster, c.PositionX, c.PositionY) = ("0305", at.X, at.Y);
            foreach (var (item, count) in items) c.Inventory.Add(new InventoryItem(item, count));
            Check(Characters.Create(c), $"character {name} created");
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
            var map = World.Maps.Find("0305")!;
            for (var i = 0; i < 100 && map.Template is null; i++)
            {
                Thread.Sleep(20);
                Run(0.1);
            }
            Run(0.5);
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

        /// <summary>The game time jumps by <paramref name="by"/> (the world clock does not), then two ticks.</summary>
        public void Jump(TimeSpan by)
        {
            Shift += by;
            Run(0.5);
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

    private static IEnumerable<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) =>
        peer.Events.Skip(from).Where(e => Code(e) == (int)code).Select(e => e.Parameters);

    /// <summary>The guild's fame (its member's PvE fame in the Member Logs, all time).</summary>
    private static void Fame(Guild g, Guid member, long fame) =>
        g.Progress.Logs[member] = new GuildMemberLog { All = { [GuildMemberLog.PveFame] = fame * Pricing.A7m } };

    private static OperationResponse? Claim(Fixture f, Member m) =>
        f.Call(m, new TerritoryClaimStartRequest { wf = f.World.GameNow, agu = f.Monolith.Id, agv = Coombe.ToString() });

    private static void Claim()
    {
        using var f = new Fixture();
        f.Shift = TimeSpan.FromHours(3); // 03:00 UTC: outside every US_* prime time
        var anna = f.Join("Anna", Tower + new Vector2(5, 0));
        var bert = f.Join("Bert", Tower + new Vector2(0, 5));
        var wolves = f.Guild(anna, "Wolves");
        f.Into(anna, bert);
        var s = f.State;
        Check(s is { Owned: false, NpcHeld: false } && s.Invader is { Dead: false, Removed: false } && f.World.Territories.InvaderBlocks(s, f.Now),
              "official: Coombe unowned, the season's Tower Guardian stands at the monolith (spawninvader day 29)");
        var refused = Claim(f, anna);
        Check(refused is { ReturnCode: Territories.ClaimError } && f.World.Territories.CanClaim(anna.Player, s, f.Now)!.Contains("PRIME_TIME"),
              $"03:00: RC 12085, only in the zone's prime time (got {refused?.ReturnCode})");
        f.ToPrime(TimeSpan.FromMinutes(5));
        Check(f.World.Territories.CanClaim(anna.Player, s, f.Now) is { } inv && inv.Contains("INVADER"), "in prime time: the Tower Guardian blocks the claim");
        lock (f.World.Sync) SpawnManager.Despawn(s.Invader!, f.World, respawn: false);
        f.Run(0.5);
        Check(s.Invader is null && !f.World.Territories.InvaderBlocks(s, f.Now), "the Tower Guardian killed: no block");
        Check(f.World.Territories.CanClaim(anna.Player, s, f.Now) is { } fame && fame.Contains("GUILD_FAME"), "guild fame 0 < 1000000: refused");
        Fame(wolves, anna.Id, 1_000_000);
        Check(f.World.Territories.CanClaim(anna.Player, s, f.Now) is { } silver && silver.Contains("NOT_ENOUGH_SILVER"), "an empty guild account: refused (2000000 needed)");
        lock (f.World.Sync) f.World.Guilds.Deposit(wolves, 2_500_000 * Pricing.A7m);
        Check(Claim(f, bert) is { ReturnCode: Territories.ClaimError } && f.World.Territories.CanClaim(bert.Player, s, f.Now)!.Contains("NO_RIGHTS"),
              "a plain member lacks CLAIM_TERRITORY: refused");
        var from = anna.Peer.Events.Count;
        var bertFrom = bert.Peer.Events.Count;
        Check(Claim(f, anna) is { ReturnCode: 0 }, "the master in prime time with fame and silver: the claim starts (RC 0)");
        var start = Of(bert.Peer, EventCodes.TerritoryClaimStart, bertFrom).SingleOrDefault();
        Check(start is not null && Convert.ToInt64(start[0]) == anna.Player.Id && Convert.ToInt64(start[3]) == f.Monolith.Id
              && Convert.ToInt64(start[2]) - Convert.ToInt64(start[1]) == TimeSpan.FromSeconds(15).Ticks,
              "340 to those in view (the claimer runs its own bar): {0 the claimer, 1/2 start and end 15 s apart, 3 the monolith}");
        f.Run(10);
        Check(!s.Owned, "after 10 s: not yet");
        f.Run(5.5);
        var monolith = Of(bert.Peer, EventCodes.NewMonolithObject, bertFrom).LastOrDefault();
        Check(s.Owner == wolves.Id && wolves.SilverA7m == 500_000 * Pricing.A7m && Of(anna.Peer, EventCodes.TerritoryClaimFinished, from).Any()
              && monolith is not null && monolith[3] is byte[] owner && new Guid(owner) == wolves.Id && monolith[4] is "Wolves"
              && Of(bert.Peer, EventCodes.GuildMemberTerritoryUpdate, bertFrom).Any(e => e[0] is string t && t == Coombe.ToString() && e[1] is true),
              $"15 s later: Wolves hold Coombe, 2000000 silver paid ({wolves.SilverA7m / Pricing.A7m} left), 342, 109 again with the owner, 137 to the members");
        var mine = f.Call(bert, new GetMyTerritoriesRequest(), 9);
        Check(mine is { ReturnCode: 0 } && mine.Parameters[0] is string[] { Length: 1 } ids && ids[0] == Coombe.ToString()
              && mine.Parameters[1] is long[] { Length: 1 } foods && Math.Abs(foods[0] - 4000 * Pricing.A7m) < Pricing.A7m
              && mine.Parameters[3] is long[] { Length: 1 } && !mine.Parameters.ContainsKey(8),
              "138: the guild's territory, key 1 its food 4000 (a7m), no hideout keys");
        Check(f.World.Territories.CanClaim(anna.Player, s, f.Now) is { } held && held.Contains("ALREADY_CLAIMED"), "held: no second claim");

        // A claim breaks off when the claimer moves.
        using var p = new Fixture(new ServerConfig { Territories = ServerConfig.PveDefaults.Territories });
        var pia = p.Join("Pia", Tower + new Vector2(5, 0));
        var bears = p.Guild(pia, "Bears");
        var ps = p.State;
        Check(ps is { NpcHeld: true, OwnerName: Territories.NpcGuildName } && ps.NpcGuards.Count > 0 && ps.Invader is null && p.World.Territories.CanClaim(pia.Player, ps, p.Now) is { } npc
              && npc.Contains(Territories.NpcGuildName),
              $"pve: the {Territories.NpcGuildName} hold Coombe with {ps.NpcGuards.Count} guard(s); the claim waits for them (no Tower Guardian: schedule off)");
        lock (p.World.Sync)
            foreach (var g in ps.NpcGuards.ToList()) SpawnManager.Despawn(g, p.World, respawn: false);
        p.Run(0.5);
        Check(ps is { Owned: false, NpcBeatenAt: not null }, "the guards dead: Coombe is free");
        p.Run(120.5);
        lock (p.World.Sync) p.World.Guilds.Deposit(bears, 300_000 * Pricing.A7m);
        Check(Claim(p, pia) is { ReturnCode: 0 }, "pve at 03:00 (no prime time rule), no fame: the claim starts");
        lock (p.World.Sync) pia.Player.Teleport(Tower + new Vector2(9, 0), 0, p.Now);
        p.Run(0.5);
        Check(Of(pia.Peer, EventCodes.TerritoryClaimCancel).Any(e => Convert.ToInt32(e[1]) == Territories.ClaimInterrupted) && !ps.Owned, "moved: 341 (12080), no claim");
        lock (p.World.Sync) pia.Player.Teleport(Tower + new Vector2(5, 0), 0, p.Now);
        Check(Claim(p, pia) is { ReturnCode: 0 }, "again");
        p.Run(15.5);
        Check(ps.Owner == bears.Id && bears.SilverA7m == 100_000 * Pricing.A7m && ps.NpcGuards.Count == 0, "pve: Bears hold it for 200000 silver");
        p.Jump(TimeSpan.FromSeconds(3601));
        Check(ps.Owner == bears.Id, "the NPC holder does not come back to a held territory");
    }

    private static void Upkeep()
    {
        using var f = new Fixture();
        f.Shift = TimeSpan.FromHours(3);
        var food = Client.Items.All.First(i => Hideouts.Nutrition(i) is >= 100 and < 2000 && i.CanCreate && i.MaxStackSize >= 20);
        var per = Hideouts.Nutrition(food);
        var anna = f.Join("Anna", Tower + new Vector2(5, 0));
        var bert = f.Join("Bert", Tower + new Vector2(0, 5), (food.Name, 20));
        var wolves = f.Guild(anna, "Wolves");
        f.Into(anna, bert);
        var bears =f.Guild(f.Join("Cora", Tower + new Vector2(-5, 0)), "Bears");
        var s = f.State;
        var hideout = new Hideout(Guid.NewGuid(), bears.Id, "0305", Vector2.Zero, 0, 0) { Tribute = 1000 * Pricing.A7m };
        lock (f.World.Sync) f.World.Guilds.Deposit(bears, 5000 * Pricing.A7m);
        Check(f.World.Hideouts.PayTribute(hideout) == 0, "an unowned zone: no tribute");
        lock (f.World.Sync) f.World.Territories.SetOwner(s, wolves, f.Now);
        long paid;
        lock (f.World.Sync) paid = f.World.Hideouts.PayTribute(hideout);
        Check(paid == 1000 * Pricing.A7m && wolves.SilverA7m == 1000 * Pricing.A7m, "Wolves hold the territory: the Bears' hideout tribute goes to them");

        // Food: 4000 at the claim, one per 22.2 s.
        f.Jump(TimeSpan.FromHours(1));
        var left = f.World.Territories.NutritionAt(s, f.Now);
        Check(Math.Abs(left - (4000 - 3600.5 / 22.222222)) < 2, $"an hour: {left:0} food left (4000 - 162)");
        var stack = bert.Player.Inventory.Bag.Items.First(i => i.Template == food);
        var before = f.World.Territories.NutritionAt(s, f.Now);
        var fed = f.Call(bert, new TerritoryFillNutritionRequest { agw = Coombe.ToString(), agx = stack.Id, agy = 10 });
        Check(fed is { ReturnCode: 0 } && stack.Count == 10 && Math.Abs(f.World.Territories.NutritionAt(s, f.Now) - (before + 10 * per)) < 1,
              $"223: 10 x {food.Name} ({per} each) fed by a member at the tower");
        lock (f.World.Sync) bert.Player.Teleport(Tower + new Vector2(60, 0), 0, f.Now);
        Check(f.Call(bert, new TerritoryFillNutritionRequest { agw = Coombe.ToString(), agx = stack.Id, agy = 1 }) is { ReturnCode: Territories.Refused } && stack.Count == 10,
              "60 m from the tower: refused (RC 1)");

        // Access: a plain member may not, the master may.
        Check(f.Call(bert, new ChangeTerritoryAccessRightsRequest { e5 = Coombe.ToString(), e6 = 1 }) is { ReturnCode: Territories.Refused } && s.Access == 0,
              "116 by a plain member: refused");
        Check(f.Call(anna, new ChangeTerritoryAccessRightsRequest { e5 = Coombe.ToString(), e6 = 1 }) is { ReturnCode: 0 } && s.Access == 1, "116 by the master: access 1");

        // Unfed: empty = lost.
        var from = bert.Peer.Events.Count;
        f.Jump(TimeSpan.FromDays(2));
        Check(!s.Owned && Of(bert.Peer, EventCodes.GuildMemberTerritoryUpdate, from).Any(e => e.GetValueOrDefault((byte)1) is not true),
              "two days unfed: the food ran out, the territory is lost (137 false to the members)");

        // Give up: the master only.
        lock (f.World.Sync) f.World.Territories.SetOwner(s, wolves, f.Now);
        Check(f.Call(bert, new GiveUpTerritoryRequest { o7 = Coombe.ToString() }) is { ReturnCode: Territories.Refused } && s.Owner == wolves.Id,
              "115 by a plain member: refused");
        Check(f.Call(anna, new GiveUpTerritoryRequest { o7 = Coombe.ToString() }) is { ReturnCode: 0 } && !s.Owned, "115 by the master: given up");
        lock (f.World.Sync) f.World.Territories.SetOwner(s, bears, f.Now);
        lock (f.World.Sync) paid = f.World.Hideouts.PayTribute(hideout);
        Check(paid == 0 && f.World.Territories.MonolithInfo(anna.Player, Coombe.ToString()) is { a1n: 4000 * Pricing.A7m, a1o: 1 },
              "the hideout's own guild holds the zone now: no tribute; 117 {0 food 4000, 1 level 1}");
    }

    private static void Schedule()
    {
        // Held since day 36; the day-57 11:00 resetterritories frees the territory and the castle (the season's territorytypes list CASTLE).
        using var f = new Fixture(new ServerConfig { Territories = new TerritoryOptions { Food = false } });
        f.Shift = TimeSpan.FromHours(3);
        var anna = f.Join("Anna", Tower + new Vector2(5, 0));
        var wolves = f.Guild(anna, "Wolves");
        var s = f.State;
        var tor = f.World.Territories.Find(Tor.ToString())!;
        lock (f.World.Sync)
        {
            f.World.Territories.SetOwner(s, wolves, f.Now);
            f.World.Territories.SetOwner(tor, wolves, f.Now);
            f.World.Territories.SetPower(s, 12_345, f.Now);
        }
        f.Restart();
        var again = f.World.Territories.Find(Coombe.ToString())!;
        Check(again.Owner == wolves.Id && again.Nutrition == 4000 && again.Power == 12_345 && f.World.Territories.Find(Tor.ToString())!.Owned,
              "a restart: owner, food and tower power of Coombe and the castle's owner kept");
        f.Jump(new DateTime(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc) - f.Now); // day 57 10:00
        Check(again.Owned, "day 57 10:00: still held");
        f.Jump(TimeSpan.FromHours(1.5));
        Check(!again.Owned && !f.World.Territories.Find(Tor.ToString())!.Owned, "day 57 11:00 resetterritories: the territory and the castle are free");
        lock (f.World.Sync) f.World.Territories.SetOwner(again, wolves, f.Now);
        f.Jump(TimeSpan.FromHours(1));
        Check(again.Owner == wolves.Id, "claimed after the reset: kept");

        using var off = new Fixture(new ServerConfig { Territories = new TerritoryOptions { Food = false, SeasonSchedule = false } });
        off.Shift = TimeSpan.FromHours(3);
        var bo = off.Join("Bo", Tower + new Vector2(5, 0));
        var bears = off.Guild(bo, "Bears");
        lock (off.World.Sync) off.World.Territories.SetOwner(off.State, bears, off.Now);
        off.Jump(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc) - off.Now);
        Check(off.State.Owner == bears.Id && off.State.Invader is null, "SeasonSchedule off: no reset, no Tower Guardian");

        var dir = NewDir();
        var path = Path.Combine(dir, "save.db");
        try
        {
            using (new SqliteCharacterRepository(path)) { }
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "ALTER TABLE progression DROP COLUMN tracked; DROP TABLE territory_state; PRAGMA user_version=40;";
                cmd.ExecuteNonQuery();
            }
            using (var migrated = new SqliteCharacterRepository(path))
                Check(migrated.LoadTerritoryOwners().Count == 0, "v40 save migrated: territory_state added");
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 43, "schema version 43 (including Destiny Board tracking)");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Scoring()
    {
        using var f = new Fixture(new ServerConfig { Territories = new TerritoryOptions { Food = false } });
        f.Shift = TimeSpan.FromHours(3);
        var anna = f.Join("Anna", Tower + new Vector2(5, 0));
        var wolves = f.Guild(anna, "Wolves");
        var s = f.State;
        var t = f.World.Territories;
        var basePoints = t.BasePoints(s);
        Check(basePoints > 0 && t.ScoreMultiplier(s, f.Now) == 2 && t.ScoreMultiplier(s, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)) == 1
              && t.Scoring(s, f.Now) && !t.Scoring(s, new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc)),
              $"0305 ({s.Cluster.Type}): {basePoints} base points; multiplier 2 after day 29 11:00 (1 on day 20); scoring on day 36, off on day 29 12:00");
        lock (f.World.Sync) t.SetOwner(s, wolves, f.Now);
        f.Run(0.25);
        var first = s.LastScore!.Value;
        GuildSeasonState State() => GuildGoals.StateOf(wolves, f.World.GuildGoals.Season);
        f.Jump(TimeSpan.FromMinutes(61));
        Check(!t.InPrimeTime(s, first.AddHours(1)) && State().Activity.GetValueOrDefault(GuildGoals.TerritoryHold) == basePoints * 2
              && State().Activity.GetValueOrDefault(GuildGoals.TerritoryPrimeTime) == 0,
              $"one tick outside prime time: {basePoints} x 2 TerritoryHold, no prime-time points");
        f.ToPrime(TimeSpan.FromMinutes(61)); // just past the zone's prime hour: exactly one tick fell into it
        var ticks = (long)((s.LastScore!.Value - first) / TimeSpan.FromHours(1));
        Check(State().Activity.GetValueOrDefault(GuildGoals.TerritoryHold) == ticks * basePoints * 2
              && State().Activity.GetValueOrDefault(GuildGoals.TerritoryPrimeTime) == basePoints * 2 && State().Points >= (ticks + 1) * basePoints * 2,
              $"{ticks} ticks: TerritoryHold {ticks} x {basePoints * 2}, the prime-time tick once more as TerritoryPrimeTime");
        var by = f.Call(anna, new GetGvgSeasonContributionByActivityRequest { nl = wolves.Id }, 11);
        Check(by is { ReturnCode: 0 } && by.Parameters[4] is string[] acts && acts.Contains(GuildGoals.TerritoryHold) && acts.Contains(GuildGoals.TerritoryPrimeTime),
              "447: the guild season window lists TerritoryHold and TerritoryPrimeTime");
        Check(f.Call(anna, new GetTerritorySeasonPointsRequest { o5 = Coombe.ToString() }, 12) is { } sp && sp.Parameters.GetValueOrDefault((byte)0) is true,
              "120: key 0 true for the owner guild");

        // Tower power: level 3 from 9613 + 14996; a day later 1000 points went at level 3's rate, the rest at level 2's.
        Check(t.LevelOf(9612) == 1 && t.LevelOf(9613) == 2 && t.LevelOf(24_609) == 3 && t.LevelOf(1e9) == 8, "levels: the pools stacked (9613 -> 2, 24609 -> 3, at most 8)");
        var from = anna.Peer.Events.Count;
        lock (f.World.Sync) t.SetPower(s, 25_609, f.Now);
        var update = Of(anna.Peer, EventCodes.TowerPowerPointUpdate, from).SingleOrDefault();
        var day = f.Now.AddDays(1);
        var expected = 24_609 - (1 - 1000 / (22_744 / 2.8)) * (14_996 / 2.4);
        Check(update is not null && Convert.ToInt64(update[0]) == 25_609 * Pricing.A7m && Math.Abs(t.PowerAt(s, day) - expected) < 1 && t.TowerLevelAt(s, day) == 2
              && t.PowerAt(s, f.Now.AddDays(30)) == 0,
              $"433 with the points; a day later {t.PowerAt(s, day):0} (expected {expected:0}), level 2; a month later 0");
    }
}
