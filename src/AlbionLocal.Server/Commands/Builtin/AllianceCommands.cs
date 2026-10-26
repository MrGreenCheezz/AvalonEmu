using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// The alliance commands (stageP hideouts SPEC §7 A1/A2): .alliance info and the alliance window's flows as commands (invite, accept,
/// decline, kick, leave; a fallback while the client's dialogs are unchecked, and the way to answer when mail is off), each through the
/// same rules; .alliance list and .alliance disband for the GM.
/// </summary>
public static class AllianceCommands
{
    private static Alliances Alliances(CommandContext ctx) =>
        ctx.World.Alliances.Enabled ? ctx.World.Alliances : throw new CommandError("alliances are off on this server (Alliances:Enabled, Guilds:Enabled)");

    /// <summary>.alliance info: the player's alliance, or the one named (name or tag).</summary>
    [Command("alliance info", Level = AccountLevel.Player, Help = "your alliance (or the alliance <name or tag>): its guilds")]
    public static void Info(CommandContext ctx, Tail name = default)
    {
        var alliances = Alliances(ctx);
        var a = !name.IsEmpty ? alliances.Find(name.Text) ?? throw new CommandError($"no alliance '{name.Text!.Trim()}'")
            : alliances.Of(ctx.RequirePlayer().Character.Id) ?? throw new CommandError("your guild is in no alliance");
        ctx.Reply(alliances.Describe(a));
    }

    private static void Rc(short rc, string what)
    {
        if (rc != 0) throw new CommandError($"{what} refused ({rc})");
    }

    /// <summary>.alliance invite &lt;guild&gt;: as AllianceInvite (the guild gets the invitation mail).</summary>
    [Command("alliance invite", Level = AccountLevel.Player, Help = "invites the guild <name> into your alliance (founder guild, guild master or The Right Hand)")]
    public static void Invite(CommandContext ctx, Tail guild)
    {
        Rc(Alliances(ctx).Invite(ctx.RequirePlayer(), Guid.Empty, guild.Text), "the invitation");
        ctx.Reply($"guild '{guild.Text?.Trim()}' is invited (.alliance accept on their side, or the mail)");
    }

    /// <summary>.alliance accept / decline [alliance]: answers the guild's (newest, or named) open invitation, as the mail's buttons.</summary>
    [Command("alliance accept", Level = AccountLevel.Player, Help = "accepts your guild's invitation into the alliance <name or tag> (the newest without a name)")]
    public static void Accept(CommandContext ctx, Tail name = default) => Answer(ctx, name, accept: true);

    [Command("alliance decline", Level = AccountLevel.Player, Help = "declines your guild's invitation into the alliance <name or tag> (the newest without a name)")]
    public static void Decline(CommandContext ctx, Tail name = default) => Answer(ctx, name, accept: false);

    private static void Answer(CommandContext ctx, Tail name, bool accept)
    {
        var alliances = Alliances(ctx);
        var player = ctx.RequirePlayer();
        var g = ctx.World.Guilds.Of(player.Character.Id) ?? throw new CommandError("you are in no guild");
        var named = name.IsEmpty ? null : alliances.Find(name.Text) ?? throw new CommandError($"no alliance '{name.Text!.Trim()}'");
        var invite = alliances.InvitesTo(g.Id).LastOrDefault(i => named is null || i.Alliance == named.Id) ?? throw new CommandError("your guild has no such invitation");
        Rc(alliances.Answer(player, invite, accept, out _), accept ? "joining" : "declining");
        ctx.Reply(accept ? $"your guild joined '{alliances.ById(invite.Alliance)?.Name}'" : "invitation declined");
    }

    /// <summary>.alliance kick &lt;guild&gt;: as AllianceKickGuild.</summary>
    [Command("alliance kick", Level = AccountLevel.Player, Help = "kicks the guild <name> from your alliance (founder guild)")]
    public static void Kick(CommandContext ctx, Tail guild)
    {
        var g = ctx.World.Guilds.Find(guild.Text) ?? throw new CommandError($"no guild '{guild.Text?.Trim()}'");
        Rc(Alliances(ctx).Kick(ctx.RequirePlayer(), g.Id), "the kick");
        ctx.Reply($"guild '{g.Name}' kicked");
    }

    /// <summary>.alliance leave: as AllianceLeave (the founder alone disbands).</summary>
    [Command("alliance leave", Level = AccountLevel.Player, Help = "your guild leaves its alliance (the founder guild alone: disbands it)")]
    public static void Leave(CommandContext ctx)
    {
        Rc(Alliances(ctx).Leave(ctx.RequirePlayer(), Guid.Empty), "leaving");
        ctx.Reply("your guild left its alliance");
    }

    /// <summary>.alliance list: every alliance with its guild and player counts (GM).</summary>
    [Command("alliance list", Level = AccountLevel.GameMaster, Help = "every alliance with its guild and player counts")]
    public static void List(CommandContext ctx)
    {
        var alliances = Alliances(ctx);
        var all = alliances.All.ToList();
        ctx.Reply(all.Count == 0 ? "no alliances" : string.Join("\n", all.Select(a => $"{a.Name} [{a.Tag}]: {a.Guilds.Count} guild(s), {alliances.Players(a)} player(s)")));
    }

    /// <summary>.alliance disband &lt;name or tag&gt;: the GM disbands an alliance (every guild leaves, as AllianceDisband).</summary>
    [Command("alliance disband", Level = AccountLevel.GameMaster, Help = "disbands the alliance <name or tag>")]
    public static void Disband(CommandContext ctx, Tail name)
    {
        var alliances = Alliances(ctx);
        var a = alliances.Find(name.Text) ?? throw new CommandError($"no alliance '{name.Text?.Trim()}'");
        alliances.Disband(a, "a GM");
        ctx.Reply($"alliance '{a.Name}' disbanded");
    }
}
