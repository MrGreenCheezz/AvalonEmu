namespace AlbionLocal.Modules;

/// <summary>All the hooks as lambdas (<see cref="IModuleContext.Hooks"/>). The same hooks are the methods of the *Script classes.</summary>
public interface IHooks
{
    /// <summary>The server starting, stopping and ticking.</summary>
    IServerHooks Server { get; }

    /// <summary>Players entering and leaving the game and the zones, going down, completing goals.</summary>
    IPlayerHooks Players { get; }

    /// <summary>Damage and heals, whatever caused them: cancel or change them here.</summary>
    ICombatHooks Combat { get; }

    /// <summary>Mobs spawning and dying, their loot.</summary>
    IMobHooks Mobs { get; }

    /// <summary>The life of a spell: cast, hit, effect, channel ticks, areas.</summary>
    ISpellHooks Spells { get; }

    /// <summary>The life of a buff or debuff on a unit.</summary>
    IBuffHooks Buffs { get; }

    /// <summary>Equipment, consumables, crafting, gathering.</summary>
    IItemHooks Items { get; }

    /// <summary>Mounting and riding.</summary>
    IMountHooks Mounts { get; }

    /// <summary>Health and energy changes, combat state, revives.</summary>
    IUnitHooks Units { get; }

    /// <summary>The numbers the engine works out: cast times, cooldowns, fame, silver, item power.</summary>
    IFormulaHooks Formulas { get; }

    /// <summary>Chat and parties.</summary>
    ISocialHooks Social { get; }

    /// <summary>Everything between the client and the server: any operation, event and response.</summary>
    INetworkHooks Network { get; }

    /// <summary>Maps being built and dropped.</summary>
    IMapHooks Maps { get; }
}
