using System.Globalization;
using System.Net;

namespace AlbionLocal.Server.Web;

/// <summary>
/// The built-in web panel (analysis/work/stageN/webpanel/SPEC.md): Web:Enabled (false; the pve preset true), Web:BindAddress
/// (127.0.0.1; 0.0.0.0 = every interface), Web:Port (8080; 0 = a free one), Web:Registration (true: the public registration
/// form, which Accounts:Registration false closes as well), Web:SessionMinutes (30: an admin session ends after this long
/// without a request).
/// </summary>
public sealed record WebOptions
{
    public bool Enabled { get; init; }
    public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = 8080;
    public bool Registration { get; init; } = true;
    public int SessionMinutes { get; init; } = 30;

    /// <summary>The Web and Backup sections out of <paramref name="values"/> over the preset's <paramref name="d"/>; a bad value throws.</summary>
    internal static (WebOptions Web, BackupOptions Backup) Load(Dictionary<string, string> values, WebOptions d, BackupOptions b)
    {
        string? Take(string key) => values.Remove(key, out var v) ? v : null;
        bool Flag(string key, bool fallback) => Take(key) is not { } v ? fallback
            : bool.TryParse(v, out var f) ? f : throw new InvalidDataException($"{key}: '{v}' is not true or false");
        int Int(string key, int fallback, int min, int max) => Take(key) is not { } v ? fallback
            : int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= min && i <= max ? i
            : throw new InvalidDataException($"{key}: '{v}' is not {min}..{max}");
        var bind = Take("Web:BindAddress") is not { } a ? d.BindAddress
            : IPAddress.TryParse(a, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && a.Count(ch => ch == '.') == 3 ? ip
            : throw new InvalidDataException($"Web:BindAddress: '{a}' is not an IPv4 address");
        var web = new WebOptions
        {
            Enabled = Flag("Web:Enabled", d.Enabled),
            BindAddress = bind,
            Port = Int("Web:Port", d.Port, 0, 65535),
            Registration = Flag("Web:Registration", d.Registration),
            SessionMinutes = Int("Web:SessionMinutes", d.SessionMinutes, 1, 7 * 24 * 60),
        };
        var hours = Take("Backup:EveryHours") is not { } h ? b.EveryHours
            : double.TryParse(h, NumberStyles.Float, CultureInfo.InvariantCulture, out var eh) && eh is >= 0 and <= 24 * 365 ? eh
            : throw new InvalidDataException($"Backup:EveryHours: '{h}' is not 0..8760");
        return (web, new BackupOptions { EveryHours = hours, Keep = Int("Backup:Keep", b.Keep, 1, 10_000) });
    }
}

/// <summary>
/// Database backups into Paths:Saves/backups (SPEC §4): Backup:EveryHours (0 = only the panel's button; e.g. 6 = one every six hours
/// while the server runs, the first one after that long) and Backup:Keep (10: the newest kept, older ones deleted after each backup).
/// Independent of Web:Enabled.
/// </summary>
public sealed record BackupOptions
{
    public double EveryHours { get; init; }
    public int Keep { get; init; } = 10;
}
