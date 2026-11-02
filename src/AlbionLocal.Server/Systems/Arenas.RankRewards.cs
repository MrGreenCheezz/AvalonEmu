using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Arenas
{
    /// <summary>One complete rank at a time: prepare the normal bag placement, or mail the whole rank,
    /// and commit it with Peak. A failed component pays none of this rank; prior completed ranks stay claimed.
    /// The existing SQLite repository supplies the joint transaction. Other stores leave the reward pending.</summary>
    private bool TryGiveRankReward(Player p, ArenaMatch m, int rankIndex, IReadOnlyList<(string Name, int Count)> items,
        out List<string> given)
    {
        given = [];
        if (world.Characters is not IRankRewardStore || p.Character.Transient
            || items.Any(i => i.Count <= 0)) return false;
        var types = items.Select(i => world.Items.Find(i.Name)).ToArray();
        if (types.Any(t => t is not { CanCreate: true })) return false;
        var contents = items.Select((i, index) => (Type: types[index]!, i.Count)).ToArray();
        Container? delivery = null;
        var added = new List<Item>();
        long mailId = 0;
        try
        {
            var saved = world.Saves.Commit(() =>
            {
                if (world.Ratings.ArenaRank(p.Character.Id, m.Mode.Category) is not { } expected
                    || rankIndex != expected.Peak + 1 || rankIndex > expected.EarnedPeak) return false;
                p.StorePosition(DateTime.UtcNow);
                Consumables.Store(p);
                var snapshot = p.Character.Clone();
                p.Inventory.SaveTo(snapshot, id => world.Spells?.Name(id));
                foreach (var vault in p.OpenVaults.Values)
                    snapshot.Vaults[vault.Key] = Inventory.Records(vault.Container, id => world.Spells?.Name(id));
                var bag = RankRewardBag(p);
                Mail? mail = null;
                InventoryItem[] attachments = [];
                if (WorldSession.Fits(bag, contents))
                {
                    foreach (var (type, count) in contents)
                        WorldSession.PutItems(bag, type, count, saved => p.Session.PrepareTradeItem(type, saved, p),
                            added: (item, _) => added.Add(item));
                    // Preserve unknown/displaced records and all saved overflow, exactly as the shop's detached delivery.
                    var overflow = snapshot.Inventory.Skip(Inventory.BagSlots).ToArray();
                    var savedBag = Inventory.Records(bag, id => world.Spells?.Name(id));
                    if (overflow.Length > 0)
                    {
                        while (savedBag.Count < Inventory.BagSlots) savedBag.Add(null);
                        savedBag.AddRange(overflow);
                    }
                    snapshot.Inventory = savedBag;
                }
                else
                {
                    // A separate mail store cannot atomically consume this repository's entitlement.
                    if (!world.Config.MailRewardOverflow || !Mailer.On(world)
                        || !ReferenceEquals(world.Mail, world.Characters)) return false;
                    attachments = [.. contents.Select(i => new InventoryItem(i.Type.Name, i.Count))];
                    var now = DateTime.UtcNow;
                    var list = string.Join(", ", items.Select(i => i.Count == 1 ? i.Name : $"{i.Name} x{i.Count}"));
                    mail = new Mail(0, p.Character.Id, false, MailKind.Normal, MailCategory.System, "Albion Local", Guid.Empty,
                        false, "Arena rank-up rewards", $"These rewards of arena rank {rankIndex} did not fit your inventory: {list}. Take them out of this mail.",
                        null, now, world.Config.MailKeepDays > 0 ? now.AddDays(world.Config.MailKeepDays) : null, false, 0);
                }
                if (!world.Ratings.CommitRankReward(expected, rankIndex, snapshot, mail, attachments, out mailId)) return false;
                delivery = mail is null ? bag : null;
                return true;
            });
            if (!saved) return false;
        }
        catch (Exception e)
        {
            Log.Warn("arena", $"rank {rankIndex} rewards for '{p.Character.Name}' remain pending: {e.Message}");
            return false;
        }

        // The durable transaction has succeeded. Reconnection reads its bag/mail and Peak, so a presentation
        // failure must not make the caller retry a paid component. Install all items before sending or running hooks.
        if (delivery is not null) PublishRankReward(p, delivery, added);
        else if (mailId > 0)
        {
            try
            {
                world.Mail!.TrimMailbox(p.Character.Id, false, world.Config.MailMaxPerBox);
                Mailer.Notify(world, p.Character.Id, false);
            }
            catch (Exception e) { Log.Warn("arena", $"rank reward mail {mailId} committed, notification deferred: {e.Message}"); }
        }
        RewardReceipts(m, p.Character.Id, rankUp: true).AddRange(contents.Select(i => new ArenaRewardReceipt(6, i.Count, i.Type.Index)));
        given = [.. items.Select(i => i.Count == 1 ? i.Name : $"{i.Name} x{i.Count}")];
        return true;
    }

    private static Container RankRewardBag(Player p)
    {
        var source = p.Inventory.Bag;
        var bag = new Container(source.Id, source.Size);
        for (var slot = 0; slot < bag.Size; slot++)
        {
            bag.Unloaded[slot] = source[slot] is null ? source.Unloaded[slot] : null;
            if (source[slot] is { } item)
                bag.Put(slot, new Item(item.Id, item.Template, item.Count, item.Quality, item.DurabilityRaw,
                    item.Active, item.Passive, item.Source) { JournalFame = item.JournalFame, Soul = item.Soul });
        }
        return bag;
    }

    private void PublishRankReward(Player p, Container delivery, List<Item> added)
    {
        var changed = new List<Item>();
        var placed = new List<(Item Item, int Slot)>();
        var bag = p.Inventory.Bag;
        for (var slot = 0; slot < bag.Size; slot++)
        {
            if (delivery[slot] is not { } planned) continue;
            if (bag[slot] is { } current && current.Id == planned.Id)
            {
                if (current.Count == planned.Count) continue;
                current.Count = planned.Count;
                changed.Add(current);
            }
            else
            {
                bag.Put(slot, planned);
                placed.Add((planned, slot));
            }
        }
        p.SaveItems();
        try
        {
            foreach (var item in changed) p.Session.SendItem(item);
            foreach (var (item, slot) in placed)
            {
                p.Session.SendItem(item);
                p.Session.Send(new InventoryPutItemEvent { ckw = item.Id, ckx = slot, cky = bag.Id });
            }
            foreach (var item in added) world.Scripts.OnItemAdded(p, item);
        }
        catch (Exception e) { Log.Warn("arena", $"rank reward committed for '{p.Character.Name}', presentation interrupted: {e.Message}"); }
        finally { p.SaveItems(); }
    }
}
