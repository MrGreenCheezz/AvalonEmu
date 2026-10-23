using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>The login connection's part of a cluster change (ROADMAP stage 3; "SPEC" = analysis/work/stage3/SPEC.md).</summary>
public sealed partial class LoginSession
{
    /// <summary>
    /// The game server of a cluster (op 17, client class tp, encrypted: 0 the ChangeCluster key 0, 1 never seen; SPEC §2.3,
    /// §4.3), asked on this login connection about 1 s after a ChangeCluster response, while the loading screen starts
    /// (ci7.br 0x180978d90, after ci7.x_8 cleared the stored address). Every cluster is on this server, so the answer is
    /// always {0: our game server address} with the request id echoed: the callback ci7.e(g7q) 0x180980520 stores key 0 and
    /// connects to it after the load (ci7.dt); an RC ≠ 0 or an empty key 0 would send the client to the login screen, a
    /// missing request id leave it on the loading screen. A cluster that is not the selected character's pending target is
    /// answered all the same, with a warning (the client asks only for the id the server gave it). The cluster is
    /// prefetched; normally the change resolved it already. The answer goes out unencrypted, as the official one (SPEC §1:
    /// every captured op-17 response is plaintext, the request encrypted), so it is sent here and the handler returns null.
    /// </summary>
    [Handles(OperationCodes.GetGameServerByCluster, Status = SessionStatus.Authed)]
    private AlbionResponse? GetGameServerByCluster(GetGameServerByClusterRequest req)
    {
        var cluster = req.mt ?? "";
        if (World is { } world)
        {
            var selected = world.Accounts.Selected(_account.Id) is { } id ? world.LoadCharacter(id) : null;
            var expected = selected is null ? null : TutorialInstances.ClientCluster(selected, world);
            if (cluster != expected)
                Log.Warn(Name, $"game server of cluster '{cluster}' asked, but the selected character is bound for '{expected ?? "(none selected)"}'; answered anyway");
            else Log.Info(Name, $"game server of cluster {cluster}: {config.GameServerAddress}");
            // Private instance ids are protocol identities, never world.xml lookup keys.
            if (selected is not null && cluster == expected) world.PrefetchCluster(selected.Cluster);
            else if (cluster.Length > 0 && !cluster.StartsWith("@TUTORIALSINGLE@", StringComparison.Ordinal)) world.PrefetchCluster(cluster);
        }
        else Log.Info(Name, $"game server of cluster {cluster}: {config.GameServerAddress}");
        // [0] "ip:port" → ci7+0x118 → c32.i(string); 255 echoed (RequestId of this request).
        SendResponse(OperationCodes.GetGameServerByCluster, new GetGameServerByClusterResponse { aw5 = config.GameServerAddress }, RequestId, encrypted: false);
        return null;
    }
}
