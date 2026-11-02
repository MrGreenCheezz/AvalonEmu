using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    /// <summary>The same saved item/spell resolution as GiveItem, with no events or hooks until a trade commits.</summary>
    internal Item PrepareTradeItem(ItemTemplate type, InventoryItem saved, Player recipient) =>
        MakeItem(type, saved, SpellUsable(recipient.Progression));
}
