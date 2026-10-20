using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// The item recipes (studio phase 3): each picks the official item of a family at the chosen tier as its template (model, icon,
/// recipe, stations and spells come with it) and sets the numbers the goal is about. The item line builds the same item for
/// several tiers with the numbers scaled along the family's official curve (the template's value at each tier / at the reference tier).
/// </summary>
public static partial class StudioRecipes
{
    private sealed record Stat(string Key, string Attr, string En, string Ru, double Min = 0, double Max = 100000, string Type = "number");

    private static readonly RecipeOption[] OneHanded =
    [
        Opt("MAIN_SWORD", "Broadsword", "Палаш"), Opt("MAIN_AXE", "Battleaxe", "Боевой топор"), Opt("MAIN_MACE", "Mace", "Булава"),
        Opt("MAIN_HAMMER", "Hammer", "Молот"), Opt("MAIN_DAGGER", "Dagger", "Кинжал"), Opt("MAIN_SPEAR", "Spear", "Копьё"),
        Opt("MAIN_FIRESTAFF", "Fire staff", "Посох огня"), Opt("MAIN_HOLYSTAFF", "Holy staff", "Священный посох"), Opt("MAIN_NATURESTAFF", "Nature staff", "Посох природы"),
        Opt("MAIN_FROSTSTAFF", "Frost staff", "Посох льда"), Opt("MAIN_ARCANESTAFF", "Arcane staff", "Тайный посох"), Opt("MAIN_CURSEDSTAFF", "Cursed staff", "Проклятый посох"),
        Opt("MAIN_1HCROSSBOW", "Light crossbow", "Лёгкий арбалет"),
    ];

    private static readonly RecipeOption[] TwoHanded =
    [
        Opt("2H_CLAYMORE", "Claymore", "Клеймор"), Opt("2H_DUALSWORD", "Dual swords", "Парные мечи"), Opt("2H_AXE", "Greataxe", "Секира"),
        Opt("2H_HALBERD", "Halberd", "Алебарда"), Opt("2H_HAMMER", "Great hammer", "Большой молот"), Opt("2H_POLEHAMMER", "Polehammer", "Боевой молот на древке"),
        Opt("2H_BOW", "Bow", "Лук"), Opt("2H_LONGBOW", "Longbow", "Длинный лук"), Opt("2H_CROSSBOW", "Crossbow", "Арбалет"),
        Opt("2H_FIRESTAFF", "Great fire staff", "Большой посох огня"), Opt("2H_HOLYSTAFF", "Great holy staff", "Большой священный посох"),
        Opt("2H_NATURESTAFF", "Great nature staff", "Большой посох природы"), Opt("2H_FROSTSTAFF", "Great frost staff", "Большой посох льда"),
        Opt("2H_ARCANESTAFF", "Great arcane staff", "Большой тайный посох"), Opt("2H_CURSEDSTAFF", "Great cursed staff", "Большой проклятый посох"),
        Opt("2H_QUARTERSTAFF", "Quarterstaff", "Боевой шест"), Opt("2H_SPEAR", "Pike", "Пика"), Opt("2H_DAGGERPAIR", "Dagger pair", "Парные кинжалы"),
        Opt("2H_KNUCKLES_SET1", "Brawler gloves", "Боевые перчатки"),
    ];

    private static readonly RecipeOption[] OffHands =
    [
        Opt("OFF_SHIELD", "Shield", "Щит"), Opt("OFF_BOOK", "Tome of spells", "Книга заклинаний"), Opt("OFF_TORCH", "Torch", "Факел"),
        Opt("OFF_ORB_MORGANA", "Eye of secrets", "Око тайн"), Opt("OFF_HORN_KEEPER", "Mistcaller", "Рог тумана"), Opt("OFF_TOTEM_KEEPER", "Taproot", "Корень-тотем"),
    ];

    private static readonly RecipeOption[] Tools =
    [
        Opt("2H_TOOL_PICK", "Pickaxe (ore)", "Кирка (руда)"), Opt("2H_TOOL_AXE", "Axe (wood)", "Топор (дерево)"), Opt("2H_TOOL_SICKLE", "Sickle (fiber)", "Серп (волокно)"),
        Opt("2H_TOOL_KNIFE", "Skinning knife (hide)", "Нож (шкуры)"), Opt("2H_TOOL_HAMMER", "Stone hammer (stone)", "Молот (камень)"), Opt("2H_TOOL_FISHINGROD", "Fishing rod", "Удочка"),
    ];

    private static readonly RecipeOption[] Mounts =
    [
        Opt("MOUNT_HORSE", "Riding horse", "Верховая лошадь"), Opt("MOUNT_OX", "Transport ox", "Тягловый бык"), Opt("MOUNT_ARMORED_HORSE", "Armored horse", "Бронированная лошадь"),
    ];

    private static readonly RecipeOption[] Consumables =
    [
        Opt("POTION_HEAL", "Healing potion", "Зелье лечения"), Opt("POTION_ENERGY", "Energy potion", "Зелье энергии"), Opt("POTION_COOLDOWN", "Poison potion", "Ядовитое зелье"),
        Opt("MEAL_STEW", "Stew (damage)", "Рагу (урон)"), Opt("MEAL_SANDWICH", "Sandwich (max health)", "Сэндвич (здоровье)"), Opt("MEAL_SALAD", "Salad (crafting)", "Салат (ремесло)"),
    ];

    private static readonly RecipeOption[] Goods =
    [
        Opt("METALBAR", "Resource: metal bar", "Ресурс: слиток"), Opt("PLANKS", "Resource: planks", "Ресурс: доски"), Opt("CLOTH", "Resource: cloth", "Ресурс: ткань"),
        Opt("LEATHER", "Resource: leather", "Ресурс: кожа"), Opt("STONEBLOCK", "Resource: stone block", "Ресурс: каменный блок"),
        Opt("TREASURE_KNOWLEDGE_RARITY2", "Trophy: knowledge treasure", "Трофей: сокровище знаний"), Opt("TREASURE_SILVERWARE_RARITY2", "Trophy: silverware", "Трофей: серебро"),
        Opt("QUESTITEM_TOKEN_KEEPER", "Token (quest item)", "Жетон (предмет задания)"),
    ];

    private static readonly RecipeOption[] Slots = [Opt("HEAD", "Head", "Голова"), Opt("ARMOR", "Chest", "Тело"), Opt("SHOES", "Shoes", "Обувь")];

    private static readonly RecipeOption[] Materials = [Opt("CLOTH", "Cloth", "Ткань"), Opt("LEATHER", "Leather", "Кожа"), Opt("PLATE", "Plate", "Латы")];

    private static RecipeField TierField(int def = 6, int min = 4) => Fld("tier", "int", "Tier", "Тир", Num(def), min, 8, required: true,
        hintEn: "4-8: the template is the family's official item of this tier", hintRu: "4-8: шаблон — официальный предмет семейства этого тира");

    private static RecipeField[] SpellFields(bool active, bool passive) =>
    [
        .. active ? new[] { Fld("active", "spell", "Extra active ability", "Доп. активное умение", hintEn: "offered in slot 1 besides the template's (empty: only the template's)", hintRu: "в слоте 1 вдобавок к умениям шаблона (пусто — только они)") } : [],
        .. passive ? new[] { Fld("passive", "spell", "Extra passive", "Доп. пассивка", hintEn: "a passive spell besides the template's", hintRu: "пассивное умение вдобавок к умениям шаблона") } : [],
    ];

    private static RecipeField[] CommonItemFields() =>
    [
        Fld("craftable", "bool", "Craftable", "Можно создать", JsonValue.Create(true), hintEn: "with the template's recipe and stations", hintRu: "с рецептом и станциями шаблона"),
        Fld("enchant", "bool", "Enchantment levels .1-.3", "Уровни зачарования .1-.3", JsonValue.Create(false),
            hintEn: "keep the template's .1-.3 (their recipes and item power) for the new item", hintRu: "оставить новому предмету уровни .1-.3 шаблона (их рецепты и силу)"),
    ];

    private static Func<RecipeRun, IEnumerable<double>> ItemRange(Func<RecipeRun, string> family, string attr) => r =>
    {
        var tier = r.I("tier", 6);
        var tpl = TemplateAt(r.Ctx, family(r), tier);
        return tpl is null ? [] : ItemValues(r.Ctx, attr, tpl.El.Attribute("shopsubcategory1")?.Value, tier);
    };

    private static RecipeField[] StatFields(Func<RecipeRun, string> family, params Stat[] stats) =>
        [.. stats.Select(s => Fld(s.Key, s.Type, s.En, s.Ru, null, s.Min, s.Max, hintEn: "empty: the template's value", hintRu: "пусто — значение шаблона",
            range: ItemRange(family, s.Attr)))];

    /// <summary>
    /// The item of a recipe at <paramref name="tier"/>: the family's official template, the numbers (given at the reference tier
    /// "refTier", scaled along the template's curve), the extra spells, craftable/enchantments, the auto text and the goal.
    /// </summary>
    private static JsonObject BuildItem(RecipeRun run, string family, Stat[] stats, string kindEn, string kindRu, Action<JsonObject, XElement, int>? extra = null)
    {
        var ctx = run.Ctx;
        var tier = run.I("tier", 6);
        var refTier = run.Opt("refTier") is { } rt ? (int)rt : tier;
        var tpl = TemplateAt(ctx, family, tier) ?? throw new InvalidDataException($"no official item of family {family} near tier {tier}");
        var refTpl = TemplateAt(ctx, family, refTier) ?? tpl;
        var id = ItemId(tier, run.S("base"), tpl);
        var d = new ItemKind().NewFrom(ctx, tpl.Name, id);
        var goalValues = (JsonObject)run.Values.DeepClone();
        goalValues.Remove("refTier");
        goalValues["tier"] = tier;
        if (tpl.Tier != tier) SetAttr(d, tpl.El, "tier", tier);
        var summary = new List<string>();
        foreach (var s in stats)
        {
            if (run.Opt(s.Key) is not { } v) continue;
            // The family's curve: the template's value at this tier over its value at the reference tier.
            var scaled = v;
            if (refTier != tier && Attr(tpl.El, s.Attr) is { } at && Attr(refTpl.El, s.Attr) is { } atRef && atRef != 0) scaled = v * at / atRef;
            scaled = Math.Abs(scaled) >= 10 ? Math.Round(scaled) : Math.Round(scaled, 3);
            SetAttr(d, tpl.El, s.Attr, scaled);
            goalValues[s.Key] = scaled;
            summary.Add($"{s.En.ToLowerInvariant()} {Fmt(scaled)}");
        }
        var spells = J.Arr(d["spells"]);
        d["spells"] = spells;
        if (run.S("active") is { Length: > 0 } active) spells.Insert(0, new JsonObject { ["spell"] = active, ["slot"] = 1, ["tag"] = null });
        if (run.S("passive") is { Length: > 0 } passive)
        {
            spells.Add(new JsonObject { ["spell"] = passive, ["slot"] = null, ["tag"] = null });
            if (Attr(tpl.El, "passivespellslots") is 0) SetAttr(d, "passivespellslots", "1");
        }
        if (run.Values["craftable"] is not null && !run.B("craftable"))
        {
            d["recipe"] = new JsonObject { ["silver"] = "0", ["time"] = "1", ["focus"] = "0", ["amount"] = null, ["resources"] = new JsonArray() };
            d["stations"] = new JsonArray();
        }
        if (run.B("enchant")) d["enchantments"] = true;
        var (en, ru) = run.L("name");
        d["name"] = J.LocNode(en, ru);
        d["description"] = J.LocNode(
            $"A {kindEn} of tier {tier} made in the Studio from {tpl.Name}{(summary.Count > 0 ? ": " + string.Join(", ", summary) : "")}.",
            $"{kindRu}, тир {tier}, сделан в студии из {tpl.Name}.");
        extra?.Invoke(d, tpl.El, tier);
        d["goal"] = run.Goal(run.Recipe.Id, goalValues);
        return run.Add(d);
    }

    /// <summary>A recipe-made spell draft (the effect of a potion, a mount's buff, a load passive) next to an item.</summary>
    private static JsonObject HelperSpell(RecipeRun run, string template, string id, string en, string ru)
    {
        var s = new SpellKind().NewFrom(run.Ctx, template, id);
        s["name"] = J.LocNode(en, ru);
        s["description"] = J.LocNode("", "");
        return s;
    }

    private static void RegisterItems()
    {
        Stat[] weaponStats =
        [
            new("damage", "attackdamage", "Damage", "Урон", 1, 2000), new("speed", "attackspeed", "Attack speed", "Скорость атаки", 0.1, 5),
            new("power", "abilitypower", "Ability power", "Сила умений", 1, 1000), new("health", "hitpointsmax", "Max health", "Макс. здоровье", 0, 5000),
            new("ip", "itempower", "Item power", "Сила предмета", 100, 3000),
        ];
        Stat[] armorStats =
        [
            new("armor", "physicalarmor", "Armor", "Броня", 0, 2000), new("resist", "magicresistance", "Magic resistance", "Сопр. магии", 0, 2000),
            new("health", "hitpointsmax", "Max health", "Макс. здоровье", 0, 5000), new("power", "abilitypower", "Ability power", "Сила умений", 0, 1000),
            new("ip", "itempower", "Item power", "Сила предмета", 100, 3000),
        ];
        Stat[] simpleStats = [new("ip", "itempower", "Item power", "Сила предмета", 100, 3000), new("weight", "weight", "Weight", "Вес", 0, 500)];

        void Weapon(string id, string icon, string en, string ru, string sumEn, string sumRu, RecipeOption[] families, string def, string kindEn, string kindRu, int order, string[] examples,
            string defId, string nameEn, string nameRu)
        {
            Func<RecipeRun, string> fam = r => r.S("family");
            Register(new StudioRecipe
            {
                Id = id, Kind = "item", Icon = icon, TitleEn = en, TitleRu = ru, SummaryEn = sumEn, SummaryRu = sumRu, Order = order, Examples = examples,
                Fields = [IdField(defId, true), NameField(nameEn, nameRu), TierField(), Fld("family", "select", "Weapon family", "Вид оружия", JsonValue.Create(def), required: true, options: families),
                    .. StatFields(fam, def.StartsWith("OFF") ? armorStats : weaponStats), .. SpellFields(!def.StartsWith("OFF"), true), .. CommonItemFields()],
                Build = r => BuildItem(r, r.S("family"), def.StartsWith("OFF") ? armorStats : weaponStats, kindEn, kindRu),
            });
        }
        Weapon("item.weapon1h", "⚔", "One-handed weapon", "Одноручное оружие", "A sword, mace, staff… with your damage, speed and abilities.",
            "Меч, булава, посох… с вашим уроном, скоростью и умениями.", OneHanded, "MAIN_SWORD", "one-handed weapon", "Одноручное оружие", 10, ["T6_MAIN_SWORD", "T6_MAIN_FIRESTAFF"],
            "MAIN_MYWEAPON", "My weapon", "Моё оружие");
        Weapon("item.weapon2h", "🗡", "Two-handed weapon", "Двуручное оружие", "A claymore, bow, great staff… slower and stronger.",
            "Клеймор, лук, большой посох… медленнее и сильнее.", TwoHanded, "2H_CLAYMORE", "two-handed weapon", "Двуручное оружие", 11, ["T6_2H_CLAYMORE", "T6_2H_BOW"],
            "2H_MYWEAPON", "My great weapon", "Моё двуручное оружие");
        Weapon("item.offhand", "🛡", "Off-hand", "Вторая рука", "A shield, tome, torch… for the other hand.", "Щит, книга, факел… для второй руки.",
            OffHands, "OFF_SHIELD", "off-hand", "Предмет второй руки", 12, ["T6_OFF_SHIELD", "T6_OFF_BOOK"],
            "OFF_MYOFFHAND", "My off-hand", "Мой предмет второй руки");

        Func<RecipeRun, string> armorFamily = r => $"{r.S("slot")}_{r.S("material")}_SET1";
        Register(new StudioRecipe
        {
            Id = "item.armor", Kind = "item", Icon = "🥋", TitleEn = "Armour piece", TitleRu = "Доспех", Order = 13, Examples = ["T6_HEAD_PLATE_SET1", "T6_ARMOR_CLOTH_SET1"],
            SummaryEn = "Head, chest or shoes of cloth, leather or plate: armour, resistances, an ability.", SummaryRu = "Голова, тело или обувь из ткани, кожи или лат: броня, сопротивления, умение.",
            Fields = [IdField("ARMOR_MYARMOR", true), NameField("My armour", "Мой доспех"), TierField(),
                Fld("slot", "select", "Slot", "Слот", JsonValue.Create("ARMOR"), required: true, options: Slots),
                Fld("material", "select", "Material", "Материал", JsonValue.Create("PLATE"), required: true, options: Materials),
                .. StatFields(armorFamily, armorStats), .. SpellFields(true, true), .. CommonItemFields()],
            Build = r => BuildItem(r, armorFamily(r), armorStats, "armour piece", "Доспех"),
        });

        Register(new StudioRecipe
        {
            Id = "item.bag", Kind = "item", Icon = "🎒", TitleEn = "Bag", TitleRu = "Сумка", Order = 14, Examples = ["T6_BAG", "PASSIVE_MAXLOAD_SHOES"],
            SummaryEn = "A bag with its own load bonus (a passive made with it).", SummaryRu = "Сумка со своим бонусом к грузоподъёмности (пассивка создаётся вместе с ней).",
            Fields = [IdField("BAG_MYBAG", true), NameField("My bag", "Моя сумка"), TierField(),
                Fld("load", "number", "Load bonus", "Бонус к весу", Num(0.3), 0, 10, hintEn: "a maxload buff as the official passives (0.3 = shoes, 3 = giant stag); 0: none",
                    hintRu: "бафф maxload как у официальных пассивок (0.3 — обувь, 3 — гигантский олень); 0 — нет",
                    range: r => BlockValues(r.Ctx, "buff", "value", "maxload", e => Is(e, "type", "maxload"))),
                .. StatFields(_ => "BAG", simpleStats), .. CommonItemFields()],
            Build = r => BuildItem(r, "BAG", simpleStats, "bag", "Сумка", (d, _, tier) => LoadPassive(r, d, tier)),
        });

        Register(new StudioRecipe
        {
            Id = "item.cape", Kind = "item", Icon = "🧣", TitleEn = "Cape", TitleRu = "Плащ", Order = 15, Examples = ["T6_CAPE", "PASSIVE_CAPE_MARTLOCK"],
            SummaryEn = "A cape with a passive of your choice.", SummaryRu = "Плащ с выбранной пассивкой.",
            Fields = [IdField("CAPE_MYCAPE", true), NameField("My cape", "Мой плащ"), TierField(), .. StatFields(_ => "CAPE", armorStats), .. SpellFields(false, true), .. CommonItemFields()],
            Build = r => BuildItem(r, "CAPE", armorStats, "cape", "Плащ"),
        });

        Register(new StudioRecipe
        {
            Id = "item.consumable", Kind = "item", Icon = "🧪", TitleEn = "Potion or food", TitleRu = "Зелье или еда", Order = 16, Examples = ["T6_POTION_HEAL", "T6_MEAL_STEW"],
            SummaryEn = "Built from an official consumable's effect: its strength, cooldown and stack.", SummaryRu = "Из эффекта официального расходника: сила, перезарядка, стопка.",
            Fields = [IdField("POTION_MYPOTION", true), NameField("My potion", "Моё зелье"), TierField(6, 1),
                Fld("effect", "select", "Effect of", "Эффект как у", JsonValue.Create("POTION_HEAL"), required: true, options: Consumables),
                Fld("strength", "percent", "Strength", "Сила", Num(1), 0.1, 5, hintEn: "× the effect's numbers (100 % = as the official one)", hintRu: "× числа эффекта (100 % — как у официального)"),
                Fld("cooldown", "seconds", "Cooldown", "Перезарядка", null, 0, 3600, hintEn: "empty: the effect's", hintRu: "пусто — как у эффекта",
                    range: r => SpellRootOf(r, "recastdelay")),
                Fld("stack", "int", "Stack size", "Размер стопки", Num(999), 1, 999),
                .. CommonItemFields()],
            Build = r => BuildItem(r, r.S("effect"), [], "consumable", "Расходник", (d, tpl, tier) =>
            {
                SetAttr(d, tpl, "maxstacksize", r.N("stack", 999));
                if (tpl.Attribute("consumespell")?.Value is not { Length: > 0 } effect || r.Ctx.FindSpell(effect) is not { } fx) return;
                var sid = J.Str(d["id"]) + "_EFFECT";
                var s = HelperSpell(r, effect, sid, J.Loc(d["name"]).En, J.Loc(d["name"]).Ru);
                var k = r.N("strength", 1);
                var prepared = WebPanel.PrepareSpell(fx.El);
                foreach (var e in prepared.Descendants())
                    foreach (var a in e.Attributes().Where(a => a.Name.LocalName is "change" or "value" && WebPanel.IsNumber(a.Value)))
                        if (Attr(e, a.Name.LocalName) is { } v && v != 0) Edit(s, $"{SpellKind.PathOf(prepared, e)}@{a.Name.LocalName}", Math.Round(v * k, 4));
                if (r.Opt("cooldown") is { } cd) SetAttr(s, prepared, "recastdelay", cd);
                s["description"] = J.LocNode($"The effect of {J.Loc(d["name"]).En}: {Fmt(k * 100)} % of {effect}.", $"Эффект {J.Loc(d["name"]).Ru}: {Fmt(k * 100)} % от {effect}.");
                r.Add(s);
                SetAttr(d, "consumespell", sid);
            }),
        });

        Register(new StudioRecipe
        {
            Id = "item.tool", Kind = "item", Icon = "⛏", TitleEn = "Gathering tool", TitleRu = "Инструмент сбора", Order = 17, Examples = ["T6_2H_TOOL_PICK"],
            SummaryEn = "A pickaxe, axe, sickle… with its durability.", SummaryRu = "Кирка, топор, серп… с прочностью.",
            Fields = [IdField("2H_TOOL_MYTOOL", true), NameField("My tool", "Мой инструмент"), TierField(),
                Fld("family", "select", "Tool", "Инструмент", JsonValue.Create("2H_TOOL_PICK"), required: true, options: Tools),
                .. StatFields(r => r.S("family"), [new("durability", "durability", "Durability", "Прочность", 1, 1e7), .. simpleStats]), .. CommonItemFields()],
            Build = r => BuildItem(r, r.S("family"), [new("durability", "durability", "Durability", "Прочность"), .. simpleStats], "gathering tool", "Инструмент"),
        });

        Register(new StudioRecipe
        {
            Id = "item.mount", Kind = "item", Icon = "🐎", TitleEn = "Mount", TitleRu = "Ездовое животное", Order = 18, Examples = ["T6_MOUNT_HORSE", "T6_MOUNT_HORSE_MOUNTED"],
            SummaryEn = "An official mount model with your speed, health and load.", SummaryRu = "Официальная модель с вашей скоростью, здоровьем и грузом.",
            Fields = [IdField("MOUNT_MYMOUNT", true), NameField("My mount", "Мой скакун"), TierField(6, 3),
                Fld("family", "select", "Model", "Модель", JsonValue.Create("MOUNT_HORSE"), required: true, options: Mounts),
                Fld("speed", "percent", "Speed bonus", "Бонус скорости", Num(0.7), 0, 5, hintEn: "the mounted buff's movespeedbonus", hintRu: "movespeedbonus баффа верховой езды",
                    range: r => BlockValues(r.Ctx, "buffovertime", "value", "mountspeed", e => Is(e, "type", "movespeedbonus") && e.Parent?.Attribute("category")?.Value == "mountbuff")),
                Fld("mountHealth", "number", "Mount health", "Здоровье скакуна", null, 1, 100000, hintEn: "empty: the template's", hintRu: "пусто — как у шаблона",
                    range: ItemRange(r => r.S("family"), "mounthitpointsmax")),
                Fld("load", "number", "Load bonus", "Бонус к весу", Num(0), 0, 50, hintEn: "a maxload passive (3 = giant stag); 0: none", hintRu: "пассивка maxload (3 — гигантский олень); 0 — нет"),
                .. CommonItemFields()],
            Build = r => BuildItem(r, r.S("family"), [new("mountHealth", "mounthitpointsmax", "Mount health", "Здоровье")], "mount", "Скакун", (d, tpl, tier) =>
            {
                if (tpl.Attribute("mountedbuff")?.Value is { Length: > 0 } buff && r.Ctx.FindSpell(buff) is { } bs)
                {
                    var sid = J.Str(d["id"]) + "_MOUNTED";
                    var s = HelperSpell(r, buff, sid, J.Loc(d["name"]).En, J.Loc(d["name"]).Ru);
                    var prepared = WebPanel.PrepareSpell(bs.El);
                    if (prepared.Elements("buffovertime").FirstOrDefault(e => Is(e, "type", "movespeedbonus")) is { } speed)
                        Edit(s, SpellKind.PathOf(prepared, speed) + "@value", r.N("speed", 0.7));
                    r.Add(s);
                    SetAttr(d, "mountedbuff", sid);
                }
                LoadPassive(r, d, tier);
            }),
        });

        Register(new StudioRecipe
        {
            Id = "item.goods", Kind = "item", Icon = "💎", TitleEn = "Resource, trophy or token", TitleRu = "Ресурс, трофей или жетон", Order = 19,
            Examples = ["T6_METALBAR", "TREASURE_KNOWLEDGE_RARITY2", "QUESTITEM_TOKEN_KEEPER"],
            SummaryEn = "A stackable good: crafting material, trophy to sell, or a token for quests and events.", SummaryRu = "Складываемый предмет: материал, трофей на продажу или жетон для заданий.",
            Fields = [IdField("MY_GOOD", true), NameField("My token", "Мой жетон"), TierField(6, 1),
                Fld("family", "select", "Kind", "Вид", JsonValue.Create("QUESTITEM_TOKEN_KEEPER"), required: true, options: Goods),
                Fld("stack", "int", "Stack size", "Размер стопки", Num(999), 1, 999),
                .. StatFields(r => r.S("family"), new Stat("weight", "weight", "Weight", "Вес", 0, 100)), .. CommonItemFields()],
            Build = r => BuildItem(r, r.S("family"), [new("weight", "weight", "Weight", "Вес")], "good", "Предмет", (d, tpl, _) => SetAttr(d, tpl, "maxstacksize", r.N("stack", 999))),
        });

        // The item line: one of the tiered item recipes for T<from>..T<to>, its numbers given at the reference tier.
        RecipeOption[] lineOf =
        [
            Opt("item.weapon1h", "One-handed weapon", "Одноручное оружие"), Opt("item.weapon2h", "Two-handed weapon", "Двуручное оружие"), Opt("item.offhand", "Off-hand", "Вторая рука"),
            Opt("item.armor", "Armour piece", "Доспех"), Opt("item.bag", "Bag", "Сумка"), Opt("item.cape", "Cape", "Плащ"), Opt("item.tool", "Gathering tool", "Инструмент"),
            Opt("item.mount", "Mount", "Скакун"),
        ];
        Register(new StudioRecipe
        {
            Id = "line.items", Kind = "line", Icon = "📶", TitleEn = "Item line T4–T8", TitleRu = "Линейка T4–T8", Order = 40, InheritFrom = "of",
            SummaryEn = "The same item for every tier, its numbers scaled along the family's official curve; one draft per tier.",
            SummaryRu = "Один предмет для каждого тира, числа масштабируются по официальной кривой семейства; черновик на тир.",
            Fields = [Fld("of", "select", "Item", "Предмет", JsonValue.Create("item.weapon1h"), required: true, options: lineOf),
                Fld("from", "int", "From tier", "От тира", Num(4), 4, 8, required: true), Fld("to", "int", "To tier", "До тира", Num(8), 4, 8, required: true),
                Fld("tier", "int", "Numbers given at tier", "Числа заданы для тира", Num(6), 4, 8, required: true,
                    hintEn: "the other tiers get them × (the template's value there / here)", hintRu: "остальные тиры получат их × (значение шаблона там / здесь)")],
            Build = r =>
            {
                var from = r.I("from", 4);
                var to = r.I("to", 8);
                if (to < from) throw new InvalidDataException("to tier is below from tier");
                for (var t = from; t <= to; t++)
                {
                    var values = (JsonObject)r.Values.DeepClone();
                    foreach (var k in new[] { "of", "from", "to" }) values.Remove(k);
                    values["refTier"] = r.I("tier", 6);
                    values["tier"] = t;
                    r.Sub(r.S("of"), values);
                }
            },
        });
    }

    /// <summary>A maxload passive of its own for a bag or mount (value "load"; nothing at 0), offered by the item.</summary>
    private static void LoadPassive(RecipeRun r, JsonObject d, int tier)
    {
        if (r.N("load") <= 0 || r.Ctx.FindSpell("PASSIVE_MAXLOAD_SHOES") is null) return;
        var sid = J.Str(d["id"]) + "_LOAD";
        var s = HelperSpell(r, "PASSIVE_MAXLOAD_SHOES", sid, "Load bonus", "Бонус к весу");
        Edit(s, "0@value", r.N("load"));
        s["description"] = J.LocNode($"Increases the maximum load by {Fmt(r.N("load") * 100)} %.", $"Увеличивает максимальный вес на {Fmt(r.N("load") * 100)} %.");
        r.Add(s);
        var spells = J.Arr(d["spells"]);
        d["spells"] = spells;
        spells.Add(new JsonObject { ["spell"] = sid, ["slot"] = null, ["tag"] = null });
        SetAttr(d, "passivespellslots", "1");
    }

    private static IEnumerable<double> SpellRootOf(RecipeRun r, string attr) =>
        TemplateAt(r.Ctx, r.S("effect"), r.I("tier", 6))?.El.Attribute("consumespell")?.Value is { } fx && r.Ctx.FindSpell(fx) is { } s
            ? r.Ctx.Catalog.Items.Where(i => i.Pack is null && i.El.Attribute("consumespell") is not null)
                .Select(i => r.Ctx.Catalog.SpellByName.GetValueOrDefault(i.El.Attribute("consumespell")!.Value)?.El).OfType<XElement>()
                .Where(e => e.Attribute("category")?.Value == s.El.Attribute("category")?.Value).Select(e => Attr(e, attr)).OfType<double>()
            : [];
}
