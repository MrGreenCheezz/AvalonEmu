using System.Runtime.CompilerServices;
using AlbionLocal.Modules;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// What modules get instead of raw names (docs/modules/README.md "Types, not text"): the server's templates as <see cref="ItemType"/>, <see cref="SpellInfo"/>,
/// <see cref="MobType"/> and <see cref="ZoneInfo"/>, one object per template, and the words the game's systems report in as enums. The systems go on calling the
/// host with their own names and words: everything that turns them into types is here. One per host.
/// </summary>
internal sealed class ModuleTypes(World world)
{
    private readonly ConditionalWeakTable<ItemTemplate, ItemType> _items = new();
    private readonly ConditionalWeakTable<SpellTemplate, SpellInfo> _spells = new();
    private readonly ConditionalWeakTable<MobTemplate, MobType> _mobs = new();
    private IReadOnlyList<ItemType>? _all;

    // ---- items

    public ItemType Of(ItemTemplate item) => _items.GetValue(item, Build);

    public ItemType? OfOrNull(ItemTemplate? item) => item is null ? null : Of(item);

    /// <summary>The kind of item called <paramref name="name"/>; one that items.xml does not have still gets a type, with only its name known, so that a hook never loses what happened.</summary>
    public ItemType Item(string name) => Find(name) ?? Unknown(name);

    public ItemType? Find(string name) => world.Items.Find(name.Trim()) is { } t ? Of(t) : null;

    /// <summary><see cref="Find"/> for a module that named an item: a miss goes to the log with the closest name.</summary>
    public ItemType? FindOrHint(string name)
    {
        if (Find(name) is { } found) return found;
        Photon.Log.Warn("module:" + (ModuleHost.Current?.Name ?? "?"), $"there is no item \"{name}\"{(world.Items.Closest(name) is { } near ? $" (did you mean \"{near}\"?)" : "")}");
        return null;
    }

    public IReadOnlyList<ItemType> AllItems => _all ??= [.. world.Items.All.Select(Of)];

    public ItemInfo Info(Item item) => new(Of(item.Template), item.Count, (ItemQuality)Math.Clamp(item.Quality, 1, 5), item.DurabilityRaw / 10000.0);

    private static ItemType Unknown(string name) => new() { Name = name, BaseName = ModuleHost.BaseName(name), Family = FamilyOf(name) };

    private static string FamilyOf(string name)
    {
        var bare = ModuleHost.BaseName(name);
        return bare.Length > 3 && bare[0] == 'T' && char.IsAsciiDigit(bare[1]) && bare[2] == '_' ? bare[3..] : bare;
    }

    private static ItemType Build(ItemTemplate t)
    {
        var shop = t.Attrs.GetValueOrDefault("shopcategory", "");
        var sub = t.Attrs.GetValueOrDefault("shopsubcategory1", "");
        var slot = Array.IndexOf(Inventory.SlotNames, t.Slot);
        return new ItemType
        {
            Name = t.Name, BaseName = ModuleHost.BaseName(t.Name), Family = FamilyOf(t.Name), Tier = t.Tier, Enchantment = t.Enchantment,
            Slot = slot >= 0 ? (EquipSlot)slot : EquipSlot.None,
            Category = shop switch
            {
                "weapons" => ItemCategory.Weapon,
                "offhands" => ItemCategory.OffHand,
                "head" => ItemCategory.Head,
                "armors" => ItemCategory.Armor,
                "shoes" => ItemCategory.Shoes,
                "bags" => ItemCategory.Bag,
                "capes" => ItemCategory.Cape,
                "mounts" => ItemCategory.Mount,
                "consumables" => ItemCategory.Consumable,
                "crafting" => ItemCategory.Resource,
                "gathering" => ItemCategory.Gathering,
                "farming" => ItemCategory.Farming,
                "furniture" => ItemCategory.Furniture,
                "artefacts" => ItemCategory.Artefact,
                "vanity" => ItemCategory.Vanity,
                _ => ItemCategory.Other,
            },
            Subcategory = sub,
            Weapon = shop != "weapons" ? WeaponType.None : sub switch
            {
                "sword" => WeaponType.Sword,
                "axe" => WeaponType.Axe,
                "mace" => WeaponType.Mace,
                "hammer" => WeaponType.Hammer,
                "spear" => WeaponType.Spear,
                "dagger" => WeaponType.Dagger,
                "quarterstaff" => WeaponType.Quarterstaff,
                "knuckles" => WeaponType.WarGloves,
                "bow" => WeaponType.Bow,
                "crossbow" => WeaponType.Crossbow,
                "firestaff" => WeaponType.FireStaff,
                "froststaff" => WeaponType.FrostStaff,
                "holystaff" => WeaponType.HolyStaff,
                "naturestaff" => WeaponType.NatureStaff,
                "arcanestaff" => WeaponType.ArcaneStaff,
                "cursestaff" => WeaponType.CursedStaff,
                "shapeshifterstaff" => WeaponType.ShapeshifterStaff,
                _ => WeaponType.Other,
            },
            Armor = shop is not ("head" or "armors" or "shoes") ? ArmorType.None
                : sub.StartsWith("cloth_", StringComparison.Ordinal) ? ArmorType.Cloth
                : sub.StartsWith("leather_", StringComparison.Ordinal) ? ArmorType.Leather
                : sub.StartsWith("plate_", StringComparison.Ordinal) ? ArmorType.Plate
                : ArmorType.Other,
            OffHand = shop != "offhands" ? OffHandType.None : sub switch
            {
                "shieldtype" => OffHandType.Shield,
                "booktype" => OffHandType.Book,
                "torchtype" => OffHandType.Torch,
                _ => OffHandType.Other,
            },
            Tool = shop != "gathering" || t.Slot != "mainhand" ? ToolType.None : sub switch
            {
                "fiber" => ToolType.Fiber,
                "hide" => ToolType.Hide,
                "ore" => ToolType.Ore,
                "rock" => ToolType.Rock,
                "wood" => ToolType.Wood,
                "fish" => ToolType.Fishing,
                "tracking" => ToolType.Tracking,
                _ => ToolType.Other,
            },
            Consumable = shop != "consumables" ? ConsumableType.None : sub switch
            {
                "food" => ConsumableType.Food,
                "potions" => ConsumableType.Potion,
                "tomes" => ConsumableType.Tome,
                "silverbag" => ConsumableType.SilverBag,
                _ => ConsumableType.Other,
            },
            IsTwoHanded = t.TwoHanded, ItemPower = t.ItemPower, MaxStack = t.MaxStackSize, MaxQuality = (ItemQuality)Math.Clamp(t.MaxQuality, 1, 5), MaxDurability = t.MaxDurability,
            Attributes = t.Attrs,
        };
    }

    // ---- spells

    public SpellInfo Of(SpellTemplate spell) => _spells.GetValue(spell, Build);

    public SpellInfo? OfOrNull(SpellTemplate? spell) => spell is null ? null : Of(spell);

    /// <summary>The spell the numeric id of a hit names; null for -1 (an auto-attack).</summary>
    public SpellInfo? OfId(short id)
    {
        if (id < 0 || world.Spells is not { } spells) return null;
        return spells.Template(id) is { } t ? Of(t) : spells.Name(id) is { } name ? new SpellInfo { Name = name, Id = id } : null;
    }

    public SpellInfo? FindSpell(string? name) => world.Spells?.Template(name?.Trim()) is { } t ? Of(t) : null;

    /// <summary>The spell called <paramref name="name"/>; one that spells.xml does not have still gets a type, with only its name known.</summary>
    public SpellInfo Spell(string name) => FindSpell(name) ?? new SpellInfo { Name = name };

    private static SpellInfo Build(SpellTemplate t) => new()
    {
        Name = t.Name, Id = t.Id,
        Kind = t.Kind switch { "activespell" => SpellKind.Active, "passivespell" => SpellKind.Passive, "togglespell" => SpellKind.Toggle, _ => SpellKind.Other },
        Category = t.Category switch
        {
            "" => SpellCategory.None,
            "buff_damageshield" => SpellCategory.DamageShield,
            var c => Enum.TryParse<SpellCategory>(c, ignoreCase: true, out var parsed) ? parsed : SpellCategory.Other,
        },
        CategoryName = t.Category,
        Target = Enum.TryParse<SpellTarget>(t.Target, ignoreCase: true, out var target) ? target : SpellTarget.None,
        CastTime = t.CastingTime, Cooldown = t.RecastDelay, Range = t.CastRange, StackLimit = t.StackLimit, IsChanneled = t.HasChannel, IsProjectile = t.ProjectileSpeed is not null,
    };

    // ---- mobs

    public MobType Of(MobTemplate mob) => _mobs.GetValue(mob, Build);

    public MobType? FindMob(string name) => world.Clusters?.MobsIfLoaded?.Find(name.Trim()) is { } t ? Of(t) : null;

    private static MobType Build(MobTemplate t) => new()
    {
        Name = t.Name, Tier = t.Tier,
        Category = t.Category switch
        {
            "" => MobCategory.None,
            "standard" => MobCategory.Standard,
            "trash" => MobCategory.Trash,
            "champion" => MobCategory.Champion,
            "miniboss" => MobCategory.MiniBoss,
            "boss" => MobCategory.Boss,
            "summon" => MobCategory.Summon,
            "critter" => MobCategory.Critter,
            "environment" => MobCategory.Environment,
            "chest" => MobCategory.Chest,
            "harmless" => MobCategory.Harmless,
            "vanity" => MobCategory.Vanity,
            _ => MobCategory.Other,
        },
        Faction = t.Faction switch
        {
            "KEEPER" or "KEEPER_CAMP" or "SKEEPER" => MobFaction.Keeper,
            "UNDEAD" or "UNDEAD_CAMP" or "SUNDEAD" => MobFaction.Undead,
            "HERETIC" or "HERETIC_CAMP" or "SHERETIC" => MobFaction.Heretic,
            "MORGANA" or "MORGANA_CAMP" or "SMORGANA" => MobFaction.Morgana,
            "AVALON" or "SAVALON" => MobFaction.Avalon,
            "DEMON" or "SDEMON" => MobFaction.Demon,
            "CRITTER" => MobFaction.Critter,
            _ => MobFaction.Other,
        },
        FactionName = t.Faction, Fame = t.Fame, MaxHealth = t.HitPointsMax,
    };

    // ---- zones

    /// <summary>The zone of a unit on no map.</summary>
    internal static readonly ZoneInfo Nowhere = new();

    /// <summary>The zone of <paramref name="map"/> as it is now. A new object each time: the PvP rules can be overridden, and what it costs is small.</summary>
    public ZoneInfo Zone(Map? map)
    {
        if (map is null) return Nowhere;
        return Build(map.Cluster, map.Template?.Info, Pvp.RulesOf(world, map), Pvp.Lethal(world, map));
    }

    public ZoneInfo? FindZone(string cluster)
    {
        if (world.Maps.Find(cluster) is { } map) return Zone(map);
        if (world.Clusters?.WorldIfLoaded?.Find(cluster) is not { } info) return null;
        var rules = world.PvpOverride ?? world.CachedCluster(cluster)?.Pvp ?? ClusterPvp.Default;
        return Build(cluster, info, rules, world.Config.PvpLethalDeath && rules.PlayersAreLethal);
    }

    private static ZoneInfo Build(string cluster, ClusterInfo? info, ClusterPvp rules, bool lethal)
    {
        var type = info?.Type ?? "";
        return new ZoneInfo
        {
            Cluster = cluster, DisplayName = info?.DisplayName ?? "", Type = type, Kind = KindOf(type), Color = ColorOf(type), IsLethal = lethal,
            Pvp = rules.Rule switch
            {
                PvpRule.PvpDisabled => ZonePvp.Disabled,
                PvpRule.FactionPvpOnly => ZonePvp.FactionOnly,
                PvpRule.PvpAllowed => ZonePvp.Allowed,
                _ => ZonePvp.Forced,
            },
        };
    }

    internal static ZoneKind KindOf(string type)
    {
        bool Starts(string prefix) => type.StartsWith(prefix, StringComparison.Ordinal);
        if (type.Contains("TUTORIAL", StringComparison.Ordinal)) return ZoneKind.Tutorial;
        if (Starts("PLAYERCITY") || Starts("STARTINGCITY")) return ZoneKind.City;
        if (Starts("OPENPVP") || Starts("SAFEAREA") || Starts("STARTAREA")) return ZoneKind.OpenWorld;
        if (Starts("PASSAGE")) return ZoneKind.Passage;
        if (Starts("DUNGEON_HELL")) return ZoneKind.Hellgate;
        if (Starts("DUNGEON")) return ZoneKind.Dungeon;
        if (Starts("CORRUPTED")) return ZoneKind.CorruptedDungeon;
        if (type.Contains("EXPEDITION", StringComparison.Ordinal)) return ZoneKind.Expedition;
        if (Starts("ARENA")) return ZoneKind.Arena;
        if (Starts("HIDEOUT")) return ZoneKind.Hideout;
        if (Starts("TUNNEL")) return ZoneKind.Road;
        return type.Contains("ISLAND", StringComparison.Ordinal) ? ZoneKind.Island : ZoneKind.Other;
    }

    internal static ZoneColor ColorOf(string type)
    {
        if (type.Contains("_BLACK", StringComparison.Ordinal)) return ZoneColor.Black;
        if (type.Contains("_RED", StringComparison.Ordinal)) return ZoneColor.Red;
        if (type.Contains("_YELLOW", StringComparison.Ordinal)) return ZoneColor.Yellow;
        return type.Contains("SAFEAREA", StringComparison.Ordinal) || type.StartsWith("STARTAREA", StringComparison.Ordinal) || type.StartsWith("STARTINGCITY", StringComparison.Ordinal)
            ? ZoneColor.Blue : ZoneColor.None;
    }

    // ---- the words the systems report in

    public static BreakCause Break(string word) => word switch
    {
        "knockdown" => BreakCause.Knockdown,
        "gathering" => BreakCause.Gathering,
        "fishing" => BreakCause.Fishing,
        _ => BreakCause.Death,
    };

    public static MountReason Mount(string word) => word switch
    {
        "mount" => MountReason.Mount,
        "remount" => MountReason.Remount,
        "dismount" or "dismount request" => MountReason.Dismount,
        "damage" => MountReason.Damage,
        "logout" => MountReason.Logout,
        "the player walks" => MountReason.Walked,
        "cancelled by the client" => MountReason.Cancelled,
        "knockdown" or "knocked down" => MountReason.KnockedDown,
        "died" => MountReason.MountDied,
        _ => MountReason.Other,
    };

    public static LeaveCombatReason LeftCombat(string word) => word switch
    {
        "no target left" => LeaveCombatReason.NoTarget,
        "pursuit radius" or "no hit while out of reach" => LeaveCombatReason.TooFar,
        "no hit while out of sight" => LeaveCombatReason.OutOfSight,
        "the AI went off" => LeaveCombatReason.AiOff,
        _ => LeaveCombatReason.Other,
    };

    public static FameSource Fame(string word) => Enum.TryParse<FameSource>(word, out var source) ? source : FameSource.Other;

    public static GoalKind Goal(string word) => word switch
    {
        "bounty" => GoalKind.Bounty,
        "expedition" => GoalKind.Expedition,
        "corrupted" => GoalKind.Corrupted,
        _ => GoalKind.Other,
    };

    public static PartyChange Party(string word) => word switch
    {
        "created" => PartyChange.Created,
        "invited" => PartyChange.Invited,
        "is kicked" => PartyChange.Kicked,
        "disconnected" => PartyChange.Disconnected,
        _ => PartyChange.Left,
    };

    /// <summary>An action the server's enum has no name for comes as a number, which says nothing here.</summary>
    public static StationActionKind Station(string word) => !char.IsAsciiDigit(word[0]) && Enum.TryParse<StationActionKind>(word, out var action) ? action : StationActionKind.Other;

    public static LootContainer Loot(string kind) => kind switch
    {
        "loot" => LootContainer.Bag,
        "loot chest" => LootContainer.Chest,
        _ => LootContainer.Other,
    };
}
