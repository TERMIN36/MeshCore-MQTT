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

public sealed partial class StatsStore(LiveHub live)
{
    public static readonly TimeSpan MapKeep = TimeSpan.FromDays(30);
    const int MapNodeCap = 8000;
    const int MapLinkCap = 12000;

    readonly LiveHub _live = live;
    readonly Dictionary<Guid, NodeRuntime> _nodes = new();
    readonly Dictionary<(Guid NodeId, string Topic), TopicRuntime> _topics = new();
    readonly Dictionary<(Guid NodeId, string PublicKey), RepeaterState> _repeaters = new();
    readonly Dictionary<(Guid NodeId, string Tunnel, string PublicKey), MapNode> _mapNodes = new();
    readonly Dictionary<(Guid NodeId, string Tunnel, string From, string To), DateTime> _mapLinks = new();
    readonly HashSet<(Guid NodeId, string Tunnel, string PublicKey)> _dirtyNodes = new();
    readonly HashSet<(Guid NodeId, string Tunnel, string From, string To)> _dirtyLinks = new();
    readonly Dictionary<Guid, Queue<FeedRow>> _feed = new();
    DateTime _mapPruned = DateTime.MinValue;

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

    public TunnelMap Map(Guid nodeId, string tunnel)
    {
        var cutoff = DateTime.UtcNow - MapKeep;
        lock (_nodes)
        {
            var known = new Dictionary<string, MapPlace>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _mapNodes)
            {
                if (pair.Key.NodeId != nodeId || pair.Key.Tunnel != tunnel || pair.Value.Seen < cutoff)
                    continue;
                var node = pair.Value;
                known[pair.Key.PublicKey] = new MapPlace(
                    pair.Key.PublicKey,
                    node.Name,
                    node.Latitude,
                    node.Longitude,
                    node.Repeater,
                    node.Mqtt,
                    node.Seen);
            }

            var edges = new Dictionary<string, (string From, string To, DateTime Seen)>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _mapLinks)
            {
                if (pair.Key.NodeId != nodeId || pair.Key.Tunnel != tunnel || pair.Value < cutoff)
                    continue;
                var from = Resolve(pair.Key.From, known.Keys);
                var to = Resolve(pair.Key.To, known.Keys);
                if (from is null || to is null || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Placed(known, from) || !Placed(known, to))
                    continue;
                var key = string.Compare(from, to, StringComparison.OrdinalIgnoreCase) < 0 ? from + "\n" + to : to + "\n" + from;
                if (edges.TryGetValue(key, out var previous) && previous.Seen >= pair.Value)
                    continue;
                edges[key] = (from, to, pair.Value);
            }

            return new TunnelMap(
                known.Values.Where(Placed).ToList(),
                edges.Values.Select(edge => new MapHop(edge.From, edge.To, edge.Seen)).ToList(),
                known.Values.Where(place => !Placed(place))
                    .OrderByDescending(place => place.Seen)
                    .Take(24)
                    .Select(place => new MapName(
                        place.Name.Length > 0 ? place.Name : place.PublicKey[..Math.Min(8, place.PublicKey.Length)],
                        place.Seen))
                    .ToList());
        }
    }

    void Remember(Guid nodeId, string topic, BridgeMessage message, DateTime seen)
    {
        var onMap = MeshTopics.TryPrefix(topic, out var tunnel);
        if (onMap)
        {
            NoteAdvert(nodeId, tunnel, message, seen);
            NoteDiscovered(nodeId, tunnel, message, seen);
            NotePath(nodeId, tunnel, message, seen);
        }

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
            state.ApplyTelemetry(hello.NoiseFloor, hello.TxAirSecs, hello.RxAirSecs, hello.UptimeSecs, hello.TxQueue, hello.BatteryMv, hello.TempCx10, hello.Firmware, hello.Environment);
        }

        if (message.Heartbeat is { } beat)
        {
            state.PacketsPublished = beat.PacketsPublished;
            state.PacketsInbound = beat.PacketsInbound;
            state.Duplicates = beat.Duplicates;
            state.PublishErrors = beat.PublishErrors;
            state.ApplyTelemetry(beat.NoiseFloor, beat.TxAirSecs, beat.RxAirSecs, beat.UptimeSecs, beat.TxQueue, beat.BatteryMv, beat.TempCx10, beat.Firmware, beat.Environment);
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

        if (onMap)
            NoteMqtt(nodeId, tunnel, state);
    }

    void NoteMqtt(Guid nodeId, string tunnel, RepeaterState state)
    {
        if (state.PublicKey.Length == 0)
            return;
        var node = EnsureNode(nodeId, tunnel, state.PublicKey);
        var place = PlaceFrom(state);
        if (place.Name.Length > 0)
            node.Name = Clip(place.Name, 120);
        if (place.Latitude is not null && place.Longitude is not null)
        {
            node.Latitude = place.Latitude;
            node.Longitude = place.Longitude;
        }

        node.Repeater = true;
        node.Mqtt = true;
        node.Seen = state.LastSeen;
    }

    void NoteAdvert(Guid nodeId, string tunnel, BridgeMessage message, DateTime seen)
    {
        if (message.Packet?.Advert is not { } advert || advert.PublicKey.Length == 0)
            return;
        var node = EnsureNode(nodeId, tunnel, advert.PublicKey);
        if (advert.Name.Length > 0)
            node.Name = Clip(advert.Name, 120);
        if (advert.Latitude is { } lat && advert.Longitude is { } lon && (!node.Mqtt || node.Latitude is null))
        {
            node.Latitude = lat;
            node.Longitude = lon;
        }

        if (!node.Mqtt)
            node.Repeater = advert.Type is 2 or 3;
        node.Seen = seen;
    }

    void NoteDiscovered(Guid nodeId, string tunnel, BridgeMessage message, DateTime seen)
    {
        if (message.Packet?.NodeKey is not { Length: 32 } key)
            return;
        var node = EnsureNode(nodeId, tunnel, Convert.ToHexString(key));
        if (!node.Mqtt && node.Name.Length == 0 && message.Packet?.NodeType is { } type)
            node.Repeater = type is 2 or 3;
        node.Seen = seen;
    }

    void NotePath(Guid nodeId, string tunnel, BridgeMessage message, DateTime seen)
    {
        if (message.Packet is not { } packet || packet.PayloadType == 11 || !packet.RouteParsed)
            return;
        if (packet.PayloadType == 9)
        {
            for (var i = 0; i + 1 < packet.Path.Length; i++)
                KeepHop(nodeId, tunnel, Convert.ToHexString(packet.Path[i]), Convert.ToHexString(packet.Path[i + 1]), seen);
            return;
        }
        if (BridgeFrames.HeardDirectly(message.PublicKey, packet))
            KeepHop(nodeId, tunnel, message.PublicKey, packet.Advert!.PublicKey, seen);

        var chain = packet.SrcHash is { } origin
            ? BridgeFrames.RouteChain(message.PublicKey, packet.Route, packet.Path, origin)
            : BridgeFrames.RouteChain(message.PublicKey, packet.Route, packet.Path);
        for (var i = 0; i + 1 < chain.Count; i++)
            KeepHop(nodeId, tunnel, chain[i], chain[i + 1], seen);
    }

    MapNode EnsureNode(Guid nodeId, string tunnel, string publicKey)
    {
        var key = (nodeId, Clip(tunnel, 80), Clip(publicKey.ToLowerInvariant(), 64));
        if (!_mapNodes.TryGetValue(key, out var node))
        {
            if (_mapNodes.Count >= MapNodeCap)
                DropOldestNode();
            node = new MapNode();
            _mapNodes[key] = node;
        }

        _dirtyNodes.Add(key);
        return node;
    }

    void KeepHop(Guid nodeId, string tunnel, string from, string to, DateTime seen)
    {
        if (from.Length == 0 || to.Length == 0 || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return;
        var key = (NodeId: nodeId, Tunnel: Clip(tunnel, 80), From: Clip(from.ToLowerInvariant(), 64), To: Clip(to.ToLowerInvariant(), 64));
        if (_mapLinks.TryGetValue(key, out var previous) && previous >= seen)
            return;
        if (!_mapLinks.ContainsKey(key) && _mapLinks.Count >= MapLinkCap)
            DropOldestLink();
        _mapLinks[key] = seen;
        _dirtyLinks.Add(key);
    }

    void DropOldestNode()
    {
        (Guid NodeId, string Tunnel, string PublicKey)? oldest = null;
        var seen = DateTime.MaxValue;
        foreach (var pair in _mapNodes)
        {
            if (pair.Value.Seen >= seen)
                continue;
            seen = pair.Value.Seen;
            oldest = pair.Key;
        }

        if (oldest is not { } key)
            return;
        _mapNodes.Remove(key);
        _dirtyNodes.Remove(key);
    }

    void DropOldestLink()
    {
        (Guid NodeId, string Tunnel, string From, string To)? oldest = null;
        var seen = DateTime.MaxValue;
        foreach (var pair in _mapLinks)
        {
            if (pair.Value >= seen)
                continue;
            seen = pair.Value;
            oldest = pair.Key;
        }

        if (oldest is not { } key)
            return;
        _mapLinks.Remove(key);
        _dirtyLinks.Remove(key);
    }

    void DropExpired(DateTime cutoff)
    {
        foreach (var key in _mapNodes.Where(pair => pair.Value.Seen < cutoff).Select(pair => pair.Key).ToList())
        {
            _mapNodes.Remove(key);
            _dirtyNodes.Remove(key);
        }

        foreach (var key in _mapLinks.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToList())
        {
            _mapLinks.Remove(key);
            _dirtyLinks.Remove(key);
        }
    }

    static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    static bool Placed(MapPlace place) => place.Latitude is not null && place.Longitude is not null;

    static bool Placed(IReadOnlyDictionary<string, MapPlace> known, string key) =>
        known.TryGetValue(key, out var place) && Placed(place);

    static MapPlace PlaceFrom(RepeaterState state)
    {
        var name = state.AdvertName is { Length: > 0 } advert ? advert : state.Name;
        var fromAdvert = state.AdvertLatitude is not null && state.AdvertLongitude is not null;
        return new MapPlace(
            state.PublicKey,
            name,
            fromAdvert ? state.AdvertLatitude : state.HelloLatitude,
            fromAdvert ? state.AdvertLongitude : state.HelloLongitude,
            true,
            true,
            state.LastSeen);
    }

    static string? Resolve(string token, IEnumerable<string> keys)
    {
        string? match = null;
        foreach (var key in keys)
        {
            if (!key.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null)
                return null;
            match = key;
        }

        return match;
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

public sealed record TunnelMap(IReadOnlyList<MapPlace> Nodes, IReadOnlyList<MapHop> Links, IReadOnlyList<MapName> Unplaced);

public sealed record MapPlace(string PublicKey, string Name, double? Latitude, double? Longitude, bool Repeater, bool Mqtt, DateTime Seen);

public sealed record MapName(string Name, DateTime Seen);

public sealed record MapHop(string From, string To, DateTime Seen);

sealed class MapNode
{
    public string Name = "";
    public double? Latitude;
    public double? Longitude;
    public bool Repeater;
    public bool Mqtt;
    public DateTime Seen;
}

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

    public void ApplyTelemetry(short noise, uint txAir, uint rxAir, uint uptime, uint queue, ushort batteryMv, short tempCx10, string firmware, bool environment)
    {
        NoiseFloor = noise;
        TxAirSecs = txAir;
        RxAirSecs = rxAir;
        UptimeSecs = uptime;
        TxQueue = queue;
        if (environment)
        {
            BatteryMv = batteryMv;
            TempCx10 = tempCx10;
        }
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
