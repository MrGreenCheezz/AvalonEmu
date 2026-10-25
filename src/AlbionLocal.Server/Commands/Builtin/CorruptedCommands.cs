using System.Globalization;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// The corrupted dungeons (stageL corrupted SPEC §4.10): .cd (status) and .cd leave for every player; .cd enter, .cd finish, .cd infamy,
/// .cd list and (phase B) .cd shrine and .cd invade for GMs (<see cref="CorruptedDungeons"/>).
/// </summary>
public static class CorruptedCommands
{
    /// <summary>.cd: the player's infamy, its peak, the bonus per rank, the runs and completions, and the run it is in.</summary>
    [Command("cd", Level = AccountLevel.Player, Help = "your corrupted dungeon infamy, the reward bonus per rank and your run")]
    public static void Status(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        var c = player.Character;
        var current = CorruptedDungeons.Current(c);
        var data = world.Clusters?.CorruptedIfLoaded;
        var ranks = data is null ? "corrupteddungeons.xml not read yet (visit a Hell Den)"
            : string.Join(", ", data.Difficulties.Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Rank} T{d.Tier} +{d.Bonus(current) * 100:0.#} %")));
        var done = c.Infamy?.Completed is { } k ? string.Join("/", k.Select(x => x.ToString(CultureInfo.InvariantCulture))) : "0/0/0";
        var run = world.Corrupted.RunOf(c.Id);
        var inside = run is null ? ""
            : $"; in {run.Difficulty.Rank} {run.World}: {run.Points}/{run.Difficulty.PointsRequirement} points{(run.Completed ? ", completed" : run.DemonSpawned ? ", the demon is out" : "")}";
        ctx.Reply(string.Create(CultureInfo.InvariantCulture,
            $"corrupted dungeons: infamy {current:N0} (peak {CorruptedDungeons.Peak(c):N0}), {c.Infamy?.Runs ?? 0} run(s), completed {done}; bonus {ranks}{inside}"));
    }

    /// <summary>
    /// .cd leave: out of the player's corrupted dungeon to its way back (an early leave forfeits the boss chest); not knocked down or in combat.
    /// Corrupted:LeaveCommand (false, official: only the exit portal and the escape spell; the pve preset true).
    /// </summary>
    [Command("cd leave", Level = AccountLevel.Player, Help = "takes you out of your corrupted dungeon to where you came from")]
    public static void Leave(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (!ctx.World.Config.Corrupted.LeaveCommand) throw new CommandError("no .cd leave on this server (Corrupted:LeaveCommand): use the exit portal or the escape spell");
        if (ctx.World.Corrupted.Find(player.Map?.Cluster) is not { } run) throw new CommandError("you are in no corrupted dungeon");
        // As the exit portal and the escape (CorruptedChange): never past a knockdown or a death's respawn, nor an instant escape from a fight.
        if (player.Combat.Down is { } down) throw new CommandError($"not while knocked down ({down.Kind})");
        if (player.Combat.InCombat) throw new CommandError("not in combat: use the escape spell, or wait until the fight is over");
        ctx.Reply($"leaving {run.Difficulty.Rank} {run.World}");
        ctx.World.Corrupted.Leave(player, run, "command");
    }

    /// <summary>
    /// .cd enter &lt;1-3&gt; [map]: straight into a new run of that difficulty (1 Hunter, 2 Stalker, 3 Slayer) on <paramref name="map"/> (CORRUPT-0nn,
    /// or its number; default CORRUPT-001), without the entrance or its gates; the way back is here.
    /// </summary>
    [Command("cd enter", Level = AccountLevel.GameMaster, Help = "sends the player into a new corrupted dungeon: difficulty 1-3 [map CORRUPT-0nn or n]")]
    public static void Enter(CommandContext ctx, int difficulty, string map = "1")
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (world.Clusters?.CorruptedIfLoaded is not { } data) throw new CommandError("corrupteddungeons.xml is not read (no Hell Den or corrupted dungeon resolved yet)");
        if (data.At(difficulty - 1) is not { } d) throw new CommandError($"difficulty {difficulty}: 1 Hunter, 2 Stalker, 3 Slayer");
        var id = int.TryParse(map, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? string.Create(CultureInfo.InvariantCulture, $"CORRUPT-{n:000}") : map;
        if (!data.Maps.Any(m => m.Id == id)) throw new CommandError($"{id} is no corrupted dungeon map (CORRUPT-001..{data.Maps.Count:000})");
        if (player.Map is not { } here || CorruptedData.IsInstance(here.Cluster) || world.Corrupted.RunOf(player.Character.Id) is not null)
            throw new CommandError("the player is in a corrupted dungeon already");
        var variant = new ClusterVariant(id, d.Tier, d.ClusterType).Id;
        if (world.CachedCluster(variant) is null)
        {
            world.PrefetchCluster(variant);
            throw new CommandError($"{variant} is being read; try again in a moment");
        }
        var run = world.Corrupted.Enter(player, d, id, variant);
        ctx.Reply($"{run}: sending '{player.Character.Name}' in");
    }

    /// <summary>.cd finish: the player's run completed now (the demon's death without the demon: infamy, rewards, the boss chest, the portal).</summary>
    [Command("cd finish", Level = AccountLevel.GameMaster, Help = "completes the player's corrupted dungeon now")]
    public static void Finish(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (ctx.World.Corrupted.Find(player.Map?.Cluster) is not { } run) throw new CommandError("the player is in no corrupted dungeon");
        if (run.Completed) throw new CommandError($"{run} is completed already");
        run.Points = Math.Max(run.Points, run.Difficulty.PointsRequirement);
        ctx.World.Corrupted.Complete(run, player.Map!);
        ctx.Reply($"{run} completed");
    }

    /// <summary>.cd infamy &lt;n&gt;: the player's corrupted dungeon infamy and its peak set to n (Slayer opens at 100 000).</summary>
    [Command("cd infamy", Level = AccountLevel.GameMaster, Help = "sets the player's corrupted dungeon infamy (and peak) to n")]
    public static void Infamy(CommandContext ctx, long value)
    {
        var player = ctx.RequirePlayer();
        if (value < 0 || value > int.MaxValue) throw new CommandError($"infamy is 0..{int.MaxValue}");
        ctx.World.Corrupted.SetInfamy(player.Character, value);
        ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"'{player.Character.Name}': infamy {value:N0}"));
    }

    /// <summary>.cd shrine: the player's consent to invasions now, as by its run's Demonic Shrine (its window and range not checked).</summary>
    [Command("cd shrine", Level = AccountLevel.GameMaster, Help = "uses the Demonic Shrine of the player's corrupted dungeon now (the consent to invasions)")]
    public static void Shrine(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (ctx.World.Corrupted.Find(player.Map?.Cluster) is not { } run || run.Owner != player.Character.Id) throw new CommandError("the player owns no corrupted dungeon it is in");
        if (run.Consented) throw new CommandError("the shrine has been used already");
        ctx.World.Corrupted.Consent(run, player);
        ctx.Reply($"{run}: consented to invasions");
    }

    /// <summary>.cd invade [bot|player]: an invasion of the player's run now, by a bot invader (default) or the consenting player of its rank who waits longest.</summary>
    [Command("cd invade", Level = AccountLevel.GameMaster, Help = "starts an invasion of the player's corrupted dungeon now: bot (default) or player (a consenting one of its rank)")]
    public static void Invade(CommandContext ctx, string by = "bot")
    {
        var player = ctx.RequirePlayer();
        if (ctx.World.Corrupted.Find(player.Map?.Cluster) is not { } run || run.Owner != player.Character.Id) throw new CommandError("the player owns no corrupted dungeon it is in");
        if (by is not ("bot" or "player")) throw new CommandError("bot or player");
        if (ctx.World.Corrupted.Invade(run, by == "bot") is { } why) throw new CommandError(why);
        ctx.Reply($"{run}: invaded ({by})");
    }

    /// <summary>.cd list: the live corrupted dungeon runs.</summary>
    [Command("cd list", Level = AccountLevel.GameMaster, Help = "the live corrupted dungeon runs")]
    public static void List(CommandContext ctx)
    {
        var lines = ctx.World.Corrupted.Describe().ToList();
        ctx.Reply(lines.Count == 0 ? "no corrupted dungeon run" : $"corrupted dungeons:\n{string.Join("\n", lines)}");
    }
}
