// One problem shown to the user (studio SPEC §9.3): the catalogue sentence in the interface language (what is wrong, which field),
// the advice how to fix it, "Go to the field" and, when the server offers one, a one-click fix ("Use the template's model").
// The English detail of the producer stays in the tooltip.

import { diagAdvice, diagText, nameOf, t } from "./i18n.mjs";
import { h, plain } from "./ui.mjs";

/** Set by the app: fix(d) applies d.fix ({path, value: null → back to the template's}) to the draft d is about. */
export const diagHooks = { fix: null };

const SEV = { blocker: "⛔", warning: "⚠", info: "ℹ" };

/**
 * A problem: inline (next to its field: text, advice, fix) or in a list (with the entity and "Go to the field").
 * @param {object} d the diagnostic
 * @param {{onOpen?: (d: object) => void, inline?: boolean}} options
 */
export function problemLine(d, { onOpen, inline = false } = {}) {
    const advice = diagAdvice(d);
    const fix = d.fix && diagHooks.fix ? h("button", { class: "btn tiny", title: t("Apply this fix"), onClick: (e) => { e.stopPropagation(); diagHooks.fix(d); } }, nameOf(d.fix.label)) : null;
    const go = !inline && onOpen && (d.path || d.entity?.id)
        ? h("button", { class: "btn tiny ghost link", onClick: (e) => { e.stopPropagation(); onOpen(d); } }, d.path ? t("Go to the field") : t("Open")) : null;
    const where = inline ? null
        : d.entity?.id ? plain("div", { class: "muted small mono" }, `${d.entity.kind}/${d.entity.id}${d.field ? " · " + (nameOf(d.field) ?? "") : d.path ? " " + d.path : ""}`)
        : d.field ? plain("div", { class: "muted small" }, nameOf(d.field)) : null;
    return h("div", { class: `problem ${d.severity}${!inline && onOpen ? " clickable" : ""}`, title: d.message ?? "", onClick: !inline && onOpen ? () => onOpen(d) : undefined },
        h("span", { class: "sev" }, SEV[d.severity] ?? "ℹ"),
        h("div", { class: "grow" },
            plain("div", { class: "problem-text" }, diagText(d)),
            advice ? plain("div", { class: "problem-advice" }, advice) : null,
            where,
            fix || go ? h("div", { class: "problem-actions" }, fix, go) : null));
}
