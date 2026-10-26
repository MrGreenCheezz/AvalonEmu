using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Bots;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using static AlbionLocal.SelfTest.AlbionTests;
using Field = AlbionLocal.SelfTest.BotTest.Field;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Bot-travel (audit-bots.md improvement 6; <see cref="BotManager.Travellers"/>, Bots:Travel, Bots:Gatherer:TripMinutes / BankAtHome): a
/// gatherer walking into Thetford's exit to 0204 appears at 0204's matching entrance after Bots:Travel:Seconds as the same identity; a
/// zone Bots:Zones leaves out or Bots:Travel:Enabled false and it logs out there as before; a gatherer of 0204 with its trip over walks
/// home to Thetford and banks at its bank door.
/// </summary>
public static class BotTravelTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("bot travel: a gatherer through Thetford's exit to 0204 is in transit, then at 0204's entrance with the same name, Guid, gear and bag in a free slot", Travels),
        ("bot travel: Bots:Zones without 0204's zone, or Bots:Travel:Enabled false: it logs out at the exit as before; the keys and presets", LogsOut),
        ("bot travel: a 0204 gatherer whose trip is over walks home to Thetford, to the bank door, and its resources are banked there", BanksAtHome),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private const string Neighbour = "0204"; // Thetford's south-west exit leads there (SAFEAREA)

    private static readonly BotSettings OneEach = BotSettings.Default with
    {
        Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>>
        {
            ["0000"] = new Dictionary<BotRole, int> { [BotRole.City] = 0, [BotRole.Gatherer] = 1 }, [Neighbour] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 1 },
        },
    };

    private static string GearOf(Player bot) =>
        string.Join(",", bot.Inventory.Equipment.Items.Where(i => i is not null).Select(i => $"{i!.Template.Name}/{i.Quality}").Order());

    private static BotOptions Options(bool travel = true, IReadOnlyList<string>? zones = null) => new()
    {
        Enabled = true, PopulateSeconds = 0.5, TravelEnabled = travel, TravelSeconds = (3, 3), RespawnSeconds = (600, 600), ArrivalSeconds = (600, 600),
        Zones = zones ?? BotOptions.AllZones,
    };

    /// <summary>Thetford with one gatherer and a player, 0204 loaded with a player of its own; its own gatherer (if any) taken away for 10 min.</summary>
    private static (Player Bot, Map Target, BotExit Exit, Guid Entrance) Setup(Field f)
    {
        var w = f.World;
        var nav = f.Nav();
        var other = w.Bots.Nav.Preload(Client.Clusters!.Get(Neighbour));
        f.Join("Tara", nav.Exits[0].Arrival);
        f.Join("Uli", other.Exits[0].Arrival, edit: c => c.Cluster = Neighbour);
        f.Run(3);
        var target = w.Maps.Find(Neighbour)!;
        lock (w.Sync)
            foreach (var b in target.Bots.Values.ToList()) w.Bots.Despawn(b, "the test frees its slot", BotRefill.Respawn);
        var door = f.Map.Template!.Exits.First(e => e.World?.TargetCluster == Neighbour);
        var exit = nav.Exits.First(x => Vector2.DistanceSquared(x.Position, door.Position) < 0.01f);
        return (f.Map.Bots.Values.Single(b => b.Bot!.Role == BotRole.Gatherer), target, exit, door.World!.Target);
    }

    private static void Travels()
    {
        using var f = new Field(Options(), OneEach);
        var w = f.World;
        var (bot, target, exit, entrance) = Setup(f);
        var (name, guid, gear, id) = (bot.Character.Name, bot.Character.Id, GearOf(bot), bot.Bot!.Identity);
        bot.Bot.Fight.Carried["T1_WOOD"] = 7;
        var source = bot.Bot.Slot!;
        bool left;
        lock (w.Sync) left = w.Bots.LeaveThrough(bot, exit.Position);
        var identity = w.Bots.Roster!.All[id!.Value];
        Check(left && bot.Map is null && w.Bots.Travellers is [{ To: Neighbour } t] && t.Bot == bot && source.Bot is null && source.Identity is null
              && !w.Bots.IdentityFree(identity) && w.Bots.Taken(name) && target.Bots.Count == 0,
              $"through the exit to {Neighbour}: off Thetford, in transit, its Thetford slot free, its identity #{id} held and name taken");
        f.Run(2);
        Check(bot.Map is null && w.Bots.Travellers.Count == 1, "2 s later still in transit (Bots:Travel:Seconds 3)");
        f.Run(1.5);
        var nav = w.Bots.NavOf(target)!;
        var door = target.Template!.Exits.First(e => e.Guid == entrance);
        var arrival = nav.Exits.First(x => Vector2.DistanceSquared(x.Position, door.Position) < 0.01f).Arrival;
        var info = bot.Bot!;
        Check(bot.Map == target && target.Bots.ContainsKey(bot.Id) && w.Bots.Travellers.Count == 0 && bot.Character.Name == name && bot.Character.Id == guid
              && GearOf(bot) == gear && info.Fight.Carried.GetValueOrDefault("T1_WOOD") == 7 && info.Slot is { Role: BotRole.Gatherer } slot && slot.Bot == bot
              && slot.Identity == id && info.PersonaId == slot.PersonaId && bot.Character.Cluster == Neighbour && info.Brain is GathererBrain
              && Vector2.Distance(bot.PositionAt(w.Bots.Time), arrival) < 3,
              $"at 3 s on {Neighbour}: '{name}' with its Guid, gear and 7 wood, in slot {info.PersonaId} holding #{id}, at the entrance ({Vector2.Distance(bot.PositionAt(w.Bots.Time), arrival):0.#} m off)");
    }

    private static void LogsOut()
    {
        foreach (var (options, what) in new[] { (Options(zones: ["City"]), "Bots:Zones [City]"), (Options(travel: false), "Bots:Travel:Enabled false") })
        {
            using var f = new Field(options, OneEach);
            var w = f.World;
            var (bot, target, exit, _) = Setup(f);
            var (name, slot) = (bot.Character.Name, bot.Bot!.Slot!);
            var identity = w.Bots.Roster!.All[bot.Bot.Identity!.Value];
            lock (w.Sync) w.Bots.LeaveThrough(bot, exit.Position);
            f.Run(4);
            Check(bot.Map is null && w.Bots.Travellers.Count == 0 && !target.Bots.Values.Any(b => b.Character.Name == name) && !w.Bots.Taken(name) && w.Bots.IdentityFree(identity)
                  && slot is { Bot: null, ArriveAtExit: true, Name: null }, $"{what}: '{name}' gone at the exit, never in {Neighbour}, its name and identity free, its slot waits for a newcomer");
        }
        var file = Path.Combine(NewDir(), "albionlocal.json");
        File.WriteAllText(file, """{ "Bots": { "Travel": { "Seconds": "5-8", "MaxHops": 3 }, "Gatherer": { "TripMinutes": 12, "BankAtHome": false } } }""");
        var c = LoadTestConfig(file, ["--Preset=pve"]).Bots;
        var official = LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=official"]).Bots;
        static bool Refused(string file, string arg)
        {
            try { LoadTestConfig(file, [arg]); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check(c is { TravelEnabled: true, TravelSeconds: (5, 8), TravelMaxHops: 3, GathererTripMinutes: 12, GathererBankAtHome: false, Enabled: true }
              && official is { Enabled: false, TravelEnabled: true, TravelSeconds: (10, 20), TravelMaxHops: 6, GathererTripMinutes: 30, GathererBankAtHome: true }
              && ServerConfig.PveDefaults.Bots is { Enabled: true, TravelEnabled: true } && Refused(file, "--Bots:Travel:MaxHops=0") && Refused(file, "--Bots:Gatherer:TripMinutes=0"),
              "keys from the file; official: travel on under a master that is off (so none), pve on; MaxHops 0 and TripMinutes 0 refused");
    }

    private static void BanksAtHome()
    {
        var settings = BotSettings.Default with
        {
            Override = new Dictionary<string, IReadOnlyDictionary<BotRole, int>>
            {
                ["0000"] = new Dictionary<BotRole, int> { [BotRole.City] = 1 }, [Neighbour] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 1 },
            },
        };
        using var f = new Field(new BotOptions { Enabled = true, PopulateSeconds = 0.5, TravelSeconds = (1, 1), ThinkSeconds = (0.5, 0.5), ActiveRange = 10000 }, settings,
            cluster: Neighbour);
        var w = f.World;
        var nav = f.Nav();
        var city = w.Bots.Nav.Preload(Client.Clusters!.Get("0000"));
        f.Join("Vera", nav.Exits[0].Arrival);
        f.Join("Wim", city.Exits[0].Arrival, edit: c => c.Cluster = "0000");
        f.Run(3);
        var bot = f.Map.Bots.Values.Single(b => b.Bot!.Role == BotRole.Gatherer);
        var info = bot.Bot!;
        Check(info.HomeCity == "0000" && w.Bots.NextHop(Neighbour, "0000") == "0000", $"the gatherer of {Neighbour} has its home city {info.HomeCity}, one hop away");
        lock (w.Sync)
        {
            info.Fight.Carried.Clear();
            info.Fight.Carried["T2_FIBER"] = 12;
            info.TripUntil = w.Clock.Now; // the trip's time is over
        }
        var bank = city.Pois.Where(p => p.Building == "bank").ToList();
        var (inCity, banked, visitor) = (false, false, false);
        var at = Vector2.Zero;
        for (var s = 0; s < 400 && !banked; s++)
            f.Run(1, () =>
            {
                if (banked || bot.Map?.Cluster != "0000") return;
                inCity = true;
                visitor |= info.Slot is null && info.PersonaId.Contains("visitor", StringComparison.Ordinal);
                if (info.Fight.CarriedTotal == 0) (banked, at) = (true, bot.PositionAt(w.Bots.Time));
            });
        Check(bank.Count > 0 && inCity && visitor && banked && bank.Any(p => Vector2.Distance(p.At, at) < 3) && !info.Homing && info.HomeCity == "0000"
              && info.TripUntil > w.Clock.Now,
              $"walked home to Thetford ({inCity}, as a visitor {visitor}) and banked its 12 fiber at the bank door ({(bank.Count == 0 ? "no bank" : $"{bank.Min(p => Vector2.Distance(p.At, at)):0.#} m")}); a new trip");
    }
}
