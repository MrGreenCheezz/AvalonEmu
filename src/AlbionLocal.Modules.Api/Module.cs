using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AlbionLocal.Server")]

namespace AlbionLocal.Modules;

/// <summary>The API's version. A module's module.jsonc names the "api" it was written for; the server loads it when the major number is the same and the minor is not newer.</summary>
public static class ModuleApi
{
    /// <summary>Raised only by a change that breaks modules (a removed or changed member).</summary>
    public const int Major = 1;

    /// <summary>Raised by every addition.</summary>
    public const int Minor = 0;

    /// <summary>"major.minor", as module.jsonc writes it.</summary>
    public static string Version => $"{Major}.{Minor}";

    /// <summary>Whether a module written for API <paramref name="api"/> ("1.0") runs on this server; <paramref name="why"/> says why not.</summary>
    public static bool Supports(string api, out string? why)
    {
        var parts = api.Split('.');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var major) || major < 0 || !int.TryParse(parts.Length > 1 ? parts[1] : "0", out var minor) || minor < 0)
        {
            why = $"\"api\": \"{api}\" is not a version like \"{Version}\"";
            return false;
        }
        if (major != Major || minor > Minor)
        {
            why = $"it is written for module API {api}, this server has API {Version}: {(major > Major || major == Major && minor > Minor ? "update the server" : "update the module")}";
            return false;
        }
        why = null;
        return true;
    }
}

/// <summary>
/// The base of everything a module runs. The server creates one instance of every non-abstract class of the module's assembly that derives
/// from this (with a parameterless constructor), connects the hooks it overrides, and calls <see cref="OnLoad"/>. There is nothing to
/// register by hand. Use one of the *Script classes (<see cref="PlayerScript"/>, <see cref="CombatScript"/>, …) or <see cref="Module"/>.
/// No static state that outlives the module: it would keep the old assembly alive after a reload.
/// </summary>
/// <example>
/// A whole module in one script:
/// <code><![CDATA[
/// public sealed class Welcome : PlayerScript
/// {
///     private string _text = "";
///
///     protected override void OnLoad() => _text = Config.Get("text", "Welcome!");
///
///     public override void OnLogin(PlayerLoginArgs e) => e.Player.SendMessage(_text);
/// }
/// ]]></code>
/// </example>
public abstract class ModuleScript
{
    private IModuleContext? _context;

    /// <summary>Everything the server offers this module. Not available in the constructor: use <see cref="OnLoad"/>.</summary>
    protected IModuleContext Context => _context ?? throw new InvalidOperationException($"{GetType().Name}: the module's context is not there yet: use it in OnLoad, not in the constructor");

    /// <summary>The game: its data (items, spells, mobs, zones), players, messages, spawns, world events.</summary>
    protected IServer Server => Context.Server;

    /// <summary>The gameplay actions: damage, heal, buffs, movement, areas, summons, modifiers, cooldowns.</summary>
    protected IGameplay Game => Context.Game;

    /// <summary>The hooks as lambdas (the *Script classes are the same hooks as methods).</summary>
    protected IHooks Hooks => Context.Hooks;

    /// <summary>The module's log (prefixed with its id).</summary>
    protected IModuleLog Log => Context.Log;

    /// <summary>The module's settings (conf/&lt;id&gt;.jsonc.dist under config/modules/&lt;id&gt;.jsonc).</summary>
    protected IModuleConfig Config => Context.Config;

    /// <summary>A small saved key-value store.</summary>
    protected IModuleStorage Storage => Context.Storage;

    /// <summary>Timers on the world clock.</summary>
    protected IModuleTimers Timers => Context.Timers;

    /// <summary>The module's chat and console commands.</summary>
    protected IModuleCommands Commands => Context.Commands;

    /// <summary>Called once when the module loads, after every script of the module exists: read settings, register commands and timers. Throwing fails the load.</summary>
    protected internal virtual void OnLoad() { }

    /// <summary>Called once before the module goes (a reload, a disable, too many errors, the server stopping). What was registered through the context goes by itself.</summary>
    protected internal virtual void OnUnload() { }

    internal void Attach(IModuleContext context) => _context = context;
}

/// <summary>A script with no hooks of its own: the place for a module's start-up (commands, timers, lambda hooks) in <see cref="ModuleScript.OnLoad"/>. Optional.</summary>
public abstract class Module : ModuleScript;
