namespace AlbionLocal.Server.Data;

/// <summary>The installed loadouts.bin limits and tag palette, shared by personal and guild builds.</summary>
public sealed record LoadoutData(int PlayerLimit, int GuildLimit, int Colors, int Icons)
{
    public static LoadoutData Load(string folder)
    {
        // Defaults match build 345319; minimal worlds without client files use the same bounds.
        if (!File.Exists(Path.Combine(folder, "loadouts.bin"))) return new(30, 100, 16, 24);
        var elements = GameDataFile.Open(folder, "loadouts.bin").Elements().ToArray();
        var settings = elements.FirstOrDefault(e => e.Name.LocalName == "settings");
        return new(Math.Clamp((int?)settings?.Attribute("maximumplayerloadouts") ?? 30, 1, 256),
            Math.Clamp((int?)settings?.Attribute("maximumguildloadouts") ?? 100, 1, 256),
            elements.FirstOrDefault(e => e.Name.LocalName == "colors")?.Elements().Count() ?? 16,
            elements.FirstOrDefault(e => e.Name.LocalName == "icons")?.Elements().Count() ?? 24);
    }

    public bool ValidHeader(string? name, int color, int icon) => name is { Length: <= 256 }
        && !name.Any(char.IsControl) && color >= 0 && color < Colors && icon >= 0 && icon < Icons;
}
