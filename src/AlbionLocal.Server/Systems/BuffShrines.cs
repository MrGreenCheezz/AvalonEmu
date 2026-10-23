using System.Globalization;
using AlbionLocal.Photon;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The buff shrines (analysis/work/stageN/shrines/SPEC.md, "SPEC" here): every &lt;shrine shrinetype&gt; tile of a map's active layers whose
/// buffshrines.xml definition is a plain buff shrine (<see cref="BuffShrineData.Definition.Supported"/>) stands as a <see cref="BuffShrine"/>
/// (NewShrine 396). The use (SPEC §4): the client's click on a Ready shrine sends UseShrine (op 254 {1 id, 2 state}); answered rc 0, it casts the
/// definition's usespell (OPEN_BUFFSHRINE, useobject shrine) in slot 25 at the shrine (<see cref="StartUse"/>, a <see cref="FakeSlotCast"/>);
/// at the cast's end the effectspell runs on the player (BUFFSHRINE_RANDOM_BUFFS: one of three 60 s buffs), a charge goes and a spent shrine
/// turns state 2 (UpdateShrine 397) until its respawn time (respawns="false": for the map's life). A shrine with an alertradius (the faction
/// shrines) is Locked (state 0) while a live mob of its faction stands within it. Under <see cref="World.Sync"/>.
/// </summary>
public sealed class BuffShrines(World world)
{
    /// <summary>The client's answer code that shows the shrine's state message (cz6.a.d: 0x27B5).</summary>
    public const short NotUsable = 10165;

    /// <summary>How long an accepted UseShrine waits for its slot-25 cast.</summary>
    private static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(5);

    private readonly List<BuffShrine> _shrines = [];
    private readonly Dictionary<Player, (BuffShrine Shrine, TimeSpan Until)> _pending = [];
    private TimeSpan _next;

    /// <summary>The shrines of <paramref name="map"/>.</summary>
    public IEnumerable<BuffShrine> Of(Map map) => map.Objects.Values.OfType<BuffShrine>();

    /// <summary>The shrines of <paramref name="template"/> for <paramref name="map"/>: Ready, or Locked when they have an alert radius (the next tick decides).</summary>
    public IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        foreach (var s in template.BuffShrines)
        {
            var shrine = new BuffShrine(world.ObjectIds.Next(), s, s.Definition!, world.GameNow);
            if (Alerting(shrine.Definition)) shrine.State = BuffShrine.Locked;
            _shrines.Add(shrine);
            yield return shrine;
        }
    }

    /// <summary>A shrine of <paramref name="spawn"/> put on <paramref name="map"/> after it was filled (a GM or the self-test), shown to its players.</summary>
    public BuffShrine Place(Map map, BuffShrineSpawn spawn)
    {
        var shrine = new BuffShrine(world.ObjectIds.Next(), spawn, spawn.Definition ?? throw new ArgumentException("a shrine without its definition"), world.GameNow);
        if (Alerting(shrine.Definition)) shrine.State = BuffShrine.Locked;
        map.AddObject(shrine);
        _shrines.Add(shrine);
        return shrine;
    }

    /// <summary>A GM's .shrine reset: <paramref name="s"/> Ready again with its charges full (UpdateShrine 397 to the players who see it).</summary>
    public void Reset(BuffShrine s)
    {
        s.Charges = s.Definition.MaxCharges;
        s.ReadyAt = null;
        Set(s, BuffShrine.Ready);
    }

    private static bool Alerting(BuffShrineData.Definition d) => d.AlertRadius > 0 && d.Faction.Length > 0 && !d.Faction.Equals("NONE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Once a second: the shrines of closed maps dropped, the spent ones ready again at their time, the faction shrines locked or unlocked.</summary>
    public void Update()
    {
        if (_shrines.Count == 0 || world.Now < _next) return;
        _next = world.Now + TimeSpan.FromSeconds(1);
        _shrines.RemoveAll(s => s.Map is not { } m || world.Maps.Find(m.InstanceKey) != m || !m.Objects.ContainsKey(s.Id));
        foreach (var p in _pending.Where(x => x.Value.Until < world.Now).Select(x => x.Key).ToList()) _pending.Remove(p);
        var ticks = world.GameNow;
        foreach (var s in _shrines)
        {
            if (s.State == BuffShrine.Spent)
            {
                if (s.ReadyAt is { } at && world.Now >= at)
                {
                    s.Charges = s.Definition.MaxCharges;
                    s.ReadyAt = null;
                    Set(s, Alerting(s.Definition) && Guarded(s, ticks) ? BuffShrine.Locked : BuffShrine.Ready);
                }
                continue;
            }
            if (!Alerting(s.Definition)) continue;
            var guarded = Guarded(s, ticks);
            if (guarded && s.State == BuffShrine.Ready) Set(s, BuffShrine.Locked);
            else if (!guarded && s.State == BuffShrine.Locked) Set(s, BuffShrine.Ready);
        }
    }

    /// <summary>Whether a live mob of the shrine's faction stands within its alert radius.</summary>
    private static bool Guarded(BuffShrine s, long ticks) =>
        s.Map!.Objects.Values.OfType<Mob>().Any(m => !m.Dead && m.Template.Faction.Equals(s.Definition.Faction, StringComparison.OrdinalIgnoreCase)
            && Vector2.Distance(SpellMovement.PositionOf(m, ticks), s.Position) <= s.Definition.AlertRadius);

    private void Set(BuffShrine s, byte state, long? ticks = null)
    {
        s.State = state;
        s.StateTicks = ticks ?? world.GameNow;
        s.Map?.SendToObservers(s, s.BuildUpdate());
        Log.Debug("world", $"{s} is now state {state}");
    }

    /// <summary>How far from a shrine's centre its user may stand: the usespell's castrange (else 5) + the tolerance and the shrine's 1 m half size.</summary>
    private double Reach(BuffShrine s) => (world.Spells?.Template(s.Definition.UseSpell)?.CastRange is > 0 and var r ? r : 5) + SpellCasts.RangeTolerance + 1;

    /// <summary>
    /// UseShrine (op 254 {1 id, 2 the state the client shows}): a Ready shrine on the player's map within reach is accepted (rc 0; the client
    /// then casts the usespell, <see cref="StartUse"/>); a Locked or Spent one gets <see cref="NotUsable"/> (the client's state message), anything
    /// else 1. Returns the code and why it was refused.
    /// </summary>
    public (short Code, string? Why) Use(Player p, long id)
    {
        if (p.Map is not { } map || !map.Objects.TryGetValue(id, out var o) || o is not BuffShrine s) return (1, $"no shrine #{id} here");
        if (p.Combat.Down is { } down) return (1, $"knocked down ({down.Kind})");
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, s.Position) > Reach(s))
            return (1, string.Create(CultureInfo.InvariantCulture, $"{s} is {Vector2.Distance(at, s.Position):0.#} m away (reach {Reach(s):0.#} m)"));
        if (s.State != BuffShrine.Ready) return (NotUsable, $"{s} is not ready");
        _pending[p] = (s, world.Now + PendingFor);
        return (0, null);
    }

    /// <summary>A shrine use under way: the effect at the cast's end (<see cref="Apply"/>).</summary>
    private sealed record ShrineCast(Map Map, Vector2 Aim, int SpellId, byte Counter, long Start, long End, TimeSpan Due, BuffShrine Shrine)
        : FakeSlotCast(Map, Aim, SpellId, Counter, Start, End, Due)
    {
        public override string What => "shrine use";

        public override Vector2 HitAt => Shrine.Position;

        public override string? Broken(Player p) => Shrine.Map != Map || !Map.Objects.ContainsKey(Shrine.Id) ? $"{Shrine} is gone"
            : Shrine.State != BuffShrine.Ready ? $"{Shrine} is no longer ready" : null;

        public override void Complete(Player p)
        {
            if (p.World.Shrines.Apply(p, Shrine) is { } why) Log.Info("world", $"'{p.Character.Name}' shrine use of {Shrine} failed at its end: {why}");
        }
    }

    /// <summary>
    /// CastStart on slot 25 with a <c>useobject type="shrine"</c> spell (OPEN_BUFFSHRINE, castingtime 0): the shrine the player's accepted
    /// UseShrine named, else the Ready shrine nearest the aim within reach of it; the spell the shrine's usespell; then a server cast of its
    /// castingtime (an instant one ends in this tick). Returns why it was refused, or null.
    /// </summary>
    public string? StartUse(Player p, int spellId, Vector2 aim, byte counter, long start)
    {
        if (p.Map is not { } map) return "not on a map";
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        if (p.PvpState.Execute is not null || p.PvpState.FlagCast is not null || p.Combat.Cast is not null) return "a cast is under way";
        var shrine = _pending.Remove(p, out var pending) && pending.Shrine.Map == map ? pending.Shrine
            : Of(map).Where(s => s.State == BuffShrine.Ready).MinBy(s => Vector2.Distance(s.Position, aim));
        if (shrine is null || Vector2.Distance(shrine.Position, aim) > Reach(shrine)) return "no ready shrine at the aim";
        if (shrine.State != BuffShrine.Ready) return $"{shrine} is not ready";
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, shrine.Position) > Reach(shrine))
            return string.Create(CultureInfo.InvariantCulture, $"{shrine} is {Vector2.Distance(at, shrine.Position):0.#} m away");
        var spell = world.Spells?.Template(spellId);
        if (spell is not null && !spell.Name.Equals(shrine.Definition.UseSpell, StringComparison.OrdinalIgnoreCase))
            return $"{shrine} is used with {shrine.Definition.UseSpell}, not {spell.Name}";
        var seconds = spell?.CastingTime ?? 0;
        world.FakeCasts.Start(p, new ShrineCast(map, aim, spellId, counter, start, start + SpellCasts.Seconds(seconds), world.Now + TimeSpan.FromSeconds(seconds), shrine));
        Log.Info("world", $"'{p.Character.Name}' casts {spell?.Name ?? spellId.ToString(CultureInfo.InvariantCulture)} on {shrine}");
        if (seconds <= 0) world.FakeCasts.Update();
        return null;
    }

    /// <summary>
    /// The use's effect (SPEC §4 step 4): the effectspell on <paramref name="p"/> as its own cast (ability power 100), a charge off; out of
    /// charges (and not InfiniteCapacity) the shrine is Spent with key 6 the time it is ready again. Returns why nothing happened, or null.
    /// </summary>
    public string? Apply(Player p, BuffShrine s)
    {
        if (p.Map is not { } map || s.Map != map) return "not on the shrine's map";
        if (s.State != BuffShrine.Ready) return $"{s} is not ready";
        if (world.Spells?.Template(s.Definition.EffectSpell) is not { } spell) return $"no spell {s.Definition.EffectSpell}";
        var now = world.GameNow;
        SpellCasts.Execute(new SpellCasts.SpellContext(p, map, spell, 0, 100, null, SpellMovement.PositionOf(p, now), now), spell);
        Log.Info("world", $"'{p.Character.Name}' used {s}: {spell.Name}");
        if (s.Definition.Mode == BuffShrineData.ShrineMode.InfiniteCapacity || --s.Charges > 0) return null;
        var d = s.Definition;
        var seconds = d.RespawnMax > d.RespawnMin ? d.RespawnMin + map.Random.NextDouble() * (d.RespawnMax - d.RespawnMin) : d.RespawnMin;
        s.ReadyAt = d.Respawns ? world.Now + TimeSpan.FromSeconds(seconds) : null;
        Set(s, BuffShrine.Spent, now + SpellCasts.Seconds(seconds));
        return null;
    }
}
