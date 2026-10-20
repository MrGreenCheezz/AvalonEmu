using System;
using System.Linq;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace HelloMod;

/// <summary>
/// Step 1 of the BepInEx compatibility check: proves the chainloader reaches a plugin and reports what the
/// interop layer generated. It touches no game type, so it can't break the client.
/// </summary>
[BepInPlugin("albionlocal.hello", "AlbionLocal Hello", "0.1.0")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo("Hello from AlbionLocal: BepInEx IL2CPP chainloader reached the plugin.");
        Log.LogInfo($"Runtime {Environment.Version}, 64-bit process: {Environment.Is64BitProcess}");
        var interop = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? "")
            .Where(n => n.StartsWith("Il2Cpp", StringComparison.Ordinal) || n.StartsWith("UnityEngine", StringComparison.Ordinal) || n == "Assembly-CSharp")
            .OrderBy(n => n).ToList();
        Log.LogInfo($"Loaded interop assemblies: {interop.Count} ({string.Join(", ", interop.Take(12))}{(interop.Count > 12 ? ", ..." : "")})");
    }
}
