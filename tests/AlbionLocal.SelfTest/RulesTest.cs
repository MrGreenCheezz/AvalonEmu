using System.Numerics;
using AlbionLocal.Photon.Serialization;
using AlbionLocal.Server;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;
using static AlbionLocal.SelfTest.AlbionTests;
using static AlbionLocal.SelfTest.CombatTest;

namespace AlbionLocal.SelfTest;

/// <summary>
/// The no-code rules (server/plugins/RULES.md), light: a kill rule gives an item once and a broken rule beside it is reported and left out;
/// a damage rule changes a spell's hit; a custom command with a cooldown answers once and refuses the second use.
/// </summary>
public static class RulesTest
{
    public static IEnumerable<(string, Action)> All =>
    [
        ("rules: mob.died of the ghoul with chance 100 % gives 2 T4_ORE to the killer once; an invalid rule of the same pack is reported and not loaded", KillDropAndInvalid),
        ("rules: spell.damage of AXESMASH with modify set 77: the ghoul loses exactly 77", DamageModify),
        ("rules: a .gift command rule with a 600 s cooldown gives silver once, the second use is refused with the time left; the simulation sees the cooldown", CommandCooldown),
    ];

    private static void Check(bool cond, string what) => AlbionTests.Check(cond, what);

    /// <summary>An arena whose world reads the rules of pack "rt" (written into a temp mods/ folder).</summary>
    private static Arena RulesArena(params (string Id, string Json)[] rules)
    {
        var dir = NewDir();
        var folder = Path.Combine(dir, "mods", "rt", "rules");
        Directory.CreateDirectory(folder);
        foreach (var (id, json) in rules) File.WriteAllText(Path.Combine(folder, id + ".json"), json);
        return new Arena(config: new ServerConfig { JoinByCharacterId = true, ModsPath = Path.Combine(dir, "mods"), ModPacks = ["rt"], SavesPath = dir });
    }

    private static int Count(Player p, string item) => p.Inventory.Bag.Items.Where(i => i.Template.Name == item).Sum(i => i.Count);

    private static void KillDropAndInvalid()
    {
        using var arena = RulesArena(
            ("ghoul_drop", """
                { "schema": 1, "id": "ghoul_drop", "name": "Ghouls drop ore", "trigger": { "type": "mob.died", "id": "T4_MOB_ROAMING_UNDEAD_GHOUL" },
                  "conditions": [ { "type": "chance", "params": { "percent": 100 } } ],
                  "actions": [ { "type": "give.item", "target": "killer", "params": { "item": "T4_ORE", "count": 2 } } ] }
                """),
            ("broken", """
                { "id": "broken", "trigger": { "type": "mob.died" }, "actions": [ { "type": "explode" }, { "type": "give.item", "params": { "count": 1 } } ] }
                """));
        var world = arena.World;
        var (_, _, me) = arena.Join("Ruler", new InventoryItem("T4_MAIN_AXE"));
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        var rules = world.Plugins.Rules;
        lock (world.Sync) rules.Reload();
        var problems = rules.Problems.Where(p => p.Rule == "broken" && p.Blocker).Select(p => p.Path + " " + p.Message).ToList();
        Check(rules.Rules.Count == 1 && rules.Rules[0].Id == "ghoul_drop" && world.Plugins.IsLoaded("rules"),
              $"one rule loads, as the built-in plugin 'rules' ({rules.Summary()})");
        Check(problems.Any(p => p.StartsWith("/actions/0/type", StringComparison.Ordinal) && p.Contains("explode", StringComparison.Ordinal))
              && problems.Any(p => p.StartsWith("/actions/1/params/item", StringComparison.Ordinal)),
              $"the broken rule is reported with its unknown action and missing item, and not loaded (got: {string.Join(" | ", problems)})");
        var before = Count(me, "T4_ORE");
        lock (world.Sync) Combat.Damage(me, ghoul, 1e9, EffectType.Physical, HitOrigin.Direct, 0, world.GameNow);
        Check(ghoul.Dead && Count(me, "T4_ORE") == before + 2 && rules.Rules[0].Fires == 1,
              $"the kill fires the rule once: 2 T4_ORE more for the killer (ore {before} → {Count(me, "T4_ORE")}, fires {rules.Rules[0].Fires})");
        world.Plugins.Shutdown();
    }

    private static void DamageModify()
    {
        using var arena = RulesArena(("axe_77", """
            { "id": "axe_77", "trigger": { "type": "spell.damage", "id": "AXESMASH" },
              "conditions": [ { "type": "flag", "not": true, "params": { "flag": "heal" } }, { "type": "unit.kind", "target": "target", "params": { "kind": "mob" } } ],
              "actions": [ { "type": "modify", "params": { "op": "set", "value": 77 } } ] }
            """));
        var world = arena.World;
        arena.Setup = c => c.Equipment[3] = new InventoryItem("T4_ARMOR_PLATE_SET3");
        var (session, peer, me) = arena.Join("Smasher", new InventoryItem("T4_MAIN_AXE") { Active = ["RENDINGSTRIKE", "AXESMASH", "AXETHROW"], Passive = [] },
            ("COMBAT_AXES_AXE", 100), ("COMBAT_AXES", 100), ("COMBAT_AXES_GREAT", 25));
        arena.Setup = null;
        var ghoul = arena.Spawn("T4_MOB_ROAMING_UNDEAD_GHOUL", new Vector2(1.5f, 0));
        lock (world.Sync) world.Plugins.Rules.Reload();
        Check(world.Plugins.Rules.Rules.Count == 1, $"the rule loads ({world.Plugins.Rules.Summary()}; {string.Join(" | ", world.Plugins.Rules.Problems)})");
        var axesmash = Client.Spells!.IdOf("AXESMASH");
        var mark = peer.Events.Count;
        var before = ghoul.Health.At(world.GameNow);
        Send(session, new CastStartRequest { wf = world.GameNow, dx = 1, d0 = axesmash, d1 = ghoul.Id, d4 = 1 });
        arena.Run(0.5);
        var hit = Since(peer, mark).FirstOrDefault(e => e.Code == EventCodes.HealthUpdate && L(e.P, 0) == ghoul.Id && Convert.ToInt16(e.P[7]) == axesmash).P;
        Check(hit is not null && F(hit, 2) == -77 && Math.Abs(ghoul.Health.At(world.GameNow) - (before - 77)) < 0.5,
              $"AXESMASH's hit is 77 in the HealthUpdate and on the ghoul (got {(hit is null ? "none" : F(hit, 2))}, health {before} → {ghoul.Health.At(world.GameNow)})");
        world.Plugins.Shutdown();
    }

    private static List<string> Lines(FakePeer peer, int from) =>
        [.. peer.Events.Skip(from).Where(e => Convert.ToInt32(e.Parameters[ReservedKeys.EventCode]) == (int)EventCodes.SystemMessage).Select(e => e.Parameters[0] as string ?? "")];

    private static void CommandCooldown()
    {
        using var arena = RulesArena(("gift", """
            { "id": "gift", "trigger": { "type": "command", "params": { "command": "gift", "help": "a small gift" } }, "cooldown": { "seconds": 600, "per": "player" },
              "actions": [ { "type": "silver", "params": { "amount": 100 } }, { "type": "message", "params": { "text": "A gift for {player}!" } } ] }
            """));
        var world = arena.World;
        var (session, peer, me) = arena.Join("Gifted", new InventoryItem("T4_MAIN_AXE"));
        lock (world.Sync) world.Plugins.Rules.Reload();
        var silver = me.Character.Silver;
        var mark = peer.Events.Count;
        session.OnRequest(Request(new SayRequest { aa9 = ".gift" }, null), encrypted: false);
        var first = Lines(peer, mark);
        Check(me.Character.Silver == silver + 100 * 10000 && first.Contains("A gift for Gifted!"),
              $".gift gives 100 silver and the message (silver {silver} → {me.Character.Silver}; lines: {string.Join(" | ", first)})");
        mark = peer.Events.Count;
        arena.Run(1);
        session.OnRequest(Request(new SayRequest { aa9 = ".gift" }, null), encrypted: false);
        var second = Lines(peer, mark);
        Check(me.Character.Silver == silver + 100 * 10000 && second.Any(l => l.StartsWith("wait 6", StringComparison.Ordinal) || l.StartsWith("wait 59", StringComparison.Ordinal)) && !second.Any(l => l.StartsWith("A gift", StringComparison.Ordinal)),
              $"the second .gift is refused by the cooldown with the time left, no silver (lines: {string.Join(" | ", second)})");
        var json = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(world.Config.ModsPath, "rt", "rules", "gift.json")))!;
        System.Text.Json.Nodes.JsonObject sim;
        lock (world.Sync) sim = world.Plugins.Rules.Simulate(json, "rt", "Gifted");
        Check(sim["wouldFire"]?.GetValue<bool>() == false && sim["blocked"]?.ToString().Contains("cooldown", StringComparison.Ordinal) == true
              && sim["actions"]?.AsArray().Count == 2 && me.Character.Silver == silver + 100 * 10000,
              $"the simulation says the cooldown blocks it, describes both actions and changes nothing ({sim.ToJsonString()})");
        world.Plugins.Shutdown();
    }
}
