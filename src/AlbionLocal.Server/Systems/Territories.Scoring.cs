using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Phase P4 of the territories (analysis/work/stageP/territories/SPEC.md §3.1, §6 P4): the season points of the held watchtowers and their tower
/// power points.
/// <list type="bullet">
/// <item>Scoring: every Territories:ScoreTickMinutes (60, a placeholder: capture T6) a held watchtower pays its owner guild the base points of its
/// cluster type and tier (territorytypes PointsSettings: OPENPVP_BLACK_1 60 … _6 160) × the season's scoremultiplier (2 from day 29 in SEASON_34)
/// × Territories:SeasonPointsFactor, while the season's territoryscoringstart … territoryscoringend window of its timeregion runs (always with
/// Territories:SeasonSchedule off), activity TerritoryHold; a tick in the zone's prime time pays Territories:PrimeTimeBonus × as much again,
/// activity TerritoryPrimeTime (@SEASON_POINT_BREAKDOWN_TERRITORYHOLD / _TERRITORYPRIMETIME). Into the guild season's standings
/// (<see cref="GuildGoals.AddActivityPoints"/>: the Overview rows, the rankings and brackets). The NPC holder scores nothing.</item>
/// <item>Tower power points [guess, capture T1]: the levels' powerpointspool stacked (level 2 from 9613 points, …, level 8 from the sum of
/// the first seven); the points decay by the current level's pool over its decaydays (2 … 4.8 days), level by level down to 0.
/// TowerPowerPointUpdate (433 {0 the points, a7m, 1 their time}) to the zone's players when they are set; the client's bar decays them itself
/// (bgs.l with the tower levels). Nothing raises them yet but the GM (power crystals: a later phase).</item>
/// </list>
/// </summary>
public sealed partial class Territories
{
    /// <summary>The tower power points of <paramref name="s"/> at <paramref name="utc"/>: <see cref="TerritoryState.Power"/> decayed since its time.</summary>
    public double PowerAt(TerritoryState s, DateTime utc)
    {
        var rows = Rules.TowerLevelRows;
        var p = s.Power;
        if (p <= 0 || s.PowerAt == default || utc <= s.PowerAt || rows.Count == 0) return Math.Max(0, p);
        var left = (utc - s.PowerAt).TotalDays;
        var level = Math.Min(LevelOf(p), rows.Count);
        while (left > 0 && p > 0)
        {
            var row = rows[level - 1];
            var rate = row.Pool / Math.Max(0.01, row.DecayDays);
            var floor = FloorOf(level);
            var days = rate <= 0 ? double.MaxValue : (p - floor) / rate;
            if (days >= left)
            {
                p -= rate * left;
                break;
            }
            left -= days;
            p = floor;
            if (level == 1) break;
            level--;
        }
        return Math.Max(0, p);
    }

    /// <summary>The power points tower level <paramref name="level"/> starts at: the pools of the levels below it.</summary>
    private double FloorOf(int level) => Rules.TowerLevelRows.Take(Math.Max(0, level - 1)).Sum(r => r.Pool);

    /// <summary>The tower level of <paramref name="power"/> points [guess]: 1, and one more for each level's pool the points reach (the pools stacked), at most the last level.</summary>
    public int LevelOf(double power)
    {
        var rows = Rules.TowerLevelRows;
        var level = 1;
        for (var l = 2; l <= rows.Count; l++)
            if (power >= FloorOf(l)) level = l;
        return level;
    }

    /// <summary>The tower level of <paramref name="s"/> at <paramref name="utc"/>.</summary>
    public int TowerLevelAt(TerritoryState s, DateTime utc) => LevelOf(PowerAt(s, utc));

    /// <summary>The tower power points of <paramref name="s"/> set to <paramref name="power"/> at <paramref name="utc"/>, saved; TowerPowerPointUpdate (433) to the zone's players.</summary>
    public void SetPower(TerritoryState s, double power, DateTime utc)
    {
        (s.Power, s.PowerAt) = (power, utc);
        Save(s);
        if (MonolithOf(s) is { Map: { } map })
        {
            var e = new TowerPowerPointUpdateEvent { d1t = (long)Math.Round(power * Pricing.A7m), d1u = world.GameNow };
            foreach (var p in map.Players.Values) p.Session.Send(e);
        }
    }

    // ---- scoring

    /// <summary>Whether the season's territory scoring runs for <paramref name="s"/> at <paramref name="utc"/> (its last territoryscoringstart / end; always with the schedule off).</summary>
    public bool Scoring(TerritoryState s, DateTime utc)
    {
        if (!S.SeasonSchedule || world.GuildGoals.Season.Schedule.Count == 0) return true;
        var on = false;
        foreach (var (a, _) in Events(s, utc))
            if (a.Type == "territoryscoringstart") on = true;
            else if (a.Type == "territoryscoringend") on = false;
        return on;
    }

    /// <summary>The season's score multiplier at <paramref name="utc"/> (its last scoremultiplier newmultiplier; 1 before one and with the schedule off).</summary>
    public double ScoreMultiplier(TerritoryState s, DateTime utc)
    {
        var m = 1.0;
        foreach (var (a, _) in Events(s, utc))
            if (a.Type == "scoremultiplier" && a.Attrs.TryGetValue("newmultiplier", out var v)
                && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && x >= 0) m = x;
        return m;
    }

    /// <summary>The base points of <paramref name="s"/> per tick: PointsSettings of its cluster type and tier (0 = none in the data).</summary>
    public int BasePoints(TerritoryState s) => Rules.PointsOf(s.Cluster.Type, ClusterName.Parse(s.Cluster.File).Tier ?? 0);

    /// <summary>The held watchtowers' scoring ticks since their last (the first counts from the first tick after a claim or a start).</summary>
    private void ScoreTowers(DateTime now)
    {
        var tick = TimeSpan.FromMinutes(S.ScoreTickMinutes);
        foreach (var s in All)
        {
            if (s.Territory.IsCastle || !s.Owned || s.NpcHeld || world.Guilds.ById(s.Owner) is not { } g) continue;
            var last = s.LastScore ??= now;
            var n = (int)Math.Min(10_000, Math.Floor((now - last) / tick));
            if (n <= 0) continue;
            s.LastScore = last + tick * n;
            var basePoints = BasePoints(s);
            double hold = 0, prime = 0;
            for (var i = 1; i <= n; i++)
            {
                var t = last + tick * i;
                if (basePoints <= 0 || !Scoring(s, t)) continue;
                var points = basePoints * ScoreMultiplier(s, t) * S.SeasonPointsFactor;
                hold += points;
                if (InPrimeTime(s, t)) prime += points * S.PrimeTimeBonus;
            }
            var (h, p) = ((long)Math.Round(hold), (long)Math.Round(prime));
            if (h > 0) world.GuildGoals.AddActivityPoints(g, h, GuildGoals.TerritoryHold);
            if (p > 0) world.GuildGoals.AddActivityPoints(g, p, GuildGoals.TerritoryPrimeTime);
            if (h + p > 0) Log.Info("guild", $"territory {s.Territory.Name}: +{h} season points (+{p} prime time) for '{g.Name}' ({n} tick(s))");
        }
    }
}
