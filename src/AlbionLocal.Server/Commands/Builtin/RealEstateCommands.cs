using System.Numerics;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The city plots (public issue #18, README "City plots"): .plot list, .plot auction start|end, .plot free.</summary>
public static class RealEstateCommands
{
    private static RealEstates Plots(CommandContext ctx) =>
        ctx.World.RealEstates.Enabled ? ctx.World.RealEstates : throw new CommandError("real estate is off on this server (RealEstate:Enabled)");

    /// <summary>.plot list: the plots of the player's city, the owned ones with owner, price, bids and buildings, and the auction's times.</summary>
    [Command("plot list", Level = AccountLevel.Player, Help = "the plots of this city: owners, prices, open bids, the auction's times")]
    public static void List(CommandContext ctx)
    {
        var plots = Plots(ctx);
        var map = ctx.RequirePlayer().Map ?? throw new CommandError("the player is on no map");
        ctx.Reply(string.Join("\n", plots.Describe(map)));
    }

    /// <summary>.plot auction start|end: the auction of every owned plot starts now, or ends now and is settled (the schedule is shifted in memory).</summary>
    [Command("plot auction", Level = AccountLevel.GameMaster, Help = "start: the plot auction begins now; end: it ends now and every plot's bids are settled")]
    public static void Auction(CommandContext ctx, string what)
    {
        var plots = Plots(ctx);
        ctx.Reply(what switch
        {
            "start" => plots.StartAuctionNow(),
            "end" => plots.EndAuctionNow(),
            _ => throw new CommandError("usage: .plot auction start|end"),
        });
    }

    /// <summary>.plot free: the nearest plot within 30 m becomes free (its buildings come down, open bids come back).</summary>
    [Command("plot free", Level = AccountLevel.GameMaster, Help = "the nearest owned plot within 30 m becomes free (buildings down, bids back)")]
    public static void Free(CommandContext ctx)
    {
        var plots = Plots(ctx);
        var player = ctx.RequirePlayer();
        var at = player.PositionAt(DateTime.UtcNow);
        var o = player.Map?.Objects.Values.OfType<RealEstateObject>().Where(o => o.Plot is not null && Vector2.Distance(o.Position, at) <= 30)
            .MinBy(o => Vector2.Distance(o.Position, at)) ?? throw new CommandError("no owned plot within 30 m");
        ctx.Reply(plots.ForceFree(o.Plot!));
    }
}
