using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// The guild commands (stageH guilds SPEC §3.4, §8): .g (the guild chat the client cannot send here), .guild info and the guild
/// flows of the guild window as commands (a fallback while the client's dialogs are unchecked), each through the same rules; stage I's
/// .guild season / .guild goals / .guild ranking and the GM's .guildgoal (season end included).
/// </summary>
public static class GuildCommands
{
    private static Guilds Guilds(CommandContext ctx) =>
        ctx.World.Guilds.Enabled ? ctx.World.Guilds : throw new CommandError("guilds are off on this server (Guilds:Enabled)");

    /// <summary>.g &lt;text&gt;: a guild chat line to every member that plays (SPEC §8 option 1: the client sends guild chat to a chat server).</summary>
    [Command("g", Level = AccountLevel.Player, Help = "says <text> to your guild (shown as a system line to every member in game)")]
    public static void Say(CommandContext ctx, Tail text)
    {
        var player = ctx.RequirePlayer();
        if (text.IsEmpty) throw new CommandError("usage: .g <text>");
        if (Guilds(ctx).GuildLine(player, text.Text!.Trim()) < 0) throw new CommandError("you are in no guild");
    }

    /// <summary>.guild info: the player's guild, or the one named.</summary>
    [Command("guild info", Level = AccountLevel.Player, Help = "your guild (or the guild <name>): its members and their roles")]
    public static void Info(CommandContext ctx, Tail name = default)
    {
        var guilds = Guilds(ctx);
        var g = !name.IsEmpty ? guilds.Find(name.Text) ?? throw new CommandError($"no guild '{name.Text!.Trim()}'")
            : guilds.Of(ctx.RequirePlayer().Character.Id) ?? throw new CommandError("you are in no guild");
        ctx.Reply(guilds.Describe(g));
    }

    /// <summary>.guild list: every guild with its member count (GM).</summary>
    [Command("guild list", Level = AccountLevel.GameMaster, Help = "every guild with its member count")]
    public static void List(CommandContext ctx)
    {
        var all = Guilds(ctx).All.ToList();
        ctx.Reply(all.Count == 0 ? "no guilds" : string.Join("\n", all.Select(g => $"{g.Name}: {g.Members.Count} member(s), master {g.Master?.Name ?? "-"}")));
    }

    /// <summary>.guild create &lt;name&gt;: as CreateGuild with the default crest (Guilds:CreateCost is taken).</summary>
    [Command("guild create", Level = AccountLevel.Player, Help = "founds the guild <name> with the default crest (costs Guilds:CreateCost silver)")]
    public static void Create(CommandContext ctx, Tail name)
    {
        var player = ctx.RequirePlayer();
        var guilds = Guilds(ctx);
        var rc = guilds.Create(player, name.Text, GuildLogo.Default, "", out _);
        if (rc != 0) throw new CommandError(rc switch
        {
            Systems.Guilds.AlreadyInGuild => "you are in a guild already",
            Systems.Guilds.NameNotAllowed => "a guild name has 3-30 letters, digits and single spaces",
            Systems.Guilds.NameTaken => "that name is taken",
            Systems.Guilds.NotEnoughSilver => $"a guild costs {ctx.World.Config.GuildCreateCost} silver",
            _ => $"refused ({rc})",
        });
        guilds.Announce(player);
        ctx.Reply($"guild '{guilds.Of(player.Character.Id)!.Name}' founded");
    }

    /// <summary>.guild invite &lt;name&gt;: as InviteToGuild.</summary>
    [Command("guild invite", Level = AccountLevel.Player, Help = "invites the player <name> into your guild")]
    public static void Invite(CommandContext ctx, string name)
    {
        var rc = Guilds(ctx).Invite(ctx.RequirePlayer(), name, out var target, out _);
        if (rc != 0) throw new CommandError($"refused ({rc}: {rc switch
        {
            Systems.Guilds.NotInGuild => "you are in no guild",
            Systems.Guilds.NoInviteRight => "your rank may not invite",
            Systems.Guilds.PlayerDoesNotExist => "no such character",
            Systems.Guilds.PlayerNotOnline => "not in game",
            Systems.Guilds.PlayerInGuild => "in a guild already",
            Systems.Guilds.PlayerInvited => "invited already",
            _ => "see the guild window",
        }})");
        ctx.Reply($"{target} is invited (.guild accept on their side)");
    }

    /// <summary>.guild accept: as JoinGuild (accepts the open invitation).</summary>
    [Command("guild accept", Level = AccountLevel.Player, Help = "accepts your open guild invitation")]
    public static void Accept(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var guilds = Guilds(ctx);
        var rc = guilds.Accept(player, out var g);
        if (rc != 0 || g is null) throw new CommandError(rc == Systems.Guilds.Refused ? "no open guild invitation" : $"refused ({rc})");
        guilds.AfterJoin(player, g);
        ctx.Reply($"you joined guild '{g.Name}'");
    }

    /// <summary>.guild leave [successor]: as LeaveGuild; a guild master names the member who leads after it.</summary>
    [Command("guild leave", Level = AccountLevel.Player, Help = "leaves your guild; a guild master names the <successor> (the last member disbands it)")]
    public static void Leave(CommandContext ctx, string successor = "")
    {
        var player = ctx.RequirePlayer();
        var guilds = Guilds(ctx);
        var g = guilds.Of(player.Character.Id) ?? throw new CommandError("you are in no guild");
        var next = g.Find(successor)?.Id ?? Guid.Empty;
        if (guilds.Leave(player, next) != 0) throw new CommandError("a guild master names a member as successor: .guild leave <name>");
        // What the client's LeaveGuild response does: its guild window clears (104 kind 3 with its own Guid).
        player.Session.Send(new Protocol.GuildPlayerUpdatedEvent { cfr = player.Character.Id, cfs = player.Character.Name, cft = true, cfu = ctx.World.GameNow, cf0 = Systems.Guilds.KindLeft });
        guilds.Announce(player);
        ctx.Reply($"you left guild '{g.Name}'");
    }

    /// <summary>.guild kick &lt;name&gt;: as KickFromGuild.</summary>
    [Command("guild kick", Level = AccountLevel.Player, Help = "kicks the member <name> (the client's rule: the master anyone, others with the right and the tags)")]
    public static void Kick(CommandContext ctx, string name)
    {
        if (Guilds(ctx).Kick(ctx.RequirePlayer(), Guid.Empty, name) != 0) throw new CommandError($"cannot kick '{name}'");
        ctx.Reply($"'{name}' kicked");
    }

    /// <summary>.guild rank &lt;name&gt; &lt;role&gt;: the member's role (GUILDMASTER = the hand-over), by the rank rules.</summary>
    [Command("guild rank", Level = AccountLevel.Player, Help = "gives the member <name> the role <role> (Recruit … TheRightHand; Guildmaster hands the guild over)")]
    public static void Rank(CommandContext ctx, string name, string role)
    {
        var player = ctx.RequirePlayer();
        var guilds = Guilds(ctx);
        var g = guilds.Of(player.Character.Id) ?? throw new CommandError("you are in no guild");
        if (!Enum.TryParse<GuildRole>(role.Replace("_", ""), ignoreCase: true, out var r) || r == GuildRole.None)
            throw new CommandError($"role: one of {string.Join(", ", Enum.GetNames<GuildRole>().Skip(1))}");
        var target = g.Find(name) ?? throw new CommandError($"no member '{name}'");
        if (!guilds.SetRole(g, g.Find(player.Character.Id)!, target, r)) throw new CommandError($"you may not make '{target.Name}' {r}");
        ctx.Reply($"'{target.Name}' is {target.Role}");
    }

    /// <summary>.guild contracts: the guild's contracts of the period with their progress (SPEC §10.6).</summary>
    [Command("guild contracts", Level = AccountLevel.Player, Help = "your guild's contracts of this period and their progress")]
    public static void Contracts(CommandContext ctx)
    {
        var guilds = Guilds(ctx);
        var g = guilds.Of(ctx.RequirePlayer().Character.Id) ?? throw new CommandError("you are in no guild");
        ctx.Reply(string.Join("\n", guilds.DescribeContracts(g)));
    }

    /// <summary>.guild season: the guild's season, Guild Challenge level and points, season points, bracket and rank (stage I guild goals).</summary>
    [Command("guild season", Level = AccountLevel.Player, Help = "your guild's season: Guild Challenge level and points, season points, bracket, rank and contributions")]
    public static void Season(CommandContext ctx)
    {
        var g = Guilds(ctx).Of(ctx.RequirePlayer().Character.Id) ?? throw new CommandError("you are in no guild");
        ctx.Reply(string.Join("\n", ctx.World.GuildGoals.DescribeSeason(g)));
    }

    /// <summary>.guild goals: this week's guild goals with their progress and the guild blessing (stage I guild goals).</summary>
    [Command("guild goals", Level = AccountLevel.Player, Help = "your guild's weekly goals, their progress and the guild blessing")]
    public static void Goals(CommandContext ctx)
    {
        var g = Guilds(ctx).Of(ctx.RequirePlayer().Character.Id) ?? throw new CommandError("you are in no guild");
        ctx.Reply(string.Join("\n", ctx.World.GuildGoals.DescribeWeek(g)));
    }

    /// <summary>.guild ranking: the local guilds of the season by guild season points (stage I guild goals; the Rankings tab of the Guild Season window).</summary>
    [Command("guild ranking", Level = AccountLevel.Player, Help = "the guilds of this server by guild season points, with their brackets")]
    public static void Ranking(CommandContext ctx) =>
        ctx.Reply(string.Join("\n", ctx.World.GuildGoals.DescribeRanking(ctx.Player is { } p ? Guilds(ctx).Of(p.Character.Id) : null)));

    /// <summary>The guild named, or the GM's own when the name is empty.</summary>
    private static Guild Target(CommandContext ctx, Tail name) =>
        !name.IsEmpty ? Guilds(ctx).Find(name.Text) ?? throw new CommandError($"no guild '{name.Text!.Trim()}'")
            : Guilds(ctx).Of(ctx.RequirePlayer().Character.Id) ?? throw new CommandError("you are in no guild: name one");

    /// <summary>.guildgoal challenge &lt;points&gt; [guild]: Guild Challenge points (GM; a test of the levels, their season points and the popup).</summary>
    [Command("guildgoal challenge", Level = AccountLevel.GameMaster, Help = "adds <points> Guild Challenge points to the guild [name] (your own without one)")]
    public static void Challenge(CommandContext ctx, long points, Tail name = default)
    {
        var g = Target(ctx, name);
        if (points <= 0) throw new CommandError("give a positive amount");
        var goals = ctx.World.GuildGoals;
        goals.AddChallenge(g, ctx.Player?.Character.Id is { } me && g.Find(me) is not null ? me : Guid.Empty, points);
        ctx.Reply(string.Join("\n", goals.DescribeSeason(g).Take(2)));
    }

    /// <summary>.guildgoal points &lt;n&gt; [guild]: guild season points (GM).</summary>
    [Command("guildgoal points", Level = AccountLevel.GameMaster, Help = "adds <n> guild season points to the guild [name] (your own without one)")]
    public static void Points(CommandContext ctx, long points, Tail name = default)
    {
        var g = Target(ctx, name);
        if (points <= 0) throw new CommandError("give a positive amount");
        ctx.World.GuildGoals.GrantPoints(g, points);
        ctx.Reply(string.Join("\n", ctx.World.GuildGoals.DescribeSeason(g).Skip(2).Take(1)));
    }

    /// <summary>.guildgoal might &lt;category&gt; &lt;might&gt; [guild]: guild might of a category of the season (GM; a test of the Guild Might tab, its levels and the popup).</summary>
    [Command("guildgoal might", Level = AccountLevel.GameMaster, Help = "adds <might> guild might of <category> (PVE, GATHERING, CORRUPTED …) to the guild [name]")]
    public static void Might(CommandContext ctx, string category, long might, Tail name = default)
    {
        var g = Target(ctx, name);
        if (might <= 0) throw new CommandError("give a positive amount");
        var goals = ctx.World.GuildGoals;
        if (!goals.GrantMight(g, ctx.Player?.Character.Id is { } me && g.Find(me) is not null ? me : Guid.Empty, category.ToUpperInvariant(), might))
            throw new CommandError($"no might category '{category}' this season: {string.Join(", ", goals.Season.Might.Select(c => c.Name))}");
        ctx.Reply(string.Join("\n", goals.DescribeSeason(g).Where(l => l.StartsWith("guild might", StringComparison.Ordinal))));
    }

    /// <summary>
    /// .guildgoal season end: the current guild season of every guild closed and paid now, as at its end (GM; a test of the brackets, the
    /// member rewards, the journal and the owed rewards before the season's real end); the season then goes on from 0.
    /// </summary>
    [Command("guildgoal season end", Level = AccountLevel.GameMaster, Help = "ends the current guild season of every guild now: bracket silver and member rewards paid, the season goes on from 0")]
    public static void SeasonEnd(CommandContext ctx)
    {
        var key = ctx.World.GuildGoals.EndSeason() ?? throw new CommandError("the guild goals are off (Guild:Enabled)");
        ctx.Reply($"guild season closed and paid (kept as {key}); every guild starts again at 0");
    }

    /// <summary>.guildgoal done &lt;n&gt; [guild]: finishes this week's goal n with its reward (GM).</summary>
    [Command("guildgoal done", Level = AccountLevel.GameMaster, Help = "finishes this week's goal <n> (1-based) of the guild [name], with its reward")]
    public static void Done(CommandContext ctx, int n, Tail name = default)
    {
        var g = Target(ctx, name);
        var week = ctx.World.GuildGoals.WeekOf(g) ?? throw new CommandError("weekly guild goals are off (Guild:Weekly)");
        if (n < 1 || n > week.Goals.Count) throw new CommandError($"goal 1..{week.Goals.Count}");
        if (week.Goals[n - 1].Done) throw new CommandError("done already (paid once)");
        ctx.World.GuildGoals.Finish(g, week, week.Goals[n - 1]);
        ctx.Reply($"guild '{g.Name}': {week.Goals[n - 1].Describe()} done");
    }

    /// <summary>.guildgoal roll [guild]: this week's goals rolled again (GM; the progress is lost, the blessing kept).</summary>
    [Command("guildgoal roll", Level = AccountLevel.GameMaster, Help = "rolls this week's goals of the guild [name] again (progress lost)")]
    public static void Roll(CommandContext ctx, Tail name = default)
    {
        var g = Target(ctx, name);
        var week = ctx.World.GuildGoals.Reroll(g, Environment.TickCount) ?? throw new CommandError("weekly guild goals are off (Guild:Weekly)");
        ctx.Reply($"guild '{g.Name}': {string.Join("; ", week.Goals.Select(x => x.Describe()))}");
    }

    /// <summary>
    /// .guild payout &lt;name&gt; &lt;silver&gt;: silver of the guild account to a member in game (MASTER_OF_COIN or higher, SPEC §10.5); only with
    /// Guilds:SeasonRewards Content or another source of the account (Guilds:SilverTax, Guilds:Contracts).
    /// </summary>
    [Command("guild payout", Level = AccountLevel.Player, Help = "pays <silver> of the guild account to the member <name> in game (Master of Coin or higher)")]
    public static void Payout(CommandContext ctx, string name, long silver)
    {
        // The guild account fills only from the server's own sources: season silver (Guilds:SeasonRewards Content), Guilds:SilverTax, Guilds:Contracts.
        var c = ctx.World.Config;
        if (c.GuildSeasonRewards != RuleSource.Content && c.GuildSilverTax <= 0 && !c.GuildContracts)
            throw new CommandError("no guild account payouts on this server (Guilds:SeasonRewards Official, no Guilds:SilverTax or Guilds:Contracts)");
        if (Guilds(ctx).Payout(ctx.RequirePlayer(), name, silver) is { } error) throw new CommandError(error);
        ctx.Reply($"{silver} silver paid to '{name}'");
    }

    /// <summary>.guild xp &lt;name&gt; &lt;amount&gt;: adds guild experience (GM; a test of levels and perks).</summary>
    [Command("guild xp", Level = AccountLevel.GameMaster, Help = "adds <amount> experience to the guild <name>")]
    public static void Xp(CommandContext ctx, string name, long amount)
    {
        var guilds = Guilds(ctx);
        var g = guilds.Find(name) ?? throw new CommandError($"no guild '{name}'");
        if (amount <= 0) throw new CommandError("give a positive amount");
        guilds.AddXp(g, amount);
        ctx.Reply($"guild '{g.Name}': level {guilds.Level(g)}, {g.Xp} xp");
    }

    /// <summary>.guild disband &lt;name&gt;: deletes the guild (Admin).</summary>
    [Command("guild disband", Help = "deletes the guild <name>; its members in game see it gone")]
    public static void Disband(CommandContext ctx, Tail name)
    {
        var guilds = Guilds(ctx);
        var g = guilds.Find(name.Text) ?? throw new CommandError($"no guild '{name.Text?.Trim()}'");
        guilds.Disband(g);
        ctx.Reply($"guild '{g.Name}' disbanded");
    }
}
