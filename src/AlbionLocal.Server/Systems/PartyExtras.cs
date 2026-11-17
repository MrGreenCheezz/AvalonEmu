using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageR party: a ready check (analysis/work/stageR/party/SPEC.md §1). <see cref="Ends"/> / <see cref="CooldownEnds"/> are server
/// clock times; <see cref="EndStamp"/> / <see cref="CooldownStamp"/> the same as GameTimeStamps (event 245 keys 0 and 1). The
/// answers are beh.a per member Guid: 0 none yet, 1 ready, 2 not ready.
/// </summary>
public sealed class ReadyCheck
{
    public required TimeSpan Ends { get; init; }

    public required TimeSpan CooldownEnds { get; init; }

    public required long EndStamp { get; init; }

    public required long CooldownStamp { get; init; }

    public Dictionary<Guid, byte> Answers { get; } = [];
}

/// <summary>StageR party: the leader's navigation of op 547 (aa7 keys 0, 2, 3), relayed as event 247 and PartyJoined keys 5-7.</summary>
public sealed record PartyNavigation(string Destination, bool Flag1, bool Flag2);

/// <summary>
/// StageR party: an open join request (op 226) of <see cref="Requester"/> to <see cref="Target"/>, the leader of the party asked for, or
/// the solo player asked; at most one per requester.
/// </summary>
public sealed record PendingJoinRequest(Guid Requester, string RequesterName, Guid Target, string TargetName, TimeSpan Expires);

/// <summary>
/// StageR party (analysis/work/stageR/party/SPEC.md): ready checks (op 512/513, event 245), party navigation (op 547/548, events
/// 247/248), the FW reinforcements flag (op 235, event 246) and join requests (op 226/228, events 229/240/504).
/// </summary>
public sealed partial class Parties
{
    // ---- ready check (SPEC §1) ----

    /// <summary>Ready-check return codes: the client's enum na (20481-20484, the same checks it makes itself, f8i.as) and nb (20520) [assume for nb].</summary>
    public const short ReadyNoParty = 20481, ReadyNotLeader = 20482, ReadyRunning = 20483, ReadyCooldown = 20484, ReadyNotRunning = 20520;

    /// <summary>beh.a: the answer of one member in event 245 key 3.</summary>
    public const byte ReadyNone = 0, ReadyYes = 1, ReadyNo = 2;

    /// <summary>The PartyReadyCheckUpdate event (245) of <paramref name="party"/>'s check: 0 end, 1 cooldown end, 2 the members, 3 their answers.</summary>
    private static PartyReadyCheckUpdateEvent ReadyEvent(Party party, ReadyCheck check) => new()
    {
        dku = check.EndStamp, dkv = check.CooldownStamp,
        dkw = [.. check.Answers.Keys], dkx = [.. check.Answers.Values],
    };

    /// <summary>
    /// PartyReadyCheckBegin (512): the client's own checks again (no party 20481, not the leader 20482, a check running 20483, the
    /// wait after the last one 20484). Then the check runs for gamedata PartyReadyCheckDuration; the next may start
    /// PartyReadyCheckTimeout after its end [infer: 20484 could never show if the wait began with the check]. The leader starts
    /// ready, and so do companions (no client) [assume]; the others start unanswered. Event 245 goes to every member.
    /// </summary>
    public short BeginReadyCheck(Player p)
    {
        var me = p.Character.Id;
        if (Of(me) is not { } party) return ReadyNoParty;
        if (party.Leader != me) return ReadyNotLeader;
        var now = world.Clock.Now;
        if (party.Ready is { } last)
        {
            if (now < last.Ends) return ReadyRunning;
            if (now < last.CooldownEnds) return ReadyCooldown;
        }
        var (duration, timeout) = world.GameData?.PartyReadyCheck ?? (60, 60);
        var check = new ReadyCheck
        {
            Ends = now + TimeSpan.FromSeconds(duration),
            CooldownEnds = now + TimeSpan.FromSeconds(duration + timeout),
            EndStamp = GameTime.Now + TimeSpan.FromSeconds(duration).Ticks,
            CooldownStamp = GameTime.Now + TimeSpan.FromSeconds(duration + timeout).Ticks,
        };
        foreach (var m in party.Members) check.Answers[m.Id] = m.Id == me || m.Companion ? ReadyYes : ReadyNone;
        party.Ready = check;
        SendTo(party, ReadyEvent(party, check));
        Log.Info("party", $"'{p.Character.Name}' starts a ready check in party {party.Id} ({party.Members.Count} members)");
        return 0;
    }

    /// <summary>
    /// PartyReadyCheckUpdate (513, 0 bool ready): the member's answer (1 ready, 2 not ready) while the check runs and lists it, then
    /// 245 to every member; else 20520 [assume]. The client ends the check by itself once every member has answered.
    /// </summary>
    public short AnswerReadyCheck(Player p, bool ready)
    {
        var me = p.Character.Id;
        if (Of(me) is not { Ready: { } check } party || world.Clock.Now >= check.Ends || !check.Answers.ContainsKey(me)) return ReadyNotRunning;
        check.Answers[me] = ready ? ReadyYes : ReadyNo;
        SendTo(party, ReadyEvent(party, check));
        Log.Debug("party", $"'{p.Character.Name}' answers the ready check of party {party.Id}: {(ready ? "ready" : "not ready")}");
        return 0;
    }

    // ---- navigation (SPEC §2) ----

    /// <summary>
    /// SetPartyNavigation (547, aa7: 0 destination cluster, 1 the sender's Guid, 2/3 two route flags): the leader only, else 9242
    /// [assume: any rc ≠ 0 shows the client's failure line]. Stored, then SetPartyNavigation (247) {0 destination, 1, 2 the flags}
    /// to every other member (the leader's client ignores 247 anyway).
    /// </summary>
    public short SetNavigation(Player p, string? destination, bool flag1, bool flag2)
    {
        if (Of(p.Character.Id) is not { } party) return LeaderNoParty;
        if (party.Leader != p.Character.Id || string.IsNullOrEmpty(destination)) return LeaderNotLeader;
        party.Navigation = new PartyNavigation(destination, flag1, flag2);
        SendTo(party, new SetPartyNavigationEvent { dky = destination, dkz = flag1, dk0 = flag2 }, except: party.Leader);
        Log.Info("party", $"'{p.Character.Name}' sets the party navigation of party {party.Id} to {destination}");
        return 0;
    }

    /// <summary>EndPartyNavigation (548, r4: 0 the sender's Guid): the leader only (else 9242 [assume]); then 248 to the other members.</summary>
    public short EndNavigation(Player p)
    {
        if (Of(p.Character.Id) is not { } party) return LeaderNoParty;
        if (party.Leader != p.Character.Id) return LeaderNotLeader;
        StopNavigation(party, "the leader ends it");
        return 0;
    }

    /// <summary>The navigation of <paramref name="party"/> ends: EndPartyNavigation (248) to every member but the leader. Nothing when none runs.</summary>
    private void StopNavigation(Party party, string why)
    {
        if (party.Navigation is null) return;
        party.Navigation = null;
        SendTo(party, new EndPartyNavigationEvent(), except: party.Leader);
        Log.Info("party", $"the party navigation of party {party.Id} ends ({why})");
    }

    // ---- FW reinforcements (SPEC §3) ----

    /// <summary>
    /// PartyChangeFactionWarfareRequestReinforcementsSetting (235, yd: 0 bool): the leader's flag (the client sends it only as the
    /// leader, c3g.aa), else 9202 [assume]; then 246 {0 party id, 1 flag} to every member. Only stored: no faction-warfare
    /// reinforcement requests exist on this server.
    /// </summary>
    public short SetFwReinforcements(Player p, bool on)
    {
        if (Of(p.Character.Id) is not { } party || party.Leader != p.Character.Id) return KickNotLeader;
        party.FwReinforcements = on;
        SendTo(party, new PartyFactionWarfareReinforcementSettingChangedPlayerEvent { dfr = party.Id, dfs = on });
        return 0;
    }

    // ---- join requests (SPEC §4) ----

    private readonly Dictionary<Guid, PendingJoinRequest> _joins = []; // by requester

    // The client's texts for op 226 (f8l.b(ah5) 0x181c74120) and 228 (9120 "no longer available").
    public const short JoinAlreadyInParty = 9004, JoinPendingRequest = 9008, JoinTargetOffline = 9009, JoinPartyFull = 9013, JoinRequestGone = 9120;

    /// <summary>The open join request of <paramref name="requester"/>; null when none.</summary>
    public PendingJoinRequest? JoinRequestOf(Guid requester) => _joins.GetValueOrDefault(requester);

    /// <summary>
    /// PartyRequestJoin (226, yz: 0 target Guid, 1 name): the target by Guid, else by name (a player that plays, or a party member,
    /// e.g. a companion). Missing or self 9000; the requester in a party 9004 [assume: no party merge]; its request still open 9008;
    /// the target's party leader not playing 9009; the party full 9013. Else the request goes to the target's party leader, or to the
    /// target itself when solo [infer]: PartyJoinRequest (229) {0 requester Guid, 1 name, 5 frame} (keys 12-16, the requester's
    /// party, never: it has none), and runs for <see cref="InviteTime"/> [infer: the client's countdown is the invitation one].
    /// <paramref name="targetName"/> = response key 0 (the name the client prints in "@PARTY_JOIN_REQUEST_SENT_OUT").
    /// </summary>
    public short RequestJoin(Player requester, Guid target, string? name, out string? targetName)
    {
        targetName = null;
        var me = requester.Character.Id;
        var to = target != Guid.Empty ? Online(target) : OnlineByName(name);
        Party? party;
        if (to is not null) (party, targetName) = (Of(to.Character.Id), to.Character.Name);
        else if (target != Guid.Empty && Of(target) is { } of && of.Find(target) is { } member) (party, targetName) = (of, member.Name);
        else return PlayerNotFound;
        if (to?.Character.Id == me) return PlayerNotFound;
        if (Of(me) is not null) return JoinAlreadyInParty;
        if (_joins.ContainsKey(me)) return JoinPendingRequest;
        var leader = party is null ? to! : Online(party.Leader);
        if (leader is null || leader.Bot is not null) return JoinTargetOffline; // a solo world bot cannot answer one [assume]
        if (party is not null && party.Members.Count + _invites.Values.Count(i => party.Find(i.Inviter) is not null) >= MaxSize) return JoinPartyFull;
        _joins[me] = new PendingJoinRequest(me, requester.Character.Name ?? "", leader.Character.Id, leader.Character.Name ?? "", world.Clock.Now + InviteTime);
        leader.Session.Send(new PartyJoinRequestEvent { diw = me, dix = requester.Character.Name, di1 = (byte)requester.Character.Template });
        Log.Info("party", $"'{requester.Character.Name}' asks '{leader.Character.Name}' to join{(party is null ? "" : $" party {party.Id}")}");
        return 0;
    }

    /// <summary>
    /// PartyAnswerJoinRequest (228, yb: 0 accept, 1 auto, 2 requester Guid, 3 PartyAnswerDeclineReason) of <paramref name="answerer"/>.
    /// No such open request to it: 9120 (an auto-decline with nothing open: 0). A decline: 240 {0 the answerer's name, 1 false, 2 auto,
    /// 3 reason} to the requester. An accept whose requester is gone or in a party meanwhile, whose answerer no longer leads its party,
    /// or whose party is full: 240 {false, true, busy} and 9120. Else the requester joins the answerer's party (or a new one led by
    /// the answerer) as with an accepted invitation, and gets 240 {name, true}.
    /// </summary>
    public short AnswerJoinRequest(Player answerer, bool accept, bool auto, Guid requesterId, byte reason)
    {
        var me = answerer.Character.Id;
        if (!_joins.TryGetValue(requesterId, out var request) || request.Target != me) return auto ? (short)0 : JoinRequestGone;
        _joins.Remove(requesterId);
        var requester = Online(requesterId);
        if (!accept)
        {
            requester?.Session.Send(JoinAnswer(answerer.Character.Name, false, auto, reason));
            Log.Info("party", $"'{answerer.Character.Name}' declines the join request of '{request.RequesterName}' (reason {reason}{(auto ? ", auto" : "")})");
            return 0;
        }
        var party = Of(me);
        var why = requester is null ? "the requester is gone"
            : Of(requesterId) is not null ? "the requester is in a party already"
            : party is not null && party.Leader != me ? "the answerer no longer leads its party"
            : party is not null && party.Members.Count >= MaxSize ? "the party is full" : null;
        if (why is not null)
        {
            requester?.Session.Send(JoinAnswer(answerer.Character.Name, false, true, Busy));
            Log.Info("party", $"'{answerer.Character.Name}' accepts the join request of '{request.RequesterName}' too late ({why})");
            return JoinRequestGone;
        }
        _droppedAway.Remove(requesterId);
        var created = party is null;
        party = Admit(party, answerer, requester!, MemberOf(requester!));
        requester!.Session.Send(JoinAnswer(answerer.Character.Name, true, false, NoReason));
        Log.Info("party", $"'{request.RequesterName}' joins party {party.Id} of '{answerer.Character.Name}' by request ({party.Members.Count} members)");
        if (created) world.Modules.Party(true, me, answerer.Character.Name ?? "", party.Id, 2, "created");
        world.Modules.Party(true, requesterId, request.RequesterName, party.Id, party.Members.Count, "requested");
        return 0;
    }

    private static PartyJoinRequestAnswerEvent JoinAnswer(string? name, bool accepted, bool auto, byte reason) =>
        new() { djf = name, djg = accepted, djh = auto, dji = reason };

    /// <summary>Join requests past their time end: 504 {0 requester, 1 name} to the answerer, 240 {target, false, true, timed out} to the requester.</summary>
    private void ExpireJoinRequests(TimeSpan now)
    {
        if (_joins.Count == 0) return;
        foreach (var r in _joins.Values.Where(r => r.Expires <= now).ToList())
        {
            _joins.Remove(r.Requester);
            Online(r.Target)?.Session.Send(new PartyJoinRequestAbortedEvent { djd = r.Requester, dje = r.RequesterName });
            Online(r.Requester)?.Session.Send(JoinAnswer(r.TargetName, false, true, TimedOut));
            Log.Info("party", $"the join request of '{r.RequesterName}' to '{r.TargetName}' timed out");
        }
    }

    /// <summary>
    /// <paramref name="character"/> logged out: its own open request ends (504 to the answerer), and requests waiting for its answer
    /// end (240 {false, true, busy} to the requesters).
    /// </summary>
    private void DropJoinRequests(Guid character)
    {
        if (_joins.Count == 0) return;
        foreach (var r in _joins.Values.Where(r => r.Requester == character || r.Target == character).ToList())
        {
            _joins.Remove(r.Requester);
            if (r.Requester == character) Online(r.Target)?.Session.Send(new PartyJoinRequestAbortedEvent { djd = r.Requester, dje = r.RequesterName });
            else Online(r.Requester)?.Session.Send(JoinAnswer(r.TargetName, false, true, Busy));
        }
    }
}
