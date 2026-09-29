using System.Security.Cryptography;
using System.Text;
using MeshCoreMqtt.Api.Data;
using Microsoft.AspNetCore.Identity;

namespace MeshCoreMqtt.Api.Services;

public sealed class AppSecrets
{
    public string JwtKey { get; set; } = "";
    public string JwtIssuer { get; set; } = "meshcore";
    public string InternalToken { get; set; } = "";
    public string AdminEmail { get; set; } = "";
    public string AdminPassword { get; set; } = "";
    public string Superuser { get; set; } = "stats";
    public string SuperuserPassword { get; set; } = "";
    public string PublicHost { get; set; } = "localhost";
    public int PublicPort { get; set; } = 8883;
    public string CertsDirectory { get; set; } = "certs";
    public string ProxyDropUrl { get; set; } = "";
    public string ProxyReloadUrl { get; set; } = "";
}

public sealed class AppException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public static class PrivilegeText
{
    static readonly (Privilege Flag, string Name)[] Map =
    [
        (Privilege.View, "view"),
        (Privilege.Subscribe, "subscribe"),
        (Privilege.Publish, "publish"),
        (Privilege.Credentials, "credentials"),
        (Privilege.Topics, "topics"),
        (Privilege.Access, "access")
    ];

    public static string[] ToNames(Privilege privileges) =>
        Map.Where(item => privileges.HasFlag(item.Flag)).Select(item => item.Name).ToArray();

    public static Privilege Parse(IEnumerable<string>? names)
    {
        var value = Privilege.None;
        foreach (var name in names ?? [])
        {
            var match = Map.FirstOrDefault(item => item.Name == name);
            if (match.Name is null)
                throw new AppException(400, $"Неизвестная привилегия: {name}");
            value |= match.Flag;
        }

        if (value == Privilege.None)
            throw new AppException(400, "Нужна хотя бы одна привилегия");
        return value;
    }
}

public static class SecretText
{
    const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Username() => Take(16);

    public static string Password() => $"{Take(4)}-{Take(4)}-{Take(4)}-{Take(4)}";

    public static bool FixedEquals(string? left, string? right)
    {
        var a = Encoding.UTF8.GetBytes(left ?? "");
        var b = Encoding.UTF8.GetBytes(right ?? "");
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    static string Take(int count)
    {
        var bytes = RandomNumberGenerator.GetBytes(count);
        var chars = new char[count];
        for (var i = 0; i < count; i++)
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        return new string(chars);
    }
}

public sealed class Passwords
{
    readonly PasswordHasher<UserAccount> _users = new();
    readonly PasswordHasher<DeviceLogin> _devices = new();

    public string HashUser(UserAccount user, string password) => _users.HashPassword(user, password);

    public bool VerifyUser(UserAccount user, string password) =>
        _users.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    public string HashDevice(DeviceLogin device, string password) => _devices.HashPassword(device, password);

    public bool VerifyDevice(DeviceLogin device, string password) =>
        _devices.VerifyHashedPassword(device, device.PasswordHash, password) != PasswordVerificationResult.Failed;
}

public sealed class DecisionCache
{
    readonly Dictionary<string, (long Epoch, DateTime Expires, bool Ok)> _items = new();
    long _epoch;

    public void Invalidate() => Interlocked.Increment(ref _epoch);

    public bool TryGet(string key, out bool ok)
    {
        ok = false;
        lock (_items)
        {
            var epoch = Interlocked.Read(ref _epoch);
            if (!_items.TryGetValue(key, out var item) || item.Epoch != epoch || item.Expires < DateTime.UtcNow)
                return false;
            ok = item.Ok;
            return true;
        }
    }

    public void Set(string key, bool ok)
    {
        lock (_items)
        {
            if (_items.Count > 10000)
                _items.Clear();
            _items[key] = (Interlocked.Read(ref _epoch), DateTime.UtcNow.AddSeconds(2), ok);
        }
    }
}

public sealed class HitWindow
{
    readonly Queue<long> _hits = new();
    public DateTime LastSeen { get; private set; }

    public void Hit(long nowMs)
    {
        lock (_hits)
        {
            _hits.Enqueue(nowMs);
            Prune(nowMs);
            LastSeen = DateTime.UtcNow;
        }
    }

    public int Count(long nowMs)
    {
        lock (_hits)
        {
            Prune(nowMs);
            return _hits.Count;
        }
    }

    void Prune(long nowMs)
    {
        while (_hits.Count > 0 && nowMs - _hits.Peek() > 60_000)
            _hits.Dequeue();
    }
}

public sealed class NodeRuntime
{
    public bool Reachable { get; set; }
    public string? Error { get; set; }
    public int Connections { get; set; }
    public HitWindow Messages { get; } = new();
}

public sealed class TopicRuntime
{
    public HitWindow Messages { get; } = new();
}

public sealed class StatsStore
{
    readonly Dictionary<Guid, NodeRuntime> _nodes = new();
    readonly Dictionary<(Guid NodeId, string Topic), TopicRuntime> _topics = new();

    public void RecordMessage(Guid nodeId, string topic)
    {
        if (topic.StartsWith("$SYS/", StringComparison.Ordinal))
            return;
        var now = Now();
        lock (_nodes)
        {
            Node(nodeId).Messages.Hit(now);
            var key = (nodeId, topic);
            if (!_topics.ContainsKey(key) && _topics.Count >= 20000)
                return;
            if (!_topics.TryGetValue(key, out var runtime))
            {
                runtime = new TopicRuntime();
                _topics[key] = runtime;
            }

            runtime.Messages.Hit(now);
        }
    }

    public void SetConnections(Guid nodeId, int count)
    {
        lock (_nodes)
            Node(nodeId).Connections = count;
    }

    public void SetReachable(Guid nodeId, bool reachable, string? error)
    {
        lock (_nodes)
        {
            var node = Node(nodeId);
            node.Reachable = reachable;
            node.Error = error;
        }
    }

    public int MessagesPerMinute(Guid nodeId)
    {
        lock (_nodes)
            return _nodes.TryGetValue(nodeId, out var node) ? node.Messages.Count(Now()) : 0;
    }

    public object NodeView(Guid nodeId)
    {
        lock (_nodes)
        {
            if (!_nodes.TryGetValue(nodeId, out var node))
                return new { reachable = false, error = (string?)null, connections = 0, messagesPerMinute = 0 };
            return new
            {
                reachable = node.Reachable,
                error = node.Error,
                connections = node.Connections,
                messagesPerMinute = node.Messages.Count(Now())
            };
        }
    }

    public IReadOnlyList<TopicRow> Topics()
    {
        var now = Now();
        lock (_nodes)
        {
            return _topics.Select(pair => new TopicRow(
                pair.Key.NodeId,
                pair.Key.Topic,
                pair.Value.Messages.Count(now),
                pair.Value.Messages.LastSeen)).ToList();
        }
    }

    NodeRuntime Node(Guid nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node))
        {
            node = new NodeRuntime();
            _nodes[nodeId] = node;
        }

        return node;
    }

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed record TopicRow(Guid NodeId, string Topic, int MessagesPerMinute, DateTime LastSeen);
