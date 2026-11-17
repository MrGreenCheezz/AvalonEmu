using System.Numerics;
using AlbionLocal.Modules;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using IUnit = AlbionLocal.Modules.IUnit;
using SUnit = AlbionLocal.Server.Entities.IUnit;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// The gameplay actions (<see cref="IGameplay"/>, docs/modules/README.md "Actions"): each goes through the engine's own path (the damage
/// paths of <see cref="Combat"/> and <see cref="Pvp"/>, <see cref="SpellEffects.Apply"/>, <see cref="SpellMovement.Force"/>, <see cref="SpellCasts.Execute"/>),
/// so the clients get the same events as for the game's own effects. One per host; world thread only.
/// </summary>
internal sealed class GameplayFacade(World world, ModuleHost host) : IGameplay
{
    private long Now => world.GameNow;

    private SpellTemplate? Template(string? name) => name is null ? null : world.Spells?.Template(name);

    /// <summary>Inside a damage or heal hook <paramref name="action"/> waits for the next tick: true when it was put off. The same module must still be there then.</summary>
    private bool Later(Action action)
    {
        if (!host.InCombatHook) return false;
        var owner = ModuleHost.Current;
        world.Post(() =>
        {
            if (owner is { Dropped: false }) host.Guard(owner, "a hit put off to the next tick", action);
        });
        return true;
    }

    public double Damage(IUnit source, IUnit target, double amount, bool magic = true, string? spell = null)
    {
        if (Later(() => Damage(source, target, amount, magic, spell))) return 0;
        if (!double.IsFinite(amount) || amount <= 0 || ModuleHost.Unwrap(source) is not { Map: not null } src || ModuleHost.Unwrap(target) is not { Map: not null } tgt) return 0;
        var type = magic ? EffectType.Magic : EffectType.Physical;
        var id = Template(spell)?.Id ?? (short)-1;
        var before = ModuleHost.HealthOf(tgt, Now);
        switch (src, tgt)
        {
            case (Player p, Mob m):
                Combat.Damage(p, m, amount, type, HitOrigin.Direct, id, Now);
                break;
            case (Mob m, Player q):
                Combat.DamagePlayer(m, q, amount, type, HitOrigin.Direct, id, Now);
                break;
            case (Player p, Player q) when p != q:
                Pvp.Damage(p, q, amount, type, HitOrigin.Direct, id, Now);
                break;
            default:
                return 0;
        }
        return Math.Max(0, before - ModuleHost.HealthOf(tgt, Now));
    }

    public double Heal(IUnit source, IUnit target, double amount, string? spell = null)
    {
        if (Later(() => Heal(source, target, amount, spell))) return 0;
        if (!double.IsFinite(amount) || amount <= 0 || ModuleHost.Unwrap(target) is not { Map: not null } tgt) return 0;
        var id = Template(spell)?.Id ?? (short)-1;
        var before = ModuleHost.HealthOf(tgt, Now);
        switch (tgt)
        {
            case Player q:
                Combat.HealPlayer(ModuleHost.Unwrap(source) as Player ?? q, q, amount, HitOrigin.Direct, id, Now);
                break;
            case Mob m:
                MobCasts.Heal(m, amount, HitOrigin.Direct, id, Now);
                break;
        }
        return Math.Max(0, ModuleHost.HealthOf(tgt, Now) - before);
    }

    public bool ApplyBuff(IUnit target, string spell, IUnit? caster = null, double seconds = 0, int stacks = 1, double abilityPower = 0)
    {
        if (ModuleHost.Unwrap(target) is not { Map: not null } unit || Template(spell) is not { } t) return false;
        var by = ModuleHost.Unwrap(caster) ?? unit;
        if (host.BuffBlocked(unit, t, by)) return false;
        var now = Now;
        var weapon = by is Player cp ? cp.Inventory.Equipment[0] : null;
        var ap = abilityPower > 0 ? abilityPower : by is Player bp ? Combat.AbilityPowerOf(bp, weapon) : 100;
        var cc = abilityPower > 0 ? abilityPower : by is Player bc ? Combat.CcAbilityPowerOf(bc, weapon) : 100;
        var subs = new SubEffect?[t.Effects.Count];
        for (var i = 0; i < subs.Length; i++)
        {
            subs[i] = SpellEffects.Make(world, by, ap, cc, t.Effects[i], i, unit, now);
            if (subs[i] is { } s && seconds > 0 && double.IsFinite(seconds)) s.DurationMs = Math.Max(1, (int)Math.Round(seconds * 1000));
        }
        if (subs.All(s => s is null)) return false;
        var entry = SpellEffects.Apply(world, unit, t, by.Id, ap, cc, now, subs);
        if (stacks > 1)
        {
            var before = entry.Stacks;
            entry.Stacks = (byte)Math.Clamp(stacks, 1, Math.Min(t.StackLimit, byte.MaxValue));
            if (before != entry.Stacks) host.BuffStacks(unit, entry, before);
        }
        SpellEffects.Send(world, unit);
        SpellEffects.Refresh(world, unit);
        return true;
    }

    public bool RemoveBuff(IUnit target, string spell, int stacks = 0)
    {
        if (ModuleHost.Unwrap(target) is not { Map: not null } unit) return false;
        if (!SpellEffects.Remove(world, unit, s => s.Spell.Name.Equals(spell, StringComparison.OrdinalIgnoreCase), Math.Max(0, stacks))) return false;
        SpellEffects.Send(world, unit);
        SpellEffects.Refresh(world, unit);
        return true;
    }

    public IReadOnlyList<IBuff> Buffs(IUnit unit)
    {
        if (ModuleHost.Unwrap(unit) is not { } u) return [];
        u.Effects.Prune(Now);
        return [.. u.Effects.Spells.Select(s => new BuffFacade(host, u, s))];
    }

    public IBuff? FindBuff(IUnit unit, string spell)
    {
        if (ModuleHost.Unwrap(unit) is not { } u) return null;
        u.Effects.Prune(Now);
        return u.Effects.Spells.FirstOrDefault(s => s.Spell.Name.Equals(spell, StringComparison.OrdinalIgnoreCase)) is { } e ? new BuffFacade(host, u, e) : null;
    }

    public bool Knockback(IUnit target, float fromX, float fromY, float distance, float speed = 10)
    {
        if (ModuleHost.Unwrap(target) is not { Map: not null } u || distance <= 0) return false;
        var here = SpellMovement.PositionOf(u, Now);
        var away = here - new Vector2(fromX, fromY);
        var dir = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : new Vector2(0, 1);
        return SpellMovement.Force(world, u, 2, here + dir * distance, speed, 0);
    }

    public bool Pull(IUnit target, float toX, float toY, float speed = 10, float stopAt = 1)
    {
        if (ModuleHost.Unwrap(target) is not { Map: not null } u) return false;
        var here = SpellMovement.PositionOf(u, Now);
        var line = new Vector2(toX, toY) - here;
        var length = line.Length() - Math.Max(0, stopAt);
        return length > 0.01f && SpellMovement.Force(world, u, 4, here + Vector2.Normalize(line) * length, speed, 0);
    }

    public bool Dash(IUnit unit, float x, float y, float speed = 15) =>
        ModuleHost.Unwrap(unit) is { Map: not null } u && SpellMovement.Force(world, u, 0, new Vector2(x, y), speed, u.Id);

    public bool Teleport(IUnit unit, float x, float y) =>
        ModuleHost.Unwrap(unit) is { Map: not null } u && SpellMovement.Force(world, u, SpellMovement.TeleportKind, new Vector2(x, y), 0, u.Id);

    public IModuleArea? CreateArea(IUnit owner, float x, float y, float radius, TimeSpan duration, TimeSpan interval, Action<AreaTickArgs> onTick, string? visual = null)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        if (ModuleHost.Unwrap(owner) is not { Map: { } map } o || radius <= 0) return null;
        var area = new ModuleArea(world, host, ModuleHost.RequireCurrent(), map, o, owner, new Vector2(x, y), radius, duration,
            interval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : interval, onTick);
        area.Start();
        if (visual is not null) area.Show(visual, duration);
        return area;
    }

    public IMob? Summon(IUnit owner, string mob, float x, float y, TimeSpan? lifetime = null)
    {
        if (host.RefusedInCombatHook(mob)) return null;
        if (ModuleHost.Unwrap(owner) is not { Map: { Grid: not null } map } || world.Clusters?.MobsIfLoaded?.Find(mob) is not { } template) return null;
        var m = SpawnManager.SpawnAt(map, template, new Vector2(x, y), 0, world);
        if (lifetime is { } life)
            map.Timers.At(world.Clock.Now + life, () =>
            {
                if (!m.Dead && !m.Removed) SpawnManager.Despawn(m, world, respawn: false);
            });
        return host.Wrap(m);
    }

    public void SetModifier(IUnit unit, string source, string attribute, double value)
    {
        if (ModuleHost.Unwrap(unit) is not { } u || !double.IsFinite(value)) return;
        u.Effects.SetModifier(ModuleHost.RequireCurrent().Name, source, attribute, value);
        if (u.Map is not null) SpellEffects.Refresh(world, u);
    }

    public bool ClearModifier(IUnit unit, string source, string? attribute = null)
    {
        if (ModuleHost.Unwrap(unit) is not { } u || !u.Effects.ClearModifiers(ModuleHost.RequireCurrent().Name, source, attribute)) return false;
        if (u.Map is not null) SpellEffects.Refresh(world, u);
        return true;
    }

    public double CooldownLeft(IPlayer player, string spell) =>
        ModuleHost.Unwrap(player) is Player p && Template(spell) is { } t && p.Combat.Cooldowns.TryGetValue(t.Id, out var end) && end > Now
            ? (end - Now) / (double)TimeSpan.TicksPerSecond : 0;

    public bool SetCooldown(IPlayer player, string spell, double seconds)
    {
        if (ModuleHost.Unwrap(player) is not Player { Map: not null } p || Template(spell) is not { } t || !double.IsFinite(seconds)) return false;
        var left = SpellCasts.Seconds(Math.Max(0, seconds));
        p.Combat.Cooldowns[t.Id] = Now + left;
        Tell(p, [t.Id], left);
        return true;
    }

    public bool ResetCooldowns(IPlayer player)
    {
        if (ModuleHost.Unwrap(player) is not Player { Map: not null } p) return false;
        var running = p.Combat.Cooldowns.Where(c => c.Value > Now).Select(c => c.Key).ToList();
        foreach (var id in running) p.Combat.Cooldowns[id] = Now;
        Tell(p, running, 0);
        return true;
    }

    /// <summary>SpellCooldownUpdate for the slots that hold <paramref name="spells"/>: each ready <paramref name="left"/> from now (as setcooldowns sends it).</summary>
    private void Tell(Player p, IReadOnlyCollection<short> spells, long left)
    {
        var slots = p.Session.SpellSlots(p.Inventory);
        List<byte> changed = [];
        List<long> ends = [], starts = [], spans = [];
        for (var slot = 0; slot < slots.Length; slot++)
        {
            if (!spells.Contains((short)slots[slot]) || world.Spells?.Template(slots[slot]) is not { } t) continue;
            var span = Math.Max(SpellCasts.Seconds(t.RecastDelay), left);
            if (span <= 0) continue;
            changed.Add((byte)slot);
            ends.Add(Now + left);
            starts.Add(Now + left - span);
            spans.Add(span);
        }
        if (changed.Count > 0)
            p.Session.Send(new SpellCooldownUpdateEvent { dy7 = [.. changed], dy8 = [.. ends], dy9 = [.. starts], dza = [.. spans], dzb = new byte[changed.Count] });
    }

    public bool CastSpell(IPlayer caster, string spell, IUnit? target = null, float? x = null, float? y = null)
    {
        if (Later(() => CastSpell(caster, spell, target, x, y))) return true;
        if (ModuleHost.Unwrap(caster) is not Player { Map: { } map } p || Template(spell) is not { } t) return false;
        var now = Now;
        var tgt = ModuleHost.Unwrap(target);
        if (tgt is not null && tgt.Map != map) return false;
        var point = x is { } px && y is { } py ? new Vector2(px, py) : SpellMovement.PositionOf(tgt ?? p, now);
        var ap = Combat.AbilityPowerOf(p, p.Inventory.Equipment[0]);
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = tgt?.Id ?? p.Id, bvx = point, bvy = t.Id, bvz = now, bv0 = now, Extra = new() { [6] = (byte)0 } });
        SpellCasts.Execute(new SpellCasts.SpellContext(p, map, t, 0, ap, tgt == p ? null : tgt, point, now) { CcAbilityPower = Combat.CcAbilityPowerOf(p, p.Inventory.Equipment[0]) }, t);
        return true;
    }

    public bool CastSpell(IMob caster, string spell, IUnit? target = null, float? x = null, float? y = null)
    {
        if (Later(() => CastSpell(caster, spell, target, x, y))) return true;
        if (ModuleHost.Unwrap(caster) is not Mob { Map: { } map, Dead: false, Removed: false } m || Template(spell) is not { } t) return false;
        var now = Now;
        var tgt = ModuleHost.Unwrap(target);
        if (tgt is not null && tgt.Map != map) return false;
        var point = x is { } px && y is { } py ? new Vector2(px, py) : SpellMovement.PositionOf(tgt ?? m, now);
        m.SendAround(new CastSpellEvent { ddn = m.Id, bvw = tgt?.Id ?? m.Id, bvx = point, bvy = t.Id, bvz = now, bv0 = now, Extra = new() { [6] = (byte)0 } });
        MobCasts.Execute(new MobCasts.MobHit(m, map, t, 0, m.Template.AbilityPower, tgt == m ? null : tgt, point, now), t);
        return true;
    }

    public bool Interrupt(IUnit unit)
    {
        switch (ModuleHost.Unwrap(unit))
        {
            case Player p when p.Combat.Cast is not null || p.Combat.Channel is not null:
                SpellCasts.InterruptPlayer(p, Now, "a module");
                return true;
            case Mob m:
                SpellCasts.InterruptMob(m);
                return true;
            default:
                return false;
        }
    }

    public UnitStats Stats(IUnit unit)
    {
        var now = Now;
        return ModuleHost.Unwrap(unit) switch
        {
            Player p => new UnitStats(p.Health.At(now), p.Health.Max, p.Energy.At(now), p.Energy.Max,
                p.Inventory.Equipment[0] is { } w ? Combat.ItemPower(p, w) : 0, Combat.ResistancePoints(p, EffectType.Physical), Combat.ResistancePoints(p, EffectType.Magic), SpellEffects.ResistanceOf(p, now),
                p.Inventory.Equipment[0]?.Template.Tier ?? 0),
            Mob m => new UnitStats(m.Health.At(now), m.Health.Max, m.Energy.At(now), m.Energy.Max, 0, Combat.ResistancePoints(m, EffectType.Physical), Combat.ResistancePoints(m, EffectType.Magic),
                SpellEffects.ResistanceOf(m, now), m.Template.Tier),
            _ => new UnitStats(0, 0, 0, 0, 0, 0, 0, 0, 0),
        };
    }

    public IReadOnlyList<IUnit> UnitsAround(string cluster, float x, float y, float radius) =>
        world.Maps.Find(cluster) is { } map ? [.. Around(map, new Vector2(x, y), radius, Now).Select(host.Wrap)] : [];

    internal static IEnumerable<SUnit> Around(Map map, Vector2 at, float radius, long now)
    {
        foreach (var m in map.Objects.Values.OfType<Mob>().ToList())
            if (!m.Dead && !m.Removed && Vector2.Distance(m.Position, at) <= radius + m.Template.CollisionRadius) yield return m;
        foreach (var q in map.Characters.ToList())
            if (q.Combat.Down is null && Vector2.Distance(SpellMovement.PositionOf(q, now), at) <= radius) yield return q;
    }

    public bool IsEnemy(IUnit caster, IUnit target) => (ModuleHost.Unwrap(caster), ModuleHost.Unwrap(target)) switch
    {
        (Player p, Mob m) => m.Attackable,
        (Player p, Player q) => p != q && SpellTargets.Of(p, q, Now).Hostile,
        (Mob, Player) => true,
        _ => false,
    };
}

/// <summary>A module's server-side area (<see cref="IGameplay.CreateArea"/>): map timers every interval until its end, each tick guarded as the module's call.</summary>
internal sealed class ModuleArea(World world, ModuleHost host, ModuleContext ctx, Map map, SUnit owner, IUnit ownerFacade, Vector2 at, float radius, TimeSpan duration,
    TimeSpan interval, Action<AreaTickArgs> onTick) : IModuleArea
{
    private readonly TimeSpan _end = world.Clock.Now + duration;
    private int _tick;

    public string Cluster => map.Cluster;
    public float X { get; set; } = at.X;
    public float Y { get; set; } = at.Y;
    public float Radius { get; set; } = radius;
    public IUnit Owner => ownerFacade;
    public bool Ended { get; private set; }

    public void Start()
    {
        ctx.Areas.Add(this);
        Tick();
    }

    private AreaVisual? _visual;
    private static int _visuals;

    private sealed record VisualSpawn : ClusterObject;

    /// <summary>Shows the area of spell <paramref name="spell"/> at its centre to the clients, for <paramref name="duration"/>: a NewSpellEffectArea, a Leave at the end.</summary>
    public void Show(string spell, TimeSpan duration)
    {
        var t = world.Spells?.Template(spell);
        var index = t?.Effects.ToList().FindIndex(n => n.Name.LocalName == "spelleffectarea") ?? -1;
        if (t is null || index < 0)
        {
            Photon.Log.Warn("module:" + ctx.Name, $"the area's look {spell}: {(t is null ? "no such spell" : "that spell makes no spelleffectarea")}; the area works, but is not seen");
            return;
        }
        var now = world.GameNow;
        var at = new Vector2(X, Y);
        _visual = new AreaVisual(world.ObjectIds.Next(), new VisualSpawn
        {
            Key = new SpawnKey(map.Cluster, "modulearea", _visuals++), Tile = "", Position = at, Component = new System.Xml.Linq.XElement("spelleffectarea"),
        })
        {
            Owner = owner.Id, Holder = t, Index = (byte)index, Origin = at, Start = now, End = now + SpellCasts.Seconds(duration.TotalSeconds),
        };
        var shown = _visual;
        if (host.InCombatHook) world.Post(() => { if (!Ended) map.AddObject(shown); }); // a hit can run inside a walk over the map's objects
        else map.AddObject(shown);
    }

    /// <summary>How a module area looks: the spelleffectarea of a spell, which the clients draw from their own data.</summary>
    private sealed class AreaVisual(long id, ClusterObject spawn) : WorldObject(id, spawn)
    {
        public required long Owner { get; init; }
        public required SpellTemplate Holder { get; init; }
        public required byte Index { get; init; }
        public required Vector2 Origin { get; init; }
        public required long Start { get; init; }
        public required long End { get; init; }
        public override Vector2 Position => Origin;
        public override float Direction => 0;
        public override string Kind => "module area";
        public override string TypeName => $"{Holder.Name}[{Index}]";
        public override bool LeaveOnRange => true;

        public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
        {
            yield return new NewSpellEffectAreaEvent
            {
                da6 = Id, da7 = Owner, da8 = Origin, dba = Holder.Id, dbb = Index, dbc = Start, dbd = End, dbe = 0, Extra = new() { [5] = Index, [9] = (byte)0 },
            };
        }
    }

    private void Tick()
    {
        if (Ended) return;
        if (ctx.Dropped || owner.Map != map || world.Clock.Now >= _end)
        {
            Dispose();
            return;
        }
        _tick++;
        var inside = GameplayFacade.Around(map, new Vector2(X, Y), Radius, world.GameNow).Select(host.Wrap).ToList();
        host.Guard(ctx, "area tick", onTick, new AreaTickArgs(this, inside, _tick));
        if (!Ended) map.Timers.At(world.Clock.Now + interval, Tick);
    }

    public void Dispose()
    {
        if (Ended) return;
        Ended = true;
        ctx.Areas.Remove(this);
        if (_visual is { } shown)
        {
            if (host.InCombatHook) world.Post(() => shown.Map?.RemoveObject(shown));
            else shown.Map?.RemoveObject(shown);
        }
    }
}
