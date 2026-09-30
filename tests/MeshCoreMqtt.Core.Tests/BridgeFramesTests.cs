using MeshCoreMqtt.Core;

namespace MeshCoreMqtt.Core.Tests;

public class BridgeFramesTests
{
    [Fact]
    public void Hello_carries_radio_coordinates_and_counters()
    {
        var body = new List<byte>();
        PutU32(body, 869618000);
        PutU32(body, 62500);
        body.Add(8);
        body.Add(5);
        body.Add(22);
        body.Add(0x03);
        PutI16(body, 1250);
        PutI32(body, 557558000);
        PutI32(body, 376176000);
        body.Add(1);
        PutU32(body, 3);
        PutU32(body, 4);
        PutU32(body, 9);
        PutU32(body, 1);
        PutU32(body, 0);
        PutTelemetry(body, -110, 30, 90, 3600, 2, 3700, 365, "1.9.0");
        var raw = Envelope(2, "North", 7, 1700000000, body);

        Assert.True(BridgeFrames.TryDecode(raw, out var message));
        Assert.Equal("North", message.Name);
        Assert.Equal(7u, message.Sequence);
        Assert.True(message.HasTime);
        Assert.Equal(1700000000u, message.UnixTime);
        var hello = Assert.IsType<HelloBody>(message.Hello);
        Assert.Equal(869618000u, hello.FrequencyHz);
        Assert.Equal(62500u, hello.BandwidthHz);
        Assert.Equal(1250, hello.AntennaCm);
        Assert.Equal(55.7558, hello.Latitude!.Value, 6);
        Assert.Equal(37.6176, hello.Longitude!.Value, 6);
        Assert.True(hello.Forwarding);
        Assert.Equal(4u, hello.PacketsPublished);
        Assert.Equal((short)-110, hello.NoiseFloor);
        Assert.Equal(30u, hello.TxAirSecs);
        Assert.Equal(90u, hello.RxAirSecs);
        Assert.Equal(3600u, hello.UptimeSecs);
        Assert.Equal(2u, hello.TxQueue);
        Assert.Equal((ushort)3700, hello.BatteryMv);
        Assert.Equal((short)365, hello.TempCx10);
        Assert.Equal("1.9.0", hello.Firmware);
        var text = BridgeFrames.Describe(message);
        Assert.Contains("869.618 МГц", text);
        Assert.Contains("55.7558, 37.6176", text);
        Assert.Contains("шум -110 дБм", text);
        Assert.Contains("батарея 3700 мВ", text);
        Assert.Contains("температура 36.5 °C", text);
        Assert.Contains("прошивка 1.9.0", text);
    }

    [Fact]
    public void Hello_without_optional_fields_still_reads_telemetry()
    {
        var body = new List<byte>();
        PutU32(body, 868000000);
        PutU32(body, 125000);
        body.Add(7);
        body.Add(5);
        body.Add(0xFD);
        body.Add(0);
        body.Add(0);
        PutU32(body, 1);
        PutU32(body, 0);
        PutU32(body, 0);
        PutU32(body, 0);
        PutU32(body, 0);
        PutTelemetry(body, 0, 0, 0, 12, 0, 0, BridgeFrames.TemperatureMissing, "");
        var raw = Envelope(2, "N", 1, null, body);

        Assert.True(BridgeFrames.TryDecode(raw, out var message));
        var hello = message.Hello!;
        Assert.Null(hello.AntennaCm);
        Assert.Null(hello.Latitude);
        Assert.Equal(-3, hello.TxDbm);
        Assert.Equal((short)0, hello.NoiseFloor);
        Assert.Equal(12u, hello.UptimeSecs);
        Assert.Equal((ushort)0, hello.BatteryMv);
        Assert.Equal(BridgeFrames.TemperatureMissing, hello.TempCx10);
        Assert.Equal("", hello.Firmware);
        var text = BridgeFrames.Describe(message);
        Assert.Contains("шум не измерен", text);
        Assert.Contains("батарея не измерена", text);
        Assert.Contains("датчик не ответил", text);
    }

    [Fact]
    public void Hello_rejects_short_or_trailing_body()
    {
        var old = new List<byte>();
        PutU32(old, 868000000);
        PutU32(old, 125000);
        old.Add(7);
        old.Add(5);
        old.Add(10);
        old.Add(0);
        old.Add(1);
        PutU32(old, 1);
        PutU32(old, 0);
        PutU32(old, 0);
        PutU32(old, 0);
        PutU32(old, 0);
        Assert.False(BridgeFrames.TryDecode(Envelope(2, "N", 1, null, old), out _));

        var extra = new List<byte>(old);
        PutTelemetry(extra, -90, 1, 2, 3, 4, 0, 0, "a");
        extra.Add(0xFF);
        Assert.False(BridgeFrames.TryDecode(Envelope(2, "N", 1, null, extra), out _));
    }

    [Fact]
    public void Heartbeat_carries_telemetry_and_optional_coordinates()
    {
        var body = new List<byte>();
        PutU32(body, 8);
        PutU32(body, 2);
        PutU32(body, 5);
        PutU32(body, 1);
        PutU32(body, 0);
        PutTelemetry(body, -105, 10, 20, 60, 0, 3650, -15, "1.9.0");
        body.Add(0);
        var raw = Envelope(3, "R", 2, null, body);

        Assert.True(BridgeFrames.TryDecode(raw, out var message));
        var beat = message.Heartbeat!;
        Assert.Equal(5u, beat.PacketsInbound);
        Assert.Equal((short)-105, beat.NoiseFloor);
        Assert.Equal(10u, beat.TxAirSecs);
        Assert.Equal(20u, beat.RxAirSecs);
        Assert.Equal(60u, beat.UptimeSecs);
        Assert.Equal(0u, beat.TxQueue);
        Assert.Equal((ushort)3650, beat.BatteryMv);
        Assert.Equal((short)-15, beat.TempCx10);
        Assert.Equal("1.9.0", beat.Firmware);
        Assert.Null(beat.Latitude);
        Assert.Equal("пульс, принято 5, отдано 2, шум -105 дБм, эфир TX 10 с / RX 20 с, аптайм 60 с, очередь 0, батарея 3650 мВ, температура -1.5 °C, прошивка 1.9.0", BridgeFrames.Describe(message));

        var placedBody = new List<byte>(body);
        placedBody[^1] = 0x02;
        PutI32(placedBody, 557558000);
        PutI32(placedBody, 376176000);
        Assert.True(BridgeFrames.TryDecode(Envelope(3, "R", 3, null, placedBody), out var placed));
        Assert.Equal(55.7558, placed.Heartbeat!.Latitude!.Value, 6);
        Assert.Equal(37.6176, placed.Heartbeat.Longitude!.Value, 6);
        Assert.Contains("55.7558, 37.6176", BridgeFrames.Describe(placed));
    }

    [Fact]
    public void Heartbeat_rejects_old_short_and_unbalanced_bodies()
    {
        var old = new List<byte>();
        PutU32(old, 8);
        PutU32(old, 2);
        PutU32(old, 5);
        PutU32(old, 1);
        PutU32(old, 0);
        Assert.Equal(20, old.Count);
        Assert.False(BridgeFrames.TryDecode(Envelope(3, "R", 1, null, old), out _));

        var shortOfMinimum = new List<byte>(old);
        PutTelemetry(shortOfMinimum, -100, 1, 1, 1, 1, 0, 0, "");
        Assert.Equal(43, shortOfMinimum.Count);
        Assert.False(BridgeFrames.TryDecode(Envelope(3, "R", 1, null, shortOfMinimum), out _));

        var minimum = new List<byte>(old);
        PutTelemetry(minimum, -100, 1, 1, 1, 1, 0, BridgeFrames.TemperatureMissing, "");
        minimum.Add(0);
        Assert.Equal(44, minimum.Count);
        Assert.True(BridgeFrames.TryDecode(Envelope(3, "R", 2, null, minimum), out var bare));
        Assert.Equal((ushort)0, bare.Heartbeat!.BatteryMv);
        Assert.Equal(BridgeFrames.TemperatureMissing, bare.Heartbeat.TempCx10);

        var exact = new List<byte>(old);
        PutTelemetry(exact, -100, 1, 1, 1, 1, 4100, 210, "v");
        exact.Add(0);
        Assert.True(BridgeFrames.TryDecode(Envelope(3, "R", 2, null, exact), out _));

        var trailing = new List<byte>(exact) { 0 };
        Assert.False(BridgeFrames.TryDecode(Envelope(3, "R", 3, null, trailing), out _));

        var missingCoords = new List<byte>(old);
        PutTelemetry(missingCoords, -100, 1, 1, 1, 1, 0, 0, "v");
        missingCoords.Add(0x02);
        Assert.False(BridgeFrames.TryDecode(Envelope(3, "R", 4, null, missingCoords), out _));

        var longName = new List<byte>(old);
        PutI16(longName, -90);
        PutU32(longName, 0);
        PutU32(longName, 0);
        PutU32(longName, 0);
        PutU32(longName, 0);
        PutU16(longName, 0);
        PutI16(longName, 0);
        longName.Add(32);
        Assert.False(BridgeFrames.TryDecode(Envelope(3, "R", 5, null, longName), out _));
    }

    [Fact]
    public void Advert_packet_exposes_repeater_name_and_place()
    {
        var payload = new List<byte>();
        payload.AddRange(Enumerable.Repeat((byte)0x11, 32));
        PutU32(payload, 1700000000);
        payload.AddRange(new byte[64]);
        payload.Add((byte)(2 | 0x10 | 0x80));
        PutI32(payload, 55755800);
        PutI32(payload, 37617600);
        payload.AddRange("Север"u8.ToArray());
        var packet = new List<byte> { (byte)(1 | (4 << 2)), 0 };
        packet.AddRange(payload);
        var raw = Envelope(1, "Север", 3, null, packet);

        Assert.True(BridgeFrames.TryDecode(raw, out var message));
        var advert = message.Packet!.Advert!;
        Assert.Equal(2, advert.Type);
        Assert.Equal("Север", advert.Name);
        Assert.Equal(55.7558, advert.Latitude!.Value, 6);
        Assert.Equal(37.6176, advert.Longitude!.Value, 6);
        Assert.Equal(string.Concat(Enumerable.Repeat("11", 32)), advert.PublicKey);
        Assert.Contains("объявление репитер «Север»", BridgeFrames.Describe(message));
    }

    [Fact]
    public void Path_packet_keeps_route_marks()
    {
        var direct = new List<byte> { (byte)(2 | (8 << 2)), 2, 0xAA, 0xBB, 0x01 };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 4, null, direct), out var message));
        var packet = message.Packet!;
        Assert.Equal(8, packet.PayloadType);
        Assert.Equal(2, packet.Route);
        Assert.Equal(new byte[] { 0xAA }, packet.Path[0]);
        Assert.Equal(new byte[] { 0xBB }, packet.Path[1]);
        var publisher = string.Concat(Enumerable.Repeat("ab", 32));
        var chain = BridgeFrames.RouteChain(publisher, packet.Route, packet.Path);
        Assert.Equal([publisher, "aa", "bb"], chain);
        Assert.Contains("путь, отметок 2", BridgeFrames.Describe(message));

        var flood = new List<byte> { (byte)(1 | (8 << 2)), 2, 0x11, 0x22, 0x01 };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 5, null, flood), out var flooded));
        Assert.Equal(["11", "22"], BridgeFrames.RouteChain(publisher, flooded.Packet!.Route, flooded.Packet.Path));
    }

    [Fact]
    public void Trace_uses_hashes_from_the_payload()
    {
        var body = new List<byte> { (byte)(2 | (9 << 2)), 1, 0x40 };
        body.AddRange(new byte[8]);
        body.Add(0);
        body.Add(0xAA);
        body.Add(0xBB);

        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 6, null, body), out var message));
        var packet = message.Packet!;
        Assert.Equal(9, packet.PayloadType);
        Assert.Equal(new byte[] { 0xAA }, packet.Path[0]);
        Assert.Equal(new byte[] { 0xBB }, packet.Path[1]);
        Assert.Contains("трассировка, отметок 2", BridgeFrames.Describe(message));
    }

    [Fact]
    public void Discover_response_exposes_the_node_key()
    {
        var body = new List<byte> { (byte)(2 | (11 << 2)), 0, 0x92, 0, 0, 0, 0, 0 };
        body.AddRange(Enumerable.Repeat((byte)0x44, 32));

        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 7, null, body), out var message));
        Assert.Equal(2, message.Packet!.NodeType);
        Assert.NotNull(message.Packet.NodeKey);
        Assert.Equal(Enumerable.Repeat((byte)0x44, 32), message.Packet.NodeKey);
    }

    [Fact]
    public void Text_packet_exposes_clear_ends_and_hops()
    {
        var body = new List<byte> { (byte)(1 | (2 << 2)), 1, 0xAA, 0x11, 0x22, 0x01 };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 8, null, body), out var message));
        var packet = message.Packet!;
        Assert.Equal(2, packet.PayloadType);
        Assert.True(packet.RouteParsed);
        Assert.Equal(new byte[] { 0x11 }, packet.DestHash);
        Assert.Equal(new byte[] { 0x22 }, packet.SrcHash);
        Assert.Equal("текст от 22 для 11, хопов 1, содержимое зашифровано", BridgeFrames.Describe(message));
        Assert.Equal(["22", "aa"], BridgeFrames.RouteChain("ab", packet.Route, packet.Path, packet.SrcHash));
    }

    [Fact]
    public void Wide_transport_hash_still_parses()
    {
        var body = new List<byte> { (byte)(2 << 2), 1, 2, 3, 4, 0x41, 0xAA, 0xBB, 0xCC, 0xDD };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 9, null, body), out var message));
        var packet = message.Packet!;
        Assert.Equal(0, packet.Route);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, packet.Path[0]);
        Assert.Equal(new byte[] { 0xDD }, packet.SrcHash);
        Assert.Contains("хопов 1", BridgeFrames.Describe(message));
    }

    [Fact]
    public void Broken_route_keeps_the_payload_type()
    {
        var body = new List<byte> { (byte)(1 | (2 << 2)), 10, 0xAA, 0xBB };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 10, null, body), out var message));
        Assert.NotNull(message.Packet);
        Assert.Equal(2, message.Packet!.PayloadType);
        Assert.False(message.Packet.RouteParsed);
        Assert.Empty(message.Packet.Path);
        Assert.Equal("текст, маршрут не разобран", BridgeFrames.Describe(message));
    }

    [Fact]
    public void Anon_request_exposes_the_sender_key()
    {
        var body = new List<byte> { (byte)(2 | (7 << 2)), 0, 0x11 };
        body.AddRange(Enumerable.Repeat((byte)0x55, 32));
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 11, null, body), out var message));
        Assert.NotNull(message.Packet!.NodeKey);
        Assert.Equal(Enumerable.Repeat((byte)0x55, 32), message.Packet.NodeKey);
        Assert.Contains("анонимный запрос от 55555555", BridgeFrames.Describe(message));
    }

    [Fact]
    public void Trace_hash_size_comes_from_the_payload_flags()
    {
        var body = new List<byte> { (byte)(2 | (9 << 2)), 1, 0x10 };
        body.AddRange(new byte[8]);
        body.Add(1);
        body.AddRange([0xAA, 0xBB, 0xCC, 0xDD]);

        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 13, null, body), out var message));
        var packet = message.Packet!;
        Assert.Equal(2, packet.Path.Length);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, packet.Path[0]);
        Assert.Equal(new byte[] { 0xCC, 0xDD }, packet.Path[1]);
    }

    [Fact]
    public void Zero_transport_codes_mark_a_shared_advert()
    {
        var shared = new List<byte> { (byte)(2 << 2), 0, 0, 0, 0, 0, 0x11, 0x22 };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 14, null, shared), out var message));
        Assert.True(message.Packet!.Shared);

        var scoped = new List<byte> { (byte)(2 << 2), 1, 0, 0, 0, 0, 0x11, 0x22 };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 15, null, scoped), out var other));
        Assert.False(other.Packet!.Shared);
    }

    [Fact]
    public void Direct_hear_is_an_empty_path_or_only_the_publisher()
    {
        var advert = new AdvertBody(new string('1', 64), 1, 2, "N", 1, 2);
        var publisher = new string('a', 64);
        Assert.True(BridgeFrames.HeardDirectly(publisher, new MeshPacket(4, 1, [], advert)));
        Assert.True(BridgeFrames.HeardDirectly(publisher, new MeshPacket(4, 1, [new byte[] { 0xAA }], advert)));
        Assert.False(BridgeFrames.HeardDirectly(publisher, new MeshPacket(4, 1, [new byte[] { 0xFF }], advert)));
        Assert.False(BridgeFrames.HeardDirectly(publisher, new MeshPacket(4, 1, [new byte[] { 0xAA }], advert, Shared: true)));
        Assert.False(BridgeFrames.HeardDirectly(publisher, new MeshPacket(4, 1, [new byte[] { 0xAA }, new byte[] { 0xBB }], advert)));
    }

    [Fact]
    public void Discover_request_is_named()
    {
        var body = new List<byte> { (byte)(2 | (11 << 2)), 0, 0x80, 0x02, 1, 2, 3, 4 };
        Assert.True(BridgeFrames.TryDecode(Envelope(1, "Север", 12, null, body), out var message));
        Assert.Equal("запрос поиска", BridgeFrames.Describe(message));
        Assert.Null(message.Packet!.NodeKey);
    }

    [Fact]
    public void Unknown_version_is_rejected()
    {
        Assert.False(BridgeFrames.TryDecode([2, 1], out _));
    }

    static byte[] Envelope(byte type, string name, uint seq, uint? unix, List<byte> body)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var raw = new List<byte> { 1, type };
        raw.AddRange(Enumerable.Repeat((byte)0xAB, 32));
        raw.Add((byte)nameBytes.Length);
        raw.AddRange(nameBytes);
        PutU32(raw, seq);
        raw.Add((byte)(unix.HasValue ? 1 : 0));
        if (unix is { } time)
            PutU32(raw, time);
        raw.Add((byte)body.Count);
        raw.Add((byte)(body.Count >> 8));
        raw.AddRange(body);
        return raw.ToArray();
    }

    static void PutU32(List<byte> dest, uint value)
    {
        dest.Add((byte)value);
        dest.Add((byte)(value >> 8));
        dest.Add((byte)(value >> 16));
        dest.Add((byte)(value >> 24));
    }

    static void PutI16(List<byte> dest, short value)
    {
        dest.Add((byte)value);
        dest.Add((byte)(value >> 8));
    }

    static void PutI32(List<byte> dest, int value) => PutU32(dest, (uint)value);

    static void PutU16(List<byte> dest, ushort value)
    {
        dest.Add((byte)value);
        dest.Add((byte)(value >> 8));
    }

    static void PutTelemetry(List<byte> dest, short noise, uint txAir, uint rxAir, uint uptime, uint queue, ushort batteryMv, short tempCx10, string firmware)
    {
        PutI16(dest, noise);
        PutU32(dest, txAir);
        PutU32(dest, rxAir);
        PutU32(dest, uptime);
        PutU32(dest, queue);
        PutU16(dest, batteryMv);
        PutI16(dest, tempCx10);
        var text = System.Text.Encoding.ASCII.GetBytes(firmware);
        dest.Add((byte)text.Length);
        dest.AddRange(text);
    }
}
