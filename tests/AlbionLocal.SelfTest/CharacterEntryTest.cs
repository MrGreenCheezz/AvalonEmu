using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

public static class CharacterEntryTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("character entry: location, played milliseconds and premium days use their wire units", WireFields),
        ("character entry: creation response identifies the starting map before relogin", Creation),
        ("character entry: duplicate names use the client's name-not-unique error, not a connection error", DuplicateName),
        ("character entry: selection preview restores customization JSON and equipped items after login", SelectionPreview),
        ("character entry: bald and beardless previews use empty mesh ids without changing saved appearance", EmptyMeshes),
        ("character entry: online time counts once across snapshots and disconnect, not while offline", OnlineTime),
        ("character entry: schema v35 keeps characters and initializes unknown historic playtime to zero", Migration),
    ];

    private static void Check(bool value, string why) => AlbionTests.Check(value, why);

    private static void SelectionPreview()
    {
        var dir = NewDir();
        try
        {
            using var repository = new SqliteCharacterRepository(Path.Combine(dir, "save.db"));
            using var world = new World(new ServerConfig(), repository, Client.Items, Client.GameData) { Sync = new object() };
            var starter = new StarterKit { Equipment = [new("T4_MAIN_SWORD"), new("T4_OFF_SHIELD"), new("T4_HEAD_PLATE_SET1"),
                new("T4_ARMOR_PLATE_SET1"), new("T4_SHOES_PLATE_SET1"), null, new("T4_CAPE")] };
            var peer = new FakePeer();
            var login = new LoginSession(peer, world.Config, repository, starter) { World = world };
            login.OnRequest(Request(TestLogin(), 1), false);
            login.OnRequest(Request(new CreateCharacterRequest { h7 = "PreviewProbe", h8 = 10, h9 = 1,
                ia = [0, 0, 5, 1], ib = [0, 3, 2, 1] }, 2), false);
            var created = peer.Responses.Last().Parameters;
            int Id(string name) => Client.Items.Find(name)!.Index;
            Check(Convert.ToInt32(created[15]) == Id("T4_HEAD_PLATE_SET1")
                && Convert.ToInt32(created[16]) == Id("T4_ARMOR_PLATE_SET1")
                && Convert.ToInt32(created[17]) == Id("T4_CAPE")
                && Convert.ToInt32(created[18]) == Id("T4_MAIN_SWORD")
                && Convert.ToInt32(created[19]) == Id("T4_OFF_SHIELD")
                && Convert.ToInt32(created[20]) == Id("T4_SHOES_PLATE_SET1"), "creation preview follows native head/armor/cape/main/off/shoes order");
            var nextPeer = new FakePeer();
            var next = new LoginSession(nextPeer, world.Config, repository, starter) { World = world };
            next.OnRequest(Request(TestLogin(), 3), false);
            var fields = nextPeer.Responses.Last().Parameters;
            using var look = JsonDocument.Parse(((string[])fields[18]!)[0]);
            Check(look.RootElement.GetProperty("HAIR").GetProperty("MeshId").GetString() == "T0_HAIR_05_HF"
                && look.RootElement.GetProperty("HAIR").GetProperty("ColorPosition").GetInt32() == 2
                && look.RootElement.GetProperty("SKIN").GetProperty("ColorPosition").GetInt32() == 3,
                "login uses selected mesh resource names and colors, not empty strings or numeric mesh ids");
            Check(((int[])fields[37]!)[0] == Id("T4_HEAD_PLATE_SET1") && ((int[])fields[38]!)[0] == Id("T4_ARMOR_PLATE_SET1")
                && ((int[])fields[39]!)[0] == Id("T4_CAPE") && ((int[])fields[40]!)[0] == Id("T4_MAIN_SWORD")
                && ((int[])fields[41]!)[0] == Id("T4_OFF_SHIELD") && ((int[])fields[42]!)[0] == Id("T4_SHOES_PLATE_SET1"),
                "returning character preview receives actual equipped types in native order");
            foreach (var preset in Client.GameData!.Looks.Presets)
            {
                var c = new Character { Appearance = (byte)preset.Template, Customization = preset.Customization, Clothes = preset.Clothes };
                using var json = JsonDocument.Parse(Client.GameData.Looks.SelectionJson(c));
                Check(json.RootElement.EnumerateObject().Count() == (preset.Template == 0 ? 5 : 4), "male and female presets retain every customization slot");
            }
            Check(Client.GameData.Looks.SelectionJson(new Character()) == "", "legacy missing choices retain safe fallback");
            var invalid = new Character { Appearance = 1, Customization = [0, 0, int.MaxValue, 0], Clothes = [0, 0, 0, 0] };
            Check(Client.GameData.Looks.SelectionJson(invalid) == "", "invalid saved mesh does not break login");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void EmptyMeshes()
    {
        var looks = Client.GameData!.Looks;
        var bald = new Character { Appearance = 0, Customization = [2, 0, 0, 5, 5], Clothes = [0, 2, 9, 0, 3] };
        using (var json = JsonDocument.Parse(looks.SelectionJson(bald)))
            Check(json.RootElement.GetProperty("HAIR").GetProperty("MeshId").ValueKind == JsonValueKind.String
                && json.RootElement.GetProperty("HAIR").GetProperty("MeshId").GetString() == "",
                "client resolves the bald entry by empty resource id, not null");
        var noBeard = bald.Clone();
        noBeard.Customization[3] = 0;
        using (var json = JsonDocument.Parse(looks.SelectionJson(noBeard)))
            Check(json.RootElement.GetProperty("BEARD").GetProperty("MeshId").GetString() == "",
                "beardless mesh also uses the client's empty resource id");
        var female = new Character { Appearance = 1, Customization = [5, 0, 21, 4], Clothes = [0, 10, 10, 0] };
        using (var json = JsonDocument.Parse(looks.SelectionJson(female)))
        {
            Check(json.RootElement.GetProperty("HAIR").GetProperty("MeshId").GetString() == "T0_HAIR_21_HF"
                && json.RootElement.GetProperty("HAIR").GetProperty("ColorPosition").GetInt32() == 10,
                "existing nonempty female hair and selected color are preserved");
            Check(json.RootElement.GetProperty("SKIN").GetProperty("MeshId").ValueKind == JsonValueKind.Null,
                "color-only skin has no mesh slot and retains its representation");
        }
        Check(bald.Customization.SequenceEqual([2, 0, 0, 5, 5]) && bald.Clothes.SequenceEqual([0, 2, 9, 0, 3])
            && female.Customization.SequenceEqual([5, 0, 21, 4]) && female.Clothes.SequenceEqual([0, 10, 10, 0]),
            "preview serialization does not mutate saved customization arrays");
    }

    private static void WireFields()
    {
        var dir = NewDir();
        try
        {
            using var repository = new SqliteCharacterRepository(Path.Combine(dir, "save.db"));
            var character = new StarterKit { Cluster = "1008" }.NewCharacter("EntryProbe");
            repository.Create(character);
            void Login(PremiumMode mode, long expectedTime, long expectedDays)
            {
                var peer = new FakePeer();
                var session = new LoginSession(peer, new ServerConfig { PremiumMode = mode }, repository, StarterKit.Default);
                session.OnRequest(Request(TestLogin(), 1), encrypted: false);
                var response = peer.Responses.Single();
                Check(response.ReturnCode == 0 && response.Parameters[10] is string[] and ["1008"]
                    && response.Parameters[13] is string[] and ["1008"], "cluster instance and world ids, not character GUID or empty location");
                Check(response.Parameters[34] is long[] played && played.SequenceEqual([expectedTime])
                    && response.Parameters[35] is long[] days && days.SequenceEqual([expectedDays]), "duration units match the inspected client");
            }
            Login(PremiumMode.None, 0, 0);
            character.PlayedMilliseconds = 123456;
            repository.Save(character);
            Login(PremiumMode.Free, 123456, 365);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void OnlineTime()
    {
        var dir = NewDir();
        try
        {
            using var repository = new SqliteCharacterRepository(Path.Combine(dir, "save.db"));
            repository.Create(new StarterKit().NewCharacter("TimeProbe"));
            var clock = new ManualClock();
            using var world = new World(new ServerConfig { JoinByCharacterId = true }, repository, Items()) { Sync = new object(), Clock = clock };
            var id = repository.List()[0].Id;
            WorldSession Join()
            {
                var session = new WorldSession(new FakePeer(), world);
                session.OnRequest(Request(new JoinRequest { ss = id }, 1), encrypted: false);
                return session;
            }
            var first = Join();
            clock.Advance(TimeSpan.FromMilliseconds(1234));
            world.Saves.SaveAll();
            world.Saves.Flush();
            Check(repository.Find(id)!.PlayedMilliseconds == 1234, "stationary player's periodic snapshot includes online time");
            world.Saves.SaveAll(); world.Saves.Flush();
            Check(repository.Find(id)!.PlayedMilliseconds == 1234, "same instant is not counted twice");
            clock.Advance(TimeSpan.FromMilliseconds(766));
            first.OnDisconnected("test");
            world.Saves.Flush();
            Check(repository.Find(id)!.PlayedMilliseconds == 2000, "disconnect settles final interval once");
            clock.Advance(TimeSpan.FromHours(5));
            first.Player!.StorePlayedTime();
            Check(first.Player.Character.PlayedMilliseconds == 2000, "a departed player cannot accumulate offline time on a late snapshot");
            var second = Join();
            clock.Advance(TimeSpan.FromMilliseconds(500));
            second.OnDisconnected("test"); world.Saves.Flush();
            Check(repository.Find(id)!.PlayedMilliseconds == 2500, "offline hours excluded after a rejoin");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Creation()
    {
        var dir = NewDir();
        try
        {
            using var repository = new SqliteCharacterRepository(Path.Combine(dir, "save.db"));
            var peer = new FakePeer();
            var starter = new StarterKit { Cluster = "1008" };
            var session = new LoginSession(peer, new ServerConfig { PremiumMode = PremiumMode.None }, repository, starter);
            session.OnRequest(Request(TestLogin(), 1), encrypted: false);
            session.OnRequest(Request(new CreateCharacterRequest { h7 = "SceneProbe" }, 2), encrypted: false);
            var response = peer.Responses.Last();
            Check(response.ReturnCode == 0 && response.Parameters[1] is "1008" && response.Parameters[2] is "1008",
                "the new entry receives instance and world location without another Login");
            Check(repository.Find("SceneProbe") is { PlayedMilliseconds: 0, Silver: 0, Cluster: "1008" }, "new fixture starts with zero duration and silver");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void DuplicateName()
    {
        var dir = NewDir();
        try
        {
            using var repository = new SqliteCharacterRepository(Path.Combine(dir, "save.db"));
            var peer = new FakePeer();
            var session = new LoginSession(peer, new ServerConfig(), repository, StarterKit.Default);
            session.OnRequest(Request(TestLogin(), 1), false);
            session.OnRequest(Request(new CreateCharacterRequest { h7 = "NameProbe" }, 2), false);
            Check(peer.Responses.Last().ReturnCode == 0, "first name accepted");
            var original = repository.Find("NameProbe")!;
            session.OnRequest(Request(new CreateCharacterRequest { h7 = "nameprobe" }, 3), false);
            Check(peer.Responses.Last().ReturnCode == 482 && repository.Find("NameProbe")!.Id == original.Id,
                "case-insensitive duplicate returns NAME_NOT_UNIQUE and preserves original character");
            session.OnRequest(Request(new CreateCharacterRequest { h7 = " " }, 4), false);
            Check(peer.Responses.Last().ReturnCode == 483, "blank name returns NAME_NOT_ALLOWED");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void Migration()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "legacy.db");
            using (var repository = new SqliteCharacterRepository(path)) repository.Create(StarterKit.Default.NewCharacter("LegacyProbe"));
            using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "ALTER TABLE characters DROP COLUMN tutorial_opening; ALTER TABLE characters DROP COLUMN played_ms; PRAGMA user_version=35;";
                command.ExecuteNonQuery();
            }
            using var migrated = new SqliteCharacterRepository(path);
            Check(migrated.Find("LegacyProbe") is { PlayedMilliseconds: 0, Cluster: "0000" } c
                && c.Equipment[0]?.Name == "T4_MAIN_SWORD", "legacy position and equipment preserved; duration is not invented from creation date");
        }
        finally { Directory.Delete(dir, true); }
    }
}
