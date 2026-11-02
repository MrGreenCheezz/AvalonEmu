using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>The installed festivity catalogue, published fixed dates and an explicitly local seeded calendar.
/// Live evaluation uses World.GameNow, performs no I/O and is shared by UI publication and gameplay bonuses.</summary>
public sealed class FestivitiesData
{
    public sealed record FixedEvent(string Name, long StartUtc, long EndUtc, string SourceURL);
    public sealed record CraftBonus(string Category, double Value);
    public sealed record Festivity(byte Kind, string Category, string Name, long StartUtc, long EndUtc,
        IReadOnlyList<string> BuffSpells, IReadOnlyList<CraftBonus> CraftBonuses);

    public static FestivitiesData Empty { get; } = new([]);
    public IReadOnlyList<FixedEvent> Schedule { get; }

    private sealed record Definition(string Name, int Weight, int CooldownDays, int MinDays, int MaxDays,
        IReadOnlyList<string> BuffSpells, IReadOnlyList<CraftBonus> CraftBonuses);
    private sealed record Category(string Name, int Weight, int CooldownDays, int Quantity, IReadOnlyList<Definition> Events);
    private sealed record Calendar(string Seed, long EpochUtc, long PreviewTicks);
    private sealed record Evaluation(IReadOnlyList<Festivity> Active, IReadOnlyList<Festivity> Published, long Until);
    private readonly object gate = new();
    private readonly IReadOnlyList<Festivity> fixedRows;
    private readonly IReadOnlyList<Category> automated, daily;
    private readonly Calendar? local;
    private readonly Draws? automaticDraws;
    private readonly List<Festivity> automaticRows = [];
    private readonly Dictionary<string, long> lastEventEnd = new(StringComparer.Ordinal), lastCategoryEnd = new(StringComparer.Ordinal);
    private long automaticCursor, cachedFrom = -1;
    private Evaluation? cached;

    private FestivitiesData(IReadOnlyList<FixedEvent> schedule, IReadOnlyList<Festivity>? fixedEvents = null,
        Calendar? calendar = null, IReadOnlyList<Category>? automatedEvents = null, IReadOnlyList<Category>? dailyEvents = null)
    {
        Schedule = schedule;
        fixedRows = fixedEvents ?? [];
        local = calendar;
        automated = automatedEvents ?? [];
        daily = dailyEvents ?? [];
        automaticCursor = calendar?.EpochUtc ?? 0;
        automaticDraws = calendar is null ? null : new(calendar.Seed, "automated");
    }

    private sealed record ScheduleContent
    {
        public List<ScheduleEntry>? FixedEvents { get; init; } = [];
        public LocalCalendarContent? LocalCalendar { get; init; }
    }

    private sealed record LocalCalendarContent
    {
        public bool Enabled { get; init; }
        public string? Seed { get; init; }
        public string? EpochUtc { get; init; }
    }

    private sealed record ScheduleEntry
    {
        public string? Event { get; init; }
        public string? StartUtc { get; init; }
        public string? EndUtc { get; init; }
        public string? SourceURL { get; init; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Called once by ServerHost before listeners/world ticks run. Fixed intervals require a source;
    /// generated intervals require an explicit local seed/epoch and use the installed weights and durations.</summary>
    public static FestivitiesData Load(string gameDataFolder, string schedulePath)
    {
        if (!File.Exists(schedulePath))
        {
            Log.Info("content", $"{schedulePath} missing: no scheduled fixed festivities");
            return Empty;
        }
        try
        {
            var content = JsonSerializer.Deserialize<ScheduleContent>(File.ReadAllText(schedulePath), Options)
                ?? throw new InvalidDataException("not a JSON object");
            if (content.FixedEvents is null) throw new InvalidDataException("FixedEvents must be an array");
            if (content.FixedEvents.Count == 0 && content.LocalCalendar?.Enabled != true) return Empty;
            var root = GameDataFile.Open(gameDataFolder, "festivities.bin").Root();
            var catalogue = root.Element("fixedevents")
                ?? throw new InvalidDataException("festivities.bin has no fixed-event catalogue");
            var definitions = catalogue.Elements("event").ToDictionary(e => (string?)e.Attribute("uniquename") ?? "", StringComparer.Ordinal);
            var rows = new List<FixedEvent>();
            var fixedEvents = new List<Festivity>();
            foreach (var entry in content.FixedEvents)
            {
                if (entry is null || entry.Event is not { Length: > 0 } name
                    || !definitions.TryGetValue(name, out var definition)
                    || (string?)definition.Attribute("showinactivityboard") != "true"
                    || string.IsNullOrWhiteSpace((string?)definition.Attribute("title"))
                    || string.IsNullOrWhiteSpace((string?)definition.Attribute("image")))
                    throw new InvalidDataException("fixed event must name a visible entry of the installed festivities.bin");
                var start = Utc(entry.StartUtc, name + ".StartUtc");
                var end = Utc(entry.EndUtc, name + ".EndUtc");
                if (end <= start) throw new InvalidDataException($"{name}: EndUtc must be after StartUtc");
                if (!Uri.TryCreate(entry.SourceURL, UriKind.Absolute, out var source) || source.Scheme is not ("https" or "http"))
                    throw new InvalidDataException($"{name}: SourceURL must identify the published calendar");
                rows.Add(new(name, start, end, source.AbsoluteUri));
                fixedEvents.Add(new(0, "", name, start, end, Buffs(definition), []));
            }
            var sorted = rows.OrderBy(r => r.StartUtc).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
            foreach (var group in sorted.GroupBy(r => r.Name, StringComparer.Ordinal))
            {
                long previousEnd = 0;
                foreach (var row in group)
                {
                    if (row.StartUtc < previousEnd) throw new InvalidDataException($"{row.Name}: overlapping fixed-event intervals");
                    previousEnd = row.EndUtc;
                }
            }
            Calendar? calendar = null;
            IReadOnlyList<Category> automated = [], daily = [];
            if (content.LocalCalendar is { Enabled: true } settings)
            {
                if (settings.Seed is not { Length: > 0 and <= 256 } seed)
                    throw new InvalidDataException("LocalCalendar.Seed must be an explicit nonempty local seed (up to 256 characters)");
                var epoch = Utc(settings.EpochUtc, "LocalCalendar.EpochUtc");
                var autoRoot = root.Element("automatedevents") ?? throw new InvalidDataException("missing automatedevents");
                var dailyRoot = root.Element("dailyevents") ?? throw new InvalidDataException("missing dailyevents");
                var times = GameDataFile.Open(gameDataFolder, "times.bin").Root();
                foreach (var section in new[] { autoRoot, dailyRoot })
                {
                    var reference = Required(section, "starttime");
                    var time = times.Elements("Time").SingleOrDefault(t => (string?)t.Attribute("uniquename") == reference);
                    if (!TimeSpan.TryParse((string?)time?.Attribute("time"), CultureInfo.InvariantCulture, out var reset)
                        || new DateTime(epoch, DateTimeKind.Utc).TimeOfDay != reset)
                        throw new InvalidDataException($"LocalCalendar.EpochUtc must align with {reference} of times.bin");
                }
                calendar = new(seed, epoch, checked((long)Int(autoRoot, "previewhours") * TimeSpan.TicksPerHour));
                automated = ReadCategories(autoRoot, daily: false);
                daily = ReadCategories(dailyRoot, daily: true);
                if (!automated.Any(c => c.Weight > 0 && c.Events.Any(e => e.Weight > 0)))
                    throw new InvalidDataException("local automated calendar has no positive-weight event");
            }
            Log.Info("content", $"festivities: {sorted.Length} explicit fixed interval(s); local calendar {(calendar is null ? "off" : $"on, seed '{calendar.Seed}', epoch {new DateTime(calendar.EpochUtc, DateTimeKind.Utc):O}")}; generated dates are local, not the official draw");
            return new(Array.AsReadOnly(sorted), fixedEvents.AsReadOnly(), calendar, automated, daily);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or InvalidOperationException or ArgumentException or System.Xml.XmlException or OverflowException)
        {
            Log.Error("content", $"{schedulePath}: {e.Message}; no festivities published");
            return Empty;
        }
    }

    /// <summary>Authoritative active rows for both bonuses and UI. End is exclusive. Seed and epoch are local;
    /// cooldowns run from the previous event/category's end. The local daily policy uses distinct crafting categories.</summary>
    public IReadOnlyList<Festivity> ActiveAt(long gameNow) { lock (gate) return Evaluate(gameNow).Active; }

    internal IReadOnlyList<Festivity> PublishedAt(long gameNow) { lock (gate) return Evaluate(gameNow).Published; }
    internal long NextChangeAt(long gameNow) { lock (gate) return Evaluate(gameNow).Until; }

    private Evaluation Evaluate(long now)
    {
        if (cached is not null && cachedFrom <= now && now < cached.Until) return cached;
        var active = fixedRows.Where(r => r.StartUtc <= now && now < r.EndUtc).ToList();
        var published = new List<Festivity>();
        var boundary = long.MaxValue;
        void At(long ticks) { if (ticks > now) boundary = Math.Min(boundary, ticks); }
        foreach (var row in fixedRows) { At(row.StartUtc); At(row.EndUtc); }
        if (local is { } calendar)
        {
            At(calendar.EpochUtc);
            EnsureAutomatic(Add(now, calendar.PreviewTicks));
            foreach (var row in automaticRows.Where(r => r.EndUtc > now))
            {
                At(row.StartUtc); At(row.EndUtc); At(row.StartUtc - calendar.PreviewTicks);
                if (row.StartUtc <= now) active.Add(row);
                else if (row.StartUtc <= Add(now, calendar.PreviewTicks)) published.Add(row);
            }
            At(automaticCursor); At(automaticCursor - calendar.PreviewTicks);
            if (now >= calendar.EpochUtc)
            {
                var day = (now - calendar.EpochUtc) / TimeSpan.TicksPerDay;
                var start = Add(calendar.EpochUtc, day * TimeSpan.TicksPerDay);
                var end = Add(start, TimeSpan.TicksPerDay);
                At(end);
                foreach (var category in daily)
                {
                    var draws = new Draws(calendar.Seed, "daily:" + day.ToString(CultureInfo.InvariantCulture) + ":" + category.Name);
                    var choices = category.Events.Where(e => e.Weight > 0).ToList();
                    for (var i = 0; i < category.Quantity && choices.Count > 0; i++)
                    {
                        var entry = Pick(choices, e => e.Weight, draws);
                        // Local policy: a common/rare pair of the same crafting category cannot occupy both slots.
                        choices.RemoveAll(e => e.CraftBonuses.Any(b => entry.CraftBonuses.Any(chosen => chosen.Category == b.Category)));
                        active.Add(Row(2, category, entry, start, end));
                    }
                }
            }
        }
        published.AddRange(active);
        var ordered = published.OrderBy(r => r.StartUtc).ThenBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
        cachedFrom = now;
        return cached = new(active.AsReadOnly(), Array.AsReadOnly(ordered), boundary);
    }

    private void EnsureAutomatic(long through)
    {
        if (local is null || automaticDraws is null) return;
        while (automaticCursor <= through && automaticCursor < DateTime.MaxValue.Ticks)
        {
            var start = automaticCursor;
            var categories = automated.Where(c => c.Weight > 0
                && (!lastCategoryEnd.TryGetValue(c.Name, out var last) || Add(last, Days(c.CooldownDays)) <= start)
                && c.Events.Any(e => Eligible(c, e, start))).ToArray();
            if (categories.Length == 0)
            {
                // Honour every cooldown. Jump straight to the first eligible downtime; do not spin over empty days
                // or fabricate a replacement. Preview publication wakes at that start minus previewhours.
                automaticCursor = automated.Where(c => c.Weight > 0).SelectMany(c => c.Events.Where(e => e.Weight > 0)
                    .Select(e => ReadyAt(c, e, start))).DefaultIfEmpty(DateTime.MaxValue.Ticks).Min();
                if (automaticCursor <= start) throw new InvalidDataException("local festivity calendar cannot advance");
                continue;
            }
            var category = Pick(categories, c => c.Weight, automaticDraws);
            var entry = Pick(category.Events.Where(e => Eligible(category, e, start)).ToArray(), e => e.Weight, automaticDraws);
            var days = entry.MinDays + automaticDraws.Next(entry.MaxDays - entry.MinDays + 1);
            var end = Add(start, Days(days));
            automaticRows.Add(Row(1, category, entry, start, end));
            lastCategoryEnd[category.Name] = end;
            lastEventEnd[category.Name + ":" + entry.Name] = end;
            automaticCursor = end;
        }
    }

    private bool Eligible(Category category, Definition entry, long start) => entry.Weight > 0
        && (!lastEventEnd.TryGetValue(category.Name + ":" + entry.Name, out var last)
            || Add(last, Days(entry.CooldownDays)) <= start);

    private long ReadyAt(Category category, Definition entry, long start)
    {
        var categoryAt = lastCategoryEnd.TryGetValue(category.Name, out var previousCategory)
            ? Add(previousCategory, Days(category.CooldownDays)) : start;
        var eventAt = lastEventEnd.TryGetValue(category.Name + ":" + entry.Name, out var previousEvent)
            ? Add(previousEvent, Days(entry.CooldownDays)) : start;
        return Math.Max(start, Math.Max(categoryAt, eventAt));
    }

    private static Festivity Row(byte kind, Category category, Definition entry, long start, long end) =>
        new(kind, category.Name, entry.Name, start, end, entry.BuffSpells, entry.CraftBonuses);

    private static IReadOnlyList<Category> ReadCategories(XElement section, bool daily)
    {
        var categories = new List<Category>();
        foreach (var category in section.Elements("eventcategory"))
        {
            var entries = new List<Definition>();
            foreach (var entry in category.Elements())
            {
                var name = Required(entry, "uniquename");
                var weight = Int(entry, "weight");
                if (daily)
                {
                    if (entry.Name != "craftbonusevent") throw new InvalidDataException($"unsupported daily effect {entry.Name}");
                    if (!double.TryParse(Required(entry, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                        || !double.IsFinite(value) || value < 0) throw new InvalidDataException($"{name}: invalid craft value");
                    entries.Add(new(name, weight, 0, 1, 1, [], Array.AsReadOnly(new[] { new CraftBonus(Required(entry, "category"), value) })));
                }
                else
                {
                    if (entry.Name != "event") throw new InvalidDataException($"unsupported automated entry {entry.Name}");
                    var min = Int(entry, "mindurationdays", min: 1);
                    var max = Int(entry, "maxdurationdays", min: min);
                    var cooldown = Int(entry, "cooldowndays");
                    _ = Days(max); _ = Days(cooldown); // validate before a live tick can use the values
                    entries.Add(new(name, weight, cooldown, min, max, Buffs(entry), []));
                }
            }
            if (entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != entries.Count)
                throw new InvalidDataException("duplicate event in festivity category");
            var quantity = daily ? Int(category, "quantity", min: 1) : 1;
            if (daily && entries.Where(e => e.Weight > 0).SelectMany(e => e.CraftBonuses).Select(b => b.Category)
                .Distinct(StringComparer.Ordinal).Count() < quantity)
                throw new InvalidDataException("daily quantity exceeds distinct positive-weight craft categories");
            var categoryCooldown = Int(category, "cooldowndays", 0);
            _ = Days(categoryCooldown);
            categories.Add(new(Required(category, "uniquename"), daily ? 1 : Int(category, "weight"),
                categoryCooldown, quantity, entries.AsReadOnly()));
        }
        return categories.AsReadOnly();
    }

    private static IReadOnlyList<string> Buffs(XElement entry) => Array.AsReadOnly(entry.Elements("buff")
        .Select(b => Required(b, "spell")).Distinct(StringComparer.Ordinal).ToArray());

    private static string Required(XElement entry, string attribute) => (string?)entry.Attribute(attribute) is { Length: > 0 } value
        ? value : throw new InvalidDataException($"{entry.Name}: missing {attribute}");

    private static int Int(XElement entry, string attribute, int? fallback = null, int min = 0) =>
        entry.Attribute(attribute) is null && fallback is { } alternative ? alternative
            : int.TryParse((string?)entry.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min
                ? value : throw new InvalidDataException($"{entry.Name}: invalid {attribute}");

    private static long Add(long ticks, long duration) => ticks > DateTime.MaxValue.Ticks - duration ? DateTime.MaxValue.Ticks : ticks + duration;
    private static long Days(long days) => checked(days * TimeSpan.TicksPerDay);

    private static T Pick<T>(IReadOnlyList<T> entries, Func<T, int> weight, Draws draws)
    {
        var roll = draws.Next(entries.Sum(e => (long)weight(e)));
        foreach (var entry in entries) { roll -= weight(entry); if (roll < 0) return entry; }
        throw new InvalidDataException("no positive-weight festivity choice");
    }

    /// <summary>Local portable draws, independent of System.Random/runtime versions. Each stream is SHA256
    /// of seed, stream name and draw index; rejection avoids modulo bias. It is not the official RNG/seed.</summary>
    private sealed class Draws(string seed, string stream)
    {
        private long index;
        public long Next(long bound)
        {
            if (bound <= 0) throw new InvalidDataException("empty festivity weight range");
            var limit = (ulong)bound;
            var threshold = unchecked(0UL - limit) % limit;
            ulong value;
            do
            {
                var key = seed + "\0" + stream + "\0" + (index++).ToString(CultureInfo.InvariantCulture);
                value = BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            } while (value < threshold);
            return (long)(value % limit);
        }
    }

    private static long Utc(string? text, string field) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var utc)
        && utc.Kind == DateTimeKind.Utc && utc.Ticks > 0 ? utc.Ticks
            : throw new InvalidDataException($"{field}: an explicit UTC date ending in Z is required");
}
