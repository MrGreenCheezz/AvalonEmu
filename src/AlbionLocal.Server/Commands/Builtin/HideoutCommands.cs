using System.Numerics;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The guild hideouts (analysis/work/stageP/hideouts/SPEC.md): .hideout list, .hideout finish, .hideout place.</summary>
public static class HideoutCommands
{
    private static Hideouts Hideouts(CommandContext ctx) =>
        ctx.World.Hideouts.Enabled ? ctx.World.Hideouts : throw new CommandError("hideouts are off on this server (Hideouts:Enabled)");

    /// <summary>.hideout list [all]: the hideouts of the player's guild (with "all", a GameMaster: every hideout of the server).</summary>
    [Command("hideout list", Level = AccountLevel.Player, Help = "the hideouts of your guild (GameMaster: 'all' for every hideout)")]
    public static void List(CommandContext ctx, string all = "")
    {
        var hideouts = Hideouts(ctx);
        Guid? guild = null;
        if (all != "all" || ctx.Level < AccountLevel.GameMaster)
            guild = ctx.World.Guilds.Of(ctx.RequirePlayer().Character.Id)?.Id ?? throw new CommandError("you are in no guild");
        var lines = hideouts.Describe(guild).ToList();
        ctx.Reply(lines.Count == 0 ? "no hideouts" : string.Join("\n", lines));
    }

    /// <summary>.hideout finish: the hideout the player stands in, else the nearest one of its zone, ends its construction (or its upgrade) now.</summary>
    [Command("hideout finish", Level = AccountLevel.GameMaster, Help = "the hideout you are in (else the nearest in your zone): construction or upgrade done now")]
    public static void Finish(CommandContext ctx) => ctx.Reply(Hideouts(ctx).Finish(Pick(ctx)));

    /// <summary>.hideout day: the hideout you are in (else the nearest) eats its daily food and pays its tribute now.</summary>
    [Command("hideout day", Level = AccountLevel.GameMaster, Help = "the hideout you are in (else the nearest in your zone): its daily food and tribute now")]
    public static void Day(CommandContext ctx) => ctx.Reply(Hideouts(ctx).Day(Pick(ctx)));

    /// <summary>.hideout place: a hideout of the player's guild where the player stands, without the kit and without the rules (a GM's test).</summary>
    [Command("hideout place", Level = AccountLevel.GameMaster, Help = "places a hideout of your guild where you stand, without the kit or the placement rules")]
    public static void Place(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var hideouts = Hideouts(ctx);
        if (ctx.World.Guilds.Of(player.Character.Id) is null) throw new CommandError("you are in no guild");
        if (player.Map is not { Template: not null }) throw new CommandError("the zone is not loaded yet");
        var h = hideouts.Place(player, player.PositionAt(DateTime.UtcNow), player.Facing);
        ctx.Reply($"{h} placed: {string.Join("", hideouts.Describe(h.Guild).Where(l => l.StartsWith(h.ToString(), StringComparison.Ordinal)))}");
    }

    /// <summary>The hideout the player is in, else the nearest standing in its zone.</summary>
    internal static Hideout Pick(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var hideouts = Hideouts(ctx);
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        if (hideouts.Find(map.Cluster) is { } inside) return inside;
        var at = player.PositionAt(DateTime.UtcNow);
        return map.Objects.Values.OfType<HideoutObject>().MinBy(o => Vector2.Distance(o.Position, at))?.Hideout
            ?? throw new CommandError("no hideout here");
    }
}
