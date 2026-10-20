using System.Text.Json.Nodes;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// Kits (studio phase 3): one click makes several linked entities with a name prefix: a legendary boss (mob variant + loot list +
/// unique weapon with a new ability + the weapon's drop rule from the boss), an item line, a consumable set, an elite camp.
/// Each part is an ordinary draft built by its own recipe, so it can be edited (and rebuilt) on its own afterwards.
/// </summary>
public static partial class StudioRecipes
{
    /// <summary>An official mob of <paramref name="category"/> near <paramref name="tier"/> with a name (a sensible default for a kit).</summary>
    private static JsonNode? MobDefault(StudioContext ctx, string[] categories, int tier, int skip = 0)
    {
        foreach (var category in categories)
        {
            var list = ctx.Catalog.MobEls
                .Where(m => string.Equals(m.Value.Attribute("mobtypecategory")?.Value, category, StringComparison.OrdinalIgnoreCase)
                    && Attr(m.Value, "tier") is { } t && Math.Abs(t - tier) <= 1 && ctx.Catalog.Loc.ContainsKey(ModCatalog.MobNameKey(m.Value))
                    && !m.Key.Contains("TEST", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Key).Order(StringComparer.Ordinal).ToList();
            if (list.Count > 0) return JsonValue.Create(list[Math.Min(skip * Math.Max(1, list.Count / 4), list.Count - 1)]);
        }
        return ctx.Catalog.MobEls.Keys.Order(StringComparer.Ordinal).FirstOrDefault() is { } any ? JsonValue.Create(any) : null;
    }

    private static RecipeField PrefixField(string def) => Fld("prefix", "id", "Name prefix", "Префикс имён", JsonValue.Create(def), required: true,
        hintEn: "put in front of every unique name the kit makes (FROST → FROST_BOSS, T6_MAIN_FROST_BLADE…)", hintRu: "ставится перед каждым уникальным именем набора (FROST → FROST_BOSS, T6_MAIN_FROST_BLADE…)");

    private static RecipeField TitleField(string en, string ru) => Fld("title", "loc", "Title word", "Слово в названиях", J.LocNode(en, ru), required: true,
        hintEn: "starts the names: “Frost Blade”, “Frost Lord”", hintRu: "начинает названия: «Морозный клинок», «Морозный владыка»");

    private static JsonObject Loc(RecipeRun r, string en, string ru)
    {
        var (ten, tru) = r.L("title");
        return J.LocNode($"{ten} {en}".Trim(), $"{tru} {ru}".Trim());
    }

    private static JsonObject MobVariant(RecipeRun r, string id, string template, JsonObject name, double health, double damage, double fame, string? list, double listChance, double replace)
    {
        var mob = new MobKind().NewFrom(r.Ctx, template, id);
        mob["name"] = name;
        mob["healthFactor"] = health;
        mob["damageFactor"] = damage;
        mob["fameFactor"] = fame;
        mob["replaceChance"] = replace;
        mob["loot"] = list is null ? new JsonArray() : new JsonArray(new JsonObject { ["list"] = list, ["chance"] = listChance });
        return r.Add(mob);
    }

    private static JsonObject LootList(RecipeRun r, string id, JsonObject name, int rolls, params (string Item, double Chance, double Weight, int Min, int Max)[] entries)
    {
        var list = new LootListKind().NewFrom(r.Ctx, "", id);
        list["name"] = name;
        list["rolls"] = rolls;
        list["entries"] = new JsonArray([.. entries.Where(e => r.Ctx.ItemExists(e.Item)).Select(e => (JsonNode)new JsonObject
        {
            ["item"] = e.Item, ["chance"] = e.Chance, ["weight"] = e.Weight > 0 ? e.Weight : null, ["min"] = e.Min, ["max"] = e.Max,
        })]);
        return r.Add(list);
    }

    private static readonly RecipeOption[] AbilityRecipes =
    [
        Opt("spell.direct", "Direct damage", "Прямой урон"), Opt("spell.area", "Area damage", "Урон по области"), Opt("spell.dot", "Damage over time", "Урон со временем"),
        Opt("spell.slow", "Slow", "Замедление"), Opt("spell.control", "Stun, root or silence", "Контроль"), Opt("spell.knock", "Knockback or pull", "Отбрасывание"),
        Opt("spell.field", "Ground field", "Поле на земле"), Opt("spell.dash", "Dash", "Рывок"),
    ];

    private static void RegisterKits()
    {
        Register(new StudioRecipe
        {
            Id = "kit.boss", Kind = "kit", Icon = "👑", Order = 200, TitleEn = "Legendary boss with unique drop", TitleRu = "Легендарный босс с уникальным дропом",
            SummaryEn = "A mob variant (stronger official boss), its loot list, a unique weapon with a new ability, and the weapon's drop rule from the boss.",
            SummaryRu = "Вариант моба (усиленный официальный босс), его список лута, уникальное оружие с новым умением и правило дропа оружия с босса.",
            Fields =
            [
                PrefixField("FROST"), TitleField("Frost", "Морозный"),
                Fld("mob", "mob", "Boss (model and spells)", "Босс (модель и умения)", required: true, defaultFrom: c => MobDefault(c, ["boss", "miniboss", "champion"], 6)),
                Fld("health", "number", "Health ×", "Здоровье ×", Num(5), 0.1, 200, required: true),
                Fld("damage", "number", "Damage ×", "Урон ×", Num(1.5), 0.1, 50, required: true),
                Fld("fame", "number", "Fame ×", "Слава ×", Num(3), 0, 100),
                Fld("replace", "percent", "Replaces the template in the world", "Заменяет шаблон в мире", Num(0.02), 0, 1,
                    hintEn: "chance per world spawn of the template mob (0: only .mob variant)", hintRu: "шанс на каждое появление шаблонного моба в мире (0 — только .mob variant)"),
                Fld("family", "select", "Weapon", "Оружие", JsonValue.Create("MAIN_SWORD"), required: true, options: OneHanded),
                TierField(),
                Fld("ability", "select", "Its ability", "Его умение", JsonValue.Create("spell.area"), required: true, options: AbilityRecipes),
                Fld("dropChance", "percent", "Weapon drop chance", "Шанс дропа оружия", Num(0.1), 0.0001, 1, required: true, hintEn: "per boss kill × Rates:Loot", hintRu: "за убийство босса × Rates:Loot"),
            ],
            Build = r =>
            {
                var p = r.S("prefix");
                var tier = r.I("tier", 6);
                var spellId = $"{p}_POWER";
                var abilityTitle = AbilityRecipes.First(o => o.Value == r.S("ability"));
                r.Sub(r.S("ability"), new JsonObject { ["base"] = spellId, ["name"] = Loc(r, abilityTitle.En.ToLowerInvariant(), abilityTitle.Ru.ToLowerInvariant()) });
                var weaponRun = r.Sub("item.weapon1h", new JsonObject
                {
                    ["base"] = $"MAIN_{p}_BLADE", ["name"] = Loc(r, "Blade", "клинок"), ["tier"] = tier, ["family"] = r.S("family"), ["active"] = spellId, ["craftable"] = false,
                });
                var bossId = $"{p}_BOSS";
                var weapon = weaponRun.Drafts.First(d => J.Str(d["kind"]) == "item");
                weapon["drops"] = new JsonArray(new JsonObject { ["mobs"] = new JsonArray(bossId), ["chests"] = new JsonArray(), ["category"] = "", ["chance"] = r.N("dropChance", 0.1), ["amount"] = 1 });
                var listId = $"{p}_BOSS_LOOT";
                LootList(r, listId, Loc(r, "Lord's hoard", "клад владыки"), 2,
                    ($"T{tier}_RUNE", 1, 3, 2, 6), ($"T{tier}_SOUL", 1, 2, 1, 4), ($"T{tier}_RELIC", 1, 1, 1, 2), ($"T{tier}_METALBAR", 1, 0, 5, 15));
                MobVariant(r, bossId, r.S("mob"), Loc(r, "Lord", "владыка"), r.N("health", 5), r.N("damage", 1.5), r.N("fame", 3), listId, 1, r.N("replace"));
            },
        });

        Register(new StudioRecipe
        {
            Id = "kit.line", Kind = "kit", Icon = "📶", Order = 201, TitleEn = "Item line T4–T8 (kit)", TitleRu = "Линейка T4–T8 (набор)",
            SummaryEn = "An item of each tier with the prefix in its name, numbers on the official curve.", SummaryRu = "Предмет каждого тира с префиксом в имени, числа по официальной кривой.",
            Fields =
            [
                PrefixField("STORM"), TitleField("Storm", "Грозовой"),
                Fld("of", "select", "Item", "Предмет", JsonValue.Create("item.weapon1h"), required: true, options: [Opt("item.weapon1h", "One-handed weapon", "Одноручное оружие"),
                    Opt("item.weapon2h", "Two-handed weapon", "Двуручное оружие"), Opt("item.armor", "Armour piece", "Доспех"), Opt("item.cape", "Cape", "Плащ")]),
                Fld("from", "int", "From tier", "От тира", Num(4), 4, 8, required: true), Fld("to", "int", "To tier", "До тира", Num(8), 4, 8, required: true),
            ],
            Build = r =>
            {
                var p = r.S("prefix");
                var of = r.S("of");
                var baseName = of switch { "item.weapon2h" => $"2H_{p}", "item.armor" => $"ARMOR_{p}", "item.cape" => $"CAPE_{p}", _ => $"MAIN_{p}" };
                r.Sub("line.items", new JsonObject
                {
                    ["of"] = of, ["from"] = r.I("from", 4), ["to"] = r.I("to", 8), ["tier"] = 6, ["base"] = baseName,
                    ["name"] = Loc(r, of == "item.cape" ? "Cape" : of == "item.armor" ? "Armour" : "Weapon", of == "item.cape" ? "плащ" : of == "item.armor" ? "доспех" : "оружие"),
                });
            },
        });

        Register(new StudioRecipe
        {
            Id = "kit.consumables", Kind = "kit", Icon = "🍲", Order = 202, TitleEn = "Consumable set", TitleRu = "Набор расходников",
            SummaryEn = "A potion and a food, each with its own effect spell.", SummaryRu = "Зелье и еда, у каждого свой эффект.",
            Fields =
            [
                PrefixField("HUNTER"), TitleField("Hunter's", "Охотничий"), TierField(6, 1),
                Fld("potion", "select", "Potion", "Зелье", JsonValue.Create("POTION_HEAL"), required: true, options: Consumables.Where(c => c.Value.StartsWith("POTION")).ToArray()),
                Fld("food", "select", "Food", "Еда", JsonValue.Create("MEAL_STEW"), required: true, options: Consumables.Where(c => c.Value.StartsWith("MEAL")).ToArray()),
                Fld("strength", "percent", "Strength", "Сила", Num(1.2), 0.1, 5),
            ],
            Build = r =>
            {
                var p = r.S("prefix");
                foreach (var (key, en, ru, prefix) in new[] { ("potion", "Potion", "зелье", "POTION"), ("food", "Meal", "блюдо", "MEAL") })
                    r.Sub("item.consumable", new JsonObject
                    {
                        ["base"] = $"{prefix}_{p}", ["name"] = Loc(r, en, ru), ["tier"] = r.I("tier", 6), ["effect"] = r.S(key), ["strength"] = r.N("strength", 1),
                    });
            },
        });

        Register(new StudioRecipe
        {
            Id = "kit.camp", Kind = "kit", Icon = "⛺", Order = 203, TitleEn = "Elite camp", TitleRu = "Элитный лагерь",
            SummaryEn = "A few elite variants of official mobs that turn up in the world, and the chest loot they carry.",
            SummaryRu = "Несколько элитных вариантов официальных мобов, появляющихся в мире, и сундучный лут при них.",
            Fields =
            [
                PrefixField("RAIDER"), TitleField("Raider", "Налётчик"), TierField(6, 3),
                Fld("mob1", "mob", "Mob 1", "Моб 1", required: true, defaultFrom: c => MobDefault(c, ["standard", "champion", "trash"], 6, 0)),
                Fld("mob2", "mob", "Mob 2", "Моб 2", defaultFrom: c => MobDefault(c, ["standard", "champion", "trash"], 6, 1)),
                Fld("mob3", "mob", "Mob 3", "Моб 3", defaultFrom: c => MobDefault(c, ["champion", "miniboss", "standard"], 6, 2)),
                Fld("health", "number", "Health ×", "Здоровье ×", Num(1.6), 0.1, 50, required: true),
                Fld("damage", "number", "Damage ×", "Урон ×", Num(1.3), 0.1, 20, required: true),
                Fld("replace", "percent", "Turns up instead of the template", "Появляется вместо шаблона", Num(0.05), 0, 1),
                Fld("chest", "percent", "Chest loot chance", "Шанс сундучного лута", Num(0.35), 0, 1, required: true),
            ],
            Build = r =>
            {
                var p = r.S("prefix");
                var tier = r.I("tier", 6);
                var listId = $"{p}_CAMP_CHEST";
                LootList(r, listId, Loc(r, "camp chest", "сундук лагеря"), 2,
                    ($"T{tier}_METALBAR", 1, 3, 3, 8), ($"T{tier}_PLANKS", 1, 3, 3, 8), ($"T{tier}_LEATHER", 1, 3, 3, 8), ($"T{tier}_CLOTH", 1, 3, 3, 8), ($"T{tier}_RUNE", 0.25, 0, 1, 3));
                var n = 0;
                foreach (var key in new[] { "mob1", "mob2", "mob3" })
                {
                    if (r.S(key) is not { Length: > 0 } template) continue;
                    n++;
                    MobVariant(r, $"{p}_ELITE{n}", template, Loc(r, n == 3 ? "Chief" : "Elite", n == 3 ? "вожак" : "элита"),
                        r.N("health", 1.6) * (n == 3 ? 1.5 : 1), r.N("damage", 1.3), 1.5, listId, r.N("chest", 0.35), r.N("replace", 0.05));
                }
            },
        });
    }
}
