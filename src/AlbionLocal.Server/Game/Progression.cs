namespace AlbionLocal.Server.Game;

/// <summary>
/// FullAchievementInfo (event 150) keys 1-5; key 0 is the player object id. Keys 2-5 are parallel.
/// Never null: the client iterates every array.
/// </summary>
public sealed record FullAchievementArrays(int[] Completed, int[] Ids, int[] Levels, bool[] Flags4, bool[] Flags5);

/// <summary>
/// Destiny board state of one character: finished achievements and the level of each achievement in
/// progress (ids = 0-based order in achievements.xml, b74.v 0x180822ad0). Persisted with the character.
/// </summary>
public sealed class Progression
{
    private List<int> _completed = [];
    private SortedDictionary<int, int> _levels = [];
    private SortedDictionary<int, long> _progress = [];

    /// <summary>Finished achievements in completion order (FullAchievementInfo key 1). Never null.</summary>
    public List<int> Completed { get => _completed; set => _completed = value ?? []; }

    /// <summary>Level by achievement id (keys 2/3; the official server sends the ids ascending). Never null.</summary>
    public SortedDictionary<int, int> Levels { get => _levels; set => _levels = value ?? []; }

    /// <summary>
    /// Fame inside the current level by achievement id, a7m raw (PROGRESSION.md §3: v; plain nodes: the fame since they
    /// became available); only nodes in progress with v &gt; 0. FullAchievementProgressInfo (153) at Join. Never null.
    /// </summary>
    public SortedDictionary<int, long> Progress { get => _progress; set => _progress = value ?? []; }

    /// <summary>The last fame-fed node selected for the Destiny Board HUD; null until a gain selects one.</summary>
    public int? Tracked { get; set; }

    /// <summary>
    /// Everything unlocked: the 30 plain achievements finished and every template achievement at its base
    /// level count (100 for all 674 in this build), as analysis/work/unlocks/payload_unlock_all.json.
    /// </summary>
    public static Progression UnlockAll(GameData data)
    {
        var p = new Progression { Completed = [.. data.UnlockAll.Completed] };
        foreach (var id in data.UnlockAll.Templates)
            p.Levels[id] = data.Achievements[id].BaseLevels;
        return p;
    }

    /// <summary>Whether this progression holds at least everything <see cref="UnlockAll"/> unlocks.</summary>
    public bool UnlocksAll(GameData data) =>
        data.UnlockAll.Completed.All(Completed.Contains)
        && data.UnlockAll.Templates.All(id => Levels.TryGetValue(id, out var l) && l >= data.Achievements[id].BaseLevels);

    public void Complete(int id)
    {
        if (!Completed.Contains(id)) Completed.Add(id);
    }

    public void SetLevel(int id, int level) => Levels[id] = level;

    public Progression Clone() => new() { Completed = [.. Completed], Levels = new(Levels), Progress = new(Progress), Tracked = Tracked };

    /// <summary>
    /// Keys 1-5 of FullAchievementInfo; key 4 (the tracked flag, a38+0x28) selects the HUD node, key 5 (the auto-learn flag, cs7+0x29) true for the
    /// in-progress ids in <paramref name="autoLearn"/> (stageJ focus-lp SPEC §5.3; null = none).
    /// </summary>
    public FullAchievementArrays BuildFullAchievementInfo(IReadOnlyCollection<int>? autoLearn = null) =>
        new([.. Completed], [.. Levels.Keys], [.. Levels.Values], [.. Levels.Keys.Select(id => id == Tracked)],
            [.. Levels.Keys.Select(id => autoLearn?.Contains(id) == true)]);
}
