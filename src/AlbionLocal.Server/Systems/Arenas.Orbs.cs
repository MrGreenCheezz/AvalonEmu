using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>A player's orb channel (ClaimOrbStart): the orb, where the player stood, and when it ends.</summary>
public sealed record OrbChannel(ArenaOrb Orb, Vector2 From, TimeSpan Due, long StartTicks, long DueTicks);

/// <summary>
/// The arena's orbs ("runestones") and their ticket loss (stageP league-pvp SPEC §1.2, §3.1, §3.2, phase P5; Arena:Orbs):
/// <list type="bullet">
/// <item>At the first countdown each &lt;orb type&gt; tile of the map stands as an <see cref="ArenaOrb"/> (NewOrbObject 111), of the tile's type
/// or the matchtype's orbtype when it names one; none in the 1v1 (its map has no orb tile).</item>
/// <item>A zone orb (zonecapture, ORB_ARENA): from orbcooldowntimeonstart (60 s) after the fight's start, a team alone within orbzoneradius (9 m)
/// with a standing member claims it in orbclaimtime (10 s); an orb of the other team takes orbunclaimtime (4 s) more first, after which it is
/// neutral (OrbReset 133); both teams there hold the progress; a claim under way with nobody near drops after orbnoteamunclaimtime (20 s).</item>
/// <item>A channel orb (channelcapture, the crystal types): a player's ClaimOrbStart (op 130 {1 the orb}) within capturerange + 1 m starts a
/// orbclaimtime channel (ClaimOrbStart 128 {0 player, 1 start, 2 end, 3 orb}); damage, moving more than half a metre, a knockdown or ClaimOrbCancel
/// (op 131) end it (ClaimOrbCancel 130 {0 player, 1 0}); at its end ClaimOrbFinished (129 {0 player, 1 orb}) and the claim.</item>
/// <item>A claim: OrbClaimed (132 {0 orb, 1 the team's Guid, 2 now, 3 false}); the other team loses ticketreductionorbclaim (instantscore 10,
/// roundbasedscore 1); at 0 tickets it has lost.</item>
/// <item>The round score (roundbasedscore, or Arena:OrbRoundLookup for an instant orb): every matchtype roundduration the side holding more orbs
/// takes the ticketreductionlookup of the difference from the other; resetorbs onlyscoringorbs makes the scoring side's orbs neutral, all every orb.
/// Only where a mode's matchtype has such orbs: the Crystal Arena (MATCH_CRYSTAL_GVG_NONLETHAL, 120 s, 20/30/40; stageP P6).</item>
/// <item>The verified native match HUD uses 119/118/121/120 and the round number/start timestamp in 171. Arena:MatchHud still gates the
/// experimental OrbUpdate (131) and MatchEndCombatRound (172) fields.</item>
/// </list>
/// The score lines go to chat as before.
/// </summary>
public sealed partial class Arenas
{
    /// <summary>The orbs of <paramref name="m"/>'s map (Arena:Orbs), at its first countdown.</summary>
    private void PlaceOrbs(ArenaMatch m, Map map)
    {
        if (!Options.Orbs || m.Orbs.Count > 0 || map.Template is not { Orbs.Count: > 0 } t) return;
        var forced = MatchTypeOf(m)?.OrbType ?? "";
        foreach (var spawn in t.Orbs)
        {
            var name = forced.Length > 0 ? forced : spawn.OrbType;
            if (_matchTypes?.Orb(name) is not { } type)
            {
                Combat.LogOnce("arena:orb:" + name, $"arena: orb type '{name}' unknown; the orb is left out");
                continue;
            }
            var orb = new ArenaOrb(world.ObjectIds.Next(), spawn, type);
            map.AddObject(orb);
            m.Orbs.Add(orb);
        }
        if (m.Orbs.Count > 0) Log.Info("arena", $"{m}: {m.Orbs.Count} orb(s) ({string.Join(", ", m.Orbs.Select(o => o.Type.Name).Distinct())})");
    }

    /// <summary>
    /// An orb of orbtypes.xml <paramref name="type"/> put at <paramref name="at"/> on <paramref name="m"/>'s map after its start (the self-test: a
    /// channel orb, which no mode's map has yet); null without the map or the type.
    /// </summary>
    public ArenaOrb? PlaceOrb(ArenaMatch m, string type, Vector2 at)
    {
        _ = MatchTypeOf(m); // reads the tables
        if (world.Maps.Find(m.Id) is not { } map || _matchTypes?.Orb(type) is not { } t) return null;
        var spawn = new OrbSpawn
        {
            Key = new SpawnKey(map.Cluster, "orb", map.CustomSpawns++), Tile = "ARENA_ORB", Position = at, Component = new System.Xml.Linq.XElement("orb"), OrbType = type,
        };
        var orb = new ArenaOrb(world.ObjectIds.Next(), spawn, t);
        map.AddObject(orb);
        m.Orbs.Add(orb);
        return orb;
    }

    /// <summary>When <paramref name="orb"/> of <paramref name="m"/> can first be claimed (the fight's start + orbcooldowntimeonstart); null before the fight.</summary>
    private static TimeSpan? ActiveAt(ArenaMatch m, ArenaOrb orb) => m.FightSince + TimeSpan.FromSeconds(orb.Type.CooldownOnStart);

    private static string TeamName(int team) => team == 0 ? "blue" : "red";

    private void ToMap(ArenaMatch m, AlbionEvent e)
    {
        foreach (var p in InMap(m)) p.Session.Send(e);
    }

    /// <summary>A fight's orb tick: the zone captures, the channels, the round score.</summary>
    private void OrbTick(ArenaMatch m, TimeSpan now)
    {
        if (!Options.Orbs || m.Orbs.Count == 0 || world.Maps.Find(m.Id) is not { } map) return;
        var dt = m.OrbsAt is { } last ? Math.Max(0, (now - last).TotalSeconds) : 0;
        m.OrbsAt = now;
        var clock = DateTime.UtcNow;
        foreach (var orb in m.Orbs.Where(o => !o.Type.Channel))
        {
            if (m.State != ArenaState.Fight) return;
            if (ActiveAt(m, orb) is not { } active || now < active) continue;
            var present = new int[2];
            for (var t = 0; t < 2; t++)
                present[t] = Members(m, map, t).Count(p => p.Combat.Down is null && Vector2.Distance(p.PositionAt(clock), orb.Position) <= orb.Type.Radius);
            if (present[0] > 0 && present[1] > 0)
            {
                orb.LastPresence = now; // contested: the progress holds
                continue;
            }
            if (present[0] == 0 && present[1] == 0)
            {
                if (orb.Claiming >= 0 && now - orb.LastPresence >= TimeSpan.FromSeconds(orb.Type.NoTeamUnclaimSeconds))
                {
                    (orb.Claiming, orb.Progress) = (-1, 0);
                    Hud(m, orb);
                }
                continue;
            }
            var team = present[0] > 0 ? 0 : 1;
            orb.LastPresence = now;
            if (orb.Owner == team)
            {
                if (orb.Claiming >= 0) (orb.Claiming, orb.Progress) = (-1, 0);
                continue;
            }
            if (orb.Claiming != team) (orb.Claiming, orb.Progress) = (team, 0);
            orb.Progress += dt;
            if (orb.Owner >= 0 && orb.Progress >= orb.Type.UnclaimSeconds)
            {
                orb.Progress -= orb.Type.UnclaimSeconds;
                Neutral(m, orb, $"{TeamName(team)} takes it back");
            }
            if (orb.Owner < 0 && orb.Progress >= orb.Type.ClaimSeconds) Claimed(m, orb, team);
            else Hud(m, orb);
        }
        foreach (var (p, c) in m.Channels.ToList())
        {
            if (m.State != ArenaState.Fight) return;
            if (p.Map != map || p.Combat.Down is not null || Vector2.Distance(p.PositionAt(clock), c.From) > 0.5f) CancelClaim(m, p, "moved or fell");
            else if (c.Orb.Owner == m.Team.GetValueOrDefault(p.Character.Id, m.Bots.GetValueOrDefault(p, -1))) CancelClaim(m, p, "already ours");
            else if (now >= c.Due)
            {
                m.Channels.Remove(p);
                ToMap(m, new ClaimOrbFinishedEvent { ddn = p.Id, bx2 = c.Orb.Id });
                if (ObserveParticipant(m, p) is { } result) result.Captures++;
                Claimed(m, c.Orb, TeamOf(m, p));
            }
        }
        if (m.State == ArenaState.Fight) RoundScore(m, now);
    }

    private static int TeamOf(ArenaMatch m, Player p) => m.Team.TryGetValue(p.Character.Id, out var t) ? t : m.Bots.GetValueOrDefault(p, -1);

    /// <summary><paramref name="orb"/> is neutral again (an unclaim or a round's reset): OrbReset.</summary>
    private void Neutral(ArenaMatch m, ArenaOrb orb, string why)
    {
        if (orb.Owner < 0) return;
        orb.Owner = -1;
        orb.OwnerGuid = Guid.Empty;
        ToMap(m, new OrbResetEvent { ddn = orb.Id });
        Hud(m, orb);
        Log.Debug("arena", $"{m}: orb #{orb.Id} neutral ({why})");
    }

    /// <summary>Team <paramref name="team"/> claims <paramref name="orb"/>: OrbClaimed, the other team's ticket loss.</summary>
    private void Claimed(ArenaMatch m, ArenaOrb orb, int team)
    {
        if (team < 0) return;
        (orb.Owner, orb.OwnerGuid, orb.Claiming, orb.Progress) = (team, m.TeamGuids[team], -1, 0);
        ToMap(m, new OrbClaimedEvent { ddn = orb.Id, den = orb.OwnerGuid, deo = world.GameNow, dep = false });
        Hud(m, orb);
        TellAll(m, $"Arena: {TeamName(team)} claims a runestone.");
        Log.Info("arena", $"{m}: team {team} claims orb #{orb.Id} ({orb.Type.Name})");
        if (orb.Type.ClaimReduction > 0) TicketLoss(m, 1 - team, orb.Type.ClaimReduction, "a runestone");
    }

    /// <summary>Team <paramref name="team"/> loses <paramref name="tickets"/> to the orbs: the chat line, MatchUpdate with the HUD; at 0 the other team wins.</summary>
    private void TicketLoss(ArenaMatch m, int team, int tickets, string why)
    {
        if (m.State != ArenaState.Fight || tickets <= 0) return;
        m.Tickets[team] = Math.Max(0, m.Tickets[team] - tickets);
        TellAll(m, string.Create(CultureInfo.InvariantCulture, $"Arena: {TeamName(team)} loses {tickets} tickets to {why} ({Score(m)})."));
        HudScore(m);
        if (m.Tickets[team] == 0) End(m, 1 - team, $"{TeamName(team)} has no tickets left");
    }

    /// <summary>The round score (see the class): due every matchtype roundduration from the fight's start.</summary>
    private void RoundScore(ArenaMatch m, TimeSpan now)
    {
        if (MatchTypeOf(m) is not { RoundSeconds: > 0, Lookup.Count: > 0 } type || m.FightSince is not { } since) return;
        if (!m.Orbs.Any(o => o.Type.Score == MatchTypeData.OrbScore.RoundBased || o.Type.Score == MatchTypeData.OrbScore.Instant && Options.OrbRoundLookup)) return;
        m.RoundAt ??= since + TimeSpan.FromSeconds(type.RoundSeconds);
        if (now < m.RoundAt) return;
        m.RoundAt += TimeSpan.FromSeconds(type.RoundSeconds);
        var owned = new[] { m.Orbs.Count(o => o.Owner == 0), m.Orbs.Count(o => o.Owner == 1) };
        HudRoundEnd(m, owned[0] > owned[1], owned[1] > owned[0]);
        HudRound(m, m.CombatRound + 1);
        if (owned[0] == owned[1]) return;
        var leader = owned[0] > owned[1] ? 0 : 1;
        var reduction = type.ReductionFor(owned[leader] - owned[1 - leader]);
        foreach (var orb in m.Orbs.Where(o => o.Type.Score == MatchTypeData.OrbScore.RoundBased).ToList())
            if (orb.Type.ResetOrbs == "all" || orb.Type.ResetOrbs == "onlyscoringorbs" && orb.Owner == leader) Neutral(m, orb, "the round's reset");
        TicketLoss(m, 1 - leader, reduction, "the runestones held");
    }

    // ---- the channel orbs (op 130, 131) ----

    /// <summary>
    /// ClaimOrbStart (op 130 {1 orb}): <paramref name="p"/>'s channel on a channel orb of its match. Refused (a reason): no fight, not a
    /// member, no such orb, a zone orb (no channel), not yet active, its team's already, too far, knocked down, channelling already.
    /// </summary>
    public string? ClaimStart(Player p, long orbId)
    {
        if (Find(p.Map?.Cluster) is not { State: ArenaState.Fight } m) return "no arena fight";
        var team = TeamOf(m, p);
        if (team < 0) return "not in the match";
        if (m.Orbs.FirstOrDefault(o => o.Id == orbId) is not { } orb) return "no such orb";
        if (!orb.Type.Channel) return "a zone orb (stand in it)";
        if (ActiveAt(m, orb) is not { } active || world.Now < active) return "not active yet";
        if (orb.Owner == team) return "your team's already";
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, orb.Position) > orb.Type.Radius + 1) return "too far";
        if (p.Combat.Down is not null) return "knocked down";
        if (m.Channels.ContainsKey(p)) return "channelling already";
        var start = world.GameNow;
        var seconds = TimeSpan.FromSeconds(orb.Type.ClaimSeconds);
        var c = new OrbChannel(orb, at, world.Now + seconds, start, start + seconds.Ticks);
        m.Channels[p] = c;
        p.SendAround(new ClaimOrbStartEvent { ddn = p.Id, bx3 = c.StartTicks, bx4 = c.DueTicks, bx5 = orb.Id });
        return null;
    }

    /// <summary>ClaimOrbCancel (op 131) or an interruption: <paramref name="p"/>'s channel ends (ClaimOrbCancel 130). False without one.</summary>
    public bool CancelClaim(Player p, string why) => Find(p.Map?.Cluster) is { } m && CancelClaim(m, p, why);

    private bool CancelClaim(ArenaMatch m, Player p, string why)
    {
        if (!m.Channels.Remove(p)) return false;
        p.SendAround(new ClaimOrbCancelEvent { ddn = p.Id, bx1 = 0 });
        Log.Debug("arena", $"{m}: '{p.Character.Name}' orb channel ends ({why})");
        return true;
    }

    /// <summary>A hit on <paramref name="target"/>'s health (Pvp.Damage, the camp's turret): its orb channel breaks.</summary>
    public void Damaged(Player target)
    {
        if (_matches.Count > 0 && Find(target.Map?.Cluster) is { Channels.Count: > 0 } m) CancelClaim(m, target, "damaged");
    }

    // ---- Native HUD; experimental orb details remain behind Arena:MatchHud ----

    /// <summary>
    /// OrbUpdate (131, avx; clp.af copies keys 1-6, 7, 8-10, 11/12, 13/14, 15) with Arena:MatchHud only [infer: 1 the owner's Guid, 2 the claiming
    /// team's, 7 contested, 8 the claim's start, 9 its end, 10 the activation, 13/14 progress and need (a7m), 15 now; R2].
    /// </summary>
    private void Hud(ArenaMatch m, ArenaOrb orb)
    {
        if (!Options.MatchHud) return;
        var now = world.GameNow;
        var need = orb.Owner >= 0 ? orb.Type.UnclaimSeconds + orb.Type.ClaimSeconds : orb.Type.ClaimSeconds;
        var active = ActiveAt(m, orb) is { } at ? now + (at - world.Now).Ticks : now;
        ToMap(m, new OrbUpdateEvent
        {
            ddn = orb.Id, deq = orb.OwnerGuid, der = orb.Claiming >= 0 ? m.TeamGuids[orb.Claiming] : Guid.Empty, dew = false,
            dex = orb.Claiming >= 0 ? now - TimeSpan.FromSeconds(orb.Progress).Ticks : 0,
            dey = orb.Claiming >= 0 ? now + TimeSpan.FromSeconds(Math.Max(0, need - orb.Progress)).Ticks : 0,
            dez = active, de2 = (long)Math.Round(orb.Progress * 10000), de3 = (long)Math.Round(need * 10000), de4 = now,
        });
    }

    /// <summary>
    /// Client 345319: dha.ah B286B0 -> dd3.ah AC0E00, ArenaMatchStatsGui.SetStartMatchInfos 1307190.
    /// Key 10 is the scheduled fight start, key 12 the recipient's team (used to orient its score). Keys 2/3 are world object ids,
    /// not timestamps; instanced arenas have no associated territory objects. Unverified league/modifier fields remain unset.
    /// </summary>
    private bool HasMatchHud(ArenaMatch m) => m.HudStartTicks != 0 && MatchTypeOf(m) is not null
        && ArenaFile()?.CategoryOf(m.Mode.Category) is not null;

    private void HudStart(ArenaMatch m, Player p)
    {
        var ownTeam = m.Team.TryGetValue(p.Character.Id, out var team) ? m.TeamGuids[team] : Guid.Empty;
        p.Session.Send(new StartArenaMatchInfosEvent
        {
            dzd = m.Guid, dze = MatchTypeOf(m)!.Index, dzh = m.TeamGuids[0], dzi = m.TeamGuids[1], dzj = "", dzk = "",
            dzl = [.. m.TeamOf(0).Concat(m.Bots.Where(b => b.Value == 0).Select(b => b.Key.Character.Id))],
            dzm = [.. m.TeamOf(1).Concat(m.Bots.Where(b => b.Value == 1).Select(b => b.Key.Character.Id))],
            dzn = m.HudStartTicks, dzo = m.Mode.Category, dzp = ownTeam, dzq = -1, dzs = ArenaData.SeasonIndex,
        });
    }

    /// <summary>Recreate presentation only; joining an active/result map never repeats rewards or changes match state.</summary>
    private void HudJoined(ArenaMatch m, Player p)
    {
        if (p.IsBot || !HasMatchHud(m)) return;
        HudStart(m, p);
        HudRoster(m, p);
        if (m.FightSince is not null)
        {
            p.Session.Send(FightEvent(m));
            if (m.CombatRound > 0)
                p.Session.Send(new MatchNewCombatRoundEvent { cos = m.Guid, cot = m.HudRoundTicks, cou = m.CombatRound });
        }
        p.Session.Send(ScoreEvent(m));
        if (m.State == ArenaState.Ended) HudEnd(m, p);
    }

    private int MatchSecondsLeft(ArenaMatch m)
    {
        var duration = TimeSpan.FromMinutes(m.Mode.Minutes > 0 ? m.Mode.Minutes : Options.MatchMinutes);
        var left = m.FightSince is { } since ? duration - (world.Now - since) : duration;
        return (int)Math.Clamp(Math.Ceiling(left.TotalSeconds), 0, int.MaxValue);
    }

    /// <summary>118 sets the client's match-started flag (dd3.ah AC0350); 119 alone only opens the countdown.</summary>
    private StartMatchEvent FightEvent(ArenaMatch m) => new()
    {
        dz2 = m.Guid, dz3 = MatchSecondsLeft(m), dz5 = m.Tickets[0], dz6 = m.Tickets[1],
    };

    private void HudFight(ArenaMatch m)
    {
        if (HasMatchHud(m)) ToMap(m, FightEvent(m));
    }

    /// <summary>
    /// 345319 dd3.ao AC2900 copies key 1 to the round-start timestamp and key 2 to the round number. The arena HUD uses a future
    /// round-start timestamp for the next 1v1 countdown (13092A3); round-scored orbs retain their existing cycle number.
    /// </summary>
    private void HudRound(ArenaMatch m, int round, long? starts = null)
    {
        if (!m.Mode.Rounds && !m.Orbs.Any(o => o.Type.Score == MatchTypeData.OrbScore.RoundBased)) return;
        m.CombatRound = round;
        m.HudRoundTicks = starts ?? world.GameNow;
        if (HasMatchHud(m)) ToMap(m, new MatchNewCombatRoundEvent { cos = m.Guid, cot = m.HudRoundTicks, cou = round });
    }

    /// <summary>MatchEndCombatRound (172 {0 match, 1, 2 bool}) with Arena:MatchHud only [infer: 1 team A scored the cycle, 2 team B; capture R3].</summary>
    private void HudRoundEnd(ArenaMatch m, bool first, bool second)
    {
        if (!Options.MatchHud) return;
        ToMap(m, new MatchEndCombatRoundEvent { coj = m.Guid, cok = first, col = second });
    }

    /// <summary>
    /// 345319 dd3.an AC2320 -> ArenaMatchStatsGui.UpdateMatch 1308770: keys 1/2 are remaining seconds converted into local deadlines;
    /// keys 3/4 are the two scores. The second clock and the orb/modifier/cooldown fields have no confirmed authority mapping here.
    /// </summary>
    private MatchUpdateEvent ScoreEvent(ArenaMatch m) => new()
    {
        cqh = m.Guid, cqi = MatchSecondsLeft(m), cqk = m.Tickets[0], cql = m.Tickets[1],
    };

    private void HudScore(ArenaMatch m)
    {
        if (!HasMatchHud(m)) return;
        ToMap(m, ScoreEvent(m));
        HudRoster(m);
    }

    /// <summary>
    /// 345319 ctp.aqw 9A4530 -> dha.ah B28A80/dd3.ai AC1450: winner team and actual end timestamp; the result also hides the HUD
    /// (ArenaMatchStatsGui.OnEndArenaMatch 1306340). AC1110 -> dd4.aa AC38C0 applies name, damage done/received, healing done/received
    /// and whole fame (943B40 multiplies key 7 by 10000). ScoreboardGui.a 140FE90 / UpdateTotals 140BCD0 ->
    /// ScoreboardExtraPageView.SetTotalsTop 1406BF0 confirms the columns. Event 169 first creates the named rows.
    /// 99AC00 accepts reward triples kind 6/count/item-index and kind 24/PSP/0 only. The two ordinary reward lists are concatenated
    /// by RewardsListView 13EC2F0: all paid ordinary receipts use keys 11-13 until their main/daily distinction is established;
    /// keys 17-19 are paid rank-up rewards (CrystalArenaRankedView 12A7F4A). Silver/fame are not supported reward-row kinds here.
    /// Keys 20/21 and 22/23 are actual before/after RP and rank (GetRankPoints 12A8390 / DetermineVisualState 12A81B0).
    /// Key 24 enables the paid rank-up chest visual (12A8292 checks it before the rank-up list). Keys 8/10 retain their prior
    /// defaults: cancellation/daily fields have no confirmed authority mapping.
    /// </summary>
    private void HudEnd(ArenaMatch m, Player p)
    {
        if (!HasMatchHud(m) || m.Winner is not { } winner || m.HudEndTicks == 0 || p.IsBot) return;
        var rank = m.HudRanks.GetValueOrDefault(p.Character.Id);
        var rows = m.Results.Values.OrderBy(r => r.Team).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
        var rewards = m.RewardReceipts.GetValueOrDefault(p.Character.Id) ?? [];
        var rankUp = m.RankUpReceipts.GetValueOrDefault(p.Character.Id) ?? [];
        p.Session.Send(CompleteResults(m, rows));
        p.Session.Send(new EndArenaMatchEvent
        {
            b43 = m.Guid, b44 = winner >= 0 ? m.TeamGuids[winner] : Guid.Empty, b4n = m.HudEndTicks,
            b45 = [.. rows.Select(r => r.Name)], b46 = [.. rows.Select(r => (float)r.DamageDone)], b47 = [.. rows.Select(r => (float)r.DamageReceived)],
            b48 = [.. rows.Select(r => (float)r.HealingDone)], b49 = [.. rows.Select(r => (float)r.HealingReceived)], b5a = [.. rows.Select(r => r.Fame / 10000)],
            b4p = [.. rewards.Select(r => r.Kind)], b4q = [.. rewards.Select(r => r.Amount)], b4r = [.. rewards.Select(r => r.Item)],
            b4s = [], b4t = [], b4u = [],
            b4v = [.. rankUp.Select(r => r.Kind)], b4w = [.. rankUp.Select(r => r.Amount)], b4x = [.. rankUp.Select(r => r.Item)],
            b4y = rank.BeforePoints, b4z = rank.AfterPoints, b40 = rank.BeforeRank, b41 = rank.AfterRank,
            b42 = rankUp.Count > 0,
        });
    }
}
