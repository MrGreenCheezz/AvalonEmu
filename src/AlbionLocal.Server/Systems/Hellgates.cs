using System.Globalization;
using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>A hellgate's state (the client's buf, HellgateStatus 454 key 2 / 455 key 1; the tracker HUD, SPEC §3.6).</summary>
public enum HellgateState
{
    /// <summary>None (the tracker hidden).</summary>
    None = 0,

    /// <summary>Objective 1: the points (the progress bar points / pointsrequirement).</summary>
    Progress = 1,

    /// <summary>Objective 2: defeat the enemy (the guardian, or the enemy team).</summary>
    Defeat = 2,

    /// <summary>The enemy is defeated: the objective chest stands ("enemy defeated" checked).</summary>
    Defeated = 3,

    /// <summary>The reward is claimed: the exit portal stands ("reward claimed" checked).</summary>
    Claimed = 4,

    /// <summary>The party fell (every member inside down at once): the exit portal, no objective chest [inference: state 6, hidden].</summary>
    Lost = 6,

    /// <summary>The instance closes [inference: state 7, hidden].</summary>
    Closed = 7,
}

/// <summary>
/// One hellgate (stageL hellgates SPEC §5.4): its instance id "@HELLCLUSTER@&lt;guid&gt;", the world.xml map it copies, the mode and whether its
/// second objective is an enemy team of bots (a versus mode), the party and its members with their way back, the start points, the group
/// scaling of the players sent in, the state, the points, the guardian, the enemy team, the chests, the exit portal and the times.
/// </summary>
public sealed class HellgateInstance
{
    public required Guid Guid { get; init; }
    public string Id => HellgateData.InstanceId(Guid);
    public required HellgateData.Mode Mode { get; init; }

    /// <summary>The world.xml cluster (HELLGATE-NN-&lt;mode&gt;-01[-HRD]; ChangeCluster key 1).</summary>
    public required string World { get; init; }

    /// <summary>Whether its objective 2 is an enemy team of bots (Hellgates:VersusModes), else a demon guardian.</summary>
    public required bool Versus { get; init; }

    /// <summary>The party it was made for (0 = a GM's .hg enter without one).</summary>
    public required int Party { get; init; }

    /// <summary>The members (sent in, not left) with their way back (the place at the shrine).</summary>
    public Dictionary<Guid, DungeonReturn> Members { get; } = [];

    /// <summary>Characters sent in and not joined yet, with the clock time until which each counts as inside.</summary>
    public Dictionary<Guid, TimeSpan> Arriving { get; } = [];

    /// <summary>The players sent in at the start (the group scaling's n) and the factors on the mobs' health and damage.</summary>
    public required int Players { get; init; }
    public required double HealthFactor { get; init; }
    public required double DamageFactor { get; init; }

    /// <summary>Start A (the party's arrival, facing the objective chest) and start B (the enemy team's), SPEC §5.4.</summary>
    public required Arrival Start { get; init; }
    public required Vector2 StartB { get; init; }

    public required TimeSpan StartedAt { get; init; }

    public HellgateState State { get; set; } = HellgateState.Progress;
    public int Points { get; set; }

    /// <summary>The quarters of the requirement told so far (hg.progress every 25 %).</summary>
    public int Told { get; set; }

    /// <summary>The held-back BOSS spawn point (maps 06, 10), the guardian's mob once spawned.</summary>
    public MobSpawnPoint? GuardianPoint { get; set; }
    public long GuardianId { get; set; }

    /// <summary>The living minibosses of the map (maps 03, 04, 07): the last one's death opens the REWARD layer's CHEST.</summary>
    public HashSet<long> Minibosses { get; } = [];

    /// <summary>The REWARD layer's locked chests (CHEST after the minibosses, BOSS after the guardian), the objective chest of state 3.</summary>
    public List<LootChest> RewardChests { get; } = [];
    public LootChest? ObjectiveChest { get; set; }

    /// <summary>The enemy team (bots of BotRole.Hellgate) of a versus run, and whether the win was over them (the pvp chest) or the guardian (the pve one).</summary>
    public List<Player> Enemies { get; } = [];
    public bool FoughtTeam { get; set; }

    /// <summary>The bots of the enemy team beaten so far (BotCombat.Defeated): one gone without it (.bot off) brings the guardian instead.</summary>
    public HashSet<Player> Beaten { get; } = [];

    /// <summary>When a versus run's remaining demons leave (mobdespawndelay after objective 2), when an unopened objective chest counts as claimed.</summary>
    public TimeSpan? DespawnAt { get; set; }
    public TimeSpan? ClaimDue { get; set; }

    public HellgateExitPortal? Portal { get; set; }

    /// <summary>The claim or the loss (the closing FinishedLingerSeconds later).</summary>
    public TimeSpan? FinishedAt { get; set; }
    public TimeSpan? EmptySince { get; set; }
    public bool Warned { get; set; }
    public bool Closed { get; set; }

    // ---- phase B (SPEC §7) ----

    /// <summary>The hellgates of the chain before this one (0 for the first), and the chests' loot factor of it (the mode's bct.af(chain)).</summary>
    public int Chain { get; init; }
    public double ChainFactor { get; init; } = 1;

    /// <summary>The chain exit after the claim (a walk-in 324 at CHAIN_EXIT), the map the next hellgate takes (picked at the claim) and that hellgate once made.</summary>
    public CorruptedExitPortal? ChainExit { get; set; }
    public string? NextWorld { get; set; }
    public HellgateInstance? Next { get; set; }

    /// <summary>
    /// Party vs party (Hellgates:Matchmaking Players): the second party (0 = none) and its members (start B); <see cref="Match"/> once the
    /// second objective is the fight between the two; the team that fell (0 none, 1 the first party, 2 the second): its members see state 6.
    /// </summary>
    public int PartyB { get; set; }
    public HashSet<Guid> TeamB { get; } = [];
    public bool Match { get; set; }
    public int LostTeam { get; set; }

    /// <summary>Since when a party of a match has had no member inside (it has left after Hellgates:EmptyCloseSeconds).</summary>
    public Dictionary<int, TimeSpan> TeamAbsentSince { get; } = [];

    /// <summary>The restricted area (Hellgates:RestrictedArea): when it starts, its phase under way, the next damage second and each member's damage stacks.</summary>
    public TimeSpan? AreaDue { get; set; }
    public HellgateArea? Area { get; set; }
    public TimeSpan NextAreaHit { get; set; }
    public Dictionary<Guid, int> AreaStacks { get; } = [];

    /// <summary>The team of character <paramref name="id"/>: 2 in the second party, else 1.</summary>
    public int TeamOf(Guid id) => TeamB.Contains(id) ? 2 : 1;

    /// <summary>Whether character <paramref name="id"/> is a member of the team that fell.</summary>
    public bool LostBy(Guid id) => LostTeam != 0 && TeamOf(id) == LostTeam;

    public override string ToString() => $"hellgate {Mode.Name} {World} {Id}" + (Chain > 0 ? $" (chain {Chain})" : "");
}

/// <summary>
/// A phase of a hellgate's restricted area (SPEC §3.8, HellgateRestrictedAreaUpdate 452 → ddv.u(aqd) → c4s → HellgateFXManager: key 2 StartTime,
/// 3 Center, 4 StartRadius, 5 EndRadius, 6 StartWidth, 7 EndWidth, 8 RadiusPerSecond [code: HellgateFXManager.OnRestricedAreaUpdate 0x1819019F0]):
/// the radius shrinks from <see cref="From"/> to <see cref="To"/> at <see cref="Velocity"/> m/s since <see cref="At"/>, the ring's width with it.
/// </summary>
public sealed record HellgateArea(Vector2 Center, float From, float To, float WidthFrom, float WidthTo, float Velocity, long Start, TimeSpan At, bool Final)
{
    /// <summary>The radius <paramref name="now"/> (<see cref="World.Clock"/> time).</summary>
    public float RadiusAt(TimeSpan now) => Math.Max(To, From - Velocity * (float)Math.Max(0, (now - At).TotalSeconds));

    /// <summary>The ring's width at radius <paramref name="radius"/> (from the start's to the end's as the radius shrinks).</summary>
    public float WidthAt(float radius) => From <= To ? WidthTo : WidthFrom + (WidthTo - WidthFrom) * Math.Clamp((From - radius) / (From - To), 0, 1);

    /// <summary>When the radius reaches <see cref="To"/>.</summary>
    public TimeSpan Reached => At + TimeSpan.FromSeconds(Velocity > 0 ? Math.Max(0, From - To) / Velocity : 0);
}

/// <summary>
/// The hellgates, phase A (analysis/work/stageL/hellgates/SPEC.md, "SPEC" here, §5, §6): the Hell Dens' shrines (NewHellgateShrine 400, its
/// keys 4/5 per viewer: Hellgates:ShrineMode), UseHellgateShrine (op 255) with its channel (273 to the player, 401 to its party only: key 4
/// overwrites each receiver's shrine mode), the party's lock (274 / 275 to the player and its observers; a client cancel during the lock is
/// ignored), the instance as a world.xml cluster copy (the push {0 "@HELLCLUSTER@&lt;guid&gt;", 1 map}, SelectCharacter key 2 on a relog), the
/// computed start points, the group scaling, the tracker (454, 453, 455), the points of the demons, the second objective (a demon guardian, or in
/// a versus mode an enemy team of bots that is Hostile to the party alone), the chests (objective, REWARD layer), the exit portal (250; the
/// client's escape {0: 0, 3: true} leads out), the wipe, the timers and the closing. Only messages the unmodified client reads.
/// Phase B (SPEC §7, Hellgates.Matches.cs): the chain, the restricted area, the spawn buffs, the infamy and the party-vs-party match.
/// <see cref="World.Update"/> runs <see cref="Update"/>. Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class Hellgates(World world)
{
    /// <summary>The escape slot (21): ESCAPE_HELLGATE (900 s) in states 1-2, ESCAPE_DUNGEON (7 s) after (ddv.as), a server cast ending in DungonEscapeReady 467 (FakeSlotCasts.StartEscape).</summary>
    public const byte EscapeSlot = 21;

    /// <summary>A member stands at the shrine within opengatedistance (10 m) of it: it is counted for the party's size.</summary>
    public const double DefaultGateDistance = 10;

    /// <summary>
    /// The client's use distance (it sends op 255 within 5 m): a member that close and not channeling holds the party's lock (for ChannelSeconds
    /// at most), and a member done channeling that walks farther (+ Transfer:PositionTolerance) is out of the entry.
    /// </summary>
    public const double UseDistance = 5;

    /// <summary>
    /// How far from the shrine's centre a member's way back lies (SPEC §5.3): outside its 7x7 hellgateshrine trigger (4.95 m to a corner), so a
    /// member coming back to a Fixed shrine is not channeled again at once by the client's walk-in auto-use (OnTriggerStay → DoAutoUse, op 255).
    /// </summary>
    public const double ReturnDistance = 6;

    /// <summary>How long a lock waits at most for the map to be read and its walk grid built (then a fallback start, or a cancel).</summary>
    public const double ResolveSeconds = 30;

    /// <summary>A member's channel under way: where it began and its health then, when it is done; done = waiting for the party.</summary>
    private sealed class Channel
    {
        public required Vector2 From { get; init; }
        public required float Health { get; init; }
        public required TimeSpan Due { get; init; }
        public bool Done { get; set; }
    }

    /// <summary>
    /// A party's entry at a shrine (SPEC §5.3): the shrine and its map, the mode (the first member's; another is refused), the map picked for it (or the party's live
    /// instance a late member joins), the members' channels, and once they are done the lock: who goes, when, and since when it waits for data.
    /// </summary>
    private sealed class Entry
    {
        public required int Party { get; init; }
        public required HellgateShrine Shrine { get; init; }
        public required Map Map { get; init; }
        public required HellgateData.Mode Mode { get; init; }
        public required string World { get; init; }
        public HellgateInstance? Into { get; set; }
        public Dictionary<Player, Channel> Channels { get; } = [];
        public List<Player> Locked { get; } = [];
        public TimeSpan? LockDue { get; set; }

        /// <summary>Since when the done members wait for one at the shrine that does not channel (the lock goes on without it ChannelSeconds later).</summary>
        public TimeSpan? WaitingSince { get; set; }
    }

    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Dictionary<string, HellgateInstance> _instances = new(StringComparer.Ordinal);

    /// <summary>The characters' modes chosen with .hg mode (not saved: Hellgates:DefaultMode after a restart).</summary>
    private readonly Dictionary<Guid, int> _modes = [];

    /// <summary>The start points of each world cluster once computed (SPEC §5.4: once per map, from its walk grid).</summary>
    private readonly Dictionary<string, (Arrival A, Vector2 B)> _starts = new(StringComparer.Ordinal);

    private HellgateOptions Options => world.Config.Hellgates;

    private HellgateSettings Settings => world.HellgateSettings;

    private HellgateData? Data => world.Clusters?.HellgatesIfLoaded;

    private Random Rng => _rng ??= world.MapSeed is { } seed ? new Random(seed) : new Random();
    private Random? _rng;

    public IReadOnlyCollection<HellgateInstance> Instances => _instances.Values;

    /// <summary>The live instance of id <paramref name="cluster"/>; null for any other cluster or a closed one.</summary>
    public HellgateInstance? Find(string? cluster) => cluster is not null && _instances.TryGetValue(cluster, out var i) && !i.Closed ? i : null;

    public bool IsLive(string cluster) => _instances.ContainsKey(cluster);

    /// <summary>The world.xml cluster a map of live instance <paramref name="cluster"/> takes its data from (World.TemplateOf, SelectCharacter key 2).</summary>
    public string? WorldOf(string cluster) => Find(cluster)?.World;

    /// <summary>
    /// The live instance of party <paramref name="party"/> (its first or, phase B, its second) that a late member may still join (before the win,
    /// with room). Phase B: never a chain's (only the winners the chain exit carried go in), and the second party only before the second
    /// objective or into its match (once it began against bots sized for the first party, a late second party would share their win).
    /// </summary>
    private HellgateInstance? OpenFor(int party, int coming) => party == 0 ? null
        : _instances.Values.FirstOrDefault(i => !i.Closed && i.Chain == 0 && i.State <= HellgateState.Defeat && TeamCount(i, party) + coming <= i.Mode.TeamSize
            && (i.Party == party || i.PartyB == party && (i.State == HellgateState.Progress || i.Match)));

    /// <summary>The members of <paramref name="i"/> on <paramref name="party"/>'s side (the second party's, or the first's).</summary>
    private static int TeamCount(HellgateInstance i, int party) => i.Members.Keys.Count(id => i.TeamB.Contains(id) == (party != 0 && party == i.PartyB));

    /// <summary>The mode id of <paramref name="p"/>'s .hg mode, else Hellgates:DefaultMode's (1 without hellgate.xml).</summary>
    public int ModeOf(Player p) => _modes.TryGetValue(p.Character.Id, out var m) ? m : Data?.Find(Options.DefaultMode)?.Id ?? 1;

    /// <summary>NewHellgateShrine keys 4 and 5 for <paramref name="viewer"/> (SPEC §5.3): Fixed → (its mode, true); Gui → (0, false).</summary>
    public (int Mode, bool Fixed) ShrineKeys(Player viewer) => Options.ShrineMode == HellgateShrineMode.Fixed ? (ModeOf(viewer), true) : (0, false);

    /// <summary>Hellgates:KnockdownSeconds for a PvP knockdown of <paramref name="p"/> inside a hellgate (Knockdown.Seconds); null elsewhere or unset.</summary>
    public double? KnockdownSeconds(Player p) => Find(p.Map?.Cluster) is not null ? Options.KnockdownSeconds : null;

    private static IEnumerable<Player> InMap(Map? map) => map?.Players.Values ?? Enumerable.Empty<Player>();

    /// <summary>The players of <paramref name="i"/>'s map now.</summary>
    private IEnumerable<Player> Inside(HellgateInstance i) => InMap(world.Maps.Find(i.Id));

    // ---- the shrines (SPEC §3.1, §5.3) ----

    /// <summary>
    /// At the fill of a map (MapSpawner): a Hell Den's hellgateshrine tiles as shrines (400); an instance's map spawns its mobs once with the
    /// group scaling, its guardian's BOSS points held back (<see cref="Map.ScriptedSpawns"/>), its REWARD layer's chests locked.
    /// </summary>
    internal IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (!Options.Enabled) return [];
        if (Find(map.Cluster) is not { } i)
            return HellgateData.IsInstance(map.Cluster) ? [] : [.. template.HellgateShrines.Select(s => new HellgateShrine(world.ObjectIds.Next(), s))];
        map.NoRespawn = true; // mobspawns defaulttimefactor 9999: spawn once
        (map.MobHealthFactor, map.MobDamageFactor) = (i.HealthFactor, i.DamageFactor);
        map.ScriptedSpawns = template.MobSpawnPoints.Select(m => m.SpawnBehavior).Where(HellgateData.IsBoss).OfType<string>().ToHashSet(StringComparer.Ordinal);
        i.GuardianPoint = template.MobSpawnPoints.FirstOrDefault(m => HellgateData.IsBoss(m.SpawnBehavior) && m.Block is not null);
        var objects = new List<WorldObject>();
        if (world.Clusters?.LootChestsIfLoaded is { } chests)
            foreach (var point in template.LootChests)
                if (world.Dungeons.MakeChest(map, point, chests, RandomDungeons.BossLocked, lootFactor: i.ChainFactor) is { } chest)
                {
                    i.RewardChests.Add(chest);
                    objects.Add(chest);
                }
        return objects;
    }

    /// <summary>After an instance's fill (its mobs on the map): its minibosses noted; without one the REWARD layer's CHEST opens at once.</summary>
    internal void Filled(Map map)
    {
        if (Find(map.Cluster) is not { } i) return;
        foreach (var mob in map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && HellgateData.IsMiniboss(m.Spawn.SpawnBehavior))) i.Minibosses.Add(mob.Id);
        if (i.Minibosses.Count == 0) UnlockRewards(i, map, boss: false, "no miniboss guards it");
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{i}: {map.Objects.Values.OfType<Mob>().Count()} demons (health x{i.HealthFactor:0.###}), {i.Minibosses.Count} miniboss(es), the guardian {(i.GuardianPoint?.SpawnBehavior ?? "at the chest")}, {i.RewardChests.Count} reward chest(s)"));
    }

    /// <summary>
    /// .hg mode: <paramref name="p"/>'s mode is now <paramref name="mode"/>; each shrine of its map gets UpdateHellgateShrine {1 0, 2 false, 3 its
    /// party, 4 the mode} (to that player only: key 4 replaces the shrine's mode in its client, ckh.ajv), and the chat line. At a shrine where its
    /// party's entry runs the 401 keeps the entry's count and mode (the FX count goes on); the new mode is the shrine's once that entry ends.
    /// </summary>
    public void SetMode(Player p, HellgateData.Mode mode)
    {
        _modes[p.Character.Id] = mode.Id;
        var party = world.Parties.Of(p.Character.Id)?.Id ?? 0;
        foreach (var shrine in p.Map?.Objects.Values.OfType<HellgateShrine>() ?? [])
        {
            var running = _entries.Values.FirstOrDefault(e => e.Shrine == shrine && (e.Channels.ContainsKey(p) || e.Locked.Contains(p) || (party != 0 && e.Party == party)));
            p.Session.Send(running is null ? new UpdateHellgateShrineEvent { ddn = shrine.Id, d31 = party, d32 = mode.Id } : ShrineUpdate(running, success: false, running.Mode.Id));
        }
        TellMode(p, mode);
    }

    private void TellMode(Player p, HellgateData.Mode mode) =>
        Expeditions.Tell(p, Settings.Text("hg.mode", ("mode", mode.Name), ("teamsize", mode.TeamSize), ("ip", mode.ItemPower.ToString("0", CultureInfo.InvariantCulture))));

    /// <summary>
    /// UseHellgateShrine (op 255 {0 time, 1 shrine, 2 cancel, 3 mode}, SPEC §3.2, §5.3). A cancel ends the player's own channel while it runs
    /// (275, 401) and is a no-op otherwise (the client sends one by itself when its channel state resets, cvp.ah6: the lock goes on). A start is
    /// checked (the shrine on the map within opengatedistance + Transfer:PositionTolerance, the mode offered, alive, not mounted, not in a
    /// hellgate, Hellgates:CheckItemPower, a party for a team mode with at most teamsize members at the shrine), then its channel: 273 {1 true}
    /// to the player, 401 to its party on the map, the chat lines. Returns why it was refused, or null (RC 0).
    /// </summary>
    public string? Use(Player p, long shrineId, bool cancel, int modeId)
    {
        var entry = _entries.Values.FirstOrDefault(e => e.Channels.ContainsKey(p) || e.Locked.Contains(p));
        if (cancel)
        {
            if (entry is not null && entry.Channels.TryGetValue(p, out var running) && !running.Done) Cancel(entry, p, "cancelled by the client");
            return null;
        }
        if (!Options.Enabled) return "hellgates are off here";
        if (Data is not { } data) return "hellgate.xml is not read";
        if (p.Map is not { } map || !map.Objects.TryGetValue(shrineId, out var o) || o is not HellgateShrine shrine) return "no hellgate shrine here";
        var range = data.Global("opengatedistance", DefaultGateDistance) + world.Config.Transfer.PositionTolerance;
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, shrine.Position) > range) return string.Create(CultureInfo.InvariantCulture, $"too far from the shrine ({Vector2.Distance(at, shrine.Position):0.#} m)");
        if (data.At(modeId) is not { TeamSize: > 0 } mode || !Options.Modes.Contains(mode.Name)) return $"mode {modeId} is not offered";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (p.IsMounted) return "mounted";
        // A start again at the shrine it channels at (the client's auto-use once more): RC 0, the channel or lock goes on; elsewhere refused.
        if (entry is not null) return entry.Shrine == shrine ? null : "already channeling at another shrine";
        if (Find(map.Cluster) is not null) return "in a hellgate already";
        if (Options.CheckItemPower && Expeditions.AverageItemPower(p) is var ip && ip < mode.ItemPower)
            return string.Create(CultureInfo.InvariantCulture, $"{mode.Name} needs an average item power of {mode.ItemPower:0} (yours {ip:0})");
        var party = world.Parties.Of(p.Character.Id);
        if (party is null && mode.TeamSize > 1) return "you need a party";
        var partyId = party?.Id ?? 0;
        var present = AtShrine(partyId, p, shrine, range).Count;
        var into = OpenFor(partyId, present);
        var inside = into is null ? 0 : TeamCount(into, partyId);
        if (present + inside > mode.TeamSize) return string.Create(CultureInfo.InvariantCulture, $"the party is too large for {mode.Name} ({present + inside} > {mode.TeamSize})");
        // Each member consents to the mode it channels for (a versus mode's enemy team): one of another mode is refused, never switched.
        if (into is not null && into.Mode != mode) return $"your party's hellgate is {into.Mode.Name}; choose it with .hg mode to join";
        if (!_entries.TryGetValue(partyId, out var e))
        {
            e = new Entry { Party = partyId, Shrine = shrine, Map = map, Mode = into?.Mode ?? mode, World = into?.World ?? PickMap(data, mode) ?? "", Into = into };
            if (e.World.Length == 0) return $"no map of {mode.Name} is known";
            _entries[partyId] = e;
        }
        else if (e.Shrine != shrine) return "your party channels at another shrine";
        else if (e.LockDue is not null) return "your party is entering; use the shrine again in a moment";
        else if (e.Mode != mode) return $"your party channels for {e.Mode.Name}; choose it with .hg mode, or wait until it is in";
        world.PrefetchCluster(e.World);
        e.Channels[p] = new Channel { From = at, Health = p.Health.At(world.GameNow), Due = world.Now + TimeSpan.FromSeconds(Options.ChannelSeconds) };
        p.Session.Send(new UsingHellgateShrineEvent { ddn = p.Id, d6a = true });
        UpdateShrine(e, success: false);
        TellMode(p, e.Mode);
        if (Options.VersusModes.Contains(e.Mode.Name) && !world.Config.PvpLethalDeath)
            Expeditions.Tell(p, Settings.Text("hg.versus", ("points", e.Mode.PointsRequirement),
                ("n", Options.EnemyTeamSize == HellgateEnemyTeam.Party ? Math.Max(1, present + inside) : e.Mode.TeamSize)));
        Log.Info("world", $"'{p.Character.Name}' channels at the hellgate shrine for {e.Mode.Name} ({(e.Into is { } x ? $"into {x}" : e.World)})");
        return null;
    }

    /// <summary>The first time the done members of <paramref name="e"/> wait for <paramref name="idle"/>: hg.waiting to each; returns <paramref name="now"/>.</summary>
    private TimeSpan Waiting(Entry e, List<Player> idle, TimeSpan now)
    {
        var names = string.Join(", ", idle.Select(m => m.Character.Name));
        foreach (var p in e.Channels.Keys) Expeditions.Tell(p, Settings.Text("hg.waiting", ("names", names), ("seconds", Math.Round(Options.ChannelSeconds))));
        Log.Info("world", $"hellgate entry of party {e.Party} waits for {names}");
        return now;
    }

    /// <summary>The members of <paramref name="party"/> (or <paramref name="p"/> alone) on the shrine's map within <paramref name="range"/> of it.</summary>
    private List<Player> AtShrine(int party, Player p, HellgateShrine shrine, double range)
    {
        var utc = DateTime.UtcNow;
        var near = InMap(shrine.Map).Where(m => Vector2.Distance(m.PositionAt(utc), shrine.Position) <= range);
        return party == 0 ? [p] : [.. near.Where(m => m == p || world.Parties.Of(m.Character.Id)?.Id == party)];
    }

    /// <summary>
    /// UpdateHellgateShrine (401 {1 channeling, 2 success, 3 party, 4 mode}) to the entry's party members on its map, never to anyone else (SPEC
    /// §3.4). Once a cancelled entry is gone, a Fixed shrine's key 4 is each member's own mode again (its next walk-in uses that one).
    /// </summary>
    private void UpdateShrine(Entry e, bool success)
    {
        var over = !success && !_entries.ContainsKey(e.Party) && Options.ShrineMode == HellgateShrineMode.Fixed;
        foreach (var m in InMap(e.Map).Where(m => e.Channels.ContainsKey(m) || e.Locked.Contains(m) || (e.Party != 0 && world.Parties.Of(m.Character.Id)?.Id == e.Party)))
            m.Session.Send(ShrineUpdate(e, success, over ? ModeOf(m) : e.Mode.Id));
    }

    private static UpdateHellgateShrineEvent ShrineUpdate(Entry e, bool success, int mode) => new()
    {
        ddn = e.Shrine.Id, d3z = (byte)Math.Min(byte.MaxValue, success ? e.Locked.Count : e.Channels.Count), d30 = success, d31 = e.Party, d32 = mode,
    };

    /// <summary>
    /// <paramref name="p"/>'s channel in <paramref name="e"/> ends without the entry (a move, damage, a knockdown, its cancel, the lock failing):
    /// EnteringHellgateLockCancel (275) to it and its observers (cw5.aft and cr1.aft: back to state 0), the party's count updated.
    /// </summary>
    private void Cancel(Entry e, Player p, string why)
    {
        e.Channels.Remove(p);
        e.Locked.Remove(p);
        if (p.Map == e.Map && p.Session.Player == p) p.SendAround(new EnteringHellgateLockCancelEvent { ddn = p.Id });
        if (e.Channels.Count == 0 && e.Locked.Count == 0) _entries.Remove(e.Party);
        UpdateShrine(e, success: false);
        Log.Info("world", $"'{p.Character.Name}''s hellgate channel is cancelled: {why}");
    }

    /// <summary>
    /// A map of <paramref name="mode"/>'s mapset by weight (content/hellgates.json Maps over hellgate.xml's; the 5-weight boss maps ×
    /// Hellgates:BossMapFactor) among those world.xml has, another than <paramref name="except"/> (phase B: the chain's last) while there is one;
    /// null when none.
    /// </summary>
    private string? PickMap(HellgateData data, HellgateData.Mode mode, string? except = null)
    {
        var known = world.Clusters?.WorldIfLoaded;
        var pool = data.MapsOf(mode).Select(m => (m.Id, Weight: (Settings.Maps.TryGetValue(m.Id, out var w) ? w : m.Weight) * (m.Weight <= 5 ? Options.BossMapFactor : 1)))
            .Where(m => m.Weight > 0 && known?.Find(m.Id) is not null).ToList();
        if (except is not null && pool.Count > 1) pool.RemoveAll(m => m.Id == except);
        if (pool.Count == 0) return null;
        var roll = Rng.NextDouble() * pool.Sum(m => m.Weight);
        foreach (var (id, weight) in pool)
        {
            if (roll < weight) return id;
            roll -= weight;
        }
        return pool[^1].Id;
    }

    // ---- the instance (SPEC §5.4) ----

    /// <summary>
    /// The start points of <paramref name="t"/> (SPEC §5.4) on its walk grid: A the component cell farthest from the objective chest at least
    /// Hellgates:StartClearance from every mob spawn point, facing the chest; B the cell farthest from A with the same clearance and at least
    /// 60 m from the chest. Without such a cell, among the 10 % of cells farthest from the chest (from A for B) the one farthest from the
    /// nearest spawn point. Null without a component.
    /// </summary>
    public static (Arrival A, Vector2 B)? StartsOf(ClusterTemplate t, WalkGrid grid, double clearance)
    {
        var cells = grid.ComponentCentres().ToList();
        if (cells.Count == 0) return null;
        var chest = t.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ObjectiveChest)?.Position ?? t.Origin + t.Size / 2;
        var spawns = t.MobSpawnPoints.Select(m => m.Position).ToList();
        float Clear(Vector2 c) => spawns.Count == 0 ? float.MaxValue : spawns.Min(s => Vector2.Distance(s, c));
        Vector2 Farthest(Vector2 from, Func<Vector2, bool> ok)
        {
            var order = cells.OrderByDescending(c => Vector2.DistanceSquared(c, from)).ToList();
            foreach (var c in order.Where(ok))
                if (Clear(c) >= clearance) return c;
            return order.Take(Math.Max(1, order.Count / 10)).MaxBy(Clear);
        }
        var a = Farthest(chest, _ => true);
        var b = Farthest(a, c => Vector2.Distance(c, chest) >= 60);
        return (new Arrival(t.Id, a, MotionMaster.Facing(a, chest)), b);
    }

    /// <summary>
    /// The start points of world cluster <paramref name="map"/> once its template is read and its walk grid built (the bots' nav, asked for here:
    /// a build starts on a pool thread); null while either is missing.
    /// </summary>
    private (Arrival A, Vector2 B)? StartsFor(string map)
    {
        if (_starts.TryGetValue(map, out var known)) return known;
        if (world.CachedCluster(map) is not { } template || !world.Bots.Nav.TryGet(template, out var nav)) return null;
        if (StartsOf(template, nav.Grid, Options.StartClearance) is not { } starts) return null;
        _starts[map] = starts;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"hellgate {map}: start A ({starts.A.Position.X:0.#}, {starts.A.Position.Y:0.#}), B ({starts.B.X:0.#}, {starts.B.Y:0.#}) on {nav.Grid.ComponentCells} walkable cells"));
        return starts;
    }

    /// <summary>
    /// A new instance of <paramref name="mode"/> on <paramref name="map"/> for <paramref name="players"/> players of party <paramref name="party"/>
    /// (the map read; <paramref name="starts"/> its start points, else the EXIT_PORTAL marker): the group scaling (Hellgates:GroupScaling), state 1.
    /// </summary>
    public HellgateInstance Create(HellgateData.Mode mode, string map, int party, int players, (Arrival A, Vector2 B)? starts = null, int chain = 0)
    {
        var template = world.CachedCluster(map) ?? throw new InvalidOperationException($"cluster {map} is not resolved");
        var guid = Guid.NewGuid();
        var id = HellgateData.InstanceId(guid);
        if (starts is null)
        {
            var fallback = template.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ExitPortal)?.Position ?? Transfers.DefaultSpawn(template);
            Log.Warn("world", $"hellgate {map}: no walk grid for the start points, the party starts at the exit portal's marker");
            starts = (new Arrival(map, fallback, 0), fallback);
        }
        var n = Math.Max(1, players);
        var scale = Options.GroupScaling && mode.TeamSize > 0
            ? Math.Min(Options.ScalingMax, (Options.ScalingBase + Options.ScalingPerPlayer * n) / (Options.ScalingBase + Options.ScalingPerPlayer * mode.TeamSize)) : 1;
        var i = new HellgateInstance
        {
            Guid = guid, Mode = mode, World = map, Versus = Options.VersusModes.Contains(mode.Name), Party = party, Players = n,
            HealthFactor = scale, DamageFactor = Options.ScaleDamage ? scale : 1, Start = starts.Value.A with { Cluster = id }, StartB = starts.Value.B, StartedAt = world.Now,
            Chain = chain, ChainFactor = mode.ChainFactor(chain),
        };
        _instances[id] = i;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{i}: for {n} player(s) of party {party} ({(i.Versus ? "versus: an enemy team" : "a demon guardian")}), mobs x{scale:0.###}, {mode.PointsRequirement} points")
            + (chain > 0 ? string.Create(CultureInfo.InvariantCulture, $", chests x{i.ChainFactor:0.####}") : ""));
        return i;
    }

    /// <summary>
    /// <paramref name="p"/> goes into <paramref name="i"/> at start A (phase B: <paramref name="teamB"/>, the second party, at start B facing the
    /// objective chest), a member from now on with <paramref name="back"/> saved with the character: the push {0 id, 1 map}, or with
    /// <paramref name="answer"/> none (the caller answers a ChangeCluster with the returned arrival, phase B's chain exit).
    /// </summary>
    public Arrival Send(Player p, HellgateInstance i, DungeonReturn back, bool teamB = false, bool answer = false)
    {
        i.Members[p.Character.Id] = back;
        if (teamB) i.TeamB.Add(p.Character.Id);
        i.Arriving[p.Character.Id] = world.Now + TimeSpan.FromSeconds(Options.ArrivalGraceSeconds);
        i.EmptySince = null;
        p.Character.DungeonReturn = back;
        var to = teamB ? new Arrival(i.Id, i.StartB, MotionMaster.Facing(i.StartB, ChestOf(i))) : i.Start;
        if (!answer) p.Session.Teleport(to, $"hellgate:{i.Id}", i.World);
        return to;
    }

    /// <summary>The objective chest's marker of <paramref name="i"/>'s map (its start A without the map).</summary>
    private Vector2 ChestOf(HellgateInstance i) =>
        world.CachedCluster(i.World)?.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ObjectiveChest)?.Position ?? i.Start.Position;

    /// <summary>
    /// The lock of <paramref name="e"/> is over (SPEC §5.3): the instance (a new one, or the party's open one for late members), 401 {2 true} to
    /// the party, then each locked member still at the shrine goes in, its way back beside the shrine on its side (<see cref="WayBack"/>).
    /// </summary>
    private void Enter(Entry e, (Arrival A, Vector2 B)? starts)
    {
        _entries.Remove(e.Party);
        var going = e.Locked.Where(p => p.Map == e.Map && p.Session.Player == p && p.Session.Status == SessionStatus.InWorld).ToList();
        if (going.Count == 0)
        {
            // Phase B: a match made at this lock's end whose second party never went in is none (the first party's bots come).
            if (e.Into is { } matched && matched.PartyB == e.Party && !matched.Members.Keys.Any(matched.TeamB.Contains)) matched.PartyB = 0;
            return;
        }
        var i = e.Into is { Closed: false } into ? into : Create(e.Mode, e.World, e.Party, going.Count, starts);
        var teamB = e.Party != 0 && i.PartyB == e.Party; // phase B: the second party of a match comes in at start B
        UpdateShrine(e, success: true);
        var grid = world.Bots.Nav.TryGet(e.Map, out var nav) ? nav.Grid : null;
        foreach (var p in going)
        {
            var back = WayBack(e.Shrine.Position, p.PositionAt(DateTime.UtcNow), grid);
            Send(p, i, new DungeonReturn(e.Map.Cluster, back.X, back.Y, MotionMaster.Facing(e.Shrine.Position, back)), teamB);
        }
        Log.Info("world", $"{i}: {string.Join(", ", going.Select(p => $"'{p.Character.Name}'"))} go in{(teamB ? " as the second party" : "")}");
    }

    /// <summary>
    /// The way back of a member that stood at <paramref name="at"/> by the shrine at <paramref name="shrine"/>: ReturnDistance from its centre on
    /// that side (east when on it), outside the shrine's trigger; on the den's walk grid when built (<paramref name="grid"/>) the first walkable
    /// of twelve directions turning away from that side, else that point.
    /// </summary>
    internal static Vector2 WayBack(Vector2 shrine, Vector2 at, WalkGrid? grid)
    {
        var side = at - shrine;
        var angle = side.LengthSquared() < 0.01f ? 0 : Math.Atan2(side.Y, side.X);
        Vector2 Point(int k)
        {
            var a = angle + (k % 2 == 1 ? 1 : -1) * ((k + 1) / 2) * Math.PI / 6; // 0, +30°, -30°, +60°, ...
            return shrine + new Vector2((float)(Math.Cos(a) * ReturnDistance), (float)(Math.Sin(a) * ReturnDistance));
        }
        if (grid is not null)
            for (var k = 0; k < 12; k++)
                if (grid.Walkable(Point(k))) return Point(k);
        return Point(0);
    }

    /// <summary>
    /// After JoinFinished (WorldSession): in an instance, HellgateStatus (454 {1 mode, 2 state, 3 points}) then HellgateInfamy (453 {1 points}):
    /// 454 first, as 453 posts the tracker with the stored mode (SPEC §5.4). The client resets its hellgate state on every Join itself. Phase B:
    /// the fallen party of a match gets state 6; then <see cref="JoinedB"/> (the chain exit, the area, the spawn buffs). Anywhere else the
    /// spawn buffs without persistsafterclusterchange end (<see cref="LeftBuffs"/>).
    /// </summary>
    public void Joined(Player p)
    {
        if (Find(p.Map?.Cluster) is not { } i)
        {
            LeftBuffs(p);
            return;
        }
        var arrived = i.Arriving.Remove(p.Character.Id);
        i.EmptySince = null;
        if (!i.Members.ContainsKey(p.Character.Id)) // a GM's .tele
            i.Members[p.Character.Id] = p.Character.DungeonReturn ?? new DungeonReturn(Options.FallbackCluster, 0, 0, 0);
        var state = i.LostBy(p.Character.Id) ? HellgateState.Lost : i.State; // phase B: the fallen party of a match sees its loss
        p.Session.Send(new HellgateStatusEvent { ddn = p.Id, ciu = i.Mode.Id, civ = (int)state, ciw = i.Points });
        p.Session.Send(new HellgateInfamyEvent { ddn = p.Id, cil = i.Points });
        JoinedB(p, i, arrived);
    }

    /// <summary>The state to everyone inside (455), but to the fallen party of a match (phase B), which keeps its state 6.</summary>
    private void Status(HellgateInstance i, HellgateState state)
    {
        i.State = state;
        foreach (var p in Inside(i).Where(p => !i.LostBy(p.Character.Id))) p.Session.Send(new HellgateStatusUpdateEvent { ddn = p.Id, cix = (int)state });
        Log.Info("world", $"{i}: state {(int)state} {state}");
    }

    private void TellAll(HellgateInstance i, string key, params (string Name, object Value)[] values)
    {
        foreach (var p in Inside(i)) Expeditions.Tell(p, Settings.Text(key, values));
    }

    // ---- the objectives (SPEC §5.5) ----

    /// <summary>
    /// A mob of an instance died (Combat.Kill): the guardian's death wins objective 2 (and opens the BOSS chest), the last miniboss's opens the
    /// CHEST; in state 1 a demon with fame adds max(1, round(fame / Hellgates:FamePerPoint)) points (453 to everyone inside, a chat line every
    /// 25 %), and at the mode's pointsrequirement objective 2 begins.
    /// </summary>
    public void Killed(Map map, Mob mob)
    {
        if (Find(map.Cluster) is not { } i) return;
        if (i.Minibosses.Remove(mob.Id) && i.Minibosses.Count == 0) UnlockRewards(i, map, boss: false, "the minibosses are slain");
        if (mob.Id == i.GuardianId && i.GuardianId != 0)
        {
            UnlockRewards(i, map, boss: true, "the guardian is slain");
            if (i.State == HellgateState.Defeat) Defeated(i, map, $"the guardian {mob.Template.Name} is slain");
            return;
        }
        if (i.State != HellgateState.Progress || mob.Template.Fame <= 0) return;
        var required = i.Mode.PointsRequirement;
        i.Points += Math.Max(1, (int)Math.Round(mob.Template.Fame / Options.FamePerPoint, MidpointRounding.AwayFromZero));
        foreach (var p in Inside(i)) p.Session.Send(new HellgateInfamyEvent { ddn = p.Id, cil = i.Points });
        var quarter = required > 0 ? Math.Min(4, i.Points * 4 / required) : 4;
        if (quarter > i.Told)
        {
            i.Told = quarter;
            TellAll(i, "hg.progress", ("points", Math.Min(i.Points, required)), ("required", required));
        }
        if (i.Points >= required) StartDefeat(i, map);
    }

    /// <summary>
    /// Objective 2 (state 2, 455): HELLGATE_MATCH_START_SPELL on the party (Hellgates:MatchStartSpell); a versus run's enemy team at start B and
    /// its remaining demons gone mobdespawndelay later (Hellgates:MobDespawnOnMatch); else (or without bots) the guardian.
    /// </summary>
    public void StartDefeat(HellgateInstance i, Map map)
    {
        if (i.State != HellgateState.Progress) return;
        Status(i, HellgateState.Defeat);
        if (Options.MatchStartSpell && Data is { } data)
            foreach (var p in Inside(i)) Pvp.ApplySpell(p, data.MatchStartSpell, world.GameNow);
        // Phase B: with a second party inside the fight is between the two parties (no bots); else the enemy team of bots.
        if (i.Versus && (StartMatch(i) || SpawnEnemies(i, map) > 0))
        {
            i.FoughtTeam = true;
            if (Options.MobDespawnOnMatch) i.DespawnAt = world.Now + TimeSpan.FromSeconds(Data?.Global("mobdespawndelay", 2.4) ?? 2.4);
            if (Options.RestrictedArea) i.AreaDue = world.Now + TimeSpan.FromSeconds(i.Mode.AreaDelay); // phase B: the restricted area later
            if (!i.Match) TellAll(i, "hg.enemies", ("n", i.Enemies.Count));
            return;
        }
        SpawnGuardian(i, map);
    }

    /// <summary>
    /// A versus run's enemy team (SPEC §5.2): Hellgates:EnemyTeamSize bots (BotRole.Hellgate, one group, gear of the map's tier, × the instance's
    /// scaling) at start B; how many came (0 without Bots:Enabled, Bots:Hellgate:Enabled or the map's walk grid: the guardian instead).
    /// </summary>
    private int SpawnEnemies(HellgateInstance i, Map map)
    {
        if (!world.Bots.HellgateBots || world.Bots.NavOf(map) is not { Usable: true } nav || map.Template is not { } t) return 0;
        var chest = t.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ObjectiveChest)?.Position ?? i.Start.Position;
        var n = Options.EnemyTeamSize == HellgateEnemyTeam.Party ? i.Players : i.Mode.TeamSize;
        var group = new BotGroup(new PatrolRoute([i.StartB, chest], Loop: false));
        Vector2[] offsets = [new(0, 0), new(2, 0), new(-2, 0), new(0, 2), new(0, -2), new(2, 2), new(-2, -2), new(2, -2), new(-2, 2), new(4, 0)];
        for (var k = 0; k < n; k++)
        {
            var spot = i.StartB + offsets[k % offsets.Length];
            var at = nav.Grid.Walkable(spot) ? spot : nav.Grid.Nearest(spot, 4) ?? i.StartB;
            if (world.Bots.Spawn(map, BotRole.Hellgate, t.Name.Tier ?? 5, at, MotionMaster.Facing(at, chest), group: group,
                    healthFactor: i.HealthFactor, damageFactor: i.DamageFactor) is { } bot)
                i.Enemies.Add(bot);
        }
        if (i.Enemies.Count == 0) Log.Warn("world", $"{i}: no enemy bot could be made, the guardian instead");
        else Log.Info("world", $"{i}: {i.Enemies.Count} enemy bot(s) T{t.Name.Tier} at ({i.StartB.X:0}, {i.StartB.Y:0})");
        return i.Enemies.Count;
    }

    /// <summary>
    /// The guardian (SPEC §5.5): the held-back BOSS point's mob (maps 06, 10), else HG_DEMON_{SOLO|VETERAN}_BRUTE_BOSS of the map's tier at the
    /// objective chest's marker, × the instance's scaling; the chat line. Without a mob for it objective 2 is won at once.
    /// </summary>
    private void SpawnGuardian(HellgateInstance i, Map map)
    {
        var tier = map.Template?.Name.Tier ?? 5;
        var at = map.Template?.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ObjectiveChest)?.Position ?? i.Start.Position;
        var point = i.GuardianPoint;
        var veteran = i.Mode.TeamSize >= 5 ? "_VETERAN" : "";
        var mobs = world.Clusters?.Mobs;
        var template = point?.Block?.Pick(map.Random)
            ?? mobs?.Find(string.Create(CultureInfo.InvariantCulture, $"T{tier}_MOB_HG_DEMON_BRUTE{veteran}_BOSS")) ?? mobs?.Find($"T5_MOB_HG_DEMON_BRUTE{veteran}_BOSS");
        if (template is null)
        {
            Defeated(i, map, "no guardian mob is known");
            return;
        }
        var guardian = SpawnManager.SpawnAt(map, template, point?.Position ?? at, point?.Direction ?? 0, world, point?.Behavior ?? "GUARD", point?.Waypoints,
            map.MobHealthFactor, map.MobDamageFactor);
        i.GuardianId = guardian.Id;
        TellAll(i, "hg.guardian", ("name", template.Name));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{i}: {i.Points} points, the guardian {template.Name} at ({guardian.Position.X:0}, {guardian.Position.Y:0})"));
    }

    /// <summary>
    /// A bot of an enemy team is beaten (BotCombat.Defeated, after its rewards): with the last of them objective 2 is won.
    /// </summary>
    public void BotDefeated(Player bot)
    {
        if (Find(bot.Map?.Cluster) is not { State: HellgateState.Defeat } i || !i.Enemies.Contains(bot)) return;
        i.Beaten.Add(bot);
        TeamOver(i, bot.Map!);
    }

    /// <summary>
    /// A versus run's objective 2 once no bot of its team stands on <paramref name="map"/> (BotDefeated; every tick, as bots can leave without a
    /// defeat: .bot off): each one beaten wins it (the REWARD layer's BOSS chest opens too: the map's held-back boss never comes in a versus run);
    /// one gone unbeaten brings the guardian instead (the pve chest then), so a run never stays without an enemy.
    /// </summary>
    private void TeamOver(HellgateInstance i, Map map)
    {
        if (i.State != HellgateState.Defeat || !i.FoughtTeam || i.Match || i.Enemies.Any(b => b.Map == map && !i.Beaten.Contains(b))) return; // a match: MatchOver
        if (i.Enemies.All(i.Beaten.Contains))
        {
            UnlockRewards(i, map, boss: true, "the enemy team is beaten");
            Defeated(i, map, "the enemy team is beaten");
            return;
        }
        i.FoughtTeam = false;
        Log.Warn("world", $"{i}: the enemy team left unbeaten, the guardian instead");
        SpawnGuardian(i, map);
    }

    /// <summary>
    /// Objective 2 is won (state 3, 455): the objective chest at the HellgateObjectiveChest marker from the mode's pvp (an enemy team beaten) or
    /// pve chest spawnpoint, unlocked; the chat line; an unopened chest counts as claimed Hellgates:ClaimSeconds later.
    /// </summary>
    private void Defeated(HellgateInstance i, Map map, string why)
    {
        if (i.State is not (HellgateState.Progress or HellgateState.Defeat)) return;
        Status(i, HellgateState.Defeated);
        i.DespawnAt = null;
        i.ClaimDue = world.Now + TimeSpan.FromSeconds(Options.ClaimSeconds);
        var spawnpoint = i.FoughtTeam ? i.Mode.PvpChest : i.Mode.PveChest;
        if (map.Template?.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ObjectiveChest) is { } marker
            && world.Clusters?.LootChestsIfLoaded is { } data && data.Spawnpoints.TryGetValue(spawnpoint, out var behaviour))
        {
            var point = new LootChestSpawn
            {
                Key = new SpawnKey(map.Cluster, "hellgate-chest", map.CustomSpawns++), Tile = marker.Tile, Position = marker.Position, Direction = marker.Direction,
                Component = new XElement("lootchestspawnpoint", new XAttribute("spawnbehaviour", spawnpoint)), SpawnBehaviour = spawnpoint, Behaviour = behaviour,
            };
            if (world.Dungeons.MakeChest(map, point, data, state: 6, lootFactor: i.ChainFactor) is { } chest)
            {
                i.ObjectiveChest = chest;
                map.AddObject(chest);
            }
        }
        if (i.ObjectiveChest is null) Log.Warn("world", $"{i}: no objective chest ({spawnpoint})");
        foreach (var p in Inside(i).Where(p => !i.LostBy(p.Character.Id))) Expeditions.Tell(p, Settings.Text("hg.won"));
        Won(i); // phase B: the area ends, the winners' infamy
        Log.Info("world", $"{i} won: {why}");
    }

    /// <summary>A loot chest was opened (RandomDungeons.OpenChest): an instance's objective chest claims the reward.</summary>
    public void Opened(LootChest chest)
    {
        if (Find(chest.Map?.Cluster) is { State: HellgateState.Defeated } i && i.ObjectiveChest == chest) Claimed(i, chest.Map!);
    }

    /// <summary>The reward is claimed (state 4, 455): the exit portal at EXIT_PORTAL (250), the closing after Hellgates:FinishedLingerSeconds.</summary>
    private void Claimed(HellgateInstance i, Map map)
    {
        Status(i, HellgateState.Claimed);
        i.ClaimDue = null;
        Finish(i, map);
        if (Options.Chain) OpenChain(i, map); // phase B: the chain exit (402) beside the exit portal
    }

    /// <summary>The party fell (state 6, 455): the enemy team leaves, the exit portal (no objective chest), the closing after Hellgates:FinishedLingerSeconds.</summary>
    private void Lost(HellgateInstance i, Map map)
    {
        Status(i, HellgateState.Lost);
        i.DespawnAt = null;
        EndArea(i); // phase B
        DespawnEnemies(i, "the party fell");
        TellAll(i, "hg.lost");
        Finish(i, map);
    }

    private void Finish(HellgateInstance i, Map map)
    {
        i.FinishedAt = world.Now;
        if (i.Portal is null && map.Template?.HellgateMarkers.FirstOrDefault(m => m.Name == HellgateData.ExitPortal) is { } marker)
            map.AddObject(i.Portal = new HellgateExitPortal(world.ObjectIds.Next(), marker));
        TellAll(i, "hg.closing", ("seconds", Math.Round(Options.FinishedLingerSeconds)));
    }

    /// <summary>.hg finish: objective 2 begins now (state 1), or is won now (state 2: the guardian or the team gone; a match: by the first party), or the reward claimed (state 3).</summary>
    public void Advance(HellgateInstance i, Map map)
    {
        if (i.State == HellgateState.Progress)
        {
            i.Points = Math.Max(i.Points, i.Mode.PointsRequirement);
            foreach (var p in Inside(i)) p.Session.Send(new HellgateInfamyEvent { ddn = p.Id, cil = i.Points });
            StartDefeat(i, map);
        }
        else if (i.State == HellgateState.Defeat)
        {
            if (map.Objects.GetValueOrDefault(i.GuardianId) is Mob { Dead: false } guardian) SpawnManager.Despawn(guardian, world, respawn: false);
            UnlockRewards(i, map, boss: true, "a GM's .hg finish");
            DespawnEnemies(i, "a GM's .hg finish");
            if (i.Match) TeamLost(i, map, 2, "a GM's .hg finish"); // phase B: the second party loses
            else Defeated(i, map, "a GM's .hg finish");
        }
        else if (i.State == HellgateState.Defeated) Claimed(i, map); // phase B: the reward claimed (the exit portal and the chain exit)
    }

    /// <summary>The REWARD layer's BOSS (<paramref name="boss"/>) or CHEST chests of <paramref name="i"/> unlock (6, UpdateLootChest to the observers).</summary>
    private void UnlockRewards(HellgateInstance i, Map map, bool boss, string why)
    {
        foreach (var chest in i.RewardChests.Where(c => c.LockState == RandomDungeons.BossLocked && RandomDungeons.IsBossChest(c.Spawn is LootChestSpawn s ? s.SpawnBehaviour : "") == boss))
        {
            chest.SetState(6, world.GameNow);
            foreach (var o in map.ObserversOf(chest)) o.Session.Send(chest.Update(o));
            Log.Info("world", $"{chest} unlocked: {why}");
        }
    }

    private void DespawnEnemies(HellgateInstance i, string why)
    {
        foreach (var bot in i.Enemies.Where(b => b.Map is not null).ToList()) world.Bots.Despawn(bot, why, BotRefill.Now);
    }

    // ---- the enemy team (SPEC §5.2) ----

    /// <summary>
    /// Whether <paramref name="a"/> and <paramref name="b"/> are a bot of an instance's enemy team and a member of its party on its map while
    /// objective 2 runs: Hostile both ways (BotPvp.Of); every other pair with such a bot is Neutral.
    /// </summary>
    public bool Opponents(Player a, Player b)
    {
        var (bot, other) = a.IsBot ? (a, b) : (b, a);
        return !other.IsBot && bot.Map is { } map && other.Map == map && Find(map.Cluster) is { State: HellgateState.Defeat } i
            && i.Enemies.Contains(bot) && i.Members.ContainsKey(other.Character.Id);
    }

    /// <summary>The member an enemy bot hunts (InvaderBrain): the nearest one inside that stands, while objective 2 runs; null otherwise.</summary>
    public Player? TargetOf(Player bot)
    {
        if (Find(bot.Map?.Cluster) is not { State: HellgateState.Defeat } i || !i.Enemies.Contains(bot)) return null;
        var at = bot.PositionAt(world.Bots.Time);
        return Inside(i).Where(p => p.Combat.Down is null && i.Members.ContainsKey(p.Character.Id)).MinBy(p => Vector2.DistanceSquared(p.PositionAt(DateTime.UtcNow), at));
    }

    // ---- leaving (SPEC §5.7) ----

    /// <summary>
    /// <paramref name="p"/> leaves <paramref name="i"/> (the escape or the exit portal, .hg leave, the closing): no member any more, to its way
    /// back (<paramref name="answer"/> false: the ChangeCluster push; true: the caller answers a ChangeCluster request with the returned arrival).
    /// </summary>
    public Arrival Leave(Player p, HellgateInstance i, string why, bool answer = false)
    {
        var back = i.Members.GetValueOrDefault(p.Character.Id) ?? p.Character.DungeonReturn ?? new DungeonReturn(Options.FallbackCluster, 0, 0, 0);
        i.Members.Remove(p.Character.Id);
        i.Arriving.Remove(p.Character.Id);
        p.Character.DungeonReturn = null;
        var to = new Arrival(back.Cluster, new Vector2(back.X, back.Y), back.Direction);
        Log.Info("world", $"'{p.Character.Name}' leaves {i} ({why}, state {(int)i.State})");
        if (!answer) p.Session.Teleport(to, $"hellgate:{i.Id}:{why}");
        return to;
    }

    /// <summary>A dead player respawns outside its hellgate (Death.Respawn and Death.Departed; the official preset's lethal modes): no member any more, the way back cleared.</summary>
    public void DiedOut(Player p)
    {
        if (Find(p.Map?.Cluster) is not { } i || !i.Members.Remove(p.Character.Id)) return;
        i.Arriving.Remove(p.Character.Id);
        p.Character.DungeonReturn = null;
        world.Saves.MarkChanged(p.Character);
        Log.Info("world", $"'{p.Character.Name}' died in {i} and respawns outside it");
    }

    /// <summary>
    /// The cluster and point of a character whose saved cluster is the id of an instance that is gone (closed, or a restart: instances are not
    /// kept): its saved way back, else Hellgates:FallbackCluster; the character is changed and saved. False when its cluster is no closed hellgate id.
    /// </summary>
    public bool ReturnIfClosed(Character c)
    {
        if (!HellgateData.IsInstance(c.Cluster) || IsLive(c.Cluster)) return false;
        var to = c.DungeonReturn ?? new DungeonReturn(Options.FallbackCluster, 0, 0, 0);
        (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (to.Cluster, to.X, to.Y, to.Direction);
        c.PendingTransfer = null;
        c.DungeonReturn = null;
        world.Saves.SaveNow(c);
        Log.Info("world", $"'{c.Name}' was in a closed hellgate: back in {to.Cluster}");
        return true;
    }

    /// <summary>
    /// The instance closes: 455 {7} and the chat line to everyone still inside, then out to its way back (a knocked-down one dies first in a
    /// lethal zone, Death.Abandoned; a dead one as a respawn), the enemy team gone, the map unloaded, the id never used again.
    /// </summary>
    public void Close(HellgateInstance i, string why)
    {
        if (i.Closed) return;
        DespawnEnemies(i, $"the hellgate closes ({why})");
        foreach (var p in Inside(i)) p.Session.Send(new HellgateStatusUpdateEvent { ddn = p.Id, cix = (int)HellgateState.Closed });
        i.Closed = true;
        if (world.Maps.Find(i.Id) is { } map)
        {
            foreach (var p in map.Players.Values.ToList())
            {
                if (p.Session.Status == SessionStatus.InWorld && p.Session.Player == p)
                {
                    Expeditions.Tell(p, Settings.Text("hg.closed"));
                    if (p.Combat.Down is { Kind: not KnockdownKind.Dead }) Death.Abandoned(p);
                    var dead = p.Combat.Down is { Kind: KnockdownKind.Dead };
                    var to = Leave(p, i, why, answer: true);
                    p.Session.Teleport(to, dead ? Death.RespawnVia : $"hellgate:{i.Id}:{why}");
                }
                else world.Maps.Remove(p, world.Clock.Now);
            }
            world.Maps.Unload(map, force: true);
        }
        _instances.Remove(i.Id);
        Log.Info("world", $"{i} closed ({why})");
    }

    // ---- the timers ----

    /// <summary>
    /// Every tick (<see cref="World.Update"/>): the channels (a player gone, knocked down, damaged or moved Hellgates:CancelDistance cancels its
    /// own; done after ChannelSeconds: 273 {2 true}; a done one that leaves the use distance is out too), a party's lock once every channel is
    /// done and no member idles at the shrine (or ChannelSeconds after hg.waiting; 274 to each and its observers, LockSeconds; then, once the map
    /// is read and its start points known — at most ResolveSeconds more —, the entry); then each instance: the demons' despawn of a versus run
    /// (the minibosses with them), its enemy team gone (TeamOver), the unclaimed chest, the wipe (Hellgates:WipeEndsRun), the time limit
    /// (Hellgates:MaxMinutes, a line a minute before), the closing after the claim or loss (FinishedLingerSeconds) or empty (EmptyCloseSeconds).
    /// </summary>
    public void Update()
    {
        var now = world.Now;
        var utc = DateTime.UtcNow;
        var use = UseDistance + world.Config.Transfer.PositionTolerance;
        foreach (var e in _entries.Values.ToList())
        {
            foreach (var (p, c) in e.Channels.ToList())
            {
                if (p.Map != e.Map || p.Session.Player != p) Cancel(e, p, "gone");
                else if (c.Done && Vector2.Distance(p.PositionAt(utc), e.Shrine.Position) > Math.Max(use, Vector2.Distance(c.From, e.Shrine.Position) + Options.CancelDistance))
                    Cancel(e, p, "left the shrine"); // a done one waits at the shrine
                else if (c.Done) continue;
                else if (p.Combat.Down is not null) Cancel(e, p, "knocked down");
                else if (p.Health.At(world.GameNow) < c.Health - 0.5f) Cancel(e, p, "damaged");
                else if (Vector2.Distance(p.PositionAt(utc), c.From) > Options.CancelDistance) Cancel(e, p, "moved");
                else if (now >= c.Due)
                {
                    c.Done = true;
                    p.Session.Send(new UsingHellgateShrineEvent { ddn = p.Id, d6a = true, d6b = true });
                }
            }
            if (!_entries.ContainsKey(e.Party)) continue;
            if (e.LockDue is null)
            {
                if (e.Channels.Count == 0 || !e.Channels.Values.All(c => c.Done))
                {
                    e.WaitingSince = null;
                    continue;
                }
                // A member at the shrine (the client's use distance) that does not channel holds the others ChannelSeconds at most, told who.
                var idle = AtShrine(e.Party, e.Channels.Keys.First(), e.Shrine, use).Where(m => !e.Channels.ContainsKey(m)).ToList();
                if (idle.Count > 0 && (e.WaitingSince ??= Waiting(e, idle, now)) + TimeSpan.FromSeconds(Options.ChannelSeconds) > now) continue;
                e.Locked.AddRange(e.Channels.Keys);
                e.Channels.Clear();
                e.LockDue = now + TimeSpan.FromSeconds(Options.LockSeconds);
                foreach (var p in e.Locked) p.SendAround(new EnteringHellgateLockStartEvent { ddn = p.Id });
                Log.Info("world", $"hellgate lock of party {e.Party} ({e.Locked.Count} at the shrine) for {e.Mode.Name} on {e.World}");
                continue;
            }
            foreach (var p in e.Locked.Where(p => p.Map != e.Map || p.Session.Player != p || p.Combat.Down is not null).ToList()) Cancel(e, p, "gone or knocked down during the lock");
            if (!_entries.ContainsKey(e.Party) || now < e.LockDue) continue;
            // Phase B (Hellgates:Matchmaking Players): a young hellgate of the same versus mode with one party takes this one as its second.
            if (e.Into is null && MatchFor(e) is { } match)
            {
                (match.PartyB, e.Into) = (e.Party, match);
                Log.Info("world", $"{match}: party {e.Party} is matched as the second party");
            }
            // A late members' instance closed during the lock: a new one, as a fresh entry (its start points).
            var joining = e.Into is { Closed: false };
            var starts = joining ? null : StartsFor(e.World);
            if (joining || starts is not null) Enter(e, starts);
            else if (now >= e.LockDue + TimeSpan.FromSeconds(ResolveSeconds))
            {
                if (world.CachedCluster(e.World) is not null) Enter(e, null); // no walk grid: the fallback start
                else
                {
                    foreach (var p in e.Locked.ToList())
                    {
                        Expeditions.Tell(p, Settings.Text("hg.refused", ("why", $"the map {e.World} could not be read; try again")));
                        Cancel(e, p, $"cluster {e.World} not resolved");
                    }
                }
            }
        }
        foreach (var i in _instances.Values.ToList())
        {
            var map = world.Maps.Find(i.Id);
            var limit = TimeSpan.FromMinutes(Options.MaxMinutes);
            if (now - i.StartedAt >= limit)
            {
                Close(i, "time limit");
                continue;
            }
            if (!i.Warned && now - i.StartedAt >= limit - TimeSpan.FromSeconds(60))
            {
                i.Warned = true;
                TellAll(i, "hg.closing", ("seconds", 60));
            }
            if (map is not null)
            {
                if (i.DespawnAt is { } despawn && now >= despawn)
                {
                    i.DespawnAt = null;
                    foreach (var mob in map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && !m.Removed && m.Id != i.GuardianId).ToList()) SpawnManager.Despawn(mob, world, respawn: false);
                    // The minibosses leave with the demons (no Killed): the REWARD layer's CHEST they guarded opens.
                    if (i.Minibosses.Count > 0)
                    {
                        i.Minibosses.Clear();
                        UnlockRewards(i, map, boss: false, "the minibosses left with the demons");
                    }
                }
                TeamOver(i, map);
                if (i.ClaimDue is { } claim && now >= claim && i.State == HellgateState.Defeated) Claimed(i, map);
                var inside = map.Players.Values.Where(p => i.Members.ContainsKey(p.Character.Id)).ToList();
                if (i.Match && i.State == HellgateState.Defeat) MatchOver(i, map, inside); // phase B: one party fell or left
                else if (Options.WipeEndsRun && i.State is HellgateState.Progress or HellgateState.Defeat && inside.Count > 0 && inside.All(p => p.Combat.Down is not null)) Lost(i, map);
                UpdateArea(i, inside); // phase B: the restricted area
            }
            foreach (var late in i.Arriving.Where(a => now >= a.Value).Select(a => a.Key).ToList()) i.Arriving.Remove(late);
            if (Inside(i).Any() || i.Arriving.Count > 0) i.EmptySince = null;
            else i.EmptySince ??= now;
            if (i.EmptySince is { } since && now - since >= TimeSpan.FromSeconds(Options.EmptyCloseSeconds)) Close(i, "empty");
            else if (i.FinishedAt is { } done && now - done >= TimeSpan.FromSeconds(Options.FinishedLingerSeconds)) Close(i, "finished");
        }
    }

    /// <summary>.hg list: the channels under way and the live instances, one line each.</summary>
    public IEnumerable<string> Describe()
    {
        foreach (var e in _entries.Values)
            yield return $"  party {e.Party} at {e.Map.Cluster}: {e.Mode.Name} on {e.World}, {e.Channels.Count} channeling, {e.Locked.Count} locked";
        foreach (var i in _instances.Values)
        {
            // Phase B: the second party, the chain, the restricted area.
            var more = string.Create(CultureInfo.InvariantCulture, $"{(i.PartyB != 0 && !i.Match ? $", party {i.PartyB} matched" : "")}{(i.Chain > 0 ? $", chain {i.Chain} (x{i.ChainFactor:0.####})" : "")}")
                + (i.Area is { } area ? string.Create(CultureInfo.InvariantCulture, $", area r {area.RadiusAt(world.Now):0}{(area.Final ? " (final)" : "")}")
                    : i.AreaDue is { } due ? string.Create(CultureInfo.InvariantCulture, $", area in {(due - world.Now).TotalSeconds:0} s") : "");
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {i.Id} {i.Mode.Name} {i.World}: {i.Members.Count} member(s) (made for {i.Players}), state {(int)i.State} {i.State}, {i.Points}/{i.Mode.PointsRequirement} points, "
                + $"{(i.Match ? $"party {i.Party} vs party {i.PartyB}" : i.Versus ? $"{i.Enemies.Count(b => b.Bot is { Fight.Defeated: false })} enemy bot(s) up" : "guardian")}, {(world.Now - i.StartedAt).TotalMinutes:0} min, {Inside(i).Count()} inside{more}");
        }
    }
}
