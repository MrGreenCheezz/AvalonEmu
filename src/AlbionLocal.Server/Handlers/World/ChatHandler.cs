using AlbionLocal.Photon;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The local chat (Say), the GM commands typed into it (ARCHITECTURE.md §6.6) or into a chat-server channel, the chat lines and
/// whispers with an item link (the client sends those to the game server) and the chat tabs.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// A line typed into the Say input channel or after "/say" (op 194, client class aap: 0 text, 1 emote, 2 party say;
    /// no request id). The client sends only Say to the game server (df9.k(string, bool, bool) 0x180b16230 → c32.i
    /// 0x180a6b910); the other channels go to the chat server peer (<see cref="ChatSession"/>, stageJ). A line starting
    /// with '.' and a letter is a GM command (<see cref="CommandRegistry"/>, at the level of the speaker's account,
    /// <see cref="SpeakerLevel"/>): it is not chat, gets no response, and the replies come back as SystemMessage events.
    /// Any other line keeps the reply the unhandled default gave it, an empty SayResponse, which the client ignores
    /// (df9.k(g7q, short) 0x180b13020 reads only the error codes 7760-7763), and then goes out as ChatSay (event 74, see
    /// <see cref="ChatSay"/>) to the speaker and to every player who knows the speaker (stage 7 SPEC §5): the range is the
    /// visibility window (the official "Surrounding" radius is unknown).
    /// </summary>
    [Handles(OperationCodes.Say, Status = SessionStatus.InWorld)]
    private AlbionResponse? Say(SayRequest req)
    {
        if (!IsCommand(req.aa9))
        {
            _say = ChatSay(req);
            if (_say is not null && Player is { } speaker) world.Plugins.ChatLine(speaker, _say.bxm ?? ""); // the plugins' ChatMessage
            return new SayResponse().FillEmpty();
        }
        RunChatCommand(req.aa9!);
        return null;
    }

    /// <summary>A GM command line: '.' and a letter (Say, and the chat server's lines, stageJ chat SPEC §2.3).</summary>
    internal static bool IsCommand(string? line) => line is ['.', var first, ..] && char.IsLetter(first);

    /// <summary>
    /// Runs the command <paramref name="line"/> of this connection's character at its account's level (<see cref="SpeakerLevel"/>); the
    /// replies come as SystemMessage events here, also for a line that came on the chat peer (<see cref="ChatSession"/>).
    /// </summary>
    internal void RunChatCommand(string line)
    {
        Log.Info(Name, $"'{Character?.Name}' command: {world.Commands.Redact(line)}"); // never a password typed into it
        world.Commands.Execute(new CommandContext(world, Player, SpeakerLevel(), SendCommandReply), line);
    }

    /// <summary>
    /// SendChatMessage (189) on the game connection: the client sends a line with an item link here instead of to the chat peer
    /// (ChatItemSnapshotService.ContainsSnapshotInfoTag, stageJ chat SPEC §1.3): routed as the chat peer's line of the character's chat
    /// connection (<see cref="Systems.ChatHub.Line"/>; dropped without one), a refusal answered here.
    /// </summary>
    [Handles(OperationCodes.SendChatMessage, Status = SessionStatus.InWorld)]
    private AlbionResponse? ItemLinkLine(SendChatMessageRequest req)
    {
        if (IsCommand(req.abk))
        {
            RunChatCommand(req.abk!);
            return null;
        }
        return Character is { } c && world.Chat.SessionOf(c.Id) is { } chat ? world.Chat.Line(chat, req.abj, req.abk) : null;
    }

    /// <summary>
    /// SendWhisperMessage (193) on the game connection (a whisper with an item link): as the chat peer's (<see cref="Systems.ChatHub.Whisper"/>),
    /// and on RC 0 key 2 = the text, which the client writes as its own "to &lt;target&gt;" line for such a whisper (df9.e.c 0x180b279a0).
    /// </summary>
    [Handles(OperationCodes.SendWhisperMessage, Status = SessionStatus.InWorld)]
    private AlbionResponse? ItemLinkWhisper(SendWhisperMessageRequest req)
    {
        if (Character is not { } c) return null;
        var rc = world.Chat.Whisper(c.Id, c.Name, SpeakerLevel(), req.afo, req.afp);
        return new SendWhisperMessageResponse { ReturnCode = rc, bl8 = rc == 0 ? (req.afp ?? "").Trim() : null };
    }

    /// <summary>
    /// The account level a chat command runs at: the stored level of the account the speaking character belongs to (the local
    /// account for a character saved without one); an account that is gone meanwhile counts as Player.
    /// </summary>
    private Persistence.AccountLevel SpeakerLevel()
    {
        var id = Character?.Account is { } a && a != Guid.Empty ? a : world.Characters.Account.Id;
        return world.Characters.FindAccount(id)?.Level ?? Persistence.AccountLevel.Player;
    }

    /// <summary>A Say line waiting for its SayResponse to go out (<see cref="AfterResponse"/>); null when none.</summary>
    private ChatSayEvent? _say;

    /// <summary>Longest Say text relayed (stage 7 SPEC §5); longer lines are cut.</summary>
    private const int MaxSayLength = 1000;

    /// <summary>
    /// ChatSay (event 74, client class aly) for a Say line, null for an empty one (after trimming). Keys as the client
    /// handler df9.k(g7s, short) case 0x4a 0x180b14d40 reads them: 0 the speaker's name (equal to the local name, ignoring
    /// case, it takes the own-message path: the echo is how the speaker sees its line), 1 the text, 2 byte 0 (always sent
    /// officially), 4 the speaker's object id (looked up first: an unknown speaker drops the line, key 7 being false), 5 its
    /// character Guid (with key 0 checked against the ignore list), 6 true for an emote line (Say key 1: SurroundingEmote,
    /// channel 32), 9 true for a party line (Say key 2: Party, 26). Keys 3, 7, 8 are not sent (7 only on the party members' copy).
    /// </summary>
    private ChatSayEvent? ChatSay(SayRequest req)
    {
        if (Player is not { } player) return null;
        var text = (req.aa9 ?? "").Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxSayLength) text = text[..MaxSayLength];
        return new ChatSayEvent
        {
            bxl = player.Character.Name, bxm = text, bxp = player.Id, bxq = player.Character.Id, bxr = req.aba, bxu = req.abb,
            Extra = new() { [2] = (byte)0 }, // [2] byte 0: the serializer leaves a zero out, the official server sends it
        };
    }

    /// <summary>
    /// After the SayResponse: the ChatSay to the speaker, then to its observers, except a party line (key 9), which goes to
    /// the other party members on any map with key 7 (<see cref="Systems.Parties.PartyLine"/>, stageD party SPEC §4) and to
    /// no observer; outside a party it is only echoed [choice]. The text is logged at debug level only.
    /// </summary>
    private void SendChatSay()
    {
        if (_say is not { } say || Player is not { } player) return;
        _say = null;
        SendEvent(say);
        var listeners = say.bxu ? world.Parties.PartyLine(player, say) : player.Observers.Count;
        if (!say.bxu) player.SendToObservers(say);
        Log.Info(Name, $"'{player.Character.Name}' says a line{(say.bxr ? " (emote)" : "")}{(say.bxu ? $" to {listeners} party member(s)" : $" to {listeners} other player(s)")}");
        Log.Debug(Name, $"say: {say.bxm}");
    }

    /// <summary>
    /// UpdateChatSettings (event 217, a0w) with the official default tabs, sent on every Join right after the Join
    /// response (captures: 15/15 Joins, before JoinFinished). The client's tab list starts empty and only this event fills
    /// it (dgb.c(g7s) 0x180b209c0); df9.l 0x180b190a0 keeps a chat line only in tabs whose channel set holds its channel,
    /// so without it every line is lost, SystemMessage replies and the client's own "Entering cluster" lines alike. Keys
    /// 1-3 must be at least as long as key 0 (indexed unchecked); key 4 = 0 leaves the player's tab selection alone. With
    /// Chat:WorldChannel the GENERAL tab also holds Global (0), so every client joins the one world channel (stageJ chat SPEC §2.5).
    /// </summary>
    private void SendChatTabs() => SendEvent(new UpdateChatSettingsEvent
    {
        d2o = ["@CHAT_SETTINGS_TAB_NAME_GENERAL", "@CHAT_SETTINGS_TAB_NAME_HELP", "@CHAT_SETTINGS_TAB_NAME_LOOKING_FOR_GROUP",
               "@CHAT_SETTINGS_TAB_NAME_COMBAT", "@CHAT_SETTINGS_TAB_NAME_RECRUIT", "@CHAT_SETTINGS_TAB_NAME_SOCIAL",
               "@CHAT_SETTINGS_TAB_NAME_FACTION"],
        // ChatChannelType per tab; 33 = System (command replies), 28 in every tab but Combat.
        d2p = [world.Config.ChatWorldChannel ? [23, 0, 27, 31, 28, 38, 33] : [23, 27, 31, 28, 38, 33], [5, 28, 33], [3, 28, 33], [36, 37, 38, 39, 40, 41, 42, 43, 44], [2, 28, 33],
               [26, 24, 25, 28, 33, 38], [29, 30, 28, 33]],
        d2q = [true, true, true, true, true, true, true],
        d2r = [true, true, true, false, true, true, true], // all but Combat
    });

    /// <summary>
    /// A command reply as SystemMessage (event 79, client class azy: 0 text, 1 kind; ci7.xk → df9.k(g7s, short)
    /// 0x180b14d40): kind 0 = df9.ac 0x180b11e20, a chat line from "info" on the System channel; 1 = df9.ab
    /// 0x180b11d20, the same from "error". The text goes through a8c.e 0x18051e9d0, which looks up a text starting with
    /// '@' as a localisation key, so such a line gets a leading space. One event per line; each is also logged.
    /// </summary>
    /// <summary>A command reply to this player from elsewhere (the checker's setup runner, Systems/Checklist): SystemMessage lines as in chat.</summary>
    internal void CommandReply(string text, bool error) => SendCommandReply(text, error);

    private void SendCommandReply(string text, bool error)
    {
        foreach (var line in text.Split('\n'))
        {
            Log.Info(Name, $"{(error ? "error" : "reply")}: {line}");
            SendEvent(new SystemMessageEvent { d0p = line.StartsWith('@') ? " " + line : line, d0q = (byte)(error ? 1 : 0) });
        }
    }
}
