using System;
using HarmonyLib;
using UnityEngine;

namespace ActionBarsMod;

internal struct KeyBind
{
    public KeyCode Key;
    public bool Shift, Ctrl, Alt;
    public string Label;
    public bool Valid => Key != KeyCode.None;

    public static KeyBind Parse(string s, out string error)
    {
        error = null;
        var b = new KeyBind();
        if (string.IsNullOrWhiteSpace(s) || s.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return b;
        var labels = new System.Collections.Generic.List<string>();
        foreach (var raw in s.Split('+'))
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;
            switch (p.ToLowerInvariant())
            {
                case "shift": b.Shift = true; labels.Add("S"); continue;
                case "ctrl": case "control": b.Ctrl = true; labels.Add("C"); continue;
                case "alt": b.Alt = true; labels.Add("A"); continue;
            }
            if (p.Length == 1 && char.IsDigit(p[0])) p = "Alpha" + p;
            if (Enum.TryParse(p, true, out KeyCode k) && k != KeyCode.None) { b.Key = k; labels.Add(Short(k)); }
            else { error = $"unknown key '{p}' in '{s}'"; return new KeyBind(); }
        }
        b.Label = Join(labels); // "S1", "C1", "SA1"
        return b;
    }

    private static string Join(System.Collections.Generic.List<string> l)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var x in l) sb.Append(x);
        return sb.ToString();
    }

    internal static string Short(KeyCode k) => k switch
    {
        >= KeyCode.Alpha0 and <= KeyCode.Alpha9 => ((int)k - (int)KeyCode.Alpha0).ToString(),
        >= KeyCode.Keypad0 and <= KeyCode.Keypad9 => "N" + ((int)k - (int)KeyCode.Keypad0),
        KeyCode.Mouse2 => "MMB", KeyCode.Mouse3 => "M4", KeyCode.Mouse4 => "M5",
        _ => k.ToString()
    };
}

/// <summary>
/// Keys: polled with the legacy Input (the game uses it too). The game's own hotkeys go through the dispatcher
/// dvh.f(KeyCode, EventModifiers, bool) (VA 0x181894130, see CameraMod NOTES "Game hotkeys"): '1'/'2' are the potion/food slots.
/// A Harmony prefix there swallows a key ONLY when its modifiers equal one of our bindings whose slot exists (so plain 1/2 and every
/// other combination stay the game's). CameraMod has its own prefix on the same method; both coexist (each only skips its keys).
/// No casts while typing: NGUI UIInput.selection != null, or the dispatcher's block counter dvh.g (@0x38, text input) > 0.
/// </summary>
internal static class KeyInput
{
    internal static readonly KeyBind[] Binds = new KeyBind[12];
    private static readonly string[] _parsed = new string[12];
    private static dvh _dvh;
    private static bool _typingBroken, _loggedSwallow;

    internal static void Install(Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(dvh), "f", new[] { typeof(KeyCode), typeof(EventModifiers), typeof(bool) });
            if (m == null) { Plugin.L.LogWarning("ActionBarsMod: hook NOT applied: dvh.f(KeyCode,EventModifiers,bool) not found - the game may also react to our key combos"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(KeyInput), nameof(DispatchPrefix)));
            Plugin.L.LogInfo("ActionBarsMod: hooked dvh.f(KeyCode,EventModifiers,bool) (VA 0x181894130, game hotkey dispatcher) prefix: our modifier combos only");
        }
        catch (Exception ex) { Plugin.L.LogWarning("ActionBarsMod: hook NOT applied: hotkey dispatcher: " + ex.GetType().Name + ": " + ex.Message); }
    }

    internal static void Refresh()
    {
        for (int i = 0; i < 12; i++)
        {
            string v = Plugin.Keys[i].Value ?? "";
            if (v == _parsed[i]) continue;
            _parsed[i] = v;
            Binds[i] = KeyBind.Parse(v, out var err);
            if (err != null) Plugin.L.LogWarning($"ActionBarsMod: key Bar{i / 6 + 1}Slot{i % 6 + 1}: {err} (use e.g. Shift+Alpha1, Ctrl+Q)");
        }
    }

    private static bool DispatchPrefix(dvh __instance, KeyCode __0, EventModifiers __1, bool __2, ref bool __result)
    {
        try
        {
            if (_dvh == null) _dvh = __instance;
            bool shift = (__1 & EventModifiers.Shift) != 0, ctrl = (__1 & EventModifiers.Control) != 0, alt = (__1 & EventModifiers.Alt) != 0;
            if (!shift && !ctrl && !alt) return true; // plain keys always stay the game's
            for (int i = 0; i < 12; i++)
            {
                var b = Binds[i];
                if (!b.Valid || b.Key != __0 || b.Shift != shift || b.Ctrl != ctrl || b.Alt != alt) continue;
                if (Net.Index(i / 6, i % 6) < 0 || !BarsOverlay.Visible) continue;
                if (!_loggedSwallow) { _loggedSwallow = true; Plugin.L.LogInfo($"ActionBarsMod: game hotkey {__1}+{__0} swallowed (extra bar key)"); }
                __result = false;
                return false;
            }
            return true;
        }
        catch { return true; }
    }

    internal static bool Typing()
    {
        if (_typingBroken) return false;
        try
        {
            if (UIInput.selection != null) return true;
            if (_dvh == null) { var s = dw1.ab(); if (s != null) _dvh = s.ac(); }
            return _dvh != null && System.Runtime.InteropServices.Marshal.ReadInt32(_dvh.Pointer + 0x38) > 0; // dvh.g (hotkey block counter); the interop name clashes with method g();
        }
        catch (Exception ex) { _typingBroken = true; Plugin.L.LogWarning("ActionBarsMod: typing check unavailable: " + ex.Message); return false; }
    }

    /// <summary>Per frame (bars visible).</summary>
    internal static void Update()
    {
        Refresh();
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        for (int i = 0; i < 12; i++)
        {
            var b = Binds[i];
            if (!b.Valid || b.Shift != shift || b.Ctrl != ctrl || b.Alt != alt) continue;
            if (!Input.GetKeyDown(b.Key)) continue;
            int idx = Net.Index(i / 6, i % 6);
            if (idx < 0) continue;
            if (Typing()) return;
            Caster.Cast(idx, "key " + Plugin.Keys[i].Value);
        }
    }
}

/// <summary>
/// A click on one of our buttons must not also reach the world: Harmony POSTFIX on LocalInputHandler.EvaluateActionsByInput(int touchId,
/// UICamera.b scheme, g event, ref h mods) (VA 0x181997390), which returns the action mask (move / attack / interact) of a click; while
/// the cursor is over the bars the mask is cleared (0 = no action). CameraMod has its own postfix there; both coexist (each only changes
/// the mask in its own case). (The earlier prefix on the void HandleInputActions declared __result and made HarmonyX fail that method.)
/// </summary>
internal static class ClickGuard
{
    private static bool _logged;

    internal static void Install(Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(LocalInputHandler), "EvaluateActionsByInput");
            if (m == null) { Plugin.L.LogWarning("ActionBarsMod: hook NOT applied: LocalInputHandler.EvaluateActionsByInput not found - clicks on the bars also walk"); return; }
            if (m.ReturnType != typeof(LocalInputHandler.e)) { Plugin.L.LogWarning("ActionBarsMod: hook NOT applied: EvaluateActionsByInput returns " + m.ReturnType.Name + ", not the action mask"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(ClickGuard), nameof(Postfix)));
            Plugin.L.LogInfo("ActionBarsMod: hooked LocalInputHandler.EvaluateActionsByInput (VA 0x181997390) postfix: clicks on the bars stay ours (mask cleared)");
        }
        catch (Exception ex) { Plugin.L.LogWarning("ActionBarsMod: hook NOT applied: EvaluateActionsByInput: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static void Postfix(ref LocalInputHandler.e __result)
    {
        try
        {
            if (!Plugin.BlockClicksUnderBars.Value || !BarsOverlay.MouseOverBars()) return;
            __result = default;
            if (!_logged) { _logged = true; Plugin.L.LogInfo("ActionBarsMod: a click on the extra bars was kept from the world (action mask cleared)"); }
        }
        catch { }
    }
}
