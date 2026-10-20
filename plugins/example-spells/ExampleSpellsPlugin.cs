using AlbionLocal.Plugin.Api;

namespace ExampleSpells;

/// <summary>
/// API v2 example (server/plugins/README.md "Hooks" and "Actions"):
/// (a) a scripted spell: Clockwork Dash (the example-blade pack's CLOCKWORK_DASH, or FallbackSpell without the pack) keeps its spells.xml
///     dash (ReplaceEffects false) and leaves a lingering slow field at its aim point: a plugin area that slows every enemy inside;
/// (b) a mount hook: the chosen mount gallops with a faster speed buff at night or while the rider has a chosen buff on;
/// (c) a weapon hook: a sword gains +10 % damage while its wielder is below 30 % health (a stat modifier set and cleared on health changes).
/// </summary>
public sealed class ExampleSpellsPlugin : PluginBase
{
    public override string Name => "example-spells";
    public override string Version => "1.0.0";

    private sealed class Settings
    {
        public string DashSpell { get; set; } = "CLOCKWORK_DASH";
        public string FallbackSpell { get; set; } = "SWORD_SPIN";
        public string SlowSpell { get; set; } = "CLOCKWORK_DASH_SLOW";
        public string FallbackSlow { get; set; } = "PASSIVE_SLOWPOISON_EFFECT_MOBS";
        public double FieldSeconds { get; set; } = 6;
        public float FieldRadius { get; set; } = 4;
        public double FieldTickSeconds { get; set; } = 0.5;
        public string Mount { get; set; } = "T5_MOUNT_HORSE";
        public string FastGallopBuff { get; set; } = "MOUNT_SPEEDBUFF_DIREWOLF_T8";
        public int NightFrom { get; set; } = 20;
        public int NightTo { get; set; } = 6;
        public string WhenBuff { get; set; } = "";
        public List<string> Weapons { get; set; } = ["_MAIN_SWORD"];
        public double LowHealth { get; set; } = 0.3;
        public double DamageBonus { get; set; } = 0.10;
    }

    private Settings _s = new();
    private const string Source = "low-health-sword";

    protected override void OnLoad()
    {
        _s = Config.Bind<Settings>();

        // (a) Clockwork Dash. A pack adds the spell to spells.xml (so the client can cast it); the plugin adds what XML cannot: here a
        // field that outlives the pack's own 4 s one. Name-based: Register(spell name) is all the link there is.
        var dash = Game.Spell(_s.DashSpell) is not null ? _s.DashSpell : _s.FallbackSpell;
        var slow = Game.Spell(_s.SlowSpell) is not null ? _s.SlowSpell : _s.FallbackSlow;
        Hooks.Spells.Register(dash, new SpellScript
        {
            ReplaceEffects = false, // the spells.xml dash (and the pack's own field) still run
            OnHit = c =>
            {
                var owner = c.Caster;
                c.Game.CreateArea(owner, c.X, c.Y, _s.FieldRadius, TimeSpan.FromSeconds(_s.FieldSeconds), TimeSpan.FromSeconds(_s.FieldTickSeconds), tick =>
                {
                    foreach (var unit in tick.Inside)
                        if (c.Game.IsEnemy(owner, unit)) c.Game.ApplyBuff(unit, slow, owner, seconds: _s.FieldTickSeconds + 0.5);
                });
            },
        });
        Log.Info($"Clockwork Dash script on {dash} (slow {slow})");

        // (b) The faster gallop: decided when the mount completes.
        Hooks.Mounts.OnMounted(_s.Mount, a =>
        {
            var hour = DateTime.Now.Hour;
            var night = _s.NightFrom > _s.NightTo ? hour >= _s.NightFrom || hour < _s.NightTo : hour >= _s.NightFrom && hour < _s.NightTo;
            var buffed = _s.WhenBuff.Length > 0 && Game.FindBuff(a.Player, _s.WhenBuff) is not null;
            if ((night || buffed) && Game.Spell(_s.FastGallopBuff) is not null) a.GallopBuff.Set(_s.FastGallopBuff);
        });

        // (c) The low-health sword: kept up to date on every health change of a player, a weapon swap and a (re)load.
        Hooks.Units.OnHealthChange(a => Update(a.Unit));
        Hooks.Items.OnWeaponSwap(a => Update(a.Player));
        Hooks.Units.OnReload(a => Update(a.Unit));
    }

    private bool Sword(IPlayer p) =>
        p.Equipment.FirstOrDefault() is { } main && _s.Weapons.Any(w => main.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    private void Update(IUnit unit)
    {
        if (unit is not IPlayer p || !p.IsOnline) return;
        var low = p.MaxHealth > 0 && p.Health / p.MaxHealth < _s.LowHealth && Sword(p);
        var on = p.GetData("sword-bonus", () => new Flag());
        if (low == on.Value) return;
        on.Value = low;
        if (low)
        {
            Game.SetModifier(p, Source, "physicalattackdamagebonus", _s.DamageBonus);
            Game.SetModifier(p, Source, "physicalspelldamagebonus", _s.DamageBonus);
            p.SendMessage($"Your blade thirsts: +{_s.DamageBonus:P0} damage");
        }
        else Game.ClearModifier(p, Source);
    }

    private sealed class Flag
    {
        public bool Value;
    }
}
