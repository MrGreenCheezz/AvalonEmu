using System.Numerics;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// StageJ armour (analysis/work/stageJ/armour/SPEC.md §7): the worn items' armour, magic and crowd control resistance and defence
/// with their item power (bej.a_5), a captured fight's hits on a cloth wearer, and the &lt;ActiveBuffScaling&gt; clamp. Light: the
/// numbers the SPEC lists.
/// </summary>
public static class DefenseTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("armour: T4 plate set 1 / T4 shield / head and shoes at IP 700 (bej.a_5), a naked player takes the whole hit", Items),
        ("armour: the captured hits on T4 cloth set 2 q4 at IP 865.4 with the head and shoe passives (431 → 200, 417 → 194, 55 → 26, magic 133 → 56)", Capture),
        ("armour: <ActiveBuffScaling> clamp (121 armour −43.4 → 77.6, −200 → 48.4; naked −50 → 0)", Clamp),
        ("damage: the worn spell damage bonus (T4 cloth set 2 0.45 + two passives at AP 250.64 on MR 121: FIRESTAFFBOLT2 109, tick 7, FLAMEDASH 312 / 37; autos and Combat:GearDamageBonus false: 77; an off hand × 1.16^(IP/100))", WornDamage),
        ("damage: a spawned T7 gatherer on 0221 at Bots:Gear:Mastery 1 vs the pve preset (Mastery 0.5, HealthFactor 0.7, no worn bonus on bot spells): less HP, armour and AP, a VAULT_ATTACK at AP 313.4 takes a larger share; the cast path adds the player's worn bonus", BotStrength),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static bool Near(double a, double b, double eps) => Math.Abs(a - b) <= eps;

    private static void Items()
    {
        var data = Client.GameData!;
        var calc = new StatCalculator(data);
        ItemData I(string name) => data.Item(data.Find(name)) ?? throw new Exception($"{name} not in items.xml");
        var plate = calc.Defense([(I("T4_ARMOR_PLATE_SET1"), 700)]);
        Check(Near(plate.Armor, 198.86, 0.01) && Near(plate.MagicResistance, 160.73, 0.01) && Near(plate.Ccr, 105.55, 0.01) && plate.DefenseVsMobs == 0,
              $"T4_ARMOR_PLATE_SET1 at IP 700: armour 198.86, MR 160.73, CCR 105.55 (got {plate})");
        var shield = calc.Defense([(I("T4_OFF_SHIELD"), 700)]);
        Check(shield.Armor == 0 && Near(shield.Ccr, 28.09, 0.01) && Near(shield.DefenseVsMobs, 0.030523, 1e-5) && Near(shield.DefenseVsPlayers, 0.030523, 1e-5),
              $"T4_OFF_SHIELD at IP 700: armour 0, CCR 28.09 (ccrshare 0.6), defence vs mobs / players 0.030523 (got {shield})");
        var rest = calc.Defense([(I("T4_HEAD_PLATE_SET1"), 700), (I("T4_SHOES_PLATE_SET1"), 700)]);
        Check(rest == DefenseStats.None, $"head and shoes add nothing (got {rest})");

        using var arena = new Arena();
        arena.Setup = c => { c.Equipment[2] = null; c.Equipment[3] = null; c.Equipment[4] = null; };
        var (_, _, me) = arena.Join("Naked", new InventoryItem("T4_MAIN_SWORD") { Passive = [] });
        Check(me.Defense == DefenseStats.None && Combat.ResistanceOf(me, EffectType.Physical) == 1 && Combat.ResistanceOf(me, EffectType.Magic) == 1,
              $"a naked player (sword only): no defence, R = 1 (got {me.Defense})");
    }

    private static void Capture()
    {
        using var arena = new Arena();
        var items = Client.Items;
        // Any T4 head with PASSIVE_BALANCE and shoes with PASSIVE_MR_AR in their lists: neither adds armour (SPEC §2.7).
        string With(string slot, string passive) =>
            items.All.First(t => t.Tier == 4 && t.Slot == slot && !t.Name.Contains('@') && t.SpellLists.Any(l => l.Contains(passive))).Name;
        arena.Setup = c =>
        {
            c.Equipment[2] = new InventoryItem(With("head", "PASSIVE_BALANCE")) { Passive = ["PASSIVE_BALANCE"] };
            c.Equipment[3] = new InventoryItem("T4_ARMOR_CLOTH_SET2") { Quality = 4, Passive = [] };
            c.Equipment[4] = new InventoryItem(With("shoes", "PASSIVE_MR_AR")) { Passive = ["PASSIVE_MR_AR"] };
        };
        // 700 + 60 (q4) + 0.2 × 10 (COMBAT_CLOTH_ARMORS) + (0.2 + 2) × 47 (COMBAT_CLOTH_ARMORS_SET2) = 865.4, session B's item power.
        var (_, _, me) = arena.Join("Scholar", new InventoryItem("T4_MAIN_SWORD") { Passive = [] }, ("COMBAT_CLOTH_ARMORS", 10), ("COMBAT_CLOTH_ARMORS_SET2", 47));
        var world = arena.World;
        var ip = world.StatCalculator!.ItemPower(me.Inventory.Equipment[3]!.ForStats(), me.Progression!.Levels);
        Check(Near(ip, 865.4, 0.01) && Near(me.Defense.Armor, 107.93, 0.01) && Near(me.Defense.MagicResistance, 129.89, 0.01),
              $"cloth set 2 q4 at IP 865.4: armour 107.93, MR 129.89 (got IP {ip:0.##}, {me.Defense})");
        lock (world.Sync)
        {
            var now = world.GameNow;
            int Hit(double attack, EffectType type) =>
                (int)Combat.Rounded(attack * Combat.ResistanceOf(me, type) * SpellEffects.TakenFactor(me, fromPlayer: false, now, me.Defense.DefenseVsMobs));
            var hits = (Hit(431, EffectType.Physical), Hit(417, EffectType.Physical), Hit(55, EffectType.Physical), Hit(133, EffectType.Magic));
            Check(hits == (200, 194, 26, 56) && Near(me.Effects.Sum("bonusdefensevsmobs", now), 0.035, 1e-9),
                  $"the captured hits: 200, 194, 26, magic 56 with Σ bonusdefensevsmobs 0.035 (got {hits}, Σ {me.Effects.Sum("bonusdefensevsmobs", now):0.####})");
        }
    }

    /// <summary>
    /// stageM pvp-damage NOTES §3: session B's fire-staff player (key-2 AP 250.64, T4_ARMOR_CLOTH_SET2 with PASSIVE_ARMOR_INCREASED_DAMAGE
    /// 0.08, a head with PASSIVE_BALANCE 0.015) hit T4 mobs of magic resistance 121 for 109 per FIRESTAFFBOLT2, 7 per tick, 312 per
    /// FLAMEDASH_EXPLOSION_DAMAGE and 37 per FLAMEDASH_PASSTHROUGH_DAMAGE: × (1 + 0.45 + 0.08 + 0.015) = 1.545.
    /// </summary>
    private static void WornDamage()
    {
        var items = Client.Items;
        string With(string slot, string passive) =>
            items.All.First(t => t.Tier == 4 && t.Slot == slot && !t.Name.Contains('@') && t.SpellLists.Any(l => l.Contains(passive))).Name;
        foreach (var gear in new[] { true, false })
        {
            using var arena = new Arena(config: new ServerConfig { JoinByCharacterId = true, GearDamageBonus = gear });
            arena.Setup = c =>
            {
                c.Equipment[2] = new InventoryItem(With("head", "PASSIVE_BALANCE")) { Passive = ["PASSIVE_BALANCE"] };
                c.Equipment[3] = new InventoryItem("T4_ARMOR_CLOTH_SET2") { Passive = ["PASSIVE_ARMOR_INCREASED_DAMAGE"] };
            };
            var (_, _, me) = arena.Join(gear ? "Pyro" : "PyroAB", new InventoryItem("T4_MAIN_FIRESTAFF") { Passive = [] });
            var world = arena.World;
            lock (world.Sync)
            {
                var now = world.GameNow;
                var spell = SpellEffects.DealtFactor(me, spell: true, EffectType.Magic, vsPlayer: false, now);
                var auto = SpellEffects.DealtFactor(me, spell: false, EffectType.Magic, vsPlayer: false, now);
                var r = Combat.Resistance(121);
                int Hit(double change) => (int)Combat.Rounded(Combat.SpellChange(change, 250.64) * spell * r);
                var hits = (Hit(62), Hit(4), Hit(178), Hit(21));
                if (gear)
                    Check(Near(spell, 1.545, 1e-6) && Near(auto, 1.095, 1e-6) && hits == (109, 7, 312, 37),
                          $"worn 0.45 + passives 0.095: spells × 1.545 (bolt 109, tick 7, FLAMEDASH 312 / 37), autos × 1.095 (got {spell:0.####}, {auto:0.####}, {hits})");
                else
                    Check(Near(spell, 1.095, 1e-6) && hits.Item1 == 77,
                          $"Combat:GearDamageBonus false: spells × 1.095, the bolt 77 as before (got {spell:0.####}, {hits})");
            }
            if (!gear) continue;
            // An off hand's bonus × offhand's progression 1.16^(IP/100): T4_OFF_DEMONSKULL_HELL 0.0161 at its IP 750 (no destiny levels).
            lock (world.Sync)
            {
                var skull = items.Find("T4_OFF_DEMONSKULL_HELL")!;
                var off = new Item(world.ObjectIds.Next(), skull, 1, 1, skull.FullDurabilityRaw, [], []);
                me.Inventory.Equipment.Put(1, off);
                var (worn, ip) = (Combat.Worn(me, "magicspelldamagebonus"), Combat.ItemPower(me, off));
                Check(Near(ip, 750, 1e-6) && Near(worn, 0.45 + 0.0161 * Math.Pow(1.16, 7.5), 1e-6),
                      $"with T4_OFF_DEMONSKULL_HELL at IP 750: 0.45 + 0.0161 × 1.16^7.5 = {0.45 + 0.0161 * Math.Pow(1.16, 7.5):0.####} (got IP {ip}, {worn:0.####})");
            }
        }
    }

    /// <summary>
    /// The user's case (stageM pvp-damage NOTES §1, §5.2b): a T4 quarterstaff at AP 313.4 against a T7 gatherer bot on the T6 red map 0221.
    /// Through the real BotManager.Spawn and SpellCasts.Execute: Bots:Gear:Mastery 1 (official-like, the unlock-all board) against the
    /// old pve bot keys (Mastery 0.5, HealthFactor 0.7, Gear:DamageBonus false; since stageN botpower the pve preset's companions only, its
    /// world bots full like the official preset's); the presets resolve to these.
    /// </summary>
    private static void BotStrength()
    {
        var dir = NewDir();
        BotOptions Preset(string name)
        {
            var file = Path.Combine(dir, name + ".json");
            File.WriteAllText(file, $$"""{ "Preset": "{{name}}" }""");
            return LoadTestConfig(file, []).Bots;
        }
        var (official, pve) = (Preset("official"), Preset("pve"));
        var companions = ServerConfig.PveDefaults.Companions;
        Check(official is { GearMastery: 1, HealthFactor: 1, GearDamageBonus: true, Difficulty: 1 } && pve is { GearMastery: 1, HealthFactor: 1, GearDamageBonus: true, Difficulty: 1 }
              && companions is { GearMastery: 0.5, GearDamageBonus: false } && new CompanionOptions() is { GearMastery: 1, GearDamageBonus: true },
              $"presets (stageN botpower): bots Mastery 1 / HealthFactor 1 / DamageBonus true / Difficulty 1 in both (got pve {pve.GearMastery}/{pve.HealthFactor}/{pve.GearDamageBonus}/{pve.Difficulty}); pve companions 0.5 / false");
        pve = new BotOptions { GearMastery = 0.5, HealthFactor = 0.7, GearDamageBonus = false }; // the old soft pve bots

        var rules = new ClusterPvp(PvpRule.PvpAllowed, PlayersAreLethal: false, WarningType: "yellow");
        var vault = Client.Spells!.Template("VAULT_ATTACK_EFFECT")!;
        var cloth = Client.Items.Find("T7_ARMOR_CLOTH_SET3")!;
        var plate3 = Client.Items.Find("T4_ARMOR_PLATE_SET3")!;
        (float Hp, double Armor, double Ap, float Vault, double ClothGain, float VaultNoChestBonus) Fight(BotOptions o)
        {
            o = o with { Enabled = true, JoinGraceSeconds = 0, RouteDwellSeconds = (300, 300), RouteEndDwellSeconds = (300, 300), GathererFlag = BotGathererFlag.Red };
            using var f = new BotTest.Field(o, BotTest.NoPopulation, rules, cluster: "0221");
            var at = f.Nav().Exits[0].Arrival;
            var (_, _, me) = f.Join("Rhea", at + new Vector2(2, 0), Pvp.Red);
            lock (f.World.Sync)
            {
                var world = f.World;
                var bot = world.Bots.Spawn(f.Map, BotRole.Gatherer, 7, at, 0) ?? throw new InvalidOperationException("no T7 gatherer");
                if (bot.IsMounted) Mounts.Dismount(bot);
                float Hit()
                {
                    bot.Health.Set(bot.Health.Max, world.GameNow);
                    bot.Effects.Clear();
                    var (t, before) = (world.GameNow, bot.Health.At(world.GameNow));
                    SpellCasts.Execute(new SpellCasts.SpellContext(me, me.Map!, vault, 1, 313.4, bot, SpellMovement.PositionOf(bot, t), t), vault);
                    return before - bot.Health.At(world.GameNow);
                }
                var hit = Hit();
                // The starter kit's T4 plate set 1 chest (spell damage bonus 0.1) for one without any (set 3): the cast path's worn term.
                var chest = me.Inventory.Equipment.Put(3, new Item(world.ObjectIds.Next(), plate3, 1, 1, plate3.FullDurabilityRaw, [], []));
                var plain = Hit();
                me.Inventory.Equipment.Put(3, chest);
                // A cloth set 3 chest (magic spell damage bonus 0.5) on the bot: its spells gain it only with Bots:Gear:DamageBonus.
                var now = world.GameNow;
                var before = SpellEffects.DealtFactor(bot, spell: true, EffectType.Magic, vsPlayer: true, now);
                bot.Inventory.Equipment.Put(3, new Item(world.ObjectIds.Next(), cloth, 1, 1, cloth.FullDurabilityRaw, [], []));
                var gain = SpellEffects.DealtFactor(bot, spell: true, EffectType.Magic, vsPlayer: true, now) - before;
                return (bot.Health.Max, bot.Defense.Armor, bot.Inventory.Equipment.Items.Max(i => Combat.AbilityPowerOf(bot, i)), hit, gain, plain);
            }
        }
        var full = Fight(new BotOptions());
        var soft = Fight(pve);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Check(full.Hp is > 2400 and < 3100 && full.Vault > 0 && full.ClothGain > 0.45,
              string.Create(inv, $"Mastery 1: a T7 gatherer of 2400-3100 HP (got {full.Hp}, armour {full.Armor:0.0}, AP {full.Ap:0.0}), VAULT {full.Vault} = {100 * full.Vault / full.Hp:0.0} %, a cloth chest adds {full.ClothGain:0.###} to its spells"));
        Check(soft.Hp < full.Hp * 0.7 && soft.Armor < full.Armor && soft.Ap < full.Ap && soft.Vault > full.Vault && soft.Vault / soft.Hp > 1.5 * full.Vault / full.Hp
              && Math.Abs(soft.ClothGain) < 1e-9,
              string.Create(inv, $"pve bots: HP {soft.Hp} (< 0.7 × {full.Hp}), armour {soft.Armor:0.0}, AP {soft.Ap:0.0}, VAULT {soft.Vault} = {100 * soft.Vault / soft.Hp:0.0} % (> 1.5 × {100 * full.Vault / full.Hp:0.0} %), no cloth gain on bot spells (got {soft.ClothGain:0.###})"));
        Check((double)full.Vault / full.VaultNoChestBonus is > 1.07 and < 1.11,
              string.Create(inv, $"SpellCasts.Execute adds the player's worn plate set 1 bonus (0.1): VAULT {full.Vault} vs {full.VaultNoChestBonus} in set 3"));
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Clamp()
    {
        var data = Client.GameData!;
        var mob = Combat.Limit(data, "physicalarmor", mob: true);
        var player = Combat.Limit(data, "PhysicalArmor", mob: false);
        Check(mob == BuffLimit.Resistance && player == BuffLimit.Resistance && Combat.Limit(data, "crowdcontrolresistance", mob: true) == BuffLimit.Resistance,
              $"PhysicalArmor / CrowdControlResistance: min −0.6, max unrestricted, absolutemin 100, no <mobs> row (got {mob}, {player})");
        // A row only the parser supplies (not in DefaultBuffLimits): the section was read, not the fallback.
        var hp = data.BuffLimitOf("HitpointsMax");
        Check(!ReferenceEquals(data.BuffLimits, GameData.DefaultBuffLimits) && hp.Min == -0.5 && double.IsPositiveInfinity(hp.Max)
              && hp.AbsoluteMin == 1000 && hp.Mobs?.Min == -0.25,
              $"HitpointsMax from gamedata.xml: min −0.5, max unrestricted, absolutemin 1000, <mobs> min −0.25 (got {hp}, mobs {hp.Mobs})");
        Check(Near(Combat.Buffed(121, -43.4, mob), 77.6, 0.1) && Near(Combat.Buffed(121, -200, mob), 48.4, 0.1)
              && Combat.Resistance(Math.Max(0, Combat.Buffed(0, -50, player))) == 1,
              $"121 armour: −43.4 points → 77.6, −200 → 48.4 (−0.6 × 121); naked −50 → R 1 (got {Combat.Buffed(121, -43.4, mob):0.#}, {Combat.Buffed(121, -200, mob):0.#}, {Combat.Buffed(0, -50, player)})");
    }
}
