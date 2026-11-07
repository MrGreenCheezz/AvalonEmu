using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>
/// outpost.xml (stageQ outposts, analysis/work/stageQ/outposts/DATA.md §1): the faction warfare towers' guard rosters and capture rules.
/// <list type="bullet">
/// <item><c>guardsetups/guardsetup[@name]/faction[@name]/tier[@tier]/guardmobtype[@mob @amount @respawndelay]</c>: one setup, "default";
/// per faction and cluster tier 1 boss (T5_MOB_CITY_&lt;F&gt;_BOSS, respawndelay 15) and 7 guards (…_GUARD, 30; Caerleon 3 GUARD_MALE + 4
/// GUARD_FEMALE), T5 at tiers 4-5, T6 at 6-8.</item>
/// <item><c>outpost[@uniquename]</c> (OUTPOST_SAFE_1/3/5, OUTPOST_LETHAL_1/3 and five biome entries, all with the same values): respawnblockdelay
/// 15, minor/majorattackstatepersistence 15 / 30, bossmajoralerthealth 0.7, and <c>outpostcapture</c>: capturezoneradius 12,
/// participationzoneradius 50, participationtimerseconds 60, owner/attacker claim 16, unclaim 8, ownernoteamclaimtime and
/// attackernoteamunclaimtime 30, noteamfullreclaim true.</item>
/// </list>
/// The times are read as seconds [infer: the captured claim took 8 + 16 s, the contest ended 30 s after it].
/// </summary>
public sealed class OutpostData
{
    /// <summary>One guardmobtype: <paramref name="Amount"/> of mobs.xml <paramref name="Mob"/>, back <paramref name="RespawnSeconds"/> after a death.</summary>
    public sealed record GuardMob(string Mob, int Amount, double RespawnSeconds);

    /// <summary>One outpost entry (its uniquename is the tower member's outpost type, OUTPOST_SAFE_3 …).</summary>
    public sealed record Rules(
        string Name, double RespawnBlockSeconds, double MinorAttackSeconds, double MajorAttackSeconds, double BossMajorAlertHealth,
        double CaptureRadius, double ParticipationRadius, double ParticipationSeconds,
        double OwnerClaimSeconds, double OwnerUnclaimSeconds, double OwnerNoTeamClaimSeconds,
        double AttackerClaimSeconds, double AttackerUnclaimSeconds, double AttackerNoTeamUnclaimSeconds, bool NoTeamFullReclaim, string GuardSetup);

    /// <summary>The values every entry of the 345319 file has (the rules of a type the file does not name).</summary>
    public static readonly Rules Default = new("OUTPOST", 15, 15, 30, 0.7, 12, 50, 60, 16, 8, 30, 16, 8, 30, true, "default");

    private readonly Dictionary<string, Rules> _outposts;
    private readonly Dictionary<(string Setup, byte Faction, int Tier), IReadOnlyList<GuardMob>> _guards;

    private OutpostData(Dictionary<string, Rules> outposts, Dictionary<(string, byte, int), IReadOnlyList<GuardMob>> guards) =>
        (_outposts, _guards) = (outposts, guards);

    /// <summary>The rules of outpost type <paramref name="type"/> (the file's entry, else <see cref="Default"/>).</summary>
    public Rules For(string type) => _outposts.GetValueOrDefault(type) ?? Default;

    /// <summary>
    /// The roster of <paramref name="setup"/> for faction <paramref name="faction"/> (FlaggingStatus 1-6) in a cluster of
    /// <paramref name="tier"/> (clamped to the file's tiers: a T3 cluster takes the lowest); empty when the file has none.
    /// </summary>
    public IReadOnlyList<GuardMob> Guards(string setup, byte faction, int tier)
    {
        var tiers = _guards.Keys.Where(k => k.Setup == setup && k.Faction == faction).Select(k => k.Tier).Order().ToList();
        if (tiers.Count == 0) return [];
        var t = tiers.LastOrDefault(x => x <= tier, tiers[0]);
        return _guards[(setup, faction, t)];
    }

    public static OutpostData Load(XElement root)
    {
        var inv = CultureInfo.InvariantCulture;
        double D(XElement? e, string name, double fallback) =>
            double.TryParse((string?)e?.Attribute(name), NumberStyles.Float, inv, out var v) ? v : fallback;
        var outposts = new Dictionary<string, Rules>(StringComparer.Ordinal);
        foreach (var o in root.Elements("outpost"))
        {
            if ((string?)o.Attribute("uniquename") is not { Length: > 0 } name) continue;
            var c = o.Element("outpostcapture");
            var d = Default;
            outposts[name] = new Rules(name, D(o, "respawnblockdelay", d.RespawnBlockSeconds), D(o, "minorattackstatepersistence", d.MinorAttackSeconds),
                D(o, "majorattackstatepersistence", d.MajorAttackSeconds), D(o, "bossmajoralerthealth", d.BossMajorAlertHealth),
                D(c, "capturezoneradius", d.CaptureRadius), D(c, "participationzoneradius", d.ParticipationRadius), D(c, "participationtimerseconds", d.ParticipationSeconds),
                D(c, "ownerclaimtime", d.OwnerClaimSeconds), D(c, "ownerunclaimtime", d.OwnerUnclaimSeconds), D(c, "ownernoteamclaimtime", d.OwnerNoTeamClaimSeconds),
                D(c, "attackerclaimtime", d.AttackerClaimSeconds), D(c, "attackerunclaimtime", d.AttackerUnclaimSeconds),
                D(c, "attackernoteamunclaimtime", d.AttackerNoTeamUnclaimSeconds), (string?)c?.Attribute("noteamfullreclaim") != "false",
                (string?)o.Element("guards")?.Attribute("setup") ?? "default");
        }
        var guards = new Dictionary<(string, byte, int), IReadOnlyList<GuardMob>>();
        foreach (var s in root.Element("guardsetups")?.Elements("guardsetup") ?? [])
            foreach (var f in s.Elements("faction"))
            {
                var flag = FactionWarfareData.FlagOf((string?)f.Attribute("name"));
                if (flag == 0) continue;
                foreach (var t in f.Elements("tier"))
                    if (int.TryParse((string?)t.Attribute("tier"), NumberStyles.Integer, inv, out var tier))
                        guards[((string?)s.Attribute("name") ?? "default", flag, tier)] = [.. t.Elements("guardmobtype")
                            .Select(g => new GuardMob((string?)g.Attribute("mob") ?? "", (int)D(g, "amount", 1), D(g, "respawndelay", 30)))
                            .Where(g => g.Mob.Length > 0 && g.Amount > 0)];
            }
        return new OutpostData(outposts, guards);
    }
}
