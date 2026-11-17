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
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>Lore snippets (analysis/work/stageR/lore/SPEC.md): lore.xml, the objects of 0201, the claim, the refusals and the GM reset.</summary>
public static class LoreTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("lore: lore.xml in file order (TEST_LORE_1 0, KEEPER_01 1, DRAGON_12 33); 0201's L_SW_focusMOB_Keeper_01 places KEEPER_18: 673 {3 LORE_SPOT, 4 18} then 674 {1 false}", Objects),
        ("lore: a claim (simple action 11) rc 0, 492, after 5 s 494, 675 {0 18}, 674 true, op 542 true, JOURNAL_LORE_KEEPER_06 done, saved", Claim),
        ("lore: too far 17808, claimed 17800, a cancel 493; .lore reset 674 false + 676; LoreSnippets:Enabled false places none", Refusals),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    /// <summary>A world on capture 002835's day with a character standing next to 0201's KEEPER_18 snippet, joined.</summary>
    private sealed class Rig
    {
        public required World World;
        public required ManualClock Clock;
        public required FakePeer Peer;
        public required WorldSession Session;
        public required Player Me;
        public required ICharacterRepository Characters;

        public LoreSnippetObject Snippet => LoreSnippets.Of(World.Maps.Find("0201")!).Single(o => o.Snippet.Name == "KEEPER_18");

        public void Tick(double seconds)
        {
            Clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (World.Sync) World.Update();
        }

        public OperationResponse? Send(AlbionRequest req, int id)
        {
            var count = Peer.Responses.Count;
            lock (World.Sync) Session.OnRequest(Request(req, id), encrypted: false);
            return Peer.Responses.Count > count ? Peer.Responses[^1] : null;
        }

        public DoSimpleActionStartRequest Start(byte counter) => new() { wf = World.GameNow, jt = counter, ju = LoreSnippets.ClaimAction, jv = Snippet.Id, jx = Snippet.Position };
    }

    private static Rig Make(bool enabled = true, Vector2? offset = null)
    {
        var store = ResourceNodeTest.Day(Capture002835Key57);
        _ = store.Journal;
        var members = store.Get("0201").Critical.Where(m => m.Type == "loresnippet" && m.Guid is not null).ToList();
        Check(members.Count == 1, $"0201 has one loresnippet member in world.xml (got {string.Join(", ", members.Select(m => $"{m.Path} {m.Tile} {m.Position} {m.Components.FirstOrDefault()}"))})");
        var member = members[0];
        var characters = Repository(NewDir());
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true, LoreSnippetsEnabled = enabled }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = store, MapSeed = 5,
        };
        world.MobAi = false;
        var c = StarterKit.Default.NewCharacter("L" + Guid.NewGuid().ToString("N")[..10]);
        var at = member.Position + (offset ?? new Vector2(2, 0));
        (c.Cluster, c.PositionX, c.PositionY) = ("0201", at.X, at.Y);
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
        var rig = new Rig { World = world, Clock = clock, Peer = peer, Session = session, Me = session.Player!, Characters = characters };
        rig.Tick(0.1);
        return rig;
    }

    private static void Objects()
    {
        var data = ResourceNodeTest.Day(Capture002835Key57).Lore;
        Check(data.All.Count == 34 && data.At(0) is { Name: "TEST_LORE_1", Debug: true } && data.Find("KEEPER_01")?.Index == 1 && data.Find("DRAGON_12")?.Index == 33
              && data.Find("KEEPER_18") is { Index: 18, Title: "@LORE_KEEPER_18_TITLE", Debug: false } k && k.ClaimDuration == TimeSpan.FromSeconds(5),
              $"lore.xml: 34 snippets numbered in file order, the 5 s default claim (got {data.All.Count})");
        var rig = Make();
        var s = rig.Snippet;
        var created = rig.Peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.NewLoreSnippetObject && Convert.ToInt64(e.Parameters[0]) == s.Id)?.Parameters;
        var state = rig.Peer.Events.FirstOrDefault(e => Code(e) == (int)EventCodes.LoreSnippetObjectStateUpdate && Convert.ToInt64(e.Parameters[0]) == s.Id)?.Parameters;
        Check(created is not null && created[3] is "LORE_SPOT" && Convert.ToInt32(created[4]) == 18 && state is not null && state[1] is false,
              "NewLoreSnippetObject {0 id, 3 LORE_SPOT, 4 18} and LoreSnippetObjectStateUpdate {0 id, 1 false} for a character that has not claimed it");
    }

    private static void Claim()
    {
        var rig = Make();
        var s = rig.Snippet;
        var mark = rig.Peer.Events.Count;
        var start = rig.Send(rig.Start(3), 10);
        var started = rig.Peer.Events.Skip(mark).FirstOrDefault(e => Code(e) == (int)EventCodes.DoSimpleActionStart)?.Parameters;
        rig.Tick(2);
        var early = rig.Peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.DoSimpleActionFinished);
        rig.Tick(3.5);
        var after = rig.Peer.Events.Skip(mark).ToList();
        var finished = after.FirstOrDefault(e => Code(e) == (int)EventCodes.DoSimpleActionFinished)?.Parameters;
        var claimed = after.FirstOrDefault(e => Code(e) == (int)EventCodes.LoreSnippedClaimed)?.Parameters;
        var state = after.LastOrDefault(e => Code(e) == (int)EventCodes.LoreSnippetObjectStateUpdate)?.Parameters;
        Check(start is { ReturnCode: 0 } && Convert.ToInt32(start.Parameters[0]) == 3 && started is not null && Convert.ToInt32(started[3]) == LoreSnippets.ClaimAction
              && Convert.ToInt64(started[5]) == s.Id && !early && finished is not null && Convert.ToInt32(finished[1]) == 3 && Convert.ToInt64(finished[3]) == s.Id
              && claimed is not null && Convert.ToInt32(claimed[0]) == 18 && state is not null && state[1] is true,
              "rc 0 {0 3}, DoSimpleActionStart {3 11, 5 the snippet}, nothing before 5 s, then DoSimpleActionFinished {1 3, 3 the snippet}, LoreSnippedClaimed {0 18}, 674 {1 true}");
        var states = rig.Send(new RequestLoreSnippetStatesRequest { aag = [17, 18] }, 11);
        Check(states is { ReturnCode: 0 } && states.Parameters[0] is int[] { Length: 2 } ids && ids[0] == 17 && ids[1] == 18
              && states.Parameters[1] is bool[] { Length: 2 } flags && !flags[0] && flags[1],
              "RequestLoreSnippetStates [17, 18] -> [false, true]");
        Check(rig.Me.Character.Goals?.Done.ContainsKey("JOURNAL_LORE_KEEPER_06") == true
              && after.Any(e => Code(e) == (int)EventCodes.AlbionJournalAchievementCompleted && (string)e.Parameters[0] == "JOURNAL_LORE_KEEPER_06"),
              "the journal: JOURNAL_LORE_KEEPER_06 (loresnippet KEEPER_18) completed");
        rig.World.Saves.Flush();
        Check(rig.Characters.Find(rig.Me.Character.Id)?.Goals?.Lore.Contains("KEEPER_18") == true, "the claim is saved with the character");
    }

    private static void Refusals()
    {
        var far = Make(offset: new Vector2(20, 0));
        Check(far.Send(far.Start(1), 10) is { ReturnCode: LoreSnippets.TooFar }, "20 m away: 17808");

        var rig = Make();
        var mark = rig.Peer.Events.Count;
        Check(rig.Send(rig.Start(1), 10) is { ReturnCode: 0 } && rig.Send(new DoSimpleActionCancelRequest { wf = rig.World.GameNow, js = 1 }, 11) is { ReturnCode: 0 }, "start, then cancel: rc 0");
        rig.Tick(6);
        var events = rig.Peer.Events.Skip(mark).ToList();
        Check(events.Any(e => Code(e) == (int)EventCodes.DoSimpleActionCancel) && !events.Any(e => Code(e) == (int)EventCodes.LoreSnippedClaimed)
              && !LoreSnippets.IsClaimed(rig.Me.Character, "KEEPER_18"), "a cancelled claim: DoSimpleActionCancel, no claim");

        lock (rig.World.Sync) rig.World.Lore.Claim(rig.Me, rig.Snippet.Snippet);
        Check(rig.Send(rig.Start(2), 12) is { ReturnCode: LoreSnippets.Refused }, "claimed already: 17800");
        mark = rig.Peer.Events.Count;
        var replies = new List<string>();
        lock (rig.World.Sync)
            rig.World.Commands.Execute(new CommandContext(rig.World, rig.Me, AccountLevel.GameMaster, (t, _) => replies.Add(t)), ".lore reset KEEPER_18");
        events = rig.Peer.Events.Skip(mark).ToList();
        Check(!LoreSnippets.IsClaimed(rig.Me.Character, "KEEPER_18") && events.Any(e => Code(e) == (int)EventCodes.LoreSnippetObjectStateUpdate && e.Parameters[1] is false)
              && events.Any(e => Code(e) == (int)EventCodes.LoreSnippetStatesChangedByCheat),
              $".lore reset: unclaimed, 674 false, 676 ({string.Join(" | ", replies)})");

        var off = Make(enabled: false);
        Check(!LoreSnippets.Of(off.World.Maps.Find("0201")!).Any() && off.Send(new DoSimpleActionStartRequest { ju = LoreSnippets.ClaimAction, jv = 1 }, 10) is { ReturnCode: LoreSnippets.Disabled },
              "LoreSnippets:Enabled false: no snippet placed, a claim 17811");
    }
}
