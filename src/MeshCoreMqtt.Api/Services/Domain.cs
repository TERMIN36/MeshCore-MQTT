using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MeshCoreMqtt.Api.Data;
using MeshCoreMqtt.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;

namespace MeshCoreMqtt.Api.Services;

public sealed class Access(AppDb db)
{
    public async Task<Privilege> OnSpace(Guid userId, Space space, CancellationToken ct)
    {
        if (space.Group is null)
            await db.Entry(space).Reference(s => s.Group).LoadAsync(ct);
        if (space.Group!.OwnerUserId == userId)
            return Privilege.All;

        var grants = await db.GrantTunnels.AsNoTracking()
            .Where(t => t.Grant.UserId == userId && t.SpaceId == space.Id)
            .Select(t => t.Privileges)
            .ToListAsync(ct);
        return grants.Aggregate(Privilege.None, (current, grant) => current | grant);
    }

    public async Task<Privilege> OnGroup(Guid userId, Group group, CancellationToken ct)
    {
        if (group.OwnerUserId == userId)
            return Privilege.All;
        var grants = await db.GrantTunnels.AsNoTracking()
            .Where(t => t.Grant.UserId == userId && t.Grant.GroupId == group.Id)
            .Select(t => t.Privileges)
            .ToListAsync(ct);
        return grants.Aggregate(Privilege.None, (current, grant) => current | grant);
    }

    public void Require(Privilege have, Privilege need)
    {
        if ((have & need) != need)
            throw new AppException(403, "Недостаточно прав");
    }

    public async Task<HashSet<Guid>> VisibleGroupIds(Guid userId, CancellationToken ct)
    {
        var owned = await db.Groups.Where(g => g.OwnerUserId == userId).Select(g => g.Id).ToListAsync(ct);
        var granted = await db.GrantTunnels.AsNoTracking()
            .Where(t => t.Grant.UserId == userId && t.Privileges != Privilege.None)
            .Select(t => t.Grant.GroupId)
            .ToListAsync(ct);
        return owned.Concat(granted).ToHashSet();
    }
}

public sealed class MqttGate(AppDb db, Passwords passwords, DecisionCache cache, IOptions<AppSecrets> secrets)
{
    public async Task<bool> Authenticate(string? username, string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username))
            return false;
        var key = "auth:" + username + ":" + password;
        if (cache.TryGet(key, out var cached))
            return cached;

        var ok = await AuthenticateUncached(username, password, ct);
        cache.Set(key, ok);
        return ok;
    }

    public Task<bool> IsSuperuser(string? username)
    {
        var ok = SecretText.FixedEquals(username, secrets.Value.Superuser);
        return Task.FromResult(ok);
    }

    public async Task<bool> Allow(string? username, string? topic, int acc, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(topic) || acc == 0)
            return false;
        if (SecretText.FixedEquals(username, secrets.Value.Superuser))
            return true;

        var key = $"acl:{username}:{topic}:{acc}";
        if (cache.TryGet(key, out var cached))
            return cached;

        var ok = await AllowUncached(username, topic, acc, ct);
        cache.Set(key, ok);
        return ok;
    }

    public async Task<RouteRow?> Route(string username, CancellationToken ct)
    {
        var device = await db.DeviceLogins.AsNoTracking()
            .Include(d => d.Space).ThenInclude(s => s.BrokerNode)
            .FirstOrDefaultAsync(d => d.Username == username, ct);
        if (device is null)
            return null;
        var node = device.Space.BrokerNode;
        return new RouteRow(node.Host, node.Port, node.UseTls, MeshTopics.Prefix(device.SpaceId));
    }

    async Task<bool> AuthenticateUncached(string username, string? password, CancellationToken ct)
    {
        if (SecretText.FixedEquals(username, secrets.Value.Superuser))
            return SecretText.FixedEquals(password, secrets.Value.SuperuserPassword);

        var device = await db.DeviceLogins.FirstOrDefaultAsync(d => d.Username == username, ct);
        return device is not null && password is not null && passwords.VerifyDevice(device, password);
    }

    async Task<bool> AllowUncached(string username, string topic, int acc, CancellationToken ct)
    {
        if (topic.StartsWith('$'))
            return false;

        var device = await db.DeviceLogins.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Username == username, ct);
        if (device is null)
            return false;

        var filters = new[] { MeshTopics.Filter(device.SpaceId) };
        if ((acc & 2) != 0 && (!device.CanPublish || !TopicRules.AllowsPublish(filters, topic)))
            return false;
        if ((acc & 5) != 0 && (!device.CanSubscribe || !TopicRules.AllowsSubscribe(filters, topic)))
            return false;

        return true;
    }
}

public sealed record RouteRow(string Host, int Port, bool Tls, string Prefix);

public sealed class ProxyNotifier(HttpClient http, IOptions<AppSecrets> secrets, ILogger<ProxyNotifier> log)
{
    public async Task Drop(IReadOnlyCollection<string> usernames, CancellationToken ct)
    {
        if (usernames.Count == 0 || string.IsNullOrWhiteSpace(secrets.Value.ProxyDropUrl))
            return;
        using var request = new HttpRequestMessage(HttpMethod.Post, secrets.Value.ProxyDropUrl);
        request.Headers.TryAddWithoutValidation("X-Internal-Token", secrets.Value.InternalToken);
        request.Content = JsonContent.Create(new { usernames });
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("Прокси не оборвал сессии: {Status}", (int)response.StatusCode);
            throw new AppException(502, "Размещение обновлено, но прокси не оборвал текущие соединения");
        }
    }

    public async Task ReloadCertificate(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(secrets.Value.ProxyReloadUrl))
            return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, secrets.Value.ProxyReloadUrl);
            request.Headers.TryAddWithoutValidation("X-Internal-Token", secrets.Value.InternalToken);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Прокси не подхватил сертификат: {Status}", (int)response.StatusCode);
                throw new AppException(502, "Сертификат выпущен, но прокси его ещё не подхватил");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "Прокси не подхватил сертификат");
            throw new AppException(502, "Сертификат выпущен, но прокси его ещё не подхватил");
        }
    }
}

public sealed class StatsCollector(
    IServiceScopeFactory scopes,
    StatsStore stats,
    IOptions<AppSecrets> secrets,
    ILogger<StatsCollector> log) : BackgroundService
{
    readonly Dictionary<Guid, CancellationTokenSource> _loops = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                    await stats.SaveMapAsync(db, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Карта не сохранилась");
                }

                try
                {
                    await Sync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Не удалось обновить сборщики статистики");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            await stats.SaveMapAsync(db, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Карта не сохранилась при остановке");
        }

        foreach (var loop in _loops.Values)
            loop.Cancel();
    }

    async Task Sync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var nodes = await db.BrokerNodes.AsNoTracking().Where(n => n.Status != NodeStatus.Offline).ToListAsync(ct);
        var live = nodes.Select(n => n.Id).ToHashSet();
        foreach (var gone in _loops.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _loops[gone].Cancel();
            _loops.Remove(gone);
            stats.SetReachable(gone, false, "Узел выведен из опроса");
        }

        foreach (var node in nodes)
        {
            if (_loops.ContainsKey(node.Id))
                continue;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _loops[node.Id] = linked;
            _ = RunNode(node.Id, node.Host, node.Port, node.UseTls, linked.Token);
        }
    }

    async Task RunNode(Guid nodeId, string host, int port, bool useTls, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var factory = new MqttFactory();
                using var client = factory.CreateMqttClient();
                client.ApplicationMessageReceivedAsync += args =>
                {
                    var topic = args.ApplicationMessage.Topic;
                    if (topic.EndsWith("/clients/connected", StringComparison.Ordinal) &&
                        topic.Contains("$SYS/", StringComparison.Ordinal) &&
                        int.TryParse(Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment), out var connections))
                        stats.SetConnections(nodeId, connections);
                    stats.RecordMessage(nodeId, topic, args.ApplicationMessage.PayloadSegment);
                    return Task.CompletedTask;
                };

                var builder = new MqttClientOptionsBuilder()
                    .WithTcpServer(host, port)
                    .WithClientId($"stats-{nodeId:N}")
                    .WithCredentials(secrets.Value.Superuser, secrets.Value.SuperuserPassword)
                    .WithCleanSession();
                if (useTls)
                    builder.WithTlsOptions(options =>
                    {
                        options.UseTls();
                        options.WithCertificateValidationHandler(context =>
                            ValidateCertificate(context.Certificate, secrets.Value.CertsDirectory));
                    });

                await client.ConnectAsync(builder.Build(), ct);
                await client.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic("#").Build(), ct);
                stats.SetReachable(nodeId, true, null);
                while (client.IsConnected && !ct.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stats.SetReachable(nodeId, false, ex.Message);
                log.LogDebug(ex, "Сборщик не подключился к узлу {Node}", nodeId);
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    public static async Task<bool> Probe(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool ValidateCertificate(X509Certificate certificate, string certsDirectory)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        var caPath = Path.Combine(certsDirectory, "ca.crt");
        if (File.Exists(caPath))
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(new X509Certificate2(caPath));
        }

        return chain.Build(new X509Certificate2(certificate));
    }
}
