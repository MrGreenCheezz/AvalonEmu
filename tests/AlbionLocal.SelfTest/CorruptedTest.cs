using System.Numerics;
using System.Text.RegularExpressions;
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
/// Corrupted dungeons, phase A (analysis/work/stageL/corrupted/SPEC.md §5, "SPEC" here): the client tables and the per-difficulty variants
/// of a map; then, on FakePeer sessions with a manual clock, a Slayer run from Thetford's Hell Den: 637, 419, the enter cast's gates and
/// interrupts, the push {0, 1, 4}, the instance (450, scripted spawns left out, the shrine, chests), the equipment lock, the points (451),
/// the demon (449 kinds 1 and 2), infamy (446), a mob's knockdown, the relog's SelectCharacter key 5, slot 21, the exit portal and the
/// closing; the official preset's exit portal after the demon; the escape and a death inside.
/// </summary>
public static class CorruptedTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("corrupted: corrupteddungeons.xml (3 difficulties, 20 maps, 26 milestones, the bonus curve), the Hell Den entrance, T5/T8 variants of CORRUPT-001, content/corrupted.json", Data),
        ("corrupted: Slayer from 0000-HellDen: 637, 419, gates (960), cast cancelled and damaged, push {0, 1, 4}, 450, no scripted spawns, equip lock, 451 to the demon, 449 1/2, 446, chests, knockdown, relog key 5, slot 21, exit portal, close", Live),
        ("corrupted: official preset: no exit portal before the demon (1642), the portal after it leads out", Official),
        ("corrupted: the escape {0: 0, 3: true} (refused knocked down, the early-leave infamy), a death inside leaves the run", Escape),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static string Variant(int tier, string type, string map = "CORRUPT-001") => new ClusterVariant(map, tier, type).Id;

    private static void Data()
    {
        var data = Store.Corrupted;
        Check(data.Difficulties.Select(d => (d.Tier, d.PointsRequirement, d.ClusterType)).SequenceEqual([(5, 100, "CORRUPTED_DUNGEON_BEGINNER"), (6, 500, "CORRUPTED_DUNGEON_INTERMEDIATE"), (8, 2000, "CORRUPTED_DUNGEON_PRO")])
              && data.Maps.Count == 20 && data.Milestones.Count == 26 && data.Milestones[^1] == 10_000_000 && data.At(2) is { InfamyRequirement: 100_000, InfamyKillBonus: 6000, ItemPower: 1100 }
              && Math.Abs(data.At(0)!.Bonus(4000) - 0.067) < 0.001 && Math.Abs(data.At(2)!.Bonus(1_000_000) - 0.135) < 0.001 && data.IndexOfSpell("ENTER_CURRUPTED_DUNGEON_SPELL_3") == 2,
              "3 difficulties T5/T6/T8 with 100/500/2000 points, 20 maps, 26 milestones; Slayer 100 000 infamy, bonus 6000, 1100 IP; the curve +6.7 % (Hunter, 4000) / +13.5 % (Slayer, 1 M)");
        var den = Store.Get("0000-HellDen");
        Check(den.Info.Type == "PLAYERCITY_HELLDEN" && den.Critical.Where(m => m.Type == "corrupteddungeonstaticentrance").Select(m => m.Position).SequenceEqual([new Vector2(793, 162)]),
              "0000-HellDen (PLAYERCITY_HELLDEN): one corrupted entrance at (793, 162)");
        var hunter = Store.Get(Variant(5, "CORRUPTED_DUNGEON_BEGINNER"));
        var slayer = Store.Get(Variant(8, "CORRUPTED_DUNGEON_PRO"));
        bool CdMobs(ClusterTemplate t, string tier) => t.MobSpawnPoints.Where(p => p.Block is not null).SelectMany(p => p.Block!.Mobs).Where(m => m.Name.Contains("_MOB_CD_", StringComparison.Ordinal))
            .All(m => m.Name.StartsWith(tier, StringComparison.Ordinal));
        Check(hunter is { Id: "CORRUPT-001", DangerType: "corrupted_beginner", Pvp.PlayersAreLethal: false, Pvp.AllowEquipmentSwapping: false, Name.Tier: 5 } && CdMobs(hunter, "T5_")
              && slayer is { DangerType: "corrupted_pro", Pvp.PlayersAreLethal: true, Name.Tier: 8 } && CdMobs(slayer, "T8_")
              && slayer.MobSpawnPoints.Count(p => p.SpawnBehavior == CorruptedData.EndBoss) == 1 && !slayer.MobBindingReport().Any()
              && !slayer.MobSpawnPoints.Any(p => p.SpawnBehavior is { } b && CorruptedData.MarkerBehaviours.Contains(b))
              && slayer.Exits.Single(e => e.Tile == "Exit_CORRUPT").SpawnPosition is { } arrival && slayer.LootChests.Count(c => RandomDungeons.IsBossChest(c.SpawnBehaviour)) == 1,
              $"CORRUPT-001 as Hunter: T5 mobs, corrupted_beginner, not lethal, no equipment swap; as Slayer: T8 mobs, corrupted_pro, lethal; one demon point, no teleport markers, the Exit_CORRUPT arrival, one boss chest (got {hunter.DangerType}/{slayer.DangerType})");
        var file = CorruptedSettings.Parse(File.ReadAllText(Path.Combine(DataDir(), "..", "content", "corrupted.json")), "corrupted.json");
        Check(file.Completion.OrderBy(c => c.Key).SequenceEqual(CorruptedSettings.Default.Completion.OrderBy(c => c.Key)) && file.Maps.Count == 0
              && file.Texts.OrderBy(t => t.Key).SequenceEqual(CorruptedSettings.DefaultTexts.OrderBy(t => t.Key)),
              "content/corrupted.json holds the built-in completion rewards, map weights and texts");
        var cfg = NewDir();
        var none = Path.Combine(cfg, "albionlocal.json");
        Check(LoadTestConfig(none, ["--Preset=pve"]).Corrupted is { ExitPortal: CorruptedExitMode.Always, InfamyFactor: 2, EarlyLeaveShare: 0.5, RewardFactor: 1.5, EmptyCloseSeconds: 30, CheckItemPower: false, CheckInfamy: true }
              && LoadTestConfig(none, ["--Preset=official"]).Corrupted == new CorruptedOptions()
              && LoadTestConfig(none, ["--Preset=official", "--Corrupted:ExitPortal=Off"]).Corrupted is { ExitPortal: CorruptedExitMode.Off, InfamyFactor: 1 },
              "Corrupted:* under pve (portal Always, infamy x2, early leave 0.5, rewards x1.5, no server item power gate), official (the record's), an explicit key wins");
        try { Directory.Delete(cfg, true); } catch (IOException) { }
    }

    /// <summary>A world of the record's config (official) with the given corrupted options, on the real client data, CORRUPT-001 the only map.</summary>
    private static (World World, ManualClock Clock, string Dir, SqliteCharacterRepository Characters) NewWorld(CorruptedOptions options, int seed)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true, Corrupted = options }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Store, MapSeed = seed, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        world.CorruptedSettings = CorruptedSettings.Default with { Maps = Store.Corrupted.Maps.ToDictionary(m => m.Id, m => m.Id == "CORRUPT-001" ? 1.0 : 0) };
        return (world, clock, dir, characters);
    }

    private static Character Hero(SqliteCharacterRepository characters, Vector2 at)
    {
        var hero = StarterKit.Default.NewCharacter("C" + Guid.NewGuid().ToString("N")[..10]);
        (hero.Cluster, hero.PositionX, hero.PositionY) = ("0000-HellDen", at.X, at.Y);
        Check(characters.Create(hero), "character created");
        return hero;
    }

    private static void Live()
    {
        var (world, clock, dir, characters) = NewWorld(ServerConfig.PveDefaults.Corrupted with { CheckItemPower = true }, 7); // the server's gate on, to see it
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        _ = Store.Get("0000-HellDen");
        var variant = Variant(8, "CORRUPTED_DUNGEON_PRO");
        _ = Store.Get(variant);
        var spot = new Vector2(795, 162);
        var hero = Hero(characters, spot);
        var watcherChar = Hero(characters, spot + new Vector2(0, 2));
        var (s, peer, _) = Join(world, hero.Id);
        var (_, watcher, _) = Join(world, watcherChar.Id);
        var me = s.Player!;
        OperationResponse Call(WorldSession session, FakePeer on, AlbionRequest req, int id)
        {
            lock (world.Sync) session.OnRequest(Request(req, id), encrypted: false);
            return on.Responses[^1];
        }
        List<EventData> Of(FakePeer on, EventCodes code, int from = 0) => [.. on.Events.Skip(from).Where(e => Code(e) == (int)code)];

        // §5 checks 2-3: the entrance (637 at the tile) and GetInfamyInfo of a fresh character.
        var entrance = Of(peer, EventCodes.NewCorruptedStaticEntrance).SingleOrDefault();
        var info = new GetInfamyInfoResponse();
        AlbionSerializer.FillFields(info, Call(s, peer, new GetInfamyInfoRequest(), 3).Parameters);
        Check(entrance?.Parameters[2] is float[] p2 && new Vector2(p2[0], p2[1]) == new Vector2(793, 162) && info is { azh: 0, azl: 0, azp: false },
              "in view of the Hell Den entrance: 637 at (793, 162); GetInfamyInfo of a fresh character: 0, peak 0, key 8 false");

        // §5 check 4: Slayer with 900 item power, then 1100 without infamy: 960 and no run; with peak 100 000: key 8 true, accepted.
        void Gear(int tier)
        {
            string[] names = [$"T{tier}_MAIN_SWORD", $"T{tier}_OFF_SHIELD", $"T{tier}_HEAD_PLATE_SET1", $"T{tier}_ARMOR_PLATE_SET1", $"T{tier}_SHOES_PLATE_SET1", "", $"T{tier}_CAPE"];
            lock (world.Sync)
                for (var slot = 0; slot < names.Length; slot++)
                    if (names[slot].Length > 0) me.Inventory.Equipment.Put(slot, new Item(world.ObjectIds.Next(), Client.Items.Find(names[slot])!, 1, 1, 0, [], []));
        }
        var spell = world.Spells!.IdOf("ENTER_CURRUPTED_DUNGEON_SPELL_3");
        CastStartRequest Cast(byte counter) => new() { wf = world.GameNow, dx = CorruptedDungeons.EnterSlot, d0 = spell, d2 = new Vector2(793, 162), d4 = counter };
        Gear(6);
        var lowIp = Call(s, peer, Cast(1), 4);
        Gear(8);
        var noInfamy = Call(s, peer, Cast(2), 5);
        lock (world.Sync) world.Corrupted.SetInfamy(me.Character, 100_000);
        AlbionSerializer.FillFields(info, Call(s, peer, new GetInfamyInfoRequest(), 6).Parameters);
        Check(lowIp.ReturnCode == WorldSession.CastRefused && noInfamy.ReturnCode == WorldSession.CastRefused && world.Corrupted.Runs.Count == 0 && !world.Corrupted.Entering(me)
              && Math.Abs(Expeditions.AverageItemPower(me) - 1100) < 0.5 && info is { azh: 100_000, azl: 100_000, azp: true },
              $"Slayer refused (960) at 900 item power and at 1100 without infamy, no run; 419 at 100 000: key 8 true (got {lowIp.ReturnCode}/{noInfamy.ReturnCode}, IP {Expeditions.AverageItemPower(me):0})");

        // §5 check 17: the cast reaches an observer at once (14, slot 25); a CastCancel 1 s later and damage end it without a run.
        var mark = watcher.Events.Count;
        var before = peer.Responses.Count;
        lock (world.Sync) s.OnRequest(Request(Cast(3), null), encrypted: false);
        var shown = Of(watcher, EventCodes.CastStart, mark).SingleOrDefault();
        Tick(1);
        lock (world.Sync) s.OnRequest(Request(new CastCancelRequest { wf = world.GameNow }, null), encrypted: false);
        Tick(3);
        var den = me.Map!;
        var imp = Store.Mobs.Find("T8_MOB_CD_DEMON_IMP")!;
        Mob hitter;
        lock (world.Sync)
        {
            hitter = SpawnManager.SpawnAt(den, imp, spot + new Vector2(1, 0), 0, world);
            s.OnRequest(Request(Cast(4), null), encrypted: false);
            Combat.DamagePlayer(hitter, me, 10, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
            SpawnManager.Despawn(hitter, world, respawn: false);
        }
        Tick(4);
        Check(peer.Responses.Count == before && Convert.ToInt32(shown?.Parameters[9]) == CorruptedDungeons.EnterSlot && Convert.ToInt32(shown?.Parameters[5]) == spell
              && world.Corrupted.Runs.Count == 0 && Of(peer, EventCodes.CastCancel).Count >= 1 && Of(watcher, EventCodes.CastCancel, mark).Count == 2,
              $"accepted without an answer, CastStart {{5 spell, 9 slot 25}} to the observer; cancelled after 1 s and hit during the next: CastCancel to the observer twice, no run");

        // §5 check 5: 3 s after an accepted cast the push {0 @CORRUPTEDDUNGEON@, 1 CORRUPT-001, 4 CORRUPTED_DUNGEON_PRO}.
        lock (world.Sync) s.OnRequest(Request(Cast(5), null), encrypted: false);
        Tick(2.5);
        Check(world.Corrupted.Runs.Count == 0, "no run before the 3 s");
        Tick(0.6);
        var push = peer.Responses[^1];
        var run = world.Corrupted.Runs.SingleOrDefault();
        Check(run is not null && push.Parameters[0] is string id && id == run.Id && Regex.IsMatch(id, "^@CORRUPTEDDUNGEON@[0-9a-f-]{36}$") && push.Parameters[1] is "CORRUPT-001"
              && push.Parameters[4] is "CORRUPTED_DUNGEON_PRO" && push.Parameters.Keys.Where(k => k < 250).Order().SequenceEqual(new byte[] { 0, 1, 4 }) && s.Status == SessionStatus.Transferring
              && Of(watcher, EventCodes.CastFinished, mark).Count == 1,
              $"3 s later: CastFinished to the observer, the push {{0 @CORRUPTEDDUNGEON@<guid>, 1 CORRUPT-001, 4 CORRUPTED_DUNGEON_PRO}} (got {string.Join(",", push.Parameters.Where(x => x.Key < 250).Select(x => $"{x.Key}={x.Value}"))})");

        // §5 checks 6, 5, 15: the Join: 450 {1 the owner, 2 2}, T8 mobs only, no scripted spawns, no open-world objects; the shrine inactive, the chests locked.
        var (s2, peer2, join) = Join(world, hero.Id);
        me = s2.Player!;
        var map = me.Map!;
        var status = Of(peer2, EventCodes.CorruptedDungeonStatus).SingleOrDefault();
        var mobs = map.Objects.Values.OfType<Mob>().ToList();
        Check(join.a8i == run!.Id && join.a8j == run.Start.Position && map.Template?.Info.Type == "CORRUPTED_DUNGEON_PRO"
              && status?.Parameters[1] is byte[] owner && new Guid(owner) == hero.Id && Convert.ToInt32(status.Parameters[2]) == 2
              && mobs.Count > 50 && mobs.Where(m => m.Template.Name.Contains("_MOB_CD_", StringComparison.Ordinal)).All(m => m.Template.Name.StartsWith("T8_", StringComparison.Ordinal))
              && !mobs.Any(m => m.Spawn.SpawnBehavior is { } b && CorruptedData.ScriptedBehaviours.Contains(b))
              && !map.Objects.Values.Any(o => o is RandomDungeonExit || o is Mob { Boss: not null }) && !WorldEvents.Fits(map)
              && Of(peer2, EventCodes.NewCorruptedShrine).SingleOrDefault() is { } shrine && shrine.Parameters[4] is (byte)0 && shrine.Parameters[3] is "SHRINE_NON_ESCAPE"
              && run.BossChest is { LockState: RandomDungeons.BossLocked, LootTier: 8 } && run.Chests.Count > 0 && run.Chests.Any(c => c.LockState == RandomDungeons.BossLocked)
              && map.Objects.Values.OfType<LootChest>().Count() == run.Chests.Count + 1 && run.Portal is not null
              && me.Effects.Spells.Any(e => e.Spell.Name == CorruptedDungeons.EnterBuff),
              $"the Join at the Exit_CORRUPT arrival: 450 {{1 me, 2 2}}, {mobs.Count} mobs (CD ones T8), no demon/statue/imp, no random exit, world boss or event; shrine 458 {{3 SHRINE_NON_ESCAPE, 4 state 0: usable, pve invasions by consent}}, boss chest locked at T8, guarded chests locked, the exit portal (pve), CORRUPTED_DUNGEON_ENTER on");

        // §5 check 10: an equipment change inside is refused (allowequipmentswapping false): put back, the main hand unchanged.
        var held = me.Inventory.Equipment[0];
        int free;
        lock (world.Sync)
        {
            free = me.Inventory.Bag.FreeSlot();
            me.Inventory.Bag.Put(free, new Item(world.ObjectIds.Next(), Client.Items.Find("T8_MAIN_AXE")!, 1, 1, 0, [], []));
        }
        mark = peer2.Events.Count;
        lock (world.Sync) s2.OnRequest(Request(new InventoryMoveItemRequest { ru = free, rv = me.Inventory.Bag.Id, rx = 0, ry = me.Inventory.Equipment.Id, rz = 1 }, null), encrypted: false);
        Check(me.Inventory.Equipment[0] == held && me.Inventory.Bag[free]?.Template.Name == "T8_MAIN_AXE" && Of(peer2, EventCodes.InventoryPutItem, mark) is [var back] && Convert.ToInt32(back.Parameters[1]) == free,
              "a move into the equipment is put back (InventoryPutItem to its bag slot), the main hand unchanged");

        // §5 check 7: kills worth 60 % of W bring the points (451 each), the demon spawns once (449 kind 1).
        void Kill(Mob m)
        {
            lock (world.Sync) Combat.Damage(me, m, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        }
        var guarded = run.Chests.First(c => c.LockState == RandomDungeons.BossLocked);
        var weight = run.Weight;
        var worth = new Dictionary<long, double>(run.Worth);
        double killed = 0;
        foreach (var (mobId, fame) in worth.OrderBy(w => w.Key))
        {
            if (run.DemonSpawned) break;
            Kill((Mob)map.Objects[mobId]);
            killed += fame;
        }
        var points = Of(peer2, EventCodes.CorruptedDungeonInfamy);
        Check(run.DemonSpawned && run.Points >= 2000 && killed <= 0.6 * weight + worth.Values.Max() && killed >= 0.6 * weight - worth.Values.Max()
              && points.Count > 0 && Convert.ToInt32(points[^1].Parameters[1]) == run.Points
              && Of(peer2, EventCodes.CorruptedDungeonUpdate) is [var demonOut] && Convert.ToInt32(demonOut.Parameters[1]) == 1
              && map.Objects.GetValueOrDefault(run.DemonId) is Mob { Template.Name: var demonName } && demonName.StartsWith("T8_MOB_CD_DEMON_", StringComparison.Ordinal),
              $"kills worth {killed / weight:P0} of W: {run.Points} points (451 x{points.Count}), the demon out once (449 kind 1)");

        // §5 check 14 (standard chests): every counted mob near a guarded chest killed: it unlocks; the boss chest stays locked.
        foreach (var m in map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && run.Worth.ContainsKey(m.Id) && Vector2.Distance(m.Position, guarded.Position) <= 20).ToList()) Kill(m);
        Check(guarded.LockState == 6 && run.BossChest!.LockState == RandomDungeons.BossLocked && Of(peer2, EventCodes.CorruptedDungeonUpdate).Count == 1,
              "the guards of a standard chest dead: it unlocks (6); the boss chest stays BossLocked; still one 449");

        // §5 check 8: the demon dies: 449 kind 2, 450 key 5 = 2, the boss chest unlocks, infamy (points + 6000) x 2 saved, 446.
        mark = peer2.Events.Count;
        var silver = me.Character.Silver;
        Kill((Mob)map.Objects[run.DemonId]);
        var expected = 100_000 + (run.Points + 6000) * 2L;
        var infamy = Of(peer2, EventCodes.UpdateInfamy, mark).SingleOrDefault();
        Check(run.Completed && Of(peer2, EventCodes.CorruptedDungeonUpdate, mark) is [var done, var chain] && Convert.ToInt32(done.Parameters[1]) == 2
              && Convert.ToInt32(chain.Parameters[1]) == 15 // phase B: the chain exit (exit allowed)
              && Of(peer2, EventCodes.CorruptedDungeonStatus, mark) is [var finished, ..] && Convert.ToInt32(finished.Parameters[5]) == 2
              && run.BossChest!.LockState == 6 && CorruptedDungeons.Current(me.Character) == expected && CorruptedDungeons.Peak(me.Character) == expected
              && infamy is not null && Convert.ToInt32(infamy.Parameters[1]) == 1 && Convert.ToInt64(infamy.Parameters[2]) == expected
              && me.Character.Infamy is { Runs: 1, Completed: [0, 0, 1] }
              && me.Character.Silver - silver == (long)Math.Round(120_000 * 10000 * 1.5 * (1 + run.Bonus)),
              $"the demon slain: 449 kinds 2 and 15 (the chain), 450 state 2, the boss chest open, infamy {CorruptedDungeons.Current(me.Character)} = 100 000 + (points + 6000) x 2, 446 {{1 1, 2 total}}, +120 000 x 1.5 x (1 + bonus) silver");

        // §5 check 9: a mob's final hit inside knocks down (never dead), the official PvP rules notwithstanding.
        lock (world.Sync)
        {
            hitter = SpawnManager.SpawnAt(map, imp, me.PositionAt(DateTime.UtcNow) + new Vector2(1, 0), 0, world);
            Combat.DamagePlayer(hitter, me, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        }
        var down = me.Combat.Down?.Kind;
        lock (world.Sync) SpawnManager.Despawn(hitter, world, respawn: false);
        Tick(1); // the combat over
        Tick(19); // knockdowngetuptimeseconds_combatover (18 s) later
        Check(down == KnockdownKind.Pve && me.Combat.Down is null, $"a mob's killing hit: a PvE knockdown (got {down}), up again 20 s later");

        // §5 checks 18, 16: slot 21 inside gets no answer; a relog inside: SelectCharacter {1 the id, 2 CORRUPT-001, 5 CORRUPTED_DUNGEON_PRO}.
        before = peer2.Responses.Count;
        lock (world.Sync) s2.OnRequest(Request(new CastStartRequest { wf = world.GameNow, dx = CorruptedDungeons.EscapeSlot, d0 = world.Spells.IdOf("ESCAPE_CORRUPTED_DUNGEON"), d4 = 6 }, 9), encrypted: false);
        var quiet = peer2.Responses.Count == before;
        lock (world.Sync) s2.OnDisconnected("logout");
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, world.Config, characters, StarterKit.Default) { World = world };
        lock (world.Sync)
        {
            login.OnRequest(Request(TestLogin(), 1), encrypted: true);
            login.OnRequest(Request(new SelectCharacterRequest { abc = hero.Name }, null), encrypted: true);
        }
        var select = new SelectCharacterResponse();
        AlbionSerializer.FillFields(select, loginPeer.Responses[^1].Parameters);
        Check(quiet && select.blo == run.Id && select.blp == "CORRUPT-001" && select.bls == "CORRUPTED_DUNGEON_PRO",
              $"slot 21 inside: no answer; relog: SelectCharacter {{1 the id, 2 CORRUPT-001, 5 CORRUPTED_DUNGEON_PRO}} (got {select.blo}/{select.blp}/{select.bls})");

        // §5 check 11: the exit portal leads back to the way back; the run closes 30 s later; a relog then lands at DungeonReturn.
        var (s3, peer3, rejoin) = Join(world, hero.Id);
        me = s3.Player!;
        Tick(11); // the exit cooldown after the Join
        var portal = run.Portal!;
        lock (world.Sync) s3.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [portal.Position.X, portal.Position.Y], v8 = [portal.Position.X, portal.Position.Y], v9 = 0f }, null), encrypted: false);
        var outward = Call(s3, peer3, new ChangeClusterRequest { ee = portal.Id }, 20);
        var (s4, _, home) = Join(world, hero.Id);
        Tick(1); // empty from here
        Tick(30);
        var gone = world.LoadCharacter(hero.Id)!.Clone();
        (gone.Cluster, gone.DungeonReturn) = (run.Id, run.Back);
        bool returned;
        lock (world.Sync) returned = world.Corrupted.ReturnIfClosed(gone);
        Check(rejoin.a8i == run.Id && outward is { ReturnCode: 0 } && outward.Parameters[0] is "0000-HellDen" && home.a8i == "0000-HellDen"
              && Vector2.Distance(home.a8j, new Vector2(run.Back.X, run.Back.Y)) < 0.01 && run.Closed && !world.Corrupted.IsLive(run.Id)
              && returned && gone is { Cluster: "0000-HellDen", DungeonReturn: null } && !s4.Player!.Effects.Spells.Any(e => e.Spell.Name == CorruptedDungeons.EnterBuff),
              $"the relog's Join into the run; the exit portal: {{0: 0000-HellDen}}, the Join at the way back without CORRUPTED_DUNGEON_ENTER; closed 30 s after; a character saved inside goes to its way back (got {outward.ReturnCode} {home.a8j})");
        world.Dispose();
        Check(characters.Find(hero.Id)?.Infamy is { Runs: 1, Completed: [0, 0, 1] } saved && saved.Current[CorruptedData.InfamyType] == expected && saved.Peak[CorruptedData.InfamyType] == expected,
              "the infamy column (schema v24): current and peak, one run, one Slayer completion");
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Official()
    {
        var (world, clock, dir, characters) = NewWorld(new CorruptedOptions(), 11);
        _ = Store.Get("0000-HellDen");
        var variant = Variant(6, "CORRUPTED_DUNGEON_INTERMEDIATE");
        _ = Store.Get(variant);
        var hero = Hero(characters, new Vector2(795, 162));
        var (s, _, _) = Join(world, hero.Id);
        CorruptedRun run;
        lock (world.Sync) run = world.Corrupted.Enter(s.Player!, Store.Corrupted.At(1)!, "CORRUPT-001", variant);
        var (s2, peer2, _) = Join(world, hero.Id);
        var me = s2.Player!;
        clock.Advance(TimeSpan.FromSeconds(11));
        lock (world.Sync) world.Update();
        var tile = run.ExitTile!.Position;
        lock (world.Sync) s2.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [tile.X, tile.Y], v8 = [tile.X, tile.Y], v9 = 0f }, null), encrypted: false);
        lock (world.Sync) s2.OnRequest(Request(new ChangeClusterRequest { ee = 987654321 }, 1), encrypted: false);
        var early = peer2.Responses[^1];
        var noPortal = run.Portal is null && !me.Map!.Objects.Values.OfType<CorruptedExitPortal>().Any();
        lock (world.Sync) world.Corrupted.Complete(run, me.Map!);
        var shown = peer2.Events.Any(e => Code(e) == (int)EventCodes.NewRandomDungeonExit && Convert.ToInt64(e.Parameters[0]) == run.Portal?.Id);
        lock (world.Sync) s2.OnRequest(Request(new ChangeClusterRequest { ee = run.Portal!.Id }, 2), encrypted: false);
        var after = peer2.Responses[^1];
        Check(noPortal && early.ReturnCode == (short)ChangeClusterRefusal.ExitInfoNotFound && shown && after is { ReturnCode: 0 } && after.Parameters[0] is "0000-HellDen",
              $"official: no portal before the demon (a made-up id: {early.ReturnCode}); after it the portal (324) and its walk-in {{0: 0000-HellDen}} (got {after.ReturnCode})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Escape()
    {
        var (world, clock, dir, characters) = NewWorld(ServerConfig.PveDefaults.Corrupted, 13);
        _ = Store.Get("0000-HellDen");
        var variant = Variant(5, "CORRUPTED_DUNGEON_BEGINNER");
        _ = Store.Get(variant);
        var hero = Hero(characters, new Vector2(795, 162));
        var (s, _, _) = Join(world, hero.Id);
        CorruptedRun run;
        lock (world.Sync) run = world.Corrupted.Enter(s.Player!, Store.Corrupted.At(0)!, "CORRUPT-001", variant);
        var (s2, peer2, _) = Join(world, hero.Id);
        var me = s2.Player!;
        clock.Advance(TimeSpan.FromSeconds(11));
        lock (world.Sync) world.Update();
        run.Points = 40;

        // The escape's ChangeCluster {0: 0, 3: true}: refused knocked down (as the portal), then out with points x 2 x 0.5 infamy.
        lock (world.Sync)
        {
            Knockdown.Start(me, 0, "test", world.GameNow);
            s2.OnRequest(Request(new ChangeClusterRequest { ee = 0, eh = true }, 1), encrypted: false);
        }
        var downed = peer2.Responses[^1];
        lock (world.Sync)
        {
            Knockdown.GetUp(me);
            s2.OnRequest(Request(new ChangeClusterRequest { ee = 0, eh = true }, 2), encrypted: false);
        }
        var escape = peer2.Responses[^1];
        Check(downed.ReturnCode == (short)ChangeClusterRefusal.NotNearAnExit && escape is { ReturnCode: 0 } && escape.Parameters[0] is "0000-HellDen"
              && run.OwnerLeft && CorruptedDungeons.Current(me.Character) == 40 && world.Corrupted.RunOf(hero.Id) is null && me.Character.DungeonReturn is null,
              $"the escape: refused knocked down (got {downed.ReturnCode}), then {{0: 0000-HellDen}}, the run left, 40 points x 2 x 0.5 = 40 infamy (got {escape.ReturnCode}, {CorruptedDungeons.Current(me.Character)})");

        // A death inside (the official preset's lethal ranks, the die button): the city respawn leaves the run, a new one is open at once.
        var (s3, _, _) = Join(world, hero.Id);
        CorruptedRun second;
        lock (world.Sync) second = world.Corrupted.Enter(s3.Player!, Store.Corrupted.At(0)!, "CORRUPT-001", variant);
        var (s4, _, _) = Join(world, hero.Id);
        me = s4.Player!;
        second.Points = 20;
        bool respawned;
        lock (world.Sync)
        {
            Death.Kill(me, null, 0, "test", Death.Cause.NoLoss, false, world.GameNow);
            respawned = Death.Respawn(me, "test");
        }
        var left = second.OwnerLeft && world.Corrupted.RunOf(hero.Id) is null && me.Character.DungeonReturn is null && CorruptedDungeons.Current(me.Character) == 60;
        clock.Advance(TimeSpan.FromSeconds(1));
        lock (world.Sync) world.Update();
        clock.Advance(TimeSpan.FromSeconds(31));
        lock (world.Sync) world.Update();
        Check(respawned && left && second.Closed && run.Closed,
              $"dead inside: the respawn leaves the run (early infamy 20, no way back, a new entry open), it closes EmptyCloseSeconds later (got {respawned}/{left}/{second.Closed})");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static (WorldSession Session, FakePeer Peer, JoinResponse Join) Join(World world, Guid key)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = key }, null), encrypted: true);
        Check(peer.Responses is [{ ReturnCode: 0 }], "join answered");
        var join = new JoinResponse();
        AlbionSerializer.FillFields(join, peer.Responses[0].Parameters);
        return (session, peer, join);
    }
}
