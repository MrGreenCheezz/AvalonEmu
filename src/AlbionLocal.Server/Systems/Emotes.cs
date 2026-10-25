using AlbionLocal.Server.Data;
using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Protocol;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// StageN qol (analysis/work/stageN/qol/SPEC.md §1): the player emotes. PlayEmote (op 195, y9: 0 int emote index, 1 long target object)
/// is answered with PlayEmote (event 77, axf: 0 id, 1 emote, 2 target, 3 GameTimeStamp start) to the player itself and its observers:
/// the client plays the animation and writes the emote line (SurroundingEmote) only on the event (cyz.aft 0x180A095A0, also for its own
/// character: cuq.f5 with the start and the emote's loop flag), and answers a refused one only for RC 7807 NOT_UNLOCKED (cyz.ai7). A looping
/// emote (dance, sit, sleep) is kept on the player and sent again to every new observer with its start (as a bot's, stage I); it ends with
/// StopEmote (op 196, no fields; event 78 {0 id} to the observers), a walk, a cast, a mount or a knockdown. Touched under World.Sync.
/// </summary>
public static class Emotes
{
    /// <summary>The client's NOT_UNLOCKED answer (0x1E7F): "@PLAY_EMOTE_ERROR_NOT_UNLOCKED".</summary>
    public const short NotUnlocked = 7807;

    /// <summary>The emotes of <paramref name="world"/>'s client data (the built-in 16 without it).</summary>
    public static EmoteData Data(World world)
    {
        if (world.Clusters is not { } store) return EmoteData.Default;
        try { return store.Emotes; }
        catch (Exception e) when (e is InvalidDataException or IOException) { return EmoteData.Default; }
    }

    /// <summary>
    /// <paramref name="p"/> plays emote <paramref name="index"/> at <paramref name="target"/> (0 = none). Returns the refusal RC (null = played):
    /// an unknown or locked index gets <see cref="NotUnlocked"/>; a knocked-down player, one on a mount bar or within the cooldown gets 0 and
    /// nothing is shown (the client's own checks keep those from coming).
    /// </summary>
    public static short? Play(World world, Player p, int index, long target)
    {
        var data = Data(world);
        if (data.Find(index) is not { Locked: false } emote) return NotUnlocked;
        var now = world.GameNow;
        if (p.Combat.Down is not null || p.Mount.State == MountState.Mounting || now - p.LastEmoteAt < TimeSpan.FromMilliseconds(data.CooldownMs).Ticks - TimeSpan.TicksPerMillisecond * 100)
            return 0;
        Stop(p, "another emote");
        p.LastEmoteAt = now;
        var e = new BotEmote(index, now, emote.Loops);
        var ev = Event(p, e, target);
        p.Session.Send(ev);
        p.SendToObservers(ev);
        if (e.Loops) p.Emote = e;
        world.Bots.Social.PlayerEmoted(p, index, target); // bot-social: a city bot near may answer it
        return null;
    }

    /// <summary>A looping emote of <paramref name="p"/> ends: StopEmote 78 {0 id} to its observers (its own client has left it already).</summary>
    public static void Stop(Player p, string why)
    {
        if (p.Emote is null) return;
        p.Emote = null;
        p.SendToObservers(new StopEmoteEvent { ddn = p.Id });
    }

    /// <summary>The PlayEmote of a player's looping emote for a new observer (after its spawn block), with the original start; null when none.</summary>
    public static AlbionEvent? ForNewObserver(Player p) => p.Bot is null && p.Emote is { } e ? Event(p, e, 0) : null;

    /// <summary>PlayEmote 77: key 1 through Extra, so that index 0 (applaud) goes out too (the serializer drops a 0); key 2 only with a target.</summary>
    private static PlayEmoteEvent Event(Player p, BotEmote e, long target) =>
        new() { ddn = p.Id, dmr = target, dms = e.Start, Extra = new() { [1] = e.Emote } };
}
