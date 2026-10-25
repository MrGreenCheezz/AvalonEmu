using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// One invasion of a corrupted dungeon run (stageL corrupted SPEC §4.4, phase B): the run invaded, its invader (a player bot of role
/// Invader, or a player out of its own run <see cref="From"/> with its way back <see cref="Home"/>), the countdown before a player invader is
/// moved, the Demonic Shards (CD_STATUE mobs) and how many the owner destroyed, the anti-reset imps, and the end: who won and when the
/// invader goes (a bot 5-10 s later, a player after expeltimeafterinvasionend).
/// </summary>
public sealed class CorruptedInvasion
{
    public required CorruptedRun Run { get; init; }

    /// <summary>The bot invader; null for a player invader.</summary>
    public Player? Bot { get; set; }

    /// <summary>A player invader's character, its own run and the place in it it goes back to; null for a bot.</summary>
    public Guid? Invader { get; init; }
    public CorruptedRun? From { get; init; }
    public Arrival? Home { get; set; }

    public required string InvaderName { get; init; }

    /// <summary>The <see cref="World.Clock"/> time a matched player invader is moved (the end of 449 kind 6's countdown), and until when it must have joined.</summary>
    public TimeSpan? MoveAt { get; set; }
    public TimeSpan? ArriveBy { get; set; }

    /// <summary>The invader is in the instance and the fight is on (from its spawn or Join); its start.</summary>
    public bool Live { get; set; }
    public TimeSpan StartedAt { get; set; }

    /// <summary>The fight is over (449 kind 10): <see cref="OwnerWon"/>, and when the invader goes (<see cref="GoneAt"/>).</summary>
    public bool Ended { get; set; }
    public bool OwnerWon { get; set; }
    public TimeSpan? GoneAt { get; set; }

    /// <summary>A player invader has been sent home already (its exit, escape or death): the end does not move it again.</summary>
    public bool HomeSent { get; set; }

    /// <summary>The shards still standing (mob ids), how many are destroyed (449 kind 5, 450 key 4) and how many of them by the owner itself.</summary>
    public HashSet<long> Shards { get; } = [];
    public int Destroyed { get; set; }
    public int DestroyedByOwner { get; set; }

    /// <summary>The next wave of anti-reset imps (Corrupted:AntiReset), and the imps standing with their despawn time.</summary>
    public TimeSpan NextImps { get; set; }
    public List<(Mob Mob, TimeSpan Until)> Imps { get; } = [];

    /// <summary>Since when one side is off the map during the fight (it ends 10 s later).</summary>
    public TimeSpan? MissingSince { get; set; }

    /// <summary>The character id of the invader (the bot's transient one or the player's).</summary>
    public Guid InvaderId => Bot?.Character.Id ?? Invader ?? Guid.Empty;
}

/// <summary>
/// Phase B of the corrupted dungeons (SPEC §4.4-§4.8, §6): the Demonic Shrine (UseCorruptedShrine 416 with its use time, 460/461, 459), the
/// invasions by consent (pve: only an owner who used its shrine is ever a target; a bot invader BotInvaderDelaySeconds later, a consenting
/// player of the same rank by the rolls; official: the data's rolls with chanceperrollpve / chanceperrollpvp, real players), their run
/// (449 kinds 3/5/6/7/9/10/12, 450 keys 4/6/7, the match spells, the shards, the anti-reset imps), their end (InvaderReward to the winner,
/// Corrupted:InfamyLossOnDefeat of the loser, under Pvp:FullLoot the boss loot share of a winning invader), the chain exit, and the journal's
/// special conditions. The fighting pair is Hostile to each other only (<see cref="Opponents"/>, Pvp.Of and BotPvp.Of), as lethal as the zone
/// and Pvp:LethalDeath make it. Under <see cref="World.Sync"/>.
/// </summary>
public sealed partial class CorruptedDungeons
{
    /// <summary>How near the owner must stand to the shrine (m) [design: the client checks its own use range first].</summary>
    public const float ShrineRange = 5;

    /// <summary>Seconds one side of a fight may be off the map before the fight ends (a relog, a lost connection).</summary>
    public const double MissingSeconds = 10;

    /// <summary>The shrine uses under way: the run, the shrine and when the use takes effect.</summary>
    private readonly Dictionary<Player, (CorruptedRun Run, CorruptedShrine Shrine, TimeSpan Due)> _shrineUses = [];

    /// <summary>Player invaders whose invasion ended while they were offline: where they go at their next Join into that run.</summary>
    private readonly Dictionary<Guid, (string Cluster, Arrival To, string? World, string? Type)> _homes = [];

    /// <summary>The 450 key 7 "searching" state: the owner consented, no invasion is on, more may come.</summary>
    private bool Searching(CorruptedRun run) =>
        run.Consented && run.Invasion is null && !run.Completed && run.Invasions < Options.MaxInvasionsPerRun && Options.Invasions != CorruptedInvasionMode.Off;

    // ---- the shrine (SPEC §2.6, §4.4) ----

    /// <summary>
    /// UseCorruptedShrine (op 416 {0 time, 1 shrine id}; the client sends it only in state 0 and waits for the answer): the owner of the run, at
    /// its shrine, while it is usable (the window: shrinetimelimit 45 s from the owner's first Join), standing; then
    /// CorruptedShrineUsageStart (460) and after Corrupted:ShrineUseSeconds the consent (<see cref="Consent"/>); damage meanwhile cancels it
    /// (461). Returns why it was refused, or null.
    /// </summary>
    public string? UseShrine(Player p, long shrineId)
    {
        if (!Options.Enabled || Options.Invasions == CorruptedInvasionMode.Off) return "invasions are off here";
        if (Find(p.Map?.Cluster) is not { } run || run.Shrine is not { } shrine || shrine.Id != shrineId) return "no Demonic Shrine of a corrupted dungeon";
        if (run.Owner != p.Character.Id) return "only the dungeon's owner may use its shrine";
        if (shrine.State != CorruptedShrine.Usable || run.Consented) return "the shrine is inactive";
        if (ShrineClosed(run)) return "the shrine's time is over";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (_shrineUses.ContainsKey(p)) return "the shrine is in use";
        if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), shrine.Position) > ShrineRange + SpellCasts.RangeTolerance) return "too far from the shrine";
        _shrineUses[p] = (run, shrine, world.Now + TimeSpan.FromSeconds(Options.ShrineUseSeconds));
        // 460 is the user's (key 0 the character [code]: crs.aft 0x180BE9620, the observers' character component, sets action state 26
        // CorruptedShrineUsage on it; the user's own client starts its use itself), as CastStart.
        p.SendAround(new CorruptedShrineUsageStartEvent { ddn = p.Id });
        Log.Info("world", $"'{p.Character.Name}' uses the Demonic Shrine of {run}");
        if (Options.ShrineUseSeconds <= 0) ShrineStep();
        return null;
    }

    /// <summary>Whether the shrine's window is over (shrinetimelimit from the owner's first Join; never before it).</summary>
    private bool ShrineClosed(CorruptedRun run) =>
        run.EnteredAt is { } entered && world.Now - entered > TimeSpan.FromSeconds(Data?.Global("shrinetimelimit", 45) ?? 45);

    /// <summary>
    /// A shrine use under way ends without its effect (damage, a knockdown, another action): CorruptedShrineUsageCancel (461 {0 the user,
    /// 1 true}) to the user and its observers [code: czz.aft 0x180A29EC0 ends the user's local action (key 1 true: ct7.a.c), crs.aft its
    /// action state for the observers].
    /// </summary>
    private void CancelShrine(Player p, string why)
    {
        if (!_shrineUses.Remove(p, out _)) return;
        if (p.Map is not null) p.SendAround(new CorruptedShrineUsageCancelEvent { ddn = p.Id, b1l = true });
        Log.Info("world", $"'{p.Character.Name}''s use of the Demonic Shrine is cancelled: {why}");
    }

    /// <summary>The shrine uses whose time is up take effect; one whose player left the run is dropped.</summary>
    private void ShrineStep()
    {
        foreach (var (p, use) in _shrineUses.ToList())
        {
            if (p.Map?.Cluster != use.Run.Id || p.Session.Player != p || use.Run.Closed) _shrineUses.Remove(p);
            else if (p.Combat.Down is not null) CancelShrine(p, "knocked down");
            else if (world.Now >= use.Due)
            {
                _shrineUses.Remove(p);
                Consent(use.Run, p);
            }
        }
    }

    /// <summary>
    /// The owner consents (SPEC §4.4): the shrine inactive (459 {0 shrine, 1 state 1}), 450 with key 7 (searching) and 449 kind 12, the chat
    /// line; the bot invader's time (content/corrupted.json Rolls Fixed: Corrupted:BotInvaderDelaySeconds) and the rolls go from now. Also
    /// .cd shrine.
    /// </summary>
    public void Consent(CorruptedRun run, Player owner)
    {
        if (run.Consented) return;
        run.Consented = true;
        run.ConsentedAt = world.Now;
        if (run.Shrine is { } shrine) SetShrine(run, shrine, CorruptedShrine.Inactive);
        var interval = TimeSpan.FromSeconds(Data?.Global("rollinterval", 30) ?? 30);
        if (run.NextRoll < world.Now + interval) run.NextRoll = world.Now + interval;
        if (Options.BotInvaders && world.Bots.InvaderBots && Settings.Rolls == CorruptedRolls.Fixed)
        {
            var (min, max) = Options.BotInvaderDelaySeconds;
            run.BotDue = world.Now + TimeSpan.FromSeconds(min + Rng.NextDouble() * (max - min));
        }
        owner.Session.Send(Status(owner, run));
        Update(run, 12, 1, Vector2.Zero);
        Expeditions.Tell(owner, Settings.Text("cd.shrine"));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{run}: '{owner.Character.Name}' consents to invasions{(run.BotDue is { } due ? $", a bot invader in {(due - world.Now).TotalSeconds:0} s" : "")}"));
    }

    /// <summary>The shrine's state <paramref name="state"/> to everyone inside (UpdateCorruptedShrine 459, a0x.ajv 0x180B2DEA0).</summary>
    private void SetShrine(CorruptedRun run, CorruptedShrine shrine, byte state)
    {
        if (shrine.State == state) return;
        shrine.State = state;
        shrine.StateTicks = world.GameNow;
        var e = new UpdateCorruptedShrineEvent { ddn = shrine.Id, d2t = state, d2u = shrine.StateTicks, Extra = new() { [1] = state } }; // state 0 goes out too
        foreach (var p in Inside(run)) p.Session.Send(e);
    }

    // ---- the rolls and the start (SPEC §4.4) ----

    /// <summary>
    /// The phase B part of a run's tick (<see cref="Update"/>): the shrine uses and its window, then the invasion under way, or whether one
    /// starts now: Consent = only after the owner's shrine (the bot invader when its time is due, a consenting player of the same rank every
    /// rollinterval, under Rolls Official the bot by chanceperrollpvp too); Official = every rollinterval after safeperiod a roll with
    /// chanceperrollpve (no shrine) or chanceperrollpvp (shrine): a player invader when one is waiting, else the bot (Corrupted:BotInvaders).
    /// </summary>
    private void UpdateInvasions(CorruptedRun run, TimeSpan now)
    {
        if (_shrineUses.Count > 0) ShrineStep();
        if (run.Shrine is { State: CorruptedShrine.Usable } shrine && ShrineClosed(run) && !_shrineUses.Values.Any(u => u.Run == run))
            SetShrine(run, shrine, CorruptedShrine.Inactive);
        if (run.Invasion is { } invasion)
        {
            Progress(run, invasion, now);
            return;
        }
        if (Options.Invasions == CorruptedInvasionMode.Off || run.Completed || run.Away || run.EnteredAt is null || run.Invasions >= Options.MaxInvasionsPerRun) return;
        if (_runs.Values.Any(o => o.Invasion is { Ended: false } i && i.From == run)) return; // its owner is about to invade another run
        if (OwnerIn(run) is not { Combat.Down: null }) return;
        var data = Data;
        var d = run.Difficulty;
        if (Options.Invasions == CorruptedInvasionMode.Consent)
        {
            if (!run.Consented) return;
            if (Options.BotInvaders && run.BotDue is { } due && now >= due)
            {
                // Bots off since (Bots:Enabled, Bots:Invader:Enabled): no bot any more; one that cannot be made now (the walk grid) is tried again. Either way
                // the player matching below goes on.
                if (!world.Bots.InvaderBots) run.BotDue = null;
                else if (StartBot(run) is not null) return;
            }
        }
        if (now < run.NextRoll) return;
        run.NextRoll = now + TimeSpan.FromSeconds(data?.Global("rollinterval", 30) ?? 30);
        var chance = Options.Invasions == CorruptedInvasionMode.Official ? run.Consented ? d.ChancePvp : d.ChancePve : 1;
        if (Options.Invasions == CorruptedInvasionMode.Official && Rng.NextDouble() >= chance) return;
        if (Options.PlayerInvasions != CorruptedInvasionMode.Off && Waiting(run) is { } from)
        {
            StartPlayer(run, from);
            return;
        }
        var botRoll = Options.Invasions == CorruptedInvasionMode.Official || (Settings.Rolls == CorruptedRolls.Official && Rng.NextDouble() < d.ChancePvp);
        if (Options.BotInvaders && botRoll) StartBot(run);
    }

    /// <summary>The owner of <paramref name="run"/> on its map; null when it is not there.</summary>
    private Player? OwnerIn(CorruptedRun run) => Inside(run).FirstOrDefault(p => p.Character.Id == run.Owner);

    /// <summary>
    /// The run whose owner invades <paramref name="target"/>: another run of the same rank whose owner used its shrine (the consent of both
    /// sides; Corrupted:PlayerInvasions), standing in it, with no invasion of its own on; the one that consented first.
    /// </summary>
    private CorruptedRun? Waiting(CorruptedRun target) => _runs.Values
        .Where(r => r != target && !r.Closed && r.Owner != target.Owner && r.Consented && r.Invasion is null && !r.Away && !r.Completed
                    && r.Difficulty.Index == target.Difficulty.Index && OwnerIn(r) is { Combat.Down: null }
                    && !_runs.Values.Any(o => o.Invasion?.From == r))
        .OrderBy(r => r.ConsentedAt).FirstOrDefault();

    /// <summary>
    /// A bot invader for <paramref name="run"/> (SPEC §4.4, §4.5): at the InvaderSpawnpoint farthest from the owner (at least
    /// minimuminvaderspawndistance when one is), gear of the rank's tier, health × Corrupted:Invader:HealthFactor and damage × the rank's
    /// DamageFactor; nothing (a later tick) while the map's walk grid is being built, nothing at all without bots (Bots:Enabled or Bots:Invader:Enabled off).
    /// </summary>
    public CorruptedInvasion? StartBot(CorruptedRun run)
    {
        if (!world.Bots.InvaderBots || world.Maps.Find(run.Id) is not { Template: { } template } map || OwnerIn(run) is not { } owner) return null;
        if (world.Bots.NavOf(map) is not { Usable: true } nav) return null; // the walk grid is being built: the next tick
        var d = run.Difficulty;
        var at = InvaderSpawn(template, owner.PositionAt(DateTime.UtcNow));
        at = nav.Grid.Walkable(at) ? at : nav.Grid.Nearest(at, 8) ?? at;
        var factor = d.Index switch { 0 => Options.InvaderDamageFactor.Hunter, 1 => Options.InvaderDamageFactor.Stalker, _ => Options.InvaderDamageFactor.Slayer };
        var bot = world.Bots.Spawn(map, BotRole.Invader, d.Tier, at, Transfers.Facing(at, owner.PositionAt(DateTime.UtcNow)),
            healthFactor: Options.InvaderHealthFactor, damageFactor: factor);
        run.BotDue = null;
        if (bot is null)
        {
            Log.Warn("world", $"{run}: no bot invader could be made");
            return null;
        }
        var invasion = new CorruptedInvasion { Run = run, Bot = bot, InvaderName = bot.Character.Name };
        (run.Invasion, run.Invasions) = (invasion, run.Invasions + 1);
        Begin(run, invasion, map, at, bot);
        return invasion;
    }

    /// <summary>
    /// A player invader from <paramref name="from"/> for <paramref name="target"/> (SPEC §4.4): its countdown (449 kind 6 {2 the GameTimeStamp
    /// of its end} [inference: the tracker shows the time left to it], Corrupted:InvadeCountdownSeconds), then the move into the target's
    /// instance (<see cref="Progress"/>).
    /// </summary>
    public CorruptedInvasion StartPlayer(CorruptedRun target, CorruptedRun from)
    {
        var invader = OwnerIn(from)!;
        var invasion = new CorruptedInvasion
        {
            Run = target, Invader = from.Owner, From = from, InvaderName = from.OwnerName, MoveAt = world.Now + TimeSpan.FromSeconds(Options.InvadeCountdownSeconds),
        };
        (target.Invasion, target.Invasions) = (invasion, target.Invasions + 1);
        invader.Session.Send(new CorruptedDungeonUpdateEvent { ddn = invader.Id, b0x = 6, b0y = world.GameNow + SpellCasts.Seconds(Options.InvadeCountdownSeconds) });
        Log.Info("world", $"{target}: '{from.OwnerName}' of {from} is matched to invade it in {Options.InvadeCountdownSeconds:0} s");
        return invasion;
    }

    /// <summary>The InvaderSpawnpoint of <paramref name="t"/> farthest from <paramref name="owner"/> (or the farthest counted mob point; the start without either).</summary>
    private Vector2 InvaderSpawn(ClusterTemplate t, Vector2 owner)
    {
        var points = t.InvaderSpawns.Count > 0 ? t.InvaderSpawns.Select(s => s.Position).ToList()
            : [.. t.MobSpawnPoints.Where(m => m.Block is not null && CorruptedData.Counts(m.SpawnBehavior)).Select(m => m.Position)];
        if (points.Count == 0) return StartOf(t).Position;
        var min = (float)(Data?.Global("minimuminvaderspawndistance", 120) ?? 120);
        var far = points.Where(p => Vector2.Distance(p, owner) >= min).ToList();
        return (far.Count > 0 ? far : points).MaxBy(p => Vector2.Distance(p, owner));
    }

    /// <summary>
    /// The fight begins (the bot's spawn, the player invader's Join): to the owner 449 kind 12 (searching over), kind 7 (the FX at the invader),
    /// kind 3 (invaded) and 450 (key 6), the chat line; the match start spell on both; the shards; the anti-reset clock.
    /// </summary>
    private void Begin(CorruptedRun run, CorruptedInvasion invasion, Map map, Vector2 at, Player invader)
    {
        invasion.Live = true;
        invasion.StartedAt = world.Now;
        invasion.NextImps = world.Now + TimeSpan.FromSeconds(run.Difficulty.AntiResetSpawnSeconds is > 0 and var s ? s : 180);
        SpawnShards(run, invasion, map);
        Update(run, 12, 0, Vector2.Zero);
        Update(run, 7, 0, at);
        Update(run, 3, 0, at);
        foreach (var p in Inside(run)) p.Session.Send(Status(p, run));
        if (Data is { } data)
            foreach (var p in new[] { OwnerIn(run), invader }.OfType<Player>()) Pvp.ApplySpell(p, data.MatchStartSpell, world.GameNow);
        if (OwnerIn(run) is { } owner) Expeditions.Tell(owner, Settings.Text("cd.invaded", ("shards", invasion.Shards.Count)));
        if (!invader.IsBot) Expeditions.Tell(invader, Settings.Text("cd.invading", ("owner", run.OwnerName)));
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"{run}: invaded by {(invader.IsBot ? "the bot" : "the player")} '{invader.Character.Name}' at ({at.X:0}, {at.Y:0}), {invasion.Shards.Count} shard(s)"));
    }

    /// <summary>
    /// objectiveclearrequirement (3) Demonic Shards: the run tier's CD_STATUE mob at the template's statue points (the farthest apart first),
    /// the counted mobs' points where it has too few.
    /// </summary>
    private void SpawnShards(CorruptedRun run, CorruptedInvasion invasion, Map map)
    {
        if (map.Template is not { } t) return;
        var need = Math.Max(1, run.Difficulty.ObjectiveClear);
        var statues = t.MobSpawnPoints.Where(m => m.SpawnBehavior == CorruptedData.Statue && m.Block is not null).ToList();
        var template = statues.Select(m => m.Block!.Pick(map.Random)).FirstOrDefault(m => m is not null)
                       ?? world.Clusters?.Mobs.Find(string.Create(CultureInfo.InvariantCulture, $"T{run.Difficulty.Tier}_MOB_CD_STATUE"));
        if (template is null) return;
        var spots = statues.Select(m => (m.Position, m.Direction)).ToList();
        if (spots.Count < need)
            spots.AddRange(t.MobSpawnPoints.Where(m => m.Block is not null && CorruptedData.Counts(m.SpawnBehavior)).Select(m => (m.Position, m.Direction)));
        var chosen = new List<(Vector2 Position, float Direction)>();
        while (chosen.Count < need && spots.Count > 0)
        {
            var next = chosen.Count == 0 ? spots[Rng.Next(spots.Count)] : spots.MaxBy(s => chosen.Min(c => Vector2.Distance(c.Position, s.Position)));
            chosen.Add(next);
            spots.Remove(next);
        }
        foreach (var (position, direction) in chosen)
            invasion.Shards.Add(SpawnManager.SpawnAt(map, template, position, direction, world, "GUARD").Id);
    }

    /// <summary>
    /// A shard died (<see cref="Killed"/>): 449 kind 5 with the count destroyed; the last one ends the invasion, won by the owner (its reward
    /// only when the owner destroyed every shard itself, not the invader for it).
    /// </summary>
    private void ShardDestroyed(CorruptedRun run, CorruptedInvasion invasion, Mob mob, Player killer)
    {
        if (!invasion.Live || invasion.Ended) return;
        invasion.Destroyed++;
        if (killer.Character.Id == run.Owner) invasion.DestroyedByOwner++;
        Update(run, 5, invasion.Destroyed, mob.Position);
        Log.Info("world", $"{run}: shard {invasion.Destroyed}/{Math.Max(1, run.Difficulty.ObjectiveClear)} destroyed by '{killer.Character.Name}'");
        if (invasion.Destroyed >= Math.Max(1, run.Difficulty.ObjectiveClear))
            End(run, invasion, ownerWon: true, "the shards are destroyed", fought: invasion.DestroyedByOwner == invasion.Destroyed);
    }

    // ---- the fight and its end ----

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the two sides of a fight under way on their map: Hostile to each other alone (Pvp.Of, BotPvp.Of).</summary>
    public bool Opponents(Player a, Player b)
    {
        if (a.Map is not { } map || b.Map != map || Find(map.Cluster) is not { Invasion: { Live: true, Ended: false } invasion } run) return false;
        var (x, y) = (a.Character.Id, b.Character.Id);
        return x == run.Owner && y == invasion.InvaderId || y == run.Owner && x == invasion.InvaderId;
    }

    /// <summary>The owner a bot invader hunts (<see cref="InvaderBrain"/>): on its map, while its fight is on; null otherwise.</summary>
    public Player? TargetOf(Player bot) =>
        Find(bot.Map?.Cluster) is { Invasion: { Live: true, Ended: false } invasion } run && invasion.Bot == bot ? OwnerIn(run) : null;

    /// <summary>
    /// <paramref name="winner"/> knocked down or killed <paramref name="loser"/> (Pvp.KnockedDown, Death.Kill; after the PvP goals): when they
    /// are the two sides of a fight, it ends (the owner wins when the loser is the invader).
    /// </summary>
    public void Defeated(Player winner, Player loser)
    {
        if (!Opponents(winner, loser) || Find(loser.Map!.Cluster) is not { Invasion: { } invasion } run) return;
        End(run, invasion, ownerWon: winner.Character.Id == run.Owner, $"'{winner.Character.Name}' beat '{loser.Character.Name}'");
    }

    /// <summary>
    /// A bot invader at 0 health (BotCombat.Defeated, instead of its rewards; SPEC §4.5): the PvP goals with the bot as the victim
    /// (KillCorruptedDungeonInvader), then the invasion is the owner's; no fame, bag or silver of the bot.
    /// </summary>
    public void InvaderBeaten(Player bot, Player killer, List<(Player Player, double Damage)> attackers, bool death, bool crime, long ticks)
    {
        if (killer.IsBot || bot.Map is not { } map) return;
        PvpGoals.OnPvpKill(world, new PvpKill(PvpActor.Of(killer), PvpActor.Of(BotVictim.Of(bot)), [.. attackers.Select(a => (PvpActor.Of(a.Player), a.Damage))], map,
            death ? PvpOutcome.Death : PvpOutcome.Knockdown, crime, 0, ticks, Math.Max(1, attackers.Count)));
        if (Find(map.Cluster) is { Invasion: { Ended: false } invasion } run && invasion.Bot == bot)
            End(run, invasion, ownerWon: true, $"'{killer.Character.Name}' beat the invader");
    }

    /// <summary>
    /// The journal's specialcondition of a PvP credit of <paramref name="receiver"/> over <paramref name="victim"/> (albionjournal.xml
    /// PVP_CORRUPTEDS): KillCorruptedDungeonInvader for the owner who beats its invader, KillCorruptedDungeonHost for the invader who beats
    /// the owner; null for any other result.
    /// </summary>
    public string? SpecialOf(Player receiver, PvpActor victim)
    {
        if (Find(receiver.Map?.Cluster) is not { Invasion: { Live: true } invasion } run) return null;
        var id = victim.Player?.Character.Id ?? (victim.Bot is { } b && invasion.Bot?.Id == b.Id ? invasion.Bot.Character.Id : Guid.Empty);
        if (receiver.Character.Id == run.Owner && id == invasion.InvaderId) return "KillCorruptedDungeonInvader";
        if (receiver.Character.Id == invasion.InvaderId && id == run.Owner) return "KillCorruptedDungeonHost";
        return null;
    }

    /// <summary>
    /// The fight is over (SPEC §4.4): 449 kind 10 and 450 (key 6 false) to everyone inside but a player invader, the match end spell on
    /// both, the shards and imps gone. A fight <paramref name="fought"/> out (a knockdown or kill of one side by the other, the owner's own
    /// shards) pays: the owner who won gets InvaderReward (silver × Rates × (1 + the run's bonus), infamy × InfamyFactor), a winning player
    /// invader the reward with its own run's bonus (and under Pvp:FullLoot percentbosslootonkill % of the owner's silver, moved). One that
    /// ends by a leave, a disconnect or the invader's help pays nobody (no silver or infamy from two cooperating accounts). The loser loses
    /// Corrupted:InfamyLossOnDefeat of its infamy either way. A player invader gets 449 kind 9 with the expeltimeafterinvasionend countdown
    /// (its tracker's "returning in": kind 9 while it still shows "invading", so no kind 10 or 450 before it) and goes home then; a beaten bot
    /// goes with its defeat (Bots:DownDespawnSeconds), one banished by the shards at once, one that won 5 s later.
    /// </summary>
    private void End(CorruptedRun run, CorruptedInvasion invasion, bool ownerWon, string why, bool fought = true)
    {
        if (invasion.Ended) return;
        (invasion.Ended, invasion.OwnerWon) = (true, ownerWon);
        var map = world.Maps.Find(run.Id);
        var owner = OwnerIn(run);
        var invader = invasion.Bot ?? (map?.Players.Values.FirstOrDefault(p => p.Character.Id == invasion.Invader));
        var d = run.Difficulty;
        var reward = Settings.InvaderRewardOf(d.Name);
        long Silver(double bonus) => (long)Math.Round(reward.Silver * 10000 * world.Rates.Silver * (1 + bonus));
        var infamy = (long)Math.Round(reward.Infamy * Options.InfamyFactor);
        var expel = Data?.Global("expeltimeafterinvasionend", 30) ?? 30;
        ClearShards(invasion);
        if (invader is { IsBot: false } player)
            player.Session.Send(new CorruptedDungeonUpdateEvent { ddn = player.Id, b0x = 9, b0y = world.GameNow + SpellCasts.Seconds(expel), b0z = Vector2.Zero });
        Update(run, 10, 0, Vector2.Zero, except: invasion.Invader);
        foreach (var p in Inside(run).Where(p => p.Character.Id != invasion.Invader)) p.Session.Send(Status(p, run));
        if (Data is { } data)
            foreach (var p in new[] { owner, invader }.OfType<Player>().Where(p => p.Combat.Down is null)) Pvp.ApplySpell(p, data.MatchEndSpell, world.GameNow);
        if (ownerWon)
        {
            if (owner is not null && fought)
            {
                var silver = Silver(run.Bonus);
                Wallet.Credit(owner, silver, $"corrupted invasion {d.Rank}");
                Credit(owner, infamy, d);
                Expeditions.Tell(owner, Settings.Text("cd.repelled", ("infamy", infamy.ToString("N0", CultureInfo.InvariantCulture)),
                    ("silver", (silver / 10000).ToString("N0", CultureInfo.InvariantCulture))));
            }
            else if (owner is not null) Expeditions.Tell(owner, Settings.Text("cd.over"));
            if (invader is { IsBot: false })
            {
                Lose(invader, Options.InfamyLossOnDefeat);
                Expeditions.Tell(invader, Settings.Text(fought ? "cd.beaten" : "cd.over", ("seconds", Math.Round(expel))));
            }
        }
        else
        {
            if (owner is not null)
            {
                Lose(owner, Options.InfamyLossOnDefeat);
                Expeditions.Tell(owner, Settings.Text(fought ? "cd.lost" : "cd.over"));
            }
            if (invader is { IsBot: false } && fought)
            {
                long share = 0;
                if (world.Config.PvpFullLoot && owner is not null && d.BossLootPercent > 0)
                {
                    share = (long)Math.Floor(owner.Character.Silver * d.BossLootPercent / 100);
                    if (share > 0 && Wallet.TrySpend(owner, share, $"corrupted invasion lost to '{invader.Character.Name}'")) Wallet.Credit(invader, share, "corrupted invasion loot share");
                    else share = 0;
                }
                var silver = Silver(invasion.From?.Bonus ?? 0);
                Wallet.Credit(invader, silver, $"corrupted invasion {d.Rank}");
                Credit(invader, infamy, d);
                Expeditions.Tell(invader, Settings.Text("cd.won", ("infamy", infamy.ToString("N0", CultureInfo.InvariantCulture)),
                    ("silver", ((silver + share) / 10000).ToString("N0", CultureInfo.InvariantCulture)), ("seconds", Math.Round(expel))));
            }
            else if (invader is { IsBot: false }) Expeditions.Tell(invader, Settings.Text("cd.over"));
        }
        invasion.GoneAt = world.Now + TimeSpan.FromSeconds(invasion.Bot is null ? expel : !ownerWon ? 5 : invasion.Destroyed > 0 && !invasion.Bot.Bot!.Fight.Defeated ? 0 : 10);
        Log.Info("world", $"{run}: the invasion by '{invasion.InvaderName}' is over, {(ownerWon ? "repelled" : "the owner lost")} ({why}{(fought ? "" : ", no reward")})");
    }

    /// <summary>
    /// An invasion under way (<see cref="UpdateInvasions"/>): a player invader's countdown ends with its move (ChangeCluster push into the
    /// target at its farthest invader spawn point, its own run waiting: Away), its Join due within ArrivalGraceSeconds; a fight goes on while
    /// both sides are on the map (one off it for <see cref="MissingSeconds"/> ends it: the one there wins, unpaid), with its anti-reset imps; an ended
    /// one finishes when its invader's time is up (<see cref="Finish"/>).
    /// </summary>
    private void Progress(CorruptedRun run, CorruptedInvasion invasion, TimeSpan now)
    {
        if (invasion.Ended)
        {
            if (invasion.GoneAt is { } gone && now >= gone) Finish(run, invasion);
            return;
        }
        if (!invasion.Live)
        {
            if (invasion.MoveAt is { } move && now >= move)
            {
                invasion.MoveAt = null;
                if (invasion.From is not { Closed: false } from || OwnerIn(from) is not { Combat.Down: null } invader || OwnerIn(run) is not { } owner
                    || world.Maps.Find(run.Id)?.Template is not { } template)
                {
                    Abandon(run, invasion, "the invader or the owner is gone");
                    return;
                }
                var at = InvaderSpawn(template, owner.PositionAt(DateTime.UtcNow));
                var here = invader.PositionAt(DateTime.UtcNow);
                invasion.Home = new Arrival(from.Id, here, invader.Facing);
                invasion.ArriveBy = now + TimeSpan.FromSeconds(ArrivalGraceSeconds);
                from.Away = true;
                run.Arriving[invader.Character.Id] = now + TimeSpan.FromSeconds(ArrivalGraceSeconds);
                invader.Session.Teleport(new Arrival(run.Id, at, Transfers.Facing(at, owner.PositionAt(DateTime.UtcNow))), $"corrupted:{run.Id}:invade", run.World,
                    type: run.Difficulty.ClusterType);
            }
            else if (invasion.ArriveBy is { } by && now >= by) Abandon(run, invasion, "the invader did not arrive");
            return;
        }
        var map = world.Maps.Find(run.Id);
        var ownerHere = OwnerIn(run) is not null;
        var invaderHere = invasion.Bot is { } bot ? bot.Map == map : map?.Players.Values.Any(p => p.Character.Id == invasion.Invader) == true;
        if (ownerHere && invaderHere) invasion.MissingSince = null;
        else if ((invasion.MissingSince ??= now) <= now - TimeSpan.FromSeconds(MissingSeconds))
        {
            End(run, invasion, ownerWon: ownerHere, ownerHere ? "the invader is gone" : "the owner is gone", fought: false);
            return;
        }
        if (map is not null) AntiReset(run, invasion, map, now);
    }

    /// <summary>
    /// The anti-reset imps (Corrupted:AntiReset; SPEC §6 phase B 5 [design]): every timeuntilantiresetmobspawninseconds of the fight
    /// Corrupted:AntiResetImps CD_DEMON_IMP_ANTIRESET mobs at the anti-reset points nearest to the owner that stand at least
    /// antiresetmobrequiredplayerdistanceforspawn from every player; each goes timeuntilantiresetmobdespawninseconds later.
    /// </summary>
    private void AntiReset(CorruptedRun run, CorruptedInvasion invasion, Map map, TimeSpan now)
    {
        foreach (var (mob, until) in invasion.Imps.ToList())
            if (now >= until || mob.Dead || mob.Removed)
            {
                invasion.Imps.Remove((mob, until));
                if (!mob.Dead) SpawnManager.Despawn(mob, world, respawn: false);
            }
        if (!Options.AntiReset || Options.AntiResetImps <= 0 || now < invasion.NextImps || map.Template is not { } t) return;
        var d = run.Difficulty;
        invasion.NextImps = now + TimeSpan.FromSeconds(d.AntiResetSpawnSeconds > 0 ? d.AntiResetSpawnSeconds : 180);
        var utc = DateTime.UtcNow;
        var players = map.Players.Values.Concat(map.Bots.Values).Select(p => p.PositionAt(utc)).ToList();
        var centre = OwnerIn(run)?.PositionAt(utc) ?? (players.Count > 0 ? players[0] : Vector2.Zero);
        var points = t.MobSpawnPoints.Where(m => m.SpawnBehavior == CorruptedData.AntiResetImp && m.Block is not null
                && players.All(p => Vector2.Distance(p, m.Position) >= d.AntiResetDistance))
            .OrderBy(m => Vector2.Distance(m.Position, centre)).Take(Options.AntiResetImps).ToList();
        var until2 = now + TimeSpan.FromSeconds(d.AntiResetDespawnSeconds > 0 ? d.AntiResetDespawnSeconds : 45);
        foreach (var point in points)
            if (point.Block!.Pick(map.Random) is { } imp)
                invasion.Imps.Add((SpawnManager.SpawnAt(map, imp, point.Position, point.Direction, world, point.Behavior ?? "GUARD"), until2));
        if (points.Count > 0) Log.Info("world", $"{run}: {points.Count} anti-reset imp(s)");
    }

    /// <summary>The shards and imps still standing go (no loot, no points).</summary>
    private void ClearShards(CorruptedInvasion invasion)
    {
        if (world.Maps.Find(invasion.Run.Id) is not { } map) return;
        foreach (var id in invasion.Shards)
            if (map.Objects.GetValueOrDefault(id) is Mob { Dead: false } shard) SpawnManager.Despawn(shard, world, respawn: false);
        invasion.Shards.Clear();
        foreach (var (imp, _) in invasion.Imps)
            if (!imp.Dead) SpawnManager.Despawn(imp, world, respawn: false);
        invasion.Imps.Clear();
    }

    /// <summary>
    /// The ended invasion's invader goes: a bot off the map (if its defeat has not taken it yet), a player back into its own run (a push; one
    /// offline goes there at its next Join into this run); the run is free for the next one (searching again while more may come).
    /// </summary>
    private void Finish(CorruptedRun run, CorruptedInvasion invasion)
    {
        run.Invasion = null;
        if (invasion.Bot is { Map: not null } bot) world.Bots.Despawn(bot, invasion.OwnerWon ? "banished" : "its invasion is won", BotRefill.Now);
        if (invasion.Invader is { } id && !invasion.HomeSent) SendHome(run, invasion, id);
        if (Searching(run) && OwnerIn(run) is { } owner)
        {
            owner.Session.Send(Status(owner, run));
            Update(run, 12, 1, Vector2.Zero);
        }
    }

    /// <summary>The player invader <paramref name="id"/> back into its own run (or, offline, at its next Join into <paramref name="run"/>).</summary>
    private void SendHome(CorruptedRun run, CorruptedInvasion invasion, Guid id)
    {
        invasion.HomeSent = true;
        var home = HomeOf(invasion);
        if (invasion.From is { } from) from.Away = false;
        if (world.Maps.Find(run.Id)?.Players.Values.FirstOrDefault(p => p.Character.Id == id) is { } invader
            && invader.Session.Status == SessionStatus.InWorld && invader.Session.Player == invader)
        {
            if (invader.Combat.Down is { Kind: KnockdownKind.Dead })
            {
                // Killed (the official preset's lethal ranks): its city respawn follows, its own run counts as left.
                if (invasion.From is { } own) LeftEarly(invader, own);
            }
            else if (home is { } h)
            {
                if (invasion.From is { } own) own.Arriving[id] = world.Now + TimeSpan.FromSeconds(ArrivalGraceSeconds);
                invader.Session.Teleport(h.To, $"corrupted:{run.Id}:expelled", h.World, type: h.Type);
            }
            else Leave(invader, run, "expelled");
        }
        else if (home is { } h) _homes[id] = (run.Id, h.To, h.World, h.Type);
    }

    /// <summary>Where a player invader goes back: its place in its own run while that is live (with the push's keys 1 and 4); null when it is gone.</summary>
    private static (Arrival To, string? World, string? Type)? HomeOf(CorruptedInvasion invasion) =>
        invasion is { From: { Closed: false } from, Home: { } home } ? (home, from.World, from.Difficulty.ClusterType) : null;

    /// <summary>
    /// An invasion ends without a winner (the owner left, died or its run closes; the invader is gone before its arrival): 449 kind 10 to whoever
    /// is inside, the bot gone, a player invader home (a counting-down one gets kind 10: its countdown is off), the run free.
    /// </summary>
    private void Abandon(CorruptedRun run, CorruptedInvasion invasion, string why)
    {
        if (run.Invasion != invasion) return;
        ClearShards(invasion);
        if (!invasion.Ended)
        {
            invasion.Ended = true;
            Update(run, 10, 0, Vector2.Zero);
            foreach (var p in Inside(run).Where(p => p.Character.Id != invasion.Invader)) p.Session.Send(Status(p, run));
        }
        if (invasion is { Live: false, From: { } from } && OwnerIn(from) is { } waiting)
            waiting.Session.Send(new CorruptedDungeonUpdateEvent { ddn = waiting.Id, b0x = 10 });
        invasion.OwnerWon = true;
        Finish(run, invasion);
        Log.Info("world", $"{run}: the invasion by '{invasion.InvaderName}' is called off ({why})");
    }

    /// <summary>
    /// A player invader leaves <paramref name="run"/> by itself (the exit portal, the escape, .cd leave, the run's closing; <see cref="Leave"/>):
    /// an invasion still on is the owner's (unpaid: no fight); it goes back into its own run (that arrival with the push's keys 1 and 4). Null for anyone else,
    /// or when its own run is gone (then the usual way back).
    /// </summary>
    private (Arrival To, string? World, string? Type)? InvaderHome(Player p, CorruptedRun run, string why)
    {
        if (run.Invasion is not { Bot: null } invasion || invasion.Invader != p.Character.Id) return null;
        if (!invasion.Ended) End(run, invasion, ownerWon: true, $"the invader left ({why})", fought: false);
        invasion.HomeSent = true;
        if (invasion.From is { } from)
        {
            from.Away = false;
            from.Arriving[p.Character.Id] = world.Now + TimeSpan.FromSeconds(ArrivalGraceSeconds);
        }
        run.Arriving.Remove(p.Character.Id);
        return HomeOf(invasion);
    }

    /// <summary>A player invader died in <paramref name="run"/> (DiedOut: the city respawn follows): its own run counts as left.</summary>
    private void InvaderDied(Player p, CorruptedRun run, CorruptedInvasion invasion)
    {
        if (!invasion.Ended) End(run, invasion, ownerWon: true, "the invader died", fought: false); // not by the owner (that is Defeated, before)
        invasion.HomeSent = true;
        if (invasion.From is { } from)
        {
            from.Away = false;
            LeftEarly(p, from);
        }
    }

    /// <summary>
    /// A player who is not the owner joined <paramref name="run"/> (<see cref="Joined"/>): the matched invader's arrival begins the fight (450
    /// again with key 6); one whose invasion ended while it was away goes home now. Anyone else (a GM) just watches.
    /// </summary>
    private void InvaderJoined(Player p, CorruptedRun run)
    {
        if (run.Invasion is { Bot: null, Live: false, Ended: false } invasion && invasion.Invader == p.Character.Id && world.Maps.Find(run.Id) is { } map)
        {
            invasion.ArriveBy = null;
            Begin(run, invasion, map, p.PositionAt(DateTime.UtcNow), p);
            return;
        }
        if (_homes.Remove(p.Character.Id, out var home) && home.Cluster == run.Id)
            p.Session.Teleport(home.To, $"corrupted:{run.Id}:expelled", home.World, type: home.Type);
    }

    /// <summary>
    /// A run's chests are its owner's alone (RandomDungeons.OpenChest, RegisterToObject on a chest): anyone else in it (a player invader, a
    /// GM watching) is refused with the chat line, so no loot goes from one player's run to another (pve: no loot loss). Why not, or null.
    /// </summary>
    public string? ChestRefused(Player p, LootChest chest)
    {
        if (chest.Map is not { } map || Find(map.Cluster) is not { } run || p.Character.Id == run.Owner) return null;
        const string why = "only the dungeon's owner may open its chests";
        Expeditions.Tell(p, Settings.Text("cd.refused", ("reason", why)));
        return why;
    }

    /// <summary>The push keys 1 and 4 of an arrival in <paramref name="cluster"/>: a live run's map and cluster type; none for any other cluster.</summary>
    public (string? World, string? Type) KeysOf(string cluster) => Find(cluster) is { } r ? (r.World, r.Difficulty.ClusterType) : (null, null);

    // ---- the chain (SPEC §6 phase B 4) ----

    /// <summary>
    /// The chain exit of a completed run (Corrupted:Chain): a corrupted exit (324 with key 12) with the chain exit's visualization at the
    /// demon's point, 3 m aside (the demon's body lies there); null without the visualization.
    /// </summary>
    private CorruptedExitPortal? MakeChainExit(CorruptedRun run)
    {
        if (run.ChainExit is not null || run.DemonPoint is not { } point || world.Clusters?.DungeonsIfLoaded is not { } dungeons || Data is not { } data
            || !dungeons.Visualizations.TryGetValue(data.ChainExitVisualization, out var vis) || vis.Fallback.FirstOrDefault() is not { } prefab) return null;
        var size = TileMath.V2((string?)world.Clusters.Tiles.Components(prefab.Name).FirstOrDefault(c => c.Name.LocalName == "randomdungeonrandomexit")?.Attribute("size")) ?? Vector2.One;
        var name = run.Difficulty.LethalEntrance ? "CORRUPTED_SOLO_LETHAL" : "CORRUPTED_SOLO_NONLETHAL";
        var spot = point with { Position = point.Position + new Vector2(3, 0) };
        return run.ChainExit = new CorruptedExitPortal(world.ObjectIds.Next(), spot, name, prefab.Name, prefab.Index, size, chain: true);
    }

    /// <summary>.cd invade: an invasion of the player's run now, by the bot (or the waiting consenting player <paramref name="player"/>); why not, or null.</summary>
    public string? Invade(CorruptedRun run, bool bot)
    {
        if (run.Invasion is not null) return "an invasion is on already";
        if (OwnerIn(run) is null) return "the owner is not inside";
        if (bot)
        {
            if (!world.Bots.InvaderBots) return "bot invaders are off (Bots:Enabled, Bots:Invader:Enabled)";
            return StartBot(run) is null ? "no bot invader could be made now (the walk grid may still be built: try again)" : null;
        }
        if (Waiting(run) is not { } from) return "no consenting player of this rank is waiting";
        StartPlayer(run, from);
        return null;
    }
}
