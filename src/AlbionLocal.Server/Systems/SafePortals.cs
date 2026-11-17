using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The S.A.F.E. portals of the Outlands portal towns (analysis/work/stageR/safe-portal/SPEC.md, "SPEC" here): the town's portal object (606),
/// the costs (op 517), the binding a use makes (Join keys 127-131, <see cref="Character.SafePortal"/>), the personal return portal in the
/// destination (607) and the binding's end (608). The use and the return themselves are ChangeClusters (Handlers/World/SafePortalHandler).
/// Under <see cref="World.Sync"/>.
/// </summary>
public sealed class SafePortals(World world)
{
    /// <summary>The template member type of the town portal (a9v.g).</summary>
    public const string MemberType = "outlandsteleportationportal";

    /// <summary>The live bindings by character (filled by a use and by a Join with one): what <see cref="Update"/> ends and maps get portals for.</summary>
    private readonly Dictionary<Guid, SafePortalBinding> _active = [];

    /// <summary>The return portal object of each live binding that stands on a map.</summary>
    private readonly Dictionary<Guid, SafeReturnPortal> _portals = [];

    /// <summary>outlandsteleportationportals.xml when read (with the first portal town), else null.</summary>
    public SafePortalData? Data => world.Clusters?.SafePortalsIfLoaded;

    /// <summary>The binding of <paramref name="c"/> while it lasts at <paramref name="now"/> (GameTimeStamp), else null.</summary>
    public static SafePortalBinding? Active(Character c, long now) => c.SafePortal?.Binding is { } b && b.Until > now ? b : null;

    /// <summary>The return portal object of <paramref name="character"/>, if one stands.</summary>
    public SafeReturnPortal? ReturnPortalOf(Guid character) => _portals.GetValueOrDefault(character);

    /// <summary>
    /// At the fill of a map (MapSpawner): a portal town's outlandsteleportationportal members (with their world.xml Guid; the data file is read
    /// now) and the return portals of the live bindings in this cluster.
    /// </summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        var members = template.Critical.Where(m => m.Type == MemberType && m.Guid is not null).ToList();
        if (members.Count > 0 && world.Clusters is { } store)
        {
            try { _ = store.SafePortals; }
            catch (Exception e) when (e is InvalidDataException or IOException) { Log.Warn("world", $"outlandsteleportationportals.xml not read: {e.Message}"); }
            foreach (var m in members) yield return new SafePortal(world.ObjectIds.Next(), m);
        }
        foreach (var (owner, b) in _active.ToList())
            if (b.Cluster == map.Cluster && !_portals.ContainsKey(owner) && MakePortal(owner, b) is { } portal)
                yield return portal;
    }

    private SafeReturnPortal? MakePortal(Guid owner, SafePortalBinding b)
    {
        var tile = Data?.ReturnPortalTile ?? "OUTLANDS_TELEPORTATION_RETURN_PORTAL";
        var spawn = new SafeReturnPortalSpawn
        {
            Key = new SpawnKey(b.Cluster, "safeportal", Math.Abs(owner.GetHashCode())), Tile = tile, Position = new Vector2(b.X, b.Y),
            Component = new System.Xml.Linq.XElement("outlandsteleportationreturnportal"),
        };
        var portal = new SafeReturnPortal(world.ObjectIds.Next(), spawn, owner, b.Id, tile);
        _portals[owner] = portal;
        return portal;
    }

    /// <summary>
    /// A Join of <paramref name="c"/> into <paramref name="map"/>, before its objects and keys go out: an ended binding goes silently (the Join
    /// keys then carry none); so does one of a character who enters a royal city or a hideout ("…disabled if you … visit another city or
    /// Hideout", the tutorial). A live one is kept, and its return portal is put on the map when this is its cluster.
    /// </summary>
    public void Arriving(Character c, Map map)
    {
        if (c.SafePortal?.Binding is not { } b) return;
        var now = world.GameNow;
        var city = map.Template?.Info.Type.StartsWith("PLAYERCITY_", StringComparison.Ordinal) == true || world.Hideouts.WorldOf(map.Cluster) is not null;
        if (b.Until <= now || city)
        {
            Unbind(c, city ? $"entered {map.Cluster}" : "it ran out");
            return;
        }
        _active[c.Id] = b;
        if (map.Cluster == b.Cluster && map.Grid is not null && !_portals.ContainsKey(c.Id) && MakePortal(c.Id, b) is { } portal) map.AddObject(portal);
    }

    /// <summary>Join keys 127-131 (cx2.ai7 → cvq.a(ahi) 0x1809D18C0): the live binding; none = not sent (a5i.l: no binding).</summary>
    public static void JoinKeys(Character c, JoinResponse r, long now)
    {
        if (Active(c, now) is not { } b) return;
        r.bbt = b.Town;                      // [127] the portal town (a5i.j: the return portal's target cluster)
        r.bbu = b.Cluster;                   // [128] the return portal's cluster (the world map marker)
        r.bbv = b.Until;                     // [129] GameTimeStamp, the end
        r.bbw = b.Id;                        // [130] the return portal's id string (NewOutlandsTeleportationReturnPortal key 3)
        r.bbx = new Vector2(b.X, b.Y);       // [131] its position
    }

    /// <summary>
    /// The costs (RequestOutlandsTeleportationUsage 517 response air): the uses within costresettime (key 0), the baggage cost of what the
    /// player carries (key 1, InitialCost), with the repeat-use tax (key 2, TravelCosts) and when it next drops (key 3). Null without the data.
    /// </summary>
    public (long[] Uses, long Initial, long Travel, long? NextChange)? Quote(Player p)
    {
        if (Data is not { } data) return null;
        var now = world.GameNow;
        var items = p.Inventory.Equipment.Items.Concat(p.Inventory.Bag.Items)
            .Select(i => (world.ItemValues.Of(i.Template), 0L, i.Count, i.Template.Attrs.GetValueOrDefault("shopsubcategory1") == "tools"));
        var initial = data.InitialCost(items);
        var uses = Uses(p.Character, data, now);
        var (travel, _, next) = data.TravelCost(initial, uses, now);
        return (uses, initial, travel, next);
    }

    /// <summary>The uses of <paramref name="c"/> that still count for the repeat-use tax.</summary>
    private static long[] Uses(Character c, SafePortalData data, long now) =>
        [.. (c.SafePortal?.Uses ?? []).Where(t => now - t < TimeSpan.FromSeconds(data.CostResetSeconds).Ticks)];

    /// <summary>
    /// A use paid (the caller has charged the silver): the use counted, the binding made (<paramref name="town"/> → <paramref name="cluster"/>,
    /// the return portal at <paramref name="at"/>, returndurationinsec long), the binding buff and the blocker debuff put on.
    /// </summary>
    public SafePortalBinding Bind(Player p, string town, string cluster, Vector2 at)
    {
        var data = Data!;
        var c = p.Character;
        var now = world.GameNow;
        if (_portals.Remove(c.Id, out var old)) old.Map?.RemoveObject(old);
        var b = new SafePortalBinding(town, cluster, at.X, at.Y, now + TimeSpan.FromSeconds(data.ReturnSeconds).Ticks, Guid.NewGuid().ToString("N"));
        c.SafePortal = new SafePortalLog([.. Uses(c, data, now), now], b);
        _active[c.Id] = b;
        Pvp.ApplySpell(p, data.BindingBuff, now, send: false);
        Pvp.ApplySpell(p, data.BlockerDebuff, now);
        world.Saves.MarkChanged(c);
        return b;
    }

    /// <summary>Whether the blocker debuff (cannotuseobject outlandsteleportationportal, 30 s) is on <paramref name="p"/>.</summary>
    public bool Blocked(Player p) => Data is { } data && Pvp.SpellActive(p, data.BlockerDebuff, world.GameNow);

    /// <summary>The binding of <paramref name="c"/> ends: its return portal goes, the record keeps only the uses. No event (see <see cref="Clear"/>).</summary>
    public void Unbind(Character c, string why)
    {
        _active.Remove(c.Id);
        if (_portals.Remove(c.Id, out var portal)) portal.Map?.RemoveObject(portal);
        if (c.SafePortal is not { Binding: not null } log) return;
        c.SafePortal = log with { Binding = null };
        world.Saves.MarkChanged(c);
        Log.Info("world", $"'{c.Name}' loses the S.A.F.E. portal binding: {why}");
    }

    /// <summary>The binding of an online player ends: <see cref="Unbind"/>, then OutlandsTeleportationBindingCleared 608 (cvq.a 0x1809D1CC0: the toast, the map marker goes).</summary>
    public void Clear(Player p, string why)
    {
        if (p.Character.SafePortal?.Binding is null) return;
        Unbind(p.Character, why);
        p.Session.Send(new OutlandsTeleportationBindingClearedEvent { ddn = p.Id });
    }

    /// <summary>Every tick: the bindings that ran out end (an online owner gets 608; an offline one's record is cleared at its next Join).</summary>
    public void Update()
    {
        if (_active.Count == 0) return;
        var now = world.GameNow;
        foreach (var (owner, b) in _active.ToList())
        {
            if (b.Until > now) continue;
            _active.Remove(owner);
            if (_portals.Remove(owner, out var portal)) portal.Map?.RemoveObject(portal);
            if (world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => p.Character.Id == owner) is { } online
                && online.Character.SafePortal?.Binding?.Id == b.Id)
                Clear(online, "it ran out");
        }
    }
}
