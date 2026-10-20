using System.Numerics;
using System.Runtime.CompilerServices;
using AlbionLocal.Photon;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;
using IUnit = AlbionLocal.Plugin.Api.IUnit;
using SUnit = AlbionLocal.Server.Entities.IUnit;

namespace AlbionLocal.Server.Plugins;

/// <summary>
/// The v2 hooks' side of the host (server/plugins/README.md "Hooks", analysis/work/stageN/plugins/SPEC.md §v2): the dispatch methods the
/// game's systems call (each one cheap when nobody listens: <see cref="HookPoint{T}.Wants"/> first), the scripted spells, the per-entity
/// data bags and timers, the OnReload replay and what an unload takes back (hooks, scripts, modifiers, areas, passive sets).
/// </summary>
public sealed partial class PluginHost
{
    // ---- load and unload

    /// <summary>After <paramref name="ctx"/>'s Load: its OnReload for every joined player and living mob; the passive sets anew when it adds to them.</summary>
    private void AfterLoad(PluginContext ctx)
    {
        if (Hooks.Passives.Has(ctx)) InvalidatePassives();
        if (!Hooks.Reload.Has(ctx)) return;
        foreach (var map in World.Maps.Maps.ToList())
        {
            foreach (var p in map.Players.Values.Where(p => !p.IsBot).ToList()) Hooks.Reload.RaiseFor(ctx, null, new UnitArgs(Wrap(p)));
            foreach (var m in map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && !m.Removed).ToList()) Hooks.Reload.RaiseFor(ctx, m.Template.Name, new UnitArgs(Wrap(m)));
        }
    }

    /// <summary>At <paramref name="ctx"/>'s drop: its hooks, scripts and stat modifiers go; the passive sets are worked out anew without its additions.</summary>
    private void AfterDrop(PluginContext ctx)
    {
        var passives = Hooks.Passives.Has(ctx);
        Hooks.RemoveAll(ctx);
        foreach (var key in _scripts.Where(s => s.Value.Owner == ctx).Select(s => s.Key).ToList()) _scripts.Remove(key);
        foreach (var map in World.Maps.Maps.ToList())
            foreach (var unit in map.Objects.Values.OfType<Mob>().Cast<SUnit>().Concat(map.Characters).Distinct().ToList())
                if (unit.Effects.ClearModifiers(ctx.Name, null, null)) SpellEffects.Refresh(World, unit);
        foreach (var area in ctx.Areas.ToList()) area.Dispose();
        if (passives) InvalidatePassives();
    }

    /// <summary>An OnCalcPassives registration: the online players' passive sets are worked out again with it.</summary>
    internal IHookRegistration PassivesChanged(IHookRegistration registration)
    {
        InvalidatePassives();
        return registration;
    }

    private void InvalidatePassives()
    {
        foreach (var map in World.Maps.Maps.ToList())
            foreach (var p in map.Players.Values.ToList())
            {
                p.Effects.Passive = null;
                if (p.Map is not null) SpellEffects.Refresh(World, p);
            }
    }

    /// <summary>A server unit as plugins see it.</summary>
    internal IUnit Wrap(SUnit unit) => unit switch
    {
        Player p => Wrap(p),
        Mob m => Wrap(m),
        _ => throw new ArgumentException($"not a player or mob: {unit}"),
    };

    internal IUnit? WrapOrNull(SUnit? unit) => unit is Player or Mob ? Wrap(unit) : null;

    /// <summary>The server unit behind a plugin's <see cref="IUnit"/> (null for a foreign implementation).</summary>
    internal static SUnit? Unwrap(IUnit? unit) => unit switch
    {
        PlayerFacade p => p.Player,
        MobFacade m => m.Mob,
        _ => null,
    };

    /// <summary>The unit with object id <paramref name="id"/> on <paramref name="map"/> (a player or a mob); null when none.</summary>
    internal static SUnit? Find(Map? map, long id) => map is null || id == 0 ? null : (SUnit?)map.FindPlayer(id) ?? map.Objects.GetValueOrDefault(id) as Mob;

    // ---- data bags and entity timers

    internal T GetData<T>(object owner, string key, Func<T> factory)
    {
        var bag = RequireCurrent().Data.GetValue(owner, static _ => new Dictionary<string, object?>(StringComparer.Ordinal));
        if (bag.TryGetValue(key, out var v) && v is T t) return t;
        var made = factory();
        bag[key] = made;
        return made;
    }

    internal void SetData<T>(object owner, string key, T value) =>
        RequireCurrent().Data.GetValue(owner, static _ => new Dictionary<string, object?>(StringComparer.Ordinal))[key] = value;

    internal bool RemoveData(object owner, string key) => RequireCurrent().Data.TryGetValue(owner, out var bag) && bag.Remove(key);

    /// <summary>A timer of the current plugin bound to <paramref name="owner"/> (it stops when <paramref name="alive"/> turns false).</summary>
    internal IUnitTimer AddTimer(object owner, Func<bool> alive, TimeSpan delay, int repeats, Action<IUnitTimer> callback, string? name)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var ctx = RequireCurrent();
        if (delay < TimeSpan.FromMilliseconds(10)) delay = TimeSpan.FromMilliseconds(10);
        var named = ctx.NamedTimers.GetValue(owner, static _ => new Dictionary<string, UnitTimer>(StringComparer.Ordinal));
        if (name is not null && named.Remove(name, out var old)) old.Dispose();
        var timer = new UnitTimer(name, repeats);
        timer.Inner = new PluginTimer(World.Clock.Now + delay, delay, () =>
        {
            if (timer.Cancelled) return;
            if (!alive())
            {
                timer.Dispose();
                return;
            }
            timer.Fired++;
            if (timer.Repeats > 0 && timer.Fired >= timer.Repeats) timer.Dispose();
            callback(timer);
        });
        if (name is not null) named[name] = timer;
        if (!ctx.Dropped) ctx.TimerList.Add(timer.Inner);
        return timer;
    }

    internal bool RemoveTimer(object owner, string name)
    {
        if (!RequireCurrent().NamedTimers.TryGetValue(owner, out var named) || !named.Remove(name, out var t)) return false;
        t.Dispose();
        return true;
    }

    internal sealed class UnitTimer(string? name, int repeats) : IUnitTimer
    {
        public string? Name { get; } = name;
        public int Repeats { get; } = repeats;
        public int Fired { get; set; }
        public PluginTimer? Inner { get; set; }
        public bool Cancelled => Inner?.Cancelled ?? true;
        public void Dispose() => Inner?.Dispose();
    }

    // ---- scripted spells

    private readonly Dictionary<string, (PluginContext Owner, SpellScript Script)> _scripts = new(StringComparer.OrdinalIgnoreCase);

    internal IHookRegistration RegisterScript(PluginContext ctx, string spell, SpellScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        spell = spell.Trim();
        if (_scripts.TryGetValue(spell, out var other) && !other.Owner.Dropped) throw new InvalidOperationException($"spell {spell} is scripted by plugin {other.Owner.Name} already");
        if (World.Spells is { } spells && spells.Template(spell) is null) Log.Warn("plugin:" + ctx.Name, $"scripted spell {spell} is not in spells.xml (yet): the client cannot cast it until a pack adds it");
        if (!ctx.Dropped) _scripts[spell] = (ctx, script);
        return new ScriptRegistration(this, spell, ctx);
    }

    private sealed class ScriptRegistration(PluginHost host, string spell, PluginContext ctx) : IHookRegistration
    {
        public string? Id => spell;

        public void Dispose()
        {
            if (host._scripts.TryGetValue(spell, out var s) && s.Owner == ctx) host._scripts.Remove(spell);
        }
    }

    private (PluginContext Owner, SpellScript Script)? Script(SpellTemplate spell) =>
        _scripts.Count > 0 && _scripts.TryGetValue(spell.Name, out var s) && !s.Owner.Dropped ? s : null;

    /// <summary>Whether spell <paramref name="spell"/> is scripted with its spells.xml effects replaced.</summary>
    internal bool Replaced(SpellTemplate spell) => Script(spell) is { Script.ReplaceEffects: true };

    private SpellScriptContext ScriptContext(Player p, PendingCast cast, SUnit? target, Vector2 point, double ap, int tick) =>
        new(Wrap(p), cast.Spell.Name, cast.HookSlot, cast.Source?.Template.Name, WrapOrNull(target), point.X, point.Y, ap, tick, Game);

    // ---- spells

    /// <summary>
    /// A player's cast that passed the engine's checks (<see cref="SpellCasts.Check"/>): the cast-time and energy formulas, then OnCheckCast.
    /// Returns the cast to start (its end and cost changed), or why a plugin refused it. Bots are left alone.
    /// </summary>
    internal (PendingCast Cast, string? Refusal) CheckCast(Player p, PendingCast cast)
    {
        if (p.IsBot) return (cast, null);
        var name = cast.Spell.Name;
        if (Hooks.CastTime.Wants(name))
        {
            var a = new SpellFormulaArgs(Wrap(p), name, WrapOrNull(cast.Target), (cast.End - cast.Start) / (double)TimeSpan.TicksPerSecond);
            Hooks.CastTime.Raise(name, a);
            if (double.IsFinite(a.Value.Value)) cast = cast with { End = cast.Start + SpellCasts.Seconds(Math.Max(0, a.Value.Value)) };
        }
        if (Hooks.EnergyCost.Wants(name))
        {
            var a = new SpellFormulaArgs(Wrap(p), name, WrapOrNull(cast.Target), cast.EnergyCost);
            Hooks.EnergyCost.Raise(name, a);
            if (double.IsFinite(a.Value.Value)) cast = cast with { EnergyCost = (int)Math.Max(0, Math.Round(a.Value.Value)) };
            if (p.Energy.At(cast.Start) < cast.EnergyCost) return (cast, "not enough energy");
        }
        if (!Hooks.CheckCast.Wants(name)) return (cast, null);
        var args = new SpellCheckCastArgs(Wrap(p), name, cast.HookSlot, cast.Source?.Template.Name, cast.MountSpell, WrapOrNull(cast.Target), cast.Aim.X, cast.Aim.Y);
        Hooks.CheckCast.Raise(name, args);
        return args.Cancelled ? (cast, args.Reason ?? "") : (cast, null);
    }

    /// <summary>The cooldown (seconds) a player's cast starts: OnCalcCooldown.</summary>
    internal double CalcCooldown(Player p, SpellTemplate spell, SUnit? target, double seconds)
    {
        if (p.IsBot || !Hooks.Cooldown.Wants(spell.Name)) return seconds;
        var a = new SpellFormulaArgs(Wrap(p), spell.Name, WrapOrNull(target), seconds);
        Hooks.Cooldown.Raise(spell.Name, a);
        return double.IsFinite(a.Value.Value) ? Math.Max(0, a.Value.Value) : seconds;
    }

    /// <summary>A player's cast went through (<see cref="SpellCasts.Finish"/>): OnCast, a mount's OnMountSpell, the scripted spell's OnCast.</summary>
    internal void CastDone(Player p, PendingCast cast, Vector2 point, double ap)
    {
        if (p.IsBot) return;
        var name = cast.Spell.Name;
        if (Hooks.Cast.Wants(name)) Hooks.Cast.Raise(name, CastArgs(p, cast, point));
        if (cast.MountSpell && cast.Source is { } mount && Hooks.MountSpell.Wants(mount.Template.Name)) Hooks.MountSpell.Raise(mount.Template.Name, CastArgs(p, cast, point));
        if (Script(cast.Spell) is { Script.OnCast: { } onCast } s) Guard(s.Owner, $"scripted {name}.OnCast", onCast, ScriptContext(p, cast, cast.Target, point, ap, 0));
    }

    private SpellCastArgs CastArgs(Player p, PendingCast cast, Vector2 point) =>
        new(Wrap(p), cast.Spell.Name, cast.HookSlot, cast.Source?.Template.Name, cast.MountSpell, WrapOrNull(cast.Target), point.X, point.Y);

    internal void CastCancelled(Player p, PendingCast cast, bool interrupted)
    {
        if (p.IsBot || !Hooks.CastCancel.Wants(cast.Spell.Name)) return;
        Hooks.CastCancel.Raise(cast.Spell.Name, new SpellCancelArgs(Wrap(p), cast.Spell.Name, cast.HookSlot, cast.Source?.Template.Name, cast.MountSpell, WrapOrNull(cast.Target),
            cast.Aim.X, cast.Aim.Y, interrupted));
    }

    internal void MobCastDone(Mob mob, SpellTemplate spell, SUnit? target, Vector2 point)
    {
        if (Hooks.Cast.Wants(spell.Name)) Hooks.Cast.Raise(spell.Name, new SpellCastArgs(Wrap(mob), spell.Name, 255, null, false, WrapOrNull(target), point.X, point.Y));
    }

    internal void MobCastCancelled(Mob mob, SpellTemplate spell, SUnit? target, Vector2 point, bool interrupted)
    {
        if (Hooks.CastCancel.Wants(spell.Name))
            Hooks.CastCancel.Raise(spell.Name, new SpellCancelArgs(Wrap(mob), spell.Name, 255, null, false, WrapOrNull(target), point.X, point.Y, interrupted));
    }

    /// <summary>
    /// A player's cast's hit lands: OnBeforeHit, the scripted spell's OnHit. Returns whether the spells.xml effects run (not when a handler
    /// prevented them or a script replaces them).
    /// </summary>
    internal bool BeforeHit(SpellCasts.SpellContext c, PendingCast cast)
    {
        var p = c.Caster;
        if (p.IsBot) return true;
        var run = true;
        var name = cast.Spell.Name;
        if (Hooks.BeforeHit.Wants(name))
        {
            var args = new SpellHitArgs(Wrap(p), name, cast.HookSlot, cast.Source?.Template.Name, cast.MountSpell, WrapOrNull(c.Target), c.Point.X, c.Point.Y, cast.Spell.ProjectileSpeed is not null);
            Hooks.BeforeHit.Raise(name, args);
            run = !args.DefaultPrevented;
        }
        if (Script(cast.Spell) is { } s)
        {
            if (s.Script.OnHit is { } onHit) Guard(s.Owner, $"scripted {name}.OnHit", onHit, ScriptContext(p, cast, c.Target, c.Point, c.AbilityPower, 0));
            if (s.Script.ReplaceEffects) run = false;
        }
        return run;
    }

    internal void AfterHit(SpellCasts.SpellContext c, PendingCast cast)
    {
        var p = c.Caster;
        if (p.IsBot || !Hooks.AfterHit.Wants(cast.Spell.Name)) return;
        Hooks.AfterHit.Raise(cast.Spell.Name, new SpellHitArgs(Wrap(p), cast.Spell.Name, cast.HookSlot, cast.Source?.Template.Name, cast.MountSpell, WrapOrNull(c.Target),
            c.Point.X, c.Point.Y, cast.Spell.ProjectileSpeed is not null));
    }

    /// <summary>A channel tick (<see cref="SpellCasts"/>.StartChannel): OnTick, the scripted spell's OnTick. Returns whether the channel spell's effects run.</summary>
    internal bool ChannelTick(Player p, PendingCast cast, SpellTemplate channel, int tick, int count, SUnit? target, Vector2 point, double ap)
    {
        if (p.IsBot) return true;
        var name = cast.Spell.Name;
        if (Hooks.Tick.Wants(name)) Hooks.Tick.Raise(name, new SpellTickArgs(Wrap(p), name, channel.Name, tick, count, WrapOrNull(target), point.X, point.Y));
        if (Script(cast.Spell) is not { } s) return true;
        if (s.Script.OnTick is { } onTick) Guard(s.Owner, $"scripted {name}.OnTick", onTick, ScriptContext(p, cast, target, point, ap, tick));
        return !s.Script.ReplaceEffects;
    }

    /// <summary>Spell <paramref name="spell"/> of <paramref name="c"/> is about to act on <paramref name="unit"/>: OnEffect. True = keep it off.</summary>
    internal bool EffectBlocked(SpellCasts.SpellContext c, SpellTemplate spell, SUnit unit)
    {
        if (!Hooks.Effect.Wants(spell.Name) || unit is not (Player or Mob)) return false;
        var args = new SpellEffectArgs(Wrap(c.Caster), spell.Name, c.Root.Name, Wrap(unit), c.FromArea);
        Hooks.Effect.Raise(spell.Name, args);
        return args.DefaultPrevented;
    }

    /// <summary>OnDamageEarly (and OnCalcHeal for a heal): the amount to deal; null = dropped.</summary>
    internal double? DamageEarly(SUnit caster, SpellTemplate spell, SUnit target, double amount, bool heal, bool energy, bool overTime, bool area, bool magic)
    {
        if (heal && !energy && caster is Player hp && Hooks.Heal.Wants(spell.Name))
        {
            var f = new SpellFormulaArgs(Wrap(hp), spell.Name, WrapOrNull(target), amount);
            Hooks.Heal.Raise(spell.Name, f);
            if (double.IsFinite(f.Value.Value)) amount = Math.Max(0, f.Value.Value);
        }
        if (!Hooks.DamageEarly.Wants(spell.Name) || caster is not (Player or Mob) || target is not (Player or Mob)) return amount;
        var args = new SpellDamageArgs(Wrap(caster), spell.Name, Wrap(target), amount, heal, energy, overTime, area, magic);
        Hooks.DamageEarly.Raise(spell.Name, args);
        if (args.DefaultPrevented) return null;
        return double.IsFinite(args.Amount.Value) ? Math.Max(0, args.Amount.Value) : amount;
    }

    /// <summary>Whether OnDamageLate has listeners for <paramref name="spell"/> (the caller then measures the health before and after).</summary>
    internal bool WantsDamageLate(SpellTemplate spell) => Hooks.DamageLate.Wants(spell.Name);

    internal void DamageLate(SUnit caster, SpellTemplate spell, SUnit target, float before, bool heal, bool energy, bool overTime, bool area, bool magic)
    {
        if (caster is not (Player or Mob) || target is not (Player or Mob)) return;
        var now = World.GameNow;
        var after = energy ? EnergyOf(target, now) : HealthOf(target, now);
        if (after == before && !heal) return; // nothing landed (refused, immune, all on a shield)
        var args = new SpellDamageArgs(Wrap(caster), spell.Name, Wrap(target), Math.Abs(after - before), heal, energy, overTime, area, magic) { HealthLeft = HealthOf(target, now) };
        Hooks.DamageLate.Raise(spell.Name, args);
    }

    internal static float HealthOf(SUnit unit, long now) => unit switch { Player p => p.Health.At(now), Mob m => m.Health.At(now), _ => 0 };

    internal static float EnergyOf(SUnit unit, long now) => unit switch { Player p => p.Energy.At(now), Mob m => m.Energy.At(now), _ => 0 };

    internal void AreaCreated(SpellArea area)
    {
        if (!Hooks.AreaCreate.Wants(area.Holder.Name)) return;
        Hooks.AreaCreate.Raise(area.Holder.Name, AreaArgs(area, []));
    }

    internal bool WantsAreaTick(SpellArea area) => Hooks.AreaTick.Wants(area.Holder.Name);

    internal void AreaTicked(SpellArea area, IEnumerable<long> inside)
    {
        var units = new List<IUnit>();
        foreach (var id in inside)
            if (Find(area.Map, id) is { } u) units.Add(Wrap(u));
        Hooks.AreaTick.Raise(area.Holder.Name, AreaArgs(area, units));
    }

    private SpellAreaArgs AreaArgs(SpellArea area, IReadOnlyList<IUnit> inside) =>
        new(Wrap(area.Owner), area.Holder.Name, area.Effect?.Name, area.Id, area.Origin.X, area.Origin.Y, area.Reach,
            area.End > area.Start ? (area.End - area.Start) / (double)TimeSpan.TicksPerSecond : 0, inside);

    // ---- buffs

    /// <summary>OnCheckApply: true = <paramref name="spell"/> stays off <paramref name="unit"/>.</summary>
    internal bool BuffBlocked(SUnit unit, SpellTemplate spell, SUnit? caster)
    {
        if (!Hooks.BuffCheck.Wants(spell.Name) || unit is not (Player or Mob)) return false;
        var args = new BuffCheckArgs(Wrap(unit), spell.Name, WrapOrNull(caster));
        Hooks.BuffCheck.Raise(spell.Name, args);
        return args.Cancelled;
    }

    internal void BuffApplied(SUnit unit, ActiveSpell entry, bool isNew, int before)
    {
        if (unit is not (Player or Mob)) return;
        var name = entry.Spell.Name;
        if (Hooks.BuffApply.Wants(name)) Hooks.BuffApply.Raise(name, new BuffArgs(Wrap(unit), new BuffFacade(this, unit, entry), isNew));
        if (!isNew && before != entry.Stacks && Hooks.BuffStack.Wants(name)) Hooks.BuffStack.Raise(name, new BuffStackArgs(Wrap(unit), new BuffFacade(this, unit, entry), before));
    }

    internal void BuffStacks(SUnit unit, ActiveSpell entry, int before)
    {
        if (unit is (Player or Mob) && Hooks.BuffStack.Wants(entry.Spell.Name))
            Hooks.BuffStack.Raise(entry.Spell.Name, new BuffStackArgs(Wrap(unit), new BuffFacade(this, unit, entry), before));
    }

    internal void BuffTicked(SUnit unit, ActiveSpell? entry)
    {
        if (entry is null || unit is not (Player or Mob) || !Hooks.BuffTick.Wants(entry.Spell.Name)) return;
        Hooks.BuffTick.Raise(entry.Spell.Name, new BuffArgs(Wrap(unit), new BuffFacade(this, unit, entry), false));
    }

    /// <summary>The entries that left <paramref name="owner"/>'s list (<see cref="EffectList"/>): OnRemove for each.</summary>
    internal static void BuffsEnded(SUnit owner, IReadOnlyList<ActiveSpell> gone, BuffEnd how)
    {
        if (gone.Count == 0 || HostOf(owner) is not { } host || !host.Hooks.BuffRemove.Any) return;
        foreach (var s in gone)
            if (host.Hooks.BuffRemove.Wants(s.Spell.Name))
                host.Hooks.BuffRemove.Raise(s.Spell.Name, new BuffRemoveArgs(host.Wrap(owner), new BuffFacade(host, owner, s), how));
    }

    private static PluginHost? HostOf(SUnit unit) => unit switch { Player p => p.World.Plugins, Mob { World: { } w } => w.Plugins, _ => null };

    internal bool WantsBuffRemove => Hooks.BuffRemove.Any;

    // ---- items

    /// <summary>An item went into (or out of) an equipment slot (<see cref="Scripting.ScriptRegistry.OnEquip"/>).</summary>
    internal void Equipped(Player p, Item item, bool equip, bool login)
    {
        var point = equip ? Hooks.Equip : Hooks.Unequip;
        var name = BaseName(item.Template.Name);
        if (p.IsBot || !point.Wants(name)) return;
        var e = p.Inventory.Equipment;
        var slot = -1;
        for (var s = 0; s < e.Size; s++)
            if (e[s] == item) slot = s;
        point.Raise(name, new EquipArgs(Wrap(p), name, item.Quality, slot, login));
    }

    /// <summary>The main hand after a stats change (<see cref="Player.RecalculateStats"/>): OnWeaponSwap when another weapon is in it.</summary>
    internal void MainHand(Player p)
    {
        var now = p.Inventory.Equipment[0];
        var id = now?.Id ?? 0;
        var (before, from) = (p.PluginMainHand, p.PluginMainHandName);
        var to = now is null ? null : BaseName(now.Template.Name);
        (p.PluginMainHand, p.PluginMainHandName) = (id, to);
        if (before == -1 || before == id || p.IsBot || !Hooks.WeaponSwap.Wants(to ?? "")) return;
        Hooks.WeaponSwap.Raise(to ?? "", new WeaponSwapArgs(Wrap(p), from, to));
    }

    /// <summary>A slot of <paramref name="p"/>'s equipment holds another spell (<see cref="SlotSpells"/>): Spells.OnSlotSpellChanged (id = the new spell).</summary>
    internal void SlotSpellChanged(Player p, SpellSlot slot, Item? item, string? from, string? to, SlotChangeSource source, bool temporary)
    {
        if (p.IsBot || !Hooks.SlotChanged.Wants(to ?? "")) return;
        Hooks.SlotChanged.Raise(to ?? "", new SlotSpellChangedArgs(Wrap(p), slot, item?.Template.Name, from, to, source, temporary));
    }

    /// <summary>An extra slot of <paramref name="p"/> holds another spell (<see cref="ExtraBars"/>): Spells.OnExtraBarChanged (id = the new spell, "" = emptied).</summary>
    internal void ExtraBarChanged(Player p, int index, string? from, string? to, string? item, ExtraBarChangeSource source)
    {
        if (p.IsBot || !Hooks.ExtraBarChanged.Wants(to ?? "")) return;
        Hooks.ExtraBarChanged.Raise(to ?? "", new ExtraBarChangedArgs(Wrap(p), index, from, to, item, source));
    }

    internal void Broke(Player p, Item item, string cause)
    {
        var name = BaseName(item.Template.Name);
        if (!p.IsBot && Hooks.Break.Wants(name)) Hooks.Break.Raise(name, new ItemBreakArgs(Wrap(p), name, cause));
    }

    /// <summary>The worn passives were worked out (<see cref="Passives"/>): OnCalcPassives adds to <paramref name="buffs"/>.</summary>
    internal void CalcPassives(Player p, Dictionary<string, double> buffs)
    {
        if (p.IsBot || !Hooks.Passives.Any) return;
        var worn = new List<string>();
        foreach (var it in p.Inventory.Equipment.Items) worn.Add(BaseName(it.Template.Name));
        void Add(string attribute, double value)
        {
            if (!double.IsFinite(value) || string.IsNullOrEmpty(attribute)) return;
            buffs[attribute] = buffs.GetValueOrDefault(attribute) + value;
        }
        var args = new PassiveArgs(Wrap(p), worn, Add);
        Hooks.Passives.Raise(null, args);
        foreach (var item in worn.Distinct())
            if (Hooks.Passives.Wants(item))
            {
                args.Item = item;
                Hooks.Passives.RaiseId(item, args);
            }
    }

    /// <summary>Whether the passive sets must include every worn item (an OnCalcPassives listens).</summary>
    internal bool WantsPassives => Hooks.Passives.Any;

    internal static string BaseName(string name) => name.IndexOf('@') is var at and > 0 ? name[..at] : name;

    // ---- mounts

    internal string? MountCheck(Player p, Item item, bool remount)
    {
        var name = item.Template.Name;
        if (p.IsBot || !Hooks.MountCheck.Wants(name)) return null;
        var args = new MountCheckArgs(Wrap(p), name, remount);
        Hooks.MountCheck.Raise(name, args);
        return args.Cancelled ? args.Reason ?? "refused by a plugin" : null;
    }

    internal void MountEvent(HookPoint<MountArgs> point, Player p, ItemTemplate? mount, string reason)
    {
        var name = mount?.Name ?? "";
        if (!p.IsBot && point.Wants(name)) point.Raise(name, new MountArgs(Wrap(p), name, reason));
    }

    /// <summary>Mounted: OnMounted; returns the gallop buff to use (its name; null = none), <paramref name="gallop"/> when nobody changed it.</summary>
    internal string? Mounted(Player p, ItemTemplate mount, string? gallop)
    {
        if (p.IsBot || !Hooks.Mounted.Wants(mount.Name)) return gallop;
        var args = new MountedArgs(Wrap(p), mount.Name, gallop);
        Hooks.Mounted.Raise(mount.Name, args);
        return args.GallopBuff.Value;
    }

    internal double MountDamage(Player p, ItemTemplate? mount, long attacker, double damage)
    {
        var name = mount?.Name ?? "";
        if (p.IsBot || !Hooks.MountDamage.Wants(name)) return damage;
        var args = new MountDamageArgs(Wrap(p), name, damage, WrapOrNull(Find(p.Map, attacker)));
        Hooks.MountDamage.Raise(name, args);
        return double.IsFinite(args.Amount.Value) ? Math.Max(0, args.Amount.Value) : damage;
    }

    // ---- units

    internal void HealthChanged(SUnit unit, float before, float after, long source, short spell)
    {
        if (before == after || unit is Player { IsBot: true }) return;
        var id = unit is Mob m ? m.Template.Name : null;
        if (!Hooks.Health.Wants(id)) return;
        var max = unit switch { Player p => p.Health.Max, Mob mob => mob.Health.Max, _ => 0 };
        Hooks.Health.Raise(id, new UnitValueArgs(Wrap(unit), before, after, max, WrapOrNull(Find(unit.Map, source)), spell >= 0 ? World.Spells?.Name(spell) : null));
    }

    internal void EnergyChanged(SUnit unit, float before, float after, long source, short spell)
    {
        if (before == after || unit is Player { IsBot: true }) return;
        var id = unit is Mob m ? m.Template.Name : null;
        if (!Hooks.Energy.Wants(id)) return;
        var max = unit switch { Player p => p.Energy.Max, Mob mob => mob.Energy.Max, _ => 0 };
        Hooks.Energy.Raise(id, new UnitValueArgs(Wrap(unit), before, after, max, WrapOrNull(Find(unit.Map, source)), spell >= 0 ? World.Spells?.Name(spell) : null));
    }

    internal void UnitEvent(HookPoint<UnitArgs> point, Player p)
    {
        if (!p.IsBot && point.Any) point.Raise(null, new UnitArgs(Wrap(p)));
    }

    // ---- formulas

    internal double Threat(Player p, Mob mob, double threat)
    {
        if (!Hooks.Threat.Wants(mob.Template.Name)) return threat;
        var a = new UnitFormulaArgs(Wrap(p), Wrap(mob), threat);
        Hooks.Threat.Raise(mob.Template.Name, a);
        return double.IsFinite(a.Value.Value) ? Math.Max(0, a.Value.Value) : threat;
    }

    /// <summary>OnCalcFame: <paramref name="gain"/> in the server's ×10000 units.</summary>
    internal long Fame(Player p, Mob mob, long gain)
    {
        if (p.IsBot || !Hooks.Fame.Wants(mob.Template.Name)) return gain;
        var a = new UnitFormulaArgs(Wrap(p), Wrap(mob), gain / 10000.0);
        Hooks.Fame.Raise(mob.Template.Name, a);
        return double.IsFinite(a.Value.Value) ? (long)Math.Round(Math.Max(0, a.Value.Value) * 10000) : gain;
    }

    internal int Silver(Player killer, Mob mob, int amount)
    {
        if (!Hooks.Silver.Wants(mob.Template.Name)) return amount;
        var a = new UnitFormulaArgs(Wrap(killer), Wrap(mob), amount);
        Hooks.Silver.Raise(mob.Template.Name, a);
        return double.IsFinite(a.Value.Value) ? (int)Math.Round(Math.Clamp(a.Value.Value, 0, int.MaxValue)) : amount;
    }

    internal double ItemPower(Player p, Item item, double ip)
    {
        if (!Hooks.ItemPower.Any) return ip;
        var name = BaseName(item.Template.Name);
        if (!Hooks.ItemPower.Wants(name)) return ip;
        var a = new ItemFormulaArgs(Wrap(p), name, item.Quality, ip);
        Hooks.ItemPower.Raise(name, a);
        return double.IsFinite(a.Value.Value) ? Math.Max(0, a.Value.Value) : ip;
    }
}

/// <summary>A spell entry on a unit as plugins see it (<see cref="IBuff"/>); its data bag is the calling plugin's, keyed by the entry.</summary>
internal sealed class BuffFacade(PluginHost host, SUnit unit, ActiveSpell entry) : IBuff
{
    public ActiveSpell Entry { get; } = entry;
    public string Spell => Entry.Spell.Name;
    public long CasterId => Entry.Caster;
    public IUnit? Caster => host.WrapOrNull(PluginHost.Find(unit.Map, Entry.Caster));
    public int Stacks => Entry.Stacks;

    public double SecondsLeft
    {
        get
        {
            var now = host.World.GameNow;
            var end = Entry.Subs.Where(s => s.Active).Select(Entry.EndOf).DefaultIfEmpty(now).Max();
            return end == long.MaxValue ? double.PositiveInfinity : Math.Max(0, (end - now) / (double)TimeSpan.TicksPerSecond);
        }
    }

    public T GetData<T>(string key, Func<T> factory) => host.GetData(Entry, key, factory);
    public void SetData<T>(string key, T value) => host.SetData(Entry, key, value);
    public override string ToString() => Entry.ToString();
}
