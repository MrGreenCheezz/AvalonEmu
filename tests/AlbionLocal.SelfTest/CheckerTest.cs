using System.Numerics;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The client checker (analysis/work/stageN/checker/SPEC.md): content/checks.json against the commands, .check, and the GM commands made for it.</summary>
public static class CheckerTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("checker: content/checks.json - ids unique, order 1..n, every setup line parses as a command with valid arguments", Validate),
        ("checker: .check runs the setup (a .tele waits for the next Join), shows the steps; .check fail saves the result with log lines", RunAndFail),
        ("checker: .hp 50 halves the health, .hp 0 knocks the player down (KnockedDown)", HpZero),
        ("checker: .mob spawn <mob> 1 10 puts the mob 10 m in front of the player, facing it", MobDistance),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static string ContentDir() => Path.GetFullPath(Path.Combine(DataDir(), "..", "content"));

    private static void Validate()
    {
        _ = Client.Clusters!.Mobs; // .mob spawn parses mobs.xml names
        var checks = Checklist.Load(File.ReadAllText(Path.Combine(ContentDir(), "checks.json")));
        var dir = NewDir();
        var characters = Repository(dir);
        var world = new World(new ServerConfig { JoinByCharacterId = true, SavesPath = dir }, characters, Client.Items) { Sync = new object(), Clock = new ManualClock(), Clusters = Client.Clusters };
        var errors = Checklist.Validate(checks, new CommandContext(world, null, AccountLevel.Admin, (_, _) => { }));
        Check(checks.Count >= 150 && errors.Count == 0, $"{checks.Count} checks, {errors.Count} problem(s): {string.Join(" | ", errors.Take(12))}");
        Check(checks.All(c => c.Needs.Length == 0), "no check still needs a command that does not exist");
        var bad = Checklist.Validate([checks[0] with { Setup = [".mob spawn NO_SUCH_MOB 1 5", ".tele"] }, checks[1] with { Order = 1 }],
            new CommandContext(world, null, AccountLevel.Admin, (_, _) => { }));
        Check(bad.Count == 3 && bad.Any(e => e.Contains("unknown mob NO_SUCH_MOB")) && bad.Any(e => e.Contains("missing <cluster>")) && bad.Any(e => e.StartsWith("order")),
              $"and it finds an unknown mob, a missing argument and a repeated order (got {string.Join(" | ", bad)})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private sealed class Town : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly SqliteCharacterRepository Characters;
        public readonly ManualClock Clock = new();
        public readonly World World;

        public Town(string? checksJson = null)
        {
            Characters = Repository(Dir);
            var content = Path.Combine(Dir, "content");
            Directory.CreateDirectory(content);
            if (checksJson is not null) File.WriteAllText(Path.Combine(content, "checks.json"), checksJson);
            _ = Client.Clusters!.Mobs;
            World = new World(new ServerConfig { JoinByCharacterId = true, SavesPath = Dir, ContentPath = content }, Characters, Client.Items, Client.GameData, Client.Spells)
            {
                Sync = new object(), Clock = Clock, Clusters = Client.Clusters, MobAi = false,
            };
        }

        public (WorldSession Session, FakePeer Peer) Join(Guid character)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            lock (World.Sync) session.OnRequest(Request(new JoinRequest { ss = character }, null), encrypted: true);
            Until(() => session.Player?.Map?.Grid is not null, "joined, with the cluster's world objects");
            return (session, peer);
        }

        public Character Hero(string cluster, Vector2 at)
        {
            var c = StarterKit.Default.NewCharacter("C" + Guid.NewGuid().ToString("N")[..10]);
            (c.Cluster, c.PositionX, c.PositionY) = (cluster, at.X, at.Y);
            Check(Characters.Create(c), "character created");
            return c;
        }

        /// <summary>Ticks without moving the clock until <paramref name="done"/> (a cluster resolves on a worker and posts back), 20 s at most.</summary>
        public void Until(Func<bool> done, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!done())
            {
                Check(DateTime.UtcNow < until, what);
                Thread.Sleep(10);
                lock (World.Sync) World.Update();
            }
        }

        public void Tick(double seconds)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (World.Sync) World.Update();
        }

        public List<(string Text, bool Error)> Run(Player p, string line)
        {
            var replies = new List<(string, bool)>();
            lock (World.Sync) World.Commands.Execute(new CommandContext(World, p, AccountLevel.GameMaster, (t, e) => replies.Add((t, e))), line);
            return replies;
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    private static List<string> SystemLines(FakePeer peer) =>
        [.. peer.Events.Where(e => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode)) == (int)EventCodes.SystemMessage).Select(e => e.Parameters[0]?.ToString() ?? "")];

    private static void RunAndFail()
    {
        const string json = """
            // a test list
            [
              { "id": "T-01", "order": 1, "area": "Тест", "title": "Телепорт и руда", "priority": 1, "players": 1, "minutes": 1,
                "setup": [".tele 0007", ".item add T4_ORE 3"], "steps": ["Открой сумку."], "expect": ["Три руды."], "ifBroken": "Скриншот.", "source": "test" },
              { "id": "T-02", "order": 2, "area": "Тест", "title": "Второй", "priority": 2, "players": 2, "minutes": 1,
                "setup": [], "steps": ["Шаг."], "expect": ["Итог."], "ifBroken": "", "source": "test" },
            ]
            """;
        using var town = new Town(json);
        var hero = town.Hero("0000", Vector2.Zero);
        var (first, _) = town.Join(hero.Id);
        var p = first.Player!;
        var replies = town.Run(p, ".check T-01");
        Check(replies.Count == 0 && town.World.Checks.StatusOf("T-01") == Checklist.CheckStatus.InProgress && town.World.Checks.Running(hero.Id),
              $".check T-01: in progress, its setup under way (got {string.Join(" / ", replies)})");
        town.Tick(0.5);
        town.Until(() => first.Status == SessionStatus.Transferring, "the .tele line ran");
        Check(first.Status == SessionStatus.Transferring && town.World.Saves.Load(hero.Id) is { Cluster: "0007" }, "the first setup line .tele 0007 ran: the player changes cluster");
        town.Tick(1);
        town.Tick(1);
        Check(town.World.Checks.Running(hero.Id), "the runner waits for the Join in 0007");
        var (second, peer) = town.Join(hero.Id);
        Check(second.Player!.Map!.Cluster == "0007", "joined in 0007");
        for (var i = 0; i < 8; i++) town.Tick(0.5);
        var me = second.Player!;
        var lines = SystemLines(peer);
        Check(me.Inventory.Bag.Items.Any(x => x.Template.Name == "T4_ORE" && x.Count == 3), "after the Join and the pause the second line .item add T4_ORE 3 ran");
        Check(!town.World.Checks.Running(hero.Id) && lines.Any(l => l.StartsWith("== T-01: Телепорт и руда")) && lines.Contains("1. Открой сумку.") && lines.Contains("- Три руды."),
              $"then the steps and expected results in chat (got {string.Join(" / ", lines.TakeLast(8))})");

        Log.Warn("test", "checker-marker: what the failure keeps");
        replies = town.Run(me, ".check fail T-01 руды нет в сумке");
        var path = town.World.Checks.ResultsPath;
        Check(replies is [(var r, false)] && r.StartsWith("T-01: СБОЙ") && File.Exists(path), $".check fail: answered and written (got {string.Join(" / ", replies)})");
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var result = doc.RootElement.GetProperty("results").GetProperty("T-01");
            Check(result.GetProperty("status").GetString() == "fail" && result.GetProperty("note").GetString() == "руды нет в сумке"
                  && result.GetProperty("character").GetString() == hero.Name && result.GetProperty("build").GetString()!.Length > 0
                  && result.GetProperty("log").EnumerateArray().Any(l => l.GetString()!.Contains("checker-marker")),
                  "data/checks-results.json: status fail, the note, the character, the build and the log lines with the last warning");
        }
        replies = town.Run(me, ".check ok тоже заметка");
        Check(town.World.Checks.StatusOf("T-01") == Checklist.CheckStatus.Ok && town.World.Checks.ResultOf("T-01")?.Note == "тоже заметка",
              ".check ok without an id: the check run last, the rest is the note");
        replies = town.Run(me, ".check next");
        Check(town.World.Checks.StatusOf("T-02") == Checklist.CheckStatus.InProgress && SystemLines(peer).Any(l => l.StartsWith("Вдвоём:")),
              ".check next: T-02 (no setup: its text at once, with the two-player note)");
    }

    private static void HpZero()
    {
        using var town = new Town();
        var (session, peer) = town.Join(town.Hero("0204", new Vector2(90, -260)).Id);
        var p = session.Player!;
        var replies = town.Run(p, ".hp 50");
        Check(replies is [(_, false)] && Math.Abs(p.Health.At(town.World.GameNow) - p.Health.Max / 2) <= 1, $".hp 50: half the health (got {string.Join(" / ", replies)})");
        var mark = peer.Events.Count;
        replies = town.Run(p, ".hp 0");
        Check(replies is [(var r, false)] && r.StartsWith("knocked down") && p.Combat.Down is { Kind: KnockdownKind.Pve }
              && peer.Events.Skip(mark).Any(e => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode)) == (int)EventCodes.KnockedDown),
              $".hp 0: a PvE knockdown with KnockedDown (got {string.Join(" / ", replies)})");
    }

    private static void MobDistance()
    {
        using var town = new Town();
        var (session, _) = town.Join(town.Hero("0204", new Vector2(90, -260)).Id);
        var p = session.Player!;
        p.Facing = 90; // looking along +x (x = sin, z = cos)
        var at = p.PositionAt(DateTime.UtcNow);
        var replies = town.Run(p, ".mob spawn T4_MOB_UNDEAD_SOLDIER 1 10");
        var mob = p.Map!.Objects.Values.OfType<Mob>().Where(m => m.Slot is null && m.Template.Name == "T4_MOB_UNDEAD_SOLDIER").MaxBy(m => m.Id);
        Check(replies is [(var r, false)] && r.Contains("10 m ahead") && mob is not null && Math.Abs(Vector2.Distance(mob.Position, at) - 10) <= 6
              && mob.Position.X - at.X > 4 && Math.Abs(AngleTo(mob.Position, at) - mob.Direction) < 1,
              $".mob spawn ... 1 10: about 10 m along the facing, turned to the player (got {string.Join(" / ", replies)}; mob at {mob?.Position}, player {at})");
        Check(town.Run(p, ".mob spawn T4_MOB_UNDEAD_SOLDIER 1 61") is [(_, true)], "dist over 60 is refused");
    }

    private static float AngleTo(Vector2 from, Vector2 to) => AlbionLocal.Server.AI.MotionMaster.Facing(from, to);
}
