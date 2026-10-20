using System.Text.Json;
using System.Text.Json.Nodes;
using AlbionLocal.Photon;
using AlbionLocal.Server.Rules;
using AlbionLocal.Server.Web.Studio;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The Studio's no-code rules API (/studio/api/rules/*, server/plugins/RULES.md): the palette (triggers, conditions, actions, targets,
/// templates), a pack's rules (mods/&lt;pack&gt;/rules/&lt;id&gt;.json) with their problems and live state, save/delete (each followed by a live
/// reload of the rules on the running world: no restart, rules are server-only), validate, a dry-run simulation on an online player.
/// Same security as the rest of the Studio (admin session, CSRF on POST); writes only into mods/&lt;pack&gt;/rules/.
/// </summary>
public sealed partial class WebPanel
{
    private RulesHost RulesOf => _world.Plugins.Rules;

    private async Task<WebResponse> RulesCall(WebRequest r, AdminSession s, string sub)
    {
        var who = $"admin '{s.Name}'";
        switch (r.Method, sub)
        {
            case ("GET", "palette"): return Json(200, RuleSchema.Palette());
            case ("GET", "players"):
                return Json(200, await OnWorld(() => new JsonObject
                {
                    ["players"] = new JsonArray([.. RulesOf.Server.OnlinePlayers.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["cluster"] = p.Cluster })]),
                }));
            case ("GET", "list"): return Json(200, await RulesList(r.Q("pack")));
            case ("GET", "get"):
            {
                var (pack, dir) = RulesPack(r.Q("pack"));
                var id = RuleId(r.Q("id"));
                var file = Path.Combine(dir, id + ".json");
                if (!File.Exists(file)) return Json(404, Error($"no rule {id} in {pack}"));
                var (json, bad) = RulesHost.ReadFile(file, pack);
                if (json is null) return Json(200, new JsonObject { ["rule"] = null, ["raw"] = File.ReadAllText(file), ["diagnostics"] = new JsonArray(bad!.ToJson()) });
                var diags = await OnWorld(() => RulesOf.Validate(json, pack, id));
                return Json(200, new JsonObject { ["rule"] = json, ["diagnostics"] = Diags(diags) });
            }
            case ("POST", "validate"):
            {
                var body = Body(r);
                var (pack, _) = RulesPack(body["pack"]?.ToString());
                var rule = body["rule"] as JsonObject ?? throw new InvalidDataException("no rule");
                var diags = await OnWorld(() => RulesOf.Validate(rule, pack, null));
                return Json(200, new JsonObject { ["diagnostics"] = Diags(diags) });
            }
            case ("POST", "simulate"):
            {
                var body = Body(r);
                var (pack, _) = RulesPack(body["pack"]?.ToString());
                var rule = body["rule"] as JsonObject ?? throw new InvalidDataException("no rule");
                var player = body["player"]?.ToString() ?? "";
                return Json(200, await OnWorld(() => RulesOf.Simulate(rule, pack, player)));
            }
            case ("POST", "reload"):
            {
                var summary = await OnWorld(() => RulesOf.Reload());
                Log.Info("studio", $"{who} reloaded the rules: {summary}");
                return Json(200, new JsonObject { ["summary"] = summary });
            }
            case ("POST", "save"):
            {
                if (_modsProblem is not null) return Json(403, Error(_modsProblem));
                var body = Body(r);
                var (pack, dir) = RulesPack(body["pack"]?.ToString());
                var rule = body["rule"] as JsonObject ?? throw new InvalidDataException("no rule");
                body.Remove("rule"); // the answer carries it
                var id = RuleId(rule["id"]?.ToString());
                var from = body["from"]?.ToString() is { Length: > 0 } f ? RuleId(f) : null;
                Directory.CreateDirectory(dir);
                rule["schema"] = 1;
                rule["updated"] = DateTime.UtcNow.ToString("o");
                var file = Path.Combine(dir, id + ".json");
                if (from is not null && from != id && File.Exists(file)) return Json(409, Error($"rule {id} exists already in {pack}"));
                var tmp = file + ".tmp";
                File.WriteAllText(tmp, rule.ToJsonString(J.Indented));
                File.Move(tmp, file, overwrite: true);
                if (from is not null && from != id) File.Delete(Path.Combine(dir, from + ".json"));
                var (diags, summary, active) = await OnWorld(() =>
                {
                    var d = RulesOf.Validate(rule, pack, id);
                    var active = RulesOf.PackActive(pack);
                    return (d, active ? RulesOf.Reload() : RulesOf.Summary(), active);
                });
                Log.Info("studio", $"{who} saved rule {pack}/{id}{(from is not null && from != id ? $" (was {from})" : "")}; {summary}");
                return Json(200, new JsonObject
                {
                    ["rule"] = rule, ["diagnostics"] = Diags(diags), ["summary"] = summary, ["reloaded"] = active,
                    ["live"] = await OnWorld(() => LiveOf(pack, id)),
                });
            }
            case ("POST", "delete"):
            {
                if (_modsProblem is not null) return Json(403, Error(_modsProblem));
                var body = Body(r);
                var (pack, dir) = RulesPack(body["pack"]?.ToString());
                var id = RuleId(body["id"]?.ToString());
                var file = Path.Combine(dir, id + ".json");
                if (!File.Exists(file)) return Json(404, Error($"no rule {id} in {pack}"));
                File.Delete(file);
                var summary = await OnWorld(() => RulesOf.PackActive(pack) ? RulesOf.Reload() : RulesOf.Summary());
                Log.Info("studio", $"{who} deleted rule {pack}/{id}; {summary}");
                return Json(200, new JsonObject { ["summary"] = summary });
            }
        }
        return Json(404, Error($"no API {r.Method} /studio/api/rules/{sub}"));
    }

    private static JsonArray Diags(IEnumerable<RuleProblem> problems) => new([.. problems.Select(p => (JsonNode)p.ToJson())]);

    private (string Pack, string Dir) RulesPack(string? name)
    {
        var pack = PackFiles.Open(_modsDir, name) ?? throw new InvalidDataException($"no pack '{name}'");
        return (pack.Name, Path.Combine(pack.Dir, "rules"));
    }

    private static string RuleId(string? id) =>
        id is not null && RuleParser.IdPattern.IsMatch(id) ? id : throw new InvalidDataException($"'{id}' is no rule id (1-64 of a-z, 0-9, _ and -)");

    /// <summary>The running state of rule <paramref name="pack"/>/<paramref name="id"/>: loaded, fires, errors, switched off and why. World thread.</summary>
    private JsonObject LiveOf(string pack, string id)
    {
        var rule = RulesOf.Rules.FirstOrDefault(x => x.Pack.Equals(pack, StringComparison.OrdinalIgnoreCase) && x.Id == id);
        return new JsonObject
        {
            ["loaded"] = rule is not null, ["fires"] = rule?.Fires ?? 0, ["errors"] = rule?.Errors ?? 0, ["disabled"] = rule?.Disabled ?? false, ["why"] = rule?.DisabledWhy,
            ["problems"] = Diags(RulesOf.Problems.Where(p => p.Pack.Equals(pack, StringComparison.OrdinalIgnoreCase) && p.Rule == id)),
        };
    }

    private async Task<JsonObject> RulesList(string? packName)
    {
        var (pack, dir) = RulesPack(packName);
        var files = RulesHost.RuleFiles(dir).ToList();
        return await OnWorld(() =>
        {
            var list = new JsonArray();
            foreach (var file in files)
            {
                var id = Path.GetFileNameWithoutExtension(file);
                var (json, bad) = RulesHost.ReadFile(file, pack);
                var problems = json is null ? [bad!] : RulesOf.Validate(json, pack, id);
                var trigger = json?["trigger"] as JsonObject;
                list.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = json?["name"]?.ToString() ?? id, ["enabled"] = json?["enabled"] is not JsonValue v || !v.TryGetValue<bool>(out var e) || e,
                    ["trigger"] = trigger?["type"]?.ToString(), ["filter"] = trigger?["id"]?.ToString(),
                    ["problems"] = new JsonObject { ["blocker"] = problems.Count(p => p.Blocker), ["warning"] = problems.Count(p => p.Severity == "warning" && p.Path.Length > 0) },
                    ["live"] = LiveOf(pack, id),
                });
            }
            return new JsonObject
            {
                ["pack"] = pack, ["active"] = RulesOf.PackActive(pack), ["activePacks"] = J.Strings(RulesOf.Packs), ["summary"] = RulesOf.Summary(), ["rules"] = list,
                ["loadedAt"] = RulesOf.LoadedAt?.ToString("o"),
            };
        });
    }
}
