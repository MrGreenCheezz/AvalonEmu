using System.Buffers.Binary;
using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage 7 (analysis/work/stage7/SPEC.md §6): players in one World through <see cref="FakePeer"/>s, the movement codec
/// against movecodec.py and a port of the client's decoder, and the accounts per login name.
/// </summary>
public static class MultiplayerTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("movement codec: golden vectors of movecodec.py, the client decoder port reads them back", Codec),
        ("multiplayer: spawn both ways, movement relay, equipment, Say, Leave and re-enter by range, ghost, disconnect", TwoPlayers),
        ("accounts: one per login name, the local account claimed by the first name or Accounts:LegacyName, names unique server-wide", Accounts),
    ];

    /// <summary>One event 3 sample as the client's decoder fills as2 (c3f.a 0x180a7f880).</summary>
    internal sealed record Sample(byte Flags, long Ts, Vector2 Pos, float Direction, float Speed, Vector2 Target, long Cs2, bool Cs3);

    /// <summary>
    /// Test-side port of the client's decoder c3f.a 0x180a7f880 without a KeySync key (from movecodec.decode_client, not
    /// from captures): flags, ts i64, pos f32×2, direction byte (b · 0.00390625 · 360 in float, a71.a 0x180509e50), speed if
    /// flag 1 (else 0), target if flag 2 (else pos), cs2 from the remaining length if flag 4 (8/4/2 bytes, sign extended;
    /// any other length is the client's "Move decode failure"), cs3 = flag 8. A short buffer throws, as the client.
    /// </summary>
    internal static Sample Decode(byte[] data)
    {
        var flags = data[0];
        var p = 1;
        var ts = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(p, 8));
        p += 8;
        float F()
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(p, 4));
            p += 4;
            return v;
        }
        var pos = new Vector2(F(), F());
        var b = data[p++];
        var f = b * 0.00390625f;
        var direction = f * 360f;
        var speed = (flags & 1) != 0 ? F() : 0f;
        var target = (flags & 2) != 0 ? new Vector2(F(), F()) : pos;
        long cs2 = 0;
        if ((flags & 4) != 0)
            cs2 = (data.Length - p) switch
            {
                8 => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(p)),
                4 => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)),
                2 => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p)),
                var n => throw new Exception($"Move decode failure: {n} bytes of cs2"),
            };
        return new Sample(flags, ts, pos, direction, speed, target, cs2, (flags & 8) != 0);
    }

    /// <summary>Direction error of a decoded sample: at most half a step of 360/256 (1.40625°).</summary>
    private static bool SameDirection(float decoded, float degrees) => Math.Abs((((decoded - degrees) % 360f) + 540f) % 360f - 180f) <= 360f / 512f + 1e-3f;

    private static void Codec()
    {
        // analysis/work/stage7/movement/movecodec.py encode() at ts 639012345678901234 (its choices: speed only when non-zero,
        // target only when it differs, cs2 in the narrowest form); this encoder writes the same bytes for the same choices.
        const long ts = 639_012_345_678_901_234;
        var golden = new (string Hex, byte[] Ours)[]
        {
            ("00f2af47bf573ade08000048410000e8c040",
                MovementCodec.Encode(ts, new(12.5f, -7.25f), 90f, null, null, null)),
            ("01f2af47bf573ade08000048410000e8c0200000b040",
                MovementCodec.Encode(ts, new(12.5f, -7.25f), 45f, 5.5f, null, null)),
            ("02f2af47bf573ade0800e096c30040b04200000091c30000a142",
                MovementCodec.Encode(ts, new(-301.75f, 88.125f), 359.9f, null, new(-290f, 80.5f), null)),
            ("03f2af47bf573ade0800e096c30040b042c10000b040000091c30000a142",
                MovementCodec.Encode(ts, new(-301.75f, 88.125f), 271.3f, 5.5f, new(-290f, 80.5f), null)),
            ("07f2af47bf573ade080000404000008040070000e8400000f041000020420700000000010000",
                MovementCodec.Encode(ts, new(3f, 4f), 10f, 7.25f, new(30f, 40f), (1L << 40) + 7)),
        };
        foreach (var (hex, ours) in golden)
            Check(Convert.ToHexString(ours).Equals(hex, StringComparison.OrdinalIgnoreCase), $"{ours.Length} bytes as movecodec.py: {hex} (got {Convert.ToHexString(ours)})");
        var s38 = Decode(Convert.FromHexString(golden[4].Hex));
        Check(s38 is { Ts: ts, Speed: 7.25f, Cs2: (1L << 40) + 7, Cs3: false } && s38.Pos == new Vector2(3, 4) && s38.Target == new Vector2(30, 40)
              && SameDirection(s38.Direction, 10f), "38 bytes: speed, target and an 8-byte cs2 read back");
        // 34 bytes: movecodec's 4-byte cs2, which this encoder never writes; the decoder port reads it as the client does.
        var s34 = Decode(Convert.FromHexString("07f2af47bf573ade080000404000008040070000e8400000f04100002042a0860100"));
        Check(s34.Cs2 == 100000 && s34.Target == new Vector2(30, 40), "34 bytes: a 4-byte cs2, sign extended");
        var s26 = Decode(Convert.FromHexString(golden[2].Hex));
        Check(s26 is { Speed: 0, Flags: MovementCodec.FlagTarget } && s26.Direction == 0 && s26.Target == new Vector2(-290, 80.5f),
              "26 bytes: no speed (0), a target; 359.9° rounds to byte 0");
        var s18 = Decode(Convert.FromHexString(golden[0].Hex));
        Check(s18.Target == s18.Pos && s18.Direction == 90f, "18 bytes: target = position, 90° exact");
        // The relay's choice: always the speed (a stop is 22 bytes), a target only when it differs, cs2 only when set.
        var stop = MovementCodec.EncodeSample(ts, new(1, 2), 0, 0, new(1, 2), 0);
        var walk = MovementCodec.EncodeSample(ts, new(1, 2), 0, 5.5f, new(3, 4), 0);
        var tail = MovementCodec.EncodeSample(ts, new(1, 2), 0, 5.5f, new(3, 4), -5);
        Check(stop.Length == 22 && walk.Length == 30 && tail.Length == 38 && Decode(tail).Cs2 == -5, "relay samples: stop 22, walk 30, walk with cs2 38 bytes");
        var rnd = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var pos = new Vector2(rnd.NextSingle() * 1200 - 600, rnd.NextSingle() * 1200 - 600);
            var target = rnd.Next(3) == 0 ? pos : new Vector2(rnd.NextSingle() * 1200 - 600, rnd.NextSingle() * 1200 - 600);
            var dir = rnd.NextSingle() * 1440 - 720;
            var speed = rnd.NextSingle() * 12;
            long cs2 = rnd.Next(3) == 0 ? rnd.NextInt64() : 0;
            var t = ts + rnd.NextInt64(1_000_000_000_000);
            var d = Decode(MovementCodec.EncodeSample(t, pos, dir, speed, target, cs2));
            Check(d.Ts == t && d.Pos == pos && d.Target == target && d.Speed == speed && d.Cs2 == cs2 && SameDirection(d.Direction, dir),
                  $"round trip {i}: {pos} → {target} at {speed}, {dir}°, cs2 {cs2}");
        }
    }

    /// <summary>A character of its own in 0000 at <paramref name="at"/>, no items.</summary>
    private static Character NewHero(SqliteCharacterRepository characters, string name, Vector2 at, int template = 0)
    {
        var c = StarterKit.Default.NewCharacter(name);
        c.Cluster = "0000";
        c.PositionX = at.X;
        c.PositionY = at.Y;
        c.Appearance = (byte)template;
        c.Equipment = [];
        c.Inventory = [];
        Check(characters.Create(c), $"character {name} created");
        return c;
    }

    private sealed record Client(WorldSession Session, FakePeer Peer, long Id);

    private static Client Join(World world, Guid character)
    {
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        peer.Disconnected = session.OnDisconnected;
        session.OnRequest(Request(new JoinRequest { ss = character }, 1), encrypted: false);
        Check(session.Player is not null, "joined");
        return new Client(session, peer, session.Player!.Id);
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static IEnumerable<EventData> Of(FakePeer peer, EventCodes code, int from = 0) =>
        peer.Events.Skip(from).Where(e => e.Code == AlbionSerializer.PhotonEventCode && Code(e) == (int)code);

    private static IEnumerable<EventData> Moves(FakePeer peer, int from = 0) => peer.Events.Skip(from).Where(e => e.Code == MovementCodec.PhotonEventCode);

    private static long Id(EventData e) => Convert.ToInt64(e.Parameters[0]);

    private static void Move(Client c, Vector2 pos, Vector2 target, float speed, float direction, long wa = 0) =>
        c.Session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [pos.X, pos.Y], v7 = direction, v8 = [target.X, target.Y], v9 = speed, wa = wa }, null), encrypted: false);

    private static void Say(Client c, string text, bool party = false) =>
        c.Session.OnRequest(Request(new SayRequest { aa9 = text, abb = party }, null), encrypted: false);

    private static void TwoPlayers()
    {
        var dir = NewDir();
        var characters = Repository(dir);
        // A 5 m west of B (cells 0 and 1 of the origin-(0, 0) grid a map without a template uses); C 30 cells away.
        var heroA = NewHero(characters, "Anna", new(5, 5), template: 1);
        heroA.Customization = [1, 2, 3, 4];
        heroA.Clothes = [5, 6, 7, 8];
        heroA.Equipment = [new("T4_MAIN_SWORD"), null, null, null, null, null, null, null, null, null];
        heroA.Inventory = [new("T4_HEAD_CLOTH_SET1")];
        characters.Save(heroA);
        var heroB = NewHero(characters, "Boris", new(10, 5));
        heroB.Equipment = [new("T4_MAIN_SWORD"), null, null, null, null, null, null, null, null, null];
        characters.Save(heroB);
        var heroC = NewHero(characters, "Carl", new(300, 5));
        var items = Items("8610\tT4_MAIN_SWORD\tweapon\tmainhand\t4\t13500", "1000\tT4_ORE\tsimpleitem\t\t4", "2000\tT4_HEAD_CLOTH_SET1\tequipmentitem\thead\t4\t13500");
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, items) { Sync = new object(), Clock = new ManualClock() };

        // 1. Spawn: B's phase A holds NewCharacter(A) before its items and the Join response; A gets NewCharacter(B) meanwhile.
        var a = Join(world, heroA.Id);
        Check(!Of(a.Peer, EventCodes.NewCharacter).Any(), "alone: no NewCharacter");
        var aBefore = a.Peer.Events.Count;
        var b = Join(world, heroB.Id);
        var seq = b.Peer.Sequence;
        var ncAt = seq.FindIndex(o => o is EventData e && Code(e) == (int)EventCodes.NewCharacter && Id(e) == a.Id);
        var itemAt = seq.FindIndex(o => o is EventData e && Code(e) is (int)EventCodes.NewEquipmentItem or (int)EventCodes.NewSimpleItem);
        var joinAt = seq.FindIndex(o => o is OperationResponse);
        Check(ncAt >= 0 && ncAt < itemAt && itemAt < joinAt, $"B: NewCharacter(A) in phase A, before the items and the Join response (at {ncAt}, {itemAt}, {joinAt})");
        Check(Of(a.Peer, EventCodes.NewCharacter, aBefore).Select(Id).SequenceEqual([b.Id]), "A gets NewCharacter(B) during B's Join");
        Check(a.Session.Player!.Observers.Single() == b.Session.Player && b.Session.Player!.Observers.Single() == a.Session.Player, "each observes the other");

        // 2. The keys of NewCharacter(A) as B got it.
        var nc = (EventData)seq[ncAt];
        var k = nc.Parameters;
        var pa = a.Session.Player!;
        Check(k[1] is "Anna" && Convert.ToInt32(k[4]) == 1 && k[7] is byte[] guid && new Guid(guid) == heroA.Id
              && k[5] is int[] and [1, 2, 3, 4] && k[6] is int[] and [5, 6, 7, 8], "0 id, 1 name, 4 template 1, 7 Guid; 5/6 sent: both have the female template's 4 entries");
        var stamp18 = k.TryGetValue(18, out var s18) && s18 is long l18 ? l18 : 0;
        Check(k[14] is float[] and [5f, 5f] && k[15] is float[] and [5f, 5f] && stamp18 > 0 && k[20] is 5.5f,
              "14/15 the stored position (never moved), 18 set (Join time), 20 = 5.5 (never 0)");
        Check(new byte[] { 22, 23, 26, 27, 28, 31 }.All(k.ContainsKey) && k[40] is int[] eq && eq.SequenceEqual(pa.Inventory.EquipmentTypeIds())
              && eq[0] == 8610 && k[43] is int[] { Length: 14 } && k[55] is int[] { Length: 7 },
              "bars 22/23/26 and 27/28/31; 40 = EquipmentTypeIds() (int[10]); 43 int[14]; 55 int[7]");
        Check(!k.ContainsKey(16) && !k.ContainsKey(17) && !Enumerable.Range(32, 5).Any(i => k.ContainsKey((byte)i)),
              "no 16/17 (no FillEmpty: they would be byte[0]), no mount bar without a mount");
        var ncB = Of(a.Peer, EventCodes.NewCharacter, aBefore).Single().Parameters;
        Check(!ncB.ContainsKey(4) && !ncB.ContainsKey(5) && !ncB.ContainsKey(6), "B: template 0 left out as the default, no customization arrays (empty ones do not fit)");

        // C joins 30 cells away: nobody hears of anybody.
        var c = Join(world, heroC.Id);
        Check(!Of(c.Peer, EventCodes.NewCharacter).Any() && Of(a.Peer, EventCodes.NewCharacter).Count() == 1 && Of(b.Peer, EventCodes.NewCharacter).Count() == 1,
              "C out of range: no NewCharacter either way");

        // 3. Movement: walk, change target, stop → three event 3 to B, none to A or C.
        var bMoves = Moves(b.Peer).Count();
        Move(a, new(5, 5), new(8, 9), 5.5f, 53.13f);
        Move(a, new(5.5f, 5.6f), new(2, 9), 6f, 135f, wa: 77);
        Move(a, new(5.2f, 6f), new(5.2f, 6f), 0, 140f);
        var relayed = Moves(b.Peer, 0).ToList();
        Check(relayed.Count == bMoves + 3 && relayed.All(e => Id(e) == a.Id && e.Parameters[1] is byte[]), "B: three event 3 {0: A's id, 1: bytes}");
        Check(b.Peer.Unreliable.Count == 3 && b.Peer.Unreliable.All(u => u.Channel == 0 && u.Event.Code == 3), "sent unreliable on channel 0");
        Check(!Moves(a.Peer).Any() && !Moves(c.Peer).Any(), "no event 3 to the mover or to a player out of range");
        var samples = relayed.Select(e => Decode((byte[])e.Parameters[1]!)).ToList();
        Check(samples[0] is { Speed: 5.5f, Cs2: 0 } && samples[0].Pos == new Vector2(5, 5) && samples[0].Target == new Vector2(8, 9) && SameDirection(samples[0].Direction, 53.13f),
              "walk: position, target, speed, direction (±0.7°)");
        Check(samples[1] is { Speed: 6f, Cs2: 77 } && samples[1].Pos == new Vector2(5.5f, 5.6f) && samples[1].Target == new Vector2(2, 9) && SameDirection(samples[1].Direction, 135f),
              "new target, Move key 5 as cs2");
        Check(samples[2].Pos == samples[2].Target && samples[2].Pos == new Vector2(5.2f, 6f) && ((byte[])relayed[2].Parameters[1]!).Length == 22,
              "stop: position = target, 22 bytes");
        Check(samples[0].Ts >= stamp18 && samples[1].Ts >= samples[0].Ts && samples[2].Ts >= samples[1].Ts, "ts never goes back, and not before NewCharacter key 18");

        // 5. Equipment: A puts the head item on; B gets the same CharacterEquipmentChanged, C nothing.
        var (aEv, bEv, cEv) = (a.Peer.Events.Count, b.Peer.Events.Count, c.Peer.Events.Count);
        a.Session.OnRequest(Request(new InventoryMoveItemRequest { ru = 0, rv = heroA.InventoryContainer, rx = 2, ry = heroA.EquipmentContainer, rz = 1 }, null), encrypted: false);
        var own = Of(a.Peer, EventCodes.CharacterEquipmentChanged, aEv).Single().Parameters;
        var seen = Of(b.Peer, EventCodes.CharacterEquipmentChanged, bEv).Single().Parameters;
        Check(Id(Of(b.Peer, EventCodes.CharacterEquipmentChanged, bEv).Single()) == a.Id && seen[2] is int[] s2 && s2.SequenceEqual((int[])own[2]!) && s2[2] == 2000
              && seen[7] is int[] s7 && s7.SequenceEqual((int[])own[7]!) && !seen.ContainsKey(3), "B: CharacterEquipmentChanged of A with A's keys 2 and 7, no key 3");
        Check(c.Peer.Events.Count == cEv, "C: nothing");

        // 6. Say: A and B get one ChatSay each, C none; a command line none; a party line A only.
        (aEv, bEv, cEv) = (a.Peer.Events.Count, b.Peer.Events.Count, c.Peer.Events.Count);
        Say(a, "  hello  ");
        bool IsHello(EventData e) => e.Parameters[0] is "Anna" && e.Parameters[1] is "hello" && Convert.ToInt64(e.Parameters[4]) == a.Id
            && e.Parameters[5] is byte[] g && new Guid(g) == heroA.Id && e.Parameters[2] is (byte)0 && !e.Parameters.ContainsKey(6) && !e.Parameters.ContainsKey(9);
        Check(Of(a.Peer, EventCodes.ChatSay, aEv).ToList() is [var echo] && IsHello(echo) && Of(b.Peer, EventCodes.ChatSay, bEv).ToList() is [var heard] && IsHello(heard)
              && a.Peer.Responses[^1].Parameters[ReservedKeys.OperationCode] is (short)(int)OperationCodes.Say,
              "ChatSay to the speaker (echo) and the observer: 0 name, 1 text trimmed, 2 byte 0, 4 id, 5 Guid; after the Say response");
        Check(!Of(c.Peer, EventCodes.ChatSay, cEv).Any(), "C out of range: no ChatSay");
        Say(a, ".help");
        Say(a, "us only", party: true);
        Check(Of(a.Peer, EventCodes.ChatSay, aEv).Count() == 2 && Of(a.Peer, EventCodes.ChatSay).Last().Parameters[9] is true
              && Of(b.Peer, EventCodes.ChatSay, bEv).Count() == 1, "a GM command is no chat; a party line goes back to the speaker only (9 = true)");

        // 4. Leave and re-enter: at cell distance 5 nothing, at 6 one Leave each way, then no more event 3; back → NewCharacter both ways.
        (aEv, bEv) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Move(a, new(-35, 5), new(-35, 5), 5.5f, 180f);
        Check(!Of(a.Peer, EventCodes.Leave, aEv).Any() && !Of(b.Peer, EventCodes.Leave, bEv).Any(), "cell distance 5 (LeaveCells − 1): no Leave");
        Move(a, new(-45, 5), new(-45, 5), 5.5f, 180f);
        Check(Of(a.Peer, EventCodes.Leave, aEv).Select(Id).SequenceEqual([b.Id]) && Of(b.Peer, EventCodes.Leave, bEv).Select(Id).SequenceEqual([a.Id]),
              "cell distance 6 (LeaveCells): exactly one Leave(B) to A and one Leave(A) to B");
        var afterLeave = Moves(b.Peer).Count();
        Move(a, new(-46, 5), new(-46, 5), 5.5f, 180f);
        Check(Moves(b.Peer).Count() == afterLeave && pa.Observers.Count == 0, "after the Leave no event 3 of A reaches B");
        (aEv, bEv) = (a.Peer.Events.Count, b.Peer.Events.Count);
        Move(a, new(0, 5), new(0, 5), 5.5f, 0f);
        Check(Of(a.Peer, EventCodes.NewCharacter, aEv).Select(Id).SequenceEqual([b.Id]) && Of(b.Peer, EventCodes.NewCharacter, bEv).ToList() is [var again]
              && again.Parameters[14] is float[] and [0f, 5f] && again.Parameters[40] is int[] eq2 && eq2[2] == 2000
              && Convert.ToInt64(again.Parameters[18]) >= samples[2].Ts,
              "back within 4 cells: NewCharacter both ways; key 14 = A's new position, key 40 with the new head item, key 18 the newest stamp");

        // 8. Ghost: A's character joins again on a new connection: the new client never gets its own Guid in key 7; B sees the new id.
        // The old player leaves at that Join (stageE E3: no two live copies), so the old connection's end tells nobody anything.
        bEv = b.Peer.Events.Count;
        var a2 = Join(world, heroA.Id);
        Check(Of(a2.Peer, EventCodes.NewCharacter).All(e => e.Parameters[7] is byte[] g7 && new Guid(g7) != heroA.Id)
              && Of(a2.Peer, EventCodes.NewCharacter).Select(Id).SequenceEqual([b.Id]), "the new connection sees B, not its own character's old object");
        Check(Of(b.Peer, EventCodes.Leave, bEv).Select(Id).SequenceEqual([a.Id]) && Of(b.Peer, EventCodes.NewCharacter, bEv).Select(Id).SequenceEqual([a2.Id]),
              "B gets the old id's Leave and the new object");
        bEv = b.Peer.Events.Count;
        var a2Ev = a2.Peer.Events.Count;
        a.Peer.Disconnect(); // the old connection drops (its timeout)
        Check(b.Peer.Events.Count == bEv && a2.Peer.Events.Count == a2Ev, "the old connection's end: nothing to B or to the new client");

        // 7. Exit: B disconnects: A2 gets Leave(B).
        a2Ev = a2.Peer.Events.Count;
        b.Peer.Disconnect();
        Check(Of(a2.Peer, EventCodes.Leave, a2Ev).Select(Id).SequenceEqual([b.Id]) && a2.Session.Player!.Observers.Count == 0, "disconnect: Leave(B) to whoever knew B");

        foreach (var x in new[] { a, b, c, a2 })
            Check(!Of(x.Peer, EventCodes.NewCharacter).Concat(Of(x.Peer, EventCodes.Leave)).Any(e => Id(e) == x.Id),
                  "no client ever gets a NewCharacter or a Leave with its own id");
        world.Dispose();
        characters.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Accounts()
    {
        var dir = NewDir();
        var characters = Repository(dir); // the local account with LocalHero, as before the upgrade
        // The starter kit is known; Joins by account only; new names get an account at their first login (AutoRegister).
        var world = new World(new ServerConfig { AutoRegister = true }, characters, AlbionTests.Client.Items) { Sync = new object(), Clock = new ManualClock() };
        (LoginSession Session, FakePeer Peer, OperationResponse Login) Login(string name)
        {
            var peer = new FakePeer();
            var login = new LoginSession(peer, world.Config, characters, StarterKit.Default) { World = world };
            login.OnRequest(Request(TestLogin(name), 1), encrypted: true);
            return (login, peer, peer.Responses[^1]);
        }
        static Guid AccountOf(OperationResponse r) => new((byte[])r.Parameters[51]!);
        static string[] Names(OperationResponse r) => (string[])r.Parameters[9]!;

        var (_, _, alice) = Login("alice");
        Check(AccountOf(alice) == SqliteCharacterRepository.LocalAccountId && Names(alice) is ["LocalHero"],
              "the first name after the upgrade takes the local account with its character (Accounts:LegacyName empty)");
        var (bobLogin, bobPeer, bob) = Login("bob");
        var bobId = AccountOf(bob);
        Check(bobId != SqliteCharacterRepository.LocalAccountId && Names(bob) is [], "a new name: a new empty account");
        bobLogin.OnRequest(Request(new CreateCharacterRequest { h7 = "localhero" }, 2), encrypted: true);
        Check(bobPeer.Responses[^1].ReturnCode == 482, "duplicate character names across accounts use NAME_NOT_UNIQUE (ignoring case)");
        bobLogin.OnRequest(Request(new CreateCharacterRequest { h7 = "Bobby" }, 3), encrypted: true);
        bobLogin.OnRequest(Request(new SelectCharacterRequest { abc = "LocalHero" }, null), encrypted: true);
        Check(bobPeer.Responses[^2].ReturnCode == 0 && bobPeer.Responses[^1].ReturnCode == 0x231, "Bobby created; another account's character cannot be selected");
        bobLogin.OnRequest(Request(new SelectCharacterRequest { abc = "Bobby" }, null), encrypted: true);
        Check(bobPeer.Responses[^1].ReturnCode == 0, "the own character can");
        var (_, _, aliceAgain) = Login("ALICE");
        Check(AccountOf(aliceAgain) == SqliteCharacterRepository.LocalAccountId && Names(aliceAgain) is ["LocalHero"], "the same name (any case): the same account, without Bobby");
        var join = Join(world, bobId);
        Check(join.Session.Player!.Character.Name == "Bobby" && join.Session.Player.Character.Account == bobId, "Join key 0 = bob's account: the character bob selected");
        // An account without characters plays nobody else's: its Join is refused, not given the first character of the server.
        var (_, _, erin) = Login("erin");
        var erinPeer = new FakePeer();
        var erinSession = new WorldSession(erinPeer, world);
        erinSession.OnRequest(Request(new JoinRequest { ss = AccountOf(erin) }, 1), encrypted: false);
        Check(erinSession.Player is null && erinPeer.Responses[^1].ReturnCode == WorldSession.JoinRefused && world.Maps.Maps.Sum(m => m.Players.Count) == 1,
              "an account without characters: the Join is refused, no other account's character is played");
        // A character's Guid (an object Guid other players see) joins nothing without Accounts:JoinByCharacterId.
        var heroId = characters.List(SqliteCharacterRepository.LocalAccountId).First().Id;
        var thiefPeer = new FakePeer();
        var thief = new WorldSession(thiefPeer, world);
        thief.OnRequest(Request(new JoinRequest { ss = heroId }, 1), encrypted: false);
        Check(thief.Player is null && thiefPeer.Responses[^1].ReturnCode == WorldSession.JoinRefused && world.Maps.Maps.Sum(m => m.Players.Count) == 1,
              "a Join naming another account's character id is refused (Accounts:JoinByCharacterId off)");
        // The account id is no secret: a Join by it needs the account's login connection, logged in, and no ban.
        static bool Refused(World w, Guid key)
        {
            var peer = new FakePeer();
            var session = new WorldSession(peer, w);
            session.OnRequest(Request(new JoinRequest { ss = key }, 1), encrypted: false);
            return session.Player is null && peer.Responses[^1].ReturnCode == WorldSession.JoinRefused;
        }
        bobLogin.OnRequest(Request(TestLogin("bob", "wrong-pass"), 4), encrypted: true);
        Check(bobPeer.Responses[^1].ReturnCode == LoginCodes.InvalidAccountNameOrPassword && Refused(world, bobId),
              "a refused Login ends the connection's login: a Join by bob's account id is refused");
        var (_, _, bobAgain) = Login("bob");
        characters.UpdateAccount(characters.FindAccount(bobId)! with { BannedUntil = DateTime.UtcNow.AddHours(1) });
        Check(AccountOf(bobAgain) == bobId && Refused(world, bobId), "logged in, then banned: the Join is refused");
        characters.UpdateAccount(characters.FindAccount(bobId)! with { BannedUntil = null });
        world.Dispose();
        characters.Dispose();
        using (var reopened = new SqliteCharacterRepository(Path.Combine(dir, "albionlocal.db")))
            Check(reopened.FindAccount("Bob") is { Name: "bob", PasswordHash: not null } acc && acc.Id == bobId
                  && reopened.List(bobId) is [{ Name: "Bobby" }] && reopened.Account is { Name: "alice" },
                  "accounts and their characters are saved; the local account keeps the name that took it");

        var dir2 = NewDir();
        using (var legacy = Repository(dir2))
        {
            var config = new ServerConfig { LegacyAccountName = "Carol", AutoRegister = true, DefaultAccountLevel = AccountLevel.Player };
            OperationResponse LoginAs(string name)
            {
                var peer = new FakePeer();
                new LoginSession(peer, config, legacy, StarterKit.Default).OnRequest(Request(TestLogin(name), 1), encrypted: true);
                return peer.Responses[^1];
            }
            Check(LoginAs("dave") is { ReturnCode: 0 } dave && AccountOf(dave) != SqliteCharacterRepository.LocalAccountId && Names(dave) is []
                  && legacy.FindAccount("dave") is { Level: AccountLevel.Player }, "Accounts:LegacyName set: another name gets an empty account");
            Check(LoginAs("carol") is { ReturnCode: 0 } carol && AccountOf(carol) == SqliteCharacterRepository.LocalAccountId && Names(carol) is ["LocalHero"]
                  && legacy.Account.Level == AccountLevel.Admin, "the named login takes the local account");
        }
        // Accounts:LocalAccountLevel: the local account's level in a new database only; a reopened one keeps the stored level.
        var dir3 = NewDir();
        var path3 = Path.Combine(dir3, "albionlocal.db");
        using (var fresh = new SqliteCharacterRepository(path3, AccountLevel.GameMaster))
            Check(fresh.Account.Level == AccountLevel.GameMaster, "a new database's local account is at Accounts:LocalAccountLevel");
        using (var again = new SqliteCharacterRepository(path3, AccountLevel.Admin))
            Check(again.Account.Level == AccountLevel.GameMaster, "an existing local account keeps its stored level");
        foreach (var d in new[] { dir, dir2, dir3 })
            try { Directory.Delete(d, true); } catch (IOException) { }
    }
}
