using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>StageR victory emotes (analysis/work/stageR/kill-vanity/SPEC.md §1, §3); the rules in <see cref="KillEmotes"/>.</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// ReclaimVanityItem's refusals (VanityPreviewItemEntry.GetErrorMessageLoca 0x1815FF890): 0x4998 @RECLAIM_VANITY_ITEM_NOT_RECLAIMABLE_ERROR,
    /// 0x4999 …_NOT_UNLOCKED_ERROR (it also knows -500 internal error and 0x499A inventory full).
    /// </summary>
    public const short VanityNotReclaimable = 0x4998, VanityNotUnlocked = 0x4999;

    /// <summary>GetVanityConsumables (op 467, acn no fields; ajg {0 short[] ids, 1 int[] charges}, cjk.c.c → bh(bcm, ajg)): the victory emotes with charges.</summary>
    [Handles(OperationCodes.GetVanityConsumables)]
    private AlbionResponse? GetVanityConsumables(GetVanityConsumablesRequest req)
    {
        if (Character is not { } c) return new GetVanityConsumablesResponse().FillEmpty();
        var (ids, charges) = world.Vanity.KillEmotes.List(c);
        return new GetVanityConsumablesResponse { bni = ids, bnj = charges };
    }

    /// <summary>
    /// EquipKillEmote (op 468, acm {0 short id}; ajf {0 short}): RC 0 with the id, which the client then equips (dq8.d 0x181657BC0 → cvr.d(int));
    /// 0 is none (dq8.aqp). An unknown id: RC <see cref="KillEmotes.Refused"/>.
    /// </summary>
    [Handles(OperationCodes.EquipKillEmote, Status = SessionStatus.InWorld)]
    private AlbionResponse? EquipKillEmote(EquipKillEmoteRequest req)
    {
        if (Player is not { } p || world.Vanity.KillEmotes.Equip(p, req.aiq) is not { } id)
            return new EquipKillEmoteResponse { ReturnCode = KillEmotes.Refused, bnh = 0 };
        return new EquipKillEmoteResponse { bnh = id };
    }

    /// <summary>ChangeKillEmotePlayOnKnockdownSetting (op 469, acl {0 bool}; no response class): kept (Join key 120); RC 0.</summary>
    [Handles(OperationCodes.ChangeKillEmotePlayOnKnockdownSetting, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeKillEmotePlayOnKnockdownSetting(ChangeKillEmotePlayOnKnockdownSettingRequest req)
    {
        if (Player is { } p) world.Vanity.KillEmotes.SetOnKnockdown(p, req.aip);
        return new GenericResponse();
    }

    /// <summary>
    /// BuyVanityConsumableCharges (op 470, ack {0 type, 1 ids, 2 bundles, 3 a7m gold price}; no response class): charges for gold as the client
    /// prices them (<see cref="KillEmotes.Buy"/>); RC 0, or <see cref="KillEmotes.Refused"/> with nothing changed.
    /// </summary>
    [Handles(OperationCodes.BuyVanityConsumableCharges, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuyVanityConsumableCharges(BuyVanityConsumableChargesRequest req)
    {
        if (Player is not { } p) return new GenericResponse { ReturnCode = KillEmotes.Refused };
        if (world.Vanity.KillEmotes.Buy(p, req.ail, req.aim, req.ain, req.aio) is { } why)
        {
            Log.Info(Name, $"'{p.Character.Name}' victory emote charges refused: {why}");
            return new GenericResponse { ReturnCode = KillEmotes.Refused };
        }
        return new GenericResponse();
    }

    /// <summary>
    /// ReclaimVanityItem (op 471, aco {0 short vanity unlock id}; no response class; VanityPreviewItemEntry.OnReclaimButtonClicked): the
    /// "reclaimable skin" (@ITEMDETAILS_RECLAIMABLE_VANITY_DESC: re-lock it and get the tradable item back), which only an unlock made by using
    /// a reclaimable unlock item is (@UNLOCK_ITEM_UNLOCK_REQUEST_RECLAIMABLE). Here the unlocks drop and those items unlock nothing
    /// (unlockvanityunlock is a no-op), so nothing is reclaimable: "not unlocked" for an unlock the account lacks, else "not reclaimable".
    /// </summary>
    [Handles(OperationCodes.ReclaimVanityItem, Status = SessionStatus.InWorld)]
    private AlbionResponse? ReclaimVanityItem(ReclaimVanityItemRequest req)
    {
        var owned = Character is { } c && world.Vanity.Owned(c).Contains(req.air);
        Log.Info(Name, $"ReclaimVanityItem {req.air}: {(owned ? "not reclaimable (no unlock items here)" : "not unlocked")}");
        return new GenericResponse { ReturnCode = owned ? VanityNotReclaimable : VanityNotUnlocked };
    }
}
