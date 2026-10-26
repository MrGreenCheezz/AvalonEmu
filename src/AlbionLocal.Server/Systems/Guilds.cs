using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>An open guild invitation (SPEC §3.2): one per invitee (the client keeps one), runtime only.</summary>
public sealed record GuildInvite(Guid Guild, Guid Inviter, string InviterName, Guid Invitee, TimeSpan Expires);

/// <summary>
/// The guilds of the server (analysis/work/stageH/guilds/SPEC.md §1-§5, PVE-DESIGN step 13 part 1): create, invite / accept /
/// decline, leave, kick and ranks (the eight role tags, <see cref="GuildRoles"/>), the guild window's events (GuildUpdate 103,
/// GuildPlayerUpdated 104, GuildFullAccessTagsUpdated 349), the guild over other players (GuildMemberWorldUpdate 106,
/// NewCharacter 8/9/44-52, Join 56/58/71-80) and the server's guild chat (<see cref="GuildLine"/>, §8: .g, on the chat server's
/// Guild channel since stageJ; every membership change moves the member's Guild channel and its friends' entry, <see cref="Changed"/>). In memory, by guild and member; every change is written at once to the
/// guild store (<see cref="IGuildStore"/>, the character database; none in hand-made test worlds). Events go to the joined
/// player of each member on any map (<see cref="Parties.Online"/>); a member without one in the middle of a cluster change
/// misses them and gets the full guild again on arrival (<see cref="_missed"/>), or its own "left" when it was taken out
/// meanwhile (<see cref="_droppedAway"/>). The PvE guild of §10 (experience, perks, account, contracts) is Guilds.Pve.cs. Touched only
/// under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Guilds
{
    private readonly World world;
    private readonly IGuildStore? store;
    private readonly Dictionary<Guid, Guild> _byId = [];
    private readonly Dictionary<Guid, Guild> _byMember = [];
    private readonly Dictionary<Guid, GuildInvite> _invites = []; // by invitee
    private readonly Dictionary<Guid, DateTime> _createdUntil = []; // Guilds:CreateCooldownMinutes, runtime only
    private readonly Dictionary<Guid, DateTime> _leftAt = []; // without a store
    private readonly HashSet<Guid> _missed = [];
    private readonly HashSet<Guid> _droppedAway = [];

    // ReturnCodes of the client's texts (SPEC §1: geu.aq(c67) / aq(c7c) / c.b); 1 = refused without a text [choice].
    public const short Refused = 1,
        NameTaken = 4200, NameNotAllowed = 4201, NotEnoughSilver = 4204, AlreadyInGuild = 4205, CreateCooldown = 4206,
        PlayerDoesNotExist = 4240, NotInGuild = 4241, PlayerNotOnline = 4242, NoInviteRight = 4243, PlayerInGuild = 4244,
        PlayerInvited = 4245, InviteSelf = 4247, ChangeCooldown = 4249, AllianceMaxPlayers = 4251, GuildFull = 4252,
        JoinInGuild = 4120, JoinGuildFull = 4122, JoinAllianceFull = 4123, JoinCooldown = 4124;

    // GuildPlayerUpdated key 9 (SPEC §2.1).
    public const byte KindUpdate = 0, KindAdd = 1, KindKicked = 2, KindLeft = 3, KindRole = 4;

    public Guilds(World world, IGuildStore? store)
    {
        this.world = world;
        this.store = store;
        if (store is null) return;
        foreach (var g in store.LoadGuilds())
        {
            // A member row goes with its deleted character (ON DELETE CASCADE): a guild left empty goes, one without a master gets one.
            if (g.Members.Count == 0)
            {
                store.DeleteGuild(g.Id);
                Log.Info("guild", $"guild '{g.Name}' has no members left: deleted");
                continue;
            }
            if (g.Master is null) Promote(g);
            _byId[g.Id] = g;
            foreach (var m in g.Members) _byMember[m.Id] = g;
        }
        if (_byId.Count > 0) Log.Info("guild", $"{_byId.Count} guild(s) loaded, {_byMember.Count} member(s)");
    }

    private ServerConfig Config => world.Config;

    public bool Enabled => Config.GuildsEnabled;

    /// <summary>The guilds in creation order (the GM's .guild list).</summary>
    public IEnumerable<Guild> All => _byId.Values;

    /// <summary>The guild of character <paramref name="character"/>; null when in none (or guilds are off).</summary>
    public Guild? Of(Guid character) => Enabled ? _byMember.GetValueOrDefault(character) : null;

    /// <summary>The guild of id <paramref name="id"/>; null when none (or guilds are off).</summary>
    public Guild? ById(Guid id) => Enabled ? _byId.GetValueOrDefault(id) : null;

    /// <summary>The guild called <paramref name="name"/> (ignoring case); null when none.</summary>
    public Guild? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : _byId.Values.FirstOrDefault(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The open invitation of <paramref name="invitee"/>; null when none.</summary>
    public GuildInvite? InviteOf(Guid invitee) => _invites.GetValueOrDefault(invitee);

    /// <summary>A and B are in one guild (the guildmember spells, a revive; Pvp.Of's G adds the alliance: <see cref="Friends"/>).</summary>
    public bool Same(Guid a, Guid b) => Of(a) is { } g && Of(b) == g;

    /// <summary>Pvp.Of's G (the client's a5g.d_1): one guild, or guilds of one alliance (<see cref="Alliances.Allied"/>).</summary>
    public bool Friends(Guid a, Guid b) => Same(a, b) || world.Alliances.Allied(a, b);

    /// <summary>An event to every member of <paramref name="g"/> that plays; one that does not gets the full guild at its next arrival.</summary>
    public void SendToMembers(Guild g, AlbionEvent e) => SendTo(g, e);

    /// <summary>
    /// Takes <paramref name="a7m"/> from the account of <paramref name="g"/> (an alliance's founding cost), saved at once; the members get
    /// GuildUpdate kind 2 at the next second. False = too little in it, or the save failed (nothing taken).
    /// </summary>
    public bool SpendAccount(Guild g, long a7m, string why)
    {
        if (a7m < 0 || g.SilverA7m < a7m) return false;
        g.SilverA7m -= a7m;
        try { store?.SaveGuildProgress(g); }
        catch (Exception ex)
        {
            g.SilverA7m += a7m;
            Log.Warn("guild", $"account of guild '{g.Name}' not saved ({why}): {ex.Message}");
            return false;
        }
        _progressChanged.Remove(g);
        _accountChanged.Add(g);
        Log.Info("guild", $"guild '{g.Name}' pays {a7m / 10000} silver from its account: {why} ({g.SilverA7m / 10000} left)");
        return true;
    }

    private Player? Online(Guid character) => world.Parties.Online(character);

    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

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

    /// <summary>An event to every member of <paramref name="g"/> that plays, but <paramref name="except"/>; one that does not misses it (<see cref="_missed"/>).</summary>
    private void SendTo(Guild g, AlbionEvent e, Guid? except = null)
    {
        foreach (var m in g.Members)
            if (m.Id == except) continue;
            else if (Online(m.Id) is { } p) p.Session.Send(e);
            else _missed.Add(m.Id);
    }

    private void Save(Guild g) => store?.SaveGuild(g);

    /// <summary>The guild of <paramref name="character"/> changed (stageJ chat SPEC §2.2): its chat connection's Guild channel and its friends' entry follow.</summary>
    private void Changed(Guid character)
    {
        world.Chat.GuildChanged(character);
        world.Social.Updated(character);
    }

    // ---- events (SPEC §2) ----

    /// <summary>
    /// GuildUpdate kind 0 (103, ap1; SPEC §2.2, §3.6): 0 id, 1-9 one entry per member, online members first (all nine present and of
    /// one length: the client indexes them by key 1), 4 last seen (now for an online member), 5 roles, 6-8 zeros, 9 the flags
    /// (PackedEnum byte[]), 10 kind 0 (a zero byte is left out, read as 0), 11/14 the accounts, 15 the name, 16/17 the alliance's text and Guid
    /// ("" and left out without one, <see cref="Alliances.PlateOf"/>),
    /// 18 the tutorial popup, 19 {} and 28/32/34/36 "" as official (never null), 20-26 the crest.
    /// </summary>
    public GuildUpdateEvent FullUpdate(Guild g, bool tutorial)
    {
        var now = world.GameNow;
        var rows = g.Members.Select(m => (m, p: Online(m.Id))).OrderBy(x => x.p is null).ToList();
        var n = rows.Count;
        var logo = g.Logo;
        var alliance = world.Alliances.OfGuild(g.Id);
        return new GuildUpdateEvent
        {
            cf1 = g.Id,
            cf2 = [.. rows.Select(x => x.m.Id)],                                   // [1]
            cf3 = [.. rows.Select(x => x.m.Name)],                                 // [2]
            cf4 = [.. rows.Select(x => x.p is not null)],                          // [3]
            cf5 = [.. rows.Select(x => x.p is not null ? now : x.m.LastSeen.Ticks)], // [4] GameTimeStamp[]
            cf6 = [.. rows.Select(x => (byte)x.m.Role)],                           // [5] GuildRole[]
            cf7 = new int[n], cf8 = new int[n], cf9 = new int[n],                  // [6-8] (official [0,1,0] / [0,14,0] / [0,0,0]: unknown)
            cga = [.. rows.Select(x => x.p?.Flag ?? (byte)0)],                       // [9] FlaggingStatus[]
            cgc = g.SilverA7m, cgf = TaxOf(g),                                      // [11] [14]
            cgg = g.Name, cgh = world.Alliances.PlateOf(alliance), cgi = alliance?.Id ?? Guid.Empty, // [15] [16] [17]
            cgj = tutorial, cgk = [],                                              // [18] [19]
            cgl = logo.Schema, cgm = logo.Symbol, cgn = logo.Color1, cgo = logo.Color2, cgp = logo.SymbolColor, cgq = logo.Scale, cgr = logo.Offset,
            cgt = "", cgx = "", cgz = "", cg1 = "",                                // [28] [32] [34] [36]
        };
    }

    /// <summary>
    /// GuildFullAccessTagsUpdated (349, apu; SPEC §2.6): 0 the GM tag's id, per tag 1-9 (eight role tags, all arrays of one length;
    /// empty rows where the capture has none), per member 10 its Guid and 11 the indices of its tags ([0xFF] = none, a RECRUIT).
    /// </summary>
    public static GuildFullAccessTagsUpdatedEvent Tags(Guild g) => new()
    {
        ceh = g.MasterTag,
        cei = [.. g.Tags],
        cej = [.. GuildRoles.Names],
        cek = [.. GuildRoles.TagRoles.Select(_ => GuildRoles.TagColour)],
        cel = [.. GuildRoles.TagRoles.Select(_ => "")],
        cem = [.. GuildRoles.Masks],
        cen = [.. GuildRoles.Second],
        ceo = [.. GuildRoles.TagRoles.Select(_ => true)],
        cep = [.. GuildRoles.Assigners.Select(r => r.ToArray())],
        ceq = [.. GuildRoles.Editors.Select(r => r.ToArray())],
        cer = [.. g.Members.Select(m => m.Id)],
        ces = [.. g.Members.Select(m => GuildRoles.HeldTag(m.Role) is >= 0 and var t ? new[] { (byte)t } : new byte[] { 0xFF })],
    };

    /// <summary>GuildPlayerUpdated (104, ap0; SPEC §2.1) of <paramref name="m"/>: 0 Guid, 1 name, 2 online, 3 last seen (now when online), 4 role, 7 flag, 9 kind.</summary>
    public GuildPlayerUpdatedEvent PlayerUpdate(GuildMember m, byte kind, bool? online = null)
    {
        var p = Online(m.Id);
        var on = online ?? p is not null;
        return new GuildPlayerUpdatedEvent
        {
            cfr = m.Id, cfs = m.Name, cft = on, cfu = on ? world.GameNow : m.LastSeen.Ticks, cfv = (byte)m.Role, cfy = on && p is not null ? p.Flag : (byte)0, cf0 = kind,
        };
    }

    /// <summary>
    /// GuildMemberWorldUpdate (106, apz; SPEC §2.5) of <paramref name="p"/>: 0 guild, 2 its object id, 3 name, 4-10 crest, 11/12 the
    /// alliance's Guid and text (stageP SPEC §2.3; left out and "" without one), 14 "". Without a guild key 0 is empty and key 3 ""
    /// (the client's formatter gets no null).
    /// </summary>
    public static GuildMemberWorldUpdateEvent WorldUpdate(Player p, Guild? g, Alliance? alliance = null, string allianceText = "")
    {
        var e = new GuildMemberWorldUpdateEvent { cfe = p.Id, cff = g?.Name ?? "", cfo = "", cfq = "" };
        if (g is null) return e;
        e.cfc = g.Id;
        if (alliance is not null) (e.cfn, e.cfo) = (alliance.Id, allianceText);
        (e.cfg, e.cfh, e.cfi, e.cfj, e.cfk, e.cfl, e.cfm) = (g.Logo.Schema, g.Logo.Symbol, g.Logo.Color1, g.Logo.Color2, g.Logo.SymbolColor, g.Logo.Scale, g.Logo.Offset);
        return e;
    }

    /// <summary>106 of <paramref name="p"/> to itself (its crest) and to every player who sees it (their nameplate and colouring).</summary>
    public void Announce(Player p)
    {
        var g = Of(p.Character.Id);
        var a = g is null ? null : world.Alliances.OfGuild(g.Id);
        var e = WorldUpdate(p, g, a, world.Alliances.PlateOf(a));
        p.Session.Send(e);
        p.SendToObservers(e);
    }

    /// <summary>NewCharacter keys 8 (name), 9 (Guid), 44-50 (crest), 51/52 the alliance's text and Guid ("" and left out without one) of a guild member; nothing without a guild (SPEC §2.7).</summary>
    public void Apply(NewCharacterEvent e, Guid character)
    {
        if (Of(character) is not { } g) return;
        var l = g.Logo;
        var a = world.Alliances.OfGuild(g.Id);
        (e.cv5, e.cv6, e.cxc, e.cxd) = (g.Name, g.Id, world.Alliances.PlateOf(a), a?.Id ?? Guid.Empty);
        (e.cw5, e.cw6, e.cw7, e.cw8, e.cw9, e.cxa, e.cxb) = (l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
    }

    /// <summary>Join keys 56 (Guid), 58 (name), 71-77 (crest), 78 (joined), 79/80 the alliance's text and Guid ("" and left out without one) of a member; left as they are without a guild.</summary>
    public void Apply(JoinResponse r, Guid character)
    {
        if (Of(character) is not { } g) return;
        var l = g.Logo;
        var a = world.Alliances.OfGuild(g.Id);
        (r.a9u, r.a9w, r.bag, r.bah, r.bai) = (g.Id, g.Name, g.Find(character)!.Joined.Ticks, world.Alliances.PlateOf(a), a?.Id ?? Guid.Empty);
        (r.a99, r.baa, r.bab, r.bac, r.bad, r.bae, r.baf) = (l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
    }

    // ---- flows (SPEC §3) ----

    /// <summary>Names of 3-30 letters, digits and single inner spaces (SPEC §3.1 [choice]).</summary>
    public static bool ValidName(string name) =>
        name.Length is >= 3 and <= 30 && name == name.Trim() && !name.Contains("  ") && name.All(ch => char.IsLetterOrDigit(ch) || ch == ' ');

    /// <summary>
    /// Flow 3.1, CreateGuild (105): in a guild 4205, a bad name 4201, a taken one 4200 (ignoring case), too little silver for
    /// Guilds:CreateCost 4204, the create cooldown 4206 (<paramref name="cooldownEnd"/> = response key 0). On 0 the silver is taken,
    /// the founder is GUILDMASTER and gets GuildUpdate kind 0 (key 18 true; its c7l clears the client's "creating" flag, so it is
    /// mandatory) and 349; the response and the 106 of the founder (<see cref="Announce"/>) are the handler's.
    /// </summary>
    public short Create(Player p, string? rawName, GuildLogo logo, string? banner, out long cooldownEnd)
    {
        cooldownEnd = 0;
        if (!Enabled) return Refused;
        var me = p.Character.Id;
        if (Of(me) is not null) return AlreadyInGuild;
        var name = (rawName ?? "").Trim();
        if (!ValidName(name)) return NameNotAllowed;
        if (Find(name) is not null) return NameTaken;
        var cost = checked(Config.GuildCreateCost * 10000);
        if (p.Character.Silver < cost) return NotEnoughSilver;
        var now = UtcNow;
        if (_createdUntil.TryGetValue(me, out var until) && until > now)
        {
            cooldownEnd = until.Ticks;
            return CreateCooldown;
        }
        var g = new Guild(Guid.NewGuid(), name, now, logo, banner ?? "", [.. GuildRoles.TagRoles.Select(_ => Guid.NewGuid())]);
        g.Members.Add(new GuildMember(me, p.Character.Name ?? "", GuildRole.Guildmaster, now));
        // The row first: a failed write (disk full, busy) costs nothing; then the silver, saved at once with the guild.
        try { Save(g); }
        catch (Exception ex)
        {
            Log.Warn("guild", $"guild '{name}' not saved: {ex.Message}");
            return Refused;
        }
        if (!Wallet.TrySpend(p, cost, $"guild '{name}' created"))
        {
            store?.DeleteGuild(g.Id);
            return NotEnoughSilver;
        }
        world.Saves.SaveNow(p.Character);
        _byId[g.Id] = g;
        _byMember[me] = g;
        Changed(me);
        if (Config.GuildCreateCooldownMinutes > 0) _createdUntil[me] = now.AddMinutes(Config.GuildCreateCooldownMinutes);
        p.Session.Send(FullUpdate(g, tutorial: true));
        p.Session.Send(Tags(g));
        Log.Info("guild", $"'{p.Character.Name}' founds guild '{g.Name}' ({g.Id})");
        return 0;
    }

    /// <summary>The end of <paramref name="character"/>'s guild change cooldown (Guilds:RejoinCooldownMinutes after it left one); null = none.</summary>
    private DateTime? CooldownEnd(Guid character)
    {
        if (Config.GuildRejoinCooldownMinutes <= 0) return null;
        var left = store is not null ? store.GuildLeftAt(character) : _leftAt.TryGetValue(character, out var at) ? at : null;
        return left?.AddMinutes(Config.GuildRejoinCooldownMinutes) is { } end && end > UtcNow ? end : null;
    }

    private void SetLeftAt(Guid character)
    {
        if (store is not null) store.SetGuildLeftAt(character, UtcNow);
        else _leftAt[character] = UtcNow;
    }

    /// <summary>
    /// Flow 3.2, InviteToGuild (106) by name: no guild 4241, no INVITE right 4243, the name unknown 4240 or not online 4242, oneself
    /// 4247, in a guild 4244, invited by this guild already 4245, the guild full 4252, its alliance at Alliances:MaxPlayers 4251, the target's guild change cooldown 4249
    /// (<paramref name="cooldownEnd"/> = key 1). On 0 the target gets InvitedToGuild {0 guild, 1 inviter, 2 guild name} and may
    /// accept for Guilds:InviteSeconds. <paramref name="targetName"/> = response key 0.
    /// </summary>
    public short Invite(Player inviter, string? name, out string? targetName, out long cooldownEnd)
    {
        (targetName, cooldownEnd) = (null, 0);
        if (Of(inviter.Character.Id) is not { } g) return NotInGuild;
        var me = g.Find(inviter.Character.Id)!;
        if ((GuildRoles.Permissions(me.Role) & GuildRoles.Invite) == 0) return NoInviteRight;
        if (OnlineByName(name) is not { } to)
        {
            if (string.IsNullOrWhiteSpace(name) || world.Characters.Find(name.Trim()) is not { } offline) return PlayerDoesNotExist;
            targetName = offline.Name;
            return PlayerNotOnline;
        }
        targetName = to.Character.Name;
        var id = to.Character.Id;
        if (id == me.Id) return InviteSelf;
        if (Of(id) is not null) return PlayerInGuild;
        if (InviteOf(id) is { } open && open.Guild == g.Id && open.Expires > world.Clock.Now) return PlayerInvited;
        if (g.Members.Count >= Config.GuildMaxMembers) return GuildFull;
        if (!world.Alliances.Fits(g)) return AllianceMaxPlayers; // stageP: Alliances:MaxPlayers
        if (CooldownEnd(id) is { } end)
        {
            cooldownEnd = end.Ticks;
            return ChangeCooldown;
        }
        _invites[id] = new GuildInvite(g.Id, me.Id, me.Name, id, world.Clock.Now + TimeSpan.FromSeconds(Config.GuildInviteSeconds));
        to.Session.Send(new InvitedToGuildEvent { cl5 = g.Id, cl6 = me.Name, cl7 = g.Name });
        Log.Info("guild", $"'{me.Name}' invites '{to.Character.Name}' into guild '{g.Name}'");
        return 0;
    }

    /// <summary>
    /// Flow 3.2, JoinGuild (103): no open invitation (or expired, or its guild gone) rc 1, in a guild 4120, the guild full 4122, its alliance full 4123, the
    /// guild change cooldown 4124. On 0 the joiner is a member of Guilds:JoinRole; the response and the events are <see cref="AfterJoin"/>'s.
    /// </summary>
    public short Accept(Player p, out Guild? guild)
    {
        guild = null;
        var me = p.Character.Id;
        if (!Enabled || !_invites.Remove(me, out var invite) || invite.Expires <= world.Clock.Now) return Refused;
        if (Of(me) is not null) return JoinInGuild;
        if (!_byId.TryGetValue(invite.Guild, out var g)) return Refused;
        if (g.Members.Count >= Config.GuildMaxMembers) return JoinGuildFull;
        if (!world.Alliances.Fits(g)) return JoinAllianceFull;
        if (CooldownEnd(me) is not null) return JoinCooldown;
        var member = new GuildMember(me, p.Character.Name ?? "", Config.GuildJoinRole, UtcNow);
        g.Members.Add(member);
        _byMember[me] = g;
        Save(g);
        Changed(me);
        guild = g;
        Log.Info("guild", $"'{member.Name}' joins guild '{g.Name}' as {member.Role} ({g.Members.Count} members)");
        return 0;
    }

    /// <summary>
    /// After a JoinGuild's rc 0: the joiner gets GuildUpdate kind 0 (key 18 true) and 349, the other members GuildPlayerUpdated kind
    /// 1 (the new member, online) and 349, and the joiner's 106 goes to it and its observers.
    /// </summary>
    public void AfterJoin(Player p, Guild g)
    {
        var member = g.Find(p.Character.Id)!;
        p.Session.Send(FullUpdate(g, tutorial: true));
        var tags = Tags(g);
        p.Session.Send(tags);
        SendTo(g, PlayerUpdate(member, KindAdd, online: true), except: member.Id);
        SendTo(g, tags, except: member.Id);
        Announce(p);
        Mailer.GuildNotice(world, g, "New member", $"{member.Name} joined the guild."); // stageJ mail SPEC §3: the guild's mailbox
    }

    /// <summary>Flow 3.2, DeclineGuildInvitation (107): the invitation goes; the inviter, when it plays, gets GuildInviteDeclined {0 decliner, 1 reason}.</summary>
    public void Decline(Player p, byte reason)
    {
        if (!_invites.Remove(p.Character.Id, out var invite)) return;
        Online(invite.Inviter)?.Session.Send(new GuildInviteDeclinedEvent { cet = p.Character.Name, ceu = reason });
        Log.Info("guild", $"'{p.Character.Name}' declines the guild invitation of '{invite.InviterName}' (reason {reason})");
    }

    /// <summary>
    /// Flow 3.3, LeaveGuild (104): not in a guild rc 1; the guild master of a guild with others needs a member as successor
    /// (<paramref name="successor"/>, else rc 1), which becomes GUILDMASTER first (104 kind 4 to all). Then the leaver is out: the
    /// others get 104 kind 3 {it} (and 349 after a hand-over); the last member out disbands the guild. The leaver's rc 0 (the client
    /// clears itself) and its 106 are the handler's.
    /// </summary>
    public short Leave(Player p, Guid successor)
    {
        if (Of(p.Character.Id) is not { } g) return Refused;
        var me = g.Find(p.Character.Id)!;
        var handOver = me.Role == GuildRole.Guildmaster && g.Members.Count > 1;
        if (handOver)
        {
            if (g.Find(successor) is not { } next || next == me) return Refused;
            next.Role = GuildRole.Guildmaster;
            SendTo(g, PlayerUpdate(next, KindRole));
            Log.Info("guild", $"'{next.Name}' leads guild '{g.Name}'");
        }
        Remove(g, me, KindLeft, "leaves");
        if (handOver) SendTo(g, Tags(g));
        return 0;
    }

    /// <summary>
    /// <paramref name="m"/> is out of <paramref name="g"/> (left: kind 3, kicked: kind 2 to the others): its guild change cooldown
    /// starts, the others get 104, and the guild is saved, or deleted with its last member (its name is free again).
    /// </summary>
    private void Remove(Guild g, GuildMember m, byte kind, string why, bool deleted = false)
    {
        g.Members.Remove(m);
        _byMember.Remove(m.Id);
        _missed.Remove(m.Id);
        Changed(m.Id);
        if (!deleted) SetLeftAt(m.Id); // a deleted character has no guild_left row (its foreign key)
        if (Online(m.Id) is null) _droppedAway.Add(m.Id);
        Log.Info("guild", $"'{m.Name}' {why}: guild '{g.Name}' has {g.Members.Count} member(s)");
        if (g.Members.Count == 0)
        {
            PayOutAccount(g, m, receiverDeleted: deleted);
            Delete(g);
            return;
        }
        SendTo(g, PlayerUpdate(m, kind, online: Online(m.Id) is not null));
        Save(g);
        if (!deleted) Mailer.GuildNotice(world, g, kind == KindKicked ? "Member kicked" : "Member left", $"{m.Name} {(kind == KindKicked ? "was kicked from" : "left")} the guild.");
    }

    private void Delete(Guild g)
    {
        if (g.Progress.Owed.Count > 0) world.GuildGoals.Deleted(g); // stage I: season-end rewards still owed, given or mailed first
        world.Alliances.GuildDeleted(g); // stageP: out of its alliance (the founder role passes on), its invitations go
        world.Territories.GuildDeleted(g); // stageP P5: its castles are unowned
        world.Hideouts.GuildDeleted(g); // stageP: its hideouts go (players inside put out first)
        _byId.Remove(g.Id);
        foreach (var m in g.Members) _byMember.Remove(m.Id);
        foreach (var invite in _invites.Values.Where(i => i.Guild == g.Id).ToList()) _invites.Remove(invite.Invitee);
        store?.DeleteGuild(g.Id);
        Log.Info("guild", $"guild '{g.Name}' disbanded");
    }

    /// <summary>
    /// Flow 3.3, KickFromGuild (108) of the member <paramref name="target"/> (the GUI's key 1; by name when empty): the client's own
    /// rule (<see cref="GuildRoles.CanKick"/>), else rc 1. The kicked one, when it plays, gets 104 kind 3 with its own Guid (the
    /// only event that clears its guild) and its empty 106 goes to it and its observers; the others get kind 2.
    /// </summary>
    public short Kick(Player kicker, Guid target, string? name)
    {
        if (Of(kicker.Character.Id) is not { } g) return Refused;
        var me = g.Find(kicker.Character.Id)!;
        if ((target != Guid.Empty ? g.Find(target) : g.Find(name)) is not { } m || m == me || !GuildRoles.CanKick(me.Role, m.Role)) return Refused;
        var kicked = Online(m.Id);
        kicked?.Session.Send(PlayerUpdate(m, KindLeft, online: true));
        Remove(g, m, KindKicked, $"is kicked by '{me.Name}'");
        if (kicked is not null) Announce(kicked);
        return 0;
    }

    /// <summary>
    /// Flow 3.4, AssignGuildAccessTag (328) {0 member name, 1 tag}: the member gets the tag's role. The GM tag is the hand-over
    /// (the GM only: the target GUILDMASTER, the old one THE_RIGHT_HAND); other tags as <see cref="GuildRoles.CanAssign"/>.
    /// 104 kind 4 for each changed member to all, then the full 349. No response (the request has no response class); false = refused.
    /// </summary>
    public bool AssignTag(Player p, string? name, Guid tag)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(name) is not { } target || Array.IndexOf(g.Tags, tag) is not (>= 0 and var index)) return false;
        return SetRole(g, g.Find(p.Character.Id)!, target, GuildRoles.TagRoles[index]);
    }

    /// <summary>Flow 3.4, RemoveGuildAccessTagFromPlayer (329) {0 name, 1 tag}: a role tag the member holds is taken (it becomes a RECRUIT), by the same rule.</summary>
    public bool RemoveTag(Player p, string? name, Guid tag)
    {
        if (Of(p.Character.Id) is not { } g || g.Find(name) is not { } target || Array.IndexOf(g.Tags, tag) is not (>= 0 and var index)) return false;
        var role = GuildRoles.TagRoles[index];
        if (role != target.Role || role is GuildRole.Guildmaster or GuildRole.Recruit) return false;
        var me = g.Find(p.Character.Id)!;
        if (target == me || !GuildRoles.CanAssign(me.Role, role, target.Role)) return false;
        return SetRole(g, me, target, GuildRole.Recruit);
    }

    /// <summary>
    /// <paramref name="by"/> gives <paramref name="target"/> <paramref name="role"/> (the rank rules of §3.4); GUILDMASTER = the hand-over.
    /// False (nothing changed) when refused.
    /// </summary>
    public bool SetRole(Guild g, GuildMember by, GuildMember target, GuildRole role)
    {
        if (target == by || role == GuildRole.None) return false;
        if (role == GuildRole.Guildmaster)
        {
            if (by.Role != GuildRole.Guildmaster) return false;
            target.Role = GuildRole.Guildmaster;
            by.Role = GuildRole.TheRightHand;
            SendTo(g, PlayerUpdate(target, KindRole));
            SendTo(g, PlayerUpdate(by, KindRole));
        }
        else
        {
            var check = role == GuildRole.Recruit ? target.Role : role; // taking a tag needs the right to give it
            if (!GuildRoles.CanAssign(by.Role, check, target.Role)) return false;
            if (target.Role == role) return true;
            target.Role = role;
            SendTo(g, PlayerUpdate(target, KindRole));
        }
        SendTo(g, Tags(g));
        Save(g);
        Log.Info("guild", $"'{by.Name}' makes '{target.Name}' {target.Role} in guild '{g.Name}'");
        return true;
    }

    /// <summary>A guild without a master (its master's character was deleted): the highest-ranking member, the earliest of them, leads.</summary>
    private void Promote(Guild g)
    {
        var next = g.Members.OrderByDescending(m => m.Role).First();
        next.Role = GuildRole.Guildmaster;
        Save(g);
        Log.Info("guild", $"guild '{g.Name}' had no master: '{next.Name}' leads it");
    }

    /// <summary>
    /// The character was deleted (the login's DeleteCharacter, the console's account delete; called after the repository's delete
    /// succeeded, whose cascade already took its member row): it leaves its guild as with LeaveGuild, the master's role passing on.
    /// Runs with guilds off too, so the saved guilds stay consistent.
    /// </summary>
    public void CharacterDeleted(Guid character)
    {
        _invites.Remove(character);
        if (_byMember.GetValueOrDefault(character) is not { } g) return;
        var m = g.Find(character)!;
        var master = m.Role == GuildRole.Guildmaster;
        Remove(g, m, KindLeft, "is deleted", deleted: true);
        if (master && g.Members.Count > 0)
        {
            Promote(g);
            SendTo(g, PlayerUpdate(g.Master!, KindRole));
            SendTo(g, Tags(g));
        }
    }

    /// <summary>The GM's .guild disband: every member that plays gets its "left" (104 kind 3 with its own Guid) and an empty 106, then the guild is deleted.</summary>
    public void Disband(Guild g)
    {
        foreach (var m in g.Members.ToList())
        {
            if (Online(m.Id) is { } p)
            {
                p.Session.Send(PlayerUpdate(m, KindLeft, online: true));
                _byMember.Remove(m.Id);
                Announce(p);
            }
            else _droppedAway.Add(m.Id);
        }
        PayOutAccount(g, g.Master ?? g.Members.FirstOrDefault());
        Delete(g);
        foreach (var m in g.Members) Changed(m.Id);
    }

    /// <summary>
    /// SPEC §3.5, after the JoinFinished of <paramref name="p"/>: a member gets GuildPlayerUpdated kind 0 for itself; after a login (not
    /// a cluster change's arrival, <paramref name="arrived"/>) or when it missed guild events during the change also GuildUpdate kind 0
    /// and 349, and after a login the other members get its kind 0 (online). One taken out of its guild during a change gets its kind 3.
    /// </summary>
    public void Joined(Player p, bool arrived)
    {
        if (!Enabled) return;
        var id = p.Character.Id;
        if (_droppedAway.Remove(id) && arrived && Of(id) is null)
            p.Session.Send(new GuildPlayerUpdatedEvent { cfr = id, cfs = p.Character.Name, cft = true, cfu = world.GameNow, cf0 = KindLeft });
        if (Of(id) is not { } g)
        {
            _missed.Remove(id);
            return;
        }
        var m = g.Find(id)!;
        var self = PlayerUpdate(m, KindUpdate, online: true);
        p.Session.Send(self);
        if (_missed.Remove(id) || !arrived)
        {
            p.Session.Send(FullUpdate(g, tutorial: false));
            p.Session.Send(Tags(g));
        }
        if (!arrived) SendTo(g, self, except: id);
        // §10.6: after a login the period's open contracts (a new period is rolled first and told to every member in game).
        var period = g.Contracts?.Period;
        if (!arrived && ContractsOf(g) is { } contracts && contracts.Period == period && contracts.Goals.Any(c => !c.Done))
            p.Session.Send(new SystemMessageEvent { d0p = "[Guild] Contracts: " + string.Join("; ", contracts.Goals.Select(c => $"{c.Describe()} {c.Progress}/{c.Count}")), d0q = 0 });
    }

    /// <summary>SPEC §3.5: the last connection of <paramref name="character"/> ended (not a cluster change): its last seen is now, the others get its kind 0 (offline).</summary>
    public void Disconnected(Guid character)
    {
        _invites.Remove(character);
        if (Of(character) is not { } g) return;
        var m = g.Find(character)!;
        m.LastSeen = UtcNow;
        SendTo(g, PlayerUpdate(m, KindUpdate, online: false), except: character);
        Save(g);
    }

    /// <summary>
    /// SPEC §8 option 1: a guild line of <paramref name="speaker"/> (.g) to every member that plays, the speaker included: on the chat
    /// server's Guild channel to the members who joined it (<see cref="ChatHub.GuildLine"/>, shown in the SOCIAL tab), as SystemMessage
    /// "[Guild] name: text" to the others (no chat connection, or Chat:Enabled false), never both (stageJ chat SPEC §2.2); returns how
    /// many got it, -1 = not in a guild.
    /// </summary>
    public int GuildLine(Player speaker, string text)
    {
        if (Of(speaker.Character.Id) is not { } g) return -1;
        var reached = world.Chat.GuildLine(speaker, g, text);
        var line = $"[Guild] {speaker.Character.Name}: {text}";
        var e = new SystemMessageEvent { d0p = line, d0q = 0 };
        var heard = reached.Count;
        foreach (var m in g.Members)
            if (!reached.Contains(m.Id) && Online(m.Id) is { } p)
            {
                p.Session.Send(e);
                heard++;
            }
        return heard;
    }

    /// <summary>The guild of <paramref name="g"/> for .guild info: name, members with role and whether they play.</summary>
    /// <summary>With the PvE guild (§10.3): its level, experience (of the next level's), perks and account.</summary>
    public string Describe(Guild g) =>
        $"guild '{g.Name}': {g.Members.Count} member(s): "
        + string.Join(", ", g.Members.Select(m => $"{m.Name} {m.Role}{(Online(m.Id) is null ? "" : " (online)")}"))
        + string.Create(CultureInfo.InvariantCulture, $"; founded {g.Created:yyyy-MM-dd}")
        + $"; level {Level(g)}, {g.Xp}{(NextLevelXp(g) is { } next ? $"/{next}" : "")} xp ({Perks(Level(g))}); account {g.SilverA7m / 10000} silver";

    /// <summary>The world tick: the PvE part (<see cref="UpdatePve"/>); invitations past their time go silently (the client sent its timeout decline already, SPEC §3.2).</summary>
    public void Update(TimeSpan now)
    {
        UpdatePve(now);
        if (_invites.Count == 0) return;
        foreach (var invite in _invites.Values.Where(i => i.Expires <= now).ToList()) _invites.Remove(invite.Invitee);
    }
}
