using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// A small duel tournament (analysis/work/stageN/arena/SPEC.md §6): a GM opens the sign-up (.tournament open), players sign up
/// (.tournament join), the GM starts it (.tournament start): single elimination, seeded by the duel ladder (the best against the worst, a
/// bye for the best when the count is odd). Each pair settles its match with an ordinary duel (the duel system: a challenge and its
/// accept); the first duel won between the two (Duels.Finish → <see cref="DuelWon"/>) or a GM's .tournament win decides it. A round over,
/// the next is paired from its winners in bracket order; the last one wins. Nothing is saved (a restart ends it). Under <see cref="World.Sync"/>.
/// </summary>
public sealed class Tournament(World world)
{
    /// <summary>A pair of a round: both sides (B null = a bye) and the winner once decided.</summary>
    public sealed class Pair(Guid a, Guid? b)
    {
        public Guid A { get; } = a;
        public Guid? B { get; } = b;
        public Guid? Winner { get; set; } = b is null ? a : null;
    }

    private readonly List<(Guid Id, string Name)> _signed = [];

    public bool SignUp { get; private set; }

    /// <summary>The round under way (1 = the first; 0 = none).</summary>
    public int Round { get; private set; }

    public List<Pair> Pairs { get; } = [];

    public bool Running => Round > 0;

    public IReadOnlyList<(Guid Id, string Name)> Signed => _signed;

    private string NameOf(Guid id) => _signed.FirstOrDefault(s => s.Id == id).Name ?? "?";

    /// <summary>.tournament open: a new sign-up (ends a tournament under way).</summary>
    public void Open()
    {
        (SignUp, Round) = (true, 0);
        _signed.Clear();
        Pairs.Clear();
        Announce("A duel tournament is open: sign up with .tournament join.");
    }

    /// <summary>.tournament join / leave of <paramref name="p"/>: null, or why not.</summary>
    public string? Join(Player p)
    {
        if (!SignUp) return "no tournament is open for sign-up";
        if (_signed.Any(s => s.Id == p.Character.Id)) return "you are signed up already";
        _signed.Add((p.Character.Id, p.Character.Name));
        Announce($"{p.Character.Name} signs up for the tournament ({_signed.Count} so far).");
        return null;
    }

    public string? Leave(Player p)
    {
        if (!SignUp) return "the sign-up is closed";
        return _signed.RemoveAll(s => s.Id == p.Character.Id) > 0 ? null : "you are not signed up";
    }

    /// <summary>.tournament start: the first round from the signed-up players (at least 2); null, or why not.</summary>
    public string? Start()
    {
        if (!SignUp) return "open a sign-up first (.tournament open)";
        if (_signed.Count < 2) return "at least two players must sign up";
        SignUp = false;
        var seeded = _signed.OrderByDescending(s => world.Ratings.RatingOf(s.Id, Ratings.Duel)).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s => s.Id).ToList();
        Pairs.Clear();
        Round = 1;
        // The best against the worst; an odd count gives the best a bye.
        var (lo, hi) = (0, seeded.Count - 1);
        if (seeded.Count % 2 == 1) Pairs.Add(new Pair(seeded[lo++], null));
        while (lo < hi) Pairs.Add(new Pair(seeded[lo++], seeded[hi--]));
        AnnounceRound();
        return null;
    }

    /// <summary>A duel ended with a winner (Duels.Finish): the pair of the two in the round under way is decided.</summary>
    public void DuelWon(Player winner, Player loser)
    {
        if (!Running) return;
        var (w, l) = (winner.Character.Id, loser.Character.Id);
        if (Pairs.FirstOrDefault(p => p.Winner is null && (p.A == w && p.B == l || p.A == l && p.B == w)) is not { } pair) return;
        Decide(pair, w);
    }

    /// <summary>.tournament win &lt;name&gt;: a GM decides the open pair of that player for it; null, or why not.</summary>
    public string? SetWinner(string name)
    {
        if (!Running) return "no tournament is under way";
        var id = _signed.FirstOrDefault(s => s.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).Id;
        if (Pairs.FirstOrDefault(p => p.Winner is null && (p.A == id || p.B == id)) is not { } pair) return $"{name} has no open match";
        Decide(pair, id);
        return null;
    }

    public void Cancel()
    {
        if (SignUp || Running) Announce("The tournament is cancelled.");
        (SignUp, Round) = (false, 0);
        Pairs.Clear();
    }

    private void Decide(Pair pair, Guid winner)
    {
        pair.Winner = winner;
        var loser = pair.A == winner ? pair.B : pair.A;
        Announce($"Tournament round {Round}: {NameOf(winner)} beats {(loser is { } l ? NameOf(l) : "nobody")}.");
        if (Pairs.Any(p => p.Winner is null)) return;
        var winners = Pairs.Select(p => p.Winner!.Value).ToList();
        if (winners.Count == 1)
        {
            Announce($"{NameOf(winners[0])} wins the duel tournament!");
            if (world.Parties.Online(winners[0]) is { } champion) world.Vanity.TournamentWon(champion); // stageN vanity
            Log.Info("arena", $"tournament won by '{NameOf(winners[0])}' after {Round} round(s)");
            Round = 0;
            Pairs.Clear();
            return;
        }
        Round++;
        Pairs.Clear();
        for (var i = 0; i < winners.Count; i += 2) Pairs.Add(new Pair(winners[i], i + 1 < winners.Count ? winners[i + 1] : null));
        AnnounceRound();
    }

    private void AnnounceRound()
    {
        Announce($"Tournament round {Round}: " + string.Join("; ", Pairs.Select(p => p.B is { } b ? $"{NameOf(p.A)} vs {NameOf(b)}" : $"{NameOf(p.A)} (bye)"))
            + ". Duel your opponent (challenge it); the winner goes on.");
    }

    /// <summary>.tournament (status): the sign-up or the round's pairs.</summary>
    public string Describe() =>
        SignUp ? $"Tournament sign-up open: {(_signed.Count == 0 ? "nobody yet" : string.Join(", ", _signed.Select(s => s.Name)))}."
        : Running ? $"Tournament round {Round}: " + string.Join("; ", Pairs.Select(p => p.B is { } b
            ? $"{NameOf(p.A)} vs {NameOf(b)}{(p.Winner is { } w ? $" ({NameOf(w)} won)" : "")}" : $"{NameOf(p.A)} (bye)"))
        : "No tournament (a GM opens one with .tournament open).";

    /// <summary>A chat line to every player in the world.</summary>
    internal void Announce(string text)
    {
        foreach (var map in world.Maps.Maps)
            foreach (var p in map.Players.Values)
                Expeditions.Tell(p, text);
        Log.Info("arena", text);
    }
}
