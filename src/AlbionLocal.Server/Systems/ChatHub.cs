using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The chat server's channels and routing (analysis/work/stageJ/chat/SPEC.md §2.3): the 22 globals of the official order (ids 1-22;
/// Staff, id 23, only for GameMaster and Admin accounts), one Area channel ("Local") per map and one Guild channel per guild (ids from
/// 100, the same for a cluster or guild over the whole run, as official), the registered chat connections by character
/// (<see cref="ChatSession"/>), the lines (ChatMessage 73 to every member, the sender included: the client writes no echo) and the
/// whispers (ChatWhisper 75). The client's chat manager keeps one channel per type and joins and leaves by type (SPEC §1.2), so a
/// session's Area and Guild offers are kept in step with the game (<see cref="Refresh"/>: at the register, every Join, a map left, a
/// guild change), always LeftChatChannel 207 and RemovedChatChannel 208 of the old id before the 205 of the new one, and never a 206 for
/// a type the session joined under another id (the client's t.Add would throw). Party chat stays on the game server (Say key 2);
/// Faction, FactionArea and Alliance are not offered (no faction warfare, no alliances, §2.4). Touched only under <see cref="World.Sync"/>
/// (the chat listener shares the lock).
/// </summary>
public sealed class ChatHub
{
    private readonly World world;
    private readonly List<ChatChannel> _globals = [];
    private readonly ChatChannel _staff;
    private readonly Dictionary<int, ChatChannel> _byId = [];
    private readonly Dictionary<string, ChatChannel> _areas = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ChatChannel> _guilds = [];
    private readonly Dictionary<Guid, ChatSession> _sessions = []; // by character
    private readonly Dictionary<(Guid, ChatChannelType), TimeSpan> _lastLine = [];
    private readonly Dictionary<Guid, TimeSpan> _lastWhisper = [];
    private int _nextId = FirstDynamicId;

    /// <summary>The types of the globals in the official 205 (capture 002835): id = index + 1. Global 0 and Trade 1 are offered, joined through a tab only.</summary>
    public static readonly ChatChannelType[] GlobalOrder =
    [
        ChatChannelType.Global, (ChatChannelType)8, (ChatChannelType)9, (ChatChannelType)10, (ChatChannelType)11, (ChatChannelType)13,
        (ChatChannelType)14, (ChatChannelType)12, (ChatChannelType)15, (ChatChannelType)16, (ChatChannelType)17, (ChatChannelType)18,
        (ChatChannelType)19, (ChatChannelType)20, (ChatChannelType)21, (ChatChannelType)22, ChatChannelType.Trade, ChatChannelType.Recruit,
        ChatChannelType.LookingForGroup, ChatChannelType.HardCoreExpedition, ChatChannelType.Help, ChatChannelType.Announcement,
    ];

    /// <summary>The first id of an Area or Guild channel [choice].</summary>
    public const int FirstDynamicId = 100;

    // SendChatMessage / SendWhisperMessage ReturnCodes the client reads (i9 7560-7562, i8 7720-7723; SPEC §1.3).
    public const short LineCooldown = 7561, PlayerNotOnline = 7720, WhisperCooldown = 7722;

    /// <summary>ChatMessage key 3 / ChatWhisper key 4: the client's formatter shows 3 and more as "[GM]" (gmk.b, SPEC §1.3).</summary>
    public const byte RoleGm = 3;

    public ChatHub(World world)
    {
        this.world = world;
        for (var i = 0; i < GlobalOrder.Length; i++) Add(new ChatChannel(i + 1, GlobalOrder[i], "global"));
        _staff = new ChatChannel(GlobalOrder.Length + 1, ChatChannelType.Staff, "global");
        _byId[_staff.Id] = _staff;
        return;

        void Add(ChatChannel c)
        {
            _globals.Add(c);
            _byId[c.Id] = c;
        }
    }

    private ServerConfig Config => world.Config;

    /// <summary>The global channel of <paramref name="type"/> (the 22 of <see cref="GlobalOrder"/>); null for another type.</summary>
    public ChatChannel? Global(ChatChannelType type) => _globals.FirstOrDefault(c => c.Type == type);

    /// <summary>The registered chat connection of <paramref name="character"/>; null when it has none.</summary>
    public ChatSession? SessionOf(Guid character) => _sessions.GetValueOrDefault(character);

    /// <summary>The Area channel of the map of cluster (or instance) <paramref name="cluster"/>, made at its first use.</summary>
    public ChatChannel AreaOf(string cluster) => _areas.TryGetValue(cluster, out var c) ? c : _areas[cluster] = New(ChatChannelType.Area, cluster);

    /// <summary>The Guild channel of <paramref name="guild"/>, made at its first use.</summary>
    public ChatChannel GuildOf(Guid guild) => _guilds.TryGetValue(guild, out var c) ? c : _guilds[guild] = New(ChatChannelType.Guild, guild.ToString());

    private ChatChannel New(ChatChannelType type, string scope)
    {
        var c = new ChatChannel(_nextId++, type, scope);
        _byId[c.Id] = c;
        return c;
    }

    /// <summary>The role byte of an account's lines (Chat:StaffTags: 3 for GameMaster and Admin, else 0).</summary>
    public byte RoleOf(AccountLevel level) => Config.ChatStaffTags && level >= AccountLevel.GameMaster ? RoleGm : (byte)0;

    // ---- connections ----

    /// <summary>
    /// After a RegisterChatPeer's RC 0 (SPEC §2.3 "Register"): the session is the character's chat connection (an older one is closed:
    /// its client reconnected), then one NewChatChannels with the 22 globals in the official order (same ms as the response, as
    /// official), Staff for a staff account, and the character's Area and Guild channels when it plays already (a late or reconnected
    /// chat peer). The client cleared its channel maps when it sent the register, so this session starts with none either.
    /// </summary>
    public void Registered(ChatSession s)
    {
        if (_sessions.TryGetValue(s.CharacterId, out var old) && old != s)
        {
            Drop(old);
            old.Close("the character's client registered a new chat connection");
        }
        Drop(s);
        _sessions[s.CharacterId] = s;
        s.Active = true;
        foreach (var c in _globals) s.Offered[c.Id] = c;
        s.Send(new NewChatChannelsEvent { cxr = [.. _globals.Select(c => (int)c.Type)], cxs = [.. _globals.Select(c => c.Id)] });
        if (s.Staff) Offer(s, _staff);
        Refresh(s);
        Log.Info("chat", $"{s} registered: {s.Offered.Count} channel(s) offered");
    }

    /// <summary>
    /// SelectCharacter of <paramref name="account"/> while one of its chat connections is open (a re-Login of the same client keeps it,
    /// and its token stays, <see cref="AccountStates.Select"/>): a connection registered for another character moves to
    /// <paramref name="c"/>: every offer withdrawn (207 for a joined one, 208), then the steps of <see cref="Registered"/> for the new
    /// character (its 205s; a chat connection that character already had is closed) [choice: the client's c31.i registers again only
    /// when its Connect succeeds, so nothing else would rebind it].
    /// </summary>
    public void Selected(Guid account, Character c)
    {
        foreach (var s in world.Accounts.Sessions(account).OfType<ChatSession>())
        {
            if (s.CharacterId == Guid.Empty || s.CharacterId == c.Id) continue;
            foreach (var offered in s.Offered.Values.ToList()) Withdraw(s, offered);
            if (_sessions.GetValueOrDefault(s.CharacterId) == s) _sessions.Remove(s.CharacterId);
            LoggedOut(s.CharacterId);
            Log.Info("chat", $"{s} moves to '{c.Name}', the account's new selection");
            s.Bind(c);
            Registered(s);
        }
    }

    /// <summary>The chat connection is gone (<see cref="ChatSession.OnDisconnected"/>): off every channel; the character has no chat connection (unless a newer one took over).</summary>
    public void Unregister(ChatSession s)
    {
        Drop(s);
        if (_sessions.GetValueOrDefault(s.CharacterId) == s) _sessions.Remove(s.CharacterId);
    }

    /// <summary>Off every channel and nothing offered (no events: the client has dropped its maps, or the connection is gone).</summary>
    private static void Drop(ChatSession s)
    {
        foreach (var c in s.Joined.Values) c.Members.Remove(s);
        s.Joined.Clear();
        s.Offered.Clear();
    }

    /// <summary>NewChatChannels {[type], [id]} for one channel, now offered to <paramref name="s"/>.</summary>
    private static void Offer(ChatSession s, ChatChannel c)
    {
        s.Offered[c.Id] = c;
        s.Send(new NewChatChannelsEvent { cxr = [(int)c.Type], cxs = [c.Id] });
    }

    /// <summary>
    /// <paramref name="c"/> is no longer offered to <paramref name="s"/>: LeftChatChannel 207 when it was joined (that alone does not
    /// run the client's join sync), then RemovedChatChannel 208 (which does).
    /// </summary>
    private static void Withdraw(ChatSession s, ChatChannel c)
    {
        if (s.Joined.Remove(c.Id))
        {
            c.Members.Remove(s);
            s.Send(new LeftChatChannelEvent { cne = c.Id });
        }
        s.Offered.Remove(c.Id);
        s.Send(new RemovedChatChannelEvent { duw = c.Id });
    }

    /// <summary>
    /// The session's Area and Guild offers as the game has them now: Area = the map of the character's joined player (none while it
    /// changes cluster or is logged out), Guild = its guild. A changed one goes (207 + 208), then the new one is offered (205).
    /// </summary>
    private void Refresh(ChatSession s)
    {
        var map = world.Parties.Online(s.CharacterId)?.Map;
        SetOffer(s, ChatChannelType.Area, map is null ? null : AreaOf(map.Cluster));
        SetOffer(s, ChatChannelType.Guild, world.Guilds.Of(s.CharacterId) is { } g ? GuildOf(g.Id) : null);
    }

    private static void SetOffer(ChatSession s, ChatChannelType type, ChatChannel? wanted)
    {
        var current = s.Offered.Values.FirstOrDefault(c => c.Type == type);
        if (current == wanted) return;
        if (current is not null) Withdraw(s, current);
        if (wanted is not null) Offer(s, wanted);
    }

    // ---- the game's hooks ----

    /// <summary>After every Join's JoinFinished (WorldSession.AfterResponse): the character plays (whispers reach it), its Area of the new map and its Guild channel.</summary>
    public void Joined(Player p)
    {
        if (SessionOf(p.Character.Id) is not { } s) return; // no chat connection (yet: its register sends them then)
        s.Active = true;
        Refresh(s);
    }

    /// <summary>
    /// The player left its map (WorldSession.Leave: a cluster change's commit, before its response, or the end of the connection):
    /// 207 + 208 of the Area at once, before the next Join's 205 (SPEC §2.3 "Leaving a map").
    /// </summary>
    public void LeftMap(Player p)
    {
        if (SessionOf(p.Character.Id) is { } s) Refresh(s);
    }

    /// <summary>
    /// The character logged out (its last game connection closed, not a cluster change): 207 for every joined channel, 208 only for the
    /// Area (official: the cluster channels; LeftMap sent them already), memberships dropped, whispers to it fail with 7720. Its
    /// client has closed its chat peer by then (SPEC §1.2), so this keeps the server's own state right.
    /// </summary>
    public void LoggedOut(Guid character)
    {
        _lastWhisper.Remove(character);
        if (SessionOf(character) is not { } s) return;
        s.Active = false;
        if (s.Offered.Values.FirstOrDefault(c => c.Type == ChatChannelType.Area) is { } area) Withdraw(s, area);
        foreach (var c in s.Joined.Values)
        {
            c.Members.Remove(s);
            s.Send(new LeftChatChannelEvent { cne = c.Id });
        }
        s.Joined.Clear();
    }

    /// <summary>The character's guild changed (created, joined, left, kicked, disbanded; Guilds): its Guild channel follows.</summary>
    public void GuildChanged(Guid character)
    {
        if (SessionOf(character) is { } s) Refresh(s);
    }

    // ---- operations ----

    /// <summary>
    /// JoinChatChannel 191: an offered channel is joined (206 {type, id}; again for one already joined, which the client ignores). A
    /// known channel no longer offered (the 191 crossed our 207/208 on a quick cluster change) gets 206 and at once 207 when the
    /// session has no channel of its type joined, which clears the client's pending join (it would never ask again, SPEC §2.3), and
    /// nothing otherwise (a 206 would throw in the client); an unknown id is ignored.
    /// </summary>
    public void Join(ChatSession s, int id)
    {
        if (s.Offered.TryGetValue(id, out var c))
        {
            if (!s.Joined.ContainsKey(id) && s.Joined.Values.Any(j => j.Type == c.Type)) return; // never while the type is joined elsewhere
            s.Joined[id] = c;
            c.Members.Add(s);
            s.Send(new JoinedChatChannelEvent { cmc = (int)c.Type, cmd = c.Id });
            Log.Debug("chat", $"{s} joins {c} ({c.Members.Count} member(s))");
            return;
        }
        if (!_byId.TryGetValue(id, out var stale))
        {
            Log.Debug("chat", $"{s}: join of unknown channel {id} ignored");
            return;
        }
        if (s.Joined.Values.Any(j => j.Type == stale.Type)) return;
        s.Send(new JoinedChatChannelEvent { cmc = (int)stale.Type, cmd = stale.Id });
        s.Send(new LeftChatChannelEvent { cne = stale.Id });
        Log.Debug("chat", $"{s}: join of {stale}, no longer offered: 206 + 207");
    }

    /// <summary>LeaveChatChannel 192: off the channel when a member, and always 207 (harmless when not joined: it clears the client's pending leave).</summary>
    public void Leave(ChatSession s, int id)
    {
        if (s.Joined.Remove(id, out var c)) c.Members.Remove(s);
        s.Send(new LeftChatChannelEvent { cne = id });
    }

    /// <summary>
    /// A line of <paramref name="s"/> on channel <paramref name="id"/> (189/190, or an item-link line from the game peer): trimmed, empty
    /// dropped, cut to Chat:MaxLineLength; the sender must have joined the channel [choice]; Announcement and Staff take only staff
    /// lines (every client joins 7: a player's line would be in every tab of every player); on a global type a second line within
    /// Chat:GlobalCooldownMs is refused with RC 7561 {0 type} (the client's cooldown text). Then ChatMessage 73 {0 id, 1 sender, 2 text,
    /// 3 role (always sent, as official)} to every member but those who muted the sender (their clients drop such lines too, df9.l),
    /// with Chat:MergeLanguageChannels to the members of all 15 language channels, each with its own id. Null = no response.
    /// </summary>
    public SendChatMessageResponse? Line(ChatSession s, int id, string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return null;
        if (text.Length > Config.ChatMaxLineLength) text = text[..Config.ChatMaxLineLength];
        if (!s.Joined.TryGetValue(id, out var c))
        {
            Log.Debug("chat", $"{s}: a line on channel {id}, which it has not joined: dropped");
            return null;
        }
        if (c.Type is ChatChannelType.Announcement or ChatChannelType.Staff && !s.Staff)
        {
            Log.Info("chat", $"{s}: a line on {c.Type}, which only staff may post on: dropped");
            return null;
        }
        if (c.Global && Config.ChatGlobalCooldownMs > 0)
        {
            var now = world.Clock.Now;
            var key = (s.CharacterId, c.Type);
            if (_lastLine.TryGetValue(key, out var last) && now - last < TimeSpan.FromMilliseconds(Config.ChatGlobalCooldownMs))
                return new SendChatMessageResponse { ReturnCode = LineCooldown, bl1 = (int)c.Type };
            _lastLine[key] = now;
        }
        var heard = 0;
        IEnumerable<ChatChannel> channels = c.Language && Config.ChatMergeLanguageChannels ? _globals.Where(g => g.Language) : [c];
        foreach (var channel in channels)
        {
            var e = new ChatMessageEvent
            {
                bxc = channel.Id, bxd = s.CharacterName, bxe = text,
                Extra = new() { [3] = RoleOf(s.Level) }, // [3] the role byte, always sent (official ByteZero); key 4 (tag) left out as official
            };
            foreach (var m in channel.Members)
                if (!world.Social.Muted(m.CharacterId, s.CharacterId))
                {
                    m.Send(e);
                    heard++;
                }
        }
        Log.Info("chat", $"{s} says a line on {c} to {heard} member(s)");
        Log.Debug("chat", $"line: {text}");
        return null;
    }

    /// <summary>
    /// A whisper of <paramref name="sender"/> to <paramref name="target"/> (by name, ignoring case; 193 from either peer): no active
    /// chat connection of that name, or oneself, 7720; a second whisper within Chat:WhisperCooldownMs 7722; a target that muted the
    /// sender gets nothing and the sender RC 0 [choice]; else ChatWhisper 75 {0 sender, 3 text, 4 role} to the target and RC 0, on
    /// which the sender's client writes its own "to &lt;target&gt;" line. 7723 (the whisper limit) is not used. With Bots:Social:Enabled a world
    /// bot of that name is a target too: RC 0, and maybe its ChatWhisper back later (<see cref="Bots.BotSocial.Whispered"/>).
    /// </summary>
    public short Whisper(Guid sender, string senderName, AccountLevel level, string? target, string? raw)
    {
        var text = (raw ?? "").Trim();
        var name = (target ?? "").Trim();
        var to = _sessions.Values.FirstOrDefault(x => x.Active && string.Equals(x.CharacterName, name, StringComparison.OrdinalIgnoreCase));
        // Bot-social (Bots:Social:Enabled): a world bot of that name takes the whisper (RC 0, the cooldown as for a player) and may answer later.
        if (to is null && world.Bots.Social.Find(Guid.Empty, name) is { } bot)
        {
            if (Config.ChatWhisperCooldownMs > 0)
            {
                var now = world.Clock.Now;
                if (_lastWhisper.TryGetValue(sender, out var last) && now - last < TimeSpan.FromMilliseconds(Config.ChatWhisperCooldownMs)) return WhisperCooldown;
                _lastWhisper[sender] = now;
            }
            if (text.Length == 0) return 0;
            Log.Info("chat", $"'{senderName}' whispers to bot '{bot.Character.Name}'");
            world.Bots.Social.Whispered(sender, senderName, bot);
            return 0;
        }
        if (to is null || to.CharacterId == sender)
        {
            Log.Info("chat", $"'{senderName}' whispers to '{name}': not online");
            return PlayerNotOnline;
        }
        if (Config.ChatWhisperCooldownMs > 0)
        {
            var now = world.Clock.Now;
            if (_lastWhisper.TryGetValue(sender, out var last) && now - last < TimeSpan.FromMilliseconds(Config.ChatWhisperCooldownMs)) return WhisperCooldown;
            _lastWhisper[sender] = now;
        }
        if (text.Length == 0) return 0;
        if (text.Length > Config.ChatMaxLineLength) text = text[..Config.ChatMaxLineLength];
        if (world.Social.Muted(to.CharacterId, sender))
        {
            Log.Info("chat", $"'{senderName}' whispers to {to}, who muted it: not delivered");
            return 0;
        }
        to.Send(new ChatWhisperEvent { bxv = senderName, bxy = text, Extra = new() { [4] = RoleOf(level) } });
        Log.Info("chat", $"'{senderName}' whispers to {to}");
        Log.Debug("chat", $"whisper: {text}");
        return 0;
    }

    // ---- the server's lines ----

    /// <summary>
    /// SPEC §2.2 (.g): a guild line of <paramref name="speaker"/> on its guild's channel to every member that joined it (but those who
    /// muted the speaker); the characters it reached (empty without a chat server), which <see cref="Guilds.GuildLine"/> leaves out of its
    /// SystemMessage, so no member reads the line twice.
    /// </summary>
    public HashSet<Guid> GuildLine(Player speaker, Guild g, string text)
    {
        var reached = new HashSet<Guid>();
        if (!_guilds.TryGetValue(g.Id, out var c)) return reached;
        var level = world.Characters.FindAccount(speaker.Character.Account)?.Level ?? AccountLevel.Player;
        var e = new ChatMessageEvent { bxc = c.Id, bxd = speaker.Character.Name, bxe = text, Extra = new() { [3] = RoleOf(level) } };
        foreach (var m in c.Members)
        {
            reached.Add(m.CharacterId); // a member who muted the speaker counts as reached: it gets no SystemMessage copy either
            if (!world.Social.Muted(m.CharacterId, speaker.Character.Id)) m.Send(e);
        }
        return reached;
    }

    /// <summary>
    /// A server-wide announcement (Chat:Announcements; world bosses and events with AnnounceToAll, the bounties' new day): ChatMessage 73
    /// on the Announcement channel (7, in every tab) to each of <paramref name="readers"/> that joined it, with an empty sender [choice:
    /// client check 7]; returns the readers without it, who get the SystemMessage as before.
    /// </summary>
    public IEnumerable<Player> Announce(string text, IEnumerable<Player> readers)
    {
        if (!Config.ChatAnnouncements || Global(ChatChannelType.Announcement) is not { } c || c.Members.Count == 0) return readers;
        var e = new ChatMessageEvent { bxc = c.Id, bxd = "", bxe = text, Extra = new() { [3] = (byte)0 } };
        var rest = new List<Player>();
        foreach (var p in readers)
            if (SessionOf(p.Character.Id) is { } s && s.Joined.ContainsKey(c.Id)) s.Send(e);
            else rest.Add(p);
        return rest;
    }

    /// <summary>The GM's view (.chat): the registered connections and the channels with members.</summary>
    public string Describe() =>
        $"{_sessions.Count} chat connection(s): {string.Join(", ", _sessions.Values.Select(s => $"{s.CharacterName}{(s.Active ? "" : " (logged out)")} on {s.Joined.Count}"))}; "
        + $"channels with members: {string.Join(", ", _byId.Values.Where(c => c.Members.Count > 0).Select(c => $"{c} {c.Members.Count}"))}";
}
