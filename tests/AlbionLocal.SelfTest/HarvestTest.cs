using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Gathering the static harvestables (stage 4a, ARCHITECTURE.md §8 step 14) against what the official server sent in
/// capture 002835 (analysis/work/cap002835/harvest/HARVEST.md, harvest_sequence_0204.json): the formulas
/// on the client's data give the captured durations, amounts and tool wear; a Thetford tree harvested by hand and with
/// T1/T2 axes on a <see cref="ManualClock"/> gives the captured messages in the captured order with the captured keys.
/// Runs after <see cref="ClusterDataTest"/>, whose clusters the shared store has resolved by then.
/// </summary>
public static class HarvestTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("harvest: the client data gives the captured durations (1.4/3/6/4.5/2 s), amounts (3/3/2/2/2) and tool wear (6/3/4), item and tool ids", Formulas),
        ("harvest: a Thetford T1 tree by hand and with T1/T2 axes (captured order and keys, 2/1/0.7 s, 2 T1_WOOD, stacking), depleted 2083, cancel 2120, walking off 2/20 m, logout, regrowth on the manual clock, .harvest info, reconnect", Tree),
    ];

    /// <summary>
    /// HARVEST.md §4 on the client's harvestables, items and gamedata: the own harvests of the capture (FIBER T2/T3/T4 and
    /// the T4 dryad corpse with the T3 sickle, WOOD T1 by hand) and the axes on a T1 tree; the T3 sickle's 1200 durability
    /// goes 1194 after 3 T2_FIBER. The items' ids are the captured HarvestFinished key 4 and HarvestStart key 7.
    /// </summary>
    private static void Formulas()
    {
        var harvestables = Client.Clusters!.Harvestables;
        var items = Client.Items;
        var data = Client.GameData!;
        var sickle = items.Find("T3_2H_TOOL_SICKLE")!;
        Check(sickle.Index == 2979 && sickle.CanHarvest.SequenceEqual(["FIBER"]) && sickle.MaxDurability == 1200
              && items.Find("T1_WOOD")?.Index == 947 && items.Find("T2_FIBER")?.Index == 1053 && items.Find("T3_FIBER")?.Index == 1054 && items.Find("T4_FIBER")?.Index == 1055
              && items.Find("T2_2H_TOOL_AXE")!.CanHarvest.SequenceEqual(["WOOD"]) && items.Find("T2_FIBER")!.CanHarvest.Count == 0
              && items.Find("T2_FIBER")!.ItemValue == 4 && items.Find("T3_FIBER")!.ItemValue == 2 && items.Find("T1_WOOD")!.ItemValue == 0 && items.Find("T4_FIBER")!.FameValue == 7.5,
              "T3_2H_TOOL_SICKLE 2979 harvests FIBER (1200 durability), T1_WOOD 947, T2/T3/T4_FIBER 1053/1054/1055 (itemvalue 4/2/4, T4 famevalue 7.5); an axe harvests WOOD");
        Check(data.HarvestRange == 2 && data.ToolUseLoss[1] == 0 && data.ToolUseLoss[2] == 0.5 && data.ToolUseLoss[8] == 0.5,
              "characters.xml harvestrange 2, gamedata ToolUse T1 0, T2-T8 0.5");

        float Time(string type, int tier, string? tool) =>
            harvestables.Find(type) is { } t && Harvesting.TimeFactor(t, t.Tiers[tier], tool is null ? null : items.Find(tool)) is { } f ? Harvesting.Duration(t.Tiers[tier], f) : -1;
        int Amount(string type, int tier) => harvestables.Find(type) is { } t ? Harvesting.Amount(t.Tiers[tier], t.Tiers[tier].Charges) : -1;
        long Loss(int tier, string item, int amount) => Harvesting.DurabilityLoss(data, tier, items.Find(item)!, amount);
        var s = "T3_2H_TOOL_SICKLE";
        Check(Time("FIBER", 2, s) == 1.4f && Time("FIBER", 3, s) == 3f && Time("FIBER", 4, s) == 6f && Time("FIBER_CRITTER", 4, s) == 4.5f && Time("WOOD", 1, null) == 2f,
              $"durations as captured: 1.4, 3, 6, 4.5, 2 s (got {Time("FIBER", 2, s)}, {Time("FIBER", 3, s)}, {Time("FIBER", 4, s)}, {Time("FIBER_CRITTER", 4, s)}, {Time("WOOD", 1, null)})");
        Check(Time("WOOD", 1, "T1_2H_TOOL_AXE") == 1f && Time("WOOD", 1, "T2_2H_TOOL_AXE") == 0.7f && Time("WOOD", 1, "T3_2H_TOOL_AXE") == 0.5f
              && Time("FIBER", 5, s) == -1 && Time("FIBER", 4, "T2_2H_TOOL_SICKLE") == -1,
              "axes on a T1 tree 1 / 0.7 / 0.5 s; a tool two tiers below the node has no ToolModifier (-1 is the lowest)");
        Check(Amount("FIBER", 2) == 3 && Amount("FIBER", 3) == 3 && Amount("FIBER", 4) == 2 && Amount("FIBER_CRITTER", 4) == 2 && Amount("WOOD", 1) == 2
              && Harvesting.ChargesTaken(harvestables.Find("WOOD")!.Tiers[1], 2) == 2 && Harvesting.ChargesTaken(harvestables.Find("FIBER")!.Tiers[2], 3) == 1,
              "amounts as captured: 3, 3, 2, 2 and WOOD T1 1 x both charges = 2");
        Check(Loss(2, "T2_FIBER", 3) == 60_000 && 12_000_000 - Loss(2, "T2_FIBER", 3) == 11_940_000 && Loss(3, "T3_FIBER", 3) == 30_000
              && Loss(4, "T4_FIBER", 2) == 40_000 && Loss(1, "T1_WOOD", 2) == 0,
              "tool wear as captured: 6 (1200 -> 1194), 3, 4 durability points; none for T1_WOOD");
        var wood = harvestables.Find("WOOD")!.Tiers[1];
        var random = new Random(1);
        var draws = Enumerable.Range(0, 200).Select(_ => Harvesting.RegrowInterval(wood, 0, random)!.Value.TotalSeconds).ToList();
        Check(draws.Min() >= 60 && draws.Max() < 180 && draws.Max() - draws.Min() > 100 && wood.Level(0)?.RespawnCharges == 1 && wood.RespawnSeconds == 120
              && Harvesting.RegrowInterval(harvestables.Find("FIBER_CRITTER")!.Tiers[4], 0, random) is null,
              $"regrowth of a T1 tree: 120 s x 0.5-1.5 per charge (got {draws.Min():0}-{draws.Max():0} s); a corpse (respawntimeseconds 0) does not regrow");
    }

    /// <summary>The keys of an event or response as the client gets them (written and read back as Protocol18).</summary>
    private static Dictionary<byte, object?> Wire(object message)
    {
        var w = new ByteWriter();
        if (message is EventData e) Protocol18.WriteEventData(w, e);
        else Protocol18.WriteOperationResponse(w, (OperationResponse)message);
        var r = new ByteReader(w.ToArray());
        return message is EventData ? Protocol18.ReadEventData(r).Parameters : Protocol18.ReadOperationResponse(r).Parameters;
    }

    private static string Name(object message) => message switch
    {
        EventData e => ((EventCodes)Convert.ToInt32(e.Parameters[ReservedKeys.EventCode])).ToString(),
        OperationResponse r => $"{(OperationCodes)Convert.ToInt32(r.Parameters[ReservedKeys.OperationCode])} rc {r.ReturnCode}",
        _ => "?",
    };

    private static bool Keys(Dictionary<byte, object?> p, params byte[] keys) =>
        p.Keys.Where(k => k < 250).Order().SequenceEqual(keys);

    private static void Tree()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData) { Sync = new object(), Clock = clock, Clusters = Client.Clusters };
        var thetford = Client.Clusters!.Get("0000");
        // Three trees within a few metres of each other near the start (WOOD T1, 2 charges), the player 2 m west of each.
        var trees = thetford.Harvestables.OrderBy(h => Vector2.Distance(h.Position, new Vector2(44.5f, 3.5f))).Take(3).ToList();
        Check(trees[0].Position == new Vector2(44.5f, 3.5f) && trees.All(t => t is { Type.Name: "WOOD", Tier.Tier: 1, Charges: 2 }) && Vector2.Distance(trees[2].Position, trees[0].Position) < 12,
              "three T1 trees near (44.5, 3.5)");
        Vector2 Stand(int i) => trees[i].Position - new Vector2(2, 0);

        var c = StarterKit.Default.NewCharacter("Harvester");
        c.Cluster = "0000";
        (c.PositionX, c.PositionY) = (Stand(0).X, Stand(0).Y);
        c.Equipment = [];
        c.Inventory = [new("T1_2H_TOOL_AXE"), new("T2_2H_TOOL_AXE")];
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
        var player = session.Player!;
        var map = player.Map!;
        var nodes = trees.Select(t => map.Objects.Values.OfType<Harvestable>().Single(h => h.Key == t.Key)).ToList();
        Check(nodes.All(player.Visibility.Known.Contains), "the trees are in view");
        var (axe1, axe2) = (player.Inventory.Bag[0]!, player.Inventory.Bag[1]!);

        List<object> Since(int from) => [.. peer.Sequence.Skip(from)];
        void Stop(Vector2 at) => session.OnRequest(Request(new MoveRequest { wf = GameTime.Now, v6 = [at.X, at.Y], v8 = [at.X, at.Y], v9 = 0f }, null), encrypted: false);
        void Tick(double seconds) { clock.Advance(TimeSpan.FromSeconds(seconds)); lock (world.Sync) world.Update(); }
        int Mark() => peer.Sequence.Count;

        // 1. By hand (as the captured T1 bushes 1709 and 1644): event 59 then rc 0, keys as captured; nothing before 2 s;
        // at 2 s the burst ChangeState, NewSimpleItem, InventoryPutItem (new stack, lowest free slot = 2), HarvestFinished
        // without key 8 (0 charges left); NewSimpleItem key 4 (an a7m constant per stack, meaning unknown) is not sent,
        // as on Join. The request is stamped 1 s ahead, so that the server caps the time left at exactly the duration
        // however long the machine takes (start + duration − its receipt).
        var a = nodes[0];
        var start = GameTime.Now + TimeSpan.TicksPerSecond;
        var mark = Mark();
        session.OnRequest(Request(new HarvestStartRequest { Start = start, HarvestableId = a.Id, ToolObjectId = -1 }, 78), encrypted: false);
        var got = Since(mark);
        Check(got.Select(Name).SequenceEqual(["HarvestStart", "HarvestStart rc 0"]), $"HarvestStart event before the rc-0 answer (got {string.Join(", ", got.Select(Name))})");
        var ev = Wire(got[0]);
        Check(Keys(ev, 0, 1, 2, 3, 4, 5, 6, 7) && Convert.ToInt64(ev[0]) == player.Id && Convert.ToInt64(ev[1]) == start && Convert.ToInt64(ev[2]) == start
              && Convert.ToInt64(ev[3]) == a.Id && ev[4] is (short)0 && ev[5] is 2f && Convert.ToInt64(ev[6]) == -1 && Convert.ToInt32(ev[7]) == -1,
              $"event keys 0-7 as captured: 1 = 2 = the request's key 0, 4 WOOD = 0 sent as a short, 5 = 2.0 s (a float), 6/7 -1 by hand (got {string.Join(", ", ev.Select(p => $"{p.Key}={p.Value}"))})");
        Check(Keys(Wire(got[1])) && Convert.ToInt32(Wire(got[1])[ReservedKeys.RequestId]) == 78, "the answer is {253, 255} only");
        Tick(1.5);
        Check(Mark() == mark + 2 && player.Harvest is not null && a.Charges == 2, "nothing at 1.5 s");
        Tick(0.5);
        got = Since(mark + 2);
        Check(got.Select(Name).SequenceEqual(["HarvestableChangeState", "NewSimpleItem", "InventoryPutItem", "HarvestFinished"]),
              $"the burst at 2 s: ChangeState, NewSimpleItem, InventoryPutItem, HarvestFinished (got {string.Join(", ", got.Select(Name))})");
        var (state, simple, put, finished) = (Wire(got[0]), Wire(got[1]), Wire(got[2]), Wire(got[3]));
        var wood = player.Inventory.Bag[2]!;
        Check(Keys(state, 0, 1, 2) && Convert.ToInt64(state[0]) == a.Id && state[1] is (short)0 && state[2] is (byte)0 && a.Charges == 0,
              "HarvestableChangeState {0 node, 1 charges 0, 2 rare 0}, zeros sent");
        Check(wood is { Template.Name: "T1_WOOD", Count: 2 } && Keys(simple, 0, 1, 2) && Convert.ToInt64(simple[0]) == wood.Id && Convert.ToInt32(simple[1]) == 947
              && Convert.ToInt32(simple[2]) == 2 && Keys(put, 0, 1, 2) && Convert.ToInt64(put[0]) == wood.Id && Convert.ToInt32(put[1]) == 2
              && new Guid((byte[])put[2]!) == player.Inventory.Bag.Id,
              "2 T1_WOOD (947) as a new stack in the lowest free slot 2: NewSimpleItem + InventoryPutItem {0 object, 1 slot, 2 inventory Guid}");
        Check(Keys(finished, 0, 1, 2, 3, 4, 5, 6, 7, 9, 10) && Convert.ToInt64(finished[0]) == player.Id && Convert.ToInt64(finished[1]) == start
              && Convert.ToInt64(finished[2]) - start == 20_000_000 && Convert.ToInt64(finished[3]) == a.Id && Convert.ToInt32(finished[4]) == 947
              && Convert.ToInt32(finished[5]) == 2 && finished[6] is (short)0 && finished[7] is (short)0 && finished[9] is string[] { Length: 0 } && finished[10] is byte[] { Length: 0 },
              $"HarvestFinished keys as captured (no key 8 at 0 charges left): 2 = start + 2 s, 4 = 947, 5 = 2, bonus and premium 0, no bonus loot (got {string.Join(", ", finished.Select(p => $"{p.Key}={p.Value}"))})");
        // The auto-repeat request the client sends at the end of its bar finds the tree empty: 2083 and no event; then its
        // HarvestCancel for that request (no request id) gets 2120.
        mark = Mark();
        session.OnRequest(Request(new HarvestStartRequest { Start = start + 20_000_000, HarvestableId = a.Id, ToolObjectId = -1 }, 79), encrypted: false);
        session.OnRequest(Request(new HarvestCancelRequest { Now = start + 20_100_000, Start = start + 20_000_000 }, null), encrypted: false);
        got = Since(mark);
        Check(got.Select(Name).SequenceEqual(["HarvestStart rc 2083", "HarvestCancel rc 2120"]) && Keys(Wire(got[0])) && Convert.ToInt32(Wire(got[0])[ReservedKeys.RequestId]) == 79
              && !Wire(got[1]).ContainsKey(ReservedKeys.RequestId),
              $"depleted: 2083 with the request id, no event; the cancel: 2120 without one (got {string.Join(", ", got.Select(Name))})");
        Check(characters.Find(c.Id)!.Inventory.All(i => i?.Name != "T1_WOOD"), "not saved before the change delay");
        lock (world.Sync) world.Saves.SaveAll();
        world.Saves.Flush();
        Check(characters.Find(c.Id)!.Inventory is [{ Name: "T1_2H_TOOL_AXE" }, { Name: "T2_2H_TOOL_AXE" }, { Name: "T1_WOOD", Count: 2 }], "the wood is saved with the character");

        // 2. With the T1 axe (1 s; the tool re-sent with its durability, unchanged for T1_WOOD): the auto-repeat request comes
        // before the tick has finished the harvest, which then goes first, as captured (burst, then 2083); the wood joins the
        // stack: NewSimpleItem again with the same object id and the new total, no put.
        Stop(Stand(1));
        var b = nodes[1];
        start = GameTime.Now;
        mark = Mark();
        session.OnRequest(Request(new HarvestStartRequest { Start = start, HarvestableId = b.Id, ToolObjectId = axe1.Id }, 80), encrypted: false);
        ev = Wire(Since(mark)[0]);
        Check(ev[5] is 1f && Convert.ToInt64(ev[6]) == axe1.Id && Convert.ToInt32(ev[7]) == axe1.Template.Index, "T1 axe: 1 s, keys 6/7 the axe's object and item id");
        mark = Mark();
        session.OnRequest(Request(new HarvestStartRequest { Start = start + 10_000_000, HarvestableId = b.Id, ToolObjectId = axe1.Id }, 81), encrypted: false);
        got = Since(mark);
        Check(got.Select(Name).SequenceEqual(["HarvestableChangeState", "NewEquipmentItem", "NewSimpleItem", "HarvestFinished", "HarvestStart rc 2083"]),
              $"auto-repeat at the end: ChangeState, the tool, the stack, HarvestFinished, then 2083 (got {string.Join(", ", got.Select(Name))})");
        Check(Convert.ToInt64(Wire(got[1])[0]) == axe1.Id && Convert.ToInt64(Wire(got[1])[8]) == axe1.Template.FullDurabilityRaw && axe1.DurabilityRaw == 4_000_000
              && Convert.ToInt64(Wire(got[2])[0]) == wood.Id && Convert.ToInt32(Wire(got[2])[2]) == 4 && wood.Count == 4 && Convert.ToInt64(Wire(got[3])[2]) - start == 10_000_000,
              "the axe re-sent at its full 400 durability; the wood stack (same object id) at 4; finish = start + 1 s");

        // 3. With the T2 axe (0.7 s), cancelled by the client: rc 0, nothing taken. Then harvests the player walks away from,
        // 2 m (still in reach of the node: a walk ends the client's harvest) and 20 m: the HarvestCancel event {0 player,
        // 1 start}, nothing taken; a Move on the spot (the client's turn in the instant of the HarvestStart) keeps it. Then
        // a logout: the event, nothing taken in the countdown. Out of reach: 2084; a tool that is not the player's: 2081.
        Stop(Stand(2));
        var cTree = nodes[2];
        start = GameTime.Now;
        mark = Mark();
        session.OnRequest(Request(new HarvestStartRequest { Start = start, HarvestableId = cTree.Id, ToolObjectId = axe2.Id }, 82), encrypted: false);
        session.OnRequest(Request(new HarvestCancelRequest { Now = start + 1_000_000, Start = start }, null), encrypted: false);
        Tick(1);
        got = Since(mark);
        Check(got.Select(Name).SequenceEqual(["HarvestStart", "HarvestStart rc 0", "HarvestCancel rc 0"]) && Wire(got[0])[5] is 0.7f && cTree.Charges == 2 && player.Harvest is null,
              $"T2 axe 0.7 s; cancelled: rc 0, no burst, the tree keeps its charges (got {string.Join(", ", got.Select(Name))})");
        var stacked = wood.Count;
        foreach (var (walk, id) in new[] { (2f, 83), (20f, 84) })
        {
            Stop(Stand(2));
            start = GameTime.Now;
            session.OnRequest(Request(new HarvestStartRequest { Start = start, HarvestableId = cTree.Id, ToolObjectId = axe2.Id }, id), encrypted: false);
            mark = Mark();
            Stop(Stand(2));
            var kept = player.Harvest is not null;
            Stop(Stand(2) - new Vector2(walk, 0));
            Tick(1);
            got = [.. Since(mark).Where(m => Name(m).StartsWith("Harvest"))];
            Check(kept && got.Select(Name).SequenceEqual(["HarvestCancel"]) && Wire(got[0]) is var cancel && Convert.ToInt64(cancel[0]) == player.Id && Convert.ToInt64(cancel[1]) == start
                  && cTree.Charges == 2 && wood.Count == stacked && player.Harvest is null,
                  $"a Move on the spot keeps the harvest; walking {walk} m off cancels it: HarvestCancel event {{0 player, 1 start}}, nothing taken (got {string.Join(", ", got.Select(Name))})");
        }
        mark = Mark();
        session.OnRequest(Request(new HarvestStartRequest { Start = GameTime.Now, HarvestableId = cTree.Id, ToolObjectId = axe2.Id }, 85), encrypted: false);
        Stop(Stand(2));
        session.OnRequest(Request(new HarvestStartRequest { Start = GameTime.Now, HarvestableId = cTree.Id, ToolObjectId = 999_999 }, 86), encrypted: false);
        Check(Since(mark).Where(m => Name(m).StartsWith("Harvest")).Select(Name).SequenceEqual(["HarvestStart rc 2084", "HarvestStart rc 2081"]),
              "20 m away: 2084; a tool object the player has not: 2081");
        session.OnRequest(Request(new HarvestStartRequest { Start = GameTime.Now, HarvestableId = cTree.Id, ToolObjectId = axe2.Id }, 87), encrypted: false);
        mark = Mark();
        session.OnRequest(Request(new LogoutStartRequest(), null), encrypted: false);
        session.OnRequest(Request(new LogoutCancelRequest(), null), encrypted: false);
        Tick(1);
        got = [.. Since(mark).Where(m => Name(m).StartsWith("Harvest"))];
        Check(got.Select(Name).SequenceEqual(["HarvestCancel"]) && player.Harvest is null && cTree.Charges == 2 && wood.Count == stacked,
              $"LogoutStart ends the harvest: HarvestCancel event, nothing taken (got {string.Join(", ", got.Select(Name))})");

        // 4. Regrowth (WOOD T1: 120 s x 0.5-1.5 per charge, from the depletion at clock 2 s): nothing before 60 s, a
        // ChangeState to the players in view with the new charges by 180 s, full by 360 s. Meanwhile a depleted tree is
        // re-sent in list 39 with 0 charges when the player comes back into view (never a Leave).
        var depleted = TimeSpan.FromSeconds(2);
        Check(a.RegrowAt is { } due && due - depleted >= TimeSpan.FromSeconds(60) && due - depleted < TimeSpan.FromSeconds(180), "tree A regrows its first charge 60-180 s after its depletion");
        mark = Mark();
        Stop(new Vector2(250, 3.5f));
        Stop(Stand(0));
        var list = Since(mark).Where(m => Name(m) == "NewSimpleHarvestableObjectList").Select(Wire).ToList();
        var ids = list.SelectMany(l => (long[])l[0]!).ToList();
        var charges = list.SelectMany(l => (short[])l[4]!).ToList();
        Check(ids.Contains(a.Id) && charges[ids.IndexOf(a.Id)] == 0 && charges[ids.IndexOf(nodes[2].Id)] == 2
              && !Since(mark).Any(m => Name(m) == "Leave" && nodes.Any(n => n.Id == Convert.ToInt64(Wire(m)[0]))),
              "back in view: list 39 re-sends the depleted tree with 0 charges, the others with 2; no Leave for a tree");
        clock.Advance(depleted + TimeSpan.FromSeconds(59) - clock.Now);
        mark = Mark();
        lock (world.Sync) world.Update();
        Check(Mark() == mark && a.Charges == 0, "no regrowth before 60 s");
        clock.Advance(depleted + TimeSpan.FromSeconds(180) - clock.Now);
        lock (world.Sync) world.Update();
        got = Since(mark).Where(m => Name(m) == "HarvestableChangeState" && Convert.ToInt64(Wire(m)[0]) == a.Id).ToList();
        Check(a.Charges >= 1 && got is [var grown] && Convert.ToInt32(Wire(grown)[1]) == a.Charges, $"by 180 s: one ChangeState with the new charges (got {a.Charges})");
        clock.Advance(depleted + TimeSpan.FromSeconds(361) - clock.Now);
        lock (world.Sync) world.Update();
        Check(a.Charges == 2 && a.RegrowAt is null && b.Charges == 2, "full again by 360 s, no step pending; tree B too");

        // .harvest info by a harvesting player, next to the tree it harvests (stamped ahead as in 1: exactly 2 s left).
        Stop(Stand(1));
        var nearest = map.Objects.Values.OfType<Harvestable>().MinBy(h => Vector2.Distance(h.Position, Stand(1)))!;
        session.OnRequest(Request(new HarvestStartRequest { Start = GameTime.Now + TimeSpan.TicksPerSecond, HarvestableId = nearest.Id, ToolObjectId = -1 }, 88), encrypted: false);
        var replies = new List<string>();
        world.Commands.Execute(new CommandContext(world, player, AccountLevel.GameMaster, (t, _) => replies.Add(t)), ".harvest info");
        var lines = replies.SelectMany(r => r.Split('\n')).ToList();
        Check(lines.Count == 4 && lines[0].StartsWith($"WOOD T1 (type 0) #{nearest.Id} at (") && lines[1] == "  charges 2/2, rare state 0"
              && lines[2] == "  a harvest: 2 x T1_WOOD for 2 charge(s); by hand 2 s; WOOD tools of T0 or higher"
              && lines[3].StartsWith($"  harvesting WOOD T1 #{nearest.Id} by hand, 2.0 s left"),
              $".harvest info: the nearest tree, its charges, what a harvest gives, the harvest under way (got {string.Join(" / ", lines)})");

        // A reconnect: a second connection joins the live character while the first still has that harvest under way; the
        // old player's harvest ends without an event, and nothing goes into its inventory, which is no longer the character's.
        var session2 = new WorldSession(new FakePeer(), world);
        mark = Mark();
        session2.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
        Tick(3);
        Check(session2.Player?.Character == player.Character && player.Harvest is null && nearest.Charges == 2 && !Since(mark).Any(m => Name(m).StartsWith("Harvest")),
              "a reconnect drops the old connection's harvest: no event, nothing taken");

        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
