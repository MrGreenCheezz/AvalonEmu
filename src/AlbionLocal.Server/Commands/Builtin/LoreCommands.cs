using System.Globalization;
using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>Lore snippets for a GM (analysis/work/stageR/lore/SPEC.md §6): .lore list, .lore grant, .lore reset.</summary>
public static class LoreCommands
{
    /// <summary>The lore snippets of the player's map: object id, name, index, distance, and whether the player claimed it; then its total.</summary>
    [Command("lore list", Level = AccountLevel.GameMaster, Help = "lists the lore snippets of this map and which of them you claimed")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is not { } map) throw new CommandError("the player is on no map");
        var data = ctx.World.Lore.Data ?? throw new CommandError("lore.xml is not available (no cluster data)");
        var at = player.PositionAt(DateTime.UtcNow);
        var here = LoreSnippets.Of(map).OrderBy(o => Vector2.Distance(o.Position, at)).ToList();
        foreach (var o in here)
            ctx.Reply(string.Create(CultureInfo.InvariantCulture,
                $"#{o.Id} {o.Snippet.Name} (index {o.Snippet.Index}) at ({o.Position.X:0.#}, {o.Position.Y:0.#}), {Vector2.Distance(o.Position, at):0} m: {(LoreSnippets.IsClaimed(player.Character, o.Snippet.Name) ? "claimed" : "not claimed")}"));
        var claimed = data.All.Count(s => LoreSnippets.IsClaimed(player.Character, s.Name));
        ctx.Reply($"{here.Count} lore snippet(s) on {map.Cluster}{(ctx.World.Config.LoreSnippetsEnabled ? "" : " (LoreSnippets:Enabled is false)")}; you claimed {claimed} of {data.All.Count}");
    }

    /// <summary>A snippet (lore.xml uniquename or index; "all" = every one) claimed for the player as by a claim: 675, 674, the journal, then 676.</summary>
    [Command("lore grant", Level = AccountLevel.GameMaster, Help = "claims a lore snippet (uniquename, index or all) for you, with its journal progress")]
    public static void Grant(CommandContext ctx, string snippet)
    {
        var player = ctx.RequirePlayer();
        var list = Pick(ctx, snippet);
        var n = list.Count(s => ctx.World.Lore.Claim(player, s));
        LoreSnippets.ChangedByCheat(player);
        ctx.Reply($"{n} lore snippet(s) claimed ({list.Count - n} were already)");
    }

    /// <summary>A snippet (uniquename, index or "all") unclaimed for the player: 674 false for those it sees, then 676; the journal stays.</summary>
    [Command("lore reset", Level = AccountLevel.GameMaster, Help = "makes a lore snippet (uniquename, index or all) unclaimed for you again (the journal stays)")]
    public static void Reset(CommandContext ctx, string snippet)
    {
        var player = ctx.RequirePlayer();
        var list = Pick(ctx, snippet);
        var n = list.Count(s => ctx.World.Lore.Reset(player, s));
        LoreSnippets.ChangedByCheat(player);
        ctx.Reply($"{n} lore snippet(s) unclaimed");
    }

    private static List<LoreData.Snippet> Pick(CommandContext ctx, string what)
    {
        var data = ctx.World.Lore.Data ?? throw new CommandError("lore.xml is not available (no cluster data)");
        if (what.Equals("all", StringComparison.OrdinalIgnoreCase)) return [.. data.All];
        var one = int.TryParse(what, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? data.At(i) : data.Find(what);
        return one is not null ? [one]
            : throw new CommandError($"{what} is not a lore snippet (lore.xml: {string.Join(", ", data.All.Take(6).Select(s => s.Name))}…, or 0-{data.All.Count - 1}, or all)");
    }
}
