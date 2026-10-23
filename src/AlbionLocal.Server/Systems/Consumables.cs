using System.Globalization;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Potions, food and the items used from the inventory (analysis/work/stageD/consumables/SPEC.md). Two paths:
/// <list type="bullet">
/// <item><b>The potion and food slots</b> (equipment 8/9, CharacterSpellSlot 12/13): a CastStart of the item's consumespell,
/// checked and timed as any cast (<see cref="SpellCasts.Check"/>, <see cref="SpellCasts.Finish"/>); at the cast end one item of
/// the stack goes (<see cref="UseSlotItem"/>), the slot's cooldown starts, and the hit runs in the consumable order
/// (<see cref="Hit"/>). A cancelled or interrupted food cast costs nothing.</item>
/// <item><b>UseConsumableFromInventory</b> (op 374): n uses of a consumablefrominventoryitem, one every
/// <see cref="ServerConfig.BatchInterval"/> (<see cref="StartBatch"/>), with BatchUseItemStart/End around them when n &gt; 1 and
/// the response after the last one.</item>
/// </list>
/// The player's own timed buffs and the slot cooldowns are saved with the character (<see cref="Store"/>, <see cref="Restore"/>)
/// and sent at Join (<see cref="JoinEvents"/>). Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Consumables
{
    /// <summary>CharacterSpellSlot of the potion (equipment slot 8) and of the food (equipment slot 9).</summary>
    public const byte PotionSlot = 12, FoodSlot = 13;

    /// <summary>The ReturnCode of a refused UseConsumableFromInventory (enum ll 14960, the client shows @CONSUMABLE_ITEM_ERROR_GENERIC_FAILED; SPEC §3).</summary>
    public const short GenericFailed = 14960;

    /// <summary>
    /// The effect tags only the items used from the inventory have (SPEC §4.3 first wave; <see cref="Award"/>), and stageG's dungeon maps
    /// (activaterandomdungeon, <see cref="RandomDungeons.FromMap"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> ItemTags = new HashSet<string>(StringComparer.Ordinal)
    {
        "adddestinyboardpvefame", "awardcraftingfocus", "awardsilver", "generateloot", "activaterandomdungeon", "awardlearningpoints",
        // StageN spells-bosses: the gathering tomes, the fish food's fishing fame and the repair kits.
        "adddestinyboardgatheringfame", "adddestinyboardfishingfame", "repairequipment",
    };

    /// <summary>The ReturnCode of a dungeon map with no place for its exit (enum ll 14965, @RANDOMDUNGEONMAP_NO_DUNGEON_FOUND; SPEC §3).</summary>
    public const short NoDungeonFound = 14965;

    /// <summary>The sub-effects saved over a relog (<see cref="Store"/>): the buffs and markers; the over-time ticks, auras and shields are not.</summary>
    private static readonly IReadOnlySet<string> Saved = new HashSet<string>(StringComparer.Ordinal) { "buffovertime", "dummy" };

    public static bool IsSlot(byte slot) => slot is PotionSlot or FoodSlot;

    private static int EquipmentOf(byte slot) => slot == PotionSlot ? 8 : 9;

    // ---- the potion and food slots (SPEC §2) ----

    /// <summary>Whether the cast's stack still lies in its slot (it may have been moved during a food's 0.8 s).</summary>
    public static bool InSlot(Player p, PendingCast cast) => cast.Source is { } item && p.Inventory.Equipment[EquipmentOf(cast.Slot)] == item;

    /// <summary>
    /// The item a finished slot cast takes (SPEC §2.1 step 2), after its EnergyUpdate: to the caster's own client NewSimpleItem
    /// {0 stack, 1 type, 2 new count} (no key 3) while items are left; the last one InventoryDeleteItem {0 stack} (no key 1), the
    /// slot emptied, then CharacterEquipmentChanged with [8]/[9] = 0 and spell slot 12/13 = -1 to it and its observers (no
    /// RegenerationPlayerComboChanged). Saved with the items.
    /// </summary>
    public static void UseSlotItem(Player p, PendingCast cast)
    {
        var item = cast.Source!;
        item.Count--;
        if (item.Count > 0) p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count });
        else
        {
            p.Inventory.Equipment.Take(EquipmentOf(cast.Slot));
            p.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
            p.Session.SendConsumedEquipment(p);
        }
        p.SaveItems();
        Log.Debug("items", $"'{p.Character.Name}' used {item.Template.Name} from slot {cast.Slot}: {item.Count} left");
        p.World.Plugins.ItemUsed(p, item.Template.Name, 1, "slot"); // the plugins' ItemUsed
    }

    /// <summary>
    /// The hit of a slot cast (SPEC §2.1 step 5): nothing for a spell with only dummy/nop effects (the fireworks); otherwise its
    /// effects (<see cref="SpellCasts.ExecuteFirst"/>: the effect events, then the caster's list), then CastHit {0 me, 1 me, 2
    /// spell, 3 1, 4 counter} to it and its observers. The buffs and slot cooldowns are saved with the character.
    /// </summary>
    public static void Hit(SpellCasts.SpellContext c, SpellTemplate spell)
    {
        var p = c.Caster;
        if (spell.Effects.All(e => e.Name.LocalName is "dummy" or "nop")) return;
        SpellCasts.ExecuteFirst(c, spell);
        p.SendAround(Combat.CastHit(p.Id, p.Id, spell.Id, c.Counter));
        Store(p);
        p.World.Saves.MarkChanged(p.Character);
    }

    /// <summary>
    /// <see cref="ServerConfig.EquipCooldownSeconds"/> (0 = off, SPEC §2.2 Q3): an item put into the potion or food slot starts that
    /// slot's cooldown for so long, sent to the own client as SpellCooldownUpdate {0 [slot], 1 [end], 2 [start], 3 [span], 4 [0]}
    /// (whether the official server sends it is Q3). Called by InventoryMoveItem for equipment slots 8/9.
    /// </summary>
    public static void Equipped(Player p, int equipmentSlot)
    {
        var seconds = p.World.Config.EquipCooldownSeconds;
        if (seconds <= 0 || equipmentSlot is not (8 or 9)) return;
        var now = p.World.GameNow;
        var span = SpellCasts.Seconds(seconds);
        var slot = equipmentSlot == 8 ? PotionSlot : FoodSlot;
        if (p.Combat.SlotCooldowns.TryGetValue(slot, out var running) && running.End >= now + span) return;
        p.Combat.SlotCooldowns[slot] = new SlotCooldown(now, now + span, span);
        // The client is told as at Join (SpellCooldownUpdate, the slot's sweep); otherwise it shows the slot ready.
        p.Session.Send(new SpellCooldownUpdateEvent { dy7 = [slot], dy8 = [now + span], dy9 = [now], dza = [span], dzb = [0] });
    }

    // ---- what the inventory items do (SPEC §4.3) ----

    /// <summary>
    /// One of <see cref="ItemTags"/> for the caster: adddestinyboardpvefame tier amount (a tome: <see cref="Destiny.Tome"/>, amount
    /// fame), awardcraftingfocus amount (<see cref="Focus.Add"/>: CraftingFocusUpdate {0 me, 1 now, 2 +amount unclamped, 3 the new focus
    /// clamped to the cap}), awardsilver amount × Rates:Silver (<see cref="Wallet.Credit"/>, UpdateMoney), awardlearningpoints amount
    /// (<see cref="LearningPoints.Grant"/>, UpdateLearningPoints with key 6 = 1; stageJ focus-lp SPEC §5.2).
    /// </summary>
    public static void Award(SpellCasts.SpellContext c, XElement node)
    {
        var p = c.Caster;
        var amount = SpellTemplate.Float(node, "amount");
        switch (node.Name.LocalName)
        {
            case "adddestinyboardpvefame":
                Destiny.Tome(p, (long)Math.Round(amount * 10000.0), c.Item, (int)SpellTemplate.Float(node, "tier"));
                break;
            case "awardcraftingfocus":
                Focus.Add(p, amount, c.Ticks);
                break;
            case "awardlearningpoints":
                LearningPoints.Grant(p, (long)Math.Round(amount * 10000.0));
                break;
            case "awardsilver":
                Wallet.Credit(p, (long)Math.Round(amount * p.World.Rates.Silver * 10000.0), c.Item?.Name ?? c.Root.Name); // × Rates:Silver
                break;
            case "generateloot":
                GenerateLoot(c, (string?)node.Attribute("lootlist"));
                break;
            case "adddestinyboardgatheringfame": // a gathering tome: amount fame to its achievementid node (GATHER_ORE_T5)
            {
                var id = (string?)node.Attribute("achievementid");
                if (p.World.GameData?.Board?.Find(id ?? "") is { } target)
                    Destiny.TomeTo(p, (long)Math.Round(amount * 10000.0), c.Item, n => n.Id == target.Id);
                else Combat.LogOnce("tome:" + id, $"{c.Root.Name}: destiny board node {id} not found: no fame");
                break;
            }
            case "adddestinyboardfishingfame": // the fish food: amount fame to the fishing nodes in progress
                Destiny.TomeTo(p, (long)Math.Round(amount * 10000.0), c.Item, n => n.Mission == "fishingfame");
                break;
            case "repairequipment": // a repair kit: every worn item at full durability, re-sent
                Repair(p);
                break;
        }
    }

    /// <summary>
    /// repairequipment (stageN spells-bosses, UNIQUE_REPAIRPOWDER_ADC_GENERAL_01): the worn items with durability back at full, each
    /// re-sent as NewEquipmentItem (key 8 the durability, as a station's repair), the bars worked out again and the items saved.
    /// </summary>
    public static int Repair(Player p)
    {
        var repaired = 0;
        for (var slot = 0; slot < Inventory.EquipmentSlots; slot++)
            if (p.Inventory.Equipment[slot] is { } item && item.Template.MaxDurability > 0 && item.DurabilityRaw < item.Template.FullDurabilityRaw)
            {
                item.DurabilityRaw = item.Template.FullDurabilityRaw;
                p.Session.Send(Network.WorldSession.ItemObject(item));
                repaired++;
            }
        if (repaired == 0) return 0;
        if (p.RecalculateStats(p.World.GameNow)) p.SendAround(Combat.PlayerCombo(p, p.World.GameNow));
        p.SaveItems();
        Log.Info("items", $"'{p.Character.Name}' repaired {repaired} worn item(s) with a repair kit");
        return repaired;
    }

    /// <summary>
    /// generateloot lootlist (stageD effects batch 3, 520 uses: the loot chests, bags and reward boxes used from the inventory): one roll
    /// of the loot.xml list (<see cref="Roll"/>; <see cref="SpellCasts.SpellContext.Loot"/> when <see cref="Use"/> rolled it already)
    /// goes into the caster's inventory as any item gained (an existing stack that takes it, else new objects in the lowest free slots:
    /// NewSimpleItem / NewEquipmentItem + InventoryPutItem; equipment one per piece). A use whose roll does not fit is refused before
    /// (the official server mails it); what still does not fit here comes by mail (Mail:RewardOverflow), else is logged and lost. Saved with the use's decrement
    /// (<see cref="End"/>), so no snapshot holds the loot beside the unused chest. Returns the items given.
    /// </summary>
    public static List<(ItemTemplate Item, int Count)> GenerateLoot(SpellCasts.SpellContext c, string? list)
    {
        var p = c.Caster;
        var given = new List<(ItemTemplate, int)>();
        foreach (var (type, count) in c.Loot ?? Roll(p.World, list, c.Map.Random, c.Root.Name))
        {
            if (!Network.WorldSession.Fits(p.Inventory.Bag, [(type, count)]))
            {
                // stageJ mail SPEC §3: with Mail:RewardOverflow it comes by mail; else (the edge case) it is lost.
                if (!Mailer.Overflow(p.World, p.Character, [new InventoryItem(type.Name, count)], $"opening {c.Item?.Name ?? list}"))
                    Log.Warn("items", $"'{p.Character.Name}': {count} x {type.Name} from {list} do not fit the inventory: lost");
                continue;
            }
            p.Session.GiveItem(p, type, count);
            given.Add((type, count));
        }
        Log.Info("items", $"'{p.Character.Name}' opened {list}: {(given.Count == 0 ? "nothing" : string.Join(", ", given.Select(g => $"{g.Item2} x {g.Item1.Name}")))}");
        return given;
    }

    /// <summary>
    /// One roll of loot.xml list <paramref name="list"/> (<see cref="LootTables.RollList"/>), as the items the server can create (the
    /// others logged and left out). Empty when loot.xml is not read yet (<see cref="Data.ClusterStore.LootIfLoaded"/>: never read under
    /// the world lock) or has no such list.
    /// </summary>
    public static List<(ItemTemplate Item, int Count)> Roll(World world, string? list, Random random, string spell)
    {
        var rolled = new List<(ItemTemplate, int)>();
        if (list is null || world.Clusters?.LootIfLoaded is not { } tables || !tables.Has(list))
        {
            Combat.LogOnce("lootlist:" + list, $"loot list {list} of {spell} is not in loot.xml (or loot.xml is not read yet): nothing generated");
            return rolled;
        }
        foreach (var (name, amount) in tables.RollList(list, random))
        {
            if (world.Items.Find(name) is not { CanCreate: true } type)
            {
                Combat.LogOnce("item:" + name, $"loot item {name} is not in items.xml or has no New*Item event yet: not generated");
                continue;
            }
            rolled.Add((type, Math.Max(1, amount)));
        }
        return rolled;
    }

    /// <summary>
    /// The buffovertime types the server applies (Player.ComputeStats, the cast and cooldown factors, the damage, heal and cc
    /// sums of <see cref="SpellEffects"/>); an inventory item whose buff has another type (fame, fishing, destiny progression, …)
    /// is refused (<see cref="Supported"/>).
    /// </summary>
    private static readonly IReadOnlySet<string> Applied = new HashSet<string>(StringComparer.Ordinal)
    {
        "hitpointsmax", "hitpointsmaxbonus", "hitpointsregeneration", "hitpointsregenerationbonus",
        "energymax", "energymaxbonus", "energyregeneration", "energyregenerationbonus",
        "magiccooldownreduction", "attackspeedbonus", "movespeedbonus", "healmodifier", "magicresistance", "physicalarmor",
        "crowdcontrolresistance", "magicspelldamagebonus", "magicattackdamagebonus", "physicalspelldamagebonus", "physicalattackdamagebonus",
        "fishingspeed", // the fishing bait (econ SPEC §2.2): Fishing.BiteDelay reads it
    };

    /// <summary>
    /// Whether every effect of <paramref name="spell"/> does something on this server (SPEC §3.1 step 5, §4.3): the first-wave
    /// <see cref="ItemTags"/>, the nop/dummy markers, removeactiveeffects, health/energy changes, a buffovertime of an
    /// <see cref="Applied"/> type, and an applyspell whose named spell passes the same check. An item with another tag
    /// (unlockavatar, a fame or fishing buff, …) is refused, so it is never eaten without
    /// effect; the tag is logged once.
    /// </summary>
    public static bool Supported(SpellTemplate spell, SpellData? spells, int depth = 0)
    {
        foreach (var e in spell.Effects)
        {
            var tag = e.Name.LocalName;
            var why = tag switch
            {
                _ when ItemTags.Contains(tag) => null,
                "nop" or "dummy" or "removeactiveeffects" => null,
                "directattributechange" or "attributechangeovertime" => (string?)e.Attribute("attribute") is "health" or "energy" ? null : $"{tag} {(string?)e.Attribute("attribute")}",
                "buffovertime" => Applied.Contains((string?)e.Attribute("type") ?? "") ? null : $"buffovertime {(string?)e.Attribute("type")}",
                "applyspell" => depth < 8 && spells?.Template((string?)e.Attribute("spell")) is { } sub && Supported(sub, spells, depth + 1) ? null : $"applyspell {(string?)e.Attribute("spell")}",
                _ => tag,
            };
            if (why is null) continue;
            Combat.LogOnce("use:" + why, $"items whose spell has <{why}> cannot be used from the inventory yet ({spell.Name}): refused");
            return false;
        }
        return true;
    }

    // ---- UseConsumableFromInventory (SPEC §3) ----

    /// <summary>
    /// A batch use under way: the stack, its spell and ability power, how many uses (n) and how many are done, the first use's
    /// GameTimeStamp (t0), and the request to answer after the last one.
    /// </summary>
    public sealed class Batch(Item item, SpellTemplate spell, double ap, double ccAp, int count, long start, Map map, int? requestId, bool encrypted)
    {
        public Item Item { get; } = item;
        public SpellTemplate Spell { get; } = spell;
        public double Ap { get; } = ap;
        public double CcAp { get; } = ccAp;
        public int Count { get; } = count;
        public long Start { get; } = start;
        public Map Map { get; } = map;
        public int? RequestId { get; } = requestId;
        public bool Encrypted { get; } = encrypted;

        /// <summary>Uses done so far.</summary>
        public int Done { get; internal set; }

        /// <summary>Ended (all used, cancelled, aborted): its remaining timers do nothing.</summary>
        public bool Over { get; internal set; }

        /// <summary>Stage I: the invasion ticket it uses (<see cref="UseTicket"/>); null for every other item.</summary>
        public Content.InvasionItem? Ticket { get; init; }
    }

    /// <summary>
    /// Starts <paramref name="count"/> uses of <paramref name="item"/> (the handler checked SPEC §3.1): NewSimpleItem {0, 1, 2 count,
    /// 3 true} to the own client; when n &gt; 1 BatchUseItemStart {0 me, 1 t0, 2 t0 + (n − 1) × interval, 3 type} to it and its
    /// observers; the uses at t0 + k × interval (<see cref="Use"/>; the first at once).
    /// </summary>
    public static void StartBatch(Player p, Item item, SpellTemplate spell, int count, int? requestId, bool encrypted, Content.InvasionItem? ticket = null)
    {
        var world = p.World;
        var map = p.Map!;
        var t0 = world.GameNow;
        var interval = world.Config.BatchInterval.Ticks;
        var batch = new Batch(item, spell, Combat.AbilityPowerOf(p, item), Combat.CcAbilityPowerOf(p, item), count, t0, map, requestId, encrypted) { Ticket = ticket };
        p.Combat.Batch = batch;
        p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count, da0 = true });
        if (count > 1) p.SendAround(new BatchUseItemStartEvent { ddn = p.Id, btm = t0, btn = t0 + (count - 1) * interval, bto = item.Template.Index });
        Log.Info("items", $"'{p.Character.Name}' uses {item.Template.Name} x{count} ({spell})");
        for (var k = 0; k < count; k++)
        {
            var at = t0 + k * interval;
            SpellCasts.At(world, map, at, () => Use(p, batch, at));
        }
    }

    /// <summary>
    /// One use (SPEC §3.2 step 3): CastSpell {0 me, 1 me, 2 position, 3 spell, 4 = 5 now, 6 0, 7 0} to it and its observers, the
    /// spell's effects (<see cref="SpellCasts.ExecuteFirst"/>), one item less; then, before the last use, NewSimpleItem {2 count
    /// left, 3 true} and CastHit {0 me, 1 me, 2 spell, 3 1, 4 0}; the last use ends the batch (<see cref="End"/>) and its CastHit
    /// follows. A stack no longer in the bag ends it.
    /// </summary>
    private static void Use(Player p, Batch batch, long ticks)
    {
        if (batch.Over || p.Combat.Batch != batch || p.Map != batch.Map) return;
        var item = batch.Item;
        if (!p.Inventory.Bag.Contains(item) || item.Count <= 0)
        {
            End(p, batch, "the stack is gone");
            return;
        }
        if (batch.Ticket is { } ticket)
        {
            UseTicket(p, batch, ticket, ticks);
            return;
        }
        var spell = batch.Spell;
        // A chest's loot is rolled first: when it does not all fit the bag, the use is refused and nothing is used up. (The chest's
        // own slot is not counted as freed: it is taken out only at the end of the batch.)
        IReadOnlyList<(ItemTemplate Item, int Count)>? loot = null;
        if (spell.Effects.FirstOrDefault(e => e.Name.LocalName == "generateloot") is { } generate)
        {
            loot = Roll(p.World, (string?)generate.Attribute("lootlist"), batch.Map.Random, spell.Name);
            if (!Network.WorldSession.Fits(p.Inventory.Bag, loot))
            {
                End(p, batch, "the loot does not fit the inventory");
                return;
            }
        }
        // A dungeon map places its exit or is not used up (stageG dungeons SPEC §10): checked again here, the handler's check came earlier.
        if (!p.World.Dungeons.MapCanPlace(p, spell))
        {
            End(p, batch, "no dungeon exit marker free");
            return;
        }
        var here = SpellMovement.PositionOf(p, ticks);
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvx = here, bvy = spell.Id, bvz = ticks, bv0 = ticks, Extra = new() { [6] = (byte)0, [7] = (short)0 } });
        SpellCasts.ExecuteFirst(new SpellCasts.SpellContext(p, batch.Map, spell, 0, batch.Ap, null, here, ticks) { CcAbilityPower = batch.CcAp, Item = item.Template, Loot = loot }, spell);
        item.Count--;
        batch.Done++;
        p.World.Plugins.ItemUsed(p, item.Template.Name, 1, "inventory"); // the plugins' ItemUsed
        var hit = Combat.CastHit(p.Id, p.Id, spell.Id, 0);
        if (batch.Done < batch.Count && item.Count > 0)
        {
            p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count, da0 = true });
            p.SendAround(hit);
            return;
        }
        End(p, batch, null);
        p.SendAround(hit);
    }

    /// <summary>
    /// Stage I (invasions SPEC §2.2): the one use of an invasion ticket: its tier (a fixed-tier item's, else the player's chosen one) and
    /// cost (ItemCost) checked again (<see cref="PlayerInvasions.CanStart"/>, the bag's stacks of it hold the cost); refused: a chat line
    /// and the end with nothing used (rc 0, no box, no cast). Else the invasion (<see cref="PlayerInvasions.Start"/>; the spell's own
    /// activaterandomdungeon does not run), then CastSpell of the map's spell, the cost taken (this stack first, then the bag's others of
    /// the item: NSI or InventoryDeleteItem each), the end (NSI or InventoryDeleteItem, rc 0) and CastHit. The run starts before the cast
    /// goes out, so a start refused late (no mobs at the chosen point) leaves no cast unresolved for the observers.
    /// </summary>
    private static void UseTicket(Player p, Batch batch, Content.InvasionItem ticket, long ticks)
    {
        var world = p.World;
        var item = batch.Item;
        var invasions = world.Invasions;
        var tier = ticket.Tier > 0 ? ticket.Tier : invasions.Chosen(p);
        var cost = invasions.Cost(ticket, tier);
        var type = world.EventSettings.Find(ticket.Kind);
        var why = type is null ? $"no event kind {ticket.Kind}" : Tickets(p, item) < cost ? $"a T{tier} invasion needs {cost} of these" : invasions.CanStart(p, tier, item: true);
        WorldEvents.Run? run = null;
        if (why is null)
        {
            try { run = invasions.Start(p, tier, type!, item: true); }
            catch (InvalidOperationException e) { why = e.Message; }
        }
        if (run is null)
        {
            WorldEvents.Tell(p, "Invasion: " + why);
            End(p, batch, why);
            return;
        }
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvx = SpellMovement.PositionOf(p, ticks), bvy = batch.Spell.Id, bvz = ticks, bv0 = ticks, Extra = new() { [6] = (byte)0, [7] = (short)0 } });
        var left = cost - Math.Min(cost, item.Count);
        item.Count -= cost - left;
        var bag = p.Inventory.Bag;
        foreach (var other in bag.Items.Where(i => i != item && i.Template == item.Template).ToList())
        {
            if (left <= 0) break;
            var take = Math.Min(left, other.Count);
            other.Count -= take;
            left -= take;
            if (other.Count > 0) p.Session.Send(new NewSimpleItemEvent { dax = other.Id, day = other.Template.Index, daz = (short)other.Count });
            else
            {
                bag.Take(bag.IndexOf(other));
                p.Session.Send(new InventoryDeleteItemEvent { ckr = other.Id });
            }
        }
        batch.Done++;
        End(p, batch, null);
        p.SendAround(Combat.CastHit(p.Id, p.Id, batch.Spell.Id, 0));
    }

    /// <summary>Stage I: the tickets of <paramref name="item"/>'s kind in the player's bag, all its stacks (a T9/T10 start takes 2-3 across them).</summary>
    public static int Tickets(Player p, Item item) => p.Inventory.Bag.Items.Where(i => i.Template == item.Template).Sum(i => Math.Max(0, i.Count));

    /// <summary>
    /// The end of a batch (SPEC §3.2 step 4-5), after its last use or aborted (<paramref name="why"/>): a used-up stack NewSimpleItem
    /// {0, 1, 2 1} (in-use off; the count is not lowered in it) and InventoryDeleteItem {0}, a stack with items left NewSimpleItem
    /// {2 count} without key 3 (also when it was moved out of the bag meanwhile); the response rc 0 (empty), then BatchUseItemEnd
    /// {0 me} when n &gt; 1. Saved with the items.
    /// </summary>
    private static void End(Player p, Batch batch, string? why)
    {
        if (batch.Over) return;
        batch.Over = true;
        if (p.Combat.Batch == batch) p.Combat.Batch = null;
        var item = batch.Item;
        var bag = p.Inventory.Bag;
        if (item.Count <= 0 && bag.Contains(item))
        {
            p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = 1 });
            bag.Take(bag.IndexOf(item));
            p.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
        }
        // Items left: in-use off wherever the stack lies now (moved during the batch to the equipment or an open vault tab).
        else if (item.Count > 0 && Holds(p, item)) p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count });
        p.Session.AnswerConsumableUse(batch.RequestId, batch.Encrypted);
        if (batch.Count > 1) p.SendAround(new BatchUseItemEndEvent { ddn = p.Id });
        p.SaveItems();
        Log.Info("items", string.Create(CultureInfo.InvariantCulture,
            $"'{p.Character.Name}' used {item.Template.Name} {batch.Done} of {batch.Count} time(s){(why is null ? "" : $" ({why})")}, {Math.Max(0, item.Count)} left"));
    }

    /// <summary>Whether <paramref name="item"/> lies in the player's bag, equipment or an open vault tab (its client still has the object).</summary>
    private static bool Holds(Player p, Item item) =>
        p.Inventory.Bag.Contains(item) || p.Inventory.Equipment.Contains(item) || p.OpenVaults.Values.Any(v => v.Container.Contains(item));

    /// <summary>
    /// The batch under way stops (BatchUseItemCancel, leaving the map, the knockdown; SPEC §3.2 step 5): its remaining uses are
    /// dropped and it ends with what is left (<see cref="End"/>).
    /// </summary>
    public static void Abort(Player p, string why)
    {
        if (p.Combat.Batch is { } batch) End(p, batch, why);
    }

    // ---- saved over a relog (SPEC §6) ----

    /// <summary>
    /// Writes the player's own timed spells (their buffovertime and dummy sub-effects, with their start and durations; the
    /// over-time ticks are not kept) and the pending slot cooldowns into its character. Called after a slot cast's hit and
    /// when the player leaves the world; the caller saves.
    /// </summary>
    public static void Store(Player p)
    {
        var now = p.World.GameNow;
        p.Effects.Prune(now);
        var c = p.Character;
        c.Effects = [.. p.Effects.Spells.Where(s => s.Caster == p.Id && !s.Pending)
            .Select(s => new SavedEffect(s.Spell.Name, s.Start, s.Ap, s.CcAp, s.Stacks,
                [.. s.Subs.Select(sub => s.On(sub, now) && Saved.Contains(sub.Tag) && sub.DurationMs > 0 ? sub.DurationMs : -1)])
                { MovementOriginX = s.MovementOrigin?.X, MovementOriginY = s.MovementOrigin?.Y, MovementCluster = s.MovementCluster })
            .Where(e => e.Durations.Any(d => d > 0))];
        c.Cooldowns = [.. p.Combat.SlotCooldowns.Where(kv => kv.Value.End > now).Select(kv => new SavedCooldown(kv.Key, kv.Value.Start, kv.Value.End, kv.Value.Base))];
    }

    /// <summary>
    /// At Join, the player on its map (before its bars are worked out): the saved spells that have not ended come back with
    /// their start, Ap, CcAp, stacks and durations (their end timers armed again), the ended ones are dropped; the same for the
    /// slot cooldowns. A spell the spell table no longer has, or whose effect list changed, is dropped.
    /// </summary>
    public static void Restore(Player p)
    {
        var world = p.World;
        var now = world.GameNow;
        var c = p.Character;
        foreach (var saved in c.Effects)
        {
            if (world.Spells?.Template(saved.Spell) is not { } spell || saved.Durations.Length != spell.Effects.Count) continue;
            var subs = new SubEffect?[spell.Effects.Count];
            for (var i = 0; i < subs.Length; i++)
            {
                var ms = saved.Durations[i];
                if (ms <= 0 || saved.Start + ms * TimeSpan.TicksPerMillisecond <= now) continue;
                if (SpellEffects.Make(world, p, saved.Ap, saved.CcAp, spell.Effects[i], i, p, now) is not { } sub) continue;
                sub.DurationMs = ms;
                subs[i] = sub;
            }
            if (subs.All(s => s is null)) continue;
            var entry = SpellEffects.Apply(world, p, spell, p.Id, saved.Ap, saved.CcAp, saved.Start, subs);
            if (saved.MovementOriginX is { } originX && saved.MovementOriginY is { } originY
                && float.IsFinite(originX) && float.IsFinite(originY))
            {
                entry.MovementOrigin = new System.Numerics.Vector2(originX, originY);
                entry.MovementCluster = saved.MovementCluster ?? p.Map?.Cluster;
            }
            entry.Stacks = Math.Max((byte)1, saved.Stacks);
        }
        foreach (var cd in c.Cooldowns)
            if (cd.End > now) p.Combat.SlotCooldowns[cd.Slot] = new SlotCooldown(cd.Start, cd.End, cd.Base);
        if (p.Effects.Count > 0 || p.Combat.SlotCooldowns.Count > 0)
            Log.Info("items", $"'{c.Name}' keeps {p.Effects.Count} spell(s) and {p.Combat.SlotCooldowns.Count} slot cooldown(s) from before");
    }

    /// <summary>
    /// What a Join sends before JoinFinished (SPEC §6, the official order: ActiveSpellEffectsUpdate, then SpellCooldownUpdate):
    /// the player's own list when it has one (whether the official server sends an empty one is open, Q5), and SpellCooldownUpdate
    /// (249) {0 slots, 1 ends, 2 starts, 3 base spans (Int64 ticks: c4[] takes only Int64[]), 4 zeros} when a slot cooldown is
    /// pending (captured {0 [2], 1 [end], 2 [start], 3 [450000000], 4 [0]}). <paramref name="between"/> goes between the two (the mount
    /// events and MountCooldownUpdate, stageD mounts SPEC §4.8).
    /// </summary>
    public static IEnumerable<AlbionEvent> JoinEvents(Player p, IEnumerable<AlbionEvent>? between = null)
    {
        var now = p.World.GameNow;
        if (SpellEffects.ForNewObserver(p, now) is { } list) yield return list;
        foreach (var e in between ?? []) yield return e;
        var pending = p.Combat.SlotCooldowns.Where(kv => kv.Value.End > now).OrderBy(kv => kv.Key).ToList();
        if (pending.Count == 0) yield break;
        yield return new SpellCooldownUpdateEvent
        {
            dy7 = [.. pending.Select(kv => kv.Key)],
            dy8 = [.. pending.Select(kv => kv.Value.End)],
            dy9 = [.. pending.Select(kv => kv.Value.Start)],
            dza = [.. pending.Select(kv => kv.Value.Base)],
            dzb = new byte[pending.Count],
        };
    }
}
