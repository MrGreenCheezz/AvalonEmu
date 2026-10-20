using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime;

namespace ActionBarsMod;

/// <summary>
/// The server's item -> extra spells table (event 9004, SPEC §3.3): one entry per (rule, spell) in the server's precedence order.
/// Matching = the server's ExtraSpellTable.For: a pattern with * (any text) / ? (one character) or an exact name, case-insensitive,
/// against the item's uniquename with and without "@n"; each spell listed once.
/// </summary>
internal static class Catalog
{
    internal sealed class Entry
    {
        public string Pattern = "", Spell = "", Label = "";
        public int SpellId;
        public Regex Glob; // null = exact name
    }

    private static readonly List<Entry> _entries = new();
    private static readonly Dictionary<string, List<Entry>> _cache = new(StringComparer.OrdinalIgnoreCase);
    internal static int Revision = -1, Count;
    internal static bool Received;

    internal static void Apply(string[] patterns, string[] spells, long[] ids, int revision)
    {
        _entries.Clear();
        _cache.Clear();
        int n = patterns?.Length ?? 0;
        for (int i = 0; i < n; i++)
        {
            string p = (patterns[i] ?? "").Trim();
            string s = spells != null && i < spells.Length ? spells[i] ?? "" : "";
            if (p.Length == 0 || s.Length == 0) continue;
            Regex g = null;
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0)
                g = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _entries.Add(new Entry { Pattern = p, Spell = s, SpellId = ids != null && i < ids.Length ? (int)ids[i] : -1, Glob = g });
        }
        Revision = revision;
        Count = _entries.Count;
        Tooltips.ClearCache();
        ItemExtras.Clear();
        Received = true;
        if (Plugin.LogEvents.Value)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _entries.Count && i < 12; i++) sb.Append(i == 0 ? "" : ", ").Append(_entries[i].Pattern).Append("->").Append(_entries[i].Spell);
            Plugin.L.LogInfo($"ActionBarsMod: extra spells catalog rev {revision}: {_entries.Count} entries [{sb}{(_entries.Count > 12 ? ", ..." : "")}]");
        }
    }

    /// <summary>The extra spells an item grants (full uniquename, e.g. "T4_CAPE@1"; empty list when none).</summary>
    internal static List<Entry> For(string item)
    {
        if (string.IsNullOrEmpty(item) || _entries.Count == 0) return Empty;
        if (_cache.TryGetValue(item, out var hit)) return hit;
        int at = item.IndexOf('@');
        string baseName = at < 0 ? item : item.Substring(0, at);
        var r = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries)
        {
            bool m = e.Glob != null
                ? e.Glob.IsMatch(item) || e.Glob.IsMatch(baseName)
                : string.Equals(e.Pattern, item, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Pattern, baseName, StringComparison.OrdinalIgnoreCase);
            if (m && seen.Add(e.Spell)) r.Add(e);
        }
        _cache[item] = r;
        return r;
    }

    private static readonly List<Entry> Empty = new();

    /// <summary>
    /// The uniquename of a client item info (bdi): the item group's name (bdi +0x10 ParentItemInfoGroup -> +0x18, what bdi.hu 0x1805f3230
    /// derives the default icon from) and, when it starts with that, bdi +0x20 (the full name, may carry "@n"). Raw reads, no IL2CPP calls.
    /// </summary>
    internal static string ItemName(IntPtr bdi)
    {
        if (bdi == IntPtr.Zero) return "";
        try
        {
            string baseName = "";
            IntPtr grp = Marshal.ReadIntPtr(bdi + 0x10);
            if (grp != IntPtr.Zero)
            {
                IntPtr s = Marshal.ReadIntPtr(grp + 0x18);
                if (s != IntPtr.Zero) baseName = IL2CPP.Il2CppStringToManaged(s) ?? "";
            }
            string full = "";
            IntPtr f = Marshal.ReadIntPtr(bdi + 0x20);
            if (f != IntPtr.Zero && Il2CppValues.ClassName(f) == "String") full = IL2CPP.Il2CppStringToManaged(f) ?? "";
            if (full.Length > 0 && (baseName.Length == 0 || full.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))) return full;
            return baseName;
        }
        catch { return ""; }
    }

    // ---- language

    private static int _ru = -1;

    /// <summary>Whether the game runs in Russian: a localised item name contains Cyrillic (a8c.f).</summary>
    internal static bool Russian
    {
        get
        {
            if (_ru >= 0) return _ru == 1;
            string probe = Spells.Localize("@ITEMS_T4_BAG", "", force: true) + Spells.Localize("@SPELLS_SPRINT", "", force: true);
            if (probe.Length == 0) return false; // localisation not loaded yet: ask again later
            _ru = 0;
            foreach (char c in probe) if (c >= 'Ѐ' && c <= 'ӿ') { _ru = 1; break; }
            Plugin.L.LogInfo("ActionBarsMod: game language " + (_ru == 1 ? "Russian" : "not Russian") + " (tooltip texts)");
            return _ru == 1;
        }
    }

    internal static string T(string ru, string en) => Russian ? ru : en;
}

/// <summary>NGUI BBCode ([rrggbb]..[-], [b], [i], [u], [s], [sub], [sup], [url=..]) -> IMGUI rich text / plain text.</summary>
internal static class BBCode
{
    private static readonly Regex Tag = new(@"\[(?:([0-9a-fA-F]{6}|[0-9a-fA-F]{8})|-|/?[bius]|/?sub|/?sup|/?url(?:=[^\]]*)?|c|/c)\]", RegexOptions.CultureInvariant);

    internal static string Plain(string s) => string.IsNullOrEmpty(s) ? "" : Tag.Replace(s, "");

    /// <summary>Balanced IMGUI rich text (colours and bold/italic only; the rest is dropped).</summary>
    internal static string Rich(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        var open = new Stack<string>();
        int last = 0;
        foreach (Match m in Tag.Matches(s))
        {
            sb.Append(Escape(s.Substring(last, m.Index - last)));
            last = m.Index + m.Length;
            string t = m.Value;
            if (m.Groups[1].Success) { sb.Append("<color=#").Append(m.Groups[1].Value).Append('>'); open.Push("</color>"); }
            else if (t == "[-]") { if (open.Count > 0 && open.Peek() == "</color>") sb.Append(open.Pop()); }
            else if (t == "[b]") { sb.Append("<b>"); open.Push("</b>"); }
            else if (t == "[/b]") { if (open.Count > 0 && open.Peek() == "</b>") sb.Append(open.Pop()); }
            else if (t == "[i]") { sb.Append("<i>"); open.Push("</i>"); }
            else if (t == "[/i]") { if (open.Count > 0 && open.Peek() == "</i>") sb.Append(open.Pop()); }
        }
        sb.Append(Escape(s.Substring(last)));
        while (open.Count > 0) sb.Append(open.Pop());
        return sb.ToString();
    }

    // IMGUI rich text has no escape; a stray '<' that looks like a tag would be eaten. Replace it by a look-alike.
    private static string Escape(string s) => s.IndexOf('<') < 0 ? s : s.Replace("<", "‹");

    /// <summary>First paragraph, at most <paramref name="max"/> visible characters (0 = all), BBCode kept balanced by the caller's Rich/Plain.</summary>
    internal static string Short(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", "");
        int nl = s.IndexOf("\n\n", StringComparison.Ordinal);
        if (nl > 0) s = s.Substring(0, nl);
        if (max <= 0) return s.Trim();
        string plain = Plain(s);
        if (plain.Length <= max) return s.Trim();
        // cut the plain text (tags dropped) at a word boundary
        int cut = plain.LastIndexOf(' ', Math.Min(plain.Length - 1, max));
        if (cut < max / 2) cut = max;
        return plain.Substring(0, cut).TrimEnd(' ', ',', ';', ':') + "…";
    }
}
