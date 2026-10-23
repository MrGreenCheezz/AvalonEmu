namespace AlbionLocal.Server.Game;

/// <summary>Versioned bounded tutorial state. 0..2 walking, 3 talk, 4 ready to deliver, 5 tower offered,
/// 6..10 tower route, 11 dodge-or-walk, 12 talk, 13 ready to deliver, 14 gathering offered,
/// 15 resources approach, 16 acquire resources, 17 talk, 18 ready to deliver, 19 crafting offered,
/// 20 forge interaction, 21 sword acquisition, 22 inventory, 23 equip, 24 spell choice, 25 talk,
/// 26 crafting delivery pending, 27 crafting delivered.
/// Existing version-1 stages 0..19 keep their checkpoints; 19 now offers the newly supported quest.
/// Immutable so repository snapshots cannot share mutable progress.</summary>
public sealed record OpeningTutorialProgress(int Stage, Guid QuestId, long StartedAt)
{
    public int Version { get; init; } = 1;
    public int TutorialState { get; init; }
    public bool IntroStarted { get; init; }
    public string? JourneyQuest { get; init; }
    public int JourneyTask { get; init; }
    public string JourneyPhase { get; init; } = "offered";
    // JSON dictionaries are replaced rather than mutated: character snapshots share this immutable record.
    public string JourneyCounters { get; init; } = "{}";
    public string JourneyBlockers { get; init; } = "{}";
    public long JourneyOnlineTicks { get; init; }
    public long JourneyTaskTicks { get; init; }
    public string JourneyBranch { get; init; } = "A";
    public string? JourneyGiverCluster { get; init; }
    public Guid JourneyGiver { get; init; }
    public string? JourneyVariables { get; init; }
    public int JourneySlot { get; init; }
}
