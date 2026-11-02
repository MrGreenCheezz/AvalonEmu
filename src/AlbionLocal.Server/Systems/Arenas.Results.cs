using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>Prospective observations of one native scoreboard row, retained until its match closes.
/// Native 345319 keys rows by name. Replacement local bots retain distinct rows, and the match reserves their
/// names until closing so a later bot cannot overwrite another participant. No previous character totals are imported.</summary>
public sealed class ArenaPlayerResult(string name, int team)
{
    public string Name { get; } = name;
    public int Team { get; } = team;
    public Guid Guild { get; set; }
    public string GuildName { get; set; } = "";
    public Guid Alliance { get; set; }
    public string AllianceTag { get; set; } = "";
    public int Body { get; set; }
    public int Avatar { get; set; }
    public int Ring { get; set; }
    public byte Faction { get; set; }
    public short Rank { get; set; }
    public int RankPoints { get; set; }
    public int[] Equipment { get; set; } = [];
    public int[] Qualities { get; set; } = [];
    public long ItemPower { get; set; }
    public int Kills { get; set; }
    public int Defeats { get; set; }
    public int Assists { get; set; }
    public int Captures { get; set; }
    public double DamageDone { get; set; }
    public double DamageReceived { get; set; }
    public double HealingDone { get; set; }
    public double HealingReceived { get; set; }
    public long Fame { get; set; }
}

/// <summary>Only successfully delivered rewards; kind 6 is an item, 24 personal season points.
/// These are presentation receipts, never another reward authority.</summary>
public sealed record ArenaRewardReceipt(short Kind, int Amount, int Item = 0);

public sealed partial class Arenas
{
    private ArenaPlayerResult? ObserveParticipant(ArenaMatch m, Player p)
    {
        var team = TeamOf(m, p);
        if (team < 0) return null;
        if (m.State == ArenaState.Ended) return m.Results.GetValueOrDefault(p.Character.Name);
        if (!m.Results.TryGetValue(p.Character.Name, out var row))
            m.Results[p.Character.Name] = row = new ArenaPlayerResult(p.Character.Name, team);
        var guild = world.Guilds.Of(p.Character.Id);
        var alliance = world.Alliances.Of(p.Character.Id);
        row.Guild = guild?.Id ?? Guid.Empty;
        row.GuildName = guild?.Name ?? "";
        row.Alliance = alliance?.Id ?? Guid.Empty;
        row.AllianceTag = alliance?.Tag ?? "";
        row.Body = p.IsBot ? p.Character.Template : p.Character.Appearance;
        row.Avatar = p.IsBot ? p.Bot?.Avatar ?? 0 : p.Character.Template;
        row.Ring = p.IsBot ? 0 : Avatars.Ring(p.Character);
        row.Faction = p.Faction;
        var rank = world.Ratings.ArenaRank(p.Character.Id, m.Mode.Category);
        row.Rank = (short)(rank?.Rank ?? 0);
        row.RankPoints = rank?.Points ?? 0;
        row.Equipment = p.Inventory.EquipmentTypeIds();
        row.Qualities = [.. Enumerable.Range(0, Inventory.EquipmentSlots).Select(s => p.Inventory.Equipment[s]?.Quality ?? 0)];
        row.ItemPower = (long)Math.Round(WorldSession.AverageItemPower(p) * 10000);
        return row;
    }

    private ArenaMatch? ResultMatch(Player target) =>
        Find(target.Map?.Cluster) is { State: ArenaState.Fight } m && TeamOf(m, target) >= 0 ? m : null;

    /// <summary>The existing Armory/combat hook supplies actual health removed, after shields and health clamps.
    /// Mount damage, overkill, rejected hits and environmental damage with no player source earn no damage-done credit.</summary>
    public void RecordDamage(Player? attacker, Player target, double effective)
    {
        if (!double.IsFinite(effective) || effective <= 0 || ResultMatch(target) is not { } m) return;
        if (ObserveParticipant(m, target) is { } victim) victim.DamageReceived += effective;
        if (attacker is not null && attacker != target && attacker.Map == target.Map
            && TeamOf(m, attacker) is var team && team >= 0 && team != TeamOf(m, target)
            && ObserveParticipant(m, attacker) is { } source) source.DamageDone += effective;
    }

    /// <summary>The accepted heal's effective increase, including over-time ticks and excluding overhealing.
    /// Natural regeneration and the arena's resets/get-ups never pass this combat hook.</summary>
    public void RecordHealing(Player caster, Player target, double effective)
    {
        if (!double.IsFinite(effective) || effective <= 0 || caster.Map != target.Map
            || ResultMatch(target) is not { } m || TeamOf(m, caster) != TeamOf(m, target)) return;
        if (ObserveParticipant(m, caster) is { } healer) healer.HealingDone += effective;
        if (ObserveParticipant(m, target) is { } healed) healed.HealingReceived += effective;
    }

    /// <summary>One authoritative arena defeat, before the victim despawns/gets up. This counts nonlethal arena KOs;
    /// it never turns them into world deaths or awards kill fame. Assists reuse Death.Attackers' existing human-credit
    /// policy and combat timeout; the proprietary arena assist rule and bot-assist eligibility are not established.</summary>
    public void RecordDefeat(Player? attacker, Player target)
    {
        if (ResultMatch(target) is not { } m) return;
        if (ObserveParticipant(m, target) is { } victim) victim.Defeats++;
        if (attacker is null || attacker == target || attacker.Map != target.Map
            || TeamOf(m, attacker) is not (0 or 1) || TeamOf(m, attacker) == TeamOf(m, target)) return;
        if (ObserveParticipant(m, attacker) is { } killer) killer.Kills++;
        var timeout = TimeSpan.FromSeconds(world.GameData?.InCombatActiveSeconds ?? 20);
        foreach (var (player, hit) in target.PvpState.HitBy)
            if (player != attacker && !player.IsBot && hit.Damage > 0 && world.Clock.Now - hit.Last <= timeout
                && player.Map == target.Map && TeamOf(m, player) == TeamOf(m, attacker)
                && ObserveParticipant(m, player) is { } assist) assist.Assists++;
    }

    private static List<ArenaRewardReceipt> RewardReceipts(ArenaMatch m, Guid character, bool rankUp = false)
    {
        var receipts = rankUp ? m.RankUpReceipts : m.RewardReceipts;
        if (!receipts.TryGetValue(character, out var rows)) receipts[character] = rows = [];
        return rows;
    }

    /// <summary>
    /// The 345319 match constructor starts with an empty named-row dictionary (ABFFD0), and event 119 only supplies character Guids
    /// (AC0E00). ScoreboardGui.ParseMatchData (140A3AE) reads that dictionary even during a live match. Event 169 creates/updates its
    /// rows (AC1E70); it does not end the match. Its empty-name notification is ignored by an already-visible scoreboard (140B141).
    /// Event 168 kind 2 assigns the absolute kill count (AC34EB) and emits a named-row notification (AC3605), refreshing that row.
    /// Publish observed participants at entry and score changes; event 120 still supplies the final damage totals.
    /// </summary>
    private void HudRoster(ArenaMatch m, Player? recipient = null)
    {
        if (!HasMatchHud(m)) return;
        foreach (var player in InMap(m)) ObserveParticipant(m, player);
        var rows = m.Results.Values.OrderBy(row => row.Team).ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
        var message = CompleteResults(m, rows);
        if (recipient is not null) recipient.Session.Send(message);
        else ToMap(m, message);
        foreach (var row in rows)
        {
            var update = new MatchPlayerStatsEventEvent { cp1 = m.Guid, cp2 = 2, cp3 = row.Name, cp4 = row.Kills };
            if (recipient is not null) recipient.Session.Send(update);
            else ToMap(m, update);
        }
    }

    /// <summary>345319 dd3.am AC1D30 creates named rows before AC1110 can apply event 120 totals.
    /// Keys 15/16/17 become dd4 +6C/+70/+74 (K/D/A), 14 +78 (OrbLabel); 18/19 are raw a7m fame/IP.
    /// Avatar/body, guild/alliance and ten equipment slots use their observed values. Key 1 is the first-team flag
    /// (ScoreboardGui.AddOrUpdateData 14072CA). Keys 12/13 are the participant's observed rank/RP before this result:
    /// 140FF0B resolves key 12 through the season's b7q rank dictionary and 140FF91 formats key 13 with the rank name.
    /// asc.ToString 8E2094 names key 2 IsMerc: arena queues have no GvG mercenaries, so it is false for every participant.
    /// The same native formatter confirms key 7 is PlayerAllianceTag and key 11 FactionEnlistments (not the PvP flag).
    /// Channel captures identify a player;
    /// zone-capture participation has no confirmed individual credit.</summary>
    private static MatchPlayerStatsCompleteEventEvent CompleteResults(ArenaMatch m, ArenaPlayerResult[] rows) => new()
    {
        cpf = m.Guid,
        cpg = [.. rows.Select(r => r.Team == 0)], cph = new bool[rows.Length],
        cpi = [.. rows.Select(r => r.Name)], cpj = [.. rows.Select(r => r.Guild)], cpk = [.. rows.Select(r => r.GuildName)],
        cpl = [.. rows.Select(r => r.Alliance)], cpm = [.. rows.Select(r => r.AllianceTag)],
        cpn = [.. rows.Select(r => r.Body)], cpo = [.. rows.Select(r => r.Avatar)], cpp = [.. rows.Select(r => r.Ring)],
        cpq = [.. rows.Select(r => r.Faction)], cpr = [.. rows.Select(r => r.Rank)], cps = [.. rows.Select(r => r.RankPoints)],
        cpt = [.. rows.Select(r => r.Captures)], cpu = [.. rows.Select(r => r.Kills)],
        cpv = [.. rows.Select(r => r.Defeats)], cpw = [.. rows.Select(r => r.Assists)],
        cpx = [.. rows.Select(r => r.Fame)], cpy = [.. rows.Select(r => r.ItemPower)],
        cpz = [.. rows.Select(r => r.Equipment)], cp0 = [.. rows.Select(r => r.Qualities)],
    };
}
