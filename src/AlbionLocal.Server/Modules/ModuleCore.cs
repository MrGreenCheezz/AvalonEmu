using AlbionLocal.Modules;
using AlbionLocal.Server.Entities;

namespace AlbionLocal.Server.Modules;

/// <summary>
/// The way from the module API to the server's own objects, for a module with "coreAccess": true in its module.jsonc (docs/modules/README.md "Core access").
/// Everything public in the server is reachable from there, on the world thread; none of it is promised to stay as it is.
/// </summary>
public static class ModuleCore
{
    /// <summary>The server's <see cref="Entities.Player"/> behind <paramref name="player"/>.</summary>
    public static Player Player(IPlayer player) => ((PlayerFacade)player).Player;

    /// <summary>The server's <see cref="Entities.Mob"/> behind <paramref name="mob"/>.</summary>
    public static Mob Mob(IMob mob) => ((MobFacade)mob).Mob;

    /// <summary>The server's <see cref="Server.World"/> of the module.</summary>
    public static World World(IModuleContext context) => ((ModuleContext)context).Host.World;
}
