using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server;

/// <summary>
/// The return codes of a refused HarvestStart (op 52; client enum fp, Albion.Common, 2080-2092, names obfuscated). Only
/// 2083 was captured: 8 of 8 auto-repeat requests that came when the node had 0 charges (capture 002835,
/// HARVEST.md §3). The rest is a guess, documented: fp has 13 members and the client's own check result cn2.a (cxp.u
/// 0x1809f55f0 → cn2.af 0x180b87bf0, 0 = can harvest) has the codes 1-13 that the server can check too (14, not possible
/// while …, is a state of the client's own); with fp = 2079 + code the captured one fits (cn2.a 4, "charges == 0", shows
/// @HARVEST_ERROR_DEPLETED = 2083). The client checks all of them itself before it sends the request (dzh.as9 0x181a041f0
/// shows the texts), so a refusal only reaches it when the server and the client disagree, and what it does with the
/// code is not traced.
/// </summary>
public enum HarvestRefusal : short
{
    /// <summary>cn2.a 1: no harvestable with that id on the player's map or in its view, or none the server can give (its item unknown).</summary>
    NotPossible = 2080,

    /// <summary>cn2.a 2: no tool where the tier needs one (requirestool), or the tool given is not in the player's containers, cannot harvest this resource (items.xml canharvest) or is broken (durability 0).</summary>
    NeedTool = 2081,

    /// <summary>cn2.a 3 (@HARVEST_ERROR_NEEDBETTERTOOL): the tool's tier is too low for the node, its tier difference has no ToolModifier (below -1).</summary>
    ToolTierTooLow = 2082,

    /// <summary>cn2.a 4 (@HARVEST_ERROR_DEPLETED): the node has 0 charges. The captured code.</summary>
    Depleted = 2083,

    /// <summary>cn2.a 7 (@HARVEST_ERROR_PROTECTED): a killed mob's corpse still under the loot protection of another player (<see cref="HarvestableCorpse.MayHarvest"/>).</summary>
    Protected = 2086,

    /// <summary>cn2.a 5: the player is out of reach (characters.xml harvestrange 2 from the node's edge; here <see cref="Harvesting.Range"/> from its centre).</summary>
    OutOfRange = 2084,

    /// <summary>cn2.a 13 (@MISSION_POPUP_SPACEFORITEM_NOSP…): the resource fits neither a stack of it nor a free inventory slot.</summary>
    InventoryFull = 2092,
}

/// <summary>
/// A harvest under way (<see cref="Player.Harvest"/>): the node, the tool (null = by hand), <paramref name="Start"/> = the
/// request's key 0 (the client's GameTimeStamp, also of an auto-repeat request), the duration in seconds, when the
/// world tick finishes it (<see cref="World.Clock"/> time) and <paramref name="Spot"/>, where the player stands for it
/// (the stop point of its last Move at the HarvestStart; a Move to anywhere else cancels it, <see cref="Harvesting.WalkOffTolerance"/>).
/// </summary>
public sealed record PendingHarvest(Harvestable Node, Item? Tool, long Start, float Duration, TimeSpan Due, Vector2 Spot)
{
    /// <summary>The duration in GameTimeStamp ticks.</summary>
    public long DurationTicks => Harvesting.Ticks(Duration);

    /// <summary>Where the client's progress bar ends (HarvestStart key 2 + key 5) and HarvestFinished key 2.</summary>
    public long End => Start + DurationTicks;
}

/// <summary>
/// What a harvest of <see cref="Node"/> with <see cref="Tool"/> takes and gives, as checked by <see cref="Harvesting.Plan"/>:
/// the duration, the charges taken, the amount of <see cref="Item"/> (HarvestFinished key 5) and the tool's wear (a7m raw).
/// </summary>
public sealed record HarvestPlan(Harvestable Node, Item? Tool, float Duration, int ChargesTaken, int Amount, ItemTemplate Item, long DurabilityLoss);

/// <summary>
/// Gathering the harvestables (ROADMAP stage 4a, ARCHITECTURE.md §8 step 14: the static trees and rocks of list 39 that
/// <see cref="MapSpawner"/> places, and since 4b the resource nodes of the client's placement, the same way; an enchanted
/// node gives its RareState item), as the official server does in
/// capture 002835 (analysis/work/cap002835/harvest/HARVEST.md with its verifier's corrections; every
/// number below fits the 15 own harvests of that capture). The flow is the session's (Handlers/World/HarvestHandler.cs):
/// HarvestStart (op 52) → HarvestStart event (59) and an rc-0 answer, the world tick finishes it at the end of the
/// client's progress bar with the burst HarvestableChangeState (46), the tool's NewEquipmentItem, NewSimpleItem
/// (+ InventoryPutItem) and HarvestFinished (61). This class holds the rules: the duration, charges and amounts from
/// harvestables.xml, items.xml and gamedata.xml, the checks of a request, where the resource goes, and the regrowth.
/// Touched only under <see cref="World.Sync"/>.
/// <para>
/// Sent since the destiny board work (Systems/Destiny): UpdateFame (82) {0 player, 1 a7m new total, 2 a7m gained =
/// items.xml famevalue × amount × gamedata ClusterDangerBonus gatheringfamefactor (1 in a safe zone): T2 4.5, T3 9, T4 15
/// for 3/3/2 fiber; T1 has famevalue 0 and gets no fame block at all, 5 true} with the AchievementProgressInfo (152) of
/// the nodes it feeds before it (ADVENTURER_ELDER 1 and GATHER_FIBER_T4 661 in the capture, there × 1.5 premium and × 5
/// learning points). Since stageE: ReputationUpdate (304) {2 = gamedata Reputation level gathergainfactor of the player's level
/// (0.05 NEUTRAL) × fame, 6 2, 7 the harvestable type} (<see cref="Systems.Reputation.Gain"/>). Since PVE-DESIGN step 11 (journals SPEC §B, §C): JournalAchievementProgressUpdate (600, a tracked
/// gather achievement) and PersonalSeasonPointsGained (376) {0 = 3 × sqrt(fame), no premium} before the fame. Not sent yet (HARVEST.md §4-§5): PersonalSeasonPointsGained
/// with its premium factor (376 officially sqrt(13.5 × fame)), UpdateLearningPoints (83) {5 ≈ -0.00875 × fame}; and the
/// premium bonus (key 7 = amount × 0.5 rounded at random, there is no premium here) and the bonus of key 6 (seen only
/// for other players). The burst order is ChangeState, tool, reputation, achievement 1, season points, journal, learning
/// points, achievement 661, fame, item, put, HarvestFinished; T1 sends only the state, the item events and the finish.
/// </para>
/// </summary>
public static class Harvesting
{
    /// <summary>HarvestCancel's answer when nothing of that start is running (client enum fq.a, its only member; 8 of 8 captured cancels).</summary>
    public const short NothingToCancel = 2120;

    /// <summary>
    /// The shortest time between two regrowth steps of a node: the gaps between the state events of a node in view were
    /// never below 15.0 s (14.98 s with receive jitter; 43 non-finish states in 0204, HARVEST.md §6), while the client data
    /// gives a T1 tree 60-180 s.
    /// </summary>
    public static readonly TimeSpan MinimumRegrowth = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Metres added to characters.xml harvestrange (2, measured by the client from the node's edge) for the server's
    /// check from the node's centre and from the player's extrapolated position: 5 m in all. Captured stops 1.25-3.11 m.
    /// </summary>
    public const float RangeSlack = 3f;

    /// <summary>
    /// How far the stop point (key 3) of a Move during a harvest may lie from <see cref="PendingHarvest.Spot"/> and still
    /// keep the harvest: any walk ends the client's harvest (cxp.aft 0x1809f53c0 applies events 59/60 only to its current
    /// state when that is a cuz, the harvest state whose end case 0x3b sets, and key 1 is not older than the state's stamp,
    /// c4.s; a walk is a new state), so only a Move that stays put may come, as the turn towards the node in the instant
    /// of the HarvestStart (+403.473: key 1 = key 3 = the spot, a new key 2). No Move came during any of the 15 captured
    /// harvests. The speed (key 4) tells nothing: the stop Moves carry it too (9.9, 5.5).
    /// </summary>
    public const float WalkOffTolerance = 0.5f;

    /// <summary>
    /// How early a HarvestStart may come and still finish the pending harvest first. The client sends its next request
    /// itself when its bar ends, stamped exactly start + duration (1709: 639276898644027934), and the official server
    /// finishes 0.1-0.9 ms after that point; the world tick here (50 ms) may not have run yet.
    /// </summary>
    public static readonly TimeSpan RepeatTolerance = TimeSpan.FromMilliseconds(100);

    /// <summary>The largest stack of an item without maxstacksize (999 for nearly every stackable item, as .item add).</summary>
    public const int DefaultMaxStack = 999;

    /// <summary>The server's reach check of a HarvestStart: harvestrange + <see cref="RangeSlack"/> from the node's centre.</summary>
    public static float Range(World world) => (float)(world.GameData?.HarvestRange ?? GameData.DefaultHarvestRange) + RangeSlack;

    /// <summary>Seconds as GameTimeStamp ticks (the float of HarvestStart key 5, read as the double it is).</summary>
    public static long Ticks(float seconds) => (long)Math.Round(seconds * (double)TimeSpan.TicksPerSecond);

    /// <summary>
    /// The harvest time factor: by hand the tier's notooltimefactor (2 for the T1 trees), with a tool the type's
    /// ToolModifier timefactor for tool tier − node tier (-1 1.5, 0 1, +1 0.7, …); null = that tool cannot harvest it.
    /// </summary>
    public static double? TimeFactor(HarvestableTemplate type, HarvestableTier tier, ItemTemplate? tool) =>
        tool is null ? tier.NoToolTimeFactor : type.ToolTimeFactors.TryGetValue(tool.Tier - tier.Tier, out var factor) ? factor : null;

    /// <summary>
    /// HarvestStart key 5: harvesttimeseconds × the time factor, as a float of seconds. Captured: FIBER T2 with the T3
    /// sickle 2 × 0.7 = 1.4, T3 3 × 1 = 3, T4 4 × 1.5 = 6, FIBER_CRITTER T4 3 × 1.5 = 4.5, WOOD T1 by hand 1 × 2 = 2 (other
    /// players' 3.86 and 2.548 carry their own gathering-speed bonuses, which this server does not have).
    /// </summary>
    public static float Duration(HarvestableTier tier, double factor) => (float)(tier.HarvestSeconds * factor);

    /// <summary>Charges one harvest takes: maxchargesperharvest, at most what is left (WOOD T1 2: both at once).</summary>
    public static int ChargesTaken(HarvestableTier tier, int charges) => Math.Min(tier.MaxChargesPerHarvest, Math.Max(0, charges));

    /// <summary>
    /// HarvestFinished key 5, the base amount: the yield of the charge level the node is at × the charges taken (WOOD T1 1
    /// × 2 = 2, FIBER T2/T3 3 × 1, T4 2 × 1). Where one harvest spans levels of different yields (WOOD_GIANTTREE T2+), the
    /// current level's yield counts for all of them: not captured. No premium bonus (key 7) here; the yield bonus of key 6 is
    /// the gathering buffs' (<see cref="Systems.Passives.YieldBonus"/>).
    /// </summary>
    public static int Amount(HarvestableTier tier, int charges) => (tier.Level(charges)?.Yield ?? 0) * ChargesTaken(tier, charges);

    /// <summary>
    /// The tool's durability loss per harvest, a7m raw (NewEquipmentItem key 8 units, 10000 = 1): gamedata ToolUse factor
    /// of the node's tier (T1 0, else 0.5) × the resource's itemvalue × the amount. Captured with the T3 sickle (1200):
    /// 3 T2_FIBER 0.5 × 4 × 3 = 6 (→ 1194), 3 T3_FIBER 0.5 × 2 × 3 = 3, 2 T4_FIBER 0.5 × 4 × 2 = 4; T1_WOOD (itemvalue 0) none.
    /// </summary>
    public static long DurabilityLoss(GameData? data, int tier, ItemTemplate resource, int amount)
    {
        var factor = (data?.ToolUseLoss ?? GameData.DefaultToolUseLoss).GetValueOrDefault(tier, 0.5);
        return (long)Math.Round(factor * resource.ItemValue * amount * 10000);
    }

    /// <summary>
    /// The time to the next charge of a node at <paramref name="charges"/>: the tier's respawntimeseconds × a factor drawn
    /// between the respawnfactormin and respawnfactormax of that charge level (harvestables.xml: "RespawnFactor of charge
    /// level n affects time to reach charge level n+1"; WOOD T1 120 s × 0.5-1.5), at least <see cref="MinimumRegrowth"/>;
    /// null = the tier does not regrow (respawntimeseconds 0). The capture fits neither these times (fiber T3 regrew after
    /// 15.6-30.1 s, xml 60-180 s) nor a fixed tick: the official model has a per-node tick of 15 s or more with a chance
    /// (chargeupchance × a global activity quotient, harvestables.xml's own comment), which is open (HARVEST.md §8.3); a
    /// static T1 tree's regrowth was not captured at all. So the client data rules here, the measured gap is the floor.
    /// The result, floor included, is divided by <paramref name="speed"/> (Rates:Regrowth).
    /// </summary>
    public static TimeSpan? RegrowInterval(HarvestableTier tier, int charges, Random random, double speed = 1)
    {
        if (tier.RespawnSeconds <= 0) return null;
        var level = tier.Level(charges);
        double min = level?.RespawnFactorMin ?? 1, max = level?.RespawnFactorMax ?? 1;
        var seconds = tier.RespawnSeconds * (min + random.NextDouble() * Math.Max(0, max - min));
        var interval = TimeSpan.FromSeconds(seconds);
        return (interval < MinimumRegrowth ? MinimumRegrowth : interval) / speed;
    }

    /// <summary>
    /// The checks of a HarvestStart for <paramref name="player"/> now, in the client's own order (cn2.af 0x180b87bf0: reach,
    /// charges, tool, inventory space): the plan, or the refusal and why (for the log). <paramref name="toolId"/> is the
    /// request's key 2, a tool's object id in the inventory (the captured sickle lay in inventory slot 5) or the equipment,
    /// -1 by hand. A corpse under another player's loot protection is refused (<see cref="HarvestRefusal.Protected"/>). Not checked: guild territory, stunned, GvG (no such state here), the tool's destiny
    /// board unlock (the client checks it, as for equipping).
    /// </summary>
    public static (HarvestPlan? Plan, HarvestRefusal Code, string Why) Plan(Player player, long nodeId, long toolId, World world)
    {
        if (player.Map is not { } map || !map.Objects.TryGetValue(nodeId, out var o) || o is not Harvestable node)
            return (null, HarvestRefusal.NotPossible, $"no harvestable {nodeId} on {player.Map?.Cluster ?? player.Character.Cluster}");
        if (!player.Visibility.Known.Contains(node)) return (null, HarvestRefusal.NotPossible, $"{node} is not in the player's view");
        var at = player.PositionAt(DateTime.UtcNow);
        var distance = Vector2.Distance(at, node.Position);
        if (distance > Range(world))
            return (null, HarvestRefusal.OutOfRange, string.Create(CultureInfo.InvariantCulture, $"{node} is {distance:0.0} m away (more than {Range(world):0.#} m)"));
        if (node.Charges <= 0) return (null, HarvestRefusal.Depleted, $"{node} has no charges left");
        if (node.Corpse is { } corpse && !corpse.MayHarvest(player, world.GameNow))
            return (null, HarvestRefusal.Protected, string.Create(CultureInfo.InvariantCulture,
                $"{node} is protected for its killer for {(corpse.ProtectedUntil - world.GameNow) / (double)TimeSpan.TicksPerSecond:0.#} s more"));
        var tier = node.TierData;
        Item? tool = null;
        if (toolId <= 0)
        {
            if (tier.RequiresTool) return (null, HarvestRefusal.NeedTool, $"{node} needs a tool");
        }
        else
        {
            tool = player.Inventory.FindItem(toolId);
            if (tool is null) return (null, HarvestRefusal.NeedTool, $"tool object {toolId} is not in the player's containers");
            if (node.Type.Resource is not { } resource || !tool.Template.CanHarvest.Contains(resource, StringComparer.OrdinalIgnoreCase))
                return (null, HarvestRefusal.NeedTool, $"{tool.Template.Name} cannot harvest {node.Type.Resource ?? node.Type.Name}");
            if (tool.DurabilityRaw <= 0) return (null, HarvestRefusal.NeedTool, $"{tool.Template.Name} is broken");
        }
        if (TimeFactor(node.Type, tier, tool?.Template) is not { } factor)
            return (null, HarvestRefusal.ToolTierTooLow, $"{tool!.Template.Name} (T{tool.Template.Tier}) is too low for {node}");
        if (tier.ItemOf(node.RareState) is not { } itemName || world.Items.Find(itemName) is not { CanCreate: true } item)
            return (null, HarvestRefusal.NotPossible, $"{node} gives '{tier.ItemOf(node.RareState)}', which this server cannot create");
        var amount = Amount(tier, node.Charges);
        if (amount <= 0) return (null, HarvestRefusal.NotPossible, $"{node} yields nothing at {node.Charges} charges");
        // Rates:Harvest: the most the finish may yield (its fraction rounded up), so a harvest that cannot fit is refused here, not after the bar.
        var scaled = world.Rates.Harvest == 1 ? amount : (int)Math.Min(int.MaxValue, Math.Ceiling(amount * world.Rates.Harvest));
        if (scaled <= 0) return (null, HarvestRefusal.NotPossible, $"{node} yields nothing at Rates:Harvest {world.Rates.Harvest.ToString(CultureInfo.InvariantCulture)}");
        if (Fit(player.Inventory.Bag, item, scaled) is null)
            return (null, HarvestRefusal.InventoryFull, $"{scaled} x {item.Name} fit neither a stack nor a free inventory slot");
        var loss = tool is null ? 0 : DurabilityLoss(world.GameData, tier.Tier, item, amount);
        return (new HarvestPlan(node, tool, Duration(tier, factor), ChargesTaken(tier, node.Charges), amount, item, loss), default, "");
    }

    /// <summary>
    /// Where <paramref name="amount"/> of <paramref name="type"/> goes in <paramref name="bag"/> (HARVEST.md §5, the
    /// official server): the first stack of it in slot order that takes the whole amount (maxstacksize, else
    /// <see cref="DefaultMaxStack"/>), else the lowest free slot (captured: 8, then 1 after slot 1 was emptied, then 9, 11);
    /// null = neither, or more than one stack holds (a high Rates:Harvest). A stack is not split over two slots (not captured).
    /// </summary>
    public static (Item? Stack, int Slot)? Fit(Container bag, ItemTemplate type, int amount)
    {
        var max = type.MaxStackSize > 0 ? type.MaxStackSize : DefaultMaxStack;
        for (var slot = 0; slot < bag.Size; slot++)
            if (bag[slot] is { } stack && stack.Template == type && stack.Count + amount <= max) return (stack, slot);
        return amount <= max && bag.FreeSlot() is var free and >= 0 ? ((Item?)null, free) : null;
    }

    /// <summary>
    /// HarvestStart (59) of <paramref name="harvester"/> on <paramref name="node"/> with <paramref name="tool"/> (null = by hand) from
    /// GameTimeStamp <paramref name="start"/> for <paramref name="duration"/> s: {0 harvester, 1 = 2 = start, 3 node, 4 type (also 0, WOOD),
    /// 5 seconds as a float, 6 tool object or -1, 7 tool item id or -1}. The harvester's own, its observers' and the catch-up of a newcomer
    /// (captured with the original start; its key 5 an Int32 of ticks there, the float here, the field's own type).
    /// </summary>
    public static HarvestStartEvent StartEvent(Player harvester, Harvestable node, Item? tool, long start, float duration) => new()
    {
        HarvesterId = harvester.Id,                       // [0]
        Start = start,                                    // [1] the request's key 0 (its GameTimeStamp), also on an auto-repeat
        ProgressStart = start,                            // [2] the same (44/44): the client ends the bar at key 2 + key 5
        HarvestableId = node.Id,                          // [3]
        Duration = duration,                              // [5] seconds, float
        ToolObjectId = tool?.Id ?? -1,                    // [6] -1 by hand
        ToolItemId = tool?.Template.Index ?? -1,          // [7] -1 by hand
        Extra = new() { [4] = (short)node.Type.Id },      // [4] harvestables.xml type, also when 0 (WOOD): the official server sends it
    };

    /// <summary>
    /// An event of <paramref name="harvester"/>'s harvest of <paramref name="node"/> (59, 60, 61) to its observers whose client knows the
    /// node (stage I bots SPEC §1.11, bots-world SPEC §1.5: the official server sends them to everyone in view of the harvester; the remote
    /// handler looks the node up), not to the harvester itself.
    /// </summary>
    public static void ToWatchers(Player harvester, Harvestable node, AlbionEvent e)
    {
        foreach (var o in harvester.Observers)
            if (o.Visibility.KnowsSent(node)) o.Session.Send(e);
    }

    /// <summary>The node <paramref name="about"/> harvests now (a player's harvest or a bot's show); null when none.</summary>
    public static Harvestable? NodeOf(Player about) => about.Harvest?.Node ?? about.Bot?.Harvest?.Node;

    /// <summary>
    /// The catch-up HarvestStart of <paramref name="about"/>'s harvest under way (a player's, or a bot's show) for a new observer
    /// <paramref name="viewer"/> (its spawn block after NewCharacter, or <see cref="VisibilityTracker.FlushLists"/> once the node's list went
    /// out): the original start, so that its bar ends where the harvester's does; null without one, or while the viewer's client does not
    /// hold the node (also while its list waits for the end of the Join: <see cref="VisibilityTracker.KnowsSent"/>).
    /// </summary>
    public static HarvestStartEvent? ForNewObserver(Player about, Player viewer)
    {
        if (about.Harvest is { } h) return viewer.Visibility.KnowsSent(h.Node) ? StartEvent(about, h.Node, h.Tool, h.Start, h.Duration) : null;
        if (about.Bot?.Harvest is { } b) return viewer.Visibility.KnowsSent(b.Node) ? StartEvent(about, b.Node, b.Tool, b.Start, b.Duration) : null;
        return null;
    }

    /// <summary>HarvestableChangeState of <paramref name="node"/> to every player on the map whose client knows it (the harvester among them).</summary>
    public static void SendState(Map map, Harvestable node)
    {
        foreach (var p in map.Players.Values)
            if (p.Visibility.Known.Contains(node)) p.Session.Send(node.BuildChangeState());
    }

    /// <summary>Starts the regrowth of a node below its full charges, unless a step is pending already (it keeps its time); a corpse never regrows (<see cref="Corpses"/>).</summary>
    public static void StartRegrowth(Map map, Harvestable node, TimeSpan now, double speed = 1)
    {
        if (node.Corpse is not null || node.Charges >= node.FullCharges || node.RegrowAt is not null) return;
        if (RegrowInterval(node.TierData, node.Charges, map.Random, speed) is not { } interval) return;
        node.RegrowAt = now + interval;
        map.Regrowing.Add(node);
    }

    /// <summary>
    /// The regrowth steps due by <paramref name="now"/> (the world tick, every map, also one without players: the steps are
    /// timestamps, so a map nobody is on catches up; it stays loaded until its nodes are full, <see cref="MapManager.Unload"/>): each adds the level's respawncharges (1) up to the full charges and
    /// draws the time of the next one (÷ <paramref name="speed"/>, Rates:Regrowth); a node whose charges changed sends one HarvestableChangeState to the players who
    /// know it, as the official server does per +1 (HARVEST.md §6). Its unchanged "tick" states are not sent.
    /// </summary>
    public static void Regrow(Map map, TimeSpan now, double speed = 1)
    {
        for (var i = map.Regrowing.Count - 1; i >= 0; i--)
        {
            var node = map.Regrowing[i];
            var before = node.Charges;
            while (node.RegrowAt is { } at && at <= now)
            {
                var step = Math.Max(1, node.TierData.Level(node.Charges)?.RespawnCharges ?? 1);
                node.Charges = Math.Min(node.FullCharges, node.Charges + step);
                node.RegrowAt = node.Charges < node.FullCharges && RegrowInterval(node.TierData, node.Charges, map.Random, speed) is { } next ? at + next : null;
            }
            if (node.Charges != before)
            {
                SendState(map, node);
                Log.Debug("world", $"{node} regrew to {node.Charges}/{node.FullCharges} charges");
            }
            if (node.RegrowAt is null) map.Regrowing.RemoveAt(i);
        }
    }

    /// <summary>
    /// The world tick's part (<see cref="World.Update"/>): the players' harvests that are due finish (their session sends
    /// the burst), then the map's regrowth steps (at <paramref name="speed"/>, Rates:Regrowth), then the rare state roll of
    /// the resource nodes when it is due.
    /// </summary>
    public static void Update(Map map, TimeSpan now, double speed = 1)
    {
        foreach (var player in map.Players.Values)
            if (player.Harvest is { } h && h.Due <= now) player.Session.FinishHarvest(player);
        Regrow(map, now, speed);
        ResourceNodes.Update(map, now);
    }
}
