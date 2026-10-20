// The editors of the server-side kinds (phase 3; the modkit cannot patch mobs.bin or loot.bin): a mob variant (an official mob
// with factors, loot lists and a chance to replace the template in the world → server/mobs.json) and a server loot list
// (entries by chance or weight → server/loot.json). Both need the pack in Mods:Packs; .mob variant <id> spawns a variant.

import { t } from "../i18n.mjs";
import { renderField, section } from "../forms.mjs";
import { pickerFor, refBadge } from "../pickers.mjs";
import { h, plain, pointer } from "../ui.mjs";
import { lootSimulator } from "../lootsim.mjs";
import { registerEditor } from "./registry.mjs";

function identity(ctx, kind) {
    const d = ctx.draft;
    return section(t("Identity"), h("div", { class: "grid" },
        h("div", { class: "field", dataset: { path: "/id" } },
            h("div", { class: "field-head" }, h("label", { class: "label" }, "Unique name")),
            h("div", { class: "row" }, plain("span", { class: "mono big" }, d.id), h("button", { class: "btn small", onClick: () => ctx.rename?.() }, "Rename…")),
            h("div", { class: "hint" }, kind === "mob" ? "The server's name of the variant: drop rules and .mob variant use it." : "Mob variants and other lists refer to it by this name."),
            h("div", { class: "field-problems" })),
        kind === "mob" ? renderField({ path: "/template", type: "mob", label: t("Made from (mob)"), required: true,
            hint: t("the client shows this mob's model, name and spells") }, ctx) : null,
        renderField({ path: "/name", type: "loc", label: t("Name"), hint: kind === "mob" ? t("server-side only (logs, announcements); the client shows the template's name") : "" }, ctx)));
}

function rowButtons(ctx, path, list, i) {
    return [
        h("button", { class: "btn tiny ghost", title: "Move up", disabled: i === 0, onClick: () => { const next = [...list]; [next[i - 1], next[i]] = [next[i], next[i - 1]]; ctx.set(path, next, { structural: true }); } }, "↑"),
        h("button", { class: "btn tiny ghost", title: "Remove", onClick: () => ctx.set(path, list.filter((_, k) => k !== i), { structural: true }) }, "✕"),
    ];
}

function num(ctx, path, value, { percent = false, cls = "num" } = {}) {
    const input = h("input", { type: "text", inputmode: "decimal", class: cls, value: value === undefined || value === null ? "" : String(percent ? +(Number(value) * 100).toFixed(4) : value), "data-focus-key": path });
    input.addEventListener("input", () => {
        const n = Number(input.value.replace(",", "."));
        if (input.value.trim() === "") return ctx.set(path, undefined);
        ctx.set(path, Number.isFinite(n) ? (percent ? +(n / 100).toFixed(8) : n) : input.value);
    });
    return h("span", { class: "with-suffix", dataset: { path } }, input, percent ? h("span", { class: "suffix" }, "%") : null);
}

registerEditor("mob", {
    label: "Mob variant",
    letter: "M",
    render(ctx) {
        const d = ctx.draft;
        const loot = Array.isArray(d.loot) ? d.loot : [];
        const lootRows = loot.map((row, i) => {
            const base = pointer("loot", i);
            const choose = async () => {
                const picked = await pickerFor("lootlist", { current: row.list });
                if (picked) ctx.set(base + "/list", picked, { structural: true });
            };
            return h("div", { class: "list-row", dataset: { path: base } },
                h("div", { class: "grow", dataset: { path: base + "/list" } }, refBadge("lootlist", row.list, { onClick: choose }), h("div", { class: "field-problems" })),
                h("span", { class: "label" }, "chance"), num(ctx, base + "/chance", row.chance, { percent: true }),
                ...rowButtons(ctx, "/loot", loot, i), h("div", { class: "field-problems" }));
        });
        return h("div", { class: "editor-sections" },
            identity(ctx, "mob"),
            section(t("Strength"), h("div", { class: "grid" },
                renderField({ path: "/healthFactor", type: "number", label: t("Health ×"), min: 0.05, max: 200, hint: t("× the template's max health") }, ctx),
                renderField({ path: "/damageFactor", type: "number", label: t("Damage ×"), min: 0.05, max: 50, hint: t("× the damage it deals") }, ctx),
                renderField({ path: "/fameFactor", type: "number", label: t("Fame ×"), min: 0, max: 100 }, ctx),
                renderField({ path: "/replaceChance", type: "percent", label: t("Replaces the template in the world"), min: 0, max: 1,
                    hint: t("chance per world spawn of the template (0: only .mob variant)") }, ctx)), { state: ctx.viewState }),
            section(t("Loot lists"), h("div", { class: "item-list", dataset: { path: "/loot" } },
                loot.length === 0 ? h("div", { class: "muted small" }, "Only the template's loot (and drop rules that name it).") : null,
                ...lootRows,
                h("button", { class: "btn small", onClick: async () => { const p = await pickerFor("lootlist", {}); if (p) ctx.set("/loot", [...loot, { list: p, chance: 1 }], { structural: true }); } }, "+ Loot list…"),
                h("div", { class: "hint" }, "Each list rolls with its chance on a kill (× Rates:Loot), after the template's own loot. Lists: this pack's or loot.xml's."),
                h("div", { class: "field-problems" })), { state: ctx.viewState }),
            section(t("On the server"), h("div", { class: "hint" },
                t("Needs the pack in Mods:Packs (server restart). GMs: .mob variant {1} [count]. Drops of items: give the variant's name in their drop rules.", { 1: d.id }))));
    },
});

registerEditor("lootlist", {
    label: "Loot list",
    letter: "L",
    render(ctx) {
        const d = ctx.draft;
        const entries = Array.isArray(d.entries) ? d.entries : [];
        const rows = entries.map((row, i) => {
            const base = pointer("entries", i);
            const isList = Boolean(row.list) && !row.item;
            const choose = async () => {
                const picked = await pickerFor(isList ? "lootlist" : "item", { current: isList ? row.list : row.item });
                if (picked) ctx.set(base + (isList ? "/list" : "/item"), picked, { structural: true });
            };
            return h("div", { class: "list-row loot-row", dataset: { path: base } },
                h("div", { class: "grow", dataset: { path: base + (isList ? "/list" : "/item") } }, refBadge(isList ? "lootlist" : "item", isList ? row.list : row.item, { onClick: choose }), h("div", { class: "field-problems" })),
                h("span", { class: "label" }, "chance"), num(ctx, base + "/chance", row.chance ?? 1, { percent: true }),
                h("span", { class: "label" }, "weight"), num(ctx, base + "/weight", row.weight),
                isList ? null : [h("span", { class: "label" }, "min"), num(ctx, base + "/min", row.min ?? 1, { cls: "count" }), h("span", { class: "label" }, "max"), num(ctx, base + "/max", row.max ?? row.min ?? 1, { cls: "count" })],
                ...rowButtons(ctx, "/entries", entries, i), h("div", { class: "field-problems" }));
        });
        return h("div", { class: "editor-sections" },
            identity(ctx, "lootlist"),
            section(t("Entries"), h("div", {},
                renderField({ path: "/rolls", type: "int", label: t("Weighted picks per roll"), min: 0, max: 50, hint: t("entries with a weight share these picks; the others roll their own chance") }, ctx),
                h("div", { class: "item-list", dataset: { path: "/entries" } },
                    entries.length === 0 ? h("div", { class: "muted small" }, "No entries yet.") : null,
                    ...rows,
                    h("div", { class: "row" },
                        h("button", { class: "btn small", onClick: async () => { const p = await pickerFor("item", {}); if (p) ctx.set("/entries", [...entries, { item: p, chance: 1, min: 1, max: 1 }], { structural: true }); } }, "+ Item…"),
                        h("button", { class: "btn small", onClick: async () => { const p = await pickerFor("lootlist", {}); if (p) ctx.set("/entries", [...entries, { list: p, chance: 1 }], { structural: true }); } }, "+ Another list…")),
                    h("div", { class: "field-problems" }))), { state: ctx.viewState }),
            section(t("Simulator"), lootSimulator({ kind: "lootlist", list: d }), { open: true, state: ctx.viewState }));
    },
});
