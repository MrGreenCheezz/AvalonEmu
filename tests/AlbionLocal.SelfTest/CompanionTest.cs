using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageN companions (analysis/work/stageN/companions/SPEC.md): .companion hire puts a tank and a healer into the player's party, and they
/// follow him through Stoneroot's entrance in 4206 into the static dungeon; a healer heals its hurt owner with a heal of its holy staff.
/// The pve preset's Companions, the world's bots off (companions run without them).
/// </summary>
public static class CompanionTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("companions: .companion hire tank + healer: both in the player's party (PartyJoined of 3), then through Stoneroot's entrance they follow him into the dungeon, still in the party", HireAndFollow),
        ("companions: a healer heals its owner below Companions:HealBelow with a heal of its kit (HealthUpdate caused by it); .companion dismiss all ends the party", Heals),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private const string Dungeon = "DNG-KPR-02-MAIN-04", Entrance = "4206";

    private static BotOptions Off => new() { Enabled = false };

    private static void HireAndFollow()
    {
        _ = Client.Clusters!.Get(Dungeon);
        using var f = new Field(Off, cluster: Entrance);
        var stoneroot = f.World.StaticDungeons.Of(Dungeon)!;
        var inside = Client.Clusters.World.Find(Entrance)!.Exits.Single(e => e.Attrs.GetValueOrDefault("staticdungeonid") == stoneroot.Id.ToString());
        var (session, peer, me) = f.Join("Leader", inside.Position);
        var replies = f.Gm(me, ".companion hire tank") + "\n" + f.Gm(me, ".companion hire healer");
        var mine = f.World.Companions.Of(me.Character.Id);
        var party = f.World.Parties.Of(me.Character.Id);
        var joined = Of(peer, EventCodes.PartyJoined).LastOrDefault();
        Check(mine.Count == 2 && mine[0].Bot!.Companion!.Kind == CompanionKind.Tank && mine[1].Bot!.Companion!.Kind == CompanionKind.Healer
              && party is { Members.Count: 3 } && party.Leader == me.Character.Id && mine.All(b => f.World.Parties.Of(b.Character.Id) == party)
              && joined?[9] is string[] { Length: 2 } && Of(peer, EventCodes.PartyPlayerJoined).Count == 1,
              $"two companions in a party of 3 led by the player: PartyJoined (2 names), then PartyPlayerJoined ({replies.Replace('\n', ' ')})");
        Check(mine.All(b => b.Map == f.Map && b.Observers.Contains(me)) && mine[0].Inventory.Equipment[0]?.Template.Name.Contains("MACE") == true
              && mine[1].Inventory.Equipment[0]?.Template.Name.Contains("HOLYSTAFF") == true,
              $"both stand by him in view, the tank with a mace ({mine[0].Inventory.Equipment[0]?.Template.Name}), the healer with a holy staff ({mine[1].Inventory.Equipment[0]?.Template.Name})");

        f.Run(11); // the open-world exit cooldown
        var door = f.Map.Objects.Values.OfType<Exit>().Single(x => x.World.Id == inside.Id);
        lock (f.World.Sync)
        {
            session.OnRequest(Request(new MoveRequest { wf = DateTime.UtcNow.Ticks, v6 = [door.Position.X, door.Position.Y], v8 = [door.Position.X, door.Position.Y], v9 = 0f }, null), encrypted: false);
            session.OnRequest(Request(new ChangeClusterRequest { ee = door.Id, ef = "", eg = [-1] }, 5), encrypted: false);
        }
        Check(peer.Responses.LastOrDefault() is { ReturnCode: 0 } r && r.Parameters[0] is Dungeon, "the entrance answers {0: Stoneroot}");
        f.Run(1); // the owner is between the two connections: they wait
        Check(mine.All(b => b.Map == f.Map) && f.World.Parties.Of(me.Character.Id) == party, "during the change they wait on 4206, the party kept");
        var peer2 = new FakePeer();
        var session2 = new WorldSession(peer2, f.World);
        lock (f.World.Sync) session2.OnRequest(Request(new JoinRequest { ss = me.Character.Id }, 1), encrypted: false);
        var me2 = session2.Player!;
        f.Run(1.5);
        var dmap = f.World.Maps.Find(Dungeon)!;
        var at = me2.PositionAt(DateTime.UtcNow);
        var distances = mine.Select(b => Vector2.Distance(b.PositionAt(f.World.Bots.Time), at)).ToList();
        Check(me2.Map == dmap && mine.All(b => b.Map == dmap && b.Observers.Contains(me2)) && distances.All(d => d < 8)
              && f.World.Parties.Of(me.Character.Id) is { Members.Count: 3 } && f.World.Companions.Of(me.Character.Id).Count == 2,
              $"in Stoneroot both stand by him again ({string.Join(", ", distances.Select(d => $"{d:0.0} m"))}), seen by his new connection, the party of 3 kept");
    }

    private static void Heals()
    {
        using var f = new Field(Off);
        var (_, peer, me) = f.Join("Patient", f.Nav().Exits[0].Arrival);
        var reply = f.Gm(me, ".companion hire healer");
        var healer = f.World.Companions.Of(me.Character.Id).SingleOrDefault();
        Check(healer is not null, $"a healer hired ({reply})");
        lock (f.World.Sync) Knockdown.DamageSelf(me, me.Health.Max * 0.6f);
        var hurt = me.Health.At(f.World.GameNow);
        var mark = peer.Events.Count;
        f.Run(6);
        var heals = Of(peer, EventCodes.HealthUpdate, mark).Where(p => L(p, 0) == me.Id && L(p, 6) == healer!.Id && Convert.ToSingle(p[2]) > 0).ToList();
        var casts = Of(peer, EventCodes.CastStart, mark).Where(p => L(p, 0) == healer!.Id).Select(p => Client.Spells!.Name(Convert.ToInt32(p[5]))).ToList();
        Check(heals.Count > 0 && me.Health.At(f.World.GameNow) > hurt,
              $"the healer healed him ({heals.Count} HealthUpdate(s) caused by it, {hurt:0} → {me.Health.At(f.World.GameNow):0} of {me.Health.Max:0}; casts {string.Join(", ", casts)})");
        var list = f.Gm(me, ".companion list");
        var dismissed = f.Gm(me, ".companion dismiss");
        Check(list.Contains(healer!.Character.Name) && dismissed.Contains("1 companion") && f.World.Parties.Of(me.Character.Id) is null && healer.Map is null
              && Of(peer, EventCodes.PartyDisbanded, mark).Count == 1,
              $".companion list names it; dismissed: off the map and the party of two disbanded ({dismissed})");
    }

    private static List<Dictionary<byte, object?>> Of(FakePeer peer, EventCodes code, int from = 0) => BotTest.Of(peer, code, from);
}
