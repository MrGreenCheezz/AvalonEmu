using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Phase P3 of the territories (analysis/work/stageP/territories/SPEC.md §3.1, §3.5, §6 P3; <see cref="TerritoryOptions"/>): the watchtower
/// territories' claim, give up, access, food, the guild's territory list and the season schedule.
/// <list type="bullet">
/// <item>Claim: TerritoryClaimStart (302 {0 stamp, 1 the monolith, 2 the territory}) at the monolith starts a channel of
/// Territories:ClaimChannelSeconds (gamedata territoryclaimtimeinsec 15; the client runs its own bar from the same value): TerritoryClaimStart
/// (340 {0 player, 1 start, 2 end, 3 monolith}) to those in view; a move, damage, a knockdown or TerritoryClaimCancel (303) end it with 341
/// {0 player, 1 the reason}. At its end the checks once more, the cost from the guild account and the guild owns it: 342 {0 player},
/// NewMonolithObject again (owner and crest), GuildMemberTerritoryUpdate (137) to the members. The checks (@MONOLITH_CLAIM_TERRITORY_ERROR_*):
/// a guild, its right CLAIM_TERRITORY (the master always), unowned (the NPC holder's guards beaten first), the season's claimable window,
/// the cluster's prime time with Territories:PrimeTimeOnly (except within the window's claimlimitoverrideminutes), no Tower Guardian standing,
/// not in the first 2 min after entering the zone (@MONOLITH_CLAIM_COOLDOWN), Territories:MaxPerGuild, the guild fame (Territories:
/// GuildFameRequirement: initialclaim + claimterritory × territories held) and the claim cost in the guild account (Territories:ClaimCost).
/// A refused start answers RC 12085, the client's @OVERHEADMESSAGE_TERRITORY_ERROR (czk.ai7 0x180a0db00), with a system line saying why.</item>
/// <item>Give up (115 {0 territory}, right GIVE_UP_TERRITORY) and access (116 {0 territory, 1 byte}, right TERRITORY_ACCESS, kept); refused: RC 1
/// and a line.</item>
/// <item>Food: a claim fills claimnutrition (4000); one nutrition goes per secondspernutrition (22.2 s, 38880 = 10 days); TerritoryFillNutrition
/// (223 {0 territory, 1 item, 2 count}) puts food items (items.xml nutrition) of a player at the monolith in, up to nutritionstorage. Empty:
/// the territory is lost (@MONOLITH_FOOD_STORAGE_INFO). Territories:Food false: nothing is eaten.</item>
/// <item>GetMyTerritories (138): the guild's territories and castles (c3d.c3 0x180a59750): 0 the territory, 1 the food (a7m; a5a.m against
/// nutritionstorage, falling by secondspernutrition), 2/3 the tower power points (a7m) and their time (bgs.l), 4-6 0 (not decoded); 7-18 the
/// hideout list, left out.</item>
/// <item>Season schedule (Territories:SeasonSchedule data): resetterritories of the season's seasontimes frees every territory held from before it
/// (the season's territorytypes: castles too when it lists CASTLE); spawninvader puts the season's Tower Guardian (invadersettings bossinvader)
/// at each unowned monolith of a loaded zone, and it blocks claims until killed (Territories:Invader).</item>
/// <item>Territories:NpcOwners (pve): an unclaimed watchtower is held by an NPC holder ("Outlands Wardens"): fortification guards
/// (T&lt;tier&gt;_MOB_WATCHTOWER_*) stand on the territory's guard spawn points nearest the monolith; with all of them dead the territory is free to
/// claim until they stand again after GuardSettings respawntimeseconds (3600 s). A territory given up, starved or reset goes back to it.</item>
/// </list>
/// </summary>
public sealed partial class Territories
{
    /// <summary>The NPC holder of Territories:NpcOwners (no guild of the database: no tribute, no season points).</summary>
    public static readonly Guid NpcGuild = new("6e70c0de-0000-4000-8000-0000000000a0");

    public const string NpcGuildName = "Outlands Wardens";

    /// <summary>GuildPermission GIVE_UP_TERRITORY, CLAIM_TERRITORY, TERRITORY_ACCESS (SPEC §1).</summary>
    public const long RightGiveUp = 64, RightClaim = 128, RightAccess = 4096;

    /// <summary>The k4 reason / RC 12085: the client's @OVERHEADMESSAGE_TERRITORY_ERROR; 12080 the plain interruption (no text) [guess].</summary>
    public const short ClaimError = 12085, ClaimInterrupted = 12080;

    /// <summary>The RC of a refused give up, access change or feeding (no own code known; the client shows its generic error).</summary>
    public const short Refused = 1;

    /// <summary>@MONOLITH_CLAIM_COOLDOWN: no claim in the first two minutes after entering the zone.</summary>
    public static readonly TimeSpan ClaimCooldown = TimeSpan.FromMinutes(2);

    /// <summary>How far from the monolith a claim or a feeding may start (m), and how far the claimer may move during the channel.</summary>
    public const float Reach = 20, ChannelSlack = 1.5f;

    /// <summary>How many fortification guards the NPC holder puts up, and how far from the monolith their spawn points may be (m).</summary>
    public const int NpcGuardCount = 6;

    public const float NpcGuardRadius = 60;

    private sealed record ClaimChannel(MonolithObject Monolith, DateTime Due, Vector2 From, float Health);

    private readonly List<MonolithObject> _monoliths = [];
    private readonly Dictionary<Player, ClaimChannel> _claims = [];
    private readonly Dictionary<Player, (Map Map, DateTime At)> _arrivals = [];
    private DateTime _nextSchedule;

    /// <summary>The monolith of a map's fill (<see cref="Populate"/>), registered for the claims, food, NPC guards and the Tower Guardian.</summary>
    private MonolithObject Register(MonolithObject m)
    {
        _monoliths.RemoveAll(x => x.State == m.State || x.Map is { Unloaded: true });
        _monoliths.Add(m);
        return m;
    }

    /// <summary>The monolith of <paramref name="state"/> on its loaded map; null when none stands.</summary>
    public MonolithObject? MonolithOf(TerritoryState state) => _monoliths.FirstOrDefault(m => m.State == state && m.Map is { Unloaded: false });

    /// <summary>Whether <paramref name="state"/> goes to the NPC holder when free: Territories:NpcOwners, a watchtower.</summary>
    private bool NpcFor(TerritoryState state) => S.NpcOwners && !state.Territory.IsCastle;

    // ---- the season schedule

    /// <summary>The season's schedule events of <paramref name="state"/>'s timeregion up to <paramref name="until"/>, in time order (none with Territories:SeasonSchedule off).</summary>
    private IEnumerable<(SeasonAction Action, DateTime At)> Events(TerritoryState state, DateTime until)
    {
        if (!S.SeasonSchedule) yield break;
        var season = world.GuildGoals.Season;
        var tz = Rules.TimeRegions.GetValueOrDefault(state.TimeRegion)?.TimeZone ?? 0;
        foreach (var (e, at) in season.Schedule.Where(e => e.Applies(state.TimeRegion)).Select(e => (e, at: e.At(season.Start, tz))).OrderBy(x => x.at))
        {
            if (at > until) yield break;
            foreach (var a in e.Actions) yield return (a, at);
        }
    }

    /// <summary>When <paramref name="type"/> (a seasontimes action) last happened for <paramref name="state"/> up to <paramref name="utc"/> in this season; null = not yet.</summary>
    private DateTime? Last(TerritoryState state, string type, DateTime utc) =>
        Events(state, utc).LastOrDefault(x => x.Action.Type == type) is { Action: not null } x ? x.At : null;

    /// <summary>
    /// Until when the season's last territoriesclaimable lets <paramref name="state"/> be claimed outside its prime time (claimlimitoverrideminutes,
    /// 960 in SEASON_34; the SPEC's reading); null = no such window.
    /// </summary>
    private DateTime? OverrideUntil(TerritoryState state, DateTime utc)
    {
        var last = Events(state, utc).LastOrDefault(x => x.Action.Type == "territoriesclaimable");
        if (last.Action is null || !last.Action.Attrs.TryGetValue("claimlimitoverrideminutes", out var v)
            || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) return null;
        return last.At + TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Whether the season's Tower Guardian blocks claiming <paramref name="state"/> now: spawned by the schedule and not killed since.</summary>
    public bool InvaderBlocks(TerritoryState state, DateTime utc)
    {
        if (!S.Invader || state.Territory.IsCastle || state.Owned || world.GuildGoals.Season.Invader is null) return false;
        if (Last(state, "spawninvader", utc) is not { } due) return false;
        if (Last(state, "resetterritories", utc) is { } reset && reset > due) return false;
        return state.InvaderKilledAt is not { } killed || killed < due;
    }

    /// <summary>
    /// The schedule's resets (resetterritories frees what was held from before it; the season's territorytypes say whether castles too), the food
    /// of the claimed watchtowers (an empty store loses the territory) and the NPC holder's return; once a game second.
    /// </summary>
    private void Schedule(DateTime now)
    {
        if (now < _nextSchedule) return;
        _nextSchedule = now + TimeSpan.FromSeconds(1);
        var season = world.GuildGoals.Season;
        var castlesReset = season.TerritoryTypes.Contains("CASTLE");
        foreach (var s in All)
        {
            if (s.Owned && !s.NpcHeld)
            {
                if ((!s.Territory.IsCastle || castlesReset) && Last(s, "resetterritories", now) is { } reset && s.Since < reset)
                {
                    Lose(s, now, $"The season reset frees the territory {s.Territory.Name}.");
                    continue;
                }
                if (!s.Territory.IsCastle && S.Food && NutritionAt(s, now) <= 0)
                {
                    Lose(s, now, $"[Guild] The territory {s.Territory.Name} ran out of food and is lost.");
                    continue;
                }
            }
            if (!s.Owned && NpcFor(s) && (s.NpcBeatenAt is not { } beaten || now - beaten >= TimeSpan.FromSeconds(Rules.GuardRespawnSeconds)))
            {
                s.NpcBeatenAt = null;
                Apply(s, null, now);
                ToNpc(s);
                if (MonolithOf(s) is { } m) Resend(m);
            }
        }
    }

    /// <summary><paramref name="s"/> leaves its guild (season reset, starvation): the guild is told, it is unowned (or the NPC holder's).</summary>
    private void Lose(TerritoryState s, DateTime now, string why)
    {
        if (world.Guilds.ById(s.Owner) is { } g) world.Guilds.Tell(g, why);
        Log.Info("world", $"territory {s.Territory.Name}: {why}");
        SetOwner(s, null, now);
    }

    /// <summary>The NPC holder takes <paramref name="s"/> (not saved: it holds every free watchtower with Territories:NpcOwners).</summary>
    private static void ToNpc(TerritoryState s)
    {
        (s.Owner, s.OwnerName, s.Alliance, s.AllianceName, s.Logo) = (NpcGuild, NpcGuildName, Guid.Empty, "", null);
        s.NpcGuards.Clear();
    }

    // ---- the watchtowers' tick

    /// <summary>
    /// The world tick of the watchtowers: arrivals for the claim cooldown, the claim channels, the schedule, and on each loaded monolith the NPC
    /// holder's guards and the season's Tower Guardian.
    /// </summary>
    private void Watchtowers(DateTime now)
    {
        _monoliths.RemoveAll(m => m.Map is null or { Unloaded: true });
        foreach (var m in _monoliths)
            foreach (var p in m.Map!.Players.Values)
                if (!_arrivals.TryGetValue(p, out var a) || a.Map != p.Map) _arrivals[p] = (m.Map, now);
        foreach (var p in _arrivals.Keys.Where(p => p.Map is null || p.Session.Player != p).ToList()) _arrivals.Remove(p);
        UpdateClaims(now);
        Schedule(now);
        foreach (var m in _monoliths.ToList())
        {
            var s = m.State;
            var map = m.Map!;
            if (map.Template is null) continue;
            s.Monolith = m;
            NpcGuards(m, map, now);
            TowerGuardian(m, map, now);
        }
    }

    /// <summary>The NPC holder's guards: put up when it holds the territory and none stand; all of them dead frees it.</summary>
    private void NpcGuards(MonolithObject m, Map map, DateTime now)
    {
        var s = m.State;
        if (s.NpcGuards.Count > 0 && s.NpcGuards.Any(g => g.Map != map)) s.NpcGuards.Clear(); // a new map of the cluster
        if (!s.NpcHeld)
        {
            foreach (var g in s.NpcGuards.Where(g => Standing(g, map))) SpawnManager.Despawn(g, world, respawn: false);
            s.NpcGuards.Clear();
            return;
        }
        if (s.NpcGuards.Count == 0)
        {
            SpawnNpcGuards(m, map);
            return;
        }
        if (s.NpcGuards.Any(g => Standing(g, map))) return;
        s.NpcGuards.Clear();
        s.NpcBeatenAt = now;
        Apply(s, null, now);
        Resend(m);
        Log.Info("world", $"territory {s.Territory.Name}: the {NpcGuildName}' guards are beaten, free to claim until they return");
        foreach (var p in map.Players.Values)
            p.Session.Send(new SystemMessageEvent { d0p = $"The {NpcGuildName} lost their hold on {s.Territory.Name}: it can be claimed now." });
    }

    /// <summary>
    /// The fortification guards of the NPC holder: the template's fortificationguardspawnpoints nearest the monolith (at most
    /// <see cref="NpcGuardCount"/> within <see cref="NpcGuardRadius"/> m; else a ring round it), T&lt;tier 4-8&gt;_MOB_WATCHTOWER_SWORD / CROSSBOW /
    /// STAFF by the tile, health × Castles:GuardScale.
    /// </summary>
    private void SpawnNpcGuards(MonolithObject m, Map map)
    {
        var s = m.State;
        if (world.Clusters is not { } store) return;
        var tier = Math.Clamp(map.Template?.Name.Tier ?? 6, 4, 8);
        var spots = map.Template!.Critical.Where(c => c.Type == "fortificationguardspawnpoint" && Vector2.Distance(c.Position, m.Position) <= NpcGuardRadius)
            .OrderBy(c => Vector2.Distance(c.Position, m.Position)).Take(NpcGuardCount)
            .Select(c => (c.Position, c.Direction, Kind: c.Tile.EndsWith("_STAFF", StringComparison.Ordinal) ? "STAFF"
                : c.Tile.EndsWith("_CROSSBOW", StringComparison.Ordinal) ? "CROSSBOW" : "SWORD")).ToList();
        if (spots.Count == 0)
            spots = [.. Enumerable.Range(0, 3).Select(i => (m.Position + 8 * new Vector2(MathF.Cos(i * 2.094f), MathF.Sin(i * 2.094f)), i * 120f, i == 1 ? "CROSSBOW" : "SWORD"))];
        foreach (var (pos, dir, kind) in spots)
        {
            var name = $"T{tier}_MOB_WATCHTOWER_{kind}";
            if (store.Mobs.Find(name) is not { } template)
            {
                Combat.LogOnce("npc-guard:" + name, $"territory guard {name} is not in mobs.xml: not spawned");
                continue;
            }
            s.NpcGuards.Add(SpawnManager.SpawnAt(map, template, pos, dir, world, "GUARD", healthFactor: S.CastleGuardScale));
        }
        if (s.NpcGuards.Count == 0) Combat.LogOnce("npc-guards:" + s.Id, $"territory {s.Territory.Name}: no NPC guard could stand");
        else Log.Info("world", $"territory {s.Territory.Name}: {s.NpcGuards.Count} guard(s) of the {NpcGuildName} stand");
    }

    /// <summary>The season's Tower Guardian at the monolith while it blocks claims; its death (or removal) ends the block.</summary>
    private void TowerGuardian(MonolithObject m, Map map, DateTime now)
    {
        var s = m.State;
        if (s.Invader is { } inv && inv.Map != map) s.Invader = null;
        if (s.Invader is { } standing)
        {
            if (Standing(standing, map)) return;
            s.Invader = null;
            s.InvaderKilledAt = now;
            Log.Info("world", $"territory {s.Territory.Name}: the Tower Guardian is dead, claims are open");
            return;
        }
        if (!InvaderBlocks(s, now) || world.Clusters is not { } store || world.GuildGoals.Season.Invader is not { } name) return;
        if (store.Mobs.Find(name) is not { } template)
        {
            Combat.LogOnce("invader:" + name, $"the season's invader {name} is not in mobs.xml: no Tower Guardian, claims stay open");
            s.InvaderKilledAt = now;
            return;
        }
        s.Invader = SpawnManager.SpawnAt(map, template, m.Position + new Vector2(6, 6), 0, world);
        Log.Info("world", $"territory {s.Territory.Name}: the Tower Guardian ({name}) stands at the monolith");
    }

    // ---- claim

    /// <summary>
    /// Why <paramref name="p"/> may not claim <paramref name="s"/> at <paramref name="now"/> (null = may; SPEC §3.1). <paramref name="atStart"/>: the
    /// checks of the channel's start (the claim cooldown, a claim running).
    /// </summary>
    public string? CanClaim(Player p, TerritoryState s, DateTime now, bool atStart = true)
    {
        if (!Enabled) return "territories are off on this server";
        if (s.Territory.IsCastle) return "a castle is taken in its fight, not claimed";
        if (world.Guilds.Of(p.Character.Id) is not { } g) return "you aren't in a guild (NOT_IN_GUILD)";
        if (!HasRight(g, p.Character.Id, RightClaim)) return "your guild role lacks the permission Claim Territory (NO_RIGHTS)";
        if (s.NpcHeld) return $"the {NpcGuildName} hold it: beat their guards at the tower first";
        if (s.Owned) return "the territory is already claimed (ALREADY_CLAIMED)";
        if (!Claimable(s, now)) return "the territory can't be claimed at this point in the season (CLAIMING_DISABLED)";
        if (S.PrimeTimeOnly && !InPrimeTime(s, now) && !(OverrideUntil(s, now) is { } until && now < until))
            return PrimeWindow(s) is { } w ? $"only in the zone's prime time, {w} UTC (ONLY_CLAIM_IN_PRIME_TIME)" : "the zone has no prime time (ONLY_CLAIM_IN_PRIME_TIME)";
        if (InvaderBlocks(s, now)) return "the Tower Guardian blocks the claim: kill it first (INVADER_BLOCKING)";
        if (atStart && _arrivals.TryGetValue(p, out var a) && a.Map == p.Map && now - a.At < ClaimCooldown)
            return "no claim in the first two minutes after entering the zone (CLAIM_COOLDOWN)";
        if (atStart && _claims.Any(c => c.Key != p && c.Value.Monolith.State == s)) return "someone is claiming it already";
        var owned = OwnedBy(g.Id);
        if (S.MaxPerGuild > 0 && owned >= S.MaxPerGuild) return $"the guild holds {owned} territories, the most it may (Territories:MaxPerGuild)";
        var fame = FameRequired(owned);
        if (GuildFame(g) < fame * Pricing.A7m)
            return string.Create(CultureInfo.InvariantCulture, $"your guild needs {fame - GuildFame(g) / Pricing.A7m} more guild fame (NOT_ENOUGH_GUILD_FAME)");
        if (g.SilverA7m < ClaimCost * Pricing.A7m)
            return string.Create(CultureInfo.InvariantCulture, $"{ClaimCost} silver are needed in the guild account (NOT_ENOUGH_SILVER)");
        return null;
    }

    /// <summary>Territories:ClaimCost, else territorytypes claimcost (silver).</summary>
    public long ClaimCost => S.ClaimCost ?? Rules.ClaimCost;

    /// <summary>The guild fame for one more territory with <paramref name="owned"/> held: Territories:GuildFameRequirement × (held + 1), else initialclaim + claimterritory × held.</summary>
    public long FameRequired(int owned) => S.GuildFameRequirement is { } per ? per * (owned + 1) : Rules.FirstClaimFame + Rules.FurtherClaimFame * owned;

    /// <summary>The prime time of <paramref name="s"/>'s zone as text ("20:00-21:00"); null = none.</summary>
    private string? PrimeWindow(TerritoryState s)
    {
        if (!Rules.TimeRegions.TryGetValue(s.TimeRegion, out var r)) return null;
        var day = TimeSpan.FromDays(1).Ticks;
        var start = TimeSpan.FromTicks((((r.Start + TimeSpan.FromHours(S.PrimeTimeOffsetHours)).Ticks % day) + day) % day);
        var end = TimeSpan.FromTicks((start + Rules.PrimeTime).Ticks % day);
        return string.Create(CultureInfo.InvariantCulture, $"{start:hh\\:mm}-{end:hh\\:mm}");
    }

    /// <summary>Whether <paramref name="character"/> is <paramref name="g"/>'s master or holds <paramref name="right"/> by its role.</summary>
    private static bool HasRight(Guild g, Guid character, long right) =>
        g.Find(character) is { } m && (m.Role == GuildRole.Guildmaster || (GuildRoles.Permissions(m.Role) & right) != 0);

    /// <summary>The monolith <paramref name="objectId"/> on <paramref name="p"/>'s map, else that of territory <paramref name="territory"/> there.</summary>
    private MonolithObject? MonolithFor(Player p, long objectId, string? territory)
    {
        if (p.Map is not { } map) return null;
        if (map.Objects.GetValueOrDefault(objectId) is MonolithObject m) return m;
        return Find(territory) is { } s && MonolithOf(s) is { } x && x.Map == map ? x : null;
    }

    /// <summary>TerritoryClaimStart (302): the checks, then the channel with 340 to those in view. Null = started, else why not.</summary>
    public string? StartClaim(Player p, long monolith, string? territory)
    {
        if (MonolithFor(p, monolith, territory) is not { } m) return "no territory tower there";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (_claims.ContainsKey(p)) return "already claiming";
        var now = UtcNow;
        var at = p.PositionAt(now);
        if (Vector2.Distance(at, m.Position) > Reach) return "too far from the tower";
        if (CanClaim(p, m.State, now) is { } why) return why;
        var length = TimeSpan.FromSeconds(S.ClaimChannelSeconds ?? Rules.ClaimSeconds);
        _claims[p] = new ClaimChannel(m, now + length, at, p.Health.At(world.GameNow));
        p.SendToObservers(new TerritoryClaimStartEvent { ddn = p.Id, d08 = world.GameNow, d09 = world.GameNow + length.Ticks, d1a = m.Id });
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' claims {m.State.Territory.Name} ({length.TotalSeconds:0.#} s)"));
        return null;
    }

    /// <summary>The claim channel of <paramref name="p"/> ends unfinished: 341 {0 player, 1 reason}. False when none ran.</summary>
    public bool CancelClaim(Player p, string why, short reason = ClaimInterrupted)
    {
        if (!_claims.Remove(p)) return false;
        p.SendAround(new TerritoryClaimCancelEvent { ddn = p.Id, d03 = reason });
        Log.Info("world", $"'{p.Character.Name}' territory claim cancelled ({why})");
        return true;
    }

    private void UpdateClaims(DateTime now)
    {
        if (_claims.Count == 0) return;
        foreach (var (p, c) in _claims.ToList())
        {
            if (p.Map is null || p.Map != c.Monolith.Map || p.Session.Player != p) _claims.Remove(p);
            else if (p.Combat.Down is not null) CancelClaim(p, "knocked down");
            else if (p.Health.At(world.GameNow) < c.Health - 0.5f) CancelClaim(p, "damaged");
            else if (Vector2.Distance(p.PositionAt(now), c.From) > ChannelSlack) CancelClaim(p, "moved");
            else if (now >= c.Due) FinishClaim(p, c, now);
        }
    }

    /// <summary>The channel's end: the checks again, the cost from the guild account, the guild owns the territory (342 {0 player}).</summary>
    private void FinishClaim(Player p, ClaimChannel c, DateTime now)
    {
        var s = c.Monolith.State;
        var why = CanClaim(p, s, now, atStart: false);
        var g = world.Guilds.Of(p.Character.Id);
        if (why is null && g is not null && ClaimCost > 0 && !world.Guilds.SpendAccount(g, ClaimCost * Pricing.A7m, $"the claim of {s.Territory.Name}"))
            why = "the claim cost could not be paid from the guild account (NOT_ENOUGH_SILVER)";
        if (why is not null || g is null)
        {
            CancelClaim(p, why ?? "no guild", ClaimError);
            p.Session.Send(new SystemMessageEvent { d0p = $"The territory cannot be claimed: {why}." });
            return;
        }
        _claims.Remove(p);
        p.SendAround(new TerritoryClaimFinishedEvent { ddn = p.Id });
        SetOwner(s, g, now);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' claimed {s.Territory.Name} for '{g.Name}' ({ClaimCost} silver)"));
    }

    // ---- give up, access, food

    /// <summary>GiveUpTerritory (115): the owner guild's member with GIVE_UP_TERRITORY lets it go. Null = done, else why not.</summary>
    public string? GiveUp(Player p, string? territory)
    {
        if (Find(territory) is not { Territory.IsCastle: false } s) return "no such territory";
        if (world.Guilds.Of(p.Character.Id) is not { } g || s.Owner != g.Id) return "your guild does not hold this territory";
        if (!HasRight(g, p.Character.Id, RightGiveUp)) return "you don't have permission to give up the territory (GIVEUP_TERRITORY_ERROR_NO_RIGHTS)";
        Log.Info("world", $"'{p.Character.Name}' gives up {s.Territory.Name} of '{g.Name}'");
        SetOwner(s, null, UtcNow);
        return null;
    }

    /// <summary>ChangeTerritoryAccessRights (116): the owner guild's member with TERRITORY_ACCESS sets the access byte (kept and saved). Null = done.</summary>
    public string? SetAccess(Player p, string? territory, byte access)
    {
        if (Find(territory) is not { Territory.IsCastle: false } s) return "no such territory";
        if (world.Guilds.Of(p.Character.Id) is not { } g || s.Owner != g.Id) return "your guild does not hold this territory";
        if (!HasRight(g, p.Character.Id, RightAccess)) return "your guild role lacks the permission Territory Access";
        s.Access = access;
        Save(s);
        return null;
    }

    /// <summary>The food of <paramref name="s"/> at <paramref name="utc"/>: the stored nutrition less one per secondspernutrition since it was fed (Territories:Food).</summary>
    public double NutritionAt(TerritoryState s, DateTime utc) =>
        !S.Food || s.FedAt == default ? s.Nutrition : Math.Max(0, s.Nutrition - Math.Max(0, (utc - s.FedAt).TotalSeconds) / Math.Max(0.001, Rules.SecondsPerNutrition));

    /// <summary>
    /// TerritoryFillNutrition (223): food items (items.xml nutrition) of <paramref name="p"/> at the monolith of a held watchtower, as many as the
    /// store takes (nutritionstorage). The territory, the item and the count it takes, or why not.
    /// </summary>
    public (TerritoryState? State, ItemTemplate? Item, int Count, string Why) CheckFood(Player p, string? territory, long item, short count)
    {
        if (Find(territory) is not { Territory.IsCastle: false } s) return (null, null, 0, "no such territory");
        if (!s.Owned || s.NpcHeld) return (null, null, 0, "the territory has no owner to feed");
        if (MonolithOf(s) is not { } m || m.Map != p.Map || Vector2.Distance(p.PositionAt(UtcNow), m.Position) > Reach)
            return (null, null, 0, "too far from the territory's tower");
        if (p.Inventory.Bag.Find(item) is not { } stack) return (null, null, 0, $"item {item} is not in the inventory");
        var per = Hideouts.Nutrition(stack.Template);
        if (per <= 0) return (null, null, 0, $"{stack.Template.Name} is no food");
        var now = UtcNow;
        var room = Rules.NutritionStorage - NutritionAt(s, now);
        if (room < 1) return (null, null, 0, "the food storage is full");
        var n = Math.Min(Math.Min(Math.Max((int)count, 1), stack.Count), (int)Math.Ceiling(room / per));
        return (s, stack.Template, n, "");
    }

    /// <summary><paramref name="count"/> × <paramref name="item"/> into <paramref name="s"/>'s food (up to nutritionstorage), saved.</summary>
    public void Feed(TerritoryState s, ItemTemplate item, int count)
    {
        var now = UtcNow;
        s.Nutrition = Math.Min(Rules.NutritionStorage, NutritionAt(s, now) + Hideouts.Nutrition(item) * count);
        s.FedAt = now;
        Save(s);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"territory {s.Territory.Name}: {count} x {item.Name} fed, food {s.Nutrition:0}"));
    }

    // ---- the guild's list

    /// <summary>
    /// GetMyTerritories (138; c3d.c3 0x180a59750): the territories and castles of <paramref name="p"/>'s guild. 0 the territory (Guid text, as the
    /// requests' key 0), 1 the food (a7m, the bar against nutritionstorage), 2 the tower power points (a7m) and 3 their time; 4/5/6 0 (c3d.d
    /// +0x28/+0x2C/+0x30, not decoded); the hideout keys 7-18 left out. No guild: an empty answer.
    /// </summary>
    public GetMyTerritoriesResponse MyTerritories(Player p)
    {
        if (!Enabled || world.Guilds.Of(p.Character.Id) is not { } g) return new GetMyTerritoriesResponse();
        var now = UtcNow;
        var list = All.Where(s => s.Owner == g.Id).OrderBy(s => s.Territory.IsCastle).ThenBy(s => s.Territory.Name, StringComparer.Ordinal).ToList();
        return new GetMyTerritoriesResponse
        {
            a1q = [.. list.Select(s => s.Id.ToString())],
            a1r = [.. list.Select(s => s.Territory.IsCastle ? 0 : (long)Math.Round(NutritionAt(s, now) * Pricing.A7m))],
            a1s = [.. list.Select(s => s.Territory.IsCastle ? 0 : (long)Math.Round(PowerAt(s, now) * Pricing.A7m))],
            a1t = [.. list.Select(_ => world.GameNow)],
            a1u = new int[list.Count],
            a1v = new int[list.Count],
            a1w = new long[list.Count],
        };
    }

    /// <summary>GuildMemberTerritoryUpdate (137 {0 the territory, 1 gained}) to <paramref name="g"/>'s members in game [key meanings guessed].</summary>
    private void TellMembers(Guild g, TerritoryState s, bool gained) =>
        world.Guilds.SendToMembers(g, new GuildMemberTerritoryUpdateEvent { cfa = s.Id.ToString(), cfb = gained });

    /// <summary>NewMonolithObject again to the monolith's observers (its owner changed).</summary>
    private static void Resend(MonolithObject m)
    {
        if (m.Map is { Unloaded: false } map) map.SendToObservers(m, m.Build());
    }

    /// <summary>The owner row of <paramref name="s"/> with its state, saved at once (an NPC holder or none deletes it).</summary>
    private void Save(TerritoryState s)
    {
        try
        {
            store?.SaveTerritoryOwner(new Persistence.TerritoryOwner(s.Id, s.NpcHeld ? Guid.Empty : s.Owner, s.Since)
            {
                Nutrition = s.Nutrition, FedAt = s.FedAt, Access = s.Access, Power = s.Power, PowerAt = s.PowerAt,
            });
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log.Error("world", $"territory {s.Territory.Name}: the owner was not saved: {e.Message}");
        }
    }

    /// <summary>.territory lines of a watchtower: food, power, NPC holder, Tower Guardian, claim window.</summary>
    private IEnumerable<string> DescribeWatchtower(TerritoryState s)
    {
        var now = UtcNow;
        var inv = CultureInfo.InvariantCulture;
        if (s.Owned && !s.NpcHeld)
            yield return string.Create(inv, $"held since {s.Since:yyyy-MM-dd HH:mm}; food {NutritionAt(s, now):0}/{Rules.NutritionStorage} ({(S.Food ? "eaten" : "not eaten")}), "
                + $"tower power {PowerAt(s, now):0} (level {TowerLevelAt(s, now)}), access {s.Access}");
        if (s.NpcHeld) yield return $"held by the {NpcGuildName}: {s.NpcGuards.Count(g => g is { Dead: false, Removed: false })} guard(s) standing";
        else if (s.NpcBeatenAt is { } beaten) yield return string.Create(inv, $"the {NpcGuildName}' guards beaten at {beaten:HH:mm:ss}, back after {Rules.GuardRespawnSeconds:0} s");
        if (InvaderBlocks(s, now)) yield return $"the Tower Guardian blocks claims{(s.Invader is { Dead: false, Removed: false } ? " (standing)" : "")}";
        if (OverrideUntil(s, now) is { } until && now < until) yield return string.Create(inv, $"claims outside prime time until {until:yyyy-MM-dd HH:mm} (claimlimitoverrideminutes)");
        yield return string.Create(inv, $"season points: {BasePoints(s)} per {S.ScoreTickMinutes} min x {ScoreMultiplier(s, now)} x {S.SeasonPointsFactor}, "
            + $"prime time +{S.PrimeTimeBonus} x; scoring {(Scoring(s, now) ? "on" : "off")} now");
    }

    /// <summary>.territory claim/free/food: the GM's shortcuts on a watchtower.</summary>
    public string GmWatchtower(TerritoryState s, string action, Guild? guild, double amount)
    {
        var now = UtcNow;
        switch (action)
        {
            case "owner":
                if (guild is null) return "no guild given";
                SetOwner(s, guild, now);
                return $"{s.Territory.Name}: owner '{guild.Name}'";
            case "free":
                SetOwner(s, null, now);
                return $"{s.Territory.Name}: {(s.NpcHeld ? $"held by the {NpcGuildName}" : "unowned")}";
            case "food":
                if (!s.Owned || s.NpcHeld) return "no owner";
                (s.Nutrition, s.FedAt) = (Math.Clamp(amount, 0, Rules.NutritionStorage), now);
                Save(s);
                return string.Create(CultureInfo.InvariantCulture, $"{s.Territory.Name}: food {s.Nutrition:0}");
            case "power":
                if (!s.Owned || s.NpcHeld) return "no owner";
                SetPower(s, Math.Max(0, amount), now);
                return string.Create(CultureInfo.InvariantCulture, $"{s.Territory.Name}: tower power {s.Power:0} (level {TowerLevelAt(s, now)})");
            case "beat":
                foreach (var g in s.NpcGuards.Where(g => g is { Dead: false, Removed: false })) SpawnManager.Despawn(g, world, respawn: false);
                if (s.Invader is { Dead: false, Removed: false } i) SpawnManager.Despawn(i, world, respawn: false);
                return $"{s.Territory.Name}: its NPC guards and Tower Guardian removed (free at the next tick)";
            default:
                return "actions: owner <guild>, free, food <n>, power <n>, beat";
        }
    }
}
