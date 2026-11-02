using AlbionLocal.Photon;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Use flags, tracked Destiny nodes and auto-learn choices. These requests do not await operation responses.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// ChangeUseFlag (op 298, qm {0 UseFlag, 1 bool}): the craft window's "use focus" (1 AutoUseActionFocusForCrafting), the destiny board's
    /// learning point toggle (0 AutoUseLearningPoints) or the respec silver one (2), sent debounced when the wanted state differs from the
    /// one last sent (ctp.af_43). The bit is set (key 1 left out = false), saved with the character and sent back in the next Join (keys 32,
    /// 99, 100). Another flag: logged, nothing changes. No response.
    /// </summary>
    [Handles(OperationCodes.ChangeUseFlag, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChangeUseFlag(ChangeUseFlagRequest req)
    {
        if (Player is not { } player) return null;
        if (req.e7 is < 0 or > 2)
        {
            Log.Info(Name, $"'{player.Character.Name}' ChangeUseFlag {req.e7}: no such flag");
            return null;
        }
        var c = player.Character;
        c.UseFlags = req.e8 ? c.UseFlags | (1 << req.e7) : c.UseFlags & ~(1 << req.e7);
        world.Saves.MarkChanged(c);
        Log.Info(Name, $"'{c.Name}' use flag {(UseFlagName)req.e7} {(req.e8 ? "on" : "off")}");
        return null;
    }

    /// <summary>
    /// 345319: cs6.s (RVA C00390) flushes Dictionary&lt;int,bool&gt; as parallel ID/state arrays through A67340 (op 311).
    /// BFD850 excludes completed and tracking="forced" nodes. Event 154 replaces the entire tracked set (consumer BFA6D0).
    /// These are deltas, not two add/remove lists. No operation response is awaited.
    /// </summary>
    [Handles(OperationCodes.TrackAchievements, Status = SessionStatus.InWorld)]
    private AlbionResponse? TrackAchievements(TrackAchievementsRequest req)
    {
        if (Player is not { } player || player.Character.Progression is not { } progression || world.GameData?.Board is not { } board)
            return null;
        if (req.ag1 is not { } nodes || req.ag2 is not { } states || nodes.Length != states.Length || nodes.Length > board.Nodes.Count)
        {
            player.Session.Send(Destiny.Tracked(player.Id, progression, board));
            return null;
        }
        var selected = new SortedSet<int>(progression.TrackedNodes.Where(id => board.InProgress(progression, id)));
        var accepted = false;
        for (var i = 0; i < nodes.Length; i++)
        {
            var id = nodes[i];
            if (!board.InProgress(progression, id) || board.Nodes[id].ForcedTracking) continue;
            accepted = true;
            if (states[i]) selected.Add(id); else selected.Remove(id);
        }
        if (accepted && (progression.TrackedIds is null || !progression.TrackedIds.SequenceEqual(selected)))
        {
            progression.TrackedIds = [.. selected];
            world.Saves.MarkChanged(player.Character);
        }
        // Also acknowledge no-op/replayed deltas and restore the authoritative flags after rejected entries.
        player.Session.Send(Destiny.Tracked(player.Id, progression, board));
        return null;
    }

    /// <summary>
    /// SetAchievementsAutoLearn (op 312, aa3 {0 int[] add, 1 int[] remove, 2 byte LearnGroupType mask, 3 byte[] per category, 4 int[][] per
    /// group}; stageH ops-audit SPEC §5): the auto-learn nodes become (old ∪ key 0) \ key 1, keys 2-4 as sent (3 and 4 kept when left out),
    /// saved; the next Join's FullAutoLearnAchievementInfo (155) and FullAchievementInfo key 5 show them. No response.
    /// </summary>
    [Handles(OperationCodes.SetAchievementsAutoLearn, Status = SessionStatus.InWorld)]
    private AlbionResponse? SetAchievementsAutoLearn(SetAchievementsAutoLearnRequest req)
    {
        if (Player is not { } player) return null;
        var c = player.Character;
        var old = c.AutoLearn;
        var ids = new SortedSet<int>(old?.Ids ?? []);
        ids.UnionWith(req.afq ?? []);
        ids.ExceptWith(req.afr ?? []);
        c.AutoLearn = new AutoLearnSet([.. ids], req.afs, req.aft ?? old?.Categories, req.afu ?? old?.PerGroup);
        world.Saves.MarkChanged(c);
        Log.Info(Name, $"'{c.Name}' auto-learn: {ids.Count} node(s){(ids.Count > 0 ? $" ({string.Join(", ", ids.Take(8))}{(ids.Count > 8 ? ", …" : "")})" : "")}");
        return null;
    }

    /// <summary>The client's UseFlag (qm.UseFlag), for the log.</summary>
    private enum UseFlagName { AutoUseLearningPoints, AutoUseActionFocusForCrafting, AutoUseSilverForReSpecPointGainOnMaxLevel }
}
