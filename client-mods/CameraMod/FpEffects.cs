using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CameraMod;

public enum FpEffectsMode { Off, NearCamera, AllOnSelf }

/// <summary>
/// First person: hides effects that sit in front of the eye and the local player's own overhead GUI.
///
/// Where cosmetic (vanity) / equipment / buff effects live (v4 finding): they are NOT children of the character. The game
/// spawns them as separate GameObjects that follow a socket through its constraint system:
///   - Vfx : MonoBehaviour, IConstrainableObject (gw3) - `Vfx.Create(dtc master, ..., string socket, bbq constraint, ...)`,
///     field `pConstraintCtrl` (gw1 @0x148), `renderers` (@0xA0), `iInstigatorId` (@0xD0).
///   - ConstrainableObject : MonoBehaviour, gw3 - generic attachment (`AttachTo(dtc master, string socket, ...)`), `pConstraintCtrl` @0x20.
///   - gw1 (constraint controller): b = dtc master @0x18, q = socket name @0x60, r = dta socket @0x68 -> Transform b (@0x18).
/// v7 (performance): every scene search uses Object.FindObjectsByType(type, Exclude, SortMode.None) (FindObjectsOfType sorts
/// by instance id), the result array is walked a slice per frame (raw element pointers, no wrapper for skipped entries),
/// "is this mine" uses pointer sets of the character's transforms instead of parent walks, and per-object results are
/// cached (Vfx pointer + constraint controller pointer -> self / socket / renderers). Intervals come from the Performance preset.
/// </summary>
internal static class FpEffects
{
    private sealed class Entry { public Renderer R; public bool ForceOff; }
    private sealed class Plate { public Transform T; public Vector3 Scale; }
    private sealed class Attached { public IntPtr Ctrl; public bool Self; public int Checked; public string Socket = ""; public List<Renderer> Rs; public int RsScan; }

    private static readonly Dictionary<IntPtr, Entry> _hidden = new();
    private static readonly Dictionary<IntPtr, Plate> _plates = new();
    private static readonly Dictionary<IntPtr, Attached> _attached = new();
    private static readonly HashSet<IntPtr> _seenAttached = new(), _plateGuis = new();
    private static readonly SortedDictionary<string, int> _kinds = new();
    private static readonly HashSet<IntPtr> _self = new(), _ancestors = new(), _headSet = new();
    private static readonly List<Renderer> _selfFx = new();
    private static Transform _root, _head;
    private static int _nextSelf, _nextPlate, _nextSummary, _nextSets, _scanNo;
    private static bool _sceneBroken, _plateBroken, _loggedSelf, _boundsBroken, _vfxBroken, _coBroken, _kindsDirty;
    private static int _sceneLogs, _attachLogs;
    internal static Vector3 Eye;

    // time-sliced scans: 0 = Vfx, 1 = ConstrainableObject, 2 = scene particles, 3 = nameplates
    private const int KVfx = 0, KCo = 1, KScene = 2, KPlate = 3;
    private static readonly IntPtr[][] _arr = new IntPtr[4][];
    private static readonly Il2CppReferenceArray<UnityEngine.Object>[] _keep = new Il2CppReferenceArray<UnityEngine.Object>[4];
    private static readonly int[] _pos = new int[4], _next = new int[4];

    internal static int HiddenCount => _hidden.Count;
    internal static int PlateCount => _plates.Count;

    internal static void Enter(Transform root, Transform head)
    {
        _root = root; _head = head;
        _hidden.Clear(); _plates.Clear(); _plateGuis.Clear(); _seenAttached.Clear(); _kinds.Clear(); _attached.Clear(); _selfFx.Clear();
        _nextSelf = _nextPlate = 0; _nextSets = 0; _scanNo = 0;
        for (int i = 0; i < 4; i++) { _arr[i] = null; _keep[i] = null; _pos[i] = 0; _next[i] = 0; }
        _nextSummary = Time.frameCount + 120;
        _kindsDirty = false;
        RefreshSets();
    }

    private static void RefreshSets()
    {
        try
        {
            Perf.Subtree(_root, _self);
            _ancestors.Clear();
            for (var t = _root; t != null && _ancestors.Count < 60; t = t.parent) _ancestors.Add(t.Pointer);
            if (_head != null) Perf.Subtree(_head, _headSet); else _headSet.Clear();
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: effects: transform set: " + ex.Message); }
    }

    private static bool IsFx(Renderer r)
    {
        string type = r.GetIl2CppType().Name;
        return type.Contains("Particle") || type.Contains("Trail") || type.Contains("Line") || type.Contains("VFX");
    }

    private static readonly string[] HeadSocketWords = { "head", "neck", "face", "helm", "hat", "eye", "mouth", "hair", "mask" };
    private static bool HeadishName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        n = n.ToLowerInvariant();
        foreach (var w in HeadSocketWords) if (n.Contains(w)) return true;
        return false;
    }

    private static bool UnderHeadOrNeck(Transform t)
    {
        if (_headSet.Contains(t.Pointer)) return true;
        for (int i = 0; t != null && i < 60 && t.Pointer != _root.Pointer; i++, t = t.parent)
        {
            string n = t.name.ToLowerInvariant();
            if (n.Contains("neck") || n.EndsWith("head") || n.Contains("head_") || n.Contains("_head")) return true;
        }
        return false;
    }

    /// <summary>Squared distance from the eye to the renderer's bounds (managed maths), or to its transform.</summary>
    private static float SqrDist(Renderer r)
    {
        if (!_boundsBroken)
        {
            try
            {
                Bounds b = r.bounds;
                Vector3 c = b.m_Center, e = b.m_Extents;
                float dx = Math.Max(0, Math.Abs(Eye.x - c.x) - e.x), dy = Math.Max(0, Math.Abs(Eye.y - c.y) - e.y), dz = Math.Max(0, Math.Abs(Eye.z - c.z) - e.z);
                return dx * dx + dy * dy + dz * dz;
            }
            catch (Exception ex) { _boundsBroken = true; Plugin.L.LogWarning("CameraMod: Renderer.bounds unavailable, using transform positions: " + ex.GetType().Name + ": " + ex.Message); }
        }
        Vector3 p = r.transform.position;
        float x = p.x - Eye.x, y = p.y - Eye.y, z = p.z - Eye.z;
        return x * x + y * y + z * z;
    }

    private static void Hide(Renderer r)
    {
        if (!_hidden.TryGetValue(r.Pointer, out var e))
        {
            e = new Entry { R = r, ForceOff = r.forceRenderingOff };
            _hidden[r.Pointer] = e;
            if (!r.forceRenderingOff) r.forceRenderingOff = true;
        }
    }

    private static void Count(string kind) { _kinds.TryGetValue(kind, out int n); _kinds[kind] = n + 1; _kindsDirty = true; }

    /// <summary>Is this constraint controller attached to the local character? socket = socket/bone name.</summary>
    private static bool BoundToSelf(gw1 ctrl, out string socket)
    {
        socket = "";
        if (ctrl == null) return false;
        var sock = ctrl.r;
        var st = sock != null ? sock.b : null;
        bool self = false;
        if (st != null && _self.Contains(st.Pointer)) self = true;
        if (!self)
        {
            var m = ctrl.b;
            if (m != null)
            {
                var comp = m.TryCast<Component>();
                if (comp != null)
                {
                    var mt = comp.transform;
                    if (mt != null && (_self.Contains(mt.Pointer) || _ancestors.Contains(mt.Pointer))) self = true;
                }
            }
        }
        if (self)
        {
            try { socket = ctrl.q ?? ""; } catch { }
            if (socket.Length == 0 && st != null) socket = st.name;
        }
        return self;
    }

    /// <summary>Starts a time-sliced scan: one native search, element pointers copied, walked SliceSize per frame.</summary>
    private static bool StartScan(int k, Il2CppSystem.Type type)
    {
        var a = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        _keep[k] = a; // keeps the elements alive while the slices run
        _arr[k] = a == null ? Array.Empty<IntPtr>() : Perf.Ptrs(a, a.Length);
        _pos[k] = 0;
        return _arr[k].Length > 0;
    }

    private static void EndScan(int k, int interval)
    {
        _arr[k] = null; _keep[k] = null; _pos[k] = 0;
        _next[k] = Time.frameCount + interval;
    }

    /// <summary>Handle one attached object (Vfx / ConstrainableObject) bound to the local character.</summary>
    private static void HandleAttached(Attached a, GameObject go, string kind, bool isVfx, FpEffectsMode mode, float rad2)
    {
        bool fresh = a.Rs == null || _scanNo - a.RsScan >= 4;
        if (fresh)
        {
            a.Rs ??= new List<Renderer>();
            a.Rs.Clear();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (r != null) a.Rs.Add(r);
            a.RsScan = _scanNo;
        }
        bool headSock = HeadishName(a.Socket);
        bool hideAll = isVfx ? (mode == FpEffectsMode.AllOnSelf || headSock) : (Plugin.ShowBodyMode.Value == ShowBody.None || headSock);
        int hid = 0;
        foreach (var r in a.Rs)
        {
            if (_hidden.ContainsKey(r.Pointer)) { hid++; continue; }
            if (hideAll || SqrDist(r) < rad2) { Hide(r); hid++; }
        }
        if (_seenAttached.Add(go.Pointer))
        {
            var types = new SortedDictionary<string, int>();
            foreach (var r in a.Rs) { string tn = r.GetIl2CppType().Name; types.TryGetValue(tn, out int c); types[tn] = c + 1; }
            Count(kind);
            foreach (var kv in types) Count(kind + "/" + kv.Key);
            if (_attachLogs++ < 40)
            {
                var tl = new List<string>(); foreach (var kv in types) tl.Add(kv.Key + "=" + kv.Value);
                Plugin.L.LogInfo($"CameraMod: first person: {kind} on you '{go.name}' socket '{a.Socket}' renderers {a.Rs.Count} [{string.Join(", ", tl)}] -> hidden {hid}{(hideAll ? " (all)" : " (near camera)")}");
            }
        }
    }

    /// <summary>One attached object of the current slice: cached by object pointer + its constraint controller pointer.</summary>
    private static void StepAttached(int k, IntPtr p, FpEffectsMode mode, float rad2)
    {
        bool isVfx = k == KVfx;
        gw1 ctrl;
        Component comp;
        if (isVfx) { var v = new Vfx(p); ctrl = v.pConstraintCtrl; comp = v; }
        else { var co = new ConstrainableObject(p); ctrl = co.pConstraintCtrl; comp = co; }
        IntPtr cp = ctrl == null ? IntPtr.Zero : ctrl.Pointer;
        if (!_attached.TryGetValue(p, out var a)) { a = new Attached { Ctrl = (IntPtr)(-1) }; _attached[p] = a; }
        // re-evaluate when re-attached, or (not self) every 4th scan
        if (a.Ctrl != cp || (!a.Self && _scanNo - a.Checked >= 4))
        {
            a.Ctrl = cp; a.Checked = _scanNo; a.Rs = null;
            a.Self = BoundToSelf(ctrl, out string socket);
            a.Socket = socket;
        }
        if (!a.Self) return;
        HandleAttached(a, comp.gameObject, isVfx ? "Vfx" : "ConstrainableObject", isVfx, mode, rad2);
    }

    private static void StepScene(IntPtr p, float rad2)
    {
        if (_hidden.ContainsKey(p)) return;
        var r = new Renderer(p);
        if (SqrDist(r) >= rad2) return;
        if (!r.enabled) return;
        Hide(r);
        Count("SceneParticleNearCamera");
        if (++_sceneLogs <= 10) Plugin.L.LogInfo($"CameraMod: first person: hid effect near the camera '{r.name}' (parent '{(r.transform.parent != null ? r.transform.parent.name : "-")}')");
    }

    private static void StepPlate(IntPtr p)
    {
        if (_plateGuis.Contains(p)) return;
        var g = new WorldObjectAnchoredGui(p);
        var v = g.AnchoredWorldObjectView;
        if (v == null) return;
        var vt = v.transform;
        if (vt == null || !(_self.Contains(vt.Pointer) || _ancestors.Contains(vt.Pointer))) return;
        var t = g.transform;
        _plateGuis.Add(p);
        if (!_plates.ContainsKey(t.Pointer))
        {
            _plates[t.Pointer] = new Plate { T = t, Scale = t.localScale };
            Plugin.L.LogInfo($"CameraMod: first person: hid own overhead GUI '{g.name}' ({g.GetIl2CppType().Name})");
        }
        if (t.localScale != Vector3.zero) t.localScale = Vector3.zero;
    }

    /// <summary>Runs up to SliceSize entries of scan k; starts it when due. Returns false when the scan type broke.</summary>
    private static void RunSlice(int k, int interval, Func<Il2CppSystem.Type> type, Action<IntPtr> step, ref bool broken, string what)
    {
        if (broken) return;
        int f = Time.frameCount;
        try
        {
            if (_arr[k] == null)
            {
                if (f < _next[k]) return;
                if (!StartScan(k, type())) { EndScan(k, interval); return; }
            }
            var arr = _arr[k];
            int end = Math.Min(arr.Length, _pos[k] + Perf.SliceSize);
            for (int i = _pos[k]; i < end; i++)
            {
                var p = arr[i];
                if (p == IntPtr.Zero) continue;
                try { step(p); } catch { /* destroyed meanwhile */ }
            }
            _pos[k] = end;
            if (end >= arr.Length) EndScan(k, interval);
        }
        catch (Exception ex) { broken = true; EndScan(k, interval); Plugin.L.LogWarning($"CameraMod: {what} failed, disabled: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static Il2CppSystem.Type TVfx() => Il2CppType.Of<Vfx>();
    private static Il2CppSystem.Type TCo() => Il2CppType.Of<ConstrainableObject>();
    private static Il2CppSystem.Type TPsr() => Il2CppType.Of<ParticleSystemRenderer>();
    private static Il2CppSystem.Type TPlate() => Il2CppType.Of<WorldObjectAnchoredGui>();

    private static FpEffectsMode _mode;
    private static float _rad2, _crad2;
    private static readonly Action<IntPtr> _stepVfx = p => StepAttached(KVfx, p, _mode, _crad2);
    private static readonly Action<IntPtr> _stepCo = p => StepAttached(KCo, p, _mode, _crad2);
    private static readonly Action<IntPtr> _stepScene = p => StepScene(p, _rad2);
    private static readonly Action<IntPtr> _stepPlate = StepPlate;
    private static readonly Func<Il2CppSystem.Type> _tVfx = TVfx, _tCo = TCo, _tPsr = TPsr, _tPlate = TPlate;

    internal static void Update()
    {
        if (_root == null) return;
        var mode = Plugin.FpHideEffects.Value;
        int f = Time.frameCount;
        float rad = Math.Max(0.05f, Plugin.FpEffectRadius.Value), crad = Math.Max(0.1f, Plugin.FpCosmeticRadius.Value);
        _mode = mode; _rad2 = rad * rad; _crad2 = crad * crad;
        int interval = Perf.EffectsScanFrames;

        if (f >= _nextSets) { _nextSets = f + Perf.BodyRescanFrames; RefreshSets(); }

        if (mode != FpEffectsMode.Off)
        {
            // effects parented under the character (rare in this game, kept from v3); the list is re-read with the body rescan
            if (f >= _nextSelf)
            {
                bool reread = _selfFx.Count == 0 || (f / interval) % 4 == 0;
                _nextSelf = f + interval;
                if (reread)
                {
                    _selfFx.Clear();
                    foreach (var r in _root.GetComponentsInChildren<Renderer>(true)) if (r != null && IsFx(r)) _selfFx.Add(r);
                }
                int hid = 0;
                foreach (var r in _selfFx)
                {
                    try
                    {
                        if (_hidden.ContainsKey(r.Pointer)) { hid++; continue; }
                        bool hide = mode == FpEffectsMode.AllOnSelf || UnderHeadOrNeck(r.transform) || SqrDist(r) < _crad2;
                        if (hide) { Hide(r); hid++; }
                    }
                    catch { }
                }
                if (!_loggedSelf) { _loggedSelf = true; Plugin.L.LogInfo($"CameraMod: first-person effects ({mode}): {_selfFx.Count} particle/trail/line renderers parented under the character, hidden {hid}"); }
            }

            // cosmetic / equipment / buff effects attached to your sockets (separate GameObjects): Vfx, then ConstrainableObject
            if (_arr[KCo] == null) RunSlice(KVfx, interval, _tVfx, _stepVfx, ref _vfxBroken, "attached Vfx scan");
            if (_arr[KVfx] == null) { bool wasRunning = _arr[KCo] != null; RunSlice(KCo, interval, _tCo, _stepCo, ref _coBroken, "attached ConstrainableObject scan"); if (wasRunning && _arr[KCo] == null) _scanNo++; }

            // any particle effect in the scene touching the camera
            RunSlice(KScene, interval, _tPsr, _stepScene, ref _sceneBroken, "scene effect scan");
        }

        if (_kindsDirty && f >= _nextSummary)
        {
            _kindsDirty = false;
            _nextSummary = f + 600;
            var parts = new List<string>(); foreach (var kv in _kinds) parts.Add(kv.Key + "=" + kv.Value);
            Plugin.L.LogInfo($"CameraMod: first-person effects summary: {string.Join(", ", parts)}; hidden renderers now {_hidden.Count}");
        }

        // keep already hidden ones hidden (the game may re-enable VFX objects, forceRenderingOff is ours)
        if ((f & 15) == 0)
        {
            List<IntPtr> dead = null;
            foreach (var kv in _hidden)
            {
                try { if (kv.Value.R == null) { (dead ??= new()).Add(kv.Key); continue; } if (!kv.Value.R.forceRenderingOff) kv.Value.R.forceRenderingOff = true; }
                catch { (dead ??= new()).Add(kv.Key); }
            }
            if (dead != null) foreach (var k in dead) _hidden.Remove(k);
        }

        if (Plugin.FpHideOwnNameplate.Value)
        {
            // search rarely (the plate exists as long as the character), keep the found ones at scale 0 every 30 frames
            RunSlice(KPlate, Perf.SceneScanFrames, _tPlate, _stepPlate, ref _plateBroken, "own nameplate hide");
            if (f >= _nextPlate)
            {
                _nextPlate = f + 30;
                foreach (var p in _plates.Values) { try { if (p.T != null && p.T.localScale != Vector3.zero) p.T.localScale = Vector3.zero; } catch { } }
            }
        }
    }

    internal static int Exit()
    {
        int n = 0;
        foreach (var e in _hidden.Values) { try { if (e.R != null) { e.R.forceRenderingOff = e.ForceOff; n++; } } catch { } }
        foreach (var p in _plates.Values) { try { if (p.T != null) p.T.localScale = p.Scale; } catch { } }
        _hidden.Clear(); _plates.Clear(); _plateGuis.Clear(); _seenAttached.Clear(); _attached.Clear(); _selfFx.Clear();
        for (int i = 0; i < 4; i++) { _arr[i] = null; _keep[i] = null; }
        _root = null; _head = null;
        return n;
    }
}
