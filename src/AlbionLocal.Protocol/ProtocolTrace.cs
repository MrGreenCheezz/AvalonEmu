namespace AlbionLocal.Server.Protocol;

/// <summary>Opt-in educational trace. Parameter values are never formatted, including unknown fields.</summary>
public static class ProtocolTrace
{
    public static bool Enabled { get; set; }

    public static string Describe(string direction, string kind, byte photonCode,
        IReadOnlyDictionary<byte, object?> parameters, bool encrypted, short? returnCode = null)
    {
        var key = kind == "event" ? ReservedKeys.EventCode : ReservedKeys.OperationCode;
        var code = parameters.GetValueOrDefault(key) switch
        {
            short s => (int?)s,
            byte b => b,
            int i when i is >= short.MinValue and <= short.MaxValue => i,
            _ => null,
        };
        // Status-server event 1 is not the world Leave event 1. Event names need session context.
        var name = code is not { } n ? "none" : kind == "event"
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{Enum.GetName(typeof(OperationCodes), (short)n) ?? "unknown"}({n})";
        // Even arbitrary objects and nested dictionaries are described only by their CLR type; no ToString calls.
        var fields = string.Join(", ", parameters.OrderBy(p => p.Key)
            .Select(p => $"[{p.Key}]:{p.Value?.GetType().Name ?? "null"}=<redacted>"));
        return $"[protocol] {direction} {kind} photon={photonCode} albion={name} encrypted={encrypted}"
            + (returnCode is { } rc ? $" rc={rc}" : "") + $" | {fields}";
    }

    public static void Write(string direction, string kind, byte photonCode,
        IReadOnlyDictionary<byte, object?> parameters, bool encrypted, short? returnCode = null)
    {
        if (Enabled) Console.WriteLine(Describe(direction, kind, photonCode, parameters, encrypted, returnCode));
    }
}
