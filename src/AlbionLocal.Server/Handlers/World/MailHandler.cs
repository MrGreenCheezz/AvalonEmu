using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The mailbox (analysis/work/stageJ/mail/SPEC.md §1, §4.3): the personal and the guild inbox (GetMailInfos, GetMailCount, ReadMail,
/// DeleteMail, MarkMailUnread), the write view (SendNewMail: to a player, a guild or the own guild's members; text only, the client has no
/// attachment slots) and the attachments of the server's mails (ClaimAttachmentFromMail). Mails are stored at once in
/// <see cref="World.Mail"/> and made by <see cref="Mailer"/>. The official answers carry no request id (SPEC §1.6): the client matches them
/// by op code, so none is needed here; the response goes with the request's as every other.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>SendNewMail's return codes (SPEC §1.4; 0x1BAE from MailWriteView._SendGuildMembersMail_b__35_0).</summary>
    public const short MailPlayerNotFound = 7080, MailGuildNotFound = 7081, MailNotEnoughSilver = 7082, MailLimitReached = 7083,
        MailMutedByReceiver = 7084, MailRankNotFound = 7086;

    /// <summary>A refused mail request (no text in the client: a read becomes the "not accessible" view, a claim a re-read).</summary>
    public const short MailRefused = 1;

    /// <summary>
    /// The mailbox a request names (key "guild"): the character's own, or its guild's when it may read that (ACCESS_GUILD_MAIL, the guild master
    /// and The Right Hand: <see cref="Mailer.CanReadGuildMail"/>); Guid.Empty = none (no guild, no right). With mail off the character's
    /// own still names it (the empty mailbox of before), but nothing is read or written.
    /// </summary>
    private (Guid Owner, bool Guild) MailBox(Character c, bool guild)
    {
        if (!guild) return (c.Id, false);
        return world.Guilds.Of(c.Id) is { } g && g.Find(c.Id) is { } m && Mailer.CanReadGuildMail(m.Role) ? (g.Id, true) : (Guid.Empty, true);
    }

    /// <summary>
    /// The mailbox page the client asks for (op GetMailInfos, client class t8: 0 bool guild mail, 1 bd3.b mailbox kind, 2
    /// int page; ddp.k 0x180acd690 stores them in ddp+0x18 / +0x10 / +0x14 before ddp.t sends the request, without a request
    /// id). ddp.m 0x180acede0 takes the answer (af0) only when its return code is 0, key 1 (int) equals ddp+0x14 and key 2
    /// (byte) ddp+0x10, i.e. the request's keys 2 and 1 echoed, and key 0 equals the player's Guid (Join key 1; the guild's
    /// for guild mail); it then reads the mail count from key 3, a null key 3 throws (NullReferenceException), and builds
    /// the rows from the parallel arrays 3-13 (SPEC §1.2: 3 ids, 4 the sending character's Guid, 5 the same for the client's
    /// privacy lookup, 6 show the sender as author, 7 sender, 8 bd3.a kind, 9 bd3.b category, 10 read, 11 subject or type key,
    /// 12 received UTC ticks, 13 has attachments). Official (capture 002835): no request id on either side, key 0
    /// the character Guid, key 1 left out (= request key 2, 0), key 2 ByteZero (= request key 1), two system mails with zero Guids,
    /// newest first. Here: page key 2 of 50 (<see cref="Mailer.PageSize"/>) of the category (0 = all), newest first; without a
    /// mailbox (a guild's without the right, mail off) every array empty and key 0 left out, which the client drops.
    /// </summary>
    [Handles(OperationCodes.GetMailInfos, Status = SessionStatus.InWorld)]
    private GetMailInfosResponse? GetMailInfos(GetMailInfosRequest req)
    {
        if (Character is not { } c) return null;
        var (owner, guild) = MailBox(c, req.n1);
        var r = new GetMailInfosResponse
        {
            a0i = owner,  // [0] whose mailbox: the character's (the guild's for guild mail)
            a0j = req.n3, // [1] = request key 2 (ddp+0x14)
            a0k = req.n2, // [2] = request key 1, the mailbox kind (ddp+0x10)
        }.FillEmpty();    // [3]-[13] empty parallel arrays when there is no mail (key 3 must not be null)
        if (owner == Guid.Empty || !Mailer.On(world)) return r;
        var page = world.Mail!.MailPage(owner, guild, (MailCategory)req.n2, req.n3, Mailer.PageSize);
        r.a0l = [.. page.Select(m => m.Id)];
        r.a0m = [.. page.Select(m => m.SenderId)];
        r.a0n = [.. page.Select(m => m.SenderId)];
        r.a0o = [.. page.Select(m => m.SenderId != Guid.Empty)];
        r.a0p = [.. page.Select(m => m.Sender)];
        r.a0q = [.. page.Select(m => (byte)m.Kind)];
        r.a0r = [.. page.Select(m => (byte)m.Category)];
        r.a0s = [.. page.Select(m => m.Read)];
        r.a0t = [.. page.Select(m => m.Subject)];
        r.a0u = [.. page.Select(m => m.Received.Ticks)];
        r.a0v = [.. page.Select(m => m.Attachments > 0)];
        return r;
    }

    /// <summary>
    /// GetMailCount (op 175, t7: 0 guild, 1 category; the client asks after a full page or for a page past the first, SPEC §1.1): {0 the
    /// mailbox's Guid, 1 the category echoed, 2 the total}; ddp.k 0x180ACD6D0 takes it when keys 0 and 1 match its request.
    /// </summary>
    [Handles(OperationCodes.GetMailCount, Status = SessionStatus.InWorld)]
    private GetMailCountResponse? GetMailCount(GetMailCountRequest req)
    {
        if (Character is not { } c) return null;
        var (owner, guild) = MailBox(c, req.nz);
        return new GetMailCountResponse
        {
            a0f = owner, a0g = req.n0,
            a0h = owner == Guid.Empty || !Mailer.On(world) ? 0 : world.Mail!.MailCount(owner, guild, (MailCategory)req.n0),
        };
    }

    /// <summary>
    /// ReadMail (op 176, zn: 0 guild, 1 the mail id): a mail of the mailbox (else RC <see cref="MailRefused"/>: the client's "not accessible"
    /// view) is marked read and answered (aig, ddp.l 0x180ACE280): 0 id, 1 body, 2 last editor (null = left out, as the captured system mails),
    /// 3-7 the attachments as parallel arrays (Guid, item uniquename, crafter name "", count, quality; empty without any, as captured), 9 true
    /// (both captured mails; meaning open). The client sets the row read and its clip icon from key 3; then NewUnreadMails with the new count.
    /// </summary>
    [Handles(OperationCodes.ReadMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? ReadMail(ReadMailRequest req)
    {
        if (Character is not { } c) return null;
        var (owner, guild) = MailBox(c, req.y5);
        if (owner == Guid.Empty || !Mailer.On(world) || world.Mail!.Mail(req.y6) is not { } mail || mail.Owner != owner || mail.Guild != guild)
            return RefuseMail(c, OperationCodes.ReadMail, $"mail {req.y6} is not in the {(req.y5 ? "guild's" : "character's")} mailbox", new ReadMailResponse());
        if (!mail.Read) world.Mail.MarkMailsRead(owner, guild, [mail.Id], read: true);
        var attachments = world.Mail.Attachments(mail.Id);
        SendResponse(OperationCodes.ReadMail, new ReadMailResponse
        {
            bhf = mail.Id, bhg = mail.Body, bhh = mail.Editor,
            bhi = [.. attachments.Select(a => a.Id)],
            bhj = [.. attachments.Select(a => a.Item.Name)],
            bhk = [.. attachments.Select(_ => "")],
            bhl = [.. attachments.Select(a => a.Item.Count)],
            bhm = [.. attachments.Select(a => (short)Math.Max(1, a.Item.Quality))],
            bho = true,
        }, RequestId, RequestEncrypted);
        if (!mail.Read) Mailer.Notify(world, owner, guild);
        return null;
    }

    /// <summary>
    /// DeleteMail (op 178, ru: 0 the highest id the client listed, 1 guild, 2 the ids to delete, 3 the ids to keep, 4 category): key 2's mails,
    /// or with the client's "select all" (no key 2) every mail of the category up to key 0 but key 3's; never a mail that still has attachments
    /// (the client refuses those too, SPEC §1.2). The empty answer; NewUnreadMails when unread ones went.
    /// </summary>
    [Handles(OperationCodes.DeleteMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? DeleteMail(DeleteMailRequest req)
    {
        if (Character is not { } c) return null;
        var (owner, guild) = MailBox(c, req.jj);
        if (owner == Guid.Empty || !Mailer.On(world)) return RefuseMail(c, OperationCodes.DeleteMail, "no such mailbox");
        var unread = world.Mail!.UnreadMails(owner, guild);
        var deleted = req.jk is { Length: > 0 } ids
            ? world.Mail.DeleteMails(owner, guild, ids, 0, [], MailCategory.All)
            : req.ji > 0 ? world.Mail.DeleteMails(owner, guild, null, req.ji, req.jl ?? [], (MailCategory)req.jm) : 0;
        Log.Info(Name, $"'{c.Name}' deleted {deleted} mail(s) of the {(guild ? "guild's" : "own")} mailbox");
        if (world.Mail.UnreadMails(owner, guild) != unread) Mailer.Notify(world, owner, guild);
        return new GenericResponse();
    }

    /// <summary>MarkMailUnread (op 179, xr: 0 unread (the client sends true), 1 guild, 2 ids): the mails of the mailbox are unread (read with false) again; the empty answer, NewUnreadMails.</summary>
    [Handles(OperationCodes.MarkMailUnread, Status = SessionStatus.InWorld)]
    private AlbionResponse? MarkMailUnread(MarkMailUnreadRequest req)
    {
        if (Character is not { } c) return null;
        var (owner, guild) = MailBox(c, req.vs);
        if (owner == Guid.Empty || !Mailer.On(world)) return RefuseMail(c, OperationCodes.MarkMailUnread, "no such mailbox");
        world.Mail!.MarkMailsRead(owner, guild, req.vt ?? [], read: !req.vr);
        Mailer.Notify(world, owner, guild);
        return new GenericResponse();
    }

    /// <summary>
    /// SendNewMail (op 177, aay: 0 recipient, 1 "not a player" (redundant), 2 to a player, 3 subject, 4 text, 5 to the guild's members, 6 their
    /// rank's role tag, Guid.Empty = all; SPEC §1.2, §4.3). To a player (key 2: by name, any case, offline too; 7080 when none, 7084 when it muted
    /// the sender) as category Player; to a guild (by name, 7081) into the guild's mailbox; to the own guild's members (key 5: ACCESS_GUILD_MAIL,
    /// 7086 for a tag the guild does not have) one copy into each member's mailbox, from the guild. Each costs Mail:Postage silver (7082 when short)
    /// and counts against Mail:DailyLimit per UTC day (7083; the client reads no key with it). A subject or text out of 1..Mail:SubjectMax /
    /// Mail:TextMax is refused (RC 1). RC 0 closes the client's write tab; the answer has no keys then.
    /// </summary>
    [Handles(OperationCodes.SendNewMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? SendNewMail(SendNewMailRequest req)
    {
        if (Player is not { } player) return null;
        var c = player.Character;
        const OperationCodes op = OperationCodes.SendNewMail;
        AlbionResponse Refuse(short code, string why) => RefuseMail(c, op, why, new SendNewMailResponse(), code);
        if (!Mailer.On(world)) return Refuse(MailRefused, "mail is off (Mail:Enabled)");
        var subject = (req.act ?? "").Trim();
        var text = (req.acu ?? "").TrimEnd();
        if (subject.Length is 0 || subject.Length > world.Config.MailSubjectMax || text.Length is 0 || text.Length > world.Config.MailTextMax)
            return Refuse(MailRefused, $"subject of {subject.Length} or text of {text.Length} characters (Mail:SubjectMax {world.Config.MailSubjectMax}, Mail:TextMax {world.Config.MailTextMax})");
        var store = world.Mail!;
        // Who gets it: one personal mailbox, a guild's mailbox, or every member (of a rank) of the sender's guild.
        var recipients = new List<Guid>();
        Guild? guild = null;
        string to;
        if (req.acs)
        {
            if (store.CharacterNamed(req.acq ?? "") is not { } target) return Refuse(MailPlayerNotFound, $"no character '{req.acq}'");
            if (world.Social.Muted(target.Id, c.Id)) return Refuse(MailMutedByReceiver, $"'{target.Name}' muted the sender");
            recipients.Add(target.Id);
            to = $"'{target.Name}'";
        }
        else if (req.acv)
        {
            if (world.Guilds.Of(c.Id) is not { } own) return Refuse(MailGuildNotFound, "the sender is in no guild");
            if (!Mailer.CanReadGuildMail(own.Find(c.Id)!.Role)) return Refuse(MailRefused, "no ACCESS_GUILD_MAIL right in the guild");
            GuildRole? rank = null;
            if (req.acw != Guid.Empty)
            {
                if (Array.IndexOf(own.Tags, req.acw) is not (>= 0 and var tag)) return Refuse(MailRankNotFound, $"guild '{own.Name}' has no role tag {req.acw}");
                rank = GuildRoles.TagRoles[tag];
            }
            recipients.AddRange(own.Members.Where(m => rank is null || m.Role == rank).Select(m => m.Id));
            if (recipients.Count == 0) return Refuse(MailRankNotFound, $"no member of guild '{own.Name}' has role {rank}");
            guild = own;
            to = $"the {(rank is null ? "" : rank + " ")}members of guild '{own.Name}'";
        }
        else
        {
            if (world.Guilds.Find(req.acq) is not { } target) return Refuse(MailGuildNotFound, $"no guild '{req.acq}'");
            guild = target;
            to = $"guild '{target.Name}'";
        }
        var now = DateTime.UtcNow;
        var today = now.Date; // the client's "try again in" counts to the next 00:00 UTC (SPEC §1.4)
        if (world.Config.MailDailyLimit > 0 && store.MailSends(c.Id, today) >= world.Config.MailDailyLimit)
            return Refuse(MailLimitReached, $"{world.Config.MailDailyLimit} mails sent today (Mail:DailyLimit)");
        var postage = checked(world.Config.MailPostage * 10000);
        if (!Wallet.TrySpend(player, postage, "postage")) return Refuse(MailNotEnoughSilver, $"not enough silver for the postage {world.Config.MailPostage}");
        store.AddMailSend(c.Id, now, today);
        world.Saves.SaveNow(c); // the postage is paid now: so is the character
        if (req.acs) Mailer.Send(world, recipients[0], guild: false, MailKind.Normal, MailCategory.Player, c.Name, subject, text, senderId: c.Id);
        else if (req.acv)
            foreach (var id in recipients)
                Mailer.Send(world, id, guild: false, MailKind.Normal, MailCategory.Guild, c.Name, subject, text, senderId: c.Id, fromGuild: true);
        else Mailer.Send(world, guild!.Id, guild: true, MailKind.Normal, MailCategory.Guild, c.Name, subject, text, senderId: c.Id);
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture, $"'{c.Name}' mails {to}: '{subject}' ({text.Length} characters, postage {world.Config.MailPostage})"));
        return new SendNewMailResponse();
    }

    /// <summary>
    /// ClaimAttachmentFromMail (op 180, qt: 0 mail id, 1 all, 2 the attachment's Guid, 3 the bag slot it was dropped on; SPEC §1.2, §4.3): one
    /// attachment into that slot when it is free (else as any item gained), or with "take all" each into the inventory while it fits, by
    /// NewSimpleItem / NewEquipmentItem + InventoryPutItem with its quality, durability and spells. Each attachment row is deleted first and
    /// the item given only when that delete really took it (a second claim or a failed write gives nothing), then the character's snapshot is
    /// queued (<see cref="Persistence.SaveScheduler.SaveNow"/>); a disconnect, relog or restart keeps both in step (the writer is flushed
    /// first), a crash within the writer's moment can lose the claim but never duplicate it, as a market fetch (SPEC Q8). RC 0 when anything
    /// moved, else RC <see cref="MailRefused"/> (the client reads the mail again).
    /// </summary>
    [Handles(OperationCodes.ClaimAttachmentFromMail, Status = SessionStatus.InWorld)]
    private AlbionResponse? ClaimAttachmentFromMail(ClaimAttachmentFromMailRequest req)
    {
        if (Player is not { } player) return null;
        var c = player.Character;
        const OperationCodes op = OperationCodes.ClaimAttachmentFromMail;
        if (!Mailer.On(world) || world.Mail!.Mail(req.fj) is not { Guild: false } mail || mail.Owner != c.Id)
            return RefuseMail(c, op, $"mail {req.fj} is not in the character's mailbox");
        var store = world.Mail;
        var pick = store.Attachments(mail.Id).Where(a => req.fk || a.Id == req.fl).ToList();
        var bag = player.Inventory.Bag;
        var moved = new List<MailAttachment>();
        foreach (var a in pick)
        {
            if (world.Items.Find(a.Item.Name) is not { CanCreate: true } type)
            {
                Log.Warn(Name, $"mail {mail.Id}: attachment {a.Item.Name} is not an item this server can create; it stays in the mail");
                continue;
            }
            var count = a.Item.Count;
            var toSlot = !req.fk && req.fm >= 0 && req.fm < bag.Size && bag[req.fm] is null && bag.Unloaded[req.fm] is null && count <= MaxStack(type);
            if (!toSlot && !Fits(bag, type.IsEquipment ? Enumerable.Repeat((type, 1), count) : [(type, count)], fame: a.Item.Fame))
                continue; // does not fit: it stays in the mail
            bool taken;
            try { taken = store.TakeAttachment(a.Id); }
            catch (Exception e)
            {
                Log.Warn(Name, $"mail {mail.Id}: attachment {a.Id} could not be taken out ({e.Message}); it stays in the mail");
                break;
            }
            if (!taken) continue; // claimed already (another request got there first)
            if (toSlot)
            {
                var item = CreateItem(type, a.Item.Copy(), SpellUsable(player.Progression));
                bag.Put(req.fm, item);
                SendEvent(new InventoryPutItemEvent { ckw = item.Id, ckx = req.fm, cky = bag.Id });
                world.Scripts.OnItemAdded(player, item);
            }
            else
                for (var n = 0; n < (type.IsEquipment ? count : 1); n++)
                    GiveItem(player, type, type.IsEquipment ? 1 : count, a.Item.Quality, a.Item.Copy() with { Count = type.IsEquipment ? 1 : count });
            moved.Add(a);
        }
        if (moved.Count == 0) return RefuseMail(c, op, "nothing of it fits the inventory");
        player.SaveItems();
        world.Saves.SaveNow(c);
        Log.Info(Name, $"'{c.Name}' took {string.Join(", ", moved.Select(a => $"{a.Item.Count} x {a.Item.Name}"))} out of mail {mail.Id}");
        return new GenericResponse();
    }

    private AlbionResponse RefuseMail(Character c, OperationCodes op, string why, AlbionResponse? answer = null, short code = MailRefused)
    {
        Log.Info(Name, $"'{c.Name}' {op} refused ({code}): {why}");
        var r = answer ?? new GenericResponse();
        r.ReturnCode = code;
        return r;
    }
}
