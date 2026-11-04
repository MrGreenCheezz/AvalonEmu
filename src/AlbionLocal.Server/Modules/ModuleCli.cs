using System.Reflection;
using System.Text;
using System.Xml.Linq;
using AlbionLocal.Modules;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// The command-line side of the modules. All offline: no port or save is touched.
/// --new-module &lt;id&gt; makes a module to start from; --modules-build [id...] [--force] builds them; --check-modules [--build] checks them;
/// --modules-docs &lt;file&gt; writes the hook reference from the API's own documentation; --modules-api-baseline &lt;file&gt; writes the snapshot of the public API.
/// </summary>
public static class ModuleCli
{
    /// <summary>Runs the module command in <paramref name="args"/>: its exit code, or null when there is none.</summary>
    public static int? Run(string[] args, ServerConfig config)
    {
        string? After(string flag) => args.SkipWhile(a => a != flag).Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        var bin = Path.Combine(config.SavesPath, "modules-bin");
        if (args.Contains("--check-modules")) return ModuleDoctor.Run(config, Console.Out, build: args.Contains("--build"), bin) == 0 ? 0 : 1;
        if (args.Contains("--new-module"))
        {
            if (After("--new-module") is not { } id)
            {
                Console.Error.WriteLine("--new-module needs an id: --new-module mod-my-feature");
                return 2;
            }
            return NewModule(config.Modules.Path, id);
        }
        if (args.Contains("--modules-build"))
        {
            var names = args.SkipWhile(a => a != "--modules-build").Skip(1).TakeWhile(a => !a.StartsWith('-')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = ModuleManifest.Discover(config.Modules.Path);
            var failed = 0;
            foreach (var m in ModuleHost.Ordered(found).Where(m => names.Count == 0 || names.Contains(m.Id)))
            {
                var (dll, error) = ModuleBuilder.Resolve(m, bin, build: true, force: args.Contains("--force"));
                Console.WriteLine(dll is not null ? $"OK    {m.Id}: {dll}" : $"FAIL  {m.Id}: {error}");
                if (dll is null) failed++;
            }
            if (found.Count == 0) Console.WriteLine($"no modules in {config.Modules.Path}");
            return failed == 0 ? 0 : 1;
        }
        if (args.Contains("--modules-docs"))
        {
            if (After("--modules-docs") is not { } file) return Usage("--modules-docs needs a file");
            File.WriteAllText(file, HookReference(), new UTF8Encoding(false));
            Console.WriteLine($"written {file}");
            return 0;
        }
        if (args.Contains("--modules-api-baseline"))
        {
            if (After("--modules-api-baseline") is not { } file) return Usage("--modules-api-baseline needs a file");
            File.WriteAllLines(file, ModuleApiSurface.Lines(), new UTF8Encoding(false));
            Console.WriteLine($"written {file}");
            return 0;
        }
        return null;
    }

    private static int Usage(string text)
    {
        Console.Error.WriteLine(text);
        return 2;
    }

    /// <summary>Makes modules/&lt;id&gt;: a manifest, a first script, default settings, a project for the IDE. Refuses an existing folder.</summary>
    public static int NewModule(string modulesPath, string id)
    {
        if (id.IndexOfAny(['/', '\\', ':', ' ', '.']) >= 0)
        {
            Console.Error.WriteLine($"'{id}' cannot be a module id: letters, digits and - only (mod-my-feature)");
            return 2;
        }
        var dir = Path.Combine(modulesPath, id);
        if (Directory.Exists(dir))
        {
            Console.Error.WriteLine($"{dir} exists already");
            return 1;
        }
        var cls = string.Concat(id.Split('-', '_').Where(p => p.Length > 0 && p != "mod").Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        if (cls.Length == 0 || char.IsDigit(cls[0])) cls = "My" + cls;
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, "conf"));
        File.WriteAllText(Path.Combine(dir, ModuleManifest.FileName), $$"""
            // What the server needs to know about this module (docs/modules/README.md).
            {
              "id": "{{id}}",
              "version": "1.0.0",
              "author": "",
              "description": "",
              "api": "{{ModuleApi.Version}}"
              // "requires": ["mod-other"],     modules that must be loaded first
              // "loadAfter": ["mod-other"],    load after it when it is there
              // "coreAccess": true             use the server's own classes directly: everything is reachable, nothing is promised to stay
            }

            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "conf", id + ".jsonc.dist"), """
            // The defaults of this module. The server copies this file once to config/modules/ for the server owner to edit.
            {
              "Enabled": true,
              "Greeting": "Hello, {name}!"
            }

            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "src", cls + ".cs"), $$"""
            using AlbionLocal.Modules;

            namespace {{cls}}Module;

            // A script: derive from PlayerScript, CombatScript, SpellScript, MobScript, ItemScript..., type `override` and the IDE lists the hooks.
            // The server finds it by itself. Reload it in the game with: .modules reload {{id}}
            public sealed class {{cls}}Player : PlayerScript
            {
                public override void OnLogin(PlayerLoginArgs e) =>
                    e.Player.SendMessage(Config.Get("Greeting", "Hello, {name}!").Replace("{name}", e.Player.Name));
            }

            // Stop or change a hit: every OnBefore* hook has Cancel(), the values may be set.
            // public sealed class {{cls}}Combat : CombatScript
            // {
            //     public override void OnBeforeDamage(BeforeDamageArgs e)
            //     {
            //         if (e.Target is IPlayer { IsBot: false } && e.Attacker is IPlayer) e.Cancel();
            //     }
            // }

            """, new UTF8Encoding(false));
        var api = Path.GetRelativePath(dir, ModuleBuilder.ApiAssembly);
        File.WriteAllText(Path.Combine(dir, id + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <!--
                For the IDE only (IntelliSense, a local build to check): the server builds this folder's *.cs with a project of its own, whatever this file says.
                AlbionModuleApi: the API assembly the hints come from; build the server solution first if it is not there.
              -->
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <EnableDynamicLoading>true</EnableDynamicLoading>
                <NuGetAudit>false</NuGetAudit>
                <AlbionModuleApi Condition="'$(AlbionModuleApi)' == ''">{{api}}</AlbionModuleApi>
              </PropertyGroup>

              <ItemGroup>
                <Reference Include="AlbionLocal.Modules.Api">
                  <HintPath>$(AlbionModuleApi)</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>

            </Project>

            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, ".gitignore"), "bin/\nobj/\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "README.md"), $"# {id}\n\nWhat this module does, its settings and its commands.\n", new UTF8Encoding(false));
        Console.WriteLine($"""
            made {dir}
              {Path.Combine("src", cls + ".cs")}  your first script
              {Path.Combine("conf", id + ".jsonc.dist")}  its settings
              {id}.csproj  open this in your IDE for the hints
            Start the server (it builds and loads the module), or in the game: .modules reload {id}
            """);
        return 0;
    }

    /// <summary>The hook reference as Markdown: every *Script class with its hooks and what each one says about itself.</summary>
    public static string HookReference()
    {
        var api = typeof(ModuleScript).Assembly;
        var docs = ReadDocs(api);
        var sb = new StringBuilder();
        sb.Append("# Module hooks\n\nGenerated by `AlbionLocal.Server --modules-docs`; do not edit. API ").Append(ModuleApi.Version).Append(".\n");
        sb.Append("\nEvery hook is a method to override in the class named here and a lambda registration under `Hooks`: `CombatScript.OnBeforeDamage` is also `Hooks.Combat.OnBeforeDamage(e => ...)`.\n");
        foreach (var t in api.GetExportedTypes().Where(t => t.IsSubclassOf(typeof(ModuleScript)) && t.IsAbstract).OrderBy(t => t.MetadataToken))
        {
            var hooks = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttribute<HookAttribute>() is not null).OrderBy(m => m.MetadataToken).ToList();
            if (hooks.Count == 0) continue;
            sb.Append("\n## ").Append(t.Name).Append("\n\n");
            if (docs.GetValueOrDefault("T:" + t.FullName) is { } summary) sb.Append(summary).Append("\n\n");
            sb.Append("| Hook | Args | What |\n|---|---|---|\n");
            foreach (var m in hooks)
            {
                var args = m.GetParameters()[0].ParameterType;
                var doc = docs.GetValueOrDefault($"M:{t.FullName}.{m.Name}({args.FullName})") ?? "";
                sb.Append("| `").Append(m.Name).Append("` | `").Append(args.Name).Append("` | ").Append(doc.Replace("|", "\\|")).Append(" |\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>The summaries of the API's XML documentation file, by member id, as plain text.</summary>
    private static Dictionary<string, string> ReadDocs(Assembly api)
    {
        var docs = new Dictionary<string, string>();
        var file = Path.ChangeExtension(api.Location, ".xml");
        if (!File.Exists(file)) return docs;
        foreach (var member in XDocument.Load(file).Descendants("member"))
        {
            if (member.Attribute("name")?.Value is not { } name || member.Element("summary") is not { } summary) continue;
            var text = new StringBuilder();
            foreach (var node in summary.DescendantNodes())
                if (node is XText t) text.Append(t.Value);
                else if (node is XElement { Name.LocalName: "see" or "paramref" } e)
                    text.Append('`').Append((e.Attribute("cref")?.Value ?? e.Attribute("name")?.Value ?? "").Split('(')[0].Split('.', ':')[^1]).Append('`');
                else if (node is XElement { Name.LocalName: "c" }) text.Append('`');
            docs[name] = string.Join(" ", text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
        return docs;
    }
}

/// <summary>The public surface of the module API, one member per line, sorted: what docs/modules/api-baseline.txt holds and the contract test compares.</summary>
public static class ModuleApiSurface
{
    /// <summary>Every public type and member of the API assembly.</summary>
    public static IEnumerable<string> Lines()
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var lines = new List<string>();
        foreach (var t in typeof(ModuleScript).Assembly.GetExportedTypes())
        {
            var name = Name(t);
            lines.Add($"{(t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class")} {name}{(t.BaseType is { } b && b != typeof(object) && !t.IsEnum && !t.IsValueType ? " : " + Name(b) : "")}");
            if (t.IsEnum)
            {
                foreach (var v in t.GetFields(BindingFlags.Public | BindingFlags.Static)) lines.Add($"{name}.{v.Name} = {Convert.ToInt64(v.GetRawConstantValue())}");
                continue;
            }
            foreach (var m in t.GetMembers(Declared))
            {
                var line = m switch
                {
                    MethodInfo { IsSpecialName: false } x when Visible(x) => $"{name}.{x.Name}{(x.IsGenericMethod ? $"`{x.GetGenericArguments().Length}" : "")}({string.Join(", ", x.GetParameters().Select(Parameter))}): {Name(x.ReturnType)}",
                    ConstructorInfo x when Visible(x) && !x.IsStatic => $"{name}.ctor({string.Join(", ", x.GetParameters().Select(Parameter))})",
                    PropertyInfo x when (x.GetMethod is { } g && Visible(g)) || (x.SetMethod is { } s && Visible(s)) => $"{name}.{x.Name}: {Name(x.PropertyType)} {{{(x.GetMethod is { } g2 && Visible(g2) ? " get;" : "")}{(x.SetMethod is { } s2 && Visible(s2) ? " set;" : "")} }}",
                    FieldInfo { IsPublic: true } x => $"{name}.{x.Name}: {Name(x.FieldType)}",
                    _ => null,
                };
                if (line is not null) lines.Add(line);
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static bool Visible(MethodBase m) => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly;

    private static string Parameter(ParameterInfo p) => Name(p.ParameterType);

    private static string Name(Type t)
    {
        if (t.IsGenericParameter) return t.Name;
        if (t.IsArray) return Name(t.GetElementType()!) + "[]";
        if (Nullable.GetUnderlyingType(t) is { } u) return Name(u) + "?";
        var name = t.Name;
        if (t.IsGenericType) name = name[..name.IndexOf('`')] + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
        return name;
    }
}
