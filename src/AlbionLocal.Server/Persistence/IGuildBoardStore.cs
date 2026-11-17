using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Persistence;

/// <summary>
/// The guild window's own state beyond the guild row (analysis/work/stageR/guild/SPEC.md §4): each guild's profile (recruitment info,
/// codex, welcome mail, optional stats, upkeep flag), the open applications and the history logs. Saved at once, one statement each;
/// the rows go with their guild (ON DELETE CASCADE). The character database implements it next to <see cref="IGuildStore"/>.
/// </summary>
public interface IGuildBoardStore
{
    /// <summary>Every saved profile by guild.</summary>
    IReadOnlyDictionary<Guid, GuildProfile> LoadGuildProfiles();

    void SaveGuildProfile(Guid guild, GuildProfile profile);

    void AddGuildApplication(GuildApplication application);

    /// <summary>The application living in mail <paramref name="mail"/>; null when none (or its mail was deleted).</summary>
    GuildApplication? GuildApplication(long mail);

    /// <summary>The open application of <paramref name="character"/> to <paramref name="guild"/> whose mail still exists; null = none.</summary>
    GuildApplication? GuildApplicationOf(Guid guild, Guid character);

    void DeleteGuildApplication(long mail);

    void AddGuildLog(Guid guild, GuildLogEntry entry);

    /// <summary>The guild's log rows of <paramref name="type"/> since <paramref name="since"/> (UTC ticks), newest first.</summary>
    IReadOnlyList<GuildLogEntry> GuildLogs(Guid guild, int type, long since);

    /// <summary>Drops log rows older than <paramref name="before"/> (UTC ticks); returns how many.</summary>
    int PurgeGuildLogs(long before);
}
