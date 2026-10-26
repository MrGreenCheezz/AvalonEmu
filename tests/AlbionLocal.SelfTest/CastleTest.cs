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
using Microsoft.Data.Sqlite;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The castles, phase P5 of the territories (analysis/work/stageP/territories/SPEC.md §3.4): the fight's phases, the capture zone, the saved
/// owner and the chests, on Wetgrave Bog Tor (0305's MEDIUMCASTLE at 174/166).
/// </summary>
public static class CastleTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("castles: data (guards of the tile, capture zone, treasuredistribution schedule), phases 0 -> 1 -> 4 -> 6 -> 7 as the defenders die, guards x GuardScale", Phases),
        ("castles: a guild alone in the capture zone claims the castle (owner in 112, 417 7 -> 0), another guild in it pauses the claim", Claim),
        ("castles: the owner survives a restart; v38 -> v39 migration adds territory_owners", Persistence),
        ("castles: a chest at a scheduled hour for the owner only (official) or anyone (pve), rarity in 112 key 13, gone after 30 min", Chests),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    /// <summary>2026-10-17 12:00 UTC, a Saturday (a chest respawn; 18:00 is the weekend legendary).</summary>
    private static readonly DateTime Saturday = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Tor = Guid.Parse("47306493-1904-48d8-a77e-f9fd5cfd309e");

    private static readonly Vector2 Zone = new(174, 166);

    private sealed record Member(WorldSession Session, FakePeer Peer, Player Player)
    {
        public Guid Id => Player.Character.Id;
    }

    /// <summary>One world on a test database at 0305, the game time Saturday 12:00 + the clock + <see cref="Shift"/>.</summary>
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
            Config = config ?? new ServerConfig();
            Config = Config with { JoinByCharacterId = true, GuildCreateCost = 0, GuildRejoinCooldownMinutes = 0 };
            Characters = Repository(Dir);
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

        public Member Join(string name, Vector2 at)
        {
            _ = Client.Clusters!.Get("0305");
            var c = StarterKit.Default.NewCharacter(name + Guid.NewGuid().ToString("N")[..6]);
            (c.Cluster, c.PositionX, c.PositionY) = ("0305", at.X, at.Y);
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
            Run(0.2);
            return new Member(session, peer, session.Player!);
        }

        public Guild Guild(Member founder, string name)
        {
            Check(World.Guilds.Create(founder.Player, name, GuildLogo.Default, "", out _) == 0, $"guild {name} founded");
            return World.Guilds.Of(founder.Id)!;
        }

        public void Into(Member inviter, Member joiner) =>
            Check(World.Guilds.Invite(inviter.Player, joiner.Player.Character.Name, out _, out _) == 0 && World.Guilds.Accept(joiner.Player, out var g) == 0
                  && g is not null && Do(() => World.Guilds.AfterJoin(joiner.Player, g)), $"{joiner.Player.Character.Name} joins");

        public void Run(double seconds)
        {
            for (var i = 0; i < Math.Max(1, Math.Round(seconds * 4)); i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(250));
                lock (World.Sync) World.Update();
            }
        }

        /// <summary>The game time jumps by <paramref name="by"/> (the world clock does not: no keepalive or map timer runs), then one tick.</summary>
        public void Jump(TimeSpan by)
        {
            Shift += by;
            Run(0.25);
        }

        public CastleObject Castle => World.Maps.Find("0305")!.Objects.Values.OfType<CastleObject>().Single();

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

    private static void Move(Fixture f, Member m, Vector2 to)
    {
        lock (f.World.Sync) m.Player.Teleport(to, 0, new DateTime(f.World.GameNow, DateTimeKind.Utc));
    }

    private static IEnumerable<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) =>
        peer.Events.Skip(from).Where(e => Code(e) == (int)code).Select(e => e.Parameters);

    /// <summary>Takes every standing defender of the castle off (as .mob kill), then one tick.</summary>
    private static int Down(Fixture f)
    {
        var mobs = f.Castle.State.Fight.Mobs.Where(m => m is { Dead: false, Removed: false }).ToList();
        lock (f.World.Sync)
            foreach (var m in mobs) SpawnManager.Despawn(m, f.World, respawn: false);
        f.Run(0.25);
        return mobs.Count;
    }

    private static void Phases()
    {
        using var f = new Fixture();
        var a = f.Join("Anna", Zone + new Vector2(30, 0));
        var castle = f.Castle;
        var fight = castle.State.Fight;
        var guards = castle.Guards.GroupBy(g => g.GuardType).ToDictionary(g => g.Key, g => g.Count());
        Check(castle.State.Id == Tor && guards.GetValueOrDefault("BASIC") > 0 && guards.GetValueOrDefault("ELITE") > 0 && guards.GetValueOrDefault("LORD") == 1
              && castle.CaptureZone == castle.Position && castle.Guards.All(g => Vector2.Distance(g.Position, castle.Position) < 90),
              $"Wetgrave Bog Tor: guards of its tile in world coordinates ({string.Join(", ", guards.Select(kv => $"{kv.Key} {kv.Value}"))}), the zone at the castle");
        var schedule = f.World.Territories.Rules.ChestSchedule("CASTLE");
        Check(f.World.Territories.Rules.ChestSchedules.Count == 2 && schedule.Respawns[DayOfWeek.Monday].Count == 7 && schedule.Respawns[DayOfWeek.Monday][0] == TimeSpan.FromHours(10)
              && schedule.DrawAt(Saturday, 6) is { NoneWeight: 579, Chests.Count: 4 } && schedule.DrawAt(Saturday.AddHours(6), 6) is { NoneWeight: 0, Chests: [(var leg, _)] }
              && leg == "SPAWNPOINT_TREASURE_CASTLE_LEGENDARY" && f.World.Territories.Rules.GuardRespawnSeconds == 3600,
              "treasuredistribution: CASTLE every 2 h from 10:00 (7 a day), draw 240/120/40/21 + None 579, Saturday 18:00 the legendary; guards back after 3600 s");
        Check(fight.Phase == 0 && fight.Mobs.Count == guards["BASIC"] && fight.Mobs.All(m => m is { Dead: false, Removed: false } && Math.Abs(m.Health.Max - m.Template.HitPointsMax) < 1),
              $"phase 0: the {guards["BASIC"]} basic guards stand at full data health (GuardScale 1)");
        var from = a.Peer.Events.Count;
        Down(f);
        Check(fight.Phase == 1, $"a guard down: the fight starts (phase 1, got {fight.Phase})");
        f.Run(0.25);
        Check(fight.Phase == 4 && fight.Mobs.Count == guards["ELITE"] && fight.Mobs.All(m => m.Spawn.Position != default),
              $"all basic guards dead: the elites at once on a MEDIUMCASTLE (eliteguardarrivalinmin 0; phase 4, got {fight.Phase})");
        Down(f);
        Check(fight.Phase == 6 && fight.Mobs.Count == 1 && fight.Mobs[0].Template.Name == "MOB_CASTLE_LORD", $"the elites dead: the lord (phase 6, got {fight.Phase})");
        Down(f);
        Check(fight.Phase == 7 && fight.Mobs.Count == 0, $"the lord dead: the capture zone (phase 7, got {fight.Phase})");
        var phases = Of(a.Peer, EventCodes.CastlePhaseChanged, from).Select(p => (Convert.ToInt64(p[0]), Convert.ToInt32(p.GetValueOrDefault((byte)1) ?? 0), Convert.ToInt32(p[2]))).ToList();
        Check(phases.SequenceEqual([(castle.Id, 0, 1), (castle.Id, 1, 4), (castle.Id, 4, 6), (castle.Id, 6, 7)]),
              $"417 to the observer: 0->1, 1->4, 4->6, 6->7 (got {string.Join(" ", phases.Select(p => $"{p.Item2}->{p.Item3}"))})");
        f.Jump(TimeSpan.FromSeconds(301));
        Check(fight.Phase == 8 && fight.Mobs.Count == 1, $"the capture zone unclaimed for castlelordrespawntime (300 s): the lord is back (phase 8, got {fight.Phase})");
        f.Jump(TimeSpan.FromMinutes(30));
        Check(fight.Phase == 0 && fight.Mobs.Count == 0, $"30 min in a phase (castlephasetimeoutinmin): the attack failed, phase 0 (got {fight.Phase})");
        f.Run(0.5);
        Check(fight.Mobs.Count == guards["BASIC"], "after the failed attack the basic guards stand again");

        using var pve = new Fixture(new ServerConfig { Territories = ServerConfig.PveDefaults.Territories });
        pve.Join("Pia", Zone + new Vector2(30, 0));
        var pm = pve.Castle.State.Fight.Mobs;
        Check(pm.Count > 0 && pm.All(m => Math.Abs(m.Health.Max - m.Template.HitPointsMax * 0.6) < 1), "pve: the guards at 0.6 of their health (Castles:GuardScale)");
    }

    private static void Claim()
    {
        using var f = new Fixture();
        var a = f.Join("Anna", Zone);
        var b = f.Join("Bert", Zone + new Vector2(40, 0));
        var wolves = f.Guild(a, "Wolves");
        var bears = f.Guild(b, "Bears");
        var castle = f.Castle;
        var fight = castle.State.Fight;
        lock (f.World.Sync) f.World.Territories.Gm(castle.State, "capture", null);
        var from = a.Peer.Events.Count;
        f.Run(8);
        Check(fight.Phase == 7 && fight.Claimer == wolves.Id && fight.Progress is > 0.4 and < 0.6 && !castle.State.Owned,
              $"Wolves alone in the zone 8 s: half the claim (16 s; got {fight.Progress:0.00} by {fight.Claimer})");
        Move(f, b, Zone + new Vector2(3, 0));
        var before = fight.Progress;
        f.Run(10);
        Check(fight.Contested && Math.Abs(fight.Progress - before) < 0.02 && fight.Claimer == wolves.Id, $"Bears in the zone too: contested, the claim pauses ({before:0.00} -> {fight.Progress:0.00})");
        Move(f, b, Zone + new Vector2(40, 0));
        f.Run(9);
        Check(castle.State.Owner == wolves.Id && castle.State.OwnerName == "Wolves" && fight.Phase == 0 && fight.GuardsBackAt is not null && fight.Mobs.Count == 0,
              "Bears gone: Wolves fill the claim and own the castle; phase 0, the guards back later");
        var progress = Of(a.Peer, EventCodes.CastleClaimProgress, from).ToList();
        var logo = Of(a.Peer, EventCodes.CastleClaimProgressLogo, from).FirstOrDefault();
        Check(progress.Count > 5 && progress.All(p => Convert.ToInt64(p[0]) == castle.Id) && progress.Any(p => p.GetValueOrDefault((byte)1) is byte[] g && new Guid(g) == wolves.Id)
              && progress.Any(p => Convert.ToInt64(p.GetValueOrDefault((byte)3) ?? 0L) == 10000)
              && logo is not null && logo[1] is byte[] lg && new Guid(lg) == wolves.Id,
              $"513 to the observers: the castle, Wolves claiming, up to 10000; 514 with Wolves' crest ({progress.Count} progress events)");
        var seq = a.Peer.Events.Skip(from).Where(e => Code(e) is (int)EventCodes.NewCastleObject or (int)EventCodes.CastlePhaseChanged).ToList();
        var newObj = seq.FindLastIndex(e => Code(e) == (int)EventCodes.NewCastleObject && e.Parameters.GetValueOrDefault((byte)4) is byte[] o && new Guid(o) == wolves.Id);
        var end = seq.FindIndex(e => Code(e) == (int)EventCodes.CastlePhaseChanged && Convert.ToInt32(e.Parameters.GetValueOrDefault((byte)2) ?? 0) == 0);
        Check(newObj >= 0 && end > newObj && seq[newObj].Parameters[5] is "Wolves" && seq[newObj].Parameters.ContainsKey(18),
              "112 again with the owner (4 Wolves, 5 the name, 14-20 the crest) before 417 7 -> 0 (the client's won/lost stinger)");
        lock (f.World.Sync) f.World.Guilds.Disband(bears);
        Check(castle.State.Owner == wolves.Id, "another guild's end leaves the owner");
    }

    private static void Persistence()
    {
        using (var f = new Fixture())
        {
            var a = f.Join("Anna", Zone + new Vector2(30, 0));
            var wolves = f.Guild(a, "Wolves");
            lock (f.World.Sync) f.World.Territories.Gm(f.World.Territories.Find(Tor.ToString())!, "owner", wolves);
            f.Restart();
            var tor = f.World.Territories.Find(Tor.ToString())!;
            Check(tor.Owner == wolves.Id && tor.OwnerName == "Wolves" && tor.Logo is not null && (tor.Since - Saturday).Duration() < TimeSpan.FromMinutes(1),
                  "restart: Wetgrave Bog Tor still Wolves' (name, crest, since)");
            var again = f.World.Guilds.ById(wolves.Id)!;
            lock (f.World.Sync) f.World.Guilds.Disband(again);
            Check(!tor.Owned, "the owner guild disbanded: unowned");
            f.Restart();
            Check(!f.World.Territories.Find(Tor.ToString())!.Owned, "and still unowned after a restart");
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
                cmd.CommandText = "DROP TABLE territory_owners; PRAGMA user_version=38;";
                cmd.ExecuteNonQuery();
            }
            using (var migrated = new SqliteCharacterRepository(path))
                Check(migrated.LoadTerritoryOwners().Count == 0, "v38 save migrated: an empty territory_owners");
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 42, "schema version 42 (v40 hideouts)");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Chests()
    {
        using (var f = new Fixture())
        {
            var a = f.Join("Anna", Zone + new Vector2(20, 0));
            var b = f.Join("Bert", Zone + new Vector2(25, 0));
            var wolves = f.Guild(a, "Wolves");
            f.Guild(b, "Bears");
            var castle = f.Castle;
            var fight = castle.State.Fight;
            Check(fight.ChestSlot == Saturday && fight.Chest is null, "official: 12:00 drawn while unowned: no chest");
            lock (f.World.Sync) f.World.Territories.Gm(castle.State, "owner", wolves);
            var from = a.Peer.Events.Count;
            for (var i = 0; i < 4 && fight.Chest is null; i++) f.Jump(TimeSpan.FromHours(2));
            var chest = fight.Chest;
            Check(chest is { Map: not null, LockState: 6 } && chest.Template.Name.StartsWith("TREASURE_CASTLE_", StringComparison.Ordinal) && fight.ChestSlot!.Value.Minute == 0
                  && fight.ChestSlot.Value.Hour % 2 == 0 && fight.ChestUntil == fight.ChestSlot.Value.AddMinutes(30),
                  $"owned: a TREASURE_CASTLE_* chest at an even hour (by 18:00 the weekend legendary at the latest), Unlocked, until +30 min (got {chest} at {fight.ChestSlot:HH:mm})");
            var obj = Of(a.Peer, EventCodes.NewCastleObject, from).LastOrDefault();
            Check(obj is not null && Convert.ToInt32(obj[12]) == 6 && obj[13] is string rarity && rarity == chest!.State.State && rarity.Length > 0,
                  "112 again: 12 ChestState 6, 13 the chest's rarity");
            string? refused, opened;
            lock (f.World.Sync) (refused, opened) = (f.World.Dungeons.OpenChest(b.Player, chest!), f.World.Dungeons.OpenChest(a.Player, chest!));
            Check(refused is not null && opened is null && chest!.Rolled, $"Bears may not open it ({refused}), Wolves' member may");
            f.Jump(TimeSpan.FromMinutes(31));
            Check(fight.Chest is null && !f.World.Maps.Find("0305")!.Objects.ContainsKey(chest!.Id) && castle.Chest == CastleObject.ChestEmpty,
                  "31 min later the chest is gone, ChestState 8");
        }
        using (var pve = new Fixture(new ServerConfig { Territories = ServerConfig.PveDefaults.Territories }))
        {
            var b = pve.Join("Bert", Zone + new Vector2(25, 0));
            pve.Guild(b, "Bears");
            var fight = pve.Castle.State.Fight;
            for (var i = 0; i < 4 && fight.Chest is null; i++) pve.Jump(TimeSpan.FromHours(2));
            string? opened = "none";
            if (fight.Chest is { } chest) lock (pve.World.Sync) opened = pve.World.Dungeons.OpenChest(b.Player, chest);
            Check(!pve.Castle.State.Owned && fight.Chest is not null && opened is null, "pve: the unowned castle has its chest too, and anyone opens it");
        }
    }
}
