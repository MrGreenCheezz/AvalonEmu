// Pickers: a search dialog per reference kind (items, spells, mobs, stations, loot lists, icons) with icon tiles, filters,
// paging ("load more"), keyboard navigation and a stale-response guard; and refBadge(), the chosen reference shown with its
// icon and name (looked up in batches).

import { api } from "./api.mjs";
import { t, lang, nameOf } from "./i18n.mjs";
import { debounce, h, icon, modal, mount, plain } from "./ui.mjs";

export const KINDS = {
    item: { title: "Choose an item", facets: ["tier", "type", "slot", "category"], letter: "I" },
    spell: { title: "Choose a spell", facets: ["type", "category"], letter: "S", offered: true },
    mob: { title: "Choose a mob", facets: ["tier", "category"], letter: "M" },
    station: { title: "Choose a crafting station", facets: ["tier"], letter: "B" },
    lootlist: { title: "Choose a loot list", facets: [], letter: "L" },
    model: { title: "Choose a model", facets: [], letter: "◆" },
};

/** The drafts of the open pack, by kind: pickers list them first ("this pack"). Set by the app. */
let localRows = {};
export function setLocalRows(byKind) {
    localRows = byKind ?? {};
}

/** Hooks the app sets: showDetail(kind, name) opens a catalogue card. */
export const pickerHooks = { showDetail: null };

/** Guards an async picker against answers of an older search. */
export function createGuard() {
    let seq = 0;
    let active = null;
    return {
        begin(context) {
            const req = { token: ++seq, context, isCurrent: () => active === req };
            active = req;
            return req;
        },
        invalidate() { active = null; seq++; },
    };
}

const facetCache = new Map();
function facets(kind) {
    if (!facetCache.has(kind)) facetCache.set(kind, api.facets(kind).catch(() => ({})));
    return facetCache.get(kind);
}

/** The text of a facet chip ("T6", "weapon"). */
function chips(row) {
    const f = row.facets ?? {};
    const list = [];
    if (f.tier && f.tier !== "0") list.push(`T${f.tier}`);
    for (const key of ["type", "slot", "subcategory", "category"]) if (f[key]) list.push(f[key]);
    return list.slice(0, 3);
}

function statText(row) {
    const s = row.stats ?? {};
    const parts = [];
    if (s.ip) parts.push(`IP ${s.ip}`);
    if (s.damage) parts.push(`${t("dmg")} ${s.damage}`);
    if (s.cooldown) parts.push(`${t("cd")} ${s.cooldown}${t("s")}`);
    if (s.energy) parts.push(`${t("en.")} ${s.energy}`);
    if (s.hp) parts.push(`HP ${s.hp}`);
    if (s.items !== undefined && row.kind === "lootlist") parts.push(`${s.items} ${t("items")}`);
    if (s.crafts) parts.push(`${s.crafts} ${t("recipes")}`);
    if (s.users) parts.push(`${s.users} ${t("users")}`);
    return parts.join(" · ");
}

/** One result row: icon, localized name, unique name, chips, stats. */
export function resultRow(row, { onPick, active = false, letter = "?" } = {}) {
    const node = h("div", { class: `result-row${active ? " active" : ""}`, role: "option", tabindex: "-1", onClick: () => onPick?.(row) },
        icon(row.icon, 40, letter),
        h("div", { class: "grow" },
            h("div", { class: "row-title" }, plain("span", { class: "name" }, nameOf(row) ?? row.name), row.pack ? h("span", { class: "chip pack" }, row.pack) : null,
                row.local ? h("span", { class: "chip own" }, "this pack") : null),
            h("div", { class: "row-sub" }, plain("span", { class: "mono" }, row.name), ...chips(row).map((c) => plain("span", { class: "chip" }, c)),
                plain("span", { class: "muted" }, statText(row)))),
        pickerHooks.showDetail && !row.local ? h("button", { class: "btn small ghost", title: "Details", onClick: (e) => { e.stopPropagation(); pickerHooks.showDetail(row.kind, row.name); } }, "ⓘ") : null);
    return node;
}

/**
 * The picker of a kind: resolves with the chosen unique name, or undefined.
 * @param {string} kind item|spell|mob|station|lootlist
 * @param {{current?: string, filters?: object, title?: string, allowPattern?: boolean}} options
 */
export function pickRef(kind, { current = "", filters: preset = {}, title, allowPattern = false } = {}) {
    const def = KINDS[kind] ?? { title: "Choose", facets: [], letter: "?" };
    return modal({
        title: title ?? def.title,
        width: "940px",
        className: "picker",
        body: (close) => {
            const filters = { ...preset };
            if (def.offered && filters.offered === undefined) filters.offered = "1";
            const guard = createGuard();
            let rows = [];
            let total = 0;
            let activeIndex = 0;
            const list = h("div", { class: "result-list", role: "listbox", "data-scroll-key": "picker" });
            const counter = h("div", { class: "muted small" });
            const more = h("button", { class: "btn small", onClick: () => load(false) }, "Load more");
            const search = h("input", { type: "search", placeholder: "Search: name, English or Russian text (several words)", value: "", autofocus: true, spellcheck: "false" });
            const filterBar = h("div", { class: "filters" });

            const pick = (row) => close(row.name);
            const highlight = (index) => {
                const nodes = list.querySelectorAll(".result-row");
                if (!nodes.length) return;
                activeIndex = Math.max(0, Math.min(nodes.length - 1, index));
                nodes.forEach((n, i) => n.classList.toggle("active", i === activeIndex));
                nodes[activeIndex].scrollIntoView({ block: "nearest" });
            };
            const local = () => {
                const needle = search.value.trim().toLowerCase();
                return (localRows[kind] ?? []).filter((r) => !needle || `${r.name} ${r.en ?? ""} ${r.ru ?? ""}`.toLowerCase().includes(needle)).map((r) => ({ ...r, local: true }));
            };
            const render = () => {
                const own = local();
                const all = [...own, ...rows];
                mount(list, all.length === 0 ? h("div", { class: "empty-note" }, "Nothing found") : null,
                    ...all.map((r, i) => resultRow(r, { onPick: pick, active: i === activeIndex, letter: def.letter })));
                mount(counter, t("Shown {1} of {2}", { 1: rows.length, 2: total }), own.length ? ` + ${own.length} ${t("of this pack")}` : "");
                more.hidden = rows.length >= total;
            };
            const load = async (fresh) => {
                const query = search.value.trim();
                const req = guard.begin({ query, fresh });
                if (fresh) { rows = []; activeIndex = 0; mount(list, h("div", { class: "empty-note" }, "Searching…")); }
                try {
                    const result = await api.search(kind, query, filters, fresh ? 0 : rows.length, 40);
                    if (!req.isCurrent()) return;
                    total = result.total;
                    rows = fresh ? result.rows : rows.concat(result.rows);
                    render();
                } catch (e) {
                    if (req.isCurrent()) mount(list, h("div", { class: "empty-note bad" }, e.message));
                }
            };
            const reload = debounce(() => load(true), 200);
            search.addEventListener("input", () => { guard.invalidate(); reload(); });
            search.addEventListener("keydown", (e) => {
                if (e.key === "ArrowDown") { e.preventDefault(); highlight(activeIndex + 1); }
                else if (e.key === "ArrowUp") { e.preventDefault(); highlight(activeIndex - 1); }
                else if (e.key === "PageDown") { e.preventDefault(); highlight(activeIndex + 8); }
                else if (e.key === "PageUp") { e.preventDefault(); highlight(activeIndex - 8); }
                else if (e.key === "Enter") {
                    e.preventDefault();
                    const all = [...local(), ...rows];
                    if (all[activeIndex]) pick(all[activeIndex]);
                    else if (allowPattern && search.value.trim()) close(search.value.trim());
                }
            });
            facets(kind).then((f) => {
                for (const name of def.facets) {
                    const values = f?.[name] ?? [];
                    if (!values.length) continue;
                    const select = h("select", { "aria-label": name, onChange: () => { filters[name] = select.value; load(true); } },
                        h("option", { value: "" }, t("any {1}", { 1: t(name) })),
                        ...values.map((v) => h("option", { value: v.value, selected: filters[name] === v.value }, `${name === "tier" ? "T" : ""}${v.value} (${v.count})`)));
                    filterBar.append(select);
                }
                if (def.offered) {
                    const box = h("input", { type: "checkbox", checked: filters.offered === "1", onChange: () => { filters.offered = box.checked ? "1" : ""; load(true); } });
                    filterBar.append(h("label", { class: "check" }, box, " ", t("offered by an item (abilities)")));
                }
            });
            if (current) search.value = "";
            load(true);
            return h("div", { class: "picker-body" },
                h("div", { class: "row" }, search),
                filterBar,
                allowPattern ? h("div", { class: "hint" }, "A name pattern works too: type it (e.g. WORLD_BOSS_*) and press Enter.") : null,
                current ? h("div", { class: "hint" }, t("Now:"), " ", refBadge(kind, current)) : null,
                list,
                h("div", { class: "row between" }, counter, more));
        },
    });
}

export const pickItem = (o) => pickRef("item", o);
export const pickSpell = (o) => pickRef("spell", o);
export const pickMob = (o) => pickRef("mob", { allowPattern: true, ...o });
export const pickStation = (o) => pickRef("station", o);
export const pickLootList = (o) => pickRef("lootlist", o);

/**
 * The model picker (an item's mesh): the character models official equipment uses, each shown with the icon and name of the first
 * item that uses it; filtered by slot (the template's by default). Resolves with the model name, or undefined.
 */
export function pickModel(current = "", { slot = "" } = {}) {
    return modal({
        title: t("Choose a model"),
        width: "940px",
        className: "picker",
        body: (close) => {
            const guard = createGuard();
            let rows = [];
            let total = 0;
            let filled = false;
            const list = h("div", { class: "result-list", role: "listbox", "data-scroll-key": "models" });
            const counter = h("div", { class: "muted small" });
            const more = h("button", { class: "btn small", onClick: () => load(false) }, "Load more");
            const search = h("input", { type: "search", placeholder: t("Model or item name, e.g. CAPE, plate, плащ"), autofocus: true, spellcheck: "false" });
            const slotSelect = h("select", { "aria-label": "slot", onChange: () => load(true) }, h("option", { value: "" }, t("any slot")));
            if (slot) slotSelect.append(h("option", { value: slot, selected: true }, slot));
            const row = (r) => resultRow({ ...r, facets: { slot: r.slot }, stats: { users: r.users } }, { onPick: () => close(r.name), active: r.name === current, letter: "◆" });
            const load = async (fresh) => {
                const req = guard.begin({ q: search.value });
                try {
                    const result = await api.models(search.value.trim(), slotSelect.value, fresh ? 0 : rows.length, 60);
                    if (!req.isCurrent()) return;
                    if (!filled) {
                        filled = true;
                        for (const s of result.slots ?? []) if (s !== slot) slotSelect.append(h("option", { value: s }, s));
                    }
                    total = result.total;
                    rows = fresh ? result.rows : rows.concat(result.rows);
                    mount(list, rows.length ? null : h("div", { class: "empty-note" }, "Nothing found"), ...rows.map(row));
                    mount(counter, t("Shown {1} of {2}", { 1: rows.length, 2: total }));
                    more.hidden = rows.length >= total;
                } catch (e) {
                    if (req.isCurrent()) mount(list, h("div", { class: "empty-note bad" }, e.message));
                }
            };
            const reload = debounce(() => load(true), 250);
            search.addEventListener("input", () => { guard.invalidate(); reload(); });
            load(true);
            return h("div", { class: "picker-body" }, h("div", { class: "row" }, search, slotSelect),
                h("div", { class: "hint" }, t("Only models of official items: the client has no others. The icon is that of the first item using the model.")),
                current ? h("div", { class: "hint" }, t("Now:"), " ", refBadge("model", current)) : null,
                list, h("div", { class: "row between" }, counter, more));
        },
    });
}

/** The icon picker: tiles of the extracted icon cache, by atlas; resolves with the sprite name (forItem: only item icons). */
export function pickIcon(current = "", { forItem = false } = {}) {
    return modal({
        title: "Choose an icon",
        width: "940px",
        className: "picker",
        body: (close) => {
            const guard = createGuard();
            let rows = [];
            let total = 0;
            let atlas = "";
            const grid = h("div", { class: "icon-grid", "data-scroll-key": "icons" });
            const counter = h("div", { class: "muted small" });
            const more = h("button", { class: "btn small", onClick: () => load(false) }, "Load more");
            const search = h("input", { type: "search", placeholder: "Sprite name, e.g. SWORD, POTION, FIRE", autofocus: true, spellcheck: "false" });
            const atlasSelect = h("select", { onChange: () => { atlas = atlasSelect.value; load(true); } }, h("option", { value: "" }, "all atlases"));
            const tile = (r) => h("button", { class: `icon-tile${r.sprite === current ? " active" : ""}`, title: r.sprite, onClick: () => close(r.sprite) },
                icon(r.icon, 56, "?"), plain("span", { class: "icon-name" }, r.sprite));
            const load = async (fresh) => {
                const req = guard.begin({ q: search.value });
                try {
                    const result = await api.icons(search.value.trim(), atlas, fresh ? 0 : rows.length, 160, forItem);
                    if (!req.isCurrent()) return;
                    if (fresh && atlasSelect.options.length === 1) for (const a of result.atlases) atlasSelect.append(h("option", { value: a }, a));
                    total = result.total;
                    if (fresh) { rows = result.rows; mount(grid, ...rows.map(tile)); }
                    else { rows = rows.concat(result.rows); grid.append(...result.rows.map(tile)); }
                    if (!result.available) mount(grid, h("div", { class: "empty-note" }, "No icon cache yet: run the modkit's icons command once (dotnet run --project tools/AlbionLocal.ModKit -- icons)."));
                    mount(counter, t("Shown {1} of {2}", { 1: rows.length, 2: total }));
                    more.hidden = rows.length >= total;
                } catch (e) {
                    if (req.isCurrent()) mount(grid, h("div", { class: "empty-note bad" }, e.message));
                }
            };
            const reload = debounce(() => load(true), 200);
            search.addEventListener("input", () => { guard.invalidate(); reload(); });
            load(true);
            return h("div", { class: "picker-body" }, h("div", { class: "row" }, search, atlasSelect), grid, h("div", { class: "row between" }, counter, more));
        },
    });
}

/** Opens the picker of a reference kind. */
export function pickerFor(kind, options) {
    if (kind === "icon") return pickIcon(options?.current, options);
    if (kind === "model") return pickModel(options?.current, options);
    return pickRef(kind, options);
}

// ---- refBadge: "⚔ Broadsword · T6_MAIN_SWORD", looked up in batches and cached for the session

const cache = new Map();
let queue = new Map();
let timer = null;

function flush() {
    const batch = queue;
    queue = new Map();
    timer = null;
    const keys = [...batch.keys()];
    for (let i = 0; i < keys.length; i += 150) {
        const slice = keys.slice(i, i + 150);
        api.lookup(slice).then((result) => {
            for (const key of slice) {
                const value = result[key] ?? { found: false };
                cache.set(key, value);
                for (const resolve of batch.get(key)) resolve(value);
            }
        }).catch(() => {
            for (const key of slice) for (const resolve of batch.get(key)) resolve(null);
        });
    }
}

/** The lookup of kind:name (found, en, ru, icon), batched with the others asked in the same moment. */
export function lookup(kind, name) {
    const key = `${kind}:${name}`;
    if (cache.has(key)) return Promise.resolve(cache.get(key));
    return new Promise((resolve) => {
        if (!queue.has(key)) queue.set(key, []);
        queue.get(key).push(resolve);
        timer ??= setTimeout(flush, 30);
    });
}

/** Forget the lookups (after a Generate the packs' own names resolve differently). */
export function clearLookups() {
    cache.clear();
}

/** A reference shown with its icon and name; red when it does not exist. */
export function refBadge(kind, name, { empty = "not chosen", onClick } = {}) {
    if (!name) return h("span", { class: "ref-badge none" }, empty);
    const own = (localRows[kind] ?? []).find((r) => r.name === name);
    const node = h("span", { class: `ref-badge${onClick ? " clickable" : ""}`, title: name, onClick });
    const img = h("span", { class: "ref-icon" });
    const label = plain("span", { class: "ref-name" }, name);
    node.append(img, label);
    if (own) {
        node.classList.add("ok", "own");
        mount(img, icon(own.icon, 20, "★"));
        label.textContent = `${nameOf(own) ?? name} · ${name}`;
        return node;
    }
    lookup(kind, name).then((r) => {
        if (!r) return;
        if (r.found) {
            node.classList.add("ok");
            if (r.pattern) label.textContent = `${name} (${t("pattern")})`;
            else label.textContent = (nameOf(r) ? `${nameOf(r)} · ` : "") + name;
            mount(img, icon(r.icon, 20, KINDS[kind]?.letter ?? ""));
        } else {
            node.classList.add("bad");
            label.textContent = `${name} — ${t("not found")}`;
            node.title = t("No such entry in the game data or the packs");
        }
    });
    return node;
}

export { lang };
