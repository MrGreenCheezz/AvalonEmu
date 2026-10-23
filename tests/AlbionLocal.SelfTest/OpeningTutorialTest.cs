using System.Numerics;
using System.Net;
using System.Text.Json;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using Microsoft.Data.Sqlite;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

public static class OpeningTutorialTest
{
    private static void Check(bool condition, string why) => AlbionTests.Check(condition, why);
    public static IEnumerable<(string, Action)> All =>
    [
        ("opening tutorial: new character uses resolved beach, starter gear and empty progression", Creation),
        ("opening tutorial: route, dialogue, delivery and tower reach the explicit boundary", Route),
        ("opening tutorial: proximity, premature delivery and repeated requests cannot skip tasks", Refusals),
        ("opening tutorial: staying requires continuous online time; reconnect resumes the task", StayAndRejoin),
        ("opening tutorial: SQLite restart preserves progress and migrates legacy characters", Persistence),
        ("opening tutorial: clean host does not bootstrap LocalHero before account creation", EmptyHost),
        ("opening tutorial: tracker initializes before entry and task updates preserve its quest model", TrackerMessages),
        ("opening tutorial: real NPC registration completes conversation without automatic delivery", NpcRegistration),
        ("opening tutorial: beach arrival uses native tutorial animation and blocks movement for its data duration", Arrival),
        ("opening tutorial: tracker ready keeps opening quests instead of journal suggestions", TrackerReady),
        ("opening tutorial: creation appearance survives Join, arrival, observer creation and reconnect", Appearance),
        ("opening tutorial: entry uses native instance identity while quests resolve authored map and NPC references", ClientInstance),
        ("opening tutorial: tower dodge alternative requires a valid finished cast, not a refused request", TowerDodge),
        ("opening tutorial: tower hand-in resumes after reconnect and completed quest does not reappear", TowerDelivery),
        ("opening tutorial: characters have isolated scenery, combat, visibility and reconnect lifetimes", InstanceIsolation),
        ("opening tutorial: real gathering updates both objectives, persists and permits one explicit delivery", Gathering),
        ("opening tutorial: forge unlock, real crafting, inventory, equipment, spell and shield reward", FirstCraft),
        ("opening tutorial: gathering delivery clears its hint, repairs old saves and preserves crafting hints", GatheringHint),
    ];
    private sealed class Lab : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly OpeningTutorialData Data = OpeningTutorialData.Load(ClientGameDataDir(), Client.Clusters!);
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Repository;
        public readonly World World;
        public FakePeer Peer = null!;
        public WorldSession Session = null!;
        public Player Player => Session.Player!;
        public int Stage => Player.Character.Tutorial!.Stage;
        public Lab(bool arrival = false)
        {
            Repository = new SqliteCharacterRepository(Path.Combine(Dir, "save.db"));
            World = new World(new ServerConfig { JoinByCharacterId = true, TutorialOpening = true, PremiumMode = PremiumMode.None },
                Repository, Client.Items, Client.GameData, Client.Spells)
            { Sync = new object(), Clock = Clock, GameTicks = () => 638000000000000000L + Clock.Now.Ticks,
                Clusters = Client.Clusters, OpeningTutorial = Data };
            var character = Data.Create("OpeningProbe", Client.Items, Client.GameData!);
            if (!arrival) character.Tutorial = character.Tutorial! with { IntroStarted = true };
            Repository.Create(character);
            Rejoin();
        }
        public void Rejoin()
        {
            Session?.OnDisconnected("tutorial rejoin test");
            World.Saves.Flush();
            Peer = new FakePeer(); Session = new WorldSession(Peer, World);
            Session.OnRequest(Request(new JoinRequest { ss = Repository.Find("OpeningProbe")!.Id }, 1), false);
            Check(Session.Player is not null, "opening character joined");
        }
        public void Go(string marker)
        {
            Player.Teleport(Data.Markers(marker)[0].Position, 0, DateTime.UtcNow);
            OpeningTutorial.Update(Player);
        }
        public void GoTask() => Go((Stage < 5 ? Data.First : Data.Tower).Tasks[Stage < 5 ? Stage : Stage - 6].Marker);
        public short Act(int action, long? npc = null, byte slot = 0)
        {
            Session.OnRequest(Request(new QuestGiverRequestRequest { yz = npc ?? Player.Map!.Objects.Values.OfType<TutorialQuestGiver>().Single().Id,
                y0 = action, y1 = slot }, 2), false);
            return Peer.Responses.Last().ReturnCode;
        }
        public void ToCrafter()
        {
            GoTask(); GoTask(); GoTask();
            Clock.Advance(TimeSpan.FromSeconds(Data.First.Tasks[2].Seconds)); OpeningTutorial.Update(Player);
            Check(Stage == 3, "mirador route reached conversation step");
            Player.Teleport(Data.Crafter.Position, 0, DateTime.UtcNow);
        }
        public void Dispose() { World.Dispose(); Repository.Dispose(); Directory.Delete(Dir, true); }
    }
    private static void Creation()
    {
        using var lab = new Lab();
        var p = lab.Player; var c = p.Character;
        Check(c.Cluster == lab.Data.Tutorial.StartCluster && new Vector2(c.PositionX, c.PositionY) == lab.Data.Start.Position,
            "startpoint used, not death respawn or Thetford");
        Check(c.Silver == 0 && c.Inventory.All(i => i is null) && c.Progression is not null,
            "zero silver, no lab kit, independent progression");
        Check(c.Equipment.Where(i => i is not null).Select(i => i!.Name).Order().SequenceEqual(lab.Data.Tutorial.StartingItems.Order()), "only declared starter gear");
        Check(lab.Peer.Events.Any(e => EventCode(e) == EventCodes.TutorialUpdate)
            && lab.Peer.Events.Any(e => EventCode(e) == EventCodes.FullQuestInfo), "real Join emits tutorial and quest events");
        Check(p.Inventory.Equipment.Items.All(i => i.Active.Any(s => s >= 0)), "starter abilities selected by existing spell system");
        var settings = Path.Combine(lab.Dir, "absent-settings.json");
        var defaults = ServerConfig.Load(settings, []);
        Check(!defaults.TutorialOpening && ServerConfig.Load(settings, ["--Tutorial:Opening=true"]).TutorialOpening, "opening is explicit opt-in");
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, lab.World.Config, lab.Repository, StarterKit.Default) { World = lab.World };
        login.OnRequest(Request(TestLogin(), 1), false);
        login.OnRequest(Request(new CreateCharacterRequest { h7 = "CreatedOpeningProbe" }, 2), false);
        Check(loginPeer.Responses.Last().ReturnCode == 0
            && lab.Repository.Find("CreatedOpeningProbe") is { Tutorial.Stage: 0, PlayedMilliseconds: 0 } created
            && created.Cluster == lab.Data.Tutorial.StartCluster && created.Equipment[0] is null,
            "actual Login/CreateCharacter path uses tutorial rather than the ordinary T4 kit");
    }
    private static EventCodes EventCode(AlbionLocal.Photon.Serialization.EventData e) => (EventCodes)Convert.ToInt32(e.Parameters[252]);
    private static void ClientInstance()
    {
        using var lab = new Lab();
        var peer = new FakePeer();
        var login = new LoginSession(peer, lab.World.Config, lab.Repository, StarterKit.Default) { World = lab.World };
        login.OnRequest(Request(TestLogin(), 1), false);
        login.OnRequest(Request(new CreateCharacterRequest { h7 = "InstanceProbe" }, 2), false);
        var c = lab.Repository.Find("InstanceProbe")!;
        var instance = $"@TUTORIALSINGLE@{c.Id:D}";
        var map = lab.Data.Tutorial.StartCluster;
        var created = peer.Responses.Last().Parameters;
        Check(Equals(created[1], instance) && Equals(created[2], map), "creation keeps native instance and localized world label separate");
        login.OnRequest(Request(new SelectCharacterRequest { abc = c.Name }, 3), false);
        var selected = peer.Responses.Last().Parameters;
        Check(Equals(selected[1], instance) && Equals(selected[2], map), "selection supplies authored map for native instance clone");
        var rePeer = new FakePeer();
        var reLogin = new LoginSession(rePeer, lab.World.Config, lab.Repository, StarterKit.Default) { World = lab.World };
        reLogin.OnRequest(Request(TestLogin(), 4), false);
        var listed = rePeer.Responses.Single().Parameters;
        Check(listed[10] is string[] instances && instances.Contains(instance)
            && listed[13] is string[] maps && maps.All(m => m == map), "relogin preserves instance and base-world identities");
        var gamePeer = new FakePeer();
        var game = new WorldSession(gamePeer, lab.World);
        game.OnRequest(Request(new JoinRequest { ss = c.Id }, 5), false);
        Check(Equals(gamePeer.Responses.Last().Parameters[8], instance), "Join agrees exactly with selection");
        var full = gamePeer.Events.Single(e => EventCode(e) == EventCodes.FullQuestInfo).Parameters;
        Check(Equals(full[3], map) && Client.Clusters!.World.Find((string)full[3]!) is not null,
            "FullQuestInfo resolves its giver map through authored world catalog, not instance registry");
        Check(Client.Clusters!.World.Find(instance) is null, "client instance alias is deliberately not an authored map");
        void CheckVariables(string json, OpeningTutorialData.Quest quest)
        {
            using var parsed = JsonDocument.Parse(json);
            Check(parsed.RootElement.GetArrayLength() == quest.Variables.Count, "quest variables have exact definition count");
            for (var i = 0; i < quest.Variables.Count; i++)
            {
                var value = parsed.RootElement[i];
                var cluster = quest.Variables[i] == "dynamiccluster" ? value.GetString()! : value[0][0].GetString()!;
                Check(cluster == map && Client.Clusters!.World.Find(cluster) is not null,
                    "native bol/bop variable lookup finds the map in world data");
                if (quest.Variables[i] == "dynamicquestgiver")
                    Check(Guid.Parse(value[0][1].GetString()!) == lab.Data.Crafter.Guid
                        && lab.Data.Map.Critical.Any(m => m.Guid == lab.Data.Crafter.Guid && m.Type == "questgiverobject"),
                        "questgiver reference resolves within authored map");
            }
        }
        CheckVariables((string)full[9]!, lab.Data.First);
        Check(c.Cluster == map && game.Player!.Map!.Cluster == map && OpeningTutorial.ClientCluster(c, null) == map,
            "logical map remains unchanged and ordinary servers do not expose tutorial aliases");
        game.OnDisconnected("instance test");
        lab.Rejoin();
        Check(Equals(lab.Peer.Responses.Last().Parameters[8], OpeningTutorial.ClientCluster(lab.Player.Character, lab.World)),
            "existing tutorial characters also reconnect using native identity");
        lab.ToCrafter(); lab.Act(0); lab.Act(3);
        var offer = lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestGiverQuestOffered).Parameters;
        CheckVariables((string)offer[4]!, lab.Data.Tower);
        lab.Act(1); lab.Rejoin();
        full = lab.Peer.Events.Single(e => EventCode(e) == EventCodes.FullQuestInfo).Parameters;
        Check(Equals(full[3], map), "accepted tower quest keeps authored giver map after reconnect");
        CheckVariables((string)full[9]!, lab.Data.Tower);
    }
    private static void InstanceIsolation()
    {
        using var lab = new Lab();
        var first = lab.Player;
        var firstMap = first.Map!;
        var secondCharacter = lab.Data.Create("PrivateTutorialProbe", Client.Items, Client.GameData!);
        secondCharacter.Tutorial = secondCharacter.Tutorial! with { IntroStarted = true };
        lab.Repository.Create(secondCharacter);
        var peer = new FakePeer();
        var session = new WorldSession(peer, lab.World);
        session.OnRequest(Request(new JoinRequest { ss = secondCharacter.Id }, 20), false);
        var second = session.Player!;
        var secondMap = second.Map!;
        Check(firstMap != secondMap && firstMap.Cluster == secondMap.Cluster
            && firstMap.TutorialOwner == first.Character.Id && secondMap.TutorialOwner == secondCharacter.Id,
            "two real Join operations create private maps while retaining the authored cluster");
        Check(firstMap.Players.Count == 1 && secondMap.Players.Count == 1
            && !first.Observers.Contains(second) && !second.Observers.Contains(first),
            "characters at the same coordinates never become observers of one another");
        Check(!peer.Events.Any(e => EventCode(e) == EventCodes.NewCharacter)
            && !lab.Peer.Events.Any(e => EventCode(e) == EventCodes.NewCharacter),
            "neither connection receives another character's spawn");
        Check(!firstMap.Objects.Keys.Intersect(secondMap.Objects.Keys).Any()
            && firstMap.Objects.Count == secondMap.Objects.Count,
            "each instance owns separate objects with globally distinct ids");
        var chest = firstMap.Objects.Values.OfType<Mob>().Single(m => m.Template.Name == "T1_MOB_HERETIC_CHEST_TUTORIAL");
        var otherChest = secondMap.Objects.Values.OfType<Mob>().Single(m => m.Template.Name == chest.Template.Name);
        first.Teleport(chest.Position, 0, DateTime.UtcNow);
        second.Teleport(otherChest.Position, 0, DateTime.UtcNow);
        var health = otherChest.Health.At(lab.World.GameNow);
        session.OnRequest(Request(new AttackStartRequest { co = chest.Id }, 21), false);
        Check(peer.Responses.Last().ReturnCode == WorldSession.AttackInvalidTarget,
            "a real attack request cannot target a chest belonging to another instance");
        var eventCount = peer.Events.Count;
        Combat.Damage(first, chest, 1e7, EffectType.Physical, HitOrigin.Direct, -1, lab.World.GameNow);
        Check(chest.Dead && !otherChest.Dead && otherChest.Health.At(lab.World.GameNow) == health
            && peer.Events.Count == eventCount, "destroying one chest leaves the other's health and event stream unchanged");
        lab.Rejoin();
        Check(lab.Player.Map == firstMap && firstMap.Objects.ContainsKey(chest.Id) && chest.Dead,
            "reconnect returns to the owner's existing scenery instead of another player's instance");
        session.OnDisconnected("private tutorial unload test");
        Check(lab.World.Maps.Unload(secondMap) && lab.World.Maps.Find(secondMap.InstanceKey) is null
            && lab.World.Maps.Find(firstMap.InstanceKey) == firstMap,
            "unloading an empty private map removes only its own registration");
        var returned = new WorldSession(new FakePeer(), lab.World);
        returned.OnRequest(Request(new JoinRequest { ss = secondCharacter.Id }, 22), false);
        Check(returned.Player!.Map != secondMap && returned.Player.Map != firstMap
            && returned.Player.Map!.Objects.Values.OfType<Mob>().Any(m => m.Template.Name == chest.Template.Name && !m.Dead),
            "an unloaded instance rebuilds fresh scenery exclusively for its owner");
        returned.OnDisconnected("private tutorial test done");
        Check(lab.World.Maps.Get("0204") == lab.World.Maps.Get("0204")
            && lab.World.Maps.Get("0204").TutorialOwner is null,
            "ordinary maps continue to use shared cluster identity");
    }
    private static void TrackerMessages()
    {
        using var lab = new Lab();
        var events = lab.Peer.Events;
        Check(events.FindIndex(e => EventCode(e) == EventCodes.FullQuestInfo)
            < events.FindIndex(e => EventCode(e) == EventCodes.JoinFinished), "quest installed before client OnEnterGame opens tracker");
        var fullCount = events.Count(e => EventCode(e) == EventCodes.FullQuestInfo);
        lab.GoTask(); lab.GoTask();
        var progress = events.Last(e => EventCode(e) == EventCodes.QuestProgressInfo).Parameters;
        Check(Convert.ToInt32(progress[1]) == 2 && Convert.ToInt32(progress[4]) == 2 && Convert.ToInt32(progress.GetValueOrDefault((byte)5)) == 0,
            "mirador step 2 announces its mission 0, not an invalid mission 2");
        Check(events.Count(e => EventCode(e) == EventCodes.FullQuestInfo) == fullCount,
            "walking updates existing model without rebuilding its tasks");
        lab.Rejoin();
        progress = lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestProgressInfo).Parameters;
        Check(Convert.ToInt32(progress[1]) == 2 && Convert.ToInt32(progress[4]) == -1 && Convert.ToInt32(progress[5]) == -1,
            "reconnect restores task without replaying floating objective announcement");
    }
    private static void NpcRegistration()
    {
        using var lab = new Lab();
        var npc = lab.Player.Map!.Objects.Values.OfType<TutorialQuestGiver>().Single();
        void Register(long id) => lab.Session.OnRequest(Request(new RegisterToObjectRequest { zg = id }, 4), false);
        Register(npc.Id);
        Check(lab.Stage == 0, "remote registration cannot skip movement tasks");
        lab.ToCrafter();
        var beforeClick = OpeningTutorial.NpcInfo(lab.Player, npc);
        Check(beforeClick.dr5 && lab.Stage == 3, "NPC advertises hand-in eligibility before the first dialogue opens");
        Check(lab.Act(3) != 0 && lab.Stage == 3, "eligibility does not complete talk or authorize premature delivery");
        Register(npc.Id + 100000);
        Check(lab.Stage == 3, "unrelated object is not a conversation");
        var responses = lab.Peer.Responses.Count;
        Register(npc.Id);
        Check(lab.Stage == 4 && lab.Peer.Responses.Count == responses, "real op45 completes talk and remains response-free");
        var info = lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestGiverInfoForPlayer).Parameters;
        Check(info.GetValueOrDefault((byte)6) is true, "NPC completion flag enables the client's Complete button (cmh+0x41)");
        var progress = lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestProgressInfo).Parameters;
        Check(progress[3] is bool[] and [true], "talk mission complete flag removes its circle and enables delivery");
        Register(npc.Id);
        Check(lab.Stage == 4 && lab.Player.Character.Silver == 0, "reopening NPC does not deliver or reward automatically");
        Check(lab.Act(3) == 0 && lab.Stage == 5, "explicit complete still delivers after actual client interaction");
        info = lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestGiverInfoForPlayer).Parameters;
        Check(info.GetValueOrDefault((byte)6) is not true, "delivery clears NPC completion flag");
    }
    private static void Arrival()
    {
        using var lab = new Lab(arrival: true);
        var p = lab.Player;
        var spell = Client.Spells!.Template(OpeningTutorial.IntroSpell)!;
        Check(spell.Effects.Any(e => e.Name.LocalName == "stun" && (string?)e.Attribute("time") == "6"), "native six-second tutorial effect");
        Check(p.Character.Tutorial!.IntroStarted && SpellEffects.Stunned(p, lab.World.GameNow), "first join applies tutorial stun");
        Check(p.Combat.Down is null && p.Health.Value == p.Health.Max && p.Character.Silver == 0,
            "arrival does not cause death, health loss or combat rewards");
        var at = p.PositionAt(DateTime.UtcNow);
        lab.Session.OnRequest(Request(new MoveRequest { v6 = [at.X, at.Y], v8 = [at.X + 2, at.Y], v9 = 3 }, 7), false);
        Check(p.PositionAt(DateTime.UtcNow) == at, "server refuses walking during arrival animation");
        lab.Clock.Advance(TimeSpan.FromSeconds(5.9));
        Check(SpellEffects.Stunned(p, lab.World.GameNow), "stun holds for full data duration");
        lab.Clock.Advance(TimeSpan.FromSeconds(0.2));
        Check(!SpellEffects.Stunned(p, lab.World.GameNow), "movement unlocks after six seconds");
        lab.Rejoin();
        Check(!lab.Peer.Events.Any(e => EventCode(e) == EventCodes.CastSpell
            && Convert.ToInt32(e.Parameters.GetValueOrDefault((byte)3)) == spell.Id), "relogin does not replay arrival");
    }
    private static void TrackerReady()
    {
        using var lab = new Lab();
        lab.GoTask(); lab.GoTask();
        var count = lab.Peer.Events.Count;
        lab.Session.OnRequest(Request(new TrackAlbionJournalAchievementsRequest { ag3 = [] }, 8), false);
        var ready = lab.Peer.Events.Skip(count).ToArray();
        Check(ready.Any(e => EventCode(e) == EventCodes.FullQuestInfo)
            && ready.Any(e => EventCode(e) == EventCodes.QuestProgressInfo && Convert.ToInt32(e.Parameters[1]) == 2),
            "post-entry HUD gets active opening quest and current task");
        Check(!ready.Any(e => EventCode(e) == EventCodes.JournalAchievementProgressUpdate)
            && lab.Player.Character.JournalTracked is null, "opening does not auto-populate general journal tracker");
    }
    private static void Appearance()
    {
        using var lab = new Lab();
        var loginPeer = new FakePeer();
        var login = new LoginSession(loginPeer, lab.World.Config, lab.Repository, StarterKit.Default) { World = lab.World };
        login.OnRequest(Request(TestLogin(), 1), false);
        int[] customization = [3, 2, 1, 4];
        int[] clothes = [4, 1, 2, 3];
        login.OnRequest(Request(new CreateCharacterRequest { h7 = "LookProbe", h8 = 10, h9 = 1,
            ia = customization, ib = clothes }, 2), false);
        var created = lab.Repository.Find("LookProbe")!;
        void JoinAndCheck()
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, lab.World);
            session.OnRequest(Request(new JoinRequest { ss = created.Id }, 3), false);
            var wire = peer.Responses.Last().Parameters;
            Check(Convert.ToInt32(wire[3]) == 10 && Convert.ToInt32(wire[5]) == 1
                && Convert.ToInt32(wire.GetValueOrDefault((byte)4)) == 0
                && wire[6] is int[] custom && custom.SequenceEqual(customization)
                && wire[7] is int[] wear && wear.SequenceEqual(clothes), "Join uses creation choices, not default male/zero look");
            var view = session.Player!.BuildCreate(DateTime.UtcNow);
            Check(view.cvz == 10 && view.cv1 == 1 && view.cv0 == 0 && view.cv2!.SequenceEqual(customization)
                && view.cv3!.SequenceEqual(clothes), "observers receive separate avatar/body ids and the same appearance after arrival");
            Check(session.Player.Character.Customization.SequenceEqual(customization)
                && session.Player.Character.Clothes.SequenceEqual(clothes), "tutorial effect leaves appearance unchanged");
            session.OnDisconnected("appearance reconnect");
            lab.World.Saves.Flush();
        }
        JoinAndCheck(); JoinAndCheck();
    }
    private static void Route()
    {
        using var lab = new Lab();
        lab.ToCrafter(); Check(lab.Stage == 3, "standing by NPC does not count as speaking");
        Check(lab.Act(0) == 0 && lab.Stage == 4, "interaction marks objective fulfilled");
        Check(lab.Act(3) == 0 && lab.Stage == 5, "complete explicitly delivers first quest");
        Check(lab.Act(1) == 0 && lab.Stage == 6, "accept explicitly starts second quest");
        var questId = lab.Player.Character.Tutorial!.QuestId;
        lab.GoTask(); Check(lab.Stage == 7, "first stairs point");
        lab.GoTask(); Check(lab.Stage == 8, "last stairs point opens boat-search task");
        lab.Rejoin();
        Check(lab.Stage == 8 && lab.Player.Character.Tutorial!.QuestId == questId, "existing version-one boat-search checkpoint resumes unchanged");
        lab.GoTask(); Check(lab.Stage == 9, "lookout opens the herectics location");
        lab.GoTask(); Check(lab.Stage == 10, "heretics view opens descent");
        lab.GoTask(); Check(lab.Stage == 11, "descent opens dodge-or-walk choice");
        var progress = lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestProgressInfo).Parameters;
        Check(Convert.ToInt32(progress[1]) == 5 && Equals(progress[2], "[[0,1]]"), "OR step uses native count/target payload");
        Check(lab.Player.Character.Tutorial!.TutorialState == lab.Data.Tutorial.Find("USESPELL")!.Id,
            "dodge hint comes from the quest's tutorial preaction");
        lab.GoTask(); Check(lab.Stage == 12, "walking satisfies the authored alternative without requiring dodge");
        Check(lab.Player.Character.Tutorial!.TutorialState == lab.Data.Tutorial.Find("HARDCLEARPREDB")!.Id,
            "return clears the previous hint through authored tutorial state");
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow); OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 12 && lab.Act(3) != 0, "proximity alone cannot deliver the tower quest");
        Check(lab.Act(0) == 0 && lab.Stage == 13 && lab.Act(3) == 0 && lab.Stage == 14,
            "talk and explicit delivery finish the complete tower quest");
        Check(lab.Player.Character.Silver == 0, "empty rewards do not invent silver");
    }
    private static void ToTowerChoice(Lab lab)
    {
        lab.ToCrafter(); lab.Act(0); lab.Act(3); lab.Act(1);
        for (var i = 0; i < 5; i++) lab.GoTask();
        Check(lab.Stage == 11, "tower choice reached in authored order");
    }
    private static void TowerDodge()
    {
        using var lab = new Lab();
        ToTowerChoice(lab);
        var slots = lab.Player.BuildCreate(DateTime.UtcNow).cw4!;
        var slot = Array.FindIndex(slots, id => Client.Spells!.Template(id)?.Name.Contains("DODGE", StringComparison.Ordinal) == true);
        Check(slot >= 0, "starter shoes grant a selected dodge spell");
        var spell = Client.Spells!.Template(slots[slot])!;
        var now = lab.World.GameNow;
        lab.Player.Combat.Cooldowns[spell.Id] = now + TimeSpan.FromMinutes(1).Ticks;
        var refused = SpellCasts.Check(lab.Player, slots, (byte)slot, spell.Id, 0, lab.Player.PositionAt(DateTime.UtcNow), 1, now);
        Check(refused.Cast is null && refused.Why == CastRefusal.Cooldown && lab.Stage == 11,
            "rejected dodge cannot earn quest progress");
        lab.Player.Combat.Cooldowns.Remove(spell.Id);
        var accepted = SpellCasts.Check(lab.Player, slots, (byte)slot, spell.Id, 0, lab.Player.PositionAt(DateTime.UtcNow), 2, now);
        Check(accepted.Cast is not null, "normal cast validation accepts tutorial dodge");
        SpellCasts.Start(lab.Player, accepted.Cast!);
        lab.Clock.Advance(TimeSpan.FromSeconds(2)); lab.World.Update();
        Check(lab.Stage == 12, "finished dodge advances without walking to the circle");
        lab.Rejoin(); Check(lab.Stage == 12, "dodge completion is saved");
    }
    private static void TowerDelivery()
    {
        using var lab = new Lab(); ToTowerChoice(lab); lab.GoTask();
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        lab.Act(0); lab.Rejoin();
        Check(lab.Stage == 13 && OpeningTutorial.NpcInfo(lab.Player,
            lab.Player.Map!.Objects.Values.OfType<TutorialQuestGiver>().Single()).dr5, "pending tower delivery retains Complete eligibility after reconnect");
        Check(lab.Act(3) == 0 && lab.Stage == 14, "tower delivered exactly once");
        var delivered = lab.Player.Character.Tutorial;
        Check(lab.Act(3) != 0 && lab.Player.Character.Tutorial == delivered,
            "repeated tower delivery is refused without state change");
        lab.Rejoin();
        Check(lab.Stage == 14 && !lab.Peer.Events.Any(e => EventCode(e) == EventCodes.FullQuestInfo),
            "completed tower quest does not reappear on reconnect");
        OpeningTutorial.TrackerReady(lab.Player);
        Check(!lab.Peer.Events.Any(e => EventCode(e) == EventCodes.QuestProgressInfo), "offer checkpoint does not emit stale quest progress");
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        lab.Act(0);
        Check(Convert.ToInt32(lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestGiverQuestOffered).Parameters[2]) == lab.Data.Gathering.Id,
            "existing version-one stage 14 offers gathering instead of replaying the tower quest");
    }
    private static void Gathering()
    {
        using var lab = new Lab(); ToTowerChoice(lab); lab.GoTask();
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        lab.Act(0); lab.Act(3);
        var oldQuest = lab.Player.Character.Tutorial!.QuestId;
        Check(lab.Act(1) == 0 && lab.Stage == 15 && lab.Player.Character.Tutorial!.QuestId != oldQuest,
            "gathering acceptance starts a new quest id after tower delivery");
        Check(lab.Player.Character.Tutorial!.TutorialState == lab.Data.Tutorial.Find("FIRSTGATHER")!.Id,
            "gathering starts with the authored tutorial state");
        lab.Go("T1_RESOURCES");
        Check(lab.Stage == 16, "approach opens the two parallel resource objectives");
        string Progress() => (string)lab.Peer.Events.Last(e => EventCode(e) == EventCodes.QuestProgressInfo).Parameters[2]!;
        Check(Progress() == "[[0],[0]]", "wire progress contains one count array per acquisition in definition order");
        var eventCount = lab.Peer.Events.Count;
        OpeningTutorial.Update(lab.Player);
        Check(lab.Peer.Events.Count == eventCount, "unchanged inventory does not flood the tracker every tick");
        lab.Session.OnRequest(Request(new HarvestStartRequest { Start = lab.World.GameNow, HarvestableId = long.MaxValue, ToolObjectId = -1 }, 50), false);
        OpeningTutorial.Update(lab.Player);
        Check(lab.Peer.Responses.Last().ReturnCode != 0 && lab.Stage == 16 && Progress() == "[[0],[0]]",
            "rejected harvest requests cannot invent quest progress");
        int Count(string name) => lab.Player.Inventory.Bag.Items.Where(i => i.Template.Name == name).Sum(i => i.Count);
        void Gather(string name)
        {
            for (var attempt = 0; Count(name) < 6 && attempt < 20; attempt++)
            {
                var node = lab.Player.Map!.Objects.Values.OfType<Harvestable>()
                    .First(n => n.TierData.Item == name && n.Charges > 0);
                lab.Player.Teleport(node.Position, 0, DateTime.UtcNow);
                lab.Session.OnRequest(Request(new HarvestStartRequest
                { Start = lab.World.GameNow, HarvestableId = node.Id, ToolObjectId = -1 }, 51), false);
                Check(lab.Peer.Responses.Last().ReturnCode == 0 && lab.Player.Harvest is not null,
                    $"normal harvest system accepts the tutorial {name} by hand");
                lab.Clock.Advance(TimeSpan.FromSeconds(lab.Player.Harvest!.Duration + 0.1));
                lab.World.Update(); OpeningTutorial.Update(lab.Player);
            }
            Check(Count(name) >= 6, $"six real {name} items reached the inventory");
        }
        Gather("T1_ROCK");
        Check(lab.Stage == 16 && Progress() == "[[6],[0]]", "stone alone does not finish the wood objective");
        lab.World.Saves.Flush();
        using (var stored = new SqliteCharacterRepository(Path.Combine(lab.Dir, "save.db")))
        {
            var saved = stored.Find("OpeningProbe")!;
            Check(saved.Tutorial?.Stage == 16 && saved.Inventory.Where(i => i?.Name == "T1_ROCK").Sum(i => i!.Count) >= 6,
                "a fresh SQLite repository reads both partial quest checkpoint and harvested inventory from disk");
        }
        lab.Rejoin();
        Check(lab.Stage == 16 && Progress() == "[[6],[0]]", "reconnect reconstructs partial counters from the saved inventory");
        var rock = lab.Player.Inventory.Bag.Items.Single(i => i.Template.Name == "T1_ROCK");
        var slot = Enumerable.Range(0, lab.Player.Inventory.Bag.Size).Single(i => lab.Player.Inventory.Bag[i] == rock);
        lab.Player.Inventory.Bag.Put(slot, null); lab.Player.SaveItems(); OpeningTutorial.Update(lab.Player);
        Check(Progress() == "[[0],[0]]", "resources no longer carried cannot satisfy the pending acquisition");
        lab.Player.Inventory.Bag.Put(slot, rock); lab.Player.SaveItems(); OpeningTutorial.Update(lab.Player);
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        Check(lab.Act(3) != 0 && lab.Stage == 16, "cannot deliver after collecting only stone");
        Gather("T1_WOOD");
        Check(lab.Stage == 17, "both real resources open the conversation objective");
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        Check(lab.Act(3) != 0 && lab.Stage == 17, "proximity cannot replace conversation before delivery");
        lab.Act(0); lab.Rejoin();
        Check(lab.Stage == 18 && lab.Act(3) == 0 && lab.Stage == 19,
            "pending conversation delivery survives reconnect and can be completed once");
        Check(Count("T1_ROCK") >= 6 && Count("T1_WOOD") >= 6 && lab.Player.Character.Silver == 0,
            "delivery neither consumes resources nor invents rewards");
        Check(lab.Act(3) != 0, "no duplicate gathering delivery");
        lab.Rejoin();
        Check(lab.Stage == 19 && !lab.Peer.Events.Any(e => EventCode(e) == EventCodes.FullQuestInfo),
            "completed gathering quest stays completed on reconnect");
    }
    private static void GatheringHint()
    {
        using var lab = new Lab();
        var gather = lab.Data.Tutorial.Find("FIRSTGATHER")!.Id;
        var clear = lab.Data.Tutorial.Find("HARDCLEARPREDB")!.Id;
        Check(lab.Data.Tutorial.Find("HARDCLEARPREDB")!.TriggerHint == "CLEAR", "clear state uses the native hint-clearing action");
        lab.Player.Character.Tutorial = lab.Player.Character.Tutorial! with { Stage = 18, TutorialState = gather };
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        Check(lab.Act(3) == 0 && lab.Stage == 19 && lab.Player.Character.Tutorial!.TutorialState == clear,
            "successful gathering delivery stores the cleared hint with the new checkpoint");
        Check(Convert.ToInt32(lab.Peer.Events.Last(e => EventCode(e) == EventCodes.TutorialUpdate).Parameters[0]) == clear,
            "delivery immediately sends the clear instruction to the client");
        lab.Rejoin();
        Check(lab.Player.Character.Tutorial!.TutorialState == clear, "reconnect cannot restore the completed gathering banner");
        foreach (var stage in new[] { 19, 20 })
        {
            lab.Player.Character.Tutorial = lab.Player.Character.Tutorial! with { Stage = stage, TutorialState = gather };
            lab.Rejoin();
            Check(lab.Stage == stage && lab.Player.Character.Tutorial!.TutorialState == clear,
                $"old save at stage {stage} is repaired without advancing its quest");
            Check(Convert.ToInt32(lab.Peer.Events.First(e => EventCode(e) == EventCodes.TutorialUpdate).Parameters[0]) == clear,
                "entry clears the old banner before the quest HUD initializes");
        }
        var forge = lab.Player.Map!.Objects.Values.OfType<Building>().Single(b => b.Template.Name == OpeningTutorialData.Forge);
        lab.Player.Teleport(forge.Position, 0, DateTime.UtcNow);
        lab.Session.OnRequest(Request(new RegisterToObjectRequest { zg = forge.Id }, 81), false);
        Check(lab.Stage == 21 && lab.Player.Character.Tutorial!.TutorialState == lab.Data.Tutorial.Find("FIRSTCRAFT")!.Id,
            "interaction with forge still displays the next mission's own hint");
    }
    private static void FirstCraft()
    {
        using var lab = new Lab();
        lab.Player.Character.Tutorial = lab.Player.Character.Tutorial! with { Stage = 19 };
        foreach (var name in new[] { "T1_ROCK", "T1_WOOD" })
            lab.Player.Inventory.Bag.Put(lab.Player.Inventory.Bag.FreeSlot(), new Item(lab.World.ObjectIds.Next(), Client.Items.Find(name)!, 6, 1, 0, [], []));
        lab.Player.SaveItems(); lab.Rejoin();
        Building Forge() => lab.Player.Map!.Objects.Values.OfType<Building>().Single(b => b.Template.Name == OpeningTutorialData.Forge);
        var forge = Forge();
        lab.Player.Teleport(forge.Position, 0, DateTime.UtcNow);
        lab.Session.OnRequest(Request(new RegisterToObjectRequest { zg = forge.Id }, 70), false);
        Check(lab.Stage == 19 && lab.Player.RegisteredTo is null
            && OpeningTutorial.BuildingAccess(lab.Player, forge).bq9![2] == "noaccess", "forge is locked before crafting acceptance");
        var swordType = Client.Items.Find("T1_MAIN_SWORD")!;
        var recipe = swordType.Recipes[0];
        void Craft() => lab.Session.OnRequest(Request(new ActionOnBuildingStartRequest
        { wf = lab.World.GameNow, o = Forge().Id, p = 1, q = 1, r = Pricing.CraftPrice(recipe, 1), u = swordType.Index, w = 1 }, 71), false);
        Craft(); Check(lab.Peer.Responses.Last().ReturnCode != 0 && lab.Player.Action is null, "crafting request cannot bypass locked forge");
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        lab.Act(0); Check(lab.Act(1) == 0 && lab.Stage == 20, "old gathering completion checkpoint offers crafting");
        lab.Player.Teleport(forge.Position, 0, DateTime.UtcNow); OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 20 && OpeningTutorial.BuildingAccess(lab.Player, forge).bq9![2] == "user", "proximity alone cannot replace forge interaction");
        lab.Session.OnRequest(Request(new RegisterToObjectRequest { zg = forge.Id }, 72), false);
        Check(lab.Stage == 21, "real forge registration enables sword acquisition");
        Craft(); Check(lab.Player.Action is not null, "existing crafting system accepts sword recipe and resources");
        lab.Clock.Advance(TimeSpan.FromTicks(lab.Player.Action!.End - lab.World.GameNow) + TimeSpan.FromMilliseconds(50));
        lab.World.Update(); OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 22, "finished crafting, not its request, enables inventory objective");
        var sword = lab.Player.Inventory.Bag.Items.Single(i => i.Template.Name == swordType.Name);
        Check(sword.Active.All(id => id == -1), "first crafted sword leaves skills available for explicit choice");
        lab.Rejoin();
        lab.Session.OnRequest(Request(new UseFunctionRequest { aia = 0 }, 73), false);
        Check(lab.Stage == 22, "unrelated UI function cannot complete inventory objective");
        lab.Session.OnRequest(Request(new UseFunctionRequest { aia = 16 }, 74), false);
        Check(lab.Stage == 23, "OpenInventory uses the native function number");
        sword = lab.Player.Inventory.Bag.Items.Single(i => i.Template.Name == swordType.Name);
        lab.Session.OnRequest(Request(new InventoryMoveItemRequest
        { ru = lab.Player.Inventory.Bag.IndexOf(sword), rv = lab.Player.Inventory.Bag.Id, rx = 0, ry = lab.Player.Inventory.Equipment.Id }, 75), false);
        OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 24 && lab.Player.Inventory.Equipment[0] == sword, "real inventory move equips sword before spell selection");
        lab.Session.OnRequest(Request(new EquipmentItemChangeSpellRequest { kc = sword.Id, kd = true, ke = 0, kf = 999999 }, 76), false);
        Check(lab.Stage == 24, "invalid skill selection earns no progress");
        var selected = Client.Spells!.SlotsOf(swordType.Name)!.Active[0].First(id => !Client.Spells.IsLocked(id));
        lab.Session.OnRequest(Request(new EquipmentItemChangeSpellRequest { kc = sword.Id, kd = true, ke = 0, kf = selected }, 77), false);
        Check(lab.Stage == 25, "accepted mainhand1 selection opens return conversation");
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        Check(lab.Act(3) != 0, "reward cannot precede conversation");
        lab.Act(0); lab.Rejoin();
        var fillers = new List<int>();
        while (lab.Player.Inventory.Bag.FreeSlot() is >= 0 and var slot)
        {
            fillers.Add(slot);
            lab.Player.Inventory.Bag.Put(slot, new Item(lab.World.ObjectIds.Next(), Client.Items.Find("T1_WOOD")!, 1, 1, 0, [], []));
        }
        Check(lab.Act(3) != 0 && lab.Stage == 26, "full inventory preserves pending delivery without granting shield");
        foreach (var slot in fillers) lab.Player.Inventory.Bag.Put(slot, null);
        Check(lab.Act(3) == 0 && lab.Stage == OpeningTutorial.Boundary
            && lab.Player.Inventory.Bag.Items.Count(i => i.Template.Name == "T1_OFF_SHIELD") == 1, "delivery grants precisely the authored shield reward");
        Check(lab.Act(3) != 0, "reward cannot be delivered twice");
        lab.Rejoin();
        Check(lab.Stage == OpeningTutorial.Boundary && lab.Player.Inventory.Bag.Items.Count(i => i.Template.Name == "T1_OFF_SHIELD") == 1,
            "shield and delivered checkpoint survive reconnect together");
        lab.Act(0);
        Check(OpeningTutorial.NpcInfo(lab.Player, lab.Player.Map!.Objects.Values.OfType<TutorialQuestGiver>().Single()).dr4,
            "combat continuation is offered after the shield checkpoint");
    }
    private static void Refusals()
    {
        using var lab = new Lab();
        var id = lab.Player.Map!.Objects.Values.OfType<TutorialQuestGiver>().Single().Id;
        Check(lab.Act(0, id) != 0 && lab.Stage == 0, "far-away dialogue refused");
        lab.Player.Teleport(lab.Data.Crafter.Position, 0, DateTime.UtcNow);
        Check(lab.Act(0, id + 1000000) != 0 && lab.Stage == 0, "wrong NPC refused");
        Check(lab.Act(3) != 0 && lab.Act(1) != 0 && lab.Stage == 0, "premature delivery/acceptance refused");
        lab.Session.OnRequest(Request(new SetNextTutorialStateRequest(), 3), false);
        Check(lab.Stage == 0, "timestamp-only tutorial request cannot skip route");
        lab.ToCrafter(); lab.Act(0); lab.Act(3);
        var after = lab.Player.Character.Tutorial;
        Check(lab.Act(3) != 0 && lab.Player.Character.Tutorial == after, "duplicate delivery has no effect");
        Check(lab.Act(1, slot: 7) != 0 && lab.Stage == 5, "invalid slot refused");
        lab.Act(1); after = lab.Player.Character.Tutorial;
        Check(lab.Act(1) != 0 && lab.Player.Character.Tutorial == after, "duplicate accept retains quest instance");
    }
    private static void StayAndRejoin()
    {
        using var lab = new Lab(); lab.GoTask(); lab.GoTask(); lab.GoTask();
        lab.Clock.Advance(TimeSpan.FromSeconds(0.6)); OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 2, "incomplete duration does not advance");
        lab.Player.Teleport(lab.Data.Start.Position, 0, DateTime.UtcNow); OpeningTutorial.Update(lab.Player);
        lab.GoTask(); lab.Clock.Advance(TimeSpan.FromSeconds(0.6)); OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 2, "leaving resets duration rather than adding visits");
        lab.Rejoin(); lab.Clock.Advance(TimeSpan.FromHours(6));
        OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 2, "offline/wait before first tick cannot satisfy stay");
        lab.Clock.Advance(TimeSpan.FromSeconds(1)); OpeningTutorial.Update(lab.Player);
        Check(lab.Stage == 3, "new continuous interval completes once");
    }
    private static void Persistence()
    {
        var dir = NewDir(); var path = Path.Combine(dir, "save.db");
        try
        {
            var progress = new OpeningTutorialProgress(7, Guid.NewGuid(), 638000000000000000L) { TutorialState = 4 };
            using (var repository = new SqliteCharacterRepository(path))
            {
                var c = new StarterKit().NewCharacter("PersistOpening"); c.Tutorial = progress;
                repository.Create(c); var copy = repository.Find(c.Id)!;
                copy.Tutorial = copy.Tutorial! with { Stage = 8 };
                Check(repository.Find(c.Id)!.Tutorial == progress, "read copy cannot mutate stored tutorial");
                repository.Save(copy);
            }
            using (var repository = new SqliteCharacterRepository(path))
                Check(repository.Find("PersistOpening")!.Tutorial == progress with { Stage = 8 }, "restart preserves immutable route and quest instance");
            using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "ALTER TABLE characters DROP COLUMN tutorial_opening; PRAGMA user_version=36;"; cmd.ExecuteNonQuery();
            }
            using var migrated = new SqliteCharacterRepository(path);
            Check(migrated.Find("PersistOpening") is { Tutorial: null, Cluster: "0000" }, "schema30 migration leaves old characters outside tutorial");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void EmptyHost()
    {
        var dir = NewDir();
        try
        {
            using var host = new ServerHost(new ServerConfig
            {
                TutorialOpening = true, ClientGameDataPath = ClientGameDataDir(), SavesPath = dir,
                LoginEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                GameEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ChatEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            }, Client);
            Check(host.Characters.List().Count == 0, "empty tutorial host stays empty rather than creating LocalHero");
            Check(host.Characters.Accounts().All(a => string.IsNullOrEmpty(a.Name)), "no registered user accounts on a fresh host");
            var peer = new FakePeer();
            var login = new LoginSession(peer, host.Config, host.Characters, host.Starter) { World = host.World };
            login.OnRequest(Request(TestLogin(), 1), false);
            login.OnRequest(Request(new CreateCharacterRequest { h7 = "FirstActualHero" }, 2), false);
            Check(peer.Responses.Last().ReturnCode == 0 && host.Characters.List() is [{ Tutorial.Stage: 0 }],
                "first user-created character is the only character and starts the tutorial");
        }
        finally { Directory.Delete(dir, true); }
    }
}
