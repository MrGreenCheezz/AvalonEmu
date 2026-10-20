using System.Reflection;
using System.Runtime.Loader;

namespace AlbionLocal.Server.Plugins;

/// <summary>
/// One plugin's collectible load context: the plugin assembly and its own dependencies (next to it, or in its folder's lib/), read
/// from bytes so the files stay free for the next build; the API assembly and the framework come from the server's (default) context,
/// so <see cref="AlbionLocal.Plugin.Api.IPlugin"/> is the same type on both sides. A plugin never sees the server's own assemblies:
/// a reference to them resolves to nothing.
/// </summary>
internal sealed class PluginLoadContext(string name, string dll, string pluginDir) : AssemblyLoadContext($"plugin:{name}", isCollectible: true)
{
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase) { "AlbionLocal.Plugin.Api" };

    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "AlbionLocal.Server", "AlbionLocal.Photon", "AlbionLocal.Protocol", "Microsoft.Data.Sqlite",
    };

    /// <summary>The plugin assembly, from bytes (and its pdb when there is one, for line numbers in its stack traces).</summary>
    public Assembly LoadMain()
    {
        var pdb = Path.ChangeExtension(dll, ".pdb");
        using var code = new MemoryStream(File.ReadAllBytes(dll));
        using var symbols = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
        return LoadFromStream(code, symbols);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } n || Shared.Contains(n)) return null; // the default context's
        if (Hidden.Contains(n)) throw new FileNotFoundException($"plugin {name}: {n} is the server's own assembly; plugins use only AlbionLocal.Plugin.Api");
        foreach (var dir in new[] { Path.GetDirectoryName(dll)!, pluginDir, Path.Combine(pluginDir, "lib") })
        {
            var path = Path.Combine(dir, n + ".dll");
            if (File.Exists(path))
            {
                using var s = new MemoryStream(File.ReadAllBytes(path));
                return LoadFromStream(s);
            }
        }
        return null; // the framework, from the default context
    }
}
