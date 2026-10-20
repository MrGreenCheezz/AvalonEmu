using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace CameraMod;

/// <summary>
/// v5 camera diagnostics: one log line per Camera in the scene (enabled or not) with viewport, target texture, clear,
/// culling mask, clip planes, rendering path and the *effective* projection (vertical FOV / aspect / off-centre shift implied
/// by Camera.projectionMatrix, compared with fieldOfView/aspect), plus the Behaviours on the camera's GameObject, renderers
/// parented under the main camera, scene renderers around the eye, UIViewport (NGUI, the only game code that writes
/// Camera.rect), ReduceResolution and dynamic-resolution state.
/// All text is formatted in managed code (NOTES.md crash rule); every Unity member used here has a NativeMethodInfoPtr in
/// the interop (lensShift / sensorSize / gateFit are stripped in this build and reported as n/a). Rect / Matrix4x4 / Bounds
/// are read through their fields, not through managed-IL property bodies.
/// </summary>
internal static class CamDiag
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static string F(float v) => v.ToString("0.###", Inv);

    internal static string R(Rect r) => "(" + F(r.m_XMin) + "," + F(r.m_YMin) + " " + F(r.m_Width) + "x" + F(r.m_Height) + ")";

    internal static bool IsFull(Rect r) =>
        Math.Abs(r.m_XMin) < 1e-4f && Math.Abs(r.m_YMin) < 1e-4f && Math.Abs(r.m_Width - 1f) < 1e-4f && Math.Abs(r.m_Height - 1f) < 1e-4f;

    private static string Hex(Color c)
    {
        static int B(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255f);
        return "#" + B(c.r).ToString("X2") + B(c.g).ToString("X2") + B(c.b).ToString("X2");
    }

    internal static string PathOf(Transform t)
    {
        var parts = new List<string>();
        for (int i = 0; t != null && i < 5; i++, t = t.parent) parts.Insert(0, t.name);
        if (t != null) parts.Insert(0, "...");
        return string.Join("/", parts);
    }

    internal static List<Camera> AllCameras()
    {
        var list = new List<Camera>();
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Camera>()))
        {
            var c = o.TryCast<Camera>();
            if (c != null) list.Add(c);
        }
        return list;
    }

    /// <summary>Effective projection: vertical FOV = 2 atan(1/m11), aspect = m11/m00, shift = m02/m12.</summary>
    internal static string Projection(Camera c, out bool custom, out float implFov)
    {
        custom = false; implFov = 0;
        try
        {
            var p = c.projectionMatrix;
            if (c.orthographic) return "proj ortho m00 " + F(p.m00) + " m11 " + F(p.m11);
            implFov = (float)(2.0 * Math.Atan(1.0 / Math.Abs(p.m11)) * 180.0 / Math.PI);
            float implAspect = Math.Abs(p.m00) > 1e-6f ? Math.Abs(p.m11 / p.m00) : 0;
            custom = Math.Abs(implFov - c.fieldOfView) > 0.2f || Math.Abs(implAspect - c.aspect) > 0.01f
                     || Math.Abs(p.m02) > 1e-4f || Math.Abs(p.m12) > 1e-4f;
            return "proj fov " + F(implFov) + " aspect " + F(implAspect) + " shift (" + F(p.m02) + "," + F(p.m12) + ")" + (p.m00 < 0 ? " MIRRORED" : "")
                   + (custom ? " CUSTOM (does not match fieldOfView/aspect)" : " = fov/aspect");
        }
        catch (Exception ex) { return "proj ? (" + ex.GetType().Name + ")"; }
    }

    internal static string Describe(Camera c)
    {
        var sb = new StringBuilder();
        sb.Append('\'').Append(c.name).Append("' [").Append(PathOf(c.transform)).Append("]");
        try { sb.Append(c.enabled ? "" : " DISABLED").Append(c.gameObject.activeInHierarchy ? "" : " INACTIVE"); } catch { }
        try { sb.Append(" depth ").Append(F(c.depth)).Append(" type ").Append(c.cameraType.ToString()); } catch { }
        try { sb.Append(" rect ").Append(R(c.rect)).Append(" pixelRect ").Append(R(c.pixelRect)).Append(" px ").Append(c.pixelWidth).Append('x').Append(c.pixelHeight); } catch (Exception ex) { sb.Append(" rect ?(" + ex.GetType().Name + ")"); }
        try
        {
            var tt = c.targetTexture;
            sb.Append(" target ").Append(tt == null ? "screen" : "RT '" + tt.name + "' " + tt.width + "x" + tt.height);
        }
        catch { sb.Append(" target ?"); }
        try { sb.Append(" clear ").Append(c.clearFlags.ToString()).Append(' ').Append(Hex(c.backgroundColor)); } catch { }
        try { sb.Append(" mask 0x").Append(((uint)c.cullingMask).ToString("X8")); } catch { }
        try
        {
            if (c.orthographic) sb.Append(" ortho size ").Append(F(c.orthographicSize));
            else sb.Append(" fov ").Append(F(c.fieldOfView));
            sb.Append(" near ").Append(F(c.nearClipPlane)).Append(" far ").Append(F(c.farClipPlane));
            float pa = c.pixelHeight > 0 ? (float)c.pixelWidth / c.pixelHeight : 0;
            sb.Append(" aspect ").Append(F(c.aspect)).Append(" (pixels ").Append(F(pa)).Append(')');
        }
        catch { }
        try { sb.Append(" path ").Append(c.renderingPath.ToString()).Append('/').Append(c.actualRenderingPath.ToString()); } catch { }
        try { sb.Append(" hdr ").Append(c.allowHDR ? "on" : "off").Append(" dynRes ").Append(c.allowDynamicResolution ? "on" : "off"); } catch { }
        try { sb.Append(" physical ").Append(c.usePhysicalProperties ? "YES" : "no").Append(" lensShift n/a (stripped)"); } catch { }
        sb.Append(' ').Append(Projection(c, out _, out _));
        return sb.ToString();
    }

    internal static string Behaviours(GameObject go)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var comp in go.GetComponents(Il2CppType.Of<Behaviour>()))
            {
                var b = comp.TryCast<Behaviour>();
                if (b == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(b.GetIl2CppType().Name);
                if (!b.enabled) sb.Append(" (off)");
            }
            return sb.Length == 0 ? "none" : sb.ToString();
        }
        catch (Exception ex) { return "? (" + ex.GetType().Name + ")"; }
    }

    private static float DistToBounds(Bounds b, Vector3 p)
    {
        Vector3 c = b.m_Center, e = b.m_Extents;
        float dx = Math.Max(0, Math.Abs(p.x - c.x) - e.x), dy = Math.Max(0, Math.Abs(p.y - c.y) - e.y), dz = Math.Max(0, Math.Abs(p.z - c.z) - e.z);
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool IsUnder(Transform t, Transform root)
    {
        if (root == null) return false;
        for (int i = 0; t != null && i < 64; i++, t = t.parent) if (t.Pointer == root.Pointer) return true;
        return false;
    }

    /// <summary>Logs the full camera state. main may be null; player = local character root (excluded from the near-eye scan).</summary>
    internal static void DumpAll(string tag, Camera main, Transform player)
    {
        var L = Plugin.L;
        string p = "CameraMod: [cam " + tag + "] ";
        try
        {
            string screen = "screen " + Screen.width + "x" + Screen.height;
            try { screen += " fullScreen " + (Screen.fullScreen ? "yes" : "no") + " mode " + Screen.fullScreenMode; } catch { }
            try { screen += ", ScalableBufferManager " + F(ScalableBufferManager.widthScaleFactor) + "x" + F(ScalableBufferManager.heightScaleFactor); } catch { }
            try { int w = 0, h = 0; bool rr = ReduceResolution.IsActive(ref w, ref h); screen += ", ReduceResolution " + (rr ? "ACTIVE " + w + "x" + h : "off"); } catch (Exception ex) { screen += ", ReduceResolution ?(" + ex.GetType().Name + ")"; }
            try { screen += ", Camera.main '" + (Camera.main != null ? Camera.main.name : "none") + "'"; } catch { }
            L.LogInfo(p + screen);
        }
        catch (Exception ex) { L.LogInfo(p + "screen ? " + ex.Message); }

        List<Camera> cams;
        try { cams = AllCameras(); }
        catch (Exception ex) { L.LogWarning(p + "camera list failed: " + ex.Message); return; }
        cams.Sort((a, b) => a.depth.CompareTo(b.depth));
        for (int i = 0; i < cams.Count; i++)
        {
            var c = cams[i];
            bool isMain = main != null && c.Pointer == main.Pointer;
            try { L.LogInfo(p + "camera " + (i + 1) + "/" + cams.Count + (isMain ? " (MOD CAMERA)" : "") + ": " + Describe(c)); }
            catch (Exception ex) { L.LogInfo(p + "camera " + (i + 1) + ": ? " + ex.Message); }
            try { L.LogInfo(p + "    behaviours: " + Behaviours(c.gameObject)); } catch { }
        }

        if (main != null)
        {
            try
            {
                int n = 0;
                foreach (var r in main.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    if (n++ < 20)
                        L.LogInfo(p + "renderer under the mod camera: '" + r.name + "' " + r.GetIl2CppType().Name + " enabled " + r.enabled
                                  + " active " + r.gameObject.activeInHierarchy + " path " + PathOf(r.transform));
                }
                if (n == 0) L.LogInfo(p + "no renderers under the mod camera");
                else if (n > 20) L.LogInfo(p + "... " + n + " renderers under the mod camera in total");
            }
            catch (Exception ex) { L.LogInfo(p + "camera children scan: " + ex.Message); }

            try
            {
                Vector3 eye = main.transform.position;
                int found = 0, scanned = 0;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
                {
                    var r = o.TryCast<Renderer>();
                    if (r == null || !r.enabled) continue;
                    scanned++;
                    if (IsUnder(r.transform, player)) continue;
                    var b = r.bounds;
                    float d = DistToBounds(b, eye);
                    if (d > 1.0f) continue;
                    if (found++ < 15)
                        L.LogInfo(p + "renderer around the eye (" + F(d) + " m): '" + r.name + "' " + r.GetIl2CppType().Name + " size "
                                  + F(b.m_Extents.x * 2) + "x" + F(b.m_Extents.y * 2) + "x" + F(b.m_Extents.z * 2) + " path " + PathOf(r.transform));
                }
                L.LogInfo(p + "renderers within 1 m of the eye (not yours): " + found + " of " + scanned + " enabled");
            }
            catch (Exception ex) { L.LogInfo(p + "near-eye scan: " + ex.GetType().Name + ": " + ex.Message); }
        }

        try
        {
            int n = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UIViewport>()))
            {
                var v = o.TryCast<UIViewport>();
                if (v == null) continue;
                n++;
                var vc = v.GetComponent<Camera>();
                L.LogInfo(p + "UIViewport on '" + PathOf(v.transform) + "' camera '" + (vc != null ? vc.name : "?") + "' enabled " + v.enabled
                          + " source '" + (v.sourceCamera != null ? v.sourceCamera.name : "none") + "'");
            }
            if (n == 0) L.LogInfo(p + "no UIViewport (NGUI viewport camera) in the scene");
        }
        catch (Exception ex) { L.LogInfo(p + "UIViewport scan: " + ex.GetType().Name); }
    }

    /// <summary>Short one-liner for the mod camera (bisect steps, watchdog).</summary>
    internal static string Short(Camera c)
    {
        try
        {
            return "rect " + R(c.rect) + " pixelRect " + R(c.pixelRect) + " fov " + F(c.fieldOfView) + " near " + F(c.nearClipPlane) + " far " + F(c.farClipPlane)
                   + " clear " + c.clearFlags + " target " + (c.targetTexture == null ? "screen" : c.targetTexture.name) + " " + Projection(c, out _, out _);
        }
        catch (Exception ex) { return "? " + ex.GetType().Name; }
    }
}
