using System.Globalization;
using AlbionLocal.Photon;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;
using AlbionLocal.Server.Systems;

namespace AlbionLocal.Server.Network;

/// <summary>
/// Fighting mobs (stage 5b, analysis/work/stage5/SPEC.md §5b.1, §5b.6; the rules in <see cref="SpellCasts"/> and
/// <see cref="Combat"/>): AttackStart, CastStart, CastCancel, TerminateToggleSpell and ChannelingCancel. All are fire-and-forget:
/// no response on success (the official server never answered a CastStart in the captures), only events.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>AttackStart refused for a dead, unknown or invisible target: RcGeneric.InvalidState (seen officially).</summary>
    public const short AttackInvalidTarget = -501;

    /// <summary>AttackStart refused out of range (922, seen officially; its meaning is not confirmed, SPEC §5b.6, §9 Q5).</summary>
    public const short AttackOutOfRange = 922;

    /// <summary>
    /// The ReturnCode of a refused CastStart: the first value of the client's cast error range 960-984 (SPEC §5b.6). Which
    /// member means what is open (§9 Q5: obfuscated enum, localisation keys not traced), so every refusal uses this one.
    /// </summary>
    public const short CastRefused = 960;

    /// <summary>
    /// AttackStart (op 23, pk): 0 the client's GameTimeStamp, 1 the target, 2 the combo index (0, 1, 2 in the combo window).
    /// The client sends it for every swing and predicts the animation and the next swing itself. Refused with a bare
    /// response {code 23, ReturnCode} (the client stops its auto-attack, cvy.ai7 0x1809d4b90): -501 when the player is knocked down (5e),
    /// stunned or under cannotattack (stageD effects SPEC §3.2), or the target is not a
    /// living mob the player sees, 922 when it is farther than attackrange + 1 m (edge to edge). A request earlier than the
    /// swing interval (1 / attackspeed) − 150 ms after the last accepted one is dropped without an answer. Accepted: the
    /// swing (<see cref="SpellCasts.Swing"/>), no response. StageE (pvp SPEC §1.2, §1.3): a player as the target needs to be
    /// in view and Hostile to the attacker (<see cref="Pvp.Refusal"/>: the duel opponent once the duel has started, else the
    /// cluster's flag rules), else -501 as for an invalid mob; the swing is <see cref="Pvp.Swing"/>.
    /// </summary>
    [Handles(OperationCodes.AttackStart, Status = SessionStatus.InWorld, Quiet = true)]
    private AlbionResponse? AttackStart(AttackStartRequest req)
    {
        if (Player is not { Map: { } map } player) return null;
        Death.Interrupt(player, "the player attacks", interrupted: false); // the client has left its execute for this attack [infer]
        world.FakeCasts.Interrupt(player, "the player attacks", interrupted: false); // and its fake-slot cast (a corrupted or mists entrance, a chest)
        Emotes.Stop(player, "attacks"); // stageN qol: a looping emote ends for the observers
        SpellEffects.ExpireAfterMovement(player); // a move can precede this request within the same world tick
        if (player.Combat.Down is not null)
        {
            Log.Debug(Name, $"'{player.Character.Name}' AttackStart while knocked down, refused ({AttackInvalidTarget})");
            return new GenericResponse { ReturnCode = AttackInvalidTarget };
        }
        if (SpellEffects.CannotAttack(player, world.GameNow))
        {
            Log.Debug(Name, $"'{player.Character.Name}' AttackStart while stunned or unable to attack, refused ({AttackInvalidTarget})");
            return new GenericResponse { ReturnCode = AttackInvalidTarget };
        }
        if (map.FindPlayer(req.co) is { } victim && victim != player) return AttackPlayer(player, victim, req); // a bot too (stage I)
        if (map.Objects.GetValueOrDefault(req.co) is not Mob { Attackable: true } mob || !player.Visibility.Knows(mob))
        {
            Log.Info(Name, $"'{player.Character.Name}' AttackStart on object {req.co}: no living mob in view, refused ({AttackInvalidTarget})");
            return new GenericResponse { ReturnCode = AttackInvalidTarget };
        }
        var weapon = Combat.WeaponOf(player);
        var distance = Combat.EdgeDistance(player, player.PositionAt(DateTime.UtcNow), mob);
        if (distance > weapon.Range + SpellCasts.RangeTolerance)
        {
            Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
                $"'{player.Character.Name}' AttackStart on {mob}: {distance:0.0} m, attackrange {weapon.Range}, refused ({AttackOutOfRange})"));
            return new GenericResponse { ReturnCode = AttackOutOfRange };
        }
        var start = SpellCasts.StartOf(world, req.wf);
        var last = player.Combat.LastSwing;
        if (last != 0 && start < last + SpellCasts.SwingInterval(player) - SpellCasts.Early.Ticks)
        {
            Log.Debug(Name, $"'{player.Character.Name}' AttackStart {(start - last) / TimeSpan.TicksPerMillisecond} ms after the last swing: too early, dropped");
            return null;
        }
        Knockdown.Acted(player, "attacks");
        Mounts.BeforeAction(player, "attacks"); // stageD mounts SPEC §4.3: the server dismounts first (the client does too, dyl.as5)
        SpellCasts.Swing(player, mob, req.cp, start);
        return null;
    }

    /// <summary>AttackStart on another player (stageE): the checks of <see cref="AttackStart"/> with <see cref="Pvp.Refusal"/>, then <see cref="Pvp.Swing"/>.</summary>
    private AlbionResponse? AttackPlayer(Player player, Player victim, AttackStartRequest req)
    {
        var now = world.GameNow;
        if ((player.Observers.Contains(victim) ? Pvp.Refusal(player, victim, now) : "not in view") is { } why)
        {
            Log.Info(Name, $"'{player.Character.Name}' AttackStart on player '{victim.Character.Name}' refused ({AttackInvalidTarget}): {why}");
            return new GenericResponse { ReturnCode = AttackInvalidTarget };
        }
        var weapon = Combat.WeaponOf(player);
        var distance = Pvp.EdgeDistance(player, player.PositionAt(DateTime.UtcNow), victim);
        if (distance > weapon.Range + SpellCasts.RangeTolerance)
        {
            Log.Info(Name, string.Create(CultureInfo.InvariantCulture,
                $"'{player.Character.Name}' AttackStart on '{victim.Character.Name}': {distance:0.0} m, attackrange {weapon.Range}, refused ({AttackOutOfRange})"));
            return new GenericResponse { ReturnCode = AttackOutOfRange };
        }
        var start = SpellCasts.StartOf(world, req.wf);
        var last = player.Combat.LastSwing;
        if (last != 0 && start < last + SpellCasts.SwingInterval(player) - SpellCasts.Early.Ticks) return null; // too early: dropped
        Knockdown.Acted(player, "attacks");
        Mounts.BeforeAction(player, "attacks");
        Pvp.Swing(player, victim, req.cp, start);
        return null;
    }

    /// <summary>
    /// CastStart (op 24, p6): 0 GameTimeStamp, 1 CharacterSpellSlot, 2 byte 0, 4 spell id, 5 target (every class but self and
    /// ground: a mob, another player or bot the client judged, the caster's own id for a cast on oneself; stageJ pvp-spells SPEC
    /// §2.4), 6 aim point (ground spells), 8 cast counter (1-255, wraps to 1, restarts each Join). Checked by <see cref="SpellCasts.Check"/>;
    /// accepted: <see cref="SpellCasts.Start"/> and no response, as the official server (it answered none of 96 captured
    /// requests). A CastStart of the toggle that is on in that slot switches it off (<see cref="SpellCasts.EndToggle"/>). A refusal is answered with the response aea {1 slot, 2 spell, 7 counter, 8 target} and
    /// <see cref="CastRefused"/>, which makes the client abort its predicted cast and show an error (SPEC §5b.6). Mounted on a mount
    /// with abilities, a spell in the mount's slot layout (<see cref="Mounts.SpellSlots"/>) is checked against that layout with the mount
    /// item as its source and keeps the rider mounted; any other cast dismounts first (stageD mounts SPEC §4.3). StageE E3: slot 26
    /// (CoupDeGrace) is the execute of a knocked-down player (<see cref="Death.Execute"/>), refused the same way. Slot 24
    /// (ChangeFlagging) is the flag cast of op 354 (<see cref="Flagging.Prepare"/>): a CastStart for it gets no answer and starts nothing.
    /// Slot 25 (FakeSpell) uses an object: a corrupted dungeon entrance, a mists portal or a loot chest (<see cref="FakeSlotCast"/>). Slot 21
    /// inside an instance is the escape, a server cast ending in DungonEscapeReady (<see cref="FakeSlotCasts.StartEscape"/>).
    /// </summary>
    [Handles(OperationCodes.CastStart, Status = SessionStatus.InWorld)]
    private AlbionResponse? CastStart(CastStartRequest req)
    {
        if (Player is not { Map: not null } player) return null;
        Emotes.Stop(player, "casts"); // stageN qol
        if (req.dx == Flagging.Slot)
        {
            // The flag spell's cast is op 354's (Flagging.Prepare, which also ends the client's own cast on a refusal): a 960 here
            // would abort the client's cast bar although the flag change goes on (stageJ flag-toggle NOTES §3).
            Log.Debug(Name, $"'{player.Character.Name}' CastStart of {world.Spells?.Name(req.d0) ?? req.d0.ToString(CultureInfo.InvariantCulture)} in slot {req.dx} (the flag cast of op 354): no answer");
            return null;
        }
        if (req.dx == Death.ExecuteSlot)
        {
            if (Death.Execute(player, req.d0, req.d1, req.d4, SpellCasts.StartOf(world, req.wf)) is not { } refused) return null;
            Log.Info(Name, $"'{player.Character.Name}' execute of object {req.d1} refused: {refused}");
            return new CastStartResponse { ReturnCode = CastRefused, anl = req.d0, anq = req.d4, anr = req.d1, Extra = { [1] = req.dx } };
        }
        if (req.dx == Revive.Slot)
        {
            // StageN revive: PLAYER_BLEEDOUT_REVIVE on a bleeding-out party or guild mate (the client's DoActionAttack on a non-Hostile state-17 player).
            if (Revive.Start(player, req.d0, req.d1, req.d4, SpellCasts.StartOf(world, req.wf)) is not { } refused) return null;
            Log.Info(Name, $"'{player.Character.Name}' revive of object {req.d1} refused: {refused}");
            return new CastStartResponse { ReturnCode = CastRefused, anl = req.d0, anq = req.d4, anr = req.d1, Extra = { [1] = req.dx } };
        }
        // StageN extra bars (SPEC §4.2): the mod casts an extra slot's spell through the client's fake slot 25; a spell with a useobject is the
        // client's own object use (a chest, an entrance) whatever the bars hold.
        if (req.dx == FakeSlotCasts.Slot && world.Spells?.Template(req.d0) is { UseObject: null } && world.ExtraBars.IndexOf(player, req.d0) is var extra and >= 0)
            return ExtraSlotCastStart(player, extra, req);
        if (req.dx == FakeSlotCasts.Slot) return FakeSlotCast(player, req);
        if (req.dx == FakeSlotCasts.EscapeSlot && world.FakeCasts.MayEscape(player))
        {
            // StageN corrupted-exit (SPEC): the escape inside an instance (a dungeon, an expedition, a corrupted dungeon, a hellgate) is a server
            // cast whose end is DungonEscapeReady (467); only then does the client send ChangeCluster {0: 0, 3: true}. Unanswered it hangs.
            Death.Interrupt(player, "the player casts", interrupted: false);
            if (world.FakeCasts.StartEscape(player, req.d0, req.d4, SpellCasts.StartOf(world, req.wf)) is not { } refused) return null;
            Log.Info(Name, $"'{player.Character.Name}' escape cast of {world.Spells?.Name(req.d0) ?? req.d0.ToString(CultureInfo.InvariantCulture)} refused: {refused}");
            return new CastStartResponse { ReturnCode = CastRefused, anl = req.d0, anq = req.d4, anr = req.d1, Extra = { [1] = req.dx } };
        }
        Death.Interrupt(player, "the player casts", interrupted: false); // the client has left its execute for this cast [infer]
        world.FakeCasts.Interrupt(player, "the player casts", interrupted: false); // and its fake-slot cast (a corrupted or mists entrance, a chest)
        if (player.Combat.Toggles.GetValueOrDefault(req.dx) is { } on && on.Spell.Id == req.d0)
        {
            // The client sends TerminateToggleSpell for a toggle it shows on (cwc.u 0x1809d7720 casts nothing then); a CastStart of a
            // toggle that is on here means it has lost that state: the press switches it off all the same.
            SpellCasts.EndToggle(player, on, SpellCasts.ToggleEnd.Terminated);
            return null;
        }
        var start = SpellCasts.StartOf(world, req.wf);
        var mountSpell = Mounts.IsMountSpell(player, req.dx, req.d0);
        var (cast, why) = mountSpell
            ? SpellCasts.Check(player, Mounts.SpellSlots(player)!, req.dx, req.d0, req.d1, req.d2, req.d4, start, Mounts.ItemOf(player))
            : SpellCasts.Check(player, SpellSlots(player.Inventory), req.dx, req.d0, req.d1, req.d2, req.d4, start);
        if (cast is null)
        {
            Log.Info(Name, $"'{player.Character.Name}' CastStart of {world.Spells?.Name(req.d0) ?? req.d0.ToString(CultureInfo.InvariantCulture)} in slot {req.dx} refused: {why}");
            return new CastStartResponse
            {
                ReturnCode = CastRefused, anl = req.d0, anq = req.d4, anr = req.d1,
                Extra = { [1] = req.dx }, // [1] the slot, also 0 (must be a valid slot)
            };
        }
        // The plugins' formulas (cast time, energy cost) and Spells.OnCheckCast: a refusal is answered as the engine's own.
        var (gated, refusedBy) = world.Plugins.CheckCast(player, cast);
        if (refusedBy is not null)
        {
            Log.Info(Name, $"'{player.Character.Name}' CastStart of {cast.Spell.Name} in slot {req.dx} refused by a plugin: {refusedBy}");
            if (refusedBy.Length > 0) player.Session.Send(new SystemMessageEvent { d0p = refusedBy, d0q = 1 });
            return new CastStartResponse { ReturnCode = CastRefused, anl = req.d0, anq = req.d4, anr = req.d1, Extra = { [1] = req.dx } };
        }
        cast = gated;
        Knockdown.Acted(player, "casts");
        CancelAction(player, "the player casts", tellClient: true); // a station action (stage 6 SPEC §6a.1)
        if (mountSpell) Mounts.CastsMountSpell(player); // the mount's own ability: still mounted
        else Mounts.BeforeAction(player, "casts"); // stageD mounts SPEC §4.3 (the client dismounts first itself, LastDismountBySpell)
        SpellCasts.Start(player, cast);
        return null;
    }

    /// <summary>
    /// CastStart in CharacterSpellSlot.FakeSpell (25): the client has put an allowfakeslot spell there to use an object (stageM chest-slot25
    /// NOTES); the server cast is picked by the spell's useobject type: corrupteddungeonexitdifficulty1-3 the corrupted dungeon entrance (stageL
    /// SPEC §2.2, §4.2 step 3; a refusal also tells why in the chat), mistsentrance (ENTER_MISTS) the mists portal's entry (stageL mists SPEC
    /// §3.5, <see cref="Mists.StartEnter"/>; a refusal tells why too), lootchest the chest opening (<see cref="FakeSlotCasts.StartChest"/>; the client
    /// sends no UseLootChest for an Unlocked chest), shrine a buff shrine's use (<see cref="BuffShrines.StartUse"/>), any other refused. Accepted: no answer; refused: 960 as any cast.
    /// </summary>
    private AlbionResponse? FakeSlotCast(Player player, CastStartRequest req)
    {
        var start = SpellCasts.StartOf(world, req.wf);
        var spell = world.Spells?.Template(req.d0);
        var use = spell?.UseObject;
        string? refused;
        if (use?.StartsWith("corrupteddungeonexitdifficulty", StringComparison.Ordinal) == true || spell is null && world.Spells is null)
        {
            if ((refused = world.Corrupted.StartEnter(player, req.d0, req.d2, req.d4, start)) is null) return null;
            Log.Info(Name, $"'{player.Character.Name}' corrupted dungeon enter cast refused: {refused}");
            Expeditions.Tell(player, world.CorruptedSettings.Text("cd.refused", ("reason", refused)));
        }
        else if (use == "mistsentrance")
        {
            if ((refused = world.Mists.StartEnter(player, req.d0, req.d2, req.d4, start)) is null) return null;
            Log.Info(Name, $"'{player.Character.Name}' mists enter cast refused: {refused}");
            Expeditions.Tell(player, world.MistsSettings.Text("Refused", ("why", refused)));
        }
        else if (use == "shrine") // stageN shrines (SPEC §4): OPEN_BUFFSHRINE after an accepted UseShrine
        {
            if ((refused = world.Shrines.StartUse(player, req.d0, req.d2, req.d4, start)) is null) return null;
            Log.Info(Name, $"'{player.Character.Name}' shrine use cast of {spell!.Name} refused: {refused}");
        }
        else if (use == "lootchest")
        {
            if ((refused = world.FakeCasts.StartChest(player, req.d0, req.d2, req.d4, start)) is null) return null;
            Log.Info(Name, $"'{player.Character.Name}' chest opening cast of {spell!.Name} refused: {refused}");
        }
        else Log.Info(Name, $"'{player.Character.Name}' fake-slot cast of {spell?.Name ?? req.d0.ToString(CultureInfo.InvariantCulture)} (useobject {use ?? "none"}) is not supported, refused");
        return new CastStartResponse { ReturnCode = CastRefused, anl = req.d0, anq = req.d4, anr = req.d1, Extra = { [1] = req.dx } };
    }

    /// <summary>
    /// DeathRecap (op 492, ro) {0 byte 1}: the knockdown screen (and the character menu later) asks who did the damage. Answered with
    /// the player's last recap (stageP league-pvp SPEC §2.1, <see cref="DeathRecaps.Response"/>); none, or DeathRecap:Enabled false: the
    /// empty recap (aep with its arrays empty).
    /// </summary>
    [Handles(OperationCodes.DeathRecap, Status = SessionStatus.InWorld)]
    private AlbionResponse DeathRecap(DeathRecapRequest req)
    {
        if (Player is not { } player) return new DeathRecapResponse().FillEmpty();
        var resp = world.Recaps.Response(player);
        Log.Debug(Name, $"DeathRecap {req.je} of '{player.Character.Name}': {resp.apj?.Length ?? 0} causer(s)");
        return resp;
    }

    /// <summary>
    /// CastCancel (op 25, p5): 0 GameTimeStamp. The client has left its cast state already: the cast under way is dropped, the
    /// observers get CastCancel, the caster nothing. A channel under way that allows it (allowcancel, stageD effects SPEC §7 step 9)
    /// ends with ChannelingEnded {1 2}; a flag cast under way (stageE pvp SPEC §3.1, <see cref="Flagging.Interrupt"/>) ends as well.
    /// </summary>
    [Handles(OperationCodes.CastCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? CastCancel(CastCancelRequest req)
    {
        if (Player is { Combat.Cast: { } cast } player)
        {
            SpellCasts.Cancel(player, cast, interrupted: false);
            Log.Debug(Name, $"'{player.Character.Name}' cancelled {cast.Spell}");
        }
        else if (Player is { Combat.Channel.Info.AllowCancel: true } channeling) SpellCasts.EndChannel(channeling, SpellCasts.ChannelEnd.Cancelled);
        // The flag cast and the execute (stageE E3) are server casts of their own; a no-op without one.
        if (Player is { } canceller) Flagging.Interrupt(canceller, "the player cancelled it", interrupted: false);
        return null;
    }

    /// <summary>
    /// TerminateToggleSpell (op 26, abr) {0 GameTimeStamp, 1 CharacterSpellSlot}: the second press of a toggle the client shows on
    /// (UISpellSlotBase.UpdateToggleSpellActive). No response (the client has none for it): the toggle ends
    /// (<see cref="SpellCasts.EndToggle"/>: its effects off, CastCancel {4 true} with the cooldown). When none is on in that slot
    /// here (a toggle from before a restart) the client still gets CastCancel {4 true, 5 slot} with the spell's cooldown as it is,
    /// so its slot does not stay stuck on.
    /// </summary>
    [Handles(OperationCodes.TerminateToggleSpell, Status = SessionStatus.InWorld)]
    private AlbionResponse? TerminateToggleSpell(TerminateToggleSpellRequest req)
    {
        if (Player is not { Map: not null } player) return null;
        if (player.Combat.Toggles.GetValueOrDefault(req.agt) is { } on)
        {
            SpellCasts.EndToggle(player, on, SpellCasts.ToggleEnd.Terminated);
            return null;
        }
        var now = world.GameNow;
        var slots = Mounts.SpellSlots(player) ?? SpellSlots(player.Inventory); // a mount's toggle (MOUNTSPELL_SPRINT_*) while mounted
        var end = req.agt < slots.Length && player.Combat.Cooldowns.TryGetValue((short)slots[req.agt], out var ready) ? Math.Max(now, ready) : now;
        Log.Debug(Name, $"'{player.Character.Name}' TerminateToggleSpell in slot {req.agt}: no toggle on, the client's switched off");
        var cancel = new CastCancelEvent { ddn = player.Id, buq = SpellCasts.CooldownFactorOf(player), bur = true, bus = req.agt, but = end, buu = now, buv = end - now };
        if (player.Combat.Cast is not null) player.Combat.HeldToggleCancels.Add(cancel); // after the cast under way, as EndToggle's
        else player.Session.Send(cancel);
        return null;
    }

    /// <summary>
    /// ChannelingCancel (op 27, qn) {0 GameTimeStamp, 1 bool, 2 Vector2 (the position)}: the client has left the channel of its
    /// cast already (what key 1 means is not traced). No response: the channel under way ends as cancelled (ChannelingEnded
    /// {1 2}, its linked effects and areas off, its endeffect unless cancelpreventsendeffects, <see cref="SpellCasts.EndChannel"/>).
    /// </summary>
    [Handles(OperationCodes.ChannelingCancel, Status = SessionStatus.InWorld)]
    private AlbionResponse? ChannelingCancel(ChannelingCancelRequest req)
    {
        if (Player is not { Combat.Channel: { } channel } player) return null;
        Log.Debug(Name, $"'{player.Character.Name}' ChannelingCancel of {channel.Template} ({req.e9})");
        SpellCasts.EndChannel(player, SpellCasts.ChannelEnd.Cancelled);
        return null;
    }
}
