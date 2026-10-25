using AlbionLocal.Photon;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Scripting;

namespace AlbionLocal.Server;

/// <summary>
/// The whole server, built the same way for Program and the self-test: the client data tables (read from the client's
/// GameData folder, or handed in by the self-test, which loads them once), the scripts and the starter kit (both
/// checked against the item table), the opcode stubs (checked against the handler tables), the listeners, whose
/// factories create one <see cref="LoginSession"/> / <see cref="WorldSession"/> / <see cref="ChatSession"/> (Chat:Enabled) per connection, the character
/// repository (with the one-time import of the old characters.json) and the <see cref="World"/> every game connection
/// shares with its <see cref="WorldLoop"/>. The listeners and the world loop take one lock (<see cref="Sync"/>), so
/// login, game and chat handlers and ticks never run at the same time; the repository has a lock of its own for the save
/// writer.
/// </summary>
public sealed class ServerHost : IDisposable
{
    private bool _disposed;

    /// <param name="client">The client data to run on; null = <see cref="ClientData.Load"/> from Paths:ClientGameData, which
    /// throws <see cref="InvalidDataException"/> when the folder or a file is missing or broken.</param>
    /// <param name="repositoryFactory">Persistence backend; null keeps SQLite. The host owns the opened repository.</param>
    public ServerHost(ServerConfig config, ClientData? client = null, IGameRepositoryFactory? repositoryFactory = null)
    {
        if (!string.Equals(config.PersistenceProvider, "SQLite", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Persistence:Provider '{config.PersistenceProvider}' is not supported; use SQLite.");
        config.Rates.Validate();
        // Bind the handlers now: a handler on the wrong request class, a second handler for an operation or a
        // [Handles] outside the three sessions throws here and stops the server at startup, not at the first
        // connection (principle 6).
        OpcodeTable.Build(typeof(LoginSession), typeof(WorldSession), typeof(ChatSession));
        // The same for the GM commands: a [Command] the parser cannot bind, or a path used twice, stops the start.
        _ = CommandRegistry.Default;
        // Compressed GameTimeStamps are ms since the Login key 251 value (LoginSession sends GameTime.StartTicks).
        AlbionSerializer.CompressedTimeBase = GameTime.StartTicks;
        Client = client ?? ClientData.Load(config.ClientGameDataPath, config.Clusters);
        // The scripts bound to the item templates: a binding conflict throws here, before anything is bound or opened.
        Scripts = ScriptRegistry.Build(Items);
        foreach (var binding in Scripts.Bindings) Log.Info("scripts", binding);
        Starter = StarterKit.Load(config.StarterPath, Items);
        // The fair start's kit only when new characters get it: an unused file is not read (nor warned about).
        FairStarter = config.NewCharacters == NewCharacterProgression.FairStart ? StarterKit.Load(config.FairStarterPath, Items, StarterKit.FairDefault) : StarterKit.FairDefault;
        Combat = CombatSettings.Load(config.CombatPath);
        Fishing = FishingSettings.Load(config.FishingPath);
        // The config keys over the server's own content (Content/ContentRules): under Preset official the official rules, whatever the files say.
        Dungeons = ContentRules.Apply(config, DungeonSettings.Load(config.DungeonsPath));
        Journals = JournalSettings.Load(config.JournalsPath);
        Bosses = ContentRules.Apply(config, BossSettings.Load(config.BossesPath));
        ModDrops = Mods.ModDrops.Load(config.ModsPath, config.ModPacks, Spells is null ? null : Items); // the content packs' server-only extras
        BossData = LoadBossData(Client, Bosses);
        Events = ContentRules.Apply(config, EventSettings.Load(config.EventsPath));
        // StageL corrupted (SPEC §4.8): with Corrupted:MapsOpenDungeons the corrupted maps open their portal, the tickets move to the Knight's Vows.
        if (config.Corrupted is { Enabled: true, MapsOpenDungeons: true }) Events = Events.MoveTickets();
        Expeditions = ContentRules.Apply(config, ExpeditionSettings.Load(config.ExpeditionsPath));
        Goals = ContentRules.Apply(config, GoalSettings.Load(config.GoalsPath));
        GvgSeasons = LoadGvgSeasons(Client, Goals.Guild);
        Bots = BotSettings.Load(config.BotsPath);
        Corrupted = CorruptedSettings.Load(config.CorruptedPath);
        Hellgates = HellgateSettings.Load(config.HellgatesPath);
        Mists = MistsSettings.Load(config.MistsPath);
        ExpeditionData.SpawnAliases = Expeditions.SpawnAliases; // before the first cluster is resolved (the spawn binding reads it)
        // Answers of side requests without a handler; an unknown operation or field, a bad value or a stub for an
        // operation a session handles throws InvalidDataException here, before a port is opened.
        var opening = config.TutorialOpening ? OpeningTutorialData.Load(config.ClientGameDataPath,
            Client.Clusters ?? throw new InvalidDataException("Tutorial:Opening requires client clusters.")) : null;
        if (opening is not null && GameData?.Board is null)
            throw new InvalidDataException("Tutorial:Opening requires destiny board data.");
        if (opening is not null && (Spells?.Template(Systems.OpeningTutorial.IntroSpell)?.Effects is not [var introStun]
            || introStun.Name.LocalName != "stun" || Data.SpellTemplate.Float(introStun, "time") is not (> 0 and <= 60)))
            throw new InvalidDataException("Tutorial:Opening requires TUTORIAL_STUN with one timed stun effect.");
        Stubs = OpcodeStubs.Load(config.OpcodeStubsPath, code =>
            (OpcodeTable.For(typeof(LoginSession)).Find(code) ?? OpcodeTable.For(typeof(WorldSession)).Find(code) ?? OpcodeTable.For(typeof(ChatSession)).Find(code))?.Method);
        // Legacy saved overrides no longer participate in configuration; leave the file intact.
        var ratesFile = Game.Rates.FilePath(config.SavesPath);
        if (File.Exists(ratesFile))
            Log.Warn("config", $"{ratesFile} is ignored; set permanent rates in world.jsonc. GM changes are temporary.");
        // Both ports before the saves: a start that fails on a port in use (another server still running) stops
        // before the one-time import renames a characters.json that the other server keeps writing.
        // The factories run when a peer connects, after Start: World (it needs the bound config) and Characters are set by then.
        GameServer = new PhotonServer(config.GameEndPoint, "game", peer => new WorldSession(new PhotonPeerAdapter(peer), World!) { Stubs = Stubs }, Sync);
        // StageJ: the chat listener (SPEC §2.2), with Chat:Enabled only; its port in use stops the start as the other two do.
        try
        {
            ChatServer = config.ChatEnabled
                ? new PhotonServer(config.ChatEndPoint, "chat", peer => new ChatSession(new PhotonPeerAdapter(peer), World!) { Stubs = Stubs }, Sync)
                : null;
        }
        catch
        {
            GameServer.Dispose();
            throw;
        }
        // The SelectCharacter response names the game and chat servers; with port 0 only the bound ones are right.
        Config = config with { GameEndPoint = GameServer.EndPoint, ChatEndPoint = ChatServer?.EndPoint ?? config.ChatEndPoint };
        // Every client, the local one too, is sent to PublicAddress for the game: a loopback-only listener is not there.
        if (System.Net.IPAddress.IsLoopback(config.GameEndPoint.Address) && config.PublicAddress is { } pub && !System.Net.IPAddress.IsLoopback(pub))
            Log.Warn("config", $"Network:BindAddress {config.GameEndPoint.Address} is loopback but Network:PublicAddress {pub} is not: no client can reach the game server; use BindAddress 0.0.0.0");
        try
        {
            LoginServer = new PhotonServer(config.LoginEndPoint, "login",
                peer => new LoginSession(new PhotonPeerAdapter(peer), Config, Characters!, Starter) { World = World!, Stubs = Stubs, Throttle = Throttle, FairStarter = FairStarter }, Sync);
            Characters = OpenCharacters(Config, Starter, repositoryFactory ?? new SqliteRepositoryFactory());
        }
        catch
        {
            // A failed start leaves no port bound.
            LoginServer?.Dispose();
            ChatServer?.Dispose();
            GameServer.Dispose();
            throw;
        }
        // After the ports, as the import: a start that fails next to a running server leaves the record alone.
        Client.Record(Config.SavesPath);
        World = new World(Config, Characters, Items, GameData, Spells, Scripts)
        {
            OpeningTutorial = opening,
            Sync = Sync, Clusters = Client.Clusters, Combat = Combat, Fishing = Fishing, DungeonSettings = Dungeons, Journals = Journals, BossSettings = Bosses, BossData = BossData, ModDrops = ModDrops, GvgSeasons = GvgSeasons, EventSettings = Events, ExpeditionSettings = Expeditions, Goals = Goals, BotSettings = Bots, CorruptedSettings = Corrupted, HellgateSettings = Hellgates, MistsSettings = Mists, Crafting = Client.Crafting, Legendary = Client.Legendary, Stations = StationSpawns.Load(Config.SpawnsPath),
            // Checked against the item table of real client data (spells.bin with it); a self-test's hand-made table lacks the kits' items.
            Kits = Config.Corrupted is { Enabled: true, MapsOpenDungeons: true } ? Kits.Load(Config.KitsPath, Spells is null ? null : Items).MoveTickets()
                : Kits.Load(Config.KitsPath, Spells is null ? null : Items),
        };
        // Apply a complete validated set; later GM changes remain session-only.
        World.ApplyRates(Config.Rates);
        if (World.Rates != new Game.Rates())
            Log.Info("config", $"rates: {World.Rates.Describe(changedOnly: true)}");
        World.Bots.LoadNames(); // stage I: the names bots never take, read here rather than in a tick
        // The C# plugins (Plugins:Enabled; server/plugins/README.md): found, built when their sources changed, loaded. A plugin that fails
        // is logged and left out; the server starts anyway.
        lock (World.Sync) World.Plugins.LoadAll();
        // The no-code rules of the packs in Mods:Packs (mods/<pack>/rules/*.json; server/plugins/RULES.md): one built-in plugin, "rules".
        lock (World.Sync) World.Plugins.Rules.Reload();
        // The extra action bars' item table (content/extraspells.json + the packs'): read now so its warnings come at the start.
        lock (World.Sync) _ = World.ExtraBars.Table;
        Loop = new WorldLoop(World);
    }

    /// <summary>The configuration in use, with the game endpoint the listener is bound to.</summary>
    public ServerConfig Config { get; }

    /// <summary>The password checks per address of all login connections.</summary>
    private LoginThrottle Throttle { get; } = new();

    /// <summary>The one lock of both listeners; take it when touching game state from outside a handler.</summary>
    public object Sync { get; } = new();

    public IGameRepository Characters { get; }

    /// <summary>Start cluster, position and kit of new characters (content/starter.json).</summary>
    public StarterKit Starter { get; }

    /// <summary>The kit of a new character under Progression:NewCharacters = FairStart (content/starter-fair.json).</summary>
    public StarterKit FairStarter { get; }

    /// <summary>Combat numbers the client data leaves open (content/combat.json).</summary>
    public CombatSettings Combat { get; }

    /// <summary>Fishing numbers the client data leaves open (content/fishing.json).</summary>
    public FishingSettings Fishing { get; }

    /// <summary>Random dungeon numbers the client data leaves open (content/dungeons.json).</summary>
    public DungeonSettings Dungeons { get; }

    /// <summary>The labourer journals' filling and merchant (content/journals.json).</summary>
    public JournalSettings Journals { get; }

    /// <summary>The world bosses' settings (content/bosses.json).</summary>
    public BossSettings Bosses { get; }

    /// <summary>The content packs' server-only drops (Mods:Packs).</summary>
    public Mods.ModDrops ModDrops { get; }

    /// <summary>The world events' settings (content/events.json).</summary>
    public EventSettings Events { get; }

    /// <summary>The expeditions' settings (content/expeditions.json).</summary>
    public ExpeditionSettings Expeditions { get; }

    /// <summary>content/goals.json (PVE-DESIGN step 11), read at the start.</summary>
    public GoalSettings Goals { get; }

    /// <summary>Stage I: the player bots' tables (content/bots.json), read at the start.</summary>
    public BotSettings Bots { get; }

    /// <summary>StageL: the corrupted dungeons' tables (content/corrupted.json), read at the start.</summary>
    public CorruptedSettings Corrupted { get; }

    /// <summary>StageL: the hellgates' tables (content/hellgates.json), read at the start.</summary>
    public HellgateSettings Hellgates { get; }

    /// <summary>StageL: the Mists' treasures, spacing and chat lines (content/mists.json).</summary>
    public MistsSettings Mists { get; }

    /// <summary>worldbosses.xml (or worldbosses_asia.xml, BossSettings.DataFile) of the client data; null without it (no world bosses).</summary>
    public Data.WorldBossData? BossData { get; }

    /// <summary>
    /// The boss data file of <paramref name="settings"/> from the client's GameData folder (under 1 KB, read at the start so that no map fill
    /// under the world lock reads it); a missing or broken file is logged and gives null: the WORLDBOSS points stay empty.
    /// </summary>
    private static Data.WorldBossData? LoadBossData(ClientData client, BossSettings settings)
    {
        if (!settings.Enabled || client.Clusters is not { } store) return null;
        try
        {
            var data = Data.WorldBossData.Load(Data.GameDataFile.Open(store.Folder, settings.DataFile + ".bin").Root());
            Log.Info("data", $"{settings.DataFile}.bin: {data.Bosses.Count} world bosses");
            return data;
        }
        catch (InvalidDataException e)
        {
            Log.Warn("data", $"{settings.DataFile}.bin: {e.Message}; no world bosses");
            return null;
        }
    }

    /// <summary>Stage I: the client's guild seasons of Guild:Region (gvgseasons / times); null without them (local seasons only).</summary>
    public Data.GvgSeasonData? GvgSeasons { get; }

    /// <summary>
    /// The guild season files of <paramref name="settings"/>' region from the client's GameData folder (a few KB, read at the start like the
    /// boss data); missing or broken ones are logged and give null: the guild goals keep local seasons, the Guild Season window gets no 351.
    /// </summary>
    private static Data.GvgSeasonData? LoadGvgSeasons(ClientData client, GuildGoalSettings settings)
    {
        if (!settings.Enabled || client.Clusters is not { } store) return null;
        try
        {
            var data = Data.GvgSeasonData.Load(store.Folder, settings.Region);
            var now = DateTime.UtcNow;
            Log.Info("data", $"gvgseasons{(settings.Region.Length == 0 ? "" : "_" + settings.Region)}.bin: {data.Seasons.Count} guild seasons ({data.Might.Objectives.Count} might objectives), now "
                + (data.ClientSeason(now) is { } s ? $"{s.Key} (index {s.Index}) to {s.End:yyyy-MM-dd HH:mm} UTC" : $"the local season {data.Current(now, settings.LocalSeasonDays).Key}"));
            return data;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Log.Warn("data", $"gvgseasons: {e.Message}; local guild seasons only");
            return null;
        }
    }

    /// <summary>Responses of side requests without a handler (content/opcode-stubs.json), handed to every session.</summary>
    public OpcodeStubs Stubs { get; }

    /// <summary>The client data tables, with the build and file hashes they were read from.</summary>
    public ClientData Client { get; }

    /// <summary>Item scripts and player hooks of the server assembly (none yet), bound to the item table.</summary>
    public ScriptRegistry Scripts { get; }

    public ItemStore Items => Client.Items;
    public GameData? GameData => Client.GameData;
    public SpellData? Spells => Client.Spells;
    public World World { get; }
    public WorldLoop Loop { get; }
    public PhotonServer LoginServer { get; }
    public PhotonServer GameServer { get; }

    /// <summary>The chat listener (stageJ chat SPEC §2.2); null with Chat:Enabled false.</summary>
    public PhotonServer? ChatServer { get; }

    /// <summary>The database backups (the panel's button, Backup:EveryHours); set by <see cref="Start"/>.</summary>
    public Web.DbBackup? Backups { get; private set; }

    /// <summary>The web panel (Web:Enabled); null when off or its port could not be opened.</summary>
    public Web.WebPanel? WebPanel { get; private set; }

    public void Start()
    {
        Loop.Start(); // first: a bad World:TickMs throws before a listener runs
        LoginServer.Start();
        GameServer.Start();
        ChatServer?.Start();
        Backups = new Web.DbBackup(Config.DatabasePath, Path.Combine(Config.SavesPath, "backups"), Config.Backup, () =>
        {
            lock (World.Sync) World.Saves.SaveAll(); // the characters' unsaved changes, then written (not under the lock)
            World.Saves.Flush();
        });
        if (Config.Web.Enabled)
        {
            // A port in use leaves the game running without the panel (it is not needed to play): said in the log.
            try { WebPanel = new Web.WebPanel(World, Config.Web, Backups, Client.Build); }
            catch (System.Net.Sockets.SocketException e) { Log.Error("web", $"web panel not started on {Config.Web.BindAddress}:{Config.Web.Port}: {e.Message} (Web:Port)"); }
        }
        Log.Info("main", $"{Characters.List().Count} persisted character(s); new character start cluster {World.OpeningTutorial?.Tutorial.StartCluster ?? Starter.Cluster}");
        if (World.OpeningTutorial is not null)
            Log.Info("tutorial", "tutorial lab enabled for newly created characters; existing progress is retained; client-authored island route continues through starter-town departure (visual validation pending)");
        lock (World.Sync) World.Plugins.Started(); // the plugins' ServerStarted
    }

    /// <summary>
    /// Stops the world loop, ends the open duels as a draw, then the listeners (the game first: disconnecting its peers saves their characters; the chat after it),
    /// then the world, which saves whatever is still unsaved and waits until it is written (Ctrl+C loses nothing),
    /// and closes the database. Safe to call twice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WebPanel?.Dispose();
        Backups?.Dispose();
        Loop.Dispose();
        lock (World.Sync) World.Plugins.Shutdown(); // the plugins' ServerStopping, then each unloaded with its storage saved
        lock (World.Sync) World.Duels.CancelAll(); // the wagers back before the sessions close (a disconnect would lose the duel)
        lock (World.Sync) World.Trades.CancelAll(); // the trades end (nothing has moved before a commit)
        GameServer.Dispose();
        ChatServer?.Dispose();
        LoginServer.Dispose();
        World.Dispose();
        Characters.Dispose();
    }

    /// <summary>The character database and legacy import. New installations start without characters.</summary>
    private static IGameRepository OpenCharacters(ServerConfig config, StarterKit starter, IGameRepositoryFactory factory)
    {
        var characters = factory.Open(config);
        try
        {
            JsonSaveImport.Run(characters, config.JsonSavePath, starter);
            // Accounts made before the per-account levels were stored at the old shared LocalAccountLevel (often Admin) and keep
            // it: name them once, so a server others reach is not left with GM accounts nobody meant.
            if (characters.Accounts().Where(a => a.Id != Account.LocalId && a.Level == AccountLevel.Admin).ToList() is { Count: > 0 } admins)
                Log.Warn("main", $"{admins.Count} account(s) besides the local one are Admin (all GM commands in chat): "
                    + $"{string.Join(", ", admins.Select(a => a.Shown))}; lower them with the console's .account level <name> Player");
            return characters;
        }
        catch
        {
            characters.Dispose();
            throw;
        }
    }
}
