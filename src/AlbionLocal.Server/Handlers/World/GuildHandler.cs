using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Guilds (analysis/work/stageH/guilds/SPEC.md §1, §3; the rules in <see cref="Systems.Guilds"/>): create, invite, accept,
/// decline, leave, kick, the role tags and a side request of the guild window (the Member Logs and the Guild Season window are
/// GuildSeasonHandler's). Only JoinGuild and GetGuildEnergyDrainInfo
/// carry a request id; the others are answered without one (or not at all: decline and the tag ops have no response class).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// CreateGuild (op 105, rk: 0 name, 1-7 crest, 8 banner; no request id): rc 0 without keys after GuildUpdate and 349 (the
    /// client ignores rc 0), then the founder's 106 to it and its observers; an error rc (4206 with key 0 = the cooldown's end).
    /// </summary>
    [Handles(OperationCodes.CreateGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? CreateGuild(CreateGuildRequest req)
    {
        if (Player is not { } player) return null;
        var logo = new GuildLogo(req.iq, req.ir, req._is, req.it, req.iu, req.iv, req.iw);
        var rc = world.Guilds.Create(player, req.ip, logo, req.ix, out var end);
        if (rc != 0)
        {
            Log.Info(Name, $"'{player.Character.Name}' CreateGuild refused: {rc}");
            return new CreateGuildResponse { ReturnCode = rc, ape = end };
        }
        SendResponse(OperationCodes.CreateGuild, new CreateGuildResponse(), RequestId, RequestEncrypted);
        world.Guilds.Announce(player);
        return null;
    }

    /// <summary>InviteToGuild (op 106, wq: 0 name; no request id): rc 0 {0 the target's name} or the error (key 0 name, key 1 a cooldown's end).</summary>
    [Handles(OperationCodes.InviteToGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? InviteToGuild(InviteToGuildRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Guilds.Invite(player, req.sq, out var name, out var end);
        if (rc != 0) Log.Info(Name, $"'{player.Character.Name}' InviteToGuild refused: {rc}");
        return new InviteToGuildResponse { ReturnCode = rc, a7x = name, a7y = end };
    }

    /// <summary>JoinGuild (op 103, no fields, request id): accepts the open invitation; rc 0 {0 guild, 1 name}, then the guild's events.</summary>
    [Handles(OperationCodes.JoinGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? JoinGuild(JoinGuildRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Guilds.Accept(player, out var guild);
        if (rc != 0 || guild is null) return new GenericResponse { ReturnCode = rc };
        SendResponse(OperationCodes.JoinGuild, new JoinGuildResponse { a77 = guild.Id, a78 = guild.Name }, RequestId, RequestEncrypted);
        world.Guilds.AfterJoin(player, guild);
        return null;
    }

    /// <summary>DeclineGuildInvitation (op 107, rr: 0 the reason; no response class): the inviter gets GuildInviteDeclined.</summary>
    [Handles(OperationCodes.DeclineGuildInvitation, Status = SessionStatus.InWorld)]
    private AlbionResponse? DeclineGuildInvitation(DeclineGuildInvitationRequest req)
    {
        if (Player is { } player) world.Guilds.Decline(player, req.jf);
        return null;
    }

    /// <summary>LeaveGuild (op 104, w7: 0 the successor of a guild master; no request id): an empty rc 0 (the client clears its guild), then the leaver's empty 106; rc 1 refused.</summary>
    [Handles(OperationCodes.LeaveGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? LeaveGuild(LeaveGuildRequest req)
    {
        if (Player is not { } player) return null;
        var rc = world.Guilds.Leave(player, req.tg);
        if (rc != 0) return new LeaveGuildResponse { ReturnCode = rc };
        SendResponse(OperationCodes.LeaveGuild, new LeaveGuildResponse(), RequestId, RequestEncrypted);
        world.Guilds.Announce(player);
        return null;
    }

    /// <summary>KickFromGuild (op 108, wz: 0 name, "" from the GUI, 1 member Guid; no request id): an empty response, rc 0 or 1 (the client ignores it).</summary>
    [Handles(OperationCodes.KickFromGuild, Status = SessionStatus.InWorld)]
    private AlbionResponse? KickFromGuild(KickFromGuildRequest req) =>
        Player is { } player ? new KickFromGuildResponse { ReturnCode = world.Guilds.Kick(player, req.s2, req.s1) } : null;

    /// <summary>AssignGuildAccessTag (op 328, pi: 0 member name, 1 tag; no response class): the rank, or with the GM tag the hand-over.</summary>
    [Handles(OperationCodes.AssignGuildAccessTag, Status = SessionStatus.InWorld)]
    private AlbionResponse? AssignGuildAccessTag(AssignGuildAccessTagRequest req)
    {
        if (Player is { } player && !world.Guilds.AssignTag(player, req.cj, req.ck))
            Log.Info(Name, $"'{player.Character.Name}' AssignGuildAccessTag refused");
        return null;
    }

    /// <summary>RemoveGuildAccessTagFromPlayer (op 329, zv: 0 name, 1 tag; no response class): the member becomes a RECRUIT.</summary>
    [Handles(OperationCodes.RemoveGuildAccessTagFromPlayer, Status = SessionStatus.InWorld)]
    private AlbionResponse? RemoveGuildAccessTagFromPlayer(RemoveGuildAccessTagFromPlayerRequest req)
    {
        if (Player is { } player && !world.Guilds.RemoveTag(player, req.zh, req.zi))
            Log.Info(Name, $"'{player.Character.Name}' RemoveGuildAccessTagFromPlayer refused");
        return null;
    }

    /// <summary>
    /// DepositToGuildAccount (op 134, ry {0 gold, 1 amount a7m}; no request id, no response class): as the official 002835 deposit,
    /// UpdateMoney, then the bare rc 0 without a request id, then GuildUpdate kind 2 to the members; refused: the bare
    /// <see cref="Systems.Guilds.AccountRefused"/> and nothing else (public issue #20).
    /// </summary>
    [Handles(OperationCodes.DepositToGuildAccount, Status = SessionStatus.InWorld)]
    private AlbionResponse? DepositToGuildAccount(DepositToGuildAccountRequest req) =>
        Player is { } player ? AccountTransfer(OperationCodes.DepositToGuildAccount, world.Guilds.DepositToAccount(player, req.jr, req.jq, out var g), g) : null;

    /// <summary>
    /// WithdrawalFromAccount (op 135, act {0 gold, 1 amount a7m}; no request id, no response class): answered as a deposit (no
    /// official capture of it): UpdateMoney, the bare rc, GuildUpdate kind 2.
    /// </summary>
    [Handles(OperationCodes.WithdrawalFromAccount, Status = SessionStatus.InWorld)]
    private AlbionResponse? WithdrawalFromAccount(WithdrawalFromAccountRequest req) =>
        Player is { } player ? AccountTransfer(OperationCodes.WithdrawalFromAccount, world.Guilds.WithdrawFromAccount(player, req.ai6, req.ai5, out var g), g) : null;

    /// <summary>The answer of a guild account transfer (its UpdateMoney is out already): the bare rc, after a success the account update.</summary>
    private AlbionResponse? AccountTransfer(OperationCodes code, short rc, Guild? guild)
    {
        if (rc != 0 || guild is null)
        {
            Log.Info(Name, $"'{Player!.Character.Name}' {code} refused: {rc}");
            return new GenericResponse { ReturnCode = rc };
        }
        SendResponse(code, new GenericResponse(), RequestId, RequestEncrypted);
        world.Guilds.SendToMembers(guild, world.Guilds.AccountUpdate(guild));
        return null;
    }

    /// <summary>GetGuildEnergyDrainInfo (op 414, request id): rc 0 {0 the guild's Guid}, empty outside a guild (SPEC §1).</summary>
    [Handles(OperationCodes.GetGuildEnergyDrainInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? GetGuildEnergyDrainInfo(GetGuildEnergyDrainInfoRequest req) =>
        Player is { } player ? new GetGuildEnergyDrainInfoResponse { axl = world.Guilds.Of(player.Character.Id)?.Id ?? Guid.Empty } : null;
}
