using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>Where a player died (stageE): the <see cref="ClusterObject"/> of its loot bag and silver, which have no cluster data of their own.</summary>
public sealed record DeathSpot : ClusterObject
{
    /// <summary>The spot of a death at <paramref name="at"/> on <paramref name="map"/> (key "&lt;cluster&gt;:death:&lt;n&gt;").</summary>
    public static DeathSpot At(Map map, Vector2 at, float direction) => new()
    {
        Key = new SpawnKey(map.Cluster, "death", ++map.CorpseCount), Tile = "", Position = at, Direction = direction,
        Component = new System.Xml.Linq.XElement("death"),
    };
}

/// <summary>
/// A dead player's loot (stageE pvp SPEC §4.4, sub-step E3): the items its death dropped, at the spot it died, as the stage-5 loot
/// flow takes them (RegisterToObject, InventoryMoveGivenItems: Network/LootHandler). Until <see cref="ProtectedUntil"/> (gamedata
/// &lt;Loot&gt; pvpallowedprotection 120 s, pvpforcedprotection 90 s in PvpForced) only <see cref="Looters"/> (the killer and its party
/// at the death, NewLoot key 10) may open it, then everyone; it vanishes when empty or after vanishtime (1800 s). Nothing of it is
/// saved: the victim's character loses the items at once, so a crash loses them rather than duplicating them [infer: SPEC §4.4
/// wants the bag in the same transaction; bags live in memory only]. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class PlayerLootBag(long id, DeathSpot spot, Player victim, Container items, Guid[] looters, long createdTicks, long protectedUntil, int[] equipment)
    : LootObject(id, spot)
{
    public override Container Items { get; } = items;

    /// <summary>NewLoot key 10: the character Guids that may open it before <see cref="ProtectedUntil"/>.</summary>
    public Guid[] Looters { get; } = looters;

    /// <summary>NewLoot key 12: the death (GameTimeStamp ticks).</summary>
    public long CreatedTicks { get; } = createdTicks;

    /// <summary>GameTimeStamp from which anyone may open it.</summary>
    public long ProtectedUntil { get; } = protectedUntil;

    /// <summary>Key 2: the victim's object id at the death.</summary>
    public long VictimId { get; } = victim.Id;

    /// <summary>Key 3: the victim's name.</summary>
    public string VictimName { get; } = victim.Character.Name;

    /// <summary>Key 23: the victim's character Guid (a5g.a bb(), SPEC §6 V9; whose Guid it is was not inspected [infer: the victim's]).</summary>
    public Guid VictimCharacter { get; } = victim.Character.Id;

    /// <summary>Key 1: the victim's character template (characters.xml index, NewCharacter key 3).</summary>
    public short Template { get; } = (short)victim.Character.Template;

    /// <summary>Key 24: the victim's FlaggingStatus at the death.</summary>
    public byte Flag { get; } = victim.Flag;

    /// <summary>Key 22: the victim's party id at the death (gx(), SPEC §6 V9); 0 = none (left out).</summary>
    public int PartyId { get; } = victim.World.Parties.Of(victim.Character.Id)?.Id ?? 0;

    /// <summary>LootEquipmentChanged key 1: the item type id per equipment slot the victim wore at its death (0 = empty), so the corpse wears them.</summary>
    public int[] Equipment { get; } = equipment;

    public override string Kind => "loot";

    public override string TypeName => $"of '{VictimName}' ({Items.Count} item(s))";

    /// <summary>
    /// Whether <paramref name="p"/> may open it now: anyone after the protection, a looter before; never under a cannotlootplayers effect
    /// (DISMOUNTED_HOSTILE's 5 s, stageR spell-effects).
    /// </summary>
    public override bool MayOpen(Player p) =>
        (p.World.GameNow >= ProtectedUntil || Looters.Contains(p.Character.Id)) && !Systems.SpellEffects.CannotLootPlayers(p, p.World.GameNow);

    /// <summary>
    /// NewLoot (98) with type 1 (the 27 captured player bags of 002835): 0 loot id, 1 template, 2 the victim's object id, 3 its name,
    /// 4 position, 5 direction, 6 true, 7 byte 1 (a player's), 10 the looters, 12 the death, 19 [] (string[]), 22 the victim's party id
    /// (when in one), 23 its character Guid, 24 its flag, 25 byte 3, 26 short -1; no guild or alliance (keys 20/21: not filled yet, guilds part 2). Then
    /// LootEquipmentChanged (220) {0 loot id, 1 the victim's equipment type ids} (clk.ajv: the corpse's look), on every announcement.
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewLootEvent
        {
            c3e = Id,                                   // [0]
            c3f = Template,                             // [1]
            c3g = VictimId,                             // [2]
            c3h = VictimName,                           // [3]
            c3i = Position,                             // [4]
            c3j = Direction,                            // [5]
            c3k = true,                                 // [6]
            c3l = 1,                                    // [7] a player's loot
            c3o = Looters,                              // [10]
            c3q = CreatedTicks,                         // [12]
            c3x = [],                                   // [19]
            c30 = PartyId,                              // [22] left out at 0
            c31 = VictimCharacter,                      // [23]
            Extra = new() { [24] = Flag, [25] = (byte)3, [26] = (short)-1 },
        };
        yield return new LootEquipmentChangedEvent { ddn = Id, cn8 = Equipment };
    }
}
