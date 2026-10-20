using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace ActionBarsMod;

internal sealed class SpellInfo
{
    public int Id;
    public bio Spell;               // null: the client does not know the id (or it is not an active spell)
    public string Unique = "", Sprite = "", Atlas = "", NameTag = "", DescTag = "", Display = "";
}

/// <summary>What the game's spell tooltip shows (gdn: name, stat lines, description), NGUI BBCode inside.</summary>
internal sealed class SpellText
{
    public string Name = "", Description = "";
    public string[] Stats = Array.Empty<string>();
    public bool FromGame;      // built by the game's tooltip builder gdn.h (numbers from the local player's stats)
    public gdn Data;           // the game's tooltip data (ProximityTooltipManager.Show(Spell, Data))
    public float Made;
}

/// <summary>
/// The client's own spell table: ci7.bk() (PhotonClient singleton) .b4() -> bcm (game data) .e (be7, field 0x30) .abp(int id) -> bil
/// (be7.abp 0x18064DB20; the action bar and the chest click cz5.u 0x180A268F0 do the same). Active spells are bio.
/// Strings straight from bi1 (set in bi1.nv 0x1806B5390 from spells.xml): +0x18 uniquename, +0x20 namelocatag (default
/// "@SPELLS_" + uniquename), +0x78 uiatlas (default "RefSpellAtlas"), +0x80 uisprite (default = uniquename).
/// Display name: a8c.f(string key) (static localisation lookup, what AlbionUILocalize.OnAlbionLocalize 0x180ED6940 calls).
/// </summary>
internal static class Spells
{
    private static readonly Dictionary<int, SpellInfo> _cache = new();
    private static be7 _table;
    private static bool _locBroken;
    private static float _nextTry;
    private static int _fails;

    private static be7 Table()
    {
        if (_table != null) return _table;
        float now = UnityEngine.Time.unscaledTime;
        if (now < _nextTry) return null;
        _nextTry = now + 2f;
        try
        {
            var pc = ci7.bk();
            var gd = pc?.b4();
            _table = gd?.e;
            if (_table == null)
            {
                var g = dw1.ab();
                _table = g?.e?.e;
            }
        }
        catch (Exception ex) { if (_fails++ < 3) Plugin.L.LogWarning("ActionBarsMod: spell table not reachable: " + ex.Message); }
        return _table;
    }

    private static string Str(IntPtr obj, int off)
    {
        IntPtr s = Marshal.ReadIntPtr(obj + off);
        return s == IntPtr.Zero ? "" : IL2CPP.Il2CppStringToManaged(s) ?? "";
    }

    /// <summary>Spell info for an id; the server's uniquename is the fallback for names and the icon.</summary>
    internal static SpellInfo Get(int id, string serverUnique)
    {
        if (id <= 0 && string.IsNullOrEmpty(serverUnique)) return null;
        if (id <= 0) return GetByName(serverUnique);
        if (_cache.TryGetValue(id, out var c)) return c;
        var info = new SpellInfo { Id = id, Unique = serverUnique ?? "", Sprite = serverUnique ?? "", Display = serverUnique ?? "" };
        var t = Table();
        if (t != null)
        {
            try
            {
                bil b = id > 0 ? t.abp(id) : null;
                if (b == null && !string.IsNullOrEmpty(serverUnique)) b = t.abp(serverUnique);
                if (b != null)
                {
                    IntPtr p = b.Pointer;
                    string u = Str(p, 0x18);
                    if (u.Length > 0) info.Unique = u;
                    info.NameTag = Str(p, 0x20);
                    info.DescTag = Str(p, 0x28); // descriptionlocatag (bi1.nv 0x1806B5390), default "@SPELLS_" + name + "_DESC"
                    info.Atlas = Str(p, 0x78);
                    string spr = Str(p, 0x80);
                    info.Sprite = spr.Length > 0 ? spr : info.Unique;
                    info.Spell = b.TryCast<bio>();
                    if (info.Spell == null) Plugin.L.LogWarning($"ActionBarsMod: spell {id} {info.Unique} is not an active spell (bio) on the client");
                    if (!string.IsNullOrEmpty(serverUnique) && serverUnique != info.Unique)
                        Plugin.L.LogWarning($"ActionBarsMod: spell id {id}: client table says {info.Unique}, server says {serverUnique} (spell numbering differs?)");
                }
                else Plugin.L.LogWarning($"ActionBarsMod: spell {id} {serverUnique} unknown to the client spell table");
            }
            catch (Exception ex) { Plugin.L.LogWarning($"ActionBarsMod: spell {id} lookup failed: " + ex.Message); }
        }
        info.Display = Localize(info.NameTag, info.Unique);
        if (t != null) _cache[id] = info; // no table yet (loading): try again next time
        return info;
    }

    private static readonly Dictionary<string, SpellInfo> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A spell the server gave no id for (catalog entry of a spell the server does not know): the client's table by uniquename.</summary>
    private static SpellInfo GetByName(string unique)
    {
        if (_byName.TryGetValue(unique, out var c)) return c;
        var info = new SpellInfo { Id = -1, Unique = unique, Sprite = unique, Display = unique };
        var t = Table();
        if (t != null)
        {
            try
            {
                bil b = t.abp(unique);
                if (b != null)
                {
                    IntPtr p = b.Pointer;
                    info.NameTag = Str(p, 0x20); info.DescTag = Str(p, 0x28); info.Atlas = Str(p, 0x78);
                    string spr = Str(p, 0x80);
                    info.Sprite = spr.Length > 0 ? spr : unique;
                    info.Spell = b.TryCast<bio>();
                }
            }
            catch { }
            info.Display = Localize(info.NameTag, unique);
            _byName[unique] = info;
        }
        return info;
    }

    internal static string Localize(string key, string fallback, bool force = false)
    {
        if (!force && !Plugin.LocalizedNames.Value || _locBroken || string.IsNullOrEmpty(key)) return fallback;
        try
        {
            string r = a8c.f(key);
            if (string.IsNullOrEmpty(r) || r == key || r.StartsWith("@")) return fallback;
            return r;
        }
        catch (Exception ex) { _locBroken = true; Plugin.L.LogWarning("ActionBarsMod: localisation lookup a8c.f failed, names stay uniquenames: " + ex.Message); return fallback; }
    }

    private static readonly Dictionary<string, SpellText> _texts = new();
    private static bool _gdnBroken, _nullableChecked, _nullableOk;

    /// <summary>
    /// The spell's tooltip text as the game builds it: gdn.h(bi1, false, 100, 100, 100, 100, null, null, null) (0x181CB1EB0, what
    /// UISpellSlotBase.OnTooltip 0x1814BCDF0 calls for a slot without an item): gdn.b name (bi1.yw), gdn.d stat lines (dr2.a), gdn.e the
    /// description with the numbers for the local player's stats (null a4i = the local player's, ciy). Fallback: a8c.f(descriptionlocatag).
    /// Cached 10 s (the numbers follow the player's gear).
    /// </summary>
    internal static SpellText Describe(SpellInfo info)
    {
        if (info == null) return null;
        float now = UnityEngine.Time.unscaledTime;
        string tkey = info.Id + ":" + info.Unique;
        if (_texts.TryGetValue(tkey, out var t) && now - t.Made < 10f) return t;
        t = new SpellText { Name = info.Display, Made = now };
        if (info.Spell != null && !_gdnBroken)
        {
            try
            {
                var d = BuildGdn(info.Spell);
                if (d != null)
                {
                    t.Data = d;
                    t.FromGame = true;
                    string n = d.i();
                    if (!string.IsNullOrEmpty(n)) t.Name = n;
                    t.Description = d.l() ?? "";
                    var st = d.k();
                    if (st != null)
                    {
                        var list = new List<string>();
                        for (int i = 0; i < st.Length; i++) { string x = st[i]; if (!string.IsNullOrEmpty(x)) list.Add(x); }
                        t.Stats = list.ToArray();
                    }
                }
            }
            catch (Exception ex) { _gdnBroken = true; Plugin.L.LogWarning("ActionBarsMod: the game's spell tooltip builder gdn.h failed, plain descriptions from now on: " + ex.Message); }
        }
        if (t.Description.Length == 0)
        {
            string desc = Localize(info.DescTag.Length > 0 ? info.DescTag : "@SPELLS_" + info.Unique + "_DESC", "", force: true);
            t.Description = desc;
        }
        _texts[tkey] = t;
        return t;
    }

    private static unsafe gdn BuildGdn(bio spell)
    {
        // GameTimeStamp? argument: the interop wraps it in an Il2CppSystem.Nullable object created with il2cpp_object_new(class);
        // a zero class pointer would crash the client (CameraMod NOTES rule 2), so check it first.
        if (!_nullableChecked)
        {
            _nullableChecked = true;
            IntPtr k = Il2CppClassPointerStore<Il2CppSystem.Nullable<Albion.Common.Time.GameTimeStamp>>.NativeClassPtr;
            _nullableOk = k != IntPtr.Zero;
            Plugin.L.LogInfo("ActionBarsMod: Nullable<GameTimeStamp> class " + (_nullableOk ? "0x" + k.ToInt64().ToString("X") : "NOT available - game tooltip builder unused"));
        }
        if (!_nullableOk) return null;
        var none = new Il2CppSystem.Nullable<Albion.Common.Time.GameTimeStamp>();
        return gdn.h(spell, false, 100f, 100f, 100f, 100f, null, null, none);
    }

    /// <summary>"T4_CAPE@1" -> localised item name (best effort, "@ITEMS_" + name without the enchantment).</summary>
    internal static string ItemName(string item)
    {
        if (string.IsNullOrEmpty(item)) return "";
        string baseName = item;
        int at = item.IndexOf('@');
        if (at >= 0) baseName = item.Substring(0, at);
        string n = Localize("@ITEMS_" + baseName, item);
        return n == item ? item : n + (at >= 0 ? " ." + item.Substring(at + 1) : "") + " (" + item + ")";
    }
}
