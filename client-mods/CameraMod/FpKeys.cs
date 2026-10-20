using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CameraMod;

/// <summary>
/// Game hotkeys while in first person.
/// Every keyboard hotkey of the game (spell slots Q/W/E/R/D/F, 1/2 consumables, A = slot 14, menus, ...) goes through one
/// dispatcher: dvh.f(KeyCode key, EventModifiers mods, bool down) (RVA 0x1894130, VA 0x181894130), called from
/// dvh.f(Event) (0x181894B50) for IMGUI KeyDown (only when Input.GetKeyDown(key)) / KeyUp. It looks the
/// HotKeyDefinition(key, mods) up in the key-binding storage (dvl.asc) and runs the bound dvf action
/// (down: dvf.c / dvf.e(param), up: dvf.d / dvf.f(param)); it does nothing while text input blocks hotkeys (dvh.g @0x38 > 0).
/// Spell slot defaults (SpellButtonGui.BindHotKeysForSpellSlot 0x181475530): slot0 Q, 1 W, 2 E, 3 R, 4 D, 5 F, 12 '1', 13 '2',
/// 14 A; "&lt;slot&gt;_self" = same key + Alt.
/// In first person a Harmony prefix on that dispatcher swallows the movement keys (W/A/S/D), the remap source keys and the
/// mod's own keys (interact, hint), and the mod injects the remapped spell keys itself by calling the same dispatcher (with a
/// re-entrancy flag), so the game sees "W pressed" when e.g. mouse button 4 or '3' is pressed. The dvh instance comes from the
/// static dw1 singleton (dw1.ab().ac(), as dk4.a(bool,out bool) reads it) or from the first dispatcher call seen.
/// v4: RightMouse = Hotkey adds Mouse1 -> RightMouseHotkey (only while the cursor is held, so right clicks in windows stay UI clicks).
/// </summary>
internal static class FpKeys
{
    private static bool _injecting, _loggedBlock, _brokenInject;
    private static dvh _dvh;
    private static readonly List<(KeyCode src, KeyCode dst)> _remap = new();
    private static string _remapParsed;
    private static RightMouseMode _remapRm;
    private static KeyCode _remapRk;
    private static readonly HashSet<KeyCode> _held = new();
    private static int _injectLogs;

    internal static void Install(Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(dvh), "f", new[] { typeof(KeyCode), typeof(EventModifiers), typeof(bool) });
            if (m == null) { Plugin.L.LogWarning("CameraMod: hook NOT applied: dvh.f(KeyCode,EventModifiers,bool) not found - game hotkeys on W/A/S/D stay active in first person"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(FpKeys), nameof(DispatchPrefix)));
            Plugin.L.LogInfo("CameraMod: hooked dvh.f(KeyCode,EventModifiers,bool) (VA 0x181894130, game hotkey dispatcher) prefix: first-person key blocking/remap");
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: hook NOT applied: hotkey dispatcher: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static List<(KeyCode src, KeyCode dst)> Remap()
    {
        // cache key compared without building strings (this runs every frame and for every game hotkey event)
        string spec = Plugin.FpSpellKeys.Value ?? "";
        var rm = Plugin.RightMouse.Value; var rk = Plugin.RightMouseHotkey.Value;
        if (_remapParsed != null && ReferenceEquals(spec, _remapParsed) && rm == _remapRm && rk == _remapRk) return _remap;
        _remapParsed = spec; _remapRm = rm; _remapRk = rk;
        _remap.Clear();
        foreach (var part in (Plugin.FpSpellKeys.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv.Length != 2) { Plugin.L.LogWarning($"CameraMod: FpSpellKeys: bad entry '{part}' (use Source=Target, e.g. Mouse3=W)"); continue; }
            if (Enum.TryParse(kv[0].Trim(), true, out KeyCode src) && Enum.TryParse(kv[1].Trim(), true, out KeyCode dst)) _remap.Add((src, dst));
            else Plugin.L.LogWarning($"CameraMod: FpSpellKeys: unknown key name in '{part}' (Unity KeyCode names: W, D, Alpha3, Mouse3, Mouse4, ...)");
        }
        if (Plugin.RightMouse.Value == RightMouseMode.Hotkey && Plugin.RightMouseHotkey.Value != KeyCode.None) _remap.Add((KeyCode.Mouse1, Plugin.RightMouseHotkey.Value));
        var list = new List<string>();
        foreach (var (a, b) in _remap) list.Add($"{a}->{b}");
        Plugin.L.LogInfo("CameraMod: first-person key remap: " + (list.Count == 0 ? "(none)" : string.Join(", ", list)));
        return _remap;
    }

    internal static string RemapText()
    {
        var list = new List<string>();
        foreach (var (a, b) in Remap()) list.Add($"{KeyName(a)}={KeyName(b)}");
        return string.Join("  ", list);
    }

    internal static string KeyName(KeyCode k) => k switch
    {
        KeyCode.Mouse0 => "LMB", KeyCode.Mouse1 => "RMB", KeyCode.Mouse2 => "MMB", KeyCode.Mouse3 => "Mouse4", KeyCode.Mouse4 => "Mouse5",
        >= KeyCode.Alpha0 and <= KeyCode.Alpha9 => ((int)k - (int)KeyCode.Alpha0).ToString(),
        _ => k.ToString()
    };

    private static bool IsMoveKey(KeyCode k) =>
        k == Plugin.MoveForwardKey.Value || k == Plugin.MoveBackKey.Value || k == Plugin.MoveLeftKey.Value || k == Plugin.MoveRightKey.Value;

    private static bool IsModKey(KeyCode k) => k != KeyCode.None && (k == Plugin.InteractKey.Value || k == Plugin.HintKey.Value || k == Plugin.ModeKey.Value || k == Plugin.FirstPersonKey.Value
        || (CameraLogic.InThirdPerson && k == Plugin.TpShoulderKey.Value));

    private static bool Active => CameraLogic.InCloseCamera && Plugin.KeyboardMove.Value && Plugin.BlockGameMoveKeys.Value;

    /// <summary>Prefix of dvh.f(KeyCode, EventModifiers, bool): false = swallow the key (dispatcher returns false, event not used).</summary>
    private static bool DispatchPrefix(dvh __instance, KeyCode __0, bool __2, ref bool __result)
    {
        if (!CameraLogic.InCloseCamera) { if (_dvh == null) _dvh = __instance; return true; }
        long t0 = Perf.Begin();
        try
        {
            if (_dvh == null) _dvh = __instance;
            if (_injecting) return true;
            bool block = IsModKey(__0) || (Active && IsMoveKey(__0));
            if (!block && Active) foreach (var (src, _) in Remap()) if (src == __0) { block = true; break; }
            if (!block) return true;
            if (!_loggedBlock) { _loggedBlock = true; Plugin.L.LogInfo($"CameraMod: first person: game hotkey {__0} ({(__2 ? "down" : "up")}) swallowed (movement key / remap source / mod key)"); }
            __result = false;
            return false;
        }
        catch (Exception ex) { CameraLogic.Fail("hotkey prefix", ex); return true; }
        finally { Perf.End(Perf.Hotkey, t0); }
    }

    private static dvh Dispatcher()
    {
        if (_dvh != null) return _dvh;
        try { var s = dw1.ab(); if (s != null) _dvh = s.ac(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: dw1.ab().ac() (hotkey manager) failed: " + ex.Message); }
        return _dvh;
    }

    private static EventModifiers Mods()
    {
        EventModifiers m = 0;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) m |= EventModifiers.Shift;
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) m |= EventModifiers.Control;
        if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) m |= EventModifiers.Alt;
        return m;
    }

    private static bool IsPointerButton(KeyCode k) => k == KeyCode.Mouse0 || k == KeyCode.Mouse1 || k == KeyCode.Mouse2;

    /// <summary>Per frame in first person: inject the remapped spell keys.</summary>
    internal static void Update()
    {
        if (!Active || _brokenInject) return;
        foreach (var (src, dst) in Remap())
        {
            bool down = Input.GetKeyDown(src), up = Input.GetKeyUp(src);
            if (!down && !up) continue;
            if (down && FpMovement.Typing()) continue;
            if (down && IsPointerButton(src) && (!MouseLock.Locked || !MouseLock.CentreCaptured)) continue; // free cursor / WoW drag: mouse buttons are UI clicks / camera
            if (up && !_held.Contains(src)) continue;
            var d = Dispatcher();
            if (d == null) { Plugin.L.LogWarning("CameraMod: spell key remap: hotkey manager (dvh) not found yet"); return; }
            try
            {
                _injecting = true;
                bool ok = d.f(dst, Mods(), down);
                if (down) _held.Add(src); else _held.Remove(src);
                if (_injectLogs++ < 20) Plugin.L.LogInfo($"CameraMod: first person: {src} -> game hotkey {dst} {(down ? "down" : "up")} handled={ok}");
            }
            catch (Exception ex) { _brokenInject = true; Plugin.L.LogError("CameraMod: spell key remap failed, disabled: " + ex); }
            finally { _injecting = false; }
        }
    }

    /// <summary>Leaving first person: release injected keys that are still down.</summary>
    internal static void Exit()
    {
        if (_held.Count == 0) return;
        var d = Dispatcher();
        foreach (var src in _held)
        {
            foreach (var (s, dst) in _remap)
                if (s == src && d != null) { try { _injecting = true; d.f(dst, 0, false); } catch { } finally { _injecting = false; } }
        }
        _held.Clear();
    }
}
