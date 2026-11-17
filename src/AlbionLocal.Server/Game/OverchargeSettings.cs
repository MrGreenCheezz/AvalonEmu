using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Game;

/// <summary>
/// gamedata.xml &lt;OverCharge warningtime overchargeitem buttonholdtime&gt; and its &lt;stack&gt;s (stageR items SPEC §2; the client
/// reads them into bgf+0x8e8 warning time, +0x8f8 the token item, +0x908 the stacks, bgf.acb(stack)): per stack the item power bonus,
/// the duration (s, total from the first activation), the tokens it costs, the chance to break at the end and the durability it
/// costs when it does not break (a share of the full durability [infer]).
/// </summary>
public sealed record OverchargeSettings(double WarningSeconds, string Item, IReadOnlyList<OverchargeStack> Stacks)
{
    /// <summary>The 345319 values: UNIQUE_GVGTOKEN_GENERIC (Siphoned Energy), +100 IP for 1800 s for 10 tokens (15 %, 0.3), then 3600 s for none (30 %, 0.6).</summary>
    public static readonly OverchargeSettings Default = new(60, "UNIQUE_GVGTOKEN_GENERIC",
        [new(100, 1800, 10, 0.15, 0.3, "OVERCHARGE"), new(100, 3600, 0, 0.30, 0.6, "OVERCHARGE2")]);

    /// <summary>The element's values; null without the element or without a stack.</summary>
    public static OverchargeSettings? From(XElement? e)
    {
        if (e is null) return null;
        static double Num(XElement x, string a, double d) =>
            double.TryParse((string?)x.Attribute(a), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        var stacks = e.Elements("stack").Select(s => new OverchargeStack(Num(s, "bonus", 0), Num(s, "duration", 0), (int)Num(s, "overchargeitemcount", 0),
            Num(s, "trashchance", 0), Num(s, "durabilityloss", 0), (string?)s.Attribute("fakeslotspell") ?? "")).ToList();
        return stacks.Count == 0 ? null : new OverchargeSettings(Num(e, "warningtime", 60), (string?)e.Attribute("overchargeitem") ?? "", stacks);
    }
}

/// <summary>One &lt;stack&gt; of gamedata OverCharge.</summary>
public sealed record OverchargeStack(double Bonus, double Duration, int ItemCount, double TrashChance, double DurabilityLoss, string FakeSlotSpell);
