using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MeshCoreMqtt.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RouteTable>();
builder.Services.AddSingleton<Sessions>();
builder.Services.AddSingleton<ServerCertificate>();
builder.Services.AddHostedService<MqttFront>();
var app = builder.Build();
var token = builder.Configuration["Internal:Token"] ?? "";

app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapPost("/internal/drop", (DropBody body, Sessions sessions, RouteTable routes, HttpRequest request) =>
{
    if (!InternalTokenOk(request, token))
        return Results.Unauthorized();
    var usernames = body.Usernames ?? [];
    routes.Forget(usernames);
    sessions.Drop(usernames);
    return Results.NoContent();
});
app.MapPost("/internal/reload-cert", (ServerCertificate certificate, HttpRequest request) =>
{
    if (!InternalTokenOk(request, token))
        return Results.Unauthorized();
    certificate.Reload();
    return Results.NoContent();
});
app.Run();

static bool InternalTokenOk(HttpRequest request, string token)
{
    var header = request.Headers["X-Internal-Token"].ToString();
    var a = System.Text.Encoding.UTF8.GetBytes(header);
    var b = System.Text.Encoding.UTF8.GetBytes(token);
    return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
}

sealed class ServerCertificate
{
    readonly string _directory;
    X509Certificate2 _current;

    public ServerCertificate(IConfiguration configuration)
    {
        _directory = configuration["Certs:Directory"] ?? "certs";
        var host = CertificateFiles.ReadHost(_directory) ?? configuration["Mqtt:PublicHost"] ?? "localhost";
        CertificateFiles.Ensure(_directory, host);
        _current = CertificateFiles.LoadServerCertificate(_directory);
    }

    public X509Certificate2 Current => Volatile.Read(ref _current);

    public void Reload()
    {
        var next = CertificateFiles.LoadServerCertificate(_directory);
        Interlocked.Exchange(ref _current, next);
    }
}

sealed record DropBody(string[]? Usernames);

sealed class Sessions
{
    readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action>> _items = new(StringComparer.Ordinal);

    public IDisposable Track(string username, Action close)
    {
        var id = Guid.NewGuid();
        var bucket = _items.GetOrAdd(username, _ => new ConcurrentDictionary<Guid, Action>());
        bucket[id] = close;
        return new Removal(() =>
        {
            if (_items.TryGetValue(username, out var found))
                found.TryRemove(id, out _);
        });
    }

    public void Drop(IEnumerable<string> usernames)
    {
        foreach (var username in usernames)
        {
            if (!_items.TryRemove(username, out var bucket))
                continue;
            foreach (var close in bucket.Values)
            {
                try { close(); } catch { /* сокет уже закрыт */ }
            }
        }
    }

    sealed class Removal(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

sealed record Route(string Host, int Port, bool Tls, string? Prefix);

sealed class RouteTable
{
    readonly ConcurrentDictionary<string, (DateTime Expires, Route? Route)> _routes = new(StringComparer.Ordinal);

    public bool TryGet(string username, out Route? route)
    {
        route = null;
        if (!_routes.TryGetValue(username, out var cached) || cached.Expires <= DateTime.UtcNow)
            return false;
        route = cached.Route;
        return true;
    }

    public void Set(string username, Route? route) =>
        _routes[username] = (DateTime.UtcNow.AddSeconds(1), route);

    public void Forget(IEnumerable<string> usernames)
    {
        foreach (var username in usernames)
            _routes.TryRemove(username, out _);
    }
}

sealed class MqttFront(IConfiguration configuration, Sessions sessions, RouteTable routes, ServerCertificate certificate, ILogger<MqttFront> log) : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = int.TryParse(configuration["Mqtt:Port"], out var parsed) ? parsed : 8883;
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        log.LogInformation("MQTT TLS слушает {Port}", port);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Accept(client, certificate.Current, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    async Task Accept(TcpClient client, X509Certificate2 certificate, CancellationToken stoppingToken)
    {
        using var tcp = client;
        try
        {
            await using var ssl = new SslStream(client.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }, stoppingToken);

            var packet = await ReadConnect(ssl, stoppingToken);
            if (!ConnectReader.TryReadUsername(packet, out var username, out var length, out _, out var protocolLevel) || string.IsNullOrEmpty(username))
            {
                log.LogInformation("CONNECT без логина");
                return;
            }

            var route = await Resolve(username, stoppingToken);
            if (route is null || string.IsNullOrEmpty(route.Prefix))
            {
                log.LogInformation("Неизвестный логин");
                return;
            }

            using var backendTcp = new TcpClient();
            await backendTcp.ConnectAsync(route.Host, route.Port, stoppingToken);
            Stream backend = backendTcp.GetStream();
            if (route.Tls)
            {
                var secure = new SslStream(backend, false, (_, _, _, _) => true);
                await secure.AuthenticateAsClientAsync(route.Host);
                backend = secure;
            }

            await using var backendStream = backend;
            var connectStatus = TunnelFrames.TryRewrite(packet.AsSpan(0, length), true, route.Prefix, protocolLevel, out var connect, out _);
            if (connectStatus != FrameStatus.Ok || connect.Length == 0)
            {
                log.LogInformation("CONNECT не разобран");
                return;
            }

            await backend.WriteAsync(connect, stoppingToken);
            using var track = sessions.Track(username, () =>
            {
                try { client.Close(); } catch { /* уже закрыт */ }
                try { backendTcp.Close(); } catch { /* уже закрыт */ }
            });
            var prefix = route.Prefix;
            var left = Pump(ssl, backend, true, prefix, protocolLevel, stoppingToken);
            var right = Pump(backend, ssl, false, prefix, protocolLevel, stoppingToken);
            await Task.WhenAny(left, right);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogDebug(ex, "Соединение закрыто");
        }
    }

    async Task<Route?> Resolve(string username, CancellationToken ct)
    {
        if (routes.TryGet(username, out var cached))
            return cached;

        var api = configuration["Api:Url"] ?? "http://api:8080";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{api.TrimEnd('/')}/internal/mqtt/route?username={Uri.EscapeDataString(username)}");
        request.Headers.TryAddWithoutValidation("X-Internal-Token", configuration["Internal:Token"]);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await http.SendAsync(request, ct);
        Route? route = null;
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<Route>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            route = body;
        }

        routes.Set(username, route);
        return route;
    }

    static async Task<byte[]> ReadConnect(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length < ConnectReader.MaxPacketBytes)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
            var data = buffer.ToArray();
            if (ConnectReader.TryReadUsername(data, out _, out _, out var needMore) || !needMore)
                return data;
        }

        return buffer.ToArray();
    }

    static async Task Pump(Stream from, Stream to, bool toBroker, string prefix, byte protocolLevel, CancellationToken ct)
    {
        var pending = new byte[ConnectReader.MaxPacketBytes + 8];
        var size = 0;
        var chunk = new byte[8192];
        while (true)
        {
            var read = await from.ReadAsync(chunk, ct);
            if (read == 0)
                return;
            if (size + read > pending.Length)
                throw new IOException("Слишком большой MQTT-пакет");
            chunk.AsSpan(0, read).CopyTo(pending.AsSpan(size));
            size += read;

            var offset = 0;
            while (offset < size)
            {
                var status = TunnelFrames.TryRewrite(pending.AsSpan(offset, size - offset), toBroker, prefix, protocolLevel, out var output, out var consumed);
                if (status == FrameStatus.NeedMore)
                    break;
                if (status != FrameStatus.Ok || consumed <= 0)
                    throw new IOException("Некорректный MQTT-пакет");
                if (output.Length > 0)
                    await to.WriteAsync(output, ct);
                offset += consumed;
            }

            if (offset == 0)
                continue;
            Buffer.BlockCopy(pending, offset, pending, 0, size - offset);
            size -= offset;
        }
    }
}
