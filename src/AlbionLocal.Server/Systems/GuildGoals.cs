using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The guild goals (stage I, analysis/work/stageI/guild-goals/SPEC.md phases 1 to 4; settings <see cref="GuildGoalSettings"/>, content/goals.json
/// Guild), kept on each guild (<see cref="Guild.Progress"/>, saved with its account every 30 s).
/// <para>
/// Phase 1, the Guild Challenge of the client's Guild Season window: every Adventurer's Challenge point a member earns (<see cref="Seasons.Gain"/>)
/// gives its guild ChallengeFactor challenge points (crafts CraftChallengeFactor per fame point, which the personal season does not pay); the
/// level is the last row of the season's table (the client's own, gvgseasons) the total has reached, and each level reached adds its
/// seasonpoints to the guild's season points (activity ChallengeLevelUp, never × SeasonPointsFactor: the client's popup prints the table's
/// value). A level-up is a guild line, the journal's reachguildchallengelevel for the members in game (the others at their login), and during
/// a client season the client's own popup (GameEvent 162 type 40) for a member whose client got the season's GvgSeasonUpdate (351, after every
/// Join). The window asks GetGuildChallengePoints (390, <see cref="ChallengePoints"/>) and GetGvgSeasonRank (152, <see cref="Rank"/>). The
/// season is the client's (gvgseasons_&lt;Region&gt; with its times) while one runs, then local Guild:LocalSeasonDays seasons (chat only: the
/// client's window has no season to show).
/// </para>
/// <para>
/// Phase 2, the weekly goals (Guild:Weekly, preset): at a guild's first activity of a week (Monday at Bounties:ResetUtc) GoalsPerWeek goals
/// are rolled from the pools (one per pool while pools are left, seeded by guild and week, so a restart rolls the same), of the tier the
/// members played at last week, targets scaled by the active members. A finished goal pays once: GoalSilver × tier / 4 into the guild
/// account, GoalXp guild experience, GoalSeasonPoints × SeasonPointsFactor season points (activity ChallengeLevelUp, the window's "Guild
/// Challenge" row) and one blessing step for next week (+ fame, + mob silver, + yield for every member, at most MaxBlessing, applied
/// through <see cref="Guilds.FameBonus"/> and its siblings). The members' fame per kind goes to their Member Logs (309,
/// <see cref="MemberStats"/>): this week, last week, all time.
/// </para>
/// <para>
/// Phase 3, the ranking: a boss kill gives the guilds credited Guild:BossSeasonPoints (world boss, boss, miniboss) × SeasonPointsFactor
/// season points split between them (activity KillMob), and each member credited the client's "+N Season Points" with its chat line
/// (ReceivedGvgSeasonPoints 432 {0 its own object id, 1 the gain, 2 the mob type}). The window's Rankings tab lists the local guilds
/// (GetGvgSeasonRankings 151, <see cref="Rankings"/>), its Overview the points by activity (447, <see cref="Contribution"/>). A season that
/// ended (at the tick that sees the new one, the first tick after a downtime across it, or a GM's .guildgoal season end) is paid once
/// (<see cref="GuildSeasonState.Closed"/>): Guild:BracketSilver of the guild's bracket into its account, Guild:BracketItems and the journal's
/// gotguildmemberreward to every member who gave more than 0 and at least MinParticipation of the members' average challenge points or
/// guild might (in game at once, the others at their next login: <see cref="GuildProgress.Owed"/>; by mail when the guild is deleted
/// first), a line to the guild and the top three to everyone in game; the new season starts at 0.
/// </para>
/// <para>
/// Phase 4, the guild might (the client's Guild Might tab): the might the client data gives a member (pvpchallenge's objectives: PvE kill
/// fame × fametomightfactor, a third in a solo random dungeon; gathering and fishing fame likewise; a corrupted dungeon's objective might;
/// nothing in an orange-warning zone) × Guild:MightFactor goes to the season's category that maps the objective (PVE, GATHERING,
/// CORRUPTED …), summed per guild (GetGuildMightCategoryOverview 450, <see cref="MightOverview"/>; the members' list 449,
/// <see cref="MightContribution"/>). The level is the progression rows reached (the client's own table); each row reached adds its
/// seasonpoints (activity MightCategoryLevelUp@CATEGORY, no factor: the client's popup prints the table's value), a guild line and during a
/// client season the client's popup (GameEvent 162 type 68). The Conqueror's Challenge (personal might, op 451) is not modelled; the might
/// here exists only on the guild. Touched only under <see cref="World.Sync"/>.
/// </para>
/// </summary>
public sealed class GuildGoals(World world)
{
    /// <summary>The seasons a guild keeps (the current one and the ones before it, for .guild season and a later ranking of past seasons).</summary>
    public const int SeasonsKept = 4;

    /// <summary>GameEvent (162) key 0 a4s and key 1 a4t of the level popup (GameEventReceiver.SendPopupMessage type 40, c3d.cl kind 4 = popups).</summary>
    public const int PopupType = 40, PopupKind = 4;

    /// <summary>The GvgSeasonActivity of the level-ups and the weekly goals (the Overview's "Guild Challenge" row; an unknown name is a row without a label).</summary>
    public const string ChallengeLevelUp = "ChallengeLevelUp";

    /// <summary>The GvgSeasonActivity of the boss kills ("Killing Mobs") and the prefix of a might category's level-ups ("Might: {0}", but.z splits at '@').</summary>
    public const string KillMob = "KillMob", MightLevelUp = "MightCategoryLevelUp@";

    /// <summary>GameEvent (162) key 0 of the might level popup (SendPopupMessage type 68: JSON level = the row's index, category, season).</summary>
    public const int MightPopupType = 68;

    /// <summary>
    /// The GvgSeasonActivity names of the territory phases (stageP territories SPEC §0.2, but.cs: TerritoryHold 1, TerritoryConquer 2,
    /// TerritoryPrimeTime 3), the Overview rows their season points will go to (<see cref="AddActivityPoints"/>; P3/P4).
    /// </summary>
    public const string TerritoryHold = "TerritoryHold", TerritoryConquer = "TerritoryConquer", TerritoryPrimeTime = "TerritoryPrimeTime";

    private static readonly long Week = TimeSpan.FromDays(7).Ticks;

    private readonly HashSet<(Guid Guild, object Instance)> _clears = [];
    private string? _season;
    private DateTime _seasonEnd;

    private GuildGoalSettings S => world.Goals.Guild;

    private bool Pve => world.Config.Preset == ServerPreset.Pve;

    /// <summary>Guild:Enabled with guilds on.</summary>
    public bool Enabled => S.Enabled && world.Guilds.Enabled;

    /// <summary>The weekly goals: Guild:Weekly (preset: official false, pve true).</summary>
    public bool WeeklyOn => Enabled && (S.Weekly ?? Pve);

    /// <summary>Guild:ChallengeFactor (preset: official 1, pve 20).</summary>
    public double ChallengeFactor => S.ChallengeFactor ?? (Pve ? 20 : 1);

    /// <summary>Guild:CraftChallengeFactor (0.002 per craft fame point in both presets, SPEC §2.2; not × ChallengeFactor).</summary>
    public double CraftFactor => S.CraftChallengeFactor;

    /// <summary>
    /// Whether a credited PvP kill can give fame at all: a lethal death (Pvp:LethalDeath) or a knockdown's share (Pvp:KnockdownFameFactor);
    /// without either (the pve preset) pvpfame is never rolled.
    /// </summary>
    public bool PvpFameOn => world.Goals.Pvp.Enabled && (world.Config.PvpLethalDeath || world.Goals.Pvp.KnockdownFameFactor > 0);

    /// <summary>Guild:SeasonPointsFactor (preset: official 1, pve 5).</summary>
    public double PointsFactor => S.SeasonPointsFactor ?? (Pve ? 5 : 1);

    /// <summary>Guild:MightFactor (preset: official 1, pve 10; 0 = no guild might).</summary>
    public double MightFactor => S.MightFactor ?? (Pve ? 10 : 1);

    private GvgSeasonData Data => world.GvgSeasons ?? GvgSeasonData.Fallback;

    private DateTime UtcNow => new(world.GameNow, DateTimeKind.Utc);

    /// <summary>The guild season now: the client's while one runs, else the local one (<see cref="GvgSeasonData.Current"/>).</summary>
    public GvgSeason Season => Data.Current(UtcNow, S.LocalSeasonDays);

    private Guild? GuildOf(Player p) => Enabled ? world.Guilds.Of(p.Character.Id) : null;

    // ---- the season state

    /// <summary><paramref name="g"/>'s state in <paramref name="season"/>; null = nothing yet.</summary>
    public static GuildSeasonState? Peek(Guild g, GvgSeason season) => Peek(g, season.Key);

    private static GuildSeasonState? Peek(Guild g, string key) => g.Progress.Seasons.FirstOrDefault(s => s.Season == key);

    /// <summary><paramref name="g"/>'s state in <paramref name="season"/>, made when missing (the oldest beyond <see cref="SeasonsKept"/> dropped).</summary>
    public static GuildSeasonState StateOf(Guild g, GvgSeason season)
    {
        if (Peek(g, season) is { } st) return st;
        var list = g.Progress.Seasons;
        list.Add(st = new GuildSeasonState { Season = season.Key });
        while (list.Count > SeasonsKept) list.RemoveAt(0);
        return st;
    }

    /// <summary>
    /// <paramref name="raw"/> Adventurer's Challenge points (a7m) of <paramref name="p"/> (<see cref="Seasons.Gain"/>): × ChallengeFactor
    /// challenge points for its guild.
    /// </summary>
    public void OnChallenge(Player p, long raw)
    {
        if (raw > 0 && GuildOf(p) is { } g) AddChallengeShare(g, p.Character.Id, raw / 10000.0 * ChallengeFactor);
    }

    /// <summary>
    /// <paramref name="points"/> (a fraction too) for <paramref name="g"/>: added to the season's carry, its whole points go to
    /// <see cref="AddChallenge(Guild, Guid, long)"/> (a craft of 100 fame × 0.002 is not lost, five of them make a point).
    /// </summary>
    private void AddChallengeShare(Guild g, Guid member, double points)
    {
        if (points <= 0 || double.IsNaN(points)) return;
        var st = StateOf(g, Season);
        var sum = st.Carry + points;
        var whole = (long)Math.Min(Math.Floor(sum + 1e-9), long.MaxValue / 4);
        st.Carry = Math.Max(0, sum - whole);
        if (whole > 0) AddChallenge(g, member, whole);
        else world.Guilds.Touch(g);
    }

    private static long Whole(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>
    /// <paramref name="points"/> challenge points for <paramref name="g"/> from member <paramref name="member"/> (Guid.Empty = none: a GM's):
    /// the totals, the member's journal gainchallengepoints, and every level the total reaches (see the class).
    /// </summary>
    public void AddChallenge(Guild g, Guid member, long points)
    {
        if (points <= 0) return;
        var season = Season;
        var st = StateOf(g, season);
        st.Challenge = Math.Min(st.Challenge + points, long.MaxValue / 4);
        if (g.Find(member) is not null) st.Members[member] = st.Members.GetValueOrDefault(member) + points;
        st.Updated = world.GameNow;
        world.Guilds.Touch(g);
        if (world.Parties.Online(member) is { } by) AlbionJournal.ChallengePoints(by, points * 10000);
        var level = season.LevelOf(st.Challenge);
        if (level <= st.Level) return;
        var reached = Enumerable.Range(st.Level + 1, level - st.Level).Select(season.Row).OfType<ChallengeLevel>().ToList();
        st.Level = level;
        var gained = reached.Sum(r => (long)r.SeasonPoints);
        world.Guilds.Tell(g, $"[Guild] Guild Challenge level {level} reached" + (gained > 0 ? $": +{gained} guild season points ({st.Points + gained} this season)" : ""));
        Log.Info("guild", $"guild '{g.Name}' reached challenge level {level} of {season.Key} ({st.Challenge} points, +{gained} season points)");
        AddPoints(g, st, gained, ChallengeLevelUp);
        foreach (var m in g.Members)
            if (world.Parties.Online(m.Id) is { } p)
            {
                AlbionJournal.GuildChallengeLevel(p, level);
                // The client's own "New Guild Challenge Level reached!" (it also refetches 390 at once); its season must be the 351's, and
                // each level a row of its table (the popup handler throws otherwise).
                if (season.Index is { } index && p.GvgSeasonSent == index)
                    foreach (var r in reached)
                        p.Session.Send(new GameEventEvent
                        {
                            cdv = PopupType, cdw = PopupKind, cdx = string.Create(CultureInfo.InvariantCulture, $"{{\"level\":{r.Level},\"season\":{index}}}"),
                        });
            }
    }

    /// <summary><paramref name="points"/> guild season points of <paramref name="activity"/> for <paramref name="g"/>; the members in game get the journal's reachseasonpoints.</summary>
    private void AddPoints(Guild g, GuildSeasonState st, long points, string activity)
    {
        if (points <= 0) return;
        st.Points += points;
        st.Activity[activity] = st.Activity.GetValueOrDefault(activity) + points;
        st.Updated = st.PointsAt = world.GameNow;
        world.Guilds.Touch(g);
        foreach (var m in g.Members)
            if (world.Parties.Online(m.Id) is { } p) AlbionJournal.SeasonPoints(p, st.Points * 10000);
    }

    /// <summary>.guildgoal points: <paramref name="points"/> season points for <paramref name="g"/> (a GM's, activity ChallengeLevelUp, no factor).</summary>
    public void GrantPoints(Guild g, long points) => AddPoints(g, StateOf(g, Season), points, ChallengeLevelUp);

    /// <summary>
    /// <paramref name="points"/> season points of <paramref name="activity"/> (a GvgSeasonActivity name: <see cref="TerritoryHold"/> …) for
    /// <paramref name="g"/> in the current season, as given (the caller applies its factor); the territory phases' entry point.
    /// </summary>
    public void AddActivityPoints(Guild g, long points, string activity)
    {
        if (Enabled) AddPoints(g, StateOf(g, Season), points, activity);
    }

    // ---- hooks

    /// <summary>
    /// A fame gain of <paramref name="p"/> (<see cref="Destiny.Award"/>, not a GM's; a7m): its Member Logs (a PvE kill PVEFAME, a player kill
    /// PVPFAME, a harvest, catch or crop GATHERINGFAME, a craft CRAFTINGFAME), the guild might of a PvE kill, a harvest or a catch, and for a
    /// craft the craftfame goals and CraftChallengeFactor challenge points per fame point (both with the fraction carried to the next gain).
    /// </summary>
    public void OnFame(Player p, long gain, FameContext ctx, bool pvp)
    {
        if (gain <= 0 || GuildOf(p) is not { } g) return;
        if (!pvp && ctx.Source switch { FameSource.Kill => "pve", FameSource.Harvest => "gathering", FameSource.Fishing => "fishing", _ => null } is { } rule)
            FameMight(g, p, rule, gain);
        var stat = pvp ? GuildMemberLog.PvpFame : ctx.Source switch
        {
            FameSource.Kill => GuildMemberLog.PveFame,
            FameSource.Harvest or FameSource.Fishing or FameSource.Farm => GuildMemberLog.GatheringFame,
            FameSource.Craft => GuildMemberLog.CraftingFame,
            _ => -1,
        };
        if (stat >= 0) AddStat(g, p.Character.Id, stat, gain);
        if (pvp || ctx.Source != FameSource.Craft) return;
        AddChallengeShare(g, p.Character.Id, gain / 10000.0 * CraftFactor);
        CountFame(g, "craftfame", gain);
    }

    /// <summary>
    /// A mob killed by <paramref name="killer"/> with <paramref name="credited"/> sharing it: killmobs for each member of a guild among them
    /// (the mob's tier), bosses once per guild, dungeons once per guild and random dungeon run (a boss or miniboss on any of its floors:
    /// the run, not the floor, is the key); the members' tiers for next week's goals. A boss's season points (<see cref="BossPoints"/>).
    /// </summary>
    public void OnKill(Map map, Player killer, Mob mob, IReadOnlyList<Player> credited)
    {
        if (!Enabled) return;
        var t = mob.Template;
        var players = credited.Contains(killer) ? credited : [killer, .. credited];
        var boss = mob.Boss is not null || t.Category is "boss" or "miniboss";
        // Every floor has its own instance id: the run's first one stands for all of them.
        var run = t.Category is "boss" or "miniboss" && DungeonLayout.IsInstance(map.Cluster)
            ? world.Dungeons.Find(map.Cluster!)?.Group.Ids[0] ?? map.Cluster
            : null;
        var guilds = players.Select(p => (p, g: GuildOf(p))).Where(x => x.g is not null).GroupBy(x => x.g!).ToList();
        foreach (var group in guilds)
        {
            var g = group.Key;
            foreach (var (p, _) in group) PlayedAt(g, p, t.Tier);
            Count(g, "killmobs", t.Tier, group.Count());
            if (boss) Count(g, "bosses", t.Tier, 1);
            if (run is not null && FirstClear(g, run)) Count(g, "dungeons", t.Tier, 1);
        }
        if (boss) BossPoints(mob, [.. guilds.Select(x => (x.Key, (IReadOnlyList<Player>)[.. x.Select(y => y.p)]))]);
    }

    /// <summary>
    /// A boss kill's guild season points (SPEC §2.4): Guild:BossSeasonPoints of its kind (a world boss, else the template's boss / miniboss)
    /// × SeasonPointsFactor, split between the <paramref name="guilds"/> credited (activity KillMob); each credited member of a guild that got
    /// some is sent ReceivedGvgSeasonPoints (432: 0 its own object id, the avk event's object; 1 the gain; 2 the mob type, NewMob key 1),
    /// the client's floating "+N Season Points" and its chat line naming the mob and the guild.
    /// </summary>
    private void BossPoints(Mob mob, List<(Guild Guild, IReadOnlyList<Player> Members)> guilds)
    {
        var kind = mob.Boss is not null ? "worldboss" : mob.Template.Category;
        var points = S.BossSeasonPoints.GetValueOrDefault(kind);
        if (guilds.Count == 0 || points <= 0) return;
        var share = Whole(points * PointsFactor / guilds.Count);
        if (share <= 0) return;
        foreach (var (g, members) in guilds)
        {
            AddPoints(g, StateOf(g, Season), share, KillMob);
            foreach (var p in members)
                p.Session.Send(new ReceivedGvgSeasonPointsEvent { ddn = p.Id, dsz = share, ds0 = mob.Template.Id });
        }
        Log.Info("guild", $"{mob} ({kind}): +{share} guild season points for {string.Join(", ", guilds.Select(x => $"'{x.Guild.Name}'"))}");
    }

    // ---- the guild might (phase 4)

    /// <summary>
    /// The might of a fame gain of <paramref name="p"/> (a7m, <paramref name="rule"/> pve / gathering / fishing of the client's
    /// pvpchallengeobjectives): × fametomightfactor × the zone's multiplier (orange 0) × solorandomdungeonfactor for a PvE kill in a random
    /// dungeon without a party × MightFactor, to the category that maps the rule's objective type.
    /// </summary>
    private void FameMight(Guild g, Player p, string rule, long gain)
    {
        if (MightFactor <= 0 || !Data.Might.Fame.TryGetValue(rule, out var r) || r.Factor <= 0) return;
        var solo = rule == "pve" && DungeonLayout.IsInstance(p.Map?.Cluster)
            && (world.Parties.Of(p.Character.Id) is not { } party || party.Members.Count <= 1);
        AddMight(g, p.Character.Id, r.Objective, gain * r.Factor * r.ZoneFactor(Pvp.RulesOf(world, p.Map).WarningType) * (solo ? r.SoloDungeonFactor : 1) * MightFactor);
    }

    /// <summary>
    /// A might objective of the client data done by <paramref name="p"/> (a corrupted dungeon's mightreward, PVPCHALLENGE_CORRUPTED_SLAYER):
    /// its might × MightFactor to the category that maps its objective type.
    /// </summary>
    public void OnObjective(Player p, string? objective)
    {
        if (MightFactor <= 0 || objective is null || GuildOf(p) is not { } g || !Data.Might.Objectives.TryGetValue(objective, out var o) || o.Might <= 0) return;
        AddMight(g, p.Character.Id, o.Type, o.Might * 10000 * MightFactor);
    }

    /// <summary>
    /// <paramref name="a7m"/> might of objective type <paramref name="objective"/> from <paramref name="member"/> (Guid.Empty = a GM's) for
    /// <paramref name="g"/>: the season's category that maps the type (none: nothing), its total and the member's share, and every progression
    /// row the total reaches (see the class).
    /// </summary>
    public void AddMight(Guild g, Guid member, string objective, double a7m)
    {
        var season = Season;
        if (a7m < 1 || double.IsNaN(a7m) || season.Might.FirstOrDefault(c => c.Objectives.Contains(objective)) is not { } category) return;
        var st = StateOf(g, season);
        if (!st.Might.TryGetValue(category.Name, out var might)) st.Might[category.Name] = might = new GuildMight();
        var add = (long)Math.Min(a7m, long.MaxValue / 4);
        might.Total = Math.Min(might.Total + add, long.MaxValue / 4);
        if (g.Find(member) is not null) might.Members[member] = might.Members.GetValueOrDefault(member) + add;
        world.Guilds.Touch(g);
        var level = category.LevelOf(might.Total);
        if (level <= might.Level) return;
        var reached = Enumerable.Range(might.Level, level - might.Level).ToList(); // the rows' indexes: the client's level - 1
        might.Level = level;
        var gained = reached.Sum(i => (long)category.Levels[i].SeasonPoints);
        world.Guilds.Tell(g, $"[Guild] Guild Might {category.Name} level {level} reached" + (gained > 0 ? $": +{gained} guild season points ({st.Points + gained} this season)" : ""));
        Log.Info("guild", $"guild '{g.Name}' reached might {category.Name} level {level} of {season.Key} ({might.Total / 10000} might, +{gained} season points)");
        AddPoints(g, st, gained, MightLevelUp + category.Name);
        if (season.Index is not { } index) return;
        // The client's own might popup (a GeneralRewardPopup with the row's season points): its season must be the 351's, the category one
        // of the season's and the level a row of its table (the handler throws otherwise).
        foreach (var m in g.Members)
            if (world.Parties.Online(m.Id) is { } p && p.GvgSeasonSent == index)
                foreach (var i in reached)
                    p.Session.Send(new GameEventEvent
                    {
                        cdv = MightPopupType, cdw = PopupKind,
                        cdx = string.Create(CultureInfo.InvariantCulture, $"{{\"level\":{i},\"category\":\"{category.Name}\",\"season\":{index}}}"),
                    });
    }

    /// <summary>.guildgoal might &lt;category&gt; &lt;might&gt;: whole might for <paramref name="g"/>'s category (GM; no factor), false = no such category this season.</summary>
    public bool GrantMight(Guild g, Guid member, string category, long might)
    {
        if (Season.MightCategory(category) is not { } c || c.Objectives.Count == 0) return false;
        AddMight(g, member, c.Objectives.First(), might * 10000.0);
        return true;
    }

    /// <summary>An expedition completed, a world event won or a corrupted dungeon finished by <paramref name="p"/>: dungeons once per guild and <paramref name="instance"/>.</summary>
    public void OnDungeon(Player p, object instance)
    {
        if (GuildOf(p) is { } g && FirstClear(g, instance)) Count(g, "dungeons", 0, 1);
    }

    private bool FirstClear(Guild g, object instance)
    {
        if (_clears.Count > 10_000) _clears.Clear(); // a long-running server: old instances are long gone
        return _clears.Add((g.Id, instance));
    }

    /// <summary>A harvest of <paramref name="count"/> × <paramref name="item"/> by <paramref name="p"/>: gather goals of its tier, its tier for next week.</summary>
    public void OnGather(Player p, ItemTemplate item, int count)
    {
        if (count <= 0 || GuildOf(p) is not { } g) return;
        PlayedAt(g, p, item.Tier);
        Count(g, "gather", item.Tier, count);
    }

    /// <summary>A catch of <paramref name="p"/> (not trash): fish goals.</summary>
    public void OnFish(Player p)
    {
        if (GuildOf(p) is { } g) Count(g, "fish", 0, 1);
    }

    /// <summary>
    /// A credit of <see cref="PvpGoals.OnPvpKill"/> for <paramref name="p"/>, so a kill the PvP rules allowed and the anti-farming rules did not
    /// refuse: pvpkills for the killer's own credit of a death or knockdown (no assist, no duel) in a zone of factor <paramref name="zone"/>
    /// above 0; a player-bot victim only with Guild:CountBotKills.
    /// </summary>
    public void OnPvpKill(Player p, PvpKill k, bool assist, double zone)
    {
        if (assist || k.Outcome == PvpOutcome.DuelWin || zone <= 0 || k.Victim.IsBot && !S.CountBotKills || GuildOf(p) is not { } g) return;
        Count(g, "pvpkills", k.Victim.Tier, 1);
    }

    /// <summary>
    /// The credited kill fame <paramref name="fame"/> (a7m) of <paramref name="p"/>'s share of <paramref name="k"/>: pvpfame goals, with
    /// pvpkills' filter (a zone of factor <paramref name="zone"/> above 0, no duel, a player-bot victim only with Guild:CountBotKills).
    /// </summary>
    public void OnPvpFame(Player p, PvpKill k, double zone, long fame)
    {
        if (fame <= 0 || k.Outcome == PvpOutcome.DuelWin || zone <= 0 || k.Victim.IsBot && !S.CountBotKills || GuildOf(p) is not { } g) return;
        CountFame(g, "pvpfame", fame);
    }

    /// <summary><paramref name="a7m"/> fame for the <paramref name="kind"/> goals: whole fame points counted, the rest kept on the week for the next gain.</summary>
    private void CountFame(Guild g, string kind, long a7m)
    {
        if (a7m <= 0 || WeekOf(g) is not { } week) return;
        var sum = week.Carry.GetValueOrDefault(kind) + a7m;
        week.Carry[kind] = sum % 10000;
        world.Guilds.Touch(g);
        Count(g, kind, 0, sum / 10000);
    }

    /// <summary>Guilds:SilverTax <paramref name="a7m"/> of <paramref name="p"/>'s pickup into <paramref name="g"/>'s account: TAXPAID and SILVERBALANCE.</summary>
    public void TaxPaid(Guild g, Player p, long a7m)
    {
        if (!Enabled || a7m <= 0) return;
        AddStat(g, p.Character.Id, GuildMemberLog.TaxPaid, a7m);
        AddStat(g, p.Character.Id, GuildMemberLog.SilverBalance, a7m);
    }

    /// <summary>A .guild payout of <paramref name="a7m"/> to member <paramref name="member"/>: its SILVERBALANCE goes down.</summary>
    public void PaidOut(Guild g, Guid member, long a7m)
    {
        if (Enabled && a7m > 0) AddStat(g, member, GuildMemberLog.SilverBalance, -a7m);
    }

    // ---- member logs (309)

    /// <summary>The week start (UTC ticks) of now: Monday at the bounties' reset time (Bounties:ResetUtc).</summary>
    private long WeekStart => world.Bounties.WeekStart(UtcNow).Ticks;

    /// <summary>The log of <paramref name="member"/> brought to this week (a later week moves this week's values to the last one, or drops them after a gap).</summary>
    private GuildMemberLog LogOf(Guild g, Guid member)
    {
        var week = WeekStart;
        if (!g.Progress.Logs.TryGetValue(member, out var log)) g.Progress.Logs[member] = log = new GuildMemberLog { Week = week };
        if (log.This.Length != GuildMemberLog.Count) log.This = new long[GuildMemberLog.Count];
        if (log.Last.Length != GuildMemberLog.Count) log.Last = new long[GuildMemberLog.Count];
        if (log.All.Length != GuildMemberLog.Count) log.All = new long[GuildMemberLog.Count];
        if (log.Week != week)
        {
            var previous = log.Week == week - Week;
            (log.Last, log.LastTier) = previous ? (log.This, log.Tier) : (new long[GuildMemberLog.Count], 0);
            (log.This, log.Tier, log.Week) = (new long[GuildMemberLog.Count], 0, week);
        }
        return log;
    }

    private void AddStat(Guild g, Guid member, int stat, long a7m)
    {
        if (g.Find(member) is null) return;
        var log = LogOf(g, member);
        log.This[stat] += a7m;
        log.All[stat] += a7m;
        world.Guilds.Touch(g);
    }

    /// <summary>The highest tier <paramref name="p"/> played at this week (a kill's mob tier, a harvest's resource tier).</summary>
    private void PlayedAt(Guild g, Player p, int tier)
    {
        if (tier <= 0) return;
        var log = LogOf(g, p.Character.Id);
        if (tier <= log.Tier) return;
        log.Tier = tier;
        world.Guilds.Touch(g);
    }

    /// <summary>
    /// QueryGuildPlayerStats (309, SPEC §2.5) for <paramref name="p"/>: 0 its guild's members, 1 their value of stat <paramref name="stat"/>
    /// (bew 0-9, a7m) in <paramref name="timeFrame"/> (TimeFrame AllTime 0, ThisWeek 1, LastWeek 2); never null arrays, empty outside a guild.
    /// </summary>
    public QueryGuildPlayerStatsResponse MemberStats(Player p, int timeFrame, int stat)
    {
        if (world.Guilds.Of(p.Character.Id) is not { } g) return new QueryGuildPlayerStatsResponse { bg7 = [], bg8 = [] };
        var week = WeekStart;
        long Value(Guid id)
        {
            if (!Enabled || stat is < 0 or >= GuildMemberLog.Count || !g.Progress.Logs.TryGetValue(id, out var log)) return 0;
            long At(long[] values) => stat < values.Length ? values[stat] : 0;
            return timeFrame switch
            {
                0 => At(log.All),
                1 => log.Week == week ? At(log.This) : 0,
                2 => log.Week == week ? At(log.Last) : log.Week == week - Week ? At(log.This) : 0,
                _ => 0,
            };
        }
        return new QueryGuildPlayerStatsResponse { bg7 = [.. g.Members.Select(m => m.Id)], bg8 = [.. g.Members.Select(m => Value(m.Id))] };
    }

    // ---- weekly goals

    /// <summary>
    /// The goals of <paramref name="g"/> this week, rolled at its first look of a week (see the class; the new goals and the blessing
    /// told to the members in game); null with the weekly goals off.
    /// </summary>
    public GuildWeek? WeekOf(Guild g)
    {
        if (!WeeklyOn) return null;
        var start = WeekStart;
        var pr = g.Progress;
        if (pr.Week?.Start == start) return pr.Week;
        pr.LastWeek = pr.Week?.Start == start - Week ? pr.Week : pr.LastWeek?.Start == start - Week ? pr.LastWeek : null;
        pr.Week = Roll(g, start);
        pr.Week.Blessing = Math.Min(S.MaxBlessing, pr.LastWeek?.Goals.Count(x => x.Done) ?? 0);
        world.Guilds.Touch(g);
        world.Guilds.Tell(g, "[Guild] This week's guild goals: " + string.Join("; ", pr.Week.Goals.Select(x => x.Describe()))
            + (pr.Week.Blessing > 0 ? $". Guild blessing: {DescribeBlessing(pr.Week.Blessing)}" : "") + " (.guild goals)");
        Log.Info("guild", $"guild '{g.Name}' weekly goals {new DateTime(start, DateTimeKind.Utc):yyyy-MM-dd}: {string.Join("; ", pr.Week.Goals.Select(x => x.Describe()))}, blessing {pr.Week.Blessing}");
        return pr.Week;
    }

    /// <summary>.guildgoal roll: this week's goals of <paramref name="g"/> rolled again (progress lost, the blessing kept).</summary>
    public GuildWeek? Reroll(Guild g, int salt)
    {
        if (WeekOf(g) is not { } week) return null;
        var again = Roll(g, week.Start, salt);
        again.Blessing = week.Blessing;
        g.Progress.Week = again;
        world.Guilds.Touch(g);
        return again;
    }

    /// <summary>The goals of <paramref name="g"/> for the week of <paramref name="start"/>: deterministic per guild, week and <paramref name="salt"/>.</summary>
    private GuildWeek Roll(Guild g, long start, int salt = 0)
    {
        var seed = g.Id.ToByteArray().Aggregate(17, (h, b) => unchecked(h * 31 + b)) ^ (int)(start / TimeSpan.TicksPerDay) ^ salt * 7919;
        var rng = new Random(seed);
        var pools = S.Pools.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (Name: kv.Key, Entries: kv.Value.Where(Reachable).ToList()))
            .Where(x => x.Entries.Count > 0).OrderBy(_ => rng.Next()).ToList();
        var active = ActiveMembers(g);
        var tier = Tier(g, start);
        var goals = new List<GuildGoal>();
        for (var i = 0; goals.Count < S.GoalsPerWeek && pools.Any(x => x.Entries.Count > 0); i++)
        {
            var (name, entries) = pools[i % pools.Count];
            if (entries.Count == 0) continue;
            var e = entries[rng.Next(entries.Count)];
            entries.Remove(e);
            goals.Add(new GuildGoal
            {
                Pool = name, Kind = e.Kind, Tier = e.Kind is "killmobs" or "gather" ? Math.Max(e.MinTier, tier) : e.MinTier,
                Target = TwoDigits(e.Target * Math.Pow(active, S.TargetExponent)),
            });
        }
        return new GuildWeek { Start = start, Goals = goals };
    }

    /// <summary>Whether a goal of <paramref name="e"/>'s kind can progress here: PvP ones need the PvP goals, pvpfame also credited fame (<see cref="PvpFameOn"/>).</summary>
    private bool Reachable(GuildGoalEntry e) => e.Kind switch
    {
        "pvpkills" => world.Goals.Pvp.Enabled,
        "pvpfame" => PvpFameOn,
        _ => true,
    };

    /// <summary><paramref name="v"/> rounded to two significant digits (at least 1): 1234 → 1200, 87.4 → 87.</summary>
    public static long TwoDigits(double v)
    {
        if (v < 10) return Math.Max(1, Whole(v));
        var step = Math.Pow(10, Math.Floor(Math.Log10(v)) - 1);
        return (long)(Math.Round(v / step, MidpointRounding.AwayFromZero) * step);
    }

    /// <summary>The members in game or seen within Guild:ActiveDays; at least 1.</summary>
    public int ActiveMembers(Guild g) =>
        Math.Max(1, g.Members.Count(m => world.Parties.Online(m.Id) is not null || UtcNow - m.LastSeen <= TimeSpan.FromDays(S.ActiveDays)));

    /// <summary>
    /// The tier of the week of <paramref name="start"/>'s goals: the median of the highest tiers the members played at the week before (their
    /// kills' and harvests'), else of the members in game's maps, else 4; 1..8.
    /// </summary>
    private int Tier(Guild g, long start)
    {
        var tiers = g.Members.Select(m => g.Progress.Logs.GetValueOrDefault(m.Id) is { } log
                ? log.Week == start ? log.LastTier : log.Week == start - Week ? log.Tier : 0 : 0)
            .Where(t => t > 0).Order().ToList();
        if (tiers.Count == 0)
            tiers = [.. g.Members.Select(m => world.Parties.Online(m.Id)?.Map?.Template?.Name.Tier ?? 0).Where(t => t > 0).Order()];
        return tiers.Count == 0 ? 4 : Math.Clamp(tiers[(tiers.Count - 1) / 2], 1, 8);
    }

    /// <summary>
    /// <paramref name="n"/> of <paramref name="kind"/> at <paramref name="tier"/> for <paramref name="g"/>'s open goals of that kind and at most
    /// that tier: a line at each quarter (Guild:AnnounceProgress), and a goal reached is paid once (<see cref="Finish"/>).
    /// </summary>
    private void Count(Guild g, string kind, int tier, long n)
    {
        if (n <= 0 || WeekOf(g) is not { } week) return;
        foreach (var goal in week.Goals)
        {
            if (goal.Done || goal.Kind != kind || tier < goal.Tier) continue;
            var old = goal.Progress;
            goal.Progress = Math.Min(goal.Target, old + n);
            world.Guilds.Touch(g);
            if (goal.Progress < goal.Target)
            {
                if (S.AnnounceProgress && goal.Progress * 4 / goal.Target > old * 4 / goal.Target)
                    world.Guilds.Tell(g, $"[Guild] Weekly goal: {goal.Describe()} {goal.Progress}/{goal.Target}");
                continue;
            }
            Finish(g, week, goal);
        }
    }

    /// <summary>A goal reached (or .guildgoal done): done, and its reward to the guild (see the class), once.</summary>
    public void Finish(Guild g, GuildWeek week, GuildGoal goal)
    {
        if (goal.Done) return;
        (goal.Done, goal.Progress) = (true, goal.Target);
        var silver = Whole(S.GoalSilver * (goal.Tier > 0 ? goal.Tier : 4) / 4.0);
        var points = Whole(S.GoalSeasonPoints * PointsFactor);
        world.Guilds.Deposit(g, silver * 10000);
        world.Guilds.AddXp(g, S.GoalXp);
        AddPoints(g, StateOf(g, Season), points, ChallengeLevelUp);
        world.Guilds.Touch(g);
        var done = week.Goals.Count(x => x.Done);
        world.Guilds.Tell(g, $"[Guild] Weekly goal complete: {goal.Describe()}. Reward: {silver} silver into the guild account, {S.GoalXp} guild xp, "
            + $"{points} guild season points" + (done <= S.MaxBlessing ? $"; next week's guild blessing: {DescribeBlessing(Math.Min(done, S.MaxBlessing))}" : ""));
        Log.Info("guild", $"guild '{g.Name}' finished weekly goal {goal.Describe()}: {silver} silver, {S.GoalXp} xp, {points} season points");
        world.Vanity.GuildGoal(g.Members.Select(m => world.Parties.Online(m.Id)).OfType<Player>()); // stageN vanity
    }

    /// <summary>The blessing of <paramref name="p"/>'s guild this week (shares; zeros without one or with the weekly goals off).</summary>
    public (double Fame, double Silver, double Yield) Blessing(Player p)
    {
        if (!WeeklyOn || world.Guilds.Of(p.Character.Id) is not { } g || WeekOf(g) is not { Blessing: > 0 and var steps }) return (0, 0, 0);
        return (steps * S.BlessingFame, steps * S.BlessingSilver, steps * S.BlessingYield);
    }

    /// <summary>"+10 % fame, +10 % mob silver, +6 % gathering yield" for <paramref name="steps"/>.</summary>
    private string DescribeBlessing(int steps)
    {
        static string P(double v) => (v * 100).ToString("0.#", CultureInfo.InvariantCulture);
        return $"+{P(steps * S.BlessingFame)} % fame, +{P(steps * S.BlessingSilver)} % mob silver, +{P(steps * S.BlessingYield)} % gathering yield";
    }

    // ---- the window (390, 152, 151, 447, 450, 449) and the joins

    /// <summary>
    /// GetGuildChallengePoints (390, SPEC §2.10) for <paramref name="p"/> about guild <paramref name="guild"/>: 0 the guild, 1 the time of
    /// its last change (the season's start before any), 2 the total (clamped to int), 3 the members (the list's length), 4 the first row,
    /// 5/6 the names and points of the rows [first, first + count) of the members by points (then name); count 0 = to the end. With
    /// <paramref name="name"/> (the client's "focus own row" asks with its name and no first row) the window of count rows that holds that
    /// member. Not in a guild, another guild's Guid or the guild goals off: no keys (the client keeps no rows).
    /// </summary>
    public GetGuildChallengePointsResponse ChallengePoints(Player p, Guid guild, int first, int count, string? name = null)
    {
        var answer = new GetGuildChallengePointsResponse();
        if (GuildOf(p) is not { } g || guild != g.Id) return answer;
        var season = Season;
        var st = Peek(g, season);
        var (rows, window) = Window(g, st?.Members, ref first, count, name);
        answer.axe = g.Id;
        answer.axf = st?.Updated is > 0 and var at ? at : season.Start.Ticks;
        answer.axg = (int)Math.Min(int.MaxValue, st?.Challenge ?? 0);
        answer.axh = rows;
        answer.axi = first;
        answer.axj = [.. window.Select(r => r.Name)];
        answer.axk = [.. window.Select(r => (int)Math.Min(int.MaxValue, r.Value))];
        return answer;
    }

    /// <summary>
    /// The member rows of a list window (390, 449): <paramref name="g"/>'s members by their value in <paramref name="values"/> (then name), the
    /// rows [first, first + count) (count 0 = to the end); with <paramref name="name"/> and no first row (the client's "focus own row") the
    /// window of count rows that holds that member. Returns the member count and the window; <paramref name="first"/> becomes its start.
    /// </summary>
    private static (int Rows, List<(string Name, long Value)> Window) Window(Guild g, Dictionary<Guid, long>? values, ref int first, int count, string? name)
    {
        var rows = g.Members.Select(m => (m.Name, Value: values?.GetValueOrDefault(m.Id) ?? 0))
            .OrderByDescending(r => r.Value).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (first == 0 && count > 0 && !string.IsNullOrEmpty(name)
            && rows.FindIndex(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is >= 0 and var own)
            first = own / count * count;
        first = Math.Clamp(first, 0, rows.Count);
        return (rows.Count, rows.Skip(first).Take(count > 0 ? count : rows.Count).ToList());
    }

    /// <summary>
    /// GetGvgSeasonRank (152) of client season <paramref name="index"/> for the guild <paramref name="name"/> (the requester's when unknown):
    /// 0 its rank (<see cref="Ranked"/>), 1 its points; {0: -1} without points or guild.
    /// </summary>
    public GetGvgSeasonRankResponse Rank(Player p, int index, string? name)
    {
        var none = new GetGvgSeasonRankResponse { azf = -1 };
        if (!Enabled || (world.Guilds.Find(name) ?? world.Guilds.Of(p.Character.Id)) is not { } g) return none;
        if (world.GvgSeasons?.Seasons.ElementAtOrDefault(index) is not { } season) return none;
        var ranked = Ranked(season.Key);
        var at = ranked.FindIndex(x => x.Guild == g);
        return at < 0 ? none : new GetGvgSeasonRankResponse { azf = at + 1, azg = ranked[at].State.Points };
    }

    /// <summary>The guilds with season points in the season <paramref name="key"/>, by points, ties by who reached its points first (SPEC §2.4).</summary>
    private List<(Guild Guild, GuildSeasonState State)> Ranked(string key) =>
    [
        .. world.Guilds.All.Select(x => (Guild: x, State: Peek(x, key)!)).Where(x => x.State is { Points: > 0 })
            .OrderByDescending(x => x.State.Points).ThenBy(x => x.State.PointsAt),
    ];

    /// <summary>
    /// GetGvgSeasonRankings (151, SPEC §1.4) of client season <paramref name="index"/>: the ranked local guilds [first, first + count) (count 0 =
    /// to the end): 0 the first row's index, 1 the guilds ranked, 2 the time of the latest gain (a change clears the client's list), 3 names,
    /// 4 ids, 5-11 the crest (schema, symbol, colours, symbol colour, scale, offset), 12/13 alliance name and tag "", 14 alliance Empty,
    /// 15 season points, 16/17 0 (unknown, season wins?). Every array as long as key 15 (the client reads them by its index); an unknown season
    /// or the guild goals off: empty arrays.
    /// </summary>
    public GetGvgSeasonRankingsResponse Rankings(int index, int first, int count)
    {
        var ranked = Enabled && world.GvgSeasons?.Seasons.ElementAtOrDefault(index) is { } season ? Ranked(season.Key) : [];
        first = Math.Clamp(first, 0, ranked.Count);
        var rows = ranked.Skip(first).Take(count > 0 ? count : ranked.Count).ToList();
        var logos = rows.Select(x => x.Guild.Logo).ToList();
        return new GetGvgSeasonRankingsResponse
        {
            ayx = first, ayy = ranked.Count, ayz = ranked.Count > 0 ? ranked.Max(x => x.State.PointsAt) : 0,
            ay0 = [.. rows.Select(x => x.Guild.Name)], ay1 = [.. rows.Select(x => x.Guild.Id)],
            ay2 = [.. logos.Select(l => l.Schema)], ay3 = [.. logos.Select(l => l.Symbol)], ay4 = [.. logos.Select(l => l.Color1)],
            ay5 = [.. logos.Select(l => l.Color2)], ay6 = [.. logos.Select(l => l.SymbolColor)], ay7 = [.. logos.Select(l => l.Scale)],
            ay8 = [.. logos.Select(l => l.Offset)], ay9 = [.. rows.Select(_ => "")], aza = [.. rows.Select(_ => "")],
            azb = [.. rows.Select(_ => Guid.Empty)], azc = [.. rows.Select(x => x.State.Points)],
            azd = new int[rows.Count], aze = new int[rows.Count],
        };
    }

    /// <summary>
    /// GetGvgSeasonContributionByActivity (447, SPEC §1.6) for <paramref name="p"/> about <paramref name="guild"/>: 0 the season's index, 1 the
    /// guild, 2 its last change, 3 its season points, 4/5 the GvgSeasonActivity names with points (ChallengeLevelUp, KillMob,
    /// MightCategoryLevelUp@CAT) and their points; another guild, none or nothing yet: keys 0-2 only (as official without activity).
    /// </summary>
    public GetGvgSeasonContributionByActivityResponse Contribution(Player p, Guid guild)
    {
        var season = Season;
        var answer = new GetGvgSeasonContributionByActivityResponse { ax3 = season.Index ?? -1, ax4 = guild, ax5 = world.GameNow };
        if (GuildOf(p) is not { } g || guild != g.Id || Peek(g, season) is not { } st) return answer;
        var rows = st.Activity.Where(a => a.Value > 0).OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        answer.ax5 = st.Updated > 0 ? st.Updated : world.GameNow;
        answer.ax6 = st.Points;
        if (rows.Count == 0) return answer;
        answer.ax7 = [.. rows.Select(a => a.Key)];
        answer.ax8 = [.. rows.Select(a => a.Value)];
        return answer;
    }

    /// <summary>
    /// GetGuildMightCategoryOverview (450, SPEC §1.7) for <paramref name="p"/> about <paramref name="guild"/>: 0 the guild, 1 now, 2 the season's
    /// might categories, 3 the guild's might of each (a7m; the client levels it with its own table). Both arrays always (the client's c2z
    /// throws on a null one); empty ones outside the guild.
    /// </summary>
    public GetGuildMightCategoryOverviewResponse MightOverview(Player p, Guid guild)
    {
        var answer = new GetGuildMightCategoryOverviewResponse { axz = guild, ax0 = world.GameNow, ax1 = [], ax2 = [] };
        if (GuildOf(p) is not { } g || guild != g.Id) return answer;
        var season = Season;
        var st = Peek(g, season);
        answer.ax1 = [.. season.Might.Select(c => c.Name)];
        answer.ax2 = [.. season.Might.Select(c => st?.Might.GetValueOrDefault(c.Name)?.Total ?? 0)];
        return answer;
    }

    /// <summary>
    /// GetGuildMightCategoryContribution (449) for <paramref name="p"/> about <paramref name="guild"/>'s <paramref name="category"/>: 0 the guild,
    /// 1 the category, 2 now, 3 the guild's might of it (a7m), 4 the members, 5 the first row, 6/7 names and might of the window (as 390,
    /// <see cref="Window"/>). Another guild or an unknown category: keys 1 and 2 only (no rows).
    /// </summary>
    public GetGuildMightCategoryContributionResponse MightContribution(Player p, Guid guild, string? category, int first, int count, string? name)
    {
        var answer = new GetGuildMightCategoryContributionResponse { axs = category, axt = world.GameNow };
        var season = Season;
        if (GuildOf(p) is not { } g || guild != g.Id || season.MightCategory(category) is null) return answer;
        var might = Peek(g, season)?.Might.GetValueOrDefault(category!);
        var (rows, window) = Window(g, might?.Members, ref first, count, name);
        answer.axr = g.Id;
        answer.axu = might?.Total ?? 0;
        answer.axv = rows;
        answer.axw = first;
        answer.axx = [.. window.Select(r => r.Name)];
        answer.axy = [.. window.Select(r => r.Value)];
        return answer;
    }

    // ---- the season history and participation (stageP territories SPEC §6 P2)

    /// <summary>
    /// GetGvgSeasonHistoryRankings (153, t0: 0 a guild name, the requester's guild when unknown; afs, read by dd9.c 0x180ac76f0 into its list
    /// per guild name): 0 the time of the list (DateTime ticks: a change clears the client's rows; the guild's latest gain), 1 the client season
    /// indexes (bcp.abo: an index the client lacks leaves its row empty, so only client seasons), 2 the guild's rank, 3 its season points; one
    /// row per past client season (not the current one) the guild has points in, by index. The arrays are parallel, as key 1 drives the loop.
    /// </summary>
    public GetGvgSeasonHistoryRankingsResponse HistoryRankings(Player p, string? name)
    {
        var answer = new GetGvgSeasonHistoryRankingsResponse { ayu = [], ayv = [], ayw = [] };
        if (!Enabled || world.GvgSeasons is not { } data || (world.Guilds.Find(name) ?? world.Guilds.Of(p.Character.Id)) is not { } g) return answer;
        var current = Season.Key;
        var rows = data.Seasons.Where(s => s.Key != current && Peek(g, s) is { Points: > 0 })
            .Select(s => (Season: s, State: Peek(g, s)!, Rank: Ranked(s.Key).FindIndex(x => x.Guild == g) + 1))
            .Where(r => r.Rank > 0).OrderBy(r => r.Season.Index).ToList();
        answer.ayt = rows.Count > 0 ? rows.Max(r => r.State.PointsAt) : 0;
        answer.ayu = [.. rows.Select(r => r.Season.Index!.Value)];
        answer.ayv = [.. rows.Select(r => r.Rank)];
        answer.ayw = [.. rows.Select(r => r.State.Points)];
        return answer;
    }

    /// <summary>
    /// GetGvgSeasonGuildMemberHistory (154, tz: 0 the season index; afr): no reader in this client (no ci9.b&lt;afr&gt; instance), so the columns
    /// are the server's reading: 0/1 the members' Guids and names (by challenge points, then name), 2 the challenge points each gave in that
    /// season, 3 their guild might (a7m, all categories), 4-11 0 (parallel). Outside a guild or for a season the client lacks: empty arrays.
    /// </summary>
    public GetGvgSeasonGuildMemberHistoryResponse MemberHistory(Player p, int index)
    {
        var answer = new GetGvgSeasonGuildMemberHistoryResponse { ayh = [], ayi = [], ayj = [], ayk = [], ayl = [], aym = [], ayn = [], ayo = [], ayp = [], ayq = [], ayr = [], ays = [] };
        if (GuildOf(p) is not { } g || world.GvgSeasons?.Seasons.ElementAtOrDefault(index) is not { } season) return answer;
        var st = Peek(g, season);
        var rows = g.Members.Select(m => (m.Id, m.Name, Challenge: st?.Members.GetValueOrDefault(m.Id) ?? 0,
                Might: st?.Might.Values.Sum(x => x.Members.GetValueOrDefault(m.Id)) ?? 0))
            .OrderByDescending(r => r.Challenge).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var n = rows.Count;
        answer.ayh = [.. rows.Select(r => r.Id)];
        answer.ayi = [.. rows.Select(r => r.Name)];
        answer.ayj = [.. rows.Select(r => r.Challenge)];
        answer.ayk = [.. rows.Select(r => r.Might)];
        (answer.ayl, answer.aym, answer.ayn, answer.ayo, answer.ayp, answer.ays) = (new int[n], new int[n], new int[n], new int[n], new int[n], new int[n]);
        (answer.ayq, answer.ayr) = (new float[n], new float[n]);
        return answer;
    }

    /// <summary>
    /// GvGSeasonPlayerGuildParticipationDetails (504, vn: 0 the season index, 1 the requester's Guid; ag5 into c20 by c21.a 0x180a49a80): 0/1 echo
    /// the request (the client's callbacks wait under that pair), 2 the guild's name, 3-9 its crest (bux.j in order), 10 its season points (the
    /// client's bracket of them, but.aa), 11 0 (kept, not read), 12 its rank (the chest upgrade rows' rank limit, bq6.e; -1 = none), 13 the
    /// participation time (c4 ticks, shown against the season's length x minimumparticipationpercentage): this server counts no time, so the
    /// member's share of the participation rule (<see cref="Participants"/>: its challenge points or guild might against MinParticipation of the
    /// members' average) x that required time, 14 whether it is reached. A season index the client lacks gets no answer (c21.a throws on it);
    /// outside a guild only 0, 1 and 12 -1.
    /// </summary>
    public GvGSeasonPlayerGuildParticipationDetailsResponse? Participation(Player p, int index, Guid requester)
    {
        if (world.GvgSeasons?.Seasons.ElementAtOrDefault(index) is not { } season) return null;
        var answer = new GvGSeasonPlayerGuildParticipationDetailsResponse { a6m = requester, a6n = index, a6o = "", a6y = -1 };
        if (GuildOf(p) is not { } g) return answer;
        var st = Peek(g, season);
        var l = g.Logo;
        (answer.a6o, answer.a6p, answer.a6q, answer.a6r, answer.a6s, answer.a6t, answer.a6u, answer.a6v) =
            (g.Name, l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
        answer.a6w = st?.Points ?? 0;
        var rank = Ranked(season.Key).FindIndex(x => x.Guild == g);
        answer.a6y = rank < 0 ? -1 : rank + 1;
        var share = st is null ? 0 : Share(g, st, p.Character.Id);
        var required = (season.End - season.Start).Ticks * S.MinParticipation;
        answer.a6z = (long)(Math.Clamp(share, 0, 1) * Math.Max(0, required));
        answer.a60 = share >= 1;
        return answer;
    }

    /// <summary>
    /// How far <paramref name="member"/> is towards <see cref="Participants"/>' rule in <paramref name="st"/>: the larger of its challenge points and
    /// its guild might, each against MinParticipation of the members' average (1 = taking part; 0 = nothing given).
    /// </summary>
    private double Share(Guild g, GuildSeasonState st, Guid member)
    {
        if (g.Members.Count == 0) return 0;
        long Challenge(Guid id) => st.Members.GetValueOrDefault(id);
        long Might(Guid id) => st.Might.Values.Sum(x => x.Members.GetValueOrDefault(id));
        double Part(Func<Guid, long> of)
        {
            var own = of(member);
            if (own <= 0) return 0;
            var need = S.MinParticipation * g.Members.Average(m => (double)of(m.Id));
            return need <= 1e-9 ? 1 : own / need;
        }
        return Math.Max(Part(Challenge), Part(Might));
    }

    /// <summary>
    /// The winner of the client season before <paramref name="index"/>: the first of the local guilds ranked in it, with its points and how
    /// many of the client seasons up to it it won (at least 1); null = none (no season before, nobody with points).
    /// </summary>
    private (Guild Guild, long Points, int Wins)? Winner(int index)
    {
        if (index < 1 || world.GvgSeasons is not { } data || data.Seasons.ElementAtOrDefault(index - 1) is not { } before) return null;
        var ranked = Ranked(before.Key);
        if (ranked.Count == 0) return null;
        var (g, st) = ranked[0];
        var wins = data.Seasons.Take(index).Count(s => Ranked(s.Key) is { Count: > 0 } r && r[0].Guild == g);
        return (g, st.Points, Math.Max(1, wins));
    }

    /// <summary>
    /// GvgSeasonUpdate (351, ap4; dd7.r 0x180ac6110) of client season <paramref name="index"/>: 0 the season, 1 the one before, and with a winner
    /// of the one before (<see cref="Winner"/>) the GvgSeasonWinnerGui's keys: 2 its Guid (Empty = no winner window), 3 its name, 4-6 its alliance's
    /// Guid, name and tag ("" without one, as the official sample), 7-13 its crest in dd7.r's order (bux.j takes 7, 10, 8, 9, 11, 12, 13: schema,
    /// symbol, colour 1, colour 2, symbol colour, scale, offset), 14 its season points (the winner window's bracket), 15 its season wins
    /// (the window shows a count from 2 on).
    /// </summary>
    public GvgSeasonUpdateEvent SeasonUpdate(int index)
    {
        var e = new GvgSeasonUpdateEvent { cg5 = index, cg6 = Math.Max(0, index - 1) };
        if (Winner(index) is not { } w) return e;
        var a = world.Alliances.OfGuild(w.Guild.Id);
        var l = w.Guild.Logo;
        (e.cg7, e.cg8, e.cg9, e.cha, e.chb) = (w.Guild.Id, w.Guild.Name, a?.Id ?? Guid.Empty, a?.Name ?? "", a?.Tag ?? "");
        (e.chc, e.chf, e.chd, e.che, e.chg, e.chh, e.chi) = (l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
        (e.chj, e.chk) = (w.Points, w.Wins);
        return e;
    }

    // ---- the season's end (phase 3)

    /// <summary>
    /// Every guild season before <paramref name="current"/> not paid yet (the tick that sees a new season, or the first tick after a downtime
    /// across the end): <see cref="CloseSeason"/>, once each.
    /// </summary>
    private void CloseSeasons(GvgSeason current)
    {
        foreach (var key in world.Guilds.All.SelectMany(g => g.Progress.Seasons).Where(s => !s.Closed && s.Season != current.Key)
                     .Select(s => s.Season).Distinct().ToList())
            CloseSeason(key);
    }

    /// <summary>
    /// The end of the season <paramref name="key"/> (SPEC §2.6): every guild with points by rank gets Guild:BracketSilver of its bracket (the
    /// season's brackets) into the account, and its members who took part (<see cref="Participants"/>) the bracket's member reward (owed:
    /// given at once in game, else at the next login, <see cref="PayOwed"/>); a line to each guild, the top three to everyone in game; every
    /// state of the season marked paid (<see cref="GuildSeasonState.Closed"/>, set only once the guild's silver and rewards are booked) and
    /// saved at once. A guild whose close-out throws is logged and left open (retried at the next start or season change); the others go on.
    /// </summary>
    public void CloseSeason(string key)
    {
        var season = Data.ByKey(key, S.LocalSeasonDays) ?? Season;
        var ranked = Ranked(key);
        var open = ranked.Any(x => !x.State.Closed);
        var pay = new List<Guid>();
        for (var i = 0; i < ranked.Count; i++)
        {
            var (g, st) = ranked[i];
            if (st.Closed) continue; // paid once
            try
            {
                var bracket = season.BracketOf(st.Points);
                var silver = bracket is null ? 0 : S.BracketSilver.GetValueOrDefault(bracket.Name);
                var rewarded = bracket is null ? [] : Participants(g, st);
                if (silver > 0) world.Guilds.Deposit(g, silver * 10000);
                foreach (var m in rewarded)
                {
                    if (!g.Progress.Owed.TryGetValue(m.Id, out var owed)) g.Progress.Owed[m.Id] = owed = [];
                    owed.Add(new GuildMemberReward { Season = key, Bracket = bracket!.Name });
                }
                st.Closed = true;
                world.Guilds.Touch(g);
                pay.AddRange(rewarded.Select(m => m.Id));
                world.Guilds.Tell(g, $"[Guild] {Name(key)} ended: {st.Points} guild season points, {bracket?.Name ?? "no"} bracket, rank {i + 1} of {ranked.Count}"
                    + (silver > 0 ? $"; {silver} silver into the guild account" : "")
                    + (rewarded.Count > 0 ? $"; the {bracket!.Name} member reward for {string.Join(", ", rewarded.Select(m => m.Name))}" : ""));
                Log.Info("guild", $"guild '{g.Name}' closed {key}: rank {i + 1}/{ranked.Count}, {st.Points} points, {bracket?.Name ?? "no"} bracket, {silver} silver, {rewarded.Count} member reward(s)");
            }
            catch (Exception ex)
            {
                Log.Error("guild", $"guild '{g.Name}' could not close {key} ({(st.Closed ? "booked, its line not sent" : "retried at the next start or season change")}): {ex}");
            }
        }
        // The guilds without points have nothing to pay: closed, so that a later close-out skips them.
        foreach (var g in world.Guilds.All)
            if (Peek(g, key) is { Closed: false, Points: <= 0 } st)
            {
                st.Closed = true;
                world.Guilds.Touch(g);
            }
        if (ranked.Count > 0 && open)
        {
            var top = new SystemMessageEvent
            {
                d0p = $"[Guild season] {Name(key)} ended: " + string.Join(", ", ranked.Take(3).Select((x, i) => $"{i + 1}. {x.Guild.Name} ({x.State.Points})")),
                d0q = 0,
            };
            foreach (var p in world.Maps.Maps.SelectMany(m => m.Players.Values)) p.Session.Send(top);
        }
        world.Guilds.Flush();
        // The members in game are paid once every guild is booked, each on its own: a failure leaves only that reward owed.
        foreach (var id in pay.Distinct())
            if (world.Parties.Online(id) is { } p)
                try { PayOwed(p); }
                catch (Exception ex) { Log.Error("guild", $"season reward of '{p.Character.Name}' not given (still owed unless taken already): {ex}"); }
    }

    /// <summary>
    /// The members of <paramref name="g"/> who took part in <paramref name="st"/>'s season (SPEC §2.6): a contribution above 0 of at least
    /// MinParticipation of the members' average, in Guild Challenge points or in guild might (one who only gathered might counts too;
    /// nobody who did nothing, even when the guild's points came from boss kills or a GM).
    /// </summary>
    private List<GuildMember> Participants(Guild g, GuildSeasonState st)
    {
        if (g.Members.Count == 0) return [];
        long Challenge(GuildMember m) => st.Members.GetValueOrDefault(m.Id);
        long Might(GuildMember m) => st.Might.Values.Sum(x => x.Members.GetValueOrDefault(m.Id));
        var (challenge, might) = (g.Members.Average(m => (double)Challenge(m)), g.Members.Average(m => (double)Might(m)));
        return
        [
            .. g.Members.Where(m => Challenge(m) > 0 && Challenge(m) >= S.MinParticipation * challenge - 1e-9
                || Might(m) > 0 && Might(m) >= S.MinParticipation * might - 1e-9),
        ];
    }

    /// <summary>
    /// .guildgoal season end (GM): the current season closed and paid now as at its end (<see cref="CloseSeason"/>), its states kept under
    /// "&lt;key&gt;#n" (the first n free), so the season goes on from 0 for every guild. Returns that key; null with the guild goals off.
    /// </summary>
    public string? EndSeason()
    {
        if (!Enabled) return null;
        var key = Season.Key;
        var keys = world.Guilds.All.SelectMany(g => g.Progress.Seasons).Select(s => s.Season).ToHashSet(StringComparer.Ordinal);
        var n = 1;
        while (keys.Contains($"{key}#{n}")) n++;
        var archive = $"{key}#{n}";
        foreach (var g in world.Guilds.All)
            if (Peek(g, key) is { } st)
            {
                st.Season = archive;
                world.Guilds.Touch(g);
            }
        Log.Info("guild", $"guild season {key} ended by a GM: kept as {archive}, the season goes on from 0");
        CloseSeason(archive);
        return archive;
    }

    /// <summary>
    /// The season-end member rewards owed to <paramref name="p"/> by any guild (it may have left it since): the bracket's items into the
    /// inventory (by mail or .bounty claim when they do not fit, as the daily challenge's) and the journal's gotguildmemberreward; given once
    /// (the guild and the character saved at once, as a .guild payout).
    /// </summary>
    private void PayOwed(Player p)
    {
        var id = p.Character.Id;
        var paid = false;
        foreach (var g in world.Guilds.All)
        {
            if (!g.Progress.Owed.Remove(id, out var rewards)) continue;
            world.Guilds.Touch(g);
            paid = true;
            foreach (var r in rewards) Give(p, g, r);
        }
        if (!paid) return;
        world.Guilds.Flush();
        world.Saves.SaveNow(p.Character);
    }

    /// <summary>The items of <paramref name="bracket"/>'s member reward (Guild:BracketItems) the item table knows; an unknown one is skipped with a warning.</summary>
    private List<(ItemTemplate Type, int Count)> RewardItems(string bracket)
    {
        var items = new List<(ItemTemplate, int)>();
        foreach (var (name, count) in S.BracketItems.GetValueOrDefault(bracket) ?? new Dictionary<string, int>())
            if (world.Items.Find(name) is { CanCreate: true } type) items.Add((type, count));
            else Log.Warn("guild", $"Guild:BracketItems {bracket}: unknown item {name} (closest: {world.Items.Closest(name)}), not given");
        return items;
    }

    /// <summary>
    /// <paramref name="g"/> is being deleted (its last member left, a disband) with season-end rewards still owed: a member in game gets them
    /// now, the others the bracket's items by mail (without the journal's reward); with mail off they are lost (a warning).
    /// </summary>
    public void Deleted(Guild g)
    {
        foreach (var (id, rewards) in g.Progress.Owed.ToList())
        {
            if (world.Parties.Online(id) is { } p)
            {
                PayOwed(p);
                continue;
            }
            g.Progress.Owed.Remove(id);
            foreach (var r in rewards)
            {
                var items = RewardItems(r.Bracket).Select(x => new InventoryItem(x.Type.Name, x.Count)).ToList();
                if (items.Count == 0) continue;
                if (Mailer.Notice(world, id, "Guild season reward", $"The {r.Bracket} member reward of '{g.Name}' for {Name(r.Season)}: the guild is gone, so it comes by mail.", items) == 0)
                    Log.Warn("guild", $"guild '{g.Name}' deleted: the {r.Bracket} member reward of {r.Season} owed to {id} is lost (mail is off)");
            }
        }
    }

    private void Give(Player p, Guild g, GuildMemberReward r)
    {
        var given = new List<string>();
        var waiting = new List<InventoryItem>();
        foreach (var (type, count) in RewardItems(r.Bracket))
        {
            if (Network.WorldSession.Fits(p.Inventory.Bag, [(type, count)]))
            {
                p.Session.GiveItem(p, type, count);
                given.Add(count == 1 ? type.Name : $"{type.Name} x{count}");
            }
            else waiting.Add(new InventoryItem(type.Name, count));
        }
        var mailed = waiting.Count > 0 && Mailer.Overflow(world, p.Character, waiting, $"the guild season {Name(r.Season)}");
        if (waiting.Count > 0 && !mailed) (p.Character.Goals ??= new GoalLog()).ItemsOwed.AddRange(waiting.SelectMany(i => Enumerable.Repeat(i.Name, i.Count)));
        AlbionJournal.GuildMemberReward(p, r.Bracket);
        p.Session.Send(new SystemMessageEvent
        {
            d0p = $"[Guild] Season reward of '{g.Name}' ({r.Bracket} bracket, {Name(r.Season)})"
                + (given.Count > 0 ? ": " + string.Join(", ", given) : "")
                + (waiting.Count > 0 ? $"; {string.Join(", ", waiting.Select(i => $"{i.Name} x{i.Count}"))} " + (mailed ? "comes by mail" : "does not fit: free some room and type .bounty claim") : ""),
            d0q = 0,
        });
        Log.Info("guild", $"'{p.Character.Name}' got the {r.Bracket} member reward of {r.Season} from '{g.Name}': {string.Join(", ", given)}{(waiting.Count > 0 ? $", {waiting.Count} waiting" : "")}");
    }

    /// <summary>
    /// After the JoinFinished of <paramref name="p"/>: during a client season its GvgSeasonUpdate (351 {0 the season index, 1 the one before},
    /// as officially at a login; the level popup's precondition; again at a map change, which the client takes as no change). After a login
    /// (not <paramref name="arrived"/>) the journal's old reachseasonpoints progress is dropped once (<see cref="AlbionJournal.GuildSeasonPoints"/>),
    /// a member's journal catches up with its guild (level and season points reached while it was away), it gets the season-end rewards
    /// owed to it and it is told the week's open goals and blessing.
    /// </summary>
    public void Joined(Player p, bool arrived)
    {
        p.GvgSeasonSent = null;
        if (!Enabled) return;
        var season = Season;
        if (season.Index is { } index)
        {
            p.Session.Send(SeasonUpdate(index));
            p.GvgSeasonSent = index;
        }
        if (arrived) return;
        AlbionJournal.GuildSeasonPoints(p);
        PayOwed(p);
        if (world.Guilds.Of(p.Character.Id) is not { } g) return;
        if (Peek(g, season) is { } st)
        {
            if (st.Level > 1) AlbionJournal.GuildChallengeLevel(p, st.Level);
            if (st.Points > 0) AlbionJournal.SeasonPoints(p, st.Points * 10000);
        }
        var known = g.Progress.Week?.Start;
        if (WeekOf(g) is { } week && week.Start == known && (week.Blessing > 0 || week.Goals.Any(x => !x.Done)))
            p.Session.Send(new SystemMessageEvent
            {
                d0p = "[Guild] Weekly goals: " + string.Join("; ", week.Goals.Select(x => $"{x.Describe()} {x.Progress}/{x.Target}{(x.Done ? " (done)" : "")}"))
                    + (week.Blessing > 0 ? $". Guild blessing: {DescribeBlessing(week.Blessing)}" : ""),
                d0q = 0,
            });
    }

    /// <summary>
    /// The world tick: at the first one and whenever the season changed the seasons before it are paid (<see cref="CloseSeasons"/>), and a new
    /// guild season is told to every guild member in game (once; nothing at the first tick). The first one also warns of a Guild:BracketItems
    /// item the item table does not know.
    /// </summary>
    public void Update()
    {
        if (!Enabled) return;
        var now = UtcNow;
        if (_season is not null && now < _seasonEnd) return;
        var season = Season;
        var first = _season is null;
        var changed = _season != season.Key;
        (_season, _seasonEnd) = (season.Key, season.End);
        if (first && world.Items.Count > 0) // no item table (a test's world): nothing to check against
            foreach (var bracket in S.BracketItems.Keys) RewardItems(bracket);
        if (changed) CloseSeasons(season);
        if (first || !changed) return;
        foreach (var g in world.Guilds.All)
            world.Guilds.Tell(g, $"[Guild] A new guild season began: {Name(season)} (.guild season)");
        Log.Info("guild", $"guild season {season.Key} from {season.Start:yyyy-MM-dd HH:mm} to {season.End:yyyy-MM-dd HH:mm} UTC");
    }

    private static string Name(GvgSeason s) => Name(s.Key);

    private static string Name(string key) => key.StartsWith("LOCAL@", StringComparison.Ordinal) ? $"local season {key[6..]}" : key;

    // ---- commands

    /// <summary>.guild season: <paramref name="g"/>'s season, challenge level and points, season points, bracket and rank.</summary>
    public IEnumerable<string> DescribeSeason(Guild g)
    {
        if (!Enabled)
        {
            yield return "the guild goals are off on this server (Guild:Enabled in content/goals.json)";
            yield break;
        }
        var season = Season;
        var st = Peek(g, season);
        var (challenge, level, points) = (st?.Challenge ?? 0, st?.Level ?? 1, st?.Points ?? 0);
        var next = season.Row(level + 1);
        yield return string.Create(CultureInfo.InvariantCulture,
            $"{Name(season)} ({(season.IsClient ? "the client's Guild Season window" : "server only")}), {season.Start:yyyy-MM-dd} to {season.End:yyyy-MM-dd HH:mm} UTC");
        yield return $"Guild Challenge level {level}: {challenge} points" + (next is null ? " (top level)" : $", level {next.Level} at {next.Points} (+{next.SeasonPoints} season points)");
        var bracket = season.BracketOf(points);
        var up = season.Brackets.Where(b => b.Threshold > points).MinBy(b => b.Threshold);
        var ranked = Ranked(season.Key);
        var rank = ranked.FindIndex(x => x.Guild == g) + 1;
        yield return $"{points} guild season points, {(bracket is null ? "no bracket" : bracket.Name)}" + (up is null ? "" : $" ({up.Name} at {up.Threshold})")
            + (rank > 0 ? $"; rank {rank} of {ranked.Count}" : "");
        if (st is not null && st.Activity.Count > 0)
            yield return "by activity: " + string.Join(", ", st.Activity.Where(a => a.Value > 0).OrderByDescending(a => a.Value).Select(a => $"{a.Key} {a.Value}"));
        if (st is not null && st.Members.Count > 0)
            yield return "contributions: " + string.Join(", ", g.Members.Select(m => (m.Name, Points: st.Members.GetValueOrDefault(m.Id)))
                .Where(r => r.Points > 0).OrderByDescending(r => r.Points).Select(r => $"{r.Name} {r.Points}"));
        var might = season.Might.Select(c => (c, m: st?.Might.GetValueOrDefault(c.Name))).Where(x => x.m is { Total: > 0 }).ToList();
        if (might.Count > 0)
            yield return "guild might: " + string.Join(", ", might.Select(x => $"{x.c.Name} level {x.m!.Level} ({x.m.Total / 10000}"
                + (x.m.Level < x.c.Levels.Count ? $"/{x.c.Levels[x.m.Level].Might / 10000}" : "") + ")"));
    }

    /// <summary>.guild ranking: the local guilds of the season by season points (the top ten, and <paramref name="own"/>'s row when lower).</summary>
    public IEnumerable<string> DescribeRanking(Guild? own)
    {
        if (!Enabled)
        {
            yield return "the guild goals are off on this server (Guild:Enabled in content/goals.json)";
            yield break;
        }
        var season = Season;
        var ranked = Ranked(season.Key);
        yield return $"{Name(season)}: {ranked.Count} guild(s) with season points" + (season.IsClient ? " (the Guild Season window's Rankings tab)" : "");
        for (var i = 0; i < ranked.Count; i++)
            if (i < 10 || ranked[i].Guild == own)
                yield return $"{i + 1}. {ranked[i].Guild.Name}: {ranked[i].State.Points} ({season.BracketOf(ranked[i].State.Points)?.Name ?? "no bracket"})";
    }

    /// <summary>.guild goals: this week's goals of <paramref name="g"/>, the blessing and the next roll.</summary>
    public IEnumerable<string> DescribeWeek(Guild g)
    {
        if (WeekOf(g) is not { } week)
        {
            yield return "weekly guild goals are off on this server (Guild:Weekly in content/goals.json)";
            yield break;
        }
        for (var i = 0; i < week.Goals.Count; i++)
        {
            var x = week.Goals[i];
            yield return $"{i + 1}. {x.Describe()}: {x.Progress}/{x.Target}{(x.Done ? " (done)" : "")}";
        }
        yield return week.Blessing > 0 ? $"guild blessing this week: {DescribeBlessing(week.Blessing)}" : "no guild blessing this week (finish goals for next week's)";
        var done = week.Goals.Count(x => x.Done);
        yield return string.Create(CultureInfo.InvariantCulture,
            $"new goals at {new DateTime(week.Start, DateTimeKind.Utc).AddDays(7):yyyy-MM-dd HH:mm} UTC; {done} done so far (next week's blessing: {Math.Min(done, S.MaxBlessing)} step(s))");
    }
}
