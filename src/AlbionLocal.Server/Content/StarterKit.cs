using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Content;

/// <summary>
/// Where a new character starts and what it carries (TC playercreateinfo): content/starter.json, ARCHITECTURE.md §5.2.
/// A key the file leaves out keeps the built-in value (<see cref="Default"/>, what the server used before the file
/// existed); a missing file gives the built-in kit with a warning. Bad content does not stop the server
/// (principle 6): a file that does not parse is logged and the built-in kit used, an unknown slot or key is
/// logged and skipped, and so is an item the item table does not know (with the closest known name) or an
/// equipment item in a slot its type does not go into.
/// </summary>
public sealed record StarterKit
{
    /// <summary>T4 plate set with sword, bag and cape, 10 ore and 10 wood, at (0, 0) in Thetford.</summary>
    public static StarterKit Default { get; } = new()
    {
        Equipment =
        [
            new("T4_MAIN_SWORD"), null, new("T4_HEAD_PLATE_SET1"), new("T4_ARMOR_PLATE_SET1"), new("T4_SHOES_PLATE_SET1"),
            new("T4_BAG"), new("T4_CAPE"), null, null, null,
        ],
        Inventory = [new("T4_ORE", 10), new("T4_WOOD", 10)],
    };

    /// <summary>
    /// The built-in kit of a fair start (Progression:NewCharacters = FairStart, content/starter-fair.json): what an empty
    /// destiny board lets a character equip, the items.xml unlockedtoequip T1 items — T1 sword and shield, the T1 leather
    /// set, the T2 mule a tutorial would have given — and the T1 gathering tools (START unlocks the T2 bag and cape, COMBAT_TRAINEE the T2 weapons and armour).
    /// </summary>
    public static StarterKit FairDefault { get; } = new()
    {
        Equipment =
        [
            new("T1_MAIN_SWORD"), new("T1_OFF_SHIELD"), new("T1_HEAD_LEATHER_SET1"), new("T1_ARMOR_LEATHER_SET1"), new("T1_SHOES_LEATHER_SET1"),
            null, null, new("T2_MOUNT_MULE"), null, null,
        ],
        Inventory = [new("T1_2H_TOOL_AXE"), new("T1_2H_TOOL_PICK"), new("T1_2H_TOOL_SICKLE"), new("T1_2H_TOOL_HAMMER"), new("T1_2H_TOOL_KNIFE")],
    };

    /// <summary>Cluster id from GameData/cluster/world.xml; 0000 = Thetford.</summary>
    public string Cluster { get; init; } = "0000";

    public float PositionX { get; init; }
    public float PositionY { get; init; }

    /// <summary>Items by EquipmentSlot (<see cref="Entities.Inventory.SlotNames"/> order); null = empty.</summary>
    public IReadOnlyList<InventoryItem?> Equipment { get; init; } = new InventoryItem?[Entities.Inventory.EquipmentSlots];

    /// <summary>Inventory slots in order; null = empty.</summary>
    public IReadOnlyList<InventoryItem?> Inventory { get; init; } = [];

    /// <summary>Whole silver a new character starts with ("Silver"; stage 6); 0 built in.</summary>
    public long Silver { get; init; }

    /// <summary>A new character with this start (new id and containers); the caller sets name and appearance.</summary>
    public Character NewCharacter(string name) => new()
    {
        Name = name,
        Cluster = Cluster,
        PositionX = PositionX,
        PositionY = PositionY,
        Equipment = [.. Equipment.Select(i => i?.Copy())],
        Inventory = [.. Inventory.Select(i => i?.Copy())],
        Silver = Silver * Game.Pricing.A7m,
    };

    /// <param name="items">The item table the kit's items are checked against; null or empty (a hand-made test table) = not checked.</param>
    /// <param name="fallback">The built-in kit a missing or broken file falls back to (<see cref="Default"/>; a fair start's <see cref="FairDefault"/>).</param>
    public static StarterKit Load(string path, ItemStore? items = null, StarterKit? fallback = null)
    {
        var builtIn = fallback ?? Default;
        if (!File.Exists(path))
        {
            Log.Warn("content", $"{path} missing: built-in starter kit ({(fallback is null ? "T4 plate set" : "T1 fair start")} in {builtIn.Cluster})");
            return builtIn;
        }
        try
        {
            var kit = Parse(File.ReadAllText(path), path, builtIn);
            if (items is { Count: > 0 }) kit = kit.Checked(items, path);
            Log.Info("content", $"starter kit: cluster {kit.Cluster}, {kit.Equipment.Count(i => i is not null)} equipped, {kit.Inventory.Count(i => i is not null)} in the inventory");
            return kit;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            Log.Error("content", $"{path}: {e.Message}; built-in starter kit used");
            return builtIn;
        }
    }

    /// <summary>
    /// The file: { "Cluster": "0000", "Position": [x, y], "Equipment": { "&lt;slot&gt;": item, … }, "Inventory": [item or null, …],
    /// "Silver": whole silver }.
    /// An item is a name or an object as in the saves ({ "Name", "Count", "Quality", "Durability", "Active", "Passive" }).
    /// </summary>
    private static StarterKit Parse(string text, string path, StarterKit builtIn)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("expected an object");
        InventoryItem? Item(JsonElement e, string where)
        {
            if (e.ValueKind == JsonValueKind.Null) return null;
            if (e.Deserialize<InventoryItem>() is { Name.Length: > 0 } item) return item;
            Log.Warn("content", $"{path}: {where} has no item name, skipped");
            return null;
        }
        var kit = builtIn;
        foreach (var key in doc.RootElement.EnumerateObject())
        {
            switch (key.Name)
            {
                case "Cluster":
                    kit = kit with { Cluster = key.Value.GetString() ?? throw new InvalidDataException("Cluster is null") };
                    break;
                case "Position" when key.Value is { ValueKind: JsonValueKind.Array } p && p.GetArrayLength() == 2:
                    kit = kit with { PositionX = p[0].GetSingle(), PositionY = p[1].GetSingle() };
                    break;
                case "Position":
                    throw new InvalidDataException("Position is not [x, y]");
                case "Equipment":
                    var equipment = new InventoryItem?[Entities.Inventory.EquipmentSlots];
                    foreach (var slot in key.Value.EnumerateObject())
                    {
                        var index = Array.FindIndex(Entities.Inventory.SlotNames, n => n.Equals(slot.Name, StringComparison.OrdinalIgnoreCase));
                        if (index < 0)
                            Log.Warn("content", $"{path}: unknown equipment slot '{slot.Name}' (slots: {string.Join(", ", Entities.Inventory.SlotNames)}), skipped");
                        else
                            equipment[index] = Item(slot.Value, $"slot {slot.Name}");
                    }
                    kit = kit with { Equipment = equipment };
                    break;
                case "Inventory":
                    kit = kit with { Inventory = [.. key.Value.EnumerateArray().Select((e, i) => Item(e, $"inventory #{i}"))] };
                    break;
                case "Silver" when key.Value.TryGetInt64(out var silver) && silver is >= 0 and <= 100_000_000_000:
                    kit = kit with { Silver = silver };
                    break;
                case "Silver":
                    throw new InvalidDataException("Silver is not a whole number 0..100000000000");
                default:
                    Log.Warn("content", $"{path}: unknown key '{key.Name}', skipped");
                    break;
            }
        }
        return kit;
    }

    /// <summary>
    /// The kit without the items <paramref name="items"/> does not know (§5.2: skipped, the closest known name logged)
    /// and without equipment whose type goes into another slot (it would be sent equipped there as it is).
    /// </summary>
    private StarterKit Checked(ItemStore items, string path)
    {
        InventoryItem? Known(InventoryItem? item, string where)
        {
            if (item is null || items.Find(item.Name) is not null) return item;
            Log.Warn("content", $"{path}: unknown item {item.Name} in {where} (closest: {items.Closest(item.Name)}), skipped");
            return null;
        }
        var equipment = new InventoryItem?[Entities.Inventory.EquipmentSlots];
        for (var slot = 0; slot < equipment.Length && slot < Equipment.Count; slot++)
        {
            if (Known(Equipment[slot], $"slot {Entities.Inventory.SlotNames[slot]}") is not { } item) continue;
            var goes = items.Find(item.Name)!.Slot;
            if (goes != Entities.Inventory.SlotNames[slot])
                Log.Warn("content", $"{path}: {item.Name} goes into {(goes.Length > 0 ? $"slot {goes}" : "no equipment slot")}, "
                    + $"not {Entities.Inventory.SlotNames[slot]}, skipped");
            else
                equipment[slot] = item;
        }
        return this with { Equipment = equipment, Inventory = [.. Inventory.Select((item, i) => Known(item, $"inventory #{i}"))] };
    }
}
