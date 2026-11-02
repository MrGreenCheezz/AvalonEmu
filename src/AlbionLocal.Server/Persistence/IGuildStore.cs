using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// The guilds (stageH guilds SPEC §4), saved at once like the market (a guild changes rarely: a create, a join, a rank), not
/// through <see cref="SaveScheduler"/>. The game keeps them in memory (<see cref="Systems.Guilds"/>) and writes each change here.
/// </summary>
public interface IGuildStore
{
    /// <summary>Every guild with its members in join order (names from the character table).</summary>
    IReadOnlyList<Guild> LoadGuilds();

    /// <summary>Stores <paramref name="guild"/> (new or changed) with all its member rows, in one transaction.</summary>
    void SaveGuild(Guild guild);

    /// <summary>Stores only the account, the experience, the contracts and the guild goals of <paramref name="guild"/> (the frequent changes; no member rows).</summary>
    void SaveGuildProgress(Guild guild);

    /// <summary>Stores shared loadouts only; simple stores may reuse their atomic guild save.</summary>
    void SaveGuildLoadouts(Guild guild) => SaveGuild(guild);

    /// <summary>Deletes the guild and its member rows.</summary>
    void DeleteGuild(Guid id);

    /// <summary>When <paramref name="character"/> last left (or was kicked from) a guild; null = never.</summary>
    DateTime? GuildLeftAt(Guid character);

    void SetGuildLeftAt(Guid character, DateTime at);
}

/// <summary>An atomic transfer between a character wallet and its guild account, including the account ledger.</summary>
public interface IGuildAccountStore
{
    /// <summary>
    /// Stores the prepared character, guild balance and progress in one transaction. False when the character or its
    /// membership no longer exists. Call through SaveScheduler.Commit before publishing any of the prepared state.
    /// </summary>
    bool SaveGuildAccountTransfer(Guid guild, long silver, GuildProgress progress, Character character);
}
