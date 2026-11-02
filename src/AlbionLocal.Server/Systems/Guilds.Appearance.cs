using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Guilds
{
    private const long EditGuildAppearance = 2, EditGuildDescription = 0x200000;
    private const int GuildCodexLimit = 512;

    private Guild? AppearanceOwner(Guid character, long permission) =>
        Of(character) is { } guild && guild.Find(character) is { } member
        && (GuildRoles.Permissions(member.Role) & permission) != 0 ? guild : null;

    /// <summary>236: c2n.a5A3FD7F-DA4 checks CHANGE_GUILD_LOGO2 and512 UTF-16 characters.
    /// Empty clears the codex; no trim is applied by the native sender. Persist before publishing103.</summary>
    public short ChangeCodex(Guid character, string? text, out Guild? updated)
    {
        updated = null;
        if (AppearanceOwner(character, EditGuildAppearance) is not { } guild || text is null || text.Length > GuildCodexLimit) return Refused;
        var progress = guild.Progress;
        if (progress.Codex == text) { updated = guild; return 0; }
        var before = (progress.Codex, progress.CodexAuthor, progress.CodexAuthorName, progress.CodexUpdatedAt);
        progress.Codex = text;
        progress.CodexAuthor = character;
        progress.CodexAuthorName = guild.Find(character)!.Name;
        progress.CodexUpdatedAt = world.GameNow;
        try { store?.SaveGuildProgress(guild); }
        catch (Exception ex)
        {
            (progress.Codex, progress.CodexAuthor, progress.CodexAuthorName, progress.CodexUpdatedAt) = before;
            Log.Warn("guild", $"codex of guild '{guild.Name}' not saved: {ex.Message}");
            return Refused;
        }
        updated = guild;
        return 0;
    }

    public void PublishCodex(Guild guild) => SendTo(guild, FullUpdate(guild, tutorial: false));

    private static byte FactionAllegianceOf(Guild guild) => guild.Progress.FactionAllegiance <= 6 ? guild.Progress.FactionAllegiance : (byte)0;

    /// <summary>482 has no target: only the authenticated member's guild is queried. Both482 and481
    /// require EDIT_GUILD_DESCRIPTION (345319 cxj9F2945/9F2A9E), independently of character enlistment.</summary>
    public GetGuildFactionAllegianceResponse FactionAllegiance(Guid character) =>
        AppearanceOwner(character, EditGuildDescription) is { } guild
            ? new() { axp = guild.Id, axq = FactionAllegianceOf(guild) }
            : new() { ReturnCode = Refused };

    /// <summary>481 dropdown10B3171-10B322F offers1..6 only. c2n.A42FAD updates its cache before
    /// sending; no extra response/event is needed after the normal ACK. Save only this guild preference.</summary>
    public short ChangeFactionAllegiance(Guid character, byte faction)
    {
        if (AppearanceOwner(character, EditGuildDescription) is not { } guild || faction is < 1 or > 6) return Refused;
        var before = guild.Progress.FactionAllegiance;
        if (before == faction) return 0;
        guild.Progress.FactionAllegiance = faction;
        try { store?.SaveGuildProgress(guild); }
        catch (Exception ex)
        {
            guild.Progress.FactionAllegiance = before;
            Log.Warn("guild", $"faction allegiance of guild '{guild.Name}' not saved: {ex.Message}");
            return Refused;
        }
        return 0;
    }
}
