using System.Net;
using System.Net.Sockets;
using System.Text;
using AlbionLocal.Photon;

namespace AlbionLocal.Server.Web;

/// <summary>One parsed HTTP request: method, path, query, headers (lower-case names), cookies and the urlencoded form of a POST.</summary>
public sealed class WebRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required IPAddress Remote { get; init; }
    public string Body { get; init; } = "";
    public string ContentType { get; init; } = "";
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Form { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Cookies { get; init; } = new Dictionary<string, string>();

    public string Q(string key) => Query.TryGetValue(key, out var v) ? v : "";
    public string F(string key) => Form.TryGetValue(key, out var v) ? v : "";
}

/// <summary>An answer: status, content type, body and extra headers (Set-Cookie, Location).</summary>
public sealed record WebResponse(int Status, string Body, string ContentType = "text/html; charset=utf-8")
{
    public List<(string Name, string Value)> Headers { get; } = [];

    public static WebResponse Redirect(string to)
    {
        var r = new WebResponse(303, "");
        r.Headers.Add(("Location", to));
        return r;
    }
}

/// <summary>
/// A minimal HTTP/1.1 server on a <see cref="TcpListener"/> (no http.sys URL reservation, no package): one request per
/// connection (Connection: close), a 16 KB head, a 64 KB urlencoded body, 10 s to send it, at most 32 connections at once.
/// Enough for the panel's few pages; request bodies are never logged (they carry passwords).
/// </summary>
public sealed class MiniHttp : IDisposable
{
    private const int MaxHead = 16 * 1024, MaxBody = 64 * 1024, MaxConnections = 32;
    private readonly TcpListener _listener;
    private readonly Func<WebRequest, Task<WebResponse>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections);

    public MiniHttp(IPEndPoint endPoint, Func<WebRequest, Task<WebResponse>> handler)
    {
        _handler = handler;
        _listener = new TcpListener(endPoint);
        _listener.Start(); // a port in use throws here (SocketException)
        EndPoint = (IPEndPoint)_listener.LocalEndpoint;
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>The bound endpoint (with the real port when 0 was asked for).</summary>
    public IPEndPoint EndPoint { get; }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (SocketException e)
            {
                Log.Debug("web", $"accept: {e.Message}");
                continue;
            }
            if (!_slots.Wait(0))
            {
                client.Dispose(); // too many at once
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await Serve(client); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                catch (Exception e) { Log.Error("web", $"request failed: {e}"); }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            });
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stream = client.GetStream();
        var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var request = await Read(stream, remote, timeout.Token);
        var response = request is null ? new WebResponse(400, "bad request", "text/plain; charset=utf-8") : await _handler(request);
        await Write(stream, response, timeout.Token);
    }

    private static async Task<WebRequest?> Read(NetworkStream stream, IPAddress remote, CancellationToken ct)
    {
        var buffer = new byte[MaxHead + MaxBody];
        int filled = 0, headEnd = -1;
        while (headEnd < 0)
        {
            if (filled >= MaxHead) return null;
            var n = await stream.ReadAsync(buffer.AsMemory(filled, MaxHead - filled), ct);
            if (n == 0) return null;
            filled += n;
            headEnd = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
        }
        var head = Encoding.ASCII.GetString(buffer, 0, headEnd).Split("\r\n");
        var start = head[0].Split(' ');
        if (start.Length != 3 || !start[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Skip(1))
            if (line.IndexOf(':') is > 0 and var colon) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, out length) || length is < 0 or > MaxBody)) return null;
        if (headers.ContainsKey("Transfer-Encoding")) return null; // no chunked bodies: the forms send a length
        var bodyStart = headEnd + 4;
        while (filled < bodyStart + length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(filled, bodyStart + length - filled), ct);
            if (n == 0) return null;
            filled += n;
        }
        var target = start[1];
        var q = target.IndexOf('?');
        var path = Uri.UnescapeDataString(q < 0 ? target : target[..q]);
        var contentType = headers.TryGetValue("Content-Type", out var contentHeader) ? contentHeader : "";
        var body = Encoding.UTF8.GetString(buffer, bodyStart, length);
        var form = contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            ? Urlencoded(body) : new Dictionary<string, string>();
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (headers.TryGetValue("Cookie", out var cookie))
            foreach (var part in cookie.Split(';'))
                if (part.IndexOf('=') is > 0 and var eq) cookies[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        return new WebRequest
        {
            Method = start[0].ToUpperInvariant(), Path = path, Remote = remote, Body = body, ContentType = contentType,
            Headers = headers, Cookies = cookies, Form = form,
            Query = q < 0 ? new Dictionary<string, string>() : Urlencoded(target[(q + 1)..]),
        };
    }

    /// <summary>a=1&amp;b=x+y → {a: 1, b: "x y"}; the first of a repeated key wins.</summary>
    public static Dictionary<string, string> Urlencoded(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]);
            result.TryAdd(key, eq < 0 ? "" : WebUtility.UrlDecode(pair[(eq + 1)..]));
        }
        return result;
    }

    private static async Task Write(NetworkStream stream, WebResponse r, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(r.Body);
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\n")
            .Append($"Content-Type: {r.ContentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n")
            .Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: same-origin\r\nX-Frame-Options: DENY\r\n")
            .Append("Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'\r\n");
        foreach (var (name, value) in r.Headers) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct);
        await stream.WriteAsync(body, ct);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 303 => "See Other", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 409 => "Conflict", 423 => "Locked", 429 => "Too Many Requests", 503 => "Service Unavailable", _ => "Error",
    };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
