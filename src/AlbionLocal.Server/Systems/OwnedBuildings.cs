using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;
using AlbionLocal.Server.Persistence;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The owner's side of a player's station (analysis/work/stageR/buildings/SPEC.md §2-§3): today the crafting stations on the city plots
/// (<see cref="RealEstates"/>). Per building a <see cref="BuildingState"/>: the usage fees the owner sets per role (CraftBuildingChangeSettings
/// 47), the fees collected (CraftBuildingTakeMoney 48), the nutrition each craft eats and food fills (BuildingFillNutrition 60), the silver
/// the owner deposits to reward feeders (65-67) and the access preset (AccessRightsChangeSettings 198). The ownerless city stations have no
/// state and work as before. Touched only under <see cref="World.Sync"/>; every change is saved at once.
/// </summary>
public sealed class OwnedBuildings
{
    private readonly World world;
    private readonly IBuildingStateStore? store;
    private readonly Dictionary<Guid, BuildingState> _states = [];

    /// <summary>gamedata BuildingManagement maxuseagefee (1000): the highest fee per 100 nutrition, a7m.</summary>
    public long MaxFee => (long)Math.Round(world.Economy.BuildingMaxFee * Pricing.A7m);

    /// <summary>gamedata BuildingManagement maxnutritionreward (100000): the highest reward per 100 nutrition, a7m [infer: the fee's unit].</summary>
    public long MaxReward => (long)Math.Round(world.Economy.BuildingMaxReward * Pricing.A7m);

    public OwnedBuildings(World world, IBuildingStateStore? store)
    {
        this.world = world;
        this.store = store;
        if (store is null) return;
        foreach (var s in store.LoadBuildingStates()) _states[s.Id] = s;
    }

    /// <summary>
    /// The state of the building <paramref name="id"/> of type <paramref name="template"/>, made on its first use from buildings.xml: nutrition
    /// = initialnutrition × nutritionstorage, the fees defaultcostfactorfriends / defaultcostfactorusers (0 on every station), the preset
    /// accessrightspreset (public on every craftbuilding).
    /// </summary>
    public BuildingState For(Guid id, BuildingTemplate template)
    {
        if (_states.TryGetValue(id, out var s)) return s;
        double Attr(string name, double d) =>
            double.TryParse(template.Attrs.GetValueOrDefault(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        s = new BuildingState(id)
        {
            Nutrition = (long)Math.Round(Attr("initialnutrition", 1) * template.NutritionStorage * Pricing.A7m),
            FriendFee = (long)Math.Round(Attr("defaultcostfactorfriends", 0) * Pricing.A7m),
            UserFee = (long)Math.Round(Attr("defaultcostfactorusers", 0) * Pricing.A7m),
            Preset = template.Attrs.GetValueOrDefault("accessrightspreset") switch { "closed" => 0, "private" => 1, _ => 2 },
        };
        _states[id] = s;
        store?.SaveBuildingState(s);
        return s;
    }

    /// <summary>The building's state comes down with it; returns it (the caller pays out what silver it held).</summary>
    public BuildingState? Remove(Guid id)
    {
        if (!_states.Remove(id, out var s)) return null;
        store?.DeleteBuildingState(id);
        return s;
    }

    public void Save(BuildingState s) => store?.SaveBuildingState(s);

    /// <summary>The nutrition storage of a building in a7m (buildings.xml nutritionstorage; the client's b5j+0xD8).</summary>
    public static long Storage(BuildingTemplate t) => (long)Math.Round(t.NutritionStorage * Pricing.A7m);

    // ---- roles and fees ----

    /// <summary>
    /// The role of <paramref name="p"/> at <paramref name="b"/> (accessrights.xml AccessRightsCraftBuilding): the owner "owner"; a member of the
    /// owner's guild "friend" under the private and public presets; anyone else "user" under the public preset; else "noaccess".
    /// </summary>
    public string Role(Building b, Player p)
    {
        if (b.Owner is not { } owner) return "user";
        if (owner.Id == p.Character.Id) return "owner";
        var preset = b.State?.Preset ?? 2;
        if (owner.Guild != Guid.Empty && world.Guilds.Of(p.Character.Id)?.Id == owner.Guild && preset >= 1) return "friend";
        return preset == 2 ? "user" : "noaccess";
    }

    /// <summary>The fee per 100 nutrition <paramref name="p"/> pays at <paramref name="b"/> (cox.ab8 0x180BA6B40: owner and coowner 0, friend key 1, user key 2).</summary>
    public long FeeOf(Building b, Player p) => b.State is not { } s ? 0 : Role(b, p) switch { "friend" => s.FriendFee, "user" => s.UserFee, _ => 0 };

    /// <summary>
    /// The nutrition one craft of <paramref name="item"/> eats (bgf.abs 0x1805CE5E0): 0 up to FreeCrafting maxTier (2), else the item value (a7m)
    /// × (ItemValueToNutrition × BuildingConsumptionBalanceFactor), a7m-multiplied (truncated).
    /// </summary>
    public long NutritionPerCraft(ItemTemplate item)
    {
        if (item.Tier <= world.Economy.FreeCraftingMaxTier) return 0;
        var value = (long)Math.Round(world.ItemValues.Of(item) * Pricing.A7m);
        var factor = (long)Math.Round(world.Economy.ItemValueToNutrition * world.Economy.BuildingBalance * Pricing.A7m);
        return value * factor / Pricing.A7m;
    }

    /// <summary>The fee for <paramref name="nutrition"/> eaten at <paramref name="feePer100"/> (bgf.abo 0x1805CA000: a7m(n × fee) / a7m 100, both truncated).</summary>
    public static long Fee(long nutrition, long feePer100) => nutrition * feePer100 / Pricing.A7m * Pricing.A7m / (100 * Pricing.A7m);

    /// <summary>
    /// Whether <paramref name="b"/> has the nutrition for a use that eats <paramref name="need"/> (cou.aw: the nutrition left must stay above
    /// nutritionstorage × CraftNutrition minimumpercent). A building without state (an NPC station) always has.
    /// </summary>
    public bool HasNutrition(Building b, long need) =>
        b.State is not { } s || need <= 0 || s.Nutrition - need > (long)(Storage(b.Template) * world.Economy.CraftNutritionMinimum);

    /// <summary>
    /// A use took <paramref name="nutrition"/> and paid <paramref name="fee"/> (negative: a cancel gave them back): kept, saved, shown;
    /// <paramref name="inUse"/> keeps NewBuilding key 16 of the action that just started.
    /// </summary>
    public void Used(Building b, long nutrition, long fee, bool inUse = false)
    {
        if (b.State is not { } s || nutrition == 0 && fee == 0) return;
        s.Nutrition = Math.Clamp(s.Nutrition - nutrition, 0, Storage(b.Template));
        s.Earned = Math.Max(0, s.Earned + fee);
        Save(s);
        Changed(b, inUse);
    }

    /// <summary>Shows the building's new state to everyone who sees it: NewBuilding (nutrition, rewards) and CraftBuildingInfo (fees, earnings).</summary>
    public void Changed(Building b, bool inUse = false)
    {
        if (b.Map is not { } map) return;
        map.SendToObservers(b, b.NewBuilding(inUse));
        map.SendToObservers(b, Info(b));
    }

    /// <summary>
    /// CraftBuildingInfo (49, amh) of an owned station as the client's cox.ajv (0x180BA7950) reads it: 0 the building, 1 the friends' fee
    /// (cox+0x208), 2 the users' fee (+0x210), 3 a stamp (+0x218, its use not traced: the building's creation), 4 the earned silver (+0x220,
    /// BuildingAdministrationGui's "earned"), 5 the renovation silver (cos+0x1B0; 0, no renovation), 6/7 the renovation resources (empty),
    /// 8/9 the renovation's start and end (left out), 10 cox+0x228 (adz(); false) [infer: the meaning of 3 and 10].
    /// </summary>
    public CraftBuildingInfoEvent Info(Building b)
    {
        var s = b.State;
        return new CraftBuildingInfoEvent
        {
            ddn = b.Id, b00 = s?.FriendFee ?? 0, b01 = s?.UserFee ?? 0, b02 = b.DurabilityStamp, b03 = s?.Earned ?? 0, b05 = [], b06 = [],
        };
    }

    // ---- the owner's requests ----

    /// <summary>The owned building <paramref name="id"/> on <paramref name="p"/>'s map with its state, or why not.</summary>
    public (Building? Building, string Why) Find(Player p, long id)
    {
        if (p.Map?.Objects.GetValueOrDefault(id) is not Building b) return (null, $"object {id} is no building");
        if (b.State is null) return (null, $"the {b.Template.Name} has no owner");
        return (b, "");
    }

    private (Building? Building, string Why) Owned(Player p, long id)
    {
        var (b, why) = Find(p, id);
        if (b is null) return (null, why);
        return Role(b, p) == "owner" ? (b, "") : (null, $"the {b.Template.Name} is not yours");
    }

    /// <summary>CraftBuildingChangeSettings (47 {0 building, 1 friends' fee, 2 users' fee}, a7m per 100 nutrition; right CHANGEUSAGEFEES = owner).</summary>
    public string? ChangeFees(Player p, long id, long friendFee, long userFee)
    {
        var (b, why) = Owned(p, id);
        if (b is null) return why;
        if (friendFee < 0 || userFee < 0 || friendFee > MaxFee || userFee > MaxFee) return $"a fee must be 0 to {MaxFee / Pricing.A7m} silver per 100 nutrition";
        var s = b.State!;
        (s.FriendFee, s.UserFee) = (friendFee, userFee);
        Save(s);
        Changed(b);
        Log.Info("world", string.Create(CultureInfo.InvariantCulture,
            $"'{p.Character.Name}' sets the fees of {b.Template.Name} {s.Id:N}: friends {friendFee / (double)Pricing.A7m:0.####}, users {userFee / (double)Pricing.A7m:0.####} per 100 nutrition"));
        return null;
    }

    /// <summary>CraftBuildingTakeMoney (48 {0 building}; right PICKUPMONEY = owner): the earned fees into the owner's wallet.</summary>
    public string? TakeMoney(Player p, long id)
    {
        var (b, why) = Owned(p, id);
        if (b is null) return why;
        var s = b.State!;
        if (s.Earned <= 0) return "nothing earned to take";
        var earned = s.Earned;
        s.Earned = 0;
        Save(s);
        Wallet.Credit(p, earned, $"fees of {b.Template.Name}");
        world.Saves.SaveNow(p.Character);
        Changed(b);
        return null;
    }

    /// <summary>BuildingNutritionSilverStorageDeposit (65 {0 building, 1 a7m}): the owner's silver into the feeding reward pool.</summary>
    public string? Deposit(Player p, long id, long silver)
    {
        var (b, why) = Owned(p, id);
        if (b is null) return why;
        if (silver <= 0) return "nothing to deposit";
        if (!Wallet.TrySpend(p, silver, $"feeding reward of {b.Template.Name}")) return "not enough silver";
        b.State!.RewardStorage += silver;
        Save(b.State);
        world.Saves.SaveNow(p.Character);
        Changed(b);
        return null;
    }

    /// <summary>BuildingNutritionSilverStorageWithdraw (66 {0 building, 1 a7m}): back from the pool into the owner's wallet.</summary>
    public string? Withdraw(Player p, long id, long silver)
    {
        var (b, why) = Owned(p, id);
        if (b is null) return why;
        var s = b.State!;
        if (silver <= 0 || silver > s.RewardStorage) return $"the pool holds {s.RewardStorage / Pricing.A7m} silver";
        s.RewardStorage -= silver;
        Save(s);
        Wallet.Credit(p, silver, $"feeding reward of {b.Template.Name} withdrawn");
        world.Saves.SaveNow(p.Character);
        Changed(b);
        return null;
    }

    /// <summary>BuildingNutritionSilverRewardSet (67 {0 building, 1 a7m per nutrition point}; cou.aqw 0x180BA22C0 sends it only for ≥ 0).</summary>
    public string? SetReward(Player p, long id, long perNutrition)
    {
        var (b, why) = Owned(p, id);
        if (b is null) return why;
        if (perNutrition < 0 || perNutrition * 100 > MaxReward) return $"the reward must be 0 to {MaxReward / Pricing.A7m} silver per 100 nutrition";
        b.State!.RewardPerNutrition = perNutrition;
        Save(b.State);
        Changed(b);
        return null;
    }

    /// <summary>
    /// The nutrition one <paramref name="food"/> gives <paramref name="b"/> (BuildingFeedView.GetNutritionAddedFromItem 0x1813181A0): the item's
    /// nutrition (items.xml nutrition; bdi+0xC0), × (1 + the dish's bonus) when it is the building's favourite dish. 0 = no food.
    /// </summary>
    public long NutritionOf(Building b, ItemTemplate food)
    {
        if (food.Kind == "farmableitem" || !double.TryParse(food.Attrs.GetValueOrDefault("nutrition"), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return 0;
        var raw = (long)Math.Round(n * Pricing.A7m);
        if (FavouriteDish(b.Template) is { } dish && string.Equals(dish.Item, food.Name, StringComparison.OrdinalIgnoreCase))
            raw += (long)Math.Round(raw * dish.Bonus);
        return raw;
    }

    /// <summary>
    /// The building's favourite dish: the first &lt;favoritedish&gt;&lt;dish item bonus weight&gt; of buildings.xml [assume: the official server
    /// picks one by weight for each period; every craftbuilding of this build lists one dish].
    /// </summary>
    public static (string Item, double Bonus)? FavouriteDish(BuildingTemplate t)
    {
        foreach (var d in t.Element.Element("favoritedish")?.Elements("dish") ?? [])
            if ((string?)d.Attribute("item") is { Length: > 0 } item)
                return (item, double.TryParse((string?)d.Attribute("bonus"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0);
        return null;
    }

    /// <summary>
    /// The checks of BuildingFillNutrition (60 {0 building, 1 the food stack, 2 count}): an owned building (right INSTALLRESOURCES: everyone);
    /// the stack the player's, food, the count on it; the storage not full. The count is cut to what fills it (the client's
    /// GetMaxOfItemToGetNutritionFilled, rounded up). Returns the count to take and the nutrition it gives.
    /// </summary>
    public (Building? Building, Item? Stack, int Count, long Nutrition, string Why) CheckFill(Player p, long id, long stackId, int count)
    {
        var (b, why) = Find(p, id);
        if (b is null) return (null, null, 0, 0, why);
        if (p.Inventory.Bag.Find(stackId) is not { } stack) return (null, null, 0, 0, "the food is not in the inventory");
        var per = NutritionOf(b, stack.Template);
        if (per <= 0) return (null, null, 0, 0, $"{stack.Template.Name} cannot be fed (no nutrition)");
        if (count <= 0 || count > stack.Count) return (null, null, 0, 0, $"count {count} of a stack of {stack.Count}");
        var room = Storage(b.Template) - b.State!.Nutrition;
        if (room <= 0) return (null, null, 0, 0, "the food stockpile is full");
        count = (int)Math.Min(count, (room + per - 1) / per);
        return (b, stack, count, Math.Min(room, per * count), "");
    }

    /// <summary>The food went in: the nutrition rises (up to the storage); a feeder other than the owner gets the reward from the pool.</summary>
    public long Fill(Player p, Building b, long nutrition)
    {
        var s = b.State!;
        s.Nutrition = Math.Min(Storage(b.Template), s.Nutrition + nutrition);
        long reward = 0;
        if (Role(b, p) != "owner" && s.RewardPerNutrition > 0)
        {
            reward = Math.Min(s.RewardStorage, nutrition * s.RewardPerNutrition / Pricing.A7m);
            s.RewardStorage -= reward;
            if (reward > 0) Wallet.Credit(p, reward, $"feeding {b.Template.Name}");
        }
        Save(s);
        Changed(b);
        return reward;
    }

    // ---- access ----

    /// <summary>
    /// AccessStatus (209) of an owned station: key 1 its preset, the groups @_Owner/@_Guild/@_Everyone with owner, then friend (private and
    /// public) or noaccess, then user (public) or noaccess, the owner's guild name in key 6.
    /// </summary>
    public AccessStatusEvent AccessStatus(Building b)
    {
        var preset = b.State?.Preset ?? 2;
        return new AccessStatusEvent
        {
            ddn = b.Id, bq5 = preset, bq8 = Building.AccessGroups,
            bq9 = ["owner", preset >= 1 ? "friend" : "noaccess", preset == 2 ? "user" : "noaccess"],
            bra = ["", b.Owner?.GuildName ?? "", ""],
        };
    }

    /// <summary>
    /// AccessRightsChangeSettings (198) on an owned station: its preset (0 closed, 1 private, 2 public; 3 custom is read from the @_Everyone and
    /// @_Guild roles), by its owner. Null = changed; <paramref name="mine"/> false when the object is no owned station.
    /// </summary>
    public string? SetAccess(Player p, long objectId, int preset, string[]? groups, string[]? roles, out bool mine)
    {
        mine = p.Map?.Objects.GetValueOrDefault(objectId) is Building { State: not null };
        if (!mine) return "no owned building";
        var (b, why) = Owned(p, objectId);
        if (b is null) return why;
        if (preset is < 0 or > 2)
        {
            string Role(string group) => groups is null || roles is null ? "noaccess" : Array.IndexOf(groups, group) is >= 0 and var i && i < roles.Length ? roles[i] : "noaccess";
            preset = Role("@_Everyone") != "noaccess" ? 2 : Role("@_Guild") != "noaccess" ? 1 : 0;
        }
        b.State!.Preset = preset;
        Save(b.State);
        b.Map!.SendToObservers(b, AccessStatus(b));
        Log.Info("world", $"'{p.Character.Name}' sets the access of {b.Template.Name} {b.State.Id:N} to preset {preset}");
        return null;
    }
}
