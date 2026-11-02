using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    /// <summary>Native bank Move All sends op37 (target/revision/source/revision), not op39's item list.
    /// 345319 A75F20–A75F44 writes the destination to keys0/1 and the source to keys2/3.
    /// Resolve the items from the server's containers and reuse the existing vault transfer/weight/stack/save path.
    /// Both directions are restricted to the character's bag and an owned tab at the bank currently in reach.</summary>
    [Handles(OperationCodes.InventoryDropAll, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventoryDropAll(InventoryDropAllRequest req)
    {
        if (Player is not { Map: { } map } player) return null;
        var from = player.FindContainer(req.rm);
        var to = player.FindContainer(req.rk);
        AlbionResponse? Refuse(string why)
        {
            // The native UI predicts this move. Restore actual containers even when nothing can move.
            if (from is not null) SendEvent(new InventoryStateEvent { ck0 = from.ObjectIds(), ck2 = from.Id, ck3 = req.rn });
            if (to is not null && to != from) SendEvent(new InventoryStateEvent { ck0 = to.ObjectIds(), ck2 = to.Id, ck3 = req.rl });
            Log.Info(Name, $"'{player.Character.Name}' bank Move All refused: {why}");
            return null;
        }
        if (from is null || to is null || from == to) return Refuse("invalid containers");
        var bag = player.Inventory.Bag;
        var vaultId = from == bag ? to.Id : to == bag ? from.Id : Guid.Empty;
        if (!player.OpenVaults.TryGetValue(vaultId, out var vault)) return Refuse("only the inventory and an open bank tab are supported");
        if (player.RegisteredTo != vault.Building || !map.Objects.TryGetValue(vault.Building, out var obj)
            || obj is not Building { Template.Kind: "bankbuilding" } bank || BankUnusable(player, bank) is not null
            || !PersonalBankTabs(player).Any(t => t.Id == vault.Tab))
            return Refuse("the bank is unavailable or the tab is not owned here");
        return MoveGivenVaultItems(player, new InventoryMoveGivenItemsRequest
        {
            ro = from.Id, rp = req.rn, rq = to.Id, rr = req.rl,
            rs = from.Items.Select(item => item.Id).ToArray(),
        });
    }
}
