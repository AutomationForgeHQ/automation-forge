using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Core.Cloud;

/// <summary>
/// How a desktop signs in without ever seeing a password: listen on a loopback
/// port, open the account site's /connect page with that port and a one-time
/// state, and wait for the page — after the person says yes — to POST the
/// session back. A plain TCP listener speaking just enough HTTP, because
/// HttpListener wants a URL reservation on Windows and this must not.
/// </summary>
public static class Handshake
{
    public static async Task<StoredAccount?> SignInAsync(Action<string> openUrl, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!CloudConfig.Configured) throw new InvalidOperationException("Accounts are not configured in this build.");

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var origin = new Uri(CloudConfig.AppUrl).GetLeftPart(UriPartial.Authority);

        openUrl($"{CloudConfig.AppUrl.TrimEnd('/')}/connect/?port={port}&state={state}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                var (method, path, body) = await ReadRequestAsync(stream, cts.Token);
                if (method is null) continue;

                var cors = $"Access-Control-Allow-Origin: {origin}\r\nAccess-Control-Allow-Methods: POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nAccess-Control-Allow-Private-Network: true\r\nVary: Origin\r\n";
                if (method == "OPTIONS")
                {
                    await WriteAsync(stream, "204 No Content", cors, "", cts.Token);
                    continue;
                }
                if (method == "POST" && path.StartsWith("/connect", StringComparison.Ordinal))
                {
                    StoredAccount? account = null;
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("state", out var s) && s.GetString() == state)
                            account = new StoredAccount(
                                root.GetProperty("uid").GetString() ?? "",
                                root.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                                root.GetProperty("refreshToken").GetString() ?? "");
                    }
                    catch (JsonException) { }
                    if (account is null || account.Uid.Length == 0 || account.RefreshToken.Length == 0)
                    {
                        await WriteAsync(stream, "400 Bad Request", cors + "Content-Type: application/json\r\n", "{\"ok\":false}", cts.Token);
                        continue;
                    }
                    await WriteAsync(stream, "200 OK", cors + "Content-Type: application/json\r\n", "{\"ok\":true}", cts.Token);
                    return account;
                }
                await WriteAsync(stream, "404 Not Found", cors + "Content-Type: text/plain\r\n", "Automation Forge hub is listening for its sign-in.", cts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // nobody said yes in time
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<(string? method, string path, string body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var received = 0;
        int headerEnd;
        while (true)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(received), ct);
            if (n == 0) return (null, "", "");
            received += n;
            headerEnd = IndexOf(buffer, received, "\r\n\r\n"u8);
            if (headerEnd >= 0) break;
            if (received == buffer.Length) return (null, "", "");
        }
        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return (null, "", "");
        var length = 0;
        foreach (var line in lines.Skip(1))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line["Content-Length:".Length..].Trim(), out length);
        var bodyStart = headerEnd + 4;
        while (received - bodyStart < length && received < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(received), ct);
            if (n == 0) break;
            received += n;
        }
        var body = Encoding.UTF8.GetString(buffer, bodyStart, Math.Min(length, received - bodyStart));
        return (parts[0], parts[1], body);
    }

    private static int IndexOf(byte[] haystack, int count, ReadOnlySpan<byte> needle) =>
        haystack.AsSpan(0, count).IndexOf(needle);

    private static async Task WriteAsync(NetworkStream stream, string status, string headers, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\n{headers}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
}
