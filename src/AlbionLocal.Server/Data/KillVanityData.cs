using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// killemotes.xml (stageR kill-vanity SPEC §1, analysis/work/stageR/kill-vanity/SPEC.md): the victory emotes, in file order. Two numbers name
/// one emote on the wire:
/// <list type="bullet">
/// <item><see cref="KillEmote.Visual"/>, the index NewVisualEventObject key 5 carries (cmx.aa5 → bhb.abo(short)): bhc.nd 0x1806A55A0 hands out
/// the static counter and then raises it (MOVZX EAX; LEA EDX,[RAX+1]), reset to 0 by bhb.da, so the first &lt;killemote&gt; is 0.</item>
/// <item><see cref="KillEmote.Id"/>, the consumable id of GetVanityConsumables, EquipKillEmote, Join key 119, BuyVanityConsumableCharges and
/// event 517: bhb.db 0x1806A5170 makes a bgj.b for every emote in its Dictionary&lt;string, bhc&gt;'s order, whose constructor takes the bgj.a
/// counter and raises it; bgj's constructor resets it to bgj.a.l, 1 (bgj.a..cctor 0x18067CDC0). So the first emote is 1 and 0 is "none"
/// (EquipKillEmote 0 is the unequip of dq8.aqp). [infer: the dictionary enumerates in insertion order, the file order, as nothing is removed]</item>
/// </list>
/// </summary>
public sealed record KillEmote(short Id, short Visual, string Name, int GoldPrice, int BundledCharges, double Duration, string? ItemReference);

/// <summary>A kill trophy table of killtrophy.xml: its tiers, highest last as in the file (the roll goes from the highest down).</summary>
public sealed record KillTrophyTable(string Id, IReadOnlyList<KillTrophyTier> Tiers);

/// <summary>One tier: the victim's item value it needs, the quotient of its chance and the trophy item.</summary>
public sealed record KillTrophyTier(double MinimumItemValue, double ItemValueQuotient, string Item);

/// <summary>The victory emotes (killemotes.xml) and kill trophy tables (killtrophy.xml) of the client's GameData.</summary>
public sealed class KillVanityData
{
    public IReadOnlyList<KillEmote> Emotes { get; }

    public IReadOnlyDictionary<string, KillTrophyTable> Trophies { get; }

    /// <summary>killemotes.xml globalsettings maxtriggerdistance (25 m).</summary>
    public double MaxTriggerDistance { get; }

    public static KillVanityData Empty { get; } = new([], new Dictionary<string, KillTrophyTable>(), 25);

    public KillVanityData(IReadOnlyList<KillEmote> emotes, IReadOnlyDictionary<string, KillTrophyTable> trophies, double maxTriggerDistance)
    {
        (Emotes, Trophies, MaxTriggerDistance) = (emotes, trophies, maxTriggerDistance);
    }

    public KillEmote? Emote(short id) => id >= 1 && id <= Emotes.Count ? Emotes[id - 1] : null;

    public KillEmote? Emote(string name) => Emotes.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads killemotes.bin and killtrophy.bin of <paramref name="folder"/>.</summary>
    public static KillVanityData Load(string folder) =>
        Load(GameDataFile.Open(folder, "killemotes.bin").Root(), GameDataFile.Open(folder, "killtrophy.bin").Root());

    public static KillVanityData Load(XElement killemotes, XElement killtrophies)
    {
        var inv = CultureInfo.InvariantCulture;
        var emotes = new List<KillEmote>();
        foreach (var e in killemotes.Element("emotes")?.Elements("killemote") ?? [])
        {
            var name = (string?)e.Attribute("uniquename");
            if (string.IsNullOrEmpty(name)) continue;
            var duration = double.TryParse((string?)e.Attribute("duration"), NumberStyles.Float, inv, out var d) && d > 0 ? d : 1; // bhc.no: 1 s without it
            var bundle = XmlAttr.Int(e, "bundledchargesamount", 0);
            var gold = XmlAttr.Int(e, "goldprice", 0);
            emotes.Add(new KillEmote((short)(emotes.Count + 1), (short)emotes.Count, name, gold, gold > 0 && bundle <= 0 ? 1 : bundle, duration,
                (string?)e.Attribute("itemreference")));
        }
        var settings = killemotes.Element("globalsettings");
        var distance = settings is not null && double.TryParse((string?)settings.Attribute("maxtriggerdistance"), NumberStyles.Float, inv, out var m) ? m : 25;
        var tables = new Dictionary<string, KillTrophyTable>(StringComparer.Ordinal);
        foreach (var t in killtrophies.Elements("killtrophy"))
        {
            if ((string?)t.Attribute("id") is not { Length: > 0 } id) continue;
            var tiers = t.Elements("tier").Select(x => new KillTrophyTier(XmlAttr.Int(x, "minimumitemvalue", 0), Math.Max(1, XmlAttr.Int(x, "itemvaluequotient", 1)),
                XmlAttr.Str(x, "item"))).Where(x => x.Item.Length > 0).ToList();
            tables[id] = new KillTrophyTable(id, tiers);
        }
        return new KillVanityData(emotes, tables, distance);
    }
}
