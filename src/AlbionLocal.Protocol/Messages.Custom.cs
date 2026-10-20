#nullable enable
using System.Numerics;

namespace AlbionLocal.Server.Protocol;

// Messages of OUR client mods only (analysis/work/stageN/extrabars/SPEC.md): codes far above the client's own (OperationCodes ends at 555,
// EventCodes at 711; the client's event switch ci7.xk 0x1809835b0 sends any code above 711 to its default branch, which logs
// "Event not handled by PhotonClient" and goes on). The server sends the events only to a connection whose mod said hello.

/// <summary>The operation and event codes of the client mods' own messages (not in the client's enums).</summary>
public static class CustomCodes
{
    /// <summary>Op: the extra-bars mod announces itself (fire-and-forget, no response); the server answers with event <see cref="ExtraBars"/>.</summary>
    public const short ExtraBarsHello = 9001;

    /// <summary>Op: cast the spell of an extra slot (fire-and-forget; a refusal is event <see cref="ExtraBarCastRefused"/>).</summary>
    public const short ExtraBarCast = 9002;

    /// <summary>Op: what extra spells an item would grant (fire-and-forget; answered by event <see cref="ExtraItemSpells"/>).</summary>
    public const short ExtraItemQuery = 9003;

    /// <summary>Event: the whole extra bars (spells, sources, cooldowns).</summary>
    public const short ExtraBars = 9001;

    /// <summary>Event: cooldowns of some extra slots.</summary>
    public const short ExtraBarCooldowns = 9002;

    /// <summary>Event: an extra-slot cast was refused.</summary>
    public const short ExtraBarCastRefused = 9003;

    /// <summary>Event: the item → extra spells catalog (for the mod's item tooltips).</summary>
    public const short ExtraSpellCatalog = 9004;

    /// <summary>Event: the answer to op <see cref="ExtraItemQuery"/>, an item's extra spells resolved by the rules.</summary>
    public const short ExtraItemSpells = 9005;

    /// <summary>The extra-bars protocol version this server speaks (hello key 0, bars key 10).</summary>
    public const int ExtraBarsProtocol = 2;
}

/// <summary>ExtraBarsHello (op 9001): 0 int protocol version the mod speaks, 1 string the mod's name/version (free text, logged).</summary>
[AlbionOperation((OperationCodes)CustomCodes.ExtraBarsHello)]
public sealed class ExtraBarsHelloRequest : AlbionRequest
{
    [Param(0)] public int Protocol;
    [Param(1)] public string? Mod;
}

/// <summary>
/// ExtraBarCast (op 9002): 0 GameTimeStamp (Int64 ticks, or Int32 ms since the Login base as the client's own ops), 1 byte extra slot index
/// (0-based: 0-5 bar 1, 6-11 bar 2), 4 int spell id (0 or -1 = whatever the slot holds; else it must be the slot's), 5 long target object id
/// (0 or the caster's own id = none/self), 6 float[2] aim point (ground spells), 8 byte cast counter.
/// </summary>
[AlbionOperation((OperationCodes)CustomCodes.ExtraBarCast)]
public sealed class ExtraBarCastRequest : AlbionRequest
{
    [Param(0), Wire(WireKind.CompressedTime)] public long Time;
    [Param(1)] public byte Index;
    [Param(4)] public int Spell;
    [Param(5)] public long Target;
    [Param(6)] public Vector2 Aim;
    [Param(8)] public byte Counter;
}

/// <summary>
/// ExtraBars (event 9001), to a hello'd connection only: 0 byte bar count, 1 byte bar size, 2 int[] spell id per slot (-1 empty),
/// 3 string[] spell uniquename per slot ("" empty), 4 string[] the item that grants it ("" none or a plugin's), 5 byte[] source per slot
/// (0 empty, 1 worn item, 2 plugin/rule/GM override), 6 long[] cooldown start (GameTimeStamp ticks; 0 = ready), 7 long[] cooldown end
/// (ticks; 0 = ready), 8 int revision (counts up per sent bars), 10 int server protocol version, 11 string[] the rule per slot (protocol 2:
/// "unchosen", "map:&lt;family&gt;", "enchant:&lt;n&gt;", "quality:&lt;n&gt;", "artifact", "item:&lt;pattern&gt;", "override"; "" empty).
/// </summary>
[AlbionEvent((EventCodes)CustomCodes.ExtraBars)]
public sealed class ExtraBarsEvent : AlbionEvent
{
    [Param(0)] public byte Bars;
    [Param(1)] public byte Size;
    [Param(2)] public int[]? Spells;
    [Param(3)] public string[]? Names;
    [Param(4)] public string[]? Items;
    [Param(5)] public byte[]? Sources;
    [Param(6)] public long[]? CooldownStarts;
    [Param(7)] public long[]? CooldownEnds;
    [Param(8)] public int Revision;
    [Param(10)] public int Protocol;
    [Param(11)] public string[]? Labels;
}

/// <summary>ExtraBarCooldowns (event 9002): 0 byte[] slot indexes, 1 long[] cooldown start, 2 long[] cooldown end (ticks; 0 = ready), 3 int[] spell ids.</summary>
[AlbionEvent((EventCodes)CustomCodes.ExtraBarCooldowns)]
public sealed class ExtraBarCooldownsEvent : AlbionEvent
{
    [Param(0)] public byte[]? Indexes;
    [Param(1)] public long[]? Starts;
    [Param(2)] public long[]? Ends;
    [Param(3)] public int[]? Spells;
}

/// <summary>ExtraBarCastRefused (event 9003): 0 byte slot index (255 = not an extra slot), 1 int spell id, 2 string reason code, 3 byte cast counter, 4 string text (plugin reason or "").</summary>
[AlbionEvent((EventCodes)CustomCodes.ExtraBarCastRefused)]
public sealed class ExtraBarCastRefusedEvent : AlbionEvent
{
    [Param(0)] public byte Index;
    [Param(1)] public int Spell;
    [Param(2)] public string? Reason;
    [Param(3)] public byte Counter;
    [Param(4)] public string? Text;
}

/// <summary>
/// ExtraSpellCatalog (event 9004), to a hello'd connection after the hello, after a Join of such a character and after a reload: the item → extra
/// spells table, one entry per (rule, spell) in the server's precedence order (exact names first, then patterns; file order). 0 string[] item
/// uniquename or pattern (* any text, ? one character; matched case-insensitively against the item's uniquename with and without "@n"),
/// 1 string[] spell uniquename, 2 int[] spell id (-1 when the server does not know it), 3 int revision (counts up per reload), 4 byte bars,
/// 5 byte bar size, 10 int server protocol version.
/// </summary>
[AlbionEvent((EventCodes)CustomCodes.ExtraSpellCatalog)]
public sealed class ExtraSpellCatalogEvent : AlbionEvent
{
    [Param(0)] public string[]? Patterns;
    [Param(1)] public string[]? Spells;
    [Param(2)] public int[]? SpellIds;
    [Param(3)] public int Revision;
    [Param(4)] public byte Bars;
    [Param(5)] public byte Size;
    [Param(10)] public int Protocol;
}

/// <summary>
/// ExtraItemQuery (op 9003, protocol 2), fire-and-forget: 0 string item uniquename (with or without "@n"), 1 int quality (1-5; 0 = 1),
/// 2 int enchantment (used when the name has no "@n"), 3 int request id (echoed). Answered by event 9005 to a hello'd connection.
/// </summary>
[AlbionOperation((OperationCodes)CustomCodes.ExtraItemQuery)]
public sealed class ExtraItemQueryRequest : AlbionRequest
{
    [Param(0)] public string? Item;
    [Param(1)] public int Quality;
    [Param(2)] public int Enchantment;
    [Param(3)] public int RequestId;
}

/// <summary>
/// ExtraItemSpells (event 9005): 0 int request id, 1 string item uniquename as resolved (with "@n"; the asked name when unknown), 2 string[] spell
/// uniquenames, 3 int[] spell ids, 4 string[] the rule of each (the labels of event 9001 key 11), 5 int quality, 6 int enchantment, 7 bool known item,
/// 10 int server protocol. Empty arrays: the item grants nothing.
/// </summary>
[AlbionEvent((EventCodes)CustomCodes.ExtraItemSpells)]
public sealed class ExtraItemSpellsEvent : AlbionEvent
{
    [Param(0)] public int RequestId;
    [Param(1)] public string? Item;
    [Param(2)] public string[]? Spells;
    [Param(3)] public int[]? SpellIds;
    [Param(4)] public string[]? Labels;
    [Param(5)] public int Quality;
    [Param(6)] public int Enchantment;
    [Param(7)] public bool Known;
    [Param(10)] public int Protocol;
}
