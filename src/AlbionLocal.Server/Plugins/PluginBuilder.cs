using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace AlbionLocal.Server.Plugins;

/// <summary>
/// Turns a plugin folder into a DLL path (server/plugins/README.md "Building"): a prebuilt DLL as it is; C# sources are built with the
/// local .NET SDK (<c>dotnet build</c>, offline: a plugin project has no package references) from a project the server writes into
/// data/plugins-bin/&lt;name&gt;/build/ (the plugin's own .csproj, if any, is only for the IDE), against the running server's
/// AlbionLocal.Plugin.Api.dll, into data/plugins-bin/&lt;name&gt;/out/. A build is skipped when nothing changed since the last one
/// (sources, the API, the project template). Thread-safe; slow (seconds): never call it on the world thread except at the start.
/// </summary>
public static class PluginBuilder
{
    /// <summary>Raised when the generated project changes, so every plugin is built again once.</summary>
    private const string TemplateVersion = "1";

    private static readonly object Gate = new();

    /// <summary>How long one build may take.</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The API assembly the plugins compile against: the one this server runs with.</summary>
    public static string ApiAssembly => typeof(AlbionLocal.Plugin.Api.IPlugin).Assembly.Location;

    /// <summary>
    /// The DLL of <paramref name="m"/>: (path, null) or (null, why not). <paramref name="build"/> false: sources are not built (a DLL built
    /// before is still used). <paramref name="force"/>: build even when up to date.
    /// </summary>
    public static (string? Dll, string? Error) Resolve(PluginManifest m, string binRoot, bool build, bool force = false)
    {
        if (m.Assembly.Length > 0)
        {
            var dll = Path.GetFullPath(Path.Combine(m.Directory, m.Assembly));
            return File.Exists(dll) ? (dll, null) : (null, $"its assembly {m.Assembly} is not in {m.Directory}");
        }
        var sources = m.Sources().ToList();
        if (sources.Count == 0)
        {
            var dlls = Directory.GetFiles(m.Directory, "*.dll").Where(f => !Path.GetFileName(f).Equals("AlbionLocal.Plugin.Api.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            return dlls.Count == 1 ? (dlls[0], null)
                : (null, dlls.Count == 0 ? "no *.cs sources and no DLL in its folder" : "several DLLs and no \"assembly\" in plugin.json to name one");
        }
        var root = Path.Combine(binRoot, m.Name);
        var outDir = Path.Combine(root, "out");
        var output = Path.Combine(outDir, AssemblyName(m) + ".dll");
        var stamp = Stamp(m, sources);
        var stampFile = Path.Combine(root, "build.stamp");
        lock (Gate)
        {
            var upToDate = File.Exists(output) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp;
            if (upToDate && !force) return (output, null);
            if (!build) return File.Exists(output) ? (output, null) : (null, "it has sources but Plugins:Build is false and it was never built");
            var projDir = Path.Combine(root, "build");
            Directory.CreateDirectory(projDir);
            var project = Path.Combine(projDir, AssemblyName(m) + ".csproj");
            File.WriteAllText(project, Project(m));
            if (File.Exists(stampFile)) File.Delete(stampFile);
            var (ok, log) = Run(project, outDir);
            if (!ok) return (null, "build failed:\n" + log);
            if (!File.Exists(output)) return (null, $"the build made no {output}");
            File.WriteAllText(stampFile, stamp);
            Photon.Log.Info("plugins", $"{m.Name}: built {output}");
            return (output, null);
        }
    }

    /// <summary>The assembly name of a built plugin.</summary>
    public static string AssemblyName(PluginManifest m) =>
        "AlbionPlugin." + new string([.. m.Name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_')]);

    /// <summary>The generated project: the folder's *.cs (not bin/ obj/), lib/*.dll as references, the API not copied.</summary>
    private static string Project(PluginManifest m)
    {
        static string X(string s) => SecurityElement.Escape(s.Replace('\\', '/'));
        var src = X(m.Directory.TrimEnd('\\', '/'));
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Written by the AlbionLocal server (Plugins/PluginBuilder.cs) for plugin {X(m.Name)}; edits are overwritten. -->
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
                <Reference Include="AlbionLocal.Plugin.Api">
                  <HintPath>$(AlbionPluginApi)</HintPath>
                  <Private>false</Private>
                </Reference>
                <Reference Include="{src}/lib/*.dll" />
              </ItemGroup>
            </Project>
            """;
    }

    /// <summary>What decides whether a build is needed: the template, the API's time and size, each source's path, time and size.</summary>
    private static string Stamp(PluginManifest m, List<string> sources)
    {
        var sb = new StringBuilder(TemplateVersion).Append('|').Append(m.Directory);
        var api = new FileInfo(ApiAssembly);
        sb.Append('|').Append(api.LastWriteTimeUtc.Ticks).Append(':').Append(api.Length);
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
                     "-p:UseSharedCompilation=false", "-p:AlbionPluginApi=" + ApiAssembly })
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        // A server started by `dotnet run` inherits the SDK's MSBuild variables; the child build picks its own.
        foreach (var v in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
            psi.Environment.Remove(v);
        Photon.Log.Info("plugins", $"building {project}");
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
