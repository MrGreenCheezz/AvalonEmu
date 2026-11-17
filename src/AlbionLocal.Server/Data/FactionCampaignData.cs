using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// factionwarfare.xml &lt;factioncampaign&gt; (analysis/work/stageR/fw-campaign/SPEC.md §1, "SPEC" here): the daily rewards (three standing rewards at
/// 1250 / 2500 / 5000 faction points of a day) and the campaign rewards (four faction chests at 25000 / 50000 / 75000 / 100000 points of the
/// month, unlocked on day 0 / 7 / 14 / 21 of the campaign). The client parses the same file (bcg.f, the two tracks: bcg+0x260 daily, +0x268
/// campaign) and indexes GetFactionWarfareCampaignData by the reward ids. Immutable.
/// </summary>
public sealed class FactionCampaignData
{
    /// <summary>
    /// One &lt;rewardtrigger&gt;: its first reward (the client reads rewards[0] only), the points (whole) it needs and the campaign day from which
    /// it counts (unlockday; the daily ones 0).
    /// </summary>
    public sealed record Trigger(string Id, int UnlockDay, long PointRequirement, Reward Reward);

    /// <summary>
    /// A &lt;standingreward&gt; (<see cref="Amount"/> standing) or an &lt;itemreward&gt; (one item per faction, <see cref="ItemFor"/>) with the
    /// client's premium flags (bcg.e.m 0x1805c1d90: nonpremium without premium, premium with premium, boughtpremium with premium of more than 6 days).
    /// </summary>
    public sealed record Reward(string Id, string Kind, long Amount, IReadOnlyList<string> Items, bool BoughtPremium, bool Premium, bool NonPremium)
    {
        /// <summary>The item of FlaggingStatus <paramref name="faction"/> (1 Highland … 6 Caerleon); null for another or none.</summary>
        public string? ItemFor(byte faction) => faction is >= 1 and <= 6 && Items[faction] is { Length: > 0 } item ? item : null;

        /// <summary>bcg.e.m: whether a character of this premium state may claim it.</summary>
        public bool Eligible(bool isPremium, int premiumDays) =>
            (NonPremium && !isPremium) || (Premium && isPremium) || (BoughtPremium && isPremium && premiumDays > 6);
    }

    /// <summary>&lt;dailyrewards&gt;, ascending.</summary>
    public IReadOnlyList<Trigger> Daily { get; }

    /// <summary>&lt;campaignrewards&gt;, ascending.</summary>
    public IReadOnlyList<Trigger> Campaign { get; }

    private FactionCampaignData(IReadOnlyList<Trigger> daily, IReadOnlyList<Trigger> campaign) => (Daily, Campaign) = (daily, campaign);

    /// <summary>The campaign reward of <paramref name="id"/> (its trigger), null when there is none.</summary>
    public Trigger? FindCampaign(string? id) => Campaign.FirstOrDefault(t => t.Reward.Id == id);

    /// <summary>An empty campaign (no &lt;factioncampaign&gt;).</summary>
    public static readonly FactionCampaignData Empty = new([], []);

    public static FactionCampaignData Load(XElement root)
    {
        var campaign = root.Element("factioncampaign");
        if (campaign is null) return Empty;
        return new FactionCampaignData(Triggers(campaign.Element("dailyrewards")), Triggers(campaign.Element("campaignrewards")));
    }

    private static List<Trigger> Triggers(XElement? list)
    {
        static string S(XElement e, string a) => ((string?)e.Attribute(a) ?? "").Trim();
        static double D(XElement e, string a) => double.TryParse(S(e, a), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        // The reward flags' parser defaults (as personalseasons.xml's, 0x18072ded0): boughtpremium and premium true, nonpremium false.
        static bool B(XElement e, string a, bool fallback) => bool.TryParse(S(e, a), out var b) ? b : fallback;
        var triggers = new List<Trigger>();
        foreach (var t in list?.Elements("rewardtrigger") ?? [])
        {
            if (t.Element("rewards")?.Elements().FirstOrDefault() is not { } r) continue;
            var items = new string[7];
            foreach (var (attr, flag) in new[] { ("highlanditem", 1), ("forestitem", 2), ("steppeitem", 3), ("mountainitem", 4), ("swampitem", 5), ("caerleonitem", 6) })
                items[flag] = S(r, attr);
            var reward = new Reward(S(r, "id"), r.Name.LocalName, (long)D(r, "amount"), items,
                B(r, "boughtpremium", true), B(r, "premium", true), B(r, "nonpremium", false));
            triggers.Add(new Trigger(S(t, "id"), (int)D(t, "unlockday"), (long)D(t, "pointrequirement"), reward));
        }
        return [.. triggers.OrderBy(t => t.PointRequirement)];
    }
}
