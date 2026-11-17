using System.Globalization;
using System.Runtime.CompilerServices;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Studying an item at a crafting station (op 55 ACT 4 StudyItem; analysis/work/stageR/items/SPEC.md §1): the item is
/// destroyed for crafting fame. The fame is the client's own preview (cmm.al 0x180B71CB0, the shared descriptor maths):
/// famevalue (bdi+0x110) × destinyandjournalcraftfamefactor (bdi+0x108) × count × gamedata StudyFame factor (2.75,
/// bgf+0x228), then × Rates:Fame × Rates:CraftingFame. It goes to the item's craftitemfame nodes as a craft's fame
/// (<see cref="Destiny.Craft"/>); premium is added there as for every key-5 source. The client's focus share
/// (ActionFocus StudyingEfficiency) and its server-sent crafting bonuses (dev, 0 here) are not given (SPEC "Not done").
/// </summary>
public static class Study
{
    private static readonly ConditionalWeakTable<ItemStore, Dictionary<string, double>> Caches = new();

    /// <summary>
    /// The client's famevalue of <paramref name="item"/> (bdi.b(bdh, HashSet) 0x1805F1B40): the famevalue attribute when the
    /// element has one (resources: T4_WOOD 7.5), else the first recipe's resources' famevalues (each at its enchantment level)
    /// × their counts, divided by amountcrafted when that is not 1; 0 without either (a cycle counts 0, the client throws).
    /// </summary>
    public static double FameValue(ItemStore items, ItemTemplate item)
    {
        var cache = Caches.GetOrCreateValue(items);
        lock (cache) return Value(items, cache, item, []);
    }

    private static double Value(ItemStore items, Dictionary<string, double> cache, ItemTemplate item, HashSet<string> path)
    {
        if (cache.TryGetValue(item.Name, out var cached)) return cached;
        if (item.Attrs.TryGetValue("famevalue", out var own)
            && double.TryParse(own, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return cache[item.Name] = v;
        if (item.Recipes is not [var recipe, ..] || !path.Add(item.Name)) return 0;
        var sum = 0.0;
        foreach (var r in recipe.Resources)
            if (((r.Enchantment > 0 ? items.Find($"{r.Name}@{r.Enchantment}") : null) ?? items.Find(r.Name)) is { } resource)
                sum += Value(items, cache, resource, path) * r.Count;
        path.Remove(item.Name);
        if (recipe.AmountCrafted > 1) sum /= recipe.AmountCrafted;
        return cache[item.Name] = sum;
    }

    /// <summary>bdi+0x118: false for dontgivefameoncraft items (raw resources, …), which never give study fame.</summary>
    public static bool GivesFame(ItemTemplate item) => item.Attrs.GetValueOrDefault("dontgivefameoncraft") != "true";

    /// <summary>bdi+0x108, destinyandjournalcraftfamefactor (default 1).</summary>
    public static double DestinyFactor(ItemTemplate item) =>
        double.TryParse(item.Attrs.GetValueOrDefault("destinyandjournalcraftfamefactor"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0 ? f : 1;

    /// <summary>The study fame (a7m raw, before the rates) of <paramref name="count"/> items of <paramref name="item"/>; truncated as a7m.</summary>
    public static long Fame(ItemStore items, ItemTemplate item, int count, EconomySettings economy)
    {
        if (!GivesFame(item) || count <= 0) return 0;
        var fame = (decimal)FameValue(items, item) * (decimal)DestinyFactor(item) * count * (decimal)economy.StudyFameFactor;
        return fame <= 0 ? 0 : (long)Math.Floor(fame * Pricing.A7m);
    }

    /// <summary>
    /// Whether studying <paramref name="item"/> feeds a node of <paramref name="p"/>'s board now (the client's cs6.k, error 14
    /// ItemWontAddFameToAchievement): an in-progress craftitemfame node of the item (fameearned alone does not count [infer]).
    /// A character without a board of its own (everything unlocked) may study anything: the total fame still grows.
    /// </summary>
    public static bool FeedsBoard(Player p, ItemTemplate item)
    {
        if (p.Character.Progression is not { } progression || p.World.GameData?.Board is not { } board) return true;
        return board.Targets(progression, Destiny.Craft(item)).Any(id => board.Nodes[id].Mission == "craftitemfame");
    }
}
