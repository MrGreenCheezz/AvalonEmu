namespace AlbionLocal.Plugin.Api;

/// <summary>The API's version. A plugin's plugin.json names the apiVersion it was written for; the server loads it only when that is <see cref="Version"/>.</summary>
public static class PluginApi
{
    /// <summary>The version of this API (whole number). 2 added the gameplay hooks (<see cref="IPluginContext.Hooks"/>) and actions (<see cref="IPluginContext.Game"/>).</summary>
    public const int Version = 2;

    /// <summary>The oldest apiVersion a plugin.json may name and still load (a version-1 plugin builds unchanged against version 2).</summary>
    public const int MinVersion = 1;
}

/// <summary>
/// A server plugin: one public class with a parameterless constructor per plugin assembly. The server creates it, calls
/// <see cref="Load"/> once (subscribe to events, register commands and timers there), and <see cref="Unload"/> before the plugin's
/// assembly is unloaded (a <c>.plugins reload</c>, <c>.plugins disable</c>, too many errors, or the server stopping). Everything a plugin
/// registered through its <see cref="IPluginContext"/> (event handlers, commands, timers) is dropped by the server after Unload, so
/// Unload only has to release what the plugin made itself. Both run on the world thread.
/// </summary>
public interface IPlugin
{
    /// <summary>The plugin's name (the folder name and plugin.json's "name" should match it).</summary>
    string Name { get; }

    /// <summary>The plugin's own version, free text ("1.0.0").</summary>
    string Version { get; }

    /// <summary>Called once after the plugin is created. Throwing here fails the load: the plugin is not started.</summary>
    void Load(IPluginContext context);

    /// <summary>Called once before the plugin is dropped. The context's storage is saved after it.</summary>
    void Unload();
}

/// <summary>
/// A convenient base: keeps the context in <see cref="Context"/> and offers the common parts as properties. Override
/// <see cref="OnLoad"/> (and <see cref="OnUnload"/> if needed).
/// </summary>
public abstract class PluginBase : IPlugin
{
    private IPluginContext? _context;

    public abstract string Name { get; }

    public virtual string Version => "1.0.0";

    /// <summary>The context given to <see cref="Load"/>; throws before it.</summary>
    protected IPluginContext Context => _context ?? throw new InvalidOperationException($"plugin {Name} is not loaded");

    protected IPluginEvents Events => Context.Events;
    protected IServer Server => Context.Server;
    protected IPluginLog Log => Context.Log;
    protected IPluginConfig Config => Context.Config;
    protected IPluginStorage Storage => Context.Storage;
    protected IPluginTimers Timers => Context.Timers;
    protected IPluginCommands Commands => Context.Commands;

    /// <summary>API v2: the gameplay hooks.</summary>
    protected IHooks Hooks => Context.Hooks;

    /// <summary>API v2: the gameplay actions.</summary>
    protected IGameplay Game => Context.Game;

    public void Load(IPluginContext context)
    {
        _context = context;
        OnLoad();
    }

    public void Unload() => OnUnload();

    /// <summary>Subscribe, register commands and timers here.</summary>
    protected abstract void OnLoad();

    /// <summary>Release what the plugin made itself (event handlers, commands and timers are dropped by the server).</summary>
    protected virtual void OnUnload() { }
}
