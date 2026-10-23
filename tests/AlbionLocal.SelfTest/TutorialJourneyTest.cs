using System.Numerics;
using System.Text.Json;
using System.Xml.Linq;
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

public static class TutorialJourneyTest
{
    private static void Check(bool condition, string why) => AlbionTests.Check(condition, why);
    public static IEnumerable<(string, Action)> All => [
        ("tutorial journey: old delivery checkpoint offers shield combat and resumes online gate timer", ShieldAndGate),
        ("tutorial journey: harvest counters are independent and persist without counting held materials", GatherCounters),
        ("tutorial journey: ship hand-in requires all resources and consumes them exactly once", ShipRepair),
        ("tutorial journey: reward delivery is atomic with full inventory and survives reconnect", Rewards),
        ("tutorial journey: captain offers camp mission after soup delivery and reconnect", CaptainSoup),
        ("tutorial journey: native complete request delivers all three weapon choices without the acceptance slot", WeaponCompletion),
        ("tutorial journey: arrival fort requires entering and leaving its authored circle", FortLeave),
        ("tutorial journey: travel UI cannot substitute a validated departure", Departure),
        ("tutorial journey: completed plain achievements unlock bridge and camp objectives after real fame", CompletedAchievements),
        ("tutorial journey: moving clears forest protection and permits a real basic attack on a fox", ForestProtection),
        ("tutorial journey: complete island chain, camp A and bow slot", () => CompleteRoute(0)),
        ("tutorial journey: complete island chain, camp B and sword slot", () => CompleteRoute(1)),
        ("tutorial journey: complete island chain, camp C and firestaff slot", () => CompleteRoute(2)),
    ];
    private sealed class Lab : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly OpeningTutorialData Data = OpeningTutorialData.Load(ClientGameDataDir(), Client.Clusters!);
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Repository;
        public readonly World World;
        public FakePeer Peer = null!;
        public readonly List<FakePeer> Connections = [];
        public WorldSession Session = null!;
        public Player Player => Session.Player!;
        public OpeningTutorialProgress State => Player.Character.Tutorial!;
        public Lab(string cluster = "1008", string? quest = null, int task = 0, string phase = "active", int? branch = null)
        {
            Repository = new SqliteCharacterRepository(Path.Combine(Dir, "save.db"));
            World = new World(new ServerConfig { JoinByCharacterId = true, TutorialOpening = true, PremiumMode = PremiumMode.None }, Repository, Client.Items, Client.GameData, Client.Spells)
            { Sync = new object(), Clock = Clock, GameTicks = () => 638000000000000000L + Clock.Now.Ticks, Clusters = Client.Clusters, OpeningTutorial = Data };
            var c = Data.Create("JourneyProbe", Client.Items, Client.GameData!);
            if (branch is { } first) { var bytes = c.Id.ToByteArray(); bytes[0] = (byte)first; c.Id = new Guid(bytes); }
            c.Cluster = cluster;
            c.Tutorial = c.Tutorial! with { Stage = quest is null ? 27 : 28, IntroStarted = true,
                JourneyQuest = quest is null ? null : TutorialJourneyData.Prefix + quest, JourneyTask = task,
                JourneyPhase = quest is null ? "offered" : phase };
            Repository.Create(c); Rejoin();
        }
        public void Rejoin()
        {
            Session?.OnDisconnected("journey reconnect"); World.Saves.Flush();
            Peer = new FakePeer(); Connections.Add(Peer); Session = new WorldSession(Peer, World);
            Session.OnRequest(Request(new JoinRequest { ss = Repository.Find("JourneyProbe")!.Id }, 1), false);
            Check(Session.Player is not null, "journey checkpoint joined");
        }
        public Item Give(string name, int count = 1)
        {
            var type = Client.Items.Find(name)!;
            var slots = Client.Spells?.SlotsOf(name);
            var item = new Item(World.ObjectIds.Next(), type, count, 1, type.FullDurabilityRaw,
                Enumerable.Repeat((short)-1, slots?.Active.Length ?? 0).ToArray(), []);
            Player.Inventory.Bag.Put(Player.Inventory.Bag.FreeSlot(), item); Player.SaveItems(); return item;
        }
        public TutorialQuestGiver Npc(string reference)
        {
            var npc = Player.Map!.Objects.Values.OfType<TutorialQuestGiver>().Single(n => n.Reference == reference);
            Player.Teleport(npc.Position, 0, DateTime.UtcNow); return npc;
        }
        public short Act(string reference, int action, byte slot = 0, byte selection = 0)
        {
            var npc = Npc(reference);
            return OpeningTutorial.Request(Player, npc.Id, action, slot, selection);
        }
        public void Go(string marker)
        {
            var point = Data.Journey!.Markers(Player.Map!.Cluster, marker).First();
            Player.Teleport(point.Position, 0, DateTime.UtcNow); OpeningTutorial.Update(Player);
        }
        public void Travel(string destination)
        {
            while (Player.Map!.Cluster != destination)
            {
                var next = Player.Map.Cluster == "1011" ? destination : "1011";
                var exit = Player.Map.Objects.Values.OfType<Exit>().Single(e => e.World.TargetCluster == next);
                Clock.Advance(TimeSpan.FromSeconds(20));
                Player.Teleport(exit.Position, 0, DateTime.UtcNow);
                Session.OnRequest(Request(new ChangeClusterRequest { ee = exit.Id }, 40), false);
                Check(Peer.Responses.Last().ReturnCode == 0, "authored island exit accepted: " + Peer.Responses.Last().ReturnCode);
                Rejoin();
                Check(Player.Map!.Cluster == next, "transfer checkpoint restores logical destination");
            }
        }
        public void Buy(string name, int count = 1, bool collect = true)
        {
            Travel("1011");
            var market = Player.Map!.Objects.Values.OfType<Building>().Single(b => b.Template.Kind == "marketplacebuilding");
            Player.Teleport(market.Position, 0, DateTime.UtcNow); Player.Visibility.Refresh(market.Position);
            Session.OnRequest(Request(new AuctionBuyOfferRequest { bqs = market.Id, bn2 = count,
                bn3 = TutorialMerchant.FirstId + Client.Items.Find(name)!.Index }, 41), false);
            Check(Peer.Responses.Last().ReturnCode == 0, "real local market purchase: " + name);
            if (!collect) return;
            Session.OnRequest(Request(new AuctionFetchFinishedAuctionsRequest(), 42), false);
            Check(Peer.Responses.Last().ReturnCode == 0, "real collection: " + name);
        }
        public void Dispose() { World.Dispose(); Repository.Dispose(); Directory.Delete(Dir, true); }
    }
    private static void ForestProtection()
    {
        using var lab = new Lab("1009", "K", 2);
        var fox = lab.Player.Map!.Objects.Values.OfType<Mob>().First(m => m.Template.Name == "T2_MOB_HIDE_FOREST_FOX_TUTORIAL");
        var origin = fox.Position + new Vector2(1, 0);
        lab.Player.Teleport(origin, 0, DateTime.UtcNow);
        lab.Player.Visibility.Refresh(origin);
        var sword = lab.Give("T1_MAIN_SWORD");
        lab.Player.Inventory.Bag.Put(lab.Player.Inventory.Bag.IndexOf(sword), null);
        lab.Player.Inventory.Equipment.Put(0, sword);
        void Apply(string name)
        {
            var spell = Client.Spells!.Template(name)!;
            SpellCasts.Execute(new SpellCasts.SpellContext(lab.Player, lab.Player.Map!, spell, 0, 100, lab.Player,
                lab.Player.PositionAt(DateTime.UtcNow), lab.World.GameNow), spell);
        }
        Apply("TUTORIAL_MOB_INVISIBILITY");
        lab.Clock.Advance(TimeSpan.FromSeconds(10)); lab.World.Update();
        Check(!SpellEffects.Stunned(lab.Player, lab.World.GameNow) && SpellEffects.CannotAttack(lab.Player, lab.World.GameNow),
            "stun ends normally but stationary tutorial protection still blocks basic attacks");
        lab.Session.OnRequest(Request(new AttackStartRequest { wf = lab.World.GameNow, co = fox.Id }, 71), false);
        Check(lab.Peer.Responses.Last().ReturnCode == WorldSession.AttackInvalidTarget, "basic attack refused while protection is active");
        lab.Player.Teleport(origin + new Vector2(0.05f, 0), 0, DateTime.UtcNow); lab.World.Update();
        Check(SpellEffects.CannotAttack(lab.Player, lab.World.GameNow), "less than the authored movement threshold preserves protection");
        lab.Player.Teleport(origin + new Vector2(0.2f, 0), 0, DateTime.UtcNow); lab.World.Update();
        Check(!SpellEffects.CannotAttack(lab.Player, lab.World.GameNow) && !SpellEffects.Invincible(lab.Player, lab.World.GameNow)
            && !SpellEffects.InvisibleToMobs(lab.Player, lab.World.GameNow), "movement clears all forest introduction protection effects");
        lab.Player.Teleport(fox.Position + new Vector2(0.5f, 0), 0, DateTime.UtcNow);
        lab.Player.Visibility.Refresh(fox.Position);
        var health = fox.Health.At(lab.World.GameNow);
        var responses = lab.Peer.Responses.Count;
        lab.Session.OnRequest(Request(new AttackStartRequest { wf = lab.World.GameNow, co = fox.Id }, 72), false);
        Check(lab.Peer.Responses.Count == responses, "real basic attack is accepted after movement");
        lab.Clock.Advance(TimeSpan.FromSeconds(1)); lab.World.Update();
        Check(fox.Health.At(lab.World.GameNow) < health, "basic sword attack damages the fox");
        origin = lab.Player.PositionAt(DateTime.UtcNow);
        Apply("TUTORIAL_MOB_INVISIBILITY_NOSTUN");
        lab.Player.Teleport(origin + new Vector2(0.2f, 0), 0, DateTime.UtcNow); lab.World.Update();
        Check(!SpellEffects.CannotAttack(lab.Player, lab.World.GameNow) && SpellEffects.Invincible(lab.Player, lab.World.GameNow),
            "each sub-effect uses its own threshold: 0.1 m for attacks, 2 m for invincibility");
        lab.Player.Teleport(origin + new Vector2(2.1f, 0), 0, DateTime.UtcNow); lab.World.Update();
        Check(!SpellEffects.Invincible(lab.Player, lab.World.GameNow) && !SpellEffects.InvisibleToMobs(lab.Player, lab.World.GameNow),
            "later tutorial protection also ends at its authored distance");
    }
    private static void CompletedAchievements()
    {
        var board = Client.GameData!.Board!;
        var start = board.Find("START")!.Id;
        using (var lab = new Lab("1008", "E", 3))
        {
            var targets = lab.Player.Map!.Objects.Values.OfType<Mob>().Where(m => !m.Dead && m.Template.Faction == "HERETIC")
                .OrderByDescending(m => m.Template.Fame / Math.Max(1, m.Template.MinDivisor)).Take(4).ToArray();
            Check(targets.Length == 4, "bridge has four real enemies");
            foreach (var mob in targets)
                Combat.Damage(lab.Player, mob, 1e9, EffectType.Physical, HitOrigin.Direct, 0, lab.World.GameNow);
            Check(lab.Player.Progression!.Completed.Contains(start) && !lab.Player.Progression.Levels.ContainsKey(start),
                "combat fame finishes START and removes its in-progress level");
            OpeningTutorial.Update(lab.Player);
            Check(lab.State.JourneyTask == 4, "four kills and completed START advance to the captain marker");
            lab.Rejoin();
            Check(lab.State.JourneyTask == 4, "bridge progress survives reconnect without repeating kills");
        }
        using (var lab = new Lab("1008", "E", 3))
        {
            lab.Player.Character.Tutorial = lab.State with { JourneyCounters = "{\"0\":4,\"1\":0}" };
            OpeningTutorial.Update(lab.Player);
            Check(lab.State.JourneyTask == 3, "four kills alone cannot skip an incomplete achievement");
            Destiny.Award(lab.Player, 45 * DestinyBoard.A7m, Destiny.Kill(lab.Player, 1));
            Check(lab.Player.Progression!.Completed.Contains(start), "normal fame award completes START");
            lab.World.Saves.SaveNow(lab.Player.Character); lab.Rejoin();
            OpeningTutorial.Update(lab.Player);
            Check(lab.State.JourneyTask == 4, "existing saved four kills recover when START is already completed");
        }
        using (var lab = new Lab("1009", "MA", 6))
        {
            var progression = lab.Player.Progression!;
            board.SetLevel(progression, start, 1);
            var sword = lab.Give("T1_MAIN_SWORD");
            lab.Player.Inventory.Bag.Put(lab.Player.Inventory.Bag.IndexOf(sword), null);
            lab.Player.Inventory.Equipment.Put(0, sword);
            OpeningTutorial.Update(lab.Player);
            Check(lab.State.JourneyTask == 6, "camp still waits for its own achievement");
            Destiny.Award(lab.Player, 250 * DestinyBoard.A7m, Destiny.Kill(lab.Player, 2));
            var combat = board.Find("COMBAT_TRAINEE")!.Id;
            Check(progression.Completed.Contains(combat) && !progression.Levels.ContainsKey(combat),
                "combat trainee uses the same completed-node representation");
            OpeningTutorial.Update(lab.Player);
            Check(lab.State.JourneyTask == 7, "completed combat trainee also advances the later camp quest");
        }
    }
    private static void ShieldAndGate()
    {
        using var lab = new Lab();
        var npc = lab.Npc("WOUNDEDCRAFTER");
        Check(OpeningTutorial.NpcInfo(lab.Player, npc).dr4, "legacy stage27 offers actual continuation");
        Check(lab.Act("WOUNDEDCRAFTER", 3) != 0, "first crafting reward cannot be claimed twice");
        Check(lab.Act("WOUNDEDCRAFTER", 1) == 0 && lab.State.JourneyTask == 0, "combat quest accepted explicitly");
        OpeningTutorial.Update(lab.Player); Check(lab.State.JourneyTask == 0, "unowned shield does not count");
        var shield = lab.Give("T1_OFF_SHIELD");
        lab.Session.OnRequest(Request(new InventoryMoveItemRequest { ru = lab.Player.Inventory.Bag.IndexOf(shield), rv = lab.Player.Inventory.Bag.Id,
            rx = 1, ry = lab.Player.Inventory.Equipment.Id }, 3), false);
        OpeningTutorial.Update(lab.Player); Check(lab.State.JourneyTask == 1, "real equipment state satisfies shield");
        lab.Go("GATE"); lab.Clock.Advance(TimeSpan.FromSeconds(2)); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 1, "gate requires authored three seconds");
        lab.Rejoin(); lab.Go("GATE"); lab.Clock.Advance(TimeSpan.FromSeconds(2)); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 1, "reconnect cannot combine non-continuous stays");
        lab.Clock.Advance(TimeSpan.FromSeconds(1)); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 2, "fresh continuous stay opens combat objective");
        Check(TutorialJourney.BlockerState(lab.Player, "HERETICGATE") == 0, "authored blocker state persists");
        var questId = lab.State.QuestId; lab.Rejoin();
        Check(lab.State.JourneyTask == 2 && lab.State.QuestId == questId, "rejoin retains active quest and task");
    }
    private static void GatherCounters()
    {
        using var lab = new Lab("1009", "K", 3);
        lab.Give("T2_WOOD", 6); lab.Give("T2_HIDE", 6); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 3, "held resources cannot substitute actual gathering");
        TutorialJourney.Gathered(lab.Player, "T2_WOOD", 6);
        Check(lab.State.JourneyTask == 3, "first objective alone does not finish parallel task");
        var snapshot = lab.Player.Character.Clone();
        TutorialJourney.Gathered(lab.Player, "T2_HIDE", 5);
        Check(snapshot.Tutorial!.JourneyCounters != lab.State.JourneyCounters, "async snapshot counters remain immutable");
        lab.Rejoin(); Check(lab.State.JourneyTask == 3, "partial second objective persists");
        TutorialJourney.Gathered(lab.Player, "T1_WOOD", 99); Check(lab.State.JourneyTask == 3, "wrong item never counts");
        TutorialJourney.Gathered(lab.Player, "T2_HIDE", 1);
        Check(lab.State.JourneyTask == 4, "actual gathers complete both objectives after reconnect");
    }
    private static void ShipRepair()
    {
        using var lab = new Lab("1011", "L", 4);
        foreach (var (name, count) in new[] { ("T2_PLANKS", 6), ("T2_METALBAR", 6), ("T2_CLOTH", 4) }) lab.Give(name, count);
        Check(lab.Act("SHIP_REPAIRMAN", 0) == 0 && lab.State.JourneyTask == 4, "conversation alone cannot pay ship resources");
        Check(lab.Act("SHIP_REPAIRMAN", 2) != 0 && lab.Player.Inventory.Bag.Items.Sum(i => i.Count) == 16,
            "incomplete trade refuses without consuming any stack");
        lab.Give("T2_LEATHER", 4);
        Check(lab.Act("SHIP_REPAIRMAN", 2) == 0 && lab.State.JourneyTask == 5 && !lab.Player.Inventory.Bag.Items.Any(),
            "accepted trade removes exact authored materials and advances");
        Check(lab.Act("SHIP_REPAIRMAN", 2) != 0, "repeated trade cannot pay or advance twice");
        lab.Rejoin(); Check(lab.State.JourneyTask == 5 && !lab.Player.Inventory.Bag.Items.Any(), "trade checkpoint and consumption persist together");
        OpeningTutorial.Update(lab.Player); lab.Clock.Advance(TimeSpan.FromSeconds(9)); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 5, "ship repair does not skip its ten-second online wait");
        lab.Clock.Advance(TimeSpan.FromSeconds(1)); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 6, "ship repair wait completes online");
    }
    private static void Rewards()
    {
        using var lab = new Lab("1011", "I", 2, "ready");
        while (lab.Player.Inventory.Bag.FreeSlot() >= 0) lab.Give("T1_WOOD");
        Check(lab.Act("WOUNDEDCRAFTER_SECONDENCOUNTER", 3) != 0 && lab.State.JourneyPhase == "ready", "full bag keeps pending delivery");
        lab.Player.Inventory.Bag.Put(0, null); lab.Player.Inventory.Bag.Put(1, null);
        Check(lab.Act("WOUNDEDCRAFTER_SECONDENCOUNTER", 3) == 0 && lab.State.JourneyPhase == "offered", "both authored tool rewards fit and commit");
        Check(lab.Player.Inventory.Bag.Items.Count(i => i.Template.Name is "T1_2H_TOOL_SICKLE" or "T1_2H_TOOL_KNIFE") == 2, "one copy of each tool");
        Check(lab.Act("WOUNDEDCRAFTER_SECONDENCOUNTER", 3) != 0, "duplicate delivery rejected");
        lab.Rejoin(); Check(lab.State.JourneyPhase == "offered"
            && lab.Player.Inventory.Bag.Items.Count(i => i.Template.Name is "T1_2H_TOOL_SICKLE" or "T1_2H_TOOL_KNIFE") == 2, "reward and checkpoint survive restart");
    }
    private static void CaptainSoup()
    {
        for (var branch = 0; branch < 3; branch++)
        {
            using var lab = new Lab("1011", "L", 6, branch: branch);
            var startingSilver = lab.Player.Character.Silver;
            var completedInstance = lab.State.QuestId;
            Guid? offeredInstance = null;
            Check(lab.Act("CAPTAIN_TIA", 0) == 0 && lab.State.JourneyPhase == "ready", "captain conversation prepares resource quest delivery");
            Check(lab.Act("CAPTAIN_TIA", 3) == 0 && lab.State.JourneyPhase == "offered", "resource quest completes at captain");
            Check(lab.Player.Inventory.Bag.Items.Where(i => i.Template.Name == "T1_MEAL_SOUP").Sum(i => i.Count) == 3,
                "captain grants the three authored soups");
            void CheckOffer()
            {
                var expected = lab.Data.Journey!.Find(TutorialJourneyData.Prefix + new[] { "MA", "MB", "MC" }[branch])!;
                var offer = lab.Peer.Events.Last(e => (EventCodes)Convert.ToInt16(e.Parameters[ReservedKeys.EventCode]) == EventCodes.QuestGiverQuestOffered
                    && Convert.ToInt32(e.Parameters.GetValueOrDefault((byte)3)) == 0).Parameters;
                var instance = new Guid((byte[])offer[1]!);
                Check(Convert.ToInt32(offer[2]) == expected.Id && instance != Guid.Empty,
                    "captain publishes next camp mission in slot zero");
                // cmh.r(ax2) retains the cached quest definition when the incoming GUID is unchanged.
                Check(instance != completedInstance, "next offer must replace the client's cached soup quest");
                Check(offeredInstance is null || offeredInstance == instance, "offer identity survives queries and reconnect");
                offeredInstance = instance;
                var npc = lab.Npc("CAPTAIN_TIA");
                var info = lab.Peer.Events.Last(e => (EventCodes)Convert.ToInt16(e.Parameters[ReservedKeys.EventCode]) == EventCodes.QuestGiverInfoForPlayer
                    && Convert.ToInt64(e.Parameters[0]) == npc.Id).Parameters;
                var reward = checked((long)expected.Definition.Element("rewards")!.Element("silver")!.Attribute("absolute")! * 10000);
                Check(Convert.ToInt64(info.GetValueOrDefault((byte)3)) >= reward,
                    "captain advertises sufficient silver for the client's next-quest acceptance check");
            }
            CheckOffer();
            Check(lab.Act("CAPTAIN_TIA", 3) != 0, "soup reward cannot be claimed again");
            lab.Rejoin();
            Check(lab.Act("CAPTAIN_TIA", 0) == 0, "existing delivered checkpoint reopens captain dialogue");
            CheckOffer();
            Check(lab.Act("CAPTAIN_TIA", 1) == 0 && lab.State.JourneyPhase == "active"
                && lab.State.JourneyTask == 0, "explicit acceptance starts destiny-board objective");
            Check(lab.Player.Inventory.Bag.Items.Where(i => i.Template.Name == "T1_MEAL_SOUP").Sum(i => i.Count) == 3,
                "reconnect and acceptance preserve reward without duplication");
            Check(lab.Player.Character.Silver == startingSilver, "agent budget does not credit the player before completing the new quest");
        }
    }
    private static void WeaponCompletion()
    {
        foreach (var slot in new byte[] { 0, 1, 2 })
        {
            using var lab = new Lab("1011", "MA", phase: "offered");
            short Op(int action, byte requestSlot = 0, string giver = "CAPTAIN_TIA")
            {
                var npc = lab.Npc(giver);
                lab.Session.OnRequest(Request(new QuestGiverRequestRequest { wf = lab.World.GameNow,
                    yz = npc.Id, y0 = action, y1 = requestSlot }, 99), false);
                return lab.Peer.Responses.Last().ReturnCode;
            }
            Check(Op(1, slot) == 0 && lab.State.JourneySlot == slot, "acceptance selects the authored weapon offer");
            var quest = lab.Data.Journey!.Find(lab.State.JourneyQuest!)!;
            Check(Op(3) != 0, "native completion cannot bypass missing weapon objectives");
            foreach (var resource in quest.Tasks[0].Definition.Descendants("acquireitem"))
                lab.Give((string)resource.Attribute("itemid")!, (int)resource.Attribute("count")!);
            OpeningTutorial.Update(lab.Player);
            Check(lab.State.JourneyTask == 1, "authored refined materials advance to weapon objective");
            var weapon = quest.Tasks[1].Definition.Descendants("acquireitem").Single();
            lab.Give((string)weapon.Attribute("itemid")!);
            OpeningTutorial.Update(lab.Player);
            Check(Op(0) == 0 && lab.State.JourneyPhase == "ready", "captain conversation readies weapon delivery");
            lab.Rejoin();
            Check(Op(3, giver: "WOUNDEDCRAFTER_SECONDENCOUNTER") != 0, "another NPC cannot deliver the active quest");
            Check(Op(3, 3) != 0 && lab.State.JourneyPhase == "ready", "completion rejects non-native offer selector");
            // c32.ah(long) writes action 3 only: y1/y2 remain zero, even for sword/firestaff offers.
            Check(Op(3) == 0 && lab.State.JourneyPhase == "offered", "native Complete button delivers weapon slot " + slot);
            Check(Op(3) != 0, "duplicate completion is refused");
            lab.Rejoin();
            var next = lab.Data.Journey.Successors(quest.Name).Single();
            Check(lab.State.JourneyPhase == "offered" && Op(1) == 0 && lab.State.JourneyQuest == next.Name,
                "delivered checkpoint persists and next equipment mission can be accepted");
        }
    }
    private static void FortLeave()
    {
        using var lab = new Lab("1010", "P", 2);
        var circle = lab.Data.Journey!.Markers("1010", "FORT").First();
        lab.Player.Teleport(circle.Position + new Vector2(circle.Radius + 10, 0), 0, DateTime.UtcNow); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 2, "starting outside is insufficient for triggeronleave");
        lab.Go("FORT"); Check(lab.State.JourneyTask == 2, "entering alone does not finish leave condition");
        lab.Rejoin(); lab.Player.Teleport(circle.Position + new Vector2(circle.Radius + 10, 0), 0, DateTime.UtcNow); OpeningTutorial.Update(lab.Player);
        Check(lab.State.JourneyTask == 3, "leaving after persisted entry opens actual combat objective");
    }
    private static void Departure()
    {
        using var lab = new Lab("1010", "Q", 1);
        Check(!TutorialJourney.TravelAllowed(lab.Player), "minimum total online time remains enforced at the departure checkpoint");
        lab.Player.Character.PlayedMilliseconds = 900000;
        Check(TutorialJourney.TravelAllowed(lab.Player), "completed playtime checkpoint exposes real journey");
        OpeningTutorial.FunctionUsed(lab.Player, 15);
        Check(lab.State.JourneyPhase == "active", "spoofed BuyJourney UI message never completes the tutorial");
        TutorialJourney.Departed(lab.Player);
        Check(lab.State.JourneyPhase == "completed" && lab.State.TutorialState == lab.Data.Tutorial.End.Id, "accepted departure reaches native end state");
        var done = lab.State; TutorialJourney.Departed(lab.Player); Check(lab.State == done, "departure checkpoint is idempotent");
        lab.Rejoin(); Check(lab.State.JourneyPhase == "completed", "finished quest does not reappear after reconnect");
    }

    // This is a server integration fixture, not a simulated claim that the official client was played.
    // Craft/harvest/combat subsystems supply controlled inventory/progression/death results here;
    // the quest engine must evaluate every authored condition and advance itself. NPC requests,
    // purchases, collection and inter-map transfers use their real handlers, without checkpoint writes.
    private static void CompleteRoute(int branch)
    {
        using var lab = new Lab(branch: branch);
        var completed = new List<string>();
        var visited = new HashSet<string>();
        var tasks = 0;
        var learningBefore = lab.Player.Character.LearningPoints;
        var camp = "M" + (char)('A' + branch);
        var weapon = "N" + (char)('A' + branch);
        string A(XElement e, string name, string fallback = "") => (string?)e.Attribute(name) ?? fallback;
        int N(XElement e, string name, int fallback = 1) => int.TryParse(A(e, name), out var n) ? n : fallback;
        string Short(string name) => name[TutorialJourneyData.Prefix.Length..];
        void Equip(Item item)
        {
            var slot = Array.IndexOf(Inventory.SlotNames, item.Template.Slot);
            var old = lab.Player.Inventory.Equipment[slot];
            var bagSlot = lab.Player.Inventory.Bag.IndexOf(item);
            if (bagSlot >= 0) lab.Player.Inventory.Bag.Put(bagSlot, null);
            lab.Player.Inventory.Equipment.Put(slot, item);
            if (old is not null) lab.Player.Inventory.Bag.Put(lab.Player.Inventory.Bag.FreeSlot(), old);
            if (item.Template.Attrs.GetValueOrDefault("twohanded") == "true" && lab.Player.Inventory.Equipment[1] is { } off)
            { lab.Player.Inventory.Equipment.Put(1, null); lab.Player.Inventory.Bag.Put(lab.Player.Inventory.Bag.FreeSlot(), off); }
            lab.Player.SaveItems();
        }
        Item Ensure(string name, int count)
        {
            var owned = lab.Player.Inventory.Bag.Items.Concat(lab.Player.Inventory.Equipment.Items).Where(i => i.Template.Name == name).ToArray();
            var missing = count - owned.Sum(i => i.Count);
            return missing > 0 ? lab.Give(name, missing) : owned.First();
        }
        void Marker(XElement mission, string suffix)
        {
            var name = A(mission, "locationmarkerid", A(mission, "gotolocationmarkerid"));
            var expectedMap = suffix is "KA" or "P" ? "1010" : suffix is "K" or "MA" or "MB" or "MC" ? "1009" : lab.Player.Map!.Cluster;
            var map = lab.Data.Journey!.Maps.Values.OrderBy(m => m.Id == expectedMap ? 0 : 1)
                .First(m => lab.Data.Journey.Markers(m.Id, name).Any());
            lab.Travel(map.Id); lab.Go(name);
            if (A(mission, "triggeronleave") == "true")
            {
                var marker = lab.Data.Journey.Markers(map.Id, name).First();
                lab.Player.Teleport(marker.Position + new Vector2(marker.Radius + 10, 0), 0, DateTime.UtcNow);
            }
            lab.Clock.Advance(TimeSpan.FromSeconds(N(mission, "seconds", 0)));
            OpeningTutorial.Update(lab.Player);
        }
        string Giver(XElement mission, string suffix)
        {
            var reference = suffix is "F" or "L" or "MA" or "MB" or "MC" or "NA" or "NB" or "NC" or "OA" or "OB"
                ? "CAPTAIN_TIA" : "WOUNDEDCRAFTER_SECONDENCOUNTER";
            if (A(mission, "mustaccept") == "true") reference = "SHIP_REPAIRMAN";
            lab.Travel("1011"); return reference;
        }
        void Satisfy(XElement mission, string suffix)
        {
            switch (mission.Name.LocalName)
            {
                case "or":
                    var options = mission.Elements().ToArray();
                    Satisfy(options[options.Length == 3 ? branch : 0], suffix); break;
                case "gotolocation": Marker(mission, suffix); break;
                case "equipitem": Equip(Ensure(A(mission, "itemid"), 1)); break;
                case "acquireitem":
                    var name = A(mission, "itemid", A(mission.Elements("item").FirstOrDefault() ?? new XElement("item"), "id"));
                    if (name == "T2_MOUNT_MULE")
                    { lab.Session.OnRequest(Request(new AuctionFetchFinishedAuctionsRequest(), 42), false); Check(lab.Peer.Responses.Last().ReturnCode == 0, "mule requires actual collection"); }
                    else if (suffix is "OA" or "OB") lab.Buy(name, N(mission, "count"));
                    else Ensure(name, N(mission, "count"));
                    break;
                case "marketbuyitem": lab.Buy(A(mission, "item"), N(mission, "count"), collect: false); break;
                case "gather": TutorialJourney.Gathered(lab.Player, A(mission, "itemid"), N(mission, "count")); break;
                case "unlockachievement":
                    var board = Client.GameData!.Board!;
                    board.SetLevel(lab.Player.Progression!, board.Find(A(mission, "achievement"))!.Id, N(mission, "masterylevel")); break;
                case "killmob":
                    var mobs = lab.Player.Map!.Objects.Values.OfType<Mob>().Where(m => !m.Dead && m.Template.Faction == A(mission, "faction")
                        && m.Template.Tier >= N(mission, "mintier", 0) && m.Template.Tier <= N(mission, "maxtier", int.MaxValue)).Take(N(mission, "count")).ToArray();
                    Check(mobs.Length == N(mission, "count"), "authored map has sufficient required enemies: " + suffix + ":" + lab.State.JourneyTask + "@" + lab.Player.Map.Cluster + " found=" + mobs.Length);
                    foreach (var mob in mobs) Combat.Damage(lab.Player, mob, 1e9, EffectType.Physical, HitOrigin.Direct, 0, lab.World.GameNow);
                    break;
                case "usefunction":
                    if (A(mission, "function") == "BuyJourney")
                    {
                        lab.Travel("1010");
                        var planner = lab.Player.Map!.Objects.Values.OfType<Travelpoint>().Single();
                        lab.Player.Teleport(planner.Position, 0, DateTime.UtcNow); lab.Player.Visibility.Refresh(planner.Position);
                        var destination = new[] { "1006", "4006", "0004" }[branch];
                        _ = Client.Clusters!.Get(destination);
                        var source = Transfers.TravelpointKey(planner.Spawn.Guid, "1010");
                        var target = Client.Clusters.World.Find(destination)!.Travelpoints.First(t => Transfers.Route(Client.Clusters.World,
                            source, Transfers.TravelpointKey(t.Id, destination)) is not null);
                        lab.Session.OnRequest(Request(new BuyJourneyRequest { df = planner.Id, dg = Transfers.TravelpointKey(target.Id, destination) }, 44), false);
                        Check(lab.Session.Status == SessionStatus.Transferring && lab.Peer.Responses.Last().ReturnCode == 0,
                            "actual planner commits departure and completion: " + destination + " rc=" + lab.Peer.Responses.Last().ReturnCode);
                        lab.Rejoin();
                        Check(lab.Player.Map!.Cluster == destination, "validated departure joins chosen starter town");
                    }
                    else OpeningTutorial.FunctionUsed(lab.Player, TutorialJourneyData.FunctionIds[A(mission, "function")]);
                    break;
                case "interactwithbuilding":
                    lab.Travel("1011");
                    var building = lab.Player.Map!.Objects.Values.OfType<Building>().Single(b => b.Template.Name == A(mission, "buildingid"));
                    lab.Player.Teleport(building.Position, 0, DateTime.UtcNow); TutorialJourney.BuildingInteracted(lab.Player, building); break;
                case "gotoquestgiver":
                    Check(lab.Act(Giver(mission, suffix), A(mission, "mustaccept") == "true" ? 2 : 0) == 0,
                        "authored giver interaction " + suffix + " task=" + lab.State.JourneyTask + " variables=" + lab.State.JourneyVariables
                        + " bag=" + string.Join(',', lab.Player.Inventory.Bag.Items.Select(i => i.Template.Name + ':' + i.Count))); break;
                case "mount":
                    lab.Session.OnRequest(Request(new MountRequest { wf = lab.World.GameNow, v3 = Mounts.ItemOf(lab.Player)!.Id, v4 = true }, 43), false);
                    Check(lab.Peer.Responses.Last().ReturnCode == 0, "actual mount request accepted");
                    lab.Clock.Advance(TimeSpan.FromSeconds(4)); lab.World.Update(); break;
                case "haveplaytime": lab.Clock.Advance(TimeSpan.FromSeconds(N(mission, "seconds"))); break;
                case "haveavgitempower":
                    OpeningTutorial.Update(lab.Player);
                    Check(lab.State.JourneyTask == 2, "armor held in bag cannot satisfy equipped power");
                    foreach (var item in lab.Player.Inventory.Bag.Items.Where(i => i.Template.Tier == 2 && i.Template.Slot is "mainhand" or "offhand" or "head" or "armor" or "shoes").ToArray()) Equip(item);
                    if (suffix == "OA")
                    {
                        var armor = Enumerable.Range(2, 3).Select(i => lab.Player.Inventory.Equipment[i]).ToArray();
                        for (var i = 2; i <= 4; i++) lab.Player.Inventory.Equipment.Put(i, null);
                        OpeningTutorial.Update(lab.Player);
                        Check(lab.State.JourneyTask == 2, "bow without armor cannot satisfy the 400-power objective");
                        for (var i = 2; i <= 4; i++) lab.Player.Inventory.Equipment.Put(i, armor[i - 2]);
                    }
                    Check(TutorialJourney.AverageItemPower(lab.Player) >= N(mission, "itempower"), "authored T2 outfit reaches " + suffix + " item-power requirement; actual="
                        + TutorialJourney.AverageItemPower(lab.Player) + ", slots=" + string.Join(',', Enumerable.Range(0, 5).Select(i => lab.Player.Inventory.Equipment[i]?.Template.Name)) ); break;
                case "selectspell":
                    var slot = A(mission, "activespellslot") switch { "head" => 2, "armor" => 3, "shoes" => 4, _ => 0 };
                    var equipped = lab.Player.Inventory.Equipment[slot]!;
                    Check(equipped.Active.Length > 0, "fixture armor has an authored active spell");
                    equipped.Active[0] = Client.Spells!.SlotsOf(equipped.Template.Name)!.Active[0][0];
                    TutorialJourney.SpellSelected(lab.Player, equipped, true, 0); break;
                default: throw new InvalidOperationException("Unsupported route fixture condition " + mission.Name);
            }
        }
        for (var attempts = 0; attempts < 160 && lab.State.JourneyPhase != "completed"; attempts++)
        {
            if (lab.State.JourneyPhase == "offered")
            {
                var choices = lab.State.JourneyQuest is null ? new[] { lab.Data.Journey!.Find(TutorialJourneyData.Prefix + "E")! }
                    : lab.Data.Journey!.Successors(lab.State.JourneyQuest).ToArray();
                var choice = choices.FirstOrDefault(q => Short(q.Name) == camp || Short(q.Name) == weapon) ?? choices.First();
                var tag = A(choice.Definition, "tags");
                var giver = lab.Data.Journey.QuestGivers.First(g => g.OfferTags.Contains(tag));
                lab.Travel(giver.Cluster);
                var npc = lab.Npc(giver.Reference);
                var slot = Enumerable.Range(0, giver.OfferSlots.Count).Single(i => giver.OfferSlots[i].Contains(tag));
                Check(lab.Act(giver.Reference, 1, (byte)slot, 1) != 0, "reward selector cannot choose a different quest slot");
                Check(lab.Act(giver.Reference, 1, (byte)slot) == 0 && lab.State.JourneyQuest == choice.Name, "authored slot accepts " + Short(choice.Name));
                continue;
            }
            var quest = lab.Data.Journey!.Find(lab.State.JourneyQuest!)!;
            var suffix = Short(quest.Name);
            if (lab.State.JourneyPhase == "ready")
            {
                var reference = Giver(quest.Tasks[^1].Definition.Descendants("gotoquestgiver").Single(), suffix);
                Check(lab.Act(reference, 3, (byte)(lab.State.JourneySlot + 1)) != 0,
                    "stale completion from another slot cannot claim the active reward");
                Check(lab.Act(reference, 3) == 0, "native delivery " + suffix);
                completed.Add(suffix); continue;
            }
            var task = quest.Tasks[lab.State.JourneyTask];
            var before = (lab.State.JourneyQuest, lab.State.JourneyTask, lab.State.JourneyPhase);
            visited.Add(suffix); tasks++;
            // Tutorial scene buffs temporarily stun movement. UI actions are observed after their
            // presentation interval; no world tick or fixture may erase the quest checkpoint.
            lab.Clock.Advance(TimeSpan.FromSeconds(60));
            var missions = task.Definition.Name == "missionstep" ? task.Definition.Elements().ToArray() : task.Definition.Element("missions")!.Elements().ToArray();
            foreach (var mission in missions)
            {
                if ((lab.State.JourneyQuest, lab.State.JourneyTask, lab.State.JourneyPhase) != before) break;
                Satisfy(mission, suffix); OpeningTutorial.Update(lab.Player);
            }
            Check((lab.State.JourneyQuest, lab.State.JourneyTask, lab.State.JourneyPhase) != before,
                "route advances naturally at " + suffix + " task " + task.Index + " (stunned=" + SpellEffects.Stunned(lab.Player, lab.World.GameNow) + ")");
            if (tasks % 7 == 0) lab.Rejoin();
        }
        var expected = new[] { "E", "F", "H", "I", "K", "KA", "L", camp, weapon, branch == 0 ? "OA" : "OB", "P", "Q" };
        Check(lab.State.JourneyPhase == "completed" && visited.SetEquals(expected), "entire authored island chain completes, with selected alternatives only");
        var tracked = new HashSet<(int Quest, int Task)>();
        var numericPowerSeen = false;
        foreach (var connection in lab.Connections)
        {
            var questId = -1;
            foreach (var ev in connection.Events)
            {
                var code = (EventCodes)Convert.ToInt16(ev.Parameters[ReservedKeys.EventCode]);
                if (code == EventCodes.FullQuestInfo) questId = Convert.ToInt32(ev.Parameters.GetValueOrDefault((byte)1));
                if (code == EventCodes.QuestProgressInfo) tracked.Add((questId, Convert.ToInt32(ev.Parameters.GetValueOrDefault((byte)1))));
                if (code == EventCodes.QuestProgressInfo && Convert.ToInt32(ev.Parameters.GetValueOrDefault((byte)1)) == 2
                    && lab.Data.Journey!.Quests.Any(q => q.Id == questId && Short(q.Name) is "OA" or "OB"))
                {
                    using var progress = JsonDocument.Parse((string)ev.Parameters[2]!);
                    numericPowerSeen |= progress.RootElement[0][0].GetInt64() > 1;
                }
            }
        }
        var routeQuests = expected.Select(s => lab.Data.Journey!.Find(TutorialJourneyData.Prefix + s)!).ToArray();
        Check(routeQuests.Sum(q => q.Tasks.Count) == 61 && routeQuests.All(q => q.Tasks.All(t => tracked.Contains((q.Id, t.Index)))),
            "wire tracker visits every one of the 61 authored tasks, including automatically satisfied steps");
        Check(numericPowerSeen, "native item-power counter receives a number, not a completion flag");
        Check(lab.Player.Character.LearningPoints == learningBefore + 100 * 10000L, "final learning-point reward applied exactly once");
        lab.Rejoin(); Check(lab.State.JourneyPhase == "completed", "whole route checkpoint survives a final reconnect");
    }
}
