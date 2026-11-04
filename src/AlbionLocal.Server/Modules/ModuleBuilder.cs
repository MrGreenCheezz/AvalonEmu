using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// Turns a module folder into a DLL path (docs/modules/README.md "Building"): a prebuilt DLL as it is; C# sources are built with the
/// local .NET SDK (<c>dotnet build</c>, offline: a module project has no package references) from a project the server writes into
/// data/modules-bin/&lt;name&gt;/build/ (the module's own .csproj, if any, is only for the IDE), against the running server's
/// AlbionLocal.Modules.dll, into data/modules-bin/&lt;name&gt;/out/. A build is skipped when nothing changed since the last one
/// (sources, the API, the project template). Thread-safe; slow (seconds): never call it on the world thread except at the start.
/// </summary>
public static class ModuleBuilder
{
    /// <summary>Raised when the generated project changes, so every module is built again once.</summary>
    private const string TemplateVersion = "3";

    private static readonly object Gate = new();

    /// <summary>How long one build may take.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The API assembly the modules compile against: the one this server runs with.</summary>
    public static string ApiAssembly => typeof(AlbionLocal.Modules.ModuleScript).Assembly.Location;

    /// <summary>
    /// The DLL of <paramref name="m"/>: (path, null) or (null, why not). <paramref name="build"/> false: sources are not built (a DLL built
    /// before is still used). <paramref name="force"/>: build even when up to date.
    /// </summary>
    public static (string? Dll, string? Error) Resolve(ModuleManifest m, string binRoot, bool build, bool force = false)
    {
        if (m.Assembly.Length > 0)
        {
            var dll = Path.GetFullPath(Path.Combine(m.Directory, m.Assembly));
            return File.Exists(dll) ? (dll, null) : (null, $"its assembly {m.Assembly} is not in {m.Directory}");
        }
        var sources = m.Sources().ToList();
        if (sources.Count == 0)
        {
            var dlls = Directory.GetFiles(m.Directory, "*.dll").Where(f => !Path.GetFileName(f).Equals("AlbionLocal.Modules.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            return dlls.Count == 1 ? (dlls[0], null)
                : (null, dlls.Count == 0 ? "no *.cs sources and no DLL in its folder" : "several DLLs and no \"assembly\" in module.json to name one");
        }
        var root = Path.Combine(binRoot, m.Name);
        var outDir = Path.Combine(root, "out");
        var output = Path.Combine(outDir, AssemblyName(m) + ".dll");
        var stamp = Stamp(m, sources, binRoot);
        var stampFile = Path.Combine(root, "build.stamp");
        lock (Gate)
        {
            var upToDate = File.Exists(output) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp;
            if (upToDate && !force) return (output, null);
            if (!build) return File.Exists(output) ? (output, null) : (null, "it has sources but Modules:Build is false and it was never built");
            var projDir = Path.Combine(root, "build");
            Directory.CreateDirectory(projDir);
            var project = Path.Combine(projDir, AssemblyName(m) + ".csproj");
            File.WriteAllText(project, Project(m, binRoot));
            if (File.Exists(stampFile)) File.Delete(stampFile);
            var (ok, log) = Run(project, outDir);
            if (!ok) return (null, "build failed:\n" + log + Hint(m, log));
            if (!File.Exists(output)) return (null, $"the build made no {output}");
            File.WriteAllText(stampFile, stamp);
            Photon.Log.Info("modules", $"{m.Name}: built {output}");
            return (output, null);
        }
    }

    /// <summary>What a failed build's errors usually mean.</summary>
    private static string Hint(ModuleManifest m, string log)
    {
        if (!m.CoreAccess && (log.Contains("AlbionLocal.Server", StringComparison.Ordinal) || log.Contains("'Server' does not exist in the namespace 'AlbionLocal'", StringComparison.Ordinal)))
            return "\nhint: this module uses the server's own classes; add \"coreAccess\": true to its module.jsonc (nothing promises they stay as they are)";
        if (log.Contains("'override'", StringComparison.Ordinal) || log.Contains("CS0115", StringComparison.Ordinal))
            return "\nhint: a hook of that name does not exist in this base class; in an IDE, type `override` to see the list, or run .modules hooks in the game";
        return "";
    }

    /// <summary>The assembly name of a built module.</summary>
    public static string AssemblyName(ModuleManifest m) =>
        "AlbionModule." + new string([.. m.Name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')]);

    /// <summary>The generated project: the folder's *.cs (not bin/ obj/), lib/*.dll as references, the API not copied.</summary>
    private static string Project(ModuleManifest m, string binRoot)
    {
        static string X(string s) => SecurityElement.Escape(s.Replace('\\', '/'));
        var src = X(m.Directory.TrimEnd('\\', '/'));
        var core = string.Join("\n", (m.CoreAccess ? CoreAssemblies : []).Concat(Required(m, binRoot)).Select(a => $"""
                <Reference Include="{X(Path.GetFileNameWithoutExtension(a))}">
                  <HintPath>{X(a)}</HintPath>
                  <Private>false</Private>
                </Reference>
"""));
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Written by the AlbionLocal server (Modules/ModuleBuilder.cs) for module {X(m.Name)}; edits are overwritten. -->
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AssemblyName>{X(AssemblyName(m))}</AssemblyName>
                <RootNamespace>{X(AssemblyName(m))}</RootNamespace>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <EnableDynamicLoading>true</EnableDynamicLoading>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
                <NuGetAudit>false</NuGetAudit>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{src}/**/*.cs" Exclude="{src}/bin/**;{src}/obj/**" />
                <Reference Include="AlbionLocal.Modules.Api">
                  <HintPath>$(AlbionModuleApi)</HintPath>
                  <Private>false</Private>
                </Reference>
            {core}
                <Reference Include="{src}/lib/*.dll" />
              </ItemGroup>
            </Project>
            """;
    }

    /// <summary>The server's own assemblies a module with "coreAccess" compiles against: those this server runs with.</summary>
    private static IEnumerable<string> CoreAssemblies =>
        new[] { typeof(ModuleBuilder).Assembly, typeof(Photon.Log).Assembly, typeof(Protocol.AlbionSerializer).Assembly }.Select(a => a.Location).Where(l => l.Length > 0);

    /// <summary>The built DLLs of the modules <paramref name="m"/> requires: their public types are there for it.</summary>
    private static IEnumerable<string> Required(ModuleManifest m, string binRoot) =>
        m.Requires.Select(r => Path.Combine(binRoot, r, "out", "AlbionModule." + r + ".dll")).Where(File.Exists);

    /// <summary>What decides whether a build is needed: the template, the API's time and size, each source's path, time and size.</summary>
    private static string Stamp(ModuleManifest m, List<string> sources, string binRoot)
    {
        var sb = new StringBuilder(TemplateVersion).Append('|').Append(m.Directory);
        var api = new FileInfo(ApiAssembly);
        sb.Append('|').Append(api.LastWriteTimeUtc.Ticks).Append(':').Append(api.Length);
        foreach (var a in (m.CoreAccess ? CoreAssemblies : []).Concat(Required(m, binRoot)))
            {
                var fi = new FileInfo(a);
                sb.Append('|').Append(a).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(':').Append(fi.Length);
            }
        foreach (var f in sources.Concat(Directory.Exists(Path.Combine(m.Directory, "lib")) ? Directory.GetFiles(Path.Combine(m.Directory, "lib"), "*.dll") : []).Order(StringComparer.Ordinal))
        {
            var fi = new FileInfo(f);
            sb.Append('|').Append(Path.GetRelativePath(m.Directory, f)).Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(':').Append(fi.Length);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The dotnet host: the one running this process when it is dotnet, else DOTNET_HOST_PATH, else "dotnet" from PATH.</summary>
    private static string Dotnet()
    {
        if (Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return self;
        return Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
    }

    /// <summary>
    /// dotnet build of <paramref name="project"/> into <paramref name="outDir"/>: no node reuse and no compiler server, so no process outlives the
    /// build (and holds its output pipes); the output is kept for the error.
    /// </summary>
    private static (bool Ok, string Log) Run(string project, string outDir)
    {
        var psi = new ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(project)!,
        };
        foreach (var a in new[] { "build", project, "-c", "Release", "-o", outDir, "-nologo", "-v:q", "-nodeReuse:false",
                     "-p:UseSharedCompilation=false", "-p:AlbionModuleApi=" + ApiAssembly })
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        // A server started by `dotnet run` inherits the SDK's MSBuild variables; the child build picks its own.
        foreach (var v in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
            psi.Environment.Remove(v);
        Photon.Log.Info("modules", $"building {project}");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(Timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (false, $"dotnet build took longer than {Timeout.TotalSeconds:0} s");
        }
        var text = (stdout.Result + stderr.Result).Trim();
        var errors = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Contains(" error ", StringComparison.Ordinal) || l.Contains(": error", StringComparison.Ordinal)).Distinct().Take(20).ToList();
        return (p.ExitCode == 0, errors.Count > 0 ? string.Join("\n", errors) : text.Length > 4000 ? text[^4000..] : text);
    }
}
