using System.Globalization;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Content;

/// <summary>
/// The combat numbers neither the client data nor the captures settle (stage 5b-5d, analysis/work/stage5/SPEC.md):
/// content/combat.json, ARCHITECTURE.md §5.2. A key the file leaves out keeps the built-in value (<see cref="Default"/>,
/// the shipped file's); a missing file or one that does not parse gives the built-in values with a log line, a bad entry
/// is logged and skipped (principle 6). Immutable.
/// </summary>
public sealed record CombatSettings
{
    public static CombatSettings Default { get; } = new();

    /// <summary>m/s of a ranged or magic auto-attack's projectile (20; SPEC §5b.5, §5d.1 "use 20 m/s when the mob has no projectile data").</summary>
    public float AutoAttackProjectileSpeed { get; init; } = 20;

    /// <summary>Damage factor per combo index by item uniquename pattern (<see cref="GameData.Glob"/>), first match wins (SPEC §9 Q9).</summary>
    public IReadOnlyList<(string Pattern, float[] Factors)> ComboDamage { get; init; } = [("T?_MAIN_FIRESTAFF*", [1, 1, 2.2f])];

    /// <summary>How long a mob's loot bag stays (120 s, SPEC §5c.4).</summary>
    public TimeSpan LootBagTime { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Metres added to characters.xml useobjectrange for the loot bag checks (3, as the harvest's slack).</summary>
    public float LootRangeSlack { get; init; } = 3;

    /// <summary>
    /// How often a walking mob's event 3 is sent again from where it has got to (0.5 s, SPEC §5d.1 "re-aim every 0.5 s"): the
    /// client dead-reckons a sample for at most 1 s (stage 7 movement SPEC §4), official mobs send about every 100 ms.
    /// </summary>
    public TimeSpan MobMoveResend { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>A chasing mob aims again when its target has moved this far (m) from the last aim point (0.5, SPEC §5d.1).</summary>
    public float MobReaimDistance { get; init; } = 0.5f;

    /// <summary>
    /// A mob's first swing starts this long after it enters combat at the earliest (0.15 s). The captures: a standing ghoul's
    /// Attack (swing start) 137 ms after the HealthUpdate of the player's first hit (114814, mob 354782), a mage's 20 ms after,
    /// chasing mobs' on the tick they reached their range. SPEC §5d.1's "1.0 s + the swing windup" (a first hit 1.35-2 s late)
    /// read the 1.8 s of a mob that first had to walk as its reaction.
    /// </summary>
    public TimeSpan MobReactionTime { get; init; } = TimeSpan.FromSeconds(0.15);

    /// <summary>
    /// A mob resets when its last swing started this long ago while its target is out of its reach (4 s, SPEC §5d.3, §9 Q3:
    /// the captured resets came 3.8-4.0 s after the mob's last Attack).
    /// </summary>
    public TimeSpan MobLeashNoHit { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>The return-home buff's ActiveSpellEffectsUpdate comes this long after the reset's regeneration combo (0.2 s; captured 0.17-0.21 s, SPEC §5d.3).</summary>
    public TimeSpan MobReturnBuffDelay { get; init; } = TimeSpan.FromSeconds(0.2);

    /// <summary>A mob home again sends the clear ActiveSpellEffectsUpdate this long after its two regeneration combos (0.15 s; captured 0.12-0.2 s, mobs/mob_reset.txt).</summary>
    public TimeSpan MobHomeClearDelay { get; init; } = TimeSpan.FromSeconds(0.15);

    /// <summary>
    /// How often a fighting mob's &lt;AI&gt; rotation is evaluated while it is free (0.1 s, stageD mobspells SPEC R5, §9 Q2:
    /// the deterministic captured triggers fired 2-120 ms after they became possible).
    /// </summary>
    public TimeSpan MobAiThink { get; init; } = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// An IfChance node of a mob's &lt;AI&gt; is rolled at most once per this long on a think that reaches it (1 s, SPEC R5, §9 Q1:
    /// the captured chance-gated casts cluster at whole seconds after the mob's last swing); a failed roll reads false until then.
    /// </summary>
    public TimeSpan MobAiChanceReroll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether mobs cast the spells of their &lt;AI&gt; (stageD mobspells, BehaviourAI; on). Off: they fight as in 5d (.mob spells on|off).</summary>
    public bool MobSpells { get; init; } = true;

    /// <summary>The factor of combo index <paramref name="combo"/> for weapon <paramref name="item"/> (null = bare hands): 1 unless the table says otherwise.</summary>
    public float Combo(string? item, int combo)
    {
        if (item is null) return 1;
        foreach (var (pattern, factors) in ComboDamage)
            if (GameData.Glob(pattern, item)) return combo >= 0 && combo < factors.Length ? factors[combo] : 1;
        return 1;
    }

    public static CombatSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn("content", $"{path} missing: built-in combat settings");
            return Default;
        }
        try
        {
            var settings = Parse(File.ReadAllText(path), path);
            Log.Info("content", string.Create(CultureInfo.InvariantCulture,
                $"combat: auto-attack projectiles {settings.AutoAttackProjectileSpeed} m/s, {settings.ComboDamage.Count} combo table(s), loot bags {settings.LootBagTime.TotalSeconds} s"));
            return settings;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            Log.Error("content", $"{path}: {e.Message}; built-in combat settings used");
            return Default;
        }
    }

    /// <summary>
    /// The file: { "AutoAttackProjectileSpeed": n, "ComboDamage": { "&lt;pattern&gt;": [f0, f1, f2], … }, "LootBagSeconds": n,
    /// "LootRangeSlack": n, "MobMoveResendSeconds": n, "MobReaimDistance": n, "MobReactionSeconds": n, "MobLeashNoHitSeconds": n,
    /// "MobReturnBuffDelaySeconds": n, "MobHomeClearDelaySeconds": n, "MobAiThinkSeconds": n, "MobAiChanceRerollSeconds": n,
    /// "MobSpells": true|false }.
    /// </summary>
    public static CombatSettings Parse(string text, string path)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not a JSON object");
        var s = Default;
        float Positive(JsonElement v, string key) =>
            v.TryGetSingle(out var f) && f > 0 ? f : throw new InvalidDataException($"{key}: {v} is not a positive number");
        float NotNegative(JsonElement v, string key) =>
            v.TryGetSingle(out var f) && f >= 0 ? f : throw new InvalidDataException($"{key}: {v} is not 0 or more");
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "AutoAttackProjectileSpeed": s = s with { AutoAttackProjectileSpeed = Positive(p.Value, p.Name) }; break;
                case "LootBagSeconds": s = s with { LootBagTime = TimeSpan.FromSeconds(Positive(p.Value, p.Name)) }; break;
                case "MobMoveResendSeconds": s = s with { MobMoveResend = TimeSpan.FromSeconds(Positive(p.Value, p.Name)) }; break;
                case "MobReaimDistance": s = s with { MobReaimDistance = Positive(p.Value, p.Name) }; break;
                case "MobReactionSeconds": s = s with { MobReactionTime = TimeSpan.FromSeconds(NotNegative(p.Value, p.Name)) }; break;
                case "MobLeashNoHitSeconds": s = s with { MobLeashNoHit = TimeSpan.FromSeconds(Positive(p.Value, p.Name)) }; break;
                case "MobReturnBuffDelaySeconds": s = s with { MobReturnBuffDelay = TimeSpan.FromSeconds(NotNegative(p.Value, p.Name)) }; break;
                case "MobHomeClearDelaySeconds": s = s with { MobHomeClearDelay = TimeSpan.FromSeconds(NotNegative(p.Value, p.Name)) }; break;
                case "MobAiThinkSeconds": s = s with { MobAiThink = TimeSpan.FromSeconds(Positive(p.Value, p.Name)) }; break;
                case "MobAiChanceRerollSeconds": s = s with { MobAiChanceReroll = TimeSpan.FromSeconds(NotNegative(p.Value, p.Name)) }; break;
                case "MobSpells":
                    s = s with { MobSpells = p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.Value.GetBoolean() : throw new InvalidDataException($"MobSpells: {p.Value} is not true or false") };
                    break;
                case "LootRangeSlack":
                    s = s with { LootRangeSlack = p.Value.TryGetSingle(out var slack) && slack >= 0 ? slack : throw new InvalidDataException($"LootRangeSlack: {p.Value} is not 0 or more") };
                    break;
                case "ComboDamage":
                    var table = new List<(string, float[])>();
                    foreach (var entry in p.Value.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.Array || entry.Value.EnumerateArray().Any(f => !f.TryGetSingle(out var x) || x < 0))
                        {
                            Log.Warn("content", $"{path}: ComboDamage {entry.Name}: not an array of factors, skipped");
                            continue;
                        }
                        table.Add((entry.Name, [.. entry.Value.EnumerateArray().Select(f => f.GetSingle())]));
                    }
                    s = s with { ComboDamage = table };
                    break;
                default: Log.Warn("content", $"{path}: unknown key {p.Name}, skipped"); break;
            }
        }
        return s;
    }
}
