using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>The hunts (stageN hunts, analysis/work/stageN/hunts/SPEC.md): a toolkit finds a track and reading it starts a hunt; the target flees at its HP threshold and its kill wins.</summary>
public static class HuntTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("hunts: T5 toolkit find (498) -> wild tracks + MinimapHuntTrackMarkers, reading one (499) -> HuntStarted, a quest spot with trail and ping", FindAndStart),
        ("hunts: the target flees at its HP threshold (BehaviourAI.Escape), its health carries over, the final kill drops loot and gives fame (HuntFinished)", EscapeAndKill),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private sealed record Setup(World World, WorldSession Session, FakePeer Peer, Player Me, Action<double> Tick);

    private static Setup Make()
    {
        var characters = Repository(NewDir());
        var clock = new ManualClock();
        var anchor = DateTime.UtcNow.Ticks;
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = Client.Clusters, MapSeed = 7, GameTicks = () => anchor + clock.Now.Ticks,
        };
        world.MobAi = false;
        Client.Clusters!.Get("0204"); // resolved now: the Join fills the map at once
        var hero = EventTest.NewHero(characters, new Vector2(0, 0));
        hero.Equipment[0] = new InventoryItem("T5_2H_TOOL_TRACKING");
        Check(characters.Save(hero), "toolkit equipped");
        var (session, peer) = EventTest.Join(world, hero.Id);
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        Tick(0.1);
        return new Setup(world, session, peer, session.Player!, Tick);
    }

    private static OperationResponse? Send(Setup s, AlbionRequest req, int id)
    {
        var count = s.Peer.Responses.Count;
        lock (s.World.Sync) s.Session.OnRequest(Request(req, id), encrypted: false);
        return s.Peer.Responses.Count > count ? s.Peer.Responses[^1] : null;
    }

    private static void FindAndStart()
    {
        var s = Make();
        var data = s.World.Hunts.Data!;
        var ed = data.Target("EARTHDAUGHTER_SOLO_T5");
        Check(data.Targets.Count == 71 && ed is { MobId: "T5_MOB_TR_SILVER_KEEPER_EARTHDAUGHTER_BOSS", TrackTile: "TRACK_SILVER_SOLO" }
              && data.HuntFor(ed, 5)?.Name == "SILVER_SOLO_T5" && data.Hunt("BEGINNER_SOLO")?.Index == 0
              && data.Quest("MAIN_SOLO_SHORT_ESCAPE_80") is { Kind: HuntQuestKind.Escape, EscapeAtHealth: 0.8, EscapeOnThreatCount: 4 }
              && data.Quest("FINAL_SOLO_SIMPLE") is { Kind: HuntQuestKind.Final, Fame: 450 } && data.Quest("MAIN_GOTO_SHORT_MEDIUM_RING")?.Kind == HuntQuestKind.Track,
              $"dynamichunt.xml: 71 targets, EARTHDAUGHTER_SOLO_T5 -> SILVER_SOLO_T5, escape 80 % / final 450 fame / goto track quests (got {data.Targets.Count} targets, {data.Hunts.Count} hunts)");

        var mark = s.Peer.Events.Count;
        var find = Send(s, new UseFindTrackSpellFromItemPrepareRequest { ah9 = s.Me.Inventory.Equipment[0]!.Id }, 10);
        s.Tick(7.1);
        var markers = s.Peer.Events.Skip(mark).LastOrDefault(e => Code(e) == (int)EventCodes.MinimapHuntTrackMarkers)?.Parameters;
        var wild = s.World.Hunts.WildTracks;
        Check(find is { ReturnCode: 0 } && markers is not null
              && wild.Count == Hunts.WildPerFind && ((int[])markers[2]!).Length == wild.Count && wild.All(t => t.HuntId == Guid.Empty && t.Hunt.Type == "solo"),
              $"find: rc 0, after the 6.5 s cast MinimapHuntTrackMarkers of {wild.Count} wild tracks (got rc {find?.ReturnCode}: {string.Join(", ", wild)})");

        var track = wild[0];
        lock (s.World.Sync) s.Me.Teleport(track.Position + new Vector2(2, 0), 0, DateTime.UtcNow);
        mark = s.Peer.Events.Count;
        var read = Send(s, new InteractWithTrackStartRequest { wf = s.World.GameNow, qf = track.Id }, 11);
        s.Tick(track.Hunt.TrackingTime * (1 - Hunts.ToolTimeReduction) + 0.2);
        var started = s.Peer.Events.Skip(mark).FirstOrDefault(e => Code(e) == (int)EventCodes.HuntStarted)?.Parameters;
        var hunt = s.World.Hunts.Of(s.Me.Character.Id);
        Check(read is { ReturnCode: 0 } && s.Peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.InteractWithTrackStart)
              && s.Peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.HuntTrackUsed) && started is not null && hunt is not null
              && new Guid((byte[])started[0]!) == hunt.Id && Convert.ToInt32(started[1]) == track.Hunt.Index && (string?)started[2] == "0204"
              && hunt.Quest is not null && hunt.Footprints.Count == 2 && Vector2.Distance(hunt.Location, track.Position) is >= 50 and <= 450
              && s.Peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.MiniMapPing),
              $"reading the track: InteractWithTrackStart, HuntTrackUsed, HuntStarted {{hunt, {track.Hunt.Index}, 0204}}, quest {hunt?.Quest?.Name} {Vector2.Distance(hunt?.Location ?? default, track.Position):0} m on with 2 footprints and a ping");
    }

    private static void EscapeAndKill()
    {
        var s = Make();
        var data = s.World.Hunts.Data!;
        var target = data.Target("EARTHDAUGHTER_SOLO_T5")!;
        var track = new HuntTrackObject(s.World.ObjectIds.Next(), "0204", s.Me.PositionAt(DateTime.UtcNow), 0, target, data.Hunt("SILVER_SOLO_T5")!, Guid.Empty);
        Hunts.Hunt hunt;
        lock (s.World.Sync) hunt = s.World.Hunts.Start(s.Me, track)!;
        bool escaped = false, fled = false;
        float hpAfter = 0;
        for (var step = 0; step < 30 && s.World.Hunts.Of(s.Me.Character.Id) is not null; step++)
        {
            var q = hunt.Quest!;
            if (q.Kind == HuntQuestKind.Track)
            {
                lock (s.World.Sync) s.Me.Teleport(hunt.Track!.Position + new Vector2(1, 0), 0, DateTime.UtcNow);
                Send(s, new InteractWithTrackStartRequest { wf = s.World.GameNow, qf = hunt.Track.Id }, 20 + step);
                s.Tick(17);
                continue;
            }
            var mob = hunt.TargetMob!;
            lock (s.World.Sync) s.Me.Teleport(mob.Position + new Vector2(3, 0), 0, DateTime.UtcNow);
            if (q.Kind == HuntQuestKind.Escape && !escaped && q.EscapeAtHealth > 0)
            {
                var count = hunt.QuestCount;
                lock (s.World.Sync) Combat.Damage(s.Me, mob, mob.Health.At(s.World.GameNow) - mob.Health.Max * (q.EscapeAtHealth - 0.02), EffectType.Physical, HitOrigin.Direct, 0, s.World.GameNow);
                s.Tick(0.1);
                fled = mob.AI is BehaviourAI { Escaping: true } && hunt.EscapeAt is not null;
                s.Tick(16);
                s.Tick(0.1);
                escaped = mob.Removed && !mob.Dead && hunt.QuestCount == count + 1;
                hpAfter = hunt.HpTarget;
                continue;
            }
            if (q.Kind == HuntQuestKind.Escape)
            {
                lock (s.World.Sync) hunt.QuestCount = hunt.Definition.MaxQuests; // enough escapes: the final next
                lock (s.World.Sync) s.World.Hunts.Escape(hunt, "test");
                s.Tick(16);
                s.Tick(0.1);
                continue;
            }
            var mark = s.Peer.Events.Count;
            var fame = s.Me.Character.Fame;
            lock (s.World.Sync) Combat.Damage(s.Me, mob, 1e9, EffectType.Physical, HitOrigin.Direct, 0, s.World.GameNow);
            s.Tick(0.1);
            var events = s.Peer.Events.Skip(mark).ToList();
            Check(mob.Dead && s.World.Hunts.Of(s.Me.Character.Id) is null && events.Any(e => Code(e) == (int)EventCodes.HuntFinished && new Guid((byte[])e.Parameters[0]!) == hunt.Id)
                  && events.Any(e => Code(e) == (int)EventCodes.NewLoot) && s.Me.Character.Fame - fame >= 450 * 10000,
                  $"the final kill of {mob.Template.Name}: loot bag (NewLoot {events.Any(e => Code(e) == (int)EventCodes.NewLoot)}), +{(s.Me.Character.Fame - fame) / 10000.0:0} fame (kill + 450 x 1.1), HuntFinished ({events.Any(e => Code(e) == (int)EventCodes.HuntFinished)}), the hunt over");
            break;
        }
        Check(escaped && fled && hpAfter is > 0 and < 0.8f,
              $"an escape quest: hurt below its threshold the target flees by BehaviourAI.Escape, leaves, the trail goes on with its health {hpAfter:0.##} (fled {fled}, escaped {escaped})");
        Check(s.World.Hunts.Of(s.Me.Character.Id) is null, "the hunt is over");
    }
}
