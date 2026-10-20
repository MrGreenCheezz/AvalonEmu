using System.Globalization;
using System.Xml.Linq;

namespace AlbionLocal.Server.Data;

/// <summary>A &lt;Var name type initialValue resetOnMobReset timeToLiveSeconds resetOnMobDeath&gt; of an &lt;AI&gt; (stageD mobspells SPEC R8).</summary>
public sealed record AiVar(string Name, string Type, string Initial, bool ResetOnMobReset, double TimeToLive, bool ResetOnMobDeath);

/// <summary>
/// A condition of an &lt;AI&gt; node (SPEC §4.1, ConditionMgr): the children of a node are ANDed (<see cref="AiAll"/>), &lt;or&gt;
/// is <see cref="AiAny"/>, &lt;not&gt; is NOT(AND(children)) (true without children), every If* tag is an <see cref="AiLeaf"/>
/// matched by its lower-case name, as the client's factory b0e.ef keys them.
/// </summary>
public abstract class AiCond;

public sealed class AiAll(AiCond[] parts) : AiCond
{
    public AiCond[] Parts { get; } = parts;
}

public sealed class AiAny(AiCond[] parts) : AiCond
{
    public AiCond[] Parts { get; } = parts;
}

public sealed class AiNot(AiCond[] parts) : AiCond
{
    public AiCond[] Parts { get; } = parts;
}

/// <summary>
/// One If* condition: <see cref="Kind"/> the lower-case tag (ifchance, iffightduration, …), its op (equals, notEquals, less,
/// lessOrEqual, greater, greaterOrEqual; client b0j.Operations) and the attributes the leaves read. Each leaf is its own object, so a
/// mob's AI keeps per-node state on it (the IfChance roll, SPEC R5).
/// </summary>
public sealed class AiLeaf(XElement e) : AiCond
{
    public string Kind { get; } = e.Name.LocalName.ToLowerInvariant();

    public string Tag { get; } = e.Name.LocalName;

    public string Op { get; } = (string?)e.Attribute("op") ?? "";

    /// <summary>value as written (IfVariable compares text, or numbers when both sides parse).</summary>
    public string Value { get; } = (string?)e.Attribute("value") ?? "";

    public double Number { get; } = AiScript.Num(e, "value");

    /// <summary>name (IfVariable), spell (IfSpellActive), category, who (Caster, SpellTarget, EffectTarget).</summary>
    public string? Name { get; } = (string?)e.Attribute("name");

    public string? Spell { get; } = (string?)e.Attribute("spell");

    public string? Category { get; } = (string?)e.Attribute("category");

    public string Who { get; } = (string?)e.Attribute("who") ?? "";

    /// <summary>range (IfIsAnyEnemyInRange, IfIsAnyFactionMemberInRange).</summary>
    public double Range { get; } = AiScript.Num(e, "range");

    public int AtLeastCharges { get; } = Math.Max(1, (int)AiScript.Num(e, "atleastcharges", 1));

    public bool CasterChargesOnly { get; } = AiScript.Flag(e, "casterchargesonly");

    public XElement Element { get; } = e;

    /// <summary>area (IfHighestArea, IfPlayersInArea, IfTargetIsInHighestArea): a named arc of the AI's TargetAreaDefinition.</summary>
    public string? Area { get; } = (string?)e.Attribute("area");

    /// <summary>The condition children of the leaf itself (IfTargetIsInHighestArea's IfTargetRange): the players an area counts; null = all.</summary>
    public AiCond? Inner { get; init; }

    /// <summary><paramref name="v"/> against <see cref="Number"/> with <see cref="Op"/>.</summary>
    public bool Compare(double v) => Op.ToLowerInvariant() switch
    {
        "less" => v < Number,
        "lessorequal" => v <= Number,
        "greater" => v > Number,
        "greaterorequal" => v >= Number,
        "equals" or "equal" => Math.Abs(v - Number) < 1e-6,
        "notequals" => Math.Abs(v - Number) >= 1e-6,
        _ => false,
    };

    /// <summary>A variable's text against <see cref="Value"/>: numbers when both parse, else text ignoring case (SPEC R8).</summary>
    public bool CompareText(string v)
    {
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            return Op.ToLowerInvariant() switch
            {
                "less" => a < b, "lessorequal" => a <= b, "greater" => a > b, "greaterorequal" => a >= b, "notequals" => a != b, _ => a == b,
            };
        var same = string.Equals(v, Value, StringComparison.OrdinalIgnoreCase);
        return Op.Equals("notEquals", StringComparison.OrdinalIgnoreCase) ? !same : same;
    }

    public override string ToString() => Tag;
}

/// <summary>An action node of an &lt;AI&gt; (SPEC §4.1): its conditions (<see cref="When"/>, null = always) and its SelectRandom weight.</summary>
public abstract class AiNode
{
    public AiCond? When { get; init; }

    public double Weight { get; init; } = 1;

    /// <summary>The node's own events (Spell: onCastStart, onCastSuccessful, onCastInterrupted, onChannelingHit; Movement: onMovementStarted/Finished).</summary>
    public IReadOnlyDictionary<string, AiNode[]> Events { get; init; } = AiScript.NoEvents;
}

/// <summary>
/// &lt;Spell name target …&gt;: the spell, the target kind (self, current, anythreat, anyfriend, anyenemy, killer,
/// currentortopthreatorself, escapeposition; b3z.TargetType), targetSelection, cooldowntillnextcast (R3), cooldowngroup, the
/// ground target offset/angle/relativeto, saytext, and the slot of the client's table (R7).
/// </summary>
public sealed class AiSpell : AiNode
{
    public required string Spell { get; init; }

    public string Target { get; init; } = "current";

    public string? Selection { get; init; }

    public double Cdtnc { get; init; }

    public string? Group { get; init; }

    public double GroundOffset { get; init; }

    public double GroundAngle { get; init; }

    /// <summary>groundtargetrelativeto="caster": the offset is taken from the mob (else from the spell target).</summary>
    public bool RelativeToCaster { get; init; }

    public bool NewSlot { get; init; }

    /// <summary>&lt;RandomGroundTarget radius innerradius origin&gt; (stageN spells2): the ground point is a random free point of that ring around the target (origin="target") or the mob.</summary>
    public (double Radius, double Inner, bool AtTarget)? RandomGround { get; init; }

    public string? SayText { get; init; }

    /// <summary>CastFinished key 3 (R7); 0 for a spell that only appears in events (Q13).</summary>
    public byte Slot { get; internal set; }

    /// <summary>Whether it has a slot of the table (false: it only appears in events, its cooldown is kept by spell name).</summary>
    public bool Numbered { get; internal set; }

    public override string ToString() => $"Spell {Spell} → {Target}";
}

/// <summary>
/// &lt;Movement target angle distance cooldowntillnextmovement autoattacksallowed requireslineofsight&gt; (5f.4, stageN spells-bosses): a
/// reposition of <see cref="Distance"/> m at <see cref="Angle"/>° from the line to its target (current, anythreat) or from its facing (self).
/// </summary>
public sealed class AiMovement : AiNode
{
    public string Target { get; init; } = "current";

    public double Angle { get; init; }

    public double Distance { get; init; }

    public double Cooldown { get; init; }

    public bool AutoAttacks { get; init; } = true;

    public bool LineOfSight { get; init; }
}

/// <summary>&lt;If&gt;: one condition set and one action (client b35).</summary>
public sealed class AiIf : AiNode
{
    public AiNode? Action { get; init; }
}

/// <summary>&lt;SelectFirst&gt;: the first child that works.</summary>
public sealed class AiSelectFirst : AiNode
{
    public AiNode[] Children { get; init; } = [];
}

/// <summary>&lt;SelectRandom behaviour&gt;: a weighted pick (b39: FailIfNotWorking 0, OnlySelectWorkingActions).</summary>
public sealed class AiSelectRandom : AiNode
{
    public AiNode[] Children { get; init; } = [];

    public bool FailIfNotWorking { get; init; }
}

/// <summary>&lt;CompoundAction&gt;: works when its first Spell child starts (or it has none), then runs the other children in order.</summary>
public sealed class AiCompound : AiNode
{
    public AiNode[] Children { get; init; } = [];
}

/// <summary>&lt;Variable name op value&gt;: set, add, subtract, negate (b4b).</summary>
public sealed class AiVariable : AiNode
{
    public required string Name { get; init; }

    public string Op { get; init; } = "set";

    public string Value { get; init; } = "";
}

/// <summary>&lt;BroadcastVariable name value radius includeSelf&gt;: sets the variable on the mobs within radius.</summary>
public sealed class AiBroadcast : AiNode
{
    public required string Name { get; init; }

    public string Value { get; init; } = "";

    public double Radius { get; init; }

    public bool IncludeSelf { get; init; }
}

/// <summary>A tag the interpreter does not know (Say included): logged once, never works.</summary>
public sealed class AiUnknown(string tag) : AiNode
{
    public string Tag { get; } = tag;
}

/// <summary>
/// The compiled &lt;AI&gt; of a mob (stageD mobspells SPEC §1, §2 R7, §4.1; ARCHITECTURE.md §6.3 level 1): its variables, the
/// Behaviour priority list, the IdleBehaviour (kept for the slots; not run before 5f.4), the global events (onSpawn, onDeath,
/// onReset) and the slot table the client computes the same way (b3x.o 0x1807f40a0): Behaviour then IdleBehaviour depth first,
/// containers into all children, Events skipped, a spell without isNewSpellSlot takes the first entry of its name (ignoring case),
/// one with it always appends. Built once per resolved source (<see cref="AiScripts"/>). Immutable after compilation, except the
/// per-node state an AI keeps outside it.
/// </summary>
public sealed class AiScript
{
    internal static readonly IReadOnlyDictionary<string, AiNode[]> NoEvents = new Dictionary<string, AiNode[]>();

    /// <summary>The If* leaves the interpreter evaluates (lower case); every other one reads false and is counted in <see cref="Unknown"/>.</summary>
    public static readonly IReadOnlySet<string> KnownLeaves = new HashSet<string>(StringComparer.Ordinal)
    {
        "iffightduration", "ifchance", "ifownhealthpercentage", "iftargethealthpercentage", "iftargetrange", "ifisanyenemyinrange",
        "ifistargettopofthreatlist", "iftotalnumberoftargetsonthreatlist", "ifmobdistancetohome", "ifspellactive", "ifvariable",
        "ifsummonedmobcount", "ifistargetcasting", "ifistargetchanneling", "ifownenergypercentage", "iftargetenergypercentage",
        "ifspellcategoryactive", "ifisanyfactionmemberinrange", "iftargettype",
        // StageN spells2: damage taken, and the threat and player counts of the areas around the mob (the fire dragon).
        "iftargettookdamage", "ifhighestarea", "ifplayersinarea", "iftargetisinhighestarea",
    };

    /// <summary>The mob whose &lt;AI&gt; this is after the references (the cache key).</summary>
    public required string Source { get; init; }

    public IReadOnlyList<AiVar> Vars { get; init; } = [];

    public IReadOnlyList<AiNode> Behaviour { get; init; } = [];

    public IReadOnlyList<AiNode> Idle { get; init; } = [];

    /// <summary>IdleBehaviour idleTimeUntilFirstAction, idleTimeAfterActionStartMin/Max (s): when an idle mob runs its <see cref="Idle"/> nodes.</summary>
    public (double First, double Min, double Max) IdleTimes { get; init; }

    public IReadOnlyDictionary<string, AiNode[]> Events { get; init; } = NoEvents;

    /// <summary>&lt;TargetAreaDefinition spell&gt;: the spell whose spelleffectarea arcs name the areas of the area conditions (stageN spells2); null = none.</summary>
    public string? AreaSpell { get; init; }

    /// <summary>The slot table (R7): spell names by slot.</summary>
    public IReadOnlyList<string> Slots { get; init; } = [];

    /// <summary>Tags and conditions the interpreter does not run, with their counts in this AI (--check-content).</summary>
    public IReadOnlyDictionary<string, int> Unknown { get; init; } = new Dictionary<string, int>();

    /// <summary>Whether it has anything to run in a fight.</summary>
    public bool Empty => Behaviour.Count == 0 && Events.Count == 0;

    /// <summary>
    /// Compiles <paramref name="ai"/> (an &lt;AI&gt; element, perhaps <c>reference="OTHER"</c>): references are followed through
    /// <paramref name="lookup"/> (a mob name → its &lt;AI&gt;) with a cycle guard; null when a reference does not resolve or loops.
    /// </summary>
    public static AiScript? Compile(XElement ai, string owner, Func<string, XElement?> lookup)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner };
        var source = owner;
        while ((string?)ai.Attribute("reference") is { Length: > 0 } reference)
        {
            if (!seen.Add(reference) || lookup(reference) is not { } next) return null;
            (ai, source) = (next, reference);
        }
        var unknown = new Dictionary<string, int>(StringComparer.Ordinal);
        var compiler = new Compiler(unknown);
        var vars = ai.Element("Variables")?.Elements("Var").Select(v => new AiVar((string?)v.Attribute("name") ?? "", ((string?)v.Attribute("type") ?? "string").ToLowerInvariant(),
            (string?)v.Attribute("initialValue") ?? "", Flag(v, "resetOnMobReset"), Num(v, "timeToLiveSeconds"), Flag(v, "resetOnMobDeath"))).ToList() ?? [];
        var behaviour = compiler.Actions(ai.Element("Behaviour"));
        var idle = compiler.Actions(ai.Element("IdleBehaviour"));
        var events = compiler.EventsOf(ai.Element("Events"));
        foreach (var e in ai.Elements())
            if (e.Name.LocalName is not ("Variables" or "Behaviour" or "IdleBehaviour" or "Events" or "TargetAreaDefinition"))
                unknown[e.Name.LocalName] = unknown.GetValueOrDefault(e.Name.LocalName) + 1;
        var slots = new List<string>();
        foreach (var n in behaviour) Number(n, slots);
        foreach (var n in idle) Number(n, slots);
        var idleTimes = ai.Element("IdleBehaviour") is { } ib
            ? (Num(ib, "idleTimeUntilFirstAction", 5), Num(ib, "idleTimeAfterActionStartMin", 1), Num(ib, "idleTimeAfterActionStartMax", 2)) : (0, 0, 0);
        return new AiScript
        {
            Source = source, Vars = vars, Behaviour = behaviour, Idle = idle, IdleTimes = idleTimes, Events = events, Slots = slots, Unknown = unknown,
            AreaSpell = (string?)ai.Element("TargetAreaDefinition")?.Attribute("spell"),
        };
    }

    /// <summary>The slot numbering of R7 (b3x.o 0x1807f40a0), into <paramref name="slots"/>.</summary>
    private static void Number(AiNode node, List<string> slots)
    {
        switch (node)
        {
            case AiSpell s:
                var at = s.NewSlot ? -1 : slots.FindIndex(n => string.Equals(n, s.Spell, StringComparison.OrdinalIgnoreCase));
                if (at < 0)
                {
                    at = slots.Count;
                    slots.Add(s.Spell);
                }
                s.Slot = (byte)Math.Min(at, byte.MaxValue);
                s.Numbered = true;
                break;
            case AiIf { Action: { } a }: Number(a, slots); break;
            case AiSelectFirst f: foreach (var c in f.Children) Number(c, slots); break;
            case AiSelectRandom r: foreach (var c in r.Children) Number(c, slots); break;
            case AiCompound c: foreach (var x in c.Children) Number(x, slots); break;
        }
    }

    /// <summary>A condition child: an If* tag, not, or, and (any case).</summary>
    public static bool IsCondition(XElement e) =>
        e.Name.LocalName.StartsWith("If", StringComparison.OrdinalIgnoreCase) && e.Name.LocalName.Length > 2
        || e.Name.LocalName.ToLowerInvariant() is "not" or "or" or "and";

    internal static double Num(XElement e, string name, double fallback = 0) =>
        (string?)e.Attribute(name) is { Length: > 0 } v && double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    internal static bool Flag(XElement e, string name) => string.Equals(((string?)e.Attribute(name))?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    private sealed class Compiler(Dictionary<string, int> unknown)
    {
        private void Count(string tag) => unknown[tag] = unknown.GetValueOrDefault(tag) + 1;

        public AiNode[] Actions(XElement? parent) =>
            parent is null ? [] : [.. parent.Elements().Where(e => !IsCondition(e) && e.Name.LocalName != "Event").Select(Node)];

        public Dictionary<string, AiNode[]> EventsOf(XElement? parent)
        {
            var events = new Dictionary<string, AiNode[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in parent?.Elements("Event") ?? [])
                if ((string?)e.Attribute("type") is { Length: > 0 } type)
                    events[type] = [.. events.GetValueOrDefault(type) ?? [], .. Actions(e)];
            return events;
        }

        /// <summary>The conditions of <paramref name="e"/>: its condition children ANDed; null without any.</summary>
        private AiCond? When(XElement e)
        {
            var parts = e.Elements().Where(IsCondition).Select(Cond).ToArray();
            return parts.Length == 0 ? null : parts.Length == 1 ? parts[0] : new AiAll(parts);
        }

        private AiCond Cond(XElement e)
        {
            var parts = () => e.Elements().Where(IsCondition).Select(Cond).ToArray();
            switch (e.Name.LocalName.ToLowerInvariant())
            {
                case "not": return new AiNot(parts());
                case "or": return new AiAny(parts());
                case "and": return new AiAll(parts());
            }
            var inner = e.Elements().Where(IsCondition).Select(Cond).ToArray();
            var leaf = new AiLeaf(e) { Inner = inner.Length == 0 ? null : inner.Length == 1 ? inner[0] : new AiAll(inner) };
            if (!KnownLeaves.Contains(leaf.Kind) && !Systems.SpellConditions.Known.Contains(leaf.Tag)) Count(leaf.Tag);
            return leaf;
        }

        private AiNode Node(XElement e)
        {
            var when = When(e);
            var weight = Num(e, "weight", 1);
            var events = EventsOf(e);
            switch (e.Name.LocalName)
            {
                case "Spell":
                    foreach (var c in e.Elements().Where(c => !IsCondition(c) && c.Name.LocalName is not ("Event" or "RandomGroundTarget"))) Count("Spell/" + c.Name.LocalName);
                    var rgt = e.Element("RandomGroundTarget");
                    return new AiSpell
                    {
                        When = when, Weight = weight, Events = events, Spell = (string?)e.Attribute("name") ?? "",
                        Target = ((string?)e.Attribute("target") ?? "current").ToLowerInvariant(), Selection = (string?)e.Attribute("targetSelection"),
                        Cdtnc = Num(e, "cooldowntillnextcast"), Group = (string?)e.Attribute("cooldowngroup"), GroundOffset = Num(e, "groundtargetoffset"),
                        GroundAngle = Num(e, "groundtargetangle"),
                        RelativeToCaster = string.Equals((string?)e.Attribute("groundtargetrelativeto"), "caster", StringComparison.OrdinalIgnoreCase),
                        NewSlot = Flag(e, "isNewSpellSlot"), SayText = (string?)e.Attribute("saytext"),
                        RandomGround = rgt is null ? null
                            : (Num(rgt, "radius"), Num(rgt, "innerradius"), !string.Equals((string?)rgt.Attribute("origin"), "caster", StringComparison.OrdinalIgnoreCase)),
                    };
                case "If":
                    var actions = Actions(e);
                    if (actions.Length != 1) Count("If/" + actions.Length + " actions");
                    return new AiIf { When = when, Weight = weight, Events = events, Action = actions.FirstOrDefault() };
                case "SelectFirst":
                    return new AiSelectFirst { When = when, Weight = weight, Events = events, Children = Actions(e) };
                case "SelectRandom":
                    return new AiSelectRandom
                    {
                        When = when, Weight = weight, Events = events, Children = Actions(e),
                        FailIfNotWorking = !string.Equals((string?)e.Attribute("behaviour"), "OnlySelectWorkingActions", StringComparison.OrdinalIgnoreCase),
                    };
                case "CompoundAction":
                    return new AiCompound { When = when, Weight = weight, Events = events, Children = Actions(e) };
                case "Variable":
                    return new AiVariable
                    {
                        When = when, Weight = weight, Name = (string?)e.Attribute("name") ?? "", Op = ((string?)e.Attribute("op") ?? "set").ToLowerInvariant(),
                        Value = (string?)e.Attribute("value") ?? "",
                    };
                case "BroadcastVariable":
                    return new AiBroadcast
                    {
                        When = when, Weight = weight, Name = (string?)e.Attribute("name") ?? "", Value = (string?)e.Attribute("value") ?? "",
                        Radius = Num(e, "radius"), IncludeSelf = Flag(e, "includeSelf"),
                    };
                case "Movement":
                    return new AiMovement
                    {
                        When = when, Weight = weight, Events = events, Target = ((string?)e.Attribute("target") ?? "current").ToLowerInvariant(),
                        Angle = Num(e, "angle"), Distance = Num(e, "distance"), Cooldown = Num(e, "cooldowntillnextmovement"),
                        AutoAttacks = !string.Equals(((string?)e.Attribute("autoattacksallowed"))?.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                        LineOfSight = Flag(e, "requireslineofsight"),
                    };
                default:
                    Count(e.Name.LocalName);
                    return new AiUnknown(e.Name.LocalName) { When = when, Weight = weight };
            }
        }
    }
}

/// <summary>
/// The compiled &lt;AI&gt; of each mob of a <see cref="MobStore"/>, by the resolved source (a referenced AI is compiled once for
/// all the mobs that point at it). Compiled on first use; thread-safe.
/// </summary>
public sealed class AiScripts(MobStore mobs)
{
    private readonly Dictionary<string, AiScript?> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AiScript?> _byMob = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The AI of <paramref name="mob"/>; null without one, or when its references do not resolve.</summary>
    public AiScript? For(MobTemplate mob)
    {
        if (mob.AI is not { } ai) return null;
        lock (_byMob)
        {
            if (_byMob.TryGetValue(mob.Name, out var known)) return known;
            var source = Source(ai, mob.Name);
            AiScript? script = null;
            if (source is not null && !_bySource.TryGetValue(source, out script))
                _bySource[source] = script = AiScript.Compile(ai, mob.Name, name => mobs.Find(name)?.AI);
            return _byMob[mob.Name] = script;
        }
    }

    /// <summary>The mob whose &lt;AI&gt; <paramref name="ai"/> ends at after its references; null on a loop or a missing one.</summary>
    private string? Source(XElement ai, string owner)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner };
        var source = owner;
        while ((string?)ai.Attribute("reference") is { Length: > 0 } reference)
        {
            if (!seen.Add(reference) || mobs.Find(reference)?.AI is not { } next) return null;
            (ai, source) = (next, reference);
        }
        return source;
    }
}
