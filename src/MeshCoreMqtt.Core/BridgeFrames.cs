using System.Globalization;
using System.Text;

namespace MeshCoreMqtt.Core;

public static class BridgeFrames
{
    public const int MaxBytes = 2048;
    public const short TemperatureMissing = -32768;

    public static bool TryDecode(ReadOnlySpan<byte> data, out BridgeMessage message)
    {
        message = null!;
        if (data.Length < 1 || data[0] != 1)
            return TryBarePacket(data, out message);
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
            return TryBarePacket(data, out message);
        var name = Encoding.UTF8.GetString(data.Slice(i, nameLen));
        i += nameLen;
        var seq = ReadU32(data.Slice(i, 4));
        i += 4;
        var timeFlag = data[i++];
        if (timeFlag > 1)
            return TryBarePacket(data, out message);
        uint unix = 0;
        if (timeFlag == 1)
        {
            if (i + 4 + 2 > data.Length)
                return TryBarePacket(data, out message);
            unix = ReadU32(data.Slice(i, 4));
            i += 4;
        }

        if (i + 2 > data.Length)
            return TryBarePacket(data, out message);
        var bodyLen = ReadU16(data.Slice(i, 2));
        i += 2;
        if (i + bodyLen != data.Length)
            return TryBarePacket(data, out message);
        var body = data.Slice(i, bodyLen);

        HelloBody? hello = null;
        HeartbeatBody? heartbeat = null;
        MeshPacket? packet = null;
        if (type == 2)
            TryHello(body, out hello);
        else if (type == 3)
            TryHeartbeat(body, out heartbeat);
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
            return DescribeHeartbeat(beat);
        if (message.Packet is { } packet)
            return DescribePacket(packet);
        return message.Type switch
        {
            1 => "пакет не разобран",
            2 => "приветствие",
            3 => "пульс",
            _ => "неизвестный тип " + message.Type
        };
    }

    public static string Kind(BridgeMessage message)
    {
        if (message.Hello is not null || message.Type == 2)
            return "hello";
        if (message.Heartbeat is not null || message.Type == 3)
            return "pulse";
        if (message.Packet is { } packet)
            return PacketKind(packet);
        return message.Type == 1 ? "packet" : "unknown";
    }

    static string PacketKind(MeshPacket packet)
    {
        if (packet.Advert is not null || packet.PayloadType == 4)
            return "advert";
        return packet.PayloadType switch
        {
            0 => "request",
            1 => "response",
            2 => "text",
            3 => "ack",
            5 => "group-text",
            6 => "group-data",
            7 => "anon",
            8 => "path",
            9 => "trace",
            10 => "multipart",
            11 => ControlKind(packet),
            15 => "custom",
            _ => "packet"
        };
    }

    static string ControlKind(MeshPacket packet)
    {
        if (packet.Control is not byte code)
            return "control";
        return (code & 0xF0) switch
        {
            0x80 => "search",
            0x90 => "search-reply",
            _ => "control"
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
        AppendTelemetry(parts, hello.NoiseFloor, hello.TxAirSecs, hello.RxAirSecs, hello.UptimeSecs, hello.TxQueue, hello.Environment, hello.BatteryMv, hello.TempCx10, hello.Firmware);
        return string.Join(", ", parts);
    }

    static string DescribeHeartbeat(HeartbeatBody beat)
    {
        var parts = new List<string> { "пульс", $"принято {beat.PacketsInbound}, отдано {beat.PacketsPublished}" };
        AppendTelemetry(parts, beat.NoiseFloor, beat.TxAirSecs, beat.RxAirSecs, beat.UptimeSecs, beat.TxQueue, beat.Environment, beat.BatteryMv, beat.TempCx10, beat.Firmware);
        if (beat.Latitude is { } lat && beat.Longitude is { } lon)
            parts.Add(Place(lat, lon));
        return string.Join(", ", parts);
    }

    static void AppendTelemetry(List<string> parts, short noise, uint txAir, uint rxAir, uint uptime, uint queue, bool environment, ushort batteryMv, short tempCx10, string firmware)
    {
        parts.Add(noise == 0 ? "шум не измерен" : $"шум {noise} дБм");
        parts.Add($"эфир TX {txAir} с / RX {rxAir} с");
        parts.Add($"аптайм {uptime} с");
        parts.Add($"очередь {queue}");
        if (environment)
        {
            parts.Add(batteryMv == 0 ? "батарея не измерена" : $"батарея {batteryMv} мВ");
            parts.Add(tempCx10 == TemperatureMissing
                ? "датчик не ответил"
                : $"температура {(tempCx10 / 10d).ToString("0.0", CultureInfo.InvariantCulture)} °C");
        }
        if (firmware.Length > 0)
            parts.Add("прошивка " + firmware);
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

        var text = packet.PayloadType switch
        {
            0 => Encrypted("запрос", packet),
            1 => Encrypted("ответ", packet),
            2 => Encrypted("текст", packet),
            3 => "подтверждение",
            4 => "объявление",
            5 => Encrypted("текст группы", packet),
            6 => Encrypted("данные группы", packet),
            7 => Anon(packet),
            8 => packet.Path.Length == 0 ? "путь" : $"путь, отметок {packet.Path.Length}",
            9 => packet.Path.Length == 0 ? "трассировка" : $"трассировка, отметок {packet.Path.Length}",
            10 => Multipart(packet),
            11 => Control(packet),
            15 => "свой формат",
            _ => "пакет типа " + packet.PayloadType
        };
        if (!packet.RouteParsed)
            return text + ", маршрут не разобран";
        if (packet.PayloadType is 8 or 3 or 4 or 9 or 11)
            return WithEnds(text, packet);
        return text;
    }

    static string Encrypted(string kind, MeshPacket packet)
    {
        if (!packet.RouteParsed)
            return kind;
        var who = packet.SrcHash is not null && packet.DestHash is not null
            ? $" от {Hex(packet.SrcHash)} для {Hex(packet.DestHash)}"
            : "";
        var hops = packet.Path.Length > 0 ? $", хопов {packet.Path.Length}" : "";
        return $"{kind}{who}{hops}, содержимое зашифровано";
    }

    static string Anon(MeshPacket packet)
    {
        if (packet.NodeKey is not { Length: 32 } key)
            return "анонимный запрос, содержимое зашифровано";
        var hops = packet.RouteParsed && packet.Path.Length > 0 ? $", хопов {packet.Path.Length}" : "";
        return $"анонимный запрос от {Hex(key.AsSpan(0, 4))}{hops}, содержимое зашифровано";
    }

    static string Multipart(MeshPacket packet)
    {
        if (packet.InnerType is not int inner)
            return "составной пакет";
        var kind = inner switch
        {
            3 => "подтверждение",
            _ => "тип " + inner
        };
        return "составной пакет, " + kind;
    }

    static string Control(MeshPacket packet)
    {
        if (packet.Control is not byte code)
            return "управление";
        var high = code & 0xF0;
        if (high == 0x80)
            return "запрос поиска";
        if (high != 0x90)
            return "управление";
        var kind = AdvertKind(code & 0x0F);
        return packet.NodeKey is not null
            ? $"ответ поиска, {kind}"
            : $"ответ поиска, {kind}, ключ укорочен";
    }

    static string WithEnds(string text, MeshPacket packet)
    {
        if (packet.SrcHash is null || packet.DestHash is null)
            return text;
        return $"{text} от {Hex(packet.SrcHash)} для {Hex(packet.DestHash)}";
    }

    static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();

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

    public static IReadOnlyList<string> RouteChain(string publisherKey, byte route, byte[][] path, ReadOnlySpan<byte> origin = default)
    {
        var marks = new List<string>(path.Length + 2);
        var flood = (route & 0x03) is 0 or 1;
        if (flood && origin.Length > 0)
            marks.Add(Convert.ToHexString(origin).ToLowerInvariant());
        if (!flood && publisherKey.Length > 0)
            marks.Add(publisherKey.ToLowerInvariant());
        foreach (var hash in path)
            marks.Add(Convert.ToHexString(hash).ToLowerInvariant());
        return marks;
    }

    public static bool HeardDirectly(string publisherKey, MeshPacket packet)
    {
        if (packet.Shared || packet.Advert is null || !packet.RouteParsed)
            return false;
        if ((packet.Route & 0x03) is not (0 or 1))
            return false;
        if (string.Equals(packet.Advert.PublicKey, publisherKey, StringComparison.OrdinalIgnoreCase))
            return false;
        if (packet.Path.Length == 0)
            return true;
        if (packet.Path.Length != 1)
            return false;
        var mark = Convert.ToHexString(packet.Path[0]);
        return publisherKey.StartsWith(mark, StringComparison.OrdinalIgnoreCase);
    }

    static bool TryBarePacket(ReadOnlySpan<byte> data, out BridgeMessage message)
    {
        message = null!;
        if (data.Length is < 2 or > 255 || (data[0] & 0xC0) != 0)
            return false;
        var route = (byte)(data[0] & 0x03);
        if (!TryFrame(data, route, out _, out var payload, out _) || payload.Length == 0)
            return false;
        if (!TryPacket(data, out var packet) || packet is not { RouteParsed: true })
            return false;
        message = new BridgeMessage(1, "", "", 0, false, 0, null, null, packet);
        return true;
    }

    static bool TryRadioPrefix(
        ReadOnlySpan<byte> body,
        out int i,
        out uint freq,
        out uint bw,
        out byte sf,
        out byte cr,
        out int tx,
        out int? ant,
        out double? lat,
        out double? lon)
    {
        i = 0;
        freq = 0;
        bw = 0;
        sf = 0;
        cr = 0;
        tx = 0;
        ant = null;
        lat = null;
        lon = null;
        if (body.Length < 12)
            return false;
        freq = ReadU32(body.Slice(i, 4)); i += 4;
        bw = ReadU32(body.Slice(i, 4)); i += 4;
        sf = body[i++];
        cr = body[i++];
        tx = (sbyte)body[i++];
        var flags = body[i++];
        if ((flags & 0x01) != 0)
        {
            if (i + 2 > body.Length)
                return false;
            ant = ReadI16(body.Slice(i, 2));
            i += 2;
        }

        if ((flags & 0x02) == 0)
            return true;
        if (!TryDegrees(body, ref i, out var latValue, out var lonValue))
            return false;
        lat = latValue;
        lon = lonValue;
        return true;
    }

    static bool TryHello(ReadOnlySpan<byte> body, out HelloBody hello)
    {
        hello = null!;
        if (!TryRadioPrefix(body, out var i, out var freq, out var bw, out var sf, out var cr, out var tx, out var ant, out var lat, out var lon))
            return false;
        if (i + 21 > body.Length)
            return false;
        var forwarding = body[i++] != 0;
        var session = ReadU32(body.Slice(i, 4)); i += 4;
        var published = ReadU32(body.Slice(i, 4)); i += 4;
        var inbound = ReadU32(body.Slice(i, 4)); i += 4;
        var dups = ReadU32(body.Slice(i, 4)); i += 4;
        var errors = ReadU32(body.Slice(i, 4)); i += 4;
        var mark = i;
        var environment = true;
        if (!TryTail(body, mark, true, true, false, out var noise, out var txAir, out var rxAir, out var uptime, out var queue, out var batteryMv, out var temp, out var firmware, out _, out _)
            && !TryTail(body, mark, true, false, false, out noise, out txAir, out rxAir, out uptime, out queue, out batteryMv, out temp, out firmware, out _, out _))
        {
            environment = false;
            if (!TryTail(body, mark, false, true, false, out noise, out txAir, out rxAir, out uptime, out queue, out batteryMv, out temp, out firmware, out _, out _))
                return false;
        }
        hello = new HelloBody(
            freq, bw, sf, cr, tx, ant, lat, lon, forwarding, session, published, inbound, dups, errors,
            noise, txAir, rxAir, uptime, queue, batteryMv, temp, firmware, environment);
        return true;
    }

    static bool TryHeartbeat(ReadOnlySpan<byte> body, out HeartbeatBody beat)
    {
        beat = null!;
        if (body.Length < 20)
            return false;
        var i = 0;
        var session = ReadU32(body.Slice(i, 4)); i += 4;
        var published = ReadU32(body.Slice(i, 4)); i += 4;
        var inbound = ReadU32(body.Slice(i, 4)); i += 4;
        var dups = ReadU32(body.Slice(i, 4)); i += 4;
        var errors = ReadU32(body.Slice(i, 4)); i += 4;
        var mark = i;
        var environment = true;
        if (!TryTail(body, mark, true, true, true, out var noise, out var txAir, out var rxAir, out var uptime, out var queue, out var batteryMv, out var temp, out var firmware, out var lat, out var lon)
            && !TryTail(body, mark, true, false, true, out noise, out txAir, out rxAir, out uptime, out queue, out batteryMv, out temp, out firmware, out lat, out lon))
        {
            environment = false;
            if (!TryTail(body, mark, false, true, true, out noise, out txAir, out rxAir, out uptime, out queue, out batteryMv, out temp, out firmware, out lat, out lon))
                return false;
        }
        beat = new HeartbeatBody(session, published, inbound, dups, errors, noise, txAir, rxAir, uptime, queue, batteryMv, temp, firmware, lat, lon, environment);
        return true;
    }

    static bool TryTail(
        ReadOnlySpan<byte> body,
        int mark,
        bool battery,
        bool exact,
        bool coords,
        out short noise,
        out uint txAir,
        out uint rxAir,
        out uint uptime,
        out uint queue,
        out ushort batteryMv,
        out short temp,
        out string firmware,
        out double? lat,
        out double? lon)
    {
        lat = null;
        lon = null;
        var cursor = mark;
        if (!TryTelemetry(body, ref cursor, battery, out noise, out txAir, out rxAir, out uptime, out queue, out batteryMv, out temp, out firmware))
            return false;
        if (coords)
        {
            if (cursor >= body.Length)
                return cursor == body.Length;
            var flags = body[cursor++];
            if ((flags & 0x02) != 0)
            {
                if (!TryDegrees(body, ref cursor, out var latValue, out var lonValue))
                    return false;
                lat = latValue;
                lon = lonValue;
            }
        }

        return cursor <= body.Length && (!exact || cursor == body.Length);
    }

    static bool TryTelemetry(
        ReadOnlySpan<byte> body,
        ref int i,
        bool battery,
        out short noise,
        out uint txAir,
        out uint rxAir,
        out uint uptime,
        out uint queue,
        out ushort batteryMv,
        out short tempCx10,
        out string firmware)
    {
        noise = 0;
        txAir = 0;
        rxAir = 0;
        uptime = 0;
        queue = 0;
        batteryMv = 0;
        tempCx10 = TemperatureMissing;
        firmware = "";
        if (i + (battery ? 23 : 19) > body.Length)
            return false;
        noise = ReadI16(body.Slice(i, 2)); i += 2;
        txAir = ReadU32(body.Slice(i, 4)); i += 4;
        rxAir = ReadU32(body.Slice(i, 4)); i += 4;
        uptime = ReadU32(body.Slice(i, 4)); i += 4;
        queue = ReadU32(body.Slice(i, 4)); i += 4;
        if (battery)
        {
            batteryMv = ReadU16(body.Slice(i, 2)); i += 2;
            tempCx10 = ReadI16(body.Slice(i, 2)); i += 2;
        }
        var length = body[i++];
        if (length > 31 || i + length > body.Length)
            return false;
        firmware = Encoding.ASCII.GetString(body.Slice(i, length));
        i += length;
        return true;
    }

    static bool TryDegrees(ReadOnlySpan<byte> body, ref int i, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (i + 8 > body.Length)
            return false;
        lat = ReadI32(body.Slice(i, 4)) / 10_000_000d;
        lon = ReadI32(body.Slice(i + 4, 4)) / 10_000_000d;
        i += 8;
        return true;
    }

    static bool TryPacket(ReadOnlySpan<byte> raw, out MeshPacket? packet)
    {
        packet = null;
        if (raw.Length < 1)
            return false;
        var header = raw[0];
        var route = (byte)(header & 0x03);
        var type = (byte)((header >> 2) & 0x0F);
        if (!TryFrame(raw, route, out var path, out var payload, out var shared))
        {
            packet = new MeshPacket(type, route, [], null, RouteParsed: false);
            return true;
        }

        AdvertBody? advert = null;
        byte[]? nodeKey = null;
        int? nodeType = null;
        byte? control = null;
        int? inner = null;
        if (type == 9)
            path = TryTrace(payload, out var trace) ? trace : [];
        else if (type == 4)
            TryAdvert(payload, out advert);
        else if (type == 11)
        {
            if (payload.Length > 0)
                control = payload[0];
            TryDiscover(payload, out nodeKey, out nodeType);
        }
        else if (type == 10 && payload.Length > 0)
            inner = payload[0] & 0x0F;

        byte[]? dest = null;
        byte[]? src = null;
        if (type is 0 or 1 or 2 or 8 && payload.Length >= 2)
        {
            dest = payload[..1].ToArray();
            src = payload.Slice(1, 1).ToArray();
        }
        else if (type == 7 && payload.Length >= 33)
        {
            dest = payload[..1].ToArray();
            nodeKey = payload.Slice(1, 32).ToArray();
        }

        packet = new MeshPacket(type, route, path, advert, nodeKey, nodeType, dest, src, true, inner, control, shared);
        return true;
    }

    static bool TryFrame(ReadOnlySpan<byte> raw, byte route, out byte[][] path, out ReadOnlySpan<byte> payload, out bool shared)
    {
        path = [];
        payload = default;
        shared = false;
        var i = 1;
        if (route is 0 or 3)
        {
            if (raw.Length < i + 4)
                return false;
            var scope = ReadU16(raw.Slice(i, 2));
            var zone = ReadU16(raw.Slice(i + 2, 2));
            shared = scope == 0 && zone == 0;
            i += 4;
        }

        if (i >= raw.Length)
            return false;
        var pathLen = raw[i++];
        var hashSize = (pathLen >> 6) + 1;
        var count = pathLen & 63;
        var pathBytes = count * hashSize;
        if (hashSize == 4 || pathBytes > 64 || i + pathBytes > raw.Length)
            return false;
        path = new byte[count][];
        for (var n = 0; n < count; n++)
        {
            path[n] = raw.Slice(i, hashSize).ToArray();
            i += hashSize;
        }

        payload = raw[i..];
        return true;
    }

    static bool TryTrace(ReadOnlySpan<byte> payload, out byte[][] path)
    {
        path = [];
        if (payload.Length < 10)
            return false;
        var size = 1 << (payload[8] & 0x03);
        var body = payload[9..];
        if (body.Length == 0 || body.Length % size != 0)
            return false;
        var count = body.Length / size;
        if (count > 64)
            return false;
        path = new byte[count][];
        for (var n = 0; n < count; n++)
            path[n] = body.Slice(n * size, size).ToArray();
        return true;
    }

    static bool TryDiscover(ReadOnlySpan<byte> payload, out byte[]? key, out int? type)
    {
        key = null;
        type = null;
        if (payload.Length < 6 + 32 || (payload[0] & 0xF0) != 0x90)
            return false;
        type = payload[0] & 0x0F;
        key = payload.Slice(6, 32).ToArray();
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

        int? antenna = null;
        double? height = null;
        if ((flags & 0x20) != 0)
        {
            if (app.Length < i + 2)
                return false;
            var feat1 = ReadU16(app.Slice(i, 2));
            i += 2;
            var kind = (feat1 >> 12) & 0x7;
            if (kind != 0)
                antenna = kind;
            if ((feat1 & 0x8000) != 0)
                height = (feat1 & 0x0FFF) / 10d;
        }

        int? azimuth = null;
        if ((flags & 0x40) != 0)
        {
            if (app.Length < i + 2)
                return false;
            var feat2 = ReadU16(app.Slice(i, 2));
            i += 2;
            var degrees = feat2 & 0x1FF;
            if ((feat2 & 0x8000) != 0 && degrees <= 359)
                azimuth = degrees;
        }

        var name = (flags & 0x80) != 0 && i <= app.Length
            ? Encoding.UTF8.GetString(app[i..]).TrimEnd('\0')
            : "";
        advert = new AdvertBody(key, timestamp, flags & 0x0F, name, lat, lon, antenna, height, azimuth);
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
    uint PublishErrors,
    short NoiseFloor,
    uint TxAirSecs,
    uint RxAirSecs,
    uint UptimeSecs,
    uint TxQueue,
    ushort BatteryMv,
    short TempCx10,
    string Firmware,
    bool Environment);

public sealed record HeartbeatBody(
    uint SessionId,
    uint PacketsPublished,
    uint PacketsInbound,
    uint Duplicates,
    uint PublishErrors,
    short NoiseFloor,
    uint TxAirSecs,
    uint RxAirSecs,
    uint UptimeSecs,
    uint TxQueue,
    ushort BatteryMv,
    short TempCx10,
    string Firmware,
    double? Latitude,
    double? Longitude,
    bool Environment);

public sealed record MeshPacket(
    byte PayloadType,
    byte Route,
    byte[][] Path,
    AdvertBody? Advert,
    byte[]? NodeKey = null,
    int? NodeType = null,
    byte[]? DestHash = null,
    byte[]? SrcHash = null,
    bool RouteParsed = true,
    int? InnerType = null,
    byte? Control = null,
    bool Shared = false);

public sealed record AdvertBody(
    string PublicKey,
    uint Timestamp,
    int Type,
    string Name,
    double? Latitude,
    double? Longitude,
    int? AntennaType = null,
    double? HeightMeters = null,
    int? AzimuthDegrees = null);
