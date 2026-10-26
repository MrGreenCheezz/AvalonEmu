using System.Numerics;

namespace AlbionLocal.Server.Data;

/// <summary>
/// Stage I bots: a place a city bot walks to and dwells at (bots-world SPEC §2.1): <see cref="At"/> the walkable approach point
/// (<see cref="WalkGrid.Nearest"/> within 25 m of the object), <see cref="Facing"/> toward the object's centre, the pick
/// <see cref="Weight"/> and the dwell time range in seconds; a door's <see cref="Building"/> is "bank" or "market" (bot-travel: a gatherer banks at
/// its home city's bank door), null for the other kinds.
/// </summary>
public sealed record BotPoi(string Kind, Vector2 At, float Facing, float Weight, (float Min, float Max) Dwell, string? Building = null);

/// <summary>
/// Stage I bots: where a bot arrives and leaves by (bots SPEC §1.4 turnover): the exit's trigger <see cref="Position"/>, the walkable
/// <see cref="Arrival"/> point by its spawn position (where a player coming through it stands), the <see cref="Facing"/> away from the
/// exit there (Join key 10's rule, <see cref="Transfers.Facing"/>), and the <see cref="Gate"/>: the walkable cell nearest to the trigger
/// (within 6 m, else the arrival point), where a leaving bot walks to and is gone, as a player walking into the exit.
/// </summary>
public sealed record BotExit(Vector2 Position, Vector2 Arrival, float Facing, Vector2 Gate);

/// <summary>
/// Stage I bots (bots-world SPEC §2.1): the points of interest of a cluster for its city bots, with their kinds, weights and dwell
/// times [inference]: the bank and marketplace doors (exit tiles *_BANK_ENTRANCE / *_MARKETPLACE_ENTRANCE) 4 / 20-90 s, other exits 1 /
/// 3-10 s, the crafting stations and other action buildings 3 / 15-60 s, travel points 2 / 10-40 s, the island access point and the
/// expedition agent 1 / 10-30 s, respawn points and location markers 1 / 5-20 s, real-estate plots 0.2 / 5-15 s. An object whose
/// approach is not found within 25 m is left out (<see cref="Dropped"/>).
/// </summary>
public static class BotPois
{
    /// <summary>How far from an object its approach point may lie (m).</summary>
    public const float ApproachRadius = 25;

    /// <summary>The points of interest of <paramref name="t"/> on <paramref name="grid"/>; <paramref name="dropped"/> = the objects without an approach.</summary>
    public static IReadOnlyList<BotPoi> Of(ClusterTemplate t, WalkGrid grid, out int dropped)
    {
        var list = new List<BotPoi>();
        var missed = 0;
        void Add(string kind, Vector2 centre, Vector2? spot, float weight, float min, float max, string? building = null)
        {
            if (grid.Nearest(spot ?? centre, ApproachRadius) is not { } at)
            {
                missed++;
                return;
            }
            var facing = Vector2.Distance(at, centre) < 0.5f ? 0 : AI.MotionMaster.Facing(at, centre);
            list.Add(new BotPoi(kind, at, facing, weight, (min, max), building));
        }
        foreach (var e in t.Exits)
        {
            var door = e.Tile.EndsWith("_BANK_ENTRANCE", StringComparison.OrdinalIgnoreCase) || e.Tile.EndsWith("_MARKETPLACE_ENTRANCE", StringComparison.OrdinalIgnoreCase);
            if (door) Add("door", e.Position, e.SpawnPosition, 4, 20, 90, e.Tile.EndsWith("_BANK_ENTRANCE", StringComparison.OrdinalIgnoreCase) ? "bank" : "market");
            else Add("exit", e.Position, e.SpawnPosition, 1, 3, 10);
        }
        foreach (var b in t.Buildings) Add("station", b.Position, null, 3, 15, 60);
        foreach (var p in t.Travelpoints) Add("travel", p.Position, p.SpawnPosition, 2, 10, 40);
        foreach (var a in t.IslandAccessPoints) Add("island", a.Position, null, 1, 10, 30);
        foreach (var a in t.ExpeditionAgents) Add("expedition", a.Position, null, 1, 10, 30);
        foreach (var r in t.RespawnPoints) Add("respawn", r.Position, null, 1, 5, 20);
        foreach (var m in t.LocationMarkers) Add("marker", m.Position, null, 1, 5, 20);
        foreach (var r in t.RealEstates) Add("plot", r.Position, null, 0.2f, 5, 15);
        dropped = missed;
        return list;
    }

    /// <summary>The exits of <paramref name="t"/> with a walkable arrival point within 6 m of their spawn position, and their gates.</summary>
    public static IReadOnlyList<BotExit> Exits(ClusterTemplate t, WalkGrid grid) =>
    [
        .. t.Exits.Where(e => e.SpawnPosition is not null)
            .Select(e => grid.Nearest(e.SpawnPosition!.Value, 6) is { } at ? new BotExit(e.Position, at, Transfers.Facing(e.Position, at), grid.Nearest(e.Position, 6) ?? at) : null)
            .OfType<BotExit>(),
    ];
}
