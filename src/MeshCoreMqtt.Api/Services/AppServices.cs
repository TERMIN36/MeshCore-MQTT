using System.Security.Cryptography;
using System.Text;
using MeshCoreMqtt.Api.Data;
using MeshCoreMqtt.Core;
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

public sealed class StatsStore(LiveHub live)
{
    readonly LiveHub _live = live;
    readonly Dictionary<Guid, NodeRuntime> _nodes = new();
    readonly Dictionary<(Guid NodeId, string Topic), TopicRuntime> _topics = new();
    readonly Dictionary<(Guid NodeId, string PublicKey), RepeaterState> _repeaters = new();
    readonly Dictionary<Guid, Queue<FeedRow>> _feed = new();

    public void RecordMessage(Guid nodeId, string topic, ReadOnlySpan<byte> payload)
    {
        if (topic.StartsWith("$SYS/", StringComparison.Ordinal))
            return;
        BridgeMessage? parsed = null;
        string summary = "";
        if (payload.Length is > 0 and <= BridgeFrames.MaxBytes && BridgeFrames.TryDecode(payload, out var message))
        {
            parsed = message;
            summary = BridgeFrames.Describe(message);
        }
        else if (payload.Length > 0)
            summary = "конверт не разобран";

        var now = Now();
        var seen = DateTime.UtcNow;
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
            if (parsed is not null)
                Remember(nodeId, topic, parsed, seen);
            if (summary.Length > 0)
                RememberFeed(nodeId, topic, parsed, summary, seen);
        }

        _live.MarkChanged();
    }

    public void SetConnections(Guid nodeId, int count)
    {
        lock (_nodes)
            Node(nodeId).Connections = count;
        _live.MarkChanged();
    }

    public void SetReachable(Guid nodeId, bool reachable, string? error)
    {
        lock (_nodes)
        {
            var node = Node(nodeId);
            node.Reachable = reachable;
            node.Error = error;
        }

        _live.MarkChanged();
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

    public IReadOnlyList<RepeaterLive> Repeaters()
    {
        lock (_nodes)
            return _repeaters.Values.Select(state => state.View()).ToList();
    }

    public IReadOnlyList<FeedRow> Feed()
    {
        lock (_nodes)
            return _feed.SelectMany(pair => pair.Value).ToList();
    }

    void Remember(Guid nodeId, string topic, BridgeMessage message, DateTime seen)
    {
        if (_repeaters.Count >= 2000 && !_repeaters.ContainsKey((nodeId, message.PublicKey)))
            return;
        if (!_repeaters.TryGetValue((nodeId, message.PublicKey), out var state))
        {
            state = new RepeaterState();
            _repeaters[(nodeId, message.PublicKey)] = state;
        }

        state.NodeId = nodeId;
        state.Topic = topic;
        state.PublicKey = message.PublicKey;
        if (message.Name.Length > 0)
            state.Name = message.Name;
        state.LastSeen = seen;
        if (message.HasTime)
            state.Clock = DateTimeOffset.FromUnixTimeSeconds(message.UnixTime).UtcDateTime;
        if (message.Hello is { } hello)
        {
            state.FrequencyHz = hello.FrequencyHz;
            state.BandwidthHz = hello.BandwidthHz;
            state.SpreadingFactor = hello.SpreadingFactor;
            state.CodingRate = hello.CodingRate;
            state.TxDbm = hello.TxDbm;
            state.AntennaCm = hello.AntennaCm;
            state.Forwarding = hello.Forwarding;
            state.HelloLatitude = hello.Latitude;
            state.HelloLongitude = hello.Longitude;
            state.PacketsPublished = hello.PacketsPublished;
            state.PacketsInbound = hello.PacketsInbound;
            state.Duplicates = hello.Duplicates;
            state.PublishErrors = hello.PublishErrors;
            state.ApplyTelemetry(hello.NoiseFloor, hello.TxAirSecs, hello.RxAirSecs, hello.UptimeSecs, hello.TxQueue, hello.BatteryMv, hello.TempCx10, hello.Firmware);
        }

        if (message.Heartbeat is { } beat)
        {
            state.PacketsPublished = beat.PacketsPublished;
            state.PacketsInbound = beat.PacketsInbound;
            state.Duplicates = beat.Duplicates;
            state.PublishErrors = beat.PublishErrors;
            state.ApplyTelemetry(beat.NoiseFloor, beat.TxAirSecs, beat.RxAirSecs, beat.UptimeSecs, beat.TxQueue, beat.BatteryMv, beat.TempCx10, beat.Firmware);
            if (beat.Latitude is { } lat && beat.Longitude is { } lon)
            {
                state.HelloLatitude = lat;
                state.HelloLongitude = lon;
            }
        }

        if (message.Packet?.Advert is { } advert &&
            string.Equals(advert.PublicKey, message.PublicKey, StringComparison.OrdinalIgnoreCase))
        {
            state.AdvertType = advert.Type;
            state.AdvertName = advert.Name;
            state.AdvertLatitude = advert.Latitude;
            state.AdvertLongitude = advert.Longitude;
            state.AdvertAt = advert.Timestamp == 0
                ? seen
                : DateTimeOffset.FromUnixTimeSeconds(advert.Timestamp).UtcDateTime;
        }
    }

    void RememberFeed(Guid nodeId, string topic, BridgeMessage? message, string summary, DateTime seen)
    {
        if (!_feed.TryGetValue(nodeId, out var queue))
        {
            queue = new Queue<FeedRow>();
            _feed[nodeId] = queue;
        }

        queue.Enqueue(new FeedRow(
            nodeId,
            topic,
            seen,
            message?.PublicKey ?? "",
            message?.Name ?? "",
            summary));
        while (queue.Count > 80)
            queue.Dequeue();
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

public sealed record FeedRow(Guid NodeId, string Topic, DateTime Seen, string PublicKey, string Name, string Summary);

public sealed class RepeaterState
{
    public Guid NodeId { get; set; }
    public string Topic { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastSeen { get; set; }
    public DateTime? Clock { get; set; }
    public int? AdvertType { get; set; }
    public string? AdvertName { get; set; }
    public double? AdvertLatitude { get; set; }
    public double? AdvertLongitude { get; set; }
    public DateTime? AdvertAt { get; set; }
    public uint? FrequencyHz { get; set; }
    public uint? BandwidthHz { get; set; }
    public byte? SpreadingFactor { get; set; }
    public byte? CodingRate { get; set; }
    public int? TxDbm { get; set; }
    public int? AntennaCm { get; set; }
    public bool? Forwarding { get; set; }
    public double? HelloLatitude { get; set; }
    public double? HelloLongitude { get; set; }
    public uint? PacketsPublished { get; set; }
    public uint? PacketsInbound { get; set; }
    public uint? Duplicates { get; set; }
    public uint? PublishErrors { get; set; }
    public short? NoiseFloor { get; set; }
    public uint? TxAirSecs { get; set; }
    public uint? RxAirSecs { get; set; }
    public uint? UptimeSecs { get; set; }
    public uint? TxQueue { get; set; }
    public ushort? BatteryMv { get; set; }
    public short? TempCx10 { get; set; }
    public string? Firmware { get; set; }

    public void ApplyTelemetry(short noise, uint txAir, uint rxAir, uint uptime, uint queue, ushort batteryMv, short tempCx10, string firmware)
    {
        NoiseFloor = noise;
        TxAirSecs = txAir;
        RxAirSecs = rxAir;
        UptimeSecs = uptime;
        TxQueue = queue;
        BatteryMv = batteryMv;
        TempCx10 = tempCx10;
        Firmware = firmware;
    }

    public RepeaterLive View()
    {
        var fromAdvert = AdvertLatitude is not null && AdvertLongitude is not null;
        return new RepeaterLive(
            NodeId,
            Topic,
            PublicKey,
            Name,
            LastSeen,
            Clock,
            fromAdvert ? AdvertLatitude : HelloLatitude,
            fromAdvert ? AdvertLongitude : HelloLongitude,
            fromAdvert,
            AdvertType,
            AdvertName,
            AdvertAt,
            FrequencyHz,
            BandwidthHz,
            SpreadingFactor,
            CodingRate,
            TxDbm,
            AntennaCm,
            Forwarding,
            PacketsPublished,
            PacketsInbound,
            Duplicates,
            PublishErrors,
            NoiseFloor,
            TxAirSecs,
            RxAirSecs,
            UptimeSecs,
            TxQueue,
            BatteryMv,
            TempCx10,
            Firmware);
    }
}

public sealed record RepeaterLive(
    Guid NodeId,
    string Topic,
    string PublicKey,
    string Name,
    DateTime LastSeen,
    DateTime? Clock,
    double? Latitude,
    double? Longitude,
    bool LocationFromAdvert,
    int? AdvertType,
    string? AdvertName,
    DateTime? AdvertAt,
    uint? FrequencyHz,
    uint? BandwidthHz,
    byte? SpreadingFactor,
    byte? CodingRate,
    int? TxDbm,
    int? AntennaCm,
    bool? Forwarding,
    uint? PacketsPublished,
    uint? PacketsInbound,
    uint? Duplicates,
    uint? PublishErrors,
    short? NoiseFloor,
    uint? TxAirSecs,
    uint? RxAirSecs,
    uint? UptimeSecs,
    uint? TxQueue,
    ushort? BatteryMv,
    short? TempCx10,
    string? Firmware);
