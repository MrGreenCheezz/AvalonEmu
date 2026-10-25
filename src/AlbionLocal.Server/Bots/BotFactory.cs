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
        Random random, string? resource = null, byte? flag = null, BotIdentity? identity = null, GearStyle? style = null)
    {
        var looks = world.GameData?.Looks ?? CharacterLooks.None;
        var population = random;
        random = identity?.LookRandom() ?? random; // bot-roster: an identity's look is the same every time
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
        if (identity is not null) c.Id = identity.Id; // bot-roster: the same Guid every time (whispers, invites and friend requests by Guid)
        return (c, avatar, Gear(role, tier, style?.Random ?? population, resource, style));
    }

    /// <summary>
    /// The gear of <paramref name="role"/> at <paramref name="tier"/>, each item resolved and existing. With <paramref name="style"/> (a
    /// population bot, bot-roster gear realism) each armour piece, weapon, offhand and cape may be off its tier (MixedShare, Spread), an
    /// enchanted one (EnchantChance, EnchantLevels) or an artifact (ArtifactChance: weapons and armour, not a gatherer's set); every piece
    /// draws the same numbers whatever comes of them, so one random seed gives one set in every zone.
    /// </summary>
    public List<ItemTemplate> Gear(BotRole role, int tier, Random random, string? resource = null, GearStyle? style = null)
    {
        var s = Settings;
        var names = new List<string>();
        var pieces = new Dictionary<int, string?>(); // by index of names: a piece of the realism rules and its artifact pattern
        string Pick(IReadOnlyList<string> list) => list[random.Next(list.Count)];
        void Piece(string pattern, string? artifact)
        {
            pieces[names.Count] = artifact;
            names.Add(pattern);
        }
        void ArmourSet()
        {
            var material = tier < 2 ? "LEATHER" : Pick(s.Materials);
            var set = tier < 4 ? 1 : 1 + random.Next(3);
            var artifact = style is not null && s.ArtifactSets.Count > 0 ? s.ArtifactSets[random.Next(s.ArtifactSets.Count)] : null;
            foreach (var slot in new[] { "HEAD", "ARMOR", "SHOES" })
                Piece($"T{{t}}_{slot}_{material}_SET{set}", artifact is null ? null : $"T{{t}}_{slot}_{material}_{artifact}");
        }
        void Weapon()
        {
            var weapon = Pick(s.Weapons);
            Piece($"T{{t}}_{weapon}", style is not null && s.ArtifactWeapons.GetValueOrDefault(weapon) is { Count: > 0 } arts ? $"T{{t}}_{arts[random.Next(arts.Count)]}" : null);
            if (weapon.StartsWith("MAIN_", StringComparison.Ordinal)) Piece($"T{{t}}_{Pick(s.Offhands)}", null);
        }
        switch (role)
        {
            case BotRole.City:
                ArmourSet();
                if (random.NextDouble() < s.CityWeaponShare) Weapon();
                Piece(s.Cape, null);
                break;
            case BotRole.Patrol:
            case BotRole.Drifter: // stageL mists: a patrol's gear
                ArmourSet();
                Weapon();
                Piece(s.Cape, null);
                names.Add(tier >= 5 && random.NextDouble() < s.ArmoredShare ? s.ArmoredMount : s.PatrolMount);
                break;
            case BotRole.Invader:
            case BotRole.Hellgate:
                // StageL corrupted (SPEC §4.5): a patrol's gear at the difficulty's tier, no mount (allowmounts false inside); a hellgate's at its map's.
                ArmourSet();
                Weapon();
                Piece(s.Cape, null);
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
        for (var i = 0; i < names.Count; i++)
        {
            var item = style is not null && pieces.TryGetValue(i, out var artifact) ? Styled(names[i], artifact, tier, style) : Resolve(names[i], tier);
            if (item is not null && gear.All(g => g.Slot != item.Slot)) gear.Add(item);
        }
        return gear;
    }

    /// <summary>
    /// One piece of a population bot's gear by <paramref name="style"/>: six numbers drawn always (mixed, up or down, how far, enchanted,
    /// its level, artifact), then the piece at its tier (or 1..Spread off with MixedShare, within 1-8; where items.xml lacks that tier the
    /// bot's own), an artifact of <paramref name="artifact"/> with ArtifactChance where items.xml has one (else the plain piece), enchanted
    /// with EnchantChance (a weapon, offhand or armour piece; <see cref="Enchanted"/>).
    /// </summary>
    private ItemTemplate? Styled(string pattern, string? artifact, int tier, GearStyle style)
    {
        var r = style.Random;
        var (mixed, up, far, enchant, level, art) = (r.NextDouble(), r.NextDouble(), r.NextDouble(), r.NextDouble(), r.NextDouble(), r.NextDouble());
        var t = tier;
        if (style.Spread > 0 && mixed < style.MixedShare)
            t = Math.Clamp(tier + (up < 0.5 ? -1 : 1) * (1 + (int)(far * style.Spread)), 1, 8);
        ItemTemplate? item = null;
        if (artifact is not null && art < style.ArtifactChance && t >= 4) item = Resolve(artifact, t);
        item ??= Resolve(pattern, t) ?? Resolve(pattern, tier);
        if (item is not null && enchant < style.EnchantChance)
        {
            var (min, max) = ((int)style.EnchantLevels.Min, (int)style.EnchantLevels.Max);
            item = Enchanted(item, Math.Clamp(min + (int)(level * (max - min + 1)), min, max));
        }
        return item;
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

/// <summary>
/// Bot-roster gear realism of a population bot (Bots:Gear:MixedShare, TierSpread, EnchantChance, Enchant, ArtifactChance of its zone class):
/// the random numbers of its pieces (an identity's own: the same set every time) and the chances.
/// </summary>
public sealed record GearStyle(Random Random, int Spread, double MixedShare, double EnchantChance, (double Min, double Max) EnchantLevels, double ArtifactChance);
