using System.Globalization;
using System.Numerics;
using AlbionLocal.Plugin.Api;
using AlbionLocal.Server.Commands;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using IUnit = AlbionLocal.Plugin.Api.IUnit;
using AlbionLocal.Server.Network;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Plugins;

/// <summary>
/// A joined player as plugins see it (<see cref="IPlayer"/>): reads the live state, acts through the systems the GM commands use
/// (<see cref="Wallet"/>, <see cref="WorldSession.GiveItem"/>, <see cref="Destiny.Award"/>, <see cref="Pvp.ApplySpell"/>, …). One per
/// <see cref="Player"/> object (<see cref="PluginHost.Wrap(Player)"/>). The actions do nothing once the player has left.
/// </summary>
internal sealed class PlayerFacade(Player p) : IPlayer
{
    public Player Player { get; } = p;
    private World World => Player.World;

    public long ObjectId => Player.Id;
    public string Name => Player.Character.Name;
    public string Cluster => Player.Map?.Cluster ?? "";
    private Vector2 At => Player.PositionAt(DateTime.UtcNow);
    public float X => At.X;
    public float Y => At.Y;
    public float Health => Player.Health.At(World.GameNow);
    public float MaxHealth => Player.Health.Max;
    public bool IsPlayer => true;
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
    public IReadOnlyList<ItemInfo> Equipment => [.. Player.Inventory.Equipment.Items.Select(Info)];
    public IReadOnlyList<ItemInfo> Inventory => [.. Player.Inventory.Bag.Items.Select(Info)];

    private static ItemInfo Info(Item i) => new(i.Template.Name, i.Count, i.Quality);

    public int CountItem(string item) => World.Items.Find(item) is { } type ? WorldSession.CountOf(Player.Inventory.Bag, type) : 0;

    public bool SendMessage(string text, bool error = false)
    {
        if (!IsOnline) return false;
        foreach (var line in text.Split('\n'))
            Player.Session.Send(new SystemMessageEvent { d0p = line.StartsWith('@') ? " " + line : line, d0q = (byte)(error ? 1 : 0) });
        return true;
    }

    public int GiveItem(string item, int count = 1, int quality = 1)
    {
        if (!IsOnline || count <= 0 || World.Items.Find(item) is not { CanCreate: true } type) return 0;
        quality = type.IsEquipment ? Math.Clamp(quality, 1, Math.Max(1, type.MaxQuality)) : 1;
        var given = 0;
        if (type.IsEquipment)
            for (; given < count && WorldSession.Fits(Player.Inventory.Bag, [(type, 1)]); given++) Player.Session.GiveItem(Player, type, 1, quality);
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
        Wallet.Credit(Player, checked(silver * 10000), "plugin");
        return true;
    }

    public bool TakeSilver(long silver) => IsOnline && silver >= 0 && Wallet.TrySpend(Player, checked(silver * 10000), "plugin");

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
            Player.Session.Teleport(new Arrival(target.Id, at ?? Transfers.DefaultSpawn(target), 0), "plugin");
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
            else Photon.Log.Warn("plugins", $"teleport of '{Name}' to {cluster}: {t.Exception?.InnerException?.Message ?? "not resolved"}");
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

    public string? GetSlotSpell(SpellSlot slot) => World.Spells?.Name(SlotSpells.Current(Player, slot));

    public IReadOnlyList<string> GetSlotSpellOptions(SpellSlot slot) =>
        World.Spells is { } spells ? [.. SlotSpells.Options(Player, slot).Select(id => spells.Name(id)).OfType<string>()] : [];

    public string? SetSlotSpell(SpellSlot slot, string spell, SlotSpellMode mode = SlotSpellMode.FromItemList, TimeSpan? duration = null,
        SlotSpellCooldown cooldown = SlotSpellCooldown.Keep) =>
        IsOnline ? SlotSpells.Set(Player, slot, spell, mode, duration, cooldown) : "the player is not online";

    public IReadOnlyList<string?> GetExtraSlots() => World.ExtraBars.Names(Player);

    public string? SetExtraSlot(int index, string? spell) =>
        IsOnline ? World.ExtraBars.SetOverride(Player, index, spell, ExtraBarChangeSource.Plugin) : "the player is not online";

    public float Energy => Player.Energy.At(World.GameNow);
    public float MaxEnergy => Player.Energy.Max;
    public bool IsAlive => IsOnline && Player.Combat.Down is null;
    public bool InCombat => Player.Combat.InCombat;
    public IReadOnlyList<IBuff> Buffs => World.Plugins.Game.Buffs(this);
    public T GetData<T>(string key, Func<T> factory) => World.Plugins.GetData(Player, key, factory);
    public void SetData<T>(string key, T value) => World.Plugins.SetData(Player, key, value);
    public bool RemoveData(string key) => World.Plugins.RemoveData(Player, key);
    public IUnitTimer AddTimer(TimeSpan delay, int repeats, Action<IUnitTimer> callback, string? name = null) =>
        World.Plugins.AddTimer(Player, () => IsOnline, delay, repeats, callback, name);
    public bool RemoveTimer(string name) => World.Plugins.RemoveTimer(Player, name);

    public override string ToString() => $"player '{Name}'";
}

/// <summary>A mob as plugins see it (<see cref="IMob"/>), one per <see cref="Mob"/> object.</summary>
internal sealed class MobFacade(Mob m) : IMob
{
    public Mob Mob { get; } = m;
    public long ObjectId => Mob.Id;
    public string Name => Mob.Template.Name;
    public string Cluster => Mob.Map?.Cluster ?? "";
    public float X => Mob.Position.X;
    public float Y => Mob.Position.Y;
    public float Health => Mob.Health.At(Mob.GameNow);
    public float MaxHealth => Mob.Health.Max;
    public bool IsPlayer => false;
    public int Tier => Mob.Template.Tier;
    public string Category => Mob.Template.Category ?? "";
    public double Fame => Mob.Template.Fame;
    public bool IsAlive => !Mob.Dead && !Mob.Removed && Mob.Map is not null;
    public bool IsWorldBoss => Mob.Boss is not null;
    public bool IsEventMob => Mob.Event is not null;
    public float Energy => Mob.Energy.At(Mob.GameNow);
    public float MaxEnergy => Mob.Energy.Max;
    public bool InCombat => Mob.TargetId != 0;
    private PluginHost Host => Mob.World?.Plugins ?? throw new InvalidOperationException($"{Mob} belongs to no world");
    public IReadOnlyList<IBuff> Buffs => Mob.World is { } w ? w.Plugins.Game.Buffs(this) : [];
    public T GetData<T>(string key, Func<T> factory) => Host.GetData(Mob, key, factory);
    public void SetData<T>(string key, T value) => Host.SetData(Mob, key, value);
    public bool RemoveData(string key) => Host.RemoveData(Mob, key);
    public IUnitTimer AddTimer(TimeSpan delay, int repeats, Action<IUnitTimer> callback, string? name = null) =>
        Host.AddTimer(Mob, () => IsAlive, delay, repeats, callback, name);
    public bool RemoveTimer(string name) => Host.RemoveTimer(Mob, name);
    public override string ToString() => Mob.ToString();
}

/// <summary>The game for plugins (<see cref="IServer"/>), one per host.</summary>
internal sealed class ServerFacade(World world, PluginHost host) : IServer
{
    public IReadOnlyList<IPlayer> OnlinePlayers => [.. CommandRegistry.Players(world).Select(host.Wrap)];

    public IPlayer? FindPlayer(string name) =>
        CommandRegistry.Players(world).FirstOrDefault(p => p.Character.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) is { } p ? host.Wrap(p) : null;

    public IReadOnlyList<string> LoadedClusters => [.. world.Maps.Maps.Select(m => m.Cluster)];
    public IReadOnlyList<string> PopulatedClusters => [.. world.Maps.Maps.Where(m => m.Players.Count > 0).Select(m => m.Cluster)];

    public IReadOnlyList<IMob> Mobs(string cluster) =>
        world.Maps.Find(cluster) is { } map ? [.. map.Objects.Values.OfType<Mob>().Where(m => !m.Dead && !m.Removed).Select(host.Wrap)] : [];

    public DateTime UtcNow => DateTime.UtcNow;
    public string Preset => world.Config.Preset.ToString();

    public void Broadcast(string text)
    {
        if (text.Length == 0) return;
        Photon.Log.Info("plugins", $"broadcast: {text}");
        foreach (var p in world.Chat.Announce(text, world.Maps.Maps.SelectMany(m => m.Players.Values).ToList())) Expeditions.Tell(p, text);
    }

    public void SendToZone(string cluster, string text)
    {
        if (text.Length == 0 || world.Maps.Find(cluster) is not { } map) return;
        foreach (var p in map.Players.Values.ToList()) Expeditions.Tell(p, text);
    }

    public bool ItemExists(string item) => world.Items.Find(item) is { CanCreate: true };

    public IReadOnlyList<string> EventKinds => [.. world.EventSettings.Events.Select(e => e.Name)];

    private (Map? Map, MobTemplate? Mob, string? Why) Find(string cluster, string mob)
    {
        if (world.Maps.Find(cluster) is not { Grid: not null } map) return (null, null, $"cluster {cluster} is not loaded");
        if (world.Clusters?.MobsIfLoaded is not { } mobs) return (null, null, "mobs.xml is not loaded yet");
        return mobs.Find(mob) is { } t ? (map, t, null) : (null, null, $"no mob {mob} in mobs.xml");
    }

    public IMob? SpawnMob(string cluster, string mob, float x, float y, float facing = 0)
    {
        var (map, template, why) = Find(cluster, mob);
        if (why is not null)
        {
            Photon.Log.Warn("plugins", $"spawn {mob} in {cluster}: {why}");
            return null;
        }
        return host.Wrap(SpawnManager.SpawnAt(map!, template!, new Vector2(x, y), facing, world));
    }

    public IMob? SpawnBoss(string cluster, string mob, float x, float y, float facing = 0)
    {
        var (map, template, why) = Find(cluster, mob);
        try
        {
            if (why is null) return host.Wrap(world.Bosses.SpawnNow(map!, null, world.BossData?.Find(mob), template!, new Vector2(x, y), facing));
        }
        catch (InvalidOperationException e) { why = e.Message; }
        Photon.Log.Warn("plugins", $"boss {mob} in {cluster}: {why}");
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
            Photon.Log.Info("plugins", string.Create(CultureInfo.InvariantCulture, $"started {run} in {cluster} at ({x:0.#}, {y:0.#})"));
            return null;
        }
        catch (InvalidOperationException e) { return e.Message; }
    }
}
