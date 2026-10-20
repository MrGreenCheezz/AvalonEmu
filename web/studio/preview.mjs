// The right pane: the in-game tooltip of the draft, its difference to the template, the generated patch XML and its problems.

import { t, nameOf } from "./i18n.mjs";
import { problemLine } from "./diag.mjs";
import { h, icon, markup, mount, plain } from "./ui.mjs";
import { lootSimulator } from "./lootsim.mjs";
import { refBadge } from "./pickers.mjs";

const STAT_LABELS = {
    attackdamage: "Damage", attackspeed: "Attack speed", attackrange: "Attack range", hitpointsmax: "Max health", energymax: "Max energy",
    physicalarmor: "Armor", magicresistance: "Magic resistance", abilitypower: "Ability power", movespeedbonus: "Move speed", maxload: "Max load",
    healbonus: "Heal bonus", durability: "Durability", weight: "Weight", maxstacksize: "Stack", recastdelay: "Cooldown", energyusage: "Energy",
    castingtime: "Cast time", castrange: "Range", damagefactor: "Damage", fame: "Fame", hitdelay: "Hit delay", channelduration: "Channel", maxcharges: "Charges", target: "Target", category: "Category",
};

function statRow(s) {
    return h("div", { class: `tt-stat${s.changed ? " changed" : ""}` }, h("span", {}, STAT_LABELS[s.key] ?? s.key), " ",
        plain("b", {}, s.value), s.changed ? plain("span", { class: "was", title: t("the template's value") }, ` (${s.template})`) : null);
}

function spellCard(s) {
    const meta = [s.cooldown && `${t("Cooldown")} ${s.cooldown}${t("s")}`, s.energy && s.energy !== "0" && `${t("Energy")} ${s.energy}`,
        s.castTime && s.castTime !== "0" && `${t("Cast")} ${s.castTime}${t("s")}`, s.range && s.range !== "0" && `${t("Range")} ${s.range}`].filter(Boolean).join(" · ");
    const desc = nameOf(s.description);
    return h("div", { class: `tt-spell${s.found ? "" : " bad"}` },
        icon(s.icon, 36, "S"),
        h("div", { class: "grow" },
            h("div", { class: "tt-spell-name" }, plain("b", {}, nameOf(s.name) ?? s.spell), s.slot ? h("span", { class: "chip" }, t("slot {1}", { 1: s.slot })) : h("span", { class: "chip" }, t("passive")),
                s.own ? h("span", { class: "chip own" }, "this pack") : null),
            meta ? h("div", { class: "muted small" }, meta) : null,
            desc ? h("div", { class: "tt-desc" }, markup(desc)) : null));
}

/** A server loot list or mob variant (phase 3): their entries, and the loot simulator. */
function serverTooltip(tt) {
    const name = nameOf(tt.name) || tt.id;
    const pct = (v) => `${+(Number(v) * 100).toFixed(3)}%`;
    if (tt.kind === "lootlist")
        return h("div", { class: "tooltip" },
            h("div", { class: "tt-head" }, icon(null, 48, "L"), h("div", {}, plain("div", { class: "tt-name" }, name), h("div", { class: "tt-sub" }, t("server loot list"), ` · ${t("weighted picks")}: ${tt.rolls}`))),
            h("div", { class: "tt-section" }, ...(tt.entries ?? []).map((e) => h("div", { class: "tt-res" }, icon(e.icon, 24, e.list ? "L" : "?"),
                plain("span", {}, `${e.min === e.max ? e.min : `${e.min}-${e.max}`}× ${nameOf(e.name) ?? e.item ?? e.list}`),
                plain("span", { class: "muted small" }, Number(e.weight) > 0 ? ` ${t("weight")} ${e.weight}` : ` ${pct(e.chance)}`)))));
    return h("div", { class: "tooltip" },
        h("div", { class: "tt-head" }, icon(null, 48, "M"), h("div", {}, plain("div", { class: "tt-name" }, name),
            h("div", { class: "tt-sub" }, tt.tier ? `T${tt.tier} ` : "", t("mob variant"), tt.category ? plain("span", {}, ` · ${tt.category}`) : ""))),
        nameOf(tt.description) ? h("div", { class: "tt-desc" }, nameOf(tt.description)) : null,
        tt.stats?.length ? h("div", { class: "tt-stats" }, ...tt.stats.map(statRow)) : null,
        tt.loot?.length ? h("div", { class: "tt-section" }, h("div", { class: "tt-title" }, "Loot lists"), ...tt.loot.map((l) => h("div", { class: "small" }, refBadge("lootlist", l.list), ` ${pct(l.chance)}`))) : null,
        tt.rules?.length ? h("div", { class: "tt-section" }, h("div", { class: "tt-title" }, "Drop rules"), ...tt.rules.map((r) => h("div", { class: "small" }, refBadge("item", r.item), ` ${pct(r.chance)} × ${r.amount}`))) : null);
}

/** The tooltip as the game would show it (the main parts). */
export function tooltip(tt) {
    if (!tt) return h("div", { class: "muted" }, "No preview yet.");
    if (tt.kind === "lootlist" || tt.kind === "mob") return h("div", {}, serverTooltip(tt),
        tt.kind === "mob" ? h("div", { class: "tt-section" }, lootSimulator({ kind: "mob", lists: tt.loot ?? [], rules: tt.rules ?? [] })) : null);
    const name = nameOf(tt.name) || tt.id;
    const desc = nameOf(tt.description);
    return h("div", { class: "tooltip" },
        h("div", { class: "tt-head" }, icon(tt.icon, 64, tt.kind === "spell" ? "S" : "I"),
            h("div", {}, plain("div", { class: "tt-name" }, name),
                h("div", { class: "tt-sub" }, tt.tier ? `T${tt.tier} ` : "", tt.type ? plain("span", {}, tt.type) : "", tt.slot ? plain("span", {}, ` · ${tt.slot}`) : "", tt.category ? plain("span", {}, ` · ${tt.category}`) : ""),
                tt.itemPower ? h("div", { class: "tt-ip" }, t("Item power"), " ", plain("b", {}, tt.itemPower)) : null)),
        desc ? h("div", { class: "tt-desc" }, markup(desc)) : null,
        tt.stats?.length ? h("div", { class: "tt-stats" }, ...tt.stats.map(statRow)) : null,
        tt.spells?.length ? h("div", { class: "tt-section" }, h("div", { class: "tt-title" }, "Abilities"), ...tt.spells.map(spellCard)) : null,
        tt.recipe?.length ? h("div", { class: "tt-section" }, h("div", { class: "tt-title" }, "Recipe"),
            h("div", { class: "tt-recipe" }, ...tt.recipe.map((r) => h("span", { class: "tt-res", title: r.item }, icon(r.icon, 28, "?"), plain("span", {}, `${r.count}× ${nameOf(r.name) ?? r.item}`))))) : null,
        tt.blocks?.length ? h("div", { class: "tt-section" }, h("div", { class: "tt-title" }, "Effect blocks"),
            ...tt.blocks.map((b) => h("div", { class: "small" }, plain("span", { class: "mono" }, `<${b.element}> `), plain("span", { class: "muted" }, b.summary)))) : null,
        tt.subSpells?.length ? h("div", { class: "small muted" }, t("Cloned sub-spells: "), tt.subSpells.join(", ")) : null);
}

/** The line diff against the template. */
export function diffView(diff) {
    if (!diff?.length) return h("div", { class: "muted" }, "No difference to the template yet.");
    if (!diff.some((d) => d.op === "+" || d.op === "-")) return h("div", { class: "muted" }, "No difference to the template yet.");
    return h("pre", { class: "diff" }, ...diff.map((d) => plain("span", { class: d.op === "+" ? "add" : d.op === "-" ? "del" : d.op === "…" ? "gap" : "" }, `${d.op === "…" ? "…" : d.op} ${d.line}\n`)));
}

/** A list of diagnostics; onOpen(d) jumps to the entity/field. */
export function problemList(diags, onOpen) {
    if (!diags?.length) return h("div", { class: "ok-note" }, "✓ ", t("No problems"));
    const order = { blocker: 0, warning: 1, info: 2 };
    return h("div", { class: "problems" }, ...[...diags].sort((a, b) => order[a.severity] - order[b.severity]).map((d) => problemLine(d, { onOpen })));
}

/** The whole right pane for a preview answer; tab is "tooltip" | "diff" | "xml" | "problems". */
export function previewPane(preview, { tab, setTab, onOpenProblem, loading }) {
    const tabs = [["tooltip", t("Tooltip")], ["diff", t("Diff")], ["xml", "XML"], ["problems", `${t("Problems")} ${preview?.diagnostics?.length ? `(${preview.diagnostics.length})` : ""}`]];
    let body;
    if (!preview) body = h("div", { class: "muted" }, loading ? "Building the preview…" : "Open an entity to see its preview.");
    else if (preview.error) body = h("div", {}, h("div", { class: "problem blocker" }, preview.error), problemList(preview.diagnostics, onOpenProblem));
    else if (tab === "diff") body = diffView(preview.diff);
    else if (tab === "xml") body = h("div", {},
        ...Object.entries(preview.xml ?? {}).map(([file, text]) => h("div", {}, h("div", { class: "tt-title" }, file), plain("pre", { class: "xml" }, text))),
        preview.loc?.length ? h("div", {}, h("div", { class: "tt-title" }, "localization.xml"), plain("pre", { class: "xml" }, preview.loc.map((l) => `${l.tuid}\n  EN: ${l.en}\n  RU: ${l.ru}`).join("\n"))) : null,
        Object.keys(preview.stations ?? {}).length ? h("div", {}, h("div", { class: "tt-title" }, "buildings.xml"), plain("pre", { class: "xml" }, Object.entries(preview.stations).map(([i, s]) => `${i}: ${s.join(", ") || "-"}`).join("\n"))) : null,
        preview.drops?.length ? h("div", {}, h("div", { class: "tt-title" }, "server/drops.json"), plain("pre", { class: "xml" }, preview.drops.map((d) => d.text).join("\n"))) : null);
    else if (tab === "problems") body = problemList(preview.diagnostics, onOpenProblem);
    else body = tooltip(preview.tooltip);
    return h("div", { class: "preview" },
        h("div", { class: "tabs" }, ...tabs.map(([key, label]) => h("button", { class: `tab${tab === key ? " active" : ""}`, onClick: () => setTab(key) }, label)),
            loading ? h("span", { class: "spinner", title: t("updating") }) : null),
        h("div", { class: "preview-body", "data-scroll-key": "preview" }, body));
}

export { mount };
