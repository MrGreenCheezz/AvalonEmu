using System.Globalization;

namespace AlbionLocal.Server.Data;

/// <summary>
/// A labourer of buildings.xml (&lt;labourer&gt;, the client's b53 parsed by b53.ul 0x180802830; analysis/work/stageN/labourers/SPEC.md §1.2):
/// T2..T8 × wood, stone, ore, fiber, hide, mercenary, warrior, hunter, mage, toolmaker, fishing. Placed it is a building of its own type
/// (NewBuilding key 2 = <see cref="Id"/>, the client makes a cph LaborerObject of it, cjb.a 0x180A9FE50). Times in seconds, fame and
/// happiness in whole units (the client reads them as a7m).
/// </summary>
public sealed record LabourerTemplate
{
    /// <summary>gamedata.xml &lt;LabourerSettings happinessperbarsegment&gt;: the happiness of one unit (a bar segment).</summary>
    public const int HappinessPerUnit = 100;

    /// <summary>gamedata.xml &lt;LabourerSettings maxyield&gt;: the yield at full happiness for the journal's tier (b53.j 0x180801A00).</summary>
    public const double MaxYield = 1.5;

    public required short Id { get; init; }
    public required string Name { get; init; }
    public int Tier { get; init; }

    /// <summary>contractitem: the labourercontract item that hires this labourer when placed (T4_LABOURER_CONTRACT_WOOD).</summary>
    public string? ContractItem { get; init; }

    /// <summary>hireprice (1000 for all): the whole silver of hiring it from a house's "Hire labourer" menu without a contract.</summary>
    public double HirePrice { get; init; }

    /// <summary>settleintime (1800 s): it does not work before it has settled in after the hire.</summary>
    public double SettleInSeconds { get; init; }

    /// <summary>joblength (79200 s = 22 h): how long a job takes.</summary>
    public double JobSeconds { get; init; }

    /// <summary>fametoprogress: the labourer fame that lets it advance to <see cref="UpgradeableTo"/> (0 at T8).</summary>
    public double FameToProgress { get; init; }

    public double YieldMultiplier { get; init; } = 1;

    /// <summary>happinessmovementtickrate (360): the seconds the happiness takes to move one unit (100) towards its target [guess, SPEC §3].</summary>
    public double HappinessTickSeconds { get; init; }

    /// <summary>happinessunitsafterhire: the units of happiness a new labourer has.</summary>
    public int HappinessAfterHire { get; init; }

    /// <summary>happinessunits: the most happiness, in units (b53+0x240 = this × 100).</summary>
    public int HappinessUnits { get; init; }

    public double MaxBed { get; init; }
    public double MaxTable { get; init; }
    public double MaxTrophy { get; init; }

    /// <summary>profession (wood, stone, ore, fiber, hide, mercenary, …): which trophies please it.</summary>
    public string Profession { get; init; } = "";

    public string? UpgradeableTo { get; init; }

    /// <summary>avatar, avatarring (LaborerObjectInfo keys 1/2, SPEC §2.2 [guess]).</summary>
    public string Avatar { get; init; } = "";
    public string AvatarRing { get; init; } = "";

    /// <summary>&lt;journalssold&gt;: the _EMPTY journals it sells (LaborerBuyItem 261).</summary>
    public IReadOnlyList<string> JournalsSold { get; init; } = [];

    /// <summary>&lt;journalsaccepted&gt;: the _FULL journals it works with (LaborerStartJob 257).</summary>
    public IReadOnlyList<string> JournalsAccepted { get; init; } = [];

    /// <summary>The most happiness (whole units × 100).</summary>
    public double MaxHappiness => HappinessUnits * HappinessPerUnit;

    /// <summary>
    /// The yield of a job with a journal of <paramref name="journalTier"/> at <paramref name="happiness"/> (b53.j 0x180801A00): below the
    /// journal's 100 × tier the share of it; above, up to <see cref="MaxYield"/> at 100 × (tier + 1).
    /// </summary>
    public static double Yield(double happiness, int journalTier)
    {
        var needed = (double)HappinessPerUnit * journalTier;
        if (needed <= 0) return 1;
        if (happiness < needed) return happiness / needed;
        if (happiness == needed) return 1;
        return Math.Min(MaxYield, (happiness - needed) / HappinessPerUnit * (MaxYield - 1) + 1);
    }

    /// <summary>The labourers of <paramref name="store"/> (&lt;labourer&gt; elements) by uniquename.</summary>
    public static IReadOnlyDictionary<string, LabourerTemplate> All(BuildingStore store) =>
        store.All.Where(t => t.Kind == "labourer").Select(From).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public static LabourerTemplate From(BuildingTemplate t)
    {
        double Num(string key, double fallback = 0) =>
            double.TryParse(t.Attrs.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
        IReadOnlyList<string> Journals(string list) =>
            [.. (t.Element.Element(list)?.Elements("journal") ?? []).Select(j => (string?)j.Attribute("uniquename")).OfType<string>()];
        return new LabourerTemplate
        {
            Id = t.Id,
            Name = t.Name,
            Tier = t.Tier,
            ContractItem = t.Attrs.GetValueOrDefault("contractitem"),
            HirePrice = Num("hireprice"),
            SettleInSeconds = Num("settleintime"),
            JobSeconds = Num("joblength", 79200),
            FameToProgress = Num("fametoprogress"),
            YieldMultiplier = Num("yieldmultiplier", 1),
            HappinessTickSeconds = Num("happinessmovementtickrate", 360),
            HappinessAfterHire = (int)Num("happinessunitsafterhire"),
            HappinessUnits = (int)Num("happinessunits", 3),
            MaxBed = Num("maxbedcontribution"),
            MaxTable = Num("maxtablecontribution"),
            MaxTrophy = Num("maxtrophycontribution"),
            Profession = t.Attrs.GetValueOrDefault("profession", ""),
            UpgradeableTo = t.Attrs.GetValueOrDefault("upgradeableto"),
            Avatar = t.Attrs.GetValueOrDefault("avatar", ""),
            AvatarRing = t.Attrs.GetValueOrDefault("avatarring", ""),
            JournalsSold = Journals("journalssold"),
            JournalsAccepted = Journals("journalsaccepted"),
        };
    }
}
