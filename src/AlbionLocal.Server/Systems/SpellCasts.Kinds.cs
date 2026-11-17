using AlbionLocal.Photon;
using System.Xml.Linq;
using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageR spell-effects (analysis/work/stageR/spell-effects/SPEC.md §2): the instant effect kinds added to <see cref="Run"/>, and the kinds
/// whose spells other systems cast themselves.
/// </summary>
public static partial class SpellCasts
{
    /// <summary>
    /// Effect tags of spells that never reach this executor's effect walk on this server: their own systems run the cast and do what the effect
    /// does. useobject: the slot-25 casts at a chest, a shrine, a corrupted dungeon or mists entrance (<see cref="FakeSlotCasts"/>,
    /// <see cref="BuffShrines"/>); findtrack: the tracking toolkit's Tn_FIND_TRACK (<see cref="Hunts"/>); coupdegrace: PLAYER_COUPDEGRACE on slot 26
    /// (<see cref="Death.Execute"/>); revivefrombleedout: PLAYER_BLEEDOUT_REVIVE on slot 27 (<see cref="Revive.Start"/>); changeflagging: the
    /// FLAG_* casts of slot 24 (<see cref="Flagging"/>). Met here (another spell that has one: PROTOTYPE_REVIVE_POTION_EFFECT), they do nothing.
    /// </summary>
    public static readonly IReadOnlySet<string> OwnSystems = new HashSet<string>(StringComparer.Ordinal)
    {
        "useobject", "findtrack", "coupdegrace", "revivefrombleedout", "changeflagging",
    };

    /// <summary>
    /// dismount [mountcooldown] [cooldown] on <paramref name="q"/> (MOUNTSPELL_*_FW_* target factionself; T8_SKILLBOOK_SPECIAL_NONTRADABLE_SPELL
    /// self, cooldown 15 s in a solo or duo mists cluster and 5 s elsewhere by its conditions): a mounted player gets off as by its own dismount
    /// (<see cref="Mounts.Dismount"/>: the mount object, DISMOUNTED), and may not mount again for mountcooldown, else cooldown, seconds
    /// (MountCooldownUpdate; an earlier, longer block stays) [assume: which of the two attributes blocks the mount is not decoded]. Not mounted:
    /// nothing.
    /// </summary>
    private static void DismountBy(SpellContext c, XElement node, Player q)
    {
        if (!q.IsMounted) return;
        Mounts.Dismount(q);
        var seconds = node.Attribute("mountcooldown") is not null ? SpellTemplate.Float(node, "mountcooldown") : SpellTemplate.Float(node, "cooldown");
        if (seconds <= 0) return;
        var until = c.Ticks + Seconds(seconds);
        if (until <= q.Character.MountCooldownUntil) return;
        q.Character.MountCooldownUntil = until;
        q.World.Saves.MarkChanged(q.Character);
        q.Session.Send(Mounts.CooldownUpdate(q));
    }

    /// <summary>
    /// escapedungeon on <paramref name="q"/> run as an effect (BUFFSHRINE_ESCAPE_DUNGEON): inside an instance with a way out
    /// (<see cref="FakeSlotCasts.MayEscape"/>) DungonEscapeReady (467) to it, as at the end of the slot-21 escape cast, so its client asks
    /// for the way out (ChangeCluster {0: 0, 3: true}). Elsewhere nothing.
    /// </summary>
    private static void EscapeBy(Player q, SpellTemplate spell)
    {
        if (!q.World.FakeCasts.MayEscape(q)) return;
        q.Session.Send(new DungonEscapeReadyEvent());
        Log.Info("world", $"'{q.Character.Name}' escapes by {spell.Name}: DungonEscapeReady");
    }
}
