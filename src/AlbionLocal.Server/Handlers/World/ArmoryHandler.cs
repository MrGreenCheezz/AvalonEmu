using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    /// <summary>345319 ItemDetailsPopularBuildsView.StartSearch1519790/OnSearchResult1518D50. Each row's link
    /// builds a Stable(35) ArmorySearch for the same activity and main-hand family (14F9520).</summary>
    [Handles(OperationCodes.ArmoryItemUsageStatistics, Status = SessionStatus.InWorld)]
    private AlbionResponse ArmoryItemUsageStatistics(ArmoryItemUsageStatisticsRequest req)
    {
        var response = new ArmoryItemUsageStatisticsResponse { ak5 = [], ak6 = [] };
        if (req.bw is not { Length: > 0 and <= 512 } || req.bx is < 1 or > 20) return response;
        var families = req.bw.Select(id => world.Items[id]).OfType<ItemTemplate>()
            .Where(i => i.Slot == "mainhand").Select(ArmoryUsage.Family).Distinct(StringComparer.Ordinal).ToArray();
        var rows = new List<ArmoryUsage.BuildUsage>();
        foreach (var family in families)
            rows.AddRange(world.Armory.Data.Activities.SelectMany(a => world.Armory.Query(a.Id, 35, [0], [family]))
                .GroupBy(r => r.Activity).Select(g => g.First()).OrderByDescending(r => r.Samples.Count)
                .ThenBy(r => r.Activity).Take(req.bx));
        var distinct = rows.DistinctBy(r => (r.Activity, r.Equipment)).ToArray();
        response.ak5 = distinct.Select(r => r.Activity).ToArray();
        response.ak6 = distinct.Select(r => Primary(r.Equipment, Preferred(r.Samples).Consumables)).ToArray();
        return response;
    }

    /// <summary>Installed345319: gqi.l4 CF8630/CF89D0 consumes ALL outer arrays by key0; gqh ctor CF7640
    /// parses equipment with 8AAE00 and spell selections with 8AA520. Keys12/13/14 are raw metric vectors for
    /// spell/consumable/joint variants (CF55D0); keys4/5/8 are derived radar scores (CF8390), not those same vectors.
    /// Radar scores remain unknown rather than copying raw numbers into the official normalized-score slots.</summary>
    [Handles(OperationCodes.ArmorySearch, Status = SessionStatus.InWorld)]
    private AlbionResponse ArmorySearch(ArmorySearchRequest req)
    {
        var response = new ArmorySearchResponse().FillEmpty();
        if (req.b1 is not (-1 or 1) || req.b2?.Length > 10 || req.b3?.Any(s => s is null || s.Length > 200) == true) return response;
        var rows = world.Armory.Query(req.by, req.bz, req.b2, req.b3, req.b1).ToArray();
        // b0=-1 is the default server ordering; b1 is direction (+1 ascending, -1 descending). Explicit sort indexes
        // are derived metrics, not the raw enum. Keep the documented local qualified-use ordering in that direction
        // when their proprietary score is unavailable, never fabricate it. Native defaults: 8AD2A0/14F9520 use -1/-1.
        var count = rows.Length;
        response.ak7 = count;
        response.ak8 = new string[count]; response.ak9 = new string[count][]; response.ala = new string[count][];
        response.alb = new long[count][][]; response.alc = new long[count][][]; response.alf = new long[count][][];
        response.ald = new int[count][]; response.ale = new int[count][];
        response.alg = new int[count]; response.alh = new int[count]; response.ali = new int[count];
        response.alj = new long[count][][]; response.alk = new long[count][][]; response.all = new long[count][][];
        for (var index = 0; index < count; index++)
        {
            var row = rows[index];
            var preferred = Preferred(row.Samples);
            var spells = Variants(row.Samples.Select(s => s.Spells), preferred.Spells);
            var consumables = Variants(row.Samples.Select(s => s.Consumables), preferred.Consumables);
            var pairs = row.Samples.GroupBy(s => (Spell: Array.IndexOf(spells, s.Spells), Consumable: Array.IndexOf(consumables, s.Consumables)))
                .OrderBy(g => g.Key.Spell).ThenBy(g => g.Key.Consumable).ToArray();
            response.ak8[index] = Primary(row.Equipment, preferred.Consumables);
            // 8AA520 drops empty slot groups; 11C1CA0 assigns each remaining group to fixed slots 0,2,3,4.
            // An incomplete selection cannot round-trip without shifting slots. Keep its observed variant/index and
            // raw metrics, but omit its spell display rather than assign a real spell to the wrong equipment slot.
            response.ak9[index] = spells.Select(s => s.Split(':') is { Length: 4 } groups && groups.All(g => g.Length > 0) ? s : "").ToArray();
            // Constructor inserts key1 as consumable variant0, then parses key3 from index1 onwards.
            response.ala[index] = consumables;
            // 11BAE20/11BAF18 explicitly accepts an empty derived vector and draws the client's zero-outline fallback.
            response.alb[index] = spells.Select(_ => Array.Empty<long>()).ToArray();
            response.alc[index] = consumables.Select(_ => Array.Empty<long>()).ToArray();
            response.ald[index] = pairs.Select(g => g.Key.Spell).ToArray();
            response.ale[index] = pairs.Select(g => g.Key.Consumable).ToArray();
            response.alf[index] = pairs.Select(_ => Array.Empty<long>()).ToArray();
            response.alj[index] = spells.Select(s => ArmoryRawMetrics(row.Activity, row.Samples.Where(x => x.Spells == s))).ToArray();
            response.alk[index] = consumables.Select(c => ArmoryRawMetrics(row.Activity, row.Samples.Where(x => x.Consumables == c))).ToArray();
            response.all[index] = pairs.Select(g => ArmoryRawMetrics(row.Activity, g)).ToArray();
            response.alg[index] = row.Samples.Min(s => s.MinItemPower);
            response.alh[index] = row.Samples.Max(s => s.MaxItemPower);
            var observations = row.Samples.Sum(s => (long)s.ItemPowerObservations);
            response.ali[index] = observations == 0 ? 0 : (int)(row.Samples.Sum(s => s.ItemPowerSum) / observations);
        }
        return response;
    }
    private static string Primary(string equipment, string consumables) => consumables.Length == 0 ? equipment : equipment + ":" + consumables;
    private static ArmorySample Preferred(IReadOnlyList<ArmorySample> rows) => rows.GroupBy(s => (s.Spells, s.Consumables))
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Spells, StringComparer.Ordinal).ThenBy(g => g.Key.Consumables, StringComparer.Ordinal).First().First();
    private static string[] Variants(IEnumerable<string> values, string first) => values.GroupBy(s => s, StringComparer.Ordinal)
        .OrderBy(g => g.Key == first ? 0 : 1).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).ToArray();
    private static long[] ArmoryRawMetrics(int activity, IEnumerable<ArmorySample> source)
    {
        var rows = source.ToArray();
        // Installed b62.cctor808E60, static dictionary+30, read by80E8C0/11B9B90: metric order depends on activity.
        int[] order = activity switch
        {
            17 => [0, 4, 5, 3, 17, 2, 12, 9],
            18 or 19 or 20 => [0, 4, 5, 3, 2, 8, 12, 9],
            13 => [0, 4, 5, 3, 1, 2, 11, 10],
            14 => [0, 4, 5, 3, 1, 2, 11, 9],
            3 or 12 => [0, 4, 5, 3, 17, 1, 2, 11],
            7 or 8 or 9 or 10 => [0, 4, 5, 3, 17, 1, 2, 9],
            5 or 6 => [0, 4, 5, 17, 1, 2, 11, 9],
            2 or 23 or 24 => [0, 4, 5, 3, 1, 2, 11],
            4 => [0, 4, 5, 3, 17, 11, 9],
            28 => [0, 4, 5, 3, 2, 12, 9],
            16 => [0, 3, 17, 2, 1, 12],
            22 => [0, 4, 3, 17, 1, 2, 12, 9],
            _ => [0, 4, 5, 3, 1, 2, 11, 12],
        };
        // Local descriptive statistics: qualified-use count plus per-use arithmetic means. Values are actual
        // observations, not weighted official rankings. a7m conversion saturates instead of overflowing.
        return order.Select(m => RawArmory(m == 0 ? rows.Length : rows.Average(s => s.Value((ArmoryMetric)m)))).ToArray();
    }
    private static long RawArmory(decimal value) => (long)decimal.Clamp(decimal.Truncate(value * 10000), 0, long.MaxValue);
}
