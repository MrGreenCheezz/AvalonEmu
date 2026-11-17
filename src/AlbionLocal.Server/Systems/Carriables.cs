using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Carried objects (analysis/work/stageR/smugglers/SPEC.md §4, "SPEC" here): what a player carries, as the carriables.xml type and rarity, and
/// its carry buff. The client knows the carried object by that buff alone (carriables.xml: "carrybuff needs to be unique, because a specific
/// crystal is also identified by it when coming from the carrybuff"), so carrying is: the buff on (all its effects Active, until removed),
/// and this record. The mechanics are the type's and work for every carriable type (hideout power cores, faction battle standards, …);
/// only the smugglers' crate has a source and a destination on this server (<see cref="Smugglers"/>).
/// <list type="bullet">
/// <item>Losing (SPEC §4.3): the carrier dies (the death clears its spells, the buff with them: persistsafterknockdown keeps it while knocked
/// down), leaves the map (a cluster change, a logout) [assume: no carriable crosses a cluster on this server] or the buff is gone otherwise:
/// the object drops where the carrier last stood (<see cref="CarriableDrop"/>, NewCarriableObject 486) and lies for the type's
/// droppeddecaytime (carriabledroppeddefaultdecaytime 1800 s) [infer].</item>
/// <item>The ground pickup (op 439, SPEC §4.4): PickupCarriableObjectStart (489) {0 the player, 1 start, 2 end, 3 the object} to the player
/// and its observers, PickupCarriableObjectFinished (491) {0, 1 the time, 2 the object} after the type's pickupduration (0.5 s); op 440 or a
/// knockdown cancels it (490 {0}). The client's channel class cyv/csn (csn.u 0x180BF1D70: action state 0x26; csn.v 0x180BF20F0).</item>
/// <item>Mounting is refused while carrying (localization CARRIABLE_PICKUP_QUESTION_SMUGGLERS_CRATE: "You cannot mount up while
/// transporting"); a pickup dismounts first (<see cref="Mounts.BeforeAction"/>).</item>
/// </list>
/// Under <see cref="World.Sync"/>.
/// </summary>
public sealed class Carriables(World world)
{
    /// <summary>A carried object: its type and rarity, the map it was taken on and where the carrier last stood there.</summary>
    public sealed class Carried(CarriableType type, byte rarity, Map map, Vector2 at)
    {
        public CarriableType Type { get; } = type;
        public byte Rarity { get; } = rarity;
        public CarriableRarity Info => Type.Rarities.GetValueOrDefault(Rarity) ?? Type.Rarities.Values.First();
        public Map Map { get; } = map;
        public Vector2 LastAt { get; internal set; } = at;
    }

    /// <summary>A ground pickup under way.</summary>
    private sealed record Pickup(CarriableDrop Drop, Map Map, TimeSpan Due);

    /// <summary>The RC of a refused carriable request (the client's channel class resets its action state on any non-zero code, cyv.ai7 0x180A060D0).</summary>
    public const short Refused = 1;

    private readonly Dictionary<Player, Carried> _carried = [];
    private readonly Dictionary<Player, Pickup> _pickups = [];

    /// <summary>What <paramref name="p"/> carries; null = nothing.</summary>
    public Carried? Of(Player p) => _carried.GetValueOrDefault(p);

    /// <summary>The carriers now.</summary>
    public IReadOnlyDictionary<Player, Carried> All => _carried;

    /// <summary>
    /// <paramref name="p"/> takes <paramref name="type"/> of <paramref name="rarity"/>: its carry buff goes on (all its effects Active, without a
    /// duration: until removed) and the list goes out. False when it carries already or the buff is not in spells.xml.
    /// </summary>
    public bool Take(Player p, CarriableType type, byte rarity)
    {
        if (_carried.ContainsKey(p) || p.Map is not { } map) return false;
        var info = type.Rarities.GetValueOrDefault(rarity);
        if (info is null || world.Spells?.Template(info.CarryBuff) is not { } spell) return false;
        var now = world.GameNow;
        var planned = spell.Effects.Select((node, i) => (SubEffect?)new SubEffect(node, i) { Active = true }).ToArray();
        SpellEffects.Apply(world, p, spell, p.Id, 100, 100, now, planned);
        SpellEffects.Send(world, p);
        SpellEffects.Refresh(world, p);
        _carried[p] = new Carried(type, rarity, map, p.PositionAt(DateTime.UtcNow));
        Log.Info("world", $"'{p.Character.Name}' carries {type.Name} rarity {rarity} ({info.CarryBuff})");
        return true;
    }

    /// <summary>The carried object of <paramref name="p"/> is gone (delivered, dropped): its carry buff off and the list sent. Returns what it carried.</summary>
    public Carried? Release(Player p)
    {
        if (!_carried.Remove(p, out var c)) return null;
        var buff = c.Info.CarryBuff;
        if (SpellEffects.Remove(world, p, s => s.Spell.Name == buff) && p.Map is not null)
        {
            SpellEffects.Send(world, p);
            SpellEffects.Refresh(world, p);
        }
        return c;
    }

    /// <summary>Whether <paramref name="p"/>'s carry buff is still on it.</summary>
    private bool BuffOn(Player p, Carried c) => p.Effects.Spells.Any(s => s.Spell.Name == c.Info.CarryBuff && s.Live(world.GameNow));

    /// <summary>
    /// After a Join: a carry buff that came back with the saved spells (a relog) but carries nothing here any more goes off (the object was dropped
    /// when the carrier left).
    /// </summary>
    public void Joined(Player p)
    {
        if (_carried.ContainsKey(p) || world.Smugglers.Data is not { } data || p.Effects.Count == 0) return;
        var buffs = data.Carriables.SelectMany(t => t.Rarities.Values.Select(r => r.CarryBuff)).Where(b => b.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (SpellEffects.Remove(world, p, s => buffs.Contains(s.Spell.Name)))
        {
            SpellEffects.Send(world, p);
            SpellEffects.Refresh(world, p);
            Log.Info("world", $"'{p.Character.Name}': a carry buff without a carried object taken off");
        }
    }

    /// <summary>
    /// Every tick: each carrier's last position; a carrier gone from its map (a cluster change, a logout) or whose carry buff is gone (a death) loses
    /// the object where it last stood; the ground pickups due end (<see cref="FinishPickup"/>), those of a knocked-down or gone player are cancelled.
    /// </summary>
    public void Update()
    {
        if (_carried.Count > 0)
            foreach (var (p, c) in _carried.ToList())
            {
                if (p.Map == c.Map && BuffOn(p, c))
                {
                    c.LastAt = p.PositionAt(DateTime.UtcNow);
                    continue;
                }
                Lose(p, p.Map != c.Map ? "left the map" : "its carry buff is gone (a death)");
            }
        if (_pickups.Count == 0) return;
        foreach (var (p, pick) in _pickups.ToList())
        {
            if (p.Map != pick.Map || p.Combat.Down is not null) CancelPickup(p, p.Map != pick.Map ? "left the map" : "knocked down");
            else if (world.Now >= pick.Due) FinishPickup(p, pick);
        }
    }

    /// <summary><paramref name="p"/> loses what it carries: off its back, dropped where it last stood on the map it was taken on (unless that map is closed).</summary>
    public CarriableDrop? Lose(Player p, string why)
    {
        if (Release(p) is not { } c) return null;
        Log.Info("world", $"'{p.Character.Name}' lost {c.Type.Name} rarity {c.Rarity}: {why}");
        return c.Map.Unloaded ? null : Drop(c.Map, c.Type, c.Rarity, c.LastAt);
    }

    /// <summary>A <paramref name="type"/> of <paramref name="rarity"/> on the ground of <paramref name="map"/> at <paramref name="at"/>, gone after the type's decay time.</summary>
    public CarriableDrop Drop(Map map, CarriableType type, byte rarity, Vector2 at)
    {
        var drop = new CarriableDrop(world.ObjectIds.Next(), SmugglerSpawn.At(map.Cluster, map.CustomSpawns++, type.Rarities.GetValueOrDefault(rarity)?.Tile ?? "", at, 0, "carriable"),
            type, rarity, world.GameNow);
        map.AddObject(drop);
        var seconds = type.DroppedDecaySeconds > 0 ? type.DroppedDecaySeconds : world.Smugglers.Data?.DroppedDecaySeconds ?? 1800;
        map.Timers.At(world.Now + TimeSpan.FromSeconds(seconds), () =>
        {
            if (drop.Map != map || !map.Objects.ContainsKey(drop.Id)) return;
            if (drop.TakenBy is { } taker) CancelPickup(taker, "the object decayed");
            map.RemoveObject(drop);
            Log.Info("world", $"{drop} decayed");
        });
        Log.Info("world", $"{drop} dropped on {map.Cluster}");
        return drop;
    }

    /// <summary>
    /// PickupCarriableObjectStart (op 439 {0 time, 1 the object}): a carriable on the ground of the player's map within reach (the type's
    /// pickup range is the client's [infer]: 5 m + the tolerance), nobody taking it, the player carrying nothing, not knocked down and no
    /// pickup of its own under way; then 489 to it and its observers and the pickup after pickupduration. Returns why it was refused, or null.
    /// </summary>
    public string? StartPickup(Player p, long objectId)
    {
        if (p.Map is not { } map || !map.Objects.TryGetValue(objectId, out var o) || o is not CarriableDrop drop) return $"no carriable #{objectId} here";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (_carried.ContainsKey(p)) return "carries already";
        if (_pickups.ContainsKey(p) || world.Smugglers.Busy(p)) return "a pickup or delivery is under way";
        if (drop.TakenBy is not null) return "someone else is taking it";
        var at = p.PositionAt(DateTime.UtcNow);
        var reach = (world.Smugglers.Data?.DeliverRange ?? 5) + SpellCasts.RangeTolerance + 1;
        if (Vector2.Distance(at, drop.Position) > reach) return string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, drop.Position):0.#} m away (reach {reach:0.#})");
        Mounts.BeforeAction(p, "picks up a carriable");
        var seconds = Math.Max(0.1, drop.Type.PickupSeconds);
        var now = world.GameNow;
        drop.TakenBy = p;
        _pickups[p] = new Pickup(drop, map, world.Now + TimeSpan.FromSeconds(seconds));
        p.SendAround(new PickupCarriableObjectStartEvent { ddn = p.Id, dl2 = now, dl3 = now + SpellCasts.Seconds(seconds), dl4 = drop.Id });
        return null;
    }

    /// <summary>PickupCarriableObjectCancel (op 440) or a break: the pickup under way ends, 490 {0 the player} to it and its observers.</summary>
    public void CancelPickup(Player p, string why)
    {
        if (!_pickups.Remove(p, out var pick)) return;
        if (pick.Drop.TakenBy == p) pick.Drop.TakenBy = null;
        p.SendAround(new PickupCarriableObjectCancelEvent { ddn = p.Id });
        Log.Info("world", $"'{p.Character.Name}' pickup of {pick.Drop} cancelled: {why}");
    }

    private void FinishPickup(Player p, Pickup pick)
    {
        _pickups.Remove(p);
        var drop = pick.Drop;
        drop.TakenBy = null;
        if (drop.Map != pick.Map || !pick.Map.Objects.ContainsKey(drop.Id) || !Take(p, drop.Type, drop.Rarity))
        {
            p.SendAround(new PickupCarriableObjectCancelEvent { ddn = p.Id });
            return;
        }
        p.SendAround(new PickupCarriableObjectFinishedEvent { ddn = p.Id, dl0 = world.GameNow, dl1 = drop.Id });
        pick.Map.RemoveObject(drop);
    }

    /// <summary>Whether a ground pickup of <paramref name="p"/> is under way.</summary>
    public bool PickingUp(Player p) => _pickups.ContainsKey(p);
}
