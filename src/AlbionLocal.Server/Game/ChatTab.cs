using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbionLocal.Server.Game;

/// <summary>One character's chat tab. Channels are ChatChannelType values, not chat-server channel IDs.</summary>
public sealed record ChatTab(string Name, int[] Channels, bool ShowTimeStamps, bool ShowChannelNames)
{
    public ChatTab Copy() => this with { Channels = [.. Channels] };
}

/// <summary>Chat UI limits from 345319 chatsettings.bin; JSON is internal storage, never an op 127 payload.</summary>
public static class ChatSettings
{
    public const int MaxTabs = 10;
    public const int MaxNameLength = 10;
    public const int ChannelCount = 45;
    public const int MaxJsonLength = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // Preserve Avalon's existing defaults until the player first customizes them. Global remains a server option.
    public static ChatTab[] Defaults(bool worldChannel) =>
    [
        new("@CHAT_SETTINGS_TAB_NAME_GENERAL", worldChannel ? [23, 0, 27, 31, 28, 38, 33] : [23, 27, 31, 28, 38, 33], true, true),
        new("@CHAT_SETTINGS_TAB_NAME_HELP", [5, 28, 33], true, true),
        new("@CHAT_SETTINGS_TAB_NAME_LOOKING_FOR_GROUP", [3, 28, 33], true, true),
        new("@CHAT_SETTINGS_TAB_NAME_COMBAT", [36, 37, 38, 39, 40, 41, 42, 43, 44], true, false),
        new("@CHAT_SETTINGS_TAB_NAME_RECRUIT", [2, 28, 33], true, true),
        new("@CHAT_SETTINGS_TAB_NAME_SOCIAL", [26, 24, 25, 28, 33, 38], true, true),
        new("@CHAT_SETTINGS_TAB_NAME_FACTION", [29, 30, 28, 33], true, true),
    ];

    public static bool ValidCustomName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxNameLength && !name.StartsWith('@')
        && !name.Any(c => char.IsControl(c) || c is '<' or '>' or '[' or ']' or '\\');

    public static bool ValidChannels(int[]? channels) => channels is { Length: <= ChannelCount }
        && channels.All(c => c is >= 0 and < ChannelCount);

    /// <summary>A malformed saved preference falls back to defaults; it cannot inject UI tags or unbounded arrays.</summary>
    public static ChatTab[]? Read(string json)
    {
        if (json.Length > MaxJsonLength) return null;
        try
        {
            var tabs = JsonSerializer.Deserialize<ChatTab[]>(json, JsonOptions);
            if (tabs is null || tabs.Length > MaxTabs) return null;
            var defaults = Defaults(false).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tab in tabs)
                if (tab is null || (!ValidCustomName(tab.Name) && !defaults.Contains(tab.Name))
                    || !names.Add(tab.Name) || !ValidChannels(tab.Channels)
                    || tab.Channels.Distinct().Count() != tab.Channels.Length) return null;
            return tabs;
        }
        catch (JsonException) { return null; }
    }
}
