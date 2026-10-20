using AlbionLocal.Photon;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>GM commands: argument parsing, and .item add from the Say chat and the console on a <see cref="FakePeer"/> session.</summary>
public static class CommandTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("commands: arguments by type, defaults, bad int, unknown item with the closest, level, bad binding", Parsing),
        ("commands: '.item add T4_ORE 5' in Say sends the item events and is saved, other lines stay chat, console", ItemAdd),
        ("log file: one writer, renamed to .1 at the size limit", LogFile),
        ("commands: content/kits.json mounts ride, foods and potions run; .kit fills the free slots and names the rest", Kit),
    ];

    private static (World World, SqliteCharacterRepository Characters, string Dir) NewWorld(ManualClock? clock = null)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var items = Items("8610\tT4_MAIN_SWORD\tweapon\tmainhand\t4\t13500", "1000\tT4_ORE\tsimpleitem\t\t4", "2000\tT4_CHEST\tkilltrophy\t\t4");
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, items) { Sync = new object(), Clock = clock ?? new ManualClock() };
        return (world, characters, dir);
    }

    private static void Parsing()
    {
        var (world, characters, dir) = NewWorld();
        var replies = new List<(string Text, bool Error)>();
        var admin = new CommandContext(world, null, AccountLevel.Admin, (t, e) => replies.Add((t, e)));
        Check(world.Commands.Parse(admin, "item add T4_ORE 5") is ({ Path: "item add" }, [_, ItemTemplate { Name: "T4_ORE" }, 5, 1]),
              "'item add T4_ORE 5': the item by uniquename, the count, quality left at its default");
        Check(world.Commands.Parse(admin, ".ITEM add t4_main_sword") is (_, [_, ItemTemplate { Name: "T4_MAIN_SWORD" }, 1, 1]), "defaults, any case, leading dot");
        string Error(CommandContext ctx, string line)
        {
            try { world.Commands.Parse(ctx, line); return ""; }
            catch (CommandError e) { return e.Message; }
        }
        Check(Error(admin, "item add T4_ORE five") == "count: 'five' is not a whole number", "a bad int names the parameter");
        Check(Error(admin, "item add T4_OER") == "unknown item T4_OER (closest: T4_ORE)", "an unknown item names the closest one");
        var player = new CommandContext(world, null, AccountLevel.Player, (t, e) => replies.Add((t, e)));
        Check(Error(player, "item add T4_ORE").Contains("needs level GameMaster") && Error(admin, "item add").StartsWith("missing <item>"),
              "level checked, missing argument named");
        world.Commands.Execute(admin, ".help item");
        Check(replies is [(var help, false)] && help.StartsWith(".item add <item> [count=1] [quality=1] - "), $".help shows the usage (got {replies.FirstOrDefault().Text})");
        try
        {
            _ = new CommandRegistry([typeof(BadCommand)]);
            Check(false, "a parameter type the parser does not know was accepted");
        }
        catch (InvalidOperationException e) { Check(e.Message.Contains("BadCommand.When") && e.Message.Contains("DateTime"), $"the error names it (got {e.Message})"); }
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static class BadCommand
    {
        [Command("when")]
        public static void When(CommandContext ctx, DateTime at) { }
    }

    private static void ItemAdd()
    {
        var clock = new ManualClock();
        var (world, characters, dir) = NewWorld(clock);
        var hero = characters.List()[0];
        hero.Equipment = [];
        hero.Inventory = [new("T4_ORE", 10)];
        characters.Save(hero);
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        var (events, responses) = (peer.Events.Count, peer.Responses.Count);
        session.OnRequest(Request(new SayRequest { aa9 = ".item add T4_ORE 5" }, null), encrypted: false);
        int Code(EventData e) => Convert.ToInt32(e.Parameters[ReservedKeys.EventCode]);
        Check(peer.Events.Skip(events).ToList() is [var made, var put, var reply]
              && Code(made) == (int)EventCodes.NewSimpleItem && Convert.ToInt32(made.Parameters[1]) == 1000 && Convert.ToInt32(made.Parameters[2]) == 5
              && Code(put) == (int)EventCodes.InventoryPutItem && Convert.ToInt64(put.Parameters[0]) == Convert.ToInt64(made.Parameters[0])
              && Convert.ToInt32(put.Parameters[1]) == 1 && put.Parameters[2] is byte[] g && g.SequenceEqual(hero.InventoryContainer.ToByteArray())
              && !put.Parameters.ContainsKey(3)
              && Code(reply) == (int)EventCodes.SystemMessage && reply.Parameters[0] is string text && text.StartsWith("added 5 x T4_ORE")
              && !reply.Parameters.ContainsKey(1),
              "NewSimpleItem (type, count), InventoryPutItem into the first free slot (1), then an info SystemMessage");
        Check(peer.Responses.Count == responses, "a command line is not chat: no Say response");
        session.OnRequest(Request(new SayRequest { aa9 = "...hello" }, null), encrypted: false);
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == 0 && peer.Events.Count == events + 4
              && Code(peer.Events[^1]) == (int)EventCodes.ChatSay && (string)peer.Events[^1].Parameters[1]! == "...hello",
              "any other line gets the empty Say response, as before, and comes back as ChatSay (stage 7)");
        session.OnRequest(Request(new SayRequest { aa9 = ".item add T4_CHEST" }, null), encrypted: false);
        Check(peer.Events.Count == events + 5 && peer.Events[^1] is var refused && Code(refused) == (int)EventCodes.SystemMessage
              && refused.Parameters.ContainsKey(1) && refused.Parameters[0] is string why && why.Contains("killtrophy"),
              "a kind NewSimpleItem cannot create (a kill trophy) is refused before anything is sent");
        for (var i = 0; i < 50; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            world.Update();
        }
        world.Saves.Flush();
        Check(characters.Find(hero.Id)!.Inventory is [{ Name: "T4_ORE", Count: 10 }, { Name: "T4_ORE", Count: 5 }], "the new item is saved after the change delay");

        var console = new List<(string Text, bool Error)>();
        var reader = ConsoleInput.Start(world, new StringReader(".gps\nitem add NO_SUCH\n"), (t, e) => console.Add((t, e)));
        Check(reader.Join(2000), "the console reader ends at the end of its input");
        clock.Advance(TimeSpan.FromMilliseconds(50));
        world.Update();
        Check(console is [(var gps, false), (var bad, true)] && gps.Contains("cluster 0000") && bad.StartsWith("unknown item NO_SUCH"),
              "console lines run in the next tick on the only joined player, errors as errors");
        var (peer2, firstEvents) = (new FakePeer(), peer.Events.Count);
        new WorldSession(peer2, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
        var secondEvents = peer2.Events.Count;
        console.Clear();
        ConsoleInput.Start(world, new StringReader("item add T4_ORE 1\n"), (t, e) => console.Add((t, e))).Join(2000);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        world.Update();
        Check(console is [(_, false)] && peer2.Events.Skip(secondEvents).Select(Code).SequenceEqual([(int)EventCodes.NewSimpleItem, (int)EventCodes.InventoryPutItem])
              && peer.Events.Count == firstEvents, "a character joined twice: the console acts on the newest Join");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>Log:File (Log.SetFile): a file over the limit is renamed at open and while running, and no second writer gets in.</summary>
    private static void LogFile()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "server.log");
        File.WriteAllText(path, new string('x', 60));
        static string Read(string p)
        {
            using var s = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(s).ReadToEnd();
        }
        var console = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            Log.SetFile(path, rotateBytes: 50);
            Check(Read(path + ".1").StartsWith("xxx") && Read(path).StartsWith("---- "), "a file over the limit is renamed to .1 when opened");
            try
            {
                using var other = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                Check(false, "a second writer opened the log");
            }
            catch (IOException) { }
            Log.Always("test", "a line that crosses the limit");
            Check(Read(path + ".1").Contains("a line that crosses the limit") && Read(path).Length == 0, "and again when a line crosses it");
        }
        finally
        {
            Log.SetFile(null);
            Console.SetOut(console);
        }
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// The shipped kits against the client data (every entry kept, the mounts have a mounted buff, the consumables' spells use only
    /// effects that run), then .kit on a nearly full inventory.
    /// </summary>
    private static void Kit()
    {
        var console = Console.Out;
        Console.SetOut(TextWriter.Null); // the load lines and the expected warning below
        try
        {
            var kits = Kits.Load(Path.Combine(DataDir(), "..", "content", "kits.json"), Client.Items);
            Check(kits.All.Select(k => (k.Name, k.Items.Count)).SequenceEqual([("food", 10), ("invasion", 2), ("mounts", 8), ("potions", 13), ("test", 31)]),
                  $"every shipped entry is a known, creatable item (got {string.Join(", ", kits.All.Select(k => $"{k.Name} {k.Items.Count}"))})");
            var spells = Client.Spells!;
            var mounts = kits.Find("mounts")!.Items.Select(i => Client.Items.Find(i.Name)!).ToList();
            Check(mounts.All(m => m.Mount is { } data && spells.Template(data.MountedBuff) is not null), "every kit mount has its mounted buff (Mounts.Check)");
            // The <mountspell>s (Mounts.SpellSlots): the frost ram, hell spider, fire drake, battle swamp dragon and battle mammoth have them.
            var withSpells = mounts.Where(m => m.Mount!.Spells.Any(n => n is not null)).ToList();
            Check(withSpells.Count == 5 && withSpells.All(m => m.Mount!.Spells.OfType<string>().All(n => spells.Template(n) is { Kind: "activespell" or "togglespell" })),
                  $"5 kit mounts have mount spells, all active or toggle spells of spells.xml (got {string.Join(", ", withSpells.Select(m => m.Name))})");
            // The consume spell and the spells its effects name (an area's, an applied one): only effect kinds SpellCasts runs.
            IEnumerable<string> Tags(SpellTemplate? spell, int depth) => spell is null ? ["(no spell)"] : spell.Effects.SelectMany(e =>
                (IEnumerable<string>)[e.Name.LocalName, .. depth > 0 ? e.Attributes().SelectMany(a => spells.IdOf(a.Value) >= 0 ? Tags(spells.Template(a.Value), depth - 1) : []) : []]);
            foreach (var entry in kits.Find("food")!.Items.Concat(kits.Find("potions")!.Items))
            {
                var item = Client.Items.Find(entry.Name)!;
                var missing = Tags(spells.Template(spells.ConsumeSpell(item.Name)), 2).Where(t => !Server.Systems.SpellCasts.Supported.Contains(t)).Distinct().ToList();
                Check(item.Slot is "food" or "potion" && missing.Count == 0, $"{item.Name}: a {item.Slot} whose spell runs (not yet: {string.Join(", ", missing)})");
            }

            var dir = NewDir();
            var characters = Repository(dir);
            var items = Items("8610\tT4_MAIN_SWORD\tweapon\tmainhand\t4\t13500", "1000\tT4_ORE\tsimpleitem\t\t4", "2000\tT4_CHEST\tkilltrophy\t\t4");
            var file = Path.Combine(dir, "kits.json");
            File.WriteAllText(file, """
                { "small": { "Items": [{ "Name": "T4_MAIN_SWORD", "Count": 3, "Quality": 2 }, { "Name": "T4_ORE", "Count": 5 }, "T4_CHEST", { "Name": "T4_ORE", "Count": "9" }] },
                  "x": { "Help": 3, "Items": {} } }
                """);
            var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, items)
            {
                Sync = new object(), Clock = new ManualClock(), Kits = Kits.Load(file, items),
            };
            Check(world.Kits.Find("SMALL") is { Items.Count: 2 } && world.Kits.Find("x") is { Items.Count: 0 },
                  "a kill trophy, a string count and wrong-typed keys are skipped at load, not the kits; names in any case");
            var hero = characters.List()[0];
            hero.Equipment = [];
            hero.Inventory = [.. Enumerable.Range(0, Server.Entities.Inventory.BagSlots - 2).Select(_ => new InventoryItem("T4_ORE", 1))];
            characters.Save(hero);
            var peer = new FakePeer();
            new WorldSession(peer, world).OnRequest(Request(new JoinRequest { ss = hero.Id }, 1), encrypted: false);
            var replies = new List<(string Text, bool Error)>();
            var joined = peer.Events.Count;
            foreach (var line in new[] { ".kit small", ".kit small", ".kit list", ".kit nope" })
                world.Commands.ExecuteConsole(world, line, (t, e) => replies.Add((t, e)));
            Check(replies is [(var first, false), (var full, true), (var list, false), (var unknown, true)]
                  && first == "kit small: added 2 items; did not fit: 1 x T4_MAIN_SWORD, 5 x T4_ORE"
                  && full.Contains("nothing added") && list.StartsWith("small: 2 items") && unknown == "unknown kit nope; kits: small, x",
                  $"two of three swords fit, the ore not; then nothing; list; unknown (got {string.Join(" | ", replies.Select(r => r.Text))})");
            Check(peer.Events.Skip(joined).Count(e => Convert.ToInt32(e.Parameters[ReservedKeys.EventCode]) == (int)EventCodes.InventoryPutItem) == 2,
                  "the two free slots are filled, nothing more");
            world.Dispose();
            characters.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        finally { Console.SetOut(console); }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
