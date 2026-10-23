using AlbionLocal.Server.Protocol;
using static AlbionLocal.SelfTest.AlbionTests;

namespace AlbionLocal.SelfTest;

public static class LabTests
{
    private static void Check(bool condition, string message) => AlbionTests.Check(condition, message);
    public static IEnumerable<(string, Action)> All =>
    [
        ("lab infrastructure: options reject typos and preserve defaults", Options),
        ("lab infrastructure: traces hide passwords, tokens and nested values", TraceRedaction),
    ];

    private static void Options()
    {
        Check(TestRunOptions.Parse([]).ClientGameData is null, "legacy data path remains default");
        var options = TestRunOptions.Parse(["--client-gamedata=folder with spaces", "--only=udp", "--report=report.json", "--protocol-trace"]);
        Check(options.ClientGameData == Path.GetFullPath("folder with spaces") && options.Only == "udp"
            && options.Trace && options.Report == Path.GetFullPath("report.json"), "options with spaces parsed");
        foreach (var bad in new[] { new[] { "--client-gamedata=" }, new[] { "--lab=nope" },
            new[] { "--only=a", "--only=b" }, new[] { "--typo" } })
        {
            try { TestRunOptions.Parse(bad); Check(false, "invalid option accepted"); }
            catch (ArgumentException) { }
        }
    }

    private static void TraceRedaction()
    {
        var secret = "never-print-this-secret";
        var fields = new Dictionary<byte, object?>
        {
            [253] = (short)OperationCodes.Login, [7] = secret,
            [8] = new Dictionary<string, object> { [secret] = secret }, [200] = new DangerousValue(),
        };
        var trace = ProtocolTrace.Describe("client->server", "request", 1, fields, true);
        Check(!trace.Contains(secret) && trace.Contains("albion=Login") && trace.Contains("[7]:String=<redacted>"), "no parameter value leaks");
        var token = new Dictionary<byte, object?> { [253] = (short)OperationCodes.SelectCharacter, [8] = System.Text.Encoding.UTF8.GetBytes(secret) };
        var response = ProtocolTrace.Describe("server->client", "response", 1, token, true, 0);
        Check(!response.Contains(secret) && !response.Contains(Convert.ToHexString((byte[])token[8]!))
            && response.Contains("rc=0"), "response token bytes hidden");
        var previous = ProtocolTrace.Enabled;
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            ProtocolTrace.Enabled = false;
            ProtocolTrace.Write("client->server", "request", 1, fields, true);
            Check(output.ToString().Length == 0, "disabled trace is silent");
        }
        finally { Console.SetOut(original); ProtocolTrace.Enabled = previous; }
    }

    private sealed class DangerousValue
    {
        public override string ToString() => throw new Exception("trace must never call ToString on a value");
    }
}
