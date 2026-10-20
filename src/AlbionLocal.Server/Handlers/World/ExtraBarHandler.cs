using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The extra action bars' client side (analysis/work/stageN/extrabars/SPEC.md; <see cref="ExtraBars"/>): the mod's hello (op 9001), its own cast
/// operation (op 9002) and the extra cast in the client's fake slot 25 (CastStart, routed here by <see cref="CastStart"/>). Both casts run the
/// normal pipeline (<see cref="SpellCasts.Check"/>: knockdown, stun, silence, combat use, range, target, the spell's own cooldown, energy; the
/// plugins' formulas and OnCheckCast with Slot = Extra1..12; <see cref="SpellCasts.Start"/>: the observers' CastStart, CastFinished/CastSpell to all
/// with slot 25, the hit, OnCast/OnBeforeHit/…); the spell's power and energy cost are those of the worn item that grants it.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The extra-bars protocol the client's mod said hello with (op 9001); 0 = a vanilla client, which never gets the mod's events.</summary>
    public int ExtraBarsProtocol { get; set; }

    /// <summary>ExtraBarsHello (op 9001): no response; the bars (event 9001) at once and on every change until the character logs out.</summary>
    [Handles((OperationCodes)CustomCodes.ExtraBarsHello, Status = SessionStatus.InWorld)]
    private AlbionResponse? ExtraBarsHello(ExtraBarsHelloRequest req)
    {
        if (Player is not { } player || player.IsBot) return null;
        world.ExtraBars.Hello(player, req.Protocol, req.Mod);
        return null;
    }

    /// <summary>
    /// ExtraItemQuery (op 9003, protocol 2): what an item (worn or not) grants by the rules; answered with event 9005 to a hello'd connection only
    /// (<see cref="ExtraBars.Query"/>).
    /// </summary>
    [Handles((OperationCodes)CustomCodes.ExtraItemQuery, Status = SessionStatus.InWorld)]
    private AlbionResponse? ExtraItemQuery(ExtraItemQueryRequest req)
    {
        if (Player is not { } player || player.IsBot || !ExtraBars.Modded(player)) return null;
        world.ExtraBars.Query(player, req.Item ?? "", req.Quality, req.Enchantment, req.RequestId);
        return null;
    }

    /// <summary>
    /// ExtraBarCast (op 9002): the spell of extra slot key 1 (0-based), at target key 5 / aim key 6. No response either way; a refusal is event
    /// 9003 (the mod's client is the only sender). The caster's client gets the cast's events with slot 25 (CastFinished key 3), as the fake-slot
    /// path; it did not start a cast of its own, so its animation is the mod's business (SPEC §5.2).
    /// </summary>
    [Handles((OperationCodes)CustomCodes.ExtraBarCast, Status = SessionStatus.InWorld)]
    private AlbionResponse? ExtraBarCast(ExtraBarCastRequest req)
    {
        if (Player is not { Map: not null } player || player.IsBot) return null;
        Emotes.Stop(player, "casts");
        var start = SpellCasts.StartOf(world, req.Time);
        var spell = req.Spell > 0 ? req.Spell : -1;
        if (ExtraCast(player, req.Index, spell, req.Target, req.Aim, req.Counter, start) is { } refused)
            ExtraBars.Refused(player, req.Index, spell >= 0 ? spell : world.ExtraBars.State(player).Spells.ElementAtOrDefault(req.Index), refused.Code, req.Counter, refused.Text);
        return null;
    }

    /// <summary>
    /// CastStart in the fake slot 25 of a spell that is in the player's extra bars (and has no useobject): the mod put the extra spell into slot 25
    /// (a48.u) and cast it with the client's own fake-slot caster (cwc.u_3), so the client predicts it (cast bar, animation) as its own. A refusal
    /// is the normal CastStart refusal (960, key 1 = 25), plus event 9003 for the mod.
    /// </summary>
    private AlbionResponse? ExtraSlotCastStart(Player player, int index, CastStartRequest req)
    {
        if (ExtraCast(player, index, req.d0, req.d1, req.d2, req.d4, SpellCasts.StartOf(world, req.wf)) is not { } refused) return null;
        ExtraBars.Refused(player, index, req.d0, refused.Code, req.d4, refused.Text);
        return new CastStartResponse { ReturnCode = CastRefused, anl = req.d0, anq = req.d4, anr = req.d1, Extra = { [1] = req.dx } };
    }

    /// <summary>
    /// An extra slot's cast (both paths): the slot must hold the spell (<paramref name="spellId"/> -1 = whatever it holds), an activespell; then the
    /// CastStart pipeline with the slot's item as the source. Null when it started (or finished, an instant), else the refusal code (SPEC §4.4) and a
    /// plugin's text.
    /// </summary>
    internal (string Code, string? Text)? ExtraCast(Player player, int index, int spellId, long target, Vector2 aim, byte counter, long start)
    {
        var bars = world.ExtraBars;
        bars.Refresh(player, Plugin.Api.ExtraBarChangeSource.Equipment); // an equipment change no event announced
        var slots = bars.State(player).Spells;
        if (index < 0 || index >= slots.Length) return ("BadIndex", null);
        var id = slots[index];
        if (id < 0) return ("Empty", null);
        if (spellId >= 0 && spellId != id) return ("NotInBar", null);
        var name = world.Spells?.Name(id) ?? id.ToString(CultureInfo.InvariantCulture);
        Death.Interrupt(player, "the player casts", interrupted: false);
        world.FakeCasts.Interrupt(player, "the player casts", interrupted: false);
        var wire = new int[FakeSlotCasts.Slot + 1];
        Array.Fill(wire, -1);
        wire[FakeSlotCasts.Slot] = id;
        var item = bars.ItemAt(player, index) ?? player.Inventory.Equipment[0]; // an override: the main hand's power
        var (cast, why) = SpellCasts.Check(player, wire, FakeSlotCasts.Slot, id, target, aim, counter, start, grantedBy: item);
        if (cast is null)
        {
            Log.Info(Name, $"'{player.Character.Name}' extra slot {index + 1} cast of {name} refused: {why}");
            return (why.ToString(), null);
        }
        cast = cast with { ExtraIndex = index };
        var (gated, refusedBy) = world.Plugins.CheckCast(player, cast);
        if (refusedBy is not null)
        {
            Log.Info(Name, $"'{player.Character.Name}' extra slot {index + 1} cast of {name} refused by a plugin: {refusedBy}");
            if (refusedBy.Length > 0) player.Session.Send(new SystemMessageEvent { d0p = refusedBy, d0q = 1 });
            return ("Plugin", refusedBy);
        }
        Knockdown.Acted(player, "casts");
        CancelAction(player, "the player casts", tellClient: true);
        Mounts.BeforeAction(player, "casts");
        Log.Debug(Name, $"'{player.Character.Name}' casts {name} from extra slot {index + 1}");
        SpellCasts.Start(player, gated);
        return null;
    }
}
