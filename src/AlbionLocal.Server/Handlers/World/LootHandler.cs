using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Taking a mob's loot bag (stage 5c, analysis/work/stage5/SPEC.md §5c.4; the one captured pickup, 114814 +331 s:
/// RegisterToObject → the items and AttachItemContainer; InventoryMoveGivenItems → InventoryState of the bag, the moved
/// items again, InventoryState of the inventory and DetachItemContainer once the bag is empty). Neither request is answered (the official server sent no response).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// RegisterToObject (op 45, zu): 0 an object id. The client sends it when it opens an object's container (a loot bag,
    /// but also buildings and other objects, which stay unanswered as before, except a bank: its default vault tab,
    /// <see cref="PushBankVault"/>). For a <see cref="LootBag"/> (or a dead player's <see cref="PlayerLootBag"/>, stageE) of this player
    /// (one of its looters, or in the killer's party: <see cref="LootBag.MayOpen"/>) within reach (characters.xml useobjectrange + <see cref="Content.CombatSettings.LootRangeSlack"/>
    /// from the bag's centre): every item object of the bag (NewEquipmentItem / NewSimpleItem), then AttachItemContainer
    /// (99) {0 loot id, 1 the bag's container Guid, 2 <see cref="ViewGuid"/>, 3 the item object id per slot, 5 the slot count}. No response. Nothing
    /// while knocked down (stage 5e), neither here nor in <see cref="InventoryMoveGivenItems"/>.
    /// </summary>
    /// <summary>
    /// AttachItemContainer key 2 (aku.bsv): the Guid the client's own container view of <paramref name="bag"/> is registered under.
    /// c13.b(aku) 0x180A2E270 looks the view up by (key 0 object id, key 2 Guid) and drops the event when there is none, so the
    /// loot window stays empty. A loot chest's view comes from clj.a5 0x180B46690: Guid.Empty, but the local character's Guid
    /// (cpx+0x1F0, our Character.Id) for a personal chest (k22, or k11 without k17, which we never send). Mob bags: Guid.Empty.
    /// (stageN mists-chest SPEC.)
    /// </summary>
    internal static Guid ViewGuid(Player player, LootObject bag) => bag is LootChest { Personal: true } ? player.Character.Id : Guid.Empty;

    [Handles(OperationCodes.RegisterToObject, Status = SessionStatus.InWorld)]
    private AlbionResponse? RegisterToObject(RegisterToObjectRequest req)
    {
        if (Player is { } registered)
        {
            if (registered.Map?.Objects.GetValueOrDefault(req.zg) is Building forge)
            {
                if (OpeningTutorial.ForgeLocked(registered, forge)) return null;
                OpeningTutorial.BuildingInteracted(registered, forge);
            }
            registered.RegisteredTo = req.zg; // stage 6: ContainerOpen of a bank requires it (BankHandler)
            DetachVaults(registered, v => v.Building != req.zg); // another object: the vault tabs of the bank close
            PushBankVault(registered, req.zg); // a bank: its default tab into the main view (BankHandler)
            if (registered.Map?.Objects.GetValueOrDefault(req.zg) is TutorialQuestGiver)
                OpeningTutorial.Request(registered, req.zg, action: 0, slot: 0, selection: 0);
        }
        if (Player is not { Map: { } map } player || map.Objects.GetValueOrDefault(req.zg) is not LootObject bag) return null;
        if (player.Combat.Down is not null)
        {
            Log.Info(Name, $"'{player.Character.Name}' may not open {bag}: knocked down");
            return null;
        }
        if (!bag.MayOpen(player) || !InReach(player, bag))
        {
            Log.Info(Name, $"'{player.Character.Name}' may not open {bag} ({(InReach(player, bag) ? "not a looter" : "out of reach")})");
            return null;
        }
        if (bag is LootChest mine && world.Corrupted.ChestRefused(player, mine) is { } refused) // stageL corrupted: a run's chests are its owner's
        {
            Log.Info(Name, $"'{player.Character.Name}' may not open {bag}: {refused}");
            return null;
        }
        // StageL hellgates phase B: a match's chests are the winners', also one a winner has opened (a shared chest's Items are anyone's view).
        if (bag is LootChest won && world.Hellgates.ChestRefused(player, won) is { } lost)
        {
            Log.Info(Name, $"'{player.Character.Name}' may not open {bag}: {lost}");
            return null;
        }
        Mounts.BeforeAction(player, "loots"); // stageD mounts SPEC §4.3
        if (bag is LootChest chest) world.Dungeons.OpenChest(player, chest); // stageG: a dungeon chest opened straight away rolls its loot now
        var items = bag.ItemsFor(player); // a personal chest: the player's own loot (stageH)
        foreach (var item in items.Items) SendItem(item);
        SendEvent(new AttachItemContainerEvent { bst = bag.Id, bsu = items.Id, bsv = ViewGuid(player, bag), bsw = items.ObjectIds(), bsy = items.Size });
        player.OpenBag = bag;
        Log.Info(Name, $"'{player.Character.Name}' opened {bag}");
        return null;
    }

    /// <summary>
    /// InventoryMoveGivenItems (op 39, wi): 0 source container Guid, 1 its revision, 2 target container Guid, 3 its revision,
    /// 4 item object ids, 5 the count to take per item (the client's c13 map of object id → count; 0 or missing = all of
    /// it). "Take all" on a loot bag sends it (LootGui.OnClickTakeAll, with only the items the client finds room for; a shift+click or
    /// a drag out of the loot is an InventoryMoveItem, <see cref="MoveFromLoot"/>), shift+click in the bank window too. Supported: the open loot bag
    /// into the inventory, and the inventory to or from an open vault tab (<see cref="MoveGivenVaultItems"/>; logged
    /// otherwise). Where a count goes is our own choice (the captures moved single items only, so they cannot tell; it is
    /// not <see cref="GiveItem"/>'s one-stack rule): a stackable count first fills every inventory stack of its type with
    /// room, in slot order; the rest goes into the lowest free slot, the object itself when all of it moves, else a new
    /// object of that count (an existing stack is sent again as HARVEST.md §5 does for a gained one). What finds no room stays in
    /// the bag (a full inventory: the bag keeps the item, or the rest of its count). The answer, as captured (114814 +336.85):
    /// InventoryState (28) of the bag {0 object id per slot, 2 Guid, 3 the request's key 1}, then every object that changed
    /// sent again (NewSimpleItem with its new count / NewEquipmentItem: the client drops the item objects that left the
    /// bag's state and builds them anew from these), then InventoryState of the inventory {3: key 3}, and when the bag is
    /// empty DetachItemContainer (100) {0 its Guid} and its Leave, else the other players with it open get its state
    /// (<see cref="AfterLootTake"/>). The player hooks' OnItemAdded per new inventory object, then the items are saved. No response.
    /// </summary>
    [Handles(OperationCodes.InventoryMoveGivenItems, Status = SessionStatus.InWorld)]
    private AlbionResponse? InventoryMoveGivenItems(InventoryMoveGivenItemsRequest req)
    {
        if (Player is not { } player) return null;
        var inventory = player.Inventory.Bag;
        if (player.OpenBag is not { Map: not null } bag || bag.ItemsFor(player).Id != req.ro || req.rq != inventory.Id)
        {
            if (player.OpenVaults.ContainsKey(req.ro) && req.rq == inventory.Id || req.ro == inventory.Id && player.OpenVaults.ContainsKey(req.rq))
                return MoveGivenVaultItems(player, req);
            Log.Warn(Name, $"InventoryMoveGivenItems from {req.ro} to {req.rq}: only an open loot bag into the inventory and an open vault tab to or from it are supported, ignored");
            return null;
        }
        if (player.Combat.Down is not null || !InReach(player, bag))
        {
            Log.Info(Name, $"'{player.Character.Name}' {(player.Combat.Down is not null ? "is knocked down" : "is out of reach of " + bag)}: nothing taken");
            return null;
        }
        var source = bag.ItemsFor(player);
        var (changed, added, taken) = MoveGiven(player, source, inventory, req.rs ?? [], req.rt);
        SendEvent(new InventoryStateEvent { ck0 = source.ObjectIds(), ck2 = source.Id, ck3 = req.rp });
        foreach (var item in changed.Distinct()) SendItem(item);
        SendEvent(new InventoryStateEvent { ck0 = inventory.ObjectIds(), ck2 = inventory.Id, ck3 = req.rr });
        var left = source.Count;
        if (taken.Count > 0 || left == 0) AfterLootTake(player, bag, source, changed); // emptied: Detach and Leave (a personal chest: Empty for this player)
        foreach (var item in added) world.Scripts.OnItemAdded(player, item);
        if (taken.Count > 0) player.SaveItems();
        var asked = req.rs?.Length ?? 0;
        Log.Info(Name, $"'{player.Character.Name}' took {(taken.Count == 0 ? "nothing" : string.Join(", ", taken))} from {bag}"
            + (left > 0 ? $" ({left} left{(taken.Count < asked ? ": no room" : "")})" : ""));
        return null;
    }

    /// <summary>
    /// InventoryMoveGivenItems between the inventory and an open vault tab (shift+click in the bank window, either way; stage
    /// 6b): <see cref="MoveGiven"/> with the vault's weight limit (<see cref="VaultTakes"/>: an item that does not fit stays),
    /// answered as the loot bag's move: InventoryState of the source {0, 2, 3 key 1}, the changed objects, InventoryState of
    /// the target {3 key 3}. Not captured (the official answer to a vault move is unknown); the vault stays attached when
    /// emptied. The items are saved. Nothing while knocked down. No response.
    /// </summary>
    private AlbionResponse? MoveGivenVaultItems(Player player, InventoryMoveGivenItemsRequest req)
    {
        var from = player.FindContainer(req.ro)!;
        var to = player.FindContainer(req.rq)!;
        if (player.Combat.Down is not null)
        {
            Log.Info(Name, $"'{player.Character.Name}' is knocked down: nothing moved between the inventory and the vault");
            return null;
        }
        var (changed, _, moved) = MoveGiven(player, from, to, req.rs ?? [], req.rt);
        SendEvent(new InventoryStateEvent { ck0 = from.ObjectIds(), ck2 = from.Id, ck3 = req.rp });
        foreach (var item in changed.Distinct()) SendItem(item);
        SendEvent(new InventoryStateEvent { ck0 = to.ObjectIds(), ck2 = to.Id, ck3 = req.rr });
        if (moved.Count > 0) player.SaveItems();
        var into = player.OpenVaults.ContainsKey(to.Id);
        Log.Info(Name, $"'{player.Character.Name}' moved {(moved.Count == 0 ? "nothing" : string.Join(", ", moved))} {(into ? "into" : "out of")} the vault");
        return null;
    }

    /// <summary>
    /// Moves the given objects of <paramref name="from"/> (by id, with the count per item: 0 or missing = all of it) into
    /// <paramref name="to"/>: a stackable count first fills every stack of its type with room in <paramref name="to"/>, in slot
    /// order; the rest goes into the lowest free slot, the object itself when all of it moves, else a new object of that count.
    /// What finds no room stays; an item over a vault's weight limit (<see cref="VaultTakes"/>) does not move at all. Returns the
    /// objects to send again (changed stacks, moved objects, the part that stayed), the new objects of <paramref name="to"/>
    /// and a "name xN" per moved item.
    /// </summary>
    private (List<Item> Changed, List<Item> Added, List<string> Moved) MoveGiven(Player player, Container from, Container to, long[] ids, short[]? counts)
    {
        var changed = new List<Item>();
        var added = new List<Item>();
        var taken = new List<string>();
        for (var i = 0; i < ids.Length; i++)
        {
            if (from.Find(ids[i]) is not { } item) continue;
            var want = counts is { } c && i < c.Length && c[i] > 0 ? Math.Min((int)c[i], item.Count) : item.Count;
            if (!VaultTakes(player, to, item.Template, want, from))
            {
                Log.Info(Name, $"'{player.Character.Name}': {item.Template.Name} x{want} is over the vault's weight limit, not moved");
                continue;
            }
            var left = want;
            var max = MaxStack(item.Template);
            if (max > 1)
                foreach (var stack in to.Items.Where(s => s.StacksWith(item) && s.Count < max).ToList())
                {
                    if (left == 0) break;
                    var n = Math.Min(left, max - stack.Count);
                    stack.Count += n;
                    item.Count -= n;
                    left -= n;
                    changed.Add(stack);
                }
            if (left > 0 && to.FreeSlot() is var slot && slot >= 0)
            {
                var moved = item;
                if (left == item.Count) from.Take(from.IndexOf(item));
                else
                {
                    moved = MakeItem(item.Template, (item.Source ?? new InventoryItem(item.Template.Name)) with { Count = left, Quality = item.Quality },
                        SpellUsable(player.Progression));
                    item.Count -= left;
                }
                to.Put(slot, moved);
                changed.Add(moved);
                added.Add(moved);
                left = 0;
            }
            if (item.Count == 0) from.Take(from.IndexOf(item)); // all of it went into stacks
            else if (from.Contains(item) && left < want) changed.Add(item); // a part stays
            if (want - left > 0) taken.Add($"{item.Template.Name} x{want - left}");
        }
        return (changed, added, taken);
    }

    /// <summary>
    /// TakeSilver (op 54, abo): 0 client time, then 1 one silver object id, or 2 true and 3 several (103829: {0, 2 true, 3
    /// [the pile]}, 0.8 s after the NewSilverObject). Each pile must be on the player's map, its own (NewSilverObject key 6) or its
    /// party's (<see cref="SilverPile.MayTake"/>)
    /// and within reach (as a loot bag). Per pile, as the captured own pickups: its Leave, OtherGrabbedLoot (278) {0 me, 2 my
    /// name, 3 true, 5 net}, UpdateMoney, TakeSilver (62) {0 me, 1 now, 2 the pile, 3 gross, 5 tax, 7 premium, 8 factor}
    /// (<see cref="Combat.SilverValue"/> with Economy:SilverPickupFactor and SilverPickupTax: by default 1 and 0, so gross =
    /// net = amount × factor, keys 5 and 7 left out as on the official pile taken without premium and guild; the captures' own
    /// pickups had 1.5 and 15 %; a guild member's tax is Guilds:SilverTax, into its guild's account). The other players who saw the pile and know the taker get the same pair without
    /// keys 5 and 7 (another player's pickups of 103829 and 114814). No response on success; <see cref="TakeSilverRefused"/>
    /// when none of the ids could be taken (gone, someone else's, too far, knocked down).
    /// </summary>
    /// <summary>
    /// The RC of a TakeSilver that took nothing: the only failed one captured (103829, a pile not yet takeable, taken by the
    /// retry 0.2 s later) was answered 2167, the last value of the client's short enum fr (2160..2167); an RC the client
    /// does not know would show its generic error 1 instead.
    /// </summary>
    public const short TakeSilverRefused = 2167;

    [Handles(OperationCodes.TakeSilver, Status = SessionStatus.InWorld)]
    private AlbionResponse? TakeSilver(TakeSilverRequest req)
    {
        if (Player is not { Map: { } map } player) return null;
        long[] ids = req.agp ? req.agq ?? [] : [req.ago];
        var taken = 0;
        foreach (var id in ids.Distinct())
        {
            if (map.Objects.GetValueOrDefault(id) is not SilverPile pile || !pile.MayTake(player) || player.Combat.Down is not null || !InReach(player, pile))
                continue;
            if (taken == 0) Mounts.BeforeAction(player, "takes silver"); // stageD mounts SPEC §4.3
            // A dead player's silver (stageE E3) is the victim's own: it moves as it was, no pickup factor, no tax [infer].
            var fromPlayer = pile.SourceKind == SilverPile.FromPlayer;
            // × the premium silver factor (premiumsilverlootfactor 1.5, Premium:SilverFactor; stageJ focus-lp SPEC §5.5): key 7 true, as captured.
            var bonus = fromPlayer ? 1 : world.Config.SilverPickupFactor * Premium.SilverFactor(player);
            // Guilds:SilverTax (guilds SPEC §10.5): a member's tax goes to its guild's account in place of Economy:SilverPickupTax.
            var guild = fromPlayer ? null : world.Guilds.TaxGuild(player);
            var (gross, tax, net) = Combat.SilverValue(pile.Amount, pile.Factor, bonus, fromPlayer ? 0 : guild is not null ? world.Config.GuildSilverTax : world.Config.SilverPickupTax);
            if (guild is not null)
            {
                world.Guilds.Deposit(guild, tax);
                world.GuildGoals.TaxPaid(guild, player, tax); // stage I: the Member Logs' TAXPAID
            }
            var factor = (long)Math.Round(pile.Factor * 10000);
            var watchers = map.ObserversOf(pile).Where(p => p != player && player.Observers.Contains(p)).ToList();
            Combat.RemoveSilver(pile, "taken");
            SendEvent(new OtherGrabbedLootEvent { de6 = player.Id, de8 = player.Character.Name, de9 = true, dfb = net });
            Wallet.Credit(player, net, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pile.Amount} silver of {pile.Source}, {tax / 10000.0:0.####} tax"));
            SendEvent(new TakeSilverEvent { ddn = player.Id, d0r = world.GameNow, d0s = pile.Id, d0t = gross, d0v = tax, d0x = bonus != 1, d0y = factor });
            foreach (var w in watchers)
            {
                w.Session.Send(new OtherGrabbedLootEvent { de6 = player.Id, de8 = player.Character.Name, de9 = true, dfb = net });
                w.Session.Send(new TakeSilverEvent { ddn = player.Id, d0r = world.GameNow, d0s = pile.Id, d0t = gross, d0y = factor });
            }
            taken++;
        }
        if (taken > 0) return null;
        Log.Info(Name, $"'{player.Character.Name}' TakeSilver of [{string.Join(", ", ids)}]: nothing to take (gone, not its own, out of reach or knocked down)");
        return new GenericResponse { ReturnCode = TakeSilverRefused };
    }

    /// <summary>
    /// UseLootChest (op 253, ach): 0 GameTimeStamp, 1 the chest's object id, 2 the ChestState the client shows. The client sends it for a
    /// locked chest (states 0/2/3, at most once per 5 s: its callback cz5.a.b shows "locked" on rc <see cref="ChestLocked"/>) and for an Open one
    /// under a template flag (state 7); an Unlocked chest it opens with the slot-25 cast instead (<see cref="FakeSlotCasts.StartChest"/>, stageM
    /// chest-slot25 NOTES). A chest within reach, openable, of a standing player opens (<see cref="RandomDungeons.UseChest"/>) and the answer is rc 0.
    /// Refused (logged): <see cref="ChestLocked"/> in those locked states (BossLocked, protected by mobs or watched), rc 1 otherwise.
    /// </summary>
    [Handles(OperationCodes.UseLootChest, Status = SessionStatus.InWorld)]
    private AlbionResponse? UseLootChest(UseLootChestRequest req)
    {
        AlbionResponse Refused(string why, short rc = 1)
        {
            Log.Info(Name, $"UseLootChest {req.aif} refused: {why}");
            return new UseLootChestResponse { ReturnCode = rc };
        }
        if (Player is not { Map: { } map } player || player.Combat.Down is not null) return Refused("not standing in the world");
        if (map.Objects.GetValueOrDefault(req.aif) is not LootChest chest) return Refused("no such chest on the map");
        if (!InReach(player, chest)) return Refused($"{chest} out of reach");
        if (chest.ViewerState(player).State is var state and (0 or 2 or 3)) return Refused($"{chest} locked (ChestState {state})", ChestLocked);
        if (world.Dungeons.UseChest(player, chest) is { } why) return Refused(why);
        return new UseLootChestResponse();
    }

    /// <summary>The UseLootChest rc of a locked chest (0x278E): the client's callback cz5.a.b shows its "locked" message (1 or 2) only for it.</summary>
    public const short ChestLocked = 10126;

    /// <summary>
    /// Whether <paramref name="player"/> is within useobjectrange + the slack of <paramref name="o"/>'s centre (a loot bag, a silver pile);
    /// for a chest at least as far as its opening cast reaches (<see cref="FakeSlotCasts.ChestReach"/>: the client casts it from the
    /// edge of castrange and then opens the loot window from there).
    /// </summary>
    private bool InReach(Player player, WorldObject o)
    {
        var reach = (world.GameData?.UseObjectRange ?? 2) + world.Combat.LootRangeSlack;
        if (o is LootChest chest) reach = Math.Max(reach, world.FakeCasts.ChestReach(chest));
        return Vector2.Distance(player.PositionAt(DateTime.UtcNow), o.Position) <= reach;
    }
}
