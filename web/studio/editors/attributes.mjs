// What the Studio knows about Albion's item and spell attributes: a readable name (RU/EN), a hint, a group and a better field type
// than the raw value suggests. Attributes not listed still get a field (typed from the template's value) in the "Other" group.

import { lang } from "../i18n.mjs";
import { pointer } from "../ui.mjs";

// [name, ru, en, group, hint ru, hint en, type override]
const ITEM = [
    ["tier", "Тир", "Tier", "basic", "0..8; от тира зависят требования и сила", "0..8", "int"],
    ["itempower", "Сила предмета (IP)", "Item power (IP)", "basic", "базовая сила предмета без качества и зачарования", "base item power"],
    ["dummyitempower", "IP для расходников", "Item power (consumables)", "basic"],
    ["maxqualitylevel", "Макс. качество", "Max quality", "basic"],
    ["slottype", "Слот", "Slot", "basic", "куда надевается: mainhand, offhand, head, armor, shoes, cape, bag, mount, potion, food"],
    ["shopcategory", "Категория рынка", "Market category", "basic"],
    ["shopsubcategory1", "Подкатегория рынка", "Market subcategory", "basic"],
    ["shopsubcategory2", "Подкатегория 2", "Subcategory 2", "basic"],
    ["craftingcategory", "Категория крафта", "Crafting category", "basic"],
    ["weight", "Вес, кг", "Weight, kg", "basic"],
    ["maxstacksize", "Размер стопки", "Stack size", "basic"],
    ["twohanded", "Двуручное", "Two-handed", "slots"],
    ["activespellslots", "Активных слотов", "Active spell slots", "slots", "сколько активных умений можно выбрать"],
    ["passivespellslots", "Пассивных слотов", "Passive spell slots", "slots"],
    ["attacktype", "Тип атаки", "Attack type", "combat", "melee или ranged"],
    ["attackdamage", "Урон автоатаки", "Auto-attack damage", "combat", "урон одной автоатаки"],
    ["attackspeed", "Скорость атаки", "Attack speed", "combat", "атак в секунду"],
    ["attackrange", "Дальность атаки, м", "Attack range, m", "combat"],
    ["abilitypower", "Сила умений", "Ability power", "combat", "масштаб урона/лечения умений"],
    ["physicalspelldamagebonus", "Бонус физ. урона умений", "Physical spell damage bonus", "combat", "доля: 0.1 = +10%"],
    ["magicspelldamagebonus", "Бонус маг. урона умений", "Magic spell damage bonus", "combat", "доля: 0.1 = +10%"],
    ["physicalattackdamagebonus", "Бонус физ. урона атак", "Physical attack damage bonus", "combat"],
    ["magicattackdamagebonus", "Бонус маг. урона атак", "Magic attack damage bonus", "combat"],
    ["healbonus", "Бонус лечения", "Heal bonus", "combat"],
    ["healmodifier", "Модификатор лечения", "Heal modifier", "combat"],
    ["threatbonus", "Бонус угрозы", "Threat bonus", "combat"],
    ["attackspeedbonus", "Бонус скорости атаки", "Attack speed bonus", "combat"],
    ["magiccooldownreduction", "Сокращение перезарядки", "Cooldown reduction", "combat"],
    ["magiccasttimereduction", "Сокращение времени каста", "Cast time reduction", "combat"],
    ["energycostreduction", "Сокращение затрат энергии", "Energy cost reduction", "combat"],
    ["focusfireprotectionpenetration", "Пробивание защиты от фокуса", "Focus-fire protection penetration", "combat"],
    ["hitpointsmax", "Макс. здоровье", "Max health", "defense"],
    ["hitpointsregenerationbonus", "Бонус регенерации здоровья", "Health regeneration bonus", "defense"],
    ["energymax", "Макс. энергия", "Max energy", "defense"],
    ["energyregenerationbonus", "Бонус регенерации энергии", "Energy regeneration bonus", "defense"],
    ["physicalarmor", "Броня", "Armor", "defense"],
    ["magicresistance", "Сопротивление магии", "Magic resistance", "defense"],
    ["crowdcontrolresistance", "Сопротивление контролю", "CC resistance", "defense"],
    ["bonusdefensevsplayers", "Защита против игроков", "Defense vs players", "defense"],
    ["bonusdefensevsmobs", "Защита против мобов", "Defense vs mobs", "defense"],
    ["bonusccdurationvsplayers", "Длит. контроля по игрокам", "CC duration vs players", "defense"],
    ["bonusccdurationvsmobs", "Длит. контроля по мобам", "CC duration vs mobs", "defense"],
    ["movespeed", "Скорость передвижения", "Move speed", "defense"],
    ["movespeedbonus", "Бонус скорости передвижения", "Move speed bonus", "defense"],
    ["maxload", "Грузоподъёмность", "Max load", "defense"],
    ["durability", "Прочность", "Durability", "durability"],
    ["durabilityloss_attack", "Износ за атаку", "Wear per attack", "durability"],
    ["durabilityloss_spelluse", "Износ за умение", "Wear per spell", "durability"],
    ["durabilityloss_receivedattack", "Износ от полученной атаки", "Wear per received attack", "durability"],
    ["durabilityloss_receivedspell", "Износ от полученного умения", "Wear per received spell", "durability"],
    ["unlockedtocraft", "Открыто для крафта", "Unlocked to craft", "unlock", "у нового предмета нет узла в дереве судьбы, поэтому студия ставит true"],
    ["unlockedtoequip", "Открыто для экипировки", "Unlocked to equip", "unlock", "у нового предмета нет узла в дереве судьбы, поэтому студия ставит true"],
    ["canbeovercharged", "Можно перезаряжать", "Can be overcharged", "unlock"],
    ["showinmarketplace", "Показывать на рынке", "Show in the marketplace", "unlock"],
    ["consumespell", "Умение при использовании", "Spell when used", "consumable", "что делает расходник (зелье, еда)", "", "spell"],
    ["masterymodifier", "Модификатор мастерства", "Mastery modifier", "other"],
    ["itempowerprogressiontype", "Прогрессия IP", "IP progression", "other"],
];

const SPELL = [
    ["recastdelay", "Перезарядка", "Cooldown", "main", "секунды между применениями", "", "seconds"],
    ["energyusage", "Энергия", "Energy cost", "main"],
    ["castingtime", "Время каста", "Cast time", "main", "", "", "seconds"],
    ["castrange", "Дальность, м", "Range, m", "main"],
    ["hitdelay", "Задержка попадания", "Hit delay", "main", "", "", "seconds"],
    ["standtime", "Время стойки", "Stand time", "main", "", "", "seconds"],
    ["channelduration", "Длительность канала", "Channel duration", "main", "", "", "seconds"],
    ["maxcharges", "Заряды", "Charges", "main"],
    ["target", "Цель", "Target", "main", "enemy, self, ally, ground, all…"],
    ["category", "Категория", "Category", "main", "damage, heal, buff, crowdcontrol, movementbuff…"],
    ["uitype", "Тип в интерфейсе", "UI type", "main"],
    ["disruptionfactor", "Прерываемость", "Disruption factor", "main", "1 = прерывается полностью"],
    ["keepmovingaftercast", "Двигаться после каста", "Keep moving after cast", "main"],
    ["showrange", "Показывать дальность", "Show range", "main"],
    ["unlockedtoequip", "Открыто для экипировки", "Unlocked to equip", "main"],
    // Inside the effect blocks.
    ["change", "Изменение", "Change", "block", "отрицательное — урон, положительное — лечение"],
    ["value", "Значение", "Value", "block", "доля для баффов (−0.3 = −30%)"],
    ["time", "Длительность", "Duration", "block", "", "", "seconds"],
    ["interval", "Интервал", "Interval", "block", "", "", "seconds"],
    ["initialinterval", "Первый интервал", "Initial interval", "block", "", "", "seconds"],
    ["count", "Количество", "Count", "block"],
    ["radius", "Радиус", "Radius", "block"],
    ["effectarearadius", "Радиус области", "Area radius", "block"],
    ["spell", "Умение", "Spell", "block", "", "", "spell"],
    ["chance", "Шанс", "Chance", "block", "0..1", "", "number"],
    ["movespeed", "Скорость", "Speed", "block"],
    ["attribute", "Атрибут", "Attribute", "block"],
    ["effecttype", "Тип эффекта", "Effect type", "block", "physical, magic, true"],
    ["timewindow", "Окно комбо", "Combo window", "block", "", "", "seconds"],
];

function table(rows) {
    const map = new Map();
    for (const [name, ru, en, group, hintRu, hintEn, type] of rows) map.set(name, { name, ru, en, group, hintRu: hintRu ?? "", hintEn: hintEn ?? "", type });
    return map;
}

export const ITEM_ATTRS = table(ITEM);
export const SPELL_ATTRS = table(SPELL);

export const ITEM_GROUPS = [
    ["basic", "Основное", "Basics"], ["combat", "Атака и умения", "Offense"], ["defense", "Защита и живучесть", "Defense"],
    ["slots", "Слоты умений", "Spell slots"], ["durability", "Прочность", "Durability"], ["consumable", "Расходник", "Consumable"],
    ["unlock", "Доступ", "Access"], ["other", "Прочее", "Other"], ["asset", "Ассеты шаблона (не меняются)", "Template assets (fixed)"],
];

export const groupLabel = (groups, key) => {
    const g = groups.find((x) => x[0] === key);
    return g ? (lang() === "ru" ? g[1] : g[2]) : key;
};

const SECONDS = /(time|delay|duration|interval|window)$/i;

/**
 * A field descriptor for one template attribute.
 * @param {{name: string, value: string, type: string}} attr from the catalogue detail
 * @param {string} basePath the draft object holding the overrides ("/attributes" or "/edits")
 * @param {string} key the key inside it (the attribute name, or "2/0@value" for a spell block)
 */
export function attrDescriptor(attr, known, basePath = "/attributes", key = attr.name) {
    const info = known.get(attr.name);
    const ru = lang() === "ru";
    let type = info?.type ?? { int: "number", number: "number", bool: "bool", text: "text", asset: "readonly", spell: "spell" }[attr.type] ?? "text";
    if (attr.type === "asset") type = "readonly";
    if (type === "number" && SECONDS.test(attr.name) && !info?.type) type = "seconds";
    if ((type === "seconds" || type === "number") && attr.type !== "int" && attr.type !== "number") type = "text";
    return {
        path: basePath + pointer(key).slice(0),
        name: attr.name,
        label: info ? (ru ? info.ru : info.en) : attr.name,
        hint: info ? (ru ? info.hintRu : info.hintEn || info.hintRu) : "",
        type,
        template: attr.value,
        asString: true,
        group: attr.type === "asset" ? "asset" : info?.group ?? "other",
    };
}
