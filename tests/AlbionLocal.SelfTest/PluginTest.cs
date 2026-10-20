using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The C# plugins (server/plugins/README.md): a real build and load of the welcome example, reload, and a faulty plugin's disable.</summary>
public static class PluginTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("plugins: welcome built from a temp folder greets at login, .motd answers, reload drops and re-adds its handler, storage saved", WelcomeBuildLoginReload),
        ("plugins: a plugin that throws is disabled after Plugins:MaxErrors, the world and other plugins go on", FaultyPluginDisabled),
    ];

    /// <summary>The repository's plugins/ folder (the example plugins), found upward from the test's bin folder.</summary>
    private static string ExamplePlugins()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "plugins", "welcome", "plugin.json"))) return Path.Combine(dir.FullName, "plugins");
        throw new Exception("plugins/welcome not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters[ReservedKeys.EventCode]);

    private static List<string> SystemLines(FakePeer peer, int from = 0) =>
        [.. peer.Events.Skip(from).Where(e => Code(e) == (int)EventCodes.SystemMessage).Select(e => e.Parameters[0] as string ?? "")];

    private static void WelcomeBuildLoginReload()
    {
        var dir = NewDir();
        var plugins = Path.Combine(dir, "plugins");
        CopyDir(Path.Combine(ExamplePlugins(), "welcome"), Path.Combine(plugins, "welcome"));
        var characters = Repository(dir);
        var config = new ServerConfig
        {
            JoinByCharacterId = true, SavesPath = dir,
            Plugins = new PluginOptions { Enabled = true, Path = plugins, List = ["welcome"] }, // its plugin.json ships "enabled": false
        };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = new ManualClock() };
        world.Plugins.LoadAll();
        Check(world.Plugins.IsLoaded("welcome"), $"welcome built and loaded (state: {world.Plugins.StateOf("welcome")})");
        Check(File.Exists(Path.Combine(dir, "plugins-bin", "welcome", "out", "AlbionPlugin.welcome.dll")), "built into data/plugins-bin/welcome/out");

        var hero = characters.List()[0];
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        var greetings = SystemLines(peer).Where(l => l.Contains("Welcome", StringComparison.Ordinal)).ToList();
        Check(greetings is [var g] && g.Contains(hero.Name), $"one greeting with the name at login (got: {string.Join(" | ", SystemLines(peer))})");

        var before = peer.Events.Count;
        session.OnRequest(Request(new SayRequest { aa9 = ".motd" }, null), encrypted: false);
        Check(SystemLines(peer, before) is [var motd1, _] && motd1.StartsWith("Message of the day", StringComparison.Ordinal), $".motd answers the config's lines (got: {string.Join(" | ", SystemLines(peer, before))})");

        Check(world.Plugins.ReloadNow("welcome") is null && world.Plugins.IsLoaded("welcome"), $"reloaded (state: {world.Plugins.StateOf("welcome")})");
        var peer2 = new FakePeer();
        new WorldSession(peer2, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        var again = SystemLines(peer2).Where(l => l.Contains("Welcome", StringComparison.Ordinal)).ToList();
        Check(again.Count == 1, $"after the reload one greeting per login: the old handler is gone, the new one runs (got {again.Count})");

        world.Plugins.Unload("welcome", "test");
        var peer3 = new FakePeer();
        new WorldSession(peer3, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        Check(!SystemLines(peer3).Any(l => l.Contains("Welcome", StringComparison.Ordinal)), "unloaded: no greeting");
        var storage = Path.Combine(dir, "plugins", "welcome", "storage.json");
        Check(File.Exists(storage) && File.ReadAllText(storage).Contains(hero.Id.ToString(), StringComparison.Ordinal), "its storage (the login counts) was saved at the unload");

        world.Plugins.Shutdown();
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Throws on every tick.</summary>
    private sealed class Thrower : PluginBase
    {
        public static int Ticks;
        public override string Name => "thrower";

        protected override void OnLoad()
        {
            Events.Tick += _ =>
            {
                Ticks++;
                throw new InvalidOperationException("boom");
            };
            Commands.Register("boom", PlayerLevel.Player, "throws", _ => throw new InvalidOperationException("command boom"));
        }
    }

    /// <summary>Counts ticks and timer runs.</summary>
    private sealed class Counter : PluginBase
    {
        public int Ticks, Timer;
        public override string Name => "counter";

        protected override void OnLoad()
        {
            Events.Tick += _ => Ticks++;
            Timers.Every(TimeSpan.FromMilliseconds(100), () => Timer++);
        }
    }

    private static void FaultyPluginDisabled()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var config = new ServerConfig { SavesPath = dir, Plugins = new PluginOptions { Enabled = true, Path = Path.Combine(dir, "plugins"), MaxErrors = 3 } };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = clock };
        var counter = new Counter();
        Thrower.Ticks = 0;
        Check(world.Plugins.Load(new Thrower()) is null && world.Plugins.Load(counter) is null, "both in-process plugins load");
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            world.Update(); // never throws: every plugin call is guarded
        }
        Check(Thrower.Ticks == 3, $"the faulty plugin ran until its 3rd error, then no more (ran {Thrower.Ticks} times)");
        Check(!world.Plugins.IsLoaded("thrower") && world.Plugins.StateOf("thrower") is { } s && s.StartsWith("disabled", StringComparison.Ordinal),
            $"it is disabled and unloaded (state: {world.Plugins.StateOf("thrower")})");
        Check(world.Plugins.IsLoaded("counter") && counter.Ticks == 10 && counter.Timer >= 4, $"the other plugin got every tick and its timer (ticks {counter.Ticks}, timer {counter.Timer})");
        var replies = new List<(string Text, bool Error)>();
        world.Commands.Execute(new AlbionLocal.Server.Commands.CommandContext(world, null, AlbionLocal.Server.Persistence.AccountLevel.Admin, (t, e) => replies.Add((t, e))), ".boom");
        Check(replies is [(var unknown, true)] && unknown.Contains("unknown command"), $"the disabled plugin's command is gone (got {replies.FirstOrDefault().Text})");
        world.Plugins.Shutdown();
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
