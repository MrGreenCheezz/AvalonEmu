using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// StageQ camps: a rolled dyn slot of a map whose template has VAR layers (analysis/work/stageQ/camps/CLIENT.md). Its "create" is DynamicTemplate 604
/// {0 slot id, 1 DynamicTemplateState, 2 DynamicTemplateFeatureLayer} (client anh; handler ci7.a9 0x180977970 → bak.h 0x180558FA0): the client starts
/// every slot at NotInitialized, so it draws none of the slot's *_DYN_VAR_* layers before it, and on it switches to the (feature layer, state) layers
/// and that feature layer's STATIC one. Captured (0204 slot 21, five times in 3 min) whenever the slot came into the player's view, nothing when it
/// left: so a window object that is never left (<see cref="LeaveOnRange"/> false) and sent again on re-entry, which the client ignores while nothing
/// changed. Key 3 (a GameTimeStamp: a countdown map marker, c5z, for templates with activetemplatemapmarker; no camp has one) stays out.
/// <see cref="State"/> moves with the camp (<see cref="Systems.FactionCamps"/>); the object id is the server's own, the client never sees it.
/// </summary>
public sealed class DynamicTemplateObject(long id, DynamicSlotSpawn spawn) : WorldObject(id, spawn)
{
    public new DynamicSlotSpawn Spawn { get; } = spawn;

    /// <summary>The template instance id (instanceslot_21): 604 key 0.</summary>
    public string Slot => Spawn.Roll.Slot;

    /// <summary>DynamicTemplateFeatureLayer: Default 1, FactionWarfare 2 (the slot's roll, fixed while the map lives).</summary>
    public byte FeatureLayer => Spawn.Roll.FeatureLayer;

    /// <summary>DynamicTemplateState: Active 1 while its camp stands, Inactive 2 while a cleared faction camp waits.</summary>
    public byte State { get; internal set; } = DynamicRoll.Active;

    public override string Kind => "dynamic template";
    public override string TypeName => Spawn.Roll.Template;
    public override bool LeaveOnRange => false;

    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return Build();
    }

    /// <summary>DynamicTemplate 604 as it stands now.</summary>
    public DynamicTemplateEvent Build() => new() { b4g = Slot, b4h = State, b4i = FeatureLayer };
}
