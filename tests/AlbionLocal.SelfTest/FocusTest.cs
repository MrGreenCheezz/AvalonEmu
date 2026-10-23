using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Focus, learning points and premium (analysis/work/stageJ/focus-lp/SPEC.md §7): Premium:Mode, the focus bar and its cost in crafting, the
/// use flags, the learning points' accrual and the auto-learn split.
/// </summary>
public static class FocusTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("focus-lp: premium (pve Free, official None): Join keys 90/91/40, Login key 35, the season's nonpremium-only rewards (re-judged for older saves), 4 % market tax", PremiumState),
        ("focus-lp: focus bar 10000/day while premium (Join 27/28/30/31), 416.67 after an hour, capped; costs 4021/711/577/39195/1/0; partial use; returns 36.7/53.9/46.7 %", FocusFormulas),
        ("focus-lp: smelter batch with focus (CraftingFocusUpdate −540 after NewBuilding, more back); op 298 turns it off (no key 2, no response), the next Join has no key 32", CraftWithFocus),
        ("focus-lp: learning points 15571 s → +5, stamp +14400 s; the minute tick sends 83 kind 2; none without premium", Accrual),
        ("focus-lp: auto-learn split (GATHER_FIBER_T4: 393 LP), op 312 merge into 155, the burst 83 → 152 → UpdateFame 12/13/14", AutoLearn),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static ServerConfig Free => new() { JoinByCharacterId = true, PremiumMode = PremiumMode.Free };

    private static Dictionary<byte, object?> JoinOf(FakePeer peer) =>
        peer.Responses.Last(r => Convert.ToInt16(r.Parameters[ReservedKeys.OperationCode]) == (short)OperationCodes.Join).Parameters;

    private static void PremiumState()
    {
        var pve = ServerConfig.Load(Path.Combine(NewDir(), "none.json"), []);
        var official = ServerConfig.Load(Path.Combine(NewDir(), "none.json"), ["--Preset=official"]);
        Check(pve is { PremiumMode: PremiumMode.Free, FocusStart: 30_000, LearningPointsStart: 500, FocusCrafting: true, FocusMax: null }
              && official is { PremiumMode: PremiumMode.None, FocusStart: 0, LearningPointsStart: 0 }
              && ServerConfig.Load(Path.Combine(NewDir(), "none.json"), ["--Premium:Mode=none", "--Focus:Max=data", "--Premium:FameFactor=2"]) is { PremiumMode: PremiumMode.None, FocusMax: null, PremiumFameFactor: 2 },
              "presets: Premium:Mode pve Free, official None; Focus:Start 30000 / 0, LearningPoints:Start 500 / 0; 'data' = the client data's value");
        var now = DateTime.UtcNow.Ticks;
        Check(Premium.ListDays(pve) == 365 && Premium.ListDays(official) == 0, "Login key 35: remaining premium days, not UTC ticks");

        var nonpremium = new PersonalSeasonData.Reward("LP", "learningpointsreward", null, 5, BoughtPremium: false, Premium: false, NonPremium: true);
        var bought = new PersonalSeasonData.Reward("ITEM", "itemreward", "T4_ORE", 1, BoughtPremium: true, Premium: false, NonPremium: false);
        using (var town = new EconomyTest.Town(config: Free))
        {
            var (_, peer, me) = town.Join("Premium", 0);
            var join = JoinOf(peer);
            Check(L(join, 90) > town.World.GameNow + TimeSpan.FromDays(300).Ticks && L(join, 91) == 31 && L(join, 40) == 2880 * 10_000 && Premium.Is(me)
                  && !Seasons.Eligible(me, nonpremium) && Seasons.Eligible(me, bought)
                  && Pricing.MarketTax(300 * Pricing.A7m, town.World.Economy, Premium.TaxFactor(town.World, me.Character.Id)) == 12 * Pricing.A7m,
                  $"Free: Join key 90 a year ahead, 91 = 31, 40 = 2880 s; the learningpointsreward (nonpremium only) not claimable, a boughtpremium one is; 300 silver sold: 12 tax");
            // A save from before Free: its states of the unlock are judged again (the server's own debt stays as stored).
            var log = new SeasonLog { Rewards = { ["LP"] = Seasons.Claimable, ["ITEM"] = Seasons.Ineligible } };
            var owed = new SeasonLog { Rewards = { ["ITEM"] = Seasons.Ineligible }, Owed = { "ITEM" } };
            Check(Seasons.StateOf(me, log, nonpremium) == Seasons.Ineligible && Seasons.StateOf(me, log, bought) == Seasons.Claimable
                  && Seasons.StateOf(me, owed, bought) == Seasons.Ineligible,
                  "an older save under Free: its boughtpremium reward claimable now, its learningpointsreward not; an owed one stays the server's");
        }
        using (var town = new EconomyTest.Town())
        {
            var (_, peer, me) = town.Join("Plain", 0);
            var join = JoinOf(peer);
            Check(!join.ContainsKey(90) && !join.ContainsKey(30) && !join.ContainsKey(40) && L(join, 91) == 31 && !Premium.Is(me) && Seasons.Eligible(me, nonpremium)
                  && Pricing.MarketTax(300 * Pricing.A7m, town.World.Economy, Premium.TaxFactor(town.World, me.Character.Id)) == 24 * Pricing.A7m,
                  "None: no keys 90, 30 and 40 (key 91 stays 31), the nonpremium reward claimable, 8 % tax");
        }
    }

    private static void FocusFormulas()
    {
        static CraftRecipe Recipe(double focus, int maxReturn = int.MaxValue) => new(0, 0, 0, 1, focus, [new CraftResource("T4_ORE", 2, 0, maxReturn)]);
        Check(Focus.Cost(Recipe(4021), 1, 0) == 4021 && Focus.Cost(Recipe(4021), 1, 250) == 711 && Focus.Cost(Recipe(4021), 1, 280) == 577
              && Focus.Cost(Recipe(16085), 3, 30) == 39195 && Focus.Cost(Recipe(1), 1, 500) == 1 && Focus.Cost(Recipe(0), 1, 0) == 0
              && Focus.Cost(Recipe(54, maxReturn: 0), 1, 0) == 0 && Focus.Cost(Recipe(54, maxReturn: 0) with { ReturnProductNotResource = true }, 1, 0) == 54,
              "costs: 4021 → 711 (250 units) → 577 (280); 16085 × 3 at 30 → 39195; at least 1; no craftingfocus or not returnable → 0");
        var back = Client.Crafting.Bonus("0000", "ore");
        var efficiency = Client.GameData!.Economy.FocusEfficiency;
        Check(Math.Abs(Pricing.ReturnFraction(back) - 0.367) < 0.0005 && Math.Abs(Pricing.ReturnFraction(back + efficiency) - 0.539) < 0.0005
              && Math.Abs(Pricing.ReturnFraction(back + efficiency * 0.5) - 0.467) < 0.0005,
              "Thetford ore: 36.7 % back without focus, 53.9 % with, 46.7 % with half the cost covered");

        using var town = new EconomyTest.Town(config: Free);
        var (_, peer, me) = town.Join("Focused", 0);
        var join = JoinOf(peer);
        var now = town.World.GameNow;
        Check(Math.Abs(Focus.RegenPerSecond(me) - 10000.0 / 86400) < 1e-9 && Math.Abs(Convert.ToSingle(join[30]) - 0.11574074f) < 1e-7
              && Convert.ToSingle(join[28]) == 30000 && !join.ContainsKey(27) && L(join, 31) > 0,
              "Join: 28 = 30000, 30 = 0.11574074 per second, 31 the stamp; 27 left out at 0 focus");
        var c = me.Character;
        (c.CraftingFocus, c.FocusStamp) = (0, now);
        var hour = now + TimeSpan.FromHours(1).Ticks;
        Check(Math.Abs(Focus.Current(me, hour) - 416.67) < 0.01 && Focus.Current(me, now + TimeSpan.FromDays(10).Ticks) == 30000,
              "regeneration: 416.67 after an hour from 0, at most 30000");
        Focus.Settle(me, hour);
        c.CraftingFocus = 1000;
        Check(Focus.Spend(me, 2000, hour) == (1000, 0.5) && c.CraftingFocus == 0 && Focus.Spend(me, 10, hour) == (0, 0),
              "partial: 1000 of a 2000 cost used, half the bonus; none left: nothing");
        Premium.Set(me, false, hour);
        Check(Focus.RegenPerSecond(me) == 0 && Focus.Current(me, hour + TimeSpan.FromHours(5).Ticks) == 0, "without premium: no regeneration");
    }

    private static void CraftWithFocus()
    {
        using var town = new EconomyTest.Town(config: Free);
        var world = town.World;
        var c = StarterKit.Default.NewCharacter("Smelter");
        c.Progression = new Progression(); // an own empty board: no destiny reduction, the cost is the recipe's
        c.CraftingFocus = 1000;
        c.Inventory = [new InventoryItem("T4_ORE", 40), new InventoryItem("T3_METALBAR", 20)];
        Check(town.Characters.Create(c), "character created");
        var (session, peer, me) = town.Rejoin(c.Id);
        var bag = me.Inventory.Bag;
        int Count(string name) => bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);
        var bar = Client.Items.Find("T4_METALBAR")!;
        var smelter = town.Station("T8_SMELTER");
        var bonus = Client.Crafting.Bonus("0000", "ore");
        void Batch(byte counter, out List<(EventCodes Code, Dictionary<byte, object?> P)> events)
        {
            var mark = peer.Events.Count;
            lock (world.Sync) session.OnRequest(Request(new ActionOnBuildingStartRequest { wf = world.GameNow, o = smelter.Id, p = 1, q = counter, u = bar.Index, w = 10 }, counter), encrypted: false);
            town.Run(4.5);
            events = Since(peer, mark);
        }

        // 10 bars: 10 × 54 = 540 focus at the start (CraftingFocusUpdate right after the NewBuilding: no silver, no UpdateMoney), 53.9 % back.
        Batch(1, out var withFocus);
        var update = withFocus.FindIndex(e => e.Code == EventCodes.CraftingFocusUpdate);
        var full = Pricing.ReturnFraction(bonus + world.Economy.FocusEfficiency);
        Check(update == 1 && withFocus[0].Code == EventCodes.NewBuilding && Convert.ToSingle(withFocus[update].P[2]) == -540 && Convert.ToSingle(withFocus[update].P[3]) == 460
              && me.Character.CraftingFocus < 461 && Count("T4_METALBAR") == 10 && Count("T4_ORE") == 20 + (int)Math.Floor(20 * full + 1e-9),
              $"with focus: CraftingFocusUpdate {{2 −540, 3 460}} second, {full * 100:0.#} % of the ore back (got ore {Count("T4_ORE")}; {Show(withFocus.Take(3))})");

        // Op 298 {0: 1} (key 1 left out = false): no response, bit 1 off; the next batch spends none (no key 2) and gives the plain share.
        var answers = peer.Responses.Count;
        lock (world.Sync) session.OnRequest(Request(new ChangeUseFlagRequest { e7 = 1 }, 5), encrypted: false);
        Check(peer.Responses.Count == answers && !me.Character.UseFlag(1) && me.Character.UseFlag(0), "ChangeUseFlag {0: 1}: no response, the focus flag off, the learning point flag stays");
        var (ore, carry) = (Count("T4_ORE"), me.ReturnCarry.GetValueOrDefault("T4_ORE"));
        Batch(2, out var without);
        var plain = without.Single(e => e.Code == EventCodes.CraftingFocusUpdate).P;
        Check(!plain.ContainsKey(2) && Count("T4_ORE") == ore - 20 + (int)Math.Floor(20 * Pricing.ReturnFraction(bonus) + carry + 1e-9),
              $"without focus: CraftingFocusUpdate without key 2, 36.7 % back (got ore {Count("T4_ORE")} of {ore})");

        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (_, again, _) = town.Rejoin(c.Id);
        var join = JoinOf(again);
        Check(!join.ContainsKey(32) && join[99] is true && town.Characters.Find(c.Id)?.UseFlags == 0b001, "saved: the next Join has no key 32, key 99 true (use flags 001)");
    }

    private static void Accrual()
    {
        using var town = new EconomyTest.Town(config: Free);
        var world = town.World;
        var (_, peer, me) = town.Join("Learner", 0);
        var c = me.Character;
        var now = world.GameNow;
        (c.LearningPoints, c.LpStamp) = (0, now);
        Check(LearningPoints.Accrue(me, now + TimeSpan.FromSeconds(15571).Ticks, send: false) == 5 * 10_000 && c.LearningPoints == 5 * 10_000
              && c.LpStamp == now + TimeSpan.FromSeconds(14400).Ticks,
              "15571 s of premium: +5 learning points, the stamp 14400 s on (the rest carries)");

        // The world tick: the first sets its minute, the next pays the point that is due with UpdateLearningPoints kind 2.
        c.LpStamp = world.GameNow - TimeSpan.FromSeconds(2880).Ticks;
        lock (world.Sync) world.Update();
        var mark = peer.Events.Count;
        town.Clock.Advance(TimeSpan.FromSeconds(61));
        lock (world.Sync) world.Update();
        var paid = Since(peer, mark).Where(e => e.Code == EventCodes.UpdateLearningPoints).ToList();
        Check(paid is [var e] && L(e.P, 0) == me.Id && L(e.P, 1) == 6 * 10_000 && L(e.P, 5) == 10_000 && L(e.P, 6) == LearningPoints.Payout && L(e.P, 4) == 2880 * 10_000,
              $"the minute tick: 83 {{1 6 points, 4 2880 s, 5 +1, 6 2}} (got {Show(paid)})");

        Premium.Set(me, false, world.GameNow);
        var stamp = c.LpStamp = world.GameNow;
        Check(LearningPoints.Accrue(me, stamp + TimeSpan.FromDays(1).Ticks, send: false) == 0 && LearningPoints.SecondsPerPoint(me) == 0, "without premium: nothing accrues");
    }

    private static void AutoLearn()
    {
        Check(DestinyBoard.Split(67_500, 150_125_000, 1_000_000, 300_000_000, 350_000, 50_000) == (67_500, 337_500, 393)
              && DestinyBoard.Split(67_500, 150_125_000, 0, 300_000_000, 350_000, 50_000) == (67_500, 67_500, 0),
              "PROGRESSION §4: GATHER_FIBER_T4 at 15012.5 of 30000 (35 LP), 6.75 fame: 33.75 progress for 0.0393 LP; without LP the plain rule");

        using var town = new EconomyTest.Town(config: Free);
        var world = town.World;
        var board = world.GameData!.Board!;
        var fiber = board.Find("GATHER_FIBER_T4")!.Id;
        var helmets = board.Find("COMBAT_LEATHER_HEADS_SET2")!.Id;
        Check(board.LevelFame(fiber, 0) == 300_000_000 && board.LevelLp(fiber, 0) == 350_000 && board.LevelLp(helmets, 43) == 50_000,
              "LP prices: ceil(105 × 0.3333) = 35 at GATHER_FIBER_T4 level 0, ceil(39 × 0.125) = 5 at COMBAT_LEATHER_HEADS_SET2 level 43");
        var c = StarterKit.Default.NewCharacter("AutoLearner");
        c.Progression = new Progression { Levels = new() { [fiber] = 0, [helmets] = 0 }, Progress = new() { [fiber] = 150_125_000 } };
        c.LearningPoints = 100 * 10_000;
        Check(town.Characters.Create(c), "character created");
        var (session, peer, me) = town.Rejoin(c.Id);

        // Op 312: {0 [helmets]}, then {0 [fiber]} merge; {1 [helmets]} takes it out again (stageH ops-audit §5).
        void AutoLearnOp(int[]? add, int[]? remove)
        {
            lock (world.Sync) session.OnRequest(Request(new SetAchievementsAutoLearnRequest { afq = add, afr = remove }, null), encrypted: false);
        }
        AutoLearnOp([helmets], null);
        AutoLearnOp([fiber], null);
        var both = me.Character.AutoLearn?.Ids;
        AutoLearnOp(null, [helmets]);
        Check(both is { } b && b.SequenceEqual(new[] { fiber, helmets }.Order()) && me.Character.AutoLearn?.Ids is [var only] && only == fiber,
              "op 312: {164} ∪ {661} = both, then {1: [164]} leaves 661");

        // 4.5 fame (a T2 fiber) × 1.5 premium = 6.75 to the node: 83 {5 −393, 6 0} before its 152, UpdateFame {12 = 13 = 27, 14 = 0.0393}.
        var mark = peer.Events.Count;
        lock (world.Sync) Destiny.Award(me, 45_000, Destiny.Harvest(Client.Items.Find("T2_FIBER")!) with { Only = fiber }, journals: false, guild: false);
        var burst = Since(peer, mark);
        var codes = burst.Select(e => e.Code).ToList();
        var lp = burst.Single(e => e.Code == EventCodes.UpdateLearningPoints).P;
        var fame = burst.Single(e => e.Code == EventCodes.UpdateFame).P;
        Check(codes.SequenceEqual([EventCodes.UpdateLearningPoints, EventCodes.AchievementProgressInfo, EventCodes.UpdateFame])
              && L(lp, 5) == -393 && L(lp, 1) == 1_000_000 - 393 && !lp.ContainsKey(6) && L(fame, 12) == 270_000 && L(fame, 13) == 270_000 && L(fame, 14) == 393
              && me.Character.LearningPoints == 1_000_000 - 393 && me.Character.Progression!.Progress[fiber] == 150_125_000 + 337_500,
              $"the burst 83, 152, 82 with 393 LP spent and 27 fame bought (got {Show(burst)})");

        // The next Join: 150 key 5 flags the auto-learn node, 155 key 1 lists it.
        lock (world.Sync) peer.Disconnect();
        world.Saves.Flush();
        var (_, again, _) = town.Rejoin(c.Id);
        var info = Since(again, 0).Single(e => e.Code == EventCodes.FullAchievementInfo).P;
        var auto = Since(again, 0).Single(e => e.Code == EventCodes.FullAutoLearnAchievementInfo).P;
        var ids = (int[])info[2]!;
        Check(auto[1] is int[] and [var listed] && listed == fiber && info[5] is bool[] flags && flags[Array.IndexOf(ids, fiber)] && !flags[Array.IndexOf(ids, helmets)],
              "saved: 155 key 1 = [661], 150 key 5 true for it only");
    }
}
