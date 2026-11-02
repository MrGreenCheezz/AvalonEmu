using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>A trade waits for the invitee's answer (op 163 or 162), or both windows are open.</summary>
public enum TradeState : byte { Invited, Open }

/// <summary>An offered item: the object and its count when it was offered (a different count later takes it out of the offer).</summary>
public sealed record Offered(Item Item, int Count);

/// <summary>
/// One side of a trade: its player, the offer by trade slot (0 .. Trade:Slots - 1), the offered silver and gold (a7m raw, not reserved:
/// checked again at the commit; gold only with Trade:AllowGold) and its accept.
/// </summary>
public sealed class TradeSide(Player p)
{
    public Player Player { get; } = p;

    public SortedDictionary<int, Offered> Slots { get; } = [];

    public long Silver;

    public long Gold;

    public bool Accepted;
}

/// <summary>
/// A trade (stageJ trade SPEC §2.2): its id (the client's ctt+0x10, never 0), the inviter <see cref="A"/> and the invitee <see cref="B"/>,
/// the revision every Update raises (the client's ctt starts at 1 and takes only a higher one; op 166 names the one it accepts),
/// when an invitation lapses and when the offer last changed (the accept delay), GameTimeStamps.
/// </summary>
public sealed class PlayerTrade(int id, TradeSide a, TradeSide b, long expires)
{
    public int Id { get; } = id;

    public TradeSide A { get; } = a;

    public TradeSide B { get; } = b;

    public TradeState State;

    public int Revision = 1;

    public long InviteExpires = expires;

    public long LastChange;

    public TradeSide Side(Player p) => p == A.Player ? A : B;

    public TradeSide Other(Player p) => p == A.Player ? B : A;

    /// <summary>The sides whose window is open: the inviter's from the start (rc 0 of op 161 opens it), the invitee's from op 163.</summary>
    public IEnumerable<TradeSide> Open => State == TradeState.Open ? [A, B] : [A];
}

/// <summary>
/// The player trade window (stageJ trade SPEC §2). An invitation (op 161) is checked in the order of §2.3 and answered at once: rc 0
/// opens the inviter's window, the invitee gets InvitationPlayerTrade (175) and its 10 s popup; it lapses after Trade:InviteSeconds.
/// Op 163 opens the trade (Update 178 and AcceptChange 180 to both). Offers (ops 164 item, 165 remove, 167 silver) each raise the
/// revision, reset both accepts and go to every open window as an Update per recipient (its own offer with object ids, the
/// partner's without); a refused one gets a resync Update, since the client changed its own window before asking. An accept
/// (op 166) counts only for the current revision and Trade:AcceptDelaySeconds after the last change; withdrawing always counts.
/// When both accept, <see cref="Commit"/> exchanges the offers atomically under the world lock. With the account shop, both wallets
/// and character inventories commit before delivery; legacy repositories use <see cref="Persistence.SaveScheduler.SaveTogether"/>.
/// A cancel (op 162, too far, the map left, a knockdown, the
/// lapse, a server stop) sends PlayerTradeCancel (177) to both: an invitee that declined still holds its trade object until it.
/// Nothing moves before the commit, so a disconnect or a crash before it loses nothing. Each tick (<see cref="Update"/>) checks the
/// range and takes out offered items that left the bag or changed their count (the client locks them; this is the backstop).
/// Bots never trade (the pve-i bots are not in <see cref="Map.Players"/>; an IsBot check comes with that merge). Touched only under
/// <see cref="World.Sync"/>.
/// </summary>
public sealed class Trades(World world)
{
    // ---- op 161 return codes (f65.aq 0x181C48160, enum iw) ----
    public const short YouAlreadyTrading = 6441;
    public const short PlayerAlreadyTrading = 6442;
    public const short TooFarAway = 6443;
    public const short Busy = 6444;
    public const short CannotTrade = 6445;
    public const short NotAllowedInCluster = 6446;
    public const short OnlyInParty = 6448;

    /// <summary>A refused op 163-167 (the client reads no code of its own for them).</summary>
    public const short Refused = 1;

    private readonly Dictionary<Player, PlayerTrade> _trades = [];
    private int _nextId;

    private ServerConfig Config => world.Config;

    /// <summary>The trade <paramref name="p"/> is in, invited or open; null when none.</summary>
    public PlayerTrade? Of(Player p) => _trades.GetValueOrDefault(p);

    /// <summary>Whether <paramref name="item"/> lies in an offer.</summary>
    public bool IsOffered(Item item) => _trades.Values.Any(t => t.A.Slots.Values.Any(o => o.Item == item) || t.B.Slots.Values.Any(o => o.Item == item));

    /// <summary>
    /// Op 161 of <paramref name="a"/> on object <paramref name="targetId"/>: rc 0 (<paramref name="trade"/> made, 175 to the target) or the
    /// refusal, in the order of §2.3.
    /// </summary>
    public short Invite(Player a, long targetId, out PlayerTrade? trade)
    {
        trade = null;
        if (!Config.TradeEnabled) return CannotTrade;
        if (_trades.ContainsKey(a)) return YouAlreadyTrading;
        if (a.Map is not { } map || map.Players.GetValueOrDefault(targetId) is not { } b || b == a || !a.Observers.Contains(b)) return CannotTrade;
        if (_trades.ContainsKey(b)) return PlayerAlreadyTrading;
        if (Check(a, b, Config.TradeInviteRange) is var rc and not 0) return rc;
        var now = world.GameNow;
        trade = new PlayerTrade(++_nextId, new TradeSide(a), new TradeSide(b), now + SpellCasts.Seconds(Config.TradeInviteSeconds));
        _trades[a] = trade;
        _trades[b] = trade;
        b.Session.Send(new InvitationPlayerTradeEvent { ck4 = a.Id, ck5 = a.Character.Name, ck6 = "", ck8 = 0, cla = trade.Id });
        Log.Info("trade", $"'{a.Character.Name}' invites '{b.Character.Name}' to trade (trade {trade.Id})");
        return 0;
    }

    /// <summary>Op 163 of <paramref name="b"/>: the invitation <paramref name="tradeId"/> opens (rc 0), or the trade is cancelled (rc 1) when a rule fails now.</summary>
    public short AcceptInvitation(Player b, int tradeId)
    {
        if (Of(b) is not { State: TradeState.Invited } t || t.Id != tradeId || t.B.Player != b)
        {
            // The client opened its window for a trade the server no longer has: its 177 closes it.
            if (Of(b) is null) b.Session.Send(new PlayerTradeCancelEvent { dm6 = tradeId });
            return Refused;
        }
        if (Check(t.A.Player, b, Config.TradeKeepRange) is var rc and not 0)
        {
            Cancel(t, $"the invitation's rules fail now ({rc})");
            return Refused;
        }
        t.State = TradeState.Open;
        Changed(t); // LastChange = now: the invitee's window starts the client's accept timer on this first Update
        Log.Info("trade", $"'{b.Character.Name}' accepts trade {t.Id} with '{t.A.Player.Character.Name}'");
        return 0;
    }

    /// <summary>Op 162 of <paramref name="p"/> and every abort: its trade ends, PlayerTradeCancel to both (rc 0 also with none).</summary>
    public void Cancel(Player p, string why)
    {
        if (Of(p) is { } t) Cancel(t, why);
    }

    /// <summary>Op 164: <paramref name="p"/> offers its bag item <paramref name="itemId"/> (the whole stack) in trade slot <paramref name="slot"/>.</summary>
    public short AddItem(Player p, int tradeId, long itemId, int slot)
    {
        if (Own(p, tradeId) is not { } t) return Refuse(p);
        var side = t.Side(p);
        if (slot < 0 || slot >= Config.TradeSlots || side.Slots.ContainsKey(slot) || p.Inventory.Bag.Find(itemId) is not { } item
            || item.Template.Attrs.GetValueOrDefault("tradable") == "false" || side.Slots.Values.Any(o => o.Item == item))
            return Refuse(p);
        side.Slots[slot] = new Offered(item, item.Count);
        Changed(t);
        return 0;
    }

    /// <summary>Op 165: <paramref name="p"/> takes the item in trade slot <paramref name="slot"/> out of its offer; the removed item's object id.</summary>
    public short RemoveItem(Player p, int tradeId, int slot, out long itemId)
    {
        itemId = 0;
        if (Own(p, tradeId) is not { } t || !t.Side(p).Slots.Remove(slot, out var offered)) return Refuse(p);
        itemId = offered.Item.Id;
        Changed(t);
        return 0;
    }

    /// <summary>Op 167: <paramref name="p"/> offers <paramref name="raw"/> silver (a7m; gold only with Trade:AllowGold, which the window never sends).</summary>
    public short SetSilver(Player p, int tradeId, long raw, bool gold)
    {
        if (Own(p, tradeId) is not { } t || gold && !Config.TradeAllowGold || raw < 0 || raw > (gold ? p.Character.Gold : p.Character.Silver)) return Refuse(p);
        var side = t.Side(p);
        if ((gold ? side.Gold : side.Silver) == raw) return 0;
        if (gold) side.Gold = raw;
        else side.Silver = raw;
        Changed(t);
        return 0;
    }

    /// <summary>
    /// Op 166: <paramref name="p"/> accepts (or withdraws) the offer of <paramref name="revision"/>. An accept of an old revision or within
    /// Trade:AcceptDelaySeconds of the last change is refused (rc 1, and the true state back to it: the client ignores the code); a
    /// withdrawal always counts. Both accepted: <see cref="Commit"/>.
    /// </summary>
    public short Accept(Player p, int tradeId, int revision, bool accept)
    {
        if (Of(p) is not { State: TradeState.Open } t || t.Id != tradeId) return Refused;
        var side = t.Side(p);
        if (accept && (revision != t.Revision || world.GameNow < t.LastChange + SpellCasts.Seconds(Config.TradeAcceptDelaySeconds)))
        {
            p.Session.Send(AcceptChange(t, side));
            return Refused;
        }
        side.Accepted = accept;
        foreach (var s in t.Open) s.Player.Session.Send(AcceptChange(t, s));
        if (t.A.Accepted && t.B.Accepted) Commit(t);
        return 0;
    }

    /// <summary>
    /// The world tick: lapsed invitations and trades whose players are apart (another map, farther than Trade:KeepRange) end; offered items
    /// that left the bag or changed their count leave the offer, and offered silver over the balance is lowered to it (one change each trade).
    /// </summary>
    public void Update()
    {
        if (_trades.Count == 0) return;
        var now = world.GameNow;
        foreach (var t in _trades.Values.Distinct().ToList())
        {
            if (t.State == TradeState.Invited && now >= t.InviteExpires) Cancel(t, "the invitation lapsed");
            else if (Apart(t)) Cancel(t, "the players are apart");
            else if (Revalidate(t.A) | Revalidate(t.B)) Changed(t);
        }
    }

    /// <summary><paramref name="p"/> leaves its map (logout, cluster change, a new Join: WorldSession.Leave): its trade ends.</summary>
    public void Left(Player p) => Cancel(p, "the player left");

    /// <summary><paramref name="p"/> is knocked down (or dies): its trade ends.</summary>
    public void KnockedDown(Player p) => Cancel(p, "the player was knocked down");

    /// <summary>The server stops: every trade ends (nothing has moved).</summary>
    public void CancelAll()
    {
        foreach (var t in _trades.Values.Distinct().ToList()) Cancel(t, "the server stops");
    }

    /// <summary>
    /// Whether the items <paramref name="incoming"/> (each with its saved record, in order) fit <paramref name="bag"/> once
    /// <paramref name="outgoing"/> left it, put the way <see cref="WorldSession.GiveItem"/> puts them: onto the first stack (slot order)
    /// of the same type and journal fame that takes the whole count and is not partly filled, else new stacks in the lowest free
    /// slots, which later items may stack onto. The outgoing stacks' slots are free and give no stack room (§2.4 step 2).
    /// </summary>
    internal static bool FitsAfter(Container bag, IReadOnlyCollection<Item> outgoing, IEnumerable<(ItemTemplate Type, int Count, InventoryItem Record)> incoming)
    {
        // Per slot: occupied without stack room (an unloaded entry), empty (null), or a stack (type, fame, partly filled, count).
        var slots = new (ItemTemplate Type, long Fame, bool Alone, int Count)?[bag.Size];
        var blocked = new bool[bag.Size];
        for (var s = 0; s < bag.Size; s++)
        {
            if (bag[s] is { } i && !outgoing.Contains(i)) slots[s] = (i.Template, i.JournalFame, i.PartlyFilled, i.Count);
            else if (bag.Unloaded[s] is not null) blocked[s] = true;
        }
        foreach (var (type, count, record) in incoming)
        {
            var max = WorldSession.MaxStack(type);
            var fame = type.IsJournal ? record.Fame ?? Item.DefaultFame(type) : 0;
            var alone = type.Journal is { } j && fame > 0 && fame < j.MaxFame;
            var stack = max > 1 && !alone ? Array.FindIndex(slots, x => x is { } v && v.Type == type && v.Fame == fame && !v.Alone && v.Count + count <= max) : -1;
            if (stack >= 0)
            {
                var v = slots[stack]!.Value;
                slots[stack] = v with { Count = v.Count + count };
                continue;
            }
            for (var left = count; left > 0;)
            {
                var free = Enumerable.Range(0, bag.Size).FirstOrDefault(s => slots[s] is null && !blocked[s], -1);
                if (free < 0) return false;
                var n = Math.Min(left, max);
                left -= n;
                slots[free] = (type, fame, alone, n);
            }
        }
        return true;
    }

    /// <summary>
    /// Both accepted (§2.4): under the world lock, with no await and no I/O in between, so nothing interleaves. The live state is checked
    /// again (a failure cancels), then the room of each side after its own offer left (a failure resets the accepts and tells the side
    /// lacking room); then the outgoing items leave their bags (InventoryDeleteItem), the silver is spent, every item comes to the other
    /// side as a new object with its saved record (GiveItem: NewXItem + InventoryPutItem), the silver is credited, both characters are
    /// saved in one transaction, and PlayerTradeFinished goes to both.
    /// </summary>
    private void Commit(PlayerTrade t)
    {
        if (Apart(t) || IsBusy(t.A.Player) || IsBusy(t.B.Player) || Stale(t.A) || Stale(t.B))
        {
            Cancel(t, "the offer is no longer valid at the commit");
            return;
        }
        var lacking = new List<Player>();
        foreach (var side in new[] { t.A, t.B })
        {
            var other = t.Other(side.Player);
            if (!FitsAfter(side.Player.Inventory.Bag, side.Slots.Values.Select(o => o.Item).ToList(), other.Slots.Values.Select(o => (o.Item.Template, o.Count, Record(o.Item)))))
                lacking.Add(side.Player);
        }
        if (lacking.Count > 0)
        {
            t.A.Accepted = t.B.Accepted = false;
            foreach (var s in t.Open) s.Player.Session.Send(AcceptChange(t, s));
            foreach (var p in lacking) p.Session.Send(new SystemMessageEvent { d0p = "Not enough free inventory slots", d0q = 1 });
            Log.Info("trade", $"trade {t.Id}: no room for the partner's offer with {string.Join(", ", lacking.Select(p => $"'{p.Character.Name}'"))}");
            return;
        }
        // 3. The saved records of everything outgoing (quality, durability, spells, journal fame), before anything moves.
        var records = new[] { t.A, t.B }.ToDictionary(s => s, s => s.Slots.Values.Select(o => (o.Item, Record: Record(o.Item))).ToList());
        if (world.Shop.Enabled)
        {
            TradeInventoryPlan? a = null, b = null;
            var committed = world.Shop.TryCommitTrade(t, () =>
            {
                a = PlanInventory(t.A, records[t.A], records[t.B]);
                b = PlanInventory(t.B, records[t.B], records[t.A]);
                return (a.Character, b.Character);
            }, (first, second) =>
            {
                var publishA = InstallInventory(a!);
                var publishB = InstallInventory(b!);
                t.A.Player.Character.Silver = first.Silver; t.A.Player.Character.Gold = first.Gold;
                t.B.Player.Character.Silver = second.Silver; t.B.Player.Character.Gold = second.Gold;
                // Install both complete live states and their future save snapshots before any send or item hook.
                // A disconnected peer may throw while publishing; the durable exchange must remain complete and non-replayable.
                t.A.Player.SaveItems();
                t.B.Player.SaveItems();
                Remove(t);
                publishA();
                publishB();
                // Hooks see both completed bags. Their own subsequent changes follow the normal item save path.
                foreach (var plan in new[] { a!, b! })
                {
                    foreach (var item in plan.Added) world.Scripts.OnItemAdded(plan.Player, item);
                    plan.Player.SaveItems();
                }
            });
            if (!committed)
            {
                t.A.Accepted = t.B.Accepted = false;
                foreach (var side in t.Open)
                {
                    side.Player.Session.Send(AcceptChange(t, side));
                    side.Player.Session.Send(new SystemMessageEvent { d0p = "No se pudo guardar el intercambio. No se ha movido nada; vuelve a aceptar.", d0q = 1 });
                }
                return;
            }
            Finish(t, records);
            return;
        }
        // 4. Take.
        foreach (var side in new[] { t.A, t.B })
        {
            var bag = side.Player.Inventory.Bag;
            foreach (var (item, _) in records[side])
            {
                bag.Take(bag.IndexOf(item));
                side.Player.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
            }
            Wallet.TrySpend(side.Player, side.Silver, "trade"); // checked above under the same lock: cannot fail
        }
        foreach (var side in new[] { t.A, t.B }) Wallet.TransferGold(side.Player, t.Other(side.Player).Player, side.Gold, "trade");
        // 5. Give.
        foreach (var side in new[] { t.A, t.B })
        {
            var to = t.Other(side.Player).Player;
            foreach (var (item, record) in records[side]) to.Session.GiveItem(to, item.Template, item.Count, item.Quality, saved: record);
            Wallet.Credit(to, side.Silver, "trade");
        }
        // 6. Both saved together: a crash keeps both characters as after the trade, or both as before it.
        t.A.Player.SaveItems();
        t.B.Player.SaveItems();
        world.Saves.SaveTogether(t.A.Player.Character, t.B.Player.Character);
        Finish(t, records);
    }

    private void Finish(PlayerTrade t, Dictionary<TradeSide, List<(Item Item, InventoryItem Record)>> records)
    {
        Remove(t);
        foreach (var side in new[] { t.A, t.B }) side.Player.Session.Send(new PlayerTradeFinishedEvent { dm7 = t.Id });
        Log.Info("trade", $"trade {t.Id} done: {Describe(t.A, records[t.A])} -> '{t.B.Player.Character.Name}'; {Describe(t.B, records[t.B])} -> '{t.A.Player.Character.Name}'");
    }

    private sealed record TradeInventoryPlan(Player Player, Character Character, Inventory Inventory, Item[] Removed, List<Item> Added);

    /// <summary>Stages the exact objects GiveItem would create, without changing the live bags or sending events.</summary>
    private TradeInventoryPlan PlanInventory(TradeSide side, List<(Item Item, InventoryItem Record)> outgoing,
        List<(Item Item, InventoryItem Record)> incoming)
    {
        var player = side.Player;
        var original = player.Inventory;
        var staged = new Inventory(original.Equipment.Id, original.Bag.Id);
        static Item Copy(Item i) => new(i.Id, i.Template, i.Count, i.Quality, i.DurabilityRaw, i.Active, i.Passive, i.Source)
            { JournalFame = i.JournalFame, Soul = i.Soul };
        foreach (var (from, to) in new[] { (original.Equipment, staged.Equipment), (original.Bag, staged.Bag) })
            for (var slot = 0; slot < from.Size; slot++)
            {
                to.Unloaded[slot] = from.Unloaded[slot];
                if (from[slot] is { } item) to.Put(slot, Copy(item));
            }
        staged.Overflow.AddRange(original.Overflow);
        foreach (var (item, _) in outgoing) staged.Bag.Take(original.Bag.IndexOf(item));
        var added = new List<Item>();
        foreach (var (item, record) in incoming)
        {
            var max = WorldSession.MaxStack(item.Template);
            var count = record.Count;
            if (max > 1 && !item.PartlyFilled && staged.Bag.Items.FirstOrDefault(i => i.Template == item.Template
                && i.JournalFame == item.JournalFame && !i.PartlyFilled && i.Count + count <= max) is { } stack)
            {
                stack.Count += count;
                continue;
            }
            for (var left = count; left > 0;)
            {
                var slot = staged.Bag.FreeSlot();
                if (slot < 0) throw new InvalidOperationException("trade inventory no longer fits");
                var n = Math.Min(left, max);
                left -= n;
                var made = player.Session.PrepareTradeItem(item.Template, record with { Count = n }, player);
                staged.Bag.Put(slot, made);
                added.Add(made);
            }
        }
        var snapshot = player.Character.Clone();
        staged.SaveTo(snapshot, id => world.Spells?.Name(id));
        foreach (var vault in player.OpenVaults.Values) snapshot.Vaults[vault.Key] = Inventory.Records(vault.Container, id => world.Spells?.Name(id));
        return new(player, snapshot, staged, outgoing.Select(o => o.Item).ToArray(), added);
    }

    /// <summary>Installs without callbacks; the returned action only publishes the already installed changes.</summary>
    private static Action InstallInventory(TradeInventoryPlan plan)
    {
        var player = plan.Player;
        var bag = player.Inventory.Bag;
        var changed = new List<Item>();
        var added = new List<(Item Item, int Slot)>();
        foreach (var item in plan.Removed) bag.Take(bag.IndexOf(item));
        for (var slot = 0; slot < bag.Size; slot++)
        {
            if (plan.Inventory.Bag[slot] is not { } planned) continue;
            if (bag[slot] is { } current && current.Id == planned.Id)
            {
                if (current.Count == planned.Count) continue;
                current.Count = planned.Count;
                changed.Add(current);
            }
            else
            {
                bag.Put(slot, planned);
                added.Add((planned, slot));
            }
        }
        return () =>
        {
            foreach (var item in plan.Removed) player.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
            foreach (var item in changed) player.Session.SendItem(item);
            foreach (var (item, slot) in added)
            {
                player.Session.SendItem(item);
                player.Session.Send(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = bag.Id });
            }
        };
    }

    /// <summary>Steps 6-9 of the invite rules (§2.3) between <paramref name="a"/> and <paramref name="b"/>, the distance against <paramref name="range"/>.</summary>
    private short Check(Player a, Player b, double range)
    {
        if (IsBusy(a) || IsBusy(b)) return Busy;
        if (Pvp.Of(a, b) == Relation.Hostile) return CannotTrade;
        if (Config.TradeClusterRules)
        {
            var rule = Pvp.RulesOf(world, a.Map).AllowTrade;
            if (rule == PlayerTradeRule.Disabled) return NotAllowedInCluster;
            if (rule == PlayerTradeRule.GroupOnly && !Pvp.SameParty(a, b)) return OnlyInParty;
        }
        return Distance(a, b) > range ? TooFarAway : (short)0;
    }

    /// <summary>Knocked down or dead, at a station, casting or channelling, or (without Trade:AllowInCombat) in combat [choice].</summary>
    private bool IsBusy(Player p) =>
        p.Combat.Down is not null || p.Action is not null || p.Combat.Cast is not null || p.Combat.Channel is not null || !Config.TradeAllowInCombat && p.Combat.InCombat;

    /// <summary>Not both on one map, or farther apart than Trade:KeepRange.</summary>
    private bool Apart(PlayerTrade t) =>
        t.A.Player.Map is not { } map || t.B.Player.Map != map || Distance(t.A.Player, t.B.Player) > Config.TradeKeepRange;

    /// <summary>Whether an offered item left the bag or changed its count, or the silver or gold is over the balance.</summary>
    private static bool Stale(TradeSide side) =>
        side.Silver > side.Player.Character.Silver || side.Gold > side.Player.Character.Gold || side.Slots.Values.Any(o => !side.Player.Inventory.Bag.Contains(o.Item) || o.Item.Count != o.Count);

    /// <summary>Takes what is stale out of the offer (the tick); whether anything changed.</summary>
    private static bool Revalidate(TradeSide side)
    {
        var changed = false;
        foreach (var (slot, o) in side.Slots.ToList())
            if (!side.Player.Inventory.Bag.Contains(o.Item) || o.Item.Count != o.Count)
            {
                side.Slots.Remove(slot);
                changed = true;
            }
        if (side.Silver > side.Player.Character.Silver)
        {
            side.Silver = side.Player.Character.Silver;
            changed = true;
        }
        if (side.Gold > side.Player.Character.Gold)
        {
            side.Gold = side.Player.Character.Gold;
            changed = true;
        }
        return changed;
    }

    /// <summary>The trade of <paramref name="p"/> with id <paramref name="tradeId"/> it may change: open, or invited and p the inviter.</summary>
    private PlayerTrade? Own(Player p, int tradeId) =>
        Of(p) is { } t && t.Id == tradeId && (t.State == TradeState.Open || t.A.Player == p) ? t : null;

    /// <summary>
    /// A refused op 164/165/167: the client changed its own window before asking and undoes nothing, so a resync Update (revision + 1,
    /// the offer and accepts unchanged) goes to every open window of the sender's trade; rc 1.
    /// </summary>
    private short Refuse(Player p)
    {
        if (Of(p) is { } t && t.Open.Any(s => s.Player == p))
        {
            t.Revision++;
            foreach (var s in t.Open) s.Player.Session.Send(UpdateFor(t, s));
        }
        return Refused;
    }

    /// <summary>One change of the offer: revision + 1, LastChange = now, both accepts off; Update and AcceptChange (false, false) to every open window.</summary>
    private void Changed(PlayerTrade t)
    {
        t.Revision++;
        t.LastChange = world.GameNow;
        t.A.Accepted = t.B.Accepted = false;
        foreach (var s in t.Open)
        {
            s.Player.Session.Send(UpdateFor(t, s));
            s.Player.Session.Send(AcceptChange(t, s));
        }
    }

    private void Cancel(PlayerTrade t, string why)
    {
        if (!Remove(t)) return;
        var cancel = new PlayerTradeCancelEvent { dm6 = t.Id };
        t.A.Player.Session.Send(cancel);
        t.B.Player.Session.Send(cancel);
        Log.Info("trade", $"trade {t.Id} of '{t.A.Player.Character.Name}' and '{t.B.Player.Character.Name}' cancelled: {why}");
    }

    private bool Remove(PlayerTrade t)
    {
        if (Of(t.A.Player) != t) return false;
        _trades.Remove(t.A.Player);
        _trades.Remove(t.B.Player);
        return true;
    }

    /// <summary>AcceptChange {0 trade, 1 own accepted, 2 partner accepted} from <paramref name="own"/>'s view.</summary>
    private static PlayerTradeAcceptChangeEvent AcceptChange(PlayerTrade t, TradeSide own) =>
        new() { dm3 = t.Id, dm4 = own.Accepted, dm5 = t.Other(own.Player).Accepted };

    /// <summary>
    /// PlayerTradeUpdate (178, ctt.q(axm), §1.4) for <paramref name="own"/>'s client: 0 trade, 1 revision, 2-5 own and partner silver and
    /// gold, 6-16 its own offer (6 the object ids, by which the client finds its items), 17-26 the partner's (no ids). Every array is
    /// there (a null one throws), each entry has an int[] of spells (empty but for equipment, -1 left out), slots are distinct.
    /// </summary>
    private PlayerTradeUpdateEvent UpdateFor(PlayerTrade t, TradeSide own)
    {
        var mine = own.Slots.ToList();
        var theirs = t.Other(own.Player).Slots.ToList();
        static int[] Spells(Item i, short[] ids) => i.Template.IsEquipment ? [.. ids.Where(x => x >= 0).Select(x => (int)x)] : [];
        return new PlayerTradeUpdateEvent
        {
            dm8 = t.Id, dm9 = t.Revision, dna = own.Silver, dnb = own.Gold, dnc = t.Other(own.Player).Silver, dnd = t.Other(own.Player).Gold,
            dne = [.. mine.Select(o => o.Value.Item.Id)],
            dnf = [.. mine.Select(o => o.Key)],
            dng = [.. mine.Select(o => o.Value.Item.Template.Index)],
            dnh = [.. mine.Select(_ => "")],
            dni = [.. mine.Select(o => (byte)o.Value.Item.Quality)],
            dnj = [.. mine.Select(o => o.Value.Item.DurabilityRaw)],
            dnk = [.. mine.Select(o => o.Value.Item.JournalFame)],
            dnl = [.. mine.Select(o => Spells(o.Value.Item, o.Value.Item.Active))],
            dnm = [.. mine.Select(o => Spells(o.Value.Item, o.Value.Item.Passive))],
            dnn = [.. mine.Select(o => (short)o.Value.Count)],
            dno = [.. mine.Select(_ => "")],
            dnp = [.. theirs.Select(o => o.Key)],
            dnq = [.. theirs.Select(o => o.Value.Item.Template.Index)],
            dnr = [.. theirs.Select(_ => "")],
            dns = [.. theirs.Select(o => (byte)o.Value.Item.Quality)],
            dnt = [.. theirs.Select(o => o.Value.Item.DurabilityRaw)],
            dnu = [.. theirs.Select(o => o.Value.Item.JournalFame)],
            dnv = [.. theirs.Select(o => Spells(o.Value.Item, o.Value.Item.Active))],
            dnw = [.. theirs.Select(o => Spells(o.Value.Item, o.Value.Item.Passive))],
            dnx = [.. theirs.Select(o => (short)o.Value.Count)],
            dny = [.. theirs.Select(_ => "")],
        };
    }

    private InventoryItem Record(Item item) => item.ToRecord(id => world.Spells?.Name(id));

    private static string Describe(TradeSide side, List<(Item Item, InventoryItem Record)> items)
    {
        var parts = items.Select(x => $"{x.Item.Template.Name} x{x.Item.Count}").ToList();
        if (side.Silver > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{side.Silver / 10000.0:0.####} silver"));
        if (side.Gold > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{side.Gold / 10000.0:0.####} gold"));
        return $"'{side.Player.Character.Name}' gives {(parts.Count == 0 ? "nothing" : string.Join(", ", parts))}";
    }

    private static float Distance(Player a, Player b)
    {
        var utc = DateTime.UtcNow;
        return Vector2.Distance(a.PositionAt(utc), b.PositionAt(utc));
    }
}
