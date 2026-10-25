using System.Text.Json;
using AlbionLocal.Server;
using AlbionLocal.Server.Configuration;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

internal static class ConfigurationTest
{
    private static void Check(bool condition, string message) => AlbionTests.Check(condition, message);

    public static readonly (string Name, Action Body)[] All =
    [
        ("configuration: editable validation and application policies", Editable),
        ("configuration: required area files, ownership, arguments and paths", Files),
    ];

    private static void Reject(Action action, string label)
    {
        try { action(); }
        catch (Exception e) when (e is InvalidDataException or JsonException) { return; }
        throw new Exception($"configuration accepted {label}");
    }

    private static void Editable()
    {
        var empty = EditableConfiguration.Parse("{}");
        var explicitValue = EditableConfiguration.Parse("""{ "Rates": { "Fame": 1 } }""");
        Check(empty.Values.Count == 0 && explicitValue.Values.ContainsKey("Rates:Fame"), "editable values preserve omitted versus explicit options");
        Check(explicitValue.Validate(Environment.CurrentDirectory, ["--Rates:Fame=3"]).Rates.Fame == 3, "argument overrides explicit editable values");
        var arrays = EditableConfiguration.Parse("""{"World":{"EventLayers":["halloween", /* seasonal */]},"Guilds":{"Levels":[0,5,]}}""").Validate(Environment.CurrentDirectory);
        Check(arrays.EventLayers.SequenceEqual(["halloween"]) && arrays.GuildLevels.SequenceEqual([0L, 5L]), "JSON comments and trailing commas are supported inside option arrays");
        var nullable = EditableConfiguration.Parse("""{"Roads":{"Seed":null},"Bots":{"Rewards":{"LootProtectionSeconds":null}},"Hellgates":{"KnockdownSeconds":null}}""").Validate(Environment.CurrentDirectory);
        Check(nullable.Roads.Seed is null && nullable.Bots.LootProtectionSeconds is null && nullable.Hellgates.KnockdownSeconds is null,
            "documented nullable options retain explicit JSON null support");
        Reject(() => EditableConfiguration.Parse("""{"Paths":{"Saves":null}}""").Validate(Environment.CurrentDirectory), "null saves path");
        foreach (var json in new[] { "[]", """{"Rates":{"Fame":null}}""", """{"Rates":{"Fame":1,"fame":2}}""", """{"Rates":{"Fame":{}}}""", """{"Typo":{}}""", """{"World":{"EventLayers":[{"x":1,"X":2}]}}""" })
            Reject(() => EditableConfiguration.Parse(json).Validate(Environment.CurrentDirectory), json);
        Reject(() => EditableConfiguration.Parse("""{"World":{"TickMs":false}}""").Validate(Environment.CurrentDirectory), "wrong numeric type");
        Reject(() => EditableConfiguration.Parse("""{"Persistence":{"Provider":"PostgreSQL"}}""").Validate(Environment.CurrentDirectory), "unsupported provider");
        Reject(() => EditableConfiguration.Parse("""{"Unknown":{"Option":1}}""").Validate(Environment.CurrentDirectory), "unknown key");
        Check(EditableConfiguration.Parse("""{"Persistence":{"Provider":"sQlItE"}}""").Validate(Environment.CurrentDirectory).PersistenceProvider == "SQLite", "provider is normalized");
        Check(AlbionLocal.Server.Game.Rates.Names.All(name => ConfigurationOptions.Policy($"Rates:{name}") == ConfigurationApplicationPolicy.Reloadable)
            && ConfigurationOptions.Policy("Network:GamePort") == ConfigurationApplicationPolicy.RestartRequired
            && ConfigurationOptions.Policy("Rates:Typo") == ConfigurationApplicationPolicy.RestartRequired, "only existing rates are classified reloadable");
    }

    private static void Files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"albion-config-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "config");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in ConfigurationOptions.FileNames) File.WriteAllText(Path.Combine(directory, name), "{}");
            File.WriteAllText(Path.Combine(directory, "common.jsonc"), """
                // defaults
                {"Preset":"official", "Paths":{"Content":"assets"},
                 "Web":{"Enabled":true,"Port":8080},"Backup":{"EveryHours":6,"Keep":3},}
                """);
            File.WriteAllText(Path.Combine(directory, "world.jsonc"), """{"Tutorial":{"Opening":true},"Rates":{"Fame":2,"CombatFame":3}}""");
            var loaded = ServerConfiguration.Load(directory, ["--Rates:Fame=4"]);
            Check(loaded.Config.Web is { Enabled: true, Port: 8080 } && loaded.Config.Backup is { EveryHours: 6, Keep: 3 }
                && loaded.Sources["Web:Enabled"].EndsWith("common.jsonc") && loaded.Sources["Backup:Keep"].EndsWith("common.jsonc"),
                "existing web panel and backup settings load from the common area without starting services");
            Reject(() => ServerConfiguration.Load(directory, ["--Web:Port=5055"]), "web port conflicts with login");
            Check(loaded.Config.ContentPath == Path.Combine(root, "assets") && loaded.Config.SavesPath == Path.Combine(root, "data")
                && loaded.Config.LogFile == Path.Combine(root, "data", "server.log"), "all relative and default paths use parent of config directory");
            Check(loaded.Config.Preset == ServerPreset.Official && loaded.Config.TutorialOpening && loaded.Config.Rates.Fame == 4
                && loaded.Sources["Rates:Fame"].EndsWith("world.jsonc") && loaded.Overrides["Rates:Fame"] == "4", "files, source metadata and argument precedence");
            var summary = new StringWriter(); loaded.WriteSummary(summary);
            Check(summary.ToString().Contains("combat x12") && summary.ToString().Contains("Argument override: Rates:Fame=4"), "summary includes fame product and override");
            Check(!File.Exists(loaded.Config.DatabasePath) && !File.Exists(loaded.Config.LogFile), "load and summary create no saves or logs");
            Check(ServerConfiguration.ParseArguments([$"--config-dir={directory}", "--check-config"]).Directory == directory
                && ServerConfiguration.ParseArguments(["--config-dir", directory]).Directory == directory, "both config directory argument forms");
            Reject(() => ServerConfiguration.ParseArguments(["--config-dir"]), "missing selector value");
            Reject(() => ServerConfiguration.ParseArguments(["--config=legacy.json"]), "legacy main file selector");
            File.WriteAllText(Path.Combine(directory, "chat.jsonc"), """{"Rates":{"Fame":2}}""");
            Reject(() => ServerConfiguration.Load(directory, []), "wrong file owner");
            File.WriteAllText(Path.Combine(directory, "chat.jsonc"), """{"Network":{"ChatPort":5056}}""");
            Reject(() => ServerConfiguration.Load(directory, []), "port conflict");
            File.Delete(Path.Combine(directory, "chat.jsonc"));
            Reject(() => ServerConfiguration.Load(directory, []), "missing required file");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
