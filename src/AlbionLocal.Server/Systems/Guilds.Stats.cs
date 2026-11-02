using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Guilds
{
    // Installed345319 Appearance editor has Stat1/Stat2/Stat3 (native evidence031615229).
    private const int OptionalStatSlots = 3;
    private static bool ValidOptionalStats(int[]? selection) => selection is { Length: OptionalStatSlots }
        && selection.All(stat => stat is >= 0 and <= 13);

    /// <summary>Current345319 GuildStats147: guild identity and retained prospective Member Logs.
    /// Fame covers only gains actually recorded for this guild, with no backfill from character
    /// totals or assumption about earlier history. Unretained combat totals remain absent.</summary>
    public GuildStatsEvent? PublicStats(string? name)
    {
        if (!Enabled || Find(name) is not { } guild) return null;
        var logo = guild.Logo;
        // GuildGoals.OnFame records only real gains while GuildOf(player) holds; AddStat requires
        // current membership. All survives week changes and member departure, so sum every retained
        // entry, including former members. Reading this profile neither rolls nor rewrites the logs.
        long Fame(int stat) => FinderSum(guild.Progress.Logs.Values.Select(log =>
            log?.All is { } all && stat < all.Length ? all[stat] : 0));
        var pvp = Fame(GuildMemberLog.PvpFame);
        var pve = Fame(GuildMemberLog.PveFame);
        var gathering = Fame(GuildMemberLog.GatheringFame);
        var crafting = Fame(GuildMemberLog.CraftingFame);
        // cty.aho 9BB1CF/9BB288 copies147 keys1/2 into ct0's name/id;9BB2C8 installs
        // alliance name (key3).9BB39C installs leader name (key6), used by header10DB299.
        // 9BB58E-9BB5E2 constructs the crest from keys29-35;key38 is the banner.
        return new GuildStatsEvent
        {
            dqj = guild.Name, dqk = guild.Id,
            dql = world.Alliances.OfGuild(guild.Id)?.Name ?? "",
            dqm = "", dqo = guild.Master?.Name ?? "",
            // cty9BB3E1-9BB450 -> StatsView10E07DA-10E0AFE: raw a7m, divided by10000
            // only by the client. Keys10/19/20/21/22 = total/PvP/PvE/gathering/crafting.
            dqs = FinderSum([pvp, pve, gathering, crafting]), dq1 = pvp, dq2 = pve, dq3 = gathering, dq4 = crafting,
            drb = logo.Schema, drc = logo.Symbol, drd = logo.Color1,
            dre = logo.Color2, drf = logo.SymbolColor, drg = logo.Scale, drh = logo.Offset,
            drk = guild.Banner,
        };
    }

    /// <summary>485 keeps the native slot order, None and duplicate choices. OnClickSave10B9A60
    /// checks CHANGE_GUILD_LOGO (2), sends one value per serialized StatRollout, and expects no
    /// success payload. Its callback10BA342 distinguishes19400 internal error/19401 no permission.</summary>
    public short ChangeOptionalStats(Guid character, int[]? selection)
    {
        if (Of(character) is not { } guild || guild.Find(character) is not { } member
            || (GuildRoles.Permissions(member.Role) & 2) == 0) return 19401;
        if (!ValidOptionalStats(selection)) return 19400;
        var before = guild.Progress.OptionalStats;
        guild.Progress.OptionalStats = [.. selection!];
        try { store?.SaveGuildProgress(guild); }
        catch (Exception ex)
        {
            guild.Progress.OptionalStats = before;
            Log.Warn("guild", $"optional statistics of guild '{guild.Name}' not saved: {ex.Message}");
            return 19400;
        }
        return 0;
    }

    /// <summary>484: false restores all editor slots exactly as485 sent them. Older saves use null:
    /// the editor indexes every serialized rollout when key1 is present (10BA0CE-10BA13D), while
    /// null selects None for each. Public lists contain only selected statistics with real authority.</summary>
    public GuildGetOptionalStatsResponse OptionalStats(Guid guildId, bool publicView)
    {
        var guild = ById(guildId);
        if (guild is null) return new() { ReturnCode = Refused, a6i = guildId, a6j = publicView ? [] : null, a6k = [] };
        var selected = ValidOptionalStats(guild.Progress.OptionalStats) ? guild.Progress.OptionalStats! : [];
        if (!publicView) return new() { a6i = guild.Id, a6j = selected.Length == OptionalStatSlots ? selected.ToArray() : null, a6k = [] };

        // GuildAge1 is a creation timestamp, not a duration: ge5.a1CBDAD2 formats Now-Created.
        // Faction allegiance2 is the guild's saved editor choice, not an aggregate of member flags.
        // Faction standing3 has no guild authority here. HG kills4-9 and Crystal
        // wins10-13 have no retained guild battle ledger. Character ratings/current membership
        // cannot reconstruct those guild totals; preserve their selections without fabricating rows.
        var types = selected.Where(stat => stat is 1 or 2).ToArray();
        var allegiance = FactionAllegianceOf(guild);
        return new() { a6i = guild.Id, a6j = types,
            a6k = [.. types.Select(stat => stat == 1 ? guild.Created.Ticks : (long)allegiance)], a6l = allegiance };
    }
}
