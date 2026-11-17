using AlbionLocal.Modules;
using AlbionLocal.Server.Modules;
using AlbionLocal.Server.Protocol;

namespace CoreAccess;

/// <summary>
/// Example module with "coreAccess": true in its module.jsonc. It compiles against the server's own assemblies, so anything public there can be used when the API has
/// no such thing yet. Nothing is promised to stay: after a server update such a module may not build; the server then says why, leaves it out and runs on.
/// </summary>
public sealed class CoreAccessScript : PlayerScript
{
    public override void OnLogin(PlayerLoginArgs e)
    {
        var player = ModuleCore.Player(e.Player); // the server's own Player
        var world = ModuleCore.World(Context);    // the server's own World
        e.Player.SendMessage($"{player.Character.Name}: template {player.Character.Template}, preset {world.Config.Preset}");
    }
}

/// <summary>A typed network hook: the message classes are the protocol's, their field names the client's.</summary>
public sealed class CoreNetworkScript : NetworkScript
{
    [ForId("Say")]
    public override void OnRequest(RequestArgs e)
    {
        if (e.As<SayRequest>() is { aa9: "ping" }) e.Reject(reason: "pong");
    }
}
