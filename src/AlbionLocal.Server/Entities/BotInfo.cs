using System.Numerics;
using AlbionLocal.Server.Bots;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// Stage I: what a bot does (analysis/work/stageI/bots/SPEC.md §1.8): stands and walks in cities, gathers, patrols; stageL: invades a
/// corrupted dungeon (stageL corrupted SPEC §4.5: never a population slot, Hostile to the run's owner alone while the invasion lasts), is a
/// hellgate's enemy team (stageL hellgates SPEC §5.2: never a population slot, Hostile to the instance's party alone during objective 2).
/// </summary>
public enum BotRole : byte
{
    City,
    Gatherer,
    Patrol,
    Invader,
    Hellgate,

    /// <summary>StageL mists phase B (SPEC §6.9): a lone Red wanderer of a mists zone (a patrol of one; zone class Mists).</summary>
    Drifter,

    /// <summary>StageN companions: a bot a player hired (.companion), in its party, following it and fighting its mobs (<see cref="Bots.Companions"/>).</summary>
    Companion,

    /// <summary>Stage N arena (SPEC §4.3): a player-bot filling an arena team's empty slot; it hunts the other team's players.</summary>
    Arena,
}

/// <summary>StageN companions: what a hired companion does in its owner's party.</summary>
public enum CompanionKind : byte
{
    Tank,
    Healer,
    Melee,
    Ranged,
}

/// <summary>StageN companions: a hired companion's owner (the character it follows), its kind and its follow and taunt timers.</summary>
public sealed class CompanionInfo
{
    public required Guid Owner { get; init; }

    public required string OwnerName { get; init; }

    public required CompanionKind Kind { get; init; }

    /// <summary>Its place around the owner (0-3): the follow spot's angle.</summary>
    public int Index { get; set; }

    /// <summary>The distance to the owner when it last came nearer, and when (<see cref="World.Clock"/>): the stuck check.</summary>
    public float BestDistance { get; set; } = float.MaxValue;

    public TimeSpan BestAt { get; set; }

    /// <summary>When its owner was last seen in game (<see cref="World.Clock"/>).</summary>
    public TimeSpan OwnerSeen { get; set; }

    /// <summary>When a tank may next pull a mob off another member by threat (<see cref="World.GameNow"/> ticks).</summary>
    public long NextTaunt { get; set; }

    /// <summary>
    /// Bot-party: a world bot that accepted its owner's party invite (<see cref="Bots.BotSocial.WantsParty"/>), not a hired one: a party member
    /// that counts in the fame split, says a Chat.Join / Chat.Leave line, and goes at <see cref="Until"/> (Bots:Party:MaxMinutes).
    /// </summary>
    public bool Recruited { get; init; }

    /// <summary>Bot-party: when a recruited bot leaves its party (<see cref="World.Clock"/>); never for a hired companion.</summary>
    public TimeSpan Until { get; init; } = TimeSpan.MaxValue;
}

/// <summary>A bot's emote under way: emotes.bin <see cref="Emote"/> since GameTimeStamp <see cref="Start"/>; a looping one is shown to every new observer.</summary>
public sealed record BotEmote(int Emote, long Start, bool Loops);

/// <summary>
/// Stage I bots (SPEC §1.3): what makes a <see cref="Player"/> a bot: its role, gear tier, persona (the population slot it fills, stable for a
/// cluster over respawns and map reloads: "&lt;cluster&gt;:&lt;role&gt;:&lt;index&gt;", or "gm:&lt;object id&gt;" for a GM's), brain, walk, the
/// emote it shows and its timers. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class BotInfo
{
    public required BotRole Role { get; set; }

    /// <summary>The tier of its gear (1-8).</summary>
    public required int Tier { get; init; }

    public required string PersonaId { get; set; }

    public required IBotBrain Brain { get; set; }

    /// <summary>NewCharacter key 2: the avatars.xml index of its portrait (0 = AVATAR_01, left out).</summary>
    public byte Avatar { get; init; }

    /// <summary>Where it spawned.</summary>
    public Vector2 Home { get; set; }

    /// <summary>The population slot it fills; null for a GM's bot (.bot spawn), which never respawns.</summary>
    public BotSlot? Slot { get; set; }

    /// <summary>Bot-roster: the roster identity (<see cref="BotIdentity.Index"/>) it plays; null for a fresh name or a GM's bot.</summary>
    public int? Identity { get; set; }

    /// <summary>
    /// Bot-travel: a gatherer's home city (cluster id; the nearest royal city or rest within Bots:Travel:MaxHops of where it came online),
    /// when its trip ends (<see cref="World.Clock"/> time, Bots:Gatherer:TripMinutes) and whether it walks home to bank now. Kept over its travels.
    /// </summary>
    public string? HomeCity { get; set; }

    public TimeSpan TripUntil { get; set; } = TimeSpan.MaxValue;

    public bool Homing { get; set; }

    /// <summary><see cref="World.Clock"/> time of its spawn, and when it leaves (turnover: Bots:StayMinutes; never for a GM's bot).</summary>
    public TimeSpan SpawnedAt { get; init; }

    public TimeSpan StayUntil { get; set; } = TimeSpan.MaxValue;

    /// <summary><see cref="World.Clock"/> time of its next decision (Bots:ThinkSeconds).</summary>
    public TimeSpan NextThink { get; set; }

    /// <summary>The path it walks (<see cref="BotMotion"/>).</summary>
    public BotMotion Motion { get; } = new();

    /// <summary>The emote it shows; null when none.</summary>
    public BotEmote? Emote { get; set; }

    /// <summary>
    /// The harvest it shows (phase 2, a gatherer): its observers that know the node got its HarvestStart; null when none. Not
    /// <see cref="Player.Harvest"/>, which the bot's own (dropped) session would end without the observers' HarvestCancel.
    /// </summary>
    public BotHarvest? Harvest { get; set; }

    /// <summary>StageM bots: an armed gatherer holds the weapon of its bag in its mainhand (a fight), its tool lying in the bag meanwhile.</summary>
    public bool Armed { get; set; }

    /// <summary>Its fight (phase 2: who hit it, whom it answers, its flight and its defeat).</summary>
    public BotCombatState Fight { get; } = new();

    /// <summary>The patrol group it walks and fights with (phase 3); null for the other roles.</summary>
    public BotGroup? Group { get; set; }

    /// <summary>StageL corrupted: × its maximum health and its hits over Bots:Combat's (Corrupted:Invader:HealthFactor / DamageFactor); 1 for the other roles.</summary>
    public double HealthFactor { get; init; } = 1;

    public double DamageFactor { get; init; } = 1;

    /// <summary>StageN botpower: an elite (Bots:Gear:EliteChance): enchanted gear of a better quality, a richer bag (Bots:Rewards:Elite*).</summary>
    public bool Elite { get; init; }

    /// <summary>
    /// StageN companions: the owner and kind of a <see cref="BotRole.Companion"/>; null for the other roles. Role, brain, slot, stay, group and
    /// this change only when a world bot accepts a party invite (bot-party, <see cref="BotManager.Recruit"/>): it becomes a companion.
    /// </summary>
    public CompanionInfo? Companion { get; set; }

    /// <summary>
    /// Bot support roles (Bots:Support:Enabled, Bots:Patrol:Makeups): a patrol's part in its group: a damage dealer (every other bot), a
    /// healer (a holy or nature staff; it heals, shields and buffs its group) or a tank (a mace, hammer or shield; it engages first and holds
    /// the attacker of its healer).
    /// </summary>
    public PatrolPart Part { get; init; }
}

/// <summary>Bot support roles: a patrol's part in its group (<see cref="BotInfo.Part"/>; Bots:Patrol:Makeups "dps", "healer", "tank").</summary>
public enum PatrolPart : byte
{
    Dps,
    Healer,
    Tank,
}

/// <summary>
/// A bot's harvest show (SPEC §1.3, bots-world SPEC §2.5): <paramref name="Node"/> with <paramref name="Tool"/> (its mainhand), one repeat of
/// <paramref name="Duration"/> s from GameTimeStamp <paramref name="Start"/>, <paramref name="Repeats"/> more after this one.
/// </summary>
public sealed record BotHarvest(Harvestable Node, Item? Tool, long Start, float Duration, int Repeats)
{
    /// <summary>The end of this repeat's bar (HarvestFinished key 2), GameTimeStamp ticks.</summary>
    public long End => Start + Harvesting.Ticks(Duration);
}

/// <summary>
/// A bot's fight (SPEC §1.3, bots-combat SPEC §2.5): its attackers by object id with the GameTimeStamp of their last hit (never a
/// <see cref="Player"/>: one who left the map is simply not found), the one it answers, where the fight began, its flight and its
/// one-way defeat.
/// </summary>
public sealed class BotCombatState
{
    /// <summary>Attacker object id → GameTimeStamp of its last hit on the bot.</summary>
    public Dictionary<long, long> Engaged { get; } = [];

    /// <summary>The health damage of its attackers' hits (object id, GameTimeStamp, damage) within <see cref="BotCombat.RetargetSeconds"/>, the retarget's measure.</summary>
    public List<(long Id, long At, double Damage)> Hits { get; } = [];

    /// <summary>The player it fights (object id); 0 = none.</summary>
    public long TargetId { get; set; }

    /// <summary>Where the bot stood at the first hit of this fight (the leash's centre); null out of a fight.</summary>
    public Vector2? EngagePoint { get; set; }

    /// <summary>GameTimeStamp before which it does not swing (Bots:Combat:ReactionMs after the first hit).</summary>
    public long ReadyAt { get; set; }

    /// <summary>GameTimeStamp until which it runs off (no swings); null when it does not flee.</summary>
    public long? FleeUntil { get; set; }

    /// <summary>The counter of its last cast (CastStart key 8, 1-255, as a client's: stageJ pvp-spells SPEC §2.9).</summary>
    public byte CastCounter { get; set; }

    /// <summary>GameTimeStamp of its last spell roll (Bots:Combat:SpellChance runs on the game time since it); 0 = none in this fight.</summary>
    public long SpellRollAt { get; set; }

    /// <summary>Whether this fight's flight was rolled (once per fight).</summary>
    public bool FleeRolled { get; set; }

    /// <summary>StageN botpower: GameTimeStamp until which a ranged bot steps back from its target (a kite, no swing meanwhile), and of its last one.</summary>
    public long KiteUntil { get; set; }

    public long KitedAt { get; set; }

    /// <summary>StageN botpower: GameTimeStamp of its last look for a weaker target (Bots:Combat:FocusWeakest).</summary>
    public long FocusAt { get; set; }

    /// <summary>The target's position at the last chase path and its GameTimeStamp (a new path every 1 s or 4 m).</summary>
    public Vector2 ChasedTo { get; set; }

    public long ChasedAt { get; set; }

    /// <summary>Defeated (one-way, SPEC §1.10), and the <see cref="World.Clock"/> time of its despawn.</summary>
    public bool Defeated { get; set; }

    public TimeSpan? DespawnAt { get; set; }

    /// <summary>
    /// Characters it leaves alone → GameTimeStamp until when (phase 3, a patrol's on-sight scan): the ones its group knocked down
    /// (Bots:Patrol:SpareMinutes) and the ones it gave up at the leash (<see cref="BotCombat.LeashSpareSeconds"/>). An attack still engages it.
    /// </summary>
    public Dictionary<Guid, long> Spared { get; } = [];

    /// <summary>What a knockdown's defeat leaves behind at the despawn (phase 3, <see cref="BotRewards"/>); null when nothing.</summary>
    public BotDrop? Drop { get; set; }

    /// <summary>What a gatherer's show "gathered": item name → amount (the loot of phase 3, each item's share its own stack).</summary>
    public Dictionary<string, int> Carried { get; } = [];

    /// <summary>All it "gathered", every item together (.bot info).</summary>
    public int CarriedTotal => Carried.Values.Sum();

    /// <summary>Whether it is in a fight: a target, an attacker within the engage time, or a flight.</summary>
    public bool Active => TargetId != 0 || Engaged.Count > 0 || FleeUntil is not null;

    /// <summary>The fight is over: all but the defeat, the spared and the carried resources forgotten.</summary>
    public void End()
    {
        Engaged.Clear();
        Hits.Clear();
        TargetId = 0;
        EngagePoint = null;
        FleeUntil = null;
        FleeRolled = false;
        ReadyAt = 0;
        SpellRollAt = 0;
    }
}
