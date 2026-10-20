using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// DuelEndEvents (stageE pvp SPEC §2.5): DuelEnded key 1. 3-10 name the challenger, 11-18 the same for the challengee
/// (+ <see cref="Duels.ChallengeeOffset"/>).
/// </summary>
public enum DuelEnd : byte
{
    None = 0,
    Invalid = 1,
    OutsiderInterruptedDuel = 2,
    ChallengerKnockedDownByChallengee = 3,
    ChallengerKnockedDownByOutsider = 4,
    ChallengerKnockedDownByNonPlayer = 5,
    ChallengerInterruptedPreparation = 6,
    ChallengerKilledHimself = 7,
    ChallengerLeftArea = 8,
    ChallengerLeftAreaDuringPreparation = 9,
    ChallengerDestroyed = 10,
    DuelDurationEnded = 19,
    CanceledBySystem = 20,
}

/// <summary>
/// A duel (stageE pvp SPEC §2.6): the two players, the wager both put in at the start (a7m raw), the post (the flag object: its
/// id and position, the midpoint of the two [infer]), the start and end GameTimeStamps (DuelStarted keys 3/4) and the left-area
/// deadline of each (challenger 0, challengee 1; null inside). <see cref="PostKnown"/>: the clients on <see cref="Map"/> that got
/// NewDuellingPost, who get its Leave at the end (one that leaves the map drops out: its client drops the post with the map).
/// </summary>
public sealed class Duel(Player challenger, Player challengee, long wager, long postId, Vector2 post, long start, long end, Map map)
{
    public Map Map { get; } = map;
    public Player Challenger { get; } = challenger;
    public Player Challengee { get; } = challengee;
    public long Wager { get; } = wager;
    public long PostId { get; } = postId;
    public Vector2 Post { get; } = post;
    public long Start { get; } = start;
    public long End { get; } = end;
    public long?[] LeftAreaDeadline { get; } = new long?[2];
    public HashSet<Player> PostKnown { get; } = [];

    /// <summary>The other duelist.</summary>
    public Player Opponent(Player p) => p == Challenger ? Challengee : Challenger;
}

/// <summary>An open challenge (op 110 accepted): the challenged player, the wager and when it lapses (GameTimeStamp).</summary>
public sealed record DuelChallenge(Player Target, long Wager, long Expires);

/// <summary>
/// Duels (stageE pvp SPEC §2, sub-step E1; gamedata &lt;DuelSettings&gt;, <see cref="DuelSettings"/>). A challenge (op 110) is
/// checked in the order of the client's error codes 4400-4410 and goes to the target as DuellingChallengePlayer (183); it lapses
/// after AcceptDuelCountDownInSeconds (13 s) or when the challenger leaves the 25 m (DuelRequestCanceled 188 to the target [infer
/// for the trigger]). Accept (op 111, 4441-4445) starts the duel: both wagers are put in [infer], then NewDuellingPost (184) and
/// DuelStarted (185) for the challenger (key 2 true) and the challengee to everyone who sees either (as captured); start =
/// now + DuelPreparationTimeInSeconds, end = start + DuelMaxDurationInMins. Deny (op 112): DuelDenied (187) to the challenger.
/// During the duel the two are Hostile to each other (<see cref="Pvp.Of(Player, Player)"/>) once it has started: before, their
/// attacks are refused and their hits dropped [infer; the SPEC's "attacking in preparation ends it" (6/14) is not done, its own
/// test keeps the duel going]. Each tick (<see cref="Update"/>): a duelist farther than DuelAreaRadius from the post gets
/// DuelLeftArea (189) {1 now + DuelAreaLeftTimerInSeconds} and, back inside, DuelReEnteredArea (190); the deadline ends the duel
/// (8/16), leaving during the preparation at once (9/17); the end time 19. A knockdown (<see cref="KnockedDown"/>) ends it by the
/// opponent (3/11), an outsider (4/12), a mob (5/13) or the player itself (7/15); leaving the map 10/18; a non-duelist's hit on a
/// duelist 2 [infer]. The end (<see cref="Finish"/>): DuelEnded (186) {0 duelist, 1 reason, 2 wager} to each duelist's observers,
/// the knocked-down loser first, the post's Leave, the combat end of a duelist who fights no mob, and the wagers: both to the
/// winner (cdo.a: 3, 5, 7, 8, 10 → the challengee, 11, 13, 15, 16, 18 → the challenger), back to each on a draw. Nothing is
/// saved but the silver: a duel ends with the connection, and a server stop ends them all as a draw first (<see cref="CancelAll"/>;
/// a crash loses the wagers in escrow). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Duels(World world)
{
    // ---- op 110 return codes (dfd.c 0x180AF3A20) ----
    public const short PlayerNotFound = 4400;
    public const short ChallengeFailed = 4401;
    public const short YouHaveDuelPending = 4402;
    public const short YouAreAlreadyInDuel = 4403;
    public const short PlayerIsDead = 4404;
    public const short PlayerOutOfRange = 4405;
    public const short NotEnoughSilver = 4406;
    public const short PlayerHasDuelPending = 4407;
    public const short PlayerIsAlreadyInDuel = 4408;
    public const short PlayerBusy = 4409;
    public const short NotAllowed = 4410;

    // ---- op 111 return codes (dfd.a 0x180AF2E30, §6 V5) ----
    public const short AcceptYouAreDead = 4441;
    public const short AcceptPlayerIsDead = 4442;
    public const short AcceptOutOfRange = 4443;
    public const short AcceptNotEnoughSilver = 4444;
    public const short NoActiveDuel = 4445;

    /// <summary>
    /// Any other refused accept ("@DUELLING_ERROR_ACCEPT_CHALLENGE_FAILED"): the challenger lacks the wager now, either is in a duel,
    /// mounted or in combat, or the challengee is at a station [infer].
    /// </summary>
    public const short AcceptFailed = 4446;

    /// <summary>DuelEndEvents of the challengee = the challenger's + 8.</summary>
    public const byte ChallengeeOffset = 8;

    private readonly Dictionary<Player, Duel> _duels = [];
    private readonly Dictionary<Player, DuelChallenge> _challenges = [];
    private readonly Dictionary<Player, long> _lastChallenge = [];

    private DuelSettings Settings => world.GameData?.Duel ?? DuelSettings.Default;

    /// <summary>The duel <paramref name="p"/> is in; null when none.</summary>
    public Duel? Of(Player p) => _duels.GetValueOrDefault(p);

    /// <summary>The other duelist of <paramref name="p"/>'s duel; null outside one.</summary>
    public Player? OpponentOf(Player p) => Of(p)?.Opponent(p);

    /// <summary>The open challenge of <paramref name="challenger"/>; null when none.</summary>
    public DuelChallenge? ChallengeOf(Player challenger) => _challenges.GetValueOrDefault(challenger);

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are opponents in a duel that has not started at <paramref name="now"/>.</summary>
    public bool Preparing(Player a, Player b, long now) => Of(a) is { } d && d.Opponent(a) == b && now < d.Start;

    /// <summary>
    /// Op 110 of <paramref name="a"/> on object <paramref name="targetId"/> with <paramref name="wager"/> (a7m): the return code, in
    /// the order of the codes; 0 = sent (DuellingChallengePlayer {0 a, 1 wager} to the target only).
    /// </summary>
    public short Challenge(Player a, long targetId, long wager)
    {
        var now = world.GameNow;
        var d = Settings;
        if (a.Map is not { } map || map.Players.GetValueOrDefault(targetId) is not { } b || b == a || !a.Observers.Contains(b)) return PlayerNotFound;
        if (wager < 0 || a.Combat.Down is not null) return ChallengeFailed;
        if (_lastChallenge.TryGetValue(a, out var last) && now < last + SpellCasts.Seconds(d.IssueIntervalSeconds) && !_challenges.ContainsKey(a))
            return ChallengeFailed; // IssueDuelIntervalTimeInSeconds: the client waits too (its message 14); no own code [infer]
        if (_challenges.ContainsKey(a)) return YouHaveDuelPending;
        if (_duels.ContainsKey(a)) return YouAreAlreadyInDuel;
        if (b.Combat.Down is not null) return PlayerIsDead;
        if (Distance(a, b) > d.AreaRadius) return PlayerOutOfRange;
        if (a.Character.Silver < wager) return NotEnoughSilver;
        if (_challenges.ContainsKey(b) || _challenges.Values.Any(c => c.Target == b)) return PlayerHasDuelPending;
        if (_duels.ContainsKey(b)) return PlayerIsAlreadyInDuel;
        if (b.Action is not null) return PlayerBusy; // at a station [infer: the client's dialog state is not known here]
        if (!Pvp.RulesOf(world, map).AllowDuel || a.IsMounted || b.IsMounted || a.Combat.InCombat || b.Combat.InCombat) return NotAllowed;
        _challenges[a] = new DuelChallenge(b, wager, now + SpellCasts.Seconds(d.AcceptSeconds));
        _lastChallenge[a] = now;
        b.Session.Send(new DuellingChallengePlayerEvent { b37 = a.Id, b38 = wager });
        Log.Info("duel", string.Create(CultureInfo.InvariantCulture, $"'{a.Character.Name}' challenges '{b.Character.Name}' for {wager / 10000.0:0.####} silver"));
        return 0;
    }

    /// <summary>Op 111 of <paramref name="b"/>: the open challenge addressed to it starts its duel (rc 0), or the refusal.</summary>
    public short Accept(Player b)
    {
        if (_challenges.FirstOrDefault(c => c.Value.Target == b) is not { Key: { } a, Value: { } challenge }) return NoActiveDuel;
        if (b.Combat.Down is not null) return AcceptYouAreDead;
        if (a.Map != b.Map || a.Combat.Down is not null) return AcceptPlayerIsDead;
        if (Distance(a, b) > Settings.AreaRadius) return AcceptOutOfRange;
        if (b.Character.Silver < challenge.Wager) return AcceptNotEnoughSilver;
        if (a.Character.Silver < challenge.Wager || _duels.ContainsKey(a) || _duels.ContainsKey(b)) return AcceptFailed;
        // Challenge's NotAllowed conditions again: a mount, a fight or a station since the challenge [infer: the generic code].
        if (a.IsMounted || b.IsMounted || a.Combat.InCombat || b.Combat.InCombat || b.Action is not null) return AcceptFailed;
        _challenges.Remove(a);
        Begin(a, b, challenge.Wager);
        return 0;
    }

    /// <summary>Op 112 of <paramref name="b"/>: the challenge addressed to it is gone, the challenger gets DuelDenied {0 challenger, 1 b}; rc 0, or <see cref="NoActiveDuel"/> with none.</summary>
    public short Deny(Player b)
    {
        if (_challenges.FirstOrDefault(c => c.Value.Target == b).Key is not { } a) return NoActiveDuel;
        _challenges.Remove(a);
        a.Session.Send(new DuelDeniedEvent { ddn = a.Id, b33 = b.Id });
        Log.Info("duel", $"'{b.Character.Name}' denies '{a.Character.Name}''s challenge");
        return 0;
    }

    /// <summary>
    /// The duel of <paramref name="a"/> (the challenger) and <paramref name="b"/> starts: both wagers are put in (UpdateMoney to
    /// each), the post at their midpoint, NewDuellingPost to everyone who sees either, then DuelStarted for the challenger and for
    /// the challengee to each one's observers (captured order).
    /// </summary>
    private void Begin(Player a, Player b, long wager)
    {
        Wallet.TrySpend(a, wager, "duel wager");
        Wallet.TrySpend(b, wager, "duel wager");
        var now = world.GameNow;
        var d = Settings;
        var start = now + SpellCasts.Seconds(d.PreparationSeconds);
        var utc = DateTime.UtcNow;
        var duel = new Duel(a, b, wager, world.ObjectIds.Next(), (a.PositionAt(utc) + b.PositionAt(utc)) / 2, start, start + SpellCasts.Seconds(d.MaxMinutes * 60), a.Map!);
        _duels[a] = duel;
        _duels[b] = duel;
        foreach (var o in Audience(a).Union(Audience(b)))
        {
            o.Session.Send(PostEvent(duel));
            duel.PostKnown.Add(o);
        }
        foreach (var p in new[] { a, b })
        {
            var started = Started(duel, p);
            foreach (var o in Audience(p)) o.Session.Send(started);
        }
        Log.Info("duel", $"'{a.Character.Name}' and '{b.Character.Name}' duel (post {duel.PostId})");
    }

    /// <summary>
    /// A client gets <paramref name="about"/>'s NewCharacter (<see cref="VisibilityTracker"/>): a duelist's DuelStarted follows
    /// (18 of 18 captured repeats came then, with the same times), after the post when that client has not got it.
    /// </summary>
    public void ForNewObserver(Player about, Player viewer)
    {
        if (Of(about) is not { } duel) return;
        if (duel.PostKnown.Add(viewer)) viewer.Session.Send(PostEvent(duel));
        viewer.Session.Send(Started(duel, about));
    }

    /// <summary>
    /// A hit of <paramref name="attacker"/> on <paramref name="target"/> lands (<see cref="Pvp.Damage"/>): a duelist hit by
    /// anyone but its opponent ends its duel as OutsiderInterruptedDuel (2, a draw) [infer].
    /// </summary>
    public void Damaged(Player attacker, Player target)
    {
        if (Of(target) is { } duel && duel.Opponent(target) != attacker) Finish(duel, (byte)DuelEnd.OutsiderInterruptedDuel, first: null);
    }

    /// <summary>
    /// <paramref name="loser"/> is knocked down by object <paramref name="killer"/> (<see cref="Knockdown.Start"/>): its duel ends by
    /// its opponent (3/11), the player itself (7/15: a GM .kill), another player (4/12) or a mob (5/13), the loser's DuelEnded first.
    /// </summary>
    public void KnockedDown(Player loser, long killer)
    {
        if (Of(loser) is not { } duel) return;
        var opponent = duel.Opponent(loser);
        var reason = killer == opponent.Id ? DuelEnd.ChallengerKnockedDownByChallengee
            : killer == loser.Id ? DuelEnd.ChallengerKilledHimself
            : loser.Map?.FindPlayer(killer) is not null ? DuelEnd.ChallengerKnockedDownByOutsider // a player or a player-bot (stage I)
            : DuelEnd.ChallengerKnockedDownByNonPlayer;
        Finish(duel, Side(reason, loser == duel.Challengee), first: loser);
    }

    /// <summary>
    /// <paramref name="p"/> leaves its map (logout, cluster change: <see cref="Combat.Leave"/>): its duel ends as destroyed (10/18),
    /// its challenge lapses (DuelRequestCanceled to the target), a challenge to it is dropped, and it no longer knows any duel's post.
    /// </summary>
    public void Left(Player p)
    {
        foreach (var d in _duels.Values) d.PostKnown.Remove(p);
        if (_challenges.Remove(p, out var mine)) Cancel(p, mine);
        foreach (var a in _challenges.Where(c => c.Value.Target == p).Select(c => c.Key).ToList()) _challenges.Remove(a);
        _lastChallenge.Remove(p);
        if (Of(p) is { } duel) Finish(duel, Side(DuelEnd.ChallengerDestroyed, p == duel.Challengee), first: p);
    }

    /// <summary>
    /// The server stops (ServerHost.Dispose, before the sessions close): every open duel ends as CanceledBySystem, a draw, so both
    /// wagers go back and are saved with the characters. Without it the first duelist to disconnect would lose (10/18). A crash or
    /// kill of the process during a duel still loses both wagers: the escrow lives only in memory (nothing persists a duel).
    /// </summary>
    public void CancelAll()
    {
        foreach (var duel in _duels.Values.Distinct().ToList()) Finish(duel, (byte)DuelEnd.CanceledBySystem, first: null);
    }

    /// <summary>A GM ends <paramref name="p"/>'s duel (CanceledBySystem, a draw); false outside one.</summary>
    public bool Cancel(Player p)
    {
        if (Of(p) is not { } duel) return false;
        Finish(duel, (byte)DuelEnd.CanceledBySystem, first: null);
        return true;
    }

    /// <summary>
    /// The world tick: challenges that lapse or whose challenger is out of range or off the map; per duel the end time (19), then
    /// for each duelist standing the area (the left-area deadline, its end, leaving during the preparation).
    /// </summary>
    public void Update()
    {
        if (_challenges.Count == 0 && _duels.Count == 0) return;
        var now = world.GameNow;
        var d = Settings;
        foreach (var (a, c) in _challenges.ToList())
            if (now >= c.Expires || a.Map is null || a.Map != c.Target.Map || Distance(a, c.Target) > d.AreaRadius)
            {
                _challenges.Remove(a);
                Cancel(a, c);
            }
        foreach (var duel in _duels.Values.Distinct().ToList())
        {
            if (now >= duel.End)
            {
                Finish(duel, (byte)DuelEnd.DuelDurationEnded, first: null);
                continue;
            }
            for (var i = 0; i < 2 && _duels.ContainsKey(duel.Challenger); i++)
            {
                var p = i == 0 ? duel.Challenger : duel.Challengee;
                if (p.Combat.Down is not null) continue;
                var outside = Vector2.Distance(p.PositionAt(DateTime.UtcNow), duel.Post) > d.AreaRadius;
                if (outside && now < duel.Start)
                    Finish(duel, Side(DuelEnd.ChallengerLeftAreaDuringPreparation, i == 1), first: p);
                else if (outside && duel.LeftAreaDeadline[i] is null)
                {
                    var deadline = now + SpellCasts.Seconds(d.LeftAreaSeconds);
                    duel.LeftAreaDeadline[i] = deadline;
                    foreach (var o in Audience(p)) o.Session.Send(new DuelLeftAreaEvent { ddn = p.Id, b36 = deadline });
                }
                else if (outside && now >= duel.LeftAreaDeadline[i])
                    Finish(duel, Side(DuelEnd.ChallengerLeftArea, i == 1), first: p);
                else if (!outside && duel.LeftAreaDeadline[i] is not null)
                {
                    duel.LeftAreaDeadline[i] = null;
                    foreach (var o in Audience(p)) o.Session.Send(new DuelReEnteredAreaEvent { ddn = p.Id });
                }
            }
        }
    }

    /// <summary>
    /// The winner of a duel that ended with <paramref name="reason"/> (cdo.a 0x1808908A0, §6 V13): the challengee for 3, 5, 7, 8, 10,
    /// the challenger for 11, 13, 15, 16, 18; null (a draw) for all others.
    /// </summary>
    public static Player? Winner(Duel duel, byte reason) => reason switch
    {
        3 or 5 or 7 or 8 or 10 => duel.Challengee,
        11 or 13 or 15 or 16 or 18 => duel.Challenger,
        _ => null,
    };

    /// <summary>
    /// The end of <paramref name="duel"/> with <paramref name="reason"/>: DuelEnded to the observers of each duelist
    /// (<paramref name="first"/>'s first, else the challenger's), the post's Leave to every client that has it, the combat end of
    /// each duelist that is standing and fights no mob, and the wagers (UpdateMoney to each that gets silver).
    /// </summary>
    private void Finish(Duel duel, byte reason, Player? first)
    {
        if (!_duels.Remove(duel.Challenger)) return; // ended already
        _duels.Remove(duel.Challengee);
        var order = first == duel.Challengee ? new[] { duel.Challengee, duel.Challenger } : [duel.Challenger, duel.Challengee];
        foreach (var p in order)
        {
            var ended = new DuelEndedEvent { ddn = p.Id, b34 = reason, b35 = duel.Wager };
            foreach (var o in Audience(p)) o.Session.Send(ended);
        }
        var leave = new LeaveEvent { ObjectId = duel.PostId };
        foreach (var o in duel.PostKnown)
            if (o.Map == duel.Map) o.Session.Send(leave);
        foreach (var p in order)
            if (p.Combat.Down is null && p.Combat.InCombat && p.Combat.Enemies.Count == 0) Combat.EndCombat(p);
        var winner = Winner(duel, reason);
        // Stage I (pvp-goals SPEC §2.1, §2.4): a duel with a result (a winner, or the time run out) is a finished duel for both journals;
        // one won by knocking the opponent down (3/11) is the winner's PvP credit.
        if (world.Goals.Pvp.Enabled && (winner is not null || reason == (byte)DuelEnd.DuelDurationEnded))
            foreach (var p in order)
                if (p.Map is not null) AlbionJournal.DuelFinished(p, duel.Map);
        if (winner is not null && reason is (byte)DuelEnd.ChallengerKnockedDownByChallengee or (byte)DuelEnd.ChallengerKnockedDownByChallengee + ChallengeeOffset)
            PvpGoals.OnPvpKill(world, new PvpKill(PvpActor.Of(winner), PvpActor.Of(duel.Opponent(winner)), [], duel.Map, PvpOutcome.DuelWin, false, 0, world.GameNow, 1)
            {
                DuelSeconds = (world.GameNow - duel.Start) / (double)TimeSpan.TicksPerSecond,
            });
        // Stage N arena (SPEC §5, §6): a duel won by knockdown, the area or a disconnect moves the duel ladder and decides a tournament pair.
        if (winner is not null && reason is not ((byte)DuelEnd.OutsiderInterruptedDuel or (byte)DuelEnd.ChallengerKnockedDownByOutsider
                or (byte)DuelEnd.ChallengerKnockedDownByOutsider + ChallengeeOffset or (byte)DuelEnd.ChallengerKnockedDownByNonPlayer
                or (byte)DuelEnd.ChallengerKnockedDownByNonPlayer + ChallengeeOffset or (byte)DuelEnd.ChallengerKilledHimself or (byte)DuelEnd.ChallengerKilledHimself + ChallengeeOffset))
        {
            world.Ratings.DuelWon(winner, duel.Opponent(winner));
            world.Tournament.DuelWon(winner, duel.Opponent(winner));
        }
        if (duel.Wager > 0)
        {
            if (winner is not null) Wallet.Credit(winner, 2 * duel.Wager, "duel won");
            else
                foreach (var p in order) Wallet.Credit(p, duel.Wager, "duel wager back");
        }
        Log.Info("duel", $"duel of '{duel.Challenger.Character.Name}' and '{duel.Challengee.Character.Name}' ended: {(DuelEnd)(reason > 10 && reason < 19 ? reason - ChallengeeOffset : reason)}"
            + $"{(reason is > 10 and < 19 ? " (challengee)" : "")}, {(winner is null ? "a draw" : $"'{winner.Character.Name}' wins")}");
    }

    /// <summary>The challenge of <paramref name="a"/> lapses: DuelRequestCanceled {0 target, 1 challenger} to the target (closes its dialog).</summary>
    private static void Cancel(Player a, DuelChallenge c)
    {
        if (c.Target.Map is not null) c.Target.Session.Send(new DuelRequestCanceledEvent { ddn = c.Target.Id, b39 = a.Id });
    }

    /// <summary>A reason as the challenger's, made the challengee's when <paramref name="challengee"/>.</summary>
    private static byte Side(DuelEnd reason, bool challengee) => (byte)((byte)reason + (challengee ? ChallengeeOffset : 0));

    /// <summary>The player and every client that knows it (its observers).</summary>
    private static IEnumerable<Player> Audience(Player p) => p.Map is null ? p.Observers : p.Observers.Prepend(p);

    private static NewDuellingPostEvent PostEvent(Duel duel) => new() { cye = duel.PostId, cyf = duel.Post };

    /// <summary>DuelStarted {0 duelist, 1 opponent, 2 true only for the challenger, 3 start ticks, 4 end ticks}.</summary>
    private static DuelStartedEvent Started(Duel duel, Player p) =>
        new() { ddn = p.Id, b4a = duel.Opponent(p).Id, b4b = p == duel.Challenger, b4c = duel.Start, b4d = duel.End };

    private static float Distance(Player a, Player b)
    {
        var utc = DateTime.UtcNow;
        return Vector2.Distance(a.PositionAt(utc), b.PositionAt(utc));
    }
}
