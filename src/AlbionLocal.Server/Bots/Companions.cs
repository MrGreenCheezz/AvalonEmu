using System.Globalization;
using System.Numerics;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Bots;

/// <summary>
/// StageN companions (analysis/work/stageN/companions/SPEC.md): the bots players hire to fight beside them (.companion hire &lt;tank|healer|
/// melee|ranged&gt; [tier]). A companion is a <see cref="BotRole.Companion"/> bot (<see cref="BotManager.SpawnCompanion"/>) in its owner's
/// party (<see cref="Parties.AddCompanion"/>: the party frame shows it, and a party-only content such as a hellgate or a group expedition takes
/// the owner with it), wearing a kit of its kind at a tier from the owner's average item power (or the one asked for) within
/// Companions:MinTier..MaxTier. It follows its owner over every cluster change (the world tick puts it next to him on his new map, whatever
/// the instance: random, static and corrupted dungeons, expeditions, hellgates, mists) and within a map walks after him, put next to him when
/// Companions:TeleportDistance away or not nearer for Companions:StuckSeconds. Mobs fight it as a player (<see cref="Map.Fighters"/>); its
/// kills are its owner's (fame, silver, the loot bag), so it takes no loot and no fame share, and the owner's fame is not split with it.
/// Nothing is saved: companions go when their owner is out of the game for Companions:OwnerGraceSeconds, leaves the party or is dismissed,
/// and at a restart. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Companions(World world)
{
    private readonly Dictionary<Guid, Player> _hired = [];

    private CompanionOptions Options => world.Config.Companions;

    /// <summary>Every hired companion.</summary>
    public IReadOnlyCollection<Player> All => _hired.Values;

    /// <summary>The companions of character <paramref name="owner"/>, in their places' order.</summary>
    public IReadOnlyList<Player> Of(Guid owner) => [.. _hired.Values.Where(b => b.Bot!.Companion!.Owner == owner).OrderBy(b => b.Bot!.Companion!.Index)];

    /// <summary>Whether character <paramref name="owner"/> has a companion.</summary>
    public bool HasAny(Guid owner) => _hired.Values.Any(b => b.Bot!.Companion!.Owner == owner);

    /// <summary>The joined player who hired <paramref name="bot"/>; null when it is no companion or its owner is not in game.</summary>
    public Player? OwnerOf(Player bot) => bot.Bot?.Companion is { } c ? world.Parties.Online(c.Owner) : null;

    /// <summary>
    /// Who is credited with <paramref name="attacker"/>'s hits on mobs (<see cref="Combat.Damage"/>): a companion's owner in game, else null (it
    /// deals nothing then); anyone else himself.
    /// </summary>
    public Player? CreditOf(Player attacker) => attacker.IsCompanion ? OwnerOf(attacker) : attacker;

    /// <summary>The kinds by the words .companion takes.</summary>
    public static CompanionKind? ParseKind(string word) => word.ToLowerInvariant() switch
    {
        "tank" => CompanionKind.Tank,
        "healer" or "heal" or "heals" or "support" => CompanionKind.Healer,
        "melee" or "dps" or "mdps" or "fighter" => CompanionKind.Melee,
        "ranged" or "rdps" or "archer" or "range" => CompanionKind.Ranged,
        _ => null,
    };

    /// <summary>The gear tier for <paramref name="owner"/>'s average item power (T4 700, T5 800 … T8 1100) within Companions:MinTier..MaxTier.</summary>
    public int TierFor(Player owner)
    {
        var ip = Expeditions.AverageItemPower(owner);
        return Math.Clamp((int)Math.Round((ip - 300) / 100), Options.MinTier, Math.Max(Options.MinTier, Options.MaxTier));
    }

    /// <summary>The item patterns of a kind's kit, slot by slot, the first that resolves at the tier (or lower) taken.</summary>
    private static readonly Dictionary<CompanionKind, string[][]> Kits = new()
    {
        [CompanionKind.Tank] = [["T{t}_MAIN_MACE"], ["T{t}_OFF_SHIELD"], ["T{t}_HEAD_PLATE_SET1"], ["T{t}_ARMOR_PLATE_SET1"], ["T{t}_SHOES_PLATE_SET1"]],
        [CompanionKind.Healer] = [["T{t}_MAIN_HOLYSTAFF"], ["T{t}_OFF_BOOK"], ["T{t}_HEAD_CLOTH_SET1"], ["T{t}_ARMOR_CLOTH_SET1"], ["T{t}_SHOES_CLOTH_SET1"]],
        [CompanionKind.Melee] = [["T{t}_2H_CLAYMORE", "T{t}_MAIN_SWORD"], ["T{t}_HEAD_LEATHER_SET1"], ["T{t}_ARMOR_LEATHER_SET1"], ["T{t}_SHOES_LEATHER_SET1"]],
        [CompanionKind.Ranged] = [["T{t}_2H_BOW"], ["T{t}_HEAD_LEATHER_SET2", "T{t}_HEAD_LEATHER_SET1"], ["T{t}_ARMOR_LEATHER_SET2", "T{t}_ARMOR_LEATHER_SET1"],
            ["T{t}_SHOES_LEATHER_SET2", "T{t}_SHOES_LEATHER_SET1"]],
    };

    /// <summary>The kit of <paramref name="kind"/> at <paramref name="tier"/>: each slot's first pattern items.xml has (at that tier or below), and the bots' cape.</summary>
    public List<ItemTemplate> Gear(CompanionKind kind, int tier)
    {
        var gear = new List<ItemTemplate>();
        foreach (var slot in Kits[kind].Append([world.BotSettings.Cape]))
            if (slot.Select(p => world.Bots.Factory.Resolve(p, tier)).FirstOrDefault(i => i is not null) is { } item && gear.All(g => g.Slot != item.Slot))
                gear.Add(item);
        return gear;
    }

    /// <summary>The spells a kind's kit picks first: a tank's threat spells (TAUNT), a healer's heals; null for the damage dealers.</summary>
    public static Func<SpellTemplate, bool>? Prefer(CompanionKind kind) => kind switch
    {
        CompanionKind.Tank => Taunts,
        CompanionKind.Healer => s => Heals(s) && SpellTargets.Parse(s.Target) != SpellTargets.Cls.KnockedDownFriendPlayer,
        _ => null,
    };

    /// <summary>Whether <paramref name="spell"/> moves threat (changeaggro, maketargettopthreat, matchhighestthreatlevel: TAUNT, the hammer and bear smashes).</summary>
    public static bool Taunts(SpellTemplate spell) =>
        spell.Effects.Any(e => e.DescendantsAndSelf().Any(d => d.Name.LocalName is "changeaggro" or "maketargettopthreat" or "matchhighestthreatlevel"));

    /// <summary>Whether <paramref name="spell"/> is a heal of a friend (uitype or category heal, a friendly class or the ground).</summary>
    public static bool Heals(SpellTemplate spell) =>
        (spell.UiType == "heal" || spell.Category == "heal") && SpellTargets.Parse(spell.Target) is var cls
        && (cls is >= SpellTargets.Cls.FriendOther and <= SpellTargets.Cls.FriendAllNotPassive || cls == SpellTargets.Cls.Ground);

    /// <summary>
    /// <paramref name="owner"/> hires a companion of <paramref name="kind"/> (gear <paramref name="tier"/>, null: <see cref="TierFor"/>): it
    /// stands behind him and joins his party (a new one he leads when he has none). Returns why not (Companions:Enabled, MaxPerPlayer,
    /// MaxPartySize, a knocked-down or riding owner, no map), or null with <paramref name="hired"/>.
    /// </summary>
    public string? Hire(Player owner, CompanionKind kind, int? tier, out Player? hired)
    {
        hired = null;
        if (!Options.Enabled) return "companions are off on this server (Companions:Enabled)";
        if (owner.IsBot || owner.Map is not { Template: not null } map) return "not in a world map";
        if (owner.Combat.Down is not null) return "knocked down";
        var mine = Of(owner.Character.Id);
        if (mine.Count >= Options.MaxPerPlayer) return $"you have {mine.Count} companions already (at most {Options.MaxPerPlayer})";
        var limit = Math.Min(Options.MaxPartySize, world.Config.PartyMaxSize);
        if (world.Parties.Of(owner.Character.Id) is { } party && party.Members.Count >= limit) return $"your party has {party.Members.Count} members (at most {limit} with companions)";
        var gearTier = Math.Clamp(tier ?? TierFor(owner), 1, Math.Max(Options.MinTier, Options.MaxTier));
        var gear = Gear(kind, gearTier);
        var index = Enumerable.Range(0, 4).First(i => mine.All(b => b.Bot!.Companion!.Index != i) || i == 3);
        var info = new CompanionInfo { Owner = owner.Character.Id, OwnerName = owner.Character.Name, Kind = kind, Index = index, OwnerSeen = world.Clock.Now, BestAt = world.Clock.Now };
        var (at, facing) = SpotNear(owner, map, index);
        var bot = world.Bots.SpawnCompanion(map, gearTier, at, facing, gear, Prefer(kind), new CompanionBrain(world.Bots, this), info);
        if (bot is null) return "no free name for a companion";
        _hired[bot.Character.Id] = bot;
        if (world.Parties.AddCompanion(owner, bot, limit) is { } why)
        {
            _hired.Remove(bot.Character.Id);
            world.Bots.Despawn(bot, why, BotRefill.Now);
            return why;
        }
        hired = bot;
        return null;
    }

    /// <summary>
    /// <paramref name="owner"/>'s companions named by <paramref name="which"/> go ("all" or empty: every one; a name, a kind or a place number
    /// 1-4 of .companion list): out of the party (PartyPlayerLeft), off the map. Returns how many went.
    /// </summary>
    public int Dismiss(Player owner, string? which)
    {
        var mine = Of(owner.Character.Id);
        var all = string.IsNullOrWhiteSpace(which) || which.Equals("all", StringComparison.OrdinalIgnoreCase);
        var kind = all ? null : ParseKind(which!);
        var gone = mine.Where((b, i) => all || b.Character.Name.Equals(which, StringComparison.OrdinalIgnoreCase) || b.Bot!.Companion!.Kind == kind
            || which == (i + 1).ToString(CultureInfo.InvariantCulture)).ToList();
        if (!all && kind is not null) gone = gone.Take(1).ToList(); // "dismiss healer": one of them
        foreach (var bot in gone) Remove(bot, $"dismissed by '{owner.Character.Name}'");
        return gone.Count;
    }

    /// <summary>Every companion of <paramref name="owner"/> goes (its logout, .companion dismiss all).</summary>
    public void DismissAll(Guid owner)
    {
        foreach (var bot in Of(owner)) Remove(bot, "its owner is gone");
    }

    /// <summary><paramref name="bot"/> goes: out of the list, out of the party, off its map.</summary>
    private void Remove(Player bot, string why)
    {
        if (!_hired.Remove(bot.Character.Id)) return;
        world.Parties.RemoveCompanion(bot.Character.Id);
        world.Bots.Despawn(bot, why, BotRefill.Now);
    }

    /// <summary>The party took companion <paramref name="id"/> out (kicked from the party frame, its owner left, the party broke up): it goes.</summary>
    public void LeftParty(Guid id)
    {
        if (_hired.Remove(id, out var bot)) world.Bots.Despawn(bot, "out of its party", BotRefill.Now);
    }

    /// <summary><see cref="BotManager.Despawn"/> took <paramref name="bot"/> away (its map unloaded): out of the list and the party.</summary>
    public void Gone(Player bot, string why)
    {
        if (_hired.Remove(bot.Character.Id)) world.Parties.RemoveCompanion(bot.Character.Id);
    }

    /// <summary>A tank's hit of <paramref name="damage"/> on <paramref name="mob"/> counts Companions:TankThreatFactor times as threat (the rest added here).</summary>
    public void Threat(Player attacker, Mob mob, double damage)
    {
        if (attacker.Bot?.Companion is { Kind: CompanionKind.Tank } && Options.TankThreatFactor > 1 && !mob.Dead)
            mob.AI.AddThreat(attacker, damage * mob.Template.DamageAggroFactor * (Options.TankThreatFactor - 1));
    }

    /// <summary>
    /// The spot of place <paramref name="index"/> around <paramref name="owner"/> on <paramref name="map"/>: 2.5 m behind-left, behind-right,
    /// left or right of his facing, on the mobs' walk grid when it has one (the nearest free cell within 5 m), facing his way.
    /// </summary>
    public (Vector2 At, float Facing) SpotNear(Player owner, Map map, int index)
    {
        var here = owner.PositionAt(DateTime.UtcNow);
        var angle = (owner.Facing + index switch { 0 => 150f, 1 => 210f, 2 => 90f, _ => 270f }) * MathF.PI / 180;
        var spot = here + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * 2.5f;
        if (MobNav.Grid(world, map) is { } grid)
            spot = grid.Free(spot) && grid.ClearFree(here, spot) ? spot : grid.NearestFree(here, 5) ?? here;
        return (spot, owner.Facing);
    }

    /// <summary>
    /// The world tick (after the bots'): each companion whose owner is not in game for Companions:OwnerGraceSeconds goes; one whose owner is
    /// on another map (a cluster change, an instance) is put next to him there once that map has its cluster; on his map one beyond
    /// Companions:TeleportDistance, or not nearer to him for Companions:StuckSeconds while out of his follow distance, is put next to him.
    /// </summary>
    public void Update()
    {
        if (_hired.Count == 0) return;
        var now = world.Clock.Now;
        foreach (var bot in _hired.Values.ToList())
        {
            var info = bot.Bot!.Companion!;
            if (OwnerOf(bot) is not { Map: { } map } owner)
            {
                if ((now - info.OwnerSeen).TotalSeconds >= Options.OwnerGraceSeconds) Remove(bot, "its owner has not come back");
                continue;
            }
            info.OwnerSeen = now;
            if (map.Template is null || owner.Session.Player != owner) continue; // his map is still loading
            if (bot.Map != map)
            {
                Put(bot, owner, map, info);
                continue;
            }
            if (bot.Combat.Down is not null || bot.PvpState.Revive is not null) continue;
            var d = Vector2.Distance(bot.PositionAt(world.Bots.Time), owner.PositionAt(DateTime.UtcNow));
            if (d <= Options.FollowDistance + 1.5 || d < info.BestDistance - 0.5f)
            {
                (info.BestDistance, info.BestAt) = (d, now);
                continue;
            }
            if (d > Options.TeleportDistance || (now - info.BestAt).TotalSeconds >= Options.StuckSeconds && !bot.Bot.Fight.Active)
                Put(bot, owner, map, info);
        }
    }

    private void Put(Player bot, Player owner, Map map, CompanionInfo info)
    {
        var (at, facing) = SpotNear(owner, map, info.Index);
        world.Bots.PutCompanion(bot, map, at, facing);
        (info.BestDistance, info.BestAt) = (Vector2.Distance(at, owner.PositionAt(DateTime.UtcNow)), world.Clock.Now);
    }

    /// <summary>The lines of .companion list for <paramref name="owner"/>.</summary>
    public IEnumerable<string> Describe(Guid owner)
    {
        var n = 0;
        foreach (var bot in Of(owner))
        {
            var info = bot.Bot!;
            var ticks = world.GameNow;
            var where = bot.Map is { } m && m != OwnerOf(bot)?.Map ? $" (on {m.Cluster})" : "";
            yield return string.Create(CultureInfo.InvariantCulture,
                $"{++n}. {bot.Character.Name}: {info.Companion!.Kind} T{info.Tier}, health {bot.Health.At(ticks):0}/{bot.Health.Max:0}, {(bot.Combat.Down is not null ? "knocked down" : info.Brain.State)}{where}");
        }
    }
}
