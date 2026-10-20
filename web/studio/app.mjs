// The Studio's shell: top bar (pack, Generate, Validate, Install/Restore, problems, jobs, language), the left pane (the pack's
// entities by kind, New…), the centre (editor / catalogue / home) and the right pane (live preview: tooltip, diff, XML, problems).

import { api, setCsrf } from "./api.mjs";
import { autosave, forget, pending, recall, UndoStack } from "./autosave.mjs";
import { catalogHooks, catalogView, showDetail } from "./catalog.mjs";
import { applyDiagnostics } from "./forms.mjs";
import { lang, nameOf, setLang, t, diagText } from "./i18n.mjs";
import { clearLookups, pickItem, pickMob, pickSpell, pickerHooks, setLocalRows } from "./pickers.mjs";
import { loadRecipes, recipeGallery } from "./recipes.mjs";
import { lootHooks } from "./lootsim.mjs";
import { previewPane, problemList } from "./preview.mjs";
import { diagHooks } from "./diag.mjs";
import { choiceDialog, clone, confirmDialog, debounce, discardTyping, h, icon, menuButton, modal, mount, plain, promptDialog, remount, setAt, toast } from "./ui.mjs";
import { getEditor, recipesFor } from "./editors/registry.mjs";
import "./editors/item.mjs";
import "./editors/spell.mjs";
import "./editors/server.mjs";
import { rulesUi } from "./editors/rules.mjs";

const $ = (id) => document.getElementById(id);

const S = {
    session: null,
    packs: [],
    pack: storageGet("studio.pack") ?? "",
    entities: [],
    legacy: [],
    broken: [],
    dirty: false,
    view: "home",
    current: null,
    preview: null,
    previewTab: "tooltip",
    previewLoading: false,
    jobs: [],
    facets: {},
    filter: "",
    restartNeeded: false,
};

function storageGet(key) {
    try { return window.localStorage.getItem(key); } catch { return null; }
}
function storageSet(key, value) {
    try { window.localStorage.setItem(key, value); } catch { /* no storage */ }
}

const ID_RE = /^[A-Z0-9][A-Z0-9_]{2,79}$/;
const PACK_RE = /^[a-z0-9][a-z0-9-]{0,39}$/;

// ---------------------------------------------------------------- start

async function start() {
    document.documentElement.lang = lang();
    try {
        S.session = await api.session();
    } catch (e) {
        mount($("center"), h("div", { class: "card bad" }, e.status === 401 ? h("a", { href: "/login?next=/studio" }, "Log in again") : e.message));
        return;
    }
    setCsrf(S.session.csrf);
    pickerHooks.showDetail = (kind, name) => showDetail(kind, name);
    catalogHooks.onCopy = (kind, name) => createFrom(kind, name);
    rulesUi.attach({ pack: () => S.pack, show: showRule, refresh: refreshRules });
    renderTop();
    await loadPacks();
    if (!S.session.catalog.ready) await waitCatalog();
    api.facets("mob").then((f) => { S.facets.mob = f; }).catch(() => {});
    lootHooks.loadList = (id) => (S.pack ? api.entity(S.pack, "lootlist", id).then((r) => r.draft) : Promise.resolve(null));
    await loadRecipes();
    await openPack(S.packs.some((p) => p.name === S.pack) ? S.pack : S.packs[0]?.name ?? "");
    pollJobs();
    restoreRoute();
    window.addEventListener("hashchange", restoreRoute);
    window.addEventListener("beforeunload", (e) => { if (isModified() || rulesUi.isModified()) { e.preventDefault(); e.returnValue = ""; } });
    document.addEventListener("keydown", onKey);
}

async function waitCatalog() {
    mount($("center"), h("div", { class: "card" }, h("h2", {}, "Loading the catalogue…"),
        h("p", { class: "muted" }, "The official items, spells, names (the 78 MB localization), mobs, loot and buildings are read once: about 10-30 s.")),
        h("div", { class: "spinner big" }));
    for (;;) {
        await new Promise((r) => setTimeout(r, 1500));
        try {
            S.session = await api.session();
            if (S.session.catalog.ready) break;
            if (S.session.catalog.problem) {
                mount($("center"), h("div", { class: "card bad" }, S.session.catalog.problem));
                return;
            }
        } catch { /* keep waiting */ }
    }
    renderTop();
}

// ---------------------------------------------------------------- routing (#/pack/kind/id, #/catalog)

function restoreRoute() {
    const parts = location.hash.replace(/^#\/?/, "").split("/").map(decodeURIComponent);
    if (parts[0] === "catalog") return showView("catalog");
    if (parts[0] === "r" && parts[1] && parts[2]) {
        if (parts[1] !== S.pack) return openPack(parts[1]).then(() => showRule(parts[2]));
        if (S.view !== "rules" || rulesUi.current() !== parts[2]) return showRule(parts[2]);
        return;
    }
    if (parts[0] === "e" && parts[1] && parts[2] && parts[3]) {
        if (parts[1] !== S.pack) return openPack(parts[1]).then(() => openEntity(parts[2], parts[3]));
        if (S.current?.kind !== parts[2] || S.current?.id !== parts[3]) return openEntity(parts[2], parts[3]);
        return;
    }
    if (!S.current) showView("home");
}

function setHash(hash) {
    if (location.hash !== hash) history.replaceState(null, "", hash || location.pathname);
}

async function showView(view) {
    if (view !== "editor" && (S.current || S.view === "rules") && !(await leaveCurrent())) return;
    S.view = view;
    if (view !== "editor") { S.current = null; S.preview = null; }
    setHash(view === "catalog" ? "#/catalog" : "");
    renderAll();
}

// ---------------------------------------------------------------- packs

async function loadPacks() {
    try {
        S.packs = (await api.packs()).packs;
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function openPack(name) {
    S.pack = name ?? "";
    storageSet("studio.pack", S.pack);
    await refreshEntities();
    await rulesUi.packChanged();
    renderAll();
}

// ---------------------------------------------------------------- rules (editors/rules.mjs: the no-code rules of the pack)

async function showRule(id) {
    if (S.view === "rules") {
        if (id && id === rulesUi.current()) return;
        if (id && !(await rulesUi.leave())) return;
    } else if (!(await leaveCurrent())) return;
    S.current = null;
    S.preview = null;
    if (id) {
        try { await rulesUi.open(id); } catch (e) { toast(e.message, "bad"); return; }
    }
    S.view = "rules";
    setHash(id ? `#/r/${encodeURIComponent(S.pack)}/${encodeURIComponent(id)}` : "");
    renderAll();
}

function refreshRules(home = false) {
    if (home) {
        S.view = "home";
        setHash("");
        return renderAll();
    }
    renderSidebar();
    if (S.view === "rules") {
        renderCenter();
        renderRight();
    }
}

async function refreshEntities() {
    if (!S.pack) {
        S.entities = []; S.legacy = []; S.broken = []; setLocalRows({});
        return;
    }
    try {
        const r = await api.entities(S.pack);
        S.entities = r.entities;
        S.legacy = r.legacy;
        S.broken = r.broken;
        S.dirty = r.dirty;
        const byKind = {};
        for (const e of S.entities) (byKind[e.kind] ??= []).push({ kind: e.kind, name: e.id, en: e.name?.en, ru: e.name?.ru, icon: e.icon });
        setLocalRows(byKind);
    } catch (e) {
        if (e.status === 503) return; // the catalogue is loading
        toast(e.message, "bad");
    }
}

async function newPackDialog() {
    const name = await promptDialog(t("New pack"), {
        label: t("Pack name (folder in mods/)"), placeholder: "my-weapons", hint: t("a-z, 0-9 and -; e.g. pve-weapons"),
        transform: (v) => v.toLowerCase().replace(/[^a-z0-9-]/g, "-"),
        validate: (v) => (PACK_RE.test(v) ? (S.packs.some((p) => p.name === v) ? "This pack exists already" : "") : "1 to 40 of a-z, 0-9 and -"),
    });
    if (!name) return;
    try {
        await api.createPack(name, "1.0.0", "");
        await loadPacks();
        await openPack(name);
        toast(t("Pack {1} created", { 1: name }), "ok");
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function renamePackDialog() {
    const to = await promptDialog(t("Rename the pack {1}", { 1: S.pack }), {
        value: S.pack, transform: (v) => v.toLowerCase().replace(/[^a-z0-9-]/g, "-"), validate: (v) => (PACK_RE.test(v) ? "" : "1 to 40 of a-z, 0-9 and -"),
    });
    if (!to || to === S.pack) return;
    try {
        const r = await api.renamePack(S.pack, to);
        if (r.note) toast(r.note, "warn");
        await loadPacks();
        await openPack(to);
    } catch (e) {
        toast(e.message, "bad");
    }
}

function importDialog() {
    return modal({
        title: t("Import a pack"),
        width: "520px",
        body: (close) => {
            const file = h("input", { type: "file", accept: ".zip,.json,application/zip,application/json" });
            const name = h("input", { type: "text", placeholder: "new-pack-name" });
            const error = h("div", { class: "field-error" });
            file.addEventListener("change", () => {
                const f = file.files?.[0];
                if (f && !name.value) name.value = f.name.replace(/(\.studio)?\.(zip|json)$/i, "").toLowerCase().replace(/[^a-z0-9-]/g, "-").slice(0, 40);
            });
            const go = async () => {
                const f = file.files?.[0];
                if (!f) { error.textContent = t("Choose a file"); return; }
                if (!PACK_RE.test(name.value)) { error.textContent = t("1 to 40 of a-z, 0-9 and -"); return; }
                try {
                    const bytes = await f.arrayBuffer();
                    await api.importPack(name.value, bytes, /\.zip$/i.test(f.name));
                    close(true);
                    await loadPacks();
                    await openPack(name.value);
                    toast(t("Pack {1} imported", { 1: name.value }), "ok");
                } catch (e) {
                    error.textContent = e.message;
                }
            };
            return h("div", { class: "stack" }, h("p", { class: "muted" }, "A zip exported by the studio (the whole pack) or a .studio.json (the drafts only; Generate makes the patch files)."),
                file, h("label", { class: "label" }, "New pack name"), name, error,
                h("div", { class: "row end" }, h("button", { class: "btn", onClick: () => close() }, "Cancel"), h("button", { class: "btn primary", onClick: go }, "Import")));
        },
    });
}

// ---------------------------------------------------------------- entities

function suggestId(name) {
    const base = (name.length > 72 ? name.slice(0, 72) : name) + "_MOD";
    let id = base;
    for (let i = 2; S.entities.some((e) => e.id === id); i++) id = `${base}${i}`;
    return id;
}

/** "Make a copy": a new draft of the open pack from a catalogue entry. */
async function createFrom(kind, name) {
    if (!S.pack) {
        toast(t("Create or choose a pack first"), "warn");
        await newPackDialog();
        if (!S.pack) return;
    }
    if (!(await leaveCurrent())) return;
    const id = await promptDialog(name ? t("New {1} from {2}", { 1: t(kind), 2: name }) : t("New {1}", { 1: t(kind) }), {
        label: t("Unique name of the new {1}", { 1: t(kind) }), value: suggestId(name || "MY_LOOT"),
        hint: t("A-Z, 0-9 and _ (the game's key). Into the pack {1}.", { 1: S.pack }),
        transform: (v) => v.toUpperCase().replace(/[^A-Z0-9_]/g, "_"),
        validate: (v) => (!ID_RE.test(v) ? "3 to 80 of A-Z, 0-9 and _" : S.entities.some((e) => e.kind === kind && e.id === v) ? "This pack has it already" : ""),
    });
    if (!id) return;
    try {
        await api.newEntity(S.pack, kind, name, id);
        await refreshEntities();
        await openEntity(kind, id, { force: true });
        toast(t("{1} created from {2}", { 1: id, 2: name }), "ok");
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function newMenuPick(kind) {
    if (kind === "lootlist") return createFrom("lootlist", "");
    const picked = kind === "item" ? await pickItem({ title: t("New item: choose the item to copy") })
        : kind === "mob" ? await pickMob({ title: t("New mob variant: choose the official mob it is based on") })
            : await pickSpell({ title: t("New spell: choose the spell to copy") });
    if (picked) createFrom(kind, picked);
}

/** "New… → From a goal": the recipe cards (only: the groups to show, e.g. ["kit"]). */
async function openRecipes(only) {
    if (!S.pack) {
        toast(t("Create or choose a pack first"), "warn");
        await newPackDialog();
        if (!S.pack) return;
    }
    if (!(await leaveCurrent())) return;
    await recipeGallery({
        pack: S.pack, only,
        onCreated: async (created) => {
            await refreshEntities();
            renderSidebar();
            renderTop();
            if (created?.length) await openEntity(created[0].kind, created[0].id, { force: true });
        },
    });
}

/** Turns an element of the classic editor into a studio draft. */
async function adoptLegacy(l) {
    try {
        const r = await api.adopt(S.pack, l.kind, l.id);
        await refreshEntities();
        renderSidebar();
        if (r.notes?.length) toast(r.notes.join("; "), "warn");
        else toast(t("{1} is a studio draft now", { 1: l.id }), "ok");
        await openEntity(l.kind, l.id, { force: true });
    } catch (e) {
        toast(e.message, "bad");
    }
}

function isModified() {
    return Boolean(S.current && JSON.stringify(S.current.draft) !== S.current.savedText);
}

/** Before leaving the open entity: save / discard / stay. */
async function leaveCurrent() {
    if (S.view === "rules" && !(await rulesUi.leave())) return false;
    if (!isModified()) return true;
    const choice = await choiceDialog(t("Unsaved changes"), t("{1} has unsaved changes.", { 1: S.current.id }), [
        { key: "discard", label: t("Discard"), kind: "danger" }, { key: "save", label: t("Save"), kind: "primary" }]);
    if (choice === "save") return save();
    if (choice === "discard") {
        forget(S.pack, S.current.kind, S.current.id);
        return true;
    }
    return false;
}

async function openEntity(kind, id, { force = false } = {}) {
    if (!force && S.current && !(S.current.kind === kind && S.current.id === id) && !(await leaveCurrent())) return;
    let r;
    try {
        r = await api.entity(S.pack, kind, id);
    } catch (e) {
        toast(e.message, "bad");
        return;
    }
    const draft = r.draft;
    let template = null;
    try {
        template = draft.template ? await api.detail(kind, draft.template) : null;
    } catch { template = null; }
    S.current = { kind, id, draft, savedText: JSON.stringify(draft), diagnostics: r.diagnostics, template, undo: new UndoStack(), viewState: {}, tab: draft.goal ? "goal" : "generic", restore: null, ranges: {} };
    // The value helper of the generic editors: official ranges of the template's numbers (loaded on the side).
    if ((kind === "item" || kind === "spell") && draft.template) {
        const current = S.current;
        api.attrRanges(kind, draft.template).then((ranges) => { current.ranges = ranges; if (S.current === current) renderEditorBody(); }).catch(() => {});
    }
    const saved = recall(S.pack, kind, id);
    if (saved && JSON.stringify(saved.draft) !== S.current.savedText) S.current.restore = saved;
    S.view = "editor";
    S.preview = null;
    setHash(`#/e/${encodeURIComponent(S.pack)}/${encodeURIComponent(kind)}/${encodeURIComponent(id)}`);
    renderAll();
    refreshPreview();
}

async function save() {
    const c = S.current;
    if (!c) return false;
    try {
        const r = await api.saveEntity(S.pack, c.draft);
        c.draft = r.draft;
        c.savedText = JSON.stringify(r.draft);
        c.diagnostics = r.diagnostics;
        forget(S.pack, c.kind, c.id);
        await refreshEntities();
        renderSidebar();
        renderEditorHead();
        const blockers = r.diagnostics.filter((d) => d.severity === "blocker").length;
        toast(blockers ? t("Saved, with {1} blocker(s): Generate skips it until they are fixed", { 1: blockers }) : t("Saved"), blockers ? "warn" : "ok");
        return true;
    } catch (e) {
        toast(e.message, "bad");
        return false;
    }
}

const autosaveSoon = debounce(() => {
    const c = S.current;
    if (c && isModified()) autosave(S.pack, c.kind, c.id, c.draft, c.savedText);
    else if (c) forget(S.pack, c.kind, c.id);
}, 600);

/** The form's change entry point. */
function set(path, value, { structural = false } = {}) {
    const c = S.current;
    if (!c) return;
    c.undo.record(JSON.stringify(c.draft), structural ? null : path);
    setAt(c.draft, path, value);
    if (!structural) {
        const node = document.querySelector(`.editor-body [data-path="${CSS.escape(path)}"]`);
        if (node?.dataset.template !== undefined) node.classList.toggle("changed", value !== undefined && value !== null && String(value) !== node.dataset.template);
    }
    afterChange(structural);
}

function afterChange(structural) {
    autosaveSoon();
    refreshPreviewSoon();
    renderEditorHead();
    if (structural) renderEditorBody();
}

function undo() {
    discardTyping();
    const c = S.current;
    const prev = c?.undo.undo(JSON.stringify(c.draft));
    if (!prev) return;
    c.draft = JSON.parse(prev);
    afterChange(true);
}

function redo() {
    discardTyping();
    const c = S.current;
    const next = c?.undo.redo(JSON.stringify(c.draft));
    if (!next) return;
    c.draft = JSON.parse(next);
    afterChange(true);
}

async function renameEntity() {
    const c = S.current;
    if (!c) return;
    if (isModified() && !(await save())) return;
    const to = await promptDialog(t("Rename {1}", { 1: c.id }), {
        value: c.id, transform: (v) => v.toUpperCase().replace(/[^A-Z0-9_]/g, "_"), validate: (v) => (ID_RE.test(v) ? "" : "3 to 80 of A-Z, 0-9 and _"),
    });
    if (!to || to === c.id) return;
    try {
        const p = await api.renamePreview(S.pack, c.kind, c.id, to);
        const deps = p.dependents ?? [];
        const ok = await confirmDialog(t("Rename {1} → {2}", { 1: c.id, 2: to }), h("div", {},
            p.problems?.length ? h("div", { class: "problem blocker" }, p.problems.join("; ")) : null,
            deps.length ? h("div", {}, h("p", {}, t("{1} reference(s) in drafts:", { 1: deps.length })),
                h("ul", {}, ...deps.map((d) => plain("li", { class: "mono small" }, `${d.pack}: ${d.kind}/${d.id} ${d.path}`)))) : h("p", { class: "muted" }, "No other draft refers to it."),
            h("p", { class: "muted small" }, p.note ?? "")), t("Rename"));
        if (!ok || p.problems?.length) return;
        await api.renameEntity(S.pack, c.kind, c.id, to);
        forget(S.pack, c.kind, c.id);
        await refreshEntities();
        await openEntity(c.kind, to, { force: true });
        toast(t("Renamed to {1}", { 1: to }), "ok");
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function deleteEntity() {
    const c = S.current;
    if (!c) return;
    if (!(await confirmDialog(t("Delete {1}?", { 1: c.id }), t("The draft is deleted (its last version stays in the history). Generate takes its elements out of the patch files."), t("Delete"), "danger"))) return;
    try {
        await api.deleteEntity(S.pack, c.kind, c.id);
    } catch (e) {
        if (e.status !== 409) return toast(e.message, "bad");
        const deps = e.payload?.dependents ?? [];
        const force = await confirmDialog(t("{1} is used", { 1: c.id }), h("div", {}, h("p", {}, e.message),
            h("ul", {}, ...deps.map((d) => plain("li", { class: "mono small" }, `${d.pack}: ${d.kind}/${d.id} ${d.path}`)))), t("Delete anyway"), "danger");
        if (!force) return;
        try { await api.deleteEntity(S.pack, c.kind, c.id, true); } catch (e2) { return toast(e2.message, "bad"); }
    }
    forget(S.pack, c.kind, c.id);
    S.current = null;
    S.view = "home";
    setHash("");
    await refreshEntities();
    renderAll();
    toast(t("Deleted"), "ok");
}

async function duplicateEntity() {
    const c = S.current;
    if (!c) return;
    const id = await promptDialog(t("Duplicate {1}", { 1: c.id }), {
        value: suggestId(c.id), transform: (v) => v.toUpperCase().replace(/[^A-Z0-9_]/g, "_"),
        validate: (v) => (!ID_RE.test(v) ? "3 to 80 of A-Z, 0-9 and _" : S.entities.some((e) => e.kind === c.kind && e.id === v) ? "This pack has it already" : ""),
    });
    if (!id) return;
    const copy = clone(c.draft);
    copy.id = id;
    try {
        await api.saveEntity(S.pack, copy);
        await refreshEntities();
        await openEntity(c.kind, id);
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function historyDialog() {
    const c = S.current;
    if (!c) return;
    let versions;
    try { versions = (await api.history(S.pack, c.kind, c.id)).versions; } catch (e) { return toast(e.message, "bad"); }
    await modal({
        title: t("History of {1}", { 1: c.id }),
        width: "820px",
        body: (close) => {
            const view = h("pre", { class: "xml" }, t("Choose a version."));
            let chosen = null;
            const restoreBtn = h("button", { class: "btn primary", disabled: true, onClick: async () => {
                if (!chosen) return;
                try {
                    await api.restoreVersion(S.pack, c.kind, c.id, chosen);
                    close();
                    forget(S.pack, c.kind, c.id);
                    await openEntity(c.kind, c.id, { force: true });
                    toast(t("Restored the version of {1}", { 1: chosen }), "ok");
                } catch (e) { toast(e.message, "bad"); }
            } }, "Restore this version");
            const list = h("div", { class: "history-list" }, versions.length === 0 ? h("div", { class: "muted" }, "No earlier versions yet (each save keeps the one before; the last 20).") : null,
                ...versions.map((v) => h("button", { class: "btn ghost", onClick: async () => {
                    chosen = v.stamp;
                    restoreBtn.disabled = false;
                    try { view.textContent = JSON.stringify(await api.version(S.pack, c.kind, c.id, v.stamp), null, 2); } catch (e) { view.textContent = e.message; }
                } }, plain("span", { class: "mono" }, v.stamp.replace(/^(\d{4})(\d\d)(\d\d)-(\d\d)(\d\d)(\d\d)-\d+$/, "$1-$2-$3 $4:$5:$6 UTC")))));
            return h("div", { class: "history" }, list, h("div", { class: "grow" }, view, h("div", { class: "row end" }, restoreBtn)));
        },
    });
}

async function whereUsedDialog() {
    const c = S.current;
    if (!c) return;
    try {
        const r = await api.whereUsed(S.pack, c.kind, c.id);
        await modal({
            title: t("Where {1} is used", { 1: c.id }),
            width: "620px",
            body: () => r.dependents.length === 0 ? h("p", { class: "muted" }, "No draft refers to it.")
                : h("ul", {}, ...r.dependents.map((d) => h("li", {}, h("a", { href: `#/e/${encodeURIComponent(d.pack)}/${d.kind}/${d.id}` }, `${d.pack}: ${d.kind}/${d.id}`), plain("span", { class: "mono muted small" }, ` ${d.path}`)))),
        });
    } catch (e) {
        toast(e.message, "bad");
    }
}

// ---------------------------------------------------------------- preview

let previewSeq = 0;
async function refreshPreview() {
    const c = S.current;
    if (!c) return;
    const seq = ++previewSeq;
    S.previewLoading = true;
    renderRight();
    try {
        const p = await api.preview(S.pack, c.draft);
        if (seq !== previewSeq || S.current !== c) return;
        S.preview = p;
        c.diagnostics = p.diagnostics;
    } catch (e) {
        if (seq !== previewSeq) return;
        S.preview = { error: e.message, diagnostics: [] };
    }
    S.previewLoading = false;
    renderRight();
    showFieldProblems();
    renderEditorHead();
}
const refreshPreviewSoon = debounce(refreshPreview, 450);

function showFieldProblems() {
    const body = document.querySelector(".editor-body");
    if (body && S.current) applyDiagnostics(body, S.current.diagnostics ?? []);
}

/** A problem's one-click fix (d.fix: {path, value null → the template's}): on its draft, one undo step; Save keeps it. */
diagHooks.fix = async (d) => {
    if (d.entity?.id && (S.current?.id !== d.entity.id || S.current?.kind !== d.entity.kind)) await openEntity(d.entity.kind, d.entity.id);
    if (!S.current || !d.fix?.path) return;
    set(d.fix.path, d.fix.value ?? undefined, { structural: true });
    toast(t("Done: {1}. Save the draft, then Generate / Validate again.", { 1: nameOf(d.fix.label) }), "ok");
};

/** Opens the entity of a problem and shows its field. */
async function openProblem(d) {
    if (d.entity?.id && (S.current?.id !== d.entity.id || S.current?.kind !== d.entity.kind)) await openEntity(d.entity.kind, d.entity.id);
    if (!d.path) return;
    setTimeout(() => {
        let path = d.path;
        let node = null;
        while (path && !node) {
            node = document.querySelector(`.editor-body [data-path="${CSS.escape(path)}"]`);
            if (!node) path = path.slice(0, path.lastIndexOf("/"));
        }
        if (!node) return;
        for (let p = node.parentElement; p; p = p.parentElement) if (p.tagName === "DETAILS") p.open = true;
        node.scrollIntoView({ block: "center", behavior: "smooth" });
        node.classList.add("flash");
        setTimeout(() => node.classList.remove("flash"), 1600);
        node.querySelector("input, select, textarea, button")?.focus({ preventScroll: true });
    }, 120);
}

// ---------------------------------------------------------------- actions: generate, validate, install, restore, jobs

async function generate() {
    if (!S.pack) return;
    try {
        const r = await api.generate(S.pack);
        clearLookups();
        await refreshEntities();
        renderAll();
        const blockers = r.diagnostics.filter((d) => d.severity === "blocker");
        if (blockers.length) problemsDialog(r.diagnostics, t("Generate: {1} blocker(s), those drafts were skipped", { 1: blockers.length }));
        else toast(t("Generated: {1}", { 1: r.written.join(", ") || t("nothing to write") }), "ok");
        return r;
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function startJob(promise) {
    try {
        const job = await promise;
        S.jobs = [job, ...S.jobs.filter((j) => j.id !== job.id)];
        renderJobs();
        pollJobs();
    } catch (e) {
        if (e.payload?.diagnostics) problemsDialog(e.payload.diagnostics, e.message);
        else toast(e.message, "bad");
    }
}

async function validate() {
    if (!S.pack) return;
    if (isModified()) await save();
    startJob(api.validate(S.pack));
}

function installDialog() {
    return modal({
        title: t("Install into the game"),
        width: "560px",
        body: (close) => {
            const boxes = S.packs.map((p) => {
                const box = h("input", { type: "checkbox", checked: p.name === S.pack || p.installed });
                return [p.name, box, h("label", { class: "check" }, box, " ", plain("b", {}, p.name), p.installed ? h("span", { class: "chip" }, "installed") : null)];
            });
            const confirm = h("input", { type: "checkbox" });
            const go = h("button", { class: "btn danger", onClick: async () => {
                const packs = boxes.filter(([, b]) => b.checked).map(([n]) => n);
                if (!packs.length) return toast(t("Choose at least one pack"), "warn");
                if (!confirm.checked) return toast(t("Tick the confirmation"), "warn");
                close();
                if (isModified()) await save();
                startJob(api.install(packs));
            } }, "Install");
            return h("div", { class: "stack" },
                h("div", { class: "warnbox" }, h("b", {}, "Install builds the checked packs into game-local with the modkit"), " — ",
                    t("a build replaces the previous one, so check every pack you want. Afterwards restart the server and the game client: both read the game data only at their start. For drops add the packs to Mods:Packs.")),
                ...boxes.map(([, , label]) => label),
                h("label", { class: "check" }, confirm, " ", t("I will restart the server and the game client")),
                h("div", { class: "row end" }, h("button", { class: "btn", onClick: () => close() }, "Cancel"), go));
        },
    });
}

async function restoreDialog() {
    if (!(await confirmDialog(t("Restore the official files?"), t("game-local gets the official GameData back (hard links). Restart the server and the game client afterwards."), t("Restore"), "danger"))) return;
    startJob(api.restore());
}

let polling = false;
async function pollJobs() {
    if (polling) return;
    polling = true;
    try {
        for (;;) {
            let jobs;
            try { jobs = (await api.jobs()).jobs; } catch { break; }
            const before = new Map(S.jobs.map((j) => [j.id, j.done]));
            S.jobs = jobs;
            for (const j of jobs) {
                if (before.get(j.id) === false && j.done) {
                    toast(`${t(j.what)} ${j.packs.join(", ")}: ${j.ok ? t("done") : t("failed")}`, j.ok ? "ok" : "bad");
                    if (j.restartNeeded) S.restartNeeded = true;
                    if (!j.ok || j.diagnostics.some((d) => d.severity !== "info")) problemsDialog(j.diagnostics, `${t(j.what)}: ${j.ok ? t("done") : t("failed")}`, j);
                    if (j.what !== "validate") loadPacks().then(renderTop);
                }
            }
            renderJobs();
            if (!jobs.some((j) => !j.done)) break;
            await new Promise((r) => setTimeout(r, 1200));
        }
    } finally {
        polling = false;
    }
}

function problemsDialog(diags, title = t("Problems"), job = null) {
    return modal({
        title,
        width: "820px",
        body: (close) => h("div", { class: "stack" },
            job?.restartNeeded ? h("div", { class: "warnbox" }, h("b", {}, "Restart the server and the game client now."), " ", t("Both read the game data only when they start.")) : null,
            problemList(diags, (d) => { close(); openProblem(d); }),
            job?.notes?.length ? h("details", { open: !diags.some((d) => d.severity !== "info") }, h("summary", {}, t("Notes")),
                h("ul", { class: "notes" }, ...(job.notesText ?? job.notes.map((n) => ({ en: n, ru: n }))).map((n) => plain("li", {}, nameOf(n))))) : null,
            job?.output ? h("details", {}, h("summary", {}, t("Modkit output")), plain("pre", { class: "xml" }, job.output)) : null),
    });
}

function problemsCenter() {
    const current = S.current?.diagnostics ?? [];
    const lastJob = S.jobs.find((j) => j.done);
    const withProblems = S.entities.filter((e) => e.problems.blocker || e.problems.warning);
    return modal({
        title: t("Problems"),
        width: "860px",
        body: (close) => h("div", { class: "stack" },
            S.current ? h("div", {}, h("h4", {}, t("Open: {1}", { 1: S.current.id })), problemList(current, (d) => { close(); openProblem(d); })) : null,
            h("h4", {}, t("Drafts of {1}", { 1: S.pack || "—" })),
            withProblems.length === 0 ? h("div", { class: "ok-note" }, "✓ ", t("No draft has blockers or warnings")) :
                h("div", { class: "problems" }, ...withProblems.map((e) => h("div", { class: `problem ${e.problems.blocker ? "blocker" : "warning"} clickable`, onClick: () => { close(); openEntity(e.kind, e.id); } },
                    h("span", { class: "sev" }, e.problems.blocker ? "⛔" : "⚠"), plain("span", { class: "grow" }, `${e.kind}/${e.id} — ${nameOf(e.name) ?? ""}`),
                    h("span", { class: "muted small" }, t("{1} blocker(s), {2} warning(s)", { 1: e.problems.blocker, 2: e.problems.warning }))))),
            S.broken.length ? h("div", { class: "problem blocker" }, t("Broken draft files: {1}", { 1: S.broken.join(", ") })) : null,
            lastJob ? h("div", {}, h("h4", {}, t("Last modkit run: {1} {2}", { 1: t(lastJob.what), 2: lastJob.packs.join(", ") })), problemList(lastJob.diagnostics, (d) => { close(); openProblem(d); })) : null),
    });
}

// ---------------------------------------------------------------- keyboard

function onKey(e) {
    const mod = e.ctrlKey || e.metaKey;
    if (!mod || document.querySelector("#modal-root .modal-backdrop")) return;
    const inText = e.target instanceof HTMLElement && (e.target.matches("input[type=text], input[type=search], textarea") || e.target.isContentEditable);
    if (e.key === "s" || e.key === "S" || e.key === "ы" || e.key === "Ы") {
        e.preventDefault();
        if (S.view === "rules") rulesUi.save();
        else if (S.current) save();
    } else if (!inText && (e.key === "z" || e.key === "я") && !e.shiftKey) {
        e.preventDefault(); undo();
    } else if (!inText && ((e.key === "y" || e.key === "н") || ((e.key === "Z" || e.key === "z" || e.key === "Я") && e.shiftKey))) {
        e.preventDefault(); redo();
    }
}

// ---------------------------------------------------------------- rendering

function renderAll() {
    renderTop();
    renderSidebar();
    renderCenter();
    renderRight();
}

function renderTop() {
    const s = S.session;
    if (!s) return;
    const counts = { blocker: 0, warning: 0 };
    for (const e of S.entities) { counts.blocker += e.problems.blocker; counts.warning += e.problems.warning; }
    const packSelect = h("select", { class: "pack-select", "aria-label": "pack" },
        h("option", { value: "" }, S.packs.length ? t("— choose a pack —") : t("— no packs yet —")),
        ...S.packs.map((p) => h("option", { value: p.name, selected: p.name === S.pack }, `${p.name}${p.installed ? " ✓" : ""}`)));
    packSelect.addEventListener("change", async () => {
        if (!(await leaveCurrent())) { packSelect.value = S.pack; return; }
        S.current = null;
        S.view = "home";
        setHash("");
        await openPack(packSelect.value);
    });
    const packMenu = menuButton("⋯", [
        { label: t("New pack…"), onClick: newPackDialog },
        { label: t("Rename the pack…"), onClick: renamePackDialog, disabled: !S.pack },
        { separator: true },
        { label: t("Export as zip (whole pack)"), onClick: () => download(api.exportUrl(S.pack, "zip")), disabled: !S.pack },
        { label: t("Export as JSON (drafts)"), onClick: () => download(api.exportUrl(S.pack, "json")), disabled: !S.pack },
        { label: t("Import a pack…"), onClick: importDialog },
        { separator: true },
        { label: t("Patch files of the pack…"), onClick: patchFilesDialog, disabled: !S.pack },
        { label: t("Modkit status…"), onClick: statusDialog },
        { label: t("Classic editor (/admin/mods)"), onClick: () => { location.href = "/admin/mods"; } },
    ], { title: t("Pack actions"), className: "btn ghost" });
    const job = S.jobs.find((j) => !j.done);
    mount($("topbar"),
        h("div", { class: "brand" }, h("span", { class: "logo" }, "◆"), " Albion Studio"),
        h("div", { class: "group" }, packSelect, packMenu),
        h("div", { class: "group" },
            h("button", { class: `btn${S.dirty ? " attention" : ""}`, disabled: !S.pack, title: t("Compile the drafts into the pack's patch files (items.xml, …)"), onClick: generate }, S.dirty ? t("Generate •") : t("Generate")),
            h("button", { class: "btn", disabled: !S.pack || !s.official || Boolean(job), title: t("Generate, then the modkit's check (a build in memory, nothing installed)"), onClick: validate }, "Validate"),
            h("button", { class: "btn danger-outline", disabled: !s.official || Boolean(job), onClick: installDialog }, "Install…"),
            h("button", { class: "btn ghost", disabled: !s.official || Boolean(job), onClick: restoreDialog }, "Restore…")),
        h("button", { class: `btn problems-btn${counts.blocker ? " bad" : counts.warning ? " warn" : " good"}`, onClick: problemsCenter, title: t("Problems center") },
            counts.blocker ? `⛔ ${counts.blocker}` : "", counts.warning ? ` ⚠ ${counts.warning}` : "", !counts.blocker && !counts.warning ? "✓" : ""),
        h("div", { id: "jobs", class: "jobs" }),
        h("div", { class: "grow" }),
        h("button", { class: `btn ghost${S.view === "catalog" ? " active" : ""}`, onClick: () => showView("catalog") }, "Catalogue"),
        h("div", { class: "lang" }, ...["ru", "en"].map((l) => h("button", { class: `btn tiny${lang() === l ? " active" : ""}`, onClick: () => { setLang(l); document.documentElement.lang = l; renderAll(); } }, l.toUpperCase()))),
        h("a", { class: "btn ghost", href: "/admin/players", title: t("Web panel") }, plain("span", {}, s.user)));
    renderJobs();
    if (!s.official) $("topbar").title = t("No official GameData found next to Paths:ClientGameData: Validate, Install and Restore are off.");
}

function download(url) {
    const a = h("a", { href: url, download: "" });
    document.body.append(a);
    a.click();
    a.remove();
}

function renderJobs() {
    const box = $("jobs");
    if (!box) return;
    const job = S.jobs[0];
    const parts = [];
    if (job && (!job.done || Date.now() - Date.parse(job.finished ?? 0) < 120000)) {
        parts.push(h("button", { class: `job ${job.done ? (job.ok ? "ok" : "bad") : "running"}`, title: job.stage, onClick: () => problemsDialog(job.diagnostics, `${t(job.what)}: ${t(job.stage)}`, job) },
            h("span", {}, `${t(job.what)} ${job.packs.join(", ")}`), h("span", { class: "bar" }, h("span", { style: { width: `${job.progress}%` } })),
            h("span", { class: "small" }, job.done ? (job.ok ? "✓" : "✕") : `${job.progress}%`)));
    }
    if (S.restartNeeded) parts.push(h("span", { class: "restart", title: t("Both read the game data only when they start.") }, "⟳ ", t("Restart the server and the client")));
    mount(box, ...parts);
}

function renderSidebar() {
    const side = $("sidebar");
    if (!S.pack) {
        mount(side, h("div", { class: "side-empty" }, h("p", {}, S.packs.length ? "Choose a pack above, or create one." : "No packs yet."),
            h("button", { class: "btn primary", onClick: newPackDialog }, "New pack…"), h("button", { class: "btn", onClick: importDialog }, "Import…")));
        return;
    }
    const pack = S.packs.find((p) => p.name === S.pack);
    const auto = new Set(pending(S.pack).map((p) => `${p.kind}/${p.id}`));
    const filter = h("input", { type: "search", placeholder: "Filter", value: S.filter, "data-focus-key": "side-filter" });
    filter.addEventListener("input", () => { S.filter = filter.value; renderList(); });
    const listBox = h("div", { class: "entity-list", "data-scroll-key": "entities" });
    const renderList = () => {
        const needle = S.filter.trim().toLowerCase();
        const groups = [["item", t("Items")], ["spell", t("Spells")], ["mob", t("Mob variants")], ["lootlist", t("Loot lists")]];
        const known = new Set(groups.map((g) => g[0]));
        for (const e of S.entities) if (!known.has(e.kind)) { groups.push([e.kind, e.kind]); known.add(e.kind); }
        listBox.replaceChildren(...groups.map(([kind, label]) => {
            const rows = S.entities.filter((e) => e.kind === kind && (!needle || `${e.id} ${e.name?.en ?? ""} ${e.name?.ru ?? ""}`.toLowerCase().includes(needle)));
            return h("div", { class: "entity-group" },
                h("div", { class: "group-title" }, label, h("span", { class: "muted" }, ` ${rows.length}`),
                    h("button", { class: "btn tiny ghost", title: t("New {1} from the catalogue", { 1: t(kind) }), onClick: () => newMenuPick(kind) }, "+")),
                ...rows.map((e) => h("a", {
                    class: `entity${S.current?.kind === e.kind && S.current?.id === e.id ? " active" : ""}`,
                    href: `#/e/${encodeURIComponent(S.pack)}/${e.kind}/${e.id}`,
                    onClick: (ev) => { ev.preventDefault(); openEntity(e.kind, e.id); },
                }, icon(e.icon, 28, { item: "I", spell: "S", mob: "M", lootlist: "L" }[kind] ?? "?"),
                h("span", { class: "grow" }, plain("span", { class: "ename" }, nameOf(e.name) || e.id), plain("span", { class: "eid mono" }, e.id)),
                auto.has(`${e.kind}/${e.id}`) ? h("span", { class: "dot unsaved", title: t("unsaved changes (autosaved in this browser)") }) : null,
                e.problems.blocker ? h("span", { class: "dot bad", title: t("{1} blocker(s)", { 1: e.problems.blocker }) }) : e.problems.warning ? h("span", { class: "dot warn", title: t("{1} warning(s)", { 1: e.problems.warning }) }) : null)));
        }), rulesUi.sidebar(), S.legacy.length ? h("details", { class: "entity-group legacy" }, h("summary", { class: "group-title" }, t("Other new elements ({1})", { 1: S.legacy.length })),
            h("div", { class: "hint" }, "Made by the classic editor or by hand: edit them there (/admin/mods), or make a studio copy from the catalogue."),
            ...S.legacy.map((l) => h("div", { class: "entity legacy" }, plain("span", { class: "mono small grow", onClick: () => showDetail(l.kind, l.id) }, `${l.kind}/${l.id}`),
                l.template ? h("button", { class: "btn tiny", title: t("Make it a studio draft (template {1})", { 1: l.template }), onClick: () => adoptLegacy(l) }, "Import") : null))) : "");
    };
    renderList();
    remount(side,
        h("div", { class: "pack-head" },
            h("div", { class: "row between" }, plain("b", {}, S.pack), pack?.version ? plain("span", { class: "muted small" }, `v${pack.version}`) : null),
            pack?.description ? plain("div", { class: "muted small" }, pack.description) : null,
            h("div", { class: "small" }, S.dirty ? h("span", { class: "warn-text" }, t("drafts changed since the last Generate")) : pack?.generated ? h("span", { class: "muted" }, t("generated")) : h("span", { class: "muted" }, t("not generated yet")))),
        h("div", { class: "row" }, menuButton(t("+ New…"), [
            { label: t("From a goal (recipes)…"), onClick: () => openRecipes() },
            { label: t("Kit (several linked entities)…"), onClick: () => openRecipes(["kit", "line"]) },
            { separator: true },
            { label: t("Item — copy one from the catalogue…"), onClick: () => newMenuPick("item") },
            { label: t("Spell — copy one from the catalogue…"), onClick: () => newMenuPick("spell") },
            { label: t("Mob variant (server-side)…"), onClick: () => newMenuPick("mob") },
            { label: t("Loot list (server-side)…"), onClick: () => newMenuPick("lootlist") },
            { label: lang() === "ru" ? "Правило — из шаблона…" : "Rule — from a template…", onClick: () => rulesUi.newRule() },
            { separator: true },
            { label: t("Browse the catalogue…"), onClick: () => showView("catalog") },
        ], { className: "btn primary" }), filter),
        listBox);
}

function renderCenter() {
    const center = $("center");
    if (S.view === "catalog") return mount(center, catalogView());
    if (S.view === "rules") return rulesUi.renderCenter(center);
    if (S.view === "editor" && S.current) return renderEditor();
    mount(center, home());
}

function home() {
    const s = S.session;
    return h("div", { class: "home" },
        h("h1", {}, "Albion Studio"),
        h("p", { class: "lead" }, "Make new items and abilities from the game's own: pick an existing one, make a copy, change it visually, see the tooltip live, then install."),
        h("div", { class: "cards" },
            h("div", { class: "card action goal", onClick: () => openRecipes() }, h("div", { class: "big" }, "🎯"), h("b", {}, "From a goal"),
                h("p", { class: "muted small" }, "Pick what you want — a two-handed weapon, an area spell, a mount, an item line T4–T8 — and fill only the numbers that matter.")),
            h("div", { class: "card action goal", onClick: () => openRecipes(["kit", "line"]) }, h("div", { class: "big" }, "👑"), h("b", {}, "Kits"),
                h("p", { class: "muted small" }, "One click: a legendary boss with its unique drop, an item line, a consumable set, an elite camp.")),
            h("div", { class: "card action", onClick: () => newMenuPick("item") }, h("div", { class: "big" }, "⚔"), h("b", {}, "New item"), h("p", { class: "muted small" }, "Copy a weapon, armour, potion… and change its stats, recipe, spells and drops.")),
            h("div", { class: "card action", onClick: () => newMenuPick("spell") }, h("div", { class: "big" }, "✦"), h("b", {}, "New ability"), h("p", { class: "muted small" }, "Copy a spell and change its cooldown, damage, effects; then offer it in an item's slots.")),
            h("div", { class: "card action", onClick: () => showView("catalog") }, h("div", { class: "big" }, "🔍"), h("b", {}, "Catalogue"), h("p", { class: "muted small" }, "Browse items, spells, mobs, loot lists and stations with icons."))),
        h("ol", { class: "steps" },
            h("li", {}, "Choose or create a pack (top left)."),
            h("li", {}, "New… → pick what to copy → give it a unique name."),
            h("li", {}, "Edit: pickers instead of typed names; problems show next to the fields."),
            h("li", {}, "Generate writes the pack's patch files; Validate runs the modkit's check."),
            h("li", {}, "Install… builds into game-local; restart the server and the game client.")),
        h("p", { class: "muted small" }, t("Catalogue: {1} items, {2} spells, {3} mobs · client {4} · packs in {5}", {
            1: s.catalog.items ?? "…", 2: s.catalog.spells ?? "…", 3: s.catalog.mobs ?? "…", 4: s.clientBuild ?? "?", 5: s.modsDir })),
        s.icons ? null : h("p", { class: "warnbox" }, "No icon cache: run dotnet run --project tools/AlbionLocal.ModKit -- icons once for the icons."),
        s.modsProblem ? h("p", { class: "problem blocker" }, s.modsProblem) : null);
}

function editorCtx() {
    const c = S.current;
    return {
        draft: c.draft, set, rerender: renderEditorBody, template: c.template, pack: S.pack, session: S.session, facets: S.facets,
        viewState: c.viewState, openEntity, showDetail, rename: renameEntity, ranges: c.ranges,
        // The goal tab rebuilds the whole draft: one undo step.
        replace: (next) => { c.undo.record(JSON.stringify(c.draft), null); c.draft = next; afterChange(true); },
    };
}

function renderEditor() {
    const c = S.current;
    const center = $("center");
    mount(center,
        h("div", { class: "editor" },
            h("div", { id: "editor-head", class: "editor-head" }),
            c.restore ? h("div", { class: "restore-banner" }, t("This browser kept unsaved changes of {1} ({2}).", { 1: c.id, 2: new Date(c.restore.at).toLocaleString() }),
                h("button", { class: "btn small primary", onClick: () => { c.undo.record(JSON.stringify(c.draft), null); c.draft = c.restore.draft; c.restore = null; renderEditor(); refreshPreview(); } }, "Restore them"),
                h("button", { class: "btn small", onClick: () => { forget(S.pack, c.kind, c.id); c.restore = null; renderEditor(); } }, "Discard")) : null,
            h("div", { id: "editor-tabs", class: "tabs" }),
            h("div", { id: "editor-body", class: "editor-body", "data-scroll-key": "editor" })));
    renderEditorHead();
    renderEditorBody();
}

function renderEditorHead() {
    const c = S.current;
    const head = $("editor-head");
    if (!c || !head) return;
    const editor = getEditor(c.kind);
    const diags = c.diagnostics ?? [];
    const blockers = diags.filter((d) => d.severity === "blocker").length;
    const warnings = diags.filter((d) => d.severity === "warning").length;
    const modified = isModified();
    mount(head,
        icon(S.preview?.tooltip?.icon ?? S.entities.find((e) => e.kind === c.kind && e.id === c.id)?.icon, 48, editor?.letter ?? "?"),
        h("div", { class: "grow" },
            h("div", { class: "title" }, plain("span", {}, nameOf(c.draft.name) || c.id), modified ? h("span", { class: "chip changed" }, "unsaved") : null),
            h("div", { class: "sub" }, h("span", { class: "chip" }, t(c.kind)), plain("span", { class: "mono" }, c.id), " ", t("from"), " ", plain("span", { class: "mono" }, c.draft.template ?? "—"),
                blockers ? h("span", { class: "chip bad" }, `⛔ ${blockers}`) : null, warnings ? h("span", { class: "chip warn" }, `⚠ ${warnings}`) : null)),
        h("div", { class: "row" },
            h("button", { class: "btn ghost", disabled: !c.undo.canUndo, title: t("Undo (Ctrl+Z)"), onClick: undo }, "↶"),
            h("button", { class: "btn ghost", disabled: !c.undo.canRedo, title: t("Redo (Ctrl+Y)"), onClick: redo }, "↷"),
            h("button", { class: `btn primary${modified ? " attention" : ""}`, title: t("Save the draft (Ctrl+S)"), onClick: save }, "Save"),
            menuButton("⋯", [
                { label: t("History…"), onClick: historyDialog },
                { label: t("Where used…"), onClick: whereUsedDialog },
                { label: t("Rename…"), onClick: renameEntity },
                { label: t("Duplicate…"), onClick: duplicateEntity },
                { label: t("Template in the catalogue"), onClick: () => showDetail(c.kind, c.draft.template) },
                { separator: true },
                { label: t("Delete…"), onClick: deleteEntity, danger: true },
            ], { className: "btn ghost", title: t("More") })));
}

function renderEditorBody() {
    const c = S.current;
    const body = $("editor-body");
    const tabsBox = $("editor-tabs");
    if (!c || !body) return;
    const editor = getEditor(c.kind);
    const recipes = recipesFor(c.kind, c.draft, c.template);
    if (c.tab !== "generic" && !recipes.some((r) => r.id === c.tab)) c.tab = recipes[0]?.id ?? "generic";
    mount(tabsBox, ...recipes.map((r) => h("button", { class: `tab${c.tab === r.id ? " active" : ""}`, onClick: () => { c.tab = r.id; renderEditorBody(); } }, r.label)),
        h("button", { class: `tab${c.tab === "generic" ? " active" : ""}`, onClick: () => { c.tab = "generic"; renderEditorBody(); } }, t("All fields")));
    tabsBox.hidden = recipes.length === 0;
    const ctx = editorCtx();
    let content;
    try {
        const recipe = recipes.find((r) => r.id === c.tab);
        content = recipe ? recipe.render(ctx) : editor ? editor.render(ctx) : h("div", { class: "bad" }, `no editor for ${c.kind}`);
    } catch (e) {
        console.error(e);
        content = h("div", { class: "problem blocker" }, `${t("The editor failed")}: ${e.message}`);
    }
    if (!c.template) content = h("div", {}, h("div", { class: "problem warning" }, t("The template {1} is not in the catalogue: only the texts can be edited.", { 1: c.draft.template ?? "?" })), content);
    remount(body, content);
    showFieldProblems();
}

function renderRight() {
    const right = $("right");
    if (S.view === "rules") return rulesUi.renderRight(right);
    if (S.view !== "editor" || !S.current) {
        const pack = S.packs.find((p) => p.name === S.pack);
        mount(right, h("div", { class: "preview" }, h("div", { class: "tabs" }, h("span", { class: "tab active" }, "Pack")),
            h("div", { class: "preview-body" },
                pack ? h("div", { class: "stack" },
                    h("div", {}, t("Drafts: {1}", { 1: Object.entries(pack.drafts).map(([k, n]) => `${n} ${t(k)}`).join(", ") })),
                    h("div", {}, pack.installed ? t("Installed in game-local") : t("Not installed")),
                    h("div", {}, h("a", { href: api.exportUrl(S.pack, "zip"), download: "" }, t("Download the pack (zip)")))) : h("div", { class: "muted" }, "No pack open."))));
        return;
    }
    remount(right, previewPane(S.preview, { tab: S.previewTab, setTab: (tab) => { S.previewTab = tab; renderRight(); }, onOpenProblem: openProblem, loading: S.previewLoading }));
}

async function patchFilesDialog() {
    try {
        const r = await api.files(S.pack);
        await modal({
            title: t("Patch files of {1}", { 1: S.pack }),
            width: "900px",
            body: () => Object.keys(r.files).length === 0 ? h("p", { class: "muted" }, "No patch files yet: Generate writes them.")
                : h("div", {}, ...Object.entries(r.files).map(([f, text]) => h("details", {}, h("summary", { class: "mono" }, f), plain("pre", { class: "xml" }, text)))),
        });
    } catch (e) {
        toast(e.message, "bad");
    }
}

async function statusDialog() {
    try {
        const r = await api.status();
        await modal({ title: t("Modkit status"), width: "820px", body: () => plain("pre", { class: "xml" }, r.text) });
    } catch (e) {
        toast(e.message, "bad");
    }
}

start();
export { diagText };
