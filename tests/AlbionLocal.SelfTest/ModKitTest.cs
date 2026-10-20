using AlbionLocal.Server.Data;
using AlbionLocal.Server.Mods;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The modkit (tools/AlbionLocal.ModKit, analysis/work/stageN/modkit/SPEC.md) on a TEMP copy only: a stand-in official install
/// (copies of the needed files) and a stand-in game-local whose files are hard links to it, as the real game-local is. The
/// example pack mods/example-blade is built into it; the real game-local and the official install are never touched.
/// </summary>
public static class ModKitTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("modkit: example-blade builds into a temp game-local of hard links; the 'official' files stay unchanged, the modded items.bin has the blade", Build),
        ("modkit: the server loads the modded temp data (blade at the next item id with Clockwork Dash), the drops roll, restore links back", ServerSide),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static readonly string[] Files = [.. ClientData.FileNames, "buildings.bin", "localization.bin"];

    private sealed record Setup(string Root, ModPaths Paths, ModKit Kit, ModBuildResult? Result, Dictionary<string, string> OfficialBefore, string Output);

    private static readonly Lazy<Setup> Built = new(() =>
    {
        var root = NewDir();
        var official = Directory.CreateDirectory(Path.Combine(root, "game", "Albion-Online_Data", "StreamingAssets", "GameData")).FullName;
        var local = Directory.CreateDirectory(Path.Combine(root, "game-local", "Albion-Online_Data", "StreamingAssets", "GameData")).FullName;
        var source = ClientGameDataDir(); // the pristine data (only read)
        foreach (var f in Files)
        {
            File.Copy(Path.Combine(source, f), Path.Combine(official, f));
            SafeFiles.HardLink(Path.Combine(local, f), Path.Combine(official, f));
        }
        var build = ModBuilder.ReadBuild(source) ?? "unknown";
        File.WriteAllText(Path.Combine(root, "game", "version.txt"), build);
        File.WriteAllText(Path.Combine(root, "game-local", "version.txt"), build);
        var paths = ModPaths.For(local) ?? throw new Exception("ModPaths.For found no official folder beside the temp game-local");
        var before = Files.ToDictionary(f => f, f => SafeFiles.Sha256(Path.Combine(official, f)));
        var output = new StringWriter();
        var kit = new ModKit(paths, output);
        var pack = ModPack.Load(Path.Combine(DataDir(), "..", "mods", "example-blade"));
        var result = kit.Build([pack], force: false);
        return new Setup(root, paths, kit, result, before, output.ToString());
    });

    private static void Build()
    {
        var s = Built.Value;
        Check(s.Paths.Official.StartsWith(s.Root, StringComparison.OrdinalIgnoreCase), $"the temp official folder is used ({s.Paths.Official})");
        Check(s.Result is { Ok: true } && s.Result.Files.Keys.Order().SequenceEqual(["buildings.bin", "items.bin", "localization.bin", "spells.bin"]),
              $"built 4 files ({s.Output})");
        var changed = Files.Where(f => SafeFiles.Sha256(Path.Combine(s.Paths.Official, f)) != s.OfficialBefore[f]).ToList();
        Check(changed.Count == 0, $"the hard-linked 'official' files are unchanged ({string.Join(", ", changed)})");
        Check(SafeFiles.LinkCount(Path.Combine(s.Paths.Local, "items.bin")) == 1 && SafeFiles.LinkCount(Path.Combine(s.Paths.Official, "items.bin")) == 1
              && SafeFiles.LinkCount(Path.Combine(s.Paths.Local, "gamedata.bin")) == 2,
              "the modded file is unlinked (its own data), an untouched one is still a link");
        var blade = GameDataFile.Open(s.Paths.Local, "items.bin").Elements().FirstOrDefault(e => (string?)e.Attribute("uniquename") == "T6_MAIN_CLOCKWORKBLADE");
        Check(blade is not null && (string?)blade.Attribute("mesh") == "T6_MAIN_SWORD" && blade.Descendants("craftspell").Any(c => (string?)c.Attribute("uniquename") == "CLOCKWORK_DASH"),
              "the modded items.bin decrypts and holds the blade (broadsword model, Clockwork Dash)");
        var refused = false;
        try { SafeFiles.ReplaceUnlinked(Path.Combine(s.Paths.Official, "items.bin"), [1, 2, 3], s.Paths.OfficialRoot); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && SafeFiles.Sha256(Path.Combine(s.Paths.Official, "items.bin")) == s.OfficialBefore["items.bin"], "a write into the official folder is refused");
    }

    private static void ServerSide()
    {
        var s = Built.Value;
        try
        {
            Check(s.Result is { Ok: true, Items: { } items, Spells: { } spells }, "built");
            var data = ClientData.Load(s.Paths.Local); // ModState.Verify runs in it
            var blade = data.Items.Find("T6_MAIN_CLOCKWORKBLADE");
            var bladeId = s.Result!.Items!.PristineCount + 1;
            var dash = data.Spells!.IdOf("CLOCKWORK_DASH");
            Check(blade?.Index == bladeId && s.Result.Items.New["T6_MAIN_CLOCKWORKBLADE"] == bladeId,
                  $"the blade is item {blade?.Index} = official count + 1 ({bladeId}), the ids before it unchanged");
            Check(dash == s.Result.Spells!.PristineCount && data.Spells.SlotsOf("T6_MAIN_CLOCKWORKBLADE") is { } slots && slots.Active[1].Contains(dash)
                  && data.Spells.Template("CLOCKWORK_DASH_SLOW") is not null, $"Clockwork Dash is spell {dash} (official count), in the blade's second slot");
            // The start-up check: an id the state does not expect stops the server.
            var state = ModState.Load(s.Paths.Local)!;
            state.Items!.New["T6_MAIN_CLOCKWORKBLADE"] = bladeId + 5;
            var stopped = false;
            var path = ModState.PathFor(s.Paths.Local);
            var saved = File.ReadAllText(path);
            state.Save(s.Paths.Local);
            try { ModState.Verify(s.Paths.Local, data.Items, data.Spells, new Dictionary<string, string>()); }
            catch (InvalidDataException) { stopped = true; }
            File.WriteAllText(path, saved);
            Check(stopped, "a moved id stops the start");
            // The server-only drops of the pack.
            var drops = ModDrops.Load(Path.Combine(DataDir(), "..", "mods"), ["example-blade"], data.Items);
            drops.Random = new Random(7);
            var boss = Enumerable.Range(0, 2000).Count(_ => drops.ForMob("WORLD_BOSS_T5_KEEPER", "boss", 1, []).Count == 1);
            var other = Enumerable.Range(0, 500).Count(_ => drops.ForMob("T5_MOB_KEEPER_BEAR", "standard", 1, []).Count > 0);
            Check(drops.Drops.Count == 1 && boss is > 30 and < 95 && other == 0, $"3 % from world bosses ({boss} of 2000), never from other mobs ({other})");
            s.Kit.Restore();
            Check(SafeFiles.SameFile(Path.Combine(s.Paths.Local, "items.bin"), Path.Combine(s.Paths.Official, "items.bin"))
                  && SafeFiles.LinkCount(Path.Combine(s.Paths.Local, "localization.bin")) == 2 && ModState.Load(s.Paths.Local) is { Installed.Count: 0 },
                  "restore makes the files hard links to the official ones again");
        }
        finally
        {
            try { Directory.Delete(s.Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
