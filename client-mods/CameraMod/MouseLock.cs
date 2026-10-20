using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CameraMod;

public enum LookMode { HoldRight, Toggle, Always }

/// <summary>
/// Mouse look for first person. Cursor.lockState's setter is stripped from this build (only the getter is in the
/// metadata), so the lock is done with Win32: the cursor is hidden (Cursor.visible) and warped back every frame to the
/// "game cursor point"; the movement since the last warp is the look delta. Side effect, on purpose: everything the game
/// derives from Input.mousePosition (hover/target under the cursor, click actions, spell ground aim) uses that point.
/// The point is the screen centre (crosshair), except when the crosshair ray does not reach the ground within
/// AimMaxDistance: then CameraLogic sets AimPoint to the projection of a ground point in front of the player, so
/// ground-target spells do not fall back to the player position (LocalInputHandler.RayCastGroundHit behaviour).
/// v4: Always mode (FPS), free cursor on FreeCursorKey hold / MouseLockKey toggle / open game window / chat focus.
/// </summary>
internal static class MouseLock
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

    internal static bool Locked { get; private set; }
    /// <summary>Why the cursor is free right now (for the overlay), "" when captured.</summary>
    internal static string FreeReason { get; private set; } = "";
    /// <summary>Game cursor point in Unity screen pixels (origin bottom-left); null = screen centre.</summary>
    internal static Vector2? AimPoint;
    /// <summary>Where the cursor was put this frame (Unity screen pixels, origin bottom-left), for the overlay.</summary>
    internal static Vector2 CursorScreen;

    private static bool _toggled;
    private static POINT _warp;
    private static bool _skipFirst;
    private static readonly uint _pid = (uint)Process.GetCurrentProcess().Id;

    /// <summary>Is the game window focused (foreground window belongs to this process)?</summary>
    private static bool Focused(out IntPtr hwnd)
    {
        hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == _pid;
    }

    private static bool KeyHeld(KeyCode k) => k != KeyCode.None && Input.GetKey(k);

    /// <summary>Per frame in first person. Returns the look delta in pixels (x right, y up), zero when not looking.</summary>
    internal static Vector2 Update(bool firstThisFrame)
    {
        if (!firstThisFrame) return Vector2.zero;
        var mode = Plugin.LookMode.Value;
        if (Plugin.MouseLockKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.MouseLockKey.Value))
        {
            _toggled = !_toggled;
            bool lookNow = mode == LookMode.Always ? !_toggled : _toggled;
            StatusOverlay.Show(lookNow ? "mouse look ON (cursor held at the crosshair)" : "mouse look OFF (free cursor)");
        }

        string reason = "";
        bool want;
        if (mode == LookMode.Always) { want = !_toggled; if (!want) reason = Plugin.MouseLockKey.Value + " toggle"; }
        else
        {
            want = _toggled || (mode == LookMode.HoldRight && Input.GetMouseButton(1));
            if (!want) reason = mode == LookMode.HoldRight ? "hold RMB to look" : Plugin.MouseLockKey.Value + " to look";
        }
        if (want && KeyHeld(Plugin.FreeCursorKey.Value)) { want = false; reason = Plugin.FreeCursorKey.Value + " held"; }
        if (want && Plugin.AutoFreeCursor.Value)
        {
            if (FpMovement.Typing()) { want = false; reason = "text input"; }
            else if (FpControls.UiOpen(out string which)) { want = false; reason = "window open" + (which.Length > 0 ? " (" + which + ")" : ""); }
        }

        bool focused = Focused(out var hwnd);
        if (want && !focused) reason = "game not focused";
        FreeReason = want && focused ? "" : reason;
        if (want && focused) { if (!Locked) Lock(); }
        else if (Locked) Unlock();

        CentreCaptured = Locked;
        if (!Locked) return Vector2.zero;
        return WarpCentre(hwnd);
    }

    /// <summary>Cursor held at the window centre / aim point (FP, third-person Shooter, WoW mouse-look toggle).</summary>
    internal static bool CentreCaptured { get; private set; }
    /// <summary>Third-person WoW: a mouse button is held and drives the camera (cursor hidden, parked where it was pressed).</summary>
    internal static bool WowDragging => _wowBtn >= 0;
    /// <summary>Third-person WoW: both mouse buttons held (= move forward).</summary>
    internal static bool BothButtons { get; private set; }
    internal static bool WowToggled => _wowToggled;
    /// <summary>Third-person WoW: the right button is held as mouse look (cursor parked).</summary>
    internal static bool WowRightHeld { get; private set; }
    /// <summary>Pixels the mouse moved since the right button went down (WoW), for the click-or-drag decision on release.</summary>
    internal static float RmbDragPx { get; private set; }
    private static long _rmbDownTs;
    private static bool _rmbTracked;
    /// <summary>Seconds the right button has been (or was) held.</summary>
    internal static float RmbHeldSec => _rmbDownTs == 0 ? 0f : (float)((Stopwatch.GetTimestamp() - _rmbDownTs) / (double)Stopwatch.Frequency);
    /// <summary>Was the last right press a click (no drag, short)? Read by the click filter on the release event.</summary>
    internal static bool RmbWasClick() => _rmbDownTs != 0 && RmbDragPx <= Math.Max(0f, Plugin.TpRightClickMaxDrag.Value) && RmbHeldSec <= Math.Max(0.05f, Plugin.TpRightClickMaxSec.Value);

    private static int _wowBtn = -1;
    private static POINT _anchor;
    private static bool _wowToggled;

    /// <summary>
    /// Third person, WoW scheme: the cursor is free and visible; holding the right button = mouse look that also turns the
    /// movement heading (Turn), holding the left button = orbit the camera only (Orbit). While a button is held the cursor is
    /// hidden and parked at the point where it was pressed (the game sees it there, so a click without drag is a normal click
    /// at that point). A press over the UI does not start a drag. MouseLockKey toggles permanent mouse look (centre capture).
    /// </summary>
    internal static Vector2 UpdateWow(out LookKind kind)
    {
        kind = LookKind.None;
        BothButtons = false;
        WowRightHeld = false;
        // right button bookkeeping (press time, drag distance); reset on the next press, so the release event still sees it
        bool rNow = Input.GetMouseButton(1);
        if (rNow && !_rmbTracked) { _rmbTracked = true; RmbDragPx = 0; _rmbDownTs = Stopwatch.GetTimestamp(); }
        else if (!rNow) _rmbTracked = false;
        if (Plugin.MouseLockKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.MouseLockKey.Value))
        {
            _wowToggled = !_wowToggled;
            if (_wowBtn >= 0) { _wowBtn = -1; if (Locked) Unlock(); }
            StatusOverlay.Show(_wowToggled ? "third person: mouse look ON (cursor held at the crosshair)" : "third person: mouse look OFF (free cursor, hold RMB to look, LMB to orbit)");
        }
        bool focused = Focused(out var hwnd);
        bool blocked = false; string reason = "";
        if (KeyHeld(Plugin.FreeCursorKey.Value)) { blocked = true; reason = Plugin.FreeCursorKey.Value + " held"; }
        else if (FpMovement.Typing()) { blocked = true; reason = "text input"; }
        else if (Plugin.AutoFreeCursor.Value && FpControls.UiOpen(out string which)) { blocked = true; reason = "window open" + (which.Length > 0 ? " (" + which + ")" : ""); }
        else if (!focused) { blocked = true; reason = "game not focused"; }

        if (_wowToggled)
        {
            if (_wowBtn >= 0) _wowBtn = -1;
            if (blocked) { FreeReason = reason; CentreCaptured = false; if (Locked) Unlock(); return Vector2.zero; }
            FreeReason = "";
            if (!Locked) Lock();
            CentreCaptured = true;
            kind = LookKind.Turn;
            return WarpCentre(hwnd);
        }

        CentreCaptured = false;
        AimPoint = null;
        bool r = Input.GetMouseButton(1), l = Input.GetMouseButton(0);
        if (_wowBtn < 0 && focused && !KeyHeld(Plugin.FreeCursorKey.Value) && !FpMovement.Typing())
        {
            bool rd = Input.GetMouseButtonDown(1), ld = Input.GetMouseButtonDown(0);
            if ((rd || ld) && !CameraLogic.OverUiPublic())
            {
                _wowBtn = rd ? 1 : 0;
                GetCursorPos(out _anchor);
                if (!Locked) { Locked = true; try { Cursor.visible = false; } catch { } }
            }
        }
        if (_wowBtn >= 0 && !r && !l)
        {
            _wowBtn = -1;
            if (Locked) Unlock();
        }
        if (_wowBtn < 0)
        {
            if (Locked) Unlock();
            FreeReason = blocked ? reason : "hold RMB = look, LMB = orbit";
            return Vector2.zero;
        }
        FreeReason = "";
        if (!GetCursorPos(out var cur)) return Vector2.zero;
        var d = new Vector2(cur.X - _anchor.X, -(cur.Y - _anchor.Y));
        if (cur.X != _anchor.X || cur.Y != _anchor.Y) SetCursorPos(_anchor.X, _anchor.Y);
        try { if (Cursor.visible) Cursor.visible = false; } catch { }
        kind = r ? LookKind.Turn : LookKind.Orbit;
        BothButtons = r && l;
        WowRightHeld = r;
        if (r) RmbDragPx += Math.Abs(d.x) + Math.Abs(d.y);
        return d;
    }

    private static Vector2 WarpCentre(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rc)) return Vector2.zero;
        int w = rc.R - rc.L, h = rc.B - rc.T;
        if (w <= 0 || h <= 0) return Vector2.zero;
        // target point in client pixels: centre, or the aim point (Unity screen coords are bottom-up)
        float sx = w / 2f, sy = h / 2f;
        if (AimPoint.HasValue)
        {
            float scaleX = Screen.width > 0 ? (float)w / Screen.width : 1f, scaleY = Screen.height > 0 ? (float)h / Screen.height : 1f;
            var a = AimPoint.Value;
            sx = Math.Clamp(a.x * scaleX, 1, w - 2);
            sy = Math.Clamp(h - a.y * scaleY, 1, h - 2);
        }
        var target = new POINT { X = (int)sx, Y = (int)sy };
        if (!ClientToScreen(hwnd, ref target)) return Vector2.zero;
        if (!GetCursorPos(out var cur)) return Vector2.zero;
        var d = new Vector2(cur.X - _warp.X, -(cur.Y - _warp.Y));
        SetCursorPos(target.X, target.Y);
        _warp = target;
        CursorScreen = new Vector2(Screen.width > 0 ? sx * Screen.width / w : sx, Screen.height > 0 ? (h - sy) * Screen.height / h : h - sy);
        try { if (Cursor.visible) Cursor.visible = false; } catch { }
        if (_skipFirst) { _skipFirst = false; return Vector2.zero; }
        return d;
    }

    private static void Lock()
    {
        Locked = true;
        _skipFirst = true;
        try { Cursor.visible = false; } catch { }
    }

    internal static void Unlock()
    {
        if (!Locked) return;
        Locked = false;
        try { Cursor.visible = true; } catch { }
    }

    /// <summary>Leaving first person: release and forget the toggle.</summary>
    internal static void Reset() { Unlock(); _toggled = false; AimPoint = null; FreeReason = ""; _wowBtn = -1; _wowToggled = false; CentreCaptured = false; BothButtons = false; WowRightHeld = false; _rmbTracked = false; _rmbDownTs = 0; RmbDragPx = 0; }
}

public enum RightMouseMode { Game, None, Hotkey }
public enum LookKind { None, Turn, Orbit }
