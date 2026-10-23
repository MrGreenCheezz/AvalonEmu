using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// The mailboxes (stageJ mail SPEC §4.1, §4.2): a character's and a guild's mails with their item attachments, and the player mails each
/// character sent (the daily limit). Saved at once, each call one statement or one transaction, like the market (<see cref="IMarketStore"/>);
/// the game keeps nothing of it in memory. A mailbox is named by its owner's Guid and whether that is a guild.
/// </summary>
public interface IMailStore
{
    /// <summary>Page <paramref name="page"/> (from 0) of <paramref name="size"/> mails of the mailbox, newest first; <see cref="MailCategory.All"/> = every category.</summary>
    IReadOnlyList<Mail> MailPage(Guid owner, bool guild, MailCategory category, int page, int size);

    /// <summary>How many mails of <paramref name="category"/> (<see cref="MailCategory.All"/> = all) the mailbox holds.</summary>
    long MailCount(Guid owner, bool guild, MailCategory category);

    /// <summary>The unread mails of the mailbox (NewUnreadMails key 1).</summary>
    int UnreadMails(Guid owner, bool guild);

    Mail? Mail(long id);

    /// <summary>The attachments still in mail <paramref name="mail"/>, in the order they were added.</summary>
    IReadOnlyList<MailAttachment> Attachments(long mail);

    /// <summary>Stores <paramref name="mail"/> (its Id, Attachments and Read are ignored: a new mail is unread) with <paramref name="items"/> as its attachments; returns its id.</summary>
    long AddMail(Mail mail, IReadOnlyList<InventoryItem>? items = null);

    /// <summary>Sets the read flag of the mailbox's mails of <paramref name="ids"/> (others' ids are skipped).</summary>
    void MarkMailsRead(Guid owner, bool guild, IEnumerable<long> ids, bool read);

    /// <summary>
    /// Deletes mails of the mailbox, never one that still has attachments: those of <paramref name="ids"/>, or (<paramref name="ids"/> null,
    /// the client's "select all") every mail of <paramref name="category"/> with an id up to <paramref name="upper"/> except <paramref name="keep"/>.
    /// Returns how many went.
    /// </summary>
    int DeleteMails(Guid owner, bool guild, IReadOnlyCollection<long>? ids, long upper, IReadOnlyCollection<long> keep, MailCategory category);

    /// <summary>Takes attachment <paramref name="id"/> out of its mail; false when it is no longer there (claimed already).</summary>
    bool TakeAttachment(Guid id);

    /// <summary>Deletes the mails past their end that have no attachments (an attachment is never lost); returns how many.</summary>
    int PurgeMails(DateTime now);

    /// <summary>Deletes the oldest read mails without attachments of the mailbox while it holds more than <paramref name="max"/>.</summary>
    void TrimMailbox(Guid owner, bool guild, int max);

    /// <summary>The player mails <paramref name="character"/> has sent since <paramref name="since"/>.</summary>
    int MailSends(Guid character, DateTime since);

    /// <summary>Counts one player mail of <paramref name="character"/> at <paramref name="at"/> (and drops its counts of before <paramref name="keepFrom"/>).</summary>
    void AddMailSend(Guid character, DateTime at, DateTime keepFrom);

    /// <summary>The character of <paramref name="name"/> (any case, also offline): its Guid and its name as stored; null = none.</summary>
    (Guid Id, string Name)? CharacterNamed(string name);
}
