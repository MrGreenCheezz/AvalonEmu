using System.Collections.Concurrent;
using AlbionLocal.Photon;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Scripting;

namespace AlbionLocal.Server;

/// <summary>
/// What every game connection shares: object ids, the character repository and its <see cref="SaveScheduler"/>, the
/// client data tables, the scripts, the maps with their players and the lock. Each <see cref="Network.WorldSession"/>
/// holds only its own connection's state. <see cref="WorldLoop"/> runs <see cref="Update"/> every tick (ARCHITECTURE.md
/// §4.4). Dispose saves what is unsaved and waits for the writer.
/// </summary>
public sealed class World : IDisposable
{
    private readonly ConcurrentQueue<Action> _posted = new();

    /// <summary>Characters that have joined since the start (<see cref="FirstJoin"/>).</summary>
    private readonly HashSet<Guid> _joined = [];

    /// <param name="scripts">The scripts bound to <paramref name="items"/>; null = <see cref="ScriptRegistry.Build"/> (those of the server assembly).</param>
    public World(ServerConfig config, ICharacterRepository characters, ItemStore items, GameData? gameData = null, SpellData? spells = null,
        ScriptRegistry? scripts = null)
    {
        config.Rates.Validate();
        Config = config;
        Characters = characters;
        Items = items;
        GameData = gameData;
        Spells = spells;
        StatCalculator = gameData is null ? null : new StatCalculator(gameData);
        UnlockAll = gameData is null ? null : Progression.UnlockAll(gameData);
        Scripts = scripts ?? ScriptRegistry.Build(items);
        Saves = new SaveScheduler(characters, StorePosition);
        Market = characters as IMarketStore;
        Mail = characters as IMailStore;
        ItemValues = new ItemValues(items);
        _rates = config.Rates;
        MobAi = config.MobAi;
        Parties = new Systems.Parties(this);
        Guilds = new Systems.Guilds(this, characters as IGuildStore);
        Alliances = new Systems.Alliances(this, characters as IAllianceStore);
        GuildGoals = new Systems.GuildGoals(this);
        Duels = new Systems.Duels(this);
        Recaps = new Systems.DeathRecaps(this);
        Trades = new Systems.Trades(this);
        Dungeons = new Systems.RandomDungeons(this);
        WorldChests = new Systems.WorldChests(this);
        StaticDungeons = new Systems.StaticDungeons(this);
        Checks = new Systems.Checklist(this);
        Bosses = new Systems.WorldBosses(this);
        Events = new Systems.WorldEvents(this);
        Invasions = new Systems.PlayerInvasions(this);
        Expeditions = new Systems.Expeditions(this);
        Corrupted = new Systems.CorruptedDungeons(this);
        Hellgates = new Systems.Hellgates(this);
        Arenas = new Systems.Arenas(this);
        Ratings = new Systems.Ratings(this);
        Tournament = new Systems.Tournament(this);
        Roads = new Systems.AvalonRoads(this, characters as IRoadStore);
        Mists = new Systems.Mists(this);
        Factions = new Systems.Factions(this);
        FakeCasts = new Systems.FakeSlotCasts(this);
        ExtraBars = new Systems.ExtraBars(this);
        Shrines = new Systems.BuffShrines(this);
        Territories = new Systems.Territories(this, characters as ITerritoryStore);
        Bounties = new Systems.Bounties(this);
        Hunts = new Systems.Hunts(this);
        Vanity = new Systems.Vanity(this);
        Islands = new Systems.Islands(this, characters as IIslandStore);
        Hideouts = new Systems.Hideouts(this, characters as IHideoutStore);
        Chat = new Systems.ChatHub(this);
        Social = new Systems.Social(this, characters as ISocialStore);
        Bots = new Bots.BotManager(this);
        Companions = new Bots.Companions(this);
        Plugins = new Plugins.PluginHost(this);
    }

    /// <summary>The C# plugins (Plugins:*, server/plugins/README.md): loaded by ServerHost with Plugins:Enabled; none in a bare World.</summary>
    public Plugins.PluginHost Plugins { get; }

    public ServerConfig Config { get; }

    /// <summary>The saved characters (copies); a joined character is loaded with <see cref="LoadCharacter"/>.</summary>
    public ICharacterRepository Characters { get; }

    /// <summary>Saves the live characters: snapshot under <see cref="Sync"/>, write on its own thread.</summary>
    public SaveScheduler Saves { get; }

    public ItemStore Items { get; }

    /// <summary>Item values (the repair cost and crafting numbers of stage 6), worked out from items.xml and cached.</summary>
    public ItemValues ItemValues { get; }

    /// <summary>The marketplace's orders (stage 6d): the repository's own when it has one (SQLite); null = no market (every list is empty).</summary>
    public IMarketStore? Market { get; init; }

    /// <summary>The mailboxes (stageJ mail SPEC §4.2, <see cref="Systems.Mailer"/>): the repository's own when it has one (SQLite); null = no mail (an empty mailbox).</summary>
    public IMailStore? Mail { get; init; }

    /// <summary>When <see cref="Systems.MarketExpiry.Update"/> looks again (clock time); null before the first tick.</summary>
    internal TimeSpan? MarketSweepDue { get; set; }

    /// <summary>When <see cref="Systems.LearningPoints.Update"/> pays the online players' learning points again (clock time); null before the first tick.</summary>
    internal TimeSpan? LearningTickDue { get; set; }

    /// <summary>The crafting and refining bonus per city (craftingmodifiers.bin); none in hand-made test tables.</summary>
    public CraftingModifiers Crafting { get; init; } = CraftingModifiers.None;

    /// <summary>Awakened weapons and their traits (legendaryitems.bin, stageN awakening SPEC); null = no awakening (hand-made tables).</summary>
    public LegendaryData? Legendary { get; init; }

    /// <summary>Buildings content/spawns adds to the clusters (stage 6: ownerless crafting stations).</summary>
    public StationSpawns Stations { get; init; } = StationSpawns.None;

    /// <summary>The GM's item kits (content/kits.json, .kit); none in hand-made test worlds.</summary>
    public Kits Kits { get; init; } = Kits.None;

    /// <summary>The randomness of crafting (the quality roll); the self-test seeds it.</summary>
    public Random CraftRandom { get; init; } = new();

    /// <summary>The economy numbers of gamedata.xml, or 345319's without game data.</summary>
    public EconomySettings Economy => GameData?.Economy ?? EconomySettings.Default;

    /// <summary>Item stats, constants and achievements; null only in hand-made test tables (default stats, nothing sent for the destiny board).</summary>
    public GameData? GameData { get; }

    public OpeningTutorialData? OpeningTutorial { get; init; }

    /// <summary>StageN qol (SPEC §3): until when (<see cref="Now"/>) each character's /stuck is on cooldown; kept over relogs, not over a restart.</summary>
    public Dictionary<Guid, TimeSpan> StuckCooldowns { get; } = [];

    /// <summary>Spell ids and item spell lists; null only in hand-made test tables (no spells sent, saved choices kept).</summary>
    public SpellData? Spells { get; }

    public StatCalculator? StatCalculator { get; }

    /// <summary>A character's collision radius (<see cref="Game.GameData.CollisionRadius"/>; characters.xml's 0.75 without game data): the arrival push of a cluster change.</summary>
    public float CollisionRadius => (float)(GameData?.CollisionRadius ?? GameData.DefaultCollisionRadius);

    /// <summary>The destiny board state of a character without its own: everything unlocked.</summary>
    public Progression? UnlockAll { get; }

    /// <summary>Item scripts and player hooks (ARCHITECTURE.md §6.1); the game calls them only through it.</summary>
    public ScriptRegistry Scripts { get; }

    public ObjectIds ObjectIds { get; } = new();

    /// <summary>The maps and the joined players on them.</summary>
    public MapManager Maps { get; } = new();

    /// <summary>The parties and open party invitations (stageD party SPEC §7), by character Guid.</summary>
    public Systems.Parties Parties { get; }

    /// <summary>The guilds and open guild invitations (stageH guilds SPEC), loaded from the character database's guild tables.</summary>
    public Systems.Guilds Guilds { get; }

    /// <summary>StageP: the alliances of guilds and their open invitations (analysis/work/stageP/hideouts/SPEC.md), loaded from the character database.</summary>
    public Systems.Alliances Alliances { get; }

    /// <summary>Stage I: the Guild Challenge, the guild season points and the weekly guild goals (analysis/work/stageI/guild-goals/SPEC.md), kept on the guilds.</summary>
    public Systems.GuildGoals GuildGoals { get; }

    /// <summary>
    /// Stage I: the client's guild seasons (gvgseasons / times of Guild:Region, read at the start); null = none (hand-made test tables, or
    /// the files missing): local seasons with the built-in table only. A self-test sets it.
    /// </summary>
    public GvgSeasonData? GvgSeasons { get; set; }

    /// <summary>The duels and open challenges (stageE pvp SPEC §2, <see cref="Systems.Duels"/>).</summary>
    public Systems.Duels Duels { get; }

    /// <summary>The players' last death recaps (stageP SPEC §2.1, <see cref="Systems.DeathRecaps"/>).</summary>
    public Systems.DeathRecaps Recaps { get; }

    /// <summary>The player trades, invited or open (stageJ trade SPEC, <see cref="Systems.Trades"/>).</summary>
    public Systems.Trades Trades { get; }

    /// <summary>
    /// The PvP rules of a map whose cluster is not resolved (yet: its template loads asynchronously at the first Join, a failed load
    /// keeps none): fail safe, <see cref="ClusterPvp.Unresolved"/> (the client parser's defaults, PvpAllowed, stay for a cluster type
    /// worldsettings.xml does not name); the self-test's maps without cluster data set a city's.
    /// </summary>
    public ClusterPvp PvpFallback { get; init; } = ClusterPvp.Unresolved;

    /// <summary>
    /// The PvP rules of every map, whatever its cluster's archetype (the self-test: a real cluster with its objects, which loot bags
    /// need, under the rules of another zone); null = the cluster's own (<see cref="Systems.Pvp.RulesOf"/>).
    /// </summary>
    public ClusterPvp? PvpOverride { get; init; }

    /// <summary>The random dungeons: the exits on the open-world markers, the dungeons behind them and their floor maps (stageG dungeons SPEC).</summary>
    public Systems.RandomDungeons Dungeons { get; }

    /// <summary>The loot chests of the open world: POI chests, boss-point reward chests, camp chests and their local quests (stageH bosses SPEC §4.2-4.4).</summary>
    public Systems.WorldChests WorldChests { get; }

    /// <summary>The static dungeons: their events and value on the world map and in 499 (stageN static-dungeons SPEC).</summary>
    public Systems.StaticDungeons StaticDungeons { get; }

    /// <summary>The client checker: content/checks.json, the .check setup runs, data/checks-results.json (stageN checker).</summary>
    public Systems.Checklist Checks { get; init; }

    /// <summary>The world bosses: the WORLDBOSS spawn points' and content/bosses.json's, their schedule, announcements and loot (stageH bosses SPEC §4.1, §6.1).</summary>
    public Systems.WorldBosses Bosses { get; }

    /// <summary>The world events: scheduled and GM invasions with mob waves, announcements and reward chests (PVE-DESIGN.md §2.H, step 7).</summary>
    public Systems.WorldEvents Events { get; }

    /// <summary>Stage I: the invasions players start (.invasion, the tickets): their rules, cooldowns and chosen tiers (invasions SPEC §2).</summary>
    public Systems.PlayerInvasions Invasions { get; }

    /// <summary>The expeditions: registrations at the agents, the instances, their objectives and rewards (stageH expeditions SPEC).</summary>
    public Systems.Expeditions Expeditions { get; }

    /// <summary>StageL: the corrupted dungeons: the Hell Den entrances, the enter cast, the instances, their points, demon, rewards and infamy (stageL corrupted SPEC).</summary>
    public Systems.CorruptedDungeons Corrupted { get; }

    /// <summary>StageL: the hellgates: the Hell Den shrines, the channels and locks, the instances, their objectives, enemy teams and chests (stageL hellgates SPEC).</summary>
    public Systems.Hellgates Hellgates { get; }

    /// <summary>Stage N: the arena queues and matches (analysis/work/stageN/arena/SPEC.md, <see cref="Systems.Arenas"/>).</summary>
    public Systems.Arenas Arenas { get; }

    /// <summary>Stage N: the duel and arena ladders (<see cref="Systems.Ratings"/>, schema v32).</summary>
    public Systems.Ratings Ratings { get; }

    /// <summary>Stage N: the GM's duel tournament (<see cref="Systems.Tournament"/>).</summary>
    public Systems.Tournament Tournament { get; }

    /// <summary>StageL: the Roads of Avalon: the portal network of the open world and the roads, its portals, charges and links (stageL roads SPEC).</summary>
    public Systems.AvalonRoads Roads { get; }

    /// <summary>StageL: the Mists: the open world's mists portals' enter cast, the zones behind them, their objects, treasures and ways out (stageL mists SPEC).</summary>
    public Systems.Mists Mists { get; }

    /// <summary>StageN: faction warfare's PvE side: enlisting, faction points and standing, the faction trader's gates (stageN faction SPEC).</summary>
    public Systems.Factions Factions { get; }

    /// <summary>The server casts of CharacterSpellSlot.FakeSpell (25): the corrupted dungeon entrance, the mists entrance, the loot chest opening (stageM chest-slot25 NOTES).</summary>
    public Systems.FakeSlotCasts FakeCasts { get; }

    /// <summary>The extra action bars (analysis/work/stageN/extrabars/SPEC.md): the worn items' extra spells, the overrides, the mod's channel.</summary>
    public Systems.ExtraBars ExtraBars { get; }

    /// <summary>StageN: the buff shrines of the dungeon rooms and open-world clusters (NewShrine, UseShrine; stageN shrines SPEC).</summary>
    public Systems.BuffShrines Shrines { get; }

    /// <summary>StageP: the guild territories and castles (world.xml territories, monoliths, castles, the territory window; stageP territories SPEC).</summary>
    public Systems.Territories Territories { get; }

    /// <summary>The owned islands, their instances and journeys (stageH islands SPEC phase 1), loaded from the character database's island table.</summary>
    public Systems.Islands Islands { get; }

    /// <summary>StageP: the guild hideouts (analysis/work/stageP/hideouts/SPEC.md H1-H4).</summary>
    public Systems.Hideouts Hideouts { get; }

    /// <summary>The chat server's channels, its connections and their lines and whispers (stageJ chat SPEC §2.3).</summary>
    public Systems.ChatHub Chat { get; }

    /// <summary>The friends, friend requests and mute lists (stageJ chat SPEC §2.6), loaded from the character database's social tables.</summary>
    public Systems.Social Social { get; }

    /// <summary>The expedition settings (content/expeditions.json; the built-in ones without it; a self-test swaps them under Sync).</summary>
    public ExpeditionSettings ExpeditionSettings { get; set; } = ExpeditionSettings.Default;

    /// <summary>StageL: the corrupted dungeons' tables (content/corrupted.json; the built-in ones without it; a self-test swaps them under Sync).</summary>
    public CorruptedSettings CorruptedSettings { get; set; } = CorruptedSettings.Default;

    /// <summary>StageL: the hellgates' tables (content/hellgates.json; the built-in ones without it; a self-test swaps them under Sync).</summary>
    public HellgateSettings HellgateSettings { get; set; } = HellgateSettings.Default;

    /// <summary>StageL: the Mists' tables (content/mists.json; the built-in ones without it; a self-test swaps them under Sync).</summary>
    public MistsSettings MistsSettings { get; set; } = MistsSettings.Default;

    /// <summary>The world event settings (content/events.json; the built-in ones without it).</summary>
    public EventSettings EventSettings { get; init; } = EventSettings.Default;

    /// <summary>The world boss settings (content/bosses.json; the built-in ones without it).</summary>
    public BossSettings BossSettings { get; init; } = BossSettings.Default;

    /// <summary>The content packs' server-only drops (Mods:Packs, mods/&lt;pack&gt;/server/drops.json; none by default).</summary>
    public Mods.ModDrops ModDrops { get; init; } = Mods.ModDrops.None;

    /// <summary>worldbosses.xml (or its regional variant, BossSettings.DataFile), read at the start; null = no world bosses (hand-made test worlds).</summary>
    public WorldBossData? BossData { get; init; }

    /// <summary>The random dungeon numbers the client data leaves open (content/dungeons.json; the built-in ones without it).</summary>
    public DungeonSettings DungeonSettings { get; init; } = DungeonSettings.Default;

    /// <summary>The labourer journals' filling and merchant (content/journals.json; the built-in ones without it).</summary>
    public JournalSettings Journals { get; init; } = JournalSettings.Default;

    /// <summary>The goals of PVE-DESIGN step 11: Adventurer's Challenge, Albion Journal and bounties (content/goals.json; the official rules and no bounties without it; a self-test swaps them).</summary>
    public GoalSettings Goals { get; set; } = GoalSettings.Default;

    /// <summary>Stage I: the player bots (analysis/work/stageI/bots/SPEC.md): their population per map, spawns, walks and brains.</summary>
    public Bots.BotManager Bots { get; }

    /// <summary>StageN companions: the bots players hired (.companion; analysis/work/stageN/companions/SPEC.md).</summary>
    public Bots.Companions Companions { get; }

    /// <summary>Stage I: the bots' tables (content/bots.json; the built-in ones without it).</summary>
    public BotSettings BotSettings { get; init; } = BotSettings.Default;

    /// <summary>The server bounties: the daily and weekly picks, their counts and rewards (PVE-DESIGN.md §2.I).</summary>
    public Systems.Bounties Bounties { get; }

    /// <summary>The hunts / tracking: tracking toolkits, wild tracks, the hunts' quest chains and escapes (stageN hunts SPEC).</summary>
    public Systems.Hunts Hunts { get; }

    /// <summary>StageN vanity: the cosmetics' drops, the wardrobe and the mount skins (analysis/work/stageN/vanity/SPEC.md).</summary>
    public Systems.Vanity Vanity { get; }

    /// <summary>The logged-in accounts' selected character, login time and Join count (SPEC stage 3 §4.4), set by the login sessions.</summary>
    public AccountStates Accounts { get; } = new();

    /// <summary>The client's cluster data, loaded per cluster at the first Join into it (<see cref="LoadTemplate"/>); null = none (hand-made test tables).</summary>
    public ClusterStore? Clusters { get; init; }

    /// <summary>
    /// How <see cref="LoadTemplate"/> has a cluster read: null = <see cref="ClusterStore.LoadAsync"/> of <see cref="Clusters"/>
    /// (a pool thread). The self-test hands in one whose task it completes itself, to see that a Join does not wait for it.
    /// </summary>
    public Func<string, Task<ClusterTemplate>>? LoadCluster { get; init; }

    /// <summary>
    /// Lock both listeners run their handlers under (ServerHost.Sync); the world loop and the logout timers take
    /// it too. Required, no default: a World with a lock of its own would let its timers race the handlers.
    /// </summary>
    public required object Sync { get; init; }

    /// <summary>The GM commands (chat '.' lines and the server console), built once for the server assembly.</summary>
    public CommandRegistry Commands { get; init; } = CommandRegistry.Default;

    /// <summary>Time source of the ticks; the self-test steps a manual one.</summary>
    public IWorldClock Clock { get; init; } = new RealClock();

    /// <summary>Clock time of the current (or last) tick.</summary>
    public TimeSpan Now { get; private set; }

    /// <summary>Time between the last two ticks (dt), for the systems that will step by it (units, AI).</summary>
    public TimeSpan Delta { get; private set; }

    /// <summary>
    /// The rate modifiers in force: the startup configuration followed by temporary administrative changes.
    /// New calculations use these; already awarded rewards and scheduled timers keep their values.
    /// </summary>
    public Game.Rates Rates
    {
        get => _rates;
        set => ApplyRates(value);
    }

    private Game.Rates _rates;

    /// <summary>Validates the entire candidate before replacing the world's rates under its game-state lock.</summary>
    public void ApplyRates(Game.Rates candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (Sync)
        {
            candidate.Validate();
            _rates = candidate;
        }
    }

    /// <summary>
    /// The factor on every mob respawn time (stage 5 SPEC §5a.2), the old form of <see cref="Game.Rates.Respawn"/>: 1 / it
    /// (.mob respawnscale 0.01 = Rates:Respawn 100 brings a killed mob back within seconds; 0 = the fastest rate).
    /// </summary>
    public double MobRespawnScale
    {
        get => 1 / Rates.Respawn;
        set => Rates = Rates with { Respawn = value <= 1 / Game.Rates.Max ? Game.Rates.Max : Math.Max(Game.Rates.MinSpeed, 1 / value) };
    }

    /// <summary>
    /// Whether the mobs fight back (stage 5 SPEC §5d, <see cref="AI.MobAI"/>): World:MobAi at the start (on), then what a GM
    /// sets (.mob ai on|off). Off: no mob moves, aggroes or swings, and damage gives no threat (5a-5c's standing mobs); the
    /// switch off settles every mob where it is first (<see cref="AI.MobAI.Halt(World)"/>). Under <see cref="Sync"/>.
    /// </summary>
    public bool MobAi
    {
        get => _mobAi;
        set
        {
            if (_mobAi && !value) AI.MobAI.Halt(this);
            _mobAi = value;
        }
    }

    private bool _mobAi;

    /// <summary>
    /// Whether mobs cast the spells of their mobs.xml &lt;AI&gt; (stageD mobspells, <see cref="AI.BehaviourAI"/>): combat.json MobSpells
    /// at the start (on), then what a GM sets (.mob spells on|off). Off: they fight as in 5d, with no CastStart; a cast under way
    /// still ends. Under <see cref="Sync"/>.
    /// </summary>
    public bool MobSpells
    {
        get => _mobSpells ?? Combat.MobSpells;
        set => _mobSpells = value;
    }

    private bool? _mobSpells;

    /// <summary>A test and GM hook: the chance every mob IfChance rolls with instead of its value (null = the data's).</summary>
    public double? MobAiChance { get; set; }

    /// <summary>
    /// The GameTimeStamp of now for combat (stage 5b/5c): what a request's key 0 is clamped to, the stamp of what a timed
    /// action sends and the base its clock time is counted from (<see cref="ClockAt"/>). The UTC time (<see cref="GameTime.Now"/>)
    /// on the server; the self-test hands in one that moves with its manual clock, so that casts, hits and
    /// regeneration follow the ticks it runs.
    /// </summary>
    public Func<long> GameTicks { get; init; } = static () => GameTime.Now;

    /// <summary>Now as a GameTimeStamp (<see cref="GameTicks"/>).</summary>
    public long GameNow => GameTicks();

    /// <summary>The <see cref="Clock"/> time of GameTimeStamp <paramref name="ticks"/>: when a map timer for it is due.</summary>
    public TimeSpan ClockAt(long ticks) => Clock.Now + TimeSpan.FromTicks(ticks - GameNow);

    /// <summary>The combat numbers the client data leaves open (content/combat.json; the built-in ones without it).</summary>
    public CombatSettings Combat { get; init; } = CombatSettings.Default;

    /// <summary>The fishing numbers the client data leaves open (content/fishing.json; the built-in ones without it).</summary>
    public FishingSettings Fishing { get; init; } = FishingSettings.Default;

    /// <summary>
    /// Stage 5c: the items a mob drops, instead of its loot.xml roll (<see cref="Systems.Combat"/>); null = the roll. The
    /// self-test forces a drop with it.
    /// </summary>
    public Func<Entities.Mob, IReadOnlyList<(string Item, int Amount)>>? LootOverride { get; init; }

    /// <summary>
    /// The seed of every map's <see cref="Map.Random"/> (the self-test, for reproducible picks): from the map's creation, again when its
    /// cluster's objects are made (<see cref="MapSpawner"/>); null = a random one per map.
    /// </summary>
    public int? MapSeed { get => Maps.Seed; init => Maps.Seed = value; }

    /// <summary>Time from LogoutStart until the server ends the session (config World:LogoutSeconds).</summary>
    public TimeSpan LogoutTime => Config.LogoutTime;

    /// <summary>The logout time on <paramref name="map"/>: in a road Roads:LogoutSeconds (official 900; stageL roads phase B), else <see cref="LogoutTime"/>.</summary>
    public TimeSpan LogoutTimeIn(Map? map) =>
        Config.Roads.LogoutSeconds is { } road && map?.Template?.Info.Type.StartsWith("TUNNEL_", StringComparison.Ordinal) == true ? TimeSpan.FromSeconds(road) : LogoutTime;

    /// <summary>
    /// Runs <paramref name="action"/> under <see cref="Sync"/> at the start of the next tick. The way for any
    /// other thread (console commands, login, background work) to touch world state. Thread-safe.
    /// </summary>
    public void Post(Action action) => _posted.Enqueue(action);

    /// <summary>
    /// The character a Join plays, under <see cref="Sync"/>: the live one when a joined player already holds it (a
    /// second connection shares it, so neither overwrites the other's changes), else the newest saved state (a
    /// snapshot still waiting for the writer, then the repository). Null = no such character.
    /// </summary>
    public Character? LoadCharacter(Guid id)
    {
        foreach (var map in Maps.Maps)
            foreach (var player in map.Players.Values)
                if (player.Character.Id == id) return player.Character;
        return Saves.Load(id);
    }

    /// <summary>
    /// Under <see cref="Sync"/>, once per Join: whether it is the character's first Join (<see cref="IPlayerHooks.OnLogin"/>)
    /// as far as this run can tell without a saved flag: the character was created since the server started and has not
    /// joined since. A character created in an earlier run that never joined counts as not new.
    /// </summary>
    public bool FirstJoin(Character c) => _joined.Add(c.Id) && c.Created.Ticks >= GameTime.StartTicks;

    /// <summary>
    /// One tick, under <see cref="Sync"/> (<see cref="WorldLoop"/>; the self-test calls it directly): dt from the
    /// clock, then the posted actions, then the visibility of the players who have moved (while one walks, its
    /// extrapolated position crosses into new cells between its Moves), then the harvests that are due and the regrowth of
    /// the harvestables (<see cref="Harvesting.Update"/>, every map), the mob respawns that are due (<see cref="SpawnManager.Update"/>,
    /// every map; before them the map timers and, with <see cref="MobAi"/>, the mob AI, <see cref="AI.MobAI.Update(Map, World)"/>),
    /// then the world timers and the party timers (<see cref="Systems.Parties.Update"/>), then the maps that have been
    /// without players for World:MapIdleUnloadMinutes are unloaded (SPEC stage 3 §5).
    /// </summary>
    public void Update()
    {
        var now = Clock.Now;
        Delta = now - Now;
        Now = now;
        // Only what was queued before this tick: an action that posts again runs in the next one.
        for (var n = _posted.Count; n > 0 && _posted.TryDequeue(out var action); n--)
        {
            try { action(); }
            catch (Exception e) { Log.Error("world", $"posted action failed: {e}"); }
        }
        var utc = DateTime.UtcNow;
        foreach (var map in Maps.Maps)
            foreach (var player in map.Players.Values)
                if (player.Movement is not null) player.UpdateVisibility(utc); // a cell compare unless the cell changed
        // Stage I: the walking bots too, on the bots' clock (the game time; the self-test's manual one).
        var botTime = Bots.Time;
        foreach (var map in Maps.Maps)
            foreach (var bot in map.Bots.Values)
                if (bot.Movement is not null) bot.UpdateVisibility(botTime);
        foreach (var map in Maps.Maps) Harvesting.Update(map, now, Rates.Regrowth);
        foreach (var map in Maps.Maps) Systems.Fishing.Update(map, now, Rates.Regrowth); // stageG: fishing zone charges
        foreach (var map in Maps.Maps) Islands.Update(map, now); // stageH islands: crops that ripen, growth refreshes
        // Combat (stage 5b/5c): the casts, hits, ticks, corpses and loot bags that are due, then the players' combat timeouts.
        foreach (var map in Maps.Maps) map.Timers.Run(now);
        // Mob AI (stage 5d): aggro, chase, swings, leash and idle walks of every map with players.
        if (MobAi)
            foreach (var map in Maps.Maps) AI.MobAI.Update(map, this);
        foreach (var map in Maps.Maps)
        {
            foreach (var player in map.Players.Values) PlayerSystems(player, now);
            foreach (var bot in map.Bots.Values) PlayerSystems(bot, now); // stage I: a bot is a player to these
        }
        foreach (var map in Maps.Maps) SpawnManager.Update(map, this, now);
        foreach (var map in Maps.Maps) WorldChests.Update(map); // stageH: chests gone and due again, the camp quest circles
        Bosses.Update(); // stageH: world bosses due, despawned ones
        Territories.Update(); // stageP P5: the castle fights, capture zones, chests and castle season points
        Hideouts.Update(); // stageP hideouts: construction phases, upgrades, the daily food and tribute
        Events.Update(); // PVE-DESIGN step 7: world events due, their waves, ends and rewards
        // Stage I: the bot populations, walks and brains, guarded so that a fault there never costs the tick its keepalives and saves.
        try { Bots.Update(now); }
        catch (Exception e) { Log.Error("bots", $"bot update failed: {e}"); }
        try { Companions.Update(); } // stageN: companions follow their owners over cluster changes, left behind or stuck
        catch (Exception e) { Log.Error("bots", $"companion update failed: {e}"); }
        Expeditions.Update(); // stageH: ready timeouts, the Join and exit channels, instance timeouts and closing
        FakeCasts.Update(); // the slot-25 server casts: the corrupted dungeon and mists enter casts, the chest openings
        Corrupted.Update(); // stageL: the corrupted instances' time limits and closing
        Shrines.Update(); // stageN: the buff shrines' respawns and faction locks
        Checks.Update(); // stageN checker: the .check setup lines that are due
        Hellgates.Update(); // stageL: the shrine channels and locks, the hellgate instances' objectives, wipes, time limits and closing
        Arenas.Update(); // stage N: the arena queues, ready checks, countdowns, respawns, results and closing
        Roads.Update(); // stageL: the road network's build, its expiries, charge-ups, rescue links and batched saves
        Mists.Update(); // stageL: the zone builds, return portals, treasures, warnings, shutdowns and empty zones
        Bounties.Update(); // PVE-DESIGN step 11: a new day's bounties announced
        Hunts.Update(); // stageN hunts: find casts, track readings, hunt timeouts, escapes and fled targets
        Systems.MarketExpiry.Update(this, now); // stageJ mail: once a minute the expired market orders and mails
        Systems.LearningPoints.Update(this, now); // stageJ focus-lp: once a minute the learning points of the online players
        // World timers: the keepalive of every joined connection, then the saves that are due.
        foreach (var map in Maps.Maps)
            foreach (var player in map.Players.Values)
                player.Session.UpdateKeepalive(now);
        Parties.Update(now); // party invitations that time out, members whose cluster change never arrived
        Guilds.Update(now); // guild invitations that time out
        Alliances.Update(now); // stageP: alliance leave protections that end
        GuildGoals.Update(); // stage I: a new guild season told to the members in game
        Social.Update(now); // stageJ: characters whose cluster change never arrived log out for their friends and chat
        Duels.Update(); // challenges that time out, duel starts, areas and ends (stageE pvp SPEC §2)
        Trades.Update(); // stageJ: trade invitations that lapse, trades whose players are apart, offered items that left the bag
        Dungeons.UpdateExits(); // stageM exit: the click-and-channel entries at random dungeon portals (every tick)
        Hideouts.UpdateChannels(); // stageP hideouts: the 4 s channels into and out of a hideout
        Dungeons.Update(now); // stageG: exit rerolls, despawns and refills, dungeons that close
        Saves.Update(now);
        // The plugins' timers, Tick and storage writes (each call guarded: a plugin's fault never costs the tick).
        try { Plugins.Update(now); }
        catch (Exception e) { Log.Error("plugins", $"plugin update failed: {e}"); }
        if (Config.MapIdleUnload > TimeSpan.Zero)
            // A floor of a live random dungeon stays until the dungeon closes (its own empty lifetime, RandomDungeons.Close).
            // So does an expedition instance's map (Expeditions.Close), a corrupted dungeon's (CorruptedDungeons.Close), a hellgate's (Hellgates.Close)
            // and a mists zone's (Mists.Close).
            // And an open-world map holding a dungeon map's portal, until the portal goes (dungeon-maps SPEC §4).
            foreach (var map in Maps.Maps.Where(m => m.EmptySince is { } since && now - since >= Config.MapIdleUnload && !Dungeons.IsLive(m.Cluster)
                         && !Expeditions.IsLive(m.Cluster) && !Corrupted.IsLive(m.Cluster) && !Hellgates.IsLive(m.Cluster) && !Arenas.IsLive(m.Cluster) && !Mists.IsLive(m.Cluster)
                         && !Dungeons.HoldsMapExit(m)).ToList())
            {
                Bots.Unloaded(map); // stage I: its bots go first (the unload counts humans only)
                if (Maps.Unload(map))
                    Log.Info("world", $"map {map.Cluster} unloaded: no players for {Config.MapIdleUnload.TotalMinutes:0.#} min"
                        + (map.Template is null ? "" : $" ({map.Objects.Count} world objects dropped)"));
            }
    }

    /// <summary>The per-player systems of the tick (a human's or a bot's).</summary>
    private static void PlayerSystems(Entities.Player player, TimeSpan now)
    {
        Systems.Combat.Update(player, now);
        Systems.OpeningTutorial.Update(player);
        Systems.Mounts.Update(player); // the mounted speed buff (stageD mounts SPEC §4.7)
        Systems.Flagging.Update(player); // a flag cast that is due ends (stageE pvp SPEC §3.1)
        Systems.Death.UpdateExecute(player); // an execute that is due kills (stageE pvp SPEC §4.2)
        Systems.Revive.Update(player); // a revive that is due raises its ally (stageN revive)
    }

    /// <summary>
    /// Under <see cref="Sync"/>, at each Join, before the Join's world objects (phase A): gives <paramref name="map"/> its
    /// cluster and the world objects made from it (<see cref="MapSpawner"/>). A cluster the store has resolved already
    /// (<see cref="PrefetchCluster"/> at SelectCharacter, an earlier Join) is taken at once, so the Join can send the
    /// window around the player before its items, as the official server does. Otherwise the cluster is resolved
    /// (<see cref="ClusterStore.LoadAsync"/>) on a pool thread unless that was asked for already, and the map gets it in
    /// the next tick (<see cref="Post"/>), when the players on it get their objects in one late pass
    /// (<see cref="VisibilityTracker.Refresh"/>): the Join waits for no file (ARCHITECTURE.md §4.4). A cluster that cannot
    /// be resolved is warned about and its map stays without objects until the next Join into it tries again (a file
    /// being rewritten by an official update does not stick).
    /// </summary>
    public void LoadTemplate(Map map)
    {
        if (map.Template is not null) return;
        // StageG: a random dungeon floor has its cluster from its layout, made before anyone is sent there (no world.xml entry).
        if (Data.DungeonLayout.IsInstance(map.Cluster))
        {
            if (Dungeons.Floor(map.Cluster) is { } floor)
            {
                map.TemplateRequested = true;
                Populate(map, floor.Template);
            }
            return;
        }
        // StageL mists: a zone too (SPEC §6.2: built before anyone is sent in).
        if (Data.MistsIds.IsInstance(map.Cluster))
        {
            if (Mists.Find(map.Cluster) is { } zone)
            {
                map.TemplateRequested = true;
                Populate(map, zone.Template);
            }
            return;
        }
        // StageH: an expedition instance is a copy of its world.xml cluster (SPEC §2.6).
        var id = TemplateOf(map.Cluster);
        if (LoadCluster is null && Clusters is { } store && store.IsLoaded(id))
        {
            map.TemplateRequested = true;
            Populate(map, store.Get(id)); // cached: no file is read under the lock
            return;
        }
        var load = LoadCluster ?? (Clusters is { } clusters ? clusters.LoadAsync : null);
        if (load is null || map.TemplateRequested) return;
        map.TemplateRequested = true;
        load(id).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                Post(() => Populate(map, task.Result));
                return;
            }
            Log.Warn("world", $"cluster {map.Cluster}: {task.Exception?.InnerException?.Message ?? "load cancelled"}; the map has no world objects until the next Join into it");
            Post(() => map.TemplateRequested = false);
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Has the store resolve <paramref name="cluster"/> on a pool thread if it has not yet (SelectCharacter, on the login
    /// server: the client loads the cluster for several seconds before it sends Join, so the first Join usually finds it
    /// resolved and sends phase A). Touches no world state; a failure is left to the Join, which tries again.
    /// </summary>
    public void PrefetchCluster(string cluster)
    {
        cluster = TemplateOf(cluster);
        if (LoadCluster is not null || Clusters is not { } store || store.IsLoaded(cluster) || Data.DungeonLayout.IsInstance(cluster) || Data.MistsIds.IsInstance(cluster)) return;
        store.LoadAsync(cluster).ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// The resolved cluster <paramref name="cluster"/> when the store has it already (no file is read, so callable under
    /// <see cref="Sync"/>); null when it has not, without cluster data, or with a loader of the self-test's own
    /// (<see cref="LoadCluster"/>), which always goes through <see cref="ResolveCluster"/>.
    /// </summary>
    public ClusterTemplate? CachedCluster(string cluster) =>
        Data.DungeonLayout.IsInstance(cluster) ? Dungeons.Floor(cluster)?.Template
        : Data.MistsIds.IsInstance(cluster) ? Mists.Find(cluster)?.Template
        : LoadCluster is null && Clusters is { } store && store.IsLoaded(TemplateOf(cluster)) ? store.Get(TemplateOf(cluster)) : null;

    /// <summary>Resolves <paramref name="cluster"/> off the world thread (<see cref="LoadCluster"/>, else the store on a pool thread); null without cluster data.</summary>
    public Task<ClusterTemplate>? ResolveCluster(string cluster) =>
        Data.DungeonLayout.IsInstance(cluster) ? Dungeons.Floor(cluster) is { } floor ? Task.FromResult(floor.Template) : null
        : Data.MistsIds.IsInstance(cluster) ? Mists.Find(cluster) is { } zone ? Task.FromResult(zone.Template) : null
        : LoadCluster is { } load ? load(TemplateOf(cluster)) : Clusters?.LoadAsync(TemplateOf(cluster));

    /// <summary>
    /// The world.xml cluster whose data a map of <paramref name="cluster"/> takes: a live expedition instance's world cluster, a corrupted
    /// dungeon's variant of its map for its difficulty (stageL corrupted SPEC §4.2 step 4, <see cref="ClusterVariant"/>), a hellgate's world.xml map, an island's
    /// template of its current level (stageH islands SPEC §1.6), else itself.
    /// </summary>
    private string TemplateOf(string cluster) => Expeditions.WorldOf(cluster) ?? Corrupted.TemplateOf(cluster) ?? Hellgates.WorldOf(cluster) ?? Arenas.WorldOf(cluster) ?? Islands.WorldOf(cluster) ?? Hideouts.WorldOf(cluster) ?? cluster;

    /// <summary>The map's objects from <paramref name="template"/> (once), then the players already on it get theirs.</summary>
    private void Populate(Map map, ClusterTemplate template)
    {
        if (map.Template is not null) return; // the fast path of a later Join was first
        if (map.Unloaded) return; // unloaded while its cluster was read: the next Join builds a new map
        MapSpawner.Populate(map, template, this);
        foreach (var player in map.Players.Values) Systems.TutorialWorld.Prepare(map, player.Character);
        var now = DateTime.UtcNow;
        // Every player's cell off the temporary origin-(0, 0) grid first: a Refresh compares its new cell with the others'
        // cells, which must not be on the old grid (30 cells off at 0000: Leave and NewCharacter again for every pair).
        foreach (var player in map.Players.Values) player.Visibility.ForgetPlayerCell();
        foreach (var player in map.Players.Values) player.Visibility.Refresh(player.PositionAt(now));
    }

    /// <summary>Saves every character with unsaved changes and waits until the writer has stored them. Safe to call twice.</summary>
    public void Dispose()
    {
        lock (Sync)
        {
            Guilds.Flush(); // the guilds' account, experience and contracts (saved every 30 s otherwise)
            Roads.Flush(); // stageL: the road network's changes since its last batch
            // StageL roads phase B: an online character's LastLeftAt is its last cluster change; the shutdown is its logout (the thrown-out rule).
            foreach (var p in Maps.Maps.SelectMany(m => m.Players.Values).Where(p => !p.Character.Transient).ToList())
            {
                p.Character.LastLeftAt = GameNow;
                Saves.MarkDirty(p.Character);
            }
            Saves.SaveAll();
        }
        Saves.Dispose();
    }

    /// <summary>Before a snapshot: a joined character's position is where its player's walk has got to by now.</summary>
    private void StorePosition(Character c)
    {
        foreach (var map in Maps.Maps)
            foreach (var player in map.Players.Values)
                if (player.Character == c) player.StorePosition(DateTime.UtcNow);
    }
}
