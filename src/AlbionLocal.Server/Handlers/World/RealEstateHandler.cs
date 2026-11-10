using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The real-estate window's requests (public issue #18, analysis/work/realestate/SPEC.md §3): claiming a free plot, giving it up, its outline,
/// the auction's data and bids, listing it for sale and buying a listed one. The rules are <see cref="Systems.RealEstates"/>'; a refusal is
/// RC 1 with a system line saying why (the client's own codes for these are not known; its error message box shows the generic text).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The RC of a refused real-estate request.</summary>
    public const short RealEstateRefused = 1;

    private GenericResponse RealEstateRefusal(Entities.Player p, string op, string why)
    {
        Log.Info(Name, $"'{p.Character.Name}' {op} refused: {why}");
        TellIsland(why);
        return new GenericResponse { ReturnCode = RealEstateRefused };
    }

    /// <summary>ClaimRealEstate (op 171, q4 {0 the plot Guid}; RealEstateGui.OnClaimSubmit 0x181618500, no callback): a free plot for its price.</summary>
    [Handles(OperationCodes.ClaimRealEstate, Status = SessionStatus.InWorld)]
    private AlbionResponse? ClaimRealEstate(ClaimRealEstateRequest req)
    {
        if (Player is not { } p) return null;
        return world.RealEstates.Claim(p, req.fy, out var why) ? new GenericResponse() : RealEstateRefusal(p, "plot claim", why);
    }

    /// <summary>GiveUpRealEstate (op 172, u7 {0 the plot Guid}): the own plot becomes free, the buildings on it come down.</summary>
    [Handles(OperationCodes.GiveUpRealEstate, Status = SessionStatus.InWorld)]
    private AlbionResponse? GiveUpRealEstate(GiveUpRealEstateRequest req)
    {
        if (Player is not { } p) return null;
        return world.RealEstates.GiveUp(p, req.o6, out var why) ? new GenericResponse() : RealEstateRefusal(p, "plot give-up", why);
    }

    /// <summary>ChangeRealEstateOutline (op 173, qk {0 the plot Guid, 1 the OutlineType name}): the own plot's outline for its gold cost.</summary>
    [Handles(OperationCodes.ChangeRealEstateOutline, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeRealEstateOutline(ChangeRealEstateOutlineRequest req)
    {
        if (Player is not { } p) return null;
        return world.RealEstates.ChangeOutline(p, req.e3, req.e4, out var why) ? new GenericResponse() : RealEstateRefusal(p, "plot outline", why);
    }

    /// <summary>RealEstateGetAuctionData (op 264, zp {0 the plot object}): aih, the asker's bid, the highest bid and the auction's times.</summary>
    [Handles(OperationCodes.RealEstateGetAuctionData, Status = SessionStatus.InWorld)]
    private AlbionResponse? RealEstateGetAuctionData(RealEstateGetAuctionDataRequest req)
    {
        if (Player is not { } p) return null;
        return world.RealEstates.AuctionData(p, req.y9) ?? (AlbionResponse)RealEstateRefusal(p, "auction data", "@REALESTATE_AUCTION_NO_AUCTION_FOUND");
    }

    /// <summary>
    /// RealEstateBidOnAuction (op 265, zo {0 the plot object, 1 the whole bid a7m}; cly.aqr 0x180B60F10 with callback cly.af(g7q) 0x180B60800:
    /// RC 0 asks the auction data again, another RC goes to the client's error message): the bid, held from the silver.
    /// </summary>
    [Handles(OperationCodes.RealEstateBidOnAuction, Status = SessionStatus.InWorld)]
    private AlbionResponse? RealEstateBidOnAuction(RealEstateBidOnAuctionRequest req)
    {
        if (Player is not { } p) return null;
        return world.RealEstates.Bid(p, req.y7, req.y8, out var why) ? new GenericResponse() : RealEstateRefusal(p, "plot bid", why);
    }

    /// <summary>SetSaleStatusForEstate (aa9 {0 the plot object, 1 the price a7m, 2 cancel}; RealEstateGui.OnSellEstate / OnCancelSale): list or unlist the own plot.</summary>
    [Handles(OperationCodes.SetSaleStatusForEstate, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetSaleStatusForEstate(SetSaleStatusForEstateRequest req)
    {
        if (Player is not { } p) return null;
        return world.RealEstates.SetSale(p, req.af5, req.af6, req.af7, out var why) ? new GenericResponse() : RealEstateRefusal(p, "plot sale", why);
    }

    /// <summary>BuyRealEstate (op 170, p2 {0 the plot object, 1 the asking price a7m}; RealEstateGui.OnBuyUserSale): buy a listed plot.</summary>
    [Handles(OperationCodes.BuyRealEstate, Status = SessionStatus.InWorld)]
    private AlbionResponse? BuyRealEstate(BuyRealEstateRequest req)
    {
        if (Player is not { } p) return null;
        return world.RealEstates.Buy(p, req.dr, req.ds, out var why) ? new GenericResponse() : RealEstateRefusal(p, "plot purchase", why);
    }
}
