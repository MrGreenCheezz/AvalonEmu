using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlbionLocal.Photon;
using AlbionLocal.Modules;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using IUnit = AlbionLocal.Modules.IUnit;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// The C# modules of one world (docs/modules/README.md, analysis/work/stageN/modules/SPEC.md): finds them (Modules:Path and the modules/
/// of the packs in Mods:Packs), builds them (<see cref="ModuleBuilder"/>), loads each in its own collectible <see cref="ModuleLoadContext"/>,
/// and dispatches the game's events to their handlers. Everything runs on the world thread under <see cref="World.Sync"/>, but a reload's
/// build, which runs on a pool thread and comes back with <see cref="World.Post"/>. Every call into a module goes through
/// <see cref="Guard(ModuleContext, string, Action)"/>: an exception is logged and counted, Modules:MaxErrors of them disable the module, and
/// a call slower than Modules:SlowMs is logged; the game goes on either way. With no module loaded every event is one array-length check.
/// </summary>
public sealed partial class ModuleHost
{
    private readonly List<ModuleContext> _loaded = [];
    private ModuleContext[] _snapshot = [];
    private readonly Dictionary<string, ModuleManifest> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleCommand> _commands = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Player, PlayerFacade> _players = new();
    private readonly ConditionalWeakTable<Mob, MobFacade> _mobs = new();

    internal ModuleHost(World world)
    {
        World = world;
        Server = new ServerFacade(world, this);
        Hooks = new HookRegistry(this);
        Game = new GameplayFacade(world, this);
        Types = new ModuleTypes(world);
        world.Maps.Modules = this;
    }

    /// <summary>The hook points (docs/modules/README.md "Hooks").</summary>
    internal HookRegistry Hooks { get; }

    /// <summary>The server's templates and words as the types modules get.</summary>
    internal ModuleTypes Types { get; }

    /// <summary>The gameplay actions (<see cref="IGameplay"/>), one per host.</summary>
    internal GameplayFacade Game { get; }

    public World World { get; }

    internal IServer Server { get; }

    public ModuleOptions Options => World.Config.Modules;

    /// <summary>data/modules: each module's data folder (storage.json and any file of its own).</summary>
    public string DataRoot => Path.Combine(World.Config.SavesPath, "modules");

    /// <summary>data/modules-bin: each module's generated project and build output.</summary>
    public string BinRoot => Path.Combine(World.Config.SavesPath, "modules-bin");

    /// <summary>The names of the loaded modules, in load order.</summary>
    public IReadOnlyList<string> Loaded => [.. _loaded.Where(c => !c.Dropped).Select(c => c.Name)];

    /// <summary>Whether module <paramref name="name"/> is loaded (and not disabled).</summary>
    public bool IsLoaded(string name) => _loaded.Any(c => !c.Dropped && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The error count of loaded module <paramref name="name"/> (0 when not loaded).</summary>
    public int ErrorsOf(string name) => _loaded.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Errors ?? 0;

    /// <summary>What happened to module <paramref name="name"/> last ("loaded", "disabled: …", "failed: …"); null when never touched.</summary>
    public string? StateOf(string name) => _state.GetValueOrDefault(name);

    private void Resnap() => _snapshot = [.. _loaded.Where(c => !c.Dropped)];

    internal IPlayer Wrap(Player p) => _players.GetValue(p, static x => new PlayerFacade(x));

    internal IMob Wrap(Mob m) => _mobs.GetValue(m, static x => new MobFacade(x));

    // ---- discovery and loading

    /// <summary>Reads the module folders again (Modules:Path); the known manifests, by id.</summary>
    public IReadOnlyList<ModuleManifest> Discover()
    {
        var found = ModuleManifest.Discover(Options.Path);
        _known.Clear();
        foreach (var m in found) _known[m.Name] = m;
        return found;
    }

    /// <summary>
    /// At the start (Modules:Enabled): finds the modules, picks those of Modules:List (or, with none listed, those whose module.jsonc is enabled) and not turned off in
    /// config/modules/&lt;id&gt;.jsonc, and loads them dependencies first, building what changed. Synchronous: the server is not running yet. Under <see cref="World.Sync"/>.
    /// </summary>
    public void LoadAll()
    {
        if (!Options.Enabled) return;
        var found = Discover();
        var listed = Options.List;
        foreach (var missing in listed.Where(n => !_known.ContainsKey(n))) Log.Warn("modules", $"Modules:List names {missing}, which is in no module folder ({Options.Path})");
        var picked = found.Where(m => listed.Count > 0 ? listed.Contains(m.Name, StringComparer.OrdinalIgnoreCase) : m.Enabled).ToList();
        foreach (var m in found.Except(picked)) _state[m.Name] = "not enabled";
        foreach (var m in picked) ModuleSettings.EnsureOwnCopy(m, Options.ConfigPath);
        foreach (var m in picked.Where(m => ModuleSettings.DisabledByOwner(m, Options.ConfigPath)).ToList())
        {
            _state[m.Name] = $"turned off in {ModuleSettings.Files(m, Options.ConfigPath).Own}";
            picked.Remove(m);
        }
        if (found.Count > 0) Log.Info("modules", $"{found.Count} module(s) found, {picked.Count} to load: {string.Join(", ", picked.Select(m => m.Name))}");
        foreach (var m in Ordered(picked))
            if (LoadNow(m) is { } why) Log.Error("modules", $"{m.Name} not loaded: {why}");
    }

    /// <summary><paramref name="list"/> with every module after the ones it requires or loads after that are in the list too (a cycle: in file order).</summary>
    internal static List<ModuleManifest> Ordered(List<ModuleManifest> list)
    {
        var done = new List<ModuleManifest>();
        var left = new List<ModuleManifest>(list);
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(m => m.Requires.Concat(m.LoadAfter).All(d => done.Any(x => x.Name.Equals(d, StringComparison.OrdinalIgnoreCase))
                || !list.Any(x => x.Name.Equals(d, StringComparison.OrdinalIgnoreCase)))) ?? left[0];
            done.Add(next);
            left.Remove(next);
        }
        return done;
    }

    /// <summary>Why <paramref name="m"/> cannot load now (API version, a dependency not loaded, already loaded); null when it can.</summary>
    private string? Refusal(ModuleManifest m)
    {
        if (IsLoaded(m.Name)) return "it is loaded already";
        if (!ModuleApi.Supports(m.Api, out var api)) return api;
        if (m.Requires.FirstOrDefault(d => !IsLoaded(d)) is { } dep) return $"it requires module {dep}, which is not loaded{(_known.ContainsKey(dep) ? $" (.modules enable {dep})" : " and not in the modules folder")}";
        return null;
    }

    /// <summary>Loads <paramref name="m"/> now: checks, build (synchronous, may take seconds), load. Null when loaded, else why not.</summary>
    public string? LoadNow(ModuleManifest m, bool force = false)
    {
        if (Refusal(m) is { } why) return Failed(m.Name, why);
        var (dll, error) = ModuleBuilder.Resolve(m, BinRoot, Options.Build, force);
        return dll is null ? Failed(m.Name, error!) : Activate(m, dll);
    }

    private string Failed(string name, string why)
    {
        _state[name] = "failed: " + why.Split('\n')[0];
        return why;
    }

    /// <summary>Loads the built <paramref name="dll"/> of <paramref name="m"/> into a new load context and starts its scripts.</summary>
    internal string? Activate(ModuleManifest m, string dll)
    {
        if (Refusal(m) is { } why) return Failed(m.Name, why);
        var alc = new ModuleLoadContext(m.Name, dll, m.Directory, m.CoreAccess, RequiredAssembly(m));
        System.Reflection.Assembly asm;
        List<ModuleScript> scripts;
        try
        {
            asm = alc.LoadMain();
            scripts = [.. asm.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(ModuleScript)) && t.GetConstructor(Type.EmptyTypes) is not null)
                .OrderBy(t => t.FullName, StringComparer.Ordinal).Select(t => (ModuleScript)Activator.CreateInstance(t)!)];
            if (scripts.Count == 0)
            {
                alc.Unload();
                return Failed(m.Name, $"{Path.GetFileName(dll)} has no script: a class deriving from Module, PlayerScript, CombatScript or another *Script, with a parameterless constructor");
            }
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException or TargetInvocationException or TypeLoadException or MissingMethodException)
        {
            alc.Unload();
            return Failed(m.Name, $"{Path.GetFileName(dll)}: {(e is ReflectionTypeLoadException r ? string.Join("; ", r.LoaderExceptions.Select(x => x?.Message).Distinct()) : (e.InnerException ?? e).Message)}");
        }
        return Start(scripts, m, alc, asm);
    }

    /// <summary>The assemblies of the modules <paramref name="m"/> requires, for its load context.</summary>
    private Func<AssemblyName, System.Reflection.Assembly?> RequiredAssembly(ModuleManifest m) => name =>
        m.Requires.Select(r => _loaded.Find(c => !c.Dropped && c.Name.Equals(r, StringComparison.OrdinalIgnoreCase))?.Assembly)
            .FirstOrDefault(a => a is not null && a.GetName().Name == name.Name);

    /// <summary>Starts scripts made elsewhere as one module (the self-test's in-process modules: no load context).</summary>
    public string? Load(string id, params ModuleScript[] scripts)
    {
        var m = new ModuleManifest { Id = id, Directory = Path.Combine(Options.Path, id) };
        return Refusal(m) is { } why ? Failed(m.Name, why) : Start(scripts, m, null, null);
    }

    private string? Start(IReadOnlyList<ModuleScript> scripts, ModuleManifest m, ModuleLoadContext? alc, System.Reflection.Assembly? assembly)
    {
        var ctx = new ModuleContext(this, m, scripts, alc, assembly);
        _loaded.Add(ctx);
        Resnap();
        foreach (var warning in m.Warnings()) Log.Warn("module:" + m.Name, warning);
        ctx.IdChecks = []; // after the OnLoads: one of them may define a mob type that a script's [ForId] names
        foreach (var s in scripts) Prepare(ctx, s);
        foreach (var s in scripts)
        {
            if (Guard(ctx, $"{s.GetType().Name}.OnLoad", s.OnLoad, count: false)) continue;
            var why = ctx.LastError ?? "OnLoad threw";
            Teardown(ctx, "its OnLoad failed", callUnload: false);
            return Failed(m.Name, $"{s.GetType().Name}.OnLoad threw: {why}");
        }
        if (ctx.Dropped) return Failed(m.Name, ctx.LastError ?? "dropped while loading");
        var checks = ctx.IdChecks;
        ctx.IdChecks = null;
        foreach (var check in checks!) check();
        _state[m.Name] = "loaded";
        AfterLoad(ctx);
        Log.Info("modules", $"loaded {m.Name} {m.Version}{(m.Author.Length > 0 ? $" by {m.Author}" : "")}{(m.CoreAccess ? " (core access: may break with a server update)" : "")}: {Subscriptions(ctx)}");
        return null;
    }

    /// <summary>Gives <paramref name="script"/> its context and connects the hook methods it overrides (with their [ForId], [Priority], [ReceiveCancelled]).</summary>
    private void Prepare(ModuleContext ctx, ModuleScript script)
    {
        script.Attach(ctx);
        var type = script.GetType();
        var classIds = type.GetCustomAttributes<ForIdAttribute>(inherit: false).Select(a => a.Id).ToList();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var definition = method.GetBaseDefinition();
            if (method.DeclaringType == definition.DeclaringType || definition.GetCustomAttribute<HookAttribute>() is not { } hook) continue; // only what is overridden
            if (Hooks.Find(hook.Name) is not { } point)
            {
                Log.Error("module:" + ctx.Name, $"{type.Name}.{method.Name}: this server has no hook {hook.Name}");
                continue;
            }
            var handler = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(point.ArgsType), script, method);
            var ids = method.GetCustomAttributes<ForIdAttribute>(inherit: false).Select(a => a.Id).ToList();
            if (ids.Count == 0) ids = classIds;
            var priority = method.GetCustomAttribute<PriorityAttribute>()?.Priority ?? HookPriority.Normal;
            var receives = method.GetCustomAttribute<ReceiveCancelledAttribute>() is not null;
            foreach (var id in ids.Count == 0 ? [null] : ids.Select(i => (string?)i))
            {
                var registration = point.AddDelegate(ctx, id, handler, priority);
                if (receives) registration.ReceiveCancelled();
            }
        }
    }

    /// <summary>A script the module made itself (<see cref="IModuleContext.AddScript"/>): connected and loaded now.</summary>
    internal void AddScript(ModuleContext ctx, ModuleScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (ctx.Dropped) return;
        ctx.Scripts.Add(script);
        Prepare(ctx, script);
        Guard(ctx, $"{script.GetType().Name}.OnLoad", script.OnLoad);
    }

    private string Subscriptions(ModuleContext ctx)
    {
        var hooks = Hooks.Of(ctx).ToList();
        if (_scripts.Values.Any(s => s.Owner == ctx)) hooks.Add($"custom spells {string.Join(", ", _scripts.Where(s => s.Value.Owner == ctx).Select(s => s.Key))}");
        var commands = _commands.Values.Where(c => c.Owner == ctx).Select(c => "." + c.Path).ToList();
        return $"{(hooks.Count == 0 ? "no hooks" : string.Join(", ", hooks))}; {(commands.Count == 0 ? "no commands" : string.Join(", ", commands))}; {ctx.TimerList.Count} timer(s)";
    }

    /// <summary>Unloads module <paramref name="name"/> now (its Unload, storage saved, handlers, commands and timers dropped, its load context unloaded).</summary>
    public bool Unload(string name, string why)
    {
        if (_loaded.FirstOrDefault(c => !c.Dropped && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } ctx) return false;
        Teardown(ctx, why, callUnload: true);
        _state[ctx.Name] = "disabled: " + why;
        return true;
    }

    /// <summary>Stops dispatching to <paramref name="ctx"/> at once (its commands go too); the rest of its teardown may follow later.</summary>
    private void Drop(ModuleContext ctx)
    {
        if (ctx.Dropped) return;
        ctx.Dropped = true;
        foreach (var key in _commands.Where(c => c.Value.Owner == ctx).Select(c => c.Key).ToList()) _commands.Remove(key);
        AfterDrop(ctx);
        Resnap();
    }

    /// <summary>The loaded modules that require <paramref name="name"/>, directly or not, the farthest first: they go before it and come back after it.</summary>
    private List<ModuleContext> Dependents(string name)
    {
        var found = new List<ModuleContext>();
        void Walk(string of)
        {
            foreach (var c in _loaded.Where(c => !c.Dropped && c.Manifest.Requires.Contains(of, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                Walk(c.Name);
                if (!found.Contains(c)) found.Add(c);
            }
        }
        Walk(name);
        return found;
    }

    private void Teardown(ModuleContext ctx, string why, bool callUnload)
    {
        foreach (var d in Dependents(ctx.Name))
        {
            Teardown(d, $"it requires {ctx.Name}, which goes ({why})", callUnload: true);
            _state[d.Name] = $"unloaded: it requires {ctx.Name}";
        }
        var alc = ctx.Alc;
        Drop(ctx);
        ctx.Unloading = true; // its OnUnload may still read what it keeps on units
        if (callUnload)
            for (var i = ctx.Scripts.Count - 1; i >= 0; i--) Guard(ctx, $"{ctx.Scripts[i].GetType().Name}.OnUnload", ctx.Scripts[i].OnUnload, count: false);
        ctx.StorageImpl.Save();
        ctx.Release();
        _loaded.Remove(ctx);
        alc?.Unload();
        Log.Info("modules", $"unloaded {ctx.Name}: {why}");
    }

    /// <summary>
    /// Reloads module <paramref name="name"/>: unloads it, reads its folder again, then builds (on a pool thread) and loads it in the next tick;
    /// <paramref name="done"/> gets the outcome (null = loaded) on the world thread. A module not loaded is just loaded (enable).
    /// </summary>
    public void Reload(string name, Action<string?> done)
    {
        var dependents = Dependents(name).Select(c => c.Name).Reverse().ToList();
        var finish = done;
        done = why =>
        {
            finish(why);
            if (why is null)
                foreach (var d in dependents) Reload(d, again => { if (again is not null) Log.Error("modules", $"{d} not loaded again after {name}: {again}"); });
        };
        Unload(name, "reload");
        Discover();
        if (!_known.TryGetValue(name, out var m))
        {
            done(Failed(name, $"no module {name} in {Options.Path}{(Nearest(name, _known.Keys) is { } near ? $" (did you mean {near}?)" : "")}"));
            return;
        }
        if (Refusal(m) is { } why)
        {
            done(Failed(name, why));
            return;
        }
        _state[name] = "building";
        Task.Run(() => ModuleBuilder.Resolve(m, BinRoot, Options.Build)).ContinueWith(t => World.Post(() =>
        {
            if (!t.IsCompletedSuccessfully) done(Failed(name, t.Exception?.InnerException?.Message ?? "the build failed"));
            else if (t.Result.Dll is null) done(Failed(name, t.Result.Error!));
            else done(Activate(m, t.Result.Dll));
        }), TaskScheduler.Default);
    }

    /// <summary>As <see cref="Reload(string, Action{string?})"/>, but the build runs here (the console's tests): null when loaded, else why not.</summary>
    public string? ReloadNow(string name)
    {
        var dependents = Dependents(name).Select(c => c.Name).Reverse().ToList();
        Unload(name, "reload");
        Discover();
        var why = _known.TryGetValue(name, out var m) ? LoadNow(m) : Failed(name, $"no module {name} in {Options.Path}");
        if (why is null)
            foreach (var d in dependents)
                if (_known.TryGetValue(d, out var dm)) LoadNow(dm);
        return why;
    }

    /// <summary>One line per module found: name, version, state, errors, origin.</summary>
    public IEnumerable<string> Describe()
    {
        if (_known.Count == 0) Discover();
        var names = _known.Keys.Concat(_loaded.Select(c => c.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var ctx = _loaded.FirstOrDefault(c => !c.Dropped && c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            var m = ctx?.Manifest ?? _known.GetValueOrDefault(n);
            var state = ctx is not null ? "loaded" : _state.GetValueOrDefault(n) ?? (m?.Enabled == true ? "not loaded" : "not enabled");
            yield return $"{n} {m?.Version}: {state}{(ctx is { Errors: > 0 } ? $", {ctx.Errors} error(s)" : "")}"
                + (m is { CoreAccess: true } ? " (core access)" : "") + (m is { Description.Length: > 0 } ? $" - {m.Description}" : "");
        }
    }

    // ---- the world thread's hooks

    /// <summary>The server has started: Server.OnStarted.</summary>
    internal void Started() => Hooks.Started.Raise(null, new ServerArgs());

    /// <summary>The server stops: Server.OnStopping, then every module unloaded (last loaded first), its storage saved.</summary>
    public void Shutdown()
    {
        Hooks.Stopping.Raise(null, new ServerArgs());
        for (var i = _loaded.Count - 1; i >= 0; i--)
            if (i < _loaded.Count) Teardown(_loaded[i], "the server stops", callUnload: true);
    }

    /// <summary>Every tick (<see cref="World.Update"/>): the timers that are due, Tick, the storage writes that are due.</summary>
    internal void Update(TimeSpan now)
    {
        if (_snapshot.Length == 0) return;
        foreach (var ctx in _snapshot)
        {
            if (ctx.Dropped) continue;
            var timers = ctx.TimerList;
            for (int i = 0, n = timers.Count; i < n && i < timers.Count && !ctx.Dropped; i++)
            {
                var t = timers[i];
                if (t.Cancelled || now < t.Due) continue;
                if (t.Interval is { } every) t.Due = t.Due + every > now ? t.Due + every : now + every;
                else t.Cancelled = true;
                Guard(ctx, "timer", t.Action);
            }
            timers.RemoveAll(t => t.Cancelled);
            if (ctx.StorageImpl.Due(StorageDelay)) ctx.StorageImpl.Save();
        }
        if (Hooks.ServerTick.Any) Hooks.ServerTick.Raise(null, new TickArgs(now.TotalSeconds, World.Delta.TotalSeconds));
    }

    /// <summary>How long after a change a module's storage is written.</summary>
    public static readonly TimeSpan StorageDelay = TimeSpan.FromSeconds(5);

    // ---- safety

    /// <summary>
    /// Runs <paramref name="action"/> of <paramref name="ctx"/>: an exception is logged and (<paramref name="count"/>) counted, the
    /// Modules:MaxErrors-th disables the module (dispatch stops at once, the unload follows in the next tick); a run slower than Modules:SlowMs
    /// is logged (once a minute per module). False when it threw.
    /// </summary>
    internal bool Guard(ModuleContext ctx, string what, Action action, bool count = true)
    {
        var start = Stopwatch.GetTimestamp();
        var outer = _current;
        _current = ctx;
        try
        {
            action();
            return true;
        }
        catch (Exception e)
        {
            Fault(ctx, what, e, count);
            return false;
        }
        finally
        {
            _current = outer;
            Slow(ctx, what, start);
        }
    }

    /// <summary>The module whose code runs now on this thread (set by every guarded call): the owner of a unit's data bag or timer.</summary>
    [ThreadStatic] private static ModuleContext? _current;

    internal static ModuleContext? Current => _current;

    /// <summary><see cref="Current"/>, or an error for a data bag or entity timer touched outside a module's own call.</summary>
    internal static ModuleContext RequireCurrent() =>
        _current is { } c && (!c.Dropped || c.Unloading) ? c : throw new InvalidOperationException("unit data and timers can be used only from a module's own handlers, timers and commands");

    /// <summary>A hook handler of <paramref name="ctx"/>, guarded as every module call.</summary>
    internal bool Guard<T>(ModuleContext ctx, string what, Action<T> handler, T args)
    {
        var start = Stopwatch.GetTimestamp();
        var outer = _current;
        _current = ctx;
        try
        {
            handler(args);
            return true;
        }
        catch (Exception e)
        {
            Fault(ctx, what, e, count: true);
            return false;
        }
        finally
        {
            _current = outer;
            Slow(ctx, what, start);
        }
    }

    private void Slow(ModuleContext ctx, string what, long start)
    {
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (ms <= Options.SlowMs || Environment.TickCount64 - ctx.LastSlowWarning < 60_000) return;
        ctx.LastSlowWarning = Environment.TickCount64;
        Log.Warn("module:" + ctx.Name, string.Create(CultureInfo.InvariantCulture,
            $"{what} took {ms:0} ms (Modules:SlowMs {Options.SlowMs}): it holds up the whole world; keep handlers short and move slow work off the world thread"));
    }

    private void Fault(ModuleContext ctx, string what, Exception e, bool count)
    {
        ctx.LastError = $"{e.GetType().Name}: {e.Message}";
        if (!count)
        {
            Log.Error("module:" + ctx.Name, $"{what} threw: {e}");
            return;
        }
        ctx.Errors++;
        Log.Error("module:" + ctx.Name, $"{what} threw ({ctx.Errors}/{Options.MaxErrors}): {e}");
        if (ctx.Errors < Options.MaxErrors || ctx.Dropped) return;
        Log.Error("modules", $"{ctx.Name} disabled: {ctx.Errors} errors (Modules:MaxErrors); .modules reload {ctx.Name} loads it again");
        Drop(ctx);
        _state[ctx.Name] = $"disabled: {ctx.Errors} errors, the last {ctx.LastError}";
        World.Post(() =>
        {
            if (_loaded.Contains(ctx)) Teardown(ctx, "too many errors", callUnload: true);
            _state[ctx.Name] = $"disabled: {ctx.Errors} errors, the last {ctx.LastError}";
        });
    }

    // ---- dispatch of the player, mob, item and social hooks (the game's systems call these; each is one check when nobody listens)

    internal void PlayerLogin(Player p, bool first)
    {
        if (Hooks.Login.Any && !p.IsBot) Hooks.Login.Raise(null, new PlayerLoginArgs(Wrap(p), first));
    }

    internal void PlayerLogout(Player p)
    {
        if (Hooks.Logout.Any && !p.IsBot) Hooks.Logout.Raise(null, new PlayerArgs(Wrap(p)));
    }

    internal void ZoneEnter(Player p, bool clusterChange)
    {
        if (Hooks.EnterZone.Any && !p.IsBot && p.Map is { } map) Hooks.EnterZone.Raise(map.Cluster, new ZoneArgs(Wrap(p), Types.Zone(map), clusterChange));
    }

    internal void ZoneLeave(Player p, bool clusterChange)
    {
        if (Hooks.LeaveZone.Any && !p.IsBot && p.Map is { } map) Hooks.LeaveZone.Raise(map.Cluster, new ZoneArgs(Wrap(p), Types.Zone(map), clusterChange));
    }

    internal void MobSpawned(Mob m)
    {
        if (Hooks.MobSpawned.Wants(m.Template.Name)) Hooks.MobSpawned.Raise(m.Template.Name, new MobArgs(Wrap(m)));
    }

    internal void MobDied(Mob m, Player killer, IReadOnlyList<Player> credited)
    {
        EndModuleCast(m, death: true);
        if (Hooks.MobDied.Wants(m.Template.Name)) Hooks.MobDied.Raise(m.Template.Name, new MobDeathArgs(Wrap(m), Wrap(killer), [.. credited.Where(c => !c.IsBot).Select(Wrap)]));
    }

    internal void PlayerDown(Player p, long killer, string killerText, bool died)
    {
        var point = died ? Hooks.PlayerDied : Hooks.KnockedDown;
        if (!point.Any || p.IsBot) return;
        IUnit? unit = p.Map?.FindPlayer(killer) is { } kp ? Wrap(kp) : p.Map?.Objects.GetValueOrDefault(killer) is Mob km ? Wrap(km) : null;
        point.Raise(null, new PlayerDownArgs(Wrap(p), killerText, unit));
    }

    internal void ItemUsed(Player p, string item, int count, string source)
    {
        if (Hooks.Used.Wants(item) && !p.IsBot) Hooks.Used.Raise(item, new ItemUsedArgs(Wrap(p), Types.Item(item), source == "slot"));
    }

    internal void ItemCrafted(Player p, string item, int count, string building)
    {
        if (Hooks.Crafted.Wants(item) && !p.IsBot) Hooks.Crafted.Raise(item, new ItemCraftedArgs(Wrap(p), Types.Item(item), count, building));
    }

    /// <summary>Mobs.OnLootRolled: the drop as the handlers leave it (<paramref name="drops"/> itself when none listens).</summary>
    internal IReadOnlyList<(string Item, int Amount)> LootRolled(Mob m, Player killer, IReadOnlyList<(string Item, int Amount)> drops)
    {
        if (!Hooks.LootRolled.Wants(m.Template.Name)) return drops;
        var args = new LootArgs(Wrap(m), Wrap(killer), Loot(drops));
        Hooks.LootRolled.Raise(m.Template.Name, args);
        return Loot(args.Items);
    }

    private LootList Loot(IReadOnlyList<(string Item, int Amount)> drops) => new(drops.Select(d => new LootItem(Types.Item(d.Item), d.Amount)), Types.FindOrHint);

    private static IReadOnlyList<(string Item, int Amount)> Loot(LootList items) => [.. items.Where(i => i is { Count: > 0, Item: not null }).Select(i => (i.Item.Name, i.Count))];

    internal void Harvested(Player p, string item, int count, string kind)
    {
        if (Hooks.Harvested.Wants(item) && !p.IsBot) Hooks.Harvested.Raise(item, new HarvestArgs(Wrap(p), Types.Item(item), count, kind == "fish" ? HarvestKind.Fish : HarvestKind.Gather));
    }

    internal void GoalCompleted(Player p, string kind, string description)
    {
        if (Hooks.Goal.Wants(kind) && !p.IsBot) Hooks.Goal.Raise(kind, new GoalArgs(Wrap(p), ModuleTypes.Goal(kind), description));
    }

    internal void BuildingAction(Player p, string building, string action, bool accepted)
    {
        if (Hooks.Station.Wants(building) && !p.IsBot) Hooks.Station.Raise(building, new BuildingActionArgs(Wrap(p), building, ModuleTypes.Station(action), accepted));
    }

    internal void Party(bool joined, Guid id, string name, int partyId, int members, string reason)
    {
        var point = joined ? Hooks.PartyJoined : Hooks.PartyLeft;
        if (!point.Any) return;
        var online = World.Parties.Online(id);
        point.Raise(null, new PartyArgs(id, name, online is null || online.IsBot ? null : Wrap(online), partyId, members, ModuleTypes.Party(reason)));
    }

    /// <summary>Social.OnBeforeChat for a Say line about to go out: true = dropped (the reason told to the player). The line may come back changed in <paramref name="text"/>.</summary>
    internal bool BeforeChat(Player p, ref string? text)
    {
        if (!Hooks.Chat.Any || p.IsBot) return false;
        var args = new ChatArgs(Wrap(p), text ?? "");
        Hooks.Chat.Raise(null, args);
        if (args.IsCancelled)
        {
            if (args.CancelReason is { Length: > 0 } why) p.Session.Send(new Protocol.SystemMessageEvent { d0p = why, d0q = 1 });
            return true;
        }
        text = args.Text;
        return false;
    }

    /// <summary>
    /// Combat.OnBeforeDamage of a hit of <paramref name="amount"/>: true when it is cancelled (a spell's <paramref name="hit"/> animation still goes out, nothing else
    /// of the hit happens). The amount may come back changed.
    /// </summary>
    internal bool BeforeDamage(Player attacker, Mob target, ref float amount, EffectType type, HitOrigin origin, short spell, AlbionEvent? hit)
    {
        if (!Hooks.BeforeDamage.Wants(target.Template.Name)) return false;
        return RaiseDamage(Wrap(attacker), Wrap(target), DamageKind.PlayerToMob, target.Template.Name, target.Health.At(World.GameNow), ref amount, type, origin, spell, hit, target);
    }

    internal bool BeforeDamage(Mob attacker, Player target, ref float amount, EffectType type, HitOrigin origin, short spell, AlbionEvent? hit)
    {
        if (!Hooks.BeforeDamage.Any) return false;
        return RaiseDamage(Wrap(attacker), Wrap(target), DamageKind.MobToPlayer, null, target.Health.At(World.GameNow), ref amount, type, origin, spell, hit, target);
    }

    /// <summary>
    /// A hit between players, before the attack's bookkeeping (<see cref="Pvp.Engage"/>): the handlers see what the hit will come to, <paramref name="amount"/> stays what
    /// goes in at the top of <see cref="Pvp.Damage"/>, so that with none listening or none changing it nothing differs.
    /// </summary>
    internal bool BeforeDamage(Player attacker, Player target, ref double amount, long ticks, EffectType type, HitOrigin origin, short spell, AlbionEvent? hit)
    {
        if (!Hooks.BeforeDamage.Any) return false;
        var factor = Pvp.DefenseFactor(target, ticks) * PvpDetails.ResilienceInForce(attacker, target, ticks)
            * (attacker.IsBot ? World.Config.Bots.DamageFactor * World.Config.Bots.Difficulty * attacker.Bot!.DamageFactor : 1);
        var shown = Math.Max(0, Combat.Rounded(amount * factor));
        var changed = shown;
        if (RaiseDamage(Wrap(attacker), Wrap(target), DamageKind.PlayerToPlayer, null, target.Health.At(ticks), ref changed, type, origin, spell, hit, target)) return true;
        if (changed != shown) amount = factor > 0 ? changed / factor : 0;
        return false;
    }

    private bool RaiseDamage(IUnit attacker, IUnit target, DamageKind kind, string? id, float health, ref float amount, EffectType type, HitOrigin origin, short spell, AlbionEvent? hit, Entities.IUnit shown)
    {
        var args = new BeforeDamageArgs(attacker, target, kind, (DamageOrigin)origin, type == EffectType.Magic, Types.OfId(spell), health, amount);
        _combatHooks++;
        Hooks.BeforeDamage.Raise(id, args);
        _combatHooks--;
        if (args.IsCancelled)
        {
            RaiseAvoided(attacker, target, id, HitAvoidReason.Cancelled, args.Origin, args.Spell, args.OriginalAmount, null, null, "");
            if (hit is not null)
            {
                if (shown is Player q) q.SendAround(hit);
                else shown.Map?.SendToObservers((WorldObject)shown, hit);
            }
            return true;
        }
        if (shown is Mob { Dead: true } or Player { Combat.Down: not null } || shown.Map is null) return true; // a handler has ended it meanwhile: nothing left to hit
        if (double.IsFinite(args.Amount)) amount = Combat.Rounded(Math.Max(0, args.Amount));
        return false;
    }

    /// <summary>
    /// A spawn asked for inside a damage or heal hook is refused, with a hint: a hit can run inside a walk over the map's objects, which a new object would break, and the
    /// caller wants the mob at once, so it cannot simply wait as a hit does.
    /// </summary>
    internal bool RefusedInCombatHook(string mob)
    {
        if (!InCombatHook) return false;
        Log.Warn("module:" + (Current?.Name ?? "?"), $"{mob} not spawned: no spawning inside a damage or heal hook. Do it a moment later: Timers.After(TimeSpan.Zero, () => ...)");
        return true;
    }

    /// <summary>Inside a damage or heal hook: <see cref="GameplayFacade"/> then keeps its hits and heals for the next tick, so that a handler never hits into the middle of a hit.</summary>
    internal bool InCombatHook => _combatHooks > 0;

    private int _combatHooks;

    /// <summary>Combat.OnBeforeHeal of a heal of <paramref name="amount"/> on a player: true when it is cancelled. The amount may come back changed.</summary>
    internal bool BeforeHeal(Player healer, Player target, ref double amount, HitOrigin origin, short spell)
    {
        if (!Hooks.BeforeHeal.Any) return false;
        var args = new BeforeHealArgs(Wrap(healer), Wrap(target), (DamageOrigin)origin, Types.OfId(spell), amount);
        _combatHooks++;
        Hooks.BeforeHeal.Raise(null, args);
        _combatHooks--;
        if (args.IsCancelled) return true;
        if (double.IsFinite(args.Amount)) amount = Math.Max(0, args.Amount);
        return false;
    }

    /// <summary>How many load contexts of module <paramref name="id"/> beyond the <paramref name="live"/> ones are still in memory, after a garbage collection.</summary>
    public static int OldCopies(string id, int live)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        var name = "module:" + id;
        return Math.Max(0, System.Runtime.Loader.AssemblyLoadContext.All.Count(a => a.Name == name) - live);
    }

    /// <summary>What <c>.modules info</c> shows of one module.</summary>
    public string Info(string id)
    {
        if (_known.Count == 0) Discover();
        var ctx = _loaded.FirstOrDefault(c => !c.Dropped && c.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        if ((ctx?.Manifest ?? _known.GetValueOrDefault(id)) is not { } m) return $"no module {id}{(Nearest(id, _known.Keys) is { } near ? $" (did you mean {near}?)" : "")}";
        var lines = new List<string>
        {
            $"{m.Id} {m.Version}{(m.Author.Length > 0 ? " by " + m.Author : "")}: {(ctx is not null ? "loaded" : _state.GetValueOrDefault(m.Id) ?? "not loaded")}",
            $"folder {m.Directory}; API {m.Api}{(m.CoreAccess ? "; core access: uses the server's own classes, may break with a server update" : "")}",
        };
        if (m.Description.Length > 0) lines.Add(m.Description);
        if (ctx is not null)
        {
            lines.Add(Subscriptions(ctx));
            lines.Add($"{ctx.Errors} error(s){(ctx.LastError is { } last ? ", the last " + last : "")}");
        }
        var stale = OldCopies(m.Id, ctx is null ? 0 : 1);
        if (stale > 0) lines.Add($"{stale} old copy/copies of its code are still in memory: a static field or something handed to another thread may be holding it (a reload will not free them)");
        return string.Join("\n", lines);
    }

    // ---- what the hook points ask of the host

    /// <summary>How deep hook handlers may run inside each other before a call is dropped: a handler that sets off its own hook would otherwise overflow the stack, which ends the process.</summary>
    internal const int MaxHookDepth = 16;

    /// <summary>How many hook handlers run inside each other now.</summary>
    internal int HookDepth { get; set; }

    /// <summary>A hook call dropped at <see cref="MaxHookDepth"/>: an error of that module, so that a loop ends with it being disabled.</summary>
    internal void TooDeep(ModuleContext ctx, string hook) =>
        Fault(ctx, hook, new InvalidOperationException($"hooks run {MaxHookDepth} deep inside each other: a handler sets off its own hook; this call is dropped"), count: true);

    /// <summary>A Monitor handler changed a cancel: it only watches, so that is undone and logged.</summary>
    internal void MonitorChanged(ModuleContext ctx, string hook, ICancellableArgs args, bool wasCancelled, string? reason, string? by)
    {
        Log.Warn("module:" + ctx.Name, $"{hook}: a Monitor handler changed the cancel; it may only watch (use another priority)");
        if (!wasCancelled)
        {
            args.Uncancel();
            return;
        }
        args.Cancel(reason);
        if (by is not null) args.MarkCancelledBy(by);
    }

    /// <summary>One line of <c>.modules trace</c>.</summary>
    internal void Trace(string hook, string module, string? id, long ticks, ICancellableArgs? cancel) =>
        Log.Info("trace", string.Create(CultureInfo.InvariantCulture, $"{hook}{(id is null ? "" : $" [{id}]")} -> {module} {Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds:0.###} ms{(cancel is { IsCancelled: true } ? $", cancelled by {cancel.CancelledBy}" : "")}"));

    /// <summary>A hook registered for an id the game's data does not know: a warning with the closest name. Only a hint: the data may come later (or from a mod).</summary>
    internal void CheckHookId(ModuleContext ctx, string hook, HookIdKind kind, string id)
    {
        if (ctx.IdChecks is { } later)
        {
            later.Add(() => CheckHookId(ctx, hook, kind, id));
            return;
        }
        var (known, closest) = kind switch
        {
            HookIdKind.Item => (World.Items.Find(id) is not null, World.Items.Closest(id)),
            HookIdKind.Spell when World.Spells is { } spells => (spells.Template(id) is not null, null),
            HookIdKind.Mob when World.Clusters?.MobsIfLoaded is { } mobs => (mobs.Find(id) is not null, null),
            HookIdKind.Operation => (ModuleNetwork.OperationNames.Contains(id), Nearest(id, ModuleNetwork.OperationNames)),
            HookIdKind.Event => (ModuleNetwork.EventNames.Contains(id), Nearest(id, ModuleNetwork.EventNames)),
            _ => (true, (string?)null),
        };
        if (!known) Log.Warn("module:" + ctx.Name, $"{hook}: there is no {kind.ToString().ToLowerInvariant()} \"{id}\"{(closest is null ? "" : $" (did you mean \"{closest}\"?)")}");
    }

    /// <summary>A mask with * and ? as a case-insensitive whole-name pattern.</summary>
    internal static System.Text.RegularExpressions.Regex Mask(string mask) =>
        new("^" + System.Text.RegularExpressions.Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The name of <paramref name="names"/> closest to <paramref name="name"/> (by edit distance, ignoring case); null when none is close.</summary>
    internal static string? Nearest(string name, IEnumerable<string> names)
    {
        string? best = null;
        var bestDistance = Math.Max(2, name.Length / 3) + 1;
        foreach (var candidate in names)
        {
            if (Math.Abs(candidate.Length - name.Length) >= bestDistance) continue;
            var d = Distance(name, candidate);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }
        return best;
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (char.ToUpperInvariant(a[i - 1]) == char.ToUpperInvariant(b[j - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    // ---- commands

    private sealed record ModuleCommand(ModuleContext Owner, string Path, string[] Words, PlayerLevel Level, string Help, Action<ICommandCall> Handler);

    internal void RegisterCommand(ModuleContext ctx, string path, PlayerLevel level, string help, Action<ICommandCall> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var words = path.TrimStart('.').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
        if (words.Length == 0) throw new InvalidOperationException("a command needs a path");
        var key = string.Join(" ", words);
        if (World.Commands.Commands.Any(c => c.Path == key)) throw new InvalidOperationException($".{key} is a built-in command");
        if (_commands.TryGetValue(key, out var other) && !other.Owner.Dropped) throw new InvalidOperationException($".{key} is module {other.Owner.Name}'s");
        if (ctx.Dropped) return;
        _commands[key] = new ModuleCommand(ctx, key, words, level, help ?? "", handler);
    }

    /// <summary>The .help lines of the module commands <paramref name="level"/> may run, starting with <paramref name="words"/>.</summary>
    public IEnumerable<string> HelpLines(Persistence.AccountLevel level, IReadOnlyList<string> words) =>
        _commands.Values.Where(c => !c.Owner.Dropped && (int)c.Level <= (int)level
                && words.Select((w, i) => i < c.Words.Length && c.Words[i].Equals(w, StringComparison.OrdinalIgnoreCase)).All(x => x))
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => $".{c.Path} [args...]{(c.Help.Length > 0 ? " - " + c.Help : "")} (module {c.Owner.Name})");

    /// <summary>
    /// Runs <paramref name="line"/> when a module command matches it with a longer path than any built-in one (<see cref="CommandRegistry.Execute"/>
    /// asks first); false = not a module command. A command that throws is answered as failed and counted as the module's error.
    /// </summary>
    internal bool TryRun(CommandContext c, string line)
    {
        if (_commands.Count == 0) return false;
        var text = line.Trim();
        if (text.StartsWith('.')) text = text[1..];
        var words = Split(text);
        if (words.Count == 0) return false;
        bool Matches(string[] path) => path.Length <= words.Count && path.Select((w, i) => w.Equals(words[i].Text, StringComparison.OrdinalIgnoreCase)).All(x => x);
        var best = _commands.Values.Where(x => !x.Owner.Dropped && Matches(x.Words)).MaxBy(x => x.Words.Length);
        if (best is null) return false;
        if (World.Commands.Commands.Where(b => Matches(b.Words)).Select(b => b.Words.Length).DefaultIfEmpty(0).Max() >= best.Words.Length) return false;
        if ((int)c.Level < (int)best.Level)
        {
            c.Error($".{best.Path} needs level {best.Level} (yours: {c.Level})");
            return true;
        }
        var n = best.Words.Length;
        var call = new CommandCall(this, c.Player is { } p ? Wrap(p) : null, (PlayerLevel)(int)c.Level, c.Console,
            n < words.Count ? text[words[n].Start..].Trim() : "", [.. words.Skip(n).Select(w => w.Text)], c);
        if (!Guard(best.Owner, $"command .{best.Path}", () => best.Handler(call))) c.Error($".{best.Path} failed: {best.Owner.LastError}");
        return true;
    }

    private sealed class CommandCall(ModuleHost host, IPlayer? player, PlayerLevel level, bool console, string args, IReadOnlyList<string> words, CommandContext c) : ICommandCall
    {
        public IPlayer? Player { get; } = player;
        public PlayerLevel Level { get; } = level;
        public bool FromConsole { get; } = console;
        public string Args { get; } = args;
        public IReadOnlyList<string> Words { get; } = words;
        public void Reply(string text) => c.Reply(text);
        public void Error(string text) => c.Error(text);

        private string? Named(int index, string what)
        {
            if (index >= 0 && index < Words.Count) return Words[index];
            Error($"name a {what}");
            return null;
        }

        public IPlayer? PlayerArg(int index)
        {
            if (Named(index, "player") is not { } name) return null;
            if (host.Server.FindPlayer(name) is { } found) return found;
            Error($"no player called {name} is online");
            return null;
        }

        public ItemType? ItemArg(int index)
        {
            if (Named(index, "item") is not { } name) return null;
            if (host.Types.Find(name) is { } found) return found;
            Error($"there is no item {name}{(host.World.Items.Closest(name) is { } near ? $" (did you mean {near}?)" : "")}");
            return null;
        }

        public SpellInfo? SpellArg(int index)
        {
            if (Named(index, "spell") is not { } name) return null;
            if (host.Types.FindSpell(name) is { } found) return found;
            Error($"there is no spell {name}");
            return null;
        }
    }

    /// <summary>The words of a line with where each starts; "a b" in double quotes is one word (as the built-in commands split).</summary>
    private static List<(string Text, int Start)> Split(string line)
    {
        var words = new List<(string, int)>();
        for (var i = 0; i < line.Length;)
        {
            if (char.IsWhiteSpace(line[i]))
            {
                i++;
                continue;
            }
            var start = i;
            if (line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                if (end < 0) end = line.Length;
                words.Add((line[(i + 1)..end], start));
                i = end + 1;
                continue;
            }
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            words.Add((line[start..i], start));
        }
        return words;
    }
}
