using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Prospective, local Armory observations, touched under World.Sync. No preexisting inventory or combat totals are
/// imported. A use is a connected interval with one activity/location/build; gear/spell changes, leaving and restart
/// end it. Only time within 60 seconds of an observed action counts (no idle/offline duration). Eligibility/minuses
/// come from armory.bin. Local ranking is qualified uses then equipment ID, not the proprietary official score.
/// </summary>
public sealed class ArmoryUsage
{
    private readonly World world;
    private readonly IArmoryStore? store;
    private readonly IReadOnlyDictionary<string, HashSet<string>> familyCategories;
    private static readonly string[] CategoryLevels = ["shopcategory", "shopsubcategory1", "shopsubcategory2", "shopsubcategory3"];
    private readonly Dictionary<Guid, ArmorySample> samples = [];
    private readonly Dictionary<Guid, Live> live = [];
    private readonly HashSet<Guid> dirty = [];
    private long tickAt, flushAt;
    private sealed class Live(Player p, ArmorySample sample, long now, bool gathering)
    {
        public Player Player = p;
        public ArmorySample Sample = sample;
        public long LastTick = now, LastAction = now;
        public bool Gathering = gathering;
    }
    public ArmoryData Data { get; }
    public ArmoryUsage(World world, IArmoryStore? store)
    {
        this.world = world;
        this.store = store;
        // 345319 CF7040/CF7368 serialize cfp.g (8AC390): item family, most-relevant category or parent category.
        // A type-dropdown choice such as "sword" is not the family "MAIN_SWORD". Resolve all category levels from
        // the existing item definitions, once; use the recorded slot family rather than the character's current gear.
        familyCategories = world.Items.All.Where(i => i.Slot.Length > 0).GroupBy(Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(i => CategoryLevels.Select(k => i.Attrs.GetValueOrDefault(k)))
                .OfType<string>().Where(s => s.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Data = ArmoryData.Load(world.Config.ClientGameDataPath);
        if (store is not null)
            foreach (var s in store.LoadArmorySamples(RetainSince)) samples[s.Id] = s;
    }
    private long RetainSince => Math.Max(0, world.GameNow - TimeSpan.FromDays(45).Ticks);
    public static string Family(ItemTemplate item)
    {
        // Installed 345319 EquipmentIdHelper 8AAAC0 -> bdi.m8 5F5760: tierless item-family name.
        var name = item.BaseName;
        return name.Length > 3 && name[0] == 'T' && char.IsDigit(name[1]) && name[2] == '_' ? name[3..] : name;
    }
    private (string Equipment, string Consumables, string Spells, string[] Slots) Build(Player p)
    {
        var slots = Enumerable.Range(0, 10).Select(i => p.Inventory.Equipment[i] is { } item ? Family(item.Template) : "").ToArray();
        var equipment = string.Join(':', new[] { 0, 1, 2, 3, 4, 6 }.Select(i => slots[i]).Where(s => s.Length > 0));
        var consumables = string.Join(':', new[] { 8, 9 }.Select(i => slots[i]).Where(s => s.Length > 0));
        // cfm.ParseSpellId 8AA520: ':' between equipment slots, ',' between selected spells in that slot.
        // Its .cctor8AA310 fixes spell slot order to 0,2,3,4 (not offhand/cape).
        var spells = string.Join(':', new[] { 0, 2, 3, 4 }.Select(i => p.Inventory.Equipment[i] is { } item
            ? string.Join(',', item.Active.Concat(item.Passive).Select(id => world.Spells?.Name(id)).OfType<string>()) : ""));
        return (equipment, consumables, spells, slots);
    }
    private int Activity(Player p, bool gathering)
    {
        if (p.Map is not { TutorialOwner: null } map || p.IsBot || p.Character.Transient) return -1;
        if (world.Arenas.Find(map.Cluster) is { } arena)
            return arena.State == ArenaState.Fight ? arena.Mode.Category switch { 0 => 10, 1 => 9, _ => -1 } : -1;
        if (world.Corrupted.Find(map.Cluster) is not null) return 4;
        if (world.Hellgates.Find(map.Cluster) is { } hell) return hell.Mode.TeamSize switch { 2 => 5, 5 => 6, _ => -1 };
        if (world.Mists.Find(map.Cluster) is not null) return 11;
        if (world.Dungeons.Find(map.Cluster) is { } dungeon)
            return dungeon.Group.Dragon ? 28 : dungeon.Group.Exit.Subtype switch { 1 or 4 => 17, 2 or 5 => 18, _ => -1 };
        if (world.Expeditions.InstanceOf(p.Character.Id) is { } expedition)
            return expedition.Category.GroupType == "HARDCORE" ? 20 : -1;
        if (gathering) return 16;
        if (world.Roads.IsRoad(map.Cluster)) return 15;
        // Only ordinary combat-capable world maps. Do not classify housing/cities/unknown instances as world combat.
        if (map.Cluster.StartsWith('@') || map.Template?.Pvp.Rule is not (PvpRule.PvpAllowed or PvpRule.PvpForced)) return -1;
        // Local grouping policy; unknown encounter-specific activities (ZvZ, castles, etc.) are not inferred from map names.
        var size = world.Parties.Of(p.Character.Id)?.Members.Count ?? 1;
        return size == 1 ? 0 : size <= 7 ? 1 : 2;
    }
    private Live? Touch(Player p, bool gathering = false)
    {
        var activity = Activity(p, gathering);
        if (activity < 0 || Data.Find(activity) is null || p.Inventory.Equipment[0] is null) return null;
        var now = world.GameNow;
        var build = Build(p);
        var key = p.Map!.InstanceKey;
        if (live.TryGetValue(p.Character.Id, out var prior))
        {
            if (now - prior.LastAction <= TimeSpan.FromSeconds(60).Ticks
                && prior.Player == p && prior.Sample.Activity == activity && prior.Sample.Location == key
                && prior.Sample.Equipment == build.Equipment && prior.Sample.Consumables == build.Consumables && prior.Sample.Spells == build.Spells)
            {
                Accrue(prior, now);
                prior.LastAction = now;
                return prior;
            }
            live.Remove(p.Character.Id);
        }
        var sample = new ArmorySample { Character = p.Character.Id, Activity = activity, Location = key,
            Started = now, Updated = now, Equipment = build.Equipment, Consumables = build.Consumables,
            Spells = build.Spells, Slots = build.Slots };
        sample.Metrics[(int)ArmoryMetric.Uses] = 1;
        samples[sample.Id] = sample;
        dirty.Add(sample.Id);
        var result = new Live(p, sample, now, gathering);
        ObservePower(result);
        return live[p.Character.Id] = result;
    }
    private void Accrue(Live row, long now)
    {
        var until = Math.Min(now, row.LastAction + TimeSpan.FromSeconds(60).Ticks);
        if (until > row.LastTick)
        {
            row.Sample.DurationSeconds += (decimal)(until - row.LastTick) / TimeSpan.TicksPerSecond;
            row.Sample.Updated = until;
            dirty.Add(row.Sample.Id);
        }
        row.LastTick = now;
    }
    private void ObservePower(Live row)
    {
        var ip = (int)Math.Clamp(Network.WorldSession.AverageItemPower(row.Player), 0, 100000);
        var s = row.Sample;
        s.MinItemPower = s.ItemPowerObservations == 0 ? ip : Math.Min(s.MinItemPower, ip);
        s.MaxItemPower = Math.Max(s.MaxItemPower, ip);
        s.ItemPowerSum += ip;
        s.ItemPowerObservations++;
    }
    private void Add(Player p, ArmoryMetric metric, decimal amount, bool gathering = false)
    {
        if (amount <= 0 || Touch(p, gathering) is not { } row) return;
        row.Sample.Metrics[(int)metric] = Math.Min(1000000000000000000m, row.Sample.Value(metric) + amount);
        row.Sample.Updated = world.GameNow;
        dirty.Add(row.Sample.Id);
    }
    public void OnFame(Player p, long gain, FameContext context, bool pvp)
    {
        if (gain <= 0 || context.Only is not null) return;
        if (pvp) Add(p, ArmoryMetric.PvPFameGained, gain / 10000m);
        else if (context.Source == FameSource.Kill) Add(p, ArmoryMetric.PvEFameGained, gain / 10000m);
        // armory.bin Gathering's descriptive/score metric is PvEFameGained. Count the fame actually awarded for
        // harvesting/fishing too; previously these actions started a use but left its earned fame at zero.
        else if (context.Source is FameSource.Harvest or FameSource.Fishing) Add(p, ArmoryMetric.PvEFameGained, gain / 10000m, gathering: true);
    }
    public void Damage(Player? attacker, Player? target, double effective)
    {
        if (!double.IsFinite(effective) || effective <= 0) return;
        if (target is not null) world.Arenas.RecordDamage(attacker, target, effective);
        var value = (decimal)Math.Min(effective, 1000000000000);
        if (attacker is not null) Add(attacker, ArmoryMetric.DamageDealt, value);
        if (target is not null) Add(target, ArmoryMetric.DamageReceived, value);
    }
    public void Heal(Player caster, Player target, double effective)
    {
        if (!double.IsFinite(effective) || effective <= 0) return;
        world.Arenas.RecordHealing(caster, target, effective);
        var value = (decimal)Math.Min(effective, 1000000000000);
        Add(caster, ArmoryMetric.HealingDone, value);
        Add(target, ArmoryMetric.HealingReceived, value);
    }
    public void MobKilled(Player p) => Add(p, ArmoryMetric.MobsKilled, 1);
    public void PvpKilled(PvpKill result)
    {
        if (result.Outcome != PvpOutcome.Death || result.Killer.Player is not { IsBot: false } killer
            || result.Victim.Player is not { IsBot: false } victim || killer.Character.Account == victim.Character.Account) return;
        Add(killer, ArmoryMetric.Kills, 1);
        Add(victim, ArmoryMetric.Deaths, 1);
    }
    public void ArenaEnded(Player p, bool won)
    {
        // Called before the match leaves Fight. Match results are observed, including local matches with real bots;
        // bots themselves never become samples. Outside arena, nonlethal knockdowns are not counted as deaths.
        if (won) Add(p, ArmoryMetric.SuccessWin, 1);
        live.Remove(p.Character.Id);
    }
    public void ArenaDefeated(Player? attacker, Player target)
    {
        world.Arenas.RecordDefeat(attacker, target);
        // These are the arena's actual ticket-loss defeats, not fabricated lethal world kills.
        if (attacker is not null && attacker != target) Add(attacker, ArmoryMetric.Kills, 1);
        Add(target, ArmoryMetric.Deaths, 1);
    }
    public void Update()
    {
        var now = world.GameNow;
        if (now < tickAt) return;
        tickAt = now + TimeSpan.TicksPerSecond;
        foreach (var (id, row) in live.ToArray())
        {
            var p = row.Player;
            var build = Build(p);
            if (p.Map is null || !p.Map.Players.ContainsKey(p.Id) || p.Map.InstanceKey != row.Sample.Location
                || Activity(p, row.Gathering) != row.Sample.Activity || build.Equipment != row.Sample.Equipment
                || build.Consumables != row.Sample.Consumables || build.Spells != row.Sample.Spells)
            { live.Remove(id); continue; }
            Accrue(row, now);
            if (now - row.LastAction > TimeSpan.FromSeconds(60).Ticks) { live.Remove(id); continue; }
            ObservePower(row);
        }
        if (now >= flushAt) { Flush(); flushAt = now + TimeSpan.FromSeconds(30).Ticks; }
    }
    public void Flush()
    {
        if (dirty.Count == 0) return;
        try
        {
            store?.SaveArmorySamples(dirty.Select(id => samples[id]).ToArray(), RetainSince);
            dirty.Clear();
            foreach (var id in samples.Where(x => x.Value.Updated < RetainSince).Select(x => x.Key).ToArray()) samples.Remove(id);
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        { Log.Warn("armory", $"Save deferred; observations retained: {ex.Message}"); }
    }
    public sealed record BuildUsage(int Activity, string Equipment, IReadOnlyList<ArmorySample> Samples)
    {
        public decimal Metric(ArmoryMetric metric) => metric == ArmoryMetric.Uses ? Samples.Count : Samples.Average(s => s.Value(metric));
    }
    public IReadOnlyList<BuildUsage> Query(int activity, int days, int[]? slots = null, string[]? filters = null, int direction = -1)
    {
        if (days is not (10 or 35) || Data.Find(activity) is not { } rule) return [];
        if ((slots?.Length ?? 0) != (filters?.Length ?? 0) || slots?.Any(s => s is < 0 or > 9) == true) return [];
        // Rolling local window. The official client labels these Current(10)/Stable(35); its server's batch boundaries
        // and ranking formula are not reproduced. Queries never create observations or advance character state.
        var since = world.GameNow - TimeSpan.FromDays(days).Ticks;
        bool Filter(ArmorySample s) => slots is null || filters is null || slots.Select((slot, i) => (slot, filter: filters[i]))
            .All(f => f.filter.Length == 0 || s.Slots[f.slot].Equals(f.filter, StringComparison.OrdinalIgnoreCase)
                || familyCategories.TryGetValue(s.Slots[f.slot], out var categories) && categories.Contains(f.filter));
        var qualified = samples.Values.Where(s => s.Activity == activity && s.Updated >= since && Data.Accepts(s) && Filter(s))
            .GroupBy(s => s.Equipment, StringComparer.Ordinal).Where(g => g.Count() >= rule.MinimumUses)
            .Select(g => new BuildUsage(activity, g.Key, g.ToArray()));
        var ordered = direction == 1 ? qualified.OrderBy(g => g.Samples.Count) : qualified.OrderByDescending(g => g.Samples.Count);
        return ordered.ThenBy(g => g.Equipment, StringComparer.Ordinal).Take(100).ToArray();
    }
}
