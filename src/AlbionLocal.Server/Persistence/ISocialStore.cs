namespace AlbionLocal.Server.Persistence;

/// <summary>What the friends list, the friend requests and the mute lists of one character name of another (stageJ chat SPEC §2.7).</summary>
public sealed record SocialProfile(string Name, int Template, byte Faction, long LastLeftAt);

/// <summary>Every row of the social tables, with a profile of each character they name (names from the character table).</summary>
public sealed record SocialData(
    IReadOnlyList<(Guid A, Guid B, DateTime Since)> Friends,
    IReadOnlyList<(Guid From, Guid To, DateTime At)> Requests,
    IReadOnlyList<(Guid Owner, Guid Muted, string Name, DateTime At)> Mutes,
    IReadOnlyDictionary<Guid, SocialProfile> Profiles);

/// <summary>
/// The friends, the open friend requests and the mute lists (stageJ chat SPEC §2.6, §2.7), saved at once like the guilds, not through
/// <see cref="SaveScheduler"/>. The game keeps them in memory (<see cref="Systems.Social"/>) and writes each change here; a deleted
/// character's rows go with it (ON DELETE CASCADE, both directions).
/// </summary>
public interface ISocialStore
{
    SocialData LoadSocial();

    /// <summary>Stores the friendship of <paramref name="a"/> and <paramref name="b"/> (one row per pair, whatever the order).</summary>
    void AddFriends(Guid a, Guid b, DateTime since);

    void RemoveFriends(Guid a, Guid b);

    void AddFriendRequest(Guid from, Guid to, DateTime at);

    void RemoveFriendRequest(Guid from, Guid to);

    void AddMute(Guid owner, Guid muted, string name, DateTime at);

    void RemoveMute(Guid owner, Guid muted);
}
