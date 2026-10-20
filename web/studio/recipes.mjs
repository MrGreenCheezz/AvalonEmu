// Recipes (phase 3): the user picks a GOAL ("one-handed weapon", "area damage", "legendary boss kit") instead of raw fields.
// The recipes and their declarative field lists come from the server (GET /studio/api/recipes, Web/Studio/StudioRecipes.cs):
// label RU/EN, hint, type, default, min/max, required, options, show-condition, whether an official range exists. Here:
//   recipeGallery()  the cards of "New…" (items, abilities, lines, kits) → recipeDialog()
//   recipeDialog()   the form (with the value helper), a live build (tooltip, the drafts it will make, problems) and Create
//   the "Goal" tab   (registerRecipe for every kind): a draft made by a recipe shows the recipe's form again, bound to
//                    /goal/values/…; a change rebuilds the draft from the goal (and saves the helper drafts it comes with).

import { api, ApiError } from "./api.mjs";
import { applyDiagnostics, renderField, section } from "./forms.mjs";
import { nameOf, t } from "./i18n.mjs";
import { problemList, tooltip } from "./preview.mjs";
import { clone, confirmDialog, debounce, getAt, h, isComposing, modal, plain, remount, setAt, toast } from "./ui.mjs";
import { registerRecipe } from "./editors/registry.mjs";

let recipes = [];
const byId = () => new Map(recipes.map((r) => [r.id, r]));

export async function loadRecipes() {
    try {
        recipes = (await api.recipes()).recipes ?? [];
    } catch {
        recipes = [];
    }
    return recipes;
}

export const recipeList = () => recipes;

const GROUPS = [["item", "Items"], ["spell", "Abilities"], ["line", "Item lines"], ["kit", "Kits"]];

/** Whether a field applies to the values ("key=a|b"). */
function shown(f, values) {
    if (!f.show) return true;
    const eq = f.show.indexOf("=");
    if (eq <= 0) return true;
    return f.show.slice(eq + 1).split("|").includes(String(values[f.show.slice(0, eq)] ?? ""));
}

/** The forms.mjs descriptor of a recipe field at base path (the values' root, or /goal/values in a draft). */
export function fieldDesc(f, base, ranges) {
    const type = { id: "text", lootlist: "lootList" }[f.type] ?? f.type;
    return {
        path: `${base}/${f.key}`, name: f.key, label: nameOf(f.label) ?? f.key, hint: nameOf(f.hint) ?? "", type, required: f.required,
        min: f.min ?? undefined, max: f.max ?? undefined, suffix: f.suffix ?? "", default: f.default ?? undefined,
        options: f.options?.map((o) => ({ value: o.value, label: nameOf(o.label) ?? o.value })),
        range: ranges?.[f.key], filters: f.type === "spell" ? {} : undefined,
        transform: f.type === "id" ? (v) => v.toUpperCase().replace(/[^A-Z0-9_]/g, "_") : undefined,
    };
}

function withDefaults(fields, values) {
    for (const f of fields) if (values[f.key] === undefined && f.default !== undefined && f.default !== null) values[f.key] = clone(f.default);
    return values;
}

/** The gallery of goals ("New… → From a goal"): cards by group; a click opens the recipe's dialog. */
export async function recipeGallery({ pack, only, onCreated }) {
    if (!recipes.length) await loadRecipes();
    const picked = await modal({
        title: t("New from a goal"),
        width: "980px",
        body: (close) => h("div", { class: "recipe-gallery" },
            h("p", { class: "muted" }, "Pick what you want to make; the recipe asks only for the numbers that matter and builds the drafts from official templates."),
            ...GROUPS.filter(([kind]) => !only || only.includes(kind)).map(([kind, label]) => {
                const list = recipes.filter((r) => r.kind === kind);
                if (!list.length) return null;
                return h("div", {}, h("h3", {}, label, h("span", { class: "muted small" }, ` ${list.length}`)),
                    h("div", { class: "cards recipe-cards" }, ...list.map((r) => h("button", { class: "card action recipe-card", onClick: () => close(r), title: (r.examples ?? []).join(", ") },
                        h("div", { class: "big" }, plain("span", {}, r.icon)), plain("b", {}, nameOf(r.title)),
                        plain("p", { class: "muted small" }, nameOf(r.summary)),
                        r.examples?.length ? plain("div", { class: "mono tiny-text" }, t("like {1}", { 1: r.examples.slice(0, 2).join(", ") })) : null))));
            })),
    });
    if (picked) return recipeDialog(picked, { pack, onCreated });
    return null;
}

/** The recipe's form with a live build on the right; Create saves the drafts into the pack. */
export function recipeDialog(recipe, { pack, onCreated, values: initial }) {
    const state = { values: withDefaults(recipe.fields, clone(initial ?? {})), fields: recipe.fields, build: null, ranges: {}, loading: false, busy: false };
    return modal({
        title: h("span", {}, plain("span", {}, `${recipe.icon} `), plain("span", {}, nameOf(recipe.title))),
        width: "1180px",
        className: "recipe-modal",
        body: (close) => {
            const form = h("div", { class: "recipe-form", "data-scroll-key": "recipe-form" });
            const side = h("div", { class: "recipe-side" });
            const create = h("button", { class: "btn primary", onClick: () => doCreate(close) }, recipe.kind === "kit" ? "Create the kit" : "Create");
            const ctx = {
                draft: state.values,
                set(path, value, { structural = false } = {}) {
                    setAt(state.values, path, value);
                    if (structural) renderForm();
                    buildSoon();
                },
                rerender: () => renderForm(),
            };
            const showProblems = () =>
                applyDiagnostics(form, (state.build?.diagnostics ?? []).filter((d) => d.path?.startsWith("/values/")).map((d) => ({ ...d, path: d.path.slice("/values".length) })));
            // remount: a re-render keeps the focused field, its caret and the text as typed (SPEC §9.1).
            const renderForm = () => {
                const fields = state.fields.filter((f) => shown(f, state.values));
                remount(form, h("p", { class: "muted small" }, nameOf(recipe.summary)),
                    h("div", { class: "grid" }, ...fields.map((f) => renderField(fieldDesc(f, "", state.ranges), ctx))));
                showProblems();
            };
            const renderSide = () => {
                const b = state.build;
                const blockers = (b?.diagnostics ?? []).filter((d) => d.severity === "blocker");
                create.disabled = state.busy || !b || blockers.some((d) => d.path?.startsWith("/values/") || d.code === "RECIPE" || d.code === "ID_TAKEN" || d.code === "BAD_ID") || !(b.drafts ?? []).length;
                side.replaceChildren(...[
                    state.loading ? h("div", { class: "muted small" }, h("span", { class: "spinner" }), " ", t("building…")) : null,
                    b?.tooltip ? tooltip(b.tooltip) : h("div", { class: "muted" }, "Fill the form: the preview appears here."),
                    b?.drafts?.length ? h("div", { class: "tt-section" }, h("div", { class: "tt-title" }, t("Will create ({1})", { 1: b.drafts.length })),
                        ...b.drafts.map((d) => h("div", { class: "small" }, h("span", { class: "chip" }, t(d.kind)), " ", plain("span", { class: "mono" }, d.id), " ", plain("span", { class: "muted" }, nameOf(d.name) ?? "")))) : null,
                    b ? h("div", { class: "tt-section" }, h("div", { class: "tt-title" }, t("Problems")), problemList((b.diagnostics ?? []).filter((d) => d.severity !== "info"))) : null].filter(Boolean));
            };
            // The live build runs 400 ms after the last change; it re-renders the form only when its fields or the official ranges
            // changed, else it only updates the preview and the problems next to the fields (the focused input is left alone).
            let seq = 0;
            const build = async () => {
                if (isComposing()) return buildSoon();
                const mine = ++seq;
                state.loading = true;
                renderSide();
                let structural = false;
                try {
                    const b = await api.buildRecipe(pack, recipe.id, state.values);
                    if (mine !== seq) return;
                    state.build = b;
                    const ranges = b.ranges ?? {};
                    if (JSON.stringify(ranges) !== JSON.stringify(state.ranges)) { state.ranges = ranges; structural = true; }
                    if (b.fields?.length && b.fields.length !== state.fields.length) {
                        state.fields = b.fields;
                        withDefaults(state.fields, state.values);
                        structural = true;
                    }
                } catch (e) {
                    if (mine !== seq) return;
                    state.build = { diagnostics: [{ severity: "blocker", code: "RECIPE", message: e.message, path: "" }], drafts: [] };
                }
                state.loading = false;
                if (structural) renderForm(); else showProblems();
                renderSide();
            };
            const buildSoon = debounce(build, 400);
            const doCreate = async (closeDialog) => {
                state.busy = true;
                renderSide();
                try {
                    let r;
                    try {
                        r = await api.createFromRecipe(pack, recipe.id, state.values);
                    } catch (e) {
                        if (!(e instanceof ApiError) || e.status !== 409) throw e;
                        const ok = await confirmDialog(t("Overwrite?"), h("div", {}, h("p", {}, e.message),
                            h("p", { class: "muted small" }, "Overwriting replaces those drafts (their earlier version stays in the history).")), t("Overwrite"), "danger");
                        if (!ok) return;
                        r = await api.createFromRecipe(pack, recipe.id, state.values, true);
                    }
                    closeDialog(r);
                    toast(t("Created: {1}", { 1: r.created.map((c) => c.id).join(", ") }), "ok");
                    await onCreated?.(r.created);
                } catch (e) {
                    toast(e.message, "bad");
                } finally {
                    state.busy = false;
                    renderSide();
                }
            };
            renderForm();
            renderSide();
            build();
            return h("div", { class: "recipe-layout" }, form, h("div", { class: "recipe-preview" }, side, h("div", { class: "row end" }, h("button", { class: "btn", onClick: () => close() }, "Cancel"), create)));
        },
    });
}

// ---------------------------------------------------------------- the Goal tab of a draft made by a recipe

const goalState = new WeakMap();

function goalTab(kind) {
    return {
        id: "goal",
        label: "Goal",
        order: 0,
        match: (draft) => Boolean(draft?.goal?.recipe) && byId().has(draft.goal.recipe),
        render(ctx) {
            const recipe = byId().get(ctx.draft.goal.recipe);
            const values = ctx.draft.goal.values ?? {};
            let st = goalState.get(ctx.viewState);
            if (!st) {
                st = { ranges: {}, last: JSON.stringify(values), applying: false, note: "" };
                goalState.set(ctx.viewState, st);
                api.buildRecipe(ctx.pack, recipe.id, values).then((b) => { st.ranges = b.ranges ?? {}; ctx.rerender(); }).catch(() => {});
            }
            const apply = async () => {
                if (isComposing()) return applySoon();
                const current = ctx.draft.goal?.values ?? {};
                const text = JSON.stringify(current);
                if (text === st.last && !st.force) return;
                st.last = text;
                st.force = false;
                let b;
                try {
                    b = await api.buildRecipe(ctx.pack, recipe.id, current);
                } catch (e) {
                    toast(e.message, "bad");
                    return;
                }
                st.ranges = b.ranges ?? {};
                const valueBlockers = (b.diagnostics ?? []).filter((d) => d.severity === "blocker" && d.path?.startsWith("/values/"));
                if (valueBlockers.length || !(b.drafts ?? []).length) { st.note = valueBlockers[0]?.message ?? ""; ctx.rerender(); return; }
                const main = b.drafts.find((d) => d.kind === ctx.draft.kind && d.id === ctx.draft.id) ?? b.drafts.find((d) => d.kind === ctx.draft.kind);
                if (!main) return;
                const next = clone(main);
                next.id = ctx.draft.id;
                next.goal = clone(ctx.draft.goal);
                if (Array.isArray(ctx.draft.drops) && ctx.draft.drops.length) next.drops = clone(ctx.draft.drops);
                for (const key of ["mesh", "uisprite"]) if (ctx.draft[key]) next[key] = ctx.draft[key]; // the look chosen by hand stays
                if (ctx.draft.updated) next.updated = ctx.draft.updated;
                st.note = main.id !== ctx.draft.id ? t("The goal's unique name is {1}: use Rename to change this draft's ({2}).", { 1: main.id, 2: ctx.draft.id }) : "";
                ctx.replace(next);
                // The helper drafts the recipe makes with it (a potion's effect, a field's tick, a mount's buff): saved at once.
                for (const helper of b.drafts.filter((d) => d !== main && d.goal === undefined)) {
                    try { await api.saveEntity(ctx.pack, helper); } catch (e) { toast(`${helper.id}: ${e.message}`, "warn"); }
                }
            };
            const applySoon = debounce(apply, 500);
            const goalCtx = { ...ctx, set: (path, value, opts) => { ctx.set(path, value, opts); applySoon(); } };
            const fields = recipe.fields.filter((f) => shown(f, values));
            return h("div", { class: "editor-sections" },
                h("div", { class: "goal-head" }, plain("span", { class: "big" }, recipe.icon), h("div", { class: "grow" }, plain("b", {}, nameOf(recipe.title)), plain("div", { class: "muted small" }, nameOf(recipe.summary))),
                    h("button", { class: "btn small", title: t("Build the draft from the goal again"), onClick: () => { st.force = true; apply(); } }, "Rebuild")),
                st.note ? plain("div", { class: "problem warning" }, st.note) : null,
                section(t("The goal"), h("div", { class: "grid" }, ...fields.map((f) => f.key === "base"
                    ? renderField({ ...fieldDesc(f, "/goal/values", st.ranges), type: "readonly", hint: t("the unique name: Rename… changes it") }, goalCtx)
                    : renderField(fieldDesc(f, "/goal/values", st.ranges), goalCtx))), { state: ctx.viewState }),
                h("p", { class: "muted small" }, "A change here rebuilds the draft from the goal (the other tabs show the result; drops you set stay). Save keeps it."));
        },
    };
}

for (const kind of ["item", "spell", "mob", "lootlist"]) registerRecipe(kind, goalTab(kind));

export { getAt };
