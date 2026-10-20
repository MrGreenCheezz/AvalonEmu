using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Items used from the inventory (stageD consumables SPEC §3; the rules in <see cref="Consumables"/>): UseConsumableFromInventory
/// and BatchUseItemCancel, and the equipment event of a potion or food stack used up in its slot (§2.1).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// UseConsumableFromInventory (op 374, acc): 0 the item object, 1 count (1 = "use", the stack = "use all"). The checks of SPEC
    /// §3.1 in order: the player is in the world and standing; the item lies in its bag, is a consumablefrominventoryitem with a
    /// consumespell of spells.xml; no batch runs; n = clamp(count, 1, stack); every effect of the spell runs
    /// (<see cref="Consumables.Supported"/>). A failed check is answered at once with <see cref="Consumables.GenericFailed"/> and
    /// no event; an accepted use gets its empty rc-0 response after its last use (<see cref="Consumables.StartBatch"/>).
    /// </summary>
    [Handles(OperationCodes.UseConsumableFromInventory, Status = SessionStatus.InWorld)]
    private AlbionResponse? UseConsumableFromInventory(UseConsumableFromInventoryRequest req)
    {
        AlbionResponse Refused(string why)
        {
            Log.Info(Name, $"UseConsumableFromInventory of object {req.ah5} x{req.ah6} refused ({Consumables.GenericFailed}): {why}");
            return new UseConsumableFromInventoryResponse { ReturnCode = Consumables.GenericFailed };
        }
        if (Player is not { Map: not null } player || player.Combat.Down is not null) return Refused("not standing in the world");
        if (player.Inventory.Bag.Find(req.ah5) is not { } item) return Refused($"no item object {req.ah5} in the bag");
        if (item.Template.Kind != "consumablefrominventoryitem") return Refused($"{item.Template.Name} is a {item.Template.Kind}");
        if (world.Spells?.Template(world.Spells.ConsumeSpell(item.Template.Name)) is not { } spell) return Refused($"{item.Template.Name} has no consumespell in spells.xml");
        if (player.Combat.Batch is not null) return Refused("a batch use is under way");
        // Stage I (invasions SPEC §2.2): an invasion ticket (content/events.json Player.Items) starts an invasion at its tier, one at a
        // time; refused with a chat line why and nothing used (its dungeon-map spell would be refused with 14965 below).
        if (world.EventSettings.ItemFor(item.Template) is { } ticket)
        {
            var tier = ticket.Tier > 0 ? ticket.Tier : world.Invasions.Chosen(player);
            var cost = world.Invasions.Cost(ticket, tier);
            var why = world.EventSettings.Find(ticket.Kind) is null ? $"no event kind {ticket.Kind}"
                : Consumables.Tickets(player, item) < cost ? $"a T{tier} invasion needs {cost} of these" : world.Invasions.CanStart(player, tier, item: true);
            if (why is not null)
            {
                WorldEvents.Tell(player, "Invasion: " + why);
                return Refused(why);
            }
            Consumables.StartBatch(player, item, spell, 1, RequestId, RequestEncrypted, ticket);
            return null;
        }
        if (!Consumables.Supported(spell, world.Spells)) return Refused($"{spell.Name} has an effect that does not run yet");
        // StageG: a dungeon map needs a free exit marker of its exit type here (stageG dungeons SPEC §10), else 14965.
        if (!world.Dungeons.MapCanPlace(player, spell))
        {
            Log.Info(Name, $"UseConsumableFromInventory of {item.Template.Name} refused ({Consumables.NoDungeonFound}): no free dungeon exit marker in {player.Map.Cluster}");
            return new UseConsumableFromInventoryResponse { ReturnCode = Consumables.NoDungeonFound };
        }
        // A dungeon map is used one at a time, also on "use all" (SPEC §10: one exit per use) [assume].
        var count = RandomDungeons.IsMap(spell) ? 1 : Math.Clamp((int)req.ah6, 1, item.Count);
        Consumables.StartBatch(player, item, spell, count, RequestId, RequestEncrypted);
        return null;
    }

    /// <summary>BatchUseItemCancel (op 432, no keys, no response class): the batch under way ends with what is left (SPEC §3.2 step 5; not captured).</summary>
    [Handles(OperationCodes.BatchUseItemCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? BatchUseItemCancel(BatchUseItemCancelRequest req)
    {
        if (Player is { } player) Consumables.Abort(player, "cancelled");
        return null;
    }

    /// <summary>The empty rc-0 response of an accepted UseConsumableFromInventory, sent after its last use (SPEC §3.2 step 4).</summary>
    internal void AnswerConsumableUse(int? requestId, bool encrypted) =>
        SendResponse(OperationCodes.UseConsumableFromInventory, new UseConsumableFromInventoryResponse(), requestId, encrypted);

    /// <summary>
    /// CharacterEquipmentChanged after a potion or food stack was used up in its slot (SPEC §2.1 step 2): {0 me, 1 now, 2 the type
    /// ids with [8]/[9] = 0, 6 0, 7 the spell slots with [12]/[13] = -1}, no keys 8 and 9, to the player and its observers, and no
    /// RegenerationPlayerComboChanged (consumables give no bars).
    /// </summary>
    internal void SendConsumedEquipment(Player player)
    {
        world.ExtraBars.Refresh(player, Plugin.Api.ExtraBarChangeSource.Equipment); // a used-up potion or food leaves the extra bars too
        SendAround(player, new CharacterEquipmentChangedEvent
        {
            ddn = player.Id,                               // [0]
            bw3 = GameTime.Now,                            // [1]
            bw4 = player.Inventory.EquipmentTypeIds(),     // [2]
            bw9 = SpellSlots(player.Inventory),            // [7]
            Extra = new() { [6] = (byte)0 },               // [6] 0, as captured
        });
    }
}
