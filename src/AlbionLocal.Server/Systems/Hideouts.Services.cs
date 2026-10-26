using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Hideouts phase H3 (SPEC §5, §7 H3): the hideout's own bank vault (Hideouts:BankVault Own: a location of its own, never a city's; the
/// per-city logistics rule), its own market at levels b/c (the marketplace's location is the instance id, as any map's), the repair shop
/// (an ordinary one), Make Home / Leave Home (205/206 with Join key 67 and UpdateHome 216), the respawn in the home hideout, the Journey Back
/// home (BuyJourney kind 1) and the access presets (AccessRightsChangeSettings 198).
/// </summary>
public sealed partial class Hideouts
{
    /// <summary>
    /// The vault location a bank on <paramref name="map"/> opens when the map is a hideout's interior: Own (official) the instance id itself
    /// ("@HIDEOUT@&lt;guid&gt;|&lt;tab&gt;", a vault no other bank opens); City the nearest city's (<see cref="NearestCityVault"/>). Null elsewhere
    /// (the caller's rule: the map's own cluster, an island's home city).
    /// </summary>
    public string? VaultLocation(Map map)
    {
        if (Find(map.Cluster) is not { } h) return null;
        return S.BankVault == HideoutBankVault.City ? NearestCityVault(h.Cluster) : h.Instance;
    }

    /// <summary>
    /// Hideouts:BankVault City: the vault of the city nearest to <paramref name="cluster"/> by world.xml exits (the first PLAYERCITY_* cluster a
    /// breadth-first walk meets; its "Bank of …" interior when it has one, as 1000's bank is 1001) [choice: SPEC §5 "nearest"]; the cluster
    /// itself without world.xml.
    /// </summary>
    public string NearestCityVault(string cluster)
    {
        if (world.Clusters?.WorldIfLoaded is not { } w) return cluster;
        var seen = new HashSet<string>(StringComparer.Ordinal) { cluster };
        var queue = new Queue<string>([cluster]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (w.Find(id) is not { } info) continue;
            if (info.Type.StartsWith("PLAYERCITY_", StringComparison.Ordinal))
                return info.Exits.Select(e => w.Find(e.TargetCluster)).FirstOrDefault(t => t is not null && t.DisplayName.StartsWith("Bank", StringComparison.OrdinalIgnoreCase))?.Id ?? id;
            foreach (var e in info.Exits)
                if (seen.Add(e.TargetCluster)) queue.Enqueue(e.TargetCluster);
        }
        return cluster;
    }

    // ---- home (Make Home 205, Leave Home 206)

    /// <summary>
    /// Why <paramref name="character"/> may not make <paramref name="h"/> its home (null = it may): the entry (<see cref="CanVisit"/>), then the
    /// level's &lt;makehome&gt; (hideouts.xml: a and b the owner guild, c also its alliance; public never).
    /// </summary>
    public string? CanMakeHome(Hideout h, Guid character, bool upgradeBlocks = true)
    {
        if (CanVisit(h, character, upgradeBlocks) is { } why) return why;
        var level = Data.Level(h.Level) ?? Data.Levels[0];
        var guild = world.Guilds.Of(character);
        if (level.HomeGuild && guild?.Id == h.Guild) return null;
        if (level.HomeAlliance && guild is not null && world.Alliances.OfGuild(guild.Id) is { } a && world.Alliances.OfGuild(h.Guild)?.Id == a.Id) return null;
        if (level.HomePublic) return null;
        return level.HomeAlliance ? "only the owner guild and its alliance may make this hideout their home" : "only the owner guild may make this hideout its home";
    }

    /// <summary>
    /// The home hideout of <paramref name="character"/> while it still may live there (<see cref="CanMakeHome"/>; a running upgrade keeps it a home,
    /// though nobody enters until it is done); null = none (the city rules).
    /// </summary>
    public Hideout? HomeOf(Guid character) =>
        Enabled && _homes.TryGetValue(character, out var id) && _hideouts.GetValueOrDefault(id) is { } h && CanMakeHome(h, character, upgradeBlocks: false) is null ? h : null;

    /// <summary>Join key 67 (ctp+0x3a0, the home location the travel window and the management's button read): the home hideout's instance id.</summary>
    public void JoinKeys(Character c, JoinResponse r)
    {
        if (HomeOf(c.Id) is { } h) r.a95 = h.Instance;
    }

    /// <summary>
    /// MakeHome (205, {0 the management}): <paramref name="p"/>'s home becomes the hideout of the management (<see cref="CanMakeHome"/>); saved,
    /// UpdateHome {0 player, 1 the old home string, 2 the new} so the client's home (ctp+0x3a0) follows (ctp.af(a07) swaps it when key 1 matches).
    /// Null = done, else why not.
    /// </summary>
    public string? MakeHome(Player p, long management)
    {
        if (p.Map?.Objects.GetValueOrDefault(management) is not HideoutManagement m) return $"object {management} is no hideout management";
        if (CanMakeHome(m.Hideout, p.Character.Id) is { } why) return why;
        var old = HomeOf(p.Character.Id)?.Instance ?? "";
        _homes[p.Character.Id] = m.Hideout.Id;
        store?.SaveHideoutHome(p.Character.Id, m.Hideout.Id);
        p.Session.Send(new UpdateHomeEvent { ddn = p.Id, d33 = old, d34 = m.Hideout.Instance });
        Log.Info("world", $"'{p.Character.Name}' makes {m.Hideout} its home");
        return null;
    }

    /// <summary>LeaveHome (206, {0 the management}): the home hideout is no home any more (UpdateHome to ""). Null = done, else why not.</summary>
    public string? LeaveHome(Player p, long management)
    {
        if (p.Map?.Objects.GetValueOrDefault(management) is not HideoutManagement m) return $"object {management} is no hideout management";
        if (!_homes.TryGetValue(p.Character.Id, out var id) || id != m.Hideout.Id) return "this hideout is not the character's home";
        _homes.Remove(p.Character.Id);
        store?.SaveHideoutHome(p.Character.Id, null);
        p.Session.Send(new UpdateHomeEvent { ddn = p.Id, d33 = m.Hideout.Instance, d34 = "" });
        Log.Info("world", $"'{p.Character.Name}' leaves its home {m.Hideout}");
        return null;
    }

    /// <summary>
    /// The respawn of dead <paramref name="p"/> in its home hideout (Hideouts:RespawnAtHome; Death.Respawn asks first): inside at the exit's spawn
    /// point (the clustertype's HIDEOUT_EXIT_ON_RESPAWN spawnbuff is the client's), a template not resolved yet resolved first. False = no home
    /// (the city rules).
    /// </summary>
    public bool RespawnHome(Player p, string why)
    {
        if (!S.RespawnAtHome || HomeOf(p.Character.Id) is not { Upgrading: false } h || WorldOf(h.Instance) is not { } template) return false;
        void Go(ClusterTemplate t)
        {
            if (p.Combat.Down is not { Kind: KnockdownKind.Dead } || p.Map is null || p.Session.Player != p) return;
            Log.Info("pvp", $"'{p.Character.Name}' respawns in its home {h} ({why})");
            p.Session.Teleport(new Arrival(h.Instance, Inside(t), 0), Death.RespawnVia, template, Info(h).GuildName);
        }
        if (world.CachedCluster(h.Instance) is { } cached) Go(cached);
        else if (world.ResolveCluster(h.Instance) is { } load)
            load.ContinueWith(t => world.Post(() =>
            {
                if (t.IsCompletedSuccessfully) Go(t.Result);
                else if (p.Combat.Down is { Kind: KnockdownKind.Dead } && p.Session.Player == p)
                {
                    Log.Warn("pvp", $"home {h} of '{p.Character.Name}' not resolved: {t.Exception?.InnerException?.Message}; the city respawn");
                    _homes.Remove(p.Character.Id); // this once: the next respawn goes to the city
                    Death.Respawn(p, why);
                    _homes[p.Character.Id] = h.Id;
                }
            }), TaskScheduler.Default);
        else return false;
        return true;
    }

    // ---- access presets (AccessRightsChangeSettings 198)

    /// <summary>
    /// AccessRightsChangeSettings (198 {0 object, 3 preset, 4 groups, 5 their roles}) on a hideout's surface object or management by a manager
    /// of its guild (CHANGE_TRIBUTE/role management: owner and coowner = the master and Manage Hideouts): the preset (0 closed, 1 private, 2 public;
    /// 3 custom is read from the @_Everyone and @_Guild roles), saved, the AccessStatus again to the observers. Null = changed, else why not;
    /// false in <paramref name="mine"/> when the object is no hideout's.
    /// </summary>
    public string? SetAccess(Player p, long objectId, int preset, string[]? groups, string[]? roles, out bool mine)
    {
        var h = (p.Map?.Objects.GetValueOrDefault(objectId)) switch { HideoutObject o => o.Hideout, HideoutManagement m => m.Hideout, _ => null };
        mine = h is not null;
        if (h is null) return "no hideout object";
        if (world.Guilds.Of(p.Character.Id) is not { } g || g.Id != h.Guild || !Manages(g, p.Character.Id)) return "only the owner guild's managers change its access";
        HideoutAccess access;
        if (preset is >= 0 and <= 2) access = (HideoutAccess)preset;
        else
        {
            string Role(string group) => groups is null || roles is null ? "noaccess" : Array.IndexOf(groups, group) is >= 0 and var i && i < roles.Length ? roles[i] : "noaccess";
            access = Role("@_Everyone") != "noaccess" ? HideoutAccess.Public : Role("@_Guild") != "noaccess" ? HideoutAccess.Private : HideoutAccess.Closed;
        }
        h.Access = access;
        store?.SaveHideout(h);
        foreach (var map in new[] { world.Maps.Find(h.Cluster), world.Maps.Find(h.Instance) })
            if (map is not null)
                foreach (var o in map.Objects.Values.OfType<IAccessStatusObject>().OfType<WorldObject>().Where(o => o is HideoutObject x && x.Hideout == h || o is HideoutManagement y && y.Hideout == h).ToList())
                    map.SendToObservers(o, ((IAccessStatusObject)o).AccessStatus());
        Log.Info("world", $"'{p.Character.Name}' sets {h} to {access}");
        return null;
    }
}
