using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// PVE-DESIGN.md §1 (analysis/work/stageH/PVE-DESIGN.md), the PvE preset: the config (Preset pve by default, official = the record's
/// defaults, explicit keys win), Pvp:Mode's relation table, and <see cref="PvpDeathTest.Arena"/> players in 0204 (its own rules) and
/// under black rules: nobody hits a Blue player, Red hits Red, the lethal hit knocks down without loot bag or wear, no execute, no die
/// button, Red in a black zone and Blue again at once.
/// </summary>
public static class PveTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("pve preset: Preset pve by default (Consensual, no deaths, BM 0.15, rates), official = today's defaults, explicit keys win", Config),
        ("pve preset: Pvp:Mode Consensual / Off relations (only Red vs Red, black as red), Official unchanged", Relations),
        ("pve preset: 0204 and black: no hit on Blue, Red on Red knocks down (no bag, no wear), no execute or die button, Red lock 0; Pve:KnockdownWear", Arena),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static void Config()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "albionlocal.json");
        var pve = ServerConfig.Load(path, []);
        Check(pve is { Preset: ServerPreset.Pve, PvpMode: PvpMode.Consensual, PvpLethalDeath: false, PvpFullLoot: false, PvpKnockdownWear: 0,
                  PvpRedLockMinutes: 0, PveKnockdownWear: 0, LootBlackMarketChance: 0.15, PvpDeathSilverDrop: false, SilverPickupTax: 0,
                  OpenWorldChests: true, ChestsPoiLimit: 0 }
              && pve.Rates == new Rates { Fame = 2, Silver = 1.5, Loot = 1, Respawn = 2, Harvest = 1.5, Regrowth = 2, ChampionWeight = 2 },
              "no file: the pve preset (Consensual, no lethal death or full loot, no knockdown wear, no Red lock, BM 0.15, every POI chest, Fame 2 Silver 1.5 Loot 1 Respawn 2 Harvest 1.5 Regrowth 2 ChampionWeight 2)");
        var official = ServerConfig.Load(path, ["--Preset=official"]);
        var record = new ServerConfig();
        Check(official == record with { ContentPath = official.ContentPath, ClientGameDataPath = official.ClientGameDataPath, SavesPath = official.SavesPath, Preset = ServerPreset.Official }
              && official is { PvpMode: PvpMode.Official, PvpLethalDeath: true, PvpFullLoot: true, PvpKnockdownWear: null, PvpRedLockMinutes: null, LootBlackMarketChance: 0.1 }
              && official.Rates == new Rates(), "--Preset=official: exactly the record's (today's) defaults");
        var mixed = ServerConfig.Load(path, ["--Pvp:Mode=Official", "--Pvp:LethalDeath=true", "--Rates:Fame=1", "--Loot:BlackMarketChance=0.2", "--Pvp:RedLockMinutes=spell", "--Pvp:KnockdownWear=gamedata"]);
        Check(mixed is { Preset: ServerPreset.Pve, PvpMode: PvpMode.Official, PvpLethalDeath: true, PvpFullLoot: false, LootBlackMarketChance: 0.2, PvpRedLockMinutes: null, PvpKnockdownWear: null }
              && mixed.Rates is { Fame: 1, Silver: 1.5, Respawn: 2 }, "explicit keys win over the preset, the others keep its values");
        File.WriteAllText(path, """{ "Preset": "official", "Pvp": { "Mode": "Consensual", "KnockdownWear": 0.02 }, "Pve": { "KnockdownWear": 0.01 } }""");
        Check(ServerConfig.Load(path, []) is { Preset: ServerPreset.Official, PvpMode: PvpMode.Consensual, PvpLethalDeath: true, PvpKnockdownWear: 0.02, PveKnockdownWear: 0.01, LootBlackMarketChance: 0.1 },
              "the file's root Preset official with its own Pvp:Mode and wear keys");
        foreach (var bad in new[] { "--Preset=pvp", "--Pvp:Mode=Friendly", "--Pvp:KnockdownWear=2", "--Pve:KnockdownWear=-1" })
        {
            try { ServerConfig.Load(path, [bad]); Check(false, $"{bad} accepted"); }
            catch (InvalidDataException e) { Check(e.Message.Contains(bad[2..bad.IndexOf('=')]), $"{bad} refused, naming its key"); }
        }
        Check(ServerConfig.Load(Path.Combine(DataDir(), "..", "config", "albionlocal.json"), []).Preset == ServerPreset.Pve, "config/albionlocal.json: Preset pve");
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void Relations()
    {
        const byte blue = Pvp.Blue, red = Pvp.Red, forest = 2, swamp = 5;
        var redZone = new ClusterPvp(PvpRule.PvpAllowed, PlayersAreLethal: true, WarningType: "red");
        var black = new ClusterPvp(PvpRule.PvpForced, PlayersAreLethal: true, WarningType: "black");
        var green = new ClusterPvp(PvpRule.FactionPvpOnly, PlayersAreLethal: false);
        Relation R(byte a, byte b, ClusterPvp rules, PvpMode mode, bool party = false) => Pvp.ModeOf(a, b, party, guild: false, rules, mode);
        Check(R(red, red, redZone, PvpMode.Consensual) == Relation.Hostile && R(red, red, black, PvpMode.Consensual) == Relation.Hostile
              && R(red, red, black, PvpMode.Consensual, party: true) == Relation.Friendly, "Consensual: Red vs Red Hostile (red and black), a party Friendly");
        Check(R(red, blue, redZone, PvpMode.Consensual) == Relation.Neutral && R(blue, red, redZone, PvpMode.Consensual) == Relation.Neutral
              && R(blue, blue, black, PvpMode.Consensual) != Relation.Hostile && R(forest, swamp, green, PvpMode.Consensual) == Relation.Neutral
              && R(forest, red, redZone, PvpMode.Consensual) == Relation.Neutral, "Consensual: nobody else Hostile (Red on Blue, black Blue on Blue, factions)");
        Check(R(red, red, redZone, PvpMode.Off) == Relation.Neutral && R(blue, blue, black, PvpMode.Off) == Relation.Neutral, "Off: nothing Hostile");
        Check(R(blue, blue, black, PvpMode.Official) == Relation.Hostile && R(red, blue, redZone, PvpMode.Official) == Relation.Hostile
              && R(forest, swamp, green, PvpMode.Official) == Relation.Hostile, "Official: the client's table unchanged");
    }

    private static void Arena()
    {
        static ServerConfig Pve(ServerConfig c) => ServerConfig.PveDefaults with { JoinByCharacterId = true, PvpRespawnCluster = c.PvpRespawnCluster, Bots = new() }; // no bots
        var zone = Client.Clusters!.Get("0204").Pvp;
        using (var arena = new PvpDeathTest.Arena(zone, Pve))
        {
            var a = arena.Join("Ann", 5, ch => ch.Flag = Pvp.Red);
            var b = arena.Join("Bob", 7);
            Check(arena.Attack(a, b) == WorldSession.AttackInvalidTarget && Pvp.Of(a.Player, b.Player) != Relation.Hostile,
                  $"0204 ({zone.Rule}{(zone.PlayersAreLethal ? ", lethal" : "")}): Red A cannot hit Blue B");
        }
        var black = new ClusterPvp(PvpRule.PvpForced, PlayersAreLethal: true, WarningType: "black");
        using (var arena = new PvpDeathTest.Arena(black, Pve))
        {
            var world = arena.World;
            var a = arena.Join("Ann", 5);
            var b = arena.Join("Bob", 7, ch => ch.Inventory.Add(new InventoryItem("T6_ORE", 30)));
            Check(arena.Attack(a, b) == WorldSession.AttackInvalidTarget, "black zone: Blue A cannot hit Blue B");
            Check(arena.Flag(a, Pvp.Red) == 0 && arena.Flag(b, Pvp.Red) == 0, "black zone: the Red flag is allowed (a black zone counts as red)");
            arena.Run(15.2);
            Check(a.Player.Flag == Pvp.Red && b.Player.Flag == Pvp.Red && !Pvp.SpellActive(a.Player, "CANNOTDISABLEPVP", world.GameNow),
                  "both Red after 15 s, without CANNOTDISABLEPVP (Pvp:RedLockMinutes 0)");
            var worn = b.Player.Inventory.Equipment.Items.Concat(b.Player.Inventory.Bag.Items).Select(i => (i.Template.Name, i.DurabilityRaw)).ToList();
            arena.Weaken(b);
            Check(arena.Attack(a, b) is null, "Red A on Red B: accepted");
            arena.Run(1);
            Check(b.Player.Combat.Down is { Kind: KnockdownKind.Pvp } && !arena.Map.Objects.Values.OfType<PlayerLootBag>().Any()
                  && b.Player.Inventory.Equipment.Items.Concat(b.Player.Inventory.Bag.Items).Select(i => (i.Template.Name, i.DurabilityRaw)).SequenceEqual(worn),
                  "the lethal hit of a lethal zone knocks down (Pvp:LethalDeath false): no death, no loot bag, every item kept without wear (Pvp:KnockdownWear 0)");
            Check(arena.Execute(a, b) == WorldSession.CastRefused, "no execute (PLAYER_COUPDEGRACE) on the knocked-down player");
            arena.Run(4);
            lock (world.Sync) Check(Death.Suicide(b.Player) is not null && b.Player.Combat.Down is { Kind: KnockdownKind.Pvp }, "no die button");
            lock (world.Sync) Combat.EndCombat(a.Player);
            Check(arena.Flag(a, Pvp.Blue) == 0, "Red A asks for Blue at once: accepted (no 15 min lock)");
            arena.Run(15.2);
            Check(a.Player.Flag == Pvp.Blue, "Blue again after FLAG_BLUE's cast");
        }
        using (var arena = new PvpDeathTest.Arena(zone, c => Pve(c) with { PveKnockdownWear = 0.05 }))
        {
            var world = arena.World;
            var a = arena.Join("Ann", 5);
            var sword = a.Player.Inventory.Equipment.Items.First(i => i.Template.Name == "T4_MAIN_SWORD");
            var before = sword.DurabilityRaw;
            var mob = arena.Map.Objects.Values.OfType<Mob>().First(m => !m.Dead);
            lock (world.Sync) Combat.DamagePlayer(mob, a.Player, 1e9, EffectType.Physical, HitOrigin.Direct, -1, Math.Max(world.GameNow, a.Player.Health.Stamp));
            Check(a.Player.Combat.Down is { Kind: KnockdownKind.Pve } && before - sword.DurabilityRaw == (long)Math.Round(sword.Template.FullDurabilityRaw * 0.05),
                  $"Pve:KnockdownWear 0.05: a mob's knockdown takes 5 % of the sword's durability ({before} -> {sword.DurabilityRaw})");
        }
    }
}
