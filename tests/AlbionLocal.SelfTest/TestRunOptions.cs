namespace AlbionLocal.SelfTest;

/// <summary>Options belong to the runner, rather than to the server's configuration.</summary>
public sealed record TestRunOptions(string? ClientGameData = null, string? Only = null,
    string? Report = null, string? Lab = null, bool Trace = false, bool Verbose = false, bool Help = false)
{
    public static TestRunOptions Parse(IEnumerable<string> args)
    {
        var result = new TestRunOptions();
        var seen = new HashSet<string>();
        foreach (var arg in args)
        {
            var parts = arg.Split('=', 2);
            if (!seen.Add(parts[0])) throw new ArgumentException($"duplicate option: {parts[0]}");
            if (parts.Length == 2 && string.IsNullOrWhiteSpace(parts[1]))
                throw new ArgumentException($"empty option: {parts[0]}");
            result = (parts[0], parts.Length) switch
            {
                ("--client-gamedata", 2) => result with { ClientGameData = Path.GetFullPath(parts[1]) },
                ("--only", 2) => result with { Only = parts[1] },
                ("--report", 2) => result with { Report = Path.GetFullPath(parts[1]) },
                ("--lab", 2) when parts[1] is "all" or "serialization" or "transport" or "session" or "messages"
                    => result with { Lab = parts[1] },
                ("--protocol-trace", 1) => result with { Trace = true },
                ("-v", 1) => result with { Verbose = true },
                ("--help", 1) => result with { Help = true },
                _ => throw new ArgumentException($"unknown or invalid option: {parts[0]}"),
            };
        }
        return result;
    }
}

public sealed class TestSkippedException(string message) : Exception(message);

public sealed record TestResult(string Name, string Status, double DurationMs, string? Detail = null);
