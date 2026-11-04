using AlbionLocal.Server.Entities;
using AlbionLocal.Server.Modules;

namespace AlbionLocal.Server.Network;

public sealed partial class WorldSession
{
    protected override ModuleHost ModuleGate => world.Modules;

    protected override Player? ModulePlayer => Player;
}
