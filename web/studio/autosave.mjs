// Autosave of unsaved edits in localStorage (per pack/kind/id) with a restore banner, and the undo/redo stack of the open form.
// localStorage can be missing or throw (private window, blocked storage): every access is guarded and the studio works without it.

const PREFIX = "studio.autosave.";
const MAX_ENTRIES = 50;

const key = (pack, kind, id) => `${PREFIX}${pack}/${kind}/${id}`;

function storage() {
    try {
        return window.localStorage;
    } catch {
        return null;
    }
}

/** Remembers the unsaved draft (and the saved one it started from, to tell whether the server's changed meanwhile). */
export function autosave(pack, kind, id, draft, basedOn) {
    const s = storage();
    if (!s) return;
    try {
        s.setItem(key(pack, kind, id), JSON.stringify({ at: Date.now(), draft, basedOn }));
        prune(s);
    } catch { /* full or blocked: no autosave */ }
}

/** The autosaved draft of an entity, or null. */
export function recall(pack, kind, id) {
    const s = storage();
    if (!s) return null;
    try {
        const text = s.getItem(key(pack, kind, id));
        return text ? JSON.parse(text) : null;
    } catch {
        return null;
    }
}

export function forget(pack, kind, id) {
    try { storage()?.removeItem(key(pack, kind, id)); } catch { /* ignore */ }
}

/** Every autosaved entry of a pack: [{kind, id, at}] (the sidebar marks them). */
export function pending(pack) {
    const s = storage();
    if (!s) return [];
    const list = [];
    try {
        for (let i = 0; i < s.length; i++) {
            const k = s.key(i);
            if (!k?.startsWith(`${PREFIX}${pack}/`)) continue;
            const [, kind, id] = k.slice(PREFIX.length).split("/");
            const entry = JSON.parse(s.getItem(k) ?? "null");
            list.push({ kind, id, at: entry?.at ?? 0 });
        }
    } catch { /* ignore */ }
    return list;
}

function prune(s) {
    const entries = [];
    for (let i = 0; i < s.length; i++) {
        const k = s.key(i);
        if (k?.startsWith(PREFIX)) {
            try { entries.push([k, JSON.parse(s.getItem(k) ?? "{}").at ?? 0]); } catch { entries.push([k, 0]); }
        }
    }
    entries.sort((a, b) => b[1] - a[1]);
    for (const [k] of entries.slice(MAX_ENTRIES)) s.removeItem(k);
}

/** Undo/redo of one form: snapshots (JSON text) of the draft; consecutive typing in one field is merged into one step. */
export class UndoStack {
    constructor(limit = 200) {
        this.limit = limit;
        this.past = [];
        this.future = [];
        this.lastKey = null;
        this.lastAt = 0;
    }

    reset() {
        this.past = [];
        this.future = [];
        this.lastKey = null;
    }

    /** Before a change: remembers the state; changes of the same field within 1.2 s are one step. */
    record(snapshot, mergeKey) {
        const now = Date.now();
        if (mergeKey && mergeKey === this.lastKey && now - this.lastAt < 1200) {
            this.lastAt = now;
            return;
        }
        this.past.push(snapshot);
        if (this.past.length > this.limit) this.past.shift();
        this.future = [];
        this.lastKey = mergeKey;
        this.lastAt = now;
    }

    undo(current) {
        if (!this.past.length) return null;
        this.future.push(current);
        this.lastKey = null;
        return this.past.pop();
    }

    redo(current) {
        if (!this.future.length) return null;
        this.past.push(current);
        this.lastKey = null;
        return this.future.pop();
    }

    get canUndo() { return this.past.length > 0; }
    get canRedo() { return this.future.length > 0; }
}
