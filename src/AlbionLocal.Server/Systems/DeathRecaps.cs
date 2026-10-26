using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The death recap (stageP league-pvp SPEC §2.1, B1; analysis/work/stageP/league-pvp/SPEC.md): what hit a player in the last
/// DeathRecap:Seconds (gamedata &lt;DeathRecap timeframe="180"/&gt;) before its knockdown or death, answered to DeathRecap (op 492).
/// <list type="number">
/// <item><b>Recording</b> (<see cref="Hit"/>, <see cref="Effect"/>): every health change of a human player by a mob or a player
/// (bots count as players) and its own heals, and every spell another unit puts on it that is crowd control, a debuff or a mob's,
/// as well as its own spells on itself; older entries than the window are dropped. Not recorded: the mount bar's hits, damage the
/// server deals in the player's own name (the hellgate ring, a bleed-out).</item>
/// <item><b>Freezing</b> (<see cref="Freeze"/>, from <see cref="Knockdown.Start"/> and <see cref="Knockdown.Die"/>): the window
/// before the knockdown or death becomes the player's last recap, kept per character until the next one and for the server's run
/// (a relog reopens it from the character menu; a restart forgets it [choice: the official server keeps it longer, not verified]).</item>
/// <item><b>Answer</b> (<see cref="Response"/>, the layout of the two official responses of captures 103829 and 002835): key 0 the
/// knockdown's GameTimeStamp; one entry per causer in the parallel arrays 1-19, entry 0 always the victim (CauserType 1, its own
/// spells on itself), then the others by their damage, most first. Per causer the buckets (keys 14-19): damage or heal of one
/// (spell, kind) summed with the number of hits merged and the first one's time (keys 16 = 17); every effect a bucket of its own
/// (value 0, key 17 its end). Kind (key 18): 0 a spell or effect (DoT ticks too), 1 a melee auto-attack, 2 a ranged one (spell -1).
/// Mob rows as captured (template "", key 3 -1, its mobs.xml avatar and avatarring, flag 0x80, weapon 0, quality 0, guild "", item
/// power 0); player rows: name, HUMAN_MALE / HUMAN_FEMALE, key 3 0, AVATAR_nn, RING1, its flag, the main hand's item index and
/// quality, the guild's name, the average item power. Keys 9 and 10 (a JSON loadout [infer]) stay "" until a capture with players
/// (R1) shows them.</item>
/// </list>
/// DeathRecap:Enabled false: nothing is recorded and the answer is the empty recap of before. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class DeathRecaps(World world)
{
    /// <summary>cdt.CauserType: System, LocalPlayer, Player, Mob, Unknown.</summary>
    public const byte CauserSystem = 0, CauserLocalPlayer = 1, CauserPlayer = 2, CauserMob = 3;

    /// <summary>Key 6 of a mob row (captured 0x80 for every mob).</summary>
    public const byte MobFlag = 0x80;

    /// <summary>Key 18: a spell or effect, a melee auto-attack, a ranged one.</summary>
    public const int KindSpell = 0, KindMelee = 1, KindRanged = 2;

    /// <summary>One recorded change on the player: its time and (for an effect) end, the causer, the spell (-1 an auto-attack), the kind, the value (negative damage, positive heal, 0 an effect).</summary>
    public sealed record Entry(long At, long End, long Causer, short Spell, int Kind, float Value, bool IsEffect);

    /// <summary>A player's running record (<see cref="PlayerCombat.Recap"/>): the entries in time order and each causer's unit as last seen.</summary>
    public sealed class Record
    {
        internal List<Entry> Entries { get; } = [];
        internal Dictionary<long, IUnit> Sources { get; } = [];
    }

    /// <summary>One bucket of a causer (keys 14-19).</summary>
    public sealed record Bucket(int Spell, float Value, long First, long End, int Kind, int Count);

    /// <summary>One causer row (keys 1-13) with its buckets.</summary>
    public sealed record Causer(string Name, string Template, int Key3, string Avatar, string Ring, byte Flag, int Weapon, short Quality, byte Type,
        string Guild, float ItemPower, IReadOnlyList<Bucket> Buckets)
    {
        public float Total => Buckets.Sum(b => b.Value);
    }

    /// <summary>A frozen recap: the knockdown or death time and the causers, the victim first.</summary>
    public sealed record Recap(long At, IReadOnlyList<Causer> Causers);

    private readonly Dictionary<Guid, Recap> _last = [];

    /// <summary>The last recap of character <paramref name="character"/>; null = none since the server started.</summary>
    public Recap? LastOf(Guid character) => _last.GetValueOrDefault(character);

    /// <summary>The window in seconds: DeathRecap:Seconds, else gamedata &lt;DeathRecap timeframe&gt; (180).</summary>
    public static double Seconds(World world) => world.Config.DeathRecapSeconds ?? world.GameData?.PvpDetails.DeathRecapSeconds ?? Game.PvpDetailSettings.Default.DeathRecapSeconds;

    /// <summary>Key 18 for a HealthUpdate origin: the auto-attacks 1 / 2, everything else 0.</summary>
    public static int KindOf(HitOrigin origin) => origin switch
    {
        HitOrigin.MeleeAutoAttack => KindMelee,
        HitOrigin.RangedAutoAttack => KindRanged,
        _ => KindSpell,
    };

    // ---- recording ----

    /// <summary>
    /// A health change <paramref name="change"/> (negative damage, positive heal) of <paramref name="causer"/> on <paramref name="victim"/>
    /// at <paramref name="ticks"/>: <paramref name="spell"/> (-1 an auto-attack) and the HitOrigin's kind. Nothing for a bot victim (no client
    /// asks) or under DeathRecap:Enabled false.
    /// </summary>
    public static void Hit(Player victim, IUnit causer, short spell, HitOrigin origin, double change, long ticks)
    {
        if (change == 0 || victim.IsBot || !victim.World.Config.DeathRecapEnabled) return;
        Add(victim, causer, new Entry(ticks, ticks, causer.Id, spell, KindOf(origin), (float)change, false));
    }

    /// <summary>
    /// <paramref name="entry"/> applied to <paramref name="victim"/> (<see cref="SpellEffects.Apply"/>): kept when its caster is the victim
    /// itself (entry 0's own effects, as the captured 3275), a mob, or a player whose spell is crowd control or a debuff; its end is the
    /// latest end of its timed sub-effects (its start without one).
    /// </summary>
    public static void Effect(Player victim, ActiveSpell entry, long ticks)
    {
        if (victim.IsBot || !victim.World.Config.DeathRecapEnabled || entry.Spell.Id < 0) return;
        IUnit? causer = entry.Caster == victim.Id ? victim : victim.Map is { } map
            ? map.FindPlayer(entry.Caster) ?? (IUnit?)(map.Objects.GetValueOrDefault(entry.Caster) as Mob) : null;
        if (causer is null) return;
        var spell = entry.Spell;
        var hostile = causer is Mob || causer == victim || spell.Category == "debuff" || spell.UiType is "debuff" or "crowdcontrol"
            || entry.Subs.Any(s => s.Active && s.CcType is not null);
        if (!hostile) return;
        var end = entry.Subs.Where(s => s.Active && s.DurationMs > 0).Select(entry.EndOf).DefaultIfEmpty(entry.Start).Max();
        Add(victim, causer, new Entry(ticks, Math.Max(ticks, end), causer.Id, spell.Id, KindSpell, 0, true));
    }

    private static void Add(Player victim, IUnit causer, Entry e)
    {
        var log = victim.Combat.Recap;
        Prune(victim, log, e.At);
        log.Entries.Add(e);
        log.Sources[causer.Id] = causer;
    }

    private static void Prune(Player victim, Record log, long now)
    {
        var from = now - SpellCasts.Seconds(Seconds(victim.World));
        if (log.Entries.Count == 0 || log.Entries[0].At >= from) return;
        log.Entries.RemoveAll(x => x.At < from);
        var live = log.Entries.Select(x => x.Causer).ToHashSet();
        foreach (var gone in log.Sources.Keys.Where(id => !live.Contains(id)).ToList()) log.Sources.Remove(gone);
    }

    // ---- freezing ----

    /// <summary>
    /// <paramref name="victim"/>'s knockdown or death at <paramref name="ticks"/>: the window before it becomes its last recap (an empty
    /// window keeps none: the answer is then the empty recap).
    /// </summary>
    public void Freeze(Player victim, long ticks)
    {
        if (victim.IsBot || !world.Config.DeathRecapEnabled) return;
        var log = victim.Combat.Recap;
        Prune(victim, log, ticks);
        var entries = log.Entries.Where(e => e.At <= ticks).ToList();
        if (entries.Count == 0) return;
        var rows = new List<Causer>();
        foreach (var group in entries.GroupBy(e => e.Causer))
        {
            var source = log.Sources.GetValueOrDefault(group.Key);
            rows.Add(Row(victim, source, group.Key == victim.Id, Buckets(group)));
        }
        if (!rows.Any(r => r.Type == CauserLocalPlayer)) rows.Add(Row(victim, victim, true, []));
        var ordered = rows.Where(r => r.Type == CauserLocalPlayer).Concat(rows.Where(r => r.Type != CauserLocalPlayer).OrderBy(r => r.Total)).ToList();
        _last[victim.Character.Id] = new Recap(ticks, ordered);
        Log.Debug("combat", $"'{victim.Character.Name}' death recap: {ordered.Count - 1} causer(s), {entries.Count} entries");
    }

    /// <summary>The buckets of one causer's entries: damage and heals merged by (spell, kind, sign), every effect its own, in time order.</summary>
    private static List<Bucket> Buckets(IEnumerable<Entry> entries)
    {
        var list = new List<Bucket>();
        var merged = new Dictionary<(short, int, bool), int>();
        foreach (var e in entries.OrderBy(e => e.At))
        {
            if (e.IsEffect)
            {
                list.Add(new Bucket(e.Spell, 0, e.At, e.End, e.Kind, 1));
                continue;
            }
            var key = (e.Spell, e.Kind, e.Value > 0);
            if (merged.TryGetValue(key, out var at))
            {
                var b = list[at];
                list[at] = b with { Value = b.Value + e.Value, Count = b.Count + 1 };
            }
            else
            {
                merged[key] = list.Count;
                list.Add(new Bucket(e.Spell, e.Value, e.At, e.At, e.Kind, 1));
            }
        }
        return list;
    }

    /// <summary>A causer's row; <paramref name="source"/> null (gone from memory) reads as an unknown player.</summary>
    private Causer Row(Player victim, IUnit? source, bool self, List<Bucket> buckets)
    {
        switch (source)
        {
            case Mob m:
                return new Causer(m.Template.Name, "", -1, m.Template.Avatar, m.Template.AvatarRing, MobFlag, 0, 0, CauserMob, "", 0, buckets);
            case Player p:
            {
                var main = p.Inventory.Equipment[0];
                var avatar = p.Bot is { } bot ? bot.Avatar : p.Character.Template;
                return new Causer(p.Character.Name ?? "", p.Character.Appearance == 1 ? "HUMAN_FEMALE" : "HUMAN_MALE", 0,
                    avatar is >= 0 and <= 98 ? $"AVATAR_{avatar + 1:00}" : "AVATAR_01", "RING1", p.Flag, main is null ? 0 : main.Template.Index,
                    (short)(main?.Quality ?? 0), self ? CauserLocalPlayer : CauserPlayer, world.Guilds.Of(p.Character.Id)?.Name ?? "",
                    (float)Expeditions.AverageItemPower(p), buckets);
            }
            default:
                return new Causer("", "", -1, "", "", 0, 0, 0, self ? CauserLocalPlayer : CauserSystem, "", 0, buckets);
        }
    }

    // ---- the answer ----

    /// <summary>DeathRecap (op 492) for <paramref name="p"/>: its last recap in the captured layout, else the empty recap.</summary>
    public DeathRecapResponse Response(Player p)
    {
        if (!world.Config.DeathRecapEnabled || LastOf(p.Character.Id) is not { } r) return new DeathRecapResponse().FillEmpty();
        var c = r.Causers;
        T[] Each<T>(Func<Causer, T> f) => [.. c.Select(f)];
        T[][] Rows<T>(Func<Bucket, T> f) => [.. c.Select(x => x.Buckets.Select(f).ToArray())];
        return new DeathRecapResponse
        {
            api = r.At,
            apj = Each(x => x.Name), apk = Each(x => x.Template), apl = Each(x => x.Key3), apm = Each(x => x.Avatar), apn = Each(x => x.Ring),
            apo = Each(x => x.Flag), app = Each(x => x.Weapon), apq = Each(x => x.Quality), apr = Each(_ => ""), aps = Each(_ => ""),
            apt = Each(x => x.Type), apu = Each(x => x.Guild), apv = Each(x => x.ItemPower),
            apw = Rows(b => b.Spell), apx = Rows(b => b.Value), apy = Rows(b => b.First), apz = Rows(b => b.End), ap0 = Rows(b => b.Kind),
            ap1 = Rows(b => b.Count),
        };
    }
}
