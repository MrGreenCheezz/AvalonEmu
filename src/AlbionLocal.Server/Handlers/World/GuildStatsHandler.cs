using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    // 345319 ge2.a 1CBD54B requests RankingEntityTypes.Guild (1). ctu.b 9B8DC9-9B8DE2
    // routes event147 by guild name to that entity's cty, independently of character event142.
    private AlbionResponse? GetGuildStats(GetCharacterStatsRequest req)
    {
        if (world.Guilds.PublicStats(req.mf) is not { } stats) return new GenericResponse { ReturnCode = 1 };
        SendResponse(OperationCodes.GetCharacterStats, new GenericResponse(), RequestId, RequestEncrypted);
        SendEvent(stats);
        return null;
    }

    [Handles(OperationCodes.GuildGetOptionalStats, Status = SessionStatus.InWorld)]
    private AlbionResponse GuildGetOptionalStats(GuildGetOptionalStatsRequest req) =>
        world.Guilds.OptionalStats(req.pr, req.ps);

    [Handles(OperationCodes.GuildSetOptionalStats, Status = SessionStatus.InWorld)]
    private AlbionResponse GuildSetOptionalStats(GuildSetOptionalStatsRequest req) => new GenericResponse
    {
        ReturnCode = Player is { } player ? world.Guilds.ChangeOptionalStats(player.Character.Id, req.pt) : (short)19401,
    };
}
