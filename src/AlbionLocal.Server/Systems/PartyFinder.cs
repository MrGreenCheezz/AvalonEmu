using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A party search of the party finder (op 361/363): keyed by its leader's character Guid, as the client keys it (c3k.b, the
/// debug dump c3k.h prints "LeaderGuid"; c3k.f_1 matches a key against the party leader c3g+0x18 or the own Guid). Activity,
/// min item power (a67.b), max group size (a67.c), auto accept (a67.d: the find view shows "Join" instead of "Apply"),
/// title (a67.e), details (a67.f), visibility (PartyFinderVisibility). <see cref="Pending"/> = the applicants waiting for an answer.
/// </summary>
public sealed class FinderListing(Guid leader, string leaderName)
{
    public Guid Leader { get; set; } = leader;
    public string LeaderName { get; set; } = leaderName;
    public int Activity { get; set; }
    public int MinItemPower { get; set; }
    public int MaxGroupSize { get; set; }
    public bool AutoAccept { get; set; }
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public int Visibility { get; set; }
    public List<Guid> Pending { get; } = [];
}

/// <summary>A player's own "find parties" registration (op 368, the client's c3k.d / a63): what it looks for and its roles.</summary>
public sealed class FinderApplicant(Guid id, string name)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public int Activity { get; set; }
    public int MinItemPower { get; set; }
    public int MaxGroupSize { get; set; }
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public int Visibility { get; set; }
}

/// <summary>
/// The party finder (analysis/work/stageR/party/SPEC.md §5): a world-wide list kept in memory (nothing saved, as the parties). Leaders
/// list their party (<see cref="FinderListing"/>), players subscribe per activity (op 359: FullUpdate 370 at once, then Update 371 per
/// change), apply (365), and the leader sees its applicants (ApplicantsUpdate 372) and accepts (the applicant joins through
/// <see cref="Parties"/>' Admit) or declines (374 to the applicant). A listing goes when its party fills, disbands, or its leader
/// deletes it or logs out. Every per-listing / per-member array of an event has the length the client's readers index
/// (c3k.f(g7s) 0x180a874d0, c3k.g 0x180a893f0, c3k.h 0x180a8a6c0, c3k.f_t2e68 0x180a889b0). Touched only under World.Sync.
/// </summary>
public sealed class PartyFinder(World world, Parties parties)
{
    /// <summary>Update kinds (enum a66; c3k.g: 0/1 add or replace, 2/3 remove; the window closes on 3, PartyFinderGui.OnPartyFinderUpdateMsg).</summary>
    public const int KindNew = 0, KindChanged = 1, KindRemoved = 2, KindClosed = 3;

    /// <summary>Update list (enum a65; c3k.g): 0 a party search, 1 the receiver's own applicant registration.</summary>
    public const int ListParties = 0, ListOwnApplicant = 1;

    // RcOpPartyFinderApplyForGroup / RcOpPartyFinderEnlistNewPartySearch (Albion.Common.Photon).
    public const short ApplyLeaderNotKnown = 14600, ApplyApplicantNotKnown = 14601, ApplyGroupNotKnown = 14602, ApplyAlreadyFull = 14603,
        ApplyPartyDoesNotFit = 14604, ApplyInGroupNotLeader = 14605, ApplyEnlistedParty = 14608, ApplyOwnSearch = 14607,
        ApplyAlreadyPending = 14609, ApplyItemPowerTooLow = 14610, ApplyPending = 14611, EnlistInPartyNotLeader = 14444;

    /// <summary>Activity.NumActivities: valid activities are 0..6 (OpenWorldFarm .. Roleplay).</summary>
    public const int Activities = 7;

    /// <summary>[assume] the longest title / details kept (the client's inputs limit them; the server's rcs 14440/14441 print no text).</summary>
    public const int MaxTitle = 64, MaxDetails = 512;

    private readonly Dictionary<Guid, FinderListing> _listings = [];     // by leader
    private readonly Dictionary<Guid, FinderApplicant> _applicants = []; // by character
    private readonly Dictionary<Guid, int> _subscribed = [];             // character → activity of op 359
    private readonly Dictionary<Guid, int[]> _roles = [];                // character → [dps, tank, heal] of op 368 / 364

    public FinderListing? ListingOf(Guid leader) => _listings.GetValueOrDefault(leader);

    public FinderApplicant? ApplicantOf(Guid id) => _applicants.GetValueOrDefault(id);

    public int? SubscriptionOf(Guid id) => _subscribed.TryGetValue(id, out var a) ? a : null;

    public IReadOnlyCollection<FinderListing> Listings => _listings.Values;

    // ---- members, roles, item power ----

    private sealed record Row(Guid Id, string Name, int[] Roles, float ItemPower);

    /// <summary>
    /// The roles a member shows: the [dps, tank, heal] flags it chose (op 368 / 364; the client's GetRolesFromToggles), else its party
    /// role (op 234) as a one-element itemroles index, else [-1] (no role). The client reads either form (length 3, or length 1 through
    /// GetPartyFinderRoleFromItemRole); the official server sends the one-element form for party members (capture 002835: [7], [-1]).
    /// </summary>
    private int[] RolesOf(Guid id, int partyRole) =>
        _roles.TryGetValue(id, out var r) ? [.. r] : [partyRole >= 0 ? partyRole : -1];

    private float ItemPowerOf(Guid id) =>
        parties.Online(id) is { } p ? (float)Math.Round(Network.WorldSession.AverageItemPower(p), 2) : 0f;

    private List<Row> Members(FinderListing l)
    {
        if (parties.Of(l.Leader) is { Members.Count: > 0 } party)
            return [.. party.Members.Select(m => new Row(m.Id, m.Name, RolesOf(m.Id, m.Role), ItemPowerOf(m.Id)))];
        return [new Row(l.Leader, l.LeaderName, RolesOf(l.Leader, -1), ItemPowerOf(l.Leader))];
    }

    private int Size(FinderListing l) => parties.Of(l.Leader)?.Members.Count ?? 1;

    /// <summary>The most members the listing's party may have: its max group size, at most Party:MaxSize.</summary>
    private int Cap(FinderListing l) => Math.Min(l.MaxGroupSize, parties.MaxSize);

    // ---- who sees what ----

    /// <summary>
    /// Whether <paramref name="viewer"/> may see <paramref name="l"/>: its party always; else by visibility 0 all, 1 the leader's guild,
    /// 2 the leader's alliance (or guild) [infer: the enum names].
    /// </summary>
    private bool Visible(FinderListing l, Guid viewer)
    {
        if (viewer == l.Leader || parties.Of(l.Leader)?.Find(viewer) is not null) return true;
        return l.Visibility switch
        {
            1 => world.Guilds.Of(l.Leader) is { } g && world.Guilds.Of(viewer) == g,
            2 => world.Guilds.Of(l.Leader) is { } g2 && world.Guilds.Of(viewer) == g2 || world.Alliances.SameAlliance(l.Leader, viewer),
            _ => true,
        };
    }

    /// <summary>The listing's party members (or its solo leader) that play.</summary>
    private IEnumerable<Player> Own(FinderListing l) =>
        (parties.Of(l.Leader)?.Members.Select(m => m.Id) ?? [l.Leader]).Select(parties.Online).OfType<Player>();

    /// <summary>The players subscribed to the listing's activity that may see it, outside its party.</summary>
    private IEnumerable<Player> Watchers(FinderListing l, int? activity = null)
    {
        var own = parties.Of(l.Leader);
        foreach (var (id, a) in _subscribed.ToList())
            if (a == (activity ?? l.Activity) && id != l.Leader && own?.Find(id) is null && Visible(l, id) && parties.Online(id) is { } p)
                yield return p;
    }

    // ---- events ----

    /// <summary>
    /// PartyFinderUpdate (371, awb) of a party search: 0 kind, 1 list 0, 2 key (leader Guid), 3 the previous key when it moved (else
    /// empty: c3k.g removes it), 4 leader name, 5-11 one entry per member (5 names, 6/7 ints (avatar / ring [infer], 0), 8 flagging
    /// (packed, 0), 9 a byte (0 [assume]), 10 item power, 11 roles), 12 activity, 13 min IP, 14 max group size, 15 auto accept,
    /// 16 title, 17 details, 18 visibility.
    /// </summary>
    private PartyFinderUpdateEvent Update(FinderListing l, int kind, Guid? previous = null)
    {
        var rows = Members(l);
        var n = rows.Count;
        return new PartyFinderUpdateEvent
        {
            dg2 = kind, dg3 = ListParties, dg4 = l.Leader, dg5 = previous ?? Guid.Empty, dg6 = l.LeaderName,
            dg7 = [.. rows.Select(r => r.Name)], dg8 = new int[n], dg9 = new int[n], dha = new byte[n], dhb = new byte[n],
            dhc = [.. rows.Select(r => r.ItemPower)], dhd = [.. rows.Select(r => r.Roles)],
            dhe = l.Activity, dhf = l.MinItemPower, dhg = l.MaxGroupSize, dhh = l.AutoAccept, dhi = l.Title, dhj = l.Details, dhk = l.Visibility,
        };
    }

    /// <summary>
    /// PartyFinderUpdate (371) of the receiver's own registration (list 1; c3k.g makes it c3k.d whoever it names, so it only ever goes
    /// to the registered player): one member, itself; 13 min IP, 14 max group size, 15 false.
    /// </summary>
    private PartyFinderUpdateEvent Update(FinderApplicant a, int kind) => new()
    {
        dg2 = kind, dg3 = ListOwnApplicant, dg4 = a.Id, dg6 = a.Name,
        dg7 = [a.Name], dg8 = [0], dg9 = [0], dha = [0], dhb = [0], dhc = [ItemPowerOf(a.Id)], dhd = [RolesOf(a.Id, -1)],
        dhe = a.Activity, dhf = a.MinItemPower, dhg = a.MaxGroupSize, dhi = a.Title, dhj = a.Details, dhk = a.Visibility,
    };

    /// <summary>
    /// PartyFinderFullUpdate (370, av9) for <paramref name="viewer"/>: the listings of <paramref name="activity"/> it may see (matching
    /// <paramref name="text"/> when given), keys 2-18 one entry each (2 Guid, 3 leader name, 4-10 the per-member rows, 11 activity, 12
    /// auto accept, 13 min IP, 14 max group size, 15 title, 16 details, 17 visibility, 18 the viewer has applied: c3k keeps it as pending).
    /// Keys 0/1 are not read by the client (c3k.f(g7s)) and stay 0. With no listing key 2 is an empty Guid[] (a null one throws in
    /// c3k.f) and the nested arrays are left out.
    /// </summary>
    public PartyFinderFullUpdateEvent FullUpdate(Guid viewer, int activity, string? text = null)
    {
        var list = _listings.Values.Where(l => l.Activity == activity && Visible(l, viewer) && Matches(l, text)).ToList();
        var rows = list.Select(Members).ToList();
        var e = new PartyFinderFullUpdateEvent
        {
            dgh = [.. list.Select(l => l.Leader)], dgi = [.. list.Select(l => l.LeaderName)],
            dgq = [.. list.Select(l => l.Activity)], dgr = [.. list.Select(l => l.AutoAccept)],
            dgs = [.. list.Select(l => l.MinItemPower)], dgt = [.. list.Select(l => l.MaxGroupSize)],
            dgu = [.. list.Select(l => l.Title)], dgv = [.. list.Select(l => l.Details)],
            dgw = [.. list.Select(l => l.Visibility)], dgx = [.. list.Select(l => l.Pending.Contains(viewer))],
        };
        if (list.Count == 0) return e;
        e.dgj = [.. rows.Select(r => r.Select(x => x.Name).ToArray())];
        e.dgk = [.. rows.Select(r => new int[r.Count])];
        e.dgl = [.. rows.Select(r => new int[r.Count])];
        e.dgm = [.. rows.Select(r => new byte[r.Count])];
        e.dgn = [.. rows.Select(r => new byte[r.Count])];
        e.dgo = [.. rows.Select(r => r.Select(x => x.ItemPower).ToArray())];
        e.dgp = [.. rows.Select(r => r.Select(x => x.Roles).ToArray())];
        return e;
    }

    private bool Matches(FinderListing l, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        bool Has(string? s) => s?.Contains(t, StringComparison.OrdinalIgnoreCase) == true;
        return Has(l.Title) || Has(l.Details) || Has(l.LeaderName) || Members(l).Any(r => Has(r.Name));
    }

    /// <summary>
    /// PartyFinderApplicantsUpdate (372, av7) for the leader of <paramref name="l"/>: its pending applicants, each one entry (0 Guid =
    /// the key op 366 answers, 1 name, 2-8 the per-member rows of one member, 9 activity, 10 min IP, 11 max group size, 12/13 title and
    /// details, 14 visibility, from the applicant's own registration, else the listing's activity). None: key 0 empty, the rest left out
    /// (c3k.h clears its list when key 2 is missing).
    /// </summary>
    private PartyFinderApplicantsUpdateEvent Applicants(FinderListing l)
    {
        var list = l.Pending.Select(id => (Id: id, Player: parties.Online(id), Reg: ApplicantOf(id))).Where(x => x.Player is not null).ToList();
        var e = new PartyFinderApplicantsUpdateEvent { dft = [.. list.Select(x => x.Id)] };
        if (list.Count == 0) return e;
        string Name((Guid Id, Player? Player, FinderApplicant? Reg) x) => x.Player!.Character.Name ?? "";
        e.dfu = [.. list.Select(Name)];
        e.dfv = [.. list.Select(x => new[] { Name(x) })];
        e.dfw = [.. list.Select(_ => new int[1])];
        e.dfx = [.. list.Select(_ => new int[1])];
        e.dfy = [.. list.Select(_ => new byte[1])];
        e.dfz = [.. list.Select(_ => new byte[1])];
        e.df0 = [.. list.Select(x => new[] { ItemPowerOf(x.Id) })];
        e.df1 = [.. list.Select(x => new[] { RolesOf(x.Id, -1) })];
        e.df2 = [.. list.Select(x => x.Reg?.Activity ?? l.Activity)];
        e.df3 = [.. list.Select(x => x.Reg?.MinItemPower ?? 0)];
        e.df4 = [.. list.Select(x => x.Reg?.MaxGroupSize ?? l.MaxGroupSize)];
        e.df5 = [.. list.Select(x => x.Reg?.Title ?? "")];
        e.df6 = [.. list.Select(x => x.Reg?.Details ?? "")];
        e.df7 = [.. list.Select(x => x.Reg?.Visibility ?? 0)];
        return e;
    }

    private void SendApplicants(FinderListing l) => parties.Online(l.Leader)?.Session.Send(Applicants(l));

    /// <summary>A change of <paramref name="l"/> to its party and its watchers.</summary>
    private void Broadcast(FinderListing l, int kind, Guid? previous = null)
    {
        var e = Update(l, kind, previous);
        foreach (var p in Own(l).Concat(Watchers(l))) p.Session.Send(e);
    }

    // ---- requests ----

    /// <summary>
    /// PartyFinderRegisterForUpdates (359, yo: 0 Activity): the player watches that activity (one at a time, as c3k.f keeps one) and gets
    /// its FullUpdate. No response (capture 002835). An invalid activity is ignored.
    /// </summary>
    public void Register(Player p, int activity)
    {
        if (activity is < 0 or >= Activities) return;
        _subscribed[p.Character.Id] = activity;
        p.Session.Send(FullUpdate(p.Character.Id, activity));
    }

    /// <summary>PartyFinderUnregisterForUpdates (360, yr: 0 Activity): the player stops watching. No response (capture 002835).</summary>
    public void Unregister(Player p, int activity)
    {
        if (_subscribed.TryGetValue(p.Character.Id, out var a) && (a == activity || activity < 0)) _subscribed.Remove(p.Character.Id);
    }

    /// <summary>PartyFinderFulltextSearch (370, yl: 0 Activity, 1 text, 2 int [unknown, ignored]): a FullUpdate of the matching listings [infer]. No response [assume].</summary>
    public void Search(Player p, int activity, string? text)
    {
        if (activity is < 0 or >= Activities) return;
        p.Session.Send(FullUpdate(p.Character.Id, activity, text));
    }

    /// <summary>
    /// PartyFinderEnlistNewPartySearch (361, yk) and ChangePartySearch (363, yh): 0 activity, 1 min IP, 2 max group size, 3 auto accept,
    /// 4 title, 5 details, 6 visibility. In a party but not its leader: 14444 [infer]. Else the leader's listing is made or replaced: the
    /// response first (<paramref name="reply"/>), then Update kind 0 (a new one) or 1 to its party and the activity's watchers; a moved
    /// activity removes it (kind 2) from the old one's watchers. An own registration (368) ends with it (kind 3 to the player) [infer:
    /// the client clears it on sending, c3k.f_5].
    /// </summary>
    public void Enlist(Player p, int activity, int minIp, int maxSize, bool autoAccept, string? title, string? details, int visibility, Action<short> reply)
    {
        var me = p.Character.Id;
        if (parties.Of(me) is { } party && party.Leader != me)
        {
            reply(EnlistInPartyNotLeader);
            return;
        }
        reply(0);
        if (_applicants.Remove(me, out var reg)) p.Session.Send(Update(reg, KindClosed));
        var isNew = !_listings.TryGetValue(me, out var l);
        l ??= new FinderListing(me, p.Character.Name ?? "");
        var oldActivity = l.Activity;
        l.Activity = Math.Clamp(activity, 0, Activities - 1);
        l.MinItemPower = Math.Max(0, minIp);
        l.MaxGroupSize = Math.Clamp(maxSize, 2, parties.MaxSize);
        l.AutoAccept = autoAccept;
        l.Title = Clip(title, MaxTitle);
        l.Details = Clip(details, MaxDetails);
        l.Visibility = visibility is >= 0 and <= 2 ? visibility : 0;
        _listings[me] = l;
        if (!isNew && oldActivity != l.Activity)
        {
            var gone = Update(l, KindRemoved);
            foreach (var w in Watchers(l, oldActivity)) w.Session.Send(gone);
        }
        Broadcast(l, isNew || oldActivity != l.Activity ? KindNew : KindChanged);
        Log.Info("partyfinder", $"'{p.Character.Name}' {(isNew ? "lists" : "changes")} a party search: activity {l.Activity}, size {l.MaxGroupSize}, min IP {l.MinItemPower}{(l.AutoAccept ? ", auto accept" : "")}, visibility {l.Visibility}");
    }

    private static string Clip(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    /// <summary>PartyFinderDeletePartySearch (362, yj): the player's listing goes (<see cref="Remove"/>). rc 0 either way [assume].</summary>
    public void Delete(Player p, Action<short> reply)
    {
        reply(0);
        if (_listings.TryGetValue(p.Character.Id, out var l)) Remove(l, "deleted");
    }

    /// <summary>
    /// A listing goes: Update kind 3 (closes the window) to its party, kind 2 to the watchers, and each pending applicant gets
    /// PartyFinderJoinRequestDeclined (374) {0 key, 1 leader name, 2 true = declined automatically, 3 false}.
    /// </summary>
    private void Remove(FinderListing l, string why)
    {
        if (!_listings.Remove(l.Leader)) return;
        var closed = Update(l, KindClosed);
        foreach (var p in Own(l)) p.Session.Send(closed);
        var removed = Update(l, KindRemoved);
        foreach (var w in Watchers(l)) w.Session.Send(removed);
        foreach (var id in l.Pending) parties.Online(id)?.Session.Send(Declined(l, auto: true, notFit: false));
        l.Pending.Clear();
        Log.Info("partyfinder", $"the party search of '{l.LeaderName}' ends ({why})");
    }

    private static PartyFinderJoinRequestDeclinedEvent Declined(FinderListing l, bool auto, bool notFit) =>
        new() { dgy = l.Leader, dgz = l.LeaderName, dg0 = auto, dg1 = notFit };

    /// <summary>
    /// PartyFinderChangeRole (364, yi: 0 int[3] roles [dps, tank, heal], 1 the party leader's Guid or the own one): the roles are kept and
    /// shown: an Update of the listing the player is in (kind 1), of its own registration (kind 1, to itself), and the leaders it applied
    /// to get their ApplicantsUpdate again. rc 0; a roles array not of length 3 is ignored (rc 0) [assume].
    /// </summary>
    public void ChangeRole(Player p, int[]? roles, Action<short> reply)
    {
        reply(0);
        if (roles is not { Length: 3 }) return;
        var me = p.Character.Id;
        _roles[me] = [.. roles.Select(r => r > 0 ? 1 : 0)];
        Refresh(p);
    }

    /// <summary>
    /// PartyFinderRequestEquipmentSnapshot (371, yp, no keys; the find view and the HUD's refresh): the player's entries are sent again
    /// with its current item power, as the official server answered it with Update kind 1 (capture 002835). No response.
    /// </summary>
    public void Refresh(Player p)
    {
        var me = p.Character.Id;
        if (_applicants.TryGetValue(me, out var reg)) p.Session.Send(Update(reg, KindChanged));
        var leader = parties.Of(me)?.Leader ?? me;
        if (_listings.TryGetValue(leader, out var l)) Broadcast(l, KindChanged);
        foreach (var other in _listings.Values.Where(x => x.Pending.Contains(me))) SendApplicants(other);
    }

    /// <summary>
    /// PartyFinderRegisterApplicants (368, yn: 0 int[3] roles, 1 activity, 2 min IP, 3 max group size, 4 title, 5 details, 6 visibility;
    /// the create view's "find parties"): the player's own registration. rc 0 (with the request id, capture 002835), then Update list 1
    /// kind 0 to it. Its own listing, if any, ends [infer: the client clears it on sending, c3k.f_4].
    /// </summary>
    public void RegisterApplicant(Player p, int[]? roles, int activity, int minIp, int maxSize, string? title, string? details, int visibility, Action<short> reply)
    {
        var me = p.Character.Id;
        reply(0);
        if (_listings.TryGetValue(me, out var own)) Remove(own, "the leader looks for a party instead");
        if (roles is { Length: 3 }) _roles[me] = [.. roles.Select(r => r > 0 ? 1 : 0)];
        var isNew = !_applicants.ContainsKey(me);
        var reg = new FinderApplicant(me, p.Character.Name ?? "")
        {
            Activity = Math.Clamp(activity, 0, Activities - 1), MinItemPower = Math.Max(0, minIp), MaxGroupSize = Math.Clamp(maxSize, 2, parties.MaxSize),
            Title = Clip(title, MaxTitle), Details = Clip(details, MaxDetails), Visibility = visibility is >= 0 and <= 2 ? visibility : 0,
        };
        _applicants[me] = reg;
        p.Session.Send(Update(reg, isNew ? KindNew : KindChanged));
        Log.Info("partyfinder", $"'{p.Character.Name}' looks for a party: activity {reg.Activity}");
    }

    /// <summary>
    /// PartyFinderUnregisterApplicants (369, yq): the registration ends (Update list 1 kind 3 to the player, capture 002835) and its open
    /// applications are withdrawn [assume] (the leaders get their ApplicantsUpdate again). rc 0 without the request id (as captured).
    /// </summary>
    public void UnregisterApplicant(Player p, Action<short> reply)
    {
        var me = p.Character.Id;
        reply(0);
        if (_applicants.Remove(me, out var reg)) p.Session.Send(Update(reg, KindClosed));
        Withdraw(me);
    }

    private void Withdraw(Guid id)
    {
        foreach (var l in _listings.Values.Where(l => l.Pending.Remove(id)).ToList()) SendApplicants(l);
    }

    /// <summary>
    /// PartyFinderApplyForGroup (365, yg: 0 the listing's key). Checked: no such listing or not visible 14602, the own search 14607, the
    /// leader not playing 14600, the applicant in a party (a member 14605, an enlisted leader 14608, another leader 14604: no party merge
    /// [assume]), already pending 14609, the party full 14603, the applicant's item power below the listing's minimum 14610. Then an
    /// auto-accept listing takes it at once (rc 0, the party join follows); else it waits (rc 14611 EnteredPendingPlayers, which the
    /// client keeps as "applied", c3k.b.d) and the leader gets its ApplicantsUpdate.
    /// </summary>
    public short Apply(Player p, Guid key)
    {
        var me = p.Character.Id;
        if (!_listings.TryGetValue(key, out var l) || !Visible(l, me)) return ApplyGroupNotKnown;
        if (key == me || parties.Of(me)?.Leader == key) return ApplyOwnSearch;
        if (parties.Online(l.Leader) is not { } leader) return ApplyLeaderNotKnown;
        if (parties.Of(me) is { } mine) return mine.Leader != me ? ApplyInGroupNotLeader : _listings.ContainsKey(me) ? ApplyEnlistedParty : ApplyPartyDoesNotFit;
        if (l.Pending.Contains(me)) return ApplyAlreadyPending;
        if (Size(l) >= Cap(l)) return ApplyAlreadyFull;
        if (ItemPowerOf(me) < l.MinItemPower) return ApplyItemPowerTooLow;
        if (l.AutoAccept)
        {
            Join(l, leader, p, "auto accept");
            return 0;
        }
        l.Pending.Add(me);
        SendApplicants(l);
        Log.Info("partyfinder", $"'{p.Character.Name}' applies to the party search of '{l.LeaderName}'");
        return ApplyPending;
    }

    /// <summary>
    /// PartyFinderAcceptOrDeclineApplyForGroup (366, yf: 0 applicant Guid, 1 accept) of a listing's leader. No listing 14602, the applicant
    /// not pending 14601. A decline: 374 {key, leader name, false, false} to the applicant ("@PARTYFINDER_JOINREQUEST_DECLINE"), rc 0. An
    /// accept: the applicant gone or in a party meanwhile 14601 (374 automatic); the party full 14604 (374 not fit; the client prints
    /// "@PARTYFINDER_ACCEPTED_REQUEST_DO…", c3k.e.b); else it joins (<see cref="Join"/>), rc 0. The leader gets its ApplicantsUpdate again.
    /// </summary>
    public short Answer(Player leader, Guid applicant, bool accept)
    {
        if (!_listings.TryGetValue(leader.Character.Id, out var l)) return ApplyGroupNotKnown;
        if (!l.Pending.Remove(applicant)) return ApplyApplicantNotKnown;
        var who = parties.Online(applicant);
        short rc = 0;
        if (!accept) who?.Session.Send(Declined(l, auto: false, notFit: false));
        else if (who is null || parties.Of(applicant) is not null)
        {
            who?.Session.Send(Declined(l, auto: true, notFit: false));
            rc = ApplyApplicantNotKnown;
        }
        else if (Size(l) >= Cap(l))
        {
            who.Session.Send(Declined(l, auto: false, notFit: true));
            rc = ApplyPartyDoesNotFit;
        }
        if (_listings.ContainsKey(l.Leader)) SendApplicants(l);
        if (accept && rc == 0) Join(l, leader, who!, "accepted");
        else Log.Info("partyfinder", $"'{leader.Character.Name}' {(accept ? "cannot take" : "declines")} applicant {who?.Character.Name ?? applicant.ToString()}{(rc != 0 ? $" ({rc})" : "")}");
        return rc;
    }

    /// <summary>
    /// <paramref name="joiner"/> joins the listing's party through <see cref="Parties.AdmitFromFinder"/> (a new party led by the leader
    /// when it had none); its own registration ends (kind 3 to it) and its other applications are withdrawn. The party's change updates
    /// the listing (<see cref="PartyChanged"/>), which goes when the party is full.
    /// </summary>
    private void Join(FinderListing l, Player leader, Player joiner, string how)
    {
        var id = joiner.Character.Id;
        l.Pending.Remove(id);
        if (_applicants.Remove(id, out var reg)) joiner.Session.Send(Update(reg, KindClosed));
        Withdraw(id);
        Log.Info("partyfinder", $"'{joiner.Character.Name}' joins the party of '{l.LeaderName}' from the party finder ({how})");
        parties.AdmitFromFinder(leader, joiner);
    }

    /// <summary>
    /// PartyFinderGetEquipmentSnapshot (367, ym: 0 the name of a listed player; the inspect window gmu.a3j): PartyFinderEquipmentSnapshot
    /// (373, av8) to the asker, one entry per EquipmentSlot (gmv.a3g reads 1-4 by slot, gmv.a3h 6): 0 name, 1 item index (0 = empty),
    /// 2 quality, 3 durability (a7m raw [infer: dn7.a7's a7m after the count]), 4 crafter name ("" [assume]), 5 a7m 0 (passed to
    /// c3d.cl with the item [unknown]), 6 mastery level + 1 (slots 0-4, as op 143). Nothing when the player does not play [assume].
    /// </summary>
    public void Snapshot(Player asker, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Player? target = null;
        foreach (var map in world.Maps.Maps)
            foreach (var p in map.Players.Values)
                if (string.Equals(p.Character.Name, name.Trim(), StringComparison.OrdinalIgnoreCase) && (target is null || p.Id > target.Id)) target = p;
        if (target is null) return;
        var eq = target.Inventory.Equipment;
        var n = eq.Size;
        var e = new PartyFinderEquipmentSnapshotEvent
        {
            df8 = target.Character.Name, df9 = new int[n], dga = new byte[n], dgb = new long[n], dgc = [.. Enumerable.Repeat("", n)],
            dgd = new long[n], dge = new byte[n],
        };
        for (var slot = 0; slot < n; slot++)
        {
            if (eq[slot] is not { } item) continue;
            e.df9[slot] = item.Template.Index;
            e.dga[slot] = (byte)item.Quality;
            e.dgb[slot] = item.DurabilityRaw;
            if (slot < 5) e.dge[slot] = (byte)Math.Min(byte.MaxValue, Network.WorldSession.MasteryLevel(target, item) + 1);
        }
        asker.Session.Send(e);
    }

    // ---- the party's side (called by Parties) ----

    /// <summary>
    /// The party of <paramref name="party"/> changed (a member joined or left): its listing shows the new members (kind 1); when the party
    /// has reached the listing's max group size (or Party:MaxSize) the listing goes.
    /// </summary>
    internal void PartyChanged(Party party)
    {
        if (!_listings.TryGetValue(party.Leader, out var l)) return;
        if (party.Members.Count >= Cap(l)) Remove(l, "the party is full");
        else Broadcast(l, KindChanged);
    }

    /// <summary>The party of <paramref name="leader"/> disbanded: its listing goes.</summary>
    internal void PartyGone(Guid leader)
    {
        if (_listings.TryGetValue(leader, out var l)) Remove(l, "the party disbanded");
    }

    /// <summary>
    /// The lead of <paramref name="party"/> passed from <paramref name="old"/>: its listing moves to the new leader (Update kind 1 with key 3
    /// = the old key, which c3k.g removes) [infer: the use of key 3]; its pending applicants follow.
    /// </summary>
    internal void LeaderChanged(Party party, Guid old)
    {
        if (!_listings.Remove(old, out var l)) return;
        if (_listings.TryGetValue(party.Leader, out var other)) Remove(other, "its leader took over another party");
        l.Leader = party.Leader;
        l.LeaderName = party.Find(party.Leader)?.Name ?? "";
        _listings[l.Leader] = l;
        Broadcast(l, KindChanged, old);
    }

    /// <summary>
    /// <paramref name="character"/> logged out: it watches nothing, its registration ends, its applications are withdrawn, and its listing
    /// goes (as the leader of a party, before it leaves the party).
    /// </summary>
    internal void Disconnected(Guid character)
    {
        _subscribed.Remove(character);
        _applicants.Remove(character);
        Withdraw(character);
        if (_listings.TryGetValue(character, out var l)) Remove(l, "the leader logged out");
    }
}

/// <summary>The party finder's side of the parties: the finder lives with them, and its accepted applicants join through <see cref="Admit"/>.</summary>
public sealed partial class Parties
{
    private PartyFinder? _finder;

    /// <summary>The party finder (stageR party SPEC §5).</summary>
    public PartyFinder Finder => _finder ??= new PartyFinder(world, this);

    /// <summary>
    /// An applicant accepted by the party finder (an auto-accept listing or op 366) joins the party of <paramref name="leader"/>, or a new
    /// one it leads, as an accepted invitation or join request does (232 to the old members, 230 to the joiner).
    /// </summary>
    public Party AdmitFromFinder(Player leader, Player joiner)
    {
        var me = joiner.Character.Id;
        var party = Of(leader.Character.Id);
        _droppedAway.Remove(me);
        var created = party is null;
        party = Admit(party, leader, joiner, MemberOf(joiner));
        if (created) world.Modules.Party(true, leader.Character.Id, leader.Character.Name ?? "", party.Id, 2, "created");
        world.Modules.Party(true, me, joiner.Character.Name ?? "", party.Id, party.Members.Count, "party finder");
        return party;
    }
}
