using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// Bot-social (analysis/work/official-audit/audit-bots.md improvements 2 and 3): the world bots' social presence and their local chat.
/// <para>
/// Presence (Bots:Social:Enabled under Bots:Enabled): a world bot (City, Gatherer, Patrol, Drifter; not defeated, on a map) is an online
/// player by name and Guid to the party invite (<see cref="Parties.Invite"/>: the ordinary PartyInvitation flow, then a decline after
/// Bots:Social:ReplyDelaySeconds, or with Bots:Party an accept by <see cref="WantsParty"/>: the bot then plays as the inviter's companion,
/// <see cref="Companions.Recruit"/>), the friend request (<see cref="Social.Invite"/>: RC 0
/// with the bot's entry, then FriendRequestAnswered 296 {declined}; nothing stored) and the whisper (<see cref="ChatHub.Whisper"/>: RC 0, then
/// with Bots:Social:WhisperReplyChance a ChatWhisper 75 from the bot with a line of content/bots.json Chat.Whisper on the sender's chat
/// connection). A bot never becomes an account, a character row, a friend, a request or a mute.
/// </para>
/// <para>
/// Chat (Bots:Chat:Enabled under Bots:Enabled): ChatSay 74 from the bot to the players that see it (the event of a player's Say line: the
/// client knows the speaker by its object id): city chatter (Bots:Chat:SayChancePerMinute per city bot a player sees), an emote back at a
/// player's emote (Bots:Chat:EmoteReplies, sometimes with a Chat.Greet line) and after a fight a Chat.Win / Chat.Loss line
/// (Bots:Chat:FightLines). Every local line counts against Bots:Chat:MaxLinesPerMinutePerCluster and a bot speaks at most once in
/// <see cref="BotLineCooldown"/>. Touched only under <see cref="World.Sync"/>.
/// </para>
/// </summary>
public sealed class BotSocial(World world, BotManager bots)
{
    /// <summary>A bot's shortest time between two local lines (and between two emote replies).</summary>
    public static readonly TimeSpan BotLineCooldown = TimeSpan.FromSeconds(30);

    /// <summary>A bot answers the whispers of one sender at most once in this time (no endless whisper ping-pong).</summary>
    public static readonly TimeSpan WhisperCooldown = TimeSpan.FromSeconds(30);

    /// <summary>How often the city chatter is rolled.</summary>
    public static readonly TimeSpan ChatterEvery = TimeSpan.FromSeconds(5);

    /// <summary>The distance within which the nearest city bot answers an untargeted emote.</summary>
    public const float EmoteRange = 8;

    private readonly List<(TimeSpan Due, Action Act)> _later = [];
    private readonly Dictionary<string, Queue<TimeSpan>> _clusterLines = new(StringComparer.Ordinal);
    private readonly Dictionary<long, TimeSpan> _botLine = [];
    private readonly Dictionary<long, TimeSpan> _botEmote = [];
    private readonly Dictionary<(Guid Bot, Guid Sender), TimeSpan> _whispered = [];
    private readonly HashSet<(Guid Player, Guid Bot)> _friendAsks = [];
    private TimeSpan _nextChatter;

    private Random? _random;

    /// <summary>Its own random numbers (seeded from World.MapSeed + 1 when set: the self-test), so that the chat never shifts the bots' other rolls.</summary>
    private Random Random => _random ??= world.MapSeed is { } seed ? new Random(seed + 1) : new Random();

    private BotOptions Options => bots.Options;

    private TimeSpan Now => world.Clock.Now;

    /// <summary>Bots:Social:Enabled under the master switch.</summary>
    public bool SocialOn => bots.Enabled && Options.SocialEnabled;

    /// <summary>Bots:Party:Enabled under Bots:Social:Enabled and the master switch.</summary>
    public bool PartyOn => SocialOn && Options.PartyEnabled;

    /// <summary>Bots:Chat:Enabled under the master switch.</summary>
    public bool ChatOn => bots.Enabled && Options.ChatEnabled;

    /// <summary>Delayed answers waiting (the self-test's view).</summary>
    public int Pending => _later.Count;

    private TimeSpan ReplyDelay()
    {
        var (min, max) = Options.ReplyDelaySeconds;
        return TimeSpan.FromSeconds(min + Random.NextDouble() * (max - min));
    }

    private void Later(TimeSpan delay, Action act) => _later.Add((Now + delay, act));

    private string? Pick(IReadOnlyList<string> lines) => lines.Count == 0 ? null : lines[Random.Next(lines.Count)];

    private static bool WorldBot(Player p) =>
        p.Bot is { Role: BotRole.City or BotRole.Gatherer or BotRole.Patrol or BotRole.Drifter, Fight.Defeated: false } && p.Map is not null;

    /// <summary>The world bot with character <paramref name="id"/>, else the one called <paramref name="name"/> (ignoring case); null when none or Bots:Social is off.</summary>
    public Player? Find(Guid id, string? name)
    {
        if (!SocialOn) return null;
        var trimmed = name?.Trim();
        if (id == Guid.Empty && string.IsNullOrEmpty(trimmed)) return null;
        foreach (var bot in bots.All)
            if (WorldBot(bot) && (id != Guid.Empty ? bot.Character.Id == id : string.Equals(bot.Character.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
                return bot;
        return null;
    }

    // ---- presence ----

    /// <summary>Bots:Party:City / Gatherer / Patrol / Drifter: whether a world bot of <paramref name="role"/> may join a party.</summary>
    private bool PartyKind(BotRole role) => role switch
    {
        BotRole.City => Options.PartyCity,
        BotRole.Gatherer => Options.PartyGatherer,
        BotRole.Patrol => Options.PartyPatrol,
        BotRole.Drifter => Options.PartyDrifter,
        _ => false,
    };

    /// <summary>
    /// Whether world bot <paramref name="bot"/> accepts <paramref name="inviter"/>'s party invite (bot-party, Bots:Party:*): Bots:Party:Enabled
    /// (under Social and the master), its kind allowed, standing (not knocked down or defeated) and out of a fight, in no party, the inviter a
    /// human on its map, its gear tier within Bots:Party:TierRange of the inviter's (<see cref="Companions.TierOf"/>), the inviter's party with
    /// fewer than Bots:Party:MaxPerParty world bots; then with Bots:Party:AcceptChance. Else it declines as before.
    /// </summary>
    public bool WantsParty(Player bot, Player inviter)
    {
        if (!PartyOn || bot.Bot is not { } info || !WorldBot(bot) || !PartyKind(info.Role) || info.Fight.Active || bot.Combat.Down is not null
            || bot.Combat.InCombat || inviter.IsBot || inviter.Map is null || inviter.Map != bot.Map || world.Parties.Of(bot.Character.Id) is not null) return false;
        if (Math.Abs(info.Tier - Companions.TierOf(inviter)) > Options.PartyTierRange) return false;
        var bots = world.Parties.Of(inviter.Character.Id) is { } party ? world.Companions.RecruitsIn(party) : 0;
        if (bots >= Options.PartyMaxPerParty) return false;
        return Random.NextDouble() < Options.PartyAcceptChance;
    }

    /// <summary>
    /// <paramref name="inviter"/>'s PartyInvitation went to <paramref name="bot"/> (<see cref="Parties.Invite"/> stored it as for a player): after
    /// Bots:Social:ReplyDelaySeconds the bot answers it as a client would (PartyAnswerInvitation: declined via the UI, or accepted through
    /// <see cref="WantsParty"/>), when the invitation is still this inviter's.
    /// </summary>
    public void PartyInvited(Player bot, Player inviter)
    {
        var from = inviter.Character.Id;
        Later(ReplyDelay(), () =>
        {
            if (world.Parties.InviteOf(bot.Character.Id)?.Inviter != from) return; // timed out, or another invitation meanwhile
            var host = world.Parties.Online(from); // the inviter's joined player now (he may have changed maps meanwhile)
            var accept = host is not null && WantsParty(bot, host);
            var rc = world.Parties.AnswerInvitation(bot, accept, auto: false, accept ? Parties.NoReason : Parties.DeclinedViaUi);
            if (accept && rc == 0) world.Companions.Recruit(bot, host!); // bot-party: it joined as the inviter's member; now it plays as his companion
        });
    }

    /// <summary>Whether <paramref name="player"/> has a friend request to <paramref name="bot"/> waiting for its answer (FriendInvite 10642).</summary>
    public bool FriendAsked(Guid player, Guid bot) => _friendAsks.Contains((player, bot));

    /// <summary>
    /// <paramref name="player"/> asked <paramref name="bot"/> to be friends (answered RC 0 with the bot's entry, nothing stored): after
    /// Bots:Social:ReplyDelaySeconds FriendRequestAnswered 296 {0 the bot, 10 false} to the player's joined player, which drops the request.
    /// </summary>
    public void FriendInvited(Player player, Player bot)
    {
        var key = (player.Character.Id, bot.Character.Id);
        _friendAsks.Add(key);
        Later(ReplyDelay(), () =>
        {
            if (!_friendAsks.Remove(key)) return;
            world.Parties.Online(key.Item1)?.Session.Send(new FriendRequestAnsweredEvent { cbx = key.Item2 });
            Log.Info("bots", $"'{bot.Character.Name}' declines the friend request of '{player.Character.Name}'");
        });
    }

    /// <summary>
    /// A whisper of <paramref name="sender"/> reached <paramref name="bot"/> (RC 0 already): with Bots:Social:WhisperReplyChance, and once per
    /// sender in <see cref="WhisperCooldown"/>, a ChatWhisper 75 {0 the bot, 3 a Chat.Whisper line, 4 role 0} to the sender's chat connection
    /// after Bots:Social:ReplyDelaySeconds.
    /// </summary>
    public void Whispered(Guid sender, string senderName, Player bot)
    {
        var key = (bot.Character.Id, sender);
        if (_whispered.TryGetValue(key, out var last) && Now - last < WhisperCooldown) return;
        _whispered[key] = Now;
        if (Random.NextDouble() >= Options.WhisperReplyChance || Pick(bots.Settings.WhisperLines) is not { } line) return;
        var name = bot.Character.Name;
        Later(ReplyDelay(), () =>
        {
            if (world.Chat.SessionOf(sender) is not { Active: true } s) return;
            s.Send(new ChatWhisperEvent { bxv = name, bxy = line, Extra = new() { [4] = (byte)0 } });
            Log.Info("bots", $"'{name}' answers the whisper of '{senderName}'");
        });
    }

    // ---- local chat ----

    /// <summary>
    /// <paramref name="bot"/> says <paramref name="text"/> on the local channel: ChatSay 74 {0 name, 1 text, 2 byte 0, 4 object id, 5 Guid} to
    /// the players that see it, when Bots:Chat is on, someone sees it, the bot spoke not within <see cref="BotLineCooldown"/> and its
    /// cluster had fewer than Bots:Chat:MaxLinesPerMinutePerCluster lines in the last minute. Whether it was said.
    /// </summary>
    public bool TrySay(Player bot, string? text)
    {
        if (!ChatOn || string.IsNullOrWhiteSpace(text) || bot.Map is not { } map || bot.Observers.Count == 0) return false;
        var now = Now;
        if (_botLine.TryGetValue(bot.Id, out var last) && now - last < BotLineCooldown) return false;
        var times = _clusterLines.TryGetValue(map.Cluster, out var q) ? q : _clusterLines[map.Cluster] = new Queue<TimeSpan>();
        while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromMinutes(1)) times.Dequeue();
        if (times.Count >= Options.MaxLinesPerMinutePerCluster) return false;
        times.Enqueue(now);
        _botLine[bot.Id] = now;
        bot.SendToObservers(new ChatSayEvent
        {
            bxl = bot.Character.Name, bxm = text.Trim(), bxp = bot.Id, bxq = bot.Character.Id,
            Extra = new() { [2] = (byte)0 }, // as a player's Say line (WorldSession.ChatSay)
        });
        Log.Debug("bots", $"'{bot.Character.Name}' says: {text}");
        return true;
    }

    /// <summary>
    /// A player's emote was shown (<see cref="Emotes.Play"/>): with Bots:Chat:EmoteReplies the city bot it targets, else the nearest one within
    /// <see cref="EmoteRange"/> that the player sees, answers with the emote of Chat.EmoteReplies after 1-2.5 s (once in
    /// <see cref="BotLineCooldown"/>), a third of the time with a Chat.Greet line.
    /// </summary>
    public void PlayerEmoted(Player p, int emote, long target)
    {
        if (!ChatOn || !Options.EmoteReplies || p.Bot is not null || p.Map is not { } map || map.Bots.Count == 0
            || !bots.Settings.EmoteReplies.TryGetValue(emote, out var reply)) return;
        var at = p.PositionAt(DateTime.UtcNow);
        Player? bot = null;
        if (target != 0 && map.Bots.TryGetValue(target, out var aimed) && aimed.Bot?.Role == BotRole.City) bot = aimed;
        else
        {
            var best = EmoteRange;
            foreach (var b in map.Bots.Values)
            {
                if (b.Bot?.Role != BotRole.City || !b.Observers.Contains(p)) continue;
                var d = Vector2.Distance(b.PositionAt(bots.Time), at);
                if (d <= best) (bot, best) = (b, d);
            }
        }
        if (bot is null || bot.Bot!.Fight.Active || _botEmote.TryGetValue(bot.Id, out var last) && Now - last < BotLineCooldown) return;
        _botEmote[bot.Id] = Now;
        var greet = Random.NextDouble() < 1 / 3.0 ? Pick(bots.Settings.GreetLines) : null;
        Later(TimeSpan.FromSeconds(1 + Random.NextDouble() * 1.5), () =>
        {
            if (!WorldBot(bot) || bot.Map != map || bot.Bot!.Fight.Active) return;
            bots.Emote(bot, reply);
            if (greet is not null) TrySay(bot, greet);
        });
    }

    /// <summary>
    /// A fight of world bot <paramref name="bot"/> ended: it knocked a player down (<paramref name="won"/>) or was beaten. With
    /// Bots:Chat:FightLines, half the time, a Chat.Win / Chat.Loss line 1-3 s later.
    /// </summary>
    public void FightEnded(Player bot, bool won)
    {
        if (!ChatOn || !Options.FightLines || bot.Bot?.Role is not (BotRole.Gatherer or BotRole.Patrol or BotRole.Drifter) || Random.NextDouble() >= 0.5) return;
        if (Pick(won ? bots.Settings.WinLines : bots.Settings.LossLines) is not { } line) return;
        Later(TimeSpan.FromSeconds(1 + Random.NextDouble() * 2), () => TrySay(bot, line));
    }

    // ---- the tick ----

    /// <summary>The answers that are due, then every <see cref="ChatterEvery"/> the city chatter of the city bots a player sees.</summary>
    public void Update(TimeSpan now)
    {
        if (_later.Count > 0)
        {
            var due = _later.Where(x => x.Due <= now).ToList();
            if (due.Count > 0)
            {
                _later.RemoveAll(x => x.Due <= now);
                foreach (var (_, act) in due) act();
            }
        }
        if (now < _nextChatter) return;
        _nextChatter = now + ChatterEvery;
        // Old entries go once in a while: a bot's last line, whispers answered.
        foreach (var id in _botLine.Where(x => now - x.Value >= BotLineCooldown).Select(x => x.Key).ToList()) _botLine.Remove(id);
        foreach (var id in _botEmote.Where(x => now - x.Value >= BotLineCooldown).Select(x => x.Key).ToList()) _botEmote.Remove(id);
        foreach (var k in _whispered.Where(x => now - x.Value >= WhisperCooldown).Select(x => x.Key).ToList()) _whispered.Remove(k);
        if (!ChatOn || Options.SayChancePerMinute <= 0 || bots.Settings.SayLines.Count == 0) return;
        // The chance per roll of a per-minute chance (1 = a line at every roll, the cluster cap still holding).
        var chance = 1 - Math.Pow(1 - Options.SayChancePerMinute, ChatterEvery.TotalMinutes);
        foreach (var map in world.Maps.Maps)
        {
            if (map.HumanCount == 0 || map.Bots.Count == 0) continue;
            foreach (var bot in map.Bots.Values.ToList())
                if (bot.Bot is { Role: BotRole.City, Fight.Active: false } && bot.Observers.Count > 0 && Random.NextDouble() < chance)
                    TrySay(bot, Pick(bots.Settings.SayLines));
        }
    }
}
