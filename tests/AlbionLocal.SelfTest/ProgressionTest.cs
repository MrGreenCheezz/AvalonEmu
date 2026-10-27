using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Fame and the destiny board (analysis/work/progression/PROGRESSION.md, Systems/Destiny): the board's numbers and §3's
/// level rule against the spec's captured examples, a kill feeding the sword's node with its AchievementProgressInfo, a
/// level-up's FinishedAchievement unlocking the T4 swords, and the fair start (config, kit, GM commands).
/// </summary>
public static class ProgressionTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("progression: board tables (fame per level, elite rows, neighbours) and the level rule of PROGRESSION.md §3 (node 49 + tome)", Rules),
        ("progression: a kill feeds the sword's node (152), .fame add completes its level 0 (151, 152 at level 1), T4 swords become equippable, saved", KillLevelUp),
        ("progression: FairStart: config, kit equippable on an empty board, CreateCharacter, .progress mode/set/show, START and COMBAT_TRAINEE by .fame add", FairStart),
        ("progression: latest fame activity selects the HUD node, completion clears it, tracking survives SQLite and Join", Tracking),
    ];

    private static DestinyBoard Board => Client.GameData!.Board!;

    private static void Tracking()
    {
        using var arena = new Arena();
        var (session, peer, me) = arena.Join("Tracker", new InventoryItem("T2_MAIN_SWORD"),
            ("START", 0), ("COMBAT_SWORDS", 0), ("CRAFT_TRAINEE", 0), ("GATHER_FIBER_T4", 0));
        var p = me.Character.Progression!;
        var swords = Board.Find("COMBAT_SWORDS")!.Id;
        var craft = Board.Find("CRAFT_TRAINEE")!.Id;
        var craftedItem = Client.Items.Find(Board.Nodes[craft].Items[0])!;
        Check(p.Tracked is null && peer.Events.Any(e => Code(e) == (int)EventCodes.FullTrackedAchievementInfo),
            "Join sends an explicit empty tracking list before any fame gain");
        lock (arena.World.Sync) Destiny.Award(me, 10_000, Destiny.Kill(me, 3), guild: false);
        Check(p.Tracked == swords, "combat selects its activity node ahead of general START fame");
        lock (arena.World.Sync) Destiny.Award(me, 10_000, Destiny.Harvest(Client.Items.Find("T4_FIBER")!), guild: false);
        Check(p.Tracked == Board.Find("GATHER_FIBER_T4")!.Id, "harvesting replaces combat with the matching gathering node");
        lock (arena.World.Sync) Destiny.Award(me, 10_000, Destiny.Craft(craftedItem), guild: false);
        var tracking = Since(peer, 0).Last(e => e.Code == EventCodes.FullTrackedAchievementInfo);
        Check(p.Tracked == craft && ((int[])tracking.P[1]!).SequenceEqual([craft]) && L(tracking.P, 0) == me.Id,
            "craft fame replaces combat tracking and sends the full list for this player");
        var mark = peer.Events.Count;
        lock (arena.World.Sync) Destiny.Award(me, 0, Destiny.Craft(craftedItem), guild: false);
        Check(p.Tracked == craft && !Since(peer, mark).Any(e => e.Code == EventCodes.FullTrackedAchievementInfo),
            "zero fame does not switch or resend tracking");
        lock (arena.World.Sync) arena.World.Saves.SaveAll();
        arena.World.Saves.Flush();
        var saved = arena.Characters.Find(me.Character.Id)!.Progression!;
        Check(saved.Tracked == craft && saved.Clone().Tracked == craft, "SQLite and save snapshots retain the selected node");
        var info = saved.BuildFullAchievementInfo();
        Check(info.Flags4[Array.IndexOf(info.Ids, craft)] && info.Flags4.Count(x => x) == 1,
            "Join achievement flags agree with the tracked-node list");
        lock (arena.World.Sync) peer.Disconnect();
        arena.World.Saves.Flush();
        var again = new FakePeer();
        var restoredSession = new WorldSession(again, arena.World);
        again.Disconnected = restoredSession.OnDisconnected;
        restoredSession.OnRequest(Request(new JoinRequest { ss = me.Character.Id }, 1), encrypted: false);
        me = restoredSession.Player!;
        p = me.Character.Progression!;
        Check(p.Tracked == craft && ((int[])Since(again, 0).Single(e => e.Code == EventCodes.FullTrackedAchievementInfo).P[1]!).SequenceEqual([craft]),
            "a real disconnect and Join restore the saved tracking list");
        lock (arena.World.Sync) Destiny.Advance(me, Board.LevelFame(craft, 0), new FameContext(FameSource.Craft, 2, [], craft), out _, out _);
        Check(p.Completed.Contains(craft) && p.Tracked is null && Destiny.Tracked(me.Id, p, Board).cdr!.Length == 0,
            "finishing the selected plain node clears the HUD instead of leaving an invalid bar");
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static void Rules()
    {
        var board = Board;
        int Id(string name) => board.Find(name)!.Id;
        Check(Id("COMBAT_CLOTH_ARMORS") == 41 && Id("COMBAT_CLOTH_ARMORS_SET2") == 49 && Id("GATHER_FIBER_T4") == 661 && Id("ADVENTURER_ELDER") == 1
              && board.StartId == 693 && Id("COMBAT_TRAINEE") == 278 && board.Nodes.Count == 704,
              "node ids in achievements.xml order (41, 49, 661, ADVENTURER_ELDER 1, START 693, COMBAT_TRAINEE 278; 704 nodes)");
        Check(board.LevelFame(41, 64) == 161553 * DestinyBoard.A7m && board.LevelFame(49, 41) == 190454 * DestinyBoard.A7m && board.LevelFame(661, 0) == 30000 * DestinyBoard.A7m
              && board.LevelFame(693, 0) == 45 * DestinyBoard.A7m && board.LevelCount(41) == 100 && board.LevelCount(49) == 120 && board.LevelCount(693) == 1,
              "fame per level (§1.2 table: 161553, 190454, 30000; START 45), COMBAT_SPEC has 20 elite rows");
        Check(board.Nodes[278].Neighbours.Contains(693) && board.Nodes[693].Neighbours.Contains(278) && board.Nodes[661].Levels[0] is { TargetMin: 2, TargetMax: 8, UnlockTier: 4 }
              && board.Nodes[661].Levels[1] is { TargetMin: 4, TargetMax: 4, UnlockTier: 0 },
              "START and COMBAT_TRAINEE are neighbours; GATHER_T4's tier gates 2-8 on row 0, then 4-4");
        // A harvest (HARVEST.md §4): famevalue × amount; T2 and enchanted T4 fiber feed ADVENTURER_ELDER and GATHER_FIBER_T4
        // (row 0 takes T2-T8), not the cloth armour; T1 wood (famevalue 0) gives none.
        var gatherer = new Progression { Levels = new() { [1] = 0, [661] = 0, [41] = 3 } };
        Check(Destiny.HarvestFame(1.5, 3, 1) == 45_000 && Destiny.HarvestFame(7.5, 2, 1) == 150_000 && Destiny.HarvestFame(0, 2, 1) == 0
              && board.Targets(gatherer, Destiny.Harvest(Client.Items.Find("T2_FIBER")!)).SequenceEqual([1, 661])
              && board.Targets(gatherer, Destiny.Harvest(Client.Items.Find("T4_FIBER_LEVEL1")!)).SequenceEqual([1, 661]),
              "harvest fame 4.5 / 15 / 0; T2 and T4.1 fiber feed nodes 1 and 661 (§2.2)");
        // §3's captured example: node 49 at level 41 with v 176387.5155, a 50000 tome → Finished{49, 41}, level 42 v 35933.5155.
        // Key 3 is exactly v / need = 0.1832707; the capture has 0.18326806, which the spec calls exact (an open question for the
        // analysis side: the official rounding is not known), so the capture is only held to verify.py's tolerance of 1e-4.
        var p = new Progression { Levels = new() { [49] = 41 }, Progress = new() { [49] = 1_763_875_155 } };
        var steps = board.Apply(p, 50000 * DestinyBoard.A7m, new FameContext(FameSource.Kill, 4, [], Only: 49));
        Check(steps is [{ Node: 49, Finished: true, Level: 41 }, { Node: 49, Finished: false, Level: 42, Value: 359_335_155, Need: 1_960_680_000 } s] && s.Fraction == (float)(359_335_155.0 / 1_960_680_000) && Math.Abs(s.Fraction - 0.18326806f) < 1e-4
              && s.Json == "[[359335155]]" && p.Levels[49] == 42 && p.Progress[49] == 359_335_155,
              $"§3: Finished {{49, 41}}, then level 42 with v 35933.5155 of 196068 (captured fraction 0.18326806) (got {string.Join("; ", steps)})");
    }

    private static void KillLevelUp()
    {
        using var arena = new Arena();
        var world = arena.World;
        var board = Board;
        var swords = board.Find("COMBAT_SWORDS")!.Id;
        var (session, peer, me) = arena.Join("Swordsman", new InventoryItem("T4_MAIN_SWORD"), ("COMBAT_SWORDS", 0));
        var progression = me.Character.Progression!;
        var t4 = Client.Items.Find("T4_MAIN_SWORD")!;
        Check(!board.CanEquip(progression, t4) && board.CanEquip(null, t4), "COMBAT_SWORDS at level 0: the T4 sword is not unlocked (everything is without a board)");

        // A kill: the sword's node (COMBAT_BASE row 0: 25157 fame, mob T3-T8, item T2-T8) gets the full 202.1, then UpdateFame.
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        lock (world.Sync) ghoul.Health.Set(1, Math.Max(world.GameNow, ghoul.Health.Stamp));
        var mark = peer.Events.Count;
        Send(session, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = 0 });
        arena.Run(0.4);
        var got = Since(peer, mark);
        var fame = got.FindIndex(e => e.Code == EventCodes.UpdateFame);
        var progress = got.Where(e => e.Code == EventCodes.AchievementProgressInfo).ToList();
        Check(ghoul.Dead && fame > 0 && progress.Count == 1 && got.IndexOf(progress[0]) == fame - 2 && got[fame - 1].Code == EventCodes.PersonalSeasonPointsGained && progress[0].P is var a
              && L(a, 0) == me.Id && L(a, 1) == swords && !a.ContainsKey(2) && (string)a[4]! == "[[2021000]]" && F(a, 3) == (float)(2021000.0 / 251_570_000)
              && progression.Progress[swords] == 2_021_000 && !got.Any(e => e.Code == EventCodes.FinishedAchievement),
              $"the kill: AchievementProgressInfo {{1 COMBAT_SWORDS, no level key at 0, 3 v / 25157, 4 [[2021000]]}} right before the season points and UpdateFame (got {Show(got.Take(fame + 1))})");

        // .fame add 30000 COMBAT_SWORDS: level 0 completes (151 with the old level 0), level 1 takes the rest (152 key 2 = 1).
        var replies = new List<string>();
        var gm = new CommandContext(world, me, AccountLevel.Admin, (t, _) => replies.Add(t));
        mark = peer.Events.Count;
        lock (world.Sync) world.Commands.Execute(gm, ".fame add 30000 COMBAT_SWORDS");
        got = Since(peer, mark);
        Check(got.Select(e => e.Code).SequenceEqual([EventCodes.FinishedAchievement, EventCodes.AchievementProgressInfo, EventCodes.UpdateFame])
              && L(got[0].P, 1) == swords && !got[0].P.ContainsKey(2) && L(got[1].P, 2) == 1 && (string)got[1].P[4]! == "[[50451000]]"
              && L(got[2].P, 1) == 302_021_000 && L(got[2].P, 2) == 300_000_000 && !got[2].P.ContainsKey(3) && progression.Levels[swords] == 1,
              $"151 {{1 COMBAT_SWORDS, 2 level 0 (left out)}}, 152 {{2 level 1, 4 [[5045.1]]}}, UpdateFame total 30202.1 (got {Show(got)}; {string.Join(" / ", replies)})");
        Check(board.CanEquip(progression, t4) && board.CanEquip(progression, Client.Items.Find("T4_MAIN_SWORD@2")!) && !board.CanEquip(progression, Client.Items.Find("T5_MAIN_SWORD")!)
              && board.ItemUse(progression).Contains("T4_MAIN_SWORD"),
              "level 1 of COMBAT_SWORDS (row 0 UnlockTier 4): the T4 swords become equippable (enchanted too), T5 not yet");

        lock (world.Sync) world.Saves.SaveAll();
        world.Saves.Flush();
        Check(arena.Characters.Find(me.Character.Id)!.Progression is { } saved && saved.Levels[swords] == 1 && saved.Progress[swords] == 50_451_000
              && arena.Characters.Find(me.Character.Id)!.Fame == 302_021_000,
              "level, progress inside the level and total fame are saved with the character");
    }

    private static void FairStart()
    {
        var board = Board;
        Check(LoadTestConfig(Path.Combine(NewDir(), "none.json"), []).NewCharacters == NewCharacterProgression.FairStart
              && LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=pve"]).NewCharacters == NewCharacterProgression.UnlockAll
              && LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=pve", "--Progression:NewCharacters=fairstart"]).NewCharacters == NewCharacterProgression.FairStart,
              "Progression:NewCharacters: FairStart (official) by default, UnlockAll under pve, FairStart when set");
        var kit = StarterKit.Load(Path.Combine(DataDir(), "..", "content", "starter-fair.json"), Client.Items, StarterKit.FairDefault);
        Check(System.Text.Json.JsonSerializer.Serialize(kit) == System.Text.Json.JsonSerializer.Serialize(StarterKit.FairDefault),
              "content/starter-fair.json is the built-in fair kit");
        var fair = board.FairStart();
        Check(fair.Completed.Count == 0 && fair.Levels.Count == 1 && fair.Levels[693] == 0, "a fair start: nothing finished, START in progress at level 0");
        var equipped = kit.Equipment.OfType<InventoryItem>().Select(i => Client.Items.Find(i.Name)!).ToList();
        Check(equipped.Count == 5 && equipped.All(t => board.CanEquip(fair, t)) && kit.Inventory.OfType<InventoryItem>().All(i => board.CanEquip(fair, Client.Items.Find(i.Name)!))
              && !board.CanEquip(fair, Client.Items.Find("T2_MAIN_SWORD")!) && !board.CanEquip(fair, Client.Items.Find("T4_MAIN_SWORD")!),
              "every item of the fair kit is equippable on the empty board; T2 and T4 swords are not");

        using var arena = new Arena();
        var world = arena.World;
        // CreateCharacter under FairStart: the fair kit and the empty board, saved.
        var config = new ServerConfig { AutoRegister = true, NewCharacters = NewCharacterProgression.FairStart };
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, config, arena.Characters, StarterKit.Default) { World = world, FairStarter = kit };
        login.OnRequest(Request(TestLogin("fairplayer", "password1"), 1), encrypted: true);
        login.OnRequest(Request(new CreateCharacterRequest { h7 = "Newcomer" }, 2), encrypted: true);
        Check(arena.Characters.Find("Newcomer") is { Progression: { Completed.Count: 0 } np } created && np.Levels.Keys.SequenceEqual([693])
              && created.Equipment[0]?.Name == "T1_MAIN_SWORD" && created.Equipment[3]?.Name == "T1_ARMOR_LEATHER_SET1",
              "CreateCharacter with Progression:NewCharacters = FairStart: START in progress only, the T1 kit");

        // A character of before (no board) joined; .progress mode fair gives it an empty board and sends it.
        var (_, peer, me) = arena.Join("Veteran", new InventoryItem("T2_MAIN_SWORD"));
        me.Character.Progression = null;
        var replies = new List<string>();
        var gm = new CommandContext(world, me, AccountLevel.Admin, (t, _) => replies.Add(t));
        void Run(string line) { lock (world.Sync) world.Commands.Execute(gm, line); }
        var mark = peer.Events.Count;
        Run(".progress mode fair");
        var got = Since(peer, mark);
        Check(me.Character.Progression is { } p && me.Progression == p && got.FirstOrDefault(e => e.Code == EventCodes.FullAchievementInfo).P is { } info
              && info[1] is int[] { Length: 0 } && info[2] is int[] ids && ids.SequenceEqual([693]),
              $".progress mode fair: an empty board, FullAchievementInfo {{1 [], 2 [693]}} sent (got {Show(got)})");
        var progression = me.Character.Progression!;

        // .fame add 300 (as a kill with the worn gear): START finishes (151 {693}); COMBAT_TRAINEE becomes available but this
        // gain was shared out before; the next 300 finish it (250 kill fame): the T2 sword becomes equippable.
        mark = peer.Events.Count;
        Run(".fame add 300");
        got = Since(peer, mark);
        Check(got.Select(e => e.Code).SequenceEqual([EventCodes.FinishedAchievement, EventCodes.UpdateFame]) && L(got[0].P, 1) == 693
              && progression.Completed.SequenceEqual([693]) && progression.Levels.ContainsKey(278) && !progression.Levels.ContainsKey(693)
              && board.CanEquip(progression, Client.Items.Find("T2_BAG")!) && !board.CanEquip(progression, Client.Items.Find("T2_MAIN_SWORD")!),
              $"START finished by the first 300: 151 {{693}} then UpdateFame; COMBAT_TRAINEE available; the T2 bag unlocked (got {Show(got)})");
        mark = peer.Events.Count;
        Run(".fame add 300");
        got = Since(peer, mark);
        var finished = got.Where(e => e.Code == EventCodes.FinishedAchievement).Select(e => L(e.P, 1)).ToList();
        Check(finished.Contains(278) && progression.Completed.Contains(278) && board.CanEquip(progression, Client.Items.Find("T2_MAIN_SWORD")!)
              && got[^1].Code == EventCodes.UpdateFame,
              $"COMBAT_TRAINEE finished by the next 300 (151 {{278}}): the T2 sword becomes equippable (got {Show(got)})");

        Run(".progress set COMBAT_SWORDS 10");
        Check(progression.Levels[board.Find("COMBAT_SWORDS")!.Id] == 10 && board.CanEquip(progression, Client.Items.Find("T5_MAIN_SWORD")!)
              && !board.CanEquip(progression, Client.Items.Find("T6_MAIN_SWORD")!),
              $".progress set COMBAT_SWORDS 10: T4 and T5 swords unlocked, T6 not ({string.Join(" / ", replies)})");
        replies.Clear();
        Run(".progress show");
        Check(replies.Count == 1 && replies[0].StartsWith("mode fair") && replies[0].Contains("COMBAT_SWORDS (") && replies[0].Contains("START"),
              $".progress show lists the mode, the finished nodes and the levels (got {string.Join(" / ", replies)})");
        mark = peer.Events.Count;
        Run(".progress mode unlockall");
        got = Since(peer, mark);
        Check(me.Character.Progression is null && me.Progression == world.UnlockAll
              && got.FirstOrDefault(e => e.Code == EventCodes.FullAchievementInfo).P is { } all && all[1] is int[] { Length: 30 },
              $".progress mode unlockall: no board of its own, FullAchievementInfo with the 30 plain nodes finished (got {Show(got.Take(1))})");
    }
}
