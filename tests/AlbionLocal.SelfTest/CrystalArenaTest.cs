using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;
using Microsoft.Data.Sqlite;
using static AlbionLocal.SelfTest.ArenaRulesTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageP P6 (analysis/work/stageP/league-pvp/SPEC.md §3.1, §3.4): the client's arena category 1 is the Crystal Arena on CrystalRealm03
/// with MATCH_CRYSTAL_GVG_NONLETHAL: 150 tickets, 6 per death, the channel runestones and the 120 s round score. P7 (§3.3): the rank
/// points (the [H] placeholder formula), the ranks, the rank-up chest, GetArenaRankings, the saved ranks (schema v42).
/// </summary>
public static class CrystalArenaTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("crystal arena P6: category 1 → a 5v5 on CrystalRealm03 (150 tickets, 3 channel orbs, 900 soft cap); a kill costs 6; a channel claim −1, after 120 s the orb majority takes 20 and its orb resets", Match),
        ("arena ranks P7: RP of a win/loss from the rank's modifiers (1v1 × 0.5); a 1v1 win at 140 RP crosses 150 → IRON_2 and its rank-up chest; GetArenaRankings season 12, [1, 2], ranks and RP; saved, v41 save migrated", Ranks),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static void Match()
    {
        Check(Arenas.ModeOfCategory(1, crystal: false)?.Name == "5v5" && Arenas.ModeOfCategory(1)?.Name == Arenas.Crystal && Arenas.ModeOf("5")?.Name == "5v5",
              "category 1: the Crystal Arena, the 5v5 with Arena:CrystalArena false");
        using var s = NewSetup(null, [.. Enumerable.Range(0, 10).Select(i => ($"Cr{(char)('a' + i)}", "T8_MAIN_SWORD@3"))]);
        var m = Enter(s, Arenas.ModeOfCategory(1, s.World.Config.Arena.CrystalArena)!);
        var gil = s.Players[0].Session.Player!;
        var team = m.Team[gil.Character.Id];
        var sword = gil.Inventory.Equipment[0]!;
        Check(m.Mode.Name == Arenas.Crystal && m.Mode.Cluster == "CrystalRealm03" && gil.Map?.Cluster == m.Id && m.Tickets is [150, 150]
              && m.Orbs.Count == 3 && m.Orbs.All(o => o.Type is { Name: "ORB_CRYSTAL_GVG_WINNER_RESET_INDIVIDUAL_CHANNEL", Channel: true }) && m.Camps.Length == 2
              && gil.ItemPowerCap is { Cap: 900, Factor: 0.2 } && Combat.ItemPower(gil, sword) is > 900 and < 1100,
              $"a crystal match on {m.Mode.Cluster}: tickets {string.Join("/", m.Tickets)}, {m.Orbs.Count} orbs ({string.Join(", ", m.Orbs.Select(o => o.Type.Name).Distinct())}), cap {gil.ItemPowerCap}, sword {Combat.ItemPower(gil, sword):0.#}, camps {m.Camps.Length}");
        Check(!s.Players[0].Peer.Events.Any(e => Code(e) is (int)EventCodes.StartArenaMatchInfos or (int)EventCodes.MatchNewCombatRound),
              "no match HUD events (Arena:MatchHud false)");
        s.Tick(s.World.Config.Arena.CountdownSeconds + 1);
        Check(m.State == ArenaState.Fight, $"the fight ({m.State})");

        // A channel claim on the orb farthest from both camps: −1 for the other team.
        var orb = m.Orbs.MaxBy(o => Math.Min(Vector2.Distance(o.Position, m.Camps[0]), Vector2.Distance(o.Position, m.Camps[1])))!;
        lock (s.World.Sync) gil.Teleport(orb.Position, 0, DateTime.UtcNow);
        string? refused;
        lock (s.World.Sync) refused = s.World.Arenas.ClaimStart(gil, orb.Id);
        s.Tick(9);
        Check(refused is null && orb.Owner == team && m.Tickets[1 - team] == 149 && m.Tickets[team] == 150,
              $"the claim ({refused}): owner {orb.Owner}, tickets {m.Tickets[team]}/{m.Tickets[1 - team]}");

        // A kill costs the victim's team 6 (MATCH_CRYSTAL_GVG_NONLETHAL ticketreductionbydeath), the respawn 18 s later.
        var enemy = s.Players.Select(x => x.Session.Player!).First(p => m.Team[p.Character.Id] != team);
        lock (s.World.Sync) enemy.Teleport(orb.Position + new Vector2(1, 0), 0, DateTime.UtcNow);
        s.Tick(1); // out of its camp's WARCAMP_INVULNERABILITY circle
        lock (s.World.Sync) Pvp.Damage(gil, enemy, 1e9, EffectType.Physical, HitOrigin.Direct, -1, s.World.GameNow);
        Check(m.Tickets[1 - team] == 143 && enemy.Combat.Down is not null && m.RespawnAt.TryGetValue(enemy.Character.Id, out var up)
              && Math.Abs((up - s.World.Now).TotalSeconds - 18) < 0.01,
              $"a kill: 149 → {m.Tickets[1 - team]}, respawn in {(m.RespawnAt.TryGetValue(enemy.Character.Id, out var u) ? (u - s.World.Now).TotalSeconds : -1):0.#} s");

        // 120 s after the fight's start: one orb against none takes 20, and the scoring side's orb turns neutral.
        var since = m.FightSince!.Value;
        s.Tick((since + TimeSpan.FromSeconds(119) - s.World.Now).TotalSeconds);
        Check(m.Tickets[1 - team] == 143 && orb.Owner == team, $"before the cycle: {m.Tickets[1 - team]}, owner {orb.Owner}");
        s.Tick(2);
        Check(m.Tickets[1 - team] == 123 && m.Tickets[team] == 150 && orb.Owner < 0 && m.State == ArenaState.Fight,
              $"the 120 s cycle: the other team 143 → {m.Tickets[1 - team]} (−20 for a difference of 1), the orb neutral (owner {orb.Owner})");

        // The result pays arena.bin's ranked (ARENA_IRON_1) items: the win bag, 8 (premium 12) tomes for a loss.
        lock (s.World.Sync) Check(s.World.Arenas.Finish(gil, team) is null, "the GM ends the match");
        var bags = gil.Inventory.Bag.Items.Select(i => i.Template.Name).ToList();
        var loser = enemy.Inventory.Bag.Items.Where(i => i.Template.Name == "T2_SKILLBOOK_NONTRADABLE").Sum(i => i.Count);
        Check(bags.Any(n => n is "T2_LOOTBAG_ARENA_WIN_BASIC_IRON1" or "T3_LOOTBAG_ARENA_WIN_PREMIUM_IRON1") && loser == (Premium.Is(enemy) ? 12 : 8),
              $"ranked rewards: winner {string.Join(", ", bags)}; loser tomes {loser}");
    }

    private static void Ranks()
    {
        var data = AlbionTests.Client.Clusters!.Arena;
        var iron1 = data.Ranks[0];
        var options = new ArenaOptions();
        Check(data.Ranks.Count == 17 && iron1.Id == "ARENA_IRON_1" && data.Ranks[^1].Id == "ARENA_CRYSTAL" && iron1.Promotion == 150 && data.Ranks[1].Demotion == 100
              && data.CategoryOf(1) is { Name: "ranked", Cluster: "CrystalRealm03", RankPointFactor: 1 } && data.CategoryOf(2) is { Name: "ranked_1v1", RankPointFactor: 0.5 }
              && data.Seasons[ArenaData.SeasonIndex] == "SEASON_34" && data.Template == "RANKED_BRACKETS_ARENA_S26",
              $"arena_europe: {data.Ranks.Count} ranks of {data.Template}, season {data.Seasons[ArenaData.SeasonIndex]}");
        // [H] win: 4 × 10 + elowinbooster 10 (Elo 1210 above expectedelo 800) = 50, the 1v1 × 0.5 = 25; loss: −1 × 10 (no booster above 800).
        Check(Ratings.RankPointChange(iron1, 1, 10, 1210, 1, options) == 50 && Ratings.RankPointChange(iron1, 0.5, 10, 1210, 1, options) == 25
              && Ratings.RankPointChange(iron1, 1, -10, 1190, 0, options) == -10 && Ratings.RankPointChange(iron1, 1, -10, 700, 0, options) == -11
              && Ratings.RankPointChange(iron1, 1, 0, 1200, 0.5, options) == 0 && Ratings.RankPointChange(iron1, 1, 0, 700, 1, options with { RankPointBoosters = false }) == 4,
              "the rank point placeholder: win 50 (1v1 25), loss −10 (−11 below expectedelo), draw 0, min gain 1 × 4");

        using var s = NewSetup(null, ("Ann", "T4_MAIN_SWORD"), ("Bob", "T4_MAIN_SWORD"));
        lock (s.World.Sync) s.World.Ratings.AddRankPoints(s.Players[0].Hero.Id, 2, data, 140);
        var m = Enter(s, Arenas.ModeOfCategory(2)!);
        var (ann, bob) = (s.Players[0].Session.Player!, s.Players[1].Session.Player!); // the sessions of the arena's map
        s.Tick(s.World.Config.Arena.CountdownSeconds + 1);
        lock (s.World.Sync) Pvp.Damage(ann, bob, 1e9, EffectType.Physical, HitOrigin.Direct, -1, s.World.GameNow);
        s.Tick(3 + s.World.Config.Arena.CountdownSeconds + 1);
        lock (s.World.Sync) Pvp.Damage(ann, bob, 1e9, EffectType.Physical, HitOrigin.Direct, -1, s.World.GameNow);
        var annRank = s.World.Ratings.ArenaRank(ann.Character.Id, 2);
        var bobRank = s.World.Ratings.ArenaRank(bob.Character.Id, 2);
        var bag = ann.Inventory.Bag.Items.Select(i => i.Template.Name).ToList();
        Check(m.State == ArenaState.Ended && annRank is { Rank: 1, Points: 165, Peak: 1 } && bobRank is { Rank: 0, Points: 0 }
              && bag.Contains("T4_LOOTBAG_1V1_ARENA_RANK_UP_IRON2") && bag.Any(n => n.Contains("1V1_ARENA_WIN_", StringComparison.Ordinal) && n.EndsWith("_IRON1", StringComparison.Ordinal)),
              $"a 1v1 win ({m.State}, tickets {string.Join("/", m.Tickets)}, round {m.Round}): Ann {annRank} (140 + 25 → IRON_2), Bob {bobRank}; Ann's bag {string.Join(", ", bag)}");

        // GetArenaRankings: the official shape (season 12, both ranked categories, short ranks, int points, short zeros) with Ann's values.
        var (session, peer) = (s.Players[0].Session, s.Players[0].Peer);
        lock (s.World.Sync) session.OnRequest(AlbionTests.Request(new GetArenaRankingsRequest(), 7), encrypted: false);
        var r = peer.Responses[^1];
        Check(r.ReturnCode == 0 && r.Parameters[ReservedKeys.RequestId] is 7 && r.Parameters[ReservedKeys.OperationCode] is (short)OperationCodes.GetArenaRankings
              && r.Parameters[0] is (short)12 && r.Parameters[1] is int[] and [1, 2] && r.Parameters[2] is short[] and [0, 1]
              && r.Parameters[3] is int[] and [0, 165] && r.Parameters[4] is short[] and [0, 0],
              $"GetArenaRankings: {string.Join(" ", r.Parameters.Where(p => p.Key < 5).Select(p => $"{p.Key}={(p.Value is Array a ? string.Join(",", a.Cast<object>()) : p.Value)}"))}");

        // Saved at once (schema v42 arena_ranks); a v41 save gets the table.
        Check(s.Characters.LoadRanks().Any(x => x.Character == ann.Character.Id && x is { Category: 2, Rank: 1, Points: 165, Peak: 1, Season: 12 }), "Ann's rank saved");
        var dir = AlbionTests.NewDir();
        var path = Path.Combine(dir, "save.db");
        try
        {
            using (var fresh = new SqliteCharacterRepository(path)) fresh.SaveRanks([new RankRow(Guid.NewGuid(), 1, 12, 3, 470, 3, 1)]);
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DROP TABLE arena_ranks; PRAGMA user_version=41;";
                cmd.ExecuteNonQuery();
            }
            using (var migrated = new SqliteCharacterRepository(path))
            {
                Check(migrated.LoadRanks().Count == 0, "v41 save migrated: arena_ranks added (empty)");
                migrated.SaveRanks([new RankRow(ann.Character.Id, 1, 12, 2, 320, 2, 5)]);
                Check(migrated.LoadRanks() is [{ Rank: 2, Points: 320, Category: 1 }], "and keeps a rank");
            }
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version";
                Check(Convert.ToInt64(cmd.ExecuteScalar()) == 42, "schema version 42 (v42 arena_ranks)");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
