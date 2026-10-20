using System;
using System.Collections.Generic;
using System.Text;
using Albion.Common.GameData;
using HarmonyLib;
using UnityEngine;

namespace ActionBarsMod;

public enum TooltipMode { Game, Mod, Off }

/// <summary>
/// Tooltips:
///  1. Our buttons (TooltipMode Game): the game's own spell tooltip, exactly as UISpellSlotBase.OnTooltip (0x1814BCDF0) shows it for a
///     normal button: data = gdn.h(bi1, false, 100, 100, 100, 100, null, null, null) (0x181CB1EB0), then
///     GameGui.Instance.ProximityTooltipManager (GameGui +0x130) .Show(ProximityTooltipType.Spell (28), data, false, true) (0x1815F58E0)
///     and .Close(Spell) when the cursor leaves. Mod (or when that fails): an IMGUI tooltip with the same texts (name, stat lines, description).
///  2. Item hover tooltips: AlbionItem.OnTooltip(bool) (0x1812E1610; inventory, equipment, bank, market, loot, chests, trade) shows a text
///     tooltip with UITooltip.Show (static) -> SetText. Prefix on AlbionItem.OnTooltip / UIItemSlot.OnTooltip (0x1814EC400) reads the hovered
///     item (AlbionItem.ItemInfoDisplayed = bdi @0x108); the postfix appends "Доп. способности" (NGUI BBCode) to the shown tooltip label
///     (UITooltip.a .text, stored text @0x68, container.ForceUpdateAnchorsNow) - icons are not possible in that text label.
///  3. The item info window (ItemDetailsWindow.Show(dn7, ...) postfix remembers the window; its item = window.c (dn7 @0x208) .b8() = bdi):
///     an IMGUI panel next to the window (WindowWidget rectangle) lists the extra spells with their icons, names and descriptions.
/// </summary>
internal static class Tooltips
{
    // ---- 1. our buttons

    private static int _gameTipFor = -1;
    private static bool _gameTipBroken;

    /// <summary>Per frame (main thread): opens/closes the game's spell tooltip for the hovered extra slot.</summary>
    internal static void UpdateButtons(int hover)
    {
        bool want = hover >= 0 && Plugin.SpellTooltips.Value == TooltipMode.Game && !_gameTipBroken;
        int id = -1;
        SpellInfo info = null;
        if (want)
        {
            var s = Net.Slots[hover];
            if (s.SpellId < 0 || s.Unique.Length == 0) want = false;
            else { info = Spells.Get(s.SpellId, s.Unique); id = hover * 100000 + s.SpellId; if (info?.Spell == null) want = false; }
        }
        if (!want) { CloseGame(); return; }
        if (_gameTipFor == id) return;
        CloseGame();
        try
        {
            var text = Spells.Describe(info);
            if (text?.Data == null) { _gameTipBroken = true; Plugin.L.LogWarning("ActionBarsMod: the game's spell tooltip data could not be built, IMGUI tooltips instead"); return; }
            var mgr = GameGui.Instance?.ProximityTooltipManager;
            if (mgr == null) return;
            mgr.Show(ProximityTooltipType.Spell, text.Data, false, true);
            _gameTipFor = id;
        }
        catch (Exception ex) { _gameTipBroken = true; Plugin.L.LogWarning("ActionBarsMod: the game's spell tooltip failed, IMGUI tooltips instead: " + ex.Message); }
    }

    private static void CloseGame()
    {
        if (_gameTipFor < 0) return;
        _gameTipFor = -1;
        try { GameGui.Instance?.ProximityTooltipManager?.Close(ProximityTooltipType.Spell); } catch { }
    }

    /// <summary>Whether OnGUI must draw the IMGUI tooltip for the hovered button.</summary>
    internal static bool ImguiForButtons => Plugin.SpellTooltips.Value == TooltipMode.Mod || Plugin.SpellTooltips.Value == TooltipMode.Game && _gameTipBroken;

    // ---- 2. item hover text

    [ThreadStatic] private static string _tipItem;
    [ThreadStatic] private static int _tipQuality;
    private static int _sections;

    /// <summary>The item (and quality) hovered last: the item info window shows no quality, it takes this one for the same item.</summary>
    private static string _lastItem = "";
    private static int _lastQuality = 1;

    /// <summary>A hover tooltip shown while its 9005 answer was still missing: the key, the text it had, when.</summary>
    private static string _waitKey, _waitBase, _waitItem;
    private static int _waitQuality;
    private static float _waitAt;

    private static bool Enabled => Plugin.ItemTooltips.Value && (Catalog.Received || ItemExtras.Available);

    private static int Quality(AlbionItem ai)
    {
        try { return ai != null ? Math.Clamp((int)ai.ShownQuality, 1, 5) : 1; } catch { return 1; }
    }

    internal static void Install(Harmony h)
    {
        Patch(h, AccessTools.DeclaredMethod(typeof(AlbionItem), "OnTooltip", new[] { typeof(bool) }), nameof(AlbionItemPrefix), nameof(TipPostfix),
            "AlbionItem.OnTooltip (VA 0x1812E1610)");
        Patch(h, AccessTools.DeclaredMethod(typeof(UIItemSlot), "OnTooltip", new[] { typeof(bool) }), nameof(ItemSlotPrefix), nameof(TipPostfix),
            "UIItemSlot.OnTooltip (VA 0x1814EC400)");
        Patch(h, AccessTools.DeclaredMethod(typeof(ItemDetailsWindow), "Show"), null, nameof(DetailsPostfix), "ItemDetailsWindow.Show (item info window)");
    }

    private static void Patch(Harmony h, System.Reflection.MethodInfo m, string prefix, string postfix, string what)
    {
        try
        {
            if (m == null) { Plugin.L.LogWarning("ActionBarsMod: hook NOT applied (item tooltips): " + what + " not found"); return; }
            h.Patch(m, prefix: prefix != null ? new HarmonyMethod(typeof(Tooltips), prefix) : null, postfix: postfix != null ? new HarmonyMethod(typeof(Tooltips), postfix) : null);
            Plugin.L.LogInfo("ActionBarsMod: hooked " + what);
        }
        catch (Exception ex) { Plugin.L.LogWarning("ActionBarsMod: hook NOT applied (item tooltips): " + what + ": " + ex.GetType().Name + ": " + ex.Message); }
    }

    private static void AlbionItemPrefix(AlbionItem __instance, bool __0, out string __state)
    {
        __state = _tipItem;
        try
        {
            if (!__0 || !Enabled) return;
            var info = __instance.ItemInfoDisplayed;
            _tipItem = info != null ? Catalog.ItemName(info.Pointer) : null;
            _tipQuality = Quality(__instance);
        }
        catch { }
    }

    private static void ItemSlotPrefix(UIItemSlot __instance, bool __0, out string __state)
    {
        __state = _tipItem;
        try
        {
            if (!__0 || !Enabled) return;
            var ai = __instance.GetComponentInChildren<AlbionItem>(true);
            var info = ai != null ? ai.ItemInfoDisplayed : null;
            if (info != null) { _tipItem = Catalog.ItemName(info.Pointer); _tipQuality = Quality(ai); }
        }
        catch { }
    }

    /// <summary>
    /// After the game showed the item's text tooltip (UITooltip.Show -> SetText 0x181755770: stores the text @0x68, label.text, then
    /// container.ForceUpdateAnchorsNow): the same three steps again with our section appended. UITooltip.Show itself is NOT hooked: its
    /// Nullable&lt;int&gt; parameter would make the Harmony trampoline box with a possibly zero class pointer (CameraMod NOTES rule 2).
    /// </summary>
    private static void TipPostfix(bool __0, string __state)
    {
        string item = _tipItem;
        _tipItem = __state;
        if (!__0 || string.IsNullOrEmpty(item)) return;
        int quality = _tipQuality <= 0 ? 1 : _tipQuality;
        _lastItem = item; _lastQuality = quality;
        try
        {
            string add = HoverSection(item, quality, out bool pending);
            if (add.Length == 0)
            {
                if (!pending) return;
                // The server's answer is on its way: append the section when it comes, if this tooltip is still shown (ExtrasArrived).
                var t0 = TooltipInstance();
                _waitKey = ItemExtras.Key(item, quality); _waitItem = item; _waitQuality = quality; _waitAt = Time.unscaledTime;
                _waitBase = t0 != null && t0.text != null ? t0.text.text ?? "" : "";
                return;
            }
            if (Append(add) && _sections++ < 5) Plugin.L.LogInfo($"ActionBarsMod: item tooltip of {item}: extra abilities section added");
        }
        catch (Exception ex) { if (_sections++ < 5) Plugin.L.LogWarning("ActionBarsMod: item tooltip section failed: " + ex.Message); }
    }

    /// <summary>Appends <paramref name="add"/> to the shown text tooltip (label text, stored text @0x68, container anchors); false when none is shown.</summary>
    private static bool Append(string add, string expect = null)
    {
        var tip = TooltipInstance();
        var label = tip != null ? tip.text : null;
        if (label == null) return false;
        string cur = label.text ?? "";
        if (cur.Length == 0 || cur.Contains(add)) return false;
        if (expect != null && cur != expect) return false; // another tooltip by now
        string next = cur + add;
        label.text = next;
        tip.f = next;
        var cont = tip.container;
        if (cont != null) cont.ForceUpdateAnchorsNow();
        return true;
    }

    /// <summary>Event 9005 came (main thread): the hover tooltip that waited for it gets its section now, if it is still the one shown.</summary>
    internal static void ExtrasArrived(string key)
    {
        if (_waitKey == null || !string.Equals(_waitKey, key, StringComparison.OrdinalIgnoreCase)) return;
        string item = _waitItem, expect = _waitBase;
        int quality = _waitQuality;
        bool fresh = Time.unscaledTime - _waitAt < 15f;
        _waitKey = null;
        if (!fresh || string.IsNullOrEmpty(expect)) return;
        try
        {
            string add = HoverSection(item, quality, out _);
            if (add.Length > 0 && Append(add, expect) && _sections++ < 8) Plugin.L.LogInfo($"ActionBarsMod: item tooltip of {item}: section added when the server answered");
        }
        catch (Exception ex) { if (_sections++ < 8) Plugin.L.LogWarning("ActionBarsMod: late item tooltip section failed: " + ex.Message); }
    }

    private static System.Reflection.PropertyInfo _tipProp;

    /// <summary>The UITooltip singleton (static field a; the name clashes with the nested enum a, so by reflection).</summary>
    private static UITooltip TooltipInstance()
    {
        _tipProp ??= typeof(UITooltip).GetProperty("a", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return _tipProp?.GetValue(null) as UITooltip;
    }

    private static readonly Dictionary<string, (string text, float made)> _hoverCache = new();

    internal static void ClearCache() => _hoverCache.Clear();

    /// <summary>
    /// What <paramref name="item"/> of <paramref name="quality"/> grants: the server's rule answer (9005) when cached, else the explicit catalog
    /// (9004) at once while the answer is asked for (<paramref name="pending"/>).
    /// </summary>
    internal static List<Catalog.Entry> Entries(string item, int quality, out bool pending, out float made)
    {
        var r = ItemExtras.Get(item, quality);
        made = r?.Made ?? -1f;
        pending = r == null && ItemExtras.Available;
        return r != null ? r.Spells : Catalog.For(item);
    }

    /// <summary>The section (NGUI BBCode): title, then per spell its name, the rule in grey and a short description; "" when the item grants none.</summary>
    private static string HoverSection(string item, int quality, out bool pending)
    {
        var list = Entries(item, quality, out pending, out float made);
        if (list.Count == 0) return "";
        float now = Time.unscaledTime;
        string key = ItemExtras.Key(item, quality) + "|" + made;
        if (_hoverCache.TryGetValue(key, out var c) && now - c.made < 10f) return c.text;
        var sb = new StringBuilder();
        bool colors = Plugin.ItemTooltipColors.Value;
        sb.Append("\n\n").Append(colors ? "[c8a065]" : "").Append(Catalog.T("Доп. способности", "Extra abilities")).Append(colors ? "[-]" : "");
        foreach (var e in list)
        {
            var info = Spells.Get(e.SpellId, e.Spell);
            var t = Spells.Describe(info);
            string name = t != null && t.Name.Length > 0 ? BBCode.Plain(t.Name) : info?.Display ?? e.Spell;
            sb.Append('\n').Append(colors ? "[ffffff]" : "").Append("• ").Append(name).Append(colors ? "[-]" : "");
            string why = ItemExtras.LabelText(e.Label);
            if (why.Length > 0) sb.Append(colors ? "  [808080](" : "  (").Append(why).Append(colors ? ")[-]" : ")");
            string d = BBCode.Short(BBCode.Plain(t?.Description ?? ""), Plugin.ItemTooltipChars.Value);
            if (d.Length > 0) sb.Append('\n').Append(colors ? "[b4aa96]" : "").Append(d).Append(colors ? "[-]" : "");
        }
        string s = sb.ToString();
        _hoverCache[key] = (s, now);
        return s;
    }

    // ---- 3. item info window

    private static ItemDetailsWindow _window;
    internal static bool PanelVisible;
    internal static Rect WindowRect;
    internal static string PanelItem = "";
    internal static List<Catalog.Entry> PanelSpells = new();
    private static float _panelAlpha;
    private static int _panelLogs;

    private static void DetailsPostfix(ItemDetailsWindow __instance)
    {
        try { _window = __instance; } catch { }
    }

    /// <summary>Per frame: where the item info window is and which item it shows.</summary>
    internal static void UpdatePanel()
    {
        PanelVisible = false;
        var w = _window;
        if (w == null || !Enabled) return;
        try
        {
            if (!w) { _window = null; return; }
            var go = w.gameObject;
            if (go == null || !go.activeInHierarchy) return;
            var proxy = w.c;
            var bdi = proxy != null ? proxy.b8() : null;
            string item = bdi != null ? Catalog.ItemName(bdi.Pointer) : "";
            if (item.Length == 0) return;
            int quality = string.Equals(item, _lastItem, StringComparison.OrdinalIgnoreCase) ? _lastQuality : 1;
            var list = Entries(item, quality, out _, out _);
            if (list.Count == 0) return;
            var ww = w.WindowWidget;
            if (ww == null) return;
            var uic = UICamera.FindCameraForLayer(ww.gameObject.layer);
            var cam = uic != null ? uic.cachedCamera : null;
            if (cam == null) return;
            var c = ww.worldCorners;
            if (c == null || c.Length < 4) return;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                var p = cam.WorldToScreenPoint(c[k]);
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
            }
            _panelAlpha = ww.finalAlpha;
            if (_panelAlpha < 0.05f || maxX - minX < 20) return;
            WindowRect = new Rect(minX, Screen.height - maxY, maxX - minX, maxY - minY);
            PanelItem = item;
            PanelSpells = list;
            PanelVisible = true;
            foreach (var e in list) { var info = Spells.Get(e.SpellId, e.Spell); Icons.Get(info?.Sprite ?? e.Spell); }
            if (_panelLogs++ < 3) Plugin.L.LogInfo($"ActionBarsMod: item info window shows {item}: {list.Count} extra spell(s), panel next to it");
        }
        catch (Exception ex) { if (_panelLogs++ < 5) Plugin.L.LogWarning("ActionBarsMod: item info window check failed: " + ex.Message); }
    }

    // ---- IMGUI drawing (OnGUI)

    private static GUIStyle _title, _body, _stat, _head, _small;
    private static Font _font;

    private static void Styles()
    {
        if (_title == null)
        {
            _title = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, wordWrap = false, richText = true, alignment = TextAnchor.UpperLeft };
            _head = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, wordWrap = false, richText = true, alignment = TextAnchor.UpperLeft };
            _body = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, richText = true, alignment = TextAnchor.UpperLeft };
            _stat = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false, richText = true, alignment = TextAnchor.UpperLeft };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true, richText = true, alignment = TextAnchor.UpperLeft };
        }
        if (_font != null || _fontTried > 3 || _window == null) return;
        _fontTried++;
        try
        {
            // the game's dynamic font (its windows' labels use it): the text then looks like the game's own
            var lab = _window.ItemName;
            _font = lab != null ? lab.trueTypeFont : null;
            if (_font != null) { _title.font = _font; _head.font = _font; _body.font = _font; _stat.font = _font; _small.font = _font; Plugin.L.LogInfo("ActionBarsMod: tooltips use the game's font " + _font.name); }
        }
        catch { }
    }

    private static int _fontTried;

    // colours of the game's tooltips: gold title, light body, beige-grey stats
    internal static readonly Color Gold = new(0.86f, 0.70f, 0.38f, 1f), Body = new(0.86f, 0.84f, 0.78f, 1f), Stat = new(0.72f, 0.68f, 0.6f, 1f);
    internal static readonly Color Bg = new(0.07f, 0.06f, 0.05f, 0.94f), Edge = new(0.55f, 0.45f, 0.28f, 1f);

    private static float Lines(string plain, float width, int fontSize)
    {
        if (string.IsNullOrEmpty(plain)) return 0;
        float perLine = Math.Max(8f, width / (fontSize * 0.53f));
        float n = 0;
        foreach (var part in plain.Split('\n')) n += Math.Max(1f, (float)Math.Ceiling(part.Length / perLine));
        return n;
    }

    internal static void Fill(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = o; }

    internal static void Frame(Rect r, Color c)
    {
        Fill(new Rect(r.x, r.y, r.width, 1), c); Fill(new Rect(r.x, r.yMax - 1, r.width, 1), c);
        Fill(new Rect(r.x, r.y, 1, r.height), c); Fill(new Rect(r.xMax - 1, r.y, 1, r.height), c);
    }

    private static void Text(Rect r, string rich, GUIStyle st, Color c)
    {
        var o = GUI.color;
        GUI.color = new Color(0, 0, 0, c.a * 0.8f);
        GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), BBCode.Plain(rich), st);
        GUI.color = c;
        GUI.Label(r, rich, st);
        GUI.color = o;
    }

    /// <summary>The IMGUI spell tooltip of an extra button (name, stats, description, source item, cooldown).</summary>
    internal static void DrawButtonTooltip(int idx, float mx, float my, long now)
    {
        Styles();
        var s = Net.Slots[idx];
        const float W = 340f;
        if (s.SpellId < 0 || s.Unique.Length == 0)
        {
            string e = Catalog.T("Доп. ячейка ", "Extra slot ") + Net.SlotName(idx) + Catalog.T(": пусто. Наденьте предмет с доп. способностью.", ": empty. Wear an item that grants an extra ability.");
            float h0 = Lines(e, W - 16, 13) * 17 + 14;
            var r0 = Place(mx, my, W, h0);
            Fill(r0, Bg); Frame(r0, Edge);
            Text(new Rect(r0.x + 8, r0.y + 6, W - 16, h0), e, _body, Body);
            return;
        }
        var info = Spells.Get(s.SpellId, s.Unique);
        var t = Spells.Describe(info);
        string name = t != null && t.Name.Length > 0 ? t.Name : info?.Display ?? s.Unique;
        var stats = new List<string>();
        if (t != null) foreach (var line in t.Stats) stats.Add(BBCode.Rich(line));
        string desc = BBCode.Rich(t?.Description ?? "");
        var extra = new List<string>();
        if (s.Item.Length > 0)
        {
            string why = ItemExtras.LabelText(s.Label);
            extra.Add(Catalog.T("Даёт: ", "Granted by: ") + Spells.ItemName(s.Item) + (why.Length > 0 ? " (" + why + ")" : ""));
        }
        else if (s.Source == 2) extra.Add(Catalog.T("Назначено плагином / ГМ", "Set by a plugin / GM"));
        if (s.CdEnd > now) extra.Add(Catalog.T("Перезарядка: ", "Cooldown: ") + ((s.CdEnd - now) / 10_000_000.0).ToString("0.0") + Catalog.T(" с", " s"));
        float h = 8 + 20 + stats.Count * 16 + (stats.Count > 0 ? 4 : 0) + Lines(BBCode.Plain(desc), W - 16, 13) * 17 + (desc.Length > 0 ? 6 : 0) + extra.Count * 16 + 8;
        var r = Place(mx, my, W, h);
        Fill(r, Bg); Frame(r, Edge);
        Icon ic = Icons.Get(info?.Sprite ?? s.Unique);
        float y = r.y + 6, x = r.x + 8;
        if (ic != null) { GUI.DrawTexture(new Rect(x, y, 20, 20), ic.Tex); x += 24; }
        Text(new Rect(x, y, W - (x - r.x) - 8, 22), BBCode.Rich(name), _title, Gold);
        y += 22;
        foreach (var st in stats) { Text(new Rect(r.x + 8, y, W - 16, 18), st, _stat, Stat); y += 16; }
        if (stats.Count > 0) y += 4;
        if (desc.Length > 0)
        {
            float dh = Lines(BBCode.Plain(desc), W - 16, 13) * 17;
            Text(new Rect(r.x + 8, y, W - 16, dh + 4), desc, _body, Body);
            y += dh + 6;
        }
        foreach (var e in extra) { Text(new Rect(r.x + 8, y, W - 16, 18), e, _stat, Stat); y += 16; }
    }

    private static Rect Place(float mx, float my, float w, float h)
    {
        float x = Math.Min(mx + 18, Screen.width - w - 4), y = Math.Max(4, my - h - 14);
        return new Rect(Math.Max(4, x), y, w, h);
    }

    /// <summary>The panel next to the item info window.</summary>
    internal static void DrawPanel()
    {
        if (!PanelVisible || PanelSpells.Count == 0) return;
        Styles();
        const float W = 320f, Icon = 40f;
        float a = Math.Clamp(_panelAlpha, 0f, 1f);
        var rows = new List<(SpellInfo info, string name, string desc, float h)>();
        float total = 8 + 22;
        foreach (var e in PanelSpells)
        {
            var info = Spells.Get(e.SpellId, e.Spell);
            var t = Spells.Describe(info);
            string name = t != null && t.Name.Length > 0 ? t.Name : info?.Display ?? e.Spell;
            string desc = BBCode.Rich(BBCode.Short(t?.Description ?? "", Plugin.ItemTooltipChars.Value));
            float dh = Lines(BBCode.Plain(desc), W - Icon - 22, 12) * 15;
            float h = Math.Max(Icon + 6, 20 + dh + 6);
            rows.Add((info, name, desc, h));
            total += h;
        }
        total += 6;
        float x = WindowRect.xMax + 6;
        if (x + W > Screen.width - 4) x = WindowRect.x - W - 6;
        if (x < 4) x = Math.Min(Screen.width - W - 4, WindowRect.xMax - W);
        float y = Math.Clamp(WindowRect.y, 4, Math.Max(4, Screen.height - total - 4));
        var r = new Rect(x, y, W, total);
        Fill(r, new Color(Bg.r, Bg.g, Bg.b, Bg.a * a)); Frame(r, new Color(Edge.r, Edge.g, Edge.b, a));
        Text(new Rect(r.x + 8, r.y + 6, W - 16, 22), Catalog.T("Доп. способности", "Extra abilities"), _head, new Color(Gold.r, Gold.g, Gold.b, a));
        float yy = r.y + 30;
        foreach (var row in rows)
        {
            var ic = Icons.Get(row.info?.Sprite ?? "");
            var ir = new Rect(r.x + 8, yy + 2, Icon, Icon);
            Fill(ir, new Color(0, 0, 0, 0.6f * a));
            if (ic != null)
            {
                var o = GUI.color; GUI.color = new Color(1, 1, 1, a);
                GUI.DrawTexture(new Rect(ir.x + ir.width * ic.PadL, ir.y + ir.height * ic.PadT, ir.width * (1 - ic.PadL - ic.PadR), ir.height * (1 - ic.PadT - ic.PadB)), ic.Tex);
                GUI.color = o;
            }
            Frame(ir, new Color(Edge.r, Edge.g, Edge.b, 0.8f * a));
            float tx = ir.xMax + 8, tw = W - (tx - r.x) - 8;
            Text(new Rect(tx, yy, tw, 20), BBCode.Rich(row.name), _title, new Color(1f, 0.95f, 0.85f, a));
            if (row.desc.Length > 0) Text(new Rect(tx, yy + 19, tw, row.h - 19), row.desc, _small, new Color(Stat.r, Stat.g, Stat.b, a));
            yy += row.h;
        }
    }
}
