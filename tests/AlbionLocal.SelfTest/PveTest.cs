using AlbionLocal.Server;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Content;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// PVE-DESIGN.md §1 (analysis/work/stageH/PVE-DESIGN.md), the PvE preset: the config (no Preset key = official, the record's
/// defaults, explicit keys win), Pvp:Mode's relation table, and <see cref="PvpDeathTest.Arena"/> players in 0204 (its own rules) and
/// under black rules: nobody hits a Blue player, Red hits Red, the lethal hit knocks down without loot bag or wear, no execute, no die
/// button, Red in a black zone and Blue again at once.
/// </summary>
public static class PveTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("pve preset: no Preset key = official; Preset pve (Consensual, no deaths, BM 0.15, rates), official = today's defaults, explicit keys win", Config),
        ("official audit: the content keys, arena, loot, roads, corrupted and island keys per preset; content/*.json only where a key allows it; .who/.save GM-only", OfficialAudit),
        ("pve preset: Pvp:Mode Consensual / Off relations (only Red vs Red, black as red), Official unchanged", Relations),
        ("pve preset: 0204 and black: no hit on Blue, Red on Red knocks down (no bag, no wear), no execute or die button, Red lock 0; Pve:KnockdownWear", Arena),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    private static void Config()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "albionlocal.json");
        Check(LoadTestConfig(path, []).Preset == ServerPreset.Official, "no Preset key: the official preset");
        var pve = LoadTestConfig(path, ["--Preset=pve"]);
        Check(pve is { Preset: ServerPreset.Pve, PvpMode: PvpMode.Consensual, PvpLethalDeath: false, PvpFullLoot: false, PvpKnockdownWear: 0,
                  PvpRedLockMinutes: 0, PveKnockdownWear: 0, LootBlackMarketChance: 0.15, PvpDeathSilverDrop: false, SilverPickupTax: 0,
                  OpenWorldChests: true, ChestsPoiLimit: 0 }
              && pve.Rates == new Rates { Fame = 2, Silver = 1.5, Loot = 1, Respawn = 2, Harvest = 1.5, Regrowth = 2, ChampionWeight = 2 },
              "Preset pve: Consensual, no lethal death or full loot, no knockdown wear, no Red lock, BM 0.15, every POI chest, Fame 2 Silver 1.5 Loot 1 Respawn 2 Harvest 1.5 Regrowth 2 ChampionWeight 2");
        var official = LoadTestConfig(path, ["--Preset=official"]);
        var record = new ServerConfig();
        Check(official == record with { ContentPath = official.ContentPath, ClientGameDataPath = official.ClientGameDataPath, SavesPath = official.SavesPath, Preset = ServerPreset.Official, TutorialOpening = true }
              && official is { PvpMode: PvpMode.Official, PvpLethalDeath: true, PvpFullLoot: true, PvpKnockdownWear: null, PvpRedLockMinutes: null, LootBlackMarketChance: 0.1 }
              && official.Rates == new Rates(), "--Preset=official: exactly the record's (today's) defaults");
        var mixed = LoadTestConfig(path, ["--Preset=pve", "--Pvp:Mode=Official", "--Pvp:LethalDeath=true", "--Rates:Fame=1", "--Loot:BlackMarketChance=0.2", "--Pvp:RedLockMinutes=spell", "--Pvp:KnockdownWear=gamedata"]);
        Check(mixed is { Preset: ServerPreset.Pve, PvpMode: PvpMode.Official, PvpLethalDeath: true, PvpFullLoot: false, LootBlackMarketChance: 0.2, PvpRedLockMinutes: null, PvpKnockdownWear: null }
              && mixed.Rates is { Fame: 1, Silver: 1.5, Respawn: 2 }, "explicit keys win over the preset, the others keep its values");
        File.WriteAllText(path, """{ "Preset": "official", "Pvp": { "Mode": "Consensual", "KnockdownWear": 0.02 }, "Pve": { "KnockdownWear": 0.01 } }""");
        Check(LoadTestConfig(path, []) is { Preset: ServerPreset.Official, PvpMode: PvpMode.Consensual, PvpLethalDeath: true, PvpKnockdownWear: 0.02, PveKnockdownWear: 0.01, LootBlackMarketChance: 0.1 },
              "the file's root Preset official with its own Pvp:Mode and wear keys");
        foreach (var bad in new[] { "--Preset=pvp", "--Pvp:Mode=Friendly", "--Pvp:KnockdownWear=2", "--Pve:KnockdownWear=-1" })
        {
            try { LoadTestConfig(path, [bad]); Check(false, $"{bad} accepted"); }
            catch (InvalidDataException e) { Check(e.Message.Contains(bad[2..bad.IndexOf('=')]), $"{bad} refused, naming its key"); }
        }
        Check(ServerConfig.Load(Path.Combine(DataDir(), "..", "config"), []).Preset == ServerPreset.Official, "shipped area configuration: Preset official");
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private static void OfficialAudit()
    {
        var path = Path.Combine(NewDir(), "none.json");
        var o = LoadTestConfig(path, []);
        var p = LoadTestConfig(path, ["--Preset=pve"]);
        Check(o is { EventsPlayerInvasions: false, BountiesEnabled: false, PvpGoalsEnabled: false, DungeonsDailyBonus: 1, DungeonsGroupScaling: false,
                  GuildBossSeasonPoints: false, GuildSeasonRewards: RuleSource.Official, SeasonRules: RuleSource.Official, ExpeditionsRules: RuleSource.Official,
                  WorldBossesExtraSpawns: false, WorldBossesLoot: BossLoot.Killer, LootTierCap: false, LootBlackMarketSoftCap: 0, IslandsBuyWithoutPremium: true,
                  NewCharacters: NewCharacterProgression.FairStart, TutorialOpening: true, Vanity.Enabled: true,
                  Roads.Rescue: false, Corrupted: { MapsOpenDungeons: true, LeaveCommand: false },
                  Arena: { Bots: false, CountdownSeconds: 15, Tickets2v2: 150, Tickets5v5: 150, TicketsPerDeath: 2, MatchMinutes: 25, DuelsRated: false,
                           WinSilver: 0, LossSilver: 0, WinFame: 0, LossFame: 0 } },
              "official (no Preset key): every server addition off, official arena figures, FairStart, the tutorial, islands still for all (temporary)");
        Check(p is { EventsPlayerInvasions: true, BountiesEnabled: true, PvpGoalsEnabled: true, DungeonsDailyBonus: null, DungeonsGroupScaling: true,
                  GuildBossSeasonPoints: true, GuildSeasonRewards: RuleSource.Content, SeasonRules: RuleSource.Content, ExpeditionsRules: RuleSource.Content,
                  WorldBossesExtraSpawns: true, WorldBossesLoot: null, LootTierCap: true, LootBlackMarketSoftCap: 4, IslandsBuyWithoutPremium: true,
                  NewCharacters: NewCharacterProgression.UnlockAll, Vanity.Enabled: true, Roads.Rescue: true, Corrupted: { MapsOpenDungeons: false, LeaveCommand: true } }
              && p.Arena == ArenaOptions.PveDefaults && p.Arena is { Bots: true, CountdownSeconds: 10, Tickets2v2: 6, Tickets5v5: 15, TicketsPerDeath: 1, MatchMinutes: 15, WinSilver: 5000 },
              "pve: today's behaviour (content decides, the old arena, soft cap 4, tier cap, rescue, maps as tickets, .cd leave)");
        Check(LoadTestConfig(path, ["--Dungeons:DailyBonus=content", "--WorldBosses:Loot=shared", "--Season:Rules=content", "--Islands:BuyWithoutPremium=false"])
                  is { DungeonsDailyBonus: null, WorldBossesLoot: BossLoot.Shared, SeasonRules: RuleSource.Content, IslandsBuyWithoutPremium: false },
              "explicit keys win (content, Shared, Content, false)");

        // The content files' additions apply only where the key allows them.
        var root = Path.Combine(DataDir(), "..", "content");
        var goals = GoalSettings.Load(Path.Combine(root, "goals.json"));
        var events = EventSettings.Load(Path.Combine(root, "events.json"));
        var g = ContentRules.Apply(o, goals);
        var e = ContentRules.Apply(o, events);
        var b = ContentRules.Apply(o, BossSettings.Load(Path.Combine(root, "bosses.json")));
        var dn = ContentRules.Apply(o, DungeonSettings.Load(Path.Combine(root, "dungeons.json")));
        var x = ContentRules.Apply(o, ExpeditionSettings.Load(Path.Combine(root, "expeditions.json")));
        Check(goals.Bounties.Enabled && goals.Season.Pve == 3 && !g.Bounties.Enabled && g.Season is { Enabled: true, Pve: null, Dungeon: null, BossBonus: 0, PointsFactor: 1, GrantPremiumRewards: false, DailyExtra.Count: 0 }
              && g.Pvp is { Enabled: true, KnockdownIsKill: false, KillPoints: 0, KillPointsPerTier: 0, DuelWinPoints: 0 }
              && g.Guild.BossSeasonPoints.Count == 0 && g.Guild.BracketSilver.Count == 0 && g.Guild.BracketItems.Count == 0
              && !e.Player.Enabled && e.Merchant.Prices.Count == 0 && e.Drops is { Boss: 0, Miniboss: 0, Won: 0 }
              && b is { ExtraSpawns.Count: 0, Loot: BossLoot.Killer } && dn is { DailyBonus: 1, GroupScaling: false }
              && x is { SoloGroupCategories: false, PartyAutoAccept: false, HardcorePartyCarry: false, GroupScaling: false, EveryRunItems.Count: 0, LeavePenalty: true },
              "official over the shipped content: no bounties, invasions, tickets, extra bosses, daily bonus or scaling; the client's season, PvP and expedition rules");
        Check(ContentRules.Apply(p, goals) == goals && ContentRules.Apply(p, events) == events,
              "pve: the shipped goals.json and events.json as written");

        // .who and .save: GameMaster.
        Check(CommandRegistry.Default.Commands.Where(c => c.Words is ["who"] or ["save"]).All(c => c.Level == AccountLevel.GameMaster)
              && CommandRegistry.Default.Commands.Count(c => c.Words is ["who"] or ["save"]) == 2, ".who and .save are GameMaster commands");
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
