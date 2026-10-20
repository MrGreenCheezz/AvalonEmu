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
    /// BuyVanityUnlocks (op 384, p3 {0 short type, 1 short[] ids, 2 a7m price}; no response class): never sold here: RC <see cref="Vanity.Refused"/>
    /// and a chat line that the cosmetics come from content only.
    /// </summary>
    [Handles(OperationCodes.BuyVanityUnlocks)]
    private AlbionResponse? BuyVanityUnlocks(BuyVanityUnlocksRequest req)
    {
        if (Player is { } p)
        {
            Expeditions.Tell(p, Vanity.NotForSale);
            Log.Info(Name, $"'{p.Character.Name}' tried to buy vanity unlock(s) {string.Join(", ", req.du ?? [])}: refused (content only)");
        }
        return new GenericResponse { ReturnCode = Vanity.Refused };
    }

    /// <summary>GetMountSkins (op 385, ue no fields; af4 {0 int[]}): the mount skin numbers chosen (cjj.a builds its per-category table from them).</summary>
    [Handles(OperationCodes.GetMountSkins)]
    private AlbionResponse? GetMountSkins(GetMountSkinsRequest req) =>
        new GetMountSkinsResponse { a1p = Character is { } c ? Vanity.MountSkins(c) : [] };

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
