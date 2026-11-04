using System.Text.Json;
using System.Text.Json.Nodes;
using AlbionLocal.Modules;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// What --check-modules and .modules doctor run: every module's manifest, API version, dependencies, sources and settings, each problem with what to do about it.
/// Offline: no port, save or world is touched, nothing is built unless asked.
/// </summary>
public static class ModuleDoctor
{
    /// <summary>Writes the report to <paramref name="output"/>; the number of errors found (warnings do not count).</summary>
    public static int Run(ServerConfig config, TextWriter output, bool build = false, string? binRoot = null)
    {
        var options = config.Modules;
        var errors = 0;
        void Problem(string text)
        {
            errors++;
            output.WriteLine("  ERROR " + text);
        }
        output.WriteLine($"Modules: {(options.Enabled ? "on" : "off (Modules:Enabled)")}, folder {options.Path}, API {ModuleApi.Version}");
        var problems = new List<string>();
        var found = ModuleManifest.Discover(options.Path, problems.Add);
        foreach (var p in problems) Problem(p);
        if (found.Count == 0 && problems.Count == 0) output.WriteLine($"  no modules (make one with: AlbionLocal.Server --new-module mod-x)");
        var order = ModuleHost.Ordered(found);
        foreach (var m in order)
        {
            var before = errors;
            var off = options.List.Count > 0 ? !options.List.Contains(m.Id, StringComparer.OrdinalIgnoreCase) : !m.Enabled;
            output.WriteLine($"{m.Id} {m.Version}{(off ? " (not enabled)" : ModuleSettings.DisabledByOwner(m, options.ConfigPath) ? " (turned off in its settings)" : "")}{(m.CoreAccess ? " (core access: may break with a server update)" : "")}");
            foreach (var w in m.Warnings()) output.WriteLine("  warning " + w);
            if (!ModuleApi.Supports(m.Api, out var api)) Problem(api!);
            foreach (var r in m.Requires)
                if (!found.Any(f => f.Id.Equals(r, StringComparison.OrdinalIgnoreCase)))
                    Problem($"it requires {r}, which is not in {options.Path}{(ModuleHost.Nearest(r, found.Select(f => f.Id)) is { } near ? $" (did you mean {near}?)" : "")}");
            if (m.Assembly.Length > 0)
            {
                if (!File.Exists(Path.GetFullPath(Path.Combine(m.Directory, m.Assembly)))) Problem($"\"assembly\": {m.Assembly} is not in its folder");
            }
            else if (!m.Sources().Any() && Directory.GetFiles(m.Directory, "*.dll").Length != 1)
                Problem("no *.cs sources and no DLL in its folder: add a class deriving from Module or a *Script");
            CheckSettings(m, options.ConfigPath, output, Problem);
            if (build && errors == before && binRoot is not null)
            {
                var (dll, why) = ModuleBuilder.Resolve(m, binRoot, build: true);
                if (dll is null) Problem(why!);
                else output.WriteLine("  builds");
            }
            if (errors == before) output.WriteLine("  ok");
        }
        output.WriteLine(errors == 0 ? "No problems found." : $"{errors} problem(s) found.");
        return errors;
    }

    private static void CheckSettings(ModuleManifest m, string configPath, TextWriter output, Action<string> problem)
    {
        var (dist, own) = ModuleSettings.Files(m, configPath);
        JsonObject? Read(string file)
        {
            if (!File.Exists(file)) return null;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file), documentOptions: ModuleSettings.Jsonc) is JsonObject o) return o;
                problem($"{file} is not a JSON object");
            }
            catch (Exception e) when (e is JsonException or IOException) { problem($"{file}: {e.Message}"); }
            return null;
        }
        var defaults = Read(dist);
        if (own == dist || Read(own) is not { } mine || defaults is null) return;
        foreach (var (key, _) in mine)
            if (!key.Equals(ModuleSettings.EnabledKey, StringComparison.OrdinalIgnoreCase) && !defaults.Any(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                output.WriteLine($"  warning {own}: unknown key \"{key}\"{(ModuleHost.Nearest(key, defaults.Select(d => d.Key)) is { } near ? $" (did you mean \"{near}\"?)" : "")}");
    }
}
