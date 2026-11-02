using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.RequestGuildFinderNameSearch, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildFinderNameSearch(RequestGuildFinderNameSearchRequest req)
    {
        if (Player is not { } player) return null;
        var result = world.Guilds.FinderName(player, req.aab);
        // Inspect the normal serializer's shape only; the session sends this response once below.
        // No guild names, query text or payload bytes are included in the diagnostic.
        var response = AlbionSerializer.BuildResponse(result, RequestId, OperationCodes.RequestGuildFinderNameSearch);
        string Shape(byte key) => !response.Parameters.TryGetValue(key, out var value) ? "missing"
            : value is Array array ? $"{value.GetType().Name}[{array.Length}]" : value?.GetType().Name ?? "null";
        Log.Info("guild-finder", $"name search by '{player.Character.Name}': ids {result.bi3?.Length ?? 0}, "
            + $"names {result.bi4?.Length ?? 0}, joins {result.bjd?.Length ?? 0}, rc {result.ReturnCode}, request {RequestId?.ToString() ?? "none"}; "
            + $"wire 0={Shape(0)}, 1={Shape(1)}, 10={Shape(10)}, 253={Shape(253)}, 255={Shape(255)}");
        return result;
    }

    [Handles(OperationCodes.RequestGuildFinderFilteredList, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildFinderFilteredList(RequestGuildFinderFilteredListRequest req)
    {
        if (Player is not { } player) return null;
        var result = world.Guilds.FinderFiltered(player, req);
        Log.Info("guild-finder", $"filtered search by '{player.Character.Name}': {result.bh5?.Length ?? 0} real guild(s), "
            + $"offset {req.zy}, count {req.zz}, eligible {req.z2}, beginner {req.z3}, language {req.z4}, home {req.z5}, "
            + $"active {req.z6}-{req.z7}, UTC {req.z8}-{req.z9}, activities {req.aaa}");
        return result;
    }

    [Handles(OperationCodes.RequestGuildFinderRecommendedList, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildFinderRecommendedList(RequestGuildFinderRecommendedListRequest req) =>
        Player is { } player ? world.Guilds.FinderRecommended(player, req.aac, req.aad) : null;

    [Handles(OperationCodes.ApplyToGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? ApplyToGuild(ApplyToGuildRequest req)
    {
        if (Player is not { } player) return null;
        var result = world.Guilds.ApplyThroughFinder(player, req, out var cooldown);
        return new ApplyToGuildResponse { ReturnCode = result, akw = cooldown };
    }

    [Handles(OperationCodes.InstantJoinGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? InstantJoinGuild(InstantJoinGuildRequest req)
    {
        if (Player is not { } player) return null;
        var result = world.Guilds.InstantJoinThroughFinder(player, req.rc, out var guild, out var cooldown);
        if (result != 0 || guild is null) return new InstantJoinGuildResponse { ReturnCode = result, a7w = cooldown };
        SendResponse(OperationCodes.InstantJoinGuild, new InstantJoinGuildResponse(), RequestId, RequestEncrypted);
        world.Guilds.AfterJoin(player, guild);
        return null;
    }

    [Handles(OperationCodes.AnswerGuildApplication, Status = SessionStatus.InWorld)]
    private AlbionResponse? AnswerGuildApplication(AnswerGuildApplicationRequest req)
    {
        if (Player is not { } player) return null;
        var result = world.Guilds.AnswerApplication(player, req.aw, req.ax, out var character, out var guild, out var applicant, out var cooldown);
        SendResponse(OperationCodes.AnswerGuildApplication,
            new AnswerGuildApplicationResponse { ReturnCode = result, aku = cooldown, akv = applicant }, RequestId, RequestEncrypted);
        if (result == 0 && guild is not null) world.Guilds.AfterApplicationJoin(character, guild);
        return null;
    }
}
