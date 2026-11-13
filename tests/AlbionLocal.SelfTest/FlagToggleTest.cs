using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageJ flag-toggle (analysis/work/stageJ/flag-toggle/NOTES.md): the HUD PvP button's op 354 in the real cluster data. Red and
/// Blue work in a city and a blue zone (pve) and in a black zone (official; Red refused in a blue zone there), a refusal ends the client's own cast (CastCancel to
/// the player, a SystemMessage reason, nothing to the others), a CastStart for slot 24 gets no answer, and the events come in the
/// order CastStart to the others, then CastSpell + ChangeFlaggingFinished to both.
/// </summary>
public static class FlagToggleTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("flag toggle: Red/Blue in Thetford and a blue zone (pve), black (official); refusals end the client cast; slot 24 CastStart unanswered", Toggle),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private sealed class Zone : IDisposable
    {
        private readonly string _dir = NewDir();
        private readonly ManualClock _clock = new();
        private readonly AlbionLocal.Server.Persistence.SqliteCharacterRepository _characters;
        public readonly World World;
        public readonly (WorldSession S, FakePeer P) A, B;

        public Zone(ServerConfig cfg, string cluster)
        {
            _characters = Repository(_dir);
            var anchor = GameTime.Now;
            var store = Client.Clusters!;
            World = new World(cfg with { JoinByCharacterId = true }, _characters, Client.Items, Client.GameData, Client.Spells)
            {
                Sync = new object(), Clock = _clock, Clusters = store, GameTicks = () => anchor + _clock.Now.Ticks, MapSeed = 3,
            };
            var pos = store.Get(cluster).Exits.Select(e => e.SpawnPosition).FirstOrDefault(p => p is not null) ?? Vector2.Zero;
            A = Join("Ann", cluster, pos);
            B = Join("Bob", cluster, pos);
        }

        private (WorldSession, FakePeer) Join(string name, string cluster, Vector2 pos)
        {
            var c = StarterKit.Default.NewCharacter(name);
            (c.Cluster, c.PositionX, c.PositionY) = (cluster, pos.X, pos.Y);
            Check(_characters.Create(c), $"character {name} created");
            var peer = new FakePeer();
            var s = new WorldSession(peer, World);
            peer.Disconnected = s.OnDisconnected;
            s.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
            return (s, peer);
        }

        public void Run(double seconds)
        {
            for (var i = 0; i < Math.Round(seconds * 20); i++)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(50));
                lock (World.Sync) World.Update();
            }
        }

        public short Flag(byte to)
        {
            var n = A.P.Responses.Count;
            A.S.OnRequest(Request(new ChangeFlaggingPrepareRequest { eo = to }, 5), encrypted: false);
            Check(A.P.Responses.Count == n + 1, "one response to op 354");
            return A.P.Responses[^1].ReturnCode;
        }

        public void Dispose()
        {
            World.Dispose();
            _characters.Dispose();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    /// <summary>Op 354 to <paramref name="to"/> is accepted and 15.2 s later the flag is set, with the captured event order (364 key 1 left out for Blue).</summary>
    private static void Switch(Zone z, byte to, string where)
    {
        var p = z.A.S.Player!;
        var am = z.A.P.Events.Count;
        var bm = z.B.P.Events.Count;
        Check(z.Flag(to) == 0, $"{where}: op 354 {{0 {to}}} rc 0");
        z.Run(15.2);
        var seen = Since(z.B.P, bm).Where(e => e.Code is EventCodes.CastStart or EventCodes.CastFinished or EventCodes.CastSpell or EventCodes.ChangeFlaggingFinished).Select(e => e.Code).ToList();
        var finished = Since(z.B.P, bm).FirstOrDefault(e => e.Code == EventCodes.CastFinished).P;
        Check(p.Flag == to && seen is [EventCodes.CastStart, EventCodes.CastFinished, EventCodes.CastSpell, EventCodes.ChangeFlaggingFinished]
              && finished is not null && Convert.ToByte(finished[3]) == Flagging.Slot && Convert.ToInt16(finished[2]) == Client.Spells!.IdOf(Flagging.SpellName(to))
              && Since(z.A.P, am).Any(e => e.Code == EventCodes.ChangeFlaggingFinished && (e.P.ContainsKey(1) ? L(e.P, 1) : 0) == to)
              && !Since(z.A.P, am).Any(e => e.Code is EventCodes.CastCancel or EventCodes.CastStart),
              $"{where}: flag {to} after 15 s: B sees 14, 18, 19, 364; A gets 364, no CastCancel ({string.Join(",", seen)})");
    }

    /// <summary>Op 354 to <paramref name="to"/> is refused: rc -501, CastCancel and a SystemMessage to A only, the flag unchanged.</summary>
    private static void Refused(Zone z, byte to, string where)
    {
        var p = z.A.S.Player!;
        var before = p.Flag;
        var am = z.A.P.Events.Count;
        var bm = z.B.P.Events.Count;
        Check(z.Flag(to) == Flagging.Refused, $"{where}: op 354 {{0 {to}}} refused (-501)");
        var toA = Since(z.A.P, am);
        Check(toA.Any(e => e.Code == EventCodes.CastCancel && L(e.P, 0) == p.Id && e.P[1] is true) && toA.Any(e => e.Code == EventCodes.SystemMessage)
              && !Since(z.B.P, bm).Any(e => e.Code is EventCodes.CastStart or EventCodes.CastCancel) && p.Flag == before && p.PvpState.FlagCast is null,
              $"{where}: CastCancel {{0 A, 1 true}} and a SystemMessage reason to A, nothing to B");
    }

    private static void Toggle()
    {
        using (var city = new Zone(ServerConfig.PveDefaults, "0000"))
        {
            Switch(city, Pvp.Red, "pve Thetford (PvpDisabled)");
            Switch(city, Pvp.Blue, "pve Thetford, back");
            var p = city.A.S.Player!;
            var n = city.A.P.Responses.Count;
            Check(city.Flag(Pvp.Red) == 0, "pve Thetford: Red again");
            city.A.S.OnRequest(Request(new CastStartRequest { wf = city.World.GameNow, dx = Flagging.Slot, d0 = Client.Spells!.IdOf("FLAG_RED"), d4 = 1 }, 6), encrypted: false);
            Check(city.A.P.Responses.Count == n + 1 && p.PvpState.FlagCast is not null, "the client's CastStart for slot 24: no answer (no 960), the flag cast goes on");
            city.Run(15.2);
            Check(p.Flag == Pvp.Red, "and the flag is Red 15 s later");
            Flagging.Arrive(p, ClusterPvp.City);
            Flagging.Arrive(p, new ClusterPvp(PvpRule.FactionPvpOnly));
            Check(p.Flag == Pvp.Red, "pve: Red is kept on joining a city or a blue zone (the consent)");
        }
        using (var blue = new Zone(ServerConfig.PveDefaults, "1207"))
            Switch(blue, Pvp.Red, "pve blue 1207 (FactionPvpOnly)");
        using (var blue = new Zone(new ServerConfig(), "1207"))
            Refused(blue, Pvp.Red, "official blue 1207 (Arrive would reset it)");
        using (var black = new Zone(new ServerConfig(), "2305"))
        {
            Switch(black, Pvp.Red, "official black 2305 (PvpForced)");
            Refused(black, Pvp.Blue, "official, CANNOTDISABLEPVP on");
        }
        using (var off = new Zone(ServerConfig.PveDefaults with { PvpMode = PvpMode.Off }, "0000"))
            Refused(off, Pvp.Red, "Pvp:Mode Off");
    }
}
