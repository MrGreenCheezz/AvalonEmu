using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Phase P5 of the territories (analysis/work/stageP/territories/SPEC.md §3.4, §6 P5; Castles:* of <see cref="TerritoryOptions"/>): the castles
/// and castle outposts.
/// <list type="bullet">
/// <item>Fight: the castle tile's guardspawnpoints (BASIC, ELITE, LORD) stand as mobs (health × Castles:GuardScale). The basic guards stand
/// at rest (phase 0); a hit or a kill on one starts the fight (1). When they are all dead the elite guards come after castles.xml
/// eliteguardarrivalinmin (3, then 4), when those are dead the lord after castlelordarrivalinmin (5, then 6), and his death opens the capture
/// zone (7). Each phase times out after castlephasetimeoutinmin (30): the attack failed, the castle stands as before. An unclaimed capture zone
/// brings the lord back after castlelordrespawntime (8). CastlePhaseChanged (417 {0 castle, 1 old, 2 new}) to the castle's observers.</item>
/// <item>Capture: the alive, standing players within capturezoneradius of the zone, by team (the guild's alliance, else the guild; a player
/// without a guild is a team of its own that cannot claim). One team alone fills its claim in attackerclaimtime (the owner's team in
/// ownerclaimtime: that ends the fight, castle kept), empties another's in attackerunclaimtime (the owner's in ownerunclaimtime); nobody in the
/// zone lets a claim run out in attackernoteamunclaimtime (the owner's in ownernoteamclaimtime); two teams, or a standing defender, pause it
/// (contested). Mounted players count as presence, not as claimers. A full claim makes the guild the owner (saved, NewCastleObject again
/// with the owner, then 417 7 → 0), the guards stand again after territorytypes GuardSettings respawntimeseconds. CastleClaimProgress (513) and
/// CastleClaimProgressLogo (514) to the observers while the zone is open.</item>
/// <item>Chests: at each respawn of the treasuredistribution schedule (CASTLE / CASTLE_OUTPOST, every 2 h UTC 10:00-22:00, the weekend's
/// legendary 18:00; Castles:ChestSchedule hourly: every hour) a draw of the event (None included) puts the TREASURE_CASTLE_* chest of its
/// lootchests spawnpoint on the template's treasure tile, Unlocked, for the spawnpoint's despawntimeseconds (1800); only for an owned castle
/// with Castles:ChestNeedsOwnership (and only the owner guild's members open it), for every castle without (anyone opens it).
/// NewCastleObject keys 12/13 follow it.</item>
/// <item>Season points: Castles:SeasonPoints × Territories:SeasonPointsFactor per Territories:ScoreTickMinutes to the owner (GvgSeasonActivity
/// TerritoryHold), 0 = off.</item>
/// </list>
/// </summary>
public sealed partial class Territories
{
    private readonly List<CastleObject> _castles = [];
    private readonly Dictionary<long, TerritoryState> _castleChests = [];

    private long GameNow => world.GameNow;

    /// <summary>The castle object of <paramref name="state"/> on its loaded map; null when none stands.</summary>
    public CastleObject? CastleOf(TerritoryState state) => _castles.FirstOrDefault(c => c.State == state && c.Map is { Unloaded: false });

    /// <summary>The template's treasure tile of the castle's subtype nearest to it (within 150 m), else its capture zone.</summary>
    private static Vector2 ChestSpotOf(CastleObject castle, ClusterTemplate template)
    {
        var kind = "treasure:" + castle.Type.ChestSubtype;
        var spot = template.MistsSpots.Where(s => s.Kind == kind && Vector2.Distance(s.Position, castle.Position) <= 150)
            .MinBy(s => Vector2.Distance(s.Position, castle.Position));
        return spot?.Position ?? castle.CaptureZone;
    }

    /// <summary>A castle of a map's fill (<see cref="Populate"/>): its chest spot, and its fight starts anew on the next tick.</summary>
    private CastleObject Register(CastleObject castle, ClusterTemplate template)
    {
        castle.ChestSpot = ChestSpotOf(castle, template);
        _castles.RemoveAll(c => c.State == castle.State || c.Map is { Unloaded: true });
        _castles.Add(castle);
        return castle;
    }

    /// <summary>The world tick: the watchtowers (claims, food, schedule, NPC guards, Tower Guardian), every standing castle's fight, capture zone and chest; the owners' season points.</summary>
    public void Update()
    {
        if (!Enabled) return;
        var now = UtcNow;
        Watchtowers(now);
        ScoreTowers(now);
        if (!CastlesOn) return;
        _castles.RemoveAll(c => c.Map is { Unloaded: true });
        foreach (var c in _castles.ToList())
        {
            if (c.Map is not { Template: not null, Grid: not null, Unloaded: false } map) continue;
            try
            {
                Fight(c, map, now);
                Chests(c, map, now);
                if (c.State.Fight.SentChest != c.Chest) Resend(c);
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or KeyNotFoundException)
            {
                Combat.LogOnce("castle:" + c.State.Id, $"castle {c.State.Territory.Name}: {e.Message}");
            }
        }
        Score(now);
    }

    // ---- the fight

    private static bool Standing(Mob m, Map map) => !m.Dead && !m.Removed && m.Map == map;

    private void Fight(CastleObject c, Map map, DateTime now)
    {
        var f = c.State.Fight;
        if (f.Map != map)
        {
            // A new map of the cluster: the fight (if any) ended with the old one, its chest is gone with it.
            f.Map = map;
            f.Mobs.Clear();
            (f.Phase, f.PhaseSince, f.WaveAt, f.LastTick) = (CastleFight.Idle, now, null, now);
            ClearClaim(f);
            if (f.Chest is { } old) _castleChests.Remove(old.Id);
            f.Chest = null;
        }
        var dt = Math.Clamp((now - f.LastTick).TotalSeconds, 0, 5);
        f.LastTick = now;
        if (f.Phase is not CastleFight.Idle && now - f.PhaseSince >= TimeSpan.FromMinutes(c.Type.PhaseTimeoutMinutes))
        {
            Fail(c, map, now);
            return;
        }
        switch (f.Phase)
        {
            case CastleFight.Idle:
                if (f.Mobs.Count == 0)
                {
                    if (f.GuardsBackAt is { } back && now < back) break;
                    f.GuardsBackAt = null;
                    Spawn(c, map, "BASIC");
                    break;
                }
                var ticks = GameNow;
                if (f.Mobs.Any(m => !Standing(m, map) || m.Threat.Count > 0 || m.Health.At(ticks) < m.Health.Max - 0.5f)) SetPhase(c, map, CastleFight.Guards, now);
                break;
            case CastleFight.Guards:
                if (f.Mobs.All(m => !Standing(m, map))) Wave(c, map, now, "ELITE");
                break;
            case CastleFight.ElitesComing or CastleFight.LordComing:
                if (f.WaveAt is { } at && now >= at) Arrive(c, map, now, f.Phase == CastleFight.ElitesComing ? "ELITE" : "LORD");
                break;
            case CastleFight.Elites:
                if (f.Mobs.All(m => !Standing(m, map))) Wave(c, map, now, "LORD");
                break;
            case CastleFight.Lord or CastleFight.LordBack:
                if (f.Mobs.All(m => !Standing(m, map))) OpenZone(c, map, now);
                break;
            case CastleFight.Capture:
                Claim(c, map, now, dt);
                if (f.Phase == CastleFight.Capture && f.Claimer == Guid.Empty && now - f.PhaseSince >= TimeSpan.FromSeconds(c.Type.LordRespawnSeconds)
                    && Spawn(c, map, "LORD") > 0)
                    SetPhase(c, map, CastleFight.LordBack, now);
                break;
        }
    }

    /// <summary>The guards of <paramref name="guardType"/> stand (the fight's defenders now); how many.</summary>
    private int Spawn(CastleObject c, Map map, string guardType)
    {
        var f = c.State.Fight;
        f.Mobs.Clear();
        if (world.Clusters is not { } store) return 0;
        foreach (var spot in c.Guards.Where(g => g.GuardType == guardType))
        {
            if (store.Mobs.Find(spot.Mob) is not { } template)
            {
                Combat.LogOnce("castle-mob:" + spot.Mob, $"castle guard {spot.Mob} is not in mobs.xml: not spawned");
                continue;
            }
            f.Mobs.Add(SpawnManager.SpawnAt(map, template, spot.Position, spot.Direction, world, spot.Behavior, healthFactor: S.CastleGuardScale));
        }
        return f.Mobs.Count;
    }

    /// <summary>The next wave (ELITE, LORD) comes: after its castles.xml arrival time, or at once; without any of its spots the one after it.</summary>
    private void Wave(CastleObject c, Map map, DateTime now, string guardType)
    {
        var f = c.State.Fight;
        f.Mobs.Clear();
        if (!c.Guards.Any(g => g.GuardType == guardType))
        {
            if (guardType == "ELITE") Wave(c, map, now, "LORD");
            else OpenZone(c, map, now);
            return;
        }
        var minutes = guardType == "ELITE" ? c.Type.EliteArrivalMinutes : c.Type.LordArrivalMinutes;
        if (minutes <= 0)
        {
            Arrive(c, map, now, guardType);
            return;
        }
        SetPhase(c, map, guardType == "ELITE" ? CastleFight.ElitesComing : CastleFight.LordComing, now);
        f.WaveAt = now + TimeSpan.FromMinutes(minutes);
    }

    private void Arrive(CastleObject c, Map map, DateTime now, string guardType)
    {
        Spawn(c, map, guardType);
        SetPhase(c, map, guardType == "ELITE" ? CastleFight.Elites : CastleFight.Lord, now);
    }

    /// <summary>The lord is dead: the capture zone opens (phase 7), no claim yet.</summary>
    private void OpenZone(CastleObject c, Map map, DateTime now)
    {
        var f = c.State.Fight;
        f.Mobs.Clear();
        ClearClaim(f);
        SetPhase(c, map, CastleFight.Capture, now);
        SendProgress(c, map, now, force: true);
    }

    /// <summary>The phase timed out: the attack failed, the defenders go and the castle stands as before (its basic guards anew at the next tick).</summary>
    private void Fail(CastleObject c, Map map, DateTime now)
    {
        var f = c.State.Fight;
        foreach (var m in f.Mobs.Where(m => Standing(m, map))) SpawnManager.Despawn(m, world, respawn: false);
        f.Mobs.Clear();
        var hadZone = f.Phase == CastleFight.Capture;
        ClearClaim(f);
        if (hadZone) SendProgress(c, map, now, force: true);
        Log.Info("world", $"castle {c.State.Territory.Name}: phase {f.Phase} timed out, the attack failed");
        SetPhase(c, map, CastleFight.Idle, now);
    }

    /// <summary>The phase changes: CastlePhaseChanged (417 {0 castle, 1 old, 2 new}) to the castle's observers.</summary>
    private void SetPhase(CastleObject c, Map map, int phase, DateTime now)
    {
        var f = c.State.Fight;
        var old = f.Phase;
        (f.Phase, f.PhaseSince, f.WaveAt) = (phase, now, null);
        if (old == phase) return;
        map.SendToObservers(c, new CastlePhaseChangedEvent { ddn = c.Id, bvu = old, bvv = phase });
        Log.Info("world", $"castle {c.State.Territory.Name}: phase {old} -> {phase}");
    }

    /// <summary>NewCastleObject again to the castle's observers (its owner, phase or chest changed; the client updates the object it has).</summary>
    private void Resend(CastleObject c)
    {
        c.State.Fight.SentChest = c.Chest;
        if (c.Map is { } map) map.SendToObservers(c, c.Build());
    }

    // ---- the capture zone

    private static void ClearClaim(CastleFight f) => (f.Claimer, f.ClaimerAlliance, f.Progress, f.OwnerClaim, f.Contested) = (Guid.Empty, Guid.Empty, 0, false, false);

    /// <summary>The team of guild <paramref name="g"/>: its alliance, else itself.</summary>
    private Guid TeamOf(Guid g) => g == Guid.Empty ? Guid.Empty : world.Alliances.OfGuild(g)?.Id ?? g;

    private void Claim(CastleObject c, Map map, DateTime now, double dt)
    {
        var f = c.State.Fight;
        var t = c.Type;
        var time = new DateTime(GameNow, DateTimeKind.Utc);
        var radius = (float)t.CaptureRadius;
        var present = map.Players.Values.Where(p => p.Combat.Down is null && Vector2.Distance(p.PositionAt(time), c.CaptureZone) <= radius).ToList();
        var teams = present.GroupBy(p => world.Guilds.Of(p.Character.Id) is { } g ? TeamOf(g.Id) : p.Character.Id).ToList();
        var defender = f.Mobs.Any(m => Standing(m, map) && Vector2.Distance(m.Position, c.CaptureZone) <= radius);
        var ownerTeam = TeamOf(c.State.Owner);
        f.Contested = teams.Count >= 2 || defender && teams.Count > 0;
        if (f.Contested) { }
        else if (teams.Count == 0)
        {
            if (f.Claimer != Guid.Empty)
            {
                f.Progress -= dt / Math.Max(0.1, f.OwnerClaim ? t.CaptureValue("ownernoteamclaimtime", 30) : t.AttackerNoTeamUnclaimSeconds);
                if (f.Progress <= 0) ClearClaim(f);
            }
        }
        else if (present.Where(p => !p.IsMounted).Select(p => world.Guilds.Of(p.Character.Id)).FirstOrDefault(g => g is not null) is { } guild)
        {
            var team = TeamOf(guild.Id);
            var isOwner = c.State.Owned && team == ownerTeam;
            if (f.Claimer == Guid.Empty)
                (f.Claimer, f.ClaimerAlliance, f.OwnerClaim, f.Progress) = (guild.Id, world.Alliances.OfGuild(guild.Id)?.Id ?? Guid.Empty, isOwner, 0);
            else if (TeamOf(f.Claimer) == team)
                f.Progress += dt / Math.Max(0.1, f.OwnerClaim ? t.OwnerClaimSeconds : t.AttackerClaimSeconds);
            else
            {
                f.Progress -= dt / Math.Max(0.1, isOwner ? t.OwnerUnclaimSeconds : t.AttackerUnclaimSeconds);
                if (f.Progress <= 0)
                    (f.Claimer, f.ClaimerAlliance, f.OwnerClaim, f.Progress) = (guild.Id, world.Alliances.OfGuild(guild.Id)?.Id ?? Guid.Empty, isOwner, 0);
            }
            if (f.Progress >= 1)
            {
                Captured(c, map, now, guild);
                return;
            }
        }
        f.Progress = Math.Clamp(f.Progress, 0, 1);
        SendProgress(c, map, now, force: false);
    }

    /// <summary>
    /// CastleClaimProgress (513; cj0.ajv 0x180b2c8b0 → a5a resource bar): 0 castle, 1 the claiming guild (Castle +0xD4, the zone's colour by
    /// the reader's guild), 2 its alliance, 3 the claim 0..1 (a7m), 4 its rate (a7m; 0: the server sends each change), 5 the stamp, 6 0 (c4);
    /// and CastleClaimProgressLogo (514) when the claimer changes: 1 the claimer (must be key 1 of 513), 2-8 its crest in bux.j's order
    /// (key 2 the schema, -1 = no crest). At most every 0.5 s, at once on a claimer change or an end.
    /// </summary>
    private void SendProgress(CastleObject c, Map map, DateTime now, bool force)
    {
        var f = c.State.Fight;
        var value = (long)Math.Round(f.Progress * 10000);
        var key = (f.Claimer, value);
        var claimerChanged = f.SentProgress?.Claimer != f.Claimer;
        if (!force && f.SentProgress == key) return;
        if (!force && !claimerChanged && value is not 0 and not 10000 && now - f.LastProgressSent < TimeSpan.FromSeconds(0.5)) return;
        (f.SentProgress, f.LastProgressSent) = (key, now);
        if (claimerChanged || force && f.SentLogo != f.Claimer)
        {
            f.SentLogo = f.Claimer;
            var logo = new CastleClaimProgressLogoEvent { ddn = c.Id, bvh = f.Claimer, bvi = -1 };
            if (world.Guilds.ById(f.Claimer)?.Logo is { } l)
                (logo.bvi, logo.bvl, logo.bvj, logo.bvk, logo.bvm, logo.bvn, logo.bvo) = (l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
            map.SendToObservers(c, logo);
        }
        map.SendToObservers(c, new CastleClaimProgressEvent
        {
            ddn = c.Id, bvb = f.Claimer, bvc = f.ClaimerAlliance, bvd = value, bve = 0, bvf = GameNow, bvg = 0, // 0 / Empty keys stay out: the defaults
        });
    }

    /// <summary>A full claim: the owner's team kept the castle, or <paramref name="guild"/> takes it. The fight ends; the guards come back later.</summary>
    private void Captured(CastleObject c, Map map, DateTime now, Guild guild)
    {
        var f = c.State.Fight;
        var kept = f.OwnerClaim || guild.Id == c.State.Owner;
        f.Progress = 1;
        SendProgress(c, map, now, force: true);
        foreach (var m in f.Mobs.Where(m => Standing(m, map))) SpawnManager.Despawn(m, world, respawn: false);
        f.Mobs.Clear();
        ClearClaim(f);
        f.GuardsBackAt = now + TimeSpan.FromSeconds(Rules.GuardRespawnSeconds);
        if (!kept) SetOwner(c.State, guild, now, resend: false);
        Resend(c); // the new owner before 417 7 -> 0: the client's won/lost stinger compares the owner of the zone's start with this one
        SetPhase(c, map, CastleFight.Idle, now);
        Log.Info("world", $"castle {c.State.Territory.Name}: {(kept ? "kept by" : "captured by")} '{guild.Name}'; guards back at {f.GuardsBackAt:HH:mm:ss}");
    }

    /// <summary>
    /// <paramref name="state"/> gets <paramref name="guild"/> as its owner (null = none, or the NPC holder of Territories:NpcOwners): its name,
    /// alliance and crest, since <paramref name="now"/>, saved at once; a watchtower newly held gets claimnutrition, no tower power and access 0.
    /// The old and new owner guilds are told (a system line, GuildMemberTerritoryUpdate 137 for a watchtower); NewCastleObject / NewMonolithObject
    /// again to the observers.
    /// </summary>
    public void SetOwner(TerritoryState state, Guild? guild, DateTime now, bool resend = true)
    {
        var old = state.Owner;
        Apply(state, guild, now);
        state.Fight.LastScore = null;
        var tower = !state.Territory.IsCastle;
        if (tower && guild is not null && old != guild.Id)
        {
            (state.Nutrition, state.FedAt, state.Power, state.PowerAt, state.Access, state.LastScore) = (Rules.ClaimNutrition, now, 0, now, 0, null);
            state.NpcBeatenAt = null;
        }
        if (guild is null)
        {
            (state.Nutrition, state.FedAt, state.Power, state.PowerAt, state.Access, state.LastScore) = (0, default, 0, default, 0, null);
            state.NpcBeatenAt = null;
            if (NpcFor(state)) ToNpc(state);
        }
        Save(state);
        var kind = tower ? "territory" : state.Territory.CastleType == "SMALLCASTLE" ? "castle outpost" : "castle";
        if (old != Guid.Empty && old != state.Owner && world.Guilds.ById(old) is { } lost)
        {
            world.Guilds.Tell(lost, $"[Guild] The {kind} {state.Territory.Name} is lost.");
            if (tower) TellMembers(lost, state, gained: false);
        }
        if (guild is not null && old != guild.Id)
        {
            world.Guilds.Tell(guild, $"[Guild] Your guild holds the {kind} {state.Territory.Name} now.");
            if (tower) TellMembers(guild, state, gained: true);
        }
        Log.Info("world", $"territory {state.Territory.Name}: owner {(guild is not null ? $"'{guild.Name}'" : state.NpcHeld ? NpcGuildName : "none")}");
        if (!resend) return;
        if (CastleOf(state) is { } c) Resend(c);
        if (MonolithOf(state) is { } m) Resend(m);
    }

    /// <summary>The owner fields of <paramref name="state"/> from <paramref name="guild"/> (null = none), not saved.</summary>
    private void Apply(TerritoryState state, Guild? guild, DateTime since)
    {
        var alliance = guild is null ? null : world.Alliances.OfGuild(guild.Id);
        state.Owner = guild?.Id ?? Guid.Empty;
        state.OwnerName = guild?.Name ?? "";
        state.Alliance = alliance?.Id ?? Guid.Empty;
        state.AllianceName = alliance?.Name ?? "";
        state.Logo = guild?.Logo;
        state.Since = guild is null ? DateTime.MinValue : since;
    }

    /// <summary>
    /// The saved owners onto the territories with a watchtower's food, access and tower power (a guild that is gone, or guilds off, leaves its
    /// row unused); with Territories:NpcOwners every other watchtower is the NPC holder's.
    /// </summary>
    private void LoadOwners(Dictionary<Guid, TerritoryState> states)
    {
        try
        {
            foreach (var o in store?.LoadTerritoryOwners() ?? [])
                if (states.TryGetValue(o.Territory, out var s) && world.Guilds.ById(o.Guild) is { } g)
                {
                    Apply(s, g, o.Since);
                    (s.Nutrition, s.FedAt, s.Access, s.Power, s.PowerAt) = (o.Nutrition, o.FedAt, o.Access, o.Power, o.PowerAt);
                }
            foreach (var s in states.Values)
                if (!s.Owned && NpcFor(s)) ToNpc(s);
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or FormatException)
        {
            Log.Error("world", $"territory owners not loaded: {e.Message}");
        }
    }

    /// <summary>A guild was deleted (<see cref="Guilds"/>): its castles and territories are unowned (its rows go with it in the database).</summary>
    public void GuildDeleted(Guild g)
    {
        foreach (var s in All.Where(s => s.Owner == g.Id).ToList()) SetOwner(s, null, UtcNow);
    }

    // ---- the chests

    private void Chests(CastleObject c, Map map, DateTime now)
    {
        var f = c.State.Fight;
        if (f.Chest is { } chest)
        {
            if (chest.Map != map || !map.Objects.ContainsKey(chest.Id)) DropChest(c, null);
            else if (now >= f.ChestUntil) DropChest(c, "its 30 minutes are up");
        }
        if (!S.CastleChests) return;
        var schedule = Rules.ChestSchedule(c.Type.ChestSubtype);
        if (schedule.LastSlot(now, S.CastleChestsHourly) is not { } slot || f.ChestSlot is { } done && slot <= done) return;
        f.ChestSlot = slot;
        if (S.CastleChestNeedsOwnership && !c.State.Owned) return;
        var tier = map.Template?.Name.Tier ?? 5;
        if (schedule.DrawAt(slot, tier)?.Roll(map.Random) is not { } spawnpoint)
        {
            Log.Info("world", $"castle {c.State.Territory.Name}: no chest at {slot:HH:mm} (the draw's None)");
            return;
        }
        SpawnChest(c, map, spawnpoint, slot, now);
    }

    /// <summary>The chest of LootChestSpawnpoint <paramref name="spawnpoint"/> on the castle's chest spot, Unlocked, until <paramref name="slot"/> + its despawn time.</summary>
    private LootChest? SpawnChest(CastleObject c, Map map, string spawnpoint, DateTime slot, DateTime now)
    {
        var f = c.State.Fight;
        if (world.Clusters is not { } store) return null;
        var data = store.LootChests;
        if (!data.Spawnpoints.TryGetValue(spawnpoint, out var behaviour))
        {
            Combat.LogOnce("castle-chest:" + spawnpoint, $"castle chest {spawnpoint} is no LootChestSpawnpoint of lootchests.xml: not spawned");
            return null;
        }
        var until = slot + TimeSpan.FromSeconds(behaviour.DespawnSeconds > 0 ? behaviour.DespawnSeconds : 1800);
        if (now >= until) return null;
        var point = new LootChestSpawn
        {
            Key = new SpawnKey(map.Cluster, "castle-chest", map.CustomSpawns++), Tile = "", Position = c.ChestSpot, Direction = c.Direction,
            Component = new XElement("lootchestspawnpoint", new XAttribute("spawnbehaviour", spawnpoint)), SpawnBehaviour = spawnpoint, Behaviour = behaviour,
        };
        if (world.Dungeons.MakeChest(map, point, data, state: 6, lootTier: map.Template?.Name.Tier ?? 5) is not { } chest) return null;
        if (f.Chest is not null) DropChest(c, "a new one comes");
        map.AddObject(chest);
        (f.Chest, f.ChestRarity, f.ChestUntil) = (chest, chest.State.State, until);
        _castleChests[chest.Id] = c.State;
        Log.Info("world", $"castle {c.State.Territory.Name}: {chest} until {until:HH:mm}");
        Resend(c);
        return chest;
    }

    private void DropChest(CastleObject c, string? why)
    {
        var f = c.State.Fight;
        if (f.Chest is not { } chest) return;
        _castleChests.Remove(chest.Id);
        f.Chest = null;
        if (why is not null) Combat.RemoveBag(chest, why);
        Resend(c);
    }

    /// <summary>
    /// Whether <paramref name="p"/> may not open <paramref name="chest"/> (<see cref="RandomDungeons.OpenChest"/>): a castle chest with
    /// Castles:ChestNeedsOwnership is its owner guild's (lootchests requirescastleownership; the client's @TREASURE_CHEST_DOES_NOT_OWN_CASTLE).
    /// </summary>
    public string? ChestRefused(Player p, LootChest chest)
    {
        if (!_castleChests.TryGetValue(chest.Id, out var s) || !S.CastleChestNeedsOwnership) return null;
        return world.Guilds.Of(p.Character.Id) is { } g && g.Id == s.Owner ? null : "the castle chest is its owner guild's";
    }

    // ---- season points

    private void Score(DateTime now)
    {
        if (S.CastleSeasonPoints <= 0) return;
        var tick = TimeSpan.FromMinutes(S.ScoreTickMinutes);
        foreach (var s in All)
        {
            if (!s.Territory.IsCastle || !s.Owned) continue;
            var f = s.Fight;
            f.LastScore ??= now;
            var n = (int)Math.Floor((now - f.LastScore.Value) / tick);
            if (n <= 0) continue;
            f.LastScore += tick * n;
            var points = (long)Math.Round(S.CastleSeasonPoints * S.SeasonPointsFactor * n);
            if (points > 0 && world.Guilds.ById(s.Owner) is { } g)
            {
                world.GuildGoals.AddActivityPoints(g, points, GuildGoals.TerritoryHold);
                Log.Info("guild", $"castle {s.Territory.Name}: +{points} season points for '{g.Name}'");
            }
        }
    }

    // ---- GM

    /// <summary>
    /// .castle: <paramref name="action"/> on <paramref name="state"/>'s castle: capture (the defenders go, the capture zone opens), reset (the
    /// fight fails), chest (a chest of the schedule's draw now, None skipped), owner (the guild <paramref name="guild"/>), free (unowned).
    /// </summary>
    public string Gm(TerritoryState state, string action, Guild? guild)
    {
        var now = UtcNow;
        switch (action)
        {
            case "owner":
                if (guild is null) return "no guild given";
                SetOwner(state, guild, now);
                return $"{state.Territory.Name}: owner '{guild.Name}'";
            case "free":
                SetOwner(state, null, now);
                return $"{state.Territory.Name}: unowned";
        }
        if (CastleOf(state) is not { Map: { } map } c) return $"{state.Territory.Name}: its map is not loaded";
        var f = state.Fight;
        switch (action)
        {
            case "capture":
                foreach (var m in f.Mobs.Where(m => Standing(m, map))) SpawnManager.Despawn(m, world, respawn: false);
                if (f.Phase == CastleFight.Idle) SetPhase(c, map, CastleFight.Guards, now);
                OpenZone(c, map, now);
                return $"{state.Territory.Name}: the capture zone is open";
            case "reset":
                Fail(c, map, now);
                return $"{state.Territory.Name}: the fight is reset";
            case "chest":
                var schedule = Rules.ChestSchedule(c.Type.ChestSubtype);
                var draw = schedule.DrawAt(now, map.Template?.Name.Tier ?? 5);
                var spawnpoint = draw?.Chests.Count > 0 ? draw.Chests[map.Random.Next(draw.Chests.Count)].Spawnpoint : null;
                return spawnpoint is not null && SpawnChest(c, map, spawnpoint, now, now) is { } chest ? $"{state.Territory.Name}: {chest}" : "no chest could be made";
            default:
                return "actions: capture, reset, chest, owner <guild>, free";
        }
    }

    /// <summary>.territory of a castle: its fight, claim and chest.</summary>
    private IEnumerable<string> DescribeCastle(TerritoryState s)
    {
        var f = s.Fight;
        var c = CastleOf(s);
        var inv = CultureInfo.InvariantCulture;
        yield return $"castle: {(c is null ? "its map is not loaded" : f.ToString())}"
            + (f.GuardsBackAt is { } back ? "; guards back at " + back.ToString("HH:mm:ss", inv) : "")
            + (f.Chest is { Map: not null } chest ? $"; chest {chest.Template.Name} until " + f.ChestUntil.ToString("HH:mm", inv) : "; no chest")
            + (s.Owned ? "; held since " + s.Since.ToString("yyyy-MM-dd HH:mm", inv) : "");
    }
}
