using System.Text.Json.Nodes;

namespace AlbionLocal.Server.Rules;

/// <summary>
/// The palette of the no-code rules (server/plugins/RULES.md): every trigger, condition, action and target a rule may name, with typed
/// parameters and RU/EN labels. The validator (<see cref="RuleParser"/>), the engine (<see cref="RuleEngine"/>) and the Studio's editor
/// (GET /studio/api/rules/palette) all read this one table, so a new entry here appears everywhere.
/// </summary>
public static class RuleSchema
{
    /// <summary>A parameter: name, field type (text, int, number, seconds, bool, select, flags, item, spell, mob, buff, mount, cluster, time, list, textarea), labels.</summary>
    public sealed record Param(string Name, string Type, string En, string Ru, bool Required = false, object? Default = null, string[]? Options = null,
        double? Min = null, double? Max = null, string? Hint = null, string? HintRu = null);

    /// <summary>What a trigger's event offers: a changeable number (modify), a refusal (cancel), a skippable default (prevent), the rolled loot, a command call.</summary>
    [Flags]
    public enum Feature
    {
        None = 0,
        Amount = 1,
        Cancel = 2,
        Prevent = 4,
        Loot = 8,
        Command = 16,
        Timer = 32,
    }

    public sealed record TriggerDef(string Type, string Group, string En, string Ru, string? IdKind, string[] Targets, Feature Features, string[] Flags,
        Param[] Params, string? AmountEn = null, string? AmountRu = null);

    public sealed record StepDef(string Type, string Group, string En, string Ru, Param[] Params, bool Who = false, Feature Needs = Feature.None, bool Targeted = false);

    // ---- shared parameter shapes

    /// <summary>A slot of the equipped items (<see cref="Systems.SlotSpells.SlotOptions"/>: Q W E R D F, Potion, Food, the passive slots).</summary>
    private static Param SlotParam() => new("slot", "select", "Slot", "Слот", true, "Q", Systems.SlotSpells.SlotOptions,
        Hint: "R = armour, D = helmet, F = shoes; …Passive = the item's passive", HintRu: "R — броня, D — шлем, F — обувь; …Passive — пассивка предмета");

    private static readonly string[] Ops = ["<", "<=", "=", ">=", ">", "!="];
    private static Param Op(string def = ">=") => new("op", "select", "Compare", "Сравнение", Default: def, Options: Ops);

    public static readonly string[] AllTargets = ["self", "killer", "victim", "caster", "target", "mob", "party", "cluster", "radius", "all"];

    /// <summary>The target selectors with their labels.</summary>
    public static readonly (string Value, string En, string Ru)[] TargetLabels =
    [
        ("self", "the player of the event (self)", "игрок события (сам)"),
        ("killer", "the killer", "убийца"),
        ("victim", "the victim", "жертва"),
        ("caster", "the caster / attacker", "заклинатель / атакующий"),
        ("target", "the target", "цель"),
        ("mob", "the mob", "моб"),
        ("party", "the party (self and members online)", "группа (сам и члены группы онлайн)"),
        ("cluster", "everyone in the cluster", "все в кластере"),
        ("radius", "players in a radius", "игроки в радиусе"),
        ("all", "everyone online", "все онлайн"),
    ];

    private static readonly string[] PlayerTargets = ["self", "party", "cluster", "radius", "all"];
    private static string[] With(params string[] extra) => [.. extra, .. PlayerTargets.Where(t => !extra.Contains(t))];

    // ---- triggers

    public static readonly TriggerDef[] Triggers =
    [
        // players
        new("player.login", "player", "Player logs in", "Игрок входит в игру", null, PlayerTargets, Feature.None, ["firstLogin"], []),
        new("player.logout", "player", "Player logs out", "Игрок выходит из игры", null, ["self", "cluster", "all"], Feature.None, [], []),
        new("zone.enter", "player", "Player enters a cluster", "Игрок входит в кластер", "cluster", PlayerTargets, Feature.None, ["clusterChange"], []),
        new("zone.leave", "player", "Player leaves a cluster", "Игрок покидает кластер", "cluster", PlayerTargets, Feature.None, ["clusterChange"], []),
        new("player.knockdown", "player", "Player is knocked down", "Игрок повержен (нокдаун)", null, With("self", "victim", "killer"), Feature.None, [], []),
        new("player.died", "player", "Player dies", "Игрок погибает", null, With("self", "victim", "killer"), Feature.None, [], []),
        new("player.revive", "player", "Player gets up / is revived", "Игрок поднимается / воскрешён", null, PlayerTargets, Feature.None, [], []),
        new("combat.enter", "player", "Player enters combat", "Игрок вступает в бой", null, PlayerTargets, Feature.None, [], []),
        new("combat.leave", "player", "Player leaves combat", "Игрок выходит из боя", null, PlayerTargets, Feature.None, [], []),
        new("health.below", "player", "Health falls below %", "Здоровье падает ниже %", "mob", With("self", "caster"), Feature.None, ["mob"],
            [new("percent", "number", "Health below, %", "Здоровье ниже, %", true, 30, Min: 1, Max: 99, Hint: "players: leave the id empty; a mob's uniquename for mobs", HintRu: "игроки: id пустой; uniquename моба — для мобов")]),
        new("chat", "player", "Player says something (chat)", "Игрок пишет в чат", "text", PlayerTargets, Feature.None, [], []),
        new("command", "player", "Custom chat command", "Своя команда чата", null, PlayerTargets, Feature.Command, [],
            [new("command", "text", "Command (without the dot)", "Команда (без точки)", true, "home", Hint: "a word or two: .home, .event join", HintRu: "одно-два слова: .home, .event join"),
             new("level", "select", "Who may use it", "Кто может использовать", false, "Player", ["Player", "GameMaster", "Admin"]),
             new("help", "text", "Help line", "Описание в .help")]),
        new("party.joined", "player", "Player joins a party", "Игрок вступает в группу", null, PlayerTargets, Feature.None, [], []),
        new("party.left", "player", "Player leaves a party", "Игрок покидает группу", null, PlayerTargets, Feature.None, [], []),
        new("goal.completed", "player", "Goal completed (bounty, expedition, corrupted)", "Цель выполнена (баунти, экспедиция, осквернённое)", "text", PlayerTargets, Feature.None, [], []),

        // mobs and loot
        new("mob.died", "mob", "Mob is killed", "Моб убит", "mob", With("self", "killer", "victim", "mob"), Feature.None, ["boss", "event"], []),
        new("mob.spawned", "mob", "Mob appears", "Моб появляется", "mob", ["mob", "self", "cluster", "radius", "all"], Feature.None, ["boss", "event"], []),
        new("loot.rolled", "mob", "Mob's loot is rolled", "Выпадает добыча моба", "mob", With("self", "killer", "victim", "mob"), Feature.Loot, [], []),
        new("damage", "mob", "Any hit (auto-attacks too)", "Любой удар (и автоатаки)", null, With("self", "caster", "target"), Feature.Amount, ["overTime", "pvp", "toMob", "toPlayer"], [],
            "the hit", "урон удара"),

        // items
        new("item.used", "item", "Item is used (potion, food…)", "Использован предмет (зелье, еда…)", "item", PlayerTargets, Feature.None, [], []),
        new("item.crafted", "item", "Item is crafted / bought", "Предмет создан / куплен", "item", PlayerTargets, Feature.None, [], []),
        new("item.equip", "item", "Item is equipped", "Предмет надет", "item", PlayerTargets, Feature.None, ["atLogin", "weapon"], []),
        new("item.unequip", "item", "Item is taken off", "Предмет снят", "item", PlayerTargets, Feature.None, ["weapon"], []),
        new("item.break", "item", "Item breaks (durability 0)", "Предмет сломался", "item", PlayerTargets, Feature.None, [], []),
        new("harvest", "item", "Resource harvested / fish caught", "Собран ресурс / поймана рыба", "item", PlayerTargets, Feature.None, ["fish"], []),

        // spells
        new("spell.check", "spell", "Spell is about to be cast (can refuse)", "Заклинание вот-вот будет применено (можно запретить)", "spell", With("self", "caster", "target"), Feature.Cancel, ["mount"], []),
        new("spell.cast", "spell", "Spell is cast", "Заклинание применено", "spell", With("self", "caster", "target"), Feature.None, ["mob", "mount"], []),
        new("spell.hit", "spell", "Spell hits", "Заклинание попадает", "spell", With("self", "caster", "target"), Feature.Prevent, ["projectile"], []),
        new("spell.damage", "spell", "Spell damages / heals a unit", "Заклинание наносит урон / лечит", "spell", With("self", "caster", "target"), Feature.Amount | Feature.Prevent,
            ["heal", "overTime", "area", "magic", "toMob", "toPlayer"], [], "the damage or heal", "урон или лечение"),
        new("spell.cancel", "spell", "Cast is interrupted / cancelled", "Каст прерван / отменён", "spell", With("self", "caster", "target"), Feature.None, ["interrupted"], []),
        new("slot.changed", "spell", "A slot gets another spell (choice, rule, plugin, end of a temporary one)", "В слоте сменилась способность (выбор, правило, плагин, конец временной)",
            "spell", PlayerTargets, Feature.None, ["byPlayer", "byPlugin", "revert", "temporary"], []),
        new("extrabar.changed", "spell", "An extra bar slot gets another spell (gear, rule, plugin)", "В доп. панели сменилась способность (экипировка, правило, плагин)",
            "spell", PlayerTargets, Feature.None, ["byGear", "byPlugin", "emptied"], []),

        // buffs
        new("buff.check", "buff", "Buff is about to be applied (can refuse)", "Бафф вот-вот наложится (можно запретить)", "buff", With("self", "caster"), Feature.Cancel, ["mob"], []),
        new("buff.applied", "buff", "Buff is applied", "Бафф наложен", "buff", With("self", "caster"), Feature.None, ["new", "mob"], []),
        new("buff.removed", "buff", "Buff ends", "Бафф закончился", "buff", With("self", "caster"), Feature.None, ["expired", "removed", "cleared", "mob"], []),
        new("buff.tick", "buff", "Buff ticks (damage/heal over time)", "Тик баффа (урон/лечение со временем)", "buff", With("self", "caster"), Feature.None, ["mob"], []),

        // mounts
        new("mount.check", "mount", "Player tries to mount (can refuse)", "Игрок садится на маунта (можно запретить)", "mount", PlayerTargets, Feature.Cancel, ["remount"], []),
        new("mount.mounted", "mount", "Player is mounted", "Игрок сел на маунта", "mount", PlayerTargets, Feature.None, [], []),
        new("mount.dismount", "mount", "Player dismounts", "Игрок спешился", "mount", PlayerTargets, Feature.None, ["died", "knockdown"], []),
        new("mount.damage", "mount", "Mount is hit", "Удар по маунту", "mount", With("self", "caster"), Feature.Amount, [], [], "the hit on the mount", "урон по маунту"),

        // formulas
        new("calc.cooldown", "formula", "Cooldown of a spell", "Перезарядка заклинания", "spell", With("self", "caster", "target"), Feature.Amount, [], [], "the cooldown (s)", "перезарядка (с)"),
        new("calc.casttime", "formula", "Cast time of a spell", "Время каста", "spell", With("self", "caster", "target"), Feature.Amount, [], [], "the cast time (s)", "время каста (с)"),
        new("calc.energy", "formula", "Energy cost of a spell", "Стоимость энергии", "spell", With("self", "caster", "target"), Feature.Amount, [], [], "the energy cost", "стоимость энергии"),
        new("calc.heal", "formula", "Heal of a spell", "Лечение заклинания", "spell", With("self", "caster", "target"), Feature.Amount, [], [], "the heal", "лечение"),
        new("calc.fame", "formula", "Fame of a kill", "Слава за убийство", "mob", With("self", "mob", "victim"), Feature.Amount, ["boss"], [], "the fame", "слава"),
        new("calc.silver", "formula", "Silver a mob drops", "Серебро с моба", "mob", With("self", "mob", "victim"), Feature.Amount, [], [], "the silver", "серебро"),
        new("calc.threat", "formula", "Threat of a hit on a mob", "Угроза удара по мобу", "mob", With("self", "mob", "target"), Feature.Amount, [], [], "the threat", "угроза"),

        // time and chains
        new("timer.every", "time", "Every N seconds", "Каждые N секунд", null, ["self", "all"], Feature.Timer, [],
            [new("seconds", "seconds", "Every", "Каждые", true, 600, Min: 1, Max: 86400),
             new("eachPlayer", "bool", "Once for each online player (self = that player)", "Для каждого игрока онлайн (сам = этот игрок)", false, false)]),
        new("timer.daily", "time", "Every day at a time", "Каждый день в заданное время", null, ["self", "all"], Feature.Timer, [],
            [new("time", "time", "Time (server clock, HH:MM)", "Время (часы сервера, ЧЧ:ММ)", true, "20:00"),
             new("eachPlayer", "bool", "Once for each online player (self = that player)", "Для каждого игрока онлайн (сам = этот игрок)", false, false)]),
        new("chain", "time", "Called by another rule (a named chain)", "Вызвано другим правилом (цепочка)", "chain", AllTargets, Feature.None, [], []),
    ];

    // ---- conditions (who = the unit the condition looks at: self by default)

    public static readonly StepDef[] Conditions =
    [
        new("chance", "general", "Random chance", "Случайный шанс", [new("percent", "number", "Chance, %", "Шанс, %", true, 50, Min: 0, Max: 100)]),
        new("account.level", "player", "Account level at least", "Уровень аккаунта не ниже", [new("min", "select", "Level", "Уровень", true, "GameMaster", ["Player", "GameMaster", "Admin"])], Who: true),
        new("has.item", "player", "Has an item in the inventory", "Есть предмет в сумке", [new("item", "item", "Item", "Предмет", true), new("count", "int", "At least", "Не меньше", false, 1, Min: 1)], Who: true),
        new("equipped", "player", "Wears an item", "Надет предмет", [new("item", "item", "Item (* for any text)", "Предмет (* — любой текст)", true)], Who: true),
        new("has.buff", "unit", "Has a buff", "Есть бафф", [new("buff", "buff", "Buff", "Бафф", true)], Who: true),
        new("health", "unit", "Health %", "Здоровье %", [Op("<"), new("percent", "number", "Percent", "Процент", true, 50, Min: 0, Max: 100)], Who: true),
        new("in.combat", "unit", "Is in combat", "В бою", [], Who: true),
        new("slot.is", "player", "A slot holds a spell", "В слоте способность", [SlotParam(),
            new("spell", "spell", "Spell (* patterns, comma list)", "Способность (шаблоны *, список через запятую)", true)], Who: true),
        new("extrabar.has", "player", "The extra bars hold a spell", "В доп. панелях есть способность", [
            new("spell", "spell", "Spell (* patterns, comma list)", "Способность (шаблоны *, список через запятую)", true),
            new("slot", "int", "Slot (0 = any, 1-12)", "Слот (0 — любой, 1-12)", false, 0, Min: 0, Max: 12)], Who: true),
        new("unit.kind", "unit", "Is a player / a mob", "Это игрок / моб", [new("kind", "select", "Kind", "Вид", true, "player", ["player", "mob"])], Who: true),
        new("silver", "player", "Silver", "Серебро", [Op(), new("value", "number", "Silver", "Серебро", true, 1000, Min: 0)], Who: true),
        new("party.size", "player", "Party size", "Размер группы", [Op(), new("value", "int", "Members", "Участников", true, 2, Min: 1, Max: 20)], Who: true),
        new("cluster", "place", "In a cluster", "В кластере", [new("clusters", "list", "Cluster ids (world.xml, comma separated)", "Id кластеров (world.xml, через запятую)", true)], Who: true),
        new("zone.color", "place", "Zone colour", "Цвет зоны", [new("colors", "flags", "Colours", "Цвета", true, null, ["safe", "yellow", "orange", "red", "black"])], Who: true),
        new("time", "time", "Time of day (server clock)", "Время суток (часы сервера)",
            [new("from", "time", "From", "С", true, "20:00"), new("to", "time", "To", "До", true, "06:00", Hint: "a range over midnight works (20:00-06:00 = night)", HintRu: "диапазон через полночь работает (20:00-06:00 = ночь)")]),
        new("weekday", "time", "Day of the week", "День недели", [new("days", "flags", "Days", "Дни", true, null, ["mon", "tue", "wed", "thu", "fri", "sat", "sun"])]),
        new("mob.tier", "mob", "Mob tier", "Тир моба", [Op(), new("value", "int", "Tier", "Тир", true, 6, Min: 1, Max: 8)]),
        new("mob.category", "mob", "Mob category", "Категория моба", [new("categories", "list", "Categories (standard, champion, miniboss, boss…)", "Категории (standard, champion, miniboss, boss…)", true, "boss")]),
        new("mob.boss", "mob", "Mob is a world boss", "Моб — мировой босс", []),
        new("var", "variable", "Variable / counter", "Переменная / счётчик",
            [new("scope", "select", "Scope", "Область", true, "player", ["player", "global"]), new("name", "text", "Name", "Имя", true, "kills"), Op(), new("value", "number", "Value", "Значение", true, 10)], Who: true),
        new("amount", "event", "The event's number (damage, fame…)", "Число события (урон, слава…)", [Op(), new("value", "number", "Value", "Значение", true, 100)], Needs: Feature.Amount),
        new("flag", "event", "Event detail", "Деталь события", [new("flag", "select", "Detail", "Деталь", true, "heal",
            ["firstLogin", "clusterChange", "heal", "overTime", "area", "magic", "projectile", "interrupted", "atLogin", "weapon", "new", "expired", "removed", "cleared", "remount",
             "died", "knockdown", "boss", "event", "fish", "mob", "mount", "pvp", "toMob", "toPlayer", "byPlayer", "byPlugin", "revert", "temporary"])]),
        new("text", "event", "Chat text / goal / command arguments contain", "Текст чата / цели / аргументы команды содержат", [new("contains", "text", "Text", "Текст", true)]),
    ];

    // ---- actions (target = whom; the rule's default target when empty)

    public static readonly StepDef[] Actions =
    [
        new("message", "chat", "Message", "Сообщение",
            [new("text", "textarea", "Text ({player} {target} {mob} {spell} {item} {buff} {amount} {cluster} {args} {var:name} {gvar:name})", "Текст ({player} {target} {mob} {spell} {item} {buff} {amount} {cluster} {args} {var:имя} {gvar:имя})", true),
             new("channel", "select", "Channel", "Канал", false, "chat", ["chat", "error", "announce", "zone"])], Targeted: true),
        new("give.item", "reward", "Give an item", "Выдать предмет", [new("item", "item", "Item", "Предмет", true), new("count", "int", "Count", "Количество", false, 1, Min: 1, Max: 9999),
            new("quality", "int", "Quality (1-5)", "Качество (1-5)", false, 1, Min: 1, Max: 5)], Targeted: true),
        new("take.item", "reward", "Take an item", "Забрать предмет", [new("item", "item", "Item", "Предмет", true), new("count", "int", "Count", "Количество", false, 1, Min: 1, Max: 9999)], Targeted: true),
        new("silver", "reward", "Give / take silver", "Дать / забрать серебро", [new("amount", "number", "Silver (negative takes)", "Серебро (минус — забрать)", true, 1000)], Targeted: true),
        new("fame", "reward", "Give fame", "Дать славу", [new("amount", "number", "Fame", "Слава", true, 1000, Min: 0), new("tier", "int", "As tier", "Как тир", false, 4, Min: 1, Max: 8)], Targeted: true),
        new("buff.apply", "unit", "Apply a buff", "Наложить бафф", [new("buff", "buff", "Buff (spells.xml spell)", "Бафф (заклинание spells.xml)", true),
            new("seconds", "seconds", "Duration (0 = the spell's own)", "Длительность (0 — своя)", false, 0, Min: 0), new("stacks", "int", "Stacks", "Стаки", false, 1, Min: 1, Max: 100)], Targeted: true),
        new("buff.remove", "unit", "Remove a buff", "Снять бафф", [new("buff", "buff", "Buff", "Бафф", true)], Targeted: true),
        new("damage", "unit", "Damage", "Нанести урон", [new("amount", "number", "Amount", "Величина", false, 100, Min: 0), new("percent", "number", "or % of max health", "или % от макс. здоровья", false, 0, Min: 0, Max: 100),
            new("magic", "bool", "Magic damage", "Магический урон", false, true)], Targeted: true),
        new("heal", "unit", "Heal", "Вылечить", [new("amount", "number", "Amount", "Величина", false, 100, Min: 0), new("percent", "number", "or % of max health", "или % от макс. здоровья", false, 0, Min: 0, Max: 100)], Targeted: true),
        new("teleport", "unit", "Teleport", "Телепорт", [new("cluster", "cluster", "Cluster (empty = the same)", "Кластер (пусто — тот же)"), new("x", "number", "X (empty = the spawn point)", "X (пусто — точка появления)"),
            new("y", "number", "Y", "Y")], Targeted: true),
        new("move", "unit", "Knockback / pull / dash", "Отбросить / притянуть / рывок", [new("kind", "select", "Movement", "Движение", true, "knockback", ["knockback", "pull", "dash"]),
            new("distance", "number", "Distance (m)", "Дистанция (м)", false, 5, Min: 0.5, Max: 30), new("speed", "number", "Speed", "Скорость", false, 12, Min: 1, Max: 60)], Targeted: true),
        new("summon", "world", "Summon a mob", "Призвать моба", [new("mob", "mob", "Mob", "Моб", true), new("count", "int", "Count", "Количество", false, 1, Min: 1, Max: 20),
            new("lifetime", "seconds", "Lifetime (0 = until killed)", "Время жизни (0 — пока не убьют)", false, 60, Min: 0)], Targeted: true),
        new("boss.spawn", "world", "Spawn a world boss", "Призвать мирового босса", [new("mob", "mob", "Boss mob", "Моб-босс", true)], Targeted: true),
        new("event.start", "world", "Start a world event", "Запустить мировое событие", [new("kind", "text", "Event kind (content/events.json: invasion, champion…)", "Вид события (content/events.json: invasion, champion…)", true, "invasion"),
            new("tier", "int", "Tier (0 = the cluster's)", "Тир (0 — тир кластера)", false, 0, Min: 0, Max: 10)], Targeted: true),
        new("var.set", "variable", "Set a variable / counter", "Изменить переменную / счётчик", [new("scope", "select", "Scope", "Область", true, "player", ["player", "global"]),
            new("name", "text", "Name", "Имя", true, "kills"), new("op", "select", "Operation", "Операция", false, "add", ["set", "add", "sub"]), new("value", "number", "Value", "Значение", true, 1)], Targeted: true),
        new("loot.add", "event", "Add to the dropped loot", "Добавить в добычу", [new("item", "item", "Item", "Предмет", true), new("count", "int", "Count", "Количество", false, 1, Min: 1, Max: 999)], Needs: Feature.Loot),
        new("modify", "event", "Change the event's number", "Изменить число события", [new("op", "select", "Operation", "Операция", true, "mul", ["mul", "add", "set"]),
            new("value", "number", "Value (mul 1.5 = +50 %)", "Значение (mul 1.5 = +50 %)", true, 1.5)], Needs: Feature.Amount),
        new("cancel", "event", "Refuse (cancel the action)", "Запретить (отменить действие)", [new("reason", "text", "Reason shown to the player", "Причина для игрока")], Needs: Feature.Cancel),
        new("prevent", "event", "Skip the game's own effect", "Отменить стандартный эффект", [], Needs: Feature.Prevent),
        new("cast", "unit", "Cast a spells.xml spell (as self, at the target)", "Применить заклинание spells.xml (от себя на цель)", [new("spell", "spell", "Spell", "Заклинание", true)], Targeted: true),
        new("slot.set", "unit", "Change the spell in a slot", "Сменить способность в слоте", [SlotParam(), new("spell", "spell", "Spell", "Способность", true),
            new("mode", "select", "Which spells", "Какие способности", false, "fromItemList", ["fromItemList", "any"],
                Hint: "fromItemList: only the equipped item's options; any (experimental): any spell of the slot's kind",
                HintRu: "fromItemList: только варианты надетого предмета; any (экспериментально): любая способность этого вида"),
            new("duration", "seconds", "For (0 = for good)", "На время (0 — навсегда)", false, 0, Min: 0, Max: 86400),
            new("cooldown", "select", "Cooldown", "Перезарядка", false, "keep", ["keep", "carry", "reset"],
                Hint: "keep: each spell its own; carry: the new one takes the old one's rest; reset: ready at once",
                HintRu: "keep: у каждой своя; carry: новая получает остаток старой; reset: сразу готова")], Targeted: true),
        new("extrabar.set", "unit", "Put a spell into an extra bar slot", "Поставить способность в слот доп. панели", [
            new("slot", "int", "Slot (1-6 bar 1, 7-12 bar 2)", "Слот (1-6 панель 1, 7-12 панель 2)", true, 1, Min: 1, Max: 12),
            new("spell", "spell", "Spell (empty = the gear's again)", "Способность (пусто — снова от экипировки)", false,
                Hint: "an activespell; kept until cleared or the server restarts (not saved)", HintRu: "активная способность; держится до сброса или перезапуска сервера (не сохраняется)")], Targeted: true),
        new("cooldown", "unit", "Set / reset a cooldown", "Задать / сбросить перезарядку", [new("spell", "spell", "Spell (empty = reset all)", "Заклинание (пусто — сбросить все)"),
            new("seconds", "seconds", "Seconds left", "Осталось секунд", false, 0, Min: 0)], Targeted: true),
        new("run.chain", "flow", "Run a chain (rules with trigger \"chain\")", "Запустить цепочку (правила с триггером «цепочка»)", [new("chain", "text", "Chain name", "Имя цепочки", true),
            new("delay", "seconds", "After (0 = now)", "Через (0 — сразу)", false, 0, Min: 0, Max: 86400)]),
        new("log", "flow", "Write to the server log", "Записать в лог сервера", [new("text", "text", "Text", "Текст", true)]),
    ];

    public static TriggerDef? Trigger(string type) => Triggers.FirstOrDefault(t => t.Type == type);
    public static StepDef? Condition(string type) => Conditions.FirstOrDefault(c => c.Type == type);
    public static StepDef? Action(string type) => Actions.FirstOrDefault(a => a.Type == type);

    // ---- the palette as JSON (the Studio)

    private static JsonObject ParamJson(Param p)
    {
        var o = new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["en"] = p.En, ["ru"] = p.Ru, ["required"] = p.Required };
        if (p.Default is not null)
            o["default"] = p.Default switch
            {
                int i => JsonValue.Create(i), double d => JsonValue.Create(d), bool b => JsonValue.Create(b), _ => JsonValue.Create(p.Default.ToString()),
            };
        if (p.Options is not null) o["options"] = new JsonArray([.. p.Options.Select(x => (JsonNode)x)]);
        if (p.Min is { } min) o["min"] = min;
        if (p.Max is { } max) o["max"] = max;
        if (p.Hint is not null) o["hint"] = p.Hint;
        if (p.HintRu is not null) o["hintRu"] = p.HintRu;
        return o;
    }

    private static JsonArray Features(Feature f) =>
        new([.. Enum.GetValues<Feature>().Where(x => x != Feature.None && f.HasFlag(x)).Select(x => (JsonNode)x.ToString().ToLowerInvariant())]);

    public static JsonObject Palette() => new()
    {
        ["schema"] = 1,
        ["triggers"] = new JsonArray([.. Triggers.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = t.Type, ["group"] = t.Group, ["en"] = t.En, ["ru"] = t.Ru, ["idKind"] = t.IdKind, ["targets"] = new JsonArray([.. t.Targets.Select(x => (JsonNode)x)]),
            ["features"] = Features(t.Features), ["flags"] = new JsonArray([.. t.Flags.Select(x => (JsonNode)x)]), ["params"] = new JsonArray([.. t.Params.Select(p => (JsonNode)ParamJson(p))]),
            ["amountEn"] = t.AmountEn, ["amountRu"] = t.AmountRu,
        })]),
        ["conditions"] = new JsonArray([.. Conditions.Select(c => (JsonNode)StepJson(c))]),
        ["actions"] = new JsonArray([.. Actions.Select(a => (JsonNode)StepJson(a))]),
        ["targets"] = new JsonArray([.. TargetLabels.Select(t => (JsonNode)new JsonObject { ["value"] = t.Value, ["en"] = t.En, ["ru"] = t.Ru })]),
        ["templates"] = RuleTemplates.Json(),
    };

    private static JsonObject StepJson(StepDef s) => new()
    {
        ["type"] = s.Type, ["group"] = s.Group, ["en"] = s.En, ["ru"] = s.Ru, ["who"] = s.Who, ["targeted"] = s.Targeted, ["needs"] = Features(s.Needs),
        ["params"] = new JsonArray([.. s.Params.Select(p => (JsonNode)ParamJson(p))]),
    };
}
