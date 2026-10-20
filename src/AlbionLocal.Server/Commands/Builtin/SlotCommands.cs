using System.Globalization;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>The spell in a slot of the equipped items (<see cref="SlotSpells"/>, the plugins' SetSlotSpell): .slot list, .slot &lt;slot&gt; &lt;spell&gt; [any] [seconds].</summary>
public static class SlotCommands
{
    /// <summary>.slot list: every slot with its item, its spell now and the item's options.</summary>
    [Command("slot list", Level = AccountLevel.GameMaster, Help = "your slots: the spell in each and the options of its item")]
    public static void List(CommandContext ctx)
    {
        var player = ctx.RequirePlayer();
        var spells = ctx.World.Spells ?? throw new CommandError("no spell data");
        var lines = new List<string>();
        foreach (var name in SlotSpells.SlotOptions)
        {
            var slot = SlotSpells.Parse(name)!.Value;
            if (SlotSpells.Locate(player, slot) is not { } at || player.Inventory.Equipment[at.Equipment] is not { } item) continue;
            if (at.Index >= 0 && at.Index >= (at.Active ? item.Active : item.Passive).Length) continue;
            var now = spells.Name(SlotSpells.Current(player, slot)) ?? "-";
            var options = SlotSpells.Options(player, slot).Select(id => spells.Name(id) ?? "?").ToList();
            var temporary = item.Temporary?.ContainsKey((at.Active, at.Index)) == true ? " (temporary)" : "";
            lines.Add($"{name} [{item.Template.Name}]: {now}{temporary}; options: {(options.Count == 0 ? "-" : string.Join(", ", options))}");
        }
        ctx.Reply(lines.Count == 0 ? "nothing equipped that holds a spell" : string.Join("\n", lines));
    }

    /// <summary>
    /// .slot &lt;slot&gt; &lt;spell&gt; [any] [seconds]: puts the spell into the slot (Q W E R D F, Potion, Food, WeaponPassive, ArmorPassive, … as .slot list
    /// shows them). Without "any" only an option of the equipped item; with it any spell of the slot's kind (experimental). With seconds the
    /// previous spell comes back after that long.
    /// </summary>
    [Command("slot", Level = AccountLevel.GameMaster, Help = "puts <spell> into your <slot> (Q W E R D F Potion Food …Passive); 'any' = not only the item's options; [seconds] = temporary")]
    public static void Set(CommandContext ctx, string slot, string spell, string? a = null, string? b = null)
    {
        var player = ctx.RequirePlayer();
        var which = SlotSpells.Parse(slot) ?? throw new CommandError($"unknown slot '{slot}' (one of {string.Join(" ", SlotSpells.SlotOptions)}; .slot list)");
        var mode = SlotSpellMode.FromItemList;
        double? seconds = null;
        foreach (var word in new[] { a, b }.OfType<string>())
        {
            if (word.Equals("any", StringComparison.OrdinalIgnoreCase)) mode = SlotSpellMode.Any;
            else if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0) seconds = s;
            else throw new CommandError($"'{word}' is neither 'any' nor a number of seconds above 0");
        }
        var why = SlotSpells.Set(player, which, spell.ToUpperInvariant(), mode, seconds is { } x ? TimeSpan.FromSeconds(x) : null, SlotSpellCooldown.Keep);
        if (why is not null) throw new CommandError(why);
        ctx.Reply($"{SlotSpells.NameOf(which)} = {ctx.World.Spells?.Name(SlotSpells.Current(player, which))}{(mode == SlotSpellMode.Any ? " (any)" : "")}"
            + (seconds is { } t ? string.Create(CultureInfo.InvariantCulture, $", back to the previous spell in {t:0.#} s") : ""));
    }
}
