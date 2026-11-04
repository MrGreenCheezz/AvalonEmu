namespace AlbionLocal.Modules;

/// <summary>Chance, for hooks and timers.</summary>
/// <example>
/// <code><![CDATA[
/// if (e.IsDirectHit && Roll.Chance(0.15)) Game.ApplyBuff(e.Target, Roll.Pick(Debuffs), e.Attacker);
/// ]]></code>
/// </example>
public static class Roll
{
    /// <summary>True with this probability: 0.15 is 15 %. At 0 or less never, at 1 or more always.</summary>
    public static bool Chance(double chance) => Random.Shared.NextDouble() < chance;

    /// <summary>One of <paramref name="items"/> at random, all as likely. An empty list throws: check a list from the settings in OnLoad.</summary>
    public static T Pick<T>(IReadOnlyList<T> items)
    {
        if (items.Count == 0) throw new ArgumentException("Roll.Pick: the list is empty, there is nothing to pick from", nameof(items));
        return items[Random.Shared.Next(items.Count)];
    }
}
