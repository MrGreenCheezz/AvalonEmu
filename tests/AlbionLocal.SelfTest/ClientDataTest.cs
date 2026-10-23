using System.Text.Json;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The client data loaders (Data/) against the golden tables the analysis tools generated from the same client files:
/// data/items.tsv (gen_items.py), data/gamedata.json (gen_gamedata.py), data/spells.json (gen_item_spells.py).
/// </summary>
public static class ClientDataTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("client data: loads in a few seconds, a broken file is named, build and hashes recorded, a change is warned", Record),
        ("client data: all 11971 items equal items.tsv (id, name, kind, slot, tier, durability)", ItemsGolden),
        ("client data: game data equals gamedata.json value by value", GameDataGolden),
        ("client data: all 9326 spells, item slot lists and consume map equal spells.json", SpellsGolden),
    ];

    private static void Record()
    {
        var client = Client;
        Check(ClientLoadTime < TimeSpan.FromSeconds(10), $"loaded in {ClientLoadTime.TotalSeconds:0.00} s");
        Check(client.Build is { Length: > 0 } && client.Hashes.Count == ClientData.FileNames.Length
              && client.Hashes.Values.All(h => h.Length == 64), $"build {client.Build} and a SHA-256 per file");
        var dir = NewDir();
        try { ClientData.Load(Path.Combine(dir, "nowhere")); Check(false, "a missing folder was accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("Paths:ClientGameData"), $"a missing folder names the key (got {e.Message})"); }
        var broken = Directory.CreateDirectory(Path.Combine(dir, "broken")).FullName;
        foreach (var name in ClientData.FileNames) File.WriteAllBytes(Path.Combine(broken, name), []);
        var bin = File.ReadAllBytes(Path.Combine(ClientGameDataDir(), "items.bin"));
        bin[2] ^= 2; // CBC: flips the deflate block type in plaintext byte 10, so XmlReader.Create already fails to inflate
        File.WriteAllBytes(Path.Combine(broken, "items.bin"), bin);
        string? Error() { try { ClientData.Load(broken); return null; } catch (InvalidDataException e) { return e.Message; } }
        var damaged = Error();
        Check(damaged?.Contains("items.bin") == true, $"a damaged file is named (got {damaged})");
        string? locked;
        using (File.Open(Path.Combine(broken, "spells.bin"), FileMode.Open, FileAccess.Read, FileShare.None)) locked = Error();
        Check(locked?.Contains("spells.bin") == true, $"a locked file is named (got {locked})");
        var path = Path.Combine(dir, ClientData.RecordFile);
        var console = Console.Out;
        Console.SetOut(TextWriter.Null); // the change warnings below are expected
        try
        {
            Check(client.Record(dir).Count == 0 && File.Exists(path), "first start: recorded, nothing to warn");
            var written = File.GetLastWriteTimeUtc(path);
            Check(client.Record(dir).Count == 0 && File.GetLastWriteTimeUtc(path) == written, "same data: no warning, the file is left alone");
            File.WriteAllText(path, File.ReadAllText(path).Replace(client.Hashes["items.bin"], new string('0', 64)).Replace(client.Build!, "an older build"));
            Check(client.Record(dir) is [var build, var items] && build.Contains("an older build") && items.StartsWith("items.bin changed"),
                  "an official update: the build and the changed file are warned");
            Check(client.Record(dir).Count == 0, "and recorded for the next start");
        }
        finally { Console.SetOut(console); }
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void ItemsGolden()
    {
        if (Golden("items.tsv") is not { } tsv) return;
        var items = Client.Items;
        var rows = File.ReadLines(tsv).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        // items.tsv has the items.xml variants; the client adds _EMPTY and _FULL of the 133 journals after them (JournalTest).
        Check(rows.Count == 11971 && items.Count == 11971 + 2 * 133, $"11971 items + 266 journal variants (items.tsv {rows.Count}, client {items.Count})");
        var bad = new List<string>();
        foreach (var row in rows)
        {
            var want = TsvTemplate(row);
            var got = items[want.Index];
            if (got is null || got.Name != want.Name || got.Kind != want.Kind || got.Slot != want.Slot || got.Tier != want.Tier
                || got.MaxDurability != want.MaxDurability || items.Find(want.Name) != got)
                bad.Add($"{want.Index} {want.Name} (got {got?.Name} {got?.Kind} {got?.Slot} {got?.Tier} {got?.MaxDurability})");
        }
        Check(bad.Count == 0, $"{bad.Count} items differ, first {bad.FirstOrDefault()}");
        Check(items.All.Select((t, i) => t.Index == i + 1).All(ok => ok), "ids 1..12237 in file order");
        var sword = items.Find("t4_main_sword@1");
        Check(sword is { Name: "T4_MAIN_SWORD@1", Enchantment: 1, ActiveSpellSlots: 3, PassiveSpellSlots: 1, SpellLists.Length: 3 }
              && sword.Attrs["uniquename"] == "T4_MAIN_SWORD" && sword.SpellLists == items.Find("T4_MAIN_SWORD")!.SpellLists
              && items.Find("T4_ORE") is { MaxStackSize: 999, IsSimple: true } && items.Find("T1_MEAL_SOUP@1")?.ConsumeSpell is { Length: > 0 },
              "hot fields: by name ignoring case, enchantment over its item, shared spell lists, stack size, consumespell");
    }

    private static void GameDataGolden()
    {
        if (Golden("gamedata.json") is not { } json) return;
        var client = Client.GameData!;
        var golden = GameData.Load(json);
        static string Json(object? o) => JsonSerializer.Serialize(o);
        foreach (var (what, a, b) in new (string, object?, object?)[]
                 {
                     ("bases", client.Bases, golden.Bases), ("shares", client.Shares, golden.Shares),
                     ("progressions", client.Progressions, golden.Progressions), ("quality", client.QualityItemPower, golden.QualityItemPower),
                     ("durability", client.Durability, golden.Durability), ("unlockAll", client.UnlockAll, golden.UnlockAll),
                 })
            Check(Json(a) == Json(b), $"{what}: {Json(a)} (gamedata.json {Json(b)})");
        Check(client.Achievements.Length == 704 && golden.Achievements.Length == 704, $"704 achievements (got {client.Achievements.Length})");
        var badAchievements = Enumerable.Range(0, 704).Where(i => Json(client.Achievements[i]) != Json(golden.Achievements[i])).ToList();
        Check(badAchievements.Count == 0, $"{badAchievements.Count} achievements differ, first {badAchievements.FirstOrDefault()}");
        Check(client.Items.Length == golden.Items.Length + 2 * 133, $"{golden.Items.Length - 1} items + 266 journal variants (got {client.Items.Length - 1})");
        var badItems = Enumerable.Range(0, golden.Items.Length).Where(i => Json(client.Items[i]) != Json(golden.Items[i])).ToList();
        Check(badItems.Count == 0, $"{badItems.Count} items differ, first {badItems.Select(i => Json(client.Items[i])).FirstOrDefault()}");
    }

    private static void SpellsGolden()
    {
        if (Golden("spells.json") is not { } json) return;
        var client = Client.Spells!;
        var golden = SpellData.Load(json);
        Check(client.Count == 9326 && golden.Count == 9326, $"9326 spell ids (got {client.Count})");
        var badSpells = Enumerable.Range(0, 9326)
            .Where(i => client.Name(i) != golden.Name(i) || client.KindOf(i) != golden.KindOf(i) || client.IsLocked(i) != golden.IsLocked(i)).ToList();
        Check(badSpells.Count == 0, $"{badSpells.Count} spells differ, first {badSpells.Select(i => $"{i} {client.Name(i)}").FirstOrDefault()}");
        static bool Same(short[][] a, short[][] b) => a.Length == b.Length && a.Zip(b).All(p => p.First.SequenceEqual(p.Second));
        var badItems = golden.Items.Where(g => !client.Items.TryGetValue(g.Key, out var c) || c.ActiveSlots != g.Value.ActiveSlots
                || c.PassiveSlots != g.Value.PassiveSlots || !Same(c.Active, g.Value.Active) || !Same(c.Passive, g.Value.Passive))
            .Select(g => g.Key).ToList();
        Check(client.Items.Count == golden.Items.Count && badItems.Count == 0,
              $"{golden.Items.Count} items with spell slots (got {client.Items.Count}), {badItems.Count} differ, first {badItems.FirstOrDefault()}");
        Check(client.Consume.Count == golden.Consume.Count && golden.Consume.All(g => client.Consume.GetValueOrDefault(g.Key, (short)-1) == g.Value),
              $"{golden.Consume.Count} consumables (got {client.Consume.Count})");
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
