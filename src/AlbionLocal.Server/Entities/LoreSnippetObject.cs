using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// A lore snippet in the world (analysis/work/stageR/lore/SPEC.md §2): a template's criticalscenemember type="loresnippet" (tile LORE_SPOT,
/// component &lt;loresnippet loresnippetid="KEEPER_18"/&gt;). NewLoreSnippetObject (673, a2h; cjb handler 0x180AAE990 → ddr ctor 0x180ACFA50):
/// key 0 id, 1 position, 2 direction, 3 the tile name (a9f.u looks the scene tile up: "LoreSnippetObject: Tile not found" without), 4 the
/// lore.xml index (bsi.abo; "Unable to find lore snippet for" without). The client's object starts claimed (ddr+0x68 = 1), so the viewer's own
/// state follows at once as LoreSnippetObjectStateUpdate (674, a2f {0 id, 1 claimed}; ddr.ajv) [infer: nothing else ever clears it]. The state is
/// per viewer (the claims are per character). Landscape-bound like the shrines: never left, sent again on re-entry.
/// </summary>
public sealed class LoreSnippetObject(long id, ClusterObject spawn, LoreData.Snippet snippet) : WorldObject(id, spawn)
{
    public LoreData.Snippet Snippet { get; } = snippet;

    public override string Kind => "lore snippet";
    public override string TypeName => Snippet.Name;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewLoreSnippetObjectEvent
        {
            d8h = Id,              // [0]
            d8i = Position,        // [1]
            d8j = Direction,       // [2]
            d8k = Spawn.Tile,      // [3] the scene tile (LORE_SPOT)
            d8l = Snippet.Index,   // [4] lore.xml index
            Extra = new() { [4] = Snippet.Index }, // index 0 (TEST_LORE_1) goes out too
        };
        yield return BuildState(Systems.LoreSnippets.IsClaimed(viewer.Character, Snippet.Name));
    }

    /// <summary>LoreSnippetObjectStateUpdate (674): {0 id, 1 claimed by the viewer}; false goes out too.</summary>
    public LoreSnippetObjectStateUpdateEvent BuildState(bool claimed) => new() { ddn = Id, d8g = claimed, Extra = new() { [1] = claimed } };
}
