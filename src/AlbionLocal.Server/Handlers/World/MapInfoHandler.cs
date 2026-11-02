using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    /// <summary>
    /// The selected cluster's real territory owners and hideouts. Installed 345319: ClusterMapBase.OnGetClusterInfo
    /// (RVA 114AC90) checks key 0 against the selected cluster before dispatching its modules; Process (114BF50)
    /// consumes the parallel territory arrays 3-16 and remote hideout arrays 42-58. Other sections remain empty.
    /// Querying a map does not load its simulation, create world objects, advance timers or save state.
    /// </summary>
    [Handles(OperationCodes.GetClusterMapInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse GetClusterMapInfo(GetClusterMapInfoRequest req)
    {
        var cluster = req.mr ?? "";
        var now = world.GameNow;
        var response = new GetClusterMapInfoResponse { ati = cluster, atj = now }.FillEmpty();
        if (world.Clusters?.WorldIfLoaded?.Find(cluster) is null) return response;

        if (world.Territories.Enabled)
        {
            var territories = world.Territories.All.Where(t => t.Cluster.Id == cluster).OrderBy(t => t.Id).ToArray();
            var owners = territories.Select(t => (Guild: world.Guilds.ById(t.Owner), Alliance: world.Alliances.OfGuild(t.Owner))).ToArray();
            var count = territories.Length;
            // av.o (642050) builds Guid + "@" + cluster; the map looks up that exact key, not a bare Guid.
            // Territory tooltip (16251F0) reads 6 as guild name and 7 as alliance TAG, and 14/15 as tower power/time.
            response.atl = [.. territories.Select(t => $"{t.Id:D}@{t.Cluster.Id}")];
            response.atm = [.. territories.Select(t => t.Owner)];
            response.atn = [.. owners.Select(o => o.Alliance?.Id ?? Guid.Empty)];
            response.ato = [.. territories.Select((t, i) => owners[i].Guild?.Name ?? t.OwnerName)];
            response.atp = [.. owners.Select(o => o.Alliance?.Tag ?? "")];
            // Avalon does not model siphoning boosters/energy, raid state or scheduled attacks yet. These mandatory
            // parallel slots retain the neutral values used by its monolith publisher; do not invent statistics/timers.
            response.atq = new byte[count]; // 8 active boosters
            response.atr = new byte[count]; // 9 maximum boosters
            response.ats = new long[count]; // 10 energy storage
            response.att = new byte[count]; // 11 current state
            response.atu = new long[count]; // 12 state/raid timer
            response.atv = new long[count]; // 13 upcoming attack
            var utc = new DateTime(now, DateTimeKind.Utc);
            response.atw = [.. territories.Select(t => ClusterMapPower(world.Territories.PowerAt(t, utc)))];
            response.atx = [.. territories.Select(_ => now)];
            response.aty = new long[count]; // 16 no additional territory value implemented
        }

        if (world.Hideouts.Enabled)
        {
            var hideouts = world.Hideouts.All.Where(h => h.Cluster == cluster).OrderBy(h => h.Placed).ThenBy(h => h.Id)
                .Select(h => (Hideout: h, Info: world.Hideouts.Info(h))).ToArray();
            var count = hideouts.Length;
            // Installed 345319 Process 114CD2A -> HideoutMapLabel.a; compare its live-object constructor 1466460
            // with NewHideoutObject's consumer BB2580. 44 is construction PHASE, 51 level, 52 shields. 53 is
            // NewHideoutObject key 29, NOT the construction-end time (key 18). Keep unimplemented flags/power
            // identical to NewHideoutObject rather than infer HQ/access/attack status from unrelated state.
            response.auo = [.. hideouts.Select(x => world.Hideouts.SurfaceId(x.Hideout))]; // 42 live surface object id
            response.aup = [.. hideouts.Select(x => x.Hideout.Position)];
            response.auq = [.. hideouts.Select(x => (int)x.Info.State.Phase)];
            response.aur = [.. hideouts.Select(x => (int)x.Info.State.Mode)];
            response.aus = new bool[count]; // 46 NewHideoutObject 16
            response.aut = [.. hideouts.Select(x => x.Hideout.Guild)];
            response.auu = [.. hideouts.Select(x => x.Info.GuildName)];
            response.auv = [.. hideouts.Select(x => x.Info.Alliance)];
            response.auw = [.. hideouts.Select(x => x.Info.AllianceName)];
            response.auy = [.. hideouts.Select(x => (int)x.Info.State.Level)];
            response.auz = [.. hideouts.Select(x => (int)x.Info.State.Shields)];
            response.au0 = new long[count]; // 53 NewHideoutObject 29
            response.au1 = new bool[count]; // 54 NewHideoutObject 23
            response.au2 = new bool[count]; // 55 NewHideoutObject 27
            response.au3 = new long[count]; // 56 NewHideoutObject 24 (power pool)
            response.au4 = [.. hideouts.Select(x => x.Info.State.Stamp)]; // 57 NewHideoutObject 25
            response.au5 = new bool[count]; // 58 NewHideoutObject 26
        }

        return response;
    }

    private static long ClusterMapPower(double power) => !double.IsFinite(power) || power <= 0 ? 0
        : power >= long.MaxValue / (double)Pricing.A7m ? long.MaxValue : (long)Math.Round(power * Pricing.A7m);
}
