using AlbionLocal.Server.Content;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Bots;

/// <summary>The kind of a roster identity: the world roles it plays (a Fighter is a patrol or a Drifter).</summary>
public enum BotKind
{
    City,
    Gatherer,
    Fighter,
}

/// <summary>
/// One identity of the roster (<see cref="BotRoster"/>): its place, name, character Guid, kind, home tier (the gear tier it wears where its
/// zone allows that tier) and the seeds of its look and its gear.
/// </summary>
public sealed record BotIdentity(int Index, string Name, Guid Id, BotKind Kind, int Tier, int LookSeed, int GearSeed)
{
    /// <summary>The random numbers of its look (the same face, hair and portrait every time).</summary>
    public Random LookRandom() => new(LookSeed);

    /// <summary>The random numbers of its gear as <paramref name="role"/> at <paramref name="tier"/> (gathering <paramref name="resource"/>): the same set every time.</summary>
    public Random GearRandom(BotRole role, int tier, string? resource) =>
        new(StableHash(GearSeed, (int)role, tier, resource ?? ""));

    /// <summary>A hash that is the same in every process (HashCode is randomized per process).</summary>
    private static int StableHash(int seed, int role, int tier, string text)
    {
        unchecked
        {
            var h = (uint)seed * 2654435761u;
            h = (h ^ (uint)role) * 16777619u;
            h = (h ^ (uint)tier) * 16777619u;
            foreach (var c in text) h = (h ^ c) * 16777619u;
            return (int)(h & 0x7fffffff);
        }
    }
}

/// <summary>
/// Bot-roster (analysis/work/official-audit/audit-bots.md improvement 5, AzerothCore's random bots): a fixed pool of Bots:Roster:Size bot
/// identities derived from Bots:Roster:Seed and content/bots.json's name syllables alone, so the same seed gives the same names, Guids, kinds,
/// tiers, looks and gear on every start; nothing is stored. Kinds: 40 % City, 35 % Gatherer, 25 % Fighter. A City identity's tier is drawn
/// by CityTiers (15 % of them T1-T2 for the start towns), the others' uniform T1-T8. The population takes a free identity of the slot's kind
/// (<see cref="Pick"/>): offline (no live bot, no character, no human of the name) and held by no other slot.
/// </summary>
public sealed class BotRoster
{
    private readonly Dictionary<BotKind, List<BotIdentity>> _byKind = [];

    public IReadOnlyList<BotIdentity> All { get; }

    public int Seed { get; }

    private BotRoster(int seed, List<BotIdentity> all)
    {
        Seed = seed;
        All = all;
        foreach (var kind in Enum.GetValues<BotKind>()) _byKind[kind] = [.. all.Where(i => i.Kind == kind)];
    }

    /// <summary>The kind that plays world role <paramref name="role"/>; null for a role the roster does not fill (companions, arena, hellgate, invader).</summary>
    public static BotKind? KindOf(BotRole role) => role switch
    {
        BotRole.City => BotKind.City,
        BotRole.Gatherer => BotKind.Gatherer,
        BotRole.Patrol or BotRole.Drifter => BotKind.Fighter,
        _ => null,
    };

    /// <summary>
    /// The roster of <paramref name="seed"/> with up to <paramref name="size"/> identities of unique names (<see cref="BotNames"/> shape; fewer
    /// when the syllables run out). The same arguments always give the same roster.
    /// </summary>
    public static BotRoster Build(BotSettings settings, int seed, int size)
    {
        var random = new Random(seed);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<BotIdentity>(size);
        var cityTiers = settings.CityTiers.Where(w => w.Value > 0).ToList();
        var cityWeight = cityTiers.Sum(w => w.Value);
        for (var i = 0; i < size; i++)
        {
            if (BotNames.Next(settings, random, n => names.Contains(n) || settings.ReservedNames.Contains(n), 200) is not { } name) break;
            names.Add(name);
            var k = random.NextDouble();
            var kind = k < 0.40 ? BotKind.City : k < 0.75 ? BotKind.Gatherer : BotKind.Fighter;
            int tier;
            if (kind == BotKind.City)
            {
                if (random.NextDouble() < 0.15 || cityWeight == 0) tier = 1 + random.Next(2);
                else
                {
                    var roll = random.Next(cityWeight);
                    tier = 4;
                    foreach (var (t, w) in cityTiers)
                        if ((roll -= w) < 0) { tier = Math.Clamp(t, 1, 8); break; }
                }
            }
            else tier = 1 + random.Next(8);
            var bytes = new byte[16];
            bytes[0] = 0xb0;
            bytes[1] = 0x70;
            bytes[2] = 0x7e;
            BitConverter.TryWriteBytes(bytes.AsSpan(4), seed);
            BitConverter.TryWriteBytes(bytes.AsSpan(8), i);
            BitConverter.TryWriteBytes(bytes.AsSpan(12), random.Next());
            all.Add(new BotIdentity(i, name, new Guid(bytes), kind, tier, random.Next(), random.Next()));
        }
        return new BotRoster(seed, all);
    }

    /// <summary>
    /// A free identity of the kind of <paramref name="role"/> (<paramref name="free"/>), its home tier nearest to <paramref name="tier"/>
    /// (one at random among the nearest), never <paramref name="except"/> while another is free; null when every one of the kind is taken.
    /// </summary>
    public BotIdentity? Pick(BotRole role, int tier, Func<BotIdentity, bool> free, Random random, int? except = null)
    {
        if (KindOf(role) is not { } kind) return null;
        BotIdentity? fallback = null;
        foreach (var group in _byKind[kind].GroupBy(i => Math.Abs(i.Tier - tier)).OrderBy(g => g.Key))
        {
            var list = group.ToList();
            // A random start, then in order: a random free one of the group without testing them all.
            var start = random.Next(list.Count);
            for (var n = 0; n < list.Count; n++)
            {
                var id = list[(start + n) % list.Count];
                if (!free(id)) continue;
                if (id.Index != except) return id;
                fallback = id;
            }
        }
        return fallback;
    }
}
