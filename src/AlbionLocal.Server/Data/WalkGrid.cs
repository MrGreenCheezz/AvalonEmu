using System.Numerics;

namespace AlbionLocal.Server.Data;

/// <summary>
/// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.6, bots-world SPEC §2.1): where a character can walk in a cluster, on 1 m
/// cells over the .cluster bounds. A cell is blocked when a placed tile's collision grid (<see cref="ClusterTemplate.Colliders"/>)
/// has flag bit 0 (BlockGround) there: each blocked cell centre of the tile, turned with its yaw, marks the world cell it lands in
/// (the reference walkgrid.py; tile scale and seasonal layers are not taken). A cell is free when no blocked cell lies in its 3×3
/// (the 0.75 m collision radius rounded up), and the walkable component is the 4-connected flood fill of free cells from the
/// exits' spawn positions, the travel points' and the respawn points (a seed in a blocked cell moved to the nearest free cell
/// within 3 m first; a road's: the points in front of its portal spots, stageL roads phase B; a mists zone's: its arrivals, wisp spawns and
/// mob spawn points, stageL mists phase B): sea, cliffs and building interiors are outside. A hellgate template (stageL hellgates SPEC §5.4) has none of them: its
/// seeds are its named markers (objective chest, exit portal, chain exit) and its active mob spawn points. The client keeps its own player inside the same component
/// (0 of 2,314 captured own positions in a blocked cell) and draws other characters through the same grid, so a bot that walks only
/// <see cref="Clear"/> lines inside it is never seen crossing a wall. A cluster with no free seed (dungeon floors, islands) has no
/// component (<see cref="ComponentCells"/> 0): no bots there. Immutable once built; built off the world thread (Bots/BotNavCache).
/// </summary>
public sealed class WalkGrid
{
    /// <summary>Cell flags: blocked by a tile, free (no blocked cell in its 3×3), in the walkable component.</summary>
    private const byte BlockedBit = 1, FreeBit = 2, ComponentBit = 4;

    /// <summary>The cells with a margin of one around the bounds (blocked cells just outside count for the free test).</summary>
    private readonly byte[] _cells;

    private readonly int _x0, _z0, _width, _depth;

    /// <summary>The bounds in whole cells: [MinX, MaxX) × [MinZ, MaxZ) (the .cluster origin and size).</summary>
    public int MinX { get; }

    public int MinZ { get; }

    public int MaxX { get; }

    public int MaxZ { get; }

    /// <summary>Cells of the bounds, of them blocked by a tile, and in the walkable component.</summary>
    public int Cells { get; private set; }

    public int BlockedCells { get; private set; }

    public int ComponentCells { get; private set; }

    /// <summary>The seeds the component grew from (after the move out of a blocked cell).</summary>
    public IReadOnlyList<Vector2> Seeds { get; private set; } = [];

    private WalkGrid(int minX, int minZ, int maxX, int maxZ)
    {
        (MinX, MinZ, MaxX, MaxZ) = (minX, minZ, maxX, maxZ);
        (_x0, _z0) = (minX - 1, minZ - 1);
        (_width, _depth) = (maxX - minX + 2, maxZ - minZ + 2);
        _cells = new byte[_width * _depth];
        Cells = (maxX - minX) * (maxZ - minZ);
    }

    /// <summary>
    /// The grid of <paramref name="t"/>: its colliders rasterised, the free cells, the component from its exits, travel points and respawn
    /// points (a hellgate's: its markers and mob spawn points). Tens of milliseconds for an open-world cluster (≈ 870k cells); call it off
    /// the world thread.
    /// </summary>
    public static WalkGrid Build(ClusterTemplate t)
    {
        var g = new WalkGrid((int)Math.Floor(t.Origin.X), (int)Math.Floor(t.Origin.Y), (int)Math.Floor(t.Origin.X + t.Size.X), (int)Math.Floor(t.Origin.Y + t.Size.Y));
        foreach (var c in t.Colliders) g.Rasterise(c);
        g.MarkFree();
        var seeds = t.Exits.Select(e => e.SpawnPosition ?? e.Position)
            .Concat(t.Travelpoints.Select(p => p.SpawnPosition ?? p.Position))
            .Concat(t.RespawnPoints.Select(r => r.Position))
            .Concat(t.HellgateMarkers.Select(m => m.Position))
            .Concat(t.HellgateMarkers.Count > 0 ? t.MobSpawnPoints.Select(m => m.Position) : [])
            .Concat(t.Info.Type.StartsWith("TUNNEL_", StringComparison.Ordinal) ? t.Critical.Where(m => m.Type == "tunnelexit").Select(PortalFront) : [])
            .Concat(MistsIds.IsInstance(t.Id) ? MistsSeeds(t) : [])
            .Concat(t.Warcamps.SelectMany(w => w.Spawns)); // stage N arena: the teams' camps (an arena has none of the seeds above)
        g.Fill(seeds);
        return g;
    }

    /// <summary>
    /// StageL roads phase B: a road has no exits, travel or respawn points; its seeds are the points in front of its portal spots, where a player
    /// coming through arrives (the member's tunnelexit spawnpos, else tiles.xml's: Tunnel_Exit_Inside 5.53 0, Tunnel_Exit 4 0, turned with the spot).
    /// </summary>
    private static Vector2 PortalFront(CriticalMember m)
    {
        var spawn = TileMath.V2((string?)m.Components.FirstOrDefault(c => c.Name.LocalName == "tunnelexit")?.Attribute("spawnpos"))
                    ?? new Vector2(m.Tile == "Tunnel_Exit_Inside" ? 5.53f : 4f, 0);
        var (dx, dz) = TileMath.Rotate(spawn.X, spawn.Y, m.Direction);
        return m.Position + new Vector2((float)dx, (float)dz);
    }

    /// <summary>
    /// StageL mists phase B: a mists zone (open, resource, Knightfall Abbey) has none of the seeds above either; its seeds are where players
    /// arrive (each mistsborderexit's position + R(direction)·spawnpos, the MistsSpawnpoint markers), its special exit spots, its wisp spawns and
    /// its mob spawn points.
    /// </summary>
    private static IEnumerable<Vector2> MistsSeeds(ClusterTemplate t) =>
        t.Critical.Where(m => m.Type == "mistsborderexit").Select(m =>
            {
                var sp = TileMath.V2(m.Attr("spawnpos")) ?? Vector2.Zero;
                var (dx, dz) = TileMath.Rotate(sp.X, sp.Y, m.Direction);
                return m.Position + new Vector2((float)dx, (float)dz);
            })
            .Concat(t.MistsSpots.Where(s => s.Kind is "MistsSpawnpoint" or "MistsSpecialExitSpawnpoint" or "mistswispspawn").Select(s => s.Position))
            .Concat(t.MobSpawnPoints.Select(m => m.Position));

    /// <summary>A grid of the bounds with the blocked cells <paramref name="blocked"/> and the component grown from <paramref name="seeds"/> (the self-test's hand-made maps).</summary>
    public static WalkGrid FromCells(int minX, int minZ, int maxX, int maxZ, IEnumerable<(int X, int Z)> blocked, IEnumerable<Vector2> seeds)
    {
        var g = new WalkGrid(minX, minZ, maxX, maxZ);
        foreach (var (x, z) in blocked)
            if (g.Index(x, z) is { } i) g._cells[i] |= BlockedBit;
        g.MarkFree();
        g.Fill(seeds);
        return g;
    }

    /// <summary>The cell index of (x, z) in the margin-padded array; null outside it.</summary>
    private int? Index(int x, int z)
    {
        int dx = x - _x0, dz = z - _z0;
        return dx < 0 || dz < 0 || dx >= _width || dz >= _depth ? null : dx + dz * _width;
    }

    private bool Has(int x, int z, byte bit) => Index(x, z) is { } i && (_cells[i] & bit) != 0;

    private bool InBounds(int x, int z) => x >= MinX && x < MaxX && z >= MinZ && z < MaxZ;

    /// <summary>The tile's blocked cell centres turned and moved into the world, each marking its cell.</summary>
    private void Rasterise(PlacedCollider c)
    {
        var grid = c.Grid;
        var flags = grid.Flags;
        for (var r = 0; r < grid.Height; r++)
            for (var col = 0; col < grid.Width; col++)
            {
                if ((flags[r * grid.Width + col] & 1) == 0) continue;
                var (rx, rz) = TileMath.Rotate(grid.Offset.X + col + 0.5, grid.Offset.Y + r + 0.5, c.Yaw);
                if (Index((int)Math.Floor(c.X + rx), (int)Math.Floor(c.Z + rz)) is { } i) _cells[i] |= BlockedBit;
            }
    }

    private void MarkFree()
    {
        var blocked = 0;
        for (var z = MinZ; z < MaxZ; z++)
            for (var x = MinX; x < MaxX; x++)
            {
                var i = Index(x, z)!.Value;
                if ((_cells[i] & BlockedBit) != 0)
                {
                    blocked++;
                    continue;
                }
                var free = true;
                for (var dz = -1; dz <= 1 && free; dz++)
                    for (var dx = -1; dx <= 1 && free; dx++)
                        free = !Has(x + dx, z + dz, BlockedBit);
                if (free) _cells[i] |= FreeBit;
            }
        BlockedCells = blocked;
    }

    /// <summary>The 4-connected flood fill of free cells from <paramref name="seeds"/> (a seed in a cell that is not free moved to the nearest free one within 3 m).</summary>
    private void Fill(IEnumerable<Vector2> seeds)
    {
        var stack = new Stack<(int X, int Z)>();
        var used = new List<Vector2>();
        var count = 0;
        foreach (var raw in seeds)
        {
            var seed = Has(Cell(raw.X), Cell(raw.Y), FreeBit) ? raw : NearestWith(raw, 3, FreeBit);
            if (seed is not { } s) continue;
            used.Add(s);
            var (x, z) = (Cell(s.X), Cell(s.Y));
            var i = Index(x, z)!.Value;
            if ((_cells[i] & ComponentBit) != 0) continue;
            _cells[i] |= ComponentBit;
            count++;
            stack.Push((x, z));
        }
        while (stack.Count > 0)
        {
            var (x, z) = stack.Pop();
            foreach (var (nx, nz) in (ReadOnlySpan<(int, int)>)[(x + 1, z), (x - 1, z), (x, z + 1), (x, z - 1)])
            {
                if (!InBounds(nx, nz)) continue;
                var i = Index(nx, nz)!.Value;
                if ((_cells[i] & (FreeBit | ComponentBit)) != FreeBit) continue;
                _cells[i] |= ComponentBit;
                count++;
                stack.Push((nx, nz));
            }
        }
        ComponentCells = count;
        Seeds = used;
    }

    private static int Cell(float v) => (int)Math.Floor(v);

    /// <summary>Whether a tile blocks cell (x, z) (outside the bounds: true).</summary>
    public bool Blocked(int x, int z) => !InBounds(x, z) || Has(x, z, BlockedBit);

    /// <summary>The centres of the cells in the walkable component, row by row (stageL hellgates: the start points' search, SPEC §5.4).</summary>
    public IEnumerable<Vector2> ComponentCentres()
    {
        for (var z = MinZ; z < MaxZ; z++)
            for (var x = MinX; x < MaxX; x++)
                if (Has(x, z, ComponentBit)) yield return new Vector2(x + 0.5f, z + 0.5f);
    }

    /// <summary>Whether cell (x, z) is in the walkable component.</summary>
    public bool Walkable(int x, int z) => InBounds(x, z) && Has(x, z, ComponentBit);

    /// <summary>Whether <paramref name="p"/>'s cell is in the walkable component.</summary>
    public bool Walkable(Vector2 p) => Walkable(Cell(p.X), Cell(p.Y));

    /// <summary>Whether the straight line a → b stays in the component: every 0.5 m sample's cell (and both ends) walkable.</summary>
    public bool Clear(Vector2 a, Vector2 b)
    {
        var n = Math.Max(1, (int)(Vector2.Distance(a, b) / 0.5f));
        for (var i = 0; i <= n; i++)
            if (!Walkable(Vector2.Lerp(a, b, (float)i / n))) return false;
        return true;
    }

    /// <summary>The centre of the walkable cell nearest to <paramref name="p"/> within <paramref name="maxRadius"/> m (its own when walkable); null when none.</summary>
    public Vector2? Nearest(Vector2 p, float maxRadius) => NearestWith(p, maxRadius, ComponentBit);

    /// <summary>Whether no tile blocks <paramref name="p"/>'s cell (the un-inflated grid: the ground right by a rock or a wall).</summary>
    public bool Open(Vector2 p) => !Blocked(Cell(p.X), Cell(p.Y));

    /// <summary>Whether the straight line a → b crosses no blocked cell (every 0.25 m sample): the short last step to a harvest node.</summary>
    public bool ClearOpen(Vector2 a, Vector2 b)
    {
        var n = Math.Max(1, (int)(Vector2.Distance(a, b) / 0.25f));
        for (var i = 0; i <= n; i++)
            if (!Open(Vector2.Lerp(a, b, (float)i / n))) return false;
        return true;
    }

    /// <summary>
    /// Mob navigation (stageN mobnav SPEC): whether cell (x, z) is free, no blocked cell in its 3×3 (in or out of the bots' component:
    /// a mob walks wherever it stands, its region is whatever free cells connect to it, so dungeon floors and islands work too).
    /// </summary>
    public bool Free(int x, int z) => InBounds(x, z) && Has(x, z, FreeBit);

    public bool Free(Vector2 p) => Free(Cell(p.X), Cell(p.Y));

    /// <summary>Whether the straight line a → b stays on free cells (every 0.5 m sample and both ends): a mob's leg.</summary>
    public bool ClearFree(Vector2 a, Vector2 b)
    {
        var n = Math.Max(1, (int)(Vector2.Distance(a, b) / 0.5f));
        for (var i = 0; i <= n; i++)
            if (!Free(Vector2.Lerp(a, b, (float)i / n))) return false;
        return true;
    }

    /// <summary>
    /// Line of sight (stageN mobnav SPEC): no tile-blocked cell on the line a → b (0.5 m samples), the first and last
    /// <paramref name="skip"/> m left out so a unit standing right by a wall or a rock is not hidden by its own corner.
    /// Outside the bounds counts as blocked.
    /// </summary>
    public bool Sight(Vector2 a, Vector2 b, float skip = 0.75f)
    {
        var length = Vector2.Distance(a, b);
        if (length <= 2 * skip) return true;
        var n = Math.Max(1, (int)(length / 0.5f));
        for (var i = 0; i <= n; i++)
        {
            var d = length * i / n;
            if (d < skip || d > length - skip) continue;
            if (!Open(Vector2.Lerp(a, b, (float)i / n))) return false;
        }
        return true;
    }

    /// <summary>The centre of the free cell nearest to <paramref name="p"/> within <paramref name="maxRadius"/> m (<paramref name="p"/> itself when free); null when none.</summary>
    public Vector2? NearestFree(Vector2 p, float maxRadius) => Free(p) ? p : NearestWith(p, maxRadius, FreeBit);

    /// <summary>The centre of the cell nearest to <paramref name="p"/> within <paramref name="maxRadius"/> m that no tile blocks; null when none.</summary>
    public Vector2? NearestOpen(Vector2 p, float maxRadius) => NearestWhere(p, maxRadius, (x, z) => !Blocked(x, z));

    /// <summary>Ring by ring around <paramref name="p"/>'s cell, the closest cell centre with <paramref name="bit"/> (rings up to <paramref name="maxRadius"/> + 1).</summary>
    private Vector2? NearestWith(Vector2 p, float maxRadius, byte bit) => NearestWhere(p, maxRadius, (x, z) => Has(x, z, bit));

    /// <summary>Ring by ring around <paramref name="p"/>'s cell, the closest cell centre in the bounds that <paramref name="ok"/> takes.</summary>
    private Vector2? NearestWhere(Vector2 p, float maxRadius, Func<int, int, bool> ok)
    {
        var (cx, cz) = (Cell(p.X), Cell(p.Y));
        var rings = (int)Math.Ceiling(maxRadius) + 1;
        Vector2? best = null;
        var bestDistance = float.MaxValue;
        for (var r = 0; r <= rings; r++)
        {
            for (var dz = -r; dz <= r; dz++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r || !InBounds(cx + dx, cz + dz) || !ok(cx + dx, cz + dz)) continue;
                    var centre = new Vector2(cx + dx + 0.5f, cz + dz + 0.5f);
                    var d = Vector2.Distance(centre, p);
                    if (d <= maxRadius + 0.71f && d < bestDistance) (best, bestDistance) = (centre, d);
                }
            if (best is not null && bestDistance <= r + 0.5f) return best; // every centre of a later ring is at least r + 0.5 m away
        }
        return best;
    }

    /// <summary>
    /// A walkable point <paramref name="min"/>-<paramref name="max"/> m from <paramref name="centre"/> in a random direction with a
    /// <see cref="Clear"/> line from it; null after <paramref name="tries"/> misses.
    /// </summary>
    public Vector2? RandomNear(Random random, Vector2 centre, float min, float max, int tries = 12)
    {
        for (var i = 0; i < tries; i++)
        {
            var angle = random.NextDouble() * 2 * Math.PI;
            var d = min + (float)random.NextDouble() * (max - min);
            var p = centre + new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * d;
            if (Walkable(p) && Clear(centre, p)) return p;
        }
        return null;
    }
}
