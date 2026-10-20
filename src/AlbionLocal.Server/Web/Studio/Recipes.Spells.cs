using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// The ability recipes (studio phase 3). Each starts from an official spell whose structure fits the goal (its animation,
/// effects and targeting), takes out the blocks the goal does not want and adds effect blocks copied from other official
/// spells (SpellKind's blocks add/remove), then sets the meaningful numbers and writes the RU/EN description from them.
/// Templates (client 1.32.030): THREATENINGSTRIKE_HAMMER (single-target hit), FLAMEPILLAR (ground circle), CURSEDOT (projectile
/// + DoT of CURSEDOT_EFFECT), HOLYHOT (friendly target), SHIELDFRIENDLY, HAMSTRINGSWORD_EFFECT (slow block), BEAR_GROUND_SMASH_STUN /
/// GROUNDARROW_ROOT / PASSIVE_SILENCECHANCE_EFFECT (control blocks), GROUNDDASH, PBAOE_KNOCKBACK, PBAOE_PULL, SUMMON_FOX,
/// PASSIVE_MR_AR, GLACIALFIELD and WELLOFLIFE2 (ground fields ticking through a spell of their own).
/// </summary>
public static partial class StudioRecipes
{
    private static readonly RecipeOption[] DamageTypes = [Opt("physical", "Physical", "Физический"), Opt("magic", "Magic", "Магический")];

    private static readonly RecipeOption[] BuffStats =
    [
        Opt("physicalattackdamagebonus", "Attack damage", "Урон атак"), Opt("physicalspelldamagebonus", "Physical ability damage", "Урон физ. умений"),
        Opt("magicspelldamagebonus", "Magic ability damage", "Урон маг. умений"), Opt("attackspeedbonus", "Attack speed", "Скорость атаки"),
        Opt("movespeedbonus", "Move speed", "Скорость передвижения"), Opt("physicalarmor", "Armor", "Броня"), Opt("magicresistance", "Magic resistance", "Сопротивление магии"),
        Opt("healbonus", "Healing done", "Исходящее лечение"), Opt("healmodifier", "Healing received", "Входящее лечение"),
        Opt("bonusdefensevsmobs", "Defense vs mobs", "Защита от мобов"), Opt("bonusdamagevsmobs", "Damage vs mobs", "Урон по мобам"),
        Opt("crowdcontrolresistance", "CC resistance", "Сопр. контролю"), Opt("energycostreduction", "Energy cost reduction", "Снижение расхода энергии"),
        Opt("magiccooldownreduction", "Cooldown reduction", "Снижение перезарядки"),
    ];

    private static string StatEn(string type) => BuffStats.FirstOrDefault(o => o.Value == type)?.En.ToLowerInvariant() ?? type;
    private static string StatRu(string type) => BuffStats.FirstOrDefault(o => o.Value == type)?.Ru.ToLowerInvariant() ?? type;

    private static bool Hurts(XElement e) => Is(e, "attribute", "health") && Attr(e, "change") <= -1 && e.Attribute("changetype")?.Value is null or "absolute";
    private static bool Heals(XElement e) => Is(e, "attribute", "health") && Attr(e, "change") >= 1 && e.Attribute("changetype")?.Value is null or "absolute";

    private static RecipeField[] RootFields(string category, double cd, double energy, double cast, double? range)
    {
        var list = new List<RecipeField>
        {
            Fld("cooldown", "seconds", "Cooldown", "Перезарядка", Num(cd), 0, 600, required: true, range: r => SpellRoot(r.Ctx, "recastdelay", category)),
            Fld("energy", "number", "Energy cost", "Расход энергии", Num(energy), 0, 200, range: r => SpellRoot(r.Ctx, "energyusage", category)),
            Fld("castTime", "seconds", "Cast time", "Время каста", Num(cast), 0, 10, range: r => SpellRoot(r.Ctx, "castingtime", category)),
        };
        if (range is { } rg) list.Add(Fld("range", "number", "Range", "Дальность", Num(rg), 0, 40, suffix: "m", range: r => SpellRoot(r.Ctx, "castrange", category)));
        return [.. list];
    }

    private static RecipeField Amount(string key, string en, string ru, double def, string element, string attr, string memo, Func<XElement, bool> where, double max = 100000, string type = "number") =>
        Fld(key, type, en, ru, Num(def), 0, max, required: true, range: r => BlockValues(r.Ctx, element, attr, memo, where));

    private static RecipeField[] Head(string id, string en, string ru) => [IdField(id, false), NameField(en, ru)];

    /// <summary>The spell draft of a recipe: the template, its root numbers, the name and the goal.</summary>
    private static JsonObject NewSpell(RecipeRun r, string template)
    {
        var src = r.Ctx.FindSpell(template)?.El ?? throw new InvalidDataException($"the official spell {template} is missing from this client");
        var d = new SpellKind().NewFrom(r.Ctx, template, r.S("base"));
        var (en, ru) = r.L("name");
        d["name"] = J.LocNode(en, ru);
        foreach (var (key, attr) in new[] { ("cooldown", "recastdelay"), ("energy", "energyusage"), ("castTime", "castingtime"), ("range", "castrange") })
            if (r.Opt(key) is { } v && src.Attribute(attr) is not null) SetAttr(d, src, attr, v);
        d["goal"] = r.Goal();
        return d;
    }

    private static void Done(RecipeRun r, JsonObject d, string en, string ru)
    {
        d["description"] = J.LocNode(en, ru);
        r.Add(d);
    }

    private static string Pct(double v) => Fmt(Math.Round(Math.Abs(v) * 100, 1)) + "%";

    private static void RegisterSpells()
    {
        var order = 100;
        void Spell(string id, string icon, string en, string ru, string sumEn, string sumRu, string[] examples, RecipeField[] fields, Action<RecipeRun> build) =>
            Register(new StudioRecipe
            {
                Id = id, Kind = "spell", Icon = icon, TitleEn = en, TitleRu = ru, SummaryEn = sumEn, SummaryRu = sumRu, Examples = examples,
                Order = order++, Fields = fields, Build = build,
            });

        var dmgRange = (Func<XElement, bool>)(e => Hurts(e) && (e.Attribute("target")?.Value ?? "enemy").StartsWith("enemy", StringComparison.Ordinal));

        Spell("spell.direct", "🗡", "Direct damage", "Прямой урон", "One hit on one enemy.", "Один удар по одной цели.", ["THREATENINGSTRIKE_HAMMER"],
            [.. Head("MY_STRIKE", "Heavy strike", "Тяжёлый удар"),
                Amount("damage", "Damage", "Урон", 150, "directattributechange", "change", "dmg", dmgRange, 5000),
                Fld("type", "select", "Damage type", "Тип урона", JsonValue.Create("physical"), required: true, options: DamageTypes),
                .. RootFields("damage", 6, 8, 0, 3)],
            r =>
            {
                var d = NewSpell(r, "THREATENINGSTRIKE_HAMMER");
                RemoveBlock(d, "0");
                Edit(d, "1@change", -r.N("damage"));
                Edit(d, "1@effecttype", r.S("type"));
                var magic = r.S("type") == "magic";
                Done(r, d, $"Strikes the target for [dmg]{Fmt(r.N("damage"))}[/dmg] {(magic ? "magic" : "physical")} damage.",
                    $"Наносит цели [dmg]{Fmt(r.N("damage"))}[/dmg] {(magic ? "магического" : "физического")} урона.");
            });

        Spell("spell.area", "💥", "Area damage", "Урон по области", "Damage to every enemy in a circle at the target point.", "Урон всем врагам в круге в точке цели.", ["FLAMEPILLAR"],
            [.. Head("MY_BLAST", "Blast", "Взрыв"),
                Amount("damage", "Damage", "Урон", 200, "directattributechange", "change", "dmg", dmgRange, 5000),
                Fld("radius", "number", "Radius", "Радиус", Num(4), 1, 20, required: true, suffix: "m", range: r => BlockValues(r.Ctx, "directattributechange", "effectarearadius", "dmgradius", dmgRange)),
                Fld("type", "select", "Damage type", "Тип урона", JsonValue.Create("magic"), required: true, options: DamageTypes),
                .. RootFields("damage", 15, 9, 0, 15)],
            r =>
            {
                var d = NewSpell(r, "FLAMEPILLAR");
                var rad = r.N("radius", 4);
                RemoveBlock(d, "1/0"); // the official cooldown refund on hit
                Edit(d, "1@change", -r.N("damage"));
                Edit(d, "1@effecttype", r.S("type"));
                Edit(d, "1@effectarearadius", rad);
                Edit(d, "0/0/0/0@radius", rad);
                Edit(d, "0/0/1/0@radius", "0-" + Fmt(rad));
                Done(r, d, $"Deals [dmg]{Fmt(r.N("damage"))}[/dmg] {(r.S("type") == "magic" ? "magic" : "physical")} damage to all enemies within {Fmt(rad)} m of the target point.",
                    $"Наносит [dmg]{Fmt(r.N("damage"))}[/dmg] {(r.S("type") == "magic" ? "магического" : "физического")} урона всем врагам в радиусе {Fmt(rad)} м от точки.");
            });

        var dotRange = (Func<XElement, bool>)(e => Hurts(e) && (e.Attribute("target")?.Value ?? "").StartsWith("enemy", StringComparison.Ordinal));
        Spell("spell.dot", "☠", "Damage over time", "Урон со временем", "A projectile that hits, then hurts every tick.", "Снаряд: удар, затем урон каждый тик.", ["CURSEDOT", "CURSEDOT_EFFECT"],
            [.. Head("MY_CURSE", "Wasting curse", "Иссушающее проклятие"),
                Amount("hit", "Initial hit", "Первый удар", 20, "directattributechange", "change", "dmg", dmgRange, 5000),
                Amount("tick", "Damage per tick", "Урон за тик", 17, "attributechangeovertime", "change", "dot", dotRange, 2000),
                Fld("ticks", "int", "Ticks", "Тиков", Num(5), 1, 60, required: true, range: r => BlockValues(r.Ctx, "attributechangeovertime", "count", "dotcount", dotRange)),
                Fld("interval", "seconds", "Tick every", "Тик каждые", Num(2), 0.1, 10, required: true, range: r => BlockValues(r.Ctx, "attributechangeovertime", "interval", "dotint", dotRange)),
                .. RootFields("damage", 3, 4, 0, 9)],
            r =>
            {
                var d = NewSpell(r, "CURSEDOT");
                RemoveBlock(d, "1");
                Edit(d, "0@change", -r.N("hit"));
                AddBlock(d, "CURSEDOT_EFFECT", "0", ("change", Fmt(-r.N("tick"))), ("count", Fmt(r.I("ticks"))), ("interval", Fmt(r.N("interval"))));
                var total = r.N("tick") * r.I("ticks");
                Done(r, d, $"Hits for [dmg]{Fmt(r.N("hit"))}[/dmg], then [dmg]{Fmt(r.N("tick"))}[/dmg] every {Fmt(r.N("interval"))} s, {r.I("ticks")} times ({Fmt(total)} in total).",
                    $"Наносит [dmg]{Fmt(r.N("hit"))}[/dmg] урона, затем [dmg]{Fmt(r.N("tick"))}[/dmg] каждые {Fmt(r.N("interval"))} с, {r.I("ticks")} раз (всего {Fmt(total)}).");
            });

        var healRange = (Func<XElement, bool>)(e => Heals(e) && (e.Attribute("target")?.Value ?? "").StartsWith("friend", StringComparison.Ordinal));
        Spell("spell.heal", "✚", "Heal", "Лечение", "An instant heal on an ally (or yourself).", "Мгновенное лечение союзника (или себя).", ["HOLYHOT", "HOLYDESPERATEPRAYER2_EFFECT2"],
            [.. Head("MY_HEAL", "Mending light", "Исцеляющий свет"),
                Amount("heal", "Heal", "Лечение", 220, "directattributechange", "change", "heal", healRange, 5000),
                .. RootFields("heal", 10, 15, 0, 13)],
            r =>
            {
                var d = NewSpell(r, "HOLYHOT");
                SetAttr(d, "category", "heal");
                RemoveBlock(d, "0", "1", "2");
                AddBlock(d, "HOLYDESPERATEPRAYER2_EFFECT2", "0", ("change", Fmt(r.N("heal"))));
                Done(r, d, $"Heals the target for [heal]{Fmt(r.N("heal"))}[/heal].", $"Лечит цель на [heal]{Fmt(r.N("heal"))}[/heal].");
            });

        var hotRange = (Func<XElement, bool>)(e => Heals(e) && (e.Attribute("target")?.Value ?? "").StartsWith("friend", StringComparison.Ordinal));
        Spell("spell.hot", "❤", "Heal over time", "Лечение со временем", "Heals an ally every tick.", "Лечит союзника каждый тик.", ["HOLYHOT", "WELLOFLIFE2_EFFECT"],
            [.. Head("MY_RENEW", "Renewal", "Обновление"),
                Amount("tick", "Heal per tick", "Лечение за тик", 60, "attributechangeovertime", "change", "hot", hotRange, 2000),
                Fld("ticks", "int", "Ticks", "Тиков", Num(5), 1, 60, required: true, range: r => BlockValues(r.Ctx, "attributechangeovertime", "count", "hotcount", hotRange)),
                Fld("interval", "seconds", "Tick every", "Тик каждые", Num(1), 0.1, 10, required: true),
                .. RootFields("heal", 12, 10, 0, 13)],
            r =>
            {
                var d = NewSpell(r, "HOLYHOT");
                SetAttr(d, "category", "heal");
                RemoveBlock(d, "0", "1", "2");
                AddBlock(d, "WELLOFLIFE2_EFFECT", "0", ("change", Fmt(r.N("tick"))), ("count", Fmt(r.I("ticks"))), ("interval", Fmt(r.N("interval"))));
                Done(r, d, $"Heals the target for [heal]{Fmt(r.N("tick"))}[/heal] every {Fmt(r.N("interval"))} s, {r.I("ticks")} times ({Fmt(r.N("tick") * r.I("ticks"))} in total).",
                    $"Лечит цель на [heal]{Fmt(r.N("tick"))}[/heal] каждые {Fmt(r.N("interval"))} с, {r.I("ticks")} раз (всего {Fmt(r.N("tick") * r.I("ticks"))}).");
            });

        Spell("spell.shield", "🔰", "Shield", "Щит", "Absorbs damage on an ally for a while.", "Поглощает урон по союзнику на время.", ["SHIELDFRIENDLY"],
            [.. Head("MY_BARRIER", "Barrier", "Барьер"),
                Amount("absorb", "Absorbs", "Поглощает", 300, "damageshield", "damageabsorbed", "shield", _ => true, 20000),
                Fld("duration", "seconds", "Duration", "Длительность", Num(3), 0.5, 60, required: true, range: r => BlockValues(r.Ctx, "damageshield", "time", "shieldtime", _ => true)),
                .. RootFields("buff_damageshield", 6, 6, 0, 11)],
            r =>
            {
                var d = NewSpell(r, "SHIELDFRIENDLY");
                Edit(d, "0@damageabsorbed", r.N("absorb"));
                foreach (var p in new[] { "0", "1", "2" }) Edit(d, p + "@time", r.N("duration"));
                Done(r, d, $"Shields the target: absorbs [heal]{Fmt(r.N("absorb"))}[/heal] damage for {Fmt(r.N("duration"))} s.",
                    $"Щит на цели: поглощает [heal]{Fmt(r.N("absorb"))}[/heal] урона в течение {Fmt(r.N("duration"))} с.");
            });

        Spell("spell.buff", "⬆", "Buff a stat", "Усиление", "Raises one stat of an ally for a while.", "Повышает характеристику союзника на время.", ["HOLYHOT", "HASTE"],
            [.. Head("MY_BLESSING", "Blessing", "Благословение"),
                Fld("stat", "select", "Stat", "Характеристика", JsonValue.Create("physicalattackdamagebonus"), required: true, options: BuffStats),
                Fld("value", "percent", "Bonus", "Бонус", Num(0.2), 0.01, 5, required: true,
                    range: r => BlockValues(r.Ctx, "buffovertime", "value", "buff:" + r.S("stat"), e => Is(e, "type", r.S("stat")) && Attr(e, "value") > 0 && Attr(e, "time") > 0)),
                Fld("duration", "seconds", "Duration", "Длительность", Num(8), 0.5, 600, required: true),
                .. RootFields("buff", 20, 8, 0, 13)],
            r =>
            {
                var d = NewSpell(r, "HOLYHOT");
                RemoveBlock(d, "1", "2");
                Edit(d, "0@type", r.S("stat"));
                Edit(d, "0@value", r.N("value"));
                Edit(d, "0@time", r.N("duration"));
                Done(r, d, $"Increases the target's {StatEn(r.S("stat"))} by {Pct(r.N("value"))} for {Fmt(r.N("duration"))} s.",
                    $"Повышает цели {StatRu(r.S("stat"))} на {Pct(r.N("value"))} на {Fmt(r.N("duration"))} с.");
            });

        var slowRange = (Func<XElement, bool>)(e => Is(e, "type", "movespeedbonus") && Attr(e, "value") < 0);
        Spell("spell.slow", "🐌", "Slow", "Замедление", "A hit that slows the enemy.", "Удар, замедляющий врага.", ["THREATENINGSTRIKE_HAMMER", "HAMSTRINGSWORD_EFFECT"],
            [.. Head("MY_HOBBLE", "Hobble", "Подсечка"),
                Amount("damage", "Damage", "Урон", 80, "directattributechange", "change", "dmg", dmgRange, 5000),
                Fld("slow", "percent", "Slow", "Замедление", Num(0.3), 0.05, 0.95, required: true, range: r => BlockValues(r.Ctx, "buffovertime", "value", "slow", slowRange)),
                Fld("duration", "seconds", "Duration", "Длительность", Num(3), 0.5, 30, required: true, range: r => BlockValues(r.Ctx, "buffovertime", "time", "slowtime", slowRange)),
                .. RootFields("crowdcontrol", 8, 6, 0, 3)],
            r =>
            {
                var d = NewSpell(r, "THREATENINGSTRIKE_HAMMER");
                RemoveBlock(d, "0");
                Edit(d, "1@change", -r.N("damage"));
                AddBlock(d, "HAMSTRINGSWORD_EFFECT", "1", ("value", Fmt(-r.N("slow"))), ("time", Fmt(r.N("duration"))));
                Done(r, d, $"Deals [dmg]{Fmt(r.N("damage"))}[/dmg] physical damage and slows the target by {Pct(r.N("slow"))} for {Fmt(r.N("duration"))} s.",
                    $"Наносит [dmg]{Fmt(r.N("damage"))}[/dmg] физического урона и замедляет цель на {Pct(r.N("slow"))} на {Fmt(r.N("duration"))} с.");
            });

        RecipeOption[] controls = [Opt("stun", "Stun", "Оглушение"), Opt("root", "Root", "Обездвиживание"), Opt("silence", "Silence", "Безмолвие")];
        Spell("spell.control", "🌀", "Stun, root or silence", "Оглушение, корни, безмолвие", "A hit that takes control away from the enemy.", "Удар, лишающий врага контроля.",
            ["KNOCKOUT", "BEAR_GROUND_SMASH_STUN", "GROUNDARROW_ROOT", "PASSIVE_SILENCECHANCE_EFFECT"],
            [.. Head("MY_BASH", "Bash", "Оглушающий удар"),
                Fld("control", "select", "Effect", "Эффект", JsonValue.Create("stun"), required: true, options: controls),
                Amount("damage", "Damage", "Урон", 60, "directattributechange", "change", "dmg", dmgRange, 5000),
                Fld("duration", "seconds", "Duration", "Длительность", Num(1.5), 0.1, 30, required: true, range: r => BlockValues(r.Ctx, r.S("control"), "time", "cc:" + r.S("control"), e => Attr(e, "time") < 20)),
                .. RootFields("crowdcontrol", 15, 12, 0, 3)],
            r =>
            {
                var d = NewSpell(r, "THREATENINGSTRIKE_HAMMER");
                RemoveBlock(d, "0");
                Edit(d, "1@change", -r.N("damage"));
                var (from, en, ru) = r.S("control") switch
                {
                    "root" => ("GROUNDARROW_ROOT", "roots", "обездвиживает"),
                    "silence" => ("PASSIVE_SILENCECHANCE_EFFECT", "silences", "накладывает безмолвие на"),
                    _ => ("BEAR_GROUND_SMASH_STUN", "stuns", "оглушает"),
                };
                AddBlock(d, from, "0", ("time", Fmt(r.N("duration"))));
                Done(r, d, $"Deals [dmg]{Fmt(r.N("damage"))}[/dmg] physical damage and {en} the target for {Fmt(r.N("duration"))} s.",
                    $"Наносит [dmg]{Fmt(r.N("damage"))}[/dmg] физического урона и {ru} цель на {Fmt(r.N("duration"))} с.");
            });

        Spell("spell.dash", "💨", "Dash", "Рывок", "A dash to the target point, then a short damage buff.", "Рывок к точке, затем короткое усиление урона.", ["GROUNDDASH"],
            [.. Head("MY_DASH", "Swift dash", "Быстрый рывок"),
                Fld("speed", "number", "Dash speed", "Скорость рывка", Num(30), 5, 80, required: true, suffix: "m/s", range: r => BlockValues(r.Ctx, "dash", "movespeed", "dash", _ => true)),
                .. RootFields("movementbuff", 15, 0, 0, 8)],
            r =>
            {
                var d = NewSpell(r, "GROUNDDASH");
                Edit(d, "0@movespeed", r.N("speed"));
                Done(r, d, $"Dashes up to {Fmt(r.N("range", 8))} m to the target point; afterwards the damage dealt rises by 15 % for 4 s.",
                    $"Рывок до {Fmt(r.N("range", 8))} м к точке; затем урон повышен на 15 % на 4 с.");
            });

        RecipeOption[] knocks = [Opt("knockback", "Knock back", "Отбросить"), Opt("pull", "Pull in", "Притянуть")];
        Spell("spell.knock", "↔", "Knockback or pull", "Отбрасывание или притягивание", "Hits the enemies around you and pushes them away or pulls them in.",
            "Бьёт врагов вокруг и отбрасывает их или притягивает к себе.", ["PBAOE_KNOCKBACK", "PBAOE_PULL"],
            [.. Head("MY_SHOCKWAVE", "Shockwave", "Ударная волна"),
                Fld("mode", "select", "Effect", "Эффект", JsonValue.Create("knockback"), required: true, options: knocks),
                Amount("damage", "Damage", "Урон", 70, "directattributechange", "change", "dmg", dmgRange, 5000),
                Fld("radius", "number", "Radius", "Радиус", Num(6), 1, 20, required: true, suffix: "m"),
                Fld("distance", "number", "Knockback distance", "Дальность отбрасывания", Num(9), 1, 30, required: true, suffix: "m", show: "mode=knockback",
                    range: r => BlockValues(r.Ctx, "knockback", "distance", "knock", _ => true)),
                .. RootFields("crowdcontrol", 30, 15, 0, null)],
            r =>
            {
                var pull = r.S("mode") == "pull";
                var d = NewSpell(r, pull ? "PBAOE_PULL" : "PBAOE_KNOCKBACK");
                var rad = Fmt(r.N("radius", 6));
                RemoveBlock(d, "1");
                if (pull)
                {
                    AddBlock(d, "PBAOE_PULL_EFFECT", "0", ("effectarearadius", rad));
                    AddBlock(d, "PBAOE_PULL_EFFECT", "2", ("change", Fmt(-r.N("damage"))), ("effectarearadius", rad));
                    Done(r, d, $"Pulls all enemies within {rad} m to you and deals [dmg]{Fmt(r.N("damage"))}[/dmg] magic damage.",
                        $"Притягивает к вам всех врагов в радиусе {rad} м и наносит [dmg]{Fmt(r.N("damage"))}[/dmg] магического урона.");
                }
                else
                {
                    Edit(d, "0@change", -r.N("damage"));
                    Edit(d, "0@effectarearadius", rad);
                    AddBlock(d, "PBAOE_KNOCKBACK_KNOCKBACK", "0", ("distance", Fmt(r.N("distance", 9))), ("effectarearadius", rad));
                    Done(r, d, $"Deals [dmg]{Fmt(r.N("damage"))}[/dmg] magic damage to all enemies within {rad} m and knocks them back {Fmt(r.N("distance", 9))} m.",
                        $"Наносит [dmg]{Fmt(r.N("damage"))}[/dmg] магического урона всем врагам в радиусе {rad} м и отбрасывает их на {Fmt(r.N("distance", 9))} м.");
                }
            });

        Spell("spell.summon", "🐺", "Summon", "Призыв", "Calls a creature that fights for you for a while.", "Призывает существо, которое сражается за вас.", ["SUMMON_FOX"],
            [.. Head("MY_SUMMON", "Call of the wild", "Зов дикой природы"),
                Fld("mob", "mob", "Creature", "Существо", JsonValue.Create("SUMMON_FOX"), required: true, hintEn: "a mobs.xml mob (summons: SUMMON_*)", hintRu: "моб из mobs.xml (призывы: SUMMON_*)"),
                Fld("count", "int", "At most at once", "Не больше одновременно", Num(1), 1, 10, required: true),
                Fld("lifetime", "seconds", "Lifetime", "Время жизни", Num(60), 5, 3600, required: true, range: r => BlockValues(r.Ctx, "spawnmob", "ttl", "summon", e => Attr(e, "ttl") < 3600)),
                .. RootFields("instant", 30, 10, 0, 11)],
            r =>
            {
                var d = NewSpell(r, "SUMMON_FOX");
                Edit(d, "1@type", r.S("mob"));
                Edit(d, "1@limit", r.I("count", 1));
                Edit(d, "1@ttl", r.N("lifetime", 60));
                var (men, mru) = r.Ctx.Catalog.MobEls.TryGetValue(r.S("mob"), out var mob) ? r.Ctx.Catalog.Loc.GetValueOrDefault(ModCatalog.MobNameKey(mob)) : (null, null);
                Done(r, d, $"Summons {men ?? r.S("mob")} for {Fmt(r.N("lifetime"))} s (at most {r.I("count")} at once).",
                    $"Призывает {mru ?? men ?? r.S("mob")} на {Fmt(r.N("lifetime"))} с (не больше {r.I("count")} одновременно).");
            });

        var passiveStats = BuffStats.Append(Opt("none", "— none —", "— нет —")).ToArray();
        Spell("spell.passive", "♾", "Passive", "Пассивка", "An always-on bonus to one or two stats.", "Постоянный бонус к одной-двум характеристикам.", ["PASSIVE_MR_AR", "PASSIVE_INCREASED_DAMAGE"],
            [.. Head("PASSIVE_MY_BONUS", "Hardened", "Закалка"),
                Fld("stat", "select", "Stat", "Характеристика", JsonValue.Create("physicalarmor"), required: true, options: BuffStats),
                Fld("value", "percent", "Bonus", "Бонус", Num(0.05), 0.001, 3, required: true,
                    range: r => BlockValues(r.Ctx, "buff", "value", "passive:" + r.S("stat"), e => Is(e, "type", r.S("stat")) && Attr(e, "value") > 0)),
                Fld("stat2", "select", "Second stat", "Вторая характеристика", JsonValue.Create("none"), options: passiveStats),
                Fld("value2", "percent", "Second bonus", "Второй бонус", Num(0.05), 0.001, 3, show: "stat2=" + string.Join("|", BuffStats.Select(o => o.Value)))],
            r =>
            {
                var d = NewSpell(r, "PASSIVE_MR_AR");
                Edit(d, "0@type", r.S("stat"));
                Edit(d, "0@value", r.N("value"));
                var two = r.S("stat2") is { Length: > 0 } s2 && s2 != "none";
                if (two) { Edit(d, "1@type", r.S("stat2")); Edit(d, "1@value", r.N("value2")); }
                else RemoveBlock(d, "1");
                Done(r, d, $"Permanently increases {StatEn(r.S("stat"))} by {Pct(r.N("value"))}{(two ? $" and {StatEn(r.S("stat2"))} by {Pct(r.N("value2"))}" : "")}.",
                    $"Постоянно повышает {StatRu(r.S("stat"))} на {Pct(r.N("value"))}{(two ? $" и {StatRu(r.S("stat2"))} на {Pct(r.N("value2"))}" : "")}.");
            });

        RecipeOption[] fields = [Opt("damage", "Hurts enemies", "Ранит врагов"), Opt("heal", "Heals allies", "Лечит союзников")];
        var fieldTick = (Func<RecipeRun, Func<XElement, bool>>)(r => r.S("effect") == "heal" ? hotRange : dotRange);
        Spell("spell.field", "🔥", "Ground field", "Поле на земле", "A circle on the ground that ticks on everyone inside.", "Круг на земле, действующий каждый тик на всех внутри.",
            ["GLACIALFIELD", "GLACIALFIELD_EFFECT", "WELLOFLIFE2", "WELLOFLIFE2_EFFECT"],
            [.. Head("MY_FIELD", "Burning ground", "Горящая земля"),
                Fld("effect", "select", "Field", "Поле", JsonValue.Create("damage"), required: true, options: fields),
                Fld("tick", "number", "Per tick", "За тик", Num(25), 1, 2000, required: true,
                    range: r => BlockValues(r.Ctx, "attributechangeovertime", "change", r.S("effect") == "heal" ? "hot" : "dot", fieldTick(r))),
                Fld("ticks", "int", "Ticks", "Тиков", Num(6), 1, 60, required: true),
                Fld("interval", "seconds", "Tick every", "Тик каждые", Num(1), 0.25, 10, required: true),
                Fld("radius", "number", "Radius", "Радиус", Num(6), 1, 20, required: true, suffix: "m"),
                Fld("duration", "seconds", "Field lasts", "Поле держится", Num(5), 1, 60, required: true),
                Fld("slow", "percent", "Slow inside", "Замедление внутри", Num(0), 0, 0.9, show: "effect=damage"),
                .. RootFields("damage", 30, 20, 0, 14)],
            r =>
            {
                var heal = r.S("effect") == "heal";
                var d = NewSpell(r, heal ? "WELLOFLIFE2" : "GLACIALFIELD");
                var rad = r.N("radius", 6);
                var tickId = r.S("base") + "_TICK";
                Edit(d, "0/0/0/0@radius", rad);
                Edit(d, "0/0/1/0@radius", "0-" + Fmt(rad));
                if (heal)
                {
                    Edit(d, "1@effectarearadius", rad);
                    Edit(d, "1/0/0/0@radius", rad);
                    Edit(d, "1@time", r.N("duration"));
                    Edit(d, "1@effect", tickId);
                }
                else
                {
                    RemoveBlock(d, "3");
                    Edit(d, "1@effectarearadius", rad);
                    Edit(d, "2/0/0/0/0@radius", rad);
                    Edit(d, "2@time", r.N("duration"));
                    Edit(d, "2@effect", tickId);
                }
                // The tick: a spell of its own, as the official fields have (…_EFFECT).
                var tick = new SpellKind().NewFrom(r.Ctx, heal ? "WELLOFLIFE2_EFFECT" : "GLACIALFIELD_EFFECT", tickId);
                tick["name"] = J.LocNode(J.Loc(d["name"]).En, J.Loc(d["name"]).Ru);
                var block = heal ? "0" : "1";
                Edit(tick, block + "@change", heal ? r.N("tick") : -r.N("tick"));
                Edit(tick, block + "@count", r.I("ticks"));
                Edit(tick, block + "@interval", r.N("interval"));
                if (!heal)
                {
                    Edit(tick, "0@value", -r.N("slow"));
                    Edit(tick, "0@time", r.N("duration"));
                }
                tick["description"] = J.LocNode($"The tick of {r.S("base")}.", $"Тик поля {r.S("base")}.");
                r.Add(tick);
                var total = r.N("tick") * r.I("ticks");
                Done(r, d, heal
                        ? $"Creates a {Fmt(rad)} m field for {Fmt(r.N("duration"))} s that heals allies inside for [heal]{Fmt(r.N("tick"))}[/heal] every {Fmt(r.N("interval"))} s ({Fmt(total)} in total)."
                        : $"Creates a {Fmt(rad)} m field for {Fmt(r.N("duration"))} s that deals [dmg]{Fmt(r.N("tick"))}[/dmg] magic damage every {Fmt(r.N("interval"))} s to enemies inside ({Fmt(total)} in total){(r.N("slow") > 0 ? $" and slows them by {Pct(r.N("slow"))}" : "")}.",
                    heal
                        ? $"Создаёт поле радиусом {Fmt(rad)} м на {Fmt(r.N("duration"))} с: лечит союзников внутри на [heal]{Fmt(r.N("tick"))}[/heal] каждые {Fmt(r.N("interval"))} с (всего {Fmt(total)})."
                        : $"Создаёт поле радиусом {Fmt(rad)} м на {Fmt(r.N("duration"))} с: наносит врагам внутри [dmg]{Fmt(r.N("tick"))}[/dmg] магического урона каждые {Fmt(r.N("interval"))} с (всего {Fmt(total)}){(r.N("slow") > 0 ? $" и замедляет на {Pct(r.N("slow"))}" : "")}.");
            });
    }
}
