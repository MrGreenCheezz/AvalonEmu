using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace AlbionLocal.Server.Game;

/// <summary>
/// One look of characters.xml AvatarToCustomizationPresets (stage I bots, bots-entity SPEC §1.4) as NewCharacter sends it: the template
/// (0 HUMAN_MALE, 1 HUMAN_FEMALE), key 5 the mesh index of each customization item, key 6 its colour index, key 2 the avatar
/// (AVATAR_nn − 1, the index into avatars.xml).
/// </summary>
public sealed record LookPreset(int Template, byte Avatar, int[] Customization, int[] Clothes);

/// <summary>
/// The customization items of one template in file order (b3d.g_7 reads keys 5/6 by this order): each item's free meshes (no
/// state="Buyable", the ones a new character may pick; empty for an item without meshes, SKIN) and its colour count.
/// </summary>
public sealed record CustomizationItem(string Name, int[] FreeMeshes, int Colors)
{
    /// <summary>All mesh resource ids in client order, including paid and empty meshes.</summary>
    public string?[] Meshes { get; init; } = [];
}

/// <summary>
/// Stage I bots: the looks a bot may have (bots SPEC §1.5): the 22 presets of characters.xml (11 avatars × male / female), known-good
/// combinations, and per template the customization items for a random look. <see cref="None"/> without the client's characters.xml
/// (the bots then have the default look).
/// </summary>
public sealed class CharacterLooks
{
    public static CharacterLooks None { get; } = new([], new Dictionary<int, IReadOnlyList<CustomizationItem>>());

    private CharacterLooks(IReadOnlyList<LookPreset> presets, IReadOnlyDictionary<int, IReadOnlyList<CustomizationItem>> items)
    {
        Presets = presets;
        Items = items;
    }

    public IReadOnlyList<LookPreset> Presets { get; }

    /// <summary>The customization items by template (0 male, 1 female).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<CustomizationItem>> Items { get; }

    /// <summary>Login key 18 is cpr's JSON dictionary, not the Join arrays. cpr.b(string)
    /// reads item names and MeshId/ColorPosition; mesh ids are resource names, not numeric indices.</summary>
    public string SelectionJson(Character character)
    {
        if (!Items.TryGetValue(character.Appearance, out var items)
            || character.Customization.Length != items.Count || character.Clothes.Length != items.Count) return "";
        var settings = new Dictionary<string, object>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var mesh = character.Customization[i];
            if (item.Meshes.Length > 0 && (mesh < 0 || mesh >= item.Meshes.Length)) return "";
            // Bald/beardless entries have no mesh resource. cpr resolves them by the empty
            // string; null does not match that entry and produces "Could not set item: HAIR".
            settings[item.Name] = new { MeshId = item.Meshes.Length == 0 ? null : item.Meshes[mesh] ?? "",
                ColorPosition = character.Clothes[i] };
        }
        return JsonSerializer.Serialize(settings);
    }

    /// <summary>The template id of a characters.xml character type (HUMAN_MALE 0, HUMAN_FEMALE 1); -1 for another.</summary>
    private static int TemplateOf(string? type) => type switch { "HUMAN_MALE" => 0, "HUMAN_FEMALE" => 1, _ => -1 };

    /// <summary>
    /// characters.xml's Customizations (Customization uniquename = template, its Items with Meshes, the ColorSets) and its
    /// AvatarToCustomizationPresets (Preset avatar charactertype, CustomizationItem item mesh color): a preset's mesh is the index of the
    /// item's Mesh with that mesh attribute (none: the Mesh without one, the bald / beardless index 0), its colour the color attribute (0).
    /// An entry for an item the template does not have is skipped (every HUMAN_FEMALE preset lists a BEARD); a preset naming an unknown
    /// mesh is left out.
    /// </summary>
    public static CharacterLooks Read(XElement characters)
    {
        var colorSets = characters.Descendants("ColorSet").ToDictionary(c => (string?)c.Attribute("uniquename") ?? "", c => c.Elements("Color").Count(), StringComparer.Ordinal);
        var items = new Dictionary<int, IReadOnlyList<CustomizationItem>>();
        var meshes = new Dictionary<(int, string), List<string?>>();
        foreach (var c in characters.Descendants("Customization"))
        {
            var template = TemplateOf((string?)c.Attribute("uniquename"));
            if (template < 0) continue;
            var list = new List<CustomizationItem>();
            foreach (var item in c.Elements("Item"))
            {
                var name = (string?)item.Attribute("uniquename") ?? "";
                var all = item.Elements("Mesh").ToList();
                meshes[(template, name)] = [.. all.Select(m => (string?)m.Attribute("mesh"))];
                var free = all.Select((m, i) => (m, i)).Where(x => (string?)x.m.Attribute("state") != "Buyable").Select(x => x.i).ToArray();
                list.Add(new CustomizationItem(name, free, colorSets.GetValueOrDefault((string?)item.Attribute("colorset") ?? ""))
                    { Meshes = [.. meshes[(template, name)]] });
            }
            items[template] = list;
        }
        var presets = new List<LookPreset>();
        foreach (var p in characters.Descendants("AvatarToCustomizationPresets").Elements("Preset"))
        {
            var template = TemplateOf((string?)p.Attribute("charactertype"));
            var avatar = (string?)p.Attribute("avatar") is { } a && a.StartsWith("AVATAR_", StringComparison.Ordinal)
                && int.TryParse(a["AVATAR_".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n - 1 : -1;
            if (template < 0 || avatar is < 0 or > 255 || items.GetValueOrDefault(template) is not { } order) continue;
            var custom = new int[order.Count];
            var clothes = new int[order.Count];
            var ok = true;
            foreach (var ci in p.Elements("CustomizationItem"))
            {
                var name = (string?)ci.Attribute("item") ?? "";
                var i = order.ToList().FindIndex(x => x.Name == name);
                if (i < 0) continue; // an item of the other template (BEARD in the female presets)
                var list = meshes[(template, name)];
                var mesh = (string?)ci.Attribute("mesh");
                var index = list.Count == 0 ? 0 : list.IndexOf(mesh);
                if (index < 0) { ok = false; break; }
                custom[i] = index;
                clothes[i] = int.TryParse((string?)ci.Attribute("color"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var color) ? color : 0;
            }
            if (ok) presets.Add(new LookPreset(template, (byte)avatar, custom, clothes));
        }
        return new CharacterLooks(presets, items);
    }
}
