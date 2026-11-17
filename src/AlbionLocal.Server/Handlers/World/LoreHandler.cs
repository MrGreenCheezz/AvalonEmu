using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The lore snippets' operations (analysis/work/stageR/lore/SPEC.md §3, §4); the rules are in <see cref="Systems.LoreSnippets"/>. The claim
/// rides on the client's generic simple action (DoSimpleActionStart / DoSimpleActionCancel, r0.SimpleAction): only ClaimLoreSnippet (11)
/// runs here, any other action is answered as an unhandled operation was before (the empty response, a warning).
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>RequestLoreSnippetStates (op 542, z8 {0 int[]}): the claimed flags of the asked lore.xml indices (aiq {0 int[], 1 bool[]}).</summary>
    [Handles(OperationCodes.RequestLoreSnippetStates, Status = SessionStatus.InWorld)]
    private AlbionResponse? RequestLoreSnippetStates(RequestLoreSnippetStatesRequest req)
    {
        if (Player is not { } player) return new RequestLoreSnippetStatesResponse().FillEmpty();
        return world.Lore.States(player.Character, req.aag);
    }

    /// <summary>
    /// DoSimpleActionStart (op 445, r0 {0 time, 1 counter, 2 action, 3 target object, 4 flag, 5 position, 6 direction, 7, 8}; c32.i 0x180A683E0):
    /// ClaimLoreSnippet starts a claim (<see cref="Systems.LoreSnippets.Start"/>: aes {0 counter, 1 action, 2 end}, an l0 code when refused).
    /// </summary>
    [Handles(OperationCodes.DoSimpleActionStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? DoSimpleActionStart(DoSimpleActionStartRequest req)
    {
        if (Player is { } player && req.ju == Systems.LoreSnippets.ClaimAction) return world.Lore.Start(player, req);
        Log.Warn(Name, $"DoSimpleActionStart action {req.ju} is not run here: sending the empty response");
        return new DoSimpleActionStartResponse().FillEmpty();
    }

    /// <summary>DoSimpleActionCancel (op 446, rz {0 time, 1 counter}): a lore claim of that counter dropped (493), aer {0 counter}; otherwise as before.</summary>
    [Handles(OperationCodes.DoSimpleActionCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? DoSimpleActionCancel(DoSimpleActionCancelRequest req)
    {
        if (Player is { } player && world.Lore.Cancel(player, req.js)) return new DoSimpleActionCancelResponse { aqz = req.js, Extra = { [0] = req.js } };
        return new DoSimpleActionCancelResponse().FillEmpty();
    }
}
