using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Faction warfare, the PvE side (analysis/work/stageN/faction/SPEC.md, "SPEC" here): enlisting at a faction trader (op 433, the official
/// cooldown on a switch, FactionEnlistmentChanged 482), the faction points (the FACTION_* currencies, UpdateCurrency 85) and the standing
/// (UpdateFactionStanding 86) that PvE fame earns in the warfare clusters while enlisted and flagged to the faction, and the faction trader's
/// currency and standing gates (<see cref="ShopRefusal"/>, <see cref="Pay"/>). The flag itself stays <see cref="Flagging"/>'s, and what it
/// means between players stays <see cref="Pvp"/>'s (under Pvp:Mode Consensual a faction flag makes nobody Hostile). Under <see cref="World.Sync"/>.
/// </summary>
public sealed class Factions(World world)
{
    private Data.FactionWarfareData? _data;
    private bool _read;

    /// <summary>UpdateCurrency 85 key 0: the reason FROM_CLUSTER (12, CHAT_FACTIONPOINTS_FROM_CLUSTER with the cluster's name, SPEC §2.3) and GENERAL (0).</summary>
    public const byte FromCluster = 12, General = 0;

    /// <summary>factionwarfare.xml and currencies.xml, read on first use; null without client data (then nothing here works).</summary>
    public FactionWarfareData? Data
    {
        get
        {
            if (_read) return _data;
            _read = true;
            if (world.Clusters?.Folder is not { } folder) return null;
            try { _data = FactionWarfareData.Load(GameDataFile.Open(folder, "factionwarfare.bin").Root(), GameDataFile.Open(folder, "currencies.bin").Root()); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Log.Warn("faction", $"factionwarfare.bin not read: {e.Message}; faction warfare is off");
            }
            return _data;
        }
    }

    /// <summary>The enlistment cooldown of a switch (factionenlistmentcooldowninmins, 1440 min).</summary>
    public TimeSpan Cooldown => TimeSpan.FromMinutes(Data?.EnlistCooldownMinutes ?? 1440);

    // ---- enlisting (SPEC §1) ----

    /// <summary>
    /// EnlistFactionWarfare (op 433) {0 the faction trader's object id, 1 a bool the client sends false}: the trader on the player's map within
    /// 15 m (+ Transfer:PositionTolerance) names the faction (its faction attribute). The same faction again changes nothing; a switch from
    /// another faction within the cooldown of the last change is refused. Returns the answer's key 0 (the GameTimeStamp from which the next
    /// switch is allowed [inference]) or why it was refused.
    /// </summary>
    public (long Stamp, string? Refused) Enlist(Player p, long buildingId)
    {
        if (Data is null) return (0, "factionwarfare.xml is not read");
        if (p.Map is not { } map || !map.Objects.TryGetValue(buildingId, out var o) || o is not Building { Template.Kind: "factionbuilding" } trader)
            return (0, $"no faction trader {buildingId}");
        var faction = FactionWarfareData.FlagOf(trader.Template.Attrs.GetValueOrDefault("faction"));
        if (faction == 0) return (0, $"{trader.Template.Name} names no faction");
        var reach = 15 + world.Config.Transfer.PositionTolerance;
        if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), trader.Position) > reach) return (0, $"too far from {trader.Template.Name}");
        return Enlist(p, faction, force: false);
    }

    /// <summary>
    /// Enlists <paramref name="p"/> to <paramref name="faction"/> (a FlaggingStatus 1-6, or 0: none, a GM's). Unless <paramref name="force"/>d, a
    /// switch away from a faction within <see cref="Cooldown"/> of the last change is refused with a chat line. A change ends a flag cast under
    /// way, makes a player flagged to another faction Blue again (ChangeFlaggingFinished) and goes to it and its observers as
    /// FactionEnlistmentChanged (482) {0 the player, 1 the faction}; the standing and points of the old faction stay.
    /// </summary>
    public (long Stamp, string? Refused) Enlist(Player p, byte faction, bool force)
    {
        var c = p.Character;
        var now = world.GameNow;
        if (p.Faction == faction) return (c.FactionChangedAt + Cooldown.Ticks, null);
        if (!force && p.Faction != 0 && c.FactionChangedAt + Cooldown.Ticks > now)
        {
            var left = TimeSpan.FromTicks(c.FactionChangedAt + Cooldown.Ticks - now);
            Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture, $"You can change your faction again in {(int)left.TotalHours} h {left.Minutes} min."));
            return (0, string.Create(CultureInfo.InvariantCulture, $"the enlistment cooldown: {left.TotalMinutes:0} min left"));
        }
        var before = p.Faction;
        p.Faction = faction;
        c.FactionChangedAt = now;
        world.Saves.MarkChanged(c);
        Flagging.Interrupt(p, "the enlistment changed");
        if (Pvp.IsFaction(p.Flag) && p.Flag != faction) Flagging.Set(p, Pvp.Blue);
        p.SendAround(new FactionEnlistmentChangedEvent { ddn = p.Id, b6r = faction });
        SendRank(p); // stageR fw-campaign: the nameplate rank of the new faction's standing
        Log.Info("faction", $"'{c.Name}' enlisted to {Name(faction)} (was {Name(before)}){(force ? " by a GM" : "")}");
        return (now + Cooldown.Ticks, null);
    }

    /// <summary>The name of FlaggingStatus <paramref name="flag"/> (Highland …; none for 0).</summary>
    public string Name(byte flag) => Data?.Of(flag)?.Name ?? (flag == 0 ? "none" : flag.ToString(CultureInfo.InvariantCulture));

    // ---- points and standing (SPEC §2) ----

    /// <summary>
    /// A PvE fame gain of <paramref name="p"/> (<see cref="Destiny.Award"/>; a7m, after the rates): in a warfare cluster
    /// (<see cref="FactionWarfareData.IsWarzone"/>), enlisted and flagged to that faction (the official rule; under Pvp:Mode Consensual the flag
    /// makes nobody Hostile), it earns fame × the source's scalingmultiplier (pve 0.007 for kills, gathering 0.1, fishing 0.1) × the rank's
    /// pointmultiplier faction points, and the same base × pointmultiplier × standingmultiplier standing.
    /// </summary>
    public void OnFame(Player p, long gain, FameSource source)
    {
        if (gain <= 0 || p.IsBot || p.Faction == 0 || p.Flag != p.Faction || p.Map is not { } map || Data is not { } data || !data.IsWarzone(map.Cluster)) return;
        var factor = source switch
        {
            FameSource.Kill => data.PvePoints,
            FameSource.Harvest => data.GatheringPoints,
            FameSource.Fishing => data.FishingPoints,
            _ => 0,
        };
        if (factor <= 0) return;
        Earn(p, (long)Math.Floor(gain * factor * GroupFactor(p)), map.Cluster);
    }

    /// <summary>
    /// StageR fw-campaign: &lt;factionpointbonus&gt;'s bonusfactor for <paramref name="p"/>'s group (2 → 1.04 … 20 → 1.40; 1 alone): the party members
    /// on its map that are enlisted to and flagged for its faction, itself included [assume: who counts as the group is not known].
    /// </summary>
    public double GroupFactor(Player p)
    {
        if (Data is not { GroupBonus.Count: > 0 } data || p.Map is not { } map || world.Parties.Of(p.Character.Id) is not { } party) return 1;
        var size = party.Members.Count(m => !m.Companion && (m.Id == p.Character.Id
            || world.Parties.Online(m.Id) is { } o && o.Map == map && o.Faction == p.Faction && o.Flag == p.Faction));
        size = Math.Min(size, data.GroupBonus.Keys.Max());
        return data.GroupBonus.TryGetValue(size, out var f) ? f : 1;
    }

    // ---- the campaign's tally and the nameplate rank (stageR fw-campaign SPEC §1.4, §3) ----

    private readonly Dictionary<Guid, (byte Faction, int Month, long Points)> _tally = [];

    /// <summary>A character's points of <paramref name="month"/> for <paramref name="faction"/> (its latest), for the campaign's per-faction totals.</summary>
    public void Tally(Guid character, byte faction, int month, long points)
    {
        if (faction is >= 1 and <= 6) _tally[character] = (faction, month, points);
    }

    /// <summary>GetFactionWarfareCampaignData key 5: the points of each faction (index faction − 1) in <paramref name="month"/>, over the characters this server saw.</summary>
    public long[] TallyOf(int month)
    {
        var sums = new long[6];
        foreach (var (faction, m, points) in _tally.Values)
            if (m == month) sums[faction - 1] += points;
        return sums;
    }

    /// <summary>
    /// The nameplate rank (NewCharacter key 57, UpdateFactionRank 483 key 1): the 0-based rank of the enlisted faction's standing (the captures
    /// show 0-20; cp0.af 0x180bd9380 indexes bcg's rank list with it); 0 when not enlisted.
    /// </summary>
    public int RankIndex(Character c) => Data is { } data && c.Faction is >= 1 and <= 6 ? data.RankOf(c.FactionStanding[c.Faction - 1]) - 1 : 0;

    /// <summary>UpdateFactionRank (483) {0 the player, 1 <see cref="RankIndex"/>} to <paramref name="p"/> and its observers (cp0.ajv 0x180bda020).</summary>
    public void SendRank(Player p)
    {
        if (p.IsBot) return;
        p.SendAround(new UpdateFactionRankEvent { ddn = p.Id, d3a = RankIndex(p.Character) });
    }

    /// <summary>
    /// <paramref name="raw"/> standing (a7m) to <paramref name="faction"/> up to the max (a daily campaign reward): UpdateFactionStanding 86 and,
    /// when the enlisted faction's rank changes, 483.
    /// </summary>
    public void GrantStanding(Player p, byte faction, long raw)
    {
        if (raw <= 0 || Data is not { } data || faction is < 1 or > 6) return;
        var c = p.Character;
        var rank = RankIndex(c);
        var before = c.FactionStanding[faction - 1];
        c.FactionStanding[faction - 1] = Math.Min(data.MaxStanding * 10000, before + raw);
        world.Saves.MarkChanged(c);
        if (c.FactionStanding[faction - 1] > before) SendStanding(p, faction, c.FactionStanding[faction - 1] - before);
        if (RankIndex(c) != rank) SendRank(p);
    }

    /// <summary>
    /// <paramref name="p"/>'s enlisted faction gets <paramref name="basePoints"/> (a7m) × its rank's pointmultiplier as points (UpdateCurrency 85
    /// {0 FromCluster, 1 <paramref name="cluster"/>, 2 the currency's index, 3 the gain, 9 the new total}) and base × pointmultiplier ×
    /// standingmultiplier standing up to the max (UpdateFactionStanding 86 {0 faction, 1 the gain, 3 the new total}).
    /// </summary>
    public void Earn(Player p, long basePoints, string? cluster)
    {
        if (basePoints <= 0 || Data is not { } data || data.Of(p.Faction) is not { } faction) return;
        var c = p.Character;
        var i = p.Faction - 1;
        var rank = data.Ranks[data.RankOf(c.FactionStanding[i]) - 1];
        var points = (long)Math.Floor(basePoints * rank.PointMultiplier);
        var standing = (long)Math.Floor(basePoints * rank.PointMultiplier * rank.StandingMultiplier);
        c.FactionPoints[i] += points;
        var rankBefore = data.RankOf(c.FactionStanding[i]);
        var before = c.FactionStanding[i];
        c.FactionStanding[i] = Math.Min(data.MaxStanding * 10000, before + standing);
        world.Saves.MarkChanged(c);
        if (points > 0) SendCurrency(p, p.Faction, points, cluster is null ? General : FromCluster, cluster);
        if (c.FactionStanding[i] > before) SendStanding(p, p.Faction, c.FactionStanding[i] - before);
        if (data.RankOf(c.FactionStanding[i]) > rankBefore)
        {
            Log.Info("faction", $"'{c.Name}' reached {faction.Name} rank {data.RankOf(c.FactionStanding[i])}");
            SendRank(p); // stageR fw-campaign: the nameplate rank
        }
        FactionCampaign.OnPoints(p, points); // stageR fw-campaign: the day's, month's and week's points and their rewards
    }

    /// <summary>UpdateCurrency (85) for the FACTION_* currency of <paramref name="faction"/>: {0 reason, 1 the cluster, 2 the index, 3 the gain, 9 the total}.</summary>
    public void SendCurrency(Player p, byte faction, long gain, byte reason = General, string? cluster = null)
    {
        if (Data is not { } data || data.Of(faction) is not { } f || !data.CurrencyIndex.TryGetValue(f.Currency, out var index)) return;
        p.Session.Send(new UpdateCurrencyEvent { d2v = reason, d2w = cluster, d2x = index, d2y = gain, d24 = p.Character.FactionPoints[faction - 1] });
    }

    /// <summary>UpdateFactionStanding (86) {0 the faction, 1 the change, 2 the premium part (0), 3 the new total}: ctp.aqp sets ctp.ama[faction − 1] from key 3.</summary>
    public void SendStanding(Player p, byte faction, long change) =>
        p.Session.Send(new UpdateFactionStandingEvent { d3b = faction, d3c = change, d3d = 0, d3e = p.Character.FactionStanding[faction - 1] });

    /// <summary>
    /// Join keys 104 (the currencies by index: the six faction ones with points, and the mists' FAVOR, as captured) and 105 (the standing per
    /// faction, a7m[6]).
    /// </summary>
    public void JoinKeys(Character c, JoinResponse r)
    {
        r.ba7 = [.. c.FactionStanding];
        if (c.Goals?.FactionCampaign is { } log) Tally(c.Id, c.Faction, log.Month, log.MonthPoints); // stageR fw-campaign: the saved month's points
        if (Data is not { } data) return;
        var currencies = new Dictionary<int, long>();
        for (byte f = 1; f <= 6; f++)
            if (c.FactionPoints[f - 1] > 0 && data.Of(f) is { } faction && data.CurrencyIndex.TryGetValue(faction.Currency, out var index)) currencies[index] = c.FactionPoints[f - 1];
        if (c.MistFavor > 0 && data.CurrencyIndex.TryGetValue("FAVOR", out var favor)) currencies[favor] = c.MistFavor;
        if (currencies.Count > 0) r.ba6 = currencies;
    }

    // ---- the faction trader (SPEC §3) ----

    /// <summary>The recipe requirements the trader's checks take: the FACTION_* currencies and playerfactionstanding.</summary>
    public static bool Supports(string requirement) => requirement == "playerfactionstanding" || requirement.StartsWith("currency FACTION_", StringComparison.Ordinal);

    /// <summary>
    /// Why <paramref name="p"/> may not buy <paramref name="crafts"/> of <paramref name="recipe"/> at <paramref name="building"/>: another
    /// building's recipe (buildingfilter), too little standing with the recipe's faction, too few points of a FACTION_* currency; null = it may.
    /// </summary>
    public string? ShopRefusal(Player p, CraftRecipe recipe, int crafts, BuildingTemplate building)
    {
        var c = p.Character;
        if (recipe.BuildingFilter is { } filter && !filter.Equals(building.Name, StringComparison.OrdinalIgnoreCase)) return $"the recipe is {filter}'s";
        if (recipe.FactionStanding is { } fs)
        {
            var f = FactionWarfareData.FlagOf(fs.Faction);
            if (f == 0) return $"unknown faction {fs.Faction}";
            if (c.FactionStanding[f - 1] < (long)Math.Round(fs.Min * 10000))
            {
                Expeditions.Tell(p, string.Create(CultureInfo.InvariantCulture, $"This needs {fs.Min:N0} {Name(f)} standing; you have {c.FactionStanding[f - 1] / 10000:N0}."));
                return string.Create(CultureInfo.InvariantCulture, $"{Name(f)} standing {c.FactionStanding[f - 1] / 10000} of {fs.Min:0}");
            }
        }
        foreach (var (name, amount) in recipe.Currencies.Where(x => x.Name.StartsWith("FACTION_", StringComparison.Ordinal)))
        {
            var f = FactionOfCurrency(name);
            if (f == 0) return $"unknown currency {name}";
            var need = (long)Math.Round(amount * 10000) * crafts;
            if (c.FactionPoints[f - 1] < need) return string.Create(CultureInfo.InvariantCulture, $"{c.FactionPoints[f - 1] / 10000} of {need / 10000} {name}");
        }
        return null;
    }

    /// <summary>The FACTION_* points of <paramref name="crafts"/> of the recipe are taken (after <see cref="ShopRefusal"/>), each with an UpdateCurrency (gain 0, the new total).</summary>
    public void Pay(Player p, CraftRecipe recipe, int crafts)
    {
        foreach (var (name, amount) in recipe.Currencies)
        {
            if (FactionOfCurrency(name) is var f and > 0)
            {
                p.Character.FactionPoints[f - 1] = Math.Max(0, p.Character.FactionPoints[f - 1] - (long)Math.Round(amount * 10000) * crafts);
                world.Saves.MarkChanged(p.Character);
                SendCurrency(p, f, 0);
            }
        }
    }

    private byte FactionOfCurrency(string currency)
    {
        if (Data is not { } data) return 0;
        for (byte f = 1; f <= 6; f++)
            if (data.Of(f)?.Currency == currency) return f;
        return 0;
    }
}
