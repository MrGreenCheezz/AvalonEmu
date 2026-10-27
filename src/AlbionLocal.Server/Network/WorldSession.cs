using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Game server (client peer c32, connected to the address from SelectCharacter), one session per
/// connection; what all connections share (object ids, characters, client data) is in <see cref="World"/>.
/// Join is answered with the local character (consumers ci7.e_25 and cx2.ai7, traced fields
/// are commented below): its items go out first, then the Join response, the destiny board state
/// and JoinFinished. Health/energy are the player's bars (<see cref="Player.RecalculateStats"/>, from
/// <see cref="StatCalculator"/> when game data is loaded).
/// Handlers: Handlers/World (CharacterHandler: Join and logout; MovementHandler; ItemHandler: moves,
/// item spells and GM items; ChatHandler: Say, relayed as ChatSay, and the GM commands typed into it, the item-link chat lines and
/// whispers; SocialHandler: the friends window and the mute list; RankingHandler: the ranking lists;
/// MailHandler: the mailbox (lists, reading, player mail, attachments); KeepaliveHandler: the Photon-level keepalive; ClusterHandler: exits and .tele;
/// JourneyHandler: travel-point journeys; HarvestHandler: gathering the static harvestables; CombatHandler: auto-attacks
/// and spells on mobs; LootHandler: a mob's loot bag; GuildHandler: the guild window's requests). Join makes the session InWorld and puts
/// its <see cref="Player"/> on the map of its cluster; everything else needs that. A cluster change ends the session's
/// part in the world (Transferring): the client joins again on a new connection.
/// </summary>
public sealed partial class WorldSession(IPeer peer, World world) : AlbionSession(peer, "game")
{
    /// <summary>The joined player; null before Join.</summary>
    public Player? Player { get; private set; }

    /// <summary>The account whose character this connection joined with (<see cref="AccountStates"/>); null before Join.</summary>
    private Guid? _account;

    /// <summary>Pending logout countdown (<see cref="World.LogoutTime"/>); null when none runs.</summary>
    private Timer? _logout;

    /// <summary>An event to this connection's client (the world objects of <see cref="VisibilityTracker"/>).</summary>
    public void Send(AlbionEvent ev) => SendEvent(ev);

    /// <summary>
    /// Stage I bots (analysis/work/stageI/bots/SPEC.md §1.2): the session of a bot, on <see cref="BotPeer"/>: no client, no account, no
    /// keepalive, nothing sent. <see cref="AttachBot"/> gives it its player.
    /// </summary>
    internal static WorldSession ForBot(World world) => new(BotPeer.Instance, world);

    /// <summary>A bot's player on its session (step 4 of the spawn): InWorld at once, as a Join would leave it, with no account.</summary>
    internal void AttachBot(Player player)
    {
        Player = player;
        Character = player.Character;
        Status = SessionStatus.InWorld;
    }

    /// <summary>
    /// One item of a bot's gear (SPEC D10): quality 1, full durability, a full active spell choice (the default selection, and in every
    /// slot it leaves to the player, -1, a random usable candidate: a bot casts its whole kit, stageJ pvp-spells SPEC §2.9; stageM bots: a
    /// <see cref="FightingSpell"/> when the slot has one, never a usableincombat="false" one) and <b>no
    /// passives</b> (a passive's pulses and on-hit procs would cast areas with the bot as caster that hit mobs, and mobs never fight bots).
    /// Not sent.
    /// </summary>
    internal Item NewBotItem(ItemTemplate type, Func<Data.SpellTemplate, bool>? prefer = null, int quality = 1)
    {
        var usable = SpellUsable(world.UnlockAll);
        var (active, _) = Selection(type.Name, new InventoryItem(type.Name, 1), usable);
        if (world.Spells is { } spells && spells.SlotsOf(type.Name) is { } slots)
            for (var i = 0; i < active.Length && i < slots.Active.Length; i++)
            {
                if (active[i] >= 0) continue;
                // StageM bots: never a spell it may not cast in a fight (OUTOFCOMBATHEAL), and a fighting one (damage, cc, movement) first.
                var choice = slots.Active[i].Where(c => usable(c) && spells.Template(c) is not { UsableInCombat: false }).ToArray();
                // StageN companions: a kit by role (a tank's taunt, a healer's heals) before the rest.
                if (prefer is not null && choice.Where(c => spells.Template(c) is { } t && prefer(t)).ToArray() is { Length: > 0 } preferred) choice = preferred;
                else if (choice.Where(c => spells.Template(c) is { } t && FightingSpell(t)).ToArray() is { Length: > 0 } fighting) choice = fighting;
                if (choice.Length > 0) active[i] = choice[world.Bots.Random.Next(choice.Length)];
            }
        return new Item(world.ObjectIds.Next(), type, 1, Math.Clamp(quality, 1, 5), type.FullDurabilityRaw, active, [], null); // stageN botpower: Bots:Gear:Quality
    }

    /// <summary>
    /// A spell a bot's kit prefers (stageM bots): one at an enemy or the ground, or of category damage, crowdcontrol or debuff, or of uitype
    /// damage, crowdcontrol or movement (a dash); not a heal, a shield or a utility buff (SUMMONER_CD_REDUCTION).
    /// </summary>
    internal static bool FightingSpell(Data.SpellTemplate spell) =>
        Systems.SpellTargets.Parse(spell.Target) is var cls && (cls == Systems.SpellTargets.Cls.Ground || Systems.SpellCasts.Enemy(cls))
        || spell.Category is "damage" or "crowdcontrol" or "debuff" || spell.UiType is "damage" or "crowdcontrol" or "movement";

    /// <summary>
    /// <paramref name="item"/> of a bot (or minted for its bag) as a player's item (Bots/BotRewards: a defeated bot's bag): the same object
    /// id, template, count, quality and durability with a player's spell layout, its active choice when it has the template's slots (else
    /// the default selection) and the default passive selection, so that EquipmentItemChangeSpell and the passives work on it. Not sent.
    /// </summary>
    internal Item PlayerItem(Item item)
    {
        var (active, passive) = Selection(item.Template.Name, new InventoryItem(item.Template.Name, item.Count), SpellUsable(world.UnlockAll));
        return new Item(item.Id, item.Template, item.Count, item.Quality, item.DurabilityRaw, item.Active.Length == active.Length ? item.Active : active, passive, null);
    }

    /// <summary>
    /// The connection is gone: the player leaves the world (<see cref="Leave"/>) and its party (<see cref="Systems.Parties.Disconnected"/>),
    /// its guild sees it offline (<see cref="Systems.Guilds.Disconnected"/>), its chat connection leaves its channels
    /// (<see cref="Systems.ChatHub.LoggedOut"/>) and its friends see it offline (<see cref="Systems.Social.Disconnected"/>),
    /// then the character is saved with the
    /// time it left (Join key 101 of the next login). A <see cref="SessionStatus.Transferring"/> session only logs: its
    /// character was saved in the target cluster at the commit and belongs to the next Join, which a late timeout here must
    /// not write over (SPEC stage 3 §4.1).
    /// </summary>
    public override void OnDisconnected(string reason)
    {
        base.OnDisconnected(reason);
        if (_account is { } account) world.Accounts.Detach(account, this);
        _closed = true;
        _changing = null;
        if (Status == SessionStatus.Transferring)
        {
            Log.Info(Name, "the game connection left behind by a cluster change is closed (nothing to save)");
            return;
        }
        CancelLogout();
        if (Player is { Map: not null } downed) Systems.Death.Abandoned(downed); // stageE E3: knocked down in a lethal cluster, it dies
        var dead = Player is { Combat.Down.Kind: KnockdownKind.Dead } ? Player : null; // stageE E3: it joins at its respawn point
        if (Player is { } player) Leave(player);
        // Out of its party unless another connection still plays the character (a reconnect; stageD party SPEC §7 flow 3).
        // Its guild's members see it go offline the same way (stageH guilds SPEC §3.5), and its chat channels and friends (stageJ chat SPEC §2.2).
        if (Character is { } member && world.Parties.Online(member.Id) is null)
        {
            world.Parties.Disconnected(member.Id);
            world.Guilds.Disconnected(member.Id);
            world.Chat.LoggedOut(member.Id);
            world.Social.Disconnected(member);
        }
        if (Character is { } c)
        {
            Player?.StorePosition(DateTime.UtcNow);
            if (dead is not null) Systems.Death.Departed(world, dead);
            c.LastLeftAt = DateTime.UtcNow.Ticks;
            Log.Info(Name, $"'{c.Name}' left at ({c.PositionX:0.##}, {c.PositionY:0.##})");
            world.Saves.SaveNow(c);
        }
    }

    /// <summary>
    /// <paramref name="player"/> leaves the world (the connection ended, a cluster change, or a new Join on it): the player
    /// hooks' OnLogout while it is still on its map, its items saved into the character when a hook ran
    /// (<see cref="Player.SaveItems"/>), its fight dropped (<see cref="Systems.Combat.Leave"/>), then a Leave to every player who knows it (<see cref="VisibilityTracker.LeavePlayers"/>,
    /// stage 7 SPEC §3), then off the map (the last one out starts the map's idle time), and its chat connection off the map's Area
    /// channel at once (<see cref="Systems.ChatHub.LeftMap"/>: at a cluster change before the ChangeCluster response, as official).
    /// </summary>
    private void Leave(Player player)
    {
        player.StopPlayedTime();
        if (world.Scripts.PlayerHooks.Count > 0)
        {
            world.Scripts.OnLogout(player);
            player.SaveItems();
        }
        Systems.Consumables.Abort(player, "the player left");
        Systems.SpellCasts.EndToggles(player, Systems.SpellCasts.ToggleEnd.Left); // its toggles end quietly: their buffs are not saved
        Systems.Consumables.Store(player); // its own buffs and slot cooldowns, saved with the character (stageD consumables SPEC §6)
        Systems.Fishing.End(player, "the player left"); // stageG: the FishingCancel and the float's Leave to the observers
        Systems.Mounts.Leave(player); // a mount under way dropped, the mount object gone; mounted stays saved (stageD mounts SPEC §3)
        world.Trades.Left(player); // stageJ: its trade ends (177 to both) before anything moved
        // stageJ focus-lp: the focus and the learning points settled under this player's premium (a GM's .premium lives with the player,
        // so the next Join, a cluster change included, counts from here under the mode's state).
        Systems.Focus.Settle(player, world.GameNow);
        Systems.LearningPoints.Accrue(player, world.GameNow, send: false);
        Systems.Combat.Leave(player);
        CancelAction(player, "the player left", tellClient: false);
        CloseVaults(player);
        player.Visibility.LeavePlayers();
        world.Maps.Remove(player, world.Clock.Now);
        world.Chat.LeftMap(player);
    }

    /// <summary>
    /// The destiny board state of <paramref name="player"/>: FullAchievementInfo (150) from its progression, then, when a
    /// node of its own board has fame inside its level, FullAchievementProgressInfo (153), and FullAutoLearnAchievementInfo (155) (the official order,
    /// PROGRESSION.md §6.5; without it the board shows no progress, 150 clears it). After every Join and after a GM changes
    /// the board (.progress). Nothing without game data (<see cref="Player.Progression"/> null).
    /// </summary>
    internal void SendDestinyBoard(Player player)
    {
        if (player.Progression is not { } progression) return;
        var auto = player.Character.Progression is not null ? player.Character.AutoLearn : null; // an own board only
        var a = progression.BuildFullAchievementInfo(auto?.Ids);
        // Keys 1 and 2 must be non-null even when empty (key 2 null throws in cs6.k 0x180bfc140);
        // keys 3-5 are parallel to key 2.
        SendEvent(new FullAchievementInfoEvent
        {
            ddn = player.Id, ccy = a.Completed, ccz = a.Ids, cc0 = a.Levels, cc1 = a.Flags4, cc2 = a.Flags5,
        });
        if (player.Character.Progression is { } own && Systems.Destiny.FullProgress(player.Id, own, world.GameData?.Board) is { } full)
            SendEvent(full);
        SendEvent(Systems.Destiny.Tracked(player.Id, progression, world.GameData?.Board));
        // FullAutoLearnAchievementInfo (155), the official third (§6.5): the auto-learn nodes as op 312 left them (stageJ focus-lp SPEC §5.3);
        // key 3 only when the client sent one, key 4 only with 2 entries or more (the handler indexes it per learn group).
        SendEvent(new FullAutoLearnAchievementInfoEvent
        {
            ddn = player.Id, cc7 = auto?.Ids ?? [], cc8 = auto?.Groups ?? 0, cc9 = auto?.Categories,
            cda = auto?.PerGroup is { Length: >= 2 } groups ? groups : null,
        });
    }

    /// <summary>
    /// After a Say response: the line as ChatSay (<see cref="SendChatSay"/>).
    /// After the Join response: the chat tabs, the cluster-wide world objects (phase B: the faction trader with its
    /// QuestGiverInfoForPlayer, SPEC stage 2 §6), FullAchievementInfo (event 150), FullAchievementProgressInfo (153) and
    /// FullAutoLearnAchievementInfo (155, <see cref="SendDestinyBoard"/>), then JoinFinished (event 2) and right
    /// after it the AccessStatus batch of the buildings (<see cref="VisibilityTracker.SendAccessStatus"/>) and the static harvestables in view (phase C, NewSimpleHarvestableObjectList; official 0-33 ms later), the
    /// official order. 150 must follow every Join (each Join builds a new player object, which clears
    /// the unlock sets) and carry the player object id in key 0, or cs6.k(aph) 0x180bf9e40 ignores it.
    /// The client rebuilds from it the item/spell unlock sets that equipping checks (ctp.ahf
    /// 0x1809965e0: items.xml unlockedtoequip or an ItemUse reward of an achievement at its level).
    /// JoinFinished: ci7.xk case 2 (0x1809836f2) moves the connection to state 9; without it the
    /// client stays on a black screen. Then the scripts, now that the client has the player: OnEquip of the equipped
    /// items' scripts, the player hooks' OnLogin, and when one of them ran, <see cref="Player.SaveItems"/> (what they
    /// change in the items is saved; with no script the character's saved form stays as it was). Last, for a party member,
    /// its PartyJoined (<see cref="Systems.Parties.Joined(Entities.Player, bool)"/>), then the guild's events, the chat connection's Area
    /// and Guild channels (<see cref="Systems.ChatHub.Joined"/>), the mailbox's unread count (<see cref="Systems.Mailer.Joined"/>) and, after a
    /// login, the friends and mute lists (<see cref="Systems.Social.Joined"/>).
    /// </summary>
    protected override void AfterResponse(OperationCodes code, AlbionResponse resp)
    {
        if (code == OperationCodes.Say)
        {
            SendChatSay(); // the Say line as ChatSay, after its SayResponse (stage 7 SPEC §5)
            return;
        }
        if (code != OperationCodes.Join || resp is not JoinResponse { ReturnCode: 0 } jr) return;
        SendChatTabs();
        Player?.Visibility.SendClusterWide();
        if (Player is { } joined)
        {
            SendDestinyBoard(joined);
            // Its list, the mount events and the slot cooldowns (stageD consumables SPEC §6, mounts SPEC §4.8).
            foreach (var e in Systems.Consumables.JoinEvents(joined, Systems.Mounts.JoinEvents(joined))) SendEvent(e);
        }
        // OnEnterGame reads the active quest to decide whether the tracker opens. Install it before
        // JoinFinished switches the client into the world; later progress events update that model.
        if (Player is { } tutorialPlayer) Systems.OpeningTutorial.Joined(tutorialPlayer);
        SendEvent(new JoinFinishedEvent { ddn = jr.a8a });
        if (Player is not { } player) return;
        Systems.OpeningTutorial.Entered(player);
        player.Visibility.SendAccessStatus();
        player.Visibility.FlushLists();
        var scripted = false;
        for (var slot = 0; slot < Inventory.EquipmentSlots; slot++)
            if (player.Inventory.Equipment[slot] is { } item) scripted |= world.Scripts.OnEquip(player, item);
        if (world.Scripts.PlayerHooks.Count > 0)
        {
            world.Scripts.OnLogin(player, world.FirstJoin(player.Character));
            scripted = true;
        }
        if (scripted) player.SaveItems();
        world.Parties.Joined(player, _arrived); // a member's PartyJoined after a fresh login (stageD party SPEC §7 flow 9)
        world.Guilds.Joined(player, _arrived); // a member's 104 kind 0, after a login also 103 kind 0 and 349 (stageH guilds SPEC §3.5)
        world.GuildGoals.Joined(player, _arrived); // stage I: 351 in a client season; after a login the guild's level, points and week
        world.Chat.Joined(player); // stageJ: 205 of the map's Area (and the guild's channel) to its chat connection
        Systems.Mailer.Joined(player, login: !_arrived); // stageJ mail: 201 with the unread count, after a login 202 {0: true}
        world.Social.Joined(player, login: !_arrived); // stageJ: after a login 403, 294, 294, 295 and 297 to its friends
        if (_respawned) Systems.Death.Arrived(player); // stageE E3: the Respawn event of a respawn's arrival [infer]
        world.Expeditions.Joined(player); // stageH: in an instance 263 kind 7, 260, 261; reward items that wait
        world.Corrupted.Joined(player); // stageL: in a corrupted instance 450 (the tracker), the arrival buff, the chat line
        world.Hellgates.Joined(player); // stageL: in a hellgate 454 and 453 (the tracker)
        Systems.SpawnBuffs.Joined(player, _arrived, _respawned); // stageP: the zone-entry protection and the spell cooldowns on entering
        world.Arenas.Joined(player); // stage N: in an arena match its arrival (the countdown once all are in)
        world.Mists.Joined(player); // stageL: in a mists zone 521 (the return portal, the border exits' target, the shutdown), the chat line
        world.StaticDungeons.Joined(player); // stageN: 499, the events and values of the dungeon here and of those entered from here
        world.Roads.Joined(player); // stageL roads phase B: the thrown-out line
        world.Dungeons.Arrived(player); // stageH: the dungeon of the day's chat line on its floor
        world.Islands.Joined(player); // stageN labourers: on an island the houses unlocked for the build menu (224)
    }

    /// <summary>Whether the last Join was the arrival of a cluster change (a PendingTransfer, and not the first Join after a Login).</summary>
    private bool _arrived;

    /// <summary>Whether the last Join was the arrival of a dead player's respawn (<see cref="Systems.Death.RespawnVia"/>).</summary>
    private bool _respawned;
}
