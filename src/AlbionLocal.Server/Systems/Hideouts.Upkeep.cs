using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>bcv.UpgradeResult (the client's enum; sent as the return code of UpgradeHideout 394 [H]).</summary>
public enum HideoutUpgradeResult : short
{
    Ok = 0, InternalServerError = 1, AlreadyUpgrading = 2, AlreadyAtMaxLevel = 3, NotInOwnersGuild = 4, InvalidGuildPermission = 5, NotEnoughSilver = 6,
    PriceChanged = 7, NotEnoughResources = 8,
}

/// <summary>
/// Hideouts phase H4 (SPEC §7 H4): the food (HideoutFillNutrition 397 with items.xml nutrition, the level's storage), its daily upkeep at the
/// zone's prime time (dailyupkeepnutrition; a starving day costs a shield, a fed day gives one back: shieldregenerationrate), the upgrades
/// (UpgradeHideout 394: the next level's silver and resources from the upgrading member, its time, the players inside put out at the end;
/// nobody enters meanwhile) and the tribute (HideoutSetTribute 400; paid daily from the guild account to the owner of the zone's territory,
/// nothing without an owner).
/// </summary>
public sealed partial class Hideouts
{
    /// <summary>RcOpHideoutSetTribute NoPermission.</summary>
    public const short TributeNoPermission = 16000;

    /// <summary>At most this many missed days are caught up at once (a server that was down for weeks).</summary>
    private const int MaxCatchUpDays = 30;

    /// <summary>The hideout of a request's key 0: its management (inside) or its surface object; null for any other object.</summary>
    public Hideout? OfObject(Player p, long id) => p.Map?.Objects.GetValueOrDefault(id) switch
    {
        HideoutManagement m => m.Hideout,
        HideoutObject o => o.Hideout,
        _ => null,
    };

    /// <summary>
    /// Every second for each hideout (<see cref="Update"/>): the daily upkeep and tribute at each of the zone's prime-time starts since the last
    /// one, and a running upgrade that is done (<see cref="Upgraded"/>; before a day that came after its end).
    /// </summary>
    private void UpdateUpkeep(Hideout h, long now)
    {
        if (!h.Built) return;
        var last = new DateTime(Math.Max(h.LastUpkeep, h.Placed), DateTimeKind.Utc);
        var due = NextPrimeTime(h.Cluster, last);
        var changed = false;
        for (var i = 0; due.Ticks <= now && i < MaxCatchUpDays; i++)
        {
            Upgraded(h, due.Ticks); // an upgrade done before this day's upkeep counts first
            Daily(h, due);
            h.LastUpkeep = due.Ticks;
            due = due.AddDays(1);
            changed = true;
        }
        if (due.Ticks <= now) h.LastUpkeep = now; // the rest of a long downtime is forgiven
        Upgraded(h, now);
        if (!changed) return;
        store?.SaveHideout(h);
        Refresh(h);
    }

    /// <summary>A running upgrade of <paramref name="h"/> done by <paramref name="at"/>: the next level, its shields, the players inside put out, the guild's mail.</summary>
    private void Upgraded(Hideout h, long at)
    {
        if (!h.Upgrading || Data.Level(h.Level + 1) is not { } next || at < h.UpgradeStarted + TimeSpan.FromSeconds(next.UpgradeSeconds).Ticks) return;
        h.Level = next.Index;
        h.UpgradeStarted = 0;
        h.Shields = next.MaxShields;
        store?.SaveHideout(h);
        Evict(h, "upgrade", "The hideout has been upgraded: it opens again.");
        Mail(h, "Hideout upgrade finished", $"The guild's hideout in {ZoneName(h.Cluster)} is now level {next.Index + 1} ({next.Cluster}).");
        Log.Info("world", $"{h}: upgraded to level {h.Level} ({next.Cluster})");
        Refresh(h);
    }

    /// <summary>
    /// One day's upkeep of <paramref name="h"/> (Hideouts:Upkeep): its level's dailyupkeepnutrition from the food; too little = starving (the food
    /// gone, a shield lost, the guild's mail), enough = a shield back (shieldregenerationrate) up to the level's. Then the tribute.
    /// </summary>
    private void Daily(Hideout h, DateTime at)
    {
        var level = Data.Level(h.Level) ?? Data.Levels[0];
        if (S.Upkeep)
        {
            if (h.Nutrition >= level.DailyUpkeep)
            {
                h.Nutrition -= level.DailyUpkeep;
                if (h.Shields < level.MaxShields) h.Shields++;
            }
            else
            {
                h.Nutrition = 0;
                h.Shields = Math.Max(0, h.Shields - 1);
                Mail(h, "Hideout starving", $"The guild's hideout in {ZoneName(h.Cluster)} had too little food for the day ({level.DailyUpkeep} needed) and lost a shield ({h.Shields} left).");
                Log.Info("world", $"{h}: starving at {at:yyyy-MM-dd HH:mm}, {h.Shields} shield(s) left");
            }
        }
        PayTribute(h);
    }

    /// <summary>
    /// The daily tribute of <paramref name="h"/>: its <see cref="Hideout.Tribute"/> (at most what the account holds) from its guild's account to the
    /// guild owning a territory of its zone (world.xml; the first owned one); none without an owner or to its own guild. The silver that was paid.
    /// </summary>
    public long PayTribute(Hideout h)
    {
        if (h.Tribute <= 0 || world.Guilds.ById(h.Guild) is not { } payer) return 0;
        var owner = world.Territories.All.Where(t => t.Cluster.Id == h.Cluster && t.Owned && t.Owner != h.Guild).Select(t => world.Guilds.ById(t.Owner)).FirstOrDefault(g => g is not null);
        if (owner is null) return 0;
        var amount = Math.Min(h.Tribute, payer.SilverA7m);
        if (amount <= 0 || !world.Guilds.SpendAccount(payer, amount, $"the tribute of its hideout in {h.Cluster}")) return 0;
        world.Guilds.Deposit(owner, amount);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{h}: tribute {amount / Pricing.A7m} silver to '{owner.Name}'"));
        return amount;
    }

    /// <summary>
    /// HideoutFillNutrition (397 {0 the hideout's management, 1 an item stack, 2 the count}): food items (items.xml nutrition &gt; 0, the client's
    /// test ckj.af_5) of a player inside into the store, as many as the level's storage takes (the client refuses a full one itself). The items it
    /// takes, or why not.
    /// </summary>
    public (Hideout? Hideout, ItemTemplate? Item, int Count, string Why) CheckFood(Player p, long target, long item, short count)
    {
        if (OfObject(p, target) is not { } h || p.Map?.Objects.GetValueOrDefault(target) is not HideoutManagement) return (null, null, 0, $"object {target} is no hideout management");
        if (!h.Built) return (null, null, 0, "the hideout is not built");
        if (p.Inventory.Bag.Find(item) is not { } stack) return (null, null, 0, $"item {item} is not in the inventory");
        var per = Nutrition(stack.Template);
        if (per <= 0) return (null, null, 0, $"{stack.Template.Name} is no food (HIDEOUT_MANAGEMENT_FOOD_ERROR_ITEM)");
        var max = (Data.Level(h.Level) ?? Data.Levels[0]).MaxNutrition;
        if (h.Nutrition >= max) return (null, null, 0, "the food storage is full (HIDEOUT_MANAGEMENT_FOOD_ERROR_STORAGE)");
        var fits = (int)Math.Ceiling((max - h.Nutrition) / per);
        var n = Math.Min(Math.Min(Math.Max((int)count, 1), stack.Count), fits);
        return (h, stack.Template, n, "");
    }

    /// <summary>The nutrition of one <paramref name="item"/> (items.xml nutrition; 0 = no food).</summary>
    public static double Nutrition(ItemTemplate item) =>
        double.TryParse(item.Attrs.GetValueOrDefault("nutrition"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Puts <paramref name="count"/> × <paramref name="item"/>'s nutrition into <paramref name="h"/> (capped at the level's storage), saved, 425 inside.</summary>
    public void Feed(Hideout h, ItemTemplate item, int count)
    {
        var max = (Data.Level(h.Level) ?? Data.Levels[0]).MaxNutrition;
        h.Nutrition = Math.Min(max, h.Nutrition + count * Nutrition(item));
        store?.SaveHideout(h);
        Refresh(h);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{h}: fed {count} x {item.Name}, food {h.Nutrition:0}/{max}"));
    }

    /// <summary>
    /// UpgradeHideout (394 {0 the hideout's management, 1 the client's price}): the checks of bcv.UpgradeResult (the owner guild, Manage Hideouts,
    /// not upgrading, not at the last level, the next level's silver and resources in the member's purse and inventory) and the cost; the caller
    /// takes it and <see cref="StartUpgrade"/>. The price the client shows is logged when it differs: the data's is taken [H].
    /// </summary>
    public (HideoutUpgradeResult Result, Hideout? Hideout, HideoutLevel? Next, List<(ItemTemplate Item, int Count)> Cost, string Why) CheckUpgrade(Player p, long target, long price)
    {
        List<(ItemTemplate, int)> cost = [];
        if (OfObject(p, target) is not { } h) return (HideoutUpgradeResult.InternalServerError, null, null, cost, $"object {target} is no hideout object");
        if (world.Guilds.Of(p.Character.Id) is not { } g || g.Id != h.Guild) return (HideoutUpgradeResult.NotInOwnersGuild, h, null, cost, "not in the owner guild");
        if (!Manages(g, p.Character.Id)) return (HideoutUpgradeResult.InvalidGuildPermission, h, null, cost, "the role lacks Manage Hideouts");
        if (!h.Built) return (HideoutUpgradeResult.InternalServerError, h, null, cost, "the hideout is not built");
        if (h.Upgrading) return (HideoutUpgradeResult.AlreadyUpgrading, h, null, cost, "an upgrade runs already");
        if (Data.Level(h.Level + 1) is not { } next) return (HideoutUpgradeResult.AlreadyAtMaxLevel, h, null, cost, "the hideout is at its last level");
        var silver = next.UpgradeSilver * Pricing.A7m;
        if (price != silver) Log.Info("world", $"'{p.Character.Name}' upgrade of {h}: the client's price {price / Pricing.A7m}, the data's {next.UpgradeSilver} taken");
        if (p.Character.Silver < silver) return (HideoutUpgradeResult.NotEnoughSilver, h, next, cost, $"{p.Character.Silver / Pricing.A7m} silver, the upgrade costs {next.UpgradeSilver}");
        foreach (var (name, count) in next.UpgradeResources)
        {
            if (world.Items.Find(name) is not { } item) return (HideoutUpgradeResult.InternalServerError, h, next, cost, $"{name} is not in items.xml");
            var have = p.Inventory.Bag.Items.Where(i => i.Template == item).Sum(i => i.Count);
            if (have < count) return (HideoutUpgradeResult.NotEnoughResources, h, next, cost, $"{count} x {name} needed, the inventory has {have}");
            cost.Add((item, count));
        }
        return (HideoutUpgradeResult.Ok, h, next, cost, "");
    }

    /// <summary>The upgrade of <paramref name="h"/> begins now (the cost taken by the caller): saved, the players inside stay until it is done, the guild's mail.</summary>
    public void StartUpgrade(Hideout h, HideoutLevel next, Player by)
    {
        h.UpgradeStarted = world.GameNow;
        store?.SaveHideout(h);
        Refresh(h);
        Mail(h, "Hideout upgrade started", string.Create(CultureInfo.InvariantCulture,
            $"{by.Character.Name} started the upgrade of the guild's hideout in {ZoneName(h.Cluster)} to level {next.Index + 1}; it takes {next.UpgradeSeconds / 3600:0.#} h."));
        Log.Info("world", $"'{by.Character.Name}' upgrades {h} to level {next.Index} ({next.UpgradeSeconds} s)");
    }

    /// <summary>HideoutSetTribute (400 {0 the hideout's object, 1 a7m}): the daily tribute, by the owner guild's managers (CHANGE_TRIBUTE: owner, coowner). RC 0 or 16000.</summary>
    public short SetTribute(Player p, long target, long a7m)
    {
        if (OfObject(p, target) is not { } h || world.Guilds.Of(p.Character.Id) is not { } g || g.Id != h.Guild || !Manages(g, p.Character.Id) || a7m < 0)
            return TributeNoPermission;
        h.Tribute = a7m;
        store?.SaveHideout(h);
        Refresh(h);
        Log.Info("world", $"'{p.Character.Name}' sets the tribute of {h} to {a7m / Pricing.A7m} silver a day");
        return 0;
    }

    /// <summary>GM .hideout day: the daily upkeep and tribute of <paramref name="h"/> now. Returns the state after it.</summary>
    public string Day(Hideout h)
    {
        if (!h.Built) return $"{h}: not built";
        Daily(h, Utc);
        h.LastUpkeep = world.GameNow;
        store?.SaveHideout(h);
        Refresh(h);
        return string.Create(CultureInfo.InvariantCulture, $"{h}: food {h.Nutrition:0}, shields {h.Shields}");
    }
}
