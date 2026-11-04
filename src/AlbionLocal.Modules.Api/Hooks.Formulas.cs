namespace AlbionLocal.Modules;

/// <summary>The numbers the engine works out: a handler sets Value, later handlers see it. A value that is not finite is ignored and a negative one counts as 0.</summary>
public interface IFormulaHooks
{
    /// <summary>A player's cast time (seconds, after cast-time reductions); id = spell.</summary>
    IHookRegistration OnCalcCastTime(Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcCastTime, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcCastTime(string spell, Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player's cooldown (seconds, after cooldown reductions); id = spell.</summary>
    IHookRegistration OnCalcCooldown(Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcCooldown, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcCooldown(string spell, Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A player's energy cost of a cast; id = spell.</summary>
    IHookRegistration OnCalcEnergyCost(Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcEnergyCost, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcEnergyCost(string spell, Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A spell's heal on a player before it lands; id = spell.</summary>
    IHookRegistration OnCalcHeal(Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcHeal, for one spell (its spells.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcHeal(string spell, Action<SpellFormulaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The threat a player's damage puts on a mob (mob AI); id = the mob.</summary>
    IHookRegistration OnCalcThreat(Action<UnitFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcThreat, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcThreat(string mob, Action<UnitFormulaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The fame a kill gives each player of the fame group (whole fame units, after rates); id = the mob.</summary>
    IHookRegistration OnCalcFame(Action<UnitFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcFame, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcFame(string mob, Action<UnitFormulaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>The silver a killed mob drops (a pile's amount); id = the mob.</summary>
    IHookRegistration OnCalcSilver(Action<UnitFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcSilver, for one mob (its mobs.xml uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcSilver(string mob, Action<UnitFormulaArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>An item's item power as worn by a player (damage, ability power and stats scale with it); id = the item.</summary>
    IHookRegistration OnCalcItemPower(Action<ItemFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcItemPower, for one item (its uniquename, or a mask with * and ?). The id has no "@n" enchantment suffix.</summary>
    IHookRegistration OnCalcItemPower(string item, Action<ItemFormulaArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>A number about a spell: OnCalcCastTime, OnCalcCooldown, OnCalcEnergyCost, OnCalcHeal.</summary>
public sealed class SpellFormulaArgs(IUnit caster, SpellInfo spell, IUnit? target, double value)
{
    /// <summary>The player casting or healing.</summary>
    public IUnit Caster { get; } = caster;
    /// <summary>The spell.</summary>
    public SpellInfo Spell { get; } = spell;
    /// <summary>The unit it is aimed at; null for a ground or self cast.</summary>
    public IUnit? Target { get; } = target;
    /// <summary>The number the engine is about to use, in the hook's units; set it to change that.</summary>
    public double Value { get; set; } = value;

    /// <summary>The value before any handler changed it.</summary>
    public double OriginalValue { get; } = value;
}

/// <summary>A number about a player and a mob: OnCalcThreat, OnCalcFame, OnCalcSilver.</summary>
public sealed class UnitFormulaArgs(IPlayer player, IUnit unit, double value)
{
    /// <summary>The player concerned (the attacker, the fame receiver, the killer).</summary>
    public IPlayer Player { get; } = player;

    /// <summary>The mob concerned.</summary>
    public IUnit Unit { get; } = unit;

    /// <summary>The number the engine is about to use, in the hook's units; set it to change that.</summary>
    public double Value { get; set; } = value;

    /// <summary>The value before any handler changed it.</summary>
    public double OriginalValue { get; } = value;
}

/// <summary>An item's item power as worn by a player (OnCalcItemPower).</summary>
public sealed class ItemFormulaArgs(IPlayer player, ItemInfo item, double value)
{
    /// <summary>The player wearing it.</summary>
    public IPlayer Player { get; } = player;
    /// <summary>The item.</summary>
    public ItemInfo Item { get; } = item;
    /// <summary>The item power the engine is about to use; set it to change that.</summary>
    public double Value { get; set; } = value;

    /// <summary>The value before any handler changed it.</summary>
    public double OriginalValue { get; } = value;
}
