using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

public sealed partial class Guilds
{
    private LoadoutData? loadoutData;
    public LoadoutData LoadoutSettings => loadoutData ??= LoadoutData.Load(world.Config.ClientGameDataPath);

    /// <summary>Every current member can read/use builds; only MANAGE_LOADOUTS may change them.</summary>
    public Guild? LoadoutOwner(Guid character, Guid owner, bool write = false) =>
        Of(character) is { } g && g.Id == owner && g.Find(character) is { } member
        && (!write || (GuildRoles.Permissions(member.Role) & GuildRoles.ManageLoadouts) != 0) ? g : null;

    /// <summary>World.Sync serializes mutations. Publish success only after the guild store commits; restore on failure.</summary>
    public bool SaveLoadouts(Guid character, Guid owner, List<Loadout> entries, int? orderVersion = null)
    {
        if (LoadoutOwner(character, owner, write: true) is not { } g
            || entries.Count > LoadoutSettings.GuildLimit || entries.Any(l => l.Owner != owner)
            || entries.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count) return false;
        var before = g.Loadouts;
        g.Loadouts = new GuildLoadouts { Entries = entries, OrderVersion = orderVersion ?? before.OrderVersion };
        try { store?.SaveGuildLoadouts(g); }
        catch (Exception ex)
        {
            g.Loadouts = before;
            Log.Warn("guild", $"loadouts of guild '{g.Name}' not saved: {ex.Message}");
            return false;
        }
        return true;
    }
}
