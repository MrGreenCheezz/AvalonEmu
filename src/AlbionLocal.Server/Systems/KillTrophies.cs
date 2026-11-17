using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageR kill trophies (analysis/work/stageR/kill-vanity/SPEC.md §2). A lethal player kill (<see cref="Death.Kill"/>) in a cluster whose
/// worldsettings.xml archetype names a killtrophy table (KILLTROPHY_BLACK: the outlands and roads; KILLTROPHY_HELL: corrupted dungeons and
/// hellgates) may give the killer the table's trophy: the tiers are rolled from the highest down, each one whose minimumitemvalue the victim's
/// equipment reaches with the chance the file's own comment gives, Σ itemvalue / itemvaluequotient × (1 + assists × 0.2), divided by the
/// archetype's killtrophydenominator; the first hit wins. The trophy (UNIQUE_FURNITUREITEM_KILLTROPHY_*, a killtrophy item) goes into the
/// killer's inventory (NewKillTrophyItem 34), else by mail (Mail:RewardOverflow), else it is lost (logged). [infer: whose item value (the
/// victim's equipment, as the kill fame) and who gets it (the killer) are not shown by the client; the comment names the formula.]
/// Not done (SPEC "Not done"): the kill details the trophy keeps (GetItemKillHistory 466 / ItemKillHistoryDetails 149), placing it as an
/// outdoor furniture building (NewKillTrophyFurnitureBuilding 621), ImmortalizeKillTrophy 528, and its expiretimedays (3). Under World.Sync.
/// </summary>
public sealed class KillTrophies(World world)
{
    /// <summary>The rolls' random (a test sets its own).</summary>
    public Random Random { get; set; } = new();

    /// <summary>The victim's equipment item value (the kill fame's base, <see cref="Death.KillFame"/>).</summary>
    public double ItemValue(Player victim) => victim.Inventory.Equipment.Items.Sum(i => world.ItemValues.Of(i.Template));

    /// <summary>The trophy of one roll: the table's tiers from the highest down; null = none.</summary>
    public string? Roll(KillTrophyTable table, double itemValue, int assists, int denominator)
    {
        for (var i = table.Tiers.Count - 1; i >= 0; i--)
        {
            var tier = table.Tiers[i];
            if (itemValue < tier.MinimumItemValue) continue;
            var chance = itemValue / tier.ItemValueQuotient * (1 + Math.Max(0, assists) * 0.2) / Math.Max(1, denominator);
            if (Random.NextDouble() < chance) return tier.Item;
        }
        return null;
    }

    /// <summary><paramref name="killer"/> killed <paramref name="victim"/> with <paramref name="attackers"/> attackers (itself among them): the roll and the trophy.</summary>
    public string? Killed(Player killer, Player victim, int attackers)
    {
        if (!world.Config.Vanity.KillTrophies || killer.IsBot || victim.Map is not { } map) return null;
        var rules = Pvp.RulesOf(world, map);
        if (rules.KillTrophy is not { } id || world.Vanity.KillEmotes.Data.Trophies.GetValueOrDefault(id) is not { } table) return null;
        var value = ItemValue(victim);
        if (Roll(table, value, Math.Max(0, attackers - 1), rules.KillTrophyDenominator) is not { } name) return null;
        Log.Info("pvp", string.Create(CultureInfo.InvariantCulture, $"kill trophy roll for '{killer.Character.Name}' ({id}, item value {value:0}): {name}"));
        return Give(killer, name, victim.Character.Name);
    }

    /// <summary>The trophy <paramref name="name"/> for <paramref name="killer"/>: into the inventory (NewKillTrophyItem), else by mail, else lost. Its name, or null.</summary>
    public string? Give(Player killer, string name, string victimName)
    {
        if (world.Items.Find(name) is not { CanCreate: true } type)
        {
            Log.Warn("pvp", $"kill trophy {name} is not an item this server can create");
            return null;
        }
        if (Network.WorldSession.Fits(killer.Inventory.Bag, [(type, 1)]))
        {
            killer.Session.GiveItem(killer, type, 1);
            killer.SaveItems();
        }
        else if (!Mailer.Overflow(world, killer.Character, [new InventoryItem(type.Name, 1)], $"the kill of {victimName}"))
        {
            Log.Warn("pvp", $"'{killer.Character.Name}': kill trophy {type.Name} does not fit the inventory: lost");
            return null;
        }
        Expeditions.Tell(killer, $"Kill trophy: {type.Name} for the kill of {victimName}.");
        Log.Info("pvp", $"'{killer.Character.Name}' got kill trophy {type.Name} for '{victimName}'");
        return type.Name;
    }
}
