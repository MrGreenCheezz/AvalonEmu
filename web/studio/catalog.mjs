// The catalogue browser (centre pane): items, spells, mobs, loot lists, stations and icons with search, filters and paging;
// and the detail card of any entry, with "Make a copy" (a new draft pre-filled from it) for items and spells.

import { api } from "./api.mjs";
import { t, nameOf } from "./i18n.mjs";
import { KINDS, createGuard, pickIcon, refBadge, resultRow } from "./pickers.mjs";
import { debounce, h, icon, markup, modal, mount, plain, toast } from "./ui.mjs";

const TABS = [["item", "Items"], ["spell", "Spells"], ["mob", "Mobs"], ["lootlist", "Loot lists"], ["station", "Stations"]];
const state = { kind: "item", query: "", filters: {}, rows: [], total: 0, facets: {} };

/** Hooks set by the app: onCopy(kind, name) makes a draft from an entry. */
export const catalogHooks = { onCopy: null };

export function catalogView() {
    const guard = createGuard();
    const list = h("div", { class: "result-list grid-list", "data-scroll-key": "catalog" });
    const counter = h("div", { class: "muted small" });
    const more = h("button", { class: "btn small", onClick: () => load(false) }, "Load more");
    const search = h("input", { type: "search", placeholder: "Search the official data and the packs: name, English or Russian text", value: state.query, "data-focus-key": "catalog-search", spellcheck: "false" });
    const filterBar = h("div", { class: "filters" });
    const tabs = h("div", { class: "tabs" });

    const renderTabs = () => mount(tabs, ...TABS.map(([k, label]) => h("button", { class: `tab${state.kind === k ? " active" : ""}`, onClick: () => { state.kind = k; state.filters = {}; renderTabs(); renderFilters(); load(true); } }, label)),
        h("button", { class: "tab", onClick: async () => { const s = await pickIcon(); if (s) { navigator.clipboard?.writeText(s); toast(t("Sprite name copied: {1}", { 1: s }), "ok"); } } }, "Icons…"));
    const render = () => {
        mount(list, state.rows.length === 0 ? h("div", { class: "empty-note" }, "Nothing found") : null,
            ...state.rows.map((r) => resultRow(r, { onPick: () => showDetail(r.kind, r.name), letter: KINDS[r.kind]?.letter })));
        mount(counter, t("Shown {1} of {2}", { 1: state.rows.length, 2: state.total }));
        more.hidden = state.rows.length >= state.total;
    };
    const renderFilters = async () => {
        filterBar.replaceChildren();
        const kind = state.kind;
        if (!state.facets[kind]) state.facets[kind] = await api.facets(kind).catch(() => ({}));
        if (kind !== state.kind) return;
        for (const name of KINDS[kind]?.facets ?? []) {
            const values = state.facets[kind]?.[name] ?? [];
            if (!values.length) continue;
            const select = h("select", { "aria-label": name, onChange: () => { state.filters[name] = select.value; load(true); } },
                h("option", { value: "" }, t("any {1}", { 1: t(name) })),
                ...values.map((v) => h("option", { value: v.value, selected: state.filters[name] === v.value }, `${name === "tier" ? "T" : ""}${v.value} (${v.count})`)));
            filterBar.append(select);
        }
        if (kind === "spell") {
            const box = h("input", { type: "checkbox", checked: state.filters.offered === "1", onChange: () => { state.filters.offered = box.checked ? "1" : ""; load(true); } });
            filterBar.append(h("label", { class: "check" }, box, " ", t("offered by an item (abilities)")));
        }
    };
    const load = async (fresh) => {
        const req = guard.begin({});
        state.query = search.value.trim();
        try {
            const result = await api.search(state.kind, state.query, state.filters, fresh ? 0 : state.rows.length, 60);
            if (!req.isCurrent()) return;
            state.total = result.total;
            state.rows = fresh ? result.rows : state.rows.concat(result.rows);
            render();
        } catch (e) {
            if (req.isCurrent()) mount(list, h("div", { class: "empty-note bad" }, e.message));
        }
    };
    const reload = debounce(() => load(true), 220);
    search.addEventListener("input", () => { guard.invalidate(); reload(); });
    renderTabs();
    renderFilters();
    if (state.rows.length) render(); else load(true);
    return h("div", { class: "catalog" },
        h("div", { class: "catalog-head" }, h("h2", {}, "Catalogue"), h("p", { class: "muted" }, "Open any entry to see it whole; \"Make a copy\" turns it into a draft of the open pack.")),
        tabs, h("div", { class: "row" }, search), filterBar, list, h("div", { class: "row between" }, counter, more));
}

const section = (title, ...children) => h("div", { class: "card-section" }, h("h4", {}, title), ...children);
const badges = (kind, names) => h("div", { class: "badges" }, ...names.map((n) => refBadge(kind, n, { onClick: () => showDetail(kind, n) })));

function attrTable(attrs, keys) {
    const rows = (keys ? attrs.filter((a) => keys.includes(a.name)) : attrs).filter((a) => a.type !== "asset" || !keys);
    return h("table", { class: "attrs" }, ...rows.map((a) => h("tr", {}, plain("th", {}, a.name), plain("td", {}, a.value))));
}

/** The detail card of a catalogue entry (a modal); items and spells offer "Make a copy". */
export async function showDetail(kind, name) {
    let d;
    try {
        d = await api.detail(kind, name);
    } catch (e) {
        toast(e.message, "bad");
        return;
    }
    const canCopy = (kind === "item" || kind === "spell") && catalogHooks.onCopy;
    await modal({
        title: h("span", { class: "row" }, icon(d.icon, 32, KINDS[kind]?.letter), plain("span", {}, nameOf(d) ?? d.name), plain("span", { class: "mono muted small" }, d.name)),
        width: "900px",
        body: (close) => {
            const parts = [];
            const desc = kind === "spell" ? nameOf(d.description) : kind === "item" ? (nameOf({ en: d.descEn, ru: d.descRu })) : null;
            parts.push(h("div", { class: "row wrap" },
                d.pack ? h("span", { class: "chip pack" }, t("pack {1}", { 1: d.pack })) : h("span", { class: "chip" }, "official"),
                d.tier ? h("span", { class: "chip" }, `T${d.tier}`) : null, d.type ? plain("span", { class: "chip" }, d.type) : null,
                d.slot ? plain("span", { class: "chip" }, d.slot) : null, d.category ? plain("span", { class: "chip" }, d.category) : null,
                d.en && d.ru ? plain("span", { class: "muted" }, `EN: ${d.en} · RU: ${d.ru}`) : null));
            if (desc) parts.push(h("div", { class: "tt-desc" }, markup(desc)));
            if (kind === "item") {
                parts.push(section(t("Main stats"), attrTable(d.attributes, ["tier", "itempower", "attackdamage", "attackspeed", "attackrange", "hitpointsmax", "physicalarmor", "magicresistance", "abilitypower", "weight", "durability", "consumespell", "maxstacksize"])));
                if (d.spells?.length) parts.push(section(t("Spells"), h("div", { class: "badges" }, ...d.spells.map((s) => h("span", { class: "row" }, h("span", { class: "chip" }, s.slot ? t("slot {1}", { 1: s.slot }) : t("passive")), refBadge("spell", s.name, { onClick: () => showDetail("spell", s.name) }))))));
                for (const r of d.recipes ?? []) parts.push(section(t("Recipe"), h("div", { class: "muted small" }, t("silver {1}, time {2}, focus {3}", { 1: r.silver ?? 0, 2: r.time ?? "?", 3: r.focus ?? 0 })),
                    h("div", { class: "badges" }, ...r.resources.map((x) => h("span", { class: "row" }, plain("b", {}, `${x.count}×`), refBadge("item", x.name, { onClick: () => showDetail("item", x.name) }))))));
                if (d.stations?.length) parts.push(section(t("Crafted at"), badges("station", d.stations.map((s) => s.name))));
                if (d.drops?.sources?.length || d.drops?.packs?.length) parts.push(section(t("Drops from"), plain("div", { class: "small" }, d.drops.sources.join(", ")), ...d.drops.packs.map((p) => plain("div", { class: "small" }, `${p.pack}: ${p.text}`))));
                if (d.usedIn?.recipes?.length) parts.push(section(t("Ingredient of"), badges("item", d.usedIn.recipes)));
                if (d.enchantments) parts.push(h("div", { class: "muted small" }, t("{1} enchantment levels (a copy has the base level only)", { 1: d.enchantments })));
            }
            if (kind === "spell") {
                parts.push(section(t("Numbers"), attrTable(d.attributes, ["recastdelay", "energyusage", "castingtime", "castrange", "target", "category", "maxcharges", "uisprite"])));
                if (d.offeredBy?.length) parts.push(section(t("Offered by"), badges("item", d.offeredBy.map((x) => x.name))));
                if (d.uses?.length) parts.push(section(t("Uses the spells"), badges("spell", d.uses.map((x) => x.name))));
                parts.push(h("div", { class: "muted small" }, t("{1} effect elements", { 1: d.blocks?.length ?? 0 })));
            }
            if (kind === "mob") {
                parts.push(section(t("Attributes"), attrTable(d.attributes)));
                if (d.loot?.length) parts.push(section(t("Loot lists"), badges("lootlist", d.loot)));
            }
            if (kind === "lootlist") {
                if (d.items?.length) parts.push(section(t("Items"), h("div", { class: "badges" }, ...d.items.map((x) => h("span", { class: "row" }, refBadge("item", x.name, { onClick: () => showDetail("item", x.name) }),
                    plain("span", { class: "muted small" }, x.attributes.filter((a) => a.name !== "type").map((a) => `${a.name}=${a.value}`).join(" ")))))));
                if (d.lists?.length) parts.push(section(t("Contains the lists"), badges("lootlist", d.lists)));
                if (d.parents?.length) parts.push(section(t("Part of the lists"), badges("lootlist", d.parents)));
                if (d.users?.length) parts.push(section(t("Used by"), plain("div", { class: "small" }, d.users.join(", "))));
            }
            if (kind === "station") {
                parts.push(section(t("Crafts ({1})", { 1: d.craftsTotal }), badges("item", d.crafts.map((x) => x.name))));
            }
            if (d.usedIn?.drafts?.length) parts.push(section(t("Used by drafts"), ...d.usedIn.drafts.map((u) => plain("div", { class: "small mono" }, `${u.pack}: ${u.kind}/${u.id} ${u.path}`))));
            if (d.attributes && kind !== "mob") parts.push(h("details", {}, h("summary", {}, t("All attributes ({1})", { 1: d.attributes.length })), attrTable(d.attributes)));
            if (d.xml) parts.push(h("details", {}, h("summary", {}, "XML"), plain("pre", { class: "xml" }, d.xml)));
            parts.push(h("div", { class: "row end sticky-actions" },
                h("button", { class: "btn", onClick: () => { navigator.clipboard?.writeText(d.name); toast(t("Copied: {1}", { 1: d.name }), "ok"); } }, "Copy the name"),
                canCopy ? h("button", { class: "btn primary", onClick: () => { close(); catalogHooks.onCopy(kind, d.name); } }, "Make a copy…") : null));
            return h("div", { class: "detail" }, ...parts);
        },
    });
}
