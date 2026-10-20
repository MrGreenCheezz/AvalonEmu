using System;
using System.Collections.Generic;
using System.Reflection;
using AlbionGui.Hud;
using UnityEngine;

namespace ActionBarsMod;

public enum BarStyle { Native, Atlas, Classic }

/// <summary>Whether an interop method has a native method behind it (stripped Unity helpers have a zero NativeMethodInfoPtr; CameraMod NOTES rule 3).</summary>
internal static class NativeCheck
{
    private static readonly Dictionary<string, bool> _cache = new();

    internal static bool Has(Type t, string fieldSuffix)
    {
        string key = t.FullName + "." + fieldSuffix;
        if (_cache.TryGetValue(key, out var ok)) return ok;
        ok = false;
        try
        {
            var f = t.GetField("NativeMethodInfoPtr_" + fieldSuffix, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            ok = f != null && f.GetValue(null) is IntPtr p && p != IntPtr.Zero;
        }
        catch { ok = false; }
        _cache[key] = ok;
        if (!ok) Plugin.L.LogWarning("ActionBarsMod: native method missing: " + key);
        return ok;
    }
}

/// <summary>
/// Style Native: the extra buttons ARE the game's buttons. The game's own MainHand1 button (the UISpellSlotContainer with its frame and its
/// Normal UISpellSlot with its NGUI widgets: frame, background, Icon @0x40, ProgressSprite @0x50 (cooldown fill), NumericCooldownOverlay @0xA0 / Label @0xA8,
/// ShortcutLabels @0xE8 ...) is cloned 12 times with Object.Instantiate under an INACTIVE root (so no Awake/OnEnable of the game's scripts
/// runs), every non-NGUI script of the clone is destroyed (UISpellSlot, tweens, buttons, anchors to outside objects are cut), and only then
/// the root is activated under SpellButtonGui (hidden with the HUD). The mod then drives the widgets like UISpellSlotBase does:
///  - icon: UISprite.atlas = drv.s(spell uiatlas) (the game's atlas lookup, UISpellSlotBase.SetIconSpriteByState 0x1814BD6A0), spriteName = uisprite;
///  - cooldown: ProgressSprite.fillAmount = remaining fraction (SetUiSpellSlotCooldown 0x1814BD750: fill = 1 - progress), NumericCooldownOverlay
///    shown with our own text (the game's GetCooldownString formats inside IL2CPP: not called), Icon tinted UISpellSlotBase.CooldownIconColor;
///  - key: every ShortcutLabels[i].text = our binding label.
/// Positions: the clones are placed in screen space at BarsOverlay.SlotRect (icon size and pitch measured from the game's bar, Hud).
/// Any failure: Failed = true, the overlay falls back to Atlas (IMGUI with the game's sprites).
/// </summary>
internal static class NativeBar
{
    internal static bool Failed;
    internal static bool Built => _root != null && _built;

    private sealed class Btn
    {
        public GameObject Go;
        public Transform T;
        public UISprite Icon, Progress;
        public GameObject CdOverlay;
        public UILabel CdLabel;
        public readonly List<UILabel> Keys = new();
        public bool Shown;
        public int ShownId = -2;
        public string ShownKey, ShownCd;
        public float ShownFill = -1;
        public Color ShownColor = new(-1, -1, -1, -1);
        public float RetryIcon;
    }

    private static GameObject _root;
    private static bool _built, _rootActive;
    private static IntPtr _gui;
    private static readonly Btn[] _btns = new Btn[12];
    private static float _nextLayout;
    private static Color _cdColor = new(0.45f, 0.45f, 0.45f, 1f);
    private static int _logs;

    internal static bool Available()
    {
        var o = typeof(UnityEngine.Object);
        return NativeCheck.Has(o, "Instantiate_Public_Static_Object_Object_Transform_Boolean_0")
            && NativeCheck.Has(o, "DestroyImmediate_Public_Static_Void_Object_0")
            && NativeCheck.Has(typeof(Camera), "ScreenToWorldPoint_Public_Vector3_Vector3_0")
            && NativeCheck.Has(typeof(Transform), "IsChildOf_Public_Boolean_Transform_0");
    }

    /// <summary>Hides the clones (another style, HUD hidden).</summary>
    internal static void Hide()
    {
        if (_root == null || !_rootActive) return;
        try { if (_root) _root.SetActive(false); } catch { }
        _rootActive = false;
    }

    private static void Fail(string why, Exception ex = null)
    {
        Failed = true;
        Plugin.L.LogError("ActionBarsMod: native (cloned) buttons unavailable, falling back to Style Atlas: " + why + (ex != null ? " " + ex : ""));
        try { if (_root != null && _root) UnityEngine.Object.Destroy(_root); } catch { }
        _root = null; _built = false; _rootActive = false;
    }

    /// <summary>Per frame from BarsOverlay.Update (main thread).</summary>
    internal static void Update(bool visible)
    {
        if (Failed) return;
        try
        {
            if (_root != null && (!_root || _gui != (Hud.Gui != null ? Hud.Gui.Pointer : IntPtr.Zero))) { _root = null; _built = false; _rootActive = false; } // HUD rebuilt
            if (!visible) { Hide(); return; }
            if (!Built && !Build()) return;
            if (!_rootActive) { _root.SetActive(true); _rootActive = true; }
            float t = Time.unscaledTime;
            if (t >= _nextLayout) { _nextLayout = t + 0.25f; Layout(); }
            Refresh();
        }
        catch (Exception ex) { Fail("per-frame update failed", ex); }
    }

    private static bool Build()
    {
        var src = Hud.SourceContainer;
        var gui = Hud.Gui;
        if (src == null || Hud.Source == null || gui == null || Hud.UiCam == null || Hud.IconPx < 4) return false;
        if (!Available()) { Fail("a Unity method is stripped in this build"); return false; }
        try
        {
            var root = new GameObject("ActionBarsMod.ExtraBars");
            root.SetActive(false);
            root.layer = src.gameObject.layer;
            root.transform.SetParent(gui.transform, false);
            _root = root; _gui = gui.Pointer; _built = false; _rootActive = false;
            int destroyed = 0, cut = 0;
            for (int k = 0; k < 12; k++)
            {
                var o = UnityEngine.Object.Instantiate(src.gameObject.Cast<UnityEngine.Object>(), root.transform, false);
                var go = o?.TryCast<GameObject>();
                if (go == null) { Fail("Object.Instantiate returned no GameObject"); return false; }
                go.name = "ExtraSlot" + (k + 1);
                var b = new Btn { Go = go, T = go.transform };
                var cont = go.GetComponent<UISpellSlotContainer>();
                var slot = cont != null ? Own(cont.Normal, go) : null;
                if (slot == null) { Fail("the clone has no UISpellSlotContainer.Normal"); return false; }
                var vanity = cont.Vanity != null ? Own(cont.Vanity, go) : null;
                if (vanity != null && vanity.Pointer != slot.Pointer) vanity.gameObject.SetActive(false);
                // the normal slot and its parents up to the clone stay visible (the container may have been mid-transition)
                for (var tr = slot.transform; tr != null && tr.Pointer != go.transform.Pointer; tr = tr.parent) if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);
                b.Icon = Own(slot.Icon, go);
                b.Progress = Own(slot.ProgressSprite, go);
                b.CdOverlay = OwnGo(slot.NumericCooldownOverlay, go);
                b.CdLabel = Own(slot.NumericCooldownLabel, go);
                var labels = slot.ShortcutLabels;
                if (labels != null) for (int i = 0; i < labels.Length; i++) { var l = Own(labels[i], go); if (l != null) b.Keys.Add(l); }
                // the game's state overlays: off (only the plain button stays)
                Off(slot.ActiveOverlay, go); Off(slot.LockOverlay, go); Off(slot.FinishOverlayParent, go);
                Off(slot.MultispellTimeoutOverlay, go); Off(slot.SelectedOverlay, go); Off(slot.NumericCooldownOverlay, go);
                var sv = OwnGo(slot.SpellView, go); if (sv != null) sv.SetActive(true);
                Off(slot.SlotIconView, go);
                if (b.Icon == null) { Fail("the clone's Icon widget is not inside the button"); return false; }
                // the game's scripts go: the slot scripts first, then everything that is not an NGUI widget/panel
                UnityEngine.Object.DestroyImmediate(slot);
                if (vanity != null && vanity.Pointer != slot.Pointer) UnityEngine.Object.DestroyImmediate(vanity);
                UnityEngine.Object.DestroyImmediate(cont);
                destroyed += 3;
                var comps = go.GetComponentsInChildren<MonoBehaviour>(true);
                if (comps != null)
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        if (c.TryCast<UIWidget>() != null || c.TryCast<UIPanel>() != null) continue;
                        UnityEngine.Object.DestroyImmediate(c);
                        destroyed++;
                    }
                if (go.GetComponentInChildren<UISpellSlotBase>(true) != null) { Fail("a UISpellSlot script could not be removed from the clone"); return false; }
                // anchors to objects outside the clone would drag it to the original's place
                var rects = go.GetComponentsInChildren<UIRect>(true);
                if (rects != null)
                    foreach (var r in rects)
                    {
                        if (r == null) continue;
                        if (Outside(r.leftAnchor, go) || Outside(r.rightAnchor, go) || Outside(r.bottomAnchor, go) || Outside(r.topAnchor, go))
                        { r.SetAnchor((Transform)null); cut++; }
                    }
                _btns[k] = b;
            }
            try { _cdColor = UISpellSlotBase.CooldownIconColor; } catch { }
            _built = true;
            _nextLayout = 0;
            Plugin.L.LogInfo($"ActionBarsMod: native buttons: 12 clones of the game's '{src.gameObject.name}' under SpellButtonGui ({destroyed} game scripts removed, {cut} outside anchors cut, icon {Hud.IconPx:0} px, pitch {Hud.Pitch:0} px)");
            return true;
        }
        catch (Exception ex) { Fail("cloning the game's button failed", ex); return false; }
    }

    private static T Own<T>(T w, GameObject go) where T : Component
    {
        if (w == null) return null;
        try { return w.transform.IsChildOf(go.transform) ? w : null; } catch { return null; }
    }

    private static GameObject OwnGo(GameObject o, GameObject go)
    {
        if (o == null) return null;
        try { return o.transform.IsChildOf(go.transform) ? o : null; } catch { return null; }
    }

    private static void Off(GameObject o, GameObject go)
    {
        var x = OwnGo(o, go);
        if (x != null) x.SetActive(false);
    }

    private static bool Outside(UIRect.AnchorPoint a, GameObject go)
    {
        if (a == null) return false;
        var t = a.target;
        return t != null && !t.IsChildOf(go.transform);
    }

    /// <summary>Places the clones on BarsOverlay.SlotRect (screen space) with the source button's scale/rotation.</summary>
    private static void Layout()
    {
        var src = Hud.SourceContainer;
        var cam = Hud.UiCam;
        if (src == null || Hud.Source == null || cam == null) return;
        var st = src.transform;
        // offset from the icon centre to the button's pivot, in screen pixels (the slot rects are icon rects)
        var srcIcon = Hud.Source.Icon;
        var ic = srcIcon != null ? srcIcon.worldCorners : null;
        if (ic == null || ic.Length < 4) return;
        Vector3 a = cam.WorldToScreenPoint(ic[0]), c = cam.WorldToScreenPoint(ic[2]);
        Vector3 pivot = cam.WorldToScreenPoint(st.position);
        float dx = pivot.x - (a.x + c.x) / 2f, dy = pivot.y - (a.y + c.y) / 2f, z = pivot.z;
        Vector3 ls = st.lossyScale, rs = _root.transform.lossyScale;
        var scale = new Vector3(rs.x != 0 ? ls.x / rs.x : 1, rs.y != 0 ? ls.y / rs.y : 1, rs.z != 0 ? ls.z / rs.z : 1);
        var rot = st.rotation;
        int rows = BarsOverlay.RowsShown;
        for (int k = 0; k < 12; k++)
        {
            var b = _btns[k];
            if (b == null) continue;
            int bar = k / 6, pos = k % 6;
            bool show = bar < rows && pos < Net.BarSize && Net.Index(bar, pos) >= 0;
            if (show != b.Shown) { b.Go.SetActive(show); b.Shown = show; }
            if (!show) continue;
            var r = BarsOverlay.SlotRect(bar, pos);
            float sx = r.x + r.width / 2f, sy = Screen.height - (r.y + r.height / 2f);
            b.T.position = cam.ScreenToWorldPoint(new Vector3(sx + dx, sy + dy, z));
            b.T.rotation = rot;
            b.T.localScale = scale;
        }
    }

    private static void Refresh()
    {
        long now = Clock.Now();
        float t = Time.unscaledTime;
        for (int k = 0; k < 12; k++)
        {
            var b = _btns[k];
            if (b == null || !b.Shown) continue;
            int idx = Net.Index(k / 6, k % 6);
            if (idx < 0) continue;
            var s = Net.Slots[idx];
            bool empty = s.SpellId < 0 || s.Unique.Length == 0;
            int id = empty ? -1 : s.SpellId;
            if (id != b.ShownId || (b.RetryIcon > 0 && t >= b.RetryIcon)) SetIcon(b, s, id, t);
            // cooldown
            float fill = 0; string cd = "";
            if (!empty && s.CdEnd > now)
            {
                long total = Math.Max(1, s.CdStart > 0 && s.CdStart < s.CdEnd ? s.CdEnd - s.CdStart : s.CdEnd - now);
                fill = Math.Clamp((float)(s.CdEnd - now) / total, 0f, 1f);
                double sec = (s.CdEnd - now) / 10_000_000.0;
                cd = sec >= 10 ? ((int)Math.Ceiling(sec)).ToString() : sec.ToString("0.0");
            }
            if (b.Progress != null && Math.Abs(fill - b.ShownFill) > 0.002f) { b.Progress.fillAmount = fill; b.ShownFill = fill; }
            if (cd != b.ShownCd)
            {
                bool on = cd.Length > 0 && Plugin.ShowCooldownNumbers.Value;
                if (b.CdOverlay != null) b.CdOverlay.SetActive(on);
                if (on && b.CdLabel != null) b.CdLabel.text = cd;
                b.ShownCd = cd;
            }
            // icon tint: cooldown grey, refusal flash red
            Color col = Color.white;
            if (cd.Length > 0) col = _cdColor;
            if (s.FlashUntil > t) { float f = Math.Clamp((s.FlashUntil - t) / 0.6f, 0f, 1f); col = Color.Lerp(col, new Color(1f, 0.25f, 0.2f, 1f), f); }
            if (b.Icon != null && col != b.ShownColor) { b.Icon.color = col; b.ShownColor = col; }
            // key label
            string key = Plugin.ShowKeys.Value ? KeyInput.Binds[k].Label ?? "" : "";
            if (key != b.ShownKey)
            {
                foreach (var l in b.Keys) if (l != null) l.text = key;
                b.ShownKey = key;
            }
        }
    }

    private static void SetIcon(Btn b, SlotInfo s, int id, float t)
    {
        b.ShownId = id;
        b.RetryIcon = 0;
        if (id < 0) { b.Icon.enabled = false; return; }
        var info = Spells.Get(s.SpellId, s.Unique);
        string sprite = info?.Sprite ?? s.Unique;
        UIAtlas atlas = null;
        string atlasName = info != null && info.Atlas.Length > 0 ? info.Atlas : "RefSpellAtlas";
        try { atlas = drv.s(atlasName); } catch (Exception ex) { if (_logs++ < 5) Plugin.L.LogWarning($"ActionBarsMod: atlas lookup drv.s('{atlasName}') failed: {ex.Message}"); }
        if (atlas == null || atlas.GetSprite(sprite) == null) atlas = Icons.AtlasOf(sprite) ?? atlas;
        if (atlas == null) { b.Icon.enabled = false; b.RetryIcon = t + 3f; return; }
        b.Icon.atlas = atlas;
        b.Icon.spriteName = sprite;
        b.Icon.enabled = true;
        if (_logs++ < 12) Plugin.L.LogInfo($"ActionBarsMod: native button {Net.SlotName(Net.Index(Array.IndexOf(_btns, b) / 6, Array.IndexOf(_btns, b) % 6))} icon '{sprite}' from atlas '{atlas.name}'");
    }
}
