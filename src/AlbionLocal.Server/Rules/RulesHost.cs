using System.Text.Json;
using System.Text.Json.Nodes;
using AlbionLocal.Photon;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Plugins;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Rules;

/// <summary>
/// The no-code rules of one world (server/plugins/RULES.md): reads mods/&lt;pack&gt;/rules/*.json of every pack in Mods:Packs, checks them
/// (<see cref="RuleParser"/>; a rule with a blocker is reported and left out), and runs the rest in one built-in plugin ("rules",
/// <see cref="RuleEngine"/>) loaded through the <see cref="PluginHost"/> like any C# plugin (same safety wrapper, storage in
/// data/plugins/rules/). <see cref="Reload"/> (.rules reload, a Studio save) drops that plugin and loads it anew: no restart. Also the
/// Studio's validation and dry-run simulation. World thread only.
/// </summary>
public sealed class RulesHost : IRuleRefs
{
    public const string PluginName = "rules";

    private List<RuleProblem> _problems = [];
    private RuleEngine? _engine;

    internal RulesHost(PluginHost plugins) => Plugins = plugins;

    public PluginHost Plugins { get; }
    private World World => Plugins.World;
    internal IServer Server => Plugins.Server;
    internal IGameplay Game => Plugins.Game;
    internal int MaxErrors => Plugins.Options.MaxErrors;

    /// <summary>The rules' cooldowns (key → until, UTC); kept across reloads (those of a minute and more are also stored).</summary>
    internal Dictionary<string, DateTime> Cooldowns { get; } = new(StringComparer.Ordinal);

    /// <summary>The problems of the last load (and the rules switched off since).</summary>
    public IReadOnlyList<RuleProblem> Problems => _problems;

    /// <summary>The rules running now (none when nothing is loaded).</summary>
    public IReadOnlyList<Rule> Rules => _engine is not null && Plugins.IsLoaded(PluginName) ? _engine.Rules : [];

    public DateTime? LoadedAt { get; private set; }

    /// <summary>The packs whose rules load (Mods:Packs).</summary>
    public IReadOnlyList<string> Packs => [.. World.Config.ModPacks.Distinct(StringComparer.OrdinalIgnoreCase)];

    public bool PackActive(string pack) => Packs.Contains(pack, StringComparer.OrdinalIgnoreCase);

    public string RulesDir(string pack) => Path.Combine(World.Config.ModsPath, pack, "rules");

    /// <summary>The rule files of a folder, by name.</summary>
    public static IEnumerable<string> RuleFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Order(StringComparer.OrdinalIgnoreCase) : [];

    /// <summary>A rule file's JSON object; a <see cref="RuleProblem"/> when it is not one.</summary>
    public static (JsonObject? Json, RuleProblem? Problem) ReadFile(string file, string pack)
    {
        var id = Path.GetFileNameWithoutExtension(file);
        try
        {
            return JsonNode.Parse(File.ReadAllText(file), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o
                ? (o, null) : (null, new RuleProblem("blocker", "", "the file is no JSON object", pack, id));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return (null, new RuleProblem("blocker", "", $"{Path.GetFileName(file)}: {e.Message}", pack, id));
        }
    }

    /// <summary>Every rule of the active packs, checked; the problems of all (a rule with a blocker is not in the list).</summary>
    public (List<Rule> Rules, List<RuleProblem> Problems) ReadAll()
    {
        var rules = new List<Rule>();
        var problems = new List<RuleProblem>();
        foreach (var pack in Packs)
            foreach (var file in RuleFiles(RulesDir(pack)))
            {
                var (json, bad) = ReadFile(file, pack);
                if (bad is not null)
                {
                    problems.Add(bad);
                    continue;
                }
                var (rule, found) = RuleParser.Parse(json!, pack, Path.GetFileNameWithoutExtension(file), this);
                problems.AddRange(found);
                if (rule is null) continue;
                if (CommandConflict(rule, rules) is { } why)
                {
                    problems.Add(new RuleProblem("blocker", "/trigger/params/command", why, pack, rule.Id));
                    continue;
                }
                rules.Add(rule);
            }
        return (rules, problems);
    }

    private static string CommandOf(Rule r) => r.TriggerStr("command").TrimStart('.').ToLowerInvariant();

    /// <summary>Why <paramref name="rule"/>'s chat command cannot be registered next to <paramref name="others"/> and the built-in ones; null when it can.</summary>
    private string? CommandConflict(Rule rule, IEnumerable<Rule> others)
    {
        if (rule.Trigger != "command" || !rule.Enabled) return null;
        var command = CommandOf(rule);
        if (World.Commands.Commands.Any(c => c.Path == command)) return $".{command} is a built-in command";
        if (others.FirstOrDefault(o => o.Trigger == "command" && o.Enabled && o.Key != rule.Key && CommandOf(o) == command) is { } other)
            return $".{command} is used by rule {other.Key} already";
        return null;
    }

    /// <summary>
    /// Reads the rules again and restarts the "rules" plugin with them (its hooks, commands and timers dropped and made anew; variables,
    /// counters and long cooldowns kept in its storage). A line saying what loaded.
    /// </summary>
    public string Reload()
    {
        var (rules, problems) = ReadAll();
        Plugins.Unload(PluginName, "reload");
        _engine = null;
        _problems = problems;
        LoadedAt = DateTime.UtcNow;
        foreach (var p in problems.Where(p => p.Blocker)) Log.Warn("rules", $"not loaded: {p}");
        foreach (var p in problems.Where(p => !p.Blocker)) Log.Info("rules", $"warning: {p}");
        if (rules.Count > 0)
        {
            var engine = new RuleEngine(this, rules);
            var manifest = new PluginManifest
            {
                Name = PluginName, Version = "1.0.0", ApiVersion = PluginApi.Version, Description = "the no-code rules of the packs (mods/<pack>/rules/)",
                Directory = World.Config.ModsPath,
            };
            if (Plugins.Load(engine, manifest) is { } why) _problems.Add(new RuleProblem("blocker", "", $"the rules engine did not load: {why}"));
            else _engine = engine;
        }
        var summary = Summary();
        Log.Info("rules", summary);
        return summary;
    }

    /// <summary>"N rule(s) running from packs …; M not loaded; K warning(s)".</summary>
    public string Summary()
    {
        var running = Rules.Count(r => r.Enabled && !r.Disabled);
        var off = Rules.Count(r => !r.Enabled);
        var blocked = _problems.Where(p => p.Blocker).Select(p => (p.Pack, p.Rule)).Distinct().Count();
        var warnings = _problems.Count(p => !p.Blocker);
        return $"{running} rule(s) running{(off > 0 ? $", {off} switched off" : "")} from {(Packs.Count == 0 ? "no pack (Mods:Packs is empty)" : string.Join(", ", Packs))}"
            + $"{(blocked > 0 ? $"; {blocked} not loaded" : "")}{(warnings > 0 ? $"; {warnings} warning(s)" : "")}";
    }

    internal void Report(RuleProblem problem)
    {
        _problems.Add(problem);
        Log.Warn("rules", problem.ToString());
    }

    /// <summary>The problems of one rule's JSON (as the Studio shows them): the checks of the load plus a command used elsewhere.</summary>
    public List<RuleProblem> Validate(JsonObject json, string pack, string? fileId)
    {
        var (rule, problems) = RuleParser.Parse(json, pack, fileId, this);
        if (rule is not null && CommandConflict(rule, Rules.Concat(ReadAll().Rules).Where(r => !(r.Pack == pack && r.Id == rule.Id))) is { } why)
            problems.Add(new RuleProblem("blocker", "/trigger/params/command", why, pack, rule.Id));
        if (rule is not null && !PackActive(pack))
            problems.Add(new RuleProblem("warning", "", $"pack {pack} is not in Mods:Packs: its rules do not run on this server", pack, rule.Id));
        return problems;
    }

    /// <summary>
    /// A dry run of a rule's JSON for online player <paramref name="playerName"/> (the event made up around that player: the nearest mob as
    /// the mob/victim/target, the trigger's id as the spell/item/buff, 100 as the number): conditions evaluated, actions described, nothing done.
    /// </summary>
    public JsonObject Simulate(JsonObject json, string pack, string playerName)
    {
        var (rule, problems) = RuleParser.Parse(json, pack, null, this);
        if (rule is null)
            return new JsonObject { ["error"] = "the rule has blockers: fix them first", ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)p.ToJson())]) };
        var player = Server.FindPlayer(playerName);
        if (player is null) return new JsonObject { ["error"] = $"{playerName} is not online" };
        var def = RuleSchema.Trigger(rule.Trigger)!;
        var mob = Server.Mobs(player.Cluster).Where(m => m.IsAlive && (def.IdKind != "mob" || rule.Matches(m.Name)))
            .OrderBy(m => (m.X - player.X) * (m.X - player.X) + (m.Y - player.Y) * (m.Y - player.Y)).FirstOrDefault()
            ?? Server.Mobs(player.Cluster).Where(m => m.IsAlive).OrderBy(m => (m.X - player.X) * (m.X - player.X) + (m.Y - player.Y) * (m.Y - player.Y)).FirstOrDefault();
        var id = rule.ExactFilter ?? (def.IdKind == "mob" ? mob?.Name : def.IdKind == "cluster" ? player.Cluster : rule.Filter);
        var mobFirst = rule.Trigger is "mob.spawned";
        var died = rule.Trigger is "player.died" or "player.knockdown";
        var ev = new RuleEvent
        {
            Trigger = rule.Trigger, Dry = true, Id = id, Self = mobFirst ? mob : player, Killer = died ? mob : player, Victim = died ? player : mob, Mob = mob,
            Caster = player, Target = mob, Cluster = player.Cluster,
            Spell = def.IdKind == "spell" ? id : null, Buff = def.IdKind == "buff" ? id : null, Item = def.IdKind is "item" or "mount" ? id : null, Mount = def.IdKind == "mount" ? id : null,
            Amount = def.Features.HasFlag(RuleSchema.Feature.Amount) ? new Mutable<double>(100) : null,
            Loot = def.Features.HasFlag(RuleSchema.Feature.Loot) ? [] : null, Text = "",
        };
        var engine = _engine is not null && Plugins.IsLoaded(PluginName) ? _engine : new RuleEngine(this, [rule]);
        JsonObject result = [];
        RunAsPlugin(() => result = engine.Simulate(rule, ev));
        result["player"] = player.Name;
        result["mob"] = mob?.Name;
        result["event"] = new JsonObject
        {
            ["id"] = id, ["amount"] = ev.Amount is null ? null : 100, ["note"] = "a made-up event around the player: the nearest mob as the mob/victim/target, 100 as the event's number",
        };
        result["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)p.ToJson())]);
        return result;
    }

    /// <summary>Runs <paramref name="action"/> as the rules plugin when it is loaded (unit data and timers need a current plugin), else plainly.</summary>
    private void RunAsPlugin(Action action) => Plugins.RunAs(PluginName, action);

    internal IReadOnlyList<Rule> ChainRules(string pack, string chain) =>
        [.. Rules.Where(r => r.Pack == pack && r.Trigger == "chain" && r.Enabled && !r.Disabled && r.Matches(chain))];

    /// <summary>The other members of <paramref name="player"/>'s party who are online (none without a party).</summary>
    internal IReadOnlyList<IPlayer> PartyOf(IPlayer player)
    {
        if (World.Parties.Of(player.CharacterId) is not { } party) return [];
        return [.. party.Members.Where(m => m.Id != player.CharacterId).Select(m => World.Parties.Online(m.Id)).OfType<Entities.Player>().Where(p => !p.IsBot).Select(Plugins.Wrap)];
    }

    /// <summary>The zone colour of loaded cluster <paramref name="cluster"/> (worldsettings pvpwarningtype: safe, yellow, orange, red, black); null when not loaded.</summary>
    internal string? ZoneColor(string cluster) =>
        cluster.Length == 0 || World.Maps.Find(cluster) is not { } map ? null : Pvp.RulesOf(World, map).WarningType.ToLowerInvariant();

    // ---- IRuleRefs (the validator's lookups)

    public bool? Item(string name) => World.Items.Count == 0 ? null : World.Items.Find(name) is not null;

    public bool? Spell(string name) => World.Spells is null ? null : Game.Spell(name) is not null;

    public bool? Mob(string name) => World.Clusters?.MobsIfLoaded is not { } mobs ? null : mobs.Find(name) is not null;
}
