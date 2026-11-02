namespace AlbionLocal.Server.Game;

/// <summary>
/// One saved loadout of a character or guild (the client's ctg), stored as the client sends it in
/// LoadoutUpdate (op 428, xl) and read back unchanged in LoadoutRead (op 426, ahu). The per-slot arrays are in EquipmentSlot order (10);
/// <see cref="SlotStacks"/> and <see cref="PotionFoodPreference"/> are for the potion and food slots (8, 9) only. Immutable: an update
/// replaces the record, so <see cref="Character.Clone"/> can share it. Saved as JSON in characters.loadouts or guilds.loadouts.
/// </summary>
public sealed record Loadout
{
    /// <summary>The loadout's id (LoadoutCreate answer key 0; the key of every later op).</summary>
    public required string Id { get; init; }

    /// <summary>The owner Guid the client asked with: the character or guild id.</summary>
    public Guid Owner { get; init; }

    public string Name { get; init; } = "";

    /// <summary>The colour and icon indices the client picks (ahu keys 3, 4; headers keys 5, 6).</summary>
    public int Color { get; init; }

    public int Icon { get; init; }

    /// <summary>Raised by every update (ahv key 1, ahu key 5); the client sends the one it read (xl key 2).</summary>
    public int Version { get; init; }

    /// <summary>The order in the list (headers key 3; LoadoutOrderUpdate).</summary>
    public int Position { get; init; }

    /// <summary>xl 6 / ahu 6: the item uniquename per equipment slot (with @N; "" = empty, as the client sends it).</summary>
    public string[] Equipment { get; init; } = [];

    /// <summary>xl 7 / ahu 7: the item quality per equipment slot.</summary>
    public int[] Quality { get; init; } = [];

    /// <summary>xl 8 / ahu 8: the stack size of the potion and food slots.</summary>
    public int[] SlotStacks { get; init; } = [];

    /// <summary>xl 9, 10 / ahu 9, 10: per equipment slot, a string of candidate indices for its spell slots; '-' means unspecified (ctj.i/ctj.j).</summary>
    public string[] ActiveSpells { get; init; } = [];

    public string[] PassiveSpells { get; init; } = [];

    /// <summary>xl 11 / ahu 11: tier preference per slot (0 EqualOrWorse, 1 Equal, 2 EqualOrBetter).</summary>
    public int[] PreferenceA { get; init; } = [];

    /// <summary>xl 12 / ahu 12: enchantment preference per slot.</summary>
    public int[] PreferenceB { get; init; } = [];

    /// <summary>xl 13 / ahu 13: quality preference per slot.</summary>
    public int[] PreferenceC { get; init; } = [];

    /// <summary>xl 14 / ahu 14: the preference of the potion and food slots.</summary>
    public int[] PotionFoodPreference { get; init; } = [];

    /// <summary>xl 15-17 / ahu 15-17: the inventory part: uniquename, quality and stack size per entry.</summary>
    public string[] Items { get; init; } = [];

    public int[] ItemQuality { get; init; } = [];

    public int[] ItemStacks { get; init; } = [];
}

/// <summary>One guild's shared builds and ordering revision (op427 key1 / op430 key3), saved as one JSON value.</summary>
public sealed record GuildLoadouts
{
    public int OrderVersion { get; init; }
    public List<Loadout> Entries { get; init; } = [];
}
