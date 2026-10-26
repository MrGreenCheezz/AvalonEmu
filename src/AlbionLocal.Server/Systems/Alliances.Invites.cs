using AlbionLocal.Photon;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>The open alliance invitations (SPEC §4 A.3): saved with the alliance store, each with its kind-2 mail in the invited guild's mailbox.</summary>
public sealed partial class Alliances
{
    private readonly List<AllianceInvite> _invites = [];

    private void LoadInvites()
    {
        foreach (var i in store!.LoadAllianceInvites())
            if (_byId.ContainsKey(i.Alliance) && Guilds.All.Any(g => g.Id == i.Guild)) _invites.Add(i);
            else store.DeleteAllianceInvite(i.Alliance, i.Guild);
    }

    /// <summary>The open invitations of <paramref name="a"/>, oldest first.</summary>
    public IEnumerable<AllianceInvite> InvitesOf(Alliance a) => _invites.Where(i => i.Alliance == a.Id);

    /// <summary>The open invitations of guild <paramref name="guild"/>.</summary>
    public IEnumerable<AllianceInvite> InvitesTo(Guid guild) => _invites.Where(i => i.Guild == guild);

    /// <summary>The invitation is gone (answered, cancelled, its alliance disbanded): its row and its mail.</summary>
    private void DropInvite(AllianceInvite invite)
    {
        _invites.Remove(invite);
        try { store?.DeleteAllianceInvite(invite.Alliance, invite.Guild); }
        catch (Exception ex) { Log.Warn("alliance", $"invitation of guild {invite.Guild} not deleted: {ex.Message}"); }
        if (invite.Mail != 0 && Mailer.On(world) && world.Mail!.Mail(invite.Mail) is { Guild: true } mail && mail.Owner == invite.Guild)
        {
            var unread = !mail.Read;
            world.Mail.DeleteMails(invite.Guild, true, [invite.Mail], 0, [], MailCategory.All);
            if (unread) Mailer.Notify(world, invite.Guild, guild: true);
        }
    }
}
