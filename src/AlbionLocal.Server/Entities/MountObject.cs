using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// The mount a dismounted player leaves standing beside it (stageD mounts SPEC §1, §4.3, §4.4): a world object in the visibility
/// grid, NewMountObject (311, auj) to whoever sees it and the common Leave when it goes (a remount or full mount, the owner's
/// knockdown, the mount unequipped, the owner leaving the map or walking beyond its halfmountrange, 18 m: stageJ carry-load SPEC §3.1). At most one per player, never saved. Only its owner may
/// remount at it (the client checks key 5 against its own Guid, clo.af). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class MountObject(long id, ClusterObject spawn, Player owner, ItemTemplate item, Vector2 position, float facing) : WorldObject(id, spawn)
{
    public Player Owner { get; } = owner;

    /// <summary>Key 1: the mount item's type; its remountdistance decides a remount (clo.aqt: the object's item, not the equipped one).</summary>
    public ItemTemplate Item { get; } = item;

    public override Vector2 Position { get; } = position;

    /// <summary>Key 4: the owner's facing at the dismount (left out at 0).</summary>
    public override float Direction { get; } = facing;

    public override string Kind => "mount";

    public override string TypeName => Item.Name;

    public override bool LeaveOnRange => true;

    /// <summary>
    /// NewMountObject (as captured for an own ox without a guild): 0 id, 1 the mount item's index, 2 skin -1 (none), 3 position,
    /// 4 facing, 5 the owner's character Guid, 7 byte 3 (bgi, always 3), 11-15 -1 and 17 1.0 (no guild logo); 6 FlaggingStatus 0,
    /// 8 (never sent), 9/10 (guild, alliance) and 16 are left out.
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        var skin = Owner.World.Vanity.MountSkinFor(Owner.Character, Item); // stageN vanity: the owner's skin of the mount's category
        yield return new NewMountObjectEvent
        {
            c69 = Id,                        // [0]
            c7a = Item.Index,                // [1]
            c7b = skin,                      // [2] the skin number, -1 = none (0 goes through Extra)
            c7c = Position,                  // [3]
            c7d = Direction,                 // [4]
            c7e = Owner.Character.Id,        // [5]
            c7g = 3,                         // [7]
            c7k = -1, c7l = -1, c7m = -1, c7n = -1, c7o = -1, // [11-15] no guild logo
            c7q = 1f,                        // [17]
            Extra = skin == 0 ? new() { [2] = 0 } : null,
        };
    }
}

/// <summary>A player's mount state (stageD mounts SPEC §3): none, the mount bar running (<see cref="PendingMount"/>), mounted.</summary>
public enum MountState { None, Mounting, Mounted }

/// <summary>A mount under way: its start and end GameTimeStamps, whether it is a remount at the own mount object, and the mount item.</summary>
public sealed record PendingMount(long Start, long End, bool Remount, Item Item);

/// <summary>
/// The mounted buff's castspellaftermoving (stageD mounts SPEC §4.7), read once when the player mounts: the speed buff, minmovespeed
/// (m/s) and movetimetocast, cooldownafterdamage and standtimetoendspell as ticks.
/// </summary>
public sealed record MountSpeedBuff(SpellTemplate Spell, float MinMoveSpeed, long MoveTime, long DamageCooldown, long StandTime);

/// <summary>
/// A player's mount (stageD mounts SPEC §3; <see cref="Systems.Mounts"/> holds the rules): the state, the mount under way, the
/// mount object it left standing, and the speed-buff tracker of the mounted buff's castspellaftermoving (§4.7). Whether it is
/// mounted and its cooldown are saved with the character (<see cref="Game.Character.MountedSince"/>); the rest lives with the
/// connection. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class PlayerMount
{
    public MountState State { get; internal set; }

    /// <summary>The mount under way while <see cref="State"/> is Mounting; null otherwise.</summary>
    public PendingMount? Pending { get; internal set; }

    /// <summary>
    /// The mount item ridden while <see cref="State"/> is Mounted (set when it mounts): which buffs a dismount takes off, also when the
    /// item has left slot 7 meanwhile.
    /// </summary>
    public ItemTemplate? Ridden { get; internal set; }

    /// <summary>The ridden mount's speed buff trigger (<see cref="MountSpeedBuff"/>); null = its mounted buff has none.</summary>
    public MountSpeedBuff? Speed { get; internal set; }

    /// <summary>The mount object on the map; null when none stands.</summary>
    public MountObject? Object { get; internal set; }

    /// <summary>GameTimeStamp it has been moving at speed since (the speed buff's movetimetocast); null while standing.</summary>
    internal long? MovingSince { get; set; }

    /// <summary>GameTimeStamp it has been standing since (standtimetoendspell); null while moving.</summary>
    internal long? StandingSince { get; set; }

    /// <summary>GameTimeStamp of the last hit on the mount (cooldownafterdamage); 0 = none.</summary>
    internal long LastDamage { get; set; }

    /// <summary>The speed buff is on.</summary>
    internal bool SpeedBuff { get; set; }
}
