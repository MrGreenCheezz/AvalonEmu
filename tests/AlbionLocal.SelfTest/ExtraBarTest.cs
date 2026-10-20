using AlbionLocal.Plugin.Api;
using AlbionLocal.Server;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The extra action bars (Systems/ExtraBars, analysis/work/stageN/extrabars/SPEC.md), light: a pack's extraspells.json fills the bars from the worn
/// items and the bars message (event 9001) goes out only after the mod's hello (op 9001); casting extra slots through slot 25 and op 9002 runs the
/// normal cast (CastFinished, the spell's cooldown, OnCheckCast with Slot ExtraN) and is refused for a spell that is not in the bars.
/// </summary>
public static class ExtraBarTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("extra bars: a worn cape adds its extra spells (main hand first, a preferred bar-2 slot); event 9001 + catalog 9004 only after the hello, again on unequip", FillAndHello),
        ("extra bars: slot-25 CastStart and op 9002 cast extra slots (CastFinished slot 25, cooldown, event 9002, OnCheckCast Extra2); a cooldown, an empty slot and a foreign spell are refused", CastFromExtraSlot),
        ("extra bars rules: a T6 sword + shield + plate set + cape + bag + mount fill 9 slots from their unchosen options and family tables; .2/.3 and masterpiece add 3 more (12, one left out); an artifact plate adds its own FORCESHIELD", RulesFill),
        ("extra bars rules: op 9003 answers event 9005 for an unworn item (sword@2 masterpiece: 2 unchosen + enchant + quality), labels in event 9001 key 11", ItemQuery),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private const EventCodes BarsCode = (EventCodes)CustomCodes.ExtraBars;

    /// <summary>A temp content dir (no defaults) and pack "xb": the axe → BLINK, the T4 cape → SPRINT and FLEE (bar 2, position 1).</summary>
    private static ServerConfig Config(string dir)
    {
        var content = Path.Combine(dir, "content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "extraspells.json"), """{ "Bars": 2, "BarSize": 6, "Items": {} }""");
        var server = Path.Combine(dir, "mods", "xb", "server");
        Directory.CreateDirectory(server);
        File.WriteAllText(Path.Combine(server, "extraspells.json"), """
            { "note": "test", "T4_MAIN_AXE": ["BLINK"], "T?_CAPE": ["SPRINT", { "spell": "FLEE", "bar": 2, "position": 1 }, "NO_SUCH_SPELL"] }
            """);
        return new ServerConfig { JoinByCharacterId = true, ContentPath = content, ModsPath = Path.Combine(dir, "mods"), ModPacks = ["xb"], SavesPath = dir };
    }

    private static InventoryItem Axe => new("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] };

    private static void FillAndHello()
    {
        var dir = NewDir();
        using var arena = new Arena(config: Config(dir));
        arena.Setup = c => { c.Equipment[6] = null; c.Inventory = [new InventoryItem("T4_CAPE")]; };
        var world = arena.World;
        var (session, peer, me) = arena.Join("Barman", Axe, ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100));
        Check(world.ExtraBars.Table.Problems.Any(p => p.Contains("NO_SUCH_SPELL")), "an unknown spell of the pack's file is reported and left out");
        Check(world.ExtraBars.Names(me) is [ "BLINK", null, null, null, null, null, null, null, null, null, null, null ]
              && !peer.Events.Any(e => e.Parameters.GetValueOrDefault(ReservedKeys.EventCode) is short code && code == CustomCodes.ExtraBars),
              $"after the Join slot 1 = BLINK (the axe) and no bars event to a client without the mod ({string.Join(" ", world.ExtraBars.Names(me))})");

        var mark = peer.Events.Count;
        Send(session, new InventoryMoveItemRequest { ru = 0, rv = me.Inventory.Bag.Id, rx = 6, ry = me.Inventory.Equipment.Id, rz = 1 });
        var names = world.ExtraBars.Names(me);
        Check(me.Inventory.Equipment[6]?.Template.Name == "T4_CAPE" && names[0] == "BLINK" && names[1] == "SPRINT" && names[6] == "FLEE"
              && names.Count(n => n is not null) == 3 && !Since(peer, mark).Any(e => e.Code == BarsCode),
              $"the cape equipped: SPRINT in slot 2, FLEE in bar 2 position 1; still nothing sent ({string.Join(" ", names.Select(n => n ?? "-"))})");

        mark = peer.Events.Count;
        Send(session, new ExtraBarsHelloRequest { Protocol = 1, Mod = "test" });
        var bars = Since(peer, mark).LastOrDefault(e => e.Code == BarsCode).P;
        var spells = Client.Spells!;
        Check(session.ExtraBarsProtocol == 1 && bars is not null && Convert.ToInt32(bars[0]) == 2 && Convert.ToInt32(bars[1]) == 6
              && bars[2] is int[] ids && ids.Length == 12 && ids[0] == spells.IdOf("BLINK") && ids[1] == spells.IdOf("SPRINT") && ids[6] == spells.IdOf("FLEE") && ids[2] == -1
              && bars[3] is string[] { Length: 12 } n3 && n3[1] == "SPRINT" && bars[4] is string[] items && items[1] == "T4_CAPE" && items[0] == "T4_MAIN_AXE"
              && bars[5] is byte[] src && src[1] == 1 && src[2] == 0 && bars[7] is long[] { Length: 12 },
              "the hello is answered with event 9001: 2 bars of 6, ids/names/items/sources per slot, cooldown arrays");
        var catalog = Since(peer, mark).LastOrDefault(e => e.Code == (EventCodes)CustomCodes.ExtraSpellCatalog).P;
        Check(catalog?[0] is string[] pats && catalog[1] is string[] cs && catalog[2] is int[] cids && pats.Length == 3 && cs.Length == 3 && cids.Length == 3
              && pats[0] == "T4_MAIN_AXE" && cs[0] == "BLINK" && cids[0] == spells.IdOf("BLINK") && pats[1] == "T?_CAPE" && cs[1] == "SPRINT" && cs[2] == "FLEE",
              $"the hello also brings event 9004, the item → spells catalog in precedence order ({(catalog?[0] is string[] p0 ? string.Join(",", p0) : "none")})");

        mark = peer.Events.Count;
        Send(session, new InventoryMoveItemRequest { ru = 6, rv = me.Inventory.Equipment.Id, rx = 0, ry = me.Inventory.Bag.Id, rz = 1 });
        bars = Since(peer, mark).LastOrDefault(e => e.Code == BarsCode).P;
        Check(bars?[2] is int[] after && after[0] == spells.IdOf("BLINK") && after.Skip(1).All(i => i == -1),
              "the cape taken off: event 9001 again with only BLINK left");
    }

    private sealed class Watcher : PluginBase
    {
        public readonly List<SpellSlot> Slots = [];
        public override string Name => "extrabar-watcher";
        protected override void OnLoad() => Hooks.Spells.OnCheckCast("SPRINT", a => Slots.Add(a.Slot));
    }

    private static void CastFromExtraSlot()
    {
        var dir = NewDir();
        using var arena = new Arena(config: Config(dir));
        arena.Setup = c => c.Equipment[6] = new InventoryItem("T4_CAPE");
        var world = arena.World;
        var (session, peer, me) = arena.Join("Caster", Axe, ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100));
        var watcher = new Watcher();
        Check(world.Plugins.Load(watcher) is null, "the watcher plugin loads");
        Send(session, new ExtraBarsHelloRequest { Protocol = 1 });
        var spells = Client.Spells!;
        short sprint = spells.IdOf("SPRINT"), flee = spells.IdOf("FLEE");

        // Slot 25 (the client's fake slot) with SPRINT, extra slot 2.
        var mark = peer.Events.Count;
        var responses = peer.Responses.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 25, d0 = sprint, d1 = me.Id, d4 = 1 });
        var events = Since(peer, mark);
        var finished = events.FirstOrDefault(e => e.Code == EventCodes.CastFinished).P;
        var cooled = events.FirstOrDefault(e => e.Code == (EventCodes)CustomCodes.ExtraBarCooldowns).P;
        Check(peer.Responses.Count == responses && finished is not null && Convert.ToInt32(finished[3]) == 25
              && me.Combat.Cooldowns.TryGetValue(sprint, out var ready) && ready > world.GameNow
              && cooled?[0] is byte[] { Length: 1 } ix && ix[0] == 1 && cooled[2] is long[] { Length: 1 } ends && ends[0] == ready && watcher.Slots is [SpellSlot.Extra2],
              $"CastStart slot 25 SPRINT: no answer, CastFinished key 3 = 25, its cooldown and event 9002 for slot index 1, OnCheckCast Slot Extra2 ({string.Join(",", watcher.Slots)})");

        mark = peer.Events.Count;
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 25, d0 = sprint, d1 = me.Id, d4 = 2 });
        var refused = Since(peer, mark).FirstOrDefault(e => e.Code == (EventCodes)CustomCodes.ExtraBarCastRefused).P;
        Check(peer.Responses[^1].ReturnCode == WorldSession.CastRefused && refused?[2] as string == "Cooldown",
              $"SPRINT again at once: refused 960 and event 9003 Cooldown ({refused?[2]})");

        // Op 9002: extra slot 7 (index 6) = FLEE; an empty slot; a spell that is not that slot's.
        mark = peer.Events.Count;
        Send(session, new ExtraBarCastRequest { Time = world.GameNow, Index = 6, Target = me.Id, Counter = 3 });
        Check(Since(peer, mark).Any(e => e.Code == EventCodes.CastFinished) && me.Combat.Cooldowns.ContainsKey(flee), "op 9002 index 6 casts FLEE (CastFinished, cooldown)");
        mark = peer.Events.Count;
        Send(session, new ExtraBarCastRequest { Time = world.GameNow, Index = 3, Counter = 4 });
        Send(session, new ExtraBarCastRequest { Time = world.GameNow, Index = 0, Spell = sprint, Counter = 5 });
        var reasons = Since(peer, mark).Where(e => e.Code == (EventCodes)CustomCodes.ExtraBarCastRefused).Select(e => e.P[2] as string).ToList();
        Check(reasons is ["Empty", "NotInBar"], $"op 9002 on an empty slot and with a spell the slot does not hold: 9003 Empty, NotInBar ({string.Join(",", reasons)})");

        // A spell outside the bars in slot 25 stays the fake-slot path's: refused.
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 25, d0 = spells.IdOf("HEROICSTRIKE2"), d1 = me.Id, d4 = 6 });
        Check(peer.Responses[^1].ReturnCode == WorldSession.CastRefused, "HEROICSTRIKE2 (not in the bars) in slot 25 is refused");
        world.Plugins.Shutdown();
    }

    /// <summary>The shipped content/extraspells.json (its Rules) in a temp dir, with RespectUnlocks off (the test characters have an empty board).</summary>
    private static ServerConfig RulesConfig(string dir)
    {
        var content = Path.Combine(dir, "content");
        Directory.CreateDirectory(content);
        var shipped = File.ReadAllText(Path.Combine(DataDir(), "..", "content", "extraspells.json"));
        File.WriteAllText(Path.Combine(content, "extraspells.json"), shipped.Replace("\"RespectUnlocks\": true", "\"RespectUnlocks\": false"));
        return new ServerConfig { JoinByCharacterId = true, ContentPath = content, ModsPath = Path.Combine(dir, "mods"), ModPacks = [], SavesPath = dir };
    }

    private static InventoryItem Sword(string name = "T6_MAIN_SWORD", int quality = 1) =>
        new(name) { Quality = quality, Active = ["HEROICSTRIKE2", "SWORD_SPIN", "MIGHTYBLOW"], Passive = [] };

    /// <summary>Shield, plate set (head STONESKIN, armour TAUNT, shoes DODGE chosen), cape, bag, horse; <paramref name="armor"/> replaces the chest.</summary>
    private static Action<Character> Gear(string armor = "T6_ARMOR_PLATE_SET1", int shoesQuality = 1) => c =>
    {
        c.Equipment[1] = new InventoryItem("T6_OFF_SHIELD");
        c.Equipment[2] = new InventoryItem("T6_HEAD_PLATE_SET1") { Active = ["STONESKIN"], Passive = [] };
        c.Equipment[3] = new InventoryItem(armor) { Active = ["TAUNT"], Passive = [] };
        c.Equipment[4] = new InventoryItem("T6_SHOES_PLATE_SET1") { Quality = shoesQuality, Active = ["DODGE"], Passive = [] };
        c.Equipment[5] = new InventoryItem("T6_BAG");
        c.Equipment[6] = new InventoryItem("T6_CAPE");
        c.Equipment[7] = new InventoryItem("T5_MOUNT_HORSE");
        c.Equipment[8] = null;
        c.Equipment[9] = null;
    };

    private static string Show(IEnumerable<string?> names) => string.Join(" ", names.Select(n => n ?? "-"));

    private static void RulesFill()
    {
        var dir = NewDir();
        using var arena = new Arena(config: RulesConfig(dir));
        var world = arena.World;
        arena.Setup = Gear();
        var (_, _, plain) = arena.Join("Ruler", Sword());
        Check(world.ExtraBars.Table.Problems.Count == 0 && world.ExtraBars.Table.Generators.Enabled, $"the shipped rules load without problems ({string.Join("; ", world.ExtraBars.Table.Problems)})");
        var names = world.ExtraBars.Names(plain);
        var labels = world.ExtraBars.State(plain).Labels;
        Check(names is ["CLEAVE", "INTERRUPT2", "BLOCK", "ENERGY_BARRIER", "OUTOFCOMBATHEAL", "SPRINTHOT", "SPRINT", "SELF_CLEANSE", "SPRINT_CD_REDUCTION", null, null, null]
              && labels[0] == "unchosen" && labels[2] == "map:shieldtype" && labels[6] == "map:*",
              $"sword 2 unchosen (Q CLEAVE, W INTERRUPT2), shield BLOCK, head/armour/shoes 1 unchosen each, cape SPRINT, bag SELF_CLEANSE, horse a sprint ({Show(names)}; {string.Join(",", labels)})");

        arena.Setup = Gear("T6_ARMOR_PLATE_SET1@3", shoesQuality: 5);
        var (_, _, rich) = arena.Join("Richer", Sword("T6_MAIN_SWORD@2", quality: 5));
        names = world.ExtraBars.Names(rich);
        labels = world.ExtraBars.State(rich).Labels;
        var explain = world.ExtraBars.Explain(rich);
        Check(names.Count(n => n is not null) == 12 && names[9] == "SPLITTINGSLASH" && names[10] == "HAMSTRINGSWORD" && names[11] == "ENRAGE"
              && labels[9] == "enchant:2" && labels[10] == "quality:5" && labels[11] == "enchant:3" && explain.Any(l => l.Contains("left out: WANDERLUST")),
              $"sword .2 masterpiece +2, plate armour .3 +1: 12 slots full, the masterpiece shoes' WANDERLUST left out ({Show(names)}; {string.Join(" | ", explain)})");

        arena.Setup = Gear("T6_ARMOR_PLATE_KEEPER");
        var (_, _, keeper) = arena.Join("Keeper", Sword());
        names = world.ExtraBars.Names(keeper);
        var at = names.ToList().IndexOf("FORCESHIELD");
        Check(at == 9 && world.ExtraBars.State(keeper).Labels[at] == "artifact" && names[4] == "OUTOFCOMBATHEAL" && names.Count(n => n is not null) == 10,
              $"the Keeper plate (an artifact) adds its own FORCESHIELD as a bonus, its base pick OUTOFCOMBATHEAL ({Show(names)})");
    }

    private static void ItemQuery()
    {
        var dir = NewDir();
        using var arena = new Arena(config: RulesConfig(dir));
        arena.Setup = Gear();
        var world = arena.World;
        var (session, peer, me) = arena.Join("Asker", Sword());
        var mark = peer.Events.Count;
        Send(session, new ExtraItemQueryRequest { Item = "T6_MAIN_SWORD", Quality = 5, Enchantment = 2, RequestId = 7 });
        Check(!Since(peer, mark).Any(e => e.Code == (EventCodes)CustomCodes.ExtraItemSpells), "no event 9005 before the mod's hello");
        Send(session, new ExtraBarsHelloRequest { Protocol = 2, Mod = "test" });
        var bars = Since(peer, mark).LastOrDefault(e => e.Code == BarsCode).P;
        Check(bars?[11] is string[] { Length: 12 } l11 && l11[0] == "unchosen" && l11[6] == "map:*" && l11[11] == "" && Convert.ToInt32(bars[10]) == 2,
              "event 9001 carries the rule labels (key 11) and protocol 2");
        mark = peer.Events.Count;
        Send(session, new ExtraItemQueryRequest { Item = "T6_MAIN_SWORD", Quality = 5, Enchantment = 2, RequestId = 7 });
        Send(session, new ExtraItemQueryRequest { Item = "NO_SUCH_ITEM", RequestId = 8 });
        var answers = Since(peer, mark).Where(e => e.Code == (EventCodes)CustomCodes.ExtraItemSpells).Select(e => e.P).ToList();
        var a = answers.ElementAtOrDefault(0);
        Check(answers.Count == 2 && a is not null && Convert.ToInt32(a[0]) == 7 && a[1] as string == "T6_MAIN_SWORD@2"
              && a[2] is string[] sp && sp.SequenceEqual(["CLEAVE", "INTERRUPT2", "SPLITTINGSLASH", "HAMSTRINGSWORD"])
              && a[4] is string[] lb && lb.SequenceEqual(["unchosen", "unchosen", "enchant:2", "quality:5"])
              && a[3] is int[] ids && ids[0] == Client.Spells!.IdOf("CLEAVE")
              && answers[1][2] is string[] { Length: 0 } && Convert.ToInt32(answers[1][0]) == 8,
              $"op 9003 for an unworn T6_MAIN_SWORD@2 masterpiece → 9005 with 4 spells and their rules; an unknown item → empty ({(a?[2] is string[] x ? string.Join(",", x) : "none")})");
        var claymore = world.ExtraBars.Resolve(me, Client.Items.Find("T6_2H_CLAYMORE_AVALON")!, 1);
        Check(claymore.Select(x => x.Label).SequenceEqual(["unchosen", "unchosen", "artifact"]) && claymore.All(x => Client.Spells!.Name(x.Id) != "MAJESTIC_SMASH"),
              $"an artifact weapon (Avalon claymore) gets +1: its own MAJESTIC_SMASH is its E already, so another unchosen option ({string.Join(",", claymore.Select(x => $"{Client.Spells!.Name(x.Id)}:{x.Label}"))})");
    }
}
