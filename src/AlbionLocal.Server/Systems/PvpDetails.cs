using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Healing sickness and resilience (stageP league-pvp SPEC §2.2, §2.3; analysis/work/stageP/league-pvp/SPEC.md), for players and
/// bots alike (a bot counts as a player, as on the client's side), never for mobs. Touched only under <see cref="World.Sync"/>.
/// <list type="bullet">
/// <item><b>Healing sickness</b> (Pvp:HealingSickness; gamedata &lt;PlayerHealingSickness lookbackseconds="10"&gt;): every heal of a player
/// by another player or bot (a spell heal, a heal over time's tick, an area's; not its own heals, potions, food or regeneration [infer:
/// "healed by too many sources"]) notes the healer. With n ≥ 2 distinct healers in the last 10 s the healed player gets HEALINGSICKNESS_n
/// (the row of the highest count not above n) as a spell of its own, 8 s, replacing a lower one (a higher one stays until it runs out),
/// before this heal is worked out: its buffovertime healingsicknessmodifier (0.353 for 2 … 0.782 for 20) cuts every heal it receives to
/// × (1 − Σ) on top of the healmodifier sum (<see cref="SpellEffects.HealFactor"/>). The client shows it from ActiveSpellEffectsUpdate as
/// any debuff (its uitype="debuff" icon and @SPELLS_HEALINGSICKNESS_2 text).</item>
/// <item><b>Resilience</b> (Pvp:Resilience, "focus fire protection"; gamedata &lt;PlayerFocusFire lookbackseconds="10"
/// effectdurationinsec="5"&gt;): every damage hit of a player or bot on another (<see cref="Pvp.Damage"/>) notes the attacker; with n ≥ 2
/// distinct attackers in the last 10 s the target's resilience is raised to n for 5 s (renewed by every such hit). Each player hit on a
/// target with resilience in force does × (1 − m × (1 − p)): m the row's meleemodifier or rangedmodifier by the attacker's weapon
/// (mountedmodifier on a mounted target: the hit on its mount bar), p the attacker's focusfireprotectionpenetration (its buffs, the
/// PASSIVE_TRAIT_RESILIENCE_PENETRATION trait, its worn items) [infer: how penetration combines is not in the data]. The client has no spell
/// for it and no reference to its status text was found (SPEC §2.3): the reduction only, no icon [infer: the display needs capture R1].</item>
/// </list>
/// </summary>
public static class PvpDetails
{
    /// <summary>The spell category of the HEALINGSICKNESS_n buffs.</summary>
    public const string SicknessCategory = "healingsickness";

    public static PvpDetailSettings Settings(World world) => world.GameData?.PvpDetails ?? PvpDetailSettings.Default;

    /// <summary>
    /// <paramref name="healer"/> is about to heal <paramref name="target"/> at <paramref name="ticks"/> (<see cref="SpellCasts"/>' heal path, before
    /// the amount is worked out): the healer noted and, with 2 or more distinct healers in the lookback, the target's HEALINGSICKNESS_n.
    /// Nothing for its own heals, a knocked-down target or under Pvp:HealingSickness false.
    /// </summary>
    public static void Healed(Player healer, Player target, long ticks)
    {
        var world = target.World;
        if (healer == target || !world.Config.PvpHealingSickness || target.Combat.Down is not null || target.Map is null) return;
        var s = Settings(world);
        var healers = target.Combat.Healers;
        var from = ticks - SpellCasts.Seconds(s.SicknessLookbackSeconds);
        foreach (var old in healers.Where(h => h.Value < from).Select(h => h.Key).ToList()) healers.Remove(old);
        healers[healer.Id] = ticks;
        if (s.SicknessBuff(healers.Count) is not { } buff || world.Spells?.Template(buff) is not { } spell) return;
        var level = Level(s, spell.Name);
        var now = world.GameNow;
        var held = target.Effects.Spells.Where(x => x.Spell.Category == SicknessCategory && x.Live(now) && s.Sickness.Any(r => r.Buff == x.Spell.Name)).ToList();
        if (held.Any(x => Level(s, x.Spell.Name) > level)) return; // a higher one stays until it runs out
        if (held.Count > 0) SpellEffects.Remove(world, target, x => held.Contains(x));
        Pvp.ApplySpell(target, buff, ticks);
        Log.Debug("combat", $"'{target.Character.Name}' healed by {healers.Count} sources: {buff}");
    }

    private static int Level(PvpDetailSettings s, string buff) => s.Sickness.FirstOrDefault(r => r.Buff == buff).Healers;

    /// <summary>
    /// The resilience factor on <paramref name="attacker"/>'s hit on <paramref name="target"/> at <paramref name="ticks"/> (<see cref="Pvp.Damage"/>,
    /// after its PvP checks): the attacker noted, the resilience raised with 2 or more distinct attackers in the lookback, then
    /// 1 − m × (1 − penetration) while it is in force; 1 otherwise and under Pvp:Resilience false.
    /// </summary>
    public static double Resilience(Player attacker, Player target, long ticks)
    {
        var world = target.World;
        if (attacker == target || !world.Config.PvpResilience) return 1;
        var s = Settings(world);
        var c = target.Combat;
        var from = ticks - SpellCasts.Seconds(s.FocusFireLookbackSeconds);
        foreach (var old in c.FocusAttackers.Where(h => h.Value < from).Select(h => h.Key).ToList()) c.FocusAttackers.Remove(old);
        c.FocusAttackers[attacker.Id] = ticks;
        if (c.FocusAttackers.Count >= 2) c.FocusFire = (c.FocusAttackers.Count, ticks + SpellCasts.Seconds(s.FocusFireSeconds));
        if (c.FocusFire.Until <= ticks || s.FocusFireRowOf(c.FocusFire.Level) is not { } row) return 1;
        var m = target.IsMounted ? row.Mounted : Combat.WeaponOf(attacker).Type == "melee" ? row.Melee : row.Ranged;
        var penetration = Math.Clamp((attacker.Effects.Idle ? 0 : attacker.Effects.Sum("focusfireprotectionpenetration", world.GameNow))
            + Combat.Worn(attacker, "focusfireprotectionpenetration"), 0, 1);
        return Math.Max(0, 1 - m * (1 - penetration));
    }
}
