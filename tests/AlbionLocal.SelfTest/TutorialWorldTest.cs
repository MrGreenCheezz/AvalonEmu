using System.Numerics;
using System.Text.Json;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

public static class TutorialWorldTest
{
    private static void Check(bool condition, string message) => AlbionTests.Check(condition, message);
    public static IEnumerable<(string, Action)> All =>
    [
        ("tutorial world: private opening leads to shared island maps, with revisit and reconnect", MapTravel),
        ("tutorial world: deferred map resolution restores obstacles before visibility and preserves transfer identity", DeferredTravel),
        ("tutorial world: shared tutorial merchant shows one renewable item, charges and fetches once", Market),
    ];
    private sealed class Lab : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly SqliteCharacterRepository Repository;
        public readonly World World;
        public readonly OpeningTutorialData Data = OpeningTutorialData.Load(ClientGameDataDir(), Client.Clusters!);
        public Lab(Func<string, Task<ClusterTemplate>>? load = null)
        {
            Repository = new SqliteCharacterRepository(Path.Combine(Dir, "save.db"));
            World = new World(new ServerConfig { JoinByCharacterId = true, TutorialOpening = true,
                Transfer = new() { CheckExitCooldown = false } }, Repository, Client.Items, Client.GameData, Client.Spells)
            { Sync = new object(), Clusters = Client.Clusters, OpeningTutorial = Data, LoadCluster = load };
        }
        public (WorldSession Session, FakePeer Peer, Player Player) Create(string name, string cluster)
        {
            var c = Data.Create(name, Client.Items, Client.GameData!);
            c.Cluster = cluster; c.Tutorial = c.Tutorial! with { IntroStarted = true };
            if (cluster != "1008")
            {
                var pos = Transfers.DefaultSpawn(Client.Clusters!.Get(cluster)); c.PositionX = pos.X; c.PositionY = pos.Y;
            }
            Check(Repository.Create(c), "new private tutorial character created");
            return Join(c.Id);
        }
        public (WorldSession Session, FakePeer Peer, Player Player) Join(Guid id)
        {
            var peer = new FakePeer(); var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            session.OnRequest(Request(new JoinRequest { ss = id }, 1), false);
            Check(SpinWait.SpinUntil(() => { World.Update(); return session.Player?.Map?.Template is not null; }, 5000), "joined map resolves");
            return (session, peer, session.Player!);
        }
        public void Dispose() { World.Dispose(); Repository.Dispose(); Directory.Delete(Dir, true); }
    }
    private static void DeferredTravel()
    {
        var pending = new TaskCompletionSource<ClusterTemplate>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lab = new Lab(id => id == "1011" ? pending.Task : Task.FromResult(Client.Clusters!.Get(id)));
        var (session, peer, player) = lab.Create("DeferredIslandTraveller", "1008");
        var exit = player.Map!.Objects.Values.OfType<Exit>().Single();
        player.Teleport(exit.Position, 0, DateTime.UtcNow);
        var responses = peer.Responses.Count;
        session.OnRequest(Request(new ChangeClusterRequest { ee = exit.Id }, 30), false);
        Check(peer.Responses.Count == responses && player.Map is not null, "a pending template cannot detach or answer the character early");
        pending.SetResult(Client.Clusters!.Get("1011"));
        Check(SpinWait.SpinUntil(() => { lab.World.Update(); return session.Status == SessionStatus.Transferring; }, 5000), "deferred change commits when its data arrives");
        Check(Equals(peer.Responses.Last().Parameters[0], "1011"), "deferred response selects the shared island identity");
        player.Character.Tutorial = player.Character.Tutorial! with { JourneyBlockers = "{\"BLOCKNPCS\":0,\"FIXSHIP\":0}" };
        lab.World.Saves.SaveNow(player.Character);
        var joined = lab.Join(player.Character.Id);
        var restored = joined.Player.Map!.Objects.Values.OfType<TutorialBlocker>().Where(b => b.BlockerId is "BLOCKNPCS" or "FIXSHIP").ToArray();
        Check(restored.Length > 0, "authored shipyard obstacles are present in the resolved map");
        foreach (var obstacle in restored)
            Check(!TutorialWorld.BlockedFor(joined.Player, obstacle), "late map population restores the viewer's saved obstacle state");
        Check(joined.Player.Map.InstanceKey == "1011", "asynchronous rejoin reaches the shared island");
    }
    private static void MapTravel()
    {
        using var lab = new Lab();
        var (session, peer, player) = lab.Create("IslandTraveller", "1008");
        var character = player.Character; var firstMap = player.Map!;
        foreach (var destination in new[] { "1011", "1009", "1011", "1010", "1011", "1008" })
        {
            var from = player.Map!;
            var exit = from.Objects.Values.OfType<Exit>().Single(e => e.World.TargetCluster == destination);
            player.Teleport(exit.Position, 0, DateTime.UtcNow);
            session.OnRequest(Request(new ChangeClusterRequest { ee = exit.Id }, 20), false);
            var response = peer.Responses.Last();
            var identity = destination == "1008" ? TutorialInstances.Key(destination, character.Id) : destination;
            Check(response.ReturnCode == 0 && Equals(response.Parameters[0], identity)
                && (destination != "1008" || Equals(response.Parameters[1], destination)), "transfer response selects the private opening or shared island");
            (session, peer, player) = lab.Join(character.Id);
            Check(player.Map!.Cluster == destination && player.Map.TutorialOwner == (destination == "1008" ? character.Id : (Guid?)null)
                && player.Map.InstanceKey == TutorialInstances.ClientCluster(player.Character, lab.World), "new Join uses the selected map identity");
            Check(player.Map != from, "a different logical map cannot collide with the previous private instance");
            foreach (var npc in player.Map.Objects.Values.OfType<TutorialQuestGiver>())
            {
                var definition = lab.Data.Journey!.QuestGivers.Single(n => n.Cluster == destination && n.Reference == npc.Reference);
                Check(npc.ObjectType == definition.Type
                    && npc.QuestGiverType == Client.Clusters!.QuestGiverTypes.IdOf(npc.Reference),
                    "NPC object and dialogue type use their distinct authored catalog indices");
            }
        }
        Check(ReferenceEquals(player.Map, firstMap), "revisit recovers the original private scenery instead of replacing it");
        var other = lab.Create("IslandNeighbour", "1008").Player;
        Check(other.Map != firstMap && !player.Observers.Contains(other), "another character has independent scenery and visibility");
        Check(lab.World.Maps.Maps.Count(m => m.TutorialOwner == character.Id) == 1, "only the opening remains private");
        var island = lab.Create("SharedTraveller", "1011").Player;
        var neighbour = lab.Create("SharedNeighbour", "1011").Player;
        Check(ReferenceEquals(island.Map, neighbour.Map), "characters share the captain's island map");
        neighbour.Teleport(island.PositionAt(DateTime.UtcNow), 0, DateTime.UtcNow);
        island.Visibility.Refresh(island.PositionAt(DateTime.UtcNow));
        neighbour.Visibility.Refresh(neighbour.PositionAt(DateTime.UtcNow));
        Check(island.Observers.Contains(neighbour) && neighbour.Observers.Contains(island), "nearby island players see one another");
        var blocker = island.Map!.Objects.Values.OfType<TutorialBlocker>().First(b => b.BlockerId == "FIXSHIP");
        island.Character.Tutorial = island.Character.Tutorial! with { JourneyBlockers = "{\"FIXSHIP\":0}" };
        neighbour.Character.Tutorial = neighbour.Character.Tutorial! with { JourneyBlockers = "{\"FIXSHIP\":1}" };
        Check(!blocker.State(island).dcq && blocker.State(neighbour).dcq, "shared scenery retains per-player quest obstacles");
    }
    private static void Market()
    {
        using var lab = new Lab();
        var (session, peer, player) = lab.Create("IslandBuyer", "1011");
        var market = player.Map!.Objects.Values.OfType<Building>().Single(b => b.Template.Kind == "marketplacebuilding");
        player.Teleport(market.Position, 0, DateTime.UtcNow);
        player.Visibility.Refresh(market.Position);
        var mule = Client.Items.Find("T2_MOUNT_MULE")!;
        var orderId = TutorialMerchant.FirstId + mule.Index;
        Check(TutorialMerchant.Order(player, orderId, player.Map.InstanceKey)?.Amount == 1, "NPC stock displays one item");
        void Buy() => session.OnRequest(Request(new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 1, bn3 = orderId }, 21), false);
        Buy();
        Check(peer.Responses.Last().ReturnCode != 0 && lab.World.Market!.FinishedOf(player.Character.Id).Count == 0,
            "insufficient money creates neither an item nor a finished order");
        player.Character.Silver = TutorialMerchant.LaboratoryMulePrice;
        Buy();
        Check(peer.Responses.Last().ReturnCode == 0 && player.Character.Silver == 0
            && !player.Inventory.Bag.Items.Any(i => i.Template == mule)
            && lab.World.Market!.FinishedOf(player.Character.Id).Single().ItemName == mule.Name,
            "successful purchase charges exactly once and requires actual collection");
        Check(TutorialMerchant.Order(player, orderId, player.Map.InstanceKey)?.Amount == 1, "a purchased NPC offer remains available");
        session.OnRequest(Request(new AuctionGetFinishedAuctionsRequest { bqs = market.Id }, 22), false);
        var pendingDtos = (string[])peer.Responses.Last().Parameters[0]!;
        using var pendingDto = JsonDocument.Parse(pendingDtos.Single());
        var dto = pendingDto.RootElement;
        // MarketPlaceCompleteView.CompleteEntry checks these fields before it sends op 91.
        Check(dto.GetProperty("BuyerCharacterId").GetString() == player.Character.Id.ToString()
            && dto.GetProperty("SellerCharacterId").GetString() != player.Character.Id.ToString()
            && !dto.GetProperty("HasBuyerFetched").GetBoolean(), "client can identify this purchase as its own uncollected item");
        var finishedId = dto.GetProperty("Id").GetInt64();
        session.OnRequest(Request(new AuctionFetchAuctionRequest { bqs = market.Id, boe = finishedId, bof = 1 }, 23), false);
        Check(peer.Responses.Last().ReturnCode == 0 && player.Inventory.Bag.Items.Count(i => i.Template == mule) == 1,
            "individual fetch moves the purchased mule into the inventory");
        session.OnRequest(Request(new AuctionFetchAuctionRequest { bqs = market.Id, boe = finishedId, bof = 1 }, 24), false);
        Check(peer.Responses.Last().ReturnCode != 0 && player.Inventory.Bag.Items.Count(i => i.Template == mule) == 1,
            "repeated individual collection cannot duplicate the purchase");
        session.OnRequest(Request(new AuctionFetchFinishedAuctionsRequest(), 23), false);
        Check(player.Inventory.Bag.Items.Count(i => i.Template == mule) == 1 && lab.World.Market!.FinishedOf(player.Character.Id).Count == 0,
            "repeated collection cannot duplicate the purchase");
        player.Character.Silver = 300 * 10_000L;
        foreach (var name in new[] { "T2_HEAD_PLATE_SET1", "T2_ARMOR_PLATE_SET1", "T2_SHOES_PLATE_SET1", "T2_OFF_SHIELD" })
        {
            var type = Client.Items.Find(name)!;
            session.OnRequest(Request(new AuctionBuyOfferRequest { bqs = market.Id, bn2 = 1, bn3 = TutorialMerchant.FirstId + type.Index }, 24), false);
            Check(peer.Responses.Last().ReturnCode == 0, "the local reward budget buys each required armor and offhand piece");
        }
        session.OnRequest(Request(new AuctionFetchFinishedAuctionsRequest(), 25), false);
        Check(player.Character.Silver == 0 && player.Inventory.Bag.Items.Any(i => i.Template.Name == "T2_OFF_SHIELD"),
            "four actual purchases and collection fit the 300-silver tutorial reward");
        Check(TutorialMerchant.Orders(player, player.Map.InstanceKey, MarketOrder.Offer).Any(o => o.ItemName == "T2_OFF_BOOK"),
            "firestaff branch also has its authored offhand book available");
        Check(!TutorialMerchant.Orders(player, "0000", MarketOrder.Offer).Any(), "normal cities receive no tutorial offers");
        var other = lab.Create("PrivateOtherBuyer", "1011").Player;
        Check(other.Map == player.Map && TutorialMerchant.Orders(other, player.Map.InstanceKey, MarketOrder.Offer).Any(),
            "both players can access the renewable merchant on the shared island");
    }
}
