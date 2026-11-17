using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The kill history of the stats window (analysis/work/stageR/stats/SPEC.md §1, "SPEC" here). Every PvP death (<see cref="Death.Kill"/> with a
/// player killer) and every PvP knockdown (<see cref="Pvp.KnockedDown"/>) between two human players is kept with a new Guid, its time, both
/// names, the killer's fame, both sides' item power and the details JSON of the client's Albion.Common.GameData.KillHistoryDetails (both
/// loadouts, guilds, avatars, the cluster).
/// <list type="bullet">
/// <item>GetCharacterStats (145 {0 a name, 1 RankingEntityTypes}) for a player: the rc-0 answer and the four lists the client's ctw keeps per
/// name (ctw.aho 0x1809b98f0): CharacterStatsKillHistory (143), DeathHistory (144), KnockDownHistory (145), KnockedDownHistory (146), each
/// {0 the asker's object, 1 the name, 2 the other side's names, (3 the fame, 143/144 only), the kill Guids as bytes, the other side's item
/// power [assume], the GameTimeStamps}, the newest first, at most <see cref="MaxRows"/> each [assume].</item>
/// <item>GetKillHistoryDetails (146 {0 the Guid}; cte.a.q 0x180bf4ef0): KillHistoryDetails (148) {1 the Guid's bytes, 2 the JSON, 3 a knockdown}
/// read by cte.a 0x180c05940, then the empty rc-0 answer; an unknown Guid only the answer with an error (cte.a 0x180c05210: "not available").</item>
/// </list>
/// The rows are saved at once (<see cref="IKillHistoryStore"/>, table kill_history). Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class KillHistory(World world)
{
    /// <summary>The rows each list of the stats window gets [assume: the official count is not known].</summary>
    public const int MaxRows = 50;

    /// <summary>KillHistoryDetails.FighterType: Unknown, Player, Mob, Warcamp, Orb.</summary>
    public const int FighterPlayer = 1;

    /// <summary>The details JSON's version (key v; the client reads it, its meaning is not known) [assume].</summary>
    public const int DetailsVersion = 1;

    private List<KillRow>? _rows;
    private readonly Dictionary<Guid, KillRow> _byId = [];

    private IKillHistoryStore? Store => world.Characters as IKillHistoryStore;

    private List<KillRow> Rows
    {
        get
        {
            if (_rows is not null) return _rows;
            _rows = [];
            try
            {
                foreach (var row in Store?.LoadKills() ?? []) Add(row);
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                Log.Warn("pvp", $"kill history not read: {e.Message}");
            }
            return _rows;
        }
    }

    private void Add(KillRow row)
    {
        _rows!.Add(row);
        _byId[row.Id] = row;
    }

    /// <summary>The row of <paramref name="id"/>; null = none.</summary>
    public KillRow? Find(Guid id)
    {
        _ = Rows;
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>The rows of character <paramref name="character"/> as killer (<paramref name="asKiller"/>) or victim, deaths or knockdowns, the newest first.</summary>
    public IReadOnlyList<KillRow> Of(Guid character, bool asKiller, bool knockdown) =>
        [.. Rows.Where(r => r.Knockdown == knockdown && (asKiller ? r.Killer : r.Victim) == character).OrderByDescending(r => r.At).Take(MaxRows)];

    /// <summary>
    /// A PvP death or knockdown of <paramref name="victim"/> by <paramref name="killer"/> (both human players; bots are left out) with the killer's
    /// fame (a7m): a new row, saved at once. Call it before the victim's items drop (the loadouts are read here).
    /// </summary>
    public KillRow? Record(Player killer, Player victim, bool knockdown, long fame, long ticks)
    {
        if (killer.IsBot || victim.IsBot || killer == victim) return null;
        _ = Rows; // the saved rows first
        var (kip, vip) = ((int)Math.Round(Expeditions.AverageItemPower(killer)), (int)Math.Round(Expeditions.AverageItemPower(victim)));
        var details = new Details
        {
            v = DetailsVersion,
            dt = new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            f = fame / 10000, p = Math.Max(1, Death.Attackers(victim).Count), c = victim.Map?.Cluster ?? "",
            kn = killer.Character.Name ?? "", kft = FighterPlayer, kgn = world.Guilds.Of(killer.Character.Id)?.Name ?? "", ki = Loadout(killer),
            kp = kip, kat = killer.Character.Template, kfe = killer.Faction,
            dn = victim.Character.Name ?? "", dgn = world.Guilds.Of(victim.Character.Id)?.Name ?? "", di = Loadout(victim),
            dp = vip, dat = victim.Character.Template, dfe = victim.Faction,
        };
        var row = new KillRow(Guid.NewGuid(), ticks, killer.Character.Id, details.kn, victim.Character.Id, details.dn, knockdown, Math.Max(0, fame), kip, vip,
            JsonSerializer.Serialize(details, Json));
        Add(row);
        try
        {
            Store?.SaveKill(row);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Log.Warn("pvp", $"kill history row not saved: {e.Message}");
        }
        return row;
    }

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    /// <summary>The equipment slots in order (the client's loadout view skips a list of another length; an empty slot is null).</summary>
    private static DetailItem?[] Loadout(Player p)
    {
        var e = p.Inventory.Equipment;
        var list = new DetailItem?[e.Size];
        for (var i = 0; i < e.Size; i++)
            if (e[i] is { } item)
                list[i] = new DetailItem { id = item.Template.Name, c = Math.Max(1, item.Count), q = item.Quality };
        return list;
    }

    /// <summary>
    /// GetCharacterStats for <paramref name="name"/> asked by <paramref name="asker"/>: the four lists (see the class); none for an unknown name.
    /// </summary>
    public IEnumerable<AlbionEvent> StatsEvents(Player asker, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;
        var character = world.Characters.Find(name);
        if (character is null) yield break;
        var id = character.Id;
        var kills = Of(id, true, false);
        var deaths = Of(id, false, false);
        var downs = Of(id, true, true);
        var downed = Of(id, false, true);
        yield return new CharacterStatsKillHistoryEvent
        {
            ddn = asker.Id, dpv = character.Name, dpw = [.. kills.Select(r => r.VictimName)], dpx = [.. kills.Select(r => r.Fame)],
            dpy = [.. kills.Select(r => r.Id.ToByteArray())], dpz = [.. kills.Select(r => r.VictimIp)], dp0 = [.. kills.Select(r => r.At)],
        };
        yield return new CharacterStatsDeathHistoryEvent
        {
            ddn = asker.Id, dpp = character.Name, dpq = [.. deaths.Select(r => r.KillerName)], dpr = [.. deaths.Select(r => r.Fame)],
            dps = [.. deaths.Select(r => r.Id.ToByteArray())], dpt = [.. deaths.Select(r => r.KillerIp)], dpu = [.. deaths.Select(r => r.At)],
        };
        yield return new CharacterStatsKnockDownHistoryEvent
        {
            ddn = asker.Id, dp1 = character.Name, dp2 = [.. downs.Select(r => r.VictimName)], dp3 = [.. downs.Select(r => r.Id.ToByteArray())],
            dp4 = [.. downs.Select(r => r.VictimIp)], dp5 = [.. downs.Select(r => r.At)],
        };
        yield return new CharacterStatsKnockedDownHistoryEvent
        {
            ddn = asker.Id, dp6 = character.Name, dp7 = [.. downed.Select(r => r.KillerName)], dp8 = [.. downed.Select(r => r.Id.ToByteArray())],
            dp9 = [.. downed.Select(r => r.KillerIp)], dqa = [.. downed.Select(r => r.At)],
        };
    }

    /// <summary>KillHistoryDetails (148) of <paramref name="id"/> for <paramref name="asker"/>; null = no such kill.</summary>
    public KillHistoryDetailsEvent? DetailsEvent(Player asker, Guid id) =>
        Find(id) is { } row ? new KillHistoryDetailsEvent { ddn = asker.Id, drr = row.Id.ToByteArray(), drs = row.Details, drt = row.Knockdown } : null;

    /// <summary>For .kills: the counts of <paramref name="p"/>'s four lists.</summary>
    public string Describe(Player p)
    {
        var id = p.Character.Id;
        return $"Kill history: {Of(id, true, false).Count} kills, {Of(id, false, false).Count} deaths, {Of(id, true, true).Count} knockdowns, {Of(id, false, true).Count} times knocked down (newest {MaxRows} each)";
    }

    // The client's Albion.Common.GameData.KillHistoryDetails, read by JsonFx by field name (SPEC §1.3).
#pragma warning disable IDE1006, CS0649 // the client's field names; the ones this server leaves at their default
    private sealed class Details
    {
        public int v;
        public string dt = "";
        public long f;
        public int p;
        public string c = "";
        public string kn = "";
        public int kft;
        public string kgn = "";
        public DetailItem?[] ki = [];
        public float kp;
        public int kat;
        public byte kfe;
        public int kct;
        public string dn = "";
        public string dgn = "";
        public DetailItem?[] di = [];
        public float dp;
        public int dat;
        public byte dfe;
        public int dct;
    }

    private sealed class DetailItem
    {
        public string id = "";
        public int c;
        public int q;
        public string[] asp = [];
        public string[] psp = [];
        public string cr = "";
        public byte csl;
        public byte csmax;
        public byte csemax;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? leg;
    }
#pragma warning restore IDE1006, CS0649
}
