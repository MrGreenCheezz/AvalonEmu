using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Crafting focus (analysis/work/stageJ/focus-lp/SPEC.md §2.1, §2.4, §5.2-§5.4): the bar of <see cref="Character.CraftingFocus"/> as of
/// <see cref="Character.FocusStamp"/>, regenerating while premium (premiumcraftingfocusregenerationperday 10000 a day, Focus:RegenPerDay;
/// offline time counts [inference]) up to its cap (craftingfocusmax 30000, Focus:Max), worked out lazily (<see cref="Current"/>) and
/// settled whenever it changes (<see cref="Settle"/>). Crafting spends it (<see cref="Cost"/>, <see cref="Spend"/>: the client's bgf.abo_37 and
/// bgf.abo_t2cdb, shared code), focus potions and nothing else add to it. The client gets the bar from the Join (keys 27, 28, 30, 31),
/// RegenerationCraftingChanged (94) after a premium change, and CraftingFocusUpdate (10) after each change. Under <see cref="World.Sync"/>.
/// </summary>
public static class Focus
{
    private const double TicksPerSecond = TimeSpan.TicksPerSecond;

    private static CharacterDefaults Defaults(World world) => world.GameData?.Defaults ?? CharacterDefaults.Default;

    /// <summary>
    /// The focus and learning points a new character starts with (Focus:Start, LearningPoints:Start; the pve preset gives the premium
    /// lifetime reward's 30000 and 500, gamedata &lt;PremiumLifeTimeRewards&gt;). Both stamps stay 0: the first Join starts the clocks.
    /// </summary>
    public static void NewCharacter(Character c, ServerConfig config)
    {
        c.CraftingFocus = config.FocusStart;
        c.LearningPoints = (long)Math.Round(config.LearningPointsStart * 10000);
    }

    /// <summary>The focus cap of <paramref name="p"/> (Join key 28): Focus:Max, else characters.xml (premium)craftingfocusmax, 30000.</summary>
    public static double Max(Player p) => Max(p.World, Premium.Is(p));

    private static double Max(World world, bool premium) =>
        world.Config.FocusMax ?? (premium ? Defaults(world).FocusMaxPremium : Defaults(world).FocusMax);

    /// <summary>
    /// The focus regained per second (Join key 30, 94 key 5): while premium Focus:RegenPerDay, else premiumcraftingfocusregenerationperday
    /// (10000), / 86400 = 0.11574074; without premium craftingfocusregenerationperday (0).
    /// </summary>
    public static double RegenPerSecond(Player p) => RegenPerSecond(p.World, Premium.Is(p));

    private static double RegenPerSecond(World world, bool premium) =>
        (premium ? world.Config.FocusRegenPerDay ?? Defaults(world).FocusRegenPerDayPremium : Defaults(world).FocusRegenPerDay) / 86400.0;

    /// <summary>
    /// The focus at GameTimeStamp <paramref name="now"/>: the stored value plus the regeneration since its stamp, at most <see cref="Max"/>
    /// (a value above the cap, which a GM may set, does not go down); a stamp of 0 regenerates nothing.
    /// </summary>
    public static double Current(Player p, long now) => Current(p.World, p.Character, now, p.PremiumOverride);

    internal static double Current(World world, Character c, long now, bool? sessionOverride)
    {
        if (c.FocusStamp == 0 || now <= c.FocusStamp) return c.CraftingFocus;
        var focus = c.CraftingFocus;
        foreach (var (start, end, premium) in Premium.AccrualPeriods(world, c, c.FocusStamp, now, sessionOverride))
        {
            var regen = RegenPerSecond(world, premium);
            if (regen > 0) focus = Math.Max(focus, Math.Min(Max(world, premium), focus + (end - start) / TicksPerSecond * regen));
        }
        return focus;
    }

    /// <summary>The regeneration so far into the stored value, the stamp at <paramref name="now"/> (before any change of the value or of the regeneration).</summary>
    public static void Settle(Player p, long now)
    {
        var c = p.Character;
        c.CraftingFocus = Current(p, now);
        c.FocusStamp = now;
        p.World.Saves.MarkDirty(c); // a spend or a gain after it marks the change itself
    }

    /// <summary>The Join keys of the focus bar: 27 the focus, 28 its cap, 30 the regeneration per second (only while it regenerates), 31 the stamp (settled now).</summary>
    public static void JoinKeys(Player p, JoinResponse r, long now)
    {
        Settle(p, now);
        r.a81 = (float)p.Character.CraftingFocus; // [27] (0 = left out; the client then reads 0)
        r.a82 = (float)Max(p);                    // [28] (captured 30000)
        r.a84 = (float)RegenPerSecond(p);         // [30] (0 = left out: no regeneration, as captured without premium)
        r.a85 = now;                              // [31] GameTimeStamp
    }

    /// <summary>
    /// <paramref name="amount"/> focus to <paramref name="p"/> (a focus potion; settled first), at most <see cref="Max"/>, and its
    /// CraftingFocusUpdate {2 +amount as given, 3 the new focus}.
    /// </summary>
    public static void Add(Player p, double amount, long now)
    {
        Settle(p, now);
        var c = p.Character;
        c.CraftingFocus = Math.Max(c.CraftingFocus, Math.Min(Max(p), c.CraftingFocus + Math.Max(0, amount)));
        p.World.Saves.MarkChanged(c);
        Send(p, now, amount);
    }

    /// <summary>
    /// Takes up to <paramref name="cost"/> focus (settled first; partial allowed, SPEC §2.4): what was used and the share of the cost it
    /// covers (1 = all, 0 = none), as the client's preview (fraction = cur / cost when less than the cost).
    /// </summary>
    public static (double Used, double Fraction) Spend(Player p, double cost, long now)
    {
        Settle(p, now);
        var c = p.Character;
        if (cost <= 0 || c.CraftingFocus <= 0) return (0, 0);
        var used = Math.Min(cost, c.CraftingFocus);
        c.CraftingFocus -= used;
        p.World.Saves.MarkChanged(c);
        return (used, c.CraftingFocus + used >= cost ? 1 : used / cost);
    }

    /// <summary>
    /// Gives back <paramref name="amount"/> focus (a cancelled batch; settled first) with its CraftingFocusUpdate; all of it, even over the
    /// cap when the bar regenerated meanwhile (it was taken from under the cap; such a value does not go down, as a GM's).
    /// </summary>
    public static void Refund(Player p, double amount, long now)
    {
        if (amount <= 0) return;
        Settle(p, now);
        var c = p.Character;
        c.CraftingFocus += amount;
        p.World.Saves.MarkChanged(c);
        Send(p, now, amount);
    }

    /// <summary>CraftingFocusUpdate (10) {0 player, 1 now, 2 <paramref name="delta"/> (left out when 0), 3 the focus left}.</summary>
    public static void Send(Player p, long now, double delta) =>
        p.Session.Send(new CraftingFocusUpdateEvent { ddn = p.Id, b1a = now, b1b = (float)delta, b1c = (float)p.Character.CraftingFocus });

    /// <summary>RegenerationCraftingChanged (94) {0 player, 1 now, 2 the focus, 3 its cap, 5 the regeneration per second, 6 the stamp} (key 4 left out, as captured).</summary>
    public static RegenerationCraftingChangedEvent Bar(Player p, long now)
    {
        Settle(p, now);
        return new RegenerationCraftingChangedEvent
        {
            ddn = p.Id, dth = now, dti = (float)p.Character.CraftingFocus, dtj = (float)Max(p), dtl = (float)RegenPerSecond(p), dtm = now,
        };
    }

    /// <summary>
    /// The focus <paramref name="crafts"/> crafts of <paramref name="recipe"/> cost (bgf.abo_37 0x1805C9340): max(1, round half to even
    /// (craftingfocus × crafts / <paramref name="constant"/>^<paramref name="reduction"/>)); 0 when the recipe has no craftingfocus or is not
    /// returnable (the client's parser zeroes its craftingfocus then, SPEC §2.4). 4021 at 0 units, 711 at 250, 577 at 280.
    /// </summary>
    public static double Cost(CraftRecipe recipe, int crafts, double reduction, double constant = 1.00695555005672)
    {
        if (recipe.Focus <= 0 || !recipe.Returnable || crafts <= 0) return 0;
        return Math.Max(1, Math.Round(recipe.Focus * crafts / Math.Pow(constant, reduction), MidpointRounding.ToEven));
    }

    /// <summary>
    /// The destiny board's focus cost reduction of <paramref name="p"/> for crafting <paramref name="item"/> (SPEC §1.3, §5.2; a36.q_1 +
    /// the stat CraftingFocusCostReduction): Σ craftingfocuscostreduction bonus × min(level, base levels) over the nodes in progress
    /// whose patterns and tier gate match (as <see cref="StatCalculator.DestinyBonus"/> sums the wear bonus; Q6), plus the buffs'
    /// craftingfocuscostreduction. 100 units halve the cost; a node's own item at level 100 is 250.
    /// </summary>
    public static double Reduction(Player p, ItemTemplate item, long now)
    {
        var sum = 0.0;
        if (p.World.GameData is { } data && p.Progression is { } progression)
            foreach (var (achievement, bonus) in data.FocusBonuses(item.Index))
                if (progression.Levels.TryGetValue(achievement, out var level) && level > 0)
                    sum += bonus * Math.Min(level, data.Achievements[achievement].BaseLevels);
        return sum + p.Effects.Sum("craftingfocuscostreduction", now);
    }

    /// <summary>
    /// The crafting quality bonus of <paramref name="p"/> for <paramref name="item"/> (enchanting SPEC §3): Σ itemcraftquality bonus ×
    /// min(level, base levels) over the destiny board nodes in progress whose patterns and tier gate match (as <see cref="Reduction"/>),
    /// the buffs' craftingquality, and gamedata ActionFocus CraftingQuality (50) × the share of the focus cost covered.
    /// </summary>
    public static double QualityBonus(Player p, ItemTemplate item, long now, double focusShare)
    {
        var sum = 0.0;
        if (p.World.GameData is { } data && p.Progression is { } progression)
            foreach (var (achievement, bonus) in data.QualityBonuses(item.Index))
                if (progression.Levels.TryGetValue(achievement, out var level) && level > 0)
                    sum += bonus * Math.Min(level, data.Achievements[achievement].BaseLevels);
        return sum + p.Effects.Sum("craftingquality", now) + p.World.Economy.FocusQuality * Math.Clamp(focusShare, 0, 1);
    }
}
