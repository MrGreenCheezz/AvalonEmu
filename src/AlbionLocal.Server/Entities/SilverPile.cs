using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// The silver a killed mob drops (stage 5c SPEC §5c.3; the killer's own piles of 103829): a world object in the visibility
/// grid at the mob's position, made in the kill batch after UpdateFame, for <see cref="Owner"/> (the killer) to take with
/// TakeSilver (op 54). It vanishes with a Leave when taken or after its decay time (harvestables.xml decaytimeseconds of the
/// silver type's tier, 300 s). StageE (pvp SPEC §4.4 [infer]): a dead player's silver drops the same way next to its corpse,
/// with key 3 the victim's object id and key 18 <see cref="FromPlayer"/>. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class SilverPile(long id, ClusterObject spawn, Vector2 position, long sourceId, string source, int amount, double factor, Guid owner,
    long createdTicks, int partyId = 0, byte kind = SilverPile.FromMob)
    : WorldObject(id, spawn)
{
    /// <summary>NewSilverObject key 18: a mob's silver (0) or other silver (3, a player's; Knockdown's notes, stage 5 SPEC §5e).</summary>
    public const byte FromMob = 0, FromPlayer = 3;

    /// <summary>The silver of <paramref name="mob"/>, killed (key 3 is its object id).</summary>
    public SilverPile(long id, Mob mob, int amount, double factor, Guid owner, long createdTicks, int partyId = 0)
        : this(id, mob.Spawn, mob.Position, mob.Id, mob.Template.Name, amount, factor, owner, createdTicks, partyId)
    {
    }

    /// <summary>Key 3: the object id of the dead mob or player it comes from.</summary>
    public long SourceId { get; } = sourceId;

    /// <summary>What it comes from, for logs (the mob's uniquename, the player's name).</summary>
    public string Source { get; } = source;

    /// <summary>Key 18 (<see cref="FromMob"/>, <see cref="FromPlayer"/>).</summary>
    public byte SourceKind { get; } = kind;

    /// <summary>Key 2: whole silver before the factor (ghoul T4 47-49).</summary>
    public int Amount { get; } = amount;

    /// <summary>TakeSilver key 8: mobsilverlootfactor × globalmobsilverlootfactor of the cluster (safe 1.305).</summary>
    public double Factor { get; } = factor;

    /// <summary>Key 6: the killer's character Guid, the only one who may take it.</summary>
    public Guid Owner { get; } = owner;

    /// <summary>The killer's party id when it was in one (0 = none): its members may take the pile too (stageD party SPEC §6 [choice]); not on the wire.</summary>
    public int PartyId { get; } = partyId;

    /// <summary>
    /// The killer's guild when Guilds:ShareWithoutParty was on at the kill (empty = none): its members may take the pile too (guilds
    /// SPEC §10.4, as a party's); not on the wire.
    /// </summary>
    public Guid GuildId { get; init; }

    /// <summary>Whether <paramref name="p"/> may take it: the owner, or a member of the owner's party (or sharing guild) at the kill (checked at take time).</summary>
    public bool MayTake(Player p) =>
        p.Character.Id == Owner || PartyId != 0 && p.World.Parties.Of(p.Character.Id)?.Id == PartyId
        || GuildId != Guid.Empty && p.World.Guilds.Of(p.Character.Id)?.Id == GuildId;

    /// <summary>Key 5: when the pile was made (Int64 ticks).</summary>
    public long CreatedTicks { get; } = createdTicks;

    private readonly Vector2 _position = position;

    public override Vector2 Position => _position;

    public override string Kind => "silver";

    public override string TypeName => $"{Amount} silver of {Source}";

    /// <summary>Left when out of range, sent anew on re-entry, as a loot bag.</summary>
    public override bool LeaveOnRange => true;

    /// <summary>
    /// NewSilverObject (44, auv; the killer's own piles of 103829): 0 id, 1 position, 2 amount, 3 the dead mob's object id,
    /// 4 true, 5 creation ticks, 6 [the owner's Guid], 12 the premium appearance for an eligible premium viewer,
    /// 17 byte 0 (FlaggingStatus), 18 byte 0 (a mob's; 3 is non-mob silver, a dead player's). The client's SilverDropSettings
    /// selects prefab/premiumprefab from key 12; the captured owner's true value was not an ownership flag.
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewSilverObjectEvent
        {
            c94 = Id,                                  // [0]
            c95 = Position,                            // [1]
            c96 = Amount,                              // [2]
            c97 = SourceId,                            // [3]
            c98 = true,                                // [4]
            c99 = CreatedTicks,                        // [5]
            daa = [Owner],                             // [6]
            dag = MayTake(viewer) && Systems.Premium.Is(viewer), // [12] premium graphics; ownership is carried separately
            Extra = new() { [17] = (byte)0, [18] = SourceKind },
        };
    }
}
