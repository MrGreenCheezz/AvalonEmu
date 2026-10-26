using System.Numerics;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// A joined character in the world (TC Player): created by Join and put on the map of its cluster, taken off
/// when the connection ends, so it lives exactly one game connection (ARCHITECTURE.md §4.2). Until step 6 this
/// state was WorldSession's PlayerState and LastMove. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Player(WorldSession session, World world, long id, Character character, Inventory inventory, Progression? progression) : IUnit
{
    private TimeSpan _playedAt = world.Clock.Now;
    private bool _playing = true;

    internal TimeSpan? TutorialInsideSince { get; set; }
    internal string? TutorialGatherProgress { get; set; }

    /// <summary>Settle only elapsed online time; repeated save/disconnect snapshots cannot count it twice.</summary>
    public void StorePlayedTime()
    {
        if (!_playing) return;
        var elapsed = (World.Clock.Now - _playedAt).Ticks / TimeSpan.TicksPerMillisecond;
        if (elapsed <= 0) return;
        Character.PlayedMilliseconds = checked(Character.PlayedMilliseconds + elapsed);
        _playedAt += TimeSpan.FromMilliseconds(elapsed);
    }

    internal void StopPlayedTime()
    {
        StorePlayedTime();
        _playing = false;
    }

    /// <summary>Object id of the player (Join key 0); new for every Join.</summary>
    public long Id { get; } = id;

    public WorldSession Session { get; } = session;

    public World World { get; } = world;

    public Character Character { get; } = character;

    /// <summary>
    /// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.2): what makes this player a bot (its role, tier, brain, walk); null for a human.
    /// A bot is an ordinary player on a <see cref="Network.BotPeer"/> session with a transient character, kept in <see cref="Map.Bots"/>.
    /// </summary>
    public BotInfo? Bot { get; init; }

    /// <summary>Whether this player is a bot (<see cref="Bot"/>).</summary>
    public bool IsBot => Bot is not null;

    /// <summary>StageN companions: a bot a player hired (<see cref="BotRole.Companion"/>): a party member that fights mobs, never players.</summary>
    public bool IsCompanion => Bot is { Role: BotRole.Companion };

    /// <summary>
    /// The item objects created for this Join and the containers they lie in. Join creates the player with empty
    /// containers, sends the world objects of phase A (<see cref="Visibility"/>), then the items, and sets them here.
    /// </summary>
    public Inventory Inventory { get; internal set; } = inventory;

    /// <summary>
    /// Destiny board state as sent: the character's own (<see cref="Character.Progression"/>, the same object, which fame
    /// changes) or the shared unlock-all one; null without game data (nothing is sent then). A GM's .progress mode sets it anew.
    /// </summary>
    public Progression? Progression { get; internal set; } = progression;

    /// <summary>
    /// The defence of the worn items (stageJ armour SPEC §5.3, <see cref="ComputeDefense"/>): set with the bars by
    /// <see cref="RecalculateStats"/> from the same equipment and destiny levels, so health and armour never disagree;
    /// <see cref="DefenseStats.None"/> before the first and without game data.
    /// </summary>
    public DefenseStats Defense { get; private set; } = DefenseStats.None;

    /// <summary>Health as last sent (Join keys 11-15, RegenerationPlayerComboChanged keys 2-6); <see cref="RecalculateStats"/> sets it.</summary>
    public ResourceBar Health;

    /// <summary>Energy (Join keys 16-20, RegenerationPlayerComboChanged keys 7-11).</summary>
    public ResourceBar Energy;

    /// <summary>Health of the mount in the mount slot (Join keys 21-25, RegenerationPlayerComboChanged keys 12-16); all 0 without one.</summary>
    public ResourceBar MountHealth;

    /// <summary>Last Move; null until the player first moves. <see cref="Move"/> sets it with its stamp.</summary>
    public MovementState? Movement { get; set; }

    /// <summary>
    /// Newest movement GameTimeStamp handed out for this player (<see cref="NextMoveStamp"/>); the Join time until the first
    /// Move, which is NewCharacter key 18 of a player that never moved.
    /// </summary>
    public long MoveStamp { get; set; }

    /// <summary>NewCharacter key 20 of a standing player: the last non-zero Move speed, 5.5 before one (captures: 0.7-24, typically 5.5; never 0).</summary>
    public float LastSpeed { get; private set; } = DefaultSpeed;

    /// <summary>The usual walking speed of the captured NewCharacter key 20, for a player who has not moved yet.</summary>
    public const float DefaultSpeed = 5.5f;

    /// <summary>
    /// The direction the player faces, degrees: key 2 of its last Move, until then the Join's key 10. A travel-point
    /// journey keeps it (its Join key 10 is the last Move's direction, capture 002835).
    /// </summary>
    public float Facing { get; set; }

    /// <summary>
    /// The harvest under way (HarvestStart accepted, HarvestFinished not sent yet); null when none. The world tick finishes
    /// it when due (<see cref="Harvesting"/>); it ends with the player's connection, unfinished.
    /// </summary>
    public PendingHarvest? Harvest { get; set; }

    /// <summary>
    /// The fishing session under way (stageG fishing SPEC §4: an accepted FishingStart op until the FishingCancel event); null
    /// when none. Its timers run on the map (<see cref="Systems.Fishing"/>); it ends with the player's connection.
    /// </summary>
    public FishingSession? Fishing { get; set; }

    /// <summary>
    /// Stage I guild goals: the client season index the GvgSeasonUpdate (351) after this player's Join named; null = none sent (no client
    /// season, or Guild:Enabled off). The level popup (GameEvent 162 type 40) goes only to a player whose client knows that season.
    /// </summary>
    public int? GvgSeasonSent { get; set; }

    /// <summary>
    /// Its FlaggingStatus (stageE pvp SPEC §0: 0 Blue, 1-6 a faction, 255 Red; Join key 49, NewCharacter key 53,
    /// ChangeFlaggingFinished 364): who may attack it (<see cref="Systems.Pvp.Of(Player, Player)"/>). The character's (saved), changed by
    /// the flag cast of op 354 (<see cref="Systems.Flagging"/>) and a GM's .pvp flag.
    /// </summary>
    public byte Flag { get => Character.Flag; internal set => Character.Flag = value; }

    /// <summary>The faction it is enlisted to (a FlaggingStatus; Join key 106, NewCharacter key 56); none (0) until a GM's .pvp faction.</summary>
    public byte Faction { get => Character.Faction; internal set => Character.Faction = value; }

    /// <summary>Its open-world PvP state (stageE pvp SPEC §3): the flag cast under way, the crimes it committed and its implication.</summary>
    public PlayerPvp PvpState { get; } = new();

    /// <summary>Its fight (stage 5b/5c): combat state, enemies, the swing, the cast under way and the cooldowns.</summary>
    public PlayerCombat Combat { get; } = new();

    /// <summary>Its active spells (stageD effects SPEC §1): buffs, crowd control, heals over time. Not saved (lives with the connection).</summary>
    public Systems.EffectList Effects => _effects ??= new() { Owner = this, PassiveBuffs = () => Systems.Passives.Of(this).Buffs };

    /// <summary>The main-hand item's object id at the last stats change (the plugins' OnWeaponSwap); -1 before the first.</summary>
    internal long PluginMainHand { get; set; } = -1;

    internal string? PluginMainHandName { get; set; }

    private Systems.EffectList? _effects;

    /// <summary>Its mount (stageD mounts SPEC §3, <see cref="Systems.Mounts"/>): the state, the mount under way, its mount object.</summary>
    public PlayerMount Mount { get; } = new();

    /// <summary>StageN qol (SPEC §1): its looping emote under way (dance, sit, sleep), shown to every new observer; null = none. A bot's is <see cref="BotInfo.Emote"/>.</summary>
    public BotEmote? Emote { get; set; }

    /// <summary>StageN qol (SPEC §4): the world clock time of its last minimap ping (one per <see cref="Systems.Parties.PingInterval"/>).</summary>
    public TimeSpan? LastPing { get; set; }

    /// <summary>StageN qol (SPEC §3): when (world clock) its accepted /stuck moves it; null = none under way.</summary>
    public TimeSpan? StuckDue { get; set; }

    /// <summary>StageN qol: the GameTimeStamp of its last emote (emotes.xml cooldown_ms between two).</summary>
    public long LastEmoteAt { get; set; }

    /// <summary>On its mount (Mounted sent, not dismounted since).</summary>
    public bool IsMounted => Mount.State == MountState.Mounted;

    /// <summary>
    /// The mount bar's regeneration from the mount's <paramref name="regen"/>: 0 in combat, mounted or not (stageD mounts SPEC §4.5,
    /// combo 96 without key 15; §4.6: a dead mount's bar refills out of combat only), else the mount's.
    /// </summary>
    public float MountRegeneration(float regen) => Combat.InCombat ? 0 : regen;

    /// <summary>An event about the player to its own client and to its <see cref="Observers"/>.</summary>
    public void SendAround(AlbionEvent e)
    {
        Session.Send(e);
        SendToObservers(e);
    }

    /// <summary>The loot bag whose container its client has open (RegisterToObject, stage 5c); null when none.</summary>
    public LootObject? OpenBag { get; internal set; }

    /// <summary>
    /// The object its client registered to last (RegisterToObject: a bank, a marketplace, a loot bag; stage 6b), which
    /// ContainerOpen requires; null = none.
    /// </summary>
    public long? RegisteredTo { get; internal set; }

    /// <summary>The bank vault tabs its client has open (ContainerOpen, stage 6b), by container Guid; saved with <see cref="SaveItems"/>.</summary>
    public Dictionary<Guid, Vault> OpenVaults { get; } = [];

    /// <summary>One of the player's containers by Guid: equipment, inventory or an open vault tab; null = none of them.</summary>
    public Container? FindContainer(Guid id) => Inventory.GetContainer(id) ?? (OpenVaults.TryGetValue(id, out var v) ? v.Container : null);

    /// <summary>The station action under way (op 55: a repair, a craft batch; stage 6a/6c); null when none.</summary>
    public BuildingAction? Action { get; internal set; }

    /// <summary>
    /// The fractions of returned crafting resources not handed out yet, per resource uniquename (stage 6 SPEC §6c: floor, then the
    /// remainder carried, so the totals stay right over many crafts). Lives with the connection.
    /// </summary>
    public Dictionary<string, double> ReturnCarry { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The GM's premium of this player (.premium on|off, <see cref="Systems.Premium.Set"/>) over Premium:Mode; null = the mode's. Lives with
    /// the connection (not saved): the next Join is the mode's again.
    /// </summary>
    public bool? PremiumOverride { get; internal set; }

    /// <summary>The premium end the client was last told (Join key 90, PremiumChanged key 1), UTC ticks; 0 = none (no premium).</summary>
    public long PremiumEnd { get; internal set; }

    /// <summary>The world objects and other players its client knows (SPEC stage 2 §5, §6; stage 7 SPEC §3).</summary>
    public VisibilityTracker Visibility => _visibility ??= new VisibilityTracker(this);

    /// <summary>
    /// The players whose clients know this one (got its NewCharacter and no Leave since). Visibility between players is
    /// symmetric (one pair test, stage 7 SPEC §3), so these are exactly the players this one's client knows.
    /// </summary>
    public IReadOnlyCollection<Player> Observers => Visibility.KnownPlayers;

    /// <summary>
    /// An event about this player that others see (stage 7 SPEC §4: same keys for the owner and the others) to its
    /// <see cref="Observers"/>; the owner's own copy goes out by its session as before.
    /// </summary>
    public void SendToObservers(AlbionEvent e)
    {
        foreach (var o in Observers) o.Session.Send(e);
    }

    /// <summary>
    /// The GameTimeStamp of a new movement sample (stage 7 SPEC §2.2): the server's time now, but never earlier than the
    /// last one handed out, so the samples of this player never go back for any observer (a5m.b 0x180918c50 drops an older
    /// one) and the first event 3 after a NewCharacter is never older than its key 18.
    /// </summary>
    public long NextMoveStamp() => MoveStamp = Math.Max(GameTime.Now, MoveStamp);

    /// <summary>
    /// Takes a Move (or a server-made stop) as the player's movement, with a new stamp, and relays it to the observers as
    /// event 3 (<see cref="RelayMovement"/>). <paramref name="cs2"/> = Move key 5.
    /// </summary>
    public void Move(Vector2 from, Vector2 target, float speed, float direction, long cs2, DateTime now)
    {
        Movement = new MovementState(from, target, speed, now) { Direction = direction, Stamp = NextMoveStamp(), Cs2 = cs2 };
        if (speed > 0) LastSpeed = speed;
        Facing = direction;
        RelayMovement();
    }

    /// <summary>
    /// The current movement sample to every observer, never to the player itself: event 3 {0: id, 1: bytes}
    /// (<see cref="MovementCodec.EncodeSample"/>), unreliable on channel 0 as the official server sends 96 % of them; each
    /// observer's client dead-reckons it toward its target for up to 1 s (a5k.a 0x1809163f0), and the client sends a Move
    /// about every 100 ms while walking, so relaying every Move at once needs no resend.
    /// </summary>
    public void RelayMovement()
    {
        if (Movement is not { } m || Observers.Count == 0) return;
        var ev = MovementCodec.Event(Id, MovementCodec.EncodeSample(m.Stamp, m.From, m.Direction, m.Speed, m.Target, m.Cs2));
        foreach (var o in Observers) o.Session.SendMovement(ev);
    }

    /// <summary>
    /// A server teleport within the map to <paramref name="to"/> (GM .go; later death and the like): the player stands
    /// there with a fresh stamp, and the observers get Teleport (event 4, az0: 0 id, 1 ts, 2 pos, 3 direction, 4 speed,
    /// 5 target = pos), which resets their history of it (csh.aft → a5m.b(az0) 0x180918850) instead of animating a jump.
    /// The player's own client is moved by the caller (a MoveResponse). Then its visibility follows the new cell.
    /// </summary>
    public void Teleport(Vector2 to, float direction, DateTime now)
    {
        Movement = new MovementState(to, to, 0, now) { Direction = direction, Stamp = NextMoveStamp() };
        Facing = direction;
        Character.PositionX = to.X;
        Character.PositionY = to.Y;
        SendToObservers(new TeleportEvent { ddn = Id, csx = Movement.Stamp, csy = to, csz = direction, cs0 = LastSpeed, cs1 = to });
        UpdateVisibility(now);
    }

    /// <summary>
    /// NewCharacter (event 29, client class atd) for another player's client (stage 7 SPEC §1): the handler cjb.d_t462
    /// 0x180aacaf0 builds the remote character (cp0) from keys 0, 1, 4, 7 when the id is new and applies the event with
    /// csc.v 0x180becdc0 either way, so a repeated one is a full update. Never sent to the player it describes, and never
    /// through FillEmpty: keys 16/17 (the obfuscated position, decoded only when 16 is non-null) must stay absent, and so
    /// they are, null fields are not written. Keys:
    /// 0 id; 1 name; 2 avatar; 3 avatar ring (default 0); 4 body template (characters.xml index); 5/6 saved customization and
    /// clothes when both arrays are present and have equal length (the client checks its template); 7 character Guid;
    /// 14/15/18/19/20 the movement sample (<see cref="MovementSample"/>); 22-26 health, 27-31 energy (24/29 the regeneration
    /// cap, 0 = 100 % left out); 32-36 the mount bar only with a mount in slot 7; 40 equipment type ids per EquipmentSlot,
    /// which alone dresses the remote model (no NewEquipmentItem for others); 43 the 14 active spell slots, required
    /// (cp0.x8_1 0x180bdab90 throws on null); 55 seven -1 (cosmetic slots, null-safe). StageE (pvp SPEC §0, §3.4, §5): 37 the
    /// reputation points (left out at 0), 38 now, 39 their level (NEUTRAL 4 at 0 points), 53 the flag and 56 the enlisted faction
    /// (both left out at 0). StageH guilds (SPEC §2.7): 8 the guild name, 9 its Guid, 44-50 the crest and 51 "" for a member
    /// (<see cref="Systems.Guilds.Apply(NewCharacterEvent, Guid)"/>); left out without a guild. Avatar keys are left out (read null-safe), but a bot's avatar (key 2, stage I).
    /// </summary>
    public NewCharacterEvent BuildCreate(DateTime now)
    {
        var c = Character;
        var (position, target, stamp, direction, speed) = MovementSample(now);
        // Creation supplies the arrays for the selected body, independently of the avatar id.
        // Preserve them and let the client's body template validate its customization count.
        var look = c.Customization.Length > 0 && c.Customization.Length == c.Clothes.Length;
        ResourceBar hp = Health, en = Energy, mount = MountHealth;
        var e = new NewCharacterEvent
        {
            cvx = Id, cvy = c.Name ?? "", cvz = IsBot ? 0 : c.Template,
            cv1 = IsBot ? c.Template : c.Appearance,
            cv2 = look ? c.Customization : null, cv3 = look ? c.Clothes : null, cv4 = c.Id,
            cwb = position, cwc = target, cwf = stamp, cwg = direction, cwh = speed,                        // [14] [15] [18] [19] [20]
            cwj = hp.Value, cwk = hp.Max, cwl = hp.RegenCap, cwm = hp.RegenPerSecond, cwn = hp.Stamp,        // [22-26]
            cwo = en.Value, cwp = en.Max, cwq = en.RegenCap, cwr = en.RegenPerSecond, cws = en.Stamp,        // [27-31]
            cw1 = Inventory.EquipmentTypeIds(),                                                              // [40] int[10]
            cw4 = Session.SpellSlots(Inventory),                                                             // [43] int[14]
            cxg = Systems.Vanity.Wardrobe(c),                                                                // [55] int[7] the wardrobe (stageN vanity)
            cwy = (float)Character.Reputation, cwz = World.GameNow, cw0 = Systems.Reputation.Level(this),    // [37] [38] [39]
            cxe = Flag, cxh = Faction,                                                                       // [53] [56]
        };
        World.Guilds.Apply(e, c.Id); // 8, 9, 44-51: the guild over the nameplate (stageH guilds SPEC §2.7)
        if (Bot is { Avatar: > 0 } bot) e.cvz = bot.Avatar; // [2] stage I: a bot's avatar (index into avatars.xml; a human's is not kept yet)
        if (Inventory.Equipment[MountSlot] is not null)
        {
            e.cwt = mount.Value; e.cwu = mount.Max; e.cwv = mount.RegenCap; e.cww = mount.RegenPerSecond; e.cwx = mount.Stamp; // [32-36]
        }
        return e;
    }

    /// <summary>EquipmentSlot.Mount.</summary>
    private const int MountSlot = 7;

    /// <summary>
    /// NewCharacter keys 14, 15, 18, 19, 20 (stage 7 SPEC §2.2): the stored sample, not an extrapolated one (the client
    /// dead-reckons from it): position at the stamp (From), target, stamp, facing, speed. A player whose walk has ended is
    /// sent standing at its target with the stamp kept; one that never moved stands where it joined with its Join stamp.
    /// The speed is never 0 (<see cref="LastSpeed"/>).
    /// </summary>
    public (Vector2 Position, Vector2 Target, long Stamp, float Direction, float Speed) MovementSample(DateTime now)
    {
        if (Movement is not { } m)
        {
            var at = new Vector2(Character.PositionX, Character.PositionY);
            return (at, at, MoveStamp, Facing, LastSpeed);
        }
        var speed = m.Speed > 0 ? m.Speed : LastSpeed;
        return m.PositionAt(now) == m.Target ? (m.Target, m.Target, m.Stamp, m.Direction, speed) : (m.From, m.Target, m.Stamp, m.Direction, speed);
    }

    private VisibilityTracker? _visibility;

    /// <summary>Where the player is at <paramref name="now"/>: its last Move extrapolated, else where it joined (the character's position).</summary>
    public Vector2 PositionAt(DateTime now) => Movement?.PositionAt(now) ?? new Vector2(Character.PositionX, Character.PositionY);

    /// <summary>Recomputes what the client sees at the player's position now (a Move; the world tick while it walks); only a new cell costs anything.</summary>
    public void UpdateVisibility(DateTime now) => Visibility.Update(PositionAt(now));

    /// <summary>Writes where the player is at <paramref name="now"/> (the last Move extrapolated) into its character, for saving.</summary>
    public void StorePosition(DateTime now)
    {
        StorePlayedTime();
        if (Movement is not { } move) return;
        var p = move.PositionAt(now);
        Character.PositionX = p.X;
        Character.PositionY = p.Y;
    }

    /// <summary>The map the player is on; null once the connection ended.</summary>
    public Map? Map { get; internal set; }

    /// <summary>
    /// Writes the items as they lie now into the character (<see cref="Inventory.SaveTo"/>, spells by uniquename) and
    /// saves it <see cref="Persistence.SaveScheduler.ChangeDelay"/> later. The item handlers call it after each change,
    /// after the scripts they call; after the scripts of a Join and OnLogout the game calls it when a script ran. A
    /// script that changes items anywhere else calls it itself.
    /// </summary>
    public void SaveItems()
    {
        Inventory.SaveTo(Character, id => World.Spells?.Name(id));
        foreach (var vault in OpenVaults.Values) Character.Vaults[vault.Key] = Inventory.Records(vault.Container, id => World.Spells?.Name(id));
        World.Saves.MarkChanged(Character);
    }

    /// <summary>
    /// Maxima and regeneration for the current equipment and destiny levels (<see cref="StatCalculator"/>; out of combat)
    /// and the buffs in force (stageD effects SPEC §3.1: (value + Σ hitpointsmax) × (1 + Σ hitpointsmaxbonus), the same for the
    /// health regeneration and the energy). Without game data a naked character's values (<see cref="CharacterStats.Naked"/>).
    /// </summary>
    public CharacterStats ComputeStats()
    {
        var s = World.StatCalculator is { } calculator && Progression is { } progression
            ? calculator.Compute(Inventory.EquippedItems(), progression.Levels, ItemPowerCap)
            : CharacterStats.Naked;
        if (Effects.Idle) return s;
        var now = World.GameNow;
        float Buffed(float value, string flat, string bonus) => (float)((value + Effects.Sum(flat, now)) * Math.Max(0, 1 + Effects.Sum(bonus, now)));
        return s with
        {
            MaxHealth = MathF.Round(Buffed(s.MaxHealth, "hitpointsmax", "hitpointsmaxbonus"), MidpointRounding.AwayFromZero),
            HealthRegen = Buffed(s.HealthRegen, "hitpointsregeneration", "hitpointsregenerationbonus"),
            MaxEnergy = MathF.Round(Buffed(s.MaxEnergy, "energymax", "energymaxbonus"), MidpointRounding.AwayFromZero),
            EnergyRegen = Buffed(s.EnergyRegen, "energyregeneration", "energyregenerationbonus"),
        };
    }

    /// <summary>
    /// The worn items' armour, magic and crowd control resistance and defence vs mobs / players (<see cref="StatCalculator.Defense(IReadOnlyList{EquippedItem?}, IReadOnlyDictionary{int, int}, bool)"/>,
    /// scaled unless Combat:GearArmor is false); without destiny levels (no <see cref="Progression"/>) the items at their own item power.
    /// </summary>
    public DefenseStats ComputeDefense() =>
        World.StatCalculator is { } calculator
            ? calculator.Defense(Inventory.EquippedItems(), Progression?.Levels ?? NoLevels, World.Config.GearArmor, ItemPowerCap)
            : DefenseStats.None;

    /// <summary>The item power cap the player's items are under now (an arena match's, Arena:ItemPowerCap); null = none.</summary>
    public ItemPowerCap? ItemPowerCap => Map is not null ? World.Arenas.ItemPowerCapOf(this) : null;

    private static readonly IReadOnlyDictionary<int, int> NoLevels = new Dictionary<int, int>();

    /// <summary>
    /// Sets the bars to <see cref="ComputeStats"/> at GameTimeStamp <paramref name="now"/> (Join: all of them, full).
    /// In combat the health regeneration stays 0 (<see cref="PlayerCombat"/>).
    /// Returns whether the player's own bars changed (maximum or regeneration of health or energy), i.e. whether
    /// RegenerationPlayerComboChanged (96) is due: a mount, bag or food move changes at most the mount bar and sends
    /// none (113433). Known deviation: a change refills all three bars (current = the new maximum, stamp = now; every
    /// captured swap happened at full health), while the official server keeps the current values, the mount's too, and
    /// regenerates them (114814). In combat (mobs hit back since 5d) health and energy keep their current values, clamped
    /// to the new maxima, so a gear swap mid-fight heals nothing; so do they while knocked down (stage 5e), with the knockdown's
    /// regeneration worked out again for the new maximum. <paramref name="keep"/> (a buff came or went, stageD effects SPEC
    /// §3.1): the current values are kept out of combat too. Stage I: a bot's maximum health × Bots:Combat:HealthFactor.
    /// </summary>
    public bool RecalculateStats(long now, bool keep = false)
    {
        World.Plugins.MainHand(this); // the plugins' Items.OnWeaponSwap
        Defense = ComputeDefense();
        var s = ComputeStats();
        // StageN companions: Companions:HealthFactor instead of the world bots' Bots:Combat:HealthFactor.
        var botHealth = Bot is null ? 1 : IsCompanion ? World.Config.Companions.HealthFactor
            : World.Config.Bots.HealthFactor * World.Config.Bots.Difficulty * Bot.HealthFactor; // stageN botpower: Bots:Difficulty
        if (botHealth != 1)
            s = s with { MaxHealth = Math.Max(1, MathF.Round(s.MaxHealth * (float)botHealth, MidpointRounding.AwayFromZero)) };
        // No health regeneration in combat (stage 5b, SPEC §5b.5); knocked down (5e) the knockdown's own rate for the new maximum.
        var healthRegen = Combat.Down is not null ? Systems.Knockdown.Regeneration(this, s.MaxHealth) : Combat.InCombat ? 0 : s.HealthRegen;
        var changed = Health.Max != s.MaxHealth || Health.RegenPerSecond != healthRegen
            || Energy.Max != s.MaxEnergy || Energy.RegenPerSecond != s.EnergyRegen;
        static void Keep(ref ResourceBar bar, float max, float regen, long now)
        {
            var value = bar.At(now);
            bar.Max = max;
            bar.RegenPerSecond = regen;
            bar.Set(Math.Min(value, max), Math.Max(now, bar.Stamp));
        }
        if (changed && (keep || Combat.InCombat || Combat.Down is not null))
        {
            Keep(ref Health, s.MaxHealth, healthRegen, now);
            Keep(ref Energy, s.MaxEnergy, s.EnergyRegen, now);
        }
        else if (changed)
        {
            Health.Fill(s.MaxHealth, healthRegen, now);
            Energy.Fill(s.MaxEnergy, s.EnergyRegen, now);
        }
        // The mount bar (stageD mounts SPEC §4.5): refilled with the others out of combat (the known deviation above) and for another
        // mount; a buff that comes or goes, a fight or the knockdown keep its value (its regeneration 0 in combat).
        var mountRegen = MountRegeneration(s.MountHealthRegen);
        var mountChanged = MountHealth.Max != s.MountMaxHealth;
        if ((changed && !keep && !Combat.InCombat && Combat.Down is null) || (mountChanged && !keep && !Combat.InCombat))
            MountHealth.Fill(s.MountMaxHealth, mountRegen, now);
        else if (mountChanged || MountHealth.RegenPerSecond != mountRegen)
            Keep(ref MountHealth, s.MountMaxHealth, mountRegen, now);
        return changed;
    }
}
