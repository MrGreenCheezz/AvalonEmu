using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>StageR items (analysis/work/stageR/items/SPEC.md): studying, overcharge, fame credits, equipment stacks.</summary>
public static class ItemsTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("items: study (ACT 4) a T4 sword at the forge: famevalue 540 × 2.75 = 1485 fame into CRAFT_SWORDS, the sword deleted; ore and a damaged sword refused", StudyItem),
        ("items: fame credits: a re-spec from level 3 to 1 of COMBAT_SWORDS_SWORD gives (progress + 2 levels) × 0.5 × 0.8 credits for that much silver, 1 → 2 costs a level × 0.5; a maxed node turns 20 % of its kill fame into credits; Join key 43 after a relog", FameCreditsRespec),
        ("items: equipment stacks: stack all merges two equal T4 swords (NewEquipmentItem count 2), never a better-quality or a damaged one; a split keeps quality and durability; a stack cannot be equipped, one split off can; kept over a relog", EquipmentStacks),
        ("items: overcharge (op 355) the worn sword: 10 tokens, +100 item power, status 369; extended for free; kept over a relog; at its end the break roll (deleted, or 60 % durability lost)", OverchargeItem),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static ItemStore Catalog => Client.Items;

    private static void StudyItem()
    {
        var sword = Catalog.Find("T4_MAIN_SWORD")!;
        Check(Study.FameValue(Catalog, Catalog.Find("T4_PLANKS")!) == 22.5 && Study.FameValue(Catalog, sword) == 540
              && Study.Fame(Catalog, sword, 1, Client.GameData!.Economy) == 1485 * Pricing.A7m && !Study.GivesFame(Catalog.Find("T4_WOOD")!),
              $"famevalue: T4_PLANKS 2 × 7.5 + T3_PLANKS 7.5 = 22.5, T4_MAIN_SWORD 16 bars + 8 leather × 22.5 = 540, × 2.75 = 1485; raw wood gives none (got {Study.FameValue(Catalog, sword)})");

        using var town = new EconomyTest.Town();
        var world = town.World;
        var board = world.GameData!.Board!;
        var swords = board.Find("CRAFT_SWORDS")!.Id;
        var (session, peer, me) = town.Join("Scholar", 100, new Progression { Levels = new() { [swords] = 0 } },
            new InventoryItem("T4_MAIN_SWORD"), new InventoryItem("T4_ORE", 5), new InventoryItem("T4_MAIN_SWORD") { Durability = sword.FullDurabilityRaw / 2 });
        var forge = town.Station("T8_FORGE");
        var (blade, ore, damaged) = (me.Inventory.Bag[0]!, me.Inventory.Bag[1]!, me.Inventory.Bag[2]!);
        void Op55(ActionOnBuildingStartRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, 7), encrypted: false);
        }
        byte? Refusal(long item, byte counter)
        {
            var answers = peer.Responses.Count;
            Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = forge.Id, p = 4, q = counter, s = [item] });
            return peer.Responses.Skip(answers).ToList() is [{ ReturnCode: WorldSession.StationComponentError } r] ? (byte)L(r.Parameters, 3) : null;
        }
        Check(Refusal(ore.Id, 1) == (byte)StudyError.ItemNotStudyable && Refusal(damaged.Id, 2) == (byte)StudyError.ItemHasNotFullDurability
              && me.Inventory.FindItem(ore.Id) == ore && me.Inventory.FindItem(damaged.Id) == damaged,
              "ore (not crafted at the forge): ItemNotStudyable; the damaged sword: ItemHasNotFullDurability; both kept");

        var mark = peer.Events.Count;
        Op55(new ActionOnBuildingStartRequest { wf = world.GameNow, o = forge.Id, p = 4, q = 3, s = [blade.Id] });
        Check(me.Action is { Act: 4 }, "the study starts");
        town.Run(1);
        var burst = Since(peer, mark);
        var fame = burst.Where(e => e.Code == EventCodes.UpdateFame).ToList();
        Check(me.Action is null && me.Inventory.FindItem(blade.Id) is null
              && burst.Any(e => e.Code == EventCodes.InventoryDeleteItem && L(e.P, 0) == blade.Id)
              && fame is [var f] && L(f.P, 2) == 1485 * Pricing.A7m && me.Character.Fame == 1485 * Pricing.A7m
              && me.Character.Progression!.Progress.GetValueOrDefault(swords) == 1485 * Pricing.A7m
              && burst[^1].Code == EventCodes.ActionOnBuildingFinished,
              $"at the end: the sword deleted, UpdateFame 1485, CRAFT_SWORDS at 1485, 66 last (got {Show(burst)})");
    }

    private static void OverchargeItem()
    {
        using var town = new EconomyTest.Town();
        var world = town.World;
        var c = StarterKit.Default.NewCharacter("Overcharger");
        c.Equipment[0] = new InventoryItem("T4_MAIN_SWORD");
        c.Inventory = [new InventoryItem("UNIQUE_GVGTOKEN_GENERIC", 12)];
        Check(town.Characters.Create(c), "character Overcharger created");
        var (session, peer, me) = town.Rejoin(c.Id);
        var joinStatus = Since(peer, 0).Where(e => e.Code == EventCodes.OverChargeStatus).ToList();
        Check(joinStatus is [var js] && js.P[2] is long[] { Length: 5 } none && none.All(t => t == long.MinValue),
              "the Join sends OverChargeStatus with five empty slots, as captured");
        var sword = me.Inventory.Equipment[0]!;
        var baseIp = Combat.ItemPower(me, sword);
        void Op(AlbionRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, null), encrypted: false);
        }
        int Tokens() => me.Inventory.Bag.Items.Where(i => i.Template.Name == "UNIQUE_GVGTOKEN_GENERIC").Sum(i => i.Count);

        var mark = peer.Events.Count;
        Op(new OverChargeRequest { wg = 0b00011 }); // main and off hand enabled: the off hand is empty
        var status = Since(peer, mark).LastOrDefault(e => e.Code == EventCodes.OverChargeStatus);
        Check(Tokens() == 2 && me.Character.Overcharge is [{ Slot: 0, Stack: 0 } first] && Combat.ItemPower(me, sword) == baseIp + 100
              && status.P is { } sp && L(sp, 1) == 1 && sp[2] is long[] ends && ends[0] == first.End && ends[1] == long.MinValue,
              $"overcharge: 10 tokens taken, slot 0 at stack 0, item power {baseIp} + 100, status flags 1 with its end (got IP {Combat.ItemPower(me, sword)}, tokens {Tokens()}, {Show(Since(peer, mark))}, {status.P?[2]?.GetType()})");
        var firstEnd = me.Character.Overcharge.FirstOrDefault()?.End ?? 0;
        Op(new OverChargeRequest { wg = 1 });
        Check(Tokens() == 2 && me.Character.Overcharge is [{ Stack: 1 } second] && second.End == firstEnd + TimeSpan.FromSeconds(1800).Ticks,
              "extended: stack 1 for no tokens, 60 minutes from the first activation");

        // Kept over a relog: the bonus comes back with the Join.
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        (session, peer, me) = town.Rejoin(c.Id);
        sword = me.Inventory.Equipment[0]!;
        Check(me.Character.Overcharge is [{ Stack: 1 }] && Combat.ItemPower(me, sword) == baseIp + 100
              && Since(peer, 0).Any(e => e.Code == EventCodes.OverChargeStatus && L(e.P, 1) == (1 | 0x40000000)),
              "after a relog: still at stack 1, +100 item power, the Join's status shows it");

        // The end: the break roll at 30 %.
        me.Character.Overcharge = [me.Character.Overcharge[0] with { End = world.GameNow }];
        mark = peer.Events.Count;
        town.Run(0.1);
        var end = Since(peer, mark).Where(e => e.Code == EventCodes.OverChargeEnd).ToList();
        var broke = me.Inventory.Equipment[0] is null;
        Check(me.Character.Overcharge.Count == 0 && end is [var ev] && L(ev.P, 1) == 1 && L(ev.P, 2) == (broke ? 3 : 1)
              && (broke || (sword.DurabilityRaw == sword.Template.FullDurabilityRaw - (long)(0.6 * sword.Template.FullDurabilityRaw) && Combat.ItemPower(me, sword) < baseIp + 100)),
              $"at the end: OverChargeEnd {{1 mask 1, 2 {(broke ? "3 broken: the sword deleted" : "1: 60 % durability lost")}}} (got {Show(Since(peer, mark))})");
    }

    private static void FameCreditsRespec()
    {
        using var town = new EconomyTest.Town();
        var world = town.World;
        var board = world.GameData!.Board!;
        var node = board.Find("COMBAT_SWORDS_SWORD")!;
        var id = node.Id;
        var (session, peer, me) = town.Join("Respecer", 100_000, new Progression { Levels = new() { [id] = 3 }, Progress = new() { [id] = 1000 * Pricing.A7m } });
        Check(node.RespecType == 1 && node.CreditRatio == 5000 && node.RespecModifier == 10000, "COMBAT_SWORDS_SWORD: combat, lpmultiplier 0.5 / famemultiplier 1, respecmodifier 1");
        AlbionLocal.Photon.Serialization.OperationResponse? Respec(int current, int target)
        {
            var answers = peer.Responses.Count;
            lock (world.Sync) session.OnRequest(Request(new ReSpecAchievementRequest { aay = id, aaz = current, aa0 = target }, 9), encrypted: false);
            return peer.Responses.Skip(answers).FirstOrDefault();
        }
        var fame = 1000 * Pricing.A7m + board.LevelFame(id, 1) + board.LevelFame(id, 2);
        var gained = (long)Math.Floor(FameCredits.Credits(node, fame) * 0.8m);
        var silver = (long)Math.Ceiling(gained / (decimal)Pricing.A7m) * Pricing.A7m;
        var mark = peer.Events.Count;
        Check(Respec(3, 1) is { ReturnCode: 0 } && me.Character.Progression!.Levels[id] == 1 && !me.Character.Progression.Progress.ContainsKey(id)
              && me.Character.FameCredits[1] == gained && me.Character.Silver == 100_000 * Pricing.A7m - silver
              && Since(peer, mark).Any(e => e.Code == EventCodes.UpdateReSpecPoints && L(e.P, 2) == gained),
              $"3 → 1: RC 0, level 1, {gained / 10000.0} credits for {silver / 10000} silver, UpdateReSpecPoints (got {me.Character.FameCredits[1]}, {Show(Since(peer, mark))})");
        var cost = FameCredits.Credits(node, board.LevelFame(id, 1));
        Check(Respec(2, 3) is { ReturnCode: 1 } && Respec(1, 1) is { ReturnCode: 9 } && Respec(1, 2) is { ReturnCode: 0 }
              && me.Character.Progression.Levels[id] == 2 && me.Character.FameCredits[1] == gained - cost,
              "a wrong current level: RC 1; no change: RC 9; 1 → 2 costs need(1) × 0.5 credits");
        Check(Respec(2, 100) is { ReturnCode: 2 }, "2 → 100 without the credits: RC 2 (not enough fame credits)");

        // A maxed node: 20 % of its kill fame in credits (auto-respec off).
        me.Character.Progression.Levels[id] = board.LevelCount(id);
        var before = me.Character.FameCredits[1];
        lock (world.Sync) Destiny.Award(me, 100 * Pricing.A7m, new FameContext(FameSource.Kill, 4, [("T4_MAIN_SWORD", 4)]));
        var earned = me.Character.FameCredits[1] - before;
        Check(earned == (long)Math.Floor(FameCredits.Credits(node, 100 * Pricing.A7m) * 0.2m), $"maxed: 100 fame × 0.5 × 0.2 = 10 credits (got {earned / 10000.0})");

        var kept = me.Character.FameCredits[1];
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (_, again, _) = town.Rejoin(me.Character.Id);
        var join = again.Responses.First(r => r.Parameters.TryGetValue(253, out var op) && Convert.ToInt32(op) == (int)OperationCodes.Join);
        Check(join.Parameters[43] is long[] { Length: 5 } k43 && k43[1] == kept, "after a relog: Join key 43 carries the combat credits");
    }

    private static void EquipmentStacks()
    {
        using var town = new EconomyTest.Town();
        var world = town.World;
        var sword = Catalog.Find("T4_MAIN_SWORD")!;
        var c = StarterKit.Default.NewCharacter("Stacker");
        c.Equipment[0] = null;
        c.Inventory = [new InventoryItem("T4_MAIN_SWORD"), new InventoryItem("T4_MAIN_SWORD") { Quality = 2 }, new InventoryItem("T4_MAIN_SWORD"),
            new InventoryItem("T4_MAIN_SWORD") { Durability = sword.FullDurabilityRaw / 2 }];
        Check(town.Characters.Create(c), "character Stacker created");
        var (session, peer, me) = town.Rejoin(c.Id);
        var bag = me.Inventory.Bag;
        void Op(AlbionRequest req)
        {
            lock (world.Sync) session.OnRequest(Request(req, null), encrypted: false);
        }
        var mark = peer.Events.Count;
        Op(new InventoryStackRequest { so = bag.Id, sp = 3 });
        var swords = bag.Items.Where(i => i.Template == sword).ToList();
        Check(swords.Count == 3 && bag[0] is { Count: 2, Quality: 1 } merged && merged.DurabilityRaw == sword.FullDurabilityRaw
              && bag[1] is { Count: 1, Quality: 2 } && bag[3] is { Count: 1 } && bag[2] is null
              && Since(peer, mark).Any(e => e.Code == EventCodes.NewEquipmentItem && L(e.P, 0) == merged.Id && L(e.P, 2) == 2),
              $"stack all: the two equal swords in one stack of 2 (NewEquipmentItem key 2 = 2), quality 2 and the damaged one apart (got {string.Join(", ", bag.Items.Select(i => $"{i.Count}xQ{i.Quality}"))})");

        // A stack does not go into the equipment.
        Op(new InventoryMoveItemRequest { ru = 0, rv = bag.Id, rx = 0, ry = me.Inventory.Equipment.Id });
        Check(me.Inventory.Equipment[0] is null && bag[0] is { Count: 2 }, "the stack of 2 into the main hand: refused, kept in the bag");

        // A split keeps the quality and the durability; the single one can be worn.
        Op(new InventorySplitStackRequest { sd = 0, se = bag.Id, sg = 1 });
        var parts = bag.Items.Where(i => i.Template == sword && i.Quality == 1 && i.DurabilityRaw == sword.FullDurabilityRaw).ToList();
        Check(parts.Count == 2 && parts.All(p => p.Count == 1), "split 1 off the stack: two swords of quality 1 at full durability");
        var single = bag.IndexOf(parts[1]);
        Op(new InventoryMoveItemRequest { ru = single, rv = bag.Id, rx = 0, ry = me.Inventory.Equipment.Id });
        Check(me.Inventory.Equipment[0] == parts[1], "the single sword goes into the main hand");

        // Stacked again and kept over a relog.
        Op(new InventoryMoveItemRequest { ru = 0, rv = me.Inventory.Equipment.Id, rx = 2, ry = bag.Id });
        Op(new InventoryStackRequest { so = bag.Id, sp = 4 });
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (_, _, again) = town.Rejoin(c.Id);
        Check(again.Inventory.Bag.Items.Where(i => i.Template == sword).Select(i => (i.Count, i.Quality)).Order().SequenceEqual([(1, 1), (1, 2), (2, 1)]),
              $"after a relog: the stack of 2, the quality-2 and the damaged sword (got {string.Join(", ", again.Inventory.Bag.Items.Select(i => $"{i.Count}xQ{i.Quality}"))})");
    }
}
