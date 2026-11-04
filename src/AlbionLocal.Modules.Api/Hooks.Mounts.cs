namespace AlbionLocal.Modules;

/// <summary>Mounting and riding. Id = the mount item's uniquename. Bots do not fire these.</summary>
public interface IMountHooks
{
    /// <summary>A mount was asked for and passed the engine's checks: Cancel refuses it (the client's mount bar aborts).</summary>
    IHookRegistration OnBeforeMount(Action<BeforeMountArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnBeforeMount, for one mount (the mount item's uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnBeforeMount(string mount, Action<BeforeMountArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>The mount bar started filling. <see cref="MountArgs.Reason"/> is <see cref="MountReason.Mount"/>, or <see cref="MountReason.Remount"/> at the player's mount object.</summary>
    IHookRegistration OnMountStart(Action<MountArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnMountStart, for one mount (the mount item's uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnMountStart(string mount, Action<MountArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>Mounted (the bar is full). <see cref="MountedArgs.GallopBuff"/> may be changed: the speed buff the mount's gallop applies.</summary>
    IHookRegistration OnMounted(Action<MountedArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnMounted, for one mount (the mount item's uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnMounted(string mount, Action<MountedArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>The mount bar stopped before it was full: <see cref="MountArgs.Reason"/> says why, <see cref="MountArgs.Detail"/> in words for a log.</summary>
    IHookRegistration OnMountCancel(Action<MountArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnMountCancel, for one mount (the mount item's uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnMountCancel(string mount, Action<MountArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>Off the mount: <see cref="MountArgs.Reason"/> is <see cref="MountReason.Dismount"/>, <see cref="MountReason.KnockedDown"/> or <see cref="MountReason.MountDied"/> (its bar ran out).</summary>
    IHookRegistration OnDismount(Action<MountArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnDismount, for one mount (the mount item's uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnDismount(string mount, Action<MountArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A hit on the mount bar: <see cref="MountDamageArgs.Amount"/> may be changed.</summary>
    IHookRegistration OnCalcMountDamage(Action<MountDamageArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnCalcMountDamage, for one mount (the mount item's uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnCalcMountDamage(string mount, Action<MountDamageArgs> handler, HookPriority priority = HookPriority.Normal);

    /// <summary>A mount spell (the ridden mount's ability) was cast.</summary>
    IHookRegistration OnMountSpell(Action<SpellCastArgs> handler, HookPriority priority = HookPriority.Normal);
    /// <summary>As OnMountSpell, for one mount (the mount item's uniquename, or a mask with * and ?).</summary>
    IHookRegistration OnMountSpell(string mount, Action<SpellCastArgs> handler, HookPriority priority = HookPriority.Normal);
}

/// <summary>Why a mount hook fired.</summary>
public enum MountReason
{
    /// <summary>One this list does not have yet: see <see cref="MountArgs.Detail"/>.</summary>
    Other,
    /// <summary>The bar started filling.</summary>
    Mount,
    /// <summary>The bar started filling at the player's mount object.</summary>
    Remount,
    /// <summary>The bar is full (OnMounted).</summary>
    Mounted,
    /// <summary>The player got off.</summary>
    Dismount,
    /// <summary>Damage: it stopped the bar (OnMountCancel), or it is a hit on the mount (OnCalcMountDamage).</summary>
    Damage,
    /// <summary>The player logged out.</summary>
    Logout,
    /// <summary>The player walked off.</summary>
    Walked,
    /// <summary>The player cancelled it.</summary>
    Cancelled,
    /// <summary>The player was knocked down.</summary>
    KnockedDown,
    /// <summary>The mount's bar ran out.</summary>
    MountDied,
}

/// <summary>A mount event of a player: OnMountStart, OnMountCancel, OnDismount, and the base of the other mount args.</summary>
public class MountArgs(IPlayer player, ItemType? mount, MountReason reason, string detail) : PlayerArgs(player)
{
    /// <summary>The mount item; null only when it is no longer known (a dismount after the item went).</summary>
    public ItemType? Mount { get; } = mount;

    /// <summary>Why it fired.</summary>
    public MountReason Reason { get; } = reason;

    /// <summary>The same in the server's words, for a log. Not for code to read: it may change.</summary>
    public string Detail { get; } = detail;
}

/// <summary>A mount was asked for and passed the engine's checks (OnBeforeMount): Cancel refuses it.</summary>
public sealed class BeforeMountArgs(IPlayer player, ItemInfo mount, bool remount) : CancellablePlayerArgs(player)
{
    /// <summary>The mount item.</summary>
    public ItemInfo Mount { get; } = mount;

    /// <summary>The client asked for a remount at its mount object.</summary>
    public bool Remount { get; } = remount;
}

/// <summary>A player is mounted, the bar is full (OnMounted).</summary>
public sealed class MountedArgs(IPlayer player, ItemType mount, SpellInfo? gallop) : MountArgs(player, mount, MountReason.Mounted, "mounted")
{
    /// <summary>The spell the gallop (castspellaftermoving) applies; set another for a faster (or slower) gallop (<c>e.GallopBuff = Server.Spell("…")</c>), null for none.</summary>
    public SpellInfo? GallopBuff { get; set; } = gallop;
}

/// <summary>A hit on the mount bar (OnCalcMountDamage).</summary>
public sealed class MountDamageArgs(IPlayer player, ItemType? mount, double amount, IUnit? attacker, DamageOrigin origin, SpellInfo? spell) : MountArgs(player, mount, MountReason.Damage, "damage")
{
    /// <summary>The damage about to go on the bar; set it to change that (at least 0).</summary>
    public double Amount { get; set; } = amount;

    /// <summary>The amount before any handler changed it.</summary>
    public double OriginalAmount { get; } = amount;
    /// <summary>The unit that hit; null when it is unknown or no longer on the map.</summary>
    public IUnit? Attacker { get; } = attacker;

    /// <summary>What caused the hit.</summary>
    public DamageOrigin Origin { get; } = origin;

    /// <summary>An attack or a spell's hit, not a tick over time, reflected damage or a passive's proc (<see cref="BeforeDamageArgs.IsDirectHit"/>).</summary>
    public bool IsDirectHit => Origin.IsDirect();

    /// <summary>The spell when a spell did it; null for an auto-attack.</summary>
    public SpellInfo? Spell { get; } = spell;
}
