using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Protocol;
using System.Runtime.CompilerServices;

namespace AlbionLocal.Server.Systems;

/// <summary>The installed client renders the fixed and local rotating calendar from its own catalogue.
/// Publication alone does not grant mail/items, apply bonuses or activate world-spawn flags.</summary>
public static class Festivities
{
    private sealed class Publication
    {
        public bool Initialized;
        public long LastNow, NextBoundary;
        public IReadOnlyList<FestivitiesData.Festivity> Rows = [];
    }
    private static readonly ConditionalWeakTable<World, Publication> Publications = new();

    /// <summary>345319 dev.c, AE6AE0: event 518 replaces the whole list. Kind 0 resolves key 2 in
    /// fixedevents; nonzero kinds resolve category key 1 first. Keys 3/4 are UTC GameTimeStamps.</summary>
    public static FestivitiesUpdateEvent Update(World world)
        => Event(world.Festivities.PublishedAt(world.GameNow));

    private static FestivitiesUpdateEvent Event(IReadOnlyList<FestivitiesData.Festivity> rows) => new()
        {
            b97 = [.. rows.Select(r => r.Kind)], // 0=fixed, 1=automated, 2=daily; 345319 7ADC49/7AE209.
            b98 = [.. rows.Select(r => r.Category)],
            b99 = [.. rows.Select(r => r.Name)],
            caa = [.. rows.Select(r => r.StartUtc)], cab = [.. rows.Select(r => r.EndUtc)],
        };

    public static void Joined(Player player) => player.Session.Send(Update(player.World));

    /// <summary>Clock boundaries publish a replacement snapshot to current clients, without a relog.
    /// Pure cached calendar evaluation; an earlier GameNow also refreshes after a development clock rewind.</summary>
    public static void Tick(World world)
    {
        var now = world.GameNow;
        var state = Publications.GetValue(world, _ => new());
        if (state.Initialized && now >= state.LastNow && now < state.NextBoundary) { state.LastNow = now; return; }
        var rows = world.Festivities.PublishedAt(now);
        if (!state.Initialized || !rows.SequenceEqual(state.Rows))
            foreach (var player in world.Maps.Maps.SelectMany(m => m.Players.Values).DistinctBy(p => p.Character.Id))
                if (player.Session.Player == player) player.Session.Send(Event(rows));
        state.Rows = rows;
        state.NextBoundary = world.Festivities.NextChangeAt(now);
        state.LastNow = now;
        state.Initialized = true;
    }
}
