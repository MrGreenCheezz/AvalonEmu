using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using AlbionLocal.Plugin.Api;

namespace AlbionLocal.Server.Rules;

/// <summary>What one firing of a rule sees: the units of the event, its names, the changeable number, the refusal and default-skip verbs, the loot, the command call.</summary>
internal sealed class RuleEvent
{
    public required string Trigger { get; init; }
    public string? Id { get; set; }
    public IUnit? Self { get; set; }
    public IUnit? Killer { get; set; }
    public IUnit? Victim { get; set; }
    public IUnit? Caster { get; set; }
    public IUnit? Target { get; set; }
    public IMob? Mob { get; set; }
    public IReadOnlyList<IPlayer>? Credited { get; set; }
    public string? Spell { get; set; }
    public string? Item { get; set; }
    public string? Buff { get; set; }
    public string? Mount { get; set; }
    public string? Cluster { get; set; }
    public string? Text { get; set; }
    public Mutable<double>? Amount { get; set; }
    public Action<string?>? Cancel { get; set; }
    public Action? Prevent { get; set; }
    public List<LootItem>? Loot { get; set; }
    public ICommandCall? Command { get; set; }
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Simulation: nothing is done, every step is described in <see cref="Trace"/>.</summary>
    public bool Dry { get; init; }

    public int Depth { get; init; }

    public RuleEvent Chain(string name, bool immediate) => new()
    {
        Trigger = "chain", Id = name, Self = Self, Killer = Killer, Victim = Victim, Caster = Caster, Target = Target, Mob = Mob, Credited = Credited,
        Spell = Spell, Item = Item, Buff = Buff, Mount = Mount, Cluster = Cluster, Text = Text, Command = immediate ? Command : null,
        Amount = immediate ? Amount : null, Cancel = immediate ? Cancel : null, Prevent = immediate ? Prevent : null, Loot = immediate ? Loot : null,
        Dry = Dry, Depth = Depth + 1,
    };
}

/// <summary>
/// The built-in "rules" plugin (server/plugins/RULES.md): an <see cref="IPlugin"/> of the server, loaded through the same
/// <see cref="Plugins.PluginHost"/> as the C# plugins, that registers exactly the events and hooks its rules name and runs each rule's
/// conditions and actions with the public API (<see cref="IServer"/>, <see cref="IPlayer"/>, <see cref="IGameplay"/>, storage, timers, commands).
/// A rule that throws <see cref="MaxErrors"/> times is switched off; the rest go on.
/// </summary>
internal sealed class RuleEngine(RulesHost host, IReadOnlyList<Rule> rules) : IPlugin
{
    private IPluginContext? _ctx;

    public string Name => RulesHost.PluginName;
    public string Version => "1.0.0";
    public IReadOnlyList<Rule> Rules => rules;

    private IServer Server => host.Server;
    private IGameplay Game => host.Game;
    private IPluginStorage? Storage => _ctx?.Storage;
    private int MaxErrors => host.MaxErrors;

    public void Load(IPluginContext context)
    {
        _ctx = context;
        foreach (var rule in rules.Where(r => r.Enabled))
        {
            try { Register(rule); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                rule.Disabled = true;
                rule.DisabledWhy = e.Message;
                host.Report(new RuleProblem("blocker", "/trigger", e.Message, rule.Pack, rule.Id));
            }
        }
    }

    public void Unload() { }

    // ---- registration

    private static string Base(string item) => item.Contains('@') ? item[..item.IndexOf('@')] : item;

    private static void UnitFlags(RuleEvent ev, IUnit? a, IUnit? b)
    {
        if (b is not null) ev.Flags.Add(b.IsPlayer ? "toPlayer" : "toMob");
        if (a is not null && !a.IsPlayer) ev.Flags.Add("mob");
    }

    private static IUnit? PlayerFirst(IUnit? a, IUnit? b) => a is IPlayer ? a : b is IPlayer ? b : a;

    private void Reg<T>(Rule rule, Func<Action<T>, IHookRegistration> all, Func<string, Action<T>, IHookRegistration> one, Action<T> handler)
    {
        if (rule.ExactFilter is { } id) one(id, handler);
        else all(handler);
    }

    private void Register(Rule rule)
    {
        var ctx = _ctx!;
        var E = ctx.Events;
        var H = ctx.Hooks;
        var t = rule.Trigger;
        void Fire(RuleEvent ev) => Run(rule, ev);
        switch (t)
        {
            case "player.login":
                E.PlayerLogin += a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player };
                    if (a.FirstLogin) ev.Flags.Add("firstLogin");
                    Fire(ev);
                };
                break;
            case "player.logout": E.PlayerLogout += a => Fire(new RuleEvent { Trigger = t, Self = a.Player }); break;
            case "zone.enter" or "zone.leave":
                Action<ZoneArgs> zone = a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Id = a.Cluster, Cluster = a.Cluster };
                    if (a.ClusterChange) ev.Flags.Add("clusterChange");
                    Fire(ev);
                };
                if (t == "zone.enter") E.ZoneEnter += zone;
                else E.ZoneLeave += zone;
                break;
            case "player.knockdown" or "player.died":
                Action<PlayerDownArgs> down = a => Fire(new RuleEvent { Trigger = t, Self = a.Player, Victim = a.Player, Killer = a.Killer, Text = a.KillerName });
                if (t == "player.died") E.PlayerDied += down;
                else E.PlayerKnockedDown += down;
                break;
            case "player.revive": H.Units.OnRevive(a => { if (a.Unit is IPlayer) Fire(new RuleEvent { Trigger = t, Self = a.Unit }); }); break;
            case "combat.enter": H.Units.OnEnterCombat(a => { if (a.Unit is IPlayer) Fire(new RuleEvent { Trigger = t, Self = a.Unit }); }); break;
            case "combat.leave": H.Units.OnLeaveCombat(a => { if (a.Unit is IPlayer) Fire(new RuleEvent { Trigger = t, Self = a.Unit }); }); break;
            case "health.below":
            {
                var share = rule.TriggerNum("percent", 30) / 100.0;
                Action<UnitValueArgs> h = a =>
                {
                    if (a.Max <= 0 || a.Before / a.Max < share || a.After / a.Max >= share) return;
                    if (rule.Filter is null && !a.Unit.IsPlayer) return;
                    var ev = new RuleEvent { Trigger = t, Self = a.Unit, Caster = a.Source, Id = a.Unit.IsPlayer ? null : a.Unit.Name, Spell = a.Spell, Amount = new Mutable<double>(a.Share * 100) };
                    if (a.Unit is IMob m)
                    {
                        ev.Mob = m;
                        ev.Flags.Add("mob");
                    }
                    Fire(ev);
                };
                Reg(rule, H.Units.OnHealthChange, H.Units.OnHealthChange, h);
                break;
            }
            case "chat": E.ChatMessage += a => Fire(new RuleEvent { Trigger = t, Self = a.Player, Id = a.Text, Text = a.Text }); break;
            case "command":
            {
                var command = rule.TriggerStr("command", "").TrimStart('.').ToLowerInvariant();
                var level = Enum.TryParse<PlayerLevel>(rule.TriggerStr("level", "Player"), out var l) ? l : PlayerLevel.Player;
                var help = rule.TriggerStr("help", rule.Name);
                ctx.Commands.Register(command, level, help + " (rule)", call =>
                {
                    if (call.Player is null)
                    {
                        call.Error("a player must run it (in game)");
                        return;
                    }
                    Fire(new RuleEvent { Trigger = t, Self = call.Player, Command = call, Text = call.Args, Id = command });
                });
                break;
            }
            case "party.joined" or "party.left":
                Action<PartyArgs> party = a => { if (a.Player is { } p) Fire(new RuleEvent { Trigger = t, Self = p, Text = a.Reason }); };
                if (t == "party.joined") E.PartyJoined += party;
                else E.PartyLeft += party;
                break;
            case "goal.completed": E.GoalCompleted += a => Fire(new RuleEvent { Trigger = t, Self = a.Player, Id = a.Kind, Text = a.Description }); break;
            case "mob.died":
                E.MobDied += a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Killer, Killer = a.Killer, Victim = a.Mob, Mob = a.Mob, Target = a.Mob, Credited = a.Credited, Id = a.Mob.Name };
                    MobFlags(ev, a.Mob);
                    Fire(ev);
                };
                break;
            case "mob.spawned":
                E.MobSpawned += a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Mob, Mob = a.Mob, Id = a.Mob.Name };
                    MobFlags(ev, a.Mob);
                    Fire(ev);
                };
                break;
            case "loot.rolled":
                E.LootRolled += a => Fire(new RuleEvent { Trigger = t, Self = a.Killer, Killer = a.Killer, Victim = a.Mob, Mob = a.Mob, Target = a.Mob, Id = a.Mob.Name, Loot = a.Items });
                break;
            case "damage":
                E.Damage += a =>
                {
                    var amount = new Mutable<double>(a.Amount);
                    var ev = new RuleEvent { Trigger = t, Self = PlayerFirst(a.Attacker, a.Target), Caster = a.Attacker, Target = a.Target, Amount = amount, Mob = a.Target as IMob ?? a.Attacker as IMob };
                    if (a.OverTime) ev.Flags.Add("overTime");
                    if (a.Kind == DamageKind.PlayerToPlayer) ev.Flags.Add("pvp");
                    UnitFlags(ev, null, a.Target);
                    Fire(ev);
                    if (amount.Value != amount.Original) a.Amount = amount.Value;
                };
                break;
            case "item.used" or "item.crafted":
                Action<ItemArgs> item = a => Fire(new RuleEvent { Trigger = t, Self = a.Player, Id = Base(a.Item), Item = a.Item, Text = a.Source });
                if (t == "item.used") E.ItemUsed += item;
                else E.ItemCrafted += item;
                break;
            case "item.equip" or "item.unequip":
            {
                Action<EquipArgs> h = a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Id = Base(a.Item), Item = a.Item };
                    if (a.AtLogin) ev.Flags.Add("atLogin");
                    if (a.IsWeapon) ev.Flags.Add("weapon");
                    Fire(ev);
                };
                if (t == "item.equip") Reg(rule, H.Items.OnEquip, H.Items.OnEquip, h);
                else Reg(rule, H.Items.OnUnequip, H.Items.OnUnequip, h);
                break;
            }
            case "item.break": Reg<ItemBreakArgs>(rule, H.Items.OnBreak, H.Items.OnBreak, a => Fire(new RuleEvent { Trigger = t, Self = a.Player, Id = Base(a.Item), Item = a.Item, Text = a.Cause })); break;
            case "harvest":
                E.Harvested += a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Id = Base(a.Item), Item = a.Item, Amount = new Mutable<double>(a.Count) };
                    if (a.Kind == "fish") ev.Flags.Add("fish");
                    Fire(ev);
                };
                break;
            case "spell.check":
                Reg<SpellCheckCastArgs>(rule, H.Spells.OnCheckCast, H.Spells.OnCheckCast, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Caster, Caster = a.Caster, Target = a.Target, Spell = a.Spell, Id = a.Spell, Item = a.Item, Cancel = a.Cancel };
                    if (a.MountSpell) ev.Flags.Add("mount");
                    Fire(ev);
                });
                break;
            case "spell.cast":
                Reg<SpellCastArgs>(rule, H.Spells.OnCast, H.Spells.OnCast, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Caster, Caster = a.Caster, Target = a.Target, Spell = a.Spell, Id = a.Spell, Item = a.Item, Mob = a.Caster as IMob };
                    if (a.MountSpell) ev.Flags.Add("mount");
                    UnitFlags(ev, a.Caster, null);
                    Fire(ev);
                });
                break;
            case "spell.hit":
                Reg<SpellHitArgs>(rule, H.Spells.OnBeforeHit, H.Spells.OnBeforeHit, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Caster, Caster = a.Caster, Target = a.Target, Spell = a.Spell, Id = a.Spell, Item = a.Item, Prevent = a.PreventDefault };
                    if (a.Projectile) ev.Flags.Add("projectile");
                    Fire(ev);
                });
                break;
            case "spell.damage":
                Reg<SpellDamageArgs>(rule, H.Spells.OnDamageEarly, H.Spells.OnDamageEarly, a =>
                {
                    if (a.IsEnergy) return;
                    var ev = new RuleEvent { Trigger = t, Self = PlayerFirst(a.Caster, a.Target), Caster = a.Caster, Target = a.Target, Spell = a.Spell, Id = a.Spell, Amount = a.Amount, Prevent = a.PreventDefault,
                        Mob = a.Target as IMob ?? a.Caster as IMob };
                    if (a.IsHeal) ev.Flags.Add("heal");
                    if (a.OverTime) ev.Flags.Add("overTime");
                    if (a.Area) ev.Flags.Add("area");
                    if (a.Magic) ev.Flags.Add("magic");
                    UnitFlags(ev, a.Caster, a.Target);
                    Fire(ev);
                });
                break;
            case "spell.cancel":
                Reg<SpellCancelArgs>(rule, H.Spells.OnCancel, H.Spells.OnCancel, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Caster, Caster = a.Caster, Target = a.Target, Spell = a.Spell, Id = a.Spell };
                    if (a.Interrupted) ev.Flags.Add("interrupted");
                    Fire(ev);
                });
                break;
            case "slot.changed":
                Reg<SlotSpellChangedArgs>(rule, H.Spells.OnSlotSpellChanged, H.Spells.OnSlotSpellChanged, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Spell = a.To, Id = a.To, Item = a.Item, Text = Systems.SlotSpells.NameOf(a.Slot) };
                    ev.Flags.Add(a.Source switch { SlotChangeSource.Player => "byPlayer", SlotChangeSource.Revert => "revert", _ => "byPlugin" });
                    if (a.Temporary) ev.Flags.Add("temporary");
                    Fire(ev);
                });
                break;
            case "extrabar.changed":
                Reg<ExtraBarChangedArgs>(rule, H.Spells.OnExtraBarChanged, H.Spells.OnExtraBarChanged, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Spell = a.To, Id = a.To ?? "", Item = a.Item, Text = (a.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    ev.Flags.Add(a.Source == ExtraBarChangeSource.Plugin ? "byPlugin" : "byGear");
                    if (a.To is null) ev.Flags.Add("emptied");
                    Fire(ev);
                });
                break;
            case "buff.check":
                Reg<BuffCheckArgs>(rule, H.Buffs.OnCheckApply, H.Buffs.OnCheckApply, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Unit, Caster = a.Caster, Target = a.Unit, Buff = a.Buff, Id = a.Buff, Cancel = a.Cancel, Mob = a.Unit as IMob };
                    UnitFlags(ev, a.Unit, null);
                    Fire(ev);
                });
                break;
            case "buff.applied" or "buff.removed" or "buff.tick":
            {
                Action<BuffArgs> h = a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Unit, Caster = a.Buff.Caster, Target = a.Unit, Buff = a.Buff.Spell, Id = a.Buff.Spell, Mob = a.Unit as IMob };
                    if (a.IsNew) ev.Flags.Add("new");
                    if (a is BuffRemoveArgs r) ev.Flags.Add(r.How.ToString().ToLowerInvariant());
                    UnitFlags(ev, a.Unit, null);
                    Fire(ev);
                };
                if (t == "buff.applied") Reg(rule, H.Buffs.OnApply, H.Buffs.OnApply, h);
                else if (t == "buff.tick") Reg(rule, H.Buffs.OnTick, H.Buffs.OnTick, h);
                else Reg<BuffRemoveArgs>(rule, H.Buffs.OnRemove, H.Buffs.OnRemove, a => h(a));
                break;
            }
            case "mount.check":
                Reg<MountCheckArgs>(rule, H.Mounts.OnCheckMount, H.Mounts.OnCheckMount, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Mount = a.Mount, Id = Base(a.Mount), Item = a.Mount, Cancel = a.Cancel };
                    if (a.Remount) ev.Flags.Add("remount");
                    Fire(ev);
                });
                break;
            case "mount.mounted": Reg<MountedArgs>(rule, H.Mounts.OnMounted, H.Mounts.OnMounted, a => Fire(new RuleEvent { Trigger = t, Self = a.Player, Mount = a.Mount, Id = Base(a.Mount), Item = a.Mount })); break;
            case "mount.dismount":
                Reg<MountArgs>(rule, H.Mounts.OnDismount, H.Mounts.OnDismount, a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Mount = a.Mount, Id = Base(a.Mount), Item = a.Mount, Text = a.Reason };
                    if (a.Reason is "died" or "knockdown") ev.Flags.Add(a.Reason);
                    Fire(ev);
                });
                break;
            case "mount.damage":
                Reg<MountDamageArgs>(rule, H.Mounts.OnMountDamage, H.Mounts.OnMountDamage, a =>
                    Fire(new RuleEvent { Trigger = t, Self = a.Player, Caster = a.Attacker, Mount = a.Mount, Id = Base(a.Mount), Item = a.Mount, Amount = a.Amount }));
                break;
            case "calc.cooldown" or "calc.casttime" or "calc.energy" or "calc.heal":
            {
                Action<SpellFormulaArgs> h = a => Fire(new RuleEvent { Trigger = t, Self = a.Caster, Caster = a.Caster, Target = a.Target, Spell = a.Spell, Id = a.Spell, Amount = a.Value });
                var f = H.Formulas;
                switch (t)
                {
                    case "calc.cooldown": Reg(rule, f.OnCalcCooldown, f.OnCalcCooldown, h); break;
                    case "calc.casttime": Reg(rule, f.OnCalcCastTime, f.OnCalcCastTime, h); break;
                    case "calc.energy": Reg(rule, f.OnCalcEnergyCost, f.OnCalcEnergyCost, h); break;
                    default: Reg(rule, f.OnCalcHeal, f.OnCalcHeal, h); break;
                }
                break;
            }
            case "calc.fame" or "calc.silver" or "calc.threat":
            {
                Action<UnitFormulaArgs> h = a =>
                {
                    var ev = new RuleEvent { Trigger = t, Self = a.Player, Mob = a.Unit as IMob, Victim = a.Unit, Target = a.Unit, Id = a.Unit.Name, Amount = a.Value };
                    if (a.Unit is IMob m) MobFlags(ev, m);
                    Fire(ev);
                };
                var f = H.Formulas;
                if (t == "calc.fame") Reg(rule, f.OnCalcFame, f.OnCalcFame, h);
                else if (t == "calc.silver") Reg(rule, f.OnCalcSilver, f.OnCalcSilver, h);
                else Reg(rule, f.OnCalcThreat, f.OnCalcThreat, h);
                break;
            }
            case "timer.every":
            {
                var seconds = Math.Max(1, rule.TriggerNum("seconds", 600));
                ctx.Timers.Every(TimeSpan.FromSeconds(seconds), () => Timed(rule));
                break;
            }
            case "timer.daily":
            {
                var at = RuleParser.Minutes(rule.TriggerStr("time", "20:00")) ?? 20 * 60;
                ctx.Timers.Every(TimeSpan.FromSeconds(20), () =>
                {
                    var now = Server.UtcNow.ToLocalTime();
                    var minutes = now.Hour * 60 + now.Minute;
                    if (minutes < at || minutes > at + 5) return; // a late tick (a pause) still fires within 5 minutes
                    var key = $"daily:{rule.Key}";
                    var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (Storage?.Get(key, "") == today) return;
                    Storage?.Set(key, today);
                    Timed(rule);
                });
                break;
            }
            case "chain": break; // run by run.chain
            default: throw new InvalidOperationException($"trigger {t} is not wired");
        }
    }

    private static void MobFlags(RuleEvent ev, IMob m)
    {
        if (m.IsWorldBoss) ev.Flags.Add("boss");
        if (m.IsEventMob) ev.Flags.Add("event");
    }

    private void Timed(Rule rule)
    {
        if (rule.TriggerBool("eachPlayer"))
            foreach (var p in Server.OnlinePlayers.ToList()) Run(rule, new RuleEvent { Trigger = rule.Trigger, Self = p });
        else Run(rule, new RuleEvent { Trigger = rule.Trigger, Self = Server.OnlinePlayers.FirstOrDefault() });
    }

    // ---- running a rule

    private static string Who(IUnit? u) => u is IPlayer p ? p.CharacterId.ToString() : u?.ObjectId.ToString(CultureInfo.InvariantCulture) ?? "";

    private string CooldownKey(Rule rule, RuleEvent ev) => $"cd:{rule.Key}{(rule.CooldownGlobal ? "" : ":" + Who(ev.Self))}";
    private string FiresKey(Rule rule, RuleEvent ev) => $"fires:{rule.Key}{(rule.LimitGlobal ? "" : ":" + Who(ev.Self))}";

    /// <summary>Seconds of the rule's cooldown left for this event's player (or globally); 0 = ready.</summary>
    public double CooldownLeft(Rule rule, RuleEvent ev)
    {
        if (rule.CooldownSeconds <= 0) return 0;
        var key = CooldownKey(rule, ev);
        var until = host.Cooldowns.TryGetValue(key, out var u) ? u : Storage?.Get<DateTime?>(key, null);
        return until is { } x ? Math.Max(0, (x - Server.UtcNow).TotalSeconds) : 0;
    }

    public long FiresOf(Rule rule, RuleEvent ev) => rule.MaxFires <= 0 ? 0 : Storage?.Get(FiresKey(rule, ev), 0L) ?? 0;

    /// <summary>Fires <paramref name="rule"/> for <paramref name="ev"/>: id filter, rate, cooldown, limit, conditions, then the actions. Never throws.</summary>
    public void Run(Rule rule, RuleEvent ev)
    {
        if (rule.Disabled || !rule.Enabled) return;
        _pack = rule.Pack;
        try
        {
            if (!IdMatches(rule, ev)) return;
            var second = Environment.TickCount64 / 1000;
            if (rule.SecondStamp != second)
            {
                rule.SecondStamp = second;
                rule.SecondCount = 0;
            }
            if (++rule.SecondCount > rule.PerSecond)
            {
                if (rule.SecondCount == rule.PerSecond + 1) _ctx?.Log.Warn($"rule {rule.Key}: more than {rule.PerSecond} fires in a second (limits.perSecond): the rest of this second is skipped");
                return;
            }
            if (CooldownLeft(rule, ev) is var left and > 0)
            {
                ev.Command?.Error($"wait {Math.Ceiling(left):0} s");
                return;
            }
            if (rule.MaxFires > 0 && FiresOf(rule, ev) >= rule.MaxFires)
            {
                ev.Command?.Error("no more uses");
                return;
            }
            if (!Conditions(rule, ev, null))
            {
                if (ev.Command is { } call) call.Error(rule.FailMessage.Length > 0 ? Text(rule.FailMessage, ev, ev.Self) : "not now");
                else if (rule.FailMessage.Length > 0 && rule.Trigger == "command") (ev.Self as IPlayer)?.SendMessage(Text(rule.FailMessage, ev, ev.Self), true);
                return;
            }
            rule.Fires++;
            if (rule.CooldownSeconds > 0)
            {
                var key = CooldownKey(rule, ev);
                var until = Server.UtcNow.AddSeconds(rule.CooldownSeconds);
                host.Cooldowns[key] = until;
                if (rule.CooldownSeconds >= 60) Storage?.Set<DateTime?>(key, until);
            }
            if (rule.MaxFires > 0) Storage?.Set(FiresKey(rule, ev), FiresOf(rule, ev) + 1);
            foreach (var a in rule.Actions) Act(rule, a, ev, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            rule.Errors++;
            _ctx?.Log.Error($"rule {rule.Key} ({rule.Trigger}) threw ({rule.Errors}/{MaxErrors}): {e.Message}");
            if (rule.Errors >= MaxErrors)
            {
                rule.Disabled = true;
                rule.DisabledWhy = $"{rule.Errors} errors, the last {e.GetType().Name}: {e.Message}";
                host.Report(new RuleProblem("blocker", "", $"switched off after {rule.Errors} errors (Plugins:MaxErrors): {e.Message}", rule.Pack, rule.Id));
            }
        }
    }

    private static bool IdMatches(Rule rule, RuleEvent ev)
    {
        if (rule.Filter is null) return true;
        if (rule.Trigger is "chat" or "goal.completed")
            return ev.Id?.Contains(rule.Filter, StringComparison.OrdinalIgnoreCase) == true;
        return rule.Matches(ev.Id) || (ev.Item is { } item && rule.Matches(item));
    }

    // ---- targets

    private IReadOnlyList<IUnit> Resolve(string selector, RuleEvent ev, float radius)
    {
        IEnumerable<IUnit?> units = selector switch
        {
            "self" => Of(ev.Self),
            "killer" => Of(ev.Killer),
            "victim" => Of(ev.Victim),
            "caster" => Of(ev.Caster),
            "target" => Of(ev.Target),
            "mob" => Of(ev.Mob),
            "party" => Party(ev),
            "cluster" => Server.OnlinePlayers.Where(p => p.Cluster == (ev.Cluster ?? ev.Self?.Cluster)),
            "radius" => ev.Self is { Cluster.Length: > 0 } s ? Game.UnitsAround(s.Cluster, s.X, s.Y, radius).OfType<IPlayer>() : Array.Empty<IUnit>(),
            "all" => Server.OnlinePlayers,
            _ => Array.Empty<IUnit>(),
        };
        return [.. units.OfType<IUnit>().DistinctBy(u => (u.IsPlayer, u.ObjectId))];
    }

    private static IEnumerable<IUnit?> Of(IUnit? unit) => [unit];

    private IEnumerable<IUnit> Party(RuleEvent ev)
    {
        if (ev.Self is not IPlayer self) return [];
        var list = new List<IUnit> { self };
        list.AddRange(host.PartyOf(self).Where(p => p.Cluster == self.Cluster));
        if (ev.Credited is { } credited) list.AddRange(credited);
        return list;
    }

    private IUnit? One(string? who, RuleEvent ev) => Resolve(who ?? "self", ev, 20).FirstOrDefault();

    // ---- conditions

    private static bool Compare(double a, string op, double b) => op switch
    {
        "<" => a < b, "<=" => a <= b, "=" => Math.Abs(a - b) < 1e-9, "!=" => Math.Abs(a - b) >= 1e-9, ">" => a > b, _ => a >= b,
    };

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The rule's conditions (all / any); <paramref name="trace"/> gets one line per condition in a simulation.</summary>
    public bool Conditions(Rule rule, RuleEvent ev, JsonArray? trace)
    {
        if (rule.Conditions.Count == 0) return true;
        var any = false;
        var all = true;
        foreach (var c in rule.Conditions)
        {
            var ok = Check(c, ev, out var detail);
            if (c.Not) ok = !ok;
            trace?.Add(new JsonObject { ["index"] = c.Index, ["type"] = c.Type, ["pass"] = ok, ["detail"] = (c.Not ? "NOT " : "") + detail });
            any |= ok;
            all &= ok;
            if (trace is null && (rule.Any ? ok : !ok)) break;
        }
        return rule.Any ? any : all;
    }

    private bool Check(RuleStep c, RuleEvent ev, out string detail)
    {
        var unit = One(c.Target, ev);
        var player = unit as IPlayer;
        var name = unit?.Name ?? "(nobody)";
        switch (c.Type)
        {
            case "chance":
            {
                var p = c.Num("percent", 50);
                if (ev.Dry)
                {
                    detail = $"{F(p)} % chance (not rolled in a simulation)";
                    return p > 0;
                }
                detail = $"{F(p)} %";
                return Random.Shared.NextDouble() * 100 < p;
            }
            case "account.level":
            {
                var min = Enum.TryParse<PlayerLevel>(c.Str("min", "GameMaster"), out var m) ? m : PlayerLevel.GameMaster;
                detail = $"{name}: {player?.Level.ToString() ?? "not a player"} ≥ {min}";
                return player is not null && player.Level >= min;
            }
            case "has.item":
            {
                var item = c.Str("item");
                var count = (int)c.Num("count", 1);
                var have = player?.CountItem(item) ?? 0;
                detail = $"{name} has {have} × {item} (needs {count})";
                return have >= count;
            }
            case "equipped":
            {
                var pattern = c.Str("item");
                var worn = player?.Equipment.Select(e => e.Name).ToList() ?? [];
                var hit = worn.FirstOrDefault(w => RuleParser.Glob(pattern, w) || RuleParser.Glob(pattern, Base(w)));
                detail = hit is null ? $"{name} wears no {pattern}" : $"{name} wears {hit}";
                return hit is not null;
            }
            case "slot.is":
            {
                var slot = c.Str("slot", "Q");
                var pattern = c.Str("spell");
                var have = Systems.SlotSpells.Parse(slot) is { } s ? player?.GetSlotSpell(s) : null;
                var hit = have is not null && pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(x => RuleParser.Glob(x, have));
                detail = $"{name}: slot {slot} holds {have ?? "nothing"} (wants {pattern})";
                return hit;
            }
            case "extrabar.has":
            {
                var pattern = c.Str("spell");
                var only = (int)c.Num("slot");
                var have = player?.GetExtraSlots() ?? [];
                var hit = have.Select((s, i) => (s, i)).Any(x => x.s is not null && (only <= 0 || x.i == only - 1)
                    && pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(g => RuleParser.Glob(g, x.s)));
                detail = $"{name}: extra bars {string.Join(" ", have.Select(s => s ?? "-"))} (wants {pattern}{(only > 0 ? $" in slot {only}" : "")})";
                return hit;
            }
            case "has.buff":
            {
                var buff = c.Str("buff");
                var has = unit?.Buffs.Any(b => b.Spell.Equals(buff, StringComparison.OrdinalIgnoreCase)) == true;
                detail = $"{name} {(has ? "has" : "has no")} {buff}";
                return has;
            }
            case "health":
            {
                var share = unit is { MaxHealth: > 0 } u ? u.Health / u.MaxHealth * 100 : 0;
                detail = $"{name}: {F(share)} % {c.Str("op", "<")} {F(c.Num("percent", 50))} %";
                return unit is not null && Compare(share, c.Str("op", "<"), c.Num("percent", 50));
            }
            case "in.combat":
                detail = $"{name} {(unit?.InCombat == true ? "is" : "is not")} in combat";
                return unit?.InCombat == true;
            case "unit.kind":
            {
                var kind = c.Str("kind", "player");
                detail = $"{name} is {(unit is null ? "nobody" : unit.IsPlayer ? "a player" : "a mob")}";
                return unit is not null && unit.IsPlayer == (kind == "player");
            }
            case "silver":
                detail = $"{name}: {player?.Silver ?? 0} silver {c.Str("op", ">=")} {F(c.Num("value"))}";
                return player is not null && Compare(player.Silver, c.Str("op", ">="), c.Num("value"));
            case "party.size":
            {
                var size = player is null ? 0 : Math.Max(1, host.PartyOf(player).Count + 1);
                detail = $"{name}'s party: {size} {c.Str("op", ">=")} {F(c.Num("value", 2))}";
                return player is not null && Compare(size, c.Str("op", ">="), c.Num("value", 2));
            }
            case "cluster":
            {
                var cluster = unit?.Cluster ?? ev.Cluster ?? "";
                var list = c.List("clusters");
                detail = $"{name} is in {(cluster.Length == 0 ? "no cluster" : cluster)}";
                return list.Contains(cluster, StringComparer.OrdinalIgnoreCase);
            }
            case "zone.color":
            {
                var cluster = unit?.Cluster ?? ev.Cluster ?? "";
                var color = host.ZoneColor(cluster);
                detail = $"{(cluster.Length == 0 ? "no cluster" : cluster)} is {color ?? "unknown"}";
                return color is not null && c.List("colors").Contains(color, StringComparer.OrdinalIgnoreCase);
            }
            case "time":
            {
                var now = Server.UtcNow.ToLocalTime();
                var minutes = now.Hour * 60 + now.Minute;
                var from = RuleParser.Minutes(c.Str("from", "20:00")) ?? 0;
                var to = RuleParser.Minutes(c.Str("to", "06:00")) ?? 0;
                var inside = from <= to ? minutes >= from && minutes < to : minutes >= from || minutes < to;
                detail = $"{now:HH:mm} {(inside ? "in" : "not in")} {c.Str("from")}-{c.Str("to")}";
                return inside;
            }
            case "weekday":
            {
                var day = Server.UtcNow.ToLocalTime().DayOfWeek.ToString()[..3].ToLowerInvariant();
                detail = $"today is {day}";
                return c.List("days").Contains(day);
            }
            case "mob.tier":
            {
                var mob = ev.Mob ?? unit as IMob;
                detail = mob is null ? "no mob" : $"{mob.Name}: tier {mob.Tier} {c.Str("op", ">=")} {F(c.Num("value", 6))}";
                return mob is not null && Compare(mob.Tier, c.Str("op", ">="), c.Num("value", 6));
            }
            case "mob.category":
            {
                var mob = ev.Mob ?? unit as IMob;
                detail = mob is null ? "no mob" : $"{mob.Name}: category '{mob.Category}'";
                return mob is not null && c.List("categories").Contains(mob.Category, StringComparer.OrdinalIgnoreCase);
            }
            case "mob.boss":
            {
                var mob = ev.Mob ?? unit as IMob;
                detail = mob is null ? "no mob" : $"{mob.Name} {(mob.IsWorldBoss ? "is" : "is not")} a world boss";
                return mob?.IsWorldBoss == true;
            }
            case "var":
            {
                var value = Var(c.Str("scope", "player"), c.Str("name"), unit, ev);
                detail = $"{c.Str("scope", "player")} {c.Str("name")} = {F(value)} {c.Str("op", ">=")} {F(c.Num("value"))}{(Storage is null ? " (no storage in a simulation of an unloaded engine)" : "")}";
                return Compare(value, c.Str("op", ">="), c.Num("value"));
            }
            case "amount":
                detail = ev.Amount is null ? "no number" : $"{F(ev.Amount.Value)} {c.Str("op", ">=")} {F(c.Num("value"))}";
                return ev.Amount is not null && Compare(ev.Amount.Value, c.Str("op", ">="), c.Num("value"));
            case "flag":
            {
                var flag = c.Str("flag");
                detail = $"{flag}: {(ev.Flags.Contains(flag) ? "yes" : "no")}";
                return ev.Flags.Contains(flag);
            }
            case "text":
            {
                var text = ev.Text ?? ev.Command?.Args ?? "";
                var needle = c.Str("contains");
                detail = $"'{text}' {(text.Contains(needle, StringComparison.OrdinalIgnoreCase) ? "contains" : "lacks")} '{needle}'";
                return text.Contains(needle, StringComparison.OrdinalIgnoreCase);
            }
            default:
                detail = "unknown condition";
                return false;
        }
    }

    // ---- variables (the rules' storage: data/plugins/rules/storage.json, by pack)

    private string VarKey(string scope, string name, IUnit? unit, RuleEvent ev) =>
        scope == "global" ? $"var:{_pack}:g:{name}" : $"var:{_pack}:p:{Who(unit ?? ev.Self)}:{name}";

    /// <summary>The pack of the rule being run (set at each run: variables are per pack).</summary>
    private string _pack = "";

    private double Var(string scope, string name, IUnit? unit, RuleEvent ev) => Storage?.Get(VarKey(scope, name, unit, ev), 0.0) ?? 0;

    // ---- actions

    /// <summary>The text with its placeholders filled for <paramref name="to"/> (the unit it goes to).</summary>
    public string Text(string text, RuleEvent ev, IUnit? to)
    {
        if (!text.Contains('{')) return text;
        var sb = new StringBuilder(text);
        sb.Replace("{player}", ev.Self?.Name ?? "")
            .Replace("{target}", to?.Name ?? ev.Target?.Name ?? "")
            .Replace("{killer}", ev.Killer?.Name ?? "")
            .Replace("{mob}", ev.Mob?.Name ?? ev.Victim?.Name ?? "")
            .Replace("{spell}", ev.Spell ?? "")
            .Replace("{item}", ev.Item ?? "")
            .Replace("{buff}", ev.Buff ?? "")
            .Replace("{mount}", ev.Mount ?? "")
            .Replace("{cluster}", ev.Cluster ?? ev.Self?.Cluster ?? "")
            .Replace("{amount}", ev.Amount is { } a ? F(a.Value) : "")
            .Replace("{args}", ev.Command?.Args ?? ev.Text ?? "");
        var s = sb.ToString();
        foreach (var (prefix, scope) in new[] { ("{var:", "player"), ("{gvar:", "global") })
            for (var i = s.IndexOf(prefix, StringComparison.Ordinal); i >= 0; i = s.IndexOf(prefix, StringComparison.Ordinal))
            {
                var end = s.IndexOf('}', i);
                if (end < 0) break;
                var name = s[(i + prefix.Length)..end];
                s = s[..i] + F(Var(scope, name, to, ev)) + s[(end + 1)..];
            }
        return s;
    }

    private static string Names(IReadOnlyList<IUnit> units) => units.Count == 0 ? "nobody" : string.Join(", ", units.Take(8).Select(u => u.Name)) + (units.Count > 8 ? $" … ({units.Count})" : "");

    /// <summary>One action of <paramref name="rule"/>; in a simulation (<paramref name="trace"/>) only described.</summary>
    public void Act(Rule rule, RuleStep a, RuleEvent ev, JsonArray? trace)
    {
        _pack = rule.Pack;
        var dry = trace is not null;
        var selector = a.Target ?? rule.Target;
        var targeted = RuleSchema.Action(a.Type)?.Targeted == true;
        var units = targeted ? Resolve(selector, ev, a.Radius) : [];
        var players = units.OfType<IPlayer>().ToList();
        string detail;
        switch (a.Type)
        {
            case "message":
            {
                var channel = a.Str("channel", "chat");
                var text = a.Str("text");
                if (channel == "announce")
                {
                    detail = $"announce to everyone: {Text(text, ev, null)}";
                    if (!dry) Server.Broadcast(Text(text, ev, null));
                }
                else if (channel == "zone")
                {
                    var cluster = units.FirstOrDefault()?.Cluster ?? ev.Cluster ?? ev.Self?.Cluster ?? "";
                    detail = $"to cluster {cluster}: {Text(text, ev, null)}";
                    if (!dry && cluster.Length > 0) Server.SendToZone(cluster, Text(text, ev, null));
                }
                else
                {
                    detail = $"to {Names(players)}: {(players.Count > 0 ? Text(text, ev, players[0]) : text)}";
                    if (!dry) foreach (var p in players) p.SendMessage(Text(text, ev, p), channel == "error");
                }
                break;
            }
            case "give.item":
            {
                var item = a.Str("item");
                var count = (int)a.Num("count", 1);
                detail = $"give {count} × {item} to {Names(players)}";
                if (!dry) foreach (var p in players) p.GiveItem(item, count, (int)a.Num("quality", 1));
                break;
            }
            case "take.item":
            {
                var item = a.Str("item");
                var count = (int)a.Num("count", 1);
                detail = $"take {count} × {item} from {Names(players)}";
                if (!dry) foreach (var p in players) p.TakeItem(item, count);
                break;
            }
            case "silver":
            {
                var amount = (long)a.Num("amount");
                detail = $"{(amount >= 0 ? "give" : "take")} {Math.Abs(amount)} silver {(amount >= 0 ? "to" : "from")} {Names(players)}";
                if (!dry) foreach (var p in players) _ = amount >= 0 ? p.GiveSilver(amount) : p.TakeSilver(-amount);
                break;
            }
            case "fame":
            {
                var amount = (long)a.Num("amount");
                detail = $"give {amount} fame (tier {(int)a.Num("tier", 4)}) to {Names(players)}";
                if (!dry) foreach (var p in players) p.GiveFame(amount, (int)a.Num("tier", 4));
                break;
            }
            case "buff.apply":
            {
                var buff = a.Str("buff");
                detail = $"apply {buff}{(a.Num("seconds") > 0 ? $" for {F(a.Num("seconds"))} s" : "")} ×{(int)a.Num("stacks", 1)} on {Names(units)}";
                if (!dry) foreach (var u in units) Game.ApplyBuff(u, buff, ev.Caster ?? ev.Self, a.Num("seconds"), (int)a.Num("stacks", 1));
                break;
            }
            case "buff.remove":
                detail = $"remove {a.Str("buff")} from {Names(units)}";
                if (!dry) foreach (var u in units) Game.RemoveBuff(u, a.Str("buff"));
                break;
            case "damage" or "heal":
            {
                var percent = a.Num("percent");
                var flat = a.Num("amount", 100);
                detail = $"{a.Type} {(percent > 0 ? F(percent) + " % of max health" : F(flat))} on {Names(units)}";
                if (!dry)
                    foreach (var u in units)
                    {
                        var amount = percent > 0 ? u.MaxHealth * percent / 100 : flat;
                        var source = ev.Caster is { } c && c != u ? c : ev.Self is { } s && s != u ? s : u;
                        if (a.Type == "heal") Game.Heal(source, u, amount);
                        else if (source != u) Game.Damage(source, u, amount, a.Bool("magic", true));
                    }
                break;
            }
            case "teleport":
            {
                var cluster = a.Str("cluster");
                double? x = a.Params["x"] is null || a.Str("x").Length == 0 ? null : a.Num("x");
                double? y = a.Params["y"] is null || a.Str("y").Length == 0 ? null : a.Num("y");
                detail = $"teleport {Names(units)} to {(cluster.Length > 0 ? cluster : "the same cluster")}{(x is not null ? $" ({F(x.Value)}, {F(y ?? 0)})" : " (spawn point)")}";
                if (!dry)
                    foreach (var u in units)
                    {
                        if (cluster.Length == 0 && x is not null) Game.Teleport(u, (float)x, (float)(y ?? 0));
                        else if (u is IPlayer p) p.TeleportTo(cluster.Length > 0 ? cluster : p.Cluster, (float?)x, (float?)y);
                    }
                break;
            }
            case "move":
            {
                var kind = a.Str("kind", "knockback");
                var source = ev.Caster ?? ev.Self;
                var other = ev.Target ?? ev.Mob ?? ev.Caster;
                detail = $"{kind} {Names(units)} ({F(a.Num("distance", 5))} m)";
                if (!dry)
                    foreach (var u in units)
                    {
                        var speed = (float)a.Num("speed", 12);
                        switch (kind)
                        {
                            case "pull" when source is not null && source != u: Game.Pull(u, source.X, source.Y, speed); break;
                            case "dash" when other is not null && other != u:
                            {
                                var (dx, dy) = (other.X - u.X, other.Y - u.Y);
                                var len = MathF.Max(0.01f, MathF.Sqrt(dx * dx + dy * dy));
                                var step = MathF.Min(len - 1, (float)a.Num("distance", 5));
                                if (step > 0) Game.Dash(u, u.X + dx / len * step, u.Y + dy / len * step, speed);
                                break;
                            }
                            case "knockback" when source is not null && source != u: Game.Knockback(u, source.X, source.Y, (float)a.Num("distance", 5), speed); break;
                        }
                    }
                break;
            }
            case "summon":
            {
                var mob = a.Str("mob");
                var count = (int)a.Num("count", 1);
                var life = a.Num("lifetime", 60);
                detail = $"summon {count} × {mob} at {Names(units)}{(life > 0 ? $" for {F(life)} s" : "")}";
                if (!dry)
                    foreach (var u in units)
                        for (var i = 0; i < count; i++)
                        {
                            var angle = i * 2 * Math.PI / Math.Max(1, count);
                            Game.Summon(u, mob, u.X + 2 * (float)Math.Cos(angle), u.Y + 2 * (float)Math.Sin(angle), life > 0 ? TimeSpan.FromSeconds(life) : null);
                        }
                break;
            }
            case "boss.spawn":
            {
                var at = units.FirstOrDefault();
                detail = at is null ? "spawn the boss: nobody to spawn it at" : $"spawn world boss {a.Str("mob")} at {at.Name} ({at.Cluster})";
                if (!dry && at is { Cluster.Length: > 0 }) Server.SpawnBoss(at.Cluster, a.Str("mob"), at.X + 3, at.Y);
                break;
            }
            case "event.start":
            {
                var at = units.FirstOrDefault();
                detail = at is null ? "start the event: nobody to start it at" : $"start world event {a.Str("kind")} at {at.Name} ({at.Cluster})";
                if (!dry && at is { Cluster.Length: > 0 } && Server.StartEvent(a.Str("kind"), at.Cluster, at.X, at.Y, (int)a.Num("tier")) is { } why)
                    _ctx?.Log.Warn($"rule {rule.Key}: event {a.Str("kind")} not started: {why}");
                break;
            }
            case "var.set":
            {
                var scope = a.Str("scope", "player");
                var name = a.Str("name");
                var op = a.Str("op", "add");
                var value = a.Num("value");
                IEnumerable<IUnit?> who = scope == "global" ? Of(null) : units.Select(u => (IUnit?)u);
                detail = $"{scope} {name} {op} {F(value)}{(scope == "global" ? "" : $" for {Names(units)}")}";
                if (!dry && Storage is { } st)
                    foreach (var u in who)
                    {
                        var key = VarKey(scope, name, u, ev);
                        var now = st.Get(key, 0.0);
                        st.Set(key, op switch { "set" => value, "sub" => now - value, _ => now + value });
                    }
                break;
            }
            case "loot.add":
                detail = $"add {(int)a.Num("count", 1)} × {a.Str("item")} to the loot";
                if (!dry) ev.Loot?.Add(new LootItem(a.Str("item"), (int)a.Num("count", 1)));
                break;
            case "modify":
            {
                var op = a.Str("op", "mul");
                var value = a.Num("value", 1);
                if (ev.Amount is not { } m)
                {
                    detail = "no number to change";
                    break;
                }
                var next = Math.Max(0, op switch { "add" => m.Value + value, "set" => value, _ => m.Value * value });
                detail = $"{F(m.Value)} → {F(next)} ({op} {F(value)})";
                if (!dry) m.Value = next;
                break;
            }
            case "cancel":
                detail = $"refuse{(a.Str("reason").Length > 0 ? ": " + Text(a.Str("reason"), ev, ev.Self) : "")}";
                if (!dry) ev.Cancel?.Invoke(a.Str("reason").Length > 0 ? Text(a.Str("reason"), ev, ev.Self) : null);
                break;
            case "prevent":
                detail = "skip the game's own effect";
                if (!dry) ev.Prevent?.Invoke();
                break;
            case "cast":
            {
                var spell = a.Str("spell");
                var caster = ev.Self as IPlayer;
                detail = $"{caster?.Name ?? "each target player"} casts {spell} at {Names(units)}";
                if (!dry)
                    foreach (var u in units)
                        if ((caster ?? u as IPlayer) is { } c) Game.CastSpell(c, spell, u, u.X, u.Y);
                break;
            }
            case "slot.set":
            {
                var slot = Systems.SlotSpells.Parse(a.Str("slot", "Q")) ?? SpellSlot.Other;
                var spell = a.Str("spell");
                var mode = a.Str("mode", "fromItemList") == "any" ? SlotSpellMode.Any : SlotSpellMode.FromItemList;
                var seconds = a.Num("duration");
                var cooldown = a.Str("cooldown", "keep") switch { "carry" => SlotSpellCooldown.Carry, "reset" => SlotSpellCooldown.Reset, _ => SlotSpellCooldown.Keep };
                detail = $"slot {Systems.SlotSpells.NameOf(slot)} = {spell}{(mode == SlotSpellMode.Any ? " (any spell)" : "")}{(seconds > 0 ? $" for {F(seconds)} s" : "")} for {Names(players)}";
                if (!dry)
                    foreach (var p in players)
                        if (p.SetSlotSpell(slot, spell, mode, seconds > 0 ? TimeSpan.FromSeconds(seconds) : null, cooldown) is { } why)
                        {
                            detail += $"; {p.Name}: {why}";
                            _ctx?.Log.Info($"rule {rule.Key}: slot.set for {p.Name}: {why}");
                        }
                break;
            }
            case "extrabar.set":
            {
                var slot = (int)a.Num("slot");
                var spell = a.Str("spell");
                detail = $"extra slot {slot} = {(spell.Length == 0 ? "the gear's" : spell)} for {Names(players)}";
                if (!dry)
                    foreach (var p in players)
                        if (p.SetExtraSlot(slot - 1, spell.Length == 0 ? null : spell) is { } why)
                        {
                            detail += $"; {p.Name}: {why}";
                            _ctx?.Log.Info($"rule {rule.Key}: extrabar.set for {p.Name}: {why}");
                        }
                break;
            }
            case "cooldown":
            {
                var spell = a.Str("spell");
                detail = spell.Length == 0 ? $"reset the cooldowns of {Names(players)}" : $"cooldown of {spell} = {F(a.Num("seconds"))} s for {Names(players)}";
                if (!dry)
                    foreach (var p in players)
                        _ = spell.Length == 0 ? Game.ResetCooldowns(p) : Game.SetCooldown(p, spell, a.Num("seconds"));
                break;
            }
            case "run.chain":
            {
                var chain = a.Str("chain");
                var delay = a.Num("delay");
                var targets = host.ChainRules(rule.Pack, chain);
                detail = $"run chain '{chain}'{(delay > 0 ? $" after {F(delay)} s" : "")}: {(targets.Count == 0 ? "no rule of this pack has it" : string.Join(", ", targets.Select(r => r.Id)))}";
                if (dry || ev.Depth >= 8) break;
                if (delay <= 0) foreach (var r in targets) Run(r, ev.Chain(chain, immediate: true));
                else
                {
                    var later = ev.Chain(chain, immediate: false);
                    _ctx?.Timers.After(TimeSpan.FromSeconds(delay), () => { foreach (var r in host.ChainRules(rule.Pack, chain)) Run(r, later); });
                }
                break;
            }
            case "log":
                detail = $"log: {Text(a.Str("text"), ev, ev.Self)}";
                if (!dry) _ctx?.Log.Info($"rule {rule.Key}: {Text(a.Str("text"), ev, ev.Self)}");
                break;
            default:
                detail = "unknown action";
                break;
        }
        trace?.Add(new JsonObject { ["index"] = a.Index, ["type"] = a.Type, ["target"] = targeted ? selector : null, ["targets"] = new JsonArray([.. units.Select(u => (JsonNode)u.Name)]), ["detail"] = detail });
    }

    /// <summary>A dry run of <paramref name="rule"/> for <paramref name="ev"/>: whether it would fire, why not, and what each step would do.</summary>
    public JsonObject Simulate(Rule rule, RuleEvent ev)
    {
        _pack = rule.Pack;
        var notes = new JsonArray();
        var conditions = new JsonArray();
        var actions = new JsonArray();
        string? blocked = null;
        if (!rule.Enabled) blocked = "the rule is switched off (enabled: false)";
        if (CooldownLeft(rule, ev) is var left and > 0) blocked ??= $"on cooldown for {Math.Ceiling(left):0} s more";
        if (rule.MaxFires > 0 && FiresOf(rule, ev) >= rule.MaxFires) blocked ??= $"used up: {FiresOf(rule, ev)} of {rule.MaxFires} fires";
        if (!IdMatches(rule, ev)) notes.Add($"the trigger's id filter '{rule.Filter}' does not take '{ev.Id}' (simulated anyway)");
        var pass = Conditions(rule, ev, conditions);
        foreach (var a in rule.Actions) Act(rule, a, ev, actions);
        return new JsonObject
        {
            ["wouldFire"] = pass && blocked is null, ["conditionsPass"] = pass, ["blocked"] = blocked, ["conditions"] = conditions, ["actions"] = actions, ["notes"] = notes,
        };
    }
}
