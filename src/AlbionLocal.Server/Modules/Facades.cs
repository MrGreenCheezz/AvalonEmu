using System.Globalization;
using System.Numerics;
using AlbionLocal.Modules;
using AlbionLocal.Server.AI;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using IUnit = AlbionLocal.Modules.IUnit;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// A joined player as modules see it (<see cref="IPlayer"/>): reads the live state, acts through the systems the GM commands use
/// (<see cref="Wallet"/>, <see cref="WorldSession.GiveItem"/>, <see cref="Destiny.Award"/>, <see cref="Pvp.ApplySpell"/>, …). One per
/// <see cref="Player"/> object (<see cref="ModuleHost.Wrap(Player)"/>). The actions do nothing once the player has left.
/// </summary>
internal sealed class PlayerFacade(Player p) : IPlayer
{
    public Player Player { get; } = p;
    private World World => Player.World;

    public long ObjectId => Player.Id;
    public string Name => Player.Character.Name;
    public string Cluster => Player.Map?.Cluster ?? "";
    public ZoneInfo Zone => World.Modules.Types.Zone(Player.Map);
    private Vector2 At => Player.PositionAt(DateTime.UtcNow);
    public float X => At.X;
    public float Y => At.Y;
    public float Health => Player.Health.At(World.GameNow);
    public float MaxHealth => Player.Health.Max;
    public bool IsPlayer => true;
    public bool IsBot => Player.IsBot;
    public bool IsInPartyWith(IPlayer other) => other is PlayerFacade o && World.Parties.Of(Player.Character.Id) is { } party && party == World.Parties.Of(o.Player.Character.Id);
    public Guid CharacterId => Player.Character.Id;

    public PlayerLevel Level
    {
        get
        {
            var id = Player.Character.Account is { } a && a != Guid.Empty ? a : World.Characters.Account.Id;
            return (PlayerLevel)(int)(World.Characters.FindAccount(id)?.Level ?? Persistence.AccountLevel.Player);
        }
    }

    public bool IsOnline => Player.Map is not null && Player.Session.Player == Player;
    public long Silver => Player.Character.Silver / 10000;
    public long Fame => Player.Character.Fame / 10000;
    public ItemInfo? Worn(EquipSlot slot) => slot is >= EquipSlot.MainHand and <= EquipSlot.Food && Player.Inventory.Equipment[(int)slot] is { } item ? World.Modules.Types.Info(item) : null;
    public IReadOnlyList<ItemInfo> Equipment => [.. Player.Inventory.Equipment.Items.Select(World.Modules.Types.Info)];
    public IReadOnlyList<ItemInfo> Inventory => [.. Player.Inventory.Bag.Items.Select(World.Modules.Types.Info)];

    public int CountItem(string item) => World.Items.Find(item) is { } type ? WorldSession.CountOf(Player.Inventory.Bag, type) : 0;

    public bool SendMessage(string text, bool error = false)
    {
        if (!IsOnline) return false;
        foreach (var line in text.Split('\n'))
            Player.Session.Send(new SystemMessageEvent { d0p = line.StartsWith('@') ? " " + line : line, d0q = (byte)(error ? 1 : 0) });
        return true;
    }

    public int GiveItem(string item, int count = 1, ItemQuality quality = ItemQuality.Normal)
    {
        if (!IsOnline || count <= 0) return 0;
        if (World.Modules.Types.FindOrHint(item) is null || World.Items.Find(item.Trim()) is not { CanCreate: true } type) return 0;
        var level = type.IsEquipment ? Math.Clamp((int)quality, 1, Math.Max(1, type.MaxQuality)) : 1;
        var given = 0;
        if (type.IsEquipment)
            for (; given < count && WorldSession.Fits(Player.Inventory.Bag, [(type, 1)]); given++) Player.Session.GiveItem(Player, type, 1, level);
        else if (WorldSession.Fits(Player.Inventory.Bag, [(type, count)]))
        {
            Player.Session.GiveItem(Player, type, count);
            given = count;
        }
        if (given > 0) Player.SaveItems();
        return given;
    }

    public bool TakeItem(string item, int count = 1)
    {
        if (!IsOnline || count <= 0 || World.Items.Find(item) is not { } type || WorldSession.CountOf(Player.Inventory.Bag, type) < count) return false;
        Player.Session.TakeItems(Player, type, count);
        Player.SaveItems();
        return true;
    }

    public bool GiveSilver(long silver)
    {
        if (!IsOnline || silver < 0) return false;
        Wallet.Credit(Player, checked(silver * 10000), "module");
        return true;
    }

    public bool TakeSilver(long silver) => IsOnline && silver >= 0 && Wallet.TrySpend(Player, checked(silver * 10000), "module");

    public bool GiveFame(long fame, int tier = 4)
    {
        if (!IsOnline || fame <= 0) return false;
        Destiny.Award(Player, checked(fame * 10000), Destiny.Kill(Player, Math.Clamp(tier, 1, 8)), journals: false);
        return true;
    }

    public bool Teleport(float x, float y, float facing = 0)
    {
        if (!IsOnline || !float.IsFinite(x) || !float.IsFinite(y)) return false;
        Player.Session.Relocate(new Vector2(x, y), facing);
        return true;
    }

    public bool TeleportTo(string cluster, float? x = null, float? y = null)
    {
        if (!IsOnline || World.Clusters is not { } store || cluster.Contains('@')) return false;
        if (store.WorldIfLoaded is { } info && info.Find(cluster) is null) return false;
        var at = x is { } gx && y is { } gy ? new Vector2(gx, gy) : (Vector2?)null;
        void Go(ClusterTemplate target)
        {
            if (!IsOnline || target.Instances.Count == 0) return;
            if (target.Info.Type.Contains("ISLAND", StringComparison.Ordinal) || target.Info.Type.Contains("EXPEDITION", StringComparison.Ordinal)) return;
            Player.Session.Teleport(new Arrival(target.Id, at ?? Transfers.DefaultSpawn(target), 0), "module");
        }
        if (World.CachedCluster(cluster) is { } cached)
        {
            Go(cached);
            return true;
        }
        if (World.ResolveCluster(cluster) is not { } task) return false;
        task.ContinueWith(t => World.Post(() =>
        {
            if (t.IsCompletedSuccessfully) Go(t.Result);
            else Photon.Log.Warn("modules", $"teleport of '{Name}' to {cluster}: {t.Exception?.InnerException?.Message ?? "not resolved"}");
        }), TaskScheduler.Default);
        return true;
    }

    public bool ApplyBuff(string spell) => IsOnline && Pvp.ApplySpell(Player, spell, World.GameNow);

    public bool RemoveBuff(string spell)
    {
        if (!IsOnline || !SpellEffects.Remove(World, Player, s => s.Spell.Name.Equals(spell, StringComparison.OrdinalIgnoreCase))) return false;
        SpellEffects.Send(World, Player);
        SpellEffects.Refresh(World, Player);
        return true;
    }

    public bool HasBuff(string spell) => Pvp.SpellActive(Player, spell, World.GameNow);

    public float Energy => Player.Energy.At(World.GameNow);
    public float MaxEnergy => Player.Energy.Max;
    public bool IsAlive => IsOnline && Player.Combat.Down is null;
    public bool InCombat => Player.Combat.InCombat;
    public IReadOnlyList<IBuff> Buffs => World.Modules.Game.Buffs(this);
    public T GetData<T>(string key, Func<T> factory) => World.Modules.GetData(Player, key, factory);
    public void SetData<T>(string key, T value) => World.Modules.SetData(Player, key, value);
    public bool RemoveData(string key) => World.Modules.RemoveData(Player, key);
    public IUnitTimer AddTimer(TimeSpan delay, int repeats, Action<IUnitTimer> callback, string? name = null) =>
        World.Modules.AddTimer(Player, () => IsOnline, delay, repeats, callback, name);
    public bool RemoveTimer(string name) => World.Modules.RemoveTimer(Player, name);

    public override string ToString() => $"player '{Name}'";
}

/// <summary>A mob as modules see it (<see cref="IMob"/>), one per <see cref="Mob"/> object.</summary>
internal sealed class MobFacade(Mob m) : IMob
{
    public Mob Mob { get; } = m;
    public long ObjectId => Mob.Id;
    public string Name => Mob.Template.Name;
    public string Cluster => Mob.Map?.Cluster ?? "";
    public ZoneInfo Zone => Mob.World?.Modules.Types.Zone(Mob.Map) ?? ModuleTypes.Nowhere;
    public float X => Mob.Position.X;
    public float Y => Mob.Position.Y;
    public float Health => Mob.Health.At(Mob.GameNow);
    public float MaxHealth => Mob.Health.Max;
    public bool IsPlayer => false;
    public MobType Type => Host.Types.Of(Mob.Template);
    public int Tier => Mob.Template.Tier;
    public MobCategory Category => Type.Category;
    public double Fame => Mob.Template.Fame;
    public bool IsAlive => !Mob.Dead && !Mob.Removed && Mob.Map is not null;
    public bool IsWorldBoss => Mob.Boss is not null;
    public bool IsEventMob => Mob.Event is not null;
    public float Energy => Mob.Energy.At(Mob.GameNow);
    public float MaxEnergy => Mob.Energy.Max;
    public bool InCombat => Mob.TargetId != 0;
    private ModuleHost Host => Mob.World?.Modules ?? throw new InvalidOperationException($"{Mob} belongs to no world");
    public IReadOnlyList<IBuff> Buffs => Mob.World is { } w ? w.Modules.Game.Buffs(this) : [];
    public T GetData<T>(string key, Func<T> factory) => Host.GetData(Mob, key, factory);
    public void SetData<T>(string key, T value) => Host.SetData(Mob, key, value);
    public bool RemoveData(string key) => Host.RemoveData(Mob, key);
    public IUnitTimer AddTimer(TimeSpan delay, int repeats, Action<IUnitTimer> callback, string? name = null) =>
        Host.AddTimer(Mob, () => IsAlive, delay, repeats, callback, name);
    public bool RemoveTimer(string name) => Host.RemoveTimer(Mob, name);

    public IUnit? Target => Mob.TargetId == 0 ? null : Host.WrapOrNull(ModuleHost.Find(Mob.Map, Mob.TargetId));

    public IReadOnlyList<ThreatEntry> Threat =>
        Mob.Map is not { } map ? [] : [.. Mob.Threat.Where(t => MobCasts.Valid(t.Key, map, Mob.GameNow)).OrderByDescending(t => t.Value).Select(t => new ThreatEntry(Host.Wrap(t.Key), t.Value))];

    public SpellInfo? CurrentSpell =>
        ((Mob.AI as BehaviourAI)?.Cast is { Over: false } own ? own : _cast is { Over: false } order ? order : null) is { } cast ? Host.Types.Of(cast.Spell) : null;

    public bool IsMoving => Mob.Motion.Moving;

    public bool Attack(IPlayer target)
    {
        if (Later(() => Attack(target))) return true;
        if (Fightable(target) is not { } p || Mob.AI is not AggressiveAI ai || Mob.World is not { } world || Mob.Map is not { } map || CurrentSpell is not null) return false;
        if (Mob.TargetId != p.Id && !SetTarget(target)) return false;
        return ai.OrderSwing(world, map, p);
    }

    public bool InReach(IUnit unit)
    {
        if (ModuleHost.Unwrap(unit) is not { } u || u.Map != Mob.Map || Mob.World is not { } world) return false;
        var reach = Mob.Template.AttackRange + Mob.Template.CollisionRadius + (u is Mob m ? m.Template.CollisionRadius : world.CollisionRadius);
        return Vector2.Distance(Mob.Position, SpellMovement.PositionOf(u, Mob.GameNow)) <= reach;
    }

    public bool Stop()
    {
        if (Later(() => Stop())) return true;
        if (!IsAlive || !Mob.Motion.Moving) return false;
        Mob.Motion.Stop();
        return true;
    }

    public bool FaceTo(float x, float y)
    {
        if (Later(() => FaceTo(x, y))) return true;
        if (!IsAlive || !float.IsFinite(x) || !float.IsFinite(y)) return false;
        Mob.Motion.Stop(MotionMaster.Facing(Mob.Position, new Vector2(x, y)));
        return true;
    }

    /// <summary>Inside a damage, heal or mob AI hook an action waits for the next tick, so that it never runs into the middle of what the hook is about.</summary>
    private bool Later(Action action)
    {
        if (!Host.InCombatHook) return false;
        var host = Host;
        var owner = ModuleHost.Current;
        Mob.World!.Post(() =>
        {
            if (owner is { Dropped: false }) host.Guard(owner, "a mob's action put off to the next tick", action);
        });
        return true;
    }

    /// <summary>The player when the mob can fight it: both alive and on the same map.</summary>
    private Player? Fightable(IPlayer player) =>
        IsAlive && player is PlayerFacade { Player: { } p } && p.Map == Mob.Map && MobCasts.Valid(p, p.Map!, Mob.GameNow) ? p : null;

    public bool SetTarget(IPlayer player)
    {
        if (Later(() => SetTarget(player))) return true;
        if (Fightable(player) is not { } p) return false;
        // The list's highest, so that Target() of the AI keeps it: it changes only to more than the current one's threat × a factor of at least 1.
        Mob.AI.AddThreat(p, Mob.Threat.Count == 0 ? 0 : Mob.Threat.Values.Max() - Mob.Threat.GetValueOrDefault(p));
        if (Mob.AI is AggressiveAI && !Mob.Threat.ContainsKey(p)) return false;
        Mob.TargetId = p.Id;
        return true;
    }

    public bool AddThreat(IPlayer player, double threat)
    {
        if (Later(() => AddThreat(player, threat))) return true;
        if (!double.IsFinite(threat) || Fightable(player) is not { } p) return false;
        Mob.AI.AddThreat(p, threat);
        return Mob.AI is not AggressiveAI || Mob.Threat.ContainsKey(p);
    }

    /// <summary>The cast of a module on a mob without an &lt;AI&gt; rotation, which has no cast of its own: one at a time.</summary>
    private MobCast? _cast;

    /// <summary>This mob without a rotation has cast for a module: its areas go at its death or reset as a rotation's do.</summary>
    private bool _castAreas;

    /// <summary>Its death or reset: what the AI of a mob with a rotation does for its own cast.</summary>
    internal void EndOwnCast(bool death)
    {
        if (_cast is { Over: false } cast) MobCasts.Cancel(cast, interrupted: true);
        _cast = null;
        if (_castAreas) MobCasts.OwnerGone(Mob, death);
    }

    public bool CastSpell(string spell, IUnit? target = null, float? x = null, float? y = null, double? castSeconds = null)
    {
        if (Later(() =>
            {
                if (!CastSpell(spell, target, x, y, castSeconds)) Photon.Log.Warn("modules", $"{Mob}: the cast of {spell} put off from a hook could not start");
            })) return true;
        if (!IsAlive || Mob.World is not { MobSpells: true, Spells: { } spells } world || Mob.Map is not { } map) return false;
        if (spells.Template(spell) is not { Kind: "activespell" or "togglespell" } t) return false;
        var ai = Mob.AI as BehaviourAI;
        if (ai?.Cast is not null || _cast is { Over: false }) return false;
        var now = world.GameNow;
        if (SpellEffects.Stunned(Mob, now) || SpellMovement.Moving(Mob, now) || !t.CastableWhenSilenced && SpellEffects.Silenced(Mob, now)) return false;
        var ap = Mob.Template.AbilityPower;
        var cost = t.EnergyUsage <= 0 ? 0 : t.EnergyUsageIsRelative ? (int)Math.Round(t.EnergyUsage * Mob.Energy.Max) : Combat.MobEnergyCost(t.EnergyUsage, ap);
        if (Mob.Energy.At(now) < cost) return false;

        // The target and the aim, by the rules of a <Spell> node (BehaviourAI.InRange).
        if (target is not null && ModuleHost.Unwrap(target)?.Map != map) return false;
        var unit = ModuleHost.Unwrap(target) ?? ModuleHost.Find(map, Mob.TargetId);
        var here = Mob.Position;
        var ground = t.Target == "ground";
        var self = t.Target is "self" or "all";
        if (self) unit = Mob;
        if (unit is Player q && !MobCasts.Valid(q, map, now) || unit is Mob { Dead: true }) return false;
        var aim = ground && x is { } gx && y is { } gy ? new Vector2(gx, gy) : unit is null ? here : SpellMovement.PositionOf(unit, now);
        if (!float.IsFinite(aim.X) || !float.IsFinite(aim.Y)) return false;
        if (ground)
        {
            var d = Vector2.Distance(here, aim);
            if (d > t.CastRange && !t.AdjustGroundTargetToRange) return false;
            if (d > t.CastRange && d > 0.001f) aim = here + (aim - here) * (t.CastRange / d);
        }
        else if (!self)
        {
            if (unit is null) return false;
            if (unit is Player && !MobNav.Sees(world, map, here, aim)) return false;
            var slack = Mob.Template.CollisionRadius + (unit is Player ? world.CollisionRadius : unit is Mob m && m != Mob ? m.Template.CollisionRadius : 0);
            if (unit != Mob && Vector2.Distance(here, aim) > t.CastRange + slack) return false;
        }

        if (t.Effects.Count == 0 && t.CastingTime <= 0 && !t.HasChannel)
        {
            Mob.NextCastCounter(); // as the node: nothing to show, only a counter (MOB_SPEAK)
            return true;
        }
        var cast = new MobCast
        {
            Caster = Mob, Spell = t, Counter = Mob.NextCastCounter(), Target = ground ? null : unit, Point = ground ? aim : here,
            Start = now, End = now + SpellCasts.Seconds(castSeconds is >= 0 ? castSeconds.Value : t.CastingTime), Cost = cost, Ap = ap, Owner = ai,
        };
        if (ai is not null) ai.Adopt(cast, world);
        else (_cast, _castAreas) = (cast, true);
        var turn = unit != Mob && Vector2.DistanceSquared(aim, here) > 1e-4f;
        if (cast.End > cast.Start && Mob.Motion.Moving) Mob.Motion.Stop(turn ? MotionMaster.Facing(here, aim) : null);
        else if (turn && !Mob.Motion.Moving) Mob.Motion.Stop(MotionMaster.Facing(here, aim));
        MobCasts.Start(world, map, cast);
        return true;
    }

    public bool MoveTo(float x, float y)
    {
        if (Later(() => MoveTo(x, y))) return true;
        if (!IsAlive || !float.IsFinite(x) || !float.IsFinite(y) || Mob.World is not { } world || Mob.Map is not { } map) return false;
        var t = Mob.Template;
        var speed = Mob.AI.State == MobState.Idle && t.MoveSpeed > 0 ? t.MoveSpeed : t.AttackMoveSpeed;
        if (speed <= 0) return false;
        var to = new Vector2(x, y);
        if (MobNav.Grid(world, map) is { } grid)
        {
            if (MobNav.Path(world, grid, Mob.Position, to, partial: false, out _) is not { } path) return false; // no way there
            if (path.Count > 2) Mob.Motion.MoveAlong(path, speed);
            else Mob.Motion.MoveTo(to, speed);
        }
        else Mob.Motion.MoveTo(to, speed);
        return Mob.Motion.Moving;
    }

    public bool Despawn(bool respawn = true)
    {
        if (Later(() => Despawn(respawn))) return true;
        if (Mob.Removed || Mob.Map is null || Mob.World is not { } world) return false;
        SpawnManager.Despawn(Mob, world, respawn);
        return true;
    }

    public override string ToString() => Mob.ToString();
}

/// <summary>The game for modules (<see cref="IServer"/>), one per host.</summary>
internal sealed class ServerFacade(World world, ModuleHost host) : IServer
{
    public IReadOnlyList<IPlayer> OnlinePlayers => [.. CommandRegistry.Players(world).Select(host.Wrap)];

    public IPlayer? FindPlayer(string name) =>
        CommandRegistry.Players(world).FirstOrDefault(p => p.Character.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } p ? host.Wrap(p) : null;

    public IReadOnlyList<string> LoadedClusters => [.. world.Maps.Maps.Select(m => m.Cluster)];
    public IReadOnlyList<string> PopulatedClusters => [.. world.Maps.Maps.Where(m => m.Players.Count > 0).Select(m => m.Cluster)];

    public IReadOnlyList<IMob> Mobs(string cluster) =>
        world.Maps.Find(cluster) is { } map ? [.. map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && !m.Removed).Select(host.Wrap)] : [];

    public DateTime UtcNow => DateTime.UtcNow;
    public GamePreset Preset => world.Config.Preset == ServerPreset.Pve ? GamePreset.Pve : GamePreset.Official;

    public void Broadcast(string text)
    {
        if (text.Length == 0) return;
        Photon.Log.Info("modules", $"broadcast: {text}");
        foreach (var p in world.Chat.Announce(text, world.Maps.Maps.SelectMany(m => m.Players.Values).ToList())) Expeditions.Tell(p, text);
    }

    public void SendToZone(string cluster, string text)
    {
        if (text.Length == 0 || world.Maps.Find(cluster) is not { } map) return;
        foreach (var p in map.Players.Values.ToList()) Expeditions.Tell(p, text);
    }

    public bool ItemExists(string item) => world.Items.Find(item) is { CanCreate: true };

    public ItemType? Item(string name) => host.Types.Find(name);

    public ItemType? Item(string family, int tier, int enchantment = 0) =>
        host.Types.Find(enchantment > 0 ? $"T{tier}_{family}@{enchantment}" : $"T{tier}_{family}");

    public IReadOnlyList<ItemType> Items => host.Types.AllItems;

    public SpellInfo? Spell(string name) => host.Types.FindSpell(name);

    public MobType? Mob(string name) => host.Types.FindMob(name);

    public ZoneInfo? Zone(string cluster) => host.Types.FindZone(cluster);

    public IReadOnlyList<string> EventKinds => [.. world.EventSettings.Events.Select(e => e.Name)];

    private (Map? Map, MobTemplate? Mob, string? Why) Find(string cluster, string mob)
    {
        if (world.Maps.Find(cluster) is not { Grid: not null } map) return (null, null, $"cluster {cluster} is not loaded");
        if (world.Clusters?.MobsIfLoaded is not { } mobs) return (null, null, "mobs.xml is not loaded yet");
        return mobs.Find(mob) is { } t ? (map, t, null) : (null, null, $"no mob {mob} in mobs.xml");
    }

    public MobType? DefineMob(string name, string basedOn, Action<MobDefinition>? setup = null) => host.DefineMob(name, basedOn, setup);

    public IMob? SpawnMob(string cluster, string mob, float x, float y, float facing = 0)
    {
        if (host.RefusedInCombatHook(mob)) return null;
        var (map, template, why) = Find(cluster, mob);
        if (why is not null)
        {
            Photon.Log.Warn("modules", $"spawn {mob} in {cluster}: {why}");
            return null;
        }
        return host.Wrap(SpawnManager.SpawnAt(map!, template!, new Vector2(x, y), facing, world));
    }

    public IMob? SpawnBoss(string cluster, string mob, float x, float y, float facing = 0)
    {
        if (host.RefusedInCombatHook(mob)) return null;
        var (map, template, why) = Find(cluster, mob);
        try
        {
            if (why is null) return host.Wrap(world.Bosses.SpawnNow(map!, null, world.BossData?.Find(mob), template!, new Vector2(x, y), facing));
        }
        catch (InvalidOperationException e) { why = e.Message; }
        Photon.Log.Warn("modules", $"boss {mob} in {cluster}: {why}");
        return null;
    }

    public string? StartEvent(string kind, string cluster, float x, float y, int tier = 0)
    {
        if (world.Maps.Find(cluster) is not { Grid: not null } map) return $"cluster {cluster} is not loaded";
        if (world.EventSettings.Find(kind) is not { } type) return $"no world event kind '{kind}' (kinds: {string.Join(", ", EventKinds)})";
        if (tier is < 0 or > 10) return "tier must be 0-10";
        try
        {
            var run = world.Events.Start(map, type, new Vector2(x, y), tier: tier);
            Photon.Log.Info("modules", string.Create(CultureInfo.InvariantCulture, $"started {run} in {cluster} at ({x:0.#}, {y:0.#})"));
            return null;
        }
        catch (InvalidOperationException e) { return e.Message; }
    }
}
