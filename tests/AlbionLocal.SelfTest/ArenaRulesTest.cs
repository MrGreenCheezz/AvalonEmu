using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageP official arena rules (analysis/work/stageP/league-pvp/SPEC.md §2.5, §3, phases P4 and P5) under the official preset without the
/// ready check: the item power caps, the ARENA_* entry buffs, the double-healer penalty, the season points at the result, the orbs.
/// </summary>
public static class ArenaRulesTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("arena P4: 1v1 soft cap 900 (× 0.2 above), ARENA_1V1_ENTER and ARENA_OVERLOAD_PROTECTION on arrival, 1v1 season points at the result", OneVsOne),
        ("arena P4: 2v2 hard cap 600, DOUBLEHEALER_PENALTY (heal × 0.65) on the team with two holy staffs only, ARENA_WARCAMP_INVULNERABILITY in the camp", TwoVsTwo),
        ("arena P5: ARENA-STANDARD's 3 ORB_ARENA (NewOrbObject); none claimable in the first 60 s; 10 s alone in the zone claims it (OrbClaimed), the enemy loses 10 tickets", ZoneOrb),
        ("arena P5: a channel orb's ClaimOrbStart, a hit cancels it (ClaimOrbCancel); a quiet 7.5 s channel claims it (ClaimOrbFinished, −1 ticket)", ChannelOrb),
    ];

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static int Count(FakePeer peer, int from, EventCodes code) => peer.Events.Skip(from).Count(e => Code(e) == (int)code);

    /// <summary>A 2v2 of four sword fighters, in its fight (the countdown over).</summary>
    private static (Setup S, ArenaMatch M) Fighting()
    {
        var s = NewSetup(null, ("Gil", "T4_MAIN_SWORD"), ("Hal", "T4_MAIN_SWORD"), ("Ida", "T4_MAIN_SWORD"), ("Jo", "T4_MAIN_SWORD"));
        var m = Enter(s, Arenas.ModeOf("2v2")!);
        s.Tick(s.World.Config.Arena.CountdownSeconds + 1);
        Check(m.State == ArenaState.Fight, $"the fight is on ({m.State})");
        return (s, m);
    }

    private static void ZoneOrb()
    {
        var (s, m) = Fighting();
        using var _ = s;
        var gil = s.Players[0].Session.Player!;
        var team = m.Team[gil.Character.Id];
        // The orb farthest from both camps, so neither turret reaches Gil there; it comes into his view there (NewOrbObject {0 id, 1 type, 2 tile}).
        var orb = m.Orbs.MaxBy(o => Math.Min(System.Numerics.Vector2.Distance(o.Position, m.Camps[0]), System.Numerics.Vector2.Distance(o.Position, m.Camps[1])))!;
        var seen = s.Players[0].Peer.Events.Count;
        lock (s.World.Sync) gil.Teleport(orb.Position, 0, DateTime.UtcNow);
        var shown = s.Players[0].Peer.Events.Skip(seen).FirstOrDefault(e => Code(e) == (int)EventCodes.NewOrbObject && Convert.ToInt64(e.Parameters[0]) == orb.Id);
        Check(m.Orbs.Count == 3 && m.Orbs.All(o => o.Type.Name == "ORB_ARENA" && o.Owner < 0) && shown?.Parameters[1] is "ORB_ARENA" && shown.Parameters[2] is "ARENA_ORB",
              $"three neutral ORB_ARENA orbs ({m.Orbs.Count}); NewOrbObject when Gil comes near one ({shown is not null})");
        s.Tick(30);
        Check(orb.Owner < 0 && m.Tickets[1 - team] == 150, $"30 s into the fight: not claimable yet (owner {orb.Owner}, tickets {m.Tickets[1 - team]})");
        var mark = s.Players[0].Peer.Events.Count;
        s.Tick(30 + 10 + 1);
        Check(orb.Owner == team && m.Tickets[1 - team] == 140 && m.Tickets[team] == 150 && Count(s.Players[0].Peer, mark, EventCodes.OrbClaimed) == 1
              && Count(s.Players[0].Peer, 0, EventCodes.OrbUpdate) == 0 && Count(s.Players[0].Peer, 0, EventCodes.MatchUpdate) == 0,
              $"after 60 s + 10 s alone in the zone: claimed by Gil's team (owner {orb.Owner}), the other team 150 → {m.Tickets[1 - team]}, OrbClaimed; no HUD events (Arena:MatchHud false)");
    }

    private static void ChannelOrb()
    {
        var (s, m) = Fighting();
        using var _ = s;
        var (gil, hal) = (s.Players[0].Session.Player!, s.Players[1].Session.Player!);
        var team = m.Team[gil.Character.Id];
        var at = m.Orbs.MaxBy(o => Math.Min(System.Numerics.Vector2.Distance(o.Position, m.Camps[0]), System.Numerics.Vector2.Distance(o.Position, m.Camps[1])))!.Position
            + new System.Numerics.Vector2(0, 12); // out of the zone orb's 9 m
        ArenaOrb? orb;
        lock (s.World.Sync) orb = s.World.Arenas.PlaceOrb(m, "ORB_CRYSTAL_GVG_WINNER_RESET_INDIVIDUAL_CHANNEL", at);
        Check(orb is { Type.Channel: true, Type.ClaimSeconds: 7.5 }, $"a channel orb placed ({orb?.Type})");
        lock (s.World.Sync) gil.Teleport(at, 0, DateTime.UtcNow);
        s.Tick(61);
        var mark = s.Players[0].Peer.Events.Count;
        string? refused;
        lock (s.World.Sync) refused = s.World.Arenas.ClaimStart(gil, orb!.Id);
        var enemy = new[] { gil, hal }.Concat(s.Players.Skip(2).Select(x => x.Session.Player!)).First(p => m.Team[p.Character.Id] != team);
        lock (s.World.Sync)
        {
            enemy.Teleport(at + new System.Numerics.Vector2(1, 0), 0, DateTime.UtcNow);
            Pvp.Damage(enemy, gil, 50, EffectType.Physical, HitOrigin.Direct, -1, s.World.GameNow);
        }
        Check(refused is null && Count(s.Players[0].Peer, mark, EventCodes.ClaimOrbStart) == 1 && Count(s.Players[0].Peer, mark, EventCodes.ClaimOrbCancel) == 1
              && !m.Channels.ContainsKey(gil),
              $"ClaimOrbStart accepted ({refused}), a hit cancels the channel (ClaimOrbCancel)");
        lock (s.World.Sync) enemy.Teleport(m.Camps[1 - team], 0, DateTime.UtcNow);
        lock (s.World.Sync) refused = s.World.Arenas.ClaimStart(gil, orb.Id);
        s.Tick(9);
        Check(refused is null && orb.Owner == team && m.Tickets[1 - team] == 149 && Count(s.Players[0].Peer, mark, EventCodes.ClaimOrbFinished) == 1,
              $"a quiet channel claims it: owner {orb.Owner}, the other team {m.Tickets[1 - team]} (−1, roundbasedscore)");
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static ClusterStore Store => Client.Clusters!;

    internal sealed class Setup : IDisposable
    {
        public required World World { get; init; }
        public required ManualClock Clock { get; init; }
        public required string Dir { get; init; }
        public required SqliteCharacterRepository Characters { get; init; }
        public List<(WorldSession Session, FakePeer Peer, Character Hero)> Players { get; } = [];

        public void Tick(double seconds)
        {
            for (var i = 0; i < Math.Ceiling(seconds); i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(Math.Min(1, seconds - i)));
                lock (World.Sync) World.Update();
            }
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    /// <summary>A world of the official preset (no ready check), the heroes in 0000 each with its main hand.</summary>
    internal static Setup NewSetup(Func<ArenaOptions, ArenaOptions>? arena = null, params (string Name, string Weapon)[] heroes)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var options = new ArenaOptions { ReadyCheck = false };
        var config = new ServerConfig { JoinByCharacterId = true, Arena = arena?.Invoke(options) ?? options };
        var world = new World(config, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = 3, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        _ = Store.Get("0000");
        _ = Store.Seasons;
        var s = new Setup { World = world, Clock = clock, Dir = dir, Characters = characters };
        var x = 5f;
        foreach (var (name, weapon) in heroes)
        {
            var hero = StarterKit.Default.NewCharacter(name);
            (hero.Cluster, hero.PositionX, hero.PositionY) = ("0000", x, 5);
            x += 3;
            hero.Equipment[0] = new InventoryItem(weapon);
            Check(characters.Create(hero), $"character {name} created");
            var (session, peer) = Join(world, hero.Id);
            s.Players.Add((session, peer, hero));
        }
        return s;
    }

    internal static (WorldSession Session, FakePeer Peer) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        return (session, peer);
    }

    /// <summary>Every hero registers for <paramref name="category"/> (or the 2v2 by .arena join's path), the match starts, all join its map.</summary>
    internal static ArenaMatch Enter(Setup s, ArenaMode mode)
    {
        _ = Store.Get(mode.Cluster);
        foreach (var (session, _, _) in s.Players)
            lock (s.World.Sync) Check(s.World.Arenas.Register(session.Player!, mode, group: false) is null, $"{session.Player!.Character.Name} queued for {mode}");
        for (var i = 0; i < 20 && s.World.Arenas.Matches.Count == 0; i++)
        {
            s.Tick(1);
            if (s.World.Arenas.Matches.Count == 0) Thread.Sleep(50); // the map is read off the world thread
        }
        var m = s.World.Arenas.Matches.SingleOrDefault();
        Check(m is { State: ArenaState.Arriving }, $"a {mode} match started ({m?.State})");
        for (var i = 0; i < s.Players.Count; i++)
        {
            var (session, peer) = Join(s.World, s.Players[i].Hero.Id);
            s.Players[i] = (session, peer, s.Players[i].Hero);
        }
        Check(m!.State == ArenaState.Countdown, $"all arrived: the countdown ({m.State})");
        return m;
    }

    private static bool Has(Player p, string spell) => p.Effects.Spells.Any(s => s.Spell.Name == spell && s.Live(p.World.GameNow));

    private static double RawIp(Player p, Item item) => p.World.StatCalculator!.ItemPower(item.ForStats(), p.Progression?.Levels ?? new SortedDictionary<int, int>());

    private static void OneVsOne()
    {
        using var s = NewSetup(null, ("Ann", "T8_MAIN_SWORD@3"), ("Bob", "T4_MAIN_SWORD"));
        var m = Enter(s, Arenas.ModeOf("1v1")!);
        var ann = s.Players[0].Session.Player!;
        var bob = s.Players[1].Session.Player!;
        var sword = ann.Inventory.Equipment[0]!;
        var raw = RawIp(ann, sword);
        var capped = Combat.ItemPower(ann, sword);
        Check(raw > 900 && Math.Abs(capped - (900 + (raw - 900) * 0.2)) < 0.01 && ann.ItemPowerCap is { Cap: 900, Factor: 0.2 },
              $"1v1 soft cap: item power {raw:0.#} counts {capped:0.#} (900 + 20 % above)");
        Check(Has(ann, "ARENA_1V1_ENTER") && Has(ann, "ARENA_OVERLOAD_PROTECTION") && Has(bob, "ARENA_1V1_ENTER"),
              $"entry buffs on arrival: {string.Join(", ", ann.Effects.Spells.Select(e => e.Spell.Name))}");

        // Ann wins two rounds: 1v1 season points 400 (500 premium) for her, 175 (250) for Bob.
        s.Tick(s.World.Config.Arena.CountdownSeconds + 1);
        var mark = s.Players[0].Peer.Events.Count;
        var markBob = s.Players[1].Peer.Events.Count;
        lock (s.World.Sync) Pvp.Damage(ann, bob, 1e9, EffectType.Physical, HitOrigin.Direct, -1, s.World.GameNow);
        s.Tick(3 + s.World.Config.Arena.CountdownSeconds + 1);
        lock (s.World.Sync) Pvp.Damage(ann, bob, 1e9, EffectType.Physical, HitOrigin.Direct, -1, s.World.GameNow);
        long Gained(FakePeer peer, int from) => peer.Events.Skip(from).Where(e => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode)) == (int)EventCodes.PersonalSeasonPointsGained)
            .Select(e => Convert.ToInt64(e.Parameters[0])).Sum();
        var annPoints = Gained(s.Players[0].Peer, mark);
        var bobPoints = Gained(s.Players[1].Peer, markBob);
        Check(m.State == ArenaState.Ended && annPoints == (Premium.Is(ann) ? 500 : 400) * 10000L && bobPoints == (Premium.Is(bob) ? 250 : 175) * 10000L,
              $"season points at the result: Ann {annPoints / 10000}, Bob {bobPoints / 10000}");
        Check(ann.ItemPowerCap is not null, "still capped in the arena after the result");
    }

    private static void TwoVsTwo()
    {
        // Registration order Cid, Eve, Dee, Fay: Cid and Dee (the holy staffs) on team 0, Eve and Fay on team 1.
        using var s = NewSetup(null, ("Cid", "T4_MAIN_HOLYSTAFF"), ("Eve", "T8_MAIN_SWORD@3"), ("Dee", "T4_MAIN_HOLYSTAFF"), ("Fay", "T4_MAIN_SWORD"));
        var m = Enter(s, Arenas.ModeOf("2v2")!);
        var (cid, eve, dee, fay) = (s.Players[0].Session.Player!, s.Players[1].Session.Player!, s.Players[2].Session.Player!, s.Players[3].Session.Player!);
        Check(m.Team[cid.Character.Id] == m.Team[dee.Character.Id] && m.Team[eve.Character.Id] != m.Team[cid.Character.Id], "the two healers share a team");
        var sword = eve.Inventory.Equipment[0]!;
        Check(RawIp(eve, sword) > 600 && Math.Abs(Combat.ItemPower(eve, sword) - 600) < 0.01, $"2v2 hard cap: {RawIp(eve, sword):0.#} counts {Combat.ItemPower(eve, sword):0.#}");
        s.Tick(1);
        var now = s.World.GameNow;
        Check(Has(cid, "DOUBLEHEALER_PENALTY") && Has(dee, "DOUBLEHEALER_PENALTY") && !Has(eve, "DOUBLEHEALER_PENALTY") && !Has(fay, "DOUBLEHEALER_PENALTY")
              && Math.Abs(SpellEffects.HealFactor(cid, now) - 0.65) < 0.01,
              $"double-healer penalty on team 0 only (heal factor {SpellEffects.HealFactor(cid, now):0.###})");
        // The countdown's spots are the camps' spawn positions: whoever stands within 8.7 m of its warcamp tile has the camp's shield.
        var inCamp = new[] { cid, eve, dee, fay }.Where(p => System.Numerics.Vector2.Distance(p.PositionAt(DateTime.UtcNow), m.Camps[m.Team[p.Character.Id]]) <= 8.7f).ToList();
        Check(inCamp.Count > 0 && inCamp.All(p => Has(p, "ARENA_WARCAMP_INVULNERABILITY")) && new[] { cid, eve, dee, fay }.Except(inCamp).All(p => !Has(p, "ARENA_WARCAMP_INVULNERABILITY")),
              $"ARENA_WARCAMP_INVULNERABILITY on the {inCamp.Count} member(s) inside their camp's circle");
    }
}
