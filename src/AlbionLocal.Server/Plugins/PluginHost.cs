using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using AlbionLocal.Photon;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Entities;
using IUnit = AlbionLocal.Plugin.Api.IUnit;

namespace AlbionLocal.Server.Plugins;

/// <summary>
/// The C# plugins of one world (server/plugins/README.md, analysis/work/stageN/plugins/SPEC.md): finds them (Plugins:Path and the plugins/
/// of the packs in Mods:Packs), builds them (<see cref="PluginBuilder"/>), loads each in its own collectible <see cref="PluginLoadContext"/>,
/// and dispatches the game's events to their handlers. Everything runs on the world thread under <see cref="World.Sync"/>, but a reload's
/// build, which runs on a pool thread and comes back with <see cref="World.Post"/>. Every call into a plugin goes through
/// <see cref="Guard(PluginContext, string, Action)"/>: an exception is logged and counted, Plugins:MaxErrors of them disable the plugin, and
/// a call slower than Plugins:SlowMs is logged; the game goes on either way. With no plugin loaded every event is one array-length check.
/// </summary>
public sealed partial class PluginHost
{
    private readonly List<PluginContext> _loaded = [];
    private PluginContext[] _snapshot = [];
    private readonly int[] _counts = new int[PluginContext.EventCount];
    private readonly Dictionary<string, PluginManifest> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginCommand> _commands = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Player, PlayerFacade> _players = new();
    private readonly ConditionalWeakTable<Mob, MobFacade> _mobs = new();

    internal PluginHost(World world)
    {
        World = world;
        Server = new ServerFacade(world, this);
        Hooks = new HookRegistry(this);
        Game = new GameplayFacade(world, this);
    }

    /// <summary>The v2 hook points (server/plugins/README.md "Hooks").</summary>
    internal HookRegistry Hooks { get; }

    /// <summary>The v2 gameplay actions (<see cref="IGameplay"/>), one per host.</summary>
    internal GameplayFacade Game { get; }

    public World World { get; }

    internal IServer Server { get; }

    public PluginOptions Options => World.Config.Plugins;

    /// <summary>data/plugins: each plugin's data folder (storage.json, a config.json of the owner's).</summary>
    public string DataRoot => Path.Combine(World.Config.SavesPath, "plugins");

    /// <summary>data/plugins-bin: each plugin's generated project and build output.</summary>
    public string BinRoot => Path.Combine(World.Config.SavesPath, "plugins-bin");

    /// <summary>The names of the loaded plugins, in load order.</summary>
    public IReadOnlyList<string> Loaded => [.. _loaded.Where(c => !c.Dropped).Select(c => c.Name)];

    /// <summary>Whether plugin <paramref name="name"/> is loaded (and not disabled).</summary>
    public bool IsLoaded(string name) => _loaded.Any(c => !c.Dropped && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The error count of loaded plugin <paramref name="name"/> (0 when not loaded).</summary>
    public int ErrorsOf(string name) => _loaded.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Errors ?? 0;

    /// <summary>What happened to plugin <paramref name="name"/> last ("loaded", "disabled: …", "failed: …"); null when never touched.</summary>
    public string? StateOf(string name) => _state.GetValueOrDefault(name);

    internal bool Wants(PluginEvent e) => _counts[(int)e] > 0;

    internal void Recount()
    {
        Array.Clear(_counts);
        foreach (var c in _loaded)
            if (!c.Dropped)
                for (var e = 0; e < _counts.Length; e++)
                    if (c.Has((PluginEvent)e)) _counts[e]++;
    }

    private void Resnap()
    {
        _snapshot = [.. _loaded.Where(c => !c.Dropped)];
        Recount();
    }

    internal IPlayer Wrap(Player p) => _players.GetValue(p, static x => new PlayerFacade(x));

    internal IMob Wrap(Mob m) => _mobs.GetValue(m, static x => new MobFacade(x));

    // ---- discovery and loading

    /// <summary>Reads the plugin folders again (Plugins:Path and the packs' plugins/); the known manifests, by name.</summary>
    public IReadOnlyList<PluginManifest> Discover()
    {
        var found = PluginManifest.Discover(Options.Path, World.Config.ModsPath, World.Config.ModPacks);
        _known.Clear();
        foreach (var m in found) _known[m.Name] = m;
        return found;
    }

    /// <summary>
    /// At the start (Plugins:Enabled): finds the plugins, picks those of Plugins:List (or, with none listed, those whose plugin.json is enabled),
    /// and loads them dependencies first, building what changed. Synchronous: the server is not running yet. Under <see cref="World.Sync"/>.
    /// </summary>
    public void LoadAll()
    {
        if (!Options.Enabled) return;
        var found = Discover();
        var listed = Options.List;
        foreach (var missing in listed.Where(n => !_known.ContainsKey(n))) Log.Warn("plugins", $"Plugins:List names {missing}, which is in no plugin folder ({Options.Path})");
        var picked = found.Where(m => listed.Count > 0 ? listed.Contains(m.Name, StringComparer.OrdinalIgnoreCase) : m.Enabled).ToList();
        foreach (var m in found.Except(picked)) _state[m.Name] = "not enabled";
        Log.Info("plugins", $"{found.Count} plugin(s) found, {picked.Count} to load: {string.Join(", ", picked.Select(m => m.Name))}");
        foreach (var m in Ordered(picked))
            if (LoadNow(m) is { } why) Log.Error("plugins", $"{m.Name} not loaded: {why}");
    }

    /// <summary><paramref name="list"/> with every plugin after the dependencies it names that are in the list too.</summary>
    private static List<PluginManifest> Ordered(List<PluginManifest> list)
    {
        var done = new List<PluginManifest>();
        var left = new List<PluginManifest>(list);
        while (left.Count > 0)
        {
            var next = left.FirstOrDefault(m => m.Dependencies.All(d => done.Any(x => x.Name.Equals(d, StringComparison.OrdinalIgnoreCase))
                || !list.Any(x => x.Name.Equals(d, StringComparison.OrdinalIgnoreCase)))) ?? left[0]; // a cycle: in file order
            done.Add(next);
            left.Remove(next);
        }
        return done;
    }

    /// <summary>Why <paramref name="m"/> cannot load now (API version, a dependency not loaded, already loaded); null when it can.</summary>
    private string? Refusal(PluginManifest m)
    {
        if (IsLoaded(m.Name)) return "it is loaded already";
        if (m.ApiVersion < PluginApi.MinVersion || m.ApiVersion > PluginApi.Version)
            return $"it is written for plugin API {m.ApiVersion}, this server has API {PluginApi.Version} (and loads {PluginApi.MinVersion}-{PluginApi.Version})";
        if (m.Dependencies.FirstOrDefault(d => !IsLoaded(d)) is { } dep) return $"it needs plugin {dep}, which is not loaded";
        return null;
    }

    /// <summary>Loads <paramref name="m"/> now: checks, build (synchronous, may take seconds), load. Null when loaded, else why not.</summary>
    public string? LoadNow(PluginManifest m, bool force = false)
    {
        if (Refusal(m) is { } why) return Failed(m.Name, why);
        var (dll, error) = PluginBuilder.Resolve(m, BinRoot, Options.Build, force);
        return dll is null ? Failed(m.Name, error!) : Activate(m, dll);
    }

    private string Failed(string name, string why)
    {
        _state[name] = "failed: " + why.Split('\n')[0];
        return why;
    }

    /// <summary>Loads the built <paramref name="dll"/> of <paramref name="m"/> into a new load context and starts its one <see cref="IPlugin"/>.</summary>
    internal string? Activate(PluginManifest m, string dll)
    {
        if (Refusal(m) is { } why) return Failed(m.Name, why);
        var alc = new PluginLoadContext(m.Name, dll, m.Directory);
        IPlugin plugin;
        try
        {
            var asm = alc.LoadMain();
            var types = asm.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null).ToList();
            if (types.Count != 1)
            {
                alc.Unload();
                return Failed(m.Name, types.Count == 0 ? $"{Path.GetFileName(dll)} has no public IPlugin class with a parameterless constructor" : $"{Path.GetFileName(dll)} has {types.Count} IPlugin classes: {string.Join(", ", types.Select(t => t.FullName))}");
            }
            plugin = (IPlugin)Activator.CreateInstance(types[0])!;
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException or TargetInvocationException or TypeLoadException or MissingMethodException)
        {
            alc.Unload();
            return Failed(m.Name, $"{Path.GetFileName(dll)}: {(e is ReflectionTypeLoadException r ? string.Join("; ", r.LoaderExceptions.Select(x => x?.Message).Distinct()) : (e.InnerException ?? e).Message)}");
        }
        return Start(plugin, m, alc);
    }

    /// <summary>Starts a plugin instance made elsewhere (the self-test's in-process plugins: no load context).</summary>
    public string? Load(IPlugin plugin, PluginManifest? manifest = null)
    {
        var m = manifest ?? new PluginManifest { Name = plugin.Name, Version = plugin.Version, Directory = Path.Combine(Options.Path, plugin.Name) };
        return Refusal(m) is { } why ? Failed(m.Name, why) : Start(plugin, m, null);
    }

    private string? Start(IPlugin plugin, PluginManifest m, PluginLoadContext? alc)
    {
        var ctx = new PluginContext(this, m, plugin, alc);
        _loaded.Add(ctx);
        Resnap();
        var ok = Guard(ctx, "Load", () => plugin.Load(ctx), count: false);
        if (!ok)
        {
            var why = ctx.LastError ?? "Load threw";
            Teardown(ctx, "its Load failed", callUnload: false);
            return Failed(m.Name, $"Load threw: {why}");
        }
        _state[m.Name] = "loaded";
        AfterLoad(ctx);
        Log.Info("plugins", $"loaded {m.Name} {plugin.Version}{(m.Pack is { } pack ? $" (pack {pack})" : "")}{(m.Author.Length > 0 ? $" by {m.Author}" : "")}"
            + $": {Subscriptions(ctx)}");
        return null;
    }

    private string Subscriptions(PluginContext ctx)
    {
        var events = Enum.GetValues<PluginEvent>().Where(ctx.Has).Select(e => e.ToString()).ToList();
        events.AddRange(Hooks.Of(ctx));
        if (_scripts.Values.Any(s => s.Owner == ctx)) events.Add($"scripted spells {string.Join(", ", _scripts.Where(s => s.Value.Owner == ctx).Select(s => s.Key))}");
        var commands = _commands.Values.Where(c => c.Owner == ctx).Select(c => "." + c.Path).ToList();
        return $"{(events.Count == 0 ? "no events" : string.Join(", ", events))}; {(commands.Count == 0 ? "no commands" : string.Join(", ", commands))}; {ctx.TimerList.Count} timer(s)";
    }

    /// <summary>Unloads plugin <paramref name="name"/> now (its Unload, storage saved, handlers, commands and timers dropped, its load context unloaded).</summary>
    public bool Unload(string name, string why)
    {
        if (_loaded.FirstOrDefault(c => !c.Dropped && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } ctx) return false;
        Teardown(ctx, why, callUnload: true);
        _state[ctx.Name] = "disabled: " + why;
        return true;
    }

    /// <summary>Stops dispatching to <paramref name="ctx"/> at once (its commands go too); the rest of its teardown may follow later.</summary>
    private void Drop(PluginContext ctx)
    {
        if (ctx.Dropped) return;
        ctx.Dropped = true;
        foreach (var key in _commands.Where(c => c.Value.Owner == ctx).Select(c => c.Key).ToList()) _commands.Remove(key);
        AfterDrop(ctx);
        Resnap();
    }

    private void Teardown(PluginContext ctx, string why, bool callUnload)
    {
        var plugin = ctx.Plugin;
        var alc = ctx.Alc;
        Drop(ctx);
        if (callUnload && plugin is not null) Guard(ctx, "Unload", plugin.Unload, count: false);
        ctx.StorageImpl.Save();
        ctx.Release();
        _loaded.Remove(ctx);
        alc?.Unload();
        Log.Info("plugins", $"unloaded {ctx.Name}: {why}");
    }

    /// <summary>
    /// Reloads plugin <paramref name="name"/>: unloads it, reads its folder again, then builds (on a pool thread) and loads it in the next tick;
    /// <paramref name="done"/> gets the outcome (null = loaded) on the world thread. A plugin not loaded is just loaded (enable).
    /// </summary>
    public void Reload(string name, Action<string?> done)
    {
        Unload(name, "reload");
        Discover();
        if (!_known.TryGetValue(name, out var m))
        {
            done(Failed(name, $"no plugin {name} in {Options.Path}{(World.Config.ModPacks.Count > 0 ? " or the packs' plugins/" : "")}"));
            return;
        }
        if (Refusal(m) is { } why)
        {
            done(Failed(name, why));
            return;
        }
        _state[name] = "building";
        Task.Run(() => PluginBuilder.Resolve(m, BinRoot, Options.Build)).ContinueWith(t => World.Post(() =>
        {
            if (!t.IsCompletedSuccessfully) done(Failed(name, t.Exception?.InnerException?.Message ?? "the build failed"));
            else if (t.Result.Dll is null) done(Failed(name, t.Result.Error!));
            else done(Activate(m, t.Result.Dll));
        }), TaskScheduler.Default);
    }

    /// <summary>As <see cref="Reload(string, Action{string?})"/>, but the build runs here (the console's tests): null when loaded, else why not.</summary>
    public string? ReloadNow(string name)
    {
        Unload(name, "reload");
        Discover();
        return _known.TryGetValue(name, out var m) ? LoadNow(m) : Failed(name, $"no plugin {name} in {Options.Path}");
    }

    /// <summary>One line per plugin found: name, version, state, errors, origin.</summary>
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
                + (m?.Pack is { } pack ? $" (pack {pack})" : "") + (m is { Description.Length: > 0 } ? $" - {m.Description}" : "");
        }
    }

    // ---- the world thread's hooks

    /// <summary>The server has started: ServerStarted to every plugin.</summary>
    internal void Started() => Raise(PluginEvent.ServerStarted);

    /// <summary>The server stops: ServerStopping, then every plugin unloaded (last loaded first), its storage saved.</summary>
    public void Shutdown()
    {
        Raise(PluginEvent.ServerStopping);
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
        if (Wants(PluginEvent.Tick)) Raise(PluginEvent.Tick, new TickArgs(now.TotalSeconds, World.Delta.TotalSeconds));
    }

    /// <summary>How long after a change a plugin's storage is written.</summary>
    public static readonly TimeSpan StorageDelay = TimeSpan.FromSeconds(5);

    // ---- safety

    /// <summary>
    /// Runs <paramref name="action"/> of <paramref name="ctx"/>: an exception is logged and (<paramref name="count"/>) counted, the
    /// Plugins:MaxErrors-th disables the plugin (dispatch stops at once, the unload follows in the next tick); a run slower than Plugins:SlowMs
    /// is logged (once a minute per plugin). False when it threw.
    /// </summary>
    internal bool Guard(PluginContext ctx, string what, Action action, bool count = true)
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

    /// <summary>The plugin whose code runs now on this thread (set by every guarded call): the owner of a unit's data bag or timer.</summary>
    [ThreadStatic] private static PluginContext? _current;

    internal static PluginContext? Current => _current;

    /// <summary><see cref="Current"/>, or an error for a data bag or entity timer touched outside a plugin's own call.</summary>
    internal static PluginContext RequireCurrent() =>
        _current is { Dropped: false } c ? c : throw new InvalidOperationException("unit data and timers can be used only from a plugin's own handlers, timers and commands");

    /// <summary>A hook handler of <paramref name="ctx"/>, guarded as every plugin call.</summary>
    internal bool Guard<T>(PluginContext ctx, string what, Action<T> handler, T args)
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

    private bool Guard<T>(PluginContext ctx, PluginEvent what, Action<T> handler, T args) => Guard(ctx, what.ToString(), handler, args);

    private void Slow(PluginContext ctx, string what, long start)
    {
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (ms <= Options.SlowMs || Environment.TickCount64 - ctx.LastSlowWarning < 60_000) return;
        ctx.LastSlowWarning = Environment.TickCount64;
        Log.Warn("plugin:" + ctx.Name, string.Create(CultureInfo.InvariantCulture,
            $"{what} took {ms:0} ms (Plugins:SlowMs {Options.SlowMs}): it holds up the whole world; keep handlers short and move slow work off the world thread"));
    }

    private void Fault(PluginContext ctx, string what, Exception e, bool count)
    {
        ctx.LastError = $"{e.GetType().Name}: {e.Message}";
        if (!count)
        {
            Log.Error("plugin:" + ctx.Name, $"{what} threw: {e}");
            return;
        }
        ctx.Errors++;
        Log.Error("plugin:" + ctx.Name, $"{what} threw ({ctx.Errors}/{Options.MaxErrors}): {e}");
        if (ctx.Errors < Options.MaxErrors || ctx.Dropped) return;
        Log.Error("plugins", $"{ctx.Name} disabled: {ctx.Errors} errors (Plugins:MaxErrors); .plugins reload {ctx.Name} loads it again");
        Drop(ctx);
        _state[ctx.Name] = $"disabled: {ctx.Errors} errors, the last {ctx.LastError}";
        World.Post(() =>
        {
            if (_loaded.Contains(ctx)) Teardown(ctx, "too many errors", callUnload: true);
            _state[ctx.Name] = $"disabled: {ctx.Errors} errors, the last {ctx.LastError}";
        });
    }

    // ---- dispatch

    private void Raise(PluginEvent e)
    {
        if (!Wants(e)) return;
        foreach (var ctx in _snapshot)
            if (ctx.Handlers(e) is Action d)
                foreach (var h in d.GetInvocationList())
                    if (!ctx.Dropped) Guard(ctx, e.ToString(), (Action)h);
    }

    private void Raise<T>(PluginEvent e, T args)
    {
        foreach (var ctx in _snapshot)
            if (ctx.Handlers(e) is Action<T> d)
                foreach (var h in d.GetInvocationList())
                    if (!ctx.Dropped) Guard(ctx, e, (Action<T>)h, args);
    }

    internal void PlayerLogin(Player p, bool first)
    {
        if (Wants(PluginEvent.PlayerLogin) && !p.IsBot) Raise(PluginEvent.PlayerLogin, new PlayerLoginArgs(Wrap(p), first));
    }

    internal void PlayerLogout(Player p)
    {
        if (Wants(PluginEvent.PlayerLogout) && !p.IsBot) Raise(PluginEvent.PlayerLogout, new PlayerArgs(Wrap(p)));
    }

    internal void ZoneEnter(Player p, bool clusterChange)
    {
        if (Wants(PluginEvent.ZoneEnter) && !p.IsBot && p.Map is { } map) Raise(PluginEvent.ZoneEnter, new ZoneArgs(Wrap(p), map.Cluster, clusterChange));
    }

    internal void ZoneLeave(Player p, bool clusterChange)
    {
        if (Wants(PluginEvent.ZoneLeave) && !p.IsBot && p.Map is { } map) Raise(PluginEvent.ZoneLeave, new ZoneArgs(Wrap(p), map.Cluster, clusterChange));
    }

    internal void MobSpawned(Mob m)
    {
        if (Wants(PluginEvent.MobSpawned)) Raise(PluginEvent.MobSpawned, new MobArgs(Wrap(m)));
    }

    internal void MobDied(Mob m, Player killer, IReadOnlyList<Player> credited)
    {
        if (Wants(PluginEvent.MobDied)) Raise(PluginEvent.MobDied, new MobDeathArgs(Wrap(m), Wrap(killer), [.. credited.Where(c => !c.IsBot).Select(Wrap)]));
    }

    internal void PlayerDown(Player p, long killer, string killerText, bool died)
    {
        var e = died ? PluginEvent.PlayerDied : PluginEvent.PlayerKnockedDown;
        if (!Wants(e) || p.IsBot) return;
        IUnit? unit = p.Map?.FindPlayer(killer) is { } kp ? Wrap(kp) : p.Map?.Objects.GetValueOrDefault(killer) is Mob km ? Wrap(km) : null;
        Raise(e, new PlayerDownArgs(Wrap(p), killerText, unit));
    }

    /// <summary>The Damage event of a hit of <paramref name="amount"/>: the amount the handlers leave (≥ 0); <paramref name="amount"/> when none listens.</summary>
    internal double Damage(Player? attackerP, Mob? attackerM, Player? targetP, Mob? targetM, DamageKind kind, bool overTime, double amount)
    {
        if (!Wants(PluginEvent.Damage)) return amount;
        IUnit attacker = attackerP is not null ? Wrap(attackerP) : Wrap(attackerM!);
        IUnit target = targetP is not null ? Wrap(targetP) : Wrap(targetM!);
        var args = new DamageArgs(attacker, target, kind, overTime, amount);
        Raise(PluginEvent.Damage, args);
        return double.IsFinite(args.Amount) ? Math.Max(0, args.Amount) : amount;
    }

    internal void ItemUsed(Player p, string item, int count, string source)
    {
        if (Wants(PluginEvent.ItemUsed) && !p.IsBot) Raise(PluginEvent.ItemUsed, new ItemArgs(Wrap(p), item, count, source));
    }

    internal void ItemCrafted(Player p, string item, int count, string building)
    {
        if (Wants(PluginEvent.ItemCrafted) && !p.IsBot) Raise(PluginEvent.ItemCrafted, new ItemArgs(Wrap(p), item, count, building));
    }

    /// <summary>The LootRolled event: the drop as the handlers leave it (<paramref name="drops"/> itself when none listens).</summary>
    internal IReadOnlyList<(string Item, int Amount)> LootRolled(Mob m, Player killer, IReadOnlyList<(string Item, int Amount)> drops)
    {
        if (!Wants(PluginEvent.LootRolled)) return drops;
        var args = new LootArgs(Wrap(m), Wrap(killer), [.. drops.Select(d => new LootItem(d.Item, d.Amount))]);
        Raise(PluginEvent.LootRolled, args);
        return [.. args.Items.Where(i => i is { Count: > 0 } && !string.IsNullOrEmpty(i.Item)).Select(i => (i.Item, i.Count))];
    }

    internal void Harvested(Player p, string item, int count, string kind)
    {
        if (Wants(PluginEvent.Harvested) && !p.IsBot) Raise(PluginEvent.Harvested, new HarvestArgs(Wrap(p), item, count, kind));
    }

    internal void GoalCompleted(Player p, string kind, string description)
    {
        if (Wants(PluginEvent.GoalCompleted) && !p.IsBot) Raise(PluginEvent.GoalCompleted, new GoalArgs(Wrap(p), kind, description));
    }

    internal void BuildingAction(Player p, string building, string action, bool accepted)
    {
        if (Wants(PluginEvent.BuildingAction) && !p.IsBot) Raise(PluginEvent.BuildingAction, new BuildingActionArgs(Wrap(p), building, action, accepted));
    }

    internal void Party(bool joined, Guid id, string name, int partyId, int members, string reason)
    {
        var e = joined ? PluginEvent.PartyJoined : PluginEvent.PartyLeft;
        if (!Wants(e)) return;
        var online = World.Parties.Online(id);
        Raise(e, new PartyArgs(id, name, online is null || online.IsBot ? null : Wrap(online), partyId, members, reason));
    }

    internal void ChatLine(Player p, string text)
    {
        if (Wants(PluginEvent.ChatMessage) && !p.IsBot) Raise(PluginEvent.ChatMessage, new ChatArgs(Wrap(p), text));
    }

    // ---- commands

    private sealed record PluginCommand(PluginContext Owner, string Path, string[] Words, PlayerLevel Level, string Help, Action<ICommandCall> Handler);

    internal void RegisterCommand(PluginContext ctx, string path, PlayerLevel level, string help, Action<ICommandCall> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var words = path.TrimStart('.').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
        if (words.Length == 0) throw new InvalidOperationException("a command needs a path");
        var key = string.Join(" ", words);
        if (World.Commands.Commands.Any(c => c.Path == key)) throw new InvalidOperationException($".{key} is a built-in command");
        if (_commands.TryGetValue(key, out var other) && !other.Owner.Dropped) throw new InvalidOperationException($".{key} is plugin {other.Owner.Name}'s");
        if (ctx.Dropped) return;
        _commands[key] = new PluginCommand(ctx, key, words, level, help ?? "", handler);
    }

    /// <summary>The .help lines of the plugin commands <paramref name="level"/> may run, starting with <paramref name="words"/>.</summary>
    public IEnumerable<string> HelpLines(Persistence.AccountLevel level, IReadOnlyList<string> words) =>
        _commands.Values.Where(c => !c.Owner.Dropped && (int)c.Level <= (int)level
                && words.Select((w, i) => i < c.Words.Length && c.Words[i].Equals(w, StringComparison.OrdinalIgnoreCase)).All(x => x))
            .OrderBy(c => c.Path, StringComparer.Ordinal)
            .Select(c => $".{c.Path} [args...]{(c.Help.Length > 0 ? " - " + c.Help : "")} (plugin {c.Owner.Name})");

    /// <summary>
    /// Runs <paramref name="line"/> when a plugin command matches it with a longer path than any built-in one (<see cref="CommandRegistry.Execute"/>
    /// asks first); false = not a plugin command. A command that throws is answered as failed and counted as the plugin's error.
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
        var call = new CommandCall(c.Player is { } p ? Wrap(p) : null, (PlayerLevel)(int)c.Level, c.Console,
            n < words.Count ? text[words[n].Start..].Trim() : "", [.. words.Skip(n).Select(w => w.Text)], c);
        if (!Guard(best.Owner, $"command .{best.Path}", () => best.Handler(call))) c.Error($".{best.Path} failed: {best.Owner.LastError}");
        return true;
    }

    private sealed class CommandCall(IPlayer? player, PlayerLevel level, bool console, string args, IReadOnlyList<string> words, CommandContext c) : ICommandCall
    {
        public IPlayer? Player { get; } = player;
        public PlayerLevel Level { get; } = level;
        public bool FromConsole { get; } = console;
        public string Args { get; } = args;
        public IReadOnlyList<string> Words { get; } = words;
        public void Reply(string text) => c.Reply(text);
        public void Error(string text) => c.Error(text);
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
