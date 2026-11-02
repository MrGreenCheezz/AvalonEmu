using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>StageN vanity (analysis/work/stageN/vanity/SPEC.md §1): the vanity store, the wardrobe and the mount skins; the rules in <see cref="Vanity"/>.</summary>
public sealed partial class WorldSession
{
    /// <summary>GetVanityUnlocks (op 383, u6 no fields; ags {0 short[]}): the account's unlock ids (cjk.b: the client's owned set).</summary>
    [Handles(OperationCodes.GetVanityUnlocks)]
    private AlbionResponse? GetVanityUnlocks(GetVanityUnlocksRequest req) =>
        new GetVanityUnlocksResponse { a49 = Character is { } c ? world.Vanity.UnlockIds(c) : [] };

    /// <summary>
    /// BuyVanityUnlocks (op 384, p3 {0 short type, 1 short[] ids, 2 a7m price}; no response class):
    /// the local shop validates the catalogue, account ownership and quoted price, then commits before publishing unlocks.
    /// </summary>
    [Handles(OperationCodes.BuyVanityUnlocks, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuyVanityUnlocks(BuyVanityUnlocksRequest req)
    {
        if (Player is not { } p || ShopRequest(OperationCodes.BuyVanityUnlocks) is not { } nonce)
            return new GenericResponse { ReturnCode = Vanity.Refused };
        var result = world.Shop.BuyVanity(p, req.dt, req.du ?? [], req.dv, nonce);
        if (!result.Success) Expeditions.Tell(p, result.Message);
        return new GenericResponse { ReturnCode = result.Success ? (short)0 : Vanity.Refused };
    }

    [Handles(OperationCodes.GetVanityConsumables, Status = SessionStatus.InWorld)]
    private AlbionResponse GetVanityConsumables(GetVanityConsumablesRequest req)
    {
        var owned = world.Shop.Consumables(Player!.Character.Account);
        return new GetVanityConsumablesResponse { bni = owned.Ids, bnj = owned.Charges };
    }

    [Handles(OperationCodes.BuyVanityConsumableCharges, Status = SessionStatus.InWorld)]
    private AlbionResponse BuyVanityConsumableCharges(BuyVanityConsumableChargesRequest req)
    {
        if (Player is not { } p || ShopRequest(OperationCodes.BuyVanityConsumableCharges) is not { } nonce)
            return new GenericResponse { ReturnCode = Vanity.Refused };
        // Native quantities are bundles (100 charges in the installed data), and the price is raw gold.
        // Shop publishes only the committed charge delta; a replay must not send that additive event again.
        var result = world.Shop.BuyConsumables(p, req.ail, req.aim ?? [], req.ain ?? [], req.aio, nonce);
        if (!result.Success) Expeditions.Tell(p, result.Message);
        return new GenericResponse { ReturnCode = result.Success ? (short)0 : Vanity.Refused };
    }

    [Handles(OperationCodes.EquipKillEmote, Status = SessionStatus.InWorld)]
    private AlbionResponse EquipKillEmote(EquipKillEmoteRequest req)
    {
        var p = Player!;
        var success = world.Shop.EquipKillEmote(p, req.aiq);
        return new EquipKillEmoteResponse { ReturnCode = success ? (short)0 : Vanity.Refused, bnh = world.Shop.EquippedKillEmote(p.Character) };
    }

    [Handles(OperationCodes.ChangeKillEmotePlayOnKnockdownSetting, Status = SessionStatus.InWorld)]
    private AlbionResponse ChangeKillEmotePlayOnKnockdownSetting(ChangeKillEmotePlayOnKnockdownSettingRequest req) =>
        new GenericResponse { ReturnCode = world.Shop.SetKillEmoteOnKnockdown(Player!, req.aip) ? (short)0 : Vanity.Refused };

    /// <summary>GetMountSkins (op 385, ue no fields; af4 {0 int[]}): one skin number per catalogue category, -1 when none is selected.</summary>
    [Handles(OperationCodes.GetMountSkins)]
    private AlbionResponse? GetMountSkins(GetMountSkinsRequest req) =>
        new GetMountSkinsResponse { a1p = world.Vanity.MountSkins(Character) };

    /// <summary>SetMountSkin (op 386, aa5 {0 category index, 1 skin number or -1}; no response class): RC 0 (the client then shows it) or <see cref="Vanity.Refused"/>.</summary>
    [Handles(OperationCodes.SetMountSkin, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetMountSkin(SetMountSkinRequest req)
    {
        if (Player is not { } p) return new GenericResponse { ReturnCode = Vanity.Refused };
        if (world.Vanity.SetMountSkin(p, req.afw, req.afx) is { } why)
        {
            Log.Info(Name, $"'{p.Character.Name}' mount skin {req.afx} for category {req.afw} refused: {why}");
            return new GenericResponse { ReturnCode = Vanity.Refused };
        }
        return new GenericResponse();
    }

    /// <summary>SetWardrobe (op 387, abb {0 int[7] wardrobe skin numbers by EquipmentSlot}; no response class): what is owned is kept, UpdateWardrobe to all; RC 0.</summary>
    [Handles(OperationCodes.SetWardrobe, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetWardrobe(SetWardrobeRequest req)
    {
        if (Player is not { } p) return new GenericResponse { ReturnCode = Vanity.Refused };
        world.Vanity.SetWardrobe(p, req.agb);
        return new GenericResponse();
    }
}
