using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Furniture in use (analysis/work/stageR/buildings/SPEC.md §4): a placed chest (a furnitureitem with &lt;container capacity weightlimit&gt;, as
/// T2_FURNITUREITEM_CHEST) opens like a bank vault and keeps its items with the island owner's vaults (bank_items, so they survive a restart);
/// it cannot be picked up or torn down while it holds anything. FurnitureObjectUse (72) is refused: no furniture of this build's items.xml
/// has an effect to use (beds and tables only raise the labourers' happiness, which they already do by standing in the house).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The key of a chest's items in its owner's <see cref="Character.Vaults"/>: "chest:&lt;row&gt;|&lt;empty Guid&gt;".</summary>
    public static string ChestKey(Guid row) => Character.VaultKey(ChestPrefix + row.ToString("N"), Guid.Empty);

    /// <summary>The location prefix of the chest keys (the Asset Overview leaves them out: they are no bank).</summary>
    public const string ChestPrefix = "chest:";

    /// <summary>How many items the chest <paramref name="row"/> of <paramref name="owner"/> holds (its open container, else its saved slots).</summary>
    public static int ChestItems(Player owner, Guid row)
    {
        var key = ChestKey(row);
        if (owner.OpenVaults.Values.FirstOrDefault(v => v.Key == key) is { } open) return open.Container.Count;
        return owner.Character.Vaults.GetValueOrDefault(key)?.Count(i => i is not null) ?? 0;
    }

    /// <summary>
    /// The chest part of RegisterToObject (op 45) on a placed chest: the client made the chest's container itself when the chest came into view
    /// (co7 ctor 0x180BAE050: a ck9 with the building's Guid, NewBuilding key 1, the item's capacity) and opens it on the register (co7.a6 →
    /// ctp.abq). So the server sends the stored items and AttachItemContainer (99) {0 the chest, 1 the container Guid = the chest's Guid, 2 the
    /// empty tab, 3 the object id per slot, 5 the slots, 6 false}, as for a bank vault [infer: the chest's key 2; not captured]. Moving items in
    /// and out is the ordinary InventoryMoveItem with that Guid (<see cref="Player.FindContainer"/>), up to the item's weightlimit; the items are
    /// saved with the owner when the chest closes (UnRegisterFromObject) or the owner leaves. Only the island's owner opens it (only the owner
    /// visits an island); out of range or knocked down: nothing.
    /// </summary>
    private void PushChest(Player player, long objectId)
    {
        if (player.Map?.Objects.GetValueOrDefault(objectId) is not FurnitureObject { Item.Container: { } box } chest) return;
        if (chest.Island.Owner != player.Character.Id)
        {
            Log.Info(Name, $"'{player.Character.Name}' may not open the chest {chest.Id}: not the island's owner");
            return;
        }
        if (player.Combat.Down is not null || Vector2.Distance(player.PositionAt(DateTime.UtcNow), chest.Position) > StationRange)
        {
            Log.Info(Name, $"'{player.Character.Name}' may not open the chest {chest.Id}: knocked down or out of range");
            return;
        }
        var key = ChestKey(chest.Row.Id);
        var id = chest.Row.Id;
        if (!player.OpenVaults.TryGetValue(id, out var vault))
        {
            var container = new Container(id, Math.Max(1, box.Capacity));
            var saved = player.Character.Vaults.GetValueOrDefault(key) ?? [];
            var usable = SpellUsable(player.Progression);
            for (var slot = 0; slot < saved.Count; slot++)
            {
                if (saved[slot] is not { } entry) continue;
                if (slot < container.Size && world.Items.Find(entry.Name) is { CanCreate: true } type) container.Put(slot, MakeItem(type, entry, usable));
                else if (slot < container.Size) container.Unloaded[slot] = entry;
                else Log.Warn(Name, $"chest {key} of '{player.Character.Name}': slot {slot} beyond {container.Size}, kept unsent");
            }
            vault = new Vault(key, container, chest.Id, Guid.Empty, box.WeightLimit);
            player.OpenVaults[id] = vault;
        }
        vault.Views.Add(0);
        foreach (var item in vault.Container.Items) SendItem(item);
        SendEvent(new AttachItemContainerEvent
        {
            bst = chest.Id, bsu = id, bsv = Guid.Empty, bsw = vault.Container.ObjectIds(), bsy = vault.Container.Size,
            Extra = new() { [6] = false },
        });
        Log.Info(Name, $"'{player.Character.Name}' opened the {chest.Item.Name} {chest.Id} ({vault.Container.Count} item(s))");
    }

    /// <summary>
    /// FurnitureObjectUse (op 72, s8 {0 the furniture}; answer ae0 {0, 1, 2 longs}): the use of a buff-providing furniture (the client's co5,
    /// AccessRightsFurnitureBuffProvider, FurnitureObjectBuffProviderInfo 198) [infer: the only furniture class with a "use"]. This build's
    /// items.xml has no such furniture (no &lt;buffprovider&gt; under any furnitureitem), so there is nothing to use: RC 1 with a line.
    /// </summary>
    [Handles(OperationCodes.FurnitureObjectUse, Status = SessionStatus.InWorld)]
    private AlbionResponse? FurnitureObjectUse(FurnitureObjectUseRequest req)
    {
        if (Player is not { } p) return null;
        var what = p.Map?.Objects.GetValueOrDefault(req.l2) is FurnitureObject f ? f.Item.Name : $"object {req.l2}";
        return BuildingRefusal(p, "furniture use", $"{what} has no use");
    }
}
