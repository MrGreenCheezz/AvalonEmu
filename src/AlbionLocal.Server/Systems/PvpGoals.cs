using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A player-bot as one side of a PvP result (stage I bots): a stable persona id (the same after each respawn, so the anti-farming caps
/// hold), its name, flag, tier (1..8; T10 bots count as 8 and more) and object id.
/// </summary>
public interface IPvpBot
{
    string PersonaId { get; }
    string Name { get; }
    byte Flag { get; }
    int Tier { get; }
    long Id { get; }
}

/// <summary>One side of a PvP result: a real player (<see cref="Player"/>) or a player-bot (<see cref="Bot"/>), never both.</summary>
public readonly record struct PvpActor(Player? Player, IPvpBot? Bot)
{
    public static PvpActor Of(Player p) => new(p, null);

    public static PvpActor Of(IPvpBot b) => new(null, b);

    /// <summary>The anti-farming key: "c:" + the character id, "b:" + the bot's persona.</summary>
    public string Key => Player is { } p ? "c:" + p.Character.Id.ToString("N") : "b:" + Bot!.PersonaId;

    public Guid? Account => Player?.Character.Account;

    public byte Flag => Player?.Flag ?? Bot!.Flag;

    /// <summary>A player's: the highest tier of its equipped combat gear (1 when naked); a bot's own.</summary>
    public int Tier => Player is { } p ? PvpGoals.EquippedTier(p) : Bot!.Tier;

    public bool IsBot => Bot is not null;

    public string Name => Player?.Character.Name ?? Bot!.Name;
}

/// <summary>What a PvP result was: a death (a lethal zone, an execute), a non-lethal knockdown outside a duel, a duel won.</summary>
public enum PvpOutcome : byte
{
    Death,
    Knockdown,
    DuelWin,
}

/// <summary>
/// One PvP result for <see cref="PvpGoals.OnPvpKill"/>. <see cref="Assists"/>: the victim's attackers of the combat timeout with their
/// damage (<see cref="Death.Attackers"/>), the killer's own entry included (the kill credit, never an assist); empty for a duel.
/// <see cref="KillFame"/> the victim's kill fame before Rates, <see cref="Ticks"/> the GameTimeStamp, <see cref="Attackers"/> how many
/// attacked the victim (IfCombatHistoryAttackers). <see cref="FameShares"/>: the fame each sharer got (journal killplayerfame);
/// <see cref="DuelSeconds"/>: a duel's length since its start.
/// </summary>
public sealed record PvpKill(PvpActor Killer, PvpActor Victim, IReadOnlyList<(PvpActor Actor, double Damage)> Assists, Map Map, PvpOutcome Outcome,
    bool Crime, long KillFame, long Ticks, int Attackers)
{
    public IReadOnlyList<(Player Player, long Fame)> FameShares { get; init; } = [];

    public double DuelSeconds { get; init; }
}

/// <summary>
/// The PvP goals (stage I, analysis/work/stageI/pvp-goals/SPEC.md; settings <see cref="PvpGoalSettings"/>, content/goals.json Pvp). Every
/// PvP result goes through one hook, <see cref="OnPvpKill"/> (from <see cref="Death.Kill"/>, <see cref="Pvp.KnockedDown"/>, the duel's end
/// and the player-bots), which hands out credits: the killer's kill credit, an assist credit to each other player with at least
/// Pvp:AssistMinShare of the damage, and a fame-only one to each other fame sharer. A credit is refused (§2.2, nothing at all, the journal
/// included) for a crime (unless CrimesCount), the same party or the same account (not the empty or the local one, which every legacy and
/// self-test character shares), a victim below MinVictimTier, the same victim again within SameVictimCooldownMinutes or more than
/// SameVictimPerDay times a bounty day, a duel shorter than DuelMinSeconds or the same pair more than DuelSamePairPerDay times. A credit
/// gives Adventurer's Challenge points (§2.3: (KillPoints + KillPointsPerTier × tier, or DuelWinPoints) × the zone's factor by its
/// pvpwarningtype × BotFactor × AssistFactor × Season:PointsFactor, up to DailyPointsCap a day; 376 through <see cref="Seasons.Gain"/>),
/// the pvpkill / duel bounties (zone factor above 0), the journal's PvP leaves (<see cref="AlbionJournal.PvpCredited"/>) and the weekly guild
/// goals' pvpkills / pvpfame (<see cref="GuildGoals.OnPvpKill"/>). A bot
/// killer gets nothing (bots keep no goals); a player killed by a bot gets its journal "die" (<see cref="OnPvpDeath"/>). Touched only
/// under <see cref="World.Sync"/>.
/// </summary>
public static class PvpGoals
{
    private enum Role : byte { Kill, Assist, FameOnly }

    private static readonly long Day = TimeSpan.TicksPerDay;

    /// <summary>The equipment slots that make a victim's tier: the combat gear (mainhand, offhand, head, armor, shoes, cape), not the bag, mount, potion or food.</summary>
    private static readonly int[] GearSlots = [0, 1, 2, 3, 4, 6];

    /// <summary>
    /// The highest tier of <paramref name="p"/>'s equipped combat gear (<see cref="GearSlots"/>), 1 when it wears none: a naked alt on a
    /// T8 mount or with T8 food stays below Pvp:MinVictimTier.
    /// </summary>
    public static int EquippedTier(Player p) =>
        Math.Max(1, GearSlots.Select(s => p.Inventory.Equipment[s]?.Template.Tier ?? 1).Max());

    /// <summary>The bounty day of GameTimeStamp <paramref name="ticks"/> (yyyyMMdd of <see cref="Bounties.DayStart"/>).</summary>
    private static string DayOf(World world, long ticks) =>
        world.Bounties.DayStart(new DateTime(ticks, DateTimeKind.Utc)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>The character's goals with <see cref="GoalLog.PvpDay"/> brought to <paramref name="day"/> (a new day: no points, no refusals).</summary>
    private static GoalLog LogOf(Player p, string day)
    {
        var g = p.Character.Goals ??= new GoalLog();
        if (g.PvpDay != day)
        {
            (g.PvpDay, g.PvpDayPoints) = (day, 0);
            g.PvpRefused.Clear();
        }
        return g;
    }

    /// <summary>
    /// Two accounts that are really one person's: the same, but neither empty nor the repository's local account while no login has
    /// claimed it (the legacy and self-test characters share it). Once claimed, the local account is its owner's like any other.
    /// </summary>
    private static bool SameAccount(World world, Guid? a, Guid? b) =>
        a is { } x && b is { } y && x == y && x != Guid.Empty
        && (x != Account.LocalId || world.Characters.FindAccount(x) is { Name: not null });

    /// <summary>
    /// Why <paramref name="receiver"/> gets no credit for <paramref name="k"/> (§2.2 rules 1-4 and 6; null = it does). Reads only: the
    /// knockdown's fame (Pvp:KnockdownFameFactor) asks it before <see cref="OnPvpKill"/> records anything.
    /// </summary>
    public static string? Refusal(World world, PvpKill k, Player receiver)
    {
        var s = world.Goals.Pvp;
        if (k.Crime && !s.CrimesCount) return "crime";
        if (k.Victim.Player is { } victim)
        {
            if (victim == receiver) return "self";
            // A duel is Hostile inside a party by design (Pvp.Of): friends duel, so the party rule leaves duels out. TODO (guilds SPEC):
            // the same guild, once guilds exist.
            if (k.Outcome != PvpOutcome.DuelWin && Pvp.SameParty(receiver, victim)) return "same party";
            if (SameAccount(world, receiver.Character.Account, k.Victim.Account)) return "same account";
        }
        if (k.Victim.Tier < s.MinVictimTier) return "victim tier";
        var g = receiver.Character.Goals;
        var dayStart = world.Bounties.DayStart(new DateTime(k.Ticks, DateTimeKind.Utc)).Ticks;
        if (k.Outcome == PvpOutcome.DuelWin)
        {
            // Rule 6 instead of rule 4: a pair may duel again at once, but credits only so often a day.
            if (k.DuelSeconds < s.DuelMinSeconds) return "duel too short";
            if (g?.PvpDuels.GetValueOrDefault(PairKey(k)) is { } wins && wins.Count(t => t >= dayStart) >= s.DuelSamePairPerDay) return "same duel pair today";
            return null;
        }
        if (g?.PvpVictims.GetValueOrDefault(k.Victim.Key) is { Count: > 0 } times)
        {
            if (k.Ticks - times.Max() < TimeSpan.FromMinutes(s.SameVictimCooldownMinutes).Ticks) return "same victim cooldown";
            if (times.Count(t => t >= dayStart) >= s.SameVictimPerDay) return "same victim today";
        }
        return null;
    }

    private static string PairKey(PvpKill k)
    {
        var (a, b) = (k.Killer.Key, k.Victim.Key);
        return string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
    }

    /// <summary>The credit of <paramref name="k"/> for <paramref name="g"/>'s owner kept, the entries older than a day dropped.</summary>
    private static void Record(GoalLog g, PvpKill k)
    {
        var log = k.Outcome == PvpOutcome.DuelWin ? g.PvpDuels : g.PvpVictims;
        var key = k.Outcome == PvpOutcome.DuelWin ? PairKey(k) : k.Victim.Key;
        foreach (var (name, list) in log.ToList())
        {
            list.RemoveAll(t => k.Ticks - t >= Day);
            if (list.Count == 0) log.Remove(name);
        }
        if (!log.TryGetValue(key, out var times)) log[key] = times = [];
        times.Add(k.Ticks);
    }

    /// <summary>
    /// The one entry for every PvP result (see the class): the credits judged and recorded, then for each its season points, bounties
    /// and journal leaves. Nothing with Pvp:Enabled off or a bot killer. A knockdown and the death that follows it while the victim is
    /// still down (an execute) are one result: the receivers credited for the knockdown (<see cref="PlayerPvp.DownCredits"/>) get at the
    /// death only what the knockdown did not give (journal killplayer without Pvp:KnockdownIsKill, the points when it gave none, the
    /// death's fame), no new refusal or record; anyone else (a third player's execute) gets nothing for it ("knockdown credited").
    /// </summary>
    public static void OnPvpKill(World world, PvpKill k)
    {
        var s = world.Goals.Pvp;
        if (!s.Enabled || k.Killer.Player is not { } killer) return;
        var receivers = new List<(Player Player, Role Role)> { (killer, Role.Kill) };
        var total = k.Assists.Sum(a => a.Damage);
        if (k.Outcome != PvpOutcome.DuelWin && total > 0)
            foreach (var (actor, damage) in k.Assists)
                if (actor.Player is { } p && p != killer && damage >= s.AssistMinShare * total && receivers.All(r => r.Player != p)) receivers.Add((p, Role.Assist));
        foreach (var (p, _) in k.FameShares)
            if (receivers.All(r => r.Player != p)) receivers.Add((p, Role.FameOnly));
        var day = DayOf(world, k.Ticks);
        var warning = Pvp.RulesOf(world, k.Map).WarningType;
        var zone = k.Outcome == PvpOutcome.DuelWin ? 1 : s.ZoneFactor(warning);
        var down = k.Outcome == PvpOutcome.Death ? k.Victim.Player?.PvpState.DownCredits : null;
        var credited = new Dictionary<Player, bool>();
        foreach (var (p, role) in receivers)
        {
            var g = LogOf(p, day);
            world.Saves.MarkChanged(p.Character);
            var fame = k.FameShares.FirstOrDefault(f => f.Player == p) is { Fame: > 0 } share ? share.Fame : 0;
            if (down is not null && down.TryGetValue(p, out var hadPoints))
            {
                // The rest of the knockdown's result: no refusal or record again, its bounties and spree were counted.
                if (fame > 0)
                {
                    AlbionJournal.PvpFame(p, k, fame);
                    world.GuildGoals.OnPvpFame(p, k, zone, fame);
                }
                if (role == Role.FameOnly) continue;
                if (!hadPoints) Points(world, p, g, k, role == Role.Assist, zone, warning);
                if (!s.KnockdownIsKill) AlbionJournal.PvpKillOnly(p, k, role == Role.Assist);
                continue;
            }
            if (down is not null && role != Role.FameOnly)
            {
                Refused(p, g, k, role, "knockdown credited");
                continue;
            }
            if (Refusal(world, k, p) is { } why)
            {
                Refused(p, g, k, role, why);
                continue;
            }
            if (fame > 0)
            {
                AlbionJournal.PvpFame(p, k, fame);
                world.GuildGoals.OnPvpFame(p, k, zone, fame); // stage I: the guild goals count only credited fame
            }
            // A fame sharer (neither killer nor assist) keeps no record: its own kill of this victim later is not refused for it.
            if (role == Role.FameOnly) continue;
            Record(g, k);
            var assist = role == Role.Assist;
            credited[p] = Points(world, p, g, k, assist, zone, warning);
            world.Bounties.PvpCredited(p, k, assist, zone, warning);
            AlbionJournal.PvpCredited(p, k, assist);
            world.GuildGoals.OnPvpKill(p, k, assist, zone); // stage I: the weekly guild goals' pvpkills
        }
        if (k.Outcome == PvpOutcome.Knockdown && credited.Count > 0 && k.Victim.Player is { } victim) victim.PvpState.DownCredits = credited;
    }

    private static void Refused(Player p, GoalLog g, PvpKill k, Role role, string why)
    {
        g.PvpRefused[why] = g.PvpRefused.GetValueOrDefault(why) + 1;
        Log.Debug("pvp", $"pvp-goals: no credit for '{p.Character.Name}' ({role}) on {k.Victim.Name}: {why}");
    }

    /// <summary>The season points of one credit (§2.3), up to the day's cap; a chat line with Pvp:Announce. True when it gave any.</summary>
    private static bool Points(World world, Player p, GoalLog g, PvpKill k, bool assist, double zone, string warning)
    {
        var s = world.Goals.Pvp;
        var basis = k.Outcome == PvpOutcome.DuelWin ? s.DuelWinPoints : s.KillPoints + s.KillPointsPerTier * k.Victim.Tier;
        var points = basis * zone * (k.Victim.IsBot ? s.BotFactor : 1) * (assist ? s.AssistFactor : 1) * world.Goals.Season.PointsFactor;
        var raw = Math.Min((long)Math.Round(points * 10000), (long)Math.Round(s.DailyPointsCap * 10000) - g.PvpDayPoints);
        if (raw <= 0 || Seasons.Current(world, out _) is null) return false;
        g.PvpDayPoints += raw;
        Seasons.Gain(p, raw);
        if (s.Announce)
            Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture, $"PvP: +{raw / 10000.0:0.##} challenge points (")
                + (k.Outcome == PvpOutcome.DuelWin ? $"duel won against {k.Victim.Name})" : $"{k.Victim.Name}, T{k.Victim.Tier}, {warning} zone{(assist ? ", assist" : "")})"));
        return true;
    }

    /// <summary>A player who died to another player (<paramref name="byBot"/>: to a player-bot): the journal's die where=pvp.</summary>
    public static void OnPvpDeath(Player victim, Map map, bool byBot)
    {
        if (!victim.World.Goals.Pvp.Enabled) return;
        AlbionJournal.Count(victim, new JournalEvent("die", 1) { Map = map, Where = "pvp", Bot = byBot });
    }

    /// <summary>.bounty's PvP line: today's PvP season points and cap, the credits refused today by reason.</summary>
    public static string Describe(Player p)
    {
        var world = p.World;
        var s = world.Goals.Pvp;
        if (!s.Enabled) return "PvP goals: off";
        var g = p.Character.Goals;
        var today = g?.PvpDay == DayOf(world, world.GameNow);
        var points = today ? g!.PvpDayPoints : 0;
        var refused = today && g!.PvpRefused.Count > 0 ? "; refused today: " + string.Join(", ", g.PvpRefused.Select(r => $"{r.Key} {r.Value}")) : "";
        return string.Create(CultureInfo.InvariantCulture, $"PvP goals: {points / 10000.0:0.##}/{s.DailyPointsCap:0.##} challenge points today{refused}");
    }
}
