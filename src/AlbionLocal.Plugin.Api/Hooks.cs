namespace AlbionLocal.Plugin.Api;

// API v2: the gameplay hooks (shaped after tswow's livescripts events, TSEvents.h). Every hook can be registered for every id (spell,
// buff, item, mount or mob uniquename) or for one: Hooks.Spells.OnCheckCast(h) / Hooks.Spells.OnCheckCast("MY_SPELL", h). A hook nobody
// listens to costs one check; a filtered one a dictionary lookup. Handlers run on the world thread inside the plugin safety wrapper (an
// exception is logged and counted, the game goes on); the args objects are valid only during the call. Every registration returns an
// IDisposable that removes it; all of a plugin's registrations go at its unload.

/// <summary>A value a handler may read and change (tswow's TSMutable): <see cref="Get"/>/<see cref="Set"/> or <see cref="Value"/>.</summary>
public sealed class Mutable<T>(T value)
{
    private readonly T _original = value;

    public T Value { get; set; } = value;

    public T Get() => Value;

    public void Set(T value) => Value = value;

    /// <summary>The value before any handler changed it.</summary>
    public T Original => _original;

    public override string ToString() => $"{Value}";
}

/// <summary>Args of a hook that can stop what is about to happen (a cast, a buff, a mount): <see cref="Cancel"/>, with a reason the player is told.</summary>
public abstract class CancellableArgs
{
    public bool Cancelled { get; private set; }

    /// <summary>Why it was cancelled (sent to the player as an error line when not empty).</summary>
    public string? Reason { get; private set; }

    /// <summary>Stops it. The engine still sends the client what a refusal needs (a cast refusal, a mount refusal).</summary>
    public void Cancel(string? reason = null)
    {
        Cancelled = true;
        Reason ??= reason;
    }
}

/// <summary>Args of a hook whose default effect can be skipped (the spells.xml effects of a hit, damage): <see cref="PreventDefault"/>.</summary>
public abstract class PreventableArgs
{
    public bool DefaultPrevented { get; private set; }

    /// <summary>The engine does not run its own effect (the plugin does whatever it wants instead).</summary>
    public void PreventDefault() => DefaultPrevented = true;
}

/// <summary>The spell slot of a cast (Albion's CharacterSpellSlot; the keys are the default key bindings).</summary>
public enum SpellSlot
{
    /// <summary>Q: the main hand's first spell.</summary>
    Q = 0,
    /// <summary>W: the main hand's second spell.</summary>
    W = 1,
    /// <summary>E: the off hand's or the two-handed weapon's third spell.</summary>
    E = 2,
    /// <summary>R: the armour's spell.</summary>
    Armor = 3,
    /// <summary>D: the helmet's spell.</summary>
    Head = 4,
    /// <summary>F: the shoes' spell.</summary>
    Shoes = 5,
    Potion = 12,
    Food = 13,

    // The passive slots (not CharacterSpellSlots: no cast comes from them; IPlayer.GetSlotSpell / SetSlotSpell only).

    /// <summary>The main-hand weapon's passive.</summary>
    WeaponPassive = 100,
    /// <summary>The armour's first and second passive.</summary>
    ArmorPassive = 101,
    ArmorPassive2 = 102,
    HeadPassive = 103,
    ShoesPassive = 104,
    CapePassive = 105,
    BagPassive = 106,
    /// <summary>The mount item's first and second passive (the mount's own active spells are fixed by items.xml and cannot be changed).</summary>
    MountPassive = 107,
    MountPassive2 = 108,

    // The extra action bars (analysis/work/stageN/extrabars/SPEC.md): 12 slots filled from the worn items' extra spells (server data
    // extraspells.json) or by plugins (IPlayer.SetExtraSlot); cast through the client mod. Extra1-6 = bar 1 (Shift+1..6), Extra7-12 = bar 2 (Ctrl+1..6).
    Extra1 = 200, Extra2 = 201, Extra3 = 202, Extra4 = 203, Extra5 = 204, Extra6 = 205,
    Extra7 = 206, Extra8 = 207, Extra9 = 208, Extra10 = 209, Extra11 = 210, Extra12 = 211,

    /// <summary>Any other slot (a mob's cast, a server-side cast).</summary>
    Other = 255,
}

/// <summary>Helpers for <see cref="SpellSlot"/>.</summary>
public static class SpellSlots
{
    /// <summary>The first extra slot's raw index (<see cref="SpellSlot.Extra1"/>).</summary>
    public const byte ExtraBase = 200;

    /// <summary>How many extra slots exist at most (2 bars of 6).</summary>
    public const int ExtraCount = 12;

    /// <summary>A cast's raw slot index as a <see cref="SpellSlot"/>: 0-5, 12, 13 the CharacterSpellSlots, 200-211 the extra slots, else Other.</summary>
    public static SpellSlot FromIndex(byte index) =>
        index is <= 5 or 12 or 13 || index is >= ExtraBase and < ExtraBase + ExtraCount ? (SpellSlot)index : SpellSlot.Other;

    /// <summary>The extra slot of a 0-based index (0 → Extra1).</summary>
    public static SpellSlot Extra(int index) => (SpellSlot)(ExtraBase + index);

    /// <summary>The 0-based extra index of <paramref name="slot"/>, or -1 when it is not an extra slot.</summary>
    public static int ExtraIndex(SpellSlot slot) => slot is >= SpellSlot.Extra1 and <= SpellSlot.Extra12 ? (int)slot - ExtraBase : -1;
}

/// <summary>What <see cref="IPlayer.SetSlotSpell"/> accepts.</summary>
public enum SlotSpellMode
{
    /// <summary>Only a spell the equipped item offers for that slot (as the player's own choice in the item's window); anything else is refused.</summary>
    FromItemList,

    /// <summary>
    /// EXPERIMENTAL: any spell of the slot's kind (an active spell for Q-F, potion and food; a passive for a passive slot). The server casts it
    /// (and works out the passive) as if the item offered it; the client shows it on the action bar and casts it by its id (it looks the id up
    /// in the whole spell table, not in the item's list), but the item's own spell window shows none of its options as chosen.
    /// </summary>
    Any,
}

/// <summary>The cooldowns at a slot change (<see cref="IPlayer.SetSlotSpell"/>).</summary>
public enum SlotSpellCooldown
{
    /// <summary>Each spell keeps its own cooldown (as the game does): the new spell is ready unless it was itself cooling down.</summary>
    Keep,
    /// <summary>The new spell takes over what is left of the old one's cooldown (no free cast by swapping).</summary>
    Carry,
    /// <summary>The new spell is ready at once.</summary>
    Reset,
}

/// <summary>Who changed a slot's spell (<see cref="SlotSpellChangedArgs.Source"/>).</summary>
public enum SlotChangeSource
{
    /// <summary>The player chose it in the item's spell window (EquipmentItemChangeSpell).</summary>
    Player,
    /// <summary>A plugin or a rule (<see cref="IPlayer.SetSlotSpell"/>), or the GM command .slot.</summary>
    Plugin,
    /// <summary>A temporary change ran out and the previous spell came back.</summary>
    Revert,
}

/// <summary>A slot of an equipped item holds another spell now (<see cref="ISpellHooks.OnSlotSpellChanged(Action{SlotSpellChangedArgs})"/>; id = the new spell).</summary>
public sealed class SlotSpellChangedArgs(IPlayer player, SpellSlot slot, string? item, string? from, string? to, SlotChangeSource source, bool temporary)
    : PlayerArgs(player)
{
    public SpellSlot Slot { get; } = slot;

    /// <summary>The item whose slot changed (uniquename with "@n"); null for none.</summary>
    public string? Item { get; } = item;

    /// <summary>The spell before and after (uniquenames; null = none).</summary>
    public string? From { get; } = from;
    public string? To { get; } = to;

    public SlotChangeSource Source { get; } = source;

    /// <summary>A change with a duration: the previous spell comes back when it runs out (then a change with <see cref="SlotChangeSource.Revert"/>).</summary>
    public bool Temporary { get; } = temporary;
}

/// <summary>Who changed an extra slot (<see cref="ExtraBarChangedArgs.Source"/>).</summary>
public enum ExtraBarChangeSource
{
    /// <summary>The worn items changed (equip, unequip, login, a reload of extraspells.json).</summary>
    Equipment,
    /// <summary>A plugin, a rule (extrabar.set) or the GM command .extrabar.</summary>
    Plugin,
}

/// <summary>
/// An extra slot holds another spell now (<see cref="ISpellHooks.OnExtraBarChanged(Action{ExtraBarChangedArgs})"/>; id = the new spell, "" when it
/// became empty). One call per changed slot.
/// </summary>
public sealed class ExtraBarChangedArgs(IPlayer player, int index, string? from, string? to, string? item, ExtraBarChangeSource source) : PlayerArgs(player)
{
    /// <summary>The 0-based extra slot (0-5 bar 1, 6-11 bar 2).</summary>
    public int Index { get; } = index;

    /// <summary>The slot as a <see cref="SpellSlot"/> (Extra1..Extra12).</summary>
    public SpellSlot Slot => SpellSlots.Extra(Index);

    /// <summary>The spell before and after (uniquenames; null = empty).</summary>
    public string? From { get; } = from;
    public string? To { get; } = to;

    /// <summary>The worn item that grants <see cref="To"/> (uniquename with "@n"); null for an override or an empty slot.</summary>
    public string? Item { get; } = item;

    public ExtraBarChangeSource Source { get; } = source;
}

/// <summary>A hook point with an id filter, as returned by the hook groups' registrations.</summary>
public interface IHookRegistration : IDisposable
{
    /// <summary>The id it listens to; null = all.</summary>
    string? Id { get; }
}

/// <summary>All the v2 hooks (<see cref="IPluginContext.Hooks"/>).</summary>
public interface IHooks
{
    ISpellHooks Spells { get; }
    IBuffHooks Buffs { get; }
    IItemHooks Items { get; }
    IMountHooks Mounts { get; }
    IUnitHooks Units { get; }
    IFormulaHooks Formulas { get; }
}

// ---- spells (id = the spell's uniquename)

/// <summary>
/// The spell lifecycle (as TrinityCore's SpellScript): OnCheckCast (cancel) → OnCast (the cast went through) → OnBeforeHit (its hit lands;
/// PreventDefault skips the spells.xml effects) → OnEffect per unit it reaches (PreventDefault: not on this unit) → OnDamageEarly /
/// OnDamageLate per health change → OnAfterHit; OnCancel when it is cancelled or interrupted; OnTick per channel tick; OnAreaCreate /
/// OnAreaTick for its spelleffectarea. Players' casts; OnCast, OnCancel, OnDamageEarly/Late also for mobs (Caster is an IMob).
/// </summary>
public interface ISpellHooks
{
    IHookRegistration OnCheckCast(Action<SpellCheckCastArgs> handler);
    IHookRegistration OnCheckCast(string spell, Action<SpellCheckCastArgs> handler);
    IHookRegistration OnCast(Action<SpellCastArgs> handler);
    IHookRegistration OnCast(string spell, Action<SpellCastArgs> handler);
    IHookRegistration OnCancel(Action<SpellCancelArgs> handler);
    IHookRegistration OnCancel(string spell, Action<SpellCancelArgs> handler);
    IHookRegistration OnBeforeHit(Action<SpellHitArgs> handler);
    IHookRegistration OnBeforeHit(string spell, Action<SpellHitArgs> handler);
    IHookRegistration OnAfterHit(Action<SpellHitArgs> handler);
    IHookRegistration OnAfterHit(string spell, Action<SpellHitArgs> handler);
    IHookRegistration OnEffect(Action<SpellEffectArgs> handler);
    IHookRegistration OnEffect(string spell, Action<SpellEffectArgs> handler);
    IHookRegistration OnDamageEarly(Action<SpellDamageArgs> handler);
    IHookRegistration OnDamageEarly(string spell, Action<SpellDamageArgs> handler);
    IHookRegistration OnDamageLate(Action<SpellDamageArgs> handler);
    IHookRegistration OnDamageLate(string spell, Action<SpellDamageArgs> handler);
    IHookRegistration OnTick(Action<SpellTickArgs> handler);
    IHookRegistration OnTick(string spell, Action<SpellTickArgs> handler);
    IHookRegistration OnAreaCreate(Action<SpellAreaArgs> handler);
    IHookRegistration OnAreaCreate(string spell, Action<SpellAreaArgs> handler);
    IHookRegistration OnAreaTick(Action<SpellAreaArgs> handler);
    IHookRegistration OnAreaTick(string spell, Action<SpellAreaArgs> handler);

    /// <summary>
    /// A slot of a player's equipped item holds another spell (the player's choice in the item window, <see cref="IPlayer.SetSlotSpell"/>, a
    /// temporary change running out); id = the new spell.
    /// </summary>
    IHookRegistration OnSlotSpellChanged(Action<SlotSpellChangedArgs> handler);
    IHookRegistration OnSlotSpellChanged(string spell, Action<SlotSpellChangedArgs> handler);

    /// <summary>An extra slot (the extra action bars, <see cref="IPlayer.GetExtraSlots"/>) holds another spell; id = the new spell ("" = emptied). Once per changed slot.</summary>
    IHookRegistration OnExtraBarChanged(Action<ExtraBarChangedArgs> handler);
    IHookRegistration OnExtraBarChanged(string spell, Action<ExtraBarChangedArgs> handler);

    /// <summary>
    /// A scripted spell: <paramref name="script"/>'s callbacks implement (or, with <see cref="SpellScript.ReplaceEffects"/> false, add to)
    /// spell <paramref name="spell"/> server-side. The spell must exist in spells.xml (the client casts it by its id: a ModKit pack's new spell
    /// or an official one). One script per spell; a second registration of the same spell is refused.
    /// </summary>
    IHookRegistration Register(string spell, SpellScript script);
}

/// <summary>What a cast is about (the caster, the spell, where it came from, its target).</summary>
public abstract class SpellArgsBase(IUnit caster, string spell, byte slotIndex, string? item, bool mountSpell, IUnit? target, float x, float y)
{
    public IUnit Caster { get; } = caster;

    /// <summary>The caster when it is a player.</summary>
    public IPlayer? Player => Caster as IPlayer;

    /// <summary>The spell's uniquename.</summary>
    public string Spell { get; } = spell;

    /// <summary>The raw CharacterSpellSlot (0-13; 200-211 an extra slot, cast as the wire's slot 25; 255 for a mob).</summary>
    public byte SlotIndex { get; } = slotIndex;

    public SpellSlot Slot => SpellSlots.FromIndex(SlotIndex);

    /// <summary>The item that granted the spell (the weapon, armour, helmet, shoes, potion, food or the ridden mount); null when none.</summary>
    public string? Item { get; } = item;

    /// <summary>A spell of the ridden mount (its slots replace the equipment's while mounted).</summary>
    public bool MountSpell { get; } = mountSpell;

    /// <summary>The unit it is cast at; null for a ground or self cast.</summary>
    public IUnit? Target { get; } = target;

    /// <summary>The aim point (a ground cast) or the target's position.</summary>
    public float X { get; } = x;
    public float Y { get; } = y;
}

/// <summary>A player's cast passed the engine's checks and is about to start: <see cref="CancellableArgs.Cancel"/> refuses it (the client gets the cast refusal).</summary>
public sealed class SpellCheckCastArgs(IUnit caster, string spell, byte slot, string? item, bool mount, IUnit? target, float x, float y)
    : SpellArgsBase(caster, spell, slot, item, mount, target, x, y)
{
    private readonly CancelState _cancel = new();

    public bool Cancelled => _cancel.Cancelled;
    public string? Reason => _cancel.Reason;

    /// <summary>Refuses the cast: CastStart is answered with the refusal code, <paramref name="reason"/> goes to the player as an error line.</summary>
    public void Cancel(string? reason = null) => _cancel.Cancel(reason);

    private sealed class CancelState : CancellableArgs;
}

/// <summary>
/// A cast went through (energy paid, CastFinished sent, the cooldown started; the hit follows). Its cooldown is
/// <see cref="IFormulaHooks.OnCalcCooldown(Action{SpellFormulaArgs})"/>'s (so the client shows the same).
/// </summary>
public sealed class SpellCastArgs(IUnit caster, string spell, byte slot, string? item, bool mount, IUnit? target, float x, float y)
    : SpellArgsBase(caster, spell, slot, item, mount, target, x, y);

/// <summary>A cast under way was dropped: <see cref="Interrupted"/> by the game (a stun, damage, a dead target) or else cancelled (the player, a new cast, a refusal).</summary>
public sealed class SpellCancelArgs(IUnit caster, string spell, byte slot, string? item, bool mount, IUnit? target, float x, float y, bool interrupted)
    : SpellArgsBase(caster, spell, slot, item, mount, target, x, y)
{
    public bool Interrupted { get; } = interrupted;
}

/// <summary>A player's cast's hit lands (after hitdelay and a projectile's flight). OnBeforeHit: PreventDefault skips the spells.xml effects.</summary>
public sealed class SpellHitArgs(IUnit caster, string spell, byte slot, string? item, bool mount, IUnit? target, float x, float y, bool projectile)
    : SpellArgsBase(caster, spell, slot, item, mount, target, x, y)
{
    private readonly Prevent _prevent = new();

    /// <summary>The spell flies (projectilespeed): this is its impact.</summary>
    public bool Projectile { get; } = projectile;

    public bool DefaultPrevented => _prevent.DefaultPrevented;

    /// <summary>OnBeforeHit: the spells.xml effects of the hit do not run (no effect on OnAfterHit).</summary>
    public void PreventDefault() => _prevent.PreventDefault();

    private sealed class Prevent : PreventableArgs;
}

/// <summary>
/// A spell (the cast one, one it applies, an area's effect) is about to act on one unit: <see cref="PreventableArgs.PreventDefault"/> keeps
/// all of it off that unit (the client sees the hit as blocked, as by a spell immunity). <see cref="Spell"/> is the acting spell,
/// <see cref="RootSpell"/> the cast one.
/// </summary>
public sealed class SpellEffectArgs(IUnit caster, string spell, string root, IUnit unit, bool fromArea) : PreventableArgs
{
    public IUnit Caster { get; } = caster;
    public string Spell { get; } = spell;
    public string RootSpell { get; } = root;
    public IUnit Unit { get; } = unit;

    /// <summary>Applied by a spell area (a field the unit stands in).</summary>
    public bool FromArea { get; } = fromArea;
}

/// <summary>
/// One health (or energy) change of a spell on one unit. OnDamageEarly: before the target's defences and damage shields; <see cref="Amount"/>
/// may be changed, PreventDefault drops it. OnDamageLate: after it (Amount is what the health lost or gained, <see cref="HealthLeft"/> after).
/// Heals come through too (<see cref="IsHeal"/>), and energy drains/gains (<see cref="IsEnergy"/>).
/// </summary>
public sealed class SpellDamageArgs(IUnit caster, string spell, IUnit target, double amount, bool heal, bool energy, bool overTime, bool area, bool magic) : PreventableArgs
{
    public IUnit Caster { get; } = caster;
    public string Spell { get; } = spell;
    public IUnit Target { get; } = target;

    /// <summary>The amount (positive for damage and heals alike).</summary>
    public Mutable<double> Amount { get; } = new(amount);

    public bool IsHeal { get; } = heal;
    public bool IsEnergy { get; } = energy;

    /// <summary>A damage- or heal-over-time tick.</summary>
    public bool OverTime { get; } = overTime;

    /// <summary>An area node's hit (effectarearadius) or a spell area's.</summary>
    public bool Area { get; } = area;

    /// <summary>Magic (else physical) damage.</summary>
    public bool Magic { get; } = magic;

    /// <summary>OnDamageLate: the target's health after the hit.</summary>
    public float HealthLeft { get; set; }
}

/// <summary>A channel tick of a player's channelled spell (PARRY, BLOCK, PYROBLAST…): <see cref="Tick"/> from 1 of <see cref="Count"/>.</summary>
public sealed class SpellTickArgs(IUnit caster, string spell, string channel, int tick, int count, IUnit? target, float x, float y)
{
    public IUnit Caster { get; } = caster;

    /// <summary>The cast spell.</summary>
    public string Spell { get; } = spell;

    /// <summary>The channel spell (the next spell id, "…_CHANNEL").</summary>
    public string Channel { get; } = channel;

    public int Tick { get; } = tick;
    public int Count { get; } = count;
    public IUnit? Target { get; } = target;
    public float X { get; } = x;
    public float Y { get; } = y;
}

/// <summary>A spell area (spelleffectarea, a field on the ground): created, or one of its ticks with the units inside.</summary>
public sealed class SpellAreaArgs(IUnit owner, string spell, string? effect, long areaId, float x, float y, float radius, double seconds, IReadOnlyList<IUnit> inside)
{
    public IUnit Owner { get; } = owner;

    /// <summary>The spell that holds the area node.</summary>
    public string Spell { get; } = spell;

    /// <summary>The spell it applies to who enters; null for a visual one.</summary>
    public string? Effect { get; } = effect;

    public long AreaId { get; } = areaId;
    public float X { get; } = x;
    public float Y { get; } = y;
    public float Radius { get; } = radius;

    /// <summary>Its lifetime in seconds (0 = until its owner removes it).</summary>
    public double Seconds { get; } = seconds;

    /// <summary>OnAreaTick: the units inside now (empty at OnAreaCreate).</summary>
    public IReadOnlyList<IUnit> Inside { get; } = inside;
}

// ---- buffs (id = the buff spell's uniquename)

/// <summary>The life of a spell on a unit (a buff, debuff, crowd control, mount buff), as TrinityCore's AuraScript.</summary>
public interface IBuffHooks
{
    /// <summary>Before a spell's timed effects go on a unit (cast spells, area effects, procs): Cancel keeps it off.</summary>
    IHookRegistration OnCheckApply(Action<BuffCheckArgs> handler);
    IHookRegistration OnCheckApply(string buff, Action<BuffCheckArgs> handler);

    /// <summary>It went on (new, or re-applied: a stack more).</summary>
    IHookRegistration OnApply(Action<BuffArgs> handler);
    IHookRegistration OnApply(string buff, Action<BuffArgs> handler);

    /// <summary>Its stacks changed (re-applied, charges consumed).</summary>
    IHookRegistration OnStackChange(Action<BuffStackArgs> handler);
    IHookRegistration OnStackChange(string buff, Action<BuffStackArgs> handler);

    /// <summary>A tick of its over-time effect (damage/heal over time, a pulse).</summary>
    IHookRegistration OnTick(Action<BuffArgs> handler);
    IHookRegistration OnTick(string buff, Action<BuffArgs> handler);

    /// <summary>It went off: expired, removed (a cleanse, a dispel, a plugin), or cleared (death, reset, knockdown).</summary>
    IHookRegistration OnRemove(Action<BuffRemoveArgs> handler);
    IHookRegistration OnRemove(string buff, Action<BuffRemoveArgs> handler);
}

public sealed class BuffCheckArgs(IUnit unit, string buff, IUnit? caster) : CancellableArgs
{
    public IUnit Unit { get; } = unit;
    public string Buff { get; } = buff;

    /// <summary>Who applies it (null: the world, or a caster no longer on the map).</summary>
    public IUnit? Caster { get; } = caster;
}

public class BuffArgs(IUnit unit, IBuff buff, bool isNew)
{
    public IUnit Unit { get; } = unit;
    public IBuff Buff { get; } = buff;

    /// <summary>OnApply: a new entry (else a re-application: a stack more and a restart).</summary>
    public bool IsNew { get; } = isNew;
}

public sealed class BuffStackArgs(IUnit unit, IBuff buff, int before) : BuffArgs(unit, buff, false)
{
    public int Before { get; } = before;
    public int After => Buff.Stacks;
}

public enum BuffEnd
{
    Expired,
    Removed,
    Cleared,
}

public sealed class BuffRemoveArgs(IUnit unit, IBuff buff, BuffEnd how) : BuffArgs(unit, buff, false)
{
    public BuffEnd How { get; } = how;
}

// ---- items (id = the item's uniquename, without "@n")

public interface IItemHooks
{
    /// <summary>An item went into an equipment slot (also every worn item at a login: <see cref="EquipArgs.AtLogin"/>).</summary>
    IHookRegistration OnEquip(Action<EquipArgs> handler);
    IHookRegistration OnEquip(string item, Action<EquipArgs> handler);
    IHookRegistration OnUnequip(Action<EquipArgs> handler);
    IHookRegistration OnUnequip(string item, Action<EquipArgs> handler);

    /// <summary>The main-hand weapon changed (id = the new weapon; "" when the hand is empty now).</summary>
    IHookRegistration OnWeaponSwap(Action<WeaponSwapArgs> handler);
    IHookRegistration OnWeaponSwap(string item, Action<WeaponSwapArgs> handler);

    /// <summary>An item's durability reached 0 (death and knockdown wear, a gathering tool, a fishing rod).</summary>
    IHookRegistration OnBreak(Action<ItemBreakArgs> handler);
    IHookRegistration OnBreak(string item, Action<ItemBreakArgs> handler);

    /// <summary>
    /// The worn items' passives are worked out again (the equipment changed): add stat modifiers with <see cref="PassiveArgs.Add"/>
    /// (the buff attributes of spells.xml: physicalattackdamagebonus, magicspelldamagebonus, movespeedbonus, …). A filtered hook runs
    /// when that item is worn.
    /// </summary>
    IHookRegistration OnCalcPassives(Action<PassiveArgs> handler);
    IHookRegistration OnCalcPassives(string item, Action<PassiveArgs> handler);
}

public sealed class EquipArgs(IPlayer player, string item, int quality, int slot, bool atLogin) : PlayerArgs(player)
{
    public string Item { get; } = item;
    public int Quality { get; } = quality;

    /// <summary>The equipment slot (0 main hand, 1 off hand, 2 head, 3 armour, 4 shoes, 5 bag, 6 cape, 7 mount, 8 potion, 9 food; -1 unknown).</summary>
    public int Slot { get; } = slot;

    public bool AtLogin { get; } = atLogin;

    public bool IsWeapon => Slot is 0 or 1;
}

public sealed class WeaponSwapArgs(IPlayer player, string? from, string? to) : PlayerArgs(player)
{
    public string? From { get; } = from;
    public string? To { get; } = to;
}

public sealed class ItemBreakArgs(IPlayer player, string item, string cause) : PlayerArgs(player)
{
    public string Item { get; } = item;

    /// <summary>"death", "knockdown", "gathering", "fishing".</summary>
    public string Cause { get; } = cause;
}

public sealed class PassiveArgs(IPlayer player, IReadOnlyList<string> worn, Action<string, double> add) : PlayerArgs(player)
{
    /// <summary>The worn items (uniquenames).</summary>
    public IReadOnlyList<string> Worn { get; } = worn;

    /// <summary>The item the filtered hook runs for (its id); null for a global one.</summary>
    public string? Item { get; set; }

    /// <summary>Adds <paramref name="value"/> to stat <paramref name="attribute"/> while this equipment is worn (summed with the passives).</summary>
    public void Add(string attribute, double value) => add(attribute, value);
}

// ---- mounts (id = the mount item's uniquename)

public interface IMountHooks
{
    /// <summary>A mount was asked for and passed the engine's checks: Cancel refuses it (the client's mount bar aborts).</summary>
    IHookRegistration OnCheckMount(Action<MountCheckArgs> handler);
    IHookRegistration OnCheckMount(string mount, Action<MountCheckArgs> handler);
    IHookRegistration OnMountStart(Action<MountArgs> handler);
    IHookRegistration OnMountStart(string mount, Action<MountArgs> handler);

    /// <summary>Mounted (the bar is full). <see cref="MountedArgs.GallopBuff"/> may be changed: the speed buff the mount's gallop applies.</summary>
    IHookRegistration OnMounted(Action<MountedArgs> handler);
    IHookRegistration OnMounted(string mount, Action<MountedArgs> handler);
    IHookRegistration OnMountCancel(Action<MountArgs> handler);
    IHookRegistration OnMountCancel(string mount, Action<MountArgs> handler);

    /// <summary>Off the mount: <see cref="MountArgs.Reason"/> "dismount", "knockdown", "died" (its bar ran out), "destroyed".</summary>
    IHookRegistration OnDismount(Action<MountArgs> handler);
    IHookRegistration OnDismount(string mount, Action<MountArgs> handler);

    /// <summary>A hit on the mount bar: <see cref="MountDamageArgs.Amount"/> may be changed.</summary>
    IHookRegistration OnMountDamage(Action<MountDamageArgs> handler);
    IHookRegistration OnMountDamage(string mount, Action<MountDamageArgs> handler);

    /// <summary>A mount spell (the ridden mount's ability) was cast.</summary>
    IHookRegistration OnMountSpell(Action<SpellCastArgs> handler);
    IHookRegistration OnMountSpell(string mount, Action<SpellCastArgs> handler);
}

public class MountArgs(IPlayer player, string mount, string reason) : PlayerArgs(player)
{
    /// <summary>The mount item's uniquename.</summary>
    public string Mount { get; } = mount;

    public string Reason { get; } = reason;
}

public sealed class MountCheckArgs(IPlayer player, string mount, bool remount) : CancellableArgs
{
    public IPlayer Player { get; } = player;
    public string Mount { get; } = mount;

    /// <summary>The client asked for a remount at its mount object.</summary>
    public bool Remount { get; } = remount;
}

public sealed class MountedArgs(IPlayer player, string mount, string? gallop) : MountArgs(player, mount, "mounted")
{
    /// <summary>The spell the gallop (castspellaftermoving) applies; set another spells.xml buff for a faster (or slower) gallop, null for none.</summary>
    public Mutable<string?> GallopBuff { get; } = new(gallop);
}

public sealed class MountDamageArgs(IPlayer player, string mount, double amount, IUnit? attacker) : MountArgs(player, mount, "damage")
{
    public Mutable<double> Amount { get; } = new(amount);
    public IUnit? Attacker { get; } = attacker;

    /// <summary>The bar after the hit (set after the handlers).</summary>
    public float Left { get; set; }
}

// ---- units (id = a mob's uniquename; players are matched by the global registration only)

public interface IUnitHooks
{
    IHookRegistration OnHealthChange(Action<UnitValueArgs> handler);
    IHookRegistration OnHealthChange(string mob, Action<UnitValueArgs> handler);
    IHookRegistration OnEnergyChange(Action<UnitValueArgs> handler);
    IHookRegistration OnEnergyChange(string mob, Action<UnitValueArgs> handler);

    /// <summary>A player entered combat (with a mob or a player).</summary>
    IHookRegistration OnEnterCombat(Action<UnitArgs> handler);

    /// <summary>A player's combat ended.</summary>
    IHookRegistration OnLeaveCombat(Action<UnitArgs> handler);

    /// <summary>A knocked-down player got up (the timer, a revive by an ally, a resurrection, a GM).</summary>
    IHookRegistration OnRevive(Action<UnitArgs> handler);

    /// <summary>
    /// Replayed for every joined player and living mob right after this plugin was (re)loaded, so it can rebuild its per-entity state
    /// (data bags and entity timers are cleared at an unload).
    /// </summary>
    IHookRegistration OnReload(Action<UnitArgs> handler);
    IHookRegistration OnReload(string mob, Action<UnitArgs> handler);
}

public class UnitArgs(IUnit unit)
{
    public IUnit Unit { get; } = unit;
}

public sealed class UnitValueArgs(IUnit unit, float before, float after, float max, IUnit? source, string? spell) : UnitArgs(unit)
{
    public float Before { get; } = before;
    public float After { get; } = after;
    public float Max { get; } = max;

    /// <summary>Who caused it (the attacker or healer); null for regeneration or unknown.</summary>
    public IUnit? Source { get; } = source;

    /// <summary>The spell, when a spell did it.</summary>
    public string? Spell { get; } = spell;

    public float Share => Max > 0 ? After / Max : 0;
}

// ---- formulas (OnCalc*: change a number the engine is about to use)

public interface IFormulaHooks
{
    /// <summary>A player's cast time (seconds, after cast-time reductions); id = spell.</summary>
    IHookRegistration OnCalcCastTime(Action<SpellFormulaArgs> handler);
    IHookRegistration OnCalcCastTime(string spell, Action<SpellFormulaArgs> handler);

    /// <summary>A player's cooldown (seconds, after cooldown reductions); id = spell.</summary>
    IHookRegistration OnCalcCooldown(Action<SpellFormulaArgs> handler);
    IHookRegistration OnCalcCooldown(string spell, Action<SpellFormulaArgs> handler);

    /// <summary>A player's energy cost of a cast; id = spell.</summary>
    IHookRegistration OnCalcEnergyCost(Action<SpellFormulaArgs> handler);
    IHookRegistration OnCalcEnergyCost(string spell, Action<SpellFormulaArgs> handler);

    /// <summary>A spell's heal on a player before it lands; id = spell.</summary>
    IHookRegistration OnCalcHeal(Action<SpellFormulaArgs> handler);
    IHookRegistration OnCalcHeal(string spell, Action<SpellFormulaArgs> handler);

    /// <summary>The threat a player's damage puts on a mob (mob AI); id = the mob.</summary>
    IHookRegistration OnCalcThreat(Action<UnitFormulaArgs> handler);
    IHookRegistration OnCalcThreat(string mob, Action<UnitFormulaArgs> handler);

    /// <summary>The fame a kill gives each player of the fame group (whole fame units, after rates); id = the mob.</summary>
    IHookRegistration OnCalcFame(Action<UnitFormulaArgs> handler);
    IHookRegistration OnCalcFame(string mob, Action<UnitFormulaArgs> handler);

    /// <summary>The silver a killed mob drops (a pile's amount); id = the mob.</summary>
    IHookRegistration OnCalcSilver(Action<UnitFormulaArgs> handler);
    IHookRegistration OnCalcSilver(string mob, Action<UnitFormulaArgs> handler);

    /// <summary>An item's item power as worn by a player (damage, ability power and stats scale with it); id = the item.</summary>
    IHookRegistration OnCalcItemPower(Action<ItemFormulaArgs> handler);
    IHookRegistration OnCalcItemPower(string item, Action<ItemFormulaArgs> handler);
}

public sealed class SpellFormulaArgs(IUnit caster, string spell, IUnit? target, double value)
{
    public IUnit Caster { get; } = caster;
    public string Spell { get; } = spell;
    public IUnit? Target { get; } = target;
    public Mutable<double> Value { get; } = new(value);
}

public sealed class UnitFormulaArgs(IPlayer player, IUnit unit, double value)
{
    /// <summary>The player concerned (the attacker, the fame receiver, the killer).</summary>
    public IPlayer Player { get; } = player;

    /// <summary>The mob concerned.</summary>
    public IUnit Unit { get; } = unit;

    public Mutable<double> Value { get; } = new(value);
}

public sealed class ItemFormulaArgs(IPlayer player, string item, int quality, double value)
{
    public IPlayer Player { get; } = player;
    public string Item { get; } = item;
    public int Quality { get; } = quality;
    public Mutable<double> Value { get; } = new(value);
}
