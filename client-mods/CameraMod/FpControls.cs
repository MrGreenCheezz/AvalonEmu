using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace CameraMod;

/// <summary>
/// First-person mouse buttons, interact key, UI detection.
///
/// Game click path (see NOTES.md): UICamera.ProcessMouse -> LocalInputHandler.OnHandleInput (0x1819A4700)
///   -> EvaluateActionsByInput -> EvaluateActionsByMouseInput(button, event, out mods) (0x1819977E0) gives an action mask
///   (LocalInputHandler.e, flags) -> HandleInputActions(Vector2 screenPos, e mask, h mods, int touchId) (0x18199EF20):
///   ScreenPointToRay -> GetClickDetailsFor -> HandleInputActionsNotMove (0x18199EAA0; spell-target / placement modes, then
///   per clicked object HandleActionNotMove_Single 0x18199CC60: 0x8 = DoActionAttack, 0x10 = DoActionStaticObjectInteraction,
///   0x1C00 = DoActionChargeInteraction; nothing handled + 0x4 = select) and, when mask &amp; 0x201, HandleInputActionsMove(Ray..)
///   (walk to the ground point).
///   Masks of a mouse press: "new" scheme (MouseOptions 0) left = 0x454, right = 0x28; classic scheme (MouseOptions 1)
///   left = 0x20|0xF8 (or 0x1F0)|0x404 [+0x1 move].
/// Mod (close camera, cursor captured or third-person WoW): Harmony postfix on EvaluateActionsByInput (v7, see Install) -
///   - LeftClickAttack: a left press mask containing 0x4 gets 0x8|0x10 (attack + interact, like the classic scheme),
///   - ClickMove=false: strips the move flags 0x201 (incl. the right-button held/release 0x200 = "walk to the cursor"),
///   - WoW with a free cursor: right press/held -> 0, release -> the press mask (minus move) only for a click without drag,
///   - captured cursor: RightMouse None/Hotkey drop right-button masks (Hotkey is injected by FpKeys as a game key).
/// Interact key: HandleInputActions(screen centre, 0x454, 0, -10) = the game's own left-press "use" action on the crosshair ray.
/// Open windows: GameGui.Instance.IsAnyNoneHudGuiOpenByAlignment(alignment, null) (0x1813D0400) per GuiAlignment
///   (non-HUD guis with IsOpen; the method throws KeyNotFound for alignments without guis - those are skipped afterwards).
/// </summary>
internal static class FpControls
{
    private const short MaskSelect = 0x4, MaskAttack = 0x8, MaskInteract = 0x10, MaskMove = 0x201, UsePress = 0x454;
    private static bool _injecting, _loggedPatch;
    private static int _maskLogs;
    internal static bool HintVisible;

    /// <summary>
    /// v7: the filter moved from a HandleInputActions prefix to a postfix on
    /// LocalInputHandler.EvaluateActionsByInput(int touchId, UICamera.b scheme, g event, out h mods) (VA 0x181997390), which
    /// returns the action mask that OnHandleInput (0x1819A4700) then passes to HandleInputActions. Reason: the HandleInputActions
    /// patch never applied ("Cannot get result from void method": another plugin's prefix on that void method declares
    /// __result, and HarmonyX keeps the failed patch in the method's shared patch list, so every later patch of it fails).
    /// touchId: -1 left, -2 right, -3 middle (NGUI mouse ids); event g: 0 press, 1 release, 2 held
    /// (UpdateInputForButtonHeld 0x1819B3330 calls OnHandleInput(.., 2) every frame for every held button).
    /// Game masks (MouseOptions 0): left press 0x454, right press 0x28, right held/release 0x200 (= walk to the cursor);
    /// classic scheme (1) via EvaluateActionsByMouseInputOriginal. HandleInputActions walks when mask &amp; 0x201.
    /// </summary>
    internal static void Install(Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(LocalInputHandler), "EvaluateActionsByInput");
            if (m == null) { Plugin.L.LogWarning("CameraMod: hook NOT applied: LocalInputHandler.EvaluateActionsByInput not found - close-camera clicks use the game's own mapping (right button walks)"); return; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(FpControls), nameof(EvaluatePostfix)));
            Plugin.L.LogInfo("CameraMod: hooked LocalInputHandler.EvaluateActionsByInput (VA 0x181997390) postfix: close-camera click filter (no click-move, left = attack/use, WoW right button = look only, click on release without drag)");
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: hook NOT applied: EvaluateActionsByInput: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static int _rPressMask;

    private static void EvaluatePostfix(int __0, LocalInputHandler.g __2, ref LocalInputHandler.e __result)
    {
        long t0 = Perf.Begin();
        try
        {
            if (_injecting || !CameraLogic.InCloseCamera) return;
            bool captured = MouseLock.CentreCaptured, wow = CameraLogic.WowMode;
            if (!captured && !wow) return;
            int btn = __0 == -1 ? 0 : __0 == -2 ? 1 : __0 == -3 ? 2 : -1;
            if (btn < 0) return; // touch / controller
            int ev = (int)__2;
            int m = (short)__result, orig = m;
            string note = "";
            if (btn == 1)
            {
                if (wow && !captured)
                {
                    // WoW free cursor: the right button is mouse look. Press and held events never reach the game; the
                    // release becomes the game's right-click action only for a click without drag.
                    if (ev == 0) { _rPressMask = m & ~MaskMove; m = 0; note = " (look: press held back)"; }
                    else if (ev == 2) m = 0;
                    else
                    {
                        bool click = MouseLock.RmbWasClick() && Plugin.RightMouse.Value == RightMouseMode.Game;
                        m = click ? _rPressMask : 0;
                        note = click ? " (click: right press action sent on release)" : " (look drag " + MouseLock.RmbDragPx.ToString("0") + " px / " + MouseLock.RmbHeldSec.ToString("0.00") + " s: no click)";
                        _rPressMask = 0;
                    }
                }
                else if (Plugin.RightMouse.Value != RightMouseMode.Game) m = 0;
                else if (!Plugin.ClickMoveInFp.Value) m &= ~MaskMove;
            }
            else if (btn == 0)
            {
                if (!Plugin.ClickMoveInFp.Value) m &= ~MaskMove;
                if (Plugin.LeftClickAttack.Value && ev == 0 && (m & MaskSelect) != 0) m |= MaskAttack | MaskInteract;
            }
            else if (!Plugin.ClickMoveInFp.Value) m &= ~MaskMove;

            if (m != orig || !_loggedPatch)
            {
                bool log = !_loggedPatch || (ev != 2 && _maskLogs < 12);
                _loggedPatch = true;
                if (log)
                {
                    if (m != orig) _maskLogs++;
                    Plugin.L.LogInfo("CameraMod: " + CameraLogic.ModeName + " click: " + (btn == 0 ? "left" : btn == 1 ? "right" : "middle") + " "
                        + (ev == 0 ? "press" : ev == 1 ? "release" : "held") + " mask 0x" + orig.ToString("X") + " -> 0x" + m.ToString("X") + note);
                }
                __result = (LocalInputHandler.e)(short)m;
            }
        }
        catch (Exception ex) { CameraLogic.Fail("click filter", ex); }
        finally { Perf.End(Perf.Click, t0); }
    }

    internal static void Enter() { HintVisible = Plugin.ShowHintOnEnter.Value; _uiNext = 0; _uiOpen = false; _uiWhich = ""; }

    /// <summary>Per frame in first person: hint toggle and interact key.</summary>
    internal static void Update()
    {
        if (Plugin.HintKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.HintKey.Value) && !FpMovement.Typing()) HintVisible = !HintVisible;
        if (Plugin.InteractKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.InteractKey.Value) && !FpMovement.Typing()) Interact();
    }

    private static int _interactLogs;
    private static void Interact()
    {
        LocalInputHandler h = null;
        try { h = LocalInputHandler.Instance; } catch { }
        if (h == null) { Plugin.L.LogWarning("CameraMod: interact: LocalInputHandler.Instance is null"); return; }
        // the crosshair, or in third-person WoW mode with a free cursor the real cursor
        Vector2 p = CameraLogic.WowMode && !MouseLock.CentreCaptured ? new Vector2(Input.mousePosition.x, Input.mousePosition.y) : new Vector2(Screen.width / 2f, Screen.height / 2f);
        try
        {
            _injecting = true;
            h.HandleInputActions(p, (LocalInputHandler.e)UsePress, (LocalInputHandler.h)0, -10);
            if (_interactLogs++ < 5) Plugin.L.LogInfo($"CameraMod: {CameraLogic.ModeName}: interact ({Plugin.InteractKey.Value}) -> LocalInputHandler.HandleInputActions(screen {p.x:0},{p.y:0}, 0x{UsePress:X})");
        }
        catch (Exception ex) { Plugin.L.LogError("CameraMod: interact failed: " + ex); }
        finally { _injecting = false; }
    }

    // ---- open game windows ----
    private static readonly GuiAlignment[] Alignments =
        { GuiAlignment.Left, GuiAlignment.Right, GuiAlignment.Center, GuiAlignment.Auto, GuiAlignment.LeftTop, GuiAlignment.RightTop,
          GuiAlignment.LeftBottom, GuiAlignment.RightBottom, GuiAlignment.Top, GuiAlignment.Bottom };
    private static readonly HashSet<GuiAlignment> _badAlign = new();
    private static bool _uiOpen, _uiBroken;
    private static string _uiWhich = "";
    private static int _uiNext, _uiErrors;

    /// <summary>Is a (non-HUD) game window open? Checked every 10 frames.</summary>
    internal static bool UiOpen(out string which)
    {
        which = _uiWhich;
        if (_uiBroken) return false;
        int f = Time.frameCount;
        if (f < _uiNext) return _uiOpen;
        _uiNext = f + 10;
        bool open = false; string w = "";
        GameGui gg = null;
        try { gg = GameGui.Instance; } catch (Exception ex) { _uiBroken = true; Plugin.L.LogWarning("CameraMod: GameGui.Instance unavailable, cursor is not freed for windows: " + ex.Message); return false; }
        if (gg != null)
        {
            foreach (var a in Alignments)
            {
                if (_badAlign.Contains(a)) continue;
                try { if (gg.IsAnyNoneHudGuiOpenByAlignment(a, null)) { open = true; w = a.ToString(); break; } }
                catch (Exception ex)
                {
                    _badAlign.Add(a);
                    if (++_uiErrors <= 12) Plugin.L.LogInfo($"CameraMod: window check: alignment {a} skipped ({ex.GetType().Name})");
                    if (_badAlign.Count == Alignments.Length) { _uiBroken = true; Plugin.L.LogWarning("CameraMod: window check failed for every alignment, disabled"); }
                }
            }
        }
        if (open != _uiOpen) Plugin.L.LogInfo(open ? $"CameraMod: first person: game window open ({w}) -> cursor freed" : "CameraMod: first person: game windows closed -> cursor captured");
        _uiOpen = open; _uiWhich = w; which = w;
        return open;
    }
}
