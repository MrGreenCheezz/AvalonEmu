using System.Globalization;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The temporary world rate modifiers (config section Rates): .rates.</summary>
public static class RatesCommands
{
    /// <summary>
    /// Shows the active rates, or (Admin) changes them for future calculations until restart.
    /// Already scheduled respawns and regrowth keep their time. Reset restores the startup configuration.
    /// The legacy save argument is accepted by the parser only to return a useful error without changing state.
    /// </summary>
    [Command("rates", Level = AccountLevel.GameMaster, Help = "shows the rates (fame, harvest, silver, loot, respawn, …); Admin: .rates <name> <value>, .rates reset; changes last until restart")]
    public static void Show(CommandContext ctx, string? name = null, string? value = null, string? save = null)
    {
        var world = ctx.World;
        if (name is null)
        {
            ctx.Reply($"rates: {world.Rates.Describe()}");
            return;
        }
        if (ctx.Level < AccountLevel.Admin) throw new CommandError("changing the rates needs Admin");
        var reset = name.Equals("reset", StringComparison.OrdinalIgnoreCase);
        if (save is not null || reset && value is not null)
            throw new CommandError("rates cannot be saved by command; edit world.jsonc for permanent changes. Use .rates <name> <value> or .rates reset for temporary changes.");
        lock (world.Sync)
        {
            Rates rates;
            if (reset) rates = world.Config.Rates;
            else
            {
                if (Rates.Canonical(name) is not { } key) throw new CommandError($"'{name}' is not a rate ({string.Join(", ", Rates.Names)})");
                if (value is null) throw new CommandError($".rates {key} <value>; now {world.Rates.Get(key)!.Value.ToString("0.####", CultureInfo.InvariantCulture)}");
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) throw new CommandError($"value: '{value}' is not a number");
                try { rates = world.Rates.With(key, v); }
                catch (ArgumentException e) { throw new CommandError(e.Message); }
            }
            try { world.ApplyRates(rates); }
            catch (ArgumentException e) { throw new CommandError(e.Message); }
            ctx.Reply($"rates: {rates.Describe()}; temporary until server restart");
        }
    }
}