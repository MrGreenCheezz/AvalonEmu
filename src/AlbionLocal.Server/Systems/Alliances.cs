using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The alliances of guilds (analysis/work/stageP/hideouts/SPEC.md §1.2, §2.1-§2.3, §4, phase A1): create (AllianceCreate 208, paid from
/// the guild account, RCs 8320-8330), disband (209, the founder guild), leave (215), the member list (210) and the detail info (217), the
/// alliance keys of the guild messages (GuildUpdate kinds 0/5 keys 16/17, Join 79/80, NewCharacter 51/52, GuildMemberWorldUpdate 11/12)
/// and the friend test of <see cref="Pvp"/> (same guild or same alliance, as the client's a5g.d_1). A guild acts through its members
/// with ALLIANCE_MANAGEMENT (the guild master, The Right Hand). In memory by id and guild; every change is written at once to the alliance
/// store (none in hand-made test worlds). Phase A2 (invitations, kicks, limits) is Alliances.Membership.cs. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Alliances
{
    private readonly World world;
    private readonly IAllianceStore? store;
    private readonly Dictionary<Guid, Alliance> _byId = [];
    private readonly Dictionary<Guid, Alliance> _byGuild = [];
    private readonly Dictionary<Guid, AllianceHistory> _history = []; // by guild
    private readonly List<(Guid Guild, Guid Alliance, TimeSpan Until)> _protected = []; // Alliances:LeaveProtectionSeconds, runtime only

    /// <summary>A refusal without a client text (disband, leave, an unknown alliance) [choice: SPEC §2.1 "not traced"].</summary>
    public const short Refused = 1;

    // AllianceCreate's RCs (GuildCreateAllianceView_c_a 0x1810a9690, SPEC §2.1).
    public const short CreateNotEnoughSilver = 8320, CreateNameNotAllowed = 8321, CreateNameProfanity = 8322, CreateInvalidTag = 8323,
        CreateTagProfanity = 8324, CreateNoRights = 8325, CreateNameTaken = 8326, CreateTagTaken = 8327, CreateNotInGuild = 8328,
        CreateAlreadyInAlliance = 8329, CreateCooldown = 8330;

    /// <summary>GuildUpdate key 10 kind 5: the guild's alliance changed (stageH guilds SPEC §2.2: keys 16 and 17).</summary>
    public const byte KindAlliance = 5;

    public Alliances(World world, IAllianceStore? store)
    {
        this.world = world;
        this.store = store;
        if (store is null) return;
        var guilds = world.Guilds.All.Select(g => g.Id).ToHashSet();
        foreach (var a in store.LoadAlliances())
        {
            // A guild row goes with its guild (ON DELETE CASCADE): an alliance left empty goes, one without its founder gets the oldest guild.
            if (a.Guilds.RemoveAll(m => !guilds.Contains(m.Guild)) > 0) Save(a);
            if (a.Guilds.Count == 0)
            {
                store.DeleteAlliance(a.Id);
                Log.Info("alliance", $"alliance '{a.Name}' has no guilds left: deleted");
                continue;
            }
            if (a.Find(a.Founder) is null)
            {
                a.Founder = a.Guilds[0].Guild;
                Save(a);
            }
            _byId[a.Id] = a;
            foreach (var m in a.Guilds) _byGuild[m.Guild] = a;
        }
        foreach (var h in store.LoadAllianceHistory()) _history[h.Guild] = h;
        LoadInvites();
        if (_byId.Count > 0) Log.Info("alliance", $"{_byId.Count} alliance(s) loaded, {_byGuild.Count} guild(s)");
    }

    private ServerConfig Config => world.Config;

    private Guilds Guilds => world.Guilds;

    public bool Enabled => Config.AlliancesEnabled && Guilds.Enabled;

    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>The alliances in creation order.</summary>
    public IEnumerable<Alliance> All => _byId.Values;

    public Alliance? ById(Guid id) => Enabled ? _byId.GetValueOrDefault(id) : null;

    /// <summary>The alliance of guild <paramref name="guild"/>; null when in none (or alliances are off).</summary>
    public Alliance? OfGuild(Guid guild) => Enabled ? _byGuild.GetValueOrDefault(guild) : null;

    /// <summary>The alliance of character <paramref name="character"/> (through its guild); null when none.</summary>
    public Alliance? Of(Guid character) => Guilds.Of(character) is { } g ? OfGuild(g.Id) : null;

    /// <summary>The alliance called <paramref name="text"/> by name or tag (ignoring case); null when none.</summary>
    public Alliance? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        return _byId.Values.FirstOrDefault(a => string.Equals(a.Name, t, StringComparison.OrdinalIgnoreCase))
            ?? _byId.Values.FirstOrDefault(a => string.Equals(a.Tag, t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A and B are in guilds of one alliance (the spell condition alliancemember; the client's a5g.d_1 G).</summary>
    public bool SameAlliance(Guid a, Guid b) => Of(a) is { } x && Of(b) == x;

    /// <summary>
    /// A and B are allies for <see cref="Pvp"/>: in one alliance, or one's guild left the other's alliance (or both left the same one)
    /// within Alliances:LeaveProtectionSeconds [H].
    /// </summary>
    public bool Allied(Guid a, Guid b)
    {
        if (!Enabled || Guilds.Of(a) is not { } ga || Guilds.Of(b) is not { } gb) return false;
        var (aa, ab) = (OfGuild(ga.Id), OfGuild(gb.Id));
        if (aa is not null && aa == ab) return true;
        if (_protected.Count == 0) return false;
        var now = world.Clock.Now;
        foreach (var p in _protected)
        {
            if (p.Until <= now) continue;
            if (p.Guild == ga.Id && (ab?.Id == p.Alliance || LeftRecently(gb.Id, p.Alliance, now))) return true;
            if (p.Guild == gb.Id && aa?.Id == p.Alliance) return true;
        }
        return false;
    }

    private bool LeftRecently(Guid guild, Guid alliance, TimeSpan now) => _protected.Any(p => p.Guild == guild && p.Alliance == alliance && p.Until > now);

    /// <summary>The alliance text of the nameplate keys (Alliances:PlateText: the name or the tag); "" = none.</summary>
    public string PlateOf(Alliance? a) => a is null ? "" : Config.AlliancePlateText == AlliancePlateText.Tag ? a.Tag : a.Name;

    /// <summary>The members of every guild of <paramref name="a"/> together (Alliances:MaxPlayers).</summary>
    public int Players(Alliance a) => a.Guilds.Sum(m => Guilds.ById(m.Guild)?.Members.Count ?? 0);

    /// <summary>Whether the member <paramref name="character"/> of <paramref name="g"/> may act for it in alliances (GuildPermission ALLIANCE_MANAGEMENT).</summary>
    public static bool MayManage(Guild g, Guid character) =>
        g.Find(character) is { } m && (GuildRoles.Permissions(m.Role) & GuildRoles.AllianceManagement) != 0;

    private void Save(Alliance a) => store?.SaveAlliance(a);

    private void SaveHistory(AllianceHistory h)
    {
        _history[h.Guild] = h;
        try { store?.SaveAllianceHistory(h); }
        catch (Exception ex) { Log.Warn("alliance", $"alliance history of guild {h.Guild} not saved: {ex.Message}"); }
    }

    /// <summary>The end of <paramref name="guild"/>'s change cooldown (Alliances:ChangeCooldownMinutes after it founded or joined one); null = none.</summary>
    private DateTime? ChangeCooldownEnd(Guid guild)
    {
        if (Config.AllianceChangeCooldownMinutes <= 0 || !_history.TryGetValue(guild, out var h)) return null;
        var end = h.Changed.AddMinutes(Config.AllianceChangeCooldownMinutes);
        return end > UtcNow ? end : null;
    }

    /// <summary>The end of <paramref name="guild"/>'s rejoin cooldown for <paramref name="alliance"/> (after it left that one); null = none.</summary>
    private DateTime? RejoinCooldownEnd(Guid guild, Guid alliance)
    {
        if (Config.AllianceChangeCooldownMinutes <= 0 || !_history.TryGetValue(guild, out var h) || h.LastAlliance != alliance || h.LeftAt is not { } left) return null;
        var end = left.AddMinutes(Config.AllianceChangeCooldownMinutes);
        return end > UtcNow ? end : null;
    }

    // ---- events (SPEC §2.3) ----

    /// <summary>
    /// GuildUpdate kind 5 (cxn.aft: alliance changed, stageH guilds SPEC §2.2): 0 the guild, 10 kind 5, 15 the guild's name, 16 the
    /// alliance's text (<see cref="PlateOf"/>; "" when it has none), 17 the alliance's Guid (left out = none), and the later updates'
    /// scalars as the account update (28/32/34/36 "").
    /// </summary>
    public GuildUpdateEvent AllianceUpdate(Guild g)
    {
        var a = OfGuild(g.Id);
        return new GuildUpdateEvent { cf1 = g.Id, cgb = KindAlliance, cgg = g.Name, cgh = PlateOf(a), cgi = a?.Id ?? Guid.Empty, cgt = "", cgx = "", cgz = "", cg1 = "" };
    }

    /// <summary>
    /// The alliance of <paramref name="g"/> changed: GuildUpdate kind 5 to every member (one in the middle of a cluster change gets the full
    /// guild at its arrival), the members' chat connections follow (the Alliance channel) and the nameplates of those in game (106).
    /// </summary>
    private void Changed(Guild g)
    {
        Guilds.SendToMembers(g, AllianceUpdate(g));
        foreach (var m in g.Members)
        {
            world.Chat.GuildChanged(m.Id);
            if (world.Parties.Online(m.Id) is { } p) Guilds.Announce(p);
        }
    }

    // ---- flows (SPEC §4) ----

    /// <summary>Names of 3-30 latin letters, digits, '-', '_' and single inner spaces (SPEC §1.2).</summary>
    public static bool ValidName(string name) =>
        name.Length is >= 3 and <= 30 && name == name.Trim() && !name.Contains("  ") && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ' ' or '-' or '_');

    /// <summary>Tags of 2-5 latin letters and digits (SPEC §1.2).</summary>
    public static bool ValidTag(string tag) => tag.Length is >= 2 and <= 5 && tag.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// AllianceCreate (208) {0 name, 1 tag [H order]}: not in a guild 8328, its guild in an alliance 8329, no ALLIANCE_MANAGEMENT 8325, a bad
    /// name 8321 or tag 8323, a taken name 8326 or tag 8327 (ignoring case), the guild's change cooldown 8330 (<paramref name="cooldownEnd"/> =
    /// response key 0), too little silver in the guild account for Alliances:CreateCost 8320. On 0 the account pays, the guild is the
    /// founder and its members get GuildUpdate kind 5 (the client's own create callback does nothing on 0).
    /// </summary>
    public short Create(Player p, string? rawName, string? rawTag, out long cooldownEnd)
    {
        cooldownEnd = 0;
        if (!Enabled) return CreateNoRights;
        var me = p.Character.Id;
        if (Guilds.Of(me) is not { } g) return CreateNotInGuild;
        if (OfGuild(g.Id) is not null) return CreateAlreadyInAlliance;
        if (!MayManage(g, me)) return CreateNoRights;
        var (name, tag) = ((rawName ?? "").Trim(), (rawTag ?? "").Trim());
        if (!ValidName(name)) return CreateNameNotAllowed;
        if (!ValidTag(tag)) return CreateInvalidTag;
        if (_byId.Values.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) return CreateNameTaken;
        if (_byId.Values.Any(a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase))) return CreateTagTaken;
        if (ChangeCooldownEnd(g.Id) is { } end)
        {
            cooldownEnd = end.Ticks;
            return CreateCooldown;
        }
        var cost = checked(Config.AllianceCreateCost * 10000);
        if (g.SilverA7m < cost) return CreateNotEnoughSilver;
        var now = UtcNow;
        var alliance = new Alliance(Guid.NewGuid(), name, tag, g.Id, now);
        alliance.Guilds.Add(new AllianceGuild(g.Id, now));
        // The rows first: a failed write costs nothing; then the guild account, saved at once.
        try { Save(alliance); }
        catch (Exception ex)
        {
            Log.Warn("alliance", $"alliance '{name}' not saved: {ex.Message}");
            return Refused;
        }
        if (!Guilds.SpendAccount(g, cost, $"alliance '{name}' founded"))
        {
            store?.DeleteAlliance(alliance.Id);
            return CreateNotEnoughSilver;
        }
        _byId[alliance.Id] = alliance;
        _byGuild[g.Id] = alliance;
        SaveHistory(new AllianceHistory(g.Id, now, _history.GetValueOrDefault(g.Id)?.LastAlliance ?? Guid.Empty, _history.GetValueOrDefault(g.Id)?.LeftAt));
        Changed(g);
        Mailer.GuildNotice(world, g, "Alliance founded", $"{p.Character.Name} founded the alliance '{name}' [{tag}].");
        Log.Info("alliance", $"'{p.Character.Name}' of guild '{g.Name}' founds alliance '{name}' [{tag}] ({alliance.Id})");
        return 0;
    }

    /// <summary>The alliance <paramref name="id"/> a member of the founder guild with ALLIANCE_MANAGEMENT acts on; null = refused.</summary>
    private (Guild, Alliance)? Founding(Player p, Guid id)
    {
        if (!Enabled || Guilds.Of(p.Character.Id) is not { } g || OfGuild(g.Id) is not { } a || (id != Guid.Empty && a.Id != id)) return null;
        return a.Founder == g.Id && MayManage(g, p.Character.Id) ? (g, a) : null;
    }

    /// <summary>
    /// AllianceDisband (209) {0 alliance}: only the founder guild (ALLIANCE_MANAGEMENT), else rc 1. Every guild leaves (kind 5 to each,
    /// Alliances:LeaveProtectionSeconds between them), the open invitations go with their mails, the alliance is deleted.
    /// </summary>
    public short Disband(Player p, Guid id)
    {
        if (Founding(p, id) is not var (_, a)) return Refused;
        Disband(a, $"'{p.Character.Name}'");
        return 0;
    }

    /// <summary>The alliance is gone (its founder disbanded it, the GM, or its last guild went).</summary>
    public void Disband(Alliance a, string by)
    {
        var now = UtcNow;
        _byId.Remove(a.Id);
        foreach (var invite in InvitesOf(a).ToList()) DropInvite(invite);
        store?.DeleteAlliance(a.Id);
        foreach (var m in a.Guilds)
        {
            _byGuild.Remove(m.Guild);
            Left(m.Guild, a, now);
        }
        foreach (var m in a.Guilds)
            if (Guilds.ById(m.Guild) is { } g)
            {
                Changed(g);
                Mailer.GuildNotice(world, g, "Alliance disbanded", $"The alliance '{a.Name}' was disbanded.");
            }
        Log.Info("alliance", $"alliance '{a.Name}' disbanded by {by}");
    }

    /// <summary>
    /// <paramref name="g"/> joins <paramref name="a"/> (an accepted invitation; the rules are the caller's): saved, its change cooldown starts,
    /// its members get GuildUpdate kind 5 and their nameplates change.
    /// </summary>
    public void AddGuild(Alliance a, Guild g)
    {
        var now = UtcNow;
        a.Guilds.Add(new AllianceGuild(g.Id, now));
        _byGuild[g.Id] = a;
        Save(a);
        var h = _history.GetValueOrDefault(g.Id);
        SaveHistory(new AllianceHistory(g.Id, now, h?.LastAlliance ?? Guid.Empty, h?.LeftAt));
        _protected.RemoveAll(p => p.Guild == g.Id);
        Changed(g);
        Log.Info("alliance", $"guild '{g.Name}' joins alliance '{a.Name}' ({a.Guilds.Count} guilds)");
    }

    /// <summary><paramref name="guild"/> is out of <paramref name="a"/>: its rejoin cooldown and the leave protection start.</summary>
    private void Left(Guid guild, Alliance a, DateTime now)
    {
        var changed = _history.GetValueOrDefault(guild)?.Changed ?? DateTime.MinValue.ToUniversalTime();
        if (Guilds.ById(guild) is not null) SaveHistory(new AllianceHistory(guild, changed, a.Id, now));
        if (Config.AllianceLeaveProtectionSeconds > 0)
        {
            var nowClock = world.Clock.Now;
            _protected.RemoveAll(x => x.Until <= nowClock);
            _protected.Add((guild, a.Id, nowClock + TimeSpan.FromSeconds(Config.AllianceLeaveProtectionSeconds)));
        }
    }

    /// <summary>
    /// AllianceLeave (215) {0 alliance}: a guild with ALLIANCE_MANAGEMENT leaves; the founder guild only when alone (that disbands it), else
    /// rc 1. Its members get kind 5 with no alliance; the alliance is saved.
    /// </summary>
    public short Leave(Player p, Guid id)
    {
        if (!Enabled || Guilds.Of(p.Character.Id) is not { } g || OfGuild(g.Id) is not { } a || (id != Guid.Empty && a.Id != id) || !MayManage(g, p.Character.Id)) return Refused;
        if (a.Founder == g.Id)
        {
            if (a.Guilds.Count > 1) return Refused;
            Disband(a, $"'{p.Character.Name}' (the last guild left)");
            return 0;
        }
        Remove(a, g, "leaves");
        return 0;
    }

    /// <summary><paramref name="g"/> is out of <paramref name="a"/> (left or kicked): saved, kind 5 to its members, a notice to both guild mailboxes.</summary>
    private void Remove(Alliance a, Guild g, string why)
    {
        a.Guilds.RemoveAll(m => m.Guild == g.Id);
        _byGuild.Remove(g.Id);
        Save(a);
        Left(g.Id, a, UtcNow);
        Changed(g);
        Mailer.GuildNotice(world, g, "Alliance left", $"Your guild is no longer in the alliance '{a.Name}'.");
        if (Guilds.ById(a.Founder) is { } founder) Mailer.GuildNotice(world, founder, "Alliance member left", $"The guild '{g.Name}' {why} the alliance.");
        Log.Info("alliance", $"guild '{g.Name}' {why} alliance '{a.Name}' ({a.Guilds.Count} guild(s) left)");
    }

    /// <summary>
    /// Guild <paramref name="g"/> is being deleted (its last member left, the GM's disband; Guilds.Delete): out of its alliance (whose rows
    /// the store's cascade takes), the founder role passing to the oldest other guild; an alliance left empty goes. Its invitations go too.
    /// </summary>
    public void GuildDeleted(Guild g)
    {
        foreach (var invite in _invites.Where(i => i.Guild == g.Id).ToList()) _invites.Remove(invite);
        _history.Remove(g.Id);
        if (_byGuild.GetValueOrDefault(g.Id) is not { } a) return;
        a.Guilds.RemoveAll(m => m.Guild == g.Id);
        _byGuild.Remove(g.Id);
        if (a.Guilds.Count == 0)
        {
            Disband(a, $"the deletion of its last guild '{g.Name}'");
            return;
        }
        if (a.Founder == g.Id) a.Founder = a.Guilds[0].Guild;
        Save(a);
        Log.Info("alliance", $"guild '{g.Name}' deleted: out of alliance '{a.Name}'");
    }

    // ---- the alliance window (SPEC §2.1, §2.2) ----

    /// <summary>
    /// AllianceGetMemberInfos (210) of <paramref name="id"/> (c2n.a_9, SPEC §2.2): 0 the founder guild, 1 the founding time, 2 the tag, 3 the name
    /// [H: 3 the client's alliance-name slot], 4 the asking guild's change cooldown end (0 = none) [H], 5 0 (a7m, the upkeep: 0 officially);
    /// per guild 6-18 (all arrays of one length): 6 Guid, 7 name, 8 members, 9 invited (open invitations are rows "(Invited)"), 10-14 + 15/16
    /// the crest, 17 member since, 18 0. Null = an unknown alliance (rc 1, ignored by the client).
    /// </summary>
    public AllianceGetMemberInfosResponse? MemberInfos(Player p, Guid id)
    {
        if (ById(id) is not { } a) return null;
        var rows = a.Guilds.Select(m => (Guild: Guilds.ById(m.Guild), Invited: false, Since: m.Joined.Ticks))
            .Concat(InvitesOf(a).Select(i => (Guild: Guilds.ById(i.Guild), Invited: true, Since: i.At.Ticks)))
            .Where(r => r.Guild is not null).ToList();
        var mine = Guilds.Of(p.Character.Id);
        return new AllianceGetMemberInfosResponse
        {
            akb = a.Founder, akc = a.Created.Ticks, akd = a.Tag, ake = a.Name,
            akf = mine is not null && ChangeCooldownEnd(mine.Id) is { } end ? end.Ticks : 0, akg = 0,
            akh = [.. rows.Select(r => r.Guild!.Id)],
            aki = [.. rows.Select(r => r.Guild!.Name)],
            akj = [.. rows.Select(r => r.Guild!.Members.Count)],
            akk = [.. rows.Select(r => r.Invited)],
            akl = [.. rows.Select(r => r.Guild!.Logo.Schema)],
            akm = [.. rows.Select(r => r.Guild!.Logo.Symbol)],
            akn = [.. rows.Select(r => r.Guild!.Logo.Color1)],
            ako = [.. rows.Select(r => r.Guild!.Logo.Color2)],
            akp = [.. rows.Select(r => r.Guild!.Logo.SymbolColor)],
            akq = [.. rows.Select(r => r.Guild!.Logo.Scale)],
            akr = [.. rows.Select(r => r.Guild!.Logo.Offset)],
            aks = [.. rows.Select(r => r.Since)],
            akt = new int[rows.Count],
        };
    }

    /// <summary>
    /// AllianceGetDetailInfo (217) {0 the alliance's text, name or tag}: 0 name, 1 tag, 2 guilds, 3 players, 4 Guid, 5 the founder's index
    /// (GuildDetailsHeader: key 6[key 5] is the founder, its crest 13-19 the alliance's); per guild (one length) 6 name (the client finds
    /// its own guild's name here), 7 Guid, 8 its master's name, 9 members, 10-12 0, 13-17 + 18/19 the crest, 20 member since, 21 0
    /// [H for 2/3, 8-12, 20/21]. Null = no such alliance.
    /// </summary>
    public AllianceGetDetailInfoResponse? DetailInfo(string? text)
    {
        if (!Enabled || Find(text) is not { } a) return null;
        var rows = a.Guilds.Select(m => (Guild: Guilds.ById(m.Guild)!, m.Joined)).Where(r => r.Guild is not null).ToList();
        var n = rows.Count;
        return new AllianceGetDetailInfoResponse
        {
            ajp = a.Name, ajq = a.Tag, ajr = n, ajs = Players(a), ajt = a.Id, aju = Math.Max(0, rows.FindIndex(r => r.Guild.Id == a.Founder)),
            ajv = [.. rows.Select(r => r.Guild.Name)],
            ajw = [.. rows.Select(r => r.Guild.Id)],
            ajx = [.. rows.Select(r => r.Guild.Master?.Name ?? "")],
            ajy = [.. rows.Select(r => r.Guild.Members.Count)],
            ajz = new int[n], aj0 = new int[n], aj1 = new int[n],
            aj2 = [.. rows.Select(r => r.Guild.Logo.Schema)],
            aj3 = [.. rows.Select(r => r.Guild.Logo.Symbol)],
            aj4 = [.. rows.Select(r => r.Guild.Logo.Color1)],
            aj5 = [.. rows.Select(r => r.Guild.Logo.Color2)],
            aj6 = [.. rows.Select(r => r.Guild.Logo.SymbolColor)],
            aj7 = [.. rows.Select(r => r.Guild.Logo.Scale)],
            aj8 = [.. rows.Select(r => r.Guild.Logo.Offset)],
            aj9 = [.. rows.Select(r => r.Joined.Ticks)],
            aka = new int[n],
        };
    }

    /// <summary>The alliance for .alliance info: name, tag, founder and guilds with their member counts.</summary>
    public string Describe(Alliance a) =>
        $"alliance '{a.Name}' [{a.Tag}]: {a.Guilds.Count} guild(s), {Players(a)} player(s): "
        + string.Join(", ", a.Guilds.Select(m => Guilds.ById(m.Guild) is { } g ? $"{g.Name} ({g.Members.Count}){(m.Guild == a.Founder ? " founder" : "")}" : "?"))
        + $"; founded {a.Created:yyyy-MM-dd}";

    /// <summary>The world tick: the leave protections past their end go.</summary>
    public void Update(TimeSpan now)
    {
        if (_protected.Count > 0) _protected.RemoveAll(p => p.Until <= now);
    }
}
