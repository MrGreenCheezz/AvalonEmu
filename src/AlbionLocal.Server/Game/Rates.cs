using System.Globalization;
using System.Text.Json;

namespace AlbionLocal.Server.Game;

/// <summary>
/// The server's rate modifiers (config section Rates, ARCHITECTURE.md §5.4; all 1 = the official numbers). Each is applied
/// once, where its quantity is computed, so what the client is sent (UpdateFame, HarvestFinished, NewSilverObject, UpdateMoney)
/// already carries it: <see cref="Fame"/> × <see cref="CombatFame"/> on a kill (Systems/Combat.Kill), × <see cref="GatheringFame"/>
/// on a harvest (Handlers/World/HarvestHandler.FinishHarvest), × <see cref="CraftingFame"/> on a craft (BuildingActionHandler),
/// <see cref="Fame"/> alone on a tome; <see cref="Harvest"/> on a harvest's base amount (HarvestFinished key 5, nodes and
/// corpses; the fraction rounded at random as the yield bonus); <see cref="Silver"/> on a mob's silver pile (key 2) and the
/// awardsilver effects; <see cref="Loot"/> on the total loot factor of a mob or an opened chest; <see cref="Respawn"/> and
/// <see cref="Regrowth"/> divide the mob respawn and node regrowth times; <see cref="CraftingReturn"/> on a craft's resource
/// return share (at most all of them); <see cref="ChampionWeight"/> on the elite picks of the RANDOM spawn points and
/// <see cref="ChestRespawn"/> dividing the open-world chest respawn times. The world's are <see cref="World.Rates"/>: the config's, then data/rates.json (a GM's
/// .rates … save), then what a GM sets while the server runs.
/// </summary>
public sealed record Rates
{
    /// <summary>Rates:Fame: every fame gain (kills, harvests, crafts, tomes).</summary>
    public double Fame { get; init; } = 1;

    /// <summary>Rates:CombatFame: the fame of a mob kill, × <see cref="Fame"/>.</summary>
    public double CombatFame { get; init; } = 1;

    /// <summary>Rates:GatheringFame: the fame of a harvest, × <see cref="Fame"/>.</summary>
    public double GatheringFame { get; init; } = 1;

    /// <summary>Rates:CraftingFame: the fame of a craft, × <see cref="Fame"/>.</summary>
    public double CraftingFame { get; init; } = 1;

    /// <summary>Rates:Harvest: the resources a harvest yields (resource nodes and corpses).</summary>
    public double Harvest { get; init; } = 1;

    /// <summary>Rates:Silver: a mob's silver pile and the silver-giving effects (awardsilver).</summary>
    public double Silver { get; init; } = 1;

    /// <summary>
    /// Rates:Loot: the item drop chances of a mob or an opened chest (once, on its total loot factor; not on the Black Market chance,
    /// loot-balance SPEC §2.1); not the loot of opened items (generateloot).
    /// </summary>
    public double Loot { get; init; } = 1;

    /// <summary>Rates:Respawn: the mob respawn speed (2 = back in half the time); replaces World:MobRespawnScale (= 1 / this).</summary>
    public double Respawn { get; init; } = 1;

    /// <summary>Rates:Regrowth: the resource node regrowth speed (2 = a charge back in half the time).</summary>
    public double Regrowth { get; init; } = 1;

    /// <summary>Rates:CraftingReturn: the factor on the share of the resources a craft gives back (at most all).</summary>
    public double CraftingReturn { get; init; } = 1;

    /// <summary>
    /// Rates:ChampionWeight: the factor on the weight of every champion, miniboss and boss mob when a RANDOM spawn point picks
    /// (stageH bosses SPEC §6.2; 2-3 = more elites in the open world). Their fame and loot follow from their own data.
    /// </summary>
    public double ChampionWeight { get; init; } = 1;

    /// <summary>Rates:ChestRespawn: the open-world chest respawn and reroll speed (stageH bosses SPEC §4.5; 2 = back in half the time).</summary>
    public double ChestRespawn { get; init; } = 1;

    /// <summary>The rates' names in the config's order (the keys of the section and of data/rates.json, the .rates names).</summary>
    public static readonly string[] Names = ["Fame", "CombatFame", "GatheringFame", "CraftingFame", "Harvest", "Silver", "Loot", "Respawn", "Regrowth", "CraftingReturn", "ChampionWeight", "ChestRespawn"];

    /// <summary>The largest rate; Respawn and Regrowth (divisors) are at least <see cref="MinSpeed"/>, the others at least 0.</summary>
    public const double Max = 1000;

    /// <summary>The smallest Respawn and Regrowth (a hundred times slower).</summary>
    public const double MinSpeed = 0.01;

    /// <summary>The rate <paramref name="name"/> (one of <see cref="Names"/>, ignoring case); null = no such rate.</summary>
    public double? Get(string name) => Canonical(name) switch
    {
        "Fame" => Fame, "CombatFame" => CombatFame, "GatheringFame" => GatheringFame, "CraftingFame" => CraftingFame,
        "Harvest" => Harvest, "Silver" => Silver, "Loot" => Loot, "Respawn" => Respawn, "Regrowth" => Regrowth,
        "CraftingReturn" => CraftingReturn, "ChampionWeight" => ChampionWeight, "ChestRespawn" => ChestRespawn, _ => null,
    };

    /// <summary>These rates with <paramref name="name"/> set to <paramref name="value"/>; an unknown name or a value out of range throws <see cref="ArgumentException"/>.</summary>
    public Rates With(string name, double value)
    {
        var key = Canonical(name) ?? throw new ArgumentException($"'{name}' is not a rate ({string.Join(", ", Names)})");
        var min = key is "Respawn" or "Regrowth" or "ChestRespawn" ? MinSpeed : 0;
        if (!double.IsFinite(value) || value < min || value > Max)
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"{key}: {value} is not {min}..{Max}"));
        return key switch
        {
            "Fame" => this with { Fame = value }, "CombatFame" => this with { CombatFame = value },
            "GatheringFame" => this with { GatheringFame = value }, "CraftingFame" => this with { CraftingFame = value },
            "Harvest" => this with { Harvest = value }, "Silver" => this with { Silver = value }, "Loot" => this with { Loot = value },
            "Respawn" => this with { Respawn = value }, "Regrowth" => this with { Regrowth = value },
            "ChampionWeight" => this with { ChampionWeight = value }, "ChestRespawn" => this with { ChestRespawn = value },
            _ => this with { CraftingReturn = value },
        };
    }

    /// <summary>The name as <see cref="Names"/> spells it; null = none.</summary>
    public static string? Canonical(string name) => Names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Fame 1, CombatFame 3, …" (the .rates reply; <paramref name="changedOnly"/>: only those that are not 1).</summary>
    public string Describe(bool changedOnly = false)
    {
        var parts = Names.Where(n => !changedOnly || Get(n) != 1).Select(n => string.Create(CultureInfo.InvariantCulture, $"{n} {Get(n):0.####}")).ToList();
        return parts.Count == 0 ? "all 1" : string.Join(", ", parts);
    }

    /// <summary>
    /// <paramref name="amount"/> × <paramref name="rate"/> with the fraction rounded up at random (as the yield bonus,
    /// Systems.Passives.Bonus): ×1 is the amount itself, ×1.5 of 3 is 4 or 5.
    /// </summary>
    public static int Scale(int amount, double rate, Random random)
    {
        if (rate == 1 || amount <= 0) return amount;
        var x = amount * rate;
        var whole = Math.Floor(x);
        return (int)Math.Min(int.MaxValue, whole + (random.NextDouble() < x - whole ? 1 : 0));
    }

    /// <summary>A fame gain (a7m) × <paramref name="rate"/>, truncated as the fame itself; unchanged at 1.</summary>
    public static long ScaleFame(long gain, double rate) => rate == 1 ? gain : (long)Math.Floor(gain * (decimal)rate);

    // ---- data/rates.json: the GM's saved rates ----

    /// <summary>The override file in the saves folder (Paths:Saves).</summary>
    public static string FilePath(string savesPath) => Path.Combine(savesPath, "rates.json");

    /// <summary>
    /// <paramref name="baseRates"/> (the config's) with the rates of <paramref name="path"/> over them ({"Harvest": 2, …}; a
    /// missing file = none). A bad file throws <see cref="InvalidDataException"/>, so the start stops as on a bad config.
    /// </summary>
    public static Rates Load(string path, Rates baseRates)
    {
        if (!File.Exists(path)) return baseRates;
        var rates = baseRates;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{path}: expected an object of rates");
            foreach (var p in doc.RootElement.EnumerateObject())
                rates = p.Value.ValueKind == JsonValueKind.Number ? rates.With(p.Name, p.Value.GetDouble())
                    : throw new InvalidDataException($"{path}: {p.Name} is not a number");
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new InvalidDataException($"{path}: {e.Message}");
        }
        return rates;
    }

    /// <summary>Writes the rates that differ from <paramref name="baseRates"/> to <paramref name="path"/>; none = the file is deleted.</summary>
    public void Save(string path, Rates baseRates)
    {
        var changed = Names.Where(n => Get(n) != baseRates.Get(n)).ToDictionary(n => n, n => Get(n)!.Value);
        if (changed.Count == 0)
        {
            File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(changed, new JsonSerializerOptions { WriteIndented = true }));
    }
}
