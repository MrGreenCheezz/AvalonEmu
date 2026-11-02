using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Guilds
{
    private sealed record FinderRow(Guild Guild, GuildRecruitmentSettings Settings, string Alliance,
        short Members, short Active, int CurrentRank, int LastRank, long Total, long Pvp, long Gathering, long Pve);

    private static long FinderSum(IEnumerable<long> values)
    {
        var total = 0L;
        foreach (var value in values.Where(n => n > 0)) total = value > long.MaxValue - total ? long.MaxValue : total + value;
        return total;
    }

    private static long FinderTotal(Character c) => Math.Max(0, c.Fame);
    private static long FinderPvp(Character c) => Math.Max(0, c.Statistics?.PvpFame ?? 0);
    private static bool FinderEligible(Character c, GuildRecruitmentSettings s) =>
        FinderTotal(c) >= s.MinimumFame && FinderPvp(c) >= s.MinimumPvpFame;

    private FinderRow FinderData(Player viewer, Guild guild)
    {
        var settings = guild.Progress.Recruitment ?? new GuildRecruitmentSettings();
        var week = world.Bounties.WeekStart(UtcNow).Ticks;
        long Weekly(int stat) => FinderSum(guild.Members.Select(member =>
            guild.Progress.Logs.TryGetValue(member.Id, out var log) && log.Week == week && stat < log.This.Length ? log.This[stat] : 0));
        var pve = Weekly(GuildMemberLog.PveFame);
        var pvp = Weekly(GuildMemberLog.PvpFame);
        var gathering = Weekly(GuildMemberLog.GatheringFame);
        var crafting = Weekly(GuildMemberLog.CraftingFame);
        var season = world.GuildGoals.Season;
        var previous = world.GvgSeasons?.Seasons.Where(s => s.End <= season.Start).OrderByDescending(s => s.End).FirstOrDefault();
        int Rank(int? index) => index is { } n ? world.GuildGoals.Rank(viewer, n, guild.Name).azf : -1;
        // Local activity policy: a real member seen within seven days, including players online now.
        // Fame is the existing prospective Member Logs for this UTC week, without historical backfill.
        var active = guild.Members.Count(m => Online(m.Id) is not null || m.LastSeen >= UtcNow.AddDays(-7));
        return new(guild, settings, world.Alliances.PlateOf(world.Alliances.OfGuild(guild.Id)),
            (short)Math.Min(short.MaxValue, guild.Members.Count), (short)Math.Min(short.MaxValue, active),
            Rank(season.Index), Rank(previous?.Index), FinderSum([pve, pvp, gathering, crafting]), pvp, gathering, pve);
    }

    private IEnumerable<FinderRow> FinderRows(Player viewer, bool configured = true) =>
        All.Where(g => !configured || g.Progress.Recruitment is not null).Select(g => FinderData(viewer, g));

    private void FillFinderDetails(RequestGuildRecruitmentInfoResponse response, Guid viewer, Guild guild)
    {
        if (Online(viewer) is not { } player) return;
        var row = FinderData(player, guild);
        // 345319 deb.a(aip) ADC2CF-ADC339 and RecruitmentStatsModule.BuildRecruitmentStats:
        // keys16-19 gathering/PvP/PvE/total fame;20/21 active/total members;22/23 current/last ranks;
        // 24/25 territories/hideouts. Counts come from the existing owners and member registries.
        response.bks = row.Gathering; response.bkt = row.Pvp; response.bku = row.Pve; response.bkv = row.Total;
        response.bkw = row.Active; response.bkx = row.Members;
        response.bky = row.CurrentRank; response.bkz = row.LastRank;
        response.bk0 = world.Territories.OwnedBy(guild.Id);
        response.bk1 = world.Hideouts.OfGuild(guild.Id).Count;
    }

    private static bool FinderHours(GuildRecruitmentSettings s, short first, short last)
    {
        if (first < 0 || last < 0 || s.ActiveFrom < 0 || s.ActiveTo < 0) return true;
        if (first > 23 || last > 23) return false;
        // Equal filter endpoints are the native default (00-00): treat them as the full day.
        static bool Contains(int value, int from, int to) => from == to || (from < to ? value >= from && value <= to : value >= from || value <= to);
        return Enumerable.Range(0, 24).Any(hour => Contains(hour, first, last) && Contains(hour, s.ActiveFrom, s.ActiveTo));
    }

    // The native name-search instructions ignore these three separators on both sides of the query.
    private static string FinderNameKey(string name) => new(name.Where(c => c is not (' ' or '_' or '-')).ToArray());

    /// <summary>345319 deg.b ADE48F-ADE643 maps the native name-search arrays into dei's crest,
    /// guild identity, Discord and fame requirements. Closed guilds remain searchable by their real name.</summary>
    public RequestGuildFinderNameSearchResponse FinderName(Player p, string? search)
    {
        var query = search?.Trim() ?? "";
        var name = FinderNameKey(query);
        var rows = query.Length is > 0 and <= 128 && name.Length > 0 ? FinderRows(p, configured: false)
            .Where(r => FinderNameKey(r.Guild.Name).Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Guild.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Guild.Id).Take(50).ToArray() : [];
        return new()
        {
            bi3 = [.. rows.Select(r => r.Guild.Id)], bi4 = [.. rows.Select(r => r.Guild.Name)],
            bi5 = [.. rows.Select(r => r.Guild.Logo.Schema)], bi6 = [.. rows.Select(r => r.Guild.Logo.Symbol)],
            bi7 = [.. rows.Select(r => r.Guild.Logo.Color1)], bi8 = [.. rows.Select(r => r.Guild.Logo.Color2)],
            bi9 = [.. rows.Select(r => r.Guild.Logo.SymbolColor)], bja = [.. rows.Select(r => r.Guild.Logo.Scale)],
            bjb = [.. rows.Select(r => r.Guild.Logo.Offset)], bjc = [.. rows.Select(r => r.Settings.DiscordLink)],
            bjd = [.. rows.Select(r => r.Settings.JoinSettings)], bje = [.. rows.Select(r => r.Settings.MinimumFame)],
            bjf = [.. rows.Select(r => r.Settings.MinimumPvpFame)], bjg = FinderTotal(p.Character), bjh = FinderPvp(p.Character),
        };
    }

    /// <summary>183 has ten entries per native page (ADCF6E-ADCF8A), an echoed offset and a has-more
    /// flag. Filters/sort apply to real configured guilds; local ordering does not claim the official ranking algorithm.</summary>
    public RequestGuildFinderFilteredListResponse FinderFiltered(Player p, RequestGuildFinderFilteredListRequest request)
    {
        var rows = FinderRows(p).Where(r =>
            (!request.z2 || r.Settings.JoinSettings != 0 && FinderEligible(p.Character, r.Settings))
            && (!request.z3 || r.Settings.BeginnerFriendly)
            && (request.z4 == 0 || r.Settings.PrimaryLanguage == request.z4 || r.Settings.SecondaryLanguage == request.z4)
            && (request.z5 == 0 || r.Settings.Home == request.z5)
            && (request.z6 < 0 || r.Active >= request.z6) && (request.z7 < 0 || r.Active <= request.z7)
            && FinderHours(r.Settings, request.z8, request.z9)
            && (request.aaa == 0 || (r.Settings.Activities & request.aaa) == request.aaa));
        long Value(FinderRow r) => request.z1 switch
        {
            1 => r.LastRank > 0 ? r.LastRank : request.z0 ? long.MaxValue : long.MinValue,
            2 => r.CurrentRank > 0 ? r.CurrentRank : request.z0 ? long.MaxValue : long.MinValue,
            3 => r.Total, 4 => r.Pvp, 5 => r.Gathering, 6 => r.Pve, _ => r.Active,
        };
        var ordered = (request.z0 ? rows.OrderBy(Value) : rows.OrderByDescending(Value))
            .ThenBy(r => r.Guild.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Guild.Id);
        var first = Math.Max(0, request.zy);
        var count = Math.Clamp(request.zz == 0 ? 10 : (int)request.zz, 1, 50);
        var page = ordered.Skip(first).Take(count + 1).ToArray();
        var more = page.Length > count;
        page = page.Take(count).ToArray();
        // ADD0A9-ADD56F: all row arrays are parallel, including empty lists. GuildJoinSettings,
        // Home/Language/Activities use packed-enum wire arrays from the generated 345319 schema.
        return new()
        {
            bh3 = first, bh4 = more, bh5 = [.. page.Select(r => r.Guild.Id)], bh6 = [.. page.Select(r => r.Guild.Name)],
            bh7 = [.. page.Select(r => r.Alliance)], bh8 = [.. page.Select(r => r.Active)],
            bh9 = [.. page.Select(r => r.Guild.Logo.Schema)], bia = [.. page.Select(r => r.Guild.Logo.Symbol)],
            bib = [.. page.Select(r => r.Guild.Logo.Color1)], bic = [.. page.Select(r => r.Guild.Logo.Color2)],
            bid = [.. page.Select(r => r.Guild.Logo.SymbolColor)], bie = [.. page.Select(r => r.Guild.Logo.Scale)],
            bif = [.. page.Select(r => r.Guild.Logo.Offset)], big = [.. page.Select(r => r.Guild.Banner)],
            bih = [.. page.Select(r => r.Settings.DiscordLink)], bii = [.. page.Select(r => r.Settings.JoinSettings)],
            bij = [.. page.Select(r => r.Settings.Description)], bik = [.. page.Select(r => r.Settings.Author)],
            bil = [.. page.Select(r => r.Members)], bim = [.. page.Select(r => (byte)Math.Max(0, (int)r.Settings.ActiveFrom))],
            bin = [.. page.Select(r => (byte)(r.Settings.ActiveTo < 0 ? 23 : r.Settings.ActiveTo))],
            bio = [.. page.Select(r => r.Settings.Home)], bip = [.. page.Select(r => r.Settings.PrimaryLanguage)],
            biq = [.. page.Select(r => r.Settings.SecondaryLanguage)], bir = [.. page.Select(r => r.Settings.Activities)],
            bis = [.. page.Select(r => r.Settings.BeginnerFriendly)], bit = [.. page.Select(r => r.CurrentRank)],
            // dee.ADD3AD-ADD436 -> ded.C0/C8/D0/D8; FullEntryStatBuilder10AE66F-10AE900
            // displays keys28-31 as gathering/PvP/PvE/total, respectively.
            biu = [.. page.Select(r => r.LastRank)], biv = [.. page.Select(r => r.Gathering)], biw = [.. page.Select(r => r.Pvp)],
            bix = [.. page.Select(r => r.Pve)], biy = [.. page.Select(r => r.Total)],
            biz = [.. page.Select(r => r.Settings.MinimumFame)], bi0 = [.. page.Select(r => r.Settings.MinimumPvpFame)],
            bi1 = FinderTotal(p.Character), bi2 = FinderPvp(p.Character),
        };
    }

    /// <summary>187 has three cards per page (ADEE7B), language and page echoes. Recommendations
    /// use compatible beginner-friendly guilds ordered by observed activity. RecommendationStatBuilder.a1l
    /// (345319 10C44E5-10C4504) labels every card Beginner-Friendly without a response boolean.</summary>
    public RequestGuildFinderRecommendedListResponse FinderRecommended(Player p, byte language, byte pageIndex)
    {
        var all = FinderRows(p).Where(r => r.Settings.BeginnerFriendly && r.Settings.JoinSettings != 0 && FinderEligible(p.Character, r.Settings)
            && r.Guild.Members.Count < Config.GuildMaxMembers
            && (language == 0 || r.Settings.PrimaryLanguage == language || r.Settings.SecondaryLanguage == language))
            .OrderByDescending(r => r.Active).ThenBy(r => r.Guild.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Guild.Id).ToArray();
        var page = all.Skip(pageIndex * 3).Take(3).ToArray();
        return new()
        {
            bji = pageIndex, bjj = (byte)Math.Min(byte.MaxValue, Math.Max(1, (all.Length + 2) / 3)), bjk = language,
            bjl = [.. page.Select(r => r.Guild.Id)], bjm = [.. page.Select(r => r.Guild.Name)], bjn = [.. page.Select(r => r.Alliance)],
            bjo = [.. page.Select(r => r.Active)], bjp = [.. page.Select(r => r.Guild.Logo.Schema)],
            bjq = [.. page.Select(r => r.Guild.Logo.Symbol)], bjr = [.. page.Select(r => r.Guild.Logo.Color1)],
            bjs = [.. page.Select(r => r.Guild.Logo.Color2)], bjt = [.. page.Select(r => r.Guild.Logo.SymbolColor)],
            bju = [.. page.Select(r => r.Guild.Logo.Scale)], bjv = [.. page.Select(r => r.Guild.Logo.Offset)],
            bjw = [.. page.Select(r => r.Guild.Banner)], bjx = [.. page.Select(r => r.Settings.DiscordLink)],
            bjy = [.. page.Select(r => r.Settings.JoinSettings)], bjz = [.. page.Select(r => r.Settings.Description)],
            bj0 = [.. page.Select(r => r.Settings.Author)], bj1 = [.. page.Select(r => r.Members)],
            bj2 = [.. page.Select(r => (byte)Math.Max(0, (int)r.Settings.ActiveFrom))],
            bj3 = [.. page.Select(r => (byte)(r.Settings.ActiveTo < 0 ? 23 : r.Settings.ActiveTo))],
            bj4 = [.. page.Select(r => r.Settings.Home)], bj5 = [.. page.Select(r => r.Settings.PrimaryLanguage)],
            bj6 = [.. page.Select(r => r.Settings.SecondaryLanguage)], bj7 = [.. page.Select(r => r.Settings.Activities)],
            bj8 = [.. page.Select(r => r.Settings.MinimumFame)], bj9 = [.. page.Select(r => r.Settings.MinimumPvpFame)],
            bka = FinderTotal(p.Character), bkb = FinderPvp(p.Character),
        };
    }
}
