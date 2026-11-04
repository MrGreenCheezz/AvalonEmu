using System.Reflection;
using System.Text.RegularExpressions;
using AlbionLocal.Modules;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Modules;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The server modules (docs/modules/README.md), without client data: a real build and load, a faulty script, the hook model, settings, the network hooks and the contract checks.</summary>
public static class ModuleTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("modules: mod-skeleton built from a temp folder greets at login, .motd answers, reload swaps the handler, storage saved, settings copied once", SkeletonBuildLoginReload),
        ("modules: a script that throws is disabled after Modules:MaxErrors, the world and other modules go on", FaultyDisabled),
        ("modules: \"Enabled\": false in the owner's settings keeps a module out, an unknown manifest key and a wrong API version are told", SettingsAndManifest),
        ("modules: priorities order the handlers, Cancel stops the later ones, Monitor always runs and cannot cancel, CancelledBy names the module, Text changes", HookModel),
        ("modules: Network.OnRequest rejects an operation before its handler with a return code and a line, OnEvent keeps an event back", NetworkHooks),
        ("modules: a module with coreAccess reaches the server's classes; without it the build says what to do", CoreAccess),
        ("modules: every example builds and loads; a module that requires another uses its public class, and goes and comes back with it", ExamplesAndRequires),
        ("modules contract: the public API has every member of docs/modules/api-baseline.txt", ApiContract),
        ("modules contract: every hook is raised and every dispatch method is called from the game's code", SeamContract),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AlbionLocal.slnx"))) return dir.FullName;
        throw new Exception("AlbionLocal.slnx not found above " + AppContext.BaseDirectory);
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from).Where(d => Path.GetFileName(d) is not ("bin" or "obj"))) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters[ReservedKeys.EventCode]);

    private static List<string> SystemLines(FakePeer peer, int from = 0) =>
        [.. peer.Events.Skip(from).Where(e => Code(e) == (int)EventCodes.SystemMessage).Select(e => e.Parameters[0] as string ?? "")];

    private static void Clean(string dir)
    {
        try { Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void SkeletonBuildLoginReload()
    {
        var dir = NewDir();
        var modules = Path.Combine(dir, "modules");
        var settings = Path.Combine(dir, "config", "modules");
        CopyDir(Path.Combine(RepoRoot(), "modules", "examples", "mod-skeleton"), Path.Combine(modules, "mod-skeleton"));
        var characters = Repository(dir);
        var config = new ServerConfig
        {
            JoinByCharacterId = true, SavesPath = dir,
            Modules = new ModuleOptions { Enabled = true, Path = modules, ConfigPath = settings, List = ["mod-skeleton"] }, // its module.jsonc ships "enabled": false
        };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = new ManualClock() };
        world.Modules.LoadAll();
        Check(world.Modules.IsLoaded("mod-skeleton"), $"built and loaded (state: {world.Modules.StateOf("mod-skeleton")})");
        Check(File.Exists(Path.Combine(settings, "mod-skeleton.jsonc")), "its defaults were copied for the owner to config/modules/");

        var hero = characters.List()[0];
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        var greetings = SystemLines(peer).Where(l => l.Contains("Welcome", StringComparison.Ordinal)).ToList();
        Check(greetings is [var g] && g.Contains(hero.Name), $"one greeting with the name at login (got: {string.Join(" | ", SystemLines(peer))})");

        var before = peer.Events.Count;
        session.OnRequest(Request(new SayRequest { aa9 = ".motd" }, null), encrypted: false);
        Check(SystemLines(peer, before) is [var motd, _] && motd.StartsWith("Message of the day", StringComparison.Ordinal), $".motd answers the settings' lines (got: {string.Join(" | ", SystemLines(peer, before))})");

        Check(world.Modules.ReloadNow("mod-skeleton") is null && world.Modules.IsLoaded("mod-skeleton"), $"reloaded (state: {world.Modules.StateOf("mod-skeleton")})");
        var peer2 = new FakePeer();
        new WorldSession(peer2, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        Check(SystemLines(peer2).Count(l => l.Contains("Welcome", StringComparison.Ordinal)) == 1, "after the reload one greeting per login: the old handler is gone, the new one runs");

        Check(ModuleHost.OldCopies("mod-skeleton", 1) == 0, "the reload left no old copy of the module's code in memory");
        world.Modules.Unload("mod-skeleton", "test");
        var peer3 = new FakePeer();
        new WorldSession(peer3, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        Check(!SystemLines(peer3).Any(l => l.Contains("Welcome", StringComparison.Ordinal)), "unloaded: no greeting");
        var storage = Path.Combine(dir, "modules", "mod-skeleton", "storage.json");
        Check(File.Exists(storage) && File.ReadAllText(storage).Contains(hero.Id.ToString(), StringComparison.Ordinal), "its storage (the login counts) was saved at the unload");

        world.Modules.Shutdown();
        world.Dispose();
        characters.Dispose();
        Clean(dir);
    }

    /// <summary>Throws on every tick.</summary>
    private sealed class Thrower : ServerScript
    {
        public int Ticks;

        protected override void OnLoad() => Commands.Register("boom", PlayerLevel.Player, "throws", _ => throw new InvalidOperationException("command boom"));

        public override void OnTick(TickArgs e)
        {
            Ticks++;
            throw new InvalidOperationException("boom");
        }
    }

    /// <summary>Counts ticks and timer runs.</summary>
    private sealed class Counter : ServerScript
    {
        public int Ticks, Timer;

        protected override void OnLoad() => Timers.Every(TimeSpan.FromMilliseconds(100), () => Timer++);

        public override void OnTick(TickArgs e) => Ticks++;
    }

    private static void FaultyDisabled()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var config = new ServerConfig { SavesPath = dir, Modules = new ModuleOptions { Enabled = true, Path = Path.Combine(dir, "modules"), MaxErrors = 3 } };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = clock };
        var (thrower, counter) = (new Thrower(), new Counter());
        Check(world.Modules.Load("thrower", thrower) is null && world.Modules.Load("counter", counter) is null, "both in-process modules load");
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            world.Update(); // never throws: every module call is guarded
        }
        Check(thrower.Ticks == 3, $"the faulty script ran until its 3rd error, then no more (ran {thrower.Ticks} times)");
        Check(!world.Modules.IsLoaded("thrower") && world.Modules.StateOf("thrower") is { } s && s.StartsWith("disabled", StringComparison.Ordinal), $"it is disabled and unloaded (state: {world.Modules.StateOf("thrower")})");
        Check(world.Modules.IsLoaded("counter") && counter.Ticks == 10 && counter.Timer >= 4, $"the other module got every tick and its timer (ticks {counter.Ticks}, timer {counter.Timer})");
        var replies = new List<(string Text, bool Error)>();
        world.Commands.Execute(new AlbionLocal.Server.Commands.CommandContext(world, null, AlbionLocal.Server.Persistence.AccountLevel.Admin, (t, e) => replies.Add((t, e))), ".boom");
        Check(replies is [(var unknown, true)] && unknown.Contains("unknown command"), $"the disabled module's command is gone (got {replies.FirstOrDefault().Text})");
        world.Modules.Shutdown();
        world.Dispose();
        characters.Dispose();
        Clean(dir);
    }

    private static void SettingsAndManifest()
    {
        var dir = NewDir();
        var modules = Path.Combine(dir, "modules");
        var settings = Path.Combine(dir, "config");
        void Module(string id, string manifest)
        {
            Directory.CreateDirectory(Path.Combine(modules, id));
            File.WriteAllText(Path.Combine(modules, id, ModuleManifest.FileName), manifest);
            File.WriteAllText(Path.Combine(modules, id, "S.cs"), "public sealed class S : AlbionLocal.Modules.Module;");
        }
        Module("mod-off", """{ "id": "mod-off" }""");
        Directory.CreateDirectory(settings);
        File.WriteAllText(Path.Combine(settings, "mod-off.jsonc"), """{ "Enabled": false }""");
        Module("mod-typo", """{ "id": "mod-typo", "requries": ["x"] }""");
        Module("mod-future", """{ "id": "mod-future", "api": "99.0" }""");
        Module("mod-needs", """{ "id": "mod-needs", "requires": ["mod-offf"] }""");
        var config = new ServerConfig { SavesPath = dir, Modules = new ModuleOptions { Path = modules, ConfigPath = settings } };

        var report = new StringWriter();
        var errors = ModuleDoctor.Run(config, report);
        var text = report.ToString();
        Check(errors == 2 && text.Contains("update the server") && text.Contains("did you mean mod-off"), $"the doctor finds the wrong API version and the missing requirement with its hint:\n{text}");
        Check(text.Contains("unknown key \"requries\" (did you mean \"requires\"?)"), $"and warns of a manifest typo:\n{text}");
        Check(text.Contains("mod-off 1.0.0 (turned off in its settings)"), $"and names the module its owner turned off:\n{text}");

        var characters = Repository(dir);
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = new ManualClock() };
        world.Modules.LoadAll();
        Check(!world.Modules.IsLoaded("mod-off") && world.Modules.StateOf("mod-off")!.StartsWith("turned off", StringComparison.Ordinal), $"not loaded (state: {world.Modules.StateOf("mod-off")})");
        Check(!world.Modules.IsLoaded("mod-future") && world.Modules.StateOf("mod-future")!.Contains("update the server"), $"the newer API refused (state: {world.Modules.StateOf("mod-future")})");
        Check(world.Modules.IsLoaded("mod-typo"), $"a typo is only a warning (state: {world.Modules.StateOf("mod-typo")})");
        world.Modules.Shutdown();
        world.Dispose();
        characters.Dispose();
        Clean(dir);
    }

    private sealed class ChatFilter(List<string> log, string name, bool cancel) : SocialScript
    {
        [Priority(HookPriority.Early)]
        public override void OnBeforeChat(ChatArgs e)
        {
            log.Add(name);
            if (cancel) e.Cancel("no talking");
            else e.Text = e.Text.ToUpperInvariant();
        }
    }

    private static void HookModel()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var config = new ServerConfig { JoinByCharacterId = true, SavesPath = dir, Modules = new ModuleOptions { Path = Path.Combine(dir, "modules") } };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = new ManualClock() };
        var log = new List<string>();
        ChatArgs? seen = null;
        var hooks = new Lambdas();
        Check(world.Modules.Load("lambdas", hooks) is null, "the lambda module loads");
        hooks.Hooks_.Social.OnBeforeChat(_ => log.Add("normal"));
        hooks.Hooks_.Social.OnBeforeChat(_ => log.Add("late"), HookPriority.Late);
        hooks.Hooks_.Social.OnBeforeChat(e =>
        {
            log.Add("monitor");
            seen = e;
            e.Uncancel();
        }, HookPriority.Monitor);
        var filter = new ChatFilter(log, "early", cancel: false);
        Check(world.Modules.Load("filter", filter) is null, "the script module loads");

        var hero = characters.List()[0];
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        session.OnRequest(Request(new SayRequest { aa9 = "hello" }, null), encrypted: false);
        Check(log.SequenceEqual(["early", "normal", "late", "monitor"]), $"by priority, whatever the load order (got {string.Join(", ", log)})");
        var info = world.Modules.Info("filter");
        Check(info.Contains("Social.OnBeforeChat") && !info.Contains("Social.OnPartyJoined"), $"only the method it overrides is connected: {info}");
        Check(seen is { Text: "HELLO" }, $"the text a handler set reaches the later ones (got {seen?.Text})");

        world.Modules.Unload("filter", "test");
        log.Clear();
        var canceller = new ChatFilter(log, "early", cancel: true);
        Check(world.Modules.Load("canceller", canceller) is null, "the cancelling module loads");
        var mark = peer.Events.Count;
        session.OnRequest(Request(new SayRequest { aa9 = "hello" }, null), encrypted: false);
        Check(log.SequenceEqual(["early", "monitor"]), $"Cancel stops the later handlers, a Monitor still runs (got {string.Join(", ", log)})");
        Check(seen is { IsCancelled: true, CancelledBy: "canceller" }, $"a Monitor's Uncancel is undone and CancelledBy names the module (cancelled {seen?.IsCancelled}, by {seen?.CancelledBy})");
        Check(SystemLines(peer, mark).Contains("no talking"), $"the reason goes to the player (got {string.Join(" | ", SystemLines(peer, mark))})");

        world.Modules.Shutdown();
        world.Dispose();
        characters.Dispose();
        Clean(dir);
    }

    /// <summary>A module that hands out its hooks to the test.</summary>
    private sealed class Lambdas : AlbionLocal.Modules.Module
    {
        public IHooks Hooks_ => Hooks;
    }

    private static void NetworkHooks()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var config = new ServerConfig { JoinByCharacterId = true, SavesPath = dir, Modules = new ModuleOptions { Path = Path.Combine(dir, "modules") } };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = new ManualClock() };
        var net = new Lambdas();
        Check(world.Modules.Load("net", net) is null, "the module loads");
        var names = new List<string>();
        net.Hooks_.Network.OnRequest(e => names.Add(e.Name));
        net.Hooks_.Network.OnRequest("Say", e => e.Reject(1234, "not here"));
        net.Hooks_.Network.OnEvent("SystemMessage", e =>
        {
            if (e.As<SystemMessageEvent>() is { d0p: "secret" }) e.Cancel();
        });

        var hero = characters.List()[0];
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        Check(names.Contains("Join"), $"every operation passes (got {string.Join(", ", names)})");
        var (mark, responses) = (peer.Events.Count, peer.Responses.Count);
        session.OnRequest(Request(new SayRequest { aa9 = ".help" }, 7), encrypted: false);
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == 1234, $"the operation is answered with the code the module gave (rc {peer.Responses[^1].ReturnCode})");
        Check(SystemLines(peer, mark) is ["not here"], $"its handler did not run, the reason is a line (got {string.Join(" | ", SystemLines(peer, mark))})");
        mark = peer.Events.Count;
        session.Send(new SystemMessageEvent { d0p = "secret", d0q = 0 });
        session.Send(new SystemMessageEvent { d0p = "open", d0q = 0 });
        Check(SystemLines(peer, mark) is ["open"], $"the event a module keeps back does not go out (got {string.Join(" | ", SystemLines(peer, mark))})");
        var answered = 0;
        net.Hooks_.Network.OnEvent("SystemMessage", e =>
        {
            answered++;
            e.Player?.SendMessage("again"); // would set off OnEvent for ever
        });
        mark = peer.Events.Count;
        session.Send(new SystemMessageEvent { d0p = "open", d0q = 0 });
        Check(answered == 1 && SystemLines(peer, mark).Count == 2, $"a message sent from OnEvent goes straight out, no loop (handler ran {answered} times, {SystemLines(peer, mark).Count} lines)");

        world.Modules.Shutdown();
        world.Dispose();
        characters.Dispose();
        Clean(dir);
    }

    private static void CoreAccess()
    {
        var dir = NewDir();
        var modules = Path.Combine(dir, "modules");
        CopyDir(Path.Combine(RepoRoot(), "modules", "examples", "mod-core-access"), Path.Combine(modules, "mod-core-access"));
        var plain = Path.Combine(modules, "mod-no-core");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, ModuleManifest.FileName), """{ "id": "mod-no-core" }""");
        File.WriteAllText(Path.Combine(plain, "S.cs"), "public sealed class S : AlbionLocal.Modules.PlayerScript { public override void OnLogin(AlbionLocal.Modules.PlayerLoginArgs e) { _ = AlbionLocal.Server.Modules.ModuleCore.Player(e.Player); } }");
        var characters = Repository(dir);
        var config = new ServerConfig { JoinByCharacterId = true, SavesPath = dir, Modules = new ModuleOptions { Path = modules, List = ["mod-core-access", "mod-no-core"] } };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = new ManualClock() };
        world.Modules.LoadAll();
        Check(world.Modules.IsLoaded("mod-core-access"), $"the coreAccess module builds and loads (state: {world.Modules.StateOf("mod-core-access")})");
        Check(world.Modules.StateOf("mod-no-core") is { } why && why.StartsWith("failed", StringComparison.Ordinal), $"the one without it does not build (state: {world.Modules.StateOf("mod-no-core")})");
        var (_, error) = ModuleBuilder.Resolve(ModuleManifest.Read(plain), Path.Combine(dir, "modules-bin"), build: true, force: true);
        Check(error is not null && error.Contains("coreAccess", StringComparison.Ordinal), $"and the error says what to do: {error}");

        var hero = characters.List()[0];
        var peer = new FakePeer();
        new WorldSession(peer, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        Check(SystemLines(peer).Any(l => l.StartsWith(hero.Name + ": template", StringComparison.Ordinal)), $"it read the server's own Player and World at login (got {string.Join(" | ", SystemLines(peer))})");
        world.Modules.Shutdown();
        world.Dispose();
        characters.Dispose();
        Clean(dir);
    }

    private static void ExamplesAndRequires()
    {
        var dir = NewDir();
        var modules = Path.Combine(dir, "modules");
        var examples = Directory.GetDirectories(Path.Combine(RepoRoot(), "modules", "examples"));
        foreach (var example in examples) CopyDir(example, Path.Combine(modules, Path.GetFileName(example)));
        void Module(string id, string manifest, string source)
        {
            Directory.CreateDirectory(Path.Combine(modules, id));
            File.WriteAllText(Path.Combine(modules, id, ModuleManifest.FileName), manifest);
            File.WriteAllText(Path.Combine(modules, id, "S.cs"), source);
        }
        Module("mod-base", """{ "id": "mod-base" }""", "public static class Shared { public static string Hello => \"hello\"; } public sealed class B : AlbionLocal.Modules.Module;");
        Module("mod-top", """{ "id": "mod-top", "requires": ["mod-base"] }""", "public sealed class T : AlbionLocal.Modules.Module { protected override void OnLoad() => Log.Info(Shared.Hello); }");
        var ids = examples.Select(Path.GetFileName).Concat(["mod-base", "mod-top"]).ToList();
        var characters = Repository(dir);
        var config = new ServerConfig { SavesPath = dir, Modules = new ModuleOptions { Path = modules, List = [.. ids!] } };
        var world = new World(config, characters, Items("1000\tT4_ORE\tsimpleitem\t\t4")) { Sync = new object(), Clock = new ManualClock() };
        world.Modules.LoadAll();
        foreach (var id in ids) Check(world.Modules.IsLoaded(id!), $"{id} builds and loads (state: {world.Modules.StateOf(id!)})");
        Check(world.Modules.Unload("mod-base", "test") && !world.Modules.IsLoaded("mod-top"), "what requires a module goes with it");
        Check(world.Modules.ReloadNow("mod-base") is null && world.Modules.ReloadNow("mod-top") is null && world.Modules.IsLoaded("mod-top"), "they come back in order");
        Check(world.Modules.ReloadNow("mod-base") is null && world.Modules.IsLoaded("mod-top"), "reloading the required one brings the other back too");
        world.Modules.Shutdown();
        world.Dispose();
        characters.Dispose();
        Clean(dir);
    }

    private static void ApiContract()
    {
        var file = Path.Combine(RepoRoot(), "docs", "modules", "api-baseline.txt");
        Check(File.Exists(file), $"{file} exists (AlbionLocal.Server --modules-api-baseline writes it)");
        var now = ModuleApiSurface.Lines().ToHashSet(StringComparer.Ordinal);
        var then = File.ReadAllLines(file).Where(l => l.Length > 0).ToList();
        var gone = then.Where(l => !now.Contains(l)).ToList();
        Check(gone.Count == 0, "the module API lost or changed these members, which breaks modules:\n  " + string.Join("\n  ", gone)
            + "\nKeep them (an overload or a new member is fine). A deliberate break raises ModuleApi.Major, then: AlbionLocal.Server --modules-api-baseline docs/modules/api-baseline.txt");
        // The hooks hand out what a thing is, not a name to take apart (docs/modules/README.md "Types, not text").
        string[] typed = ["Item", "Spell", "RootSpell", "Channel", "Effect", "Buff", "Mount", "GallopBuff", "From", "To", "Slot", "Cause", "Action", "Kind", "Source", "Container"];
        var raw = typeof(ModuleApi).Assembly.GetExportedTypes().Where(t => t.Name.EndsWith("Args", StringComparison.Ordinal))
            .SelectMany(t => t.GetProperties().Where(p => p.PropertyType == typeof(string) && typed.Contains(p.Name)).Select(p => $"{t.Name}.{p.Name}")).ToList();
        Check(raw.Count == 0, "hook args hand out a type or an enum here, not a string: " + string.Join(", ", raw));
        var added = now.Except(then).Count();
        if (added > 0) Console.WriteLine($"      (the module API has {added} member(s) more than docs/modules/api-baseline.txt: --modules-api-baseline adds them)");
    }

    /// <summary>Dispatch methods that are the host's own plumbing, not called from the game's code.</summary>
    private static readonly HashSet<string> Plumbing =
    [
        "Activate", "AddScript", "BaseName", "TooDeep", "RefusedInCombatHook", "EndModuleCast", "CheckHookId", "Guard", "MonitorChanged", "RegisterCommand", "RegisterScript", "Trace", "Wrap", "WrapOrNull", "Unwrap", "Find", "GetData", "SetData", "RemoveData", "AddTimer",
        "DefineMob", "RemoveTimer", "PassivesChanged", "Mask", "Nearest", "Ordered", "RequireCurrent", "UnitEvent", "MountEvent", "HealthOf", "EnergyOf", "WantsDamageLate", "Update", "Started", "AfterDrop", "AfterLoad",
    ];

    private static void SeamContract()
    {
        var server = Path.Combine(RepoRoot(), "src", "AlbionLocal.Server");
        var modulesDir = Path.Combine(server, "Modules") + Path.DirectorySeparatorChar;
        var files = Directory.GetFiles(server, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
        var game = string.Join("\n", files.Where(f => !f.StartsWith(modulesDir, StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
        var host = string.Join("\n", files.Where(f => f.StartsWith(modulesDir, StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));

        var missing = typeof(ModuleHost).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(m => m is { IsAssembly: true, IsSpecialName: false } && !Plumbing.Contains(m.Name))
            .Select(m => m.Name).Distinct()
            .Where(name => !Regex.IsMatch(game, $@"(?i:modules)\??\.{name}\b|ModuleHost\.{name}\b")).ToList();
        Check(missing.Count == 0, "the game's code no longer calls these module dispatch methods: " + string.Join(", ", missing)
            + "\nIf you moved the code around such a call (world.Modules.X), move the call with it; docs/modules/CONTRIBUTING.md says more.");

        var registry = typeof(ModuleHost).Assembly.GetType("AlbionLocal.Server.Modules.HookRegistry")!;
        var points = registry.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.PropertyType.IsGenericType).Select(p => p.Name).ToList();
        var silent = points.Where(p => !Regex.IsMatch(host + game, $@"\b(Hooks|hooks|point|change)\.{p}\b")).ToList();
        Check(points.Count > 50 && silent.Count == 0, $"hooks nothing raises ({points.Count} hooks in all): {string.Join(", ", silent)}");
    }
}
