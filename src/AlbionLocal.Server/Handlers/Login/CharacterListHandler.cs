using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Network;

/// <summary>
/// The character list: sent with the Login response, then select, create and delete. All need a logged-in
/// connection (Authed); the client sends them only after Login on the same connection (capture 103829:
/// after a refused Login the client opens a new connection and sends Login again, the same encrypted
/// 5730-byte request, before SelectCharacter).
/// </summary>
public sealed partial class LoginSession
{
    private static int SelectionEquipment(Character c, int slot, ItemStore? items) =>
        c.Equipment.ElementAtOrDefault(slot) is { } item ? items?.Find(item.Name)?.Index ?? 0 : 0;

    /// <summary>
    /// Character list = parallel arrays (LoginInProcessDialog.CreateCharacterRecord → LoginGui.c).
    /// Every array must hold one entry per character or the client throws. A guild member's keys 19 (guild name), 20 (role) and
    /// 22-28 (crest) come from <paramref name="guilds"/>; null = none. Key 34 is played milliseconds (client c4.k),
    /// key 35 is remaining premium days (client c4.p), not UTC timestamps. Zero means no premium.
    /// </summary>
    private static void FillCharacterList(LoginResponse r, IReadOnlyList<Character> chars, Systems.Guilds? guilds = null, long premiumDays = 0, World? world = null)
    {
        var n = chars.Count;
        T[] Each<T>(Func<Character, T> f) => chars.Select(f).ToArray();
        int[] Ints() => new int[n];
        long[] Longs() => new long[n];
        string[] Strings() => Enumerable.Repeat("", n).ToArray();

        r.bdd = Each(c => c.Name);            // [9]  c.a  name (sent back in SelectCharacter key 0)
        r.bde = Each(c => Systems.OpeningTutorial.ClientCluster(c, world)); // [10] client instance id
        r.bdf = Strings();                    // [11] c.d
        r.bdg = Strings();                    // [12] c.e
        r.bdh = Each(c => world?.Islands.WorldOf(c.Cluster) ?? world?.Expeditions.WorldOf(c.Cluster)
            ?? world?.Corrupted.Find(c.Cluster)?.World ?? world?.Hellgates.WorldOf(c.Cluster) ?? c.Cluster); // [13] base world map
        r.bdi = Each(c => c.Template);        // [14] avatar lookup (creation key 1)
        r.bdj = Ints();                       // [15] c.g  appearance lookup
        r.bdk = new byte[n];                  // [16] c.h  FlaggingStatus
        r.bdl = Each(c => c.Appearance);      // [17] body template (creation key 2)
        r.bdm = Each(c => world?.GameData?.Looks.SelectionJson(c) ?? ""); // [18] cpr customization JSON for selection preview
        r.bdn = Strings();                    // [19] c.j  guild name (logo built only when non-empty)
        r.bdo = new byte[n];                  // [20] c.k  GuildRole
        r.bdp = new byte[n];                  // [21] c.m  FlaggingStatus
        r.bdq = Ints(); r.bdr = Ints(); r.bds = Ints(); r.bdt = Ints(); r.bdu = Ints(); // [22-26] guild logo
        r.bdv = new float[n]; r.bdw = new float[n];                                      // [27-28] guild logo
        r.bdx = Longs(); r.bdy = Longs(); r.bdz = Longs(); r.bd0 = Longs(); r.bd1 = Longs(); // [29-33] a7m values
        r.bd2 = Each(c => c.PlayedMilliseconds); // [34] c.s: played duration in milliseconds
        r.bd3 = Each(c => premiumDays);       // [35] c.t: remaining premium duration in days
        r.bd4 = Ints();                       // [36] c.u
        // LoginGui.c stores head, armor, cape, mainhand, offhand, shoes in that wire order.
        r.bd5 = Each(c => SelectionEquipment(c, 2, world?.Items));
        r.bd6 = Each(c => SelectionEquipment(c, 3, world?.Items));
        r.bd7 = Each(c => SelectionEquipment(c, 6, world?.Items));
        r.bd8 = Each(c => SelectionEquipment(c, 0, world?.Items));
        r.bd9 = Each(c => SelectionEquipment(c, 1, world?.Items));
        r.bea = Each(c => SelectionEquipment(c, 4, world?.Items));
        r.beb = Ints(); r.bec = Ints(); r.bed = Ints(); r.bee = Ints(); r.bef = Ints(); r.beg = Ints(); // [37-48] equipment
        r.bfq = Longs();                      // [94] c.ai
        for (var i = 0; i < n && guilds is not null; i++)
        {
            if (guilds.Of(chars[i].Id) is not { } g) continue;
            var l = g.Logo;
            (r.bdn[i], r.bdo[i]) = (g.Name, (byte)g.Find(chars[i].Id)!.Role);
            (r.bdq[i], r.bdr[i], r.bds[i], r.bdt[i], r.bdu[i], r.bdv[i], r.bdw[i]) = (l.Schema, l.Symbol, l.Color1, l.Color2, l.SymbolColor, l.Scale, l.Offset);
        }
    }

    /// <summary>
    /// The character to play: its cluster (key 1, which the Join's key 8 must equal) from the newest saved state, a snapshot
    /// still waiting for the writer after a cluster change included (SPEC stage 3 §4.4), and the account's selected
    /// character for the Joins, whose key 0 is the account. With the chat server (Chat:Enabled) key 7 names it and key 8 is the
    /// selection's chat token, which the client's chat peer presents in RegisterChatPeer (stageJ chat SPEC §1.1, §2.2); without it
    /// key 7 stays "" and the client opens no chat peer.
    /// </summary>
    [Handles(OperationCodes.SelectCharacter, Status = SessionStatus.Authed)]
    private SelectCharacterResponse? SelectCharacter(SelectCharacterRequest req)
    {
        // Only a character of this login's account (names are unique over all accounts).
        var found = characters.Find(req.abc ?? "") is { } named && named.Account == _account.Id ? named : null;
        if (found is null)
        {
            Log.Warn(Name, $"unknown character '{req.abc}'");
            return new SelectCharacterResponse { ReturnCode = 0x231 };
        }
        var c = World?.LoadCharacter(found.Id)?.Clone() ?? found;
        // StageG dungeons (SPEC §2.3, §9 step 4): inside a live random dungeon the client needs the floor's layout in key 4 (ci7.x_t325
        // parses it and builds the floor as after a ChangeCluster); a dungeon that is gone sends the character back where it came in.
        World?.Dungeons.ReturnIfClosed(c);
        World?.Expeditions.ReturnIfClosed(c);
        World?.Corrupted.ReturnIfClosed(c); // stageL: a corrupted dungeon that is gone → the way back
        World?.Hellgates.ReturnIfClosed(c); // stageL: a hellgate that is gone (closed, or a restart) → the way back
        World?.Arenas.ReturnIfClosed(c); // stage N: an arena match that is gone → the way back
        World?.Mists.ReturnIfClosed(c); // stageL: a mists zone that is gone (closed, or a restart) → the way back
        World?.Roads.ThrowOutIfStale(c); // stageL roads phase B: offline in a road past Roads:ThrowOutAfterMinutes → the respawn city, ThrownOutAvalonRoads
        World?.Islands.ReturnIfGone(c); // stageH islands SPEC §1.6: an island that is gone → its city's travel point
        var floor = World?.Dungeons.Floor(c.Cluster);
        Character = c;
        var token = World?.Accounts.Select(_account.Id, c.Id);
        World?.Chat.Selected(_account.Id, c); // stageJ: a chat connection kept over a re-Login follows the selection
        Log.Info(Name, $"character '{c.Name}' selected; game server {config.GameServerAddress}, cluster {c.Cluster}{(floor is null ? "" : $" (dungeon floor, {floor.Bytes.Length} layout bytes)")}");
        World?.PrefetchCluster(c.Cluster);
        var r = new SelectCharacterResponse
        {
            bln = config.GameServerAddress,   // [0] game server "ip:port" (ci7+0x118 → c32.i(string))
            blo = Systems.OpeningTutorial.ClientCluster(c, World), // [1] client instance, paired with Join key 8
            blw = DateTime.UtcNow.Ticks,      // [9] GameTimeStamp
            blx = DateTime.UtcNow.Ticks,      // [10] c4
        }.FillEmpty();
        if (r.blo != c.Cluster) r.blp = c.Cluster; // [2] authored tutorial map cloned under its client instance id
        if (config.ChatEnabled && token is { } chat)
        {
            r.blu = config.ChatServerAddress; // [7] chat server "ip:port" (c31.i: "" = no chat peer)
            r.blv = chat;                     // [8] the token of RegisterChatPeer key 0
        }
        if (floor is not null) r.blr = floor.Bytes; // [4] the floor's a9p layout (a80.aq); outside a dungeon FillEmpty's empty one, as before
        // StageH (expeditions SPEC §2.6): inside a live expedition, key 2 = the world.xml cluster the instance is a copy of (ChangeCluster's key 1).
        if (World?.Expeditions.WorldOf(c.Cluster) is { } expedition) r.blp = expedition;
        // StageL corrupted (SPEC §2.3 [V], ci7.dn): inside a live corrupted dungeon, key 2 = its world.xml map and key 5 = the difficulty's cluster type.
        if (World?.Corrupted.Find(c.Cluster) is { } corrupted) (r.blp, r.bls) = (corrupted.World, corrupted.Difficulty.ClusterType);
        // StageL hellgates (SPEC §3.5): inside a live hellgate, key 2 = its world.xml map (as an expedition's).
        if (World?.Hellgates.WorldOf(c.Cluster) is { } hellgate) r.blp = hellgate;
        // Stage N arena (SPEC §1.4): inside a live arena match, key 2 = its world.xml map (as a hellgate's).
        if (World?.Arenas.WorldOf(c.Cluster) is { } arena) r.blp = arena;
        // StageL mists (SPEC §3.4, §6.6): inside a live zone, key 4 = its a9i layout, key 5 = the mode's clustertype, key 6 = the tier.
        if (World?.Mists.Find(c.Cluster) is { } zone) (r.blr, r.bls, r.blt) = (zone.Bytes, zone.Mode.ClusterType, zone.Tier);
        // StageH islands (SPEC §1.6): on an island, key 2 = the template of its level (ciy.a_9 clones it under "@ISLAND@<guid>").
        if (World?.Islands.WorldOf(c.Cluster) is { } island) r.blp = island;
        return r;
    }

    [Handles(OperationCodes.CreateCharacter, Status = SessionStatus.Authed)]
    private CreateCharacterResponse CreateCharacter(CreateCharacterRequest req)
    {
        var name = (req.h7 ?? "").Trim();
        // Progression:NewCharacters: FairStart = an empty destiny board and the kit its level allows; UnlockAll = no board
        // of its own (everything unlocked) and the usual kit.
        var fair = config.NewCharacters == NewCharacterProgression.FairStart;
        var opening = config.TutorialOpening ? World?.OpeningTutorial
            ?? throw new InvalidOperationException("Opening tutorial data was not loaded.") : null;
        var c = opening is null ? (fair ? FairStarter : starter).NewCharacter(name)
            : opening.Create(name, World!.Items, World.GameData!);
        // Without the client's board data an empty board would have no START to grow from: unlock-all instead.
        if (opening is null && fair && World?.GameData?.Board is { } board) c.Progression = board.FairStart();
        else if (opening is null && fair) Log.Warn(Name, $"fair start for '{name}' without destiny board data: everything unlocked");
        c.Account = _account.Id; // in this login's account; the name must be free on the whole server
        c.Template = req.h8;
        c.Appearance = req.h9;
        c.Customization = req.ia ?? [];
        c.Clothes = req.ib ?? [];
        Systems.Focus.NewCharacter(c, config); // stageJ focus-lp: Focus:Start, LearningPoints:Start
        if (name.Length == 0)
            return new CreateCharacterResponse { ReturnCode = 483 }.FillEmpty(); // NAME_NOT_ALLOWED
        if (!characters.Create(c))
            return new CreateCharacterResponse { ReturnCode = 482 }.FillEmpty(); // NAME_NOT_UNIQUE
        Log.Info(Name, $"created character '{c.Name}' avatar {c.Template}, body {c.Appearance}{(fair ? ", fair start" : "")}");
        if (World is { } world) world.Post(() => world.Bots.NameTaken(c.Name)); // stage I: a bot carrying the name goes
        return new CreateCharacterResponse
        {
            aop = c.Name,
            aoq = Systems.OpeningTutorial.ClientCluster(c, World), // [1] client instance
            aor = c.Cluster, // [2] world map used to resolve the entry's location label
            aos = c.Template,
            aou = c.Appearance,
            aov = c.Customization,
            aow = c.Clothes,
            ao4 = SelectionEquipment(c, 2, World?.Items),
            ao5 = SelectionEquipment(c, 3, World?.Items),
            ao6 = SelectionEquipment(c, 6, World?.Items),
            ao7 = SelectionEquipment(c, 0, World?.Items),
            ao8 = SelectionEquipment(c, 1, World?.Items),
            ao9 = SelectionEquipment(c, 4, World?.Items),
        }.FillEmpty();
    }

    /// <summary>Deletes a character of this login's account; another account's is left alone (logged as not deleted).</summary>
    [Handles(OperationCodes.DeleteCharacter, Status = SessionStatus.Authed)]
    private AlbionResponse DeleteCharacter(DeleteCharacterRequest req)
    {
        var found = characters.Find(req.jg ?? "");
        var own = found is not null && found.Account == _account.Id;
        var deleted = own && characters.Delete(req.jg ?? "");
        if (deleted) World?.Social.CharacterDeleted(found!.Id); // off its friends' lists first (the rows went with the character)
        if (deleted) World?.Guilds.CharacterDeleted(found!.Id); // then out of its guild, as with LeaveGuild (stageH guilds SPEC §4)
        if (deleted) World?.Islands.CharacterDeleted(found!.Id); // its islands go with it
        Log.Info(Name, $"delete character '{req.jg}': {deleted}");
        return new GenericResponse();
    }
}
