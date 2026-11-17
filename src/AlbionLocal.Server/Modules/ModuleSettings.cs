using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// Where a module's settings live (as AzerothCore's conf/*.conf.dist and configs/modules/): the defaults it ships with in conf/&lt;id&gt;.jsonc.dist, the server
/// owner's copy in config/modules/&lt;id&gt;.jsonc, written once from the defaults and never overwritten. The owner's copy may say "Enabled": false.
/// </summary>
internal static class ModuleSettings
{
    public const string EnabledKey = "Enabled";

    public static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The defaults file and the owner's copy (with no config folder, the copy's path is the defaults' own).</summary>
    public static (string Dist, string Own) Files(ModuleManifest m, string configPath)
    {
        var dist = Path.Combine(m.Directory, "conf", m.Id + ".jsonc.dist");
        return (dist, configPath.Length == 0 ? dist : Path.Combine(configPath, m.Id + ".jsonc"));
    }

    /// <summary>Writes the owner's copy when it is missing and the module ships defaults. A failure is logged, the defaults then apply.</summary>
    public static void EnsureOwnCopy(ModuleManifest m, string configPath)
    {
        var (dist, own) = Files(m, configPath);
        if (dist == own || File.Exists(own) || !File.Exists(dist)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(own)!);
            File.Copy(dist, own);
            Photon.Log.Info("modules", $"{m.Id}: settings written to {own}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Photon.Log.Warn("modules", $"{m.Id}: {own} not written: {e.Message}"); }
    }

    /// <summary>Whether the owner's copy (or, without one, the defaults) turns the module off ("Enabled": false).</summary>
    public static bool DisabledByOwner(ModuleManifest m, string configPath)
    {
        var (dist, own) = Files(m, configPath);
        foreach (var file in new[] { own, dist })
        {
            if (!File.Exists(file)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: Jsonc) is JsonObject o
                    && o.FirstOrDefault(p => p.Key.Equals(EnabledKey, StringComparison.OrdinalIgnoreCase)).Value is { } v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                    return !v.GetValue<bool>();
            }
            catch (Exception e) when (e is JsonException or IOException) { Photon.Log.Error("modules", $"{file}: {e.Message}"); }
            return false;
        }
        return false;
    }
}
