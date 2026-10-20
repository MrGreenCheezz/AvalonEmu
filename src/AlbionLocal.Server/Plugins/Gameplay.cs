using System.Numerics;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using IUnit = AlbionLocal.Plugin.Api.IUnit;
using SUnit = AlbionLocal.Server.Entities.IUnit;

namespace AlbionLocal.Server.Plugins;

/// <summary>
/// The v2 gameplay actions (<see cref="IGameplay"/>, server/plugins/README.md "Actions"): each goes through the engine's own path (the damage
/// paths of <see cref="Combat"/> and <see cref="Pvp"/>, <see cref="SpellEffects.Apply"/>, <see cref="SpellMovement.Force"/>, <see cref="SpellCasts.Execute"/>),
/// so the clients get the same events as for the game's own effects. One per host; world thread only.
/// </summary>
internal sealed class GameplayFacade(World world, PluginHost host) : IGameplay
{
    private long Now => world.GameNow;

    private SpellTemplate? Template(string? name) => name is null ? null : world.Spells?.Template(name);

    public SpellInfo? Spell(string spell) =>
        Template(spell) is { } t ? new SpellInfo(t.Name, t.Id, t.Kind, t.Category, t.CastingTime, t.RecastDelay, t.CastRange, t.Target, t.StackLimit) : null;

    public double Damage(IUnit source, IUnit target, double amount, bool magic = true, string? spell = null)
    {
        if (!double.IsFinite(amount) || amount <= 0 || PluginHost.Unwrap(source) is not { Map: not null } src || PluginHost.Unwrap(target) is not { Map: not null } tgt) return 0;
        var type = magic ? EffectType.Magic : EffectType.Physical;
        var id = Template(spell)?.Id ?? (short)-1;
        var before = PluginHost.HealthOf(tgt, Now);
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
        return Math.Max(0, before - PluginHost.HealthOf(tgt, Now));
    }

    public double Heal(IUnit source, IUnit target, double amount, string? spell = null)
    {
        if (!double.IsFinite(amount) || amount <= 0 || PluginHost.Unwrap(target) is not { Map: not null } tgt) return 0;
        var id = Template(spell)?.Id ?? (short)-1;
        var before = PluginHost.HealthOf(tgt, Now);
        switch (tgt)
        {
            case Player q:
                Combat.HealPlayer(PluginHost.Unwrap(source) as Player ?? q, q, amount, HitOrigin.Direct, id, Now);
                break;
            case Mob m:
                MobCasts.Heal(m, amount, HitOrigin.Direct, id, Now);
                break;
        }
        return Math.Max(0, PluginHost.HealthOf(tgt, Now) - before);
    }

    public bool ApplyBuff(IUnit target, string spell, IUnit? caster = null, double seconds = 0, int stacks = 1, double abilityPower = 0)
    {
        if (PluginHost.Unwrap(target) is not { Map: not null } unit || Template(spell) is not { } t) return false;
        var by = PluginHost.Unwrap(caster) ?? unit;
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
        if (PluginHost.Unwrap(target) is not { Map: not null } unit) return false;
        if (!SpellEffects.Remove(world, unit, s => s.Spell.Name.Equals(spell, StringComparison.OrdinalIgnoreCase), Math.Max(0, stacks))) return false;
        SpellEffects.Send(world, unit);
        SpellEffects.Refresh(world, unit);
        return true;
    }

    public IReadOnlyList<IBuff> Buffs(IUnit unit)
    {
        if (PluginHost.Unwrap(unit) is not { } u) return [];
        u.Effects.Prune(Now);
        return [.. u.Effects.Spells.Select(s => new BuffFacade(host, u, s))];
    }

    public IBuff? FindBuff(IUnit unit, string spell)
    {
        if (PluginHost.Unwrap(unit) is not { } u) return null;
        u.Effects.Prune(Now);
        return u.Effects.Spells.FirstOrDefault(s => s.Spell.Name.Equals(spell, StringComparison.OrdinalIgnoreCase)) is { } e ? new BuffFacade(host, u, e) : null;
    }

    public bool Knockback(IUnit target, float fromX, float fromY, float distance, float speed = 10)
    {
        if (PluginHost.Unwrap(target) is not { Map: not null } u || distance <= 0) return false;
        var here = SpellMovement.PositionOf(u, Now);
        var away = here - new Vector2(fromX, fromY);
        var dir = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : new Vector2(0, 1);
        return SpellMovement.Force(world, u, 2, here + dir * distance, speed, 0);
    }

    public bool Pull(IUnit target, float toX, float toY, float speed = 10, float stopAt = 1)
    {
        if (PluginHost.Unwrap(target) is not { Map: not null } u) return false;
        var here = SpellMovement.PositionOf(u, Now);
        var line = new Vector2(toX, toY) - here;
        var length = line.Length() - Math.Max(0, stopAt);
        return length > 0.01f && SpellMovement.Force(world, u, 4, here + Vector2.Normalize(line) * length, speed, 0);
    }

    public bool Dash(IUnit unit, float x, float y, float speed = 15) =>
        PluginHost.Unwrap(unit) is { Map: not null } u && SpellMovement.Force(world, u, 0, new Vector2(x, y), speed, u.Id);

    public bool Teleport(IUnit unit, float x, float y) =>
        PluginHost.Unwrap(unit) is { Map: not null } u && SpellMovement.Force(world, u, SpellMovement.TeleportKind, new Vector2(x, y), 0, u.Id);

    public IPluginArea? CreateArea(IUnit owner, float x, float y, float radius, TimeSpan duration, TimeSpan interval, Action<AreaTickArgs> onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        if (PluginHost.Unwrap(owner) is not { Map: { } map } o || radius <= 0) return null;
        var area = new PluginArea(world, host, PluginHost.RequireCurrent(), map, o, owner, new Vector2(x, y), radius, duration,
            interval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : interval, onTick);
        area.Start();
        return area;
    }

    public IMob? Summon(IUnit owner, string mob, float x, float y, TimeSpan? lifetime = null)
    {
        if (PluginHost.Unwrap(owner) is not { Map: { Grid: not null } map } || world.Clusters?.MobsIfLoaded?.Find(mob) is not { } template) return null;
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
        if (PluginHost.Unwrap(unit) is not { } u || !double.IsFinite(value)) return;
        u.Effects.SetModifier(PluginHost.RequireCurrent().Name, source, attribute, value);
        if (u.Map is not null) SpellEffects.Refresh(world, u);
    }

    public bool ClearModifier(IUnit unit, string source, string? attribute = null)
    {
        if (PluginHost.Unwrap(unit) is not { } u || !u.Effects.ClearModifiers(PluginHost.RequireCurrent().Name, source, attribute)) return false;
        if (u.Map is not null) SpellEffects.Refresh(world, u);
        return true;
    }

    public double CooldownLeft(IPlayer player, string spell) =>
        PluginHost.Unwrap(player) is Player p && Template(spell) is { } t && p.Combat.Cooldowns.TryGetValue(t.Id, out var end) && end > Now
            ? (end - Now) / (double)TimeSpan.TicksPerSecond : 0;

    public bool SetCooldown(IPlayer player, string spell, double seconds)
    {
        if (PluginHost.Unwrap(player) is not Player { Map: not null } p || Template(spell) is not { } t || !double.IsFinite(seconds)) return false;
        var left = SpellCasts.Seconds(Math.Max(0, seconds));
        p.Combat.Cooldowns[t.Id] = Now + left;
        Tell(p, [t.Id], left);
        world.ExtraBars.Cooled(p, [t.Id]); // the extra bars' mod (event 9002), when it said hello
        return true;
    }

    public bool ResetCooldowns(IPlayer player)
    {
        if (PluginHost.Unwrap(player) is not Player { Map: not null } p) return false;
        var running = p.Combat.Cooldowns.Where(c => c.Value > Now).Select(c => c.Key).ToList();
        foreach (var id in running) p.Combat.Cooldowns[id] = Now;
        Tell(p, running, 0);
        world.ExtraBars.Cooled(p, running);
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
        if (PluginHost.Unwrap(caster) is not Player { Map: { } map } p || Template(spell) is not { } t) return false;
        var now = Now;
        var tgt = PluginHost.Unwrap(target);
        if (tgt is not null && tgt.Map != map) return false;
        var point = x is { } px && y is { } py ? new Vector2(px, py) : SpellMovement.PositionOf(tgt ?? p, now);
        var ap = Combat.AbilityPowerOf(p, p.Inventory.Equipment[0]);
        p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = tgt?.Id ?? p.Id, bvx = point, bvy = t.Id, bvz = now, bv0 = now, Extra = new() { [6] = (byte)0 } });
        SpellCasts.Execute(new SpellCasts.SpellContext(p, map, t, 0, ap, tgt == p ? null : tgt, point, now) { CcAbilityPower = Combat.CcAbilityPowerOf(p, p.Inventory.Equipment[0]) }, t);
        return true;
    }

    public bool Interrupt(IUnit unit)
    {
        switch (PluginHost.Unwrap(unit))
        {
            case Player p when p.Combat.Cast is not null || p.Combat.Channel is not null:
                SpellCasts.InterruptPlayer(p, Now, "a plugin");
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
        return PluginHost.Unwrap(unit) switch
        {
            Player p => new UnitStats(p.Health.At(now), p.Health.Max, p.Energy.At(now), p.Energy.Max,
                p.Inventory.Equipment[0] is { } w ? Combat.ItemPower(p, w) : 0, p.Defense.Armor, p.Defense.MagicResistance, SpellEffects.ResistanceOf(p, now),
                p.Inventory.Equipment[0]?.Template.Tier ?? 0),
            Mob m => new UnitStats(m.Health.At(now), m.Health.Max, m.Energy.At(now), m.Energy.Max, 0, m.Template.Armor, m.Template.MagicResistance,
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

    public bool IsEnemy(IUnit caster, IUnit target) => (PluginHost.Unwrap(caster), PluginHost.Unwrap(target)) switch
    {
        (Player p, Mob m) => m.Attackable,
        (Player p, Player q) => p != q && SpellTargets.Of(p, q, Now).Hostile,
        (Mob, Player) => true,
        _ => false,
    };
}

/// <summary>A plugin's server-side area (<see cref="IGameplay.CreateArea"/>): map timers every interval until its end, each tick guarded as the plugin's call.</summary>
internal sealed class PluginArea(World world, PluginHost host, PluginContext ctx, Map map, SUnit owner, IUnit ownerFacade, Vector2 at, float radius, TimeSpan duration,
    TimeSpan interval, Action<AreaTickArgs> onTick) : IPluginArea
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
    }
}
