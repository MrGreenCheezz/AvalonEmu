using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Territories
{
    /// <summary>
    /// Whether the installed season permits territorial battles at this instant. This is distinct from claiming a free tower:
    /// seasontimes enables territorybattlestart on day 3/31 and disables it at the reset/end. The current client's requirement
    /// f7a (345319 RVA 1C57380 -> dd7.q AC5280) checks the window both now and at the proposed battle time.
    /// </summary>
    private bool BattlesAllowed(TerritoryState state, DateTime utc)
    {
        if (!S.SeasonSchedule) return true;
        var season = world.GuildGoals.Season;
        if (utc < season.Start || utc >= season.End) return false;
        if (season.Schedule.Count == 0) return true;
        var allowed = false;
        foreach (var (action, _) in Events(state, utc))
            if (action.Type == "territorybattlestart") allowed = true;
            else if (action.Type == "territorybattleend") allowed = false;
        return allowed;
    }

    /// <summary>
    /// The next potential attack slot, not a scheduled match. Local scheduling policy: the first regional prime-time start at
    /// least OpenWorldAttackSettings.MinTimeUntilNextOpenWorldAttackInMinutes from now; both instants must be in a battle window.
    /// The notice and daily region times come from the installed data; this is not a claim to reproduce the official scheduler.
    /// Unknown regions or invalid data return no time rather than inventing a date. No owner, currency or battle state changes.
    /// </summary>
    public DateTime? NextAttackTime(TerritoryState state, DateTime utc)
    {
        if (!Enabled || !S.Attacks || state.Territory.IsCastle || !Rules.TimeRegions.TryGetValue(state.TimeRegion, out var region)
            || !BattlesAllowed(state, utc)) return null;
        var minutes = Rules.MinimumAttackNoticeMinutes;
        if (!double.IsFinite(minutes) || minutes < 0 || !double.IsFinite(S.PrimeTimeOffsetHours)) return null;
        // Configuration is bounded by ServerConfig; still reject invalid/out-of-range values if this World was built directly.
        var noticeTicks = minutes * TimeSpan.TicksPerMinute;
        var offsetTicks = S.PrimeTimeOffsetHours * TimeSpan.TicksPerHour;
        if (noticeTicks > DateTime.MaxValue.Ticks - utc.Ticks || Math.Abs(offsetTicks) > TimeSpan.MaxValue.Ticks) return null;
        var earliest = utc.AddTicks((long)Math.Ceiling(noticeTicks));
        var dayTicks = TimeSpan.TicksPerDay;
        var startTicks = (region.Start.Ticks % dayTicks + (long)(offsetTicks % dayTicks)) % dayTicks;
        if (startTicks < 0) startTicks += dayTicks;
        if (startTicks > DateTime.MaxValue.Ticks - earliest.Date.Ticks) return null;
        var candidate = DateTime.SpecifyKind(earliest.Date.AddTicks(startTicks), DateTimeKind.Utc);
        if (candidate < earliest)
        {
            if (candidate.Ticks > DateTime.MaxValue.Ticks - dayTicks) return null;
            candidate = candidate.AddDays(1);
        }
        return BattlesAllowed(state, candidate) ? candidate : null;
    }

    /// <summary>
    /// 412: only an existing monolith on the requester's loaded map. Installed 345319 sender A6D110 supplies the entity id;
    /// cln.af B544E0 reads response key 1 into +178, then derives preparation time in +180. It ignores string key 0.
    /// A nonzero return code takes that consumer's unavailable-time path. This query does not create an attack or bypass
    /// the missing declaration/conquest authority (407-411); GetMatches remains empty until real matches can be scheduled.
    /// </summary>
    public GetNextOpenWorldAttackScheduleTimeResponse NextAttackSchedule(Player player, long objectId)
    {
        if (player.Map is not { Unloaded: false } map || !map.Objects.TryGetValue(objectId, out var obj)
            || obj is not MonolithObject monolith || NextAttackTime(monolith.State, UtcNow) is not { } time)
            return new() { ReturnCode = Refused, a2b = "" };
        return new() { a2b = "", a2c = time.Ticks };
    }
}
