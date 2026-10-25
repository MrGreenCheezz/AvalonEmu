using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The friends list, the friend requests and the mute ("block") list (analysis/work/stageJ/chat/SPEC.md §1.5, §1.6, §2.6): game-server
/// features (ops 266-269, 380, 381; events 293-300, 403 on the game connection), nothing on the chat peer. In memory, loaded at the
/// start from the character database (<see cref="ISocialStore"/>; none in hand-made test worlds) and written there at once. Online =
/// the character has a joined or transferring game connection (from its Join to its logout); presence goes to the online friends
/// (FriendOnlineStatus 297) at the first Join of a login and at the logout. An event for a friend in the middle of a cluster change is
/// not lost: it gets its lists again after its next Join (<see cref="_missed"/>, as the guilds'). A cluster change that never arrives
/// (the client closed or crashed on the loading screen: its Transferring connection's end logs nothing out) is a logout after
/// <see cref="Parties.OfflineGrace"/> without a joined player (<see cref="Update"/>, as the party's). The mutes also filter the chat
/// server's lines and whispers (<see cref="Muted"/>). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Social
{
    /// <summary>A muted player: its character, its name when muted (MutePlayerUpdate key 3) and when (key 5).</summary>
    public sealed record MuteEntry(Guid Id, string Name, DateTime At);

    private readonly World world;
    private readonly ISocialStore? store;
    private readonly Dictionary<Guid, HashSet<Guid>> _friends = [];
    private readonly Dictionary<Guid, Dictionary<Guid, DateTime>> _outgoing = []; // from → to → sent
    private readonly Dictionary<Guid, HashSet<Guid>> _incoming = []; // to → from
    private readonly Dictionary<Guid, List<MuteEntry>> _mutes = []; // by owner
    private readonly Dictionary<Guid, SocialProfile> _profiles = [];
    private readonly HashSet<Guid> _online = [];
    private readonly HashSet<Guid> _missed = [];
    private readonly Dictionary<Guid, TimeSpan> _awaySince = []; // online without a joined player (a cluster change) since
    private readonly HashSet<Guid> _timedOut = []; // logged out by Update: a late arrival is a login again
    private TimeSpan _nextAwayCheck;

    // FriendInvite ReturnCodes (RcOpFriendInvite, SPEC §1.5); 1 = refused without a text [choice].
    public const short Refused = 1, PlayerNotFound = 10640, CantInviteYourself = 10641, AlreadyInvited = 10642, FriendLimitReached = 10644,
        OtherFriendLimitReached = 10645, RequestLimitReached = 10646, OtherRequestLimitReached = 10647, AlreadyFriends = 10648, AddedAsFriend = 10649;

    // MutePlayerUpdate key 1 (mode) and key 2 (the reason of mode 3; c37.c(g7s) throws on any other value, SPEC §1.6).
    public const int MuteFullList = 0, MuteAdded = 1, MuteRemoved = 2, MuteError = 3;
    public const int MuteLimit = 1, MuteUnknown = 2, MuteNotMuted = 3, MuteSelf = 4, MuteAlready = 5;

    public Social(World world, ISocialStore? store)
    {
        this.world = world;
        this.store = store;
        if (store is null) return;
        var data = store.LoadSocial();
        foreach (var (a, b, _) in data.Friends) Befriend(a, b);
        foreach (var (from, to, at) in data.Requests) Request(from, to, at);
        foreach (var (owner, muted, name, at) in data.Mutes) MutesOf(owner).Add(new MuteEntry(muted, name, at));
        foreach (var (id, profile) in data.Profiles) _profiles[id] = profile;
        if (data.Friends.Count + data.Requests.Count + data.Mutes.Count > 0)
            Log.Info("social", $"{data.Friends.Count} friendship(s), {data.Requests.Count} open friend request(s), {data.Mutes.Count} mute(s) loaded");
    }

    private ServerConfig Config => world.Config;

    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    private static HashSet<Guid> Of(Dictionary<Guid, HashSet<Guid>> d, Guid id) => d.TryGetValue(id, out var s) ? s : d[id] = [];

    private Dictionary<Guid, DateTime> OutgoingOf(Guid id) => _outgoing.TryGetValue(id, out var d) ? d : _outgoing[id] = [];

    private List<MuteEntry> MutesOf(Guid id) => _mutes.TryGetValue(id, out var l) ? l : _mutes[id] = [];

    private void Befriend(Guid a, Guid b)
    {
        Of(_friends, a).Add(b);
        Of(_friends, b).Add(a);
    }

    private void Request(Guid from, Guid to, DateTime at)
    {
        OutgoingOf(from)[to] = at;
        Of(_incoming, to).Add(from);
    }

    private void Unrequest(Guid from, Guid to)
    {
        if (_outgoing.TryGetValue(from, out var d)) d.Remove(to);
        if (_incoming.TryGetValue(to, out var s)) s.Remove(from);
    }

    /// <summary>The friends of <paramref name="character"/>.</summary>
    public IReadOnlyCollection<Guid> FriendsOf(Guid character) => _friends.TryGetValue(character, out var s) ? s : [];

    /// <summary>Whether <paramref name="owner"/> muted <paramref name="sender"/> (the chat hub's filter).</summary>
    public bool Muted(Guid owner, Guid sender) => _mutes.TryGetValue(owner, out var l) && l.Exists(m => m.Id == sender);

    /// <summary>The joined player of <paramref name="character"/> on any map; null while it changes cluster or is logged out.</summary>
    private Player? Playing(Guid character) => world.Parties.Online(character);

    private bool Online(Guid character) => _online.Contains(character) || Playing(character) is not null;

    /// <summary>An event to the joined player of <paramref name="character"/>; one online without a player (a cluster change) gets its lists again at its next Join.</summary>
    private void Send(Guid character, AlbionEvent e)
    {
        if (Playing(character) is { } p) p.Session.Send(e);
        else if (_online.Contains(character)) _missed.Add(character);
    }

    private static SocialProfile ProfileOf(Character c) => new(c.Name, c.Template, c.Faction, c.LastLeftAt);

    /// <summary>What the lists show of <paramref name="character"/>: its live character when it plays, else the stored profile (read once from the repository).</summary>
    private SocialProfile? Profile(Guid character)
    {
        if (Playing(character) is { } p) return _profiles[character] = ProfileOf(p.Character);
        if (_profiles.TryGetValue(character, out var known)) return known;
        return world.Characters.Find(character) is { } c ? _profiles[character] = ProfileOf(c) : null;
    }

    /// <summary>FriendInfos key 9 / 296 key 9: now for an online character, else when it last left.</summary>
    private long LastSeen(Guid character, SocialProfile p) => Online(character) ? world.GameNow : p.LastLeftAt;

    /// <summary>Keys 8 (the guild's name, "" = none) and 7 (the alliance: none here) of a friend entry [inference: SPEC §1.5, client check 5].</summary>
    private string GuildName(Guid character) => world.Guilds.Of(character)?.Name ?? "";

    // ---- events (SPEC §1.5, §1.6) ----

    /// <summary>
    /// FriendInfos (295, ao9) of <paramref name="me"/>'s friends: ten parallel arrays, all present and of one length (empty without a
    /// friend): 0 Guid, 1 name, 2 online, 3/4 avatar and ring (0: not stored here), 5 faction, 6 character type (the template
    /// [inference]), 7 alliance "", 8 guild name, 9 last seen.
    /// </summary>
    public FriendInfosEvent FriendInfos(Guid me)
    {
        var rows = FriendsOf(me).Select(id => (id, p: Profile(id))).Where(x => x.p is not null).ToList();
        var n = rows.Count;
        return new FriendInfosEvent
        {
            cbj = [.. rows.Select(x => x.id)], cbk = [.. rows.Select(x => x.p!.Name)], cbl = [.. rows.Select(x => Online(x.id))],
            cbm = new short[n], cbn = new short[n], cbo = [.. rows.Select(x => x.p!.Faction)], cbp = [.. rows.Select(x => (byte)x.p!.Template)],
            cbq = [.. rows.Select(_ => "")], cbr = [.. rows.Select(x => GuildName(x.id))], cbs = [.. rows.Select(x => LastSeen(x.id, x.p!))],
        };
    }

    /// <summary>FriendRequestInfos (294, apf): key 0 true = the requests <paramref name="me"/> got, false (left out, as official) = those it sent; every array present.</summary>
    public FriendRequestInfosEvent RequestInfos(Guid me, bool incoming)
    {
        IEnumerable<Guid> ids = incoming ? _incoming.GetValueOrDefault(me) ?? [] : _outgoing.GetValueOrDefault(me)?.Keys ?? (IEnumerable<Guid>)[];
        var rows = ids.Select(id => (id, p: Profile(id))).Where(x => x.p is not null).ToList();
        var n = rows.Count;
        return new FriendRequestInfosEvent
        {
            ccg = incoming, cch = [.. rows.Select(x => x.id)], cci = [.. rows.Select(x => x.p!.Name)], ccj = [.. rows.Select(_ => "")],
            cck = new short[n], ccl = new short[n], ccm = [.. rows.Select(x => x.p!.Faction)], ccn = [.. rows.Select(x => (byte)x.p!.Template)],
        };
    }

    /// <summary>
    /// MutePlayerUpdate (403, as3): key 1 the mode, 2 the reason (both always sent, explicit zeros as official at login), 3/4/5 parallel
    /// (name, "" [open], when); key 0 (an object id) left out as official.
    /// </summary>
    private static MutePlayerUpdateEvent MuteUpdate(int mode, int reason, IReadOnlyList<MuteEntry> rows) => new()
    {
        cs5 = mode, cs6 = reason, cs7 = [.. rows.Select(m => m.Name)], cs8 = [.. rows.Select(_ => "")], cs9 = [.. rows.Select(m => m.At.Ticks)],
        Extra = new() { [1] = mode, [2] = reason },
    };

    private static MutePlayerUpdateEvent MuteRefused(int reason, string? name = null) => MuteUpdate(MuteError, reason, name is null ? [] : [new MuteEntry(Guid.Empty, name, default)]);

    /// <summary>FriendRequestAnswered (296, apc) about <paramref name="who"/>: its entry and key 10 accepted (a declined one carries only its Guid).</summary>
    private FriendRequestAnsweredEvent Answered(Guid who, bool accepted)
    {
        if (!accepted || Profile(who) is not { } p) return new FriendRequestAnsweredEvent { cbx = who, cb7 = accepted };
        return new FriendRequestAnsweredEvent
        {
            cbx = who, cby = p.Name, cbz = Online(who), cb2 = p.Faction, cb3 = (byte)p.Template, cb4 = "", cb5 = GuildName(who), cb6 = LastSeen(who, p), cb7 = true,
        };
    }

    /// <summary>FriendRequest (293, ape) of <paramref name="from"/>: 0 Guid, 1 name, 2 "", 3/4 avatar 0, 5 faction, 6 character type.</summary>
    private FriendRequestEvent RequestEvent(Guid from, SocialProfile p) => new() { cb9 = from, cca = p.Name, ccb = "", cce = p.Faction, ccf = (byte)p.Template };

    // ---- the game's hooks ----

    /// <summary>
    /// After every Join (WorldSession.AfterResponse, after the guild's events): the character is online. After a login's first Join
    /// (<paramref name="login"/>, not a cluster change's arrival) its mute list (403 mode 0), its received and sent requests (294
    /// true, 294) and its friends (295), all arrays present, as official once per login (SPEC §2.6), and its online friends get 297
    /// {true}; after an arrival the three lists only when it missed an event during the change.
    /// </summary>
    public void Joined(Player p, bool login)
    {
        var me = p.Character.Id;
        login |= _timedOut.Remove(me); // its friends saw it go offline: announced again, with its lists
        _online.Add(me);
        _profiles[me] = ProfileOf(p.Character);
        if (!login && !_missed.Remove(me)) return;
        if (login)
        {
            _missed.Remove(me);
            p.Session.Send(MuteUpdate(MuteFullList, 0, _mutes.GetValueOrDefault(me) ?? []));
        }
        p.Session.Send(RequestInfos(me, incoming: true));
        p.Session.Send(RequestInfos(me, incoming: false));
        p.Session.Send(FriendInfos(me));
        if (!login) return;
        var online = new FriendOnlineStatusEvent { cbt = true, cbu = me, cbv = p.Character.Name };
        foreach (var f in FriendsOf(me)) Send(f, online);
    }

    /// <summary>
    /// The character's last game connection closed (not a cluster change; WorldSession.OnDisconnected under the same guard as the
    /// party and guild): offline, last seen now, and 297 {false} to its online friends.
    /// </summary>
    public void Disconnected(Character c)
    {
        _profiles[c.Id] = ProfileOf(c);
        Offline(c.Id, c.Name, DateTime.UtcNow.Ticks);
    }

    private void Offline(Guid character, string name, long leftAt)
    {
        _online.Remove(character);
        _missed.Remove(character);
        _awaySince.Remove(character);
        if (_profiles.TryGetValue(character, out var p)) _profiles[character] = p with { LastLeftAt = leftAt };
        var offline = new FriendOnlineStatusEvent { cbu = character, cbv = name, Extra = new() { [0] = false } }; // [0] false sent (the serializer leaves it out)
        foreach (var f in FriendsOf(character)) Send(f, offline);
    }

    /// <summary>
    /// The world tick, once a second: a character online without a joined player for <see cref="Parties.OfflineGrace"/> (its cluster
    /// change never arrived) logs out as at the end of its last connection: out of its chat channels (<see cref="ChatHub.LoggedOut"/>),
    /// last seen when its player left, 297 {false} to its online friends. Should it arrive after all, that Join counts as a login's.
    /// </summary>
    public void Update(TimeSpan now)
    {
        if (_online.Count == 0 || now < _nextAwayCheck) return;
        _nextAwayCheck = now + TimeSpan.FromSeconds(1);
        foreach (var id in _online.ToList())
        {
            if (Playing(id) is not null) _awaySince.Remove(id);
            else if (!_awaySince.TryGetValue(id, out var since)) _awaySince[id] = now;
            else if (now - since >= Parties.OfflineGrace)
            {
                var name = Profile(id)?.Name ?? "?";
                Log.Info("social", $"'{name}' has not come back from a cluster change: logged out for its friends and chat");
                world.Chat.LoggedOut(id);
                Offline(id, name, DateTime.UtcNow.Ticks - (now - since).Ticks);
                _timedOut.Add(id);
            }
        }
    }

    /// <summary>
    /// The character's entry changed (its guild: created, joined, left, kicked, disbanded; Guilds) [choice, SPEC §2.6]: FriendUpdated
    /// (300) to its online friends.
    /// </summary>
    public void Updated(Guid character)
    {
        if (!_friends.TryGetValue(character, out var friends) || friends.Count == 0 || Profile(character) is not { } p) return;
        var e = new FriendUpdatedEvent
        {
            cco = character, ccp = p.Name, ccq = Online(character), cct = p.Faction, ccu = (byte)p.Template, ccv = "", ccw = GuildName(character), ccx = LastSeen(character, p),
        };
        foreach (var f in friends) Send(f, e);
    }

    /// <summary>
    /// The character was deleted (DeleteCharacter, the console's account delete; its rows went with it): off every list in memory, its
    /// friends get 299, those it had sent a request get 298, those who had sent it one a declined 296.
    /// </summary>
    public void CharacterDeleted(Guid character)
    {
        if (_friends.Remove(character, out var friends))
            foreach (var f in friends)
            {
                _friends.GetValueOrDefault(f)?.Remove(character);
                Send(f, new FriendRemovedEvent { cbw = character });
            }
        if (_outgoing.Remove(character, out var sent))
            foreach (var to in sent.Keys)
            {
                _incoming.GetValueOrDefault(to)?.Remove(character);
                Send(to, new FriendRequestCanceledEvent { cb8 = character });
            }
        if (_incoming.Remove(character, out var got))
            foreach (var from in got)
            {
                _outgoing.GetValueOrDefault(from)?.Remove(character);
                Send(from, Answered(character, accepted: false));
            }
        _mutes.Remove(character);
        foreach (var list in _mutes.Values) list.RemoveAll(m => m.Id == character);
        _profiles.Remove(character);
        _online.Remove(character);
        _missed.Remove(character);
        _awaySince.Remove(character);
        _timedOut.Remove(character);
    }

    // ---- operations (SPEC §2.6) ----

    /// <summary>
    /// FriendInvite (266, s6 {0 name}): unknown 10640, oneself 10641, friends already 10648, asked already 10642, a full friends list
    /// 10644 (own) / 10645 (the target's). When the target had asked us, both are friends at once: RC 10649 with its entry (the client
    /// adds it and drops its request) and 296 {…, 10 true} to it. Else too many open requests 10646 (sent) / 10647 (the target's
    /// received); otherwise the request is stored, RC 0 with the target's entry, and 293 to the target.
    /// </summary>
    public FriendInviteResponse Invite(Player p, string? rawName)
    {
        var me = p.Character.Id;
        var name = (rawName ?? "").Trim();
        if (name.Length > 0 && world.Characters.Find(name) is null && world.Bots.Social.Find(Guid.Empty, name) is { } bot) return InviteBot(p, bot);
        if (name.Length == 0 || world.Characters.Find(name) is not { } found) return new FriendInviteResponse { ReturnCode = PlayerNotFound };
        var to = found.Id;
        if (to == me) return new FriendInviteResponse { ReturnCode = CantInviteYourself };
        if (FriendsOf(me).Contains(to)) return new FriendInviteResponse { ReturnCode = AlreadyFriends };
        if (_outgoing.GetValueOrDefault(me)?.ContainsKey(to) == true) return new FriendInviteResponse { ReturnCode = AlreadyInvited };
        if (FriendsOf(me).Count >= Config.SocialFriendLimit) return new FriendInviteResponse { ReturnCode = FriendLimitReached };
        if (FriendsOf(to).Count >= Config.SocialFriendLimit) return new FriendInviteResponse { ReturnCode = OtherFriendLimitReached };
        var target = Profile(to) ?? ProfileOf(found);
        var now = UtcNow;
        if (_outgoing.GetValueOrDefault(to)?.ContainsKey(me) == true)
        {
            store?.RemoveFriendRequest(to, me);
            store?.AddFriends(me, to, now);
            Unrequest(to, me);
            Befriend(me, to);
            Send(to, Answered(me, accepted: true));
            Log.Info("social", $"'{p.Character.Name}' and '{target.Name}' are friends (crossed requests)");
            return Entry(to, target, AddedAsFriend);
        }
        if ((_outgoing.GetValueOrDefault(me)?.Count ?? 0) >= Config.SocialFriendRequestLimit) return new FriendInviteResponse { ReturnCode = RequestLimitReached };
        if ((_incoming.GetValueOrDefault(to)?.Count ?? 0) >= Config.SocialFriendRequestLimit) return new FriendInviteResponse { ReturnCode = OtherRequestLimitReached };
        store?.AddFriendRequest(me, to, now);
        Request(me, to, now);
        Send(to, RequestEvent(me, ProfileOf(p.Character)));
        Log.Info("social", $"'{p.Character.Name}' asks '{target.Name}' to be friends");
        return Entry(to, target, 0);
    }

    /// <summary>
    /// Bot-social (Bots:Social:Enabled): a friend request to world bot <paramref name="bot"/>: RC 0 with its entry (online, no guild), its
    /// decline later (<see cref="Bots.BotSocial.FriendInvited"/>); asked already 10642. Nothing is stored: no request row, no profile.
    /// </summary>
    private FriendInviteResponse InviteBot(Player p, Player bot)
    {
        var id = bot.Character.Id;
        if (world.Bots.Social.FriendAsked(p.Character.Id, id)) return new FriendInviteResponse { ReturnCode = AlreadyInvited };
        world.Bots.Social.FriendInvited(p, bot);
        Log.Info("social", $"'{p.Character.Name}' asks bot '{bot.Character.Name}' to be friends");
        return new FriendInviteResponse
        {
            arn = id, aro = bot.Character.Name, arr = bot.Character.Faction, ars = (byte)bot.Character.Template, art = true, aru = "", arv = "", arw = world.GameNow,
        };
    }

    /// <summary>FriendInvite's answer with <paramref name="id"/>'s entry: 0 Guid, 1 name, 2/3 avatar 0, 4 faction, 5 type, 6 online, 7 "", 8 guild, 9 last seen.</summary>
    private FriendInviteResponse Entry(Guid id, SocialProfile p, short rc) => new()
    {
        ReturnCode = rc, arn = id, aro = p.Name, arr = p.Faction, ars = (byte)p.Template, art = Online(id), aru = "", arv = GuildName(id), arw = LastSeen(id, p),
    };

    /// <summary>
    /// FriendAnswerInvitation (267, s4 {0 requester, 1 accept}): no such request RC 1, an accept with either friends list at
    /// Social:FriendLimit RC 1 too (the request stays). Accepted: friends, the response with the
    /// requester's entry and key 10 true, 296 {our entry, 10 true} to the requester; declined: key 0 and key 10 false, and 296 {0 us, 10
    /// false} to the requester. Either way the request is gone (the client drops it on RC 0).
    /// </summary>
    public FriendAnswerInvitationResponse Answer(Player p, Guid from, bool accept)
    {
        var me = p.Character.Id;
        if (_outgoing.GetValueOrDefault(from)?.ContainsKey(me) != true) return new FriendAnswerInvitationResponse { ReturnCode = Refused };
        // A request sent while both lists had room: a full list now refuses the accept and keeps the request (the client drops it
        // only on RC 0), so neither side goes past Social:FriendLimit [choice: 267 has no ReturnCode texts of its own].
        if (accept && (FriendsOf(me).Count >= Config.SocialFriendLimit || FriendsOf(from).Count >= Config.SocialFriendLimit))
            return new FriendAnswerInvitationResponse { ReturnCode = Refused };
        store?.RemoveFriendRequest(from, me);
        Unrequest(from, me);
        if (!accept || Profile(from) is not { } other)
        {
            Send(from, Answered(me, accepted: false));
            Log.Info("social", $"'{p.Character.Name}' declines a friend request");
            return new FriendAnswerInvitationResponse { arc = from, arm = false };
        }
        store?.AddFriends(me, from, UtcNow);
        Befriend(me, from);
        Send(from, Answered(me, accepted: true));
        Log.Info("social", $"'{p.Character.Name}' and '{other.Name}' are friends");
        return new FriendAnswerInvitationResponse
        {
            arc = from, ard = other.Name, are = Online(from), arh = other.Faction, ari = (byte)other.Template, arj = "", ark = GuildName(from), arl = LastSeen(from, other), arm = true,
        };
    }

    /// <summary>FriendCancelnvitation (268, s5 {0 target}): our request to it is gone and it gets 298 {us}; the answer is the empty RC 0 either way (SPEC §1.5).</summary>
    public void Cancel(Player p, Guid to)
    {
        var me = p.Character.Id;
        if (_outgoing.GetValueOrDefault(me)?.ContainsKey(to) != true) return;
        store?.RemoveFriendRequest(me, to);
        Unrequest(me, to);
        Send(to, new FriendRequestCanceledEvent { cb8 = me });
        Log.Info("social", $"'{p.Character.Name}' takes back a friend request");
    }

    /// <summary>FriendRemove (269, s7 {0 friend}): no longer friends on either side; the other one gets 299 {us}; RC 0 either way.</summary>
    public void Remove(Player p, Guid other)
    {
        var me = p.Character.Id;
        if (!FriendsOf(me).Contains(other)) return;
        store?.RemoveFriends(me, other);
        _friends.GetValueOrDefault(me)?.Remove(other);
        _friends.GetValueOrDefault(other)?.Remove(me);
        Send(other, new FriendRemovedEvent { cbw = me });
        Log.Info("social", $"'{p.Character.Name}' removes a friend");
    }

    /// <summary>
    /// AddPlayerToMuteList (380, om {0 name}; no response): oneself 403 {3, 4}, unknown {3, 2}, muted already {3, 5}, Social:MuteLimit
    /// reached {3, 1, [name]}; else stored and 403 {1, [name], [""], [now]} (the client's "muted" line). The chat hub then drops the
    /// player's lines and whispers to <paramref name="p"/>.
    /// </summary>
    public MutePlayerUpdateEvent Mute(Player p, string? rawName)
    {
        var me = p.Character.Id;
        var name = (rawName ?? "").Trim();
        if (string.Equals(name, p.Character.Name, StringComparison.OrdinalIgnoreCase)) return MuteRefused(MuteSelf);
        if (name.Length == 0 || world.Characters.Find(name) is not { } c) return MuteRefused(MuteUnknown);
        if (c.Id == me) return MuteRefused(MuteSelf);
        var list = MutesOf(me);
        if (list.Exists(m => m.Id == c.Id)) return MuteRefused(MuteAlready);
        if (list.Count >= Config.SocialMuteLimit) return MuteRefused(MuteLimit, c.Name);
        var mute = new MuteEntry(c.Id, c.Name, UtcNow);
        store?.AddMute(me, c.Id, c.Name, mute.At);
        list.Add(mute);
        Log.Info("social", $"'{p.Character.Name}' mutes '{c.Name}'");
        return MuteUpdate(MuteAdded, 0, [mute]);
    }

    /// <summary>RemovePlayerFromMuteList (381, zw {0 name}; no response): not muted 403 {3, 3}; else deleted and 403 {2, [name]} (the client's "unmuted" line).</summary>
    public MutePlayerUpdateEvent Unmute(Player p, string? rawName)
    {
        var me = p.Character.Id;
        var name = (rawName ?? "").Trim();
        if (_mutes.GetValueOrDefault(me)?.Find(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } mute) return MuteRefused(MuteNotMuted);
        store?.RemoveMute(me, mute.Id);
        _mutes[me].Remove(mute);
        Log.Info("social", $"'{p.Character.Name}' unmutes '{mute.Name}'");
        return MuteUpdate(MuteRemoved, 0, [mute]);
    }

    /// <summary>The GM's view (.friends): a character's friends with their online state, its open requests and mutes.</summary>
    public string Describe(Guid character, string name) =>
        $"'{name}': {FriendsOf(character).Count} friend(s): {string.Join(", ", FriendsOf(character).Select(f => $"{Profile(f)?.Name ?? "?"}{(Online(f) ? " (online)" : "")}"))}; "
        + $"requests sent {_outgoing.GetValueOrDefault(character)?.Count ?? 0}, received {_incoming.GetValueOrDefault(character)?.Count ?? 0}; "
        + $"muted {(_mutes.GetValueOrDefault(character) is { Count: > 0 } m ? string.Join(", ", m.Select(x => x.Name)) : "none")}";
}
