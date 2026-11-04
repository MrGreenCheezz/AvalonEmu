using AlbionLocal.Modules;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;
using IUnit = AlbionLocal.Modules.IUnit;
using SUnit = AlbionLocal.Server.Entities.IUnit;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// What became of a hit (docs/modules/README.md "What became of a hit"): the damage shields, the hits that did not land and the reactions a hit sets off.
/// </summary>
public sealed partial class ModuleHost
{
    private static string? HitId(SUnit unit) => unit is Mob m ? m.Template.Name : null;

    /// <summary>Combat.OnBeforeAbsorb: what this shield takes of the hit, at most <paramref name="take"/>; below 0 = it lets the hit through.</summary>
    internal double BeforeAbsorb(SUnit unit, ActiveSpell shield, double take, double holds, double damage, SUnit attacker, HitOrigin origin, short spell)
    {
        var id = HitId(unit);
        if (unit is Player { IsBot: true } || !Hooks.BeforeAbsorb.Wants(id)) return take;
        var args = new AbsorbArgs(Wrap(unit), new BuffFacade(this, unit, shield), WrapOrNull(attacker), (DamageOrigin)origin, Types.OfId(spell), take, holds, damage);
        _combatHooks++;
        Hooks.BeforeAbsorb.Raise(id, args);
        _combatHooks--;
        if (args.IsCancelled) return -1;
        return double.IsFinite(args.Amount) ? Math.Clamp(args.Amount, 0, take) : take;
    }

    /// <summary>Combat.OnAfterAbsorb: this shield took <paramref name="taken"/> of the hit, <paramref name="holds"/> is left of it and <paramref name="damage"/> of the hit.</summary>
    internal void AfterAbsorb(SUnit unit, ActiveSpell shield, double taken, double holds, double damage, SUnit attacker, HitOrigin origin, short spell)
    {
        var id = HitId(unit);
        if (unit is Player { IsBot: true } || !Hooks.AfterAbsorb.Wants(id)) return;
        var args = new AbsorbArgs(Wrap(unit), new BuffFacade(this, unit, shield), WrapOrNull(attacker), (DamageOrigin)origin, Types.OfId(spell), taken, holds, damage);
        _combatHooks++;
        Hooks.AfterAbsorb.Raise(id, args);
        _combatHooks--;
    }

    /// <summary>
    /// Combat.OnHitAvoided of a hit about to be tried, when <paramref name="target"/> is protected from it; nothing when it is not. Called ahead of the game's own checks, whose
    /// result it does not change. <paramref name="refused"/>: why the PvP rules drop the hit.
    /// </summary>
    internal void HitAvoided(SUnit attacker, SUnit target, double amount, HitOrigin origin, short spell, long now, string? refused = null)
    {
        var id = HitId(target);
        if (!Hooks.HitAvoided.Wants(id) || target is Player { IsBot: true } or Mob { Attackable: false } || attacker is Player { Combat.Down: not null } || target.Map is null || attacker.Map != target.Map) return;
        ActiveSpell? by = null;
        HitAvoidReason? reason =
            target is Mob { Immune: true } ? HitAvoidReason.Evading
            : (by = EffectOf(target, "invincibility", now)) is not null ? HitAvoidReason.Invincible
            : SpellEffects.NotHittable(target, now) ? HitAvoidReason.NotHittable
            : target is Player { Combat.Untouchable: true } ? HitAvoidReason.KnockedDown
            : refused is not null ? HitAvoidReason.NotAllowed
            : null;
        if (reason is not { } why) return;
        if (why == HitAvoidReason.NotHittable) by = EffectOf(target, "nothittablebyothers", now) ?? EffectOf(target, "nothittable", now);
        RaiseAvoided(Wrap(attacker), Wrap(target), id, why, (DamageOrigin)origin, Types.OfId(spell), amount, target, by, refused ?? "");
    }

    /// <summary>Combat.OnHitAvoided of a spell kept off <paramref name="unit"/>: a mob running home, a spell immunity, or else a module's Spells.OnBeforeEffect.</summary>
    internal void SpellAvoided(SUnit caster, SpellTemplate spell, SUnit unit, long now)
    {
        var id = HitId(unit);
        if (!Hooks.HitAvoided.Wants(id) || unit is Player { IsBot: true } || caster is not (Player or Mob) || unit is not (Player or Mob)) return;
        var by = ImmunityOf(unit, spell, now);
        var reason = unit is Mob { Immune: true } ? HitAvoidReason.Evading : by is not null ? HitAvoidReason.SpellImmunity : HitAvoidReason.Cancelled;
        RaiseAvoided(Wrap(caster), Wrap(unit), id, reason, DamageOrigin.Direct, Types.Of(spell), 0, unit, by, "");
    }

    private void RaiseAvoided(IUnit attacker, IUnit target, string? id, HitAvoidReason reason, DamageOrigin origin, SpellInfo? spell, double amount, SUnit? holder, ActiveSpell? by, string detail)
    {
        if (!Hooks.HitAvoided.Wants(id)) return;
        var args = new HitAvoidedArgs(attacker, target, reason, origin, spell, amount, by is null || holder is null ? null : new BuffFacade(this, holder, by), detail);
        _combatHooks++;
        Hooks.HitAvoided.Raise(id, args);
        _combatHooks--;
    }

    private static ActiveSpell? EffectOf(SUnit unit, string tag, long now)
    {
        if (unit.Effects.Count == 0) return null;
        foreach (var (entry, sub) in unit.Effects.On(now))
            if (sub.Tag == tag) return entry;
        return null;
    }

    /// <summary>The entry whose spellimmunity blocks <paramref name="spell"/> (<see cref="SpellEffects.Immune"/>).</summary>
    private static ActiveSpell? ImmunityOf(SUnit unit, SpellTemplate spell, long now)
    {
        if (unit.Effects.Count == 0) return null;
        foreach (var (entry, sub) in unit.Effects.On(now))
        {
            if (sub.Tag != "spellimmunity") continue;
            var name = (string?)sub.Node.Attribute("spellname");
            if (name is "*" || name == spell.Name) return entry;
            if ((string?)sub.Node.Attribute("category") is { Length: > 0 } category && category == spell.Category) return entry;
        }
        return null;
    }

    /// <summary>Spells.OnAfterHit of a mob's cast, once its effects have run.</summary>
    internal void MobHitDone(Mob mob, SpellTemplate spell, SUnit? target, System.Numerics.Vector2 point, int unitsHit)
    {
        if (!Hooks.AfterHit.Wants(spell.Name)) return;
        Hooks.AfterHit.Raise(spell.Name, new SpellHitArgs(Wrap(mob), Types.Of(spell), 255, null, false, WrapOrNull(target), point.X, point.Y, spell.ProjectileSpeed is not null, unitsHit));
    }

    /// <summary>Spells.OnBeforeTrigger: a hit or a cast sets off <paramref name="spell"/> of <paramref name="effect"/> on <paramref name="owner"/>. True = it does not run.</summary>
    internal bool TriggerBlocked(Player owner, string on, SUnit? other, SpellTemplate effect, SpellTemplate spell, bool passive)
    {
        if (owner.IsBot || !Hooks.BeforeTrigger.Wants(spell.Name)) return false;
        var what = Enum.TryParse<SpellTriggerEvent>(on, out var parsed) ? parsed : SpellTriggerEvent.Other;
        var args = new SpellTriggerArgs(Wrap(owner), what, WrapOrNull(other), Types.Of(effect), Types.Of(spell), passive);
        _combatHooks++;
        Hooks.BeforeTrigger.Raise(spell.Name, args);
        _combatHooks--;
        return args.IsCancelled;
    }
}
