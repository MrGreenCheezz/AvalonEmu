using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    [Handles(OperationCodes.RequestGuildRecruitmentInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestGuildRecruitmentInfo(RequestGuildRecruitmentInfoRequest req) =>
        Player is { } player ? world.Guilds.RecruitmentInfo(player.Character.Id, req.aae, req.aaf) : null;

    [Handles(OperationCodes.UpdateGuildRecruitmentInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? UpdateGuildRecruitmentInfo(UpdateGuildRecruitmentInfoRequest req)
    {
        if (Player is not { } player) return null;
        var saved = world.Guilds.ChangeRecruitment(player.Character.Id, player.Character.Name ?? "", req);
        // cxm.u(c2q) 9F3C86 installs the proposed values before sending 184. Return committed
        // configuration through 185 to refresh that cache and the editor, including after a refused write.
        if (world.Guilds.NewMemberMailOwner(player.Character.Id) is { } guild)
            SendResponse(OperationCodes.RequestGuildRecruitmentInfo,
                world.Guilds.RecruitmentInfo(player.Character.Id, guild.Id, 0), RequestId, RequestEncrypted);
        if (!saved) Systems.Expeditions.Tell(player, "No se pudo guardar la configuración de reclutamiento del gremio.");
        return new GenericResponse { ReturnCode = saved ? (short)0 : (short)1 };
    }

    // 345319 qi/ug/af6: change479 key0=text; get480 key0=guild Guid, key1=text.
    // c2n.a(af6) A417AE copies +0x30 into its new-member mail cache (+0x108), then notifies
    // GuildNewMemberMailGui.OnReceivedNewMemberMailContent (107D9A0) to remove its loading view.
    [Handles(OperationCodes.GetNewGuildMemberMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetNewGuildMemberMail(GetNewGuildMemberMailRequest req)
    {
        if (Player is not { } player) return null;
        var guild = world.Guilds.NewMemberMailOwner(player.Character.Id);
        return new GetNewGuildMemberMailResponse
        {
            ReturnCode = guild is null ? (short)1 : (short)0,
            a19 = guild?.Id ?? Guid.Empty,
            a2a = guild?.Progress.NewMemberMail ?? "",
        };
    }

    [Handles(OperationCodes.ChangeNewGuildMemberMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeNewGuildMemberMail(ChangeNewGuildMemberMailRequest req)
    {
        if (Player is not { } player) return null;
        var saved = world.Guilds.ChangeNewMemberMail(player.Character.Id, req.ez);
        // The native editor updates this cache optimistically (A3FA7D). Refresh it with committed
        // text on either outcome, so a failed save cannot appear accepted on reopening the editor.
        if (world.Guilds.NewMemberMailOwner(player.Character.Id) is { } guild)
            SendResponse(OperationCodes.GetNewGuildMemberMail,
                new GetNewGuildMemberMailResponse { a19 = guild.Id, a2a = guild.Progress.NewMemberMail }, RequestId, RequestEncrypted);
        if (!saved) Systems.Expeditions.Tell(player, "No se pudo guardar el correo de bienvenida del gremio.");
        return new GenericResponse { ReturnCode = saved ? (short)0 : (short)1 };
    }
}
