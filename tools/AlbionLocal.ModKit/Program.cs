// The content-pack tool (README.md next to this file; MODDING.md in Russian): own items and abilities for the local server and
// its client in game-local, made of the official assets. Run from the server folder:
//   dotnet run --project tools/AlbionLocal.ModKit -- <verb> [args] [options]
using AlbionLocal.Server;
using AlbionLocal.Server.Mods;

const string Usage = """
    modkit <verb> [args] [options]
      extract [files...]   decrypt GameData files to XML for reading (default items spells localization buildings); --out <dir>, --local
      check <packs...>     build in memory and report errors and warnings; writes nothing
      build <packs...>     apply the packs to the pristine official files and install them into game-local (--force after an official update)
      restore              put the official files back into game-local (hard links again)
      status               what is modded, by which pack, and whether the files are as built
      icons                once: item and spell icons out of the client's atlases (read only) into data/icon-cache for the web panel;
                           --assets <resources.assets> (default: the official install's), --out <dir>, --atlas <name,name>
    options: --server <dir> (the server folder; default: found from here), --game-local <GameData dir>, --official <GameData dir>, --mods <dir>
    """;

var positional = new List<string>();
var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) { positional.Add(args[i]); continue; }
    var key = args[i][2..];
    var eq = key.IndexOf('=');
    if (eq > 0) options[key[..eq]] = key[(eq + 1)..];
    else if (key is "force" or "local") options[key] = "true";
    else options[key] = i + 1 < args.Length ? args[++i] : null;
}
if (positional.Count == 0 || positional[0] is "help" or "-h" or "/?")
{
    Console.WriteLine(Usage);
    return positional.Count == 0 ? 1 : 0;
}

try
{
    var server = options.GetValueOrDefault("server") is { } s ? Path.GetFullPath(s) : FindServer();
    var local = Path.GetFullPath(options.GetValueOrDefault("game-local") ?? ServerConfig.DefaultClientGameData, server);
    var mods = Path.GetFullPath(options.GetValueOrDefault("mods") ?? "mods", server);
    ModPaths paths;
    if (options.GetValueOrDefault("official") is { } official)
    {
        var o = Path.GetFullPath(official, server);
        paths = new ModPaths(local, o, Path.GetFullPath(Path.Combine(o, "..", "..", "..")));
    }
    else paths = ModPaths.For(local) ?? throw new InvalidDataException(
        $"no official GameData next to {local} (expected ../game beside ../game-local); give it with --official <GameData dir>");
    if (!Directory.Exists(paths.Local)) throw new InvalidDataException($"game-local GameData {paths.Local} not found (--game-local)");
    var kit = new ModKit(paths, Console.Out);
    var verb = positional[0].ToLowerInvariant();
    var rest = positional.Skip(1).ToList();
    switch (verb)
    {
        case "extract":
            var outDir = Path.GetFullPath(options.GetValueOrDefault("out") ?? Path.Combine("data", "modkit-extract"), server);
            var n = kit.Extract(rest.Count > 0 ? rest : ["items", "spells", "localization", "buildings"], outDir, options.ContainsKey("local"));
            Console.WriteLine($"{n} file(s) extracted to {outDir} (read only: edit packs, not these files)");
            return n > 0 ? 0 : 1;
        case "check":
            if (rest.Count == 0) throw new InvalidDataException("check: name the pack(s), e.g. check example-blade");
            return kit.Check(ModPack.LoadAll(mods, rest)) ? 0 : 1;
        case "build":
            if (rest.Count == 0) throw new InvalidDataException("build: name the pack(s), e.g. build example-blade (all packs you want at once: a build replaces the last one)");
            return kit.Build(ModPack.LoadAll(mods, rest), options.ContainsKey("force")) is { Ok: true } ? 0 : 1;
        case "restore":
            kit.Restore();
            return 0;
        case "status":
            kit.Status();
            return 0;
        case "icons":
            var assets = Path.GetFullPath(options.GetValueOrDefault("assets") ?? Path.Combine(paths.OfficialRoot, "Albion-Online_Data", "resources.assets"), server);
            if (!File.Exists(assets)) throw new InvalidDataException($"{assets} not found (--assets <resources.assets>)");
            var iconDir = Path.GetFullPath(options.GetValueOrDefault("out") ?? Path.Combine("data", "icon-cache"), server);
            foreach (var game in new[] { paths.OfficialRoot, Path.GetFullPath(Path.Combine(paths.Local, "..", "..", "..")) })
                if (iconDir.StartsWith(Path.TrimEndingDirectorySeparator(game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || SafeFiles.RealPath(iconDir).StartsWith(SafeFiles.RealPath(game) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"--out {iconDir} is inside the game folder {game}: refused");
            var atlases = options.GetValueOrDefault("atlas")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var res = IconExtractor.Extract(assets, iconDir, atlases, Console.Out);
            foreach (var note in res.Notes) Console.WriteLine("note: " + note);
            Console.WriteLine($"{res.Icons} icon(s), {res.Bytes / 1024.0 / 1024.0:F1} MB, in {res.Time.TotalSeconds:F1} s → {iconDir} (derived from the client: git-ignored, never published)");
            return res.Icons > 0 ? 0 : 1;
        default:
            Console.WriteLine($"unknown verb {verb}\n{Usage}");
            return 1;
    }
}
catch (Exception e) when (e is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
{
    Console.WriteLine("error: " + e.Message);
    return 2;
}

// The server folder: upwards from here (or from the tool) to the folder with content/opcode-stubs.json.
static string FindServer()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "content", "opcode-stubs.json"))) return d.FullName;
    throw new InvalidDataException("the server folder (with content/opcode-stubs.json) not found from here: run in server/ or give --server <dir>");
}
