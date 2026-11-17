using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>Smugglers (analysis/work/stageR/smugglers/SPEC.md): a den's services, and a crate from a pile to a Smuggler Stash.</summary>
public static class SmugglerTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("smugglers: BLACKBANK-0307 is a den with its bank, Smuggler's Market (the network's book), repair shop, meld building and trader", Den),
        ("smugglers: a crate from a pile (518 -> 616, 619, the carry buff, no mount), delivered at a stash (304 -> 336, 339, standing 87); a lost crate picked up again (439)", Crate),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static int Code(EventData e) => Convert.ToInt32(e.Parameters.GetValueOrDefault(ReservedKeys.EventCode));

    private static void Den()
    {
        var store = ResourceNodeTest.Day(Capture002835Key57);
        var den = store.Get("BLACKBANK-0307");
        Check(den.Info.Type == "PLAYERCITY_BLACK_SMUGGLERSDEN" && den.Exits.Count == 2, $"world.xml: BLACKBANK-0307 is a PLAYERCITY_BLACK_SMUGGLERSDEN with two exits to 0307 ({den.Info.Type}, {den.Exits.Count})");
        var kinds = den.Buildings.Select(b => (b.Template.Name, b.Template.Kind)).ToList();
        Check(kinds.Contains(("HIGHLAND_RED_T1_BANK_BLACKBANK", "bankbuilding")) && kinds.Contains(("HIGHLAND_RED_MARKETPLACE_BLACKBANK_B", "smugglermarketbuilding"))
              && kinds.Any(k => k.Name == "HIGHLAND_RED_REPAIRSHOP_BLACKBANK") && kinds.Any(k => k.Name == "HIGHLAND_RED_T8_MELDBUILDING_BLACKBANK")
              && kinds.Contains(("HIGHLAND_RED_SMUGGLER_SPOT_BLACKBANK", "craftbuilding")),
              $"the den's buildings: bank, Smuggler's Market, repair shop, meld building, the trader ({string.Join(", ", kinds.Select(k => k.Name + ":" + k.Kind))})");

        var world = new World(new ServerConfig { JoinByCharacterId = true }, Repository(NewDir()), Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = new ManualClock(), Clusters = store, MapSeed = 5,
        };
        Check(world.Smugglers.MarketLocation("BLACKBANK-0307") == world.Smugglers.MarketLocation("BLACKBANK-2310") && Smugglers.IsMarket(world.Smugglers.MarketLocation("BLACKBANK-0307")),
              "Smugglers:NetworkMarket (official): every den's Smuggler's Market is one book");
        var own = new World(new ServerConfig { JoinByCharacterId = true, Smugglers = new SmugglerOptions { NetworkMarket = false } }, Repository(NewDir()),
            Client.Items, Client.GameData, Client.Spells) { Sync = new object(), Clock = new ManualClock(), Clusters = store };
        Check(own.Smugglers.MarketLocation("BLACKBANK-0307") != own.Smugglers.MarketLocation("BLACKBANK-2310"), "Smugglers:NetworkMarket false: each den its own book");
        Check(world.Economy.SmugglerSetupFee == 0.015, $"gamedata smugglersetupfee 0.015 ({world.Economy.SmugglerSetupFee})");
    }

    private static void Crate()
    {
        var store = ResourceNodeTest.Day(Capture002835Key57);
        var data = SmugglerData.Load(store.Folder);
        var crate = data.Carriable(SmugglerData.Crate);
        Check(crate is { Index: 8, DeliverSeconds: 3 } && crate.Rarities[0].CarryBuff == "SMUGGLERS_CRATE_CARRY_BUFF_1" && data.Pile("SMUGGLER_CRATE_PILE_SMALL") is { Index: 8, StackSize: 1, ProtectionSeconds: 0 }
              && data.Pile("SMUGGLER_CRATE_PILE_LARGE1") is { Index: 0, StackSize: 10, ProtectionSeconds: 300 } && data.DeliverRange == 5
              && data.TileFor("SMUGGLER_CRATE_PILE", "Swamp", "RED") == "SWAMP_RED_SMUGGLER_WAGON"
              && data.Objectives["PVPCHALLENGE_SMUGGLERS_CRATE_0"].Standing["smugglers"] == 3250.123,
              "carriables.xml SMUGGLERS_CRATE is index 8; piledobjects SMALL index 8, LARGE1 index 0; 5 m; the wagon tile; crate 0's 3250.123 standing");

        var characters = Repository(NewDir());
        var clock = new ManualClock();
        var world = new World(new ServerConfig { JoinByCharacterId = true }, characters, Client.Items, Client.GameData, Client.Spells)
        {
            Sync = new object(), Clock = clock, Clusters = store, MapSeed = 5,
        };
        world.MobAi = false;
        void Tick(double seconds)
        {
            clock.Advance(TimeSpan.FromSeconds(seconds));
            lock (world.Sync) world.Update();
        }
        var c = StarterKit.Default.NewCharacter("G" + Guid.NewGuid().ToString("N")[..10]);
        (c.Cluster, c.PositionX, c.PositionY) = ("0307", -300, -150);
        Check(characters.Create(c), "character created");
        var peer = new FakePeer();
        var session = new WorldSession(peer, world);
        lock (world.Sync) session.OnRequest(Request(new JoinRequest { ss = c.Id }, null), encrypted: true);
        var me = session.Player!;
        var map = world.Maps.Find("0307")!;
        for (var i = 0; i < 300 && map.Template is null; i++) { Thread.Sleep(20); Tick(0.01); }
        Check(map.Template is not null, "0307 loaded");
        var at = me.PositionAt(DateTime.UtcNow);
        SmugglerCratePile? pile;
        SmugglerDeliveryStation station;
        lock (world.Sync)
        {
            pile = world.Smugglers.Place(map, "SMUGGLER_CRATE_PILE_SMALL", at + new Vector2(2, 0));
            station = world.Smugglers.Station(map, at + new Vector2(0, 2));
        }
        Check(pile is { State: SmugglerCratePile.Lootable, Crates.Count: 1 }, "a SMALL pile placed: Lootable at once (no protection), one crate");
        Tick(0.1);
        var created = peer.Events.LastOrDefault(e => Code(e) == (int)EventCodes.NewPiledObject)?.Parameters;
        Check(created is not null && Convert.ToInt64(created[0]) == pile!.Id && Convert.ToInt32(created[3]) == 8 && created[5] is string tile && tile.EndsWith("_SMUGGLER_WAGON", StringComparison.Ordinal)
              && peer.Events.Any(e => Code(e) == (int)EventCodes.NewSmugglerCrateDeliveryStation),
              "NewPiledObject {0 id, 3 index 8, 5 the wagon tile} and NewSmugglerCrateDeliveryStation to the player");

        OperationResponse? Send(AlbionRequest req, int id)
        {
            var count = peer.Responses.Count;
            lock (world.Sync) session.OnRequest(Request(req, id), encrypted: false);
            return peer.Responses.Count > count ? peer.Responses[^1] : null;
        }
        var rarity = pile.Crates[0];
        var mark = peer.Events.Count;
        var start = Send(new PickupFromPiledObjectStartRequest { wf = world.GameNow, x4 = pile.Id }, 20);
        Tick(1);
        Check(start is { ReturnCode: 0 } && peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.PickupFromPiledObjectStart) && world.Carriables.Of(me) is null,
              "op 518: rc 0, 616 to the player, nothing carried before the end");
        Tick(2.5);
        var buff = crate!.Rarities[rarity].CarryBuff;
        Check(peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.PickupFromPiledObjectFinished) && world.Carriables.Of(me) is { Rarity: var r } && r == rarity
              && me.Effects.Spells.Any(s => s.Spell.Name == buff) && pile.State == SmugglerCratePile.Empty && Mounts.Check(me, 0) is { } refused && refused.Contains("carrying"),
              $"after 3 s: 619, the crate of rarity {rarity} carried with {buff}, the pile empty, no mount");

        mark = peer.Events.Count;
        var deliver = Send(new DeliverCarriableObjectStartRequest { wf = world.GameNow, jn = station.Id }, 21);
        Tick(3.2);
        var expected = (long)Math.Round(data.Objectives[crate.Rarities[rarity].MightReward!].Standing["smugglers"] * 10000);
        var standing = peer.Events.Skip(mark).LastOrDefault(e => Code(e) == (int)EventCodes.UpdateStanding)?.Parameters;
        Check(deliver is { ReturnCode: 0 } && peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.DeliverCarriableObjectStart)
              && peer.Events.Skip(mark).Any(e => Code(e) == (int)EventCodes.DeliverCarriableObjectFinished) && world.Carriables.Of(me) is null
              && !me.Effects.Spells.Any(s => s.Spell.Name == buff) && me.Character.SmugglerStanding == expected
              && standing is not null && Convert.ToInt32(standing[0]) == Smugglers.StandingType && Convert.ToInt64(standing[1]) == expected,
              $"op 304: rc 0, 336, after 3 s 339, the buff off, smugglers standing {me.Character.SmugglerStanding} = {expected} with UpdateStanding {{0 1}}");

        // A crate lost (as at a death) lies on the ground; op 439 picks it up again.
        CarriableDrop? drop;
        lock (world.Sync)
        {
            world.Carriables.Take(me, crate, 0);
            drop = world.Carriables.Lose(me, "the self-test");
        }
        Tick(0.1);
        Check(drop is not null && world.Carriables.Of(me) is null && peer.Events.Any(e => Code(e) == (int)EventCodes.NewCarriableObject), "a lost crate drops (NewCarriableObject)");
        var pick = Send(new PickupCarriableObjectStartRequest { wf = world.GameNow, x3 = drop!.Id }, 22);
        Tick(0.6);
        Check(pick is { ReturnCode: 0 } && world.Carriables.Of(me) is { Type.Name: SmugglerData.Crate } && !map.Objects.ContainsKey(drop.Id)
              && peer.Events.Any(e => Code(e) == (int)EventCodes.PickupCarriableObjectFinished),
              "op 439: rc 0, after 0.5 s 491, carried again and gone from the ground");
    }
}
