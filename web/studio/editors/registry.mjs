// The editor registry: the front-end extension point of the Studio (SPEC §6).
//
// A kind ("item", "spell", later "mob", "lootlist", …) registers its generic editor once:
//     registerEditor("item", { label, letter, render(ctx) })
// and any number of "recipes": focused visual editors for one kind of entity (a weapon, a potion, a dash ability…):
//     registerRecipe("item", { id: "weapon", label: "Weapon", order: 10,
//                              match: (draft, template) => template?.type === "weapon",   // offered for this draft?
//                              render: (ctx) => Node })                                   // the editor body
// The editor view shows the matching recipes as tabs before the generic "All attributes" tab (the fallback that always works).
//
// ctx (what render gets):
//   draft            the draft object (edit it only through ctx.set)
//   set(path, value, {structural})  change a field by JSON pointer; structural: true re-renders the form (rows added/removed)
//   rerender()       render the form again
//   template         the catalogue detail of the template (GET /studio/api/detail/<kind>?name=…): attributes, spells, recipes, blocks…
//   pack, session    the open pack's name, the session (csrf, catalogue state)
//   facets           facet values of the kind (filter options), when loaded
//   openEntity(kind, id), showDetail(kind, name)   navigation helpers
// Fields: build them with forms.mjs renderField(descriptor, ctx) so pickers, inline problems and undo work the same everywhere.

const editors = new Map();
const recipes = new Map();

export function registerEditor(kind, editor) {
    editors.set(kind, { kind, ...editor });
}

export function registerRecipe(kind, recipe) {
    if (!recipes.has(kind)) recipes.set(kind, []);
    const list = recipes.get(kind).filter((r) => r.id !== recipe.id);
    list.push({ order: 100, ...recipe });
    list.sort((a, b) => a.order - b.order);
    recipes.set(kind, list);
}

export function getEditor(kind) {
    return editors.get(kind) ?? null;
}

/** The recipes offered for a draft (their match() says yes), in order. */
export function recipesFor(kind, draft, template) {
    return (recipes.get(kind) ?? []).filter((r) => {
        try {
            return r.match ? r.match(draft, template) : true;
        } catch {
            return false;
        }
    });
}

export function editorKinds() {
    return [...editors.keys()];
}
