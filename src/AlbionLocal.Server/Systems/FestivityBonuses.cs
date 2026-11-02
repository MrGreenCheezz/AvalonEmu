using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Systems;

/// <summary>Calendar spells contribute their authored effects while the event is active. They are not
/// persisted as personal casts, and a spell already active on the player is counted only once.</summary>
public static class FestivityBonuses
{
    public static IEnumerable<XElement> AdditionalEffects(Player player, long now)
    {
        var spells = player.World.Festivities.ActiveAt(now).SelectMany(e => e.BuffSpells)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (spells.Length == 0) yield break;
        var represented = player.Effects.On(now).Select(e => e.Spell.Spell.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in spells)
            if (!represented.Contains(name) && player.World.Spells?.Template(name) is { } spell)
                foreach (var effect in spell.Effects)
                    if (AppliesToMap(player.World, player.Map, effect)) yield return effect;
    }

    /// <summary>A bound calendar effect applies only in one of its authored cluster archetypes.</summary>
    internal static bool AppliesToMap(World world, Map? map, XElement effect)
    {
        if ((string?)effect.Attribute("bindtoclusterarchetype") is not { Length: > 0 } bound) return true;
        string archetype;
        try { archetype = map?.Template?.Info.Type is { } type ? world.Clusters?.Archetypes.Of(type).Archetype ?? "" : ""; }
        catch (Exception e) when (e is InvalidDataException or IOException) { return false; }
        return bound.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(archetype, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Personal/passive attribute buffs plus the additional active calendar effects.</summary>
    public static double Sum(Player player, string attribute, long now) => player.Effects.Sum(attribute, now)
        + AdditionalEffects(player, now).Where(e => e.Name.LocalName == "buffovertime"
            && (string?)e.Attribute("type") == attribute).Sum(e => SpellTemplate.Float(e, "value"));
}
