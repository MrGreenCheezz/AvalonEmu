using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using AlbionGui.Hud;

namespace ActionBarsMod;

/// <summary>
/// Where the game's spell bar is and whether the HUD is shown. SpellButtonGui (HudElementGuiBase, TypeDefIndex 1299) holds the six
/// UISpellSlotContainer of the weapon/armour spells (MainHand1 @0x38 .. Shoes @0x60). Its NGUI widgets' worldCorners projected with
/// the camera of their layer (UICamera.FindCameraForLayer(layer).cachedCamera) give the bar's screen rectangle. Hidden when there is
/// no local player view (loading), no active SpellButtonGui, or its widgets are fully transparent (cinematics fade the HUD).
/// Measured every 0.5 s.
/// </summary>
internal static class Hud
{
    internal static bool Visible, HasAnchor;
    internal static float CenterX, TopY, ButtonSize; // GUI coordinates (top-left origin)
    /// <summary>The game's own button (MainHand1's normal UISpellSlot): the clone source / sprite source of the native styles.</summary>
    internal static UISpellSlot Source;
    /// <summary>Its container (MainHand1: frame, Normal + Vanity slot, transition overlays) = what NativeBar clones.</summary>
    internal static UISpellSlotContainer SourceContainer;
    internal static Camera UiCam;
    /// <summary>Native look: the game's icon size and the distance between two neighbouring buttons, in screen pixels (0 = not measured).</summary>
    internal static float IconPx, Pitch;
    internal static SpellButtonGui Gui => _gui;
    private static SpellButtonGui _gui;
    private static float _next, _nextFind;
    private static bool _anchorBroken, _loggedAnchor;
    private static int _fails;

    internal static void Update()
    {
        float t = Time.unscaledTime;
        if (t < _next) return;
        _next = t + 0.5f;
        try
        {
            LocalPlayerCharacterView view = null;
            if (!LocalPlayerCharacterView.TryGet(out view) || view == null) { Visible = false; return; }
            if ((_gui == null || _gui.Pointer == IntPtr.Zero || !_gui) && t >= _nextFind)
            {
                _nextFind = t + 2f;
                var o = UnityEngine.Object.FindObjectOfType(Il2CppType.Of<SpellButtonGui>());
                _gui = o?.TryCast<SpellButtonGui>();
            }
            if (_gui == null || !_gui) { Visible = false; return; }
            var go = _gui.gameObject;
            Visible = go != null && go.activeInHierarchy;
            if (Visible && !_anchorBroken) Measure();
        }
        catch (Exception ex)
        {
            if (_fails++ < 3) Plugin.L.LogWarning("ActionBarsMod: HUD check failed: " + ex.Message);
            Visible = true; // never hide the bars because of our own error
        }
    }

    private static void Measure()
    {
        try
        {
            UISpellSlotContainer[] cs = { _gui.MainHand1, _gui.MainHand2, _gui.OffHand, _gui.Armor, _gui.Head, _gui.Shoes };
            float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int widgets = 0, active = 0;
            float alpha = 0;
            Camera cam = null;
            UISpellSlot first = null;
            UISpellSlotContainer firstC = null;
            var centers = new System.Collections.Generic.List<float>();
            float iconPx = 0;
            foreach (var c in cs)
            {
                if (c == null || !c) continue;
                var cgo = c.gameObject;
                if (cgo == null || !cgo.activeInHierarchy) continue;
                active++;
                if (cam == null)
                {
                    var uic = UICamera.FindCameraForLayer(cgo.layer);
                    cam = uic != null ? uic.cachedCamera : null;
                    if (cam == null) return;
                }
                try
                {
                    var slot = c.Normal;
                    var icon = slot != null ? slot.Icon : null;
                    if (icon != null && icon.gameObject.activeInHierarchy)
                    {
                        if (first == null) { first = slot; firstC = c; }
                        var ic = icon.worldCorners;
                        if (ic != null && ic.Length >= 4)
                        {
                            var a = cam.WorldToScreenPoint(ic[0]); var b = cam.WorldToScreenPoint(ic[2]);
                            centers.Add((a.x + b.x) / 2f);
                            iconPx = Math.Max(iconPx, Math.Abs(b.x - a.x));
                        }
                    }
                }
                catch (Exception ex) { if (_fails++ < 3) Plugin.L.LogWarning("ActionBarsMod: reading the game's button failed: " + ex.Message); }
                var ws = cgo.GetComponentsInChildren<UIWidget>(false);
                if (ws == null) continue;
                foreach (var w in ws)
                {
                    if (w == null || !w.enabled) continue;
                    var corners = w.worldCorners;
                    if (corners == null || corners.Length < 4) continue;
                    alpha = Math.Max(alpha, w.finalAlpha);
                    for (int k = 0; k < 4; k++)
                    {
                        var sp = cam.WorldToScreenPoint(corners[k]);
                        minX = Math.Min(minX, sp.x); maxX = Math.Max(maxX, sp.x); maxY = Math.Max(maxY, sp.y);
                    }
                    widgets++;
                }
            }
            if (widgets == 0 || maxX <= minX) { HasAnchor = false; return; }
            if (alpha < 0.02f) Visible = false;
            CenterX = (minX + maxX) / 2f;
            TopY = Screen.height - maxY;
            ButtonSize = active > 0 ? (maxX - minX) / Math.Max(6, active) * 0.8f : 0;
            HasAnchor = TopY > 0 && TopY < Screen.height;
            UiCam = cam;
            Source = first;
            SourceContainer = firstC;
            IconPx = iconPx;
            centers.Sort();
            float pitch = 0;
            for (int k = 1; k < centers.Count; k++)
            {
                float d = centers[k] - centers[k - 1];
                if (d > iconPx * 0.5f && (pitch == 0 || d < pitch)) pitch = d;
            }
            Pitch = pitch > 0 ? pitch : iconPx * 1.15f;
            if (!_loggedAnchor && HasAnchor)
            {
                _loggedAnchor = true;
                Plugin.L.LogInfo($"ActionBarsMod: game spell bar at x {minX:0}..{maxX:0}, top {TopY:0} px from the screen top ({widgets} widgets, {active} slots, icon {IconPx:0} px, pitch {Pitch:0} px, screen {Screen.width}x{Screen.height})");
            }
        }
        catch (Exception ex)
        {
            _anchorBroken = true; HasAnchor = false;
            Plugin.L.LogWarning("ActionBarsMod: measuring the game's spell bar failed, using the config offsets: " + ex.Message);
        }
    }
}
