namespace AlbionLocal.Modules;

/// <summary>
/// Actions for custom gameplay logic (<see cref="IModuleContext.Game"/>): damage and heals, buffs by spell name, forced movement
/// that respects walls, module areas that tick a callback, summons, stat modifiers, cooldowns, running a spells.xml spell. World thread
/// only. Every action returns false / 0 / null when it cannot act (a unit off its map, an unknown spell) instead of throwing.
/// </summary>
public interface IGameplay
{
    /// <summary>
    /// Called from inside a damage or heal hook, this and <see cref="Heal"/> and <see cref="CastSpell"/> wait for the next tick and return 0 or true: a handler never hits into the middle of a hit.
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
    /// <paramref name="interval"/> (first at once) for <paramref name="duration"/> it calls <paramref name="onTick"/> with the units inside. The clients do not see it unless
    /// <paramref name="visual"/> names a spells.xml spell with a spelleffectarea: they then draw that spell's area at this place for that time, with the size and look of their own data,
    /// whatever <paramref name="radius"/> says, and it does not follow a move of the area. It ends with its duration, <see cref="IDisposable.Dispose"/>, its owner leaving the map, or the module's unload.
    /// </summary>
    IModuleArea? CreateArea(IUnit owner, float x, float y, float radius, TimeSpan duration, TimeSpan interval, Action<AreaTickArgs> onTick, string? visual = null);

    /// <summary>A mob of mobs.xml <paramref name="mob"/> at (x, y) on <paramref name="owner"/>'s map; it never respawns and goes after <paramref name="lifetime"/> when given. Null when no such mob, or from inside a damage or heal hook (summon a moment later: <c>Timers.After(TimeSpan.Zero, ...)</c>).</summary>
    IMob? Summon(IUnit owner, string mob, float x, float y, TimeSpan? lifetime = null);

    /// <summary>
    /// Sets a stat modifier on <paramref name="unit"/>: <paramref name="value"/> added to buff attribute <paramref name="attribute"/> (pick one from <see cref="Stat"/>:
    /// <c>Stat.AttackDamage</c>, <c>Stat.MagicSpellDamage</c>, <c>Stat.DefenseVsMobs</c>, <c>Stat.CooldownReduction</c>, …) under the key
    /// <paramref name="source"/>, until cleared, the unit gone or the module unloaded. Server-side only: movement speed is the client's own
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

    /// <summary>The same for a mob: spells.xml spell <paramref name="spell"/>'s effects as <paramref name="caster"/>'s hit, at once and without a cast bar. For one with a cast bar: <see cref="IMob.CastSpell"/>.</summary>
    bool CastSpell(IMob caster, string spell, IUnit? target = null, float? x = null, float? y = null);

    /// <summary>Interrupts <paramref name="unit"/>'s cast or channel under way.</summary>
    bool Interrupt(IUnit unit);

    /// <summary>The unit's stats now (health, energy, item power, resistances).</summary>
    UnitStats Stats(IUnit unit);

    /// <summary>The units within <paramref name="radius"/> m of (x, y) in <paramref name="cluster"/> (players and living mobs).</summary>
    IReadOnlyList<IUnit> UnitsAround(string cluster, float x, float y, float radius);

    /// <summary>Whether <paramref name="caster"/> may hurt <paramref name="target"/> (a mob: attackable; a player: Hostile by the PvP rules).</summary>
    bool IsEnemy(IUnit caster, IUnit target);
}

/// <summary>A spell on a unit (a live view during the call; keep the unit, not the buff).</summary>
public interface IBuff
{
    /// <summary>The spell.</summary>
    SpellInfo Spell { get; }

    /// <summary>The caster's object id (0 = the world).</summary>
    long CasterId { get; }

    /// <summary>The caster when it is still on the unit's map.</summary>
    IUnit? Caster { get; }

    /// <summary>The stacks it has now.</summary>
    int Stacks { get; }

    /// <summary>Seconds until its longest timed effect ends; <see cref="double.PositiveInfinity"/> when it lasts until removed.</summary>
    double SecondsLeft { get; }

    /// <summary>
    /// This module's data on this buff entry (key <paramref name="key"/>; made by <paramref name="factory"/> on first use), gone with the entry
    /// or at the module's unload.
    /// </summary>
    T GetData<T>(string key, Func<T> factory);

    /// <summary>Sets this module's data <paramref name="key"/> on this buff entry (see <see cref="GetData"/>).</summary>
    void SetData<T>(string key, T value);

    /// <summary>This module's data of class <typeparamref name="T"/> on this buff entry, made on first use. It is <see cref="GetData{T}"/> with the class as the key.</summary>
    T Data<T>() where T : class, new() => GetData(typeof(T).FullName!, static () => new T());
}

/// <summary>A module area (<see cref="IGameplay.CreateArea"/>); Dispose ends it.</summary>
public interface IModuleArea : IDisposable
{
    /// <summary>The cluster it is on.</summary>
    string Cluster { get; }
    /// <summary>The centre's X. Set it to move the area.</summary>
    float X { get; set; }
    /// <summary>The centre's Y. Set it to move the area.</summary>
    float Y { get; set; }
    /// <summary>The radius in metres. Set it to resize the area.</summary>
    float Radius { get; set; }
    /// <summary>The unit that owns it: the area ends when this unit leaves the map.</summary>
    IUnit Owner { get; }
    /// <summary>It has ended (its duration, Dispose, its owner gone or the module unloaded) and will not tick again.</summary>
    bool Ended { get; }
}

/// <summary>One tick of a module area: the units inside (living mobs and joined players, bots included).</summary>
public sealed class AreaTickArgs(IModuleArea area, IReadOnlyList<IUnit> inside, int tick)
{
    /// <summary>The area that ticked: move, resize or end it here.</summary>
    public IModuleArea Area { get; } = area;
    /// <summary>The units inside now.</summary>
    public IReadOnlyList<IUnit> Inside { get; } = inside;

    /// <summary>The tick, from 1.</summary>
    public int Tick { get; } = tick;
}

/// <summary>A unit's stats (<see cref="IGameplay.Stats"/>).</summary>
/// <param name="Health">Current health.</param>
/// <param name="MaxHealth">Maximum health.</param>
/// <param name="Energy">Current energy.</param>
/// <param name="MaxEnergy">Maximum energy.</param>
/// <param name="ItemPower">A player's main-hand item power; 0 with an empty hand and for a mob.</param>
/// <param name="ArmorResist">The armour (physical resistance).</param>
/// <param name="MagicResist">The magic resistance.</param>
/// <param name="CrowdControlResist">The crowd-control resistance now.</param>
/// <param name="Tier">A player's main-hand tier (0 with an empty hand); a mob's mobs.xml tier.</param>
public sealed record UnitStats(float Health, float MaxHealth, float Energy, float MaxEnergy, double ItemPower, double ArmorResist, double MagicResist,
    double CrowdControlResist, int Tier);

/// <summary>A unit's timer (<see cref="IUnit.AddTimer"/>).</summary>
public interface IUnitTimer : IDisposable
{
    /// <summary>The name it was given; null for an unnamed timer.</summary>
    string? Name { get; }

    /// <summary>How many times it has fired.</summary>
    int Fired { get; }

    /// <summary>It will not fire again: cancelled, its repeats done, its unit gone or the module unloaded.</summary>
    bool Cancelled { get; }
}

/// <summary>
/// A scripted spell (<see cref="ISpellHooks.Register"/>): callbacks that implement spell <c>spell</c> server-side. OnCast when the cast goes through
/// (energy paid, cooldown started), OnHit when its hit lands (after hitdelay and a projectile's flight), OnTick at each channel tick. With
/// <see cref="ReplaceEffects"/> (the default) the spells.xml effects of the hit and of the channel ticks do not run: the script is the spell.
/// </summary>
public sealed class CustomSpell
{
    /// <summary>True (the default): the spells.xml effects of the hit and of the channel ticks do not run. False: the callbacks run as well as those effects.</summary>
    public bool ReplaceEffects { get; init; } = true;
    /// <summary>Called when the cast goes through (energy paid, cooldown started); null for nothing.</summary>
    public Action<CustomSpellContext>? OnCast { get; init; }
    /// <summary>Called when the hit lands (after hitdelay and a projectile's flight); null for nothing.</summary>
    public Action<CustomSpellContext>? OnHit { get; init; }
    /// <summary>Called at each channel tick (<see cref="CustomSpellContext.Tick"/>); null for nothing.</summary>
    public Action<CustomSpellContext>? OnTick { get; init; }
}

/// <summary>What a scripted spell's callback runs with.</summary>
public sealed class CustomSpellContext(IUnit caster, SpellInfo spell, byte slot, ItemType? item, IUnit? target, float x, float y, double abilityPower, int tick, IGameplay game)
{
    /// <summary>The unit casting it: a player or a mob.</summary>
    public IUnit Caster { get; } = caster;
    /// <summary>The caster when it is a player.</summary>
    public IPlayer? Player => Caster as IPlayer;
    /// <summary>The spell.</summary>
    public SpellInfo Spell { get; } = spell;
    /// <summary>The raw CharacterSpellSlot of the cast (see <see cref="SpellArgsBase.SlotIndex"/>).</summary>
    public byte SlotIndex { get; } = slot;
    /// <summary><see cref="SlotIndex"/> as a <see cref="SpellSlot"/>.</summary>
    public SpellSlot Slot => SpellSlots.FromIndex(SlotIndex);
    /// <summary>The item that granted the spell; null when none.</summary>
    public ItemType? Item { get; } = item;

    /// <summary>The unit it was cast at; null for a ground or self cast.</summary>
    public IUnit? Target { get; } = target;

    /// <summary>The aim point or the target's position.</summary>
    public float X { get; } = x;
    /// <summary>The Y of <see cref="X"/>'s point.</summary>
    public float Y { get; } = y;

    /// <summary>The ability power of the item that granted it.</summary>
    public double AbilityPower { get; } = abilityPower;

    /// <summary>OnTick: the channel tick, from 1.</summary>
    public int Tick { get; } = tick;

    /// <summary>The actions (<see cref="IModuleContext.Game"/>).</summary>
    public IGameplay Game { get; } = game;
}
