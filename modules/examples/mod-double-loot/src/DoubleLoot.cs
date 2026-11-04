using AlbionLocal.Modules;

namespace DoubleLoot;

/// <summary>Example module: scales a mob's loot, by a factor from its settings. MobScript is one *Script class; FormulaScript below is another.</summary>
public sealed class LootScript : MobScript
{
    private int _factor = 1;

    protected override void OnLoad() => _factor = Math.Max(1, Config.Get("LootFactor", 2));

    public override void OnLootRolled(LootArgs e)
    {
        for (var i = 0; i < e.Items.Count; i++) e.Items[i] = e.Items[i] with { Count = e.Items[i].Count * _factor };
    }
}

/// <summary>OnCalc* hooks change a number the engine is about to use.</summary>
public sealed class FameScript : FormulaScript
{
    private double _factor = 1;

    protected override void OnLoad() => _factor = Config.Get("FameFactor", 1.5);

    public override void OnCalcFame(UnitFormulaArgs e) => e.Value *= _factor;
}

/// <summary>[ForId] limits a script to some ids; a mask works too. Every T8 mob's silver is doubled.</summary>
[ForId("T8_*")]
public sealed class EliteSilverScript : FormulaScript
{
    public override void OnCalcSilver(UnitFormulaArgs e) => e.Value *= 2;
}
