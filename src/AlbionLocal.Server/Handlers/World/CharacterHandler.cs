using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>Entering the world (Join, with the character's items and bars) and leaving it (logout).</summary>
public sealed partial class WorldSession
{
    /// <summary>
    /// First operation of a game connection (captures: one Join per connection, also after each cluster
    /// change); the session is InWorld afterwards and its player is on the map of the character's cluster.
    /// Which character (<see cref="JoinCharacter"/>): key 0 is the account for the real client, so the one its login
    /// selected. After a cluster change the character is already saved in the target cluster at the arrival point
    /// (<see cref="Commit"/>); the Join only clears its <see cref="PendingTransfer"/> (SPEC stage 3 §4.5).
    /// </summary>
    /// <summary>
    /// The ReturnCode of a refused Join (no character of the account to play): the one SelectCharacter answers an unknown
    /// character with (0x231). What the client shows for it was not tried; it gets no world, as with any refused Join.
    /// </summary>
    public const short JoinRefused = 0x231;

    [Handles(OperationCodes.Join)]
    private AlbionResponse Join(JoinRequest req)
    {
        if (JoinCharacter(req.ss) is not ({ } c, var account)) return new GenericResponse { ReturnCode = JoinRefused };
        Character = c;
        // StageG dungeons (SPEC §9 step 4): a random dungeon that is gone (closed, or a restart) sends the character back where it
        // came in. The client loaded that id, so it reports a wrong cluster and goes to the login; the next login is outside.
        if (world.Dungeons.ReturnIfClosed(c))
            Log.Warn(Name, $"join into a dungeon that is gone: '{c.Name}' is put back in {c.Cluster}; the client will log out to the character selection");
        if (world.Expeditions.ReturnIfClosed(c)) // stageH: the same for an expedition instance that is gone (SPEC §7)
            Log.Warn(Name, $"join into an expedition that is gone: '{c.Name}' is put back in {c.Cluster}; the client will log out to the character selection");
        if (world.Hellgates.ReturnIfClosed(c)) // stageL: the same for a hellgate that is gone (hellgates SPEC §5.7)
            Log.Warn(Name, $"join into a hellgate that is gone: '{c.Name}' is put back in {c.Cluster}; the client will log out to the character selection");
        if (world.Arenas.ReturnIfClosed(c)) // stage N: the same for an arena match that is gone (arena SPEC §4.2)
            Log.Warn(Name, $"join into an arena that is gone: '{c.Name}' is put back in {c.Cluster}; the client will log out to the character selection");
        if (world.Corrupted.ReturnIfClosed(c)) // stageL: the same for a corrupted dungeon that is gone (SPEC §4.2 step 9)
            Log.Warn(Name, $"join into a corrupted dungeon that is gone: '{c.Name}' is put back in {c.Cluster}; the client will log out to the character selection");
        if (world.Mists.ReturnIfClosed(c)) // stageL: the same for a mists zone that is gone (mists SPEC §6.6)
            Log.Warn(Name, $"join into a mists zone that is gone: '{c.Name}' is put back in {c.Cluster}; the client will log out to the character selection");
        if (world.Roads.ThrowOutIfStale(c, atJoin: true)) // stageL roads phase B: offline in a road too long (Roads:ThrowOutAfterMinutes), a Join without a SelectCharacter
            Log.Warn(Name, $"join into a road left too long ago: '{c.Name}' is thrown out to {c.Cluster}; the client will log out to the character selection");
        // One of the account's open connections now: a newer Login of the account closes it (LoginSession.Login).
        if (_account is { } before && before != account) world.Accounts.Detach(before, this);
        _account = account;
        world.Accounts.Attach(account, this);
        // The selection is per account (AccountStates), not per connection: one login name on two clients at once would make
        // each Join play the character the other one selected last. Not refused (a reconnect looks the same), only told; a Join
        // by character id (the self-test's) plays that character and is not affected.
        if (req.ss == account && world.Maps.Maps.SelectMany(m => m.Players.Values).FirstOrDefault(p => p.Character.Account == account && p.Character != c) is { } busy)
            Log.Warn(Name, $"join: the account already plays '{busy.Character.Name}' on another connection; use one login name per client at a time");
        var objectId = world.ObjectIds.Next();
        // Clock anchor: GameTimeStamp = UTC DateTime ticks. ci7.e_25 passes keys 68/69 to the clock
        // (ci5.x1); cjd.c then returns anchor + (Photon server ms - anchor ms) * 10000. Without it
        // the client's game time is the bare Photon clock, i.e. a DateTime in year 1.
        var nowTicks = DateTime.UtcNow.Ticks;
        var nowMs = PhotonClock.ServerTimeMs;
        Log.Info(Name, $"join '{c.Name}' ({c.Id}) as object {objectId} in cluster {c.Cluster}");
        // Join key 10, the facing: the arrival's while a transfer into this cluster is pending (SPEC stage 3 §2.5, §4.5),
        // else none (0, not sent), as at every login before step 13; the facing at logout is not kept.
        var facing = 0f;
        var arriving = c.PendingTransfer is not null;
        _respawned = c.PendingTransfer is { Via: Systems.Death.RespawnVia } respawn && respawn.To == c.Cluster;
        if (c.PendingTransfer is { } transfer)
        {
            // Cluster and position are the arrival's since the commit; a transfer to another cluster than the
            // character's (never the case so far) is stale and only cleared.
            if (transfer.To == c.Cluster)
            {
                facing = transfer.Direction;
                Log.Info(Name, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"'{c.Name}' arrives from {transfer.From} ({transfer.Via}) at ({c.PositionX:0.###}, {c.PositionY:0.###}) facing {facing:0.####}"));
            }
            c.PendingTransfer = null;
            world.Saves.MarkChanged(c);
        }
        var (loginAt, joins) = world.Accounts.Joined(account);
        // A cluster-change arrival for the party (Parties.Joined): not the first Join after a Login, which is a fresh client
        // process even with a PendingTransfer left by a change it never finished (closed during the load).
        _arrived = arriving && joins > 1;
        var progression = ProgressionOf(c);
        // A second Join on one connection (never captured): the old player leaves first, so that what its OnLogout
        // changes in the items is in the character the new item objects are made from. The scripts of the new one run
        // after JoinFinished (AfterResponse).
        if (Player is { } old) Leave(old, clusterChange: false);
        // A reconnect: the player of the old connection (up to its 30 s timeout) shares the
        // live character but has an inventory of its own. A harvest it had under way would finish into that one and save
        // it over the character (FinishHarvest, Player.SaveItems), so it ends here, without an event: its client is gone.
        foreach (var other in world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => p.Character == c && p.Harvest is not null).ToList())
            other.Session.CancelHarvest(other, "its character joined again on another connection", tellClient: false);
        // Then that player leaves (stageE E3): two live copies with inventories of their own would duplicate the items a lethal
        // death drops from one of them, and its end would run the death's Departed over the new one. Its session is detached
        // (ClusterHandler.DetachDuplicate), so its timeout saves nothing and keeps the character in its party.
        foreach (var other in world.Maps.Maps.SelectMany(m => m.Players.Values).Where(p => p.Character == c && p.Session != this).ToList())
            other.Session.DetachDuplicate(other, "whose character joined again on another one");
        // Phase A (SPEC stage 2 §6, all 15 captured Joins): the world objects in view go out before the items. The map
        // has them when its cluster is resolved (World.PrefetchCluster at SelectCharacter, or an earlier Join); else the
        // first Join into it starts reading the client data in the background and the objects follow when it is in.
        var map = world.Maps.Get(c.Cluster);
        world.LoadTemplate(map);
        // Join key 118: the last PLAYERCITY_* cluster (world.xml type; the bank and market interiors count, captured "1001"
        // in the Lymhurst bank); known when the cluster is resolved, as it normally is by the Join (prefetched at
        // SelectCharacter, resolved at the cluster change).
        if (map.Template?.Info.Type.StartsWith("PLAYERCITY_", StringComparison.Ordinal) == true && c.LastCity != c.Cluster)
        {
            c.LastCity = c.Cluster;
            world.Saves.MarkDirty(c);
        }
        // Stage 6d: the silver of market sales made while the character was away (IMarketStore credits), saved at once.
        if (world.Market?.TakeCredits(c.Id) is > 0 and var credits)
        {
            c.Silver = checked(c.Silver + credits);
            world.Saves.SaveNow(c);
            Log.Info(Name, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"'{c.Name}' gets {credits / 10000.0:0.####} silver of market sales made while away"));
        }
        _joinedAt = world.Clock.Now;
        var player = new Player(this, world, objectId, c, new Inventory(c.EquipmentContainer, c.InventoryContainer), progression)
        {
            Facing = facing,
            MoveStamp = nowTicks, // NewCharacter key 18 until the first Move (stage 7 SPEC §2.2)
        };
        // StageE (pvp SPEC §3.1, §3.4): Red is Blue again on joining a blue cluster under Official, anywhere under Off; cities keep it
        // (Flagging.Arrive, its rules known once the cluster is resolved),
        // and the daily recovery of a negative reputation since the last Join; both before anyone gets this player's NewCharacter.
        if (map.Template is not null) Systems.Flagging.Arrive(player, Systems.Pvp.RulesOf(world, map));
        Systems.Reputation.Recover(player, DateTime.UtcNow);
        player.Visibility.Join(map, new Vector2(c.PositionX, c.PositionY));
        if (map.Template is not null)
        {
            var trees = player.Visibility.Known.Count(o => o is Harvestable);
            Log.Info(Name, $"'{c.Name}' sees {player.Visibility.Known.Count - trees} world objects, {trees} harvestables and {map.ClusterWide.Count} cluster-wide");
        }
        else if (map.TemplateRequested) Log.Info(Name, $"cluster {c.Cluster} is still loading: its world objects follow when it is in");
        player.Inventory = SendItems(c, progression);
        Player = player;
        world.Maps.Add(player);
        world.Dungeons.Joined(c.Cluster, c.Id); // stageG: a Join resets a dungeon's empty lifetime (SPEC §9)
        var inventory = player.Inventory;
        Systems.Consumables.Restore(player); // its saved buffs count in its bars (stageD consumables SPEC §6: Join key 14 with the food)
        Systems.Mounts.Restore(player); // mounted when it left: mounted again, its mount buffs on (stageD mounts SPEC §4.8)
        Systems.Pvp.RestoreImplication(player, world.GameNow); // a saved OUTLAW still implicates it (stageE pvp SPEC §3.4)
        player.RecalculateStats(nowTicks);
        // The players phase A found get this one's NewCharacter now that it has items and bars (stage 7 SPEC §3).
        player.Visibility.AnnounceSelf();
        if (player.Observers.Count > 0) Log.Info(Name, $"'{c.Name}' sees {player.Observers.Count} other player(s)");
        Status = SessionStatus.InWorld;
        StartKeepalive();
        ResourceBar hp = player.Health, en = player.Energy, mount = player.MountHealth;
        var r = new JoinResponse
        {
            a8a = objectId,   // [0] long  local player object id
            a8b = c.Id,       // [1] Guid  character id
            a8c = c.Name,     // [2] string name
            a8i = c.Cluster,  // [8] must equal the cluster the client loaded (ci7.e_25: "wrong cluster map loaded on join")
            a8j = new Vector2(c.PositionX, c.PositionY), // [9] spawn position (cx2.ai7 → player vfunc +0x278, same setter as MoveResponse)
            a8k = facing,     // [10] facing in degrees (cx2.ai7, with key 9): the arrival's after a cluster change (SPEC stage 3 §3), else 0
            // Resource bars, 5 keys each (cur, max, regenCap, regen/s, stamp), applied in cx2.ai7 via a44.n(cur, max,
            // regenCap) and a44.n(stamp, regenPerSecond, cur). regenCap 0 = 100% (not sent, as official).
            a8l = hp.Value, a8m = hp.Max, a8n = hp.RegenCap, a8o = hp.RegenPerSecond, a8p = hp.Stamp, // [11-15] health (cpo.ac3 @0x88)
            a8q = en.Value, a8r = en.Max, a8s = en.RegenCap, a8t = en.RegenPerSecond, a8u = en.Stamp, // [16-20] energy (cpo.ac4 @0x90)
            // [21-25] mount health (cpx.ahz @0x230); zero values are not sent, as the official server
            // leaves them out without mount health (the stamp too).
            a8v = mount.Value, a8w = mount.Max, a8x = mount.RegenCap, a8y = mount.RegenPerSecond,
            a8z = mount.Max > 0 ? mount.Stamp : 0,
            a87 = c.Silver,   // [33] silver, a7m raw (stage 6 SPEC §0; ctp+0x2c8)
            a88 = c.Gold,     // [34] gold, a7m raw (ctp+0x2d0); keys 83-86 (the city price factor) are left out: factor 1 (SPEC §0.1)
            a96 = nowTicks,   // [68] clock anchor GameTimeStamp (UTC ticks)
            a97 = nowMs,      // [69] Photon server time (ms) at the anchor
            // Item containers (cx2.ai7): equipment via ctp.af (10 EquipmentSlot slots), inventory via
            // ctp.x8 (48 slots, as on the official server). c15.m stores the Guid and fills slot i
            // with the item object ids[i] created above (0 = empty). Only a container that got an id
            // array is registered by Guid (c13.g checks the c15+0x28 flag set at the end of c15.m).
            a9p = inventory.Equipment.Id,            // [51]
            a9q = inventory.Equipment.ObjectIds(),   // [52]
            a9s = inventory.Bag.Id,                  // [54]
            a9t = inventory.Bag.ObjectIds(),         // [55]
            // The cluster history (SPEC stage 3 §2.5, as official; cx2.ai7 stores 66 → ctp+0x380, 101 → +0x528, 103 → +0x530,
            // no reader traced): the cluster before ("" until a change), when the character left it (the change, else the
            // last logout), the login's start and this Join's number in it, the last PLAYERCITY_* cluster.
            a94 = c.PreviousCluster,  // [66]
            ba3 = c.LastLeftAt,       // [101] GameTimeStamp
            ba4 = loginAt,            // [102] GameTimeStamp, the same for every Join of a login
            ba5 = joins,              // [103] 1, 2, 3, …
            bbk = c.LastCity,         // [118]
            // StageE (pvp SPEC §0, §3.4): the reputation points, now and their level (4 NEUTRAL at 0), the saved flag and the enlisted faction.
            a9f = (float)c.Reputation,               // [41]
            a9g = nowTicks,                          // [42] GameTimeStamp
            a9i = Systems.Reputation.Level(player),  // [44]
            a9n = player.Flag,                       // [49]
            ba8 = player.Faction,                    // [106]
        }.FillEmpty();
        // Fixed-size arrays the official server always sends (captures: every Join has these
        // lengths); FillEmpty's empty arrays crash readers that index them, e.g. key 43 in
        // ctp.af(ReSpecFameType) 0x180994500 when the Destiny Board opens.
        r.a8g = new int[5];                          // [6]
        r.a8h = new int[5];                          // [7]
        r.a9h = new long[5];                         // [43] a7m per ReSpecFameType (ctp+0x580)
        r.ba7 = new long[6];                         // [105] a7m[6] (ctp.af(a7m[]) → ctp+0x588): the standing by faction (index FlaggingStatus − 1, bcj.a)
        world.Factions.JoinKeys(c, r);               // [104] the currencies by index (faction points, FAVOR), [105] (stageN faction SPEC §2)
        r.bba = Systems.Vanity.Wardrobe(c);          // [108] the wardrobe skin numbers by EquipmentSlot, -1 = none (stageN vanity SPEC §1.4)
        // StageJ focus-lp (SPEC §5.3): premium (90 the end, 91 the days), the focus bar (27, 28, 30, 31; settled now), the learning points
        // (37-40; paid up to now) and the use flags (32 AutoUseActionFocusForCrafting, 99 AutoUseLearningPoints, 100 the respec silver).
        var gameNow = world.GameNow;
        Systems.Premium.JoinKeys(player, r, gameNow);
        Systems.Focus.JoinKeys(player, r, gameNow);
        Systems.LearningPoints.JoinKeys(player, r, gameNow);
        r.a86 = c.UseFlag(1); // [32] (false = left out, as captured after op 298 {0: 1})
        r.ba1 = c.UseFlag(0); // [99]
        r.ba2 = c.UseFlag(2); // [100]
        // Never sent by the official server: leave out instead of empty arrays.
        // [132]/[133] are slot-index sets (c16.d → HashSet<int>), null = none.
        r.bbe = null; r.bbf = null; r.bbg = null; r.bbh = null; r.bbi = null; // [112]-[116]
        r.bby = r.bbz = null;
        // [70] GameTimeStamp time scaling: ci7.e_25 passes it to GameTimeStamp.a(long[]) 0x1808b2b00,
        // which needs exactly 5 values ("GameTimeStamp.SetScaling: Invalid scaling parameters" otherwise).
        // Left out, the client keeps unscaled real time.
        r.a98 = null;
        // Keys 56/58/71-80: the member's guild (stageH guilds SPEC §2.7; left as FillEmpty made them, no guild, otherwise).
        world.Guilds.Apply(r, c.Id);
        // Keys 81/82 on an island (stageH islands SPEC §1.5): its access point and Guid; elsewhere 81 stays "" (left out) and 82 empty.
        world.Islands.JoinKeys(c, r);
        // No key 251: the official Join has none, and the client takes the compressed-time base only
        // from Login (ci7.a9 -> ce.e_1).
        return r;
    }

    /// <summary>
    /// The character a Join with key 0 = <paramref name="key"/> plays (SPEC stage 3 §4.4), the newest state of it
    /// (<see cref="World.LoadCharacter"/>), and the account whose login state (Join keys 102/103) it counts in: a character
    /// id (the self-test's Joins) is that character, in its own account, only with <see cref="ServerConfig.JoinByCharacterId"/>
    /// (the game connection is not tied to a login, so this would let anyone play a character whose Guid it saw); an account id, which the real client always sends
    /// (Login key 51, the same for every Join of its session; one account per login name since stage 7), is the character
    /// its login selected (<see cref="AccountStates"/>) when it is still the account's; an account that has not selected one
    /// since the server started joins its first character, with a warning. An account without characters, or a key that is
    /// neither, joins nothing (null, the Join is refused): every account plays only its own characters, with their own
    /// items, fame and progression, never another account's. The account id is no secret (Login key 51; the local account's
    /// is a constant), so a Join by account also needs the account's login connection, Authed (a successful Login on it;
    /// the client keeps that connection for the whole game session), and an account that is not banned.
    /// </summary>
    private (Character Character, Guid Account)? JoinCharacter(Guid key)
    {
        if (world.Config.JoinByCharacterId && world.LoadCharacter(key) is { } c) return (c, c.Account);
        if (!world.Accounts.Sessions(key).OfType<LoginSession>().Any(s => s.Status == SessionStatus.Authed))
        {
            Log.Warn(Name, $"join refused: key 0 {key} has no logged-in login connection (log in first)");
            return null;
        }
        if (world.Characters.FindAccount(key) is { BannedUntil: { } banned } && banned > DateTime.UtcNow)
        {
            Log.Warn(Name, "join refused: the account is banned");
            return null;
        }
        if (world.Accounts.Selected(key) is { } selected && world.LoadCharacter(selected) is { } chosen && chosen.Account == key)
            return (chosen, key);
        var account = world.Characters.FindAccount(key);
        if (account is not null && world.Characters.List(account.Id).FirstOrDefault() is { } own)
        {
            Log.Warn(Name, $"join: the account has selected no character since the server started; joining the first one, '{own.Name}'");
            return (world.LoadCharacter(own.Id)!, account.Id);
        }
        Log.Warn(Name, account is not null
            ? "join refused: the account has no character (create one at the character selection first)"
            : $"join refused: key 0 {key} is neither a character nor an account");
        return null;
    }

    /// <summary>The character's destiny board state; null without game data (nothing is sent then).</summary>
    private Progression? ProgressionOf(Character c) => world.GameData is null ? null : c.Progression ?? world.UnlockAll;

    /// <summary>
    /// Creates the character's item objects in their containers (<see cref="Inventory.Load"/>: equipment slots first,
    /// then the inventory slots). As the official server does (capture 103829), the items go out before the
    /// Join response, which lists the ids in keys 52/55; c15.m then finds the objects by id. NewEquipmentItem (30) /
    /// NewSimpleItem (32) build the object (cjb.bl → c10.af(id, type, count)). Entries that do not become objects
    /// (unknown name, a kind without its New*Item yet) are kept by the inventory so that saving does not drop them.
    /// </summary>
    private Inventory SendItems(Character c, Progression? progression)
    {
        var usable = SpellUsable(progression);
        Item? Create(InventoryItem saved)
        {
            if (world.Items.Find(saved.Name) is not { } type)
            {
                Log.Warn(Name, $"unknown item '{saved.Name}' in {c.Name}'s container, skipped");
                return null;
            }
            if (!type.CanCreate)
            {
                Log.Warn(Name, $"item '{saved.Name}' in {c.Name}'s container is a {type.Kind} (no New*Item for it yet), skipped");
                return null;
            }
            return CreateItem(type, saved, usable);
        }
        var inventory = Inventory.Load(c, Create);
        Log.Info(Name, $"sent {inventory.Count} items for '{c.Name}'");
        return inventory;
    }

    /// <summary>
    /// Creates one item object from its saved form (a new item: a record of its own) and sends it: NewEquipmentItem (30)
    /// with quality, durability and the saved (or default) spell choice, or NewSimpleItem (32) with the count. The caller
    /// puts it into a slot (Join: the container arrays of the response; .item add: InventoryPutItem) and has checked
    /// <see cref="ItemTemplate.CanCreate"/>.
    /// </summary>
    private Item CreateItem(ItemTemplate type, InventoryItem saved, Func<int, bool> usable)
    {
        var item = MakeItem(type, saved, usable);
        SendItem(item);
        return item;
    }

    /// <summary>A new item object from its saved form (spells: the saved choice or the default one), not sent yet.</summary>
    private Item MakeItem(ItemTemplate type, InventoryItem saved, Func<int, bool> usable)
    {
        var (active, passive) = Selection(type.Name, saved, usable);
        return new Item(world.ObjectIds.Next(), type, saved.Count, Math.Max(saved.Quality, 1), saved.Durability ?? type.FullDurabilityRaw, active, passive, saved)
        {
            // Clamped: a saved fame over the maxfame (a lowered maxfame, an edited database) is a full one that Journals.Fill skips.
            JournalFame = type.IsJournal ? Math.Clamp(saved.Fame ?? Item.DefaultFame(type), 0, type.Journal!.MaxFame) : 0,
            Soul = type.IsEquipment ? saved.Soul : null,
        };
    }

    /// <summary>
    /// A new item of <paramref name="count"/> × <paramref name="type"/> (of <paramref name="quality"/>, full durability, the default spell choice
    /// for this session's player), not sent and in no container: a mob's loot (stage 5c), sent when its bag is opened.
    /// </summary>
    internal Item NewItem(ItemTemplate type, int count, int quality = 1) =>
        MakeItem(type, new InventoryItem(type.Name, count) { Quality = quality }, SpellUsable(Player?.Progression));

    /// <summary>An item object to this client: NewEquipmentItem (30) for equipment, else <see cref="CountEvent"/> (NewSimpleItem or NewJournalItem).</summary>
    internal void SendItem(Item item)
    {
        if (item.Template.IsEquipment) SendItemObject(item);
        else SendEvent(CountEvent(item));
    }

    /// <summary>
    /// A stackable item object with its count, as a new object or an update of a known one: NewSimpleItem (32) {0 id, 1 type, 2 count},
    /// a journal NewJournalItem (35) {0, 1, 2 count, 8 its fame a7m raw; 6 "", 7 0} (stageH journals SPEC §A.2: the client updates count
    /// and fame of a known journal object in place, cjb.bm 0x180aa8e90; key 7 is the c1p durability-like slot, 0, Q-A3).
    /// </summary>
    internal static AlbionEvent CountEvent(Item item) => item.Template switch
    {
        { IsJournal: true } => new NewJournalItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count, c27 = item.JournalFame }.FillEmpty(),
        // Furniture: NewFurnitureItem (33) {0, 1, 2 count, 4 durability a7m, 6 crafter "", 7 0} as captured (103829: key 4 686.98, 7 1350000 for a
        // chest; the beds and tables have no durability: key 4 0); labourers SPEC §1.4.
        { IsFurniture: true } => new NewFurnitureItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count, da1 = item.Template.FullDurabilityRaw, c0i = "" }.FillEmpty(),
        // A labourer contract: NewLaborerItem (36) {0, 1, 2 count} (the auz base only; SPEC §1.4).
        { IsLabourerContract: true } => new NewLaborerItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count }.FillEmpty(),
        _ => new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count }.FillEmpty(),
    };

    /// <summary>
    /// Logout from the settings menu (op 128, no fields, no request id). The client runs the countdown itself
    /// (cyg.u 0x180a00450: state Logout, "Logout - N" over the character) and then only waits: it goes back
    /// to the login screen once the server disconnects the game peer (GameGui.Update 0x1813d77f0 →
    /// FadeoutThenGoToLoginScreen on PhotonClient state 0), closes its login peer itself and later logs in
    /// from scratch. No response: any op-128 response or event 72 cancels the client's countdown.
    /// The time is the cluster type's logoutseconds, 10 s in every city and safe area (worldsettings);
    /// open world (60 s) and faction-flagged (30 s) are not modelled yet. Exit also sends op 128 and quits
    /// 0.4 s later on its own; <see cref="OnDisconnected"/> drops the timer then.
    /// </summary>
    [Handles(OperationCodes.LogoutStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? LogoutStart(LogoutStartRequest req)
    {
        if (Character is not { } c) return null;
        CancelLogout();
        // The client stops the player where it stands (cyg.u: movement → Idle) and sends no stop Move,
        // so the walk must not be extrapolated any further. Its Logout state also ends a harvest under way (the client
        // applies the harvest events to its harvest state only, cxp.aft 0x1809f53c0), so the server's ends too: else the
        // world tick would finish it during the countdown and add the resource. Event 60 as for a walk-off (ignored by a
        // client in its Logout state; not captured: the capture's logout came 23 s after the last harvest).
        if (Player is { } player)
        {
            if (player.Movement is { } move)
            {
                // A server-made stop: the observers get it as event 3 with position = target (stage 7 SPEC §2.2).
                var now = DateTime.UtcNow;
                var here = move.PositionAt(now);
                player.Move(here, here, 0, move.Direction, 0, now);
            }
            CancelHarvest(player, "logout", tellClient: true);
            Systems.Fishing.End(player, "logout"); // stageG: the Logout state ends the client's fishing action too
            Systems.Mounts.Cancel(player, "logout"); // the client's Logout state ends its mount bar (stageD mounts SPEC §4.2)
        }
        Timer? timer = null;
        // A little after the client's countdown, which it started just before sending op 128 (stageL roads: 900 s in a road under official).
        var time = world.LogoutTimeIn(Player?.Map);
        timer = new Timer(_ => EndLogout(timer!), null, time + TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
        _logout = timer;
        Log.Info(Name, $"'{c.Name}' logging out in {time.TotalSeconds:0.#} s");
        return null;
    }

    /// <summary>The player moved or acted during the countdown (op 129, no fields); the client has already left its Logout state.</summary>
    [Handles(OperationCodes.LogoutCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? LogoutCancel(LogoutCancelRequest req)
    {
        if (CancelLogout()) Log.Info(Name, $"'{Character?.Name}' logout cancelled");
        return null;
    }

    private bool CancelLogout()
    {
        if (_logout is not { } timer) return false;
        _logout = null;
        timer.Dispose();
        return true;
    }

    /// <summary>
    /// Countdown over: DISCONNECT with DisconnectByServerLogic (never 2, server timeout: the client would try
    /// to reconnect). <see cref="OnDisconnected"/> then saves the character.
    /// </summary>
    private void EndLogout(Timer timer)
    {
        lock (world.Sync)
        {
            if (_logout != timer) return; // cancelled or restarted meanwhile
            CancelLogout();
            Log.Info(Name, $"'{Character?.Name}' logged out: ending the game session");
            Peer.Disconnect(PhotonPeer.DisconnectByServerLogic);
        }
    }
}
