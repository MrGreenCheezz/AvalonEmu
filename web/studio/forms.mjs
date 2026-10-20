// The generic field renderer: one descriptor {path, label, type, hint, required, default, min, max, options, refKind, template}
// becomes an input bound to the draft at its JSON pointer. Every field wrapper carries data-path, so diagnostics can be shown
// inline (applyDiagnostics) without rendering the form again.
//
// Types: text, textarea, loc ({en, ru}), number, int, percent (0..1 shown as %), seconds, bool, select, flags (array of options),
// item, spell, mob, icon, station, lootList (a reference with its picker), refList (array of references of refKind),
// itemList (ingredients [{item, count}]), drops ([{mobs, chests, category, chance, amount}]), readonly.

import { t } from "./i18n.mjs";
import { pickerFor, refBadge } from "./pickers.mjs";
import { problemLine } from "./diag.mjs";
import { getAt, h, plain, pointer } from "./ui.mjs";

const REF_TYPES = { item: "item", spell: "spell", mob: "mob", icon: "icon", station: "station", lootList: "lootlist" };

/**
 * The form context an editor passes: the draft, change notifications and a structural re-render.
 * @typedef {{draft: object, set: (path: string, value: any, opts?: {structural?: boolean}) => void, rerender: () => void}} FormCtx
 */

/**
 * Typing (SPEC §9.1): what the user types is never rewritten while they type. live() runs on every keystroke (outside an IME
 * composition) and may store the value in the draft but must not touch the input; commit() runs once the input is finished (blur,
 * change, Enter) and may normalise the shown text (upper case, "1,5" → "1.5"). data-editing marks the field meanwhile, so a re-render
 * (ui.mjs remount) keeps the raw text, the focus and the caret.
 */
export function bindTyping(input, { live, commit } = {}) {
    input.addEventListener("input", (e) => {
        input.dataset.editing = "1";
        if (!e.isComposing) live?.();
    });
    const done = () => {
        // A re-render removed this input (remount carried its text over to the new one): nothing to commit here.
        if (input.dataset.editing !== "1" || !input.isConnected) return;
        delete input.dataset.editing;
        commit?.();
    };
    input.addEventListener("change", done);
    input.addEventListener("blur", done);
    input.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.isComposing && input.tagName !== "TEXTAREA") done(); });
    return input;
}

/** A number as typed: "" (empty), a finite number, or NaN (not a number, or not finished: "-", "1e"). */
export function parseNumber(text) {
    const s = String(text ?? "").trim().replace(",", ".");
    if (s === "") return "";
    return /^[-+]?(\d+\.?\d*|\.\d+)(e[-+]?\d+)?$/i.test(s) ? Number(s) : NaN;
}

/** The wrapper of one field: label, input, hint, the template's value and a reset button, inline problems. */
function wrap(desc, input, ctx, { wide = false } = {}) {
    const value = getAt(ctx.draft, desc.path);
    const changed = desc.template !== undefined && value !== undefined && value !== null && String(value) !== String(desc.template);
    const dataset = { path: desc.path };
    if (desc.template !== undefined) dataset.template = String(desc.template);
    return h("div", { class: `field${wide || desc.wide ? " wide" : ""}${changed ? " changed" : ""}`, dataset },
        h("div", { class: "field-head" },
            h("label", { class: "label", title: desc.name ?? "" }, desc.label ?? desc.name, desc.required ? h("span", { class: "req" }, " *") : null),
            desc.name && desc.label && desc.label !== desc.name ? plain("span", { class: "attr-name mono" }, desc.name) : null,
            changed ? h("button", { class: "btn tiny ghost", title: t("Back to the template's value: {1}", { 1: desc.template }), onClick: () => ctx.set(desc.path, undefined, { structural: true }) }, "↺") : null),
        input,
        desc.hint ? h("div", { class: "hint" }, desc.hint) : null,
        desc.range ? rangeHelper(desc, ctx) : null,
        desc.template !== undefined && desc.type !== "bool" ? h("div", { class: "template-value" }, t("template:"), " ", plain("span", { class: "mono" }, String(desc.template))) : null,
        h("div", { class: "field-problems" }));
}

/**
 * The value helper (phase 3): the official min / median / max of this number among items or spells of the same kind (desc.range =
 * {min, median, max, n}), a bar showing where the current value sits, and "Suggest" (the median).
 */
function rangeHelper(desc, ctx) {
    const r = desc.range;
    const scale = desc.type === "percent" ? 100 : 1;
    const fmt = (v) => `${+(Number(v) * scale).toFixed(scale === 100 ? 1 : 3)}${scale === 100 ? "%" : ""}`;
    const raw = getAt(ctx.draft, desc.path);
    const current = raw === undefined || raw === null || raw === "" ? Number(desc.template) : Number(raw);
    const span = Number(r.max) - Number(r.min);
    const at = Number.isFinite(current) && span > 0 ? Math.max(0, Math.min(1, (current - r.min) / span)) : null;
    const out = Number.isFinite(current) && (current < r.min || current > r.max);
    return h("div", { class: `range-helper${out ? " out" : ""}`, title: t("Official values of the same kind ({1} found)", { 1: r.n }) },
        h("span", { class: "muted small" }, t("official:")), " ",
        plain("span", { class: "mono small" }, fmt(r.min)),
        h("span", { class: "range-bar" }, h("span", { class: "median", style: { left: `${span > 0 ? ((r.median - r.min) / span) * 100 : 50}%` } }),
            at === null ? null : h("span", { class: "now", style: { left: `${at * 100}%` } })),
        plain("span", { class: "mono small" }, fmt(r.max)), " ",
        h("button", { class: "btn tiny", title: t("Use the official median ({1})", { 1: fmt(r.median) }),
            onClick: () => ctx.set(desc.path, desc.asString ? String(r.median) : r.median, { structural: true }) }, t("Suggest"), " ", plain("span", { class: "mono" }, fmt(r.median))));
}

function numberInput(desc, ctx, { scale = 1, suffix = "" } = {}) {
    const raw = getAt(ctx.draft, desc.path);
    const shown = raw === undefined || raw === null || raw === "" ? "" : scale === 1 ? String(raw) : String(+(Number(raw) * scale).toFixed(6));
    const input = h("input", {
        type: "text", inputmode: "decimal", value: shown, spellcheck: "false", "data-focus-key": desc.path,
        placeholder: desc.template !== undefined ? String(scale === 1 ? desc.template : +(Number(desc.template) * scale).toFixed(6)) : desc.default !== undefined ? String(desc.default) : "",
    });
    const store = (n) => {
        const value = scale === 1 ? n : +(n / scale).toFixed(8);
        ctx.set(desc.path, desc.asString ? String(value) : value);
    };
    // While typing: a complete number goes to the draft (the preview follows), nothing is clamped or marked ("1" on the way to "12").
    const live = () => {
        input.classList.remove("invalid");
        const n = parseNumber(input.value);
        if (n === "") ctx.set(desc.path, undefined);
        else if (Number.isFinite(n)) store(n);
    };
    // Finished: the text is stored even when wrong (the check says why), out-of-range values are marked, "1,5" shows as "1.5".
    const commit = () => {
        const text = input.value.trim().replace(",", ".");
        if (text !== input.value) input.value = text;
        const n = parseNumber(text);
        if (n === "") return ctx.set(desc.path, undefined);
        input.classList.toggle("invalid", !Number.isFinite(n) || (desc.type === "int" && !Number.isInteger(n)) || (desc.min !== undefined && n < desc.min) || (desc.max !== undefined && n > desc.max));
        if (!Number.isFinite(n)) return ctx.set(desc.path, text);
        store(n);
    };
    bindTyping(input, { live, commit });
    return suffix ? h("div", { class: "with-suffix" }, input, h("span", { class: "suffix" }, suffix)) : input;
}

function refInput(desc, ctx, kind) {
    const value = getAt(ctx.draft, desc.path) ?? "";
    const choose = async () => {
        const picked = await pickerFor(kind, { current: value, filters: desc.filters });
        if (picked !== undefined) ctx.set(desc.path, picked, { structural: true });
    };
    return h("div", { class: "ref-input" },
        refBadge(kind, value, { onClick: choose }),
        h("button", { class: "btn small", onClick: choose, title: t("Choose from the catalogue") }, "Choose…"),
        value && !desc.required ? h("button", { class: "btn small ghost", title: "Clear", onClick: () => ctx.set(desc.path, undefined, { structural: true }) }, "✕") : null);
}

/** One field for a descriptor. */
export function renderField(desc, ctx) {
    const value = getAt(ctx.draft, desc.path);
    switch (desc.type) {
        case "readonly":
            return wrap(desc, plain("div", { class: "readonly mono" }, value ?? desc.template ?? ""), ctx);
        case "text":
        case "textarea": {
            const input = h(desc.type === "textarea" ? "textarea" : "input", {
                type: "text", value: value ?? "", rows: desc.rows ?? 3, maxlength: desc.max ?? 1000, spellcheck: desc.spellcheck ? "true" : "false",
                placeholder: desc.template ?? desc.placeholder ?? "", "data-focus-key": desc.path,
            });
            if (desc.type === "textarea") input.value = value ?? "";
            // The draft gets the normalised value (e.g. an id in upper case) at once; the field shows it only once typing is finished.
            const valueOf = () => (input.value === "" && desc.template !== undefined ? undefined : desc.transform ? desc.transform(input.value) : input.value);
            bindTyping(input, {
                live: () => ctx.set(desc.path, valueOf()),
                commit: () => {
                    const v = valueOf();
                    if (desc.transform && v !== undefined && v !== input.value) input.value = v;
                    ctx.set(desc.path, v);
                },
            });
            return wrap(desc, input, ctx, { wide: desc.type === "textarea" });
        }
        case "loc": {
            const v = value ?? {};
            const field = (key, label) => {
                const multi = desc.multiline;
                const input = h(multi ? "textarea" : "input", { type: "text", rows: 3, maxlength: multi ? 1000 : 200, "data-focus-key": `${desc.path}/${key}`, spellcheck: "true", lang: key });
                input.value = v[key] ?? "";
                bindTyping(input, { live: () => ctx.set(`${desc.path}/${key}`, input.value), commit: () => ctx.set(`${desc.path}/${key}`, input.value) });
                return h("div", { class: "loc-row", dataset: { path: `${desc.path}/${key}` } }, h("span", { class: "lang-tag" }, label), input, h("div", { class: "field-problems" }));
            };
            return wrap(desc, h("div", { class: "loc" }, field("ru", "RU"), field("en", "EN")), ctx, { wide: true });
        }
        case "number":
        case "int":
        case "seconds":
            return wrap(desc, numberInput(desc, ctx, { suffix: desc.type === "seconds" ? t("s") : desc.suffix ?? "" }), ctx);
        case "percent":
            return wrap(desc, numberInput(desc, ctx, { scale: 100, suffix: "%" }), ctx);
        case "bool": {
            const current = value === undefined ? desc.template ?? desc.default : value;
            const on = current === true || current === "true";
            const box = h("input", { type: "checkbox", checked: on, "data-focus-key": desc.path });
            box.addEventListener("change", () => {
                const next = desc.asString ? String(box.checked) : box.checked;
                ctx.set(desc.path, desc.template !== undefined && String(next) === String(desc.template) ? undefined : next, { structural: true });
            });
            return wrap(desc, h("label", { class: "switch" }, box, h("span", {}, on ? t("yes") : t("no"))), ctx);
        }
        case "select": {
            const select = h("select", { "data-focus-key": desc.path },
                ...(desc.allowEmpty ? [h("option", { value: "" }, desc.emptyLabel ?? "—")] : []),
                ...(desc.options ?? []).map((o) => {
                    const opt = typeof o === "object" ? o : { value: o, label: String(o) };
                    return h("option", { value: opt.value, selected: String(value ?? desc.template ?? desc.default ?? "") === String(opt.value) }, opt.label ?? opt.value);
                }));
            select.addEventListener("change", () => ctx.set(desc.path, select.value === "" ? undefined : select.value, { structural: true }));
            return wrap(desc, select, ctx);
        }
        case "flags": {
            const set = new Set(Array.isArray(value) ? value : []);
            return wrap(desc, h("div", { class: "flags" }, ...(desc.options ?? []).map((o) => {
                const opt = typeof o === "object" ? o : { value: o, label: o };
                const box = h("input", { type: "checkbox", checked: set.has(opt.value) });
                box.addEventListener("change", () => {
                    if (box.checked) set.add(opt.value); else set.delete(opt.value);
                    ctx.set(desc.path, [...set], { structural: true });
                });
                return h("label", { class: "check" }, box, " ", opt.label);
            })), ctx, { wide: true });
        }
        case "item": case "spell": case "mob": case "icon": case "station": case "lootList":
            return wrap(desc, refInput(desc, ctx, REF_TYPES[desc.type]), ctx);
        case "refList":
            return wrap(desc, refList(desc, ctx), ctx, { wide: true });
        case "itemList":
            return wrap(desc, itemList(desc, ctx), ctx, { wide: true });
        case "drops":
            return wrap(desc, dropsList(desc, ctx), ctx, { wide: true });
        default:
            return wrap(desc, plain("div", { class: "bad" }, `unknown field type ${desc.type}`), ctx);
    }
}

/** A list of references of one kind (stations): badges with remove, and Add… */
function refList(desc, ctx) {
    const kind = REF_TYPES[desc.refKind] ?? desc.refKind;
    const list = Array.isArray(getAt(ctx.draft, desc.path)) ? getAt(ctx.draft, desc.path) : [];
    const add = async () => {
        const picked = await pickerFor(kind, { filters: desc.filters });
        if (picked && !list.includes(picked)) ctx.set(desc.path, [...list, picked], { structural: true });
    };
    return h("div", { class: "ref-list" },
        ...list.map((name, i) => h("div", { class: "ref-item", dataset: { path: pointer(...pathParts(desc.path), i) } },
            refBadge(kind, name),
            h("button", { class: "btn tiny ghost", title: "Remove", onClick: () => ctx.set(desc.path, list.filter((_, k) => k !== i), { structural: true }) }, "✕"),
            h("div", { class: "field-problems" }))),
        h("button", { class: "btn small", onClick: add }, "+ Add…"));
}

function pathParts(path) {
    return String(path).split("/").slice(1).map((s) => s.replace(/~1/g, "/").replace(/~0/g, "~"));
}

/** Ingredients: rows of item + count. */
function itemList(desc, ctx) {
    const list = Array.isArray(getAt(ctx.draft, desc.path)) ? getAt(ctx.draft, desc.path) : [];
    const rowPath = (i) => pointer(...pathParts(desc.path), i);
    const add = async () => {
        const picked = await pickerFor("item", { title: t("Add an ingredient") });
        if (picked) ctx.set(desc.path, [...list, { item: picked, count: 1 }], { structural: true });
    };
    return h("div", { class: "item-list" },
        list.length === 0 ? h("div", { class: "muted small" }, desc.emptyText ?? "No ingredients: not craftable.") : null,
        ...list.map((row, i) => {
            const count = h("input", { type: "text", inputmode: "numeric", value: String(row.count ?? 1), class: "count", "aria-label": "count", "data-focus-key": rowPath(i) + "/count" });
            bindTyping(count, {
                live: () => { count.classList.remove("invalid"); const n = parseNumber(count.value); if (Number.isFinite(n)) ctx.set(rowPath(i) + "/count", n); },
                commit: () => {
                    const n = parseNumber(count.value);
                    count.classList.toggle("invalid", !Number.isInteger(n) || n < 1 || n > 9999);
                    ctx.set(rowPath(i) + "/count", Number.isFinite(n) ? n : count.value);
                },
            });
            const choose = async () => {
                const picked = await pickerFor("item", { current: row.item });
                if (picked) ctx.set(rowPath(i) + "/item", picked, { structural: true });
            };
            return h("div", { class: "list-row", dataset: { path: rowPath(i) } },
                h("span", { class: "times" }, "×"), count,
                h("div", { class: "grow", dataset: { path: rowPath(i) + "/item" } }, refBadge("item", row.item, { onClick: choose }), h("div", { class: "field-problems" })),
                h("button", { class: "btn tiny ghost", title: "Move up", disabled: i === 0, onClick: () => { const next = [...list]; [next[i - 1], next[i]] = [next[i], next[i - 1]]; ctx.set(desc.path, next, { structural: true }); } }, "↑"),
                h("button", { class: "btn tiny ghost", title: "Remove", onClick: () => ctx.set(desc.path, list.filter((_, k) => k !== i), { structural: true }) }, "✕"),
                h("div", { class: "field-problems" }));
        }),
        h("button", { class: "btn small", onClick: add }, "+ Ingredient…"));
}

/** Drops: where the item drops on the server (mobs or patterns, chests, a mob category) with a chance and an amount. */
function dropsList(desc, ctx) {
    const list = Array.isArray(getAt(ctx.draft, desc.path)) ? getAt(ctx.draft, desc.path) : [];
    const rowPath = (i) => pointer(...pathParts(desc.path), i);
    const categories = desc.categories ?? [];
    return h("div", { class: "drops-list" },
        list.length === 0 ? h("div", { class: "muted small" }, "Does not drop from mobs or chests (only crafted or given).") : null,
        ...list.map((row, i) => {
            const base = rowPath(i);
            const mobs = Array.isArray(row.mobs) ? row.mobs : [];
            const chests = Array.isArray(row.chests) ? row.chests : [];
            const chance = h("input", { type: "text", inputmode: "decimal", value: row.chance === undefined || row.chance === "" ? "" : String(+(Number(row.chance) * 100).toFixed(4)), class: "num", "data-focus-key": base + "/chance" });
            bindTyping(chance, {
                live: () => { chance.classList.remove("invalid"); const n = parseNumber(chance.value); if (Number.isFinite(n)) ctx.set(base + "/chance", +(n / 100).toFixed(8)); },
                commit: () => {
                    const n = parseNumber(chance.value);
                    chance.classList.toggle("invalid", !Number.isFinite(n) || n < 0 || n > 100);
                    ctx.set(base + "/chance", Number.isFinite(n) ? +(n / 100).toFixed(8) : chance.value);
                },
            });
            const amount = h("input", { type: "text", inputmode: "numeric", value: String(row.amount ?? 1), class: "count", "data-focus-key": base + "/amount" });
            const amountValue = () => { const n = parseNumber(amount.value); return Number.isFinite(n) ? n : amount.value; };
            bindTyping(amount, { live: () => { if (Number.isFinite(parseNumber(amount.value))) ctx.set(base + "/amount", amountValue()); }, commit: () => ctx.set(base + "/amount", amountValue()) });
            const category = h("select", {}, h("option", { value: "" }, t("any category")), ...categories.map((c) => h("option", { value: c, selected: row.category === c }, c)));
            category.addEventListener("change", () => ctx.set(base + "/category", category.value || undefined, { structural: true }));
            const addMob = async () => {
                const picked = await pickerFor("mob", { allowPattern: true });
                if (picked) ctx.set(base + "/mobs", [...mobs, picked], { structural: true });
            };
            const addChest = async () => {
                const { promptDialog } = await import("./ui.mjs");
                const picked = await promptDialog(t("Chest name or pattern"), { placeholder: "e.g. *_BOSS_* ", hint: t("lootchests.bin names; * matches any text") });
                if (picked) ctx.set(base + "/chests", [...chests, picked], { structural: true });
            };
            return h("div", { class: "drop-card", dataset: { path: base } },
                h("div", { class: "row wrap" },
                    h("span", { class: "label" }, "Chance"), h("div", { class: "with-suffix", dataset: { path: base + "/chance" } }, chance, h("span", { class: "suffix" }, "%")),
                    h("span", { class: "label" }, "Amount"), h("div", { dataset: { path: base + "/amount" } }, amount),
                    h("span", { class: "label" }, "Mob category"), category,
                    h("span", { class: "grow" }),
                    h("button", { class: "btn tiny ghost", title: "Remove this drop", onClick: () => ctx.set(desc.path, list.filter((_, k) => k !== i), { structural: true }) }, "✕")),
                h("div", { class: "row wrap" }, h("span", { class: "label" }, "Mobs"),
                    ...mobs.map((m, k) => h("span", { class: "ref-item", dataset: { path: `${base}/mobs/${k}` } }, refBadge("mob", m),
                        h("button", { class: "btn tiny ghost", onClick: () => ctx.set(base + "/mobs", mobs.filter((_, j) => j !== k), { structural: true }) }, "✕"))),
                    h("button", { class: "btn small", onClick: addMob }, "+ Mob…")),
                h("div", { class: "row wrap" }, h("span", { class: "label" }, "Chests"),
                    ...chests.map((m, k) => h("span", { class: "ref-item" }, plain("span", { class: "ref-badge ok" }, m),
                        h("button", { class: "btn tiny ghost", onClick: () => ctx.set(base + "/chests", chests.filter((_, j) => j !== k), { structural: true }) }, "✕"))),
                    h("button", { class: "btn small", onClick: addChest }, "+ Chest…")),
                h("div", { class: "hint" }, "Per kill (or a chest's first roll) × Rates:Loot. Mobs and a category together: both must match."),
                h("div", { class: "field-problems" }));
        }),
        h("button", { class: "btn small", onClick: () => ctx.set(desc.path, [...list, { mobs: [], chests: [], category: "", chance: 0.01, amount: 1 }], { structural: true }) }, "+ Drop"));
}

/**
 * Shows diagnostics next to their fields: each goes to the nearest [data-path] that equals its path or is its parent;
 * those without a field are returned (the problems panel lists them).
 */
export function applyDiagnostics(root, diagnostics) {
    for (const node of root.querySelectorAll(".field-problems")) node.replaceChildren();
    for (const node of root.querySelectorAll("[data-path].has-blocker, [data-path].has-warning")) node.classList.remove("has-blocker", "has-warning");
    const nodes = [...root.querySelectorAll("[data-path]")];
    const unplaced = [];
    for (const d of diagnostics ?? []) {
        if (d.severity === "info" && !d.path) { unplaced.push(d); continue; }
        let path = d.path ?? "";
        let target = null;
        while (path && !target) {
            target = nodes.find((n) => n.dataset.path === path);
            if (!target) path = path.slice(0, Math.max(0, path.lastIndexOf("/")));
        }
        if (!target) { unplaced.push(d); continue; }
        target.classList.add(d.severity === "blocker" ? "has-blocker" : "has-warning");
        const box = [...target.children].find((c) => c.classList?.contains("field-problems")) ?? target.querySelector(".field-problems");
        box?.append(problemLine(d, { inline: true }));
    }
    return unplaced;
}

/** A titled, collapsible group of fields; with state (the editor's viewState) its open/closed survives re-renders. */
export function section(title, children, { open = true, extra = null, id, state } = {}) {
    const key = `section:${title}`;
    const isOpen = state && state[key] !== undefined ? state[key] : open;
    const details = h("details", { class: "section", open: isOpen, id }, h("summary", {}, h("span", { class: "section-title" }, title), extra), h("div", { class: "section-body" }, children));
    if (state) details.addEventListener("toggle", () => { state[key] = details.open; });
    return details;
}
