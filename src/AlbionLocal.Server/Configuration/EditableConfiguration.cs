using System.Collections.ObjectModel;
using System.Text.Json;

namespace AlbionLocal.Server.Configuration;

/// <summary>Only explicitly supplied options. Missing keys remain distinguishable from preset defaults.</summary>
public sealed class EditableConfiguration
{
    public IReadOnlyDictionary<string, string> Values { get; }

    public EditableConfiguration(IEnumerable<KeyValuePair<string, string>> values)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
            if (!copy.TryAdd(key, value)) throw new InvalidDataException($"duplicate config key: {key}");
        Values = new ReadOnlyDictionary<string, string>(copy);
    }

    public static EditableConfiguration Parse(string json, string source = "configuration")
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: expected an object of sections");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Visit(document.RootElement, "", flatten: true);
        return new EditableConfiguration(values);

        void Visit(JsonElement element, string prefix, bool flatten)
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray()) Visit(item, prefix, flatten: false);
                return;
            }
            if (element.ValueKind != JsonValueKind.Object) return;
            if (prefix.Length > 0 && !element.EnumerateObject().Any())
                throw new InvalidDataException($"{source}: {prefix} is an empty object; omit optional sections instead");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";
                if (!names.Add(property.Name)) throw new InvalidDataException($"{source}: duplicate config key: {key}");
                if (property.Value.ValueKind == JsonValueKind.Null && !ConfigurationOptions.AllowsNull(key))
                    throw new InvalidDataException($"{source}: {key} cannot be null; omit the key to use its default");
                if (property.Value.ValueKind == JsonValueKind.Object) Visit(property.Value, key, flatten);
                else
                {
                    if (prefix.Length == 0 && !property.Name.Equals("Preset", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{source}: section {key} must be an object");
                    if (flatten && !values.TryAdd(key, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : JsonSerializer.Serialize(property.Value)))
                        throw new InvalidDataException($"{source}: duplicate config key: {key}");
                    Visit(property.Value, key, flatten: false);
                }
            }
        }
    }

    public ServerConfig Validate(string baseDirectory, IEnumerable<string>? arguments = null) =>
        ServerConfig.Validate(Values, baseDirectory, arguments ?? []);
}

public enum ConfigurationApplicationPolicy { RestartRequired, Reloadable }

/// <summary>Shared ownership and application metadata for loaders and future administration.</summary>
public static class ConfigurationOptions
{
    public static bool AllowsNull(string key) =>
        key.Equals("Roads:Seed", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Bots:Rewards:LootProtectionSeconds", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Hellgates:KnockdownSeconds", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> FileNames { get; } = Array.AsReadOnly(new[] { "common.jsonc", "login.jsonc", "world.jsonc", "chat.jsonc", "persistence.jsonc" });

    public static string Owner(string key)
    {
        if (key.Equals("Preset", StringComparison.OrdinalIgnoreCase) || key.Equals("Network:BindAddress", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Network:PublicAddress", StringComparison.OrdinalIgnoreCase) || key.Equals("Paths:ClientGameData", StringComparison.OrdinalIgnoreCase)
            || key.Equals("Paths:Content", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Log:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Web:", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Backup:", StringComparison.OrdinalIgnoreCase)) return "common.jsonc";
        if (key.Equals("Network:LoginPort", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Accounts:", StringComparison.OrdinalIgnoreCase)) return "login.jsonc";
        if (key.Equals("Network:ChatPort", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Chat:", StringComparison.OrdinalIgnoreCase)) return "chat.jsonc";
        if (key.Equals("Paths:Saves", StringComparison.OrdinalIgnoreCase) || key.StartsWith("Persistence:", StringComparison.OrdinalIgnoreCase)) return "persistence.jsonc";
        return "world.jsonc";
    }

    public static ConfigurationApplicationPolicy Policy(string key) =>
        key.StartsWith("Rates:", StringComparison.OrdinalIgnoreCase) && Game.Rates.Canonical(key[6..]) is not null
            ? ConfigurationApplicationPolicy.Reloadable : ConfigurationApplicationPolicy.RestartRequired;
}
