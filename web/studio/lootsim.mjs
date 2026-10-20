// The loot simulator (phase 3): N rolls of a pack loot list (or N kills of a mob variant: its lists with their chances plus the drop
// rules that name it) → the expected amount of each item (exact, from the chances and weights) and the simulated counts of this run.
// Lists are the server's semantics (Mods/ModDrops.RollList): unweighted entries roll their chance, weighted ones share `rolls` picks.

import { t, nameOf } from "./i18n.mjs";
import { h, plain } from "./ui.mjs";
import { refBadge } from "./pickers.mjs";

/** Hooks the app sets: loadList(id) → the pack's lootlist draft (or null for an official list). */
export const lootHooks = { loadList: null };

const avg = (e) => ((Number(e.min ?? 1) + Number(e.max ?? e.min ?? 1)) / 2);

async function resolveLists(entries, cache, depth = 0) {
    if (depth > 4) return;
    for (const e of entries) {
        if (!e.list || cache.has(e.list)) continue;
        cache.set(e.list, null);
        const draft = lootHooks.loadList ? await lootHooks.loadList(e.list).catch(() => null) : null;
        cache.set(e.list, draft);
        if (draft) await resolveLists(draft.entries ?? [], cache, depth + 1);
    }
}

/** Expected amounts per item of one roll of a list (scaled by `factor`), into `out`. */
function expect(list, factor, cache, out, depth = 0) {
    const entries = list.entries ?? [];
    const weighted = entries.filter((e) => Number(e.weight) > 0);
    const total = weighted.reduce((s, e) => s + Number(e.weight), 0);
    const give = (e, p) => {
        if (e.item) out.set(e.item, (out.get(e.item) ?? 0) + p * avg(e));
        else if (e.list) {
            const sub = cache.get(e.list);
            if (sub && depth < 4) expect(sub, p, cache, out, depth + 1);
            else out.set(`list:${e.list}`, (out.get(`list:${e.list}`) ?? 0) + p);
        }
    };
    for (const e of entries.filter((e) => !(Number(e.weight) > 0))) give(e, factor * Math.min(1, Number(e.chance ?? 1)));
    for (const e of weighted) give(e, factor * Number(list.rolls ?? 1) * Number(e.weight) / total);
}

/** One random roll of a list into `out`. */
function roll(list, cache, out, depth = 0) {
    const give = (e) => {
        if (e.item) {
            const min = Number(e.min ?? 1);
            const max = Number(e.max ?? min);
            out.set(e.item, (out.get(e.item) ?? 0) + min + Math.floor(Math.random() * (max - min + 1)));
        } else if (e.list) {
            const sub = cache.get(e.list);
            if (sub && depth < 4) roll(sub, cache, out, depth + 1);
            else out.set(`list:${e.list}`, (out.get(`list:${e.list}`) ?? 0) + 1);
        }
    };
    const entries = list.entries ?? [];
    for (const e of entries.filter((e) => !(Number(e.weight) > 0))) if (Math.random() < Number(e.chance ?? 1)) give(e);
    const weighted = entries.filter((e) => Number(e.weight) > 0);
    const total = weighted.reduce((s, e) => s + Number(e.weight), 0);
    for (let r = 0; r < Number(list.rolls ?? 1) && total > 0; r++) {
        let pick = Math.random() * total;
        for (const e of weighted) if ((pick -= Number(e.weight)) < 0) { give(e); break; }
    }
}

/**
 * The simulator panel. source: {kind: "lootlist", list} or {kind: "mob", lists: [{list, chance}], rules: [{item, chance, amount}]}.
 */
export function lootSimulator(source) {
    const box = h("div", { class: "loot-sim" });
    const n = h("input", { type: "text", inputmode: "numeric", value: "1000", class: "count", "aria-label": "N" });
    const result = h("div", {});
    const run = async () => {
        const count = Math.max(1, Math.min(100000, Math.floor(Number(n.value) || 1000)));
        const cache = new Map();
        const lists = source.kind === "lootlist" ? [{ list: source.list, chance: 1 }] : (source.lists ?? []).map((l) => ({ list: null, id: l.list, chance: Number(l.chance ?? 1) }));
        for (const l of lists) if (l.id) { await resolveLists([{ list: l.id }], cache); l.list = cache.get(l.id); }
        if (source.kind === "lootlist") await resolveLists(source.list.entries ?? [], cache);
        const expected = new Map();
        const counted = new Map();
        for (const l of lists) {
            if (l.list) expect(l.list, l.chance, cache, expected);
            else if (l.id) expected.set(`list:${l.id}`, (expected.get(`list:${l.id}`) ?? 0) + l.chance);
        }
        for (const r of source.rules ?? []) expected.set(r.item, (expected.get(r.item) ?? 0) + Number(r.chance) * Number(r.amount ?? 1));
        for (let i = 0; i < count; i++) {
            for (const l of lists) if (l.list && Math.random() < l.chance) roll(l.list, cache, counted);
            for (const r of source.rules ?? []) if (Math.random() < Number(r.chance)) counted.set(r.item, (counted.get(r.item) ?? 0) + Number(r.amount ?? 1));
        }
        const keys = [...new Set([...expected.keys(), ...counted.keys()])].sort((a, b) => (expected.get(b) ?? 0) - (expected.get(a) ?? 0));
        result.replaceChildren(keys.length === 0 ? h("div", { class: "muted small" }, "Nothing drops.") : h("table", { class: "sim-table" },
            h("tr", {}, h("th", {}, "Item"), h("th", {}, t("per roll")), h("th", {}, t("expected in {1}", { 1: count })), h("th", {}, t("this run"))),
            ...keys.map((k) => h("tr", {},
                h("td", {}, k.startsWith("list:") ? plain("span", { class: "mono small" }, `${t("official list")} ${k.slice(5)}`) : refBadge("item", k)),
                plain("td", { class: "mono" }, (+(expected.get(k) ?? 0).toFixed(4)).toString()),
                plain("td", { class: "mono" }, (+((expected.get(k) ?? 0) * count).toFixed(1)).toString()),
                plain("td", { class: "mono" }, String(counted.get(k) ?? 0))))));
    };
    box.append(h("div", { class: "row" }, h("b", {}, source.kind === "mob" ? t("Loot simulator: kills") : t("Loot simulator: rolls")), n,
        h("button", { class: "btn small", onClick: run }, "Simulate")), result,
    h("div", { class: "hint" }, source.kind === "mob" ? "Per kill × Rates:Loot 1; drop rules of this pack's items that name it are included." : "The server's roll: unweighted entries by chance, weighted ones share the picks."));
    run();
    return box;
}

export { nameOf };
