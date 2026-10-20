using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace CameraMod;

public enum TpLook { WoW, Shooter }

/// <summary>
/// Third-person "over the shoulder" camera (v6). Shares the close-camera state with first person (fog, sky, fov, near/far,
/// viewport guards, WASD, keys, clicks; see CameraLogic) and only computes its own pose:
///   pivot  = player position + up * PivotHeight (critically damped spring),
///   rot    = Euler(pitch, heading + orbit, 0),
///   camera = pivot + yawRight * shoulder + rot * back * distance, pulled in by a sphere cast pivot -> camera.
/// Look schemes: WoW (cursor visible; hold RMB = look + turn the movement heading, hold LMB = orbit only, both = walk
/// forward; MouseLockKey = permanent mouse look) or Shooter (cursor captured at the crosshair, like first person).
/// Collision: Physics.SphereCastNonAlloc (native in this build; Physics.SphereCast(bool) is not in the interop) against
/// the layers in CollisionLayers (resolved with LayerMask.NameToLayer; LayerToName is stripped), triggers ignored,
/// colliders under the local character skipped; fallback Physics.Raycast. Pull in at once, ease back out.
/// Very close camera (< FadeDistance) hides the character meshes (BodyHider, all) and near effects (FpEffects).
/// </summary>
internal static class ThirdPerson
{
    private static Transform _target, _head;
    private static Camera _cam;
    private static float _heading, _orbit, _pitch, _dist, _distTarget, _side = 1f, _shoulderCur, _collDist = -1f;
    private static Vector3 _pivot, _pivotVel;
    private static bool _init, _faded;
    private static int _mask = -1;
    private static string _maskText = "";
    private static Il2CppStructArray<RaycastHit> _hits;
    private static bool _sphereBroken, _rayBroken, _loggedFirstHit;
    private static string _lastBlocker = "";
    private static int _blockLogs;

    internal static Vector3 CamPos { get; private set; }
    internal static Quaternion CamRot { get; private set; } = Quaternion.identity;
    internal static float Heading => _heading;
    internal static float CamYaw => Wrap180(_heading + _orbit);
    internal static float Pitch => _pitch;
    internal static float Distance => _collDist > 0 ? _collDist : _dist;
    internal static float DistanceTarget => _distTarget;
    internal static bool Blocked { get; private set; }
    internal static bool Faded => _faded;
    internal static string Shoulder => Math.Abs(Plugin.TpShoulderOffset.Value) < 0.01f ? "centre" : (_side * Plugin.TpShoulderOffset.Value > 0 ? "right" : "left");

    private static float Wrap180(float a) { a %= 360f; if (a > 180f) a -= 360f; else if (a < -180f) a += 360f; return a; }

    internal static void Enter(Transform target, Transform head, Camera cam, float yaw)
    {
        _target = target; _head = head; _cam = cam;
        _heading = Wrap180(yaw); _orbit = 0;
        _pitch = Math.Clamp(Plugin.TpDefaultPitch.Value, PitchMin(), PitchMax());
        _distTarget = _dist = Math.Clamp(Plugin.TpDistance.Value, MinDist(), MaxDist());
        _shoulderCur = Plugin.TpShoulderOffset.Value * _side;
        _collDist = -1f; _init = false; _faded = false; Blocked = false;
        _lastBlocker = ""; _blockLogs = 0; _lastBlockerPtr = IntPtr.Zero; _colSelf.Clear();
        _rmbHeld = false;
        BuildMask();
        Plugin.L.LogInfo($"CameraMod: third person: look {Plugin.TpLookMode.Value}, distance {_dist:0.0} m ({MinDist():0.0}..{MaxDist():0.0}), shoulder {Plugin.TpShoulderOffset.Value:0.00} m {Shoulder}, pivot {Plugin.TpPivotHeight.Value:0.00} m, pitch {PitchMin():0}..{PitchMax():0}, collision {(Plugin.TpCollision.Value ? "on" : "off")} mask 0x{_mask:X8} [{_maskText}]");
    }

    internal static void Exit()
    {
        Unfade();
        FpMovement.ExtraForward = false;
        _target = null; _head = null; _cam = null;
    }

    private static float MinDist() => Math.Max(0.3f, Plugin.TpMinDistance.Value);
    private static float MaxDist() => Math.Max(MinDist(), Plugin.TpMaxDistance.Value);
    private static float PitchMin() => Math.Min(Plugin.TpPitchMin.Value, Plugin.TpPitchMax.Value);
    private static float PitchMax() => Math.Max(Plugin.TpPitchMin.Value, Plugin.TpPitchMax.Value);

    private static void BuildMask()
    {
        _mask = 0;
        var found = new List<string>();
        foreach (var raw in (Plugin.TpCollisionLayers.Value ?? "").Split(',', ';'))
        {
            string n = raw.Trim();
            if (n.Length == 0) continue;
            int l = -1;
            if (int.TryParse(n, out int num)) l = num;
            else { try { l = LayerMask.NameToLayer(n); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: LayerMask.NameToLayer failed: " + ex.Message); } }
            if (l < 0 || l > 31) { found.Add(n + "=?"); continue; }
            _mask |= 1 << l;
            found.Add(n + "=" + l);
        }
        if (_mask == 0) { _mask = ~0; found.Add("none resolved -> all layers"); }
        _maskText = string.Join(", ", found);
    }

    /// <summary>Per frame (first controller call of the frame does the input). Returns the camera pose.</summary>
    internal static void Update(bool firstThisFrame, float dt, out LookKind kind)
    {
        kind = LookKind.None;
        if (_target == null) return;
        if (firstThisFrame)
        {
            Vector2 d;
            if (Plugin.TpLookMode.Value == TpLook.Shooter)
            {
                d = MouseLock.Update(true);
                kind = MouseLock.Locked ? LookKind.Turn : LookKind.None;
            }
            else d = MouseLock.UpdateWow(out kind);

            float sens = Plugin.LookSensitivity.Value;
            float dx = d.x * sens, dy = d.y * sens * (Plugin.InvertY.Value ? -1f : 1f);
            if (kind == LookKind.Turn)
            {
                // RMB / captured: the movement heading follows the camera (any orbit offset is folded into the heading)
                _heading = Wrap180(_heading + _orbit + dx);
                _orbit = 0;
            }
            else if (kind == LookKind.Orbit) _orbit = Wrap180(_orbit + dx);
            if (kind != LookKind.None) _pitch = Math.Clamp(_pitch - dy, PitchMin(), PitchMax());

            FpMovement.ExtraForward = Plugin.TpBothButtonsForward.Value && MouseLock.BothButtons;

            // WoW-style camera follow: walking without orbiting swings the camera back behind the heading
            if (Plugin.TpAutoFollow.Value && kind != LookKind.Orbit && FpMovement.AnyKey && Math.Abs(_orbit) > 0.01f)
            {
                float tau = Math.Max(0.05f, Plugin.TpAutoFollowSec.Value);
                _orbit *= (float)Math.Exp(-dt / tau);
                if (Math.Abs(_orbit) < 0.05f) _orbit = 0;
            }

            // wheel = distance
            float scroll = Input.mouseScrollDelta.y;
            if (scroll != 0 && !CameraLogic.OverUiPublic() && !FpMovement.Typing())
            {
                float step = Math.Max(0.1f, Plugin.TpZoomStep.Value) * Math.Max(0.5f, _distTarget / 5f);
                _distTarget = Math.Clamp(_distTarget - scroll * step, MinDist(), MaxDist());
            }
            _distTarget = Math.Clamp(_distTarget, MinDist(), MaxDist());
            float kz = 1f - (float)Math.Exp(-Math.Max(0.5f, Plugin.TpZoomSmoothing.Value) * dt);
            _dist += (_distTarget - _dist) * kz;

            if (Plugin.TpShoulderKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.TpShoulderKey.Value) && !FpMovement.Typing())
            {
                _side = -_side;
                StatusOverlay.Show("third person: shoulder " + Shoulder);
            }
            float ks = 1f - (float)Math.Exp(-8f * dt);
            _shoulderCur += (Plugin.TpShoulderOffset.Value * _side - _shoulderCur) * ks;

            Vector3 tp = _target.position;
            RmbGuard(tp, dt);

            // pivot: player + height, smoothed like the first-person eye
            Vector3 raw = tp + Vector3.up * Plugin.TpPivotHeight.Value;
            float st = Plugin.TpPositionSmoothingMs.Value / 1000f;
            if (!_init || st <= 0.0005f || (raw - _pivot).sqrMagnitude > 25f) { _pivot = raw; _pivotVel = Vector3.zero; }
            else
            {
                float vx = _pivotVel.x, vy = _pivotVel.y, vz = _pivotVel.z;
                _pivot = new Vector3(Damp(_pivot.x, raw.x, ref vx, st, dt), Damp(_pivot.y, raw.y, ref vy, st, dt), Damp(_pivot.z, raw.z, ref vz, st, dt));
                _pivotVel = new Vector3(vx, vy, vz);
            }

            Quaternion rot = Quaternion.Euler(_pitch, CamYaw, 0f);
            Vector3 right = Quaternion.Euler(0f, CamYaw, 0f) * Vector3.right;
            Vector3 desired = _pivot + right * _shoulderCur - (rot * Vector3.forward) * _dist;
            Vector3 dir = desired - _pivot;
            float full = dir.magnitude;
            if (full < 1e-3f) { dir = rot * Vector3.back; full = 1e-3f; } else dir /= full;

            float allowed = full;
            Blocked = false;
            if (Plugin.TpCollision.Value)
            {
                float hit = Cast(_pivot, dir, full);
                if (hit < full)
                {
                    allowed = Math.Max(0.05f, hit - Math.Max(0f, Plugin.TpCollisionMargin.Value));
                    Blocked = true;
                }
            }
            if (!_init || _collDist < 0 || allowed < _collDist) _collDist = allowed; // pull in at once
            else
            {
                float kr = 1f - (float)Math.Exp(-Math.Max(0.5f, Plugin.TpCollisionReturnSpeed.Value) * dt);
                _collDist += (allowed - _collDist) * kr;
                if (Math.Abs(allowed - _collDist) < 0.005f) _collDist = allowed;
            }
            _init = true;
            CamPos = _pivot + dir * _collDist;
            CamRot = rot;

            // fade the character when the camera is inside / next to it
            float near = Math.Max(0f, Plugin.TpFadeDistance.Value);
            float d2head = _collDist;
            if (_head != null) d2head = Math.Min(d2head, (CamPos - _head.position).magnitude);
            if (!_faded && near > 0 && d2head < near) FadeIn();
            else if (_faded && d2head > near + 0.3f) Unfade();
            if (_faded)
            {
                long t0 = Perf.Begin();
                try { BodyHider.Update(); } catch (Exception ex) { CameraLogic.Fail("tp body fade", ex); }
                Perf.End(Perf.Body, t0);
                FpEffects.Eye = CamPos;
                t0 = Perf.Begin();
                try { FpEffects.Update(); } catch (Exception ex) { CameraLogic.Fail("tp effects", ex); }
                Perf.End(Perf.Effects, t0);
            }
        }
    }

    // ---- WoW right button = look only: safety net against the game walking while it is held ----
    private static bool _rmbHeld, _rmbWasMoving, _rmbStopped;
    private static Vector3 _rmbPos, _prevTp;
    private static float _rmbT;
    private static int _rmbStopLogs;

    /// <summary>
    /// The right-button masks are stripped in LocalInputHandler.EvaluateActionsByInput (FpControls), so the game should never
    /// start a walk from it. If the character still starts moving within 1.5 s of an RMB press (it stood still at the press, no
    /// movement key, not both buttons), the walk is stopped once (StopMovementAction) and logged.
    /// </summary>
    private static void RmbGuard(Vector3 tp, float dt)
    {
        float spd = _init ? (tp - _prevTp).magnitude / Math.Max(1e-4f, dt) : 0f;
        _prevTp = tp;
        bool rmb = Plugin.TpLookMode.Value == TpLook.WoW && MouseLock.WowRightHeld;
        if (!rmb) { _rmbHeld = false; return; }
        if (!_rmbHeld) { _rmbHeld = true; _rmbWasMoving = spd > 0.5f; _rmbPos = tp; _rmbStopped = false; _rmbT = 0; return; }
        _rmbT += dt;
        if (FpMovement.AnyKey || FpMovement.ExtraForward) { _rmbPos = tp; _rmbWasMoving = true; return; }
        if (_rmbWasMoving || _rmbStopped || _rmbT > 1.5f) return; // later walks (spell range, auto-attack) are the game's own
        if ((tp - _rmbPos).sqrMagnitude < 0.16f) return;
        _rmbStopped = true;
        try { LocalInputHandler.Instance?.StopMovementAction(); } catch { }
        if (_rmbStopLogs++ < 5) Plugin.L.LogInfo("CameraMod: third person: the character started walking while the right button (look) was held -> StopMovementAction");
    }

    private static void FadeIn()
    {
        _faded = true;
        try { BodyHider.Enter(_target, _head, true); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: third person fade: " + ex.Message); }
        try { FpEffects.Enter(_target, _head); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: third person fade effects: " + ex.Message); }
        Plugin.L.LogInfo($"CameraMod: third person: camera within {Plugin.TpFadeDistance.Value:0.0} m of the character -> character hidden ({BodyHider.HiddenCount} renderers)");
    }

    private static void Unfade()
    {
        if (!_faded) return;
        _faded = false;
        int n = 0;
        try { n = BodyHider.Exit(); } catch { }
        try { FpEffects.Exit(); } catch { }
        Plugin.L.LogInfo($"CameraMod: third person: camera away from the character -> character shown again ({n} renderers)");
    }

    private static bool IsUnder(Transform t, Transform root)
    {
        for (int i = 0; t != null && root != null && i < 60; i++, t = t.parent) if (t.Pointer == root.Pointer) return true;
        return false;
    }

    /// <summary>Distance along dir to the first blocking collider (or max).</summary>
    // collider pointer -> belongs to the local character (cached: one parent walk per collider ever)
    private static readonly Dictionary<IntPtr, bool> _colSelf = new();
    private static bool ColliderIsSelf(Collider col)
    {
        if (_colSelf.TryGetValue(col.Pointer, out bool s)) return s;
        if (_colSelf.Count > 4096) _colSelf.Clear();
        s = IsUnder(col.transform, _target);
        _colSelf[col.Pointer] = s;
        return s;
    }

    private static float Cast(Vector3 origin, Vector3 dir, float max)
    {
        float best = max;
        Collider blocker = null;
        float radius = Math.Max(0.01f, Plugin.TpCollisionRadius.Value);
        long t0 = Perf.Begin();
        try
        {
            if (!_sphereBroken)
            {
                try
                {
                    _hits ??= new Il2CppStructArray<RaycastHit>(24);
                    int n = Physics.SphereCastNonAlloc(origin, radius, dir, _hits, max, _mask, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n && i < _hits.Length; i++)
                    {
                        var h = _hits[i];
                        float dist = h.distance;
                        if (dist <= 0.0001f || dist >= best) continue; // 0 = overlapping at the start (pivot inside): ignored
                        var col = h.collider;
                        if (col == null || ColliderIsSelf(col)) continue;
                        best = dist;
                        blocker = col;
                    }
                    LogBlocker(blocker, best, "SphereCastNonAlloc");
                    return best;
                }
                catch (Exception ex) { _sphereBroken = true; Plugin.L.LogWarning("CameraMod: third person: Physics.SphereCastNonAlloc failed, using Physics.Raycast: " + ex.GetType().Name + ": " + ex.Message); }
            }
            if (!_rayBroken)
            {
                try
                {
                    if (Physics.Raycast(origin, dir, out RaycastHit h, max + radius, _mask, QueryTriggerInteraction.Ignore))
                    {
                        var col = h.collider;
                        if (col != null && !ColliderIsSelf(col))
                        {
                            best = Math.Min(max, Math.Max(0f, h.distance - radius));
                            blocker = col;
                        }
                    }
                    LogBlocker(blocker, best, "Raycast");
                }
                catch (Exception ex) { _rayBroken = true; Plugin.L.LogWarning("CameraMod: third person: Physics.Raycast failed, camera collision off: " + ex.GetType().Name + ": " + ex.Message); }
            }
            return best;
        }
        finally { Perf.End(Perf.Collision, t0); }
    }

    private static IntPtr _lastBlockerPtr;
    /// <summary>Logs a new blocking collider (name built only when it changes and logs remain).</summary>
    private static void LogBlocker(Collider blocker, float dist, string how)
    {
        if (!_loggedFirstHit) { _loggedFirstHit = true; Plugin.L.LogInfo("CameraMod: third person: collision probe works (" + how + ")"); }
        IntPtr p = blocker == null ? IntPtr.Zero : blocker.Pointer;
        if (p == _lastBlockerPtr) return;
        _lastBlockerPtr = p;
        if (p == IntPtr.Zero || _blockLogs >= 30) return;
        string name = blocker.name + " (layer " + blocker.gameObject.layer + ")";
        if (name == _lastBlocker) return;
        _lastBlocker = name;
        _blockLogs++;
        Plugin.L.LogInfo($"CameraMod: third person: camera pulled in by '{name}' at {dist:0.00} m ({how})");
    }

    private static float Damp(float cur, float target, ref float vel, float smoothTime, float dt)
    {
        float omega = 2f / smoothTime, x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = cur - target;
        float temp = (vel + omega * change) * dt;
        vel = (vel - omega * temp) * exp;
        return target + (change + temp) * exp;
    }
}
