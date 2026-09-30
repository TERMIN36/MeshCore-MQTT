using System.Globalization;
using System.Text;

namespace MeshCoreMqtt.Core;

public static class BridgeFrames
{
    public const int MaxBytes = 360;

    public static bool TryDecode(ReadOnlySpan<byte> data, out BridgeMessage message)
    {
        message = null!;
        if (data.Length < 1 || data[0] != 1)
            return false;
        var i = 1;
        if (i >= data.Length)
            return false;
        var type = data[i++];
        if (i + 33 > data.Length)
            return false;
        var id = Convert.ToHexString(data.Slice(i, 32)).ToLowerInvariant();
        i += 32;
        var nameLen = data[i++];
        if (nameLen > 31 || i + nameLen + 4 + 1 + 2 > data.Length)
            return false;
        var name = Encoding.UTF8.GetString(data.Slice(i, nameLen));
        i += nameLen;
        var seq = ReadU32(data.Slice(i, 4));
        i += 4;
        var timeFlag = data[i++];
        if (timeFlag > 1)
            return false;
        uint unix = 0;
        if (timeFlag == 1)
        {
            if (i + 4 + 2 > data.Length)
                return false;
            unix = ReadU32(data.Slice(i, 4));
            i += 4;
        }

        if (i + 2 > data.Length)
            return false;
        var bodyLen = ReadU16(data.Slice(i, 2));
        i += 2;
        if (i + bodyLen != data.Length)
            return false;
        var body = data.Slice(i, bodyLen);

        HelloBody? hello = null;
        HeartbeatBody? heartbeat = null;
        MeshPacket? packet = null;
        if (type == 2)
        {
            if (!TryHello(body, out hello))
                return false;
        }
        else if (type == 3)
        {
            if (!TryHeartbeat(body, out heartbeat))
                return false;
        }
        else if (type == 1)
        {
            TryPacket(body, out packet);
        }

        message = new BridgeMessage(type, id, name, seq, timeFlag == 1, unix, hello, heartbeat, packet);
        return true;
    }

    public static string Describe(BridgeMessage message)
    {
        if (message.Hello is { } hello)
            return DescribeHello(hello);
        if (message.Heartbeat is { } beat)
            return $"пульс, принято {beat.PacketsInbound}, отдано {beat.PacketsPublished}";
        if (message.Packet is { } packet)
            return DescribePacket(packet);
        return message.Type switch
        {
            1 => "пакет",
            2 => "приветствие",
            3 => "пульс",
            _ => "неизвестный тип " + message.Type
        };
    }

    static string DescribeHello(HelloBody hello)
    {
        var parts = new List<string> { "приветствие", Mhz(hello.FrequencyHz), $"SF {hello.SpreadingFactor}", $"CR {hello.CodingRate}", $"{hello.TxDbm} дБм" };
        if (hello.AntennaCm is { } cm)
            parts.Add($"антенна {((double)cm / 100).ToString("0.##", CultureInfo.InvariantCulture)} м");
        if (hello.Latitude is { } lat && hello.Longitude is { } lon)
            parts.Add(Place(lat, lon));
        parts.Add(hello.Forwarding ? "пересылка включена" : "пересылка выключена");
        parts.Add($"принято {hello.PacketsInbound}, отдано {hello.PacketsPublished}");
        return string.Join(", ", parts);
    }

    static string DescribePacket(MeshPacket packet)
    {
        if (packet.Advert is { } advert)
        {
            var kind = AdvertKind(advert.Type);
            var named = string.IsNullOrEmpty(advert.Name) ? kind : $"{kind} «{advert.Name}»";
            return advert.Latitude is { } lat && advert.Longitude is { } lon
                ? $"объявление {named}, {Place(lat, lon)}"
                : $"объявление {named}";
        }

        return packet.PayloadType switch
        {
            0 => "запрос, содержимое зашифровано",
            1 => "ответ, содержимое зашифровано",
            2 => "текст, содержимое зашифровано",
            3 => "подтверждение",
            4 => "объявление",
            5 => "текст группы, содержимое зашифровано",
            6 => "данные группы, содержимое зашифровано",
            7 => "анонимный запрос, содержимое зашифровано",
            8 => "путь, содержимое зашифровано",
            9 => "трассировка",
            10 => "составной пакет",
            11 => "управление",
            15 => "свой формат",
            _ => "пакет типа " + packet.PayloadType
        };
    }

    public static string AdvertKind(int type) => type switch
    {
        1 => "чат",
        2 => "репитер",
        3 => "комната",
        4 => "датчик",
        _ => "узел"
    };

    public static string Mhz(uint hz) => (hz / 1_000_000d).ToString("0.###", CultureInfo.InvariantCulture) + " МГц";

    public static string Place(double lat, double lon) =>
        lat.ToString("0.######", CultureInfo.InvariantCulture) + ", " + lon.ToString("0.######", CultureInfo.InvariantCulture);

    static bool TryHello(ReadOnlySpan<byte> body, out HelloBody hello)
    {
        hello = null!;
        if (body.Length < 16)
            return false;
        var i = 0;
        var freq = ReadU32(body.Slice(i, 4)); i += 4;
        var bw = ReadU32(body.Slice(i, 4)); i += 4;
        var sf = body[i++];
        var cr = body[i++];
        var tx = (sbyte)body[i++];
        var flags = body[i++];
        int? ant = null;
        double? lat = null;
        double? lon = null;
        if ((flags & 0x01) != 0)
        {
            if (i + 2 > body.Length)
                return false;
            ant = ReadI16(body.Slice(i, 2));
            i += 2;
        }

        if ((flags & 0x02) != 0)
        {
            if (i + 8 > body.Length)
                return false;
            lat = ReadI32(body.Slice(i, 4)) / 10_000_000d;
            lon = ReadI32(body.Slice(i + 4, 4)) / 10_000_000d;
            i += 8;
        }

        if (i + 21 > body.Length)
            return false;
        var forwarding = body[i++] != 0;
        var session = ReadU32(body.Slice(i, 4)); i += 4;
        var published = ReadU32(body.Slice(i, 4)); i += 4;
        var inbound = ReadU32(body.Slice(i, 4)); i += 4;
        var dups = ReadU32(body.Slice(i, 4)); i += 4;
        var errors = ReadU32(body.Slice(i, 4)); i += 4;
        if (i != body.Length)
            return false;
        hello = new HelloBody(freq, bw, sf, cr, tx, ant, lat, lon, forwarding, session, published, inbound, dups, errors);
        return true;
    }

    static bool TryHeartbeat(ReadOnlySpan<byte> body, out HeartbeatBody beat)
    {
        beat = null!;
        if (body.Length != 20)
            return false;
        beat = new HeartbeatBody(
            ReadU32(body[..4]),
            ReadU32(body.Slice(4, 4)),
            ReadU32(body.Slice(8, 4)),
            ReadU32(body.Slice(12, 4)),
            ReadU32(body.Slice(16, 4)));
        return true;
    }

    static bool TryPacket(ReadOnlySpan<byte> raw, out MeshPacket? packet)
    {
        packet = null;
        if (raw.Length < 2)
            return false;
        var header = raw[0];
        var route = header & 0x03;
        var type = (byte)((header >> 2) & 0x0F);
        var i = 1;
        if (route is 0 or 3)
        {
            if (raw.Length < i + 4)
                return false;
            i += 4;
        }

        if (i >= raw.Length)
            return false;
        var pathLen = raw[i++];
        var hashSize = (pathLen >> 6) + 1;
        var pathBytes = (pathLen & 63) * hashSize;
        if (hashSize == 4 || pathBytes > 64 || i + pathBytes > raw.Length)
            return false;
        i += pathBytes;
        AdvertBody? advert = null;
        if (type == 4)
            TryAdvert(raw[i..], out advert);
        packet = new MeshPacket(type, advert);
        return true;
    }

    static bool TryAdvert(ReadOnlySpan<byte> payload, out AdvertBody? advert)
    {
        advert = null;
        if (payload.Length < 32 + 4 + 64 + 1)
            return false;
        var key = Convert.ToHexString(payload[..32]).ToLowerInvariant();
        var timestamp = ReadU32(payload.Slice(32, 4));
        var app = payload[(32 + 4 + 64)..];
        var flags = app[0];
        var i = 1;
        double? lat = null;
        double? lon = null;
        if ((flags & 0x10) != 0)
        {
            if (app.Length < i + 8)
                return false;
            lat = ReadI32(app.Slice(i, 4)) / 1_000_000d;
            lon = ReadI32(app.Slice(i + 4, 4)) / 1_000_000d;
            i += 8;
        }

        if ((flags & 0x20) != 0)
        {
            if (app.Length < i + 2)
                return false;
            i += 2;
        }

        if ((flags & 0x40) != 0)
        {
            if (app.Length < i + 2)
                return false;
            i += 2;
        }

        var name = (flags & 0x80) != 0 && i <= app.Length
            ? Encoding.UTF8.GetString(app[i..]).TrimEnd('\0')
            : "";
        advert = new AdvertBody(key, timestamp, flags & 0x0F, name, lat, lon);
        return true;
    }

    static ushort ReadU16(ReadOnlySpan<byte> data) => (ushort)(data[0] | (data[1] << 8));
    static uint ReadU32(ReadOnlySpan<byte> data) => (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));
    static short ReadI16(ReadOnlySpan<byte> data) => (short)ReadU16(data);
    static int ReadI32(ReadOnlySpan<byte> data) => (int)ReadU32(data);
}

public sealed record BridgeMessage(
    byte Type,
    string PublicKey,
    string Name,
    uint Sequence,
    bool HasTime,
    uint UnixTime,
    HelloBody? Hello,
    HeartbeatBody? Heartbeat,
    MeshPacket? Packet);

public sealed record HelloBody(
    uint FrequencyHz,
    uint BandwidthHz,
    byte SpreadingFactor,
    byte CodingRate,
    int TxDbm,
    int? AntennaCm,
    double? Latitude,
    double? Longitude,
    bool Forwarding,
    uint SessionId,
    uint PacketsPublished,
    uint PacketsInbound,
    uint Duplicates,
    uint PublishErrors);

public sealed record HeartbeatBody(
    uint SessionId,
    uint PacketsPublished,
    uint PacketsInbound,
    uint Duplicates,
    uint PublishErrors);

public sealed record MeshPacket(byte PayloadType, AdvertBody? Advert);

public sealed record AdvertBody(
    string PublicKey,
    uint Timestamp,
    int Type,
    string Name,
    double? Latitude,
    double? Longitude);
