using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>StageR fw-zone (<see cref="Systems.FactionFortresses"/>, <see cref="Systems.RedZoneEvents"/>): .fortress and .redzone.</summary>
public static class FortressCommands
{
    [Command("fortress", Level = AccountLevel.Player, Help = "the faction fortress of this zone: owner, state, siege, capture area, guards")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("not on a map");
        foreach (var line in ctx.World.Fortresses.Describe(map)) ctx.Reply(line);
    }

    [Command("fortress siege", Level = AccountLevel.GameMaster, Help = "<faction 1-6> besieges this zone's fortress: announcement, pre-siege, active siege")]
    public static void Siege(CommandContext ctx, int faction) => Gm(ctx, "siege", faction);

    [Command("fortress next", Level = AccountLevel.GameMaster, Help = "the running siege goes on to its next phase (an active siege ends as defended)")]
    public static void Next(CommandContext ctx) => Gm(ctx, "next", 0);

    [Command("fortress owner", Level = AccountLevel.GameMaster, Help = "this zone's fortress becomes <faction 1-6>'s")]
    public static void Owner(CommandContext ctx, int faction) => Gm(ctx, "owner", faction);

    [Command("fortress clear", Level = AccountLevel.GameMaster, Help = "the fortress tower's guards go for an hour")]
    public static void Clear(CommandContext ctx) => Gm(ctx, "clear", 0);

    private static void Gm(CommandContext ctx, string action, int faction)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("not on a map");
        if (faction is < 0 or > 6) throw new CommandError("a faction 1-6 (Highland 1, Forest 2, Steppe 3, Mountain 4, Swamp 5, Caerleon 6)");
        ctx.Reply(ctx.World.Fortresses.Gm(map, action, (byte)faction));
    }

    [Command("redzone", Level = AccountLevel.Player, Help = "the bandit assault: phase, its end, standings")]
    public static void RedZone(CommandContext ctx) => ctx.Reply(ctx.World.RedZone.Describe());

    [Command("redzone start", Level = AccountLevel.GameMaster, Help = "a bandit assault starts now (announcement)")]
    public static void Start(CommandContext ctx)
    {
        ctx.World.RedZone.Begin();
        ctx.Reply(ctx.World.RedZone.Describe());
    }

    [Command("redzone next", Level = AccountLevel.GameMaster, Help = "the bandit assault goes on to its next phase (after phase 2: the end)")]
    public static void NextPhase(CommandContext ctx)
    {
        if (ctx.World.RedZone.Phase == Systems.RedZoneEvents.None) throw new CommandError("no bandit assault runs");
        ctx.World.RedZone.Advance();
        ctx.Reply(ctx.World.RedZone.Describe());
    }
}
