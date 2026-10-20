using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>The sub-action of ACT 8 (op 55 key 14; the client's request classes cm2.b-g, abb() 0-5).</summary>
public enum LegendaryAction : byte { Awaken = 0, Disenchant = 1, GenerateTraits = 2, ApplyTrait = 3, RerollTraitValue = 4, Reattune = 5 }

/// <summary>AwakenItemErrorCode (key 3 of a failed op 55 with RC 2202 for ACT 8 / Awaken).</summary>
public enum AwakenError : byte
{
    OutOfRange = 2, ItemTypeCannotBeAwakened = 3, ItemDamaged = 4, ItemTierTooLow = 5, ItemEnchantmentTooLow = 6, NotEnoughResources = 7,
    NotEnoughSilver = 14, PriceChanged = 15, AlreadyAwakened = 16, FeatureDisabled = 19,
}

/// <summary>
/// The trait error codes (key 3, RC 2202): GenerateItemTraitsErrorCode, ApplyItemTraitErrorCode, RerollItemTraitValueErrorCode,
/// ReattuneItemErrorCode and DisenchantItemErrorCode share 2 OutOfRange, 3 PriceChanged (not Reattune), 4 ItemIsNotAwakened (Reattune 3);
/// the rest is per enum and written out where used.
/// </summary>
public static class TraitError
{
    public const byte OutOfRange = 2, PriceChanged = 3, ItemIsNotAwakened = 4;
    public const byte GenerateNotEnoughAttunement = 8, GenerateAttunementCostsChanged = 9, GenerateNotEnoughSilver = 10, GenerateSilverCostsChanged = 11,
        GenerateOtherTraitHasPendingTraits = 13;
    public const byte ApplyNoPendingModification = 8;
    public const byte RerollNotEnoughAttunement = 8, RerollPendingTraits = 9, RerollAttunementCostsChanged = 10, RerollNotEnoughSilver = 11,
        RerollSilverCostsChanged = 12, RerollMaxRollValueReached = 13;
    public const byte ReattuneItemIsNotAwakened = 3, ReattuneNotEnoughSilver = 6, ReattuneAlreadyAttuned = 7;
}

/// <summary>
/// Awakened weapons at the meld building (op 55 ACT 8 AwakenLegendaryItem; the client's component cm2 on cpj; analysis/work/stageN/awakening/
/// SPEC.md). Request (c32.i 0x180A68160): 1 building, 2 ACT 8, 3 counter, 5 [the item object], 14 the sub-action (<see cref="LegendaryAction"/>),
/// 15 the trait slot (generate, apply, reroll), 16 the chosen option (apply), 17 the silver price (a7m), 18 the attunement cost (a7m),
/// 19 the focus the client would use, 20 cover missing attunement with focus. As the other station actions: checked at the start (failure =
/// RC 2202 with the sub-action's error code in key 3), nothing taken until the end; at the end everything is checked again and then paid and
/// done in one step, so a failure on the way cancels without a loss. The item object stays (only its soul changes): no item is ever made or
/// destroyed here.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The trait actions' duration (not in the data; as <see cref="RepairTime"/>).</summary>
    public static readonly TimeSpan TraitTime = RepairTime;

    /// <summary>What awakening <paramref name="item"/> costs: silver (a7m) and resources (awakeningrequirements × the tier scaling).</summary>
    public (long Silver, List<(string Name, int Count)> Resources) AwakenCost(LegendaryData data, Item item)
    {
        var scale = data.Scale(item.Template.Tier);
        var silver = (long)Math.Round((decimal)(data.AwakenSilver * scale) * Pricing.A7m);
        var resources = data.AwakenResources.Select(r => (r.Name, (int)Math.Max(0, Math.Round(r.Count * scale)))).Where(r => r.Item2 > 0).ToList();
        return (silver, resources);
    }

    /// <summary>
    /// The attunement (a7m) a trait action costs: base × the tier scaling × the soul's strain (cm5.d_4 0x1829660E0); the silver is that ×
    /// attunementtosilverfactor (cm5.e 0x1829664F0).
    /// </summary>
    public static (long Attunement, long Silver) TraitCost(LegendaryData data, Item item, double baseCost)
    {
        var strain = (item.Soul?.Strain ?? 10000) / (double)Pricing.A7m;
        var attunement = (long)Math.Round((decimal)(baseCost * data.Scale(item.Template.Tier) * strain) * Pricing.A7m);
        return (attunement, (long)Math.Round(attunement * (decimal)data.AttunementToSilver));
    }

    /// <summary>The silver (a7m) of a reattunement: reattunementsilvercost × strain × the tier scaling (cna.aa8 0x180B7BDA0).</summary>
    public static long ReattuneCost(LegendaryData data, Item item) =>
        (long)Math.Round((decimal)(data.ReattuneSilver * ((item.Soul?.Strain ?? 10000) / (double)Pricing.A7m) * data.Scale(item.Template.Tier)) * Pricing.A7m);

    /// <summary>
    /// <paramref name="count"/> different traits of the pool that fit <paramref name="group"/> and are not in <paramref name="taken"/>, picked by
    /// the rarity weights (common 2, uncommon 1), each with an initial roll in [initialrollmin, initialrollmax].
    /// </summary>
    internal static SoulTrait[] RollTraits(LegendaryData data, LegendaryGroup group, IEnumerable<string> taken, int count, Random random)
    {
        var pool = data.Traits.Where(t => t.Fits(group.Keywords) && !taken.Contains(t.Name)).ToList();
        var picked = new List<SoulTrait>();
        while (picked.Count < count && pool.Count > 0)
        {
            var roll = random.NextDouble() * pool.Sum(t => t.Weight);
            var k = 0;
            while (k < pool.Count - 1 && (roll -= pool[k].Weight) >= 0) k++;
            picked.Add(new SoulTrait(pool[k].Name, data.InitialRollMin + random.NextDouble() * (data.InitialRollMax - data.InitialRollMin)));
            pool.RemoveAt(k);
        }
        return [.. picked];
    }

    /// <summary>
    /// A trait value reroll of <paramref name="old"/> (reroll settings; the official formula is server-side, SPEC §3 assumption): a fresh
    /// roll 0-1 that replaces the value when higher, else the value + failurebonus (0.02-0.06); then with criticalchance (0.3375) a critical
    /// + criticalbonus (0.1-0.3). Never lower, at most 1.
    /// </summary>
    internal static (double Value, bool Critical) RerollValue(LegendaryData data, double old, Random random)
    {
        var fresh = random.NextDouble();
        var value = fresh > old ? fresh : old + data.FailureBonusMin + random.NextDouble() * (data.FailureBonusMax - data.FailureBonusMin);
        var critical = random.NextDouble() < data.CriticalChance;
        if (critical) value += data.CriticalBonusMin + random.NextDouble() * (data.CriticalBonusMax - data.CriticalBonusMin);
        return (Math.Min(1, value), critical);
    }

    /// <summary>ACT 8: the sub-action of key 14 after the checks every one shares (the item is the player's, not the mount).</summary>
    private AlbionResponse? StartLegendary(Player player, Building building, ActionOnBuildingStartRequest req)
    {
        var sub = (LegendaryAction)req.ab;
        AlbionResponse Generic(string why) => StationRefused(player, req, StationGenericError, 0, why);
        AlbionResponse Code(byte e, string why) => StationRefused(player, req, StationComponentError, e, $"{sub}: {why}");
        if (world.Legendary is not { } data) return sub == LegendaryAction.Awaken ? Code((byte)AwakenError.FeatureDisabled, "no legendaryitems data") : Generic("no legendaryitems data");
        if ((req.s ?? []).Distinct().ToList() is not [var id] || player.Inventory.FindItem(id) is not { Template.IsEquipment: true } item
            || player.Inventory.Equipment.IndexOf(item) == Mounts.MountSlot)
            return sub == LegendaryAction.Awaken ? Code((byte)AwakenError.ItemTypeCannotBeAwakened, "not one weapon of the player listed") : Generic("not one item of the player listed");
        return sub switch
        {
            LegendaryAction.Awaken => StartAwaken(player, building, req, data, item),
            LegendaryAction.Disenchant or LegendaryAction.GenerateTraits or LegendaryAction.ApplyTrait or LegendaryAction.RerollTraitValue or LegendaryAction.Reattune
                => StartTraitAction(player, building, req, data, item, sub),
            _ => Generic($"sub-action {req.ab} unknown"),
        };
    }

    /// <summary>
    /// Awaken (sub 0; cm7, AwakenItemModule): the weapon must be of an item group (ItemTypeCannotBeAwakened), not awakened (AlreadyAwakened),
    /// tier ≥ mintier 4 (ItemTierTooLow), enchantment ≥ minenchant 4 (ItemEnchantmentTooLow), not broken (ItemDamaged); key 17 the silver
    /// (awakeningrequirements silver × tier scaling, <see cref="Pricing.Accepts"/>, PriceChanged) in the wallet (NotEnoughSilver) and the
    /// resources (craftresource count × tier scaling: UNIQUE_GVGTOKEN_GENERIC and QUESTITEM_TOKEN_AVALON, NotEnoughResources) in the
    /// inventory. At the end (actionduration awakening 1.5 s): paid, the item gets a soul attuned to the player with basetraits (3) traits
    /// (<see cref="RollTraits"/>), re-sent (NewEquipmentItem key 11 = 1, then event 37), NewBuilding, 66.
    /// </summary>
    private AlbionResponse? StartAwaken(Player player, Building building, ActionOnBuildingStartRequest req, LegendaryData data, Item item)
    {
        AlbionResponse Refuse(AwakenError e, string why) => StationRefused(player, req, StationComponentError, (byte)e, $"awaken: {why}");
        if (data.GroupOf(item.Template) is not { } group) return Refuse(AwakenError.ItemTypeCannotBeAwakened, $"{item.Template.Name} is in no item group");
        if (item.Soul is not null) return Refuse(AwakenError.AlreadyAwakened, $"{item} is awakened");
        if (item.Template.Tier < data.MinTier) return Refuse(AwakenError.ItemTierTooLow, $"{item.Template.Name} is below tier {data.MinTier}");
        if (item.Template.Enchantment < data.MinEnchant) return Refuse(AwakenError.ItemEnchantmentTooLow, $"{item.Template.Name} is below enchantment {data.MinEnchant}");
        if (item.Template.MaxDurability > 0 && item.DurabilityRaw <= 0) return Refuse(AwakenError.ItemDamaged, $"{item} is broken");
        var (silver, resources) = AwakenCost(data, item);
        if (!Pricing.Accepts(req.ae, silver))
            return Refuse(AwakenError.PriceChanged, string.Create(CultureInfo.InvariantCulture, $"the client's price {req.ae / 10000.0:0.####} is not the server's {silver / 10000.0:0.####}"));
        var price = Math.Max(req.ae, 0);
        if (player.Character.Silver < price) return Refuse(AwakenError.NotEnoughSilver, $"{player.Character.Silver / 10000} silver of {price / 10000}");
        if (AwakenNeeds(player, resources) is { Missing: { } missing }) return Refuse(AwakenError.NotEnoughResources, missing);
        var action = BeginLegendary(player, building, req, data.AwakenSeconds, "awakens");
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' awakens {item} at {building.Template.Name} for {price / 10000.0:0.####} silver{string.Concat(resources.Select(r => $", {r.Count} x {r.Name}"))}"));
        player.Map!.Timers.At(world.ClockAt(action.End), () =>
        {
            if (!StillOn(player, action, item, null)) return;
            if (item.Soul is not null || AwakenNeeds(player, resources) is not { Needs: { } needs } || player.Character.Silver < price)
            {
                CancelAction(player, "the item, the resources or the silver changed", tellClient: true);
                return;
            }
            if (price > 0 && !Wallet.TrySpend(player, price, $"awakening at {building.Template.Name}"))
            {
                CancelAction(player, "not enough silver any more", tellClient: true);
                return;
            }
            foreach (var (type, need) in needs) TakeItems(player, type, need);
            var c = player.Character;
            item.Soul = new ItemSoul
            {
                Name = c.Name, AttunedId = c.Id, AttunedName = c.Name,
                Traits = RollTraits(data, group, [], group.BaseTraits, world.CraftRandom),
            };
            EndLegendary(player, action, item, resend: true);
            Log.Info(Name, $"'{c.Name}' awakened {item}: {string.Join(", ", item.Soul.Traits.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Name} {t.Roll:0.###}")))}");
        });
        return null;
    }

    /// <summary>The resources of an awakening in the inventory (types and counts), or what is missing.</summary>
    private (List<(ItemTemplate Type, int Count)>? Needs, string? Missing) AwakenNeeds(Player player, List<(string Name, int Count)> resources)
    {
        var needs = new List<(ItemTemplate, int)>();
        foreach (var (name, count) in resources)
        {
            if (world.Items.Find(name) is not { } type) return (null, $"unknown resource {name}");
            var have = CountOf(player.Inventory.Bag, type);
            if (have < count) return (null, $"{have} of {count} x {name}");
            needs.Add((type, count));
        }
        return (needs, null);
    }

    /// <summary>
    /// The trait actions on an awakened weapon (subs 1-5):
    /// <list type="bullet">
    /// <item>Disenchant (1, cm8): the soul goes, the weapon is a plain one again (re-sent with key 11 = 0); free; actionduration disenchanting.</item>
    /// <item>GenerateTraits (2, cm9; key 15 the slot): <see cref="LegendaryData.OptionsPerModify"/> replacement options for the slot (event 551
    /// ModifyItemTraitFinished {0 player, 1 time, 2 item, 3 slot, 4 names, 5 rolls}, keys 10/11 of event 37). Costs addreplace × tier scaling ×
    /// strain attunement and that × 33 silver; free when the slot already has its options; OtherTraitHasPendingTraits while another slot has.</item>
    /// <item>ApplyTrait (3, cm1; 15 slot, 16 the option): the option replaces the slot's trait; an option out of range keeps the old trait; the
    /// options go either way (NoPendingModification without).</item>
    /// <item>RerollTraitValue (4, cnb; 15 slot): <see cref="RerollValue"/>; event 552 RerollItemTraitValueFinished {0 player, 1 time, 2 item,
    /// 3 slot, 4 old, 5 new, 6 critical}. Costs reroll × tier scaling × strain attunement and × 33 silver; MaxRollValueReached at 1; PendingTraits
    /// while the slot has options.</item>
    /// <item>Reattune (5, cna): attunes the soul to the player (AlreadyAttuned when it is); reattunementsilvercost × strain × tier scaling silver.</item>
    /// </list>
    /// Attunement: key 18 must not exceed the server's cost (AttunementCostsChanged) and is taken from the soul; with key 20 the part the soul
    /// lacks is taken as focus (× attunementtofocusfactor), else NotEnoughAttunement. Silver as key 17 per <see cref="Pricing.Accepts"/>.
    /// </summary>
    private AlbionResponse? StartTraitAction(Player player, Building building, ActionOnBuildingStartRequest req, LegendaryData data, Item item, LegendaryAction sub)
    {
        AlbionResponse Refuse(byte e, string why) => StationRefused(player, req, StationComponentError, e, $"{sub}: {why}");
        if (item.Soul is not { } soul)
            return Refuse(sub == LegendaryAction.Reattune ? TraitError.ReattuneItemIsNotAwakened : TraitError.ItemIsNotAwakened, $"{item} is not awakened");
        var slot = req.ac;
        var slotted = sub is LegendaryAction.GenerateTraits or LegendaryAction.ApplyTrait or LegendaryAction.RerollTraitValue;
        if (slotted && (slot < 0 || slot >= soul.Traits.Length)) return StationRefused(player, req, StationGenericError, 0, $"{sub}: no trait slot {slot}");
        long attunement = 0, silver = 0;
        string what;
        switch (sub)
        {
            case LegendaryAction.Disenchant:
                what = "disenchants";
                break;
            case LegendaryAction.ApplyTrait:
                if (soul.PendingSlot != slot) return Refuse(TraitError.ApplyNoPendingModification, $"slot {slot} has no options");
                what = $"applies option {req.ad} to slot {slot} of";
                break;
            case LegendaryAction.Reattune:
                if (soul.AttunedTo(player.Character.Id)) return Refuse(TraitError.ReattuneAlreadyAttuned, "attuned to the player already");
                silver = ReattuneCost(data, item);
                if (!Pricing.Accepts(req.ae, silver)) // ReattuneItemErrorCode has no PriceChanged: the generic error
                    return StationRefused(player, req, StationGenericError, 0, string.Create(CultureInfo.InvariantCulture, $"Reattune: the client's price {req.ae / 10000.0:0.####} is not the server's {silver / 10000.0:0.####}"));
                silver = Math.Max(req.ae, 0);
                if (player.Character.Silver < silver) return Refuse(TraitError.ReattuneNotEnoughSilver, $"{player.Character.Silver / 10000} silver of {silver / 10000}");
                what = "reattunes";
                break;
            default:
            {
                var generate = sub == LegendaryAction.GenerateTraits;
                if (generate && soul.PendingSlot >= 0 && soul.PendingSlot != slot)
                    return Refuse(TraitError.GenerateOtherTraitHasPendingTraits, $"slot {soul.PendingSlot} has options");
                if (!generate && soul.PendingSlot == slot) return Refuse(TraitError.RerollPendingTraits, $"slot {slot} has options");
                if (!generate && soul.Traits[slot].Roll >= 1) return Refuse(TraitError.RerollMaxRollValueReached, $"slot {slot} is at 1");
                var free = generate && soul.PendingSlot == slot;
                (attunement, silver) = free ? (0, 0) : TraitCost(data, item, generate ? data.AddReplaceBase : data.RerollBase);
                if (!Pricing.Accepts(req.ae, silver))
                    return Refuse(generate ? TraitError.GenerateSilverCostsChanged : TraitError.RerollSilverCostsChanged,
                        string.Create(CultureInfo.InvariantCulture, $"the client's silver {req.ae / 10000.0:0.####} is not the server's {silver / 10000.0:0.####}"));
                if (req.af < 0 || req.af > attunement + Pricing.A7m)
                    return Refuse(generate ? TraitError.GenerateAttunementCostsChanged : TraitError.RerollAttunementCostsChanged,
                        string.Create(CultureInfo.InvariantCulture, $"the client's attunement {req.af / 10000.0:0.####} is not the server's {attunement / 10000.0:0.####}"));
                silver = Math.Max(req.ae, 0);
                if (player.Character.Silver < silver)
                    return Refuse(generate ? TraitError.GenerateNotEnoughSilver : TraitError.RerollNotEnoughSilver, $"{player.Character.Silver / 10000} silver of {silver / 10000}");
                if (AttunementShort(player, soul, attunement, req.ah) is { } why)
                    return Refuse(generate ? TraitError.GenerateNotEnoughAttunement : TraitError.RerollNotEnoughAttunement, why);
                what = generate ? $"generates traits for slot {slot} of" : $"rerolls the value of slot {slot} of";
                break;
            }
        }
        var seconds = sub switch { LegendaryAction.Disenchant => data.DisenchantSeconds, LegendaryAction.Reattune => data.ReattuneSeconds, _ => TraitTime.TotalSeconds };
        var action = BeginLegendary(player, building, req, seconds, what);
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' {what} {item} at {building.Template.Name} ({silver / 10000.0:0.####} silver, {attunement / 10000.0:0.####} attunement)"));
        var useFocus = req.ah;
        var option = req.ad;
        player.Map!.Timers.At(world.ClockAt(action.End), () =>
        {
            if (!StillOn(player, action, item, soul)) return;
            if (player.Character.Silver < silver || AttunementShort(player, soul, attunement, useFocus) is not null)
            {
                CancelAction(player, "the silver or the attunement is gone", tellClient: true);
                return;
            }
            if (silver > 0 && !Wallet.TrySpend(player, silver, $"{sub} at {building.Template.Name}"))
            {
                CancelAction(player, "not enough silver any more", tellClient: true);
                return;
            }
            var now = world.GameNow;
            var paid = soul;
            if (attunement > 0)
            {
                var fromSoul = Math.Min(attunement, soul.Attunement);
                if (fromSoul < attunement && Focus.Spend(player, (attunement - fromSoul) / (double)Pricing.A7m * data.AttunementToFocus, now) is var (used, _))
                    Focus.Send(player, now, -used);
                paid = soul with { Attunement = soul.Attunement - fromSoul, Spent = soul.Spent + attunement };
            }
            var resend = false;
            switch (sub)
            {
                case LegendaryAction.Disenchant:
                    item.Soul = null;
                    resend = true;
                    break;
                case LegendaryAction.Reattune:
                    item.Soul = paid with { AttunedId = player.Character.Id, AttunedName = player.Character.Name };
                    break;
                case LegendaryAction.ApplyTrait:
                    var traits = paid.Traits.ToArray();
                    if (option >= 0 && option < paid.Pending.Length) traits[slot] = paid.Pending[option];
                    item.Soul = paid with { Traits = traits, PendingSlot = -1, Pending = [] };
                    break;
                case LegendaryAction.GenerateTraits:
                    var options = paid.PendingSlot == slot ? paid.Pending
                        : RollTraits(data, data.GroupOf(item.Template) ?? new LegendaryGroup([], 3, new HashSet<string>()), paid.Traits.Select(t => t.Name),
                            data.OptionsPerModify, world.CraftRandom);
                    item.Soul = paid with { PendingSlot = slot, Pending = options };
                    SendEvent(new ModifyItemTraitFinishedEvent
                    {
                        ddn = player.Id, cr0 = now, cr1 = item.Id, cr2 = slot, cr3 = [.. options.Select(o => o.Name)], cr4 = [.. options.Select(o => o.Roll)],
                    });
                    break;
                case LegendaryAction.RerollTraitValue:
                    var old = paid.Traits[slot];
                    var (value, critical) = RerollValue(data, old.Roll, world.CraftRandom);
                    var rolled = paid.Traits.ToArray();
                    rolled[slot] = old with { Roll = value };
                    item.Soul = paid with { Traits = rolled };
                    SendEvent(new RerollItemTraitValueFinishedEvent { ddn = player.Id, dvn = now, dvo = item.Id, dvp = slot, dvq = old.Roll, dvr = value, dvs = critical });
                    break;
            }
            EndLegendary(player, action, item, resend);
            var state = item.Soul is { } s
                ? string.Join(", ", s.Traits.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Name} {t.Roll:0.###}")))
                  + string.Create(CultureInfo.InvariantCulture, $", attunement {s.Attunement / 10000.0:0.####}")
                : "no soul";
            Log.Info(Name, $"'{player.Character.Name}' {sub} on {item} done: {state}");
        });
        return null;
    }

    /// <summary>Why the soul (and with <paramref name="useFocus"/> the focus) cannot pay <paramref name="cost"/> attunement; null = it can.</summary>
    private string? AttunementShort(Player player, ItemSoul soul, long cost, bool useFocus)
    {
        if (cost <= soul.Attunement) return null;
        if (!useFocus || world.Legendary is not { } data) return string.Create(CultureInfo.InvariantCulture, $"attunement {soul.Attunement / 10000.0:0.##} of {cost / 10000.0:0.##}");
        var focus = (cost - soul.Attunement) / (double)Pricing.A7m * data.AttunementToFocus;
        var have = Focus.Current(player, world.GameNow);
        return have + 1e-9 >= focus ? null : string.Create(CultureInfo.InvariantCulture, $"focus {have:0.##} of {focus:0.##} for the missing attunement");
    }

    /// <summary>Starts the timed action (NewBuilding key 16, event 64 to the observers), as the enchanting.</summary>
    private BuildingAction BeginLegendary(Player player, Building building, ActionOnBuildingStartRequest req, double seconds, string what)
    {
        var now = world.GameNow;
        var action = new BuildingAction(building, req.p, req.q, now, now + TimeSpan.FromSeconds(Math.Max(0, seconds)).Ticks);
        Mounts.BeforeAction(player, what);
        player.Action = action;
        SendEvent(building.NewBuilding(inUse: true));
        StartedToObservers(player, action);
        return action;
    }

    /// <summary>Whether the action still runs and the item is still the player's with the soul it started with (else cancelled).</summary>
    private bool StillOn(Player player, BuildingAction action, Item item, ItemSoul? soul)
    {
        if (action.Over || player.Action != action || player != Player) return false;
        if (player.Inventory.FindItem(item.Id) == item && item.Soul == soul) return true;
        CancelAction(player, "the item is gone or changed", tellClient: true);
        return false;
    }

    /// <summary>
    /// The end of an ACT 8 action: the item re-sent (<paramref name="resend"/>: NewEquipmentItem with its key 11 and the soul; else only the
    /// soul event), the worn weapon's stats recalculated (its traits), NewBuilding, 66, saved.
    /// </summary>
    private void EndLegendary(Player player, BuildingAction action, Item item, bool resend)
    {
        action.Over = true;
        player.Action = null;
        if (resend) SendItemObject(item);
        else if (item.Soul is not null) SendEvent(SoulEvent(item, player.Character.Id));
        if (player.Inventory.IsEquipped(item) && player.RecalculateStats(world.GameNow)) SendAround(player, Combat.PlayerCombo(player, world.GameNow));
        SendEvent(action.Building.NewBuilding());
        Finished(player, action);
        player.SaveItems();
    }
}
