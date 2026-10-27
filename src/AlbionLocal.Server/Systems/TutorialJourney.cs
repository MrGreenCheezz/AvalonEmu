using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>The island continuation uses the installed client's quest tree. The only counters accepted here
/// originate in authoritative harvest, combat and market operations. Persistent values are immutable snapshots.</summary>
public static class TutorialJourney
{
    private sealed class OnlineClock { public TimeSpan Last; public readonly Dictionary<string, TimeSpan> Inside = []; }
    private static readonly ConditionalWeakTable<Player, OnlineClock> Clocks = new();
    private static TutorialJourneyData Data(Player p) => p.World.OpeningTutorial!.Journey!;
    public static bool Active(Player p) => p.Character.Tutorial is { Version: 1, Stage: >= 27 }
        && p.World.Config.TutorialOpening && p.World.OpeningTutorial?.Journey is { } d
        && p.Map is { } map && d.Tutorial.Clusters.Contains(map.Cluster);
    private static OpeningTutorialProgress State(Player p) => p.Character.Tutorial!;
    private static TutorialJourneyData.Quest Quest(Player p) => Data(p).Find(State(p).JourneyQuest ?? TutorialJourneyData.Prefix + "E")!;
    private static TutorialJourneyData.Task Task(Player p) => Quest(p).Tasks[State(p).JourneyTask];
    private static string Attr(XElement e, string key, string fallback = "") => (string?)e.Attribute(key) ?? fallback;
    private static long Number(XElement e, string key, long fallback = 0) => long.TryParse(Attr(e, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    private static Dictionary<string, long> Counters(Player p) => JsonSerializer.Deserialize<Dictionary<string, long>>(State(p).JourneyCounters) ?? [];
    private static XElement[] Missions(TutorialJourneyData.Task task) => (task.Definition.Name == "missionstep"
        ? task.Definition.Elements() : task.Definition.Element("missions")!.Elements()).ToArray();
    private static bool Running(Player p) => State(p).JourneyPhase == "active";
    private static void Store(Player p, OpeningTutorialProgress state)
    {
        p.Character.Tutorial = state;
        p.World.Saves.MarkChanged(p.Character);
    }
    public static void Joined(Player p)
    {
        if (!Active(p)) return;
        Clocks.Remove(p); Clocks.Add(p, new OnlineClock { Last = p.World.Clock.Now });
        p.Session.Send(new TutorialUpdateEvent { d18 = State(p).TutorialState });
        TutorialWorld.RestoreBlockers(p, JsonSerializer.Deserialize<Dictionary<string, int>>(State(p).JourneyBlockers) ?? []);
        SendFull(p); SendProgress(p, false); RefreshNpcs(p);
    }
    public static void TrackerReady(Player p) { if (Active(p)) { SendFull(p); SendProgress(p, false); } }
    public static void Update(Player p)
    {
        if (!Active(p)) return;
        p.StorePlayedTime();
        var clock = Clocks.GetValue(p, _ => new OnlineClock { Last = p.World.Clock.Now });
        var now = p.World.Clock.Now;
        var delta = Math.Max(0, (now - clock.Last).Ticks); clock.Last = now;
        if (delta > 0) Store(p, State(p) with { JourneyOnlineTicks = checked(State(p).JourneyOnlineTicks + delta),
            JourneyTaskTicks = checked(State(p).JourneyTaskTicks + delta) });
        if (!Running(p) || SpellEffects.Stunned(p, p.World.GameNow)) return;
        // Settle already-satisfied steps in this operation, so the next NPC marker is ready immediately.
        for (var transitions = 0; Running(p) && transitions < 64; transitions++)
        {
            var task = Task(p); var before = State(p).JourneyCounters;
            var counts = Counters(p);
            var missions = Missions(task);
            var done = missions.Select((m, i) => Evaluate(p, m, i.ToString(CultureInfo.InvariantCulture), counts, clock)).ToArray();
            var serialized = JsonSerializer.Serialize(counts);
            if (serialized != before) Store(p, State(p) with { JourneyCounters = serialized });
            if (!done.All(v => v)) { if (serialized != before) SendProgress(p, false); break; }
            Advance(p);
        }
    }
    private static bool Evaluate(Player p, XElement m, string key, Dictionary<string, long> counts, OnlineClock clock)
    {
        var required = Math.Max(1, Number(m, "count", 1));
        long value = counts.GetValueOrDefault(key);
        switch (m.Name.LocalName)
        {
            case "or":
                var children = m.Elements().ToArray();
                value = children.Select((c, i) => Evaluate(p, c, key + "." + i, counts, clock)).Any(x => x) ? 1 : 0;
                break;
            case "gotolocation":
                var marker = Attr(m, "locationmarkerid", Attr(m, "gotolocationmarkerid"));
                var position = p.PositionAt(DateTime.UtcNow);
                var inside = Data(p).Markers(p.Map!.Cluster, marker).Any(a => Vector2.DistanceSquared(position, a.Position) <= a.Radius * a.Radius);
                if (Attr(m, "triggeronleave") == "true")
                {
                    if (inside) counts[key + ".entered"] = 1;
                    value = !inside && counts.GetValueOrDefault(key + ".entered") == 1 ? 1 : 0;
                }
                else if (!inside) { clock.Inside.Remove(key); value = 0; }
                else
                {
                    clock.Inside.TryAdd(key, p.World.Clock.Now);
                    value = (p.World.Clock.Now - clock.Inside[key]).TotalSeconds >= Number(m, "seconds") ? 1 : 0;
                }
                break;
            case "equipitem": value = p.Inventory.Equipment.Items.Any(i => i.Template.Name == Attr(m, "itemid")) ? 1 : 0; break;
            case "acquireitem":
                var alternatives = m.Elements("item").Select(e => Attr(e, "id")).ToArray();
                var names = alternatives.Length > 0 ? alternatives : [Attr(m, "itemid")];
                value = p.Inventory.Bag.Items.Concat(p.Inventory.Equipment.Items)
                    .Where(i => names.Contains(i.Template.Name)).Sum(i => (long)i.Count);
                break;
            case "unlockachievement":
                var node = p.World.GameData?.Board?.Find(Attr(m, "achievement"));
                // DestinyBoard.Apply removes finished plain nodes from Levels and stores them in
                // Completed. START and COMBAT_TRAINEE are such nodes, not mastery templates.
                var level = node is not null && p.Progression is { } progression
                    ? !node.IsTemplate && progression.Completed.Contains(node.Id) ? 1 : progression.Levels.GetValueOrDefault(node.Id)
                    : 0;
                value = node is not null && level >= Number(m, "masterylevel", 1) ? 1 : 0;
                break;
            case "mount": value = p.IsMounted && p.Inventory.Equipment.Items.Any(i => i.Template.Name == Attr(m, "itemid")) ? 1 : 0; break;
            case "haveavgitempower": value = AverageItemPower(p) >= Number(m, "itempower") ? 1 : 0; break;
            case "haveplaytime":
                // haveplaytime checks the character's online play time, not time spent in this task.
                var ticks = PlayedTicks(p);
                value = ticks >= Number(m, "seconds") * TimeSpan.TicksPerSecond ? 1 : 0; break;
            case "killmob": case "gather": case "marketbuyitem": case "usefunction":
            case "interactwithbuilding": case "selectspell": case "gotoquestgiver": break;
            default: throw new InvalidDataException($"Unhandled tutorial condition {m.Name}.");
        }
        counts[key] = Math.Min(required, value);
        return value >= required;
    }
    private static void Observe(Player p, Func<XElement, bool> match, long amount = 1)
    {
        if (!Active(p) || !Running(p) || amount <= 0) return;
        var counts = Counters(p);
        void Visit(XElement m, string key)
        {
            if (m.Name == "or") { var index = 0; foreach (var child in m.Elements()) Visit(child, key + "." + index++); }
            else if (match(m)) counts[key] = Math.Min(Math.Max(1, Number(m, "count", 1)), counts.GetValueOrDefault(key) + amount);
        }
        var missions = Missions(Task(p)); for (var i = 0; i < missions.Length; i++) Visit(missions[i], i.ToString(CultureInfo.InvariantCulture));
        Store(p, State(p) with { JourneyCounters = JsonSerializer.Serialize(counts) });
        Update(p); SendProgress(p, false);
    }
    public static void MobKilled(Player p, Mob mob)
    {
        if (mob.Map != p.Map || !mob.Dead) return;
        Observe(p, m => m.Name == "killmob" && Attr(m, "faction") == mob.Template.Faction
            && mob.Template.Tier >= Number(m, "mintier") && mob.Template.Tier <= Number(m, "maxtier", int.MaxValue));
    }
    public static void Gathered(Player p, string item, int count) => Observe(p, m => m.Name == "gather" && Attr(m, "itemid") == item, count);
    public static void MarketBought(Player p, string item, int count) => Observe(p, m => m.Name == "marketbuyitem" && Attr(m, "item") == item, count);
    public static void FunctionUsed(Player p, int function)
    {
        var name = function switch { 2 => "OpenClusterMap", 3 => "OpenDestinyBoard", 14 => "InteractWithMarketplace", 15 => "BuyJourney", 19 => "FinishTutorial", _ => "" };
        // BuyJourney is acknowledged only by the completed authoritative travel operation.
        if (function == 15) return;
        Observe(p, m => m.Name == "usefunction" && Attr(m, "function") == name);
    }
    public static void BuildingInteracted(Player p, Building building)
    {
        if (p.Map is null || building.Map != p.Map || !p.Visibility.Knows(building)
            || Vector2.DistanceSquared(p.PositionAt(DateTime.UtcNow), building.Position) > WorldSession.StationRange * WorldSession.StationRange) return;
        Observe(p, m => m.Name == "interactwithbuilding" && Attr(m, "buildingid") == building.Template.Name
            || m.Name == "usefunction" && Attr(m, "function") == "InteractWithMarketplace" && building.Template.Name.Contains("MARKETPLACE", StringComparison.Ordinal));
    }
    public static void SpellSelected(Player p, Item item, bool active, int slot)
    {
        if (!active || slot < 0 || slot >= item.Active.Length || item.Active[slot] < 0) return;
        var equipmentSlot = p.Inventory.Equipment.Items.Contains(item) ? Array.FindIndex(Inventory.SlotNames, s => s.Equals(item.Template.Slot, StringComparison.OrdinalIgnoreCase)) : -1;
        var name = equipmentSlot switch { 0 when slot == 0 => "mainhand1", 2 => "head", 3 => "armor", 4 => "shoes", _ => "" };
        Observe(p, m => m.Name == "selectspell" && Attr(m, "activespellslot") == name && item.Template.Tier >= Number(m, "minitemtier"));
    }
    public static bool TravelAllowed(Player p)
    {
        p.StorePlayedTime();
        return Active(p) && Quest(p).Name.EndsWith("_Q", StringComparison.Ordinal)
            && Running(p) && State(p).JourneyTask == Quest(p).Tasks.Count - 1
            && p.Character.PlayedMilliseconds >= Number(Missions(Quest(p).Tasks[0])[0], "seconds") * 1000;
    }
    public static void Departed(Player p)
    {
        if (!TravelAllowed(p)) return;
        Observe(p, m => m.Name == "usefunction" && Attr(m, "function") == "BuyJourney");
    }
    private static void Advance(Player p)
    {
        var q = Quest(p); var s = State(p);
        if (s.JourneyTask + 1 < q.Tasks.Count)
        {
            Store(p, s with { JourneyTask = s.JourneyTask + 1, JourneyCounters = "{}", JourneyTaskTicks = 0 });
            Clocks.GetValue(p, _ => new()).Inside.Clear(); EnterTask(p); SendProgress(p); RefreshNpcs(p); return;
        }
        Store(p, s with { JourneyPhase = "ready" }); SendProgress(p); RefreshNpcs(p);
        // E's final condition is a location, and P ends at the boat. Their next gotoa quests start automatically.
        if (Missions(q.Tasks[^1]).All(m => m.Name != "gotoquestgiver")) Complete(p, null);
    }
    private static void EnterTask(Player p)
    {
        var s = State(p); var blockers = JsonSerializer.Deserialize<Dictionary<string, int>>(s.JourneyBlockers) ?? [];
        foreach (var action in Task(p).Preactions)
        {
            if (action.Name == "settutorialstate")
            {
                var state = Data(p).Tutorial.Find(Attr(action, "state"))!;
                s = s with { TutorialState = state.Id }; p.Session.Send(new TutorialUpdateEvent { d18 = state.Id });
            }
            else if (action.Name == "settutorialblocker")
            {
                var id = Attr(action, "id"); var value = checked((int)Number(action, "statetoset"));
                blockers[id] = value; TutorialWorld.SetBlocker(p, id, value);
            }
            else if (action.Name == "openhintbox") Hint(p, Attr(action, "uniquename"), Attr(action, "displayonplatform"));
        }
        Store(p, s with { JourneyBlockers = JsonSerializer.Serialize(blockers) });
        if (Attr(Task(p).Definition, "applybuff") is { Length: > 0 } buff)
        {
            var spell = p.World.Spells?.Template(buff) ?? throw new InvalidDataException($"Missing tutorial buff {buff}.");
            SpellCasts.Execute(new SpellCasts.SpellContext(p, p.Map!, spell, 0, 100, p, p.PositionAt(DateTime.UtcNow), p.World.GameNow), spell);
        }
    }
    // Dedicated hint ids are catalog entries, not tutorial-state ids. Populated from the client catalog by the loader.
    private static void Hint(Player p, string name, string platform)
    {
        if (platform.Length > 0 && platform != "desktop") return;
        if (Data(p).Hints.TryGetValue(name, out var id)) p.Session.Send(new TriggerHintBoxEvent { d11 = id });
    }
    public static int? BlockerState(Player p, string id) => Active(p)
        ? (JsonSerializer.Deserialize<Dictionary<string, int>>(State(p).JourneyBlockers) ?? []).TryGetValue(id, out var state) ? state : null : null;
    public static short Request(Player p, long npcId, int action, byte slot, byte selection)
    {
        if (!Active(p) || p.Map?.Objects.GetValueOrDefault(npcId) is not TutorialQuestGiver npc
            || Vector2.DistanceSquared(p.PositionAt(DateTime.UtcNow), npc.Position) > OpeningTutorial.TalkRange * OpeningTutorial.TalkRange) return 1;
        if (action == 0)
        {
            if (Running(p)) Observe(p, m => m.Name == "gotoquestgiver" && Attr(m, "mustaccept") != "true" && Matches(p, npc, m));
            Offer(p, npc); return 0;
        }
        if (action == 2 && Running(p))
        {
            var mission = Missions(Task(p)).SingleOrDefault(m => m.Name == "gotoquestgiver" && Attr(m, "mustaccept") == "true" && Matches(p, npc, m));
            if (mission is null) return 1;
            var costs = mission.Elements("item").Select(e => (Type: p.World.Items.Find(Attr(e, "itemid"))!, Count: checked((int)Number(e, "count")))).ToArray();
            if (costs.Any(c => c.Type is null || p.Inventory.Bag.Items.Where(i => i.Template == c.Type).Sum(i => (long)i.Count) < c.Count)) return 1;
            foreach (var cost in costs) p.Session.TakeItems(p, cost.Type, cost.Count);
            Observe(p, m => ReferenceEquals(m, mission)); p.SaveItems(); p.World.Saves.SaveNow(p.Character); return 0;
        }
        if (action == 1 && State(p).JourneyPhase == "offered")
        {
            if (selection != 0) return 1; // The final byte selects a reward, not a different quest slot.
            var choice = Offers(p).SingleOrDefault(q => OfferingNpc(p, q, npc) && Slot(p, q, npc) == slot);
            if (choice is null) return 1;
            Accept(p, choice, slot); Offer(p, npc); return 0;
        }
        // c32.ah(long) sends only action 3 and the NPC: offer/reward selectors stay zero.
        // JourneySlot identifies where this quest was accepted, not how it is handed in.
        if (action == 3 && selection == 0 && slot == 0 && State(p).JourneyPhase == "ready"
            && Missions(Quest(p).Tasks[^1]).Any(m => m.Name == "gotoquestgiver" && Matches(p, npc, m)))
            return Complete(p, npc) ? (short)0 : (short)1;
        return 1;
    }
    private static bool Matches(Player p, TutorialQuestGiver npc, XElement mission)
    {
        var variable = Attr(mission, "questgivervariable");
        var reference = variable.Length == 0 ? null : Quest(p).Definition.Element("variables")?.Elements("dynamicquestgiver")
            .FirstOrDefault(v => Attr(v, "name") == variable)?.Element("questgivertag")?.Attribute("tag")?.Value;
        if (reference is not null)
        {
            var target = Giver(p, Quest(p), variable);
            var cluster = target.Cluster; var guid = target.Guid;
            if (State(p).JourneyVariables is { } serialized)
            {
                var variables = Quest(p).Definition.Element("variables")!.Elements().ToArray();
                var index = Array.FindIndex(variables, e => Attr(e, "name") == variable);
                using var json = JsonDocument.Parse(serialized);
                cluster = json.RootElement[index][0][0].GetString()!;
                guid = Guid.Parse(json.RootElement[index][0][1].GetString()!);
            }
            return npc.Spawn is CriticalMember member && member.Guid == guid && npc.Map?.Cluster == cluster;
        }
        var marker = Attr(mission, "locationmarkerid");
        if (marker.Length > 0 && Data(p).Markers(p.Map!.Cluster, marker).Any(m => Vector2.DistanceSquared(m.Position, npc.Position) <= MathF.Pow(m.Radius + OpeningTutorial.TalkRange, 2))) return true;
        return marker.Length == 0 && OfferingNpc(p, Quest(p), npc);
    }
    private static bool OfferingNpc(Player p, TutorialJourneyData.Quest quest, TutorialQuestGiver npc)
    {
        var tag = Attr(quest.Definition, "tags");
        return Data(p).QuestGivers.Any(g => g.Cluster == npc.Map?.Cluster && g.Guid == ((CriticalMember)npc.Spawn).Guid
            && g.OfferTags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }
    private static IEnumerable<TutorialJourneyData.Quest> Offers(Player p)
    {
        if (State(p).JourneyPhase != "offered") return [];
        if (State(p).JourneyQuest is null) return [Data(p).Find(TutorialJourneyData.Prefix + "E")!];
        // MA/B/C share one NPC slot and differ only by suggested camp. They are route alternatives,
        // while NA/B/C use three distinct authored slots and remain independently selectable.
        return Data(p).Successors(State(p).JourneyQuest!).GroupBy(q => Attr(q.Definition, "tags"))
            .Select(group => { var choices = group.ToArray(); return choices[p.Character.Id.ToByteArray()[0] % choices.Length]; });
    }
    private static void Accept(Player p, TutorialJourneyData.Quest quest, int slot = 0)
    {
        var s = State(p);
        var giver = Giver(p, quest);
        var variables = Variables(p, quest);
        Store(p, s with { Stage = 28, JourneyQuest = quest.Name, JourneyTask = 0, JourneyPhase = "active",
            JourneyCounters = "{}", JourneyTaskTicks = 0, QuestId = Guid.NewGuid(), StartedAt = p.World.GameNow,
            JourneyGiver = giver.Guid, JourneyGiverCluster = giver.Cluster, JourneyVariables = variables, JourneySlot = slot });
        Clocks.GetValue(p, _ => new OnlineClock { Last = p.World.Clock.Now }).Inside.Clear();
        EnterTask(p); SendFull(p); SendProgress(p); RefreshNpcs(p);
    }
    private static bool Complete(Player p, TutorialQuestGiver? npc)
    {
        if (State(p).JourneyPhase != "ready") return false;
        var quest = Quest(p);
        var rewards = quest.Definition.Element("rewards")!.Elements().ToArray();
        var items = rewards.Where(r => r.Name == "item").Select(r => (Type: p.World.Items.Find(Attr(r, "itemid"))!, Count: checked((int)Number(r, "count")))).ToArray();
        if (items.Any(i => i.Type is null) || !WorldSession.Fits(p.Inventory.Bag, items)) return false;
        foreach (var (type, count) in items) p.Session.GiveItem(p, type, count);
        foreach (var reward in rewards)
        {
            if (reward.Name == "silver") Wallet.Credit(p, checked(Number(reward, "absolute") * 10000), "tutorial quest " + quest.Name);
            if (reward.Name == "learningpoints") LearningPoints.Grant(p, checked(Number(reward, "amount") * 10000));
        }
        p.Session.Send(Full(p, 1, true));
        Hint(p, Attr(quest.Definition, "completehintbox", "CLEAR"), "");
        var terminal = quest.Name.EndsWith("_Q", StringComparison.Ordinal);
        Store(p, State(p) with { JourneyPhase = terminal ? "completed" : "offered", JourneyCounters = "{}" });
        if (terminal)
        {
            Store(p, State(p) with { TutorialState = Data(p).Tutorial.End.Id });
            p.Session.Send(new TutorialUpdateEvent { d18 = Data(p).Tutorial.End.Id });
        }
        p.SaveItems(); p.World.Saves.SaveNow(p.Character);
        // nextquest steers the client's following dialogue; it does not authorize accepting a new
        // mission without the player, especially when the weapon quests occupy different slots.
        var next = Data(p).Successors(quest.Name).SingleOrDefault(q => Attr(q.Definition, "tags") == "gotoa");
        if (!terminal && next is not null) Accept(p, next);
        RefreshNpcs(p); if (npc is not null) Offer(p, npc);
        return true;
    }
    public static QuestGiverInfoForPlayerEvent NpcInfo(Player p, TutorialQuestGiver npc)
    {
        var final = Running(p) || State(p).JourneyPhase == "ready" ? Missions(Quest(p).Tasks[State(p).JourneyTask]) : [];
        return new QuestGiverInfoForPlayerEvent { BuildingObjectId = npc.Id,
            QuestGiverGuid = ((CriticalMember)npc.Spawn).Guid!.Value, QuestGiverType = npc.QuestGiverType,
            // The client checks the agent's funds before sending an acceptance request. Tutorial
            // agents cover their largest authored reward per player, without a shared pool.
            // Rewards are still credited only by Complete, with its persisted delivery checkpoint.
            dr2 = Data(p).Quests.Where(q => OfferingNpc(p, q, npc))
                .Select(q => checked(Number(q.Definition.Element("rewards")?.Element("silver") ?? new XElement("silver"), "absolute", 0) * 10000))
                .DefaultIfEmpty(0).Max(),
            dr4 = Offers(p).Any(q => OfferingNpc(p, q, npc)),
            dr5 = final.Any(m => m.Name == "gotoquestgiver" && Matches(p, npc, m)), BarStamp = p.World.GameNow };
    }
    private static void RefreshNpcs(Player p)
    { foreach (var npc in p.Map!.Objects.Values.OfType<TutorialQuestGiver>()) p.Session.Send(NpcInfo(p, npc)); }
    private static TutorialJourneyData.QuestGiver Giver(Player p, TutorialJourneyData.Quest quest, string? variable = null)
    {
        var tag = variable is null ? Attr(quest.Definition, "tags")
            : quest.Definition.Element("variables")!.Elements("dynamicquestgiver").First(v => Attr(v, "name") == variable).Element("questgivertag")!.Attribute("tag")!.Value;
        var candidates = Data(p).QuestGivers.Where(g => (variable is null ? g.OfferTags : g.Tags).Contains(tag, StringComparer.OrdinalIgnoreCase)).ToArray();
        // Auto-started travel quests have no physical GOTO_A on the island. Bind their actual target giver.
        if (candidates.Length == 0 && variable is null)
            return Giver(p, quest, Attr(quest.Definition.Element("variables")!.Elements("dynamicquestgiver").First(), "name"));
        return candidates.OrderBy(g => g.Cluster == p.Map!.Cluster ? 0 : 1).First();
    }
    private static string Variables(Player p, TutorialJourneyData.Quest quest) => JsonSerializer.Serialize(
        (quest.Definition.Element("variables")?.Elements() ?? []).Select(v => v.Name.LocalName switch {
            "dynamiccluster" => (object)(quest.Definition.Element("variables")?.Elements("dynamicquestgiver").FirstOrDefault() is { } target
                ? Giver(p, quest, Attr(target, "name")).Cluster : p.Map!.Cluster),
            "dynamicquestgiver" => new[] { new[] { Giver(p, quest, Attr(v, "name")).Cluster, Giver(p, quest, Attr(v, "name")).Guid.ToString() } },
            _ => throw new InvalidDataException("Unknown tutorial variable.") }).ToArray());
    private static FullQuestInfoEvent Full(Player p, byte state = 0, bool clear = false)
    {
        var quest = Quest(p); var giver = Giver(p, quest);
        return new FullQuestInfoEvent { ddn = p.Id, cdi = quest.Id, cdj = State(p).JourneyGiver == Guid.Empty ? giver.Guid : State(p).JourneyGiver,
            cdk = State(p).JourneyGiverCluster ?? giver.Cluster,
            cdm = clear ? Guid.Empty : State(p).QuestId, cdn = checked((byte)State(p).JourneySlot), cdo = State(p).StartedAt, cdp = state, cdq = State(p).JourneyVariables ?? Variables(p, quest) };
    }
    private static void SendFull(Player p) { if (State(p).JourneyPhase is "active" or "ready") p.Session.Send(Full(p)); }
    private static void SendProgress(Player p, bool announce = true)
    {
        if (State(p).JourneyPhase is not ("active" or "ready")) return;
        var counts = Counters(p); var missions = Missions(Task(p)); var ready = State(p).JourneyPhase == "ready";
        p.Session.Send(new QuestProgressInfoEvent { ddn = p.Id, dsd = State(p).JourneyTask,
            dse = JsonSerializer.Serialize(missions.Select((m, i) => m.Name == "haveplaytime"
                ? new object[] { ready || counts.GetValueOrDefault(i.ToString()) > 0, PlayedTicks(p), p.World.GameNow }
                : m.Name == "haveavgitempower" ? new object[] { (long)AverageItemPower(p) }
                : m.Name == "or" ? new object[] { counts.GetValueOrDefault(i.ToString()), 1L }
                : new object[] { counts.GetValueOrDefault(i.ToString()) })),
            dsf = missions.Select((m, i) => ready || counts.GetValueOrDefault(i.ToString()) >= Math.Max(1, Number(m, "count", 1))).ToArray(),
            dsg = announce ? State(p).JourneyTask : -1, dsh = announce ? 0 : -1 });
    }
    private static long PlayedTicks(Player p) => checked(p.Character.PlayedMilliseconds * TimeSpan.TicksPerMillisecond);
    /// <summary>Uses the emulator's six-position average (weapon, offhand, head, chest, shoes, cape),
    /// with two-handed weapons filling both hands. Each item's power includes the actual destiny,
    /// quality and durability contributions; template base values alone omit the tutorial's large
    /// COMBAT_TRAINEE bonus. The official server's average formula is not present in the client.</summary>
    public static double AverageItemPower(Player p)
    {
        var equipment = p.Inventory.Equipment;
        double Power(int slot) => equipment[slot] is { } item
            ? p.World.StatCalculator?.ItemPower(item.ForStats(), p.Progression?.Levels ?? []) ?? item.Template.ItemPower : 0;
        var offhand = equipment[1] is null && equipment[0]?.Template.Attrs.GetValueOrDefault("twohanded") == "true" ? Power(0) : Power(1);
        return (Power(0) + offhand + Power(2) + Power(3) + Power(4) + Power(6)) / 6;
    }
    public static void Offer(Player p, TutorialQuestGiver npc)
    {
        p.Session.Send(NpcInfo(p, npc));
        var choices = Offers(p).Where(q => OfferingNpc(p, q, npc)).ToArray();
        var giver = Data(p).QuestGivers.Single(g => g.Guid == ((CriticalMember)npc.Spawn).Guid && g.Cluster == npc.Map!.Cluster);
        for (var slot = 0; slot < Math.Max(1, giver.OfferSlots.Count); slot++)
        {
            var quest = choices.SingleOrDefault(q => Slot(p, q, npc) == slot);
            p.Session.Send(new QuestGiverQuestOfferedEvent { ddn = npc.Id, dr6 = quest is null ? Guid.Empty : OfferId(p, quest),
                dr7 = quest?.Id ?? -1, dr8 = slot, dr9 = quest is null ? "[]" : Variables(p, quest),
                dsa = 0, dsb = State(p).StartedAt, dsc = 0 });
        }
    }
    private static Guid OfferId(Player p, TutorialJourneyData.Quest quest)
    {
        // cmh.r(ax2) only replaces a cached slot definition when its GUID changes. Reusing the
        // completed quest's ID leaves the old dialogue visible. Derive a stable ID per offer so
        // repeated queries/reconnects and existing delivered saves need no database migration.
        var key = Encoding.UTF8.GetBytes($"{State(p).QuestId:D}:{quest.Name}");
        return new Guid(SHA256.HashData(key).AsSpan(0, 16));
    }
    private static int Slot(Player p, TutorialJourneyData.Quest quest, TutorialQuestGiver npc)
    {
        var giver = Data(p).QuestGivers.Single(g => g.Guid == ((CriticalMember)npc.Spawn).Guid && g.Cluster == npc.Map!.Cluster);
        for (var slot = 0; slot < giver.OfferSlots.Count; slot++)
            if (giver.OfferSlots[slot].Contains(Attr(quest.Definition, "tags"), StringComparer.OrdinalIgnoreCase)) return slot;
        return -1;
    }
}
