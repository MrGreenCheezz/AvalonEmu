using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>A client-authored effect, announced by normal AOI and removed by its map timer. NewVisualEventObject uses key2 for
/// its object ID, unlike most New* events. ci9.t / cmv.aa5: key0 killer (guild crest/filter), key1 defeated character (rotation).</summary>
public sealed class KillEmoteVisual(long id, DeathSpot spot, KillEmoteCatalog.Emote emote, long killer, long victim, long start, long end)
    : WorldObject(id, spot)
{
    public long Killer { get; } = killer;
    public long Victim { get; } = victim;
    public long Start { get; } = start;
    public long End { get; } = end;
    public override string Kind => "kill-emote";
    public override string TypeName => emote.Name;
    public override bool LeaveOnRange => true;
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewVisualEventObjectEvent
        {
            d6u = Killer, d6v = Victim, d6w = Id, d6x = Position, d6y = Direction,
            d6z = emote.VisualId, d60 = 0, d61 = Start, d62 = End,
            Extra = new() { [5] = emote.VisualId, [6] = (byte)0 },
        };
    }
}
