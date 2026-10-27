using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The area of a spelleffectarea node (stageD effects SPEC §4; ARCHITECTURE.md §4.1 DynamicArea): a world object in the
/// visibility grid, created by the cast with NewSpellEffectArea (113, au1) and left with the common Leave at its end. It has the
/// node's shapes placed at its origin and turned by its direction (<see cref="SpellAreas.Inside"/>), and applies the node's
/// effect spell to the targets that enter it (<see cref="SpellAreas"/>). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class SpellArea(long id, ClusterObject spawn) : WorldObject(id, spawn)
{
    public required Player Owner { get; init; }

    /// <summary>Key 4: the spell that holds the node (FIREWALL 3234, not FIREWALL_EFFECT).</summary>
    public required SpellTemplate Holder { get; init; }

    /// <summary>Key 5: the node's client effect index in <see cref="Holder"/> (THROWINGBLADES 1, 2, 3).</summary>
    public required byte Index { get; init; }

    public required XElement Node { get; init; }

    /// <summary>The effect spell (effect="…"); null for effect="NONE", a visual only (SPLITTINGSLASH's first area).</summary>
    public SpellTemplate? Effect { get; init; }

    /// <summary>
    /// Key 2 and where the node shapes are placed from: the caster (position="atcaster") or the target point. Not the origin + the
    /// node offset as SPEC §4 says: the client adds the offset itself (own FIREWALL casts: key 2 = the caster 5 of 5 times, the aim
    /// 3 m further at castrange 3), and the fears run away from it.
    /// </summary>
    public required Vector2 Origin { get; init; }

    /// <summary>The node shapes, read once (<see cref="SpellAreas.Inside"/>), and how far from the origin they reach at most.</summary>
    public required IReadOnlyList<SpellAreas.ShapeNode> Shapes { get; init; }

    public required float Reach { get; init; }

    /// <summary>Key 3: the direction in degrees (castdirection: caster → aim); left out at 0.</summary>
    public required float Yaw { get; init; }

    /// <summary>Keys 6 / 7: the start (the hit) and the end (start + time; = start for time 0, removed by its owner only).</summary>
    public required long Start { get; init; }

    public required long End { get; init; }

    /// <summary>Key 8: the root cast's counter.</summary>
    public required byte Counter { get; init; }

    /// <summary>The cast's ability power and crowd control power: the effect spell's entries carry them.</summary>
    public required double Ap { get; init; }

    public required double CcAp { get; init; }

    /// <summary>The channel the area is linked to (linktochanneling): it ends with it; null = none.</summary>
    public SpellCasts.ChannelState? Channel { get; init; }

    /// <summary>The units inside at the last tick, and those it has applied its effect to (applyeffectonlyoncepertarget).</summary>
    internal HashSet<long> InsideLast { get; } = [];

    internal HashSet<long> Applied { get; } = [];

    /// <summary>Targets hit so far (maxeffectareatargets).</summary>
    internal int Hits { get; set; }

    /// <summary>Taken off its map (its end, deletewhenmaxtargetshit, its channel, its owner's knockdown).</summary>
    public bool Gone { get; internal set; }

    public override Vector2 Position => Origin;

    public override float Direction => Yaw;

    public override string Kind => "spell area";

    public override string TypeName => $"{Holder.Name}[{Index}]";

    public override bool LeaveOnRange => true;

    /// <summary>
    /// NewSpellEffectArea (113): 0 id, 1 owner, 2 centre, 3 direction (left out at 0), 4 the holding spell, 5 its client index,
    /// 6 start, 7 end, 8 the root cast's counter, 9 byte 0 (sent, as all cast-made areas; key 10 never). 11-13 never.
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        yield return new NewSpellEffectAreaEvent
        {
            da6 = Id, da7 = Owner.Id, da8 = Origin, da9 = Yaw, dba = Holder.Id, dbb = Index, dbc = Start, dbd = End, dbe = Counter,
            Extra = new() { [9] = (byte)0 },
        };
    }
}

/// <summary>
/// spelleffectarea (stageD effects SPEC §4, batch 1 part B): <see cref="Create"/> puts the area on the map in the cast's hit
/// (NewSpellEffectArea right after CastSpell, to everyone who sees it, the caster included) and runs its first tick at once;
/// every world tick after, a valid target (the area's class, stageJ pvp-spells SPEC §2.6: enemy the attackable mobs, friendall the owner and
/// its Friends, all and ground everyone but players protected from the owner, self the owner) that is inside and was not inside at the
/// last tick gets the effect spell applied (when its own class takes the unit's kind and one of its nodes acts on it, <see cref="SpellCasts.Reaches"/>)
/// (counter 0, the cast's Ap; at most once per target with
/// applyeffectonlyoncepertarget), its hit batch ForcedMovement → ActiveSpellEffectsUpdate → CastHits {0 target, 1 [owner…],
/// 2 [effect, the spells it applies…], 3 [1…], 4 [0…]} → HealthUpdate, or CastHit {3 2} when a spell immunity blocks it
/// (FIREWALL re-entries within 3 s). removeonleave takes the effect spell off a target that leaves. The area leaves at its end,
/// in the tick its maxeffectareatargets-th target is hit with deletewhenmaxtargetshit (SEARING_FLAME), with its channel
/// (linktochanneling) and at its owner's knockdown (removeoncasterdeath). Static: no UpdateSpellEffectArea (114).
/// </summary>
public static class SpellAreas
{
    private sealed record AreaSpawn : ClusterObject;

    private static int _spawns;

    /// <summary>
    /// The area of node <paramref name="node"/> (client index <paramref name="index"/>) of <paramref name="spell"/> in the hit
    /// <paramref name="c"/>: origin the caster (position="atcaster") or the target point, direction castdirection (caster → aim),
    /// casterrotation (its facing), targetrotation (the target's), none at the caster castdirection, else 0; time × 1000 ms from the hit.
    /// </summary>
    public static SpellArea? Create(SpellCasts.SpellContext c, SpellTemplate spell, XElement node, int index)
    {
        var world = c.Caster.World;
        var map = c.Map;
        if (map.Grid is null || c.Caster.Map != map) return null;
        var caster = SpellMovement.PositionOf(c.Caster, c.Ticks);
        var aim = c.Target is { } target ? SpellMovement.PositionOf(target, c.Ticks) : c.Point;
        var atCaster = (string?)node.Attribute("position") == "atcaster";
        var origin = atCaster ? caster : aim;
        var line = aim - caster;
        // No rotation at the caster (PYROBLAST_SKILLSHOT_HIT's skill shot): read as castdirection.
        var yaw = ((string?)node.Attribute("rotation") ?? (atCaster ? "castdirection" : "")) switch
        {
            "castdirection" => line.LengthSquared() > 1e-4f ? AI.MotionMaster.Facing(caster, aim) : c.Caster.Facing,
            "casterrotation" => c.Caster.Facing,
            "targetrotation" => c.Target switch { Mob m => m.Direction, Player q => q.Facing, _ => 0f },
            _ => 0f,
        };
        var effectName = (string?)node.Attribute("effect");
        var effect = effectName is null or "NONE" ? null : world.Spells?.Template(effectName);
        if (effect is null && effectName is not (null or "NONE"))
            Combat.LogOnce("spell:" + effectName, $"spell {effectName} of {spell.Name}'s area is not in spells.xml: the area applies nothing");
        var shapes = Nodes(node).ToList();
        var seconds = SpellTemplate.Float(node, "time");
        var area = new SpellArea(world.ObjectIds.Next(), new AreaSpawn
        {
            Key = new SpawnKey(map.Cluster, "area", _spawns++), Tile = "", Position = origin, Component = new XElement("spelleffectarea"),
        })
        {
            Owner = c.Caster, Holder = spell, Index = (byte)index, Node = node, Effect = effect, Origin = origin, Shapes = shapes,
            Reach = shapes.Count == 0 ? SpellTemplate.Float(node, "effectarearadius") : shapes.Max(n => n.Reach),
            Yaw = (float)Math.Round(yaw, 3), Start = c.Ticks, End = c.Ticks + SpellCasts.Seconds(seconds), Counter = c.Counter, Ap = c.AbilityPower,
            CcAp = c.CcAbilityPower, Channel = SpellTemplate.Bool(node, "linktochanneling") ? c.Channel : null,
        };
        map.AddObject(area);
        Log.Debug("combat", string.Create(CultureInfo.InvariantCulture,
            $"'{c.Caster.Character.Name}' {spell}: area #{area.Id} [{index}] at ({origin.X:0.##}, {origin.Y:0.##}) facing {area.Yaw:0.#} for {seconds:0.##} s"));
        Tick(world, area);
        return area;
    }

    /// <summary>One tick of <paramref name="area"/>: its end, else the next tick, then the targets that entered and left.</summary>
    private static void Tick(World world, SpellArea area)
    {
        if (area.Gone || area.Map is not { } map) return;
        var now = world.GameNow;
        if (area.Owner.Map != map || (area.End > area.Start && now >= area.End) || area.Channel is { Over: true })
        {
            Remove(area);
            return;
        }
        var node = area.Node;
        var once = SpellTemplate.Bool(node, "applyeffectonlyoncepertarget");
        var max = (int)SpellTemplate.Float(node, "maxeffectareatargets");
        var inside = new HashSet<long>();
        // The next tick first: an effect that throws (the timer catches it) must not leave an area that never leaves.
        var next = world.Clock.Now + world.Config.TickTime;
        if (area.End > area.Start) next = TimeSpan.FromTicks(Math.Min(next.Ticks, world.ClockAt(area.End).Ticks));
        map.Timers.At(next, () => Tick(world, area));
        // A list first: a kill drops loot onto the map (and an effect may make objects) while the effects run.
        foreach (var unit in Candidates(area, map, now).ToList())
        {
            if (unit.Map != map || unit is Mob { Attackable: false }) continue;
            if (!Inside(area, SpellMovement.PositionOf(unit, now), unit is Mob m ? m.Template.CollisionRadius : world.CollisionRadius, now)) continue;
            inside.Add(unit.Id);
            if (area.InsideLast.Contains(unit.Id) || (once && area.Applied.Contains(unit.Id)) || (max > 0 && area.Hits >= max)) continue;
            // Nothing of the effect acts on this unit (its kind; for a unit but the owner, its nodes' classes, SpellCasts.Reaches): no hit, no
            // CastHits, not one of maxeffectareatargets. The owner keeps the reading of before (its radius nodes act around it).
            if (area.Effect is { } acts && (!SpellTargets.Kind(acts, unit) || (unit != area.Owner && !SpellCasts.Reaches(area.Owner, acts, unit, now)))) continue;
            area.Applied.Add(unit.Id);
            if (area.Effect is not { } effect) continue;
            if (!SpellEffects.Immune(unit, effect, now) && unit is not Mob { Immune: true }) area.Hits++;
            var target = unit == area.Owner ? null : unit;
            SpellCasts.Execute(new SpellCasts.SpellContext(area.Owner, map, area.Holder, 0, area.Ap, target, target is null ? area.Origin : SpellMovement.PositionOf(target, now), now)
            {
                CcAbilityPower = area.CcAp, Source = area.Origin, FromArea = true, Depth = 1, Channel = area.Channel,
            }, effect);
            if (area.Gone) return;
        }
        if (SpellTemplate.Bool(node, "removeonleave") && area.Effect is { } left)
            foreach (var id in area.InsideLast.Where(id => !inside.Contains(id)))
            {
                IUnit? unit = map.Objects.GetValueOrDefault(id) as Mob;
                unit ??= map.FindPlayer(id); // the owner or a Friend (a bot too)
                if (unit is null || !SpellEffects.Remove(world, unit, s => s.Spell == left && s.Caster == area.Owner.Id)) continue;
                SpellEffects.Send(world, unit);
                SpellEffects.Refresh(world, unit);
            }
        area.InsideLast.Clear();
        area.InsideLast.UnionWith(inside);
        if (max > 0 && area.Hits >= max && SpellTemplate.Bool(node, "deletewhenmaxtargetshit")) Remove(area);
    }

    /// <summary>
    /// Who an area may hit (stageJ pvp-spells SPEC §2.6): the mobs, players and bots near it that match its class for the owner
    /// (<see cref="SpellTargets.Effect"/>; target absent = enemy). A ground area (SPIRITANIMAL's heal; MIGHTYSWING's damage wave, whose
    /// nested shape nodes <see cref="Nodes"/> does not read yet) hits whoever its effect spell's nodes take, read as all [inference: the
    /// client's table has Ground false, but these areas carry their effect to units].
    /// </summary>
    private static IEnumerable<IUnit> Candidates(SpellArea area, Map map, long now)
    {
        var cls = SpellTargets.Parse((string?)area.Node.Attribute("target") ?? "enemy");
        if (cls == SpellTargets.Cls.None) cls = SpellTargets.Cls.Enemy;
        if (cls == SpellTargets.Cls.Ground) cls = SpellTargets.Cls.All;
        var owner = area.Owner;
        foreach (var m in map.Objects.Values.OfType<Mob>())
            if (Vector2.Distance(m.Position, area.Origin) <= area.Reach + m.Template.CollisionRadius + 1 // + 1: one on its way
                && SpellTargets.Effect(cls, SpellTargets.Of(owner, m, now))) yield return m;
        var reach = area.Reach + owner.World.CollisionRadius + 1;
        foreach (var q in map.Characters)
            if (Vector2.Distance(SpellMovement.PositionOf(q, now), area.Origin) <= reach && SpellTargets.Effect(cls, SpellTargets.Of(owner, q, now))) yield return q;
    }

    /// <summary>Takes <paramref name="area"/> off its map: a Leave to every client that knows it.</summary>
    public static void Remove(SpellArea area)
    {
        if (area.Gone) return;
        area.Gone = true;
        area.Map?.RemoveObject(area);
        Log.Debug("combat", $"area #{area.Id} of {area.Holder} left");
    }

    /// <summary>
    /// removespelleffectareasinarea spell removeradius ownedby (stageD effects batch 3; MOUNTSPELL_BEHEMOTH_STICKY_BARREL takes its own
    /// older barrels off): every area of <c>spell</c> (any without one) whose origin lies within removeradius of <paramref name="center"/>
    /// leaves; ownedby friendly = the players' areas, enemy = the mobs', all both. Returns how many left.
    /// </summary>
    public static int RemoveInArea(Player caster, Map map, Vector2 center, XElement node)
    {
        var name = (string?)node.Attribute("spell");
        var radius = SpellTemplate.Float(node, "removeradius");
        var owned = (string?)node.Attribute("ownedby") ?? "all";
        bool Fits(SpellTemplate holder, Vector2 origin) => (name is null || holder.Name == name) && Vector2.Distance(origin, center) <= radius;
        var n = 0;
        if (owned is "friendly" or "all")
            foreach (var area in map.Objects.Values.OfType<SpellArea>().Where(a => !a.Gone && Fits(a.Holder, a.Origin)).ToList())
            {
                Remove(area);
                n++;
            }
        if (owned is "enemy" or "all")
            foreach (var area in map.Objects.Values.OfType<MobArea>().Where(a => !a.Gone && Fits(a.Holder, a.Origin)).ToList())
            {
                MobCasts.Remove(area);
                n++;
            }
        if (n > 0) Log.Debug("combat", $"'{caster.Character.Name}' removed {n} area(s) of {name ?? "any spell"}");
        return n;
    }

    /// <summary>
    /// Whether an area of <paramref name="holder"/> with node <paramref name="node"/> fits a batch-3 removal's filters: <paramref name="spell"/>
    /// names its holding spell (null = any) and <paramref name="tags"/> its areatag (comma-separated; null = any).
    /// </summary>
    internal static bool Fits(SpellTemplate holder, XElement node, string? spell, string? tags) =>
        (spell is null || holder.Name == spell)
        && (tags is null || ((string?)node.Attribute("areatag") is { } tag && tags.Split(',', StringSplitOptions.TrimEntries).Contains(tag)));

    /// <summary>
    /// removespelleffectareasoftarget spell areatags target="self" (stageD effects batch 3; FLAME_ORB_COLLISION_EFFECT, AFTER_IMAGE_REMOVE,
    /// SHAPESHIFT_WEREWOLF_TO_HUMAN): every area of <paramref name="owner"/> whose holding spell and areatag fit leaves. Returns how many.
    /// </summary>
    public static int RemoveOf(Player owner, string? spell, string? tags)
    {
        if (owner.Map is not { } map) return 0;
        var gone = map.Objects.Values.OfType<SpellArea>().Where(a => a.Owner == owner && !a.Gone && Fits(a.Holder, a.Node, spell, tags)).ToList();
        foreach (var area in gone) Remove(area);
        return gone.Count;
    }

    /// <summary>
    /// removespelleffectareasaffectingtarget spell ownedby="caster" effectarearadius maxareasremoved (stageD effects batch 3): the areas of
    /// <paramref name="caster"/> (holding spell <c>spell</c>, any without one) that touch <paramref name="target"/> (its position with
    /// effectarearadius + its collision radius) leave, at most maxareasremoved (0 = all). Returns how many.
    /// </summary>
    public static int RemoveAffecting(Player caster, IUnit target, XElement node)
    {
        if (caster.Map is not { } map) return 0;
        var now = caster.World.GameNow;
        var at = SpellMovement.PositionOf(target, now);
        var radius = SpellTemplate.Float(node, "effectarearadius") + (target is Mob m ? m.Template.CollisionRadius : 0);
        var max = (int)SpellTemplate.Float(node, "maxareasremoved");
        var gone = map.Objects.Values.OfType<SpellArea>()
            .Where(a => a.Owner == caster && !a.Gone && Fits(a.Holder, a.Node, (string?)node.Attribute("spell"), null) && Inside(a, at, radius, now))
            .Take(max > 0 ? max : int.MaxValue).ToList();
        foreach (var area in gone) Remove(area);
        return gone.Count;
    }

    /// <summary>The areas of <paramref name="owner"/> on its map that end with it (removeoncasterdeath; its knockdown) or with <paramref name="channel"/>.</summary>
    public static void OwnerGone(Player owner, SpellCasts.ChannelState? channel = null)
    {
        if (owner.Map is not { } map) return;
        foreach (var area in map.Objects.Values.OfType<SpellArea>().Where(a => a.Owner == owner).ToList())
            if (channel is null ? SpellTemplate.Bool(area.Node, "removeoncasterdeath") || area.Channel is not null : area.Channel == channel)
                Remove(area);
    }

    // ---- aura (SPEC §3.5) ----

    /// <summary>
    /// An aura sub-effect of <paramref name="entry"/> on <paramref name="unit"/> (its caster's own: FLAMEDASH, HAMMER_SHOVE,
    /// LANCE_CHARGE): while it is in force, every world tick each unit within auraradius (+ its collision radius) of the owner's
    /// current position (on its dash path) that matches the class of the aura's spell (stageJ pvp-spells SPEC §2.6: its spell-level target;
    /// enemy, all and none the attackable mobs, friendall the owner and its Friends, as MOUNTSPELL_CHARIOT_DEFENSESTATION) and has just come in gets
    /// the aura's spell, with the entry's Ap (at most once per target with applyeffectonlyoncepertarget, at most maxauratargets). A
    /// re-application starts it anew.
    /// </summary>
    public static void Aura(SpellCasts.SpellContext c, ActiveSpell entry, SubEffect sub, IUnit unit)
    {
        var node = sub.Node;
        if (unit is not Player owner || entry.Caster != owner.Id || owner.Map is not { } map)
        {
            Combat.LogOnce("aura:" + entry.Spell.Name, $"the aura of {entry.Spell.Name} on another unit than its caster is not supported yet");
            return;
        }
        var world = owner.World;
        if (world.Spells?.Template((string?)node.Attribute("spell")) is not { } spell)
        {
            Combat.LogOnce("spell:" + (string?)node.Attribute("spell"), $"aura spell {(string?)node.Attribute("spell")} of {entry.Spell.Name} is not in spells.xml");
            return;
        }
        var generation = ++sub.Generation;
        var radius = SpellTemplate.Float(node, "auraradius");
        var once = SpellTemplate.Bool(node, "applyeffectonlyoncepertarget");
        var max = (int)SpellTemplate.Float(node, "maxauratargets");
        var applied = new HashSet<long>();
        var last = new HashSet<long>();
        // A spell-level all only says "wherever it is applied" (SPEC §1.3): the auras of class all (DASHDMG_EFFECT, QS_SLOWROPE, SPINNING_SMASH)
        // act on enemies, so they scan the attackable mobs as before; the owner is not its own target (DASHDMG_EFFECT's self node would
        // take SPIRITSPEAR_PENALTY off on every dash). A friend class (the mount auras, DUAL_NATURE) keeps the owner and its Friends.
        var cls = SpellTargets.Parse(spell.Target);
        if (cls is SpellTargets.Cls.None or SpellTargets.Cls.All) cls = SpellTargets.Cls.Enemy;
        void Tick()
        {
            var now = world.GameNow;
            if (owner.Map != map || owner.Combat.Down is not null || sub.Generation != generation || !owner.Effects.Contains(entry) || !entry.On(sub, now)) return;
            var here = SpellMovement.PositionOf(owner, now);
            var inside = new HashSet<long>();
            IEnumerable<(IUnit Unit, Vector2 At, float Radius)> near = [
                .. map.Objects.Values.OfType<Mob>().Select(m => ((IUnit)m, m.Position, m.Template.CollisionRadius)),
                .. map.Characters.Select(q => ((IUnit)q, SpellMovement.PositionOf(q, now), world.CollisionRadius))];
            foreach (var (u, at, size) in near.ToList())
            {
                if (Vector2.Distance(here, at) > radius + size || !SpellTargets.Effect(cls, SpellTargets.Of(owner, u, now))) continue;
                inside.Add(u.Id);
                if (last.Contains(u.Id) || (once && applied.Contains(u.Id)) || (max > 0 && applied.Count >= max)) continue;
                applied.Add(u.Id);
                var target = u == owner ? null : u;
                SpellCasts.Execute(new SpellCasts.SpellContext(owner, map, entry.Spell, 0, entry.Ap, target, at, now)
                    { CcAbilityPower = entry.CcAp, Source = here, Depth = c.Depth + 1 }, spell);
            }
            last = inside;
            map.Timers.At(world.Clock.Now + world.Config.TickTime, Tick);
        }
        Tick();
    }

    // ---- shapes ----

    /// <summary>
    /// One &lt;node angle distance&gt; of an &lt;area&gt;&lt;shape&gt; with its circle (Radius), rectangle (Width across × Height
    /// along) or arc (Opening, Inner, Outer); Kind null = none. Reach: how far from the origin it reaches at most.
    /// </summary>
    public sealed record ShapeNode(float Angle, Curve Distance, string? Kind, Curve Radius, float Width, float Height, float Opening, Curve Inner, Curve Outer)
    {
        public ShapeNode? Parent { get; init; }

        public Curve? AnimatedWidth { get; init; }

        public Curve? AnimatedHeight { get; init; }

        public Curve? AnimatedOpening { get; init; }

        private float OffsetReach => (float)Distance.Keys.Max(k => Math.Abs(k.V)) + (Parent?.OffsetReach ?? 0);

        public float Reach => OffsetReach + (Kind switch
        {
            "circle" => (float)Radius.Max,
            "rectangle" => new Vector2((float)(AnimatedWidth?.Max ?? Width), (float)(AnimatedHeight?.Max ?? Height)).Length() / 2,
            "arc" => (float)Outer.Max,
            _ => 0f,
        });

        public (Vector2 Center, float Yaw) Frame(Vector2 origin, float direction, double seconds)
        {
            var (center, yaw) = Parent?.Frame(origin, direction, seconds) ?? (origin, direction);
            yaw += Angle;
            return (center + SpellMovement.Facing(yaw) * (float)Distance.At(seconds), yaw);
        }
    }

    /// <summary>
    /// The leaf shapes of a spelleffectarea node (also the mobs' areas, <see cref="MobCasts"/>), retaining nested node transforms.
    /// A shape without a node (AOE_SLOW, BLIZZARD) is at the origin. Compact curves span the area's time.
    /// </summary>
    public static IEnumerable<ShapeNode> Nodes(XElement node)
    {
        var seconds = SpellTemplate.Float(node, "time");
        return node.Element("area")?.Element("shape") is { } shape ? Walk(shape, null) : [];

        IEnumerable<ShapeNode> Walk(XElement element, ShapeNode? parent)
        {
            var frame = new ShapeNode(SpellTemplate.Float(element, "angle"), Curve.Of((string?)element.Attribute("distance"), seconds),
                null, Curve.Of(null), 0, 0, 360, Curve.Of(null), Curve.Of(null)) { Parent = parent };
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName == "node")
                {
                    foreach (var nested in Walk(child, frame)) yield return nested;
                }
                else if (child.Name.LocalName is "circle" or "rectangle" or "arc")
                    yield return frame with
                    {
                        Kind = child.Name.LocalName, Radius = Curve.Of((string?)child.Attribute("radius"), seconds),
                        Width = SpellTemplate.Float(child, "width"), Height = SpellTemplate.Float(child, "height"),
                        AnimatedWidth = Curve.Of((string?)child.Attribute("width"), seconds),
                        AnimatedHeight = Curve.Of((string?)child.Attribute("height"), seconds),
                        Opening = SpellTemplate.Float(child, "angle", 360),
                        AnimatedOpening = Curve.Of((string?)child.Attribute("angle") ?? "360", seconds),
                        Inner = Curve.Of((string?)child.Attribute("innerradius"), seconds),
                        Outer = Curve.Of((string?)child.Attribute("outerradius"), seconds),
                    };
            }
        }
    }

    /// <summary>
    /// Whether a unit at <paramref name="at"/> with <paramref name="radius"/> (its collision radius) touches the area at
    /// <paramref name="now"/>: inside any node shape (SPEC §4), each placed at the origin + its distance along direction + its
    /// angle and turned the same way: circle radius, rectangle width (across) × height (along), arc angle (its full opening)
    /// between innerradius and outerradius. An area without shapes: a circle of effectarearadius at the origin. Animated values
    /// ("A 0:0.5; 0.6:16.5") are read at the time since the start.
    /// </summary>
    public static bool Inside(SpellArea area, Vector2 at, float radius, long now) =>
        Inside(area.Shapes, area.Reach, area.Origin, area.Yaw, area.Start, at, radius, now);

    /// <summary>
    /// <see cref="Inside(SpellArea, Vector2, float, long)"/> for any area: its shapes, reach, origin, direction and start.
    /// <paramref name="zeroOff"/>: a circle whose radius is 0 at that time touches nothing (the mobs' animated circles, AOE_SLOW_EFFECT's
    /// "A 0:0;1.69:0;1.7:4", stageD mobspells §4.4 item 6).
    /// </summary>
    public static bool Inside(IReadOnlyList<ShapeNode> shapes, float reach, Vector2 origin, float direction, long start, Vector2 at, float radius, long now, bool zeroOff = false)
    {
        var t = Math.Max(0, (now - start) / (double)TimeSpan.TicksPerSecond);
        if (shapes.Count == 0) return Vector2.Distance(at, origin) <= reach + radius;
        foreach (var n in shapes)
        {
            var (center, yaw) = n.Frame(origin, direction, t);
            var along = SpellMovement.Facing(yaw);
            var across = new Vector2(along.Y, -along.X);
            var d = at - center;
            switch (n.Kind)
            {
                case "circle":
                    if (zeroOff && n.Radius.At(t) <= 0) break;
                    if (d.Length() <= n.Radius.At(t) + radius) return true;
                    break;
                case "rectangle":
                    var width = n.AnimatedWidth?.At(t) ?? n.Width;
                    var height = n.AnimatedHeight?.At(t) ?? n.Height;
                    if (zeroOff && (width <= 0 || height <= 0)) break;
                    if (Math.Abs(Vector2.Dot(d, along)) <= height / 2 + radius && Math.Abs(Vector2.Dot(d, across)) <= width / 2 + radius) return true;
                    break;
                case "arc":
                    var opening = n.AnimatedOpening?.At(t) ?? n.Opening;
                    if (opening <= 0 || (zeroOff && n.Outer.At(t) <= 0)) break;
                    var length = d.Length();
                    if (length > n.Outer.At(t) + radius || length < n.Inner.At(t) - radius) break;
                    if (length < 0.01f) return true;
                    var off = Math.Acos(Math.Clamp(Vector2.Dot(d / length, along), -1, 1)) * 180 / Math.PI;
                    if (off <= opening / 2) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// A spells.xml constant, compact start-end range over the area's lifetime, or explicit seconds:value keyframes (leading
    /// A/S flags dropped), linear between keys and held after the last.
    /// </summary>
    public sealed record Curve(IReadOnlyList<(double T, double V)> Keys)
    {
        public static Curve Of(string? text, double duration = 1)
        {
            if (string.IsNullOrWhiteSpace(text)) return new([(0, 0)]);
            var body = text.TrimStart().TrimStart('A', 'S', 'a', 's').Trim();
            if (double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out var constant)) return new([(0, constant)]);
            // Compact start-end curves span the area's lifetime; explicit keys retain their seconds.
            const string number = @"[+-]?\d*\.?\d+(?:[eE][+-]?\d+)?";
            var range = Regex.Match(body, $@"^({number})\s*-\s*({number})$");
            if (duration > 0 && range.Success
                && double.TryParse(range.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var from)
                && double.TryParse(range.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var to))
                return new([(0, from), (duration, to)]);
            var keys = new List<(double, double)>();
            foreach (var part in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var k)
                    && double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) keys.Add((k, v));
                else if (kv.Length == 1 && double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) keys.Add((0, c));
            }
            return new(keys.Count > 0 ? keys : [(0, 0)]);
        }

        /// <summary>The largest value it takes.</summary>
        public double Max => Keys.Max(k => k.V);

        public double At(double t)
        {
            if (t <= Keys[0].T) return Keys[0].V;
            for (var i = 1; i < Keys.Count; i++)
                if (t <= Keys[i].T)
                {
                    var (t0, v0) = Keys[i - 1];
                    var (t1, v1) = Keys[i];
                    return t == t1 || t1 <= t0 ? v1 : v0 + (v1 - v0) * (t - t0) / (t1 - t0);
                }
            return Keys[^1].V;
        }
    }
}
