using System.Globalization;
using System.Numerics;
using AlbionLocal.Photon;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// Fishing (analysis/work/stageG/fishing/SPEC.md with its verification log): the rules and the server's half of the session
/// state machine (§4); the requests are the session's (Handlers/World/FishingHandler.cs). The client decides where the cast
/// lands, whether that is water and the minigame's outcome (it simulates the minigame itself and reports FishingFinish); the
/// server decides the float, the bite and its window, the catch, the zone charges, fame and wear. The flow: FishingStart op →
/// state Casting (3); FishingCast op → Cast (4) and the float after its flight (or GroundFail 11 on ground); the bite (5) after
/// the zone's bite delay, its window <see cref="FishingSettings.CatchTimeout"/>; the strike (FishingCatch false) rolls the catch,
/// takes the zone's charge, hooks the float and sends the fisher FishingMiniGame (state 6, never as FishingStart); Pull (7) /
/// GiveLine (8) as the client reports them; FishingFinish → Success (9) or Fail (10); the client's FishingCancel op (or the
/// server's fallback timer) ends it with the FishingCancel event and the float's Leave. Every FishingStart event, the float and
/// the FishingCancel go to the fisher and everyone who sees it (§10); the minigame, items and fame to the fisher only.
/// The numbers the client data does not hold are content/fishing.json's (<see cref="FishingSettings"/>, [assume] where
/// marked there). Bait (econ SPEC §2.2): the FISHINGBAIT_* buff's fishingspeed shortens the bite delay as the rod's does, and
/// each strike takes one of its 10 charges (FISHINGBAIT_CONSUME_CHARGE). Not done: the black-market list, the rare share of the
/// fish spots, the fishingspeed of gear other than the rod and the bait. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Fishing
{
    /// <summary>items.xml fishing="true": a fishing rod (T3_2H_TOOL_FISHINGROD … T8).</summary>
    public static bool IsRod(ItemTemplate t) => t.Attrs.GetValueOrDefault("fishing") == "true";

    /// <summary>items.xml fishingspeed of a rod (0.05 on every one): divides the bite delay [assume formula, SPEC §7.3].</summary>
    public static double Speed(ItemTemplate rod) =>
        double.TryParse(rod.Attrs.GetValueOrDefault("fishingspeed"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : 0;

    /// <summary>items.xml fishingfame of a catch (T4 common 40, rares 100-300, the boss shark 2000); 0 = none (trash, seaweed).</summary>
    public static double Fame(ItemTemplate item) =>
        double.TryParse(item.Attrs.GetValueOrDefault("fishingfame"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>The seconds of the float's flight for a cast of <paramref name="power"/> (0.44 + 0.30 × power by default, SPEC §6).</summary>
    public static double Flight(FishingSettings s, float power) => s.FlightBase + s.FlightPerPower * Math.Clamp(power, 0, 1);

    /// <summary>
    /// The bite delay: uniform(BiteMin, BiteMax) / (1 + the rod's fishingspeed + <paramref name="bait"/>, the fishingspeed buff of a
    /// bait: 0.5 / 1.25 / 2.5) [assume, SPEC §6; econ SPEC §2.2].
    /// </summary>
    public static TimeSpan BiteDelay(FishingZoneSettings zone, ItemTemplate rod, Random random, double bait = 0) =>
        TimeSpan.FromSeconds((zone.BiteMin + random.NextDouble() * Math.Max(0, zone.BiteMax - zone.BiteMin)) / (1 + Speed(rod) + Math.Max(0, bait)));

    /// <summary>The spell category of a bait's charges (FISHINGBAIT_LEVELn_CHARGE, startcharges 10) and of its buff (FISHINGBAIT_LEVELn_USE).</summary>
    public const string BaitCharges = "fishingbait_charges", BaitBuffs = "fishingbait_buffs";

    /// <summary>
    /// FISHINGBAIT_CONSUME_CHARGE at a strike (econ SPEC §2.2): one charge of the bait goes; with the last one the bait's buff goes too
    /// (its removeactiveeffects fishingbait_buffs under "not IfSpellCategoryActive fishingbait_charges"). The list goes out when it changed.
    /// </summary>
    public static void ConsumeBait(Player p)
    {
        var world = p.World;
        if (!SpellEffects.Remove(world, p, a => a.Spell.Category == BaitCharges, charges: 1)) return;
        if (!p.Effects.Spells.Any(a => a.Spell.Category == BaitCharges)) SpellEffects.Remove(world, p, a => a.Spell.Category == BaitBuffs, charges: 1);
        SpellEffects.Send(world, p);
        SpellEffects.Refresh(world, p);
    }

    /// <summary>The settings of the water at the landing point: the zone's kind, or the default zone of the water type.</summary>
    public static FishingZoneSettings SettingsOf(World world, FishingSession s) => s.Zone?.Settings ?? (s.Ocean ? world.Fishing.Ocean : world.Fishing.Freshwater);

    /// <summary>
    /// The zone at <paramref name="at"/> (SPEC §7.1): of the zones that contain it and have charges, the highest priority, then
    /// the nearest; an empty spot is skipped, so its enclosing base zone pays (7 of 10 captured strikes). None: the default
    /// zone, ocean when the nearest zone within <see cref="FishingSettings.OceanNearby"/> is an OCEAN_ one [assume].
    /// </summary>
    public static (FishingZone? Zone, bool Ocean) Pick(World world, Map map, Vector2 at)
    {
        FishingZone? best = null;
        foreach (var z in map.FishingZones)
        {
            if (z.Charges <= 0 || !z.Contains(at)) continue;
            if (best is null || z.Settings.Priority > best.Settings.Priority
                || z.Settings.Priority == best.Settings.Priority && Vector2.Distance(z.Position, at) < Vector2.Distance(best.Position, at))
                best = z;
        }
        if (best is not null) return (best, best.IsOcean);
        var near = map.FishingZones.MinBy(z => Vector2.Distance(z.Position, at));
        return (null, near is not null && near.IsOcean && Vector2.Distance(near.Position, at) <= world.Fishing.OceanNearby);
    }

    /// <summary>
    /// The base loot list of the cluster (SPEC §7.2): cities FISHING_ZONE_FRESHWATER_BASE_&lt;BIOME&gt;CITY_LOOT /
    /// FISHING_ZONE_OCEAN_BASE_CITY_LOOT (Thetford, Swamp: SWAMPCITY), islands and hideouts their untiered lists, dungeons
    /// T&lt;t&gt;_…_BASE_DUNGEON_LOOT, the open world T&lt;t&gt;_FISHING_ZONE_FRESHWATER_BASE_&lt;BIOME&gt;_LOOT (Highland HIGHLANDS, Roads
    /// AVALON, Dragon DRAGON_AREA) and T&lt;t&gt;_FISHING_ZONE_OCEAN_BASE_ALL_LOOT. Tier = the cluster's (1-8).
    /// </summary>
    public static string BaseList(ClusterName name, bool ocean)
    {
        var t = Math.Clamp(name.Tier ?? 1, 1, 8);
        var water = ocean ? "OCEAN" : "FRESHWATER";
        var biome = name.Biome?.ToUpperInvariant();
        return name.Type?.ToUpperInvariant() switch
        {
            "CTY" when ocean => "FISHING_ZONE_OCEAN_BASE_CITY_LOOT",
            "CTY" => $"FISHING_ZONE_FRESHWATER_BASE_{biome}CITY_LOOT",
            "ISL" => $"FISHING_ZONE_{water}_BASE_ISLAND_LOOT",
            "HDO" => "FISHING_ZONE_FRESHWATER_BASE_HIDEOUT_LOOT",
            "DNG" or "EXP" or "HEL" or "COR" => $"T{t}_FISHING_ZONE_{water}_BASE_DUNGEON_LOOT",
            _ when ocean => $"T{t}_FISHING_ZONE_OCEAN_BASE_ALL_LOOT",
            "RDS" => $"T{t}_FISHING_ZONE_FRESHWATER_BASE_AVALON_LOOT",
            _ => $"T{t}_FISHING_ZONE_FRESHWATER_BASE_{biome switch { "HIGHLAND" => "HIGHLANDS", "ROADS" => "AVALON", "DRAGON" => "DRAGON_AREA", _ => biome }}_LOOT",
        };
    }

    /// <summary>
    /// The loot list of a catch in <paramref name="zone"/>'s water: BASE the cluster's (<see cref="BaseList"/>), TREASURE
    /// T&lt;t&gt;_FISHING_ZONE_TREASURE, DEFAULT or a list loot.xml does not have T&lt;t&gt;_FISHING_ZONE_DEFAULT_&lt;water&gt;_LOOT (then the
    /// untiered one), any other name as given.
    /// </summary>
    public static string ListOf(ClusterName name, FishingZoneSettings zone, bool ocean, LootTables tables)
    {
        var t = Math.Clamp(name.Tier ?? 1, 1, 8);
        var water = ocean ? "OCEAN" : "FRESHWATER";
        var list = zone.Loot.ToUpperInvariant() switch
        {
            "BASE" => BaseList(name, ocean),
            "TREASURE" => $"T{t}_FISHING_ZONE_TREASURE",
            "DEFAULT" => "",
            _ => zone.Loot,
        };
        if (tables.Has(list)) return list;
        return tables.Has($"T{t}_FISHING_ZONE_DEFAULT_{water}_LOOT") ? $"T{t}_FISHING_ZONE_DEFAULT_{water}_LOOT" : $"FISHING_ZONE_DEFAULT_{water}_LOOT";
    }

    /// <summary>
    /// The catch of a strike (§7.2: rolled at the strike, given on a success): the list's roll, the items this server can create
    /// (a list's furniture or journals cannot be sent yet), rolled again up to four times when nothing is left, else
    /// FISHING_TRASH. Empty = nothing at all: no loot data, or loot.xml not read yet (never read here, under the world lock; a
    /// cluster with fishing zones reads it when it is resolved, as one with mobs).
    /// </summary>
    public static List<(ItemTemplate Item, int Amount)> Roll(World world, Map map, FishingSession s)
    {
        if (world.Clusters?.LootIfLoaded is not { } tables || map.Template is not { } template) return [];
        var list = ListOf(template.Name, SettingsOf(world, s), s.Ocean, tables);
        for (var i = 0; i < 6; i++)
        {
            var items = tables.RollList(i < 5 ? list : "FISHING_TRASH", map.Random)
                .Select(d => (Item: world.Items.Find(d.Item), d.Amount))
                .Where(d => d.Item is { CanCreate: true } && d.Amount > 0)
                .Select(d => (d.Item!, d.Amount)).ToList();
            if (items.Count > 0) return items;
        }
        return [];
    }

    /// <summary>Whether the session's rod is still in <paramref name="p"/>'s equipment or inventory (not dropped, banked, sold, traded or destroyed since FishingStart).</summary>
    public static bool HasRod(Player p, FishingSession s) => p.Inventory.FindItem(s.Rod.Id) == s.Rod;

    /// <summary>The minigame setting of a catch: items.xml fishingminigamesetting of its first item that has one, else DEFAULT (bdi.li).</summary>
    public static string? SettingName(IEnumerable<(ItemTemplate Item, int Amount)> catches) =>
        catches.Select(c => c.Item.Attrs.GetValueOrDefault("fishingminigamesetting")).FirstOrDefault(n => n is { Length: > 0 });

    // ---- events ----

    /// <summary>
    /// FishingStart (354, ao2) {0 fisher, 1 rod object id, 2 rod item type (observers draw the rod by it), 3 the state, 4 the session
    /// stamp, the bite's time for Bitten} to the fisher and its observers; the state is set first.
    /// </summary>
    public static void Send(Player p, FishingSession s, FishingState state)
    {
        s.State = state;
        p.SendAround(StartEvent(p, s));
    }

    public static FishingStartEvent StartEvent(Player p, FishingSession s) => new()
    {
        ddn = p.Id,                                                        // [0]
        cax = s.Rod.Id,                                                    // [1]
        cay = s.Rod.Template.Index,                                        // [2] 3015 for T3_2H_TOOL_FISHINGROD
        caz = (int)s.State,                                                // [3]
        ca0 = s.State == FishingState.Bitten ? s.BiteStamp : s.Stamp,      // [4]
    };

    /// <summary>
    /// FishingMiniGame (361, ao1) {0 fisher, 1 6 (MiniGame), 2 the strike's time, 3-15 the setting as Single; key 14 is 0 in the
    /// captured one and so left out}. The client builds its minigame from keys 3-15; observers ignore it.
    /// </summary>
    public static FishingMiniGameEvent MiniGameEvent(Player p, FishingSession s)
    {
        var v = s.MiniGame ?? FishingSettings.CapturedMiniGame;
        return new()
        {
            ddn = p.Id, cai = (int)FishingState.MiniGame, caj = s.MiniGameStart,
            cak = v[0], cal = v[1], cam = v[2], can = v[3], cao = v[4], cap = v[5], caq = v[6], car = v[7], cas = v[8], cat = v[9], cau = v[10], cav = v[11], caw = v[12],
        };
    }

    /// <summary>
    /// What a player who gets <paramref name="fisher"/> in view mid-session needs after its NewCharacter (§10): FishingStart of the
    /// current state, except right after the strike (state MiniGame, no Pull/GiveLine yet), where the official server sends the
    /// whole FishingMiniGame (captured once, 0.02 s after the NewCharacter). The float comes by its own visibility. Null = no session.
    /// </summary>
    public static AlbionEvent? ForNewObserver(Player fisher) => fisher.Fishing switch
    {
        null => null,
        { State: FishingState.MiniGame } s => MiniGameEvent(fisher, s),
        { } s => StartEvent(fisher, s),
    };

    /// <summary>The float's state, to everyone who knows it.</summary>
    public static void SetFloat(FishingFloat f, FloatState state)
    {
        f.State = state;
        f.Map?.SendToObservers(f, f.Changed());
    }

    /// <summary>
    /// Ends <paramref name="p"/>'s session: FishingCancel (358, aox) {0 fisher, 1 the state it ended in, 2 1 only for a missed bite}
    /// to the fisher (its client leaves the fishing action) and its observers, then the float's Leave (captured together).
    /// </summary>
    public static void End(Player p, string why, bool missed = false, FishingState? state = null)
    {
        if (p.Fishing is not { } s) return;
        p.Fishing = null;
        var ended = state ?? s.State;
        p.SendAround(new FishingCancelEvent { ddn = p.Id, caf = (int)ended, cag = missed ? 1 : 0 });
        if (s.Float is { Map: { } map } f) map.RemoveObject(f);
        Log.Info("fishing", $"'{p.Character.Name}' fishing ended in {ended}: {why}");
    }

    // ---- the server's timers (map timers, at World.Clock times) ----

    /// <summary>Runs <paramref name="action"/> after <paramref name="delay"/> while <paramref name="s"/> is still <paramref name="p"/>'s session on its map.</summary>
    public static void After(Player p, FishingSession s, TimeSpan delay, Action action)
    {
        if (p.Map is not { } map) return;
        map.Timers.At(p.World.Clock.Now + delay, () =>
        {
            if (p.Fishing == s && p.Map == map) action();
        });
    }

    /// <summary>
    /// The client casts by itself once the charge is full (power 1 after 1 s, cuw); a session still in Casting
    /// <see cref="FishingSettings.EndFallback"/> after that got no FishingCast and ends [assume].
    /// </summary>
    public static void AwaitCast(Player p, FishingSession s) =>
        After(p, s, TimeSpan.FromSeconds(1 + p.World.Fishing.EndFallback), () =>
        {
            if (s.State == FishingState.Casting) End(p, "no FishingCast from the client (fallback)");
        });

    /// <summary>
    /// The cast is out (state Cast, or GroundFail: the captured float of a ground cast came 0.41 s after S11): after the flight
    /// the float splashes (NewFloatObject state 1); in water the bite delay starts from there.
    /// </summary>
    public static void Launch(Player p, FishingSession s)
    {
        var world = p.World;
        After(p, s, TimeSpan.FromSeconds(Flight(world.Fishing, s.Power)), () =>
        {
            var map = p.Map!;
            var spawn = new FloatSpawn { Key = new SpawnKey(map.Cluster, "float", 0), Tile = "", Position = s.Target, Component = new System.Xml.Linq.XElement("float") };
            s.Float = new FishingFloat(world.ObjectIds.Next(), spawn, p.Id, p.Facing);
            map.AddObject(s.Float);
            if (s.State != FishingState.Cast) return;
            var delay = BiteDelay(SettingsOf(world, s), s.Rod.Template, map.Random, p.Effects.Sum("fishingspeed", world.GameNow));
            After(p, s, delay, () => Bite(p, s));
        });
    }

    /// <summary>
    /// The bite (state Cast still): the zone's chancetohit, then FishingStart 5 with the bite's time, the float bitten (2), and
    /// the miss after <see cref="FishingSettings.CatchTimeout"/> + <see cref="FishingSettings.StrikeSlack"/> (FishingCancel {1 5,
    /// 2 1}; captured 0.80-0.82 s, strikes accepted up to 0.87 s, so the slack for the round trip comes first). A failed
    /// chancetohit ends it as caught nothing (13, only as FishingCancel; never captured) [assume].
    /// </summary>
    private static void Bite(Player p, FishingSession s)
    {
        if (s.State != FishingState.Cast) return;
        var world = p.World;
        if (p.Map!.Random.NextDouble() >= SettingsOf(world, s).ChanceToHit)
        {
            End(p, "no bite (chancetohit)", state: FishingState.CaughtNothing);
            return;
        }
        s.BiteStamp = world.GameNow;
        Send(p, s, FishingState.Bitten);
        if (s.Float is { } f) SetFloat(f, FloatState.Bitten);
        var bite = s.BiteStamp;
        After(p, s, TimeSpan.FromSeconds(world.Fishing.CatchTimeout + world.Fishing.StrikeSlack), () =>
        {
            if (s.State == FishingState.Bitten && s.BiteStamp == bite) End(p, "the bite was missed", missed: true);
        });
    }

    /// <summary>
    /// The strike (FishingCatch in state Bitten, within the window): the catch is rolled (it names the minigame setting), the zone the
    /// landing point picks now pays a charge (NewFishingZoneObject to its viewers, the same instant as the hooked float), the
    /// float is hooked (3), the fisher gets FishingMiniGame, the state is MiniGame; a minigame without FishingFinish fails after
    /// <see cref="FishingSettings.MiniGameTimeout"/>. False = ended, no charge taken: the rod gone (ToolFail 12, the client's
    /// notice), nothing to catch (no loot data: caught nothing) or a catch that does not fit the inventory (FishingCancel of
    /// Bitten, as a harvest that does not fit is cancelled before it takes anything [assume]).
    /// </summary>
    public static bool Strike(Player p, FishingSession s)
    {
        var world = p.World;
        var map = p.Map!;
        if (!HasRod(p, s))
        {
            End(p, "the rod has left the inventory", state: FishingState.ToolFail);
            return false;
        }
        if (s.Zone is { Charges: <= 0 }) (s.Zone, s.Ocean) = Pick(world, map, s.Target); // emptied by someone since the cast
        s.Catch = Roll(world, map, s);
        if (s.Catch.Count == 0)
        {
            End(p, "nothing to catch (no loot data)", state: FishingState.CaughtNothing);
            return false;
        }
        if (!Network.WorldSession.Fits(p.Inventory.Bag, s.Catch))
        {
            End(p, $"{string.Join(", ", s.Catch.Select(c => $"{c.Amount} x {c.Item.Name}"))} do not fit the inventory");
            return false;
        }
        s.MiniGame = world.Fishing.MiniGame(SettingName(s.Catch));
        if (s.Zone is { } zone) TakeCharge(map, zone, world.Clock.Now, world.Rates.Regrowth);
        ConsumeBait(p);
        if (s.Float is { } f) SetFloat(f, FloatState.Hooked);
        s.State = FishingState.MiniGame;
        s.MiniGameStart = world.GameNow;
        p.Session.Send(MiniGameEvent(p, s));
        var start = s.MiniGameStart;
        After(p, s, TimeSpan.FromSeconds(world.Fishing.MiniGameTimeout), () =>
        {
            if (s.MiniGameStart == start && s.State is FishingState.MiniGame or FishingState.Pull or FishingState.GiveLine) Lose(p, s, "the minigame timed out");
        });
        Log.Info("fishing", $"'{p.Character.Name}' hooked {string.Join(", ", s.Catch.Select(c => $"{c.Amount} x {c.Item.Name}"))}"
            + $" in {s.Zone?.ToString() ?? (s.Ocean ? "the default ocean" : "the default freshwater")}");
        return true;
    }

    /// <summary>The minigame lost (FishingFinish false, too early, or timed out): FishingStart 10, the float escapes (5), the catch is gone, the charge stays used [assume].</summary>
    public static void Lose(Player p, FishingSession s, string why)
    {
        s.Catch = [];
        Send(p, s, FishingState.Fail);
        if (s.Float is { } f) SetFloat(f, FloatState.Escaped);
        Log.Info("fishing", $"'{p.Character.Name}' lost the fish: {why}");
        EndLater(p, s);
    }

    /// <summary>After Success, Fail or GroundFail the client sends FishingCancel itself (1.8 s / 1 s captured); the server ends it after <see cref="FishingSettings.EndFallback"/> otherwise [assume].</summary>
    public static void EndLater(Player p, FishingSession s)
    {
        var state = s.State;
        After(p, s, TimeSpan.FromSeconds(p.World.Fishing.EndFallback), () =>
        {
            if (s.State == state) End(p, "no FishingCancel from the client (fallback)");
        });
    }

    // ---- zone charges ----

    /// <summary>
    /// One charge of <paramref name="zone"/> taken (the strike): its new state to everyone who knows it, and the regrowth: at 0
    /// the next charge after delayatzerocharges + one interval, else one interval when none is pending (÷ <paramref name="speed"/>, Rates:Regrowth).
    /// </summary>
    public static void TakeCharge(Map map, FishingZone zone, TimeSpan now, double speed = 1)
    {
        if (zone.MaxCharges <= 0 || zone.Charges <= 0) return;
        zone.Charges--;
        map.SendToObservers(zone, zone.Changed());
        if (zone.Charges == 0) zone.RegrowAt = now + (TimeSpan.FromSeconds(zone.Settings.DelayAtZero) + Interval(zone, map.Random)) / speed;
        else zone.RegrowAt ??= now + Interval(zone, map.Random) / speed;
    }

    private static TimeSpan Interval(FishingZone zone, Random random) =>
        TimeSpan.FromSeconds(zone.Settings.ChargeMin + random.NextDouble() * Math.Max(0, zone.Settings.ChargeMax - zone.Settings.ChargeMin));

    /// <summary>The world tick (every map): zone charges due back come back one at a time, each change sent to the zone's viewers.</summary>
    public static void Update(Map map, TimeSpan now, double speed = 1)
    {
        foreach (var zone in map.FishingZones)
        {
            if (zone.RegrowAt is not { } at || at > now) continue;
            var before = zone.Charges;
            while (zone.RegrowAt is { } due && due <= now && zone.Charges < zone.MaxCharges)
            {
                zone.Charges++;
                zone.RegrowAt = zone.Charges < zone.MaxCharges ? due + Interval(zone, map.Random) / speed : null;
            }
            if (zone.Charges >= zone.MaxCharges) zone.RegrowAt = null;
            if (zone.Charges != before)
            {
                map.SendToObservers(zone, zone.Changed());
                Log.Debug("world", $"{zone} regrew");
            }
        }
    }
}
