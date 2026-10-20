using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A party member as the party events carry it (stageD party SPEC §7): the character Guid, its name, the avatar frame byte
/// (PartyJoined key 10, the character's template [H]) and its role (op 234). Avatar, ring and flagging are 0 (the server has
/// no avatars and no flagging yet), presence is always true (no away state, SPEC §8).
/// </summary>
public sealed class PartyMember(Guid id, string name, byte frame)
{
    public Guid Id { get; } = id;

    public string Name { get; } = name;

    public byte Frame { get; } = frame;

    /// <summary>The role of op 234 (an itemroles index; -1 special), PartyJoined key 15 / PartyPlayerJoined key 9.</summary>
    public int Role { get; set; }

    /// <summary>When the member was first seen without a joined player (<see cref="Parties.Update"/>); null while it plays.</summary>
    public TimeSpan? OfflineSince { get; set; }

    /// <summary>A party event went out while it had no joined player (a cluster change under way): its next Join gets PartyJoined again.</summary>
    public bool Missed { get; set; }

    /// <summary>StageN companions: the character Guid of the player who hired this member (a companion bot); null for a player.</summary>
    public Guid? Owner { get; init; }

    /// <summary>Whether this member is a hired companion (<see cref="Owner"/> set): no client, never offline, no fame share.</summary>
    public bool Companion => Owner is not null;
}

/// <summary>
/// A party (stageD party SPEC §7): its id (a counter from 1, never 0, never reused while the server runs, so a loot bag's party
/// id never comes to mean another party), the leader, the members in join order (the display order of PartyJoined), the loot
/// mode (stored and broadcast, looted as FreeLoot: §6) and the six marks. Not saved: a member who logs out leaves (flow 3).
/// </summary>
public sealed class Party(int id, Guid leader)
{
    public int Id { get; } = id;

    public Guid Leader { get; set; } = leader;

    public List<PartyMember> Members { get; } = [];

    /// <summary>PartyLootModus: 0 FreeLoot, 1 PartyLoot (not implemented, §6).</summary>
    public int LootMode { get; set; }

    /// <summary>PartyMarkedObjectsUpdated key 1: string[6], "" for an empty slot (a null entry would not serialize).</summary>
    public string[] Marks { get; } = ["", "", "", "", "", ""];

    public PartyMember? Find(Guid id) => Members.FirstOrDefault(m => m.Id == id);

    /// <summary>The member called <paramref name="name"/> (case-insensitive); null when none.</summary>
    public PartyMember? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : Members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>An open party invitation (SPEC §7): at most one per invitee, as the client keeps one (a second 228 overwrites it).</summary>
public sealed record PendingInvite(Guid Inviter, string InviterName, Guid Invitee, string InviteeName, TimeSpan Expires);

/// <summary>
/// Parties and invitations of the running server (analysis/work/stageD/party/SPEC.md §1, §2, §7), by character Guid: a party
/// survives cluster changes (the client keeps its party manager c3g over them, so nothing is resent, flow 9) and ends for a
/// member when it leaves, is kicked or disconnects (flow 3). The events go to each member's joined player on any map
/// (<see cref="Online"/>); a member without one (between the two connections of a cluster change) misses them and gets the
/// party's PartyJoined on arrival instead (<see cref="PartyMember.Missed"/>), or PartyDisbanded when it was taken out
/// meanwhile. One whose change never comes back (the client closed during the load) is taken out after
/// <see cref="OfflineGrace"/>. Missed party chat is not repeated; an invitation to a player in the middle of a change, or
/// one accepted from a solo inviter in the middle of one, is answered as if it were gone (9000, 239 busy) [choice].
/// Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Parties(World world)
{
    private readonly Dictionary<Guid, Party> _byMember = [];
    private readonly Dictionary<Guid, PendingInvite> _invites = []; // by invitee
    private readonly List<Party> _parties = [];
    private readonly Dictionary<Guid, int> _droppedAway = []; // character → the party it was taken out of while it had no joined player
    private TimeSpan _nextOfflineCheck;
    private int _lastId;

    // ReturnCodes of the client's texts (f8k.i.e 0x181c80f30, f8l.b/a_1/c; SPEC §1). [choice] where the SPEC says so.
    public const short PlayerNotFound = 9000, TargetInParty = 9002, TargetWaiting = 9004, NotLeader = 9007, PendingInvitation = 9009,
        PartyFull = 9012, InvitationGone = 9080, LeaveFailed = 9161, KickNotFound = 9200, KickNoParty = 9201, KickNotLeader = 9202,
        LeaderNotFound = 9240, LeaderNoParty = 9241, LeaderNotLeader = 9242;

    // PartyAnswerDeclineReason.
    public const byte DeclinedViaUi = 0, TimedOut = 1, Busy = 4, NoReason = 5;

    /// <summary>The client's countdown (gamedata AcceptPartyInviteCountDownInSeconds 60) plus 5 s grace: then the invitation expires (flow 1).</summary>
    public static readonly TimeSpan InviteTime = TimeSpan.FromSeconds(65);

    /// <summary>How long a member may be without a joined player (a cluster change that never arrives) before it is taken out [choice].</summary>
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromMinutes(2);

    /// <summary>Config Party:MaxSize (20 [choice]: GroupBonus goes to 20).</summary>
    public int MaxSize => world.Config.PartyMaxSize;

    /// <summary>The party of character <paramref name="character"/>; null when it is in none.</summary>
    public Party? Of(Guid character) => _byMember.GetValueOrDefault(character);

    /// <summary>The open invitation to <paramref name="invitee"/>; null when none.</summary>
    public PendingInvite? InviteOf(Guid invitee) => _invites.GetValueOrDefault(invitee);

    /// <summary>
    /// The joined player of character <paramref name="character"/> on any map; with two (a reconnect while the old connection
    /// waits for its timeout) the newest Join (the higher object id). Null when it plays nowhere.
    /// </summary>
    public Player? Online(Guid character)
    {
        Player? found = null;
        foreach (var map in world.Maps.Maps)
            foreach (var p in map.Players.Values)
                if (p.Character.Id == character && (found is null || p.Id > found.Id)) found = p;
        return found;
    }

    /// <summary>The joined player called <paramref name="name"/> (case-insensitive), the newest one; null when none.</summary>
    private Player? OnlineByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        Player? found = null;
        foreach (var map in world.Maps.Maps)
            foreach (var p in map.Players.Values)
                if (string.Equals(p.Character.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) && (found is null || p.Id > found.Id)) found = p;
        return found;
    }

    private static PartyMember MemberOf(Player p) => new(p.Character.Id, p.Character.Name ?? "", (byte)p.Character.Template);

    /// <summary>
    /// An event to every member of <paramref name="party"/> that plays (<see cref="Online"/>), but <paramref name="except"/>; one
    /// that does not is marked <see cref="PartyMember.Missed"/>.
    /// </summary>
    private void SendTo(Party party, AlbionEvent e, Guid? except = null)
    {
        foreach (var m in party.Members)
            if (m.Id == except || m.Companion) continue; // stageN: a companion has no client
            else if (Online(m.Id) is { } p) p.Session.Send(e);
            else m.Missed = true;
    }

    // ---- events (SPEC §2) ----

    /// <summary>
    /// PartyJoined (230, awg) of <paramref name="party"/> for one member's client: 0 id, 2 loot mode, 4 leader, keys 8-16 one entry
    /// per member incl. the receiver, all non-null and of one length (c3g.k case 0xe6; a null array skips the object link and
    /// the chat line): 8 Guids, 9 names, 10 frame bytes, 11 avatar / 12 ring (Int16 0), 13 flagging (packed byte 0), 14 Int32 0,
    /// 15 roles (Int16), 16 present (true). Key 1 stays false (true would turn Leave into cast 0x15, SPEC §2), 3/5-7 absent.
    /// </summary>
    public static PartyJoinedEvent Joined(Party party)
    {
        var n = party.Members.Count;
        return new PartyJoinedEvent
        {
            dif = party.Id, dih = party.LootMode, dij = party.Leader,
            din = [.. party.Members.Select(m => m.Id)],                 // [8]
            dio = [.. party.Members.Select(m => m.Name)],               // [9]
            dip = [.. party.Members.Select(m => m.Frame)],              // [10] byte[]
            diq = new short[n], dir = new short[n],                     // [11] avatar, [12] ring
            dis = new byte[n],                                          // [13] FlaggingStatus[] (packed)
            dit = new int[n],                                           // [14]
            diu = [.. party.Members.Select(m => (short)m.Role)],        // [15]
            div = [.. Enumerable.Repeat(true, n)],                      // [16] present
        };
    }

    /// <summary>PartyPlayerJoined (232, awr) of <paramref name="m"/>: 0 party id, 1 Guid, 2 name, 3 frame, 8 present, 9 role (4-7 avatar, ring, flagging, int: 0).</summary>
    private static PartyPlayerJoinedEvent PlayerJoined(Party party, PartyMember m) =>
        new() { dj9 = party.Id, dka = m.Id, dkb = m.Name, dkc = m.Frame, dkh = true, dki = (short)m.Role };

    /// <summary>
    /// PartyInvitation (228, awc) from <paramref name="inviter"/>: 0 its Guid, 1 name, 5 frame; 2-4 and 7-11 0, 6 no guild. Keys 12-16
    /// (the inviter's party for the flyout) only for a party of two or more; left out for a solo inviter.
    /// </summary>
    private static PartyInvitationEvent Invitation(Player inviter, Party? party)
    {
        var e = new PartyInvitationEvent { dhl = inviter.Character.Id, dhm = inviter.Character.Name, dhq = (byte)inviter.Character.Template };
        if (party is { Members.Count: >= 2 } p)
        {
            var n = p.Members.Count;
            e.dhx = [.. p.Members.Select(m => m.Id)];
            e.dhy = [.. p.Members.Select(m => m.Name)];
            e.dhz = new short[n];
            e.dh0 = new byte[n];
            e.dh1 = [.. p.Members.Select(m => m.Frame)];
        }
        return e;
    }

    private static PartyInvitationAnswerEvent Answer(string invitee, bool accepted, bool auto, byte reason) =>
        new() { dh2 = invitee, dh3 = accepted, dh4 = auto, dh5 = reason };

    // ---- flows (SPEC §7) ----

    /// <summary>
    /// Flow 1, PartyInvitePlayer (225): the target by Guid, else by name; checked in the SPEC's order (missing or self 9000, in a
    /// party 9002, inviter in a party but not its leader 9007, the target's open invitation from this inviter 9009 or from
    /// another one 9004 [choice], party + its open invitations ≥ <see cref="MaxSize"/> 9012). On 0 the target gets
    /// PartyInvitation and the invitation runs for <see cref="InviteTime"/>. <paramref name="targetName"/> = response key 0.
    /// </summary>
    public short Invite(Player inviter, Guid target, string? name, out string? targetName)
    {
        targetName = null;
        var to = target != Guid.Empty ? Online(target) : OnlineByName(name);
        if (to is null || to.Character.Id == inviter.Character.Id) return PlayerNotFound;
        targetName = to.Character.Name;
        var me = inviter.Character.Id;
        var party = Of(me);
        if (Of(to.Character.Id) is not null) return TargetInParty;
        if (party is not null && party.Leader != me) return NotLeader;
        if (InviteOf(to.Character.Id) is { } open) return open.Inviter == me ? PendingInvitation : TargetWaiting;
        var size = (party?.Members.Count ?? 1) + _invites.Values.Count(i => party is null ? i.Inviter == me : party.Find(i.Inviter) is not null);
        if (size >= MaxSize) return PartyFull;
        _invites[to.Character.Id] = new PendingInvite(me, inviter.Character.Name ?? "", to.Character.Id, to.Character.Name ?? "", world.Clock.Now + InviteTime);
        to.Session.Send(Invitation(inviter, party));
        Log.Info("party", $"'{inviter.Character.Name}' invites '{to.Character.Name}'{(party is null ? "" : $" into party {party.Id}")}");
        return 0;
    }

    /// <summary>
    /// Flow 2, PartyAnswerInvitation (227) of <paramref name="invitee"/>. Nothing open: 9080, or 0 for an auto-decline (the client
    /// sends one before its own Leave / MakeLeader, f8k.as_2). A decline: 239 {name, false, auto, reason} to the inviter. An
    /// accept whose inviter is gone (no joined player and in no party), no longer leads its party (it joined another one or
    /// handed the lead on: the 9007 rule), whose party is full, or whose invitee joined another party meanwhile: 239 {false,
    /// true, busy} and 9080. Else the invitee joins: a new party of the two (230 to both), or the inviter's (232 to the
    /// members, then 230 to the invitee); then 239 {name, true, false, none} to the inviter when it plays.
    /// </summary>
    public short AnswerInvitation(Player invitee, bool accept, bool auto, byte reason)
    {
        var me = invitee.Character.Id;
        if (!_invites.Remove(me, out var invite)) return auto ? (short)0 : InvitationGone;
        var inviter = Online(invite.Inviter);
        if (!accept)
        {
            inviter?.Session.Send(Answer(invite.InviteeName, false, auto, reason));
            Log.Info("party", $"'{invite.InviteeName}' declines the invitation of '{invite.InviterName}' (reason {reason}{(auto ? ", auto" : "")})");
            return 0;
        }
        var party = Of(invite.Inviter);
        // An existing party needs no inviter player (it may be in the middle of a cluster change); a new one is made of it.
        var why = party is null && inviter is null ? "the inviter is gone"
            : party is not null && party.Leader != invite.Inviter ? "the inviter no longer leads its party"
            : Of(me) is not null ? "in a party already"
            : party is not null && party.Members.Count >= MaxSize ? "the party is full" : null;
        if (why is not null)
        {
            inviter?.Session.Send(Answer(invite.InviteeName, false, true, Busy));
            Log.Info("party", $"'{invite.InviteeName}' accepts the invitation of '{invite.InviterName}' too late ({why})");
            return InvitationGone;
        }
        _droppedAway.Remove(me);
        var joiner = MemberOf(invitee);
        var created = party is null;
        if (party is null)
        {
            var host = inviter!; // a new party without an inviter player was refused above
            _droppedAway.Remove(invite.Inviter);
            party = new Party(++_lastId, invite.Inviter);
            _parties.Add(party);
            party.Members.Add(MemberOf(host));
            _byMember[invite.Inviter] = party;
            party.Members.Add(joiner);
            _byMember[me] = party;
            var joined = Joined(party);
            host.Session.Send(joined);
            invitee.Session.Send(joined);
        }
        else
        {
            SendTo(party, PlayerJoined(party, joiner)); // the old members only: the joiner would add itself twice (SPEC §2)
            party.Members.Add(joiner);
            _byMember[me] = party;
            invitee.Session.Send(Joined(party));
        }
        inviter?.Session.Send(Answer(invite.InviteeName, true, false, NoReason));
        Log.Info("party", $"'{invite.InviteeName}' joins party {party.Id} of '{invite.InviterName}' ({party.Members.Count} members)");
        // The plugins' PartyJoined: a new party's leader first, then the joiner.
        if (created) world.Plugins.Party(true, invite.Inviter, invite.InviterName, party.Id, 2, "created");
        world.Plugins.Party(true, me, invite.InviteeName, party.Id, party.Members.Count, "invited");
        return 0;
    }

    /// <summary>Flow 3, PartyLeave (229): not in a party 9161 [choice: "@PARTY_LEAVE_FAILED"]; else out (<see cref="Remove"/>).</summary>
    public short Leave(Player p)
    {
        if (Of(p.Character.Id) is not { } party) return LeaveFailed;
        Remove(party, party.Find(p.Character.Id)!, kicked: false, "leaves");
        return 0;
    }

    /// <summary>
    /// The connection of <paramref name="character"/> ended without a cluster change and no other connection plays it: it leaves
    /// its party as with PartyLeave, without a response (flow 3).
    /// </summary>
    public void Disconnected(Guid character)
    {
        if (Of(character) is { } party) Remove(party, party.Find(character)!, kicked: false, "disconnected");
    }

    /// <summary>
    /// Flow 4, PartyKickPlayer (230): not in a party 9201, not the leader 9202, the target (by Guid, else by name) not another
    /// member 9200; else it is out with key 3 true. <paramref name="kickedName"/> = response key 0.
    /// </summary>
    public short Kick(Player leader, Guid target, string? name, out string? kickedName)
    {
        kickedName = null;
        var me = leader.Character.Id;
        if (Of(me) is not { } party) return KickNoParty;
        if (party.Leader != me) return KickNotLeader;
        if ((target != Guid.Empty ? party.Find(target) : party.Find(name)) is not { } m || m.Id == me) return KickNotFound;
        kickedName = m.Name;
        Remove(party, m, kicked: true, "is kicked");
        return 0;
    }

    /// <summary>
    /// <paramref name="m"/> is out of <paramref name="party"/>: PartyPlayerLeft (234) {0 id, 1 its Guid, 2 false, 3 kicked} to every
    /// member incl. it, then off the list. One member left: PartyDisbanded (231) {1 id, 2 false} to it and the party is gone.
    /// Else, when it led, the first member leads: PartyLeaderChanged (235) to all. A member out of the party that had no joined
    /// player for it is noted (<see cref="_droppedAway"/>) for PartyDisbanded on its arrival (<see cref="Joined(Player, bool)"/>).
    /// </summary>
    private void Remove(Party party, PartyMember m, bool kicked, string why)
    {
        SendTo(party, new PartyPlayerLeftEvent { dkj = party.Id, dkk = m.Id, dkm = kicked });
        party.Members.Remove(m);
        _byMember.Remove(m.Id);
        if (m.Missed) _droppedAway[m.Id] = party.Id;
        world.Expeditions.PartyLeft(m.Id); // stageH: a registration of the party's expedition group ends with it
        Log.Info("party", $"'{m.Name}' {why}: party {party.Id} has {party.Members.Count} member(s)");
        if (!m.Companion) world.Plugins.Party(false, m.Id, m.Name, party.Id, party.Members.Count, why); // the plugins' PartyLeft
        // StageN companions: a companion out of the party (kicked from the party frame, dismissed) goes; a player's companions leave with it.
        if (m.Companion) world.Companions.LeftParty(m.Id);
        else
            foreach (var own in party.Members.Where(c => c.Owner == m.Id).ToList())
            {
                SendTo(party, new PartyPlayerLeftEvent { dkj = party.Id, dkk = own.Id, dkm = false });
                party.Members.Remove(own);
                _byMember.Remove(own.Id);
                world.Companions.LeftParty(own.Id);
            }
        if (party.Members.Count <= 1 || party.Members.All(x => x.Companion))
        {
            SendTo(party, new PartyDisbandedEvent { dfp = party.Id });
            foreach (var rest in party.Members.ToList())
            {
                _byMember.Remove(rest.Id);
                if (rest.Missed) _droppedAway[rest.Id] = party.Id;
                if (rest.Companion) world.Companions.LeftParty(rest.Id); // stageN: the companions of a party with no player left go
            }
            party.Members.Clear();
            _parties.Remove(party);
            Log.Info("party", $"party {party.Id} disbanded");
            return;
        }
        if (party.Leader == m.Id) SetLeader(party, party.Members.FirstOrDefault(x => !x.Companion) ?? party.Members[0]);
    }

    // ---- companions (stageN companions SPEC §2) ----

    /// <summary>
    /// StageN companions: <paramref name="companion"/> (a bot) joins the party of <paramref name="owner"/>, which is made when it has none (the
    /// owner leads it: PartyJoined to the owner); else PartyPlayerJoined to the members that play, as for an accepted invitation. Null when it
    /// joined, else why not (the party is full by <paramref name="maxSize"/>).
    /// </summary>
    public string? AddCompanion(Player owner, Player companion, int maxSize)
    {
        var me = owner.Character.Id;
        var member = new PartyMember(companion.Character.Id, companion.Character.Name ?? "", (byte)companion.Character.Template) { Owner = me };
        if (Of(me) is not { } party)
        {
            _droppedAway.Remove(me);
            party = new Party(++_lastId, me);
            _parties.Add(party);
            party.Members.Add(MemberOf(owner));
            _byMember[me] = party;
            party.Members.Add(member);
            _byMember[member.Id] = party;
            owner.Session.Send(Joined(party));
        }
        else
        {
            if (party.Members.Count >= Math.Min(maxSize, MaxSize)) return $"the party is full ({party.Members.Count} of {Math.Min(maxSize, MaxSize)})";
            SendTo(party, PlayerJoined(party, member));
            party.Members.Add(member);
            _byMember[member.Id] = party;
        }
        Log.Info("party", $"companion '{member.Name}' of '{owner.Character.Name}' joins party {party.Id} ({party.Members.Count} members)");
        return null;
    }

    /// <summary>StageN companions: the companion of character <paramref name="id"/> is out of its party (dismissed, gone): as a leave.</summary>
    public void RemoveCompanion(Guid id)
    {
        if (Of(id) is { } party && party.Find(id) is { Companion: true } m) Remove(party, m, kicked: false, "is dismissed");
    }

    private void SetLeader(Party party, PartyMember m)
    {
        party.Leader = m.Id;
        SendTo(party, new PartyLeaderChangedEvent { djj = party.Id, djk = m.Id });
        Log.Info("party", $"'{m.Name}' leads party {party.Id}");
    }

    /// <summary>Flow 5, PartyMakeLeader (231): 9241 / 9242 / 9240 as Kick (the leader itself counts as not found [choice]); else 235 to all.</summary>
    public short MakeLeader(Player leader, Guid target, string? name, out string? newLeader)
    {
        newLeader = null;
        var me = leader.Character.Id;
        if (Of(me) is not { } party) return LeaderNoParty;
        if (party.Leader != me) return LeaderNotLeader;
        if ((target != Guid.Empty ? party.Find(target) : party.Find(name)) is not { } m || m.Id == me || m.Companion) return LeaderNotFound; // a companion never leads
        newLeader = m.Name;
        SetLeader(party, m);
        return 0;
    }

    /// <summary>Flow 6, PartyChangeLootSetting (232): the leader only, mode 0 or 1 (else 9202 [choice]); then 236 {0 id, 1 mode} to all.</summary>
    public short ChangeLootSetting(Player p, int mode)
    {
        if (Of(p.Character.Id) is not { } party || party.Leader != p.Character.Id || mode is not (0 or 1)) return KickNotLeader;
        party.LootMode = mode;
        SendTo(party, new PartyLootSettingChangedPlayerEvent { dj1 = party.Id, dj2 = mode });
        Log.Info("party", $"party {party.Id} loot mode {(mode == 0 ? "FreeLoot" : "PartyLoot (looted as FreeLoot)")}");
        return 0;
    }

    /// <summary>Flow 7, PartySetRole (234, no response): the role is stored, then PartySetRoleFlag (243) {0 role, 1 Guid} to all. Nothing outside a party.</summary>
    public void SetRole(Player p, int role)
    {
        if (Of(p.Character.Id) is not { } party) return;
        party.Find(p.Character.Id)!.Role = role;
        SendTo(party, new PartySetRoleFlagEvent { dk1 = role, dk2 = p.Character.Id });
    }

    /// <summary>
    /// Flow 7, PartyMarkObject (233): Marks[slot] = mark (slot 0..5; null clears), then PartyMarkedObjectsUpdated (241) {0 id,
    /// 1 the six marks, 2 the marker} to all. Outside a party or with another slot nothing happens; the answer is rc 0 either way.
    /// </summary>
    public void MarkObject(Player p, int slot, string? mark)
    {
        if (Of(p.Character.Id) is not { } party || slot is < 0 or > 5) return;
        party.Marks[slot] = mark ?? "";
        SendTo(party, new PartyMarkedObjectsUpdatedEvent { dj3 = party.Id, dj4 = [.. party.Marks], dj5 = p.Character.Id });
    }

    /// <summary>StageN qol (SPEC §4): the shortest time between two minimap pings of one player; a faster one is dropped.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// StageN qol (analysis/work/stageN/qol/SPEC.md §4), SendMiniMapPing (op 168, aaw: 0 Vector2 map position, 1 MiniMapPingTypes 0 Default /
    /// 1 Attack / 2 Defend): MiniMapPing (event 181, asn: 0 position, 1 type, 2 the pinger's name) to the pinger and every party member on the
    /// same map (outside a party, to the pinger alone). An unknown type or a ping within <see cref="PingInterval"/> is dropped. Returns how many got it.
    /// </summary>
    public int Ping(Player p, System.Numerics.Vector2 at, byte type)
    {
        if (type > 2 || p.Map is not { } map || p.LastPing is { } last && world.Now - last < PingInterval) return 0;
        p.LastPing = world.Now;
        var ping = new MiniMapPingEvent { crr = at, crs = type, crt = p.Character.Name };
        var to = Of(p.Character.Id) is { } party
            ? party.Members.Select(m => Online(m.Id)).OfType<Player>().Where(m => m.Map == map).ToList()
            : [p];
        foreach (var m in to) m.Session.Send(ping);
        return to.Count;
    }

    /// <summary>Flow 8, GetPartyMemberCluster (546): the cluster of the member called <paramref name="name"/> that plays; null = 9000 [choice].</summary>
    public string? MemberCluster(Player p, string? name) =>
        Of(p.Character.Id)?.Find(name) is { } m && Online(m.Id) is { } member ? member.Character.Cluster : null;

    /// <summary>
    /// Flow 9: <paramref name="p"/> has joined. A character listed in a party gets its PartyJoined again after a fresh login (a
    /// reconnect; a relog cannot keep it, flow 3), and after a cluster change when it missed a party event meanwhile
    /// (<see cref="PartyMember.Missed"/>) or with Party:ResendAfterClusterChange (else the client keeps the party over it, C5).
    /// One taken out of its party during the change gets PartyDisbanded {1 old id, 2 false} (231 clears the client's party
    /// whatever its id; a fresh client has none to clear). Its fellow members get nothing (232 would list it twice, SPEC §2).
    /// </summary>
    public void Joined(Player p, bool clusterChange)
    {
        var id = p.Character.Id;
        if (_droppedAway.Remove(id, out var old) && clusterChange && Of(id) is null)
        {
            p.Session.Send(new PartyDisbandedEvent { dfp = old });
            Log.Info("party", $"'{p.Character.Name}' arrives out of party {old}");
        }
        if (Of(id) is not { } party) return;
        var member = party.Find(id)!;
        var missed = member.Missed;
        member.Missed = false;
        if (clusterChange && !missed && !world.Config.PartyResendAfterClusterChange) return;
        p.Session.Send(Joined(party));
        Log.Info("party", $"'{p.Character.Name}' gets party {party.Id} again ({(!clusterChange ? "login" : missed ? "missed events" : "cluster change")})");
    }

    /// <summary>
    /// SPEC §4: a party Say line (key 9) of <paramref name="speaker"/>, already echoed to it, as ChatSay with key 7 = true to every
    /// other member that plays, on any map (key 7 lets the client show a speaker it does not know). Returns how many got it.
    /// </summary>
    public int PartyLine(Player speaker, ChatSayEvent say)
    {
        if (Of(speaker.Character.Id) is not { } party) return 0;
        var line = new ChatSayEvent { bxl = say.bxl, bxm = say.bxm, bxp = say.bxp, bxq = say.bxq, bxr = say.bxr, bxu = say.bxu, bxs = true, Extra = say.Extra };
        var heard = 0;
        foreach (var m in party.Members)
            if (m.Id != speaker.Character.Id && Online(m.Id) is { } p)
            {
                p.Session.Send(line);
                heard++;
            }
        return heard;
    }

    /// <summary>
    /// SPEC §5: the players who share the fame of <paramref name="mob"/>, killed by <paramref name="killer"/>: the killer first,
    /// then its party members on the mob's map within gamedata pvefamesharingradius (50 m) of the mob [choice: the mob's position]
    /// that are not knocked down [choice]; with Guilds:ShareWithoutParty also its guild mates there, as party members.
    /// </summary>
    public List<Player> FameGroup(Player killer, Mob mob)
    {
        var group = new List<Player> { killer };
        if (mob.Map is not { } map) return group;
        var radius = world.GameData?.PveFameSharingRadius ?? 50;
        var now = DateTime.UtcNow;
        bool Near(Player p) => p.Combat.Down is null && p.Map == map && Vector2.Distance(p.PositionAt(now), mob.Position) <= radius;
        if (Of(killer.Character.Id) is { } party)
            foreach (var m in party.Members)
                if (m.Id != killer.Character.Id && Online(m.Id) is { } p && Near(p))
                    group.Add(p);
        // Guilds:ShareWithoutParty (guilds SPEC §10.4): the killer's guild mates near the kill share as party members do.
        if (world.Guilds.SharingGuild(killer) is { } guild)
            foreach (var m in guild.Members)
                if (m.Id != killer.Character.Id && Online(m.Id) is { } p && !group.Contains(p) && Near(p))
                    group.Add(p);
        return group;
    }

    /// <summary>
    /// The world tick: invitations past their time end (505 {0 inviter Guid, 1 name} to the invitee, 239 {invitee, false, true,
    /// timed out} to the inviter), and members without a joined player for <see cref="OfflineGrace"/> leave.
    /// </summary>
    public void Update(TimeSpan now)
    {
        if (_invites.Count > 0)
            foreach (var invite in _invites.Values.Where(i => i.Expires <= now).ToList())
            {
                _invites.Remove(invite.Invitee);
                Online(invite.Invitee)?.Session.Send(new PartyInviteAbortedEvent { dh6 = invite.Inviter, dh7 = invite.InviterName });
                Online(invite.Inviter)?.Session.Send(Answer(invite.InviteeName, false, true, TimedOut));
                Log.Info("party", $"the invitation of '{invite.InviteeName}' by '{invite.InviterName}' timed out");
            }
        // Members without a joined player: once a second, not every tick (Online scans every map).
        if (_parties.Count == 0 || now < _nextOfflineCheck) return;
        _nextOfflineCheck = now + TimeSpan.FromSeconds(1);
        foreach (var party in _parties.ToList())
            foreach (var m in party.Members.ToList())
            {
                if (m.Companion) continue; // stageN: Companions keeps its own owners' grace
                if (Online(m.Id) is not null) m.OfflineSince = null;
                else if (m.OfflineSince is not { } since) m.OfflineSince = now;
                else if (now - since >= OfflineGrace && party.Members.Contains(m)) Remove(party, m, kicked: false, "has not come back");
            }
    }
}
