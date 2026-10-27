using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The world objects of step 12 (analysis/work/stage2/SPEC.md §3, §5-§7) on the client's own cluster data: the Thetford
/// Join the spec worked out, every Join the captures hold (Fixtures/stage2_joins.json and stage2_joins_002835.json, made
/// by analysis/tools/stage2_fixture.py), and the wiring (SelectCharacter, AnnounceAll, .near). Runs after
/// <see cref="ClusterDataTest"/>, whose clusters the shared store has resolved by then.
/// </summary>
public static class WorldObjectTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("world objects: Thetford Join at (0, 0) as SPEC §7 (phases A/B/C, window edges, NewBuilding keys on the wire), walking out and back (1-cell hysteresis)", Thetford),
        ("world objects: every captured Join gets the captured phases A/B/C (Fixtures/stage2_joins*.json, 20 Joins), only the listed exceptions differ", CapturedJoins),
        ("world objects: SelectCharacter resolves the cluster, objects before the items (UDP); World:AnnounceAll, Visibility keys; .near", Wiring),
        ("world objects: hidden simple behaviour stays unannounced on arrival, visibility reentry and reconnect", SimpleBehaviourState),
    ];

    private static ClusterStore Store => Client.Clusters!;

    /// <summary>The event codes of the world objects (and Leave).</summary>
    internal static readonly HashSet<int> WorldCodes =
    [
        (int)EventCodes.NewBuilding, (int)EventCodes.NewExit, (int)EventCodes.NewTravelpoint, (int)EventCodes.NewFactionWarfarePortal,
        (int)EventCodes.NewIslandAccessPoint, (int)EventCodes.NewExpeditionAgent, (int)EventCodes.NewLocationMarker,
        (int)EventCodes.QuestGiverInfoForPlayer, (int)EventCodes.NewSimpleHarvestableObjectList, (int)EventCodes.Leave,
        (int)EventCodes.NewMob, (int)EventCodes.MobChangeState, (int)EventCodes.NewFishingZoneObject, (int)EventCodes.NewShrine,
    ];

    internal static int Code(EventData e) => Convert.ToInt32(e.Parameters[ReservedKeys.EventCode]);

    private static Vector2 Pos(EventData e, byte key) => e.Parameters.GetValueOrDefault(key) is float[] f ? new Vector2(f[0], f[1]) : Vector2.Zero;

    private static Guid GuidOf(EventData e, byte key) => e.Parameters.GetValueOrDefault(key) is byte[] { Length: 16 } b ? new Guid(b) : Guid.Empty;

    /// <summary>A world with the client's cluster store and an empty item table (no item events), on a manual clock.</summary>
    private static (World World, SqliteCharacterRepository Characters, string Dir) NewWorld(ServerConfig? config = null)
    {
        var dir = NewDir();
        var characters = Repository(dir);
        var world = new World((config ?? new ServerConfig()) with { JoinByCharacterId = true }, characters, Items()) { Sync = new object(), Clock = new ManualClock(), Clusters = Store };
        return (world, characters, dir);
    }

    /// <summary>A new character in <paramref name="cluster"/> at <paramref name="at"/>, without items, joined through a <see cref="FakePeer"/>.</summary>
    private static (WorldSession Session, FakePeer Peer) JoinAt(World world, SqliteCharacterRepository characters, string cluster, Vector2 at)
    {
        var c = StarterKit.Default.NewCharacter("C" + Guid.NewGuid().ToString("N")[..10]);
        c.Cluster = cluster;
        c.PositionX = at.X;
        c.PositionY = at.Y;
        c.Equipment = [];
        c.Inventory = [];
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        session.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
        Check(peer.Responses is [{ ReturnCode: 0 }], $"join into {cluster}");
        return (session, peer);
    }

    /// <summary>The world-object events of a Join by phase: A before the Join response, B up to JoinFinished, C after it.</summary>
    private static (List<EventData> A, List<EventData> B, List<EventData> C) Phases(FakePeer peer)
    {
        var seq = peer.Sequence;
        var response = seq.FindIndex(x => x is OperationResponse);
        var finished = seq.FindIndex(x => x is EventData e && Code(e) == (int)EventCodes.JoinFinished);
        Check(response >= 0 && finished > response, "Join response, then JoinFinished");
        List<EventData> Of(int from, int to) => [.. seq.Skip(from).Take(to - from).OfType<EventData>().Where(e => WorldCodes.Contains(Code(e)))];
        return (Of(0, response), Of(response + 1, finished), Of(finished + 1, seq.Count));
    }

    /// <summary>
    /// SPEC §7 "Expected WorldTest result": a Join at the start position (0, 0) of Thetford, player cell (30, 30), window
    /// x, z in [-45, 45). Then a walk to x = 90 (cell 39, by the world tick while walking), back, and one cell on to the
    /// west, across the edge of the default hysteresis band (enter at 4 cells, leave at 6).
    /// </summary>
    private static void Thetford()
    {
        var (world, characters, dir) = NewWorld();
        _ = Store.Get("0000");
        var (session, peer) = JoinAt(world, characters, "0000", Vector2.Zero);
        var (a, b, c) = Phases(peer);
        var player = session.Player!;
        var map = player.Map!;
        Check(map.Template?.Id == "0000" && map.Objects.Count == 5 + 9 + 1 + 1 + 1 + 1 + 20 + 271 + 66, $"0000 spawned its 309 objects and 66 fishing zones (got {map.Objects.Count})");
        Check(peer.Sequence.FindIndex(x => x is OperationResponse) == a.Count, "phase A is everything before the Join response (no items here)");

        string Of(EventData e) => (EventCodes)Code(e) switch
        {
            EventCodes.NewBuilding => $"building {e.Parameters[2]} {e.Parameters[3]}",
            EventCodes.NewExit => $"exit {GuidOf(e, 1).ToString()[..8]}",
            EventCodes.NewTravelpoint => $"travelpoint {e.Parameters[3]}",
            EventCodes.NewExpeditionAgent => $"agent {e.Parameters[3]}",
            EventCodes.NewLocationMarker => $"marker {e.Parameters[2]}",
            EventCodes.NewFishingZoneObject => $"zone {e.Parameters[4]}",
            var other => other.ToString(),
        };
        var got = a.Select(Of).Order(StringComparer.Ordinal).ToArray();
        string[] want =
        [
            "agent 4", "building 27 SWAMP_GREEN_T8_MELDBUILDING", "building 495 SWAMP_GREEN_REPAIRSHOP", "exit 399399b1", "exit 982df85e",
            "marker ARTIFACTFOUNDRY", "marker BANK", "marker BANK", "marker BANK", "marker EXPEDITION", "marker EXPLORER", "marker FACTION_WARFARE",
            "marker ISLAND", "marker MARKETPLACE", "marker REPAIR", "marker TRAVEL", "travelpoint FASTTRAVEL_POINT",
            "zone ", "zone ", // stageG: two FRESHWATER_BASE fishing zones (no prefab)
        ];
        Check(got.Order(StringComparer.Ordinal).SequenceEqual(want.Order(StringComparer.Ordinal)), $"phase A: repair and meld, bank and market doors, travel point, expedition agent, 11 markers, 2 fishing zones (got {string.Join(", ", got)})");
        Check(a.Where(e => Code(e) == (int)EventCodes.NewLocationMarker).Any(e => Pos(e, 1) == new Vector2(-43.5f, -21.5f))
              && a.All(e => Pos(e, 1) != new Vector2(-45.5f, 15.5f) && Pos(e, 4) != new Vector2(-50, 12) && Pos(e, 1) != new Vector2(50, 0)),
              "edges: ISLAND marker (-43.5, -21.5) in; VANITY marker (-45.5, 15.5), vanity store (-50, 12), wagon (50, 0) out (cell distance 5)");

        Check(b.Select(Code).ToArray() is [(int)EventCodes.NewBuilding, (int)EventCodes.QuestGiverInfoForPlayer], "phase B: the faction trader and its QuestGiverInfoForPlayer");
        var trader = b[0].Parameters;
        var info = b[1].Parameters;
        Check(Convert.ToInt32(trader[2]) == 21 && Pos(b[0], 4) == new Vector2(34, 2) && GuidOf(b[0], 1) == Guid.Parse("5f932a92-8895-43ef-bc72-01b12bc00b2d")
              && Convert.ToInt64(info[0]) == Convert.ToInt64(trader[0]) && GuidOf(b[1], 1) == GuidOf(b[0], 1) && Convert.ToInt32(info[2]) == 20
              && Convert.ToInt64(info[4]) == Convert.ToInt64(trader[20]) && !info.ContainsKey(3) && !info.ContainsKey(5) && !info.ContainsKey(6),
              "trader 21 at (34, 2); 259: its id and Guid, FACTIONSTORE_SWAMP = 20, stamp = its key 20, no keys 3/5/6");

        // AccessStatus (002835): none before JoinFinished; right after it one batch for the 3 buildings sent (repair, meld,
        // trader), preset 2, @_Owner/@_Guild/@_Everyone, owner/friend/user (the trader owner/noaccess/noaccess), three "".
        var seq = peer.Sequence;
        var finishedAt = seq.FindIndex(x => x is EventData e && Code(e) == (int)EventCodes.JoinFinished);
        var batch = seq.Skip(finishedAt + 1).OfType<EventData>().TakeWhile(e => Code(e) == (int)EventCodes.AccessStatus).ToList();
        var buildings = a.Concat(b).Where(e => Code(e) == (int)EventCodes.NewBuilding).Select(e => Convert.ToInt64(e.Parameters[0])).ToList();
        Check(seq.Take(finishedAt).All(x => x is not EventData e || Code(e) != (int)EventCodes.AccessStatus)
              && batch.Select(e => Convert.ToInt64(e.Parameters[0])).SequenceEqual(buildings) && buildings.Count == 3
              && batch.All(e => Convert.ToInt32(e.Parameters[1]) == 2 && !e.Parameters.ContainsKey(2) && !e.Parameters.ContainsKey(3)
                  && e.Parameters[4] as string[] is ["@_Owner", "@_Guild", "@_Everyone"] && e.Parameters[6] as string[] is ["", "", ""])
              && batch.Select(e => ((string[])e.Parameters[5]!)[2]).SequenceEqual(["user", "user", "noaccess"]),
              $"AccessStatus: none before JoinFinished, then one batch for repair, meld and trader (got {batch.Count})");
        Check(c is [var list] && Code(list) == (int)EventCodes.NewSimpleHarvestableObjectList, $"phase C: one harvestable list (got {c.Count} events)");
        var flat = (float[])c[0].Parameters[3]!;
        var trees = Enumerable.Range(0, flat.Length / 2).Select(i => new Vector2(flat[2 * i], flat[2 * i + 1])).ToList();
        Check(trees.Count == 16 && trees.Contains(new Vector2(44.5f, 3.5f)) && !trees.Contains(new Vector2(-25.5f, -46.5f))
              && c[0].Parameters[1] is byte[] { Length: 16 } types && types.All(t => t == 0) && c[0].Parameters[2] is byte[] tiers && tiers.All(t => t == 1)
              && c[0].Parameters[4] is short[] charges && charges.All(n => n == 2) && c[0].Parameters[0] is long[] { Length: 16 },
              $"16 trees, WOOD (0) T1 with 2 charges; (44.5, 3.5) in, (-25.5, -46.5) out (got {trees.Count})");

        // The repair shop on the wire, as the captured Lymhurst one: exactly these keys, key 29 a byte 0.
        var repair = a.First(e => Code(e) == (int)EventCodes.NewBuilding && Convert.ToInt32(e.Parameters[2]) == 495);
        var w = new ByteWriter();
        Protocol18.WriteEventData(w, repair);
        var wire = Protocol18.ReadEventData(new ByteReader(w.ToArray())).Parameters;
        Check(wire.Keys.Order().SequenceEqual(new byte[] { 0, 1, 2, 3, 4, 6, 7, 8, 18, 19, 20, 21, 22, 26, 27, 28, 29, 252 }) && wire[29] is (byte)0
              && wire[6] is true && wire[26] is true && wire[27] is true && wire[28] is true && Convert.ToInt32(wire[19]) == -1
              && Convert.ToInt64(wire[7]) == 17_496_000_000 && Convert.ToInt64(wire[18]) == 7_950_000_000 && Convert.ToInt64(wire[21]) == 7_949_930_000
              && Convert.ToInt64(wire[22]) == Convert.ToInt64(wire[8]) && Convert.ToInt64(wire[20]) == map.CreatedTicks && Convert.ToInt64(wire[8]) == map.CreatedTicks,
              $"repair shop keys 0-4, 6-8, 18-22, 26-29 (no 5 at 0°), durability/nutrition/craft capacity x 10000, 22 = 8 (got {string.Join(",", wire.Keys.Order())})");
        var meld = a.First(e => Code(e) == (int)EventCodes.NewBuilding && Convert.ToInt32(e.Parameters[2]) == 27).Parameters;
        Check(meld[5] is 90f && Convert.ToInt64(meld[7]) == 3_000_000 && Convert.ToInt64(meld[18]) == 300_000 && Convert.ToInt64(meld[21]) == 1_544_340_000,
              "meld building: key 5 = 90, 3000000 / 300000 / 1544340000 as captured");

        // Out: Move to x = 90 at a high speed; the Move itself (still at (0, 0)) changes nothing, the tick after the walk does.
        var before = peer.Events.Count;
        session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [0, 0], v8 = [90, 0], v9 = 2000f }, null), encrypted: false);
        Check(peer.Events.Count == before, "a Move in the same cell sends nothing");
        Thread.Sleep(100);
        lock (world.Sync) world.Update();
        var walk = peer.Events.Skip(before).ToList();
        var left = walk.Where(e => Code(e) == (int)EventCodes.Leave).Select(e => Convert.ToInt64(e.Parameters[0])).ToHashSet();
        var leaveKinds = left.Select(id => map.Objects[id].Kind).Distinct().Order().ToArray();
        Check(leaveKinds.SequenceEqual(["building", "location marker", "travel point"]) && left.Count == 2 + 1 + 11 && !left.Contains(player.Id),
              $"at (90, 0): Leave for the 2 buildings, the travel point and the 11 markers only (got {left.Count}: {string.Join(", ", leaveKinds)})");
        Check(walk.Count(e => Code(e) == (int)EventCodes.NewFactionWarfarePortal) == 1 && walk.First(e => Code(e) == (int)EventCodes.NewFactionWarfarePortal) is var wagon
              && Pos(wagon, 1) == new Vector2(50, 0) && Convert.ToInt32(wagon.Parameters[4]) == 5 && (string)wagon.Parameters[3]! == "SWAMP_GREEN_CITY_FWARFARE_PORTAL_WAGON",
              "the war wagon (50, 0) comes into view: FlaggingStatus 5 (Swamp), its tile name");
        Check(player.Visibility.Known.All(o => o.Kind is not ("exit" or "expedition agent" or "harvestable") || o.Position.X >= 35)
              && player.Visibility.Known.Any(o => o.Kind == "harvestable" && o.Position == new Vector2(38.5f, 12.5f))
              && player.Visibility.Known.Any(o => o is AlbionLocal.Server.Entities.Building { IsQuestGiver: true }),
              "exits, the expedition agent and the trees are forgotten without a Leave, but not the trees at cell distance 5 (x >= 35, e.g. (38.5, 12.5)); the faction trader stays");
        // Back to the start (a Move that stops there): the forgotten ones are sent again with the same object ids. What
        // came into view at x = 90 and gets a Leave is only the wagon (cell 35), at cell distance 5: in the band, so it stays.
        var entered = walk.Where(e => Code(e) is not ((int)EventCodes.Leave or (int)EventCodes.NewSimpleHarvestableObjectList))
            .Select(e => map.Objects[Convert.ToInt64(e.Parameters[0])]).ToList();
        var wagonId = walk.Where(e => Code(e) == (int)EventCodes.NewFactionWarfarePortal).Select(e => Convert.ToInt64(e.Parameters[0])).First();
        before = peer.Events.Count;
        session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [0, 0], v8 = [0, 0], v9 = 0f }, null), encrypted: false);
        var back = peer.Events.Skip(before).ToList();
        var exits = back.Where(e => Code(e) == (int)EventCodes.NewExit).Select(e => Convert.ToInt64(e.Parameters[0])).Order().ToArray();
        var backLeft = back.Where(e => Code(e) == (int)EventCodes.Leave).Select(e => Convert.ToInt64(e.Parameters[0])).ToHashSet();
        Check(exits.SequenceEqual(a.Where(e => Code(e) == (int)EventCodes.NewExit).Select(e => Convert.ToInt64(e.Parameters[0])).Order())
              && back.Count(e => Code(e) == (int)EventCodes.NewSimpleHarvestableObjectList) == 1 && back.Count(e => Code(e) == (int)EventCodes.NewBuilding) == 2
              && entered.Where(o => o.LeaveOnRange && o is not AlbionLocal.Server.Entities.FishingZone).Select(o => o.Id).SequenceEqual([wagonId])
              && backLeft.SetEquals(entered.OfType<AlbionLocal.Server.Entities.FishingZone>().Where(z => CellGrid.Distance(z.Cell, map.Grid!.CellOf(Vector2.Zero)) >= 6).Select(z => z.Id))
              && player.Visibility.Known.Any(o => o.Id == wagonId),
              $"back at (0, 0): both exits again (same ids), the trees in one list, repair and meld again; no Leave for the wagon (cell distance 5), only for the fishing zones east of the band (got {backLeft.Count} Leaves)");
        Check(back.Select((e, i) => (e, i)).Where(x => Code(x.e) == (int)EventCodes.NewBuilding)
                  .All(x => back.ElementAtOrDefault(x.i + 1) is { } next && Code(next) == (int)EventCodes.AccessStatus
                      && Convert.ToInt64(next.Parameters[0]) == Convert.ToInt64(x.e.Parameters[0]))
              && back.Count(e => Code(e) == (int)EventCodes.AccessStatus) == 2,
              "after the Join each building's AccessStatus follows its NewBuilding");
        before = peer.Events.Count;
        session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [-10, 0], v8 = [-10, 0], v9 = 0f }, null), encrypted: false);
        var west = peer.Events.Skip(before).ToList();
        Check(west.Where(e => Code(e) == (int)EventCodes.Leave).Select(e => Convert.ToInt64(e.Parameters[0])).SequenceEqual([wagonId])
              && west.Any(e => Code(e) == (int)EventCodes.NewBuilding && Convert.ToInt32(e.Parameters[2]) == 371),
              "at (-10, 0), cell 29: Leave for the wagon only (cell distance 6); the vanity store (distance 4) comes");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>One world object of a phase, comparable between the fixture and our events.</summary>
    private sealed record Obj(int Event, string Key, Vector2 Pos, float Extra)
    {
        public bool Matches(Obj o) => Event == o.Event && Key == o.Key && At(o.Pos) && Math.Abs(Extra - o.Extra) <= 0.01f;

        /// <summary>
        /// Same place: for the kinds the client binds to a landscape tile (214, 215, 314, 662, list 39) as the client binds
        /// (<see cref="MapSpawner.Binds"/>, per axis &lt; 0.001), for those that place their own tile (45, 213, 383) within 0.003.
        /// </summary>
        public bool At(Vector2 pos) => Event is 214 or 215 or 314 or 662 or 39 ? MapSpawner.Binds(Pos, pos) : Vector2.Distance(Pos, pos) <= 0.003f;

        public override string ToString() => $"{Event} {Key} ({Pos.X}, {Pos.Y}){(Extra != 0 ? $" {Extra}" : "")}";
    }

    /// <summary>The capture decoder's rule for strings it keeps (analysis/work/stage2/captures/pcapdec.py SAFE_STRING); others become "&lt;strN&gt;".</summary>
    private static readonly Regex Safe = new(@"^$|^(?:[0-9a-f]{2})+$|^[0-9a-fA-F\-]{8,}$|^-?\d+$|^(?=[A-Z0-9_@.:\-]*[_@])[A-Z0-9_@.:\-]+$|^@");

    private static string Masked(string s) => Safe.IsMatch(s) ? s : $"<str{s.Length}>";

    /// <summary>Our world-object events as <see cref="Obj"/> (list entries one by one; QuestGiverInfoForPlayer by its building's Guid).</summary>
    private static List<Obj> Ours(IEnumerable<EventData> events, Dictionary<long, Guid> buildings)
    {
        var list = new List<Obj>();
        foreach (var e in events)
        {
            var p = e.Parameters;
            string S(byte k) => p.GetValueOrDefault(k) as string ?? "";
            int I(byte k) => p.GetValueOrDefault(k) is { } v ? Convert.ToInt32(v) : 0;
            float F(byte k) => p.GetValueOrDefault(k) is float f ? f : 0;
            switch ((EventCodes)Code(e))
            {
                case EventCodes.NewBuilding:
                    buildings[Convert.ToInt64(p[0])] = GuidOf(e, 1);
                    list.Add(new(45, $"{I(2)}|{GuidOf(e, 1)}|{S(3)}", Pos(e, 4), F(5)));
                    break;
                case EventCodes.NewExit: list.Add(new(215, $"{GuidOf(e, 1)}", Pos(e, 2), 0)); break;
                case EventCodes.NewTravelpoint: list.Add(new(213, $"{GuidOf(e, 2)}|{S(3)}", Pos(e, 1), 0)); break;
                case EventCodes.NewFactionWarfarePortal: list.Add(new(662, $"{GuidOf(e, 2)}|{S(3)}|{I(4)}", Pos(e, 1), 0)); break;
                case EventCodes.NewIslandAccessPoint: list.Add(new(214, $"{GuidOf(e, 3)}", Pos(e, 1), F(2))); break;
                case EventCodes.NewExpeditionAgent: list.Add(new(314, $"{GuidOf(e, 2)}|{I(3)}", Pos(e, 1), 0)); break;
                case EventCodes.NewLocationMarker: list.Add(new(383, Masked(S(2)), Pos(e, 1), F(3))); break;
                // StageG: the fishing zones (the fixture keeps their position only; capacity and prefab: FishingTest).
                case EventCodes.NewFishingZoneObject: list.Add(new(360, "", Pos(e, 1), 0)); break;
                case EventCodes.QuestGiverInfoForPlayer:
                    list.Add(new(259, $"{buildings.GetValueOrDefault(Convert.ToInt64(p[0]))}|{GuidOf(e, 1)}|{I(2)}", Vector2.Zero, 0));
                    break;
                case EventCodes.NewSimpleHarvestableObjectList:
                    var flat = (float[])p[3]!;
                    var (types, tiers, charges) = ((byte[])p[1]!, (byte[])p[2]!, (short[])p[4]!);
                    for (var i = 0; i < flat.Length / 2; i++)
                        list.Add(new(39, $"{types[i]}|{tiers[i]}|{charges[i]}", new Vector2(flat[2 * i], flat[2 * i + 1]), 0));
                    break;
                // Stage 5a: our mobs are the server's own spawns and picks (MobSpawnTest checks them); the captured ones fall
                // under the NewMob rule of CapturedJoins.
                case EventCodes.NewMob or EventCodes.MobChangeState: break;
                default: throw new Exception($"unexpected world event {(EventCodes)Code(e)} at Join");
            }
        }
        return list;
    }

    /// <summary>A fixture record as <see cref="Obj"/>.</summary>
    private static Obj Captured(JsonElement o)
    {
        string S(string k) => o.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ValueKind == JsonValueKind.Null ? "" : v.GetRawText() : "";
        float F(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : 0;
        Vector2 P(string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? new Vector2(v[0].GetSingle(), v[1].GetSingle()) : Vector2.Zero;
        var ev = o.GetProperty("event").GetInt32();
        return ev switch
        {
            45 when o.TryGetProperty("owned", out _) => new(45, "owned", P("pos"), 0), // a player's, only its position kept
            45 => new(45, $"{S("type")}|{S("guid")}|{S("tile")}", P("pos"), F("rot")),
            215 => new(215, S("guid"), P("pos"), 0),
            213 => new(213, $"{S("guid")}|{S("tile")}", P("pos"), 0),
            662 => new(662, $"{S("guid")}|{S("tile")}|{S("faction")}", P("pos"), 0),
            214 => new(214, S("guid"), P("pos"), F("rot")),
            314 => new(314, $"{S("guid")}|{S("type")}", P("pos"), 0),
            383 => new(383, S("location"), P("pos"), F("radius")),
            259 => new(259, $"{S("building")}|{S("guid")}|{S("type")}", Vector2.Zero, 0),
            39 or 40 => new(ev, $"{S("type")}|{S("tier")}|{S("charges")}", P("pos"), 0),
            366 => new(366, "", P("outpost"), 0),
            _ => new(ev, S("guid"), P("pos"), 0),
        };
    }

    /// <summary>
    /// Fixtures/stage2_joins.json and stage2_joins_002835.json: for every captured Join into a world.xml cluster (1000 x6,
    /// 1001, 1002 x3, 1207 x2; then 1207, 1000, Thetford 0000 x3, its bank 0006 and market 0007, 0204), a character joins
    /// at the captured position and its phases A, B and C must hold exactly the captured world objects (type, Guid, tile,
    /// position, rotation, radius, charges and tier; positions as <see cref="Obj.At"/>; the resource nodes and dynamic
    /// templates of the capture's day, stage 4b), and nothing else, except what the
    /// official server sends and this server does not yet, each kind with the rule that recognises it below. Every
    /// exception must occur somewhere, so the list cannot go stale. (The hall-of-fame banners 519 and the fishing zone
    /// that 002835 first sent at cell distance 5 occur only during the stay, never in a Join's phases.)
    /// </summary>
    private static void CapturedJoins()
    {
        // stage2_joins.json: 103829, 113433, 114814; stage2_joins_002835.json: capture 002835 (the same tool
        // on analysis/work/cap002835: 1207, 1000, Thetford 0000 three times, its bank 0006 and market 0007, 0204).
        // Capture-derived fixtures are not in the public repository: the comparison is skipped without them.
        var files = new[] { "stage2_joins.json", "stage2_joins_002835.json" }.Select(f => Path.Combine(AppContext.BaseDirectory, "Fixtures", f)).ToList();
        if (!files.All(File.Exists)) { Console.WriteLine("      (capture fixtures stage2_joins*.json absent: comparison skipped)"); return; }
        var docs = files.Select(f => JsonDocument.Parse(File.ReadAllText(f))).ToList();
        // Two servers, one per capture day (stage 4b): 103829 and 114814 logged in with a key 57 of 2026-10-14, 002835 of
        // 2026-10-15; the dynamic templates and resource nodes are that day's. No enchanted nodes (they are the server's
        // own random roll; the captured ones are event 40 and fall under the rule below).
        var config = new ServerConfig { RareResources = false, JoinByCharacterId = true };
        var (world, characters, dir) = NewWorld(config);
        var day15 = ResourceNodeTest.Day(Capture002835Key57);
        var world15 = new World(config, characters, Items()) { Sync = new object(), Clock = new ManualClock(), Clusters = day15 };
        // What the captures have and we leave out, by rule (SPEC §7 "Skipped for now", §9; ARCHITECTURE.md §8 steps 13-15).
        // Replaces = the captured object stands for one of ours at the same position, which differs in state only.
        var exceptions = new (string Why, Func<ClusterTemplate, Obj, bool> Is, bool Replaces)[]
        {
            ("NewRealEstate 191: plots are not sent yet (SPEC §7)", (_, o) => o.Event == 191, false),
            ("NewMob 123: the official server's mobs, its own picks, walks and kills (1207: outpost guards, guard towers, rabbits); ours: MobSpawnTest", (_, o) => o.Event == 123, false),
            // Event 40 carries two kinds (002835, 1207 Join): resources the server spawned at a resource node, sent one at a
            // time (key 6 = -1, no keys 1/3: ROCK T4 at two ROCK_HIGH_NODEs, WOOD T4 at a WOOD_HIGH_NODE ± 0.5), and mob
            // corpses (key 6 the mob type, 1 the dead mob, 3 the owner: HIDE and WOOD_CRITTER, which the fixture keeps
            // only as type, tier, position and charges). The node rule wants the node's own resourcetype, so the
            // WOOD_CRITTER corpse 0.84 m from a HIDE_MEDIUM_NODE stays a corpse.
            ("NewHarvestableObject 40 at a resource node of its resource: a server-spawned resource sent singly (002835 1207: ROCK T4 x2, WOOD T4; stage 4)",
                (t, o) => o.Event == 40 && o.Key.Split('|') is [var type, _, _] && Store.Harvestables[int.Parse(type)]?.Resource is { } resource
                && t.ResourceNodes.Any(n => string.Equals(n.ResourceType, resource, StringComparison.OrdinalIgnoreCase) && Vector2.Distance(n.Position, o.Pos) <= 1f), false),
            ("NewHarvestableObject 40 elsewhere: the skinnable corpse of a killed mob (002835 1207: HIDE, WOOD_CRITTER; stage 5)", (_, o) => o.Event == 40, false),
            ("NewOutpostObject 365 / OutpostUpdate 366: open-world outposts (stage 4)", (_, o) => o.Event is 365 or 366, false),
            ("NewBuilding with an owner on a real-estate plot: crafting stations and furniture, server content (stage 6)", (t, o) => o.Event == 45
                && o.Key == "owned" && t.Buildings.All(b => !b.Position.Equals(o.Pos))
                && t.RealEstates.Any(r => r.Size is { } s && Math.Abs(o.Pos.X - r.Position.X) <= s.X / 2 + 0.5f && Math.Abs(o.Pos.Y - r.Position.Y) <= s.Y / 2 + 0.5f), false),
            ("NewBuilding with an owner off the plots: a player's object in the city (002835 Thetford, south of the bank: (11, -59), (31.5, -57.5)), kind not kept by the fixture (stage 6)",
                (t, o) => o.Event == 45 && o.Key == "owned" && t.Buildings.All(b => !b.Position.Equals(o.Pos)), false),
            ("list 39 entry with fewer than its full charges: a static tree or resource node harvested by someone at capture time (stage 4)", (t, o) => o.Event == 39
                && o.Key.Split('|') is [var type, var tier, var charges]
                && t.Harvestables.Concat(t.Resources?.Harvestables ?? []).Any(h => MapSpawner.Binds(h.Position, o.Pos) && $"{h.Type.Id}" == type && $"{h.Tier.Tier}" == tier
                    && int.Parse(charges) < h.Charges), true),
        };
        var used = new int[exceptions.Length];
        var failures = new List<string>();
        var joins = 0;
        var enchanted = 0;
        static string TypeTier(string key) => key[..key.LastIndexOf('|')];
        foreach (var join in docs.SelectMany(d => d.RootElement.GetProperty("joins").EnumerateArray()))
        {
            var cluster = join.GetProperty("cluster").GetString()!;
            var at = new Vector2(join.GetProperty("position")[0].GetSingle(), join.GetProperty("position")[1].GetSingle());
            var name = $"{join.GetProperty("capture").GetString()}#{join.GetProperty("connection").GetInt32()} {cluster} ({at.X}, {at.Y})";
            var on15 = join.GetProperty("capture").GetString() == "002835";
            var template = (on15 ? day15 : Store).Get(cluster);
            var (_, peer) = JoinAt(on15 ? world15 : world, characters, cluster, at);
            var (a, b, c) = Phases(peer);
            var buildings = new Dictionary<long, Guid>();
            // An enchanted resource node of the capture (event 40) is a plain one in our lists: the rare roll is off here. Its
            // charges may differ (the WOOD T4 one had been emptied by someone); type, tier and position may not.
            var captured40 = new[] { "A", "B", "C" }.SelectMany(ph => join.GetProperty(ph).EnumerateArray().Select(Captured)).Where(o => o.Event == 40).ToList();
            foreach (var (phase, events) in new[] { ("A", a), ("B", b), ("C", c) })
            {
                var ours = Ours(events, buildings);
                foreach (var o in join.GetProperty(phase).EnumerateArray().Select(Captured))
                {
                    var hit = ours.FindIndex(x => x.Matches(o));
                    if (hit >= 0)
                    {
                        ours.RemoveAt(hit);
                        continue;
                    }
                    var rule = Array.FindIndex(exceptions, x => x.Is(template, o));
                    if (rule < 0)
                    {
                        failures.Add($"{name} phase {phase}: captured, not sent: {o}");
                        continue;
                    }
                    used[rule]++;
                    if (exceptions[rule].Replaces && ours.FindIndex(x => x.Event == o.Event && x.At(o.Pos)) is >= 0 and var mine)
                        ours.RemoveAt(mine);
                }
                enchanted += ours.RemoveAll(o => o.Event == 39 && captured40.Any(x => TypeTier(x.Key) == TypeTier(o.Key) && template.Resources?.Harvestables.Any(h => MapSpawner.Binds(h.Position, x.Pos)) == true
                    && MapSpawner.Binds(x.Pos, o.Pos)));
                failures.AddRange(ours.Select(o => $"{name} phase {phase}: sent, not captured: {o}"));
            }
            joins++;
        }
        Check(joins == 12 + 8, $"12 + 8 captured Joins into world.xml clusters (got {joins})");
        Check(enchanted == 3, $"the 3 enchanted resource nodes of 002835 1207 (event 40 there) are ours in a list (got {enchanted})");
        Check(failures.Count == 0, $"{failures.Count} differences, first: {string.Join(" | ", failures.Take(12))}");
        Check(used.All(n => n > 0), $"every exception occurs ({string.Join("; ", exceptions.Select((x, i) => $"{x.Why}: {used[i]}"))})");
        int Uses(string why) => used[Array.FindIndex(exceptions, x => x.Why.StartsWith(why, StringComparison.Ordinal))];
        Check(Uses("NewHarvestableObject 40 at") == 3 && Uses("NewHarvestableObject 40 elsewhere") == 12,
              $"event 40 in 1207: 3 resource-node resources (key 6 = -1 in the capture), 12 mob corpses (got {Uses("NewHarvestableObject 40 at")}, {Uses("NewHarvestableObject 40 elsewhere")})");
        foreach (var d in docs) d.Dispose();
        world.Dispose();
        world15.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    /// <summary>
    /// The server as Program builds it, over UDP: SelectCharacter on the login server has the character's cluster resolved
    /// (Bank of Bridgewatch, 2003, which no other test loads), so the Join finds it and sends the building and exits before
    /// the items. Then, on FakePeer worlds: World:AnnounceAll sends every object of Thetford at Join and never a Leave; the
    /// Visibility keys parse and are checked; .near lists the window.
    /// </summary>
    private static void Wiring()
    {
        var dir = NewDir();
        using (var host = Host(dir, spells: false, clusters: true))
        {
            var hero = host.Characters.List()[0];
            hero.Cluster = "2003";
            host.Characters.Save(hero);
            Check(!Store.IsLoaded("2003"), "2003 is not resolved before the test");
            using var login = new TestPhotonClient(host.LoginServer.EndPoint);
            login.Connect();
            Check(login.EncryptionDone.Task.Wait(10000), "login encryption");
            Check(Call(login, TestLogin(), 1).ReturnCode == 0, "login");
            Check(Call(login, new SelectCharacterRequest { abc = hero.Name }, null).ReturnCode == 0, "select");
            Check(SpinWait.SpinUntil(() => Store.IsLoaded("2003"), 30000), "SelectCharacter has 2003 resolved in the background");
            using var game = new TestPhotonClient(host.GameServer.EndPoint);
            game.Connect();
            Check(game.EncryptionDone.Task.Wait(10000), "game encryption");
            Check(Call(game, new JoinRequest { ss = hero.Id }, 2).ReturnCode == 0, "join");
            var codes = new List<int>();
            while (game.Events.TryTake(out var e, 5000))
            {
                codes.Add(Code(e));
                if (codes[^1] == (int)EventCodes.JoinFinished) break;
            }
            var building = codes.IndexOf((int)EventCodes.NewBuilding);
            var item = codes.IndexOf((int)EventCodes.NewEquipmentItem);
            Check(building >= 0 && codes.Contains((int)EventCodes.NewExit) && item > building && codes.LastIndexOf((int)EventCodes.NewExit) < item,
                  $"the bank building and its exits before the first item (got {string.Join(",", codes)})");
        }
        try { Directory.Delete(dir, true); } catch (IOException) { }

        var (world, characters, dir2) = NewWorld(new ServerConfig { AnnounceAll = true });
        var (session, peer) = JoinAt(world, characters, "0000", Vector2.Zero);
        var (a, b, c) = Phases(peer);
        Check(a.Count == 4 + 9 + 1 + 1 + 1 + 1 + 20 + 66 && b.Count == 2 && c.Count == 7 && c.Sum(e => ((long[])e.Parameters[0]!).Length) == 271
              && c.All(e => ((long[])e.Parameters[0]!).Length <= 40),
              $"AnnounceAll: phase A every object but the trader (66 fishing zones with them), phase B the trader, phase C 271 trees in 7 lists of at most 40 (got {a.Count}, {b.Count}, {c.Count})");
        var count = peer.Events.Count;
        session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [200, 200], v8 = [200, 200], v9 = 0f }, null), encrypted: false);
        Check(peer.Events.Count == count, "AnnounceAll: walking away sends no Leave and nothing new");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir2, true); } catch (IOException) { }

        var none = Path.Combine(dir2, "none.json");
        Check(LoadTestConfig(none, ["--World:AnnounceAll=true", "--Visibility:EnterCells=2", "--Visibility:LeaveCells=4", "--Visibility:CellSize=20"]) is
              { AnnounceAll: true, Visibility: { EnterCells: 2, LeaveCells: 4, CellSize: 20 } }
              && new ServerConfig() is { AnnounceAll: false, Visibility: { CellSize: 10, EnterCells: 4, LeaveCells: 6 } }
              && LoadTestConfig(none, []).Visibility == new VisibilityOptions(),
              "World:AnnounceAll and Visibility:CellSize/EnterCells/LeaveCells; defaults off, 10, 4, 6 (Load as the record)");
        try { LoadTestConfig(none, ["--Visibility:EnterCells=6"]); Check(false, "LeaveCells not above EnterCells was accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("LeaveCells"), $"the error names LeaveCells (got {e.Message})"); }

        var (world3, characters3, dir3) = NewWorld();
        var (near, _) = JoinAt(world3, characters3, "0000", Vector2.Zero);
        var replies = new List<string>();
        world3.Commands.Execute(new CommandContext(world3, near.Player, AccountLevel.GameMaster, (t, _) => replies.Add(t)), ".near 3");
        var lines = replies.SelectMany(r => r.Split('\n')).ToList();
        Check(lines.Count == 5 && lines[0].StartsWith("0000 at (0, 0), cell (30, 30): 36 objects in view (")
              && lines.Skip(1).Take(3).All(l => Regex.IsMatch(l, @"^  [a-z ]+ .+ at \(-?[\d.]+, -?[\d.]+\), [\d.]+ m$")) && lines[^1].StartsWith("  ... 33 more"),
              $".near 3: header (17 + 16 trees + the trader + 2 fishing zones), the 3 nearest, the rest counted (got {string.Join(" / ", lines)})");
        world3.Dispose();
        characters3.Dispose();
        try { Directory.Delete(dir3, true); } catch (IOException) { }
    }

    private static void SimpleBehaviourState()
    {
        var (world, characters, dir) = NewWorld();
        try
        {
            var arrival = new Vector2(66.5f, 111.5f);
            _ = Store.Get("0004");
            var (session, peer) = JoinAt(world, characters, "0004", arrival);
            var player = session.Player!;
            var building = player.Map!.Objects.Values.OfType<AlbionLocal.Server.Entities.Building>()
                .Single(b => b.Template.Kind == "simplebehaviourbuilding");
            Check(building.Template.Name == "T1_TUTORIAL_CAPTAIN_TIA_JOURNAL_01"
                && building.Template.Element.Elements("triggerablestate").Any(s =>
                    (string?)s.Attribute("name") == "HIDDEN" && (bool?)s.Attribute("default") == true),
                "the arrival journal NPC declares HIDDEN as its default state");

            void CheckHidden(IEnumerable<EventData> events, Player viewer)
            {
                var sequence = events.ToList();
                Check(!viewer.Visibility.Knows(building), "hidden journal NPC never enters the viewer's known objects");
                Check(!sequence.Any(e => Code(e) is (int)EventCodes.NewBuilding
                        or (int)EventCodes.SimpleBehaviourBuildingStateUpdate or (int)EventCodes.AccessStatus
                        or (int)EventCodes.Leave
                    && e.Parameters.TryGetValue(0, out var id) && Convert.ToInt64(id) == building.Id),
                    "hidden journal NPC has no spawn, state, access or leave event");
                Check(sequence.Any(e => Code(e) == (int)EventCodes.NewBuilding),
                    "ordinary visible buildings remain announced");
            }

            Check(!building.IsInitiallyVisible && !building.BuildCreate(player).Any(),
                "the authored invisible default has no creation block, even outside the visibility tracker");
            CheckHidden(peer.Events, player);
            var (other, otherPeer) = JoinAt(world, characters, "0004", arrival);
            Check(ReferenceEquals(other.Player!.Map, player.Map), "both viewers share the public map");
            CheckHidden(otherPeer.Events, other.Player!);
            var otherCount = otherPeer.Events.Count(e => Code(e) == (int)EventCodes.SimpleBehaviourBuildingStateUpdate);

            var before = peer.Events.Count;
            player.Visibility.Refresh(new Vector2(220, 220));
            Check(!peer.Events.Skip(before).Any(e => Code(e) == (int)EventCodes.Leave
                && Convert.ToInt64(e.Parameters[0]) == building.Id), "unannounced journal NPC needs no Leave");
            before = peer.Events.Count;
            player.Visibility.Refresh(arrival);
            CheckHidden(peer.Events.Skip(before), player);
            Check(otherPeer.Events.Count(e => Code(e) == (int)EventCodes.SimpleBehaviourBuildingStateUpdate) == otherCount,
                "one viewer's reentry does not update the other viewer's building state");

            var characterId = player.Character.Id;
            session.OnDisconnected("simple behaviour reconnect");
            world.Saves.Flush();
            var reconnectPeer = new FakePeer();
            var reconnect = new WorldSession(reconnectPeer, world);
            reconnect.OnRequest(Request(new JoinRequest { ss = characterId }, 1), false);
            Check(reconnect.Player?.Map == other.Player.Map, "reconnect returns to the same public map");
            CheckHidden(reconnectPeer.Events, reconnect.Player!);

            // Read the authored default rather than hardcoding this NPC's HIDDEN state.
            var alternate = building.Spawn with { Template = building.Template with {
                Element = XElement.Parse("<simplebehaviourbuilding><triggerablestate name='READY' default='true'><animatorstatebool name='HasJournal' value='true'/></triggerablestate></simplebehaviourbuilding>") } };
            var alternateBuilding = new AlbionLocal.Server.Entities.Building(99999, alternate, 0, 0);
            var visibleEvents = alternateBuilding.BuildCreate(reconnect.Player!).ToList();
            Check(alternateBuilding.IsInitiallyVisible && visibleEvents[0] is NewBuildingEvent
                && visibleEvents[1] is SimpleBehaviourBuildingStateUpdateEvent { dyl: "READY" },
                "a visible default still sends NewBuilding followed by its own state");
            var writer = new ByteWriter();
            Protocol18.WriteEventData(writer, AlbionSerializer.BuildEvent(visibleEvents[1]));
            var wire = Protocol18.ReadEventData(new ByteReader(writer.ToArray())).Parameters;
            Check(Convert.ToInt64(wire[0]) == alternateBuilding.Id && wire[1] is "READY",
                "visible state event carries the ID and state on the wire");
        }
        finally
        {
            world.Dispose();
            characters.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
