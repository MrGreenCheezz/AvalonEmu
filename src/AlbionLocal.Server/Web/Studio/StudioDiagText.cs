using System.Globalization;
using System.Text.RegularExpressions;

namespace AlbionLocal.Server.Web.Studio;

/// <summary>
/// The catalogue of the Studio's problem texts (studio SPEC §9.3): every diagnostic code — the drafts' checks, the recipes', the
/// modkit's check of a build (MK_*), the rules' — becomes a sentence in Russian and English that says what is wrong and in which
/// field, an advice how to fix it, and where possible a one-click fix ("use the template's model"). The producers keep their short
/// English "message" (logs, the console modkit); the values in it (names, numbers) are taken out here by the code's patterns.
/// Unknown messages keep the code's Russian title with the English detail.
/// </summary>
public static class StudioDiagText
{
    public sealed record Text(string Ru, string En, string? AdviceRu = null, string? AdviceEn = null)
    {
        public (string Ru, string En)? Field { get; init; }
        public (string Ru, string En, string Path)? Fix { get; init; }
    }

    private const string PickModel = "Выберите модель из списка (кнопка «Выбрать модель») или оставьте модель шаблона.";
    private const string PickModelEn = "Choose a model from the list (the \"Choose model\" button) or keep the template's model.";
    private const string PickIcon = "Выберите иконку из списка (кнопка «Выбрать иконку») или оставьте иконку шаблона.";
    private const string PickIconEn = "Choose an icon from the list (the \"Choose icon\" button) or keep the template's icon.";

    /// <summary>The texts of one diagnostic.</summary>
    public static Text Describe(StudioDiag d)
    {
        var field = d.Label ?? FieldName(d.Path);
        var text = Find(d.Code, d.Message, field) ?? Fallback(d.Code, d.Message);
        return text with { Field = field };
    }

    /// <summary>A modkit message (ModBuilder's Errors/Warnings) → its catalogue code and the draft field it is about.</summary>
    public static (string Code, string Path) Modkit(string message)
    {
        foreach (var (code, path, re) in ModkitPatterns)
            if (re.IsMatch(message)) return (code, path(re.Match(message)));
        return ("MODKIT", "");
    }

    /// <summary>A note of a build (new ids, files changed) in Russian and English.</summary>
    public static (string Ru, string En) Note(string note)
    {
        var m = Regex.Match(note, @"^(\S+): (\d+) added, (\d+) changed \((.*)\)$");
        if (m.Success) return ($"{m.Groups[1].Value}: добавлено {m.Groups[2].Value}, изменено {m.Groups[3].Value} (пакеты: {m.Groups[4].Value}).", note);
        m = Regex.Match(note, @"^items\.bin: new item ids (.*); the (\d+) journal _EMPTY/_FULL variants the client makes after the file move by \+(\d+)");
        if (m.Success)
            return ($"Новые ID предметов: {m.Groups[1].Value}. Варианты журналов (_EMPTY/_FULL, {m.Groups[2].Value} шт.), которые клиент создаёт после файла, сдвигаются на +{m.Groups[3].Value} — одинаково у клиента и сервера. Это нормально: сохранения хранят имена предметов, а не ID.",
                $"New item ids: {m.Groups[1].Value}. The {m.Groups[2].Value} journal variants (_EMPTY/_FULL) the client makes after the file move by +{m.Groups[3].Value} on client and server alike. That is fine: saves keep item names, not ids.");
        m = Regex.Match(note, @"^spells\.bin: new spell ids (.*)$");
        if (m.Success) return ($"Новые ID умений: {m.Groups[1].Value}.", $"New spell ids: {m.Groups[1].Value}.");
        return (note, note);
    }

    /// <summary>A rule problem (Rules/RuleModel.cs) in Russian and English.</summary>
    public static (string Ru, string En) Rule(string message)
    {
        foreach (var (re, ru) in RulePatterns)
        {
            var m = re.Match(message);
            if (m.Success) return (m.Result(ru), message);
        }
        return (message, message);
    }

    // ---------------------------------------------------------------- fields

    /// <summary>The name of the field a JSON pointer points at ("/recipe/resources/0/item" → «ингредиент 1»).</summary>
    public static (string Ru, string En)? FieldName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var p = path.Split('/').Skip(1).Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray();
        string N(int i) => int.TryParse(p.ElementAtOrDefault(i), out var n) ? (n + 1).ToString(CultureInfo.InvariantCulture) : "?";
        return p switch
        {
            ["id"] => ("уникальное имя", "unique name"),
            ["template"] => ("шаблон", "template"),
            ["name"] => ("название", "name"),
            ["name", var l] => ($"название ({l.ToUpperInvariant()})", $"name ({l.ToUpperInvariant()})"),
            ["description", ..] => ("описание", "description"),
            ["mesh"] => ("модель", "model"),
            ["uisprite"] => ("иконка", "icon"),
            ["attributes", var a, ..] => ($"атрибут «{a}»", $"attribute \"{a}\""),
            ["recipe", "resources", _, "count"] => ($"количество ингредиента {N(2)}", $"count of ingredient {N(2)}"),
            ["recipe", "resources", _, ..] => ($"ингредиент {N(2)}", $"ingredient {N(2)}"),
            ["recipe", "silver"] => ("серебро рецепта", "recipe silver"),
            ["recipe", "time"] => ("время крафта", "craft time"),
            ["recipe", "focus"] => ("фокус рецепта", "recipe focus"),
            ["recipe", ..] => ("рецепт", "recipe"),
            ["stations", _] => ($"станция крафта {N(1)}", $"crafting station {N(1)}"),
            ["stations"] => ("станции крафта", "crafting stations"),
            ["spells", _, "slot"] => ($"слот умения {N(1)}", $"slot of spell {N(1)}"),
            ["spells", _, ..] => ($"умение {N(1)}", $"spell {N(1)}"),
            ["spells"] => ("умения", "spells"),
            ["extraSpells", _] => ($"доп. способность {N(1)}", $"extra ability {N(1)}"),
            ["extraSpells"] => ("доп. способности", "extra abilities"),
            ["drops", _, ..] => ($"дроп {N(1)}", $"drop {N(1)}"),
            ["enchantments"] => ("уровни зачарования", "enchantment levels"),
            ["loot", _, ..] => ($"список лута {N(1)}", $"loot list {N(1)}"),
            ["entries", _, ..] => ($"запись {N(1)}", $"entry {N(1)}"),
            ["entries"] => ("записи", "entries"),
            ["blocks", ..] => ("блоки умения", "spell blocks"),
            ["goal", "values", var k, ..] => (k, k),
            ["values", var k, ..] => (k, k),
            [var k, ..] => (k, k),
            _ => null,
        };
    }

    private static string Q((string Ru, string En)? f, bool ru) => f is { } x ? (ru ? $"«{x.Ru}»" : $"\"{x.En}\"") : "";

    // ---------------------------------------------------------------- the catalogue

    private delegate Text? Maker(Match m, (string Ru, string En)? f);

    private static readonly Dictionary<string, List<(Regex Re, Maker Make)>> Catalogue = new(StringComparer.Ordinal);

    private static void C(string code, string pattern, Maker make)
    {
        if (!Catalogue.TryGetValue(code, out var list)) Catalogue[code] = list = [];
        list.Add((new Regex(pattern, RegexOptions.CultureInvariant), make));
    }

    private static Text? Find(string code, string message, (string Ru, string En)? f)
    {
        if (!Catalogue.TryGetValue(code, out var list)) return null;
        foreach (var (re, make) in list)
        {
            var m = re.Match(message);
            if (m.Success && make(m, f) is { } t) return t;
        }
        return null;
    }

    private static Text Fallback(string code, string message) =>
        new(Titles.TryGetValue(code, out var t) ? $"{t}: {message}" : message, message);

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["BAD_ID"] = "Недопустимое уникальное имя", ["ID_TAKEN"] = "Имя уже занято", ["NO_TEMPLATE"] = "Нет шаблона", ["BAD_VALUE"] = "Неверное значение",
        ["NO_SPELL"] = "Нет такого умения", ["NO_ITEM"] = "Нет такого предмета", ["RECIPE"] = "Рецепт не собрался", ["COMPILE"] = "Черновик не собирается",
        ["MODKIT"] = "Проверка модкита", ["FAILED"] = "Сбой", ["REFUSED"] = "Отказано", ["TOO_MANY"] = "Слишком много", ["OUT_OF_RANGE"] = "Вне диапазона",
        ["BAD_NUMBER"] = "Не число", ["EMPTY_REF"] = "Ничего не выбрано", ["NO_ELEMENT"] = "Элемента нет у шаблона", ["rule"] = "Правило",
        ["NOT_ACTIVE"] = "Не активное умение (в доп. панели — только activespell)", ["DUPLICATE"] = "Повтор", ["BAD_FILE"] = "Файл не читается",
    };

    static StudioDiagText()
    {
        // Ids and templates.
        C("BAD_ID", @"^(.+?): A-Z, 0-9 and _ \(2 to 70\)$", (m, f) => new(
            $"{Q(f, true)}: только заглавные латинские буквы, цифры и _ (2–70 символов).", $"{Q(f, false)}: only A–Z, 0–9 and _ (2 to 70).",
            "Например: MY_BLADE. Строчные буквы станут заглавными, когда вы выйдете из поля.", "E.g. MY_BLADE. Lower case becomes upper case when you leave the field."));
        C("BAD_ID", @"the unique name", (m, f) => new(
            "Уникальное имя должно состоять из 3–80 заглавных латинских букв, цифр и _.", "The unique name must be 3 to 80 of A–Z, 0–9 and _.",
            "Например: T6_MAIN_MYSWORD. Переименовать: меню ⋯ → «Переименовать…».", "E.g. T6_MAIN_MYSWORD. Rename: menu ⋯ → Rename…"));
        C("ID_TAKEN", @"^(?:a spell )?(\S+) exists already \((?:in pack (\S+)|.*)\)", (m, f) => new(
            m.Groups[2].Success ? $"Имя «{m.Groups[1].Value}» уже занято в пакете «{m.Groups[2].Value}»." : $"Имя «{m.Groups[1].Value}» уже есть в игре.",
            m.Groups[2].Success ? $"The name {m.Groups[1].Value} is taken in pack {m.Groups[2].Value}." : $"The name {m.Groups[1].Value} exists in the game already.",
            "Выберите другое уникальное имя (например, добавьте _MY).", "Choose another unique name (e.g. add _MY)."));
        C("ID_TAKEN", @"^(\S+) is (?:an official mob|a loot\.xml list)", (m, f) => new(
            $"Имя «{m.Groups[1].Value}» уже есть в игре: нужно своё имя.", $"{m.Groups[1].Value} exists in the game: it needs a name of its own.",
            "Переименуйте черновик (меню ⋯ → «Переименовать…»).", "Rename the draft (menu ⋯ → Rename…)."));
        C("NO_TEMPLATE", @"^no (?:template (item|spell) |mob )(\S+)", (m, f) => new(
            $"Шаблон «{m.Groups[2].Value}» не найден в игре.", $"The template {m.Groups[2].Value} is not in the game.",
            "Создайте черновик заново из существующего шаблона (Новый… → выбрать из каталога).", "Make the draft again from an existing template (New… → pick from the catalogue)."));

        // Names.
        C("NO_NAME_EN", @"^no name \(", (m, f) => new("Нет названия (его используют логи и объявления сервера; клиент показывает имя шаблона).",
            "No name (the server's logs and announcements use it; the client shows the template's).", "Заполните поле «Название».", "Fill in the Name field."));
        C("NO_NAME_EN", @"", (m, f) => new("Нет английского названия: английский клиент покажет служебный ключ вместо имени.", "No English name: the English client shows the raw key.",
            "Заполните поле «Название» → EN.", "Fill in Name → EN."));
        C("NO_NAME_RU", @"", (m, f) => new("Нет русского названия: русский клиент покажет служебный ключ вместо имени.", "No Russian name: the Russian client shows the raw key.",
            "Заполните поле «Название» → RU.", "Fill in Name → RU."));
        C("NAME_LONG", @"", (m, f) => new("Название длиннее 200 символов.", "The name is longer than 200 characters.", "Сократите название.", "Shorten the name."));

        // Attributes and values.
        C("NEW_ATTR", @"^(\S+): the template has no such attribute", (m, f) => new(
            $"У шаблона нет атрибута «{m.Groups[1].Value}»: он будет добавлен как текст.", $"The template has no attribute {m.Groups[1].Value}: it is added as text.",
            "Проверьте имя атрибута или уберите это изменение (↺).", "Check the attribute's name or take the change back (↺)."));
        C("FIXED_ATTR", @"^(\S+) is set by the studio", (m, f) => new($"Атрибут «{m.Groups[1].Value}» задаёт сама студия.", $"{m.Groups[1].Value} is set by the studio.",
            "Уберите это изменение (↺).", "Take the change back (↺)."));
        C("FIXED_ATTR", @"^(\S+) is (?:the template's asset|an asset)", (m, f) => new(
            $"Атрибут «{m.Groups[1].Value}» — ресурс шаблона (модель, иконка, эффект, звук) и не меняется здесь.",
            $"{m.Groups[1].Value} is the template's asset (model, icon, effect, sound) and does not change here.",
            "Уберите это изменение (↺). Модель и иконку предмета можно выбрать в разделе «Внешний вид».", "Take the change back (↺). An item's model and icon are chosen under Look."));
        C("BAD_VALUE", @"^(.+?): '(.*)' is not a number", (m, f) => new($"{Q(f, true)}: «{m.Groups[2].Value}» — не число.", $"{Q(f, false)}: '{m.Groups[2].Value}' is not a number.",
            "Введите число; дробную часть отделяйте точкой (1.5).", "Type a number; use . for decimals (1.5)."));
        C("BAD_VALUE", @"^(.+?): a whole number \(the template has (.*)\)", (m, f) => new($"{Q(f, true)}: нужно целое число (у шаблона {m.Groups[2].Value}).",
            $"{Q(f, false)}: a whole number (the template has {m.Groups[2].Value}).", "Уберите дробную часть.", "Drop the decimals."));
        C("BAD_VALUE", @"^(.+?): true or false", (m, f) => new($"{Q(f, true)}: только «да» или «нет» (true/false).", $"{Q(f, false)}: true or false."));
        C("BAD_VALUE", @"^(.+?): at most 200 characters", (m, f) => new($"{Q(f, true)}: не больше 200 символов, без управляющих символов.", $"{Q(f, false)}: at most 200 characters, no control characters."));
        C("BAD_VALUE", @"^(.+?): '(.*)' is not one of (.*)$", (m, f) => new($"{Q(f, true)}: «{m.Groups[2].Value}» — не из списка ({m.Groups[3].Value}).",
            $"{Q(f, false)}: '{m.Groups[2].Value}' is not one of {m.Groups[3].Value}.", "Выберите значение из списка.", "Choose a value from the list."));
        C("BAD_VALUE", @"^entry (\d+): a weight", (m, f) => new($"Запись {m.Groups[1].Value}: вес должен быть ≥ 0.", $"Entry {m.Groups[1].Value}: a weight ≥ 0."));
        C("BAD_VALUE", @"^enchantments: true or false", (m, f) => new("Уровни зачарования: только «да» или «нет».", "Enchantment levels: true or false."));
        C("BAD_VALUE", @"^subSpells: official or clone", (m, f) => new("Под-умения: «official» (официальные) или «clone» (копии).", "Sub-spells: official or clone."));
        C("BAD_VALUE", @"^(\S+): no attribute name", (m, f) => new($"«{m.Groups[1].Value}» — недопустимое имя атрибута.", $"{m.Groups[1].Value} is no attribute name."));
        C("BAD_TIER", @"", (m, f) => new("Тир должен быть от 0 до 8.", "The tier is 0 to 8."));
        C("BAD_NUMBER", @"^recipe (\w+): '(.*)' is not a number", (m, f) => new($"{Q(f, true)}: «{m.Groups[2].Value}» — не число ≥ 0.", $"{Q(f, false)}: '{m.Groups[2].Value}' is not a number ≥ 0.",
            "Введите число не меньше 0 (дробную часть — через точку).", "Type a number ≥ 0 (use . for decimals)."));
        C("BAD_NUMBER", @"^(.+?): '(.*)' is not a number", (m, f) => new($"{Q(f, true)}: «{m.Groups[2].Value}» — не число.", $"{Q(f, false)}: '{m.Groups[2].Value}' is not a number.",
            "Введите число (дробную часть — через точку).", "Type a number (use . for decimals)."));
        C("BAD_NUMBER", @"^(.+?): a whole number", (m, f) => new($"{Q(f, true)}: нужно целое число.", $"{Q(f, false)}: a whole number."));
        C("OUT_OF_RANGE", @"^(.+?): (\S+)\.\.(\S+) \(now (\S+)\)$", (m, f) => new(
            $"{Q(f, true)}: допустимо от {m.Groups[2].Value} до {m.Groups[3].Value}, сейчас {m.Groups[4].Value}.", $"{Q(f, false)}: {m.Groups[2].Value} to {m.Groups[3].Value}, now {m.Groups[4].Value}.",
            "Введите значение в этих пределах (подсказка «официально» показывает типичные числа).", "Type a value in that range (the \"official\" helper shows typical numbers)."));
        C("OUT_OF_RANGE", @"^rolls: 0\.\.50", (m, f) => new("Число выборов: от 0 до 50.", "Rolls: 0 to 50."));
        C("OUT_OF_RANGE", @"^(\w+): (\S+)\.\.(\S+)$", (m, f) => new($"{Q(f, true)}: допустимо от {m.Groups[2].Value} до {m.Groups[3].Value}.", $"{Q(f, false)}: {m.Groups[2].Value} to {m.Groups[3].Value}."));
        C("REQUIRED", @"", (m, f) => new($"Обязательное поле {Q(f, true)} не заполнено.", $"The required field {Q(f, false)} is empty.", "Заполните его.", "Fill it in."));

        // References.
        C("NO_SPELL", @"no spell (\S+)", (m, f) => new($"{Q(f, true)}: умение «{m.Groups[1].Value}» не найдено.", $"{Q(f, false)}: no spell {m.Groups[1].Value}.",
            "Выберите умение из списка (кнопка «Выбрать…») или создайте его черновик в этом пакете.", "Pick a spell from the list (Choose…) or make its draft in this pack."));
        C("NO_ITEM", @"(?:ingredient (\S+): no such item|no item (\S+))", (m, f) => new(
            $"{Q(f, true)}: предмет «{(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}» не найден.", $"{Q(f, false)}: no item {(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)}.",
            "Выберите предмет из каталога (кнопка «Выбрать…»).", "Pick an item from the catalogue (Choose…)."));
        C("NO_STATION", @"no crafting station (\S+)", (m, f) => new($"Станция крафта «{m.Groups[1].Value}» не найдена.", $"No crafting station {m.Groups[1].Value}.",
            "Уберите её и добавьте станцию из списка.", "Remove it and add a station from the list."));
        C("NO_LIST", @"no loot list (\S+)", (m, f) => new($"Список лута «{m.Groups[1].Value}» не найден (ни в пакете, ни в loot.xml).", $"No loot list {m.Groups[1].Value} (neither this pack's nor loot.xml's).",
            "Выберите список из каталога или создайте его черновик.", "Pick a list from the catalogue or make its draft."));
        C("NO_MOB", @"no mob (\S+)", (m, f) => new($"{Q(f, true)}: моб «{m.Groups[1].Value}» не найден в mobs.bin.", $"{Q(f, false)}: no mob {m.Groups[1].Value} in mobs.bin.",
            "Выберите моба из списка или используйте шаблон имени с * (например, *_BOSS_*).", "Pick a mob from the list or use a name pattern with * (e.g. *_BOSS_*)."));
        C("EMPTY_REF", @"", (m, f) => new($"{Q(f, true)}: ничего не выбрано.", $"{Q(f, false)}: nothing chosen.",
            "Выберите значение из списка или удалите строку (✕).", "Choose a value or remove the row (✕)."));
        C("NO_BLOCK", @"^added block (\d+): no block (\S*) in spell (\S*)", (m, f) => new(
            $"Добавленный блок {m.Groups[1].Value}: в умении «{m.Groups[3].Value}» нет блока «{m.Groups[2].Value}».", $"Added block {m.Groups[1].Value}: no block {m.Groups[2].Value} in spell {m.Groups[3].Value}.",
            "Добавьте блок заново кнопкой «+ Добавить блок из умения…».", "Add the block again with \"+ Add a block from a spell…\"."));
        C("NO_ELEMENT", @"^(?:remove )?(\S+?):? the template has no such (?:element|block)", (m, f) => new(
            $"У шаблона больше нет элемента «{m.Groups[1].Value}» (обновление клиента?).", $"The template has no element {m.Groups[1].Value} any more (a client update?).",
            "Уберите это изменение.", "Take the change back."));
        C("SELF_REF", @"", (m, f) => new("Список лута не может ссылаться сам на себя.", "A loot list cannot roll itself.", "Уберите эту запись.", "Remove the entry."));
        C("EMPTY_LIST", @"", (m, f) => new("Список пуст: он ничего не даёт.", "No entries: the list gives nothing.", "Добавьте предметы или другие списки.", "Add items or other lists."));

        // Counts, chances, slots.
        C("BAD_COUNT", @"^ingredient (\S*): a count", (m, f) => new($"Ингредиент «{m.Groups[1].Value}»: количество — целое число от 1 до 9999.", $"Ingredient {m.Groups[1].Value}: a count of 1 to 9999."));
        C("BAD_CHANCE", @"^replaceChance", (m, f) => new("Шанс замены: от 0 до 1 (0.01 = 1 % появлений шаблона в мире).", "Replace chance: 0 to 1 (0.01 = 1 % of the template's world spawns)."));
        C("BAD_CHANCE", @"^(drop|loot|entry) (\d+):", (m, f) => new(
            $"{(m.Groups[1].Value switch { "drop" => "Дроп", "loot" => "Список лута", _ => "Запись" })} {m.Groups[2].Value}: шанс должен быть от 0 до 100 %.",
            $"{m.Groups[1].Value} {m.Groups[2].Value}: a chance of 0 to 100 %.", "Например, 3 % — это 0.03 в файле; в поле вводите проценты.", "E.g. 3 % is 0.03 in the file; the field takes percent."));
        C("BAD_AMOUNT", @"^drop (\d+)", (m, f) => new($"Дроп {m.Groups[1].Value}: количество — целое от 1 до 999.", $"Drop {m.Groups[1].Value}: an amount of 1 to 999."));
        C("BAD_AMOUNT", @"^entry (\d+)", (m, f) => new($"Запись {m.Groups[1].Value}: нужно 1 ≤ мин ≤ макс ≤ 9999.", $"Entry {m.Groups[1].Value}: amounts 1 ≤ min ≤ max ≤ 9999."));
        C("BAD_SLOT", @"^spell (\S+)", (m, f) => new($"Умение «{m.Groups[1].Value}»: слот от 1 до 6 (пусто — пассивное).", $"Spell {m.Groups[1].Value}: slot 1 to 6 (empty = passive)."));
        C("TOO_MANY", @"at most (\d+) (.+)$", (m, f) => new(
            $"Слишком много: не больше {m.Groups[1].Value} ({m.Groups[2].Value switch { "spells" => "умений", "added blocks" => "добавленных блоков", "entries" => "записей", var x => x }}).",
            $"Too many: at most {m.Groups[1].Value} {m.Groups[2].Value}.", "Уберите лишние строки.", "Remove some rows."));
        C("NO_ACTIVE", @"", (m, f) => new("У оружия нет активных умений (слоты 1–3).", "A weapon without active spells (slots 1–3).",
            "Добавьте умение кнопкой «+ Активное умение…» или «Взять умения шаблона».", "Add one with \"+ Active spell…\" or use the template's."));
        C("NOT_CRAFTABLE", @"", (m, f) => new("Нет ингредиентов: предмет не крафтится.", "No ingredients: the item is not craftable."));
        C("STATION_NO_RECIPE", @"", (m, f) => new("Станции указаны, а ингредиентов нет: крафтить там нечего.", "Stations without ingredients: nothing to craft there.",
            "Добавьте ингредиенты или уберите станции.", "Add ingredients or remove the stations."));
        C("NO_ENCHANTMENTS", @"", (m, f) => new("У шаблона нет уровней зачарования .1–.3: сохранять нечего.", "The template has no enchantment levels (.1-.3): nothing to keep."));
        C("DROP_NOWHERE", @"^drop (\d+)", (m, f) => new($"Дроп {m.Groups[1].Value}: не указано, откуда он выпадает.", $"Drop {m.Groups[1].Value}: no mobs, chests or mob category.",
            "Добавьте мобов, сундуки или категорию мобов.", "Add mobs, chests or a mob category."));
        C("BAD_PATTERN", @"^drop (\d+): '(.*)'", (m, f) => new($"Дроп {m.Groups[1].Value}: «{m.Groups[2].Value}» — не имя и не шаблон (допустимы A–Z, 0–9, _, * и ?).",
            $"Drop {m.Groups[1].Value}: '{m.Groups[2].Value}' is no name or pattern (A–Z, 0–9, _, * and ?)."));

        // The look (SPEC §9.2).
        C("MODEL_UNKNOWN", @"^mesh=""([^""]*)""", (m, f) => new($"Модель «{m.Groups[1].Value}» не найдена в клиенте.", $"The model \"{m.Groups[1].Value}\" is not in the client.", PickModel, PickModelEn)
            { Fix = ("Взять модель шаблона", "Use the template's model", "/mesh") });
        C("MODEL_NONE", @"^mesh=""([^""]*)""", (m, f) => new($"У предметов этого вида нет модели на персонаже: модель «{m.Groups[1].Value}» не используется.",
            $"Items of this kind show no model on the character: \"{m.Groups[1].Value}\" is not used.") { Fix = ("Убрать модель", "Remove the model", "/mesh") });
        C("ICON_UNKNOWN", @"^uisprite=""([^""]*)""", (m, f) => new($"Иконка «{m.Groups[1].Value}» не используется ни одним официальным предметом.",
            $"No official item uses the icon \"{m.Groups[1].Value}\".", PickIcon, PickIconEn) { Fix = ("Взять иконку шаблона", "Use the template's icon", "/uisprite") });

        // Misc of the studio.
        C("NO_ICON_CACHE", @"", (m, f) => new("Нет иконки в кэше студии (в игре она есть).", "No cached icon (the game shows it).",
            "Один раз выполните: dotnet run --project tools/AlbionLocal.ModKit -- icons", "Run once: dotnet run --project tools/AlbionLocal.ModKit -- icons"));
        C("NO_RECIPE", @"^recipe (\S+)", (m, f) => new($"Рецепт «{m.Groups[1].Value}» неизвестен: черновик редактируется только по полям.", $"Recipe {m.Groups[1].Value} is not known: the draft is edited by its fields only."));
        C("RECIPE", @"^the recipe made nothing$", (m, f) => new("Рецепт ничего не создал.", "The recipe made nothing.", "Проверьте поля формы.", "Check the form's fields."));
        C("BROKEN_DRAFT", @"^(\S+) is no JSON object", (m, f) => new($"Файл черновика «{m.Groups[1].Value}» повреждён (не JSON-объект).", $"The draft file {m.Groups[1].Value} is no JSON object.",
            "Исправьте файл в mods/<пакет>/studio/ или удалите его.", "Fix the file in mods/<pack>/studio/ or delete it."));
        C("REFUSED", @"", (m, f) => new("Сборка отклонена: официальный клиент обновился после первой сборки.", "Refused: the official client was updated since the first build.",
            "Выполните в консоли: dotnet run --project tools/AlbionLocal.ModKit -- build --force", "Run on the console: dotnet run --project tools/AlbionLocal.ModKit -- build --force"));

        // The modkit's check of a build (ModBuilder messages, classified by Modkit()).
        C("MK_MODEL", @"mesh=""([^""]*)"" is no model", (m, f) => new($"Модель «{m.Groups[1].Value}» не найдена в клиенте.", $"The model \"{m.Groups[1].Value}\" is not in the client.", PickModel, PickModelEn)
            { Fix = ("Взять модель шаблона", "Use the template's model", "/mesh") });
        C("MK_ICON", @"uisprite=""([^""]*)"" is no icon", (m, f) => new($"Иконка «{m.Groups[1].Value}» не используется ни одним официальным предметом.",
            $"No official item uses the icon \"{m.Groups[1].Value}\".", PickIcon, PickIconEn) { Fix = ("Взять иконку шаблона", "Use the template's icon", "/uisprite") });
        C("MK_ASSET", @"<(\w+) (\w+)=""([^""]*)""> is not used", (m, f) => new(
            $"Ресурс «{m.Groups[3].Value}» (атрибут {m.Groups[2].Value} в <{m.Groups[1].Value}>) не используется ни одним официальным элементом.",
            $"The asset \"{m.Groups[3].Value}\" ({m.Groups[2].Value} of <{m.Groups[1].Value}>) is used by no official element.",
            "Можно брать только существующие модели, иконки, эффекты и звуки: оставьте значение шаблона.", "Only existing models, icons, effects and sounds can be used: keep the template's value."));
        C("MK_NO_REF", @"<(\w+) (\w+)=""([^""]*)""> names no (item|spell)", (m, f) => new(
            $"Ссылка на несуществующ{(m.Groups[4].Value == "item" ? "ий предмет" : "ее умение")} «{m.Groups[3].Value}» (<{m.Groups[1].Value} {m.Groups[2].Value}>).",
            $"<{m.Groups[1].Value} {m.Groups[2].Value}=\"{m.Groups[3].Value}\"> names no {m.Groups[4].Value}.",
            "Выберите существующий из каталога или создайте его черновик в этом пакете (и сгенерируйте пакет).", "Pick an existing one or make its draft in this pack (and Generate)."));
        C("MK_NO_ICON", @"^items\.bin (\S+): a new item has no icon", (m, f) => new($"У нового предмета «{m.Groups[1].Value}» нет своей иконки.", $"The new item {m.Groups[1].Value} has no icon of its own.",
            PickIcon, PickIconEn));
        C("MK_NO_MODEL", @"^items\.bin (\S+): new equipment has no model", (m, f) => new($"У нового снаряжения «{m.Groups[1].Value}» нет своей модели.", $"The new equipment {m.Groups[1].Value} has no model of its own.",
            PickModel, PickModelEn));
        C("MK_TIER_NAME", @"^items\.bin (\S+): tier (\d+)", (m, f) => new($"У «{m.Groups[1].Value}» тир {m.Groups[2].Value}, но имя не начинается с T{m.Groups[2].Value}_ (клиент покажет тир из атрибута).",
            $"{m.Groups[1].Value}: tier {m.Groups[2].Value} but the name does not start with T{m.Groups[2].Value}_.",
            $"Переименуйте, чтобы имя начиналось с T{m.Groups[2].Value}_ (меню ⋯ → «Переименовать…»).", $"Rename it to start with T{m.Groups[2].Value}_ (menu ⋯ → Rename…)."));
        C("MK_NO_LOC", @"^localization: the (name|description) of (item|spell) (\S+) has no (EN-US|RU-RU) text \((\S+)\)", (m, f) => new(
            $"Нет {(m.Groups[4].Value == "RU-RU" ? "русского" : "английского")} текста: {(m.Groups[1].Value == "name" ? "название" : "описание")} {(m.Groups[2].Value == "item" ? "предмета" : "умения")} «{m.Groups[3].Value}» — клиент покажет ключ {m.Groups[5].Value}.",
            $"The {m.Groups[1].Value} of {m.Groups[2].Value} {m.Groups[3].Value} has no {m.Groups[4].Value} text: the client shows the key {m.Groups[5].Value}.",
            $"Заполните поле «{(m.Groups[1].Value == "name" ? "Название" : "Описание")}» → {(m.Groups[4].Value == "RU-RU" ? "RU" : "EN")}.", $"Fill in {(m.Groups[1].Value == "name" ? "Name" : "Description")} → {(m.Groups[4].Value == "RU-RU" ? "RU" : "EN")}."));
        C("MK_IDS_MOVE", @"^(items|spells)\.bin: (\d+) official \w+ ids would move \(first (\S+)", (m, f) => new(
            $"Сдвинулись бы ID официальных {(m.Groups[1].Value == "items" ? "предметов" : "умений")}: {m.Groups[2].Value} (первый — {m.Groups[3].Value}).",
            $"{m.Groups[2].Value} official {m.Groups[1].Value} ids would move (first {m.Groups[3].Value}).",
            "Новые элементы можно только добавлять в конец; официальные (и их зачарования) не удаляйте.", "Only append new elements; keep the official ones (and their enchantments)."));
        C("MK_ORDER", @"^(\S+): element (\d+) is (\S+), the official file has (\S+) there", (m, f) => new(
            $"{m.Groups[1].Value}: на месте {m.Groups[2].Value} стоит «{m.Groups[3].Value}» вместо официального «{m.Groups[4].Value}».",
            $"{m.Groups[1].Value}: element {m.Groups[2].Value} is {m.Groups[3].Value}, the official file has {m.Groups[4].Value}.",
            "Официальные элементы остаются на своих местах, новые добавляются в конец.", "Official elements keep their place; new ones are appended."));
        C("MK_CLIENT", @"^(\S+): written for client (\S+), the official client is (\S+)", (m, f) => new(
            $"Пакет «{m.Groups[1].Value}» сделан для клиента {m.Groups[2].Value}, а сейчас клиент {m.Groups[3].Value}.",
            $"Pack {m.Groups[1].Value} was written for client {m.Groups[2].Value}; the client is {m.Groups[3].Value}.",
            "Проверьте пакет на новых данных и пересохраните его.", "Check the pack against the new data and save it again."));
    }

    private static readonly (string Code, Func<Match, string> Path, Regex Re)[] ModkitPatterns =
    [
        ("MK_MODEL", _ => "/mesh", new Regex(@"mesh=""[^""]*"" is no model")),
        ("MK_ICON", _ => "/uisprite", new Regex(@"uisprite=""[^""]*"" is no icon")),
        ("MK_NO_MODEL", _ => "/mesh", new Regex(@"new equipment has no model")),
        ("MK_NO_ICON", _ => "/uisprite", new Regex(@"a new item has no icon")),
        ("MK_ASSET", _ => "", new Regex(@"> is not used by any official element")),
        ("MK_NO_REF", _ => "", new Regex(@"> names no (item|spell)$")),
        ("MK_TIER_NAME", _ => "/id", new Regex(@"^items\.bin \S+: tier \d+ but the name")),
        ("MK_NO_LOC", m => $"/{m.Groups[1].Value}/{(m.Groups[2].Value == "RU-RU" ? "ru" : "en")}", new Regex(@"^localization: the (name|description) of \w+ \S+ has no (EN-US|RU-RU) text")),
        ("MK_IDS_MOVE", _ => "", new Regex(@"official \w+ ids would move")),
        ("MK_ORDER", _ => "", new Regex(@": element \d+ is \S+, the official file has")),
        ("MK_CLIENT", _ => "", new Regex(@": written for client \S+, the official client is")),
    ];

    private static readonly (Regex, string)[] RulePatterns =
    [
        (new(@"^the id '(.*)' must be"), "ID правила «$1»: 1–64 символа a–z, 0–9, _ и - (это имя файла)."),
        (new(@"^the id '(.*)' differs from the file name (\S+)"), "ID правила «$1» не совпадает с именем файла $2."),
        (new(@"^no trigger"), "Нет триггера: укажите, когда срабатывает правило."),
        (new(@"^choose a trigger$"), "Выберите триггер («Когда»)."),
        (new(@"^unknown trigger '(.*)'$"), "Неизвестный триггер «$1»."),
        (new(@"^choose a type$"), "Выберите тип."),
        (new(@"^unknown action '(.*)'$"), "Неизвестное действие «$1»."),
        (new(@"^unknown condition '(.*)'$"), "Неизвестное условие «$1»."),
        (new(@"^unknown target '(.*)'$"), "Неизвестная цель «$1»."),
        (new(@"^the trigger '(.*)' has no '(.*)'$"), "У триггера «$1» нет цели «$2»."),
        (new(@"^the trigger '(.*)' takes no id filter"), "Триггер «$1» не принимает фильтр по имени: он игнорируется."),
        (new(@"^name the chain"), "Укажите имя цепочки (действие run.chain вызывает её по этому имени)."),
        (new(@"^a command is one or two words"), "Команда — одно-два слова из a–z, 0–9, _ и - (без точки)."),
        (new(@"^'(.*)' needs a trigger that offers (\w+); '(.*)' does not$"), "«$1» требует триггер с возможностью «$2», а у «$3» её нет."),
        (new(@"^a condition looks at one unit: (.*)$"), "Условие проверяет одного участника: $1."),
        (new(@"^the radius is 1-200 m$"), "Радиус — от 1 до 200 м."),
        (new(@"^no actions"), "Нет действий: правило ничего не делает."),
        (new(@"^match is all or any$"), "Совпадение: «все» или «любое»."),
        (new(@"^the cooldown is"), "Перезарядка: от 0 до 2592000 с."),
        (new(@"^max fires is"), "Макс. срабатываний — целое ≥ 0 (0 — без ограничения)."),
        (new(@"^fires per second is"), "Срабатываний в секунду: от 1 до 1000."),
        (new(@"^unknown parameter '(.*)'"), "Неизвестный параметр «$1» (игнорируется)."),
        (new(@"^'(.*)' is required$"), "Параметр «$1» обязателен."),
        (new(@"^'(.*)' must be a number$"), "«$1» должно быть числом."),
        (new(@"^'(.*)' must be a whole number$"), "«$1» должно быть целым числом."),
        (new(@"^'(.*)' is true or false$"), "«$1»: только «да» или «нет»."),
        (new(@"^'(.*)' is a time HH:MM$"), "«$1»: время в виде ЧЧ:ММ."),
        (new(@"^'(.*)' is one of (.*)$"), "«$1» — одно из: $2."),
        (new(@"^'(.*)' is a list of (.*)$"), "«$1» — список из: $2."),
        (new(@"^'(.*)' is (\S+)-(\S+)$"), "«$1»: от $2 до $3."),
        (new(@"^'(.*)': no pattern here$"), "«$1»: шаблон с * здесь нельзя."),
        (new(@"^no (\w+) '(.*)' in the game data$"), "«$2» не найден в игровых данных ($1)."),
        (new(@"^pack (\S+) is not in Mods:Packs"), "Пакета «$1» нет в Mods:Packs: его правила на этом сервере не выполняются."),
        (new(@"^the file is no JSON object$"), "Файл правила — не JSON-объект."),
        (new(@"^schema should be 1$"), "schema должна быть 1."),
        (new(@"^switched off after (\d+) errors"), "Отключено после $1 ошибок (Plugins:MaxErrors)."),
    ];
}
