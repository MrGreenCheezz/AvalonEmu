using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Game;

/// <summary>
/// The Login response values the client lays its open world out by (stage 4b, analysis/work/stage4b/SPEC.md §2, §5.6):
/// the one place both the Login (Handlers/Login/LoginHandler.cs) and the cluster resolver
/// (<see cref="Data.LayoutSession.Login"/>) take them from, so that the resource nodes and dynamic templates the server
/// computes are the ones the client computes.
/// </summary>
public static class LoginWorld
{
    /// <summary>
    /// Login key 57 (ahw.bep): the server session, which the client also keeps as c3d.ac (GameTimeStamp, c3d.cl
    /// 0x180A5BAE0) and seeds the resource placement and the dynamic-template roll with (only its UTC date counts).
    /// The server start, constant for the whole run, as the official server sends it; never "now".
    /// </summary>
    public static long Key57 => GameTime.StartTicks;

    /// <summary>Login key 79 (string[], c3d+0x368): the event flags of prefabs, dynamic templates and active layers; none (sent as an empty array).</summary>
    public static string[] Flags => [];

    /// <summary>Login key 99, the feature switch table (<see cref="FeatureSwitches"/>); [112] DynamicTemplates is on, the client default.</summary>
    public static bool[] Features()
    {
        var features = FeatureSwitches.Defaults();
        features[171] = true; // PartyFinder: native advertisements/applications backed by Systems.PartyFinder.
        return features;
    }
}
