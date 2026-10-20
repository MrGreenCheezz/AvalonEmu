using System.Numerics;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Entities;

/// <summary>
/// A mob on a map (stage 5a, analysis/work/stage5/SPEC.md §5a; ARCHITECTURE.md §4.1 Mob): made from its mobs.xml
/// <see cref="Template"/> by <see cref="SpawnManager"/> at a spawn point of the cluster (<see cref="Slot"/>) or by a GM
/// (.mob spawn, no slot). It lives in the visibility grid like the static objects: entering a player's window sends
/// <see cref="BuildCreate"/> (NewMob, then MobChangeState when it has a rare state), leaving it a Leave; every NewMob is a
/// full update with the current state, so a re-entry is a fresh one. In 5a it runs no AI (NullAI): it stands at its
/// point facing the tile's rotation and sends no event 3. Its bars are lazy <see cref="ResourceBar"/>s, full at spawn.
/// Since 5b/5c players damage it (<see cref="Systems.Combat"/>; its health regenerates on) and it dies: a corpse for
/// postDeathLifetime (<see cref="Dead"/>), then off the map. Since 5d it has an <see cref="AI"/> (<see cref="MobAI.For"/>:
/// NullAI, PassiveAI or AggressiveAI by its mobs.xml attributes) that walks it (<see cref="Motion"/>, event 3), fights the
/// players on its <see cref="Threat"/> list and leashes it back to its <see cref="Home"/>. Its movement runs on the world's
/// GameTimeStamp (<see cref="World.GameNow"/>), so the self-test's manual clock moves it. Touched only under <see cref="World.Sync"/>.
/// </summary>
public sealed class Mob(long id, MobSpawnPoint spawn, MobTemplate template, byte flagging, long spawnStamp, TimeSpan spawnedAt) : WorldObject(id, spawn), IUnit
{
    public new MobSpawnPoint Spawn { get; } = spawn;

    public MobTemplate Template { get; } = template;

    /// <summary>The world it lives in: its clock (<see cref="World.GameNow"/>) and settings; null only for a mob made outside one.</summary>
    public World? World { get; init; }

    /// <summary>
    /// Where it stands when idle and walks back to on a leash (stage 5d, SPEC §5d.3): the spawn position and rotation, or where
    /// it stood at a reset when it has keepPositionOnReset.
    /// </summary>
    public Vector2 Home { get; internal set; } = spawn.Position;

    public float HomeDirection { get; internal set; } = spawn.Direction;

    /// <summary>The spawn point state it came from (respawn after its death); null for a GM mob, which never comes back.</summary>
    public MobSpawnSlot? Slot { get; init; }

    /// <summary>
    /// NewMob key 2, FlaggingStatus (<see cref="FlaggingOf"/>): 255 for a hostile mob, the city's faction for a guard (2 Forest,
    /// 5 Swamp, …).
    /// </summary>
    public byte Flagging { get; } = flagging;

    /// <summary>Health and energy as the client holds them (NewMob keys 13/14/16/17 and 18/19/21/22): full at the spawn, regenerating lazily.</summary>
    public ResourceBar Health = Full(template.HitPointsMax, template.HitPointsRegeneration, spawnStamp);

    public ResourceBar Energy = Full(template.EnergyMax, template.EnergyRegeneration, spawnStamp);

    /// <summary>
    /// The movement sample (stage 7 SPEC §2.2, as players): where the mob stands or walks to. NewMob keys 7, 8, 9, 10, 11 come
    /// from it, so a re-send never carries a stamp older than the last event 3 for the mob. 5a mobs stand at the point.
    /// </summary>
    public MovementState Movement { get; internal set; } =
        new(spawn.Position, spawn.Position, 0, new DateTime(spawnStamp, DateTimeKind.Utc)) { Direction = spawn.Direction, Stamp = spawnStamp };

    /// <summary>Rare (enchanted) state, MobChangeState key 1: 0 in 5a (the hide and fiber critters' roll waits, SPEC §9 Q7).</summary>
    public byte RareState { get; set; }

    /// <summary>NewMob key 12: the object id of its current target while in combat (5b-5d; its AI sets it); 0 = none, left out.</summary>
    public long TargetId { get; set; }

    /// <summary><see cref="World.Clock"/> time of the spawn: key 32 is sent within spawninvisibiltytime of it.</summary>
    public TimeSpan SpawnedAt { get; } = spawnedAt;

    /// <summary>Whether it was taken off its map (killed or despawned); its id is never used again.</summary>
    public bool Removed { get; internal set; }

    /// <summary>Its mobs.xml spawn spells have run (stageN spells-bosses, <see cref="Systems.MobEvents.Spawned"/>).</summary>
    internal bool SpawnSpellsCast { get; set; }

    /// <summary>
    /// Killed (stage 5c, SPEC §5c.1): a corpse until postDeathLifetime has passed, then taken off the map. Not targetable,
    /// no threat, its health 0 without regeneration, so a NewMob for it leaves key 13 out.
    /// </summary>
    public bool Dead { get; internal set; }

    /// <summary>
    /// The mob's own cast counter (CastStart key 8 / CastFinished key 4 of its casts; the ENERGYREWARD CastSpell at its death
    /// carries it, SPEC §5c.1): 0 until its first cast, then 1..255 and on from 1 (stageD mobspells §3; never reset but by a respawn).
    /// </summary>
    public byte CastCounter { get; internal set; }

    /// <summary>The counter of a new cast: <see cref="CastCounter"/> + 1, 255 wrapping to 1 (assumed, never captured).</summary>
    internal byte NextCastCounter() => CastCounter = CastCounter >= byte.MaxValue ? (byte)1 : (byte)(CastCounter + 1);

    /// <summary>
    /// Its threat list (stage 5d, SPEC §5d.1): the players it fights and their threat (damage × damageaggrofactor, healing ×
    /// healingaggrofactor, 1 for an aggro pick). Empty while idle, returning or dead.
    /// </summary>
    public Dictionary<Player, double> Threat { get; } = [];

    /// <summary>Its brain (stage 5d): made on first use from its mobs.xml attributes (<see cref="MobAI.For"/>).</summary>
    public MobAI AI => _ai ??= MobAI.For(this);

    private MobAI? _ai;

    /// <summary>Its walking (event 3 to the observers, <see cref="MotionMaster"/>).</summary>
    public MotionMaster Motion => _motion ??= new MotionMaster(this);

    private MotionMaster? _motion;

    /// <summary>
    /// A summoned mob (spawnmob, stageD effects batch 3, <see cref="Systems.Summons"/>): the mob or player that summoned it; null for a
    /// spawn point's mob.
    /// </summary>
    public IUnit? Summoner { get; internal set; }

    /// <summary>The spawnmob node that summoned it (ttl, ttlaftercasterdeath, limit); null for a spawn point's mob.</summary>
    internal System.Xml.Linq.XElement? SummonNode { get; set; }

    /// <summary>While it runs home with MOB_RETURNHOME's spellimmunity (SPEC §5d.3 step 4): damage and spells on it do nothing.</summary>
    public bool Immune { get; internal set; }

    /// <summary>Its active spells (stageD effects SPEC §1): the players' damage over time and debuffs, its return-home buff.</summary>
    public Systems.EffectList Effects => _effects ??= new() { Owner = this };

    private Systems.EffectList? _effects;

    /// <summary>StageH bosses SPEC §4.1: the world boss slot it stands for (<see cref="Systems.WorldBosses"/>); null for every other mob.</summary>
    public Systems.WorldBosses.Slot? Boss { get; init; }

    /// <summary>StageH (content/bosses.json HealthFactor): a factor on its maximum health (<see cref="Systems.SpellEffects.MobMaxHealth"/>); 1 but for a world boss, a world event's wave mob and an expedition's scaled mob.</summary>
    public double HealthFactor { get; init; } = 1;

    /// <summary>StageH (content/bosses.json DamageFactor): a factor on the damage it deals (<see cref="Systems.SpellEffects.DealtFactor"/>); 1 but for a world boss, a world event's wave mob and an expedition's scaled mob.</summary>
    public double DamageFactor { get; init; } = 1;

    /// <summary>The world event whose wave it belongs to (<see cref="Systems.WorldEvents"/>, PVE-DESIGN step 7): its damage counts for the reward; null for every other mob.</summary>
    public Systems.WorldEvents.Run? Event { get; init; }

    /// <summary>Stage I: a factor on its kill fame (a T9/T10 invasion's wave mob); 1 for every other mob.</summary>
    public double FameFactor { get; init; } = 1;

    /// <summary>Studio phase 3: the content pack's mob variant it is (mods/&lt;pack&gt;/server/mobs.json, <see cref="Mods.ModVariant"/>): its extra loot lists and drop rules; null for every other mob.</summary>
    public string? Variant { get; init; }

    /// <summary>StageG: the dungeon level's mobmodifier it carries from its spawn (<see cref="SpawnManager.LevelBuff"/>); null off a dungeon floor of level 1-4.</summary>
    public SpellTemplate? LevelBuff { get; internal set; }

    /// <summary>An event about the mob to every client that knows it.</summary>
    public void SendAround(AlbionEvent e) => Map?.SendToObservers(this, e);

    /// <summary>Whether attacks and spells may target it: alive, on its map, and a mob players fight (not a guard, decoration or friendly NPC).</summary>
    public bool Attackable => !Dead && !Removed && Map is not null && Template.Hostility is NpcHostility.Hostile or NpcHostility.None;

    /// <summary>Where it is now: its movement sample extrapolated to the world's GameTimeStamp (the stored target once it has arrived).</summary>
    public override Vector2 Position => Movement.PositionAt(Time);

    /// <summary>Now as the world's GameTimeStamp (<see cref="World.GameNow"/>; the UTC time without a world).</summary>
    public long GameNow => World?.GameNow ?? GameTime.Now;

    /// <summary><see cref="GameNow"/> as the DateTime the movement samples are extrapolated with.</summary>
    public DateTime Time => new(GameNow, DateTimeKind.Utc);

    public override float Direction => Movement.Direction;

    public override string Kind => "mob";
    public override string TypeName => RareState > 0 ? $"{Template.Name}.{RareState}" : Template.Name;

    /// <summary>Left when out of range, sent anew with its current state on re-entry (SPEC §5a.4: 233 Leave for 237 NewMob).</summary>
    public override bool LeaveOnRange => true;

    private static ResourceBar Full(float max, float regen, long stamp)
    {
        var bar = new ResourceBar();
        bar.Fill(max, regen, stamp);
        return bar;
    }

    /// <summary>
    /// The GameTimeStamp of a new movement sample: now, never earlier than the last one (the client's history a5m.b drops an
    /// older one). A standing mob takes it for its sample, so NewMob key 9 is always at least the previous one.
    /// </summary>
    internal long NextStamp() => Math.Max(GameNow, Movement.Stamp);

    /// <summary>
    /// NewMob (123, aug; SPEC §5a.3, client cjb.f 0x180aad7f0 → c0a ctor 0x180a12140), then MobChangeState (47) {0 id, 1 rare
    /// state} in the same batch when the rare state is not 0. Keys: 0 id; 1 mob type (index + 16); 2 FlaggingStatus, 6 ""
    /// (owner guild), 30 and 34 byte 0, always, as official; 7/8 position and target (= position standing); 9 the sample
    /// stamp; 10 facing and 11 movespeed, left out at 0 (towers); 12 the target only in combat; 13/14 health and max (13
    /// left out at 0, a corpse); 16 regeneration (out at 0); 17 the health stamp; 18/19/21 energy, max and regeneration
    /// (out at 0); 22 the energy stamp (now); 32 true within spawninvisibiltytime of the spawn. Never 3-5, 15, 20, 23-29,
    /// 31, 33 (guild owner and logo). A walking mob (5d) goes out with its stored sample (the last event 3: position at its
    /// stamp, target, stamp), which the client dead-reckons; a standing one with a fresh stamp at its position. A mob with
    /// active spells: its ActiveSpellEffectsUpdate right after (stageD effects SPEC §2, "a new observer gets the list").
    /// </summary>
    public override IEnumerable<AlbionEvent> BuildCreate(Player viewer)
    {
        var now = GameNow;
        var moving = Movement.PositionAt(Time) != Movement.Target;
        if (!moving) Movement = Movement with { From = Movement.Target, Speed = 0, Stamp = NextStamp() };
        var invisible = Template.SpawnInvisibilityTime > 0 && viewer.World.Now - SpawnedAt < TimeSpan.FromSeconds(Template.SpawnInvisibilityTime);
        yield return new NewMobEvent
        {
            c5b = Id,                                   // [0]
            c5c = Template.Id,                          // [1] index + 16
            c5i = Movement.From,                        // [7] position (at the stamp)
            c5j = Movement.Target,                      // [8] move target (= position standing)
            c5k = Movement.Stamp,                       // [9] movement sample stamp
            c5l = Movement.Direction,                   // [10] facing, left out at 0
            c5m = Template.MoveSpeed,                   // [11] movespeed, left out at 0 (towers)
            c5n = TargetId,                             // [12] only while in combat
            c5o = Health.At(now),                       // [13] left out at 0: a corpse
            c5p = Health.Max,                           // [14]
            c5r = Health.RegenPerSecond,                // [16] left out at 0
            c5s = Health.Stamp,                         // [17] the last health change (the spawn)
            c5t = Energy.At(now),                       // [18] left out at 0
            c5u = Energy.Max,                           // [19]
            c5w = Energy.RegenPerSecond,                // [21]
            c5x = now,                                  // [22] the energy stamp
            c57 = invisible,                            // [32] just spawned
            Extra = new() { [2] = Flagging, [6] = "", [30] = (byte)0, [34] = (byte)0 }, // always, also 0 and ""
        };
        if (RareState != 0) yield return BuildChangeState();
        if (Systems.SpellEffects.ForNewObserver(this, now) is { } effects) yield return effects;
    }

    /// <summary>MobChangeState (47, asq) {0 id, 1 byte rare state}.</summary>
    public MobChangeStateEvent BuildChangeState() => new() { ddn = Id, Extra = new() { [1] = RareState } };

    /// <summary>
    /// NewMob key 2 (SPEC §5a.3): 255 for a hostile mob; for a guard the city faction (Highland 1, Forest 2, Steppe 3,
    /// Mountain 4, Swamp 5, Caerleon 6) of its faction name (CITY_SWAMP, captured 5) or, for a faction without a city
    /// (CITYGUARD towers), of the tile and cluster (<see cref="MapSpawner.CityFaction"/>: the 0204 towers sent 5);
    /// 0 for the decoration and friendly mobs (9 in mobs.xml, never captured).
    /// </summary>
    public static byte FlaggingOf(MobTemplate template, string tile, ClusterName? cluster) => template.Hostility switch
    {
        NpcHostility.Hostile => 255,
        NpcHostility.Guard => template.Faction.StartsWith("CITY_", StringComparison.OrdinalIgnoreCase)
                && MapSpawner.CityFaction(template.Faction[5..] + "_", cluster ?? ClusterName.Parse("")) is { } city ? city
            : cluster is not null && MapSpawner.CityFaction(tile, cluster) is { } own ? own : (byte)0,
        _ => 0,
    };

    public override string ToString() => $"mob {TypeName} ({Template.Id}) #{Id} at ({Position.X:0.##}, {Position.Y:0.##})";
}
