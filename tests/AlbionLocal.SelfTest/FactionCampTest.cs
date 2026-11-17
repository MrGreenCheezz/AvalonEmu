using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageQ faction camps (analysis/work/stageQ/camps; <see cref="FactionCamps"/>): 1207 on 2026-10-15 (YELLOW_10, at war with YELLOW_15) rolls its
/// camps with the faction warfare layers, whose open quests are the three the capture's LocalQuestInfos listed; a Swamp player at the small camp of
/// slot 31 gets 604 Active / FactionWarfare and 539, the Caerleon guards fight it, the last one's death unlocks the chest, 300 s later the camp is
/// Inactive (604, 603) and after its reactivation timer it stands again.
/// </summary>
public static class FactionCampTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("faction camps: 1207 (10-15) FWARFARE layers with the 3 captured open quests, 0204 Default; 604 + 539 at the Join, Caerleon guards vs a Swamp player, chest unlocked by the last kill, Inactive 300 s later, back after 420-660 s", Flow),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static bool Near(Vector2 a, Vector2 b) => Math.Abs(a.X - b.X) <= 0.01f && Math.Abs(a.Y - b.Y) <= 0.01f;

    private const string Slot = "instanceslot_31";

    private static void Flow()
    {
        var store = ResourceNodeTest.Day(Capture002835Key57);
        var t = store.Get("1207");
        var fw = t.Instances.Where(i => i.Roll is { FeatureLayer: DynamicRoll.FactionWarfare }).Select(i => i.Slot).Order(StringComparer.Ordinal).ToList();
        var open = t.LocalQuestAreas.Where(a => a.Quest is { Open: true }).ToList();
        bool Has(float x, float z, float r, int index) => open.Any(a => Near(a.Position, new Vector2(x, z)) && a.Radius == r && a.Quest!.Index == index);
        Check(fw.SequenceEqual(["instanceslot_01", "instanceslot_04", "instanceslot_28", Slot]) && Has(-139.5f, 10.5f, 75, 13) && Has(49.5f, -270.5f, 35, 12)
              && Has(293.5f, -106.5f, 80, 13) && t.LootChests.Count(c => c.SpawnBehaviour.StartsWith("SPAWNPOINT_FACTION_DYNAMIC_CAMP_", StringComparison.Ordinal)) == 4
              && store.Get("0204").Instances.Any(i => i.Slot == "instanceslot_21" && i.Roll is { FeatureLayer: DynamicRoll.Default, Switched: true }),
              $"1207 (10-15): the 4 rolled camps on FWARFARE, the captured open quests at (-139.5, 10.5) r75 / (49.5, -270.5) r35 / (293.5, -106.5) r80 (index 13 / 12 / 13), 4 faction chest spots; 0204 slot 21 Default (got {string.Join(", ", fw)}; {string.Join(", ", open.Select(a => $"{a.Position} r{a.Radius}"))})");

        var clock = new ManualClock();
        var start = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true }, Repository(NewDir()), Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = store, MapSeed = 7, GameTicks = () => start + clock.Now.Ticks,
        };
        world.MobAi = false;
        var characters = (AlbionLocal.Server.Persistence.SqliteCharacterRepository)world.Characters;
        var c = StarterKit.Default.NewCharacter("F" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY, c.Flag) = ("1207", 49.5f, -270.5f, 5); // Swamp, in the small camp's circle
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
        var map = world.Maps.Find("1207")!;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        for (var i = 0; i < 100 && map.Template is null; i++)
        {
            Thread.Sleep(20);
            Tick(0.1);
        }
        Tick(0.2);
        var me = map.Players.Values.Single();
        List<Dictionary<byte, object?>> Of(EventCodes code) => [.. peer.Events.Where(e => Code(e) == (int)code).Select(e => e.Parameters)];
        bool Dyn(Dictionary<byte, object?> p, int state) => p[0] is Slot && Convert.ToInt32(p[1]) == state && Convert.ToInt32(p[2]) == DynamicRoll.FactionWarfare;

        // The Join: 604 of the slot in view, 539 after the zone's 468 with the four open quests.
        var codes = peer.Events.Select(Code).ToList();
        var infos = Of(EventCodes.LocalQuestInfos).FirstOrDefault();
        Check(Of(EventCodes.DynamicTemplate).Any(p => Dyn(p, DynamicRoll.Active)) && infos is not null
              && codes.IndexOf((int)EventCodes.LocalQuestInfos) > codes.IndexOf((int)EventCodes.FactionWarfareClusterState)
              && infos[1] is byte[] { Length: 4 } index && index.Count(x => x == 13) == 2 && infos[4] is byte[] states && states.All(s => s == 1)
              && infos[6] is string[] args && args.Count(a => a == "{\"RelevantMobCountInArea\":60}") == 2 && args.Count(a => a == "{\"RelevantMobCountInArea\":12}") == 2
              && infos[10] is float[] radii && radii.Contains(35) && radii.Contains(75) && radii.Contains(80),
              "Join: 604 {slot 31, Active, FactionWarfare}; after 468 one 539 (4 quests: index byte[] 13 ×2, Started, RelevantMobCountInArea 60 ×2 / 12 ×2, radii 35 / 75 / 80)");

        var camp = world.FactionCamps.CampOf(map, Slot)!;
        var area = world.WorldChests.AreasOf(map).Single(a => a.Open && a.Spawn.Key.Slot == Slot);
        var guards = camp.Guards.Select(g => g.Mob).ToList();
        Check(camp is { Medium: false } && guards.Count == 12 && guards.All(m => m is { Dead: false, Flagging: FactionCamps.Caerleon } && m.Template.Name.Contains("_MOB_CITY_CAMP_CAERLEON_GUARD_")
                  && m.Foes is { } foes && foes(me) && m.AttackableBy(me)) && area.Marked.Count == 12
              && area.Chest?.Chest is { Template.Name: "FACTION_DYNAMIC_CAMP_SMALL_LC", LockState: 4 } chest && map.Objects.ContainsKey(chest.Id),
              $"the small camp: 12 T4 CAMP_CAERLEON guards (faction 6) that fight and may be hit by the Swamp player, 12 marked; FACTION_DYNAMIC_CAMP_SMALL_LC LockedByQuest (got {camp})");

        var box = area.Chest!.Chest!;
        peer.Events.Clear();
        for (var i = 0; i < guards.Count; i++)
        {
            lock (world.Sync) Combat.Damage(me, guards[i]!, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
            if (i == guards.Count - 2) Check(box.LockState == 4 && Of(EventCodes.LocalQuestProgressUpdate).LastOrDefault() is { } p && Math.Abs(Convert.ToSingle(p[3]) - 11f / 12) < 1e-4,
                "11 of 12 dead: still LockedByQuest, 543 progress 11/12");
        }
        Tick(0.5);
        var done = Of(EventCodes.LocalQuestProgressUpdate).LastOrDefault();
        Check(area.Finished && box.LockState == 6 && done is not null && done[2] is bool[] { Length: 1 } flags && flags[0] && Of(EventCodes.UpdateLootChest).Any(p => Convert.ToInt64(p[0]) == box.Id && Convert.ToInt32(p[1]) == 6)
              && camp.Due is { } due && Math.Abs((due - world.Now).TotalSeconds - 299.5) < 1,
              $"the last one dead: quest Finished (543 done), the chest Unlocked for everyone (UpdateLootChest 6), the camp inactive in 300 s (got {camp})");

        peer.Events.Clear();
        Tick(300);
        Check(camp.Object.State == DynamicRoll.Inactive && Of(EventCodes.DynamicTemplate).Any(p => Dyn(p, DynamicRoll.Inactive))
              && Of(EventCodes.LocalQuestAreaGone).Any(p => Convert.ToInt64(p[0]) == area.Id) && !map.Objects.ContainsKey(box.Id)
              && camp.Due is { } back && (back - world.Now).TotalSeconds is >= 419 and <= 661,
              $"300 s later: 604 {{slot 31, Inactive, FactionWarfare}}, 603 for its quest, the chest gone, back in 420-660 s (got {camp})");

        peer.Events.Clear();
        Tick(661);
        var again = world.WorldChests.AreasOf(map).SingleOrDefault(a => a.Open && a.Spawn.Key.Slot == Slot);
        Check(camp.Object.State == DynamicRoll.Active && Of(EventCodes.DynamicTemplate).Any(p => Dyn(p, DynamicRoll.Active))
              && camp.Guards.All(g => g.Mob is { Dead: false }) && again is { Finished: false, Marked.Count: 12 } && again.Id != area.Id
              && again.Chest?.Chest is { LockState: 4 } fresh && map.Objects.ContainsKey(fresh.Id) && Of(EventCodes.LocalQuestInfos).Count == 1,
              $"after the reactivation timer: 604 Active again, 12 guards anew, a fresh quest (539) and a locked chest (got {camp})");
        world.Dispose();
    }
}
