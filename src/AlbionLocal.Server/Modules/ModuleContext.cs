using System.Text.Json;
using System.Text.Json.Nodes;
using AlbionLocal.Modules;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// One loaded module: its manifest, instance and load context, and the <see cref="IModuleContext"/> it was given, which holds everything
/// it registered (handlers, timers, commands), so that dropping the context drops them all (<see cref="ModuleHost"/>).
/// </summary>
internal sealed class ModuleContext : IModuleContext, IModuleLog, IModuleTimers, IModuleCommands
{
    /// <summary>While the module loads: the checks of its hooks' ids, for after its OnLoads.</summary>
    internal List<Action>? IdChecks { get; set; }

    private readonly ModuleHost _host;

    public ModuleContext(ModuleHost host, ModuleManifest manifest, IReadOnlyList<ModuleScript> scripts, ModuleLoadContext? alc, System.Reflection.Assembly? assembly)
    {
        _host = host;
        Manifest = manifest;
        Scripts.AddRange(scripts);
        Alc = alc;
        Assembly = assembly;
        DataDirectory = Path.Combine(host.DataRoot, manifest.Name);
        Json = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, WriteIndented = true };
        ConfigImpl = new ModuleConfig(this);
        StorageImpl = new ModuleStorage(this);
    }

    public ModuleManifest Manifest { get; }

    /// <summary>The module's script instances, in the order they load.</summary>
    public List<ModuleScript> Scripts { get; } = [];

    public ModuleLoadContext? Alc { get; private set; }

    /// <summary>The module's assembly (a module that "requires" this one shares it).</summary>
    public System.Reflection.Assembly? Assembly { get; private set; }

    /// <summary>Per module, so System.Text.Json's caches of the module's types go with it (a shared one would keep its load context alive).</summary>
    public JsonSerializerOptions Json { get; }

    /// <summary>Dropped: no more dispatch to it (set at the first moment of a disable, before its unload).</summary>
    public bool Dropped { get; set; }

    /// <summary>Its OnUnload runs (or has run): what it keeps on units can still be read, nothing new starts.</summary>
    public bool Unloading { get; set; }

    public int Errors { get; set; }
    public string? LastError { get; set; }
    public long LastSlowWarning { get; set; }

    /// <summary>Its timers (cancelled ones are removed at the next tick).</summary>
    public List<ModuleTimer> TimerList { get; } = [];

    public ModuleConfig ConfigImpl { get; }
    public ModuleStorage StorageImpl { get; }

    public string Name => Manifest.Name;
    public string ModuleDirectory => Manifest.Directory;
    public ModuleHost Host => _host;
    public string ConfigPath => _host.Options.ConfigPath;
    public string DataDirectory { get; }
    public IServer Server => _host.Server;
    public IModuleLog Log => this;
    public IModuleConfig Config => ConfigImpl;
    public IModuleStorage Storage => StorageImpl;
    public IModuleTimers Timers => this;
    public IModuleCommands Commands => this;
    public IHooks Hooks => _hooks ??= new ContextHooks(this, _host.Hooks);
    public IGameplay Game => _host.Game;

    private ContextHooks? _hooks;

    /// <summary>The module's data bags by entity (a Player, a Mob, an ActiveSpell entry): gone with the entity, replaced at the unload.</summary>
    public System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, object?>> Data { get; private set; } = new();

    /// <summary>Its named entity timers, by entity.</summary>
    public System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<string, ModuleHost.UnitTimer>> NamedTimers { get; private set; } = new();

    /// <summary>Its areas under way (<see cref="IGameplay.CreateArea"/>).</summary>
    public List<ModuleArea> Areas { get; } = [];

    public void Post(Action action) => _host.World.Post(() =>
    {
        if (!Dropped) _host.Guard(this, "posted action", action);
    });

    public void AddScript(ModuleScript script) => _host.AddScript(this, script);

    /// <summary>Forgets every handler and timer and the instance (the context may still be referenced by a module's closure).</summary>
    public void Release()
    {
        Dropped = true;
        foreach (var t in TimerList) t.Cancelled = true;
        TimerList.Clear();
        foreach (var area in Areas.ToList()) area.Dispose();
        Data = new();
        NamedTimers = new();
        Scripts.Clear();
        Alc = null;
        Assembly = null;
    }

    // ---- IModuleLog

    private string Source => "module:" + Manifest.Name;
    void IModuleLog.Debug(string message) => Photon.Log.Debug(Source, message);
    void IModuleLog.Info(string message) => Photon.Log.Info(Source, message);
    void IModuleLog.Warn(string message) => Photon.Log.Warn(Source, message);
    void IModuleLog.Error(string message) => Photon.Log.Error(Source, message);

    // ---- IModuleTimers

    public IDisposable Every(TimeSpan interval, Action action)
    {
        if (interval < TimeSpan.FromMilliseconds(10)) throw new ArgumentOutOfRangeException(nameof(interval), "at least 10 ms");
        var t = new ModuleTimer(_host.World.Clock.Now + interval, interval, action);
        if (!Dropped) TimerList.Add(t);
        return t;
    }

    public IDisposable After(TimeSpan delay, Action action)
    {
        var t = new ModuleTimer(_host.World.Clock.Now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay), null, action);
        if (!Dropped) TimerList.Add(t);
        return t;
    }

    // ---- IModuleCommands

    public void Register(string path, PlayerLevel level, string help, Action<ICommandCall> handler) => _host.RegisterCommand(this, path, level, help, handler);
}

/// <summary>A module timer: due on the world clock, repeating with <see cref="Interval"/>.</summary>
internal sealed class ModuleTimer(TimeSpan due, TimeSpan? interval, Action action) : IDisposable
{
    public TimeSpan Due { get; set; } = due;
    public TimeSpan? Interval { get; } = interval;
    public Action Action { get; } = action;
    public bool Cancelled { get; set; }
    public void Dispose() => Cancelled = true;
}

/// <summary>conf/&lt;id&gt;.jsonc.dist of the module's folder with config/modules/&lt;id&gt;.jsonc over it, key by key (<see cref="IModuleConfig"/>).</summary>
internal sealed class ModuleConfig(ModuleContext owner) : IModuleConfig
{
    private JsonElement? _root;

    public JsonElement Root => _root ??= Read();

    public void Reload() => _root = Read();

    private JsonElement Read()
    {
        var merged = new JsonObject();
        var (dist, own) = ModuleSettings.Files(owner.Manifest, owner.ConfigPath);
        var defaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in dist == own ? [dist] : new[] { dist, own })
        {
            if (!File.Exists(file)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o)
                    foreach (var (k, v) in o.ToList())
                    {
                        o.Remove(k);
                        foreach (var same in merged.Select(m => m.Key).Where(m => m.Equals(k, StringComparison.OrdinalIgnoreCase)).ToList()) merged.Remove(same); // keys ignore case
                        merged[k] = v;
                        if (file == dist) defaults.Add(k);
                        else if (File.Exists(dist) && !defaults.Contains(k) && !k.Equals(ModuleSettings.EnabledKey, StringComparison.OrdinalIgnoreCase))
                            Photon.Log.Warn("module:" + owner.Name, $"{file}: unknown key \"{k}\"{(ModuleHost.Nearest(k, defaults) is { } near ? $" (did you mean \"{near}\"?)" : "")}");
                    }
                else Photon.Log.Warn("module:" + owner.Name, $"{file} is not a JSON object; ignored");
            }
            catch (Exception e) when (e is JsonException or IOException) { Photon.Log.Error("module:" + owner.Name, $"{file}: {e.Message}"); }
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
            Photon.Log.Error("module:" + owner.Name, $"config: {e.Message}; defaults used");
            return new T();
        }
    }
}

/// <summary>The module's key-value store, data/modules/&lt;name&gt;/storage.json (<see cref="IModuleStorage"/>).</summary>
internal sealed class ModuleStorage(ModuleContext owner) : IModuleStorage
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
            Photon.Log.Error("module:" + owner.Name, $"{File}: {e.Message}; starting empty (the file is kept as storage.json.bad)");
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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Photon.Log.Error("module:" + owner.Name, $"storage not saved: {e.Message}"); }
    }
}
