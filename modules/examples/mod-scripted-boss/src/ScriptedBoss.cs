using AlbionLocal.Modules;

namespace ScriptedBoss;

/// <summary>
/// Example module: a boss of a module's own (docs/modules/README.md "A boss of your own"). Its type is a copy of a mobs.xml mob with other numbers: the clients see that mob's
/// model and name. In its first phase the engine's AI fights and the module adds a burning circle. At half health it stands, invulnerable, until its servants are dead.
/// Then it is enraged and the module fights for it. A GM spawns it with .warden.
/// </summary>
[ForId(Warden)]
public sealed class Boss : MobScript
{
    public const string Warden = "EXAMPLE_WARDEN";
    private const string Servant = "T4_MOB_ROAMING_UNDEAD_GHOUL";
    private const string Circle = "FIREWALL";                 // the look of the burning circle: a spell with a spelleffectarea

    /// <summary>What this module keeps on the mob (a mob's data goes with it and with the module).</summary>
    public sealed class Fight
    {
        public int Phase = 1;
        public double NextCircle = 5;
        public readonly List<IMob> Servants = [];
    }

    protected override void OnLoad()
    {
        Server.DefineMob(Warden, "T4_MOB_ROAMING_UNDEAD_SOLDIER_MINIBOSS", m =>
        {
            m.MaxHealth = 20000;
            m.AttackDamage = 200;
            m.KeepsPositionOnReset = true;
        });
        Commands.Register("warden", PlayerLevel.Admin, "spawns the warden next to you", call =>
        {
            if (call.Player is { } p) call.Reply(Server.SpawnMob(p.Cluster, Warden, p.X + 3, p.Y) is null ? "no warden here" : "the warden is here");
        });
    }

    public override void OnAggro(MobAggroArgs e) => Server.SendToZone(e.Mob.Cluster, $"{e.Player.Name}, you will not leave this place alive!");

    public override void OnBeforeThink(BeforeThinkArgs e)
    {
        var boss = e.Mob;
        if (boss.Threat is not [var top, ..]) return;         // nobody to fight: the engine's AI
        var fight = boss.Data<Fight>();
        fight.NextCircle -= e.DeltaSeconds;

        switch (fight.Phase)
        {
            case 1 when boss.Health < boss.MaxHealth / 2:
                fight.Phase = 2;
                boss.Stop();
                foreach (var side in new[] { -3, 3 })
                    if (Game.Summon(boss, Servant, boss.X + side, boss.Y, TimeSpan.FromMinutes(2)) is { } servant)
                    {
                        servant.SetTarget(top.Player);
                        fight.Servants.Add(servant);
                    }
                Server.SendToZone(boss.Cluster, "Rise, my servants!");
                break;
            case 1 when fight.NextCircle <= 0:
                fight.NextCircle = 8;
                BurnAround(boss, top.Player);
                break;
            case 2:
                e.Cancel();                                   // it stands: no chase, no swing
                if (fight.Servants.Any(s => s.IsAlive)) break;
                fight.Phase = 3;
                Game.SetModifier(boss, "enraged", Stat.AttackDamage, 1);
                Game.SetModifier(boss, "enraged", Stat.AttackSpeed, 0.5);
                Server.SendToZone(boss.Cluster, "Enough!");
                break;
            case 3:
                e.Cancel();                                   // the module fights for it
                if (boss.IsCasting) break;
                if (!boss.InReach(top.Player)) boss.MoveTo(top.Player.X, top.Player.Y);
                else if (boss.IsMoving) boss.Stop();
                else boss.Attack(top.Player);
                break;
        }
    }

    private void BurnAround(IMob boss, IPlayer player) =>
        Game.CreateArea(boss, player.X, player.Y, 3, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), tick =>
        {
            foreach (var victim in tick.Inside.OfType<IPlayer>()) Game.Damage(boss, victim, 100);
        }, visual: Circle);

    public override void OnLootRolled(LootArgs e) => e.Items.Add("T4_ORE", 20);

    // The fight starts over, with its numbers as they were. Its servants have their own time.
    public override void OnLeaveCombat(MobLeaveCombatArgs e)
    {
        Game.ClearModifier(e.Mob, "enraged");
        e.Mob.RemoveData<Fight>();
    }
}

/// <summary>While its servants live it takes no damage.</summary>
[ForId(Boss.Warden)]
public sealed class BossShield : CombatScript
{
    public override void OnBeforeDamage(BeforeDamageArgs e)
    {
        if (e.Target.Data<Boss.Fight>().Phase == 2) e.Cancel();
    }
}
