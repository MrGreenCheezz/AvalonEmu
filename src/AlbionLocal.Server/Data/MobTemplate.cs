using System.Xml.Linq;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Data;

/// <summary>How a mob treats players (mobs.xml npchostility): what NewMob key 2 (FlaggingStatus) is built from.</summary>
public enum NpcHostility
{
    /// <summary>The client's default (b3o.ms reads 0 when the attribute is missing).</summary>
    None,
    Hostile,
    Guard,
    Decoration,
    Friendly,
}

/// <summary>
/// One &lt;Mob&gt; of mobs.xml (stage 5 SPEC §5a.1; analysis/work/stage5/SPEC.md), with the client's defaults where an
/// attribute is missing (b3o.ms 0x1807efdf0, stage5/mobs/b3o_ms.clean.c). <see cref="Id"/> = the element's index + 16
/// (NewMob key 1; ids 0-15 are the 2 characters and 14 transformations that share the b3h counter, stage 2 SPEC §4,
/// checked on all 34 captured types). The numbers 5b-5f need are read now; <see cref="Loot"/> and <see cref="AI"/> are
/// kept as XML for 5c and 5f (copied out of the kept as XML for 5c and 5f. Immutable.lt;Mobkept as XML for 5c and 5f. Immutable.gt;, so the rest of its tree is not kept alive). Immutable.
/// </summary>
public sealed record MobTemplate
{
    /// <summary>First mob id: the index of the first &lt;Mob&gt; on the wire.</summary>
    public const short FirstId = 16;

    /// <summary>Index among the &lt;Mob&gt; elements of mobs.xml.</summary>
    public required int Index { get; init; }

    /// <summary>NewMob key 1, the client's mob type: <see cref="Index"/> + 16 (16..5494 in 345319; anything else NREs in the client).</summary>
    public short Id => (short)(Index + FirstId);

    public required string Name { get; init; }

    public int Tier { get; init; }

    /// <summary>faction, as written (UNDEAD, CRITTER, CITY_SWAMP, CITYGUARD; the client lower-cases it).</summary>
    public string Faction { get; init; } = "";

    /// <summary>mobtypecategory (standard, champion, miniboss, boss, summon, chest; "" when missing): stageH bosses SPEC §2, §4.2, §6.2.</summary>
    public string Category { get; init; } = "";

    /// <summary>Whether <see cref="Category"/> is champion, miniboss or boss: the elite picks Rates:ChampionWeight favours (stageH bosses SPEC §6.2).</summary>
    public bool IsElite => Category is "champion" or "miniboss" or "boss";

    /// <summary>
    /// category (roaming, static, rd_solo, rd_veteran, rd_elite, avalon, hidemob, territoryguards, …; "" when missing): what the fame
    /// buffs (mobfamebycategoryovertime) and IfTargetMobCategory ask about (stageN spells-bosses).
    /// </summary>
    public string FameCategory { get; init; } = "";

    /// <summary>dangerstate (normal, veteran, elite, roaming, booster; "" when missing): IfTargetMobDangerState.</summary>
    public string DangerState { get; init; } = "";

    public NpcHostility Hostility { get; init; }

    public float HitPointsMax { get; init; }

    public float HitPointsRegeneration { get; init; }

    public float EnergyMax { get; init; }

    public float EnergyRegeneration { get; init; }

    /// <summary>Idle walking speed (NewMob key 11; 0 for towers, which then omit the key).</summary>
    public float MoveSpeed { get; init; }

    /// <summary>Chase and return speed (default 5; 7.5 on 3162 mobs).</summary>
    public float AttackMoveSpeed { get; init; } = 5;

    /// <summary>Swings per second.</summary>
    public float AttackSpeed { get; init; } = 1;

    public float AttackRange { get; init; }

    public float AttackDamage { get; init; }

    public float AbilityPower { get; init; }

    public float MeleeAttackDamageTime { get; init; }

    /// <summary>melee, ranged, magic or none.</summary>
    public string AttackType { get; init; } = "none";

    /// <summary>0 = passive: fights back only when damaged.</summary>
    public float AggroRadius { get; init; }

    public float AggroConeAngle { get; init; }

    public float AggroConeLength { get; init; }

    public float AggroDelayAfterSpawn { get; init; }

    public bool CanAggroMountedPlayers { get; init; } = true;

    public float PursuitRadius { get; init; }

    /// <summary>passivepursuitradius, = <see cref="PursuitRadius"/> when missing.</summary>
    public float PassivePursuitRadius { get; init; }

    public float RoamingRadius { get; init; } = 10;

    public float RoamingIdleTimeMin { get; init; } = 1;

    public float RoamingIdleTimeMax { get; init; } = 3;

    public float AlertRadius { get; init; }

    /// <summary>alertsfactions ("all" when missing).</summary>
    public string AlertsFactions { get; init; } = "all";

    public float DamageAggroFactor { get; init; } = 1;

    public float HealingAggroFactor { get; init; } = 0.5f;

    public float MinThreatFactorInRange { get; init; } = 1.1f;

    public float MinThreatFactorOutOfRange { get; init; } = 1.3f;

    /// <summary>respawntimesecondsmin/max of a named spawn point (1 and 2 s when missing).</summary>
    public float RespawnSecondsMin { get; init; } = 1;

    public float RespawnSecondsMax { get; init; } = 2;

    /// <summary>How long the corpse stays (postDeathLifetime, at most 5 s; 5 when missing).</summary>
    public float PostDeathLifetime { get; init; } = 5;

    /// <summary>spawninvisibiltytime (sic), seconds; > 0 makes a fresh NewMob carry key 32.</summary>
    public float SpawnInvisibilityTime { get; init; }

    public bool DontResetAfterPursuit { get; init; }

    public bool KeepPositionOnReset { get; init; }

    public string? ReturnHomeSpell { get; init; }

    /// <summary>Energy the killer gets (never below 0).</summary>
    public float EnergyReward { get; init; }

    public string? EnergyRewardSpell { get; init; }

    public double Fame { get; init; }

    public double MinDivisor { get; init; }

    public float SilverModifier { get; init; } = 1;

    public string LootPrefab { get; init; } = "T0_LOOTBAG_PREMIUM";

    /// <summary>physicalarmor.</summary>
    public float Armor { get; init; }

    public float MagicResistance { get; init; }

    /// <summary>crowdcontrolresistance (ghoul 38, soldier 50, wolf 26; 99999 = immune): a crowd control lasts × 100 / (100 + it).</summary>
    public float CrowdControlResistance { get; init; }

    /// <summary>immunetoforcedmovement="true": no knockback, pull or fear.</summary>
    public bool ImmuneToForcedMovement { get; init; }

    /// <summary>collisionradius (0.75 on most mobs; 0 when missing): the range checks of the attacks on it count from its edge.</summary>
    public float CollisionRadius { get; init; }

    /// <summary>namelocatag (the "@MOB_..." text key; KnockedDown key 3, NewLoot key 3).</summary>
    public string? NameLocaTag { get; init; }

    /// <summary>The &lt;Loot&gt; element (5c), detached; null without one.</summary>
    public XElement? Loot { get; init; }

    /// <summary>The &lt;AI&gt; element (5f, BehaviourAI), detached; null without one.</summary>
    public XElement? AI { get; init; }

    /// <summary>&lt;DeathSpells&gt;&lt;deathspell name target&gt; (self, anyfriend, anythreat, anyenemy, killer): cast as it dies (stageN spells-bosses).</summary>
    public IReadOnlyList<(string Spell, string Target)> DeathSpells { get; init; } = [];

    /// <summary>&lt;SpawnSpells&gt;&lt;spawnspell name&gt;: cast on itself when it appears (MOB_IS_BOSS, the traps' invincibility, the hounds' auras).</summary>
    public IReadOnlyList<string> SpawnSpells { get; init; } = [];

    /// <summary>&lt;KillRewardBuff name target="threathistory"&gt;: applied to the players of its threat list at its death (the dragons' curse).</summary>
    public IReadOnlyList<(string Spell, string Target)> KillRewardBuffs { get; init; } = [];

    /// <summary>
    /// &lt;EscapeSpellSequence&gt;&lt;escapespell name target&gt; (self, escapeposition): what a tracking mob casts in order as it flees
    /// (stageN spells2, <see cref="AI.BehaviourAI.Escape"/>); empty without one. <see cref="EscapeReference"/>: the mob whose sequence it uses.
    /// </summary>
    public IReadOnlyList<(string Spell, string Target)> EscapeSpells { get; init; } = [];

    public string? EscapeReference { get; init; }

    public override string ToString() => $"{Name} ({Id})";
}

/// <summary>
/// mobs.xml (mobs.bin) as <see cref="MobTemplate"/>s, by id and by uniquename ignoring case (GM commands; the cluster data
/// writes the names as mobs.xml does). Loaded once per <see cref="ClusterStore"/>, off the world thread. Immutable.
/// </summary>
public sealed class MobStore
{
    private readonly MobTemplate[] _all;
    private readonly Dictionary<string, MobTemplate> _byName = new(StringComparer.OrdinalIgnoreCase);

    public MobStore(IEnumerable<MobTemplate> mobs)
    {
        _all = [.. mobs];
        foreach (var m in _all) _byName.TryAdd(m.Name, m); // a name twice: the first one, as the client's lookup by name
        Scripts = new AiScripts(this);
    }

    /// <summary>The compiled &lt;AI&gt; of each mob (stageD mobspells, <see cref="AiScript"/>), made on first use.</summary>
    public AiScripts Scripts { get; }

    public IReadOnlyList<MobTemplate> All => _all;

    public int Count => _all.Length;

    /// <summary>The mob of NewMob key 1 <paramref name="id"/>; null outside 16..(16 + count - 1).</summary>
    public MobTemplate? this[int id] => id - MobTemplate.FirstId is var i && i >= 0 && i < _all.Length ? _all[i] : null;

    public MobTemplate? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Up to <paramref name="count"/> names containing <paramref name="part"/> (ignoring case), shortest first: the hint of an unknown name.</summary>
    public IEnumerable<string> Like(string part, int count = 5) =>
        _all.Select(m => m.Name).Where(n => n.Contains(part, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).Take(count);

    /// <summary>
    /// mobs.bin: every &lt;Mob&gt; in file order, numbered from 16. A number that does not parse reads as its default and is
    /// warned once per attribute (<see cref="NumberReader"/>).
    /// </summary>
    public static MobStore Load(GameDataFile file)
    {
        var numbers = new NumberReader(file.Name);
        var mobs = new List<MobTemplate>();
        foreach (var e in file.Elements())
        {
            if (e.Name.LocalName != "Mob") continue;
            var name = (string?)e.Attribute("uniquename") ?? "";
            string? S(string a) => (string?)e.Attribute(a) is { Length: > 0 } v ? v : null;
            float F(string a, float fallback = 0) => (float)numbers.Num(S(a), a, name, fallback);
            bool B(string a, bool fallback = false) => S(a) is { } v ? string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) : fallback;
            var pursuit = F("pursuitradius");
            // b3o.ms clears the cone unless both values are positive.
            var (coneAngle, coneLength) = (F("aggrofrontalconeangle"), F("aggrofrontalconelength"));
            var cone = coneAngle > 0 && coneLength > 0;
            mobs.Add(new MobTemplate
            {
                Index = mobs.Count,
                Name = name,
                Tier = numbers.Int(S("tier"), "tier", name),
                Faction = S("faction") ?? "",
                Category = S("mobtypecategory")?.ToLowerInvariant() ?? "",
                FameCategory = S("category")?.ToLowerInvariant() ?? "",
                DangerState = S("dangerstate")?.ToLowerInvariant() ?? "",
                Hostility = Enum.TryParse<NpcHostility>(S("npchostility"), ignoreCase: true, out var h) ? h : NpcHostility.None,
                HitPointsMax = F("hitpointsmax"),
                HitPointsRegeneration = F("hitpointsregeneration"),
                EnergyMax = F("energymax"),
                EnergyRegeneration = F("energyregeneration"),
                MoveSpeed = F("movespeed"),
                AttackMoveSpeed = F("attackmovespeed", 5),
                AttackSpeed = F("attackspeed", 1),
                AttackRange = F("attackrange"),
                AttackDamage = F("attackdamage"),
                AbilityPower = F("abilitypower"),
                MeleeAttackDamageTime = F("meleeattackdamagetime"),
                AttackType = S("attacktype") ?? "none",
                AggroRadius = F("aggroradius"),
                AggroConeAngle = cone ? coneAngle : 0,
                AggroConeLength = cone ? coneLength : 0,
                AggroDelayAfterSpawn = F("aggrodelayafterspawn"),
                CanAggroMountedPlayers = B("canaggromountedplayers", true),
                PursuitRadius = pursuit,
                PassivePursuitRadius = F("passivepursuitradius", pursuit),
                RoamingRadius = F("roamingradius", 10),
                RoamingIdleTimeMin = F("roamingidletimemin", 1),
                RoamingIdleTimeMax = F("roamingidletimemax", 3),
                AlertRadius = F("alertradius"),
                AlertsFactions = S("alertsfactions") ?? "all",
                DamageAggroFactor = F("damageaggrofactor", 1),
                HealingAggroFactor = F("healingaggrofactor", 0.5f),
                MinThreatFactorInRange = F("minthreatfactorforchangetotargetinattackrange", 1.1f),
                MinThreatFactorOutOfRange = F("minthreatfactorforchangetotargetoutsideattackrange", 1.3f),
                RespawnSecondsMin = F("respawntimesecondsmin", 1),
                RespawnSecondsMax = F("respawntimesecondsmax", 2),
                PostDeathLifetime = Math.Min(5, F("postDeathLifetime", 5)),
                SpawnInvisibilityTime = F("spawninvisibiltytime"),
                DontResetAfterPursuit = B("dontresetafterpursuit"),
                KeepPositionOnReset = B("keepPositionOnReset"),
                ReturnHomeSpell = S("returnhomespell"),
                EnergyReward = Math.Max(0, F("energyreward")),
                EnergyRewardSpell = S("energyrewardspell"),
                Fame = numbers.Num(S("fame"), "fame", name),
                MinDivisor = numbers.Num(S("mindivisor"), "mindivisor", name),
                SilverModifier = F("silvermodifier", 1),
                LootPrefab = S("lootprefab") ?? "T0_LOOTBAG_PREMIUM",
                Armor = F("physicalarmor"),
                MagicResistance = F("magicresistance"),
                CrowdControlResistance = F("crowdcontrolresistance"),
                ImmuneToForcedMovement = B("immunetoforcedmovement"),
                CollisionRadius = F("collisionradius"),
                NameLocaTag = S("namelocatag"),
                Loot = e.Element("Loot") is { } loot ? new XElement(loot) : null,
                AI = e.Element("AI") is { } ai ? new XElement(ai) : null,
                DeathSpells = [.. (e.Element("DeathSpells")?.Elements("deathspell") ?? []).Select(d => ((string?)d.Attribute("name") ?? "", ((string?)d.Attribute("target") ?? "self").ToLowerInvariant()))
                    .Where(d => d.Item1.Length > 0)],
                SpawnSpells = [.. (e.Element("SpawnSpells")?.Elements("spawnspell") ?? []).Select(d => (string?)d.Attribute("name") ?? "").Where(n => n.Length > 0)],
                KillRewardBuffs = [.. e.Elements("KillRewardBuff").Select(d => ((string?)d.Attribute("name") ?? "", ((string?)d.Attribute("target") ?? "threathistory").ToLowerInvariant()))
                    .Where(d => d.Item1.Length > 0)],
                EscapeSpells = [.. (e.Element("EscapeSpellSequence")?.Elements("escapespell") ?? []).Select(d => ((string?)d.Attribute("name") ?? "", ((string?)d.Attribute("target") ?? "self").ToLowerInvariant()))
                    .Where(d => d.Item1.Length > 0)],
                EscapeReference = (string?)e.Element("EscapeSpellSequence")?.Attribute("reference"),
            });
        }
        numbers.Report();
        if (mobs.Count == 0) throw new InvalidDataException($"{file.Path}: no <Mob> elements");
        Log.Debug("data", $"{file.Name}: {mobs.Count} mobs (ids {MobTemplate.FirstId}..{MobTemplate.FirstId + mobs.Count - 1})");
        return new MobStore(mobs);
    }
}
