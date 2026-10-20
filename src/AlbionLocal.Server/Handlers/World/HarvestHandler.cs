using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Gathering the static harvestables (stage 4a, ARCHITECTURE.md §8 step 14; the rules in <see cref="Harvesting"/>), as the
/// official server answers it in capture 002835 (analysis/work/cap002835/harvest/HARVEST.md §1, §3, §5).
/// The burst goes to the harvester; 59, 60 and 61 also to everyone in view of the harvester whose client knows the node, as the
/// official server sends them (stage I, <see cref="Harvesting.ToWatchers"/>; a newcomer gets the catch-up 59 after the NewCharacter),
/// 46 to everyone in view of the node (<see cref="Harvesting.SendState"/>).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// HarvestStart (op 52, vp, plaintext): 0 the client's GameTimeStamp (the start), 1 the harvestable's object id, 2 the
    /// tool's object id (inventory or equipment) or -1 by hand. The client sends it after its own checks (cxp.u
    /// 0x1809f55f0), right after a Move that stops it 2 m from the node's edge, and again by itself when its progress bar
    /// ends (the auto-repeat, stamped exactly start + duration) as long as the node had charges left. A harvest still
    /// pending then is finished first (<see cref="Harvesting.RepeatTolerance"/>: the world tick may not have run yet), so
    /// the answer to an auto-repeat that finds the node empty is the captured 2083 after the burst; one not yet due is
    /// replaced (the HarvestCancel event for it; not captured). The checks are <see cref="Harvesting.Plan"/>'s; a refusal is
    /// the bare answer {253, 255} with its code (<see cref="HarvestRefusal"/>) and no event. Accepted: the HarvestStart
    /// event (59) {0 harvester, 1 = 2 = request key 0, 3 node, 4 type, 5 duration in seconds (a float, as every captured
    /// own one; the catch-up of a harvester coming into view carries the ticks in the float, not needed here), 6 tool
    /// object or -1, 7 tool item id or -1} before the answer {253, 255} rc 0 (15/15). The harvest finishes at the end of
    /// the client's bar (start + duration, counted from now and capped at the duration), in the world tick.
    /// A knocked-down player (stage 5e) gets <see cref="HarvestRefusal.NotPossible"/>.
    /// A mounted harvester is dismounted first (stageD mounts SPEC §4.3, 5/5 captured: the dismount events, then the HarvestStart
    /// event and the answer); a mount under way is cancelled.
    /// </summary>
    [Handles(OperationCodes.HarvestStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? HarvestStart(HarvestStartRequest req)
    {
        if (Player is not { } player) return null;
        if (player.Combat.Down is not null)
        {
            // Knocked down (stage 5e): no harvest at 0 health; the code of a harvest that is not possible (not captured).
            Log.Info(Name, $"'{player.Character.Name}' harvest of object {req.HarvestableId} refused: knocked down");
            return new GenericResponse { ReturnCode = (short)HarvestRefusal.NotPossible };
        }
        if (player.Harvest is { } pending)
        {
            if (req.Start >= pending.End - Harvesting.RepeatTolerance.Ticks) FinishHarvest(player);
            else CancelHarvest(player, "a new HarvestStart came before it ended", tellClient: true);
        }
        var (plan, code, why) = Harvesting.Plan(player, req.HarvestableId, req.ToolObjectId, world);
        if (plan is null)
        {
            Log.Info(Name, $"'{player.Character.Name}' harvest of object {req.HarvestableId} refused, {code} ({(short)code}): {why}");
            return new GenericResponse { ReturnCode = (short)code };
        }
        Systems.Mounts.BeforeAction(player, "harvests");
        var node = plan.Node;
        var ticks = Harvesting.Ticks(plan.Duration);
        var remaining = Math.Clamp(req.Start + ticks - GameTime.Now, 0, ticks);
        // The spot: where the player's last Move stops (the client sends the stop Move first; a walk still under way ends there).
        var spot = player.Movement?.Target ?? player.PositionAt(DateTime.UtcNow);
        player.Harvest = new PendingHarvest(node, plan.Tool, req.Start, plan.Duration, world.Clock.Now + TimeSpan.FromTicks(remaining), spot);
        var started = Harvesting.StartEvent(player, node, plan.Tool, req.Start, plan.Duration);
        SendEvent(started);
        Harvesting.ToWatchers(player, node, started); // stage I: the players in view see it gather (official)
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' harvests {node} {(plan.Tool is { } tool ? $"with {tool.Template.Name}" : "by hand")}: {plan.Duration:0.###} s for {plan.Amount} x {plan.Item.Name}"));
        return new GenericResponse();
    }

    /// <summary>
    /// HarvestCancel (op 53, vo, no request id): 0 now, 1 the start (key 0) of the HarvestStart to cancel. A harvest of the
    /// player with that start is stopped without anything taken and answered rc 0 (assumed: never captured, HARVEST.md
    /// §7.4); anything else gets the captured 2120 (<see cref="Harvesting.NothingToCancel"/>): all 8 captured cancels came
    /// 1.5-9.5 ms after a HarvestFinished without key 8, for the auto-repeat request the server had refused with 2083.
    /// No HarvestCancel event: the client stopped its own bar when it sent this (the event is for the other players in view).
    /// </summary>
    [Handles(OperationCodes.HarvestCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? HarvestCancel(HarvestCancelRequest req)
    {
        if (Player is not { } player) return null;
        if (player.Harvest is { } h && h.Start == req.Start)
        {
            CancelHarvest(player, "cancelled by the client", tellClient: false);
            return new GenericResponse();
        }
        return new GenericResponse { ReturnCode = Harvesting.NothingToCancel };
    }

    /// <summary>
    /// A Move of a harvesting player (<see cref="Move"/>): a stop point (key 3) more than <see cref="Harvesting.WalkOffTolerance"/>
    /// from the harvest's spot cancels the harvest, however close to the node it stays (a walk ends the client's harvest;
    /// the reach of <see cref="Harvesting.Range"/> is the HarvestStart's check only), with the HarvestCancel event (60)
    /// {0 harvester, 1 start} to the harvester (the client stops the bar only when key 1 matches, cxp.aft case 0x3c). A
    /// Move that stays on the spot (a turn) keeps it. The client may cancel a harvest itself when the player walks off;
    /// that was not captured, so the server does not rely on it.
    /// </summary>
    private void HarvestMoved(Player player)
    {
        if (player.Harvest is not { } h || player.Movement is not { } move) return;
        var off = Vector2.Distance(move.Target, h.Spot);
        if (off > Harvesting.WalkOffTolerance)
            CancelHarvest(player, string.Create(CultureInfo.InvariantCulture,
                $"the player walks off ({off:0.0} m from its spot, {Vector2.Distance(move.Target, h.Node.Position):0.0} m from the node)"), tellClient: true);
    }

    /// <summary>
    /// Drops the player's pending harvest; <paramref name="tellClient"/> sends the HarvestCancel event (60) {0 harvester, 1 start}, which its
    /// observers that know the node get in every case (stage I: their animation of it ends).
    /// </summary>
    internal void CancelHarvest(Player player, string why, bool tellClient)
    {
        if (player.Harvest is not { } h) return;
        player.Harvest = null;
        var cancel = new HarvestCancelEvent { HarvesterId = player.Id, Start = h.Start };
        if (tellClient) SendEvent(cancel);
        Harvesting.ToWatchers(player, h.Node, cancel);
        Log.Info(Name, $"'{player.Character.Name}' harvest of {h.Node} cancelled: {why}");
    }

    /// <summary>
    /// The end of <paramref name="player"/>'s pending harvest (the world tick when it is due, <see cref="Harvesting.Update"/>;
    /// or the next HarvestStart): the charges are taken and the burst goes out in the captured order with what exists on
    /// this server (HARVEST.md §5): HarvestableChangeState (46) to every player who knows the node; the tool's
    /// NewEquipmentItem (30) with its new durability (key 8; the loss of <see cref="Harvesting.DurabilityLoss"/>, never a
    /// DurabilityChanged 97); the resource: an existing stack gets NewSimpleItem (32) again with its object id and the new
    /// total, a new stack NewSimpleItem + InventoryPutItem (0 object, 1 slot, 2 inventory Guid; no key 3) at the lowest
    /// free slot; then HarvestFinished (61) {0 harvester, 1 start, 2 = start + duration, 3 node, 4 item id, 5 amount, 6
    /// the yield bonus (<see cref="Systems.Passives.YieldBonus"/>: amount × the worn and eaten gathering buffs, its fraction rounded at
    /// random; the item count gets amount + bonus), 7 premium bonus 0 (6 and 7 sent also as 0, as the official server always sends them), 8 charges left, left
    /// out at 0 (the client sets the node's charges from it, cr5.aft 0x180be2fe0 → cn2.aja, and sends its HarvestCancel
    /// for the auto-repeat then), 9 [] and 10 an empty byte[] (no bonus loot; the official wire form)}. Between the tool and
    /// the item: the fame of a T2+ resource with its destiny board events (<see cref="Systems.Destiny.Award"/>); reputation,
    /// season and learning points are not sent (<see cref="Harvesting"/>). The node then
    /// regrows (a corpse does not; it stays, depleted, until its decay, World/Corpses.cs); the character is saved after the item change (<see cref="Player.SaveItems"/>). The node emptied meanwhile
    /// (another player), the tool gone or the inventory full by now: cancelled with the HarvestCancel event, nothing taken.
    /// </summary>
    internal void FinishHarvest(Player player)
    {
        if (player.Harvest is not { } h || player != Player) return;
        var node = h.Node;
        if (player.Map is not { } map || node.Map != map) { player.Harvest = null; return; }
        var tier = node.TierData;
        if (node.Charges <= 0)
        {
            CancelHarvest(player, "the node was emptied meanwhile", tellClient: true);
            return;
        }
        if (h.Tool is { } used && player.Inventory.FindItem(used.Id) != used)
        {
            CancelHarvest(player, $"the tool {used.Template.Name} is no longer in the player's containers", tellClient: true);
            return;
        }
        var item = tier.ItemOf(node.RareState) is { } name ? world.Items.Find(name) : null;
        // Rates:Harvest on the base amount (key 5), its fraction rounded at random as the yield bonus; the fame and the tool's wear keep the unscaled one.
        var baseAmount = Harvesting.Amount(tier, node.Charges);
        var amount = Rates.Scale(baseAmount, world.Rates.Harvest, map.Random);
        // The yield bonus of the worn gathering passives and the gathering food (resourcegatheringbuff, stageD effects batch 3): key 6.
        // With the gathering perk of the player's guild level (guilds SPEC §10.3).
        var bonus = amount <= 0 ? 0 : Systems.Passives.Bonus(amount,
            Systems.Passives.YieldBonus(player, "gatheringyield", node.Type.Resource, tier.Tier) + world.Guilds.GatheringBonus(player), map.Random);
        // The premium bonus (HarvestFinished key 7; stageJ focus-lp SPEC §5.5): the amount × premiumgatheringyieldbonuschance (0.5), the
        // fraction rounded at random; none without premium.
        var chance = Systems.Premium.GatherBonusChance(player);
        var premium = amount <= 0 || chance <= 0 ? 0 : Rates.Scale(amount, chance, map.Random);
        var total = amount + bonus + premium;
        var bag = player.Inventory.Bag;
        if (item is not null && amount <= 0)
        {
            CancelHarvest(player, $"{baseAmount} x {item.Name} round to none at Rates:Harvest {world.Rates.Harvest.ToString(CultureInfo.InvariantCulture)}", tellClient: true);
            return;
        }
        if (item is null || Harvesting.Fit(bag, item, total) is not { } target)
        {
            CancelHarvest(player, $"{total} x {tier.ItemOf(node.RareState)} do not fit the inventory any more", tellClient: true);
            return;
        }
        var (stack, slot) = target;
        player.Harvest = null;
        node.Charges -= Harvesting.ChargesTaken(tier, node.Charges);
        Harvesting.StartRegrowth(map, node, world.Clock.Now, world.Rates.Regrowth);
        Harvesting.SendState(map, node);                                            // 1. HarvestableChangeState
        if (h.Tool is { } tool)
        {
            var toolBefore = tool.DurabilityRaw;
            tool.DurabilityRaw = Math.Max(0, tool.DurabilityRaw - Harvesting.DurabilityLoss(world.GameData, tier.Tier, item, baseAmount));
            if (toolBefore > 0 && tool.DurabilityRaw <= 0) world.Plugins.Broke(player, tool, "gathering"); // Items.OnBreak
            SendItemObject(tool);                                                   // 2. the tool, key 8 = the new durability
        }
        // 3. The fame (T2+; T1 has famevalue 0 and gets none): the destiny board's 151/152 of the resource's nodes, then
        // UpdateFame {1 total, 2 famevalue × amount × gatheringfamefactor, 5 true} (HARVEST.md §4-§5, PROGRESSION.md §2.2).
        // × Rates:Fame × Rates:GatheringFame.
        var fame = Rates.ScaleFame(Systems.Destiny.HarvestFame(item.FameValue, baseAmount, Systems.Combat.DangerOf(world, map)?.GatheringFameFactor ?? 1),
            world.Rates.Fame * world.Rates.GatheringFame);
        // StageE (pvp SPEC §3.4 [infer]): the reputation gain, gathergainfactor × fame (ReputationUpdate reason 2), before the
        // achievements as HARVEST.md's burst order has it.
        Systems.Reputation.Gain(player, fame / 10000.0, Systems.Reputation.Gathering, node.Type.Id);
        // PVE-DESIGN step 11: the journal's gather missions (600 before the fame, as captured) and the gather bounties count the items received.
        Systems.AlbionJournal.Gathered(player, item, total);
        world.Bounties.Gathered(player, item, total);
        world.Plugins.Harvested(player, item.Name, total, "gather"); // the plugins' Harvested
        world.Guilds.Gathered(player, item, total); // guilds SPEC §10.6: the gather contracts
        world.GuildGoals.OnGather(player, item, total); // stage I: the weekly guild goals
        if (fame > 0) Systems.Destiny.Award(player, fame, Systems.Destiny.Harvest(item), source: item);
        Item? added = null;
        if (stack is not null)
        {
            stack.Count += total;                                                   // 4. the existing stack, same object id
            SendEvent(new NewSimpleItemEvent { dax = stack.Id, day = item.Index, daz = (short)Math.Min(short.MaxValue, stack.Count) }.FillEmpty());
        }
        else
        {
            added = CreateItem(item, new InventoryItem(item.Name, total), SpellUsable(player.Progression));  // 4. NewSimpleItem
            bag.Put(slot, added);
            SendEvent(new InventoryPutItemEvent { ckw = added.Id, ckx = slot, cky = bag.Id });               //    + InventoryPutItem
        }
        // The bonus loot (econ SPEC §2.1): the tier's <Loot> rolled once (× Rates:Loot), the items that fit after the resource; keys 9/10.
        var extras = BonusLoot(player, tier.LootOf(node.RareState), map.Random);
        var finished = new HarvestFinishedEvent
        {
            HarvesterId = player.Id,               // [0]
            Start = h.Start,                       // [1] = HarvestStart key 1
            Finish = h.End,                        // [2] the end of the bar (official: 0.1-0.9 ms after it)
            HarvestableId = node.Id,               // [3]
            ItemId = item.Index,                   // [4] items.xml id (T1_WOOD 947)
            Amount = (short)Math.Min(short.MaxValue, amount),              // [5] base amount
            ChargesLeft = node.Charges,            // [8] left out at 0
            BonusLootItems = [.. extras.Select(e => e.Item.Name)], // [9] the bonus loot's uniquenames
            Extra = new() { [6] = (short)Math.Min(short.MaxValue, bonus), [7] = (short)Math.Min(short.MaxValue, premium) }, // [6] yield bonus, [7] premium bonus
        };
        if (extras.Count > 0) finished.BonusLootCounts = [.. extras.Select(e => e.Count)]; // [10] their counts
        else finished.Extra![10] = Array.Empty<byte>();                                       //      none: the official empty byte[]
        SendEvent(finished);
        Harvesting.ToWatchers(player, node, finished); // stage I: the "+N" over its head and the node's charges for the players in view
        if (added is not null) world.Scripts.OnItemAdded(player, added);
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' harvested {amount}{(bonus > 0 ? $" + {bonus}" : "")}{(premium > 0 ? $" + {premium} premium" : "")} x {item.Name} from {node}: {node.Charges}/{node.FullCharges} charges left"
            + (h.Tool is { } t ? string.Create(CultureInfo.InvariantCulture, $", {t.Template.Name} at {t.DurabilityRaw / 10000.0:0.####}") : "")
            + (added is not null ? $", new stack in inventory #{slot}" : "")
            + (extras.Count > 0 ? $", bonus loot {string.Join(", ", extras.Select(e => $"{e.Count} x {e.Item.Name}"))}" : ""));
    }

    /// <summary>
    /// One roll of a harvest's bonus loot (<see cref="HarvestableTier.LootOf"/>; loot.xml through <see cref="LootTables.RollMob"/>, the
    /// chances × Rates:Loot): the items the server can create that fit the inventory now, given at once (<see cref="GiveItem"/>).
    /// loot.xml not read: none.
    /// </summary>
    private List<(ItemTemplate Item, int Count)> BonusLoot(Player player, System.Xml.Linq.XElement? loot, Random random)
    {
        var given = new List<(ItemTemplate, int)>();
        if (loot is null) return given;
        LootTables? tables;
        try { tables = world.Clusters?.Loot; }
        catch (Exception e) when (e is InvalidDataException or IOException) { tables = null; }
        if (tables is null) return given;
        foreach (var (name, amount) in tables.RollMob(loot, random, world.Rates.Loot, world.Config.LootBlackMarketChance, blackMarketSoftCap: world.Config.LootBlackMarketSoftCap))
        {
            if (world.Items.Find(name) is not { CanCreate: true } type || amount <= 0) continue;
            if (!Fits(player.Inventory.Bag, [(type, amount)]))
            {
                Log.Info(Name, $"'{player.Character.Name}' bonus loot {amount} x {name} does not fit the inventory: not given");
                continue;
            }
            GiveItem(player, type, amount);
            given.Add((type, amount));
        }
        return given;
    }
}
