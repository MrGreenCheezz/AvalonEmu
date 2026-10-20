using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace CameraMod;

public enum PerfPreset { Custom, Performance }

/// <summary>
/// Lightweight profiler + the performance preset.
/// Each subsystem is wrapped in Stopwatch timestamps (managed, no interop); every ProfileIntervalSec (and on every camera
/// mode change) one line is logged: frame rate / frame time of the window, the mod's total ms per frame and the
/// per-subsystem averages and worst single call, plus the render knobs that cost GPU/CPU time in the close camera
/// (far clip, fog end, shadow distance, cascades, lodBias, layer culling). All formatting is managed (NOTES.md crash rule).
/// </summary>
internal static class Perf
{
    internal const int Total = 0, Pose = 1, Collision = 2, Body = 3, Effects = 4, Fog = 5, Move = 6, Keys = 7, Controls = 8,
        Sky = 9, View = 10, Aim = 11, Gui = 12, Click = 13, Hotkey = 14, FogHook = 15, Count = 16;
    private static readonly string[] Names =
        { "total", "pose", "collision", "body", "effects", "fog", "move", "keys", "controls", "sky", "view", "aim", "gui", "click", "hotkey", "fogHook" };

    private static readonly long[] _ticks = new long[Count], _max = new long[Count];
    private static readonly int[] _calls = new int[Count];
    private static long _windowStart, _lastFrame, _frameSum, _frameMax;
    private static int _frames;
    private static CamMode _windowMode = CamMode.TopDown;
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    internal static bool On => Plugin.ProfileLog != null && Plugin.ProfileLog.Value;

    // ---------------- effective preset values ----------------
    internal static bool Performance => Plugin.PerfPresetCfg != null && Plugin.PerfPresetCfg.Value == PerfPreset.Performance;
    /// <summary>Far clip in the close camera. Performance: 600 (may be lower than the game's 1000), fitted to the fog end.</summary>
    internal static float FarClip => Performance ? 600f : Plugin.FirstPersonFarClip.Value;
    internal static bool FarClipMayLower => Performance;
    internal static float LodBias => Performance ? 1.5f : Plugin.LodBias.Value;
    internal static bool ClearLayerCull => !Performance && Plugin.DisableLayerCullDistances.Value;
    internal static bool FitFarToFog => Plugin.FarClipFitFog.Value;
    /// <summary>Frames between scans of attached effects / own renderers (time-sliced).</summary>
    internal static int EffectsScanFrames => Performance ? 60 : Math.Max(4, Plugin.EffectsScanFrames.Value);
    /// <summary>Frames between re-reads of the character's renderer list (equipment changes).</summary>
    internal static int BodyRescanFrames => Performance ? 120 : Math.Max(10, Plugin.EffectsScanFrames.Value * 3);
    /// <summary>Frames between scene-wide searches (deep skinned-mesh scan, nameplates).</summary>
    internal static int SceneScanFrames => Performance ? 600 : 180;
    internal static int SliceSize => Performance ? 32 : 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Begin() => On ? Stopwatch.GetTimestamp() : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void End(int s, long t0)
    {
        if (t0 == 0) return;
        long d = Stopwatch.GetTimestamp() - t0;
        _ticks[s] += d; _calls[s]++;
        if (d > _max[s]) _max[s] = d;
    }

    /// <summary>Once per rendered frame (first controller call of the frame).</summary>
    internal static void Frame(CamMode mode)
    {
        if (!On) return;
        long now = Stopwatch.GetTimestamp();
        if (_windowStart == 0) { _windowStart = now; _lastFrame = now; _windowMode = mode; return; }
        if (mode != _windowMode) { Flush("mode change"); _windowMode = mode; _windowStart = now; _lastFrame = now; return; }
        long dt = now - _lastFrame;
        _lastFrame = now;
        _frames++; _frameSum += dt; if (dt > _frameMax) _frameMax = dt;
        double interval = Math.Max(2f, Plugin.ProfileIntervalSec.Value);
        if ((now - _windowStart) * TickMs >= interval * 1000.0) { Flush("periodic"); _windowStart = now; }
    }

    private static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    private static void Flush(string why)
    {
        try
        {
            if (_frames < 10) { Reset(); return; }
            double secs = _frameSum * TickMs / 1000.0;
            double avgFrame = _frameSum * TickMs / _frames;
            var sb = new StringBuilder(512);
            sb.Append("CameraMod perf [").Append(ModeName(_windowMode)).Append(", ").Append(F1(secs)).Append(" s, ").Append(_frames).Append(" frames")
              .Append(why == "periodic" ? "" : ", " + why).Append("]: fps ").Append(F1(avgFrame > 0 ? 1000.0 / avgFrame : 0))
              .Append(" (frame avg ").Append(F2(avgFrame)).Append(" ms, max ").Append(F1(_frameMax * TickMs)).Append(" ms); mod ")
              .Append(F2(_ticks[Total] * TickMs / _frames)).Append(" ms/frame");
            // per-subsystem: avg ms per frame (max single call ms)
            for (int i = 1; i < Count; i++)
            {
                if (_calls[i] == 0) continue;
                double avg = _ticks[i] * TickMs / _frames;
                double mx = _max[i] * TickMs;
                if (avg < 0.005 && mx < 0.2) continue; // negligible
                sb.Append(", ").Append(Names[i]).Append(' ').Append(F2(avg));
                if (mx >= 0.5) sb.Append(" (max ").Append(F1(mx)).Append(')');
                if (i == Gui || i == Click || i == Hotkey || i == FogHook) sb.Append(" x").Append(F1((double)_calls[i] / _frames)).Append("/f");
            }
            sb.Append(" | render: ").Append(RenderState());
            Plugin.L.LogInfo(sb.ToString());
        }
        catch (Exception ex) { Plugin.L.LogWarning("CameraMod: perf log failed: " + ex.Message); }
        Reset();
    }

    private static void Reset()
    {
        Array.Clear(_ticks, 0, Count); Array.Clear(_max, 0, Count); Array.Clear(_calls, 0, Count);
        _frames = 0; _frameSum = 0; _frameMax = 0;
    }

    private static string ModeName(CamMode m) => m switch { CamMode.FirstPerson => "first person", CamMode.ThirdPerson => "third person", _ => "top-down" };

    private static string RenderState()
    {
        var sb = new StringBuilder(200);
        try
        {
            var cam = CameraLogic.CloseCam ?? Camera.main;
            if (cam != null) sb.Append("far ").Append(F1(cam.farClipPlane)).Append(", fov ").Append(F1(cam.fieldOfView)).Append(", ");
            sb.Append("fog ").Append(RenderSettings.fog ? "on" : "off").Append(" end ").Append(F1(RenderSettings.fogEndDistance))
              .Append(", shadows ").Append(F1(QualitySettings.shadowDistance)).Append(" m x").Append(QualitySettings.shadowCascades)
              .Append(" cascades, lodBias ").Append(F2(QualitySettings.lodBias))
              .Append(", layerCull ").Append(CameraLogic.LayerCullCleared ? "cleared" : "game")
              .Append(", preset ").Append(Performance ? "Performance" : "Custom")
              .Append(", effects hidden ").Append(FpEffects.HiddenCount).Append(", body hidden ").Append(BodyHider.HiddenCount);
        }
        catch (Exception ex) { sb.Append("? (").Append(ex.GetType().Name).Append(')'); }
        return sb.ToString();
    }

    // ---------------- helpers shared by the scanners ----------------

    /// <summary>Native element pointers of an IL2CPP reference array (no wrapper / GC handle per element).</summary>
    internal static IntPtr[] Ptrs(Il2CppObjectBase arr, int length)
    {
        var res = new IntPtr[length];
        if (arr == null || length <= 0) return res;
        IntPtr start = IntPtr.Add(arr.Pointer, 4 * IntPtr.Size);
        for (int i = 0; i < length; i++) res[i] = Marshal.ReadIntPtr(start, i * IntPtr.Size);
        return res;
    }

    /// <summary>Pointers of every Transform under root (inclusive, inactive too), read straight from the native array.</summary>
    internal static void Subtree(Transform root, HashSet<IntPtr> into)
    {
        into.Clear();
        if (root == null) return;
        var arr = root.GetComponentsInChildren<Transform>(true);
        if (arr == null) return;
        foreach (var p in Ptrs(arr, arr.Length)) if (p != IntPtr.Zero) into.Add(p);
    }
}
