using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>An authored tutorial obstacle, private to the character's map.</summary>
public sealed class TutorialBlocker(long id, TutorialBlockerSpawn spawn) : WorldObject(id, spawn)
{
    public string BlockerId => spawn.BlockerId;
    public bool Blocked { get; set; } = (string?)spawn.Component.Attribute("blockeddefault") == "true";
    public override string Kind => "tutorial blocker";
    public override string TypeName => spawn.Tile;
    public override bool LeaveOnRange => true;
    public NewTutorialBlockerEvent State() => new()
    {
        dcm = Id, dcn = spawn.Tile, dco = Position, dcp = spawn.Direction, dcq = Blocked,
    };
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return State();
    }
}
