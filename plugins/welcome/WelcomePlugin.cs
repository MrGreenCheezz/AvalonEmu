using AlbionLocal.Plugin.Api;

namespace Welcome;

/// <summary>
/// Example plugin: greets each player at login (config "Greeting", or "FirstGreeting" for a new character), optionally announces logins
/// to everyone, and answers .motd with the message of the day (config "Motd", or a GM's .motd set &lt;text&gt;, kept in storage).
/// It counts each character's logins in its storage to show how per-player data is kept.
/// </summary>
public sealed class WelcomePlugin : PluginBase
{
    public override string Name => "welcome";
    public override string Version => "1.0.0";

    private sealed class Settings
    {
        public string Greeting { get; set; } = "Welcome back, {name}!";
        public string FirstGreeting { get; set; } = "Welcome, {name}!";
        public List<string> Motd { get; set; } = [];
        public bool AnnounceLogins { get; set; }
    }

    private Settings _settings = new();

    protected override void OnLoad()
    {
        _settings = Config.Bind<Settings>();
        Events.PlayerLogin += OnLogin;
        Commands.Register("motd", PlayerLevel.Player, "the message of the day; a GM: .motd set <text> | .motd reset", Motd);
        Log.Info($"ready: {_settings.Motd.Count} motd line(s), login announcements {(_settings.AnnounceLogins ? "on" : "off")}");
    }

    private void OnLogin(PlayerLoginArgs e)
    {
        var logins = Storage.Get("logins", new Dictionary<string, int>());
        var key = e.Player.CharacterId.ToString();
        logins[key] = logins.GetValueOrDefault(key) + 1;
        Storage.Set("logins", logins);
        var text = (e.FirstLogin ? _settings.FirstGreeting : _settings.Greeting)
            .Replace("{name}", e.Player.Name)
            .Replace("{online}", Server.OnlinePlayers.Count.ToString())
            .Replace("{logins}", logins[key].ToString());
        e.Player.SendMessage(text);
        if (_settings.AnnounceLogins) Server.Broadcast($"{e.Player.Name} has logged in.");
    }

    private void Motd(ICommandCall call)
    {
        var words = call.Words;
        if (words.Count > 0 && words[0].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            if (call.Level < PlayerLevel.GameMaster)
            {
                call.Error("only a GM may set the message of the day");
                return;
            }
            var text = call.Args[3..].Trim();
            if (text.Length == 0)
            {
                call.Error("usage: .motd set <text> (a '|' starts a new line)");
                return;
            }
            Storage.Set("motd", text.Split('|', StringSplitOptions.TrimEntries));
            call.Reply("message of the day set");
            return;
        }
        if (words.Count > 0 && words[0].Equals("reset", StringComparison.OrdinalIgnoreCase) && call.Level >= PlayerLevel.GameMaster)
        {
            Storage.Remove("motd");
            call.Reply("message of the day back to the config's");
            return;
        }
        var lines = Storage.Get<string[]?>("motd", null) ?? [.. _settings.Motd];
        call.Reply(lines.Length == 0 ? "no message of the day" : string.Join("\n", lines));
    }
}
