using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage 5a (analysis/work/stage5/SPEC.md §5a.5): the mobs of 0204 from mobs.xml and randomspawnbehaviors.xml, NewMob
/// with the captured keys (stage5/mobs/newmob_cap.txt: 2306, 2757, 2724, 448), visibility with Leave and re-entry,
/// MobChangeState, the GM commands and the respawn of a spawn point, on a <see cref="ManualClock"/> with FakePeers.
/// </summary>
public static class MobSpawnTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("mobs: mob ids (index + 16), 0204's spawn points (149 toads, 2 guard towers, RANDOM blocks of the cluster), the prefab points", Data),
        ("mobs on the map: NewMob keys as captured (2306, 2757, 2724, 448), Leave and re-entry, MobChangeState, .mob spawn/kill, respawn, .near", OnTheMap),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Data()
    {
        var mobs = Store.Mobs;
        var ids = new (string Name, short Id)[]
        {
            ("MOB_RABBIT", 424), ("T1_MOB_HIDE_SWAMP_TOAD", 448), ("T4_MOB_ROAMING_UNDEAD_GHOUL", 1888), ("T4_MOB_UNDEAD_SOLDIER_VETERAN", 2306),
            ("T4_MOB_CITY_SWAMP_KNIGHT_ELITE", 2724), ("T6_MOB_GUARD_TOWER", 2757),
        };
        Check(ids.All(x => mobs.Find(x.Name)?.Id == x.Id && mobs[x.Id]?.Name == x.Name) && mobs[15] is null && mobs[MobTemplate.FirstId + mobs.Count] is null,
              $"mob type = mobs.xml index + 16, as the captured types ({string.Join(", ", ids.Select(x => $"{x.Name} {mobs.Find(x.Name)?.Id}"))})");
        var soldier = mobs.Find("T4_MOB_UNDEAD_SOLDIER_VETERAN")!;
        Check(soldier is { HitPointsMax: 5118, HitPointsRegeneration: 33, EnergyMax: 157, EnergyRegeneration: 5, MoveSpeed: 1.5f, AttackMoveSpeed: 6.5f,
                  Hostility: NpcHostility.Hostile, Faction: "UNDEAD", AttackSpeed: 0.4f, AggroRadius: 8, PursuitRadius: 40, PassivePursuitRadius: 40, Armor: 121,
                  RespawnSecondsMin: 1013, RespawnSecondsMax: 1688, PostDeathLifetime: 5, EnergyReward: 34, MinDivisor: 3.2, AI: not null, Loot: not null }
              && mobs.Find("T6_MOB_GUARD_TOWER") is { Hostility: NpcHostility.Guard, MoveSpeed: 0, RoamingRadius: 0 }
              && mobs.Find("T1_MOB_HIDE_SWAMP_TOAD") is { PassivePursuitRadius: 10, AttackType: "none", LootPrefab: "T0_LOOTBAG_PREMIUM" },
              "mobs.xml values and the client's defaults (passivepursuitradius = pursuitradius, lootprefab T0_LOOTBAG_PREMIUM)");

        var t = Store.Get("0204");
        var points = Store.Resolve("0204").MobSpawnPoints; // the default templates (the map's day rolls dynamic ones: 462 points on 10-14)
        int Named(string name, short id) => points.Count(p => p.Mob == name && p.Template?.Id == id);
        var random = points.Where(p => p.MobName == MobSpawnPoint.Random).ToList();
        Check(points.Count == 387 && Named("T1_MOB_HIDE_SWAMP_TOAD", 448) == 149 && Named("T6_MOB_GUARD_TOWER", 2757) == 2 && random.Count == 236,
              $"0204: 387 points of its own, 149 toads (448), 2 guard towers (2757), 236 RANDOM (got {points.Count}, {Named("T1_MOB_HIDE_SWAMP_TOAD", 448)}, "
              + $"{Named("T6_MOB_GUARD_TOWER", 2757)}, {random.Count})");
        var rng = new Random(1);
        Check(random.All(p => p.Block is { } b && Enumerable.Range(0, 20).All(_ => b.Pick(rng) is { } m && mobs.Find(m.Name) == m)),
              "every RANDOM point has a block of its behaviour and picks mobs of mobs.xml");
        var soldiers = random.Where(p => p.SpawnBehavior == "STATIC_UNDEAD_VETERAN_SOLDIER_BASE").ToList();
        Check(soldiers.Count > 0 && soldiers.All(p => p.Block is { IsDefault: false, Tier: 4, Continents: ["Royal"], RespawnSecondsMin: 60, RespawnSecondsMax: 180 }),
              $"the {soldiers.Count} STATIC_UNDEAD_VETERAN_SOLDIER_BASE points use the tier 4 Royal block (60-180 s)");
        var block = soldiers[0].Block!;
        var picks = Enumerable.Range(0, 2000).Select(_ => block.Pick(rng)!.Name).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
        Check(picks.Keys.Order().SequenceEqual(["T4_MOB_UNDEAD_SKELETON_VETERAN", "T4_MOB_UNDEAD_SOLDIER_VETERAN", "T4_MOB_UNDEAD_SOLDIER_VETERAN_CHAMPION"])
              && picks["T4_MOB_UNDEAD_SKELETON_VETERAN"] > picks["T4_MOB_UNDEAD_SOLDIER_VETERAN"] && picks["T4_MOB_UNDEAD_SOLDIER_VETERAN"] > picks["T4_MOB_UNDEAD_SOLDIER_VETERAN_CHAMPION"],
              $"the block's plain weights 62/35/3 (got {string.Join(", ", picks.Select(p => $"{p.Key} {p.Value}"))})");
        var unknown = soldiers[0] with { Mob = "NO_SUCH_MOB", SpawnBehavior = null, Block = null };
        Check(unknown.Bind(mobs, Store.SpawnBehaviors, 4, "Royal", t.Info.Type) is { Template: null, Block: null }
              && (soldiers[0] with { Mob = MobSpawnPoint.WorldBossName, Block = null }).Bind(mobs, Store.SpawnBehaviors, 4, "Royal", null) is { Template: null, Block: null },
              "an unknown mob name and a WORLDBOSS point stay unbound (the spawner skips them)");
        var prefabPoints = t.Resources!.MobSpawnPoints;
        Check(prefabPoints.Count > 0 && prefabPoints.All(p => p.Prefab is not null && (p.Template is not null || p.Block is not null || p.MobName is null or MobSpawnPoint.WorldBossName)),
              $"the resource layout's prefabs (camps, roaming mobs, critters) bring spawn points of their own, bound ({prefabPoints.Count})");
    }

    private static Dictionary<byte, object?> Wire(EventData e)
    {
        var w = new ByteWriter();
        Protocol18.WriteEventData(w, e);
        return Protocol18.ReadEventData(new ByteReader(w.ToArray())).Parameters;
    }

    private static int Code(EventData e) => WorldObjectTest.Code(e);

    private static long Id(EventData e) => Convert.ToInt64(e.Parameters[0]);

    private static void OnTheMap()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        // 0204 is a faction warfare zone: its towers' guards (stageQ, mobs of no spawn point) are left out here.
        var world = new World(new ServerConfig { JoinByCharacterId = true, FactionWarfare = new FactionWarfareOptions { Outposts = false } }, characters, Client.Items, Client.GameData)
            { Sync = new object(), Clock = clock, Clusters = Store, MapSeed = 42 };
        var template = Store.Get("0204");
        var tower = template.MobSpawnPoints.First(p => p.Mob == "T6_MOB_GUARD_TOWER");
        var c = StarterKit.Default.NewCharacter("Hunter");
        c.Cluster = "0204";
        var stand = tower.Position + new Vector2(3, 0);
        (c.PositionX, c.PositionY) = (stand.X, stand.Y);
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
        var player = session.Player!;
        var map = player.Map!;
        var grid = map.Grid!;
        var range = world.Config.Visibility.EnterCells;
        var mobs = map.Objects.Values.OfType<Mob>().ToList();
        var inView = grid.Around(grid.CellOf(stand), range).OfType<Mob>().ToList();
        var newMobs = peer.Events.Where(e => Code(e) == (int)EventCodes.NewMob).ToList();
        Check(mobs.Count >= 387 - 236 && inView.Count > 0 && newMobs.Count == inView.Count && newMobs.Select(Id).Order().SequenceEqual(inView.Select(m => m.Id).Order()),
              $"the Join sends exactly one NewMob per mob in view ({newMobs.Count} for {inView.Count} in view, {mobs.Count} mobs on the map)");
        var towerMob = inView.Single(m => m.Spawn == tower);
        var tw = Wire(newMobs.Single(e => Id(e) == towerMob.Id));
        Check(Convert.ToInt16(tw[1]) == 2757 && Convert.ToByte(tw[2]) == 5 && !tw.ContainsKey(11) && Convert.ToSingle(tw[13]) == 153572 && Convert.ToSingle(tw[14]) == 153572
              && Convert.ToSingle(tw[16]) == 200 && Convert.ToSingle(tw[18]) == 65 && Convert.ToSingle(tw[19]) == 65 && Convert.ToSingle(tw[21]) == 2
              && (string)tw[6]! == "" && Convert.ToByte(tw[30]) == 0 && Convert.ToByte(tw[34]) == 0 && tw[7] is float[] p7 && p7[0] == tower.Position.X && p7[1] == tower.Position.Y,
              $"guard tower as captured in 0204: 2757, faction 5 (Swamp), no key 11, 153572 hp, regen 200, energy 65/2 (got {string.Join(", ", tw.Select(x => $"{x.Key}={x.Value}"))})");

        var replies = new List<(string Text, bool Error)>();
        void Command(string line) { replies.Clear(); world.Commands.Execute(new CommandContext(world, player, AccountLevel.GameMaster, (t, e) => replies.Add((t, e))), line); }
        Dictionary<byte, object?> SpawnOne(string name)
        {
            var mark = peer.Events.Count;
            Command($".mob spawn {name}");
            var ev = peer.Events.Skip(mark).Where(e => Code(e) == (int)EventCodes.NewMob).ToList();
            Check(replies is [{ Error: false }] && ev.Count == 1, $".mob spawn {name}: one NewMob (got {ev.Count}; {string.Join(" / ", replies)})");
            return Wire(ev[0]);
        }
        var s = SpawnOne("T4_MOB_UNDEAD_SOLDIER_VETERAN");
        Check(Convert.ToInt16(s[1]) == 2306 && Convert.ToByte(s[2]) == 255 && Convert.ToSingle(s[13]) == 5118 && Convert.ToSingle(s[14]) == 5118 && Convert.ToSingle(s[16]) == 33
              && Convert.ToSingle(s[18]) == 157 && Convert.ToSingle(s[19]) == 157 && Convert.ToSingle(s[21]) == 5 && Convert.ToSingle(s[11]) == 1.5f
              && (string)s[6]! == "" && Convert.ToByte(s[30]) == 0 && Convert.ToByte(s[34]) == 0 && !s.ContainsKey(12) && !s.ContainsKey(32)
              && new byte[] { 3, 4, 5, 15, 20, 23, 24, 25, 26, 27, 28, 29, 31, 33 }.All(k => !s.ContainsKey(k)) && s[7] is float[] a && s[8] is float[] b && a.SequenceEqual(b)
              && Convert.ToInt64(s[9]) >= Convert.ToInt64(s[17]) && Convert.ToInt64(s[22]) >= Convert.ToInt64(s[17]),
              $"2306 as captured: k1 2306, k2 255, 5118 hp, regen 33, energy 157/5, speed 1.5, k6 \"\", k30 0, k34 0 (got {string.Join(", ", s.Select(x => $"{x.Key}={x.Value}"))})");
        var knight = SpawnOne("t4_mob_city_swamp_knight_elite");
        Check(Convert.ToInt16(knight[1]) == 2724 && Convert.ToByte(knight[2]) == 5 && Convert.ToSingle(knight[11]) == 2.5f && Convert.ToSingle(knight[14]) == 11606 && !knight.ContainsKey(16),
              "2724 (CITY_SWAMP guard): faction 5, speed 2.5, 11606 hp, no key 16 (regen 0)");
        var toad = SpawnOne("T1_MOB_HIDE_SWAMP_TOAD");
        Check(Convert.ToInt16(toad[1]) == 448 && Convert.ToByte(toad[2]) == 255 && Convert.ToSingle(toad[13]) == 20 && Convert.ToSingle(toad[11]) == 3
              && new byte[] { 16, 18, 19, 21 }.All(k => !toad.ContainsKey(k)) && toad.ContainsKey(22),
              "448 toad: 20 hp, speed 3, no energy keys (18/19/21) and no regen (16), key 22 still sent");
        var mark0 = peer.Events.Count;
        Command(".mob spawn NO_SUCH_MOB");
        Check(replies is [{ Error: true, Text: var unknownText }] && unknownText.Contains("unknown mob NO_SUCH_MOB") && peer.Events.Count == mark0,
              $"an unknown mob name: an error, no NewMob ({string.Join(" / ", replies)})");
        var gm = map.Objects.Values.OfType<Mob>().Single(m => m.Template.Id == 2306 && m.Slot is null);
        Command(".near 200");
        Check(replies is [{ Error: false, Text: var near }] && near.Contains($"mob T4_MOB_UNDEAD_SOLDIER_VETERAN at ") && near.Contains(", type 2306 T4, 5118/5118 hp, spawn 0204:custom:0"),
              $".near lists the mobs with type, tier, health and spawn key (got {string.Join(" / ", replies)})");

        // Walking out of view: one Leave per known mob; back: NewMob again with a newer or equal key 9, MobChangeState for a rare one.
        var known = player.Visibility.Known.OfType<Mob>().ToList();
        var firstStamp = known.ToDictionary(m => m.Id, m => m.Movement.Stamp);
        void MoveTo(Vector2 to) => session.OnRequest(Request(new MoveRequest { wf = GameTime.Now, v6 = [to.X, to.Y], v8 = [to.X, to.Y], v9 = 0f }, null), encrypted: false);
        var away = stand + new Vector2(0, 200);
        var mark = peer.Events.Count;
        MoveTo(away);
        var leaves = peer.Events.Skip(mark).Where(e => Code(e) == (int)EventCodes.Leave).Select(Id).ToList();
        Check(known.Count > 3 && known.All(m => leaves.Count(id => id == m.Id) == 1) && !player.Visibility.Known.Any(o => known.Contains(o)),
              $"walking 200 m away: exactly one Leave per mob the client knew ({known.Count})");
        gm.RareState = 1;
        mark = peer.Events.Count;
        MoveTo(stand);
        var back = peer.Events.Skip(mark).ToList();
        var again = back.Where(e => Code(e) == (int)EventCodes.NewMob).ToList();
        var rareAt = back.FindIndex(e => Code(e) == (int)EventCodes.NewMob && Id(e) == gm.Id);
        Check(known.All(m => again.Count(e => Id(e) == m.Id) == 1 && Convert.ToInt64(again.First(e => Id(e) == m.Id).Parameters[9]) >= firstStamp[m.Id])
              && rareAt >= 0 && Code(back[rareAt + 1]) == (int)EventCodes.MobChangeState && Id(back[rareAt + 1]) == gm.Id && Convert.ToByte(Wire(back[rareAt + 1])[1]) == 1
              && back.Count(e => Code(e) == (int)EventCodes.MobChangeState) == 1,
              "back again: a fresh NewMob per mob (key 9 not older), the rare one followed by MobChangeState {1: 1}");

        // .mob kill: the nearest goes with a Leave; a GM mob does not come back, a spawn point does, after its time x the scale.
        Command(".mob respawnscale 0.01");
        Check(Math.Abs(world.MobRespawnScale - 0.01) < 1e-6 && replies is [{ Error: false }], ".mob respawnscale 0.01");
        mark = peer.Events.Count;
        var before = player.Visibility.Known.OfType<Mob>().ToList();
        var closest = before.Min(m => Vector2.Distance(m.Position, stand));
        Command(".mob kill");
        var gone = peer.Events.Skip(mark).ToList();
        var nearest = before.Single(m => gone is [var only] && Code(only) == (int)EventCodes.Leave && Id(only) == m.Id);
        Check(Vector2.Distance(nearest.Position, stand) == closest && !map.Objects.ContainsKey(nearest.Id) && nearest.Removed,
              $".mob kill: one Leave, for a mob nearest to the player ({nearest}; {string.Join(" / ", replies)})");
        var pointMobs = player.Visibility.Known.OfType<Mob>().Where(m => m.Slot is not null).ToList();
        mark = peer.Events.Count;
        Command(".mob kill all");
        var killed = peer.Events.Skip(mark).Where(e => Code(e) == (int)EventCodes.Leave).Select(Id).ToHashSet();
        Check(pointMobs.Count > 0 && pointMobs.All(m => killed.Contains(m.Id) && m.Slot!.Mob is null && m.Slot.RespawnAt is not null)
              && !player.Visibility.Known.OfType<Mob>().Any(),
              $".mob kill all: a Leave for every mob in view; their points wait ({string.Join(" / ", replies)})");
        var slots = pointMobs.Select(m => m.Slot!).Concat(nearest.Slot is { } first ? [first] : []).ToList();
        var due = slots.Max(x => x.RespawnAt!.Value);
        Check(slots.All(x => x.RespawnAt!.Value - world.Now <= TimeSpan.FromSeconds(x.Point.Block?.RespawnSecondsMax ?? x.Point.Template!.RespawnSecondsMax) * 0.01 + TimeSpan.FromMilliseconds(1)),
              "respawn within the max time x 0.01");
        mark = peer.Events.Count;
        clock.Advance(due - world.Now + TimeSpan.FromMilliseconds(50));
        lock (world.Sync) world.Update();
        var respawned = peer.Events.Skip(mark).Where(e => Code(e) == (int)EventCodes.NewMob).Select(Id).ToHashSet();
        Check(slots.All(x => x.Mob is { } m && respawned.Contains(m.Id) && !pointMobs.Any(old => old.Id == m.Id) && m.Position == x.Point.Position)
              && !map.Objects.Values.OfType<Mob>().Any(m => m.Slot is null)
              && respawned.Count == slots.Count,
              $"after the scaled respawn time every spawn point has a new mob (new id, NewMob in view), the GM mobs stay gone ({respawned.Count} of {slots.Count})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
