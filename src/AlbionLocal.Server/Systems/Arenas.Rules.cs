using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The official arena rules (stageP league-pvp SPEC §2.5, §3.3, phase P4; analysis/work/stageP/league-pvp/SPEC.md):
/// <list type="bullet">
/// <item><b>Item power caps</b> (Arena:ItemPowerCap): a member's every item counts under its mode's cap while it is in the match's map
/// (<see cref="ItemPowerCapOf"/>; <see cref="Player.ItemPowerCap"/> feeds the bars, the armour and <see cref="Combat.ItemPower"/>).</item>
/// <item><b>The double-healer penalty</b> (Arena:DoubleHealerPenalty): the matchtype's doublehealerpenalty spell on every member of a team
/// with two or more healers, again every 5 s while it lasts (10 s each).</item>
/// <item><b>Entry buffs</b> (Arena:EnterBuffs): the map's cluster type's ARENA_* spawn buffs, cast whole on the arrival
/// (<see cref="SpawnBuffs.Give"/> with run).</item>
/// <item><b>The camps' defense</b> (Arena:WarcampDefense): the matchtype's &lt;warcamp&gt;&lt;defense&gt; around the two warcamp tiles: the
/// buff spell on the camp's own standing team within buffradius (taken off outside it; once broken by attacking it comes back only after
/// leaving the circle), and in the fight the turret's volleys every attackinterval s on the other team within engageattackrange: attackdamage
/// × the maximum health, lethal (a knockdown and a ticket) when islethal. No NewWarCampObject (its fields are not decoded): the turret has no
/// beam on the client [infer: the official one shows its FX], the hit is a HealthUpdate caused by the target itself, as GM damage.</item>
/// <item><b>Personal season points</b> (Arena:Psp): the mode's arena.bin pspreward at the result (<see cref="Seasons.Gain"/>).</item>
/// </list>
/// </summary>
public sealed partial class Arenas
{
    private MatchTypeData? _matchTypes;
    private bool _matchTypesFailed;

    /// <summary>The matchtypes.xml rules of <paramref name="m"/>'s mode; null without the file (logged once).</summary>
    public MatchTypeData.MatchType? MatchTypeOf(ArenaMatch m)
    {
        if (_matchTypes is null && !_matchTypesFailed)
            try { _matchTypes = world.Clusters?.MatchTypes; }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                _matchTypesFailed = true;
                Log.Warn("arena", $"matchtypes.bin: {e.Message}; no camp defense, double-healer penalty or orbs");
            }
        return _matchTypes?.Find(m.Mode.MatchType);
    }

    /// <summary>arena_europe.bin's categories and ranks (<see cref="ArenaData"/>); null without the file (logged once).</summary>
    public ArenaData? ArenaFile()
    {
        if (_arenaData is null && !_arenaDataFailed)
            try { _arenaData = world.Clusters?.Arena; }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                _arenaDataFailed = true;
                Log.Warn("arena", $"arena.bin: {e.Message}; the ranked modes pay the built-in first-rank figures");
            }
        return _arenaData;
    }

    private ArenaData? _arenaData;
    private bool _arenaDataFailed;

    /// <summary>
    /// <paramref name="m"/>'s starting tickets: 2 rounds in the 1v1; the matchtype's ticketdefault for a mode that takes its figures from it
    /// (the Crystal Arena); else Arena:Tickets5v5 / Tickets2v2.
    /// </summary>
    private int TicketsOf(ArenaMatch m) =>
        m.Mode.Rounds ? 2
        : m.Mode.MatchTypeFigures is { } f ? MatchTypeOf(m) is { Tickets: > 0 } t ? t.Tickets : f.Tickets
        : m.Mode.TeamSize >= 5 ? Options.Tickets5v5 : Options.Tickets2v2;

    /// <summary>The tickets a death costs in <paramref name="m"/> (the matchtype's ticketreductionbydeath, or Arena:TicketsPerDeath).</summary>
    private int PerDeath(ArenaMatch m) =>
        m.Mode.MatchTypeFigures is { } f ? MatchTypeOf(m) is { TicketsPerDeath: > 0 } t ? t.TicketsPerDeath : f.PerDeath : Options.TicketsPerDeath;

    /// <summary>The respawn delay of <paramref name="m"/> (the matchtype's respawndelay, or Arena:RespawnSeconds).</summary>
    private double RespawnOf(ArenaMatch m) =>
        m.Mode.MatchTypeFigures is { } f ? MatchTypeOf(m) is { RespawnSeconds: > 0 } t ? t.RespawnSeconds : f.Respawn : Options.RespawnSeconds;

    /// <summary>Whether <paramref name="m"/> pays arena.bin's rank-template rewards: the Crystal Arena always, the 1v1 with Arena:RankPoints.</summary>
    private bool RankRewards(ArenaMatch m) => m.Mode.RankedCategory is not null && (m.Mode.Name == Crystal || Options.RankPoints);

    /// <summary>The rank whose rewards <paramref name="p"/> gets in <paramref name="m"/>; null when the mode pays no rank rewards or the file is missing.</summary>
    private ArenaData.Rank? RankFor(Player p, ArenaMatch m) =>
        RankRewards(m) && ArenaFile() is { } data ? data.RankAt(RankIndexOf(p.Character.Id, m)) : null;

    /// <summary>
    /// The rank index whose rewards a member gets: its rank when the match ended (before the result's rank points), with Arena:RankPoints;
    /// else 0 (ARENA_IRON_1).
    /// </summary>
    private int RankIndexOf(Guid character, ArenaMatch m) =>
        m.RewardRank.TryGetValue(character, out var r) ? r
        : Options.RankPoints && world.Ratings.ArenaRank(character, m.Mode.Category) is { } row ? row.Rank : 0;

    /// <summary>
    /// Arena:RankPoints at the result (stageP league-pvp SPEC §3.3, phase P7): each rated member's rank points from its Elo change
    /// (<see cref="Ratings.RankPointChange"/>, [H] the placeholder formula), its new rank, and for a rank above the highest one rewarded this
    /// season the rank-up chests (seasonrankrewards of each rank climbed; an offline member gets them at its next ranked result). Returns the
    /// chat part per member.
    /// </summary>
    private Dictionary<Guid, string> RankPointsAt(ArenaMatch m, IReadOnlyDictionary<Guid, int> eloChanges, int winner)
    {
        var lines = new Dictionary<Guid, string>();
        if (!Options.RankPoints || m.Mode.RankedCategory is not { } category || eloChanges.Count == 0 || ArenaFile() is not { } data) return lines;
        var factor = data.CategoryOf(m.Mode.Category)?.RankPointFactor ?? 1;
        foreach (var (id, delta) in eloChanges)
        {
            var score = winner < 0 ? 0.5 : m.Team.GetValueOrDefault(id, -1) == winner ? 1 : 0;
            var rank = data.RankAt(world.Ratings.ArenaRank(id, m.Mode.Category)?.Rank ?? 0);
            var change = Ratings.RankPointChange(rank, factor, delta, world.Ratings.RatingOf(id, m.Mode.Name), score, Options);
            var (before, after) = world.Ratings.AddRankPoints(id, m.Mode.Category, data, change);
            var text = string.Create(CultureInfo.InvariantCulture, $" Rank {data.RankAt(after.Rank).Id}, {after.Points} RP ({change:+0;-0;0}).");
            if (after.Rank > after.Peak && Online(id) is { } p)
            {
                var items = Enumerable.Range(after.Peak + 1, after.Rank - after.Peak)
                    .SelectMany(i => data.Ranks[i].RankUpOf(category)).Where(l => l.Item.Length > 0).Select(l => (l.Item, l.Count));
                var given = GiveItems(p, items, "the arena rank up");
                world.Ratings.MarkPeak(id, m.Mode.Category, after.Rank);
                world.Saves.MarkChanged(p.Character);
                text += $" Rank up: {(given.Count > 0 ? string.Join(", ", given) : "no chest")}.";
            }
            lines[id] = text;
            Log.Info("arena", $"{m}: '{m.Names.GetValueOrDefault(id)}' {data.RankAt(before.Rank).Id} {before.Points} → {data.RankAt(after.Rank).Id} {after.Points} RP");
        }
        return lines;
    }

    /// <summary>
    /// The rank-template reward lines of <paramref name="p"/>'s result in <paramref name="m"/> (a draw pays the loss lines): the premium
    /// status's items and season points, the ARENA_HEAL lines for a healer only; null without rank rewards.
    /// </summary>
    private List<ArenaData.Reward>? RankedLines(Player p, bool won, ArenaMatch m)
    {
        if (RankFor(p, m) is not { } rank) return null;
        var premium = Premium.Is(p);
        var healer = IsHealer(p);
        var lines = won ? rank.WinOf(m.Mode.RankedCategory!) : rank.LoseOf(m.Mode.RankedCategory!);
        return [.. lines.Where(l => l.For(premium) && (l.Role.Length == 0 || l.Role == "ARENA_HEAL" && healer))];
    }

    /// <summary>The item power cap of <paramref name="p"/> now: its match's mode's while it is a member (or bot) in the match's map; else null.</summary>
    public ItemPowerCap? ItemPowerCapOf(Player p) =>
        Options.ItemPowerCap && _matches.Count > 0 && Find(p.Map?.Cluster) is { } m && (m.Team.ContainsKey(p.Character.Id) || m.Bots.ContainsKey(p))
            ? m.Mode.ItemPowerCap : null;

    /// <summary>
    /// A member's (or bot's) arrival in <paramref name="m"/>'s map: its bars and armour worked out under the cap (RegenerationPlayerComboChanged
    /// when they changed) and, on the first arrival, the ARENA_* entry buffs.
    /// </summary>
    private void Entered(Player p, ArenaMatch m, bool arrived)
    {
        var now = world.GameNow;
        if (Options.ItemPowerCap && p.RecalculateStats(now)) p.SendAround(Combat.PlayerCombo(p, now));
        if (!arrived || !Options.EnterBuffs || p.Map?.Template is not { SpawnBuffs.Count: > 0 } t) return;
        var given = SpawnBuffs.Give(p, t.SpawnBuffs, c => c == "isclusterchange", n => n.StartsWith("ARENA_", StringComparison.Ordinal), run: true);
        if (given.Count > 0) Log.Debug("arena", $"{m}: '{p.Character.Name}' enters with {string.Join(", ", given)}");
    }

    /// <summary>The standing and fallen members and bots of team <paramref name="team"/> in <paramref name="map"/>.</summary>
    private static IEnumerable<Player> Members(ArenaMatch m, Map map, int team) =>
        map.Players.Values.Where(p => m.Team.TryGetValue(p.Character.Id, out var t) && t == team && !m.Left.Contains(p.Character.Id))
            .Concat(m.Bots.Where(b => b.Value == team && b.Key.Map == map).Select(b => b.Key));

    /// <summary>itemroles ARENA_HEAL's weapons: a holy or nature staff in the main hand.</summary>
    public static bool IsHealer(Player p) =>
        p.Inventory.Equipment[0]?.Template.Attrs.GetValueOrDefault("shopsubcategory1") is "holystaff" or "naturestaff";

    /// <summary>A countdown's or fight's tick of the stageP rules: the double-healer check once a second, the camps' defense.</summary>
    private void Rules(ArenaMatch m, TimeSpan now)
    {
        if (world.Maps.Find(m.Id) is not { } map) return;
        if (Options.DoubleHealerPenalty && now >= m.HealersDue)
        {
            m.HealersDue = now + TimeSpan.FromSeconds(1);
            Healers(m, map, now);
        }
        if (Options.WarcampDefense && m.Camps.Length == 2 && MatchTypeOf(m)?.Defense is { } d) Defend(m, map, d, now);
    }

    /// <summary>The double-healer penalty (see the class): on each member of a team with ≥ 2 healers, again every 5 s.</summary>
    private void Healers(ArenaMatch m, Map map, TimeSpan now)
    {
        var spell = MatchTypeOf(m) is { } type ? type.DoubleHealerPenalty : "DOUBLEHEALER_PENALTY";
        if (spell.Length == 0 || world.Spells?.Template(spell) is null) return;
        for (var t = 0; t < 2; t++)
        {
            var members = Members(m, map, t).ToList();
            if (members.Count(IsHealer) < 2) continue;
            foreach (var p in members)
            {
                if (m.Penalized.TryGetValue(p, out var at) && now - at < TimeSpan.FromSeconds(5)) continue;
                m.Penalized[p] = now;
                Pvp.ApplySpell(p, spell, world.GameNow);
            }
        }
    }

    /// <summary>The camps' defense (see the class): the friendly buff in the circle, the turret's volleys in the fight.</summary>
    private void Defend(ArenaMatch m, Map map, MatchTypeData.WarcampDefense d, TimeSpan now)
    {
        var ticks = world.GameNow;
        var clock = DateTime.UtcNow;
        if (world.Spells?.Template(d.BuffSpell) is { } buff)
            for (var t = 0; t < 2; t++)
                foreach (var p in Members(m, map, t).ToList())
                {
                    var inside = p.Combat.Down is null && Vector2.Distance(p.PositionAt(clock), m.Camps[t]) <= d.BuffRadius;
                    var entry = p.Effects.Find(buff.Id, p.Id);
                    if (inside && entry is null) Pvp.ApplySpell(p, d.BuffSpell, ticks);
                    else if (!inside && entry is not null && SpellEffects.Remove(world, p, s => s.Spell.Id == buff.Id && s.Caster == p.Id))
                    {
                        SpellEffects.Send(world, p);
                        SpellEffects.Refresh(world, p);
                    }
                }
        if (m.State != ArenaState.Fight || d.Damage <= 0 || d.EngageRange <= 0) return;
        var interval = TimeSpan.FromSeconds(Math.Max(0.05, d.Interval));
        if (m.TurretDue == default) m.TurretDue = now;
        if (now < m.TurretDue) return;
        var volleys = Math.Min(5, (int)((now - m.TurretDue).Ticks / interval.Ticks) + 1);
        m.TurretDue += interval * volleys;
        for (var t = 0; t < 2; t++)
            foreach (var enemy in Members(m, map, 1 - t).ToList())
                if (enemy.Map == map && enemy.Combat.Down is null && !SpellEffects.Invincible(enemy, ticks)
                    && Vector2.Distance(enemy.PositionAt(clock), m.Camps[t]) <= d.EngageRange)
                    TurretHit(m, enemy, t, d.Damage * volleys, d.Lethal, ticks);
    }

    /// <summary>A turret volley of team <paramref name="camp"/>'s camp on <paramref name="target"/>: <paramref name="share"/> of its maximum health.</summary>
    private void TurretHit(ArenaMatch m, Player target, int camp, double share, bool lethal, long ticks)
    {
        var health = target.Health.At(ticks);
        if (health <= 0 || m.State != ArenaState.Fight) return;
        var left = Math.Max(lethal ? 0 : 1, health - Combat.Rounded(target.Health.Max * share));
        if (left >= health) return;
        target.Health.Set(left, Math.Max(ticks, target.Health.Stamp));
        var update = Combat.HealthUpdate(target.Id, ticks, left - health, left, EffectType.Physical, HitOrigin.Direct, target.Id, -1);
        SpellEffects.Break(world, target, "damage", ticks);
        Damaged(target);
        var name = camp == 0 ? "the blue camp" : "the red camp";
        if (left > 0) target.SendAround(update);
        else if (target.IsBot) BotDown(name, target, update);
        else Down(target.Id, name, target, update, ticks);
    }

    /// <summary>Arena:Psp: the mode's personal season points for <paramref name="p"/> (a draw pays the loss figure); the chat part.</summary>
    private string SeasonPoints(Player p, bool won, ArenaMatch m)
    {
        if (!Options.Psp) return "";
        var premium = Premium.Is(p);
        var (win, winPremium, loss, lossPremium) = m.Mode.Psp;
        var points = RankedLines(p, won, m) is { } lines ? lines.Sum(l => l.Psp) : won ? premium ? winPremium : win : premium ? lossPremium : loss;
        if (points <= 0 || Seasons.Current(world, out _) is null) return "";
        Seasons.Gain(p, points * 10000L);
        return string.Create(CultureInfo.InvariantCulture, $", {points} season points");
    }
}
