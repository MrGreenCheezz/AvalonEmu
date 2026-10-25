using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Bot-social (Bots:Social:*, Bots:Chat:*; <see cref="Server.Bots.BotSocial"/>): a human in Thetford with a game and a chat connection, GM
/// city bots beside him. A whisper to a bot's name, a party invite and a friend request are taken and answered after the delay; nothing is
/// stored; the city chatter keeps the cluster cap; an emote is waved back; with the switches off all of it is "not found" and silent.
/// </summary>
public static class BotSocialTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bot social: whisper to a bot by name (any case) RC 0, its ChatWhisper reply 2-6 s later; party invite RC 0 → declined 239 after the delay; friend request RC 0 → 296 declined, no row", Presence),
        ("bot chat: city chatter at chance 1 keeps Bots:Chat:MaxLinesPerMinutePerCluster in every minute; a wave at a bot is waved back", ChatLimit),
        ("bot social: Social and Chat off (or the master off): whisper 7720, invite 9000, friend 10640, no line, no emote back", SwitchesOff),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static List<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) => BotTest.Of(peer, code, from);

    /// <summary>A human with an account at <paramref name="at"/>, joined, with a registered chat connection.</summary>
    private static (WorldSession Game, FakePeer GamePeer, Player P, FakePeer ChatPeer) Human(Field f, string name, Vector2 at)
    {
        var account = f.Characters.CreateAccount(name, null, AccountLevel.Player)!;
        var (game, gamePeer, p) = f.Join(name, at, edit: c => c.Account = account.Id);
        var chatPeer = new FakePeer();
        var chat = new ChatSession(chatPeer, f.World);
        lock (f.World.Sync)
        {
            var token = f.World.Accounts.Select(account.Id, p.Character.Id);
            chat.OnRequest(Request(new RegisterChatPeerRequest { zc = token }, 1), encrypted: true);
        }
        Check(f.World.Chat.SessionOf(p.Character.Id) is not null, $"{name}'s chat connection registered");
        return (game, gamePeer, p, chatPeer);
    }

    private static Player City(Field f, Vector2 at)
    {
        lock (f.World.Sync) return f.World.Bots.Spawn(f.Map, BotRole.City, 4, at, 0)!;
    }

    private static void Presence()
    {
        using var f = new Field(new BotOptions { Enabled = true, ThinkSeconds = (60, 60), WhisperReplyChance = 1, PartyEnabled = false }, BotTest.NoPopulation); // the decline (Bots:Party: BotPartyTest)
        var at = f.Nav().Exits[0].Arrival;
        var (_, gamePeer, me, chatPeer) = Human(f, "Wendy", at);
        var bot = City(f, at + new Vector2(3, 0));
        var name = bot.Character.Name;
        var w = f.World;

        // Whisper.
        var chatMark = chatPeer.Events.Count;
        short rc;
        lock (w.Sync) rc = w.Chat.Whisper(me.Character.Id, me.Character.Name, AccountLevel.Player, name.ToUpperInvariant(), "hey, wanna group?");
        f.Run(1.5);
        var early = Of(chatPeer, EventCodes.ChatWhisper, chatMark).Count;
        f.Run(5);
        var replies = Of(chatPeer, EventCodes.ChatWhisper, chatMark);
        Check(rc == 0 && early == 0 && replies is [var reply] && (string)reply[0]! == name && w.BotSettings.WhisperLines.Contains((string)reply[3]!),
              $"whisper to '{name.ToUpperInvariant()}': RC {rc}, nothing within 1.5 s ({early}), then one ChatWhisper from the bot with a Chat.Whisper line ({replies.Count})");

        // Party invite: the ordinary checks (a second one 9009), then the decline.
        var mark = gamePeer.Events.Count;
        short first, second;
        string? target;
        lock (w.Sync)
        {
            first = w.Parties.Invite(me, Guid.Empty, name.ToLowerInvariant(), out target);
            second = w.Parties.Invite(me, bot.Character.Id, null, out _);
        }
        var open = w.Parties.InviteOf(bot.Character.Id) is not null;
        f.Run(6.5);
        var answers = Of(gamePeer, EventCodes.PartyInvitationAnswer, mark);
        Check(first == 0 && target == name && second == Parties.PendingInvitation && open && answers is [var a] && (string)a[0]! == name && a.GetValueOrDefault((byte)1) is null or false
              && w.Parties.InviteOf(bot.Character.Id) is null && w.Parties.Of(bot.Character.Id) is null && w.Parties.Of(me.Character.Id) is null,
              $"party invite: RC {first} ('{target}'), again by Guid {second}, then one PartyInvitationAnswer declined ({answers.Count}); no party");

        // Friend request: RC 0 with the bot's entry, 10642 again, then the decline; nothing stored.
        mark = gamePeer.Events.Count;
        FriendInviteResponse r1, r2;
        lock (w.Sync)
        {
            r1 = w.Social.Invite(me, name);
            r2 = w.Social.Invite(me, name);
        }
        f.Run(6.5);
        var declined = Of(gamePeer, EventCodes.FriendRequestAnswered, mark);
        var stored = ((ISocialStore)f.Characters).LoadSocial();
        Check(r1.ReturnCode == 0 && r1.arn == bot.Character.Id && r1.aro == name && r1.art && r2.ReturnCode == Social.AlreadyInvited
              && declined is [var d] && new Guid((byte[])d[0]!) == bot.Character.Id && d.GetValueOrDefault((byte)10) is null or false
              && stored.Requests.Count == 0 && stored.Friends.Count == 0 && w.Social.FriendsOf(me.Character.Id).Count == 0 && w.Characters.Find(name) is null,
              $"friend request: RC {r1.ReturnCode} with its entry, again {r2.ReturnCode}, one FriendRequestAnswered declined ({declined.Count}); no request, friend or character row");
        Check(w.Bots.Social.Pending == 0, "nothing left waiting");
    }

    private static void ChatLimit()
    {
        using var f = new Field(new BotOptions { Enabled = true, ThinkSeconds = (60, 60), SayChancePerMinute = 1, MaxLinesPerMinutePerCluster = 3 }, BotTest.NoPopulation);
        var at = f.Nav().Exits[0].Arrival;
        var (_, peer, me, _) = Human(f, "Clara", at);
        var bots = Enumerable.Range(0, 8).Select(i => City(f, at + new Vector2(2 + i % 4, 2 + i / 4))).ToList();
        var ids = bots.Select(b => b.Id).ToHashSet();
        var times = new List<int>(); // the second of each bot line
        for (var s = 0; s < 150; s++)
        {
            var mark = peer.Events.Count;
            f.Run(1);
            times.AddRange(Of(peer, EventCodes.ChatSay, mark).Where(p => ids.Contains(L(p, 4))).Select(_ => s));
        }
        var worst = times.Count == 0 ? 0 : times.Max(t => times.Count(x => x >= t && x < t + 60));
        Check(times.Count >= 6 && worst <= 3, $"150 s of chatter at chance 1: {times.Count} line(s), at most {worst} in any 60 s (cap 3)");

        // A wave at a bot: its wave back 1-2.5 s later.
        var bot = bots[0];
        var markE = peer.Events.Count;
        f.Run(1.1); // past the player's emote cooldown
        lock (f.World.Sync) Emotes.Play(f.World, me, 14, bot.Id);
        f.Run(3);
        var waves = Of(peer, EventCodes.PlayEmote, markE).Where(p => L(p, 0) == bot.Id).ToList();
        Check(waves is [var wave] && Convert.ToInt32(wave[1]) == 14, $"a wave at the bot: it waves back once ({waves.Count})");
    }

    private static void SwitchesOff()
    {
        foreach (var master in new[] { true, false })
        {
            // master: the kind switches off under an on master; else both on and the master switched off at run time.
            var options = new BotOptions { Enabled = true, ThinkSeconds = (60, 60), SocialEnabled = !master, ChatEnabled = !master, SayChancePerMinute = 1, WhisperReplyChance = 1 };
            using var f = new Field(options, BotTest.NoPopulation);
            var at = f.Nav().Exits[0].Arrival;
            var (_, peer, me, chatPeer) = Human(f, "Otto", at);
            var bot = City(f, at + new Vector2(3, 0));
            var name = bot.Character.Name;
            var w = f.World;
            if (!master)
            {
                // The master off for this run (.bot off): the bot goes, and so does any trace of it.
                lock (w.Sync) w.Bots.Enabled = false;
            }
            var mark = peer.Events.Count;
            var chatMark = chatPeer.Events.Count;
            short whisper, invite;
            FriendInviteResponse friend;
            lock (w.Sync)
            {
                whisper = w.Chat.Whisper(me.Character.Id, me.Character.Name, AccountLevel.Player, name, "hello?");
                invite = w.Parties.Invite(me, Guid.Empty, name, out _);
                friend = w.Social.Invite(me, name);
                Emotes.Play(w, me, 14, bot.Id);
            }
            f.Run(70);
            Check(whisper == ChatHub.PlayerNotOnline && invite == Parties.PlayerNotFound && friend.ReturnCode == Social.PlayerNotFound
                  && Of(chatPeer, EventCodes.ChatWhisper, chatMark).Count == 0 && Of(peer, EventCodes.ChatSay, mark).Count == 0
                  && !Of(peer, EventCodes.PlayEmote, mark).Any(p => L(p, 0) == bot.Id) && w.Bots.Social.Pending == 0,
                  $"{(master ? "Social and Chat off" : "the master off")}: whisper {whisper}, invite {invite}, friend {friend.ReturnCode}; no reply, line or emote in 70 s");
        }
        var file = Path.Combine(NewDir(), "albionlocal.json");
        File.WriteAllText(file, "{}");
        var official = LoadTestConfig(file, ["--Preset=official"]);
        Check(official.Bots is { Enabled: false, SocialEnabled: true, ChatEnabled: true, ReplyDelaySeconds: (2, 6), MaxLinesPerMinutePerCluster: 4 }
              && ServerConfig.PveDefaults.Bots is { Enabled: true, SocialEnabled: true, ChatEnabled: true, SayChancePerMinute: 0.05, FightLines: true },
              "official: off through Bots:Enabled false (the kind keys true under it); pve: on with the defaults");
    }
}
