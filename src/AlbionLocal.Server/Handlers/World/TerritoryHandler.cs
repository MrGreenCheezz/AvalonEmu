using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The territory window's requests (stageP territories SPEC §2.1; the rules in <see cref="Systems.Territories"/>): the info requests of phase P1,
/// GetMonolithInfo (117), GetClaimInfo (118) and GetTerritorySeasonPoints (120), each with the territory's Guid as text in key 0; phase P3's claim
/// (302/303), give up (115), access (116), food (223) and the guild's list (138).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>GetMonolithInfo (op 117, ud: 0 territory): af3 {0 the food (a7m), 1 the tower level} of a held watchtower [guess: no reader found], else 0/0.</summary>
    [Handles(OperationCodes.GetMonolithInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetMonolithInfo(GetMonolithInfoRequest req) =>
        Player is { } player ? world.Territories.MonolithInfo(player, req.n8) : null;

    /// <summary>GetClaimInfo (op 118, tg: 0 territory): ae7 {0 guild fame, 1 territories owned, 2 false, 3 claimable by the season}.</summary>
    [Handles(OperationCodes.GetClaimInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetClaimInfo(GetClaimInfoRequest req) =>
        Player is { } player ? world.Territories.ClaimInfo(player, req.mo) : null;

    /// <summary>GetTerritorySeasonPoints (op 120, u5: 0 territory): agr, false / zeros / tower level -1 for an unowned territory.</summary>
    [Handles(OperationCodes.GetTerritorySeasonPoints, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetTerritorySeasonPoints(GetTerritorySeasonPointsRequest req) =>
        Player is { } player ? world.Territories.SeasonPoints(player, req.o5) : null;

    /// <summary>A refused territory request: a system line saying why.</summary>
    private void TellTerritory(string text) => SendEvent(new SystemMessageEvent { d0p = text, d0q = 0 });

    /// <summary>
    /// TerritoryClaimStart (op 302, abt {0 stamp, 1 the monolith, 2 the territory}; czk.u sends it with its own bar of territoryclaimtimeinsec):
    /// the channel (<see cref="Systems.Territories.StartClaim"/>), RC 0. Refused: RC 12085 (@OVERHEADMESSAGE_TERRITORY_ERROR) and a system line.
    /// </summary>
    [Handles(OperationCodes.TerritoryClaimStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? TerritoryClaimStart(TerritoryClaimStartRequest req)
    {
        if (Player is not { } p) return null;
        if (world.Territories.StartClaim(p, req.agu, req.agv) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{p.Character.Name}' territory claim refused: {why}");
        TellTerritory($"The territory cannot be claimed: {why}.");
        return new GenericResponse { ReturnCode = Systems.Territories.ClaimError };
    }

    /// <summary>TerritoryClaimCancel (op 303, abs {}): the running claim ends (341), RC 0.</summary>
    [Handles(OperationCodes.TerritoryClaimCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? TerritoryClaimCancel(TerritoryClaimCancelRequest req)
    {
        if (Player is not { } p) return null;
        world.Territories.CancelClaim(p, "cancelled");
        return new GenericResponse();
    }

    /// <summary>GiveUpTerritory (op 115, u8 {0 territory}): <see cref="Systems.Territories.GiveUp"/>, RC 0; refused RC 1 and a system line.</summary>
    [Handles(OperationCodes.GiveUpTerritory, Status = SessionStatus.InWorld)]
    private AlbionResponse? GiveUpTerritory(GiveUpTerritoryRequest req)
    {
        if (Player is not { } p) return null;
        if (world.Territories.GiveUp(p, req.o7) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{p.Character.Name}' give up of territory {req.o7} refused: {why}");
        TellTerritory($"The territory cannot be given up: {why}.");
        return new GenericResponse { ReturnCode = Systems.Territories.Refused };
    }

    /// <summary>ChangeTerritoryAccessRights (op 116, ql {0 territory, 1 byte}): <see cref="Systems.Territories.SetAccess"/>, RC 0; refused RC 1 and a line.</summary>
    [Handles(OperationCodes.ChangeTerritoryAccessRights, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeTerritoryAccessRights(ChangeTerritoryAccessRightsRequest req)
    {
        if (Player is not { } p) return null;
        if (world.Territories.SetAccess(p, req.e5, req.e6) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{p.Character.Name}' access change of territory {req.e5} refused: {why}");
        TellTerritory($"The territory's access cannot be changed: {why}.");
        return new GenericResponse { ReturnCode = Systems.Territories.Refused };
    }

    /// <summary>
    /// TerritoryFillNutrition (op 223, abu {0 territory, 1 the item stack, 2 the count}): food into the territory's store
    /// (<see cref="Systems.Territories.CheckFood"/>): the items leave the stack, RC 0. Refused: RC 1 and a system line.
    /// </summary>
    [Handles(OperationCodes.TerritoryFillNutrition, Status = SessionStatus.InWorld)]
    private AlbionResponse? TerritoryFillNutrition(TerritoryFillNutritionRequest req)
    {
        if (Player is not { } p) return null;
        var (s, item, count, why) = world.Territories.CheckFood(p, req.agw, req.agx, req.agy);
        if (s is null || item is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' territory food refused: {why}");
            TellTerritory($"The territory cannot be fed: {why}.");
            return new GenericResponse { ReturnCode = Systems.Territories.Refused };
        }
        var stack = p.Inventory.Bag.Find(req.agx)!;
        if ((stack.Count -= count) > 0) SendCount(stack);
        else
        {
            p.Inventory.Bag.Take(p.Inventory.Bag.IndexOf(stack));
            SendEvent(new InventoryDeleteItemEvent { ckr = stack.Id });
        }
        world.Territories.Feed(s, item, count);
        p.SaveItems();
        world.Saves.SaveNow(p.Character); // the food is stored at once: so are the items it took
        return new GenericResponse();
    }

    /// <summary>GetMyTerritories (op 138, uf {}): af5, the guild's territories and castles (<see cref="Systems.Territories.MyTerritories"/>).</summary>
    [Handles(OperationCodes.GetMyTerritories, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetMyTerritories(GetMyTerritoriesRequest req) =>
        Player is { } player ? world.Territories.MyTerritories(player) : null;
}
