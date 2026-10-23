using System.Net;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Web;

namespace AlbionLocal.Server;

public sealed record ServerConfig
{
    /// <summary>
    /// The login server: clients connect here
    /// (Address = Network:BindAddress (127.0.0.1), port Network:LoginPort).
    /// </summary>
    public IPEndPoint LoginEndPoint { get; init; } = new(IPAddress.Loopback, 5055);

    /// <summary>The game listener: Network:BindAddress, Network:GamePort.</summary>
    public IPEndPoint GameEndPoint { get; init; } = new(IPAddress.Loopback, 5056);

    /// <summary>
    /// The address the clients are told to connect to for the game server (Network:PublicAddress); null = the bound one.
    /// Needed when the server listens on all interfaces (0.0.0.0) for clients on other PCs.
    /// </summary>
    public IPAddress? PublicAddress { get; init; }

    /// <summary>Address handed to the client in the SelectCharacter and GetGameServerByCluster responses: <see cref="PublicAddress"/>, else the bound one.</summary>
    public string GameServerAddress => $"{PublicAddress ?? GameEndPoint.Address}:{GameEndPoint.Port}";

    /// <summary>
    /// The chat listener (stageJ chat SPEC §2.2): Network:BindAddress, Network:ChatPort (5057; 0 = any free port, as the other two).
    /// The client's third Photon peer ("Albion.ChatServer") connects here when SelectCharacter key 7 names it.
    /// </summary>
    public IPEndPoint ChatEndPoint { get; init; } = new(IPAddress.Loopback, 5057);

    /// <summary>SelectCharacter key 7 (stageJ chat SPEC §1.1, "ip:port" as key 0): <see cref="PublicAddress"/>, else the bound one, and the bound chat port.</summary>
    public string ChatServerAddress => $"{PublicAddress ?? ChatEndPoint.Address}:{ChatEndPoint.Port}";

    /// <summary>
    /// Chat:Enabled (true): the chat server (stageJ chat SPEC: channels, whispers, <see cref="Systems.ChatHub"/>). False: no chat
    /// listener and SelectCharacter key 7 empty, so the client opens no chat peer (Say, party lines and .g as before stageJ).
    /// </summary>
    public bool ChatEnabled { get; init; } = true;

    /// <summary>
    /// Chat:WorldChannel (false; the pve preset true): the default GENERAL tab also holds Global (0), so every player joins one world
    /// channel (SPEC §2.5); false = the official tabs (Global offered but in no tab).
    /// </summary>
    public bool ChatWorldChannel { get; init; }

    /// <summary>Chat:MergeLanguageChannels (false; the pve preset true): a line on any of the 15 language channels (8-22) reaches the members of all of them (SPEC §2.3).</summary>
    public bool ChatMergeLanguageChannels { get; init; }

    /// <summary>
    /// Chat:Announcements (true): the server-wide announcements (world bosses and events with AnnounceToAll, the bounties' new day) go
    /// on the Announcement channel (7, shown in every tab) to the players who joined it, as a SystemMessage to the others [choice].
    /// </summary>
    public bool ChatAnnouncements { get; init; } = true;

    /// <summary>Chat:GlobalCooldownMs (2000, gamedata GlobalMessageCooldownInMs): the time between two lines of a player on one global channel (types 0-5, 8-22); 0 = none.</summary>
    public int ChatGlobalCooldownMs { get; init; } = 2000;

    /// <summary>Chat:WhisperCooldownMs (100, gamedata WhisperCooldownInMs): the time between two whispers of a player (RC 7722 within it); 0 = none.</summary>
    public int ChatWhisperCooldownMs { get; init; } = 100;

    /// <summary>Chat:MaxLineLength (1000, as Say): longer chat lines and whispers are cut.</summary>
    public int ChatMaxLineLength { get; init; } = 1000;

    /// <summary>Chat:StaffTags (true): the lines of GameMaster and Admin accounts carry role 3, which the client shows as "[GM]" (SPEC §1.3).</summary>
    public bool ChatStaffTags { get; init; } = true;

    /// <summary>Social:FriendLimit (200; official unknown): the friends a character may have (FriendInvite 10644 / 10645).</summary>
    public int SocialFriendLimit { get; init; } = 200;

    /// <summary>Social:FriendRequestLimit (50; official unknown): the open friend requests a character may have sent (10646), or received (10647).</summary>
    public int SocialFriendRequestLimit { get; init; } = 50;

    /// <summary>Social:MuteLimit (100; official unknown): the players a character may mute (MutePlayerUpdate mode 3 reason 1).</summary>
    public int SocialMuteLimit { get; init; } = 100;

    /// <summary>
    /// Mail:Enabled (true): the mailbox (stageJ mail SPEC: mail lists, reading, deleting, player mail, attachments, the market and reward
    /// mails, <see cref="Systems.Mailer"/>). False: the empty mailbox of before, SendNewMail refused (RC 1), no mail is made; stored mails stay.
    /// </summary>
    public bool MailEnabled { get; init; } = true;

    /// <summary>Mail:Postage (50, gamedata MailSettings postage): the silver a player mail costs (7082 when short). The write view always shows gamedata's 50.</summary>
    public long MailPostage { get; init; } = 50;

    /// <summary>Mail:DailyLimit (30, gamedata sendmaillimitperday): the player mails a character may send per UTC day (7083); 0 = no limit. The client's box always says 30.</summary>
    public int MailDailyLimit { get; init; } = 30;

    /// <summary>Mail:MarketExpiryDays (30, bd3.d): the market mails go this many days after they came (the row's timer tooltip); 0 = never.</summary>
    public int MailMarketExpiryDays { get; init; } = 30;

    /// <summary>Mail:KeepDays (0): the other mails go this many days after they came; 0 = never. A mail with attachments is never deleted.</summary>
    public int MailKeepDays { get; init; } = 0;

    /// <summary>Mail:MaxPerBox (500, bd3.c [inference]): over it a new mail deletes the mailbox's oldest read mails without attachments.</summary>
    public int MailMaxPerBox { get; init; } = 500;

    /// <summary>Mail:MarketNotices (true): a mail when a market order is filled, expires or is cancelled (SPEC §2, the client's SUMMARY types).</summary>
    public bool MailMarketNotices { get; init; } = true;

    /// <summary>
    /// Mail:RewardOverflow (false; the pve preset true): reward items that do not fit the inventory (expeditions, bounties, the Adventurer's
    /// Challenge, an opened loot box) come as a mail with the items attached; false = the waiting lists (a later login, .bounty claim) as before.
    /// </summary>
    public bool MailRewardOverflow { get; init; }

    /// <summary>Mail:SubjectMax (50) and Mail:TextMax (1000): the longest subject and text of a player mail ([inference]: no client limit found); longer is refused.</summary>
    public int MailSubjectMax { get; init; } = 50;

    /// <inheritdoc cref="MailSubjectMax"/>
    public int MailTextMax { get; init; } = 1000;

    /// <summary>Trade:Enabled (true): the player trade window (stageJ trade SPEC, <see cref="Systems.Trades"/>). False: every invitation gets "cannot trade" (6445).</summary>
    public bool TradeEnabled { get; init; } = true;

    /// <summary>Trade:InviteRange (7 m, the client's bgf.pa [inference: the client checks no distance before it invites]): farther gets 6443.</summary>
    public double TradeInviteRange { get; init; } = 7;

    /// <summary>Trade:KeepRange (9 m, bgf.pa + 2: the client closes its window beyond it): an invitation or a trade ends when the two are farther apart.</summary>
    public double TradeKeepRange { get; init; } = 9;

    /// <summary>Trade:InviteSeconds (15 [choice]; the client's popup closes after 10 s): an unanswered invitation lapses then.</summary>
    public double TradeInviteSeconds { get; init; } = 15;

    /// <summary>
    /// Trade:AcceptDelaySeconds (1.5 [choice]): an accept this soon after a change of either offer is refused (the anti-bait rule). The client
    /// enables its Accept button 2 s after an Update, so more than 2 would refuse honest clicks.
    /// </summary>
    public double TradeAcceptDelaySeconds { get; init; } = 1.5;

    /// <summary>Trade:Slots (11, ctt.ag): the trade slots per side (0-10).</summary>
    public int TradeSlots { get; init; } = 11;

    /// <summary>Trade:ClusterRules (true): the worldsettings archetype's allowtrade (Disabled: 6446, GroupOnly without one party: 6448).</summary>
    public bool TradeClusterRules { get; init; } = true;

    /// <summary>Trade:AllowGold (false): op 167 with gold (the client's window never sends it).</summary>
    public bool TradeAllowGold { get; init; }

    /// <summary>Trade:AllowInCombat (false [choice]; the official rule is not known): whether a player in combat may trade (else "busy", 6444).</summary>
    public bool TradeAllowInCombat { get; init; }

    /// <summary>Folder of the saves (Paths:Saves): <see cref="DatabasePath"/>, and the old characters.json until it is imported.</summary>
    public string SavesPath { get; init; } = Path.Combine(Environment.CurrentDirectory, "data");

    /// <summary>The character database (SQLite, Persistence/SqliteCharacterRepository).</summary>
    public string DatabasePath => Path.Combine(SavesPath, "albionlocal.db");

    /// <summary>The JSON save of the server before step 7; imported once into the database, then renamed (Persistence/JsonSaveImport).</summary>
    public string JsonSavePath => Path.Combine(SavesPath, "characters.json");

    /// <summary>Server content under git (Paths:Content, ARCHITECTURE.md §5.2): starter.json, opcode-stubs.json, combat.json, kits.json.</summary>
    public string ContentPath { get; init; } = Path.Combine(Environment.CurrentDirectory, "content");

    /// <summary>Start cluster, position and kit of new characters (Content/StarterKit): content/starter.json unless set (the self-test
    /// points it at a missing file, so its characters get the built-in T4 kit whatever the shipped one is).</summary>
    public string StarterPath { get => _starterPath ?? Path.Combine(ContentPath, "starter.json"); init => _starterPath = value; }

    private readonly string? _starterPath;

    /// <summary>Start cluster, position and kit of a new character under Progression:NewCharacters = FairStart (Content/StarterKit).</summary>
    public string FairStarterPath => Path.Combine(ContentPath, "starter-fair.json");

    /// <summary>
    /// Progression:NewCharacters: the destiny board a new character starts with. UnlockAll (the default, as before):
    /// none of its own, everything unlocked, no fame on the board; FairStart: an empty board (START in progress) that
    /// fame fills, with the kit of <see cref="FairStarterPath"/>. Existing characters keep theirs (a GM's .progress mode
    /// switches one).
    /// </summary>
    public NewCharacterProgression NewCharacters { get; init; } = NewCharacterProgression.UnlockAll;

    /// <summary>Explicit tutorial lab for NEW characters, from the beach through departure to a starter town.</summary>
    public bool TutorialOpening { get; init; }

    /// <summary>Answers of side requests without a handler (Content/OpcodeStubs).</summary>
    public string OpcodeStubsPath => Path.Combine(ContentPath, "opcode-stubs.json");

    /// <summary>Combat numbers the client data leaves open (Content/CombatSettings, stage 5b/5c).</summary>
    public string CombatPath => Path.Combine(ContentPath, "combat.json");

    /// <summary>Fishing numbers the client data leaves open (Content/FishingSettings, stageG fishing).</summary>
    public string FishingPath => Path.Combine(ContentPath, "fishing.json");

    /// <summary>Random dungeon numbers the client data leaves open (Content/DungeonSettings, stageG dungeons).</summary>
    public string DungeonsPath => Path.Combine(ContentPath, "dungeons.json");

    /// <summary>The labourer journals: filling and the journal merchant (Content/JournalSettings, stageH journals).</summary>
    public string JournalsPath => Path.Combine(ContentPath, "journals.json");

    /// <summary>The world bosses: schedule, announcements, health and damage factors, loot, extra spawns (Content/BossSettings, stageH bosses).</summary>
    public string BossesPath => Path.Combine(ContentPath, "bosses.json");

    /// <summary>The world events: schedule, waves, announcements, rewards (Content/EventSettings, PVE-DESIGN step 7).</summary>
    public string EventsPath => Path.Combine(ContentPath, "events.json");

    /// <summary>The expeditions' settings (Content/ExpeditionSettings, stageH expeditions).</summary>
    public string ExpeditionsPath => Path.Combine(ContentPath, "expeditions.json");

    /// <summary>content/goals.json: the Adventurer's Challenge, the Albion Journal and the bounties (PVE-DESIGN step 11).</summary>
    public string GoalsPath => Path.Combine(ContentPath, "goals.json");

    /// <summary>Stage I bots: the tables (counts per zone, names, gear patterns; Content/BotSettings). The switches and timings are <see cref="Bots"/>.</summary>
    public string BotsPath => Path.Combine(ContentPath, "bots.json");

    /// <summary>StageL corrupted dungeons: the completion rewards, chat lines and map weights (Content/CorruptedSettings). The switches are <see cref="Corrupted"/>.</summary>
    public string CorruptedPath => Path.Combine(ContentPath, "corrupted.json");

    /// <summary>StageL hellgates: the chat lines and map weights (Content/HellgateSettings). The switches are <see cref="Hellgates"/>.</summary>
    public string HellgatesPath => Path.Combine(ContentPath, "hellgates.json");

    /// <summary>StageL mists: the treasure names, spacing and chat lines (Content/MistsSettings). The switches are <see cref="Mists"/>.</summary>
    public string MistsPath => Path.Combine(ContentPath, "mists.json");

    /// <summary>The GM's item kits (Content/Kits, .kit).</summary>
    public string KitsPath => Path.Combine(ContentPath, "kits.json");

    /// <summary>content/spawns: buildings added per cluster (&lt;cluster&gt;.json; stage 6: ownerless crafting stations).</summary>
    public string SpawnsPath => Path.Combine(ContentPath, "spawns");

    /// <summary>Mods:Path (mods): the content packs of the modkit (tools/AlbionLocal.ModKit, MODDING.md), one folder each.</summary>
    public string ModsPath { get; init; } = Path.Combine(Environment.CurrentDirectory, "mods");

    /// <summary>
    /// Mods:Packs (none; a JSON array or a comma list): the packs whose server-only extras (server/drops.json: which mob or chest
    /// drops a pack's item, with what chance) the server applies at the start. The client-side part of a pack (items, spells,
    /// names) is installed into game-local by the modkit's build, never by the server.
    /// </summary>
    public IReadOnlyList<string> ModPacks { get; init; } = Array.Empty<string>();

    /// <summary>Default of Paths:ClientGameData, relative to the folder the server is started in (server/).</summary>
    public const string DefaultClientGameData = "../game-local/Albion-Online_Data/StreamingAssets/GameData";

    /// <summary>
    /// The client's GameData folder (Paths:ClientGameData, ARCHITECTURE.md §5.1): items.bin, spells.bin, gamedata.bin,
    /// characters.bin and achievements.bin are read from here at startup (Data/ClientData).
    /// </summary>
    public string ClientGameDataPath { get; init; } = Path.GetFullPath(DefaultClientGameData, Environment.CurrentDirectory);

    public LogLevel LogLevel { get; init; } = LogLevel.Info;

    /// <summary>File the log lines are appended to as well (Log:File; Program opens it); null = console only.</summary>
    public string? LogFile { get; init; } = Path.Combine(Environment.CurrentDirectory, "data", "server.log");

    /// <summary>
    /// Level of the local account (Accounts:LocalAccountLevel, Admin): the account of the characters of before stage 7, which
    /// the first login name (or Accounts:LegacyName) takes over. It is given when the database is made; later the stored
    /// level counts, which the console's .account level changes. The console itself always runs as Admin.
    /// </summary>
    public AccountLevel LocalAccountLevel { get; init; } = AccountLevel.Admin;

    /// <summary>
    /// Level of every account made from now on (Accounts:DefaultLevel, Player): by the registration dialog, Accounts:AutoRegister
    /// or the console's .account create. Chat GM commands check the speaker's account level (Commands/); .account level
    /// changes it. Accounts made before keep the level they were stored with.
    /// </summary>
    public AccountLevel DefaultAccountLevel { get; init; } = AccountLevel.Player;

    /// <summary>
    /// Accounts:JoinByCharacterId: a Join whose key 0 is a character id plays that character, whatever account the connection
    /// logged in with (off). The self-test's Joins use it; the real client sends its account id, and with it off an account
    /// plays only its own characters (a character's object Guid seen in world events joins nothing).
    /// </summary>
    public bool JoinByCharacterId { get; init; }

    /// <summary>
    /// The login name that takes over the local account with the characters saved before stage 7 (Accounts:LegacyName);
    /// "" = the first name that logs in after the upgrade (with a password: the one it types becomes the account's).
    /// </summary>
    public string LegacyAccountName { get; init; } = "";

    /// <summary>
    /// Accounts:AutoRegister: a Login with a name that has no account and a password makes the account with that password
    /// (false: it is refused with RC 201 like a wrong password; accounts come from the client's registration dialog or the
    /// console's .account create).
    /// </summary>
    public bool AutoRegister { get; init; }

    /// <summary>Accounts:Registration: the client's registration dialog (CreateAccount, op 4) may make accounts (true); false = RC 165.</summary>
    public bool Registration { get; init; } = true;

    /// <summary>Accounts:MaxFailedLogins: wrong passwords in a row that lock the account (5); 0 = never locked.</summary>
    public int MaxFailedLogins { get; init; } = 5;

    /// <summary>Accounts:LockMinutes: how long such a lock lasts (15).</summary>
    public TimeSpan LockTime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Accounts:Maintenance: every Login is refused with RC 204, which the client shows as maintenance (false).</summary>
    public bool Maintenance { get; init; }

    /// <summary>From LogoutStart until the server ends the session; 10 s = every city and safe area.</summary>
    public TimeSpan LogoutTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>World loop period (<see cref="WorldLoop"/>, ARCHITECTURE.md §4.4).</summary>
    public TimeSpan TickTime { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The seasonal activelayer flags of the cluster files that count as on (World:EventLayers, <see cref="KnownEventLayers"/>); none by default.</summary>
    public IReadOnlyList<string> EventLayers { get; init; } = Array.Empty<string>(); // one instance: a default config equals a loaded one

    /// <summary>The flags the cluster files use (345319).</summary>
    public static readonly string[] KnownEventLayers = ["halloween", "christmas", "easter", "anniversary", "anniversary_prelude"];

    /// <summary>Building type id = buildings.xml index + this (ClientNumbering:BuildingIdOffset; SPEC stage 2 §4: 1, as every captured building).</summary>
    public int BuildingIdOffset { get; init; } = 1;

    /// <summary>
    /// The building type id of a placed item (a seed, furniture) = this + its index among the farmableitem and furnitureitem elements of
    /// items.xml (ClientNumbering:ItemBuildingBase; stageH islands SPEC §0.4: 653, empirical, T6_FARM_FOXGLOVE_SEED 665 and a Morgana crate 885 captured).
    /// </summary>
    public int ItemBuildingBase { get; init; } = 653;

    /// <summary>
    /// World:AnnounceAll, a debug switch (SPEC stage 2 §7): every world object of the map at Join, whatever the distance,
    /// and never a Leave, to tell a rendering problem from a visibility one. Off by default.
    /// </summary>
    public bool AnnounceAll { get; init; }

    /// <summary>
    /// World:RareResources: roll enchanted (rare) states for the resource nodes (stage 4b SPEC §5.4; on). Off: every node
    /// is plain and goes in the lists (the self-test compares with captured Joins this way).
    /// </summary>
    public bool RareResources { get; init; } = true;

    /// <summary>
    /// World:OpenWorldChests (true): the loot chests of the open world (stageH bosses SPEC §4.2-4.4): the POI chests of the static
    /// mob areas, the reward chests of their boss points and the camps' personal chests with their local quests. False: none.
    /// </summary>
    public bool OpenWorldChests { get; init; } = true;

    /// <summary>
    /// Chests:PoiLimit (−1; the pve preset 0): how many POI chests stand at once in a cluster. −1 = the data's (worldsettings.xml
    /// poilootchestspawnlimit: 1 in the open world), 0 = every POI spot has its chest, n = at most n.
    /// </summary>
    public int ChestsPoiLimit { get; init; } = -1;

    /// <summary>
    /// Bank:Shared (false in both presets: the user keeps the haul between cities, 2026-10-17): one vault per character for
    /// every city bank (Character.VaultKey location "shared") instead of one per city. A city's vault of before is merged into the shared one when a bank is opened (into its
    /// free slots; what does not fit stays in the city's vault for a later opening); with false again the shared vault goes back
    /// into the first city bank opened the same way, so nothing becomes unreachable.
    /// </summary>
    public bool BankShared { get; init; }

    /// <summary>
    /// Bm:BuyFactor (0; the pve preset 5): the Black Market's NPC buy orders (Systems.BlackMarket, stageH blackmarket SPEC §3): every
    /// tradable T4-T8 equipment item at its item value × this, whole silver; 0 = none (the official server's orders come from
    /// its players, a local server has none).
    /// </summary>
    public double BmBuyFactor { get; init; }

    /// <summary>Bm:DailyLimit (1000000): the silver (before tax) a character may sell to the Black Market per day (from 10:00 UTC); 0 = no limit.</summary>
    public long BmDailyLimit { get; init; } = 1_000_000;

    /// <summary>
    /// The rate modifiers (section Rates: Fame, CombatFame, GatheringFame, CraftingFame, Harvest, Silver, Loot, Respawn,
    /// Regrowth, CraftingReturn; all 1). The world starts from these, with data/rates.json over them (<see cref="Game.Rates.Load"/>);
    /// a GM changes them while the server runs (.rates). World:MobRespawnScale, the respawn time factor of before, is read
    /// as Rates:Respawn = 1 / it (0 = the fastest, <see cref="Game.Rates.Max"/>).
    /// </summary>
    public Game.Rates Rates { get; init; } = new();

    /// <summary>
    /// World:MobAi: the mobs fight back (stage 5 SPEC §5d: aggro, chase, attack, leash, roaming; on). Off: they stand at
    /// their points as in 5a-5c and only take damage; a GM can switch it while the server runs (.mob ai on|off).
    /// </summary>
    public bool MobAi { get; init; } = true;

    /// <summary>
    /// Crafting:Returns (true): a craft at a station gives back part of its resources by the city's crafting bonus
    /// (craftingmodifiers.xml; stage 6 SPEC §6c, the conversion assumed); false = nothing comes back.
    /// </summary>
    public bool CraftingReturns { get; init; } = true;

    /// <summary>
    /// Combat:EquipCooldownSeconds (0): seconds of the potion or food slot's cooldown after an item goes into it (gamedata
    /// SpellCooldowns onequipmentchange 10 exists, but whether the official server applies it is open, stageD consumables SPEC
    /// §2.2 Q3); 0 = none.
    /// </summary>
    public double EquipCooldownSeconds { get; init; }

    /// <summary>
    /// Combat:GearArmor (true, both presets): a player's armour, magic and crowd control resistance and defence vs mobs / players
    /// come from its worn items with their item power scaling, as the client's character window shows them (stageJ armour SPEC
    /// §5.5); false = the raw items.xml armour and magic resistance summed as before stageJ (rollback and A-B checks only).
    /// </summary>
    public bool GearArmor { get; init; } = true;

    /// <summary>
    /// Combat:GearDamageBonus (true, both presets): a player's spells add its worn items' physical|magic spelldamagebonus (chest armours:
    /// plate 0-0.1, leather 0.2-0.3, cloth 0.4-0.5; some off hands × 1.16^(IP/100)) to their damage bonus, as the official server does
    /// (stageM pvp-damage NOTES §3: the captured fire-staff hits to the integer); false = buffs and worn passives only, as before (A-B checks).
    /// </summary>
    public bool GearDamageBonus { get; init; } = true;

    /// <summary>Consumables:BatchInterval (0.5 s): the time between two uses of a batch from the inventory (stageD consumables SPEC §3.2).</summary>
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>
    /// Economy:SilverPickupFactor (1): the factor on a silver pile's worth when it is taken (TakeSilver 62 key 3, with key 7
    /// true when it is not 1). Every own pickup of the captures had 1.5 there: the official player had premium. Nobody has
    /// premium here, so a pile pays its amount × the cluster's silver factor (key 8) and nothing more.
    /// </summary>
    public double SilverPickupFactor { get; init; } = 1;

    /// <summary>
    /// Economy:SilverPickupTax (0): the share of a taken pile's worth that is kept back (TakeSilver 62 key 5). The captures'
    /// 15 % is the official player's guild tax; a guild member pays Guilds:SilverTax into its guild's account instead when that is on.
    /// </summary>
    public double SilverPickupTax { get; init; }

    /// <summary>
    /// Loot:BlackMarketChance (0.1): the chance that a mob drops a rolled loot.xml item marked useblackmarket="true" (all the
    /// DIRECTLOOTDROP equipment). The client's loot printer calls them "drops only via blackmarket": the official server drops
    /// such an item only when its Black Market holds one, which a local server has no stock for. 0.1 fits the 18 captured mob
    /// kills (3 of about 28 expected such items came; stage 5 SPEC §5c.4); 1 drops every roll, 0 none. Mob kills and opened chests
    /// alike; not × Rates:Loot (that is on the loot factor already; loot-balance SPEC §2.1).
    /// </summary>
    public double LootBlackMarketChance { get; init; } = 0.1;

    /// <summary>
    /// Loot:BlackMarketSoftCap (4; 0 = none): in one roll of a mob's or chest's loot, past this many useblackmarket items each further
    /// one only with cap / (its number) (<see cref="Data.LootTables.RollMob"/>): about sqrt(2 × cap × n) of n, so the chests whose
    /// lists name hundreds of Black Market rolls (Avalonian, static and hellgate chests, world bosses) give tens of items, not
    /// hundreds, while a mob or a solo chest (a few) is untouched (loot-balance SPEC §2.3 [design]).
    /// </summary>
    public int LootBlackMarketSoftCap { get; init; } = 4;

    /// <summary>
    /// Pvp:RespawnCluster (""): the cluster a player who died in a lethal zone respawns in (its respawn point, else its first
    /// travel point or exit arrival; stageE pvp SPEC §4.3); "" = the last city it was in (Join key 118), else 0000 (Thetford). The
    /// respawn options of the client (home, last, closest city, …) are not told apart [infer: their order is unknown, SPEC §5 Q7].
    /// </summary>
    public string PvpRespawnCluster { get; init; } = "";

    /// <summary>
    /// Pvp:DeathSilverDrop (false): a player killed by a player in a lethal zone drops gamedata SilverDropFromPvp (0.75) of its
    /// silver next to its corpse, for the killer (stageE pvp SPEC §4.4 [infer: the amount is unproven]); false = it keeps it.
    /// </summary>
    public bool PvpDeathSilverDrop { get; init; }

    /// <summary>
    /// Preset (a root key, not a section): the rules the keys below default to (PVE-DESIGN.md §1). <see cref="ServerPreset.Pve"/>,
    /// what <see cref="Load"/> takes without the key: consensual PvP (Pvp:Mode Consensual), no lethal deaths (Pvp:LethalDeath false,
    /// Pvp:FullLoot false), no wear on a PvP knockdown (Pvp:KnockdownWear 0), a Red flag removed without its lock
    /// (Pvp:RedLockMinutes 0), Loot:BlackMarketChance 0.15, a chest on every POI spot (Chests:PoiLimit 0), the Black Market's NPC buyer (Bm:BuyFactor 5), cheap open guilds (Guilds:CreateCost 100000,
    /// Guilds:JoinRole Member, Guilds:RejoinCooldownMinutes 0) with the PvE guild of SPEC §10 (Guilds:NoFriendlyFire, ShareWithoutParty and
    /// Contracts true, XpShare 0.1, ClearXp 2000, SilverTax 0.05), a free starter island with a farm and a herb garden (Islands:StarterIsland, Islands:StarterBuildings true) whose crops grow 4 times as fast
    /// (Islands:GrowthSpeed 4) and are watered without focus (Islands:WaterFocusFactor 0), one world chat channel in the GENERAL tab with the
    /// language channels merged (Chat:WorldChannel, Chat:MergeLanguageChannels true), everyone premium (Premium:Mode Free) with a new character's
    /// focus and learning points of the premium lifetime reward (Focus:Start 30000, LearningPoints:Start 500), the corrupted dungeons' exit portal from
    /// the start with more infamy and rewards (Corrupted:ExitPortal Always, InfamyFactor 2, EarlyLeaveShare 0.5, RewardFactor 1.5) and invasions only by
    /// consent with a bot invader, nothing lost, one per run, no anti-reset imps and a chain bonus (Corrupted:Invasions / PlayerInvasions Consent,
    /// BotInvaders true, InfamyLossOnDefeat 0, MaxInvasionsPerRun 1, AntiReset false, ChainBonus 0.1), the hellgates' fixed-mode shrine with an enemy
    /// team of bots only in the lethal modes, as many as the party, scaled demons, 30 s knockdowns and .hg leave (Hellgates:ShrineMode Fixed,
    /// VersusModes, EnemyTeamSize Party, CheckItemPower false, BossMapFactor 3, GroupScaling true, KnockdownSeconds 30, LeaveCommand true,
    /// FinishedLingerSeconds 300; phase B RestrictedArea false, SpawnBuffs false, Matchmaking Bots), .roads for every player (Roads:Command All; phase B
    /// Roads:LogoutSeconds "world", Roads:ThrowOutAfterMinutes 0), a private mists zone per team that lasts twice as long, unlocks its treasures
    /// twice as fast, weakens a duo zone's mobs for one player, leads its open-world exit back to the entrance, gives the mist boss's
    /// damagers a reward chest and draws the portals' rarity more often enchanted (Mists:Matchmaking Private, LifetimeFactor 2, BlockFactor
    /// 0.5, DuoSoloScaling 0.6, OpenWorldExit Entrance, UnrestrictedZones false, BossChest true, RarityWeights 6000/1600/1200/800/400; phase B
    /// StandingFactor 2), and the rates Fame 2,

    /// Silver 1.5, Loot 1.5, Respawn 2, Harvest 1.5, Regrowth 2, ChampionWeight 2 (<see cref="PveDefaults"/>). <see cref="ServerPreset.Official"/>: the official rules, the defaults of this record.
    /// Every key given in the file or on the command line wins over its preset's value.
    /// </summary>
    public ServerPreset Preset { get; init; } = ServerPreset.Official;

    /// <summary>
    /// Pvp:Mode: who may attack whom (<see cref="Systems.Pvp.Of(Entities.Player, Entities.Player)"/>). Official (this record's): the
    /// client's table by the zone's archetype; Consensual (the pve preset's): only two Red players (outside one party) and duel
    /// opponents are Hostile, a black zone treated as a red one; Off: only duels.
    /// </summary>
    public PvpMode PvpMode { get; init; } = PvpMode.Official;

    /// <summary>
    /// Pvp:LethalDeath (true; the pve preset false): a lethal zone's (playersarelethal) killing PvP hit kills (stageE E3), and the
    /// execute and the die button exist. False: the hit knocks down as everywhere else (KnockdownKind.Pvp), the player gets up by
    /// itself and loses nothing; no execute (PLAYER_COUPDEGRACE), no die button, no death on a logout while down.
    /// </summary>
    public bool PvpLethalDeath { get; init; } = true;

    /// <summary>
    /// Pvp:FullLoot (true; the pve preset false): a death (only with <see cref="PvpLethalDeath"/>) drops the items into a loot bag and
    /// destroys a share of each stack (gamedata ItemLoss). False: the victim keeps every item; only the durability loss applies.
    /// </summary>
    public bool PvpFullLoot { get; init; } = true;

    /// <summary>
    /// Pvp:KnockdownWear (null; the pve preset 0): the share of each item's maximum durability a PvP knockdown outside a duel takes
    /// (0..1, at least 1 % left); null = gamedata AliveToKnockedOut (5 %, at least 1 % left; rewardtoken nothing), 0 = none.
    /// </summary>
    public double? PvpKnockdownWear { get; init; }

    /// <summary>
    /// Pvp:RedLockMinutes (null; the pve preset 0): how long a Red flag cannot be removed. Null = FLAG_RED's CANNOTDISABLEPVP spell
    /// (15 min, saved as its effect); a number = that many minutes, kept by the server without the spell [choice: not over a relog],
    /// 0 = Blue again whenever asked (the 15 s cast of FLAG_BLUE stays: the client casts it too).
    /// </summary>
    public double? PvpRedLockMinutes { get; init; }

    /// <summary>
    /// Pvp:FriendlySpells (All in both presets; stageJ pvp-spells SPEC §2.3, §2.11): which other players a player's friendly
    /// spells (heals, shields, buffs) reach. All = any Friend (<see cref="Systems.SpellTargets.Friend"/>: not Hostile under Pvp:Mode
    /// nor under the client's own table); Group = only the party and guild mates among them; Self = the caster alone (as before).
    /// </summary>
    public FriendlySpells PvpFriendlySpells { get; init; } = FriendlySpells.All;

    /// <summary>
    /// Pvp:DuelIsolation (true in both presets; SPEC §2.3): no friendly effect of another player reaches a duelist, and a duelist's
    /// reach no one but itself (a duel's preparation counts); false = duelists take and give them as anyone.
    /// </summary>
    public bool PvpDuelIsolation { get; init; } = true;

    /// <summary>
    /// Pvp:Spells (true in both presets; stageJ pvp-spells SPEC §2.11, phase 2): a player's enemy spells (damage, over-time damage,
    /// debuffs, crowd control, knockbacks) reach the players and bots that are Hostile to it by <see cref="Systems.Pvp.Refusal"/>, the
    /// rule auto-attacks use (so under the consensual mode only a Red player, a duel opponent, a bot by the zone's flags); false = they
    /// reach mobs only and a CastStart of an enemy spell at a player is refused (auto-attacks unchanged).
    /// </summary>
    public bool PvpSpells { get; init; } = true;

    /// <summary>
    /// Pve:KnockdownWear (0): the share of each item's maximum durability a mob's knockdown takes (0..1, at least 1 % left); 0 = none,
    /// as the official server (stage 5 SPEC §5e defines no loss). PVE-DESIGN.md §1 suggests 0.01-0.03 for some risk.
    /// </summary>
    public double PveKnockdownWear { get; init; }

    /// <summary>
    /// Pve:PartyRevive (false; the pve preset true; analysis/work/stageN/revive/SPEC.md): a non-lethal knockdown (a mob's, a PvP one outside a
    /// duel) of a player with a party or guild mate on its map is the client's bleed-out state (BleedOut 693, gamedata BleedOutSetting
    /// DEFAULT): a party or guild mate revives it with PLAYER_BLEEDOUT_REVIVE (CharacterSpellSlot 27, 6 s) to 30 % health. Officially only
    /// the zones with enterbleedoutstateondeath (none in 345319's world.xml) have it. False: the plain knockdown (KnockedDown 165).
    /// </summary>
    public bool PvePartyRevive { get; init; }

    /// <summary>The values <see cref="ServerPreset.Pve"/> gives the keys a config leaves out (see <see cref="Preset"/>).</summary>
    public static ServerConfig PveDefaults { get; } = new()
    {
        Preset = ServerPreset.Pve, PvpMode = PvpMode.Consensual, PvpLethalDeath = false, PvePartyRevive = true, PvpFullLoot = false, PvpKnockdownWear = 0,
        PvpRedLockMinutes = 0, LootBlackMarketChance = 0.15, ChestsPoiLimit = 0, BmBuyFactor = 5,
        GuildCreateCost = 100_000, GuildJoinRole = Game.GuildRole.Member, GuildRejoinCooldownMinutes = 0,
        GuildNoFriendlyFire = true, GuildXpShare = 0.1, GuildClearXp = 2000, GuildShareWithoutParty = true, GuildSilverTax = 0.05, GuildContracts = true,
        IslandsStarterIsland = true, IslandsGrowthSpeed = 4, IslandsWaterFocusFactor = 0, IslandsStarterBuildings = true,
        ChatWorldChannel = true, ChatMergeLanguageChannels = true, MailRewardOverflow = true,
        PremiumMode = PremiumMode.Free, FocusStart = 30_000, LearningPointsStart = 500,
        Plugins = new PluginOptions { Enabled = true },
        Rates = new Game.Rates { Fame = 2, Silver = 1.5, Loot = 1, Respawn = 2, Harvest = 1.5, Regrowth = 2, ChampionWeight = 2 },
        Bots = new BotOptions
        {
            // StageN botpower (the user's choice 2026-10-18, "fighting them should be a real risk"): full mastery, the worn spell bonus and full
            // health like an equal-tier player (before: Mastery 0.5, DamageBonus false, HealthFactor 0.7); Bots:Difficulty scales them.
            Enabled = true, CrimesAgainstBots = false, DeathGearDropChance = 0.5, MinItems = 1,
            // The user's choice (2026-10-18): patrols attack Blue players too in the red and black zones (the roads are black), as officially.
            PvpRulesByZone = new Dictionary<string, BotPvpRules>(StringComparer.OrdinalIgnoreCase) { ["red"] = BotPvpRules.Official, ["black"] = BotPvpRules.Official },
        },
        Corrupted = new CorruptedOptions
        {
            ExitPortal = CorruptedExitMode.Always, InfamyFactor = 2, EarlyLeaveShare = 0.5, RewardFactor = 1.5,
            Invasions = CorruptedInvasionMode.Consent, BotInvaders = true, PlayerInvasions = CorruptedInvasionMode.Consent, InfamyLossOnDefeat = 0,
            MaxInvasionsPerRun = 1, AntiReset = false, ChainBonus = 0.1,
        },
        Hellgates = new HellgateOptions
        {
            VersusModes = HellgateOptions.LethalModes, ShrineMode = HellgateShrineMode.Fixed, CheckItemPower = false, BossMapFactor = 3, GroupScaling = true,
            EnemyTeamSize = HellgateEnemyTeam.Party, KnockdownSeconds = 30, LeaveCommand = true, FinishedLingerSeconds = 300,
            RestrictedArea = false, SpawnBuffs = false, Matchmaking = HellgateMatchmaking.Bots, // phase B: no closing area, no spawn debuffs, no party vs party
        },
        Companions = new CompanionOptions { Enabled = true, GearMastery = 0.5, GearDamageBonus = false }, // stageN botpower: the companions keep the old pve bot strength
        Roads = new RoadsOptions { Command = RoadsCommandAccess.All, LogoutSeconds = null, ThrowOutAfterMinutes = 0 }, // phase B: the world's logout, never thrown out
        Web = new WebOptions { Enabled = true }, // the web panel on 127.0.0.1:8080 (SPEC analysis/work/stageN/webpanel)
        Mists = new MistsOptions
        {
            Matchmaking = MistsMatchmaking.Private, LifetimeFactor = 2, BlockFactor = 0.5, DuoSoloScaling = 0.6, OpenWorldExit = MistsExitTarget.Entrance,
            UnrestrictedZones = false, BossChest = true, RarityWeights = [6000, 1600, 1200, 800, 400], StandingFactor = 2, // phase B: twice the Brecilien standing
        },
    };

    /// <summary>Party:MaxSize (20): the most members a party takes with its open invitations (stageD party SPEC §7 [choice]: GroupBonus goes to 20).</summary>
    public int PartyMaxSize { get; init; } = 20;

    /// <summary>
    /// Party:ResendAfterClusterChange (false): PartyJoined again after the Join of a cluster change. The client keeps its party
    /// over one (stageD party SPEC §7 flow 9); switch it on if client check C5 shows the party frame lost.
    /// </summary>
    public bool PartyResendAfterClusterChange { get; init; }

    /// <summary>
    /// Guilds:Enabled (true): guilds (stageH guilds SPEC, <see cref="Systems.Guilds"/>). False: every guild request is refused and
    /// no guild key is sent; saved guilds stay, but a deleted character still leaves its guild and a guild left empty at startup goes.
    /// </summary>
    public bool GuildsEnabled { get; init; } = true;

    /// <summary>Guilds:CreateCost (1000000 silver, official; the pve preset 100000): what CreateGuild takes from the founder (SPEC §3.1).</summary>
    public long GuildCreateCost { get; init; } = 1_000_000;

    /// <summary>Guilds:MaxMembers (300, gamedata maxplayers): a full guild refuses invitations (4252) and joins (4122).</summary>
    public int GuildMaxMembers { get; init; } = 300;

    /// <summary>Guilds:InviteSeconds (70): how long an invitation may be accepted (the client's box times out after 60 s, SPEC §3.2).</summary>
    public int GuildInviteSeconds { get; init; } = 70;

    /// <summary>Guilds:JoinRole (Recruit, official; the pve preset Member): the role of a member that accepts an invitation (SPEC §3.2, §10).</summary>
    public Game.GuildRole GuildJoinRole { get; init; } = Game.GuildRole.Recruit;

    /// <summary>
    /// Guilds:RejoinCooldownMinutes (4320, gamedata guildchangecooldownminutes; the pve preset 0): after leaving or being kicked a
    /// character can be invited (4249) or join (4124) no guild for this long; 0 = no cooldown.
    /// </summary>
    public double GuildRejoinCooldownMinutes { get; init; } = 4320;

    /// <summary>Guilds:CreateCooldownMinutes (0): after creating a guild the character can create no other for this long (4206; not saved over a restart).</summary>
    public double GuildCreateCooldownMinutes { get; init; }

    /// <summary>
    /// Guilds:NoFriendlyFire (false; the pve preset true): guild members are never Hostile to each other outside a duel (SPEC §10.2):
    /// where the table makes them Hostile (two factions in one guild) they are Neutral, so their attacks and areas never hit each other.
    /// </summary>
    public bool GuildNoFriendlyFire { get; init; }

    /// <summary>
    /// Guilds:XpShare (0; the pve preset 0.1): the share of every PvE fame gain of a member (kills, harvests, catches, crafts and the
    /// rewards of bounties, expeditions and events; not PvP, not a GM's grant) that goes to its guild's experience (SPEC §10.3); 0 = none.
    /// </summary>
    public double GuildXpShare { get; init; }

    /// <summary>Guilds:ClearXp (0; the pve preset 2000): the guild experience per tier of a dungeon cleared (once per instance) or an expedition completed by a member (SPEC §10.3).</summary>
    public long GuildClearXp { get; init; }

    /// <summary>
    /// Guilds:Levels (0, 50000, 250000, 1000000, 5000000): the guild experience each guild level starts at (level 1 at the first, the
    /// first is 0). Each level above 1 adds the perks below (SPEC §10.3).
    /// </summary>
    public IReadOnlyList<long> GuildLevels { get; init; } = DefaultGuildLevels;

    /// <summary>The default of Guilds:Levels.</summary>
    public static readonly long[] DefaultGuildLevels = [0, 50_000, 250_000, 1_000_000, 5_000_000];

    /// <summary>Guilds:FamePerLevel (0.02): the PvE fame bonus of the members per guild level above 1.</summary>
    public double GuildFamePerLevel { get; init; } = 0.02;

    /// <summary>Guilds:SilverPerLevel (0.02): the bonus on the silver piles of the members' mob kills per guild level above 1.</summary>
    public double GuildSilverPerLevel { get; init; } = 0.02;

    /// <summary>Guilds:GatheringPerLevel (0.01): the gathering and fishing yield bonus of the members per guild level above 1 (with the gear's).</summary>
    public double GuildGatheringPerLevel { get; init; } = 0.01;

    /// <summary>Guilds:RepairPerLevel (0.05): the repair discount of the members per guild level above 1 (at most 90 %).</summary>
    public double GuildRepairPerLevel { get; init; } = 0.05;

    /// <summary>
    /// Guilds:ShareWithoutParty (false; the pve preset true): guild members near a kill share its fame like party members (the party's
    /// fame sharing radius and group bonus, stageD party SPEC §5) and may take its silver piles, without a party (SPEC §10.4).
    /// </summary>
    public bool GuildShareWithoutParty { get; init; }

    /// <summary>
    /// Guilds:SilverTax (0; the pve preset 0.05): the share of each silver pile a member takes that goes to its guild's account
    /// (TakeSilver key 5, GuildUpdate kinds 0/2 keys 11 and 14) in place of Economy:SilverPickupTax (SPEC §10.5); 0 = the economy's tax,
    /// kept back as before. A MASTER_OF_COIN or higher pays the account out with .guild payout.
    /// </summary>
    public double GuildSilverTax { get; init; }

    /// <summary>
    /// Guilds:Contracts (false; the pve preset true): every guild gets Guilds:ContractCount PvE goals per Guilds:ContractPeriod (kill,
    /// dungeon, gather and expedition goals of a tier its members play at), rewarded with guild experience and silver into its account
    /// (SPEC §10.6; .guild contracts, completion as a system line to the members).
    /// </summary>
    public bool GuildContracts { get; init; }

    /// <summary>Guilds:ContractCount (3): the contracts per period.</summary>
    public int GuildContractCount { get; init; } = 3;

    /// <summary>Guilds:ContractPeriod (Weekly): Weekly (from Monday at the bounties' reset time, 10:00 UTC) or Daily.</summary>
    public GuildContractPeriod GuildContractPeriod { get; init; } = GuildContractPeriod.Weekly;

    /// <summary>Guilds:ContractXp (20000): the guild experience of a completed contract of tier 4 (× tier / 4).</summary>
    public long GuildContractXp { get; init; } = 20_000;

    /// <summary>Guilds:ContractSilver (100000): the silver a completed contract of tier 4 puts into the guild's account (× tier / 4).</summary>
    public long GuildContractSilver { get; init; } = 100_000;

    /// <summary>
    /// Islands:Enabled (true): the personal islands (stageH islands SPEC phase 1, <see cref="Systems.Islands"/>): the merchant lists the
    /// character's islands (GetIslandInfos), the journey there and back, the island instance. False: an empty island list as before,
    /// every island journey refused; a character saved on an island logs in at its city's travel point.
    /// </summary>
    public bool IslandsEnabled { get; init; } = true;

    /// <summary>
    /// Islands:StarterIsland (false; the pve preset true): a character without a personal island gets one free, at
    /// Islands:StarterLevel, when it opens the island merchant of a city that has a personal island type; false = it buys one at the
    /// merchant (SPEC §2.2) or a GM gives it (.island give). A PvE addition beyond the SPEC, not its Islands:FreeIslands ("PvE
    /// direction" 4: purchases at 0 silver).
    /// </summary>
    public bool IslandsStarterIsland { get; init; }

    /// <summary>Islands:StarterLevel (0): the level (0-5, template …0001a-f) of the free starter island.</summary>
    public int IslandsStarterLevel { get; init; }

    /// <summary>
    /// Islands:PremiumDays (31, as captured): Join key 91, the premium days the island merchant compares with the island type's
    /// premiumrequirement (7) before it offers "Buy" or "Upgrade" (stageH islands SPEC §2.1); 0 = the key left out (no buying in the client).
    /// </summary>
    public int IslandsPremiumDays { get; init; } = 31;

    /// <summary>Islands:FreeIslands (false): buying and upgrading an island cost nothing (SPEC "PvE direction" 4); false = the worldsettings.xml prices.</summary>
    public bool IslandsFreeIslands { get; init; }

    /// <summary>
    /// Islands:GrowthSpeed (1; the pve preset 4): how many seconds of growth a crop makes per real second (SPEC §3.3, "PvE direction" 3: 4 makes
    /// the 22 h crop cycle 5.5 h). The client's growth bar runs at 1 between the server's FarmableObjectInfo updates.
    /// </summary>
    public double IslandsGrowthSpeed { get; init; } = 1;

    /// <summary>
    /// Islands:FocusSeedReturn (true): a focus-watered crop's seed chance rises by half its activefarmbonus (= a certain seed for every
    /// crop of the client data; SPEC §3.4, Q8 assumed); false = watering changes nothing at the harvest.
    /// </summary>
    public bool IslandsFocusSeedReturn { get; init; } = true;

    /// <summary>
    /// Islands:WaterFocusFactor (1; the pve preset 0): the share of a seed's activefarmfocuscost (1000) that watering takes from the crafting
    /// focus; 0 = watering is free (stageJ focus-lp D7: kept at 0 in the pve preset although premium focus regenerates now, Q3).
    /// </summary>
    public double IslandsWaterFocusFactor { get; init; } = 1;

    /// <summary>
    /// Islands:StarterBuildings (false; the pve preset true): a new island gets a farm (T1_FARMHOUSE) and a herb garden (T1_HERBGARDEN) at
    /// once, so crops can be grown before building on islands exists (SPEC §3: phase 5); an island without any building (one given before
    /// phase 3) gets them on its map load; false = only a GM builds them (.island build).
    /// </summary>
    public bool IslandsStarterBuildings { get; init; }

    /// <summary>
    /// Premium:Mode (None; the pve preset Free; analysis/work/stageJ/focus-lp/SPEC.md D1): None = nobody is premium (the official preset has
    /// no premium shop: BuyPremium stays refused); Free = every character is premium, always and at no cost (Join keys 90/91, Login key 35,
    /// the premium factors of characters.xml, <see cref="Systems.Premium"/>).
    /// </summary>
    public PremiumMode PremiumMode { get; init; } = PremiumMode.None;

    /// <summary>Premium:FameFactor (null = characters.xml premiumdestinyboardprogressionfactor, 1.5): the destiny board nodes' share of a kill's or harvest's fame while premium.</summary>
    public double? PremiumFameFactor { get; init; }

    /// <summary>Premium:SilverFactor (null = premiumsilverlootfactor, 1.5): a silver pile taken while premium (TakeSilver keys 7, 8), × Economy:SilverPickupFactor.</summary>
    public double? PremiumSilverFactor { get; init; }

    /// <summary>Premium:GatherBonusChance (null = premiumgatheringyieldbonuschance, 0.5): HarvestFinished key 7 = the amount × it (the fraction rounded at random) while premium.</summary>
    public double? PremiumGatherBonusChance { get; init; }

    /// <summary>Premium:CropYieldFactor (null = premiumcropyieldfactor, 2): the items of an island crop's harvest (not its seed) while premium.</summary>
    public double? PremiumCropYieldFactor { get; init; }

    /// <summary>Focus:Max (null = characters.xml craftingfocusmax / premiumcraftingfocusmax, 30000): the crafting focus cap (Join key 28).</summary>
    public double? FocusMax { get; init; }

    /// <summary>Focus:RegenPerDay (null = premiumcraftingfocusregenerationperday, 10000): the focus regained per day while premium (Join key 30 per second); none without premium.</summary>
    public double? FocusRegenPerDay { get; init; }

    /// <summary>Focus:Start (0; the pve preset 30000, the premium lifetime reward of 7 days): the focus of a new character.</summary>
    public double FocusStart { get; init; }

    /// <summary>Focus:Crafting (true): crafting and refining at a station spend focus for the return bonus when the craft window's "use focus" asks for it (UseFlag 1).</summary>
    public bool FocusCrafting { get; init; } = true;

    /// <summary>LearningPoints:SecondsPerPoint (null = learningpointsecondsperpointpremium, 2880 = 30 a day): how often a premium character gains one learning point; none without premium.</summary>
    public double? LearningPointsSecondsPerPoint { get; init; }

    /// <summary>LearningPoints:Start (0; the pve preset 500, the premium lifetime reward): the learning points of a new character.</summary>
    public double LearningPointsStart { get; init; }

    /// <summary>LearningPoints:Max (null = learningpointsmax / learningpointsmaxpremium, 999999): the learning point cap.</summary>
    public double? LearningPointsMax { get; init; }

    /// <summary>The visibility window (Visibility:CellSize, EnterCells, LeaveCells; SPEC stage 2 §5).</summary>
    public VisibilityOptions Visibility { get; init; } = new();

    /// <summary>Stage I: the player bots (Bots:*, <see cref="BotOptions"/>; off under Preset official, on under pve).</summary>
    public BotOptions Bots { get; init; } = new();

    /// <summary>StageL: the corrupted dungeons (Corrupted:*, <see cref="CorruptedOptions"/>; the pve preset's exit portal, infamy and reward factors).</summary>
    public CorruptedOptions Corrupted { get; init; } = new();

    /// <summary>StageL: the hellgates (Hellgates:*, <see cref="HellgateOptions"/>; the pve preset's fixed shrine, versus modes, scaling and knockdowns).</summary>
    public HellgateOptions Hellgates { get; init; } = new();

    /// <summary>Stage N: the arena queues, matches and ratings (Arena:*, <see cref="ArenaOptions"/>).</summary>
    public ArenaOptions Arena { get; init; } = new();

    /// <summary>Stage N: the cosmetics' drops (Vanity:*, <see cref="VanityOptions"/>).</summary>
    public VanityOptions Vanity { get; init; } = new();

    /// <summary>StageL: the Roads of Avalon (Roads:*, <see cref="RoadsOptions"/>; the pve preset's .roads for every player).</summary>
    public RoadsOptions Roads { get; init; } = new();

    /// <summary>StageL: the Mists (Mists:*, <see cref="MistsOptions"/>; the pve preset's private zones, longer lifetime, sooner treasures).</summary>
    public MistsOptions Mists { get; init; } = new();

    /// <summary>Web:* (<see cref="WebOptions"/>): the built-in web panel (off; the pve preset on, at 127.0.0.1:8080).</summary>
    public WebOptions Web { get; init; } = new();

    /// <summary>Backup:* (<see cref="BackupOptions"/>): the database backups into Paths:Saves/backups.</summary>
    public BackupOptions Backup { get; init; } = new();

    /// <summary>StageN companions: the hired companion bots (Companions:*, <see cref="CompanionOptions"/>; off under Preset official, on under pve).</summary>
    public CompanionOptions Companions { get; init; } = new();

    /// <summary>The C# plugins (Plugins:*, <see cref="PluginOptions"/>; off under Preset official, on under pve).</summary>
    public PluginOptions Plugins { get; init; } = new();

    /// <summary>
    /// How long a map stays loaded without players (World:MapIdleUnloadMinutes, 5; 0 = never): then it is dropped with its
    /// objects, and the next Join into the cluster builds it anew with new object ids (SPEC stage 3 §5).
    /// </summary>
    public TimeSpan MapIdleUnload { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The checks of ChangeCluster (Transfer:PositionTolerance, Transfer:CheckExitCooldown; SPEC stage 3 §4.1).</summary>
    public TransferOptions Transfer { get; init; } = new();

    /// <summary>The settings of the cluster resolver (Data/ClusterStore).</summary>
    public ClusterOptions Clusters => new() { EventLayers = new HashSet<string>(EventLayers, StringComparer.Ordinal), BuildingIdOffset = BuildingIdOffset };

    /// <summary>
    /// Reads <paramref name="path"/> (JSON, comments allowed; missing file = all defaults of the pve preset), then applies
    /// <c>--Section:Key=value</c> arguments over it. The root key Preset (pve, the default, or official; <c>--Preset=official</c>)
    /// picks the defaults of the keys it sets (<see cref="Preset"/>). Keys:
    /// Network:BindAddress (IPv4 the listeners bind to, 127.0.0.1; 0.0.0.0 = every interface), Network:PublicAddress (the
    /// game server address given to clients; default = the bind address, required with 0.0.0.0), Network:LoginPort,
    /// Network:GamePort, Network:ChatPort (none of the three equal unless 0), World:LogoutSeconds, World:TickMs, World:EventLayers (a JSON array or a
    /// comma list of <see cref="KnownEventLayers"/>), World:AnnounceAll, World:RareResources, World:OpenWorldChests, Chests:PoiLimit, World:MapIdleUnloadMinutes, World:MobRespawnScale (old name, read as Rates:Respawn = 1 / it), World:MobAi, Visibility:CellSize,
    /// Visibility:EnterCells, Visibility:LeaveCells (more than EnterCells), Transfer:PositionTolerance,
    /// Transfer:CheckExitCooldown, Paths:ClientGameData (the client's GameData folder), Paths:Saves
    /// (albionlocal.db), Paths:Content (starter.json, opcode-stubs.json, combat.json, kits.json), Log:Level, Log:File ("" = no file), Accounts:LocalAccountLevel, Accounts:DefaultLevel, Accounts:JoinByCharacterId,
    /// Accounts:LegacyName, Accounts:AutoRegister, Accounts:Registration, Accounts:MaxFailedLogins, Accounts:LockMinutes,
    /// Accounts:Maintenance, ClientNumbering:BuildingIdOffset, ClientNumbering:ItemBuildingBase, Progression:NewCharacters (UnlockAll or FairStart), Crafting:Returns, Combat:EquipCooldownSeconds, Combat:GearArmor, Combat:GearDamageBonus, Consumables:BatchInterval (seconds), Party:MaxSize, Party:ResendAfterClusterChange, Guilds:Enabled, Guilds:CreateCost, Guilds:MaxMembers, Guilds:InviteSeconds, Guilds:JoinRole, Guilds:RejoinCooldownMinutes, Guilds:CreateCooldownMinutes, Guilds:NoFriendlyFire, Guilds:XpShare, Guilds:ClearXp, Guilds:Levels (a JSON array or a comma list), Guilds:FamePerLevel, Guilds:SilverPerLevel, Guilds:GatheringPerLevel, Guilds:RepairPerLevel, Guilds:ShareWithoutParty, Guilds:SilverTax, Guilds:Contracts, Guilds:ContractCount, Guilds:ContractPeriod (Weekly or Daily), Guilds:ContractXp, Guilds:ContractSilver, Islands:Enabled, Islands:StarterIsland, Islands:StarterLevel, Islands:PremiumDays, Islands:FreeIslands, Islands:GrowthSpeed, Islands:FocusSeedReturn, Islands:WaterFocusFactor, Islands:StarterBuildings, Premium:Mode (None or Free), Premium:FameFactor, Premium:SilverFactor, Premium:GatherBonusChance, Premium:CropYieldFactor, Focus:Max, Focus:RegenPerDay, Focus:Start, Focus:Crafting, LearningPoints:SecondsPerPoint, LearningPoints:Start, LearningPoints:Max (the ones without a default: a number or data), Chat:Enabled, Chat:WorldChannel, Chat:MergeLanguageChannels, Chat:Announcements, Chat:GlobalCooldownMs, Chat:WhisperCooldownMs, Chat:MaxLineLength, Chat:StaffTags, Social:FriendLimit, Social:FriendRequestLimit, Social:MuteLimit, Mail:Enabled, Mail:Postage, Mail:DailyLimit, Mail:MarketExpiryDays, Mail:KeepDays, Mail:MaxPerBox, Mail:MarketNotices, Mail:RewardOverflow, Mail:SubjectMax, Mail:TextMax, Trade:Enabled, Trade:InviteRange, Trade:KeepRange, Trade:InviteSeconds, Trade:AcceptDelaySeconds, Trade:Slots, Trade:ClusterRules, Trade:AllowGold, Trade:AllowInCombat, Economy:SilverPickupFactor, Economy:SilverPickupTax, Loot:BlackMarketChance, Loot:BlackMarketSoftCap, Bank:Shared, Bm:BuyFactor, Bm:DailyLimit, Pvp:RespawnCluster, Pvp:DeathSilverDrop, Pvp:Mode, Pvp:LethalDeath, Pvp:FullLoot,
    /// Pvp:KnockdownWear (0..1 or gamedata), Pvp:RedLockMinutes (minutes or spell), Pvp:FriendlySpells (All, Group or Self), Pvp:DuelIsolation, Pvp:Spells, Pve:KnockdownWear, Rates:<name> (<see cref="Game.Rates.Names"/>), Corrupted:* (<see cref="CorruptedOptions"/>), Hellgates:* (<see cref="HellgateOptions"/>), Roads:* (<see cref="RoadsOptions"/>), Mists:* (<see cref="MistsOptions"/>), Web:* and Backup:* (<see cref="WebOptions"/>), Bots:* (<see cref="BotOptions"/>; a nested
    /// object in the file is a group of keys, "Bots": { "Nav": { "LatticeStep": 4 } }). Relative paths are taken from the working directory.
    /// An unknown key or a bad value throws <see cref="InvalidDataException"/>, so a typo stops the start.
    /// </summary>
    public static ServerConfig Load(string path, IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}: expected an object of sections");
            foreach (var section in doc.RootElement.EnumerateObject())
            {
                if (section.Name.Equals("Preset", StringComparison.OrdinalIgnoreCase) && section.Value.ValueKind == JsonValueKind.String)
                {
                    values["Preset"] = section.Value.GetString()!; // the one root key
                    continue;
                }
                if (section.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}: section {section.Name} is not an object");
                Keys(section.Name, section.Value);
                // A nested object is a group of keys (Bots: { Nav: { LatticeStep } } = Bots:Nav:LatticeStep, stage I).
                void Keys(string prefix, JsonElement obj)
                {
                    foreach (var key in obj.EnumerateObject())
                        if (key.Value.ValueKind == JsonValueKind.Object) Keys($"{prefix}:{key.Name}", key.Value);
                        else values[$"{prefix}:{key.Name}"] = key.Value.ValueKind == JsonValueKind.String ? key.Value.GetString()! : key.Value.GetRawText();
                }
            }
        }
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--") || arg.IndexOf('=') is not (> 2 and var eq)) continue;
            values[arg[2..eq]] = arg[(eq + 1)..];
        }

        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        int Port(string key, int fallback) => Take(key) is not { } v ? fallback
            : int.TryParse(v, out var p) && p is >= 0 and <= 65535 ? p : throw new InvalidDataException($"{key}: '{v}' is not a port");
        string Dir(string key, string fallback = "data") => Path.GetFullPath(Take(key) ?? fallback, Environment.CurrentDirectory);
        int Int(string key, int fallback, int min, int max) => Take(key) is not { } v ? fallback
            : int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var i) && i >= min && i <= max
                ? i : throw new InvalidDataException($"{key}: '{v}' is not {min}..{max}");
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= min && d <= max
                ? d : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(System.Globalization.CultureInfo.InvariantCulture)}..{max.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        // A number, or null (not given, or "data"): the client data's value (characters.xml) is used then.
        double? Data(string key, double min, double max) => Take(key) is not { } v || v.Equals("data", StringComparison.OrdinalIgnoreCase) ? null
            : double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= min && d <= max
                ? d : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(System.Globalization.CultureInfo.InvariantCulture)}..{max.ToString(System.Globalization.CultureInfo.InvariantCulture)} or data");
        AccountLevel Level(string key, AccountLevel fallback) => Take(key) is not { } v ? fallback
            : Enum.TryParse<AccountLevel>(v, ignoreCase: true, out var a) && Enum.IsDefined(a) ? a
            : throw new InvalidDataException($"{key}: '{v}' is not one of {string.Join(", ", Enum.GetNames<AccountLevel>())}");

        IPAddress? Address(string key) => Take(key) is not { } v ? null
            : IPAddress.TryParse(v, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && v.Count(ch => ch == '.') == 3 ? ip
            : throw new InvalidDataException($"{key}: '{v}' is not an IPv4 address");
        var bind = Address("Network:BindAddress") ?? IPAddress.Loopback;
        var publicAddress = Address("Network:PublicAddress");
        if (publicAddress is not null && publicAddress.Equals(IPAddress.Any))
            throw new InvalidDataException("Network:PublicAddress: 0.0.0.0 cannot be connected to; give the address the clients reach this PC at");
        if (publicAddress is null && bind.Equals(IPAddress.Any))
            throw new InvalidDataException("Network:BindAddress 0.0.0.0 needs Network:PublicAddress (the address the clients reach this PC at)");

        var preset = Take("Preset") is not { } presetName ? ServerPreset.Pve
            : Enum.TryParse<ServerPreset>(presetName, ignoreCase: true, out var ps) && Enum.IsDefined(ps) ? ps
            : throw new InvalidDataException($"Preset: '{presetName}' is not pve or official");
        var d = preset == ServerPreset.Pve ? PveDefaults : new ServerConfig(); // the fallbacks of the keys a preset sets
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");

        var (web, backup) = WebOptions.Load(values, d.Web, d.Backup);
        var config = new ServerConfig
        {
            Preset = preset,
            Web = web,
            Backup = backup,
            PvpMode = Take("Pvp:Mode") is not { } mode ? d.PvpMode
                : Enum.TryParse<PvpMode>(mode, ignoreCase: true, out var pm) && Enum.IsDefined(pm) ? pm
                : throw new InvalidDataException($"Pvp:Mode: '{mode}' is not one of {string.Join(", ", Enum.GetNames<PvpMode>())}"),
            PvpLethalDeath = Flag("Pvp:LethalDeath", d.PvpLethalDeath),
            PvpFullLoot = Flag("Pvp:FullLoot", d.PvpFullLoot),
            PvpKnockdownWear = Take("Pvp:KnockdownWear") is not { } wear ? d.PvpKnockdownWear
                : wear.Equals("gamedata", StringComparison.OrdinalIgnoreCase) ? null
                : double.TryParse(wear, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var kw) && kw is >= 0 and <= 1 ? kw
                : throw new InvalidDataException($"Pvp:KnockdownWear: '{wear}' is not 0..1 or gamedata"),
            PvpRedLockMinutes = Take("Pvp:RedLockMinutes") is not { } redLock ? d.PvpRedLockMinutes
                : redLock.Equals("spell", StringComparison.OrdinalIgnoreCase) ? null
                : double.TryParse(redLock, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rl) && rl is >= 0 and <= 1440 ? rl
                : throw new InvalidDataException($"Pvp:RedLockMinutes: '{redLock}' is not 0..1440 or spell"),
            PvpFriendlySpells = Take("Pvp:FriendlySpells") is not { } friendly ? d.PvpFriendlySpells
                : Enum.TryParse<FriendlySpells>(friendly, ignoreCase: true, out var fs) && Enum.IsDefined(fs) ? fs
                : throw new InvalidDataException($"Pvp:FriendlySpells: '{friendly}' is not one of {string.Join(", ", Enum.GetNames<FriendlySpells>())}"),
            PvpDuelIsolation = Flag("Pvp:DuelIsolation", d.PvpDuelIsolation),
            PvpSpells = Flag("Pvp:Spells", d.PvpSpells),
            PveKnockdownWear = Number("Pve:KnockdownWear", d.PveKnockdownWear, 0, 1),
            PvePartyRevive = Flag("Pve:PartyRevive", d.PvePartyRevive),
            LoginEndPoint = new IPEndPoint(bind, Port("Network:LoginPort", 5055)),
            GameEndPoint = new IPEndPoint(bind, Port("Network:GamePort", 5056)),
            ChatEndPoint = new IPEndPoint(bind, Port("Network:ChatPort", 5057)),
            PublicAddress = publicAddress,
            LogoutTime = Take("World:LogoutSeconds") is not { } logout ? TimeSpan.FromSeconds(10)
                : double.TryParse(logout, System.Globalization.CultureInfo.InvariantCulture, out var sec) && sec is >= 0 and <= 3600
                    ? TimeSpan.FromSeconds(sec) : throw new InvalidDataException($"World:LogoutSeconds: '{logout}' is not 0..3600"),
            TickTime = Take("World:TickMs") is not { } tick ? TimeSpan.FromMilliseconds(50)
                : int.TryParse(tick, out var ms) && ms is >= 1 and <= 1000
                    ? TimeSpan.FromMilliseconds(ms) : throw new InvalidDataException($"World:TickMs: '{tick}' is not 1..1000"),
            SavesPath = Dir("Paths:Saves"),
            ContentPath = Dir("Paths:Content", "content"),
            ClientGameDataPath = Dir("Paths:ClientGameData", DefaultClientGameData),
            ModsPath = Dir("Mods:Path", "mods"),
            ModPacks = Take("Mods:Packs") is not { } packs ? Array.Empty<string>() : PackList(packs),
            LogLevel = Take("Log:Level") is not { } level ? LogLevel.Info
                : Enum.TryParse<LogLevel>(level, ignoreCase: true, out var l) ? l
                : throw new InvalidDataException($"Log:Level: '{level}' is not one of {string.Join(", ", Enum.GetNames<LogLevel>())}"),
            LogFile = Take("Log:File") is not { } file ? Path.Combine(Environment.CurrentDirectory, "data", "server.log")
                : file.Length == 0 ? null : Path.GetFullPath(file, Environment.CurrentDirectory),
            JoinByCharacterId = Take("Accounts:JoinByCharacterId") is not { } byId ? false
                : bool.TryParse(byId, out var join) ? join : throw new InvalidDataException($"Accounts:JoinByCharacterId: '{byId}' is not true or false"),
            LocalAccountLevel = Level("Accounts:LocalAccountLevel", AccountLevel.Admin),
            DefaultAccountLevel = Level("Accounts:DefaultLevel", AccountLevel.Player),
            LegacyAccountName = (Take("Accounts:LegacyName") ?? "").Trim(),
            AutoRegister = Take("Accounts:AutoRegister") is not { } auto ? false
                : bool.TryParse(auto, out var autoRegister) ? autoRegister : throw new InvalidDataException($"Accounts:AutoRegister: '{auto}' is not true or false"),
            Registration = Take("Accounts:Registration") is not { } reg ? true
                : bool.TryParse(reg, out var registration) ? registration : throw new InvalidDataException($"Accounts:Registration: '{reg}' is not true or false"),
            MaxFailedLogins = Int("Accounts:MaxFailedLogins", 5, 0, 1000),
            LockTime = TimeSpan.FromMinutes(Int("Accounts:LockMinutes", 15, 1, 100_000)),
            Maintenance = Take("Accounts:Maintenance") is not { } maint ? false
                : bool.TryParse(maint, out var maintenance) ? maintenance : throw new InvalidDataException($"Accounts:Maintenance: '{maint}' is not true or false"),
            NewCharacters = Take("Progression:NewCharacters") is not { } progression ? NewCharacterProgression.UnlockAll
                : Enum.TryParse<NewCharacterProgression>(progression, ignoreCase: true, out var np) && Enum.IsDefined(np) ? np
                : throw new InvalidDataException($"Progression:NewCharacters: '{progression}' is not one of {string.Join(", ", Enum.GetNames<NewCharacterProgression>())}"),
            TutorialOpening = Take("Tutorial:Opening") is not { } tutorial ? false
                : bool.TryParse(tutorial, out var opening) ? opening : throw new InvalidDataException("Tutorial:Opening must be true or false"),
            EventLayers = Take("World:EventLayers") is not { } layers ? Array.Empty<string>() : Flags(layers),
            BuildingIdOffset = Take("ClientNumbering:BuildingIdOffset") is not { } offset ? 1
                : int.TryParse(offset, out var o) && o is >= 0 and <= 1000 ? o
                : throw new InvalidDataException($"ClientNumbering:BuildingIdOffset: '{offset}' is not 0..1000"),
            ItemBuildingBase = Int("ClientNumbering:ItemBuildingBase", 653, 0, 30_000),
            AnnounceAll = Take("World:AnnounceAll") is not { } all ? false
                : bool.TryParse(all, out var b) ? b : throw new InvalidDataException($"World:AnnounceAll: '{all}' is not true or false"),
            RareResources = Take("World:RareResources") is not { } rare ? true
                : bool.TryParse(rare, out var r) ? r : throw new InvalidDataException($"World:RareResources: '{rare}' is not true or false"),
            Rates = LoadRates(values, d.Rates),
            OpenWorldChests = Flag("World:OpenWorldChests", true),
            ChestsPoiLimit = Int("Chests:PoiLimit", d.ChestsPoiLimit, -1, 1000),
            BankShared = Flag("Bank:Shared", d.BankShared),
            BmBuyFactor = Number("Bm:BuyFactor", d.BmBuyFactor, 0, 1000),
            BmDailyLimit = (long)Number("Bm:DailyLimit", d.BmDailyLimit, 0, 1e12),
            CraftingReturns = Take("Crafting:Returns") is not { } returns ? true
                : bool.TryParse(returns, out var cr) ? cr : throw new InvalidDataException($"Crafting:Returns: '{returns}' is not true or false"),
            MobAi = Take("World:MobAi") is not { } ai ? true
                : bool.TryParse(ai, out var mobAi) ? mobAi : throw new InvalidDataException($"World:MobAi: '{ai}' is not true or false"),
            EquipCooldownSeconds = Take("Combat:EquipCooldownSeconds") is not { } equip ? 0
                : double.TryParse(equip, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var equipSeconds) && equipSeconds is >= 0 and <= 600
                    ? equipSeconds : throw new InvalidDataException($"Combat:EquipCooldownSeconds: '{equip}' is not 0..600"),
            GearArmor = Flag("Combat:GearArmor", true),
            GearDamageBonus = Flag("Combat:GearDamageBonus", true),
            BatchInterval = Take("Consumables:BatchInterval") is not { } batch ? TimeSpan.FromSeconds(0.5)
                : double.TryParse(batch, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var bi) && bi is >= 0 and <= 60
                    ? TimeSpan.FromSeconds(bi) : throw new InvalidDataException($"Consumables:BatchInterval: '{batch}' is not 0..60"),
            SilverPickupFactor = Number("Economy:SilverPickupFactor", 1, 0, 100),
            SilverPickupTax = Number("Economy:SilverPickupTax", 0, 0, 1),
            LootBlackMarketChance = Number("Loot:BlackMarketChance", d.LootBlackMarketChance, 0, 1),
            LootBlackMarketSoftCap = Int("Loot:BlackMarketSoftCap", d.LootBlackMarketSoftCap, 0, 1000),
            PartyMaxSize = Int("Party:MaxSize", 20, 2, 100),
            GuildsEnabled = Flag("Guilds:Enabled", true),
            GuildCreateCost = (long)Number("Guilds:CreateCost", d.GuildCreateCost, 0, 1e9),
            GuildMaxMembers = Int("Guilds:MaxMembers", 300, 1, 10_000),
            GuildInviteSeconds = Int("Guilds:InviteSeconds", 70, 5, 3600),
            GuildJoinRole = Take("Guilds:JoinRole") is not { } joinRole ? d.GuildJoinRole
                : Enum.TryParse<Game.GuildRole>(joinRole, ignoreCase: true, out var jr) && jr is >= Game.GuildRole.Recruit and < Game.GuildRole.Guildmaster ? jr
                : throw new InvalidDataException($"Guilds:JoinRole: '{joinRole}' is not one of Recruit, Member, Guardsman, Officer, Warmaster, MasterOfCoin, TheRightHand"),
            GuildRejoinCooldownMinutes = Number("Guilds:RejoinCooldownMinutes", d.GuildRejoinCooldownMinutes, 0, 100_000),
            GuildCreateCooldownMinutes = Number("Guilds:CreateCooldownMinutes", 0, 0, 100_000),
            GuildNoFriendlyFire = Flag("Guilds:NoFriendlyFire", d.GuildNoFriendlyFire),
            GuildXpShare = Number("Guilds:XpShare", d.GuildXpShare, 0, 100),
            GuildClearXp = (long)Number("Guilds:ClearXp", d.GuildClearXp, 0, 1e9),
            GuildLevels = Take("Guilds:Levels") is not { } levels ? DefaultGuildLevels : GuildLevelList(levels),
            GuildFamePerLevel = Number("Guilds:FamePerLevel", 0.02, 0, 10),
            GuildSilverPerLevel = Number("Guilds:SilverPerLevel", 0.02, 0, 10),
            GuildGatheringPerLevel = Number("Guilds:GatheringPerLevel", 0.01, 0, 10),
            GuildRepairPerLevel = Number("Guilds:RepairPerLevel", 0.05, 0, 0.9),
            GuildShareWithoutParty = Flag("Guilds:ShareWithoutParty", d.GuildShareWithoutParty),
            GuildSilverTax = Number("Guilds:SilverTax", d.GuildSilverTax, 0, 1),
            GuildContracts = Flag("Guilds:Contracts", d.GuildContracts),
            GuildContractCount = Int("Guilds:ContractCount", 3, 1, 4),
            GuildContractPeriod = Take("Guilds:ContractPeriod") is not { } period ? GuildContractPeriod.Weekly
                : Enum.TryParse<GuildContractPeriod>(period, ignoreCase: true, out var gcp) && Enum.IsDefined(gcp) ? gcp
                : throw new InvalidDataException($"Guilds:ContractPeriod: '{period}' is not Weekly or Daily"),
            GuildContractXp = (long)Number("Guilds:ContractXp", 20_000, 0, 1e9),
            GuildContractSilver = (long)Number("Guilds:ContractSilver", 100_000, 0, 1e9),
            IslandsEnabled = Flag("Islands:Enabled", true),
            IslandsStarterIsland = Flag("Islands:StarterIsland", d.IslandsStarterIsland),
            IslandsStarterLevel = Int("Islands:StarterLevel", 0, 0, 5),
            IslandsPremiumDays = Int("Islands:PremiumDays", 31, 0, 10_000),
            IslandsFreeIslands = Flag("Islands:FreeIslands", false),
            IslandsGrowthSpeed = Number("Islands:GrowthSpeed", d.IslandsGrowthSpeed, 0.01, 10_000),
            IslandsFocusSeedReturn = Flag("Islands:FocusSeedReturn", true),
            IslandsWaterFocusFactor = Number("Islands:WaterFocusFactor", d.IslandsWaterFocusFactor, 0, 100),
            IslandsStarterBuildings = Flag("Islands:StarterBuildings", d.IslandsStarterBuildings),
            PremiumMode = Take("Premium:Mode") is not { } premium ? d.PremiumMode
                : Enum.TryParse<PremiumMode>(premium, ignoreCase: true, out var pmode) && Enum.IsDefined(pmode) ? pmode
                : throw new InvalidDataException($"Premium:Mode: '{premium}' is not None or Free"),
            PremiumFameFactor = Data("Premium:FameFactor", 0, 100),
            PremiumSilverFactor = Data("Premium:SilverFactor", 0, 100),
            PremiumGatherBonusChance = Data("Premium:GatherBonusChance", 0, 100),
            PremiumCropYieldFactor = Data("Premium:CropYieldFactor", 0, 100),
            FocusMax = Data("Focus:Max", 0, 10_000_000),
            FocusRegenPerDay = Data("Focus:RegenPerDay", 0, 100_000_000),
            FocusStart = Number("Focus:Start", d.FocusStart, 0, 10_000_000),
            FocusCrafting = Flag("Focus:Crafting", true),
            LearningPointsSecondsPerPoint = Data("LearningPoints:SecondsPerPoint", 0, 1e9),
            LearningPointsStart = Number("LearningPoints:Start", d.LearningPointsStart, 0, 1e8),
            LearningPointsMax = Data("LearningPoints:Max", 0, 1e8),
            ChatEnabled = Flag("Chat:Enabled", true),
            ChatWorldChannel = Flag("Chat:WorldChannel", d.ChatWorldChannel),
            ChatMergeLanguageChannels = Flag("Chat:MergeLanguageChannels", d.ChatMergeLanguageChannels),
            ChatAnnouncements = Flag("Chat:Announcements", true),
            ChatGlobalCooldownMs = Int("Chat:GlobalCooldownMs", 2000, 0, 3_600_000),
            ChatWhisperCooldownMs = Int("Chat:WhisperCooldownMs", 100, 0, 3_600_000),
            ChatMaxLineLength = Int("Chat:MaxLineLength", 1000, 1, 10_000),
            ChatStaffTags = Flag("Chat:StaffTags", true),
            SocialFriendLimit = Int("Social:FriendLimit", 200, 0, 10_000),
            SocialFriendRequestLimit = Int("Social:FriendRequestLimit", 50, 0, 10_000),
            SocialMuteLimit = Int("Social:MuteLimit", 100, 0, 10_000),
            MailEnabled = Flag("Mail:Enabled", true),
            MailPostage = (long)Number("Mail:Postage", 50, 0, 1e9),
            MailDailyLimit = Int("Mail:DailyLimit", 30, 0, 100_000),
            MailMarketExpiryDays = Int("Mail:MarketExpiryDays", 30, 0, 10_000),
            MailKeepDays = Int("Mail:KeepDays", 0, 0, 10_000),
            MailMaxPerBox = Int("Mail:MaxPerBox", 500, 1, 100_000),
            MailMarketNotices = Flag("Mail:MarketNotices", true),
            MailRewardOverflow = Flag("Mail:RewardOverflow", d.MailRewardOverflow),
            MailSubjectMax = Int("Mail:SubjectMax", 50, 1, 1000),
            MailTextMax = Int("Mail:TextMax", 1000, 1, 100_000),
            TradeEnabled = Flag("Trade:Enabled", true),
            TradeInviteRange = Number("Trade:InviteRange", 7, 0, 1000),
            TradeKeepRange = Number("Trade:KeepRange", 9, 0, 1000),
            TradeInviteSeconds = Number("Trade:InviteSeconds", 15, 1, 3600),
            TradeAcceptDelaySeconds = Number("Trade:AcceptDelaySeconds", 1.5, 0, 60),
            TradeSlots = Int("Trade:Slots", 11, 1, 11),
            TradeClusterRules = Flag("Trade:ClusterRules", true),
            TradeAllowGold = Flag("Trade:AllowGold", false),
            TradeAllowInCombat = Flag("Trade:AllowInCombat", false),
            PvpRespawnCluster = (Take("Pvp:RespawnCluster") ?? "").Trim(),
            PvpDeathSilverDrop = Take("Pvp:DeathSilverDrop") is not { } silverDrop ? false
                : bool.TryParse(silverDrop, out var sd) ? sd : throw new InvalidDataException($"Pvp:DeathSilverDrop: '{silverDrop}' is not true or false"),
            PartyResendAfterClusterChange = Take("Party:ResendAfterClusterChange") is not { } resend ? false
                : bool.TryParse(resend, out var rs) ? rs : throw new InvalidDataException($"Party:ResendAfterClusterChange: '{resend}' is not true or false"),
            Visibility = new VisibilityOptions
            {
                CellSize = Int("Visibility:CellSize", 10, 1, 1000),
                EnterCells = Int("Visibility:EnterCells", 4, 0, 100),
                LeaveCells = Int("Visibility:LeaveCells", 6, 1, 101),
            },
            Bots = LoadBots(values, d.Bots),
            Corrupted = LoadCorrupted(values, d.Corrupted),
            Hellgates = LoadHellgates(values, d.Hellgates),
            Arena = ArenaOptions.Load(values, d.Arena),
            Vanity = VanityOptions.Load(values, d.Vanity),
            Roads = LoadRoads(values, d.Roads),
            Mists = LoadMists(values, d.Mists),
            Companions = LoadCompanions(values, d.Companions),
            Plugins = PluginOptions.Load(values, d.Plugins),
            MapIdleUnload = Take("World:MapIdleUnloadMinutes") is not { } idle ? TimeSpan.FromMinutes(5)
                : double.TryParse(idle, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var min) && min is >= 0 and <= 1440
                    ? TimeSpan.FromMinutes(min) : throw new InvalidDataException($"World:MapIdleUnloadMinutes: '{idle}' is not 0..1440"),
            Transfer = new TransferOptions
            {
                PositionTolerance = Take("Transfer:PositionTolerance") is not { } tolerance ? 2f
                    : float.TryParse(tolerance, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tol) && tol is >= 0 and <= 100
                        ? tol : throw new InvalidDataException($"Transfer:PositionTolerance: '{tolerance}' is not 0..100"),
                CheckExitCooldown = Take("Transfer:CheckExitCooldown") is not { } cooldown ? true
                    : bool.TryParse(cooldown, out var check) ? check : throw new InvalidDataException($"Transfer:CheckExitCooldown: '{cooldown}' is not true or false"),
            },
        };
        if (config.Visibility.LeaveCells <= config.Visibility.EnterCells)
            throw new InvalidDataException($"Visibility:LeaveCells ({config.Visibility.LeaveCells}) must be more than Visibility:EnterCells ({config.Visibility.EnterCells})");
        if (values.Count > 0) throw new InvalidDataException($"unknown config key(s): {string.Join(", ", values.Keys)}");
        // Two listeners cannot share a port (0 = each its own free one); the old README example moved the game to 5057.
        var ports = new (string Key, int Port)[] { ("Network:LoginPort", config.LoginEndPoint.Port), ("Network:GamePort", config.GameEndPoint.Port), ("Network:ChatPort", config.ChatEndPoint.Port) };
        if (ports.Where(p => p.Port != 0 && (p.Key != "Network:ChatPort" || config.ChatEnabled)).GroupBy(p => p.Port).FirstOrDefault(g => g.Count() > 1) is { } clash)
            throw new InvalidDataException($"{string.Join(" and ", clash.Select(p => p.Key))} are both {clash.Key}: give each listener its own port");
        if (config.Web is { Enabled: true, Port: > 0 } w && ports.FirstOrDefault(p => p.Port == w.Port && (p.Key != "Network:ChatPort" || config.ChatEnabled)) is { Key: { } taken })
            throw new InvalidDataException($"Web:Port and {taken} are both {w.Port}: give the web panel its own port");
        return config;
    }

    /// <summary>
    /// The Roads section out of <paramref name="values"/> over the preset's <paramref name="d"/> (stageL roads SPEC §5.8): every key optional. "data"
    /// (or a missing key) keeps the client data's value for Roads:LifetimeMinutes, Roads:Charges and Roads:EnterRadius.
    /// </summary>
    private static RoadsOptions LoadRoads(Dictionary<string, string> values, RoadsOptions d)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        static bool Data(string v) => v.Trim().Equals("data", StringComparison.OrdinalIgnoreCase);
        static double? Parse(string text) =>
            double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : Parse(v) is { } n && n >= min && n <= max ? n : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        // "data", minutes, "min-max" or a JSON array [min, max].
        (double Min, double Max)? Lifetime(string key, (double Min, double Max)? fallback)
        {
            if (Take(key) is not { } v) return fallback;
            if (Data(v)) return null;
            var parts = v.Trim().Trim('[', ']').Split([',', '-'], 2, StringSplitOptions.TrimEntries).Select(Parse).ToList();
            if (parts.All(n => n is > 0 and <= 100_000)) return (parts.Min()!.Value, parts.Max()!.Value);
            throw new InvalidDataException($"{key}: '{v}' is not \"data\", minutes, min-max or [min, max] within 0..100000");
        }
        // Roads:Charges "data", or Roads:Charges:<SOLO|GROUP|RAID>:<MaxCharges|ChargeSeconds|UsedSeconds> over the data's values of that type.
        const string charges = "Roads:Charges:";
        if (Take("Roads:Charges") is { } all && !Data(all)) throw new InvalidDataException($"Roads:Charges: '{all}' is not \"data\" or an object by exit type");
        var chargeKeys = values.Keys.Where(k => k.StartsWith(charges, StringComparison.OrdinalIgnoreCase)).ToList();
        var byType = new Dictionary<string, RoadChargeOverride>(d.Charges, StringComparer.OrdinalIgnoreCase);
        foreach (var key in chargeKeys)
        {
            var parts = key[charges.Length..].Split(':');
            var type = parts[0].ToUpperInvariant();
            if (parts.Length != 2 || type is not ("SOLO" or "GROUP" or "RAID"))
                throw new InvalidDataException($"{key}: not Roads:Charges:<SOLO|GROUP|RAID>:<MaxCharges|ChargeSeconds|UsedSeconds>");
            var o = byType.GetValueOrDefault(type) ?? new RoadChargeOverride();
            byType[type] = parts[1].ToLowerInvariant() switch
            {
                "maxcharges" => o with { MaxCharges = (int)Number(key, 0, 1, 1000) },
                "chargeseconds" => o with { ChargeSeconds = Number(key, 0, 1, 1_000_000) },
                "usedseconds" => o with { UsedSeconds = Number(key, 0, 0, 1_000_000) },
                _ => throw new InvalidDataException($"{key}: not Roads:Charges:<type>:<MaxCharges|ChargeSeconds|UsedSeconds>"),
            };
        }
        var layers = d.Layers;
        if (Take("Roads:Layers") is { } l)
        {
            string[]? names = null;
            try { names = l.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(l) : l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
            catch (JsonException) { }
            if (names is null || names.FirstOrDefault(n => !n.Equals("all", StringComparison.OrdinalIgnoreCase) && !RoadsOptions.LayerNames.ContainsKey(n)
                    && !RoadsOptions.LayerNames.Values.Any(p => p.Contains(n, StringComparer.OrdinalIgnoreCase))) is { } typo) // a typo would leave the network empty
                throw new InvalidDataException($"Roads:Layers: '{l}' is not \"all\" or a list of layers ({string.Join(", ", RoadsOptions.LayerNames.Keys)}) or tunnel presets");
            layers = names.Any(n => n.Equals("all", StringComparison.OrdinalIgnoreCase)) ? [] : [.. names.Select(n => n.ToUpperInvariant()).Distinct()];
        }
        return new RoadsOptions
        {
            Enabled = Flag("Roads:Enabled", d.Enabled),
            Layers = layers,
            LifetimeMinutes = Lifetime("Roads:LifetimeMinutes", d.LifetimeMinutes),
            Charges = chargeKeys.Count > 0 ? byType : d.Charges, // the same instance without keys: a default config equals a loaded one
            EnterRadius = Take("Roads:EnterRadius") is not { } radius ? d.EnterRadius : Data(radius) ? null
                : Parse(radius) is { } r && r is > 0 and <= 100 ? r : throw new InvalidDataException($"Roads:EnterRadius: '{radius}' is not \"data\" or 0..100"),
            Rescue = Flag("Roads:Rescue", d.Rescue),
            RescueMinutes = Number("Roads:RescueMinutes", d.RescueMinutes, 1, 100_000),
            UpdateSeconds = Number("Roads:UpdateSeconds", d.UpdateSeconds, 0.05, 3600),
            RetrySeconds = Number("Roads:RetrySeconds", d.RetrySeconds, 1, 86400),
            Seed = Take("Roads:Seed") is not { } seed ? d.Seed : seed.Trim().Equals("null", StringComparison.OrdinalIgnoreCase) ? null
                : int.TryParse(seed, System.Globalization.NumberStyles.Integer, inv, out var sd) ? sd : throw new InvalidDataException($"Roads:Seed: '{seed}' is not a number or null"),
            Command = Take("Roads:Command") is not { } cmd ? d.Command
                : Enum.TryParse<RoadsCommandAccess>(cmd, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c
                : throw new InvalidDataException($"Roads:Command: '{cmd}' is not one of {string.Join(", ", Enum.GetNames<RoadsCommandAccess>())}"),
            Chests = Flag("Roads:Chests", d.Chests),
            FixedDungeons = Flag("Roads:FixedDungeons", d.FixedDungeons),
            LogoutSeconds = Take("Roads:LogoutSeconds") is not { } logout ? d.LogoutSeconds
                : logout.Trim().Equals("world", StringComparison.OrdinalIgnoreCase) ? null
                : Parse(logout) is { } ls && ls is >= 0 and <= 3600 ? ls : throw new InvalidDataException($"Roads:LogoutSeconds: '{logout}' is not \"world\" or 0..3600"),
            ThrowOutAfterMinutes = Number("Roads:ThrowOutAfterMinutes", d.ThrowOutAfterMinutes, 0, 100_000),
            TempPortalSeconds = Number("Roads:TempPortalSeconds", d.TempPortalSeconds, 0, 600),
        };
    }

    /// <summary>The Mists section out of <paramref name="values"/> over the preset's <paramref name="d"/> (stageL mists SPEC §6.10): every key optional.</summary>
    private static CompanionOptions LoadCompanions(Dictionary<string, string> values, CompanionOptions d)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        return new CompanionOptions
        {
            Enabled = Flag("Companions:Enabled", d.Enabled),
            MaxPerPlayer = (int)Number("Companions:MaxPerPlayer", d.MaxPerPlayer, 0, 4),
            MaxPartySize = (int)Number("Companions:MaxPartySize", d.MaxPartySize, 2, 20),
            MinTier = (int)Number("Companions:MinTier", d.MinTier, 1, 8),
            MaxTier = (int)Number("Companions:MaxTier", d.MaxTier, 1, 8),
            HealthFactor = Number("Companions:HealthFactor", d.HealthFactor, 0.1, 10),
            DamageFactor = Number("Companions:DamageFactor", d.DamageFactor, 0, 10),
            TankThreatFactor = Number("Companions:TankThreatFactor", d.TankThreatFactor, 1, 20),
            HealBelow = Number("Companions:HealBelow", d.HealBelow, 0.1, 1),
            FollowDistance = Number("Companions:FollowDistance", d.FollowDistance, 1, 30),
            AssistRange = Number("Companions:AssistRange", d.AssistRange, 5, 80),
            LeashDistance = Number("Companions:LeashDistance", d.LeashDistance, 10, 200),
            TeleportDistance = Number("Companions:TeleportDistance", d.TeleportDistance, 10, 500),
            StuckSeconds = Number("Companions:StuckSeconds", d.StuckSeconds, 1, 120),
            OwnerGraceSeconds = Number("Companions:OwnerGraceSeconds", d.OwnerGraceSeconds, 5, 3600),
            SpellChance = Number("Companions:SpellChance", d.SpellChance, 0, 1),
            GearMastery = Number("Companions:GearMastery", d.GearMastery, 0, 1),
            GearDamageBonus = Flag("Companions:GearDamageBonus", d.GearDamageBonus),
        };
    }

    private static MistsOptions LoadMists(Dictionary<string, string> values, MistsOptions d)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        TEnum Choice<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum => Take(key) is not { } v ? fallback
            : Enum.TryParse<TEnum>(v, ignoreCase: true, out var e) && Enum.IsDefined(e) ? e
            : throw new InvalidDataException($"{key}: '{v}' is not one of {string.Join(", ", Enum.GetNames<TEnum>())}");
        string Cluster(string key, string fallback) => Take(key)?.Trim() is { Length: > 0 } c ? c : fallback;
        // Mists:RarityWeights: "data" (or []) = randomdungeons.xml's rerolls, else the five weights of rarity 0-4 (a JSON array or a comma list).
        IReadOnlyList<double> Weights(string key, IReadOnlyList<double> fallback)
        {
            if (Take(key) is not { } v) return fallback;
            if (v.Trim().Equals("data", StringComparison.OrdinalIgnoreCase)) return [];
            double[]? w = null;
            try
            {
                w = v.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<double[]>(v)
                    : [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => double.Parse(x, System.Globalization.NumberStyles.Float, inv))];
            }
            catch (Exception e) when (e is JsonException or FormatException or OverflowException) { }
            if (w is not null && (w.Length == 0 || w.Length == 5 && w.All(x => x >= 0 && double.IsFinite(x)) && w.Sum() > 0)) return w;
            throw new InvalidDataException($"{key}: '{v}' is not \"data\" or five weights 0 or more of rarity 0-4");
        }
        return new MistsOptions
        {
            Enabled = Flag("Mists:Enabled", d.Enabled),
            Matchmaking = Choice("Mists:Matchmaking", d.Matchmaking),
            LifetimeFactor = Number("Mists:LifetimeFactor", d.LifetimeFactor, 0.001, 100),
            BlockFactor = Number("Mists:BlockFactor", d.BlockFactor, 0, 100),
            OpenWorldWeight = Number("Mists:OpenWorldWeight", d.OpenWorldWeight, 0, 1000),
            DuoSoloScaling = Number("Mists:DuoSoloScaling", d.DuoSoloScaling, 0.01, 10),
            OpenWorldExit = Choice("Mists:OpenWorldExit", d.OpenWorldExit),
            UnrestrictedZones = Flag("Mists:UnrestrictedZones", d.UnrestrictedZones),
            BossChest = Flag("Mists:BossChest", d.BossChest),
            BossChestShare = Number("Mists:BossChestShare", d.BossChestShare, 0, 1),
            EmptySeconds = Number("Mists:EmptySeconds", d.EmptySeconds, 1, 86400),
            FallbackCluster = Cluster("Mists:FallbackCluster", d.FallbackCluster),
            RespawnCluster = Cluster("Mists:RespawnCluster", d.RespawnCluster),
            RarityWeights = Weights("Mists:RarityWeights", d.RarityWeights),
            StandingFactor = Number("Mists:StandingFactor", d.StandingFactor, 0, 1000),
            StaticEntrance = Flag("Mists:StaticEntrance", d.StaticEntrance),
            DrifterNeedsRed = Flag("Mists:DrifterNeedsRed", d.DrifterNeedsRed),
        };
    }

    /// <summary>The Hellgates section out of <paramref name="values"/> over the preset's <paramref name="d"/> (stageL hellgates SPEC §5.8): every key optional.</summary>
    private static HellgateOptions LoadHellgates(Dictionary<string, string> values, HellgateOptions d)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        TEnum Choice<TEnum>(string key, TEnum fallback) where TEnum : struct, Enum => Take(key) is not { } v ? fallback
            : Enum.TryParse<TEnum>(v, ignoreCase: true, out var e) && Enum.IsDefined(e) ? e
            : throw new InvalidDataException($"{key}: '{v}' is not one of {string.Join(", ", Enum.GetNames<TEnum>())}");
        // A JSON array of mode names (the file) or a comma list (the command line).
        IReadOnlyList<string> Names(string key, IReadOnlyList<string> fallback)
        {
            if (Take(key) is not { } v) return fallback;
            string[]? names = null;
            try { names = v.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(v) : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); }
            catch (JsonException) { }
            if (names is null || names.Any(n => !HellgateOptions.AllModes.Contains(n, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{key}: '{v}' is not a list of {string.Join(", ", HellgateOptions.AllModes)}");
            return [.. names.Select(n => HellgateOptions.AllModes.First(m => m.Equals(n, StringComparison.OrdinalIgnoreCase))).Distinct()];
        }
        var defaultMode = Take("Hellgates:DefaultMode") is not { } dm ? d.DefaultMode
            : HellgateOptions.AllModes.FirstOrDefault(m => m.Equals(dm, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidDataException($"Hellgates:DefaultMode: '{dm}' is not one of {string.Join(", ", HellgateOptions.AllModes)}");
        return new HellgateOptions
        {
            Enabled = Flag("Hellgates:Enabled", d.Enabled),
            Modes = Names("Hellgates:Modes", d.Modes),
            VersusModes = Names("Hellgates:VersusModes", d.VersusModes),
            ShrineMode = Choice("Hellgates:ShrineMode", d.ShrineMode),
            DefaultMode = defaultMode,
            CheckItemPower = Flag("Hellgates:CheckItemPower", d.CheckItemPower),
            ChannelSeconds = Number("Hellgates:ChannelSeconds", d.ChannelSeconds, 0, 60),
            LockSeconds = Number("Hellgates:LockSeconds", d.LockSeconds, 0, 60),
            CancelDistance = Number("Hellgates:CancelDistance", d.CancelDistance, 0.1, 100),
            BossMapFactor = Number("Hellgates:BossMapFactor", d.BossMapFactor, 0, 100),
            GroupScaling = Flag("Hellgates:GroupScaling", d.GroupScaling),
            ScalingBase = Number("Hellgates:ScalingBase", d.ScalingBase, 0.01, 10),
            ScalingPerPlayer = Number("Hellgates:ScalingPerPlayer", d.ScalingPerPlayer, 0, 10),
            ScalingMax = Number("Hellgates:ScalingMax", d.ScalingMax, 0.01, 10),
            ScaleDamage = Flag("Hellgates:ScaleDamage", d.ScaleDamage),
            FamePerPoint = Number("Hellgates:FamePerPoint", d.FamePerPoint, 1, 1_000_000),
            EnemyTeamSize = Choice("Hellgates:EnemyTeamSize", d.EnemyTeamSize),
            MatchStartSpell = Flag("Hellgates:MatchStartSpell", d.MatchStartSpell),
            MobDespawnOnMatch = Flag("Hellgates:MobDespawnOnMatch", d.MobDespawnOnMatch),
            KnockdownSeconds = Take("Hellgates:KnockdownSeconds") is not { } kd ? d.KnockdownSeconds
                : kd.Equals("archetype", StringComparison.OrdinalIgnoreCase) || kd.Equals("null", StringComparison.OrdinalIgnoreCase) ? null
                : double.TryParse(kd, System.Globalization.NumberStyles.Float, inv, out var k) && k is >= 1 and <= 3600 ? k
                : throw new InvalidDataException($"Hellgates:KnockdownSeconds: '{kd}' is not 1..3600 or archetype"),
            WipeEndsRun = Flag("Hellgates:WipeEndsRun", d.WipeEndsRun),
            ClaimSeconds = Number("Hellgates:ClaimSeconds", d.ClaimSeconds, 1, 86400),
            LeaveCommand = Flag("Hellgates:LeaveCommand", d.LeaveCommand),
            StartClearance = Number("Hellgates:StartClearance", d.StartClearance, 0, 500),
            MaxMinutes = Number("Hellgates:MaxMinutes", d.MaxMinutes, 1, 1440),
            FinishedLingerSeconds = Number("Hellgates:FinishedLingerSeconds", d.FinishedLingerSeconds, 1, 86400),
            EmptyCloseSeconds = Number("Hellgates:EmptyCloseSeconds", d.EmptyCloseSeconds, 1, 86400),
            ArrivalGraceSeconds = Number("Hellgates:ArrivalGraceSeconds", d.ArrivalGraceSeconds, 1, 3600),
            FallbackCluster = Take("Hellgates:FallbackCluster")?.Trim() is { Length: > 0 } fb ? fb : d.FallbackCluster,
            Chain = Flag("Hellgates:Chain", d.Chain),
            RestrictedArea = Flag("Hellgates:RestrictedArea", d.RestrictedArea),
            SpawnBuffs = Flag("Hellgates:SpawnBuffs", d.SpawnBuffs),
            InfamyWin = Number("Hellgates:InfamyWin", d.InfamyWin, 0, 1_000_000),
            Matchmaking = Choice("Hellgates:Matchmaking", d.Matchmaking),
            MatchSeconds = Number("Hellgates:MatchSeconds", d.MatchSeconds, 0, 3600),
        };
    }

    /// <summary>The Corrupted section out of <paramref name="values"/> over the preset's <paramref name="d"/> (stageL corrupted SPEC §4.7): every key optional.</summary>
    private static CorruptedOptions LoadCorrupted(Dictionary<string, string> values, CorruptedOptions d)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : double.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= min && n <= max ? n
            : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        return new CorruptedOptions
        {
            Enabled = Flag("Corrupted:Enabled", d.Enabled),
            ExitPortal = Take("Corrupted:ExitPortal") is not { } portal ? d.ExitPortal
                : Enum.TryParse<CorruptedExitMode>(portal, ignoreCase: true, out var ep) && Enum.IsDefined(ep) ? ep
                : throw new InvalidDataException($"Corrupted:ExitPortal: '{portal}' is not one of {string.Join(", ", Enum.GetNames<CorruptedExitMode>())}"),
            EnterBuff = Flag("Corrupted:EnterBuff", d.EnterBuff),
            ChestGuardRadius = Number("Corrupted:ChestGuardRadius", d.ChestGuardRadius, 0, 1000),
            ClearShare = Number("Corrupted:ClearShare", d.ClearShare, 0.01, 1),
            InfamyFactor = Number("Corrupted:InfamyFactor", d.InfamyFactor, 0, 100),
            EarlyLeaveShare = Number("Corrupted:EarlyLeaveShare", d.EarlyLeaveShare, 0, 1),
            RewardFactor = Number("Corrupted:RewardFactor", d.RewardFactor, 0, 100),
            EmptyCloseSeconds = Number("Corrupted:EmptyCloseSeconds", d.EmptyCloseSeconds, 1, 86400),
            LingerSeconds = Number("Corrupted:LingerSeconds", d.LingerSeconds, 1, 86400),
            MaxMinutes = Number("Corrupted:MaxMinutes", d.MaxMinutes, 1, 1440),
            RelogMinutes = Number("Corrupted:RelogMinutes", d.RelogMinutes, 0, 1440),
            CheckItemPower = Flag("Corrupted:CheckItemPower", d.CheckItemPower),
            CheckInfamy = Flag("Corrupted:CheckInfamy", d.CheckInfamy),
            Invasions = Mode("Corrupted:Invasions", d.Invasions),
            BotInvaders = Flag("Corrupted:BotInvaders", d.BotInvaders),
            PlayerInvasions = Mode("Corrupted:PlayerInvasions", d.PlayerInvasions),
            BotInvaderDelaySeconds = Range("Corrupted:BotInvaderDelaySeconds", d.BotInvaderDelaySeconds, 0, 86400),
            InvaderHealthFactor = Number("Corrupted:Invader:HealthFactor", d.InvaderHealthFactor, 0.01, 100),
            InvaderDamageFactor = Factors("Corrupted:Invader:DamageFactor", d.InvaderDamageFactor),
            InfamyLossOnDefeat = Number("Corrupted:InfamyLossOnDefeat", d.InfamyLossOnDefeat, 0, 1),
            MaxInvasionsPerRun = (int)Number("Corrupted:MaxInvasionsPerRun", d.MaxInvasionsPerRun, 0, 100),
            ShrineUseSeconds = Number("Corrupted:ShrineUseSeconds", d.ShrineUseSeconds, 0, 60),
            InvadeCountdownSeconds = Number("Corrupted:InvadeCountdownSeconds", d.InvadeCountdownSeconds, 0, 600),
            OpenWorldPortals = Flag("Corrupted:OpenWorldPortals", d.OpenWorldPortals),
            MapsOpenDungeons = Flag("Corrupted:MapsOpenDungeons", d.MapsOpenDungeons),
            AntiReset = Flag("Corrupted:AntiReset", d.AntiReset),
            AntiResetImps = (int)Number("Corrupted:AntiResetImps", d.AntiResetImps, 0, 20),
            Chain = Flag("Corrupted:Chain", d.Chain),
            ChainBonus = Number("Corrupted:ChainBonus", d.ChainBonus, 0, 10),
            ChainBonusMax = Number("Corrupted:ChainBonusMax", d.ChainBonusMax, 0, 100),
        };

        CorruptedInvasionMode Mode(string key, CorruptedInvasionMode fallback) => Take(key) is not { } v ? fallback
            : Enum.TryParse<CorruptedInvasionMode>(v, ignoreCase: true, out var m) && Enum.IsDefined(m) ? m
            : throw new InvalidDataException($"{key}: '{v}' is not one of {string.Join(", ", Enum.GetNames<CorruptedInvasionMode>())}");
        (double Min, double Max) Range(string key, (double Min, double Max) fallback, double min, double max)
        {
            if (Take(key) is not { } v) return fallback;
            var parts = v.Split('-', 2, StringSplitOptions.TrimEntries)
                .Select(p => double.TryParse(p, System.Globalization.NumberStyles.Float, inv, out var n) && double.IsFinite(n) ? n : (double?)null).ToList();
            if (parts.All(n => n is not null) && parts.Min() >= min && parts.Max() <= max) return (parts.Min()!.Value, parts.Max()!.Value);
            throw new InvalidDataException($"{key}: '{v}' is not a range min-max within {min.ToString(inv)}..{max.ToString(inv)}");
        }
        // Hunter, Stalker, Slayer: "0.8,1,1.2", or one number for all three.
        (double, double, double) Factors(string key, (double, double, double) fallback)
        {
            if (Take(key) is not { } v) return fallback;
            var parts = v.Split(',', StringSplitOptions.TrimEntries)
                .Select(p => double.TryParse(p, System.Globalization.NumberStyles.Float, inv, out var n) && double.IsFinite(n) && n >= 0 && n <= 100 ? n : (double?)null).ToList();
            if (parts.All(n => n is not null) && parts.Count is 1 or 3)
                return parts.Count == 1 ? (parts[0]!.Value, parts[0]!.Value, parts[0]!.Value) : (parts[0]!.Value, parts[1]!.Value, parts[2]!.Value);
            throw new InvalidDataException($"{key}: '{v}' is not one factor 0..100 or three (Hunter, Stalker, Slayer) separated by commas");
        }
    }

    /// <summary>
    /// The Bots section out of <paramref name="values"/> over the preset's <paramref name="d"/> (stage I bots SPEC §3.1): every key optional; a
    /// range is "min-max" or one number. Bots:MinSpawnCells defaults to Visibility:EnterCells + 1 (<see cref="BotOptions.SpawnCells"/>).
    /// </summary>
    private static BotOptions LoadBots(Dictionary<string, string> values, BotOptions d)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        static double? Parse(string text) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        double Number(string key, double fallback, double min, double max) => Take(key) is not { } v ? fallback
            : Parse(v) is { } n && n >= min && n <= max ? n : throw new InvalidDataException($"{key}: '{v}' is not {min.ToString(inv)}..{max.ToString(inv)}");
        int Int(string key, int fallback, int min, int max) => Take(key) is not { } v ? fallback
            : int.TryParse(v, System.Globalization.NumberStyles.Integer, inv, out var i) && i >= min && i <= max ? i : throw new InvalidDataException($"{key}: '{v}' is not {min}..{max}");
        (double Min, double Max) Range(string key, (double Min, double Max) fallback, double min, double max)
        {
            if (Take(key) is not { } v) return fallback;
            var parts = v.Split('-', 2, StringSplitOptions.TrimEntries).Select(Parse).ToList();
            if (parts.All(n => n is not null) && parts.Min() >= min && parts.Max() <= max) return (parts.Min()!.Value, parts.Max()!.Value);
            throw new InvalidDataException($"{key}: '{v}' is not a range min-max within {min.ToString(inv)}..{max.ToString(inv)}");
        }
        T Choice<T>(string key, T fallback) where T : struct, Enum => Take(key) is not { } v ? fallback
            : Enum.TryParse<T>(v, ignoreCase: true, out var e) && Enum.IsDefined(e) ? e
            : throw new InvalidDataException($"{key}: '{v}' is not one of {string.Join(", ", Enum.GetNames<T>())}");
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        const string zoneRules = "Bots:PvpRulesByZone:"; // Bots:PvpRulesByZone:<warning type> = a Bots:PvpRules value
        var byZone = values.Keys.Where(k => k.StartsWith(zoneRules, StringComparison.OrdinalIgnoreCase)).ToList()
            .ToDictionary(k => k[zoneRules.Length..], k => Choice(k, d.PvpRules), StringComparer.OrdinalIgnoreCase);
        return new BotOptions
        {
            Enabled = Flag("Bots:Enabled", d.Enabled),
            MaxTotal = Int("Bots:MaxTotal", d.MaxTotal, 0, 2000),
            MaxPerMap = Int("Bots:MaxPerMap", d.MaxPerMap, 0, 500),
            PopulateSeconds = Number("Bots:PopulateSeconds", d.PopulateSeconds, 0.05, 3600),
            SpawnPerPass = Int("Bots:SpawnPerPass", d.SpawnPerPass, 1, 100),
            MinSpawnCells = Take("Bots:MinSpawnCells") is { } cells ? int.TryParse(cells, out var c) && c is >= 0 and <= 100 ? c
                : throw new InvalidDataException($"Bots:MinSpawnCells: '{cells}' is not 0..100") : d.MinSpawnCells,
            LingerSeconds = Number("Bots:LingerSeconds", d.LingerSeconds, 0, 86400),
            RespawnSeconds = Range("Bots:RespawnSeconds", d.RespawnSeconds, 0, 86400),
            StayMinutes = Range("Bots:StayMinutes", d.StayMinutes, 0.001, 10080),
            ArrivalSeconds = Range("Bots:ArrivalSeconds", d.ArrivalSeconds, 0, 86400),
            ActiveRange = Number("Bots:ActiveRange", d.ActiveRange, 0, 10000),
            ThinkSeconds = Range("Bots:ThinkSeconds", d.ThinkSeconds, 0.05, 60),
            MoveResendSeconds = Number("Bots:MoveResendSeconds", d.MoveResendSeconds, 0.05, 10),
            PauseChance = Number("Bots:Move:PauseChance", d.PauseChance, 0, 1),
            Look = Choice("Bots:Look", d.Look),
            FemaleShare = Number("Bots:FemaleShare", d.FemaleShare, 0, 1),
            LatticeStep = Int("Bots:Nav:LatticeStep", d.LatticeStep, 1, 32),
            MaxExpansions = Int("Bots:Nav:MaxExpansions", d.MaxExpansions, 100, 10_000_000),
            SlowPathMs = Number("Bots:Nav:SlowPathMs", d.SlowPathMs, 0, 10000),
            CacheClusters = Int("Bots:Nav:CacheClusters", d.CacheClusters, 1, 1000),
            CityPerPoi = Int("Bots:City:PerPoi", d.CityPerPoi, 1, 100),
            CityEmoteChance = Number("Bots:City:EmoteChance", d.CityEmoteChance, 0, 1),
            PvpRules = Choice("Bots:PvpRules", d.PvpRules),
            PvpRulesByZone = byZone.Count > 0 ? byZone : d.PvpRulesByZone,
            MountDistance = Number("Bots:MountDistance", d.MountDistance, 0, 10000),
            CityGatherers = Int("Bots:City:Gatherers", d.CityGatherers, 0, 100),
            GathererFlag = Choice("Bots:Gatherer:Flag", d.GathererFlag),
            GathererRedShare = Number("Bots:Gatherer:RedShare", d.GathererRedShare, 0, 1),
            GathererRadius = Number("Bots:Gatherer:Radius", d.GathererRadius, 5, 10000),
            GathererNodes = Range("Bots:Gatherer:Nodes", d.GathererNodes, 1, 10000),
            AvoidPlayerRadius = Number("Bots:Gatherer:AvoidPlayerRadius", d.AvoidPlayerRadius, 0, 1000),
            AvoidMobRadius = Number("Bots:Gatherer:AvoidMobRadius", d.AvoidMobRadius, 0, 1000),
            FleeHealth = Number("Bots:Gatherer:FleeHealth", d.FleeHealth, 0, 1),
            FleeChance = Number("Bots:Gatherer:FleeChance", d.FleeChance, 0, 1),
            ConsumeNodes = Flag("Bots:ConsumeNodes", d.ConsumeNodes),
            EngageSeconds = Number("Bots:Pvp:EngageSeconds", d.EngageSeconds, 0.5, 3600),
            EngageParty = Flag("Bots:Pvp:EngageParty", d.EngageParty),
            CrimesAgainstBots = Flag("Bots:Pvp:CrimesAgainstBots", d.CrimesAgainstBots),
            CombatTickMs = Int("Bots:Combat:TickMs", d.CombatTickMs, 50, 5000),
            ReactionMs = Int("Bots:Combat:ReactionMs", d.ReactionMs, 0, 10000),
            Spells = Choice("Bots:Combat:Spells", d.Spells),
            SpellChance = Number("Bots:Combat:SpellChance", d.SpellChance, 0, 1),
            LeashRadius = Number("Bots:Patrol:LeashRadius", d.LeashRadius, 5, 1000),
            LethalToPlayers = Flag("Bots:Pvp:LethalToPlayers", d.LethalToPlayers),
            DamageFactor = Number("Bots:Combat:DamageFactor", d.DamageFactor, 0, 100),
            HealthFactor = Number("Bots:Combat:HealthFactor", d.HealthFactor, 0.01, 100),
            GearMastery = Number("Bots:Gear:Mastery", d.GearMastery, 0, 1),
            GearDamageBonus = Flag("Bots:Gear:DamageBonus", d.GearDamageBonus),
            AggroRadius = Number("Bots:Patrol:AggroRadius", d.AggroRadius, 0, 100),
            SpareMinutes = Number("Bots:Patrol:SpareMinutes", d.SpareMinutes, 0, 1440),
            JoinGraceSeconds = Number("Bots:Patrol:JoinGraceSeconds", d.JoinGraceSeconds, 0, 3600),
            AttackMounted = Flag("Bots:Patrol:AttackMounted", d.AttackMounted),
            GroupSize = Range("Bots:Patrol:GroupSize", d.GroupSize, 1, 10),
            RoadsPatrolGroupSize = Range("Bots:Roads:PatrolGroupSize", d.RoadsPatrolGroupSize, 1, 10),
            RoadsPortals = Flag("Bots:Roads:Portals", d.RoadsPortals),
            RoadsPortalWaitSeconds = Range("Bots:Roads:PortalWaitSeconds", d.RoadsPortalWaitSeconds, 0, 3600),
            RouteDwellSeconds = Range("Bots:Patrol:RouteDwellSeconds", d.RouteDwellSeconds, 0, 3600),
            RouteEndDwellSeconds = Range("Bots:Patrol:RouteEndDwellSeconds", d.RouteEndDwellSeconds, 0, 3600),
            FameFactor = Number("Bots:Rewards:FameFactor", d.FameFactor, 0, 100),
            GearDropChance = Number("Bots:Rewards:GearDropChance", d.GearDropChance, 0, 1),
            DeathGearDropChance = Number("Bots:Rewards:DeathGearDropChance", d.DeathGearDropChance, 0, 1),
            MinItems = Int("Bots:Rewards:MinItems", d.MinItems, 0, 6),
            ResourceDropShare = Number("Bots:Rewards:ResourceDropShare", d.ResourceDropShare, 0, 1),
            LootProtectionSeconds = Take("Bots:Rewards:LootProtectionSeconds") is { } protection
                ? protection.Equals("null", StringComparison.OrdinalIgnoreCase) ? null
                : Parse(protection) is { } ps && ps >= 0 && ps <= 86400 ? ps : throw new InvalidDataException($"Bots:Rewards:LootProtectionSeconds: '{protection}' is not 0..86400 or null")
                : d.LootProtectionSeconds,
            BagsPerHour = Int("Bots:Rewards:BagsPerHour", d.BagsPerHour, 0, 100000),
            SameBotMinutes = Number("Bots:Rewards:SameBotMinutes", d.SameBotMinutes, 0, 10080),
            DieInLethalZones = Flag("Bots:Defeat:DieInLethalZones", d.DieInLethalZones),
            // Below the shortest PvP get-up (90 s): a defeated bot is gone before it could stand up again.
            DownDespawnSeconds = Number("Bots:DownDespawnSeconds", d.DownDespawnSeconds, 0, 89),
            // StageN botpower (analysis/work/stageN/botpower/SPEC.md).
            Difficulty = Number("Bots:Difficulty", d.Difficulty, 0.1, 10),
            RedZoneTierBonus = Int("Bots:Gear:RedZoneTierBonus", d.RedZoneTierBonus, 0, 3),
            Quality = Range("Bots:Gear:Quality", d.Quality, 1, 5),
            EliteChance = Number("Bots:Gear:EliteChance", d.EliteChance, 0, 1),
            EliteEnchant = Range("Bots:Gear:EliteEnchant", d.EliteEnchant, 0, 4),
            EliteQuality = Range("Bots:Gear:EliteQuality", d.EliteQuality, 1, 5),
            Tactics = Flag("Bots:Combat:Tactics", d.Tactics),
            KiteDistance = Number("Bots:Combat:KiteDistance", d.KiteDistance, 0, 30),
            RetreatHealth = Number("Bots:Combat:RetreatHealth", d.RetreatHealth, 0, 1),
            Potions = Range("Bots:Combat:Potions", d.Potions, 0, 10),
            Food = Flag("Bots:Combat:Food", d.Food),
            FocusWeakest = Flag("Bots:Combat:FocusWeakest", d.FocusWeakest),
            RedGroupSize = Range("Bots:Patrol:RedGroupSize", d.RedGroupSize, 1, 10),
            EliteSilverFactor = Number("Bots:Rewards:EliteSilverFactor", d.EliteSilverFactor, 0, 100),
            EliteExtraItems = Int("Bots:Rewards:EliteExtraItems", d.EliteExtraItems, 0, 6),
        };
    }

    /// <summary>
    /// The Rates section out of <paramref name="values"/> over the preset's <paramref name="baseRates"/> (each 0..<see cref="Game.Rates.Max"/>, Respawn and Regrowth from
    /// <see cref="Game.Rates.MinSpeed"/>), with the old World:MobRespawnScale (a time factor 0..100) as Rates:Respawn = 1 / it.
    /// </summary>
    private static Game.Rates LoadRates(Dictionary<string, string> values, Game.Rates baseRates)
    {
        var rates = baseRates;
        var respawnGiven = false;
        foreach (var name in Game.Rates.Names)
        {
            if (!values.Remove($"Rates:{name}", out var v)) continue;
            respawnGiven |= name == "Respawn";
            rates = double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? Rate(rates, name, d, $"Rates:{name}") : throw new InvalidDataException($"Rates:{name}: '{v}' is not a number");
        }
        if (values.Remove("World:MobRespawnScale", out var scale))
        {
            if (respawnGiven)
                throw new InvalidDataException("World:MobRespawnScale is the old name of Rates:Respawn (= 1 / it): give only Rates:Respawn");
            rates = double.TryParse(scale, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) && sc is >= 0 and <= 100
                ? rates with { Respawn = sc <= 1 / Game.Rates.Max ? Game.Rates.Max : 1 / sc }
                : throw new InvalidDataException($"World:MobRespawnScale: '{scale}' is not 0..100");
        }
        return rates;

        static Game.Rates Rate(Game.Rates rates, string name, double value, string key)
        {
            try { return rates.With(name, value); }
            catch (ArgumentException e) { throw new InvalidDataException($"{key}: {e.Message}"); }
        }
    }

    /// <summary>Guilds:Levels: a JSON array or a comma list of rising experience thresholds, the first 0 (1-20 levels).</summary>
    private static long[] GuildLevelList(string value)
    {
        long[]? levels = null;
        try
        {
            levels = value.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<long[]>(value)
                : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(v => long.Parse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture))];
        }
        catch (Exception e) when (e is JsonException or FormatException or OverflowException) { }
        if (levels is not { Length: >= 1 and <= 20 } || levels[0] != 0 || levels.Zip(levels.Skip(1)).Any(x => x.Second <= x.First))
            throw new InvalidDataException($"Guilds:Levels: '{value}' is not a list of 1-20 rising thresholds starting at 0");
        return levels;
    }

    /// <summary>World:EventLayers: a JSON array of strings (the file) or a comma list (the command line).</summary>
    /// <summary>Mods:Packs: pack folder names (a JSON array or a comma list), no paths.</summary>
    private static string[] PackList(string value)
    {
        string[] packs;
        try
        {
            packs = value.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(value) ?? []
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"Mods:Packs: '{value}' is not a list of pack names");
        }
        foreach (var pack in packs)
            if (pack.Length == 0 || pack.IndexOfAny(['/', '\\', ':']) >= 0 || pack is "." or "..")
                throw new InvalidDataException($"Mods:Packs: '{pack}' is not a pack folder name");
        return packs;
    }

    private static string[] Flags(string value)
    {
        string[] flags;
        try
        {
            flags = value.TrimStart().StartsWith('[') ? JsonSerializer.Deserialize<string[]>(value) ?? []
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (JsonException)
        {
            throw new InvalidDataException($"World:EventLayers: '{value}' is not a list of flags");
        }
        foreach (var flag in flags)
            if (!KnownEventLayers.Contains(flag))
                throw new InvalidDataException($"World:EventLayers: '{flag}' is not one of {string.Join(", ", KnownEventLayers)}");
        return flags;
    }
}

/// <summary>Premium:Mode (<see cref="ServerConfig.PremiumMode"/>): who is premium.</summary>
public enum PremiumMode
{
    /// <summary>Nobody is premium (the official preset: this server has no premium shop).</summary>
    None,

    /// <summary>Every character is premium, always, at no cost (the pve preset; focus-lp SPEC D1).</summary>
    Free,
}

/// <summary>The rule set the config's keys default to (Preset, <see cref="ServerConfig.Preset"/>).</summary>
public enum ServerPreset
{
    /// <summary>The PvE server of PVE-DESIGN.md §1 (<see cref="ServerConfig.PveDefaults"/>); Load's default.</summary>
    Pve,

    /// <summary>The official rules: every key at the official value (the record's defaults).</summary>
    Official,
}

/// <summary>Pvp:FriendlySpells (<see cref="ServerConfig.PvpFriendlySpells"/>): which other players a player's friendly spells reach.</summary>
public enum FriendlySpells
{
    /// <summary>Every Friend (stageJ pvp-spells SPEC §2.3).</summary>
    All,

    /// <summary>The party and guild mates among the Friends.</summary>
    Group,

    /// <summary>Nobody but the caster.</summary>
    Self,
}

/// <summary>Pvp:Mode (<see cref="ServerConfig.PvpMode"/>): who may attack whom.</summary>
public enum PvpMode
{
    /// <summary>The client's table by the zone's archetype (stageE pvp SPEC §1.2).</summary>
    Official,

    /// <summary>Only two Red players (outside one party) and duel opponents are Hostile; a black zone counts as a red one.</summary>
    Consensual,

    /// <summary>Only duel opponents are Hostile.</summary>
    Off,
}

/// <summary>Guilds:ContractPeriod (<see cref="ServerConfig.GuildContractPeriod"/>): how often the guild contracts change.</summary>
public enum GuildContractPeriod
{
    /// <summary>Every Monday at the bounties' reset time (10:00 UTC).</summary>
    Weekly,

    /// <summary>Every day at the bounties' reset time.</summary>
    Daily,
}

/// <summary>The destiny board of a new character (Progression:NewCharacters, <see cref="ServerConfig.NewCharacters"/>).</summary>
public enum NewCharacterProgression
{
    /// <summary>No board of its own: everything unlocked (Progression.UnlockAll), fame only raises the total.</summary>
    UnlockAll,

    /// <summary>An empty board that fame fills (DestinyBoard.FairStart) and the content/starter-fair.json kit.</summary>
    FairStart,
}

/// <summary>
/// The visibility window (SPEC stage 2 §5): cells of <see cref="CellSize"/> on the cluster origin; an object is sent when
/// its cell is at most <see cref="EnterCells"/> cells (Chebyshev) from the player's, a 9×9 window, and left or dropped at
/// <see cref="LeaveCells"/> or more. Official Leaves come at 5 (about 0.2 s after the crossing) or 6 (about 1.3 s), so a
/// player stepping back and forth over a cell border gets no Leave there; LeaveCells 6 is that 1-cell hysteresis in space
/// (an object known at distance 5 stays, one not yet known comes at 4), LeaveCells 5 leaves on the crossing itself.
/// </summary>
public sealed record VisibilityOptions
{
    /// <summary>Cell size in world units (Visibility:CellSize, 10).</summary>
    public int CellSize { get; init; } = 10;

    /// <summary>New* at a cell distance of at most this (Visibility:EnterCells, 4).</summary>
    public int EnterCells { get; init; } = 4;

    /// <summary>Leave (or a silent drop) at a cell distance of at least this (Visibility:LeaveCells, 6: one cell of hysteresis).</summary>
    public int LeaveCells { get; init; } = 6;
}

/// <summary>Corrupted:ExitPortal (stageL corrupted SPEC §4.2 step 8): when the server's exit portal stands in a corrupted dungeon's start room.</summary>
public enum CorruptedExitMode
{
    /// <summary>Never (the escape spell and .cd leave only).</summary>
    Off,

    /// <summary>From the start of the run (the pve preset: leaving early just forfeits the boss chest).</summary>
    Always,

    /// <summary>Once the demon is slain (the official preset).</summary>
    AfterDemon,
}

/// <summary>
/// StageL: the corrupted dungeons' switches (Corrupted:*, analysis/work/stageL/corrupted/SPEC.md §4.7); the tables are content/corrupted.json
/// (<see cref="Content.CorruptedSettings"/>). The record's values are the official preset's; the pve preset changes ExitPortal (Always),
/// InfamyFactor (2), EarlyLeaveShare (0.5) and RewardFactor (1.5), and of phase B Invasions and PlayerInvasions (Consent), BotInvaders (true),
/// InfamyLossOnDefeat (0), MaxInvasionsPerRun (1), AntiReset (false) and ChainBonus (0.1) (<see cref="ServerConfig.PveDefaults"/>).
/// </summary>
public sealed record CorruptedOptions
{
    /// <summary>Corrupted:Enabled (true): the Hell Den entrances, the enter cast and the instances.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Corrupted:ExitPortal (AfterDemon; pve Always): when the start room's exit portal stands (Off = never).</summary>
    public CorruptedExitMode ExitPortal { get; init; } = CorruptedExitMode.AfterDemon;

    /// <summary>Corrupted:EnterBuff (true): CORRUPTED_DUNGEON_ENTER (healing −40 %) on arrival, the cluster type's spawnbuff (the server has no spawnbuffs of its own).</summary>
    public bool EnterBuff { get; init; } = true;

    /// <summary>Corrupted:ChestGuardRadius (20 m, the chests' alertradius): a standard chest stays locked while a counted mob lives this near.</summary>
    public double ChestGuardRadius { get; init; } = 20;

    /// <summary>Corrupted:ClearShare (0.6): the share of the map's mob fame whose kills bring the points the demon needs.</summary>
    public double ClearShare { get; init; } = 0.6;

    /// <summary>Corrupted:InfamyFactor (1; pve 2): the factor on the infamy a run credits.</summary>
    public double InfamyFactor { get; init; } = 1;

    /// <summary>Corrupted:EarlyLeaveShare (0; pve 0.5): the share of its points × InfamyFactor a run left before the demon credits.</summary>
    public double EarlyLeaveShare { get; init; }

    /// <summary>Corrupted:RewardFactor (1; pve 1.5): the factor on the completion's silver and fame (content/corrupted.json Completion).</summary>
    public double RewardFactor { get; init; } = 1;

    /// <summary>Corrupted:EmptyCloseSeconds (30, randomdungeons SOLO lifetimeafteremptyseconds): an instance its owner left closes this long after.</summary>
    public double EmptyCloseSeconds { get; init; } = 30;

    /// <summary>Corrupted:LingerSeconds (300, exitdungeontime): a completed instance closes this long after the demon's death.</summary>
    public double LingerSeconds { get; init; } = 300;

    /// <summary>Corrupted:MaxMinutes (120): the hard limit of a run.</summary>
    public double MaxMinutes { get; init; } = 120;

    /// <summary>Corrupted:RelogMinutes (10): an owner who logged out inside keeps the run this long.</summary>
    public double RelogMinutes { get; init; } = 10;

    /// <summary>
    /// Corrupted:CheckItemPower (false): the server's copy of the client's item power gate. Off as Expeditions:CheckItemPower: the client
    /// checks its own figure (quality and mastery included) before the cast, the server's plain average of the gear is lower and would refuse
    /// a player the client shows as eligible.
    /// </summary>
    public bool CheckItemPower { get; init; }

    /// <summary>Corrupted:CheckInfamy (true): the server's copy of the client's infamy gate (Slayer: peak infamy ≥ 100 000).</summary>
    public bool CheckInfamy { get; init; } = true;

    // ---- phase B (SPEC §4.4-§4.8, §6) ----

    /// <summary>
    /// Corrupted:Invasions (Official; pve Consent): Off = the Demonic Shrine stays inactive and nobody invades; Consent = only an owner who used
    /// the shrine of its run is invaded (the bot invader after Corrupted:BotInvaderDelaySeconds, a consenting player by the rolls); Official = the
    /// data's rolls every rollinterval with chanceperrollpve (no shrine) or chanceperrollpvp (shrine).
    /// </summary>
    public CorruptedInvasionMode Invasions { get; init; } = CorruptedInvasionMode.Official;

    /// <summary>Corrupted:BotInvaders (false; pve true): the invader is a player bot (BotRole.Invader) when no player is matched.</summary>
    public bool BotInvaders { get; init; }

    /// <summary>Corrupted:PlayerInvasions (Official; pve Consent): real players invade (Off = never; Consent = only when both used a shrine).</summary>
    public CorruptedInvasionMode PlayerInvasions { get; init; } = CorruptedInvasionMode.Official;

    /// <summary>Corrupted:BotInvaderDelaySeconds (60-180): the bot invader comes this long after the shrine use (content/corrupted.json Rolls Fixed).</summary>
    public (double Min, double Max) BotInvaderDelaySeconds { get; init; } = (60, 180);

    /// <summary>Corrupted:Invader:HealthFactor (1.0): × the bot invader's maximum health (over Bots:Combat:HealthFactor).</summary>
    public double InvaderHealthFactor { get; init; } = 1.0;

    /// <summary>Corrupted:Invader:DamageFactor (0.8, 1.0, 1.2 for Hunter, Stalker, Slayer): × the bot invader's hits (over Bots:Combat:DamageFactor).</summary>
    public (double Hunter, double Stalker, double Slayer) InvaderDamageFactor { get; init; } = (0.8, 1.0, 1.2);

    /// <summary>Corrupted:InfamyLossOnDefeat (0.1, the data's pvpinfamyloss; pve 0): the share of its infamy the beaten side of an invasion loses.</summary>
    public double InfamyLossOnDefeat { get; init; } = 0.1;

    /// <summary>Corrupted:MaxInvasionsPerRun (3; pve 1): invasions of one run at most.</summary>
    public int MaxInvasionsPerRun { get; init; } = 3;

    /// <summary>Corrupted:ShrineUseSeconds (3) [design]: the shrine's use time between CorruptedShrineUsageStart (460) and its effect; damage cancels it (461).</summary>
    public double ShrineUseSeconds { get; init; } = 3;

    /// <summary>Corrupted:InvadeCountdownSeconds (10) [design]: a matched player invader's "invading in" countdown (449 kind 6) before it is moved.</summary>
    public double InvadeCountdownSeconds { get; init; } = 10;

    /// <summary>Corrupted:OpenWorldPortals (true): randomdungeons.xml's CORRUPTED_SOLO_* exits stand on the open world's SOLO markers (key 12 true).</summary>
    public bool OpenWorldPortals { get; init; } = true;

    /// <summary>
    /// Corrupted:MapsOpenDungeons (false): the two corrupted dungeon maps open a personal corrupted portal (their activaterandomdungeon), and the
    /// invasion tickets move to the Knight's Vows (SPEC §4.8, <see cref="Content.EventSettings.MoveTickets"/>). Off: the maps stay invasion tickets.
    /// </summary>
    public bool MapsOpenDungeons { get; init; }

    /// <summary>
    /// Corrupted:AntiReset (true; pve false): during an invasion the anti-reset imps (CD_DEMON_IMP_ANTIRESET) come every
    /// timeuntilantiresetmobspawninseconds (180) at the points near the fighters at least antiresetmobrequiredplayerdistanceforspawn (11 m) from
    /// every player, and go timeuntilantiresetmobdespawninseconds (45) later [design].
    /// </summary>
    public bool AntiReset { get; init; } = true;

    /// <summary>Corrupted:AntiResetImps (3) [design]: the imps of one such wave.</summary>
    public int AntiResetImps { get; init; } = 3;

    /// <summary>Corrupted:Chain (true): after the demon the chain exit in its lair and the client's Next Dungeon (the same rank, a new map, the first way back).</summary>
    public bool Chain { get; init; } = true;

    /// <summary>Corrupted:ChainBonus (0; pve 0.1), ChainBonusMax (0.5): a chained run's completion silver, fame and infamy + this per dungeon of the streak, at most the max.</summary>
    public double ChainBonus { get; init; }

    public double ChainBonusMax { get; init; } = 0.5;
}

/// <summary>
/// StageL: the hellgates' switches (Hellgates:*, analysis/work/stageL/hellgates/SPEC.md §5.8); the chat lines and map weights are
/// content/hellgates.json (<see cref="Content.HellgateSettings"/>). The record's values are the official preset's; the pve preset changes
/// VersusModes (the two lethal modes only), ShrineMode (Fixed), CheckItemPower (false), BossMapFactor (3), GroupScaling (true), EnemyTeamSize
/// (Party), KnockdownSeconds (30), LeaveCommand (true) and FinishedLingerSeconds (300), and of phase B RestrictedArea (false), SpawnBuffs (false)
/// and Matchmaking (Bots) (<see cref="ServerConfig.PveDefaults"/>).
/// </summary>
public sealed record HellgateOptions
{
    /// <summary>The four modes of hellgate.xml (ids 1-4) by name.</summary>
    public static readonly IReadOnlyList<string> AllModes = ["hellgate_2v2_non_lethal", "hellgate_5v5_non_lethal", "hellgate_2v2", "hellgate_5v5"];

    /// <summary>The lethal two (the pve preset's versus modes).</summary>
    public static readonly IReadOnlyList<string> LethalModes = ["hellgate_2v2", "hellgate_5v5"];

    /// <summary>Hellgates:Enabled (true): the Hell Dens' shrines answer UseHellgateShrine (op 255).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Hellgates:Modes (all four): the modes a shrine takes (names; the others are refused).</summary>
    public IReadOnlyList<string> Modes { get; init; } = AllModes;

    /// <summary>Hellgates:VersusModes (all four; pve the lethal two): the modes whose second objective is an enemy team of bots, else a demon guardian.</summary>
    public IReadOnlyList<string> VersusModes { get; init; } = AllModes;

    /// <summary>
    /// Hellgates:ShrineMode (Gui; pve Fixed): NewHellgateShrine key 5. Fixed: the client uses the shrine at once in the viewer's .hg mode (2-4
    /// players for a 5v5 too); Gui: its mode selector, whose Join wants as many party members as the mode's team.
    /// </summary>
    public HellgateShrineMode ShrineMode { get; init; } = HellgateShrineMode.Gui;

    /// <summary>Hellgates:DefaultMode (hellgate_2v2_non_lethal): a character's mode before .hg mode.</summary>
    public string DefaultMode { get; init; } = "hellgate_2v2_non_lethal";

    /// <summary>Hellgates:CheckItemPower (true; pve false): the server's copy of the client's item power gate (700 / 900; the client checks its own figure always).</summary>
    public bool CheckItemPower { get; init; } = true;

    /// <summary>Hellgates:ChannelSeconds (3, individualchanneltime) and LockSeconds (3, secondstoenter): a member's channel, then the party's lock before the push.</summary>
    public double ChannelSeconds { get; init; } = 3;

    public double LockSeconds { get; init; } = 3;

    /// <summary>Hellgates:CancelDistance (1 m): a move this far from where the channel started cancels it.</summary>
    public double CancelDistance { get; init; } = 1;

    /// <summary>Hellgates:BossMapFactor (1; pve 3): × the weight of the boss maps (Hellgate_06 and _10, weight 5) in the map pick.</summary>
    public double BossMapFactor { get; init; } = 1;

    /// <summary>
    /// Hellgates:GroupScaling (false; pve true), ScalingBase (0.40), ScalingPerPlayer (0.15), ScalingMax (1.0), ScaleDamage (true): the mobs' health
    /// (and damage) × min(ScalingMax, (Base + PerPlayer × n) / (Base + PerPlayer × teamsize)) for n players sent in (a full team: 1).
    /// </summary>
    public bool GroupScaling { get; init; }

    public double ScalingBase { get; init; } = 0.40;

    public double ScalingPerPlayer { get; init; } = 0.15;

    public double ScalingMax { get; init; } = 1.0;

    public bool ScaleDamage { get; init; } = true;

    /// <summary>Hellgates:FamePerPoint (100) [assume]: a demon's kill adds max(1, round(its mobs.xml fame / this)) points.</summary>
    public double FamePerPoint { get; init; } = 100;

    /// <summary>Hellgates:EnemyTeamSize (Mode; pve Party): the bots of a versus mode's enemy team: the mode's team size, or the party members sent in.</summary>
    public HellgateEnemyTeam EnemyTeamSize { get; init; } = HellgateEnemyTeam.Mode;

    /// <summary>Hellgates:MatchStartSpell (true): HELLGATE_MATCH_START_SPELL (full health and energy) on the party when objective 2 starts.</summary>
    public bool MatchStartSpell { get; init; } = true;

    /// <summary>Hellgates:MobDespawnOnMatch (true): a versus mode's remaining demons leave mobdespawndelay (2.4 s) after objective 2 starts.</summary>
    public bool MobDespawnOnMatch { get; init; } = true;

    /// <summary>Hellgates:KnockdownSeconds (null = the archetype's knockdowngetuptimeseconds_pvp, 600; pve 30): a PvP knockdown inside a hellgate.</summary>
    public double? KnockdownSeconds { get; init; }

    /// <summary>Hellgates:WipeEndsRun (true): every party member inside down at once loses the run (state 6, the exit portal, no objective chest).</summary>
    public bool WipeEndsRun { get; init; } = true;

    /// <summary>Hellgates:ClaimSeconds (120): the objective chest not opened this long after the win: the claim counts as done (state 4, the exit portal).</summary>
    public double ClaimSeconds { get; init; } = 120;

    /// <summary>Hellgates:LeaveCommand (false; pve true): .hg leave for players (else the escape spell, 15 min during the run, or the exit portal).</summary>
    public bool LeaveCommand { get; init; }

    /// <summary>Hellgates:StartClearance (25 m): the start points keep this far from every mob spawn point (SPEC §5.4).</summary>
    public double StartClearance { get; init; } = 25;

    /// <summary>
    /// Hellgates:MaxMinutes (30), FinishedLingerSeconds (120; pve 300), EmptyCloseSeconds (60), ArrivalGraceSeconds (120) [assume]: an instance
    /// closes at the latest MaxMinutes after its start, this long after the claim or the loss, or empty this long (a player sent in counts as
    /// inside for ArrivalGraceSeconds until its Join).
    /// </summary>
    public double MaxMinutes { get; init; } = 30;

    public double FinishedLingerSeconds { get; init; } = 120;

    public double EmptyCloseSeconds { get; init; } = 60;

    public double ArrivalGraceSeconds { get; init; } = 120;

    /// <summary>Hellgates:FallbackCluster (0000): where a player goes out without a saved way back.</summary>
    public string FallbackCluster { get; init; } = "0000";

    /// <summary>
    /// Phase B (SPEC §7) Hellgates:Chain (true): after the claim the chain exit (402 ActivateHellgateExit, a walk-in portal at CHAIN_EXIT) leads the
    /// winners into the next hellgate of the mode, the chests × the mode's bct.af(chain).
    /// </summary>
    public bool Chain { get; init; } = true;

    /// <summary>
    /// Phase B Hellgates:RestrictedArea (true; pve false): arearestrictiondelay after the start of a fight against a team (bots or a party) the
    /// area closes in (452), the warning ring and beyond it the damage (1 % and 2 % and more of the maximum health a second).
    /// </summary>
    public bool RestrictedArea { get; init; } = true;

    /// <summary>Phase B Hellgates:SpawnBuffs (true; pve false): the cluster type's spawnbuffs on arrival (HELL_2V2_ENTER −35 % healing, SPAWN_INVULNERABILITY_HELL).</summary>
    public bool SpawnBuffs { get; init; } = true;

    /// <summary>
    /// Phase B Hellgates:InfamyWin (100; 0 = none) [assume: the official rating is an MMR this server does not model]: the infamy of the mode's
    /// InfamyType (Hell2v2, Hell5v5) each winner inside gets at the win, × the chain's factor (UpdateInfamy 446, the milestones in chat).
    /// </summary>
    public double InfamyWin { get; init; } = 100;

    /// <summary>
    /// Phase B Hellgates:Matchmaking (Players; pve Bots) and MatchSeconds (60): Players lets a second party of the same versus mode into a
    /// hellgate made less than MatchSeconds ago (start B; the second objective is the fight between the two, PvP for those who chose the mode);
    /// without one the bots come as with Bots.
    /// </summary>
    public HellgateMatchmaking Matchmaking { get; init; } = HellgateMatchmaking.Players;

    public double MatchSeconds { get; init; } = 60;
}

/// <summary>Hellgates:Matchmaking (stageL hellgates SPEC §7): where a versus mode's enemy team comes from.</summary>
public enum HellgateMatchmaking
{
    /// <summary>Always a team of bots.</summary>
    Bots,

    /// <summary>A second party of the same mode while the hellgate is young (MatchSeconds), else bots.</summary>
    Players,
}

/// <summary>Hellgates:ShrineMode (stageL hellgates SPEC §5.3): NewHellgateShrine key 5.</summary>
public enum HellgateShrineMode
{
    /// <summary>The client's mode selector (HellGateEntranceGui; its Join wants as many party members as the mode's team).</summary>
    Gui,

    /// <summary>A fixed-mode shrine: the client uses it at once in the viewer's mode (.hg mode).</summary>
    Fixed,
}

/// <summary>Hellgates:EnemyTeamSize (stageL hellgates SPEC §5.2): how many bots a versus mode's enemy team has.</summary>
public enum HellgateEnemyTeam
{
    /// <summary>The mode's team size (2 or 5).</summary>
    Mode,

    /// <summary>The party members sent in.</summary>
    Party,
}

/// <summary>Corrupted:Invasions and Corrupted:PlayerInvasions (stageL corrupted SPEC §4.4, §4.7).</summary>
public enum CorruptedInvasionMode
{
    /// <summary>Never.</summary>
    Off,

    /// <summary>Only after the owner's Demonic Shrine (the pve preset: PvP by consent).</summary>
    Consent,

    /// <summary>The data's rolls (the official preset).</summary>
    Official,
}

/// <summary>The look of a bot (Bots:Look): a preset of characters.xml AvatarToCustomizationPresets, or a free mesh and colour per item.</summary>
public enum BotLook
{
    Presets,
    Random,
}

/// <summary>Bots:PvpRules (stage I bots SPEC §1.8): when a bot and a player are Hostile.</summary>
public enum BotPvpRules
{
    /// <summary>A player may attack a bot as the client's zone table allows; a bot starts a fight only where Pvp:Mode makes the pair Hostile.</summary>
    Consent,

    /// <summary>Bots are players under Pvp:Mode both ways.</summary>
    Server,

    /// <summary>The client's zone table both ways, whatever Pvp:Mode (a "dangerous world" under the pve preset).</summary>
    Official,
}

/// <summary>
/// Stage I: the player bots' switches and timings (Bots:*, analysis/work/stageI/bots/SPEC.md §3.1); the tables are content/bots.json
/// (<see cref="Content.BotSettings"/>). Bots are not an official feature: off under Preset official, on under pve; every other key is the same
/// in both presets. Ranges are (min, max), drawn uniformly.
/// </summary>
public sealed record BotOptions
{
    /// <summary>Bots:Enabled (official false, pve true): the master switch; .bot on|off changes it while running (not saved).</summary>
    public bool Enabled { get; init; }

    /// <summary>Bots:MaxTotal (400; 150 before stageM bots) and Bots:MaxPerMap (40): the caps of the population.</summary>
    public int MaxTotal { get; init; } = 400;

    public int MaxPerMap { get; init; } = 40;

    /// <summary>Bots:PopulateSeconds (5) and Bots:SpawnPerPass (4; 2 before stageM bots): a map with a human gets at most this many new bots per pass.</summary>
    public double PopulateSeconds { get; init; } = 5;

    public int SpawnPerPass { get; init; } = 4;

    /// <summary>Bots:MinSpawnCells (Visibility:EnterCells + 1): a bot appears more than this many cells (Chebyshev) from every human's cell, out of view.</summary>
    public int? MinSpawnCells { get; init; }

    /// <summary><see cref="MinSpawnCells"/> or its default for the window <paramref name="visibility"/>.</summary>
    public int SpawnCells(VisibilityOptions visibility) => MinSpawnCells ?? visibility.EnterCells + 1;

    /// <summary>Bots:LingerSeconds (30): the bots of a map leave this long after its last human did.</summary>
    public double LingerSeconds { get; init; } = 30;

    /// <summary>Bots:RespawnSeconds ("60-180"): a slot emptied by a defeat or a GM's .bot clear is filled again after this.</summary>
    public (double Min, double Max) RespawnSeconds { get; init; } = (60, 180);

    /// <summary>Bots:StayMinutes ("10-30"): how long a bot stays before it leaves through an exit or logs out.</summary>
    public (double Min, double Max) StayMinutes { get; init; } = (10, 30);

    /// <summary>Bots:ArrivalSeconds ("20-90"): after a bot left, the next one of its slot arrives through an exit this much later.</summary>
    public (double Min, double Max) ArrivalSeconds { get; init; } = (20, 90);

    /// <summary>Bots:ActiveRange (120): a bot thinks only while a human on its map is this near or sees it; else it stands.</summary>
    public double ActiveRange { get; init; } = 120;

    /// <summary>Bots:ThinkSeconds ("0.5-2"): the time between two decisions of a bot.</summary>
    public (double Min, double Max) ThinkSeconds { get; init; } = (0.5, 2);

    /// <summary>Bots:MoveResendSeconds (0.5): a leg under way is sent again this often while a human sees the bot (the client dead-reckons 1 s).</summary>
    public double MoveResendSeconds { get; init; } = 0.5;

    /// <summary>Bots:Move:PauseChance (0.15): the chance of a 0.3-1.2 s pause at a corner of a path.</summary>
    public double PauseChance { get; init; } = 0.15;

    /// <summary>Bots:Look (Presets) and Bots:FemaleShare (0.4): the appearance.</summary>
    public BotLook Look { get; init; } = BotLook.Presets;

    public double FemaleShare { get; init; } = 0.4;

    /// <summary>Bots:Nav:LatticeStep (4 m), Bots:Nav:MaxExpansions (20000), Bots:Nav:SlowPathMs (2: a slower path is logged at Debug), Bots:Nav:CacheClusters (32 walk grids kept).</summary>
    public int LatticeStep { get; init; } = 4;

    public int MaxExpansions { get; init; } = 20000;

    public double SlowPathMs { get; init; } = 2;

    public int CacheClusters { get; init; } = 32;

    /// <summary>Bots:City:PerPoi (3): city bots dwelling at one point of interest at most; Bots:City:EmoteChance (0.35): an emote at a dwell.</summary>
    public int CityPerPoi { get; init; } = 3;

    public double CityEmoteChance { get; init; } = 0.35;

    /// <summary>Bots:PvpRules (Consent in both presets).</summary>
    public BotPvpRules PvpRules { get; init; } = BotPvpRules.Consent;

    /// <summary>
    /// Bots:PvpRulesByZone (none; the pve preset { "red": "Official", "black": "Official" }): Bots:PvpRules for the zones of one
    /// pvpwarningtype (safe, yellow, orange, red, black): there patrols attack every player on sight as the client's zone table allows.
    /// </summary>
    public IReadOnlyDictionary<string, BotPvpRules> PvpRulesByZone { get; init; } = NoZoneRules;

    /// <summary>The empty Bots:PvpRulesByZone (one instance: a default config equals a loaded one).</summary>
    public static readonly IReadOnlyDictionary<string, BotPvpRules> NoZoneRules = new Dictionary<string, BotPvpRules>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Bots:PvpRules of a zone of pvpwarningtype <paramref name="warningType"/> (Bots:PvpRulesByZone, else Bots:PvpRules); stageL roads (SPEC §5.6,
    /// V9): a map of zone class <paramref name="zoneClass"/> ("Roads") takes that key first, so { "Roads": "Official" } leaves the open black zones alone.
    /// </summary>
    public BotPvpRules PvpRulesIn(string? warningType, string? zoneClass = null) =>
        zoneClass is not null && PvpRulesByZone.TryGetValue(zoneClass, out var z) ? z
        : warningType is not null && PvpRulesByZone.TryGetValue(warningType, out var r) ? r : PvpRules;

    /// <summary>Bots:MountDistance (40): a bot that carries a mount rides a path at least this long (m) out of combat; shorter ones it walks.</summary>
    public double MountDistance { get; init; } = 40;

    /// <summary>Bots:City:Gatherers (0): gatherer bots on the T1 trees of a royal city, beside its city bots.</summary>
    public int CityGatherers { get; init; }

    /// <summary>
    /// Bots:Gatherer:Flag (Zone): a gatherer's flag; Zone = Blue in safe, yellow and orange zones, Red in red and black ones with
    /// Bots:Gatherer:RedShare (1.0, else Blue), Blue on the Roads of Avalon; or always Blue / Red (a Red one in a zone that keeps no Red turns Blue on arrival).
    /// </summary>
    public BotGathererFlag GathererFlag { get; init; } = BotGathererFlag.Zone;

    public double GathererRedShare { get; init; } = 1.0;

    /// <summary>Bots:Gatherer:Radius (150): a gatherer's nodes lie within this many metres of its spawn; Bots:Gatherer:Nodes ("6-20"): nodes per trip.</summary>
    public double GathererRadius { get; init; } = 150;

    public (double Min, double Max) GathererNodes { get; init; } = (6, 20);

    /// <summary>Bots:Gatherer:AvoidPlayerRadius (6) / AvoidMobRadius (12): a node with a human this near, or a live hostile mob's home this near, is not chosen.</summary>
    public double AvoidPlayerRadius { get; init; } = 6;

    public double AvoidMobRadius { get; init; } = 12;

    /// <summary>Bots:Gatherer:FleeHealth (0.25) / FleeChance (0.5): below this share of its health a fighting gatherer runs off once per fight with this chance.</summary>
    public double FleeHealth { get; init; } = 0.25;

    public double FleeChance { get; init; } = 0.5;

    /// <summary>Bots:ConsumeNodes (false): a gatherer takes the node's charges as a player would (HarvestableChangeState, regrowth); false = a show only.</summary>
    public bool ConsumeNodes { get; init; }

    /// <summary>Bots:Pvp:EngageSeconds (20): how long a hit lets the bot answer its attacker; Bots:Pvp:EngageParty (true): the attacker's party too.</summary>
    public double EngageSeconds { get; init; } = 20;

    public bool EngageParty { get; init; } = true;

    /// <summary>Bots:Pvp:CrimesAgainstBots (official true, pve false): a Red player's hit on a bot that is not Red is a crime (reputation, OUTLAW).</summary>
    public bool CrimesAgainstBots { get; init; } = true;

    /// <summary>Bots:Combat:TickMs (200): how often a fighting bot decides; Bots:Combat:ReactionMs (600): its first swing after the first hit.</summary>
    public int CombatTickMs { get; init; } = 200;

    public int ReactionMs { get; init; } = 600;

    /// <summary>
    /// Bots:Combat:Spells (Full, both presets; stageJ pvp-spells SPEC §2.9, D10): a fighting bot casts the active spells of its gear (its
    /// Q, W, E, armour, helmet and shoe slots) through the players' cast path, with Bots:Combat:SpellChance (0.6) per second of its
    /// fight a spell whose target, range, cooldown and energy fit; Off = auto-attacks only.
    /// </summary>
    public BotSpells Spells { get; init; } = BotSpells.Full;

    /// <summary>
    /// Bots:Combat:SpellChance (0.6, 0-1; 0.35 before stageM bots): the chance per second of game time in a fight that a bot casts (from the
    /// time since its last roll); its attack spells come first (<see cref="Bots.BotCombat"/>).
    /// </summary>
    public double SpellChance { get; init; } = 0.6;

    /// <summary>Bots:Patrol:LeashRadius (40): a bot drops a target this far from where its fight began (gatherers too).</summary>
    public double LeashRadius { get; init; } = 40;

    /// <summary>
    /// Bots:Pvp:LethalToPlayers (false): a bot's hit that takes a player to 0 in a lethal cluster under Pvp:LethalDeath kills him (the death
    /// without a reward, its bag for anyone); false = a knockdown everywhere.
    /// </summary>
    public bool LethalToPlayers { get; init; }

    /// <summary>
    /// Bots:Combat:DamageFactor (1.0) on a bot's hits on players, Bots:Combat:HealthFactor (1.0; the pve preset 0.7 before stageN botpower) on a bot's maximum
    /// health (corrupted-dungeon invaders too): the difficulty. pve 0.7: the user's T4 quarterstaff (AP 313.4) needs about a third fewer
    /// hits on a T6-T7 full-spec bot (stageM pvp-damage NOTES §1).
    /// </summary>
    public double DamageFactor { get; init; } = 1.0;

    public double HealthFactor { get; init; } = 1.0;

    /// <summary>
    /// Bots:Gear:Mastery (1.0; the pve preset 0.5): the share of a full destiny board's item power a bot's gear carries (its spec levels ×
    /// this, so 1 = every node at its base level count, the official-like full-spec player: T7 gatherer gear at IP 1288; 0 = the items' own
    /// item power: T7 1000 + quality). Their armour, health and ability power follow (stageM pvp-damage NOTES §5.2b).
    /// </summary>
    public double GearMastery { get; init; } = 1.0;

    /// <summary>
    /// Bots:Gear:DamageBonus (true; the pve preset false): a bot's spells add its worn items' spell damage bonus as a player's do
    /// (Combat:GearDamageBonus; official-like: a cloth patrol's spells × 1.4-1.5, leather × 1.2-1.3, plate × 1-1.1). false = a bot's
    /// spells keep only its buffs and passives, as before that bonus came (stageM pvp-damage review).
    /// </summary>
    public bool GearDamageBonus { get; init; } = true;

    /// <summary>Bots:Patrol:AggroRadius (15): a patrol attacks a player it may fight this near (m), on sight, when a way leads to him.</summary>
    public double AggroRadius { get; init; } = 15;

    /// <summary>Bots:Patrol:SpareMinutes (5): a patrol leaves a player it knocked down alone this long; Bots:Patrol:JoinGraceSeconds (10): nor one who arrived on the map this recently.</summary>
    public double SpareMinutes { get; init; } = 5;

    public double JoinGraceSeconds { get; init; } = 10;

    /// <summary>Bots:Patrol:AttackMounted (true): a patrol attacks riding players too.</summary>
    public bool AttackMounted { get; init; } = true;

    /// <summary>Bots:Patrol:GroupSize ("1-3"): patrols walk in groups of this many.</summary>
    public (double Min, double Max) GroupSize { get; init; } = (1, 3);

    /// <summary>Bots:Roads:PatrolGroupSize ("2-3"): the patrol groups of a Roads of Avalon map (the zone class Roads), the road gankers (stageL roads SPEC §5.6).</summary>
    public (double Min, double Max) RoadsPatrolGroupSize { get; init; } = (2, 3);

    /// <summary>
    /// Bots:Roads:Portals (true): on a road the bots come and go through its portals (stageL roads SPEC §7.2: a pass takes a charge and opens the
    /// "recently used" window; a bot leaves a portal's last charge to the players), and a patrol's beat strings the portals within 400 m.
    /// </summary>
    public bool RoadsPortals { get; init; } = true;

    /// <summary>Bots:Roads:PortalWaitSeconds ("20-60"): how long a road patrol waits at a portal of its beat for a Red player coming through.</summary>
    public (double Min, double Max) RoadsPortalWaitSeconds { get; init; } = (20, 60);

    /// <summary>Bots:Patrol:RouteDwellSeconds ("2-8") at a stop of its route, Bots:Patrol:RouteEndDwellSeconds ("5-20") at a road's end.</summary>
    public (double Min, double Max) RouteDwellSeconds { get; init; } = (2, 8);

    public (double Min, double Max) RouteEndDwellSeconds { get; init; } = (5, 20);

    /// <summary>
    /// Bots:Rewards:FameFactor (0.5): a defeated bot's kill fame share; Bots:Rewards:GearDropChance (0.2): each of its worn combat items in its
    /// bag; Bots:Rewards:ResourceDropShare (0.5): of what a gatherer "gathered".
    /// </summary>
    public double FameFactor { get; init; } = 0.5;

    public double GearDropChance { get; init; } = 0.2;

    /// <summary>
    /// Bots:Rewards:DeathGearDropChance (1.0, official full loot; the pve preset 0.5): GearDropChance's place for a bot that <b>died</b> (a
    /// playersarelethal zone, red and black: Bots:Defeat:DieInLethalZones); a knockdown keeps GearDropChance.
    /// </summary>
    public double DeathGearDropChance { get; init; } = 1.0;

    /// <summary>
    /// Bots:Rewards:MinItems (0; the pve preset 1, 0-6): a full reward's bag holds at least this many of its worn combat items; when the
    /// rolls gave fewer, random further ones are moved out of its equipment (never a copy).
    /// </summary>
    public int MinItems { get; init; }

    public double ResourceDropShare { get; init; } = 0.5;

    /// <summary>Bots:Rewards:LootProtectionSeconds (null = gamedata pvpallowedprotection 120 s, pvpforcedprotection 90 s in PvpForced): its bag the killer's party's this long.</summary>
    public double? LootProtectionSeconds { get; init; }

    /// <summary>
    /// Bots:Rewards:BagsPerHour (30): bags and silver piles of bots per killer and hour, beyond it fame only; Bots:Rewards:SameBotMinutes (10): the
    /// same bot (persona) gives one killer its full reward once in this time, else a quarter of the fame and nothing else.
    /// </summary>
    public int BagsPerHour { get; init; } = 30;

    public double SameBotMinutes { get; init; } = 10;

    /// <summary>Bots:Defeat:DieInLethalZones (true): a bot defeated in a playersarelethal zone shows a death (Died), elsewhere a knockdown.</summary>
    public bool DieInLethalZones { get; init; } = true;

    /// <summary>Bots:DownDespawnSeconds (10, below 90): a knocked-down bot is gone this long after its defeat (a dead one after playervisibilityafterdeath).</summary>
    public double DownDespawnSeconds { get; init; } = 10;

    // ---- stageN botpower (analysis/work/stageN/botpower/SPEC.md) ----

    /// <summary>
    /// Bots:Difficulty (1.0, 0.1-10): x the maximum health and x the hits on players of every bot but the hired companions, on top of
    /// Bots:Combat:HealthFactor / DamageFactor (1 = an equal-tier, full-spec player).
    /// </summary>
    public double Difficulty { get; init; } = 1.0;

    /// <summary>Bots:Gear:RedZoneTierBonus (1, 0-3): patrols in red and black zones (and on the roads) wear gear this many tiers above the open world's roll.</summary>
    public int RedZoneTierBonus { get; init; } = 1;

    /// <summary>Bots:Gear:Quality ("1-2", 1-5): the quality of a world bot's gear (1 normal .. 5 masterpiece: its item power, and its drop).</summary>
    public (double Min, double Max) Quality { get; init; } = (1, 2);

    /// <summary>
    /// Bots:Gear:EliteChance (0.06): a patrol or Drifter spawns as an elite with this chance: its weapon, offhand and armour enchanted
    /// Bots:Gear:EliteEnchant ("1-3") where items.xml has the level, of quality Bots:Gear:EliteQuality ("3-4").
    /// </summary>
    public double EliteChance { get; init; } = 0.06;

    public (double Min, double Max) EliteEnchant { get; init; } = (1, 3);

    public (double Min, double Max) EliteQuality { get; init; } = (3, 4);

    /// <summary>
    /// Bots:Combat:Tactics (true): a fighting bot picks its spells by the situation (a gap closer when far, a heal or shield when low, crowd
    /// control on a casting target, its damage when the target is low: those as soon as off cooldown, without the SpellChance roll), drinks its
    /// potion or falls back once below Bots:Combat:RetreatHealth (0.3), and a ranged one steps back from a target nearer than
    /// Bots:Combat:KiteDistance (5 m edge to edge; 0 = never). false = the stageM behaviour (spells by SpellChance, heals first when hurt).
    /// </summary>
    public bool Tactics { get; init; } = true;

    public double KiteDistance { get; init; } = 5;

    public double RetreatHealth { get; init; } = 0.3;

    /// <summary>Bots:Combat:Potions ("1-2", 0-10): the healing potions (T{t}_POTION_HEAL) a patrol, Drifter or gatherer carries in its potion slot.</summary>
    public (double Min, double Max) Potions { get; init; } = (1, 2);

    /// <summary>Bots:Combat:Food (true): a patrol or Drifter eats a meal (T{t}_MEAL_STEW) at its spawn: the food buff for its stay.</summary>
    public bool Food { get; init; } = true;

    /// <summary>Bots:Combat:FocusWeakest (true): a patrol attacks the weakest player in reach (lowest health share) and turns to one nearly down.</summary>
    public bool FocusWeakest { get; init; } = true;

    /// <summary>Bots:Patrol:RedGroupSize ("2-3"): the patrol groups of red and black zones (the roads keep Bots:Roads:PatrolGroupSize).</summary>
    public (double Min, double Max) RedGroupSize { get; init; } = (2, 3);

    /// <summary>
    /// Bots:Rewards:EliteSilverFactor (2) x an elite bot's silver; Bots:Rewards:EliteExtraItems (1): its bag holds this many more of its worn
    /// items than Bots:Rewards:MinItems (its enchanted gear drops as it was worn).
    /// </summary>
    public double EliteSilverFactor { get; init; } = 2;

    public int EliteExtraItems { get; init; } = 1;
}

/// <summary>Bots:Combat:Spells: whether fighting bots cast their gear's spells (stageJ pvp-spells SPEC §2.9).</summary>
public enum BotSpells
{
    /// <summary>Auto-attacks only.</summary>
    Off,

    /// <summary>The whole kit through <see cref="Systems.SpellCasts.Check"/> and <see cref="Systems.SpellCasts.Start"/>, as a player's casts.</summary>
    Full,
}

/// <summary>Bots:Gatherer:Flag (stage I bots SPEC §1.8, D4): a gatherer's flag by its zone, or always Blue or Red.</summary>
public enum BotGathererFlag
{
    Zone,
    Blue,
    Red,
}

/// <summary>
/// The server's checks of a ChangeCluster request (SPEC stage 3 §4.1). The client runs the same checks before it sends one,
/// so these are defensive: <see cref="PositionTolerance"/> is the margin around the exit square (the official one is
/// unknown, §13), <see cref="CheckExitCooldown"/> turns the exit cooldown after a Join off (1 s in a city, 10 s elsewhere).
/// </summary>
public sealed record TransferOptions
{
    /// <summary>Metres the player may be outside the exit square, per axis (Transfer:PositionTolerance, 2).</summary>
    public float PositionTolerance { get; init; } = 2;

    /// <summary>Refuse an exit used too soon after the Join (Transfer:CheckExitCooldown, true).</summary>
    public bool CheckExitCooldown { get; init; } = true;
}

/// <summary>StageL mists: how the zones behind a portal are shared (Mists:Matchmaking, analysis/work/stageL/mists/SPEC.md §6.2).</summary>
public enum MistsMatchmaking
{
    /// <summary>One zone per team and portal (the pve preset): nobody meets a stranger.</summary>
    Private,

    /// <summary>An open zone of the same mode, rarity and tier with room (softplayerlimitmax) is joined, else a new one (the official preset).</summary>
    Shared,
}

/// <summary>StageL mists: where a zone's open-world exit leads (Mists:OpenWorldExit, SPEC §6.5).</summary>
public enum MistsExitTarget
{
    /// <summary>Back to the entrance cluster, in front of the portal entered (the pve preset).</summary>
    Entrance,

    /// <summary>A world cluster of the warning type drawn by the mode's openworldexitdistribution, tier ±1, on the entrance's continent (official).</summary>
    Random,
}

/// <summary>
/// StageN companions (analysis/work/stageN/companions/SPEC.md): bots a player hires with .companion hire &lt;tank|healer|melee|ranged&gt; [tier]
/// that join its party, follow it over every cluster change and fight the mobs that fight the party (<see cref="Bots.Companions"/>). The
/// record's values are the official preset's (off); the pve preset switches them on. Nothing of them is saved: they go at a restart or logout.
/// </summary>
public sealed record CompanionOptions
{
    /// <summary>Companions:Enabled (false; pve true): .companion is accepted.</summary>
    public bool Enabled { get; init; }

    /// <summary>Companions:MaxPerPlayer (4, at most 4): companions one player may have at once.</summary>
    public int MaxPerPlayer { get; init; } = 4;

    /// <summary>Companions:MaxPartySize (5): a companion is hired only while the party (humans and companions) stays within this.</summary>
    public int MaxPartySize { get; init; } = 5;

    /// <summary>Companions:MinTier / MaxTier (3 / 8): the gear tier a hire gets from the owner's average item power is kept within these.</summary>
    public int MinTier { get; init; } = 3;

    public int MaxTier { get; init; } = 8;

    /// <summary>
    /// Companions:GearMastery (1.0; the pve preset 0.5) and Companions:GearDamageBonus (true; pve false): a companion's share of a full destiny
    /// board's item power and whether its spells add its worn spell damage bonus (stageN botpower: split off Bots:Gear:Mastery / DamageBonus,
    /// so that stronger world bots leave the hired allies as they were).
    /// </summary>
    public double GearMastery { get; init; } = 1.0;

    public bool GearDamageBonus { get; init; } = true;

    /// <summary>Companions:HealthFactor (1): × a companion's maximum health (its gear's, as a player's).</summary>
    public double HealthFactor { get; init; } = 1;

    /// <summary>Companions:DamageFactor (1): × a companion's auto-attack and spell damage on mobs.</summary>
    public double DamageFactor { get; init; } = 1;

    /// <summary>Companions:TankThreatFactor (3): a tank's damage on a mob counts this many times as threat.</summary>
    public double TankThreatFactor { get; init; } = 3;

    /// <summary>Companions:HealBelow (0.85): a healer heals a party member under this share of its health.</summary>
    public double HealBelow { get; init; } = 0.85;

    /// <summary>Companions:FollowDistance (4 m): out of a fight a companion walks back to its owner beyond this.</summary>
    public double FollowDistance { get; init; } = 4;

    /// <summary>Companions:AssistRange (25 m): a companion takes on the mobs fighting the party within this of its owner.</summary>
    public double AssistRange { get; init; } = 25;

    /// <summary>Companions:LeashDistance (35 m): a fighting companion this far from its owner gives up the fight and follows.</summary>
    public double LeashDistance { get; init; } = 35;

    /// <summary>Companions:TeleportDistance (60 m): a companion this far from its owner (or stuck, below) is put next to it.</summary>
    public double TeleportDistance { get; init; } = 60;

    /// <summary>Companions:StuckSeconds (6): a companion that has not come nearer to its owner for this long is put next to it.</summary>
    public double StuckSeconds { get; init; } = 6;

    /// <summary>Companions:OwnerGraceSeconds (120): companions wait this long for an owner that is not in game (a cluster change, a relog) before they go.</summary>
    public double OwnerGraceSeconds { get; init; } = 120;

    /// <summary>Companions:SpellChance (0.5): the chance per second of a fight that a companion casts one of its spells.</summary>
    public double SpellChance { get; init; } = 0.5;
}

/// <summary>
/// StageL: the Mists (Mists:*, analysis/work/stageL/mists/SPEC.md §6.10); the tables are content/mists.json (<see cref="Content.MistsSettings"/>).
/// The record's values are the official preset's; the pve preset changes Matchmaking (Private), LifetimeFactor (2), BlockFactor (0.5),
/// DuoSoloScaling (0.6), OpenWorldExit (Entrance), UnrestrictedZones (false), BossChest (true), RarityWeights (6000/1600/1200/800/400) and,
/// phase B, StandingFactor (2) (<see cref="ServerConfig.PveDefaults"/>). PvP inside follows Pvp:* (the mists' archetypes: under Pvp:Mode
/// Consensual Red against Red only).
/// </summary>
public sealed record MistsOptions
{
    /// <summary>Mists:Enabled (true): the open world's mists portals stand on the random exit markers and ENTER_MISTS is accepted.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Mists:Matchmaking (Shared; pve Private): a zone per team, or shared zones up to the map's soft player limit.</summary>
    public MistsMatchmaking Matchmaking { get; init; } = MistsMatchmaking.Shared;

    /// <summary>Mists:LifetimeFactor (1; pve 2): × the map's clusterclosetime and clustershutdowntime (600 / 1200 s).</summary>
    public double LifetimeFactor { get; init; } = 1;

    /// <summary>Mists:BlockFactor (1; pve 0.5): × the treasures' unlock times (initial..final block seconds).</summary>
    public double BlockFactor { get; init; } = 1;

    /// <summary>Mists:OpenWorldWeight (1): × the MISTS_* weights of the random exit distribution (0 = no open-world mists portals).</summary>
    public double OpenWorldWeight { get; init; } = 1;

    /// <summary>Mists:DuoSoloScaling (1; pve 0.6): the mobs' health and damage factor in a duo zone entered by one player alone.</summary>
    public double DuoSoloScaling { get; init; } = 1;

    /// <summary>Mists:OpenWorldExit (Random; pve Entrance): where the zone's open-world exit leads.</summary>
    public MistsExitTarget OpenWorldExit { get; init; } = MistsExitTarget.Random;

    /// <summary>Mists:UnrestrictedZones (true; pve false): the camps' free-for-all zones (phase B: event 544, and two players inside an active camp's circle are Hostile), only under Pvp:Mode Official.</summary>
    public bool UnrestrictedZones { get; init; } = true;

    /// <summary>Mists:BossChest (false; pve true): a personal reward chest at a mist boss's death for every player with BossChestShare of its threat.</summary>
    public bool BossChest { get; init; }

    /// <summary>Mists:BossChestShare (0.10): the share of the mist boss's threat a player needs for its reward chest.</summary>
    public double BossChestShare { get; init; } = 0.10;

    /// <summary>Mists:EmptySeconds (120): a zone with nobody inside (and nobody on the way in) closes this long after.</summary>
    public double EmptySeconds { get; init; } = 120;

    /// <summary>Mists:FallbackCluster (5001, Brecilien): where a character goes out of a zone without a way back.</summary>
    public string FallbackCluster { get; init; } = "5001";

    /// <summary>Mists:RespawnCluster (5001): where a player who died in a zone respawns [inference: the official place is unknown].</summary>
    public string RespawnCluster { get; init; } = "5001";

    /// <summary>
    /// Mists:RarityWeights (empty = the data's MISTS_OPEN_WORLD_MODE_* rerolls, 9590/195/110/65/40; pve 6000/1600/1200/800/400): the weights of
    /// rarity 0-4 an open-world mists portal and a border's next zone draw (SPEC Q13: the mist boss comes from rarity 1).
    /// </summary>
    public IReadOnlyList<double> RarityWeights { get; init; } = [];

    /// <summary>Mists:StandingFactor (1; pve 2), phase B: × the Brecilien standing a mists kill's fame gives (content/mists.json StandingPerFame).</summary>
    public double StandingFactor { get; init; } = 1;

    /// <summary>Mists:StaticEntrance (true), phase B: Brecilien's static entrance (522, op 477) takes players into the mists.</summary>
    public bool StaticEntrance { get; init; } = true;

    /// <summary>Mists:DrifterNeedsRed (true), phase B: the Drifter bots come into a private zone only while a Red-flagged player is in it.</summary>
    public bool DrifterNeedsRed { get; init; } = true;

    /// <summary>Whether this record's values equal <paramref name="other"/>'s (the weights by value).</summary>
    public bool Equals(MistsOptions? other) =>
        other is not null && Enabled == other.Enabled && Matchmaking == other.Matchmaking && LifetimeFactor == other.LifetimeFactor && BlockFactor == other.BlockFactor
        && OpenWorldWeight == other.OpenWorldWeight && DuoSoloScaling == other.DuoSoloScaling && OpenWorldExit == other.OpenWorldExit
        && UnrestrictedZones == other.UnrestrictedZones && BossChest == other.BossChest && BossChestShare == other.BossChestShare && EmptySeconds == other.EmptySeconds
        && FallbackCluster == other.FallbackCluster && RespawnCluster == other.RespawnCluster && RarityWeights.SequenceEqual(other.RarityWeights)
        && StandingFactor == other.StandingFactor && StaticEntrance == other.StaticEntrance && DrifterNeedsRed == other.DrifterNeedsRed;

    public override int GetHashCode() => HashCode.Combine(Enabled, Matchmaking, LifetimeFactor, BlockFactor, OpenWorldExit, BossChest, RarityWeights.Count);
}

/// <summary>
/// StageL: the Roads of Avalon (Roads:*, analysis/work/stageL/roads/SPEC.md §5.8): the portal network, its portals and the travel through them,
/// the road camps' chests. The network's rules, lifetimes and charges are the client data's (tunnelsystem.xml) in both presets; the pve preset
/// only opens .roads to every player (<see cref="ServerConfig.PveDefaults"/>). PvP in the roads follows Pvp:* (black zones: under Pvp:Mode
/// Consensual only two Red players fight there).
/// </summary>
public sealed record RoadsOptions
{
    /// <summary>The preset layers Roads:Layers names and their tunnel presets (a tunnel preset's own name works too).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> LayerNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["ROYAL"] = ["SAFEZONE", "YELLOW", "OPENPVP_RED", "TUNNEL_ROYAL", "TUNNEL_ROYAL_RED"],
        ["BLACK"] = ["OPENPVP_BLACK_LOW", "OPENPVP_BLACK_MEDIUM", "OPENPVP_BLACK_HIGH", "TUNNEL_BLACK_LOW", "TUNNEL_BLACK_MEDIUM", "TUNNEL_BLACK_HIGH"],
        ["TUNNEL"] = ["TUNNEL_LOW", "TUNNEL_MEDIUM", "TUNNEL_HIGH"],
        ["DEEP"] = ["TUNNEL_DEEP", "TUNNEL_DEEP_RAID"],
        ["HIDEOUT"] = ["TUNNEL_HIDEOUT", "TUNNEL_HIDEOUT_DEEP"],
    };

    /// <summary>Roads:Enabled (true): the network, its portals (NewTunnelExit 448) and the travel through them.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Roads:Layers ("all" = empty): the preset layers (or tunnel presets) in the network; an exit whose keyword has no enabled cluster stays idle.</summary>
    public IReadOnlyList<string> Layers { get; init; } = [];

    /// <summary>Whether tunnel preset <paramref name="preset"/> is in the network (<see cref="Layers"/>).</summary>
    public bool LayerOn(string preset) => Layers.Count == 0 || Layers.Any(l => l.Equals(preset, StringComparison.OrdinalIgnoreCase)
        || LayerNames.TryGetValue(l, out var presets) && presets.Contains(preset, StringComparer.OrdinalIgnoreCase));

    /// <summary>Roads:LifetimeMinutes ("data" = null: tunnelexitsettings, 720-1440): a link's lifetime; (min, max) minutes overrides it (tests, events).</summary>
    public (double Min, double Max)? LifetimeMinutes { get; init; }

    /// <summary>Roads:Charges ("data" = empty): overrides of an exit type's maxcharges, charge-up and recently-used seconds, by type name.</summary>
    public IReadOnlyDictionary<string, RoadChargeOverride> Charges { get; init; } = DataCharges;

    /// <summary>The empty Roads:Charges (one instance: a default config equals a loaded one).</summary>
    public static readonly IReadOnlyDictionary<string, RoadChargeOverride> DataCharges = new Dictionary<string, RoadChargeOverride>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Roads:EnterRadius ("data" = null: gamedata ExitSettings defaultexitusagedistance, 5 m, the client's own range): the metres about NewTunnelExit
    /// key 1 a player may use a portal from, plus Transfer:PositionTolerance.
    /// </summary>
    public double? EnterRadius { get; init; }

    /// <summary>Roads:Rescue (true) and Roads:RescueMinutes (60): a road with a human inside and no link gets one at once, this long (SPEC §5.2).</summary>
    public bool Rescue { get; init; } = true;

    public double RescueMinutes { get; init; } = 60;

    /// <summary>Roads:UpdateSeconds (1): the network's tick (expiries, charge-ups); Roads:RetrySeconds (60): a searching exit's next try for a partner.</summary>
    public double UpdateSeconds { get; init; } = 1;

    public double RetrySeconds { get; init; } = 60;

    /// <summary>Roads:Seed (null = random): the network's random numbers (the self-test fixes them).</summary>
    public int? Seed { get; init; }

    /// <summary>Roads:Command (official Gm, pve All): who may use .roads and .roads route (the other subcommands stay a GM's).</summary>
    public RoadsCommandAccess Command { get; init; } = RoadsCommandAccess.Gm;

    /// <summary>Roads:Chests (true): the road camps' chests (LootChestSpawnpointChestUpgrade) and their guards at the chest's level (SPEC §5.5).</summary>
    public bool Chests { get; init; } = true;

    /// <summary>
    /// Roads:FixedDungeons (true, phase B, SPEC §7.1): the road camps' fixed dungeon exits (ROADS_FIXED_EXIT_* tiles, randomdungeons.xml
    /// &lt;FixedExits&gt; *_ROAD_SOLO / _VETERAN / _ELITE) stand on the road maps, Dungeons:FixedExits or not.
    /// </summary>
    public bool FixedDungeons { get; init; } = true;

    /// <summary>
    /// Roads:LogoutSeconds (official 900, the road cluster types' logoutseconds; pve "world" = null: World:LogoutSeconds): the server's logout
    /// time in a road (phase B, SPEC §5.1). The client counts the cluster type's 900 s itself with the "@OVERHEADMESSAGE_ROADS_LOGOUT_NOT_POSSIBLE"
    /// override; a shorter server time ends the session earlier and the client goes back to the login screen.
    /// </summary>
    public double? LogoutSeconds { get; init; } = 900;

    /// <summary>
    /// Roads:ThrowOutAfterMinutes (official 60, the road types' graceperiodthreshold 3600 s; pve 0 = never; phase B, SPEC §5.1, Q6 [inference]):
    /// a character that logs in to a road it left this long ago is thrown out: it comes back in its respawn city with gamedata's
    /// ItemLossEvent ThrownOutAvalonRoads (20-40 % durability, a third of each stack destroyed; equipment, weapons and mounts kept).
    /// </summary>
    public double ThrowOutAfterMinutes { get; init; } = 60;

    /// <summary>
    /// Roads:TempPortalSeconds (5; 0 = off; phase B, SPEC §7.5 [inference]): when an open-world portal closes in a player's view, the short-lived
    /// portal visual NewTunnelExitTemp (524, tile Tunnel_Exit_Temp) stands on its spot this long.
    /// </summary>
    public double TempPortalSeconds { get; init; } = 5;
}

/// <summary>Roads:Charges:&lt;type&gt;: an exit type's numbers over the data's; null = the data's.</summary>
public sealed record RoadChargeOverride
{
    public int? MaxCharges { get; init; }
    public double? ChargeSeconds { get; init; }
    public double? UsedSeconds { get; init; }
}

/// <summary>Roads:Command: who may use .roads.</summary>
public enum RoadsCommandAccess
{
    /// <summary>Every account level (the pve preset: no community scouting maps on a local server).</summary>
    All,

    /// <summary>Game masters only (the official preset).</summary>
    Gm,
}
