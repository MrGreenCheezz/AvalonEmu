using System.Numerics;
using System.Xml.Linq;
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
/// The open-world chests and camp quests (stageH, analysis/work/stageH/bosses/SPEC.md, "SPEC" here, §8 assertions 3, 4, 8-10, 12,
/// 14, 15): the spots of 0204 on 2026-10-15, the data rules (upgrade groups, parser defaults, Rates:ChampionWeight), and on FakePeer
/// sessions with a manual clock the POI cap, lock and unlock, the opening by the client's slot-25 cast (stageM chest-slot25 NOTES), the boss
/// point's reward chest and the camp quest with its personal chest.
/// </summary>
public static class WorldChestTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("world chests: 0204 spots (3 POI, 1 mob upgrade, the camp chest and quest circle of slot 21), 1207 none; upgrade groups, parser defaults, ChampionWeight 3", Data),
        ("world chests: POI cap 1 over respawns, locked by its mobs (rc 10126), unlocked by their death (393, 395), opened by the slot-25 cast, despawned; the mage boss's reward chest; the camp quest", Live),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static bool Near(Vector2 a, Vector2 b, float tolerance = 0.01f) => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

    private static readonly Lazy<ClusterStore> Day15 = new(() => ResourceNodeTest.Day(Capture002835Key57));

    private static void Data()
    {
        var store = Day15.Value;
        var t = store.Get("0204");
        var pois = t.LootChests.Where(c => c.Behaviour is { IsPoi: true }).ToList();
        var upgrade = t.LootChests.Where(c => c.Behaviour is { Kind: ChestSpawnKind.MobUpgrade }).ToList();
        bool At(LootChestSpawn c, float x, float z, float r) => Near(c.Position, new Vector2(x, z)) && Math.Abs(c.Direction - r) < 0.01f;
        Check(pois.Count == 3 && pois.All(p => p.SpawnBehaviour == "STATIC_UNDEAD_VETERAN_POI")
              && pois.Any(p => At(p, -90, -141, 249.2f)) && pois.Any(p => At(p, 131, 81, 17)) && pois.Any(p => At(p, -68, 71, 0))
              && upgrade is [{ SpawnBehaviour: "STATIC_UNDEAD_VETERAN" } u] && At(u, 17, -27, 317.83f) && t.PoiChestLimit == 1,
              $"0204: STATIC_UNDEAD_VETERAN_POI at (-90, -141) r 249.2, (131, 81) r 17, (-68, 71) r 0, STATIC_UNDEAD_VETERAN at (17, -27) r 317.83, POI limit 1 (got {pois.Count} POI, {upgrade.Count} upgrade, limit {t.PoiChestLimit})");
        var camp = t.LootChests.FirstOrDefault(c => c.SpawnBehaviour == "UNDEAD_DYNAMIC_CAMP_PERSONAL_SMALL" && At(c, 2.5f, -227.5f, 134));
        var area = t.LocalQuestAreas.FirstOrDefault(a => a.QuestId == "LOCAL_QUEST_SMALL_UNDEAD" && Near(a.Position, new Vector2(-0.5f, -219.5f)));
        Check(camp is { Behaviour.Kind: ChestSpawnKind.Personal, Key.Slot: var slot } && area is { Radius: 35, Quest: { Index: 8, PerPlayer: true, Fame: 7580, Faction: "UNDEAD_CAMP", UnlocksChest: true } }
              && area.Key.Slot == slot,
              "0204 (2026-10-15): the camp chest UNDEAD_DYNAMIC_CAMP_PERSONAL_SMALL at (2.5, -227.5) r 134 and LOCAL_QUEST_SMALL_UNDEAD (index 8, 7580 UNDEAD_CAMP fame) at (-0.5, -219.5) r 35 in the same slot");
        var t1207 = store.Get("1207");
        Check(!t1207.LootChests.Any(c => c.Behaviour is { IsPoi: true } or { Kind: ChestSpawnKind.MobUpgrade }), "1207: no static chest spots");

        var groups = store.LootChests.Spawnpoints["STATIC_UNDEAD_VETERAN"];
        string? G(int level) => groups.UpgradeFor(level) is [{ } e] ? $"{e.Level}:{e.Chest}" : groups.UpgradeFor(level).Count == 0 ? null : "many";
        Check(G(4) == "3:STATIC_UNDEAD_CHAMPION" && G(6) == "5:STATIC_UNDEAD_MINIBOSS" && G(8) == "7:STATIC_UNDEAD_BOSS" && G(2) is null
              && groups is { Respawn: ChestRespawn.Never, DespawnSeconds: 300, MobUpgradeFaction: "UNDEAD", MobUpgradeRadius: 15 },
              "the mob-upgrade groups: level 4 -> 3 CHAMPION, 6 -> 5 MINIBOSS, 8 -> 7 BOSS, 2 -> none; respawn Never, despawn 300, UNDEAD within 15 m");
        var parsed = LootChestData.Load(new XElement("lootchests", new XElement("LootChest", new XAttribute("uniquename", "X")),
            new XElement("LootChestSpawnpoint", new XAttribute("name", "P"), new XElement("LootChest", new XAttribute("name", "X")))));
        Check(parsed.Spawnpoints["P"] is { Respawn: ChestRespawn.OnDespawn, RespawnSeconds: (36_000_000, 36_000_000), InitialSeconds: -1, DespawnSeconds: -1, IsPoi: false },
              "parser defaults: no respawn = OnDespawn, no respawn min/max = 36 000 000 s, initial and despawn unset");
        var avalon = LootChestData.Load(new XElement("lootchests", new XElement("LootChest", new XAttribute("uniquename", "X")),
            new XElement("LootChestSpawnpointChestUpgrade", new XAttribute("name", "A"),
                new XElement("LootChest", new XAttribute("name", "X"), new XAttribute("lootchestupgradelevelindex", "4")))));
        Check(avalon.Spawnpoints["A"] is { Kind: ChestSpawnKind.ChestUpgrade, IsPoi: false, Chests: [("X", 1)] },
              "LootChestSpawnpointChestUpgrade (Roads of Avalon): a kind of its own, not a POI spot, its chests drawable as a plain one's");

        var block = store.SpawnBehaviors.Select("STATIC_UNDEAD_VETERAN_SOLDIER_BASE", 4, "Royal", "SAFEAREA")!;
        var champion = block.Mobs.Single(m => m.Mob is { IsElite: true }).Mob!;
        var random = new Random(3);
        var picks = Enumerable.Range(0, 100_000).Count(_ => block.Pick(random, 3) == champion);
        var expected = 9.0 / (62 + 35 + 9);
        Check(Math.Abs(picks / 100_000.0 - expected) < 0.01 && champion.Category == "champion",
              $"ChampionWeight 3: {champion.Name} picked {picks / 1000.0:0.##} % of 100 000, expected {expected * 100:0.##} %");
    }

    private static void Live()
    {
        var store = Day15.Value;
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = store, MapSeed = 21,
        };
        world.Rates = world.Rates with { ChestRespawn = 1000, Respawn = 1000 };
        world.MobAi = false;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var hero = NewHero(characters, new Vector2(-90, -137));
        var (a, peerA) = Join(world, hero.Id);
        var me = a.Player!;
        var map = world.Maps.Find("0204")!;
        var spots = world.WorldChests.SpotsOf(map);
        IEnumerable<MobSpawnSlot> Slots() => map.Objects.Values.OfType<Mob>().Select(m => m.Slot).OfType<MobSpawnSlot>().Distinct(); // the points with a mob now
        int Standing() => spots.Count(s => s.Behaviour.IsPoi && s.Chest is not null);
        Check(spots.Count(s => s.Behaviour.IsPoi) == 3 && Standing() == 1 && spots.Any(s => s.Behaviour.Kind == ChestSpawnKind.Personal && s.Chest is { Personal: true }),
              $"0204 with the data's POI limit: one POI chest of three spots, the camp chests stand ({Standing()} POI)");
        var personal = spots.Where(s => s.Behaviour.Kind == ChestSpawnKind.Personal).ToList();
        Check(personal.All(s => s.Area is null ? s.Chest is null : s.Chest is { DefaultViewerState: 4 }),
              $"a camp chest stands only where a per-player quest unlocks it, LockedByQuest by default ({personal.Count(s => s.Area is null)} of {personal.Count} spot(s) without one)");

        // Respawns (Rates:ChestRespawn 1000: 1.8-5.4 s): never two at once, until the chest is the one at (-90, -141).
        var target = spots.First(s => s.Behaviour.IsPoi && Near(s.Point.Position, new Vector2(-90, -141)));
        var most = 0;
        for (var i = 0; i < 60 && target.Chest is null; i++)
        {
            var chest = spots.First(s => s.Behaviour.IsPoi && s.Chest is not null).Chest!;
            lock (world.Sync) Combat.RemoveBag(chest, "test");
            Tick(0.1);
            for (var w = 0; w < 7; w++)
            {
                Tick(1);
                most = Math.Max(most, Standing());
            }
        }
        Check(target.Chest is not null && most == 1, $"the POI chest moves between the spots on its respawns, never two at once (most {most})");

        // Locked by the mobs of its faction whose spawn point is within 15 m (the soldier boss point 6.5 m away).
        var poi = target.Chest!;
        var guards = Slots().Where(s => s.Mob is { Dead: false } && (s.Point.Block?.Faction ?? s.Mob.Template.Faction) == "UNDEAD"
            && Vector2.Distance(s.Point.Position, poi.Position) <= 15).Select(s => s.Mob!).ToList();
        Tick(0.1);
        var sent = peerA.Events.LastOrDefault(e => Code(e) == (int)EventCodes.NewLootChest && Convert.ToInt64(e.Parameters[0]) == poi.Id)?.Parameters;
        OperationResponse? Open(WorldSession s, FakePeer peer, LootChest chest, int id)
        {
            var count = peer.Responses.Count;
            lock (world.Sync) s.OnRequest(Request(new UseLootChestRequest { wf = world.GameNow, aif = chest.Id }, id), encrypted: false);
            return peer.Responses.Count > count ? peer.Responses[^1] : null;
        }
        Check(guards.Count > 0 && poi is { LockState: 2, ProtectedByMobs: true } && sent is not null && sent[5] is (byte)2 && sent[19] is true && sent[21] is -1
              && sent[18] is "STATIC_UNDEAD_VETERAN_POI" && Open(a, peerA, poi, 60) is { ReturnCode: WorldSession.ChestLocked },
              $"the POI chest {poi.Template.Name}: LockedByProtectingMobs by {guards.Count} mob(s), NewLootChest 5 = 2, 19 true, 21 -1; UseLootChest rc 10126 (the client's locked message)");
        // The client's chest click (stageM chest-slot25 NOTES): CastStart in slot 25 with the chest's OPEN_LOOTCHEST at its position, no UseLootChest.
        var openSpell = world.Spells!.IdOf("OPEN_LOOTCHEST");
        OperationResponse? CastAt(Vector2 aim, short spell, byte counter)
        {
            var count = peerA.Responses.Count;
            lock (world.Sync) a.OnRequest(Request(new CastStartRequest { wf = world.GameNow, dx = FakeSlotCasts.Slot, d0 = spell, d2 = aim, d4 = counter }, 80 + counter), encrypted: false);
            return peerA.Responses.Count > count ? peerA.Responses[^1] : null;
        }
        bool Chat(int from) => peerA.Events.Skip(from).Any(e => Code(e) == (int)EventCodes.SystemMessage);
        var chatMark = peerA.Events.Count;
        Check(CastAt(poi.Position, openSpell, 1) is { ReturnCode: WorldSession.CastRefused } && !Chat(chatMark) && !poi.Rolled,
              "the slot-25 OPEN_LOOTCHEST cast on the locked chest: 960, no chat line (no \"Corrupted dungeon\" text), nothing rolled");
        var mark = peerA.Events.Count;
        lock (world.Sync)
            foreach (var g in guards) Combat.Damage(me, g, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        var after = peerA.Events.Skip(mark).ToList();
        var updates = after.Where(e => Code(e) == (int)EventCodes.UpdateLootChest && Convert.ToInt64(e.Parameters[0]) == poi.Id).ToList();
        var minimap = after.Where(e => Code(e) == (int)EventCodes.UpdateLootProtectedByMobsWithMinimapDisplay && Convert.ToInt64(e.Parameters[0]) == poi.Id).ToList();
        Check(updates is [{ } u] && u.Parameters[1] is (byte)6 && u.Parameters[16] is (byte)6 && u.Parameters[13] is int r && r >= 0
              && minimap is [{ } mm] && mm.Parameters.GetValueOrDefault((byte)1) is null or false && poi.LockState == 6,
              $"the last guard's death: one UpdateLootChest {{1 6, 16 6, 13 the rarity}} and one 395 {{1 false}} (got {updates.Count}, {minimap.Count})");
        var seer = NewHero(characters, new Vector2(-92, -137));
        var (_, peerW) = Join(world, seer.Id);
        Tick(0.1);
        // From the edge of the cast's reach (castrange 5 + 1 m): the client walks only into castrange, casts there and opens the loot from there.
        var edge = poi.Position + Vector2.Normalize(me.PositionAt(DateTime.UtcNow) - poi.Position) * 5.6f;
        // A ghoul killed at that spot leaves its silver there (issue #34, below).
        lock (world.Sync) Combat.Damage(me, SpawnManager.SpawnAt(map, store.Mobs.Find("T4_MOB_ROAMING_UNDEAD_GHOUL")!, edge, 0, world), 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        var pile = map.Objects.Values.OfType<SilverPile>().FirstOrDefault(s => s.MayTake(me) && Vector2.Distance(s.Position, edge) < 0.1f);
        mark = peerA.Events.Count;
        var seen = peerW.Events.Count;
        lock (world.Sync) a.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [edge.X, edge.Y], v8 = [edge.X, edge.Y], v9 = 0f }, 79), encrypted: false);
        var shrineCast = CastAt(poi.Position, world.Spells.IdOf("OPEN_BUFFSHRINE"), 2);
        var farCast = CastAt(poi.Position + new Vector2(0, 30), openSpell, 3);
        var accepted = CastAt(poi.Position, openSpell, 4);
        // Public issue #34: the client's auto pickup sends TakeSilver 15 ms into the opening and keeps casting; the pile goes, the cast goes on.
        lock (world.Sync) if (pile is not null) a.OnRequest(Request(new TakeSilverRequest { wf = world.GameNow, agp = true, agq = [pile.Id] }, 78), encrypted: false);
        Check(pile is not null && !map.Objects.ContainsKey(pile.Id) && world.FakeCasts.Of(me) is not null
              && !peerA.Events.Skip(mark).Concat(peerW.Events.Skip(seen)).Any(e => Code(e) == (int)EventCodes.CastCancel),
              "issue #34: TakeSilver during the chest opening takes the pile and leaves the cast running (no CastCancel to anyone)");
        var shown =peerW.Events.Skip(seen).FirstOrDefault(e => Code(e) == (int)EventCodes.CastStart && Convert.ToInt64(e.Parameters[0]) == me.Id)?.Parameters;
        Tick(1);
        var early = poi.Rolled || peerA.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.LootChestOpened);
        Tick(1.1);
        var cast = peerA.Events.Skip(mark).ToList();
        int Index(EventCodes code) => cast.FindIndex(e => Code(e) == (int)code);
        Check(shrineCast is { ReturnCode: WorldSession.CastRefused } && farCast is { ReturnCode: WorldSession.CastRefused } && accepted is null && !Chat(mark)
              && Convert.ToInt32(shown?[9]) == FakeSlotCasts.Slot && Convert.ToInt32(shown?[5]) == openSpell && !early
              && Index(EventCodes.CastFinished) is >= 0 and var finished && Index(EventCodes.CastSpell) > finished && Index(EventCodes.LootChestOpened) > finished
              && poi.Rolled && poi.LockState is 7 or 8 && peerW.Events.Skip(seen).Any(e => Code(e) == (int)EventCodes.LootChestOpened && Convert.ToInt64(e.Parameters[0]) == poi.Id),
              "unlocked: slot-25 OPEN_BUFFSHRINE and an aim 30 m off refused (960, no chat); OPEN_LOOTCHEST at the chest from 5.6 m: no answer, CastStart {5 spell, 9 25} to the observer, "
              + "2 s later CastFinished, CastSpell, LootChestOpened (to the observer too), the loot rolled");
        var attachMark = peerA.Events.Count;
        lock (world.Sync) a.OnRequest(Request(new RegisterToObjectRequest { zg = poi.Id }, 90), encrypted: false);
        Check(poi.LockState == 8 || peerA.Events.Skip(attachMark).Any(e => Code(e) == (int)EventCodes.AttachItemContainer && Convert.ToInt64(e.Parameters[0]) == poi.Id),
              $"from where the cast reached ({Vector2.Distance(me.PositionAt(DateTime.UtcNow), poi.Position):0.#} m) the loot window opens: AttachItemContainer");
        Tick(299);
        var stays = map.Objects.ContainsKey(poi.Id);
        Tick(2);
        Check(stays && !map.Objects.ContainsKey(poi.Id) && target.Chest != poi, "the opened chest goes 300 s after its opening");

        // The mage boss point next to the mob-upgrade spot: its mob's death puts the reward chest there, unlocked.
        var spot = spots.Single(s => s.Behaviour.Kind == ChestSpawnKind.MobUpgrade);
        Check(spot.Chest is null, "the mob-upgrade spot is empty (the soldier boss far away did not fill it)");
        var mage = Slots().FirstOrDefault(s => s.Point.Block is { TriggersMobUpgrade: true } && Vector2.Distance(s.Point.Position, spot.Point.Position) <= 15);
        for (var i = 0; i < 50 && mage?.Mob is not { Dead: false }; i++) Tick(1);
        var boss = mage?.Mob;
        lock (world.Sync) if (boss is not null) Combat.Damage(me, boss, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        var reward = spot.Chest;
        var expect = boss?.Template.Category switch { "champion" => "STATIC_UNDEAD_CHAMPION", "miniboss" => "STATIC_UNDEAD_MINIBOSS", "boss" => "STATIC_UNDEAD_BOSS", _ => "?" };
        Check(boss is not null && reward is { LockState: 6 } && reward.Template.Name == expect && map.Objects.ContainsKey(reward.Id) && reward.RarityFor(6) >= 0,
              $"the kill of {boss?.Template.Name} puts {expect} on the spot, Unlocked, its rarity shown (got {reward})");
        // Its loot taken by single moves (stageM loot-moves: shift+clicks, InventoryMoveItem with the chest's container as the source).
        lock (world.Sync) me.Teleport(reward!.Position + new Vector2(1, 0), 0, DateTime.UtcNow);
        Tick(0.1);
        Check(Open(a, peerA, reward, 91) is { ReturnCode: 0 }, "the reward chest opens");
        var (firstPut, emptied) = TakeOneByOne(world, a, peerA, reward);
        Check(firstPut && emptied.Any(e => Code(e) == (int)EventCodes.DetachItemContainer)
              && emptied.Any(e => Code(e) == (int)EventCodes.Leave && Convert.ToInt64(e.Parameters[0]) == reward.Id) && !map.Objects.ContainsKey(reward.Id) && me.OpenBag is null,
              "the reward chest's loot by single moves: the first a put {id, slot, inventory, key 5}; emptied, DetachItemContainer and the chest's Leave");

        // The camp quest: in the circle LocalQuestStarted {1 8, 7 35}; its fame unlocks the camp chest for this player alone.
        var other = NewHero(characters, new Vector2(-0.5f, -219.5f));
        var (b, peerB) = Join(world, other.Id);
        var you = b.Player!;
        Tick(0.1);
        var started = peerB.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.LocalQuestStarted)?.Parameters;
        var area = world.WorldChests.AreasOf(map).Single(x => x.Quest.Name == "LOCAL_QUEST_SMALL_UNDEAD" && Near(x.Spawn.Position, new Vector2(-0.5f, -219.5f)));
        var campChest = area.Chest?.Chest;
        Check(started is not null && Convert.ToInt32(started[1]) == 8 && started[7] is 35f && started[6] is (byte)1 && Convert.ToInt64(started[0]) == area.Id
              && campChest is { Personal: true } && campChest.ViewerState(you).State == 4 && !peerA.Events.Any(e => Code(e) == (int)EventCodes.LocalQuestStarted),
              "entering the circle: LocalQuestStarted {0 the quest, 1 8, 6 1, 7 35} to that player only; its camp chest LockedByQuest (4)");
        for (var i = 0; i < 300 && area.Progress.TryGetValue(other.Id, out var st) && !st.Done; i++)
        {
            var camp = Slots().FirstOrDefault(s => s.Mob is { Dead: false } && s.Point.Block is { IsQuestMobSpawn: true, Faction: "UNDEAD_CAMP" } && area.Contains(s.Point.Position));
            lock (world.Sync) if (camp is not null) Combat.Damage(you, camp.Mob!, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
            Tick(1);
        }
        var progress = peerB.Events.Where(e => Code(e) == (int)EventCodes.LocalQuestProgressUpdate).ToList();
        var unlocked = peerB.Events.LastOrDefault(e => Code(e) == (int)EventCodes.UpdateLootChest && Convert.ToInt64(e.Parameters[0]) == campChest!.Id)?.Parameters;
        Check(area.Progress[other.Id] is { Done: true, Fame: >= 7580 } && progress.Count > 0 && progress[^1].Parameters[3] is 1f
              && unlocked is not null && unlocked[16] is (byte)6 && unlocked[14] is true && campChest!.ViewerState(me).State == 4,
              $"the camp's fame ({area.Progress[other.Id].Fame:0} of 7580): LocalQuestProgressUpdate to 1, UpdateLootChest {{16 6}} to that player; the other one still sees 4");
        lock (world.Sync) you.Teleport(new Vector2(2.5f, -226.5f), 0, DateTime.UtcNow); // within reach of the chest
        Tick(0.1);
        var own = campChest!;
        Check(Open(b, peerB, own, 70) is { ReturnCode: 0 } && own.RolledFor(you) && !own.RolledFor(me) && own.ViewerState(me).State == 4,
              "the camp chest opens for that player (its own roll); the other one's is untouched");
        var (ownPut, ownEmptied) = TakeOneByOne(world, b, peerB, own);
        Check(ownPut && ownEmptied.Any(e => Code(e) == (int)EventCodes.DetachItemContainer) && own.ViewerState(you).State == 8
              && map.Objects.ContainsKey(own.Id) && !ownEmptied.Any(e => Code(e) == (int)EventCodes.Leave && Convert.ToInt64(e.Parameters[0]) == own.Id),
              "the personal camp chest emptied by single moves: DetachItemContainer, Empty (8) for that player, the chest stays");
        var wasOpen = you.OpenBag == own;
        var before = peerB.Events.Count;
        Tick(601);
        Tick(0.1);
        var reset = peerB.Events.Skip(before).ToList();
        int At(EventCodes code) => reset.FindIndex(e => Code(e) == (int)code && Convert.ToInt64(e.Parameters[0]) == area.Id);
        Check(own.ViewerState(you).State == 4 && !own.RolledFor(you) && you.OpenBag is null
              && (!wasOpen || reset.Any(e => Code(e) == (int)EventCodes.DetachItemContainer))
              && At(EventCodes.LocalQuestAreaGone) is >= 0 and var gone && At(EventCodes.LocalQuestStarted) > gone,
              "600 s after the opening the player's camp chest and quest are cleared: its container detached, 603 before the quest starts again");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// Opens <paramref name="chest"/>'s loot window (RegisterToObject) and takes its items one by one into free inventory slots (the
    /// client's shift+click): whether the first take was answered with its put {0 id, 1 slot, 2 inventory, 3 key 5}, and the events since.
    /// </summary>
    private static (bool FirstPut, List<EventData> Events) TakeOneByOne(World world, WorldSession s, FakePeer peer, LootChest chest)
    {
        var p = s.Player!;
        var mark = peer.Events.Count;
        lock (world.Sync) s.OnRequest(Request(new RegisterToObjectRequest { zg = chest.Id }, 92), encrypted: false);
        var items = chest.ItemsFor(p);
        var bag = p.Inventory.Bag;
        var first = false;
        Check(p.OpenBag == chest && items.Count > 0, $"{chest} attached with {items.Count} item(s)");
        // stageN mists-chest: the client finds its view of a personal chest under the character's Guid (clj.a5), else drops the attach.
        var attach = peer.Events.Skip(mark).LastOrDefault(e => Code(e) == (int)EventCodes.AttachItemContainer)?.Parameters;
        var view = attach?.GetValueOrDefault((byte)2) is byte[] { Length: 16 } g ? new Guid(g) : Guid.Empty;
        Check(attach is not null && view == (chest.Personal ? p.Character.Id : Guid.Empty),
              $"AttachItemContainer key 2 = {(chest.Personal ? "the character's Guid (personal chest)" : "empty")} (got {view})");
        for (var i = 0; i < 64 && items.Count > 0; i++)
        {
            var item = items.Items.First();
            var slot = bag.FreeSlot();
            var before = peer.Events.Count;
            lock (world.Sync)
                s.OnRequest(Request(new InventoryMoveItemRequest { ru = items.IndexOf(item), rv = items.Id, rw = 2 + i, rx = slot, ry = bag.Id, rz = 5 + i }, 93), encrypted: false);
            if (i == 0)
                first = peer.Events.Skip(before).FirstOrDefault()?.Parameters is { } put && Code(peer.Events[before]) == (int)EventCodes.InventoryPutItem
                    && Convert.ToInt64(put[0]) == item.Id && Convert.ToInt32(put.GetValueOrDefault((byte)1) ?? 0) == slot && Convert.ToInt64(put[3]) == 5 && bag[slot] == item;
        }
        return (first, peer.Events.Skip(mark).ToList());
    }

    private static Character NewHero(SqliteCharacterRepository characters, Vector2 at)
    {
        var c = StarterKit.Default.NewCharacter("W" + Guid.NewGuid().ToString("N")[..10]);
        c.Cluster = "0204";
        c.PositionX = at.X;
        c.PositionY = at.Y;
        Check(characters.Create(c), "character created");
        return c;
    }

    private static (WorldSession Session, FakePeer Peer) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        return (session, peer);
    }
}
