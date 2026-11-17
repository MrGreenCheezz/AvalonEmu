using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The two operations of a dragon area entrance (analysis/work/stageR/dragon-areas/SPEC.md §3). The entrance window (DragonAreaEntranceGui.OnReadyClicked)
/// starts the client's own use state (cvo.f5(id)) and sends UseDragonAreaEntrance 549 (dff.af 0x180AF4B20 → c32.n: {0 stamp, 1 the entrance, 2 false});
/// the matchmaking window's Abort sends it with key 2 true (OnClickAbort 0x1810FF990, cvo.ah6). Its result callback (OnUseEntranceResult 0x1810FC7B0)
/// shows 21967-21970 (21970 "blocked for", with key 0 as the time). The matchmaking window opens when 699 says WaitingToEnter (cvo.f5(am6)
/// 0x1809D0490 posts c5t); its Enter sends EnterDragonAreaEntrance 550 (EnterInternally 0x1810FF480 → dff.af 0x180AF4F90 → c32.i: {1 the entrance,
/// 2 and 3 the binding of event 700}), whose errors are 22007-22013 (ShowEnterEntranceError 0x1811003F0; which text is which is not known).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>The use failed (@DRAGON_AREA_ENTRANCE_USE_ENTRANCE_FAILED) [assume: the first of the four codes the window knows].</summary>
    public const short DragonUseFailed = 21967;

    /// <summary>The window's "disabled by feature switch" [infer: the second code; DragonAreas:Enabled false].</summary>
    public const short DragonUseDisabled = 21968;

    /// <summary>Blocked for a time (key 0 of the answer: until when) — the code ShowUseEntranceError reads the response for.</summary>
    public const short DragonUseBlockedFor = 21970;

    /// <summary>The entry failed [assume: the first of the codes ShowEnterEntranceError knows, 0x55F7].</summary>
    public const short DragonEnterFailed = 22007;

    /// <summary>How near the entrance the player must stand [assume].</summary>
    public const float DragonEntranceReach = 8;

    [Handles(OperationCodes.UseDragonAreaEntrance, Status = SessionStatus.InWorld)]
    private UseDragonAreaEntranceResponse? UseDragonAreaEntrance(UseDragonAreaEntranceRequest req)
    {
        if (Player is not { } player) return null;
        if (req.j6) // key 2 true: the matchmaking window's Abort
        {
            world.DragonAreas.Cancel(player, "aborted");
            return new UseDragonAreaEntranceResponse();
        }
        if (DragonEntranceRefusal(player, req.j5) is { } refused)
        {
            Log.Info(Name, $"'{player.Character.Name}' uses dragon area entrance {req.j5}: refused {refused.ReturnCode}");
            return refused;
        }
        if (!world.Config.DragonAreasEnabled)
        {
            Expeditions.Tell(player, "The Ancient Lands are closed on this server (DragonAreas:Enabled).");
            return new UseDragonAreaEntranceResponse { ReturnCode = DragonUseDisabled };
        }
        world.DragonAreas.Use(player, (DragonAreaEntrance)player.Map!.Objects[req.j5]);
        return new UseDragonAreaEntranceResponse();
    }

    [Handles(OperationCodes.EnterDragonAreaEntrance, Status = SessionStatus.InWorld)]
    private EnterDragonAreaEntranceResponse? EnterDragonAreaEntrance(EnterDragonAreaEntranceRequest req)
    {
        if (Player is not { } player) return null;
        if (DragonEntranceRefusal(player, req.j2) is { } refused)
            return new EnterDragonAreaEntranceResponse { ReturnCode = refused.ReturnCode == DragonUseBlockedFor ? DragonEnterFailed : refused.ReturnCode };
        if (world.DragonAreas.Enter(player, (DragonAreaEntrance)player.Map!.Objects[req.j2]) is { } why)
        {
            Log.Info(Name, $"'{player.Character.Name}' enters dragon area entrance {req.j2}: refused, {why}");
            Expeditions.Tell(player, $"The Ancient Lands cannot be entered: {why}.");
            return new EnterDragonAreaEntranceResponse { ReturnCode = DragonEnterFailed };
        }
        return new EnterDragonAreaEntranceResponse { bnq = world.GameNow };
    }

    /// <summary>The entrance checks: an Open entrance of the player's map in reach, the player up and not under the entrance's blocker debuff.</summary>
    private UseDragonAreaEntranceResponse? DragonEntranceRefusal(Player player, long id)
    {
        if (player.Map is not { } map || !map.Objects.TryGetValue(id, out var o) || o is not DragonAreaEntrance entrance)
            return new UseDragonAreaEntranceResponse { ReturnCode = DragonUseFailed };
        if (entrance.State != DragonAreaEntranceState.Open || player.Combat.Down is not null
            || Vector2.Distance(player.PositionAt(DateTime.UtcNow), entrance.Position) > DragonEntranceReach + world.Config.Transfer.PositionTolerance)
            return new UseDragonAreaEntranceResponse { ReturnCode = DragonUseFailed };
        if (world.Clusters?.DragonAreasIfLoaded?.EntranceBlockerDebuff is { } debuff
            && player.Effects.Spells.FirstOrDefault(s => s.Spell.Name == debuff && !s.Pending && s.Live(world.GameNow)) is { } blocker)
            return new UseDragonAreaEntranceResponse { ReturnCode = DragonUseBlockedFor, bnr = blocker.Subs.Where(s => s.Active).Select(blocker.EndOf).DefaultIfEmpty(0).Max() };
        return null;
    }
}
