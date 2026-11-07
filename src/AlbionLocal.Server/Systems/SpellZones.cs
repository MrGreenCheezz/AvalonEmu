using System.Numerics;
using System.Runtime.CompilerServices;
using AlbionLocal.Server.Data;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The ground emblems' zone spells (template tiles with &lt;spellzone spell&gt;, <see cref="ClusterTemplate.SpellZones"/>; a tester's report:
/// the Roads' speed buff did nothing). A zone spell is a ground cast every recastdelay (1 s) whose spelleffectarea (effectarearadius 3, allplayers)
/// gives its effect spell to the players in it; here each player within the radius gets that effect spell run on itself every second
/// (<see cref="SpellCasts.Execute"/>, its own conditions deciding: SPELLZONE_ROADS_SPEEDBUFF_EFFECT's movespeedbonus 0.7 for 5 s only while
/// mounted, SPELLZONE_CITY_SPEEDBUFF_EFFECT's outside a duel). Only the speed buffs (<see cref="Modelled"/>) are run: the other zone spells
/// of the data (MONITORED_SEA, SPELLZONE_MORGANA_SPIKES, TRADING_OUTPOST_SAFEAREA, SPELLZONE_SHRINE_BONUSFAME_STANDARD, …) are not modelled.
/// Bots are left out. Under <see cref="World.Sync"/>.
/// </summary>
public sealed class SpellZones(World world)
{
    /// <summary>The zone spells run here (the movement buffs of the Roads' and the cities' speed emblems).</summary>
    public static readonly IReadOnlySet<string> Modelled = new HashSet<string>(StringComparer.Ordinal) { "SPELLZONE_ROADS_SPEEDBUFF", "SPELLZONE_CITY_SPEEDBUFF" };

    /// <summary>How often a zone gives its effect (the zone spells' recastdelay 1).</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private sealed class Due
    {
        public TimeSpan At;
    }

    private readonly ConditionalWeakTable<Map, Due> _due = new();
    private readonly Dictionary<string, (SpellTemplate Effect, float Radius)?> _zones = new(StringComparer.Ordinal);

    /// <summary>A zone spell's effect spell and radius (its spelleffectarea); null when it is not modelled or not in spells.xml.</summary>
    private (SpellTemplate Effect, float Radius)? Zone(string name)
    {
        if (_zones.TryGetValue(name, out var known)) return known;
        (SpellTemplate, float)? zone = null;
        if (Modelled.Contains(name) && world.Spells?.Template(name) is { } spell
            && spell.Effects.FirstOrDefault(e => e.Name.LocalName == "spelleffectarea") is { } area
            && world.Spells.Template((string?)area.Attribute("effect") ?? "") is { } effect)
            zone = (effect, Math.Max(0.1f, SpellTemplate.Float(area, "effectarearadius")));
        return _zones[name] = zone;
    }

    /// <summary>The world tick of <paramref name="map"/>: every second, each modelled zone's effect on the players within its radius.</summary>
    public void Update(Map map)
    {
        if (map.HumanCount == 0 || map.Template is not { SpellZones.Count: > 0 } template) return;
        var due = _due.GetOrCreateValue(map);
        if (world.Now < due.At) return;
        due.At = world.Now + Every;
        var ticks = world.GameNow;
        var time = new DateTime(ticks, DateTimeKind.Utc);
        foreach (var spot in template.SpellZones)
        {
            if (Zone(spot.Spell) is not { } zone) continue;
            foreach (var p in map.Players.Values.ToList())
            {
                if (p.Map != map || p.Combat.Down is not null) continue;
                var at = p.PositionAt(time);
                if (Vector2.Distance(at, spot.Position) <= zone.Radius)
                    SpellCasts.Execute(new SpellCasts.SpellContext(p, map, zone.Effect, 0, 100, null, at, ticks), zone.Effect);
            }
        }
    }
}
