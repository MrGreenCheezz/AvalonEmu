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

    /// <summary>
    /// The feature switch table (<see cref="FeatureSwitches"/>) of Login key 99 and FeatureSwitchInfo (event 503) key 0: the official
    /// table (stageR switches SPEC §2) with [151] off (<see cref="FeatureSwitches.ServerDefaultOff"/>), then [171] PartyFinder from
    /// Party:Finder (default true, as official), then Client:FeatureSwitches (<paramref name="overrides"/>), which win over Party:Finder.
    /// [112] DynamicTemplates stays on whatever the config says (the config refuses it): <see cref="Data.LayoutSession.Login"/> reads it.
    /// </summary>
    public static bool[] Features(bool partyFinder = true, IReadOnlyDictionary<int, bool>? overrides = null)
    {
        var flags = FeatureSwitches.ServerDefaults();
        flags[FeatureSwitches.PartyFinder] = partyFinder;
        if (overrides is not null)
            foreach (var (i, on) in overrides) flags[i] = on;
        return flags;
    }

    /// <summary>The table this config sends (<see cref="Features(bool, IReadOnlyDictionary{int, bool}?)"/>).</summary>
    public static bool[] Features(ServerConfig config) => Features(config.PartyFinderEnabled, config.FeatureSwitchOverrides);

    /// <summary>
    /// FeatureSwitchInfo (event 503) as the official server sends it after every Join response: key 0 the table, key 4 the VFX
    /// limit overrides (<see cref="FeatureSwitches.OfficialVfxLimits"/>); keys 1-3 and 5 left out (none in the captures).
    /// </summary>
    public static FeatureSwitchInfoEvent FeatureSwitchInfo(ServerConfig config) => new()
    {
        b91 = Features(config),
        b95 = FeatureSwitches.OfficialVfxLimits,
    };

    /// <summary>For the log: where this config's table differs from the official one ("" when it does not).</summary>
    public static string Describe(ServerConfig config)
    {
        var official = FeatureSwitches.Official();
        var mine = Features(config);
        return string.Join(", ", Enumerable.Range(0, FeatureSwitches.Count).Where(i => mine[i] != official[i])
            .Select(i => $"[{i}] {FeatureSwitches.Names[i]} {(mine[i] ? "on" : "off")}"));
    }
}
