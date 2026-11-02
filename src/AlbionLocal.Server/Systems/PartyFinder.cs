using System.Globalization;
using System.Text;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The native finder, backed by this server's real, online parties. Like Parties, advertisements and applications are
/// session state, not saved characters. All access is under World.Sync. 345319 consumers A874D0/A893F0/A8A6C0:
/// full search list, own search/applicant update (kind 0/1), and the search's pending applicants, respectively.
/// There is no population from bots, official servers, or fabricated equipment. Limits below are local server policy.
/// </summary>
public sealed class PartyFinder(World world)
{
    public const short Invalid = 1, InfoTooLong = 14440, TitleTooLong = 14441, InvalidSize = 14442,
        InvalidPower = 14443, NotLeader = 14444, LeaderGone = 14600, ApplicantGone = 14601,
        SearchGone = 14602, Full = 14603, DoesNotFit = 14604, ApplicantNotLeader = 14605,
        TooManyApplications = 14606, OwnSearch = 14607, AlreadyAdvertising = 14608,
        AlreadyPending = 14609, PowerTooLow = 14610, Pending = 14611, Blocked = 14612;

    private sealed record Terms(int Activity, int MinPower, int MaxSize, bool AutoAccept, string Title, string Info, int Visibility);
    private sealed record Member(Guid Id, string Name, int Avatar, byte Frame, byte Flag, float Power, int[] Roles);
    // Native Party Finder retains the session's equipment until the member explicitly refreshes it.
    // These are slot values, not inventory items; response arrays never expose the retained arrays.
    private sealed class EquipmentSnapshot(float power, int[] types, byte[] qualities, long[] durability, string[] souls, byte[] mastery)
    {
        public float Power { get; } = power;

        public PartyFinderEquipmentSnapshotEvent Event(string name) => new()
        {
            df8 = name, df9 = [.. types], dga = [.. qualities], dgb = [.. durability], dgc = [.. souls],
            dgd = new long[types.Length], dge = [.. mastery],
        };
    }
    private sealed class Entry(Guid leader, Terms terms, bool applicant)
    {
        public Guid Id { get; } = leader;
        public Terms Terms { get; set; } = terms;
        public bool Applicant { get; } = applicant;
        public Member[] Members { get; set; } = [];
        public string State { get; set; } = "";
        public TimeSpan? AwaySince { get; set; }
    }
    private sealed record Query(int Activity, string Text, int Visibility);
    private readonly Dictionary<Guid, Entry> _searches = [];
    private readonly Dictionary<Guid, Entry> _applicants = [];
    private readonly HashSet<(Guid Search, Guid Applicant)> _pending = [];
    private readonly Dictionary<Guid, Query> _watchers = [];
    private readonly Dictionary<Guid, (Guid Guild, Guid Alliance)> _viewerAffiliations = [];
    private readonly Dictionary<Guid, int[]> _roles = [];
    private readonly Dictionary<Guid, EquipmentSnapshot> _equipment = [];
    private TimeSpan _nextRefresh;

    private Guid LeaderOf(Guid id) => world.Parties.Of(id)?.Leader ?? id;
    private bool IsLeader(Player p) => !p.IsBot && LeaderOf(p.Character.Id) == p.Character.Id;
    private static bool Activity(int n, bool all = false) => n is >= 0 and < 7 || all && n == -1;
    private static bool Text(string text) => !text.Any(c => char.IsControl(c) || c is '[' or ']' or '<' or '>');
    // The client uses three independent role weights, not a list of item-role enum values (F65F20).
    private static bool Roles(int[]? roles) => roles is { Length: 3 } && roles.All(x => x is >= 0 and <= 1);

    private static EquipmentSnapshot CaptureEquipment(Player p)
    {
        var equipment = p.Inventory.Equipment;
        var types = new int[equipment.Size];
        var qualities = new byte[equipment.Size];
        var durability = new long[equipment.Size];
        var souls = Enumerable.Repeat("", equipment.Size).ToArray();
        var mastery = new byte[equipment.Size];
        for (var i = 0; i < equipment.Size; i++)
        {
            if (equipment[i] is not { } item) continue;
            types[i] = item.Template.Index;
            qualities[i] = (byte)item.Quality;
            durability[i] = item.DurabilityRaw;
            souls[i] = item.Soul?.Id.ToString() ?? "";
            if (i < 5) mastery[i] = (byte)Math.Min(byte.MaxValue, WorldSession.MasteryLevel(p, item) + 1);
        }
        return new EquipmentSnapshot((float)WorldSession.AverageItemPower(p), types, qualities, durability, souls, mastery);
    }

    private Member[]? Members(Guid leader, bool captureEquipment = false)
    {
        var party = world.Parties.Of(leader);
        if (party is not null && (party.Leader != leader || party.Members.Any(x => x.Companion))) return null;
        Guid[] ids = party is null ? [leader] : party.Members.Select(x => x.Id).ToArray();
        var members = new List<Member>();
        foreach (var id in ids)
        {
            if (world.Parties.Online(id) is not { IsBot: false } p) return null;
            float power;
            if (_equipment.TryGetValue(id, out var snapshot)) power = snapshot.Power;
            else if (captureEquipment)
            {
                _equipment[id] = snapshot = CaptureEquipment(p);
                power = snapshot.Power;
            }
            else power = 0; // Validation only counts online members; it does not start or refresh their finder session.
            members.Add(new Member(id, p.Character.Name, p.Character.Template, 0, p.Flag,
                power, _roles.GetValueOrDefault(id) ?? [0, 0, 0]));
        }
        return [.. members];
    }

    private short Validate(Player p, int activity, int power, int size, string title, string info, int visibility)
    {
        if (!IsLeader(p)) return NotLeader;
        if (!Activity(activity) || visibility is < 0 or > 2 || !Text(title) || !Text(info)) return Invalid;
        if (title.Length > 100) return TitleTooLong;
        if (info.Length > 1000) return InfoTooLong;
        if (power is < 0 or > 10000) return InvalidPower;
        if (size < 2 || size > world.Parties.MaxSize || Members(p.Character.Id) is not { } members || members.Length > size) return InvalidSize;
        return 0;
    }

    public short Enlist(Player p, int activity, int power, int size, bool auto, string? title, string? info, int visibility, bool change)
    {
        title ??= ""; info ??= "";
        var rc = Validate(p, activity, power, size, title, info, visibility);
        if (rc != 0) return rc;
        var id = p.Character.Id;
        if (change && !_searches.ContainsKey(id)) return SearchGone;
        RemoveApplicant(id);
        var terms = new Terms(activity, power, size, auto, title, info, visibility);
        if (_searches.TryGetValue(id, out var entry)) entry.Terms = terms;
        else _searches[id] = new Entry(id, terms, false);
        Refresh(true);
        return 0;
    }

    public short RegisterApplicant(Player p, int[]? roles, int activity, int power, int size, string? title, string? info, int visibility)
    {
        title ??= ""; info ??= "";
        if (!Roles(roles)) return Invalid;
        var rc = Validate(p, activity, power, size, title, info, visibility);
        if (rc != 0) return rc;
        var id = p.Character.Id;
        RemoveSearch(id);
        _roles[id] = [.. roles!];
        var terms = new Terms(activity, power, size, false, title, info, visibility);
        if (_applicants.TryGetValue(id, out var entry)) entry.Terms = terms;
        else _applicants[id] = new Entry(id, terms, true);
        Refresh(true);
        return 0;
    }

    public short Delete(Player p, bool applicant)
    {
        if (!IsLeader(p)) return NotLeader;
        if (applicant) RemoveApplicant(p.Character.Id); else RemoveSearch(p.Character.Id);
        Refresh(true);
        return 0;
    }

    public short ChangeRole(Player p, Guid group, int[]? roles)
    {
        if (!Roles(roles)) return Invalid;
        // 345319 HUD F6DF50: key1 is the party leader Guid (self when solo), not the member whose roles change.
        // Each member changes its own roles; the group id prevents applying a stale HUD action to a different party.
        if (group != LeaderOf(p.Character.Id)) return ApplicantGone;
        if (!_searches.ContainsKey(group) && !_applicants.ContainsKey(group)) return ApplicantGone;
        _roles[p.Character.Id] = [.. roles!];
        Refresh(true);
        return 0;
    }

    public short Watch(Player p, int activity, string? text = null, int visibility = 0)
    {
        text ??= "";
        if (!Activity(activity, true) || visibility is < 0 or > 2 || text.Length > 128 || !Text(text)) return Invalid;
        Refresh(false);
        _watchers[p.Character.Id] = new Query(activity, text.Trim(), visibility);
        SendFull(p, _watchers[p.Character.Id]);
        var leader = LeaderOf(p.Character.Id);
        if (_searches.GetValueOrDefault(leader) is { } search)
        {
            p.Session.Send(Update(search));
            SendApplicants(search);
        }
        else if (_applicants.GetValueOrDefault(leader) is { } applicant) p.Session.Send(Update(applicant));
        return 0;
    }

    public void Unwatch(Player p, int activity)
    {
        if (_watchers.GetValueOrDefault(p.Character.Id) is { } query && query.Activity == activity)
        {
            _watchers.Remove(p.Character.Id);
            _viewerAffiliations.Remove(p.Character.Id);
        }
    }

    private bool Visible(Entry entry, Guid viewer) => entry.Id == LeaderOf(viewer) || (entry.Terms.Visibility switch
    {
        0 => true,
        1 => world.Guilds.Same(entry.Id, viewer),
        2 => world.Guilds.Same(entry.Id, viewer) || world.Alliances.SameAlliance(entry.Id, viewer),
        _ => false,
    });

    private bool Matches(Entry e, Guid viewer, Query q) => Visible(e, viewer)
        && (q.Activity == -1 || e.Terms.Activity == q.Activity)
        && (q.Visibility == 0 || q.Visibility == 1 && world.Guilds.Same(e.Id, viewer)
            || q.Visibility == 2 && (world.Guilds.Same(e.Id, viewer) || world.Alliances.SameAlliance(e.Id, viewer)))
        && (q.Text.Length == 0 || e.Terms.Title.Contains(q.Text, StringComparison.OrdinalIgnoreCase)
            || e.Terms.Info.Contains(q.Text, StringComparison.OrdinalIgnoreCase)
            || e.Members.Any(m => m.Name.Contains(q.Text, StringComparison.OrdinalIgnoreCase)));

    private short CanJoin(Entry search, Entry applicant)
    {
        if (search.Id == applicant.Id) return OwnSearch;
        if (world.Parties.Online(search.Id) is not { IsBot: false }) return LeaderGone;
        if (world.Parties.Online(applicant.Id) is not { IsBot: false }) return ApplicantGone;
        if (LeaderOf(search.Id) != search.Id) return SearchGone;
        if (LeaderOf(applicant.Id) != applicant.Id) return ApplicantNotLeader;
        if (_searches.ContainsKey(applicant.Id)) return AlreadyAdvertising;
        if (!Visible(search, applicant.Id)) return SearchGone;
        if (Members(search.Id, true) is not { } host || Members(applicant.Id, true) is not { } guests) return DoesNotFit;
        if (host.Any(h => guests.Any(g => h.Id == g.Id))) return OwnSearch;
        if (host.Length >= search.Terms.MaxSize) return Full;
        if (host.Length + guests.Length > Math.Min(search.Terms.MaxSize, world.Parties.MaxSize)) return DoesNotFit;
        if (guests.Any(g => g.Power < search.Terms.MinPower)) return PowerTooLow;
        if (host.Any(h => guests.Any(g => world.Social.Muted(h.Id, g.Id) || world.Social.Muted(g.Id, h.Id)))) return Blocked;
        return 0;
    }

    public short Apply(Player p, Guid searchId)
    {
        if (!IsLeader(p)) return ApplicantNotLeader;
        if (searchId == p.Character.Id) return OwnSearch;
        if (_searches.ContainsKey(p.Character.Id)) return AlreadyAdvertising;
        if (!_searches.TryGetValue(searchId, out var search)) return SearchGone;
        if (!_applicants.TryGetValue(p.Character.Id, out var applicant)) return ApplicantGone;
        var pair = (searchId, p.Character.Id);
        if (_pending.Contains(pair)) return AlreadyPending;
        if (_pending.Count(x => x.Applicant == p.Character.Id) >= 10) return TooManyApplications;
        var rc = CanJoin(search, applicant);
        if (rc != 0) return rc;
        if (search.Terms.AutoAccept) return Join(search, applicant);
        _pending.Add(pair);
        Refresh(true);
        // The native callback distinguishes queued applications from immediate joins with this return code.
        return Pending;
    }

    public short Answer(Player p, Guid applicantId, bool accept)
    {
        if (!IsLeader(p)) return NotLeader;
        if (!_searches.TryGetValue(p.Character.Id, out var search)) return SearchGone;
        if (!_pending.Contains((search.Id, applicantId)) || !_applicants.TryGetValue(applicantId, out var applicant)) return ApplicantGone;
        if (!accept)
        {
            _pending.Remove((search.Id, applicantId));
            Declined(search, applicant, false, false);
            Refresh(true);
            return 0;
        }
        var rc = CanJoin(search, applicant);
        if (rc != 0)
        {
            _pending.Remove((search.Id, applicantId));
            Declined(search, applicant, false, true);
            Refresh(true);
            return rc;
        }
        return Join(search, applicant);
    }

    private short Join(Entry search, Entry applicant)
    {
        if (world.Parties.Online(search.Id) is not { } host || world.Parties.Online(applicant.Id) is not { } guest) return ApplicantGone;
        if (!world.Parties.JoinFinder(host, guest, search.Terms.MaxSize)) return DoesNotFit;
        RemoveApplicant(applicant.Id);
        Refresh(true);
        return 0;
    }

    private void Declined(Entry search, Entry applicant, bool automatic, bool noLongerFits)
    {
        SendMembers(applicant, new PartyFinderJoinRequestDeclinedEvent
        {
            dgy = search.Id, dgz = search.Members.FirstOrDefault(m => m.Id == search.Id)?.Name ?? "",
            dg0 = automatic, dg1 = noLongerFits,
        });
    }

    private void RemoveSearch(Guid id)
    {
        if (!_searches.Remove(id, out var search)) return;
        SendMembers(search, Update(search, 2));
        foreach (var pair in _pending.Where(x => x.Search == id).ToArray())
        {
            _pending.Remove(pair);
            if (_applicants.GetValueOrDefault(pair.Applicant) is { } applicant) Declined(search, applicant, true, false);
        }
    }

    private void RemoveApplicant(Guid id)
    {
        if (!_applicants.Remove(id, out var applicant)) return;
        SendMembers(applicant, Update(applicant, 2));
        _pending.RemoveWhere(x => x.Applicant == id);
    }

    public void Disconnected(Guid id)
    {
        _watchers.Remove(id);
        _viewerAffiliations.Remove(id);
        _roles.Remove(id);
        RemoveSearch(id);
        RemoveApplicant(id);
        Refresh(true);
    }

    public void Update(TimeSpan now)
    {
        if (now < _nextRefresh || _searches.Count + _applicants.Count == 0) return;
        _nextRefresh = now + TimeSpan.FromSeconds(1);
        Refresh(false);
    }

    private void Refresh(bool force)
    {
        var changed = force;
        foreach (var id in _watchers.Keys)
            if (_viewerAffiliations.GetValueOrDefault(id) != Affiliations(id)) changed = true;
        foreach (var entry in _searches.Values.Concat(_applicants.Values).ToArray())
        {
            if (LeaderOf(entry.Id) != entry.Id || world.Parties.Of(entry.Id)?.Members.Any(x => x.Companion) == true)
            {
                if (entry.Applicant) RemoveApplicant(entry.Id); else RemoveSearch(entry.Id);
                changed = true;
                continue;
            }
            if (Members(entry.Id, true) is not { } members)
            {
                entry.AwaySince ??= world.Now;
                if (world.Now - entry.AwaySince.Value >= Parties.OfflineGrace)
                {
                    if (entry.Applicant) RemoveApplicant(entry.Id); else RemoveSearch(entry.Id);
                    changed = true;
                }
                continue;
            }
            entry.AwaySince = null;
            var state = State(entry, members);
            if (state == entry.State && !force) continue;
            // Departed members must lose their own finder model even if the leader still advertises.
            foreach (var old in entry.Members.Where(m => !members.Any(n => n.Id == m.Id)))
                world.Parties.Online(old.Id)?.Session.Send(Update(entry, 2));
            entry.Members = members;
            entry.State = state;
            SendMembers(entry, Update(entry));
            changed = true;
        }
        // Keep captures across an applicant joining an advertised party, but end them when the member leaves the finder.
        var registered = _searches.Values.Concat(_applicants.Values).SelectMany(e => e.Members).Select(m => m.Id).ToHashSet();
        foreach (var id in _equipment.Keys.Where(id => !registered.Contains(id)).ToArray()) _equipment.Remove(id);
        if (!changed) return;
        foreach (var pair in _pending.ToArray())
        {
            if (!_searches.TryGetValue(pair.Search, out var search) || !_applicants.TryGetValue(pair.Applicant, out var applicant))
            { _pending.Remove(pair); continue; }
            if (search.AwaySince is null && applicant.AwaySince is null && CanJoin(search, applicant) != 0)
            {
                _pending.Remove(pair);
                Declined(search, applicant, false, true);
            }
        }
        foreach (var (id, query) in _watchers.ToArray())
            if (world.Parties.Online(id) is { } p) SendFull(p, query);
        foreach (var entry in _searches.Values) SendApplicants(entry);
    }

    private string State(Entry e, Member[] members)
    {
        var b = new StringBuilder().Append(e.Terms);
        foreach (var m in members)
            b.Append('|').Append(m.Id).Append(':').Append(m.Name).Append(':').Append(m.Avatar).Append(':').Append(m.Flag)
                .Append(':').Append(m.Power.ToString("R", CultureInfo.InvariantCulture)).Append(':').AppendJoin(',', m.Roles)
                .Append(':').Append(world.Guilds.Of(m.Id)?.Id).Append(':').Append(world.Alliances.Of(m.Id)?.Id);
        return b.ToString();
    }

    private void SendMembers(Entry entry, AlbionEvent e)
    {
        foreach (var m in entry.Members)
            world.Parties.Online(m.Id)?.Session.Send(e);
    }

    private static PartyFinderUpdateEvent Update(Entry e, int change = 1) => new()
    {
        dg2 = change, dg3 = e.Applicant ? 1 : 0, dg4 = e.Id,
        dg6 = e.Members.FirstOrDefault(m => m.Id == e.Id)?.Name ?? "",
        dg7 = [.. e.Members.Select(m => m.Name)], dg8 = [.. e.Members.Select(m => m.Avatar)], dg9 = new int[e.Members.Length],
        dha = [.. e.Members.Select(m => m.Flag)], dhb = [.. e.Members.Select(m => m.Frame)],
        dhc = [.. e.Members.Select(m => m.Power)], dhd = [.. e.Members.Select(m => m.Roles)],
        dhe = e.Terms.Activity, dhf = e.Terms.MinPower, dhg = e.Terms.MaxSize, dhh = e.Terms.AutoAccept,
        dhi = e.Terms.Title, dhj = e.Terms.Info, dhk = e.Terms.Visibility,
    };

    private void SendFull(Player p, Query query)
    {
        _viewerAffiliations[p.Character.Id] = Affiliations(p.Character.Id);
        var rows = _searches.Values.Where(e => e.Members.Length > 0 && Matches(e, p.Character.Id, query)).OrderBy(e => e.Id).ToArray();
        p.Session.Send(new PartyFinderFullUpdateEvent
        {
            dgh = [.. rows.Select(e => e.Id)], dgi = [.. rows.Select(e => e.Members.First(m => m.Id == e.Id).Name)],
            dgj = [.. rows.Select(e => e.Members.Select(m => m.Name).ToArray())],
            dgk = [.. rows.Select(e => e.Members.Select(m => m.Avatar).ToArray())], dgl = [.. rows.Select(e => new int[e.Members.Length])],
            dgm = [.. rows.Select(e => e.Members.Select(m => m.Flag).ToArray())], dgn = [.. rows.Select(e => e.Members.Select(m => m.Frame).ToArray())],
            dgo = [.. rows.Select(e => e.Members.Select(m => m.Power).ToArray())], dgp = [.. rows.Select(e => e.Members.Select(m => m.Roles).ToArray())],
            dgq = [.. rows.Select(e => e.Terms.Activity)], dgr = [.. rows.Select(e => e.Terms.AutoAccept)],
            dgs = [.. rows.Select(e => e.Terms.MinPower)], dgt = [.. rows.Select(e => e.Terms.MaxSize)],
            dgu = [.. rows.Select(e => e.Terms.Title)], dgv = [.. rows.Select(e => e.Terms.Info)], dgw = [.. rows.Select(e => e.Terms.Visibility)],
            dgx = [.. rows.Select(e => _pending.Contains((e.Id, LeaderOf(p.Character.Id))))],
        });
    }

    private (Guid Guild, Guid Alliance) Affiliations(Guid id) =>
        (world.Guilds.Of(id)?.Id ?? Guid.Empty, world.Alliances.Of(id)?.Id ?? Guid.Empty);

    private void SendApplicants(Entry search)
    {
        var rows = _pending.Where(x => x.Search == search.Id).Select(x => _applicants.GetValueOrDefault(x.Applicant))
            .OfType<Entry>().Where(e => e.Members.Length > 0).OrderBy(e => e.Id).ToArray();
        SendMembers(search, new PartyFinderApplicantsUpdateEvent
        {
            dft = [.. rows.Select(e => e.Id)], dfu = [.. rows.Select(e => e.Members.First(m => m.Id == e.Id).Name)],
            dfv = [.. rows.Select(e => e.Members.Select(m => m.Name).ToArray())],
            dfw = [.. rows.Select(e => e.Members.Select(m => m.Avatar).ToArray())], dfx = [.. rows.Select(e => new int[e.Members.Length])],
            dfy = [.. rows.Select(e => e.Members.Select(m => m.Flag).ToArray())], dfz = [.. rows.Select(e => e.Members.Select(m => m.Frame).ToArray())],
            df0 = [.. rows.Select(e => e.Members.Select(m => m.Power).ToArray())], df1 = [.. rows.Select(e => e.Members.Select(m => m.Roles).ToArray())],
            df2 = [.. rows.Select(e => e.Terms.Activity)], df3 = [.. rows.Select(e => e.Terms.MinPower)], df4 = [.. rows.Select(e => e.Terms.MaxSize)],
            df5 = [.. rows.Select(e => e.Terms.Title)], df6 = [.. rows.Select(e => e.Terms.Info)], df7 = [.. rows.Select(e => e.Terms.Visibility)],
        });
    }

    public PartyFinderEquipmentSnapshotEvent? Inspect(Player viewer, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var leader = LeaderOf(viewer.Character.Id);
        var entries = _searches.Values.Where(e => Visible(e, viewer.Character.Id))
            .Concat(_applicants.Values.Where(e => e.Id == leader || _pending.Contains((leader, e.Id))));
        var member = entries.SelectMany(e => e.Members).FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        return member is null ? null : _equipment.GetValueOrDefault(member.Id)?.Event(member.Name);
    }

    public void RefreshEquipment(Player p)
    {
        if (!_searches.ContainsKey(LeaderOf(p.Character.Id)) && !_applicants.ContainsKey(LeaderOf(p.Character.Id))) return;
        _equipment[p.Character.Id] = CaptureEquipment(p);
        Refresh(true);
    }
}
