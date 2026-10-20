using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>A registration's state (the client's de4 states 2 Invited, 3 Registered, 5 Ready, 6 Entering; SPEC §2.3).</summary>
public enum ExpeditionState
{
    Registered,

    /// <summary>A party member the leader's group registration covers, not answered yet (262 and 263 kind 2; SPEC §3.3).</summary>
    Invited,
    Ready,
    Entering,
}

/// <summary>
/// One character's registration at an agent (SPEC §8 ExpeditionRegistration): the category and the expedition chosen, the agent it stands
/// at, the ready time, and the Join channel while it runs.
/// </summary>
public sealed class ExpeditionRegistration
{
    public required Guid Character { get; init; }
    public required ExpeditionData.Category Category { get; init; }

    /// <summary>The expedition; a queued group registration takes the one of the group it joins (<see cref="ExpeditionGroup"/>).</summary>
    public required ExpeditionData.Expedition Expedition { get; set; }

    /// <summary>The hardcore level (1-18), 0 for every other category.</summary>
    public int Difficulty { get; init; }

    /// <summary>The group of a Veteran or Hardcore registration (a leader's party, or the queue); null for Standard (solo).</summary>
    public ExpeditionGroup? Group { get; set; }

    /// <summary>The hardcore token that opens <see cref="Difficulty"/> beyond the ladder (consumed when the player goes in); null = none needed.</summary>
    public string? Token { get; init; }

    /// <summary>Whether the expedition is fixed (chosen in the registration, or opened by <see cref="Token"/>): a queue merges it only into a group of that expedition.</summary>
    public bool Pinned { get; init; }

    /// <summary><see cref="World.Clock"/> time an invitation runs out (<see cref="ExpeditionState.Invited"/>, Expeditions:InvitationTimeoutSeconds).</summary>
    public TimeSpan InviteUntil { get; set; }
    public required string AgentCluster { get; init; }
    public required Vector2 AgentPosition { get; init; }
    public ExpeditionState State { get; set; }

    /// <summary>GameTimeStamp of the registration and of the ready message (263 key 4).</summary>
    public long RegisteredTicks { get; init; }
    public long ReadyTicks { get; set; }

    /// <summary><see cref="World.Clock"/> time the Ready state began (the ready timeout counts from it).</summary>
    public TimeSpan ReadySince { get; set; }

    /// <summary>The Join channel: when it ends and where the player stood when it began.</summary>
    public TimeSpan EnterDue { get; set; }
    public Vector2 EnterFrom { get; set; }

    /// <summary>The health when the Join channel began: lower later means the player was hit (§2.5: damage cancels).</summary>
    public float EnterHealth { get; set; }
}

/// <summary>
/// The players who registered a Veteran or Hardcore category together (SPEC §3.3, §12.1): a party leader's registration with the party
/// members who accepted, or the registrations of one category (and level) queued within Expeditions:GroupQueueSeconds. They go into one
/// instance (made by the first who goes in), its mobs scaled for their number.
/// </summary>
public sealed class ExpeditionGroup
{
    public required Guid Leader { get; init; }
    public required ExpeditionData.Category Category { get; init; }
    public required ExpeditionData.Expedition Expedition { get; init; }
    public int Difficulty { get; init; }

    /// <summary>The registrations still in the group (the leader first); a declined, timed-out or cancelled one leaves it.</summary>
    public List<ExpeditionRegistration> Members { get; } = [];

    /// <summary>A party leader's group (else a queued one): a member leaving the party leaves it (<see cref="Expeditions.PartyLeft"/>).</summary>
    public bool FromParty { get; init; }

    /// <summary>A queued group (no party): <see cref="World.Clock"/> time the queue closes; null for a party's group or once it closed.</summary>
    public TimeSpan? QueueUntil { get; set; }

    /// <summary>The group's instance once its first member went in.</summary>
    public ExpeditionInstance? Instance { get; set; }

    public override string ToString() => $"{Category.Name} group of {Members.Count}";
}

/// <summary>
/// One expedition instance (SPEC §8 ExpeditionInstance): its id "@EXPEDITION@&lt;guid&gt;" and world.xml cluster, the expedition, the
/// members with their way back, the objectives (step, counts, flags), the times, the active checkpoint and the end.
/// </summary>
public sealed class ExpeditionInstance
{
    public required Guid Guid { get; init; }
    public string Id => ExpeditionData.InstanceId(Guid);
    public required ExpeditionData.Expedition Expedition { get; init; }
    public required ExpeditionData.Category Category { get; init; }
    public int Difficulty { get; init; }

    /// <summary>The players it was made for (a group's size when its first member went in; 1 solo): its mobs' scaling (§12.1).</summary>
    public int Players { get; init; } = 1;

    /// <summary>The factors on its mobs' max health and damage (Expeditions:GroupScaling for a group category; 1 for Standard).</summary>
    public double HealthFactor { get; init; } = 1;
    public double DamageFactor { get; init; } = 1;

    /// <summary>The world.xml cluster the instance is a copy of (ChangeCluster key 1, SelectCharacter key 2).</summary>
    public required string World { get; init; }

    /// <summary>Where a player comes in: the template's startpoint (Join keys 9 and 10).</summary>
    public required Arrival Start { get; init; }

    /// <summary>The characters who entered, with the way back each saved (the return point of SPEC §2.5).</summary>
    public Dictionary<Guid, DungeonReturn> Members { get; } = [];

    /// <summary>Characters sent in and not joined yet, with the clock time until which each counts as inside.</summary>
    public Dictionary<Guid, TimeSpan> Arriving { get; } = [];

    public int Step { get; set; }
    public int[] Counts { get; set; } = [];
    public bool[] Done { get; set; } = [];

    /// <summary>GameTimeStamp of the start (FullExpeditionInfo key 2) and the clock time of it (timeout, bonus time).</summary>
    public required long StartTicks { get; init; }
    public required TimeSpan StartedAt { get; init; }

    public ExpeditionCheckPoint? Active { get; set; }
    public bool Finished { get; set; }
    public TimeSpan? FinishedAt { get; set; }
    public TimeSpan? EmptySince { get; set; }
    public bool Closed { get; set; }

    /// <summary>The members not on the map at the completion: each gets its rewards when it joins the finished instance (once).</summary>
    public HashSet<Guid> Owed { get; } = [];

    /// <summary>The instance map's exits, narrators and checkpoints once it is filled (<see cref="Expeditions.Populate"/>).</summary>
    public List<ExpeditionObject> Objects { get; } = [];

    public IReadOnlyList<ExpeditionData.KillTask> Tasks => Expedition.Steps[Step];

    public override string ToString() => $"expedition {Expedition.Name} {Id}";
}

/// <summary>
/// Expeditions (analysis/work/stageH/expeditions/SPEC.md, "SPEC" here; PVE-DESIGN.md §2.G, §3 step 8): the registration at an agent
/// (ExpeditionRegister 271 and its info event 263, §2.1-§3.2), the Join channel (278/279, events 264/265, §2.5), the instance (the
/// ChangeCluster push into "@EXPEDITION@&lt;guid&gt;" with the world.xml cluster, §2.6; its exits, narrators and checkpoints, §2.7; the
/// mobs of its active layers through the usual spawning, spawned once), the objectives (FullExpeditionInfo 260, ExpeditionQuestProgressInfo
/// 261, §2.8, §5), the rewards (§5.1), the exits (ExitEnterStart 237, §2.10), the timeout and the closing (§7) and the checkpoint respawn
/// (§6). Only messages the unmodified client has. PVE-DESIGN step 9: Veteran and Hardcore for 1-5 players (a party leader's group with
/// 262 invitations and ready checks, or a short queue; §3.3), their mobs scaled by the group size (Expeditions:GroupScaling, §12.1), the
/// hardcore ladder of 18 levels with its mob buffs, factors and tokens (§12.2), the leave penalty off by default (§12.5).
/// The handlers are in Handlers/World/ExpeditionHandler; <see cref="World.Update"/> runs <see cref="Update"/>. Under <see cref="World.Sync"/>.
/// </summary>
public sealed class Expeditions(World world)
{
    /// <summary>a62 values (ExpeditionRegisterResult, SPEC §2.9): the answer's RC is 10843 + a62; 1 = OK.</summary>
    public const byte Ok = 1, OutOfRange = 2, LowItemPower = 7, Penalty = 8, TooManyPartyMembers = 12, AlreadyRegistered = 13, NoExpedition = 15,
        InvalidPartyComposition = 16, DidntArriveInTime = 19, DidntAcceptInTime = 20, NoToken = 21, CanceledBy = 23;

    /// <summary>The ReturnCode of a refused ExpeditionRegister: 10843 + a62 (cj8.af(g7q) 0x180B30180: a62 = (byte)RC − 0x5B).</summary>
    public static short RefusalCode(byte a62) => (short)(10843 + a62);

    /// <summary>The 263 message kinds (a6l, SPEC §2.3).</summary>
    public const int KindRegistered = 1, KindInvited = 2, KindEnded = 3, KindReady = 5, KindEntering = 6, KindInside = 7, KindCounts = 8;

    /// <summary>Seconds a character sent into an instance counts as inside until its Join (the client loads the cluster first).</summary>
    public const double ArrivalGraceSeconds = 120;

    private readonly Dictionary<Guid, ExpeditionRegistration> _registrations = [];
    private readonly Dictionary<string, ExpeditionInstance> _instances = new(StringComparer.Ordinal);

    /// <summary>The exit channels under way: the player, the exit, when it ends, where it began and the health then (§2.10: damage cancels).</summary>
    private readonly Dictionary<Player, (ExpeditionExit Exit, TimeSpan Due, Vector2 From, float Health)> _exiting = [];

    private ExpeditionSettings Settings => world.ExpeditionSettings;

    private Random Rng => _rng ??= world.MapSeed is { } seed ? new Random(seed) : new Random();
    private Random? _rng;

    public IReadOnlyCollection<ExpeditionInstance> Instances => _instances.Values;

    public ExpeditionRegistration? RegistrationOf(Guid character) => _registrations.GetValueOrDefault(character);

    /// <summary>The live instance of id <paramref name="cluster"/>; null for any other cluster or a closed one (also while its <see cref="Close"/> runs).</summary>
    public ExpeditionInstance? Find(string? cluster) => cluster is not null && _instances.TryGetValue(cluster, out var i) && !i.Closed ? i : null;

    /// <summary>Whether <paramref name="p"/> lost health since <paramref name="health"/> (regeneration only raises it): a channel's damage cancel.</summary>
    private bool Hit(Player p, float health) => p.Health.At(world.GameNow) < health - 0.5f;

    public bool IsLive(string cluster) => _instances.ContainsKey(cluster);

    /// <summary>The world.xml cluster a live instance id is a copy of; null for any other id.</summary>
    public string? WorldOf(string cluster) => Find(cluster)?.World;

    /// <summary>The instance <paramref name="character"/> is a member of, if any.</summary>
    public ExpeditionInstance? InstanceOf(Guid character) => _instances.Values.FirstOrDefault(i => i.Members.ContainsKey(character));

    private ExpeditionData? Data => world.Clusters?.ExpeditionsIfLoaded;

    // ---- registration (SPEC §3.2) ----

    /// <summary>
    /// ExpeditionRegister (§3.2) for <paramref name="p"/> at agent object <paramref name="agentId"/>, category index <paramref name="category"/>,
    /// expedition index <paramref name="expedition"/> (-1 = the server picks) and <paramref name="difficulty"/> (a hardcore level; 0 = the
    /// highest open one): the a62 result (1 = registered), with <paramref name="why"/> for the log. Standard: 263 kind 1 and kind 5 right
    /// after (solo). Veteran/Hardcore (§3.3): a party leader's registration covers the party members near the agent (262 and 263 kind 2
    /// to them, kind 1 and the counts to the leader); without a party it is queued for Expeditions:GroupQueueSeconds (<see cref="Update"/>).
    /// </summary>
    public byte Register(Player p, long agentId, int category, int expedition, int difficulty, out string why)
    {
        var data = Data;
        if (!Settings.Enabled || data is null) return Refuse(NoExpedition, data is null ? "the expedition tables are not read" : "Expeditions:Enabled is off", out why);
        if (p.Map is not { } map || !map.Objects.TryGetValue(agentId, out var o) || o is not ExpeditionAgent agent)
            return Refuse(OutOfRange, $"object {agentId} is no expedition agent of {p.Map?.Cluster}", out why);
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, agent.Position) > Settings.RegisterRange)
            return Refuse(OutOfRange, string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, agent.Position):0.#} m from the agent"), out why);
        if (data.CategoryAt(category) is not { } cat || !data.CategoriesOf(agent.AgentId).Contains(category))
            return Refuse(NoExpedition, $"category {category} is not one of agent {agent.AgentId}'s", out why);
        if (Busy(p.Character.Id) is { } busy) return Refuse(AlreadyRegistered, busy, out why);
        if (Settings.CheckItemPower && cat.MinItemPower > 0 && AverageItemPower(p) is var ip && ip < cat.MinItemPower)
            return Refuse(LowItemPower, string.Create(CultureInfo.InvariantCulture, $"average item power {ip:0} < {cat.MinItemPower:0}"), out why);
        if (PenaltyLeft(p.Character) is { } penalty)
            return Refuse(Penalty, string.Create(CultureInfo.InvariantCulture, $"the leave penalty runs {penalty.TotalSeconds:0} s more"), out why);
        // §3.3: a group category registered by a party leader covers the party (the client refuses more members than the group size: 12).
        var party = cat.GroupSize > 1 && world.Parties.Of(p.Character.Id) is { } pa && pa.Leader == p.Character.Id && pa.Members.Count > 1 ? pa : null;
        if (party is not null && party.Members.Count > cat.GroupSize)
            return Refuse(TooManyPartyMembers, $"the party has {party.Members.Count} members, {cat.Name} takes {cat.GroupSize}", out why);
        var playable = data.Expeditions.Where(e => e.CategoryIndex == category && Playable(e)).ToList();
        var wanted = expedition >= 0 ? playable.FirstOrDefault(e => e.Index == expedition) : null;
        if (expedition >= 0 && wanted is null) return Refuse(NoExpedition, $"no playable expedition {expedition} in {cat.Name}", out why);
        // §12.2 the hardcore ladder: the level must be open to the registering player, or a token of it in the inventory opens it.
        var level = 0;
        string? token = null;
        if (cat.Difficulties.Count > 0)
        {
            var open = HardcoreLevel(p.Character, cat);
            // Ladder off (every level open): no level sent takes the character's recorded one (1 when none), not the hardest.
            level = difficulty > 0 ? Math.Min(difficulty, cat.Difficulties.Count)
                : Settings.HardcoreLadder ? open : Math.Clamp(p.Character.Expeditions?.Hardcore ?? 1, 1, cat.Difficulties.Count);
            if (level > open)
            {
                token = TokenFor(p, level, wanted, playable);
                if (token is null)
                    return Refuse(NoToken, $"hardcore level {level} is not open (the highest open one is {open}: complete level {open} to open the next)", out why);
                wanted ??= playable.First(e => token.EndsWith("_" + e.Name, StringComparison.Ordinal));
            }
        }
        var chosen = wanted;
        if (chosen is null)
        {
            // §3.2 step 7: at random among the category's playable ones, not the one done last when there is a choice [design].
            var last = p.Character.Expeditions?.Last;
            var pool = playable.Count > 1 ? [.. playable.Where(e => e.Name != last)] : playable;
            chosen = pool.Count == 0 ? null : pool[Rng.Next(pool.Count)];
        }
        if (chosen is null) return Refuse(NoExpedition, $"no playable expedition in {cat.Name}", out why);
        var now = world.GameNow;
        var r = new ExpeditionRegistration
        {
            Character = p.Character.Id, Category = cat, Expedition = chosen, Difficulty = level, Token = token, Pinned = expedition >= 0 || token is not null,
            AgentCluster = map.Cluster, AgentPosition = agent.Position, RegisteredTicks = now,
        };
        _registrations[p.Character.Id] = r;
        // The instance's cluster read now, while the player clicks Join (the push then finds it resolved).
        world.PrefetchCluster(chosen.World);
        why = $"{cat.Name}{(level > 0 ? $" level {level}" : "")}: {chosen.Name} ({chosen.World})";
        Log.Info("world", $"'{p.Character.Name}' registered for expedition {why}");
        Info(p, r, KindRegistered, now);
        if (cat.GroupSize <= 1)
        {
            MakeReady(p, r); // solo: ready at once (§1 row 5)
            return Ok;
        }
        var group = new ExpeditionGroup { Leader = p.Character.Id, Category = cat, Expedition = chosen, Difficulty = level, FromParty = party is not null };
        group.Members.Add(r);
        r.Group = group;
        if (party is null)
        {
            group.QueueUntil = world.Now + TimeSpan.FromSeconds(Settings.GroupQueueSeconds);
            Tell(p, string.Create(CultureInfo.InvariantCulture, $"Queued for {cat.Name}: others who register it within {Settings.GroupQueueSeconds:0} s go in with you"));
            return Ok;
        }
        Invite(p, r, party, agent);
        return Ok;
    }

    /// <summary>Why <paramref name="character"/> cannot register (§2.3: the client's de4.x does not block state 7, inside); null when it can.</summary>
    private string? Busy(Guid character) =>
        _registrations.ContainsKey(character) ? "registered already"
        : InstanceOf(character) is not null || ExpeditionData.IsInstance(world.Parties.Online(character)?.Map?.Cluster) ? "in an expedition already" : null;

    /// <summary>The leave penalty still running for <paramref name="c"/> (Expeditions:LeavePenalty); null when none.</summary>
    private TimeSpan? PenaltyLeft(Character c) =>
        Settings.LeavePenalty && c.Expeditions?.PenaltyUntil is { } until && until > DateTime.UtcNow.Ticks ? TimeSpan.FromTicks(until - DateTime.UtcNow.Ticks) : null;

    /// <summary>The highest hardcore level of <paramref name="cat"/> open to <paramref name="c"/> (Expeditions:HardcoreLadder; level 1 always, off: all).</summary>
    public int HardcoreLevel(Character c, ExpeditionData.Category cat) =>
        cat.Difficulties.Count == 0 ? 0 : !Settings.HardcoreLadder ? cat.Difficulties.Count : Math.Clamp(c.Expeditions?.Hardcore ?? 1, 1, cat.Difficulties.Count);

    /// <summary>The name of a hardcore token "QUESTITEM_EXP_TOKEN_D&lt;level&gt;_&lt;expedition&gt;" (loot.xml L&lt;n&gt;_REWARD_EXPEDITION_TOKEN).</summary>
    public static string TokenName(int level, ExpeditionData.Expedition e) => string.Create(CultureInfo.InvariantCulture, $"QUESTITEM_EXP_TOKEN_D{level}_{e.Name}");

    /// <summary>
    /// A token in <paramref name="p"/>'s bag that opens <paramref name="level"/> of <paramref name="wanted"/> (or of any of
    /// <paramref name="playable"/>), with Expeditions:HardcoreTokens; null when none.
    /// </summary>
    private string? TokenFor(Player p, int level, ExpeditionData.Expedition? wanted, IReadOnlyList<ExpeditionData.Expedition> playable)
    {
        if (!Settings.HardcoreTokens) return null;
        var names = (wanted is null ? playable : [wanted]).Select(e => TokenName(level, e)).ToHashSet(StringComparer.Ordinal);
        var bag = p.Inventory.Bag;
        for (var n = 0; n < bag.Size; n++)
            if (bag[n] is { } item && names.Contains(item.Template.Name)) return item.Template.Name;
        return null;
    }

    /// <summary>One <paramref name="token"/> out of <paramref name="p"/>'s bag (NewSimpleItem with the new count, or InventoryDeleteItem); false when it has none.</summary>
    private static bool TakeToken(Player p, string token)
    {
        var bag = p.Inventory.Bag;
        for (var n = 0; n < bag.Size; n++)
        {
            if (bag[n] is not { } item || item.Template.Name != token) continue;
            item.Count--;
            if (item.Count > 0) p.Session.Send(new NewSimpleItemEvent { dax = item.Id, day = item.Template.Index, daz = (short)item.Count });
            else
            {
                bag.Take(n);
                p.Session.Send(new InventoryDeleteItemEvent { ckr = item.Id });
            }
            p.SaveItems();
            return true;
        }
        return false;
    }

    /// <summary>Ready (263 kind 5, the ready time now): the Join button and the ready timeout (Expeditions:ReadyTimeoutSeconds).</summary>
    private void MakeReady(Player p, ExpeditionRegistration r)
    {
        r.State = ExpeditionState.Ready;
        r.ReadyTicks = world.GameNow;
        r.ReadySince = world.Now;
        Info(p, r, KindReady, r.ReadyTicks);
    }

    // ---- groups (SPEC §3.3) ----

    /// <summary>
    /// The leader's registration covers the party members on the agent's map within Expeditions:InviteRange of it who may register (online,
    /// not registered or inside, no leave penalty; with Expeditions:HardcorePartyCarry off, the leader's hardcore level open to them). With
    /// Expeditions:PartyAutoAccept (the default) each is registered at once (263 kind 1); else each gets InvitedToExpedition (262 {0 leader,
    /// 1 category, 4 expedition, 5 level}) and 263 kind 2, with Expeditions:InvitationTimeoutSeconds to answer (JoinExpedition 273 /
    /// DeclineExpeditionInvitation 274). Then the group settles (<see cref="Settle"/>: ready at once when nobody is left to answer).
    /// </summary>
    private void Invite(Player leader, ExpeditionRegistration lead, Party party, ExpeditionAgent agent)
    {
        var group = lead.Group!;
        var skipped = new List<string>();
        foreach (var m in party.Members)
        {
            if (m.Id == leader.Character.Id || m.Companion) continue; // stageN: companions follow their owner in, unregistered
            var q = world.Parties.Online(m.Id);
            var no = q is null ? "offline"
                : q.Map != leader.Map || Vector2.Distance(q.PositionAt(DateTime.UtcNow), agent.Position) > Settings.InviteRange ? "not at the agent"
                : Busy(m.Id) ?? (PenaltyLeft(q.Character) is not null ? "leave penalty"
                    : !Settings.HardcorePartyCarry && group.Difficulty > HardcoreLevel(q.Character, group.Category) ? "level locked" : null);
            if (no is not null)
            {
                skipped.Add($"{m.Name} ({no})");
                continue;
            }
            var auto = Settings.PartyAutoAccept;
            var r = new ExpeditionRegistration
            {
                Character = m.Id, Category = group.Category, Expedition = group.Expedition, Difficulty = group.Difficulty, Group = group,
                State = auto ? ExpeditionState.Registered : ExpeditionState.Invited,
                AgentCluster = lead.AgentCluster, AgentPosition = lead.AgentPosition, RegisteredTicks = world.GameNow,
                InviteUntil = world.Now + TimeSpan.FromSeconds(Settings.InvitationTimeoutSeconds),
            };
            _registrations[m.Id] = r;
            group.Members.Add(r);
            if (auto)
            {
                Info(q!, r, KindRegistered, world.GameNow);
                continue;
            }
            q!.Session.Send(new InvitedToExpeditionEvent
            {
                clz = leader.Character.Name,    // [0] the inviter
                cl0 = group.Category.Index,     // [1] [inf]
                cl3 = group.Expedition.Index,   // [4]
                cl4 = group.Difficulty,         // [5]
            });
            Info(q, r, KindInvited, world.GameNow);
        }
        if (skipped.Count > 0) Tell(leader, $"Not invited to the expedition: {string.Join(", ", skipped)}");
        Counts(group);
        Settle(group);
    }

    /// <summary>
    /// 263 kind 8 to every member of <paramref name="group"/>: 7 the members, 8 those who accepted, 9 those ready (the "{0}/{1}" counts of
    /// the registration HUD [inf]).
    /// </summary>
    private void Counts(ExpeditionGroup group)
    {
        var accepted = group.Members.Count(m => m.State != ExpeditionState.Invited);
        var ready = group.Members.Count(m => m.State is ExpeditionState.Ready or ExpeditionState.Entering);
        foreach (var m in group.Members)
            if (world.Parties.Online(m.Character) is { } q)
            {
                var e = InfoEvent(q, m.Category, m.Expedition, KindCounts, world.GameNow);
                (e.b6c, e.b6d, e.b6e) = (group.Members.Count, accepted, ready);
                q.Session.Send(e);
            }
    }

    /// <summary>
    /// JoinExpedition (273): an invited member accepts (263 kind 1, "wait for the others"); when nobody is left to answer the group settles.
    /// False when <paramref name="p"/> has no open invitation.
    /// </summary>
    public bool Accept(Player p)
    {
        if (!_registrations.TryGetValue(p.Character.Id, out var r) || r.State != ExpeditionState.Invited || r.Group is not { } group) return false;
        r.State = ExpeditionState.Registered;
        Info(p, r, KindRegistered, world.GameNow);
        Log.Info("world", $"'{p.Character.Name}' accepted the expedition invitation ({group})");
        Counts(group);
        Settle(group);
        return true;
    }

    /// <summary>
    /// Whether everyone of <paramref name="group"/> answered (no open invitation, the queue closed): then every member is ready (263 kind 5);
    /// one alone with Expeditions:SoloGroupCategories off ends with a62 16.
    /// </summary>
    private void Settle(ExpeditionGroup group)
    {
        if (group.QueueUntil is not null || group.Members.Count == 0 || group.Members.Any(m => m.State == ExpeditionState.Invited)) return;
        // StageN companions: a player with companions in its party is a group.
        if (group.Members.Count == 1 && !Settings.SoloGroupCategories && group.Instance is null && !world.Companions.HasAny(group.Members[0].Character))
        {
            var only = group.Members[0];
            if (world.Parties.Online(only.Character) is { } q) Tell(q, $"{group.Category.Name} needs at least two players here (Expeditions:SoloGroupCategories is off)");
            Drop(only, InvalidPartyComposition, null, "alone in a group category");
            return;
        }
        var made = false;
        foreach (var m in group.Members.Where(m => m.State == ExpeditionState.Registered).ToList())
            if (world.Parties.Online(m.Character) is { } q)
            {
                MakeReady(q, m);
                made = true;
            }
        if (made) Counts(group);
    }

    /// <summary>
    /// The queue of a group registered without a party is over (§3.3 [design]): the other queued groups of the same category and level join
    /// it (oldest first, up to the group size; they take its expedition, so one whose expedition is pinned, chosen or opened by a token, only
    /// when it is the same), then it settles.
    /// </summary>
    private void CloseQueue(ExpeditionGroup group)
    {
        group.QueueUntil = null;
        var others = _registrations.Values.Select(r => r.Group).OfType<ExpeditionGroup>().Distinct()
            .Where(g => g != group && g.QueueUntil is not null && g.Category == group.Category && g.Difficulty == group.Difficulty
                && (g.Expedition == group.Expedition || !g.Members.Any(m => m.Pinned)))
            .OrderBy(g => g.QueueUntil).ToList();
        foreach (var g in others)
        {
            if (group.Members.Count + g.Members.Count > group.Category.GroupSize) continue;
            foreach (var m in g.Members)
            {
                m.Group = group;
                m.Expedition = group.Expedition;
                group.Members.Add(m);
            }
            g.Members.Clear();
            g.QueueUntil = null;
        }
        if (group.Members.Count > 1) Log.Info("world", $"expedition queue: {group} for {group.Expedition.Name}");
        Settle(group);
    }

    /// <summary>
    /// <paramref name="r"/> ends (263 kind 3 with <paramref name="a62"/> and <paramref name="by"/> as key 12 while its player is in the world)
    /// and leaves its group: the others get the new counts, and the group settles without it.
    /// </summary>
    private void Drop(ExpeditionRegistration r, byte a62, string? by, string why)
    {
        _registrations.Remove(r.Character);
        var p = world.Parties.Online(r.Character);
        if (p is not null)
        {
            if (r.State == ExpeditionState.Entering) p.SendAround(new EnteringExpeditionCancelEvent { ddn = p.Id, b5i = false });
            Info(p, r, KindEnded, world.GameNow, a62, by);
        }
        Log.Info("world", $"'{p?.Character.Name ?? r.Character.ToString()}' expedition registration ended ({why})");
        Ungroup(r);
    }

    /// <summary><paramref name="r"/> out of its group (gone, or gone in): the others get the new counts and the group settles without it.</summary>
    private void Ungroup(ExpeditionRegistration r)
    {
        if (r.Group is not { } group) return;
        group.Members.Remove(r);
        r.Group = null;
        if (group.Members.Count == 0) return;
        Counts(group);
        Settle(group);
    }

    /// <summary>
    /// <paramref name="character"/> left its party (Parties.Remove): its registration in the party's group ends (§3.3: 263 kind 3, a62 23
    /// with its own name, which the client shows as nothing); the others of the group go on. A queued registration is not touched.
    /// </summary>
    public void PartyLeft(Guid character)
    {
        if (!_registrations.TryGetValue(character, out var r) || r.Group is not { FromParty: true, Members.Count: > 1 }) return;
        Drop(r, CanceledBy, world.Parties.Online(character)?.Character.Name, "left the party");
    }

    private static byte Refuse(byte a62, string text, out string why)
    {
        why = text;
        return a62;
    }

    /// <summary>Whether the world.xml cluster of <paramref name="e"/> is known (§4.3: 26 playable; 0-2, 19 and 30 are not).</summary>
    public bool Playable(ExpeditionData.Expedition e) =>
        ExpeditionData.Playable(e, id => world.Clusters?.WorldIfLoaded?.Find(id) is not null); // world.xml is in: the agent's city was resolved

    /// <summary>
    /// The plain average itempower of the gear (main hand, off hand, head, armor, shoes, cape; a two-handed weapon counts twice), for
    /// Expeditions:CheckItemPower: lower than the client's figure (no quality, mastery or enchantment bonus).
    /// </summary>
    public static double AverageItemPower(Player p)
    {
        var e = p.Inventory.Equipment;
        double Ip(int slot) => e[slot]?.Template.ItemPower ?? 0;
        var main = e[0];
        var off = e[1] is null && main?.Template.Attrs.GetValueOrDefault("twohanded") == "true" ? Ip(0) : Ip(1);
        return (Ip(0) + off + Ip(2) + Ip(3) + Ip(4) + Ip(6)) / 6;
    }

    /// <summary>
    /// ExpeditionRegisterCancel (272) / DeclineExpeditionInvitation (274): the registration ends, 263 kind 3 with a62 23 and key 12 = the
    /// player's own name (the client shows nothing for it, §2.4). A running Join channel ends with 265 {1: false}. A party leader who cancels
    /// while invitations are open ends the whole group (the others: a62 23 "cancelled by {leader}"); else the group goes on without it.
    /// </summary>
    public void Cancel(Player p, string why)
    {
        if (!_registrations.TryGetValue(p.Character.Id, out var r)) return;
        if (r.Group is { FromParty: true } group && group.Leader == p.Character.Id && group.Members.Any(m => m.State == ExpeditionState.Invited))
            foreach (var m in group.Members.Where(m => m != r).ToList()) Drop(m, CanceledBy, p.Character.Name, $"the leader {why}");
        Drop(r, CanceledBy, p.Character.Name, why);
    }

    /// <summary>
    /// ExpeditionRegistrationInfo (263, §2.3) to <paramref name="p"/>: key 0 its object id, 1 the category's uniquename [inf], 2 the category
    /// index, 3 the kind, 4 the time, 5 the result (1 unless an error: never 0), 10 the expedition index, 11 = 1 when the daily bonus is used.
    /// </summary>
    private void Info(Player p, ExpeditionRegistration r, int kind, long ticks, byte a62 = Ok, string? by = null) =>
        p.Session.Send(InfoEvent(p, r.Category, r.Expedition, kind, ticks, a62, by));

    private ExpeditionRegistrationInfoEvent InfoEvent(Player p, ExpeditionData.Category cat, ExpeditionData.Expedition e, int kind, long ticks, byte a62 = Ok, string? by = null) => new()
    {
        ddn = p.Id,                                 // [0]
        b56 = cat.Name,                             // [1] [inf]
        b57 = cat.Index,                            // [2]
        b58 = kind,                                 // [3] a6l
        b59 = ticks,                                // [4] GameTimeStamp
        b6a = a62,                                  // [5] 1 = OK
        b6f = e.Index,                              // [10]
        b6g = BonusAvailable(p.Character, e) ? 0 : 1, // [11] < 1: the reward popup shows the bonus list
        b6h = by,                                   // [12]
    };

    // ---- entering (SPEC §2.5) ----

    /// <summary>
    /// EnteringExpeditionStart (278): Ready, still within Expeditions:EnterRange of the agent on its map, alive and not in combat → 263 kind 6, 264 to
    /// the others in view, and the channel of Expeditions:EnteringSeconds; null. Else why not (265 {1: false} and kind 5 again).
    /// </summary>
    public string? StartEntering(Player p)
    {
        if (!_registrations.TryGetValue(p.Character.Id, out var r) || r.State != ExpeditionState.Ready) return "not ready for an expedition";
        var at = p.PositionAt(DateTime.UtcNow);
        string? why = p.Map?.Cluster != r.AgentCluster ? "not in the agent's cluster"
            : Vector2.Distance(at, r.AgentPosition) > Settings.EnterRange ? string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, r.AgentPosition):0.#} m from the agent")
            : p.Combat.Down is { } down ? $"knocked down ({down.Kind})"
            : p.Combat.InCombat ? "in combat" : null;
        if (why is not null)
        {
            p.SendAround(new EnteringExpeditionCancelEvent { ddn = p.Id, b5i = false });
            Info(p, r, KindReady, r.ReadyTicks);
            return why;
        }
        r.State = ExpeditionState.Entering;
        r.EnterDue = world.Now + TimeSpan.FromSeconds(Settings.EnteringSeconds);
        r.EnterFrom = at;
        r.EnterHealth = p.Health.At(world.GameNow);
        Info(p, r, KindEntering, world.GameNow);
        p.SendToObservers(new EnteringExpeditionStartEvent { ddn = p.Id });
        world.PrefetchCluster(r.Expedition.World);
        return null;
    }

    /// <summary>EnteringExpeditionCancel (279), or a channel broken by a move, damage or a knockdown: 265 {1: false}, back to Ready (263 kind 5, the old ready time).</summary>
    public void CancelEntering(Player p, string why)
    {
        if (!_registrations.TryGetValue(p.Character.Id, out var r) || r.State != ExpeditionState.Entering) return;
        r.State = ExpeditionState.Ready;
        p.SendAround(new EnteringExpeditionCancelEvent { ddn = p.Id, b5i = false });
        Info(p, r, KindReady, r.ReadyTicks);
        Log.Info("world", $"'{p.Character.Name}' expedition Join channel cancelled ({why})");
    }

    /// <summary>
    /// The channel is over (§2.5): 265 {1: true} and the lock spell to the player and those in view, the return point saved (the player's
    /// position and cluster now), the instance (a group's: the one its first member made, else a new one), and the ChangeCluster push
    /// {0: "@EXPEDITION@&lt;guid&gt;", 1: world.xml cluster}. A hardcore level opened by a token takes the token now (gone: a62 21).
    /// </summary>
    private void Enter(Player p, ExpeditionRegistration r)
    {
        if (r.Token is { } token && !TakeToken(p, token))
        {
            Tell(p, $"The token {token} is no longer in the inventory");
            Drop(r, NoToken, null, $"token {token} gone");
            return;
        }
        _registrations.Remove(p.Character.Id);
        var at = p.PositionAt(DateTime.UtcNow);
        var ticks = world.GameNow;
        p.SendAround(new EnteringExpeditionCancelEvent { ddn = p.Id, b5i = true });
        if (Settings.LockSpell.Length > 0 && world.Spells?.IdOf(Settings.LockSpell) is { } lockSpell and >= 0)
            p.SendAround(new CastSpellEvent { ddn = p.Id, bvw = p.Id, bvx = at, bvy = lockSpell, bvz = ticks, bv0 = ticks, Extra = new() { [6] = (byte)0, [7] = (short)0 } });
        var group = r.Group;
        // A group's instance completed meanwhile (a late member): a new one for those still in the group, not a run without a reward.
        var instance = group?.Instance is { Closed: false, Finished: false } made ? made : Create(r.Expedition, r.Category, r.Difficulty, group?.Members.Count ?? 1);
        if (group is not null)
        {
            group.Instance = instance;
            Ungroup(r);
        }
        var back = new DungeonReturn(p.Map!.Cluster, at.X, at.Y, p.Facing);
        Send(p, instance, back, $"expedition:{instance.Id}");
    }

    /// <summary>
    /// A new instance of <paramref name="e"/> (one new Guid each, §2.6) for <paramref name="players"/> (a group category's mobs scaled
    /// for them: Expeditions:GroupScaling, §12.1): its start point from the world cluster when it is resolved (else the world.xml
    /// startpoint's place is taken at the fill), the objectives at step 0, started now.
    /// </summary>
    public ExpeditionInstance Create(ExpeditionData.Expedition e, ExpeditionData.Category cat, int difficulty = 0, int players = 1)
    {
        var guid = Guid.NewGuid();
        var id = ExpeditionData.InstanceId(guid);
        var start = world.CachedCluster(e.World) is { } template ? StartOf(template) with { Cluster = id } : new Arrival(id, Vector2.Zero, 0);
        var scale = cat.GroupSize > 1 ? Settings.GroupFactor(players) : 1;
        var instance = new ExpeditionInstance
        {
            Guid = guid, Expedition = e, Category = cat, Difficulty = difficulty, World = e.World, StartTicks = world.GameNow, StartedAt = world.Now, Start = start,
            Players = Math.Max(1, players), HealthFactor = scale, DamageFactor = Settings.ScaleDamage ? scale : 1,
        };
        SetStep(instance, 0);
        _instances[instance.Id] = instance;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{instance}: {cat.Name}{(difficulty > 0 ? $" level {difficulty}" : "")} in {e.World} for {instance.Players} player(s)"
            + $" (mobs: health x{instance.HealthFactor:0.##}, damage x{instance.DamageFactor:0.##}), {e.Steps.Count} step(s), timeout {e.Timeout} s, bonus {e.BonusTimeout} s"));
        return instance;
    }

    /// <summary>The hardcore level of <paramref name="i"/> (the category's difficultylevels entry); null for every other category.</summary>
    public static ExpeditionData.Difficulty? LevelOf(ExpeditionInstance i) => i.Difficulty > 0 ? i.Category.Difficulties.FirstOrDefault(d => d.Level == i.Difficulty) : null;

    /// <summary>The template's startpoint (a critical member of type startpoint, = the world.xml startpoint, §2.6), else its default spawn.</summary>
    private static Arrival StartOf(ClusterTemplate t) =>
        t.Critical.FirstOrDefault(m => m.Type == "startpoint") is { } s ? new Arrival(t.Id, s.Position, s.Direction) : new Arrival(t.Id, Transfers.DefaultSpawn(t), 0);

    /// <summary>
    /// <paramref name="p"/> goes into <paramref name="instance"/> at its start point (a member from now on, with <paramref name="back"/> as its
    /// way back, saved with the character for a restart: <see cref="Character.DungeonReturn"/>): the push {0: id, 1: world}.
    /// </summary>
    public void Send(Player p, ExpeditionInstance instance, DungeonReturn back, string via)
    {
        var start = instance.Start;
        if (start.Position == Vector2.Zero && world.CachedCluster(instance.World) is { } t) start = StartOf(t) with { Cluster = instance.Id };
        instance.Members[p.Character.Id] = back;
        instance.Arriving[p.Character.Id] = world.Now + TimeSpan.FromSeconds(ArrivalGraceSeconds);
        instance.EmptySince = null;
        p.Character.DungeonReturn = back;
        p.Session.Teleport(start, via, instance.World);
    }

    // ---- inside (SPEC §2.7, §2.8, §5) ----

    /// <summary>
    /// At the fill of an instance map (MapSpawner): its exits, narrators and checkpoints (cluster-wide, never left); the map's mobs spawn
    /// once (clustertype mobspawns defaulttimefactor 9999), × the instance's group scaling, and on a hardcore level with its mobmodifier
    /// (MOB_HARDCORE_EXPEDITION_BUFF_nn, as a random dungeon level's: <see cref="SpawnManager.LevelBuff"/>) and its silver, loot and fame
    /// factors on the kills (<see cref="Map.Difficulty"/>); a task whose mob has no spawn point in the instance starts done (§4.3 safety net).
    /// </summary>
    internal IEnumerable<WorldObject> Populate(Map map, ClusterTemplate template)
    {
        if (Find(map.Cluster) is not { } instance) return [];
        map.NoRespawn = true;
        (map.MobHealthFactor, map.MobDamageFactor) = (instance.HealthFactor, instance.DamageFactor);
        if (LevelOf(instance) is { } level)
            map.Difficulty = new DungeonDifficulty(level.MobModifier.Length > 0 ? level.MobModifier : null, level.Silver, level.Loot, level.MobFame);
        foreach (var o in template.ExpeditionObjects)
        {
            ExpeditionObject made = o.Kind switch
            {
                "expeditionexit" => new ExpeditionExit(world.ObjectIds.Next(), o),
                "expeditionnarrator" => new ExpeditionNarrator(world.ObjectIds.Next(), o),
                _ => new ExpeditionCheckPoint(world.ObjectIds.Next(), o),
            };
            instance.Objects.Add(made);
        }
        var spawned = template.MobSpawnPoints.Select(m => m.Template?.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < instance.Tasks.Count; i++)
            if (instance.Tasks[i] is { Mob: { } mob } task && !spawned.Contains(mob) && !instance.Done[i])
            {
                Log.Warn("world", $"{instance}: task {task} has no spawn point in {template.Id}, counted as done");
                instance.Counts[i] = task.Count;
                instance.Done[i] = true;
            }
        return instance.Objects;
    }

    /// <summary>
    /// After JoinFinished (WorldSession): in an instance, 263 kind 7, FullExpeditionInfo (260) and ExpeditionQuestProgressInfo (261), in that
    /// order (a 261 before the Join's 260 is dropped by the client, §2.8); its arrival is over; a member owed the completion's rewards (not
    /// on the map then: a checkpoint reload, a relog) gets them now. Anywhere, the reward items that did not fit earlier go into the
    /// inventory as far as they fit now.
    /// </summary>
    public void Joined(Player p)
    {
        GiveUnclaimed(p);
        if (p.Map is not { } map || Find(map.Cluster) is not { } instance) return;
        instance.Arriving.Remove(p.Character.Id);
        instance.EmptySince = null;
        if (!instance.Members.ContainsKey(p.Character.Id)) // a GM's .tele, a relog after a restart cannot come here (the instance would be gone)
            instance.Members[p.Character.Id] = p.Character.DungeonReturn ?? new DungeonReturn(Settings.FallbackCluster, 0, 0, 0);
        p.Session.Send(InfoEvent(p, instance.Category, instance.Expedition, KindInside, world.GameNow));
        p.Session.Send(new FullExpeditionInfoEvent
        {
            ddn = p.Id,                         // [0] own id: ignored otherwise
            cdb = instance.Expedition.Index,    // [1]
            cdc = instance.StartTicks,          // [2] GameTimeStamp of the start: the tracker counts down to start + timeout
            cdd = instance.Expedition.Narrator, // [3] [inf]
        });
        p.Session.Send(Progress(p, instance, -1, -1)); // the checkpoints' states came with their 315 in phase B
        if (instance.Finished && instance.Owed.Remove(p.Character.Id)) Reward(p, instance);
    }

    /// <summary>
    /// ExpeditionQuestProgressInfo (261, §2.8) for <paramref name="p"/>: key 2 the step, 3 the counts of its tasks as JSON object[][]
    /// ("[[4],[0],[0]]"), 4 one done flag per task (a short array would read as finished), 5/6 the step and task just counted (else -1).
    /// </summary>
    private static ExpeditionQuestProgressInfoEvent Progress(Player p, ExpeditionInstance i, int step, int task) => new()
    {
        ddn = p.Id,                                                                         // [0]
        b50 = i.Guid,                                                                       // [1] [inf]
        b51 = i.Step,                                                                       // [2]
        b52 = "[" + string.Join(",", i.Counts.Select(c => "[" + c.ToString(CultureInfo.InvariantCulture) + "]")) + "]", // [3]
        b53 = [.. i.Done],                                                                  // [4]
        b54 = step,                                                                         // [5]
        b55 = task,                                                                         // [6]
    };

    private static void SetStep(ExpeditionInstance i, int step)
    {
        i.Step = step;
        i.Counts = new int[i.Tasks.Count];
        i.Done = new bool[i.Tasks.Count];
    }

    /// <summary>The players of the instance on its map now.</summary>
    private IEnumerable<Player> Inside(ExpeditionInstance i) => world.Maps.Find(i.Id)?.Players.Values ?? Enumerable.Empty<Player>();

    /// <summary>
    /// A mob of an instance died (Combat.Kill): every not-done task of the current step it fits (its uniquename = mobid, or its faction)
    /// counts it, capped at the task's count; then 261 to everyone inside (5/6 = that step and task), the next step when this one is done,
    /// the completion after the last (§5). Progress is the instance's (countpartykills is not looked at [design]).
    /// </summary>
    public void Killed(Map map, Mob mob)
    {
        if (Find(map.Cluster) is not { Finished: false } i) return;
        var counted = -1;
        for (var t = 0; t < i.Tasks.Count; t++)
        {
            var task = i.Tasks[t];
            if (i.Done[t] || !(task.Mob is { } id ? id.Equals(mob.Template.Name, StringComparison.OrdinalIgnoreCase)
                    : task.Faction is { } f && f.Equals(mob.Template.Faction, StringComparison.OrdinalIgnoreCase))) continue;
            i.Counts[t] = Math.Min(task.Count, i.Counts[t] + 1);
            i.Done[t] = i.Counts[t] >= task.Count;
            if (counted < 0) counted = t;
        }
        if (counted < 0) return;
        var step = i.Step;
        foreach (var p in Inside(i)) p.Session.Send(Progress(p, i, step, counted));
        Advance(i);
    }

    /// <summary>The step is done: the next one (261 with zero counts, 5/6 = -1), or after the last the completion.</summary>
    private void Advance(ExpeditionInstance i)
    {
        while (!i.Finished && i.Done.All(d => d))
        {
            if (i.Step + 1 >= i.Expedition.Steps.Count)
            {
                Complete(i);
                return;
            }
            SetStep(i, i.Step + 1);
            foreach (var p in Inside(i)) p.Session.Send(Progress(p, i, -1, -1));
        }
    }

    /// <summary>.exp finish: every task of every step done now (each step's 261, then the completion).</summary>
    public void Finish(ExpeditionInstance i)
    {
        while (!i.Finished)
        {
            for (var t = 0; t < i.Tasks.Count; t++) (i.Counts[t], i.Done[t]) = (i.Tasks[t].Count, true);
            foreach (var p in Inside(i)) p.Session.Send(Progress(p, i, -1, -1));
            Advance(i);
        }
    }

    /// <summary>
    /// The last task is done (§5.1): the finish exit is usable, the timeout no longer applies (the instance closes after
    /// Expeditions:FinishedLingerSeconds); every member inside gets the rewards, a member not on the map now when it joins
    /// (<see cref="ExpeditionInstance.Owed"/>, <see cref="Joined"/>).
    /// </summary>
    private void Complete(ExpeditionInstance i)
    {
        i.Finished = true;
        i.FinishedAt = world.Now;
        Log.Info("world", string.Create(CultureInfo.InvariantCulture, $"{i} completed in {(world.Now - i.StartedAt).TotalSeconds:0} s"));
        i.Owed.UnionWith(i.Members.Keys);
        foreach (var p in Inside(i).ToList())
            if (i.Owed.Remove(p.Character.Id)) Reward(p, i);
        if (LevelOf(i) is { BonusChest: true }) SpawnBonusChest(i);
    }

    /// <summary>The LootChestSpawnpoint of the level-18 bonus chest (lootchests.xml: HARCORE_EXPEDITION_LVL_18, ishardcoreexpeditionreward).</summary>
    public const string BonusChestSpawnpoint = "HCE_FINAL_REWARD";

    /// <summary>
    /// A completed level with spawnbonuslootchest (T6_HARDCORE level 18, econ SPEC §3.1): the chest of the map's lootchestspawnpoint
    /// HCE_FINAL_REWARD (every hardcore map has one, SpawnPoint_LOOTCHEST_HCE_FINAL_REWARD; expedition maps spawn no chests otherwise), as a
    /// dungeon chest (<see cref="RandomDungeons.MakeChest"/>: shared, opened with OPEN_LOOTCHEST, its LootByTier of the category's tier
    /// rolled once with the cluster's loot factor, not the level's lootfactor: the chest's lists are the level-18 reward already
    /// [design]). Null when it could not be placed (no map or point, lootchests.xml not readable).
    /// </summary>
    public LootChest? SpawnBonusChest(ExpeditionInstance i)
    {
        if (world.Maps.Find(i.Id) is not { Template: { } template } map) return null;
        if (template.LootChests.FirstOrDefault(p => p.SpawnBehaviour == BonusChestSpawnpoint) is not { } point)
        {
            Log.Warn("world", $"{i}: {template.Id} has no {BonusChestSpawnpoint} chest point: no bonus chest");
            return null;
        }
        try
        {
            if (world.Clusters?.LootChests is not { } data || world.Dungeons.MakeChest(map, point, data, lootTier: i.Category.Tier) is not { } chest) return null;
            map.AddObject(chest);
            Log.Info("world", $"{i}: level {i.Difficulty} bonus chest {chest} at {point.Position}");
            return chest;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            Log.Warn("world", $"{i}: the bonus chest was not placed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// The rewards of <paramref name="p"/> (§5.1): the category's &lt;bonusrewards&gt; instead of its &lt;rewards&gt; when completed within the
    /// bonus time and the daily bonus is available (Expeditions:DailyBonus), plus Expeditions:EveryRunItems; silver × Rates:Silver, fame ×
    /// Rates:Fame (PvE fame), items into the inventory (what does not fit comes by mail with Mail:RewardOverflow, else waits for a later
    /// Join); a system line with what was given. A
    /// hardcore level (§5.1, §12.2): silver × its silverfactor, fame × its mobfamefactor (its lootfactor applies to the kills only, through
    /// <see cref="Map.Difficulty"/>: the reward items keep their counts [design]), a token of its tokenlootlist (Expeditions:HardcoreTokens),
    /// and the next level opened (Expeditions:HardcoreLadder; within the bonus time with HardcoreUnlockInBonusTime).
    /// </summary>
    public void Reward(Player p, ExpeditionInstance i)
    {
        var c = p.Character;
        var log = c.Expeditions ??= new ExpeditionLog();
        var inTime = world.Now - i.StartedAt <= TimeSpan.FromSeconds(i.Expedition.BonusTimeout);
        var bonus = inTime && BonusAvailable(c, i.Expedition);
        var list = bonus ? i.Category.BonusRewards : i.Category.Rewards;
        var level = LevelOf(i);
        if (bonus && Settings.DailyBonus != ExpeditionBonus.Off) log.Bonus[Settings.DailyBonus == ExpeditionBonus.Character ? "*" : i.Expedition.Name] = DateTime.UtcNow.Ticks;
        log.Last = i.Expedition.Name;
        var given = new List<string>();
        var items = new List<(string Name, int Count)>();
        foreach (var r in list)
            switch (r.Kind)
            {
                case "silver":
                    var silver = (long)Math.Round(r.Amount * 10000 * world.Rates.Silver * (level?.Silver ?? 1));
                    if (silver <= 0) break;
                    Wallet.Credit(p, silver, $"expedition {i.Expedition.Name}");
                    given.Add(string.Create(CultureInfo.InvariantCulture, $"{silver / 10000.0:0.##} silver"));
                    break;
                case "fame":
                    var fame = Rates.ScaleFame((long)Math.Round(r.Amount * 10000 * (level?.MobFame ?? 1)), world.Rates.Fame);
                    if (fame <= 0) break;
                    Destiny.Award(p, fame, Destiny.Kill(p, i.Category.Tier), journals: false);
                    given.Add(string.Create(CultureInfo.InvariantCulture, $"{fame / 10000.0:0.##} fame"));
                    break;
                case "item":
                    items.Add((r.Enchantment > 0 ? $"{r.Item}@{r.Enchantment}" : r.Item, r.Count));
                    break;
            }
        foreach (var extra in Settings.EveryRunItems)
            if (!items.Any(x => x.Name.Equals(extra, StringComparison.OrdinalIgnoreCase))) items.Add((extra, 1));
        // §12.2: a hardcore level gives a roll of its tokenlootlist (a token of the next level of one of the ten maps) and opens the next level.
        if (level is not null && Settings.HardcoreTokens && level.TokenLootList.Length > 0)
            items.AddRange(Consumables.Roll(world, level.TokenLootList, Rng, i.Expedition.Name).Select(x => (x.Item.Name, x.Count)));
        string? opened = null;
        if (level is not null && Settings.HardcoreLadder && (inTime || !Settings.HardcoreUnlockInBonusTime)
            && level.Level + 1 <= i.Category.Difficulties.Count && (log.Hardcore ?? 1) < level.Level + 1)
        {
            log.Hardcore = level.Level + 1;
            opened = $"; hardcore level {log.Hardcore} is open";
        }
        var waiting = 0;
        var overflow = new List<InventoryItem>();
        foreach (var (name, count) in items)
        {
            var type = world.Items.Find(name) is { CanCreate: true } t ? t
                : Settings.Substitutes.TryGetValue(name, out var sub) && world.Items.Find(sub) is { CanCreate: true } s ? s : null;
            if (type is null)
            {
                Combat.LogOnce("expreward:" + name, $"expedition reward {name} is not an item this server can create and has no substitute: left out");
                continue;
            }
            if (WorldSession.Fits(p.Inventory.Bag, [(type, count)])) p.Session.GiveItem(p, type, count);
            else overflow.Add(new InventoryItem(type.Name, count));
            given.Add(count == 1 ? type.Name : $"{type.Name} x{count}");
        }
        // What did not fit: a mail with it (Mail:RewardOverflow, stageJ mail SPEC §3), else it waits for a later Join (GiveUnclaimed).
        var mailed = Mailer.Overflow(world, c, overflow, $"the expedition {i.Expedition.Name}");
        if (!mailed) log.Unclaimed.AddRange(overflow);
        waiting = overflow.Count;
        world.Saves.MarkChanged(c);
        // PVE-DESIGN step 11: the journal's finishexpedition missions and the expedition bounties.
        AlbionJournal.ExpeditionFinished(p, i, ((i.FinishedAt ?? world.Now) - i.StartedAt).TotalSeconds);
        world.Bounties.ExpeditionFinished(p, i.Category.Tier);
        world.Guilds.ExpeditionFinished(p, i.Category.Tier, i); // guilds SPEC §10.3/§10.6: once per guild and instance
        world.GuildGoals.OnDungeon(p, i); // stage I: the weekly guild goals' dungeons, once per guild and instance
        Tell(p, $"Expedition complete{(bonus ? " (bonus)" : "")}{(level is null ? "" : $" (level {level.Level})")}: {(given.Count == 0 ? "no reward" : string.Join(", ", given))}"
            + (waiting > 0 ? $"; {waiting} item(s) did not fit the inventory and come {(mailed ? "by mail" : "at a later login")}" : "") + opened);
        Log.Info("world", $"'{c.Name}' completed {i}{(bonus ? " with the bonus" : "")}: {string.Join(", ", given)}");
    }

    /// <summary>Whether the daily bonus of <paramref name="e"/> is open to <paramref name="c"/> (Expeditions:DailyBonus, reset at BonusResetUtc).</summary>
    public bool BonusAvailable(Character c, ExpeditionData.Expedition e)
    {
        if (Settings.DailyBonus == ExpeditionBonus.Off) return true;
        var key = Settings.DailyBonus == ExpeditionBonus.Character ? "*" : e.Name;
        return c.Expeditions?.Bonus.GetValueOrDefault(key) is not { } taken || taken < LastReset(DateTime.UtcNow).Ticks;
    }

    /// <summary>The last daily reset at or before <paramref name="utc"/>.</summary>
    public DateTime LastReset(DateTime utc)
    {
        var today = utc.Date + Settings.BonusResetUtc;
        return utc >= today ? today : today.AddDays(-1);
    }

    /// <summary>The reward items that did not fit: into the inventory as far as they fit now, the rest keeps waiting.</summary>
    private void GiveUnclaimed(Player p)
    {
        if (p.Character.Expeditions is not { Unclaimed.Count: > 0 } log) return;
        var given = 0;
        foreach (var item in log.Unclaimed.ToList())
            if (world.Items.Find(item.Name) is { } type && WorldSession.Fits(p.Inventory.Bag, [(type, item.Count)]))
            {
                p.Session.GiveItem(p, type, item.Count);
                log.Unclaimed.Remove(item);
                given++;
            }
        if (given == 0) return;
        world.Saves.MarkChanged(p.Character);
        Tell(p, $"{given} expedition reward item(s) put into the inventory{(log.Unclaimed.Count > 0 ? $"; {log.Unclaimed.Count} still wait for free slots" : "")}");
    }

    /// <summary>
    /// ActivateExpeditionCheckPoint (280, §2.7.3): a checkpoint of the player's instance in state 0, the player within maxcolliderdist + 5 m:
    /// it becomes 1 (the respawn point), the active one before 2, 315 for both to everyone inside. Else nothing (the answer is RC 0 anyway).
    /// </summary>
    public bool ActivateCheckpoint(Player p, long id)
    {
        if (p.Map is not { } map || Find(map.Cluster) is not { } i || i.Objects.OfType<ExpeditionCheckPoint>().FirstOrDefault(c => c.Id == id) is not { State: 0 } cp) return false;
        if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), cp.Position) > cp.Spawn.ColliderDistance + 5) return false;
        var changed = new List<ExpeditionCheckPoint> { cp };
        if (i.Active is { } old)
        {
            old.State = 2;
            changed.Insert(0, old);
        }
        cp.State = 1;
        i.Active = cp;
        foreach (var o in map.Players.Values)
            foreach (var c in changed) o.Session.Send(c.BuildCreate(o).First());
        return true;
    }

    // ---- leaving (SPEC §2.10, §7) ----

    /// <summary>
    /// ExitEnterStart (237) on <paramref name="exitId"/>: an exit of the player's instance that is usable (the start exit always, the finish
    /// exit after the completion) within Expeditions:ExitRange, not knocked down → 253 to the others and the channel of ExitUseSeconds; null.
    /// Else why not (the handler sends 254).
    /// </summary>
    public string? StartExit(Player p, long exitId)
    {
        if (p.Map is not { } map || Find(map.Cluster) is not { } i) return "not in an expedition";
        if (i.Objects.OfType<ExpeditionExit>().FirstOrDefault(e => e.Id == exitId) is not { } exit) return $"object {exitId} is no exit of {i}";
        if (!exit.Start && !i.Finished) return "the finish exit opens at the completion";
        var at = p.PositionAt(DateTime.UtcNow);
        if (Vector2.Distance(at, exit.Position) > Settings.ExitRange) return string.Create(CultureInfo.InvariantCulture, $"{Vector2.Distance(at, exit.Position):0.#} m from the exit");
        if (p.Combat.Down is { } down) return $"knocked down ({down.Kind})";
        var now = world.GameNow;
        var end = now + TimeSpan.FromSeconds(Settings.ExitUseSeconds).Ticks;
        _exiting[p] = (exit, world.Now + TimeSpan.FromSeconds(Settings.ExitUseSeconds), at, p.Health.At(world.GameNow));
        p.SendToObservers(new ExitEnterStartEvent { ddn = p.Id, b5w = now, b5x = end, b5y = exit.Id });
        return null;
    }

    /// <summary>ExitEnterCancel (238), or a move, damage or knockdown during the channel: 254 to the player and those in view.</summary>
    public void CancelExit(Player p, string why)
    {
        if (!_exiting.Remove(p)) return;
        p.SendAround(new ExitEnterCancelEvent { ddn = p.Id });
        Log.Info("world", $"'{p.Character.Name}' expedition exit cancelled ({why})");
    }

    /// <summary>
    /// <paramref name="p"/> leaves <paramref name="i"/> (an exit, the escape spell, the timeout, the closing): 263 kind 3, then back to its
    /// return point (<paramref name="answer"/> false: the ChangeCluster push; true: the caller answers a ChangeCluster request with the
    /// returned arrival). Before the completion it is an abandon (no reward); through the start exit or the escape spell it starts the leave
    /// penalty with Expeditions:LeavePenalty (off by default: PvE, §12.5).
    /// </summary>
    public Arrival Leave(Player p, ExpeditionInstance i, string why, bool answer = false)
    {
        if (Settings.LeavePenalty && !i.Finished && why is "start exit" or "escape")
        {
            (p.Character.Expeditions ??= new ExpeditionLog()).PenaltyUntil = DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(Settings.PenaltySeconds).Ticks;
            Tell(p, string.Create(CultureInfo.InvariantCulture, $"Expedition abandoned: no new registration for {Settings.PenaltySeconds:0} s"));
        }
        var back = i.Members.GetValueOrDefault(p.Character.Id) ?? p.Character.DungeonReturn ?? new DungeonReturn(Settings.FallbackCluster, 0, 0, 0);
        i.Members.Remove(p.Character.Id);
        i.Arriving.Remove(p.Character.Id);
        _exiting.Remove(p);
        p.Session.Send(InfoEvent(p, i.Category, i.Expedition, KindEnded, world.GameNow));
        p.Character.DungeonReturn = null;
        var to = new Arrival(back.Cluster, new Vector2(back.X, back.Y), back.Direction);
        Log.Info("world", $"'{p.Character.Name}' leaves {i} ({why}{(i.Finished ? "" : ", not completed")})");
        if (!answer) p.Session.Teleport(to, $"expedition:{i.Id}:{why}");
        return to;
    }

    /// <summary>
    /// The way back of a dead player in an instance with Expeditions:CheckpointRespawn: the active checkpoint, else the start point, in the
    /// same instance (§6; the push reloads it, the cluster key 1 included); null outside a live instance (a closing one included: the
    /// close sends the player out, <see cref="Evict"/>).
    /// </summary>
    public (Arrival To, string World)? RespawnOf(Player p)
    {
        if (!Settings.CheckpointRespawn || p.Map is not { } map || Find(map.Cluster) is not { } i) return null;
        var start = i.Start.Position == Vector2.Zero && world.CachedCluster(i.World) is { } t ? StartOf(t) with { Cluster = i.Id } : i.Start;
        var at = i.Active is { } cp ? new Arrival(i.Id, cp.Position, cp.Direction) : start;
        i.Arriving[p.Character.Id] = world.Now + TimeSpan.FromSeconds(ArrivalGraceSeconds);
        return (at, i.World);
    }

    /// <summary>
    /// The cluster and point of a character whose saved cluster is the id of an instance that is gone (§7: closed, or a restart): its saved
    /// way back, else Expeditions:FallbackCluster; the character is changed and saved. False when its cluster is no closed instance id.
    /// </summary>
    public bool ReturnIfClosed(Character c)
    {
        if (!ExpeditionData.IsInstance(c.Cluster) || IsLive(c.Cluster)) return false;
        var to = c.DungeonReturn ?? new DungeonReturn(Settings.FallbackCluster, 0, 0, 0);
        (c.Cluster, c.PositionX, c.PositionY, c.Direction) = (to.Cluster, to.X, to.Y, to.Direction);
        c.PendingTransfer = null;
        c.DungeonReturn = null;
        world.Saves.SaveNow(c);
        Log.Info("world", $"'{c.Name}' was in a closed expedition: back in {to.Cluster}");
        return true;
    }

    /// <summary>
    /// The instance closes (§7): every player still inside goes to its return point (<see cref="Evict"/>), the map goes, the id is never
    /// used again.
    /// </summary>
    public void Close(ExpeditionInstance i, string why)
    {
        if (i.Closed) return;
        i.Closed = true;
        if (world.Maps.Find(i.Id) is { } map)
        {
            foreach (var p in map.Players.Values.ToList())
            {
                if (p.Session.Status == SessionStatus.InWorld && p.Session.Player == p) Evict(p, i, why == "timeout" ? "timeout" : "closed");
                else world.Maps.Remove(p, world.Clock.Now);
            }
            world.Maps.Unload(map, force: true);
        }
        _instances.Remove(i.Id);
        Log.Info("world", $"{i} closed ({why})");
    }

    /// <summary>
    /// A player still inside a closing or timed-out instance goes out to its return point (263 kind 3, <see cref="Leave"/>), as
    /// RandomDungeons.Evict: a knocked-down one in a lethal clustertype dies first (Death.Abandoned); a dead one goes with
    /// <see cref="Death.RespawnVia"/>, so its Join there is the respawn (revived, Respawn 88). A knockdown that is not lethal ends with the move.
    /// </summary>
    private void Evict(Player p, ExpeditionInstance i, string why)
    {
        if (p.Combat.Down is { Kind: not KnockdownKind.Dead }) Death.Abandoned(p);
        var dead = p.Combat.Down is { Kind: KnockdownKind.Dead };
        var to = Leave(p, i, why, answer: true);
        p.Session.Teleport(to, dead ? Death.RespawnVia : $"expedition:{i.Id}:{why}");
    }

    /// <summary>
    /// A dead member respawns outside (Expeditions:CheckpointRespawn off): it is no member any more (263 kind 3 while it is in the world,
    /// the way back cleared), so it can register again at once. Nothing for a non-member.
    /// </summary>
    public void DiedOut(Player p)
    {
        if (InstanceOf(p.Character.Id) is not { } i) return;
        i.Members.Remove(p.Character.Id);
        i.Arriving.Remove(p.Character.Id);
        _exiting.Remove(p);
        if (p.Session.Status == SessionStatus.InWorld && p.Session.Player == p) p.Session.Send(InfoEvent(p, i.Category, i.Expedition, KindEnded, world.GameNow));
        p.Character.DungeonReturn = null;
        Log.Info("world", $"'{p.Character.Name}' died in {i} and respawns outside it");
    }

    // ---- the timers ----

    /// <summary>
    /// Every tick (<see cref="World.Update"/>): the group queues that close, the invitation timeouts (a62 20), the ready timeouts (263 kind 3,
    /// a62 19), the registrations of players gone from the world, the Join channels (broken by a move over
    /// CancelDistance, damage, a knockdown or the player gone; done after EnteringSeconds), the exit channels (the same; done: 255 and the way back),
    /// the instances' timeout (failed: everyone out), their closing when empty for EmptyCloseSeconds or FinishedLingerSeconds after the completion.
    /// </summary>
    public void Update()
    {
        var now = world.Now;
        // The queues of group registrations without a party (§3.3 [design]): the oldest due one takes in the others queued.
        foreach (var g in _registrations.Values.Select(r => r.Group).OfType<ExpeditionGroup>().Distinct().Where(g => g.QueueUntil <= now).OrderBy(g => g.QueueUntil).ToList())
            if (g.QueueUntil is not null && g.Members.Count > 0) CloseQueue(g);
        foreach (var r in _registrations.Values.ToList())
        {
            if (_registrations.GetValueOrDefault(r.Character) != r) continue; // ended by another one's drop this tick
            var p = world.Parties.Online(r.Character);
            if (p is null)
            {
                // Gone from the world: a ready one keeps its place until the ready timeout (a quick relog), the others end.
                if (r.State != ExpeditionState.Ready || now - r.ReadySince >= TimeSpan.FromSeconds(Settings.ReadyTimeoutSeconds)) Drop(r, Ok, null, "offline");
                continue;
            }
            switch (r.State)
            {
                case ExpeditionState.Invited when now >= r.InviteUntil:
                    Drop(r, DidntAcceptInTime, null, "did not answer the invitation in time");
                    break;
                case ExpeditionState.Ready when now - r.ReadySince >= TimeSpan.FromSeconds(Settings.ReadyTimeoutSeconds):
                    Drop(r, DidntArriveInTime, null, $"did not join expedition {r.Expedition.Name} in time");
                    break;
                case ExpeditionState.Entering:
                    if (p.Combat.Down is not null) CancelEntering(p, "knocked down");
                    else if (Hit(p, r.EnterHealth)) CancelEntering(p, "damaged");
                    else if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), r.EnterFrom) > Settings.CancelDistance) CancelEntering(p, "moved");
                    else if (now >= r.EnterDue && p.Session.Status == SessionStatus.InWorld)
                    {
                        // The instance's world cluster must be resolved for its start point: wait for it (prefetched at the
                        // registration), at most 30 s more.
                        if (world.CachedCluster(r.Expedition.World) is not null) Enter(p, r);
                        else if (now >= r.EnterDue + TimeSpan.FromSeconds(30))
                        {
                            CancelEntering(p, $"cluster {r.Expedition.World} not resolved");
                            Tell(p, $"The expedition's map {r.Expedition.World} could not be read; try again");
                        }
                    }
                    break;
            }
        }
        foreach (var (p, (exit, due, from, health)) in _exiting.ToList())
        {
            if (p.Map is null || p.Map != exit.Map || p.Session.Player != p) _exiting.Remove(p);
            else if (p.Combat.Down is not null) CancelExit(p, "knocked down");
            else if (Hit(p, health)) CancelExit(p, "damaged");
            else if (Vector2.Distance(p.PositionAt(DateTime.UtcNow), from) > Settings.CancelDistance) CancelExit(p, "moved");
            else if (now >= due && Find(p.Map.Cluster) is { } i)
            {
                _exiting.Remove(p);
                p.SendAround(new ExitEnterFinishedEvent { ddn = p.Id, b5v = exit.Id });
                Leave(p, i, exit.Start ? "start exit" : "finish exit");
            }
        }
        foreach (var i in _instances.Values.ToList())
        {
            if (!i.Finished && now - i.StartedAt >= TimeSpan.FromSeconds(i.Expedition.Timeout))
            {
                foreach (var p in Inside(i)) Tell(p, $"The expedition failed: its time ({i.Expedition.Timeout / 60} min) is up");
                Close(i, "timeout"); // everyone inside out (Evict: a dead one respawns at its return point)
                continue;
            }
            foreach (var late in i.Arriving.Where(a => now >= a.Value).Select(a => a.Key).ToList()) i.Arriving.Remove(late);
            if (Inside(i).Any() || i.Arriving.Count > 0) i.EmptySince = null;
            else i.EmptySince ??= now;
            if (i.EmptySince is { } since && now - since >= TimeSpan.FromSeconds(Settings.EmptyCloseSeconds)) Close(i, "empty");
            else if (i.FinishedAt is { } done && now - done >= TimeSpan.FromSeconds(Settings.FinishedLingerSeconds)) Close(i, "completed");
        }
    }

    /// <summary>.exp list: the registrations and the live instances, one line each.</summary>
    public IEnumerable<string> Describe()
    {
        var now = world.Now;
        foreach (var r in _registrations.Values)
            yield return $"  queued: {world.Parties.Online(r.Character)?.Character.Name ?? r.Character.ToString()} {r.Category.Name}{(r.Difficulty > 0 ? $" level {r.Difficulty}" : "")} {r.Expedition.Name} ({r.State}"
                + (r.Group is { } g ? $", {g}{(g.QueueUntil is null ? "" : ", queue open")})" : ")");
        foreach (var i in _instances.Values)
        {
            var tasks = string.Join(", ", i.Tasks.Select((t, n) => $"{t.Mob ?? t.Faction} {i.Counts[n]}/{t.Count}"));
            var left = i.Finished ? "completed" : string.Create(CultureInfo.InvariantCulture, $"{(TimeSpan.FromSeconds(i.Expedition.Timeout) - (now - i.StartedAt)).TotalMinutes:0} min left");
            yield return $"  {i.Id} {i.Expedition.Name}{(i.Difficulty > 0 ? $" level {i.Difficulty}" : "")} in {i.World}: {i.Members.Count} member(s) (made for {i.Players}), step {i.Step + 1}/{i.Expedition.Steps.Count} [{tasks}], {left}";
        }
    }

    /// <summary>A system chat line (SystemMessage; a leading '@' would be taken for a localisation tag).</summary>
    internal static void Tell(Player p, string text)
    {
        if (text.Length > 0) p.Session.Send(new SystemMessageEvent { d0p = text.StartsWith('@') ? " " + text : text, d0q = 0 });
    }
}
