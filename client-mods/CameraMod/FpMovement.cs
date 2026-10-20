using System;
using HarmonyLib;
using UnityEngine;

namespace CameraMod;

public enum MoveMethod { Auto, InputAction, ClickMove }

/// <summary>
/// WASD movement relative to the first-person view yaw.
/// InputAction: LocalInputHandler.HandleInputActionsMove(Vector2 vInput, bool bForced) (VA 0x18199DCA0) - the entry the game's
///   own keyboard (HandleKeyboardMovement 0x18199F580), gamepad and virtual joystick movement use:
///   returns false if the view is walk-locked or (not forced) the character is in a blocking state; else
///   target = player.position + WorldSpaceRotation * (x,0,y) -> DoMoveTriggered(target). WorldSpaceRotation (0x1819BAA70) is a
///   cached AngleAxis(-48 deg, up), so the mod pre-rotates the camera-relative direction by its inverse.
/// ClickMove: LocalInputHandler.DoMoveTriggered(Vector3 worldTarget) (0x181996280) directly - the end of the ground-click path
///   (HandleInputActionsMove(Ray,..) 0x18199DF30): returns 3 when input actions are blocked, 0 when the target is within
///   MinMoveDistance (0x294), 2 when queued behind a cast, 4 when refused, else RequestMove (0x1819C9550, sends the Move op,
///   rate-limited by the game) and 0.
/// Auto: InputAction; if the player has not moved 0.5 s after a press, switches to ClickMove (logged).
/// Release -> LocalInputHandler.StopMovementAction() (0x1819B1280).
/// v4 send policy: immediately (bForced = true) when the key combination changes, immediately (at most ~30/s) when the view
/// yaw turned by more than 3 degrees, otherwise every IntervalMs; release of the last key stops at once.
/// </summary>
internal static class FpMovement
{
    private static bool _moving, _broken, _autoClick;
    private static float _nextSend, _pressTime;
    private static Vector3 _pressPos;
    private static Transform _target;
    private static bool _checkedAuto;
    private static int _errors;
    private static bool _wsrBroken, _typingBroken, _loggedWsr;
    private static float _lastX, _lastY, _lastYaw, _lastSend;
    /// <summary>Third person WoW: both mouse buttons held = walk forward.</summary>
    internal static bool ExtraForward;
    /// <summary>Any movement key held this frame (third-person auto follow).</summary>
    internal static bool AnyKey { get; private set; }
    internal static string MethodName => !Plugin.KeyboardMove.Value ? "off" : _broken ? "broken" : Current().ToString();

    internal static void Enter(ActorCameraController c, Transform target) { _moving = false; _target = target; _checkedAuto = false; }

    private static LocalInputHandler Handler()
    {
        try { return LocalInputHandler.Instance; } catch { return null; }
    }

    /// <summary>True while a text field (chat, search) has focus - keys are text then, not movement.</summary>
    internal static bool Typing()
    {
        if (_typingBroken) return false;
        try { return UIInput.selection != null; }
        catch (Exception ex) { _typingBroken = true; Plugin.L.LogWarning("CameraMod: UIInput.selection unavailable: " + ex.Message); return false; }
    }

    private static MoveMethod Current()
    {
        var m = Plugin.MoveMethodCfg.Value;
        if (m == MoveMethod.Auto) return _autoClick ? MoveMethod.ClickMove : MoveMethod.InputAction;
        return m;
    }

    /// <summary>Inverse of the game's WorldSpaceRotation (read from the game when possible, else config YawCompensation).</summary>
    private static Quaternion ToGameInput(LocalInputHandler h)
    {
        if (!_wsrBroken)
        {
            try
            {
                Quaternion q = h.WorldSpaceRotation;
                if (!_loggedWsr) { _loggedWsr = true; Plugin.L.LogInfo($"CameraMod: LocalInputHandler.WorldSpaceRotation yaw = {q.eulerAngles.y:0.#} deg (input is pre-rotated by the inverse)"); }
                return Quaternion.Inverse(q);
            }
            catch (Exception ex) { _wsrBroken = true; Plugin.L.LogWarning("CameraMod: WorldSpaceRotation unavailable, using YawCompensation: " + ex.Message); }
        }
        return Quaternion.Euler(0, Plugin.MoveYawCompensation.Value, 0);
    }

    internal static void Update(float yaw)
    {
        if (_broken || !Plugin.KeyboardMove.Value) return;
        float x = 0, y = 0;
        if (!Typing())
        {
            if (Input.GetKey(Plugin.MoveForwardKey.Value)) y += 1;
            if (Input.GetKey(Plugin.MoveBackKey.Value)) y -= 1;
            if (Input.GetKey(Plugin.MoveRightKey.Value)) x += 1;
            if (Input.GetKey(Plugin.MoveLeftKey.Value)) x -= 1;
            if (ExtraForward && y == 0) y = 1;
        }
        AnyKey = x != 0 || y != 0;

        if (x == 0 && y == 0)
        {
            if (_moving)
            {
                _moving = false;
                try { Handler()?.StopMovementAction(); } catch (Exception ex) { Plugin.L.LogWarning("CameraMod: StopMovementAction: " + ex.Message); }
            }
            return;
        }

        float now = Time.unscaledTime;
        bool start = !_moving;
        if (start)
        {
            _pressTime = now;
            _pressPos = _target != null ? _target.position : Vector3.zero;
            _checkedAuto = false;
            _nextSend = 0;
        }

        // Auto: no position change 0.5 s after the press -> use the click-move path from now on
        if (Plugin.MoveMethodCfg.Value == MoveMethod.Auto && !_autoClick && !_checkedAuto && now - _pressTime > 0.5f && _target != null)
        {
            _checkedAuto = true;
            float moved = (_target.position - _pressPos).magnitude;
            if (moved < 0.05f)
            {
                _autoClick = true;
                Plugin.L.LogWarning($"CameraMod: first-person move: InputAction did not move the player in 0.5 s (moved {moved:0.###} m) -> MoveMethod Auto switched to ClickMove (DoMoveTriggered)");
            }
            else Plugin.L.LogInfo($"CameraMod: first-person move: InputAction works (moved {moved:0.##} m in 0.5 s)");
        }

        bool keysChanged = start || x != _lastX || y != _lastY;
        float dyaw = Math.Abs(((yaw - _lastYaw) % 360f + 540f) % 360f - 180f);
        bool turned = dyaw > 3f && now - _lastSend > 0.033f;
        if (!keysChanged && !turned && now < _nextSend) return;
        _nextSend = now + Math.Max(0.02f, Plugin.MoveIntervalMs.Value / 1000f);
        _lastX = x; _lastY = y; _lastYaw = yaw; _lastSend = now;

        var h = Handler();
        if (h == null) { if (start) Plugin.L.LogWarning("CameraMod: first-person move: LocalInputHandler.Instance is null"); return; }
        Vector3 world = Quaternion.Euler(0, yaw, 0) * (new Vector3(x, 0, y).normalized * Math.Max(0.5f, Plugin.MoveLookAhead.Value));
        var method = Current();
        try
        {
            string res;
            if (method == MoveMethod.ClickMove)
            {
                Vector3 from = _target != null ? _target.position : h.transform.position;
                var r = h.DoMoveTriggered(from + world);
                res = r.ToString();
            }
            else
            {
                Vector3 v = ToGameInput(h) * world;
                bool ok = h.HandleInputActionsMove(new Vector2(v.x, v.z), keysChanged);
                res = ok.ToString();
            }
            _moving = true;
            if (start) Plugin.L.LogInfo($"CameraMod: first-person move: dir=({x:0},{y:0}) yaw={yaw:0} world=({world.x:0.0},{world.z:0.0}) via {(method == MoveMethod.ClickMove ? "LocalInputHandler.DoMoveTriggered" : "LocalInputHandler.HandleInputActionsMove")} -> {res}");
        }
        catch (Exception ex)
        {
            _errors++;
            Plugin.L.LogError($"CameraMod: first-person move via {method} failed ({_errors}/5): {ex}");
            if (method == MoveMethod.InputAction && Plugin.MoveMethodCfg.Value == MoveMethod.Auto) { _autoClick = true; Plugin.L.LogWarning("CameraMod: first-person move: switching to ClickMove"); }
            else if (_errors >= 5) { _broken = true; Plugin.L.LogError("CameraMod: first-person keyboard movement disabled after errors"); }
        }
    }

    internal static void Exit()
    {
        ExtraForward = false; AnyKey = false;
        if (_moving) { _moving = false; try { Handler()?.StopMovementAction(); } catch { } }
        _target = null;
    }
}
