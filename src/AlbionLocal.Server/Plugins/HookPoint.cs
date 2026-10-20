using AlbionLocal.Plugin.Api;

namespace AlbionLocal.Server.Plugins;

/// <summary>What every hook point offers the host: dropping a plugin's registrations, telling whether a plugin has any.</summary>
internal interface IHookPoint
{
    string Name { get; }
    void RemoveAll(PluginContext ctx);
    bool Has(PluginContext ctx);
}

/// <summary>
/// One v2 hook (server/plugins/README.md "Hooks"): its global handlers and its handlers by id (a spell, buff, item, mount or mob uniquename,
/// case-insensitive), in registration order. <see cref="Wants"/> is the call sites' cheap test: a count check with nobody listening, then
/// a dictionary lookup only when filtered handlers exist. Every handler runs in <see cref="PluginHost.Guard{T}"/>. World thread only.
/// </summary>
internal sealed class HookPoint<T>(PluginHost host, string name) : IHookPoint
{
    private readonly List<Entry> _all = [];
    private readonly Dictionary<string, List<Entry>> _byId = new(StringComparer.OrdinalIgnoreCase);
    private int _count;

    public string Name { get; } = name;

    /// <summary>Anyone listens at all.</summary>
    public bool Any => _count > 0;

    /// <summary>Anyone listens for <paramref name="id"/> (a global handler, or one of that id).</summary>
    public bool Wants(string? id) => _count > 0 && (_all.Count > 0 || id is not null && _byId.Count > 0 && _byId.ContainsKey(id));

    public IHookRegistration Add(PluginContext ctx, string? id, Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var e = new Entry(this, ctx, id is { Length: > 0 } ? id.Trim() : null, handler);
        if (ctx.Dropped) return e;
        if (e.Id is null) _all.Add(e);
        else
        {
            if (!_byId.TryGetValue(e.Id, out var list)) _byId[e.Id] = list = [];
            list.Add(e);
        }
        _count++;
        return e;
    }

    private void Remove(Entry e)
    {
        var removed = e.Id is null ? _all.Remove(e) : _byId.TryGetValue(e.Id, out var list) && list.Remove(e) && (list.Count > 0 || _byId.Remove(e.Id));
        if (removed) _count--;
    }

    public void RemoveAll(PluginContext ctx)
    {
        _count -= _all.RemoveAll(e => e.Ctx == ctx);
        foreach (var (id, list) in _byId.ToList())
        {
            _count -= list.RemoveAll(e => e.Ctx == ctx);
            if (list.Count == 0) _byId.Remove(id);
        }
    }

    public bool Has(PluginContext ctx) => _all.Any(e => e.Ctx == ctx) || _byId.Values.Any(l => l.Any(e => e.Ctx == ctx));

    /// <summary>Runs the global handlers, then those of <paramref name="id"/>, each guarded; a plugin dropped meanwhile is skipped.</summary>
    public void Raise(string? id, T args)
    {
        if (_count == 0) return;
        if (_all.Count > 0)
            foreach (var e in _all.ToArray())
                if (!e.Ctx.Dropped) host.Guard(e.Ctx, Name, e.Handler, args);
        if (id is not null && _byId.TryGetValue(id, out var list))
            foreach (var e in list.ToArray())
                if (!e.Ctx.Dropped) host.Guard(e.Ctx, Name, e.Handler, args);
    }

    /// <summary>Only the handlers of <paramref name="id"/> (not the global ones).</summary>
    public void RaiseId(string id, T args)
    {
        if (_byId.TryGetValue(id, out var list))
            foreach (var e in list.ToArray())
                if (!e.Ctx.Dropped) host.Guard(e.Ctx, Name, e.Handler, args);
    }

    /// <summary>As <see cref="Raise"/>, but only <paramref name="ctx"/>'s handlers (OnReload after its load).</summary>
    public void RaiseFor(PluginContext ctx, string? id, T args)
    {
        foreach (var e in _all.ToArray())
            if (e.Ctx == ctx && !ctx.Dropped) host.Guard(ctx, Name, e.Handler, args);
        if (id is not null && _byId.TryGetValue(id, out var list))
            foreach (var e in list.ToArray())
                if (e.Ctx == ctx && !ctx.Dropped) host.Guard(ctx, Name, e.Handler, args);
    }

    private sealed class Entry(HookPoint<T> point, PluginContext ctx, string? id, Action<T> handler) : IHookRegistration
    {
        public PluginContext Ctx { get; } = ctx;
        public string? Id { get; } = id;
        public Action<T> Handler { get; } = handler;
        public void Dispose() => point.Remove(this);
    }
}

/// <summary>Every v2 hook point of a host, by group (the plugin side is <see cref="ContextHooks"/>).</summary>
internal sealed class HookRegistry
{
    private readonly List<IHookPoint> _points = [];

    public HookRegistry(PluginHost host)
    {
        HookPoint<T> P<T>(string name)
        {
            var p = new HookPoint<T>(host, name);
            _points.Add(p);
            return p;
        }
        CheckCast = P<SpellCheckCastArgs>("Spells.OnCheckCast");
        Cast = P<SpellCastArgs>("Spells.OnCast");
        CastCancel = P<SpellCancelArgs>("Spells.OnCancel");
        BeforeHit = P<SpellHitArgs>("Spells.OnBeforeHit");
        AfterHit = P<SpellHitArgs>("Spells.OnAfterHit");
        Effect = P<SpellEffectArgs>("Spells.OnEffect");
        DamageEarly = P<SpellDamageArgs>("Spells.OnDamageEarly");
        DamageLate = P<SpellDamageArgs>("Spells.OnDamageLate");
        Tick = P<SpellTickArgs>("Spells.OnTick");
        AreaCreate = P<SpellAreaArgs>("Spells.OnAreaCreate");
        AreaTick = P<SpellAreaArgs>("Spells.OnAreaTick");
        SlotChanged = P<SlotSpellChangedArgs>("Spells.OnSlotSpellChanged");
        ExtraBarChanged = P<ExtraBarChangedArgs>("Spells.OnExtraBarChanged");
        BuffCheck = P<BuffCheckArgs>("Buffs.OnCheckApply");
        BuffApply = P<BuffArgs>("Buffs.OnApply");
        BuffStack = P<BuffStackArgs>("Buffs.OnStackChange");
        BuffTick = P<BuffArgs>("Buffs.OnTick");
        BuffRemove = P<BuffRemoveArgs>("Buffs.OnRemove");
        Equip = P<EquipArgs>("Items.OnEquip");
        Unequip = P<EquipArgs>("Items.OnUnequip");
        WeaponSwap = P<WeaponSwapArgs>("Items.OnWeaponSwap");
        Break = P<ItemBreakArgs>("Items.OnBreak");
        Passives = P<PassiveArgs>("Items.OnCalcPassives");
        MountCheck = P<MountCheckArgs>("Mounts.OnCheckMount");
        MountStart = P<MountArgs>("Mounts.OnMountStart");
        Mounted = P<MountedArgs>("Mounts.OnMounted");
        MountCancel = P<MountArgs>("Mounts.OnMountCancel");
        Dismount = P<MountArgs>("Mounts.OnDismount");
        MountDamage = P<MountDamageArgs>("Mounts.OnMountDamage");
        MountSpell = P<SpellCastArgs>("Mounts.OnMountSpell");
        Health = P<UnitValueArgs>("Units.OnHealthChange");
        Energy = P<UnitValueArgs>("Units.OnEnergyChange");
        EnterCombat = P<UnitArgs>("Units.OnEnterCombat");
        LeaveCombat = P<UnitArgs>("Units.OnLeaveCombat");
        Revive = P<UnitArgs>("Units.OnRevive");
        Reload = P<UnitArgs>("Units.OnReload");
        CastTime = P<SpellFormulaArgs>("Formulas.OnCalcCastTime");
        Cooldown = P<SpellFormulaArgs>("Formulas.OnCalcCooldown");
        EnergyCost = P<SpellFormulaArgs>("Formulas.OnCalcEnergyCost");
        Heal = P<SpellFormulaArgs>("Formulas.OnCalcHeal");
        Threat = P<UnitFormulaArgs>("Formulas.OnCalcThreat");
        Fame = P<UnitFormulaArgs>("Formulas.OnCalcFame");
        Silver = P<UnitFormulaArgs>("Formulas.OnCalcSilver");
        ItemPower = P<ItemFormulaArgs>("Formulas.OnCalcItemPower");
    }

    public HookPoint<SpellCheckCastArgs> CheckCast { get; }
    public HookPoint<SpellCastArgs> Cast { get; }
    public HookPoint<SpellCancelArgs> CastCancel { get; }
    public HookPoint<SpellHitArgs> BeforeHit { get; }
    public HookPoint<SpellHitArgs> AfterHit { get; }
    public HookPoint<SpellEffectArgs> Effect { get; }
    public HookPoint<SpellDamageArgs> DamageEarly { get; }
    public HookPoint<SpellDamageArgs> DamageLate { get; }
    public HookPoint<SpellTickArgs> Tick { get; }
    public HookPoint<SpellAreaArgs> AreaCreate { get; }
    public HookPoint<SpellAreaArgs> AreaTick { get; }
    public HookPoint<SlotSpellChangedArgs> SlotChanged { get; }
    public HookPoint<ExtraBarChangedArgs> ExtraBarChanged { get; }
    public HookPoint<BuffCheckArgs> BuffCheck { get; }
    public HookPoint<BuffArgs> BuffApply { get; }
    public HookPoint<BuffStackArgs> BuffStack { get; }
    public HookPoint<BuffArgs> BuffTick { get; }
    public HookPoint<BuffRemoveArgs> BuffRemove { get; }
    public HookPoint<EquipArgs> Equip { get; }
    public HookPoint<EquipArgs> Unequip { get; }
    public HookPoint<WeaponSwapArgs> WeaponSwap { get; }
    public HookPoint<ItemBreakArgs> Break { get; }
    public HookPoint<PassiveArgs> Passives { get; }
    public HookPoint<MountCheckArgs> MountCheck { get; }
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

    public void RemoveAll(PluginContext ctx)
    {
        foreach (var p in _points) p.RemoveAll(ctx);
    }

    /// <summary>The hooks <paramref name="ctx"/> registered (its load line).</summary>
    public IEnumerable<string> Of(PluginContext ctx) => _points.Where(p => p.Has(ctx)).Select(p => p.Name);
}

/// <summary>A plugin's view of the hooks (<see cref="IPluginContext.Hooks"/>): every registration is made for its context.</summary>
internal sealed class ContextHooks(PluginContext ctx, PluginHost host) : IHooks, ISpellHooks, IBuffHooks, IItemHooks, IMountHooks, IUnitHooks, IFormulaHooks
{
    private HookRegistry R => host.Hooks;

    public ISpellHooks Spells => this;
    public IBuffHooks Buffs => this;
    public IItemHooks Items => this;
    public IMountHooks Mounts => this;
    public IUnitHooks Units => this;
    public IFormulaHooks Formulas => this;

    public IHookRegistration OnCheckCast(Action<SpellCheckCastArgs> h) => R.CheckCast.Add(ctx, null, h);
    public IHookRegistration OnCheckCast(string spell, Action<SpellCheckCastArgs> h) => R.CheckCast.Add(ctx, spell, h);
    public IHookRegistration OnCast(Action<SpellCastArgs> h) => R.Cast.Add(ctx, null, h);
    public IHookRegistration OnCast(string spell, Action<SpellCastArgs> h) => R.Cast.Add(ctx, spell, h);
    public IHookRegistration OnCancel(Action<SpellCancelArgs> h) => R.CastCancel.Add(ctx, null, h);
    public IHookRegistration OnCancel(string spell, Action<SpellCancelArgs> h) => R.CastCancel.Add(ctx, spell, h);
    public IHookRegistration OnBeforeHit(Action<SpellHitArgs> h) => R.BeforeHit.Add(ctx, null, h);
    public IHookRegistration OnBeforeHit(string spell, Action<SpellHitArgs> h) => R.BeforeHit.Add(ctx, spell, h);
    public IHookRegistration OnAfterHit(Action<SpellHitArgs> h) => R.AfterHit.Add(ctx, null, h);
    public IHookRegistration OnAfterHit(string spell, Action<SpellHitArgs> h) => R.AfterHit.Add(ctx, spell, h);
    public IHookRegistration OnEffect(Action<SpellEffectArgs> h) => R.Effect.Add(ctx, null, h);
    public IHookRegistration OnEffect(string spell, Action<SpellEffectArgs> h) => R.Effect.Add(ctx, spell, h);
    public IHookRegistration OnDamageEarly(Action<SpellDamageArgs> h) => R.DamageEarly.Add(ctx, null, h);
    public IHookRegistration OnDamageEarly(string spell, Action<SpellDamageArgs> h) => R.DamageEarly.Add(ctx, spell, h);
    public IHookRegistration OnDamageLate(Action<SpellDamageArgs> h) => R.DamageLate.Add(ctx, null, h);
    public IHookRegistration OnDamageLate(string spell, Action<SpellDamageArgs> h) => R.DamageLate.Add(ctx, spell, h);
    IHookRegistration ISpellHooks.OnTick(Action<SpellTickArgs> h) => R.Tick.Add(ctx, null, h);
    IHookRegistration ISpellHooks.OnTick(string spell, Action<SpellTickArgs> h) => R.Tick.Add(ctx, spell, h);
    public IHookRegistration OnAreaCreate(Action<SpellAreaArgs> h) => R.AreaCreate.Add(ctx, null, h);
    public IHookRegistration OnAreaCreate(string spell, Action<SpellAreaArgs> h) => R.AreaCreate.Add(ctx, spell, h);
    public IHookRegistration OnAreaTick(Action<SpellAreaArgs> h) => R.AreaTick.Add(ctx, null, h);
    public IHookRegistration OnAreaTick(string spell, Action<SpellAreaArgs> h) => R.AreaTick.Add(ctx, spell, h);
    public IHookRegistration OnSlotSpellChanged(Action<SlotSpellChangedArgs> h) => R.SlotChanged.Add(ctx, null, h);
    public IHookRegistration OnSlotSpellChanged(string spell, Action<SlotSpellChangedArgs> h) => R.SlotChanged.Add(ctx, spell, h);
    public IHookRegistration OnExtraBarChanged(Action<ExtraBarChangedArgs> h) => R.ExtraBarChanged.Add(ctx, null, h);
    public IHookRegistration OnExtraBarChanged(string spell, Action<ExtraBarChangedArgs> h) => R.ExtraBarChanged.Add(ctx, spell, h);
    public IHookRegistration Register(string spell, SpellScript script) => host.RegisterScript(ctx, spell, script);

    public IHookRegistration OnCheckApply(Action<BuffCheckArgs> h) => R.BuffCheck.Add(ctx, null, h);
    public IHookRegistration OnCheckApply(string buff, Action<BuffCheckArgs> h) => R.BuffCheck.Add(ctx, buff, h);
    public IHookRegistration OnApply(Action<BuffArgs> h) => R.BuffApply.Add(ctx, null, h);
    public IHookRegistration OnApply(string buff, Action<BuffArgs> h) => R.BuffApply.Add(ctx, buff, h);
    public IHookRegistration OnStackChange(Action<BuffStackArgs> h) => R.BuffStack.Add(ctx, null, h);
    public IHookRegistration OnStackChange(string buff, Action<BuffStackArgs> h) => R.BuffStack.Add(ctx, buff, h);
    IHookRegistration IBuffHooks.OnTick(Action<BuffArgs> h) => R.BuffTick.Add(ctx, null, h);
    IHookRegistration IBuffHooks.OnTick(string buff, Action<BuffArgs> h) => R.BuffTick.Add(ctx, buff, h);
    public IHookRegistration OnRemove(Action<BuffRemoveArgs> h) => R.BuffRemove.Add(ctx, null, h);
    public IHookRegistration OnRemove(string buff, Action<BuffRemoveArgs> h) => R.BuffRemove.Add(ctx, buff, h);

    public IHookRegistration OnEquip(Action<EquipArgs> h) => R.Equip.Add(ctx, null, h);
    public IHookRegistration OnEquip(string item, Action<EquipArgs> h) => R.Equip.Add(ctx, item, h);
    public IHookRegistration OnUnequip(Action<EquipArgs> h) => R.Unequip.Add(ctx, null, h);
    public IHookRegistration OnUnequip(string item, Action<EquipArgs> h) => R.Unequip.Add(ctx, item, h);
    public IHookRegistration OnWeaponSwap(Action<WeaponSwapArgs> h) => R.WeaponSwap.Add(ctx, null, h);
    public IHookRegistration OnWeaponSwap(string item, Action<WeaponSwapArgs> h) => R.WeaponSwap.Add(ctx, item, h);
    public IHookRegistration OnBreak(Action<ItemBreakArgs> h) => R.Break.Add(ctx, null, h);
    public IHookRegistration OnBreak(string item, Action<ItemBreakArgs> h) => R.Break.Add(ctx, item, h);
    public IHookRegistration OnCalcPassives(Action<PassiveArgs> h) => host.PassivesChanged(R.Passives.Add(ctx, null, h));
    public IHookRegistration OnCalcPassives(string item, Action<PassiveArgs> h) => host.PassivesChanged(R.Passives.Add(ctx, item, h));

    public IHookRegistration OnCheckMount(Action<MountCheckArgs> h) => R.MountCheck.Add(ctx, null, h);
    public IHookRegistration OnCheckMount(string mount, Action<MountCheckArgs> h) => R.MountCheck.Add(ctx, mount, h);
    public IHookRegistration OnMountStart(Action<MountArgs> h) => R.MountStart.Add(ctx, null, h);
    public IHookRegistration OnMountStart(string mount, Action<MountArgs> h) => R.MountStart.Add(ctx, mount, h);
    public IHookRegistration OnMounted(Action<MountedArgs> h) => R.Mounted.Add(ctx, null, h);
    public IHookRegistration OnMounted(string mount, Action<MountedArgs> h) => R.Mounted.Add(ctx, mount, h);
    public IHookRegistration OnMountCancel(Action<MountArgs> h) => R.MountCancel.Add(ctx, null, h);
    public IHookRegistration OnMountCancel(string mount, Action<MountArgs> h) => R.MountCancel.Add(ctx, mount, h);
    public IHookRegistration OnDismount(Action<MountArgs> h) => R.Dismount.Add(ctx, null, h);
    public IHookRegistration OnDismount(string mount, Action<MountArgs> h) => R.Dismount.Add(ctx, mount, h);
    public IHookRegistration OnMountDamage(Action<MountDamageArgs> h) => R.MountDamage.Add(ctx, null, h);
    public IHookRegistration OnMountDamage(string mount, Action<MountDamageArgs> h) => R.MountDamage.Add(ctx, mount, h);
    public IHookRegistration OnMountSpell(Action<SpellCastArgs> h) => R.MountSpell.Add(ctx, null, h);
    public IHookRegistration OnMountSpell(string mount, Action<SpellCastArgs> h) => R.MountSpell.Add(ctx, mount, h);

    public IHookRegistration OnHealthChange(Action<UnitValueArgs> h) => R.Health.Add(ctx, null, h);
    public IHookRegistration OnHealthChange(string mob, Action<UnitValueArgs> h) => R.Health.Add(ctx, mob, h);
    public IHookRegistration OnEnergyChange(Action<UnitValueArgs> h) => R.Energy.Add(ctx, null, h);
    public IHookRegistration OnEnergyChange(string mob, Action<UnitValueArgs> h) => R.Energy.Add(ctx, mob, h);
    public IHookRegistration OnEnterCombat(Action<UnitArgs> h) => R.EnterCombat.Add(ctx, null, h);
    public IHookRegistration OnLeaveCombat(Action<UnitArgs> h) => R.LeaveCombat.Add(ctx, null, h);
    public IHookRegistration OnRevive(Action<UnitArgs> h) => R.Revive.Add(ctx, null, h);
    public IHookRegistration OnReload(Action<UnitArgs> h) => R.Reload.Add(ctx, null, h);
    public IHookRegistration OnReload(string mob, Action<UnitArgs> h) => R.Reload.Add(ctx, mob, h);

    public IHookRegistration OnCalcCastTime(Action<SpellFormulaArgs> h) => R.CastTime.Add(ctx, null, h);
    public IHookRegistration OnCalcCastTime(string spell, Action<SpellFormulaArgs> h) => R.CastTime.Add(ctx, spell, h);
    public IHookRegistration OnCalcCooldown(Action<SpellFormulaArgs> h) => R.Cooldown.Add(ctx, null, h);
    public IHookRegistration OnCalcCooldown(string spell, Action<SpellFormulaArgs> h) => R.Cooldown.Add(ctx, spell, h);
    public IHookRegistration OnCalcEnergyCost(Action<SpellFormulaArgs> h) => R.EnergyCost.Add(ctx, null, h);
    public IHookRegistration OnCalcEnergyCost(string spell, Action<SpellFormulaArgs> h) => R.EnergyCost.Add(ctx, spell, h);
    public IHookRegistration OnCalcHeal(Action<SpellFormulaArgs> h) => R.Heal.Add(ctx, null, h);
    public IHookRegistration OnCalcHeal(string spell, Action<SpellFormulaArgs> h) => R.Heal.Add(ctx, spell, h);
    public IHookRegistration OnCalcThreat(Action<UnitFormulaArgs> h) => R.Threat.Add(ctx, null, h);
    public IHookRegistration OnCalcThreat(string mob, Action<UnitFormulaArgs> h) => R.Threat.Add(ctx, mob, h);
    public IHookRegistration OnCalcFame(Action<UnitFormulaArgs> h) => R.Fame.Add(ctx, null, h);
    public IHookRegistration OnCalcFame(string mob, Action<UnitFormulaArgs> h) => R.Fame.Add(ctx, mob, h);
    public IHookRegistration OnCalcSilver(Action<UnitFormulaArgs> h) => R.Silver.Add(ctx, null, h);
    public IHookRegistration OnCalcSilver(string mob, Action<UnitFormulaArgs> h) => R.Silver.Add(ctx, mob, h);
    public IHookRegistration OnCalcItemPower(Action<ItemFormulaArgs> h) => R.ItemPower.Add(ctx, null, h);
    public IHookRegistration OnCalcItemPower(string item, Action<ItemFormulaArgs> h) => R.ItemPower.Add(ctx, item, h);
}
