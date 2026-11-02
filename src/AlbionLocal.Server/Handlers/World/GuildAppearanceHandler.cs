using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.SetGuildCodex, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetGuildCodex(SetGuildCodexRequest req)
    {
        if (Player is not { } player) return new SetGuildCodexResponse { ReturnCode = 1 };
        var result = world.Guilds.ChangeCodex(player.Character.Id, req.afv, out var guild);
        if (result != 0 || guild is null) return new SetGuildCodexResponse { ReturnCode = result };
        SendResponse(OperationCodes.SetGuildCodex, new SetGuildCodexResponse(), RequestId, RequestEncrypted);
        world.Guilds.PublishCodex(guild);
        return null;
    }

    [Handles(OperationCodes.GetGuildFactionAllegiance, Status = SessionStatus.InWorld)]
    private AlbionResponse GetGuildFactionAllegiance(GetGuildFactionAllegianceRequest req) =>
        Player is { } player ? world.Guilds.FactionAllegiance(player.Character.Id) : new GetGuildFactionAllegianceResponse { ReturnCode = 1 };

    [Handles(OperationCodes.ChangeGuildFactionAllegiance, Status = SessionStatus.InWorld)]
    private AlbionResponse ChangeGuildFactionAllegiance(ChangeGuildFactionAllegianceRequest req) => new GenericResponse
    {
        ReturnCode = Player is { } player ? world.Guilds.ChangeFactionAllegiance(player.Character.Id, req.ep) : (short)1,
    };
}
