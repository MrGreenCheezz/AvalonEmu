// The no-code rules editor (server/plugins/RULES.md): "When → If → Do → Whom" forms for mods/<pack>/rules/<id>.json.
// The palette (triggers, conditions, actions, targets, parameter types, templates) comes from the server
// (GET /studio/api/rules/palette), so a trigger added there appears here without changes. Saving writes the file and
// reloads the rules on the running server at once (rules are server-only: no Generate, no install, no restart).
//
// The shell (app.mjs) calls: rulesUi.attach(host), sidebar(), open(id), newRule(), renderCenter(el), renderRight(el),
// isModified(), leave(), packChanged(). host = { pack(), show(id), refresh() }.

import { call } from "../api.mjs";
import { applyDiagnostics, renderField } from "../forms.mjs";
import { diagText, lang } from "../i18n.mjs";
import { pickerFor } from "../pickers.mjs";
import { choiceDialog, clone, confirmDialog, debounce, h, menuButton, modal, mount, plain, promptDialog, remount, setAt, toast } from "../ui.mjs";

const enc = encodeURIComponent;
const R = {
    palette: () => call("GET", "rules/palette"),
    list: (pack) => call("GET", `rules/list?pack=${enc(pack)}`),
    get: (pack, id) => call("GET", `rules/get?pack=${enc(pack)}&id=${enc(id)}`),
    save: (pack, rule, from) => call("POST", "rules/save", { pack, rule, from }),
    remove: (pack, id) => call("POST", "rules/delete", { pack, id }),
    validate: (pack, rule) => call("POST", "rules/validate", { pack, rule }),
    simulate: (pack, rule, player) => call("POST", "rules/simulate", { pack, rule, player }),
    players: () => call("GET", "rules/players"),
    reload: () => call("POST", "rules/reload", {}),
};

// ---- texts (Russian by default, as the rest of the studio)

const RU = {
    "Rules": "Правила",
    "New rule": "Новое правило",
    "No rules yet.": "Правил пока нет.",
    "When": "Когда",
    "If": "Если",
    "Do": "Сделать",
    "Whom": "С кем",
    "Limits": "Ограничения",
    "Trigger": "Триггер",
    "Only for (id filter)": "Только для (фильтр по id)",
    "empty = every one; * matches any text; a comma list = any of them": "пусто — для всех; * — любой текст; список через запятую — любой из них",
    "All conditions": "Все условия",
    "Any condition": "Любое условие",
    "No conditions: it always fires.": "Условий нет: срабатывает всегда.",
    "No actions yet.": "Действий пока нет.",
    "+ Condition": "+ Условие",
    "+ Action": "+ Действие",
    "NOT": "НЕ",
    "looks at": "у кого",
    "whom": "кому",
    "(rule's default)": "(по умолчанию правила)",
    "Radius, m": "Радиус, м",
    "Default target of the actions": "Цель действий по умолчанию",
    "Each action can name its own target; this one is used when it does not.": "У каждого действия может быть своя цель; эта — когда не указана.",
    "Cooldown, s": "Перезарядка, с",
    "per": "на",
    "player": "игрока",
    "global": "всех (глобально)",
    "Max fires (0 = no limit)": "Макс. срабатываний (0 — без предела)",
    "Fires per second at most": "Не больше срабатываний в секунду",
    "Message when the conditions fail (commands)": "Сообщение, если условия не выполнены (команды)",
    "Save": "Сохранить",
    "Saved and applied on the server": "Сохранено и применено на сервере",
    "Saved. The pack is not in Mods:Packs: its rules do not run on this server.": "Сохранено. Пакета нет в Mods:Packs: его правила на этом сервере не работают.",
    "Saved, with blockers: the rule is not loaded until they are fixed": "Сохранено с блокерами: правило не загрузится, пока их не исправить",
    "unsaved": "не сохранено",
    "on": "вкл",
    "off": "выкл",
    "Enabled": "Включено",
    "running": "работает",
    "fired": "сработало",
    "not loaded": "не загружено",
    "switched off after errors": "отключено после ошибок",
    "pack not active": "пакет не активен",
    "Test (simulate)": "Проверка (симуляция)",
    "Online player": "Игрок онлайн",
    "No player online: log in with the game to test.": "Никого нет онлайн: войдите в игру, чтобы проверить.",
    "Simulate": "Симулировать",
    "Conditions are evaluated on the chosen player now; actions are only described (nothing happens).": "Условия проверяются на выбранном игроке сейчас; действия только описываются (ничего не происходит).",
    "Would fire": "Сработает",
    "Would not fire": "Не сработает",
    "Blocked": "Заблокировано",
    "Problems": "Проблемы",
    "No problems.": "Проблем нет.",
    "Rule JSON": "JSON правила",
    "Edit the JSON…": "Править JSON…",
    "Apply": "Применить",
    "Not a JSON object": "Это не JSON-объект",
    "Delete…": "Удалить…",
    "Duplicate…": "Дублировать…",
    "Rename…": "Переименовать…",
    "Reload all rules on the server": "Перезагрузить все правила на сервере",
    "Delete the rule {1}?": "Удалить правило {1}?",
    "The file is deleted and the rules are reloaded.": "Файл удаляется, правила перезагружаются.",
    "Rule id": "Id правила",
    "a-z, 0-9, _ and - (the file name in rules/)": "a-z, 0-9, _ и - (имя файла в rules/)",
    "1 to 64 of a-z, 0-9, _ and -": "от 1 до 64 символов a-z, 0-9, _ и -",
    "This pack has it already": "В пакете уже есть такое",
    "Templates": "Шаблоны",
    "Empty rule": "Пустое правило",
    "Start from nothing: choose a trigger, add conditions and actions.": "С нуля: выберите триггер, добавьте условия и действия.",
    "Choose…": "Выбрать…",
    "Choose from the catalogue": "Выбрать из каталога",
    "Unsaved changes": "Несохранённые изменения",
    "{1} has unsaved changes.": "В {1} есть несохранённые изменения.",
    "Discard": "Отбросить",
    "Move up": "Выше",
    "Move down": "Ниже",
    "Remove": "Удалить",
    "Copy": "Копия",
    "Rules are server-only: Save applies them at once, no Generate, install or restart.": "Правила работают только на сервере: «Сохранить» применяет их сразу, без генерации, установки и перезапуска.",
    "This pack is not in Mods:Packs of the server: its rules are saved but do not run.": "Этого пакета нет в Mods:Packs сервера: правила сохраняются, но не работают.",
    "The event offers": "Событие даёт",
    "a number to change": "число для изменения",
    "a refusal": "запрет",
    "skipping the game's effect": "отмену стандартного эффекта",
    "the dropped loot": "выпавшую добычу",
    "a chat command": "команду чата",
    "a timer": "таймер",
    "Name": "Название",
    "player event": "игрок",
    "mob": "мобы",
    "item": "предметы",
    "spell": "заклинания",
    "buff": "баффы",
    "mount": "маунты",
    "formula": "формулы",
    "time": "время",
    "general": "общее",
    "unit": "юнит",
    "place": "место",
    "variable": "переменные",
    "event": "событие",
    "chat": "чат",
    "reward": "награды",
    "world": "мир",
    "flow": "поток",
    "needs": "нужно",
    "Loading the rules palette…": "Загрузка палитры правил…",
    "Reloaded: {1}": "Перезагружено: {1}",
    "Deleted": "Удалено",
    "Leave this rule?": "Покинуть правило?",
    "Rule": "Правило",
};
const tr = (s, params) => {
    let text = lang() === "ru" ? RU[s] ?? s : s;
    if (params) text = text.replace(/\{(\w+)\}/g, (m, k) => (params[k] !== undefined ? String(params[k]) : m));
    return text;
};
const L = (o) => (o ? (lang() === "ru" ? o.ru ?? o.en : o.en ?? o.ru) : "");
const GROUP = { player: "player event" };
const ID_RE = /^[a-z0-9][a-z0-9_-]{0,63}$/;
const UNIT_TARGETS = ["self", "killer", "victim", "caster", "target", "mob"];
const FEATURE_TEXT = { amount: "a number to change", cancel: "a refusal", prevent: "skipping the game's effect", loot: "the dropped loot", command: "a chat command", timer: "a timer" };

// ---- state

const S = {
    host: null,
    palette: null,
    list: null, // {pack, active, rules: [...], summary}
    cur: null, // {id, rule, savedText, isNew, diagnostics, live, sim, simPlayer, players}
};

let cssLoaded = false;
function ensureCss() {
    if (cssLoaded) return;
    cssLoaded = true;
    document.head.append(h("link", { rel: "stylesheet", href: "/studio/editors/rules.css" }));
}

async function palette() {
    if (!S.palette) S.palette = await R.palette();
    return S.palette;
}

const trig = (type) => S.palette?.triggers.find((x) => x.type === type);
const cond = (type) => S.palette?.conditions.find((x) => x.type === type);
const act = (type) => S.palette?.actions.find((x) => x.type === type);
const targetLabel = (v) => L(S.palette?.targets.find((x) => x.value === v)) || v;

function defaults(params) {
    const o = {};
    for (const p of params ?? []) if (p.default !== undefined && p.default !== null) o[p.name] = clone(p.default);
    return o;
}

function isModified() {
    return Boolean(S.cur && (S.cur.isNew || JSON.stringify(S.cur.rule) !== S.cur.savedText));
}

// ---- list (sidebar)

async function refreshList() {
    const pack = S.host?.pack();
    if (!pack) {
        S.list = null;
        return;
    }
    try {
        S.list = await R.list(pack);
    } catch (e) {
        S.list = { pack, rules: [], error: e.message };
    }
}

function sidebar() {
    ensureCss();
    const list = S.list;
    const rows = list?.rules ?? [];
    return h("div", { class: "entity-group rules-group" },
        h("div", { class: "group-title" }, tr("Rules"), h("span", { class: "muted" }, ` ${rows.length}`),
            h("button", { class: "btn tiny ghost", title: tr("New rule"), onClick: () => newRule() }, "+")),
        list && !list.active ? h("div", { class: "hint warn-text" }, tr("This pack is not in Mods:Packs of the server: its rules are saved but do not run.")) : null,
        list?.error ? h("div", { class: "hint bad" }, list.error) : null,
        rows.length === 0 ? h("div", { class: "hint" }, tr("No rules yet.")) : null,
        ...rows.map((r) => h("a", {
            class: `entity rule-row${S.cur?.id === r.id && !S.cur?.isNew ? " active" : ""}${r.enabled ? "" : " rule-off"}`,
            href: `#/r/${enc(list.pack)}/${enc(r.id)}`,
            onClick: (ev) => { ev.preventDefault(); S.host.show(r.id); },
        }, h("span", { class: "rule-glyph" }, r.enabled ? "⚙" : "○"),
        h("span", { class: "grow" }, plain("span", { class: "ename" }, r.name || r.id), plain("span", { class: "eid mono" }, `${r.id} · ${r.trigger ?? "?"}${r.filter ? " " + r.filter : ""}`)),
        r.problems.blocker ? h("span", { class: "dot bad", title: `${r.problems.blocker} blocker(s)` }) : r.live?.disabled ? h("span", { class: "dot bad", title: tr("switched off after errors") })
            : r.problems.warning ? h("span", { class: "dot warn", title: `${r.problems.warning} warning(s)` }) : r.live?.loaded ? h("span", { class: "dot ok-dot", title: `${tr("running")}, ${tr("fired")} ${r.live.fires}` }) : null)));
}

// ---- open / new / save

async function open(id) {
    await palette();
    const pack = S.host.pack();
    const r = await R.get(pack, id);
    if (!r.rule) {
        S.cur = { id, rule: { id, name: id, trigger: { type: "player.login" }, conditions: [], actions: [] }, savedText: "", isNew: false, diagnostics: r.diagnostics, raw: r.raw };
    } else {
        normalize(r.rule);
        S.cur = { id, rule: r.rule, savedText: JSON.stringify(r.rule), isNew: false, diagnostics: r.diagnostics };
    }
    S.cur.live = S.list?.rules.find((x) => x.id === id)?.live ?? null;
    loadPlayers();
}

/** The containers the form fills in (so opening a rule does not count as a change). */
function normalize(rule) {
    rule.trigger ??= { type: "player.login" };
    rule.conditions ??= [];
    rule.actions ??= [];
    for (const step of [...rule.conditions, ...rule.actions]) step.params ??= {};
    return rule;
}

function freshRule(id, template) {
    const rule = template ? clone(template) : { name: "", trigger: { type: "player.login" }, conditions: [], actions: [] };
    rule.schema = 1;
    rule.id = id;
    rule.enabled = rule.enabled ?? true;
    return normalize(rule);
}

async function askId(title, suggestion) {
    const taken = new Set((S.list?.rules ?? []).map((r) => r.id));
    let base = (suggestion || "rule").toLowerCase().replace(/[^a-z0-9_-]/g, "_").replace(/^[^a-z0-9]+/, "").slice(0, 56) || "rule";
    let id = base;
    for (let i = 2; taken.has(id); i++) id = `${base}_${i}`;
    return promptDialog(title, {
        label: tr("Rule id"), value: id, hint: tr("a-z, 0-9, _ and - (the file name in rules/)"),
        transform: (v) => v.toLowerCase().replace(/[^a-z0-9_-]/g, "_"),
        validate: (v) => (!ID_RE.test(v) ? tr("1 to 64 of a-z, 0-9, _ and -") : taken.has(v) ? tr("This pack has it already") : ""),
    });
}

async function newRule() {
    if (!S.host?.pack()) return toast("Create or choose a pack first", "warn");
    const pal = await palette();
    ensureCss();
    const picked = await modal({
        title: tr("New rule"),
        width: "860px",
        body: (close) => h("div", { class: "rule-gallery" },
            h("div", { class: "card action rule-template", onClick: () => close({ id: "rule", rule: null }) },
                h("b", {}, tr("Empty rule")), h("p", { class: "muted small" }, tr("Start from nothing: choose a trigger, add conditions and actions."))),
            ...pal.templates.map((tpl) => h("div", { class: "card action rule-template", onClick: () => close({ id: tpl.id, rule: tpl.rule }) },
                plain("b", {}, L(tpl)), plain("p", { class: "muted small" }, lang() === "ru" ? tpl.descRu : tpl.descEn),
                plain("div", { class: "mono small muted" }, `${tpl.rule.trigger.type}${tpl.rule.trigger.id ? " " + tpl.rule.trigger.id : ""} → ${tpl.rule.actions.map((a) => a.type).join(", ")}`)))),
    });
    if (!picked) return;
    if (isModified() && !(await leave())) return;
    const id = await askId(tr("New rule"), picked.id);
    if (!id) return;
    const rule = freshRule(id, picked.rule);
    if (!rule.name) rule.name = id;
    S.cur = { id, rule, savedText: "", isNew: true, diagnostics: [], live: null };
    loadPlayers();
    S.host.show(null);
    validateSoon();
}

async function save() {
    const c = S.cur;
    if (!c) return false;
    try {
        const r = await R.save(S.host.pack(), c.rule, c.isNew ? null : c.id);
        c.rule = r.rule;
        c.savedText = JSON.stringify(r.rule);
        c.isNew = false;
        c.id = r.rule.id;
        c.diagnostics = r.diagnostics;
        c.live = r.live;
        await refreshList();
        S.host.refresh();
        const blockers = r.diagnostics.filter((d) => d.severity === "blocker").length;
        if (blockers) toast(tr("Saved, with blockers: the rule is not loaded until they are fixed"), "warn");
        else if (!r.reloaded) toast(tr("Saved. The pack is not in Mods:Packs: its rules do not run on this server."), "warn");
        else toast(`${tr("Saved and applied on the server")}: ${r.summary}`, "ok");
        return true;
    } catch (e) {
        toast(e.message, "bad");
        return false;
    }
}

async function leave() {
    if (!isModified()) return true;
    const choice = await choiceDialog(tr("Unsaved changes"), tr("{1} has unsaved changes.", { 1: S.cur.id }), [
        { key: "discard", label: tr("Discard"), kind: "danger" }, { key: "save", label: tr("Save"), kind: "primary" }]);
    if (choice === "save") return save();
    if (choice === "discard") {
        S.cur = null;
        return true;
    }
    return false;
}

async function removeRule() {
    const c = S.cur;
    if (!c) return;
    if (c.isNew) {
        S.cur = null;
        return S.host.refresh(true);
    }
    if (!(await confirmDialog(tr("Delete the rule {1}?", { 1: c.id }), tr("The file is deleted and the rules are reloaded."), tr("Delete…").replace("…", ""), "danger"))) return;
    try {
        const r = await R.remove(S.host.pack(), c.id);
        S.cur = null;
        await refreshList();
        S.host.refresh(true);
        toast(`${tr("Deleted")}: ${r.summary}`, "ok");
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function duplicate() {
    const c = S.cur;
    if (!c) return;
    const id = await askId(tr("Duplicate…").replace("…", ""), c.id + "_copy");
    if (!id) return;
    const rule = clone(c.rule);
    rule.id = id;
    rule.name = `${rule.name || c.id} (${tr("Copy").toLowerCase()})`;
    S.cur = { id, rule, savedText: "", isNew: true, diagnostics: [], live: null, players: c.players, simPlayer: c.simPlayer };
    S.host.show(null);
    validateSoon();
}

async function rename() {
    const c = S.cur;
    if (!c) return;
    const id = await askId(tr("Rename…").replace("…", ""), c.id);
    if (!id || id === c.id) return;
    c.rule.id = id;
    if (c.isNew) {
        c.id = id;
        return render();
    }
    try {
        const r = await R.save(S.host.pack(), c.rule, c.id);
        c.rule = r.rule;
        c.id = id;
        c.savedText = JSON.stringify(r.rule);
        c.diagnostics = r.diagnostics;
        await refreshList();
        S.host.refresh();
        S.host.show(id);
    } catch (e) {
        c.rule.id = c.id;
        toast(e.message, "bad");
    }
}

// ---- validation and simulation

const validateSoon = debounce(async () => {
    const c = S.cur;
    if (!c) return;
    try {
        const r = await R.validate(S.host.pack(), c.rule);
        if (S.cur !== c) return;
        c.diagnostics = r.diagnostics;
        showProblems();
    } catch { /* keep the last */ }
}, 450);

async function loadPlayers() {
    const c = S.cur;
    try {
        const r = await R.players();
        if (!c) return;
        c.players = r.players;
        if (!c.simPlayer || !r.players.some((p) => p.name === c.simPlayer)) c.simPlayer = r.players[0]?.name ?? "";
        renderRightNow();
    } catch { /* not important */ }
}

async function simulate() {
    const c = S.cur;
    if (!c?.simPlayer) return;
    try {
        c.sim = await R.simulate(S.host.pack(), c.rule, c.simPlayer);
    } catch (e) {
        c.sim = { error: e.message };
    }
    renderRightNow();
}

// ---- editing

let centerEl = null;
let rightEl = null;

function set(path, value, { structural = false } = {}) {
    const c = S.cur;
    if (!c) return;
    setAt(c.rule, path, value);
    validateSoon();
    if (structural) render();
    else renderHead();
}

const fctx = () => ({ draft: S.cur.rule, set, rerender: render });

function refField(p, path, kind, { label, hint } = {}) {
    const c = S.cur;
    const get = () => path.split("/").slice(1).reduce((o, k) => (o == null ? undefined : o[k]), c.rule);
    const value = get() ?? "";
    const input = h("input", { type: "text", value: Array.isArray(value) ? value.join(",") : String(value), spellcheck: "false", "data-focus-key": path, placeholder: p?.default ?? "" });
    input.addEventListener("input", () => set(path, input.value.trim() === "" ? undefined : input.value.trim()));
    const choose = async () => {
        const picked = await pickerFor(kind, { current: input.value.includes("*") ? "" : input.value, allowPattern: kind === "mob" });
        if (picked) set(path, picked, { structural: true });
    };
    return h("div", { class: "field", dataset: { path } },
        h("div", { class: "field-head" }, h("label", { class: "label" }, label ?? L(p), p?.required ? h("span", { class: "req" }, " *") : null),
            p?.name ? plain("span", { class: "attr-name mono" }, p.name) : null),
        h("div", { class: "ref-input" }, input, h("button", { class: "btn small", title: tr("Choose from the catalogue"), onClick: choose }, tr("Choose…"))),
        hint ? h("div", { class: "hint" }, hint) : null,
        h("div", { class: "field-problems" }));
}

function paramField(p, path) {
    const hint = lang() === "ru" ? p.hintRu ?? p.hint : p.hint;
    const base = { path, label: L(p), name: p.name, required: p.required, hint, min: p.min, max: p.max };
    const optionLabel = (o) => (S.palette.targets.find((x) => x.value === o) ? targetLabel(o) : o);
    switch (p.type) {
        case "item": case "mount": return refField(p, path, "item", { hint });
        case "spell": case "buff": return refField(p, path, "spell", { hint });
        case "mob": return refField(p, path, "mob", { hint });
        case "cluster": case "time": case "list": case "text":
            return renderField({ ...base, type: "text", placeholder: p.default !== undefined && p.default !== null ? String(p.default) : "" }, fctx());
        case "textarea": return renderField({ ...base, type: "textarea", rows: 2 }, fctx());
        case "int": case "seconds": return renderField({ ...base, type: p.type, default: p.default }, fctx());
        case "number": return renderField({ ...base, type: "number", default: p.default, suffix: /percent/.test(p.name) ? "%" : "" }, fctx());
        case "bool": return renderField({ ...base, type: "bool", default: p.default ?? false }, fctx());
        case "select": return renderField({ ...base, type: "select", default: p.default, options: p.options.map((o) => ({ value: o, label: optionLabel(o) })) }, fctx());
        case "flags": return renderField({ ...base, type: "flags", options: p.options }, fctx());
        default: return renderField({ ...base, type: "text" }, fctx());
    }
}

function typeSelect(defs, current, onChange) {
    const groups = new Map();
    for (const d of defs) (groups.get(d.group) ?? groups.set(d.group, []).get(d.group)).push(d);
    const select = h("select", { class: "rule-type" },
        current && !defs.some((d) => d.type === current) ? h("option", { value: current, selected: true }, `${current} ?`) : null,
        ...[...groups].map(([group, list]) => h("optgroup", { label: tr(GROUP[group] ?? group) },
            ...list.map((d) => h("option", { value: d.type, selected: d.type === current }, `${L(d)}`)))));
    select.addEventListener("change", () => onChange(select.value));
    return select;
}

function move(listPath, list, i, delta) {
    const next = [...list];
    const j = i + delta;
    if (j < 0 || j >= next.length) return;
    [next[i], next[j]] = [next[j], next[i]];
    set(listPath, next, { structural: true });
}

function stepCard(kind, step, i, list) {
    const isAction = kind === "actions";
    const def = isAction ? act(step.type) : cond(step.type);
    const listPath = `/${kind}`;
    const path = `${listPath}/${i}`;
    const t = trig(S.cur.rule.trigger?.type);
    const targets = t?.type === "chain" ? S.palette.targets.map((x) => x.value) : t?.targets ?? [];
    const head = h("div", { class: "row wrap step-head", dataset: { path: path + "/type" } },
        h("span", { class: "step-num" }, String(i + 1)),
        typeSelect(isAction ? S.palette.actions : S.palette.conditions, step.type, (type) => {
            const d = isAction ? act(type) : cond(type);
            const next = { type, params: defaults(d?.params) };
            if (step.target) next.target = step.target;
            set(path, next, { structural: true });
        }));
    if (!isAction) {
        const not = h("input", { type: "checkbox", checked: Boolean(step.not) });
        not.addEventListener("change", () => set(path + "/not", not.checked || undefined, { structural: true }));
        head.append(h("label", { class: "check not-toggle", title: "invert" }, not, " ", tr("NOT")));
        if (def?.who) {
            const who = h("select", { "data-focus-key": path + "/target" }, ...UNIT_TARGETS.filter((x) => x === "self" || targets.includes(x)).map((x) =>
                h("option", { value: x, selected: (step.target ?? "self") === x }, targetLabel(x))));
            who.addEventListener("change", () => set(path + "/target", who.value === "self" ? undefined : who.value, { structural: true }));
            head.append(h("span", { class: "muted small" }, tr("looks at")), h("span", { dataset: { path: path + "/target" } }, who));
        }
    } else if (def?.targeted) {
        const whom = h("select", { "data-focus-key": path + "/target" }, h("option", { value: "" }, `${tr("(rule's default)")}: ${targetLabel(S.cur.rule.target ?? "self")}`),
            ...targets.map((x) => h("option", { value: x, selected: step.target === x }, targetLabel(x))));
        whom.addEventListener("change", () => set(path + "/target", whom.value || undefined, { structural: true }));
        head.append(h("span", { class: "muted small" }, tr("whom")), h("span", { dataset: { path: path + "/target" } }, whom));
        if ((step.target ?? S.cur.rule.target) === "radius") {
            const radius = h("input", { type: "text", class: "num", value: String(step.radius ?? 20), "data-focus-key": path + "/radius" });
            radius.addEventListener("input", () => set(path + "/radius", Number(radius.value) || radius.value));
            head.append(h("span", { class: "muted small", dataset: { path: path + "/radius" } }, tr("Radius, m"), " ", radius));
        }
    }
    if (def?.needs?.length) head.append(h("span", { class: `chip${def.needs.every((n) => t?.features.includes(n)) ? "" : " bad"}` }, `${tr("needs")}: ${def.needs.map((n) => tr(FEATURE_TEXT[n] ?? n)).join(", ")}`));
    head.append(h("span", { class: "grow" }),
        h("button", { class: "btn tiny ghost", title: tr("Move up"), disabled: i === 0, onClick: () => move(listPath, list, i, -1) }, "↑"),
        h("button", { class: "btn tiny ghost", title: tr("Move down"), disabled: i === list.length - 1, onClick: () => move(listPath, list, i, 1) }, "↓"),
        h("button", { class: "btn tiny ghost", title: tr("Copy"), onClick: () => set(listPath, [...list.slice(0, i + 1), clone(step), ...list.slice(i + 1)], { structural: true }) }, "⧉"),
        h("button", { class: "btn tiny ghost", title: tr("Remove"), onClick: () => set(listPath, list.filter((_, k) => k !== i), { structural: true }) }, "✕"),
        h("div", { class: "field-problems" }));
    step.params ??= {};
    return h("div", { class: `step-card ${isAction ? "action-card" : "condition-card"}${step.not ? " negated" : ""}`, dataset: { path } },
        head,
        def?.params?.length ? h("div", { class: "grid step-params" }, ...def.params.map((p) => paramField(p, `${path}/params/${p.name}`))) : null,
        h("div", { class: "field-problems" }));
}

function featureLine(t) {
    if (!t?.features?.length) return null;
    return h("div", { class: "hint" }, `${tr("The event offers")}: ${t.features.map((f) => tr(FEATURE_TEXT[f] ?? f)).join(", ")}${t.features.includes("amount") && t.amountEn ? ` (${lang() === "ru" ? t.amountRu : t.amountEn})` : ""}`);
}

function block(title, sub, ...children) {
    return h("section", { class: "rule-block card" }, h("h3", {}, h("span", { class: "rule-word" }, title), sub ? h("span", { class: "muted small" }, " ", sub) : null), ...children);
}

function renderBody() {
    const c = S.cur;
    const rule = c.rule;
    rule.trigger ??= { type: "player.login" };
    const t = trig(rule.trigger.type);
    const idKinds = { spell: "spell", buff: "spell", item: "item", mount: "item", mob: "mob" };
    const when = block(tr("When"), null,
        h("div", { class: "grid" },
            h("div", { class: "field wide", dataset: { path: "/trigger/type" } }, h("label", { class: "label" }, tr("Trigger")),
                typeSelect(S.palette.triggers, rule.trigger.type, (type) => {
                    const d = trig(type);
                    const next = { type, params: defaults(d?.params) };
                    if (d?.idKind && d.idKind === t?.idKind && rule.trigger.id) next.id = rule.trigger.id;
                    set("/trigger", next, { structural: true });
                }), h("div", { class: "field-problems" })),
            t?.idKind ? idKinds[t.idKind] ? refField(null, "/trigger/id", idKinds[t.idKind], { label: `${tr("Only for (id filter)")}: ${tr(t.idKind)}`, hint: tr("empty = every one; * matches any text; a comma list = any of them") })
                : renderField({ path: "/trigger/id", label: `${tr("Only for (id filter)")}: ${t.idKind}`, type: "text", hint: tr("empty = every one; * matches any text; a comma list = any of them") }, fctx()) : null,
            ...(t?.params ?? []).map((p) => paramField(p, `/trigger/params/${p.name}`))),
        featureLine(t));
    const conditions = rule.conditions ?? (rule.conditions = []);
    const matchSel = h("select", {}, h("option", { value: "all", selected: rule.match !== "any" }, tr("All conditions")), h("option", { value: "any", selected: rule.match === "any" }, tr("Any condition")));
    matchSel.addEventListener("change", () => set("/match", matchSel.value === "all" ? undefined : matchSel.value, { structural: true }));
    const ifBlock = block(tr("If"), null, h("div", { class: "row" }, matchSel),
        conditions.length === 0 ? h("div", { class: "hint" }, tr("No conditions: it always fires.")) : null,
        ...conditions.map((s, i) => stepCard("conditions", s, i, conditions)),
        h("button", { class: "btn small", onClick: () => set("/conditions", [...conditions, { type: "chance", params: defaults(cond("chance").params) }], { structural: true }) }, tr("+ Condition")));
    const actions = rule.actions ?? (rule.actions = []);
    const doBlock = block(tr("Do"), null,
        actions.length === 0 ? h("div", { class: "hint" }, tr("No actions yet.")) : null,
        ...actions.map((s, i) => stepCard("actions", s, i, actions)),
        h("button", { class: "btn small", onClick: () => set("/actions", [...actions, { type: "message", params: defaults(act("message").params) }], { structural: true }) }, tr("+ Action")));
    const targets = t?.type === "chain" ? S.palette.targets.map((x) => x.value) : t?.targets ?? ["self"];
    const whom = block(tr("Whom"), null, renderField({
        path: "/target", label: tr("Default target of the actions"), type: "select", default: targets.includes("self") ? "self" : targets[0],
        options: targets.map((x) => ({ value: x, label: targetLabel(x) })), hint: tr("Each action can name its own target; this one is used when it does not."),
    }, fctx()));
    const limits = block(tr("Limits"), null, h("div", { class: "grid" },
        renderField({ path: "/cooldown/seconds", label: tr("Cooldown, s"), type: "seconds", min: 0, default: 0 }, fctx()),
        renderField({ path: "/cooldown/per", label: tr("per"), type: "select", default: "player", options: [{ value: "player", label: tr("player") }, { value: "global", label: tr("global") }] }, fctx()),
        renderField({ path: "/limits/maxFires", label: tr("Max fires (0 = no limit)"), type: "int", min: 0, default: 0 }, fctx()),
        renderField({ path: "/limits/per", label: tr("per"), type: "select", default: "player", options: [{ value: "player", label: tr("player") }, { value: "global", label: tr("global") }] }, fctx()),
        renderField({ path: "/limits/perSecond", label: tr("Fires per second at most"), type: "int", min: 1, max: 1000, default: 20 }, fctx()),
        t?.type === "command" ? renderField({ path: "/failMessage", label: tr("Message when the conditions fail (commands)"), type: "text" }, fctx()) : null));
    return h("div", { class: "rule-editor" }, when, ifBlock, doBlock, whom, limits,
        h("p", { class: "muted small" }, tr("Rules are server-only: Save applies them at once, no Generate, install or restart.")));
}

function liveChip() {
    const c = S.cur;
    if (S.list && !S.list.active) return h("span", { class: "chip warn" }, tr("pack not active"));
    if (c.isNew) return null;
    const live = c.live;
    if (!live) return null;
    if (live.disabled) return h("span", { class: "chip bad", title: live.why ?? "" }, tr("switched off after errors"));
    if (!live.loaded) return h("span", { class: "chip bad" }, tr("not loaded"));
    return h("span", { class: "chip ok-chip" }, `${tr("running")} · ${tr("fired")} ${live.fires}${live.errors ? ` · ⚠ ${live.errors}` : ""}`);
}

function renderHead() {
    const head = centerEl?.querySelector("#rule-head");
    if (!head || !S.cur) return;
    const c = S.cur;
    const name = h("input", { type: "text", class: "rule-name", value: c.rule.name ?? "", placeholder: tr("Name"), "data-focus-key": "/name" });
    name.addEventListener("input", () => { c.rule.name = name.value; validateSoon(); markModified(); });
    const enabled = h("input", { type: "checkbox", checked: c.rule.enabled !== false });
    enabled.addEventListener("change", () => set("/enabled", enabled.checked, { structural: true }));
    const blockers = (c.diagnostics ?? []).filter((d) => d.severity === "blocker").length;
    const warnings = (c.diagnostics ?? []).filter((d) => d.severity === "warning").length;
    remount(head,
        h("div", { class: "rule-icon" }, "⚙"),
        h("div", { class: "grow" },
            h("div", { class: "title" }, name, h("span", { id: "rule-modified" })),
            h("div", { class: "sub" }, h("span", { class: "chip" }, tr("Rule")), plain("span", { class: "mono" }, `rules/${c.id}.json`), " ",
                h("label", { class: "switch" }, enabled, h("span", {}, c.rule.enabled !== false ? tr("on") : tr("off"))), " ", liveChip(),
                blockers ? h("span", { class: "chip bad" }, `⛔ ${blockers}`) : null, warnings ? h("span", { class: "chip warn" }, `⚠ ${warnings}`) : null)),
        h("div", { class: "row" },
            h("button", { class: "btn primary", onClick: save, title: "Ctrl+S" }, tr("Save")),
            menuButton("⋯", [
                { label: tr("Rename…"), onClick: rename },
                { label: tr("Duplicate…"), onClick: duplicate },
                { label: tr("Edit the JSON…"), onClick: editJson },
                { label: tr("Reload all rules on the server"), onClick: reloadAll },
                { separator: true },
                { label: tr("Delete…"), onClick: removeRule, danger: true },
            ], { className: "btn ghost" })));
    markModified();
}

function markModified() {
    const node = centerEl?.querySelector("#rule-modified");
    if (node) mount(node, isModified() ? h("span", { class: "chip changed" }, tr("unsaved")) : "");
}

async function reloadAll() {
    try {
        const r = await R.reload();
        toast(tr("Reloaded: {1}", { 1: r.summary }), "ok");
        await refreshList();
        if (S.cur) S.cur.live = S.list?.rules.find((x) => x.id === S.cur.id)?.live ?? S.cur.live;
        S.host.refresh();
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function editJson() {
    const c = S.cur;
    if (!c) return;
    const text = await modal({
        title: tr("Rule JSON"),
        width: "760px",
        body: (close) => {
            const area = h("textarea", { class: "mono rule-json", rows: 24, spellcheck: "false" });
            area.value = JSON.stringify(c.rule, null, 2);
            const error = h("div", { class: "field-error" });
            return h("div", { class: "stack" }, area, error, h("div", { class: "row end" },
                h("button", { class: "btn primary", onClick: () => {
                    try {
                        const parsed = JSON.parse(area.value);
                        if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error(tr("Not a JSON object"));
                        close(parsed);
                    } catch (e) { error.textContent = e.message; }
                } }, tr("Apply"))));
        },
    });
    if (!text) return;
    text.id = c.id;
    c.rule = text;
    validateSoon();
    render();
}

function render() {
    if (!centerEl || !S.cur) return;
    remount(centerEl, h("div", { class: "editor rules-view" },
        h("div", { id: "rule-head", class: "editor-head" }),
        h("div", { class: "editor-body", "data-scroll-key": "rule-editor" }, renderBody())));
    renderHead();
    showProblems();
}

function showProblems() {
    if (centerEl) {
        renderHead();
        const body = centerEl.querySelector(".editor-body");
        if (body) applyDiagnostics(body, (S.cur?.diagnostics ?? []).map((d) => ({ ...d, path: d.path || "" })));
    }
    renderRightNow();
}

// ---- right pane: problems, simulation, JSON

function renderRightNow() {
    if (!rightEl || !S.cur) return;
    const c = S.cur;
    const diags = c.diagnostics ?? [];
    const players = c.players ?? [];
    const select = h("select", {}, ...players.map((p) => h("option", { value: p.name, selected: p.name === c.simPlayer }, `${p.name} (${p.cluster})`)));
    select.addEventListener("change", () => { c.simPlayer = select.value; });
    const sim = c.sim;
    remount(rightEl, h("div", { class: "preview rules-right" },
        h("div", { class: "tabs" }, h("span", { class: "tab active" }, tr("Rule"))),
        h("div", { class: "preview-body", "data-scroll-key": "rule-right" },
            h("h4", {}, tr("Problems")),
            diags.length === 0 ? h("div", { class: "ok-note" }, tr("No problems.")) : h("div", { class: "problems" },
                ...diags.map((d) => h("div", { class: `problem ${d.severity}` }, d.severity === "blocker" ? "⛔ " : "⚠ ", plain("span", { class: "mono small" }, d.path || ""), " ", plain("span", {}, diagText(d))))),
            h("h4", {}, tr("Test (simulate)")),
            h("p", { class: "muted small" }, tr("Conditions are evaluated on the chosen player now; actions are only described (nothing happens).")),
            players.length === 0 ? h("div", { class: "hint" }, tr("No player online: log in with the game to test.")) : h("div", { class: "row" }, select,
                h("button", { class: "btn small primary", onClick: simulate }, tr("Simulate")), h("button", { class: "btn small ghost", title: "↻", onClick: loadPlayers }, "↻")),
            sim ? simView(sim) : null,
            h("details", { class: "rule-json-view" }, h("summary", {}, tr("Rule JSON")), plain("pre", { class: "xml" }, JSON.stringify(c.rule, null, 2))))));
}

function simView(sim) {
    if (sim.error) return h("div", { class: "problem blocker" }, plain("span", {}, sim.error));
    return h("div", { class: "sim" },
        h("div", { class: `sim-verdict ${sim.wouldFire ? "yes" : "no"}` }, sim.wouldFire ? `✓ ${tr("Would fire")}` : `✕ ${tr("Would not fire")}`),
        sim.blocked ? h("div", { class: "problem warning" }, `${tr("Blocked")}: `, plain("span", {}, sim.blocked)) : null,
        plain("div", { class: "muted small" }, `${sim.player}${sim.mob ? " · " + sim.mob : ""}${sim.event?.id ? " · " + sim.event.id : ""}`),
        ...(sim.notes ?? []).map((n) => plain("div", { class: "hint" }, n)),
        h("ol", { class: "sim-list" }, ...(sim.conditions ?? []).map((x) => h("li", { class: x.pass ? "pass" : "fail" }, x.pass ? "✓ " : "✕ ", plain("b", {}, x.type), " ", plain("span", {}, x.detail)))),
        h("ol", { class: "sim-list actions" }, ...(sim.actions ?? []).map((x) => h("li", {}, "→ ", plain("b", {}, x.type), " ", plain("span", {}, x.detail)))));
}

// ---- the shell's interface

export const rulesUi = {
    attach(host) {
        S.host = host;
        ensureCss();
    },
    sidebar,
    async open(id) {
        if (!S.list) await refreshList();
        await open(id);
    },
    newRule,
    isModified,
    leave,
    current: () => S.cur?.id ?? null,
    async packChanged() {
        S.cur = null;
        await refreshList();
    },
    renderCenter(el) {
        centerEl = el;
        if (!S.cur) {
            mount(el, h("div", { class: "card" }, S.palette ? tr("No rules yet.") : tr("Loading the rules palette…")));
            return;
        }
        if (!S.palette) {
            mount(el, h("div", { class: "card" }, tr("Loading the rules palette…")));
            palette().then(() => render());
            return;
        }
        render();
    },
    renderRight(el) {
        rightEl = el;
        if (!S.cur) mount(el, h("div", { class: "preview" }));
        else renderRightNow();
    },
    save,
};
