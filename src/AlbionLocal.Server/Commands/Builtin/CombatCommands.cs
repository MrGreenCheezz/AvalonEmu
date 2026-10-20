using System.Globalization;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The player's own bars (stage 5e, analysis/work/stage5/SPEC.md §5e, §6): .damage, .kill and .heal on the player itself.</summary>
public static class CombatCommands
{
    /// <summary>
    /// <paramref name="amount"/> damage on the player (a HealthUpdate caused by itself, no combat); at 0 it is knocked down
    /// as by a mob (<see cref="Knockdown"/>) and gets up 18 s later.
    /// </summary>
    [Command("damage", Level = AccountLevel.GameMaster, Help = "takes <amount> health from the player; at 0 it is knocked down")]
    public static void Damage(CommandContext ctx, float amount)
    {
        var player = ctx.RequirePlayer();
        if (amount <= 0) throw new CommandError($"amount: {amount} is not above 0");
        if (player.Map is null) throw new CommandError("the player is not in the world");
        if (player.Combat.Down is not null) throw new CommandError("the player is knocked down already (.heal gets it up)");
        Knockdown.DamageSelf(player, amount);
        var left = player.Health.At(ctx.World.GameNow);
        ctx.Reply(player.Combat.Down is not null
            ? "knocked down; it gets up 18 s after the fight around it has ended"
            : string.Create(CultureInfo.InvariantCulture, $"health {left:0} / {player.Health.Max:0}"));
    }

    /// <summary>Knocks the player down at once (its whole health as damage caused by itself).</summary>
    [Command("kill", Level = AccountLevel.GameMaster, Help = "knocks the player down (PvE knockdown, the get-up follows by itself)")]
    public static void Kill(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is null) throw new CommandError("the player is not in the world");
        if (player.Combat.Down is not null) throw new CommandError("the player is knocked down already");
        Knockdown.DamageSelf(player, player.Health.At(ctx.World.GameNow));
        ctx.Reply("knocked down; it gets up 18 s after the fight around it has ended (.heal: at once)");
    }

    /// <summary>
    /// The player's health (and with <paramref name="energy"/> its energy) at <paramref name="percent"/> % of the maximum (the checker's setups,
    /// content/checks.json). Lower than now: a HealthUpdate caused by itself as .damage sends; higher: the bars set and
    /// RegenerationPlayerComboChanged to it and its observers (as .heal). 0 knocks it down as a mob would (<see cref="Knockdown.DamageSelf"/>:
    /// with a party or guild mate or a companion on the map the bleed-out of Pve:PartyRevive starts).
    /// </summary>
    [Command("hp", Level = AccountLevel.GameMaster, Help = "sets your health (and energy) to <percent> % of the maximum; 0 knocks you down as a mob would")]
    public static void Hp(CommandContext ctx, float percent, float? energy = null)
    {
        var player = ctx.RequirePlayer();
        if (percent is < 0 or > 100) throw new CommandError($"percent: {percent} is not 0..100");
        if (energy is < 0 or > 100) throw new CommandError($"energy: {energy} is not 0..100");
        if (player.Map is null) throw new CommandError("the player is not in the world");
        if (player.Combat.Down is not null) throw new CommandError("the player is knocked down (.heal gets it up first)");
        var now = Math.Max(ctx.World.GameNow, Math.Max(player.Health.Stamp, player.Energy.Stamp));
        if (energy is { } e) player.Energy.Set(player.Energy.Max * e / 100, now);
        var target = percent <= 0 ? 0 : Math.Max(1, Combat.Rounded(player.Health.Max * percent / 100));
        var current = player.Health.At(now);
        if (target <= 0) Knockdown.DamageSelf(player, player.Health.Max + current); // lethal whatever the regeneration did meanwhile
        else if (target < current) Knockdown.DamageSelf(player, current - target);
        if (player.Combat.Down is null && (target > current || energy is not null))
        {
            if (target > current) player.Health.Set(target, now);
            var combo = Combat.PlayerCombo(player, now);
            player.Session.Send(combo);
            player.SendToObservers(combo);
        }
        ctx.Reply(player.Combat.Down is not null
            ? "knocked down; it gets up 18 s after the fight around it has ended (.heal: at once)"
            : string.Create(CultureInfo.InvariantCulture, $"health {player.Health.At(ctx.World.GameNow):0} / {player.Health.Max:0}, energy {player.Energy.At(ctx.World.GameNow):0} / {player.Energy.Max:0}"));
    }

    /// <summary>
    /// The player's defence as the server uses it (stageJ armour SPEC §5.3-§5.4): the worn items' armour, magic and crowd
    /// control resistance and defence vs mobs / players, and with the buffs the share of a mob's physical / magic hit that lands
    /// (the character window's "% mitigated" is 100 − that share, before the defence vs mobs).
    /// </summary>
    [Command("defense", Level = AccountLevel.Player, Help = "your armour, magic and crowd control resistance and defence from the worn items, and the share of a mob's hit that lands")]
    public static void Defense(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var d = player.Defense;
        var now = ctx.World.GameNow;
        var taken = SpellEffects.TakenFactor(player, fromPlayer: false, now, d.DefenseVsMobs);
        var (physical, magic) = (Combat.ResistanceOf(player, EffectType.Physical) * taken * 100, Combat.ResistanceOf(player, EffectType.Magic) * taken * 100);
        var mounted = player.IsMounted ? " (mounted: the mount's resistance)" : "";
        ctx.Reply(string.Create(CultureInfo.InvariantCulture,
            $"items: armour {d.Armor:0.##}, magic resistance {d.MagicResistance:0.##}, crowd control resistance {d.Ccr:0.##}, defence vs mobs {d.DefenseVsMobs * 100:0.##} %, vs players {d.DefenseVsPlayers * 100:0.##} %; a mob's hit lands at {physical:0.#} % physical, {magic:0.#} % magic{mounted}"));
    }

    /// <summary>Full health and energy; a knocked-down player gets up at once (Respawn in place).</summary>
    [Command("heal", Level = AccountLevel.GameMaster, Help = "full health and energy; a knocked-down player gets up at once")]
    public static void Heal(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is null) throw new CommandError("the player is not in the world");
        var down = player.Combat.Down is not null;
        Knockdown.Heal(player);
        ctx.Reply(down ? "got up with full bars" : string.Create(CultureInfo.InvariantCulture, $"health {player.Health.Max:0}, energy {player.Energy.Max:0}"));
    }
}
