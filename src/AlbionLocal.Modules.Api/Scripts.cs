namespace AlbionLocal.Modules;

// The hooks as overridable methods: type `override` in a class deriving from one of these and the IDE lists them. Only what is overridden is
// connected, so an unused hook costs nothing. Written by hand from the hook groups (Hooks.*.cs): a new hook goes into both.
/// <summary>The server's life.</summary>
public abstract class ServerScript : ModuleScript
{
    /// <summary>The server has started (all modules loaded, listeners open). A module loaded later (a reload) does not get it.</summary>
    [Hook("Server.OnStarted")]
    public virtual void OnStarted(ServerArgs e) { }

    /// <summary>The server is stopping: last chance to save. Players are still in game.</summary>
    [Hook("Server.OnStopping")]
    public virtual void OnStopping(ServerArgs e) { }

    /// <summary>Every world tick (50 ms by default). Prefer <see cref="IModuleTimers"/> for anything periodic.</summary>
    [Hook("Server.OnTick")]
    public virtual void OnTick(TickArgs e) { }
}

/// <summary>Players entering and leaving the game and the zones, going down, completing goals. Bots do not fire these.</summary>
public abstract class PlayerScript : ModuleScript
{
    /// <summary>A player logged in: its first Join after the login (not a cluster change), after the client has the world. Messages work here.</summary>
    [Hook("Players.OnLogin")]
    public virtual void OnLogin(PlayerLoginArgs e) { }

    /// <summary>A player's connection ended (logout, disconnect, timeout), not a cluster change. Still readable; messages go nowhere.</summary>
    [Hook("Players.OnLogout")]
    public virtual void OnLogout(PlayerArgs e) { }

    /// <summary>A player arrived in a cluster: every Join (the login's too, with <see cref="ZoneArgs.ClusterChange"/> false), after <see cref="OnLogin"/>. Id = the cluster.</summary>
    [Hook("Players.OnEnterZone")]
    public virtual void OnEnterZone(ZoneArgs e) { }

    /// <summary>A player leaves its cluster (a cluster change or a logout), still on its map. Id = the cluster.</summary>
    [Hook("Players.OnLeaveZone")]
    public virtual void OnLeaveZone(ZoneArgs e) { }

    /// <summary>A player was knocked down (by a mob, a player, a fall to 0 health).</summary>
    [Hook("Players.OnKnockedDown")]
    public virtual void OnKnockedDown(PlayerDownArgs e) { }

    /// <summary>A player died (a lethal kill, the die button, a knockdown that ran out).</summary>
    [Hook("Players.OnDied")]
    public virtual void OnDied(PlayerDownArgs e) { }

    /// <summary>A goal was completed: a bounty, an expedition, a corrupted dungeon. Id = its kind ("bounty", "expedition", "corrupted").</summary>
    [Hook("Players.OnGoalCompleted")]
    public virtual void OnGoalCompleted(GoalArgs e) { }

    /// <summary>A typed command is about to run, whoever's and from wherever (the game's chat or the server console; a module's command too): <see cref="CancellableArgs.Cancel"/> stops it and the reason goes back as its error.</summary>
    [Hook("Players.OnBeforeCommand")]
    public virtual void OnBeforeCommand(BeforeCommandArgs e) { }

    /// <summary>A player gained fame (kills, gathering, crafting, goals, a GM's or a module's grant), after it was added and the client told. A tome's fame, which only feeds the destiny board, is not in it.</summary>
    [Hook("Players.OnFameGained")]
    public virtual void OnFameGained(FameGainedArgs e) { }

    /// <summary>A joined player's silver changed (loot, the market, trades, costs, a GM's or a module's). The silver of market sales made while away comes in before its Join and is not told.</summary>
    [Hook("Players.OnSilverChanged")]
    public virtual void OnSilverChanged(SilverChangedArgs e) { }
}

/// <summary>Every hit and heal, whatever caused it: cancel or change them in OnBeforeDamage and OnBeforeHeal. Limit a method with [ForId("T8_MOB")] to a target mob.</summary>
public abstract class CombatScript : ModuleScript
{
    /// <summary>
    /// A hit is about to land, before anything of it has happened: <see cref="CancellableArgs.Cancel"/> makes it as if it never landed (no
    /// damage, no combat, no threat, invisibility stays; a spell's hit animation still plays), <see cref="BeforeDamageArgs.Amount"/> changes it.
    /// </summary>
    /// <example>
    /// Caps the hits a player deals to one mob:
    /// <code><![CDATA[
    /// public sealed class BossShield : CombatScript
    /// {
    ///     [ForId("T8_MOB")]
    ///     public override void OnBeforeDamage(BeforeDamageArgs e)
    ///     {
    ///         if (e.Amount > 500) e.Amount = 500;
    ///     }
    /// }
    /// ]]></code>
    /// </example>
    [Hook("Combat.OnBeforeDamage")]
    public virtual void OnBeforeDamage(BeforeDamageArgs e) { }

    /// <summary>Health was lost to a hit (damage shields already took their part).</summary>
    [Hook("Combat.OnAfterDamage")]
    public virtual void OnAfterDamage(HealthChangeArgs e) { }

    /// <summary>A damage shield is about to take a hit, once per shield: <see cref="AbsorbArgs.Amount"/> changes what it takes, <see cref="CancellableArgs.Cancel"/> lets the hit through it.</summary>
    [Hook("Combat.OnBeforeAbsorb")]
    public virtual void OnBeforeAbsorb(AbsorbArgs e) { }

    /// <summary>A damage shield took a hit, once per shield: how much, what is left of it, whether it broke. Read only.</summary>
    [Hook("Combat.OnAfterAbsorb")]
    public virtual void OnAfterAbsorb(AbsorbArgs e) { }

    /// <summary>A hit or a spell did not land: <see cref="HitAvoidedArgs.Reason"/> says why, <see cref="HitAvoidedArgs.ProtectedBy"/> which effect stopped it (the DODGE spell, say). Read only.</summary>
    [Hook("Combat.OnHitAvoided")]
    public virtual void OnHitAvoided(HitAvoidedArgs e) { }

    /// <summary>A heal on a player is about to land: <see cref="CancellableArgs.Cancel"/> stops it, <see cref="BeforeHealArgs.Amount"/> changes it.</summary>
    [Hook("Combat.OnBeforeHeal")]
    public virtual void OnBeforeHeal(BeforeHealArgs e) { }

    /// <summary>Health was gained from a heal.</summary>
    [Hook("Combat.OnAfterHeal")]
    public virtual void OnAfterHeal(HealthChangeArgs e) { }
}

/// <summary>Mobs spawning and dying, their loot. Limit a method or the class with [ForId("T8_MOB")] to a mob.</summary>
public abstract class MobScript : ModuleScript
{
    /// <summary>A mob was put on a map (a spawn point's spawn or respawn, a GM's, an event's, a module's). Fires for every mob of a cluster when it loads: keep it cheap.</summary>
    [Hook("Mobs.OnSpawned")]
    public virtual void OnSpawned(MobArgs e) { }

    /// <summary>A mob was killed (after its rewards, loot and the systems' own handling). Despawns (.mob kill) do not count.</summary>
    [Hook("Mobs.OnDied")]
    public virtual void OnDied(MobDeathArgs e) { }

    /// <summary>A mob's loot was rolled, before the bag is made: <see cref="LootArgs.Items"/> may be changed (add, remove).</summary>
    [Hook("Mobs.OnLootRolled")]
    public virtual void OnLootRolled(LootArgs e) { }

    /// <summary>
    /// A mob's AI is about to run its update (each world tick, while players are on its map and the mob AI is on): <see cref="CancellableArgs.Cancel"/> skips the
    /// engine's AI for this update, so the module drives the mob itself (<see cref="IMob.MoveTo"/>, <see cref="IMob.CastSpell"/>). A walk keeps being sent; a
    /// mob left cancelled does not chase, swing, leash or finish its return home. Its spawn spells have run. Without [ForId] this runs for every mob of every populated map.
    /// </summary>
    /// <example>
    /// A boss that casts at half health and otherwise fights as usual:
    /// <code><![CDATA[
    /// [ForId("T8_MOB_BOSS")]
    /// public sealed class Boss : MobScript
    /// {
    ///     private sealed class Phase { public bool Enraged; }
    ///
    ///     public override void OnBeforeThink(BeforeThinkArgs e)
    ///     {
    ///         var phase = e.Mob.Data<Phase>();
    ///         if (!phase.Enraged && e.Mob.Health <= e.Mob.MaxHealth / 2) phase.Enraged = e.Mob.CastSpell("MY_SPELL");
    ///     }
    /// }
    /// ]]></code>
    /// </example>
    [Hook("Mobs.OnBeforeThink")]
    public virtual void OnBeforeThink(BeforeThinkArgs e) { }

    /// <summary>
    /// An idle mob picked a player in its aggro range: <see cref="CancellableArgs.Cancel"/> keeps it from that target for now (it picks again at its next update, so
    /// this comes every tick while it is refused). Damage, threat and a fellow's alert start a fight without asking: <see cref="OnAggro"/> tells of every start.
    /// </summary>
    [Hook("Mobs.OnBeforeAggro")]
    public virtual void OnBeforeAggro(BeforeAggroArgs e) { }

    /// <summary>A mob's fight started: it had no target and has one now (its own pick, damage, threat, a fellow's alert); told at the next tick. Not a change of target during the fight. Not for mobs that do not fight (guards, decorations).</summary>
    [Hook("Mobs.OnAggro")]
    public virtual void OnAggro(MobAggroArgs e) { }

    /// <summary>A mob is about to give up its fight: <see cref="CancellableArgs.Cancel"/> keeps it in the fight, and it is asked again at its next update while the reason holds.</summary>
    [Hook("Mobs.OnBeforeLeaveCombat")]
    public virtual void OnBeforeLeaveCombat(BeforeLeaveCombatArgs e) { }

    /// <summary>A mob's fight ended without its death: it lost its targets or its leash and goes home, or the mob AI went off. Actions on the mob (a cast, a walk, a despawn) wait for the next tick.</summary>
    [Hook("Mobs.OnLeaveCombat")]
    public virtual void OnLeaveCombat(MobLeaveCombatArgs e) { }

    /// <summary>A spawn point's respawn time has come, its mob picked: <see cref="CancellableArgs.Cancel"/> keeps it away; the point tries again after another respawn time. Not the first spawn of a loading map.</summary>
    [Hook("Mobs.OnBeforeRespawn")]
    public virtual void OnBeforeRespawn(BeforeRespawnArgs e) { }
}

/// <summary>The life of a spell. Limit a method or the class with [ForId("MY_SPELL")] to a spell (a mask with * and ? works too).</summary>
public abstract class SpellScript : ModuleScript
{
    /// <summary>A player's cast passed the engine's checks and is about to start: Cancel refuses it. Not fired for mobs or bots.</summary>
    [Hook("Spells.OnBeforeCast")]
    public virtual void OnBeforeCast(BeforeCastArgs e) { }

    /// <summary>A cast went through: energy paid, cooldown started, the hit still to come. Also fired for mobs (the caster is an <see cref="IMob"/>).</summary>
    [Hook("Spells.OnCast")]
    public virtual void OnCast(SpellCastArgs e) { }

    /// <summary>A cast under way was dropped before it finished: see <see cref="SpellCancelArgs.Interrupted"/>. Also fired for mobs. Read only.</summary>
    [Hook("Spells.OnCancel")]
    public virtual void OnCancel(SpellCancelArgs e) { }

    /// <summary>A player's cast's hit lands: <see cref="CancellableSpellArgs.Cancel"/> skips the spells.xml effects (a scripted spell's OnHit still runs).</summary>
    [Hook("Spells.OnBeforeHit")]
    public virtual void OnBeforeHit(SpellHitArgs e) { }

    /// <summary>A cast's hit has been handled and its effects have run (a player's also when OnBeforeHit cancelled them), a mob's too: <see cref="SpellHitArgs.UnitsHit"/> says how many it reached. Read only: Cancel does nothing here.</summary>
    [Hook("Spells.OnAfterHit")]
    public virtual void OnAfterHit(SpellHitArgs e) { }

    /// <summary>A spell is about to act on one unit: <see cref="CancellableArgs.Cancel"/> keeps all of it off that unit. Runs once per unit reached.</summary>
    [Hook("Spells.OnBeforeEffect")]
    public virtual void OnBeforeEffect(SpellEffectArgs e) { }

    /// <summary>One health or energy change of a spell is about to happen, before defences and shields: change Amount, or Cancel to drop it. Also for mobs' spells.</summary>
    [Hook("Spells.OnBeforeDamage")]
    public virtual void OnBeforeDamage(SpellDamageArgs e) { }

    /// <summary>One health or energy change of a spell has happened: Amount is what was lost or gained. Also for mobs' spells. Read only.</summary>
    [Hook("Spells.OnAfterDamage")]
    public virtual void OnAfterDamage(SpellDamageArgs e) { }

    /// <summary>A hit or a cast is about to set off a player's reaction (ENRAGE on a hit taken, a parry's riposte, a passive's proc): <see cref="CancellableArgs.Cancel"/> stops it.</summary>
    [Hook("Spells.OnBeforeTrigger")]
    public virtual void OnBeforeTrigger(SpellTriggerArgs e) { }

    /// <summary>One channel tick of a player's channelled spell. Not fired for bots.</summary>
    [Hook("Spells.OnTick")]
    public virtual void OnTick(SpellTickArgs e) { }

    /// <summary>A spell area (spelleffectarea, a field on the ground) was created. Id = the spell that holds the area node.</summary>
    [Hook("Spells.OnAreaCreate")]
    public virtual void OnAreaCreate(SpellAreaArgs e) { }

    /// <summary>A tick of a spell area, with the units inside. Id = the spell that holds the area node.</summary>
    [Hook("Spells.OnAreaTick")]
    public virtual void OnAreaTick(SpellAreaArgs e) { }
}

/// <summary>The life of a buff on a unit. Limit a method or the class with [ForId("MY_BUFF")].</summary>
public abstract class BuffScript : ModuleScript
{
    /// <summary>Before a spell's timed effects go on a unit (cast spells, area effects, procs): Cancel keeps it off.</summary>
    [Hook("Buffs.OnBeforeApply")]
    public virtual void OnBeforeApply(BeforeBuffArgs e) { }

    /// <summary>The buff went on (new, or re-applied: a stack more).</summary>
    [Hook("Buffs.OnApply")]
    public virtual void OnApply(BuffArgs e) { }

    /// <summary>A buff's stacks changed (re-applied, charges consumed).</summary>
    [Hook("Buffs.OnStackChange")]
    public virtual void OnStackChange(BuffStackArgs e) { }

    /// <summary>A tick of a buff's over-time effect (damage/heal over time, a pulse).</summary>
    [Hook("Buffs.OnTick")]
    public virtual void OnTick(BuffArgs e) { }

    /// <summary>The buff went off: expired, removed (a cleanse, a dispel, a module), or cleared (death, reset, knockdown).</summary>
    [Hook("Buffs.OnRemove")]
    public virtual void OnRemove(BuffRemoveArgs e) { }
}

/// <summary>Equipment, consumables, crafting, gathering. Limit a method or the class with [ForId("T4_MAIN_SWORD")] to an item.</summary>
public abstract class ItemScript : ModuleScript
{
    /// <summary>An item went into an equipment slot (also every worn item at a login: <see cref="EquipArgs.AtLogin"/>).</summary>
    [Hook("Items.OnEquip")]
    public virtual void OnEquip(EquipArgs e) { }

    /// <summary>An item left an equipment slot. <see cref="EquipArgs.Slot"/> is -1 when the item has already gone from the slot.</summary>
    [Hook("Items.OnUnequip")]
    public virtual void OnUnequip(EquipArgs e) { }

    /// <summary>The main-hand weapon changed (id = the new weapon; "" when the hand is empty now).</summary>
    [Hook("Items.OnWeaponSwap")]
    public virtual void OnWeaponSwap(WeaponSwapArgs e) { }

    /// <summary>An item's durability reached 0 (death and knockdown wear, a gathering tool, a fishing rod).</summary>
    [Hook("Items.OnBreak")]
    public virtual void OnBreak(ItemBreakArgs e) { }

    /// <summary>
    /// The worn items' passives are worked out again (the equipment changed): add stat modifiers with <see cref="PassiveArgs.Add"/>
    /// (pick one from <see cref="Stat"/>: <c>Stat.AttackDamage</c>, <c>Stat.MagicSpellDamage</c>, …). A filtered hook runs
    /// when that item is worn.
    /// </summary>
    [Hook("Items.OnCalcPassives")]
    public virtual void OnCalcPassives(PassiveArgs e) { }

    /// <summary>A consumable was used: one use from the inventory (potions, food, chests, maps…) or the potion/food slot. Id = the item.</summary>
    [Hook("Items.OnUsed")]
    public virtual void OnUsed(ItemUsedArgs e) { }

    /// <summary>Items were crafted at a station (or bought at an NPC shop, which is a craft), per batch. Id = the item.</summary>
    [Hook("Items.OnCrafted")]
    public virtual void OnCrafted(ItemCraftedArgs e) { }

    /// <summary>A harvest (gathering or a fishing catch) gave items. Id = the item.</summary>
    [Hook("Items.OnHarvested")]
    public virtual void OnHarvested(HarvestArgs e) { }

    /// <summary>A station action was asked for (craft, repair, salvage, enchant, reroll, awaken), with whether the server accepted it. Id = the station's building.</summary>
    [Hook("Items.OnStationAction")]
    public virtual void OnStationAction(BuildingActionArgs e) { }

    /// <summary>A player is about to take an item out of a loot container (a mob's bag, a dead player's bag, a chest): <see cref="CancellablePlayerArgs.Cancel"/> leaves it there, the reason going to the player. Per item of a take-all. Silver piles are not items. Id = the item.</summary>
    [Hook("Items.OnBeforeLootTake")]
    public virtual void OnBeforeLootTake(BeforeLootTakeArgs e) { }

    /// <summary>A loot chest's loot was rolled at its first opening (per player on a personal chest), before its items are made: <see cref="ChestLootArgs.Items"/> may be changed (add, remove). Id = the chest.</summary>
    [Hook("Items.OnChestLootRolled")]
    public virtual void OnChestLootRolled(ChestLootArgs e) { }
}

/// <summary>Mounting and riding. Limit a method or the class with [ForId("T3_MOUNT_HORSE")] to a mount.</summary>
public abstract class MountScript : ModuleScript
{
    /// <summary>A mount was asked for and passed the engine's checks: Cancel refuses it (the client's mount bar aborts).</summary>
    [Hook("Mounts.OnBeforeMount")]
    public virtual void OnBeforeMount(BeforeMountArgs e) { }

    /// <summary>The mount bar started filling. <see cref="MountArgs.Reason"/> is "mount", or "remount" at the player's mount object.</summary>
    [Hook("Mounts.OnMountStart")]
    public virtual void OnMountStart(MountArgs e) { }

    /// <summary>Mounted (the bar is full). <see cref="MountedArgs.GallopBuff"/> may be changed: the speed buff the mount's gallop applies.</summary>
    [Hook("Mounts.OnMounted")]
    public virtual void OnMounted(MountedArgs e) { }

    /// <summary>The mount bar stopped before it was full. <see cref="MountArgs.Reason"/> is a short text for logs ("damage", "logout", "the player walks", …).</summary>
    [Hook("Mounts.OnMountCancel")]
    public virtual void OnMountCancel(MountArgs e) { }

    /// <summary>Off the mount: <see cref="MountArgs.Reason"/> "dismount", "knockdown", or "died" (its bar ran out).</summary>
    [Hook("Mounts.OnDismount")]
    public virtual void OnDismount(MountArgs e) { }

    /// <summary>A hit on the mount bar: <see cref="MountDamageArgs.Amount"/> may be changed.</summary>
    [Hook("Mounts.OnCalcMountDamage")]
    public virtual void OnCalcMountDamage(MountDamageArgs e) { }

    /// <summary>A mount spell (the ridden mount's ability) was cast.</summary>
    [Hook("Mounts.OnMountSpell")]
    public virtual void OnMountSpell(SpellCastArgs e) { }
}

/// <summary>Health and energy changes, combat state, revives.</summary>
public abstract class UnitScript : ModuleScript
{
    /// <summary>A unit's health changed by a hit or a heal (not by regeneration; not for a bot). Id = the mob; a player only reaches the handlers for everything.</summary>
    [Hook("Units.OnHealthChange")]
    public virtual void OnHealthChange(UnitValueArgs e) { }

    /// <summary>A player's energy changed by a cast's cost or a spell's drain or gain (not by regeneration; not for a bot).</summary>
    [Hook("Units.OnEnergyChange")]
    public virtual void OnEnergyChange(UnitValueArgs e) { }

    /// <summary>A player entered combat (with a mob or a player).</summary>
    [Hook("Units.OnEnterCombat")]
    public virtual void OnEnterCombat(UnitArgs e) { }

    /// <summary>A player's combat ended.</summary>
    [Hook("Units.OnLeaveCombat")]
    public virtual void OnLeaveCombat(UnitArgs e) { }

    /// <summary>A knocked-down player got up (the timer, a revive by an ally, a resurrection, a GM).</summary>
    [Hook("Units.OnRevive")]
    public virtual void OnRevive(UnitArgs e) { }

    /// <summary>
    /// Replayed for every joined player and living mob right after this module was (re)loaded, so it can rebuild its per-entity state
    /// (data bags and entity timers are cleared at an unload).
    /// </summary>
    [Hook("Units.OnReload")]
    public virtual void OnReload(UnitArgs e) { }
}

/// <summary>The numbers the engine works out: set e.Value.</summary>
public abstract class FormulaScript : ModuleScript
{
    /// <summary>A player's cast time (seconds, after cast-time reductions); id = spell.</summary>
    [Hook("Formulas.OnCalcCastTime")]
    public virtual void OnCalcCastTime(SpellFormulaArgs e) { }

    /// <summary>A player's cooldown (seconds, after cooldown reductions); id = spell.</summary>
    [Hook("Formulas.OnCalcCooldown")]
    public virtual void OnCalcCooldown(SpellFormulaArgs e) { }

    /// <summary>A player's energy cost of a cast; id = spell.</summary>
    [Hook("Formulas.OnCalcEnergyCost")]
    public virtual void OnCalcEnergyCost(SpellFormulaArgs e) { }

    /// <summary>A spell's heal on a player before it lands; id = spell.</summary>
    [Hook("Formulas.OnCalcHeal")]
    public virtual void OnCalcHeal(SpellFormulaArgs e) { }

    /// <summary>The threat a player's damage puts on a mob (mob AI); id = the mob.</summary>
    [Hook("Formulas.OnCalcThreat")]
    public virtual void OnCalcThreat(UnitFormulaArgs e) { }

    /// <summary>The fame a kill gives each player of the fame group (whole fame units, after rates); id = the mob.</summary>
    [Hook("Formulas.OnCalcFame")]
    public virtual void OnCalcFame(UnitFormulaArgs e) { }

    /// <summary>The silver a killed mob drops (a pile's amount); id = the mob.</summary>
    [Hook("Formulas.OnCalcSilver")]
    public virtual void OnCalcSilver(UnitFormulaArgs e) { }

    /// <summary>An item's item power as worn by a player (damage, ability power and stats scale with it); id = the item.</summary>
    [Hook("Formulas.OnCalcItemPower")]
    public virtual void OnCalcItemPower(ItemFormulaArgs e) { }
}

/// <summary>Chat and parties.</summary>
public abstract class SocialScript : ModuleScript
{
    /// <summary>A player's Say line (not a command) is about to go out: <see cref="CancellablePlayerArgs.Cancel"/> drops it (the reason goes to the player), <see cref="ChatArgs.Text"/> changes it.</summary>
    [Hook("Social.OnBeforeChat")]
    public virtual void OnBeforeChat(ChatArgs e) { }

    /// <summary>A character joined a party (a new party's leader too).</summary>
    [Hook("Social.OnPartyJoined")]
    public virtual void OnPartyJoined(PartyArgs e) { }

    /// <summary>A character left a party (left, kicked, disconnected).</summary>
    [Hook("Social.OnPartyLeft")]
    public virtual void OnPartyLeft(PartyArgs e) { }
}

/// <summary>Maps being built and dropped. Limit a method or the class with [ForId("0204")] to a cluster.</summary>
public abstract class MapScript : ModuleScript
{
    /// <summary>A map was built: its objects and mobs are on it. A cluster's map comes again after an unload, and an instance (a dungeon floor, an expedition) has a cluster of its own. Id = the cluster.</summary>
    [Hook("Maps.OnLoaded")]
    public virtual void OnLoaded(MapArgs e) { }

    /// <summary>A built map was dropped (empty for a while, or its instance closed): it is gone from <see cref="IServer.LoadedClusters"/>. Id = the cluster.</summary>
    [Hook("Maps.OnUnloaded")]
    public virtual void OnUnloaded(MapArgs e) { }
}

/// <summary>Everything between a client and the server. Limit a method or the class with [ForId("CastStart")] to one operation or event.</summary>
public abstract class NetworkScript : ModuleScript
{
    /// <summary>
    /// An operation arrived from a player, before its handler: <see cref="RequestArgs.Reject"/> or <see cref="RequestArgs.Respond"/> stop the handler.
    /// Keep them cheap: a client sends several per second.
    /// </summary>
    [Hook("Network.OnRequest")]
    public virtual void OnRequest(RequestArgs e) { }

    /// <summary>An event is about to go to one client: <see cref="CancellableArgs.Cancel"/> keeps it from that client, <see cref="OutgoingArgs.Replace"/> sends another. Fires for every event of every client: register for one id where possible.</summary>
    [Hook("Network.OnEvent")]
    public virtual void OnEvent(OutgoingArgs e) { }

    /// <summary>A response is about to go to one client: <see cref="CancellableArgs.Cancel"/> keeps it back, <see cref="OutgoingArgs.Replace"/> sends another.</summary>
    [Hook("Network.OnResponse")]
    public virtual void OnResponse(OutgoingArgs e) { }
}
