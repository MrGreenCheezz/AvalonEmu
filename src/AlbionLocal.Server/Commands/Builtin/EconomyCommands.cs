using System.Globalization;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>Economy commands (stage 6): .money.</summary>
public static class EconomyCommands
{
    /// <summary>
    /// Shows the player's silver and gold, or sets them: a number sets the value, one with + or - adds or takes (whole silver
    /// and gold; "-" or nothing for gold keeps it); <c>.money add 5000 [gold]</c> is the same as <c>.money +5000 [+gold]</c>. Then
    /// UpdateMoney (<see cref="Wallet"/>), saved after the change delay.
    /// </summary>
    [Command("money", Level = AccountLevel.GameMaster, Help = "shows the silver and gold, or sets them (5000) or adds (+5000, -200, add 5000); whole units, gold optional")]
    public static void Money(CommandContext ctx, string? silver = null, string? gold = null, string? addGold = null)
    {
        if (silver == "add")
        {
            if (gold is null) throw new CommandError(".money add <silver> [gold]");
            (silver, gold) = (Signed(gold), addGold is null ? null : Signed(addGold));
        }
        else if (addGold is not null) throw new CommandError(".money [silver] [gold], or .money add <silver> [gold]");
        var player = ctx.RequirePlayer();
        var c = player.Character;
        if (silver is not null)
            Wallet.Set(player, Apply(c.Silver, silver, "silver"), gold is null or "-" ? c.Gold : Apply(c.Gold, gold, "gold"));
        ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"'{c.Name}': {c.Silver / (double)Pricing.A7m:#,0.####} silver, {c.Gold / (double)Pricing.A7m:#,0.####} gold"));
    }

    /// <summary>.goldrewards: the gold rewards of play (not official, Gold:Rewards) and where the player stands this day and week (<see cref="GoldRewards"/>).</summary>
    [Command("goldrewards", Level = AccountLevel.Player, Help = "the gold rewards of play: what gives gold and what you got this day and week")]
    public static void GoldRewardsInfo(CommandContext ctx) => ctx.Reply(GoldRewards.Describe(ctx.RequirePlayer()));

    /// <summary>An amount for "add": as it is when it carries a sign already, else with a +.</summary>
    private static string Signed(string text) => text.StartsWith('+') || text.StartsWith('-') ? text : "+" + text;

    /// <summary>The new a7m value: <paramref name="text"/> whole units set, with a leading + or - added to <paramref name="now"/>.</summary>
    private static long Apply(long now, string text, string what)
    {
        var relative = text.StartsWith('+') || text.StartsWith('-');
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var units) || Math.Abs(units) > 1_000_000_000_000m)
            throw new CommandError($"{what}: '{text}' is not a number (5000, +5000, -200)");
        var raw = (long)Math.Round(units * Pricing.A7m);
        var value = relative ? now + raw : raw;
        if (value < 0) throw new CommandError($"{what} would be below 0");
        return value;
    }
}
