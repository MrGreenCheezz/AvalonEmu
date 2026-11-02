using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public static partial class PvpChallenge
{
    // 345319 1133973: 18241/18281 = already claimed; 18242/18282 = no inventory slot.
    private const short SeasonClaimed = 18241, SeasonNoSpace = 18242, LootClaimed = 18281, LootNoSpace = 18282;

    /// <summary>454 decoder 345319 A53541/A5367A. Key 6 is all earned display items; key 7 is the unclaimed
    /// subset (1135806 inverts Contains before SetLockAndClaimed). No fabricated rows for earlier seasons.</summary>
    public static GetPvpChallengeSeasonRewardsResponse SeasonRewards(Player p)
    {
        if (!FinalizeRewards(p)) return EmptyRewards(1);
        var rows = LogOf(p).Rewards.OrderBy(r => p.World.GvgSeasons?.Seasons.FirstOrDefault(s => s.Key == r.Key)?.Start).ToArray();
        return new()
        {
            a3l = [.. rows.Select(r => r.Key)], a3m = [.. rows.Select(r => r.Value.Might)],
            a3n = [.. rows.Select(r => r.Value.Guild)], a3o = [.. rows.Select(r => r.Value.GuildName)],
            a3p = [.. rows.Select(r => r.Value.Bracket)], a3q = [.. rows.Select(r => r.Value.Rank)],
            a3r = [.. rows.Select(r => r.Value.Items)],
            a3s = [.. rows.Select(r => r.Value.Items.Where(i => !r.Value.Claimed.Contains(i)).ToArray())],
        };
    }

    public static GetPvpChallengeSeasonRewardsResponse EmptyRewards(short code = 0) => new()
        { ReturnCode = code, a3l = [], a3m = [], a3n = [], a3o = [], a3p = [], a3q = [], a3r = [], a3s = [] };

    /// <summary>455 is the remaining contents of a previously opened season chest, not another loot roll.</summary>
    public static GetPvpChallengeSeasonRewardItemsResponse SeasonItems(Player p, string? key)
    {
        var response = new GetPvpChallengeSeasonRewardItemsResponse { a3i = key ?? "", a3j = [], a3k = [] };
        if (key is null || !FinalizeRewards(p)) { response.ReturnCode = 1; return response; }
        if (!LogOf(p).Rewards.TryGetValue(key, out var row)) return response;
        var items = row.Pending.Where(i => i.Value > 0).OrderBy(i => i.Key, StringComparer.Ordinal).ToArray();
        response.a3j = [.. items.Select(i => i.Key)];
        response.a3k = [.. items.Select(i => i.Value)];
        return response;
    }

    private static bool FinalizeRewards(Player p)
    {
        var log = LogOf(p);
        var ended = p.World.GvgSeasons?.Seasons.Where(s => s.End.Ticks <= p.World.GameNow
            && log.Seasons.ContainsKey(s.Key) && !log.Rewards.ContainsKey(s.Key) && Data(p.World).Seasons.ContainsKey(s.Key)).ToArray() ?? [];
        if (ended.Length == 0) return true;
        var snapshot = p.Character.Clone();
        foreach (var season in ended)
        {
            var row = new PersonalMightRewards { Might = Total(log.Seasons[season.Key].Values) };
            // A retained uninterrupted membership has an authoritative Joined timestamp. Former memberships
            // predating this feature have no ledger and must not be guessed from activity share or fame.
            // We retain this final selection so changing guild later cannot upgrade/re-roll an old reward.
            if (p.World.Guilds.Of(p.Character.Id) is { } guild && guild.Find(p.Character.Id) is { } member
                && member.Joined < season.End && guild.Progress.Seasons.FirstOrDefault(s => s.Season == season.Key) is { } state)
            {
                row.Guild = guild.Id;
                row.GuildName = guild.Name;
                var bracket = season.BracketOf(state.Points);
                row.Bracket = (byte)(SeasonBracket.RankOf(bracket?.Name) + 1);
                var ranks = p.World.Guilds.All.Select(g => (Guild: g, State: g.Progress.Seasons.FirstOrDefault(s => s.Season == season.Key)))
                    .Where(r => r.State is { Points: > 0 }).OrderByDescending(r => r.State!.Points).ThenBy(r => r.State!.PointsAt).ToArray();
                var rank = Array.FindIndex(ranks, r => r.Guild.Id == guild.Id);
                row.Rank = rank < 0 ? -1 : rank + 1;
                row.Participation = (season.End - (member.Joined > season.Start ? member.Joined : season.Start)).Ticks
                    >= (season.End - season.Start).Ticks * season.MinimumParticipation;
            }
            row.Items = [.. SelectedRewards(Data(p.World).Seasons[season.Key], row).Select(r => r.Item).Distinct(StringComparer.Ordinal)];
            snapshot.Goals!.Conqueror!.Rewards[season.Key] = row;
        }
        return CommitReward(p, snapshot, [], SeasonNoSpace) == 0;
    }

    /// <summary>The native upgrade lines select the highest reached row per nonzero line (bq5; current 65E680
    /// selects the bracket/rank override). Rank -1 in XML means the bracket's remaining ranks.</summary>
    private static IEnumerable<PvpChallengeData.Reward> SelectedRewards(IReadOnlyList<PvpChallengeData.SeasonReward> all, PersonalMightRewards row)
    {
        var reached = all.Where(r => r.Might <= row.Might).ToArray();
        foreach (var reward in reached.Where(r => r.Line == 0).Concat(reached.Where(r => r.Line != 0).GroupBy(r => r.Line).Select(g => g.MaxBy(r => r.Might)!)))
        {
            var bracket = row.Bracket is >= 1 and <= 5 ? SeasonBracket.Order[row.Bracket - 1] : "";
            var alternate = row.Rank <= 0 ? null : reward.Overrides.Where(o => o.Bracket == bracket && (!o.Participation || row.Participation)
                && (o.Rank < 0 || row.Rank <= o.Rank)).OrderBy(o => o.Rank < 0 ? int.MaxValue : o.Rank).FirstOrDefault();
            yield return alternate?.Reward ?? reward.Reward;
        }
    }

    private static bool CanClaim(Player p, PvpChallengeData.Reward reward) =>
        reward.NonPremium && !Premium.Is(p) || reward.Premium && Premium.Is(p) || reward.BoughtPremium && BoughtPremium(p);

    /// <summary>456 grants direct reward objects, or opens the original loot-list reward into a durable pending
    /// ledger. A chest is not also granted as an inventory item: that would roll it a second time on consumption.</summary>
    public static short ClaimSeason(Player p, string? key, string? item)
    {
        if (OpeningTutorial.Active(p) || key is null || !FinalizeRewards(p)) return 1;
        if (!LogOf(p).Rewards.TryGetValue(key, out var live) || !Data(p.World).Seasons.TryGetValue(key, out var data)) return 1;
        if (!string.IsNullOrEmpty(item) && !live.Items.Contains(item, StringComparer.Ordinal)) return 1;
        var selection = SelectedRewards(data, live).Where(r => (string.IsNullOrEmpty(item) || r.Item == item) && !live.Claimed.Contains(r.Item)).ToArray();
        if (selection.Length == 0) return SeasonClaimed;
        if (selection.Any(r => !CanClaim(p, r))) return 1;
        var snapshot = p.Character.Clone();
        var row = snapshot.Goals!.Conqueror!.Rewards[key];
        var give = new List<(ItemTemplate Type, int Count)>();
        foreach (var reward in selection)
        {
            if (reward.Loot is { Length: > 0 } loot)
            {
                if (p.World.Clusters?.LootIfLoaded is not { } tables || !tables.Has(loot)) return 1;
                // Never discard an unsupported item from the roll. The whole transaction remains unclaimed.
                // Once committed, 455/457 read these exact amounts; no refresh or retry rolls again.
                foreach (var (name, count) in tables.RollList(loot, p.World.CraftRandom))
                {
                    if (count <= 0 || p.World.Items.Find(name) is not { CanCreate: true }
                        || row.Pending.GetValueOrDefault(name) > int.MaxValue - count)
                    {
                        Log.Warn("conqueror", $"Season reward {loot} cannot deliver {count} x {name}; claim retained");
                        return 1;
                    }
                    row.Pending[name] = row.Pending.GetValueOrDefault(name) + count;
                }
            }
            else
            {
                if (p.World.Items.Find(reward.Item) is not { CanCreate: true } type) return 1;
                give.Add((type, 1));
            }
            row.Claimed.Add(reward.Item);
        }
        return CommitReward(p, snapshot, give, SeasonNoSpace);
    }

    /// <summary>457 retires only requested positive amounts from the saved chest ledger. Client counts are
    /// Int16, but the persisted/455 amounts are Int32. Each partial withdrawal and its inventory commit together.</summary>
    public static short ClaimSeasonItems(Player p, string? key, string[]? names, short[]? counts, string? request)
    {
        if (OpeningTutorial.Active(p) || key is null || string.IsNullOrEmpty(request) || request.Length > 160
            || names is null || counts is null || names.Length == 0
            || names.Length != counts.Length || names.Length > 256 || names.Any(string.IsNullOrEmpty)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length || counts.Any(n => n <= 0)) return 1;
        if (!LogOf(p).Rewards.TryGetValue(key, out var live)) return LootClaimed;
        if (live.Withdrawals.Contains(request)) return LootClaimed;
        var snapshot = p.Character.Clone();
        var row = snapshot.Goals!.Conqueror!.Rewards[key];
        row.Withdrawals.Add(request);
        var give = new List<(ItemTemplate Type, int Count)>();
        for (var i = 0; i < names.Length; i++)
        {
            var available = live.Pending.GetValueOrDefault(names[i]);
            if (available < counts[i]) return LootClaimed;
            if (p.World.Items.Find(names[i]) is not { CanCreate: true } type) return 1;
            give.Add((type, counts[i]));
            if (available == counts[i]) row.Pending.Remove(names[i]);
            else row.Pending[names[i]] = available - counts[i];
        }
        return CommitReward(p, snapshot, give, LootNoSpace);
    }
}
