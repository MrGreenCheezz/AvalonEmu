using System;
using UnityEngine;

namespace ActionBarsMod;

/// <summary>
/// Injected MonoBehaviour: per frame pumps the event queue, the hello, icons, HUD state, keys and mouse clicks; OnGUI draws the bars
/// (IMGUI, every string formatted in managed code - see the crash rule in CameraMod NOTES).
/// Layout: bar 1 (Shift) right above the game's spell bar, bar 2 (Ctrl) above it; BarSize buttons per row as the server says.
/// Styles (Layout.Style): Native = cloned game buttons (NativeBar, NGUI), Atlas = IMGUI from the game's button sprites (AtlasSkin),
/// Classic = the first version's flat IMGUI look. Native/Atlas use the game's icon size and button pitch.
/// </summary>
public class BarsOverlay : MonoBehaviour
{
    public BarsOverlay(IntPtr ptr) : base(ptr) { }

    internal static bool Visible;
    internal static string LastRefusal = "";
    internal static float LastRefusalUntil;
    private static bool _guiFailed, _updateFailed;
    private static GUIStyle _center, _corner, _tip, _small;
    private static int _hover = -1;

    internal static int RowsShown => Math.Min(Math.Clamp(Plugin.Rows.Value, 1, 2), Net.BarCount);

    /// <summary>The style in use: Native falls back to Atlas when cloning failed.</summary>
    internal static BarStyle Style
    {
        get
        {
            var s = Plugin.Style.Value;
            if (s == BarStyle.Native && NativeBar.Failed) s = BarStyle.Atlas;
            return s;
        }
    }

    /// <summary>Native/Atlas: the game's own icon size and pitch (measured), so the rows line up with the game's bar.</summary>
    private static bool GameMetrics => Style != BarStyle.Classic && Plugin.AutoAnchor.Value && Hud.HasAnchor && Hud.IconPx > 12;

    private static float Size => GameMetrics ? Hud.IconPx
        : Plugin.AutoSize.Value && Plugin.AutoAnchor.Value && Hud.HasAnchor && Hud.ButtonSize > 12 ? Hud.ButtonSize : Math.Max(16f, Plugin.ButtonSize.Value);

    private static float Gap => GameMetrics ? Math.Max(0f, Hud.Pitch - Hud.IconPx) : Math.Max(0f, Plugin.Spacing.Value);

    /// <summary>GUI rect (top-left origin) of UI bar (0 = Shift bar, bottom row) / position.</summary>
    internal static Rect SlotRect(int bar, int pos)
    {
        float size = Size, sp = Gap, gap = Math.Max(0f, Plugin.RowGap.Value);
        if (GameMetrics) gap = Math.Max(gap, sp); // rows at least as far apart as the buttons
        int cols = Math.Max(1, Net.BarSize);
        float width = cols * size + (cols - 1) * sp;
        bool anchor = Plugin.AutoAnchor.Value && Hud.HasAnchor;
        float cx = (anchor ? Hud.CenterX : Screen.width / 2f) + Plugin.OffsetX.Value;
        float bottom = (anchor ? Hud.TopY - gap : Screen.height - 130f) - Plugin.OffsetY.Value;
        float y = bottom - (bar + 1) * size - bar * gap;
        float x = cx - width / 2f + pos * (size + sp);
        return new Rect(x, y, size, size);
    }

    internal static bool MouseOverBars() => SlotUnderMouse() >= 0;

    /// <summary>Server slot index under the cursor, -1 if none.</summary>
    internal static int SlotUnderMouse()
    {
        if (!Visible) return -1;
        var m = Input.mousePosition;
        float gx = m.x, gy = Screen.height - m.y;
        for (int bar = 0; bar < RowsShown; bar++)
            for (int pos = 0; pos < Net.BarSize; pos++)
            {
                var r = SlotRect(bar, pos);
                if (gx >= r.x && gx < r.x + r.width && gy >= r.y && gy < r.y + r.height) return Net.Index(bar, pos);
            }
        return -1;
    }

    public void Update()
    {
        if (_updateFailed) return;
        try
        {
            Net.Pump();
            Hud.Update();
            Visible = Hud.Visible && Net.BarCount > 0 && Net.BarSize > 0;
            Icons.Update();
            Tooltips.UpdatePanel();
            var style = Style;
            if (style == BarStyle.Atlas) AtlasSkin.Update();
            if (style == BarStyle.Native) NativeBar.Update(Visible);
            else NativeBar.Hide();
            if (!Visible) { Tooltips.UpdateButtons(-1); return; }
            int hover = SlotUnderMouse();
            Tooltips.UpdateButtons(Plugin.ShowTooltips.Value ? hover : -1);
            KeyInput.Update();
            if (Input.GetMouseButtonDown(0) && hover >= 0) Caster.Cast(hover, "click");
        }
        catch (Exception ex)
        {
            _updateFailed = true;
            Plugin.L.LogError("ActionBarsMod: per-frame update failed, mod stopped: " + ex);
        }
    }

    private static void Styles()
    {
        if (_center != null) return;
        _center = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 16, wordWrap = false };
        _small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 10, wordWrap = true };
        _corner = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontSize = 11, fontStyle = FontStyle.Bold, wordWrap = false };
        _tip = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
    }

    private static void Fill(Rect r, Color c)
    {
        var old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    private static void Frame(Rect r, Color c, float t)
    {
        Fill(new Rect(r.x, r.y, r.width, t), c);
        Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
        Fill(new Rect(r.x, r.y, t, r.height), c);
        Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
    }

    private static void Shadowed(Rect r, string text, GUIStyle st, Color c)
    {
        var old = GUI.color;
        GUI.color = new Color(0, 0, 0, c.a);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), text, st);
        GUI.color = c;
        GUI.Label(r, text, st);
        GUI.color = old;
    }

    public void OnGUI()
    {
        if (_guiFailed) return;
        try
        {
            if (Tooltips.PanelVisible) Tooltips.DrawPanel();
            if (!Visible) return;
            Styles();
            float op = Math.Clamp(Plugin.Opacity.Value, 0.1f, 1f);
            long now = Clock.Now();
            float tNow = Time.unscaledTime;
            _hover = -1;
            var mouse = Input.mousePosition;
            float mx = mouse.x, my = Screen.height - mouse.y;
            var style = Style;
            bool native = style == BarStyle.Native && NativeBar.Built;
            bool atlas = style == BarStyle.Atlas && AtlasSkin.Ready;
            for (int bar = 0; bar < RowsShown; bar++)
            {
                for (int pos = 0; pos < Net.BarSize; pos++)
                {
                    int idx = Net.Index(bar, pos);
                    if (idx < 0) continue;
                    var r = SlotRect(bar, pos);
                    bool hover = mx >= r.x && mx < r.xMax && my >= r.y && my < r.yMax;
                    if (hover) _hover = idx;
                    if (native) continue; // the cloned NGUI buttons draw themselves
                    if (atlas) DrawAtlas(idx, bar, pos, r, hover, op, now, tNow);
                    else DrawClassic(idx, bar, pos, r, hover, op, now, tNow);
                }
            }
            if (_hover >= 0 && Plugin.ShowTooltips.Value && Tooltips.ImguiForButtons) Tooltips.DrawButtonTooltip(_hover, mx, my, now);
            if (LastRefusalUntil > tNow && LastRefusal.Length > 0)
            {
                var r0 = SlotRect(RowsShown - 1, 0);
                Shadowed(new Rect(r0.x, r0.y - 22, 600, 20), Catalog.T("Доп. панель: ", "Extra bar: ") + LastRefusal, _corner, new Color(1f, 0.4f, 0.35f, 1f));
            }
        }
        catch (Exception ex)
        {
            _guiFailed = true;
            Plugin.L.LogError("ActionBarsMod: drawing failed, UI off (keys still work): " + ex);
        }
    }

    private static float CooldownFill(SlotInfo s, long now, out string text)
    {
        text = "";
        if (s.CdEnd <= now) return 0;
        long total = Math.Max(1, s.CdStart > 0 && s.CdStart < s.CdEnd ? s.CdEnd - s.CdStart : s.CdEnd - now);
        double sec = (s.CdEnd - now) / 10_000_000.0;
        text = sec >= 10 ? ((int)Math.Ceiling(sec)).ToString() : sec.ToString("0.0");
        return Math.Clamp((float)(s.CdEnd - now) / total, 0f, 1f);
    }

    private static void DrawAtlas(int idx, int bar, int pos, Rect r, bool hover, float op, long now, float tNow)
    {
        var s = Net.Slots[idx];
        bool empty = s.SpellId < 0 || s.Unique.Length == 0;
        Icon icon = null;
        string cdText = "";
        float fill = 0;
        if (!empty)
        {
            var info = Spells.Get(s.SpellId, s.Unique);
            icon = Icons.Get(info?.Sprite ?? s.Unique);
            fill = CooldownFill(s, now, out cdText);
        }
        var tint = fill > 0 ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white;
        string key = Plugin.ShowKeys.Value ? KeyInput.Binds[bar * 6 + pos].Label ?? "" : "";
        bool drew = AtlasSkin.Draw(r, op, icon, fill, tint, key, _corner);
        if (fill > 0 && !drew) Fill(new Rect(r.x, r.y, r.width, r.height * fill), new Color(0, 0, 0, 0.6f * op));
        if (cdText.Length > 0 && Plugin.ShowCooldownNumbers.Value) Shadowed(r, cdText, _center, new Color(1f, 1f, 1f, op));
        if (hover) Fill(r, new Color(1f, 0.95f, 0.8f, 0.10f * op));
        if (s.FlashUntil > tNow)
        {
            float a = Math.Clamp((s.FlashUntil - tNow) / 0.6f, 0f, 1f);
            Fill(r, new Color(0.9f, 0.05f, 0.05f, 0.45f * a * op));
        }
    }

    private static void DrawClassic(int idx, int bar, int pos, Rect r, bool hover, float op, long now, float tNow)
    {
        var s = Net.Slots[idx];
        bool empty = s.SpellId < 0 || s.Unique.Length == 0;
        Fill(r, new Color(0.05f, 0.05f, 0.07f, (empty ? 0.35f : 0.75f) * op));
        if (!empty)
        {
            var info = Spells.Get(s.SpellId, s.Unique);
            var icon = Icons.Get(info?.Sprite ?? s.Unique);
            var inner = new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4);
            if (icon != null)
            {
                var ir = new Rect(inner.x + inner.width * icon.PadL, inner.y + inner.height * icon.PadT,
                    inner.width * (1 - icon.PadL - icon.PadR), inner.height * (1 - icon.PadT - icon.PadB));
                var old = GUI.color;
                GUI.color = new Color(1, 1, 1, op);
                GUI.DrawTexture(ir, icon.Tex);
                GUI.color = old;
            }
            else Shadowed(inner, Abbrev(info?.Display ?? s.Unique), _small, new Color(0.9f, 0.9f, 0.8f, op));
            // cooldown: darkened part shrinking from the top + seconds
            float frac = CooldownFill(s, now, out var txt);
            if (frac > 0)
            {
                Fill(new Rect(inner.x, inner.y, inner.width, inner.height * frac), new Color(0, 0, 0, 0.65f * op));
                Fill(new Rect(inner.x, inner.y + inner.height * frac, inner.width, inner.height * (1 - frac)), new Color(0, 0, 0, 0.25f * op));
                if (Plugin.ShowCooldownNumbers.Value) Shadowed(inner, txt, _center, new Color(1f, 0.95f, 0.6f, op));
            }
        }
        if (s.FlashUntil > tNow)
        {
            float a = Math.Clamp((s.FlashUntil - tNow) / 0.6f, 0f, 1f);
            Fill(r, new Color(0.9f, 0.05f, 0.05f, 0.55f * a * op));
        }
        Frame(r, hover ? new Color(1f, 0.85f, 0.35f, op) : new Color(0.55f, 0.5f, 0.4f, 0.9f * op), hover ? 2f : 1f);
        if (Plugin.ShowKeys.Value)
        {
            string label = KeyInput.Binds[bar * 6 + pos].Label ?? "";
            if (label.Length > 0) Shadowed(new Rect(r.x + 3, r.y + 1, r.width - 4, 16), label, _corner, new Color(1, 1, 1, op));
        }
    }

    private static string Abbrev(string name)
    {
        if (string.IsNullOrEmpty(name)) return "?";
        return name.Length <= 14 ? name : name.Substring(0, 13) + "…";
    }
}
