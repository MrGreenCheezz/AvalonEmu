using System.Numerics;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The hunts (analysis/work/stageN/hunts/SPEC.md): .hunt list, .hunt start, .hunt escape, .hunt abort.</summary>
public static class HuntCommands
{
    [Command("hunt list", Level = AccountLevel.GameMaster, Help = "the running hunts")]
    public static void List(CommandContext ctx)
    {
        var lines = ctx.World.Hunts.Describe().ToList();
        ctx.Reply(lines.Count == 0 ? "no hunt running" : string.Join("\n", lines));
    }

    /// <summary>.hunt start &lt;target&gt;: a hunt of a dynamichunt.xml target (EARTHDAUGHTER_SOLO_T5, WEREWOLF_SOLO…) from the player's position, as if a track were read there.</summary>
    [Command("hunt start", Level = AccountLevel.GameMaster, Help = "starts a hunt of a dynamichunt.xml target (e.g. EARTHDAUGHTER_SOLO_T5) from here")]
    public static void Start(CommandContext ctx, string target)
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (player.Map is not { Grid: not null } map) throw new CommandError("the player is on no loaded map");
        var data = world.Hunts.Data ?? throw new CommandError("no dynamichunt data");
        var t = data.Target(target) ?? throw new CommandError($"{target} is no target of dynamichunt.xml");
        var mob = world.Clusters?.Mobs.Find(t.MobId) ?? throw new CommandError($"{t.MobId} is not in mobs.xml");
        var hunt = data.HuntFor(t, mob.Tier) ?? throw new CommandError($"no hunt for {t.Name}");
        var track = new HuntTrackObject(world.ObjectIds.Next(), map.Cluster, player.PositionAt(DateTime.UtcNow), 0, t, hunt, Guid.Empty) { Used = true };
        var made = world.Hunts.Start(player, track) ?? throw new CommandError("the hunt did not start");
        ctx.Reply($"{made} started: {made.Quest?.Name} at ({made.Location.X:0}, {made.Location.Y:0}), {Vector2.Distance(made.Location, track.Position):0} m away");
    }

    [Command("hunt escape", Level = AccountLevel.GameMaster, Help = "makes the target of the player's hunt flee now")]
    public static void Escape(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var hunt = ctx.World.Hunts.Of(player.Character.Id) ?? throw new CommandError("the player hunts nothing");
        if (hunt.TargetMob is null) throw new CommandError("the target is not out in this quest");
        ctx.World.Hunts.Escape(hunt, "GM");
        ctx.Reply($"{hunt}: the target flees");
    }

    [Command("hunt abort", Level = AccountLevel.GameMaster, Help = "aborts the player's hunt")]
    public static void Abort(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (!ctx.World.Hunts.AbortBy(player, Guid.Empty)) throw new CommandError("the player hunts nothing");
        ctx.Reply("hunt aborted");
    }
}
