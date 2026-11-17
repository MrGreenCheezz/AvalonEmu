using System.Globalization;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Fame credits and the destiny board re-spec (stageR items SPEC §3). The credits per ReSpecFameType (1 combat, 2 gathering, 3 crafting,
/// 4 refining) are Join key 43 (a7m[5], ctp+0x580) and UpdateReSpecPoints (84) {0 the credits[5], 1 the type, 2 the credits gained,
/// 3 the silver paid} (ctp.abq 0x180992BA0 takes key 0, the chat line "You gained {0} … Fame Credits [and paid {1} Silver]" keys 2/3).
/// A node's fame in credits is fame × lpmultiplier / famemultiplier × respecmodifier (<see cref="Credits"/>; b8a+0x50 × b8a+0x58, the
/// client's bgf.abq 0x1805CDA50). Earned two ways:
/// <list type="bullet">
/// <item>a maxed node with a re-spectype that a gain would feed (<see cref="DestinyBoard.MaxedTargets"/>) converts its share: with the
/// auto-respec flag (UseFlag 2, Join key 100) credits × (1 − fameDecayOnConversion 0.2) for credits × silverConversionRatio (1) silver,
/// else credits × (1 − fameDecayOnMaxLevel 0.8) for nothing ("4 times as many" with the flag, the tutorial says) [infer: the client only
/// shows these numbers; immediately instead of "about every 5 seconds"];</item>
/// <item>ReSpecAchievement (147) {0 node, 1 its current level, 2 the target, 3 the client's silver} to a lower level: the fame of the
/// levels given up (the progress + Σ need, a36.o 0x1808DEF50) in credits × (1 − 0.2), for credits × silverConversionRatio silver rounded up
/// to whole silver (bgf.abo 0x1805CBA40) [infer the rounding].</item>
/// </list>
/// Spent by ReSpecAchievement to a higher level: Σ need − progress (a36.q 0x1808DFED0) in credits, and for elite levels (100+) also that
/// level's credits × its SilverPerReSpec (0.33) in silver (a36.n 0x1808DE770). The answer is a plain response whose ReturnCode the client
/// shows as its re-spec error (cy8.u 0x1809FD240, message 0x16FA + code): 0 done (the client sets the level itself, a36.n 0x1808DD6A0),
/// 1 not respec-able or a level the server does not have, 2 not enough credits, 3 not enough silver, 6 out of range, 7 below level 1,
/// 8 nothing to give up, 9 no change, 10 unknown node (a36.p 0x1808DFD40, cy8.v 0x1809FD6B0).
/// </summary>
public static class FameCredits
{
    private const long A7m = DestinyBoard.A7m;

    private static long Mul(long a, long b) => (long)((Int128)a * b / A7m);

    /// <summary><paramref name="fame"/> (a7m) of <paramref name="node"/> in its credits (a7m).</summary>
    public static long Credits(BoardNode node, long fame) => Mul(Mul(fame, node.CreditRatio), node.RespecModifier);

    /// <summary>UpdateReSpecPoints (84) with the character's credits.</summary>
    public static UpdateReSpecPointsEvent Event(Player p, int type, long gained, long silver) =>
        new() { d5k = [.. p.Character.FameCredits], d5l = type, d5m = gained, d5n = silver };

    /// <summary>The credits a gain of <paramref name="gain"/> (a7m, the board's amount) earns on <paramref name="p"/>'s maxed nodes.</summary>
    public static void OnFame(Player p, long gain, FameContext ctx)
    {
        if (gain <= 0 || p.Character.Progression is not { } progression || p.World.GameData?.Board is not { } board) return;
        var economy = p.World.Economy;
        foreach (var id in board.MaxedTargets(progression, ctx))
        {
            var node = board.Nodes[id];
            var full = Credits(node, gain);
            long credits = 0, silver = 0;
            if (p.Character.UseFlag(2))
            {
                credits = (long)Math.Floor(full * (decimal)(1 - economy.RespecDecay));
                silver = (long)Math.Ceiling(credits * (decimal)economy.RespecSilverRatio);
                if (!Wallet.TrySpend(p, silver, $"fame credits of {node.Name}")) (credits, silver) = (0, 0);
            }
            if (credits == 0) credits = (long)Math.Floor(full * (decimal)(1 - economy.RespecMaxLevelDecay));
            if (credits <= 0) continue;
            p.Character.FameCredits[node.RespecType] += credits;
            p.World.Saves.MarkChanged(p.Character);
            p.Session.Send(Event(p, node.RespecType, credits, silver));
        }
    }

    /// <summary>ReSpecAchievement (147): the return code (0 = done) and why.</summary>
    public static short ReSpec(Player p, int id, int current, int target, long clientSilver, out string why)
    {
        why = "";
        if (p.Character.Progression is not { } progression || p.World.GameData?.Board is not { } board || id < 0 || id >= board.Nodes.Count)
        {
            why = $"unknown node {id}";
            return 10;
        }
        var node = board.Nodes[id];
        if (target < 1) { why = "below level 1"; return 7; }
        if (!node.IsTemplate || node.RespecType == 0 || !progression.Levels.TryGetValue(id, out var level) || level != current)
        {
            why = $"{node.Name} cannot be re-specced here (server level {progression.Levels.GetValueOrDefault(id)}, client {current})";
            return 1;
        }
        var count = board.LevelCount(id);
        if (target == level) { why = "no change"; return 9; }
        if (level < 1) { why = "nothing to give up or build on"; return target > level ? (short)6 : (short)8; }
        var progress = progression.Progress.GetValueOrDefault(id);
        var c = p.Character;
        var type = node.RespecType;
        var economy = p.World.Economy;
        if (target > level)
        {
            if (target > count) { why = $"{node.Name} has {count} levels"; return 6; }
            long fame = -progress, silver = 0;
            for (var l = level; l < target; l++)
            {
                fame += board.LevelFame(id, l);
                if (l >= node.BaseLevels) silver += (long)Math.Ceiling(Credits(node, board.LevelFame(id, l)) * (decimal)node.Levels[l].SilverPerRespec);
            }
            var credits = Credits(node, Math.Max(0, fame));
            if (c.FameCredits[type] < credits) { why = $"{credits / 10000.0:0.##} credits needed, {c.FameCredits[type] / 10000.0:0.##} kept"; return 2; }
            if (c.Silver < silver) { why = $"{silver / 10000} silver needed"; return 3; }
            Wallet.TrySpend(p, silver, $"re-spec of {node.Name}");
            c.FameCredits[type] -= credits;
            Apply(p, progression, id, target);
            p.Session.Send(Event(p, type, 0, silver)); // key 2 is what was gained: nothing
            why = string.Create(CultureInfo.InvariantCulture, $"{node.Name} {level} → {target} for {credits / 10000.0:0.##} credits and {silver / 10000.0:0.##} silver (client {clientSilver / 10000.0:0.##})");
        }
        else
        {
            var fame = progress;
            for (var l = target; l < level; l++) fame += board.LevelFame(id, l);
            var credits = (long)Math.Floor(Credits(node, fame) * (decimal)(1 - economy.RespecDecay));
            var silver = (long)Math.Ceiling(credits * (decimal)economy.RespecSilverRatio / A7m) * A7m;
            if (c.Silver < silver) { why = $"{silver / 10000} silver needed"; return 3; }
            Wallet.TrySpend(p, silver, $"re-spec of {node.Name}");
            c.FameCredits[type] += credits;
            Apply(p, progression, id, target);
            p.Session.Send(Event(p, type, credits, silver));
            why = string.Create(CultureInfo.InvariantCulture, $"{node.Name} {level} → {target}: {credits / 10000.0:0.##} credits for {silver / 10000.0:0.##} silver (client {clientSilver / 10000.0:0.##})");
        }
        return 0;
    }

    private static void Apply(Player p, Progression progression, int id, int target)
    {
        progression.Progress.Remove(id);
        progression.Levels[id] = target;
        p.World.Saves.MarkChanged(p.Character);
        var now = p.World.GameNow;
        if (p.RecalculateStats(now)) p.SendAround(Combat.PlayerCombo(p, now));
    }
}
