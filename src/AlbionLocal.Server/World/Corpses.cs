using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server;

/// <summary>
/// The skinnable corpses of killed mobs (HARVEST.md §2 "Event 40", stage 5 SPEC §5c.1): a mob whose mobs.xml &lt;Loot&gt;
/// has a &lt;Harvestable type tier&gt; that is not silver (HIDE of the toads, snakes and lizards, FIBER_CRITTER of the
/// dryads, the *_CRITTER, *_GUARDIAN and *_TREASURE types) leaves a <see cref="Harvestable"/> of that type and tier where
/// it died. It is made in the kill batch where the silver pile goes, right after UpdateFame and before EnergyUpdate
/// (capture 002835 +522.332, the own T4 dryad kill: UpdateFame, NewHarvestableObject, EnergyUpdate,
/// CastSpell, the combat end, then the death clear and the lethal hit), and goes to the players in view as
/// NewHarvestableObject 40 with the corpse keys (<see cref="Harvestable.BuildCreate"/>); the mob's own Leave follows 5 s
/// later as before, the corpse stays. It is gathered through the stage-4a path (World/Harvesting.cs: the type's tool
/// rules, durations and yields, a skinning knife for HIDE) and does not regrow (FIBER_CRITTER has respawntimeseconds 0;
/// HIDE's 60-500 s are the hide nodes' of the open world, not a corpse's). Loot protection: until key 2 = the kill +
/// gamedata &lt;Loot mobprotection&gt; (120 s: spawn + 120.0 s on both own captured kills) or the entry's
/// lootprotectiontimeoverride, only the killer and its party at the kill may harvest it (<see cref="HarvestableCorpse.MayHarvest"/>).
/// It is left by the normal visibility (captured: Leave at cell distance 5-6, never at depletion; one HIDE corpse was
/// announced 6 times) and taken off its map the tier's decaytimeseconds after the kill (harvestables.xml; HIDE T1 120, T3-T6
/// 3600 s), depleted or not. Not used: decaytimewhenexhaustedseconds (HIDE T3 360, T4 600 s after the last charge), which
/// the capture weakly contradicts: in the Join burst at +20.267 9 corpses arrive with key 10 = 0, and from their key 2
/// (the kill + 120 s) the depleted MOB_WOLF ones (HIDE T3) were at least 2290, 976 and 589 s old and the MOB_BOAR one
/// (HIDE T4) 1087 s, all still there long past that time unless each was skinned only just before; decaytimeseconds fits
/// all of them. Not used either: the entry's positionoffset (12 big mobs; in what frame it applies is not known), so the
/// corpse lies at the death position as every captured one, and the tier's randomizespawncharges (HIDE T7/T8; the roll's
/// range is not known, no such corpse was captured), so those start full too. Touched only under <see cref="World.Sync"/>.
/// </summary>
public static class Corpses
{
    /// <summary>The decay time of a tier without decaytimeseconds (every corpse type has it; HIDE T1 120 s).</summary>
    public const double DefaultDecaySeconds = 120;

    /// <summary>The spawn key slot of the corpses ("&lt;cluster&gt;:corpse:&lt;n&gt;").</summary>
    public const string Slot = "corpse";

    /// <summary>
    /// The corpses of <paramref name="mob"/>, killed by <paramref name="killer"/> at <paramref name="ticks"/> (the kill's
    /// GameTimeStamp), onto its map: one per non-silver &lt;Loot&gt;&lt;Harvestable&gt; entry whose type and tier are in
    /// harvestables.xml, with the tier's startcharges (FIBER_CRITTER T3/T4 3), else its full charges (HIDE T1 2, T3 3: the
    /// captured giant toad corpse had 3), rare state 0 (as all 10 captured corpses).
    /// </summary>
    public static void Drop(World world, Player killer, Mob mob, long ticks)
    {
        var map = mob.Map!;
        foreach (var entry in mob.Template.Loot?.Elements("Harvestable") ?? [])
        {
            var name = (string?)entry.Attribute("type") ?? "";
            if (name.Length == 0 || name.StartsWith("SILVERCOINS", StringComparison.Ordinal)) continue;
            int.TryParse((string?)entry.Attribute("tier"), out var tier);
            if (world.Clusters?.Harvestables.Find(name) is not { } type || type.Tiers.GetValueOrDefault(tier) is not { } data)
            {
                Combat.LogOnce("corpse:" + name + tier, $"corpse {name} T{tier} of {mob.Template.Name} is not in harvestables.xml: none left");
                continue;
            }
            var charges = data.StartCharges > 0 ? data.StartCharges : data.Charges;
            if (charges <= 0) continue;
            var protection = Seconds((string?)entry.Attribute("lootprotectiontimeoverride"))
                ?? world.GameData?.MobLootProtectionSeconds ?? Game.GameData.DefaultMobLootProtection;
            var id = world.ObjectIds.Next();
            var spawn = new HarvestableSpawn
            {
                Key = new SpawnKey(map.Cluster, Slot, ++map.CorpseCount),
                Tile = data.Attrs.GetValueOrDefault("tile") ?? "",
                Position = mob.Position,
                Direction = mob.Direction,
                Component = entry,
                Type = type,
                Tier = data,
            };
            var party = world.Parties.Of(killer.Character.Id)?.Id ?? 0;
            var corpse = new Harvestable(id, spawn)
            {
                Charges = charges,
                Corpse = new HarvestableCorpse(mob, killer.Character.Id, party, ticks + TimeSpan.FromSeconds(protection).Ticks)
                {
                    DecayAt = world.Clock.Now + TimeSpan.FromSeconds(Seconds(data.Attrs.GetValueOrDefault("decaytimeseconds")) ?? DefaultDecaySeconds),
                },
            };
            map.AddObject(corpse);
            Schedule(corpse);
            Log.Info("combat", string.Create(CultureInfo.InvariantCulture,
                $"{mob} left {corpse} with {charges} charges, protected for '{killer.Character.Name}'{(party != 0 ? $" and party {party}" : "")} for {protection:0.#} s"));
        }
    }

    /// <summary>
    /// Takes <paramref name="node"/> off its map (a Leave to every client that knows it); a harvest of it under way is
    /// cancelled with the HarvestCancel event.
    /// </summary>
    public static void Remove(Harvestable node, string why)
    {
        if (node.Map is not { } map || !map.Objects.ContainsKey(node.Id)) return;
        foreach (var p in map.Players.Values)
            if (p.Harvest?.Node == node) p.Session.CancelHarvest(p, "the corpse is gone", tellClient: true);
        map.RemoveObject(node);
        Log.Debug("combat", $"{node} removed: {why}");
    }

    /// <summary>A timer at the corpse's <see cref="HarvestableCorpse.DecayAt"/> that takes it off its map, depleted or not.</summary>
    private static void Schedule(Harvestable node)
    {
        var map = node.Map!;
        map.Timers.At(node.Corpse!.DecayAt, () => Remove(node, node.Charges > 0 ? "it decayed" : "it decayed exhausted"));
    }

    private static double? Seconds(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
}
