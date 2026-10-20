using System.Numerics;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Commands.Builtin;

/// <summary>Item commands: .item add, .kit (content/kits.json), .drop.</summary>
public static class ItemCommands
{
    /// <summary>Count limit of one stack: items.xml maxstacksize, 999 for nearly every stackable item (22 differ; <see cref="ItemTemplate.MaxStackSize"/>, not applied yet).</summary>
    public const int MaxStack = 999;

    /// <summary>
    /// Creates the item as Join creates saved items (NewSimpleItem, NewJournalItem, or NewEquipmentItem with the default spell choice) and
    /// puts it into the first free inventory slot (<see cref="Network.WorldSession.AddInventoryItem"/>), saved after the
    /// change delay. Equipment does not stack: each one is its own object in its own slot. Quality (1 normal … 5
    /// masterpiece) only exists on equipment.
    /// </summary>
    [Command("item add", Level = AccountLevel.GameMaster, Help = "puts a new item into the first free inventory slot (quality 1-5, equipment only)")]
    public static void Add(CommandContext ctx, ItemTemplate item, int count = 1, int quality = 1)
    {
        var player = ctx.RequirePlayer();
        // Checked before anything is sent or saved: a saved item the client cannot build would come back on every Join.
        if (!item.CanCreate) throw new CommandError($"{item.Name} is a {item.Kind}: only equipment, simple items and journals (_EMPTY, _FULL) can be created yet");
        if (count is < 1 or > MaxStack) throw new CommandError($"count: {count} is not 1..{MaxStack}");
        if (quality is < 1 or > 5) throw new CommandError($"quality: {quality} is not 1..5 (1 normal ... 5 masterpiece)");
        if (item.IsEquipment && quality > item.MaxQuality) throw new CommandError($"quality: {item.Name} has quality 1..{item.MaxQuality} only (maxqualitylevel)");
        var slots = Put(player, item, count, quality);
        if (slots.Count == 0) throw new CommandError("the inventory is full");
        var what = item.IsEquipment ? $"{slots.Count} x {item.Name}{(quality > 1 ? $" (quality {quality})" : "")}" : $"{count} x {item.Name}";
        ctx.Reply($"added {what} to inventory slot{(slots.Count > 1 ? "s" : "")} {string.Join(", ", slots)}"
            + (item.IsEquipment && slots.Count < count ? $"; the inventory is full, {count - slots.Count} not added" : ""));
    }

    /// <summary>
    /// <paramref name="count"/> of <paramref name="item"/> into free inventory slots as .item add puts them: one stack, or one slot
    /// per piece of equipment. The slots filled; fewer than asked (none for a stack) when the inventory is full.
    /// </summary>
    private static List<int> Put(Player player, ItemTemplate item, int count, int quality)
    {
        var slots = new List<int>();
        for (var n = 0; n < (item.IsEquipment ? count : 1); n++)
        {
            if (player.Session.AddInventoryItem(item, item.IsEquipment ? 1 : count, item.IsEquipment ? quality : 1) is not { } slot) break;
            slots.Add(slot);
        }
        return slots;
    }

    /// <summary>The kits of content/kits.json with their items.</summary>
    [Command("kit list", Level = AccountLevel.GameMaster, Help = "lists the item kits of content/kits.json")]
    public static void KitList(CommandContext ctx)
    {
        if (ctx.World.Kits.All is not { Count: > 0 } kits) throw new CommandError("no kits (content/kits.json is missing or empty)");
        ctx.Reply(string.Join("\n", kits.Select(k => $"{k.Name}: {k.Items.Count} item{(k.Items.Count == 1 ? "" : "s")}{(k.Help.Length > 0 ? $" - {k.Help}" : "")}")));
    }

    /// <summary>
    /// Every item of a kit (content/kits.json, Content/Kits) into the inventory, each as <see cref="Add"/> puts it, while free
    /// slots last; the reply counts the items added (a piece of equipment or a stack each) and names what did not fit (or
    /// cannot be created: missing from the item table, or a quality above its maxqualitylevel).
    /// </summary>
    [Command("kit", Level = AccountLevel.GameMaster, Help = "puts the items of a kit (.kit list) into the inventory, as far as free slots go")]
    public static void Kit(CommandContext ctx, string name)
    {
        var player = ctx.RequirePlayer();
        var kit = ctx.World.Kits.Find(name) ?? throw new CommandError(
            $"unknown kit {name}; kits: {(ctx.World.Kits.All is { Count: > 0 } all ? string.Join(", ", all.Select(k => k.Name)) : "none")}");
        var (added, missed) = (0, new List<string>());
        foreach (var entry in kit.Items)
        {
            // Checked at load against the server's item table; a test world's hand-made table may still lack one.
            if (ctx.World.Items.Find(entry.Name) is not { CanCreate: true } item || item.IsEquipment && entry.Quality > item.MaxQuality)
            {
                missed.Add($"{entry.Name} (not creatable)");
                continue;
            }
            // Slots filled: one per piece of equipment, one for the whole stack.
            var slots = Put(player, item, entry.Count, entry.Quality).Count;
            added += slots;
            var left = item.IsEquipment ? entry.Count - slots : slots == 0 ? entry.Count : 0;
            if (left > 0) missed.Add($"{left} x {entry.Name}");
        }
        if (added == 0 && missed.Count > 0) throw new CommandError($"kit {kit.Name}: nothing added; the inventory is full ({missed.Count} entr{(missed.Count == 1 ? "y" : "ies")} left)");
        ctx.Reply($"kit {kit.Name}: added {added} item{(added == 1 ? "" : "s")}"
            + (missed.Count > 0 ? $"; did not fit: {string.Join(", ", missed)}" : ""));
    }

    /// <summary>The mob whose bag .drop makes (NewLoot keys 1-3: the bag's look and title); any mob of mobs.xml would do.</summary>
    public const string DropMob = "T4_MOB_UNDEAD_SOLDIER";

    /// <summary>How near an earlier .drop bag must lie for the next .drop to go into it (m).</summary>
    public const float DropMerge = 3;

    /// <summary>
    /// <paramref name="count"/> of <paramref name="item"/> (same rules as .item add) as a mob's loot bag at the player's feet (<see cref="LootBag"/>,
    /// NewLoot 98) that only this player may open (no party), for the loot window checks (content/checks.json LOOT-01/02). A .drop within
    /// <see cref="DropMerge"/> m of the player's own earlier .drop bag adds to it: that bag goes (Leave) and one with all its items comes in
    /// its place. The bag lives Combat:LootBagSeconds like a mob's.
    /// </summary>
    [Command("drop", Level = AccountLevel.GameMaster, Help = "drops an item as a loot bag at your feet only you may loot (quality 1-5, equipment only); repeated calls add to it")]
    public static void Drop(CommandContext ctx, ItemTemplate item, int count = 1, int quality = 1)
    {
        var player = ctx.RequirePlayer();
        var world = ctx.World;
        if (!item.CanCreate) throw new CommandError($"{item.Name} is a {item.Kind}: only equipment, simple items and journals (_EMPTY, _FULL) can be created yet");
        if (count is < 1 or > MaxStack) throw new CommandError($"count: {count} is not 1..{MaxStack}");
        if (quality is < 1 or > 5) throw new CommandError($"quality: {quality} is not 1..5 (1 normal ... 5 masterpiece)");
        if (item.IsEquipment && quality > item.MaxQuality) throw new CommandError($"quality: {item.Name} has quality 1..{item.MaxQuality} only (maxqualitylevel)");
        if (player.Map is not { Grid: not null } map) throw new CommandError("the player is on no loaded map");
        var mobs = world.Clusters?.MobsIfLoaded ?? throw new CommandError("mobs.xml is not loaded yet (it comes with the first cluster's data)");
        var at = player.PositionAt(DateTime.UtcNow);
        var items = new List<Item>();
        var old = map.Objects.Values.OfType<LootBag>().Where(b => b.Mob.Spawn.Key.Slot == "gmdrop" && b.Looters is [var only] && only == player.Character.Id
            && Vector2.Distance(b.Position, at) <= DropMerge).MinBy(b => Vector2.Distance(b.Position, at));
        if (old is not null)
        {
            items.AddRange(old.Items.Items);
            for (var slot = 0; slot < old.Items.Size; slot++) old.Items.Take(slot);
            Combat.RemoveBag(old, "a .drop adds to it");
        }
        if (item.IsEquipment)
            for (var i = 0; i < count; i++) items.Add(player.Session.NewItem(item, 1, quality));
        else items.Add(player.Session.NewItem(item, count));
        var template = mobs.Find(DropMob) ?? throw new CommandError($"mobs.xml has no {DropMob}");
        var at3 = old?.Position ?? at;
        var point = new MobSpawnPoint
        {
            Key = new SpawnKey(map.Cluster, "gmdrop", map.CustomSpawns++), Tile = "", Position = at3, Direction = player.Facing,
            Component = new XElement("mobspawnpoint", new XAttribute("mobname", template.Name)), Mob = template.Name, Template = template,
        };
        // The dead mob of NewLoot key 2: never on the map (a mob's corpse has gone by the time its bag is looted too).
        var mob = new Mob(world.ObjectIds.Next(), point, template, 0, world.GameNow, world.Now) { World = world };
        var container = new Container(Guid.NewGuid(), items.Count);
        for (var i = 0; i < items.Count; i++) container.Put(i, items[i]);
        var bag = new LootBag(world.ObjectIds.Next(), mob, container, [player.Character.Id], 1, world.GameNow);
        map.AddObject(bag);
        map.Timers.At(world.Clock.Now + world.Combat.LootBagTime, () => Combat.RemoveBag(bag, "its time is up"));
        ctx.Reply($"loot bag (object {bag.Id}) with {string.Join(", ", items.Select(i => $"{i.Count} x {i.Template.Name}{(i.Quality > 1 ? $" q{i.Quality}" : "")}"))}"
            + (old is null ? "" : " (added to the bag here)"));
    }
}
