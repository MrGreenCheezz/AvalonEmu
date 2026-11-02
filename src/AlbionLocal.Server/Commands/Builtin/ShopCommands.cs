using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>A short-lived player checkout link; the console may select an offline character.</summary>
public static class ShopCommands
{
    [Command("shop", Level = AccountLevel.Player, Help = "opens your local shop; console: .shop <character name>")]
    public static void Open(CommandContext ctx, string character = "")
    {
        var selected = character.Length == 0 ? ctx.RequirePlayer().Character
            : ctx.Console ? ctx.World.Characters.List().SingleOrDefault(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase))
            : throw new CommandError("Only the server console may select another character.");
        if (selected is null) throw new CommandError("Character not found.");
        if (!Uri.TryCreate(ctx.World.Shop.BaseUrl, UriKind.Absolute, out var url) || !url.IsLoopback || url.Scheme != "http")
            throw new CommandError("Enable the local web panel to open the shop.");
        var token = ctx.World.Shop.IssueLoginToken(selected.Account, selected.Id);
        if (token.Length == 0) throw new CommandError("The shop is unavailable for this account.");
        ctx.Reply(ctx.World.Shop.BaseUrl.TrimEnd('/') + "?token=" + Uri.EscapeDataString(token));
    }
}
