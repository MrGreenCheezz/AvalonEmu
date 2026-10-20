using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace ActionBarsMod;

/// <summary>One extra slot as the server sent it (event 9001 / 9002).</summary>
internal sealed class SlotInfo
{
    public int SpellId = -1;
    public string Unique = "", Item = "", Label = "";
    public byte Source;
    public long CdStart, CdEnd;
    public float FlashUntil;
    public string FlashText = "";
    public bool Empty => SpellId < 0 || Unique.Length == 0 && SpellId <= 0;
}

/// <summary>
/// Protocol side (SPEC §3): send ops through the game peer, intercept events before the game's listeners.
///  - Events: c3s.akw(g7s) (VA 0x180A8D960) = IPhotonPeerListener.OnEvent of the Albion peer. It reads the Albion event code from
///    param 252 (Int16) and calls c3r.xk(peer, data, code) on every listener (ci7.xk 0x1809835b0 logs "Event not handled" for unknown
///    codes via its default 0x1809864cc). Our Harmony prefix returns false for 9001-9005, so no listener sees them.
///  - Albion event 2 = JoinFinished (ci7.xk case 0x1809836f2): remembers that peer and arms the hello for the next frame.
///  - Send: g7o.h(byte opCode, Dictionary&lt;byte,object&gt;, g73 options) = PhotonPeer.SendOperation (vtable +0x228), called exactly as
///    c3s.i(x6, bool encrypt) 0x180A8E7A0 does: Photon op code 1 (ez.a), param 253 = (short) Albion op code, options c3s.b when
///    g7o.ef() (encryption ready) else c3s.c. No param 255 (fire and forget, SPEC §3.2).
/// Parsed results are queued and applied on the main thread (BarsOverlay.Update), the Photon dispatch thread is not assumed.
/// </summary>
internal static class Net
{
    internal static c3s Peer;
    internal static volatile bool HelloPending;
    internal static bool HelloSent;
    internal static readonly ConcurrentQueue<Action> Inbox = new();
    private static int _failLogs;

    // ---- shared state (main thread only) ----
    internal static int BarCount, BarSize, Revision = -1, ServerProtocol;
    /// <summary>Server slot index of UI bar (0-based) / position (0-based), -1 outside the layout.</summary>
    internal static int Index(int bar, int pos) => bar < BarCount && pos < BarSize ? bar * BarSize + pos : -1;
    internal static string SlotName(int index) => BarSize > 0 && index >= 0 ? $"{index / BarSize + 1}.{index % BarSize + 1}" : "?";
    internal static readonly SlotInfo[] Slots = new SlotInfo[12];
    static Net() { for (int i = 0; i < 12; i++) Slots[i] = new SlotInfo(); }

    internal static void Install(Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(c3s), "akw", new[] { typeof(g7s) });
            if (m == null) { Plugin.L.LogError("ActionBarsMod: hook NOT applied: c3s.akw(g7s) not found - no bars"); return; }
            h.Patch(m, prefix: new HarmonyMethod(typeof(Net), nameof(AkwPrefix)));
            Plugin.L.LogInfo("ActionBarsMod: hooked c3s.akw (VA 0x180A8D960, Photon OnEvent) prefix: JoinFinished -> hello, events 9001-9005 consumed");
        }
        catch (Exception ex) { Plugin.L.LogError("ActionBarsMod: hook NOT applied: c3s.akw: " + ex); }
    }

    private static bool AkwPrefix(c3s __instance, g7s __0)
    {
        try
        {
            if (__0 == null) return true;
            byte photonCode = __0.a;
            if (photonCode == 2 || photonCode == 3) return true; // keepalive / Move fast path
            var codeObj = __0.h((byte)252);
            if (codeObj == null) return true;
            long code = Il2CppValues.ReadLong(codeObj.Pointer);
            if (code == 2)
            {
                Peer = __instance;
                HelloPending = true;
                return true;
            }
            if (code < 9001 || code > 9005) return true;
            ParseEvent((int)code, __0);
            return false;
        }
        catch (Exception ex)
        {
            if (_failLogs++ < 5) Plugin.L.LogError("ActionBarsMod: event hook error: " + ex);
            return true;
        }
    }

    private static IntPtr P(g7s ev, byte key)
    {
        var o = ev.h(key);
        return o == null ? IntPtr.Zero : o.Pointer;
    }

    private static void ParseEvent(int code, g7s ev)
    {
        switch (code)
        {
            case 9001:
            {
                int bars = (int)Il2CppValues.ReadLong(P(ev, 0));
                int size = (int)Il2CppValues.ReadLong(P(ev, 1));
                long[] ids = Il2CppValues.ReadLongArray(P(ev, 2));
                string[] names = Il2CppValues.ReadStringArray(P(ev, 3));
                string[] items = Il2CppValues.ReadStringArray(P(ev, 4));
                long[] src = Il2CppValues.ReadLongArray(P(ev, 5));
                long[] cs = Il2CppValues.ReadLongArray(P(ev, 6));
                long[] ce = Il2CppValues.ReadLongArray(P(ev, 7));
                int rev = (int)Il2CppValues.ReadLong(P(ev, 8));
                int proto = (int)Il2CppValues.ReadLong(P(ev, 10));
                string[] labels = Il2CppValues.ReadStringArray(P(ev, 11));
                Inbox.Enqueue(() => ApplyBars(bars, size, ids, names, items, src, cs, ce, rev, proto, labels));
                break;
            }
            case 9002:
            {
                long[] idx = Il2CppValues.ReadLongArray(P(ev, 0));
                long[] cs = Il2CppValues.ReadLongArray(P(ev, 1));
                long[] ce = Il2CppValues.ReadLongArray(P(ev, 2));
                long[] ids = Il2CppValues.ReadLongArray(P(ev, 3));
                Inbox.Enqueue(() => ApplyCooldowns(idx, cs, ce, ids));
                break;
            }
            case 9004:
            {
                string[] pats = Il2CppValues.ReadStringArray(P(ev, 0));
                string[] spells = Il2CppValues.ReadStringArray(P(ev, 1));
                long[] ids = Il2CppValues.ReadLongArray(P(ev, 2));
                int rev = (int)Il2CppValues.ReadLong(P(ev, 3));
                Inbox.Enqueue(() => Catalog.Apply(pats, spells, ids, rev));
                break;
            }
            case 9005:
            {
                int req = (int)Il2CppValues.ReadLong(P(ev, 0));
                string item = Il2CppValues.ReadString(P(ev, 1));
                string[] spells = Il2CppValues.ReadStringArray(P(ev, 2));
                long[] ids = Il2CppValues.ReadLongArray(P(ev, 3));
                string[] labels = Il2CppValues.ReadStringArray(P(ev, 4));
                int quality = (int)Il2CppValues.ReadLong(P(ev, 5));
                Inbox.Enqueue(() => ItemExtras.Apply(req, item, spells, ids, labels, quality));
                break;
            }
            case 9003:
            {
                int index = (int)Il2CppValues.ReadLong(P(ev, 0));
                int spell = (int)Il2CppValues.ReadLong(P(ev, 1));
                string reason = Il2CppValues.ReadString(P(ev, 2));
                int counter = (int)Il2CppValues.ReadLong(P(ev, 3));
                string text = Il2CppValues.ReadString(P(ev, 4));
                Inbox.Enqueue(() => ApplyRefused(index, spell, reason, counter, text));
                break;
            }
        }
    }

    private static long At(long[] a, int i) => a != null && i < a.Length ? a[i] : 0;
    private static string At(string[] a, int i) => a != null && i < a.Length ? a[i] ?? "" : "";

    private static void ApplyBars(int bars, int size, long[] ids, string[] names, string[] items, long[] src, long[] cs, long[] ce, int rev, int proto, string[] labels)
    {
        if (rev < Revision) { if (Plugin.LogEvents.Value) Plugin.L.LogInfo($"ActionBarsMod: stale bars rev {rev} < {Revision} ignored"); return; }
        Revision = rev; ServerProtocol = proto;
        ItemExtras.Clear(); // a choice or the gear changed: what items grant may differ now
        BarCount = Math.Clamp(bars, 0, 2);
        BarSize = Math.Clamp(size, 0, 6);
        var parts = new List<string>();
        int n = BarCount * BarSize; // server index = (bar-1)*BarSize + position-1 (ExtraBars.cs), arrays have n entries
        for (int i = 0; i < 12; i++)
        {
            var s = Slots[i];
            bool inLayout = i < n;
            int newId = inLayout ? (int)(ids != null && i < ids.Length ? ids[i] : -1) : -1;
            if (newId != s.SpellId) s.FlashUntil = 0;
            s.SpellId = newId;
            s.Unique = inLayout ? At(names, i) : "";
            s.Item = inLayout ? At(items, i) : "";
            s.Label = inLayout ? At(labels, i) : "";
            s.Source = (byte)(inLayout ? At(src, i) : 0);
            s.CdStart = inLayout ? At(cs, i) : 0;
            s.CdEnd = inLayout ? At(ce, i) : 0;
            if (inLayout) parts.Add(s.SpellId < 0 || s.Unique.Length == 0 ? "-" : s.Unique + (s.Item.Length > 0 ? "<" + s.Item : s.Source == 2 ? "<override" : ""));
        }
        if (Plugin.LogEvents.Value)
            Plugin.L.LogInfo($"ActionBarsMod: bars received rev {rev}: {BarCount}x{BarSize} [{string.Join(", ", parts)}] (server protocol {proto})");
    }

    private static void ApplyCooldowns(long[] idx, long[] cs, long[] ce, long[] ids)
    {
        if (idx == null) return;
        var parts = new List<string>();
        for (int k = 0; k < idx.Length; k++)
        {
            int i = (int)idx[k];
            if (i < 0 || i >= 12) continue;
            var s = Slots[i];
            int id = (int)At(ids, k);
            if (ids != null && k < ids.Length && id != s.SpellId && id > 0) continue; // stale: slot changed meanwhile
            s.CdStart = At(cs, k); s.CdEnd = At(ce, k);
            long now = Clock.Now();
            parts.Add($"{SlotName(i)}:{(s.CdEnd <= now ? "ready" : ((s.CdEnd - now) / 10000000.0).ToString("0.0") + "s")}");
        }
        if (Plugin.LogEvents.Value) Plugin.L.LogInfo("ActionBarsMod: cooldowns " + string.Join(" ", parts));
    }

    private static void ApplyRefused(int index, int spell, string reason, int counter, string text)
    {
        string name = index >= 0 && index < 12 ? Slots[index].Unique : "?";
        Plugin.L.LogInfo($"ActionBarsMod: refused slot {(index == 255 ? "?" : SlotName(index))} spell {spell} ({name}) reason {reason} counter {counter}{(text.Length > 0 ? " text '" + text + "'" : "")}");
        if (index >= 0 && index < 12)
        {
            Slots[index].FlashUntil = UnityEngine.Time.unscaledTime + 0.6f;
            Slots[index].FlashText = text.Length > 0 ? text : reason;
        }
        BarsOverlay.LastRefusal = reason + (text.Length > 0 ? ": " + text : "");
        BarsOverlay.LastRefusalUntil = UnityEngine.Time.unscaledTime + 3f;
    }

    /// <summary>Main thread, every frame.</summary>
    internal static void Pump()
    {
        while (Inbox.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { Plugin.L.LogError("ActionBarsMod: applying an event failed: " + ex); }
        }
        if (HelloPending && Peer != null)
        {
            HelloPending = false;
            Revision = -1; // new connection: the server's revision may start over
            try
            {
                var ok = Send(9001, new (byte, Il2CppSystem.Object)[] { (0, Il2CppValues.BoxInt(2)), (1, Il2CppValues.BoxString("ActionBarsMod " + Plugin.Version)) });
                if (ok) { HelloSent = true; Plugin.L.LogInfo("ActionBarsMod: hello sent (op 9001, protocol 2)"); }
            }
            catch (Exception ex) { Plugin.L.LogError("ActionBarsMod: hello not sent: " + ex.Message); }
        }
    }

    /// <summary>Albion operation without request id: Photon op 1, param 253 = code.</summary>
    internal static bool Send(short opCode, (byte key, Il2CppSystem.Object val)[] pars)
    {
        var peer = Peer;
        if (peer == null) { Plugin.L.LogWarning($"ActionBarsMod: op {opCode} not sent: no game connection seen yet (JoinFinished)"); return false; }
        try
        {
            var d = new Il2CppSystem.Collections.Generic.Dictionary<byte, Il2CppSystem.Object>();
            foreach (var (k, v) in pars) if (v != null) d[k] = v;
            d[253] = Il2CppValues.BoxShort(opCode);
            bool enc = false;
            try { enc = peer.ef(); } catch { }
            var opts = enc ? c3s.b : c3s.c;
            bool r = peer.h((byte)1, d, opts);
            if (!r) Plugin.L.LogWarning($"ActionBarsMod: op {opCode}: SendOperation returned false (not connected?)");
            return r;
        }
        catch (Exception ex) { Plugin.L.LogError($"ActionBarsMod: op {opCode} send failed: " + ex); return false; }
    }
}

/// <summary>Reading boxed IL2CPP values / arrays by class name and raw layout (no IL2CPP-side formatting, see CameraMod NOTES).</summary>
internal static class Il2CppValues
{
    // Il2CppArray (64-bit): klass, monitor, bounds, max_length, then the elements.
    private const int ArrayData = 0x20;

    internal static string ClassName(IntPtr obj) => obj == IntPtr.Zero ? "" : IL2CPP.il2cpp_class_get_name_(IL2CPP.il2cpp_object_get_class(obj)) ?? "";

    internal static long ReadLong(IntPtr obj)
    {
        if (obj == IntPtr.Zero) return 0; // SPEC §3: a missing scalar is 0
        IntPtr v = IL2CPP.il2cpp_object_unbox(obj);
        switch (ClassName(obj))
        {
            case "Byte": return Marshal.ReadByte(v);
            case "SByte": return (sbyte)Marshal.ReadByte(v);
            case "Boolean": return Marshal.ReadByte(v) != 0 ? 1 : 0;
            case "Int16": return Marshal.ReadInt16(v);
            case "UInt16": return (ushort)Marshal.ReadInt16(v);
            case "Int32": return Marshal.ReadInt32(v);
            case "UInt32": return (uint)Marshal.ReadInt32(v);
            case "Int64": case "UInt64": return Marshal.ReadInt64(v);
            case "Single": return (long)BitConverter.Int32BitsToSingle(Marshal.ReadInt32(v));
            case "Double": return (long)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(v));
            default: return 0;
        }
    }

    internal static string ReadString(IntPtr obj) => obj == IntPtr.Zero ? "" : ClassName(obj) == "String" ? IL2CPP.Il2CppStringToManaged(obj) ?? "" : "";

    internal static long[] ReadLongArray(IntPtr arr)
    {
        if (arr == IntPtr.Zero) return null;
        IntPtr ec = IL2CPP.il2cpp_class_get_element_class(IL2CPP.il2cpp_object_get_class(arr));
        string en = IL2CPP.il2cpp_class_get_name_(ec) ?? "";
        int n = (int)IL2CPP.il2cpp_array_length(arr);
        var r = new long[n];
        IntPtr d = arr + ArrayData;
        for (int i = 0; i < n; i++)
        {
            switch (en)
            {
                case "Byte": case "Boolean": r[i] = Marshal.ReadByte(d, i); break;
                case "SByte": r[i] = (sbyte)Marshal.ReadByte(d, i); break;
                case "Int16": r[i] = Marshal.ReadInt16(d, i * 2); break;
                case "UInt16": r[i] = (ushort)Marshal.ReadInt16(d, i * 2); break;
                case "Int32": r[i] = Marshal.ReadInt32(d, i * 4); break;
                case "UInt32": r[i] = (uint)Marshal.ReadInt32(d, i * 4); break;
                case "Int64": case "UInt64": r[i] = Marshal.ReadInt64(d, i * 8); break;
                case "Single": r[i] = (long)BitConverter.Int32BitsToSingle(Marshal.ReadInt32(d, i * 4)); break;
                case "Object": r[i] = ReadLong(Marshal.ReadIntPtr(d, i * IntPtr.Size)); break;
                default:
                    Plugin.L.LogWarning("ActionBarsMod: unexpected array element type " + en);
                    return r;
            }
        }
        return r;
    }

    internal static string[] ReadStringArray(IntPtr arr)
    {
        if (arr == IntPtr.Zero) return null;
        int n = (int)IL2CPP.il2cpp_array_length(arr);
        var r = new string[n];
        IntPtr d = arr + ArrayData;
        for (int i = 0; i < n; i++)
        {
            IntPtr s = Marshal.ReadIntPtr(d, i * IntPtr.Size);
            r[i] = s == IntPtr.Zero ? "" : IL2CPP.Il2CppStringToManaged(s) ?? "";
        }
        return r;
    }

    // The interop's Il2CppClassPointerStore<Il2CppSystem.Int32>.NativeClassPtr can be zero in this build (its class init
    // is not run), and il2cpp_value_box with a null class crashes the client (uncatchable AccessViolation). So the class is
    // looked up by name in mscorlib, initialised, cached, and a missing class throws a managed exception instead.
    private static readonly System.Collections.Generic.Dictionary<string, IntPtr> ClassCache = new();

    private static IntPtr PrimitiveClass(string name)
    {
        if (ClassCache.TryGetValue(name, out var k)) return k;
        k = IL2CPP.GetIl2CppClass("mscorlib.dll", "System", name);
        if (k != IntPtr.Zero) IL2CPP.il2cpp_runtime_class_init(k);
        ClassCache[name] = k;
        Plugin.L.LogInfo("ActionBarsMod: class System." + name + " -> " + (k == IntPtr.Zero ? "NOT FOUND" : "0x" + k.ToInt64().ToString("X")));
        return k;
    }

    private static unsafe Il2CppSystem.Object Box<T>(string name, T v) where T : unmanaged
    {
        IntPtr k = PrimitiveClass(name);
        if (k == IntPtr.Zero) throw new InvalidOperationException("Il2Cpp class System." + name + " not found, value not boxed");
        IntPtr o = IL2CPP.il2cpp_value_box(k, (IntPtr)(&v));
        if (o == IntPtr.Zero) throw new InvalidOperationException("il2cpp_value_box(System." + name + ") returned null");
        return new Il2CppSystem.Object(o);
    }
    internal static Il2CppSystem.Object BoxShort(short v) => Box("Int16", v);
    internal static Il2CppSystem.Object BoxInt(int v) => Box("Int32", v);
    internal static Il2CppSystem.Object BoxLong(long v) => Box("Int64", v);
    internal static Il2CppSystem.Object BoxByte(byte v) => Box("Byte", v);
    internal static Il2CppSystem.Object BoxString(string s) => new Il2CppSystem.Object(IL2CPP.ManagedStringToIl2Cpp(s));
    internal static Il2CppSystem.Object FloatArray(float a, float b)
    {
        var arr = new Il2CppStructArray<float>(2);
        arr[0] = a; arr[1] = b;
        return new Il2CppSystem.Object(arr.Pointer);
    }
}

/// <summary>Game clock in GameTimeStamp ticks (server DateTime ticks), the unit of every cooldown in events 9001/9002.</summary>
internal static class Clock
{
    private static bool _broken;
    internal static long Now()
    {
        if (!_broken)
        {
            // GameTimeStamp.Now (getter = native h(), 0x1808b3070)
            try { long t = Albion.Common.Time.GameTimeStamp.Now.Ticks; if (t > 0) return t; }
            catch (Exception ex) { _broken = true; Plugin.L.LogWarning("ActionBarsMod: GameTimeStamp.Now unavailable, using the local UTC clock: " + ex.Message); }
        }
        return DateTime.UtcNow.Ticks;
    }
}
