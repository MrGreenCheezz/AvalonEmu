namespace AlbionLocal.Server.Game;

/// <summary>One trait of an awakened weapon: the traitpool uniquename and its roll 0-1 (NewEquipmentItemLegendarySoul keys 8/9).</summary>
public sealed record SoulTrait(string Name, double Roll);

/// <summary>
/// The legendary soul of an awakened weapon (stageN awakening SPEC §2): what event 37 NewEquipmentItemLegendarySoul carries and the
/// saved form inside <see cref="InventoryItem.Soul"/> (the item JSON, no migration). Immutable: every change makes a new one, so a
/// record and an item object never share a changing soul. Attunement and Strain are a7m raw (10000 = 1).
/// </summary>
public sealed record ItemSoul
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Key 2 Name: the awakener's character name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Key 3 Era.</summary>
    public int Era { get; init; } = 1;

    /// <summary>The character the soul is attuned to (key 4 AttunedToMe for that viewer, key 5 the name); traits work only for it.</summary>
    public Guid AttunedId { get; init; }

    public string AttunedName { get; init; } = "";

    /// <summary>Key 7 Attunement (a7m raw): gained from fame with the weapon in hand, spent on trait rerolls and generations.</summary>
    public long Attunement { get; init; }

    /// <summary>Key 6 Strain (a7m raw, 10000 = 1): the multiplier of the trait costs and of the attunement cap (constant 1 here, SPEC §5).</summary>
    public long Strain { get; init; } = 10000;

    /// <summary>The attunement spent so far (LegendarySoulSnapshot.AttunementSpent; not sent).</summary>
    public long Spent { get; init; }

    /// <summary>The traits by slot (keys 8/9).</summary>
    public SoulTrait[] Traits { get; init; } = [];

    /// <summary>The slot with offered replacements (a trait generation), −1 = none.</summary>
    public int PendingSlot { get; init; } = -1;

    /// <summary>The offered replacements of <see cref="PendingSlot"/> (keys 10/11 at that slot).</summary>
    public SoulTrait[] Pending { get; init; } = [];

    public bool AttunedTo(Guid character) => AttunedId == character;
}
