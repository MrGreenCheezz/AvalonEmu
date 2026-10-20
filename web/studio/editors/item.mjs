// The generic item editor (the fallback every item has; recipe editors come on top): identity and texts, main stats, crafting
// (recipe, stations), spell slots, server drops, and every template attribute as a typed field.

import { t, lang } from "../i18n.mjs";
import { renderField, section } from "../forms.mjs";
import { pickerFor, refBadge } from "../pickers.mjs";
import { h, plain, pointer } from "../ui.mjs";
import { ITEM_ATTRS, ITEM_GROUPS, attrDescriptor, groupLabel } from "./attributes.mjs";
import { registerEditor } from "./registry.mjs";

const MAIN = ["tier", "itempower", "attackdamage", "attackspeed", "attackrange", "hitpointsmax", "physicalarmor", "magicresistance", "abilitypower", "weight", "maxstacksize", "durability"];

/** Identity: id (with rename), template, names and description. Shared by the item and spell editors. */
export function identitySection(ctx, kind) {
    const d = ctx.draft;
    return section(t("Identity and texts"), h("div", { class: "grid" },
        h("div", { class: "field", dataset: { path: "/id" } },
            h("div", { class: "field-head" }, h("label", { class: "label" }, "Unique name")),
            h("div", { class: "row" }, plain("span", { class: "mono big" }, d.id), h("button", { class: "btn small", onClick: () => ctx.rename?.() }, "Rename…")),
            h("div", { class: "hint" }, "The game's key of the new entry (A-Z 0-9 _). Rename updates the references in this pack."),
            h("div", { class: "field-problems" })),
        h("div", { class: "field", dataset: { path: "/template" } },
            h("div", { class: "field-head" }, h("label", { class: "label" }, "Made from (template)")),
            h("div", { class: "row" }, refBadge(kind, d.template, { onClick: () => ctx.showDetail(kind, d.template) })),
            h("div", { class: "hint" }, kind === "item" ? "Effects and sounds are the template's; the model and the icon can be another official item's (Look)." : "Animations, effects and sounds are the template's."),
            h("div", { class: "field-problems" })),
        renderField({ path: "/name", type: "loc", label: t("Name") }, ctx),
        renderField({ path: "/description", type: "loc", label: t("Description"), multiline: true,
            hint: kind === "spell" ? t("{0}, {1}… are filled with the spell's numbers in the game (see the preview). [dmg]…[/dmg] colours a part.") : "" }, ctx)));
}

/**
 * The look (SPEC §9.2): the model on the character (mesh) and the icon (uisprite), the template's unless another official one is
 * chosen with the model / icon picker. Empty in the draft = the template's.
 */
function lookSection(ctx) {
    const tpl = ctx.template ?? {};
    const field = (path, kind, label, templateValue, hint, chooseLabel, pickOptions) => {
        const value = ctx.draft[path.slice(1)] ?? "";
        const choose = async () => {
            const picked = await pickerFor(kind, { current: value || templateValue, ...pickOptions });
            if (picked) ctx.set(path, picked === templateValue ? undefined : picked, { structural: true });
        };
        return h("div", { class: `field${value ? " changed" : ""}`, dataset: { path, template: templateValue ?? "" } },
            h("div", { class: "field-head" }, h("label", { class: "label" }, label), plain("span", { class: "attr-name mono" }, path.slice(1))),
            h("div", { class: "ref-input" }, refBadge(kind, value || templateValue, { onClick: choose }),
                h("button", { class: "btn small", onClick: choose }, chooseLabel),
                value ? h("button", { class: "btn small ghost", title: t("Back to the template's value: {1}", { 1: templateValue }), onClick: () => ctx.set(path, undefined, { structural: true }) }, t("Use the template's")) : null),
            h("div", { class: "hint" }, hint),
            h("div", { class: "template-value" }, t("template:"), " ", plain("span", { class: "mono" }, templateValue ?? "—")),
            h("div", { class: "field-problems" }));
    };
    return h("div", { class: "grid" },
        tpl.model ? field("/mesh", "model", t("Model"), tpl.model, t("The 3D model on the character: only one an official item uses."), t("Choose model…"), { slot: tpl.slot })
            : h("div", { class: "field", dataset: { path: "/mesh" } }, h("div", { class: "label" }, t("Model")), h("div", { class: "muted small" }, t("Items of this kind show no model on the character.")), h("div", { class: "field-problems" })),
        field("/uisprite", "icon", t("Icon"), tpl.sprite, t("The icon in the inventory and the shop: only one an official item uses."), t("Choose icon…"), { forItem: true }));
}

function attributeFields(ctx, attrs, { filter = "", changedOnly = false } = {}) {
    const needle = filter.trim().toLowerCase();
    const descs = attrs.filter((a) => a.name !== "uniquename").map((a) => attrDescriptor(a, ITEM_ATTRS))
        .filter((d) => !needle || d.name.toLowerCase().includes(needle) || String(d.label).toLowerCase().includes(needle))
        .filter((d) => !changedOnly || ctx.draft.attributes?.[d.name] !== undefined);
    const groups = new Map();
    for (const d of descs) {
        if (!groups.has(d.group)) groups.set(d.group, []);
        groups.get(d.group).push(d);
    }
    return ITEM_GROUPS.filter(([g]) => groups.has(g)).map(([g]) =>
        h("div", { class: "attr-group" }, h("h4", {}, groupLabel(ITEM_GROUPS, g), " ", h("span", { class: "muted small" }, `(${groups.get(g).length})`)),
            h("div", { class: "grid" }, ...groups.get(g).map((d) => renderField(d, ctx)))));
}

function spellsSection(ctx) {
    const d = ctx.draft;
    const list = Array.isArray(d.spells) ? d.spells : null;
    if (!list) {
        return h("div", {}, h("p", { class: "muted" }, "Uses the template's spells."),
            h("button", { class: "btn", onClick: () => ctx.set("/spells", (ctx.template?.spells ?? []).map((s) => ({ spell: s.name, slot: s.slot ? Number(s.slot) : null, tag: s.tag ?? null })), { structural: true }) }, "Customise"));
    }
    const slotOptions = [["", t("passive")], ["1", "1"], ["2", "2"], ["3", "3"], ["4", "4"], ["5", "5"], ["6", "6"]];
    const rows = list.map((s, i) => {
        const base = pointer("spells", i);
        const choose = async () => {
            const picked = await pickerFor("spell", { current: s.spell, filters: s.slot ? {} : { type: "passivespell" } });
            if (picked) ctx.set(base + "/spell", picked, { structural: true });
        };
        const slot = h("select", { "aria-label": "slot" }, ...slotOptions.map(([v, l]) => h("option", { value: v, selected: String(s.slot ?? "") === v }, l)));
        slot.addEventListener("change", () => ctx.set(base + "/slot", slot.value ? Number(slot.value) : null, { structural: true }));
        return h("div", { class: "list-row", dataset: { path: base } },
            h("span", { class: "label" }, "slot"), slot,
            h("div", { class: "grow", dataset: { path: base + "/spell" } }, refBadge("spell", s.spell, { onClick: choose }), h("div", { class: "field-problems" })),
            s.tag ? plain("span", { class: "chip", title: t("unlock tag (destiny board)") }, s.tag) : null,
            h("button", { class: "btn tiny ghost", title: "Details", onClick: () => ctx.showDetail("spell", s.spell) }, "ⓘ"),
            h("button", { class: "btn tiny ghost", title: "Move up", disabled: i === 0, onClick: () => { const next = [...list]; [next[i - 1], next[i]] = [next[i], next[i - 1]]; ctx.set("/spells", next, { structural: true }); } }, "↑"),
            h("button", { class: "btn tiny ghost", title: "Remove", onClick: () => ctx.set("/spells", list.filter((_, k) => k !== i), { structural: true }) }, "✕"),
            h("div", { class: "field-problems" }));
    });
    const bySlot = (n) => list.filter((s) => String(s.slot ?? "") === n).length;
    return h("div", { class: "spell-list", dataset: { path: "/spells" } },
        h("div", { class: "muted small" }, t("Slot 1: {1}, slot 2: {2}, slot 3: {3}, passives: {4}", { 1: bySlot("1"), 2: bySlot("2"), 3: bySlot("3"), 4: bySlot("") })),
        ...rows,
        h("div", { class: "row" },
            h("button", { class: "btn small", onClick: async () => { const p = await pickerFor("spell", {}); if (p) ctx.set("/spells", [...list, { spell: p, slot: 1 }], { structural: true }); } }, "+ Active spell…"),
            h("button", { class: "btn small", onClick: async () => { const p = await pickerFor("spell", { filters: { type: "passivespell" } }); if (p) ctx.set("/spells", [...list, { spell: p, slot: null }], { structural: true }); } }, "+ Passive…"),
            h("button", { class: "btn small ghost", onClick: () => ctx.set("/spells", undefined, { structural: true }) }, "Use the template's")),
        h("div", { class: "field-problems" }));
}

/**
 * The extra action bars (server/extraspells.json, written by Generate; analysis/work/stageN/extrabars/SPEC.md): active spells the item grants
 * while worn, in two extra bars of six (Shift+1..6, Ctrl+1..6 with the client mod). Order = the order they fill the bars.
 */
function extraSpellsSection(ctx) {
    const list = Array.isArray(ctx.draft.extraSpells) ? ctx.draft.extraSpells : [];
    const set = (next) => ctx.set("/extraSpells", next.length ? next : undefined, { structural: true });
    const rows = list.map((spell, i) => {
        const path = pointer("extraSpells", i);
        const choose = async () => {
            const picked = await pickerFor("spell", { current: spell, filters: { type: "activespell" } });
            if (picked) ctx.set(path, picked, { structural: true });
        };
        return h("div", { class: "list-row", dataset: { path } },
            plain("span", { class: "label" }, String(i + 1)),
            h("div", { class: "grow" }, refBadge("spell", spell, { onClick: choose }), h("div", { class: "field-problems" })),
            h("button", { class: "btn tiny ghost", title: "Details", onClick: () => ctx.showDetail("spell", spell) }, "ⓘ"),
            h("button", { class: "btn tiny ghost", title: "Move up", disabled: i === 0, onClick: () => { const next = [...list]; [next[i - 1], next[i]] = [next[i], next[i - 1]]; set(next); } }, "↑"),
            h("button", { class: "btn tiny ghost", title: "Remove", onClick: () => set(list.filter((_, k) => k !== i)) }, "✕"));
    });
    return h("div", { class: "spell-list", dataset: { path: "/extraSpells" } },
        h("p", { class: "muted small" }, "Active spells this item adds to the two extra action bars while it is worn (the client mod: Shift+1…6, Ctrl+1…6). server/extraspells.json: needs the pack in Mods:Packs; Generate updates the running server."),
        ...rows,
        h("div", { class: "row" },
            h("button", { class: "btn small", disabled: list.length >= 12, onClick: async () => { const p = await pickerFor("spell", { filters: { type: "activespell" } }); if (p) set([...list, p]); } }, "+ Extra ability…")),
        h("div", { class: "field-problems" }));
}

function craftingSection(ctx) {
    const d = ctx.draft;
    const recipe = d.recipe;
    const tplRecipe = ctx.template?.recipes?.[0];
    const parts = [];
    if (!recipe) {
        parts.push(h("p", { class: "muted" }, tplRecipe ? "Uses the template's recipe." : "Not craftable (the template has no recipe)."),
            h("button", { class: "btn", onClick: () => ctx.set("/recipe", tplRecipe
                ? { silver: tplRecipe.silver ?? "0", time: tplRecipe.time ?? "1", focus: tplRecipe.focus ?? "0", amount: tplRecipe.amount ?? null, resources: tplRecipe.resources.map((r) => ({ item: r.name, count: Number(r.count) || 1 })) }
                : { silver: "0", time: "1", focus: "0", resources: [] }, { structural: true }) }, tplRecipe ? "Customise the recipe" : "Make it craftable"));
    } else {
        parts.push(h("div", { class: "grid" },
            renderField({ path: "/recipe/silver", type: "number", label: t("Silver"), template: tplRecipe?.silver, asString: true }, ctx),
            renderField({ path: "/recipe/time", type: "number", label: t("Craft time"), template: tplRecipe?.time, asString: true, hint: t("in game units (the template's scale)") }, ctx),
            renderField({ path: "/recipe/focus", type: "number", label: t("Focus cost"), template: tplRecipe?.focus, asString: true }, ctx),
            renderField({ path: "/recipe/amount", type: "number", label: t("Amount crafted"), template: tplRecipe?.amount ?? "1", asString: true }, ctx)),
            renderField({ path: "/recipe/resources", type: "itemList", label: t("Ingredients") }, ctx));
    }
    parts.push(renderField({ path: "/enchantments", type: "bool", label: t("Enchantment levels .1-.3"), default: false,
        hint: t("keep the template's .1-.3 (their recipes and item power) for the new item") }, ctx));
    parts.push(renderField({ path: "/stations", type: "refList", refKind: "station", label: t("Crafting stations"),
        hint: t("Where the item is crafted. None: the recipe exists but no station offers it.") }, ctx));
    return h("div", {}, ...parts);
}

registerEditor("item", {
    label: "Item",
    letter: "I",
    render(ctx) {
        const attrs = ctx.template?.attributes ?? [];
        const byName = new Map(attrs.map((a) => [a.name, a]));
        const main = MAIN.filter((n) => byName.has(n)).map((n) => renderField({ ...attrDescriptor(byName.get(n), ITEM_ATTRS), range: ctx.ranges?.[n] }, ctx));
        const state = ctx.viewState;
        state.attrFilter ??= "";
        state.changedOnly ??= false;
        const allBox = h("div", {});
        const renderAll = () => allBox.replaceChildren(...attributeFields(ctx, attrs, { filter: state.attrFilter, changedOnly: state.changedOnly }));
        const filter = h("input", { type: "search", placeholder: "Filter attributes", value: state.attrFilter, "data-focus-key": "attr-filter" });
        filter.addEventListener("input", () => { state.attrFilter = filter.value; renderAll(); });
        const changed = h("input", { type: "checkbox", checked: state.changedOnly });
        changed.addEventListener("change", () => { state.changedOnly = changed.checked; renderAll(); });
        renderAll();
        const overrides = Object.keys(ctx.draft.attributes ?? {}).length;
        return h("div", { class: "editor-sections" },
            identitySection(ctx, "item"),
            section(t("Look"), lookSection(ctx), { open: Boolean(ctx.draft.mesh || ctx.draft.uisprite), state }),
            section(t("Main stats"), h("div", { class: "grid" }, ...main), { state }),
            section(t("Crafting"), craftingSection(ctx), { state }),
            section(t("Spells (abilities)"), spellsSection(ctx), { state }),
            section(t("Extra abilities"), extraSpellsSection(ctx), { open: Array.isArray(ctx.draft.extraSpells) && ctx.draft.extraSpells.length > 0, state }),
            section(t("Drops on the server"), renderField({ path: "/drops", type: "drops", label: t("Where it drops"), categories: ctx.facets?.mob?.category?.map((c) => c.value) ?? [],
                hint: t("server/drops.json: needs the pack in Mods:Packs") }, ctx)),
            section(t("All attributes"), h("div", {},
                h("div", { class: "row" }, filter, h("label", { class: "check" }, changed, " ", t("changed only")), h("span", { class: "muted small" }, t("{1} changed", { 1: overrides }))),
                allBox), { open: overrides > 0, state }));
    },
});
