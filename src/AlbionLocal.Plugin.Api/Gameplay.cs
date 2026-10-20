namespace AlbionLocal.Plugin.Api;

/// <summary>
/// API v2: actions for custom gameplay logic (<see cref="IPluginContext.Game"/>): damage and heals, buffs by spell name, forced movement
/// that respects walls, plugin areas that tick a callback, summons, stat modifiers, cooldowns, running a spells.xml spell. World thread
/// only. Every action returns false / 0 / null when it cannot act (a unit off its map, an unknown spell) instead of throwing.
/// </summary>
public interface IGameplay
{
    /// <summary>The spells.xml spell <paramref name="spell"/>; null when there is none.</summary>
    SpellInfo? Spell(string spell);

    /// <summary>
    /// <paramref name="amount"/> damage of <paramref name="source"/> on <paramref name="target"/> through the engine's damage path (a player on a
    /// mob, a mob on a player, a player on a Hostile player: the PvP rules, damage shields, combat, kills and knockdowns as for any hit; the
    /// target's armour or magic resistance is not applied: <paramref name="amount"/> is the raw hit). <paramref name="spell"/> names the hit in the
    /// HealthUpdate. Returns the health the target lost (0 when it could not hit: mob → mob is not modelled).
    /// </summary>
    double Damage(IUnit source, IUnit target, double amount, bool magic = true, string? spell = null);

    /// <summary>Heals <paramref name="target"/> (a player or a mob) by <paramref name="amount"/>; returns the health gained.</summary>
    double Heal(IUnit source, IUnit target, double amount, string? spell = null);

    /// <summary>
    /// Puts the timed effects of spell <paramref name="spell"/> (buffovertime stats, stun, root, silence, shields, …) on <paramref name="target"/>
    /// by <paramref name="caster"/> (null: the target itself), with <paramref name="stacks"/> stacks (at most its stacklimit) and, when
    /// <paramref name="seconds"/> &gt; 0, that duration instead of the spells.xml one. Its instant effects (damage, over-time ticks) do not run:
    /// use <see cref="CastSpell"/> for those. <paramref name="abilityPower"/> 0: the caster's main-hand ability power (100 for a mob).
    /// </summary>
    bool ApplyBuff(IUnit target, string spell, IUnit? caster = null, double seconds = 0, int stacks = 1, double abilityPower = 0);

    /// <summary>Takes spell <paramref name="spell"/> off <paramref name="target"/> (all of it, or <paramref name="stacks"/> stacks).</summary>
    bool RemoveBuff(IUnit target, string spell, int stacks = 0);

    /// <summary>The spells on <paramref name="unit"/> now (buffs, debuffs, crowd control, mount buffs).</summary>
    IReadOnlyList<IBuff> Buffs(IUnit unit);

    /// <summary>Spell <paramref name="spell"/> on <paramref name="unit"/> (the first entry, any caster); null when none.</summary>
    IBuff? FindBuff(IUnit unit, string spell);

    /// <summary>Pushes <paramref name="target"/> <paramref name="distance"/> m away from (<paramref name="fromX"/>, <paramref name="fromY"/>) at <paramref name="speed"/> m/s; a wall stops it.</summary>
    bool Knockback(IUnit target, float fromX, float fromY, float distance, float speed = 10);

    /// <summary>Pulls <paramref name="target"/> towards (<paramref name="toX"/>, <paramref name="toY"/>), stopping <paramref name="stopAt"/> m before it; a wall stops it.</summary>
    bool Pull(IUnit target, float toX, float toY, float speed = 10, float stopAt = 1);

    /// <summary>Dashes <paramref name="unit"/> to (<paramref name="x"/>, <paramref name="y"/>) at <paramref name="speed"/> m/s; a wall stops it.</summary>
    bool Dash(IUnit unit, float x, float y, float speed = 15);

    /// <summary>Blinks <paramref name="unit"/> to (<paramref name="x"/>, <paramref name="y"/>) at once; a wall on the way stops it short.</summary>
    bool Teleport(IUnit unit, float x, float y);

    /// <summary>
    /// A server-side area at (<paramref name="x"/>, <paramref name="y"/>) of <paramref name="radius"/> m owned by <paramref name="owner"/>: every
    /// <paramref name="interval"/> (first at once) for <paramref name="duration"/> it calls <paramref name="onTick"/> with the units inside. It is
    /// invisible to the clients (pair it with a visual spell, or a spells.xml area); it ends with its duration, <see cref="IDisposable.Dispose"/>,
    /// its owner leaving the map, or the plugin's unload.
    /// </summary>
    IPluginArea? CreateArea(IUnit owner, float x, float y, float radius, TimeSpan duration, TimeSpan interval, Action<AreaTickArgs> onTick);

    /// <summary>A mob of mobs.xml <paramref name="mob"/> beside <paramref name="owner"/>'s map at (x, y); it never respawns and goes after <paramref name="lifetime"/> when given.</summary>
    IMob? Summon(IUnit owner, string mob, float x, float y, TimeSpan? lifetime = null);

    /// <summary>
    /// Sets a stat modifier on <paramref name="unit"/>: <paramref name="value"/> added to buff attribute <paramref name="attribute"/> (the spells.xml
    /// buffovertime types: physicalattackdamagebonus, magicspelldamagebonus, bonusdefensevsmobs, magiccooldownreduction, …) under the key
    /// <paramref name="source"/>, until cleared, the unit gone or the plugin unloaded. Server-side only: movement speed is the client's own
    /// (use a buff spell for speed).
    /// </summary>
    void SetModifier(IUnit unit, string source, string attribute, double value);

    /// <summary>Clears modifier <paramref name="source"/> (of <paramref name="attribute"/>, or all its attributes).</summary>
    bool ClearModifier(IUnit unit, string source, string? attribute = null);

    /// <summary>The cooldown left of spell <paramref name="spell"/> for <paramref name="player"/>, in seconds (0 = ready).</summary>
    double CooldownLeft(IPlayer player, string spell);

    /// <summary>Sets <paramref name="player"/>'s cooldown of <paramref name="spell"/> to <paramref name="seconds"/> from now (0 = ready). The client is told.</summary>
    bool SetCooldown(IPlayer player, string spell, double seconds);

    /// <summary>Clears every cooldown of <paramref name="player"/> (the client is told).</summary>
    bool ResetCooldowns(IPlayer player);

    /// <summary>
    /// Runs spells.xml spell <paramref name="spell"/>'s effects as <paramref name="caster"/>'s hit (no cast bar, cost or cooldown): at
    /// <paramref name="target"/>, or at (x, y), or on the caster. Damage, heals, buffs, areas, movement: everything the engine runs.
    /// </summary>
    bool CastSpell(IPlayer caster, string spell, IUnit? target = null, float? x = null, float? y = null);

    /// <summary>Interrupts <paramref name="unit"/>'s cast or channel under way.</summary>
    bool Interrupt(IUnit unit);

    /// <summary>The unit's stats now (health, energy, item power, resistances).</summary>
    UnitStats Stats(IUnit unit);

    /// <summary>The units within <paramref name="radius"/> m of (x, y) in <paramref name="cluster"/> (players and living mobs).</summary>
    IReadOnlyList<IUnit> UnitsAround(string cluster, float x, float y, float radius);

    /// <summary>Whether <paramref name="caster"/> may hurt <paramref name="target"/> (a mob: attackable; a player: Hostile by the PvP rules).</summary>
    bool IsEnemy(IUnit caster, IUnit target);
}

/// <summary>A spells.xml spell.</summary>
public sealed record SpellInfo(string Name, int Id, string Kind, string Category, double CastTime, double Cooldown, float Range, string Target, int StackLimit);

/// <summary>A spell on a unit (a live view during the call; keep the unit, not the buff).</summary>
public interface IBuff
{
    string Spell { get; }

    /// <summary>The caster's object id (0 = the world).</summary>
    long CasterId { get; }

    /// <summary>The caster when it is still on the unit's map.</summary>
    IUnit? Caster { get; }

    int Stacks { get; }

    /// <summary>Seconds until its longest timed effect ends; <see cref="double.PositiveInfinity"/> when it lasts until removed.</summary>
    double SecondsLeft { get; }

    /// <summary>
    /// This plugin's data on this buff entry (key <paramref name="key"/>; made by <paramref name="factory"/> on first use), gone with the entry
    /// or at the plugin's unload.
    /// </summary>
    T GetData<T>(string key, Func<T> factory);

    void SetData<T>(string key, T value);
}

/// <summary>A plugin area (<see cref="IGameplay.CreateArea"/>); Dispose ends it.</summary>
public interface IPluginArea : IDisposable
{
    string Cluster { get; }
    float X { get; set; }
    float Y { get; set; }
    float Radius { get; set; }
    IUnit Owner { get; }
    bool Ended { get; }
}

/// <summary>One tick of a plugin area: the units inside (living mobs and joined players, bots included).</summary>
public sealed class AreaTickArgs(IPluginArea area, IReadOnlyList<IUnit> inside, int tick)
{
    public IPluginArea Area { get; } = area;
    public IReadOnlyList<IUnit> Inside { get; } = inside;

    /// <summary>The tick, from 1.</summary>
    public int Tick { get; } = tick;
}

/// <summary>A unit's stats (<see cref="IGameplay.Stats"/>).</summary>
public sealed record UnitStats(float Health, float MaxHealth, float Energy, float MaxEnergy, double ItemPower, double ArmorResist, double MagicResist,
    double CrowdControlResist, int Tier);

/// <summary>A unit's timer (<see cref="IUnit.AddTimer"/>).</summary>
public interface IUnitTimer : IDisposable
{
    string? Name { get; }

    /// <summary>How many times it has fired.</summary>
    int Fired { get; }

    bool Cancelled { get; }
}

/// <summary>
/// A scripted spell (<see cref="ISpellHooks.Register"/>): callbacks that implement spell <c>spell</c> server-side. OnCast when the cast goes through
/// (energy paid, cooldown started), OnHit when its hit lands (after hitdelay and a projectile's flight), OnTick at each channel tick. With
/// <see cref="ReplaceEffects"/> (the default) the spells.xml effects of the hit and of the channel ticks do not run: the script is the spell.
/// </summary>
public sealed class SpellScript
{
    public bool ReplaceEffects { get; init; } = true;
    public Action<SpellScriptContext>? OnCast { get; init; }
    public Action<SpellScriptContext>? OnHit { get; init; }
    public Action<SpellScriptContext>? OnTick { get; init; }
}

/// <summary>What a scripted spell's callback runs with.</summary>
public sealed class SpellScriptContext(IPlayer caster, string spell, byte slot, string? item, IUnit? target, float x, float y, double abilityPower, int tick, IGameplay game)
{
    public IPlayer Caster { get; } = caster;
    public string Spell { get; } = spell;
    public byte SlotIndex { get; } = slot;
    public SpellSlot Slot => SpellSlots.FromIndex(SlotIndex);
    public string? Item { get; } = item;

    /// <summary>The unit it was cast at; null for a ground or self cast.</summary>
    public IUnit? Target { get; } = target;

    /// <summary>The aim point or the target's position.</summary>
    public float X { get; } = x;
    public float Y { get; } = y;

    /// <summary>The ability power of the item that granted it.</summary>
    public double AbilityPower { get; } = abilityPower;

    /// <summary>OnTick: the channel tick, from 1.</summary>
    public int Tick { get; } = tick;

    /// <summary>The actions (<see cref="IPluginContext.Game"/>).</summary>
    public IGameplay Game { get; } = game;
}
