using System.Globalization;
using AlbionLocal.Photon;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The guild hideouts (analysis/work/stageP/hideouts/SPEC.md, "SPEC" below; settings <see cref="HideoutOptions"/>), phase H1: the data
/// (<see cref="HideoutData"/>), the placement of the kit with every rule of the data (zones, territory, prime time, guild permission, limits per
/// guild and per zone, the distance to other hideouts and to territories, castles, exits and outposts), the surface object (NewHideoutObject 419)
/// and the construction phases as timers (attacks are not made: Hideouts:Sieges). The client places the kit as a placeable building
/// (ConstructionConfirmationGui.OnDoBuild → OnDoPayPlaceableObject → cyy.u → c32.i_24: PlaceableObjectPlace 69 {1 the kit, 2 the
/// hideoutbuilding's type}, decoded in analysis/work/stageP/hideouts/h1; not ConstructionSiteCreate, which is the build menu's path). Saved at
/// once to <see cref="IHideoutStore"/> (schema v40). The instance, services and upkeep are in the other Hideouts.*.cs. Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Hideouts
{
    /// <summary>GuildPermission MANAGE_HIDEOUTS (GuildPermission.cs; the client's own test, ckj_are 0x4000000).</summary>
    public const long ManageHideouts = 67_108_864;

    private readonly World world;
    private readonly IHideoutStore? store;
    private readonly Dictionary<Guid, Hideout> _hideouts = [];
    private HideoutData? _data;
    private TimeSpan _next;
    private int _spawns;

    /// <summary>The characters whose home is a hideout (Make Home, Hideouts.Services.cs): character → hideout.</summary>
    private readonly Dictionary<Guid, Guid> _homes = [];

    public Hideouts(World world, IHideoutStore? store)
    {
        this.world = world;
        this.store = store;
        if (store is null) return;
        foreach (var h in store.LoadHideouts()) _hideouts[h.Id] = h;
        foreach (var (character, hideout) in store.LoadHideoutHomes())
            if (_hideouts.ContainsKey(hideout)) _homes[character] = hideout;
        if (_hideouts.Count > 0) Log.Info("world", $"{_hideouts.Count} hideout(s) loaded, {_homes.Count} home(s)");
    }

    private HideoutOptions S => world.Config.Hideouts;

    /// <summary>Hideouts:Enabled.</summary>
    public bool Enabled => S.Enabled;

    public IEnumerable<Hideout> All => _hideouts.Values;

    public Hideout? Find(Guid id) => _hideouts.GetValueOrDefault(id);

    /// <summary>The hideout of instance id <paramref name="cluster"/>; null when it is none or unknown.</summary>
    public Hideout? Find(string? cluster) => HideoutData.GuidOf(cluster) is { } id ? _hideouts.GetValueOrDefault(id) : null;

    /// <summary>The hideouts of guild <paramref name="guild"/>, oldest first.</summary>
    public IReadOnlyList<Hideout> OfGuild(Guid guild) => [.. _hideouts.Values.Where(h => h.Guild == guild).OrderBy(h => h.Placed)];

    /// <summary>
    /// The client data (hideouts.bin, the kit's items.xml attributes, gamedata / worldsettings of Guild:Region), read at the first use; without
    /// client data or with a broken file the 345319 values (<see cref="HideoutData.Fallback"/>, logged).
    /// </summary>
    public HideoutData Data
    {
        get
        {
            if (_data is not null) return _data;
            var kit = world.Items.Find(HideoutData.Kit)?.Attrs;
            if (world.Clusters is not { } clusters) return _data = HideoutData.Load(null, kit, null, null);
            try
            {
                _data = HideoutData.Load(clusters.Folder, world.Goals.Guild.Region, kit);
                Log.Info("data", $"hideouts: {_data.Levels.Count} levels, {_data.Phases.Count} construction phases, {_data.PlacementTypes.Count} zone types, max {_data.MaxPerGuild} per guild");
            }
            catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException)
            {
                Log.Warn("data", $"hideouts: {e.Message}; the built-in 345319 values");
                _data = HideoutData.Fallback;
            }
            return _data;
        }
    }

    private DateTime Utc => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>The buildings.xml type of the hideout (NewHideoutObject key 2); 0 without building data.</summary>
    public short BuildingType => world.Clusters?.Buildings.Find(HideoutData.Kit)?.Id ?? 0;

    /// <summary>Whether <paramref name="item"/> is the hideout kit.</summary>
    public static bool IsKit(ItemTemplate item) => item.Name == HideoutData.Kit;

    /// <summary>The owner's names and the surface keys of <paramref name="h"/> now.</summary>
    public HideoutObjectInfo Info(Hideout h)
    {
        var guild = world.Guilds.ById(h.Guild);
        var alliance = world.Alliances.OfGuild(h.Guild);
        return new HideoutObjectInfo(guild?.Name ?? "", alliance?.Id ?? Guid.Empty, alliance?.Name ?? "", Surface(h));
    }

    /// <summary>
    /// NewHideoutObject keys 13-22 (see <see cref="HideoutObject"/>): built = the level, its shields left, mode 2 (established) and the level's
    /// durability; under construction = level 0 at full shields, mode 1 with the phase and its end and the phase's durability (the invulnerable
    /// one the next phase's).
    /// </summary>
    public HideoutSurfaceState Surface(Hideout h)
    {
        var d = Data;
        var level = d.Level(h.Built ? h.Level : 0) ?? d.Levels[0];
        if (h.Built)
            return new HideoutSurfaceState((byte)level.Index, (byte)Math.Clamp(h.Shields, 0, level.MaxShields), 2, 0, 0, level.MaxDurability * Pricing.A7m, world.GameNow);
        var phase = d.Phases[Math.Clamp(h.Phase, 0, d.Phases.Count - 1)];
        var durability = phase.Invulnerable ? d.Phases.Skip(h.Phase + 1).FirstOrDefault(p => !p.Invulnerable)?.MaxDurability ?? level.MaxDurability : phase.MaxDurability;
        return new HideoutSurfaceState((byte)level.Index, (byte)level.MaxShields, 1, (byte)h.Phase, h.PhaseEnds, durability * Pricing.A7m, world.GameNow);
    }

    /// <summary>
    /// AccessStatus (209) of a hideout object (the surface object, the management): 1 the preset (accessrights.xml AccessRightsHideoutObject: closed 0,
    /// private 1, public 2), 4 the groups @_Owner / @_Guild / @_Everyone, 5 their roles (closed: owner only; private: the guild visitor; public:
    /// everyone visitor), 6 "", the owner guild's name, "".
    /// </summary>
    public static AccessStatusEvent Access(long id, Hideout h, string guildName) => new()
    {
        ddn = id,
        bq5 = (int)h.Access,
        bq8 = Building.AccessGroups,
        bq9 = h.Access switch
        {
            HideoutAccess.Closed => ["owner", "noaccess", "noaccess"],
            HideoutAccess.Private => ["owner", "visitor", "noaccess"],
            _ => ["owner", "visitor", "visitor"],
        },
        bra = ["", guildName, ""],
    };

    // ---- prime time

    /// <summary>
    /// The UTC start of the prime time of <paramref name="cluster"/>: Hideouts:PrimeTime when set, else its world.xml timeregion's start
    /// (worldsettings, Territories:PrimeTimeOffsetHours applied as for the territories); null for a zone without one (NOT_USED, unknown).
    /// </summary>
    public TimeSpan? PrimeStart(string cluster)
    {
        if (S.PrimeTime is { } fixedStart) return fixedStart;
        var region = world.Clusters?.WorldIfLoaded?.Find(cluster)?.Attrs.GetValueOrDefault("timeregion");
        if (region is null || !world.Territories.Rules.TimeRegions.TryGetValue(region, out var r)) return null;
        var day = TimeSpan.FromDays(1).Ticks;
        return TimeSpan.FromTicks((((r.Start + TimeSpan.FromHours(world.Config.Territories.PrimeTimeOffsetHours)).Ticks % day) + day) % day);
    }

    /// <summary>Whether <paramref name="utc"/> is within the kit's window (primetimedurationminutes) from <paramref name="cluster"/>'s prime-time start.</summary>
    public bool InPlacementWindow(string cluster, DateTime utc)
    {
        if (PrimeStart(cluster) is not { } start) return false;
        var day = TimeSpan.FromDays(1).Ticks;
        var since = ((utc.TimeOfDay.Ticks - start.Ticks) % day + day) % day;
        return since < TimeSpan.FromMinutes(Data.PrimeTimeMinutes).Ticks;
    }

    /// <summary>The next prime-time start of <paramref name="cluster"/> after <paramref name="utc"/>; 24 h later for a zone without one.</summary>
    public DateTime NextPrimeTime(string cluster, DateTime utc)
    {
        if (PrimeStart(cluster) is not { } start) return utc.AddDays(1);
        var at = utc.Date + start;
        return at > utc ? at : at.AddDays(1);
    }

    // ---- placement (H1)

    /// <summary>Why <paramref name="p"/> may not place a hideout at <paramref name="at"/> on its map now (the client's CanBuildResult rules, SPEC §2.4); null = it may.</summary>
    public string? CheckPlace(Player p, Vector2 at)
    {
        if (!Enabled) return "hideouts are off on this server (Hideouts:Enabled)";
        if (p.Map is not { Template: { } template } map) return "the zone is not loaded yet";
        if (HideoutData.IsInstance(map.Cluster) || IslandData.IsInstance(map.Cluster)) return "not inside an instance";
        var d = Data;
        var info = template.Info;
        var tunnel = d.TunnelTypes.Contains(info.Type);
        if (!tunnel && !d.PlacementTypes.Contains(info.Type)) return $"a hideout cannot be placed in this zone ({info.Type}): only in the black zones and the Roads' hideout tunnels";
        if (world.Guilds.Of(p.Character.Id) is not { } guild) return "only a guild member may place a hideout (NOT_IN_GUILD_FOR_HIDEOUT)";
        if (!Manages(guild, p.Character.Id)) return "the guild role lacks the permission Manage Hideouts (NO_GUILDPERMISSION_HIDEOUT)";
        if (S.RequireTerritory && !tunnel && info.TerritoryList.Count == 0) return "the zone has no territory (HIDEOUT_NO_TERRITORY)";
        if (S.RequirePrimeTime && !InPlacementWindow(map.Cluster, Utc))
            return PrimeStart(map.Cluster) is { } start
                ? string.Create(CultureInfo.InvariantCulture, $"only in the zone's prime time, {start:hh\\:mm}-{start + TimeSpan.FromMinutes(d.PrimeTimeMinutes):hh\\:mm} UTC (WRONG_TIME_HIDEOUT)")
                : "the zone has no prime time (WRONG_TIME_HIDEOUT)";
        var max = S.MaxPerGuild ?? d.MaxPerGuild;
        var own = OfGuild(guild.Id);
        if (own.Count >= max) return $"the guild has {own.Count} hideout(s), the most it may have is {max} (MAX_HIDEOUTS_FOR_GUILD)";
        if (own.Any(h => h.Cluster == map.Cluster)) return "the guild has a hideout in this zone already (MAX_HIDEOUTS_IN_CLUSTER)";
        if (info.Origin is { } origin && info.Size is { } size && (at.X < origin.X || at.Y < origin.Y || at.X > origin.X + size.X || at.Y > origin.Y + size.Y))
            return "outside the zone";
        var distance = tunnel ? d.MinDistanceInTunnel : d.MinDistance;
        if (_hideouts.Values.Where(h => h.Cluster == map.Cluster).FirstOrDefault(h => Vector2.Distance(h.Position, at) < distance) is { } near)
            return string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(near.Position, at):0} m from another hideout, at least {distance:0} m (TOO_CLOSE)");
        var radius = (float)S.BlockRadius;
        foreach (var t in info.TerritoryList)
        {
            var r = t.IsCastle ? 2 * radius : radius;
            if (Vector2.Distance(t.Monolith, at) < r)
                return $"too near the {(t.IsCastle ? t.CastleType == "SMALLCASTLE" ? "castle outpost" : "castle" : "territory")} {t.Name} (HIDEOUT_PLACEMENT_BLOCKED_BY_{(t.IsCastle ? t.CastleType == "SMALLCASTLE" ? "CASTLEOUTPOST" : "CASTLE" : "TERRITORY")})";
        }
        if (info.Exits.FirstOrDefault(e => Vector2.Distance(e.Position, at) < radius) is { } exit)
            return $"too near the exit to {exit.TargetCluster} (HIDEOUT_PLACEMENT_BLOCKED_BY_CLUSTEREXIT)";
        if (info.Outposts.FirstOrDefault(o => Vector2.Distance(o.Position, at) < radius) is { } outpost)
            return $"too near the {outpost.Type} (HIDEOUT_PLACEMENT_BLOCKED_BY_{outpost.Type.ToUpperInvariant()})";
        return null;
    }

    /// <summary>Whether <paramref name="character"/> is the master of <paramref name="g"/> or holds MANAGE_HIDEOUTS there.</summary>
    public static bool Manages(Guild g, Guid character) =>
        g.Find(character) is { } m && (m.Role == GuildRole.Guildmaster || (GuildRoles.Permissions(m.Role) & ManageHideouts) != 0);

    /// <summary>
    /// A new hideout of <paramref name="p"/>'s guild at <paramref name="at"/> on its map (the checks passed and the kit taken by the caller):
    /// construction phase 0 from now, the data's starting food, saved, its object to the zone, the guild's construction mail.
    /// </summary>
    public Hideout Place(Player p, Vector2 at, float rotation)
    {
        var map = p.Map!;
        var guild = world.Guilds.Of(p.Character.Id)!;
        var now = world.GameNow;
        var h = new Hideout(Guid.NewGuid(), guild.Id, map.Cluster, at, rotation, now) { Nutrition = Data.StartingNutrition };
        StartPhase(h, 0, Utc);
        _hideouts[h.Id] = h;
        store?.SaveHideout(h);
        if (map.Grid is not null) map.AddObject(Surface(map, h));
        Mail(h, "Hideout construction started", string.Create(CultureInfo.InvariantCulture,
            $"{p.Character.Name} placed the guild's hideout in {ZoneName(h.Cluster)} at ({at.X:0}, {at.Y:0}). Its construction runs in phases until {PhasesEnd(h):yyyy-MM-dd HH:mm} UTC."));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' placed {h} at ({at.X:0.#}, {at.Y:0.#}) for '{guild.Name}'"));
        return h;
    }

    /// <summary>The surface object of <paramref name="h"/> for <paramref name="map"/>.</summary>
    private HideoutObject Surface(Map map, Hideout h) =>
        new(world.ObjectIds.Next(), HideoutObjectSpawn.At(map.Cluster, "hideout", _spawns++, HideoutData.Kit, h.Position, h.Rotation), h, BuildingType, Info);

    /// <summary>The display name of a world.xml cluster (its id without one).</summary>
    public string ZoneName(string cluster) => world.Clusters?.WorldIfLoaded?.Find(cluster)?.DisplayName is { Length: > 0 } name ? name : cluster;

    // ---- construction (H1)

    /// <summary>The length of construction phase <paramref name="index"/> started at <paramref name="from"/> (Hideouts:ConstructionSeconds splits a total evenly).</summary>
    private DateTime PhaseEnd(Hideout h, int index, DateTime from)
    {
        var phases = Data.Phases;
        if (S.ConstructionSeconds is { } total) return from.AddSeconds(total / phases.Count);
        var phase = phases[index];
        return phase.Invulnerable ? NextPrimeTime(h.Cluster, from) : from.AddSeconds(phase.Seconds);
    }

    private void StartPhase(Hideout h, int index, DateTime from)
    {
        h.Phase = index;
        h.PhaseEnds = PhaseEnd(h, index, from).Ticks;
    }

    /// <summary>When the construction of <paramref name="h"/> will be done by the schedule (the later phases from the current one's end).</summary>
    public DateTime PhasesEnd(Hideout h)
    {
        if (h.Built) return new DateTime(h.Placed, DateTimeKind.Utc);
        var at = new DateTime(h.PhaseEnds, DateTimeKind.Utc);
        for (var i = h.Phase + 1; i < Data.Phases.Count; i++) at = PhaseEnd(h, i, at);
        return at;
    }

    /// <summary>
    /// Every second of the world clock (<see cref="World.Update"/>): construction phases whose end has come (the next phase from that end; the
    /// last one done: built at level 0 with full shields), then the upgrades and the daily upkeep (Hideouts.Upkeep.cs).
    /// </summary>
    public void Update()
    {
        if (_hideouts.Count == 0 || world.Now < _next) return;
        _next = world.Now + TimeSpan.FromSeconds(1);
        var now = world.GameNow;
        foreach (var h in _hideouts.Values.ToList())
        {
            if (!h.Built && h.PhaseEnds <= now) Advance(h, now);
            UpdateUpkeep(h, now);
        }
    }

    /// <summary>The phases of <paramref name="h"/> that ended by <paramref name="now"/>; built after the last one.</summary>
    private void Advance(Hideout h, long now)
    {
        while (!h.Built && h.PhaseEnds <= now)
        {
            var ended = new DateTime(h.PhaseEnds, DateTimeKind.Utc);
            if (h.Phase + 1 < Data.Phases.Count)
            {
                StartPhase(h, h.Phase + 1, ended);
                Log.Info("world", $"{h}: construction phase {h.Phase + 1} of {Data.Phases.Count}");
                continue;
            }
            h.Phase = -1;
            h.PhaseEnds = 0;
            h.Level = 0;
            h.Shields = Data.Levels[0].MaxShields;
            h.LastUpkeep = now;
            Log.Info("world", $"{h}: built");
            Mail(h, "Hideout construction completed", $"The guild's hideout in {ZoneName(h.Cluster)} is built.");
        }
        store?.SaveHideout(h);
        Refresh(h);
    }

    /// <summary>The surface object of <paramref name="h"/> again to its observers (cpb.af animates a change of state), and the management inside.</summary>
    public void Refresh(Hideout h)
    {
        if (world.Maps.Find(h.Cluster) is { } map)
            foreach (var o in map.Objects.Values.OfType<HideoutObject>().Where(o => o.Hideout == h).ToList())
                map.SendToObservers(o, o.NewObject());
        if (world.Maps.Find(h.Instance) is { } inside)
            foreach (var m in inside.Objects.Values.OfType<HideoutManagement>().Where(m => m.Hideout == h).ToList())
                inside.SendToObservers(m, m.Update());
    }

    /// <summary>A plain-text notice into the owner guild's mailbox (the client's bcv.MailSubject system mails are not decoded: SPEC §2.5).</summary>
    private void Mail(Hideout h, string subject, string body) =>
        Mailer.Send(world, h.Guild, guild: true, MailKind.Normal, MailCategory.System, "Albion Local", subject, body);

    /// <summary>
    /// The objects of <paramref name="map"/> (MapSpawner): on a surface zone the hideouts standing there; in a hideout's instance its management
    /// and exit (Hideouts.Instance.cs). Nothing with Hideouts:Enabled false.
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!Enabled) yield break;
        if (HideoutData.IsInstance(map.Cluster))
        {
            foreach (var o in Interior(map, template)) yield return o;
            yield break;
        }
        foreach (var h in _hideouts.Values.Where(h => h.Cluster == map.Cluster).OrderBy(h => h.Placed)) yield return Surface(map, h);
    }

    /// <summary>The guild <paramref name="g"/> is deleted: its hideouts go (their rows with the guild's), players inside are put out first.</summary>
    public void GuildDeleted(Guild g)
    {
        foreach (var h in OfGuild(g.Id)) Remove(h, "its guild was disbanded");
    }

    /// <summary>Takes <paramref name="h"/> away: the players inside out to its zone, its objects off the maps, its homes, its row.</summary>
    public void Remove(Hideout h, string why)
    {
        Evict(h, why, "The hideout is gone.");
        _hideouts.Remove(h.Id);
        foreach (var c in _homes.Where(kv => kv.Value == h.Id).Select(kv => kv.Key).ToList()) _homes.Remove(c);
        store?.DeleteHideout(h.Id);
        if (world.Maps.Find(h.Cluster) is { } map)
            foreach (var o in map.Objects.Values.OfType<HideoutObject>().Where(o => o.Hideout == h).ToList()) map.RemoveObject(o);
        Log.Info("world", $"{h} removed: {why}");
    }

    /// <summary>
    /// GM .hideout finish: the construction phases (or a running upgrade) of <paramref name="h"/> end now. Returns what was done.
    /// </summary>
    public string Finish(Hideout h)
    {
        var now = world.GameNow;
        if (!h.Built)
        {
            while (!h.Built)
            {
                h.PhaseEnds = now;
                Advance(h, now);
            }
            return $"{h}: built";
        }
        if (h.Upgrading)
        {
            h.UpgradeStarted = now - TimeSpan.FromSeconds(Data.Level(h.Level + 1)?.UpgradeSeconds ?? 0).Ticks - 1;
            UpdateUpkeep(h, now);
            return $"{h}: level {h.Level}";
        }
        return $"{h}: nothing to finish";
    }

    /// <summary>.hideout list: one line per hideout.</summary>
    public IEnumerable<string> Describe(Guid? guild = null) =>
        _hideouts.Values.Where(h => guild is null || h.Guild == guild).OrderBy(h => h.Placed).Select(DescribeOne);

    private string DescribeOne(Hideout h)
    {
        var head = FormattableString.Invariant($"{h} '{world.Guilds.ById(h.Guild)?.Name}' at ({h.Position.X:0}, {h.Position.Y:0}): ");
        var state = h.Built
            ? FormattableString.Invariant($"level {h.Level} ({Data.Level(h.Level)?.Cluster}), shields {h.Shields}, food {h.Nutrition:0}, tribute {h.Tribute / Pricing.A7m}, {h.Access}")
            : FormattableString.Invariant($"construction phase {h.Phase + 1}/{Data.Phases.Count} until {new DateTime(h.PhaseEnds, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss} UTC");
        var upgrade = h.Upgrading ? FormattableString.Invariant($", upgrading since {new DateTime(h.UpgradeStarted, DateTimeKind.Utc):yyyy-MM-dd HH:mm}") : "";
        return head + state + upgrade;
    }
}
