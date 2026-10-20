// The Studio's JSON API (/studio/api/*). Writes carry the session's CSRF token in X-CSRF-Token.

let csrf = "";

export function setCsrf(token) {
    csrf = token ?? "";
}

export class ApiError extends Error {
    constructor(message, status, payload) {
        super(message);
        this.status = status;
        this.payload = payload;
    }
}

async function request(method, path, body, { raw = false, contentType } = {}) {
    const headers = {};
    if (method !== "GET") headers["X-CSRF-Token"] = csrf;
    let payloadBody;
    if (body !== undefined) {
        if (raw) {
            payloadBody = body;
            headers["Content-Type"] = contentType ?? "application/octet-stream";
        } else {
            payloadBody = JSON.stringify(body);
            headers["Content-Type"] = "application/json";
        }
    }
    const response = await fetch("/studio/api/" + path, { method, headers, body: payloadBody, credentials: "same-origin", cache: "no-store" });
    const text = await response.text();
    let payload;
    try {
        payload = text ? JSON.parse(text) : {};
    } catch {
        throw new ApiError(`unexpected answer (${response.status}): ${text.slice(0, 200)}`, response.status, null);
    }
    if (!response.ok) throw new ApiError(payload.error ?? `request failed (${response.status})`, response.status, payload);
    return payload;
}

/** A raw API call (method, path under /studio/api/, body): the editors that add their own endpoints (editors/rules.mjs). */
export const call = (method, path, body) => request(method, path, body);

const q = (params) => {
    const s = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") s.set(k, String(v));
    const text = s.toString();
    return text ? "?" + text : "";
};
const P = (pack) => `packs/${encodeURIComponent(pack)}`;

export const api = {
    session: () => request("GET", "session"),
    packs: () => request("GET", "packs"),
    jobs: () => request("GET", "jobs"),
    status: () => request("GET", "status"),
    facets: (kind) => request("GET", `facets/${kind}`),
    /** filters: {tier: "6", type: "weapon"} → f.tier=6&f.type=weapon */
    search: (kind, query, filters = {}, offset = 0, limit = 40) =>
        request("GET", `search/${kind}` + q({ q: query, offset, limit, ...Object.fromEntries(Object.entries(filters).map(([k, v]) => ["f." + k, v])) })),
    /** forItem: only the icons official items use (an item's uisprite). */
    icons: (query, atlas = "", offset = 0, limit = 120, forItem = false) => request("GET", "icons" + q({ q: query, atlas, offset, limit, for: forItem ? "item" : "" })),
    /** The character models official equipment uses (an item's mesh), by slot (cape, armor, mainhand…). */
    models: (query, slot = "", offset = 0, limit = 60) => request("GET", "models" + q({ q: query, slot, offset, limit })),
    detail: (kind, name) => request("GET", `detail/${kind}` + q({ name })),
    lookup: (refs) => request("GET", "lookup" + q({ refs: refs.join(",") })),
    createPack: (name, version, description) => request("POST", "packs/create", { name, version, description }),
    importPack: (name, bytes, isZip) => request("POST", "packs/import" + q({ name }), bytes, { raw: true, contentType: isZip ? "application/zip" : "application/json" }),
    renamePack: (pack, to) => request("POST", `${P(pack)}/rename`, { to }),
    exportUrl: (pack, format) => `/studio/api/${P(pack)}/export?format=${format}`,
    files: (pack) => request("GET", `${P(pack)}/files`),
    entities: (pack) => request("GET", `${P(pack)}/entities`),
    entity: (pack, kind, id) => request("GET", `${P(pack)}/entity` + q({ kind, id })),
    newEntity: (pack, kind, from, id) => request("POST", `${P(pack)}/entity/new`, { kind, from, id }),
    saveEntity: (pack, draft) => request("POST", `${P(pack)}/entity/save`, { draft }),
    deleteEntity: (pack, kind, id, force = false) => request("POST", `${P(pack)}/entity/delete`, { kind, id, force }),
    renamePreview: (pack, kind, id, to) => request("POST", `${P(pack)}/entity/rename-preview`, { kind, id, to }),
    renameEntity: (pack, kind, id, to) => request("POST", `${P(pack)}/entity/rename`, { kind, id, to }),
    history: (pack, kind, id) => request("GET", `${P(pack)}/history` + q({ kind, id })),
    version: (pack, kind, id, stamp) => request("GET", `${P(pack)}/version` + q({ kind, id, stamp })),
    restoreVersion: (pack, kind, id, stamp) => request("POST", `${P(pack)}/entity/restore`, { kind, id, stamp }),
    whereUsed: (pack, kind, id) => request("GET", `${P(pack)}/where-used` + q({ kind, id })),
    generate: (pack) => request("POST", `${P(pack)}/generate`, {}),
    preview: (pack, draft) => request("POST", "preview", { pack, draft }),
    // Recipes (phase 3): the goal list, a build without saving (drafts, problems, ranges, tooltip), create, the value helper, adoption.
    recipes: () => request("GET", "recipes"),
    buildRecipe: (pack, recipe, values) => request("POST", "recipes/build", { pack, recipe, values }),
    createFromRecipe: (pack, recipe, values, overwrite = false) => request("POST", `${P(pack)}/recipe/create`, { recipe, values, overwrite }),
    attrRanges: (kind, template) => request("GET", `ranges/${kind}` + q({ template })),
    adopt: (pack, kind, id) => request("POST", `${P(pack)}/adopt`, { kind, id }),
    validate: (pack) => request("POST", "actions/validate", { pack }),
    install: (packs) => request("POST", "actions/install", { packs, confirm: true }),
    restore: () => request("POST", "actions/restore", { confirm: true }),
};
