namespace AlbionLocal.Server.Entities;

/// <summary>
/// An open bank vault tab (stage 6 SPEC §6b): the container the client got with AttachItemContainer (its Guid, made from the
/// character and the vault), the bank building of the ContainerOpen, the tab, and the saved vault's key in
/// <see cref="Game.Character.Vaults"/>. Its items live while it is open; <see cref="Player.SaveItems"/> writes them back.
/// </summary>
public sealed record Vault(string Key, Container Container, long Building, Guid Tab, double WeightLimit)
{
    /// <summary>
    /// The view slots (ContainerOpen key 1: 0 main, 1 additional) that show this tab: the client may show one tab in both
    /// (C-B2), and the tab stays open until the last of them is closed.
    /// </summary>
    public HashSet<int> Views { get; init; } = [];
}

/// <summary>
/// A station action under way (op 55 ActionOnBuildingStart; stage 6 SPEC §6a.1): what, where, the request's counter
/// (echoed in a failure), its start and end GameTimeStamps, and what a cancel gives back (event 65; nothing is charged for a
/// repair until it finishes, a craft batch refunds the crafts not done). Kept on the player; a move or a cast cancels it.
/// </summary>
public sealed class BuildingAction(Building building, byte act, byte counter, long start, long end)
{
    public Building Building { get; } = building;

    /// <summary>ActionComponentType: 1 CraftItem, 2 RepairItem.</summary>
    public byte Act { get; } = act;

    public byte Counter { get; } = counter;

    public long Start { get; } = start;

    public long End { get; internal set; } = end;

    /// <summary>What the cancel gives back (the refund of the crafts not done); null = nothing.</summary>
    public Action? OnCancel { get; internal set; }

    /// <summary>Settle output whose deadline has already elapsed before deciding what to cancel.</summary>
    public Action? BeforeCancel { get; internal set; }

    /// <summary>Set once it ended (finished or cancelled): a timer that comes later does nothing.</summary>
    public bool Over { get; internal set; }
}
