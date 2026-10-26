using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The guild hideouts' requests (analysis/work/stageP/hideouts/SPEC.md; the rules are <see cref="Systems.Hideouts"/>'): placing the kit (H1).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>A refusal the player should read: a system chat line.</summary>
    private void TellHideout(string text) => SendEvent(new SystemMessageEvent { d0p = text, d0q = 0 });

    /// <summary>
    /// PlaceableObjectPlace (op 69) with the hideout kit (stageP hideouts H1): the client places the kit as a placeable building
    /// (ConstructionConfirmationGui.OnDoPayPlaceableObject → cyy.u → c32.i_24: {0 stamp, 1 the kit, 2 the hideoutbuilding's type, 3 the
    /// position, 4 the rotation}; decoded in analysis/work/stageP/hideouts/h1). The rules now (<see cref="Systems.Hideouts.CheckPlace"/>); then
    /// after the kit's placementduration (15 s) once more, one kit leaves the stack and the hideout stands in its first construction phase
    /// (NewHideoutObject to the zone). Refused: a system line with the reason and the stack again (its count unchanged). No response (as op 69).
    /// </summary>
    private AlbionResponse? PlaceHideout(Player p, Map map, Item stack, PlaceableObjectPlaceRequest req)
    {
        var at = req.x9;
        if (req.x8 != world.Hideouts.BuildingType)
            Log.Info(Name, $"'{p.Character.Name}' places the hideout kit with building type {req.x8} (the data's is {world.Hideouts.BuildingType})");
        if (world.Hideouts.CheckPlace(p, at) is { } why)
        {
            Log.Info(Name, $"'{p.Character.Name}' hideout placement refused: {why}");
            TellHideout($"The hideout cannot be placed here: {why}.");
            SendCount(stack);
            return null;
        }
        Systems.Mounts.BeforeAction(p, "places a hideout");
        var seconds = world.Hideouts.Data.PlacementSeconds;
        Log.Info(Name, string.Create(CultureInfo.InvariantCulture, $"'{p.Character.Name}' places a hideout at ({at.X:0.#}, {at.Y:0.#}): {seconds:0.#} s"));
        map.Timers.At(world.Clock.Now + TimeSpan.FromSeconds(seconds), () =>
        {
            if (Player != p || p.Map != map) return;
            var index = p.Inventory.Bag.IndexOf(stack);
            var late = index < 0 || stack.Count <= 0 ? "the kit is no longer in the inventory" : world.Hideouts.CheckPlace(p, at);
            if (late is not null)
            {
                Log.Info(Name, $"'{p.Character.Name}' hideout placement failed at the end: {late}");
                TellHideout($"The hideout cannot be placed here: {late}.");
                if (index >= 0) SendCount(stack);
                return;
            }
            if (--stack.Count > 0) SendCount(stack);
            else
            {
                p.Inventory.Bag.Take(index);
                SendEvent(new InventoryDeleteItemEvent { ckr = stack.Id });
            }
            world.Hideouts.Place(p, at, req.ya);
            p.SaveItems();
            world.Saves.SaveNow(p.Character); // the hideout's row went at once: so does the kit it took
        });
        return null;
    }
}

/// <summary>Hideouts H2: the walk into a hideout's objects and the owner query.</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// ChangeCluster (op 41) with key 0 a hideout's surface object or its exit (stageP hideouts H2): through it at once, as the end of the
    /// entry channel (<see cref="Systems.Hideouts.StartChannel"/>) goes, answered {0 "@HIDEOUT@…", 1 the level's cluster, 2 the guild} or
    /// {0 the zone}. Refused: 1640 too far, 1641 no way in (access, construction, upgrade; a template still being read, asked for), with a system
    /// line. False for any other object.
    /// </summary>
    private bool HideoutChange(Player player, ChangeClusterRequest req, out ChangeClusterResponse? response)
    {
        response = null;
        if (player.Map is not { } map || !map.Objects.TryGetValue(req.ee, out var o) || o is not (HideoutObject or HideoutExitObject)) return false;
        if (player.Combat.Down is { } down)
        {
            response = Refuse(player, req.ee, ChangeClusterRefusal.NotNearAnExit, $"knocked down ({down.Kind})");
            return true;
        }
        if (System.Numerics.Vector2.Distance(player.PositionAt(DateTime.UtcNow), o.Position) > Systems.Hideouts.EntryReach)
        {
            response = Refuse(player, req.ee, ChangeClusterRefusal.NotNearAnExit, $"too far from the {o.Kind}");
            return true;
        }
        if (o is HideoutExitObject exit)
        {
            var to = Systems.Hideouts.Outside(exit.Hideout, player.Facing);
            Commit(player, to, $"hideout:{exit.Hideout.Id}:exit");
            response = new ChangeClusterResponse { anu = to.Cluster };
            return true;
        }
        var h = ((HideoutObject)o).Hideout;
        string? why = world.Hideouts.CanVisit(h, player.Character.Id);
        Data.ClusterTemplate? resolved = null;
        if (why is null && world.Hideouts.WorldOf(h.Instance) is null) why = "the hideout has no interior now";
        if (why is null && (resolved = world.CachedCluster(h.Instance)) is null)
        {
            world.PrefetchCluster(h.Instance);
            why = "its interior is being read, try again";
        }
        if (why is not null)
        {
            TellHideout($"The hideout cannot be entered now: {why}.");
            response = Refuse(player, req.ee, ChangeClusterRefusal.ExitNotConnected, why);
            return true;
        }
        var arrival = new Arrival(h.Instance, Systems.Hideouts.Inside(resolved!), player.Facing);
        Commit(player, arrival, $"hideout:{h.Id}:entrance");
        response = new ChangeClusterResponse { anu = h.Instance, anv = world.Hideouts.WorldOf(h.Instance), anw = world.Hideouts.Info(h).GuildName };
        return true;
    }

    /// <summary>HideoutGetOwnerInfo (op 399, vx {0 the hideout Guid}; ag8 {0 string}): the owner guild's name ("" for an unknown hideout).</summary>
    [Handles(OperationCodes.HideoutGetOwnerInfo, Status = SessionStatus.InWorld)]
    private AlbionResponse? HideoutGetOwnerInfo(HideoutGetOwnerInfoRequest req) => new HideoutGetOwnerInfoResponse { a7k = world.Hideouts.OwnerName(req.p9) };
}

/// <summary>Hideouts H3: home and access.</summary>
public sealed partial class WorldSession
{
    /// <summary>The RC of a refused hideout request without its own code (the client's callbacks show their generic error).</summary>
    public const short HideoutRefused = 1;

    /// <summary>
    /// MakeHome (op 205, w7 {0 the hideout management}; HideoutManagementGui.OnClickMakeHome asks first when a home is set): the hideout becomes
    /// the character's home (<see cref="Systems.Hideouts.MakeHome"/>: UpdateHome 216 with the new home string), RC 0. Refused: RC 1 and a system line.
    /// </summary>
    [Handles(OperationCodes.MakeHome, Status = SessionStatus.InWorld)]
    private AlbionResponse? MakeHome(MakeHomeRequest req)
    {
        if (Player is not { } p) return null;
        if (world.Hideouts.MakeHome(p, req.vq) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{p.Character.Name}' make home at object {req.vq} refused: {why}");
        TellHideout($"This cannot be your home: {why}.");
        return new GenericResponse { ReturnCode = HideoutRefused };
    }

    /// <summary>LeaveHome (op 206, w8 {0 the hideout management}): the home hideout is given up (UpdateHome to ""), RC 0; else RC 1 and a line.</summary>
    [Handles(OperationCodes.LeaveHome, Status = SessionStatus.InWorld)]
    private AlbionResponse? LeaveHome(LeaveHomeRequest req)
    {
        if (Player is not { } p) return null;
        if (world.Hideouts.LeaveHome(p, req.th) is not { } why) return new GenericResponse();
        Log.Info(Name, $"'{p.Character.Name}' leave home at object {req.th} refused: {why}");
        TellHideout(why);
        return new GenericResponse { ReturnCode = HideoutRefused };
    }

    /// <summary>
    /// AccessRightsChangeSettings (op 198, od {0 object, 1 bool, 2 Guid, 3 preset, 4 groups, 5 roles}): on a hideout's surface object or
    /// management the preset (<see cref="Systems.Hideouts.SetAccess"/>), RC 0, refused RC 1 with a line. Other objects: no answer, as before (no
    /// other access rights are kept).
    /// </summary>
    [Handles(OperationCodes.AccessRightsChangeSettings, Status = SessionStatus.InWorld)]
    private AlbionResponse? AccessRightsChangeSettings(AccessRightsChangeSettingsRequest req)
    {
        if (Player is not { } p) return null;
        var why = world.Hideouts.SetAccess(p, req.d, req.g, req.h, req.i, out var mine);
        if (!mine) return null;
        if (why is null) return new GenericResponse();
        Log.Info(Name, $"'{p.Character.Name}' access change of object {req.d} refused: {why}");
        TellHideout(why);
        return new GenericResponse { ReturnCode = HideoutRefused };
    }
}

/// <summary>Hideouts H4: food, upgrades, tribute.</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// HideoutFillNutrition (op 397, vu {0 the management, 1 the item stack, 2 the count}; HideoutManagementGui.OnDoDrop → the feed window): food
    /// into the hideout (<see cref="Systems.Hideouts.CheckFood"/>): the items leave the stack (NewSimpleItem with the new count, InventoryDeleteItem
    /// for the last), the food rises (425 to those inside), RC 0. Refused: RC 1 and a system line.
    /// </summary>
    [Handles(OperationCodes.HideoutFillNutrition, Status = SessionStatus.InWorld)]
    private AlbionResponse? HideoutFillNutrition(HideoutFillNutritionRequest req)
    {
        if (Player is not { } p) return null;
        var (h, item, count, why) = world.Hideouts.CheckFood(p, req.p4, req.p5, req.p6);
        if (h is null || item is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' hideout food refused: {why}");
            TellHideout(why);
            return new GenericResponse { ReturnCode = HideoutRefused };
        }
        var stack = p.Inventory.Bag.Find(req.p5)!;
        if ((stack.Count -= count) > 0) SendCount(stack);
        else
        {
            p.Inventory.Bag.Take(p.Inventory.Bag.IndexOf(stack));
            SendEvent(new InventoryDeleteItemEvent { ckr = stack.Id });
        }
        world.Hideouts.Feed(h, item, count);
        p.SaveItems();
        world.Saves.SaveNow(p.Character); // the food is stored at once: so are the items it took
        return new GenericResponse();
    }

    /// <summary>
    /// UpgradeHideout (op 394, {0 the management, 1 the client's price a7m}): the next level (<see cref="Systems.Hideouts.CheckUpgrade"/>): its
    /// silver (UpdateMoney) and resources leave the member at once, the upgrade runs its time (nobody enters; at the end the players inside are
    /// put out and the next entry builds the new level). RC = bcv.UpgradeResult (0 Ok, 2 AlreadyUpgrading, 3 AlreadyAtMaxLevel, 4 NotInOwnersGuild,
    /// 5 InvalidGuildPermission, 6 NotEnoughSilver, 8 NotEnoughResources) [H: how the client reads it is not traced].
    /// </summary>
    [Handles(OperationCodes.UpgradeHideout, Status = SessionStatus.InWorld)]
    private AlbionResponse? UpgradeHideout(UpgradeHideoutRequest req)
    {
        if (Player is not { } p) return null;
        var (result, h, next, cost, why) = world.Hideouts.CheckUpgrade(p, req.ah1, req.ah2);
        if (result != Systems.HideoutUpgradeResult.Ok || h is null || next is null)
        {
            Log.Info(Name, $"'{p.Character.Name}' hideout upgrade refused, {result}: {why}");
            TellHideout($"The hideout cannot be upgraded: {why}.");
            return new GenericResponse { ReturnCode = (short)result };
        }
        Systems.Wallet.TrySpend(p, next.UpgradeSilver * Game.Pricing.A7m, $"the upgrade of {h}");
        foreach (var (item, count) in cost) TakeItems(p, item, count);
        world.Hideouts.StartUpgrade(h, next, p);
        p.SaveItems();
        world.Saves.SaveNow(p.Character);
        return new GenericResponse();
    }

    /// <summary>HideoutSetTribute (op 400, vy {0 the hideout's object, 1 a7m}): the daily tribute for the guild's managers, RC 0; else 16000 (NoPermission).</summary>
    [Handles(OperationCodes.HideoutSetTribute, Status = SessionStatus.InWorld)]
    private AlbionResponse? HideoutSetTribute(HideoutSetTributeRequest req) =>
        Player is not { } p ? null : new GenericResponse { ReturnCode = world.Hideouts.SetTribute(p, req.qa, req.qb) };
}
