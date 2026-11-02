namespace AlbionLocal.Server.Game;

/// <summary>Prospective native popup history; older account JSON starts empty, without inventing past views.</summary>
public sealed class EventBasedPopupState
{
    public long LoginCount { get; set; }
    public Dictionary<string, List<EventBasedPopupSeen>> Seen { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Login keys 90–93 restore the time, character, login ordinal and popup-queue ordinal.
/// Type and ClientFlag retain the additional request metadata; neither changes the authored frequency rules.</summary>
public sealed record EventBasedPopupSeen(long At, Guid Character, long Login, long Queue, int Type, bool ClientFlag);
