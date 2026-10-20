using System;
using System.Collections.Generic;
using UnityEngine;

namespace ActionBarsMod;

/// <summary>
/// The server's rule-resolved extra spells of any item (protocol 2, SPEC §3.2/§3.3): op 9003 {0 item uniquename, 1 quality, 2 enchantment,
/// 3 request id} → event 9005 {0 request id, 1 item, 2 spell names[], 3 spell ids[], 4 rule labels[], 5 quality, 6 enchantment, 7 known}.
/// Cached by (item with "@n", quality); the cache is cleared on every bars event 9001 (a choice or the gear changed) and on a new catalog.
/// Main thread only (requests from the tooltip hooks, answers through Net.Inbox).
/// </summary>
internal static class ItemExtras
{
    internal sealed class Result
    {
        public string Item = "";
        public int Quality;
        public readonly List<Catalog.Entry> Spells = new();
        public float Made;
    }

    private static readonly Dictionary<string, Result> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, float> _pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, string> _requests = new();
    private static int _next = 1, _logs;
    private const float Ttl = 120f, Retry = 3f;

    /// <summary>Whether the server answers op 9003 (it said protocol 2+ in event 9001 after our hello).</summary>
    internal static bool Available => Net.HelloSent && Net.ServerProtocol >= 2;

    internal static string Key(string item, int quality) => (item ?? "").ToUpperInvariant() + "|" + Math.Clamp(quality, 1, 5);

    /// <summary>The cached answer, or null (then a request is sent unless one is under way).</summary>
    internal static Result Get(string item, int quality)
    {
        if (!Available || string.IsNullOrEmpty(item)) return null;
        quality = Math.Clamp(quality <= 0 ? 1 : quality, 1, 5);
        string key = Key(item, quality);
        float now = Time.unscaledTime;
        if (_cache.TryGetValue(key, out var hit) && now - hit.Made < Ttl) return hit;
        if (_pending.TryGetValue(key, out var asked) && now - asked < Retry) return null;
        int id = _next++;
        _pending[key] = now;
        _requests[id] = key;
        bool ok = false;
        try
        {
            ok = Net.Send(9003, new (byte, Il2CppSystem.Object)[]
            {
                (0, Il2CppValues.BoxString(item)), (1, Il2CppValues.BoxInt(quality)), (2, Il2CppValues.BoxInt(0)), (3, Il2CppValues.BoxInt(id)),
            });
        }
        catch (Exception ex) { if (_logs++ < 5) Plugin.L.LogWarning("ActionBarsMod: op 9003 not sent: " + ex.Message); }
        if (ok && Plugin.LogEvents.Value) Plugin.L.LogInfo($"ActionBarsMod: asked the server for the extra spells of {item} q{quality} (request {id})");
        return null;
    }

    /// <summary>Event 9005 (main thread).</summary>
    internal static void Apply(int request, string item, string[] spells, long[] ids, string[] labels, int quality)
    {
        if (!_requests.TryGetValue(request, out var key)) key = Key(item, quality);
        _requests.Remove(request);
        _pending.Remove(key);
        var r = new Result { Item = item, Quality = quality, Made = Time.unscaledTime };
        int n = spells?.Length ?? 0;
        for (int i = 0; i < n; i++)
        {
            if (string.IsNullOrEmpty(spells[i])) continue;
            r.Spells.Add(new Catalog.Entry
            {
                Pattern = item, Spell = spells[i], SpellId = ids != null && i < ids.Length ? (int)ids[i] : -1,
                Label = labels != null && i < labels.Length ? labels[i] ?? "" : "",
            });
        }
        _cache[key] = r;
        if (Plugin.LogEvents.Value) Plugin.L.LogInfo($"ActionBarsMod: extra spells of {item} q{quality}: {n} (request {request})");
        Tooltips.ExtrasArrived(key);
    }

    internal static void Clear()
    {
        _cache.Clear();
        _pending.Clear();
    }

    /// <summary>The rule label of event 9001 key 11 / 9005 key 4 as a short text ("" for none or an Items entry).</summary>
    internal static string LabelText(string label)
    {
        if (string.IsNullOrEmpty(label) || label.StartsWith("item:", StringComparison.Ordinal)) return "";
        if (label == "unchosen") return Catalog.T("невыбранный вариант", "unchosen option");
        if (label == "artifact") return Catalog.T("артефакт", "artifact");
        if (label == "override") return Catalog.T("плагин / ГМ", "plugin / GM");
        if (label.StartsWith("map:", StringComparison.Ordinal)) return Catalog.T("по типу предмета", "by item type");
        if (label.StartsWith("enchant:", StringComparison.Ordinal)) return Catalog.T("зачарование .", "enchantment .") + label.Substring(8);
        if (label.StartsWith("quality:", StringComparison.Ordinal))
        {
            string q = label.Substring(8);
            return q == "5" ? Catalog.T("шедевр", "masterpiece") : q == "4" ? Catalog.T("отличное", "excellent") : Catalog.T("качество ", "quality ") + q;
        }
        return label;
    }
}
