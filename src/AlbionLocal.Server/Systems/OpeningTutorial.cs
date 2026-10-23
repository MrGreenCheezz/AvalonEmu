using System.Numerics;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>Bounded lab implementation. The client renders its own quest texts/circles; only server-observed
/// conditions advance progress. Requests cannot skip the route or deliver twice; ends after first crafting delivery.</summary>
public static class OpeningTutorial
{
    public const float TalkRange = 7; // Lab interaction limit; not claimed as an observed official distance.
    public const int Boundary = 27;
    public const string IntroSpell = "TUTORIAL_STUN";

    /// <summary>Client-facing instance identity. a4l.b recognizes this prefix as TUTORIALSINGLE;
    /// ciy.bv then enables the native quest tracker. Join uses the same identity to select a private
    /// server map; persisted cluster and quest references retain the authored start cluster.</summary>
    public static string ClientCluster(Character c, World? world) => TutorialInstances.ClientCluster(c, world);

    public static bool Active(Player p) => TutorialJourney.Active(p) || p.Character.Tutorial is { Version: 1, Stage: >= 0 and <= Boundary }
        && p.World.OpeningTutorial is { } d && p.Map?.Cluster == d.Tutorial.StartCluster;

    public static void Joined(Player p)
    {
        if (TutorialJourney.Active(p)) { TutorialJourney.Joined(p); return; }
        if (!Active(p)) return;
        p.TutorialInsideSince = null; // Offline time never satisfies a stay-in-circle task.
        p.TutorialGatherProgress = null;
        var log = p.Character.Tutorial!;
        // Repair saves from before gathering delivery cleared its persistent hint.
        // Keep the forge approach quiet until interaction starts FIRSTCRAFT.
        if (log.Stage is 19 or 20 && log.TutorialState == p.World.OpeningTutorial!.Tutorial.Find("FIRSTGATHER")!.Id)
        {
            log = log with { TutorialState = p.World.OpeningTutorial.Tutorial.Find("HARDCLEARPREDB")!.Id };
            p.Character.Tutorial = log;
            p.World.Saves.MarkChanged(p.Character);
        }
        p.Session.Send(new TutorialUpdateEvent { d18 = log.TutorialState });
        if (log.Stage >= 20) TutorialWorld.SetBlocker(p, "BLOCKFORGE", 0);
        SendFull(p);
        SendProgress(p, announce: false);
        Log.Info("tutorial", $"'{p.Character.Name}' resumes opening stage {log.Stage}; boundary is first crafting delivery");
    }

    public static void Update(Player p)
    {
        if (TutorialJourney.Active(p)) { TutorialJourney.Update(p); return; }
        if (!Active(p) || p.Character.Tutorial is not { } log || log.Stage is 4 or 5 or 13 or 14 or 18 or 19 or 26 or Boundary) return;
        if (SpellEffects.Stunned(p, p.World.GameNow)) return;
        var data = p.World.OpeningTutorial!;
        var task = Quest(p, log.Stage).Tasks[TaskIndex(log.Stage)];
        if (task.Kind == "equipitem")
        {
            if (p.Inventory.Equipment.Items.Any(i => i.Template.Name == task.Item)) Change(p, log with { Stage = 24 });
            return;
        }
        if (task.Kind == "acquireitem")
        {
            var counts = AcquisitionCounts(p, task);
            if (counts.Select((count, i) => count >= task.Acquisitions[i].Count).All(done => done))
                Change(p, log with { Stage = log.Stage == 21 ? 22 : 17 });
            else if (GatherProgress(counts) != p.TutorialGatherProgress) SendProgress(p, announce: false);
            return;
        }
        if (task.Kind is not ("gotolocation" or "or")) return; // A nearby NPC is not a conversation.
        var position = p.PositionAt(DateTime.UtcNow);
        var inside = float.IsFinite(position.X) && float.IsFinite(position.Y)
            && data.Markers(task.Marker).Any(m => Vector2.DistanceSquared(position, m.Position) <= m.Radius * m.Radius);
        if (!inside) { p.TutorialInsideSince = null; return; }
        var now = p.World.Clock.Now;
        p.TutorialInsideSince ??= now;
        if ((now - p.TutorialInsideSince.Value).TotalSeconds < task.Seconds) return;
        Change(p, log with { Stage = log.Stage + 1 });
    }

    /// <summary>Called only after the normal spell system has accepted and finished a cast.
    /// The quest's OR also permits walking to its marker; no spell request alone earns progress.</summary>
    public static void SpellFinished(Player p, SpellTemplate spell)
    {
        if (!Active(p) || p.Character.Tutorial is not { Stage: 11 } log) return;
        var task = p.World.OpeningTutorial!.Tower.Tasks[TaskIndex(log.Stage)];
        if (task.SpellNamePart is { } part && spell.Name.Contains(part, StringComparison.Ordinal))
            Change(p, log with { Stage = 12 });
    }

    /// <summary>Use the client's own six-second tutorial_stun effect, not combat knockdown/death.
    /// Run after JoinFinished so the local character view exists when the cast arrives.</summary>
    public static void Entered(Player p)
    {
        if (!Active(p) || p.Character.Tutorial is not { Stage: 0, IntroStarted: false } log) return;
        var world = p.World;
        var spell = world.Spells?.Template(IntroSpell)
            ?? throw new InvalidDataException($"Missing opening spell {IntroSpell}.");
        var now = world.GameNow;
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvy = spell.Id, Extra = new() { [6] = (byte)0 } });
        // A scripted arrival uses its authored duration. Combat CC scaling would shorten six seconds
        // to 4.2 and update diminishing returns, desynchronizing the tutorial animation.
        SpellEffects.Apply(world, p, spell, 0, 100, 100, now,
            [.. spell.Effects.Select((node, i) => new SubEffect(node, i) { Active = true, CcType = "stun",
                DurationMs = checked((int)Math.Round(SpellTemplate.Float(node, "time") * 1000)) })]);
        SpellEffects.Send(world, p);
        SpellEffects.Refresh(world, p);
        p.Character.Tutorial = log with { IntroStarted = true };
        world.Saves.MarkChanged(p.Character);
        Log.Info("tutorial", $"'{p.Character.Name}' beach arrival: {IntroSpell}, client animation tutorial_stun");
    }

    /// <summary>The client sends op515 after its HUD restores collapsed state. Keep the opening quest
    /// as its tracker content instead of populating the general journal suggestions at that point.</summary>
    public static void TrackerReady(Player p)
    {
        if (TutorialJourney.Active(p)) { TutorialJourney.TrackerReady(p); return; }
        if (!Active(p)) return;
        SendFull(p);
        SendProgress(p, announce: false);
    }

    /// <summary>239: action 0 interact/query, 1 accept (slot, selection), 2 give items, 3 complete,
    /// 4 abort, 5 clear. c32.i/ag/ah/cl/cm verify the action numbers. Only this bounded route is supported.</summary>
    public static short Request(Player p, long npcId, int action, byte slot, byte selection)
    {
        if (TutorialJourney.Active(p)) return TutorialJourney.Request(p, npcId, action, slot, selection);
        if (!Active(p) || p.Map?.Objects.GetValueOrDefault(npcId) is not TutorialQuestGiver npc
            || Vector2.DistanceSquared(p.PositionAt(DateTime.UtcNow), npc.Position) > TalkRange * TalkRange)
            return 1;
        var log = p.Character.Tutorial!;
        if (action == 0)
        {
            if (log.Stage is 3 or 12 or 17 or 25) Change(p, log with { Stage = log.Stage + 1 });
            Offer(p, npc);
            return 0;
        }
        if (slot != 0 || selection != 0) return 1;
        if (action == 3 && log.Stage is 4 or 13 or 18 or 26)
        {
            if (log.Stage == 26)
            {
                var shield = p.World.Items.Find("T1_OFF_SHIELD")!;
                if (!Network.WorldSession.Fits(p.Inventory.Bag, [(shield, 1)])) return 1;
                p.Session.GiveItem(p, shield, 1);
            }
            // Only the first crafting quest grants an item. Earlier definitions have empty rewards.
            p.Session.Send(Full(p, state: 1, clear: true));
            Change(p, log with { Stage = log.Stage switch { 4 => 5, 13 => 14, 18 => 19, _ => Boundary } });
            if (log.Stage == 26)
            {
                // Item and delivered checkpoint are stored together in the character snapshot.
                p.SaveItems();
                p.World.Saves.SaveNow(p.Character);
            }
            Offer(p, npc);
            return 0;
        }
        if (action == 1 && log.Stage is 5 or 14 or 19)
        {
            Change(p, log with { Stage = log.Stage switch { 5 => 6, 14 => 15, _ => 20 }, QuestId = Guid.NewGuid(), StartedAt = p.World.GameNow });
            // Clear the offer; the accepted quest is now carried by FullQuestInfo.
            Offer(p, npc);
            return 0;
        }
        return 1;
    }

    private static void Change(Player p, OpeningTutorialProgress next)
    {
        var old = p.Character.Tutorial!;
        var data = p.World.OpeningTutorial!;
        if (next.Stage == 19)
            next = next with { TutorialState = data.Tutorial.Find("HARDCLEARPREDB")!.Id };
        if (next.Stage is >= 0 and <= 3 or >= 6 and <= 12 or >= 15 and <= 17 or >= 20 and <= 25)
        {
            var task = Quest(p, next.Stage).Tasks[TaskIndex(next.Stage)];
            foreach (var entry in task.EntryStates.SkipLast(1))
                p.Session.Send(new TutorialUpdateEvent { d18 = data.Tutorial.Find(entry)!.Id });
            if (task.TutorialState is { } state) next = next with { TutorialState = data.Tutorial.Find(state)!.Id };
        }
        p.Character.Tutorial = next;
        p.TutorialInsideSince = null;
        p.TutorialGatherProgress = null;
        p.World.Saves.MarkChanged(p.Character);
        p.Session.Send(new TutorialUpdateEvent { d18 = next.TutorialState });
        if (next.Stage is not (5 or 14 or 19 or Boundary))
        {
            // FullQuestInfo constructs a fresh client quest model. Keep that model across task changes;
            // only acceptance replaces the quest itself. QuestProgressInfo refreshes the existing HUD.
            if (next.Stage is 6 or 15 or 20) SendFull(p);
            SendProgress(p);
        }
        foreach (var npc in p.Map!.Objects.Values.OfType<TutorialQuestGiver>()) p.Session.Send(NpcInfo(p, npc));
        if (next.Stage == 20)
        {
            TutorialWorld.SetBlocker(p, "BLOCKFORGE", 0);
            foreach (var forge in p.Map.Objects.Values.OfType<Building>().Where(b => b.Template.Name == OpeningTutorialData.Forge))
                p.Session.Send(BuildingAccess(p, forge));
        }
        Log.Info("tutorial", $"'{p.Character.Name}' opening stage {old.Stage} -> {next.Stage}");
    }

    public static bool ForgeLocked(Player p, Building building) => Active(p)
        && building.Template.Name == OpeningTutorialData.Forge && p.Character.Tutorial!.Stage < 20;

    public static AccessStatusEvent BuildingAccess(Player p, IAccessStatusObject building)
    {
        var status = building.AccessStatus();
        if (building is Building forge && ForgeLocked(p, forge)) status.bq9 = ["owner", "noaccess", "noaccess"];
        return status;
    }

    public static void BuildingInteracted(Player p, Building building)
    {
        if (TutorialJourney.Active(p)) { TutorialJourney.BuildingInteracted(p, building); return; }
        if (Active(p) && p.Character.Tutorial is { Stage: 20 } log && building.Template.Name == OpeningTutorialData.Forge
            && building.Map == p.Map && Vector2.Distance(p.PositionAt(DateTime.UtcNow), building.Position) <= TalkRange)
            Change(p, log with { Stage = 21 });
    }

    public static void FunctionUsed(Player p, int function)
    {
        if (TutorialJourney.Active(p)) { TutorialJourney.FunctionUsed(p, function); return; }
        // cb3.UseFunctionType.OpenInventory = 16. This reports a UI action, not an item/reward grant.
        if (Active(p) && p.Character.Tutorial is { Stage: 22 } log && function == 16)
            Change(p, log with { Stage = 23 });
    }

    public static void SwordSpellSelected(Player p, Item item, bool active, int slot)
    {
        if (TutorialJourney.Active(p)) { TutorialJourney.SpellSelected(p, item, active, slot); return; }
        if (Active(p) && p.Character.Tutorial is { Stage: 24 } log && active && slot == 0
            && p.Inventory.Equipment[0] == item && item.Template.Name == "T1_MAIN_SWORD" && item.Active[0] >= 0)
            Change(p, log with { Stage = 25 });
    }

    public static QuestGiverInfoForPlayerEvent NpcInfo(Player p, TutorialQuestGiver npc) => TutorialJourney.Active(p) ? TutorialJourney.NpcInfo(p, npc) : new()
    {
        BuildingObjectId = npc.Id, QuestGiverGuid = npc.Spawn is CriticalMember m ? m.Guid!.Value : Guid.Empty,
        // Type is already known from NewQuestGiverObject. This field is the questgiverTYPE index, not its object type.
        QuestGiverType = npc.QuestGiverType,
        dr4 = p.Character.Tutorial?.Stage is 5 or 14 or 19,
        // The client calculates its Complete button synchronously on opening the NPC. Advertise
        // eligibility when only the conversation remains, before the first click. This is not task
        // completion: op45 still marks the talk, and delivery is refused until stage 4.
        dr5 = p.Character.Tutorial?.Stage is 3 or 4 or 12 or 13 or 17 or 18 or 25 or 26,
        BarStamp = p.World.GameNow,
    };

    private static int TaskIndex(int stage) => stage switch
    { <= 2 => stage, 3 or 4 or 5 => 3, >= 25 => 5, >= 20 => stage - 20, >= 17 => 2, >= 15 => stage - 15, >= 12 => 6, _ => stage - 6 };
    private static OpeningTutorialData.Quest Quest(Player p, int stage) => stage < 6 ? p.World.OpeningTutorial!.First
        : stage < 15 ? p.World.OpeningTutorial!.Tower : stage < 20 ? p.World.OpeningTutorial!.Gathering : p.World.OpeningTutorial!.Crafting;
    // acquireitem is verified against carried stacks; neither request counts nor client counters are trusted.
    private static long[] AcquisitionCounts(Player p, OpeningTutorialData.Task task) =>
        task.Acquisitions.Select(a => Math.Min(a.Count, p.Inventory.Bag.Items
            .Where(i => i.Template.Name == a.Item).Sum(i => (long)i.Count))).ToArray();
    private static string GatherProgress(long[] counts) => JsonSerializer.Serialize(counts.Select(n => new[] { n }));
    private static string Variables(Player p, OpeningTutorialData.Quest quest)
    {
        var d = p.World.OpeningTutorial!;
        return JsonSerializer.Serialize(quest.Variables.Select(kind => kind switch
        {
            // Quest variables are resolved through the authored world catalog (a0.abs), not the
            // currently loaded instance. An instance alias makes variable construction fail.
            "dynamiccluster" => (object)d.Tutorial.StartCluster,
            // bop.o8 looks up the authored map first, then the questgiver GUID inside that map.
            "dynamicquestgiver" => new[] { new[] { d.Tutorial.StartCluster, d.Crafter.Guid!.Value.ToString() } },
            _ => throw new InvalidDataException($"Unsupported opening variable {kind}"),
        }).ToArray());
    }
    private static FullQuestInfoEvent Full(Player p, byte state = 0, bool clear = false)
    {
        var d = p.World.OpeningTutorial!;
        var log = p.Character.Tutorial!;
        var quest = Quest(p, log.Stage);
        return new FullQuestInfoEvent { ddn = p.Id, cdi = quest.Id, cdj = d.Crafter.Guid!.Value,
            // ct2.i(apm) also resolves this via a0.abs before looking up the giver GUID.
            cdk = d.Tutorial.StartCluster, cdm = clear ? Guid.Empty : log.QuestId,
            cdn = 0, cdo = log.StartedAt, cdp = state, cdq = Variables(p, quest) };
    }
    private static void SendFull(Player p) { if (p.Character.Tutorial!.Stage is not (5 or 14 or 19 or Boundary)) p.Session.Send(Full(p)); }
    private static void SendProgress(Player p, bool announce = true)
    {
        var stage = p.Character.Tutorial!.Stage;
        if (stage is 5 or 14 or 19 or Boundary) return;
        var index = TaskIndex(stage);
        var task = Quest(p, stage).Tasks[index];
        var counts = task.Kind == "acquireitem" ? AcquisitionCounts(p, task) : null;
        var gather = counts is not null ? GatherProgress(counts) : null;
        p.TutorialGatherProgress = gather;
        p.Session.Send(new QuestProgressInfoEvent { ddn = p.Id, dsd = index,
            // LogicalOr (cb0.wr / cbq.n9) reads achieved count and target count, not nested child arrays.
            dse = gather ?? (task.Kind == "or" ? "[[0,1]]"
                : task.Seconds > 0 ? JsonSerializer.Serialize(new[] { new[] { task.Seconds } }) : "[[]]"),
            // ct2.i(ax3) forwards these two values to day; QuestTrackerHudGui indexes
            // quest.Tasks[dsg].Missions[dsh] for the floating objective text. They are not old/new steps.
            dsf = counts is not null ? task.Acquisitions.Select((a, i) => counts[i] >= a.Count).ToArray()
                : [stage is 4 or 13 or 18 or 26], dsg = announce ? index : -1, dsh = announce ? 0 : -1 });
    }
    private static void Offer(Player p, TutorialQuestGiver npc)
    {
        if (TutorialJourney.Active(p)) { TutorialJourney.Offer(p, npc); return; }
        var d = p.World.OpeningTutorial!;
        var log = p.Character.Tutorial!;
        var offered = log.Stage == 19 ? d.Crafting : log.Stage == 14 ? d.Gathering : d.Tower;
        var available = log.Stage is 5 or 14 or 19;
        p.Session.Send(NpcInfo(p, npc));
        p.Session.Send(new QuestGiverQuestOfferedEvent { ddn = npc.Id, dr6 = available ? log.QuestId : Guid.Empty,
            dr7 = available ? offered.Id : -1, dr8 = 0, dr9 = Variables(p, offered),
            dsa = 0, dsb = log.StartedAt, dsc = 0 });
    }
}
