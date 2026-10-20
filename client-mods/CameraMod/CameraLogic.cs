using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Input = UnityEngine.Input;

namespace CameraMod;

/// <summary>
/// Per-frame camera logic, driven by the Harmony patches on ActorCameraController.LateUpdate.
/// The game writes CameraTransform's position/rotation every LateUpdate (UpdateMovement), so everything here is
/// an offset on top of the stock result: when all offsets are zero the camera is untouched.
/// </summary>
internal static class CameraLogic
{
    // --- third person state ---
    private static float _prevZoom;
    private static float _extTarget, _extCur;   // extra distance in metres (+ = farther, - = closer)
    private static float _farDist = -1, _nearDist = -1;
    private static float _yaw, _pitch;          // free-rotation offsets in degrees
    private static Vector3 _lastMouse;
    private static int _inputFrame = -1;
    private static bool _loggedTarget;

    // --- close camera state (first / third person) ---
    private static CamMode _mode = CamMode.TopDown;
    private static bool _fp => _mode != CamMode.TopDown;
    private static float _savedZoom = -1;
    private static float _fpYaw, _fpPitch;
    private static Transform _fpTarget, _head;
    private static Camera _cam;
    private static float _savedNear, _savedFar, _savedFov;
    // v4 smoothing state
    private static bool _eyeInit;
    private static Vector3 _eye, _eyeVel, _headOffLp;
    private static float _viewYaw, _viewPitch;
    private static Transform _facing;
    internal static Vector3 Eye => _eye;
    private static Il2CppStructArray<float> _savedCull;
    private static Vector3 _savedCamLocalPos;
    private static Quaternion _savedCamLocalRot;
    internal static bool InFirstPerson => _mode == CamMode.FirstPerson;
    internal static bool InThirdPerson => _mode == CamMode.ThirdPerson;
    /// <summary>First or third person: the shared "close camera" state (WASD, keys, clicks, fog, sky, fov, guards) is active.</summary>
    internal static bool InCloseCamera => _mode != CamMode.TopDown;
    internal static CamMode Mode => _mode;
    /// <summary>Third person with the WoW scheme (cursor free unless a button is held or mouse look toggled).</summary>
    internal static bool WowMode => _mode == CamMode.ThirdPerson && Plugin.TpLookMode.Value == TpLook.WoW;
    internal static string ModeName => _mode switch { CamMode.FirstPerson => "first person", CamMode.ThirdPerson => "third person", _ => "top-down" };
    /// <summary>Should the crosshair be drawn now?</summary>
    internal static bool CrosshairWanted =>
        Plugin.Crosshair.Value && (_mode == CamMode.FirstPerson || (_mode == CamMode.ThirdPerson && Plugin.TpCrosshair.Value && (Plugin.TpLookMode.Value == TpLook.Shooter || MouseLock.WowToggled)))
        && (MouseLock.CentreCaptured || (!Plugin.CrosshairOnlyWhenLocked.Value && !WowMode));
    internal static float SavedFov => _savedFov;
    internal static float SavedNear => _savedNear;
    internal static float SavedFar => _savedFar;
    internal static Il2CppStructArray<float> SavedCull => _savedCull;
    internal static float FpYaw => _fpYaw;
    /// <summary>The close-camera Camera while first/third person is on (profiler), else null.</summary>
    internal static Camera CloseCam => _mode != CamMode.TopDown ? _cam : null;
    internal static bool LayerCullCleared { get; private set; }
    private static float _farWanted;

    private static int _failCount;

    internal static void Fail(string where, Exception ex)
    {
        _failCount++;
        if (_failCount <= 5) Plugin.L.LogError($"CameraMod: error in {where} ({_failCount}/5 logged): {ex}");
        if (_failCount == 50)
        {
            Plugin.L.LogError("CameraMod: too many errors, resetting to stock camera");
            try { ExitClose(null, "errors"); } catch { }
            _yaw = _pitch = _extTarget = _extCur = 0;
        }
    }

    private static bool Usable(ActorCameraController c) =>
        c != null && c.CameraTransform != null && c.CameraTransform.gameObject.activeInHierarchy;

    internal static void Before(ActorCameraController c)
    {
        if (!Usable(c)) return;
        _prevZoom = c.Zoom;
    }

    internal static void After(ActorCameraController c)
    {
        if (!Usable(c)) return;
        var target = c.e; // follow target (local player), field 0x128
        if (!_loggedTarget && target != null)
        {
            _loggedTarget = true;
            Plugin.L.LogInfo($"CameraMod: camera active, CameraTransform='{c.CameraTransform.name}', target='{target.name}', zoom={c.Zoom:0.00}");
        }

        // input once per rendered frame (there can be more than one controller instance)
        int frame = Time.frameCount;
        bool firstThisFrame = frame != _inputFrame;
        _inputFrame = frame;
        Vector3 mouse = Input.mousePosition;
        Vector3 mouseDelta = firstThisFrame ? mouse - _lastMouse : Vector3.zero;
        if (firstThisFrame) _lastMouse = mouse;

        if (firstThisFrame)
        {
            Perf.Frame(_mode);
            var next = _mode;
            if (Plugin.ModeKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.ModeKey.Value))
                next = _mode switch
                {
                    CamMode.TopDown => CamMode.ThirdPerson,
                    CamMode.ThirdPerson when Plugin.FirstPersonEnabled.Value => CamMode.FirstPerson,
                    _ => CamMode.TopDown,
                };
            else if (Plugin.FirstPersonEnabled.Value && Plugin.FirstPersonKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.FirstPersonKey.Value))
                next = _mode == CamMode.FirstPerson ? CamMode.TopDown : CamMode.FirstPerson;
            if (next != _mode) SetMode(c, target, next, "key");
        }

        if (_fp)
        {
            if (target == null || _fpTarget == null || target.Pointer != _fpTarget.Pointer) { ExitClose(c, "target changed/lost"); }
            else { UpdateClose(c, firstThisFrame); return; }
        }

        if (firstThisFrame && Input.GetKeyDown(Plugin.ResetKey.Value))
        {
            _yaw = _pitch = 0; _extTarget = 0;
            StatusOverlay.Show("view reset");
        }

        if (target == null) return;
        var cam = c.CameraTransform;
        Vector3 pos = cam.position;
        Quaternion rot = cam.rotation;
        Vector3 tpos = target.position;

        // ---- extended zoom ----
        if (Plugin.ExtendedZoom.Value)
        {
            float zoom = c.Zoom;
            float dist = (pos - tpos).magnitude;
            if (zoom <= 0.001f) _farDist = dist;
            if (zoom >= 0.999f) _nearDist = dist;
            float far = _farDist > 0 ? _farDist : dist, near = _nearDist > 0 ? _nearDist : dist;
            float maxOut = Math.Max(0, far * (Plugin.MaxDistanceMultiplier.Value - 1f));
            float maxIn = Math.Max(0, near * (1f - Plugin.MinDistanceMultiplier.Value));

            float scroll = firstThisFrame ? Input.mouseScrollDelta.y : 0f; // + = wheel up = zoom in (game: Zoom += wheel, 1 = Near)
            bool inputOk = c.f && !OverUi() && !Input.GetKey(KeyCode.LeftShift); // game skips zoom on LeftShift (placement ghost rotation)
            if (inputOk && scroll != 0)
            {
                float step = Math.Abs(scroll) * Plugin.ZoomStep.Value;
                if (scroll < 0 && (_prevZoom <= 0.001f || _extTarget > 0)) _extTarget = Math.Min(maxOut, _extTarget + step);
                else if (scroll > 0 && _extTarget > 0) _extTarget = Math.Max(0, _extTarget - step);
                else if (scroll > 0 && (_prevZoom >= 0.999f || _extTarget < 0)) _extTarget = Math.Max(-maxIn, _extTarget - step);
                else if (scroll < 0 && _extTarget < 0) _extTarget = Math.Min(0, _extTarget + step);
            }
            _extTarget = Math.Clamp(_extTarget, -maxIn, maxOut);

            // while the extension is active, keep the game's own zoom pinned at its limit and recompute its pose
            if (_extTarget != 0 && Math.Abs(c.Zoom - _prevZoom) > 1e-5f)
            {
                c.Zoom = _prevZoom;
                c.UpdateMovement();
                pos = cam.position; rot = cam.rotation;
            }

            float k = 1f - (float)Math.Exp(-Plugin.ZoomSmoothing.Value * Time.unscaledDeltaTime);
            _extCur += (_extTarget - _extCur) * k;
            if (Math.Abs(_extCur - _extTarget) < 0.001f) _extCur = _extTarget;
            if (_extCur != 0)
            {
                Vector3 fwd = rot * Vector3.forward;
                // never push the camera past the player when zooming in
                float maxCloser = Math.Max(0, (pos - tpos).magnitude - 0.5f);
                pos -= fwd * Math.Max(_extCur, -maxCloser);
            }
        }

        // ---- free rotation ----
        if (Plugin.FreeRotation.Value)
        {
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool drag = Input.GetMouseButton(2) || (Plugin.AltRightDrag.Value && alt && Input.GetMouseButton(1));
            if (drag && firstThisFrame)
            {
                _yaw += mouseDelta.x * Plugin.RotateSensitivity.Value;
                _pitch -= mouseDelta.y * Plugin.RotateSensitivity.Value;
                if (_yaw > 360) _yaw -= 360; else if (_yaw < -360) _yaw += 360;
            }
            if (_yaw != 0 || _pitch != 0)
            {
                Vector3 fwd = rot * Vector3.forward;
                float basePitch = (float)(Math.Asin(Math.Clamp(-fwd.y, -1f, 1f)) * 180.0 / Math.PI); // + = looking down
                float lo = Plugin.MinPitch.Value - basePitch, hi = Plugin.MaxPitch.Value - basePitch;
                _pitch = lo <= hi ? Math.Clamp(_pitch, lo, hi) : 0;
                Vector3 pivot = tpos + Vector3.up * Plugin.PivotHeight.Value;
                Vector3 right = rot * Vector3.right;
                Quaternion q = Quaternion.AngleAxis(_yaw, Vector3.up) * Quaternion.AngleAxis(_pitch, right);
                pos = pivot + q * (pos - pivot);
                rot = q * rot;
            }
        }

        if (_extCur != 0 || _yaw != 0 || _pitch != 0)
        {
            cam.position = pos;
            cam.rotation = rot;
        }
    }

    private static bool _overUiBroken;
    internal static bool OverUiPublic() => OverUi();
    private static bool OverUi()
    {
        if (_overUiBroken) return false;
        try { return UICamera.isOverUI; }
        catch (Exception ex) { _overUiBroken = true; Plugin.L.LogWarning("CameraMod: UICamera.isOverUI unavailable: " + ex.Message); return false; }
    }

    // ================= close camera (first / third person) =================

    private static float ModeFov() => _mode == CamMode.ThirdPerson ? Plugin.TpFov.Value : Plugin.FpFov.Value;

    private static void SetMode(ActorCameraController c, Transform target, CamMode next, string why)
    {
        if (next == _mode) return;
        if (next == CamMode.TopDown) { ExitClose(c, why); return; }
        if (_mode == CamMode.TopDown) { EnterClose(c, target, next); return; }
        // first <-> third person: keep the shared state, swap only the mode parts
        var prev = _mode;
        float yaw = prev == CamMode.ThirdPerson ? ThirdPerson.CamYaw : _fpYaw;
        float pitch = prev == CamMode.ThirdPerson ? Math.Clamp(ThirdPerson.Pitch * 0.5f, -30f, 40f) : _fpPitch;
        LeaveModeParts(prev);
        _mode = next;
        EnterModeParts(next, target, yaw, pitch);
        StatusOverlay.Show(ModeName + " ON (from " + (prev == CamMode.FirstPerson ? "first person" : "third person") + "), fov " + (_cam != null ? _cam.fieldOfView : 0).ToString("0") + ", " + ModeLookText());
    }

    private static string ModeLookText() => _mode == CamMode.ThirdPerson
        ? (Plugin.TpLookMode.Value == TpLook.WoW ? "WoW: hold RMB = look, LMB = orbit, wheel = distance, " + Plugin.TpShoulderKey.Value + " = shoulder" : "Shooter: mouse look (cursor captured), wheel = distance, " + Plugin.TpShoulderKey.Value + " = shoulder")
        : "mouse look " + Plugin.LookMode.Value;

    /// <summary>Mode-specific parts: body/effect hiding (first person), orbit camera (third person), fov.</summary>
    private static void EnterModeParts(CamMode mode, Transform target, float yaw, float pitch)
    {
        try { MouseLock.Reset(); } catch { }
        if (mode == CamMode.FirstPerson)
        {
            _fpYaw = yaw; _fpPitch = pitch;
            _viewYaw = _fpYaw; _viewPitch = _fpPitch;
            _eyeInit = false;
            try { BodyHider.Enter(target, _head); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: body hide: " + ex); }
            try { FpEffects.Enter(target, _head); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: effects: " + ex); }
        }
        else
        {
            try { ThirdPerson.Enter(target, _head, _cam, yaw); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: third person: " + ex); }
        }
        float fov = ModeFov();
        try { if (_cam != null) _cam.fieldOfView = fov > 1f ? Math.Clamp(fov, 30f, 120f) : _savedFov; } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: fov: " + ex.Message); }
        try { FpControls.Enter(); } catch { }
    }

    private static int LeaveModeParts(CamMode mode)
    {
        int restored = 0;
        if (mode == CamMode.FirstPerson)
        {
            try { restored = BodyHider.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: restore renderers: " + ex.Message); }
            try { restored += FpEffects.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: restore effects: " + ex.Message); }
        }
        else if (mode == CamMode.ThirdPerson)
        {
            try { ThirdPerson.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: third person exit: " + ex.Message); }
        }
        try { MouseLock.Reset(); } catch { }
        return restored;
    }

    private static void EnterClose(ActorCameraController c, Transform target, CamMode mode)
    {
        string what = mode == CamMode.ThirdPerson ? "third person" : "first-person";
        if (target == null) { StatusOverlay.Show(what + ": no target (player) yet"); return; }
        _fpTarget = target;
        _cam = c.CameraTransform.GetComponentInChildren<Camera>();
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) { StatusOverlay.Show(what + ": no Camera found"); _fpTarget = null; return; }
        FpView.BeforeEnter(_cam, target);

        _savedZoom = c.Zoom;
        _savedNear = _cam.nearClipPlane;
        _savedFar = _cam.farClipPlane;
        _savedCamLocalPos = _cam.transform.localPosition;
        _savedCamLocalRot = _cam.transform.localRotation;
        _savedCull = null;
        try
        {
            var cull = _cam.layerCullDistances;
            if (cull != null) { _savedCull = new Il2CppStructArray<float>(cull.Length); for (int i = 0; i < cull.Length; i++) _savedCull[i] = cull[i]; }
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: could not read layerCullDistances: " + ex.Message); }

        _head = FindHead(target, out string how);

        Vector3 f = _cam.transform.forward;
        float yaw = (float)(Math.Atan2(f.x, f.z) * 180.0 / Math.PI);
        string facing = "camera";
        try
        {
            // start looking where the character faces (animator/model transform, else the follow target)
            var anim = target.GetComponentInChildren<Animator>();
            _facing = anim != null ? anim.transform : target;
            Vector3 cf = _facing.forward;
            if (cf.x * cf.x + cf.z * cf.z > 0.01f) { yaw = (float)(Math.Atan2(cf.x, cf.z) * 180.0 / Math.PI); facing = "character"; }
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: character facing: " + ex.Message); }
        _savedFov = 0;
        try { _savedFov = _cam.fieldOfView; } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: fov: " + ex.Message); }

        try { _cam.nearClipPlane = Plugin.NearClip.Value; } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: nearClip: " + ex.Message); }
        _farWanted = Perf.FarClip;
        try { if (_farWanted > _savedFar || (Perf.FarClipMayLower && _farWanted > 1f)) _cam.farClipPlane = _farWanted; } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: farClip: " + ex.Message); }
        LayerCullCleared = false;
        if (Perf.ClearLayerCull && _savedCull != null)
        {
            try { _cam.layerCullDistances = new Il2CppStructArray<float>(32); LayerCullCleared = true; }
            catch (Exception ex) { Plugin.L.LogWarning("CameraMod: layerCullDistances: " + ex.Message); }
        }

        string fog = "";
        try { fog = FogControl.Enter(_cam); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: fog: " + ex); }
        try { FpMovement.Enter(c, target); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: movement: " + ex); }
        string sky = "";
        try { sky = FpSky.Enter(_cam); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: sky: " + ex); }

        _mode = mode;
        EnterModeParts(mode, target, yaw, 5f);
        try { FpView.Enter(_cam, target); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: view guard: " + ex); }

        StatusOverlay.Show($"{ModeName} ON, yaw from {facing}, fov {_savedFov:0}->{(_cam != null ? _cam.fieldOfView : 0):0}, {ModeLookText()}, head: {how}, hidden renderers: {BodyHider.HiddenCount}, near {_savedNear:0.###}->{_cam.nearClipPlane:0.###}, far {_savedFar:0}->{_cam.farClipPlane:0}, {fog}, {sky}, move keys {Plugin.MoveForwardKey.Value}/{Plugin.MoveLeftKey.Value}/{Plugin.MoveBackKey.Value}/{Plugin.MoveRightKey.Value} ({Plugin.MoveMethodCfg.Value})");
    }

    /// <summary>Critically damped spring (same maths as Unity's SmoothDamp, managed).</summary>
    private static float Damp(float cur, float target, ref float vel, float smoothTime, float dt)
    {
        float omega = 2f / smoothTime, x = omega * dt;
        float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
        float change = cur - target;
        float temp = (vel + omega * change) * dt;
        vel = (vel - omega * temp) * exp;
        return target + (change + temp) * exp;
    }

    private static float Wrap180(float a) { a %= 360f; if (a > 180f) a -= 360f; else if (a < -180f) a += 360f; return a; }

    private static void UpdateClose(ActorCameraController c, bool firstThisFrame)
    {
        float dt = Math.Max(1e-4f, Time.unscaledDeltaTime);
        Vector3 eye; Quaternion rot;
        float moveYaw;
        long t0 = Perf.Begin();
        if (_mode == CamMode.ThirdPerson)
        {
            ThirdPerson.Update(firstThisFrame, dt, out _);
            eye = ThirdPerson.CamPos; rot = ThirdPerson.CamRot; moveYaw = ThirdPerson.Heading;
        }
        else
        {
            if (firstThisFrame) FirstPersonLook(dt);
            eye = _eye; rot = Quaternion.Euler(_viewPitch, _viewYaw, 0f); moveYaw = _fpYaw;
        }
        Perf.End(Perf.Pose, t0);

        if (firstThisFrame)
        {
            int frame = Time.frameCount;
            if (_mode == CamMode.FirstPerson) { t0 = Perf.Begin(); try { BodyHider.Update(); } catch (Exception ex) { Fail("body hide", ex); } Perf.End(Perf.Body, t0); }
            // the ApplyFog postfix re-applies the override after every game write (each frame); this is only a slow fallback
            if (frame % 30 == 0) { t0 = Perf.Begin(); try { FogControl.Apply(); FitFarClip(); } catch (Exception ex) { Fail("fog", ex); } Perf.End(Perf.Fog, t0); }
            t0 = Perf.Begin(); try { FpMovement.Update(moveYaw); } catch (Exception ex) { Fail("movement", ex); } Perf.End(Perf.Move, t0);
            t0 = Perf.Begin(); try { FpKeys.Update(); } catch (Exception ex) { Fail("spell keys", ex); } Perf.End(Perf.Keys, t0);
            t0 = Perf.Begin(); try { FpControls.Update(); } catch (Exception ex) { Fail("controls", ex); } Perf.End(Perf.Controls, t0);
            if (_mode == CamMode.FirstPerson) { FirstPersonEye(dt); eye = _eye; }
            float fovCfg = ModeFov();
            if (fovCfg > 1f && !FpView.KeepGameFov && (frame & 15) == 0) { try { float fov = Math.Clamp(fovCfg, 30f, 120f); if (Math.Abs(_cam.fieldOfView - fov) > 0.01f) _cam.fieldOfView = fov; } catch { } }
        }

        var t = _cam.transform;
        t.position = eye;
        t.rotation = rot;
        if (t.Pointer != c.CameraTransform.Pointer) { c.CameraTransform.position = eye; c.CameraTransform.rotation = rot; t.position = eye; t.rotation = rot; }
        if (firstThisFrame)
        {
            t0 = Perf.Begin();
            if (MouseLock.CentreCaptured) { try { UpdateAim(eye, rot); } catch (Exception ex) { MouseLock.AimPoint = null; Fail("aim", ex); } }
            else MouseLock.AimPoint = null;
            Perf.End(Perf.Aim, t0);
            if (_mode == CamMode.FirstPerson)
            {
                FpEffects.Eye = eye;
                t0 = Perf.Begin(); try { FpEffects.Update(); } catch (Exception ex) { Fail("effects", ex); } Perf.End(Perf.Effects, t0);
            }
            t0 = Perf.Begin(); try { FpSky.Update(eye); } catch (Exception ex) { Fail("sky", ex); } Perf.End(Perf.Sky, t0);
            t0 = Perf.Begin(); try { FpView.Update(); } catch (Exception ex) { Fail("view guard", ex); } Perf.End(Perf.View, t0);
        }
    }

    /// <summary>
    /// Performance: geometry beyond the end of the (linear) fog is drawn fully fog-coloured, i.e. invisible but paid for
    /// (and the game's top-down camera never sees that far). Far clip = min(preset far, fog end * 1.5 + 50 m), never
    /// below 250 m (margin for the game's separate fog curve / GlobalFog); re-checked every 30 frames, changed only on a > 5 % difference.
    /// </summary>
    private static void FitFarClip()
    {
        if (_cam == null || !Perf.FitFarToFog || FpView.GuardsOff) return;
        float want = _farWanted;
        if (RenderSettings.fog && RenderSettings.fogMode == FogMode.Linear)
        {
            float end = RenderSettings.fogEndDistance;
            if (end > 1f && end < 50000f) want = Math.Min(want, Math.Max(250f, end * 1.5f + 50f));
        }
        if (!Perf.FarClipMayLower) want = Math.Max(want, _savedFar);
        float cur = _cam.farClipPlane;
        if (Math.Abs(cur - want) > want * 0.05f)
        {
            _cam.farClipPlane = want;
            if (_farLogs++ < 6) Plugin.L.LogInfo("CameraMod: " + ModeName + ": far clip " + cur.ToString("0") + " -> " + want.ToString("0") + " m (fog end " + RenderSettings.fogEndDistance.ToString("0") + " m, preset " + (Perf.Performance ? "Performance" : "Custom") + ")");
        }
    }
    private static int _farLogs;

    private static void FirstPersonLook(float dt)
    {
        Vector2 look = MouseLock.Update(true);
        if (look != Vector2.zero)
        {
            float sens = Plugin.LookSensitivity.Value;
            _fpYaw = Wrap180(_fpYaw + look.x * sens);
            float dy = look.y * sens * (Plugin.InvertY.Value ? -1f : 1f);
            _fpPitch = Math.Clamp(_fpPitch - dy, -Math.Abs(Plugin.PitchUpLimit.Value), Math.Abs(Plugin.PitchDownLimit.Value));
        }
        // optional look smoothing (default 0 = direct)
        float ls = Plugin.FpLookSmoothingMs.Value / 1000f;
        if (ls <= 0.0005f) { _viewYaw = _fpYaw; _viewPitch = _fpPitch; }
        else
        {
            float k = 1f - (float)Math.Exp(-dt / ls);
            _viewYaw = Wrap180(_viewYaw + Wrap180(_fpYaw - _viewYaw) * k);
            _viewPitch += (_fpPitch - _viewPitch) * k;
        }
    }

    /// <summary>Eye position: root + filtered head offset, then a critically damped spring.</summary>
    private static void FirstPersonEye(float dt)
    {
        Vector3 root = _fpTarget.position;
        Vector3 headOff = _head != null ? _head.position - root : Vector3.up * Plugin.FallbackEyeHeight.Value;
        float tau = Math.Max(0.01f, Plugin.FpEyeHeightAdaptSec.Value);
        if (!_eyeInit) _headOffLp = headOff;
        else _headOffLp += (headOff - _headOffLp) * (1f - (float)Math.Exp(-dt / tau));
        float bob = Math.Clamp(Plugin.FpHeadBob.Value, 0f, 1f);
        Vector3 off = _headOffLp + (headOff - _headOffLp) * bob;
        Quaternion yawRot = Quaternion.Euler(0f, _viewYaw, 0f);
        Vector3 target = root + off + Vector3.up * Plugin.HeadUpOffset.Value + yawRot * Vector3.forward * Plugin.HeadForwardOffset.Value;
        float st = Plugin.FpPositionSmoothingMs.Value / 1000f;
        if (!_eyeInit || st <= 0.0005f || (target - _eye).sqrMagnitude > 25f) { _eye = target; _eyeVel = Vector3.zero; }
        else
        {
            float vx = _eyeVel.x, vy = _eyeVel.y, vz = _eyeVel.z;
            _eye = new Vector3(Damp(_eye.x, target.x, ref vx, st, dt), Damp(_eye.y, target.y, ref vy, st, dt), Damp(_eye.z, target.z, ref vz, st, dt));
            _eyeVel = new Vector3(vx, vy, vz);
        }
        _eyeInit = true;
    }

    private static bool _loggedAim;
    /// <summary>
    /// Ground-target aim: the game's RayCastGroundHit (Physics.Raycast 500 m, else the plane at the player's height, else - ray
    /// pointing up - the player position) would put spells on the player when the crosshair is at/above the horizon. If the
    /// crosshair ray does not meet the player's ground plane within AimMaxDistance, the game cursor is moved to the screen
    /// projection of the ground point AimMaxDistance ahead (view yaw); the crosshair itself stays at the centre.
    /// Only while the cursor is captured at the centre (first person, third-person Shooter / WoW mouse-look toggle).
    /// </summary>
    private static void UpdateAim(Vector3 eye, Quaternion rot)
    {
        float max = Plugin.AimMaxDistance.Value;
        if (max <= 0.5f || !MouseLock.Locked) { MouseLock.AimPoint = null; return; }
        Vector3 dir = rot * Vector3.forward;
        Vector3 pos = _fpTarget.position;
        float h = eye.y - pos.y;
        float flat = (float)Math.Sqrt(dir.x * dir.x + dir.z * dir.z);
        // third person: the ray starts behind the player, so allow the extra camera distance
        float reach = max + (_mode == CamMode.ThirdPerson ? ThirdPerson.Distance : 0f);
        bool reaches = dir.y < -0.01f && h > 0 && (h / -dir.y) * flat <= reach;
        if (reaches || flat < 0.05f) { MouseLock.AimPoint = null; return; }
        var p = new Vector3(pos.x + dir.x / flat * max, pos.y, pos.z + dir.z / flat * max);
        Vector3 sp = _cam.WorldToScreenPoint(p);
        if (sp.z <= 0.1f) { MouseLock.AimPoint = null; return; }
        MouseLock.AimPoint = new Vector2(sp.x, sp.y);
        if (!_loggedAim) { _loggedAim = true; Plugin.L.LogInfo($"CameraMod: {ModeName}: crosshair above the ground limit -> game cursor on the ground {max:0} m ahead (screen {sp.x:0},{sp.y:0}); AimMaxDistance = 0 disables"); }
    }

    private static void ExitClose(ActorCameraController c, string why)
    {
        if (_mode == CamMode.TopDown) return;
        var prev = _mode;
        string name = ModeName;
        int restored = LeaveModeParts(prev);
        _mode = CamMode.TopDown;
        try { FpView.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: restore view guard: " + ex.Message); }
        try { FogControl.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: restore fog: " + ex.Message); }
        try { FpMovement.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: stop movement: " + ex.Message); }
        try { FpKeys.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: release keys: " + ex.Message); }
        try { FpSky.Exit(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: restore sky: " + ex.Message); }
        try { MouseLock.Reset(); } catch { }
        try
        {
            if (_cam != null)
            {
                if (_savedFov > 1f) _cam.fieldOfView = _savedFov;
                _cam.nearClipPlane = _savedNear;
                _cam.farClipPlane = _savedFar;
                if (_savedCull != null && LayerCullCleared) _cam.layerCullDistances = _savedCull;
                LayerCullCleared = false;
                _cam.transform.localPosition = _savedCamLocalPos;
                _cam.transform.localRotation = _savedCamLocalRot;
            }
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: restore camera: " + ex.Message); }
        // the game's LateUpdate rewrites CameraTransform every frame; recompute now so this frame is already stock
        try { if (c != null && Usable(c)) { if (_savedZoom >= 0) c.Zoom = _savedZoom; c.UpdateMovement(); } } catch { }
        _savedZoom = -1;
        _fpTarget = null; _head = null; _cam = null; _facing = null;
        StatusOverlay.Show($"{name} OFF ({why}) -> top-down, restored renderers/effects: {restored}, near/far {_savedNear:0.###}/{_savedFar:0}");
    }

    private static Transform FindHead(Transform root, out string how)
    {
        try
        {
            var anim = root.GetComponentInChildren<Animator>();
            if (anim != null && anim.isHuman)
            {
                var h = anim.GetBoneTransform(HumanBodyBones.Head);
                if (h != null) { how = $"Animator humanoid bone '{h.name}'"; return h; }
            }
            Transform best = null; int bestScore = 0;
            Search(root, ref best, ref bestScore, 0);
            if (best != null) { how = $"bone by name '{best.name}'" + (anim != null ? " (animator not humanoid)" : " (no animator)"); return best; }
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: head search failed: " + ex.Message); }
        how = $"not found, using player position + {Plugin.FallbackEyeHeight.Value} m";
        return null;
    }

    private static void Search(Transform t, ref Transform best, ref int bestScore, int depth)
    {
        if (depth > 40) return;
        string n = t.name.ToLowerInvariant();
        int score = 0;
        if (n == "head" || n.EndsWith(" head") || n.EndsWith("_head") || n.EndsWith(":head") || n == "bip01 head") score = 3;
        else if (n.Contains("head") && !n.Contains("gear") && !n.Contains("item") && !n.Contains("top") && !n.Contains("end") && !n.Contains("nub")) score = 1;
        if (score > bestScore) { best = t; bestScore = score; }
        for (int i = 0; i < t.childCount; i++) Search(t.GetChild(i), ref best, ref bestScore, depth + 1);
    }
}

public enum CamMode { TopDown, ThirdPerson, FirstPerson }
