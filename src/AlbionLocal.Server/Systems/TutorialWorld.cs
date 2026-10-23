using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using System.Text.Json;

namespace AlbionLocal.Server.Systems;

public static class TutorialWorld
{
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
        if (player.Map is not { TutorialOwner: { } owner } map || owner != player.Character.Id) return;
        foreach (var blocker in map.Objects.Values.OfType<TutorialBlocker>().Where(b => b.BlockerId == id))
        {
            var blocked = state != 0;
            if (blocker.Blocked == blocked) continue;
            blocker.Blocked = blocked;
            // The client upserts this object and applies its new navmesh restriction.
            if (player.Visibility.Known.Contains(blocker)) player.Session.Send(blocker.State());
        }
    }

    public static void RestoreBlockers(Player player, IReadOnlyDictionary<string, int> states)
    {
        foreach (var (id, state) in states) SetBlocker(player, id, state);
    }
}
