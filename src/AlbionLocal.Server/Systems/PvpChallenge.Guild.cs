using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public static partial class PvpChallenge
{
    /// <summary>Called on an actual season-point increase, never first load/join or a GM point grant. Native
    /// bracket objectives are seasononly: their one-off Might does not complete the weekly challenge and does
    /// not feed guild Might back into another season-point gain. The personal marker prevents guild hopping
    /// or a replay from granting a bracket twice in one season.</summary>
    public static void GuildPoints(World world, Guild guild, string key, long before, long after)
    {
        if (world.GvgSeasons?.Seasons.FirstOrDefault(s => s.Key == key) is not { } season
            || season.End.Ticks <= world.GameNow || season.Start.Ticks > world.GameNow) return;
        var data = Data(world);
        if (data.WeeklyRequirement <= 0) return;
        var notices = new List<(Player Player, short Source, long Amount, byte Bracket)>();
        foreach (var bracket in season.Brackets.Where(b => before < b.Threshold && after >= b.Threshold).OrderBy(b => b.Threshold))
        {
            if (bracket.MightReward is not { } reward || !data.Objectives.TryGetValue(reward, out var rule) || !rule.SeasonOnly) continue;
            var amount = PvpChallengeData.Raw(rule.Might);
            if (amount <= 0 || rule.Stat(season) is not { Length: > 0 } stat) continue;
            foreach (var member in guild.Members)
            {
                var online = world.Parties.Online(member.Id);
                var c = online?.Character ?? world.LoadCharacter(member.Id);
                if (c is null || c.Transient) continue;
                var goals = c.Goals ??= new();
                var log = goals.Conqueror ??= new() { CoveredSinceUtc = world.GameNow };
                if (!log.BracketRewards.TryGetValue(key, out var paid)) log.BracketRewards[key] = paid = new(StringComparer.Ordinal);
                if (!paid.Add(bracket.Name)) continue;
                if (online is not null)
                {
                    // Gain saves the marker and the increment in the same normal character snapshot, before
                    // emitting event 496's dedicated bracket-level popup (keys 8/9).
                    Gain(online, rule, amount, 0, season, send: false);
                    notices.Add((online, rule.Id, amount, (byte)(SeasonBracket.RankOf(bracket.Name) + 1)));
                }
                else
                {
                    if (!log.Seasons.TryGetValue(key, out var values)) log.Seasons[key] = values = new(StringComparer.Ordinal);
                    values[stat] = Add(values.GetValueOrDefault(stat), amount);
                    // Queue the complete offline character immediately. SaveScheduler.Load sees this latest
                    // snapshot even before the writer finishes, so another bracket cannot overwrite it.
                    world.Saves.SaveNow(c);
                }
                Log.Info("conqueror", $"'{c.Name}': {key} {bracket.Name} bracket, +{amount / 10000.0} personal Might");
            }
        }
        // Every member's counter/marker is installed before the first transport callback can throw.
        foreach (var (player, source, amount, bracket) in notices)
            player.Session.Send(new MightAndFavorReceivedEventEvent
                { cqt = source, cqu = amount, cq1 = true, cq2 = bracket, cq3 = [], cq4 = [] });
    }
}
