using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The extra action bars (<see cref="ExtraBars"/>, analysis/work/stageN/extrabars/SPEC.md): .extrabar list | set &lt;n&gt; &lt;spell&gt; | clear [n] | reload.</summary>
public static class ExtraBarCommands
{
    /// <summary>.extrabar list: every slot with its spell, where it comes from and its cooldown; whether this connection runs the mod.</summary>
    [Command("extrabar list", Level = AccountLevel.GameMaster, Help = "your extra bars: each slot's spell, the item (or override) it comes from")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var bars = ctx.World.ExtraBars;
        var st = bars.State(player);
        var spells = ctx.World.Spells;
        var size = bars.Table.Size;
        var lines = new List<string> { $"{bars.Table.Bars} bar(s) of {size}; mod {(ExtraBars.Modded(player) ? $"connected (protocol {player.Session.ExtraBarsProtocol})" : "not connected")}" };
        for (var i = 0; i < st.Spells.Length; i++)
        {
            var id = st.Spells[i];
            var left = id >= 0 && player.Combat.Cooldowns.TryGetValue(id, out var end) && end > ctx.World.GameNow ? $", cooldown {(end - ctx.World.GameNow) / (double)TimeSpan.TicksPerSecond:0.#} s" : "";
            var from = st.Sources[i] switch { 1 => $" [{st.Items[i]}: {st.Labels.ElementAtOrDefault(i)}]", 2 => " [override]", _ => "" };
            lines.Add($"{i / size + 1}-{i % size + 1} (slot {i + 1}): {(id < 0 ? "-" : spells?.Name(id))}{from}{left}");
        }
        ctx.Reply(string.Join("\n", lines));
    }

    /// <summary>.extrabar explain: per worn item which rule (Items entry, slot rule, bonuses, artifact) made which spells, then the slots with their rule.</summary>
    [Command("extrabar explain", Level = AccountLevel.GameMaster, Help = "which rule of extraspells.json gave each of your extra slots, per worn item")]
    public static void Explain(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var bars = ctx.World.ExtraBars;
        var table = bars.Table;
        var lines = new List<string> { $"rules {(table.Generators.Enabled ? "on" : "off")}, {table.Rules.Count} Items entries, {table.Slots} slots" };
        lines.AddRange(bars.Explain(player));
        var st = bars.State(player);
        var spells = ctx.World.Spells;
        for (var i = 0; i < st.Spells.Length; i++)
            if (st.Spells[i] >= 0)
                lines.Add($"slot {i + 1}: {spells?.Name(st.Spells[i])} ← {(st.Sources[i] == 2 ? "override" : $"{st.Items[i]} ({st.Labels.ElementAtOrDefault(i)})")}");
        ctx.Reply(string.Join("\n", lines));
    }

    /// <summary>.extrabar set &lt;n&gt; &lt;spell&gt;: an override of slot n (1-12) with an activespell; "-" takes the override away.</summary>
    [Command("extrabar set", Level = AccountLevel.GameMaster, Help = "puts <spell> into your extra slot <n> (1-12) over the gear; '-' takes the override away")]
    public static void Set(CommandContext ctx, int n, string spell)
    {
        var player = ctx.RequirePlayer();
        var why = ctx.World.ExtraBars.SetOverride(player, n - 1, spell == "-" ? null : spell, ExtraBarChangeSource.Plugin);
        if (why is not null) throw new CommandError(why);
        ctx.Reply($"extra slot {n} = {ctx.World.ExtraBars.Names(player).ElementAtOrDefault(n - 1) ?? "-"}");
    }

    /// <summary>.extrabar clear [n]: the overrides away (all, or slot n's).</summary>
    [Command("extrabar clear", Level = AccountLevel.GameMaster, Help = "takes your extra bar overrides away (all, or slot [n]'s): the gear's spells come back")]
    public static void Clear(CommandContext ctx, int? n = null)
    {
        var player = ctx.RequirePlayer();
        if (n is { } one)
        {
            if (ctx.World.ExtraBars.SetOverride(player, one - 1, null, ExtraBarChangeSource.Plugin) is { } why) throw new CommandError(why);
        }
        else ctx.World.ExtraBars.ClearOverrides(player);
        ctx.Reply("extra bar overrides cleared");
    }

    /// <summary>.extrabar reload: content/extraspells.json and the packs' server/extraspells.json again; every player's bars rebuilt.</summary>
    [Command("extrabar reload", Level = AccountLevel.GameMaster, Help = "reads extraspells.json (content and packs) again and rebuilds the bars")]
    public static void Reload(CommandContext ctx)
    {
        var table = ctx.World.ExtraBars.Reload();
        ctx.Reply($"{table.Rules.Count} item entries, {table.Bars} bar(s) of {table.Size}" + (table.Problems.Count == 0 ? "" : "\n" + string.Join("\n", table.Problems)));
    }
}
