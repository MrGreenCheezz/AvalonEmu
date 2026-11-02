using System.Text.Json;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Records facts at the existing fame/combat authorities, under World.Sync. It does not award fame,
/// resolve combat or infer old statistics from progression. GM grants, bots, duels and arena ticket
/// losses are not player-kill statistics. The recorded histories have a local retention limit of 20.
/// </summary>
public static class CharacterStatisticsRecorder
{
    private const int HistoryLimit = 20;

    private static CharacterStatistics Start(Player p, long ticks) => p.Character.Statistics ??= new()
    {
        CoveredSinceUtc = ticks, FameBeforeCoverage = p.Character.Fame,
    };

    private static long Add(long total, long gain) => gain <= 0 ? total : total > long.MaxValue - gain ? long.MaxValue : total + gain;
    private static int Increment(int total) => total == int.MaxValue ? total : total + 1;

    /// <summary>
    /// Called before Character.Fame changes, after the game's rate/guild modifiers. Premium board
    /// progress is not total fame. PvP shares are recorded only by the real player-result authority.
    /// Completion rewards that bypass activity journals are excluded: that path is also used by
    /// GM finish commands and cannot prove an actual mob kill.
    /// </summary>
    public static void Fame(Player p, long gain, FameContext context, bool pvp, bool activity)
    {
        if (p.IsBot || p.Character.Transient || gain <= 0 || context.Only is not null || !activity) return;
        var stats = Start(p, p.World.GameNow);
        if (pvp) return;
        switch (context.Source)
        {
            case FameSource.Kill: stats.PveFame = Add(stats.PveFame, gain); break;
            case FameSource.Harvest:
            case FameSource.Fishing: stats.GatheringFame = Add(stats.GatheringFame, gain); break;
            case FameSource.Craft: stats.CraftingFame = Add(stats.CraftingFame, gain); break;
            case FameSource.Farm: stats.FarmingFame = Add(stats.FarmingFame, gain); break;
        }
    }

    /// <summary>Freeze equipment before death drops it; applying counters/saving belongs at the end of the result.</summary>
    public static string CapturePvpResult(PvpKill result) => Details(result.Killer.Player!, result.Victim.Player!, null,
        result.FameShares.Aggregate(0L, (total, share) => Add(total, share.Fame)), result.Ticks,
        Math.Max(1, result.Attackers), result.Map.Cluster);

    public static void PvpResult(World world, PvpKill result, string details)
    {
        if (result.Outcome == PvpOutcome.DuelWin
            || result.Killer.Player is not { IsBot: false, Character.Transient: false } killer
            || result.Victim.Player is not { IsBot: false, Character.Transient: false } victim || killer == victim) return;
        var won = Start(killer, result.Ticks);
        var lost = Start(victim, result.Ticks);
        var participants = new HashSet<Player> { killer, victim };
        long fame = 0;
        foreach (var (p, amount) in result.FameShares)
        {
            if (p.IsBot || p.Character.Transient || amount <= 0) continue;
            var stats = Start(p, result.Ticks);
            stats.PvpFame = Add(stats.PvpFame, amount);
            fame = Add(fame, amount);
            participants.Add(p);
        }
        var knockedDown = result.Outcome == PvpOutcome.Knockdown;
        var id = Guid.NewGuid();
        var win = new CharacterCombatRecord(id, victim.Character.Name, 1, fame, result.Ticks, details, knockedDown);
        var loss = new CharacterCombatRecord(id, killer.Character.Name, 1, fame, result.Ticks, details, knockedDown);
        if (knockedDown)
        {
            won.Knockdowns = Increment(won.Knockdowns);
            lost.TimesKnockedDown = Increment(lost.TimesKnockedDown);
            Push(won.KnockdownHistory, win);
            Push(lost.KnockedDownHistory, loss);
        }
        else
        {
            won.PlayersKilled = Increment(won.PlayersKilled);
            lost.Deaths = Increment(lost.Deaths);
            Push(won.Kills, win);
            Push(lost.DeathHistory, loss);
        }
        // The death/knockdown authority calls this after its item mutations, not from PvpGoals.
        // A background writer may take this snapshot immediately: it must already contain the final inventory.
        world.Saves.SaveTogether(participants.Select(p => p.Character).ToArray());
    }

    /// <summary>Only Knockdown.Start's actual PvE mob branch calls this; never .kill or a duel.</summary>
    public static void MobKnockdown(Player victim, Mob mob, long ticks)
    {
        if (victim.IsBot || victim.Character.Transient || victim.Map is not { } map) return;
        var stats = Start(victim, ticks);
        stats.TimesKnockedDown = Increment(stats.TimesKnockedDown);
        Push(stats.KnockedDownHistory, new(Guid.NewGuid(), mob.Template.Name, 2, 0, ticks,
            Details(null, victim, mob.Template.Name, 0, ticks, 1, map.Cluster), true));
        victim.World.Saves.MarkChanged(victim.Character);
    }

    /// <summary>
    /// A real death after a mob knockdown. An absent mob may use only the template identity recorded
    /// for this same live object at the knockdown; localized killer text alone does not identify a template.
    /// No record for unknown attackers or a GM's self-knockdown. Equipment is still captured before Drop.
    /// </summary>
    public static CharacterCombatRecord? CaptureMobDeath(Player victim, long sourceId, long ticks)
    {
        if (victim.IsBot || victim.Character.Transient || sourceId == victim.Id || victim.Map is not { } map) return null;
        var down = victim.Combat.Down;
        var name = (map.Objects.GetValueOrDefault(sourceId) as Mob)?.Template.Name
            ?? (down is { Kind: KnockdownKind.Pve } && down.Killer == sourceId ? down.KillerMobName : null);
        if (string.IsNullOrEmpty(name)) return null;
        return new(Guid.NewGuid(), name, 2, 0, ticks, Details(null, victim, name, 0, ticks, 1, map.Cluster), false);
    }

    public static void MobDeath(Player victim, CharacterCombatRecord record)
    {
        var stats = Start(victim, record.Ticks);
        stats.Deaths = Increment(stats.Deaths);
        Push(stats.DeathHistory, record);
        // Called after Drop has installed the final item state, also when the death has no item loss.
        victim.World.Saves.SaveNow(victim.Character);
    }

    private static void Push(List<CharacterCombatRecord> history, CharacterCombatRecord entry)
    {
        history.Insert(0, entry);
        if (history.Count > HistoryLimit) history.RemoveRange(HistoryLimit, history.Count - HistoryLimit);
    }

    /// <summary>
    /// Native KillHistoryDetails JSON. Current 345319 BF4C90/C05940 deserializes event 148 key 2;
    /// matched ShowDetails fragments 103A7DF/103AA8E/103AC86 read these names, equipment, body/avatar
    /// and power snapshots; 103AFAE reads f as raw a7m. Do not populate unrecorded crafter/soul metadata.
    /// </summary>
    private static string Details(Player? killer, Player victim, string? mobName, long fame, long ticks, int attackers, string cluster)
    {
        object?[] Equipment(Player p) => Enumerable.Range(0, Inventory.EquipmentSlots).Select(slot =>
        {
            if (p.Inventory.Equipment[slot] is not { } item) return null;
            var record = item.ToRecord(id => p.World.Spells?.Name(id));
            return (object)new
            {
                id = record.Name, c = record.Count, q = record.Quality,
                asp = record.Active ?? [], psp = record.Passive ?? [],
            };
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            dt = new DateTime(ticks, DateTimeKind.Utc), f = fame, p = attackers, c = cluster,
            kn = killer?.Character.Name ?? mobName!, kft = killer is null ? 2 : 1,
            kgn = killer is null ? "" : killer.World.Guilds.Of(killer.Character.Id)?.Name ?? "",
            ki = killer is null ? Array.Empty<object?>() : Equipment(killer),
            kp = killer is null ? 0 : (float)WorldSession.AverageItemPower(killer),
            kat = killer?.Character.Template ?? 0, kct = killer?.Character.Appearance ?? 0,
            dn = victim.Character.Name, dgn = victim.World.Guilds.Of(victim.Character.Id)?.Name ?? "",
            di = Equipment(victim), dp = (float)WorldSession.AverageItemPower(victim),
            dat = victim.Character.Template, dct = victim.Character.Appearance,
        });
    }
}
