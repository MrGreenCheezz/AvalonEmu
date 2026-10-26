using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The arena operations (analysis/work/stageN/arena/SPEC.md §1.1): the arena agency's register (281), the queue's cancel (283), the leave
/// (284), the ready popup's accept (285) and decline (286). The state and the rules are in <see cref="Arenas"/>. The group invite (282),
/// the custom matches (289) and the crystal entering lock (287/288) are answered without effect.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// ArenaRegister (o5 {0 the agency's object, 1 the arena category, 2 as a group, 3 the item roles, 4/5 -1}; ArenaDetailsGui.SendArenaRegister
    /// 0x1812ED2C0): the queue of the category's mode (2 ranked_1v1 → 1v1, 0 unranked → 5v5, 1 ranked → the Crystal Arena, or the 5v5 without
    /// Arena:CrystalArena) with the party when key 2. RC 0 with
    /// {1 now} (the client's OnRegisterResponse shows an error only for RC 11243); a refusal is RC 1 with the reason in chat.
    /// </summary>
    [Handles(OperationCodes.ArenaRegister, Status = SessionStatus.InWorld)]
    private AlbionResponse? ArenaRegister(ArenaRegisterRequest req)
    {
        if (Player is not { } player) return new GenericResponse { ReturnCode = 1 };
        var refused = Arenas.ModeOfCategory(req.bp, world.Config.Arena.CrystalArena) is not { } mode ? $"arena category {req.bp} is not offered here (unranked, ranked or 1v1)"
            : world.Arenas.Register(player, mode, req.bq, req.bp, req.br);
        if (refused is null) return new ArenaRegisterResponse { ak0 = world.GameNow };
        Log.Info(Name, $"'{player.Character.Name}' arena registration refused: {refused}");
        Expeditions.Tell(player, $"Arena: {refused}.");
        return new GenericResponse { ReturnCode = 1 };
    }

    /// <summary>ArenaRegisterCancel (o6 {0 short}, the HUD's and the popup's cancel, dg9.at): out of the queue or the ready check. RC 0.</summary>
    [Handles(OperationCodes.ArenaRegisterCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? ArenaRegisterCancel(ArenaRegisterCancelRequest req)
    {
        if (Player is { } player) world.Arenas.Unregister(player, "cancelled");
        return new GenericResponse();
    }

    /// <summary>ArenaLeave (o4, no keys, dg9.ap): out of the arena match (a forfeit while it runs), else out of the queue. RC 0.</summary>
    [Handles(OperationCodes.ArenaLeave, Status = SessionStatus.InWorld)]
    private AlbionResponse? ArenaLeave(ArenaLeaveRequest req)
    {
        if (Player is not { } player) return new GenericResponse();
        if (world.Arenas.Find(player.Map?.Cluster) is { } m) world.Arenas.Leave(player, m, "left");
        else world.Arenas.Unregister(player, "left");
        return new GenericResponse();
    }

    /// <summary>JoinArenaMatch (wt {0 the roles, 1 int}, the ready popup's join): the ready check accepted. RC 0, or 1 without a ready check.</summary>
    [Handles(OperationCodes.JoinArenaMatch, Status = SessionStatus.InWorld)]
    private AlbionResponse? JoinArenaMatch(JoinArenaMatchRequest req) =>
        new GenericResponse { ReturnCode = Player is { } player && world.Arenas.Accept(player) ? (short)0 : (short)1 };

    /// <summary>DeclineArenaInvitation (rp, no keys): the ready check (or the queue) declined. RC 0.</summary>
    [Handles(OperationCodes.DeclineArenaInvitation, Status = SessionStatus.InWorld)]
    private AlbionResponse? DeclineArenaInvitation(DeclineArenaInvitationRequest req)
    {
        if (Player is { } player) world.Arenas.Unregister(player, "declined");
        return new GenericResponse();
    }

    /// <summary>
    /// ClaimOrbStart (qy {0 now, 1 the orb, 2 Guid}; stageP league-pvp SPEC §1.2): a channel orb's claim (<see cref="Arenas.ClaimStart"/>). RC 0, or 1
    /// with the reason in the log (a zone orb needs no request).
    /// </summary>
    [Handles(OperationCodes.ClaimOrbStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? ClaimOrbStart(ClaimOrbStartRequest req)
    {
        if (Player is not { } player) return new GenericResponse { ReturnCode = 1 };
        if (world.Arenas.ClaimStart(player, req.fo) is not { } refused) return new GenericResponse();
        Log.Debug(Name, $"'{player.Character.Name}' orb claim refused: {refused}");
        return new GenericResponse { ReturnCode = 1 };
    }

    /// <summary>ClaimOrbCancel (qx, no keys): the player's orb channel ends (ClaimOrbCancel 130). RC 0.</summary>
    [Handles(OperationCodes.ClaimOrbCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? ClaimOrbCancel(ClaimOrbCancelRequest req)
    {
        if (Player is { } player) world.Arenas.CancelClaim(player, "cancelled");
        return new GenericResponse();
    }

    /// <summary>
    /// GetArenaRankings (ake, no keys → ajm; stageP league-pvp SPEC §1.2, §3.3, NOTES-protocol.md:736): {0 the arena season (short, 12 =
    /// SEASON_34 of arena_europe.bin), 1 the ranked categories [1 the Crystal Arena, 2 the 1v1] (int[]), 2 the rank level per category (short[],
    /// the index in the season's rank template, 0 = ARENA_IRON_1), 3 the rank points (int[]), 4 short[] zeros (kept by cfu, not read; 0 as
    /// sent officially)}. The client dereferences an entry for every ranked category (ArenaRankingInfoView, ArenaDetailsGui.FillRewards), so
    /// both are always there, in the shape of the official answer (the former content/opcode-stubs.json entry); Arena:RankPoints false (or
    /// before a ranked result) answers the lowest rank with 0 points. Answered in any session state, as the stub was.
    /// </summary>
    [Handles(OperationCodes.GetArenaRankings)]
    private AlbionResponse? GetArenaRankings(GetArenaRankingsRequest req)
    {
        var categories = Data.ArenaData.RankedCategories;
        var levels = new short[categories.Count];
        var points = new int[categories.Count];
        if (Player is { } player && world.Config.Arena.RankPoints)
            for (var i = 0; i < categories.Count; i++)
                if (world.Ratings.ArenaRank(player.Character.Id, categories[i]) is { } row)
                    (levels[i], points[i]) = ((short)Math.Clamp(row.Rank, 0, short.MaxValue), row.Points);
        return new GetArenaRankingsResponse
        {
            SeasonId = Data.ArenaData.SeasonIndex, CategoryIds = [.. categories], RankLevels = levels, RankPoints = points, bnw = new short[categories.Count],
        };
    }

    /// <summary>ArenaAddInvite (o2): no arena group invites here (register as a party, with the group box). RC 1 and the chat line.</summary>
    [Handles(OperationCodes.ArenaAddInvite, Status = SessionStatus.InWorld)]
    private AlbionResponse? ArenaAddInvite(ArenaAddInviteRequest req)
    {
        if (Player is { } player) Expeditions.Tell(player, "Arena: invite players to your party and register as a group instead.");
        return new GenericResponse { ReturnCode = 1 };
    }
}
