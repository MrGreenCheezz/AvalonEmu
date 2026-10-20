using System.Reflection;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Scripting;

/// <summary>
/// The scripts of the server (TC ScriptMgr, ARCHITECTURE.md §6.1): item scripts ([ItemScript] on an
/// <see cref="ItemScript"/> class) and player hooks (<see cref="IPlayerHooks"/> implementations), found by reflection
/// over the server assembly once per process (later also Paths:ScriptAssemblies), one instance per class, the item
/// scripts bound to the item templates. Building throws <see cref="InvalidOperationException"/> on a script that cannot
/// be bound ([ItemScript] on a class that is no ItemScript, an empty pattern, an abstract or generic class, no
/// constructor without parameters) and on two equally precise patterns of different classes for one item;
/// <see cref="ServerHost"/> builds it before the ports, so such a mistake stops the start (principle 6). A pattern that
/// matches no item, or is outranked on every item it matches, is warned and kept. The calls below are the only way the game reaches scripts; with nothing
/// registered each costs a length check (no item table, an empty hook list). A script that throws is logged and the
/// game goes on.
/// </summary>
public sealed class ScriptRegistry
{
    /// <summary>The script classes of the server assembly (candidates; the constructor checks them), found once per process.</summary>
    private static readonly Lazy<Type[]> ServerTypes = new(() => [.. typeof(ScriptRegistry).Assembly.GetTypes().Where(t =>
        t.IsDefined(typeof(ItemScriptAttribute), inherit: false) || t.IsSubclassOf(typeof(ItemScript)) || (t.IsClass && typeof(IPlayerHooks).IsAssignableFrom(t)))]);

    /// <summary>The item script by template index; empty when no item script is registered.</summary>
    private readonly ItemScript?[] _items = [];
    private readonly IPlayerHooks[] _playerHooks;
    private readonly List<string> _bindings = [];

    /// <summary>The scripts of the server assembly bound to <paramref name="items"/> (ServerHost, and a World built without its own).</summary>
    public static ScriptRegistry Build(ItemStore items) => new(ServerTypes.Value, items);

    /// <summary>The scripts among <paramref name="types"/> (the self-test passes its own), bound to <paramref name="items"/>.</summary>
    public ScriptRegistry(IEnumerable<Type> types, ItemStore items)
    {
        var itemScripts = new List<(string Pattern, ItemScript Script)>();
        var hooks = new List<IPlayerHooks>();
        foreach (var type in types)
        {
            var patterns = type.GetCustomAttributes<ItemScriptAttribute>(inherit: false).Select(a => a.Pattern).ToList();
            var isItemScript = type.IsSubclassOf(typeof(ItemScript));
            if (patterns.Count > 0 && !isItemScript)
                throw new InvalidOperationException($"{type.FullName}: [ItemScript] on a class that does not derive from ItemScript");
            if (patterns.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException($"{type.FullName}: [ItemScript] with an empty pattern");
            if (isItemScript && !type.IsAbstract && patterns.Count == 0)
                Log.Warn("scripts", $"{type.FullName} has no [ItemScript(\"name or mask\")]: bound to no item");
            var isHooks = type is { IsClass: true, IsAbstract: false } && typeof(IPlayerHooks).IsAssignableFrom(type);
            if (patterns.Count == 0 && !isHooks) continue;
            var instance = Create(type);
            foreach (var pattern in patterns) itemScripts.Add((pattern, (ItemScript)instance));
            if (isHooks) hooks.Add((IPlayerHooks)instance);
        }
        if (itemScripts.Count > 0) _items = Bind(itemScripts, items);
        _playerHooks = [.. hooks];
        foreach (var h in _playerHooks) _bindings.Add($"hook IPlayerHooks -> {h.GetType().FullName}");
    }

    /// <summary>What is bound, one line each (kind, key, class); ServerHost logs them at startup.</summary>
    public IReadOnlyList<string> Bindings => _bindings;

    /// <summary>The player hooks in the order they were found.</summary>
    public IReadOnlyList<IPlayerHooks> PlayerHooks => _playerHooks;

    /// <summary>The script bound to <paramref name="template"/>; null = none (the default path).</summary>
    public ItemScript? ItemScriptOf(ItemTemplate template) => template.Index < _items.Length ? _items[template.Index] : null;

    /// <summary><see cref="ItemScript.OnEquip"/> of the item's script, if it has one; returns whether it had one.</summary>
    public bool OnEquip(Player p, Item item, bool login = false)
    {
        p.World.Plugins.Equipped(p, item, equip: true, login); // the plugins' Items.OnEquip
        if (!login) p.World.ExtraBars.Refresh(p, Plugin.Api.ExtraBarChangeSource.Equipment); // the extra bars from the worn items (a Join builds them once)
        if (ItemScriptOf(item.Template) is not { } script) return false;
        try { script.OnEquip(p, item); }
        catch (Exception e) { Failed(script, nameof(ItemScript.OnEquip), e); }
        return true;
    }

    /// <summary><see cref="ItemScript.OnUnequip"/> of the item's script, if it has one.</summary>
    public void OnUnequip(Player p, Item item)
    {
        p.World.Plugins.Equipped(p, item, equip: false, login: false); // the plugins' Items.OnUnequip
        p.World.ExtraBars.Refresh(p, Plugin.Api.ExtraBarChangeSource.Equipment);
        if (ItemScriptOf(item.Template) is not { } script) return;
        try { script.OnUnequip(p, item); }
        catch (Exception e) { Failed(script, nameof(ItemScript.OnUnequip), e); }
    }

    /// <summary><see cref="IPlayerHooks.OnLogin"/> of every hook.</summary>
    public void OnLogin(Player p, bool first)
    {
        foreach (var h in _playerHooks)
        {
            try { h.OnLogin(p, first); }
            catch (Exception e) { Failed(h, nameof(IPlayerHooks.OnLogin), e); }
        }
    }

    /// <summary><see cref="IPlayerHooks.OnLogout"/> of every hook.</summary>
    public void OnLogout(Player p)
    {
        foreach (var h in _playerHooks)
        {
            try { h.OnLogout(p); }
            catch (Exception e) { Failed(h, nameof(IPlayerHooks.OnLogout), e); }
        }
    }

    /// <summary><see cref="IPlayerHooks.OnItemAdded"/> of every hook.</summary>
    public void OnItemAdded(Player p, Item item)
    {
        foreach (var h in _playerHooks)
        {
            try { h.OnItemAdded(p, item); }
            catch (Exception e) { Failed(h, nameof(IPlayerHooks.OnItemAdded), e); }
        }
    }

    private static void Failed(object script, string call, Exception e) => Log.Error("scripts", $"{script.GetType().FullName}.{call} failed: {e}");

    /// <summary>The one instance of a script class; throws when the class cannot have one.</summary>
    private static object Create(Type type)
    {
        if (type.IsAbstract || type.ContainsGenericParameters)
            throw new InvalidOperationException($"{type.FullName}: a script class must be concrete and not generic");
        var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes)
            ?? throw new InvalidOperationException($"{type.FullName}: a script class needs a constructor without parameters");
        return constructor.Invoke(null);
    }

    private static bool IsExact(string pattern) => pattern.AsSpan().IndexOfAny('*', '?') < 0;

    /// <summary>
    /// How precisely <paramref name="pattern"/> names <paramref name="t"/>, which it matched (higher wins, see
    /// <see cref="ItemScriptAttribute"/>): the item's own name exactly (with '@' the variant's, without it the base
    /// item's) beats every mask; otherwise more literal characters win, a name without '@' counting on an enchanted
    /// variant as a mask of its characters (it covers the base item and all its enchantments, so a mask such as
    /// T4_MAIN_SWORD@? is narrower), and at an equal count a pattern with '@' (matched against the variant's own name)
    /// beats an exact base name, which beats a mask on the base name.
    /// </summary>
    private static int Precision(string pattern, bool full, ItemTemplate t)
    {
        var literals = pattern.Count(c => c is not ('*' or '?'));
        var exact = IsExact(pattern);
        if (exact && (full || !t.Name.Contains('@'))) return 100_000 + literals;
        return literals * 4 + (full ? 2 : exact ? 1 : 0);
    }

    /// <summary>
    /// The most precise script per template (see <see cref="ItemScriptAttribute"/>); throws when two classes are equally
    /// precise for one item, warns about a pattern that matches none and about one that is outranked on every item it
    /// matches.
    /// </summary>
    private ItemScript?[] Bind(List<(string Pattern, ItemScript Script)> scripts, ItemStore items)
    {
        var size = items.Count == 0 ? 1 : items.All.Max(t => t.Index) + 1;
        var chosen = new ItemScript?[size];
        var precision = new int[size];
        var from = new int[size]; // index into scripts of the chosen binding
        var tie = new string?[size];
        var matched = new int[scripts.Count];
        for (var k = 0; k < scripts.Count; k++)
        {
            var (pattern, script) = scripts[k];
            var upper = pattern.ToUpperInvariant();
            var full = pattern.Contains('@');
            foreach (var t in items.All)
            {
                if (!GameData.Glob(upper, (full ? t.Name : t.BaseName).ToUpperInvariant())) continue;
                matched[k]++;
                var i = t.Index;
                var p = Precision(pattern, full, t);
                if (chosen[i] is null || p > precision[i]) (chosen[i], precision[i], from[i], tie[i]) = (script, p, k, null);
                else if (p == precision[i] && chosen[i] != script) tie[i] ??= $"[ItemScript(\"{pattern}\")] {script.GetType().FullName}";
            }
            if (matched[k] == 0)
                Log.Warn("scripts", $"[ItemScript(\"{pattern}\")] {script.GetType().FullName} matches no item"
                    + (IsExact(pattern) && items.Closest(pattern) is { } closest ? $" (closest: {closest})" : ""));
            _bindings.Add($"item {pattern} -> {script.GetType().FullName} ({matched[k]} items)");
        }
        var ties = Enumerable.Range(0, size).Where(i => tie[i] is not null).ToList();
        if (ties is [var first, ..])
            throw new InvalidOperationException($"item {items[first]?.Name}: [ItemScript(\"{scripts[from[first]].Pattern}\")] {chosen[first]!.GetType().FullName} "
                + $"and {tie[first]} are equally precise{(ties.Count > 1 ? $" (and {ties.Count - 1} more items)" : "")}");
        var won = new int[scripts.Count];
        for (var i = 0; i < size; i++)
            if (chosen[i] is not null) won[from[i]]++;
        for (var k = 0; k < scripts.Count; k++)
            if (matched[k] > 0 && won[k] == 0)
                Log.Warn("scripts", $"[ItemScript(\"{scripts[k].Pattern}\")] {scripts[k].Script.GetType().FullName} is outranked by a more precise "
                    + $"pattern on all {matched[k]} items it matches: bound to none");
        return chosen;
    }
}
