using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Hideouts phase H2 (SPEC §7 H2): the interior, the instance "@HIDEOUT@&lt;guid&gt;" built from the world.xml cluster of the hideout's level
/// (HIDEOUT-0001a/b/c: bank, repair, travel point, at b/c the market and more), with its management (420/425) and exit (421); the way in
/// through the surface object and out through the exit (ExitEnterStart 237 and hideouts.xml enterchanneltime, 4 s; a ChangeCluster on either
/// object goes at once), the access by the preset (accessrights.xml: closed = the managers, private = the guild, public = everyone), a login
/// inside, and a hideout that is gone.
/// </summary>
public sealed partial class Hideouts
{
    /// <summary>How far from the surface object or the exit the way in or out may start (m; the hideout's model is large, the client checks its own click).</summary>
    public const float EntryReach = 25;

    /// <summary>How far a player may move during the entry channel before it breaks (m).</summary>
    public const float ChannelSlack = 1.5f;

    /// <summary>A running entry or exit channel: the object, its end on the world clock, where and at which health it began.</summary>
    private sealed record Channel(WorldObject Target, TimeSpan Due, Vector2 From, float Health);

    private readonly Dictionary<Player, Channel> _channels = [];

    /// <summary>
    /// The world.xml cluster the instance <paramref name="cluster"/> is built from (World.TemplateOf, ChangeCluster key 1, SelectCharacter key 2):
    /// the cluster of the built hideout's level; null for no live hideout (unknown, hideouts off, still under construction).
    /// </summary>
    public string? WorldOf(string cluster) =>
        Enabled && Find(cluster) is { Built: true } h && Data.Level(h.Level) is { } level ? level.Cluster : null;

    /// <summary>The management and exit of a hideout's instance map (from the template's hideoutmanagement tile and hideoutexit member).</summary>
    private IEnumerable<WorldObject> Interior(Map map, ClusterTemplate template)
    {
        if (Find(map.Cluster) is not { } h) yield break;
        foreach (var m in template.HideoutManagements)
            yield return new HideoutManagement(world.ObjectIds.Next(), HideoutObjectSpawn.At(map.Cluster, "management", _spawns++, m.Tile, m.Position, m.Direction), h, Info,
                () => world.GameNow);
        foreach (var e in template.Critical.Where(c => c.Type == "hideoutexit"))
            yield return new HideoutExitObject(world.ObjectIds.Next(), HideoutObjectSpawn.At(map.Cluster, "exit", _spawns++, e.Tile, e.Position, e.Direction), h);
    }

    /// <summary>
    /// Why <paramref name="character"/> may not enter <paramref name="h"/> (null = it may): built and not being upgraded
    /// (@HIDEOUT_ACCESS_CURRENTLY_UPGRADING), VISIT by the preset: public everyone, private the owner guild's members, closed only its master and
    /// the members with Manage Hideouts. The alliance has no own right in the presets (accessrights.xml): an allied guild enters a public hideout.
    /// </summary>
    public string? CanVisit(Hideout h, Guid character, bool upgradeBlocks = true)
    {
        if (!Enabled) return "hideouts are off";
        if (!h.Built) return "the hideout is still under construction";
        if (upgradeBlocks && h.Upgrading) return "the hideout is being upgraded (HIDEOUT_ACCESS_CURRENTLY_UPGRADING)";
        if (h.Access == HideoutAccess.Public) return null;
        var guild = world.Guilds.Of(character);
        if (guild?.Id != h.Guild) return "only the owner guild may enter this hideout (private)";
        if (h.Access == HideoutAccess.Closed && !Manages(guild, character)) return "the hideout is closed to all but its managers";
        return null;
    }

    /// <summary>
    /// ExitEnterStart (237) on <paramref name="target"/>, a hideout's surface object or its exit: the checks (not knocked down, within
    /// <see cref="EntryReach"/>, on the way in the access), then the channel of enterchanneltime with ExitEnterStart (253) to the player and those in
    /// view, the target cluster resolved meanwhile. Null = started, else why not.
    /// </summary>
    public string? StartChannel(Player p, WorldObject target)
    {
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (_channels.ContainsKey(p)) return "already entering";
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, target.Position) > EntryReach) return "too far from it";
        string next;
        switch (target)
        {
            case HideoutObject o:
                if (CanVisit(o.Hideout, p.Character.Id) is { } why) return why;
                next = o.Hideout.Instance;
                break;
            case HideoutExitObject e:
                next = e.Hideout.Cluster;
                break;
            default:
                return "no hideout object";
        }
        world.PrefetchCluster(next);
        var now = world.GameNow;
        var length = TimeSpan.FromSeconds(Data.EnterChannelSeconds);
        _channels[p] = new Channel(target, world.Now + length, at, p.Health.At(now));
        p.SendToObservers(new ExitEnterStartEvent { ddn = p.Id, b5w = now, b5x = now + length.Ticks, b5y = target.Id });
        Log.Info("world", $"'{p.Character.Name}' channels at {target} ({Data.EnterChannelSeconds:0.#} s)");
        return null;
    }

    /// <summary>ExitEnterCancel (238), a move, damage or a knockdown: the channel ends with 254. False when none ran.</summary>
    public bool CancelChannel(Player p, string why)
    {
        if (!_channels.Remove(p)) return false;
        p.SendAround(new ExitEnterCancelEvent { ddn = p.Id });
        Log.Info("world", $"'{p.Character.Name}' hideout channel cancelled ({why})");
        return true;
    }

    /// <summary>Every tick (<see cref="World.Update"/>): the channels; one done sends 255 ExitEnterFinished and takes the player in or out.</summary>
    public void UpdateChannels()
    {
        if (_channels.Count == 0) return;
        foreach (var (p, c) in _channels.ToList())
        {
            if (p.Map is null || p.Map != c.Target.Map || p.Session.Player != p) _channels.Remove(p);
            else if (!p.Map.Objects.ContainsKey(c.Target.Id)) CancelChannel(p, "the object is gone");
            else if (p.Combat.Down is not null) CancelChannel(p, "knocked down");
            else if (p.Health.At(world.GameNow) < c.Health - 0.5f) CancelChannel(p, "damaged");
            else if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), c.From) > ChannelSlack) CancelChannel(p, "moved");
            else if (world.Now >= c.Due)
            {
                _channels.Remove(p);
                p.SendAround(new ExitEnterFinishedEvent { ddn = p.Id, b5v = c.Target.Id });
                if (Go(p, c.Target) is { } why)
                {
                    p.Session.Send(new SystemMessageEvent { d0p = $"The hideout cannot be entered now: {why}." });
                    Log.Info("world", $"'{p.Character.Name}' at {c.Target}: {why}");
                }
            }
        }
    }

    /// <summary>Through <paramref name="target"/> now (the channel's end, or a ChangeCluster on it): in or out; null = on the way, else why not.</summary>
    public string? Go(Player p, WorldObject target) => target switch
    {
        HideoutObject o => CanVisit(o.Hideout, p.Character.Id) ?? Enter(p, o.Hideout),
        HideoutExitObject e => Leave(p, e.Hideout, "exit"),
        _ => "no hideout object",
    };

    /// <summary>
    /// Into <paramref name="h"/>'s instance: at the exit's spawn point inside (the travel point's without one), the push {0 "@HIDEOUT@…", 1 the
    /// level's world.xml cluster, 2 the guild's name}. A template not resolved yet is resolved first (the move follows). Null = on the way.
    /// </summary>
    public string? Enter(Player p, Hideout h, string via = "entrance")
    {
        if (WorldOf(h.Instance) is not { } template) return "the hideout has no interior now";
        if (world.CachedCluster(h.Instance) is not { } resolved)
        {
            if (world.ResolveCluster(h.Instance) is not { } load) return "this server has no cluster data";
            var map = p.Map;
            load.ContinueWith(t => world.Post(() =>
            {
                if (t.IsCompletedSuccessfully && p.Map == map && p.Session.Player == p && p.Session.Status == Network.SessionStatus.InWorld) Enter(p, h, via);
            }), TaskScheduler.Default);
            return null;
        }
        var at = Inside(resolved);
        Log.Info("world", $"'{p.Character.Name}' enters {h} ({via})");
        p.Session.Teleport(new Arrival(h.Instance, at, p.Facing), $"hideout:{h.Id}:{via}", template, Info(h).GuildName);
        return null;
    }

    /// <summary>Out of <paramref name="h"/> to its zone in front of the hideout (<see cref="Outside"/>). Null = on the way.</summary>
    public string? Leave(Player p, Hideout h, string via)
    {
        Log.Info("world", $"'{p.Character.Name}' leaves {h} ({via})");
        p.Session.Teleport(Outside(h, p.Facing), $"hideout:{h.Id}:{via}");
        return null;
    }

    /// <summary>The arrival inside a hideout's template: the hideoutexit's spawnpos (turned with its tile), else the travel point's spawn, else the origin.</summary>
    public static Vector2 Inside(ClusterTemplate t)
    {
        if (t.Critical.FirstOrDefault(c => c.Type == "hideoutexit") is { } exit)
        {
            if (TileMath.V2((string?)exit.Component.Attribute("spawnpos")) is { } sp)
            {
                var (rx, rz) = TileMath.Rotate(sp.X, sp.Y, exit.Direction);
                return exit.Position + new Vector2((float)rx, (float)rz);
            }
            return exit.Position;
        }
        return t.Travelpoints.FirstOrDefault()?.SpawnPosition ?? Vector2.Zero;
    }

    /// <summary>In front of hideout <paramref name="h"/> in its zone: 12 m from its centre, turned with it [choice: the model's door is not in the data].</summary>
    public static Arrival Outside(Hideout h, float facing)
    {
        var (dx, dz) = TileMath.Rotate(0, -12, h.Rotation);
        return new Arrival(h.Cluster, h.Position + new Vector2((float)dx, (float)dz), facing);
    }

    /// <summary>
    /// Every player inside <paramref name="h"/> goes out in front of it (a system line first, when given), then its instance map is unloaded,
    /// so the next entry builds the template of its level again.
    /// </summary>
    public void Evict(Hideout h, string why, string? message = null)
    {
        if (world.Maps.Find(h.Instance) is not { } map) return;
        foreach (var p in map.Players.Values.ToList())
        {
            if (message is not null) p.Session.Send(new SystemMessageEvent { d0p = message });
            _channels.Remove(p);
            if (p.Session.Status == Network.SessionStatus.InWorld && p.Session.Player == p) p.Session.Teleport(Outside(h, p.Facing), $"hideout:{h.Id}:{why}");
            else world.Maps.Remove(p, world.Clock.Now);
        }
        world.Maps.Unload(map, force: true);
    }

    /// <summary>
    /// SelectCharacter: a character saved in a hideout that is gone (unknown, hideouts off, no interior now) goes out in front of it (its zone),
    /// or without the hideout's row to its last city; saved. True when it was moved.
    /// </summary>
    public bool ReturnIfGone(Character c)
    {
        if (!HideoutData.IsInstance(c.Cluster) || WorldOf(c.Cluster) is not null) return false;
        var to = Find(c.Cluster) is { } h ? Outside(h, c.Direction) : world.Islands.HomeOf(null, c); // no row: the last city's travel point
        (c.Cluster, c.PositionX, c.PositionY) = (to.Cluster, to.Position.X, to.Position.Y);
        c.PendingTransfer = null;
        world.Saves.SaveNow(c);
        Log.Info("world", $"'{c.Name}' was in a hideout that is gone: out to {to.Cluster}");
        return true;
    }

    /// <summary>HideoutGetOwnerInfo (399) {0 the hideout Guid}: its owner guild's name ("" for an unknown hideout).</summary>
    public string OwnerName(Guid hideout) => Find(hideout) is { } h ? Info(h).GuildName : "";
}
