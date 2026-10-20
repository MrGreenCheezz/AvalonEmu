using System.Numerics;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.5, bots-entity SPEC §2.8): the character and gear of a new bot. The look: female with
/// Bots:FemaleShare; Bots:Look Presets (one of characters.xml's 22 AvatarToCustomizationPresets, known-good combinations, with its
/// avatar) or Random (a free mesh and a colour per customization item, an avatar of content/bots.json Avatars); without the client's
/// characters.xml the default look. The gear by role, every pattern of content/bots.json resolved at the bot's tier or else the next
/// lower tier items.xml has (no item above T8 exists): a city bot wears a set of one material (SET1-3, SET1 below T4), a cape and half the
/// time a weapon; a patrol the same with a weapon always (an offhand for a one-handed one) and a horse (an armoured one from T5 with
/// ArmoredShare); a gatherer its resource's tool, the matching gatherer set from T4 (LEATHER_SET1 below), a bag and an ox or a horse.
/// Quality 1, full durability, no enchantment, no passives (<see cref="Network.WorldSession.NewBotItem"/>).
/// </summary>
public sealed class BotFactory(World world)
{
    private BotSettings Settings => world.BotSettings;

    private BotOptions Options => world.Config.Bots;

    /// <summary>
    /// The transient character of a bot named <paramref name="name"/> at <paramref name="at"/> on <paramref name="cluster"/>, its avatar and
    /// the gear of <paramref name="role"/> at <paramref name="tier"/> (the gatherer's tool by <paramref name="resource"/>, ORE when null), flagged
    /// <paramref name="flag"/> (null: Red for a patrol, Blue for the others).
    /// </summary>
    public (Character Character, byte Avatar, List<ItemTemplate> Gear) Make(BotRole role, int tier, string cluster, string name, Vector2 at, float facing,
        Random random, string? resource = null, byte? flag = null)
    {
        var looks = world.GameData?.Looks ?? CharacterLooks.None;
        var template = random.NextDouble() < Options.FemaleShare ? 1 : 0;
        int[] custom = [], clothes = [];
        byte avatar = 0;
        if (Options.Look == BotLook.Presets && looks.Presets.Where(p => p.Template == template).ToList() is { Count: > 0 } presets)
        {
            var p = presets[random.Next(presets.Count)];
            (custom, clothes, avatar) = ([.. p.Customization], [.. p.Clothes], p.Avatar);
        }
        else if (looks.Items.GetValueOrDefault(template) is { } items)
        {
            custom = [.. items.Select(i => i.FreeMeshes.Length == 0 ? 0 : i.FreeMeshes[random.Next(i.FreeMeshes.Length)])];
            clothes = [.. items.Select(i => i.Colors == 0 ? 0 : random.Next(i.Colors))];
            avatar = (byte)Settings.Avatars[random.Next(Settings.Avatars.Count)];
        }
        var c = new Character
        {
            Name = name, Account = BotManager.Account, Transient = true, Template = template, Customization = custom, Clothes = clothes,
            Cluster = cluster, PositionX = at.X, PositionY = at.Y, Direction = facing,
            Flag = flag ?? (BotManager.Patrols(role) ? Pvp.Red : Pvp.Blue), // a gatherer's by its zone (BotManager, SPEC D4)
        };
        return (c, avatar, Gear(role, tier, random, resource));
    }

    /// <summary>The gear of <paramref name="role"/> at <paramref name="tier"/>, each item resolved and existing.</summary>
    public List<ItemTemplate> Gear(BotRole role, int tier, Random random, string? resource = null)
    {
        var s = Settings;
        var names = new List<string>();
        string Pick(IReadOnlyList<string> list) => list[random.Next(list.Count)];
        void ArmourSet()
        {
            var material = tier < 2 ? "LEATHER" : Pick(s.Materials);
            var set = tier < 4 ? 1 : 1 + random.Next(3);
            foreach (var slot in new[] { "HEAD", "ARMOR", "SHOES" }) names.Add($"T{{t}}_{slot}_{material}_SET{set}");
        }
        void Weapon()
        {
            var weapon = Pick(s.Weapons);
            names.Add($"T{{t}}_{weapon}");
            if (weapon.StartsWith("MAIN_", StringComparison.Ordinal)) names.Add($"T{{t}}_{Pick(s.Offhands)}");
        }
        switch (role)
        {
            case BotRole.City:
                ArmourSet();
                if (random.NextDouble() < s.CityWeaponShare) Weapon();
                names.Add(s.Cape);
                break;
            case BotRole.Patrol:
            case BotRole.Drifter: // stageL mists: a patrol's gear
                ArmourSet();
                Weapon();
                names.Add(s.Cape);
                names.Add(tier >= 5 && random.NextDouble() < s.ArmoredShare ? s.ArmoredMount : s.PatrolMount);
                break;
            case BotRole.Invader:
            case BotRole.Hellgate:
                // StageL corrupted (SPEC §4.5): a patrol's gear at the difficulty's tier, no mount (allowmounts false inside); a hellgate's at its map's.
                ArmourSet();
                Weapon();
                names.Add(s.Cape);
                break;
            case BotRole.Gatherer:
                var kind = (resource ?? "ORE").ToUpperInvariant();
                if (s.Tools.GetValueOrDefault(kind) is { } tool) names.Add(tool);
                foreach (var slot in new[] { "HEAD", "ARMOR", "SHOES" })
                    names.Add(tier >= 4 ? s.GathererSet.Replace("{slot}", slot).Replace("{kind}", kind) : $"T{{t}}_{slot}_LEATHER_SET1");
                names.Add(s.GathererBag);
                names.Add(Pick(s.GathererMounts));
                break;
        }
        var gear = new List<ItemTemplate>();
        foreach (var pattern in names)
            if (Resolve(pattern, tier) is { } item && gear.All(g => g.Slot != item.Slot)) gear.Add(item);
        return gear;
    }

    /// <summary>
    /// The weapon a gatherer of <paramref name="tier"/> carries in its bag (stageM bots): with content/bots.json Gear.GathererWeaponShare one of
    /// Gear.Weapons (no offhand) that items.xml has at or below that tier (MAIN_SWORD from T1, 2H_BOW and MAIN_FIRESTAFF from T2, most
    /// from T3), so the share holds at every tier while any entry resolves; else null.
    /// </summary>
    public ItemTemplate? GathererWeapon(int tier, Random random)
    {
        var s = Settings;
        if (s.GathererWeaponShare <= 0 || random.NextDouble() >= s.GathererWeaponShare || s.Weapons.Count == 0) return null;
        var usable = s.Weapons.Select(w => Resolve($"T{{t}}_{w}", tier)).Where(w => w is { Slot: "mainhand" }).ToList();
        return usable.Count == 0 ? null : usable[random.Next(usable.Count)];
    }

    /// <summary>
    /// What a gatherer of <paramref name="profile"/> carries from its spawn (stageM bots, Gear.GathererStartCarry): (raw resource of its kind
    /// at a tier of its band, amount); null when the range is 0 or no such item exists.
    /// </summary>
    public (string Item, int Amount)? GathererStartCarry(GatherProfile profile, Random random)
    {
        var (min, max) = Settings.GathererStartCarry;
        var amount = min + random.Next(Math.Max(0, max - min) + 1);
        if (amount <= 0) return null;
        var tier = profile.MinTier + random.Next(Math.Max(0, profile.MaxTier - profile.MinTier) + 1);
        for (var t = tier; t >= 1; t--)
            if (world.Items.Find($"T{t}_{profile.Resource}") is { } item) return (item.Name, amount);
        return null;
    }

    /// <summary>
    /// Any item (equipment or not) of <paramref name="pattern"/> with {t} at <paramref name="tier"/>, else at the next lower tier that
    /// items.xml has (a patrol's Rewards.PatrolExtra: POTION_HEAL exists at T2, T4, T6 only); null when none exists.
    /// </summary>
    public ItemTemplate? ResolveAny(string pattern, int tier)
    {
        for (var t = Math.Clamp(tier, 1, 8); t >= 1; t--)
        {
            if (world.Items.Find(pattern.Replace("{t}", t.ToString(System.Globalization.CultureInfo.InvariantCulture))) is { } item) return item;
            if (!pattern.Contains("{t}", StringComparison.Ordinal)) break;
        }
        return null;
    }

    /// <summary>
    /// StageN botpower: an elite's <paramref name="item"/> at enchantment <paramref name="level"/> ("&lt;name&gt;@&lt;level&gt;"), else the
    /// highest lower level items.xml has, for its weapon, offhand and armour (mainhand, offhand, head, armor, shoes); any other item as it is.
    /// </summary>
    public ItemTemplate Enchanted(ItemTemplate item, int level)
    {
        if (item.Slot is not ("mainhand" or "offhand" or "head" or "armor" or "shoes") || item.Enchantment > 0) return item;
        for (var l = Math.Min(level, 4); l >= 1; l--)
            if (world.Items.Find($"{item.Name}@{l}") is { IsEquipment: true } e && e.Slot == item.Slot) return e;
        return item;
    }

    /// <summary>
    /// <paramref name="pattern"/> with {t} at <paramref name="tier"/>, else at the next lower tier that items.xml has; an equipment item
    /// with a slot of the equipment (a pattern without {t} as it is); null when none exists.
    /// </summary>
    public ItemTemplate? Resolve(string pattern, int tier)
    {
        for (var t = Math.Clamp(tier, 1, 8); t >= 1; t--)
        {
            if (world.Items.Find(pattern.Replace("{t}", t.ToString(System.Globalization.CultureInfo.InvariantCulture))) is { IsEquipment: true } item
                && Array.IndexOf(Inventory.SlotNames, item.Slot) >= 0)
                return item;
            if (!pattern.Contains("{t}", StringComparison.Ordinal)) break;
        }
        return null;
    }

    /// <summary>
    /// The gear tier of a bot of <paramref name="role"/> on a map of zone class <paramref name="zone"/> and tier <paramref name="mapTier"/>
    /// (SPEC §1.5): a royal city's by the CityTiers weights, a start town's 1-2, the open world's the map's tier −1, 0 or +1 (a tier 0 map counts
    /// as 4), within 1-8.
    /// </summary>
    public int TierFor(string zone, int mapTier, Random random)
    {
        if (zone == "City")
        {
            var weights = Settings.CityTiers.Where(w => w.Value > 0).ToList();
            var roll = random.Next(weights.Sum(w => w.Value));
            foreach (var (tier, weight) in weights)
                if ((roll -= weight) < 0) return Math.Clamp(tier, 1, 8);
            return 4;
        }
        if (zone == "Start") return 1 + random.Next(2);
        if (zone == "Mists" && mapTier > 0) return Math.Clamp(mapTier, 1, 8); // stageL mists: a Drifter of the zone's tier
        return Math.Clamp((mapTier == 0 ? 4 : mapTier) + random.Next(-1, 2), 1, 8);
    }
}
