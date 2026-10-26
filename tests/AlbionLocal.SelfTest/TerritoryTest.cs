using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The guild territories, phase P1 (analysis/work/stageP/territories/SPEC.md §6): the data layer, the monolith and castle of a black cluster,
/// the territory window's info answers and the world map's territory keys behind Territories:WorldMapInfo.
/// </summary>
public static class TerritoryTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("territories: world.xml 268 watchtowers + 23 castles + 95 outposts, castles.bin index, timeregions/prime time, SEASON_34 claim windows, config presets", Data),
        ("territories: 0305's monolith (109: key 7 the territory, its member's position) and MEDIUMCASTLE (112: T1_CASTLE, type 1, chest 8), unowned; 117/118/120 answers", Spawn),
        ("territories: WorldMapInfo off -> no event for a territory cluster; on -> keys 4-18 all one entry per territory", WorldMap),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    /// <summary>2026-10-17 12:00 UTC: day 36 of GVG_SEASON_34 (Europe), inside its second claimable window (day 29 18:00 to day 57 11:00).</summary>
    private static readonly DateTime Saturday = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Lazy<GvgSeasonData> Europe = new(() => GvgSeasonData.Load(ClientGameDataDir(), "europe"));

    /// <summary>The Wetgrave Bog (0305) territories: a watchtower and a MEDIUMCASTLE.</summary>
    private static readonly Guid Coombe = Guid.Parse("44b03c90-a6b5-4a05-bd5e-f27387856def"), Tor = Guid.Parse("47306493-1904-48d8-a77e-f9fd5cfd309e");

    private static World MakeWorld(ServerConfig config, ManualClock clock) => new(config, Repository(NewDir()), Client.Items, Client.GameData, Client.Spells)
    {
        Sync = new object(), Clock = clock, Clusters = Client.Clusters, MapSeed = 3, GameTicks = () => Saturday.Ticks + clock.Now.Ticks,
        GvgSeasons = Europe.Value,
    };

    private static void Data()
    {
        var world = MakeWorld(new ServerConfig(), new ManualClock());
        var all = world.Territories.All.ToList();
        Check(all.Count(s => s.Territory.Type == "WATCHTOWER") == 268 && all.Count(s => s.Territory.CastleType == "MEDIUMCASTLE") == 23
              && all.Count(s => s.Territory.CastleType == "SMALLCASTLE") == 95 && all.Count == 386 && all.All(s => !s.Owned)
              && all.Where(s => !s.Territory.IsCastle).All(s => s.Territory.ResourceType is "Resource" or "Farm")
              && all.Select(s => s.Cluster.Id).Distinct().Count() == 269,
              $"world.xml: 268 watchtower territories, 23 castles, 95 castle outposts in 269 clusters, all unowned (got {all.Count})");
        var rules = world.Territories.Rules;
        Check(rules.Castle("SMALLCASTLE") is { Index: 0, EliteArrivalMinutes: 1 } && rules.Castle("MEDIUMCASTLE") is { Index: 1, PhaseTimeoutMinutes: 30 }
              && rules.ClaimCost == 2_000_000 && rules.NutritionStorage == 38_880 && rules.TowerLevels == 8 && rules.ClaimSeconds == 15
              && rules.FirstClaimFame == 1_000_000 && rules.PointsOf("OPENPVP_BLACK_6", 8) == 160 && rules.PrimeTime == TimeSpan.FromHours(1)
              && rules.TimeRegions["US_22_00"].Start == TimeSpan.FromHours(20) && rules.TimeRegions["US_00_00"].TimeZone == -2,
              "rules: castles.bin SMALLCASTLE 0 / MEDIUMCASTLE 1, claim cost 2000000, 8 tower levels, 15 s claim, 1000000 fame, B6 160 points, US_22_00 at 20:00 for 1 h");
        var dunes = world.Territories.Find("Northstrand Dunes Weald")!;
        Check(dunes.TimeRegion == "US_22_00" && world.Territories.InPrimeTime(dunes, Saturday.Date.AddHours(20.5))
              && !world.Territories.InPrimeTime(dunes, Saturday.Date.AddHours(21.5)) && !world.Territories.InPrimeTime(dunes, Saturday),
              "Northstrand Dunes (US_22_00): prime time 20:00-21:00 UTC");
        var s34 = Europe.Value.ClientSeason(Saturday)!;
        Check(s34.Schedule.Count == 13 && s34.Schedule[1] is { Day: 1, TimeRegions.Count: 6 } && s34.Schedule[1].Actions.Any(a => a.Type == "territoriesclaimable"
              && a.Attrs["claimlimitoverrideminutes"] == "960") && Europe.Value.Current(s34.End.AddDays(1), 28).Schedule == s34.Schedule,
              "SEASON_34 Europe: 13 eventtimes (day 1 18:00 claimable for the six US_* timeregions, 960 min), a local season reuses them");
        var day29 = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        Check(world.Territories.Claimable(dunes, Saturday) && !world.Territories.Claimable(dunes, day29.AddHours(12))
              && world.Territories.Claimable(dunes, day29.AddHours(19)) && !world.Territories.Claimable(dunes, new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc)),
              "claimable windows: day 36 yes, day 29 12:00 no (reset at 11:00), 19:00 yes again, day 1 12:00 no (from 18:00)");
        Check(new ServerConfig().Territories is { Enabled: true, WorldMapInfo: false, CastlesEnabled: true, ClaimCost: null, PrimeTimeOnly: true, SeasonSchedule: true }
              && ServerConfig.PveDefaults.Territories is { Enabled: true, WorldMapInfo: false, ClaimCost: 200_000, PrimeTimeOnly: false, SeasonSchedule: false, NpcOwners: true, CastleGuardScale: 0.6 }
              && MakeWorld(new ServerConfig { Territories = ServerConfig.PveDefaults.Territories }, new ManualClock()).Territories.Claimable(dunes, day29.AddHours(12)),
              "presets: official Enabled, WorldMapInfo false, data values; pve 200000 silver, any time, no season schedule (always claimable)");
    }

    private static void Spawn()
    {
        _ = Client.Clusters!.Get("0305"); // resolved before the join: the map fills at once
        var clock = new ManualClock();
        var world = MakeWorld(new ServerConfig { JoinByCharacterId = true }, clock);
        world.MobAi = false;
        var characters = (AlbionLocal.Server.Persistence.SqliteCharacterRepository)world.Characters;
        var c = StarterKit.Default.NewCharacter("T" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY) = ("0305", 125, -128);
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
        var map = world.Maps.Find("0305")!;
        for (var i = 0; i < 100 && map.Template is null; i++)
        {
            Thread.Sleep(20);
            clock.Advance(TimeSpan.FromSeconds(0.1));
            lock (world.Sync) world.Update();
        }
        clock.Advance(TimeSpan.FromSeconds(0.2));
        lock (world.Sync) world.Update();
        var monolith = map.Objects.Values.OfType<MonolithObject>().SingleOrDefault();
        var castle = map.Objects.Values.OfType<CastleObject>().SingleOrDefault();
        Check(monolith is { State.Id: var mid, State.Owned: false } && mid == Coombe && monolith.Position == new Vector2(125, -135)
              && castle is { State.Id: var cid, Type: { Name: "MEDIUMCASTLE", Index: 1 }, Phase: 0, Chest: CastleObject.ChestEmpty } && cid == Tor
              && castle.Spawn.Tile == "T1_CASTLE" && castle.Position == new Vector2(174, 166),
              $"0305: one monolith (Wetgrave Bog Coombe at 125/-135) and one MEDIUMCASTLE (Wetgrave Bog Tor, T1_CASTLE at 174/166), unowned (got {monolith}; {castle})");
        var ev = peer.Events.LastOrDefault(e => Code(e) == (int)EventCodes.NewMonolithObject)?.Parameters;
        Check(ev is not null && Convert.ToInt64(ev[0]) == monolith!.Id && ev[7] is byte[] t && new Guid(t) == Coombe && ev[1] is float[] { Length: 2 } pos
              && pos[0] == 125 && pos[1] == -135 && !ev.ContainsKey(3) && ev[4] is "" && !ev.ContainsKey(28) && !ev.ContainsKey(21),
              "NewMonolithObject to the player near it: 0 id, 1 (125, -135), 7 the territory Guid, no owner (3 left out, 4 \"\"), no crest, state 0");
        var cp = AlbionSerializer.BuildEvent(castle!.Build()).Parameters;
        Check(cp[1] is "T1_CASTLE" && Convert.ToInt32(cp[2]) == 1 && cp[3] is byte[] g && new Guid(g) == Tor && !cp.ContainsKey(4) && cp[5] is "" && cp[6] is ""
              && Convert.ToInt32(cp[12]) == CastleObject.ChestEmpty && !cp.ContainsKey(10) && cp[13] is "" && !cp.ContainsKey(14),
              "NewCastleObject: 1 the tile, 2 castletype index 1, 3 the territory, no owner, phase 0, chest 8 (empty), 13 \"\" (no rarity), no crest");
        OperationResponse? Send(AlbionRequest req, int id)
        {
            var n = peer.Responses.Count;
            lock (world.Sync) session.OnRequest(Request(req, id), encrypted: false);
            return peer.Responses.Count > n ? peer.Responses[^1] : null;
        }
        var info = Send(new GetMonolithInfoRequest { n8 = Coombe.ToString() }, 20);
        var claim = Send(new GetClaimInfoRequest { mo = Coombe.ToString() }, 21);
        var castleClaim = Send(new GetClaimInfoRequest { mo = Tor.ToString() }, 22);
        var points = Send(new GetTerritorySeasonPointsRequest { o5 = Coombe.ToString() }, 23);
        static bool Only(OperationResponse? r, params byte[] keys) => r is { ReturnCode: 0 } && r.Parameters.Keys.Where(k => k < 250).OrderBy(k => k).SequenceEqual(keys);
        Check(Only(info) && Only(claim, 3) && claim!.Parameters[3] is true && Only(castleClaim) && Only(points, 7) && Convert.ToInt32(points!.Parameters[7]) == -1,
              "117 {} (0/0), 118 {3 true: claimable by the season, no guild: 0 fame, 0 owned}, a castle not claimable {}, 120 {7 -1}: nothing owned");
        world.Dispose();
    }

    private static void WorldMap()
    {
        var info = Client.Clusters!.World;
        var off = MakeWorld(new ServerConfig(), new ManualClock());
        var on = MakeWorld(new ServerConfig { Territories = new TerritoryOptions { WorldMapInfo = true } }, new ManualClock());
        var ids = new[] { "0305", "2305" }.Concat(info.Clusters.Where(c => c.Territories > 2).Select(c => c.Id).Take(2)).ToList();
        foreach (var id in ids)
        {
            var n = info.Find(id)!.Territories;
            Check(WorldSession.ClusterInfo(info, id, off.StaticDungeons, off.Territories) is null && WorldSession.ClusterInfo(info, id) is null,
                  $"{id}: WorldMapInfo off -> no ClusterInfoUpdate (as before)");
            var e = WorldSession.ClusterInfo(info, id, on.StaticDungeons, on.Territories);
            var lengths = e is null ? [] : AlbionLocal.Server.Systems.Territories.WorldMapLengths(e);
            var p = e is null ? null : AlbionSerializer.BuildEvent(e).Parameters;
            Check(e is not null && n > 0 && lengths.Length == 15 && lengths.All(l => l == n) && e.byh!.All(x => x is { Length: 0 })
                  && p is not null && Enumerable.Range(4, 15).All(k => p.ContainsKey((byte)k)) && !p.ContainsKey(62) && e.bx9 == id,
                  $"{id}: WorldMapInfo on -> keys 4-18 sent, every array {n} long (got {string.Join("/", lengths)}), 8 an empty Guid[] each, 62-64 0");
        }
        Check(WorldSession.ClusterInfo(info, "4220", on.StaticDungeons, on.Territories) is { byd: null, bys.Length: 3 },
              "a cluster without territories: no territory keys either way");
    }
}
