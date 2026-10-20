using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Game;

namespace AlbionLocal.Server.Systems;

/// <summary>
/// The attunement an awakened weapon gains (stageN awakening SPEC §5): kill fame earned with it in the main hand, attuned to the wielder,
/// × attunement famefactor (0.037), up to maxbaseattunement (100000) × the tier scaling × strain. The soul is re-sent (event 37) and the
/// items saved. The official gain formula is server-side (attunementgainexponent and the strain growth are not modelled).
/// </summary>
public static class Attunement
{
    public static void OnFame(Player p, long gain)
    {
        if (gain <= 0 || p.World.Legendary is not { } data || p.Inventory.Equipment[0] is not { Soul: { } soul } item || !soul.AttunedTo(p.Character.Id)) return;
        var cap = (long)Math.Round((decimal)(data.MaxBaseAttunement * data.Scale(item.Template.Tier) * (soul.Strain / (double)Pricing.A7m)) * Pricing.A7m);
        var next = Math.Min(cap, soul.Attunement + (long)Math.Round(gain * (decimal)data.FameFactor));
        if (next <= soul.Attunement) return;
        item.Soul = soul with { Attunement = next };
        p.Session.Send(Network.WorldSession.SoulEvent(item, p.Character.Id));
        p.SaveItems();
    }
}
