using System;
using Albion.Common.GameData;
using Albion.Common.Time;
using UnityEngine;

namespace ActionBarsMod;

/// <summary>
/// Casting an extra slot (SPEC §4.2 / §4.3).
/// Slot25 (preferred): the client's spell slots are ctp.alf (ct4, field 0x488) -> ct4.d(CharacterSpellSlot) -> a48. Like the chest
/// click cz5.u (0x180A268F0) we put the spell into slot 25 (FakeSpell) with a48.u(bio, bdf, long) (0x1808F2060, which also resets the
/// slot's charge/channel state), clear the slot's client cooldown (a48 +0x38 start / +0x40 end GameTimeStamp, +0x90 "cooling" flag
/// = interop g/h/r; checked by cwc.v(slot) 0x1809DA430 -> a48.y(now)) because our cooldowns come from events 9001/9002 only, then:
///  - Slot25: LocalInputHandler.StartCast(slot, hotkey, alt) (0x1819AE480 -> StartCastInternal) = what a spell key does: energy/
///    usability checks (CanSpellSlotBeUsedInGeneral 0x181991B60; cwc.w(25) accepts slot 25), auto target / targeting mode / ground aim
///    at the cursor, cast prediction (cast bar, animation) and the CastStart op {1: 25, 4: spell id, 5: target, 6: aim}.
///  - Slot25Direct: cwc.u(slot, Vector2 aim, null, false) (0x1809D76D0, the chest path) at the cursor ground point, no targeting mode.
/// Op: op 9002 {0 now, 1 index, 4 spell id, 5 LocalInputHandler.SelectedObjectId, 6 cursor ground point, 8 counter}; the server casts,
/// this client does not predict it (no own cast bar/animation).
/// </summary>
internal static class Caster
{
    private static byte _counter;
    private static bool _slotBroken;
    private static string _lastPathLogged = "";

    internal static void Cast(int index, string via)
    {
        if (index < 0 || index >= 12) return;
        var s = Net.Slots[index];
        if (s.SpellId < 0 || s.Unique.Length == 0) return;
        long now = Clock.Now();
        if (s.CdEnd > now)
        {
            s.FlashUntil = Time.unscaledTime + 0.25f;
            s.FlashText = "Cooldown";
            return;
        }
        var m = Plugin.Method.Value;
        string path = null;
        bool ok = false;
        if (m != CastMethod.Op && !_slotBroken)
        {
            string why = TrySlot25(s, m == CastMethod.Slot25Direct, out ok);
            if (why == null) path = m == CastMethod.Slot25Direct ? "slot25 direct (cwc.u)" : "slot25 (LocalInputHandler.StartCast)";
            else if (m != CastMethod.Auto)
            {
                Plugin.L.LogWarning($"ActionBarsMod: cast slot {Net.SlotName(index)} spell {s.SpellId} {s.Unique}: slot-25 path unavailable ({why}); CastMethod {m} -> not cast");
                return;
            }
            else Plugin.L.LogWarning($"ActionBarsMod: slot-25 path unavailable ({why}), falling back to op 9002");
        }
        if (path == null)
        {
            ok = CastOp(index, s);
            path = "op 9002";
        }
        Plugin.L.LogInfo($"ActionBarsMod: cast slot {Net.SlotName(index)} spell {s.SpellId} {s.Unique} via {path} ({via}) -> {(ok ? "started" : "refused by the client")}");
        if (path != _lastPathLogged) { _lastPathLogged = path; Plugin.L.LogInfo("ActionBarsMod: cast path in use: " + path); }
    }

    /// <summary>null = done (ok says whether the client accepted it), else why the path is unavailable.</summary>
    private static string TrySlot25(SlotInfo s, bool direct, out bool ok)
    {
        ok = false;
        try
        {
            var lih = LocalInputHandler.Instance;
            if (lih == null) return "no LocalInputHandler";
            var pc = lih.PlayerCharacter;
            if (pc == null) return "no local player character";
            var slots = pc.alf;
            if (slots == null) return "no spell slots (ctp.alf)";
            var a = slots.d(CharacterSpellSlot.FakeSpell);
            if (a == null) return "slot 25 not allocated";
            var info = Spells.Get(s.SpellId, s.Unique);
            if (info?.Spell == null) return $"spell {s.SpellId} {s.Unique} not an active spell in the client table";
            a.u(info.Spell, (bdf)null, 0L);
            ClearClientCooldown(a);
            if (direct)
            {
                var cw = pc.aze<cwc>();
                if (cw == null) return "no cwc component";
                var ground = lih.GetGroundPos(new Vector2(Input.mousePosition.x, Input.mousePosition.y));
                var aim = dle.c(ground);
                ok = cw.u(CharacterSpellSlot.FakeSpell, aim, new Il2CppSystem.Nullable<Albion.Common.Math.Vector2>(), false);
            }
            else ok = lih.StartCast(CharacterSpellSlot.FakeSpell, Plugin.CastAsHotkey.Value, false);
            return null;
        }
        catch (Exception ex)
        {
            _slotBroken = true;
            Plugin.L.LogError("ActionBarsMod: slot-25 cast failed, path disabled for this session: " + ex);
            return ex.GetType().Name;
        }
    }

    private static void ClearClientCooldown(a48 a)
    {
        try
        {
            var zero = default(GameTimeStamp);
            a.g = zero;  // +0x38 cooldown start
            a.h = zero;  // +0x40 cooldown end
            a.r = false; // +0x90 cooling flag
        }
        catch (Exception ex) { Plugin.L.LogWarning("ActionBarsMod: clearing the slot-25 client cooldown failed: " + ex.Message); }
    }

    /// <summary>Cursor ground point in Albion coordinates (dle.c(Vector3) = the conversion StartCast uses for the spell target).</summary>
    internal static bool GroundAim(out float x, out float y)
    {
        x = y = 0;
        try
        {
            var lih = LocalInputHandler.Instance;
            if (lih == null) return false;
            var g = lih.GetGroundPos(new Vector2(Input.mousePosition.x, Input.mousePosition.y));
            var v = dle.c(g);
            x = v.X; y = v.Y;
            return true;
        }
        catch (Exception ex) { Plugin.L.LogWarning("ActionBarsMod: cursor ground point failed: " + ex.Message); return false; }
    }

    private static bool CastOp(int index, SlotInfo s)
    {
        try { return CastOpUnsafe(index, s); }
        catch (Exception ex) { Plugin.L.LogError("ActionBarsMod: cast via op 9002 failed: " + ex.Message); return false; }
    }

    private static bool CastOpUnsafe(int index, SlotInfo s)
    {
        GroundAim(out float ax, out float ay);
        long target = 0;
        try { var lih = LocalInputHandler.Instance; if (lih != null) target = lih.SelectedObjectId; } catch { }
        _counter++;
        return Net.Send(9002, new (byte, Il2CppSystem.Object)[]
        {
            (0, Il2CppValues.BoxLong(Clock.Now())),
            (1, Il2CppValues.BoxByte((byte)index)),
            (4, Il2CppValues.BoxInt(s.SpellId)),
            (5, target != 0 ? Il2CppValues.BoxLong(target) : null),
            (6, Il2CppValues.FloatArray(ax, ay)),
            (8, Il2CppValues.BoxByte(_counter)),
        });
    }
}
