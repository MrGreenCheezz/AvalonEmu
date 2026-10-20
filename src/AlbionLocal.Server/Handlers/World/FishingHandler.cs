using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The fishing requests (analysis/work/stageG/fishing/SPEC.md §3.1, §4; the rules and timers in <see cref="Fishing"/>): FishingStart
/// (316), FishingCast (318), FishingCatch (319), FishingPull (320), FishingGiveLine (321), FishingFinish (322), FishingCancel
/// (323); FishingCasting (317) has no caller in the client. None has a response class: a request with a request id (255) gets
/// the bare answer {253, 255} (rc 0, or <see cref="Content.FishingSettings.Refusal"/>, on which the client aborts locally), one
/// without (FishingFinish, FishingCancel) none.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The bare answer of a fishing request: rc <paramref name="rc"/> when it carried a request id, else none.</summary>
    private AlbionResponse? FishingAnswer(short rc = 0) => RequestId is null ? null : new GenericResponse { ReturnCode = rc };

    /// <summary>
    /// A refused fishing request: the client aborts its own action on the non-zero rc and sends no FishingCancel (SPEC §3.1:
    /// cuw.ah6 sends op 323 only for reason Default), so a running session ends here too (FishingCancel of its state to the
    /// fisher and its observers, the float's Leave); otherwise observers would keep the pose and new ones get a stale state.
    /// </summary>
    private AlbionResponse? RefuseFishing(Player p, string what, string why)
    {
        Log.Info(Name, $"'{p.Character.Name}' {what} refused: {why}");
        Fishing.End(p, $"{what} refused ({why})");
        return FishingAnswer(world.Fishing.Refusal);
    }

    /// <summary>
    /// FishingStart (op 316, s1): 0 the client's GameTimeStamp (the session stamp), 1 the clicked point, 2 the rod's object id (the
    /// main hand's if it is a rod, else the best rod of the inventory: shown in hand, not equipped). Checks (§8): standing, not
    /// mounted, no cast or station action; the rod is the player's (equipment or inventory) with items.xml fishing="true" and
    /// durability left; the point within <see cref="Content.FishingSettings.CastRange"/> + slack; the cluster has fishing zones
    /// [assume]. A running session ends first (FishingCancel of its state; the captured fishers re-cast 0.1-0.7 s after one; a
    /// refusal ends it as well), a harvest under way too. Accepted: state Casting, FishingStart {3 3, 4 key 0} to the fisher and
    /// its observers, rc 0, and the fallback of <see cref="Fishing.AwaitCast"/> when no FishingCast follows.
    /// </summary>
    [Handles(OperationCodes.FishingStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? FishingStart(FishingStartRequest req)
    {
        if (Player is not { Map: { } map } player) return null;
        var settings = world.Fishing;
        if (player.Combat.Down is not null) return RefuseFishing(player, "FishingStart", "knocked down");
        if (player.Mount.State != MountState.None) return RefuseFishing(player, "FishingStart", $"{player.Mount.State}");
        if (player.Combat.Cast is not null || player.Action is { Over: false }) return RefuseFishing(player, "FishingStart", "a cast or station action is under way");
        if (player.Inventory.FindItem(req.ls) is not { } rod || !Fishing.IsRod(rod.Template))
            return RefuseFishing(player, "FishingStart", $"object {req.ls} is not a fishing rod of the player");
        if (rod.DurabilityRaw <= 0) return RefuseFishing(player, "FishingStart", $"{rod.Template.Name} is broken");
        var at = player.PositionAt(DateTime.UtcNow);
        var distance = Vector2.Distance(at, req.lr);
        if (distance > settings.CastRange + settings.RangeSlack)
            return RefuseFishing(player, "FishingStart", string.Create(CultureInfo.InvariantCulture, $"the point is {distance:0.0} m away"));
        if (map.FishingZones.Count == 0) return RefuseFishing(player, "FishingStart", $"no fishing in {map.Cluster}");
        if (player.Fishing is not null) Fishing.End(player, "a new FishingStart");
        if (player.Harvest is not null) CancelHarvest(player, "fishing", tellClient: true);
        Mounts.BeforeAction(player, "fishes"); // a flag cast and the world-interaction breaking buffs end (no mount: checked above)
        var spot = player.Movement?.Target ?? at;
        var s = new FishingSession(rod, req.wf, spot);
        player.Fishing = s;
        Fishing.Send(player, s, FishingState.Casting);
        Fishing.AwaitCast(player, s);
        Log.Info(Name, $"'{player.Character.Name}' starts fishing with {rod.Template.Name}");
        return FishingAnswer();
    }

    /// <summary>
    /// FishingCast (op 318, sv): 0 now, 1 the landing point, 2 whether it is water (the client's EffectMaterial &lt; 2). In state
    /// Casting: power = (key 0 − the session stamp) / 1 s, at most 1; the point within reach. Water: state Cast (FishingStart 4),
    /// the zone the point picks, the float after its flight and the bite after the zone's delay (<see cref="Fishing.Launch"/>).
    /// Ground: GroundFail (11); the client ends it with FishingCancel 1 s later, the float still splashes. rc 0; otherwise the
    /// refusal (the client aborts locally, and the session ends here too).
    /// </summary>
    [Handles(OperationCodes.FishingCast, Status = SessionStatus.InWorld)]
    private AlbionResponse? FishingCast(FishingCastRequest req)
    {
        if (Player is not { Map: { } map } player) return null;
        if (player.Fishing is not { State: FishingState.Casting } s) return RefuseFishing(player, "FishingCast", $"no cast to make ({player.Fishing?.State.ToString() ?? "not fishing"})");
        var settings = world.Fishing;
        var distance = Vector2.Distance(player.PositionAt(DateTime.UtcNow), req.ln);
        if (distance > settings.CastRange + settings.RangeSlack)
            return RefuseFishing(player, "FishingCast", string.Create(CultureInfo.InvariantCulture, $"the landing point is {distance:0.0} m away"));
        s.Power = (float)Math.Clamp((req.wf - s.Stamp) / (double)TimeSpan.TicksPerSecond, 0, 1);
        s.Target = req.ln;
        if (!req.lo)
        {
            Fishing.Send(player, s, FishingState.GroundFail);
            Fishing.Launch(player, s);
            Fishing.EndLater(player, s);
            return FishingAnswer();
        }
        (s.Zone, s.Ocean) = Fishing.Pick(world, map, s.Target);
        Fishing.Send(player, s, FishingState.Cast);
        Fishing.Launch(player, s);
        Log.Debug(Name, string.Create(CultureInfo.InvariantCulture,
            $"'{player.Character.Name}' casts {distance:0.0} m (power {s.Power:0.##}) into {s.Zone?.ToString() ?? (s.Ocean ? "the default ocean" : "the default freshwater")}"));
        return FishingAnswer();
    }

    /// <summary>
    /// FishingCatch (op 319, sx): 0 now, 1 true = reel in (the client's float is out, no bite yet), false = strike (the client saw
    /// the bite). Bitten: the strike (either key, a reel-in racing the bite counts as one [assume]) within the window by the
    /// server's clock (<see cref="Content.FishingSettings.CatchTimeout"/> + <see cref="Content.FishingSettings.StrikeSlack"/>, when
    /// the miss tick ends it), then the burst (§11): NewFishingZoneObject, NewFloatObject 3, FishingMiniGame, rc 0. Reel-in while
    /// the float is out or flying: FishingCancel of the state (4) and the float's Leave. Anything else: the refusal (and the end).
    /// </summary>
    [Handles(OperationCodes.FishingCatch, Status = SessionStatus.InWorld)]
    private AlbionResponse? FishingCatch(FishingCatchRequest req)
    {
        if (Player is not { Map: not null } player) return null;
        if (player.Fishing is not { } s) return RefuseFishing(player, "FishingCatch", "not fishing");
        switch (s.State)
        {
            case FishingState.Bitten:
                var late = (world.GameNow - s.BiteStamp) / (double)TimeSpan.TicksPerSecond - (world.Fishing.CatchTimeout + world.Fishing.StrikeSlack);
                if (late > 0)
                {
                    Fishing.End(player, string.Create(CultureInfo.InvariantCulture, $"the strike came {late:0.###} s after the window"), missed: true);
                    return RefuseFishing(player, "FishingCatch", "too late");
                }
                return Fishing.Strike(player, s) ? FishingAnswer() : RefuseFishing(player, "FishingCatch", "nothing to catch");
            case FishingState.Cast or FishingState.Waiting when req.lp:
                Fishing.End(player, "reeled in");
                return FishingAnswer();
            default:
                return RefuseFishing(player, "FishingCatch", $"state {s.State}");
        }
    }

    /// <summary>FishingPull (op 320, s0: once per press in the minigame): state Pull, FishingStart 7 to the fisher and its observers (their reel pose). rc 0 always (the client's callback is empty).</summary>
    [Handles(OperationCodes.FishingPull, Status = SessionStatus.InWorld)]
    private AlbionResponse? FishingPull(FishingPullRequest req) => Reel(FishingState.Pull);

    /// <summary>FishingGiveLine (op 321, sz: once per release): state GiveLine, FishingStart 8. rc 0 always.</summary>
    [Handles(OperationCodes.FishingGiveLine, Status = SessionStatus.InWorld)]
    private AlbionResponse? FishingGiveLine(FishingGiveLineRequest req) => Reel(FishingState.GiveLine);

    private AlbionResponse? Reel(FishingState to)
    {
        if (Player is { Fishing: { State: FishingState.MiniGame or FishingState.Pull or FishingState.GiveLine } s } player && s.State != to)
            Fishing.Send(player, s, to);
        return FishingAnswer();
    }

    /// <summary>
    /// FishingFinish (op 322, sy, no answer): 0 now, 1 the client's minigame result. A success counts only gamedata Fishing safety
    /// (3 s, the client's own wait) − <see cref="Content.FishingSettings.SafetyTolerance"/> after the strike by the server's clock,
    /// else it is a fail (§4). Success: <see cref="LandCatch"/>; fail: <see cref="Fishing.Lose"/>. Outside the minigame: ignored.
    /// </summary>
    [Handles(OperationCodes.FishingFinish, Status = SessionStatus.InWorld)]
    private AlbionResponse? FishingFinish(FishingFinishRequest req)
    {
        if (Player is not { Fishing: { State: FishingState.MiniGame or FishingState.Pull or FishingState.GiveLine } s } player) return FishingAnswer();
        var elapsed = (world.GameNow - s.MiniGameStart) / (double)TimeSpan.TicksPerSecond;
        var safety = (world.GameData?.FishingSafety ?? world.Fishing.Safety) - world.Fishing.SafetyTolerance;
        if (!Fishing.HasRod(player, s)) Fishing.End(player, "the rod has left the inventory", state: FishingState.ToolFail);
        else if (!req.lq) Fishing.Lose(player, s, "the client's minigame failed");
        else if (elapsed < safety) Fishing.Lose(player, s, string.Create(CultureInfo.InvariantCulture, $"a success {elapsed:0.##} s after the strike (before the {safety:0.##} s safety)"));
        else LandCatch(player, s);
        return FishingAnswer();
    }

    /// <summary>FishingCancel (op 323, su, no keys, no answer): the session ends (FishingCancel event of its state, the float's Leave).</summary>
    [Handles(OperationCodes.FishingCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? FishingCancel(FishingCancelRequest req)
    {
        if (Player is { } player) Fishing.End(player, "cancelled by the client");
        return FishingAnswer();
    }

    /// <summary>
    /// A won minigame, the burst of §11: the rod's NewEquipmentItem with its wear (<see cref="Content.FishingSettings.RodWear"/>),
    /// per item with fishingfame its destiny board events and UpdateFame {2 fishingfame × amount × the cluster's fishingfamefactor
    /// × Rates:Fame × Rates:GatheringFame, 5 true} (§9), the items (× Rates:Harvest, + the fishingyield bonus of the gear, as a
    /// harvest's; NewSimpleItem + InventoryPutItem), then FishingStart 9 and the float caught (4); the client sends FishingCancel
    /// after its success animation. A catch that no longer fits the inventory (filled during the minigame; the strike checked
    /// it) is lost as a fail, the charge used [assume: the official handling is not captured].
    /// </summary>
    private void LandCatch(Player player, FishingSession s)
    {
        var map = player.Map!;
        var gains = s.Catch.Select(c => (c.Item, Base: c.Amount, Count: Rates.Scale(c.Amount, world.Rates.Harvest, map.Random))).ToList();
        gains = [.. gains.Select(g => g with { Count = g.Count + (g.Count <= 0 ? 0 : Passives.Bonus(g.Count, Passives.YieldBonus(player, "fishingyield", "FISH", g.Item.Tier) + world.Guilds.GatheringBonus(player), map.Random)) })];
        if (!Fits(player.Inventory.Bag, gains.Where(g => g.Count > 0).Select(g => (g.Item, g.Count))))
        {
            Fishing.Lose(player, s, "the catch does not fit the inventory");
            return;
        }
        if (world.Fishing.RodWear > 0)                                              // the rod is still the player's (FishingFinish checked)
        {
            var rodBefore = s.Rod.DurabilityRaw;
            s.Rod.DurabilityRaw = Math.Max(0, s.Rod.DurabilityRaw - (long)Math.Round(world.Fishing.RodWear * 10000));
            if (rodBefore > 0 && s.Rod.DurabilityRaw <= 0) world.Plugins.Broke(player, s.Rod, "fishing"); // Items.OnBreak
            SendItemObject(s.Rod);                                                  // 1. the rod, key 8 = its new durability
        }
        var factor = Combat.DangerOf(world, map)?.FishingFameFactor ?? 1;
        foreach (var (item, amount, _) in gains)
        {
            // 2. fame (§9): as a harvest's, with fishingfame and fishingfamefactor; trash and seaweed have none.
            var fame = Rates.ScaleFame(Destiny.HarvestFame(Fishing.Fame(item), amount, factor), world.Rates.Fame * world.Rates.GatheringFame);
            if (fame > 0) Destiny.Award(player, fame, Destiny.Fishing(item), source: item);
            if (Fishing.Fame(item) > 0)
            {
                world.Bounties.Fished(player, item); // PVE-DESIGN step 11: a catch for the fish bounties (not trash)
                world.Plugins.Harvested(player, item.Name, amount, "fish"); // the plugins' Harvested
                world.GuildGoals.OnFish(player); // stage I: the weekly guild goals
            }
        }
        foreach (var (item, _, count) in gains)
            if (count > 0) GiveItem(player, item, count);                           // 3. the items
        Fishing.Send(player, s, FishingState.Success);                              // 4. FishingStart 9
        if (s.Float is { } f) Fishing.SetFloat(f, FloatState.Caught);               // 5. the float caught
        player.SaveItems();
        Log.Info(Name, $"'{player.Character.Name}' caught {string.Join(", ", gains.Select(g => $"{g.Count} x {g.Item.Name}"))}");
        Fishing.EndLater(player, s);
    }

    /// <summary>
    /// A Move of a fishing player: a stop point more than <see cref="Content.FishingSettings.WalkOffTolerance"/> from its spot ends the
    /// session (as a harvest's walk-off; a turn on the spot keeps it).
    /// </summary>
    private void FishingMoved(Player player)
    {
        if (player.Fishing is not { } s || player.Movement is not { } move) return;
        if (Vector2.Distance(move.Target, s.Spot) > world.Fishing.WalkOffTolerance) Fishing.End(player, "the player walks off");
    }
}
