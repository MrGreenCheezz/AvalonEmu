using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using System.Text.Json;

namespace AlbionLocal.Server.Systems;

public static class TutorialWorld
{
    public static bool BlockedFor(Player player, TutorialBlocker blocker)
    {
        if (player.Character.Tutorial is not { Version: 1 } state) return blocker.Blocked;
        if (blocker.BlockerId == "BLOCKFORGE" && state.Stage >= 20) return false;
        return (JsonSerializer.Deserialize<Dictionary<string, int>>(state.JourneyBlockers) ?? [])
            .TryGetValue(blocker.BlockerId, out var value) ? value != 0 : blocker.Blocked;
    }

    /// <summary>Apply saved obstacle state before visibility announces a populated private map.</summary>
    public static void Prepare(Map map, Character character)
    {
        if (map.TutorialOwner != character.Id || character.Tutorial is not { Version: 1 } state) return;
        var states = JsonSerializer.Deserialize<Dictionary<string, int>>(state.JourneyBlockers) ?? [];
        if (state.Stage >= 20) states["BLOCKFORGE"] = 0;
        foreach (var blocker in map.Objects.Values.OfType<TutorialBlocker>())
            if (states.TryGetValue(blocker.BlockerId, out var value)) blocker.Blocked = value != 0;
    }

    public static void SetBlocker(Player player, string id, int state)
    {
        if (player.Map is not { } map || !TutorialInstances.IsTutorialCluster(map.Cluster)) return;
        foreach (var blocker in map.Objects.Values.OfType<TutorialBlocker>().Where(b => b.BlockerId == id))
        {
            var blocked = state != 0;
            if (map.TutorialOwner == player.Character.Id) blocker.Blocked = blocked;
            // The client upserts this object and applies its new navmesh restriction.
            if (player.Visibility.Known.Contains(blocker))
            {
                var update = blocker.State(player);
                update.dcq = blocked;
                player.Session.Send(update);
            }
        }
    }

    public static void RestoreBlockers(Player player, IReadOnlyDictionary<string, int> states)
    {
        foreach (var (id, state) in states) SetBlocker(player, id, state);
    }
}
