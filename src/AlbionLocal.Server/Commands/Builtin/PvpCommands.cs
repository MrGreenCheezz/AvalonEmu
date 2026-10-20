using System.Globalization;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>
/// PvP (stageE pvp SPEC §1-§3): .pvp shows the map's rules and the player's flag, .pvp flag sets the flag, .pvp faction enlists,
/// .pvp reputation shows or sets the reputation, .duel end stops a duel.
/// </summary>
public static class PvpCommands
{
    /// <summary>The map's archetype rules, the player's flag and what each player in view is to it (<see cref="Pvp.Of(Entities.Player, Entities.Player)"/>).</summary>
    [Command("pvp", Level = AccountLevel.GameMaster, Help = "the map's PvP rules, the player's flag and its relation to the players in view")]
    public static void Show(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var rules = Pvp.RulesOf(ctx.World, player.Map);
        var duel = ctx.World.Duels.OpponentOf(player) is { } opponent ? $", in a duel with '{opponent.Character.Name}'" : "";
        var now = ctx.World.GameNow;
        var outlaw = player.PvpState.Implicated(now) ? string.Create(CultureInfo.InvariantCulture, $", implicated {(player.PvpState.ImplicatedUntil - now) / TimeSpan.TicksPerSecond} s") : "";
        var config = ctx.World.Config;
        var mode = config.PvpMode == PvpMode.Official ? "" : $" (Pvp:Mode {config.PvpMode})";
        var lethal = rules.PlayersAreLethal ? Pvp.Lethal(ctx.World, player.Map) ? ", lethal" : ", lethal zone without deaths (Pvp:LethalDeath false)" : "";
        ctx.Reply($"{rules.Rule}{mode}{lethal}{(rules.AllowDuel ? "" : ", no duels")}; flag {Name(player.Flag)}, enlisted "
            + string.Create(CultureInfo.InvariantCulture, $"{(player.Faction == 0 ? "none" : Name(player.Faction))}, reputation {player.Character.Reputation:0.##} (level {Reputation.Level(player)}){outlaw}{duel}"));
        foreach (var other in player.Observers.OrderBy(o => o.Character.Name, StringComparer.OrdinalIgnoreCase))
            ctx.Reply($"  '{other.Character.Name}' ({Name(other.Flag)}): {Pvp.Of(player, other)}");
    }

    /// <summary>
    /// Sets the player's FlaggingStatus at once (no flag cast, no checks; the player's op 354 casts it): ChangeFlaggingFinished (364)
    /// {0 me, 1 flag} to it and its observers, saved (<see cref="Flagging.Set"/>).
    /// </summary>
    [Command("pvp flag", Level = AccountLevel.GameMaster, Help = "sets the player's flag at once: blue, red, highland, forest, steppe, mountain, swamp, caerleon or 0-6/255")]
    public static void Flag(CommandContext ctx, string status)
    {
        var player = ctx.RequirePlayer();
        if (player.Map is null) throw new CommandError("the player is not in the world");
        var flag = Parse(status, red: true);
        Flagging.Interrupt(player, "a GM set the flag");
        Flagging.Set(player, flag);
        ctx.Reply($"flag {Name(flag)}");
    }

    /// <summary>
    /// Enlists the player to a faction (or none: blue) at once, without the switch cooldown: what a faction flag (op 354) needs (SPEC §3.1).
    /// As at a faction trader (stageN faction, <see cref="Factions.Enlist(Entities.Player, byte, bool)"/>): FactionEnlistmentChanged (482) to it and
    /// its observers, a flag cast under way ends, and a player flagged to another faction is Blue again (ChangeFlaggingFinished).
    /// </summary>
    [Command("pvp faction", Level = AccountLevel.GameMaster, Help = "enlists the player to a faction at once, no cooldown (highland, forest, steppe, mountain, swamp, caerleon or 1-6; blue = none)")]
    public static void Faction(CommandContext ctx, string faction)
    {
        var player = ctx.RequirePlayer();
        var f = Parse(faction, red: false);
        ctx.World.Factions.Enlist(player, f, force: true);
        ctx.Reply($"enlisted {(f == 0 ? "to none" : Name(f))}");
    }

    /// <summary>Shows the player's reputation, or sets it to <paramref name="points"/> (ReputationUpdate reason 0, silent in the chat).</summary>
    [Command("pvp reputation", Level = AccountLevel.GameMaster, Help = ".pvp reputation [points]: shows the player's reputation or sets it (-50000 … 50000)")]
    public static void SetReputation(CommandContext ctx, float? points = null)
    {
        var player = ctx.RequirePlayer();
        if (points is { } value) Reputation.Change(player, value - player.Character.Reputation, 0);
        ctx.Reply(string.Create(CultureInfo.InvariantCulture, $"reputation {player.Character.Reputation:0.##} (level {Reputation.Level(player)})"));
    }

    private static byte Parse(string status, bool red) =>
        Array.FindIndex(Names, n => n.Equals(status, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? (byte)i
        : red && status.Equals("red", StringComparison.OrdinalIgnoreCase) ? Pvp.Red
        : byte.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && (b <= Pvp.Caerleon || red && b == Pvp.Red) ? b
        : throw new CommandError($"'{status}' is none of blue, {(red ? "red, " : "")}{string.Join(", ", Names[1..])}, 0-6{(red ? ", 255" : "")}");

    /// <summary>Ends the player's duel as CanceledBySystem (20): a draw, the wagers go back.</summary>
    [Command("duel end", Level = AccountLevel.GameMaster, Help = "ends the player's duel (canceled by the system: a draw, the wagers go back)")]
    public static void EndDuel(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        if (!ctx.World.Duels.Cancel(player)) throw new CommandError("the player is in no duel");
        ctx.Reply("duel ended");
    }

    /// <summary>FlaggingStatus 0-6 by name (stageE pvp SPEC §0).</summary>
    private static readonly string[] Names = ["blue", "highland", "forest", "steppe", "mountain", "swamp", "caerleon"];

    private static string Name(byte flag) => flag == Pvp.Red ? "red" : flag < Names.Length ? Names[flag] : flag.ToString(CultureInfo.InvariantCulture);
}
