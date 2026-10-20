using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// An item object (ARCHITECTURE.md §4.1): not a world object, it lies in a <see cref="Container"/> and only its owner
/// gets it, as NewEquipmentItem (event 30), NewSimpleItem (32) or NewJournalItem (35). The object id comes from <see cref="ObjectIds"/>, new
/// for every Join, never saved. Quality is NewEquipmentItem key 7 (1 = normal), the durability key 8 (a7m raw, 10000 =
/// 1 items.xml point; saved, no wear yet), a journal's fame (<see cref="JournalFame"/>), the chosen spell ids per active/passive slot keys 9/10 (-1 = none; lengths =
/// activespellslots/passivespellslots, empty without spell data).
/// </summary>
public sealed class Item(long id, ItemTemplate template, int count, int quality, long durabilityRaw, short[] active, short[] passive,
    InventoryItem? source = null)
{
    public long Id { get; } = id;

    public ItemTemplate Template { get; } = template;

    /// <summary>Stack size (NewSimpleItem key 2); 1 for equipment.</summary>
    public int Count { get; set; } = count;

    /// <summary>Quality 1-5 (NewEquipmentItem key 7); set by a quality reroll (station ACT 6), which re-sends the object.</summary>
    public int Quality { get; set; } = quality;

    public long DurabilityRaw { get; set; } = durabilityRaw;

    /// <summary>Spell id per active slot, -1 = none (EquipmentItemChangeSpell sets them).</summary>
    public short[] Active { get; } = active;

    /// <summary>Spell id per passive slot, -1 = none.</summary>
    public short[] Passive { get; } = passive;

    /// <summary>
    /// A potion's or food's slot spell set by a plugin (<see cref="Systems.SlotSpells"/>): CharacterSpellSlot 12/13 casts it instead of the
    /// consumespell while this stack lies in the slot. Not saved.
    /// </summary>
    public short? ConsumeOverride { get; set; }

    /// <summary>
    /// The temporary slot spells in force (<see cref="Systems.SlotSpells"/>), by (active, index; index -1 = <see cref="ConsumeOverride"/>): the
    /// choice to save meanwhile (-1 = none) and the token of the revert timer. <see cref="ToRecord"/> writes the saved choice.
    /// </summary>
    public Dictionary<(bool Active, int Index), (short Saved, int Token)>? Temporary { get; set; }

    /// <summary>
    /// A journal's fame (NewJournalItem key 8, a7m raw; stageH journals SPEC §A.1): 0 for an _EMPTY one that has not filled yet, the
    /// maxfame for a _FULL one; 0 for every other item.
    /// </summary>
    public long JournalFame { get; set; }

    /// <summary>
    /// An awakened weapon's legendary soul (stageN awakening SPEC): NewEquipmentItem key 11 = 1 and event 37 after it; null = a plain item.
    /// Replaced as a whole on every change (immutable).
    /// </summary>
    public ItemSoul? Soul { get; set; }

    /// <summary>A journal between empty and full (0 &lt; fame &lt; maxfame): the client does not stack it (bdm.lk 0x22), neither does the server.</summary>
    public bool PartlyFilled => Template.Journal is { } j && JournalFame > 0 && JournalFame < j.MaxFame;

    /// <summary>Whether <paramref name="other"/> may go onto this stack: the same template and, for journals, the same fame and not partly filled (bdm.lk 0x21/0x22).</summary>
    public bool StacksWith(Item other) => other.Template == Template && other.JournalFame == JournalFame && !PartlyFilled;

    /// <summary>The fame a new journal of <paramref name="type"/> starts with: maxfame for a _FULL one, else 0 (bdm.a(bdm) 0x18060e620).</summary>
    public static long DefaultFame(ItemTemplate type) => type.Journal is { State: JournalState.Full } j ? j.MaxFame : 0;

    /// <summary>
    /// The saved entry the item was made from (a new item has a record of its own, without spells): spell names the
    /// current spell table cannot resolve are written back from it (<see cref="ToRecord"/>).
    /// </summary>
    public InventoryItem? Source { get; } = source;

    /// <summary>The item as the stat calculator takes it.</summary>
    public EquippedItem ForStats() => new(Template.Index, Quality, DurabilityRaw);

    /// <summary>
    /// The saved form (spells by uniquename via <paramref name="spellName"/>, durability null when full). An item without
    /// slot info (no spell data, or not in it) writes its loaded names back unchanged; a slot at -1 keeps the loaded name,
    /// which is one the spell table does not know (a choice never sets -1). Like unloaded items, saved choices outlive a
    /// spell table that cannot resolve them.
    /// </summary>
    public InventoryItem ToRecord(Func<int, string?> spellName)
    {
        static string?[]? Names(short[] ids, string?[]? loaded, Func<int, string?> spellName) => ids.Length == 0
            ? loaded
            : ids.Select((id, k) => id >= 0 ? spellName(id) : loaded is { } l && k < l.Length ? l[k] : null).ToArray();
        return new InventoryItem(Template.Name, Count)
        {
            Quality = Quality,
            Durability = DurabilityRaw == Template.FullDurabilityRaw ? null : DurabilityRaw,
            Active = Names(Saved(Active, true), Source?.Active, spellName),
            Passive = Names(Saved(Passive, false), Source?.Passive, spellName),
            Fame = Template.IsJournal && JournalFame != DefaultFame(Template) ? JournalFame : null,
            Soul = Soul,
        };
    }

    /// <summary>The choices to save: <paramref name="ids"/> with each temporary slot spell's saved choice in its place.</summary>
    private short[] Saved(short[] ids, bool active)
    {
        if (Temporary is not { Count: > 0 } temporary) return ids;
        var saved = ids.ToArray();
        foreach (var ((a, i), (s, _)) in temporary)
            if (a == active && i >= 0 && i < saved.Length) saved[i] = s;
        return saved;
    }

    public override string ToString() => $"{Template.Name} (object {Id})";
}
