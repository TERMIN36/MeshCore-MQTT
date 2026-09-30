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
        Assert.Contains("869.618 МГц", BridgeFrames.Describe(message));
        Assert.Contains("55.7558, 37.6176", BridgeFrames.Describe(message));
    }

    [Fact]
    public void Heartbeat_is_five_counters()
    {
        var body = new List<byte>();
        PutU32(body, 8);
        PutU32(body, 2);
        PutU32(body, 5);
        PutU32(body, 1);
        PutU32(body, 0);
        var raw = Envelope(3, "R", 2, null, body);

        Assert.True(BridgeFrames.TryDecode(raw, out var message));
        Assert.Equal(5u, message.Heartbeat!.PacketsInbound);
        Assert.Equal("пульс, принято 5, отдано 2", BridgeFrames.Describe(message));
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
}
