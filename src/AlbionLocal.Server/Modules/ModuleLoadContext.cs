using System.Reflection;
using System.Runtime.Loader;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// One module's collectible load context: the module's assembly and its own dependencies (next to it, or in its folder's lib/), read from bytes so the files
/// stay free for the next build; the API assembly and the framework come from the server's (default) context, so <see cref="AlbionLocal.Modules.ModuleScript"/> is
/// the same type on both sides. A module without "coreAccess" never sees the server's own assemblies: a reference to them resolves to nothing. A module it
/// "requires" is shared with it, so it can use that module's public types.
/// </summary>
internal sealed class ModuleLoadContext(string name, string dll, string moduleDir, bool coreAccess, Func<AssemblyName, Assembly?> required) : AssemblyLoadContext($"module:{name}", isCollectible: true)
{
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase) { "AlbionLocal.Modules.Api" };

    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    {
        "AlbionLocal.Server", "AlbionLocal.Photon", "AlbionLocal.Protocol", "Microsoft.Data.Sqlite",
    };

    /// <summary>The module's assembly, from bytes (and its pdb when there is one, for line numbers in its stack traces).</summary>
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
        if (Core.Contains(n))
            return coreAccess ? null : throw new FileNotFoundException($"module {name}: {n} is the server's own assembly; set \"coreAccess\": true in its module.jsonc to use it");
        if (required(assemblyName) is { } shared) return shared;
        foreach (var dir in new[] { Path.GetDirectoryName(dll)!, moduleDir, Path.Combine(moduleDir, "lib") })
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
