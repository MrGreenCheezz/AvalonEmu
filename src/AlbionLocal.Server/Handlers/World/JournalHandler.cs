using AlbionLocal.Photon;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>The Albion Journal tracker (ops-audit SPEC §3.4; the journal itself, progress and rewards, is SPEC §5).</summary>
public sealed partial class WorldSession
{
    /// <summary>The categories and the number of the achievements a character that tracks nothing is shown (PvE onboarding).</summary>
    public static readonly string[] SuggestedJournalCategories = ["TUTORIAL", "GATHERING", "PVE"];

    public const int SuggestedJournalCount = 4;

    /// <summary>Whether this connection has had its TrackAlbionJournalAchievements yet (the one sent right after the Join).</summary>
    private bool _journalTracked;

    /// <summary>
    /// TrackAlbionJournalAchievements (op 515, aby: 0 string[] the tracked uniquenames). gcq.cs 0x181c9d9a0 sends the client's
    /// list at every Join (~0.4 s after event 155) and gcq.aq/as again whenever the player pins or unpins one; no response
    /// class. Officially every request gets event JournalAchievementProgressUpdate (600, arc) within 0.12 s: key 0 = 0 (the
    /// full list: the client clears its tracker and adds these), 1 the names (unknown ones skipped by the client), 2 their
    /// progress, parallel (a shorter key 2 throws). The answer: the request's list as it came, except for the first request
    /// of a connection when it is [] (a fresh client): then the character's saved list or, when it never had one, the
    /// suggestions (<see cref="Data.JournalCatalog.Suggested"/>); progress 0 for all, there is no journal progress yet. The
    /// list is kept with the character, so a relog shows it again and an unpinned-to-empty tracker stays empty. A Join
    /// before albionjournal.bin is in (a cold start, read alongside the first cluster) gets [] now and the suggestions as
    /// a second 600 once it is (<see cref="SuggestWhenLoaded"/>). Key 0 is 0, so it is left out on the wire (zero scalars are).
    /// </summary>
    [Handles(OperationCodes.TrackAlbionJournalAchievements, Status = SessionStatus.InWorld)]
    private AlbionResponse? TrackAlbionJournalAchievements(TrackAlbionJournalAchievementsRequest req)
    {
        if (Player is not { } player) return null;
        if (Systems.OpeningTutorial.Active(player))
        {
            Systems.OpeningTutorial.TrackerReady(player);
            return null;
        }
        var c = player.Character;
        var asked = req.ag3 ?? [];
        var first = !_journalTracked;
        _journalTracked = true;
        // No albionjournal.bin (null): nothing to suggest, and nothing kept, so the next Join suggests again.
        string[]? tracked = asked.Length > 0 || !first ? [.. asked]
            : c.JournalTracked is { } saved ? [.. saved]
            : world.Clusters?.JournalIfLoaded?.Suggested(SuggestedJournalCount, SuggestedJournalCategories);
        if (tracked is null)
        {
            tracked = [];
            if (world.Clusters is { } store) SuggestWhenLoaded(store, c);
        }
        else
        {
            c.JournalTracked = tracked;
            world.Saves.MarkDirty(c); // the periodic save (or Ctrl+C's) is soon enough for a pin
        }
        SendEvent(new JournalAchievementProgressUpdateEvent { cme = 0, cmf = tracked, cmg = Systems.AlbionJournal.ProgressOf(world, c, tracked) });
        // The Join's request: the badge of rewards to claim (601), when there are any (PVE-DESIGN step 11).
        if (first && Systems.AlbionJournal.Catalog(world) is { } journal && Systems.AlbionJournal.Claimable(world, c, journal).Count is var claimable and > 0)
            SendEvent(new JournalClaimableRewardUpdateEvent { cmh = claimable });
        Log.Debug(Name, $"journal tracker: {tracked.Length} achievement(s){(asked.Length == 0 && first ? " (saved or suggested)" : "")}");
        return null;
    }

    /// <summary>
    /// TrackAlbionJournalAchievementSubCategory (op 516, abz {0 category, 1 subcategory}; stageH journals SPEC §B.2): the tracker shows
    /// that subcategory's first unfinished achievements (at most <see cref="SuggestedJournalCount"/>), answered as 515 (600 kind 0).
    /// </summary>
    [Handles(OperationCodes.TrackAlbionJournalAchievementSubCategory, Status = SessionStatus.InWorld)]
    private AlbionResponse? TrackAlbionJournalAchievementSubCategory(TrackAlbionJournalAchievementSubCategoryRequest req)
    {
        if (Player is not { } player || world.Clusters?.JournalIfLoaded is not { } journal) return null;
        var c = player.Character;
        string[] tracked = [.. journal.Achievements.Where(a => a.Subcategory == req.ag5 && c.Goals?.Done.ContainsKey(a.Name) != true)
            .Take(SuggestedJournalCount).Select(a => a.Name)];
        c.JournalTracked = tracked;
        world.Saves.MarkDirty(c);
        SendEvent(new JournalAchievementProgressUpdateEvent { cme = 0, cmf = tracked, cmg = Systems.AlbionJournal.ProgressOf(world, c, tracked) });
        return null;
    }

    /// <summary>
    /// GetCharacterSteamAchievements (op 144, tc / ae4 {0 character name}; SPEC §B.2): the Journal window's state, the own character's
    /// (key 0 empty or its name) or another one's in game (CharacterStatsGui); an unknown name gets the empty lists. See
    /// <see cref="Systems.AlbionJournal.Achievements"/>.
    /// </summary>
    [Handles(OperationCodes.GetCharacterSteamAchievements, Status = SessionStatus.InWorld)]
    private GetCharacterSteamAchievementsResponse GetCharacterSteamAchievements(GetCharacterSteamAchievementsRequest req)
    {
        var name = req.mc ?? "";
        var c = Player?.Character is { } own && (name.Length == 0 || own.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ? own
            : world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => p.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Character;
        return Systems.AlbionJournal.Achievements(world, c, name);
    }

    /// <summary>ClaimAlbionJournalReward (op 514, qs / aeg {0 reward ids}; SPEC §B.6): the rewards, then {0 one result per id}.</summary>
    [Handles(OperationCodes.ClaimAlbionJournalReward, Status = SessionStatus.InWorld)]
    private ClaimAlbionJournalRewardResponse ClaimAlbionJournalReward(ClaimAlbionJournalRewardRequest req) =>
        new() { aoh = Player is { } player ? Systems.AlbionJournal.Claim(player, req.fi ?? []) : [.. (req.fi ?? []).Select(_ => Systems.AlbionJournal.InvalidReward)] };

    /// <summary>
    /// The suggestions for <paramref name="c"/>'s first Join once albionjournal.bin is read (null: none, missing or broken),
    /// on the world thread: only while this connection still plays it and nothing has been tracked meanwhile (a pin or an
    /// unpin sets the list), so a later request is never overwritten.
    /// </summary>
    private void SuggestWhenLoaded(Data.ClusterStore store, Game.Character c) =>
        store.LoadJournalAsync().ContinueWith(task =>
        {
            if (task.Result is not { } journal) return;
            world.Post(() =>
            {
                if (Player?.Character != c || c.JournalTracked is not null) return;
                var tracked = journal.Suggested(SuggestedJournalCount, SuggestedJournalCategories);
                c.JournalTracked = tracked;
                world.Saves.MarkDirty(c);
                SendEvent(new JournalAchievementProgressUpdateEvent { cme = 0, cmf = tracked, cmg = Systems.AlbionJournal.ProgressOf(world, c, tracked) });
                Log.Debug(Name, $"journal tracker: {tracked.Length} achievement(s) suggested once albionjournal.bin was in");
            });
        }, TaskScheduler.Default);
}
