using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlbionLocal.Server.Game;

/// <summary>
/// A player character as far as the emulator knows it, as saved (Persistence/ICharacterRepository). The repository
/// hands out copies; the game plays one live copy per character (World.LoadCharacter) and saves snapshots of it
/// (SaveScheduler). A new character starts as content/starter.json says (Content/StarterKit).
/// </summary>
public sealed class Character
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";

    /// <summary>
    /// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.2): a bot's character, never saved (<see cref="Persistence.SaveScheduler"/>
    /// drops every mark and save of it) and never in the repository; not a column, not cloned.
    /// </summary>
    [JsonIgnore] public bool Transient { get; init; }

    /// <summary>
    /// The account the character belongs to (one per login name since stage 7; ICharacterRepository.OpenAccount). Empty =
    /// none given: the repository stores such a character in the local account (<see cref="Persistence.ICharacterRepository.Account"/>).
    /// </summary>
    public Guid Account { get; set; }

    /// <summary>Avatar/portrait id (CreateCharacter ri.h8), retained under its historical persistence name.
    /// Transient bots use this field for their body template and supply their avatar through BotInfo.</summary>
    public int Template { get; set; }

    /// <summary>Body template id (CreateCharacter ri.h9), not an avatar ring or skin colour.</summary>
    public byte Appearance { get; set; }

    public int[] Customization { get; set; } = [];
    public int[] Clothes { get; set; } = [];
    public string Cluster { get; set; } = "0000";
    public float PositionX { get; set; }
    public float PositionY { get; set; }

    /// <summary>
    /// Facing in degrees, 0-360, at the last arrival of a cluster change (SPEC stage 3 §3, §4.6); 0 until the first. The Join
    /// sends it (key 10) only through that arrival's <see cref="PendingTransfer"/> (§4.5), so a later login sends none.
    /// </summary>
    public float Direction { get; set; }

    /// <summary>The cluster before <see cref="Cluster"/> (Join key 66); "" until the first cluster change.</summary>
    public string PreviousCluster { get; set; } = "";

    /// <summary>UTC ticks when the character last left a cluster (Join key 101): the cluster change, else the last logout; 0 = never.</summary>
    public long LastLeftAt { get; set; }

    /// <summary>The last cluster of world.xml type PLAYERCITY_* the character joined (Join key 118; banks and markets count); "" = none yet.</summary>
    public string LastCity { get; set; } = "";

    /// <summary>
    /// A cluster change committed and not yet joined (ARCHITECTURE.md §4.2, §5.3; SPEC stage 3 §4.1): saved with the
    /// character, so a crash between the ChangeCluster response and the new Join loses nothing; that Join clears it.
    /// </summary>
    public PendingTransfer? PendingTransfer { get; set; }

    /// <summary>
    /// StageG dungeons (SPEC §6.1): where the character goes when its saved cluster is the id of a random dungeon that no longer
    /// exists (closed, or the server restarted): the entrance cluster and the arrival in front of the exit it came in by; set
    /// when it enters a dungeon from the open world. Null = none.
    /// </summary>
    public DungeonReturn? DungeonReturn { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>Accumulated time actually joined to the world, in milliseconds (Login key 34). Offline time is excluded.</summary>
    public long PlayedMilliseconds { get; set; }

    public OpeningTutorialProgress? Tutorial { get; set; }

    /// <summary>Items by EquipmentSlot (MainHand, OffHand, Head, Armor, Shoes, Bag, Cape, Mount, Potion, Food); null = empty slot.</summary>
    public InventoryItem?[] Equipment { get; set; } = new InventoryItem?[Entities.Inventory.EquipmentSlots];

    /// <summary>Inventory slots in order; null = empty slot.</summary>
    public List<InventoryItem?> Inventory { get; set; } = [];

    /// <summary>
    /// Total fame as a7m raw (fixed point, 10000 = 1): UpdateFame key 1 (stage 5c, SPEC §5c.2), raised by every mob kill.
    /// Destiny board progression from it comes later.
    /// </summary>
    public long Fame { get; set; }

    /// <summary>
    /// Silver as a7m raw (fixed point, 10000 = 1 silver): Join key 33, UpdateMoney key 1 (stage 6 SPEC §0). Changed only
    /// through <see cref="Systems.Wallet"/>, which sends the UpdateMoney.
    /// </summary>
    public long Silver { get; set; }

    /// <summary>Gold as a7m raw: Join key 34, UpdateMoney key 2 (nothing spends or gives it yet but .money).</summary>
    public long Gold { get; set; }

    /// <summary>
    /// The bank vaults (stage 6 SPEC §6b): the slots of each vault tab, by <see cref="VaultKey"/> (the bank's cluster and the
    /// tab Guid); null = empty slot. Only the tabs that ever held something are here; saved with the character.
    /// </summary>
    public Dictionary<string, List<InventoryItem?>> Vaults { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The key of a vault in <see cref="Vaults"/>: "&lt;cluster&gt;|&lt;tab Guid&gt;", or "shared|&lt;tab Guid&gt;" (<see cref="SharedVault"/>).</summary>
    public static string VaultKey(string location, Guid tab) => $"{location}|{tab}";

    /// <summary>The location of the one vault every city bank opens under Bank:Shared (PVE-DESIGN step 12).</summary>
    public const string SharedVault = "shared";

    /// <summary>Destiny board state; null = everything unlocked (<see cref="Progression.UnlockAll"/>).</summary>
    public Progression? Progression { get; set; }

    public Guid EquipmentContainer { get; set; } = Guid.NewGuid();
    public Guid InventoryContainer { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The player's own timed spells as it left (stageD consumables SPEC §6: the food buff runs on wall-clock time, so a relog
    /// gets the same start back); written by <see cref="Systems.Consumables.Store"/>, the ended ones dropped at the next Join.
    /// </summary>
    public List<SavedEffect> Effects { get; set; } = [];

    /// <summary>The slot cooldowns as it left (potion and food slots, SPEC §6); the ended ones dropped at the next Join.</summary>
    public List<SavedCooldown> Cooldowns { get; set; } = [];

    /// <summary>
    /// Crafting focus points as of <see cref="FocusStamp"/> (CraftingFocusUpdate key 3, Join key 27; stageJ focus-lp SPEC §5.2): the
    /// regeneration since the stamp is added lazily (<see cref="Systems.Focus.Current"/>), up to <see cref="Systems.Focus.Max"/>.
    /// </summary>
    public double CraftingFocus { get; set; }

    /// <summary>UTC ticks (GameTimeStamp) up to which <see cref="CraftingFocus"/> is worked out (Join key 31); 0 = never, nothing regenerated before the next Join.</summary>
    public long FocusStamp { get; set; }

    /// <summary>Learning points, a7m raw (Join key 37, UpdateLearningPoints key 1; focus-lp SPEC §5.2): paid while premium, spent by auto-learn.</summary>
    public long LearningPoints { get; set; }

    /// <summary>UTC ticks of the last learning point paid (Join key 38, event 83 key 2); 0 = never, the accrual starts at the next Join.</summary>
    public long LpStamp { get; set; }

    /// <summary>
    /// The client's use flags (enum UseFlag, op 298 ChangeUseFlag; focus-lp SPEC D4) as bits: 0 AutoUseLearningPoints (Join key 99),
    /// 1 AutoUseActionFocusForCrafting (key 32), 2 AutoUseSilverForReSpecPointGainOnMaxLevel (key 100). A new character has 0 and 1 on, as captured.
    /// </summary>
    public int UseFlags { get; set; } = DefaultUseFlags;

    /// <summary>The use flags of a new character: AutoUseLearningPoints and AutoUseActionFocusForCrafting.</summary>
    public const int DefaultUseFlags = 0b011;

    /// <summary>Whether use flag <paramref name="flag"/> (0-2) is on.</summary>
    public bool UseFlag(int flag) => (UseFlags & (1 << flag)) != 0;

    /// <summary>The destiny board nodes marked for auto-learn (op 312, event 155; focus-lp SPEC D6); null = none.</summary>
    public AutoLearnSet? AutoLearn { get; set; }

    /// <summary>
    /// GameTimeStamp the character last got mounted (stageD mounts SPEC §3: the first Join after a login is mounted again, so
    /// the state outlives logout and cluster change); 0 = dismounted. The restored mount buffs keep it as their start (§4.8).
    /// </summary>
    public long MountedSince { get; set; }

    /// <summary>Whether the character is mounted (<see cref="MountedSince"/> set).</summary>
    public bool Mounted => MountedSince != 0;

    /// <summary>GameTimeStamp from which it may mount again after its mount died (MountCooldownUpdate key 1, SPEC §4.6); 0 = none.</summary>
    public long MountCooldownUntil { get; set; }

    /// <summary>
    /// Its FlaggingStatus (stageE pvp SPEC §0: 0 Blue, 1-6 a faction, 255 Red; Join key 49, NewCharacter key 53): set by the flag
    /// cast (<see cref="Systems.Flagging"/>, op 354) and kept over a logout (§3.1 step 3).
    /// </summary>
    public byte Flag { get; set; }

    /// <summary>The faction it is enlisted to (a FlaggingStatus 1-6; Join key 106, NewCharacter key 56); 0 = none (GM .pvp faction).</summary>
    public byte Faction { get; set; }

    /// <summary>Reputation points (SPEC §3.4; Join key 41, NewCharacter key 37), clamped to gamedata min/maxreputation.</summary>
    public double Reputation { get; set; }

    /// <summary>UTC ticks up to which the daily recovery of a negative level is in <see cref="Reputation"/>; 0 = never worked out.</summary>
    public long ReputationAt { get; set; }

    /// <summary>
    /// The Albion Journal achievements its tracker shows (ops-audit SPEC §3.4: TrackAlbionJournalAchievements, op 515, and
    /// event 600), as the client last sent them; null = never set, the server suggests some at the next Join.
    /// </summary>
    public string[]? JournalTracked { get; set; }

    /// <summary>
    /// StageH expeditions (SPEC §5.1, §8): the daily bonuses taken, the last expedition run and the reward items that did not fit the
    /// inventory (given at a later Join); null = never ran one.
    /// </summary>
    public ExpeditionLog? Expeditions { get; set; }

    /// <summary>
    /// PVE-DESIGN step 11 (stageH journals SPEC §B.4, §C.4): the Albion Journal progress, finished and claimed achievements, the
    /// Adventurer's Challenge points and reward states per season, and the server bounties' progress; null = none of them yet.
    /// </summary>
    public GoalLog? Goals { get; set; }

    /// <summary>
    /// PVE-DESIGN step 12 (stageH blackmarket SPEC §3): the silver sold to the Black Market's NPC buyer on its day
    /// (<see cref="Systems.BlackMarket.Day"/>); null = never sold there.
    /// </summary>
    public BlackMarketLog? BlackMarket { get; set; }

    /// <summary>
    /// StageL corrupted dungeons (SPEC §4.9): the infamy per InfamyType (the current total and its peak), the runs entered and the
    /// completions per difficulty; null = never entered one.
    /// </summary>
    public InfamyLog? Infamy { get; set; }

    /// <summary>
    /// StageL mists phase B (SPEC §6.7): the Brecilien standing (StandingType MistCity, a7m raw: UpdateStanding 87 key 1, the City Trader's
    /// gates) and the favour (the trader's FAVOR currency, a7m raw) the mists' kills earn.
    /// </summary>
    public long MistStanding { get; set; }
    public long MistFavor { get; set; }

    /// <summary>StageN qol (SPEC §2): the saved loadouts (LoadoutCreate/Update 425/428), in no order (<see cref="Loadout.Position"/>); schema v29.</summary>
    public List<Loadout> Loadouts { get; set; } = [];

    /// <summary>
    /// StageN faction (analysis/work/stageN/faction/SPEC.md §2): the standing per faction (a7m, index FlaggingStatus − 1: Join key 105, event 86
    /// key 3), the faction points per faction (the FACTION_* currencies, a7m: Join key 104, UpdateCurrency 85) and when the enlistment last
    /// changed (GameTimeStamp; 0 = never: the cooldown of a switch); schema v30.
    /// </summary>
    public long[] FactionStanding { get; set; } = new long[6];
    public long[] FactionPoints { get; set; } = new long[6];
    public long FactionChangedAt { get; set; }

    /// <summary>
    /// StageN vanity (analysis/work/stageN/vanity/SPEC.md §2): the cosmetics this character unlocked (the account's are the union of its
    /// characters'), its wardrobe and its mount skins; null = none yet; schema v35.
    /// </summary>
    public VanityLog? Vanity { get; set; }

    /// <summary>A deep copy: what the repository hands out and what a save snapshot holds share nothing with the original.</summary>
    public Character Clone() => new()
    {
        Id = Id, Name = Name, Account = Account, Template = Template, Appearance = Appearance, Tutorial = Tutorial,
        Customization = [.. Customization], Clothes = [.. Clothes],
        Cluster = Cluster, PositionX = PositionX, PositionY = PositionY, Direction = Direction,
        PreviousCluster = PreviousCluster, LastLeftAt = LastLeftAt, LastCity = LastCity, PendingTransfer = PendingTransfer, DungeonReturn = DungeonReturn, Created = Created,
        Fame = Fame, Silver = Silver, Gold = Gold, PlayedMilliseconds = PlayedMilliseconds,
        Vaults = Vaults.ToDictionary(v => v.Key, v => v.Value.Select(i => i?.Copy()).ToList(), StringComparer.Ordinal),
        Equipment = [.. Equipment.Select(i => i?.Copy())],
        Inventory = [.. Inventory.Select(i => i?.Copy())],
        Progression = Progression?.Clone(),
        EquipmentContainer = EquipmentContainer, InventoryContainer = InventoryContainer,
        Effects = [.. Effects.Select(e => e with { Durations = [.. e.Durations] })], Cooldowns = [.. Cooldowns], CraftingFocus = CraftingFocus,
        FocusStamp = FocusStamp, LearningPoints = LearningPoints, LpStamp = LpStamp, UseFlags = UseFlags, AutoLearn = AutoLearn,
        MountedSince = MountedSince, MountCooldownUntil = MountCooldownUntil,
        Flag = Flag, Faction = Faction, Reputation = Reputation, ReputationAt = ReputationAt,
        JournalTracked = JournalTracked is { } tracked ? [.. tracked] : null,
        Expeditions = Expeditions?.Clone(),
        Goals = Goals?.Clone(),
        BlackMarket = BlackMarket,
        Infamy = Infamy?.Clone(),
        MistStanding = MistStanding, MistFavor = MistFavor,
        Loadouts = [.. Loadouts],
        FactionStanding = [.. FactionStanding], FactionPoints = [.. FactionPoints], FactionChangedAt = FactionChangedAt,
        Vanity = Vanity?.Clone(),
    };

}

/// <summary>
/// The auto-learn choice of a character (SetAchievementsAutoLearn 312 / FullAutoLearnAchievementInfo 155, stageH ops-audit §5): the node ids,
/// the LearnGroupType bitmask (key 2), per AchievementCategory a byte (key 3; null = never sent) and the per-group lists (key 4; null = none).
/// Immutable (a change makes a new one), so a character's copies may share it.
/// </summary>
public sealed record AutoLearnSet(int[] Ids, byte Groups, byte[]? Categories, int[][]? PerGroup);

/// <summary>The Black Market sales of a character: the day (<see cref="Systems.BlackMarket.Day"/>) and the silver sold on it (a7m raw, before tax). Immutable.</summary>
public sealed record BlackMarketLog(int Day, long Sold);

/// <summary>
/// A character's cosmetics (analysis/work/stageN/vanity/SPEC.md §2): the vanity unlock ids it got (<see cref="Data.VanityCatalog"/>), its
/// wardrobe (seven wardrobe skin numbers by EquipmentSlot, -1 = none: Join 108, NewCharacter 55) and its mount skin per mount category.
/// </summary>
public sealed class VanityLog
{
    public List<short> Unlocks { get; set; } = [];

    public int[] Wardrobe { get; set; } = [-1, -1, -1, -1, -1, -1, -1];

    /// <summary>Mount category index → mount skin number.</summary>
    public Dictionary<int, int> MountSkins { get; set; } = new();

    public VanityLog Clone() => new() { Unlocks = [.. Unlocks], Wardrobe = [.. Wardrobe], MountSkins = new(MountSkins) };
}

/// <summary>
/// A saved active spell of the player's own (ActiveSpellEffectsUpdate keys 1-5): the spell by uniquename, its start
/// (GameTimeStamp ticks), Ap and CcAp, stacks, and per client effect index the duration in ms (-1 = not Active, 0 = until removed).
/// </summary>
public sealed record SavedEffect(string Spell, long Start, float Ap, float CcAp, byte Stacks, int[] Durations)
{
    public float? MovementOriginX { get; init; }
    public float? MovementOriginY { get; init; }
    public string? MovementCluster { get; init; }
}

/// <summary>A saved slot cooldown (SpellCooldownUpdate): CharacterSpellSlot, start and end GameTimeStamps, the base span in ticks.</summary>
public sealed record SavedCooldown(byte Slot, long Start, long End, long Base);

/// <summary>
/// A committed cluster change (SPEC stage 3 §4.6): from and to which cluster, the arrival point (x, z) and facing the new
/// Join sends (keys 9, 10), when (UTC ticks, Join key 101) and by what: "exit:&lt;world.xml exit Guid&gt;" or "tele".
/// Immutable, so a character's copies may share it.
/// </summary>
public sealed record PendingTransfer(string From, string To, float X, float Y, float Direction, long At, string Via);

/// <summary>
/// A character's expedition record (stageH expeditions SPEC §5.1, §8): when each daily bonus was last taken (UTC ticks, by expedition
/// uniquename, or "*" for the per-character bonus), the last expedition completed (a random pick avoids it), the reward items that
/// did not fit the inventory, given at a later Join when they fit, the highest hardcore level open (§12.2 ladder; null = 1) and the end
/// of a leave penalty (UTC ticks, Expeditions:LeavePenalty).
/// </summary>
public sealed class ExpeditionLog
{
    public Dictionary<string, long> Bonus { get; set; } = new(StringComparer.Ordinal);
    public string? Last { get; set; }
    public List<InventoryItem> Unclaimed { get; set; } = [];
    public int? Hardcore { get; set; }
    public long? PenaltyUntil { get; set; }

    public ExpeditionLog Clone() => new()
    {
        Bonus = new(Bonus, StringComparer.Ordinal), Last = Last, Unclaimed = [.. Unclaimed.Select(i => i.Copy())], Hardcore = Hardcore, PenaltyUntil = PenaltyUntil,
    };
}

/// <summary>
/// A character's infamy (stageL corrupted SPEC §2.5, §4.9): the current total and the peak per InfamyType (1 = corrupted dungeons; the
/// client keeps both as int, so they stay below 2^31), the runs entered and the completions per difficulty index (Hunter, Stalker, Slayer).
/// </summary>
public sealed class InfamyLog
{
    public Dictionary<int, long> Current { get; set; } = [];
    public Dictionary<int, long> Peak { get; set; } = [];
    public int Runs { get; set; }
    public int[] Completed { get; set; } = new int[3];

    public InfamyLog Clone() => new() { Current = new(Current), Peak = new(Peak), Runs = Runs, Completed = [.. Completed] };
}

/// <summary>
/// A character's goals (PVE-DESIGN step 11; stageH journals SPEC §B.4, §C.4, kept as one JSON column): the Albion Journal's mission
/// progress by mission key (achievement_index; counts, fame and points a7m raw), the finished achievements (UTC ticks) and the claimed
/// reward ids; the Adventurer's Challenge per season id; the bounties' progress and completions by "period/id"; the PvP goals'
/// anti-farming log and daily points (stage I).
/// </summary>
public sealed class GoalLog
{
    public Dictionary<string, long> Journal { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Done { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> Claimed { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, SeasonLog> Seasons { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Bounties { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> BountiesDone { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Bounty rewards that did not fit the inventory, by "period/id" (given by .bounty claim).</summary>
    public HashSet<string> BountiesOwed { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Season:DailyExtra items that did not fit the inventory, one uniquename per item (given by .bounty claim).</summary>
    public List<string> ItemsOwed { get; set; } = [];

    /// <summary>Stage I PvP goals (Systems.PvpGoals): the GameTimeStamps of the credits per victim key ("c:&lt;id&gt;", "b:&lt;persona&gt;"), the last 24 h.</summary>
    public Dictionary<string, List<long>> PvpVictims { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The GameTimeStamps of the credited duel wins per pair of duelists (their sorted keys, "a|b"), the last 24 h.</summary>
    public Dictionary<string, List<long>> PvpDuels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The bounty day (yyyyMMdd) <see cref="PvpDayPoints"/> and <see cref="PvpRefused"/> count for.</summary>
    public string? PvpDay { get; set; }

    /// <summary>The season points (a7m raw) PvP gave in <see cref="PvpDay"/> (Pvp:DailyPointsCap).</summary>
    public long PvpDayPoints { get; set; }

    /// <summary>The credits refused in <see cref="PvpDay"/> by reason (for .bounty).</summary>
    public Dictionary<string, int> PvpRefused { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The journal's data version: 1 = the reachseasonpoints leaves follow the guild's season points (stage I guild goals; the older
    /// progress, from the personal season total, is dropped once at the login, <see cref="Systems.AlbionJournal.GuildSeasonPoints"/>).
    /// </summary>
    public int JournalVersion { get; set; }

    public GoalLog Clone() => new()
    {
        Journal = new(Journal, StringComparer.Ordinal), Done = new(Done, StringComparer.Ordinal), Claimed = new(Claimed, StringComparer.Ordinal),
        Seasons = Seasons.ToDictionary(s => s.Key, s => s.Value.Clone(), StringComparer.Ordinal),
        Bounties = new(Bounties, StringComparer.Ordinal), BountiesDone = new(BountiesDone, StringComparer.Ordinal), BountiesOwed = new(BountiesOwed, StringComparer.Ordinal),
        ItemsOwed = [.. ItemsOwed],
        PvpVictims = PvpVictims.ToDictionary(v => v.Key, v => v.Value.ToList(), StringComparer.Ordinal),
        PvpDuels = PvpDuels.ToDictionary(v => v.Key, v => v.Value.ToList(), StringComparer.Ordinal),
        PvpDay = PvpDay, PvpDayPoints = PvpDayPoints, PvpRefused = new(PvpRefused, StringComparer.Ordinal), JournalVersion = JournalVersion,
    };
}

/// <summary>
/// One season of the Adventurer's Challenge (SPEC §C.4): per tracker its points (a7m raw, capped) and the GameTimeStamp of its last
/// update (the a35 reset check), the states of the rewards that are not LOCKED (1 UNLOCKED_NOT_CLAIMABLE, 2 UNLOCKED_CLAIMABLE, 3 CLAIMED),
/// and the season's uncapped total (reachseasonpoints).
/// </summary>
public sealed class SeasonLog
{
    public Dictionary<string, long> Points { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, long> Updated { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> Rewards { get; set; } = new(StringComparer.Ordinal);
    public long Total { get; set; }

    /// <summary>The rewards the server gives itself (Season:GrantPremiumRewards) that did not fit the inventory: still UNLOCKED_NOT_CLAIMABLE, given by .bounty claim.</summary>
    public HashSet<string> Owed { get; set; } = new(StringComparer.Ordinal);

    public SeasonLog Clone() => new()
    {
        Points = new(Points, StringComparer.Ordinal), Updated = new(Updated, StringComparer.Ordinal), Rewards = new(Rewards, StringComparer.Ordinal), Total = Total,
        Owed = new(Owed, StringComparer.Ordinal),
    };
}

/// <summary>The way out of a random dungeon that is gone (stageG dungeons SPEC §6.1): cluster, arrival point (x, z), facing. Immutable.</summary>
public sealed record DungeonReturn(string Cluster, float X, float Y, float Direction);

/// <summary>
/// A saved item. Spells are kept by uniquename (null = not chosen), so a renumbered spell table in a new
/// client build does not change them; Durability is the a7m raw value (null = full). Older saves wrote
/// equipment as bare item names; those still load (<see cref="InventoryItemConverter"/>).
/// </summary>
[JsonConverter(typeof(InventoryItemConverter))]
public sealed record InventoryItem(string Name, int Count = 1)
{
    public int Quality { get; init; } = 1;
    public long? Durability { get; init; }
    public string?[]? Active { get; init; }
    public string?[]? Passive { get; init; }

    /// <summary>A journal's fame (a7m raw) when it is neither 0 (_EMPTY) nor the maxfame (_FULL); null = that default (stageH journals SPEC §A.5).</summary>
    public long? Fame { get; init; }

    /// <summary>An awakened weapon's legendary soul (stageN awakening SPEC §2); null = not awakened. Immutable, so copies may share it.</summary>
    public ItemSoul? Soul { get; init; }

    /// <summary>A copy with its own spell name arrays.</summary>
    public InventoryItem Copy() => this with { Active = Active?.ToArray(), Passive = Passive?.ToArray() };
}

/// <summary>Reads an <see cref="InventoryItem"/> from an object or from a bare item name (old saves).</summary>
public sealed class InventoryItemConverter : JsonConverter<InventoryItem>
{
    private sealed record Stored(string Name, int Count = 1, int Quality = 1, long? Durability = null,
        string?[]? Active = null, string?[]? Passive = null, long? Fame = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ItemSoul? Soul = null);

    public override InventoryItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new InventoryItem(reader.GetString()!);
        var s = JsonSerializer.Deserialize<Stored>(ref reader, options);
        return s is null ? null : new InventoryItem(s.Name, s.Count)
            { Quality = s.Quality, Durability = s.Durability, Active = s.Active, Passive = s.Passive, Fame = s.Fame, Soul = s.Soul };
    }

    public override void Write(Utf8JsonWriter writer, InventoryItem value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new Stored(value.Name, value.Count, value.Quality, value.Durability, value.Active, value.Passive, value.Fame, value.Soul), options);
}
