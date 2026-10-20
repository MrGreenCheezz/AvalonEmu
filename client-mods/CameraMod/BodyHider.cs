using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace CameraMod;

public enum ShowBody { None, BodyOnly, All }
public enum HideMode { ShadowsOnly, Disable }

/// <summary>
/// Hides the local character's meshes in first person (and the third-person close fade). v2 uses shadowCastingMode =
/// ShadowsOnly (keeps the shadow) or Renderer.forceRenderingOff and re-asserts them, includes inactive children
/// (equipment that becomes active later), and optionally scans the scene for skinned meshes bound to the character's
/// bones but parented elsewhere.
/// v7 (performance): renderers are classified once (type / head test cached per renderer); every 20 frames only the
/// hidden ones are re-asserted (1-2 interop calls each); the child list is re-read every BodyRescanFrames (equipment
/// changes) and the deep scene scan runs every SceneScanFrames with FindObjectsByType (no sort), skipping skinned meshes
/// already known not to be ours; "under the character / head" uses transform pointer sets instead of parent walks.
/// </summary>
internal static class BodyHider
{
    private sealed class Entry
    {
        public Renderer R;
        public ShadowCastingMode Shadow;
        public bool ForceOff;
        public bool Hidden;
    }

    private static readonly Dictionary<IntPtr, Entry> _tracked = new();
    private static readonly HashSet<IntPtr> _notMine = new(), _subtree = new(), _headSet = new();
    private static Transform _root, _head;
    private static int _nextAssert, _nextScan, _nextDeepFrame;
    private static bool _loggedList;

    internal static int HiddenCount { get { int n = 0; foreach (var e in _tracked.Values) if (e.Hidden) n++; return n; } }

    /// <summary>true = hide every mesh regardless of ShowBody (third-person fade when the camera is very close).</summary>
    private static bool _all;

    internal static void Enter(Transform root, Transform head, bool hideAll = false)
    {
        _root = root; _head = head; _all = hideAll;
        _tracked.Clear(); _notMine.Clear();
        _nextAssert = _nextScan = 0; _nextDeepFrame = 0;
        Update(true);
    }

    private static bool Active => _root != null && (_all || Plugin.ShowBodyMode.Value != ShowBody.All);

    /// <summary>Per frame: re-assert every 20 frames, re-read the renderer list every BodyRescanFrames, deep scan rarely.</summary>
    internal static void Update(bool force = false)
    {
        if (!Active) return;
        int f = Time.frameCount;
        if (force || f >= _nextScan)
        {
            _nextScan = f + Perf.BodyRescanFrames;
            Rescan(force, f);
            _nextAssert = f + 20;
            return;
        }
        if (f < _nextAssert) return;
        _nextAssert = f + 20;
        bool shadows = Plugin.HideRenderMode.Value == HideMode.ShadowsOnly;
        List<IntPtr> dead = null;
        foreach (var kv in _tracked)
        {
            var e = kv.Value;
            if (!e.Hidden) continue;
            try { Apply(e.R, shadows); }
            catch { (dead ??= new()).Add(kv.Key); }
        }
        if (dead != null) foreach (var k in dead) _tracked.Remove(k);
    }

    private static void Apply(Renderer r, bool shadows)
    {
        if (shadows) { if (r.shadowCastingMode != ShadowCastingMode.ShadowsOnly) r.shadowCastingMode = ShadowCastingMode.ShadowsOnly; }
        else if (!r.forceRenderingOff) r.forceRenderingOff = true;
    }

    private static void Rescan(bool force, int f)
    {
        Perf.Subtree(_root, _subtree);
        if (_head != null) Perf.Subtree(_head, _headSet); else _headSet.Clear();

        var list = new List<Renderer>();
        foreach (var r in _root.GetComponentsInChildren<Renderer>(true)) if (r != null) list.Add(r);
        if (Plugin.DeepScan.Value && (force || f >= _nextDeepFrame))
        {
            _nextDeepFrame = f + Perf.SceneScanFrames;
            try
            {
                var arr = UnityEngine.Object.FindObjectsByType(Il2CppType.Of<SkinnedMeshRenderer>(), FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (arr != null)
                    foreach (var p in Perf.Ptrs(arr, arr.Length))
                    {
                        if (p == IntPtr.Zero || _notMine.Contains(p) || _tracked.ContainsKey(p)) continue;
                        var smr = new SkinnedMeshRenderer(p);
                        var rb = smr.rootBone;
                        if (rb != null && _subtree.Contains(rb.Pointer) && !_subtree.Contains(smr.transform.Pointer)) list.Add(smr);
                        else _notMine.Add(p);
                    }
            }
            catch (Exception ex) { Plugin.L.LogWarning("CameraMod: deep renderer scan failed, disabled: " + ex.Message); Plugin.DeepScan.Value = false; }
        }

        bool log = force && !_loggedList && !_all;
        if (log) { _loggedList = true; Plugin.L.LogInfo($"CameraMod: local character renderers ({list.Count}), ShowBody={Plugin.ShowBodyMode.Value}, HideMode={Plugin.HideRenderMode.Value}:"); }
        bool shadows = Plugin.HideRenderMode.Value == HideMode.ShadowsOnly;
        int shown = 0;
        foreach (var r in list)
        {
            if (_tracked.TryGetValue(r.Pointer, out var known))
            {
                if (known.Hidden) Apply(r, shadows);
                continue;
            }
            if (_notMine.Contains(r.Pointer)) continue;
            string type = r.GetIl2CppType().Name;
            if (type.Contains("Particle") || type.Contains("Trail") || type.Contains("Line")) { _notMine.Add(r.Pointer); continue; } // keep spell VFX
            bool head = IsHeadish(r);
            bool hide = _all || Plugin.ShowBodyMode.Value == ShowBody.None || head;
            if (log && shown++ < 40) Plugin.L.LogInfo($"CameraMod:   {type} '{r.name}' parent='{(r.transform.parent != null ? r.transform.parent.name : "-")}' head={head} hide={hide}{(r.gameObject.activeInHierarchy ? "" : " (inactive)")}");
            var e = new Entry { R = r, Shadow = r.shadowCastingMode, ForceOff = r.forceRenderingOff, Hidden = hide };
            _tracked[r.Pointer] = e;
            if (hide) Apply(r, shadows);
        }
    }

    private static readonly string[] HeadWords = { "head", "hair", "helm", "hood", "face", "beard", "hat", "cap", "eye", "mask", "ear", "brow", "teeth", "tongue", "mouth", "jaw", "mustache", "horn" };

    private static bool IsHeadish(Renderer r)
    {
        if (_head != null)
        {
            if (_headSet.Contains(r.transform.Pointer)) return true;
            var smr = r.TryCast<SkinnedMeshRenderer>();
            if (smr != null && smr.rootBone != null && _headSet.Contains(smr.rootBone.Pointer)) return true;
        }
        string n = r.name.ToLowerInvariant();
        foreach (var w in HeadWords) if (n.Contains(w)) return true;
        return false;
    }

    internal static int Exit()
    {
        int restored = 0;
        foreach (var e in _tracked.Values)
        {
            if (!e.Hidden) continue;
            try
            {
                if (e.R == null) continue;
                e.R.shadowCastingMode = e.Shadow;
                e.R.forceRenderingOff = e.ForceOff;
                restored++;
            }
            catch { }
        }
        _tracked.Clear(); _notMine.Clear();
        _root = null; _head = null; _all = false;
        return restored;
    }
}
