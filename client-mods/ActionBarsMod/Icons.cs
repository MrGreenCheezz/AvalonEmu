using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace ActionBarsMod;

internal sealed class Icon
{
    public Texture2D Tex;
    public float PadL, PadR, PadT, PadB; // trimmed transparent border as a fraction of the full sprite size
}

/// <summary>
/// Spell icons from the game's NGUI atlases (icons SPEC §1: SpellAtlas / ItemAtlas*, RGBA32 4096², sprite x/y from the top-left,
/// padding = trimmed border). GUI.DrawTextureWithTexCoords is stripped in this build (no NativeMethodInfoPtr), so every needed sprite
/// is cut once into its own small Texture2D: Graphics.Blit(atlas texture -> temporary RenderTexture), RenderTexture.active = rt,
/// Texture2D.ReadPixels(rect with y flipped) + Apply (all native). Atlases: Resources.FindObjectsOfTypeAll(UIAtlas) (prefabs),
/// UIAtlas.GetSprite / texture follow mReplacement (Ref* atlases).
/// </summary>
internal static class Icons
{
    private static readonly Dictionary<string, Icon> _icons = new();
    private static readonly HashSet<string> _missing = new();
    private static readonly HashSet<string> _pending = new();
    private static readonly List<UIAtlas> _atlases = new();
    private static readonly Dictionary<string, int> _attempts = new();
    private static float _nextScan, _nextRetry;
    private static bool _broken;
    private static int _logged;

    /// <summary>null while not (yet) available; the overlay then draws the name.</summary>
    internal static Icon Get(string sprite)
    {
        if (_broken || string.IsNullOrEmpty(sprite)) return null;
        if (_icons.TryGetValue(sprite, out var ic))
        {
            if (ic.Tex != null && ic.Tex.Pointer != IntPtr.Zero) return ic;
            _icons.Remove(sprite);
        }
        if (!_missing.Contains(sprite)) _pending.Add(sprite);
        return null;
    }

    private static readonly Dictionary<string, (UIAtlas atlas, string sprite)> _pendingFrom = new();

    /// <summary>A sprite of a given atlas (the native styles' frame/background layers); null until cut (next Update).</summary>
    internal static Icon GetFrom(UIAtlas atlas, string sprite)
    {
        if (_broken || atlas == null || string.IsNullOrEmpty(sprite)) return null;
        string key;
        try { key = (atlas.name ?? "?") + "|" + sprite; } catch { return null; }
        if (_icons.TryGetValue(key, out var ic))
        {
            if (ic.Tex != null && ic.Tex.Pointer != IntPtr.Zero) return ic;
            _icons.Remove(key);
        }
        if (!_missing.Contains(key)) _pendingFrom[key] = (atlas, sprite);
        return null;
    }

    private static void UpdateFrom()
    {
        if (_pendingFrom.Count == 0) return;
        var byAtlas = new Dictionary<IntPtr, (UIAtlas atlas, List<(string name, UISpriteData sd)> list)>();
        foreach (var kv in _pendingFrom)
        {
            var (a, sprite) = kv.Value;
            UISpriteData sd = null;
            Texture tex = null;
            try { if (a != null) { sd = a.GetSprite(sprite); tex = a.texture; } } catch { }
            if (sd == null || tex == null) { _missing.Add(kv.Key); if (_logged++ < 40) Plugin.L.LogInfo($"ActionBarsMod: sprite '{kv.Key}' not cut (not in its atlas)"); continue; }
            IntPtr key = tex.Pointer;
            if (!byAtlas.TryGetValue(key, out var e)) byAtlas[key] = e = (a, new List<(string, UISpriteData)>());
            e.list.Add((kv.Key, sd));
        }
        _pendingFrom.Clear();
        foreach (var e in byAtlas.Values) Extract(e.atlas, e.list);
    }

    /// <summary>Main thread, outside OnGUI.</summary>
    internal static void Update()
    {
        if (_broken) return;
        try { UpdateFrom(); }
        catch (Exception ex) { _broken = true; Plugin.L.LogError("ActionBarsMod: sprite extraction failed, atlas sprites disabled: " + ex); return; }
        if (_pending.Count == 0) return;
        float t = Time.unscaledTime;
        if (t < _nextRetry) return;
        try
        {
            bool scanned = false;
            if (_atlases.Count == 0 || t >= _nextScan) { ScanAtlases(t); scanned = true; }
            var byAtlas = new Dictionary<IntPtr, (UIAtlas atlas, List<(string name, UISpriteData sd)> list)>();
            var done = new List<string>();
            foreach (var name in _pending)
            {
                UIAtlas found = null; UISpriteData sd = null;
                foreach (var a in _atlases)
                {
                    if (a == null) continue;
                    var s = a.GetSprite(name);
                    if (s == null) continue;
                    var tex = a.texture;
                    if (tex == null) continue;
                    found = a; sd = s;
                    break;
                }
                if (found == null) continue;
                IntPtr key = found.texture.Pointer;
                if (!byAtlas.TryGetValue(key, out var e)) byAtlas[key] = e = (found, new List<(string, UISpriteData)>());
                e.list.Add((name, sd));
                done.Add(name);
            }
            foreach (var e in byAtlas.Values) Extract(e.atlas, e.list);
            foreach (var n in done) _pending.Remove(n);
            if (_pending.Count > 0)
            {
                _nextRetry = t + 3f;
                if (scanned)
                {
                    // not in any atlas after a fresh scan: give up after 3 scans (~30 s; atlases load with the HUD), text fallback
                    var drop = new List<string>();
                    foreach (var n in _pending)
                    {
                        _attempts.TryGetValue(n, out int k);
                        _attempts[n] = ++k;
                        if (k >= 3) drop.Add(n);
                    }
                    foreach (var n in drop)
                    {
                        _pending.Remove(n); _missing.Add(n);
                        if (_logged++ < 20) Plugin.L.LogInfo($"ActionBarsMod: no atlas sprite '{n}' ({_atlases.Count} atlases), the button shows the name");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _broken = true;
            Plugin.L.LogError("ActionBarsMod: icon extraction failed, icons disabled (names shown instead): " + ex);
        }
    }

    /// <summary>The scanned atlas that holds <paramref name="sprite"/> (SpellAtlas first), null if none (yet).</summary>
    internal static UIAtlas AtlasOf(string sprite)
    {
        if (string.IsNullOrEmpty(sprite)) return null;
        if (_atlases.Count == 0) { try { ScanAtlases(Time.unscaledTime); } catch { return null; } }
        foreach (var a in _atlases)
        {
            try { if (a != null && a.GetSprite(sprite) != null) return a; } catch { }
        }
        return null;
    }

    private static void ScanAtlases(float t)
    {
        _atlases.Clear();
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UIAtlas>());
        var names = new List<string>();
        if (all != null)
            foreach (var o in all)
            {
                var a = o?.TryCast<UIAtlas>();
                if (a == null) continue;
                // SpellAtlas first, then the rest (spell icons live in SpellAtlas; items as a fallback)
                string n = a.name ?? "";
                if (n.Contains("Spell")) _atlases.Insert(0, a); else _atlases.Add(a);
                if (names.Count < 40) names.Add(n);
            }
        _nextScan = t + 10f;
        Plugin.L.LogInfo($"ActionBarsMod: {_atlases.Count} NGUI atlases found: {string.Join(", ", names)}");
    }

    private static void Extract(UIAtlas atlas, List<(string name, UISpriteData sd)> list)
    {
        var src = atlas.texture;
        int W = src.width, H = src.height;
        var rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32);
        var prev = RenderTexture.active;
        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            foreach (var (name, sd) in list)
            {
                int w = Math.Max(1, sd.width), h = Math.Max(1, sd.height);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.hideFlags = HideFlags.HideAndDontSave;
                tex.ReadPixels(new Rect(sd.x, H - sd.y - h, w, h), 0, 0);
                tex.Apply(false);
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                float fw = w + sd.paddingLeft + sd.paddingRight, fh = h + sd.paddingTop + sd.paddingBottom;
                _icons[name] = new Icon
                {
                    Tex = tex,
                    PadL = sd.paddingLeft / fw, PadR = sd.paddingRight / fw,
                    PadT = sd.paddingTop / fh, PadB = sd.paddingBottom / fh,
                };
                if (_logged++ < 20) Plugin.L.LogInfo($"ActionBarsMod: icon '{name}' cut from atlas '{atlas.name}' ({W}x{H}) at {sd.x},{sd.y} {w}x{h}");
            }
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }
}
