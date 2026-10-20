using System;
using System.Collections.Generic;
using UnityEngine;

namespace ActionBarsMod;

/// <summary>
/// Style Atlas: IMGUI buttons drawn from the SAME sprites as the game's button. The game's MainHand1 button (Hud.Source) is read as
/// layers: every active, visible UISprite/UILabel under it with its atlas + sprite name, colour, depth and its rectangle relative to the
/// button's Icon widget (screen space). Each sprite is cut once from its atlas (Icons.GetFrom: Blit + ReadPixels, like the icons) and
/// drawn in depth order at our slot rect: the Icon layer becomes the extra spell's icon, the ProgressSprite layer the cooldown (clipped
/// from the top by the remaining fraction), the shortcut label our key with the label's colour and (dynamic) font.
/// Snapshot taken again every 5 s / when the game's button object changes.
/// </summary>
internal static class AtlasSkin
{
    internal sealed class Layer
    {
        public UIAtlas Atlas;
        public string Sprite = "";
        public float X, Y, W, H; // relative to the icon rect (fractions of the icon size; y down)
        public Color Color = Color.white;
        public int Depth;
        public bool IsIcon, IsProgress, IsKeyLabel;
        public Font Font;
        public int FontSize;
    }

    internal static readonly List<Layer> Layers = new();
    internal static bool Ready => Layers.Count > 0 && _hasIcon;
    private static bool _hasIcon, _broken;
    private static IntPtr _src;
    private static float _next;
    private static int _logged;

    /// <summary>Main thread (Update).</summary>
    internal static void Update()
    {
        if (_broken) return;
        var src = Hud.Source;
        var cam = Hud.UiCam;
        if (src == null || cam == null) return;
        float t = Time.unscaledTime;
        if (src.Pointer == _src && t < _next) { Request(); return; }
        _next = t + 5f;
        try { Snapshot(src, cam); _src = src.Pointer; }
        catch (Exception ex) { _broken = true; Plugin.L.LogWarning("ActionBarsMod: reading the game's button sprites failed, Style Atlas draws the classic look: " + ex.Message); }
    }

    private static void Request()
    {
        foreach (var l in Layers) if (!l.IsIcon && !l.IsKeyLabel && l.Atlas != null && l.Sprite.Length > 0) Icons.GetFrom(l.Atlas, l.Sprite);
    }

    private static Rect ScreenRect(UIWidget w, Camera cam)
    {
        var c = w.worldCorners;
        if (c == null || c.Length < 4) return default;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int k = 0; k < 4; k++)
        {
            var p = cam.WorldToScreenPoint(c[k]);
            minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
        }
        return new Rect(minX, Screen.height - maxY, maxX - minX, maxY - minY); // GUI space
    }

    private static void Snapshot(UISpellSlot src, Camera cam)
    {
        Layers.Clear();
        _hasIcon = false;
        var icon = src.Icon;
        if (icon == null) return;
        var ir = ScreenRect(icon, cam);
        if (ir.width < 4 || ir.height < 4) return;
        IntPtr iconP = icon.Pointer, progP = src.ProgressSprite != null ? src.ProgressSprite.Pointer : IntPtr.Zero;
        var keyLabels = new HashSet<IntPtr>();
        var kl = src.ShortcutLabels;
        if (kl != null) for (int i = 0; i < kl.Length; i++) if (kl[i] != null) keyLabels.Add(kl[i].Pointer);
        var cont = Hud.SourceContainer;
        var ws = cont != null ? cont.GetComponentsInChildren<UIWidget>(false) : src.GetComponentsInChildren<UIWidget>(false);
        if (ws == null) return;
        var names = new List<string>();
        foreach (var w in ws)
        {
            if (w == null || !w.enabled) continue;
            bool isIcon = w.Pointer == iconP, isProg = w.Pointer == progP;
            if (!isIcon && !isProg && w.finalAlpha < 0.02f) continue;
            var r = ScreenRect(w, cam);
            if (r.width < 1 || r.height < 1) continue;
            var l = new Layer
            {
                X = (r.x - ir.x) / ir.width, Y = (r.y - ir.y) / ir.height, W = r.width / ir.width, H = r.height / ir.height,
                Depth = w.depth, IsIcon = isIcon, IsProgress = isProg,
            };
            try { l.Color = w.color; } catch { }
            var sp = w.TryCast<UISprite>();
            if (sp != null)
            {
                l.Atlas = sp.atlas;
                l.Sprite = sp.spriteName ?? "";
                if (!isIcon && (l.Atlas == null || l.Sprite.Length == 0)) continue;
            }
            else
            {
                var lab = w.TryCast<UILabel>();
                if (lab == null || !keyLabels.Contains(lab.Pointer)) continue; // other labels: stack size etc.
                l.IsKeyLabel = true;
                try { l.Font = lab.trueTypeFont; } catch { }
                try { l.FontSize = lab.fontSize; } catch { }
            }
            if (isIcon) _hasIcon = true;
            Layers.Add(l);
            if (names.Count < 16) names.Add((isIcon ? "ICON " : isProg ? "CD " : l.IsKeyLabel ? "KEY " : "") + l.Sprite + "@" + l.Depth);
        }
        Layers.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        Request();
        if (_logged++ < 3) Plugin.L.LogInfo($"ActionBarsMod: Style Atlas: {Layers.Count} layers of the game's button: {string.Join(", ", names)}");
    }

    /// <summary>OnGUI: one button (r = the icon rect). Returns whether the game's cooldown sprite was drawn (else the caller darkens).</summary>
    internal static bool Draw(Rect r, float op, Icon spellIcon, float cdFill, Color iconTint, string key, GUIStyle keyStyle)
    {
        var old = GUI.color;
        bool drewProgress = false;
        foreach (var l in Layers)
        {
            var lr = new Rect(r.x + l.X * r.width, r.y + l.Y * r.height, l.W * r.width, l.H * r.height);
            if (l.IsIcon)
            {
                if (spellIcon == null) continue;
                var ir = new Rect(lr.x + lr.width * spellIcon.PadL, lr.y + lr.height * spellIcon.PadT,
                    lr.width * (1 - spellIcon.PadL - spellIcon.PadR), lr.height * (1 - spellIcon.PadT - spellIcon.PadB));
                GUI.color = new Color(iconTint.r, iconTint.g, iconTint.b, iconTint.a * op);
                GUI.DrawTexture(ir, spellIcon.Tex);
                continue;
            }
            if (l.IsKeyLabel)
            {
                if (string.IsNullOrEmpty(key)) continue;
                var st = keyStyle;
                if (l.Font != null && st.font != l.Font) st.font = l.Font;
                GUI.color = new Color(0, 0, 0, op);
                GUI.Label(new Rect(lr.x + 1, lr.y + 1, Math.Max(lr.width, 30), lr.height), key, st);
                GUI.color = new Color(l.Color.r, l.Color.g, l.Color.b, op);
                GUI.Label(new Rect(lr.x, lr.y, Math.Max(lr.width, 30), lr.height), key, st);
                continue;
            }
            var tex = Icons.GetFrom(l.Atlas, l.Sprite);
            if (tex == null) continue;
            if (l.IsProgress)
            {
                if (cdFill <= 0.001f) continue;
                // the cooldown cover, squeezed to the remaining fraction from the top (GUI.BeginGroup(Rect) is stripped in this build)
                GUI.color = new Color(l.Color.r, l.Color.g, l.Color.b, Math.Max(0.6f, l.Color.a) * op);
                GUI.DrawTexture(new Rect(lr.x, lr.y, lr.width, lr.height * cdFill), tex.Tex);
                drewProgress = true;
                continue;
            }
            GUI.color = new Color(l.Color.r, l.Color.g, l.Color.b, l.Color.a * op);
            GUI.DrawTexture(lr, tex.Tex);
        }
        GUI.color = old;
        return drewProgress;
    }
}
