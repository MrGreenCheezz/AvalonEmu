using System;
using System.Diagnostics;
using UnityEngine;

namespace CameraMod;

/// <summary>
/// Injected MonoBehaviour (IMGUI, optional): status line for a few seconds, the first-person crosshair (+ a small dot where
/// the game cursor is when the ground-aim correction moved it), a compact first-person info line and the key hint panel (F1).
/// All strings are formatted in managed code (see the crash rule in NOTES.md).
/// v7 (performance): OnGUI runs for every IMGUI event (Layout, Repaint, each mouse/key event); everything is drawn only on
/// Repaint (no GUILayout is used, so Layout needs nothing). Texts are cached as GUIContent (no string marshalling per call),
/// the info line is rebuilt at most 5x per second, GUIStyles and the white texture are fetched once.
/// </summary>
public class StatusOverlay : MonoBehaviour
{
    public StatusOverlay(IntPtr ptr) : base(ptr) { }

    internal static string Text = "";
    private static long _untilTs;
    private static bool _failed;
    private static string _hint = "", _hintKey = "";
    private static int _hintLines;

    private static GUIStyle _label, _box;
    private static Texture2D _white;
    private static GUIContent _empty, _lineC, _hintC, _textC;
    private static string _lineS, _textS;
    private static long _nextLine;
    private static readonly long LineTicks = Stopwatch.Frequency / 5;

    private static GUIContent Content(ref GUIContent c, ref string cached, string s)
    {
        if (c == null || !ReferenceEquals(cached, s)) { c = new GUIContent(s); cached = s; }
        return c;
    }

    public void OnGUI()
    {
        if (_failed || Plugin.ShowStatus == null) return;
        long t0 = Perf.Begin();
        try
        {
            bool fp = CameraLogic.InCloseCamera;
            bool status = Plugin.ShowStatus.Value && Text.Length > 0 && Stopwatch.GetTimestamp() <= _untilTs;
            if (!fp && !status) return;
            var ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;
            if (_label == null)
            {
                var skin = GUI.skin;
                _label = skin.label; _box = skin.box;
                _white = Texture2D.whiteTexture;
                _empty = new GUIContent("");
            }

            if (fp && CameraLogic.CrosshairWanted)
            {
                float cx = Screen.width / 2f, cy = Screen.height / 2f, s = Math.Max(2f, Plugin.CrosshairSize.Value);
                var tex = _white;
                bool outline = Plugin.CrosshairOutline.Value;
                Color old = GUI.color;
                if (outline)
                {
                    // dark outline first (1 px around each bar), then the white bars on top
                    GUI.color = new Color(0f, 0f, 0f, 0.85f);
                    GUI.DrawTexture(new Rect(cx - s - 1, cy - 2, s * 2 + 2, 4), tex);
                    GUI.DrawTexture(new Rect(cx - 2, cy - s - 1, 4, s * 2 + 2), tex);
                    GUI.color = old;
                }
                GUI.DrawTexture(new Rect(cx - s, cy - 1, s * 2, 2), tex);
                GUI.DrawTexture(new Rect(cx - 1, cy - s, 2, s * 2), tex);
                if (MouseLock.Locked && MouseLock.AimPoint.HasValue)
                {
                    var a = MouseLock.CursorScreen; // bottom-left origin -> GUI top-left
                    float gx = a.x, gy = Screen.height - a.y;
                    if (Math.Abs(gx - cx) + Math.Abs(gy - cy) > 6)
                    {
                        if (outline) { GUI.color = new Color(0f, 0f, 0f, 0.85f); GUI.DrawTexture(new Rect(gx - 3, gy - 3, 6, 6), tex); GUI.color = old; }
                        GUI.DrawTexture(new Rect(gx - 2, gy - 2, 4, 4), tex);
                    }
                }
            }
            if (fp)
            {
                long now = Stopwatch.GetTimestamp();
                if (_lineS == null || now >= _nextLine) { _nextLine = now + LineTicks; BuildLine(); }
                GUI.Label(new Rect(10, Screen.height - 26, 900, 22), _lineC, _label);
                if (FpControls.HintVisible) DrawHint();
            }
            if (!status) return;
            GUI.Box(new Rect(10, 10, 620, 26), _empty, _box);
            GUI.Label(new Rect(18, 13, 610, 22), Content(ref _textC, ref _textS, Text), _label);
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.L.LogWarning("CameraMod: IMGUI overlay failed, log only from now: " + ex.Message);
        }
        finally { Perf.End(Perf.Gui, t0); }
    }

    private static void BuildLine()
    {
        string cur = MouseLock.Locked ? "cursor: captured" : "cursor: free" + (MouseLock.FreeReason.Length > 0 ? " (" + MouseLock.FreeReason + ")" : "");
        string line;
        if (CameraLogic.InThirdPerson)
        {
            string look = Plugin.TpLookMode.Value == TpLook.WoW ? (MouseLock.WowToggled ? "WoW, mouse look on" : "WoW") : "Shooter";
            if (MouseLock.WowDragging) cur = MouseLock.BothButtons ? "look + walk" : MouseLock.WowRightHeld ? "looking (RMB)" : "orbit (LMB)";
            line = "3rd person (" + look + ")  |  " + cur + "  |  dist " + ThirdPerson.Distance.ToString("0.0") + " m" + (ThirdPerson.Blocked ? " (blocked)" : "")
                + "  |  shoulder " + ThirdPerson.Shoulder + "  |  " + Plugin.ModeKey.Value + " = next view  |  " + Plugin.HintKey.Value + " = keys";
        }
        else line = "1st person  |  " + cur + "  |  move: " + FpMovement.MethodName + "  |  " + Plugin.ModeKey.Value + " = next view  |  " + Plugin.HintKey.Value + " = keys";
        if (line != _lineS) { _lineS = line; _lineC = new GUIContent(line); }
    }

    private static void DrawHint()
    {
        string key = _hintKey;
        if (_hintC == null || (Time.frameCount & 31) == 0)
            key = CameraLogic.Mode + "|" + Plugin.TpLookMode.Value + "|" + Plugin.FpSpellKeys.Value + "|" + Plugin.RightMouse.Value + "|" + Plugin.InteractKey.Value + "|" + Plugin.FreeCursorKey.Value + "|" + Plugin.LookMode.Value;
        if (_hintC == null || key != _hintKey)
        {
            _hintKey = key;
            string rmb = Plugin.RightMouse.Value switch
            {
                RightMouseMode.None => "nothing",
                RightMouseMode.Hotkey => "game key " + Plugin.RightMouseHotkey.Value,
                _ => "game right click"
            };
            string mf = FpKeys.KeyName(Plugin.MoveForwardKey.Value), ml = FpKeys.KeyName(Plugin.MoveLeftKey.Value), mb = FpKeys.KeyName(Plugin.MoveBackKey.Value), mr = FpKeys.KeyName(Plugin.MoveRightKey.Value);
            string[] lines;
            if (CameraLogic.InThirdPerson && Plugin.TpLookMode.Value == TpLook.WoW)
                lines = new[]
                {
                    "THIRD PERSON, WoW  (" + Plugin.ModeKey.Value + " = next view, " + Plugin.FirstPersonKey.Value + " = first person)",
                    "Hold RMB: look + turn movement (never walks)    Hold LMB: orbit camera",
                    "Both buttons: walk forward    " + mf + ml + mb + mr + ": walk",
                    "Left click (no drag): attack / use what is under the cursor",
                    "Right click (no drag): " + (Plugin.RightMouse.Value == RightMouseMode.Game ? "game right click (attack)" : "nothing"),
                    "Wheel: distance    " + Plugin.TpShoulderKey.Value + ": swap shoulder",
                    Plugin.InteractKey.Value + ": interact with what is under the cursor",
                    "Spells: Q E R F 1 2 as usual;  remap: " + FpKeys.RemapText(),
                    Plugin.MouseLockKey.Value + ": permanent mouse look (crosshair) on/off",
                    Plugin.HintKey.Value + ": hide this panel",
                };
            else if (CameraLogic.InThirdPerson)
                lines = new[]
                {
                    "THIRD PERSON, Shooter  (" + Plugin.ModeKey.Value + " = next view, " + Plugin.FirstPersonKey.Value + " = first person)",
                    "Mouse: look    " + mf + ml + mb + mr + ": walk",
                    "Left mouse: attack / use what is under the crosshair",
                    "Right mouse: " + rmb,
                    "Wheel: distance    " + Plugin.TpShoulderKey.Value + ": swap shoulder",
                    Plugin.InteractKey.Value + ": interact (loot, chest, NPC, resource)",
                    "Spells: Q E R F 1 2 as usual;  remap: " + FpKeys.RemapText(),
                    "Hold " + Plugin.FreeCursorKey.Value + " / " + Plugin.MouseLockKey.Value + ": free cursor (also automatic in windows / chat)",
                    Plugin.HintKey.Value + ": hide this panel",
                };
            else
                lines = new[]
                {
                    "FIRST PERSON  (" + Plugin.FirstPersonKey.Value + " = back to the normal view, " + Plugin.ModeKey.Value + " = next view)",
                    "Mouse: look    " + mf + ml + mb + mr + ": walk",
                    "Left mouse: attack / use what is under the crosshair",
                    "Right mouse: " + rmb,
                    Plugin.InteractKey.Value + ": interact (loot, chest, NPC, resource)",
                    "Spells: Q E R F 1 2 as usual;  remap: " + FpKeys.RemapText(),
                    "Alt + spell key: cast on yourself",
                    "Hold " + Plugin.FreeCursorKey.Value + " / " + Plugin.MouseLockKey.Value + ": free cursor (also automatic in windows / chat)",
                    Plugin.HintKey.Value + ": hide this panel",
                };
            _hint = string.Join("\n", lines);
            _hintLines = lines.Length;
            _hintC = new GUIContent(_hint);
        }
        float h = 10 + _hintLines * 19, w = 560;
        var r = new Rect(Screen.width - w - 12, 46, w, h);
        GUI.Box(r, _empty, _box);
        GUI.Label(new Rect(r.x + 8, r.y + 4, w - 12, h), _hintC, _label);
    }

    internal static void Show(string text)
    {
        Plugin.L.LogInfo("CameraMod: " + text);
        Text = text;
        _untilTs = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
    }
}
