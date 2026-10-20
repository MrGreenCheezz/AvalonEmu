// Interface language: Russian by default, English as the alternative. The code is written in English; t() looks the English
// text up in the Russian dictionary (missing entries stay English). {1}, {2}… are filled from params.

const KEY = "studio.lang";

let current = (() => {
    try {
        return window.localStorage.getItem(KEY) === "en" ? "en" : "ru";
    } catch {
        return "ru";
    }
})();

export const lang = () => current;

export function setLang(l) {
    current = l === "en" ? "en" : "ru";
    try { window.localStorage.setItem(KEY, current); } catch { /* no storage */ }
}

function fill(text, params) {
    return params ? text.replace(/\{(\w+)\}/g, (m, k) => (params[k] !== undefined ? String(params[k]) : m)) : text;
}

export function t(text, params) {
    if (text === undefined || text === null) return "";
    const s = String(text);
    return fill(current === "ru" ? RU[s] ?? s : s, params);
}

/** The display name of something with EN/RU names ({en, ru} or {name: {en, ru}}): the interface language first. */
export function nameOf(o) {
    if (!o) return null;
    const en = o.en ?? null;
    const ru = o.ru ?? null;
    const pick = current === "ru" ? ru || en : en || ru;
    return pick || null;
}

/**
 * A diagnostic's text in the interface language: the server's catalogue sentence (d.text {ru, en}: what is wrong, in which field;
 * Web/Studio/StudioDiagText.cs), else the code's Russian title with the server's English detail.
 */
export function diagText(d) {
    if (d?.text && (d.text.ru || d.text.en)) return nameOf(d.text);
    const ru = current === "ru" ? CODES[d.code] : null;
    if (!ru) return d.message;
    return `${ru}: ${d.message}`;
}

/** How to fix it (d.advice {ru, en}), or "". */
export const diagAdvice = (d) => (d?.advice ? nameOf(d.advice) ?? "" : "");

const CODES = {
    BAD_ID: "Недопустимое уникальное имя",
    ID_TAKEN: "Имя уже занято",
    NO_TEMPLATE: "Нет шаблона",
    NO_NAME_EN: "Нет английского названия",
    NO_NAME_RU: "Нет русского названия",
    NAME_LONG: "Слишком длинное название",
    NEW_ATTR: "Атрибута нет у шаблона",
    FIXED_ATTR: "Атрибут не меняется",
    BAD_VALUE: "Неверное значение",
    BAD_TIER: "Неверный тир",
    NO_SPELL: "Нет такого умения",
    NO_ITEM: "Нет такого предмета",
    NO_STATION: "Нет такой станции",
    REQUIRED: "Обязательное поле",
    OUT_OF_RANGE: "Вне допустимого диапазона",
    BAD_NUMBER: "Не число",
    NO_BLOCK: "Нет такого блока",
    NO_LIST: "Нет такого списка лута",
    NO_MOB: "Нет такого моба",
    RECIPE: "Рецепт не собрался",
    NO_RECIPE: "Неизвестный рецепт",
    BAD_CHANCE: "Неверный шанс",
    BAD_AMOUNT: "Неверное количество",
    EMPTY_LIST: "Пустой список",
    SELF_REF: "Список ссылается сам на себя",
    NO_ENCHANTMENTS: "У шаблона нет зачарований",
    NO_MOB: "Нет такого моба",
    BAD_NUMBER: "Не число",
    EMPTY_REF: "Ничего не выбрано",
    BAD_COUNT: "Неверное количество",
    NOT_CRAFTABLE: "Не крафтится",
    STATION_NO_RECIPE: "Станции без рецепта",
    BAD_SLOT: "Неверный слот",
    TOO_MANY: "Слишком много",
    NO_ACTIVE: "Нет активных умений",
    DROP_NOWHERE: "Не указано, откуда дроп",
    BAD_PATTERN: "Неверный шаблон имени",
    BAD_CHANCE: "Неверный шанс",
    BAD_AMOUNT: "Неверное количество",
    NO_ICON_CACHE: "Нет иконки в кэше студии",
    NO_ELEMENT: "Элемента больше нет у шаблона",
    BROKEN_DRAFT: "Испорченный файл черновика",
    COMPILE: "Ошибка сборки черновика",
    MODKIT: "Модкит",
    REFUSED: "Отказано",
    FAILED: "Сбой",
};

const RU = {
    // shell
    "Close (Esc)": "Закрыть (Esc)", Cancel: "Отмена", OK: "ОК", Save: "Сохранить", Discard: "Отбросить", Delete: "Удалить", Rename: "Переименовать",
    "Restore": "Восстановить", Import: "Импорт", Install: "Установить", "Install…": "Установить…", "Restore…": "Восстановить…", Validate: "Проверить",
    Generate: "Сгенерировать", "Generate •": "Сгенерировать •", Catalogue: "Каталог", "Web panel": "Веб-панель", More: "Ещё", Details: "Подробно",
    "Load more": "Показать ещё", Remove: "Убрать", Clear: "Очистить", "Move up": "Выше", "Choose…": "Выбрать…", "+ Add…": "+ Добавить…",
    "Choose from the catalogue": "Выбрать из каталога", "Nothing found": "Ничего не найдено", "Searching…": "Поиск…",
    "Shown {1} of {2}": "Показано {1} из {2}", "of this pack": "из этого пакета", "this pack": "этот пакет", official: "официальный",
    "any {1}": "любой {1}", tier: "тир", type: "тип", slot: "слот", category: "категория", subcategory: "подкатегория",
    "offered by an item (abilities)": "есть у предметов (умения)", "Now:": "Сейчас:", "not chosen": "не выбрано", "not found": "не найдено",
    pattern: "шаблон", "No such entry in the game data or the packs": "Такой записи нет ни в игре, ни в пакетах",
    dmg: "урон", cd: "КД", "en.": "эн.", s: " с", items: "предм.", recipes: "рецептов", users: "использует",
    "Search: name, English or Russian text (several words)": "Поиск: имя, английский или русский текст (можно несколько слов)",
    "A name pattern works too: type it (e.g. WORLD_BOSS_*) and press Enter.": "Можно и шаблон имени: введите (например WORLD_BOSS_*) и нажмите Enter.",
    "Choose an item": "Выбор предмета", "Choose a spell": "Выбор умения", "Choose a mob": "Выбор моба", "Choose a crafting station": "Выбор станции крафта",
    "Choose a loot list": "Выбор списка лута", "Choose an icon": "Выбор иконки", "all atlases": "все атласы",
    "Sprite name, e.g. SWORD, POTION, FIRE": "Имя спрайта, например SWORD, POTION, FIRE",
    "No icon cache yet: run the modkit's icons command once (dotnet run --project tools/AlbionLocal.ModKit -- icons).": "Кэша иконок ещё нет: один раз запустите dotnet run --project tools/AlbionLocal.ModKit -- icons.",
    item: "предмет", spell: "умение", mob: "моб", station: "станция", lootlist: "список лута",
    Items: "Предметы", Spells: "Умения", Mobs: "Мобы", "Loot lists": "Списки лута", Stations: "Станции", "Icons…": "Иконки…",
    // forms
    "Back to the template's value: {1}": "Вернуть значение шаблона: {1}", "template:": "шаблон:", yes: "да", no: "нет",
    "Add an ingredient": "Добавить ингредиент", "No ingredients: not craftable.": "Нет ингредиентов: не крафтится.", "+ Ingredient…": "+ Ингредиент…",
    count: "количество", "Does not drop from mobs or chests (only crafted or given).": "Не падает с мобов и из сундуков (только крафт или выдача).",
    Chance: "Шанс", Amount: "Количество", "Mob category": "Категория мобов", "any category": "любая категория", Mobs_: "Мобы", Chests: "Сундуки",
    "+ Mob…": "+ Моб…", "+ Chest…": "+ Сундук…", "+ Drop": "+ Дроп", "Remove this drop": "Убрать этот дроп",
    "Chest name or pattern": "Имя сундука или шаблон", "lootchests.bin names; * matches any text": "имена из lootchests.bin; * — любой текст",
    "Per kill (or a chest's first roll) × Rates:Loot. Mobs and a category together: both must match.": "За убийство (или первое открытие сундука) × Rates:Loot. Мобы и категория вместе: должно совпасть и то и другое.",
    // editors
    "Identity and texts": "Имя и тексты", "Unique name": "Уникальное имя", "Rename…": "Переименовать…",
    "The game's key of the new entry (A-Z 0-9 _). Rename updates the references in this pack.": "Ключ новой записи в игре (A-Z 0-9 _). Переименование обновит ссылки в этом пакете.",
    "Made from (template)": "Сделано из (шаблон)", Name: "Название", Description: "Описание",
    "Model, icon and effects are the template's: the client has no others.": "Модель, иконка и эффекты — от шаблона: других в клиенте нет.",
    "Animations, effects and sounds are the template's.": "Анимации, эффекты и звуки — от шаблона.",
    "{0}, {1}… are filled with the spell's numbers in the game (see the preview). [dmg]…[/dmg] colours a part.": "{0}, {1}… игра заполняет числами умения (см. предпросмотр). [dmg]…[/dmg] — цветной фрагмент.",
    "Main stats": "Основные характеристики", Crafting: "Крафт", "Spells (abilities)": "Умения", "Drops on the server": "Дроп на сервере",
    "Where it drops": "Откуда падает", "server/drops.json: needs the pack in Mods:Packs": "server/drops.json: пакет нужен в Mods:Packs",
    "All attributes": "Все атрибуты", "Filter attributes": "Фильтр атрибутов", "changed only": "только изменённые", "{1} changed": "изменено: {1}",
    "Uses the template's spells.": "Умения как у шаблона.", Customise: "Настроить", passive: "пассивное",
    "unlock tag (destiny board)": "метка открытия (дерево судьбы)", "Slot 1: {1}, slot 2: {2}, slot 3: {3}, passives: {4}": "Слот 1: {1}, слот 2: {2}, слот 3: {3}, пассивных: {4}",
    "Extra abilities": "Доп. способности", "+ Extra ability…": "+ Доп. способность…",
    "Active spells this item adds to the two extra action bars while it is worn (the client mod: Shift+1…6, Ctrl+1…6). server/extraspells.json: needs the pack in Mods:Packs; Generate updates the running server.":
        "Активные умения, которые предмет добавляет в две дополнительные панели, пока он надет (клиентский мод: Shift+1…6, Ctrl+1…6). server/extraspells.json: пакет нужен в Mods:Packs; «Собрать» обновляет работающий сервер.",
    "+ Active spell…": "+ Активное умение…", "+ Passive…": "+ Пассивное…", "Use the template's": "Как у шаблона",
    "Uses the template's recipe.": "Рецепт как у шаблона.", "Not craftable (the template has no recipe).": "Не крафтится (у шаблона нет рецепта).",
    "Customise the recipe": "Изменить рецепт", "Make it craftable": "Сделать крафтовым", Silver: "Серебро", "Craft time": "Время крафта",
    "in game units (the template's scale)": "в единицах игры (как у шаблона)", "Focus cost": "Стоимость фокуса", "Amount crafted": "Получается штук",
    Ingredients: "Ингредиенты", "Crafting stations": "Станции крафта", "Where the item is crafted. None: the recipe exists but no station offers it.": "Где крафтится. Без станций рецепт есть, но ни одна станция его не предлагает.",
    "Main numbers": "Основные числа", "Sub-spells": "Вложенные умения", "The spells it uses": "Умения, которые оно вызывает",
    "stay the official ones (shared with the original)": "остаются официальными (общие с оригиналом)",
    "are cloned into this pack with it (their numbers become its own)": "клонируются в пакет вместе с ним (их числа станут своими)",
    "It uses: ": "Вызывает: ", "It uses no other spells.": "Других умений не вызывает.", "Effect blocks": "Блоки эффектов",
    "Filter blocks (element or attribute)": "Фильтр блоков (элемент или атрибут)", "{1} edits": "правок: {1}", "Other attributes": "Прочие атрибуты",
    "All fields": "Все поля", "The editor failed": "Редактор упал",
    "The template {1} is not in the catalogue: only the texts can be edited.": "Шаблона {1} нет в каталоге: можно править только тексты.",
    // preview
    Tooltip: "Подсказка", Diff: "Разница", Problems: "Проблемы", "No preview yet.": "Предпросмотра пока нет.", "Building the preview…": "Строю предпросмотр…",
    "Open an entity to see its preview.": "Откройте запись, чтобы увидеть предпросмотр.", "No difference to the template yet.": "Пока нет отличий от шаблона.",
    "No problems": "Проблем нет", "Item power": "Сила предмета", Abilities: "Умения", Recipe: "Рецепт", "Cloned sub-spells: ": "Клонированные вложенные умения: ",
    "the template's value": "значение шаблона", Cooldown: "Перезарядка", Energy: "Энергия", Cast: "Каст", Range: "Дальность", "slot {1}": "слот {1}", updating: "обновляется",
    Damage: "Урон", "Attack speed": "Скорость атаки", "Attack range": "Дальность атаки", "Max health": "Макс. здоровье", "Max energy": "Макс. энергия", Armor: "Броня",
    "Magic resistance": "Сопр. магии", "Ability power": "Сила умений", "Move speed": "Скорость", "Max load": "Грузоподъёмность", "Heal bonus": "Бонус лечения",
    Durability: "Прочность", Weight: "Вес", Stack: "Стопка", "Cast time": "Время каста", "Hit delay": "Задержка", Channel: "Канал", Charges: "Заряды", Target: "Цель", Category: "Категория",
    // catalogue
    "Search the official data and the packs: name, English or Russian text": "Поиск по игре и пакетам: имя, английский или русский текст",
    "Open any entry to see it whole; \"Make a copy\" turns it into a draft of the open pack.": "Откройте любую запись целиком; «Сделать копию» превращает её в черновик открытого пакета.",
    "Sprite name copied: {1}": "Имя спрайта скопировано: {1}", "pack {1}": "пакет {1}", Numbers: "Числа", "Offered by": "Есть у предметов", "Uses the spells": "Вызывает умения",
    "{1} effect elements": "элементов эффектов: {1}", Attributes: "Атрибуты", "Contains the lists": "Содержит списки", "Part of the lists": "Входит в списки", "Used by": "Используют",
    "Crafts ({1})": "Крафтит ({1})", "Used by drafts": "Используется черновиками", "All attributes ({1})": "Все атрибуты ({1})", "Copy the name": "Скопировать имя",
    "Make a copy…": "Сделать копию…", "Copied: {1}": "Скопировано: {1}", "silver {1}, time {2}, focus {3}": "серебро {1}, время {2}, фокус {3}",
    "Crafted at": "Крафтится на", "Drops from": "Падает с", "Ingredient of": "Ингредиент для", "{1} enchantment levels (a copy has the base level only)": "уровней зачарования: {1} (у копии только базовый)",
    // app
    "Log in again": "Войти заново", "Loading the catalogue…": "Загружаю каталог…",
    "The official items, spells, names (the 78 MB localization), mobs, loot and buildings are read once: about 10-30 s.": "Официальные предметы, умения, названия (локализация 78 МБ), мобы, лут и здания читаются один раз: около 10-30 с.",
    "New pack": "Новый пакет", "Pack name (folder in mods/)": "Имя пакета (папка в mods/)", "a-z, 0-9 and -; e.g. pve-weapons": "a-z, 0-9 и -; например pve-weapons",
    "This pack exists already": "Такой пакет уже есть", "1 to 40 of a-z, 0-9 and -": "от 1 до 40 символов a-z, 0-9 и -", "Pack {1} created": "Пакет {1} создан",
    "Rename the pack {1}": "Переименовать пакет {1}", "Import a pack": "Импорт пакета", "Choose a file": "Выберите файл", "Pack {1} imported": "Пакет {1} импортирован",
    "A zip exported by the studio (the whole pack) or a .studio.json (the drafts only; Generate makes the patch files).": "Zip, выгруженный студией (весь пакет), или .studio.json (только черновики; патчи сделает «Сгенерировать»).",
    "New pack name": "Имя нового пакета", "Create or choose a pack first": "Сначала создайте или выберите пакет",
    "New {1} from {2}": "Новый {1} из {2}", "Unique name of the new {1}": "Уникальное имя нового: {1}", "A-Z, 0-9 and _ (the game's key). Into the pack {1}.": "A-Z, 0-9 и _ (ключ в игре). В пакет {1}.",
    "3 to 80 of A-Z, 0-9 and _": "от 3 до 80 символов A-Z, 0-9 и _", "This pack has it already": "В пакете такое уже есть", "{1} created from {2}": "{1} создан из {2}",
    "New item: choose the item to copy": "Новый предмет: выберите, что скопировать", "New spell: choose the spell to copy": "Новое умение: выберите, что скопировать",
    "Unsaved changes": "Несохранённые изменения", "{1} has unsaved changes.": "В {1} есть несохранённые изменения.",
    "Saved, with {1} blocker(s): Generate skips it until they are fixed": "Сохранено, но блокеров: {1} — генерация пропустит запись, пока их не исправить", Saved: "Сохранено",
    "Rename {1}": "Переименовать {1}", "Rename {1} → {2}": "Переименовать {1} → {2}", "{1} reference(s) in drafts:": "Ссылок в черновиках: {1}",
    "No other draft refers to it.": "Другие черновики на него не ссылаются.", "Renamed to {1}": "Переименовано в {1}", "Delete {1}?": "Удалить {1}?",
    "The draft is deleted (its last version stays in the history). Generate takes its elements out of the patch files.": "Черновик удаляется (последняя версия остаётся в истории). «Сгенерировать» уберёт его из патч-файлов.",
    "{1} is used": "{1} используется", "Delete anyway": "Всё равно удалить", Deleted: "Удалено", "Duplicate {1}": "Дублировать {1}", "History of {1}": "История {1}",
    "Choose a version.": "Выберите версию.", "Restore this version": "Вернуть эту версию", "Restored the version of {1}": "Возвращена версия {1}",
    "No earlier versions yet (each save keeps the one before; the last 20).": "Ранних версий пока нет (каждое сохранение хранит предыдущую; последние 20).",
    "Where {1} is used": "Где используется {1}", "No draft refers to it.": "Ни один черновик на него не ссылается.",
    "Generate: {1} blocker(s), those drafts were skipped": "Генерация: блокеров {1}, эти черновики пропущены", "Generated: {1}": "Сгенерировано: {1}", "nothing to write": "нечего писать",
    "Install into the game": "Установка в игру", "Choose at least one pack": "Выберите хотя бы один пакет", "Tick the confirmation": "Поставьте подтверждение",
    "Install builds the checked packs into game-local with the modkit": "Установка собирает отмеченные пакеты в game-local модкитом",
    "a build replaces the previous one, so check every pack you want. Afterwards restart the server and the game client: both read the game data only at their start. For drops add the packs to Mods:Packs.": "сборка заменяет предыдущую, поэтому отметьте все нужные пакеты. Потом перезапустите сервер и клиент игры: оба читают данные игры только при старте. Для дропа добавьте пакеты в Mods:Packs.",
    "I will restart the server and the game client": "Я перезапущу сервер и клиент игры", installed: "установлен",
    "Restore the official files?": "Вернуть официальные файлы?", "game-local gets the official GameData back (hard links). Restart the server and the game client afterwards.": "game-local получит обратно официальные GameData (жёсткие ссылки). Потом перезапустите сервер и клиент игры.",
    validate: "Проверка", install: "Установка", restore: "Восстановление", done: "готово", failed: "ошибка", queued: "в очереди",
    "Restart the server and the game client now.": "Перезапустите сервер и клиент игры.", "Both read the game data only when they start.": "Оба читают данные игры только при старте.",
    Notes: "Заметки", "Modkit output": "Вывод модкита", "Open: {1}": "Открыто: {1}", "Drafts of {1}": "Черновики {1}", "No draft has blockers or warnings": "Ни у одного черновика нет блокеров и предупреждений",
    "{1} blocker(s), {2} warning(s)": "блокеров {1}, предупреждений {2}", "Broken draft files: {1}": "Испорченные файлы черновиков: {1}", "Last modkit run: {1} {2}": "Последний запуск модкита: {1} {2}",
    "— choose a pack —": "— выберите пакет —", "— no packs yet —": "— пакетов пока нет —", "New pack…": "Новый пакет…", "Rename the pack…": "Переименовать пакет…",
    "Export as zip (whole pack)": "Выгрузить zip (весь пакет)", "Export as JSON (drafts)": "Выгрузить JSON (черновики)", "Import a pack…": "Импортировать пакет…",
    "Patch files of the pack…": "Патч-файлы пакета…", "Modkit status…": "Состояние модкита…", "Classic editor (/admin/mods)": "Классический редактор (/admin/mods)", "Pack actions": "Действия с пакетом",
    "Compile the drafts into the pack's patch files (items.xml, …)": "Собрать черновики в патч-файлы пакета (items.xml, …)",
    "Generate, then the modkit's check (a build in memory, nothing installed)": "Сгенерировать и проверить модкитом (сборка в памяти, ничего не ставится)",
    "Problems center": "Центр проблем", "No official GameData found next to Paths:ClientGameData: Validate, Install and Restore are off.": "Рядом с Paths:ClientGameData нет официальных GameData: проверка, установка и восстановление недоступны.",
    "Restart the server and the client": "Перезапустите сервер и клиент", "Choose a pack above, or create one.": "Выберите пакет сверху или создайте новый.", "No packs yet.": "Пакетов пока нет.",
    "Import…": "Импорт…", Filter: "Фильтр", "New {1} from the catalogue": "Новый {1} из каталога", "unsaved changes (autosaved in this browser)": "несохранённые изменения (автосохранены в этом браузере)",
    "{1} blocker(s)": "блокеров: {1}", "{1} warning(s)": "предупреждений: {1}", "Other new elements ({1})": "Другие новые элементы ({1})",
    "Made by the classic editor or by hand: edit them there (/admin/mods), or make a studio copy from the catalogue.": "Сделаны классическим редактором или вручную: правьте там (/admin/mods) или сделайте копию в студии из каталога.",
    "drafts changed since the last Generate": "черновики изменены после последней генерации", generated: "сгенерирован", "not generated yet": "ещё не генерировался",
    "+ New…": "+ Новое…", "Item — copy one from the catalogue…": "Предмет — копия из каталога…", "Spell — copy one from the catalogue…": "Умение — копия из каталога…", "Browse the catalogue…": "Открыть каталог…",
    "Make new items and abilities from the game's own: pick an existing one, make a copy, change it visually, see the tooltip live, then install.": "Новые предметы и умения из игровых: выберите существующее, сделайте копию, меняйте визуально, смотрите подсказку вживую и устанавливайте.",
    "New item": "Новый предмет", "Copy a weapon, armour, potion… and change its stats, recipe, spells and drops.": "Скопируйте оружие, броню, зелье… и меняйте характеристики, рецепт, умения и дроп.",
    "New ability": "Новое умение", "Copy a spell and change its cooldown, damage, effects; then offer it in an item's slots.": "Скопируйте умение, меняйте перезарядку, урон, эффекты; потом добавьте его в слоты предмета.",
    "Browse items, spells, mobs, loot lists and stations with icons.": "Предметы, умения, мобы, списки лута и станции с иконками.",
    "Choose or create a pack (top left).": "Выберите или создайте пакет (слева сверху).", "New… → pick what to copy → give it a unique name.": "«Новое…» → выберите, что копировать → дайте уникальное имя.",
    "Edit: pickers instead of typed names; problems show next to the fields.": "Правьте: выбор из списков вместо ввода имён; проблемы видны рядом с полями.",
    "Generate writes the pack's patch files; Validate runs the modkit's check.": "«Сгенерировать» пишет патч-файлы пакета; «Проверить» запускает проверку модкита.",
    "Install… builds into game-local; restart the server and the game client.": "«Установить…» собирает в game-local; перезапустите сервер и клиент игры.",
    "Catalogue: {1} items, {2} spells, {3} mobs · client {4} · packs in {5}": "Каталог: {1} предметов, {2} умений, {3} мобов · клиент {4} · пакеты в {5}",
    "No icon cache: run dotnet run --project tools/AlbionLocal.ModKit -- icons once for the icons.": "Нет кэша иконок: один раз запустите dotnet run --project tools/AlbionLocal.ModKit -- icons.",
    "This browser kept unsaved changes of {1} ({2}).": "Браузер сохранил несохранённые изменения {1} ({2}).", "Restore them": "Вернуть их",
    unsaved: "не сохранено", from: "из", "Undo (Ctrl+Z)": "Отменить (Ctrl+Z)", "Redo (Ctrl+Y)": "Повторить (Ctrl+Y)", "Save the draft (Ctrl+S)": "Сохранить черновик (Ctrl+S)",
    "History…": "История…", "Where used…": "Где используется…", "Duplicate…": "Дублировать…", "Template in the catalogue": "Шаблон в каталоге", "Delete…": "Удалить…",
    Pack: "Пакет", "Drafts: {1}": "Черновики: {1}", "Installed in game-local": "Установлен в game-local", "Not installed": "Не установлен", "Download the pack (zip)": "Скачать пакет (zip)",
    "No pack open.": "Пакет не открыт.", "Patch files of {1}": "Патч-файлы {1}", "No patch files yet: Generate writes them.": "Патч-файлов пока нет: их пишет «Сгенерировать».", "Modkit status": "Состояние модкита",
    "building in memory (reading the official files)": "сборка в памяти (чтение официальных файлов)", "loading the packs": "загрузка пакетов",
    "building and installing into game-local": "сборка и установка в game-local", "restoring the official files": "восстановление официальных файлов",
};
RU.Mobs = "Мобы";
// Phase 3: recipes, kits, the value helper, spell blocks, server kinds, the loot simulator.
Object.assign(RU, {
    Goal: "Цель", "The goal": "Цель", Rebuild: "Пересобрать", "Build the draft from the goal again": "Собрать черновик из цели заново",
    "A change here rebuilds the draft from the goal (the other tabs show the result; drops you set stay). Save keeps it.":
        "Изменение здесь пересобирает черновик из цели (остальные вкладки показывают результат; заданный дроп сохраняется). «Сохранить» фиксирует.",
    "the unique name: Rename… changes it": "уникальное имя: меняется через «Переименовать…»",
    "The goal's unique name is {1}: use Rename to change this draft's ({2}).": "Уникальное имя по цели — {1}: переименуйте черновик ({2}) через «Переименовать…».",
    "New from a goal": "Новое по цели", "Items": "Предметы", Abilities: "Умения", "Item lines": "Линейки", Kits: "Наборы",
    "Pick what you want to make; the recipe asks only for the numbers that matter and builds the drafts from official templates.":
        "Выберите, что хотите сделать: рецепт спросит только важные числа и соберёт черновики из официальных шаблонов.",
    "like {1}": "как {1}", "Create the kit": "Создать набор", Create: "Создать", "building…": "сборка…",
    "Fill the form: the preview appears here.": "Заполните форму — здесь появится предпросмотр.", "Will create ({1})": "Будет создано ({1})",
    "Overwrite?": "Перезаписать?", Overwrite: "Перезаписать", "Created: {1}": "Создано: {1}",
    "Overwriting replaces those drafts (their earlier version stays in the history).": "Перезапись заменит эти черновики (прежние версии останутся в истории).",
    "From a goal (recipes)…": "По цели (рецепты)…", "Kit (several linked entities)…": "Набор (несколько связанных сущностей)…",
    "Mob variant (server-side)…": "Вариант моба (на сервере)…", "Loot list (server-side)…": "Список лута (на сервере)…",
    "From a goal": "По цели", "Pick what you want — a two-handed weapon, an area spell, a mount, an item line T4–T8 — and fill only the numbers that matter.":
        "Выберите цель — двуручное оружие, умение по области, скакуна, линейку T4–T8 — и задайте только важные числа.",
    "One click: a legendary boss with its unique drop, an item line, a consumable set, an elite camp.":
        "В один клик: легендарный босс с уникальным дропом, линейка предметов, набор расходников, элитный лагерь.",
    "Mob variants": "Варианты мобов", "Loot lists": "Списки лута", Import: "Импорт", "Make it a studio draft (template {1})": "Сделать черновиком студии (шаблон {1})",
    "{1} is a studio draft now": "{1} теперь черновик студии", "New {1}": "Новый {1}",
    "New mob variant: choose the official mob it is based on": "Новый вариант моба: выберите официального моба-основу",
    mob: "моб", lootlist: "список лута", "Mob variant": "Вариант моба", "Loot list": "Список лута",
    "official:": "официально:", Suggest: "Предложить", "Official values of the same kind ({1} found)": "Официальные значения того же вида (найдено {1})",
    "Use the official median ({1})": "Взять официальную медиану ({1})",
    "Enchantment levels .1-.3": "Уровни зачарования .1-.3", "keep the template's .1-.3 (their recipes and item power) for the new item": "оставить новому предмету уровни .1-.3 шаблона (рецепты и силу)",
    "taken out": "убран", "Put back": "Вернуть", "Take this block out of the spell": "Убрать этот блок из умения", "{1} taken out": "убрано: {1}",
    added: "добавлен", "+ Add a block from a spell…": "+ Добавить блок из умения…", "Add a block: the spell to take it from": "Добавить блок: умение-источник",
    "Add a block of {1}": "Добавить блок из {1}", "This spell has no effect blocks with parameters.": "У этого умения нет блоков с параметрами.",
    "Added blocks go at the end of the spell, after the template's (taken-out ones removed).": "Добавленные блоки идут в конец умения, после блоков шаблона (убранные исключены).",
    "No block {1} in {2}": "Нет блока {1} в {2}", "No spell {1}": "Нет умения {1}",
    Identity: "Идентичность", "Made from (mob)": "Основа (моб)", "the client shows this mob's model, name and spells": "клиент показывает модель, имя и умения этого моба",
    "server-side only (logs, announcements); the client shows the template's name": "только на сервере (логи, объявления); клиент показывает имя шаблона",
    "The server's name of the variant: drop rules and .mob variant use it.": "Серверное имя варианта: его используют правила дропа и .mob variant.",
    "Mob variants and other lists refer to it by this name.": "Варианты мобов и другие списки ссылаются на него по этому имени.",
    Strength: "Сила", "Health ×": "Здоровье ×", "Damage ×": "Урон ×", "Fame ×": "Слава ×", "× the template's max health": "× макс. здоровье шаблона", "× the damage it deals": "× наносимый урон",
    "Replaces the template in the world": "Заменяет шаблон в мире", "chance per world spawn of the template (0: only .mob variant)": "шанс на каждое появление шаблона в мире (0 — только .mob variant)",
    "+ Loot list…": "+ Список лута…", "Only the template's loot (and drop rules that name it).": "Только лут шаблона (и правила дропа с его именем).",
    "Each list rolls with its chance on a kill (× Rates:Loot), after the template's own loot. Lists: this pack's or loot.xml's.":
        "Каждый список бросается со своим шансом при убийстве (× Rates:Loot) после лута шаблона. Списки: этого пакета или из loot.xml.",
    "On the server": "На сервере", "Needs the pack in Mods:Packs (server restart). GMs: .mob variant {1} [count]. Drops of items: give the variant's name in their drop rules.":
        "Нужен пакет в Mods:Packs (перезапуск сервера). ГМ: .mob variant {1} [кол-во]. Дроп предметов: укажите имя варианта в их правилах дропа.",
    Entries: "Записи", "Weighted picks per roll": "Взвешенных выборов за бросок", "entries with a weight share these picks; the others roll their own chance":
        "записи с весом делят эти выборы; остальные бросают свой шанс", "No entries yet.": "Записей пока нет.", "+ Item…": "+ Предмет…", "+ Another list…": "+ Другой список…",
    Simulator: "Симулятор", "Loot simulator: rolls": "Симулятор лута: бросков", "Loot simulator: kills": "Симулятор лута: убийств", Simulate: "Смоделировать",
    "per roll": "за бросок", "expected in {1}": "ожидается за {1}", "this run": "в этом прогоне", "official list": "официальный список", "Nothing drops.": "Ничего не выпадает.",
    "Per kill × Rates:Loot 1; drop rules of this pack's items that name it are included.": "За убийство при Rates:Loot 1; учтены правила дропа предметов пакета, где он указан.",
    "The server's roll: unweighted entries by chance, weighted ones share the picks.": "Бросок как на сервере: записи без веса — по шансу, с весом — делят выборы.",
    "server loot list": "серверный список лута", "weighted picks": "взвешенных выборов", weight: "вес", "mob variant": "вариант моба", "Drop rules": "Правила дропа",
    chance: "шанс", min: "мин", max: "макс",
});

// Problems, the look (model / icon pickers), typing.
Object.assign(RU, {
    Notes: "Заметки", Open: "Открыть", "Go to the field": "Перейти к полю", "Apply this fix": "Применить это исправление",
    "Done: {1}. Save the draft, then Generate / Validate again.": "Готово: {1}. Сохраните черновик, затем снова «Сгенерировать» / «Проверить».",
    Look: "Внешний вид", Model: "Модель", Icon: "Иконка", "Choose model…": "Выбрать модель…", "Choose icon…": "Выбрать иконку…",
    "Use the template's": "Как у шаблона", "Choose a model": "Выбор модели", "any slot": "любой слот",
    "The 3D model on the character: only one an official item uses.": "3D-модель на персонаже: только модель какого-либо официального предмета.",
    "The icon in the inventory and the shop: only one an official item uses.": "Иконка в инвентаре и магазине: только иконка какого-либо официального предмета.",
    "Items of this kind show no model on the character.": "У предметов этого вида нет модели на персонаже.",
    "Model or item name, e.g. CAPE, plate, плащ": "Имя модели или предмета, например CAPE, латы, плащ",
    "Only models of official items: the client has no others. The icon is that of the first item using the model.":
        "Только модели официальных предметов — других в клиенте нет. Иконка — первого предмета с этой моделью.",
    "Effects and sounds are the template's; the model and the icon can be another official item's (Look).":
        "Эффекты и звуки — шаблона; модель и иконку можно взять у другого официального предмета (раздел «Внешний вид»).",
    "will be saved as": "будет сохранено как", "Choose an icon": "Выбор иконки", users: "исп.",
});
