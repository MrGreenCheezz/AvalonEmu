using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

/// <summary>
/// Stage 5b and 5c (analysis/work/stage5/SPEC.md §5b.7, §5c.5): the damage, energy and fame formulas against the SPEC's
/// numbers, then fights in 0204 on a <see cref="ManualClock"/> with FakePeers: a T4 axe at IP 945 on a ghoul (auto 39,
/// AXESMASH 201 after its 0.4 s cast, the kill batch in the SPEC's order, the corpse's Leave 5.0 s later, the loot bag
/// taken into the inventory, the respawn of a spawn point) and a fire staff at IP 1050 (FIRESTAFFBOLT2: energy 8 at the
/// end of its 0.8 s cast, the projectile's hit and four ticks of damage over time, a cancelled cast, the combat timeout).
/// </summary>
public static class CombatTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("combat: formulas (resistance, IP and AP scaling, heal exponent, rounding, energy, fame) and their data (spells.xml, gamedata, danger type)", Formulas),
        ("combat: silver appearance follows premium eligibility without changing pickup ownership", SilverAppearance),
        ("combat: T4 axe at IP 945 on a ghoul: auto 39, AXESMASH 201 after 0.4 s, observer events, refusals, instant spell, kill batch, loot bag, corpse, respawn", AxeFight),
        ("combat: fire staff at IP 1050: FIRESTAFFBOLT2 after 0.8 s with energy 8, projectile hit, 4 ticks over time, CastCancel, combat timeout", FireStaff),
        ("loot: shift+click from a bag into stacks, a full inventory and a freed slot (InventoryMoveGivenItems counts)", LootIntoStacks),
        ("loot: roll rates of the 18 captured kills (loot.xml, total loot factor, black market chance; fixed seed) and the Economy/Loot config", LootRates),
        ("loot balance: a T5 CORRUPTED_HERETIC_CHEST_RARE under the pve preset, 300 rolls: average items and gear, tier sanity, BM soft cap (loot-balance SPEC)", LootBalance),
    ];

    private static ClusterStore Store => Client.Clusters!;

    private static void SilverAppearance()
    {
        using var arena = new Arena();
        var (_, _, owner) = arena.Join("SilverOwner", new InventoryItem("T1_MAIN_SWORD"));
        var (_, _, other) = arena.Join("SilverObserver", new InventoryItem("T1_MAIN_SWORD"));
        var mob = arena.Spawn("T1_MOB_HERETIC_THIEF", new Vector2(1.5f, 0));
        var pile = new SilverPile(arena.World.ObjectIds.Next(), mob, 3, 1.305, owner.Character.Id, arena.World.GameNow);
        NewSilverObjectEvent View(Player p) => pile.BuildCreate(p).OfType<NewSilverObjectEvent>().Single();
        Premium.Set(owner, false, arena.World.GameNow);
        Check(!View(owner).dag && pile.MayTake(owner), "ordinary owners receive ordinary silver graphics and can collect");
        Premium.Set(owner, true, arena.World.GameNow);
        Check(View(owner).dag && pile.MayTake(owner), "premium owners receive premium graphics");
        Premium.Set(other, true, arena.World.GameNow);
        Check(!View(other).dag && !pile.MayTake(other), "an unrelated premium observer gains neither premium graphics nor ownership");
        Check(View(owner).daa!.Single() == owner.Character.Id && View(other).daa!.Single() == owner.Character.Id,
            "both observers receive the same actual owner, independently of premium appearance");
    }

    /// <summary>The numeric checks of SPEC §5b.2, §5b.3 and §5c.2, and the client data they come from.</summary>
    private static void Formulas()
    {
        var r = Combat.Resistance(121);
        Check(Math.Abs(r - 100.0 / 221) < 1e-12 && Combat.Rounded(Combat.AutoAttack(41, 1.0825, 945) * r) == 39,
              $"T4_MAIN_AXE IP 945 against armor 121: 41 × 2.116 × 0.4525 = 39 (got {Combat.AutoAttack(41, 1.0825, 945) * r:0.###})");
        var axeAp = Combat.AbilityPower(138, 1.0825, 945);
        Check(Combat.Rounded(Combat.SpellChange(152, axeAp) * r) == 201, $"AXESMASH 152 × AP {axeAp:0.#} / 100 × R = 201");
        Check(Combat.Rounded(Combat.SpellChange(62, 388.5) * r) == 109 && Combat.Rounded(431 * Combat.Resistance(115)) == 200
              && Combat.Rounded(Combat.SpellChange(300, 93) * Combat.Resistance(115)) == 130,
              "FIRESTAFFBOLT2 62 × 3.885 × 100/221 = 109; mob 2306's auto 431 × 0.465 = 200; GHOUL_BITE 300 × 0.93 × 0.465 = 130");
        Check(Math.Abs(Combat.Heal(100, 400) - 100 * Math.Pow(4, 0.6634)) < 1e-9 && Combat.Rounded(Combat.Heal(14.4, 292)) == 29 && Combat.Rounded(2.5) == 2 && Combat.Rounded(3.5) == 4,
              "heal = change × (AP/100)^0.6634; Math.Round rounds half to even");
        Check(Combat.PlayerEnergyCost(3, 1.0918, 1050) == 8 && Combat.PlayerEnergyCost(8, 1.0918, 945) == 18 && Combat.PlayerEnergyCost(15, 1.0918, 1050) == 38
              && Combat.PlayerEnergyCost(3, 1.0918, 705) == 6 && Combat.PlayerEnergyCost(10, 1.0918, 705) == 19,
              "player energy: FIRESTAFFBOLT2 3 → 8 and FIREWALL 15 → 38 at IP 1050, AXESMASH 8 → 18 at IP 945, DEADLYSHOT 3 → 6 and GROUNDARROW 10 → 19 at IP 705");
        Check(Combat.MobEnergyCost(5, 93) == 5 && Combat.MobEnergyCost(10, 80) == 8 && Combat.MobEnergyCost(10, 133) == 13 && Combat.MobEnergyCost(10, 59) == 6
              && Combat.MobEnergyCost(50, 123) == 62 && Combat.MobEnergyCost(20, 97) == 19,
              "mob energy: round(energyusage × AP / 100), half to even (GHOUL_BITE 5, CHARGED_FRENZY 8, FREEZE 13/6, AOE_SLOW 62, WOLF_HOWL 19)");
        Check(Combat.Fame(94, 2.15, 0) == (2021000, 1081000) && Combat.Fame(94, 2.4, 0) == (2256000, 1316000) && Combat.Fame(66, 2.15, 3.2) == (443437, 237187),
              $"fame: ghoul 94 × 2.15 → 202.1/108.1, yellow 94 × 2.4 → 225.6/131.6, camp skeleton 66 × 2.15 / 3.2 → 44.3437/23.7187 (got {Combat.Fame(66, 2.15, 3.2)})");

        var data = Client.GameData!;
        Check(data.DangerBonuses["safe"] == new DangerBonus(2.15, 1.25, 1.45) && data.DangerBonuses["yellow"].FameFactor == 2.4
              && data.CombatProgressions["mainhand_1h"] == new CombatProgression(1.0825, 1.0825, 1.0918, 1.0474) && data.CombatProgressions["mainhand_2h"].AbilityPower == 1.0918
              && data.InCombatActiveSeconds == 20 && data.AttackDamageTimes["melee"] == 0.25 && data.AttackDamageTimes["magic"] == 0.36 && data.Barehand == (2, 20, 1),
              "gamedata ClusterDangerBonus (safe 2.15/1.25/1.45), ItemPowerProgression combat progressions, InCombat 20 s, characters.xml damage times and bare hands");
        var spells = Client.Spells!;
        Check(spells.Template("FIRESTAFFBOLT2") is { Id: 3214, Target: "enemy", CastingTime: 0.8f, HitDelay: 0.12f, EnergyUsage: 3, CastRange: 13, MaxStack: 3, ProjectileSpeed: 85 } bolt
              && bolt.Effects.Select(e => e.Name.LocalName).SequenceEqual(["directattributechange", "attributechangeovertime"])
              && spells.Template("LIFESAVIOR") is { EnergyUsage: 0.15f, EnergyUsageIsRelative: true } && spells.Template("AXESMASH") is { CastingTime: 0.4f, RecastDelay: 15 }
              && spells.Template("ENERGYREWARD")?.Id == 827,
              "spells.xml: FIRESTAFFBOLT2 3214 (0.8 s, hit delay 0.12, 3 energy, range 13, maxstack 3, flyspeed 85; its client effect list without the projectile), LIFESAVIOR relative, ENERGYREWARD 827");
        Check(Store.Get("0204").DangerType == "safe" && Store.Mobs.Find("T4_MOB_ROAMING_UNDEAD_GHOUL") is { Fame: 94, EnergyReward: 3, HitPointsMax: 385, CollisionRadius: 0.75f }
              && Store.Loot.Has("T4_DIRECTLOOTDROP_WHITE"),
              "0204 is SAFEAREA → GREEN → safe; the ghoul has fame 94, energy reward 3, 385 hp; loot.xml is read with the cluster");
        var rng = new Random(7);
        var rolls = Enumerable.Range(0, 300).SelectMany(_ => Store.Loot.RollList("T4_FRAGMENT_LOOT", rng)).ToList();
        Check(rolls.Count > 0 && rolls.All(x => x.Amount == 1 && Client.Items.Find(x.Item) is not null) && rolls.Any(x => x.Item == "T4_RUNE"),
              "loot.xml: a list's items roll with their chances (T4_FRAGMENT_LOOT gives runes, souls, relics of items.xml)");
    }

    /// <summary>A world on 0204 with a manual clock whose GameTimeStamps follow it, the shared client data and a fixed map seed.</summary>
    internal sealed class Arena : IDisposable
    {
        public readonly string Dir = NewDir();
        public readonly ManualClock Clock = new();
        public readonly SqliteCharacterRepository Characters;
        public readonly World World;
        public readonly Vector2 Stand;

        /// <param name="spells">Whether the mobs cast their &lt;AI&gt; spells (stageD mobspells); off, they fight as in 5d.</param>
        /// <param name="config">The world's config (JoinByCharacterId must be on); null = the defaults.</param>
        /// <param name="pvp">The PvP rules of every map (<see cref="World.PvpOverride"/>); null = the cluster's own.</param>
        public Arena(Func<Mob, IReadOnlyList<(string Item, int Amount)>>? loot = null, bool ai = false, CombatSettings? combat = null, bool spells = false, ServerConfig? config = null,
            ClusterPvp? pvp = null)
        {
            Characters = Repository(Dir);
            var anchor = GameTime.Now;
            World = new World(config ?? new ServerConfig { JoinByCharacterId = true }, Characters, Client.Items, Client.GameData, Client.Spells)
            {
                Sync = new object(), Clock = Clock, Clusters = Store, MapSeed = 42, GameTicks = () => anchor + Clock.Now.Ticks, LootOverride = loot,
                MobAi = ai, Combat = combat ?? CombatSettings.Default, PvpOverride = pvp,
            };
            World.MobSpells = spells;
            // Next to a guard tower of 0204, as MobSpawnTest: spawn points around; without the AI (5b/5c) nothing fights back.
            Stand = Store.Get("0204").MobSpawnPoints.First(p => p.Mob == "T6_MOB_GUARD_TOWER").Position + new Vector2(3, 0);
        }

        /// <summary>A character of the starter kit with <paramref name="weapon"/> and <paramref name="levels"/> (achievement name → level), joined at the stand.</summary>
        public (WorldSession Session, FakePeer Peer, Player Player) Join(string name, InventoryItem weapon, params (string Achievement, int Level)[] levels)
        {
            var c = StarterKit.Default.NewCharacter(name);
            c.Equipment[0] = weapon;
            Setup?.Invoke(c);
            c.Cluster = "0204";
            (c.PositionX, c.PositionY) = (Stand.X, Stand.Y);
            var data = Client.GameData!;
            c.Progression = new Progression();
            foreach (var (achievement, level) in levels) c.Progression.SetLevel(data.Achievements.Single(a => a.Name == achievement).Id, level);
            Check(Characters.Create(c), $"character {name} created");
            var peer = new FakePeer();
            var session = new WorldSession(peer, World);
            peer.Disconnected = session.OnDisconnected;
            session.OnRequest(Request(new JoinRequest { ss = c.Id }, 1), encrypted: false);
            return (session, peer, session.Player!);
        }

        public Mob Spawn(string mob, Vector2 offset) =>
            SpawnManager.SpawnAt(World.Maps.Find("0204")!, Store.Mobs.Find(mob)!, Stand + offset, 0, World);

        /// <summary>Applied to every new character of <see cref="Join"/> before it is saved (its inventory, say).</summary>
        public Action<Character>? Setup;

        /// <summary><paramref name="seconds"/> of 50 ms world ticks.</summary>
        public void Run(double seconds)
        {
            for (var i = 0; i < Math.Round(seconds * 20); i++)
            {
                Clock.Advance(TimeSpan.FromMilliseconds(50));
                lock (World.Sync) World.Update();
            }
        }

        public void Dispose()
        {
            World.Dispose();
            Characters.Dispose();
            try { Directory.Delete(Dir, true); } catch (IOException) { }
        }
    }

    internal static Dictionary<byte, object?> Wire(EventData e)
    {
        var w = new ByteWriter();
        Protocol18.WriteEventData(w, e);
        return Protocol18.ReadEventData(new ByteReader(w.ToArray())).Parameters;
    }

    internal static int Code(EventData e) => e.Parameters.ContainsKey(ReservedKeys.EventCode) ? WorldObjectTest.Code(e) : e.Code; // event 3 (a walking mob) has no key 252

    /// <summary>The events of <paramref name="peer"/> since <paramref name="mark"/> as (code, parameters).</summary>
    internal static List<(EventCodes Code, Dictionary<byte, object?> P)> Since(FakePeer peer, int mark) =>
        [.. peer.Events.Skip(mark).Select(e => ((EventCodes)Code(e), Wire(e)))];

    internal static string Show(IEnumerable<(EventCodes Code, Dictionary<byte, object?> P)> events) =>
        string.Join("; ", events.Select(e => $"{e.Code} {{{string.Join(", ", e.P.Where(p => p.Key < 250).Select(p => $"{p.Key}={Value(p.Value)}"))}}}"));

    private static string Value(object? v) => v switch
    {
        Array a => $"[{string.Join(",", a.Cast<object>())}]",
        _ => $"{v}",
    };

    internal static long L(Dictionary<byte, object?> p, byte k) => Convert.ToInt64(p[k]);

    internal static float F(Dictionary<byte, object?> p, byte k) => Convert.ToSingle(p[k]);

    internal static void Send(WorldSession s, AlbionRequest req) => s.OnRequest(Request(req, null), encrypted: false);

    /// <summary>
    /// Shift+click from a loot bag into an inventory with no free slot but a T4_ORE stack of 997: two ore join the stack, the
    /// rest of the ore, the sword and the hide stay in the bag (re-sent counts, no Detach); a freed slot then takes two of
    /// the three ore left as a new object, the bag keeping one.
    /// </summary>
    private static void LootIntoStacks()
    {
        using var arena = new Arena(mob => [("T4_ORE", 5), ("T4_MAIN_SWORD", 1), ("T4_HIDE", 2)]);
        arena.Setup = c =>
        {
            c.Inventory = [.. Enumerable.Range(0, Inventory.BagSlots).Select(s => new InventoryItem(s == 0 ? "T4_ORE" : "T4_MAIN_AXE", s == 0 ? 997 : 1))];
        };
        var world = arena.World;
        var (session, peer, me) = arena.Join("Looter", new InventoryItem("T4_MAIN_AXE"));
        var stack = me.Inventory.Bag[0]!;
        Check(stack is { Count: 997 } && me.Inventory.Bag.FreeSlot() < 0, "the inventory is full, slot 0 holds 997 ore");
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        ghoul.Health.Set(1, world.GameNow);
        Send(session, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = 0 });
        arena.Run(0.5);
        var bag = world.Maps.Find("0204")!.Objects.Values.OfType<LootBag>().Single();
        Send(session, new RegisterToObjectRequest { zg = bag.Id });
        var (ore, sword, hide) = (bag.Items.Items.Single(i => i.Template.Name == "T4_ORE"), bag.Items.Items.Single(i => i.Template.Name == "T4_MAIN_SWORD"),
            bag.Items.Items.Single(i => i.Template.Name == "T4_HIDE"));

        var mark = peer.Events.Count;
        Send(session, new InventoryMoveGivenItemsRequest { ro = bag.Items.Id, rp = 1, rq = me.Inventory.Bag.Id, rr = 5, rs = [ore.Id, sword.Id, hide.Id], rt = [5, 1, 2] });
        var move = Since(peer, mark);
        Check(move.Select(e => e.Code).SequenceEqual([EventCodes.InventoryState, EventCodes.NewSimpleItem, EventCodes.NewSimpleItem, EventCodes.InventoryState])
              && move[0].P[0] is long[] left && left.Count(id => id != 0) == 3 && L(move[1].P, 0) == stack.Id && Convert.ToInt32(move[1].P[2]) == 999
              && L(move[2].P, 0) == ore.Id && Convert.ToInt32(move[2].P[2]) == 3 && L(move[3].P, 3) == 5 && stack.Count == 999 && ore.Count == 3
              && bag.Items.Count == 3 && me.OpenBag == bag,
              $"a full inventory: 2 ore into the stack (999), 3 stay in the bag with the sword and the hide, both counts re-sent, no Detach (got {Show(move)})");

        Send(session, new InventoryDestroyItemRequest { ri = me.Inventory.Bag[1]!.Id, rj = 6 });
        mark = peer.Events.Count;
        Send(session, new InventoryMoveGivenItemsRequest { ro = bag.Items.Id, rp = 2, rq = me.Inventory.Bag.Id, rr = 7, rs = [ore.Id, hide.Id], rt = [2, 0] });
        move = Since(peer, mark);
        var part = me.Inventory.Bag[1];
        Check(move.Select(e => e.Code).SequenceEqual([EventCodes.InventoryState, EventCodes.NewSimpleItem, EventCodes.NewSimpleItem, EventCodes.InventoryState])
              && part is { Count: 2 } && part != ore && part.Template == ore.Template && L(move[1].P, 0) == part.Id && Convert.ToInt32(move[1].P[2]) == 2
              && L(move[2].P, 0) == ore.Id && Convert.ToInt32(move[2].P[2]) == 1 && move[3].P[0] is long[] slots && slots[1] == part.Id && bag.Items.Count == 3,
              $"a freed slot: 2 of the 3 ore as a new object there, the bag keeps 1, the hide finds no room (got {Show(move)})");
    }

    /// <summary>
    /// The loot roll against the 18 captured mob kills (103829, 114814, 002835: 2 bags; the miniboss's 4 items, 2 of them
    /// useblackmarket), on a fixed seed: the bag counts of 2000 replays of those kills with the safe cluster's total loot
    /// factor (1.25 × 0.9) and Loot:BlackMarketChance 0.1, and the old roll (every useblackmarket item) for contrast.
    /// Then the Economy and Loot config keys.
    /// </summary>
    private static void LootRates()
    {
        (string Mob, int Kills)[] captured =
        [
            ("T4_MOB_ROAMING_UNDEAD_GHOUL", 5), ("T4_MOB_ROAMING_UNDEAD_MAGE", 2), ("T4_MOB_ROAMING_UNDEAD_SOLDIER", 2),
            ("T4_MOB_ROAMING_UNDEAD_MAGE_CHAMPION", 1), ("T4_MOB_ROAMING_UNDEAD_SOLDIER_MINIBOSS", 1), ("T4_MOB_ROAMING_UNDEAD_CAMP_SKELETON_VETERAN", 1),
            ("MOB_FOX", 2), ("MOB_RABBIT", 2), ("T4_MOB_CRITTER_FIBER_SWAMP_GREEN", 1), ("T3_MOB_CRITTER_FIBER_SWAMP_GREEN", 1),
        ];
        var data = Client.GameData!;
        var factor = data.DangerBonuses["safe"].MobLootFactor * data.GlobalMobLootFactor;
        var chance = new ServerConfig().LootBlackMarketChance;
        Check(Math.Abs(factor - 1.125) < 1e-9 && chance == 0.1, $"the safe total loot factor is 1.25 × 0.9 (got {factor}); Loot:BlackMarketChance defaults to 0.1");
        const int Rounds = 2000;
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        (double Bags, double Ghoul, double BossItems, double BossBags) Replay(double blackMarket)
        {
            var rng = new Random(20261016);
            var (bags, ghoulBags, bossItems, bossBags) = (0, 0, 0, 0);
            for (var round = 0; round < Rounds; round++)
                foreach (var (name, kills) in captured)
                    for (var k = 0; k < kills; k++)
                    {
                        var drops = Store.Loot.RollMob(Store.Mobs.Find(name)!.Loot!, rng, factor, blackMarket, list => missing.Add("list " + list));
                        foreach (var d in drops.Where(d => Client.Items.Find(d.Item) is null)) missing.Add(d.Item);
                        if (drops.Count == 0) continue;
                        bags++;
                        if (name == "T4_MOB_ROAMING_UNDEAD_GHOUL") ghoulBags++;
                        if (name.EndsWith("_MINIBOSS", StringComparison.Ordinal)) (bossItems, bossBags) = (bossItems + drops.Sum(d => d.Amount), bossBags + 1);
                    }
            return ((double)bags / Rounds, ghoulBags / (5.0 * Rounds), (double)bossItems / Rounds, (double)bossBags / Rounds);
        }
        var now = Replay(chance);
        var before = Replay(1);
        Check(now.Bags is > 1.5 and < 3.5 && now.Ghoul < 0.12 && now.BossBags > 0.6 && now.BossItems is > 1.2 and < 4.5 && !missing.Any(m => m.StartsWith("list ", StringComparison.Ordinal)),
              $"per 18 captured kills {now.Bags:0.##} bags (captured 2), ghoul {now.Ghoul:P1} (0 of 5), miniboss {now.BossBags:P0} bags with {now.BossItems:0.#} items (1 bag, 4 items); every list in loot.xml (items not in items.xml, left out by the drop: {string.Join(", ", missing)})");
        Check(before.Bags > 8 && before.Ghoul > 0.35 && before.BossItems > 10,
              $"every useblackmarket item dropping (the old roll): {before.Bags:0.#} bags per 18 kills, ghoul {before.Ghoul:P0}, miniboss {before.BossItems:0.#} items");

        var path = Path.Combine(NewDir(), "none.json");
        var defaults = LoadTestConfig(path, ["--Preset=official"]); // the pve preset's 0.35: PveTest
        var official = LoadTestConfig(path, ["--Preset=official", "--Economy:SilverPickupFactor=1.5", "--Economy:SilverPickupTax=0.15", "--Loot:BlackMarketChance=1"]);
        Check(defaults is { SilverPickupFactor: 1, SilverPickupTax: 0, LootBlackMarketChance: 0.1 }
              && official is { SilverPickupFactor: 1.5, SilverPickupTax: 0.15, LootBlackMarketChance: 1 },
              "Economy:SilverPickupFactor 1 and SilverPickupTax 0 by default (no premium, no guild), Loot:BlackMarketChance 0.1; the captured 1.5 / 0.15 can be set");
        try { LoadTestConfig(path, ["--Economy:SilverPickupTax=2"]); Check(false, "a tax of 200 % accepted"); }
        catch (InvalidDataException e) { Check(e.Message.Contains("SilverPickupTax"), "Economy:SilverPickupTax is 0..1"); }
    }

    /// <summary>
    /// Loot balance (analysis/work/stageN/loot-balance/SPEC.md §4): 300 rolls of the T5 corrupted rare chest as OpenChest rolls it under
    /// the pve preset (factor Rates:Loot 1, no cluster moblootfactor, Loot:BlackMarketChance 0.15, soft cap 4, content tier 5; the live
    /// report had 41 items with T7 gear) average 4-14 items with at most 8 pieces of gear, gear above T6 only now and then (the data's
    /// rare T7 list), never above T7; the old roll (1.33 × 0.9 × 1.5, BM 0.525, no cap or tier) for contrast.
    /// </summary>
    private static void LootBalance()
    {
        var pve = LoadTestConfig(Path.Combine(NewDir(), "none.json"), ["--Preset=pve"]);
        Check(pve is { LootBlackMarketChance: 0.15, LootBlackMarketSoftCap: 4 } && pve.Rates.Loot == 1, "pve preset: Loot:BlackMarketChance 0.15, soft cap 4, Rates:Loot 1");
        var chest = Store.LootChests!.Chests["CORRUPTED_HERETIC_CHEST_RARE"].States[0].LootFor(5)!;
        (double Items, double Gear, double High, int Max) Roll(int rounds, double factor, double bm, int tier, int cap)
        {
            var rng = new Random(20261018);
            var (items, gear, high, max) = (0, 0, 0, 0);
            for (var i = 0; i < rounds; i++)
                foreach (var (name, amount) in Store.Loot.RollMob(chest, rng, factor, bm, null, tier, cap))
                {
                    if (Client.Items.Find(name) is not { IsEquipment: true }) { items++; continue; }
                    (items, gear) = (items + amount, gear + amount);
                    var t = LootTables.TierOf(name);
                    if (t > 6) high += amount;
                    max = Math.Max(max, t);
                }
            return ((double)items / rounds, (double)gear / rounds, (double)high / rounds, max);
        }
        var now = Roll(300, pve.Rates.Loot, pve.LootBlackMarketChance, 5, pve.LootBlackMarketSoftCap);
        var old = Roll(100, 1.33 * 0.9 * 1.5, 0.525, 0, 0);
        Check(now.Items is > 4 and < 14 && now.Gear < 8 && now.High < 0.25 && now.Max <= 7 && old.Items > 25,
              $"T5 corrupted rare chest: {now.Items:0.#} items, {now.Gear:0.#} gear, {now.High:0.###} above T6 per chest, max T{now.Max} (the old roll {old.Items:0.#} items, {old.Gear:0.#} gear)");
    }

    private static void AxeFight()
    {
        using var arena = new Arena(mob => mob.Template.Name == "T4_MOB_ROAMING_UNDEAD_GHOUL" ? [("T4_MAIN_SWORD", 1), ("T4_ORE", 3)] : []);
        var world = arena.World;
        // No passive (its default PASSIVE_BLEEDCHANCE would stack on the autos and bleed the ghoul, stageD effects batch 2).
        // IP 945: 700 + COMBAT_AXES_AXE 100 × (0.2 + 2) + COMBAT_AXES 100 × 0.2 + COMBAT_AXES_GREAT 25 × 0.2. The captured AXESMASH 201
        // has no worn spell damage bonus in it (stageM pvp-damage): a T4 plate set 3 chest (0) instead of the starter kit's set 1 (0.1, 221).
        arena.Setup = c => c.Equipment[3] = new InventoryItem("T4_ARMOR_PLATE_SET3");
        var (session, peer, me) = arena.Join("Fighter", new InventoryItem("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] },
            ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100), ("COMBAT_AXES_GREAT", 25));
        arena.Setup = null;
        var (_, watcherPeer, watcher) = arena.Join("Watcher", new InventoryItem("T4_MAIN_SWORD"));
        var axe = me.Inventory.Equipment[0]!;
        var ip = Combat.ItemPower(me, axe);
        Check(Math.Abs(ip - 945) < 0.01 && me.Observers.Contains(watcher), $"the axe's item power is 945 (got {ip}); the watcher sees the fighter");
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var spells = Client.Spells!;
        short axesmash = spells.IdOf("AXESMASH"), rending = spells.IdOf("RENDINGSTRIKE");
        Check(me.Visibility.Knows(ghoul) && watcher.Visibility.Knows(ghoul) && axe.Active is [var s0, var s1, ..] && s0 == rending && s1 == axesmash,
              "both players see the ghoul; the axe's slots 0/1 hold RENDINGSTRIKE and AXESMASH");

        // 1. AttackStart: no answer, no Attack to the attacker; combat starts; the hit 0.25 s (meleeattackdamagetime) later.
        var responses = peer.Responses.Count;
        var (mark, wmark) = (peer.Events.Count, watcherPeer.Events.Count);
        var t0 = world.GameNow;
        Send(session, new AttackStartRequest { wf = t0, co = ghoul.Id, cp = 0 });
        var start = Since(peer, mark);
        Check(peer.Responses.Count == responses && start.Select(e => e.Code).SequenceEqual([EventCodes.InCombatStateUpdate, EventCodes.RegenerationHealthChanged])
              && start[0].P[1] is true && !start[0].P.ContainsKey(2) && !start[1].P.ContainsKey(5) && L(start[1].P, 6) == t0 && F(start[1].P, 3) == me.Health.Max,
              $"AttackStart: no response; InCombatStateUpdate {{1: true}} and RegenerationHealthChanged without key 5 (got {Show(start)})");
        var attack = Since(watcherPeer, wmark).Single(e => e.Code == EventCodes.Attack).P;
        Check(L(attack, 0) == me.Id && L(attack, 1) == t0 && L(attack, 2) == ghoul.Id && Convert.ToByte(attack[3]) == 1 && L(attack, 4) - t0 == 2_500_000
              && L(attack, 5) == L(attack, 4) && Convert.ToByte(attack[6]) == 0,
              $"the watcher gets Attack {{1 start, 2 ghoul, 3 melee, 4 = 5 = start + 250 ms, 6 combo 0}} (got {Show([(EventCodes.Attack, attack)])})");
        mark = peer.Events.Count;
        arena.Run(0.2);
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate), "no HealthUpdate before the hit");
        arena.Run(0.1);
        var hit = Since(peer, mark).Single(e => e.Code == EventCodes.HealthUpdate).P;
        Check(L(hit, 0) == ghoul.Id && L(hit, 1) == t0 + 2_500_000 && F(hit, 2) == -39 && F(hit, 3) == 346 && Convert.ToByte(hit[4]) == 1 && Convert.ToByte(hit[5]) == 0
              && L(hit, 6) == me.Id && Convert.ToInt16(hit[7]) == -1 && !hit.ContainsKey(8) && Since(watcherPeer, wmark).Count(e => e.Code == EventCodes.HealthUpdate) == 1,
              $"HealthUpdate {{2: -39, 3: 346, 4: 1, 5: 0, 6: me, 7: -1}} stamped request + 250 ms, to both (got {Show([(EventCodes.HealthUpdate, hit)])})");
        // A swing before the interval (1/1.1 s - 150 ms) is dropped silently.
        mark = peer.Events.Count;
        Send(session, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = 1 });
        arena.Run(0.5);
        Check(peer.Responses.Count == responses && !Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate), "an AttackStart 0.8 s after the last is dropped: no answer, no hit");

        // 2. AXESMASH (0.4 s cast): nothing for the caster before its end, then energy, CastFinished, CastSpell, the ghoul's list
        //    with AXESMASH_EFFECT (stageD: before the hit), CastHit, HealthUpdate (the debuff acts after the damage: 201).
        (mark, wmark) = (peer.Events.Count, watcherPeer.Events.Count);
        var energy = me.Energy.At(world.GameNow);
        var t1 = world.GameNow;
        Send(session, new CastStartRequest { wf = t1, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 1 });
        var castStart = Since(watcherPeer, wmark).Single(e => e.Code == EventCodes.CastStart).P;
        Check(peer.Responses.Count == responses && Since(peer, mark).Count == 0 && L(castStart, 0) == me.Id && L(castStart, 1) == t1 && L(castStart, 4) == t1 + 4_000_000
              && Convert.ToInt16(castStart[5]) == axesmash && Convert.ToInt16(castStart[6]) == axe.Template.Index && L(castStart, 7) == ghoul.Id
              && Convert.ToByte(castStart[8]) == 1 && Convert.ToByte(castStart[9]) == 1,
              $"CastStart: no response and nothing to the caster; the watcher gets CastStart {{1 start, 4 end, 5 spell, 6 axe, 7 ghoul, 8 counter, 9 slot}} (got {Show([(EventCodes.CastStart, castStart)])})");
        arena.Run(0.35);
        Check(Since(peer, mark).Count == 0, "nothing before the cast end");
        arena.Run(0.1);
        var end = Since(peer, mark);
        Check(end.Select(e => e.Code).SequenceEqual([EventCodes.EnergyUpdate, EventCodes.CastFinished, EventCodes.CastSpell, EventCodes.ActiveSpellEffectsUpdate,
                  EventCodes.CastHit, EventCodes.HealthUpdate]),
              $"the cast end: EnergyUpdate, CastFinished, CastSpell, then (hitdelay 0) the debuff list, CastHit and HealthUpdate (got {Show(end)})");
        var debuff = end[3].P;
        Check(L(debuff, 0) == ghoul.Id && debuff[1] is short[] and [var debuffId] && debuffId == spells.IdOf("AXESMASH_EFFECT") && debuff[7] is byte[] and [0x1b]
              && debuff[8] is int[] and [8000, 8000] && debuff[9] is byte[] and [1] && debuff[10] is long[] and [var by] && by == me.Id && debuff[5] is byte[] and [1] && debuff[2] is float[] and [var dap] && dap == (float)Combat.AbilityPowerOf(me, axe),
              $"ActiveSpellEffectsUpdate {{1 [AXESMASH_EFFECT], 2 [AP], 5 {{1}}, 7 {{0x1b}} (two buffs of 8 s), 8 [8000, 8000], 9 {{1}}, 10 [me]}} (got {Show([end[3]])})");
        end.RemoveAt(3);
        var (en, fin, cs, ch, hu) = (end[0].P, end[1].P, end[2].P, end[3].P, end[4].P);
        Check(F(en, 2) == -18 && Math.Abs(F(en, 3) - (energy - 18)) < 0.01 && Convert.ToByte(en[4]) == 7 && L(en, 5) == me.Id && Convert.ToInt32(en[6]) == axesmash
              && Math.Abs(me.Energy.Value - (energy - 18)) < 0.01,
              $"EnergyUpdate {{2: -18 (8 × 1.0918^9.45), 4: 7, 5: me, 6: spell}}, charged at the end (got {Show([end[0]])})");
        Check(L(fin, 1) == ghoul.Id && Convert.ToInt16(fin[2]) == axesmash && Convert.ToByte(fin[3]) == 1 && Convert.ToByte(fin[4]) == 1 && L(fin, 5) == t1 + 4_000_000 && F(fin, 6) == 1f,
              $"CastFinished {{1 ghoul, 2 spell, 3 slot, 4 counter, 5 start + 0.4 s, 6 1.0}} (got {Show([end[1]])})");
        Check(L(cs, 1) == ghoul.Id && Convert.ToInt16(cs[3]) == axesmash && L(cs, 4) == t1 + 4_000_000 && L(cs, 5) == L(cs, 4) && Convert.ToByte(cs[6]) == 1 && Convert.ToInt16(cs[7]) == 0
              && L(ch, 0) == ghoul.Id && L(ch, 1) == me.Id && Convert.ToInt16(ch[2]) == axesmash && Convert.ToByte(ch[3]) == 1 && Convert.ToByte(ch[4]) == 1,
              $"CastSpell {{4 end, 5 hit, 6 counter, 7 0}} and CastHit {{0 ghoul, 1 me, 2 spell, 3 1, 4 counter}} (got {Show(end.Skip(2).Take(2))})");
        Check(F(hu, 2) == -201 && F(hu, 3) == 145 && Convert.ToByte(hu[4]) == 1 && Convert.ToByte(hu[5]) == 2 && Convert.ToInt16(hu[7]) == axesmash && L(hu, 1) == t1 + 4_000_000,
              $"HealthUpdate {{2: -201, 3: 145, 4: 1, 5: 2, 7: AXESMASH}} (got {Show([end[4]])})");
        Check(Since(watcherPeer, wmark).Select(e => e.Code).Where(c => c is not EventCodes.CastStart and not EventCodes.ActiveSpellEffectsUpdate).SequenceEqual(end.Select(e => e.Code))
              && Since(watcherPeer, wmark).Count(e => e.Code == EventCodes.ActiveSpellEffectsUpdate) == 1,
              "the watcher gets the same six at the end");
        // The debuff's 8 s (−0.16 armor × 200 × (AP/100)^0.3366 points) runs out before the autos below, which then hit for 39 again.
        arena.Run(8);
        // On cooldown (recast 15 s): the response aea with the slot, spell, counter and target.
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 2 });
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1] is { ReturnCode: WorldSession.CastRefused } refused
              && Convert.ToByte(refused.Parameters[1]) == 1 && Convert.ToInt32(refused.Parameters[2]) == axesmash && Convert.ToByte(refused.Parameters[7]) == 2
              && Convert.ToInt64(refused.Parameters[8]) == ghoul.Id,
              "AXESMASH again at once: refused (cooldown) with {1 slot, 2 spell, 7 counter, 8 target}");
        responses = peer.Responses.Count;

        // 3. Four more autos kill it (145 = 3 × 39 + 28). The lethal batch in the SPEC's order, with the forced loot bag.
        var kill = -1;
        for (var swing = 0; swing < 4; swing++)
        {
            arena.Run(1);
            mark = peer.Events.Count;
            wmark = watcherPeer.Events.Count;
            Send(session, new AttackStartRequest { wf = world.GameNow, co = ghoul.Id, cp = (byte)(swing % 3) });
            arena.Run(0.3);
            kill = mark;
        }
        var batch = Since(peer, kill);
        // StageE: the reputation gain leads the rewards (stage 5 SPEC §5c.1): NEUTRAL's pvegainfactor 0.007 × fame 202.1, reason 1.
        Check(batch.Count > 0 && batch[0].Code == EventCodes.ReputationUpdate && Math.Abs(F(batch[0].P, 2) - 0.007f * 202.1f) < 1e-4
              && L(batch[0].P, 4) == Pvp.NeutralLevel && L(batch[0].P, 5) == Pvp.NeutralLevel && L(batch[0].P, 6) == Reputation.MobKill && L(batch[0].P, 7) == ghoul.Template.Index,
              $"the kill's first event: ReputationUpdate {{2 0.007 × fame, 4/5 NEUTRAL, 6 1, 7 the mob}} (got {Show(batch.Take(1))})");
        batch = [.. batch.Skip(1)];
        // The destiny board first (PROGRESSION.md §6.6): the fighter's own board has COMBAT_AXES_AXE (17) at 100 of its 120
        // levels (COMBAT_SPEC's elite rows), the only node in progress the axe feeds; COMBAT_AXES is at its last level.
        var axeNode = Client.GameData!.Board!.Find("COMBAT_AXES_AXE")!.Id;
        Check(batch.Count > 0 && batch[0].Code == EventCodes.FullTrackedAchievementInfo
              && ((int[])batch[0].P[1]!).SequenceEqual([axeNode]), "the kill selects the axe node for the Destiny Board HUD");
        batch = [.. batch.Skip(1)];
        Check(batch.Count > 0 && batch[0].Code == EventCodes.AchievementProgressInfo && L(batch[0].P, 1) == axeNode && L(batch[0].P, 2) == 100
              && (string)batch[0].P[4]! == "[[2021000]]" && Math.Abs(F(batch[0].P, 3) - 2021000f / Client.GameData.Board.LevelFame(axeNode, 100)) < 1e-9,
              $"the kill's destiny board event: AchievementProgressInfo {{1 COMBAT_AXES_AXE, 2 level 100, 3 v / elite row 0, 4 [[2021000]]}} (got {Show(batch.Take(1))})");
        batch = [.. batch.Skip(1)];
        // PVE-DESIGN step 11 (journals SPEC §C.2, §C.3): the season points before the UpdateFame, 1.5 x sqrt(202.1) = 21.3243.
        Check(batch.Count > 0 && batch[0].Code == EventCodes.PersonalSeasonPointsGained && L(batch[0].P, 0) == 213243,
              $"the kill's PersonalSeasonPointsGained {{0 1.5 x sqrt(202.1) = 213243}} before the UpdateFame (got {Show(batch.Take(1))})");
        batch = [.. batch.Skip(1)];
        // Stage 6a: the silver pile right after UpdateFame (SPEC §5c.1), then taken out of the list for the indices below.
        Check(batch.Count > 1 && batch[1].Code == EventCodes.NewSilverObject, $"the kill's second event after the board is NewSilverObject (got {Show(batch.Take(2))})");
        var silver = batch[1].P;
        batch.RemoveAt(1);
        Check(ghoul.Dead && batch.Select(e => e.Code).SequenceEqual([
                  EventCodes.UpdateFame, EventCodes.EnergyUpdate, EventCodes.CastSpell, EventCodes.InCombatStateUpdate, EventCodes.RegenerationHealthChanged,
                  EventCodes.InCombatStateUpdate, EventCodes.NewLoot, EventCodes.ActiveSpellEffectsUpdate, EventCodes.HealthUpdate]),
              $"the kill: UpdateFame, EnergyUpdate, CastSpell 827, combat end (3), NewLoot, the death clear, the lethal HealthUpdate last; no Died (got {Show(batch)})");
        var fame = batch[0].P;
        Check(L(fame, 0) == me.Id && L(fame, 1) == 2021000 && L(fame, 2) == 2021000 && L(fame, 3) == 1081000 && fame[5] is true && fame[15] is true && me.Character.Fame == 2021000,
              $"UpdateFame {{1 total 202.1, 2: 202.1 (94 × 2.15), 3: 108.1, 5 true, 15 true}} (got {Show([batch[0]])})");
        var reward = batch[1].P;
        Check(F(reward, 2) == 3 && Convert.ToByte(reward[4]) == 8 && L(reward, 5) == ghoul.Id && !reward.ContainsKey(6)
              && L(batch[2].P, 0) == ghoul.Id && L(batch[2].P, 1) == me.Id && Convert.ToInt16(batch[2].P[3]) == 827 && L(batch[2].P, 4) == L(batch[2].P, 5)
              && batch[2].P.ContainsKey(6) && Convert.ToByte(batch[2].P[6]) == 0,
              $"EnergyUpdate {{2: +3, 4: 8, 5: the ghoul's object id}} without key 6, CastSpell {{0 ghoul, 1 me, 3 827, 4 = 5, 6 cast counter 0}} (got {Show(batch.Skip(1).Take(2))})");
        Check(batch[3].P is var a && !a.ContainsKey(1) && a[2] is true && F(batch[4].P, 5) == me.Health.RegenPerSecond && me.Health.RegenPerSecond > 0
              && batch[5].P.Keys.Where(k => k < 250).SequenceEqual(new byte[] { 0 }) && !me.Combat.InCombat,
              $"combat end: {{2: true}}, RegenerationHealthChanged with key 5 = the regeneration, {{}} (got {Show(batch.Skip(3).Take(3))})");
        var loot = batch[6].P;
        var bag = (LootBag)world.Maps.Find("0204")!.Objects[L(loot, 0)];
        Check(Convert.ToInt16(loot[1]) == ghoul.Template.Id && L(loot, 2) == ghoul.Id && (string)loot[3]! == "@MOB_UNDEAD_GHOUL_STANDARD" && Convert.ToByte(loot[7]) == 2
              && loot[10] is byte[] { Length: 16 } looter && new Guid(looter) == me.Character.Id && F(loot, 15) == 1.25f && F(loot, 16) == 1 && !loot.ContainsKey(14)
              && loot[19] is string[] { Length: 0 } && Convert.ToByte(loot[24]) == 0 && Convert.ToByte(loot[25]) == 0 && Convert.ToInt16(loot[26]) == -1 && bag.Items.Count == 2,
              $"NewLoot {{1 2048-like type, 2 the mob, 3 @namelocatag, 7: 2, 10 my Guid, 15: 1.25, 16: 1, no 14 for a T0_LOOTBAG, 19 [], 24/25 0, 26 -1}} (got {Show([batch[6]])})");
        var clear = batch[7].P;
        var lethal = batch[8].P;
        Check(L(clear, 0) == ghoul.Id && clear[3] is float[] { Length: 0 } && clear[4] is long[] { Length: 0 } && clear[5] is byte[] { Length: 0 } && clear[7] is byte[] { Length: 1 } b7 && b7[0] == 0 && clear[9] is byte[] { Length: 1 } b9 && b9[0] == 0
              && new byte[] { 1, 2, 6, 8, 10 }.All(k => !clear.ContainsKey(k)) && L(lethal, 0) == ghoul.Id && !lethal.ContainsKey(3) && F(lethal, 2) == -39,
              $"the death clear {{0, 3 [], 4 [], 5 empty, 7 {{0}}, 9 {{0}}}} and the lethal HealthUpdate without key 3, full damage (got {Show(batch.Skip(7))})");
        var watched = Since(watcherPeer, wmark);
        Check(watched.Select(e => e.Code).SequenceEqual([EventCodes.Attack, EventCodes.NewSilverObject, EventCodes.CastSpell, EventCodes.NewLoot, EventCodes.ActiveSpellEffectsUpdate, EventCodes.HealthUpdate])
              && !watched[1].P.ContainsKey(12),
              $"the watcher gets the swing's Attack, the silver pile without key 12, then the CastSpell 827, NewLoot, the death clear and the lethal hit (got {Show(watched)})");
        // The silver (stage 5c SPEC §5c.3): ghoul T4 SILVERCOINS_LOOT_ROAMING 22.5 × silvermodifier 2.494 × 0.84..0.88 = 47-49.
        var pile = (SilverPile)world.Maps.Find("0204")!.Objects[L(silver, 0)];
        Check(Convert.ToInt32(silver[2]) is >= 47 and <= 49 && L(silver, 3) == ghoul.Id && silver[4] is true && silver[6] is byte[] { Length: 16 } owner
              && new Guid(owner) == me.Character.Id && !silver.ContainsKey(12) && Convert.ToByte(silver[17]) == 0 && Convert.ToByte(silver[18]) == 0 && pile.Factor is > 1.3049 and < 1.3051,
              $"NewSilverObject {{2: 47..49, 3 the ghoul, 4 true, 6 my Guid, no premium key 12, 17 0, 18 0}}, factor 1.45 × 0.9 (got {Show([(EventCodes.NewSilverObject, silver)])})");
        var (bonus, guild) = (Combat.CapturedSilverBonus, Combat.CapturedSilverTax);
        Check(Combat.SilverValue(49, 1.305, bonus, guild) == (959175, 143876, 815299) && Combat.SilverValue(397, 1.305, bonus, guild) == (7771275, 1165691, 6605584)
              && Combat.SilverValue(3, 1.305, bonus, guild) == (58725, 8808, 49917) && Combat.SilverValue(173, 1.305) == (2257650, 0, 2257650)
              && world.Config is { SilverPickupFactor: 1, SilverPickupTax: 0 },
              "TakeSilver numbers of 103829 with premium and guild: 49 → 959175 gross, 143876 tax (15 % floored), 815299 net; 397 → 7771275 / 1165691; "
              + "3 → 58725 / 8808; without (the default): 173 → 2257650, no tax");
        var purse = me.Character.Silver;
        mark = peer.Events.Count;
        wmark = watcherPeer.Events.Count;
        Send(session, new TakeSilverRequest { wf = world.GameNow, agp = true, agq = [pile.Id] });
        var take = Since(peer, mark);
        var seen = Since(watcherPeer, wmark).Where(e => e.Code is EventCodes.OtherGrabbedLoot or EventCodes.TakeSilver).ToList();
        Check(seen.Count == 2 && seen[0].Code == EventCodes.OtherGrabbedLoot && L(seen[0].P, 0) == me.Id && seen[1].Code == EventCodes.TakeSilver
              && L(seen[1].P, 2) == pile.Id && !seen[1].P.ContainsKey(5) && !seen[1].P.ContainsKey(7),
              $"the watcher gets OtherGrabbedLoot and TakeSilver without keys 5 and 7 (got {Show(seen)})");
        var (gross, tax, net) = Combat.SilverValue(pile.Amount, pile.Factor);
        Check(peer.Responses.Count == responses && take.Select(e => e.Code).SequenceEqual([EventCodes.Leave, EventCodes.OtherGrabbedLoot, EventCodes.UpdateMoney, EventCodes.TakeSilver])
              && L(take[0].P, 0) == pile.Id && L(take[1].P, 5) == net && take[1].P[3] is true && L(take[2].P, 1) == purse + net && take[2].P.ContainsKey(2)
              && L(take[3].P, 2) == pile.Id && L(take[3].P, 3) == gross && tax == 0 && net == gross && !take[3].P.ContainsKey(5) && !take[3].P.ContainsKey(7)
              && L(take[3].P, 8) == 13050 && gross == pile.Amount * 13050 && me.Character.Silver == purse + net && !world.Maps.Find("0204")!.Objects.ContainsKey(pile.Id),
              $"TakeSilver: no response; Leave, OtherGrabbedLoot {{5 net}}, UpdateMoney, TakeSilver {{2 pile, 3 gross = amount × 1.305, 8 13050}} without tax (5) and premium (7); the silver is in the purse (got {Show(take)})");
        Send(session, new TakeSilverRequest { wf = world.GameNow, ago = pile.Id });
        Check(peer.Responses.Count == responses + 1 && peer.Responses[^1].ReturnCode == WorldSession.TakeSilverRefused && me.Character.Silver == purse + net, "a second TakeSilver of the same pile: RC 2167 (103829), nothing paid");
        responses = peer.Responses.Count;
        lock (world.Sync) world.Saves.SaveAll();
        world.Saves.Flush();
        Check(arena.Characters.Find(me.Character.Id)!.Fame == 2021000, "the fame is saved with the character");

        // 4. The bag: RegisterToObject → items and AttachItemContainer; InventoryMoveGivenItems → the bag's InventoryState, both items
        // again, the inventory's InventoryState, Detach, Leave (114814 +336.85).
        mark = peer.Events.Count;
        Send(session, new RegisterToObjectRequest { zg = bag.Id });
        var open = Since(peer, mark);
        Check(peer.Responses.Count == responses && open.Select(e => e.Code).SequenceEqual([EventCodes.NewEquipmentItem, EventCodes.NewSimpleItem, EventCodes.AttachItemContainer])
              && open[2].P is var attach && L(attach, 0) == bag.Id && new Guid((byte[])attach[1]!) == bag.Items.Id && attach[3] is long[] ids && ids.SequenceEqual(bag.Items.ObjectIds())
              && Convert.ToInt32(attach[5]) == 2,
              $"RegisterToObject: no response; the sword, the ore, AttachItemContainer {{0 bag, 1 Guid, 3 ids, 5: 2}} (got {Show(open)})");
        var taken = bag.Items.ObjectIds();
        var starterOre = me.Inventory.Bag.Items.First(i => i.Template.Name == "T4_ORE");
        var oreBefore = starterOre.Count;
        mark = peer.Events.Count;
        Send(session, new InventoryMoveGivenItemsRequest { ro = bag.Items.Id, rp = 2, rq = me.Inventory.Bag.Id, rr = 3, rs = taken });
        var move = Since(peer, mark);
        Check(peer.Responses.Count == responses && move.Select(e => e.Code).SequenceEqual([EventCodes.InventoryState, EventCodes.NewEquipmentItem, EventCodes.NewSimpleItem,
                  EventCodes.InventoryState, EventCodes.DetachItemContainer, EventCodes.Leave])
              && move[0].P[0] is long[] empty && empty.All(id => id == 0) && L(move[0].P, 3) == 2 && L(move[1].P, 0) == taken[0]
              && L(move[2].P, 0) == starterOre.Id && Convert.ToInt32(move[2].P[2]) == oreBefore + 3 && starterOre.Count == oreBefore + 3
              && move[3].P[0] is long[] slots && slots.Contains(taken[0]) && slots.Contains(starterOre.Id) && L(move[3].P, 3) == 3
              && new Guid((byte[])move[4].P[0]!) == bag.Items.Id && L(move[5].P, 0) == bag.Id && me.Inventory.Bag.Find(taken[0]) is not null
              && !world.Maps.Find("0204")!.Objects.ContainsKey(bag.Id),
              $"InventoryMoveGivenItems: InventoryState of the bag (empty, its revision 2), the sword and the starter ore stack (+3) again, InventoryState of the inventory (its revision 3), DetachItemContainer, the bag's Leave (got {Show(move)})");
        lock (world.Sync) world.Saves.SaveAll();
        world.Saves.Flush();
        Check(arena.Characters.Find(me.Character.Id)!.Inventory.Any(i => i?.Name == "T4_MAIN_SWORD") && arena.Characters.Find(me.Character.Id)!.Inventory.Any(i => i?.Name == "T4_ORE" && i.Count == oreBefore + 3),
              "the looted items are saved in the inventory (the ore in the starter stack)");

        // 5. The corpse: a newcomer at +2 s gets NewMob without key 13; Leave exactly 5.0 s after the kill.
        arena.Run(1.95); // the kill ran in the tick 0.05 s before the loop's last one ended: now 2 s after it
        var (_, newcomer, _) = arena.Join("Newcomer", new InventoryItem("T4_MAIN_SWORD"));
        var corpse = Since(newcomer, 0).Single(e => e.Code == EventCodes.NewMob && L(e.P, 0) == ghoul.Id).P;
        Check(!corpse.ContainsKey(13) && F(corpse, 14) == 385 && !corpse.ContainsKey(12), "a newcomer at +2 s gets the corpse: NewMob without key 13");
        mark = peer.Events.Count;
        arena.Run(2.95);
        Check(!Since(peer, mark).Any(e => e.Code == EventCodes.Leave && L(e.P, 0) == ghoul.Id), "no Leave before 5.0 s");
        arena.Run(0.05);
        Check(Since(peer, mark).Count(e => e.Code == EventCodes.Leave && L(e.P, 0) == ghoul.Id) == 1 && ghoul.Removed, "Leave of the corpse 5.0 s after the kill");

        // 6. The instant RENDINGSTRIKE (slot 0) on a soldier: its three result events in the request's batch, the hit 0.15 s later.
        var soldier = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(0, 1.5f));
        mark = peer.Events.Count;
        var t2 = world.GameNow;
        Send(session, new CastStartRequest { wf = t2, dx = 0, d0 = rending, d1 = soldier.Id, d4 = 3 });
        var instant = Since(peer, mark);
        Check(instant.Select(e => e.Code).SequenceEqual([EventCodes.EnergyUpdate, EventCodes.CastFinished, EventCodes.CastSpell]) && L(instant[1].P, 5) == t2
              && L(instant[2].P, 5) == t2 + 1_500_000 && Convert.ToByte(instant[1].P[3]) == 0,
              $"an instant spell: EnergyUpdate, CastFinished {{3: slot 0, 5: start}}, CastSpell {{5: start + 0.15 s}} in the request's batch (got {Show(instant)})");
        mark = peer.Events.Count;
        arena.Run(0.2);
        var rend = Since(peer, mark);
        Check(rend.Take(5).Select(e => e.Code).SequenceEqual([EventCodes.ActiveSpellEffectsUpdate, EventCodes.InCombatStateUpdate, EventCodes.RegenerationHealthChanged,
                  EventCodes.CastHit, EventCodes.HealthUpdate])
              && rend[0].P[1] is short[] and [var bleed] && bleed == spells.IdOf("AXE_BLEED") && rend[0].P[7] is byte[] and [3] && rend[0].P[8] is int[] and [6000]
              && F(rend[4].P, 2) == -78 && Convert.ToInt16(rend[4].P[7]) == rending,
              $"its hit: the AXE_BLEED list {{7 {{3}}, 8 [6000]}} first, combat starts again, CastHit and HealthUpdate {{2: -78 (59 × 2.919 × 0.4525)}} (got {Show(rend)})");
        // Out of range and an invalid target: bare answers.
        var far = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(0, 25));
        var tower = me.Visibility.Known.OfType<Mob>().First(m => m.Template.Name == "T6_MOB_GUARD_TOWER");
        mark = peer.Events.Count;
        Send(session, new AttackStartRequest { wf = world.GameNow, co = far.Id });
        Send(session, new AttackStartRequest { wf = world.GameNow, co = tower.Id });
        arena.Run(0.5);
        Check(peer.Responses.Count == responses + 2 && peer.Responses[^2] is { ReturnCode: WorldSession.AttackOutOfRange } r1 && r1.Parameters.Keys.All(k => k >= 250)
              && peer.Responses[^1] is { ReturnCode: WorldSession.AttackInvalidTarget } && !Since(peer, mark).Any(e => e.Code == EventCodes.HealthUpdate),
              "AttackStart 25 m away: a bare response 922; on a guard tower: -501; no HealthUpdate");

        // 7. A spawn point's mob killed: Leave after its corpse time, then (MobRespawnScale 0.01) a new mob at the point.
        world.MobRespawnScale = 0.01;
        // The tower's neighbours are guards: go to the nearest spawn point mob players fight.
        var pointMob = world.Maps.Find("0204")!.Objects.Values.OfType<Mob>().Where(m => m.Slot is not null && m.Attackable).MinBy(m => Vector2.Distance(m.Position, arena.Stand))!;
        var slot = pointMob.Slot!;
        lock (world.Sync) me.Teleport(pointMob.Position + new Vector2(1, 0), 0, DateTime.UtcNow);
        Check(me.Visibility.Knows(pointMob), $"{pointMob} in view after the teleport");
        lock (world.Sync) Combat.Damage(me, pointMob, 1e7, EffectType.Physical, HitOrigin.Direct, -1, world.GameNow);
        Check(pointMob.Dead && slot.Mob == pointMob, $"{pointMob} killed: a corpse that still holds its point");
        mark = peer.Events.Count;
        arena.Run(5.05);
        Check(Since(peer, mark).Any(e => e.Code == EventCodes.Leave && L(e.P, 0) == pointMob.Id) && slot.Mob is null && slot.RespawnAt is not null, "its Leave after 5 s; the point waits");
        arena.Run((slot.RespawnAt!.Value - world.Now).TotalSeconds + 0.1);
        Check(slot.Mob is { } again && again != pointMob && Since(peer, mark).Any(e => e.Code == EventCodes.NewMob && L(e.P, 0) == again.Id),
              "after the scaled respawn time the point has a new mob, sent as NewMob");
        Check(!peer.Events.Any(e => Code(e) == (int)EventCodes.Died), "no Died event in any of it");
    }

    private static void FireStaff()
    {
        using var arena = new Arena();
        var world = arena.World;
        // IP 1050: T4 700 + quality 5 (100) + COMBAT_FIRESTAFFS_FIRE 100 × (0.2 + 2) + COMBAT_FIRESTAFFS 100 × 0.2 + COMBAT_FIRESTAFFS_AVALON 100 × 0.1.
        var (session, peer, me) = arena.Join("Pyro",
            new InventoryItem("T4_MAIN_FIRESTAFF") { Quality = 5, Active = ["FIRESTAFFBOLT2", "FIRESTAFFIGNITE2_SPREAD", "PYROBLAST_SKILLSHOT"] },
            ("COMBAT_FIRESTAFFS_FIRE", 100), ("COMBAT_FIRESTAFFS", 100), ("COMBAT_FIRESTAFFS_AVALON", 100));
        var (_, watcherPeer, _) = arena.Join("Watcher", new InventoryItem("T4_MAIN_SWORD"));
        var ip = Combat.ItemPower(me, me.Inventory.Equipment[0]!);
        Check(Math.Abs(ip - 1050) < 0.01, $"the fire staff's item power is 1050 (got {ip})");
        var soldier = arena.Spawn("T4_MOB_UNDEAD_SOLDIER_VETERAN", new Vector2(6, 0));
        var bolt = Client.Spells!.IdOf("FIRESTAFFBOLT2");
        // AP = 120 × 1.0825^10.5 = 275.9: the bolt 62 × 2.759 × 100/221 = 77, a tick 4 × 2.759 × 100/221 = 5.
        // The captured -109 per bolt and -7 per tick (damage/hits.txt, SPEC §9 Q18a "1.408 × the item AP") came from the worn
        // T4_ARMOR_CLOTH_SET2's 0.45 spell damage bonus and two passives (stageM pvp-damage NOTES §3; DefenseTest "damage: the worn
        // spell damage bonus" replays it); this character wears the starter kit's T4 plate
        // set 1 (0.1): the bolt 77 × 1.1 = 85, a tick still 5.
        var ap = Combat.AbilityPower(120, 1.0825, ip);
        var r = Combat.Resistance(soldier.Template.MagicResistance);
        Check(Combat.Rounded(Combat.SpellChange(62, ap) * r) == 77 && Combat.Rounded(Combat.SpellChange(4, ap) * r) == 5, "without a damage-bonus chest: the bolt 77, a tick 5");
        var dealt = SpellEffects.DealtFactor(me, spell: true, EffectType.Magic, vsPlayer: false, world.GameNow);
        Check(Math.Abs(dealt - 1.1) < 1e-9 && Combat.Rounded(Combat.SpellChange(62, ap) * dealt * r) == 85 && Combat.Rounded(Combat.SpellChange(4, ap) * dealt * r) == 5,
              $"with the starter T4 plate set 1 (spell damage bonus 0.1): × 1.1, the bolt 85, a tick 5 (got × {dealt:0.####})");

        var energy = me.Energy.At(world.GameNow);
        var (mark, wmark) = (peer.Events.Count, watcherPeer.Events.Count);
        var t1 = world.GameNow;
        Send(session, new CastStartRequest { wf = t1, dx = 0, d0 = bolt, d1 = soldier.Id, d4 = 1 });
        Check(Since(peer, mark).Count == 0 && Since(watcherPeer, wmark).Single(e => e.Code == EventCodes.CastStart).P is var cs && L(cs, 4) == t1 + 8_000_000
              && Convert.ToByte(cs[9]) == 0 && Convert.ToByte(cs[8]) == 1,
              "CastStart: nothing to the caster; the watcher gets CastStart with the end 0.8 s on, slot 0 and counter 1");
        arena.Run(0.75);
        Check(Since(peer, mark).Count == 0, "nothing before start + 800 ms");
        arena.Run(0.05);
        var end = Since(peer, mark);
        Check(end.Select(e => e.Code).SequenceEqual([EventCodes.EnergyUpdate, EventCodes.CastFinished, EventCodes.CastSpell]) && F(end[0].P, 2) == -8
              && Convert.ToByte(end[0].P[4]) == 7 && L(end[1].P, 5) == t1 + 8_000_000 && Convert.ToByte(end[1].P[4]) == 1 && Convert.ToByte(end[1].P[3]) == 0
              && F(end[1].P, 6) == 1f && Convert.ToByte(end[2].P[6]) == 1 && Math.Abs(me.Energy.Value - (energy - 8)) < 0.01,
              $"at 0.8 s: EnergyUpdate {{2: -8 (3 × 1.0918^10.5), 4: 7}}, CastFinished {{5: start + 8e6 ticks, 4: 1, 3: 0, 6: 1.0}}, CastSpell {{6: 1}} (got {Show(end)})");
        // The hit: hitdelay 0.12 + 6 m / 85 m/s after the end, as CastSpell key 5 says.
        var hitAt = L(end[2].P, 5);
        Check(hitAt - (t1 + 8_000_000) == SpellCasts.Seconds(0.12 + 6.0 / 85), $"CastSpell key 5: the end + 0.12 s + 6/85 s (got {(hitAt - t1 - 8_000_000) / 1e4} ms)");
        mark = peer.Events.Count;
        arena.Run(4);
        var after = Since(peer, mark).Where(e => e.Code is EventCodes.CastHit or EventCodes.HealthUpdate).ToList();
        var ticks = after.Where(e => e.Code == EventCodes.HealthUpdate && Convert.ToByte(e.P[5]) == 5).ToList();
        Check(after.Count == 6 && after[0].Code == EventCodes.CastHit && Convert.ToInt16(after[0].P[2]) == bolt && Convert.ToByte(after[0].P[4]) == 1
              && F(after[1].P, 2) == -85 && Convert.ToByte(after[1].P[4]) == 2 && Convert.ToByte(after[1].P[5]) == 2 && Convert.ToInt16(after[1].P[7]) == bolt && L(after[1].P, 1) == hitAt
              && ticks.Count == 4 && ticks.All(t => F(t.P, 2) == -5 && Convert.ToInt16(t.P[7]) == bolt && Convert.ToByte(t.P[4]) == 2)
              && ticks.Select(t => L(t.P, 1) - hitAt).SequenceEqual(new[] { 0.3, 1.3, 2.3, 3.3 }.Select(SpellCasts.Seconds)),
              $"CastHit + HealthUpdate {{2: -85, 4: 2, 5: 2, 7: 3214}} at the hit, then 4 ticks of -5 (origin 5) at +0.3, +1.3, +2.3, +3.3 s (got {Show(after)})");

        // CastCancel before the end: nothing charged or finished; the watcher gets CastCancel {0, 2 counter, 9 0}.
        var (value, stamp) = (me.Energy.Value, me.Energy.Stamp);
        (mark, wmark) = (peer.Events.Count, watcherPeer.Events.Count);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 0, d0 = bolt, d1 = soldier.Id, d4 = 2 });
        arena.Run(0.4);
        Send(session, new CastCancelRequest { wf = world.GameNow });
        arena.Run(1);
        var cancel = Since(watcherPeer, wmark).Where(e => e.Code == EventCodes.CastCancel).ToList();
        Check(!Since(peer, mark).Any(e => e.Code is EventCodes.CastFinished or EventCodes.EnergyUpdate or EventCodes.CastCancel) && me.Combat.Cast is null
              && me.Energy.Value == value && me.Energy.Stamp == stamp
              && cancel.Count == 1 && L(cancel[0].P, 0) == me.Id && Convert.ToByte(cancel[0].P[2]) == 2 && cancel[0].P.ContainsKey(9) && !cancel[0].P.ContainsKey(1),
              $"CastCancel: no CastFinished, no energy charged, nothing to the caster; the watcher gets CastCancel {{2: 2, 9: 0}} (got {Show(cancel)})");

        // 20 s without a hit: the combat ends in the captured order.
        mark = peer.Events.Count;
        arena.Run(20);
        var ends = Since(peer, mark).Where(e => e.Code is EventCodes.InCombatStateUpdate or EventCodes.RegenerationHealthChanged).ToList();
        Check(ends.Select(e => e.Code).SequenceEqual([EventCodes.InCombatStateUpdate, EventCodes.RegenerationHealthChanged, EventCodes.InCombatStateUpdate])
              && ends[0].P[2] is true && F(ends[1].P, 5) > 0 && !me.Combat.InCombat && soldier.TargetId == 0,
              $"combat timeout after 20 s: {{2: true}}, RegenerationHealthChanged with key 5, {{}}; the soldier forgets its target (got {Show(ends)})");
    }

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);
}
