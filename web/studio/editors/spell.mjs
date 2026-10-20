// The generic spell editor: identity and texts, the main numbers (cooldown, energy, cast time, range…), whether its sub-spells
// are cloned with it, every root attribute, and every attribute of its effect blocks (edits addressed by element path).

import { api } from "../api.mjs";
import { t } from "../i18n.mjs";
import { renderField, section } from "../forms.mjs";
import { pickSpell } from "../pickers.mjs";
import { h, modal, plain, pointer, toast } from "../ui.mjs";
import { SPELL_ATTRS, attrDescriptor } from "./attributes.mjs";
import { identitySection } from "./item.mjs";
import { registerEditor } from "./registry.mjs";

const MAIN = ["recastdelay", "energyusage", "castingtime", "castrange", "hitdelay", "channelduration", "maxcharges", "target", "category", "disruptionfactor"];
const OWN = new Set(["uniquename", "namelocatag", "descriptionlocatag"]);

const removedOf = (draft) => (Array.isArray(draft.blocks?.remove) ? draft.blocks.remove : []);
const addedOf = (draft) => (Array.isArray(draft.blocks?.add) ? draft.blocks.add : []);

/** Takes a top-level block out (or back in): blocks.remove holds template paths. */
function toggleRemove(ctx, path) {
    const removed = removedOf(ctx.draft);
    const next = removed.includes(path) ? removed.filter((p) => p !== path) : [...removed, path];
    ctx.set("/blocks/remove", next, { structural: true });
}

function blockFields(ctx, blocks, filter) {
    const needle = filter.trim().toLowerCase();
    const edits = ctx.draft.edits ?? {};
    const removed = removedOf(ctx.draft);
    const gone = (path) => removed.some((r) => path === r || path.startsWith(r + "/"));
    return blocks.filter((b) => b.attributes.length > 0 || b.depth === 0).map((b) => {
        if (gone(b.path)) {
            if (!removed.includes(b.path)) return null;
            return h("div", { class: "block removed", style: { marginLeft: `${Math.min(b.depth, 4) * 14}px` } },
                plain("span", { class: "mono" }, `<${b.element}>`), " ", h("span", { class: "chip bad" }, "taken out"), " ",
                h("button", { class: "btn tiny", onClick: () => toggleRemove(ctx, b.path) }, "Put back"));
        }
        const descs = b.attributes.map((a) => attrDescriptor(a, SPELL_ATTRS, "/edits", `${b.path}@${a.name}`))
            .filter((d) => !needle || d.name.includes(needle) || b.element.toLowerCase().includes(needle));
        if (!descs.length && (b.depth !== 0 || needle)) return null;
        const changed = b.attributes.filter((a) => edits[`${b.path}@${a.name}`] !== undefined).length;
        return h("details", { class: `block depth-${Math.min(b.depth, 4)}`, open: changed > 0 || needle.length > 0, style: { marginLeft: `${Math.min(b.depth, 4) * 14}px` } },
            h("summary", {}, plain("span", { class: "mono" }, `<${b.element}>`), " ",
                plain("span", { class: "muted small" }, b.attributes.slice(0, 4).map((a) => `${a.name}=${a.value}`).join(" ")),
                changed ? h("span", { class: "chip changed" }, t("{1} changed", { 1: changed })) : null,
                b.depth === 0 ? h("button", { class: "btn tiny ghost", title: "Take this block out of the spell", onClick: (e) => { e.preventDefault(); toggleRemove(ctx, b.path); } }, "✕") : null),
            h("div", { class: "grid" }, ...descs.map((d) => renderField(d, ctx))));
    });
}

// Added blocks: copies of a block of any official (or this pack's) spell, with their own attribute values.
const sourceCache = new Map();
function sourceOf(name) {
    if (!sourceCache.has(name)) sourceCache.set(name, api.detail("spell", name).catch(() => null));
    return sourceCache.get(name);
}

/** Picks a block of a spell: the spell from the catalogue, then one of its blocks (its parameters shown). */
async function pickBlock() {
    const spell = await pickSpell({ title: t("Add a block: the spell to take it from"), filters: { offered: "" } });
    if (!spell) return null;
    const detail = await sourceOf(spell);
    if (!detail) { toast(t("No spell {1}", { 1: spell }), "bad"); return null; }
    const blocks = (detail.blocks ?? []).filter((b) => b.depth === 0 && b.attributes.length > 0);
    const path = await modal({
        title: t("Add a block of {1}", { 1: spell }),
        width: "760px",
        body: (close) => h("div", { class: "stack" },
            blocks.length === 0 ? h("p", { class: "muted" }, "This spell has no effect blocks with parameters.") : null,
            ...blocks.map((b) => h("button", { class: "card action block-pick", onClick: () => close(b.path) },
                plain("b", { class: "mono" }, `<${b.element}>`), " ", plain("span", { class: "muted small mono" }, `#${b.path}`),
                plain("div", { class: "small mono" }, b.attributes.map((a) => `${a.name}=${a.value}`).join("  "))))),
    });
    return path ? { from: spell, path, attributes: {} } : null;
}

function addedBlocks(ctx) {
    const added = addedOf(ctx.draft);
    const box = h("div", { class: "added-blocks", dataset: { path: "/blocks/add" } });
    const rows = added.map((add, i) => {
        const base = `/blocks/add/${i}`;
        const title = plain("span", { class: "mono" }, `${add.from} #${add.path}`);
        const fields = h("div", { class: "grid" });
        const row = h("div", { class: "block added", dataset: { path: base } },
            h("div", { class: "row" }, h("span", { class: "chip own" }, "added"), title, h("span", { class: "grow" }),
                h("button", { class: "btn tiny ghost", title: "Remove", onClick: () => ctx.set("/blocks/add", added.filter((_, k) => k !== i), { structural: true }) }, "✕")),
            h("div", { class: "field-problems" }), fields);
        sourceOf(add.from).then((detail) => {
            const block = detail?.blocks?.find((b) => b.path === add.path);
            if (!block) { fields.replaceChildren(h("div", { class: "problem blocker" }, t("No block {1} in {2}", { 1: add.path, 2: add.from }))); return; }
            title.textContent = `<${block.element}> ${t("from")} ${add.from} #${add.path}`;
            fields.replaceChildren(...block.attributes.filter((a) => a.type !== "asset").map((a) => renderField(attrDescriptor(a, SPELL_ATTRS, `${base}/attributes`, a.name), ctx)));
        });
        return row;
    });
    box.append(...rows, h("button", { class: "btn small", onClick: async () => {
        const block = await pickBlock();
        if (block) ctx.set("/blocks/add", [...added, block], { structural: true });
    } }, "+ Add a block from a spell…"), h("div", { class: "hint" }, "Added blocks go at the end of the spell, after the template's (taken-out ones removed)."));
    return box;
}

registerEditor("spell", {
    label: "Spell",
    letter: "S",
    render(ctx) {
        const attrs = (ctx.template?.attributes ?? []).filter((a) => !OWN.has(a.name));
        const byName = new Map(attrs.map((a) => [a.name, a]));
        const main = MAIN.filter((n) => byName.has(n)).map((n) => renderField({ ...attrDescriptor(byName.get(n), SPELL_ATTRS), range: ctx.ranges?.[n] }, ctx));
        const rest = attrs.filter((a) => !MAIN.includes(a.name)).map((a) => renderField(attrDescriptor(a, SPELL_ATTRS), ctx));
        const state = ctx.viewState;
        state.blockFilter ??= "";
        const blocksBox = h("div", {});
        const blocks = ctx.template?.blocks ?? [];
        const renderBlocks = () => blocksBox.replaceChildren(...blockFields(ctx, blocks, state.blockFilter).filter(Boolean));
        const filter = h("input", { type: "search", placeholder: "Filter blocks (element or attribute)", value: state.blockFilter, "data-focus-key": "block-filter" });
        filter.addEventListener("input", () => { state.blockFilter = filter.value; renderBlocks(); });
        renderBlocks();
        const uses = ctx.template?.uses ?? [];
        return h("div", { class: "editor-sections" },
            identitySection(ctx, "spell"),
            section(t("Main numbers"), h("div", { class: "grid" }, ...main), { state }),
            section(t("Sub-spells"), h("div", {},
                renderField({ path: "/subSpells", type: "select", label: t("The spells it uses"), options: [
                    { value: "official", label: t("stay the official ones (shared with the original)") },
                    { value: "clone", label: t("are cloned into this pack with it (their numbers become its own)") }], default: "official" }, ctx),
                uses.length ? h("div", { class: "hint" }, t("It uses: "), uses.map((u) => u.name).join(", ")) : h("div", { class: "hint" }, "It uses no other spells."))),
            section(t("Effect blocks"), h("div", {}, h("div", { class: "row" }, filter,
                h("span", { class: "muted small" }, t("{1} edits", { 1: Object.keys(ctx.draft.edits ?? {}).length })),
                removedOf(ctx.draft).length ? h("span", { class: "chip bad" }, t("{1} taken out", { 1: removedOf(ctx.draft).length })) : null), blocksBox,
                addedBlocks(ctx)), { open: true, state }),
            section(t("Other attributes"), h("div", { class: "grid" }, ...rest), { open: false, state }));
    },
});

export { pointer };
