using System.Text.Json;
using System.Text.Json.Nodes;
using AlbionLocal.Plugin.Api;

namespace AlbionLocal.Server.Plugins;

/// <summary>The events a plugin can subscribe to (<see cref="IPluginEvents"/>), as indexes into its handler table.</summary>
internal enum PluginEvent
{
    ServerStarted, ServerStopping, Tick, PlayerLogin, PlayerLogout, ZoneEnter, ZoneLeave, MobSpawned, MobDied, PlayerKnockedDown, PlayerDied,
    Damage, ItemUsed, ItemCrafted, LootRolled, Harvested, GoalCompleted, BuildingAction, PartyJoined, PartyLeft, ChatMessage,
}

/// <summary>
/// One loaded plugin: its manifest, instance and load context, and the <see cref="IPluginContext"/> it was given, which holds everything
/// it registered (handlers, timers, commands), so that dropping the context drops them all (<see cref="PluginHost"/>).
/// </summary>
internal sealed class PluginContext : IPluginContext, IPluginEvents, IPluginLog, IPluginTimers, IPluginCommands
{
    public static readonly int EventCount = Enum.GetValues<PluginEvent>().Length;

    private readonly PluginHost _host;
    private readonly Delegate?[] _handlers = new Delegate?[EventCount];

    public PluginContext(PluginHost host, PluginManifest manifest, IPlugin plugin, PluginLoadContext? alc)
    {
        _host = host;
        Manifest = manifest;
        Plugin = plugin;
        Alc = alc;
        DataDirectory = Path.Combine(host.DataRoot, manifest.Name);
        Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, WriteIndented = true };
        ConfigImpl = new PluginConfig(this);
        StorageImpl = new PluginStorage(this);
    }

    public PluginManifest Manifest { get; }
    public IPlugin? Plugin { get; private set; }
    public PluginLoadContext? Alc { get; private set; }

    /// <summary>Per plugin, so System.Text.Json's caches of the plugin's types go with it (a shared one would keep its load context alive).</summary>
    public JsonSerializerOptions Json { get; }

    /// <summary>Dropped: no more dispatch to it (set at the first moment of a disable, before its unload).</summary>
    public bool Dropped { get; set; }

    public int Errors { get; set; }
    public string? LastError { get; set; }
    public long LastSlowWarning { get; set; }

    /// <summary>Its timers (cancelled ones are removed at the next tick).</summary>
    public List<PluginTimer> TimerList { get; } = [];

    public PluginConfig ConfigImpl { get; }
    public PluginStorage StorageImpl { get; }

    public string Name => Manifest.Name;
    public string PluginDirectory => Manifest.Directory;
    public string DataDirectory { get; }
    public IPluginEvents Events => this;
    public IServer Server => _host.Server;
    public IPluginLog Log => this;
    public IPluginConfig Config => ConfigImpl;
    public IPluginStorage Storage => StorageImpl;
    public IPluginTimers Timers => this;
    public IPluginCommands Commands => this;
    public IHooks Hooks => _hooks ??= new ContextHooks(this, _host);
    public IGameplay Game => _host.Game;

    private ContextHooks? _hooks;

    /// <summary>The plugin's data bags by entity (a Player, a Mob, an ActiveSpell entry): gone with the entity, replaced at the unload.</summary>
    public System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, object?>> Data { get; private set; } = new();

    /// <summary>Its named entity timers, by entity.</summary>
    public System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, PluginHost.UnitTimer>> NamedTimers { get; private set; } = new();

    /// <summary>Its areas under way (<see cref="IGameplay.CreateArea"/>).</summary>
    public List<PluginArea> Areas { get; } = [];

    public void Post(Action action) => _host.World.Post(() => _host.Guard(this, "posted action", action));

    /// <summary>The handlers of <paramref name="e"/> (a multicast delegate), null when none or dropped.</summary>
    public Delegate? Handlers(PluginEvent e) => Dropped ? null : _handlers[(int)e];

    public bool Has(PluginEvent e) => !Dropped && _handlers[(int)e] is not null;

    /// <summary>Forgets every handler and timer and the instance (the context may still be referenced by a plugin's closure).</summary>
    public void Release()
    {
        Dropped = true;
        Array.Clear(_handlers);
        foreach (var t in TimerList) t.Cancelled = true;
        TimerList.Clear();
        foreach (var area in Areas.ToList()) area.Dispose();
        Data = new();
        NamedTimers = new();
        Plugin = null;
        Alc = null;
    }

    private void Add(PluginEvent e, Delegate? d)
    {
        if (d is null || Dropped) return;
        _handlers[(int)e] = Delegate.Combine(_handlers[(int)e], d);
        _host.Recount();
    }

    private void Remove(PluginEvent e, Delegate? d)
    {
        if (d is null) return;
        _handlers[(int)e] = Delegate.Remove(_handlers[(int)e], d);
        _host.Recount();
    }

    // ---- IPluginEvents

    public event Action? ServerStarted { add => Add(PluginEvent.ServerStarted, value); remove => Remove(PluginEvent.ServerStarted, value); }
    public event Action? ServerStopping { add => Add(PluginEvent.ServerStopping, value); remove => Remove(PluginEvent.ServerStopping, value); }
    public event Action<TickArgs>? Tick { add => Add(PluginEvent.Tick, value); remove => Remove(PluginEvent.Tick, value); }
    public event Action<PlayerLoginArgs>? PlayerLogin { add => Add(PluginEvent.PlayerLogin, value); remove => Remove(PluginEvent.PlayerLogin, value); }
    public event Action<PlayerArgs>? PlayerLogout { add => Add(PluginEvent.PlayerLogout, value); remove => Remove(PluginEvent.PlayerLogout, value); }
    public event Action<ZoneArgs>? ZoneEnter { add => Add(PluginEvent.ZoneEnter, value); remove => Remove(PluginEvent.ZoneEnter, value); }
    public event Action<ZoneArgs>? ZoneLeave { add => Add(PluginEvent.ZoneLeave, value); remove => Remove(PluginEvent.ZoneLeave, value); }
    public event Action<MobArgs>? MobSpawned { add => Add(PluginEvent.MobSpawned, value); remove => Remove(PluginEvent.MobSpawned, value); }
    public event Action<MobDeathArgs>? MobDied { add => Add(PluginEvent.MobDied, value); remove => Remove(PluginEvent.MobDied, value); }
    public event Action<PlayerDownArgs>? PlayerKnockedDown { add => Add(PluginEvent.PlayerKnockedDown, value); remove => Remove(PluginEvent.PlayerKnockedDown, value); }
    public event Action<PlayerDownArgs>? PlayerDied { add => Add(PluginEvent.PlayerDied, value); remove => Remove(PluginEvent.PlayerDied, value); }
    public event Action<DamageArgs>? Damage { add => Add(PluginEvent.Damage, value); remove => Remove(PluginEvent.Damage, value); }
    public event Action<ItemArgs>? ItemUsed { add => Add(PluginEvent.ItemUsed, value); remove => Remove(PluginEvent.ItemUsed, value); }
    public event Action<ItemArgs>? ItemCrafted { add => Add(PluginEvent.ItemCrafted, value); remove => Remove(PluginEvent.ItemCrafted, value); }
    public event Action<LootArgs>? LootRolled { add => Add(PluginEvent.LootRolled, value); remove => Remove(PluginEvent.LootRolled, value); }
    public event Action<HarvestArgs>? Harvested { add => Add(PluginEvent.Harvested, value); remove => Remove(PluginEvent.Harvested, value); }
    public event Action<GoalArgs>? GoalCompleted { add => Add(PluginEvent.GoalCompleted, value); remove => Remove(PluginEvent.GoalCompleted, value); }
    public event Action<BuildingActionArgs>? BuildingAction { add => Add(PluginEvent.BuildingAction, value); remove => Remove(PluginEvent.BuildingAction, value); }
    public event Action<PartyArgs>? PartyJoined { add => Add(PluginEvent.PartyJoined, value); remove => Remove(PluginEvent.PartyJoined, value); }
    public event Action<PartyArgs>? PartyLeft { add => Add(PluginEvent.PartyLeft, value); remove => Remove(PluginEvent.PartyLeft, value); }
    public event Action<ChatArgs>? ChatMessage { add => Add(PluginEvent.ChatMessage, value); remove => Remove(PluginEvent.ChatMessage, value); }

    // ---- IPluginLog

    private string Source => "plugin:" + Manifest.Name;
    void IPluginLog.Debug(string message) => Photon.Log.Debug(Source, message);
    void IPluginLog.Info(string message) => Photon.Log.Info(Source, message);
    void IPluginLog.Warn(string message) => Photon.Log.Warn(Source, message);
    void IPluginLog.Error(string message) => Photon.Log.Error(Source, message);

    // ---- IPluginTimers

    public IDisposable Every(TimeSpan interval, Action action)
    {
        if (interval < TimeSpan.FromMilliseconds(10)) throw new ArgumentOutOfRangeException(nameof(interval), "at least 10 ms");
        var t = new PluginTimer(_host.World.Clock.Now + interval, interval, action);
        if (!Dropped) TimerList.Add(t);
        return t;
    }

    public IDisposable After(TimeSpan delay, Action action)
    {
        var t = new PluginTimer(_host.World.Clock.Now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), null, action);
        if (!Dropped) TimerList.Add(t);
        return t;
    }

    // ---- IPluginCommands

    public void Register(string path, PlayerLevel level, string help, Action<ICommandCall> handler) => _host.RegisterCommand(this, path, level, help, handler);
}

/// <summary>A plugin timer: due on the world clock, repeating with <see cref="Interval"/>.</summary>
internal sealed class PluginTimer(TimeSpan due, TimeSpan? interval, Action action) : IDisposable
{
    public TimeSpan Due { get; set; } = due;
    public TimeSpan? Interval { get; } = interval;
    public Action Action { get; } = action;
    public bool Cancelled { get; set; }
    public void Dispose() => Cancelled = true;
}

/// <summary>config.json of the plugin's folder with data/plugins/&lt;name&gt;/config.json over it, key by key (<see cref="IPluginConfig"/>).</summary>
internal sealed class PluginConfig(PluginContext owner) : IPluginConfig
{
    private JsonElement? _root;

    public JsonElement Root => _root ??= Read();

    public void Reload() => _root = Read();

    private JsonElement Read()
    {
        var merged = new JsonObject();
        foreach (var file in new[] { Path.Combine(owner.PluginDirectory, "config.json"), Path.Combine(owner.DataDirectory, "config.json") })
        {
            if (!File.Exists(file)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o)
                    foreach (var (k, v) in o.ToList())
                    {
                        o.Remove(k);
                        merged[k] = v;
                    }
                else Photon.Log.Warn("plugin:" + owner.Name, $"{file} is not a JSON object; ignored");
            }
            catch (Exception e) when (e is JsonException or IOException) { Photon.Log.Error("plugin:" + owner.Name, $"{file}: {e.Message}"); }
        }
        using var doc = JsonDocument.Parse(merged.ToJsonString());
        return doc.RootElement.Clone();
    }

    public T Get<T>(string key, T fallback)
    {
        foreach (var p in Root.EnumerateObject())
            if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                try { return p.Value.Deserialize<T>(owner.Json) ?? fallback; }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return fallback; }
            }
        return fallback;
    }

    public T Bind<T>() where T : new()
    {
        try { return Root.Deserialize<T>(owner.Json) ?? new T(); }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            Photon.Log.Error("plugin:" + owner.Name, $"config: {e.Message}; defaults used");
            return new T();
        }
    }
}

/// <summary>The plugin's key-value store, data/plugins/&lt;name&gt;/storage.json (<see cref="IPluginStorage"/>).</summary>
internal sealed class PluginStorage(PluginContext owner) : IPluginStorage
{
    private Dictionary<string, JsonNode?>? _values;

    /// <summary>When the first unsaved change was made (world clock); null when saved.</summary>
    public TimeSpan? DirtySince { get; private set; }

    private string File => Path.Combine(owner.DataDirectory, "storage.json");

    private Dictionary<string, JsonNode?> Values => _values ??= Read();

    private Dictionary<string, JsonNode?> Read()
    {
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (!System.IO.File.Exists(File)) return values;
        try
        {
            if (JsonNode.Parse(System.IO.File.ReadAllText(File)) is JsonObject o)
                foreach (var (k, v) in o.ToList())
                {
                    o.Remove(k);
                    values[k] = v;
                }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Photon.Log.Error("plugin:" + owner.Name, $"{File}: {e.Message}; starting empty (the file is kept as storage.json.bad)");
            try { System.IO.File.Copy(File, File + ".bad", overwrite: true); } catch (IOException) { }
        }
        return values;
    }

    public IReadOnlyCollection<string> Keys => Values.Keys;

    public bool Contains(string key) => Values.ContainsKey(key);

    public T Get<T>(string key, T fallback)
    {
        if (!Values.TryGetValue(key, out var node) || node is null) return fallback;
        try { return node.Deserialize<T>(owner.Json) ?? fallback; }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return fallback; }
    }

    public void Set<T>(string key, T value)
    {
        Values[key] = JsonSerializer.SerializeToNode(value, owner.Json);
        DirtySince ??= ClockNow();
    }

    public bool Remove(string key)
    {
        if (!Values.Remove(key)) return false;
        DirtySince ??= ClockNow();
        return true;
    }

    private static TimeSpan ClockNow() => TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond);

    /// <summary>Due for a write (a change older than <paramref name="delay"/>).</summary>
    public bool Due(TimeSpan delay) => DirtySince is { } since && ClockNow() - since >= delay;

    public void Save()
    {
        if (_values is null || DirtySince is null) return;
        try
        {
            Directory.CreateDirectory(owner.DataDirectory);
            var o = new JsonObject();
            foreach (var (k, v) in _values) o[k] = v?.DeepClone();
            var tmp = File + ".tmp";
            System.IO.File.WriteAllText(tmp, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(tmp, File, overwrite: true);
            DirtySince = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Photon.Log.Error("plugin:" + owner.Name, $"storage not saved: {e.Message}"); }
    }
}
