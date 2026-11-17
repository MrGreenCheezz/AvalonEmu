using AlbionLocal.Modules;

namespace NoFriendlyFire;

/// <summary>Example module: a hit between two players of one party never lands. OnBeforeDamage runs before anything of the hit has happened, so Cancel leaves no trace of it.</summary>
public sealed class NoFriendlyFireScript : CombatScript
{
    public override void OnBeforeDamage(BeforeDamageArgs e)
    {
        if (e.Kind == DamageKind.PlayerToPlayer && e.Attacker is IPlayer attacker && e.Target is IPlayer target && attacker.IsInPartyWith(target)) e.Cancel();
    }
}
