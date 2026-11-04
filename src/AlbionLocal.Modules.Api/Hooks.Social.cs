namespace AlbionLocal.Modules;

/// <summary>Chat and parties.</summary>
public interface ISocialHooks
{
    /// <summary>A player's Say line (not a command) is about to go out: <see cref="CancellablePlayerArgs.Cancel"/> drops it (the reason goes to the player), <see cref="ChatArgs.Text"/> changes it.</summary>
    IHookRegistration OnBeforeChat(Action<ChatArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A character joined a party (a new party's leader too).</summary>
    IHookRegistration OnPartyJoined(Action<PartyArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A character left a party (left, kicked, disconnected).</summary>
    IHookRegistration OnPartyLeft(Action<PartyArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>A player's Say line about to go out (OnBeforeChat).</summary>
public sealed class ChatArgs(IPlayer player, string text) : CancellablePlayerArgs(player)
{
    /// <summary>The line; set it to change what is said.</summary>
    public string Text { get; set; } = text;
}

/// <summary>How a character joined or left a party.</summary>
public enum PartyChange
{
    /// <summary>It made the party, by inviting the first member.</summary>
    Created,
    /// <summary>It came in by an invitation.</summary>
    Invited,
    /// <summary>It left.</summary>
    Left,
    /// <summary>It was kicked.</summary>
    Kicked,
    /// <summary>It disconnected.</summary>
    Disconnected,
}

/// <summary>A character joined or left a party (OnPartyJoined, OnPartyLeft).</summary>
public sealed class PartyArgs(Guid characterId, string name, IPlayer? player, int partyId, int members, PartyChange reason)
{
    /// <summary>The character's Guid.</summary>
    public Guid CharacterId { get; } = characterId;
    /// <summary>The character's name.</summary>
    public string Name { get; } = name;

    /// <summary>The character's player when it is online.</summary>
    public IPlayer? Player { get; } = player;

    /// <summary>The party's id.</summary>
    public int PartyId { get; } = partyId;

    /// <summary>The party's size after the change.</summary>
    public int Members { get; } = members;

    /// <summary>How it joined or left.</summary>
    public PartyChange Reason { get; } = reason;
}
