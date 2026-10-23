using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>Checks that authored objectives have world sources, rather than only evaluable predicates.
/// This is a content dependency audit, not a replacement for operation-level or visual playthroughs.</summary>
public static class TutorialObtainabilityTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("tutorial obtainability: harvesting, skinning, tools and recipes reach every required item", Items),
        ("tutorial obtainability: combat targets respawn on the required island maps", Combat),
        ("tutorial obtainability: actual island mob fame unlocks START and COMBAT_TRAINEE", Progression),
        ("tutorial obtainability: authored silver funds mule and all equipment alternatives", Budget),
    ];
    private static void Check(bool value, string message) => AlbionTests.Check(value, message);
    private static TutorialJourneyData Load() => TutorialJourneyData.Load(ClientGameDataDir(), Client.Clusters!);
    private static IEnumerable<MobTemplate> Mobs(ClusterTemplate map) => map.MobSpawnPoints
        .Concat(map.Resources?.MobSpawnPoints ?? []).SelectMany(point => point.Template is { } mob
            ? new[] { mob } : point.Block?.Mobs.Where(e => e.Weight > 0 && !e.OnlyAsUpgrade && e.Mob is not null)
                .Select(e => e.Mob!) ?? []).DistinctBy(m => m.Name);
    private static IEnumerable<(HarvestableTemplate Type, HarvestableTier Tier)> Sources(TutorialJourneyData data)
    {
        foreach (var map in data.Maps.Values)
        {
            foreach (var node in map.Harvestables.Concat(map.Resources?.Harvestables ?? []))
                yield return (node.Type, node.Tier);
            foreach (var mob in Mobs(map))
                foreach (var entry in mob.Loot?.Elements("Harvestable") ?? [])
                    if (Client.Clusters!.Harvestables.Find((string?)entry.Attribute("type") ?? "") is { } type
                        && int.TryParse((string?)entry.Attribute("tier"), out var tier)
                        && type.Tiers.TryGetValue(tier, out var value)) yield return (type, value);
        }
    }
    private static readonly string[] Stock = ["T2_MOUNT_MULE", "T2_OFF_SHIELD", "T2_OFF_BOOK",
        "T2_HEAD_PLATE_SET1", "T2_ARMOR_PLATE_SET1", "T2_SHOES_PLATE_SET1",
        "T2_HEAD_LEATHER_SET1", "T2_ARMOR_LEATHER_SET1", "T2_SHOES_LEATHER_SET1",
        "T2_HEAD_CLOTH_SET1", "T2_ARMOR_CLOTH_SET1", "T2_SHOES_CLOTH_SET1"];
    private static void Items()
    {
        var data = Load();
        var available = new HashSet<string>(StringComparer.Ordinal);
        // Guaranteed prerequisite quest rewards; no GM grants, inventory seeding, or hypothetical town market.
        foreach (var quest in GameDataFile.Open(ClientGameDataDir(), "quests.bin").Root().Elements()
            .Where(q => new[] { "A", "B", "C", "D", "I", "L" }.Any(s => (string?)q.Attribute("uniquename") == TutorialJourneyData.Prefix + s)))
            foreach (var reward in quest.Element("rewards")?.Elements("item") ?? [])
                available.Add((string)reward.Attribute("itemid")!);
        available.UnionWith(Stock);
        var sources = Sources(data).ToArray();
        var stations = data.Maps.Values.SelectMany(m => m.Buildings).Select(b => b.Template).DistinctBy(b => b.Name).ToArray();
        bool changed;
        do
        {
            changed = false;
            var tools = available.Select(Client.Items.Find).OfType<ItemTemplate>().Where(t => t.CanHarvest.Count != 0).ToArray();
            foreach (var (type, tier) in sources)
            {
                if (tier.Item is not { } item || tier.Charges <= 0 || !tier.Levels.Any(l => l.Yield > 0)) continue;
                if (!tier.RequiresTool || tools.Any(tool => tool.CanHarvest.Contains(type.Resource ?? "")
                    && type.ToolTimeFactors.ContainsKey(tool.Tier - tier.Tier))) changed |= available.Add(item);
            }
            foreach (var name in stations.SelectMany(s => s.CraftItems).Distinct())
                if (Client.Items.Find(name) is { CanCreate: true } item
                    && item.Recipes.Any(r => r.Gold == 0 && r.Unsupported.Count == 0
                        && r.Resources.All(resource => available.Contains(resource.Name)))) changed |= available.Add(name);
        } while (changed);
        var needed = data.Quests.SelectMany(q => q.Definition.Descendants())
            .Where(e => e.Name.LocalName is "acquireitem" or "equipitem" or "mount" or "gather" or "item")
            .SelectMany(e => new[] { (string?)e.Attribute("itemid"), (string?)e.Attribute("id") })
            .OfType<string>().Distinct().Where(name => !available.Contains(name)).ToArray();
        Check(needed.Length == 0, "required items without an island harvesting/corpse/recipe/reward/market source: " + string.Join(", ", needed));
        foreach (var item in new[] { "T2_WOOD", "T2_HIDE", "T2_ORE", "T2_FIBER", "T2_PLANKS", "T2_METALBAR", "T2_CLOTH", "T2_LEATHER",
            "T1_2H_TOOL_AXE", "T1_2H_TOOL_PICK", "T2_2H_BOW", "T2_MAIN_SWORD", "T2_MAIN_FIRESTAFF" })
            Check(available.Contains(item), $"critical supply chain cannot produce {item}");
        Check(sources.Any(s => s.Tier.Item == "T2_HIDE"), "a real skinnable or static hide source exists; quest marker alone cannot supply leather");
        foreach (var name in new[] { "T1_2H_TOOL_AXE", "T1_2H_TOOL_PICK", "T2_PLANKS", "T2_METALBAR", "T2_CLOTH", "T2_LEATHER",
            "T2_2H_BOW", "T2_MAIN_SWORD", "T2_MAIN_FIRESTAFF" })
            Check(Client.Items.Find(name)!.Recipes.Any(r => r.Silver == 0 && r.Gold == 0 && r.Unsupported.Count == 0),
                $"{name} needs an additional currency budget before the 300-silver equipment reward");
    }
    private static void Combat()
    {
        var data = Load();
        foreach (var mapId in new[] { "1008", "1009", "1010" })
        {
            var targets = Mobs(data.Maps[mapId]).Where(m => m.Faction.Equals("HERETIC", StringComparison.OrdinalIgnoreCase)
                && m.Tier <= 2 && m.HitPointsMax > 0).ToArray();
            Check(targets.Length > 0, $"{mapId} has no bound, damageable HERETIC targets for its combat objectives");
            Check(targets.Any(m => m.Fame > 0), $"{mapId} cannot advance START/COMBAT_TRAINEE through actual combat fame");
            Check(targets.Any(m => m.RespawnSecondsMax > 0), $"{mapId} has no respawning target to replenish combat fame");
            if (mapId == "1010") Check(targets.Any(m => m.Tier == 2), "the fort needs tier-two HERETICs for P's ten kills");
        }
    }
    private static void Budget()
    {
        var data = Load();
        long Silver(string suffix) => (long?)data.Find(TutorialJourneyData.Prefix + suffix)!.Definition
            .Element("rewards")?.Element("silver")?.Attribute("absolute") ?? 0;
        Check(Silver("F") * 10000 >= TutorialMerchant.LaboratoryMulePrice, "captain's first reward must pay for the mule");
        foreach (var suffix in new[] { "MA", "MB", "MC" })
            Check(Silver(suffix) * 10000 >= TutorialMerchant.LaboratoryEquipmentPrice * 4,
                $"{suffix}'s 300 silver must cover armor, head, shoes AND an offhand without kill silver or a money cheat");
        Check(Stock.All(name => Client.Items.Find(name) is { CanCreate: true }), "all required local merchant items can be created by the protocol");
    }
    private static void Progression()
    {
        var data = Load();
        var board = Client.GameData!.Board!;
        var progress = board.FairStart();
        var gear = new[] { "T1_MAIN_SWORD", "T1_ARMOR_LEATHER_SET1", "T1_SHOES_LEATHER_SET1" }
            .Select(name => Client.Items.Find(name)!).Select(item => (item.Name, item.Tier)).ToArray();
        foreach (var (mapId, achievement) in new[] { ("1008", "START"), ("1009", "COMBAT_TRAINEE") })
        {
            var target = Mobs(data.Maps[mapId]).Where(m => m.Faction.Equals("HERETIC", StringComparison.OrdinalIgnoreCase)
                && m.Tier <= 2 && m.Fame > 0).OrderByDescending(m => m.Fame / Math.Max(1, m.MinDivisor)).First();
            var amount = (long)(target.Fame / Math.Max(1, target.MinDivisor) * DestinyBoard.A7m);
            var id = board.Find(achievement)!.Id;
            var count = 0;
            while (!progress.Completed.Contains(id) && count++ < 1000)
                board.Apply(progress, amount, new FameContext(FameSource.Kill, target.Tier, gear));
            Check(progress.Completed.Contains(id), $"{achievement} cannot unlock from actual {target.Name} fame/tier in {mapId}; no manual completion allowed");
        }
        foreach (var name in new[] { "T2_2H_BOW", "T2_MAIN_SWORD", "T2_MAIN_FIRESTAFF", "T2_HEAD_CLOTH_SET1",
            "T2_ARMOR_CLOTH_SET1", "T2_SHOES_CLOTH_SET1", "T2_OFF_SHIELD", "T2_OFF_BOOK" })
            Check(board.CanEquip(progress, Client.Items.Find(name)!), $"tutorial combat progression still cannot equip required {name}");
    }
}
