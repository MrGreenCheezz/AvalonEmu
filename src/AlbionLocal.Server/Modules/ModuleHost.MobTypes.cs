using AlbionLocal.Modules;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;
using SUnit = AlbionLocal.Server.Entities.IUnit;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// The modules' own mob types and what mobs' casts do for scripted spells (docs/modules/README.md "A boss of your own"). A type is a copy of a mobs.xml one under another name:
/// the hooks and the spawns go by that name, the clients get the base type's id and so its model. It goes with its module, and its living mobs with it.
/// </summary>
public sealed partial class ModuleHost
{
    private readonly Dictionary<string, (ModuleContext Owner, MobTemplate Type)> _mobTypes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="IServer.DefineMob"/>.</summary>
    internal MobType? DefineMob(string name, string basedOn, Action<MobDefinition>? setup)
    {
        var ctx = RequireCurrent();
        var who = "module:" + ctx.Name;
        if (World.Clusters?.Mobs is not { } mobs)
        {
            Log.Warn(who, $"mob type {name}: this world has no mobs.xml");
            return null;
        }
        if (string.IsNullOrWhiteSpace(name) || mobs.Find(name) is not null)
        {
            Log.Warn(who, $"mob type \"{name}\": that name is taken or empty");
            return null;
        }
        if (mobs.Find(basedOn) is not { } t)
        {
            var like = mobs.Like(basedOn).ToList();
            Log.Warn(who, $"mob type {name}: there is no mob \"{basedOn}\"{(like.Count == 0 ? "" : $" (did you mean {string.Join(", ", like)}?)")}");
            return null;
        }
        var d = new MobDefinition();
        setup?.Invoke(d);
        var type = t with
        {
            Name = name,
            BaseName = t.ClientName,
            Title = d.Title ?? t.Title,
            NameLocaTag = t.NameLocaTag ?? t.ClientName, // a knockdown's text names the base type
            HitPointsMax = d.MaxHealth ?? t.HitPointsMax,
            HitPointsRegeneration = d.HealthRegen ?? t.HitPointsRegeneration,
            EnergyMax = d.MaxEnergy ?? t.EnergyMax,
            EnergyRegeneration = d.EnergyRegen ?? t.EnergyRegeneration,
            AttackDamage = d.AttackDamage ?? t.AttackDamage,
            AttackSpeed = d.AttackSpeed ?? t.AttackSpeed,
            AttackRange = d.AttackRange ?? t.AttackRange,
            AbilityPower = d.AbilityPower ?? t.AbilityPower,
            MoveSpeed = d.WalkSpeed ?? t.MoveSpeed,
            AttackMoveSpeed = d.RunSpeed ?? t.AttackMoveSpeed,
            Armor = d.Armor ?? t.Armor,
            MagicResistance = d.MagicResist ?? t.MagicResistance,
            CrowdControlResistance = d.CrowdControlResist ?? t.CrowdControlResistance,
            ImmuneToForcedMovement = d.ImmuneToForcedMovement ?? t.ImmuneToForcedMovement,
            AggroRadius = d.AggroRadius ?? t.AggroRadius,
            PursuitRadius = d.PursuitRadius ?? t.PursuitRadius,
            AlertRadius = d.AlertRadius ?? t.AlertRadius,
            KeepPositionOnReset = d.KeepsPositionOnReset ?? t.KeepPositionOnReset,
            DontResetAfterPursuit = d.KeepsHealthOnReset ?? t.DontResetAfterPursuit,
            Fame = d.Fame ?? t.Fame,
            SilverModifier = d.SilverFactor ?? t.SilverModifier,
            AI = d.NoRotation ? null : t.AI,
            Loot = d.NoLoot ? null : t.Loot,
            DeathSpells = d.NoDeathSpells ? [] : t.DeathSpells,
        };
        mobs.Define(type);
        _mobTypes[name] = (ctx, type);
        return Types.Of(type);
    }

    /// <summary>At <paramref name="ctx"/>'s drop: its types go, and at the next tick their mobs (the drop may come from inside a walk over the map's objects).</summary>
    private void DropMobTypes(ModuleContext ctx)
    {
        var types = _mobTypes.Where(t => t.Value.Owner == ctx).ToList();
        if (types.Count == 0) return;
        foreach (var (name, _) in types)
        {
            _mobTypes.Remove(name);
            World.Clusters?.Mobs.Undefine(name);
        }
        World.Post(() =>
        {
            foreach (var map in World.Maps.Maps.ToList())
                foreach (var mob in map.Objects.Values.OfType<Mob>().Where(m => !m.Removed && types.Any(t => ReferenceEquals(t.Value.Type, m.Template))).ToList())
                    SpawnManager.Despawn(mob, World, respawn: false);
        });
    }

    private CustomSpellContext ScriptContext(Mob mob, SpellTemplate spell, SUnit? target, System.Numerics.Vector2 point, double ap, int tick) =>
        new(Wrap(mob), Types.Of(spell), 255, null, WrapOrNull(target), point.X, point.Y, ap, tick, Game);

    /// <summary>A mob's cast's hit lands: OnBeforeHit, the scripted spell's OnHit. Returns whether the spells.xml effects run.</summary>
    internal bool MobBeforeHit(Mob mob, SpellTemplate spell, SUnit? target, System.Numerics.Vector2 point, double ap)
    {
        var run = true;
        if (Hooks.BeforeHit.Wants(spell.Name))
        {
            var args = new SpellHitArgs(Wrap(mob), Types.Of(spell), 255, null, false, WrapOrNull(target), point.X, point.Y, spell.ProjectileSpeed is not null);
            Hooks.BeforeHit.Raise(spell.Name, args);
            run = !args.IsCancelled;
        }
        if (Script(spell) is { } s)
        {
            if (s.Script.OnHit is { } onHit) Guard(s.Owner, $"scripted {spell.Name}.OnHit", onHit, ScriptContext(mob, spell, target, point, ap, 0));
            if (s.Script.ReplaceEffects) run = false;
        }
        return run;
    }

    /// <summary>A channel tick of a mob's cast: OnTick, the scripted spell's OnTick. Returns whether the channel spell's effects run.</summary>
    internal bool MobChannelTick(Mob mob, SpellTemplate spell, SpellTemplate channel, int tick, int count, SUnit? target, System.Numerics.Vector2 point, double ap)
    {
        if (Hooks.Tick.Wants(spell.Name)) Hooks.Tick.Raise(spell.Name, new SpellTickArgs(Wrap(mob), Types.Of(spell), Types.Of(channel), tick, count, WrapOrNull(target), point.X, point.Y));
        if (Script(spell) is not { } s) return true;
        if (s.Script.OnTick is { } onTick) Guard(s.Owner, $"scripted {spell.Name}.OnTick", onTick, ScriptContext(mob, spell, target, point, ap, tick));
        return !s.Script.ReplaceEffects;
    }

    /// <summary>Mobs.OnBeforeLeaveCombat: true = it stays in the fight. A handler's actions on the mob wait for the next tick, as in a hit.</summary>
    internal bool LeaveCombatBlocked(Mob m, string reason)
    {
        if (!Hooks.BeforeLeaveCombat.Wants(m.Template.Name)) return false;
        var args = new BeforeLeaveCombatArgs(Wrap(m), ModuleTypes.LeftCombat(reason), reason);
        _combatHooks++;
        Hooks.BeforeLeaveCombat.Raise(m.Template.Name, args);
        _combatHooks--;
        return args.IsCancelled;
    }
}
