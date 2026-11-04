using System.Diagnostics;
using AlbionLocal.Modules;

namespace AlbionLocal.Server.Modules;

/// <summary>What the id of a hook names: a registration's id is checked against the game's data, so a typo gets a hint in the log.</summary>
internal enum HookIdKind
{
    /// <summary>Not checked.</summary>
    Free,
    Spell,
    Item,
    Mob,
    Operation,
    Event,
}

/// <summary>What every hook point offers the host, whatever its args.</summary>
internal interface IHookPoint
{
    string Name { get; }
    Type ArgsType { get; }
    bool Traced { get; set; }
    void RemoveAll(ModuleContext ctx);
    bool Has(ModuleContext ctx);

    /// <summary>A *Script method's registration: <paramref name="handler"/> is an Action of the hook's args.</summary>
    IHookRegistration AddDelegate(ModuleContext ctx, string? id, Delegate handler, HookPriority priority);

    /// <summary>The handlers now: module, id, priority, calls and time so far.</summary>
    IEnumerable<HookEntryInfo> Entries();
}

/// <summary>One handler as <c>.modules hooks</c> and <c>.modules timings</c> show it.</summary>
internal sealed record HookEntryInfo(string Hook, string Module, string? Id, HookPriority Priority, long Calls, double Milliseconds);

/// <summary>
/// One hook (docs/modules/README.md "Hooks"): its handlers for everything and its handlers by id (case-insensitive), each list ordered by priority, then by
/// registration. <see cref="Wants"/> is the call sites' cheap test: a count check with nobody listening, then a dictionary lookup only when filtered handlers
/// exist. The lists are replaced, never changed in place, so raising allocates nothing and a handler may register or remove handlers. A mask (* and ?) sits
/// in the list for everything and is tested when it comes up. Every handler runs in <see cref="ModuleHost.Guard{T}"/>. World thread only.
/// </summary>
internal sealed class HookPoint<T>(ModuleHost host, string name, HookIdKind idKind) : IHookPoint
{
    private Entry[] _all = [];
    private readonly Dictionary<string, Entry[]> _byId = new(StringComparer.OrdinalIgnoreCase);
    private int _count;
    private long _sequence;

    public string Name { get; } = name;
    public Type ArgsType => typeof(T);
    public bool Traced { get; set; }

    /// <summary>Anyone listens at all.</summary>
    public bool Any => _count > 0;

    /// <summary>Anyone listens for <paramref name="id"/> (a global handler or mask, or one of that id).</summary>
    public bool Wants(string? id) => _count > 0 && (_all.Length > 0 || id is not null && _byId.Count > 0 && _byId.ContainsKey(id));

    public IHookRegistration AddDelegate(ModuleContext ctx, string? id, Delegate handler, HookPriority priority) => Add(ctx, id, (Action<T>)handler, priority);

    public IHookRegistration Add(ModuleContext ctx, string? id, Action<T> handler, HookPriority priority = HookPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(handler);
        id = id is { Length: > 0 } ? id.Trim() : null;
        var mask = id is not null && id.AsSpan().IndexOfAny('*', '?') >= 0 ? ModuleHost.Mask(id) : null;
        var e = new Entry(this, ctx, id, mask, handler, priority, ((long)priority << 32) + _sequence++);
        if (ctx.Dropped) return e;
        if (id is not null && mask is null) host.CheckHookId(ctx, Name, idKind, id);
        if (id is null || mask is not null) _all = Insert(_all, e);
        else _byId[id] = Insert(_byId.GetValueOrDefault(id) ?? [], e);
        _count++;
        return e;
    }

    private static Entry[] Insert(Entry[] list, Entry e)
    {
        var at = list.Length;
        while (at > 0 && list[at - 1].Order > e.Order) at--;
        var copy = new Entry[list.Length + 1];
        Array.Copy(list, copy, at);
        copy[at] = e;
        Array.Copy(list, at, copy, at + 1, list.Length - at);
        return copy;
    }

    private void Remove(Entry e)
    {
        if (e.Id is null || e.Mask is not null)
        {
            if (Array.IndexOf(_all, e) < 0) return;
            _all = [.. _all.Where(x => x != e)];
        }
        else
        {
            if (!_byId.TryGetValue(e.Id, out var list) || Array.IndexOf(list, e) < 0) return;
            if (list.Length == 1) _byId.Remove(e.Id);
            else _byId[e.Id] = [.. list.Where(x => x != e)];
        }
        _count--;
    }

    public void RemoveAll(ModuleContext ctx)
    {
        foreach (var e in _all.Concat(_byId.Values.SelectMany(l => l)).Where(e => e.Ctx == ctx).ToList()) Remove(e);
    }

    public bool Has(ModuleContext ctx) => _all.Any(e => e.Ctx == ctx) || _byId.Values.Any(l => l.Any(e => e.Ctx == ctx));

    public IEnumerable<HookEntryInfo> Entries() =>
        _all.Concat(_byId.Values.SelectMany(l => l)).OrderBy(e => e.Order)
            .Select(e => new HookEntryInfo(Name, e.Ctx.Name, e.Id, e.Priority, e.Calls, Stopwatch.GetElapsedTime(0, e.Ticks).TotalMilliseconds));

    /// <summary>Runs the handlers for everything and those of <paramref name="id"/>, merged by priority; a module dropped meanwhile is skipped.</summary>
    public void Raise(string? id, T args)
    {
        if (_count == 0) return;
        Walk(id, args, plain: true);
    }

    private void Walk(string? id, T args, bool plain)
    {
        var all = _all;
        var byId = id is not null && _byId.Count > 0 ? _byId.GetValueOrDefault(id) ?? [] : [];
        var cancel = args as ICancellableArgs;
        for (int i = 0, j = 0; i < all.Length || j < byId.Length;)
        {
            var global = j >= byId.Length || i < all.Length && all[i].Order <= byId[j].Order;
            var e = global ? all[i++] : byId[j++];
            if (e.Mask is not null ? id is null || !e.Mask.IsMatch(id) : global && !plain) continue;
            Call(e, id, args, cancel);
        }
    }

    /// <summary>Only the handlers of <paramref name="id"/> and the masks that match it (not those for everything).</summary>
    public void RaiseId(string id, T args)
    {
        if (_count > 0) Walk(id, args, plain: false);
    }

    /// <summary>As <see cref="Raise"/>, but only <paramref name="ctx"/>'s handlers (OnReload after its load).</summary>
    public void RaiseFor(ModuleContext ctx, string? id, T args)
    {
        foreach (var e in _all)
            if (e.Ctx == ctx && (e.Mask is null || id is not null && e.Mask.IsMatch(id))) Call(e, id, args, null);
        if (id is not null && _byId.TryGetValue(id, out var list))
            foreach (var e in list)
                if (e.Ctx == ctx) Call(e, id, args, null);
    }

    private void Call(Entry e, string? id, T args, ICancellableArgs? cancel)
    {
        if (e.Ctx.Dropped) return;
        var monitor = e.Priority == HookPriority.Monitor;
        var was = cancel?.IsCancelled == true;
        if (was && !monitor && !e.Receives) return;
        var (reason, by) = monitor && was ? (cancel!.CancelReason, cancel.CancelledBy) : (null, null);
        if (host.HookDepth >= ModuleHost.MaxHookDepth)
        {
            host.TooDeep(e.Ctx, Name);
            return;
        }
        host.HookDepth++;
        var start = Stopwatch.GetTimestamp();
        host.Guard(e.Ctx, Name, e.Handler, args);
        var ticks = Stopwatch.GetTimestamp() - start;
        host.HookDepth--;
        e.Calls++;
        e.Ticks += ticks;
        if (cancel is not null && cancel.IsCancelled != was)
        {
            if (monitor) host.MonitorChanged(e.Ctx, Name, cancel, was, reason, by); // a Monitor only watches
            else if (!was) cancel.MarkCancelledBy(e.Ctx.Name);
        }
        if (Traced) host.Trace(Name, e.Ctx.Name, id, ticks, cancel);
    }

    private sealed class Entry(HookPoint<T> point, ModuleContext ctx, string? id, System.Text.RegularExpressions.Regex? mask, Action<T> handler, HookPriority priority, long order)
        : IHookRegistration
    {
        public ModuleContext Ctx { get; } = ctx;
        public string? Id { get; } = id;
        public System.Text.RegularExpressions.Regex? Mask { get; } = mask;
        public Action<T> Handler { get; } = handler;
        public HookPriority Priority { get; } = priority;
        public long Order { get; } = order;
        public bool Receives { get; private set; }
        public long Calls { get; set; }
        public long Ticks { get; set; }

        public IHookRegistration ReceiveCancelled()
        {
            Receives = true;
            return this;
        }

        public void Dispose() => point.Remove(this);
    }
}

/// <summary>Every hook point of a host, by group (the module side is <see cref="ContextHooks"/>).</summary>
internal sealed class HookRegistry
{
    private readonly List<IHookPoint> _points = [];

    public HookRegistry(ModuleHost host)
    {
        Host = host;
        HookPoint<T> P<T>(string name, HookIdKind kind)
        {
            var p = new HookPoint<T>(host, name, kind);
            _points.Add(p);
            return p;
        }
        Started = P<ServerArgs>("Server.OnStarted", HookIdKind.Free);
        Stopping = P<ServerArgs>("Server.OnStopping", HookIdKind.Free);
        ServerTick = P<TickArgs>("Server.OnTick", HookIdKind.Free);
        Login = P<PlayerLoginArgs>("Players.OnLogin", HookIdKind.Free);
        Logout = P<PlayerArgs>("Players.OnLogout", HookIdKind.Free);
        EnterZone = P<ZoneArgs>("Players.OnEnterZone", HookIdKind.Free);
        LeaveZone = P<ZoneArgs>("Players.OnLeaveZone", HookIdKind.Free);
        KnockedDown = P<PlayerDownArgs>("Players.OnKnockedDown", HookIdKind.Free);
        PlayerDied = P<PlayerDownArgs>("Players.OnDied", HookIdKind.Free);
        Goal = P<GoalArgs>("Players.OnGoalCompleted", HookIdKind.Free);
        BeforeCommand = P<BeforeCommandArgs>("Players.OnBeforeCommand", HookIdKind.Free);
        FameGained = P<FameGainedArgs>("Players.OnFameGained", HookIdKind.Free);
        SilverChanged = P<SilverChangedArgs>("Players.OnSilverChanged", HookIdKind.Free);
        BeforeDamage = P<BeforeDamageArgs>("Combat.OnBeforeDamage", HookIdKind.Mob);
        AfterDamage = P<HealthChangeArgs>("Combat.OnAfterDamage", HookIdKind.Mob);
        BeforeAbsorb = P<AbsorbArgs>("Combat.OnBeforeAbsorb", HookIdKind.Mob);
        AfterAbsorb = P<AbsorbArgs>("Combat.OnAfterAbsorb", HookIdKind.Mob);
        HitAvoided = P<HitAvoidedArgs>("Combat.OnHitAvoided", HookIdKind.Mob);
        BeforeHeal = P<BeforeHealArgs>("Combat.OnBeforeHeal", HookIdKind.Mob);
        AfterHeal = P<HealthChangeArgs>("Combat.OnAfterHeal", HookIdKind.Mob);
        MobSpawned = P<MobArgs>("Mobs.OnSpawned", HookIdKind.Mob);
        MobDied = P<MobDeathArgs>("Mobs.OnDied", HookIdKind.Mob);
        LootRolled = P<LootArgs>("Mobs.OnLootRolled", HookIdKind.Mob);
        Think = P<BeforeThinkArgs>("Mobs.OnBeforeThink", HookIdKind.Mob);
        BeforeAggro = P<BeforeAggroArgs>("Mobs.OnBeforeAggro", HookIdKind.Mob);
        MobAggro = P<MobAggroArgs>("Mobs.OnAggro", HookIdKind.Mob);
        MobLeftCombat = P<MobLeaveCombatArgs>("Mobs.OnLeaveCombat", HookIdKind.Mob);
        BeforeLeaveCombat = P<BeforeLeaveCombatArgs>("Mobs.OnBeforeLeaveCombat", HookIdKind.Mob);
        Respawn = P<BeforeRespawnArgs>("Mobs.OnBeforeRespawn", HookIdKind.Mob);
        CheckCast = P<BeforeCastArgs>("Spells.OnBeforeCast", HookIdKind.Spell);
        Cast = P<SpellCastArgs>("Spells.OnCast", HookIdKind.Spell);
        CastCancel = P<SpellCancelArgs>("Spells.OnCancel", HookIdKind.Spell);
        BeforeHit = P<SpellHitArgs>("Spells.OnBeforeHit", HookIdKind.Spell);
        AfterHit = P<SpellHitArgs>("Spells.OnAfterHit", HookIdKind.Spell);
        Effect = P<SpellEffectArgs>("Spells.OnBeforeEffect", HookIdKind.Spell);
        BeforeTrigger = P<SpellTriggerArgs>("Spells.OnBeforeTrigger", HookIdKind.Spell);
        DamageEarly = P<SpellDamageArgs>("Spells.OnBeforeDamage", HookIdKind.Spell);
        DamageLate = P<SpellDamageArgs>("Spells.OnAfterDamage", HookIdKind.Spell);
        Tick = P<SpellTickArgs>("Spells.OnTick", HookIdKind.Spell);
        AreaCreate = P<SpellAreaArgs>("Spells.OnAreaCreate", HookIdKind.Spell);
        AreaTick = P<SpellAreaArgs>("Spells.OnAreaTick", HookIdKind.Spell);
        BuffCheck = P<BeforeBuffArgs>("Buffs.OnBeforeApply", HookIdKind.Spell);
        BuffApply = P<BuffArgs>("Buffs.OnApply", HookIdKind.Spell);
        BuffStack = P<BuffStackArgs>("Buffs.OnStackChange", HookIdKind.Spell);
        BuffTick = P<BuffArgs>("Buffs.OnTick", HookIdKind.Spell);
        BuffRemove = P<BuffRemoveArgs>("Buffs.OnRemove", HookIdKind.Spell);
        Equip = P<EquipArgs>("Items.OnEquip", HookIdKind.Item);
        Unequip = P<EquipArgs>("Items.OnUnequip", HookIdKind.Item);
        WeaponSwap = P<WeaponSwapArgs>("Items.OnWeaponSwap", HookIdKind.Item);
        Break = P<ItemBreakArgs>("Items.OnBreak", HookIdKind.Item);
        Passives = P<PassiveArgs>("Items.OnCalcPassives", HookIdKind.Item);
        Used = P<ItemUsedArgs>("Items.OnUsed", HookIdKind.Item);
        Crafted = P<ItemCraftedArgs>("Items.OnCrafted", HookIdKind.Item);
        Harvested = P<HarvestArgs>("Items.OnHarvested", HookIdKind.Item);
        Station = P<BuildingActionArgs>("Items.OnStationAction", HookIdKind.Free);
        LootTake = P<BeforeLootTakeArgs>("Items.OnBeforeLootTake", HookIdKind.Item);
        ChestLoot = P<ChestLootArgs>("Items.OnChestLootRolled", HookIdKind.Free);
        MountCheck = P<BeforeMountArgs>("Mounts.OnBeforeMount", HookIdKind.Item);
        MountStart = P<MountArgs>("Mounts.OnMountStart", HookIdKind.Item);
        Mounted = P<MountedArgs>("Mounts.OnMounted", HookIdKind.Item);
        MountCancel = P<MountArgs>("Mounts.OnMountCancel", HookIdKind.Item);
        Dismount = P<MountArgs>("Mounts.OnDismount", HookIdKind.Item);
        MountDamage = P<MountDamageArgs>("Mounts.OnCalcMountDamage", HookIdKind.Item);
        MountSpell = P<SpellCastArgs>("Mounts.OnMountSpell", HookIdKind.Item);
        Health = P<UnitValueArgs>("Units.OnHealthChange", HookIdKind.Mob);
        Energy = P<UnitValueArgs>("Units.OnEnergyChange", HookIdKind.Mob);
        EnterCombat = P<UnitArgs>("Units.OnEnterCombat", HookIdKind.Mob);
        LeaveCombat = P<UnitArgs>("Units.OnLeaveCombat", HookIdKind.Mob);
        Revive = P<UnitArgs>("Units.OnRevive", HookIdKind.Mob);
        Reload = P<UnitArgs>("Units.OnReload", HookIdKind.Mob);
        CastTime = P<SpellFormulaArgs>("Formulas.OnCalcCastTime", HookIdKind.Spell);
        Cooldown = P<SpellFormulaArgs>("Formulas.OnCalcCooldown", HookIdKind.Spell);
        EnergyCost = P<SpellFormulaArgs>("Formulas.OnCalcEnergyCost", HookIdKind.Spell);
        Heal = P<SpellFormulaArgs>("Formulas.OnCalcHeal", HookIdKind.Spell);
        Threat = P<UnitFormulaArgs>("Formulas.OnCalcThreat", HookIdKind.Mob);
        Fame = P<UnitFormulaArgs>("Formulas.OnCalcFame", HookIdKind.Mob);
        Silver = P<UnitFormulaArgs>("Formulas.OnCalcSilver", HookIdKind.Mob);
        ItemPower = P<ItemFormulaArgs>("Formulas.OnCalcItemPower", HookIdKind.Item);
        Chat = P<ChatArgs>("Social.OnBeforeChat", HookIdKind.Free);
        PartyJoined = P<PartyArgs>("Social.OnPartyJoined", HookIdKind.Free);
        PartyLeft = P<PartyArgs>("Social.OnPartyLeft", HookIdKind.Free);
        Request = P<RequestArgs>("Network.OnRequest", HookIdKind.Operation);
        OutEvent = P<OutgoingArgs>("Network.OnEvent", HookIdKind.Event);
        OutResponse = P<OutgoingArgs>("Network.OnResponse", HookIdKind.Operation);
        MapLoaded = P<MapArgs>("Maps.OnLoaded", HookIdKind.Free);
        MapUnloaded = P<MapArgs>("Maps.OnUnloaded", HookIdKind.Free);
    }

    public ModuleHost Host { get; }

    /// <summary>Every hook point, in the API's order.</summary>
    public IReadOnlyList<IHookPoint> All => _points;

    /// <summary>The hook named <paramref name="name"/> ("Combat.OnBeforeDamage"); null when there is none.</summary>
    public IHookPoint? Find(string name) => _points.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public HookPoint<ServerArgs> Started { get; }
    public HookPoint<ServerArgs> Stopping { get; }
    public HookPoint<TickArgs> ServerTick { get; }
    public HookPoint<PlayerLoginArgs> Login { get; }
    public HookPoint<PlayerArgs> Logout { get; }
    public HookPoint<ZoneArgs> EnterZone { get; }
    public HookPoint<ZoneArgs> LeaveZone { get; }
    public HookPoint<PlayerDownArgs> KnockedDown { get; }
    public HookPoint<PlayerDownArgs> PlayerDied { get; }
    public HookPoint<GoalArgs> Goal { get; }
    public HookPoint<BeforeCommandArgs> BeforeCommand { get; }
    public HookPoint<FameGainedArgs> FameGained { get; }
    public HookPoint<SilverChangedArgs> SilverChanged { get; }
    public HookPoint<BeforeDamageArgs> BeforeDamage { get; }
    public HookPoint<HealthChangeArgs> AfterDamage { get; }
    public HookPoint<AbsorbArgs> BeforeAbsorb { get; }
    public HookPoint<AbsorbArgs> AfterAbsorb { get; }
    public HookPoint<HitAvoidedArgs> HitAvoided { get; }
    public HookPoint<BeforeHealArgs> BeforeHeal { get; }
    public HookPoint<HealthChangeArgs> AfterHeal { get; }
    public HookPoint<MobArgs> MobSpawned { get; }
    public HookPoint<MobDeathArgs> MobDied { get; }
    public HookPoint<LootArgs> LootRolled { get; }
    public HookPoint<BeforeThinkArgs> Think { get; }
    public HookPoint<BeforeAggroArgs> BeforeAggro { get; }
    public HookPoint<MobAggroArgs> MobAggro { get; }
    public HookPoint<MobLeaveCombatArgs> MobLeftCombat { get; }
    public HookPoint<BeforeLeaveCombatArgs> BeforeLeaveCombat { get; }
    public HookPoint<BeforeRespawnArgs> Respawn { get; }
    public HookPoint<BeforeCastArgs> CheckCast { get; }
    public HookPoint<SpellCastArgs> Cast { get; }
    public HookPoint<SpellCancelArgs> CastCancel { get; }
    public HookPoint<SpellHitArgs> BeforeHit { get; }
    public HookPoint<SpellHitArgs> AfterHit { get; }
    public HookPoint<SpellTriggerArgs> BeforeTrigger { get; }
    public HookPoint<SpellEffectArgs> Effect { get; }
    public HookPoint<SpellDamageArgs> DamageEarly { get; }
    public HookPoint<SpellDamageArgs> DamageLate { get; }
    public HookPoint<SpellTickArgs> Tick { get; }
    public HookPoint<SpellAreaArgs> AreaCreate { get; }
    public HookPoint<SpellAreaArgs> AreaTick { get; }
    public HookPoint<BeforeBuffArgs> BuffCheck { get; }
    public HookPoint<BuffArgs> BuffApply { get; }
    public HookPoint<BuffStackArgs> BuffStack { get; }
    public HookPoint<BuffArgs> BuffTick { get; }
    public HookPoint<BuffRemoveArgs> BuffRemove { get; }
    public HookPoint<EquipArgs> Equip { get; }
    public HookPoint<EquipArgs> Unequip { get; }
    public HookPoint<WeaponSwapArgs> WeaponSwap { get; }
    public HookPoint<ItemBreakArgs> Break { get; }
    public HookPoint<PassiveArgs> Passives { get; }
    public HookPoint<ItemUsedArgs> Used { get; }
    public HookPoint<ItemCraftedArgs> Crafted { get; }
    public HookPoint<HarvestArgs> Harvested { get; }
    public HookPoint<BuildingActionArgs> Station { get; }
    public HookPoint<BeforeLootTakeArgs> LootTake { get; }
    public HookPoint<ChestLootArgs> ChestLoot { get; }
    public HookPoint<BeforeMountArgs> MountCheck { get; }
    public HookPoint<MountArgs> MountStart { get; }
    public HookPoint<MountedArgs> Mounted { get; }
    public HookPoint<MountArgs> MountCancel { get; }
    public HookPoint<MountArgs> Dismount { get; }
    public HookPoint<MountDamageArgs> MountDamage { get; }
    public HookPoint<SpellCastArgs> MountSpell { get; }
    public HookPoint<UnitValueArgs> Health { get; }
    public HookPoint<UnitValueArgs> Energy { get; }
    public HookPoint<UnitArgs> EnterCombat { get; }
    public HookPoint<UnitArgs> LeaveCombat { get; }
    public HookPoint<UnitArgs> Revive { get; }
    public HookPoint<UnitArgs> Reload { get; }
    public HookPoint<SpellFormulaArgs> CastTime { get; }
    public HookPoint<SpellFormulaArgs> Cooldown { get; }
    public HookPoint<SpellFormulaArgs> EnergyCost { get; }
    public HookPoint<SpellFormulaArgs> Heal { get; }
    public HookPoint<UnitFormulaArgs> Threat { get; }
    public HookPoint<UnitFormulaArgs> Fame { get; }
    public HookPoint<UnitFormulaArgs> Silver { get; }
    public HookPoint<ItemFormulaArgs> ItemPower { get; }
    public HookPoint<ChatArgs> Chat { get; }
    public HookPoint<PartyArgs> PartyJoined { get; }
    public HookPoint<PartyArgs> PartyLeft { get; }
    public HookPoint<RequestArgs> Request { get; }
    public HookPoint<OutgoingArgs> OutEvent { get; }
    public HookPoint<OutgoingArgs> OutResponse { get; }
    public HookPoint<MapArgs> MapLoaded { get; }
    public HookPoint<MapArgs> MapUnloaded { get; }

    public void RemoveAll(ModuleContext ctx)
    {
        foreach (var p in _points) p.RemoveAll(ctx);
    }

    /// <summary>The hooks <paramref name="ctx"/> registered (its load line).</summary>
    public IEnumerable<string> Of(ModuleContext ctx) => _points.Where(p => p.Has(ctx)).Select(p => p.Name);
}

/// <summary>A module's view of the hooks (<see cref="IModuleContext.Hooks"/>): every registration is made for its context.</summary>
internal sealed class ContextHooks(ModuleContext ctx, HookRegistry r) : IHooks
{
    public IServerHooks Server { get; } = new ServerFacade(ctx, r);
    public IPlayerHooks Players { get; } = new PlayersFacade(ctx, r);
    public ICombatHooks Combat { get; } = new CombatFacade(ctx, r);
    public IMobHooks Mobs { get; } = new MobsFacade(ctx, r);
    public ISpellHooks Spells { get; } = new SpellsFacade(ctx, r);
    public IBuffHooks Buffs { get; } = new BuffsFacade(ctx, r);
    public IItemHooks Items { get; } = new ItemsFacade(ctx, r);
    public IMountHooks Mounts { get; } = new MountsFacade(ctx, r);
    public IUnitHooks Units { get; } = new UnitsFacade(ctx, r);
    public IFormulaHooks Formulas { get; } = new FormulasFacade(ctx, r);
    public ISocialHooks Social { get; } = new SocialFacade(ctx, r);
    public INetworkHooks Network { get; } = new NetworkFacade(ctx, r);
    public IMapHooks Maps { get; } = new MapsFacade(ctx, r);

    private sealed class ServerFacade(ModuleContext ctx, HookRegistry r) : IServerHooks
    {
        public IHookRegistration OnStarted(Action<ServerArgs> h, HookPriority priority = HookPriority.Normal) => r.Started.Add(ctx, null, h, priority);
        public IHookRegistration OnStopping(Action<ServerArgs> h, HookPriority priority = HookPriority.Normal) => r.Stopping.Add(ctx, null, h, priority);
        public IHookRegistration OnTick(Action<TickArgs> h, HookPriority priority = HookPriority.Normal) => r.ServerTick.Add(ctx, null, h, priority);
    }

    private sealed class PlayersFacade(ModuleContext ctx, HookRegistry r) : IPlayerHooks
    {
        public IHookRegistration OnLogin(Action<PlayerLoginArgs> h, HookPriority priority = HookPriority.Normal) => r.Login.Add(ctx, null, h, priority);
        public IHookRegistration OnLogout(Action<PlayerArgs> h, HookPriority priority = HookPriority.Normal) => r.Logout.Add(ctx, null, h, priority);
        public IHookRegistration OnEnterZone(Action<ZoneArgs> h, HookPriority priority = HookPriority.Normal) => r.EnterZone.Add(ctx, null, h, priority);
        public IHookRegistration OnEnterZone(string cluster, Action<ZoneArgs> h, HookPriority priority = HookPriority.Normal) => r.EnterZone.Add(ctx, cluster, h, priority);
        public IHookRegistration OnLeaveZone(Action<ZoneArgs> h, HookPriority priority = HookPriority.Normal) => r.LeaveZone.Add(ctx, null, h, priority);
        public IHookRegistration OnLeaveZone(string cluster, Action<ZoneArgs> h, HookPriority priority = HookPriority.Normal) => r.LeaveZone.Add(ctx, cluster, h, priority);
        public IHookRegistration OnKnockedDown(Action<PlayerDownArgs> h, HookPriority priority = HookPriority.Normal) => r.KnockedDown.Add(ctx, null, h, priority);
        public IHookRegistration OnDied(Action<PlayerDownArgs> h, HookPriority priority = HookPriority.Normal) => r.PlayerDied.Add(ctx, null, h, priority);
        public IHookRegistration OnGoalCompleted(Action<GoalArgs> h, HookPriority priority = HookPriority.Normal) => r.Goal.Add(ctx, null, h, priority);
        public IHookRegistration OnGoalCompleted(string kind, Action<GoalArgs> h, HookPriority priority = HookPriority.Normal) => r.Goal.Add(ctx, kind, h, priority);
        public IHookRegistration OnBeforeCommand(Action<BeforeCommandArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeCommand.Add(ctx, null, h, priority);
        public IHookRegistration OnFameGained(Action<FameGainedArgs> h, HookPriority priority = HookPriority.Normal) => r.FameGained.Add(ctx, null, h, priority);
        public IHookRegistration OnSilverChanged(Action<SilverChangedArgs> h, HookPriority priority = HookPriority.Normal) => r.SilverChanged.Add(ctx, null, h, priority);
    }

    private sealed class CombatFacade(ModuleContext ctx, HookRegistry r) : ICombatHooks
    {
        public IHookRegistration OnBeforeDamage(Action<BeforeDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeDamage.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeDamage(string mob, Action<BeforeDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeDamage.Add(ctx, mob, h, priority);
        public IHookRegistration OnAfterDamage(Action<HealthChangeArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterDamage.Add(ctx, null, h, priority);
        public IHookRegistration OnAfterDamage(string mob, Action<HealthChangeArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterDamage.Add(ctx, mob, h, priority);
        public IHookRegistration OnBeforeAbsorb(Action<AbsorbArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeAbsorb.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeAbsorb(string mob, Action<AbsorbArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeAbsorb.Add(ctx, mob, h, priority);
        public IHookRegistration OnAfterAbsorb(Action<AbsorbArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterAbsorb.Add(ctx, null, h, priority);
        public IHookRegistration OnAfterAbsorb(string mob, Action<AbsorbArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterAbsorb.Add(ctx, mob, h, priority);
        public IHookRegistration OnHitAvoided(Action<HitAvoidedArgs> h, HookPriority priority = HookPriority.Normal) => r.HitAvoided.Add(ctx, null, h, priority);
        public IHookRegistration OnHitAvoided(string mob, Action<HitAvoidedArgs> h, HookPriority priority = HookPriority.Normal) => r.HitAvoided.Add(ctx, mob, h, priority);
        public IHookRegistration OnBeforeHeal(Action<BeforeHealArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeHeal.Add(ctx, null, h, priority);
        public IHookRegistration OnAfterHeal(Action<HealthChangeArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterHeal.Add(ctx, null, h, priority);
        public IHookRegistration OnAfterHeal(string mob, Action<HealthChangeArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterHeal.Add(ctx, mob, h, priority);
    }

    private sealed class MobsFacade(ModuleContext ctx, HookRegistry r) : IMobHooks
    {
        public IHookRegistration OnSpawned(Action<MobArgs> h, HookPriority priority = HookPriority.Normal) => r.MobSpawned.Add(ctx, null, h, priority);
        public IHookRegistration OnSpawned(string mob, Action<MobArgs> h, HookPriority priority = HookPriority.Normal) => r.MobSpawned.Add(ctx, mob, h, priority);
        public IHookRegistration OnDied(Action<MobDeathArgs> h, HookPriority priority = HookPriority.Normal) => r.MobDied.Add(ctx, null, h, priority);
        public IHookRegistration OnDied(string mob, Action<MobDeathArgs> h, HookPriority priority = HookPriority.Normal) => r.MobDied.Add(ctx, mob, h, priority);
        public IHookRegistration OnLootRolled(Action<LootArgs> h, HookPriority priority = HookPriority.Normal) => r.LootRolled.Add(ctx, null, h, priority);
        public IHookRegistration OnLootRolled(string mob, Action<LootArgs> h, HookPriority priority = HookPriority.Normal) => r.LootRolled.Add(ctx, mob, h, priority);
        public IHookRegistration OnBeforeThink(Action<BeforeThinkArgs> h, HookPriority priority = HookPriority.Normal) => r.Think.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeThink(string mob, Action<BeforeThinkArgs> h, HookPriority priority = HookPriority.Normal) => r.Think.Add(ctx, mob, h, priority);
        public IHookRegistration OnBeforeAggro(Action<BeforeAggroArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeAggro.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeAggro(string mob, Action<BeforeAggroArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeAggro.Add(ctx, mob, h, priority);
        public IHookRegistration OnAggro(Action<MobAggroArgs> h, HookPriority priority = HookPriority.Normal) => r.MobAggro.Add(ctx, null, h, priority);
        public IHookRegistration OnAggro(string mob, Action<MobAggroArgs> h, HookPriority priority = HookPriority.Normal) => r.MobAggro.Add(ctx, mob, h, priority);
        public IHookRegistration OnBeforeLeaveCombat(Action<BeforeLeaveCombatArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeLeaveCombat.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeLeaveCombat(string mob, Action<BeforeLeaveCombatArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeLeaveCombat.Add(ctx, mob, h, priority);
        public IHookRegistration OnLeaveCombat(Action<MobLeaveCombatArgs> h, HookPriority priority = HookPriority.Normal) => r.MobLeftCombat.Add(ctx, null, h, priority);
        public IHookRegistration OnLeaveCombat(string mob, Action<MobLeaveCombatArgs> h, HookPriority priority = HookPriority.Normal) => r.MobLeftCombat.Add(ctx, mob, h, priority);
        public IHookRegistration OnBeforeRespawn(Action<BeforeRespawnArgs> h, HookPriority priority = HookPriority.Normal) => r.Respawn.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeRespawn(string mob, Action<BeforeRespawnArgs> h, HookPriority priority = HookPriority.Normal) => r.Respawn.Add(ctx, mob, h, priority);
    }

    private sealed class MapsFacade(ModuleContext ctx, HookRegistry r) : IMapHooks
    {
        public IHookRegistration OnLoaded(Action<MapArgs> h, HookPriority priority = HookPriority.Normal) => r.MapLoaded.Add(ctx, null, h, priority);
        public IHookRegistration OnLoaded(string cluster, Action<MapArgs> h, HookPriority priority = HookPriority.Normal) => r.MapLoaded.Add(ctx, cluster, h, priority);
        public IHookRegistration OnUnloaded(Action<MapArgs> h, HookPriority priority = HookPriority.Normal) => r.MapUnloaded.Add(ctx, null, h, priority);
        public IHookRegistration OnUnloaded(string cluster, Action<MapArgs> h, HookPriority priority = HookPriority.Normal) => r.MapUnloaded.Add(ctx, cluster, h, priority);
    }

    private sealed class SpellsFacade(ModuleContext ctx, HookRegistry r) : ISpellHooks
    {
        public IHookRegistration OnBeforeCast(Action<BeforeCastArgs> h, HookPriority priority = HookPriority.Normal) => r.CheckCast.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeCast(string spell, Action<BeforeCastArgs> h, HookPriority priority = HookPriority.Normal) => r.CheckCast.Add(ctx, spell, h, priority);
        public IHookRegistration OnCast(Action<SpellCastArgs> h, HookPriority priority = HookPriority.Normal) => r.Cast.Add(ctx, null, h, priority);
        public IHookRegistration OnCast(string spell, Action<SpellCastArgs> h, HookPriority priority = HookPriority.Normal) => r.Cast.Add(ctx, spell, h, priority);
        public IHookRegistration OnCancel(Action<SpellCancelArgs> h, HookPriority priority = HookPriority.Normal) => r.CastCancel.Add(ctx, null, h, priority);
        public IHookRegistration OnCancel(string spell, Action<SpellCancelArgs> h, HookPriority priority = HookPriority.Normal) => r.CastCancel.Add(ctx, spell, h, priority);
        public IHookRegistration OnBeforeHit(Action<SpellHitArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeHit.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeHit(string spell, Action<SpellHitArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeHit.Add(ctx, spell, h, priority);
        public IHookRegistration OnAfterHit(Action<SpellHitArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterHit.Add(ctx, null, h, priority);
        public IHookRegistration OnAfterHit(string spell, Action<SpellHitArgs> h, HookPriority priority = HookPriority.Normal) => r.AfterHit.Add(ctx, spell, h, priority);
        public IHookRegistration OnBeforeEffect(Action<SpellEffectArgs> h, HookPriority priority = HookPriority.Normal) => r.Effect.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeEffect(string spell, Action<SpellEffectArgs> h, HookPriority priority = HookPriority.Normal) => r.Effect.Add(ctx, spell, h, priority);
        public IHookRegistration OnBeforeTrigger(Action<SpellTriggerArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeTrigger.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeTrigger(string spell, Action<SpellTriggerArgs> h, HookPriority priority = HookPriority.Normal) => r.BeforeTrigger.Add(ctx, spell, h, priority);
        public IHookRegistration OnBeforeDamage(Action<SpellDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.DamageEarly.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeDamage(string spell, Action<SpellDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.DamageEarly.Add(ctx, spell, h, priority);
        public IHookRegistration OnAfterDamage(Action<SpellDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.DamageLate.Add(ctx, null, h, priority);
        public IHookRegistration OnAfterDamage(string spell, Action<SpellDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.DamageLate.Add(ctx, spell, h, priority);
        public IHookRegistration OnTick(Action<SpellTickArgs> h, HookPriority priority = HookPriority.Normal) => r.Tick.Add(ctx, null, h, priority);
        public IHookRegistration OnTick(string spell, Action<SpellTickArgs> h, HookPriority priority = HookPriority.Normal) => r.Tick.Add(ctx, spell, h, priority);
        public IHookRegistration OnAreaCreate(Action<SpellAreaArgs> h, HookPriority priority = HookPriority.Normal) => r.AreaCreate.Add(ctx, null, h, priority);
        public IHookRegistration OnAreaCreate(string spell, Action<SpellAreaArgs> h, HookPriority priority = HookPriority.Normal) => r.AreaCreate.Add(ctx, spell, h, priority);
        public IHookRegistration OnAreaTick(Action<SpellAreaArgs> h, HookPriority priority = HookPriority.Normal) => r.AreaTick.Add(ctx, null, h, priority);
        public IHookRegistration OnAreaTick(string spell, Action<SpellAreaArgs> h, HookPriority priority = HookPriority.Normal) => r.AreaTick.Add(ctx, spell, h, priority);
        public IHookRegistration Register(string spell, CustomSpell script) => r.Host.RegisterScript(ctx, spell, script);
    }

    private sealed class BuffsFacade(ModuleContext ctx, HookRegistry r) : IBuffHooks
    {
        public IHookRegistration OnBeforeApply(Action<BeforeBuffArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffCheck.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeApply(string buff, Action<BeforeBuffArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffCheck.Add(ctx, buff, h, priority);
        public IHookRegistration OnApply(Action<BuffArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffApply.Add(ctx, null, h, priority);
        public IHookRegistration OnApply(string buff, Action<BuffArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffApply.Add(ctx, buff, h, priority);
        public IHookRegistration OnStackChange(Action<BuffStackArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffStack.Add(ctx, null, h, priority);
        public IHookRegistration OnStackChange(string buff, Action<BuffStackArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffStack.Add(ctx, buff, h, priority);
        public IHookRegistration OnTick(Action<BuffArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffTick.Add(ctx, null, h, priority);
        public IHookRegistration OnTick(string buff, Action<BuffArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffTick.Add(ctx, buff, h, priority);
        public IHookRegistration OnRemove(Action<BuffRemoveArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffRemove.Add(ctx, null, h, priority);
        public IHookRegistration OnRemove(string buff, Action<BuffRemoveArgs> h, HookPriority priority = HookPriority.Normal) => r.BuffRemove.Add(ctx, buff, h, priority);
    }

    private sealed class ItemsFacade(ModuleContext ctx, HookRegistry r) : IItemHooks
    {
        public IHookRegistration OnEquip(Action<EquipArgs> h, HookPriority priority = HookPriority.Normal) => r.Equip.Add(ctx, null, h, priority);
        public IHookRegistration OnEquip(string item, Action<EquipArgs> h, HookPriority priority = HookPriority.Normal) => r.Equip.Add(ctx, item, h, priority);
        public IHookRegistration OnUnequip(Action<EquipArgs> h, HookPriority priority = HookPriority.Normal) => r.Unequip.Add(ctx, null, h, priority);
        public IHookRegistration OnUnequip(string item, Action<EquipArgs> h, HookPriority priority = HookPriority.Normal) => r.Unequip.Add(ctx, item, h, priority);
        public IHookRegistration OnWeaponSwap(Action<WeaponSwapArgs> h, HookPriority priority = HookPriority.Normal) => r.WeaponSwap.Add(ctx, null, h, priority);
        public IHookRegistration OnWeaponSwap(string item, Action<WeaponSwapArgs> h, HookPriority priority = HookPriority.Normal) => r.WeaponSwap.Add(ctx, item, h, priority);
        public IHookRegistration OnBreak(Action<ItemBreakArgs> h, HookPriority priority = HookPriority.Normal) => r.Break.Add(ctx, null, h, priority);
        public IHookRegistration OnBreak(string item, Action<ItemBreakArgs> h, HookPriority priority = HookPriority.Normal) => r.Break.Add(ctx, item, h, priority);
        public IHookRegistration OnCalcPassives(Action<PassiveArgs> h, HookPriority priority = HookPriority.Normal) => r.Host.PassivesChanged(r.Passives.Add(ctx, null, h, priority));
        public IHookRegistration OnCalcPassives(string item, Action<PassiveArgs> h, HookPriority priority = HookPriority.Normal) => r.Host.PassivesChanged(r.Passives.Add(ctx, item, h, priority));
        public IHookRegistration OnUsed(Action<ItemUsedArgs> h, HookPriority priority = HookPriority.Normal) => r.Used.Add(ctx, null, h, priority);
        public IHookRegistration OnUsed(string item, Action<ItemUsedArgs> h, HookPriority priority = HookPriority.Normal) => r.Used.Add(ctx, item, h, priority);
        public IHookRegistration OnCrafted(Action<ItemCraftedArgs> h, HookPriority priority = HookPriority.Normal) => r.Crafted.Add(ctx, null, h, priority);
        public IHookRegistration OnCrafted(string item, Action<ItemCraftedArgs> h, HookPriority priority = HookPriority.Normal) => r.Crafted.Add(ctx, item, h, priority);
        public IHookRegistration OnHarvested(Action<HarvestArgs> h, HookPriority priority = HookPriority.Normal) => r.Harvested.Add(ctx, null, h, priority);
        public IHookRegistration OnHarvested(string item, Action<HarvestArgs> h, HookPriority priority = HookPriority.Normal) => r.Harvested.Add(ctx, item, h, priority);
        public IHookRegistration OnStationAction(Action<BuildingActionArgs> h, HookPriority priority = HookPriority.Normal) => r.Station.Add(ctx, null, h, priority);
        public IHookRegistration OnStationAction(string building, Action<BuildingActionArgs> h, HookPriority priority = HookPriority.Normal) => r.Station.Add(ctx, building, h, priority);
        public IHookRegistration OnBeforeLootTake(Action<BeforeLootTakeArgs> h, HookPriority priority = HookPriority.Normal) => r.LootTake.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeLootTake(string item, Action<BeforeLootTakeArgs> h, HookPriority priority = HookPriority.Normal) => r.LootTake.Add(ctx, item, h, priority);
        public IHookRegistration OnChestLootRolled(Action<ChestLootArgs> h, HookPriority priority = HookPriority.Normal) => r.ChestLoot.Add(ctx, null, h, priority);
        public IHookRegistration OnChestLootRolled(string chest, Action<ChestLootArgs> h, HookPriority priority = HookPriority.Normal) => r.ChestLoot.Add(ctx, chest, h, priority);
    }

    private sealed class MountsFacade(ModuleContext ctx, HookRegistry r) : IMountHooks
    {
        public IHookRegistration OnBeforeMount(Action<BeforeMountArgs> h, HookPriority priority = HookPriority.Normal) => r.MountCheck.Add(ctx, null, h, priority);
        public IHookRegistration OnBeforeMount(string mount, Action<BeforeMountArgs> h, HookPriority priority = HookPriority.Normal) => r.MountCheck.Add(ctx, mount, h, priority);
        public IHookRegistration OnMountStart(Action<MountArgs> h, HookPriority priority = HookPriority.Normal) => r.MountStart.Add(ctx, null, h, priority);
        public IHookRegistration OnMountStart(string mount, Action<MountArgs> h, HookPriority priority = HookPriority.Normal) => r.MountStart.Add(ctx, mount, h, priority);
        public IHookRegistration OnMounted(Action<MountedArgs> h, HookPriority priority = HookPriority.Normal) => r.Mounted.Add(ctx, null, h, priority);
        public IHookRegistration OnMounted(string mount, Action<MountedArgs> h, HookPriority priority = HookPriority.Normal) => r.Mounted.Add(ctx, mount, h, priority);
        public IHookRegistration OnMountCancel(Action<MountArgs> h, HookPriority priority = HookPriority.Normal) => r.MountCancel.Add(ctx, null, h, priority);
        public IHookRegistration OnMountCancel(string mount, Action<MountArgs> h, HookPriority priority = HookPriority.Normal) => r.MountCancel.Add(ctx, mount, h, priority);
        public IHookRegistration OnDismount(Action<MountArgs> h, HookPriority priority = HookPriority.Normal) => r.Dismount.Add(ctx, null, h, priority);
        public IHookRegistration OnDismount(string mount, Action<MountArgs> h, HookPriority priority = HookPriority.Normal) => r.Dismount.Add(ctx, mount, h, priority);
        public IHookRegistration OnCalcMountDamage(Action<MountDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.MountDamage.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcMountDamage(string mount, Action<MountDamageArgs> h, HookPriority priority = HookPriority.Normal) => r.MountDamage.Add(ctx, mount, h, priority);
        public IHookRegistration OnMountSpell(Action<SpellCastArgs> h, HookPriority priority = HookPriority.Normal) => r.MountSpell.Add(ctx, null, h, priority);
        public IHookRegistration OnMountSpell(string mount, Action<SpellCastArgs> h, HookPriority priority = HookPriority.Normal) => r.MountSpell.Add(ctx, mount, h, priority);
    }

    private sealed class UnitsFacade(ModuleContext ctx, HookRegistry r) : IUnitHooks
    {
        public IHookRegistration OnHealthChange(Action<UnitValueArgs> h, HookPriority priority = HookPriority.Normal) => r.Health.Add(ctx, null, h, priority);
        public IHookRegistration OnHealthChange(string mob, Action<UnitValueArgs> h, HookPriority priority = HookPriority.Normal) => r.Health.Add(ctx, mob, h, priority);
        public IHookRegistration OnEnergyChange(Action<UnitValueArgs> h, HookPriority priority = HookPriority.Normal) => r.Energy.Add(ctx, null, h, priority);
        public IHookRegistration OnEnterCombat(Action<UnitArgs> h, HookPriority priority = HookPriority.Normal) => r.EnterCombat.Add(ctx, null, h, priority);
        public IHookRegistration OnLeaveCombat(Action<UnitArgs> h, HookPriority priority = HookPriority.Normal) => r.LeaveCombat.Add(ctx, null, h, priority);
        public IHookRegistration OnRevive(Action<UnitArgs> h, HookPriority priority = HookPriority.Normal) => r.Revive.Add(ctx, null, h, priority);
        public IHookRegistration OnReload(Action<UnitArgs> h, HookPriority priority = HookPriority.Normal) => r.Reload.Add(ctx, null, h, priority);
        public IHookRegistration OnReload(string mob, Action<UnitArgs> h, HookPriority priority = HookPriority.Normal) => r.Reload.Add(ctx, mob, h, priority);
    }

    private sealed class FormulasFacade(ModuleContext ctx, HookRegistry r) : IFormulaHooks
    {
        public IHookRegistration OnCalcCastTime(Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.CastTime.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcCastTime(string spell, Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.CastTime.Add(ctx, spell, h, priority);
        public IHookRegistration OnCalcCooldown(Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Cooldown.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcCooldown(string spell, Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Cooldown.Add(ctx, spell, h, priority);
        public IHookRegistration OnCalcEnergyCost(Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.EnergyCost.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcEnergyCost(string spell, Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.EnergyCost.Add(ctx, spell, h, priority);
        public IHookRegistration OnCalcHeal(Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Heal.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcHeal(string spell, Action<SpellFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Heal.Add(ctx, spell, h, priority);
        public IHookRegistration OnCalcThreat(Action<UnitFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Threat.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcThreat(string mob, Action<UnitFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Threat.Add(ctx, mob, h, priority);
        public IHookRegistration OnCalcFame(Action<UnitFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Fame.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcFame(string mob, Action<UnitFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Fame.Add(ctx, mob, h, priority);
        public IHookRegistration OnCalcSilver(Action<UnitFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Silver.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcSilver(string mob, Action<UnitFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.Silver.Add(ctx, mob, h, priority);
        public IHookRegistration OnCalcItemPower(Action<ItemFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.ItemPower.Add(ctx, null, h, priority);
        public IHookRegistration OnCalcItemPower(string item, Action<ItemFormulaArgs> h, HookPriority priority = HookPriority.Normal) => r.ItemPower.Add(ctx, item, h, priority);
    }

    private sealed class SocialFacade(ModuleContext ctx, HookRegistry r) : ISocialHooks
    {
        public IHookRegistration OnBeforeChat(Action<ChatArgs> h, HookPriority priority = HookPriority.Normal) => r.Chat.Add(ctx, null, h, priority);
        public IHookRegistration OnPartyJoined(Action<PartyArgs> h, HookPriority priority = HookPriority.Normal) => r.PartyJoined.Add(ctx, null, h, priority);
        public IHookRegistration OnPartyLeft(Action<PartyArgs> h, HookPriority priority = HookPriority.Normal) => r.PartyLeft.Add(ctx, null, h, priority);
    }

    private sealed class NetworkFacade(ModuleContext ctx, HookRegistry r) : INetworkHooks
    {
        public IHookRegistration OnRequest(Action<RequestArgs> h, HookPriority priority = HookPriority.Normal) => r.Request.Add(ctx, null, h, priority);
        public IHookRegistration OnRequest(string operation, Action<RequestArgs> h, HookPriority priority = HookPriority.Normal) => r.Request.Add(ctx, operation, h, priority);
        public IHookRegistration OnEvent(Action<OutgoingArgs> h, HookPriority priority = HookPriority.Normal) => r.OutEvent.Add(ctx, null, h, priority);
        public IHookRegistration OnEvent(string eventName, Action<OutgoingArgs> h, HookPriority priority = HookPriority.Normal) => r.OutEvent.Add(ctx, eventName, h, priority);
        public IHookRegistration OnResponse(Action<OutgoingArgs> h, HookPriority priority = HookPriority.Normal) => r.OutResponse.Add(ctx, null, h, priority);
        public IHookRegistration OnResponse(string operation, Action<OutgoingArgs> h, HookPriority priority = HookPriority.Normal) => r.OutResponse.Add(ctx, operation, h, priority);
        public IReadOnlyCollection<string> OperationNames => ModuleNetwork.OperationNames;
        public IReadOnlyCollection<string> EventNames => ModuleNetwork.EventNames;
    }
}
