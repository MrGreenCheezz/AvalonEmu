using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

public sealed partial class Arenas
{
    // The installed 345319 bbe constructor (0x1805754F2) sets the arena entering duration to five seconds;
    // cur.f5 (0x1809C6F93) uses that duration for the local bar. Completion is server event 269, not a new JoinArenaMatch request.
    private static readonly TimeSpan EnteringDuration = TimeSpan.FromSeconds(5);
    private const float EnteringCancelDistance = 0.5f;
    private sealed record Entering(Player Player, ArenaMatch Match, string Cluster, TimeSpan Due, Vector2 From, float Health);
    private readonly Dictionary<Guid, Entering> _entering = [];

    /// <summary>The player explicitly clicked Join: start the server-owned channel, preserving the original ready-check deadline.</summary>
    public string? StartEntering(Player p)
    {
        var match = MatchOf(p.Character.Id);
        var why = match is not { State: ArenaState.ReadyCheck, Closed: false } ? "no open arena invitation"
            : world.Now >= match.Due ? "the arena invitation expired"
            : match.Accepted.Contains(p.Character.Id) ? "already accepted"
            : p.Map is null || p.Session.Status != SessionStatus.InWorld ? "not in the world"
            : p.Character.DungeonReturn is not null ? "inside an instance"
            : p.Combat.Down is not null ? "knocked down"
            : p.Combat.InCombat ? "in combat" : null;
        if (why is not null)
        {
            if (!CancelEntering(p, why)) p.SendAround(new EnteringArenaCancelEvent { ddn = p.Id });
            return why;
        }
        if (_entering.TryGetValue(p.Character.Id, out var existing))
        {
            if (existing.Player == p && existing.Match == match) return null;
            CancelEntering(existing.Player, "replaced by a new arena invitation");
        }
        _entering[p.Character.Id] = new Entering(p, match!, p.Map!.Cluster, world.Now + EnteringDuration,
            p.PositionAt(DateTime.UtcNow), p.Health.At(world.GameNow));
        p.SendToObservers(new EnteringArenaStartEvent { ddn = p.Id });
        Log.Info("arena", $"'{p.Character.Name}' arena Join channel started for {match}; 5 s, invitation deadline preserved");
        return null;
    }

    /// <summary>Stop a pending channel without accepting or dropping the still-open invitation.</summary>
    public bool CancelEntering(Player p, string why)
    {
        if (!_entering.Remove(p.Character.Id, out var channel)) return false;
        channel.Player.SendAround(new EnteringArenaCancelEvent { ddn = channel.Player.Id });
        Log.Info("arena", $"'{channel.Player.Character.Name}' arena Join channel cancelled ({why})");
        return true;
    }

    private void CancelEntering(ArenaMatch match, string why)
    {
        foreach (var channel in _entering.Values.Where(channel => channel.Match == match).ToList())
            CancelEntering(channel.Player, why);
    }

    private void UpdateEntering(TimeSpan now)
    {
        foreach (var channel in _entering.Values.ToList())
        {
            var p = channel.Player;
            var why = channel.Match.Closed || channel.Match.State != ArenaState.ReadyCheck || MatchOf(p.Character.Id) != channel.Match ? "invitation ended"
                : now >= channel.Match.Due ? "invitation expired"
                : Online(p.Character.Id) != p || p.Session.Player != p || p.Session.Status != SessionStatus.InWorld ? "player left the world"
                : p.Map?.Cluster != channel.Cluster ? "changed cluster"
                : p.Combat.Down is not null ? "knocked down"
                : p.Health.At(world.GameNow) < channel.Health - 0.5f ? "damaged"
                : p.Combat.InCombat ? "entered combat"
                : Vector2.Distance(p.PositionAt(DateTime.UtcNow), channel.From) > EnteringCancelDistance ? "moved" : null;
            if (why is not null) CancelEntering(p, why);
            else if (now >= channel.Due)
            {
                _entering.Remove(p.Character.Id);
                // The user completed the real Join channel. Finish the native action before the normal acceptance and transfer events.
                p.SendAround(new EnteringArenaCancelEvent { ddn = p.Id, b5h = true });
                Log.Info("arena", $"'{p.Character.Name}' arena Join channel completed; accepting {channel.Match}");
                Accept(p);
            }
        }
    }
}
