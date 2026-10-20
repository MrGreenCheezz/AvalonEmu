using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The cosmetics (analysis/work/stageN/vanity/SPEC.md): .vanity list, .vanity grant, .vanity revoke, .vanity drop.</summary>
public static class VanityCommands
{
    [Command("vanity list", Level = AccountLevel.GameMaster, Help = "the player's cosmetics, or the catalogue entries whose name has the text")]
    public static void List(CommandContext ctx, Tail filter = default) =>
        ctx.Reply(string.Join("\n", ctx.World.Vanity.Describe(ctx.RequirePlayer().Character, filter.Text)));

    [Command("vanity grant", Level = AccountLevel.GameMaster, Help = "unlocks a cosmetic (its id or name) for the player")]
    public static void Grant(CommandContext ctx, string what)
    {
        var p = ctx.RequirePlayer();
        var u = Find(ctx, what);
        if (!ctx.World.Vanity.Grant(p, u, "GM")) throw new CommandError($"{u} is unlocked already");
        ctx.Reply($"{Vanity.Describe(u)} unlocked (id {u.Id})");
    }

    [Command("vanity revoke", Level = AccountLevel.GameMaster, Help = "takes a cosmetic (its id or name) from the player (the client sees it after a relog)")]
    public static void Revoke(CommandContext ctx, string what)
    {
        var p = ctx.RequirePlayer();
        var u = Find(ctx, what);
        if (!ctx.World.Vanity.Revoke(p.Character, u)) throw new CommandError($"'{p.Character.Name}' has no {u} of its own");
        ctx.Reply($"{Vanity.Describe(u)} revoked; relog to see it locked again");
    }

    /// <summary>.vanity drop [theme source]: a drop as content gives it (chance 1), themed by a source name (UNDEAD, KEEPER, …).</summary>
    [Command("vanity drop", Level = AccountLevel.GameMaster, Help = "a cosmetic drop now, as content gives it (optionally themed: a mob or chest name, e.g. UNDEAD)")]
    public static void Drop(CommandContext ctx, string theme = "")
    {
        var p = ctx.RequirePlayer();
        var u = ctx.World.Vanity.Drop(p, "GM drop", Vanity.ThemeOf(theme));
        ctx.Reply(u is null ? "nothing unlocked (all owned: silver instead, or no data)" : $"dropped {Vanity.Describe(u)} (id {u.Id})");
    }

    private static Data.VanityCatalog.Unlock Find(CommandContext ctx, string what)
    {
        var catalog = ctx.World.Vanity.Catalog;
        return (short.TryParse(what, out var id) ? catalog.Find(id) : catalog.Find(what))
            ?? throw new CommandError($"no vanity unlock '{what}' (.vanity list <text> searches)");
    }
}
