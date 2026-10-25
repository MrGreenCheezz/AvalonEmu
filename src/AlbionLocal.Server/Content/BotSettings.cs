using System.Globalization;
using System.Text.Json;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Content;

/// <summary>
/// Stage I bots, the tables (analysis/work/stageI/bots/SPEC.md §3.2): content/bots.json, as content/events.json. How many bots of each
/// role a map gets by its zone class (<see cref="Counts"/>, scaled by the humans on it), the clusters left out or overridden, the tiers of
/// city bots, their avatars and emotes, the name syllables, the gear patterns and a defeat's silver and extras. The switches, timings and rules are world.jsonc's
/// Bots section (<see cref="BotOptions"/>). Every key is optional: a key the file leaves out keeps the built-in value (<see cref="Default"/>),
/// a missing file or one that does not parse gives the built-in values with a log line. Immutable.
/// </summary>
public sealed record BotSettings
{
    public static BotSettings Default { get; } = new();

    /// <summary>The zone classes of <see cref="Counts"/> (SPEC §1.4): royal cities, start towns, the Roads of Avalon (stageL roads SPEC §5.6), then the open world by pvpwarningtype.</summary>
    public static readonly string[] Zones = ["City", "Start", "Roads", "Mists", "Safe", "Yellow", "Orange", "Red", "Black"];

    /// <summary>Bots per role on a map with one human, by zone class; a role left out has none.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<BotRole, int>> Counts { get; init; } = new Dictionary<string, IReadOnlyDictionary<BotRole, int>>(StringComparer.OrdinalIgnoreCase)
    {
        // StageM bots (analysis/work/stageM/bots/NOTES.md §4): about 2.5 × the first tables (6-8 per open-world map of about 1 km²).
        ["City"] = new Dictionary<BotRole, int> { [BotRole.City] = 24 },
        ["Start"] = new Dictionary<BotRole, int> { [BotRole.City] = 8, [BotRole.Gatherer] = 6 },
        ["Safe"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 14 },
        ["Yellow"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 12, [BotRole.Patrol] = 5 },
        ["Orange"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 12, [BotRole.Patrol] = 6 },
        ["Red"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 12, [BotRole.Patrol] = 8 },
        ["Black"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 10, [BotRole.Patrol] = 10 },
        ["Roads"] = new Dictionary<BotRole, int> { [BotRole.Gatherer] = 3, [BotRole.Patrol] = 2 },
        ["Mists"] = new Dictionary<BotRole, int> { [BotRole.Drifter] = 1 }, // stageL mists phase B: a lone Red wanderer per zone
    };

    /// <summary>The factor on <see cref="Counts"/> by the humans on the map (the highest entry for more): fewer bots each when friends come.</summary>
    public IReadOnlyDictionary<int, double> Scale { get; init; } = new Dictionary<int, double> { [1] = 1.0, [2] = 0.8, [3] = 0.6 };

    /// <summary>Clusters (world.xml id) that get no bots whatever their class.</summary>
    public IReadOnlySet<string> Exclude { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clusters that get bots although their class would give none (an island, a hideout): taken as their zone class's counts, or Override's.</summary>
    public IReadOnlySet<string> Include { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Counts of a cluster id or a world.xml type over its zone class's (only the roles given change).</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<BotRole, int>> Override { get; init; } = new Dictionary<string, IReadOnlyDictionary<BotRole, int>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The gear tiers of royal-city bots with their weights (T4-T8: 3, 3, 2, 1, 1).</summary>
    public IReadOnlyDictionary<int, int> CityTiers { get; init; } = new Dictionary<int, int> { [4] = 3, [5] = 3, [6] = 2, [7] = 1, [8] = 1 };

    /// <summary>The avatars (avatars.xml index, NewCharacter key 2) of Bots:Look Random; 0-10 are AVATAR_01-11, unlocked by default.</summary>
    public IReadOnlyList<int> Avatars { get; init; } = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    /// <summary>
    /// The emotes of a dwelling city bot (emotes.bin index, PlayEmote key 1) with their weights: 12 sit, 6 dance, 14 wave, 4 cheer, 11 salute,
    /// 8 laugh. 6 dance, 12 sit and 13 sleep loop and are shown to a player who comes into view later.
    /// </summary>
    public IReadOnlyDictionary<int, int> Emotes { get; init; } = new Dictionary<int, int> { [12] = 4, [6] = 2, [14] = 3, [4] = 2, [11] = 1, [8] = 1 };

    /// <summary>The emotes that loop until stopped (emotes.bin loop="true").</summary>
    public static readonly IReadOnlySet<int> LoopingEmotes = new HashSet<int> { 6, 12, 13 };

    /// <summary>Name syllables: prefix + middle + suffix, first letter upper, the rest lower, 5-12 letters (<see cref="Bots.BotNames"/>).</summary>
    public IReadOnlyList<string> NamePrefixes { get; init; } =
    [
        "Al", "Bran", "Cael", "Dor", "El", "Fen", "Gar", "Hal", "Is", "Jor", "Kel", "Lor", "Mar", "Nor", "Os", "Per", "Quin", "Ros", "Sel", "Tor",
        "Ul", "Val", "Wen", "Yor", "Zar", "Ar", "Ber", "Cor", "Dra", "Eld", "Gwen", "Mor", "Thal", "Vin", "Ash", "Bel",
    ];

    public IReadOnlyList<string> NameMiddles { get; init; } = ["", "", "", "dr", "ri", "an", "el", "or", "am", "is", "ul", "ev"];

    public IReadOnlyList<string> NameSuffixes { get; init; } =
    [
        "ic", "wyn", "an", "eth", "ion", "ar", "ius", "ia", "wen", "mir", "dor", "ric", "ane", "ard", "eus", "ora", "ith", "rin", "ald", "iel",
        "ock", "ra", "us", "ys", "ven",
    ];

    /// <summary>Names a bot never takes (case ignored), beside every character's and every live one's.</summary>
    public IReadOnlySet<string> ReservedNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The armour materials of a patrol's and a city bot's set (head, armor, shoes of one material).</summary>
    public IReadOnlyList<string> Materials { get; init; } = ["PLATE", "LEATHER", "CLOTH"];

    /// <summary>
    /// The weapons of patrols, of the city bots that carry one and of armed gatherers (T{t}_{weapon}; a MAIN_ weapon of a patrol or a city
    /// bot gets an offhand). StageM bots: no healer staff (its three spells are friendly ones, which a bot casts only on itself), a cursed
    /// staff, a quarterstaff and a dagger instead.
    /// </summary>
    public IReadOnlyList<string> Weapons { get; init; } = ["MAIN_SWORD", "2H_BOW", "MAIN_FIRESTAFF", "MAIN_SPEAR", "2H_AXE", "MAIN_CURSEDSTAFF", "2H_QUARTERSTAFF", "MAIN_DAGGER"];

    /// <summary>The offhands of a one-handed weapon.</summary>
    public IReadOnlyList<string> Offhands { get; init; } = ["OFF_SHIELD", "OFF_BOOK", "OFF_TORCH"];

    /// <summary>
    /// Bot-roster gear realism (Bots:Gear:ArtifactChance): the artifact sets of an armour piece (T{t}_{slot}_{material}_{set}; a combination
    /// items.xml lacks keeps the plain piece) and the artifact versions of each weapon of <see cref="Weapons"/> (one or two hands as the weapon).
    /// </summary>
    public IReadOnlyList<string> ArtifactSets { get; init; } = ["UNDEAD", "HELL", "KEEPER", "MORGANA", "AVALON", "FEY"];

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ArtifactWeapons { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["MAIN_SWORD"] = ["MAIN_SCIMITAR_MORGANA"], ["2H_BOW"] = ["2H_BOW_HELL", "2H_BOW_KEEPER", "2H_LONGBOW_UNDEAD"], ["MAIN_FIRESTAFF"] = ["MAIN_FIRESTAFF_KEEPER"],
        ["MAIN_SPEAR"] = ["MAIN_SPEAR_KEEPER"], ["2H_AXE"] = ["2H_AXE_AVALON"], ["MAIN_CURSEDSTAFF"] = ["MAIN_CURSEDSTAFF_UNDEAD", "MAIN_CURSEDSTAFF_AVALON"],
        ["2H_QUARTERSTAFF"] = ["2H_QUARTERSTAFF_AVALON"], ["MAIN_DAGGER"] = ["MAIN_DAGGER_HELL"],
    };

    /// <summary>The cape pattern ({t} the tier).</summary>
    public string Cape { get; init; } = "T{t}_CAPE";

    /// <summary>The mounts of patrols: a horse, an armoured horse with <see cref="ArmoredShare"/> (T5+; the next lower tier that exists otherwise).</summary>
    public string PatrolMount { get; init; } = "T{t}_MOUNT_HORSE";

    public string ArmoredMount { get; init; } = "T{t}_MOUNT_ARMORED_HORSE";

    public double ArmoredShare { get; init; } = 0.3;

    /// <summary>The gathering tool of each resource kind (a gatherer's mainhand and its weapon in a fight).</summary>
    public IReadOnlyDictionary<string, string> Tools { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ORE"] = "T{t}_2H_TOOL_PICK", ["WOOD"] = "T{t}_2H_TOOL_AXE", ["FIBER"] = "T{t}_2H_TOOL_SICKLE", ["HIDE"] = "T{t}_2H_TOOL_KNIFE", ["ROCK"] = "T{t}_2H_TOOL_HAMMER",
    };

    /// <summary>A gatherer's set from T4 ({slot} HEAD / ARMOR / SHOES, {kind} its resource); below T4 a LEATHER_SET1.</summary>
    public string GathererSet { get; init; } = "T{t}_{slot}_GATHERER_{kind}";

    public string GathererBag { get; init; } = "T{t}_BAG";

    /// <summary>A gatherer's mounts (one at random; an ox from T3, a mule at T2 by the lower-tier rule).</summary>
    public IReadOnlyList<string> GathererMounts { get; init; } = ["T{t}_MOUNT_OX", "T{t}_MOUNT_HORSE"];

    /// <summary>The share of city bots that carry a weapon.</summary>
    public double CityWeaponShare { get; init; } = 0.5;

    /// <summary>
    /// The share of gatherers that carry a weapon of <see cref="Weapons"/> in their bag (stageM bots: a tool has no active spell slot): an
    /// attack puts it into the mainhand, the end of the fight puts the tool back (<see cref="Bots.BotCombat"/>). 0.6.
    /// </summary>
    public double GathererWeaponShare { get; init; } = 0.6;

    /// <summary>
    /// What a gatherer carries from its spawn (stageM bots): this many (uniform, whole) of a raw resource of its kind at a tier of its node
    /// band, beside what its harvests add; its defeat's bag holds Bots:Rewards:ResourceDropShare of it, under the farming caps. "5-20".
    /// </summary>
    public (int Min, int Max) GathererStartCarry { get; init; } = (5, 20);

    /// <summary>The silver a defeated bot leaves by its gear tier (whole silver before Rates:Silver; T1 25 .. T8 2500); a tier left out leaves none.</summary>
    public IReadOnlyDictionary<int, int> Silver { get; init; } = new Dictionary<int, int>
    {
        [1] = 25, [2] = 50, [3] = 100, [4] = 250, [5] = 500, [6] = 900, [7] = 1500, [8] = 2500,
    };

    /// <summary>
    /// Items a defeated patrol may add to its bag ({t} its tier, else the next lower tier that items.xml has), each with its chance: a
    /// "bounty". StageM bots: a healing potion (25 %) and a stew (15 %).
    /// </summary>
    public IReadOnlyList<(string Item, double Chance)> PatrolExtra { get; init; } = [("T{t}_POTION_HEAL", 0.25), ("T{t}_MEAL_STEW", 0.15)];

    // ---- bot-social: the lines of a bot's whispers and local chat (Bots:Social:*, Bots:Chat:*) ----

    /// <summary>Chat.Whisper: a bot's answer to a whisper (one at random).</summary>
    public IReadOnlyList<string> WhisperLines { get; init; } =
        ["busy farming, sorry", "afk", "brb", "not now, mid dungeon", "sry, solo today", "can't talk, gathering", "занят, фармлю", "афк"];

    /// <summary>Chat.Say: a city bot's chatter on the local channel.</summary>
    public IReadOnlyList<string> SayLines { get; init; } =
    [
        "wts T5 bags, cheap", "lfg anything", "anyone for a T6 dungeon?", "selling hide, pm me", "bank is laggy today", "wtb T4 planks",
        "lf healer for statics", "where is the mage tower again?", "gl everyone", "кто в данж?", "продам руду",
    ];

    /// <summary>Chat.Greet: a line a city bot may add to its emote back (a third of the time).</summary>
    public IReadOnlyList<string> GreetLines { get; init; } = ["o/", "hi", "hey", "hello there", "привет"];

    /// <summary>Chat.Win: a bot's line after it knocked a player down.</summary>
    public IReadOnlyList<string> WinLines { get; init; } = ["gg", "ez", "gg wp", "better luck next time", "should've run", "gg"];

    /// <summary>Chat.Loss: a beaten bot's line.</summary>
    public IReadOnlyList<string> LossLines { get; init; } = ["gg", "gg wp", "lag...", "ugh", "nice one", "gg"];

    /// <summary>Chat.Join: a world bot's line when it joins a party it was invited to (Bots:Party).</summary>
    public IReadOnlyList<string> JoinLines { get; init; } = ["hi all", "o/ let's go", "sure, I'm in", "ty for the invite", "ready", "го"];

    /// <summary>Chat.Leave: a party bot's line when it leaves its party.</summary>
    public IReadOnlyList<string> LeaveLines { get; init; } = ["gotta go, ty", "cya", "thx for the group", "o/", "bb", "пока"];

    /// <summary>Chat.EmoteReplies: the emote a city bot answers a player's emote with (emotes index: 14 wave, 2 bow, 11 salute, 4 cheer, 0 applaud, 8 laugh); an emote left out gets none.</summary>
    public IReadOnlyDictionary<int, int> EmoteReplies { get; init; } = new Dictionary<int, int> { [14] = 14, [2] = 2, [11] = 11, [4] = 4, [0] = 4, [6] = 0, [8] = 8 };

    /// <summary>The counts of zone class <paramref name="zone"/> with the override of <paramref name="cluster"/> or <paramref name="type"/> over them.</summary>
    public IReadOnlyDictionary<BotRole, int> CountsOf(string zone, string cluster, string type)
    {
        var counts = new Dictionary<BotRole, int>(Counts.GetValueOrDefault(zone) ?? new Dictionary<BotRole, int>());
        foreach (var key in new[] { type, cluster })
            if (Override.GetValueOrDefault(key) is { } over)
                foreach (var (role, n) in over) counts[role] = n;
        return counts;
    }

    /// <summary>The factor of <see cref="Scale"/> for <paramref name="humans"/> on a map (the highest entry not above it; 1 below the first).</summary>
    public double ScaleOf(int humans) => Scale.Where(s => s.Key <= humans).OrderByDescending(s => s.Key).Select(s => s.Value).FirstOrDefault(1.0);

    public static BotSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn("content", $"{path} missing: built-in bot settings");
            return Default;
        }
        try
        {
            var s = Parse(File.ReadAllText(path), path);
            Log.Info("content", string.Create(CultureInfo.InvariantCulture,
                $"bots: city {s.Counts.GetValueOrDefault("City")?.GetValueOrDefault(BotRole.City)} per royal city, {s.NamePrefixes.Count}×{s.NameMiddles.Count}×{s.NameSuffixes.Count} name syllables, {s.Exclude.Count} excluded cluster(s)"));
            return s;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            Log.Error("content", $"{path}: {e.Message}; built-in bot settings used");
            return Default;
        }
    }

    /// <summary>
    /// The file: Counts { zone: { role: n } }, Scale { humans: factor }, Clusters { Include [], Exclude [], Override { id or type: { role: n } } },
    /// CityTiers { tier: weight }, Avatars [], Emotes { index: weight }, Names { Prefixes, Middles, Suffixes, ReservedNames }, Gear { Materials,
    /// Weapons, Offhands, Cape, PatrolMount, ArmoredMount, ArmoredShare, Tools { kind: pattern }, GathererSet, GathererBag, GathererMounts,
    /// CityWeaponShare, GathererWeaponShare, GathererStartCarry "min-max", ArtifactSets [], ArtifactWeapons { weapon: [] } }, Rewards { Silver { tier: silver }, PatrolExtra [{ Item, Chance }] },
    /// Chat { Whisper [], Say [], Greet [], Win [], Loss [], EmoteReplies { emote: emote } }.
    /// </summary>
    public static BotSettings Parse(string text, string path)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not a JSON object");
        var s = Default;
        double Num(JsonElement v, string key, double min, double max) =>
            v.TryGetDouble(out var d) && d >= min && d <= max ? d : throw new InvalidDataException($"{key}: {v} is not {min}..{max}");
        int Int(JsonElement v, string key, int min, int max) => (int)Num(v, key, min, max) is var i && i == v.GetDouble() ? i : throw new InvalidDataException($"{key}: {v} is not a whole number");
        string Str(JsonElement v, string key) => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"{key}: not a string");
        JsonElement Obj(JsonElement v, string key) => v.ValueKind == JsonValueKind.Object ? v : throw new InvalidDataException($"{key}: not an object");
        List<string> Strings(JsonElement v, string key) => v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(x => Str(x, key))] : throw new InvalidDataException($"{key}: not a list");
        int IntKey(string text, string key, int min, int max) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= min && i <= max
            ? i : throw new InvalidDataException($"{key}: '{text}' is not {min}..{max}");
        (int Min, int Max) IntRange(JsonElement v, string key, int min, int max)
        {
            if (v.ValueKind == JsonValueKind.Number) return (Int(v, key, min, max), Int(v, key, min, max));
            var parts = Str(v, key).Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= min && n <= max ? n : (int?)null).ToList() is { } ns
                && ns.All(n => n is not null))
                return (ns.Min()!.Value, ns.Max()!.Value);
            throw new InvalidDataException($"{key}: '{v}' is not a range min-max within {min}..{max}");
        }
        Dictionary<BotRole, int> Roles(JsonElement v, string key)
        {
            var roles = new Dictionary<BotRole, int>();
            foreach (var r in Obj(v, key).EnumerateObject())
                roles[Enum.TryParse<BotRole>(r.Name, ignoreCase: true, out var role) && Enum.IsDefined(role) ? role
                    : throw new InvalidDataException($"{key}.{r.Name}: not City, Gatherer, Patrol or Drifter")] = Int(r.Value, $"{key}.{r.Name}", 0, 100);
            return roles;
        }
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "Counts":
                    var counts = new Dictionary<string, IReadOnlyDictionary<BotRole, int>>(s.Counts, StringComparer.OrdinalIgnoreCase);
                    foreach (var z in Obj(p.Value, p.Name).EnumerateObject())
                        counts[Zones.FirstOrDefault(n => n.Equals(z.Name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"Counts.{z.Name}: not one of {string.Join(", ", Zones)}")] = Roles(z.Value, $"Counts.{z.Name}");
                    s = s with { Counts = counts };
                    break;
                case "Scale":
                    s = s with { Scale = Obj(p.Value, p.Name).EnumerateObject().ToDictionary(x => IntKey(x.Name, $"Scale.{x.Name}", 1, 1000), x => Num(x.Value, $"Scale.{x.Name}", 0, 10)) };
                    break;
                case "Clusters":
                    foreach (var c in Obj(p.Value, p.Name).EnumerateObject())
                        s = c.Name switch
                        {
                            "Include" => s with { Include = Strings(c.Value, "Clusters.Include").ToHashSet(StringComparer.OrdinalIgnoreCase) },
                            "Exclude" => s with { Exclude = Strings(c.Value, "Clusters.Exclude").ToHashSet(StringComparer.OrdinalIgnoreCase) },
                            "Override" => s with
                            {
                                Override = Obj(c.Value, "Clusters.Override").EnumerateObject()
                                    .ToDictionary(o => o.Name, o => (IReadOnlyDictionary<BotRole, int>)Roles(o.Value, $"Clusters.Override.{o.Name}"), StringComparer.OrdinalIgnoreCase),
                            },
                            _ => Warn(s, $"Clusters.{c.Name}"),
                        };
                    break;
                case "CityTiers":
                    s = s with { CityTiers = Obj(p.Value, p.Name).EnumerateObject().ToDictionary(x => IntKey(x.Name, $"CityTiers.{x.Name}", 1, 8), x => Int(x.Value, $"CityTiers.{x.Name}", 0, 1000)) };
                    if (s.CityTiers.Values.Sum() == 0) throw new InvalidDataException("CityTiers: every weight is 0");
                    break;
                case "Avatars":
                    if (p.Value.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{p.Name}: not a list");
                    s = s with { Avatars = [.. p.Value.EnumerateArray().Select(x => Int(x, p.Name, 0, 146))] };
                    if (s.Avatars.Count == 0) throw new InvalidDataException("Avatars: empty");
                    break;
                case "Emotes":
                    s = s with { Emotes = Obj(p.Value, p.Name).EnumerateObject().ToDictionary(x => IntKey(x.Name, $"Emotes.{x.Name}", 1, 15), x => Int(x.Value, $"Emotes.{x.Name}", 0, 1000)) };
                    break;
                case "Names":
                    foreach (var n in Obj(p.Value, p.Name).EnumerateObject())
                        s = n.Name switch
                        {
                            "Prefixes" => s with { NamePrefixes = Syllables(Strings(n.Value, "Names.Prefixes"), "Names.Prefixes", allowEmpty: false) },
                            "Middles" => s with { NameMiddles = Syllables(Strings(n.Value, "Names.Middles"), "Names.Middles", allowEmpty: true) },
                            "Suffixes" => s with { NameSuffixes = Syllables(Strings(n.Value, "Names.Suffixes"), "Names.Suffixes", allowEmpty: false) },
                            "ReservedNames" => s with { ReservedNames = Strings(n.Value, "Names.ReservedNames").ToHashSet(StringComparer.OrdinalIgnoreCase) },
                            _ => Warn(s, $"Names.{n.Name}"),
                        };
                    break;
                case "Rewards":
                    foreach (var r in Obj(p.Value, p.Name).EnumerateObject())
                        s = r.Name switch
                        {
                            "Silver" => s with { Silver = Obj(r.Value, "Rewards.Silver").EnumerateObject().ToDictionary(x => IntKey(x.Name, $"Rewards.Silver.{x.Name}", 1, 8), x => Int(x.Value, $"Rewards.Silver.{x.Name}", 0, 10_000_000)) },
                            "PatrolExtra" => s with
                            {
                                PatrolExtra = r.Value.ValueKind == JsonValueKind.Array
                                    ? [.. r.Value.EnumerateArray().Select((x, i) => (
                                        Str(Obj(x, $"Rewards.PatrolExtra[{i}]").TryGetProperty("Item", out var item) ? item : throw new InvalidDataException($"Rewards.PatrolExtra[{i}]: no Item"), $"Rewards.PatrolExtra[{i}].Item"),
                                        x.TryGetProperty("Chance", out var chance) ? Num(chance, $"Rewards.PatrolExtra[{i}].Chance", 0, 1) : throw new InvalidDataException($"Rewards.PatrolExtra[{i}]: no Chance")))]
                                    : throw new InvalidDataException("Rewards.PatrolExtra: not a list"),
                            },
                            _ => Warn(s, $"Rewards.{r.Name}"),
                        };
                    break;
                case "Chat":
                    foreach (var c in Obj(p.Value, p.Name).EnumerateObject())
                    {
                        var key = $"Chat.{c.Name}";
                        s = c.Name switch
                        {
                            "Whisper" => s with { WhisperLines = Lines(Strings(c.Value, key), key) },
                            "Say" => s with { SayLines = Lines(Strings(c.Value, key), key) },
                            "Greet" => s with { GreetLines = Lines(Strings(c.Value, key), key) },
                            "Win" => s with { WinLines = Lines(Strings(c.Value, key), key) },
                            "Loss" => s with { LossLines = Lines(Strings(c.Value, key), key) },
                            "Join" => s with { JoinLines = Lines(Strings(c.Value, key), key) },
                            "Leave" => s with { LeaveLines = Lines(Strings(c.Value, key), key) },
                            "EmoteReplies" => s with { EmoteReplies = Obj(c.Value, key).EnumerateObject().ToDictionary(x => IntKey(x.Name, $"{key}.{x.Name}", 0, 15), x => Int(x.Value, $"{key}.{x.Name}", 0, 15)) },
                            _ => Warn(s, key),
                        };
                    }
                    break;
                case "Gear":
                    foreach (var g in Obj(p.Value, p.Name).EnumerateObject())
                    {
                        var key = $"Gear.{g.Name}";
                        s = g.Name switch
                        {
                            "Materials" => s with { Materials = NonEmpty(Strings(g.Value, key), key) },
                            "Weapons" => s with { Weapons = NonEmpty(Strings(g.Value, key), key) },
                            "Offhands" => s with { Offhands = NonEmpty(Strings(g.Value, key), key) },
                            "Cape" => s with { Cape = Str(g.Value, key) },
                            "PatrolMount" => s with { PatrolMount = Str(g.Value, key) },
                            "ArmoredMount" => s with { ArmoredMount = Str(g.Value, key) },
                            "ArmoredShare" => s with { ArmoredShare = Num(g.Value, key, 0, 1) },
                            "Tools" => s with { Tools = Obj(g.Value, key).EnumerateObject().ToDictionary(t => t.Name.ToUpperInvariant(), t => Str(t.Value, $"{key}.{t.Name}"), StringComparer.OrdinalIgnoreCase) },
                            "GathererSet" => s with { GathererSet = Str(g.Value, key) },
                            "GathererBag" => s with { GathererBag = Str(g.Value, key) },
                            "GathererMounts" => s with { GathererMounts = NonEmpty(Strings(g.Value, key), key) },
                            "CityWeaponShare" => s with { CityWeaponShare = Num(g.Value, key, 0, 1) },
                            "GathererWeaponShare" => s with { GathererWeaponShare = Num(g.Value, key, 0, 1) },
                            "GathererStartCarry" => s with { GathererStartCarry = IntRange(g.Value, key, 0, 999) },
                            "ArtifactSets" => s with { ArtifactSets = Strings(g.Value, key) },
                            "ArtifactWeapons" => s with
                            {
                                ArtifactWeapons = Obj(g.Value, key).EnumerateObject()
                                    .ToDictionary(w => w.Name, w => (IReadOnlyList<string>)Strings(w.Value, $"{key}.{w.Name}"), StringComparer.OrdinalIgnoreCase),
                            },
                            _ => Warn(s, key),
                        };
                    }
                    break;
                default:
                    Warn(s, p.Name);
                    break;
            }
        }
        return s;

        BotSettings Warn(BotSettings same, string key)
        {
            Log.Warn("content", $"{path}: unknown key {key}, skipped");
            return same;
        }
    }

    /// <summary>Chat lines: trimmed, the empty ones dropped (an empty list = the bots never say that kind), at most 200 characters each.</summary>
    private static List<string> Lines(List<string> list, string key)
    {
        var lines = list.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (lines.FirstOrDefault(x => x.Length > 200) is { } longLine) throw new InvalidDataException($"{key}: '{longLine[..20]}...' is longer than 200 characters");
        return lines;
    }

    private static List<string> NonEmpty(List<string> list, string key) => list.Count > 0 ? list : throw new InvalidDataException($"{key}: empty");

    /// <summary>Name syllables: letters only (a middle may be empty), at least one.</summary>
    private static List<string> Syllables(List<string> list, string key, bool allowEmpty)
    {
        if (list.Count == 0) throw new InvalidDataException($"{key}: empty");
        foreach (var x in list)
            if ((x.Length == 0 && !allowEmpty) || !x.All(char.IsAsciiLetter)) throw new InvalidDataException($"{key}: '{x}' is not letters only");
        return list;
    }
}
